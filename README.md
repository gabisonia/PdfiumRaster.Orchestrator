# PdfiumRaster.Orchestrator

[![NuGet](https://img.shields.io/nuget/v/PdfiumRaster.Orchestrator.svg)](https://www.nuget.org/packages/PdfiumRaster.Orchestrator)

Companion repository: [PdfiumRaster](https://github.com/gabisonia/PdfiumRaster), the underlying PDF-to-image library.

`PdfiumRaster.Orchestrator` adds true parallel PDFium rendering to
[`PdfiumRaster`](https://www.nuget.org/packages/PdfiumRaster) by running a fixed number of isolated local worker
processes. Each worker has its own PDFium runtime and communicates with the application over a private named pipe.
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

```bash
dotnet add package PdfiumRaster.Orchestrator
```

```csharp
using PdfiumRaster;
using PdfiumRaster.Orchestration;

using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
{
    WorkerCount = Math.Min(Environment.ProcessorCount, 4),
    QueueCapacity = 42,
    RequestTimeout = TimeSpan.FromSeconds(30),
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

Page indexes are zero-based. Path, byte-array, and stream inputs are supported, along with raw `PdfBitmap`, image-path,
and caller-owned stream outputs. Input streams are read from their current position. Unless `leaveOpen: true` is used,
the orchestrator owns and disposes an input stream after completion, cancellation, validation failure, or queue
rejection. Output streams always remain caller-owned.

Workers run locally with the same operating-system identity and filesystem permissions as the calling application.
They isolate PDFium crashes and make hard timeouts possible, but they are not a security sandbox. Prefer path inputs for
large PDFs; byte-array and stream inputs must cross a named pipe and are spooled to a worker-owned temporary file.

`WorkerCount` defaults to the smaller of four and the logical processor count, and cannot exceed that processor count.
`RequestTimeout` is disabled by default. It starts when a request is dispatched and covers input transfer, rendering,
encoding, and output transfer; it does not include time spent waiting in the queue. A timeout promptly faults the
request and terminates its worker. A custom caller stream that ignores cancellation may delay final stream cleanup and
orchestrator disposal even though the request task has already timed out.

The queue is bounded. `PdfRenderQueueFullMode.Wait` applies asynchronous backpressure;
`PdfRenderQueueFullMode.Reject` faults a rejected submission with `PdfRenderQueueFullException`. `CompleteAsync()`
drains accepted work, while `CancelAsync()` and `Dispose()` cancel queued work and wait for active uninterruptible work
before stopping the workers.

In ASP.NET Core, register one orchestrator singleton per application process and inject it into controllers or scoped
services:

```csharp
using PdfiumRaster;
using PdfiumRaster.Orchestration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<PdfRenderOrchestrator>(_ =>
    new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
    {
        WorkerCount = Math.Min(Environment.ProcessorCount, 4),
        QueueCapacity = 100,
        RequestTimeout = TimeSpan.FromSeconds(30),
    }));
```

Do not create an orchestrator per request and do not call `CompleteAsync()` from request code. Drain it once during host
shutdown with an `IHostedService`; the DI container disposes the singleton afterward. Each application replica owns
its own singleton, so total worker processes equal the worker count multiplied by the number of replicas. See the
[ASP.NET Core lifetime guide](docs/API.md#aspnet-core-application-lifetime)
for the complete hosted-service implementation.

Worker startup failures throw `PdfWorkerStartupException`. Active crashes and hard timeouts throw
`PdfWorkerCrashedException` and `PdfWorkerTimeoutException` for the affected request, then start a replacement worker.
Errors reported by a healthy worker are surfaced as `PdfWorkerRemoteException`; malformed communication throws
`PdfWorkerProtocolException` and replaces the worker. Failed requests are never retried automatically.

The package supports self-contained workers on Windows x86/x64/ARM64, Linux ARM32/x64/ARM64, musl Linux x64/ARM64, and
macOS x64/ARM64. Modern .NET does not provide a self-contained worker runtime for 32-bit Linux, so `linux-x86` and
`linux-musl-x86` are not supported.

See [API usage](docs/API.md), [architecture](docs/ARCHITECTURE.md), and [releasing](docs/RELEASING.md) for more detail.
