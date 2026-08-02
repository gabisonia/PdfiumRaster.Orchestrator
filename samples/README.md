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

## ASP.NET Core lifecycle and observability

The compilable [`AspNetLifecycle`](AspNetLifecycle/) sample registers one orchestrator singleton, supplies the host's
`ILoggerFactory`, subscribes OpenTelemetry to the orchestrator activity source and meter, and drains the orchestrator
from an `IHostedService` at host shutdown. Its console exporter is intended only to make traces and metrics visible
while learning or debugging; configure the application's production exporter separately.

Run the sample on a known URL:

```bash
ASPNETCORE_URLS=http://localhost:5050 dotnet run --project samples/AspNetLifecycle
```

Then submit a local PDF path. The console output includes the incoming ASP.NET Core activity, its correlated
orchestrator child activity, request metrics, and structured lifecycle logs:

```bash
curl -X POST http://localhost:5050/render \
  -H 'Content-Type: application/json' \
  -d '{"pdfPath":"/absolute/path/to/input.pdf","pageIndex":0}'
```

The endpoint is intentionally small and demonstrates local path rendering; validate and authorize path inputs before
adapting it for a production application. See the [lifetime guide](../docs/API.md#aspnet-core-application-lifetime)
and the complete [observability schema](../docs/API.md#diagnostics).
