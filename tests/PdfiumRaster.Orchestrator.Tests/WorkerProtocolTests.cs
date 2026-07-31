namespace PdfiumRaster.Orchestration.Tests;

public sealed class WorkerProtocolTests
{
    [Fact]
    public void RequestRoundTripsAllConversionOptions()
    {
        var request = new WorkerRequest
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
