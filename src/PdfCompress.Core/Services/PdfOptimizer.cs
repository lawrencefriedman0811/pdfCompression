using System.Security.Cryptography;
using System.Text;
using iText.Kernel.Pdf;
using PdfCompress.Core.Models;

namespace PdfCompress.Core.Services;

public static class PdfOptimizer
{
    // Keys that describe the stream encoding format, not the XObject semantics.
    // Excluded from the dedup fingerprint so that same-content streams with different
    // compression settings are still treated as identical.
    private static readonly HashSet<string> StreamFormatKeys =
        new(StringComparer.Ordinal) { "/Length", "/Filter", "/DecodeParms", "/DL" };

    private static readonly byte[] EmptyFormBytes =
        Encoding.Latin1.GetBytes("q Q /Tx BMC EMC");

    public static CompressionResult Optimize(
        string inputPath, string outputPath, string? password = null)
    {
        var originalSize = new FileInfo(inputPath).Length;

        try
        {
            var readerProps = new ReaderProperties();
            if (password is not null)
                readerProps.SetPassword(Encoding.UTF8.GetBytes(password));

            using var reader = new PdfReader(inputPath, readerProps);
            using var writer = new PdfWriter(outputPath, new WriterProperties()
                .SetCompressionLevel(CompressionConstants.BEST_COMPRESSION)
                .SetFullCompressionMode(true));
            using var doc = new PdfDocument(reader, writer);

            OptimizeDocument(doc);
        }
        catch (Exception ex)
        {
            return new CompressionResult(
                Path.GetFileName(inputPath), originalSize, 0, ex.Message);
        }

        var compressedSize = new FileInfo(outputPath).Length;
        return new CompressionResult(
            Path.GetFileName(inputPath), originalSize, compressedSize);
    }

    private static void OptimizeDocument(PdfDocument doc)
    {
        // Pass 1 — build a global hash → canonical-stream map across all pages.
        // Empty Form XObjects all map to a single shared "canonical empty".
        PdfStream? canonicalEmpty = null;
        var hashToCanonical = new Dictionary<string, PdfStream>(StringComparer.Ordinal);

        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
        {
            foreach (var stream in CollectFormXObjects(doc.GetPage(i)))
            {
                byte[]? decoded = TryGetBytes(stream);
                if (decoded is null) continue;

                if (IsEmpty(decoded))
                {
                    canonicalEmpty ??= stream;
                    continue;
                }

                var hash = ComputeHash(decoded, stream);
                hashToCanonical.TryAdd(hash, stream);
            }
        }

        // Pass 2 — rewrite references on every page (snapshot entries first to
        // avoid mutating a live enumerator).
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
        {
            var xObjects = GetXObjectDict(doc.GetPage(i));
            if (xObjects is null) continue;

            // Snapshot: enumerate keys/values before any mutation.
            var entries = xObjects.EntrySet()
                .Select(e => (Key: e.Key, Stream: ResolveStream(e.Value)))
                .Where(e => e.Stream is not null)
                .ToList();

            foreach (var (name, stream) in entries)
            {
                if (stream!.GetAsName(PdfName.Subtype) != PdfName.Form)
                    continue;

                byte[]? decoded = TryGetBytes(stream);
                if (decoded is null) continue;

                if (IsEmpty(decoded))
                {
                    // Consolidate into the single canonical empty, or leave alone
                    // if this IS the canonical empty (avoid self-replacement).
                    if (canonicalEmpty is not null &&
                        !ReferencesSameObject(stream, canonicalEmpty))
                    {
                        var indRef = canonicalEmpty.GetIndirectReference();
                        if (indRef is not null)
                            xObjects.Put(name, indRef);
                    }
                    continue;
                }

                var hash = ComputeHash(decoded, stream);
                if (hashToCanonical.TryGetValue(hash, out var canonical) &&
                    !ReferencesSameObject(stream, canonical))
                {
                    var indRef = canonical.GetIndirectReference();
                    if (indRef is not null)
                        xObjects.Put(name, indRef);
                }
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<PdfStream> CollectFormXObjects(PdfPage page)
    {
        var dict = GetXObjectDict(page);
        if (dict is null) yield break;

        foreach (var entry in dict.EntrySet())
        {
            var stream = ResolveStream(entry.Value);
            if (stream is not null && stream.GetAsName(PdfName.Subtype) == PdfName.Form)
                yield return stream;
        }
    }

    private static PdfDictionary? GetXObjectDict(PdfPage page)
        => page.GetResources()?.GetResource(PdfName.XObject);

    private static PdfStream? ResolveStream(PdfObject obj)
    {
        if (obj is PdfIndirectReference indRef)
            obj = indRef.GetRefersTo();

        return obj as PdfStream;
    }

    private static bool IsEmpty(byte[] bytes)
    {
        var span = bytes.AsSpan().Trim((byte)' ').Trim((byte)'\r').Trim((byte)'\n');
        return span.SequenceEqual(EmptyFormBytes);
    }

    private static byte[]? TryGetBytes(PdfStream stream)
    {
        try { return stream.GetBytes(); }
        catch { return null; }
    }

    /// <summary>
    /// Fingerprint = SHA-256 of (semantic dictionary entries sorted by key) + decoded bytes.
    /// Excludes stream-format keys (/Length, /Filter, /DecodeParms, /DL) so that
    /// streams with the same semantic content but different compression are treated as equal.
    /// Includes /BBox, /Matrix, /Resources, /Group etc. so that visually different
    /// XObjects with accidentally identical byte streams are not collapsed.
    /// </summary>
    private static string ComputeHash(byte[] decodedBytes, PdfStream stream)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Append sorted dictionary key/value pairs (as strings).
        foreach (var entry in stream.EntrySet()
                     .Where(e => !StreamFormatKeys.Contains(e.Key.ToString()))
                     .OrderBy(e => e.Key.ToString(), StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(entry.Key.ToString()));
            sha.AppendData(Encoding.UTF8.GetBytes(entry.Value.ToString() ?? string.Empty));
        }

        sha.AppendData(decodedBytes);
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static bool ReferencesSameObject(PdfStream a, PdfStream b)
    {
        var ra = a.GetIndirectReference();
        var rb = b.GetIndirectReference();
        if (ra is null || rb is null) return ReferenceEquals(a, b);
        return ra.GetObjNumber() == rb.GetObjNumber() &&
               ra.GetGenNumber() == rb.GetGenNumber();
    }
}
