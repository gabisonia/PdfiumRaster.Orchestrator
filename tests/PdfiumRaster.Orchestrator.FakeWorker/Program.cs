using System.Buffers.Binary;
using System.IO.Pipes;
using PdfiumRaster.Orchestration;

namespace PdfiumRaster.Orchestrator.FakeWorker;

internal static class Program
{
    private const string ModeEnvironmentVariable = "PDFIUMRASTER_FAKE_WORKER_MODE";
    private const string StateFileEnvironmentVariable = "PDFIUMRASTER_FAKE_WORKER_STATE_FILE";
    private const string TokenEnvironmentVariable = "PDFIUMRASTER_PIPE_TOKEN";

    private static async Task<int> Main(string[] args)
    {
        var mode = Environment.GetEnvironmentVariable(ModeEnvironmentVariable) ?? "healthy";
        var stateFile = Environment.GetEnvironmentVariable(StateFileEnvironmentVariable);
        if (mode == "disconnect-once" &&
            !string.IsNullOrWhiteSpace(stateFile) &&
            File.Exists(stateFile))
        {
            mode = "valid-bitmap";
        }

        if (mode == "disconnect-then-replacements-fail" &&
            !string.IsNullOrWhiteSpace(stateFile) &&
            File.Exists(stateFile))
        {
            return 27;
        }

        if (mode == "exit-before-connect")
        {
            return 20;
        }

        if (mode == "hang-before-connect")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 21;
        }

        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return 22;
        }

        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (string.IsNullOrEmpty(token))
        {
            return 23;
        }

        await using var pipe = new NamedPipeClientStream(
            ".",
            args[0],
            PipeDirection.InOut,
            WorkerProtocol.LocalPipeOptions);
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(connectionTimeout.Token).ConfigureAwait(false);

        if (mode == "unexpected-first-message")
        {
            await WorkerProtocol.WriteEmptyFrameAsync(pipe, WorkerMessage.Ready, CancellationToken.None)
                .ConfigureAwait(false);
            return 24;
        }

        var helloToken = mode == "wrong-token" ? "wrong-token" : token;
        var helloPayload = WorkerProtocol.SerializeHello(helloToken);
        if (mode == "wrong-version")
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                helloPayload.AsSpan(0, sizeof(int)),
                WorkerProtocol.Version + 1);
        }

        await WorkerProtocol.WriteFrameAsync(pipe, WorkerMessage.Hello, helloPayload, CancellationToken.None)
            .ConfigureAwait(false);
        var ready = await WorkerProtocol.ReadFrameAsync(pipe, CancellationToken.None).ConfigureAwait(false);
        WorkerProtocol.ValidateReady(ready);

        while (true)
        {
            var frame = await WorkerProtocol.ReadFrameAsync(pipe, CancellationToken.None).ConfigureAwait(false);
            if (frame.Message == WorkerMessage.Shutdown)
            {
                return 0;
            }

            if (frame.Message != WorkerMessage.Request)
            {
                return 25;
            }

            switch (mode)
            {
                case "disconnect-mid-frame":
                    await pipe.WriteAsync(new byte[]
                    {
                        5, 0, 0, 0,
                        (byte)WorkerMessage.OutputChunk,
                    }).ConfigureAwait(false);
                    await pipe.FlushAsync().ConfigureAwait(false);
                    return 26;
                case "stderr-disconnect":
                    Console.Error.WriteLine(new string('x', 9000) + "stderr-tail");
                    await Console.Error.FlushAsync().ConfigureAwait(false);
                    return 28;
                case "disconnect-then-replacements-fail":
                    if (!string.IsNullOrWhiteSpace(stateFile))
                    {
                        await File.WriteAllTextAsync(stateFile, "failed").ConfigureAwait(false);
                    }

                    return 29;
                case "disconnect-once":
                    if (!string.IsNullOrWhiteSpace(stateFile))
                    {
                        await File.WriteAllTextAsync(stateFile, "failed").ConfigureAwait(false);
                    }

                    return 30;
                case "invalid-bitmap-header":
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.BitmapHeader,
                            WorkerProtocol.SerializeBitmapHeader(width: 0, height: 1, stride: 4, byteCount: 4),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case "excess-bitmap-output":
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.BitmapHeader,
                            WorkerProtocol.SerializeBitmapHeader(width: 1, height: 1, stride: 4, byteCount: 4),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.OutputChunk,
                            new byte[] { 1, 2, 3, 4, 5 },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case "missing-bitmap-header":
                    await WorkerProtocol.WriteEmptyFrameAsync(
                            pipe,
                            WorkerMessage.Complete,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case "incomplete-bitmap":
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.BitmapHeader,
                            WorkerProtocol.SerializeBitmapHeader(width: 2, height: 1, stride: 8, byteCount: 8),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.OutputChunk,
                            new byte[] { 1, 2, 3, 4 },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await WorkerProtocol.WriteEmptyFrameAsync(
                            pipe,
                            WorkerMessage.Complete,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case "duplicate-bitmap-header":
                    var header = WorkerProtocol.SerializeBitmapHeader(width: 1, height: 1, stride: 4, byteCount: 4);
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.BitmapHeader,
                            header,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.BitmapHeader,
                            header,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case "unexpected-request-message":
                    await WorkerProtocol.WriteEmptyFrameAsync(
                            pipe,
                            WorkerMessage.InputEnd,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case "bitmap-header-for-stream":
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.BitmapHeader,
                            WorkerProtocol.SerializeBitmapHeader(width: 1, height: 1, stride: 4, byteCount: 4),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case "output-for-path":
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.OutputChunk,
                            new byte[] { 1 },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case "valid-bitmap":
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.BitmapHeader,
                            WorkerProtocol.SerializeBitmapHeader(width: 1, height: 1, stride: 4, byteCount: 4),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.OutputChunk,
                            new byte[] { 1, 2, 3, 4 },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await WorkerProtocol.WriteEmptyFrameAsync(
                            pipe,
                            WorkerMessage.Complete,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case "valid-stream":
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.OutputChunk,
                            new byte[] { 1, 2, 3, 4 },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await WorkerProtocol.WriteEmptyFrameAsync(
                            pipe,
                            WorkerMessage.Complete,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case "valid-path":
                    await WorkerProtocol.WriteEmptyFrameAsync(
                            pipe,
                            WorkerMessage.Complete,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                default:
                    await WorkerProtocol.WriteFrameAsync(
                            pipe,
                            WorkerMessage.Error,
                            WorkerProtocol.SerializeError(new InvalidOperationException("Fake worker request.")),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
            }
        }
    }
}
