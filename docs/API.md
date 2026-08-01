# API guide

Install an orchestrator package. Its dependency brings in a compatible
[`PdfiumRaster`](https://www.nuget.org/packages/PdfiumRaster) version automatically.

```bash
dotnet add package PdfiumRaster.Orchestrator
```

## Creating an orchestrator

Create one long-lived `PdfRenderOrchestrator` for each application process:

```csharp
using PdfiumRaster.Orchestration;

using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
{
    WorkerCount = Math.Min(Environment.ProcessorCount, 4),
    QueueCapacity = 100,
    QueueFullMode = PdfRenderQueueFullMode.Wait,
    RequestTimeout = TimeSpan.FromSeconds(30),
    WorkerStartupTimeout = TimeSpan.FromSeconds(15),
    MaximumBatchPages = 256,
    MaximumInputBytes = 512L * 1024 * 1024,
    MaximumBitmapBytes = 256L * 1024 * 1024,
    MaximumOutputBytes = 512L * 1024 * 1024,
    TemporaryDirectory = Path.Combine(Path.GetTempPath(), "my-app-pdf-workers"),
});
```

`WorkerCount` is the number of independent PDFium processes and may not exceed the logical processor count.
`QueueCapacity` bounds accepted work waiting for a worker. `Wait` asynchronously applies backpressure; `Reject` throws
`PdfRenderQueueFullException`. `RequestTimeout` starts when a request is dispatched and covers input transfer, PDFium
rendering, image encoding, and output transfer while excluding queue time. A timeout promptly faults the request and
terminates that worker. A custom caller stream that ignores cancellation can delay final cleanup and orchestrator
disposal after the request task has timed out. The value must be greater than zero and no more than approximately 49.7
days; `null` disables the request deadline.

`WorkerStartupTimeout` bounds process connection and handshake time and defaults to 15 seconds.
`WorkerRestartDelays` defaults to 250 milliseconds, one second, and four seconds; each entry is the delay before one
replacement attempt. Both settings are validated and snapshotted during construction. See the
[troubleshooting guide](TROUBLESHOOTING.md#startup-timeout-and-replacement-policy) before increasing them in response
to persistent startup failures.

The three byte limits are optional and unlimited by default. `MaximumInputBytes` applies to path, array, and stream
PDF inputs; `MaximumBitmapBytes` applies to each uncompressed bitmap; `MaximumOutputBytes` applies to all bitmap pixels
or encoded output produced by one request. `TemporaryDirectory` selects the parent volume for private worker spool
directories. `MaximumBatchPages` defaults to 256 and prevents an accidentally enormous batch from monopolizing one
worker.

## Rendering

Page indexes are zero-based. Path, `byte[]`, and `Stream` PDF inputs are accepted. Prefer paths for large documents.
Byte arrays and streams must cross the named pipe and are spooled to a worker-owned, owner-only temporary directory.
Input streams are read from their current position. Unless `leaveOpen: true` is passed, the orchestrator assumes
ownership when the method is called and disposes the input after completion, cancellation, validation failure, or queue
rejection.

```csharp
using PdfiumRaster;
using PdfiumRaster.Orchestration;

var bitmap = await orchestrator.RenderPageAsync(
    "input.pdf",
    pageIndex: 0,
    new PdfImageConversionOptions
    {
        Render = new PdfPageRenderOptions { Dpi = 300 },
    });

await orchestrator.SavePageAsync(
    "input.pdf",
    pageIndex: 1,
    "page-2.webp",
    new PdfImageConversionOptions
    {
        Render = new PdfPageRenderOptions { Dpi = 144 },
        Format = PdfImageOutputFormat.Webp,
    });
```

`RenderPageAsync` returns a caller-owned `PdfBitmap`. `SavePageAsync` can write to a path or caller-owned output stream;
output streams are never closed by the orchestrator. Rendered bitmaps and encoded images can be large; memory grows
with page dimensions, DPI, scale, and concurrent worker count. `QueueCapacity` bounds request count, not total retained
PDF bytes, so queued byte-array inputs can still retain substantial managed memory.

## Multi-page batches

Use a batch when several pages come from the same PDF. The PDF is transferred once for array/stream inputs, opened once
with `PdfRenderSession`, and processed in order on one worker. `RenderPagesAsync` returns caller-owned bitmaps in the
same order as the requested zero-based indexes; repeated indexes are allowed. `SavePagesAsync` accepts exact
`PdfPageFileOutput` mappings, requires unique output paths, and commits each file through a same-directory temporary
file so an encoding or size-limit failure does not replace an existing destination with a partial file.

```csharp
var bitmaps = await orchestrator.RenderPagesAsync("input.pdf", new[] { 0, 3, 7 });

await orchestrator.SavePagesAsync("input.pdf", new[]
{
    new PdfPageFileOutput(0, "page-0001.webp"),
    new PdfPageFileOutput(3, "page-0004.webp"),
    new PdfPageFileOutput(7, "page-0008.webp"),
}, new PdfImageConversionOptions { Format = PdfImageOutputFormat.Webp });
```

A batch occupies one worker until it finishes and pages within that batch are sequential. For a large export, split
the page list into moderate batches and submit those batches together; separate workers can then run them in parallel
while each batch still avoids repeated PDF transfer and parsing. If a later file in a save batch fails, files already
committed by earlier items remain. The library does not retry the batch.

## Resource-limit failures

`PdfRenderResourceLimitException` reports `Resource`, `Limit`, and `Observed`. Known byte-array and seekable-stream
lengths can be rejected during submission; non-seekable streams and worker-generated output are rejected when the
limit is crossed. A stream output may already contain bytes at that point, so callers that require transactional
stream output should write to their own staging stream. Path outputs are staged atomically per file.
The stable `Resource` values are `input bytes`, `bitmap bytes`, and `output bytes`.

## Lifetime and shutdown

`CompleteAsync()` stops accepting submissions, drains accepted work, and shuts workers down. `CancelAsync()` cancels
queued work, waits for active uninterruptible work, and stops workers. `Dispose()` follows the cancellation path and
blocks until shutdown completes. Do not create an orchestrator per request.

### ASP.NET Core application lifetime

Register one singleton per host process:

```csharp
builder.Services.AddSingleton<PdfRenderOrchestrator>(_ =>
    new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
    {
        WorkerCount = Math.Min(Environment.ProcessorCount, 4),
        QueueCapacity = 100,
        RequestTimeout = TimeSpan.FromSeconds(30),
    }));
builder.Services.AddHostedService<PdfOrchestratorShutdown>();
```

Inject that singleton into controllers and scoped services. Drain it once when the host stops:

```csharp
using PdfiumRaster.Orchestration;

public sealed class PdfOrchestratorShutdown : IHostedService
{
    private readonly PdfRenderOrchestrator _orchestrator;

    public PdfOrchestratorShutdown(PdfRenderOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => _orchestrator.CompleteAsync();
}
```

The dependency injection container disposes the singleton afterward. Each replica has its own singleton, so total
workers equal `WorkerCount` multiplied by the number of application replicas.

## Error handling

- `PdfWorkerStartupException`: a worker could not start or finish its handshake.
- `PdfWorkerCrashedException`: a worker exited during an active request; inspect `ExitCode` and `StandardError`.
- `PdfWorkerTimeoutException`: the dispatched request exceeded `RequestTimeout`.
- `PdfRenderResourceLimitException`: configured input, bitmap, or total-output bytes were exceeded; inspect `Resource`,
  `Limit`, and `Observed`.
- `PdfWorkerRemoteException`: a healthy worker reported a validation, rendering, or encoding error; inspect
  `RemoteExceptionType`.
- `PdfWorkerProtocolException`: malformed or incompatible pipe communication.
- `PdfRenderQueueFullException`: a submission was rejected because the bounded queue was full.

After a crash, timeout, or protocol failure, the orchestrator replaces the affected worker. The failed request is
never retried automatically because rendering to a path or stream may have observable partial output.

## Diagnostics

The internal EventSource provider `PdfiumRaster-Orchestrator` reports request timing, worker process IDs, failures,
timeouts, and replacement attempts without emitting paths, passwords, tokens, pipe names, or payload data. See
[diagnostic events](TROUBLESHOOTING.md#diagnostic-events) for collection instructions and event semantics.

## Supported worker platforms

Packaged self-contained workers support `win-x86`, `win-x64`, `win-arm64`, `linux-arm`, `linux-x64`, `linux-arm64`,
`linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, and `osx-arm64`. The package does not support `linux-x86` or
`linux-musl-x86`.

### Worker package choices

`PdfiumRaster.Orchestrator` is the backward-compatible all-runtime package and contains all ten workers. Applications
that deploy to several RIDs from one restored dependency graph can keep using it. A slim package named
`PdfiumRaster.Orchestrator.<rid>` contains the same client library, XML documentation, build target, and dependencies,
but only that RID's worker. For example:

```bash
dotnet add package PdfiumRaster.Orchestrator.linux-x64
dotnet publish -r linux-x64
```

Choose exactly one orchestrator package. Do not reference the all-runtime and slim packages together, or multiple slim
packages together, because they contain the same client assembly and build target. When using a slim package, the
project's `RuntimeIdentifier` or publish `-r` value must match the package suffix.
