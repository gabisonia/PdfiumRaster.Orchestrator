using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

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
        Assert.DoesNotContain(events, item => item.Payload.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.Payload.Contains(pdfPath, StringComparison.Ordinal));
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
            var payload = eventData.Payload is null
                ? string.Empty
                : string.Join("|", eventData.Payload.Select(value => value?.ToString() ?? string.Empty));
            Events.Enqueue(new CapturedEvent(eventData.EventName ?? string.Empty, payload));
        }
    }

    private sealed class CapturedEvent
    {
        internal CapturedEvent(string name, string payload)
        {
            Name = name;
            Payload = payload;
        }

        internal string Name { get; }
        internal string Payload { get; }
    }
}
