namespace PdfiumRaster.Orchestration.Tests;

public sealed class StreamingRenderTests : IDisposable
{
    private const string WorkerPathVariable = "PDFIUMRASTER_WORKER_PATH";
    private readonly string? _originalWorkerPath;

    public StreamingRenderTests()
    {
        _originalWorkerPath = Environment.GetEnvironmentVariable(WorkerPathVariable);
        if (string.IsNullOrWhiteSpace(_originalWorkerPath))
        {
            Environment.SetEnvironmentVariable(WorkerPathVariable, GetWorkerPath());
        }
    }

    [Fact]
    public async Task PathBytesAndStreamInputsYieldOrderedCallerOwnedPages()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        var bytes = await File.ReadAllBytesAsync(pdfPath);
        using var callerOwnedInput = new MemoryStream(bytes, writable: false);
        var ownedInput = new MemoryStream(bytes, writable: false);
        await using var orchestrator = await PdfRenderOrchestrator.CreateAsync(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 4,
        });

        var fromPath = await CollectAsync(orchestrator.RenderPagesStreamAsync(pdfPath, new[] { 0, 0 }));
        var fromBytes = await CollectAsync(orchestrator.RenderPagesStreamAsync(bytes, new[] { 0, 0 }));
        var fromCallerOwnedStream = await CollectAsync(orchestrator.RenderPagesStreamAsync(
            callerOwnedInput,
            new[] { 0, 0 },
            leaveOpen: true));
        var fromOwnedStream = await CollectAsync(orchestrator.RenderPagesStreamAsync(
            ownedInput,
            new[] { 0, 0 }));

        foreach (var results in new[] { fromPath, fromBytes, fromCallerOwnedStream, fromOwnedStream })
        {
            Assert.Equal(new[] { 0, 1 }, results.Select(result => result.Position));
            Assert.Equal(new[] { 0, 0 }, results.Select(result => result.PageIndex));
            Assert.Equal(results[0].Bitmap.Pixels, results[1].Bitmap.Pixels);
        }

        Assert.True(callerOwnedInput.CanRead);
        Assert.False(ownedInput.CanRead);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task CapacityOneResultBufferAppliesConsumerBackpressure()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        await using var orchestrator = await PdfRenderOrchestrator.CreateAsync(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 2,
        });
        await using var pages = orchestrator.RenderPagesStreamAsync(pdfPath, new[] { 0, 0, 0 })
            .GetAsyncEnumerator();

        Assert.True(await pages.MoveNextAsync());
        Assert.Equal(0, pages.Current.Position);
        var queued = orchestrator.RenderPageAsync(pdfPath, 0);

        await Task.Delay(200);
        Assert.False(queued.IsCompleted);

        Assert.True(await pages.MoveNextAsync());
        Assert.Equal(1, pages.Current.Position);
        Assert.True(await pages.MoveNextAsync());
        Assert.Equal(2, pages.Current.Position);
        Assert.False(await pages.MoveNextAsync());
        Assert.NotEmpty((await queued).Pixels);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task EndingEnumerationEarlyReplacesWorkerAndPreservesFutureRequests()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        await using var orchestrator = await PdfRenderOrchestrator.CreateAsync(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 2,
            WorkerRestartDelays = new[] { TimeSpan.Zero },
        });

        await foreach (var page in orchestrator.RenderPagesStreamAsync(pdfPath, new[] { 0, 0, 0 }))
        {
            Assert.Equal(0, page.Position);
            break;
        }

        var recovered = await orchestrator.RenderPageAsync(pdfPath, 0);
        Assert.NotEmpty(recovered.Pixels);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task CancellationAbortsStreamingAndPreservesFutureRequests()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        await using var orchestrator = await PdfRenderOrchestrator.CreateAsync(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 2,
            WorkerRestartDelays = new[] { TimeSpan.Zero },
        });
        using var cancellation = new CancellationTokenSource();
        await using var pages = orchestrator.RenderPagesStreamAsync(pdfPath, new[] { 0, 0, 0 })
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await pages.MoveNextAsync());
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pages.MoveNextAsync().AsTask());
        Assert.NotEmpty((await orchestrator.RenderPageAsync(pdfPath, 0)).Pixels);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task ValidationAndStreamOwnershipBeginWithEnumeration()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        var input = new MemoryStream(bytes, writable: false);
        using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
        });
        var pages = orchestrator.RenderPagesStreamAsync(input, Array.Empty<int>());

        Assert.True(input.CanRead);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CollectAsync(pages));
        Assert.False(input.CanRead);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public void PageBitmapValidatesConstructorArguments()
    {
        var bitmap = new PdfBitmap(1, 1, 4, new byte[4]);

        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageBitmap(-1, 0, bitmap));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageBitmap(0, -1, bitmap));
        Assert.Throws<ArgumentNullException>(() => new PdfPageBitmap(0, 0, null!));

        var page = new PdfPageBitmap(2, 3, bitmap);
        Assert.Equal(2, page.Position);
        Assert.Equal(3, page.PageIndex);
        Assert.Same(bitmap, page.Bitmap);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkerPathVariable, _originalWorkerPath);
    }

    private static async Task<IReadOnlyList<PdfPageBitmap>> CollectAsync(
        IAsyncEnumerable<PdfPageBitmap> pages)
    {
        var results = new List<PdfPageBitmap>();
        await foreach (var page in pages)
        {
            results.Add(page);
        }

        return results;
    }

    private static string GetAssetPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName);
    }

    private static string GetWorkerPath()
    {
        return typeof(StreamingRenderTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PdfiumRasterWorkerPath")
            .Value!;
    }
}
