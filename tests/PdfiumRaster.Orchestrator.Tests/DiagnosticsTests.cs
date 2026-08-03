using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Extensions.Logging;

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
    public async Task StandardTelemetryReportsLifecycleWithoutSensitiveValues()
    {
        const string password = "standard-telemetry-secret-password";
        var pdfPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "smoke.pdf");
        var missingPdfPath = Path.Combine(AppContext.BaseDirectory, "standard-telemetry-secret-missing.pdf");
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == PdfRenderOrchestratorDiagnostics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        var activities = new ConcurrentQueue<Activity>();
        activityListener.ActivityStopped = activities.Enqueue;
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener();
        var measurements = new ConcurrentQueue<CapturedMeasurement>();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PdfRenderOrchestratorDiagnostics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            measurements.Enqueue(new CapturedMeasurement(instrument.Name, measurement, tags.ToArray())));
        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            measurements.Enqueue(new CapturedMeasurement(instrument.Name, measurement, tags.ToArray())));
        meterListener.Start();

        using var loggerFactory = new CapturingLoggerFactory();
        using var parent = new Activity("diagnostics-test-parent").Start();
        using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 1,
            LoggerFactory = loggerFactory,
        });
        meterListener.RecordObservableInstruments();

        await orchestrator.RenderPageAsync(pdfPath, pageIndex: 0, password: password);
        var pageCount = await orchestrator.GetPageCountAsync(pdfPath, password: password);
        await Assert.ThrowsAsync<PdfWorkerRemoteException>(
            () => orchestrator.RenderPageAsync(missingPdfPath, pageIndex: 0, password: password));
        await orchestrator.CompleteAsync();
        meterListener.RecordObservableInstruments();
        parent.Stop();

        Assert.True(pageCount > 0);
        var requestActivity = Assert.Single(
            activities,
            item =>
                item.ParentSpanId == parent.SpanId &&
                Equals(item.GetTagItem("pdfiumraster.orchestrator.operation"), "render") &&
                Equals(item.GetTagItem("pdfiumraster.orchestrator.outcome"), "success"));
        Assert.Equal("PdfiumRaster.Orchestrator render", requestActivity.DisplayName);
        Assert.Equal(ActivityStatusCode.Unset, requestActivity.Status);
        Assert.Equal("render", requestActivity.GetTagItem("pdfiumraster.orchestrator.operation"));
        Assert.Equal("success", requestActivity.GetTagItem("pdfiumraster.orchestrator.outcome"));
        Assert.Equal(1, requestActivity.GetTagItem("pdfiumraster.orchestrator.page_count"));
        var inspectionActivity = Assert.Single(
            activities,
            item =>
                item.ParentSpanId == parent.SpanId &&
                Equals(item.GetTagItem("pdfiumraster.orchestrator.operation"), "get_page_count") &&
                Equals(item.GetTagItem("pdfiumraster.orchestrator.outcome"), "success"));
        Assert.Equal("PdfiumRaster.Orchestrator get_page_count", inspectionActivity.DisplayName);
        Assert.Equal(pageCount, inspectionActivity.GetTagItem("pdfiumraster.orchestrator.page_count"));
        var failedActivity = Assert.Single(
            activities,
            item =>
                item.ParentSpanId == parent.SpanId &&
                Equals(item.GetTagItem("pdfiumraster.orchestrator.outcome"), "error"));
        Assert.Equal(ActivityStatusCode.Error, failedActivity.Status);
        Assert.Equal(
            typeof(PdfWorkerRemoteException).FullName,
            failedActivity.GetTagItem("error.type"));

        var logs = loggerFactory.Entries.ToArray();
        Assert.Contains(logs, item => item.EventId == 1000 && item.Level == LogLevel.Information);
        Assert.Contains(logs, item => item.EventId == 1100 && item.Level == LogLevel.Trace);
        Assert.Contains(logs, item => item.EventId == 1101 && item.Level == LogLevel.Trace);
        Assert.Contains(logs, item => item.EventId == 1102 && item.Level == LogLevel.Trace);
        Assert.Contains(logs, item => item.EventId == 1103 && item.Level == LogLevel.Debug);
        Assert.Contains(logs, item => item.EventId == 1200 && item.Level == LogLevel.Information);
        Assert.Contains(logs, item => item.EventId == 1201 && item.Level == LogLevel.Information);

        var capturedMeasurements = measurements.ToArray();
        Assert.Contains(capturedMeasurements, item =>
            item.Name == "pdfiumraster.orchestrator.requests" &&
            item.Value == 1 &&
            item.Tags.Contains(new KeyValuePair<string, object?>("operation", "render")) &&
            item.Tags.Contains(new KeyValuePair<string, object?>("outcome", "success")));
        Assert.Contains(capturedMeasurements, item =>
            item.Name == "pdfiumraster.orchestrator.requests" &&
            item.Value == 1 &&
            item.Tags.Contains(new KeyValuePair<string, object?>("operation", "get_page_count")) &&
            item.Tags.Contains(new KeyValuePair<string, object?>("outcome", "success")));
        Assert.Contains(capturedMeasurements, item =>
            item.Name == "pdfiumraster.orchestrator.request.duration" && item.Value >= 0);
        Assert.Contains(capturedMeasurements, item =>
            item.Name == "pdfiumraster.orchestrator.queue.duration" && item.Value >= 0);
        Assert.Contains(capturedMeasurements, item =>
            item.Name == "pdfiumraster.orchestrator.workers.active" && item.Value >= 1);
        Assert.Contains(capturedMeasurements, item =>
            item.Name == "pdfiumraster.orchestrator.requests" &&
            item.Tags.Contains(new KeyValuePair<string, object?>("outcome", "error")));

        var telemetryText = string.Join(
            "|",
            logs.Select(item => item.Message)
                .Concat(activities.Select(item => string.Join(";", item.TagObjects)))
                .Concat(capturedMeasurements.Select(item => string.Join(";", item.Tags))));
        Assert.DoesNotContain(password, telemetryText, StringComparison.Ordinal);
        Assert.DoesNotContain(pdfPath, telemetryText, StringComparison.Ordinal);
        Assert.DoesNotContain(missingPdfPath, telemetryText, StringComparison.Ordinal);
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

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        internal ConcurrentQueue<CapturedLog> Entries { get; } = new();

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(Entries);
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<CapturedLog> _entries;

        internal CapturingLogger(ConcurrentQueue<CapturedLog> entries)
        {
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue(new CapturedLog(eventId.Id, logLevel, formatter(state, exception)));
        }
    }

    private sealed record CapturedLog(int EventId, LogLevel Level, string Message);

    private sealed record CapturedMeasurement(
        string Name,
        double Value,
        KeyValuePair<string, object?>[] Tags);
}
