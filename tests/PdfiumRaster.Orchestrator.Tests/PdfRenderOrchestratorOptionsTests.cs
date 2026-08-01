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
}
