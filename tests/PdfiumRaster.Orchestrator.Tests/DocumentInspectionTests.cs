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
        await using var orchestrator = CreateOrchestrator();

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
    public async Task UnifiedInspectionReturnsPageCountAndImmutableOrderedSizes()
    {
        var pdfPath = GetAssetPath("smoke.pdf");
        var bytes = await File.ReadAllBytesAsync(pdfPath);
        var expectedSizes = PdfImageConverter.GetPageSizes(pdfPath);
        using var stream = new MemoryStream(bytes, writable: false);
        await using var orchestrator = CreateOrchestrator();

        var pathInfo = await orchestrator.InspectDocumentAsync(pdfPath);
        var byteInfo = await orchestrator.InspectDocumentAsync(bytes);
        var streamInfo = await orchestrator.InspectDocumentAsync(stream, leaveOpen: true);
        await orchestrator.CompleteAsync();

        Assert.Equal(expectedSizes.Count, pathInfo.PageCount);
        Assert.Equal(pathInfo.PageCount, pathInfo.PageSizes.Count);
        AssertPageSizes(expectedSizes, pathInfo.PageSizes);
        AssertPageSizes(expectedSizes, byteInfo.PageSizes);
        AssertPageSizes(expectedSizes, streamInfo.PageSizes);
        Assert.True(stream.CanRead);
        Assert.IsAssignableFrom<IReadOnlyList<PdfPageSize>>(pathInfo.PageSizes);
        Assert.False(pathInfo.PageSizes is IList<PdfPageSize> { IsReadOnly: false });
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
        await using var orchestrator = CreateOrchestrator();

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
        await using var limited = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
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

        await using var orchestrator = CreateOrchestrator();
        var missingPath = Path.Combine(AppContext.BaseDirectory, $"missing-{Guid.NewGuid():N}.pdf");
        await Assert.ThrowsAsync<PdfWorkerRemoteException>(
            () => orchestrator.GetPageCountAsync(missingPath));
        Assert.True(await orchestrator.GetPageCountAsync(pdfPath) > 0);
        await orchestrator.CompleteAsync();
    }

    [Fact]
    public async Task InspectionValidatesPublicInputs()
    {
        await using var orchestrator = CreateOrchestrator();

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
        Assert.Throws<ArgumentException>(() =>
        {
            _ = orchestrator.InspectDocumentAsync(Array.Empty<byte>());
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
