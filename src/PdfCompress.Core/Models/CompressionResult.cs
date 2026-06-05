namespace PdfCompress.Core.Models;

public record CompressionResult(
    string FileName,
    long OriginalBytes,
    long CompressedBytes,
    string? Error = null)
{
    public bool Success => Error is null;
    public long SavedBytes => OriginalBytes - CompressedBytes;
    public double ReductionPercent =>
        OriginalBytes > 0 ? (1.0 - (double)CompressedBytes / OriginalBytes) * 100 : 0;
}
