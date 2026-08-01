using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;

namespace PdfiumRaster.Orchestration;

[EventSource(Name = "PdfiumRaster-Orchestrator")]
internal sealed class PdfRenderOrchestratorEventSource : EventSource
{
    internal static readonly PdfRenderOrchestratorEventSource Log = new();

    private PdfRenderOrchestratorEventSource()
    {
    }

    [Event(1, Level = EventLevel.Informational)]
    public void OrchestratorStarted(int workerCount, int queueCapacity)
    {
        if (IsEnabled())
        {
            WriteEvent(1, workerCount, queueCapacity);
        }
    }

    [Event(2, Level = EventLevel.Verbose)]
    public void RequestSubmitted(long requestId, int operationKind)
    {
        if (IsEnabled())
        {
            WriteEvent(2, requestId, operationKind);
        }
    }

    [Event(3, Level = EventLevel.Informational)]
    public void RequestStarted(long requestId, int workerIndex, double submissionDelayMilliseconds)
    {
        if (IsEnabled())
        {
            WriteRequestTimingEvent(3, requestId, workerIndex, submissionDelayMilliseconds);
        }
    }

    [Event(4, Level = EventLevel.Informational)]
    public void RequestCompleted(long requestId, int workerIndex, double executionMilliseconds)
    {
        if (IsEnabled())
        {
            WriteRequestTimingEvent(4, requestId, workerIndex, executionMilliseconds);
        }
    }

    [Event(5, Level = EventLevel.Warning)]
    public void RequestFailed(long requestId, int workerIndex, string exceptionType, double executionMilliseconds)
    {
        if (IsEnabled())
        {
            WriteRequestFailureEvent(5, requestId, workerIndex, exceptionType, executionMilliseconds);
        }
    }

    [Event(6, Level = EventLevel.Informational)]
    public void RequestCanceled(long requestId, int workerIndex, double executionMilliseconds)
    {
        if (IsEnabled())
        {
            WriteRequestTimingEvent(6, requestId, workerIndex, executionMilliseconds);
        }
    }

    [Event(7, Level = EventLevel.Informational)]
    public void WorkerStarted(int workerIndex, int processId)
    {
        if (IsEnabled())
        {
            WriteEvent(7, workerIndex, processId);
        }
    }

    [Event(8, Level = EventLevel.Warning)]
    public void WorkerRestarting(int workerIndex, int attempt, long delayMilliseconds, string reasonType)
    {
        if (IsEnabled())
        {
            WriteWorkerRestartEvent(8, workerIndex, attempt, delayMilliseconds, reasonType);
        }
    }

    [Event(9, Level = EventLevel.Informational)]
    public void WorkerStopped(int workerIndex, int processId)
    {
        if (IsEnabled())
        {
            WriteEvent(9, workerIndex, processId);
        }
    }

    [Event(10, Level = EventLevel.Error)]
    public void WorkerStartFailed(int workerIndex, string exceptionType)
    {
        if (IsEnabled())
        {
            WriteEvent(10, workerIndex, exceptionType);
        }
    }

    [Event(11, Level = EventLevel.Error)]
    public void OrchestratorFaulted(string exceptionType)
    {
        if (IsEnabled())
        {
            WriteEvent(11, exceptionType);
        }
    }

    [Event(12, Level = EventLevel.Informational)]
    public void OrchestratorStopping(bool cancel)
    {
        if (IsEnabled())
        {
            WriteEvent(12, cancel ? 1 : 0);
        }
    }

    [NonEvent]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The EventData payload contains only explicitly described primitive values.")]
    private unsafe void WriteRequestTimingEvent(
        int eventId,
        long requestId,
        int workerIndex,
        double milliseconds)
    {
        EventData* data = stackalloc EventData[3];
        data[0].DataPointer = (IntPtr)(&requestId);
        data[0].Size = sizeof(long);
        data[1].DataPointer = (IntPtr)(&workerIndex);
        data[1].Size = sizeof(int);
        data[2].DataPointer = (IntPtr)(&milliseconds);
        data[2].Size = sizeof(double);
        WriteEventCore(eventId, 3, data);
    }

    [NonEvent]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The EventData payload contains only explicitly described primitives and one string.")]
    private unsafe void WriteRequestFailureEvent(
        int eventId,
        long requestId,
        int workerIndex,
        string exceptionType,
        double milliseconds)
    {
        fixed (char* exceptionTypePointer = exceptionType)
        {
            EventData* data = stackalloc EventData[4];
            data[0].DataPointer = (IntPtr)(&requestId);
            data[0].Size = sizeof(long);
            data[1].DataPointer = (IntPtr)(&workerIndex);
            data[1].Size = sizeof(int);
            data[2].DataPointer = (IntPtr)exceptionTypePointer;
            data[2].Size = checked((exceptionType.Length + 1) * sizeof(char));
            data[3].DataPointer = (IntPtr)(&milliseconds);
            data[3].Size = sizeof(double);
            WriteEventCore(eventId, 4, data);
        }
    }

    [NonEvent]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The EventData payload contains only explicitly described primitives and one string.")]
    private unsafe void WriteWorkerRestartEvent(
        int eventId,
        int workerIndex,
        int attempt,
        long delayMilliseconds,
        string reasonType)
    {
        fixed (char* reasonTypePointer = reasonType)
        {
            EventData* data = stackalloc EventData[4];
            data[0].DataPointer = (IntPtr)(&workerIndex);
            data[0].Size = sizeof(int);
            data[1].DataPointer = (IntPtr)(&attempt);
            data[1].Size = sizeof(int);
            data[2].DataPointer = (IntPtr)(&delayMilliseconds);
            data[2].Size = sizeof(long);
            data[3].DataPointer = (IntPtr)reasonTypePointer;
            data[3].Size = checked((reasonType.Length + 1) * sizeof(char));
            WriteEventCore(eventId, 4, data);
        }
    }

    [NonEvent]
    internal static double ElapsedMilliseconds(long startedTimestamp)
    {
        return (Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency;
    }

    [NonEvent]
    internal static string ExceptionType(Exception exception)
    {
        return exception.GetType().FullName ?? exception.GetType().Name;
    }
}
