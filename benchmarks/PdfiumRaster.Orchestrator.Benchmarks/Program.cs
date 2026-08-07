using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;
using PdfiumRaster.Orchestration;

namespace PdfiumRaster.Orchestrator.Benchmarks;

internal static class Program
{
    private const double MaximumAllocationRatio = 0.50;
    private const double MaximumLatencyRatio = 1.05;

    private static int Main(string[] args)
    {
        if (args.Length == 3 && string.Equals(args[0], "compare", StringComparison.Ordinal))
        {
            return Compare(args[1], args[2]);
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

    private static int Compare(string baselineDirectory, string candidateDirectory)
    {
        var baseline = ReadResults(baselineDirectory);
        var candidate = ReadResults(candidateDirectory);
        var failed = false;

        foreach (var method in baseline.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!candidate.TryGetValue(method, out var candidateResult))
            {
                Console.Error.WriteLine($"Candidate benchmark result is missing: {method}.");
                failed = true;
                continue;
            }

            var baselineResult = baseline[method];
            var allocationRatio = candidateResult.AllocatedBytes / baselineResult.AllocatedBytes;
            var latencyRatio = candidateResult.MedianNanoseconds / baselineResult.MedianNanoseconds;
            Console.WriteLine(
                $"{method}: allocations {allocationRatio:P1} of 1.0.0, median latency {latencyRatio:P1} of 1.0.0.");
            if (method.StartsWith("Protocol", StringComparison.Ordinal) &&
                allocationRatio > MaximumAllocationRatio)
            {
                Console.Error.WriteLine(
                    $"{method} did not reduce managed allocations by at least 50 percent.");
                failed = true;
            }

            if (!method.StartsWith("Protocol", StringComparison.Ordinal) &&
                latencyRatio > MaximumLatencyRatio)
            {
                Console.Error.WriteLine($"{method} regressed median latency by more than 5 percent.");
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }

    private static Dictionary<string, BenchmarkResult> ReadResults(string directory)
    {
        var resultFile = Directory.GetFiles(directory, "*-report-full-compressed.json", SearchOption.AllDirectories)
            .Single();
        using var document = JsonDocument.Parse(File.ReadAllBytes(resultFile));
        var results = new Dictionary<string, BenchmarkResult>(StringComparer.Ordinal);
        foreach (var benchmark in document.RootElement.GetProperty("Benchmarks").EnumerateArray())
        {
            var method = benchmark.GetProperty("Method").GetString() ??
                throw new InvalidDataException("A benchmark method name is missing.");
            var median = benchmark.GetProperty("Statistics").GetProperty("Median").GetDouble();
            var allocated = benchmark.GetProperty("Memory").GetProperty("BytesAllocatedPerOperation").GetDouble();
            results.Add(method, new BenchmarkResult(median, allocated));
        }

        if (results.Count == 0)
        {
            throw new InvalidDataException($"No benchmark results were found under {directory}.");
        }

        return results;
    }

    private readonly record struct BenchmarkResult(double MedianNanoseconds, double AllocatedBytes);
}

[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10, invocationCount: 1)]
public class PipeTransferBenchmarks
{
    private const int PayloadSize = 16 * 1024 * 1024;
    private const int ProtocolChunkSize = 64 * 1024;
    private const string WorkerPathVariable = "PDFIUMRASTER_WORKER_PATH";
    private const string FakeModeVariable = "PDFIUMRASTER_FAKE_WORKER_MODE";
    private byte[] _payload = null!;
    private MemoryStream _input = null!;
    private MemoryStream _output = null!;
    private byte[] _framedPayload = null!;
    private MemoryStream _protocolOutput = null!;
    private readonly byte[] _protocolHeader = new byte[5];
    private bool _useLegacyProtocol;
    private PdfRenderOrchestrator _orchestrator = null!;
    private string? _originalWorkerPath;
    private string? _originalFakeMode;

    [GlobalSetup]
    public void Setup()
    {
        var workerPath = Environment.GetEnvironmentVariable("PDFIUMRASTER_BENCHMARK_WORKER_PATH");
        if (string.IsNullOrWhiteSpace(workerPath))
        {
            throw new InvalidOperationException("PDFIUMRASTER_BENCHMARK_WORKER_PATH must identify the fake worker.");
        }

        _originalWorkerPath = Environment.GetEnvironmentVariable(WorkerPathVariable);
        _originalFakeMode = Environment.GetEnvironmentVariable(FakeModeVariable);
        _useLegacyProtocol = string.Equals(
            Environment.GetEnvironmentVariable("PDFIUMRASTER_BENCHMARK_BASELINE"),
            "1",
            StringComparison.Ordinal);
        Environment.SetEnvironmentVariable(WorkerPathVariable, workerPath);
        Environment.SetEnvironmentVariable(FakeModeVariable, "echo-content");
        _payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        for (var index = 0; index < _payload.Length; index++)
        {
            _payload[index] = (byte)index;
        }

        _input = new MemoryStream(_payload, writable: false);
        _output = new MemoryStream(PayloadSize);
        _framedPayload = CreateFramedPayload(_payload);
        _protocolOutput = new MemoryStream(_framedPayload.Length);
        _orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions
        {
            WorkerCount = 1,
            QueueCapacity = 1,
        });
    }

    [Benchmark]
    public async Task<int> ByteArrayToBitmap()
    {
        var bitmap = await _orchestrator.RenderPageAsync(_payload, 0).ConfigureAwait(false);
        return bitmap.Pixels.Length == PayloadSize
            ? bitmap.Pixels.Length
            : throw new InvalidDataException("The bitmap transfer was incomplete.");
    }

    [Benchmark]
    public async Task<long> StreamToEncodedStream()
    {
        _input.Position = 0;
        _output.Position = 0;
        _output.SetLength(0);
        await _orchestrator.SavePageAsync(_input, 0, _output, leaveOpen: true).ConfigureAwait(false);
        return _output.Length == PayloadSize
            ? _output.Length
            : throw new InvalidDataException("The encoded stream transfer was incomplete.");
    }

    [Benchmark]
    public long ProtocolWrite16MiB()
    {
        _protocolOutput.Position = 0;
        _protocolOutput.SetLength(0);
        var offset = 0;
        while (offset < _payload.Length)
        {
            var count = Math.Min(ProtocolChunkSize, _payload.Length - offset);
            var header = _useLegacyProtocol ? new byte[5] : _protocolHeader;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, count + 1);
            header[4] = 4;
            _protocolOutput.Write(header, 0, header.Length);
            if (_useLegacyProtocol)
            {
                var chunk = new byte[count];
                Buffer.BlockCopy(_payload, offset, chunk, 0, count);
                _protocolOutput.Write(chunk, 0, chunk.Length);
            }
            else
            {
                _protocolOutput.Write(_payload, offset, count);
            }

            offset += count;
        }

        return _protocolOutput.Length;
    }

    [Benchmark]
    public int ProtocolRead16MiB()
    {
        using var input = new MemoryStream(_framedPayload, writable: false);
        var destination = GC.AllocateUninitializedArray<byte>(PayloadSize);
        var offset = 0;
        while (offset < destination.Length)
        {
            var header = _useLegacyProtocol ? new byte[5] : _protocolHeader;
            input.ReadExactly(header, 0, header.Length);
            var payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header) - 1;
            if (_useLegacyProtocol)
            {
                var payload = new byte[payloadLength];
                input.ReadExactly(payload, 0, payload.Length);
                Buffer.BlockCopy(payload, 0, destination, offset, payload.Length);
            }
            else
            {
                input.ReadExactly(destination, offset, payloadLength);
            }

            offset += payloadLength;
        }

        return destination[^1];
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_orchestrator is not null)
        {
            await _orchestrator.CompleteAsync().ConfigureAwait(false);
            await _orchestrator.DisposeAsync().ConfigureAwait(false);
        }

        _input?.Dispose();
        _output?.Dispose();
        _protocolOutput?.Dispose();
        Environment.SetEnvironmentVariable(WorkerPathVariable, _originalWorkerPath);
        Environment.SetEnvironmentVariable(FakeModeVariable, _originalFakeMode);
    }

    private static byte[] CreateFramedPayload(byte[] payload)
    {
        var frameCount = (payload.Length + ProtocolChunkSize - 1) / ProtocolChunkSize;
        var framed = GC.AllocateUninitializedArray<byte>(payload.Length + frameCount * 5);
        var sourceOffset = 0;
        var destinationOffset = 0;
        while (sourceOffset < payload.Length)
        {
            var count = Math.Min(ProtocolChunkSize, payload.Length - sourceOffset);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                framed.AsSpan(destinationOffset, sizeof(int)),
                count + 1);
            framed[destinationOffset + sizeof(int)] = 7;
            Buffer.BlockCopy(payload, sourceOffset, framed, destinationOffset + 5, count);
            sourceOffset += count;
            destinationOffset += count + 5;
        }

        return framed;
    }
}
