# PdfiumRaster.Orchestrator

[![NuGet](https://img.shields.io/nuget/v/PdfiumRaster.Orchestrator.svg)](https://www.nuget.org/packages/PdfiumRaster.Orchestrator)
[![CI](https://github.com/gabisonia/PdfiumRaster.Orchestrator/actions/workflows/ci.yml/badge.svg)](https://github.com/gabisonia/PdfiumRaster.Orchestrator/actions/workflows/ci.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/gabisonia/PdfiumRaster.Orchestrator/badge)](https://scorecard.dev/viewer/?uri=github.com/gabisonia/PdfiumRaster.Orchestrator)
[![License](https://img.shields.io/github/license/gabisonia/PdfiumRaster.Orchestrator)](LICENSE)

> [!IMPORTANT]
> `PdfiumRaster.Orchestrator` is a multi-process orchestration layer built on top of
> [PdfiumRaster](https://github.com/gabisonia/PdfiumRaster), not a separate PDF rendering engine. PdfiumRaster performs
> the PDF-to-image conversion; this package solves process-level parallelism, bounded scheduling, crash isolation,
> hard timeouts, and worker replacement by coordinating isolated PdfiumRaster workers over private local named pipes.
> It intentionally does not provide PDF editing, text extraction, form filling, signing, or a viewer UI, and its
> workers are not a security sandbox.

`PdfiumRaster.Orchestrator` adds true parallel PDFium rendering to
[`PdfiumRaster`](https://www.nuget.org/packages/PdfiumRaster) by running a fixed number of isolated local worker
processes. Each worker has its own PDFium runtime and communicates with the application over a private named pipe
restricted to the application's operating-system user.
The orchestrator owns a bidirectional `NamedPipeServerStream`, and its child worker connects with a
`NamedPipeClientStream`. There is one persistent pipe per worker process; the pipe carries startup handshakes,
requests, in-memory inputs, and results. The roles, framing, data flow, failure handling, and trust boundary are
described in the [architecture guide](docs/ARCHITECTURE.md#named-pipe-roles).

The package depends on `PdfiumRaster` versions from 2.0.1 up to, but not including, 3.0.0. Installing the orchestrator
therefore installs a compatible core rendering library automatically while allowing the two packages to release
independently.

## Why worker processes?

[PDFium's public API is not thread-safe](https://pdfium.googlesource.com/pdfium/+/main/public/fpdfview.h): embedders
must call it from one thread or ensure that only one PDFium call executes at a time. PDFium initialization also owns
process-global native resources. PdfiumRaster follows those requirements with a process-wide native-call lock and a
reference-counted `PdfiumLibrary` lifetime. Calling `PdfiumLibrary.Initialize()` more than once creates managed lifetime
leases over the same initialized native runtime; it does not create independent PDFium engines that can render in
parallel.

Tasks and threads can improve admission control and managed image encoding, but native PDFium work remains serialized
inside one process. The orchestrator obtains true rendering parallelism by placing each worker in a separate process.
Each worker then has its own address space, PDFium global state, and native-call lock, so `WorkerCount` workers can run
that many native operations concurrently. The boundary also allows a crashed or timed-out worker to be terminated
without terminating the application. The tradeoffs are one native runtime and memory footprint per worker plus
named-pipe transfer overhead for in-memory inputs and outputs.

## Installation

> [!IMPORTANT]
> Choose exactly one orchestrator package. Install `PdfiumRaster.Orchestrator` for the all-in-one package, or install
> one `PdfiumRaster.Orchestrator.<rid>` package for a specific platform. Do not combine the all-in-one package with a
> platform-specific package, and do not install multiple platform-specific packages.

For the simplest setup, install the all-in-one package:

```bash
dotnet add package PdfiumRaster.Orchestrator
```

It contains every supported worker and automatically selects the matching worker when the application is built or
published. To reduce restore and deployment size when the target runtime is known, install one platform-specific
package and publish for the matching RID, for example:

```bash
dotnet add package PdfiumRaster.Orchestrator.linux-x64
dotnet publish -r linux-x64
```

See [worker package choices](docs/API.md#worker-package-choices) for every supported platform.

> [!NOTE]
> `new PdfRenderOrchestrator()` uses bounded defaults without requiring an options object: up to four workers, a
> 42-request waiting queue, wait-mode backpressure, a 256-page batch limit, a 15-second worker startup timeout, and
> three worker-replacement attempts. Hard request timeouts and byte limits are disabled by default, and worker
> temporary files use the operating-system temporary directory. Structured logging is also disabled until an
> `ILoggerFactory` is supplied. See the complete
> [default options table](docs/API.md#default-options).

The following example uses cancellable asynchronous startup and customizes the timeout and resource limits:

```csharp
using PdfiumRaster;
using PdfiumRaster.Orchestration;

await using var orchestrator = await PdfRenderOrchestrator.CreateAsync(new PdfRenderOrchestratorOptions
{
    WorkerCount = Math.Min(Environment.ProcessorCount, 4),
    QueueCapacity = 42,
    RequestTimeout = TimeSpan.FromSeconds(30),
    MaximumInputBytes = 512L * 1024 * 1024,
    MaximumBitmapBytes = 256L * 1024 * 1024,
    MaximumOutputBytes = 512L * 1024 * 1024,
});

var first = orchestrator.RenderPageAsync("first.pdf", pageIndex: 0);
var second = orchestrator.SavePageAsync(
    "second.pdf",
    pageIndex: 0,
    "second.png",
    new PdfImageConversionOptions { Format = PdfImageOutputFormat.Png });

await Task.WhenAll(first, second);
await orchestrator.CompleteAsync();
```

`CreateAsync` is recommended when the application already has an asynchronous startup path because worker process
connection and handshake do not block that thread and can be canceled. The constructor remains available and returns
only after all workers are ready. Generic Host and ASP.NET Core integration starts workers asynchronously for you.

Page indexes are zero-based. Path, byte-array, and stream inputs are supported, along with raw `PdfBitmap`, image-path,
and caller-owned stream outputs. Input streams are read from their current position. Unless `leaveOpen: true` is used,
the orchestrator owns and disposes an input stream after completion, cancellation, validation failure, or queue
rejection. Streaming batches are lazy, so their validation, submission, and stream ownership begin with enumeration;
an enumerable that is never started does not take ownership. Output streams always remain caller-owned.

Inspect a document through the same isolated worker pool before deciding what to render. Page sizes are returned in PDF
points (`1/72` inch) and preserve zero-based page order:

```csharp
var document = await orchestrator.InspectDocumentAsync("report.pdf");
Console.WriteLine($"Pages: {document.PageCount}");

foreach (var pageSize in document.PageSizes)
{
    Console.WriteLine($"{pageSize.Width} x {pageSize.Height} points");
}
```

`InspectDocumentAsync`, `GetPageCountAsync`, and `GetPageSizesAsync` accept path, `byte[]`, and `Stream` inputs,
passwords, and cancellation. They follow the same bounded queue, input limit, timeout, stream-ownership,
crash-isolation, logging, tracing, and metrics behavior as rendering requests; PDF parsing remains inside the worker
process.

For several pages from the same document, use `RenderPagesAsync`, `RenderPagesStreamAsync`, or `SavePagesAsync`. One
batch is one scheduled request: its worker transfers and opens the PDF once, reuses a `PdfRenderSession`, and processes
pages in the supplied order. Split very large exports into several batches to use multiple workers concurrently.

```csharp
var pages = await orchestrator.RenderPagesAsync("report.pdf", new[] { 0, 1, 2 });

await orchestrator.SavePagesAsync("report.pdf", new[]
{
    new PdfPageFileOutput(0, "page-1.png"),
    new PdfPageFileOutput(1, "page-2.png"),
});
```

> [!IMPORTANT]
> `RenderPagesAsync` retains every returned bitmap until the complete batch is ready. For large bitmap batches, prefer
> `RenderPagesStreamAsync`: it yields `PdfPageBitmap` values in request order and buffers at most one completed page
> between the worker reader and your consumer.

```csharp
await foreach (var page in orchestrator.RenderPagesStreamAsync(
                   "report.pdf",
                   new[] { 0, 3, 7 },
                   cancellationToken: cancellationToken))
{
    await ProcessBitmapAsync(page.PageIndex, page.Bitmap, cancellationToken);
}
```

Each yielded bitmap is caller-owned. Consume or release it before requesting the next item to keep memory bounded.
Cancellation or ending enumeration early aborts that batch. If it is already active, its worker is replaced because
unread private-protocol frames cannot safely be reused. The failed or abandoned batch is not retried.

Workers run locally with the same operating-system identity and filesystem permissions as the calling application.
They isolate PDFium crashes and make hard timeouts possible, but they are not a security sandbox. Prefer path inputs for
large PDFs; byte-array and stream inputs must cross a named pipe and are spooled to a worker-owned temporary file.
`TemporaryDirectory` can place those private worker directories on a controlled volume. Optional input, per-bitmap,
and total-output byte limits fail with `PdfRenderResourceLimitException`; they are unlimited by default for backward
compatibility.

`WorkerCount` defaults to the smaller of four and the logical processor count, and cannot exceed that processor count.
`RequestTimeout` is disabled by default. It starts when a request is dispatched and covers input transfer, rendering,
encoding, and output transfer; it does not include time spent waiting in the queue. A timeout promptly faults the
request and terminates its worker. A custom caller stream that ignores cancellation may delay final stream cleanup and
orchestrator disposal even though the request task has already timed out.

The queue is bounded. `PdfRenderQueueFullMode.Wait` applies asynchronous backpressure;
`PdfRenderQueueFullMode.Reject` faults a rejected submission with `PdfRenderQueueFullException`. `CompleteAsync()`
drains accepted work, while `CancelAsync()`, `Dispose()`, and `DisposeAsync()` cancel queued work and wait for active
uninterruptible work before stopping the workers. A streaming consumer must continue reading, cancel, or dispose its
enumerator; graceful completion waits for accepted streams just as it waits for other accepted requests.

## Observability

The orchestrator supports the standard .NET observability stack while retaining its `PdfiumRaster-Orchestrator`
`EventSource` for `dotnet-trace`. Pass the application's `ILoggerFactory` for structured lifecycle and failure logs:

```csharp
await using var orchestrator = await PdfRenderOrchestrator.CreateAsync(new PdfRenderOrchestratorOptions
{
    LoggerFactory = loggerFactory,
});
```

Request activities and operational metrics use the public
`PdfRenderOrchestratorDiagnostics.ActivitySourceName` and `PdfRenderOrchestratorDiagnostics.MeterName` constants.
OpenTelemetry applications can register those names with `AddSource(...)` and `AddMeter(...)`. Telemetry includes
queue and execution durations, outcomes, queue depth, active requests, worker availability, restarts, and rejections,
but never PDF or image paths, passwords, pipe data, worker standard error, or document content. See
[diagnostics](docs/API.md#diagnostics) for the metric schema and setup example.

Applications outside the .NET hosting abstractions can read the same in-memory lifecycle information without starting
a probe request:

```csharp
var status = orchestrator.GetStatus();
Console.WriteLine($"{status.State}: {status.AvailableWorkerCount}/{status.WorkerCount} workers available");
```

The status is a point-in-time snapshot. Use metrics for continuous queue and request monitoring.

> [!IMPORTANT]
> In a .NET Generic Host or ASP.NET Core application, use `AddPdfiumRasterOrchestrator`. It registers exactly one
> orchestrator, automatically supplies the host logger factory, starts the workers asynchronously with the host, and
> handles graceful shutdown. Do not create an orchestrator per request or add a second manual singleton.

Register the orchestrator and its readiness check:

```csharp
using PdfiumRaster;
using PdfiumRaster.Orchestration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPdfiumRasterOrchestrator(options =>
{
    options.WorkerCount = Math.Min(Environment.ProcessorCount, 4);
    options.QueueCapacity = 100;
    options.RequestTimeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHealthChecks()
    .AddPdfiumRasterOrchestrator(tags: new[] { "ready" });

var app = builder.Build();
app.MapHealthChecks("/health/ready");
```

The readiness check is healthy when all workers are available, degraded during initial startup or worker replacement,
and unhealthy after a terminal failure or once shutdown begins. It inspects in-memory state and does not render a
probe PDF. Each application replica owns its own singleton, so total worker processes equal the worker count multiplied
by the number of replicas. See the [hosting and health-check guide](docs/API.md#net-hosting-and-health-checks) for
registration options and shutdown semantics.

Worker startup failures throw `PdfWorkerStartupException`. Active crashes and hard timeouts throw
`PdfWorkerCrashedException` and `PdfWorkerTimeoutException` for the affected request, then start a replacement worker.
Errors reported by a healthy worker are surfaced as `PdfWorkerRemoteException`; malformed communication throws
`PdfWorkerProtocolException` and replaces the worker. Failed requests are never retried automatically.

The package supports self-contained workers on Windows x86/x64/ARM64, Linux ARM32/x64/ARM64, musl Linux x64/ARM64, and
macOS x64/ARM64. Modern .NET does not provide a self-contained worker runtime for 32-bit Linux, so `linux-x86` and
`linux-musl-x86` are not supported.

See [API usage](docs/API.md), [architecture](docs/ARCHITECTURE.md), [release history](CHANGELOG.md),
[support](SUPPORT.md), and [releasing](docs/RELEASING.md) for more detail.
For worker startup, pipe, crash, timeout, filesystem, and diagnostic guidance, see
[troubleshooting](docs/TROUBLESHOOTING.md).
