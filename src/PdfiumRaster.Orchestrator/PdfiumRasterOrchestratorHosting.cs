using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace PdfiumRaster.Orchestration;

internal sealed class PdfiumRasterOrchestratorHostedService : IHostedService
{
    private readonly PdfRenderOrchestrator _orchestrator;

    public PdfiumRasterOrchestratorHostedService(PdfRenderOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _orchestrator.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((PdfRenderOrchestrator)state!).CancelAsync(),
            _orchestrator);
        await _orchestrator.CompleteAsync().ConfigureAwait(false);
    }
}

internal sealed class PdfiumRasterOrchestratorHealthCheck : IHealthCheck
{
    private readonly PdfRenderOrchestrator _orchestrator;

    internal PdfiumRasterOrchestratorHealthCheck(PdfRenderOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _orchestrator.GetStatus();
        var data = new Dictionary<string, object>
        {
            ["available_workers"] = snapshot.AvailableWorkerCount,
            ["total_workers"] = snapshot.WorkerCount,
        };

        var result = snapshot.State switch
        {
            PdfRenderOrchestratorState.Healthy => HealthCheckResult.Healthy(
                "The orchestrator is accepting requests and all workers are available.", data),
            PdfRenderOrchestratorState.Starting => HealthCheckResult.Degraded(
                $"The orchestrator is starting with {snapshot.AvailableWorkerCount} of " +
                $"{snapshot.WorkerCount} workers available.",
                data: data),
            PdfRenderOrchestratorState.Degraded => HealthCheckResult.Degraded(
                $"The orchestrator is accepting requests with {snapshot.AvailableWorkerCount} of " +
                $"{snapshot.WorkerCount} workers available; worker replacement may be in progress.",
                data: data),
            PdfRenderOrchestratorState.Faulted => HealthCheckResult.Unhealthy(
                "The orchestrator has encountered a terminal worker failure.", data: data),
            _ => HealthCheckResult.Unhealthy(
                "The orchestrator is stopping or has stopped accepting requests.", data: data),
        };

        return Task.FromResult(result);
    }
}
