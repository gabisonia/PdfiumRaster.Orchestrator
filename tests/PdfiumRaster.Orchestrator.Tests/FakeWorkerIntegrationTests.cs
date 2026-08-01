namespace PdfiumRaster.Orchestration.Tests;

public sealed class FakeWorkerIntegrationTests : IDisposable
{
    private const string FakeModeVariable = "PDFIUMRASTER_FAKE_WORKER_MODE";
    private const string StateFileVariable = "PDFIUMRASTER_FAKE_WORKER_STATE_FILE";
    private const string WorkerPathVariable = "PDFIUMRASTER_WORKER_PATH";
    private readonly string? _originalFakeMode;
    private readonly string? _originalStateFile;
    private readonly string? _originalWorkerPath;
    private string? _stateFile;

    public FakeWorkerIntegrationTests()
    {
        _originalFakeMode = Environment.GetEnvironmentVariable(FakeModeVariable);
        _originalStateFile = Environment.GetEnvironmentVariable(StateFileVariable);
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

        var exception = await Assert.ThrowsAsync<PdfWorkerCrashedException>(
            () => orchestrator.RenderPageAsync("unused.pdf", pageIndex: 0));
        Assert.NotNull(exception.InnerException);
        Assert.Equal(26, exception.ExitCode);
        Assert.Equal(string.Empty, exception.StandardError);
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

    [Theory]
    [InlineData("missing-bitmap-header")]
    [InlineData("incomplete-bitmap")]
    [InlineData("duplicate-bitmap-header")]
    [InlineData("unexpected-request-message")]
    public async Task InvalidBitmapResponseSequencesAreRejected(string mode)
    {
        SetMode(mode);
        using var orchestrator = new PdfRenderOrchestrator(CreateOptions());

        await Assert.ThrowsAsync<PdfWorkerProtocolException>(
            () => orchestrator.RenderPageAsync("unused.pdf", pageIndex: 0));
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task BitmapHeaderForStreamOutputIsRejected()
    {
        SetMode("bitmap-header-for-stream");
        using var output = new MemoryStream();
        using var orchestrator = new PdfRenderOrchestrator(CreateOptions());

        await Assert.ThrowsAsync<PdfWorkerProtocolException>(
            () => orchestrator.SavePageAsync("unused.pdf", pageIndex: 0, output));
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task OutputBytesForPathTargetAreRejected()
    {
        SetMode("output-for-path");
        using var orchestrator = new PdfRenderOrchestrator(CreateOptions());

        await Assert.ThrowsAsync<PdfWorkerProtocolException>(
            () => orchestrator.SavePageAsync("unused.pdf", pageIndex: 0, "unused.png"));
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task FakeWorkerCoversSuccessfulBitmapStreamAndPathResponses()
    {
        SetMode("valid-bitmap");
        using (var bitmapOrchestrator = new PdfRenderOrchestrator(CreateOptions()))
        {
            var bitmap = await bitmapOrchestrator.RenderPageAsync("unused.pdf", 0);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, bitmap.Pixels);
            await bitmapOrchestrator.CompleteAsync();
        }

        SetMode("valid-stream");
        using (var output = new MemoryStream())
        using (var streamOrchestrator = new PdfRenderOrchestrator(CreateOptions()))
        {
            await streamOrchestrator.SavePageAsync("unused.pdf", 0, output);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, output.ToArray());
            await streamOrchestrator.CompleteAsync();
        }

        SetMode("valid-path");
        using var pathOrchestrator = new PdfRenderOrchestrator(CreateOptions());
        await pathOrchestrator.SavePageAsync("unused.pdf", 0, "unused.png");
        await pathOrchestrator.CompleteAsync();
    }

    [Fact]
    public async Task RemoteWorkerErrorsExposeTypeAndMessageWithoutReplacingHealthyWorker()
    {
        SetMode("healthy");
        using var orchestrator = new PdfRenderOrchestrator(CreateOptions());

        var first = await Assert.ThrowsAsync<PdfWorkerRemoteException>(
            () => orchestrator.RenderPageAsync("unused.pdf", 0));
        var second = await Assert.ThrowsAsync<PdfWorkerRemoteException>(
            () => orchestrator.RenderPageAsync("unused.pdf", 0));

        Assert.Equal(typeof(InvalidOperationException).FullName, first.RemoteExceptionType);
        Assert.Contains("Fake worker request", first.Message);
        Assert.Equal(first.RemoteExceptionType, second.RemoteExceptionType);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task CrashExceptionBoundsStandardErrorAndReportsExitCode()
    {
        SetMode("stderr-disconnect");
        using var orchestrator = new PdfRenderOrchestrator(CreateOptions());

        var exception = await Assert.ThrowsAsync<PdfWorkerCrashedException>(
            () => orchestrator.RenderPageAsync("unused.pdf", 0));

        Assert.Equal(28, exception.ExitCode);
        Assert.InRange(exception.StandardError.Length, 1, 8192);
        Assert.EndsWith("stderr-tail" + Environment.NewLine, exception.StandardError);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task ExhaustedReplacementAttemptsFaultTheOrchestrator()
    {
        SetMode("disconnect-then-replacements-fail");
        _stateFile = Path.Combine(Path.GetTempPath(), $"pdfium-fake-state-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(StateFileVariable, _stateFile);
        var options = CreateOptions();
        options.WorkerRestartDelays = new[] { TimeSpan.Zero, TimeSpan.Zero };
        var orchestrator = new PdfRenderOrchestrator(options);

        var crashing = orchestrator.RenderPageAsync("unused.pdf", 0);
        var pending = orchestrator.RenderPageAsync("unused.pdf", 0);

        await Assert.ThrowsAsync<PdfWorkerCrashedException>(() => crashing);
        await Assert.ThrowsAsync<PdfWorkerStartupException>(() => pending);
        await Assert.ThrowsAsync<PdfWorkerStartupException>(() => orchestrator.CompleteAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.RenderPageAsync("unused.pdf", 0));
        Assert.Throws<PdfWorkerStartupException>(() => orchestrator.Dispose());
    }

    [Fact]
    public void MissingConfiguredWorkerProducesActionableStartupFailure()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-worker-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(WorkerPathVariable, missingPath);

        var exception = Assert.Throws<PdfWorkerStartupException>(
            () => new PdfRenderOrchestrator(CreateOptions()));

        Assert.Contains(missingPath, exception.Message);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FakeModeVariable, _originalFakeMode);
        Environment.SetEnvironmentVariable(StateFileVariable, _originalStateFile);
        Environment.SetEnvironmentVariable(WorkerPathVariable, _originalWorkerPath);
        if (_stateFile is not null && File.Exists(_stateFile))
        {
            File.Delete(_stateFile);
        }
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
