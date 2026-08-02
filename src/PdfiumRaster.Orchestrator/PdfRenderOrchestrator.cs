using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace PdfiumRaster.Orchestration;

/// <summary>
/// Coordinates PDF rendering requests across isolated local PDFium worker processes.
/// </summary>
/// <remarks>
/// Page indexes are zero-based. Each worker owns an independent PDFium runtime, so up to
/// <see cref="PdfRenderOrchestratorOptions.WorkerCount" /> native operations can run simultaneously. Workers use the
/// caller's operating-system identity and filesystem permissions and are not a security sandbox.
/// </remarks>
public sealed class PdfRenderOrchestrator : IDisposable
{
    private readonly Channel<OrchestrationJob> _queue;
    private readonly ILogger _logger;
    private readonly PdfRenderQueueFullMode _queueFullMode;
    private readonly TimeSpan? _requestTimeout;
    private readonly TimeSpan[] _workerRestartDelays;
    private readonly int _maximumBatchPages;
    private readonly long? _maximumInputBytes;
    private readonly long? _maximumBitmapBytes;
    private readonly long? _maximumOutputBytes;
    private readonly string? _temporaryDirectory;
    private readonly WorkerSlot[] _workers;
    private readonly Task _completion;
    private readonly List<Task> _detachedOperations = new();
    private readonly object _detachedOperationsSync = new();
    private Exception? _terminalError;
    private long _nextRequestId;
    private int _accepting = 1;
    private int _cancelRequested;
    private int _disposed;

    /// <summary>
    /// Starts a fixed set of isolated PDFium workers.
    /// </summary>
    /// <param name="options">Optional worker-count, queue, backpressure, and timeout settings.</param>
    /// <exception cref="PlatformNotSupportedException">No bundled worker is available for the current platform.</exception>
    /// <exception cref="PdfWorkerStartupException">One or more workers could not start.</exception>
    public PdfRenderOrchestrator(PdfRenderOrchestratorOptions? options = null)
    {
        options ??= new PdfRenderOrchestratorOptions();
        _logger = options.LoggerFactory.CreateLogger<PdfRenderOrchestrator>();
        _queueFullMode = options.QueueFullMode;
        _requestTimeout = options.RequestTimeout;
        _workerRestartDelays = options.WorkerRestartDelays.ToArray();
        _maximumBatchPages = options.MaximumBatchPages;
        _maximumInputBytes = options.MaximumInputBytes;
        _maximumBitmapBytes = options.MaximumBitmapBytes;
        _maximumOutputBytes = options.MaximumOutputBytes;
        _temporaryDirectory = options.TemporaryDirectory;
        if (_temporaryDirectory is not null)
        {
            Directory.CreateDirectory(_temporaryDirectory);
        }
        var workerStartupTimeout = options.WorkerStartupTimeout;
        _queue = Channel.CreateBounded<OrchestrationJob>(new BoundedChannelOptions(options.QueueCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = options.WorkerCount == 1,
            SingleWriter = false,
        });

        WorkerExecutableResolver.AssertSupportedPlatform();
        _workers = new WorkerSlot[options.WorkerCount];
        try
        {
            var starts = new Task[options.WorkerCount];
            for (var index = 0; index < options.WorkerCount; index++)
            {
                var worker = new WorkerSlot(index, workerStartupTimeout, _temporaryDirectory, _logger);
                _workers[index] = worker;
                starts[index] = worker.StartAsync();
            }

            Task.WhenAll(starts).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            foreach (var worker in _workers)
            {
                worker?.Kill();
                worker?.Dispose();
            }

            if (exception is PlatformNotSupportedException or PdfWorkerStartupException)
            {
                throw;
            }

            throw new PdfWorkerStartupException("The PDFium orchestrator could not start.", exception);
        }

        var workerTasks = new Task[_workers.Length];
        for (var index = 0; index < _workers.Length; index++)
        {
            var worker = _workers[index];
            workerTasks[index] = Task.Run(() => ProcessQueueAsync(worker));
        }

        _completion = CompleteWorkersAsync(workerTasks);
        PdfRenderOrchestratorEventSource.Log.OrchestratorStarted(options.WorkerCount, options.QueueCapacity);
        _logger.OrchestratorStarted(options.WorkerCount, options.QueueCapacity);
    }

    /// <summary>
    /// Queues a zero-based page from a PDF file and returns an independently owned bitmap.
    /// </summary>
    /// <param name="pdfPath">PDF path opened by a local worker when this request is dispatched.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="options">Optional rendering and color-conversion settings, snapshotted during submission.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that produces a caller-owned BGRA bitmap.</returns>
    public Task<PdfBitmap> RenderPageAsync(
        string pdfPath,
        int pageIndex,
        PdfImageConversionOptions? options = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitRender(CreatePathSource(pdfPath), pageIndex, options, password, cancellationToken);
    }

    /// <summary>
    /// Queues a zero-based page from PDF bytes and returns an independently owned bitmap.
    /// </summary>
    /// <param name="pdfBytes">PDF bytes that must not be modified until the returned task completes.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="options">Optional rendering and color-conversion settings, snapshotted during submission.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that produces a caller-owned BGRA bitmap.</returns>
    /// <remarks>The bytes are transferred to a worker in bounded chunks and remain caller-owned.</remarks>
    public Task<PdfBitmap> RenderPageAsync(
        byte[] pdfBytes,
        int pageIndex,
        PdfImageConversionOptions? options = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitRender(CreateByteSource(pdfBytes), pageIndex, options, password, cancellationToken);
    }

    /// <summary>
    /// Queues a zero-based page from a PDF stream and returns an independently owned bitmap.
    /// </summary>
    /// <param name="pdfStream">Readable stream that must remain usable and unmodified until completion.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="options">Optional rendering and color-conversion settings, snapshotted during submission.</param>
    /// <param name="leaveOpen">Whether to leave the PDF stream open after completion, cancellation, or rejection.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that produces a caller-owned BGRA bitmap.</returns>
    /// <remarks>
    /// The stream is transferred in chunks and spooled to a worker-owned temporary file for random access. Unless
    /// <paramref name="leaveOpen" /> is <see langword="true" />, the orchestrator assumes ownership when this method is
    /// called and disposes the stream after completion, cancellation, validation failure, or queue rejection.
    /// </remarks>
    public Task<PdfBitmap> RenderPageAsync(
        Stream pdfStream,
        int pageIndex,
        PdfImageConversionOptions? options = null,
        bool leaveOpen = false,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitRender(CreateStreamSource(pdfStream, leaveOpen), pageIndex, options, password, cancellationToken);
    }

    /// <summary>
    /// Queues multiple zero-based pages from one PDF file and renders them after opening the document once.
    /// </summary>
    /// <param name="pdfPath">PDF path opened once by a local worker.</param>
    /// <param name="pageIndexes">Non-empty page sequence. Results preserve this order, including duplicates.</param>
    /// <param name="options">Optional rendering and color-conversion settings, snapshotted during submission.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>Caller-owned BGRA bitmaps in the same order as <paramref name="pageIndexes" />.</returns>
    public Task<IReadOnlyList<PdfBitmap>> RenderPagesAsync(
        string pdfPath,
        IReadOnlyList<int> pageIndexes,
        PdfImageConversionOptions? options = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitBatchRender(CreatePathSource(pdfPath), pageIndexes, options, password, cancellationToken);
    }

    /// <summary>
    /// Queues multiple zero-based pages from PDF bytes and renders them after transferring and opening the PDF once.
    /// </summary>
    /// <param name="pdfBytes">PDF bytes that must not be modified until completion.</param>
    /// <param name="pageIndexes">Non-empty page sequence. Results preserve this order, including duplicates.</param>
    /// <param name="options">Optional rendering and color-conversion settings, snapshotted during submission.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>Caller-owned BGRA bitmaps in the same order as <paramref name="pageIndexes" />.</returns>
    public Task<IReadOnlyList<PdfBitmap>> RenderPagesAsync(
        byte[] pdfBytes,
        IReadOnlyList<int> pageIndexes,
        PdfImageConversionOptions? options = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitBatchRender(CreateByteSource(pdfBytes), pageIndexes, options, password, cancellationToken);
    }

    /// <summary>
    /// Queues multiple zero-based pages from a PDF stream and renders them after transferring and opening the PDF once.
    /// </summary>
    /// <param name="pdfStream">Readable stream that must remain usable and unmodified until completion.</param>
    /// <param name="pageIndexes">Non-empty page sequence. Results preserve this order, including duplicates.</param>
    /// <param name="options">Optional rendering and color-conversion settings, snapshotted during submission.</param>
    /// <param name="leaveOpen">Whether to leave the PDF stream open after the request finishes.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>Caller-owned BGRA bitmaps in the same order as <paramref name="pageIndexes" />.</returns>
    public Task<IReadOnlyList<PdfBitmap>> RenderPagesAsync(
        Stream pdfStream,
        IReadOnlyList<int> pageIndexes,
        PdfImageConversionOptions? options = null,
        bool leaveOpen = false,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitBatchRender(CreateStreamSource(pdfStream, leaveOpen), pageIndexes, options, password,
            cancellationToken);
    }

    /// <summary>Saves multiple pages from one PDF file after opening the document once.</summary>
    /// <param name="pdfPath">PDF path opened once by a local worker.</param>
    /// <param name="outputs">Non-empty page-to-file mappings processed in order.</param>
    /// <param name="options">Optional rendering, format, and encoding settings.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that completes after all image files have been committed.</returns>
    public Task SavePagesAsync(
        string pdfPath,
        IReadOnlyList<PdfPageFileOutput> outputs,
        PdfImageConversionOptions? options = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitBatchSave(CreatePathSource(pdfPath), outputs, options, password, cancellationToken);
    }

    /// <summary>Saves multiple pages from PDF bytes after transferring and opening the document once.</summary>
    /// <param name="pdfBytes">PDF bytes that must not be modified until completion.</param>
    /// <param name="outputs">Non-empty page-to-file mappings processed in order.</param>
    /// <param name="options">Optional rendering, format, and encoding settings.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that completes after all image files have been committed.</returns>
    public Task SavePagesAsync(
        byte[] pdfBytes,
        IReadOnlyList<PdfPageFileOutput> outputs,
        PdfImageConversionOptions? options = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitBatchSave(CreateByteSource(pdfBytes), outputs, options, password, cancellationToken);
    }

    /// <summary>Saves multiple pages from a PDF stream after transferring and opening the document once.</summary>
    /// <param name="pdfStream">Readable stream that must remain usable and unmodified until completion.</param>
    /// <param name="outputs">Non-empty page-to-file mappings processed in order.</param>
    /// <param name="options">Optional rendering, format, and encoding settings.</param>
    /// <param name="leaveOpen">Whether to leave the PDF stream open after the request finishes.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that completes after all image files have been committed.</returns>
    public Task SavePagesAsync(
        Stream pdfStream,
        IReadOnlyList<PdfPageFileOutput> outputs,
        PdfImageConversionOptions? options = null,
        bool leaveOpen = false,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitBatchSave(CreateStreamSource(pdfStream, leaveOpen), outputs, options, password,
            cancellationToken);
    }

    /// <summary>
    /// Queues a zero-based page from a PDF file and saves it to an image file.
    /// </summary>
    /// <param name="pdfPath">PDF path opened by a local worker.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="imagePath">Destination image path written by the worker.</param>
    /// <param name="options">Optional rendering, format, and encoding settings, snapshotted during submission.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that completes after the image has been written.</returns>
    public Task SavePageAsync(
        string pdfPath,
        int pageIndex,
        string imagePath,
        PdfImageConversionOptions? options = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitSave(CreatePathSource(pdfPath), pageIndex, CreatePathTarget(imagePath), options, password,
            cancellationToken);
    }

    /// <summary>
    /// Queues a zero-based page from a PDF file and writes it to a caller-owned image stream.
    /// </summary>
    /// <param name="pdfPath">PDF path opened by a local worker.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="imageStream">Writable destination stream, which remains open.</param>
    /// <param name="options">Optional rendering, format, and encoding settings, snapshotted during submission.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that completes after the image has been written.</returns>
    public Task SavePageAsync(
        string pdfPath,
        int pageIndex,
        Stream imageStream,
        PdfImageConversionOptions? options = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitSave(CreatePathSource(pdfPath), pageIndex, CreateStreamTarget(imageStream), options, password,
            cancellationToken);
    }

    /// <summary>
    /// Queues a zero-based page from PDF bytes and saves it to an image file.
    /// </summary>
    /// <param name="pdfBytes">PDF bytes that must not be modified until completion.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="imagePath">Destination image path written by the worker.</param>
    /// <param name="options">Optional rendering, format, and encoding settings, snapshotted during submission.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that completes after the image has been written.</returns>
    public Task SavePageAsync(
        byte[] pdfBytes,
        int pageIndex,
        string imagePath,
        PdfImageConversionOptions? options = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitSave(CreateByteSource(pdfBytes), pageIndex, CreatePathTarget(imagePath), options, password,
            cancellationToken);
    }

    /// <summary>
    /// Queues a zero-based page from PDF bytes and writes it to a caller-owned image stream.
    /// </summary>
    /// <param name="pdfBytes">PDF bytes that must not be modified until completion.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="imageStream">Writable destination stream, which remains open.</param>
    /// <param name="options">Optional rendering, format, and encoding settings, snapshotted during submission.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that completes after the image has been written.</returns>
    public Task SavePageAsync(
        byte[] pdfBytes,
        int pageIndex,
        Stream imageStream,
        PdfImageConversionOptions? options = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitSave(CreateByteSource(pdfBytes), pageIndex, CreateStreamTarget(imageStream), options, password,
            cancellationToken);
    }

    /// <summary>
    /// Queues a zero-based page from a PDF stream and saves it to an image file.
    /// </summary>
    /// <param name="pdfStream">Readable stream that must remain usable and unmodified until completion.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="imagePath">Destination image path written by the worker.</param>
    /// <param name="options">Optional rendering, format, and encoding settings, snapshotted during submission.</param>
    /// <param name="leaveOpen">Whether to leave the PDF stream open after completion, cancellation, or rejection.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that completes after the image has been written.</returns>
    /// <remarks>
    /// Unless <paramref name="leaveOpen" /> is <see langword="true" />, the orchestrator assumes ownership when this
    /// method is called and disposes the PDF stream after completion, cancellation, validation failure, or rejection.
    /// </remarks>
    public Task SavePageAsync(
        Stream pdfStream,
        int pageIndex,
        string imagePath,
        PdfImageConversionOptions? options = null,
        bool leaveOpen = false,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return SubmitSave(CreateStreamSource(pdfStream, leaveOpen), pageIndex, CreatePathTarget(imagePath), options,
            password, cancellationToken);
    }

    /// <summary>
    /// Queues a zero-based page from a PDF stream and writes it to a separate caller-owned image stream.
    /// </summary>
    /// <param name="pdfStream">Readable stream that must remain usable and unmodified until completion.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="imageStream">Writable destination stream, which remains open.</param>
    /// <param name="options">Optional rendering, format, and encoding settings, snapshotted during submission.</param>
    /// <param name="leaveOpen">Whether to leave the PDF stream open after completion, cancellation, or rejection.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that completes after the image has been written.</returns>
    /// <remarks>
    /// Unless <paramref name="leaveOpen" /> is <see langword="true" />, the orchestrator assumes ownership when this
    /// method is called and disposes the PDF stream after completion, cancellation, validation failure, or rejection.
    /// The image output stream always remains caller-owned.
    /// </remarks>
    public Task SavePageAsync(
        Stream pdfStream,
        int pageIndex,
        Stream imageStream,
        PdfImageConversionOptions? options = null,
        bool leaveOpen = false,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (ReferenceEquals(pdfStream, imageStream))
        {
            throw new ArgumentException("PDF input and image output must be different streams.", nameof(imageStream));
        }

        return SubmitSave(CreateStreamSource(pdfStream, leaveOpen), pageIndex, CreateStreamTarget(imageStream), options,
            password, cancellationToken);
    }

    /// <summary>
    /// Stops accepting submissions and asynchronously waits for all accepted requests and workers to finish.
    /// </summary>
    /// <returns>A task that completes after accepted jobs drain and worker processes exit.</returns>
    public Task CompleteAsync()
    {
        BeginShutdown(cancel: false);
        return _completion;
    }

    /// <summary>
    /// Stops accepting submissions, cancels queued requests, and waits for active uninterruptible work to finish.
    /// </summary>
    /// <returns>A task that completes after workers exit.</returns>
    public Task CancelAsync()
    {
        BeginShutdown(cancel: true);
        return _completion;
    }

    /// <summary>
    /// Cancels queued requests, waits for active uninterruptible work, and stops all worker processes.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        BeginShutdown(cancel: true);
        _completion.GetAwaiter().GetResult();
    }

    private Task<PdfBitmap> SubmitRender(
        InputSource source,
        int pageIndex,
        PdfImageConversionOptions? options,
        string? password,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidatePageIndex(pageIndex);
            var job = OrchestrationJob.CreateBitmap(
                Interlocked.Increment(ref _nextRequestId),
                source,
                pageIndex,
                SnapshotOptions(options),
                password,
                cancellationToken,
                _maximumInputBytes,
                _maximumBitmapBytes,
                _maximumOutputBytes);
            return SubmitBitmapAsync(job);
        }
        catch
        {
            source.Cleanup();
            throw;
        }
    }

    private Task SubmitSave(
        InputSource source,
        int pageIndex,
        OutputTarget target,
        PdfImageConversionOptions? options,
        string? password,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidatePageIndex(pageIndex);
            var job = OrchestrationJob.CreateSave(
                Interlocked.Increment(ref _nextRequestId),
                source,
                target,
                pageIndex,
                SnapshotOptions(options),
                password,
                cancellationToken,
                _maximumInputBytes,
                _maximumOutputBytes);
            return SubmitSaveAsync(job);
        }
        catch
        {
            source.Cleanup();
            throw;
        }
    }

    private Task<IReadOnlyList<PdfBitmap>> SubmitBatchRender(
        InputSource source,
        IReadOnlyList<int> pageIndexes,
        PdfImageConversionOptions? options,
        string? password,
        CancellationToken cancellationToken)
    {
        try
        {
            var pages = SnapshotPageIndexes(pageIndexes);
            var job = OrchestrationJob.CreateBitmapBatch(
                Interlocked.Increment(ref _nextRequestId),
                source,
                pages,
                SnapshotOptions(options),
                password,
                cancellationToken,
                _maximumInputBytes,
                _maximumBitmapBytes,
                _maximumOutputBytes);
            return SubmitBitmapsAsync(job);
        }
        catch
        {
            source.Cleanup();
            throw;
        }
    }

    private Task SubmitBatchSave(
        InputSource source,
        IReadOnlyList<PdfPageFileOutput> outputs,
        PdfImageConversionOptions? options,
        string? password,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = SnapshotFileOutputs(outputs);
            var pages = snapshot.Select(output => output.PageIndex).ToArray();
            var paths = snapshot.Select(output => output.ImagePath).ToArray();
            var job = OrchestrationJob.CreateSaveBatch(
                Interlocked.Increment(ref _nextRequestId),
                source,
                pages,
                new BatchPathOutputTarget(paths),
                SnapshotOptions(options),
                password,
                cancellationToken,
                _maximumInputBytes,
                _maximumOutputBytes);
            return SubmitSaveAsync(job);
        }
        catch
        {
            source.Cleanup();
            throw;
        }
    }

    private async Task<PdfBitmap> SubmitBitmapAsync(OrchestrationJob job)
    {
        try
        {
            await EnqueueAsync(job).ConfigureAwait(false);
        }
        catch
        {
            job.Cleanup();
            throw;
        }

        return await job.BitmapTask.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PdfBitmap>> SubmitBitmapsAsync(OrchestrationJob job)
    {
        try
        {
            await EnqueueAsync(job).ConfigureAwait(false);
        }
        catch
        {
            job.Cleanup();
            throw;
        }

        return await job.BitmapsTask.ConfigureAwait(false);
    }

    private async Task SubmitSaveAsync(OrchestrationJob job)
    {
        try
        {
            await EnqueueAsync(job).ConfigureAwait(false);
        }
        catch
        {
            job.Cleanup();
            throw;
        }

        await job.SaveTask.ConfigureAwait(false);
    }

    private async Task EnqueueAsync(OrchestrationJob job)
    {
        ThrowIfNotAccepting();
        job.CancellationToken.ThrowIfCancellationRequested();
        PdfRenderOrchestratorEventSource.Log.RequestSubmitted(job.RequestId, job.OperationKind);
        _logger.RequestSubmitted(job.RequestId, PdfRenderOrchestratorTelemetry.OperationName(job.OperationKind));
        PdfRenderOrchestratorTelemetry.RequestQueued();

        if (_queueFullMode == PdfRenderQueueFullMode.Reject)
        {
            if (_queue.Writer.TryWrite(job))
            {
                return;
            }

            PdfRenderOrchestratorTelemetry.RequestRemovedWithoutExecution();
            ThrowIfNotAccepting();
            _logger.RequestRejected(job.RequestId);
            PdfRenderOrchestratorTelemetry.RequestRejected();
            RecordRequestBeforeExecution(
                job,
                outcome: "rejected",
                exceptionType: typeof(PdfRenderQueueFullException).FullName,
                workerIndex: null,
                removeFromQueue: false);
            throw new PdfRenderQueueFullException();
        }

        try
        {
            await _queue.Writer.WriteAsync(job, job.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.RequestCanceledBeforeDispatch(job.RequestId);
            RecordRequestBeforeExecution(
                job,
                outcome: "canceled",
                exceptionType: null,
                workerIndex: null,
                removeFromQueue: true);
            throw;
        }
        catch (ChannelClosedException exception)
        {
            PdfRenderOrchestratorTelemetry.RequestRemovedWithoutExecution();
            try
            {
                ThrowIfNotAccepting();
            }
            catch (Exception terminalException)
            {
                RecordRequestBeforeExecution(
                    job,
                    outcome: "error",
                    exceptionType: PdfRenderOrchestratorEventSource.ExceptionType(terminalException),
                    workerIndex: null,
                    removeFromQueue: false);
                throw;
            }

            RecordRequestBeforeExecution(
                job,
                outcome: "error",
                exceptionType: PdfRenderOrchestratorEventSource.ExceptionType(exception),
                workerIndex: null,
                removeFromQueue: false);
            throw;
        }
    }

    private async Task ProcessQueueAsync(WorkerSlot worker)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var job))
                {
                    var terminalError = Volatile.Read(ref _terminalError);
                    if (terminalError is not null)
                    {
                        PdfRenderOrchestratorTelemetry.RequestRemovedWithoutExecution();
                        job.Fail(terminalError);
                        PdfRenderOrchestratorEventSource.Log.RequestFailed(
                            job.RequestId,
                            worker.Index,
                            PdfRenderOrchestratorEventSource.ExceptionType(terminalError),
                            executionMilliseconds: 0);
                        _logger.RequestFailed(
                            job.RequestId,
                            worker.Index,
                            PdfRenderOrchestratorEventSource.ExceptionType(terminalError),
                            executionDurationMilliseconds: 0);
                        RecordRequestBeforeExecution(
                            job,
                            outcome: "error",
                            exceptionType: PdfRenderOrchestratorEventSource.ExceptionType(terminalError),
                            workerIndex: worker.Index,
                            removeFromQueue: false);
                        job.Cleanup();
                        continue;
                    }

                    if (Volatile.Read(ref _cancelRequested) != 0 || job.CancellationToken.IsCancellationRequested)
                    {
                        PdfRenderOrchestratorTelemetry.RequestRemovedWithoutExecution();
                        job.Cancel();
                        PdfRenderOrchestratorEventSource.Log.RequestCanceled(
                            job.RequestId,
                            worker.Index,
                            executionMilliseconds: 0);
                        _logger.RequestCanceled(
                            job.RequestId,
                            worker.Index,
                            executionDurationMilliseconds: 0);
                        RecordRequestBeforeExecution(
                            job,
                            outcome: "canceled",
                            exceptionType: null,
                            workerIndex: worker.Index,
                            removeFromQueue: false);
                        job.Cleanup();
                        continue;
                    }

                    await ExecuteJobAsync(worker, job).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            FaultOrchestrator(exception);
            throw;
        }
        finally
        {
            await worker.StopAsync().ConfigureAwait(false);
        }
    }

    private async Task ExecuteJobAsync(WorkerSlot worker, OrchestrationJob job)
    {
        var cleanupDeferred = false;
        var replacementAttempted = false;
        var terminalEventWritten = false;
        var executionStarted = Stopwatch.GetTimestamp();
        var queueDurationMilliseconds =
            PdfRenderOrchestratorEventSource.ElapsedMilliseconds(job.SubmittedTimestamp);
        PdfRenderOrchestratorTelemetry.RequestDequeued(job.OperationKind, queueDurationMilliseconds);
        var activity = PdfRenderOrchestratorTelemetry.StartRequest(
            job.RequestId,
            job.OperationKind,
            job.PageIndexes.Length,
            worker.Index,
            job.ParentActivityContext,
            job.SubmittedAt,
            queueDurationMilliseconds);
        var outcome = "error";
        string? exceptionType = null;
        PdfRenderOrchestratorEventSource.Log.RequestStarted(
            job.RequestId,
            worker.Index,
            queueDurationMilliseconds);
        _logger.RequestStarted(job.RequestId, worker.Index, queueDurationMilliseconds);
        using var executionCancellation = new CancellationTokenSource();
        try
        {
            var execution = worker.ExecuteAsync(job, executionCancellation.Token);
            if (_requestTimeout.HasValue)
            {
                using var timeoutCancellation = new CancellationTokenSource();
                var timeoutTask = Task.Delay(_requestTimeout.Value, timeoutCancellation.Token);
                if (await Task.WhenAny(execution, timeoutTask).ConfigureAwait(false) != execution)
                {
                    executionCancellation.Cancel();
                    worker.Kill();
                    var timeoutException = new PdfWorkerTimeoutException(_requestTimeout.Value);
                    job.Fail(timeoutException);
                    PdfRenderOrchestratorEventSource.Log.RequestFailed(
                        job.RequestId,
                        worker.Index,
                        PdfRenderOrchestratorEventSource.ExceptionType(timeoutException),
                        PdfRenderOrchestratorEventSource.ElapsedMilliseconds(executionStarted));
                    outcome = "timeout";
                    exceptionType = PdfRenderOrchestratorEventSource.ExceptionType(timeoutException);
                    _logger.RequestTimedOut(
                        job.RequestId,
                        worker.Index,
                        PdfRenderOrchestratorEventSource.ElapsedMilliseconds(executionStarted));
                    terminalEventWritten = true;
                    cleanupDeferred = true;
                    TrackDetachedOperation(ObserveFailureAndCleanupAsync(execution, job));
                    replacementAttempted = true;
                    if (ShouldReplaceWorker())
                    {
                        await RestartWorkerAsync(worker, timeoutException).ConfigureAwait(false);
                    }

                    return;
                }

                timeoutCancellation.Cancel();
            }

            var bitmaps = await execution.ConfigureAwait(false);
            if (Volatile.Read(ref _cancelRequested) != 0 || job.CancellationToken.IsCancellationRequested)
            {
                job.Cancel();
                outcome = "canceled";
                PdfRenderOrchestratorEventSource.Log.RequestCanceled(
                    job.RequestId,
                    worker.Index,
                    PdfRenderOrchestratorEventSource.ElapsedMilliseconds(executionStarted));
                _logger.RequestCanceled(
                    job.RequestId,
                    worker.Index,
                    PdfRenderOrchestratorEventSource.ElapsedMilliseconds(executionStarted));
                terminalEventWritten = true;
            }
            else
            {
                job.Complete(bitmaps);
                outcome = "success";
                PdfRenderOrchestratorEventSource.Log.RequestCompleted(
                    job.RequestId,
                    worker.Index,
                    PdfRenderOrchestratorEventSource.ElapsedMilliseconds(executionStarted));
                _logger.RequestCompleted(
                    job.RequestId,
                    worker.Index,
                    PdfRenderOrchestratorEventSource.ElapsedMilliseconds(executionStarted));
                terminalEventWritten = true;
            }
        }
        catch (PdfWorkerRemoteException exception)
        {
            job.Fail(exception);
            exceptionType = PdfRenderOrchestratorEventSource.ExceptionType(exception);
            if (!terminalEventWritten)
            {
                PdfRenderOrchestratorEventSource.Log.RequestFailed(
                    job.RequestId,
                    worker.Index,
                    PdfRenderOrchestratorEventSource.ExceptionType(exception),
                    PdfRenderOrchestratorEventSource.ElapsedMilliseconds(executionStarted));
                _logger.RequestFailed(
                    job.RequestId,
                    worker.Index,
                    exceptionType,
                    PdfRenderOrchestratorEventSource.ElapsedMilliseconds(executionStarted));
                terminalEventWritten = true;
            }
        }
        catch (Exception exception)
        {
            job.Fail(exception);
            exceptionType = PdfRenderOrchestratorEventSource.ExceptionType(exception);
            if (!terminalEventWritten)
            {
                PdfRenderOrchestratorEventSource.Log.RequestFailed(
                    job.RequestId,
                    worker.Index,
                    PdfRenderOrchestratorEventSource.ExceptionType(exception),
                    PdfRenderOrchestratorEventSource.ElapsedMilliseconds(executionStarted));
                _logger.RequestFailed(
                    job.RequestId,
                    worker.Index,
                    exceptionType,
                    PdfRenderOrchestratorEventSource.ElapsedMilliseconds(executionStarted));
                terminalEventWritten = true;
            }
            if (replacementAttempted)
            {
                throw;
            }

            if (ShouldReplaceWorker())
            {
                await RestartWorkerAsync(worker, exception).ConfigureAwait(false);
            }
        }
        finally
        {
            PdfRenderOrchestratorTelemetry.RequestFinished(
                activity,
                job.OperationKind,
                outcome,
                exceptionType,
                PdfRenderOrchestratorEventSource.ElapsedMilliseconds(job.SubmittedTimestamp),
                wasActive: true);
            if (!cleanupDeferred)
            {
                job.Cleanup();
            }
        }
    }

    private bool ShouldReplaceWorker()
    {
        if (Volatile.Read(ref _cancelRequested) != 0)
        {
            return false;
        }

        return Volatile.Read(ref _accepting) != 0 || _queue.Reader.TryPeek(out _);
    }

    private async Task RestartWorkerAsync(WorkerSlot worker, Exception reason)
    {
        worker.Kill();
        worker.DisposeConnection();
        Exception? lastError = null;
        for (var attempt = 0; attempt < _workerRestartDelays.Length; attempt++)
        {
            var delay = _workerRestartDelays[attempt];
            PdfRenderOrchestratorEventSource.Log.WorkerRestarting(
                worker.Index,
                attempt + 1,
                checked((long)delay.TotalMilliseconds),
                PdfRenderOrchestratorEventSource.ExceptionType(reason));
            _logger.WorkerRestarting(
                worker.Index,
                attempt + 1,
                checked((long)delay.TotalMilliseconds),
                PdfRenderOrchestratorEventSource.ExceptionType(reason));
            PdfRenderOrchestratorTelemetry.WorkerRestarted(reason);
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                await worker.StartAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                worker.Kill();
                worker.DisposeConnection();
            }
        }

        throw new PdfWorkerStartupException(
            $"PDFium worker {worker.Index} could not be replaced after {_workerRestartDelays.Length} attempts.",
            lastError ?? new InvalidOperationException("The replacement worker did not start."));
    }

    private async Task CompleteWorkersAsync(Task[] workerTasks)
    {
        Exception? error = null;
        try
        {
            await Task.WhenAll(workerTasks).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
            throw;
        }
        finally
        {
            while (_queue.Reader.TryRead(out var pending))
            {
                if (error is not null)
                {
                    pending.Fail(error);
                    RecordRequestBeforeExecution(
                        pending,
                        outcome: "error",
                        exceptionType: PdfRenderOrchestratorEventSource.ExceptionType(error),
                        workerIndex: null,
                        removeFromQueue: true);
                }
                else
                {
                    pending.Cancel();
                    RecordRequestBeforeExecution(
                        pending,
                        outcome: "canceled",
                        exceptionType: null,
                        workerIndex: null,
                        removeFromQueue: true);
                }

                pending.Cleanup();
            }

            Task[] detachedOperations;
            lock (_detachedOperationsSync)
            {
                detachedOperations = _detachedOperations.ToArray();
            }

            try
            {
                await Task.WhenAll(detachedOperations).ConfigureAwait(false);
            }
            finally
            {
                foreach (var worker in _workers)
                {
                    worker.Dispose();
                }
            }
        }
    }

    private void FaultOrchestrator(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _terminalError, exception, null) is null)
        {
            PdfRenderOrchestratorEventSource.Log.OrchestratorFaulted(
                PdfRenderOrchestratorEventSource.ExceptionType(exception));
            _logger.OrchestratorFaulted(PdfRenderOrchestratorEventSource.ExceptionType(exception));
        }

        Interlocked.Exchange(ref _accepting, 0);
        _queue.Writer.TryComplete(exception);
    }

    private void BeginShutdown(bool cancel)
    {
        if (cancel)
        {
            Volatile.Write(ref _cancelRequested, 1);
        }

        if (Interlocked.Exchange(ref _accepting, 0) != 0)
        {
            PdfRenderOrchestratorEventSource.Log.OrchestratorStopping(cancel);
            _logger.OrchestratorStopping(cancel);
            _queue.Writer.TryComplete();
        }
    }

    private static void RecordRequestBeforeExecution(
        OrchestrationJob job,
        string outcome,
        string? exceptionType,
        int? workerIndex,
        bool removeFromQueue)
    {
        if (removeFromQueue)
        {
            PdfRenderOrchestratorTelemetry.RequestRemovedWithoutExecution();
        }

        var queueDurationMilliseconds =
            PdfRenderOrchestratorEventSource.ElapsedMilliseconds(job.SubmittedTimestamp);
        var activity = PdfRenderOrchestratorTelemetry.StartRequest(
            job.RequestId,
            job.OperationKind,
            job.PageIndexes.Length,
            workerIndex,
            job.ParentActivityContext,
            job.SubmittedAt,
            queueDurationMilliseconds);
        PdfRenderOrchestratorTelemetry.RequestFinished(
            activity,
            job.OperationKind,
            outcome,
            exceptionType,
            queueDurationMilliseconds,
            wasActive: false);
    }

    private void ThrowIfNotAccepting()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        var terminalError = Volatile.Read(ref _terminalError);
        if (terminalError is not null)
        {
            throw new InvalidOperationException("The PDF render orchestrator has faulted.", terminalError);
        }

        if (Volatile.Read(ref _accepting) == 0)
        {
            throw new InvalidOperationException("The PDF render orchestrator is no longer accepting requests.");
        }
    }

    private void TrackDetachedOperation(Task task)
    {
        lock (_detachedOperationsSync)
        {
            _detachedOperations.Add(task);
        }
    }

    private static async Task ObserveFailureAndCleanupAsync(Task task, OrchestrationJob job)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The request already completed with a timeout.
        }
        finally
        {
            job.Cleanup();
        }
    }

    private static void ValidatePageIndex(int pageIndex)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex,
                "Page index must be zero or greater.");
        }
    }

    private int[] SnapshotPageIndexes(IReadOnlyList<int> pageIndexes)
    {
        if (pageIndexes is null)
        {
            throw new ArgumentNullException(nameof(pageIndexes));
        }

        if (pageIndexes.Count == 0 || pageIndexes.Count > _maximumBatchPages)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndexes), pageIndexes.Count,
                $"A batch must contain from 1 through {_maximumBatchPages} pages.");
        }

        var snapshot = new int[pageIndexes.Count];
        for (var index = 0; index < pageIndexes.Count; index++)
        {
            ValidatePageIndex(pageIndexes[index]);
            snapshot[index] = pageIndexes[index];
        }

        return snapshot;
    }

    private PdfPageFileOutput[] SnapshotFileOutputs(IReadOnlyList<PdfPageFileOutput> outputs)
    {
        if (outputs is null)
        {
            throw new ArgumentNullException(nameof(outputs));
        }

        if (outputs.Count == 0 || outputs.Count > _maximumBatchPages)
        {
            throw new ArgumentOutOfRangeException(nameof(outputs), outputs.Count,
                $"A batch must contain from 1 through {_maximumBatchPages} outputs.");
        }

        var snapshot = new PdfPageFileOutput[outputs.Count];
        var comparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var paths = new HashSet<string>(comparer);
        for (var index = 0; index < outputs.Count; index++)
        {
            var output = outputs[index] ??
                throw new ArgumentException("Batch outputs cannot contain null items.", nameof(outputs));
            if (!paths.Add(output.ImagePath))
            {
                throw new ArgumentException("Batch output paths must be unique.", nameof(outputs));
            }

            snapshot[index] = output;
        }

        return snapshot;
    }

    private static void ThrowIfResourceLimitExceeded(string resource, long? limit, long observed)
    {
        if (limit.HasValue && observed > limit.Value)
        {
            throw new PdfRenderResourceLimitException(resource, limit.Value, observed);
        }
    }

    private InputSource CreatePathSource(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
        {
            throw new ArgumentException("PDF path cannot be null or whitespace.", nameof(pdfPath));
        }

        return new PathInputSource(Path.GetFullPath(pdfPath));
    }

    private InputSource CreateByteSource(byte[] pdfBytes)
    {
        if (pdfBytes is null)
        {
            throw new ArgumentNullException(nameof(pdfBytes));
        }

        if (pdfBytes.Length == 0)
        {
            throw new ArgumentException("PDF bytes cannot be empty.", nameof(pdfBytes));
        }

        ThrowIfResourceLimitExceeded("input bytes", _maximumInputBytes, pdfBytes.LongLength);
        return new ByteInputSource(pdfBytes, _maximumInputBytes);
    }

    private InputSource CreateStreamSource(Stream pdfStream, bool leaveOpen)
    {
        if (pdfStream is null)
        {
            throw new ArgumentNullException(nameof(pdfStream));
        }

        if (!pdfStream.CanRead)
        {
            throw new ArgumentException("PDF stream must be readable.", nameof(pdfStream));
        }

        if (pdfStream.CanSeek)
        {
            ThrowIfResourceLimitExceeded("input bytes", _maximumInputBytes, pdfStream.Length - pdfStream.Position);
        }

        return new StreamInputSource(pdfStream, leaveOpen, _maximumInputBytes);
    }

    private static OutputTarget CreatePathTarget(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ArgumentException("Image path cannot be null or whitespace.", nameof(imagePath));
        }

        return new PathOutputTarget(Path.GetFullPath(imagePath));
    }

    private static OutputTarget CreateStreamTarget(Stream imageStream)
    {
        if (imageStream is null)
        {
            throw new ArgumentNullException(nameof(imageStream));
        }

        if (!imageStream.CanWrite)
        {
            throw new ArgumentException("Image stream must be writable.", nameof(imageStream));
        }

        return new StreamOutputTarget(imageStream);
    }

    private static PdfImageConversionOptions SnapshotOptions(PdfImageConversionOptions? options)
    {
        options ??= new PdfImageConversionOptions();
        var sourceRender = options.Render ?? new PdfPageRenderOptions();
        var sourceEncoding = options.Encoding ?? new PdfImageEncodingOptions();
        if (!Enum.IsDefined(typeof(PdfImageOutputFormat), options.Format))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Format,
                "Image format must be a defined value.");
        }

        if (!Enum.IsDefined(typeof(PdfImageColorMode), options.ColorMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ColorMode,
                "Image color mode must be a defined value.");
        }

        return new PdfImageConversionOptions
        {
            Render = new PdfPageRenderOptions
            {
                Dpi = sourceRender.Dpi,
                Scale = sourceRender.Scale,
                Rotation = sourceRender.Rotation,
                Flags = sourceRender.Flags,
                Width = sourceRender.Width,
                Height = sourceRender.Height,
                WithAspectRatio = sourceRender.WithAspectRatio,
                AntiAliasing = sourceRender.AntiAliasing,
                BackgroundColor = sourceRender.BackgroundColor,
                FillBackground = sourceRender.FillBackground,
            },
            Format = options.Format,
            Encoding = new PdfImageEncodingOptions
            {
                Quality = sourceEncoding.Quality,
                PngCompressionLevel = sourceEncoding.PngCompressionLevel,
            },
            ColorMode = options.ColorMode,
            BlackAndWhiteThreshold = options.BlackAndWhiteThreshold,
        };
    }

    private abstract class InputSource
    {
        internal abstract WorkerSourceKind Kind { get; }
        internal virtual string? Path => null;
        internal virtual Task SendAsync(Stream pipe, CancellationToken cancellationToken) => Task.CompletedTask;
        internal virtual void Cleanup()
        {
        }
    }

    private sealed class PathInputSource : InputSource
    {
        private readonly string _path;

        internal PathInputSource(string path)
        {
            _path = path;
        }

        internal override WorkerSourceKind Kind => WorkerSourceKind.Path;
        internal override string Path => _path;
    }

    private sealed class ByteInputSource : InputSource
    {
        private readonly byte[] _bytes;
        private readonly long? _maximumBytes;

        internal ByteInputSource(byte[] bytes, long? maximumBytes)
        {
            _bytes = bytes;
            _maximumBytes = maximumBytes;
        }

        internal override WorkerSourceKind Kind => WorkerSourceKind.Content;

        internal override async Task SendAsync(Stream pipe, CancellationToken cancellationToken)
        {
            ThrowIfResourceLimitExceeded("input bytes", _maximumBytes, _bytes.LongLength);
            var offset = 0;
            while (offset < _bytes.Length)
            {
                var count = Math.Min(WorkerProtocol.ChunkSize, _bytes.Length - offset);
                var chunk = new byte[count];
                Buffer.BlockCopy(_bytes, offset, chunk, 0, count);
                await WorkerProtocol.WriteFrameAsync(pipe, WorkerMessage.InputChunk, chunk, cancellationToken)
                    .ConfigureAwait(false);
                offset += count;
            }
        }
    }

    private sealed class StreamInputSource : InputSource
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly long? _maximumBytes;

        internal StreamInputSource(Stream stream, bool leaveOpen, long? maximumBytes)
        {
            _stream = stream;
            _leaveOpen = leaveOpen;
            _maximumBytes = maximumBytes;
        }

        internal override WorkerSourceKind Kind => WorkerSourceKind.Content;

        internal override async Task SendAsync(Stream pipe, CancellationToken cancellationToken)
        {
            var buffer = new byte[WorkerProtocol.ChunkSize];
            long total = 0;
            int read;
            while ((read = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                       .ConfigureAwait(false)) != 0)
            {
                total = checked(total + read);
                ThrowIfResourceLimitExceeded("input bytes", _maximumBytes, total);
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                await WorkerProtocol.WriteFrameAsync(pipe, WorkerMessage.InputChunk, chunk, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        internal override void Cleanup()
        {
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    private abstract class OutputTarget
    {
        internal abstract WorkerOutputKind Kind { get; }
        internal virtual string? Path => null;
        internal virtual Stream? Stream => null;
    }

    private sealed class BitmapOutputTarget : OutputTarget
    {
        internal override WorkerOutputKind Kind => WorkerOutputKind.Bitmap;
    }

    private sealed class PathOutputTarget : OutputTarget
    {
        private readonly string _path;

        internal PathOutputTarget(string path)
        {
            _path = path;
        }

        internal override WorkerOutputKind Kind => WorkerOutputKind.Path;
        internal override string Path => _path;
    }

    private sealed class StreamOutputTarget : OutputTarget
    {
        private readonly Stream _stream;

        internal StreamOutputTarget(Stream stream)
        {
            _stream = stream;
        }

        internal override WorkerOutputKind Kind => WorkerOutputKind.Stream;
        internal override Stream Stream => _stream;
    }

    private sealed class BatchPathOutputTarget : OutputTarget
    {
        internal BatchPathOutputTarget(string[] paths)
        {
            Paths = paths;
        }

        internal override WorkerOutputKind Kind => WorkerOutputKind.Path;
        internal string[] Paths { get; }
    }

    private sealed class OrchestrationJob
    {
        private readonly TaskCompletionSource<PdfBitmap>? _bitmapCompletion;
        private readonly TaskCompletionSource<IReadOnlyList<PdfBitmap>>? _bitmapsCompletion;
        private readonly TaskCompletionSource<object?>? _saveCompletion;
        private int _cleaned;

        private OrchestrationJob(
            long requestId,
            InputSource source,
            OutputTarget target,
            int[] pageIndexes,
            PdfImageConversionOptions options,
            string? password,
            CancellationToken cancellationToken,
            int resultKind,
            long? maximumInputBytes,
            long? maximumBitmapBytes,
            long? maximumOutputBytes)
        {
            RequestId = requestId;
            SubmittedTimestamp = Stopwatch.GetTimestamp();
            SubmittedAt = DateTimeOffset.UtcNow;
            ParentActivityContext = Activity.Current?.Context ?? default;
            Source = source;
            Target = target;
            PageIndexes = pageIndexes;
            Options = options;
            Password = password;
            CancellationToken = cancellationToken;
            MaximumInputBytes = maximumInputBytes;
            MaximumBitmapBytes = maximumBitmapBytes;
            MaximumOutputBytes = maximumOutputBytes;
            if (resultKind == 1)
            {
                _bitmapCompletion = new TaskCompletionSource<PdfBitmap>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else if (resultKind == 2)
            {
                _bitmapsCompletion = new TaskCompletionSource<IReadOnlyList<PdfBitmap>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                _saveCompletion = new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        internal InputSource Source { get; }
        internal long RequestId { get; }
        internal long SubmittedTimestamp { get; }
        internal DateTimeOffset SubmittedAt { get; }
        internal ActivityContext ParentActivityContext { get; }
        internal int OperationKind => _bitmapCompletion is not null ? 1 : _bitmapsCompletion is not null ? 3 :
            PageIndexes.Length == 1 ? 2 : 4;
        internal OutputTarget Target { get; }
        internal int[] PageIndexes { get; }
        internal PdfImageConversionOptions Options { get; }
        internal string? Password { get; }
        internal CancellationToken CancellationToken { get; }
        internal Task<PdfBitmap> BitmapTask => _bitmapCompletion!.Task;
        internal Task<IReadOnlyList<PdfBitmap>> BitmapsTask => _bitmapsCompletion!.Task;
        internal Task SaveTask => _saveCompletion!.Task;
        internal long? MaximumInputBytes { get; }
        internal long? MaximumBitmapBytes { get; }
        internal long? MaximumOutputBytes { get; }

        internal static OrchestrationJob CreateBitmap(
            long requestId,
            InputSource source,
            int pageIndex,
            PdfImageConversionOptions options,
            string? password,
            CancellationToken cancellationToken,
            long? maximumInputBytes,
            long? maximumBitmapBytes,
            long? maximumOutputBytes)
        {
            return new OrchestrationJob(
                requestId,
                source,
                new BitmapOutputTarget(),
                new[] { pageIndex },
                options,
                password,
                cancellationToken,
                1,
                maximumInputBytes,
                maximumBitmapBytes,
                maximumOutputBytes);
        }

        internal static OrchestrationJob CreateBitmapBatch(
            long requestId,
            InputSource source,
            int[] pageIndexes,
            PdfImageConversionOptions options,
            string? password,
            CancellationToken cancellationToken,
            long? maximumInputBytes,
            long? maximumBitmapBytes,
            long? maximumOutputBytes)
        {
            return new OrchestrationJob(requestId, source, new BitmapOutputTarget(), pageIndexes, options, password,
                cancellationToken, 2, maximumInputBytes, maximumBitmapBytes, maximumOutputBytes);
        }

        internal static OrchestrationJob CreateSave(
            long requestId,
            InputSource source,
            OutputTarget target,
            int pageIndex,
            PdfImageConversionOptions options,
            string? password,
            CancellationToken cancellationToken,
            long? maximumInputBytes,
            long? maximumOutputBytes)
        {
            return new OrchestrationJob(
                requestId,
                source,
                target,
                new[] { pageIndex },
                options,
                password,
                cancellationToken,
                0,
                maximumInputBytes,
                null,
                maximumOutputBytes);
        }

        internal static OrchestrationJob CreateSaveBatch(
            long requestId,
            InputSource source,
            int[] pageIndexes,
            BatchPathOutputTarget target,
            PdfImageConversionOptions options,
            string? password,
            CancellationToken cancellationToken,
            long? maximumInputBytes,
            long? maximumOutputBytes)
        {
            return new OrchestrationJob(requestId, source, target, pageIndexes, options, password,
                cancellationToken, 0, maximumInputBytes, null, maximumOutputBytes);
        }

        internal WorkerRequest CreateRequest()
        {
            return new WorkerRequest
            {
                SourceKind = Source.Kind,
                SourcePath = Source.Path,
                OutputKind = Target.Kind,
                OutputPath = Target.Path,
                PageIndex = PageIndexes[0],
                PageIndexes = PageIndexes.Length == 1 ? Array.Empty<int>() : PageIndexes,
                OutputPaths = Target is BatchPathOutputTarget batchTarget
                    ? batchTarget.Paths
                    : Array.Empty<string>(),
                Password = Password,
                Options = Options,
                MaximumInputBytes = MaximumInputBytes,
                MaximumBitmapBytes = MaximumBitmapBytes,
                MaximumOutputBytes = MaximumOutputBytes,
            };
        }

        internal void Complete(IReadOnlyList<PdfBitmap>? bitmaps)
        {
            if (_bitmapCompletion is not null)
            {
                if (bitmaps is null || bitmaps.Count != 1)
                {
                    throw new PdfWorkerProtocolException("The worker did not return exactly one bitmap.");
                }

                _bitmapCompletion.TrySetResult(bitmaps[0]);
            }
            else if (_bitmapsCompletion is not null)
            {
                if (bitmaps is null || bitmaps.Count != PageIndexes.Length)
                {
                    throw new PdfWorkerProtocolException("The worker returned an unexpected number of bitmaps.");
                }

                _bitmapsCompletion.TrySetResult(bitmaps);
            }
            else
            {
                _saveCompletion!.TrySetResult(null);
            }
        }

        internal void Fail(Exception exception)
        {
            if (_bitmapCompletion is not null)
            {
                _bitmapCompletion.TrySetException(exception);
            }
            else if (_bitmapsCompletion is not null)
            {
                _bitmapsCompletion.TrySetException(exception);
            }
            else
            {
                _saveCompletion!.TrySetException(exception);
            }
        }

        internal void Cancel()
        {
            if (_bitmapCompletion is not null)
            {
                _bitmapCompletion.TrySetCanceled();
            }
            else if (_bitmapsCompletion is not null)
            {
                _bitmapsCompletion.TrySetCanceled();
            }
            else
            {
                _saveCompletion!.TrySetCanceled();
            }
        }

        internal void Cleanup()
        {
            if (Interlocked.Exchange(ref _cleaned, 1) == 0)
            {
                Source.Cleanup();
            }
        }
    }

    private sealed class WorkerSlot : IDisposable
    {
        private readonly ILogger _logger;
        private readonly TimeSpan _startupTimeout;
        private readonly string? _temporaryDirectory;
        private WorkerConnection? _connection;

        internal WorkerSlot(
            int index,
            TimeSpan startupTimeout,
            string? temporaryDirectory,
            ILogger logger)
        {
            Index = index;
            _startupTimeout = startupTimeout;
            _temporaryDirectory = temporaryDirectory;
            _logger = logger;
        }

        internal int Index { get; }

        internal async Task StartAsync()
        {
            _connection = await WorkerConnection.StartAsync(Index, _startupTimeout, _temporaryDirectory, _logger)
                .ConfigureAwait(false);
        }

        internal Task<IReadOnlyList<PdfBitmap>?> ExecuteAsync(
            OrchestrationJob job,
            CancellationToken cancellationToken)
        {
            return (_connection ?? throw new PdfWorkerStartupException("The worker is not connected."))
                .ExecuteAsync(job, cancellationToken);
        }

        internal async Task StopAsync()
        {
            var connection = _connection;
            if (connection is null)
            {
                return;
            }

            try
            {
                await connection.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                DisposeConnection();
            }
        }

        internal void Kill()
        {
            _connection?.Kill();
        }

        internal void DisposeConnection()
        {
            Interlocked.Exchange(ref _connection, null)?.Dispose();
        }

        public void Dispose()
        {
            Kill();
            DisposeConnection();
        }
    }

    private sealed class WorkerConnection : IDisposable
    {
        private const int StandardErrorLimit = 8192;
        private readonly NamedPipeServerStream _pipe;
        private readonly int _index;
        private readonly Process _process;
        private readonly string _temporaryDirectory;
        private readonly StringBuilder _standardError = new();
        private readonly ILogger _logger;
        private readonly object _errorSync = new();
        private readonly TaskCompletionSource<object?> _processExited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        private int _reportedStarted;

        private WorkerConnection(
            int index,
            NamedPipeServerStream pipe,
            Process process,
            string temporaryDirectory,
            ILogger logger)
        {
            _index = index;
            _pipe = pipe;
            _process = process;
            _temporaryDirectory = temporaryDirectory;
            _logger = logger;
            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;
            _process.ErrorDataReceived += OnErrorDataReceived;
            _process.BeginErrorReadLine();
            if (_process.HasExited)
            {
                _processExited.TrySetResult(null);
            }
        }

        internal static async Task<WorkerConnection> StartAsync(
            int index,
            TimeSpan startupTimeout,
            string? temporaryDirectoryRoot,
            ILogger logger)
        {
            var pipeName = $"pdr-{index:x}-{Guid.NewGuid():N}".Substring(0, 24);
            var token = CreateToken();
            var temporaryDirectory = Path.Combine(
                temporaryDirectoryRoot ?? Path.GetTempPath(),
                $"pdfium-raster-worker-{Guid.NewGuid():N}");
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                WorkerProtocol.LocalPipeOptions);
            Process? process = null;
            WorkerConnection? connection = null;
            try
            {
                var executable = WorkerExecutableResolver.Resolve();
                var startInfo = WorkerExecutableResolver.CreateStartInfo(
                    executable,
                    pipeName,
                    token,
                    temporaryDirectory,
                    startupTimeout);
                process = Process.Start(startInfo) ??
                    throw new PdfWorkerStartupException("The PDFium worker process did not start.");
                connection = new WorkerConnection(index, pipe, process, temporaryDirectory, logger);

                using var timeout = new CancellationTokenSource(startupTimeout);
                var connectionTask = pipe.WaitForConnectionAsync(timeout.Token);
                if (await Task.WhenAny(connectionTask, connection._processExited.Task).ConfigureAwait(false) !=
                    connectionTask)
                {
                    process.WaitForExit();
                    throw new PdfWorkerStartupException(
                        $"PDFium worker {index} exited with code {process.ExitCode} before connecting." +
                        FormatStandardError(connection.GetStandardError()));
                }

                await connectionTask.ConfigureAwait(false);
                var hello = await WorkerProtocol.ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
                WorkerProtocol.ValidateWorkerHello(hello, token);

                await WorkerProtocol.WriteEmptyFrameAsync(pipe, WorkerMessage.Ready, timeout.Token)
                    .ConfigureAwait(false);
                PdfRenderOrchestratorEventSource.Log.WorkerStarted(index, process.Id);
                logger.WorkerStarted(index, process.Id);
                PdfRenderOrchestratorTelemetry.WorkerStarted();
                connection._reportedStarted = 1;
                return connection;
            }
            catch (Exception exception)
            {
                PdfRenderOrchestratorEventSource.Log.WorkerStartFailed(
                    index,
                    PdfRenderOrchestratorEventSource.ExceptionType(exception));
                logger.WorkerStartFailed(index, PdfRenderOrchestratorEventSource.ExceptionType(exception));
                connection?.Kill();
                connection?.Dispose();
                if (connection is null)
                {
                    try
                    {
                        if (process is { HasExited: false })
                        {
                            process.Kill();
                        }
                    }
                    catch
                    {
                    }

                    process?.Dispose();
                    pipe.Dispose();
                    TryDeleteDirectory(temporaryDirectory);
                }

                if (exception is PdfWorkerStartupException)
                {
                    throw;
                }

                throw new PdfWorkerStartupException($"PDFium worker {index} failed to start.", exception);
            }
        }

        internal async Task<IReadOnlyList<PdfBitmap>?> ExecuteAsync(
            OrchestrationJob job,
            CancellationToken cancellationToken)
        {
            try
            {
                var request = job.CreateRequest();
                await WorkerProtocol.WriteFrameAsync(
                        _pipe,
                        WorkerMessage.Request,
                        WorkerProtocol.SerializeRequest(request),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (request.SourceKind == WorkerSourceKind.Content)
                {
                    await job.Source.SendAsync(_pipe, cancellationToken).ConfigureAwait(false);
                    await WorkerProtocol.WriteEmptyFrameAsync(_pipe, WorkerMessage.InputEnd, cancellationToken)
                        .ConfigureAwait(false);
                }

                byte[]? bitmapPixels = null;
                var bitmapOffset = 0;
                var width = 0;
                var height = 0;
                var stride = 0;
                long totalOutputBytes = 0;
                var bitmaps = new List<PdfBitmap>();

                while (true)
                {
                    var frame = await WorkerProtocol.ReadFrameAsync(_pipe, cancellationToken)
                        .ConfigureAwait(false);
                    switch (frame.Message)
                    {
                        case WorkerMessage.BitmapHeader:
                        {
                            if (request.OutputKind != WorkerOutputKind.Bitmap)
                            {
                                throw new PdfWorkerProtocolException("The worker sent an unexpected bitmap header.");
                            }

                            if (bitmapPixels is not null)
                            {
                                if (bitmapOffset != bitmapPixels.Length)
                                {
                                    throw new PdfWorkerProtocolException("The worker returned an incomplete bitmap.");
                                }

                                bitmaps.Add(new PdfBitmap(width, height, stride, bitmapPixels));
                            }

                            var header = WorkerProtocol.DeserializeBitmapHeader(frame.Payload);
                            ValidateBitmapHeader(header.Width, header.Height, header.Stride, header.ByteCount);
                            ThrowIfResourceLimitExceeded(
                                "bitmap bytes",
                                request.MaximumBitmapBytes,
                                header.ByteCount);
                            totalOutputBytes = checked(totalOutputBytes + header.ByteCount);
                            ThrowIfResourceLimitExceeded(
                                "output bytes",
                                request.MaximumOutputBytes,
                                totalOutputBytes);
                            width = header.Width;
                            height = header.Height;
                            stride = header.Stride;
                            bitmapPixels = new byte[header.ByteCount];
                            bitmapOffset = 0;
                            break;
                        }
                        case WorkerMessage.OutputChunk:
                            if (request.OutputKind == WorkerOutputKind.Bitmap)
                            {
                                if (bitmapPixels is null ||
                                    frame.Payload.Length > bitmapPixels.Length - bitmapOffset)
                                {
                                    throw new PdfWorkerProtocolException("The worker sent too many bitmap bytes.");
                                }

                                Buffer.BlockCopy(frame.Payload, 0, bitmapPixels, bitmapOffset, frame.Payload.Length);
                                bitmapOffset += frame.Payload.Length;
                            }
                            else if (request.OutputKind == WorkerOutputKind.Stream)
                            {
                                totalOutputBytes = checked(totalOutputBytes + frame.Payload.Length);
                                ThrowIfResourceLimitExceeded(
                                    "output bytes",
                                    request.MaximumOutputBytes,
                                    totalOutputBytes);
                                await job.Target.Stream!.WriteAsync(
                                        frame.Payload,
                                        0,
                                        frame.Payload.Length,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                throw new PdfWorkerProtocolException("The worker sent output bytes for a path target.");
                            }

                            break;
                        case WorkerMessage.Complete:
                            if (request.OutputKind == WorkerOutputKind.Bitmap)
                            {
                                if (bitmapPixels is null || bitmapOffset != bitmapPixels.Length)
                                {
                                    throw new PdfWorkerProtocolException("The worker returned an incomplete bitmap.");
                                }

                                bitmaps.Add(new PdfBitmap(width, height, stride, bitmapPixels));
                                var expectedCount = request.PageIndexes.Length == 0 ? 1 : request.PageIndexes.Length;
                                if (bitmaps.Count != expectedCount)
                                {
                                    throw new PdfWorkerProtocolException(
                                        "The worker returned an unexpected number of bitmaps.");
                                }

                                return bitmaps.AsReadOnly();
                            }

                            return null;
                        case WorkerMessage.Error:
                        {
                            var error = WorkerProtocol.DeserializeError(frame.Payload);
                            throw new PdfWorkerRemoteException(error.Type, error.Message);
                        }
                        case WorkerMessage.ResourceLimit:
                        {
                            var limit = WorkerProtocol.DeserializeResourceLimit(frame.Payload);
                            throw new PdfRenderResourceLimitException(limit.Resource, limit.Limit, limit.Observed);
                        }
                        default:
                            throw new PdfWorkerProtocolException(
                                $"The worker sent unexpected message {frame.Message} while processing a request.");
                    }
                }
            }
            catch (PdfWorkerRemoteException)
            {
                throw;
            }
            catch (PdfRenderResourceLimitException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException)
            {
                throw CreateCrashException(exception);
            }
        }

        internal async Task StopAsync()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                if (_pipe.IsConnected)
                {
                    await WorkerProtocol.WriteEmptyFrameAsync(
                            _pipe,
                            WorkerMessage.Shutdown,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                if (!_process.WaitForExit(5000))
                {
                    Kill();
                }
            }
            catch
            {
                Kill();
            }
        }

        internal void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(5000);
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _process.ErrorDataReceived -= OnErrorDataReceived;
            _process.Exited -= OnProcessExited;
            _pipe.Dispose();
            PdfRenderOrchestratorEventSource.Log.WorkerStopped(_index, _process.Id);
            if (Interlocked.Exchange(ref _reportedStarted, 0) != 0)
            {
                _logger.WorkerStopped(_index, _process.Id);
                PdfRenderOrchestratorTelemetry.WorkerStopped();
            }
            _process.Dispose();
            TryDeleteDirectory(_temporaryDirectory);
        }

        private PdfWorkerCrashedException CreateCrashException(Exception innerException)
        {
            int? exitCode = null;
            try
            {
                if (!_process.HasExited)
                {
                    _process.WaitForExit(100);
                }

                if (_process.HasExited)
                {
                    exitCode = _process.ExitCode;
                }
            }
            catch
            {
            }

            string standardError;
            lock (_errorSync)
            {
                standardError = _standardError.ToString();
            }

            return new PdfWorkerCrashedException(
                "The PDFium worker exited or disconnected while processing a request.",
                exitCode,
                standardError,
                innerException);
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.Data))
            {
                return;
            }

            lock (_errorSync)
            {
                _standardError.AppendLine(args.Data);
                if (_standardError.Length > StandardErrorLimit)
                {
                    _standardError.Remove(0, _standardError.Length - StandardErrorLimit);
                }
            }
        }

        private void OnProcessExited(object? sender, EventArgs args)
        {
            _processExited.TrySetResult(null);
        }

        private string GetStandardError()
        {
            lock (_errorSync)
            {
                return _standardError.ToString();
            }
        }

        private static string FormatStandardError(string standardError)
        {
            return string.IsNullOrWhiteSpace(standardError)
                ? string.Empty
                : Environment.NewLine + standardError.TrimEnd();
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static string CreateToken()
        {
            var bytes = new byte[32];
            using (var generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }

            return Convert.ToBase64String(bytes);
        }

        private static void ValidateBitmapHeader(int width, int height, int stride, int byteCount)
        {
            if (width <= 0 || height <= 0 || stride < checked(width * 4) ||
                byteCount != checked(stride * height))
            {
                throw new PdfWorkerProtocolException("The worker returned invalid bitmap dimensions.");
            }
        }
    }

    private static class WorkerExecutableResolver
    {
        private const string WorkerPathEnvironmentVariable = "PDFIUMRASTER_WORKER_PATH";

        internal static void AssertSupportedPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                RuntimeInformation.ProcessArchitecture == Architecture.X86)
            {
                throw new PlatformNotSupportedException(
                    "PdfiumRaster.Orchestrator does not provide a self-contained worker for 32-bit Linux.");
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                throw new PlatformNotSupportedException(
                    "PdfiumRaster.Orchestrator supports Windows, Linux, and macOS.");
            }

            if (RuntimeInformation.ProcessArchitecture is not
                (Architecture.X86 or Architecture.X64 or Architecture.Arm or Architecture.Arm64))
            {
                throw new PlatformNotSupportedException(
                    $"PdfiumRaster.Orchestrator does not support {RuntimeInformation.ProcessArchitecture}.");
            }
        }

        internal static string Resolve()
        {
            var configured = Environment.GetEnvironmentVariable(WorkerPathEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var fullConfiguredPath = Path.GetFullPath(configured);
                if (!File.Exists(fullConfiguredPath))
                {
                    throw new PdfWorkerStartupException(
                        $"Configured PDFium worker was not found at '{fullConfiguredPath}'.");
                }

                return fullConfiguredPath;
            }

            var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "PdfiumRaster.Orchestrator.Worker.exe"
                : "PdfiumRaster.Orchestrator.Worker";
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path))
            {
                throw new PdfWorkerStartupException(
                    $"The bundled PDFium worker was not found at '{path}'. Ensure the package's build assets are enabled.");
            }

            return path;
        }

        internal static ProcessStartInfo CreateStartInfo(
            string workerPath,
            string pipeName,
            string token,
            string temporaryDirectory,
            TimeSpan startupTimeout)
        {
            var isDll = string.Equals(Path.GetExtension(workerPath), ".dll", StringComparison.OrdinalIgnoreCase);
            if (!isDll && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                EnsureUnixExecutable(workerPath);
            }

            var info = new ProcessStartInfo
            {
                FileName = isDll ? "dotnet" : workerPath,
                Arguments = isDll
                    ? $"{Quote(workerPath)} {Quote(pipeName)}"
                    : Quote(pipeName),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            info.EnvironmentVariables["PDFIUMRASTER_PIPE_TOKEN"] = token;
            info.EnvironmentVariables["PDFIUMRASTER_TEMP_DIRECTORY"] = temporaryDirectory;
            info.EnvironmentVariables["PDFIUMRASTER_STARTUP_TIMEOUT_TICKS"] =
                startupTimeout.Ticks.ToString(CultureInfo.InvariantCulture);
            return info;
        }

        private static void EnsureUnixExecutable(string path)
        {
            using var chmod = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/chmod",
                Arguments = $"700 {Quote(path)}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (chmod is null || !chmod.WaitForExit(5000) || chmod.ExitCode != 0)
            {
                throw new PdfWorkerStartupException($"Could not mark the bundled worker executable at '{path}'.");
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
