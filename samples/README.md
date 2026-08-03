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

await using var orchestrator = await PdfRenderOrchestrator.CreateAsync(new PdfRenderOrchestratorOptions
{
    WorkerCount = Math.Min(Environment.ProcessorCount, 4),
    QueueCapacity = 64,
});

var pageCount = await orchestrator.GetPageCountAsync("input.pdf");
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

The page count is inspected in an isolated worker before export. Each 16-page batch then opens the PDF once; multiple
batches can run on separate workers. Tune the batch size for document complexity, memory limits, and the desired
balance between reuse and parallelism.

## Streaming bitmap batches

When the caller needs raw bitmaps rather than worker-encoded files, stream a batch to avoid retaining every page until
the last render finishes:

```csharp
await foreach (var page in orchestrator.RenderPagesStreamAsync(
                   "input.pdf",
                   Enumerable.Range(0, pageCount).ToArray(),
                   cancellationToken: cancellationToken))
{
    Console.WriteLine($"{page.PageIndex}: {page.Bitmap.Width}x{page.Bitmap.Height}");
    await ProcessBitmapAsync(page.Bitmap, cancellationToken);
}
```

Results preserve request order and the completed-result buffer has capacity one. Stop enumeration only when you intend
to abort the rest of the batch; the orchestrator replaces that worker before later work uses the slot.

## ASP.NET Core lifecycle and observability

The compilable [`AspNetLifecycle`](AspNetLifecycle/) sample uses `AddPdfiumRasterOrchestrator` for singleton ownership,
automatic host logging, startup, and graceful shutdown. It registers the orchestrator readiness check at
`/health/ready` and subscribes OpenTelemetry to the orchestrator activity source and meter. Its console exporter is
intended only to make traces and metrics visible while learning or debugging; configure the application's production
exporter separately.

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

Check readiness without rendering a PDF:

```bash
curl http://localhost:5050/health/ready
```

The endpoint is intentionally small and demonstrates local path rendering; validate and authorize path inputs before
adapting it for a production application. See the [hosting guide](../docs/API.md#net-hosting-and-health-checks)
and the complete [observability schema](../docs/API.md#diagnostics).
