# API guide

Install only the orchestrator package. Its dependency brings in a compatible
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
});
```

`WorkerCount` is the number of independent PDFium processes and may not exceed the logical processor count.
`QueueCapacity` bounds accepted work waiting for a worker. `Wait` asynchronously applies backpressure; `Reject` throws
`PdfRenderQueueFullException`. `RequestTimeout` measures active worker processing and excludes queue time. A timeout
terminates and replaces that worker.

## Rendering

Page indexes are zero-based. Path, `byte[]`, and `Stream` PDF inputs are accepted. Prefer paths for large documents.
Byte arrays and streams must cross the named pipe and are spooled to a worker-owned temporary file. Input streams are
read from their current position and remain owned by the caller.

```csharp
using PdfiumRaster;
using PdfiumRaster.Orchestration;

var bitmap = await orchestrator.RenderPageAsync(
    "input.pdf",
    pageIndex: 0,
    PdfPageRenderOptions.Print);

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

`RenderPageAsync` returns a caller-owned `PdfBitmap`. `SavePageAsync` can write to a path or caller-owned stream and
does not close caller streams. Rendered bitmaps and encoded images can be large; memory grows with page dimensions,
DPI, scale, and concurrent worker count.

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
- `PdfWorkerTimeoutException`: active processing exceeded `RequestTimeout`.
- `PdfWorkerRemoteException`: a healthy worker reported a validation, rendering, or encoding error; inspect
  `RemoteExceptionType`.
- `PdfWorkerProtocolException`: malformed or incompatible pipe communication.
- `PdfRenderQueueFullException`: a submission was rejected because the bounded queue was full.

After a crash, timeout, or protocol failure, the orchestrator replaces the affected worker. The failed request is
never retried automatically because rendering to a path or stream may have observable partial output.

## Supported worker platforms

Packaged self-contained workers support `win-x86`, `win-x64`, `win-arm64`, `linux-arm`, `linux-x64`, `linux-arm64`,
`linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, and `osx-arm64`. The package does not support `linux-x86` or
`linux-musl-x86`.
