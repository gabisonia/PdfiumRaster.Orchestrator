using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PdfiumRaster.Orchestration.Tests;

public sealed class HostingIntegrationTests : IDisposable
{
    private const string WorkerPathVariable = "PDFIUMRASTER_WORKER_PATH";
    private readonly string? _originalWorkerPath;

    public HostingIntegrationTests()
    {
        _originalWorkerPath = Environment.GetEnvironmentVariable(WorkerPathVariable);
        if (string.IsNullOrWhiteSpace(_originalWorkerPath))
        {
            Environment.SetEnvironmentVariable(WorkerPathVariable, GetWorkerPath());
        }
    }

    [Fact]
    public async Task RegistrationCreatesOneConfiguredSingletonAndOneHostedService()
    {
        var loggerFactory = new TrackingLoggerFactory();
        var firstConfigurationCalls = 0;
        var secondConfigurationCalls = 0;
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddPdfiumRasterOrchestrator(options =>
        {
            firstConfigurationCalls++;
            Assert.Same(loggerFactory, options.LoggerFactory);
            options.WorkerCount = 1;
            options.QueueCapacity = 3;
        });
        services.AddPdfiumRasterOrchestrator(_ => secondConfigurationCalls++);

        await using var serviceProvider = services.BuildServiceProvider();
        var first = serviceProvider.GetRequiredService<PdfRenderOrchestrator>();
        var second = serviceProvider.GetRequiredService<PdfRenderOrchestrator>();
        var hostedService = Assert.Single(
            serviceProvider.GetServices<IHostedService>().OfType<PdfiumRasterOrchestratorHostedService>());

        Assert.Same(first, second);
        Assert.Equal(1, firstConfigurationCalls);
        Assert.Equal(0, secondConfigurationCalls);
        Assert.True(loggerFactory.CreateLoggerCalls > 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => first.RenderPageAsync(GetAssetPath("smoke.pdf"), 0));

        await hostedService.StartAsync(CancellationToken.None);
        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostedShutdownDrainsAcceptedRendering()
    {
        var services = new ServiceCollection();
        services.AddPdfiumRasterOrchestrator(options =>
        {
            options.WorkerCount = 1;
            options.QueueCapacity = 2;
        });

        await using var serviceProvider = services.BuildServiceProvider();
        var hostedService = serviceProvider.GetServices<IHostedService>().Single();
        var orchestrator = serviceProvider.GetRequiredService<PdfRenderOrchestrator>();
        await hostedService.StartAsync(CancellationToken.None);
        var rendering = orchestrator.RenderPageAsync(GetAssetPath("smoke.pdf"), 0);

        await hostedService.StopAsync(CancellationToken.None);

        var bitmap = await rendering;
        Assert.NotEmpty(bitmap.Pixels);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.RenderPageAsync(GetAssetPath("smoke.pdf"), 0));
    }

    [Fact]
    public async Task CanceledHostShutdownCancelsAcceptedRendering()
    {
        var services = new ServiceCollection();
        services.AddPdfiumRasterOrchestrator(options =>
        {
            options.WorkerCount = 1;
            options.QueueCapacity = 2;
        });

        await using var serviceProvider = services.BuildServiceProvider();
        var hostedService = serviceProvider.GetServices<IHostedService>().Single();
        var orchestrator = serviceProvider.GetRequiredService<PdfRenderOrchestrator>();
        await hostedService.StartAsync(CancellationToken.None);
        using var blockingInput = new BlockingReadStream(await File.ReadAllBytesAsync(GetAssetPath("smoke.pdf")));
        var active = orchestrator.RenderPageAsync(blockingInput, 0, leaveOpen: true);
        await blockingInput.WaitUntilReadAsync();
        var queued = orchestrator.RenderPageAsync(GetAssetPath("smoke.pdf"), 0);
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();

        var stopping = hostedService.StopAsync(shutdown.Token);
        blockingInput.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await stopping;
        Assert.True(blockingInput.CanRead);
    }

    [Fact]
    public async Task HealthCheckReportsHealthyThenUnhealthyAfterShutdown()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddPdfiumRasterOrchestrator(options => options.WorkerCount = 1);
        services.AddHealthChecks().AddPdfiumRasterOrchestrator(tags: new[] { "ready" });

        await using var serviceProvider = services.BuildServiceProvider();
        var healthChecks = serviceProvider.GetRequiredService<HealthCheckService>();
        var starting = await healthChecks.CheckHealthAsync();

        Assert.Equal(HealthStatus.Degraded, starting.Status);
        Assert.Contains("starting", starting.Entries["pdfiumraster-orchestrator"].Description);

        await serviceProvider.GetServices<IHostedService>()
            .OfType<PdfiumRasterOrchestratorHostedService>()
            .Single()
            .StartAsync(CancellationToken.None);
        var healthy = await healthChecks.CheckHealthAsync();
        var healthyEntry = healthy.Entries["pdfiumraster-orchestrator"];

        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        Assert.Equal(1, healthyEntry.Data["available_workers"]);
        Assert.Equal(1, healthyEntry.Data["total_workers"]);
        Assert.Contains("all workers are available", healthyEntry.Description);

        await serviceProvider.GetRequiredService<PdfRenderOrchestrator>().CompleteAsync();
        var stopped = await healthChecks.CheckHealthAsync();

        Assert.Equal(HealthStatus.Unhealthy, stopped.Status);
        Assert.Contains("stopped accepting requests", stopped.Entries["pdfiumraster-orchestrator"].Description);
    }

    [Fact]
    public void RegistrationValidatesArguments()
    {
        IServiceCollection? services = null;
        Assert.Throws<ArgumentNullException>(() => services!.AddPdfiumRasterOrchestrator());

        var validServices = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(
            () => validServices.AddPdfiumRasterOrchestrator(null!));

        IHealthChecksBuilder? healthBuilder = null;
        Assert.Throws<ArgumentNullException>(() => healthBuilder!.AddPdfiumRasterOrchestrator());

        var validHealthBuilder = validServices.AddHealthChecks();
        Assert.Throws<ArgumentException>(
            () => validHealthBuilder.AddPdfiumRasterOrchestrator(" "));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkerPathVariable, _originalWorkerPath);
    }

    private static string GetAssetPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName);
    }

    private static string GetWorkerPath()
    {
        return typeof(HostingIntegrationTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PdfiumRasterWorkerPath")
            .Value!;
    }

    private sealed class TrackingLoggerFactory : ILoggerFactory
    {
        internal int CreateLoggerCalls { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            CreateLoggerCalls++;
            return NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingReadStream : MemoryStream
    {
        private readonly TaskCompletionSource<object?> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _firstRead = 1;

        internal BlockingReadStream(byte[] bytes)
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
