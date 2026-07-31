namespace PdfiumRaster.Orchestration;

/// <summary>
/// Configures the isolated PDFium worker processes managed by an orchestrator.
/// </summary>
public sealed class PdfRenderOrchestratorOptions
{
    private int _workerCount = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
    private int _queueCapacity = 42;
    private PdfRenderQueueFullMode _queueFullMode;
    private TimeSpan? _requestTimeout;

    /// <summary>
    /// Gets or sets the number of worker processes and therefore the maximum number of simultaneous PDFium operations.
    /// </summary>
    /// <remarks>
    /// The default is the smaller of four and the logical processor count. The value cannot exceed the logical
    /// processor count reported to the application. Each worker has an independent native PDFium runtime and memory
    /// footprint.
    /// </remarks>
    public int WorkerCount
    {
        get => _workerCount;
        set
        {
            var maximum = Math.Max(1, Environment.ProcessorCount);
            if (value <= 0 || value > maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"Worker count must be from 1 to {maximum}.");
            }

            _workerCount = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of accepted requests waiting for an available worker. The default is 42.
    /// </summary>
    public int QueueCapacity
    {
        get => _queueCapacity;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "Queue capacity must be greater than zero.");
            }

            _queueCapacity = value;
        }
    }

    /// <summary>
    /// Gets or sets how submissions behave when the bounded queue is full. The default is
    /// <see cref="PdfRenderQueueFullMode.Wait" />.
    /// </summary>
    public PdfRenderQueueFullMode QueueFullMode
    {
        get => _queueFullMode;
        set
        {
            if (!Enum.IsDefined(typeof(PdfRenderQueueFullMode), value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "Queue full mode must be a defined value.");
            }

            _queueFullMode = value;
        }
    }

    /// <summary>
    /// Gets or sets the optional hard limit for active worker processing, excluding time spent waiting in the queue.
    /// </summary>
    /// <remarks>
    /// A timed-out operation terminates and replaces its worker process. The default is <see langword="null" />, which
    /// disables hard timeouts.
    /// </remarks>
    public TimeSpan? RequestTimeout
    {
        get => _requestTimeout;
        set
        {
            if (value.HasValue && value.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "Request timeout must be greater than zero or null.");
            }

            _requestTimeout = value;
        }
    }
}
