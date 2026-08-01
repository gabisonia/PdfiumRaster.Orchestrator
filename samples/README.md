# Samples

## Parallel page export

The compilable sample is in [`ParallelPageExport`](ParallelPageExport/). Run it with an input PDF and optional output
directory:

```bash
dotnet run --project samples/ParallelPageExport -- input.pdf ./pages
```

```csharp
using PdfiumRaster;
using PdfiumRaster.Orchestration;

using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
{
    WorkerCount = Math.Min(Environment.ProcessorCount, 4),
    QueueCapacity = 64,
});

var pageCount = PdfImageConverter.GetPageCount("input.pdf");
var jobs = Enumerable.Range(0, pageCount)
    .Chunk(16)
    .Select(batch => orchestrator.SavePagesAsync(
        "input.pdf",
        batch.Select(pageIndex => new PdfPageFileOutput(
            pageIndex,
            $"page-{pageIndex + 1:D4}.png")).ToArray(),
        new PdfImageConversionOptions { Format = PdfImageOutputFormat.Png }));

await Task.WhenAll(jobs);
await orchestrator.CompleteAsync();
```

Each 16-page batch opens the PDF once; multiple batches can run on separate workers. Tune the batch size for document
complexity, memory limits, and the desired balance between reuse and parallelism.

For web applications, register `PdfRenderOrchestrator` once as a singleton and drain it from an `IHostedService` at
host shutdown. See the compilable [`AspNetLifecycle`](AspNetLifecycle/) sample and the
[API guide](../docs/API.md#aspnet-core-application-lifetime).
