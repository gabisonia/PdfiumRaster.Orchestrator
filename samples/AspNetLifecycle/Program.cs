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
builder.Services.AddPdfiumRasterOrchestrator(options =>
{
    options.WorkerCount = Math.Min(Environment.ProcessorCount, 4);
    options.QueueCapacity = 100;
    options.RequestTimeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHealthChecks()
    .AddPdfiumRasterOrchestrator(tags: new[] { "ready" });

var app = builder.Build();
app.MapGet("/", () => "PdfiumRaster.Orchestrator is ready.");
app.MapHealthChecks("/health/ready");
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
