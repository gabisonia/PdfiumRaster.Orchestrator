using PdfiumRaster;
using PdfiumRaster.Orchestration;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: ParallelPageExport <input.pdf> [output-directory]");
    return 2;
}

var pdfPath = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args.Length == 2 ? args[1] : "pages");
Directory.CreateDirectory(outputDirectory);

using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
{
    WorkerCount = Math.Min(Environment.ProcessorCount, 4),
    QueueCapacity = 64,
});

var options = new PdfImageConversionOptions
{
    Format = PdfImageOutputFormat.Png,
};
var pageCount = PdfImageConverter.GetPageCount(pdfPath);
var jobs = Enumerable.Range(0, pageCount)
    .Chunk(16)
    .Select(batch => orchestrator.SavePagesAsync(
        pdfPath,
        batch.Select(pageIndex => new PdfPageFileOutput(
                pageIndex,
                Path.Combine(outputDirectory, $"page-{pageIndex + 1:D4}.png")))
            .ToArray(),
        options));

await Task.WhenAll(jobs);
await orchestrator.CompleteAsync();
return 0;
