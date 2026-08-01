namespace PdfiumRaster.Orchestration.Tests;

public sealed class PdfRenderOrchestratorTests : IDisposable
{
    private const string WorkerPathVariable = "PDFIUMRASTER_WORKER_PATH";
    private readonly string? _originalWorkerPath;

    public PdfRenderOrchestratorTests()
    {
        _originalWorkerPath = Environment.GetEnvironmentVariable(WorkerPathVariable);
        if (string.IsNullOrWhiteSpace(_originalWorkerPath))
        {
            var workerPath = typeof(PdfRenderOrchestratorTests).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "PdfiumRasterWorkerPath")
                .Value;
            Environment.SetEnvironmentVariable(
                WorkerPathVariable,
                workerPath);
        }
    }

    [Fact]
    public async Task RenderPathMatchesInProcessBitmap()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        var options = new PdfImageConversionOptions
        {
            Render = PdfPageRenderOptions.ScreenPreview,
            ColorMode = PdfImageColorMode.Grayscale,
        };
        var expected = PdfImageConverter.RenderPage(pdfPath, 0, options);
        using var orchestrator = CreateOrchestrator();

        var actual = await orchestrator.RenderPageAsync(pdfPath, 0, options);
        await orchestrator.CompleteAsync();

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Stride, actual.Stride);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Fact]
    public async Task ByteAndStreamInputsSupportBitmapAndEncodedStreamOutputs()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        var png = new PdfImageConversionOptions
        {
            Render = PdfPageRenderOptions.ScreenPreview,
            Format = PdfImageOutputFormat.Png,
            Encoding = PdfImageEncodingOptions.Fast,
        };
        using var orchestrator = CreateOrchestrator();
        using var input = new MemoryStream(bytes, writable: false);
        using var output = new MemoryStream();

        var bitmapTask = orchestrator.RenderPageAsync(bytes, 0, png);
        var saveTask = orchestrator.SavePageAsync(input, 0, output, png, leaveOpen: true);
        await Task.WhenAll(bitmapTask, saveTask);
        var bitmap = await bitmapTask;
        await orchestrator.CompleteAsync();

        Assert.True(bitmap.Pixels.Length > 0);
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
        Assert.True(output.Length > 8);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, output.ToArray().Take(4).ToArray());
    }

    [Fact]
    public async Task SavePathWritesImage()
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "TestOutput", Guid.NewGuid() + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var orchestrator = CreateOrchestrator();

        await orchestrator.SavePageAsync(
            GetAssetPath("smoke.pdf"),
            0,
            outputPath,
            new PdfImageConversionOptions
            {
                Render = PdfPageRenderOptions.ScreenPreview,
                Format = PdfImageOutputFormat.Png,
            });
        await orchestrator.CompleteAsync();

        Assert.True(new FileInfo(outputPath).Length > 8);
    }

    [Fact]
    public async Task HardTimeoutTerminatesActiveWorker()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        await using var input = new GateReadStream(bytes);
        using var orchestrator = CreateOrchestrator(TimeSpan.FromMilliseconds(100));
        var workerTemporaryDirectory = GetFirstWorkerTemporaryDirectory(orchestrator);
        var request = orchestrator.RenderPageAsync(input, 0, leaveOpen: true);
        await input.WaitUntilReadAsync();

        var exception = await Assert.ThrowsAsync<PdfWorkerTimeoutException>(() => request);
        Assert.Equal(TimeSpan.FromMilliseconds(100), exception.Timeout);
        input.Release();
        await WaitForDirectoryDeletionAsync(workerTemporaryDirectory);
        Assert.False(Directory.Exists(workerTemporaryDirectory));
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task HardTimeoutDefersOwnedStreamCleanupUntilPendingReadEnds()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        var input = new GateReadStream(bytes);
        using var orchestrator = CreateOrchestrator(TimeSpan.FromMilliseconds(100));
        var request = orchestrator.RenderPageAsync(input, 0);
        await input.WaitUntilReadAsync();

        await Assert.ThrowsAsync<PdfWorkerTimeoutException>(() => request);
        Assert.True(input.CanRead);

        input.Release();
        await orchestrator.CompleteAsync();
        Assert.False(input.CanRead);
    }

    [Fact]
    public async Task CrashedWorkerIsReplacedWithoutRetryingRequest()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        await using var input = new GateReadStream(bytes);
        using var orchestrator = CreateOrchestrator();
        var request = orchestrator.RenderPageAsync(input, 0, leaveOpen: true);
        await input.WaitUntilReadAsync();

        KillFirstWorker(orchestrator);
        input.Release();

        await Assert.ThrowsAsync<PdfWorkerCrashedException>(() => request);
        var bitmap = await orchestrator.RenderPageAsync(GetAssetPath("smoke.pdf"), 0);
        await orchestrator.CompleteAsync();

        Assert.True(bitmap.Pixels.Length > 0);
    }

    [Fact]
    public async Task CanceledQueuedRequestDoesNotRun()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        await using var input = new GateReadStream(bytes);
        using var orchestrator = CreateOrchestrator();
        var first = orchestrator.RenderPageAsync(input, 0, leaveOpen: true);
        await input.WaitUntilReadAsync();
        using var cancellation = new CancellationTokenSource();
        var second = orchestrator.RenderPageAsync(
            GetAssetPath("smoke.pdf"),
            0,
            cancellationToken: cancellation.Token);
        cancellation.Cancel();
        input.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await first;
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task TwoWorkersProcessTwoNativeLanesConcurrently()
    {
        if (Environment.ProcessorCount < 2)
        {
            return;
        }

        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        var barrier = new AsyncReadBarrier(2);
        await using var firstInput = new BarrierReadStream(bytes, barrier);
        await using var secondInput = new BarrierReadStream(bytes, barrier);
        using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 2,
            QueueCapacity = 2,
        });

        var requests = Task.WhenAll(
            orchestrator.RenderPageAsync(firstInput, 0, leaveOpen: true),
            orchestrator.RenderPageAsync(secondInput, 0, leaveOpen: true));
        Assert.Same(requests, await Task.WhenAny(requests, Task.Delay(TimeSpan.FromSeconds(5))));
        await requests;
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task RejectModeFaultsSubmissionBeyondActiveAndQueuedCapacity()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        await using var input = new GateReadStream(bytes);
        using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 1,
            QueueFullMode = PdfRenderQueueFullMode.Reject,
        });
        var first = orchestrator.RenderPageAsync(input, 0, leaveOpen: true);
        await input.WaitUntilReadAsync();
        var second = orchestrator.RenderPageAsync(GetAssetPath("smoke.pdf"), 0);

        await Assert.ThrowsAsync<PdfRenderQueueFullException>(
            () => orchestrator.RenderPageAsync(GetAssetPath("smoke.pdf"), 0));
        input.Release();
        await Task.WhenAll(first, second);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task RejectedStreamSubmissionReleasesOwnedInput()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        await using var activeInput = new GateReadStream(bytes);
        var rejectedInput = new MemoryStream(bytes, writable: false);
        using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 1,
            QueueFullMode = PdfRenderQueueFullMode.Reject,
        });
        var active = orchestrator.RenderPageAsync(activeInput, 0, leaveOpen: true);
        await activeInput.WaitUntilReadAsync();
        var queued = orchestrator.RenderPageAsync(GetAssetPath("smoke.pdf"), 0);

        await Assert.ThrowsAsync<PdfRenderQueueFullException>(
            () => orchestrator.RenderPageAsync(rejectedInput, 0));

        Assert.False(rejectedInput.CanRead);
        activeInput.Release();
        await Task.WhenAll(active, queued);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task PreCanceledStreamSubmissionReleasesOwnedInput()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        var input = new MemoryStream(bytes, writable: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var orchestrator = CreateOrchestrator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestrator.RenderPageAsync(input, 0, cancellationToken: cancellation.Token));

        Assert.False(input.CanRead);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task ValidationFailureHonorsInputStreamOwnership()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        var ownedInput = new MemoryStream(bytes, writable: false);
        using var callerOwnedInput = new MemoryStream(bytes, writable: false);
        using var orchestrator = CreateOrchestrator();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => orchestrator.RenderPageAsync(ownedInput, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => orchestrator.RenderPageAsync(callerOwnedInput, -1, leaveOpen: true));

        Assert.False(ownedInput.CanRead);
        Assert.True(callerOwnedInput.CanRead);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task CancelAsyncCancelsQueuedWorkAndStopsWorkers()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        await using var activeInput = new GateReadStream(bytes);
        using var orchestrator = CreateOrchestrator();
        var active = orchestrator.RenderPageAsync(activeInput, 0, leaveOpen: true);
        await activeInput.WaitUntilReadAsync();
        var queued = orchestrator.RenderPageAsync(GetAssetPath("smoke.pdf"), 0);

        var cancellation = orchestrator.CancelAsync();
        activeInput.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await cancellation;
    }

    [Fact]
    public async Task WorkerTemporaryDirectoryIsOwnerOnlyOnUnix()
    {
        using var orchestrator = CreateOrchestrator();
        var temporaryDirectory = GetFirstWorkerTemporaryDirectory(orchestrator);

        if (!OperatingSystem.IsWindows())
        {
            var mode = new DirectoryInfo(temporaryDirectory).UnixFileMode;
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                mode & (UnixFileMode)0x1FF);
        }

        await orchestrator.CompleteAsync();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkerPathVariable, _originalWorkerPath);
    }

    private static PdfRenderOrchestrator CreateOrchestrator(TimeSpan? timeout = null)
    {
        return new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 2,
            RequestTimeout = timeout,
        });
    }

    private static string GetAssetPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName);
    }

    private static void KillFirstWorker(PdfRenderOrchestrator orchestrator)
    {
        var connection = GetFirstWorkerConnection(orchestrator);
        var processField = connection.GetType().GetField(
            "_process",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var process = (System.Diagnostics.Process)processField.GetValue(connection)!;
        process.Kill();
        process.WaitForExit();
    }

    private static string GetFirstWorkerTemporaryDirectory(PdfRenderOrchestrator orchestrator)
    {
        var connection = GetFirstWorkerConnection(orchestrator);
        var temporaryDirectoryField = connection.GetType().GetField(
            "_temporaryDirectory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (string)temporaryDirectoryField.GetValue(connection)!;
    }

    private static object GetFirstWorkerConnection(PdfRenderOrchestrator orchestrator)
    {
        var workersField = typeof(PdfRenderOrchestrator).GetField(
            "_workers",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var workers = (Array)workersField.GetValue(orchestrator)!;
        var worker = workers.GetValue(0)!;
        var connectionField = worker.GetType().GetField(
            "_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return connectionField.GetValue(worker)!;
    }

    private static async Task WaitForDirectoryDeletionAsync(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Directory.Exists(path) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    private sealed class GateReadStream : MemoryStream
    {
        private readonly TaskCompletionSource<object?> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _firstRead = 1;

        internal GateReadStream(byte[] bytes)
            : base(bytes, writable: false)
        {
        }

        internal Task WaitUntilReadAsync() => _entered.Task;

        internal void Release() => _release.TrySetResult(null);

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _firstRead, 0) != 0)
            {
                _entered.TrySetResult(null);
                await _release.Task.ConfigureAwait(false);
            }

            return await base.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class AsyncReadBarrier
    {
        private readonly int _participantCount;
        private readonly TaskCompletionSource<object?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        internal AsyncReadBarrier(int participantCount)
        {
            _participantCount = participantCount;
        }

        internal Task SignalAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrivals) == _participantCount)
            {
                _completion.TrySetResult(null);
            }

            return _completion.Task;
        }
    }

    private sealed class BarrierReadStream : MemoryStream
    {
        private readonly AsyncReadBarrier _barrier;
        private int _firstRead = 1;

        internal BarrierReadStream(byte[] bytes, AsyncReadBarrier barrier)
            : base(bytes, writable: false)
        {
            _barrier = barrier;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _firstRead, 0) != 0)
            {
                await _barrier.SignalAndWaitAsync().ConfigureAwait(false);
            }

            return await base.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
    }
}
