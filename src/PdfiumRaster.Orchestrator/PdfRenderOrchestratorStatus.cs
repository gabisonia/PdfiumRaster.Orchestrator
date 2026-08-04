namespace PdfiumRaster.Orchestration;

/// <summary>
/// Describes the current lifecycle and worker availability of a PDF render orchestrator.
/// </summary>
public sealed class PdfRenderOrchestratorStatus
{
    internal PdfRenderOrchestratorStatus(
        PdfRenderOrchestratorState state,
        int availableWorkerCount,
        int workerCount)
    {
        State = state;
        AvailableWorkerCount = availableWorkerCount;
        WorkerCount = workerCount;
    }

    /// <summary>
    /// Gets the current orchestrator lifecycle state.
    /// </summary>
    public PdfRenderOrchestratorState State { get; }

    /// <summary>
    /// Gets the number of workers currently available to accept requests.
    /// </summary>
    public int AvailableWorkerCount { get; }

    /// <summary>
    /// Gets the configured number of workers.
    /// </summary>
    public int WorkerCount { get; }
}

/// <summary>
/// Identifies the current lifecycle and availability state of a PDF render orchestrator.
/// </summary>
public enum PdfRenderOrchestratorState
{
    /// <summary>The worker pool has not finished starting.</summary>
    Starting,

    /// <summary>The orchestrator is accepting requests and every configured worker is available.</summary>
    Healthy,

    /// <summary>The orchestrator is accepting requests while one or more workers are unavailable.</summary>
    Degraded,

    /// <summary>The orchestrator encountered a terminal worker failure and cannot accept requests.</summary>
    Faulted,

    /// <summary>The orchestrator is draining or canceling accepted work.</summary>
    Stopping,

    /// <summary>The orchestrator and its workers have stopped.</summary>
    Stopped,
}
