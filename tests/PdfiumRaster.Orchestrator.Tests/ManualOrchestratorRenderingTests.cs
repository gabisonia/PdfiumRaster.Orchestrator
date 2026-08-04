using Xunit.Abstractions;

namespace PdfiumRaster.Orchestration.Tests;

public sealed class ManualOrchestratorRenderingTests(ITestOutputHelper output)
{
    private const string ManualPdfVariable = "PDFIUMRASTER_MANUAL_PDF";
    private const string WorkerPathVariable = "PDFIUMRASTER_WORKER_PATH";

    [Fact]
    [Trait("Category", "Local")]
    public async Task ExportEveryPageThroughOrchestratorForVisualInspection()
    {
        var repositoryRoot = GetRepositoryRoot();
        var pdfPath = GetManualPdfPath(repositoryRoot);
        var outputRoot = Path.Combine(
            repositoryRoot,
            "tests",
            "PdfiumRaster.Orchestrator.Tests",
            "ManualOutput");
        var outputDirectory = Path.Combine(
            outputRoot,
            $"{Path.GetFileNameWithoutExtension(pdfPath)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");

        Assert.True(
            File.Exists(pdfPath),
            $"Manual PDF not found at '{pdfPath}'. Run 'make test-manual PDF=/path/to/input.pdf'.");

        Directory.CreateDirectory(outputDirectory);
        var pageCount = PdfImageConverter.GetPageCount(pdfPath);
        Assert.True(pageCount > 0);

        var originalWorkerPath = ConfigureWorkerPath();
        try
        {
            await using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
            {
                WorkerCount = Math.Min(Environment.ProcessorCount, 4),
                QueueCapacity = 42,
                QueueFullMode = PdfRenderQueueFullMode.Wait,
                RequestTimeout = TimeSpan.FromMinutes(2),
            });
            var options = new PdfImageConversionOptions
            {
                Render = new PdfPageRenderOptions
                {
                    Dpi = 144,
                    Flags = PdfRenderFlags.Annot | PdfRenderFlags.LcdText,
                },
                Format = PdfImageOutputFormat.Png,
                Encoding = PdfImageEncodingOptions.Fast,
            };

            var renders = Enumerable.Range(0, pageCount)
                .Select(pageIndex => orchestrator.SavePageAsync(
                    pdfPath,
                    pageIndex,
                    Path.Combine(outputDirectory, $"page-{pageIndex + 1:D4}.png"),
                    options))
                .ToArray();

            await Task.WhenAll(renders);
            await orchestrator.CompleteAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerPathVariable, originalWorkerPath);
        }

        var images = Directory.GetFiles(outputDirectory, "*.png");
        Assert.Equal(pageCount, images.Length);
        Assert.All(images, image => Assert.True(new FileInfo(image).Length > 8));

        output.WriteLine($"Rendered {pageCount} pages from: {pdfPath}");
        output.WriteLine($"Generated images: {outputDirectory}");
    }

    private static string GetManualPdfPath(string repositoryRoot)
    {
        var configuredPath = Environment.GetEnvironmentVariable(ManualPdfVariable);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException(
                "No manual PDF was configured. Run 'make test-manual PDF=/path/to/input.pdf'.");
        }

        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(repositoryRoot, configuredPath));
    }

    private static string? ConfigureWorkerPath()
    {
        var originalWorkerPath = Environment.GetEnvironmentVariable(WorkerPathVariable);
        if (!string.IsNullOrWhiteSpace(originalWorkerPath))
        {
            return originalWorkerPath;
        }

        var workerPath = typeof(ManualOrchestratorRenderingTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PdfiumRasterWorkerPath")
            .Value;
        Environment.SetEnvironmentVariable(WorkerPathVariable, workerPath);
        return originalWorkerPath;
    }

    private static string GetRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PdfiumRaster.Orchestrator.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "PdfiumRaster.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the PdfiumRaster repository root.");
    }
}
