using System.Buffers.Binary;
using System.IO.Pipes;

namespace PdfiumRaster.Orchestration.Tests;

public sealed class WorkerProtocolTests
{
    [Fact]
    public void LocalPipeOptionsRestrictAsyncConnectionsToCurrentUser()
    {
        Assert.Equal(
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            WorkerProtocol.LocalPipeOptions);
    }

    [Fact]
    public async Task VersionOneHelloFrameMatchesGoldenVector()
    {
        Assert.Equal(1, WorkerProtocol.Version);
        var expected = Convert.FromHexString("0B000000010100000005746F6B656E");
        using var stream = new MemoryStream();

        await WorkerProtocol.WriteFrameAsync(
            stream,
            WorkerMessage.Hello,
            WorkerProtocol.SerializeHello("token"),
            CancellationToken.None);

        Assert.Equal(expected, stream.ToArray());
    }

    [Fact]
    public void VersionOneRequestPayloadMatchesGoldenVector()
    {
        var expected = Convert.FromHexString(
            "01010E2F746D702F696E7075742E70646602010F2F746D702F6F75747075742E706E67" +
            "0300000001067365637265740000000000006240000000000000F83F0100000001000000" +
            "0120030000000001000000302010FF0001000000510000000102000000010000005A");

        var actual = WorkerProtocol.SerializeRequest(CreateGoldenRequest());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void VersionOneResponsePayloadsMatchGoldenVectors()
    {
        var expectedBitmapHeader = Convert.FromHexString("02000000030000000800000018000000");
        var expectedError = Convert.FromHexString(
            "2053797374656D2E496E76616C69644F7065726174696F6E457863657074696F6E03626164");

        Assert.Equal(
            expectedBitmapHeader,
            WorkerProtocol.SerializeBitmapHeader(width: 2, height: 3, stride: 8, byteCount: 24));
        Assert.Equal(expectedError, WorkerProtocol.SerializeError(new InvalidOperationException("bad")));
    }

    [Fact]
    public void RequestRoundTripsAllConversionOptions()
    {
        var request = CreateGoldenRequest();

        var actual = WorkerProtocol.DeserializeRequest(WorkerProtocol.SerializeRequest(request));

        Assert.Equal(request.SourcePath, actual.SourcePath);
        Assert.Equal(request.OutputPath, actual.OutputPath);
        Assert.Equal(3, actual.PageIndex);
        Assert.Equal("secret", actual.Password);
        Assert.Equal(144, actual.Options.Render.Dpi);
        Assert.Equal(1.5, actual.Options.Render.Scale);
        Assert.Equal(PdfPageRotation.Rotate90, actual.Options.Render.Rotation);
        Assert.Equal(800, actual.Options.Render.Width);
        Assert.Equal(PdfImageOutputFormat.Png, actual.Options.Format);
        Assert.Equal(2, actual.Options.Encoding.PngCompressionLevel);
        Assert.Equal(PdfImageColorMode.Grayscale, actual.Options.ColorMode);
    }

    [Fact]
    public void WorkerHandshakeAcceptsExpectedVersionAndToken()
    {
        var frame = new WorkerFrame(WorkerMessage.Hello, WorkerProtocol.SerializeHello("expected"));

        WorkerProtocol.ValidateWorkerHello(frame, "expected");
    }

    [Fact]
    public void WorkerHandshakeRejectsUnexpectedFirstMessage()
    {
        var frame = new WorkerFrame(WorkerMessage.Ready, Array.Empty<byte>());

        var exception = Assert.Throws<PdfWorkerProtocolException>(
            () => WorkerProtocol.ValidateWorkerHello(frame, "expected"));

        Assert.Contains("did not begin with a protocol handshake", exception.Message);
    }

    [Fact]
    public void WorkerHandshakeRejectsIncompatibleVersion()
    {
        var payload = WorkerProtocol.SerializeHello("expected");
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, sizeof(int)), WorkerProtocol.Version + 1);
        var frame = new WorkerFrame(WorkerMessage.Hello, payload);

        var exception = Assert.Throws<PdfWorkerProtocolException>(
            () => WorkerProtocol.ValidateWorkerHello(frame, "expected"));

        Assert.Contains("incompatible", exception.Message);
    }

    [Fact]
    public void WorkerHandshakeRejectsWrongToken()
    {
        var frame = new WorkerFrame(WorkerMessage.Hello, WorkerProtocol.SerializeHello("unexpected"));

        var exception = Assert.Throws<PdfWorkerProtocolException>(
            () => WorkerProtocol.ValidateWorkerHello(frame, "expected"));

        Assert.Contains("token did not match", exception.Message);
    }

    [Fact]
    public void WorkerHandshakeRejectsMalformedHelloPayload()
    {
        var frame = new WorkerFrame(WorkerMessage.Hello, Array.Empty<byte>());

        Assert.Throws<PdfWorkerProtocolException>(
            () => WorkerProtocol.ValidateWorkerHello(frame, "expected"));
    }

    [Fact]
    public void ReadyHandshakeRequiresAnEmptyReadyFrame()
    {
        WorkerProtocol.ValidateReady(new WorkerFrame(WorkerMessage.Ready, Array.Empty<byte>()));

        Assert.Throws<PdfWorkerProtocolException>(
            () => WorkerProtocol.ValidateReady(new WorkerFrame(WorkerMessage.Hello, Array.Empty<byte>())));
        Assert.Throws<PdfWorkerProtocolException>(
            () => WorkerProtocol.ValidateReady(new WorkerFrame(WorkerMessage.Ready, new byte[] { 1 })));
    }

    [Fact]
    public async Task FrameReaderHandlesFragmentedReads()
    {
        using var storage = new MemoryStream();
        await WorkerProtocol.WriteFrameAsync(
            storage,
            WorkerMessage.Hello,
            WorkerProtocol.SerializeHello("token"),
            CancellationToken.None);
        storage.Position = 0;
        using var fragmented = new FragmentedReadStream(storage);

        var frame = await WorkerProtocol.ReadFrameAsync(fragmented, CancellationToken.None);
        var hello = WorkerProtocol.DeserializeHello(frame.Payload);

        Assert.Equal(WorkerMessage.Hello, frame.Message);
        Assert.Equal(WorkerProtocol.Version, hello.Version);
        Assert.Equal("token", hello.Token);
    }

    [Fact]
    public async Task FrameReaderRejectsOversizedLength()
    {
        var bytes = BitConverter.GetBytes(WorkerProtocol.MaximumControlPayload + 2);
        using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<PdfWorkerProtocolException>(
            () => WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task FrameReaderRejectsNonPositiveLength(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));

        await Assert.ThrowsAsync<PdfWorkerProtocolException>(
            () => WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task FrameReaderAppliesTheSmallerChunkPayloadLimit()
    {
        var frameLength = WorkerProtocol.ChunkSize + 2;
        using var stream = new MemoryStream();
        await stream.WriteAsync(BitConverter.GetBytes(frameLength));
        stream.WriteByte((byte)WorkerMessage.OutputChunk);
        stream.Position = 0;

        await Assert.ThrowsAsync<PdfWorkerProtocolException>(
            () => WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task FrameReaderRejectsUnknownMessage()
    {
        using var stream = new MemoryStream(new byte[] { 1, 0, 0, 0, byte.MaxValue });

        await Assert.ThrowsAsync<PdfWorkerProtocolException>(
            () => WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task FrameReaderRejectsTruncatedHandshake()
    {
        using var stream = new MemoryStream(new byte[]
        {
            3, 0, 0, 0,
            (byte)WorkerMessage.Hello,
            1,
        });

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void RequestReaderRejectsTrailingDataAndUnknownKinds()
    {
        var request = new WorkerRequest
        {
            SourceKind = WorkerSourceKind.Path,
            SourcePath = "/tmp/input.pdf",
            OutputKind = WorkerOutputKind.Bitmap,
            PageIndex = 0,
        };
        var trailing = WorkerProtocol.SerializeRequest(request).Concat(new byte[] { 1 }).ToArray();
        Assert.Throws<PdfWorkerProtocolException>(() => WorkerProtocol.DeserializeRequest(trailing));

        request.SourceKind = (WorkerSourceKind)byte.MaxValue;
        Assert.Throws<PdfWorkerProtocolException>(
            () => WorkerProtocol.DeserializeRequest(WorkerProtocol.SerializeRequest(request)));
    }

    [Fact]
    public async Task FrameWriterRejectsOversizedChunk()
    {
        using var stream = new MemoryStream();
        var payload = new byte[WorkerProtocol.ChunkSize + 1];

        await Assert.ThrowsAsync<PdfWorkerProtocolException>(
            () => WorkerProtocol.WriteFrameAsync(
                stream,
                WorkerMessage.InputChunk,
                payload,
                CancellationToken.None));
    }

    [Fact]
    public async Task FrameWriterRejectsOversizedControlPayload()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<PdfWorkerProtocolException>(
            () => WorkerProtocol.WriteFrameAsync(
                stream,
                WorkerMessage.Request,
                new byte[WorkerProtocol.MaximumControlPayload + 1],
                CancellationToken.None));
    }

    [Fact]
    public void BitmapAndErrorPayloadsRoundTripAndRejectMalformedData()
    {
        Assert.Equal(
            (Width: 2, Height: 3, Stride: 8, ByteCount: 24),
            WorkerProtocol.DeserializeBitmapHeader(
                WorkerProtocol.SerializeBitmapHeader(width: 2, height: 3, stride: 8, byteCount: 24)));

        var error = WorkerProtocol.DeserializeError(
            WorkerProtocol.SerializeError(new InvalidOperationException("bad")));
        Assert.Equal(typeof(InvalidOperationException).FullName, error.Type);
        Assert.Equal("bad", error.Message);
        Assert.Throws<PdfWorkerProtocolException>(
            () => WorkerProtocol.DeserializeBitmapHeader(Array.Empty<byte>()));
        Assert.Throws<PdfWorkerProtocolException>(
            () => WorkerProtocol.DeserializeError(new byte[] { 1 }));
    }

    [Fact]
    public void RequestRoundTripPreservesNullPathsPasswordAndDefaultOptionObjects()
    {
        var request = new WorkerRequest
        {
            SourceKind = WorkerSourceKind.Content,
            OutputKind = WorkerOutputKind.Stream,
            PageIndex = 0,
            Options = new PdfImageConversionOptions { Render = null!, Encoding = null! },
        };

        var actual = WorkerProtocol.DeserializeRequest(WorkerProtocol.SerializeRequest(request));

        Assert.Null(actual.SourcePath);
        Assert.Null(actual.OutputPath);
        Assert.Null(actual.Password);
        Assert.NotNull(actual.Options.Render);
        Assert.NotNull(actual.Options.Encoding);
    }

    private static WorkerRequest CreateGoldenRequest()
    {
        return new WorkerRequest
        {
            SourceKind = WorkerSourceKind.Path,
            SourcePath = "/tmp/input.pdf",
            OutputKind = WorkerOutputKind.Path,
            OutputPath = "/tmp/output.png",
            PageIndex = 3,
            Password = "secret",
            Options = new PdfImageConversionOptions
            {
                Render = new PdfPageRenderOptions
                {
                    Dpi = 144,
                    Scale = 1.5,
                    Rotation = PdfPageRotation.Rotate90,
                    Flags = PdfRenderFlags.Annot,
                    Width = 800,
                    AntiAliasing = PdfAntiAliasing.Text,
                    BackgroundColor = 0xFF102030,
                    FillBackground = false,
                },
                Format = PdfImageOutputFormat.Png,
                Encoding = new PdfImageEncodingOptions { Quality = 81, PngCompressionLevel = 2 },
                ColorMode = PdfImageColorMode.Grayscale,
                BlackAndWhiteThreshold = 90,
            },
        };
    }

    private sealed class FragmentedReadStream : Stream
    {
        private readonly Stream _inner;

        internal FragmentedReadStream(Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, Math.Min(1, count));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return _inner.ReadAsync(buffer, offset, Math.Min(1, count), cancellationToken);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
