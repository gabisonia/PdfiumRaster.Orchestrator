namespace PdfiumRaster.Orchestration;

/// <summary>
/// Identifies one caller-owned bitmap yielded by a streaming multi-page render request.
/// </summary>
public sealed class PdfPageBitmap
{
    /// <summary>
    /// Initializes a page result.
    /// </summary>
    /// <param name="position">Zero-based position in the requested page sequence.</param>
    /// <param name="pageIndex">Zero-based PDF page index.</param>
    /// <param name="bitmap">The caller-owned rendered bitmap.</param>
    public PdfPageBitmap(int position, int pageIndex, PdfBitmap bitmap)
    {
        if (position < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position,
                "Result position must be zero or greater.");
        }

        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex,
                "Page index must be zero or greater.");
        }

        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        Position = position;
        PageIndex = pageIndex;
    }

    /// <summary>
    /// Gets the zero-based position in the requested page sequence.
    /// </summary>
    public int Position { get; }

    /// <summary>
    /// Gets the zero-based PDF page index.
    /// </summary>
    public int PageIndex { get; }

    /// <summary>
    /// Gets the caller-owned rendered bitmap.
    /// </summary>
    public PdfBitmap Bitmap { get; }
}
