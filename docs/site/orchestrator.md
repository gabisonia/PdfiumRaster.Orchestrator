# Getting started with the orchestrator

PdfiumRaster.Orchestrator runs PdfiumRaster in a bounded pool of local child processes. Each worker has its own PDFium
runtime, allowing native rendering to run in parallel. Failed requests are not automatically retried.

Install the all-in-one package:

```bash
dotnet add package PdfiumRaster.Orchestrator
```

Alternatively, install exactly one platform-specific package, such as `PdfiumRaster.Orchestrator.linux-x64`, and
publish for the matching RID. Do not combine worker packages. See [worker package choices](../API.md#worker-package-choices).

```csharp
using PdfiumRaster;
using PdfiumRaster.Orchestration;

await using var orchestrator = await PdfRenderOrchestrator.CreateAsync(new PdfRenderOrchestratorOptions
{
    WorkerCount = Math.Min(Environment.ProcessorCount, 4),
    QueueCapacity = 42,
    RequestTimeout = TimeSpan.FromSeconds(30),
});

var bitmap = await orchestrator.RenderPageAsync("input.pdf", pageIndex: 0);
await orchestrator.SavePageAsync("input.pdf", pageIndex: 1, "page-2.png",
    new PdfImageConversionOptions { Format = PdfImageOutputFormat.Png });
await orchestrator.CompleteAsync();
```

Page indexes are zero-based. `CreateAsync` starts workers asynchronously; reuse the orchestrator rather than creating
one for each request. For hosted .NET applications, use `AddPdfiumRasterOrchestrator` to manage its lifecycle.

## Operational behavior

The queue is bounded and waits by default when full; reject mode is also available. A request timeout starts on
dispatch, not while waiting in the queue. A timed-out or crashed worker is replaced, and its failed request is not
retried. `CompleteAsync` drains accepted work. Disposal cancels queued work and waits for active work to stop.

Prefer paths for large PDFs. Byte-array and stream inputs cross a private named pipe and are spooled to worker-owned
temporary files. Input streams are disposed by default unless `leaveOpen: true`; output streams remain caller-owned.
For large batches, `RenderPagesStreamAsync` yields pages in order and buffers at most one completed page between the
worker reader and consumer. Workers share the caller's OS identity and permissions and are not a security sandbox.

Continue with the [API guide](../API.md), [architecture](../ARCHITECTURE.md), and
[troubleshooting guide](../TROUBLESHOOTING.md).
