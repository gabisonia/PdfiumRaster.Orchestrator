namespace PdfiumRaster.Orchestration.Tests;

public sealed class DocumentInspectionTests : IDisposable
{
    private const string WorkerPathVariable = "PDFIUMRASTER_WORKER_PATH";
    private readonly string? _originalWorkerPath;

    public DocumentInspectionTests()
    {
        _originalWorkerPath = Environment.GetEnvironmentVariable(WorkerPathVariable);
        var workerPath = typeof(DocumentInspectionTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PdfiumRasterWorkerPath")
            .Value;
        Environment.SetEnvironmentVariable(WorkerPathVariable, workerPath);
    }

    [Fact]
    public async Task PathByteAndStreamInspectionsMatchInProcessResults()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        var bytes = await File.ReadAllBytesAsync(pdfPath);
        var expectedCount = PdfImageConverter.GetPageCount(pdfPath);
        var expectedSizes = PdfImageConverter.GetPageSizes(pdfPath);
        using var countStream = new MemoryStream(bytes, writable: false);
        using var sizesStream = new MemoryStream(bytes, writable: false);
        using var orchestrator = CreateOrchestrator();

        var pathCount = await orchestrator.GetPageCountAsync(pdfPath);
        var byteCount = await orchestrator.GetPageCountAsync(bytes);
        var streamCount = await orchestrator.GetPageCountAsync(countStream, leaveOpen: true);
        var pathSizes = await orchestrator.GetPageSizesAsync(pdfPath);
        var byteSizes = await orchestrator.GetPageSizesAsync(bytes);
        var streamSizes = await orchestrator.GetPageSizesAsync(sizesStream, leaveOpen: true);
        await orchestrator.CompleteAsync();

        Assert.Equal(expectedCount, pathCount);
        Assert.Equal(expectedCount, byteCount);
        Assert.Equal(expectedCount, streamCount);
        AssertPageSizes(expectedSizes, pathSizes);
        AssertPageSizes(expectedSizes, byteSizes);
        AssertPageSizes(expectedSizes, streamSizes);
        Assert.True(countStream.CanRead);
        Assert.True(sizesStream.CanRead);
    }

    [Fact]
    public async Task OwnedInspectionStreamsAreDisposedAfterCompletionAndCancellation()
    {
        var bytes = await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf"));
        var countStream = new MemoryStream(bytes, writable: false);
        var sizesStream = new MemoryStream(bytes, writable: false);
        var canceledStream = new MemoryStream(bytes, writable: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var orchestrator = CreateOrchestrator();

        await orchestrator.GetPageCountAsync(countStream);
        await orchestrator.GetPageSizesAsync(sizesStream);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestrator.GetPageCountAsync(canceledStream, cancellationToken: cancellation.Token));
        await orchestrator.CompleteAsync();

        Assert.False(countStream.CanRead);
        Assert.False(sizesStream.CanRead);
        Assert.False(canceledStream.CanRead);
    }

    [Fact]
    public async Task InspectionEnforcesInputLimitsAndWorkerRemainsUsableAfterRemoteFailure()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        var bytes = await File.ReadAllBytesAsync(pdfPath);
        using var limited = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 2,
            MaximumInputBytes = bytes.Length - 1,
        });

        var byteLimit = Assert.Throws<PdfRenderResourceLimitException>(() =>
        {
            _ = limited.GetPageCountAsync(bytes);
        });
        Assert.Equal("input bytes", byteLimit.Resource);
        var pathLimit = await Assert.ThrowsAsync<PdfRenderResourceLimitException>(
            () => limited.GetPageSizesAsync(pdfPath));
        Assert.Equal("input bytes", pathLimit.Resource);
        await limited.CompleteAsync();

        using var orchestrator = CreateOrchestrator();
        var missingPath = Path.Combine(AppContext.BaseDirectory, $"missing-{Guid.NewGuid():N}.pdf");
        await Assert.ThrowsAsync<PdfWorkerRemoteException>(
            () => orchestrator.GetPageCountAsync(missingPath));
        Assert.True(await orchestrator.GetPageCountAsync(pdfPath) > 0);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task InspectionValidatesPublicInputs()
    {
        using var orchestrator = CreateOrchestrator();

        Assert.Throws<ArgumentException>(() =>
        {
            _ = orchestrator.GetPageCountAsync(" ");
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = orchestrator.GetPageCountAsync((byte[])null!);
        });
        Assert.Throws<ArgumentException>(() =>
        {
            _ = orchestrator.GetPageSizesAsync(Array.Empty<byte>());
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = orchestrator.GetPageSizesAsync((Stream)null!);
        });
        using var unreadable = new MemoryStream();
        unreadable.Dispose();
        Assert.Throws<ArgumentException>(() =>
        {
            _ = orchestrator.GetPageCountAsync(unreadable);
        });
        await orchestrator.CompleteAsync();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkerPathVariable, _originalWorkerPath);
    }

    private static PdfRenderOrchestrator CreateOrchestrator()
    {
        return new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 8,
        });
    }

    private static string GetAssetPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName);
    }

    private static void AssertPageSizes(
        IReadOnlyList<PdfPageSize> expected,
        IReadOnlyList<PdfPageSize> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Width, actual[index].Width);
            Assert.Equal(expected[index].Height, actual[index].Height);
        }
    }
}
