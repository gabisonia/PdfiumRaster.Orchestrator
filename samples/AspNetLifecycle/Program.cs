using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using PdfiumRaster.Orchestration;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource(PdfRenderOrchestratorDiagnostics.ActivitySourceName)
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(PdfRenderOrchestratorDiagnostics.MeterName)
        .AddConsoleExporter());
builder.Services.AddSingleton<PdfRenderOrchestrator>(services =>
    new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
    {
        WorkerCount = Math.Min(Environment.ProcessorCount, 4),
        QueueCapacity = 100,
        RequestTimeout = TimeSpan.FromSeconds(30),
        LoggerFactory = services.GetRequiredService<ILoggerFactory>(),
    }));
builder.Services.AddHostedService<PdfOrchestratorShutdown>();

var app = builder.Build();
app.MapGet("/", () => "PdfiumRaster.Orchestrator is ready.");
app.MapPost("/render", async (
    RenderRequest request,
    PdfRenderOrchestrator orchestrator,
    CancellationToken cancellationToken) =>
{
    var bitmap = await orchestrator.RenderPageAsync(
        request.PdfPath,
        request.PageIndex,
        cancellationToken: cancellationToken);
    return Results.Ok(new { bitmap.Width, bitmap.Height, bitmap.Stride });
});
await app.RunAsync();

internal sealed record RenderRequest(string PdfPath, int PageIndex = 0);

internal sealed class PdfOrchestratorShutdown : IHostedService
{
    private readonly PdfRenderOrchestrator _orchestrator;

    public PdfOrchestratorShutdown(PdfRenderOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => _orchestrator.CompleteAsync();
}
