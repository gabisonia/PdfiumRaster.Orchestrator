namespace PdfiumRaster.Orchestration;

/// <summary>
/// Identifies a zero-based PDF page and its exact destination image path in a multi-page save request.
/// </summary>
public sealed class PdfPageFileOutput
{
    /// <summary>Creates a page-to-file mapping.</summary>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="imagePath">Destination image path, converted to an absolute path.</param>
    public PdfPageFileOutput(int pageIndex, string imagePath)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex,
                "Page index must be zero or greater.");
        }

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ArgumentException("Image path cannot be null or whitespace.", nameof(imagePath));
        }

        PageIndex = pageIndex;
        ImagePath = Path.GetFullPath(imagePath);
    }

    /// <summary>Gets the zero-based page index.</summary>
    public int PageIndex { get; }

    /// <summary>Gets the absolute destination image path.</summary>
    public string ImagePath { get; }
}
