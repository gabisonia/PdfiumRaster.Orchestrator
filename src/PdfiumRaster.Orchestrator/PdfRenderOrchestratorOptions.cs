using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PdfiumRaster.Orchestration;

/// <summary>
/// Configures the isolated PDFium worker processes managed by an orchestrator.
/// </summary>
public sealed class PdfRenderOrchestratorOptions
{
    private static readonly TimeSpan MaximumTimerTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private int _workerCount = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
    private int _queueCapacity = 42;
    private PdfRenderQueueFullMode _queueFullMode;
    private TimeSpan? _requestTimeout;
    private TimeSpan _workerStartupTimeout = TimeSpan.FromSeconds(15);
    private int _maximumBatchPages = 256;
    private long? _maximumInputBytes;
    private long? _maximumBitmapBytes;
    private long? _maximumOutputBytes;
    private string? _temporaryDirectory;
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private TimeSpan[] _workerRestartDelays =
    {
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
    };

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
    /// Gets or sets the optional hard limit for a dispatched request, excluding time spent waiting in the queue.
    /// </summary>
    /// <remarks>
    /// The deadline covers input transfer, PDFium rendering, image encoding, and output transfer. A timed-out operation
    /// terminates and replaces its worker process. The request task completes promptly, but disposal may still wait for
    /// a custom caller stream that does not honor cancellation. The default is <see langword="null" />, which disables
    /// hard timeouts. A configured value must be greater than zero and no more than approximately 49.7 days, which is
    /// the maximum portable timer interval supported by the target frameworks.
    /// </remarks>
    public TimeSpan? RequestTimeout
    {
        get => _requestTimeout;
        set
        {
            if (value.HasValue && (value.Value <= TimeSpan.Zero || value.Value > MaximumTimerTimeout))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"Request timeout must be greater than zero, no more than {MaximumTimerTimeout}, or null.");
            }

            _requestTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum time allowed for a worker process to connect and complete its startup handshake.
    /// </summary>
    /// <remarks>The default is 15 seconds. The value is snapshotted when the orchestrator is constructed.</remarks>
    public TimeSpan WorkerStartupTimeout
    {
        get => _workerStartupTimeout;
        set
        {
            if (value <= TimeSpan.Zero || value > MaximumTimerTimeout)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"Worker startup timeout must be greater than zero and no more than {MaximumTimerTimeout}.");
            }

            _workerStartupTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets the delays before successive attempts to replace a failed worker.
    /// </summary>
    /// <remarks>
    /// The defaults are 250 milliseconds, one second, and four seconds. At least one non-negative delay is required.
    /// The values are copied when assigned and snapshotted again when the orchestrator is constructed.
    /// </remarks>
    public IReadOnlyList<TimeSpan> WorkerRestartDelays
    {
        get => Array.AsReadOnly(_workerRestartDelays);
        set
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (value.Count == 0)
            {
                throw new ArgumentException("At least one worker restart delay is required.", nameof(value));
            }

            var snapshot = new TimeSpan[value.Count];
            for (var index = 0; index < value.Count; index++)
            {
                if (value[index] < TimeSpan.Zero || value[index] > MaximumTimerTimeout)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value[index],
                        $"Worker restart delays must be from zero through {MaximumTimerTimeout}.");
                }

                snapshot[index] = value[index];
            }

            _workerRestartDelays = snapshot;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of pages accepted by one batch request. The default is 256.
    /// </summary>
    public int MaximumBatchPages
    {
        get => _maximumBatchPages;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "Maximum batch pages must be greater than zero.");
            }

            _maximumBatchPages = value;
        }
    }

    /// <summary>
    /// Gets or sets the optional maximum PDF input size per request, in bytes.
    /// </summary>
    /// <remarks>
    /// The limit applies to paths, byte arrays, and streams. The default is <see langword="null" />, which is
    /// unlimited. Path lengths are checked by the worker immediately before opening the file.
    /// </remarks>
    public long? MaximumInputBytes
    {
        get => _maximumInputBytes;
        set => _maximumInputBytes = ValidateOptionalByteLimit(value);
    }

    /// <summary>
    /// Gets or sets the optional maximum uncompressed pixel-buffer size of each returned bitmap, in bytes.
    /// </summary>
    /// <remarks>The default is <see langword="null" />, which is unlimited.</remarks>
    public long? MaximumBitmapBytes
    {
        get => _maximumBitmapBytes;
        set => _maximumBitmapBytes = ValidateOptionalByteLimit(value);
    }

    /// <summary>
    /// Gets or sets the optional maximum total response size per request, in bytes.
    /// </summary>
    /// <remarks>
    /// The limit covers returned bitmap pixels and encoded stream or file outputs. In a multi-file batch, files
    /// completed before a later item exceeds the aggregate limit remain in place. The default is
    /// <see langword="null" />, which is unlimited.
    /// </remarks>
    public long? MaximumOutputBytes
    {
        get => _maximumOutputBytes;
        set => _maximumOutputBytes = ValidateOptionalByteLimit(value);
    }

    /// <summary>
    /// Gets or sets the optional parent directory used for private worker temporary directories.
    /// </summary>
    /// <remarks>
    /// The directory is created if necessary and the value is converted to an absolute path when assigned. The
    /// default is <see langword="null" />, which uses the operating system temporary directory.
    /// </remarks>
    public string? TemporaryDirectory
    {
        get => _temporaryDirectory;
        set
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Temporary directory cannot be whitespace.", nameof(value));
            }

            _temporaryDirectory = value is null ? null : Path.GetFullPath(value);
        }
    }

    /// <summary>
    /// Gets or sets the factory used to create structured diagnostic loggers.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="NullLoggerFactory.Instance" />, which disables logging. The factory is snapshotted
    /// when the orchestrator is constructed. Logs never include PDF or image paths, passwords, pipe names, handshake
    /// tokens, worker standard error, or document payloads.
    /// </remarks>
    public ILoggerFactory LoggerFactory
    {
        get => _loggerFactory;
        set => _loggerFactory = value ?? throw new ArgumentNullException(nameof(value));
    }

    private static long? ValidateOptionalByteLimit(long? value)
    {
        if (value.HasValue && value.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value,
                "Byte limits must be greater than zero or null.");
        }

        return value;
    }
}
