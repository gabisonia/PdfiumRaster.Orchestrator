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
        var snapshot = _orchestrator.GetHealthSnapshot();
        var data = new Dictionary<string, object>
        {
            ["available_workers"] = snapshot.AvailableWorkers,
            ["total_workers"] = snapshot.TotalWorkers,
        };

        var result = snapshot.State switch
        {
            PdfRenderOrchestratorHealthState.Healthy => HealthCheckResult.Healthy(
                "The orchestrator is accepting requests and all workers are available.", data),
            PdfRenderOrchestratorHealthState.Starting => HealthCheckResult.Degraded(
                $"The orchestrator is starting with {snapshot.AvailableWorkers} of " +
                $"{snapshot.TotalWorkers} workers available.",
                data: data),
            PdfRenderOrchestratorHealthState.Degraded => HealthCheckResult.Degraded(
                $"The orchestrator is accepting requests with {snapshot.AvailableWorkers} of " +
                $"{snapshot.TotalWorkers} workers available; worker replacement may be in progress.",
                data: data),
            PdfRenderOrchestratorHealthState.TerminalFailure => HealthCheckResult.Unhealthy(
                "The orchestrator has encountered a terminal worker failure.", data: data),
            _ => HealthCheckResult.Unhealthy(
                "The orchestrator is stopping or has stopped accepting requests.", data: data),
        };

        return Task.FromResult(result);
    }
}

internal enum PdfRenderOrchestratorHealthState
{
    Healthy,
    Starting,
    Degraded,
    Stopped,
    TerminalFailure,
}

internal readonly struct PdfRenderOrchestratorHealthSnapshot
{
    internal PdfRenderOrchestratorHealthSnapshot(
        PdfRenderOrchestratorHealthState state,
        int availableWorkers,
        int totalWorkers)
    {
        State = state;
        AvailableWorkers = availableWorkers;
        TotalWorkers = totalWorkers;
    }

    internal PdfRenderOrchestratorHealthState State { get; }

    internal int AvailableWorkers { get; }

    internal int TotalWorkers { get; }
}
