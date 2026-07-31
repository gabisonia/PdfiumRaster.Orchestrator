using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

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
    private readonly PdfRenderQueueFullMode _queueFullMode;
    private readonly TimeSpan? _requestTimeout;
    private readonly WorkerSlot[] _workers;
    private readonly Task _completion;
    private Exception? _terminalError;
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
        _queueFullMode = options.QueueFullMode;
        _requestTimeout = options.RequestTimeout;
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
                var worker = new WorkerSlot(index);
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
    /// <param name="leaveOpen">Whether to leave the PDF stream open after request completion.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that produces a caller-owned BGRA bitmap.</returns>
    /// <remarks>The stream is transferred in chunks and spooled to a worker-owned temporary file for random access.</remarks>
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
    /// <param name="leaveOpen">Whether to leave the PDF stream open after request completion.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that completes after the image has been written.</returns>
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
    /// <param name="leaveOpen">Whether to leave the PDF stream open after request completion.</param>
    /// <param name="password">Optional document password.</param>
    /// <param name="cancellationToken">Cancels queue waiting or work that has not entered an uninterruptible stage.</param>
    /// <returns>A task that completes after the image has been written.</returns>
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
        ValidatePageIndex(pageIndex);
        var job = OrchestrationJob.CreateBitmap(source, pageIndex, SnapshotOptions(options), password, cancellationToken);
        return SubmitBitmapAsync(job);
    }

    private Task SubmitSave(
        InputSource source,
        int pageIndex,
        OutputTarget target,
        PdfImageConversionOptions? options,
        string? password,
        CancellationToken cancellationToken)
    {
        ValidatePageIndex(pageIndex);
        var job = OrchestrationJob.CreateSave(source, target, pageIndex, SnapshotOptions(options), password, cancellationToken);
        return SubmitSaveAsync(job);
    }

    private async Task<PdfBitmap> SubmitBitmapAsync(OrchestrationJob job)
    {
        await EnqueueAsync(job).ConfigureAwait(false);
        return await job.BitmapTask.ConfigureAwait(false);
    }

    private async Task SubmitSaveAsync(OrchestrationJob job)
    {
        await EnqueueAsync(job).ConfigureAwait(false);
        await job.SaveTask.ConfigureAwait(false);
    }

    private async Task EnqueueAsync(OrchestrationJob job)
    {
        ThrowIfNotAccepting();
        job.CancellationToken.ThrowIfCancellationRequested();

        if (_queueFullMode == PdfRenderQueueFullMode.Reject)
        {
            if (_queue.Writer.TryWrite(job))
            {
                return;
            }

            ThrowIfNotAccepting();
            throw new PdfRenderQueueFullException();
        }

        try
        {
            await _queue.Writer.WriteAsync(job, job.CancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            ThrowIfNotAccepting();
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
                        job.Fail(terminalError);
                        continue;
                    }

                    if (Volatile.Read(ref _cancelRequested) != 0 || job.CancellationToken.IsCancellationRequested)
                    {
                        job.Cancel();
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
        try
        {
            var execution = worker.ExecuteAsync(job);
            if (_requestTimeout.HasValue)
            {
                var timeoutTask = Task.Delay(_requestTimeout.Value);
                if (await Task.WhenAny(execution, timeoutTask).ConfigureAwait(false) != execution)
                {
                    worker.Kill();
                    _ = ObserveFailureAsync(execution);
                    job.Fail(new PdfWorkerTimeoutException(_requestTimeout.Value));
                    await RestartWorkerAsync(worker).ConfigureAwait(false);
                    return;
                }
            }

            var bitmap = await execution.ConfigureAwait(false);
            if (Volatile.Read(ref _cancelRequested) != 0 || job.CancellationToken.IsCancellationRequested)
            {
                job.Cancel();
            }
            else
            {
                job.Complete(bitmap);
            }
        }
        catch (PdfWorkerRemoteException exception)
        {
            job.Fail(exception);
        }
        catch (Exception exception)
        {
            job.Fail(exception);
            await RestartWorkerAsync(worker).ConfigureAwait(false);
        }
        finally
        {
            job.Cleanup();
        }
    }

    private async Task RestartWorkerAsync(WorkerSlot worker)
    {
        worker.Kill();
        worker.DisposeConnection();
        Exception? lastError = null;
        var delays = new[] { 250, 1000, 4000 };
        foreach (var delay in delays)
        {
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
            $"PDFium worker {worker.Index} could not be replaced after three attempts.",
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
                }
                else
                {
                    pending.Cancel();
                }
            }

            foreach (var worker in _workers)
            {
                worker.Dispose();
            }
        }
    }

    private void FaultOrchestrator(Exception exception)
    {
        Interlocked.CompareExchange(ref _terminalError, exception, null);
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
            _queue.Writer.TryComplete();
        }
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

    private static async Task ObserveFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The request already completed with a timeout.
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

    private static InputSource CreatePathSource(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
        {
            throw new ArgumentException("PDF path cannot be null or whitespace.", nameof(pdfPath));
        }

        return new PathInputSource(Path.GetFullPath(pdfPath));
    }

    private static InputSource CreateByteSource(byte[] pdfBytes)
    {
        if (pdfBytes is null)
        {
            throw new ArgumentNullException(nameof(pdfBytes));
        }

        if (pdfBytes.Length == 0)
        {
            throw new ArgumentException("PDF bytes cannot be empty.", nameof(pdfBytes));
        }

        return new ByteInputSource(pdfBytes);
    }

    private static InputSource CreateStreamSource(Stream pdfStream, bool leaveOpen)
    {
        if (pdfStream is null)
        {
            throw new ArgumentNullException(nameof(pdfStream));
        }

        if (!pdfStream.CanRead)
        {
            throw new ArgumentException("PDF stream must be readable.", nameof(pdfStream));
        }

        return new StreamInputSource(pdfStream, leaveOpen);
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
        internal virtual Task SendAsync(Stream pipe) => Task.CompletedTask;
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

        internal ByteInputSource(byte[] bytes)
        {
            _bytes = bytes;
        }

        internal override WorkerSourceKind Kind => WorkerSourceKind.Content;

        internal override async Task SendAsync(Stream pipe)
        {
            var offset = 0;
            while (offset < _bytes.Length)
            {
                var count = Math.Min(WorkerProtocol.ChunkSize, _bytes.Length - offset);
                var chunk = new byte[count];
                Buffer.BlockCopy(_bytes, offset, chunk, 0, count);
                await WorkerProtocol.WriteFrameAsync(pipe, WorkerMessage.InputChunk, chunk, CancellationToken.None)
                    .ConfigureAwait(false);
                offset += count;
            }
        }
    }

    private sealed class StreamInputSource : InputSource
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;

        internal StreamInputSource(Stream stream, bool leaveOpen)
        {
            _stream = stream;
            _leaveOpen = leaveOpen;
        }

        internal override WorkerSourceKind Kind => WorkerSourceKind.Content;

        internal override async Task SendAsync(Stream pipe)
        {
            var buffer = new byte[WorkerProtocol.ChunkSize];
            int read;
            while ((read = await _stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                await WorkerProtocol.WriteFrameAsync(pipe, WorkerMessage.InputChunk, chunk, CancellationToken.None)
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

    private sealed class OrchestrationJob
    {
        private readonly TaskCompletionSource<PdfBitmap>? _bitmapCompletion;
        private readonly TaskCompletionSource<object?>? _saveCompletion;
        private int _cleaned;

        private OrchestrationJob(
            InputSource source,
            OutputTarget target,
            int pageIndex,
            PdfImageConversionOptions options,
            string? password,
            CancellationToken cancellationToken,
            bool bitmap)
        {
            Source = source;
            Target = target;
            PageIndex = pageIndex;
            Options = options;
            Password = password;
            CancellationToken = cancellationToken;
            if (bitmap)
            {
                _bitmapCompletion = new TaskCompletionSource<PdfBitmap>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                _saveCompletion = new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        internal InputSource Source { get; }
        internal OutputTarget Target { get; }
        internal int PageIndex { get; }
        internal PdfImageConversionOptions Options { get; }
        internal string? Password { get; }
        internal CancellationToken CancellationToken { get; }
        internal Task<PdfBitmap> BitmapTask => _bitmapCompletion!.Task;
        internal Task SaveTask => _saveCompletion!.Task;

        internal static OrchestrationJob CreateBitmap(
            InputSource source,
            int pageIndex,
            PdfImageConversionOptions options,
            string? password,
            CancellationToken cancellationToken)
        {
            return new OrchestrationJob(source, new BitmapOutputTarget(), pageIndex, options, password, cancellationToken, true);
        }

        internal static OrchestrationJob CreateSave(
            InputSource source,
            OutputTarget target,
            int pageIndex,
            PdfImageConversionOptions options,
            string? password,
            CancellationToken cancellationToken)
        {
            return new OrchestrationJob(source, target, pageIndex, options, password, cancellationToken, false);
        }

        internal WorkerRequest CreateRequest()
        {
            return new WorkerRequest
            {
                SourceKind = Source.Kind,
                SourcePath = Source.Path,
                OutputKind = Target.Kind,
                OutputPath = Target.Path,
                PageIndex = PageIndex,
                Password = Password,
                Options = Options,
            };
        }

        internal void Complete(PdfBitmap? bitmap)
        {
            if (_bitmapCompletion is not null)
            {
                _bitmapCompletion.TrySetResult(bitmap ??
                    throw new PdfWorkerProtocolException("The worker did not return a bitmap."));
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
            else
            {
                _saveCompletion!.TrySetException(exception);
            }

            Cleanup();
        }

        internal void Cancel()
        {
            if (_bitmapCompletion is not null)
            {
                _bitmapCompletion.TrySetCanceled();
            }
            else
            {
                _saveCompletion!.TrySetCanceled();
            }

            Cleanup();
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
        private WorkerConnection? _connection;

        internal WorkerSlot(int index)
        {
            Index = index;
        }

        internal int Index { get; }

        internal async Task StartAsync()
        {
            _connection = await WorkerConnection.StartAsync(Index).ConfigureAwait(false);
        }

        internal Task<PdfBitmap?> ExecuteAsync(OrchestrationJob job)
        {
            return (_connection ?? throw new PdfWorkerStartupException("The worker is not connected."))
                .ExecuteAsync(job);
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
        private const int StartupTimeoutMilliseconds = 15000;
        private const int StandardErrorLimit = 8192;
        private readonly NamedPipeServerStream _pipe;
        private readonly Process _process;
        private readonly string _temporaryDirectory;
        private readonly StringBuilder _standardError = new();
        private readonly object _errorSync = new();
        private readonly TaskCompletionSource<object?> _processExited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        private WorkerConnection(NamedPipeServerStream pipe, Process process, string temporaryDirectory)
        {
            _pipe = pipe;
            _process = process;
            _temporaryDirectory = temporaryDirectory;
            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;
            _process.ErrorDataReceived += OnErrorDataReceived;
            _process.BeginErrorReadLine();
            if (_process.HasExited)
            {
                _processExited.TrySetResult(null);
            }
        }

        internal static async Task<WorkerConnection> StartAsync(int index)
        {
            var pipeName = $"pdr-{index:x}-{Guid.NewGuid():N}".Substring(0, 24);
            var token = CreateToken();
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"pdfium-raster-worker-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            Process? process = null;
            WorkerConnection? connection = null;
            try
            {
                var executable = WorkerExecutableResolver.Resolve();
                var startInfo = WorkerExecutableResolver.CreateStartInfo(
                    executable,
                    pipeName,
                    token,
                    temporaryDirectory);
                process = Process.Start(startInfo) ??
                    throw new PdfWorkerStartupException("The PDFium worker process did not start.");
                connection = new WorkerConnection(pipe, process, temporaryDirectory);

                using var timeout = new CancellationTokenSource(StartupTimeoutMilliseconds);
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
                if (hello.Message != WorkerMessage.Hello)
                {
                    throw new PdfWorkerProtocolException("The worker did not begin with a protocol handshake.");
                }

                var (version, actualToken) = WorkerProtocol.DeserializeHello(hello.Payload);
                if (version != WorkerProtocol.Version)
                {
                    throw new PdfWorkerProtocolException(
                        $"Worker protocol version {version} is incompatible with client version {WorkerProtocol.Version}.");
                }

                if (!FixedTimeEquals(token, actualToken))
                {
                    throw new PdfWorkerProtocolException("The worker authentication token did not match.");
                }

                await WorkerProtocol.WriteEmptyFrameAsync(pipe, WorkerMessage.Ready, timeout.Token)
                    .ConfigureAwait(false);
                return connection;
            }
            catch (Exception exception)
            {
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

        internal async Task<PdfBitmap?> ExecuteAsync(OrchestrationJob job)
        {
            try
            {
                var request = job.CreateRequest();
                await WorkerProtocol.WriteFrameAsync(
                        _pipe,
                        WorkerMessage.Request,
                        WorkerProtocol.SerializeRequest(request),
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (request.SourceKind == WorkerSourceKind.Content)
                {
                    await job.Source.SendAsync(_pipe).ConfigureAwait(false);
                    await WorkerProtocol.WriteEmptyFrameAsync(_pipe, WorkerMessage.InputEnd, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                byte[]? bitmapPixels = null;
                var bitmapOffset = 0;
                var width = 0;
                var height = 0;
                var stride = 0;

                while (true)
                {
                    var frame = await WorkerProtocol.ReadFrameAsync(_pipe, CancellationToken.None)
                        .ConfigureAwait(false);
                    switch (frame.Message)
                    {
                        case WorkerMessage.BitmapHeader:
                        {
                            if (request.OutputKind != WorkerOutputKind.Bitmap || bitmapPixels is not null)
                            {
                                throw new PdfWorkerProtocolException("The worker sent an unexpected bitmap header.");
                            }

                            var header = WorkerProtocol.DeserializeBitmapHeader(frame.Payload);
                            ValidateBitmapHeader(header.Width, header.Height, header.Stride, header.ByteCount);
                            width = header.Width;
                            height = header.Height;
                            stride = header.Stride;
                            bitmapPixels = new byte[header.ByteCount];
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
                                await job.Target.Stream!.WriteAsync(
                                        frame.Payload,
                                        0,
                                        frame.Payload.Length,
                                        CancellationToken.None)
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

                                return new PdfBitmap(width, height, stride, bitmapPixels);
                            }

                            return null;
                        case WorkerMessage.Error:
                        {
                            var error = WorkerProtocol.DeserializeError(frame.Payload);
                            throw new PdfWorkerRemoteException(error.Type, error.Message);
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

        private static bool FixedTimeEquals(string expected, string actual)
        {
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var actualBytes = Encoding.UTF8.GetBytes(actual);
            if (expectedBytes.Length != actualBytes.Length)
            {
                return false;
            }

            var difference = 0;
            for (var index = 0; index < expectedBytes.Length; index++)
            {
                difference |= expectedBytes[index] ^ actualBytes[index];
            }

            return difference == 0;
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
            string temporaryDirectory)
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
