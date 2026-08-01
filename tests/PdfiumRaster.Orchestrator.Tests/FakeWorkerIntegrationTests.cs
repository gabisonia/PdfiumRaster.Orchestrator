namespace PdfiumRaster.Orchestration.Tests;

public sealed class FakeWorkerIntegrationTests : IDisposable
{
    private const string FakeModeVariable = "PDFIUMRASTER_FAKE_WORKER_MODE";
    private const string WorkerPathVariable = "PDFIUMRASTER_WORKER_PATH";
    private readonly string? _originalFakeMode;
    private readonly string? _originalWorkerPath;

    public FakeWorkerIntegrationTests()
    {
        _originalFakeMode = Environment.GetEnvironmentVariable(FakeModeVariable);
        _originalWorkerPath = Environment.GetEnvironmentVariable(WorkerPathVariable);
        var fakeWorkerPath = typeof(FakeWorkerIntegrationTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PdfiumRasterFakeWorkerPath")
            .Value;
        Environment.SetEnvironmentVariable(WorkerPathVariable, fakeWorkerPath);
    }

    [Theory]
    [InlineData("unexpected-first-message")]
    [InlineData("wrong-token")]
    [InlineData("wrong-version")]
    public void StartupRejectsInvalidHandshake(string mode)
    {
        SetMode(mode);

        var exception = Assert.Throws<PdfWorkerStartupException>(
            () => new PdfRenderOrchestrator(CreateOptions()));

        Assert.IsType<PdfWorkerProtocolException>(exception.InnerException);
    }

    [Fact]
    public void StartupReportsWorkerExitBeforeConnection()
    {
        SetMode("exit-before-connect");

        var exception = Assert.Throws<PdfWorkerStartupException>(
            () => new PdfRenderOrchestrator(CreateOptions()));

        Assert.Contains("before connecting", exception.Message);
    }

    [Fact]
    public void ConfiguredStartupTimeoutTerminatesWorkerThatNeverConnects()
    {
        SetMode("hang-before-connect");
        var options = CreateOptions();
        options.WorkerStartupTimeout = TimeSpan.FromMilliseconds(200);

        Assert.Throws<PdfWorkerStartupException>(() => new PdfRenderOrchestrator(options));
    }

    [Fact]
    public async Task MidFrameDisconnectIsReportedAsWorkerCrash()
    {
        SetMode("disconnect-mid-frame");
        using var orchestrator = new PdfRenderOrchestrator(CreateOptions());

        await Assert.ThrowsAsync<PdfWorkerCrashedException>(
            () => orchestrator.RenderPageAsync("unused.pdf", pageIndex: 0));
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task InvalidBitmapHeaderIsReportedAsProtocolFailure()
    {
        SetMode("invalid-bitmap-header");
        using var orchestrator = new PdfRenderOrchestrator(CreateOptions());

        await Assert.ThrowsAsync<PdfWorkerProtocolException>(
            () => orchestrator.RenderPageAsync("unused.pdf", pageIndex: 0));
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task BitmapBytesBeyondDeclaredLengthAreRejected()
    {
        SetMode("excess-bitmap-output");
        using var orchestrator = new PdfRenderOrchestrator(CreateOptions());

        await Assert.ThrowsAsync<PdfWorkerProtocolException>(
            () => orchestrator.RenderPageAsync("unused.pdf", pageIndex: 0));
        await orchestrator.CompleteAsync();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FakeModeVariable, _originalFakeMode);
        Environment.SetEnvironmentVariable(WorkerPathVariable, _originalWorkerPath);
    }

    private static PdfRenderOrchestratorOptions CreateOptions()
    {
        return new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 1,
            WorkerStartupTimeout = TimeSpan.FromSeconds(10),
            WorkerRestartDelays = new[] { TimeSpan.Zero },
        };
    }

    private static void SetMode(string mode)
    {
        Environment.SetEnvironmentVariable(FakeModeVariable, mode);
    }
}
