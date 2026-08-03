# API guide

Install an orchestrator package. Its dependency brings in a compatible
[`PdfiumRaster`](https://www.nuget.org/packages/PdfiumRaster) version automatically.

```bash
dotnet add package PdfiumRaster.Orchestrator
```

## Creating an orchestrator

Create one long-lived `PdfRenderOrchestrator` for each application process. Prefer cancellable asynchronous creation
when the application has an asynchronous startup path:

```csharp
using PdfiumRaster.Orchestration;

await using var orchestrator = await PdfRenderOrchestrator.CreateAsync(
    cancellationToken: startupCancellationToken);
```

`CreateAsync` completes only after every worker has connected and finished its handshake. Cancellation stops and
cleans up partially started workers. `new PdfRenderOrchestrator()` remains available for synchronous callers and also
returns only after all workers are ready. In Generic Host and ASP.NET Core applications, use the hosting integration;
it performs asynchronous startup automatically.

### Default options

| Option | Default |
| --- | --- |
| `WorkerCount` | The smaller of `4` and `Environment.ProcessorCount`, with a minimum of `1` |
| `QueueCapacity` | `42` waiting requests |
| `QueueFullMode` | `PdfRenderQueueFullMode.Wait` |
| `RequestTimeout` | `null` (hard request timeouts disabled) |
| `WorkerStartupTimeout` | 15 seconds |
| `WorkerRestartDelays` | 250 milliseconds, 1 second, then 4 seconds |
| `MaximumBatchPages` | `256` pages |
| `MaximumInputBytes` | `null` (unlimited) |
| `MaximumBitmapBytes` | `null` (unlimited) |
| `MaximumOutputBytes` | `null` (unlimited) |
| `TemporaryDirectory` | `null` (use the operating-system temporary directory) |
| `LoggerFactory` | `NullLoggerFactory.Instance` (structured logging disabled) |

Pass `PdfRenderOrchestratorOptions` to override only the values the application needs. For example, this is a custom
configuration rather than a representation of the defaults:

```csharp
using PdfiumRaster.Orchestration;

await using var orchestrator = await PdfRenderOrchestrator.CreateAsync(new PdfRenderOrchestratorOptions
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

## Document inspection

Use the worker-isolated inspection APIs when the application needs document metadata before choosing pages to render:

```csharp
var pageCount = await orchestrator.GetPageCountAsync("input.pdf");
var pageSizes = await orchestrator.GetPageSizesAsync("input.pdf");

for (var pageIndex = 0; pageIndex < pageSizes.Count; pageIndex++)
{
    var size = pageSizes[pageIndex];
    Console.WriteLine($"Page {pageIndex}: {size.Width} x {size.Height} points");
}
```

`GetPageCountAsync` and `GetPageSizesAsync` each have path, `byte[]`, and `Stream` overloads. Sizes use PDF points
(`1/72` inch) and the returned list follows zero-based page order. Stream inputs are read from their current position;
the orchestrator disposes them after success, failure, cancellation, validation failure, or queue rejection unless
`leaveOpen: true` is passed. Byte arrays remain caller-owned and must not be modified before completion.

Inspection is scheduled through the same bounded worker pool as rendering. It observes `QueueFullMode`,
`RequestTimeout`, `MaximumInputBytes`, cancellation, worker replacement, and standard diagnostics. It does not render
pages, so bitmap and encoded-output limits do not apply. Path inputs remain preferable for large documents; byte and
stream inputs cross the pipe and are spooled by the worker. Failed inspections are not retried automatically.

## Rendering

Page indexes are zero-based. Path, `byte[]`, and `Stream` PDF inputs are accepted. Prefer paths for large documents.
Byte arrays and streams must cross the named pipe and are spooled to a worker-owned, owner-only temporary directory.
Input streams are read from their current position. Unless `leaveOpen: true` is passed, task-returning APIs assume
ownership when the method is called and dispose the input after completion, cancellation, validation failure, or queue
rejection. `RenderPagesStreamAsync` is lazy: validation, submission, and stream ownership begin when enumeration
starts; an enumerable that is never enumerated does not take ownership.

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
same order as the requested zero-based indexes; repeated indexes are allowed. It retains all bitmap pixel arrays until
the full batch completes. `SavePagesAsync` accepts exact `PdfPageFileOutput` mappings, requires unique output paths,
and commits each file through a same-directory temporary file so an encoding or size-limit failure does not replace an
existing destination with a partial file.

```csharp
var bitmaps = await orchestrator.RenderPagesAsync("input.pdf", new[] { 0, 3, 7 });

await orchestrator.SavePagesAsync("input.pdf", new[]
{
    new PdfPageFileOutput(0, "page-0001.webp"),
    new PdfPageFileOutput(3, "page-0004.webp"),
    new PdfPageFileOutput(7, "page-0008.webp"),
}, new PdfImageConversionOptions { Format = PdfImageOutputFormat.Webp });
```

For a large raw-bitmap batch, stream results instead of retaining the full list:

```csharp
await foreach (var page in orchestrator.RenderPagesStreamAsync(
                   "input.pdf",
                   new[] { 0, 3, 7 },
                   cancellationToken: cancellationToken))
{
    // Position identifies this item in the request, including duplicate page indexes.
    await ProcessBitmapAsync(page.Position, page.PageIndex, page.Bitmap, cancellationToken);
}
```

`RenderPagesStreamAsync` supports path, `byte[]`, and `Stream` inputs and yields caller-owned `PdfPageBitmap` values in
request order. The orchestrator keeps a capacity-one channel of completed pages between its pipe reader and the
consumer. This applies consumer backpressure and avoids retaining every page, although the current page's pixel array,
one buffered result, and any bitmap still retained by the caller can coexist. Consume or release each bitmap promptly
to preserve the bounded-memory benefit.

The cancellation token controls queue admission and the entire enumeration. Ending enumeration early also aborts the
request. If the request is already active, either action kills and replaces that worker because unread frames from a
partially consumed batch would otherwise make its persistent pipe unsafe to reuse. A request canceled while queued is
removed without replacing a worker. The request is not retried. Enumeration does not complete until any required abort
and replacement attempt have finished, so a subsequent request never receives stale batch frames.

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
queued work, waits for active uninterruptible work, and stops workers. `Dispose()` and `DisposeAsync()` follow the
cancellation path; prefer asynchronous disposal when the calling lifetime supports it. Do not create an orchestrator
per request. Graceful completion also waits for accepted streaming enumerations; consumers must keep reading, cancel,
or dispose an unfinished enumerator.

### .NET hosting and health checks

For a .NET Generic Host or ASP.NET Core application, use the built-in registration rather than manually creating a
singleton and shutdown service:

```csharp
builder.Services.AddPdfiumRasterOrchestrator(options =>
{
    options.WorkerCount = Math.Min(Environment.ProcessorCount, 4);
    options.QueueCapacity = 100;
    options.RequestTimeout = TimeSpan.FromSeconds(30);
});
```

The extension registers one `PdfRenderOrchestrator` singleton and one internal `IHostedService`. The host's
`ILoggerFactory` is assigned before the configuration callback runs, so an application can still replace it in the
callback. The hosted service asynchronously starts and handshakes all workers from `IHostedService.StartAsync`; the
host startup token cancels partial startup. Requests made after singleton resolution but before host startup finishes
are rejected. Normal shutdown calls `CompleteAsync()` to drain accepted work; if the host shutdown token is canceled,
the integration switches to `CancelAsync()` so queued work is canceled. Repeated registration calls retain the first
singleton configuration and do not duplicate the hosted service.

Add the optional readiness check and map it in ASP.NET Core:

```csharp
builder.Services.AddHealthChecks()
    .AddPdfiumRasterOrchestrator(tags: new[] { "ready" });

var app = builder.Build();
app.MapHealthChecks("/health/ready");
```

The default health-check name is `pdfiumraster-orchestrator`; the name, exception failure status, and tags are
configurable. Its status is:

- `Healthy` when the orchestrator accepts requests and every configured worker is available.
- `Degraded` during initial hosted startup or while one or more workers are unavailable or being replaced.
- `Unhealthy` after a terminal worker failure or when completion, cancellation, or disposal starts.

The check reads in-memory lifecycle state. It does not render a document, write files, communicate over the worker
pipe, or create a separate probe worker. Inject the singleton into controllers and scoped services, but do not call
`CompleteAsync()` from request code. Each replica has its own singleton, so total workers equal `WorkerCount`
multiplied by the number of application replicas.

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

Set `PdfRenderOrchestratorOptions.LoggerFactory` to integrate structured lifecycle logs with the application's normal
`Microsoft.Extensions.Logging` providers. Trace logs cover individual requests, informational logs cover orchestrator
and worker lifecycle, warnings cover timeouts, queue rejection, and worker replacement, and terminal orchestrator
faults are errors. Logging is disabled by default.

Every request also emits an internal activity through
`PdfRenderOrchestratorDiagnostics.ActivitySourceName`. The activity inherits the caller's `Activity.Current` context,
covers queueing and execution, and records operation, page count, queue duration, worker index, outcome, and error type
when applicable. Operational metrics are emitted through `PdfRenderOrchestratorDiagnostics.MeterName`:

The bounded `operation` value is `render`, `save`, `render_batch`, `save_batch`, `get_page_count`, or
`get_page_sizes`.

| Instrument | Type | Unit | Tags |
| --- | --- | --- | --- |
| `pdfiumraster.orchestrator.requests` | Counter | `{request}` | `operation`, `outcome` |
| `pdfiumraster.orchestrator.request.duration` | Histogram | `s` | `operation`, `outcome` |
| `pdfiumraster.orchestrator.queue.duration` | Histogram | `s` | `operation` |
| `pdfiumraster.orchestrator.queue.size` | Observable gauge | `{request}` | none |
| `pdfiumraster.orchestrator.requests.active` | Observable gauge | `{request}` | none |
| `pdfiumraster.orchestrator.workers.active` | Observable gauge | `{worker}` | none |
| `pdfiumraster.orchestrator.worker.restarts` | Counter | `{attempt}` | bounded `reason` |
| `pdfiumraster.orchestrator.queue.rejections` | Counter | `{request}` | none |

`queue.size` includes submissions waiting for queue capacity in `Wait` mode as well as accepted requests waiting for
an available worker. This makes admission pressure visible even when producers are being asynchronously throttled.

Configure OpenTelemetry with the public names rather than duplicating string literals:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(
        PdfRenderOrchestratorDiagnostics.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(
        PdfRenderOrchestratorDiagnostics.MeterName));
```

Telemetry excludes PDF and image paths, passwords, pipe names, handshake tokens, worker standard error, document
bytes, and encoded output. Request IDs and worker process IDs appear only in logs, traces, and `EventSource` events;
they are never metric tags. The internal `EventSource` provider `PdfiumRaster-Orchestrator` remains available for
`dotnet-trace`; see [diagnostic events](TROUBLESHOOTING.md#diagnostic-events) for its collection instructions and
schema.

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
