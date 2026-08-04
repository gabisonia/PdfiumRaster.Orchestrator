namespace PdfiumRaster.Orchestration;

/// <summary>
/// Describes the pages in a PDF document inspected by an isolated worker.
/// </summary>
public sealed class PdfDocumentInfo
{
    internal PdfDocumentInfo(IReadOnlyList<PdfPageSize> pageSizes)
    {
        PageSizes = Array.AsReadOnly(pageSizes.ToArray());
    }

    /// <summary>
    /// Gets the number of pages in the document.
    /// </summary>
    public int PageCount => PageSizes.Count;

    /// <summary>
    /// Gets the page sizes in zero-based page order, expressed in PDF points where one point is 1/72 inch.
    /// </summary>
    public IReadOnlyList<PdfPageSize> PageSizes { get; }
}
