using Microsoft.Extensions.Logging;

namespace PdfiumRaster.Orchestration;

internal static partial class PdfRenderOrchestratorLog
{
    [LoggerMessage(1000, LogLevel.Information,
        "PDFium orchestrator started with {WorkerCount} workers and queue capacity {QueueCapacity}.")]
    internal static partial void OrchestratorStarted(this ILogger logger, int workerCount, int queueCapacity);

    [LoggerMessage(1001, LogLevel.Information,
        "PDFium orchestrator is stopping. Cancel accepted work: {Cancel}.")]
    internal static partial void OrchestratorStopping(this ILogger logger, bool cancel);

    [LoggerMessage(1002, LogLevel.Error,
        "PDFium orchestrator faulted with {ExceptionType}.")]
    internal static partial void OrchestratorFaulted(this ILogger logger, string exceptionType);

    [LoggerMessage(1100, LogLevel.Trace,
        "PDF render request {RequestId} was submitted for operation {Operation}.")]
    internal static partial void RequestSubmitted(this ILogger logger, long requestId, string operation);

    [LoggerMessage(1101, LogLevel.Trace,
        "PDF render request {RequestId} started on worker {WorkerIndex} after {QueueDurationMilliseconds} ms in the queue.")]
    internal static partial void RequestStarted(
        this ILogger logger,
        long requestId,
        int workerIndex,
        double queueDurationMilliseconds);

    [LoggerMessage(1102, LogLevel.Trace,
        "PDF render request {RequestId} completed on worker {WorkerIndex} in {ExecutionDurationMilliseconds} ms.")]
    internal static partial void RequestCompleted(
        this ILogger logger,
        long requestId,
        int workerIndex,
        double executionDurationMilliseconds);

    [LoggerMessage(1103, LogLevel.Debug,
        "PDF render request {RequestId} failed on worker {WorkerIndex} with {ExceptionType} after {ExecutionDurationMilliseconds} ms.")]
    internal static partial void RequestFailed(
        this ILogger logger,
        long requestId,
        int workerIndex,
        string exceptionType,
        double executionDurationMilliseconds);

    [LoggerMessage(1104, LogLevel.Debug,
        "PDF render request {RequestId} was canceled on worker {WorkerIndex} after {ExecutionDurationMilliseconds} ms.")]
    internal static partial void RequestCanceled(
        this ILogger logger,
        long requestId,
        int workerIndex,
        double executionDurationMilliseconds);

    [LoggerMessage(1105, LogLevel.Warning,
        "PDF render request {RequestId} timed out on worker {WorkerIndex} after {ExecutionDurationMilliseconds} ms.")]
    internal static partial void RequestTimedOut(
        this ILogger logger,
        long requestId,
        int workerIndex,
        double executionDurationMilliseconds);

    [LoggerMessage(1106, LogLevel.Warning,
        "PDF render request {RequestId} was rejected because the queue is full.")]
    internal static partial void RequestRejected(this ILogger logger, long requestId);

    [LoggerMessage(1107, LogLevel.Debug,
        "PDF render request {RequestId} was canceled before worker assignment.")]
    internal static partial void RequestCanceledBeforeDispatch(this ILogger logger, long requestId);

    [LoggerMessage(1200, LogLevel.Information,
        "PDFium worker {WorkerIndex} started with process ID {ProcessId}.")]
    internal static partial void WorkerStarted(this ILogger logger, int workerIndex, int processId);

    [LoggerMessage(1201, LogLevel.Information,
        "PDFium worker {WorkerIndex} with process ID {ProcessId} stopped.")]
    internal static partial void WorkerStopped(this ILogger logger, int workerIndex, int processId);

    [LoggerMessage(1202, LogLevel.Warning,
        "PDFium worker {WorkerIndex} replacement attempt {Attempt} will start after {DelayMilliseconds} ms because of {ReasonType}.")]
    internal static partial void WorkerRestarting(
        this ILogger logger,
        int workerIndex,
        int attempt,
        long delayMilliseconds,
        string reasonType);

    [LoggerMessage(1203, LogLevel.Warning,
        "PDFium worker {WorkerIndex} failed to start with {ExceptionType}.")]
    internal static partial void WorkerStartFailed(this ILogger logger, int workerIndex, string exceptionType);
}
