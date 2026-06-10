using System.Collections.Concurrent;
using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfCompress.Core.Models;
using PdfCompress.Core.Services;

var inputOpt = new Option<DirectoryInfo>(
    "--input", "Directory containing input PDFs") { IsRequired = true };

var outputOpt = new Option<DirectoryInfo>(
    "--output", "Directory to write compressed PDFs") { IsRequired = true };

var passwordsOpt = new Option<FileInfo?>(
    "--passwords", "Excel file with FileName and Password columns");

var limitOpt = new Option<int?>(
    "--limit", "Maximum number of files to process (useful for testing)");

var parallelismOpt = new Option<int>(
    "--parallelism",
    () => Environment.ProcessorCount,
    "Max parallel file operations");

var formatOpt = new Option<string>(
    "--format",
    () => "text",
    "Output format: text (default) or json");
formatOpt.AddValidator(r =>
{
    var v = r.GetValueForOption(formatOpt);
    if (v is not "text" and not "json")
        r.ErrorMessage = "--format must be 'text' or 'json'";
});

var overwriteOpt = new Option<bool>(
    "--overwrite",
    "Allow output directory to be the same as input (overwrites originals)");

var passwordOnlyOpt = new Option<bool>(
    "--password-only",
    "Apply passwords from --passwords without running compression optimization");

var root = new RootCommand("Compress PDF files by pruning unused resources and deduplicating Form XObjects.")
{
    inputOpt, outputOpt, passwordsOpt, limitOpt, parallelismOpt, formatOpt, overwriteOpt, passwordOnlyOpt
};

root.SetHandler(async (input, output, passwordFile, limit, parallelism, format, overwrite, passwordOnly) =>
{
    if (!input.Exists)
    {
        Console.Error.WriteLine($"Input directory not found: {input.FullName}");
        Environment.Exit(1);
    }

    if (!overwrite &&
        string.Equals(
            Path.GetFullPath(input.FullName),
            Path.GetFullPath(output.FullName),
            StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            "Output directory is the same as input. Use --overwrite to allow this.");
        Environment.Exit(1);
    }

    if (passwordOnly && passwordFile is null)
    {
        Console.Error.WriteLine("--password-only requires --passwords <file.xlsx>.");
        Environment.Exit(1);
    }

    output.Create();

    // Load password map (empty dict if no file provided).
    Dictionary<string, string> passwords;
    try
    {
        passwords = passwordFile is not null
            ? PasswordMapReader.Read(passwordFile.FullName)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to read password file: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    var pdfs = input.EnumerateFiles("*.pdf", SearchOption.TopDirectoryOnly)
        .OrderBy(f => f.Name)
        .ToList();

    if (limit.HasValue)
        pdfs = pdfs.Take(limit.Value).ToList();

    if (pdfs.Count == 0)
    {
        Console.Error.WriteLine("No PDF files found in input directory.");
        Environment.Exit(0);
    }

    var results = new ConcurrentBag<CompressionResult>();
    var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism };

    await Parallel.ForEachAsync(pdfs, options, async (pdf, ct) =>
    {
        var key = Path.GetFileNameWithoutExtension(pdf.Name);
        passwords.TryGetValue(key, out var password);
        var outputPath = Path.Combine(output.FullName, pdf.Name);

        var result = await Task.Run(
            () => passwordOnly
                ? PdfOptimizer.ApplyPasswordOnly(pdf.FullName, outputPath, password)
                : PdfOptimizer.Optimize(pdf.FullName, outputPath, password), ct);

        results.Add(result);
    });

    var sorted = results.OrderBy(r => r.FileName).ToList();

    if (format == "json")
        PrintJson(sorted);
    else
        PrintTable(sorted);

    var failures = sorted.Count(r => !r.Success);
    if (failures > 0)
        Environment.Exit(1);

},
inputOpt, outputOpt, passwordsOpt, limitOpt, parallelismOpt, formatOpt, overwriteOpt, passwordOnlyOpt);

return await root.InvokeAsync(args);

// ── Output formatters ─────────────────────────────────────────────────────

static void PrintTable(IList<CompressionResult> results)
{
    const int nameW = 45;
    Console.WriteLine(
        $"{"FILE",-nameW} {"OLD_BYTES",13} {"NEW_BYTES",13} {"SAVED_BYTES",13} {"REDUCTION",10}");
    Console.WriteLine(new string('-', nameW + 13 + 13 + 13 + 11));

    foreach (var r in results)
    {
        if (r.Success)
            Console.WriteLine(
                $"{r.FileName,-nameW} {r.OriginalBytes,13:N0} {r.CompressedBytes,13:N0}" +
                $" {r.SavedBytes,13:N0} {r.ReductionPercent,9:F1}%");
        else
            Console.WriteLine($"{r.FileName,-nameW} ERROR: {r.Error}");
    }

    var successes = results.Where(r => r.Success).ToList();
    if (successes.Count > 0)
    {
        Console.WriteLine(new string('-', nameW + 13 + 13 + 13 + 11));
        var totalOld = successes.Sum(r => r.OriginalBytes);
        var totalNew = successes.Sum(r => r.CompressedBytes);
        var totalSaved = totalOld - totalNew;
        var totalReduction = totalOld > 0 ? (1.0 - (double)totalNew / totalOld) * 100 : 0;
        Console.WriteLine(
            $"{"TOTAL",-nameW} {totalOld,13:N0} {totalNew,13:N0}" +
            $" {totalSaved,13:N0} {totalReduction,9:F1}%");
    }

    var failures = results.Where(r => !r.Success).ToList();
    if (failures.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"FAILED ({failures.Count}):");
        foreach (var f in failures)
            Console.WriteLine($"  {f.FileName}: {f.Error}");
    }
}

static void PrintJson(IList<CompressionResult> results)
{
    var opts = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    Console.WriteLine(JsonSerializer.Serialize(results, opts));
}
