namespace PdfiumRaster.Orchestration.Tests;

public sealed class PdfRenderOrchestratorOptionsTests
{
    [Fact]
    public void DefaultsAreBoundedByProcessorCount()
    {
        var options = new PdfRenderOrchestratorOptions();

        Assert.InRange(options.WorkerCount, 1, Math.Max(1, Environment.ProcessorCount));
        Assert.Equal(42, options.QueueCapacity);
        Assert.Equal(PdfRenderQueueFullMode.Wait, options.QueueFullMode);
        Assert.Null(options.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), options.WorkerStartupTimeout);
        Assert.Equal(
            new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4) },
            options.WorkerRestartDelays);
    }

    [Fact]
    public void WorkerCountRejectsValuesOutsideProcessorLimit()
    {
        var options = new PdfRenderOrchestratorOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.WorkerCount = 0);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.WorkerCount = Math.Max(1, Environment.ProcessorCount) + 1);
    }

    [Fact]
    public void RequestTimeoutMustBePositiveOrNull()
    {
        var options = new PdfRenderOrchestratorOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.RequestTimeout = TimeSpan.Zero);
        options.RequestTimeout = TimeSpan.FromSeconds(1);
        Assert.Equal(TimeSpan.FromSeconds(1), options.RequestTimeout);
        options.RequestTimeout = null;
        Assert.Null(options.RequestTimeout);
    }

    [Fact]
    public void QueueCapacityAndFullModeRejectInvalidValues()
    {
        var options = new PdfRenderOrchestratorOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.QueueCapacity = 0);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.QueueFullMode = (PdfRenderQueueFullMode)int.MaxValue);
    }

    [Fact]
    public void WorkerStartupTimeoutMustBeSupportedAndPositive()
    {
        var options = new PdfRenderOrchestratorOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.WorkerStartupTimeout = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.WorkerStartupTimeout = TimeSpan.MaxValue);
        options.WorkerStartupTimeout = TimeSpan.FromSeconds(2);
        Assert.Equal(TimeSpan.FromSeconds(2), options.WorkerStartupTimeout);
    }

    [Fact]
    public void WorkerRestartDelaysAreValidatedAndCopied()
    {
        var options = new PdfRenderOrchestratorOptions();
        var delays = new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(10) };

        options.WorkerRestartDelays = delays;
        delays[0] = TimeSpan.FromDays(1);

        Assert.Equal(TimeSpan.Zero, options.WorkerRestartDelays[0]);
        Assert.Throws<ArgumentNullException>(() => options.WorkerRestartDelays = null!);
        Assert.Throws<ArgumentException>(() => options.WorkerRestartDelays = Array.Empty<TimeSpan>());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.WorkerRestartDelays = new[] { TimeSpan.FromMilliseconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.WorkerRestartDelays = new[] { TimeSpan.MaxValue });
    }
}
