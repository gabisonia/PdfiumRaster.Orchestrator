# Samples

## Parallel page export

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
    .Select(pageIndex => orchestrator.SavePageAsync(
        "input.pdf",
        pageIndex,
        $"page-{pageIndex + 1:D4}.png",
        new PdfImageConversionOptions { Format = PdfImageOutputFormat.Png }));

await Task.WhenAll(jobs);
await orchestrator.CompleteAsync();
```

For web applications, register `PdfRenderOrchestrator` once as a singleton and drain it from an `IHostedService` at
host shutdown. See the [API guide](../docs/API.md#aspnet-core-application-lifetime).
