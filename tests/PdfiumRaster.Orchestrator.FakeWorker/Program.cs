using System.Buffers.Binary;
using System.IO.Pipes;
using PdfiumRaster.Orchestration;

namespace PdfiumRaster.Orchestrator.FakeWorker;

internal static class Program
{
    private const string ModeEnvironmentVariable = "PDFIUMRASTER_FAKE_WORKER_MODE";
    private const string TokenEnvironmentVariable = "PDFIUMRASTER_PIPE_TOKEN";

    private static async Task<int> Main(string[] args)
    {
        var mode = Environment.GetEnvironmentVariable(ModeEnvironmentVariable) ?? "healthy";
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
