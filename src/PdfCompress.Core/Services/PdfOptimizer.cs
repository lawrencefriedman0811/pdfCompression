using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using iText.IO.Source;
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
            using var srcDoc = new PdfDocument(reader);

            // Mutate the source model in memory: prune dead /XObject references and
            // collapse duplicate Form XObjects.
            OptimizeDocument(srcDoc);

            // iText never garbage-collects orphaned indirect objects, so removing the
            // last reference to a form is not enough — the bytes stay in the file.
            // Re-emit by copying the pages into a fresh document: the copy walks the
            // object graph from each page and only carries over still-referenced
            // objects, leaving the now-unreachable forms behind. Smart mode reuses
            // structurally identical objects across pages.
            var writerProps = new WriterProperties()
                .SetCompressionLevel(CompressionConstants.BEST_COMPRESSION)
                .SetFullCompressionMode(true);
            ApplyEncryption(writerProps, password);
            using var writer = new PdfWriter(outputPath, writerProps);
            writer.SetSmartMode(true);
            using var destDoc = new PdfDocument(writer);

            srcDoc.CopyPagesTo(1, srcDoc.GetNumberOfPages(), destDoc);
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

    public static CompressionResult ApplyPasswordOnly(
        string inputPath, string outputPath, string? password = null)
    {
        var originalSize = new FileInfo(inputPath).Length;

        try
        {
            var readerProps = new ReaderProperties();
            if (password is not null)
                readerProps.SetPassword(Encoding.UTF8.GetBytes(password));

            var writerProps = new WriterProperties();
            ApplyEncryption(writerProps, password);

            using var reader = new PdfReader(inputPath, readerProps);
            using var writer = new PdfWriter(outputPath, writerProps);
            using var doc = new PdfDocument(reader, writer);
        }
        catch (Exception ex)
        {
            return new CompressionResult(
                Path.GetFileName(inputPath), originalSize, 0, ex.Message);
        }

        var rewrittenSize = new FileInfo(outputPath).Length;
        return new CompressionResult(
            Path.GetFileName(inputPath), originalSize, rewrittenSize);
    }

    private static void OptimizeDocument(PdfDocument doc)
    {
        // Pass 0 — drop per-page /XObject resource entries that the page (and the
        // forms it actually draws) never invoke with a `Do` operator. EVO-generated
        // PDFs attach thousands of Form XObjects to every page's resource dictionary
        // while drawing only a handful; removing the dead name→ref entries makes the
        // orphaned forms unreachable so the copy in Optimize() leaves them behind.
        // Lossless.
        PruneUnusedXObjectReferences(doc);

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

        // Pass 3 — remove unused /Font resource entries after Form XObject pruning
        // and canonicalization. The final copy then leaves orphaned font dictionaries,
        // descriptors, and embedded font-file streams behind.
        PruneUnusedFontReferences(doc);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes name→XObject entries from page (and nested form) /XObject resource
    /// dictionaries that are never invoked with a `Do` operator in any content stream
    /// that can resolve names through them. Uses an accumulate-then-prune strategy
    /// keyed by dictionary identity so that shared/inherited resource dictionaries are
    /// pruned once, using the union of names invoked across every scope that references
    /// them. Page content, annotation appearance streams, and the Form XObjects those
    /// scopes draw are all analyzed (including forms that omit /Resources and therefore
    /// inherit the caller's resources). Any scope whose usage cannot be determined
    /// safely marks its /XObject dictionary "unsafe", leaving it completely untouched.
    /// </summary>
    private static void PruneUnusedXObjectReferences(PdfDocument doc)
    {
        var ctx = new PruneContext();

        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
        {
            var page = doc.GetPage(i);
            var pageResources = page.GetResources()?.GetPdfObject();
            var pageXObjects = GetXObjectDict(page);

            ProcessScope(TryGetPageContent(page), pageResources, pageXObjects, ctx);

            // Annotation appearance streams draw from their own /Resources, or — if
            // absent — inherit the page's. Treat each as an additional content scope.
            foreach (var stream in CollectAppearanceStreams(page))
                ProcessForm(stream, pageXObjects, ctx);
        }

        foreach (var (dict, used) in ctx.UsedNames)
        {
            if (ctx.UnsafeDicts.Contains(dict)) continue;

            var keysToRemove = dict.KeySet()
                .Where(k => !used.Contains(k.GetValue()))
                .ToList();

            foreach (var key in keysToRemove)
                dict.Remove(key);
        }
    }

    private sealed class PruneContext
    {
        public Dictionary<PdfDictionary, HashSet<string>> UsedNames { get; } = new();
        public HashSet<PdfDictionary> UnsafeDicts { get; } = new();
        public HashSet<(PdfStream Form, object Context)> VisitedForms { get; } = new();
    }

    /// <summary>
    /// Analyzes one content scope. <paramref name="resources"/> is the resource
    /// dictionary that owns the content (may be null when a form inherits its caller's
    /// resources, in which case <paramref name="inheritedXObjects"/> is used).
    /// </summary>
    private static void ProcessScope(
        byte[]? content,
        PdfDictionary? resources,
        PdfDictionary? inheritedXObjects,
        PruneContext ctx)
    {
        // Effective /XObject dictionary for resolving names in this content: a form
        // with its own /Resources never falls back to the caller, so only inherit
        // when /Resources is entirely absent.
        var xObjects = resources is null
            ? inheritedXObjects
            : resources.GetAsDictionary(PdfName.XObject);

        if (xObjects is null) return;

        if (!ctx.UsedNames.ContainsKey(xObjects))
            ctx.UsedNames[xObjects] = new HashSet<string>(StringComparer.Ordinal);
        var set = ctx.UsedNames[xObjects];

        // A Type 3 font's glyph procedures can draw XObjects through these same
        // resources (inheriting them when the font omits /Resources). We do not
        // analyze CharProcs, so never prune a dictionary reachable from one.
        if (content is null || (resources is not null && HasType3Font(resources)))
        {
            ctx.UnsafeDicts.Add(xObjects);
            return;
        }

        HashSet<string> namesInScope;
        try
        {
            namesInScope = ExtractDoNames(content);
        }
        catch
        {
            // Unparseable content (e.g. inline images) → never prune the dict.
            ctx.UnsafeDicts.Add(xObjects);
            return;
        }

        set.UnionWith(namesInScope);

        // Recurse into the Form XObjects this scope actually draws.
        foreach (var name in namesInScope)
        {
            var stream = xObjects.GetAsStream(new PdfName(name));
            if (stream is null || stream.GetAsName(PdfName.Subtype) != PdfName.Form)
                continue;

            ProcessForm(stream, xObjects, ctx);
        }
    }

    private static void ProcessForm(
        PdfStream formStream, PdfDictionary? callerXObjects, PruneContext ctx)
    {
        var resources = formStream.GetAsDictionary(PdfName.Resources);

        // Resolution context: the form's own resources, or the caller's when it
        // inherits. Visit each (form, context) pair once — the same form drawn from
        // two different inherited contexts must contribute to both.
        object context = resources ?? (object?)callerXObjects ?? formStream;
        if (!ctx.VisitedForms.Add((formStream, context)))
            return;

        ProcessScope(TryGetBytes(formStream), resources, callerXObjects, ctx);
    }

    private static bool HasType3Font(PdfDictionary resources)
    {
        var fonts = resources.GetAsDictionary(PdfName.Font);
        if (fonts is null) return false;

        foreach (var value in fonts.Values())
        {
            var font = value is PdfIndirectReference r
                ? r.GetRefersTo() as PdfDictionary
                : value as PdfDictionary;
            if (font?.GetAsName(PdfName.Subtype) == PdfName.Type3)
                return true;
        }

        return false;
    }

    private static IEnumerable<PdfStream> CollectAppearanceStreams(PdfPage page)
    {
        foreach (var annot in page.GetAnnotations())
        {
            var ap = annot.GetPdfObject().GetAsDictionary(PdfName.AP);
            if (ap is null) continue;

            foreach (var value in ap.Values())
            {
                var resolved = value is PdfIndirectReference r ? r.GetRefersTo() : value;
                if (resolved is PdfStream stream)
                {
                    yield return stream;
                }
                else if (resolved is PdfDictionary states)
                {
                    // Appearance sub-dictionary keyed by annotation state.
                    foreach (var inner in states.Values())
                    {
                        if (ResolveStream(inner) is { } innerStream)
                            yield return innerStream;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Tokenizes a content stream and returns the set of names invoked as
    /// `/Name Do`. Throws if an inline image (`BI`) is encountered, since the raw
    /// image bytes desync the tokenizer and could hide a real `Do` invocation.
    /// </summary>
    private static HashSet<string> ExtractDoNames(byte[] content)
        => ExtractResourceNames(content).XObjectNames;

    /// <summary>
    /// Removes name→Font entries from page (and nested form) /Font resource
    /// dictionaries when those names are never selected by a `Tf` operator in any
    /// content stream that can resolve names through them. Unparseable scopes and
    /// Type 3 font scopes are left untouched.
    /// </summary>
    private static void PruneUnusedFontReferences(PdfDocument doc)
    {
        var ctx = new FontPruneContext();
        MarkAcroFormDefaultFontsUnsafe(doc, ctx);

        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
        {
            var page = doc.GetPage(i);
            var pageResources = page.GetResources()?.GetPdfObject();
            var pageContextKey = pageResources is null ? null : ObjectKey(pageResources);
            var pageFonts = pageResources?.GetAsDictionary(PdfName.Font);
            var pageXObjects = pageResources?.GetAsDictionary(PdfName.XObject);

            ProcessFontScope(
                TryGetPageContent(page),
                pageResources,
                inheritedFonts: null,
                inheritedXObjects: null,
                inheritedResourceContextKey: pageContextKey,
                ctx);

            foreach (var stream in CollectAppearanceStreams(page))
            {
                ProcessFontForm(
                    stream,
                    pageFonts,
                    pageXObjects,
                    pageContextKey,
                    ctx);
            }
        }

        foreach (var usage in ctx.FontDictionaries.Values)
        {
            if (usage.Unsafe) continue;

            var keysToRemove = usage.Dictionary.KeySet()
                .Where(k => !usage.UsedNames.Contains(k.GetValue()))
                .ToList();

            foreach (var key in keysToRemove)
                usage.Dictionary.Remove(key);
        }
    }

    private sealed class FontPruneContext
    {
        public Dictionary<string, FontDictionaryUsage> FontDictionaries { get; } =
            new(StringComparer.Ordinal);
        public HashSet<string> VisitedForms { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FontDictionaryUsage(PdfDictionary dictionary)
    {
        public PdfDictionary Dictionary { get; } = dictionary;
        public HashSet<string> UsedNames { get; } = new(StringComparer.Ordinal);
        public bool Unsafe { get; set; }
    }

    private static void ProcessFontScope(
        byte[]? content,
        PdfDictionary? resources,
        PdfDictionary? inheritedFonts,
        PdfDictionary? inheritedXObjects,
        string? inheritedResourceContextKey,
        FontPruneContext ctx)
    {
        var fonts = resources is null
            ? inheritedFonts
            : resources.GetAsDictionary(PdfName.Font);
        var xObjects = resources is null
            ? inheritedXObjects
            : resources.GetAsDictionary(PdfName.XObject);
        var resourceContextKey = resources is null
            ? inheritedResourceContextKey
            : ObjectKey(resources);

        var fontUsage = GetFontUsage(fonts, ctx);
        if (fonts is null && xObjects is null)
            return;

        if (content is null)
        {
            if (fontUsage is not null)
                fontUsage.Unsafe = true;
            return;
        }

        if (fonts is not null && HasType3FontDictionary(fonts) && fontUsage is not null)
            fontUsage.Unsafe = true;

        ContentResourceNames namesInScope;
        try
        {
            namesInScope = ExtractResourceNames(content);
        }
        catch
        {
            if (fontUsage is not null)
                fontUsage.Unsafe = true;
            return;
        }

        if (fontUsage is not null)
            fontUsage.UsedNames.UnionWith(namesInScope.FontNames);

        if (xObjects is null)
            return;

        foreach (var name in namesInScope.XObjectNames)
        {
            var stream = xObjects.GetAsStream(new PdfName(name));
            if (stream is null || stream.GetAsName(PdfName.Subtype) != PdfName.Form)
                continue;

            ProcessFontForm(stream, fonts, xObjects, resourceContextKey, ctx);
        }
    }

    private static void ProcessFontForm(
        PdfStream formStream,
        PdfDictionary? callerFonts,
        PdfDictionary? callerXObjects,
        string? callerResourceContextKey,
        FontPruneContext ctx)
    {
        var resources = formStream.GetAsDictionary(PdfName.Resources);
        var resourceContextKey = resources is null
            ? callerResourceContextKey
            : ObjectKey(resources);
        var visitKey = $"{ObjectKey(formStream)}|{resourceContextKey ?? "none"}";

        if (!ctx.VisitedForms.Add(visitKey))
            return;

        ProcessFontScope(
            TryGetBytes(formStream),
            resources,
            resources is null ? callerFonts : null,
            resources is null ? callerXObjects : null,
            resourceContextKey,
            ctx);
    }

    private static FontDictionaryUsage? GetFontUsage(
        PdfDictionary? fonts,
        FontPruneContext ctx)
    {
        if (fonts is null)
            return null;

        var key = ObjectKey(fonts);
        if (!ctx.FontDictionaries.TryGetValue(key, out var usage))
        {
            usage = new FontDictionaryUsage(fonts);
            ctx.FontDictionaries.Add(key, usage);
        }

        return usage;
    }

    private static void MarkAcroFormDefaultFontsUnsafe(
        PdfDocument doc,
        FontPruneContext ctx)
    {
        var acroForm = doc.GetCatalog()
            .GetPdfObject()
            .GetAsDictionary(PdfName.AcroForm);
        var defaultResources = acroForm?.GetAsDictionary(new PdfName("DR"));
        var defaultFonts = defaultResources?.GetAsDictionary(PdfName.Font);
        var usage = GetFontUsage(defaultFonts, ctx);

        if (usage is not null)
            usage.Unsafe = true;
    }

    private static bool HasType3FontDictionary(PdfDictionary fonts)
    {
        foreach (var value in fonts.Values())
        {
            var font = value is PdfIndirectReference r
                ? r.GetRefersTo() as PdfDictionary
                : value as PdfDictionary;
            if (font?.GetAsName(PdfName.Subtype) == PdfName.Type3)
                return true;
        }

        return false;
    }

    private sealed class ContentResourceNames
    {
        public HashSet<string> XObjectNames { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FontNames { get; } = new(StringComparer.Ordinal);
    }

    private static ContentResourceNames ExtractResourceNames(byte[] content)
    {
        var used = new ContentResourceNames();
        var tokenizer = new PdfTokenizer(
            new RandomAccessFileOrArray(
                new RandomAccessSourceFactory().CreateSource(content)));

        string? lastName = null;
        while (tokenizer.NextToken())
        {
            var type = tokenizer.GetTokenType();
            if (type == PdfTokenizer.TokenType.Name)
            {
                lastName = tokenizer.GetStringValue();
            }
            else if (type == PdfTokenizer.TokenType.Other)
            {
                var op = tokenizer.GetStringValue();
                if ("Do".Equals(op, StringComparison.Ordinal) && lastName is not null)
                    used.XObjectNames.Add(lastName);
                else if ("Tf".Equals(op, StringComparison.Ordinal) && lastName is not null)
                    used.FontNames.Add(lastName);
                else if ("BI".Equals(op, StringComparison.Ordinal))
                    throw new InvalidOperationException("Inline image; cannot prune safely.");
                lastName = null;
            }
        }

        return used;
    }

    private static string ObjectKey(PdfObject obj)
    {
        var reference = obj.GetIndirectReference();
        return reference is null
            ? $"direct:{RuntimeHelpers.GetHashCode(obj)}"
            : $"{reference.GetObjNumber()}:{reference.GetGenNumber()}";
    }

    private static byte[]? TryGetPageContent(PdfPage page)
    {
        try { return page.GetContentBytes(); }
        catch { return null; }
    }

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

    private static void ApplyEncryption(WriterProperties writerProps, string? password)
    {
        if (string.IsNullOrEmpty(password))
            return;

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var permissions =
            EncryptionConstants.ALLOW_PRINTING |
            EncryptionConstants.ALLOW_MODIFY_CONTENTS |
            EncryptionConstants.ALLOW_COPY |
            EncryptionConstants.ALLOW_MODIFY_ANNOTATIONS |
            EncryptionConstants.ALLOW_FILL_IN |
            EncryptionConstants.ALLOW_SCREENREADERS |
            EncryptionConstants.ALLOW_ASSEMBLY |
            EncryptionConstants.ALLOW_DEGRADED_PRINTING;

        writerProps.SetStandardEncryption(
            userPassword: passwordBytes,
            ownerPassword: passwordBytes,
            permissions: permissions,
            encryptionAlgorithm: EncryptionConstants.ENCRYPTION_AES_256);
    }
}
