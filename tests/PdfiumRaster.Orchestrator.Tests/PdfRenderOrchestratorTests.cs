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
        await using var orchestrator = CreateOrchestrator();

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
        await using var orchestrator = CreateOrchestrator();
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
        await using var orchestrator = CreateOrchestrator();

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
    public async Task BatchRenderAndSaveReuseOneRequestAndPreserveOrder()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        var bytes = await File.ReadAllBytesAsync(pdfPath);
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "TestOutput", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        var options = new PdfImageConversionOptions
        {
            Render = PdfPageRenderOptions.ScreenPreview,
            Format = PdfImageOutputFormat.Png,
        };
        using var stream = new MemoryStream(bytes, writable: false);
        await using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 6,
        });

        var fromPath = orchestrator.RenderPagesAsync(pdfPath, new[] { 0, 0 }, options);
        var fromBytes = orchestrator.RenderPagesAsync(bytes, new[] { 0, 0 }, options);
        var fromStream = orchestrator.RenderPagesAsync(stream, new[] { 0, 0 }, options, leaveOpen: true);
        var pathFiles = new[]
        {
            new PdfPageFileOutput(0, Path.Combine(outputDirectory, "path-1.png")),
            new PdfPageFileOutput(0, Path.Combine(outputDirectory, "path-2.png")),
        };
        var byteFiles = new[]
        {
            new PdfPageFileOutput(0, Path.Combine(outputDirectory, "bytes-1.png")),
            new PdfPageFileOutput(0, Path.Combine(outputDirectory, "bytes-2.png")),
        };
        using var saveStream = new MemoryStream(bytes, writable: false);
        var streamFiles = new[]
        {
            new PdfPageFileOutput(0, Path.Combine(outputDirectory, "stream-1.png")),
            new PdfPageFileOutput(0, Path.Combine(outputDirectory, "stream-2.png")),
        };

        await Task.WhenAll(
            fromPath,
            fromBytes,
            fromStream,
            orchestrator.SavePagesAsync(pdfPath, pathFiles, options),
            orchestrator.SavePagesAsync(bytes, byteFiles, options),
            orchestrator.SavePagesAsync(saveStream, streamFiles, options, leaveOpen: true));
        await orchestrator.CompleteAsync();

        foreach (var batch in new[] { await fromPath, await fromBytes, await fromStream })
        {
            Assert.Equal(2, batch.Count);
            Assert.Equal(batch[0].Pixels, batch[1].Pixels);
        }

        Assert.True(stream.CanRead);
        Assert.True(saveStream.CanRead);
        Assert.Equal(6, Directory.GetFiles(outputDirectory, "*.png").Length);
        Assert.All(Directory.GetFiles(outputDirectory, "*.png"), path => Assert.True(new FileInfo(path).Length > 8));
    }

    [Fact]
    public async Task ResourceLimitsRejectInputsBitmapsAndAtomicFileOutputs()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        var bytes = await File.ReadAllBytesAsync(pdfPath);
        await using var inputLimited = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 2,
            MaximumInputBytes = bytes.Length - 1,
        });
        var inputException = Assert.Throws<PdfRenderResourceLimitException>(() =>
        {
            _ = inputLimited.RenderPageAsync(bytes, 0);
        });
        Assert.Equal("input bytes", inputException.Resource);
        Assert.Equal(bytes.Length - 1, inputException.Limit);
        Assert.Equal(bytes.Length, inputException.Observed);
        await inputLimited.CompleteAsync();

        await using var bitmapLimited = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 2,
            MaximumBitmapBytes = 1,
        });
        var bitmapException = await Assert.ThrowsAsync<PdfRenderResourceLimitException>(
            () => bitmapLimited.RenderPageAsync(pdfPath, 0));
        Assert.Equal("bitmap bytes", bitmapException.Resource);
        await bitmapLimited.CompleteAsync();

        var outputPath = Path.Combine(AppContext.BaseDirectory, "TestOutput", Guid.NewGuid() + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllBytesAsync(outputPath, new byte[] { 1, 2, 3 });
        await using var outputLimited = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 2,
            MaximumOutputBytes = 1,
        });
        var outputException = await Assert.ThrowsAsync<PdfRenderResourceLimitException>(
            () => outputLimited.SavePageAsync(pdfPath, 0, outputPath));
        Assert.Equal("output bytes", outputException.Resource);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(outputPath));
        await outputLimited.CompleteAsync();
    }

    [Fact]
    public async Task ResourceLimitsCoverPathNonSeekableStreamEncodedStreamAndBatchTotals()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        var bytes = await File.ReadAllBytesAsync(pdfPath);

        {
            await using var pathLimited = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
               {
                   WorkerCount = 1,
                   QueueCapacity = 2,
                   MaximumInputBytes = 1,
               });
            var exception = await Assert.ThrowsAsync<PdfRenderResourceLimitException>(
                () => pathLimited.RenderPageAsync(pdfPath, 0));
            Assert.Equal("input bytes", exception.Resource);
            await pathLimited.CompleteAsync();
        }

        {
            await using var streamLimited = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
               {
                   WorkerCount = 1,
                   QueueCapacity = 2,
                   MaximumInputBytes = bytes.Length - 1,
               });
            using var input = new NonSeekableReadStream(bytes);
            await Assert.ThrowsAsync<PdfRenderResourceLimitException>(
                () => streamLimited.RenderPageAsync(input, 0, leaveOpen: true));
            await streamLimited.CompleteAsync();
        }

        {
            await using var encodedLimited = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
               {
                   WorkerCount = 1,
                   QueueCapacity = 2,
                   MaximumOutputBytes = 1,
               });
            using var output = new MemoryStream();
            await Assert.ThrowsAsync<PdfRenderResourceLimitException>(
                () => encodedLimited.SavePageAsync(pdfPath, 0, output));
            Assert.True(output.CanWrite);
            await encodedLimited.CompleteAsync();
        }

        var oneBitmap = PdfImageConverter.RenderPage(pdfPath, 0);
        {
            await using var batchLimited = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
               {
                   WorkerCount = 1,
                   QueueCapacity = 2,
                   MaximumOutputBytes = oneBitmap.Pixels.LongLength + 1,
               });
            var exception = await Assert.ThrowsAsync<PdfRenderResourceLimitException>(
                () => batchLimited.RenderPagesAsync(pdfPath, new[] { 0, 0 }));
            Assert.Equal("output bytes", exception.Resource);
            Assert.True(exception.Observed > exception.Limit);
            await batchLimited.CompleteAsync();
        }
    }

    [Fact]
    public async Task SaveBatchKeepsEarlierAtomicFilesWhenAggregateLimitIsExceeded()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        var directory = Path.Combine(AppContext.BaseDirectory, "TestOutput", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var reference = Path.Combine(directory, "reference.png");
        PdfImageConverter.SavePage(pdfPath, 0, reference);
        var oneFileBytes = new FileInfo(reference).Length;
        var first = Path.Combine(directory, "first.png");
        var second = Path.Combine(directory, "second.png");
        await File.WriteAllBytesAsync(second, new byte[] { 7, 8, 9 });
        await using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 2,
            MaximumOutputBytes = oneFileBytes + 1,
        });

        await Assert.ThrowsAsync<PdfRenderResourceLimitException>(() => orchestrator.SavePagesAsync(pdfPath, new[]
        {
            new PdfPageFileOutput(0, first),
            new PdfPageFileOutput(0, second),
        }));
        await orchestrator.CompleteAsync();

        Assert.True(new FileInfo(first).Length > 8);
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(second));
    }

    [Fact]
    public async Task ConfiguredTemporaryDirectoryContainsAndCleansWorkerDirectory()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "TestOutput", Guid.NewGuid().ToString("N"));
        await using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 2,
            TemporaryDirectory = root,
        });
        var workerDirectory = GetFirstWorkerTemporaryDirectory(orchestrator);

        Assert.Equal(root, Directory.GetParent(workerDirectory)!.FullName);
        await orchestrator.CompleteAsync();
        Assert.False(Directory.Exists(workerDirectory));
    }

    [Fact]
    public async Task HardTimeoutTerminatesActiveWorker()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        await using var input = new GateReadStream(bytes);
        await using var orchestrator = CreateOrchestrator(TimeSpan.FromMilliseconds(100));
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
        await using var orchestrator = CreateOrchestrator(TimeSpan.FromMilliseconds(100));
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
        await using var orchestrator = CreateOrchestrator();
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
        await using var orchestrator = CreateOrchestrator();
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
        await using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
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
        await using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
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
        await using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
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
        await using var orchestrator = CreateOrchestrator();

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
        await using var orchestrator = CreateOrchestrator();

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
        await using var orchestrator = CreateOrchestrator();
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
        await using var orchestrator = CreateOrchestrator();
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

    private sealed class NonSeekableReadStream : MemoryStream
    {
        internal NonSeekableReadStream(byte[] bytes)
            : base(bytes, writable: false)
        {
        }

        public override bool CanSeek => false;
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
