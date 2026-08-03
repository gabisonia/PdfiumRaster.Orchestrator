using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PdfiumRaster.Orchestration;

internal static class PdfRenderOrchestratorTelemetry
{
    private static long _queueSize;
    private static long _activeRequestCount;
    private static long _activeWorkerCount;
    private static readonly ActivitySource ActivitySource =
        new(PdfRenderOrchestratorDiagnostics.ActivitySourceName);
    private static readonly Meter Meter = new(PdfRenderOrchestratorDiagnostics.MeterName);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "pdfiumraster.orchestrator.requests",
        "{request}",
        "PDF orchestration requests grouped by operation and terminal outcome.");
    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "pdfiumraster.orchestrator.request.duration",
        "s",
        "Elapsed time from submission through completion.");
    private static readonly Histogram<double> QueueDuration = Meter.CreateHistogram<double>(
        "pdfiumraster.orchestrator.queue.duration",
        "s",
        "Time an accepted request waited for a worker.");
    private static readonly ObservableGauge<long> QueueSize = Meter.CreateObservableGauge(
        "pdfiumraster.orchestrator.queue.size",
        () => Interlocked.Read(ref _queueSize),
        "{request}",
        "Requests waiting for queue capacity or an available worker.");
    private static readonly ObservableGauge<long> ActiveRequests = Meter.CreateObservableGauge(
        "pdfiumraster.orchestrator.requests.active",
        () => Interlocked.Read(ref _activeRequestCount),
        "{request}",
        "Requests currently assigned to workers.");
    private static readonly ObservableGauge<long> ActiveWorkers = Meter.CreateObservableGauge(
        "pdfiumraster.orchestrator.workers.active",
        () => Interlocked.Read(ref _activeWorkerCount),
        "{worker}",
        "Connected PDFium workers.");
    private static readonly Counter<long> WorkerRestarts = Meter.CreateCounter<long>(
        "pdfiumraster.orchestrator.worker.restarts",
        "{attempt}",
        "Worker replacement attempts.");
    private static readonly Counter<long> QueueRejections = Meter.CreateCounter<long>(
        "pdfiumraster.orchestrator.queue.rejections",
        "{request}",
        "Requests rejected because the bounded queue was full.");

    internal static string OperationName(int operationKind)
    {
        return operationKind switch
        {
            1 => "render",
            2 => "save",
            3 => "render_batch",
            4 => "save_batch",
            5 => "get_page_count",
            6 => "get_page_sizes",
            _ => "unknown",
        };
    }

    internal static Activity? StartRequest(
        long requestId,
        int operationKind,
        int pageCount,
        int? workerIndex,
        ActivityContext parentContext,
        DateTimeOffset submittedAt,
        double queueDurationMilliseconds)
    {
        if (!ActivitySource.HasListeners())
        {
            return null;
        }

        var operation = OperationName(operationKind);
        var tags = new ActivityTagsCollection
        {
            { "pdfiumraster.orchestrator.request_id", requestId },
            { "pdfiumraster.orchestrator.operation", operation },
            { "pdfiumraster.orchestrator.page_count", pageCount },
            { "pdfiumraster.orchestrator.queue.duration", queueDurationMilliseconds / 1000d },
        };
        if (workerIndex.HasValue)
        {
            tags.Add("pdfiumraster.orchestrator.worker.index", workerIndex.Value);
        }

        return ActivitySource.StartActivity(
            $"PdfiumRaster.Orchestrator {operation}",
            ActivityKind.Internal,
            parentContext,
            tags,
            links: null,
            startTime: submittedAt);
    }

    internal static void RequestQueued()
    {
        Interlocked.Increment(ref _queueSize);
    }

    internal static void RequestDequeued(int operationKind, double queueDurationMilliseconds)
    {
        Interlocked.Decrement(ref _queueSize);
        Interlocked.Increment(ref _activeRequestCount);
        QueueDuration.Record(
            queueDurationMilliseconds / 1000d,
            new KeyValuePair<string, object?>("operation", OperationName(operationKind)));
    }

    internal static void RequestRemovedWithoutExecution()
    {
        Interlocked.Decrement(ref _queueSize);
    }

    internal static void RequestFinished(
        Activity? activity,
        int operationKind,
        string outcome,
        string? exceptionType,
        double totalDurationMilliseconds,
        bool wasActive)
    {
        if (wasActive)
        {
            Interlocked.Decrement(ref _activeRequestCount);
        }

        var tags = new TagList
        {
            { "operation", OperationName(operationKind) },
            { "outcome", outcome },
        };
        Requests.Add(1, tags);
        RequestDuration.Record(totalDurationMilliseconds / 1000d, tags);

        if (activity is not null)
        {
            activity.SetTag("pdfiumraster.orchestrator.outcome", outcome);
            if (exceptionType is not null)
            {
                activity.SetTag("error.type", exceptionType);
                activity.SetStatus(ActivityStatusCode.Error);
            }

            activity.Dispose();
        }
    }

    internal static void RequestRejected()
    {
        QueueRejections.Add(1);
    }

    internal static void WorkerStarted()
    {
        Interlocked.Increment(ref _activeWorkerCount);
    }

    internal static void WorkerStopped()
    {
        Interlocked.Decrement(ref _activeWorkerCount);
    }

    internal static void WorkerRestarted(Exception reason)
    {
        var reasonKind = reason switch
        {
            PdfWorkerTimeoutException => "timeout",
            PdfWorkerCrashedException => "crash",
            PdfWorkerProtocolException => "protocol",
            PdfWorkerStartupException => "startup",
            _ => "other",
        };
        WorkerRestarts.Add(1, new KeyValuePair<string, object?>("reason", reasonKind));
    }
}
