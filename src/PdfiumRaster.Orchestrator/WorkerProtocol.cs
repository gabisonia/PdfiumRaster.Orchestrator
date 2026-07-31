using System.Buffers.Binary;
using System.Text;

namespace PdfiumRaster.Orchestration;

internal enum WorkerMessage : byte
{
    Hello = 1,
    Ready = 2,
    Request = 3,
    InputChunk = 4,
    InputEnd = 5,
    BitmapHeader = 6,
    OutputChunk = 7,
    Complete = 8,
    Error = 9,
    Shutdown = 10,
}

internal enum WorkerSourceKind : byte
{
    Path = 1,
    Content = 2,
}

internal enum WorkerOutputKind : byte
{
    Bitmap = 1,
    Path = 2,
    Stream = 3,
}

internal readonly struct WorkerFrame
{
    internal WorkerFrame(WorkerMessage message, byte[] payload)
    {
        Message = message;
        Payload = payload;
    }

    internal WorkerMessage Message { get; }
    internal byte[] Payload { get; }
}

internal sealed class WorkerRequest
{
    internal WorkerSourceKind SourceKind { get; set; }
    internal string? SourcePath { get; set; }
    internal WorkerOutputKind OutputKind { get; set; }
    internal string? OutputPath { get; set; }
    internal int PageIndex { get; set; }
    internal string? Password { get; set; }
    internal PdfImageConversionOptions Options { get; set; } = new();
}

internal static class WorkerProtocol
{
    internal const int Version = 1;
    internal const int ChunkSize = 64 * 1024;
    internal const int MaximumControlPayload = 1024 * 1024;
    private const int MaximumChunkPayload = ChunkSize;

    internal static async Task WriteFrameAsync(
        Stream stream,
        WorkerMessage message,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var maximum = message is WorkerMessage.InputChunk or WorkerMessage.OutputChunk
            ? MaximumChunkPayload
            : MaximumControlPayload;
        if (payload.Length > maximum)
        {
            throw new PdfWorkerProtocolException($"The {message} payload exceeds the protocol limit.");
        }

        var header = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), checked(payload.Length + 1));
        header[4] = (byte)message;
        await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
        if (payload.Length != 0)
        {
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static Task WriteEmptyFrameAsync(
        Stream stream,
        WorkerMessage message,
        CancellationToken cancellationToken)
    {
        return WriteFrameAsync(stream, message, Array.Empty<byte>(), cancellationToken);
    }

    internal static async Task<WorkerFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await ReadExactlyAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false);
        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (frameLength < 1 || frameLength > MaximumControlPayload + 1)
        {
            throw new PdfWorkerProtocolException($"Invalid worker frame length {frameLength}.");
        }

        var messageByte = new byte[1];
        await ReadExactlyAsync(stream, messageByte, cancellationToken).ConfigureAwait(false);
        var message = (WorkerMessage)messageByte[0];
        if (!Enum.IsDefined(typeof(WorkerMessage), message))
        {
            throw new PdfWorkerProtocolException($"Unknown worker message {messageByte[0]}.");
        }

        var payloadLength = frameLength - 1;
        var maximum = message is WorkerMessage.InputChunk or WorkerMessage.OutputChunk
            ? MaximumChunkPayload
            : MaximumControlPayload;
        if (payloadLength > maximum)
        {
            throw new PdfWorkerProtocolException($"The {message} payload exceeds the protocol limit.");
        }

        var payload = new byte[payloadLength];
        if (payloadLength != 0)
        {
            await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }

        return new WorkerFrame(message, payload);
    }

    internal static byte[] SerializeHello(string token)
    {
        return Serialize(writer =>
        {
            writer.Write(Version);
            writer.Write(token);
        });
    }

    internal static (int Version, string Token) DeserializeHello(byte[] payload)
    {
        return Deserialize(payload, reader => (reader.ReadInt32(), reader.ReadString()));
    }

    internal static byte[] SerializeRequest(WorkerRequest request)
    {
        return Serialize(writer =>
        {
            writer.Write((byte)request.SourceKind);
            WriteNullableString(writer, request.SourcePath);
            writer.Write((byte)request.OutputKind);
            WriteNullableString(writer, request.OutputPath);
            writer.Write(request.PageIndex);
            WriteNullableString(writer, request.Password);
            WriteOptions(writer, request.Options);
        });
    }

    internal static WorkerRequest DeserializeRequest(byte[] payload)
    {
        return Deserialize(payload, reader =>
        {
            var request = new WorkerRequest
            {
                SourceKind = (WorkerSourceKind)reader.ReadByte(),
                SourcePath = ReadNullableString(reader),
                OutputKind = (WorkerOutputKind)reader.ReadByte(),
                OutputPath = ReadNullableString(reader),
                PageIndex = reader.ReadInt32(),
                Password = ReadNullableString(reader),
                Options = ReadOptions(reader),
            };

            if (!Enum.IsDefined(typeof(WorkerSourceKind), request.SourceKind) ||
                !Enum.IsDefined(typeof(WorkerOutputKind), request.OutputKind))
            {
                throw new PdfWorkerProtocolException("The worker request contains an unknown source or output kind.");
            }

            return request;
        });
    }

    internal static byte[] SerializeBitmapHeader(int width, int height, int stride, int byteCount)
    {
        return Serialize(writer =>
        {
            writer.Write(width);
            writer.Write(height);
            writer.Write(stride);
            writer.Write(byteCount);
        });
    }

    internal static (int Width, int Height, int Stride, int ByteCount) DeserializeBitmapHeader(byte[] payload)
    {
        return Deserialize(payload,
            reader => (reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
    }

    internal static byte[] SerializeError(Exception exception)
    {
        return Serialize(writer =>
        {
            writer.Write(exception.GetType().FullName ?? exception.GetType().Name);
            writer.Write(exception.Message);
        });
    }

    internal static (string Type, string Message) DeserializeError(byte[] payload)
    {
        return Deserialize(payload, reader => (reader.ReadString(), reader.ReadString()));
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The worker pipe closed before a complete frame was received.");
            }

            offset += read;
        }
    }

    private static byte[] Serialize(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(writer);
        }

        return stream.ToArray();
    }

    private static T Deserialize<T>(byte[] payload, Func<BinaryReader, T> read)
    {
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var value = read(reader);
            if (stream.Position != stream.Length)
            {
                throw new PdfWorkerProtocolException("The worker frame contains trailing data.");
            }

            return value;
        }
        catch (PdfWorkerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException)
        {
            throw new PdfWorkerProtocolException("The worker frame payload is malformed.", exception);
        }
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static string? ReadNullableString(BinaryReader reader)
    {
        return reader.ReadBoolean() ? reader.ReadString() : null;
    }

    private static void WriteOptions(BinaryWriter writer, PdfImageConversionOptions options)
    {
        var render = options.Render ?? new PdfPageRenderOptions();
        var encoding = options.Encoding ?? new PdfImageEncodingOptions();
        writer.Write(render.Dpi);
        writer.Write(render.Scale);
        writer.Write((int)render.Rotation);
        writer.Write((int)render.Flags);
        WriteNullableInt32(writer, render.Width);
        WriteNullableInt32(writer, render.Height);
        writer.Write(render.WithAspectRatio);
        writer.Write((int)render.AntiAliasing);
        writer.Write(render.BackgroundColor);
        writer.Write(render.FillBackground);
        writer.Write((int)options.Format);
        writer.Write(encoding.Quality);
        WriteNullableInt32(writer, encoding.PngCompressionLevel);
        writer.Write((int)options.ColorMode);
        writer.Write(options.BlackAndWhiteThreshold);
    }

    private static PdfImageConversionOptions ReadOptions(BinaryReader reader)
    {
        var render = new PdfPageRenderOptions
        {
            Dpi = reader.ReadDouble(),
            Scale = reader.ReadDouble(),
            Rotation = (PdfPageRotation)reader.ReadInt32(),
            Flags = (PdfRenderFlags)reader.ReadInt32(),
            Width = ReadNullableInt32(reader),
            Height = ReadNullableInt32(reader),
            WithAspectRatio = reader.ReadBoolean(),
            AntiAliasing = (PdfAntiAliasing)reader.ReadInt32(),
            BackgroundColor = reader.ReadUInt32(),
            FillBackground = reader.ReadBoolean(),
        };

        return new PdfImageConversionOptions
        {
            Render = render,
            Format = (PdfImageOutputFormat)reader.ReadInt32(),
            Encoding = new PdfImageEncodingOptions
            {
                Quality = reader.ReadInt32(),
                PngCompressionLevel = ReadNullableInt32(reader),
            },
            ColorMode = (PdfImageColorMode)reader.ReadInt32(),
            BlackAndWhiteThreshold = reader.ReadByte(),
        };
    }

    private static void WriteNullableInt32(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
        {
            writer.Write(value.Value);
        }
    }

    private static int? ReadNullableInt32(BinaryReader reader)
    {
        return reader.ReadBoolean() ? reader.ReadInt32() : null;
    }
}
