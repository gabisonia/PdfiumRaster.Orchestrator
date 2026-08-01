using System.Globalization;
using System.IO.Pipes;
using PdfiumRaster;
using PdfiumRaster.Orchestration;

namespace PdfiumRaster.Orchestrator.Worker;

internal static class Program
{
    private const string TokenEnvironmentVariable = "PDFIUMRASTER_PIPE_TOKEN";
    private const string TemporaryDirectoryEnvironmentVariable = "PDFIUMRASTER_TEMP_DIRECTORY";
    private const string StartupTimeoutTicksEnvironmentVariable = "PDFIUMRASTER_STARTUP_TIMEOUT_TICKS";
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            await Console.Error.WriteLineAsync("A worker pipe name is required.");
            return 2;
        }

        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (string.IsNullOrEmpty(token))
        {
            await Console.Error.WriteLineAsync("The worker authentication token is missing.");
            return 3;
        }

        var temporaryDirectory = Environment.GetEnvironmentVariable(TemporaryDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(temporaryDirectory))
        {
            await Console.Error.WriteLineAsync("The worker temporary directory is missing.");
            return 4;
        }

        var startupTimeout = DefaultStartupTimeout;
        var startupTimeoutTicks = Environment.GetEnvironmentVariable(StartupTimeoutTicksEnvironmentVariable);
        if (!string.IsNullOrEmpty(startupTimeoutTicks))
        {
            if (!long.TryParse(
                    startupTimeoutTicks,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var ticks) ||
                ticks <= 0)
            {
                await Console.Error.WriteLineAsync("The worker startup timeout is invalid.");
                return 5;
            }

            startupTimeout = TimeSpan.FromTicks(ticks);
        }

        try
        {
            CreatePrivateTemporaryDirectory(temporaryDirectory);
            await using var pipe = new NamedPipeClientStream(
                ".",
                args[0],
                PipeDirection.InOut,
                WorkerProtocol.LocalPipeOptions);
            using var startupCancellation = new CancellationTokenSource(startupTimeout);
            await pipe.ConnectAsync(startupCancellation.Token).ConfigureAwait(false);
            await WorkerProtocol.WriteFrameAsync(
                    pipe,
                    WorkerMessage.Hello,
                    WorkerProtocol.SerializeHello(token),
                    startupCancellation.Token)
                .ConfigureAwait(false);

            var ready = await WorkerProtocol.ReadFrameAsync(pipe, startupCancellation.Token).ConfigureAwait(false);
            WorkerProtocol.ValidateReady(ready);

            using var pdfium = PdfiumLibrary.Initialize();
            while (true)
            {
                var frame = await WorkerProtocol.ReadFrameAsync(pipe, CancellationToken.None).ConfigureAwait(false);
                if (frame.Message == WorkerMessage.Shutdown)
                {
                    return 0;
                }

                if (frame.Message != WorkerMessage.Request)
                {
                    throw new PdfWorkerProtocolException(
                        $"Expected a request or shutdown message, but received {frame.Message}.");
                }

                var request = WorkerProtocol.DeserializeRequest(frame.Payload);
                await ProcessRequestAsync(pipe, request, temporaryDirectory).ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void CreatePrivateTemporaryDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        const UnixFileMode ownerOnly =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var directory = Directory.CreateDirectory(path, ownerOnly);
        directory.UnixFileMode = ownerOnly;
    }

    private static async Task ProcessRequestAsync(
        Stream pipe,
        WorkerRequest request,
        string temporaryDirectory)
    {
        string? temporaryPath = null;
        try
        {
            var pdfPath = request.SourcePath;
            if (request.SourceKind == WorkerSourceKind.Content)
            {
                temporaryPath = Path.Combine(
                    temporaryDirectory,
                    $"pdfium-raster-worker-{Environment.ProcessId}-{Guid.NewGuid():N}.pdf");
                await ReceiveInputAsync(pipe, temporaryPath).ConfigureAwait(false);
                pdfPath = temporaryPath;
            }

            if (string.IsNullOrWhiteSpace(pdfPath))
            {
                throw new PdfWorkerProtocolException("The request did not provide a PDF path or content.");
            }

            switch (request.OutputKind)
            {
                case WorkerOutputKind.Bitmap:
                    await RenderBitmapAsync(pipe, pdfPath, request).ConfigureAwait(false);
                    break;
                case WorkerOutputKind.Path:
                    if (string.IsNullOrWhiteSpace(request.OutputPath))
                    {
                        throw new PdfWorkerProtocolException("The request did not provide an output image path.");
                    }

                    PdfImageConverter.SavePage(
                        pdfPath,
                        request.PageIndex,
                        request.OutputPath,
                        request.Options,
                        request.Password);
                    break;
                case WorkerOutputKind.Stream:
                    await using (var output = new FramedOutputStream(pipe))
                    {
                        PdfImageConverter.SavePage(
                            pdfPath,
                            request.PageIndex,
                            output,
                            request.Options,
                            request.Password);
                    }

                    break;
                default:
                    throw new PdfWorkerProtocolException($"Unsupported output kind {request.OutputKind}.");
            }

            await WorkerProtocol.WriteEmptyFrameAsync(pipe, WorkerMessage.Complete, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var safeException = exception.Message.Length <= 64 * 1024
                ? exception
                : new InvalidOperationException(exception.Message.Substring(0, 64 * 1024));
            await WorkerProtocol.WriteFrameAsync(
                    pipe,
                    WorkerMessage.Error,
                    WorkerProtocol.SerializeError(safeException),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private static async Task ReceiveInputAsync(Stream pipe, string temporaryPath)
    {
        await using var file = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            WorkerProtocol.ChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            var frame = await WorkerProtocol.ReadFrameAsync(pipe, CancellationToken.None).ConfigureAwait(false);
            if (frame.Message == WorkerMessage.InputEnd)
            {
                break;
            }

            if (frame.Message != WorkerMessage.InputChunk)
            {
                throw new PdfWorkerProtocolException(
                    $"Expected PDF input bytes, but received {frame.Message}.");
            }

            await file.WriteAsync(frame.Payload, 0, frame.Payload.Length).ConfigureAwait(false);
        }

        await file.FlushAsync().ConfigureAwait(false);
    }

    private static async Task RenderBitmapAsync(Stream pipe, string pdfPath, WorkerRequest request)
    {
        var bitmap = PdfImageConverter.RenderPage(
            pdfPath,
            request.PageIndex,
            request.Options,
            request.Password);
        await WorkerProtocol.WriteFrameAsync(
                pipe,
                WorkerMessage.BitmapHeader,
                WorkerProtocol.SerializeBitmapHeader(
                    bitmap.Width,
                    bitmap.Height,
                    bitmap.Stride,
                    bitmap.Pixels.Length),
                CancellationToken.None)
            .ConfigureAwait(false);

        var offset = 0;
        while (offset < bitmap.Pixels.Length)
        {
            var count = Math.Min(WorkerProtocol.ChunkSize, bitmap.Pixels.Length - offset);
            var chunk = new byte[count];
            Buffer.BlockCopy(bitmap.Pixels, offset, chunk, 0, count);
            await WorkerProtocol.WriteFrameAsync(
                    pipe,
                    WorkerMessage.OutputChunk,
                    chunk,
                    CancellationToken.None)
                .ConfigureAwait(false);
            offset += count;
        }
    }

    private sealed class FramedOutputStream : Stream
    {
        private readonly Stream _pipe;

        internal FramedOutputStream(Stream pipe)
        {
            _pipe = pipe;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            _pipe.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return _pipe.FlushAsync(cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            while (count > 0)
            {
                var chunkLength = Math.Min(WorkerProtocol.ChunkSize, count);
                var chunk = new byte[chunkLength];
                Buffer.BlockCopy(buffer, offset, chunk, 0, chunkLength);
                await WorkerProtocol.WriteFrameAsync(
                        _pipe,
                        WorkerMessage.OutputChunk,
                        chunk,
                        cancellationToken)
                    .ConfigureAwait(false);
                offset += chunkLength;
                count -= chunkLength;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            // The named pipe is owned by the worker loop.
        }
    }
}
