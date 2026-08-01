using PdfiumRaster.Orchestration;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<PdfRenderOrchestrator>(_ =>
    new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
    {
        WorkerCount = Math.Min(Environment.ProcessorCount, 4),
        QueueCapacity = 100,
        RequestTimeout = TimeSpan.FromSeconds(30),
    }));
builder.Services.AddHostedService<PdfOrchestratorShutdown>();

var app = builder.Build();
app.MapGet("/", () => "PdfiumRaster.Orchestrator is ready.");
await app.RunAsync();

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
