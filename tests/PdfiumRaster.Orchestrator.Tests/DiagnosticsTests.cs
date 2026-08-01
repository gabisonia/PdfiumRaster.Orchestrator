using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace PdfiumRaster.Orchestration.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    private const string WorkerPathVariable = "PDFIUMRASTER_WORKER_PATH";
    private readonly string? _originalWorkerPath;

    public DiagnosticsTests()
    {
        _originalWorkerPath = Environment.GetEnvironmentVariable(WorkerPathVariable);
        var workerPath = typeof(DiagnosticsTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PdfiumRasterWorkerPath")
            .Value;
        Environment.SetEnvironmentVariable(WorkerPathVariable, workerPath);
    }

    [Fact]
    public async Task EventSourceReportsLifecycleWithoutSensitiveValues()
    {
        const string password = "diagnostic-secret-password";
        var pdfPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "smoke.pdf");
        using var listener = new OrchestratorEventListener();
        using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 1,
        });

        await orchestrator.RenderPageAsync(pdfPath, pageIndex: 0, password: password);
        await orchestrator.CompleteAsync();

        var events = listener.Events.ToArray();
        Assert.Contains(events, item => item.Name == "OrchestratorStarted");
        Assert.Contains(events, item => item.Name == "WorkerStarted");
        Assert.Contains(events, item => item.Name == "RequestSubmitted");
        Assert.Contains(events, item => item.Name == "RequestStarted");
        Assert.Contains(events, item => item.Name == "RequestCompleted");
        Assert.Contains(events, item => item.Name == "OrchestratorStopping");
        Assert.Contains(events, item => item.Name == "WorkerStopped");
        Assert.DoesNotContain(events, item => item.Payload.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.Payload.Contains(pdfPath, StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.Name == "EventSourceMessage");
    }

    [Fact]
    public void EveryDiagnosticEventHasStableIdentityLevelAndPayloadShape()
    {
        using var listener = new OrchestratorEventListener();
        var log = PdfRenderOrchestratorEventSource.Log;

        log.OrchestratorStarted(workerCount: 2, queueCapacity: 8);
        log.RequestSubmitted(requestId: 101, operationKind: 1);
        log.RequestStarted(requestId: 101, workerIndex: 0, submissionDelayMilliseconds: 1.5);
        log.RequestCompleted(requestId: 101, workerIndex: 0, executionMilliseconds: 2.5);
        log.RequestFailed(requestId: 102, workerIndex: 1, "Failure.Type", executionMilliseconds: 3.5);
        log.RequestCanceled(requestId: 103, workerIndex: 1, executionMilliseconds: 4.5);
        log.WorkerStarted(workerIndex: 0, processId: 2001);
        log.WorkerRestarting(workerIndex: 0, attempt: 2, delayMilliseconds: 250, "Crash.Type");
        log.WorkerStopped(workerIndex: 0, processId: 2001);
        log.WorkerStartFailed(workerIndex: 1, "Startup.Type");
        log.OrchestratorFaulted("Terminal.Type");
        log.OrchestratorStopping(cancel: true);

        var events = listener.Events
            .Where(item => item.EventId is >= 1 and <= 12)
            .GroupBy(item => item.EventId)
            .Select(group => group.Last())
            .OrderBy(item => item.EventId)
            .ToArray();

        Assert.Equal(Enumerable.Range(1, 12), events.Select(item => item.EventId));
        Assert.Equal(
            new[]
            {
                EventLevel.Informational,
                EventLevel.Verbose,
                EventLevel.Informational,
                EventLevel.Informational,
                EventLevel.Warning,
                EventLevel.Informational,
                EventLevel.Informational,
                EventLevel.Warning,
                EventLevel.Informational,
                EventLevel.Error,
                EventLevel.Error,
                EventLevel.Informational,
            },
            events.Select(item => item.Level));
        Assert.Equal(new[] { "2", "8" }, events[0].Values);
        Assert.Equal(new[] { "101", "1" }, events[1].Values);
        Assert.Equal(new[] { "102", "1", "Failure.Type" }, events[4].Values.Take(3));
        Assert.Equal(3.5, double.Parse(events[4].Values[3], CultureInfo.CurrentCulture));
        Assert.Equal(new[] { "0", "2", "250", "Crash.Type" }, events[7].Values);
        Assert.Equal(new[] { bool.TrueString }, events[11].Values);
        Assert.DoesNotContain(events, item => item.Name == "EventSourceMessage");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkerPathVariable, _originalWorkerPath);
    }

    private sealed class OrchestratorEventListener : EventListener
    {
        internal ConcurrentQueue<CapturedEvent> Events { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "PdfiumRaster-Orchestrator")
            {
                EnableEvents(eventSource, EventLevel.LogAlways, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            var values = eventData.Payload is null
                ? Array.Empty<string>()
                : eventData.Payload.Select(value => value?.ToString() ?? string.Empty).ToArray();
            Events.Enqueue(new CapturedEvent(
                eventData.EventId,
                eventData.EventName ?? string.Empty,
                eventData.Level,
                values));
        }
    }

    private sealed class CapturedEvent
    {
        internal CapturedEvent(int eventId, string name, EventLevel level, string[] values)
        {
            EventId = eventId;
            Name = name;
            Level = level;
            Values = values;
        }

        internal int EventId { get; }
        internal string Name { get; }
        internal EventLevel Level { get; }
        internal string[] Values { get; }
        internal string Payload => string.Join("|", Values);
    }
}
