namespace PdfiumRaster.Orchestration.Tests;

public sealed class PdfRenderOrchestratorPublicApiTests : IDisposable
{
    private const string WorkerPathVariable = "PDFIUMRASTER_WORKER_PATH";
    private readonly string? _originalWorkerPath;

    public PdfRenderOrchestratorPublicApiTests()
    {
        _originalWorkerPath = Environment.GetEnvironmentVariable(WorkerPathVariable);
        if (string.IsNullOrWhiteSpace(_originalWorkerPath))
        {
            Environment.SetEnvironmentVariable(WorkerPathVariable, GetWorkerPath());
        }
    }

    [Fact]
    public async Task EveryRenderAndSaveOverloadProducesOutputWithDocumentedOwnership()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "TestOutput", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        var options = new PdfImageConversionOptions
        {
            Render = PdfPageRenderOptions.ScreenPreview,
            Format = PdfImageOutputFormat.Png,
            Encoding = PdfImageEncodingOptions.Fast,
        };

        using var renderInput = new MemoryStream(pdfBytes, writable: false);
        using var pathOutput = new MemoryStream();
        using var byteOutput = new MemoryStream();
        using var streamPathInput = new MemoryStream(pdfBytes, writable: false);
        using var streamOutputInput = new MemoryStream(pdfBytes, writable: false);
        using var streamOutput = new MemoryStream();
        using var orchestrator = CreateOrchestrator(queueCapacity: 12);

        var renderPath = orchestrator.RenderPageAsync(pdfPath, 0, options);
        var renderBytes = orchestrator.RenderPageAsync(pdfBytes, 0, options);
        var renderStream = orchestrator.RenderPageAsync(renderInput, 0, options, leaveOpen: true);
        var savePathPath = orchestrator.SavePageAsync(
            pdfPath, 0, Path.Combine(outputDirectory, "path-path.png"), options);
        var savePathStream = orchestrator.SavePageAsync(pdfPath, 0, pathOutput, options);
        var saveBytesPath = orchestrator.SavePageAsync(
            pdfBytes, 0, Path.Combine(outputDirectory, "bytes-path.png"), options);
        var saveBytesStream = orchestrator.SavePageAsync(pdfBytes, 0, byteOutput, options);
        var saveStreamPath = orchestrator.SavePageAsync(
            streamPathInput,
            0,
            Path.Combine(outputDirectory, "stream-path.png"),
            options,
            leaveOpen: true);
        var saveStreamStream = orchestrator.SavePageAsync(
            streamOutputInput,
            0,
            streamOutput,
            options,
            leaveOpen: true);

        await Task.WhenAll(
            renderPath,
            renderBytes,
            renderStream,
            savePathPath,
            savePathStream,
            saveBytesPath,
            saveBytesStream,
            saveStreamPath,
            saveStreamStream);
        await orchestrator.CompleteAsync();

        Assert.All(
            new[] { await renderPath, await renderBytes, await renderStream },
            bitmap => Assert.True(bitmap.Pixels.Length > 0));
        var outputFiles = Directory.GetFiles(outputDirectory);
        Assert.Equal(3, outputFiles.Length);
        Assert.All(outputFiles, path => Assert.True(new FileInfo(path).Length > 8));
        Assert.All(
            new[] { pathOutput, byteOutput, streamOutput },
            output =>
            {
                Assert.True(output.CanWrite);
                Assert.True(output.Length > 8);
                Assert.Equal(new byte[] { 137, 80, 78, 71 }, output.ToArray().Take(4).ToArray());
            });
        Assert.True(renderInput.CanRead);
        Assert.True(streamPathInput.CanRead);
        Assert.True(streamOutputInput.CanRead);
    }

    [Fact]
    public async Task OwnedInputStreamsAreDisposedAfterSuccessfulRenderAndSave()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        var renderInput = new MemoryStream(bytes, writable: false);
        var saveInput = new MemoryStream(bytes, writable: false);
        using var output = new MemoryStream();
        using var orchestrator = CreateOrchestrator();

        await orchestrator.RenderPageAsync(renderInput, 0);
        await orchestrator.SavePageAsync(saveInput, 0, output);
        await orchestrator.CompleteAsync();

        Assert.False(renderInput.CanRead);
        Assert.False(saveInput.CanRead);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task PublicMethodsValidateEveryInputAndOutputShape()
    {
        var bytes = File.ReadAllBytes(GetAssetPath("smoke.pdf"));
        using var orchestrator = CreateOrchestrator();
        using var readable = new MemoryStream(bytes, writable: false);
        using var writable = new MemoryStream();
        var disposedInput = new MemoryStream(bytes, writable: false);
        disposedInput.Dispose();
        using var readOnlyOutput = new MemoryStream(bytes, writable: false);

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.RenderPageAsync((string)null!, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.RenderPageAsync(" ", 0));
        await Assert.ThrowsAsync<ArgumentNullException>(() => orchestrator.RenderPageAsync((byte[])null!, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.RenderPageAsync(Array.Empty<byte>(), 0));
        await Assert.ThrowsAsync<ArgumentNullException>(() => orchestrator.RenderPageAsync((Stream)null!, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.RenderPageAsync(disposedInput, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => orchestrator.RenderPageAsync(readable, -1, leaveOpen: true));

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.SavePageAsync(" ", 0, "output.png"));
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.SavePageAsync(bytes, 0, " "));
        await Assert.ThrowsAsync<ArgumentNullException>(() => orchestrator.SavePageAsync(bytes, 0, (Stream)null!));
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.SavePageAsync(bytes, 0, readOnlyOutput));
        await Assert.ThrowsAsync<ArgumentException>(
            () => orchestrator.SavePageAsync(readable, 0, readable, leaveOpen: true));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => orchestrator.RenderPageAsync(
            bytes,
            0,
            new PdfImageConversionOptions { Format = (PdfImageOutputFormat)int.MaxValue }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => orchestrator.SavePageAsync(
            bytes,
            0,
            writable,
            new PdfImageConversionOptions { ColorMode = (PdfImageColorMode)int.MaxValue }));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => orchestrator.RenderPagesAsync(bytes, null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => orchestrator.RenderPagesAsync(bytes, Array.Empty<int>()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => orchestrator.RenderPagesAsync(bytes, new[] { -1 }));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => orchestrator.SavePagesAsync(bytes, null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => orchestrator.SavePagesAsync(bytes, Array.Empty<PdfPageFileOutput>()));
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.SavePagesAsync(bytes, new[]
        {
            new PdfPageFileOutput(0, "same.png"),
            new PdfPageFileOutput(0, "same.png"),
        }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => orchestrator.SavePagesAsync(bytes, new PdfPageFileOutput[] { null! }));

        using var onePageBatch = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 1,
            MaximumBatchPages = 1,
        });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => onePageBatch.RenderPagesAsync(bytes, new[] { 0, 0 }));
        await onePageBatch.CompleteAsync();

        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageFileOutput(-1, "output.png"));
        Assert.Throws<ArgumentException>(() => new PdfPageFileOutput(0, " "));
    }

    [Fact]
    public async Task WaitModeAppliesBackpressureAndEventuallyAcceptsSubmission()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        await using var activeInput = new GateReadStream(bytes);
        using var orchestrator = CreateOrchestrator(queueCapacity: 1);
        var active = orchestrator.RenderPageAsync(activeInput, 0, leaveOpen: true);
        await activeInput.WaitUntilReadAsync();
        var queued = orchestrator.RenderPageAsync(bytes, 0);
        var waiting = orchestrator.RenderPageAsync(bytes, 0);

        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);

        activeInput.Release();
        await Task.WhenAll(active, queued, waiting);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task PreCanceledSaveDisposesOwnedInputAndLeavesOutputOpen()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        var input = new MemoryStream(bytes, writable: false);
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var orchestrator = CreateOrchestrator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestrator.SavePageAsync(input, 0, output, cancellationToken: cancellation.Token));

        Assert.False(input.CanRead);
        Assert.True(output.CanWrite);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task CancelAsyncCancelsQueuedSaveAndHonorsStreamOwnership()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        await using var activeInput = new GateReadStream(bytes);
        var queuedInput = new MemoryStream(bytes, writable: false);
        using var queuedOutput = new MemoryStream();
        using var orchestrator = CreateOrchestrator(queueCapacity: 1);
        var active = orchestrator.RenderPageAsync(activeInput, 0, leaveOpen: true);
        await activeInput.WaitUntilReadAsync();
        var queued = orchestrator.SavePageAsync(queuedInput, 0, queuedOutput);

        var cancellation = orchestrator.CancelAsync();
        activeInput.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await cancellation;
        Assert.False(queuedInput.CanRead);
        Assert.True(queuedOutput.CanWrite);
    }

    [Fact]
    public async Task CompleteAsyncDrainsWorkAndRejectsFurtherSubmissions()
    {
        using var orchestrator = CreateOrchestrator();
        var accepted = orchestrator.RenderPageAsync(GetAssetPath("smoke.pdf"), 0);

        var firstCompletion = orchestrator.CompleteAsync();
        var secondCompletion = orchestrator.CompleteAsync();

        await accepted;
        await Task.WhenAll(firstCompletion, secondCompletion);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.RenderPageAsync(GetAssetPath("smoke.pdf"), 0));
    }

    [Fact]
    public async Task DisposeIsIdempotentAndDisposedInstanceRejectsSubmissions()
    {
        var orchestrator = CreateOrchestrator();

        orchestrator.Dispose();
        orchestrator.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => orchestrator.RenderPageAsync(GetAssetPath("smoke.pdf"), 0));
    }

    [Fact]
    public void WorkerExceptionPublicConstructorsPreserveMessagesAndInnerExceptions()
    {
        var direct = new PdfWorkerException("direct");
        var inner = new InvalidOperationException("inner");
        var wrapped = new PdfWorkerException("wrapped", inner);

        Assert.Equal("direct", direct.Message);
        Assert.Null(direct.InnerException);
        Assert.Equal("wrapped", wrapped.Message);
        Assert.Same(inner, wrapped.InnerException);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkerPathVariable, _originalWorkerPath);
    }

    private static PdfRenderOrchestrator CreateOrchestrator(int queueCapacity = 4)
    {
        return new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = queueCapacity,
        });
    }

    private static string GetAssetPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName);
    }

    private static string GetWorkerPath()
    {
        return typeof(PdfRenderOrchestratorPublicApiTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PdfiumRasterWorkerPath")
            .Value!;
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
}
