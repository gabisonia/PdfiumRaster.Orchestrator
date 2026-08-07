# Architecture

PdfiumRaster.Orchestrator is a process-isolation and scheduling layer over
[`PdfiumRaster`](https://github.com/gabisonia/PdfiumRaster). The application-facing library targets
`netstandard2.1`; packaged self-contained .NET 10 workers perform rendering through their PdfiumRaster dependency.

## Process topology

```text
application
    |
    | public PdfRenderOrchestrator API
    v
PdfRenderOrchestrator
    |
    | bounded request queue
    |
    |-- NamedPipeServerStream <==> NamedPipeClientStream -- worker 1 -- PdfiumRaster -- PDFium
    |-- NamedPipeServerStream <==> NamedPipeClientStream -- worker 2 -- PdfiumRaster -- PDFium
    `-- NamedPipeServerStream <==> NamedPipeClientStream -- worker N -- PdfiumRaster -- PDFium
```

There is one child process and one persistent, private named-pipe connection per worker slot. A worker handles one
request at a time, but separate workers run concurrently. Each worker also owns an independent native PDFium runtime,
address space, and process-wide native-call lock.

PDFium exposes process-global initialization and destruction, and its public API requires calls to be single-threaded
or externally serialized. PdfiumRaster therefore reference-counts one initialized runtime per process and protects
native calls with a process-wide lock. Multiple managed initialization handles in one process share that runtime; they
do not create parallel PDFium instances. Separate operating-system processes are the concurrency boundary.

## Named-pipe roles

The words *client* and *server* can be confusing here. The application-facing assembly is commonly called the client
library, but its role in the named-pipe connection is the **server**. The child rendering process is the named-pipe
**client**.

| Component | Pipe type | Responsibility |
| --- | --- | --- |
| `PdfRenderOrchestrator` | `NamedPipeServerStream` | Creates and owns the pipe, starts the worker, accepts its connection, sends requests, receives results, and disposes or replaces the connection. |
| `PdfiumRaster.Orchestrator.Worker` | `NamedPipeClientStream` | Connects to the local pipe created for it, receives requests, invokes PdfiumRaster, returns results, and exits when the pipe closes or a shutdown message arrives. |

Both ends open the pipe with `PipeDirection.InOut` and combine the `PipeOptions.Asynchronous` and
`PipeOptions.CurrentUserOnly` flags. The same connection therefore carries data in both directions, pipe I/O can be
cancelled or awaited without blocking a dedicated thread, and the runtime rejects a pipe endpoint owned by a
different operating-system user. The server uses byte transmission mode and permits one server instance for that pipe
name. A pipe belongs to one worker only; connections are not shared between workers and are not opened per render
request.

## Connection startup and handshake

The orchestrator establishes each worker connection in this order:

1. It generates a short, unique pipe name, a cryptographically random 32-byte token, and a worker-specific temporary
   directory.
2. It creates the `NamedPipeServerStream` before starting the child process, preventing a startup race in which the
   worker tries to connect before a server exists.
3. It starts the worker executable and passes the pipe name as its only command-line argument. The token and temporary
   directory are passed in environment variables.
4. The worker creates a `NamedPipeClientStream` for server `"."`, meaning the local machine, and connects to that pipe
   name.
5. The worker sends a `Hello` frame containing its protocol version and inherited token.
6. The orchestrator verifies the version and compares the token before accepting the worker. The token binds the
   connection to the child that was just launched; it is not a replacement for an operating-system security boundary.
7. The orchestrator replies with `Ready`. Only then does the worker initialize PdfiumRaster/PDFium and enter its
   request loop.

Connection and handshake must complete within `WorkerStartupTimeout`, which defaults to 15 seconds. A premature worker
exit, timeout, wrong first message, incompatible protocol version, or token mismatch fails startup with
`PdfWorkerStartupException`, and the incomplete process and pipe are cleaned up.

`PdfRenderOrchestrator.CreateAsync` starts every slot concurrently and allows the caller to cancel process connection
and handshake. The synchronous constructor preserves its original ready-on-return contract by waiting for the same
startup path. Hosting creates a deferred singleton and awaits that path from `IHostedService.StartAsync`, so readiness
is degraded and request admission remains closed until every worker is ready.

## Private framed protocol

The named pipe is a byte stream, so application messages need their own boundaries. Every frame contains:

```text
4-byte little-endian frame length | 1-byte message kind | payload bytes
```

The frame length includes the message-kind byte. The current implementation limits control payloads to 1 MiB and
input/output chunks to 64 KiB. Reads continue until an entire frame has arrived; an early end-of-stream is treated as
a disconnected or crashed worker. Unknown message kinds, invalid lengths, unexpected message ordering, and invalid
bitmap metadata are rejected as protocol errors.

Each persistent pipe endpoint owns one reusable five-byte header and one 64 KiB transfer buffer. Byte arrays, stream
buffers, rendered pixels, and encoder buffers are written as array segments without per-chunk copies. Bitmap payloads
are read directly into their final caller-owned pixel array; spool-file and caller-stream forwarding use the reusable
transfer buffer. Pipe writes are flushed at handshake and complete logical request/response boundaries rather than
after every frame. These are implementation details and do not change the version-three frame layout.

The logical messages are:

| Direction | Message | Purpose |
| --- | --- | --- |
| Worker to orchestrator | `Hello` | Supplies the protocol version and per-process startup token. |
| Orchestrator to worker | `Ready` | Confirms that the handshake succeeded and requests may begin. |
| Orchestrator to worker | `Request` | Supplies the operation kind, source and output kinds, zero or more zero-based page indexes, paths where applicable, password, rendering/encoding options, and optional byte limits. |
| Orchestrator to worker | `InputChunk`, `InputEnd` | Streams an in-memory or caller-stream PDF to the worker and marks the end of that input. |
| Worker to orchestrator | `BitmapHeader`, `OutputChunk` | Returns validated bitmap metadata followed by pixels, or streams encoded image bytes to a caller output stream. |
| Worker to orchestrator | `PageCount`, `PageSize` | Returns an inspected page count and, for a page-size request, one validated width/height pair in PDF points per page. |
| Worker to orchestrator | `Complete` | Marks successful completion of the current request. |
| Worker to orchestrator | `Error` | Returns the remote exception type and message for a request that failed without breaking the worker connection. |
| Worker to orchestrator | `ResourceLimit` | Returns the resource name, configured limit, and observed byte count for an enforced limit. |
| Orchestrator to worker | `Shutdown` | Requests a clean worker exit after accepted work has been handled. |

These names, layouts, sizes, and sequences describe the current internal implementation. They are deliberately not
public API or an integration contract. Consumers must use `PdfRenderOrchestrator`; they must not connect to worker
pipes or depend on protocol details. Protocol compatibility is maintained between the packaged library and worker.

## Request and data flow

After queue admission, a request is assigned to an available worker. That worker's persistent pipe is used exclusively
for the request until it returns `Complete`, `Error`, or `ResourceLimit`.

Single-page and multi-page requests share the protocol. A multi-page bitmap response repeats
`BitmapHeader`/`OutputChunk` for every requested page and ends with one `Complete`; request order is result order. The
worker opens one `PdfRenderSession` for the request, so a batch transfers and parses the document once and reuses the
session render buffer. Pages inside a batch are sequential. Independent batches remain independent queue items and can
run on separate workers.

Document-inspection requests use the same admission, transfer, timeout, and failure path. `GetPageCountAsync` returns
one `PageCount` frame. `GetPageSizesAsync` returns `PageCount`, then one `PageSize` frame per zero-based page, then
`Complete`; individual size frames keep control payloads bounded for large documents. The orchestrator rejects
negative counts, missing or excess size frames, non-positive or non-finite dimensions, and unexpected render output.
The private protocol is version 3 because operation metadata and these response sequences changed its wire layout.
`InspectDocumentAsync` uses the same page-size sequence and projects its count and ordered sizes into one immutable
public result, so it does not add a protocol operation or version change.

For `RenderPagesAsync`, the orchestrator collects every completed bitmap and completes the task with the full list.
For `RenderPagesStreamAsync`, it publishes completed `PdfPageBitmap` values to a capacity-one channel. The pipe reader
waits when that channel is full, propagating consumer backpressure across the application/worker boundary without a
protocol change. Memory can include the bitmap currently being assembled, one completed buffered bitmap, and any
caller-retained results; it does not inherently grow with the total requested page count.

Input behavior depends on the source type:

| PDF input | Pipe traffic | Worker behavior |
| --- | --- | --- |
| Path | Only the full path and request metadata cross the pipe. | The worker opens the PDF directly using its inherited filesystem permissions. This is preferred for large PDFs. |
| `byte[]` | PDF content crosses as bounded `InputChunk` frames followed by `InputEnd`. | The worker spools the content to a worker-owned temporary PDF before rendering. |
| `Stream` | Content from the stream's current position crosses as bounded `InputChunk` frames followed by `InputEnd`. | The worker spools the content to a temporary PDF, providing the random access required by PDFium. |

The spool directory is worker-specific. On Unix it is explicitly set to owner read/write/execute permissions. A
temporary input file is deleted after its request, and the directory is removed when the worker connection is
disposed. Spooling avoids retaining a second complete managed copy inside the worker, but queued `byte[]` inputs still
remain in application memory until processed.

Output behavior depends on the requested target:

| Output | Pipe traffic and memory behavior |
| --- | --- |
| `PdfBitmap` | The worker sends a bitmap header and chunked pixel bytes. The orchestrator validates width, height, stride, and total byte count, allocates the final managed pixel array, reads chunks directly into it, and returns a caller-owned `PdfBitmap`. A streaming batch wraps each result with its request position and page index and uses a capacity-one handoff. |
| Image path | The output path crosses in the request. The worker encodes to a uniquely named temporary file beside the destination, enforces the aggregate limit, and atomically replaces the destination; encoded bytes do not return through the pipe. A batch repeats this per mapped page. |
| Output `Stream` | The worker encodes into a pipe-backed stream. Chunked encoded bytes return through the pipe and the orchestrator writes them to the caller-owned stream. The orchestrator never closes that output stream. |
| Page count/sizes | Only bounded metadata frames return. Sizes are validated and accumulated in zero-based page order; no bitmap or encoded-output bytes are produced. |

## Scheduling and concurrency

The orchestrator uses a bounded channel for admission control. Each worker loop takes one queued request, executes it
over that worker's pipe, and only then takes another. PDFium work is therefore serialized within a worker process,
while up to `WorkerCount` requests can perform native work concurrently across independent processes.

`QueueCapacity` bounds the number of waiting requests, not their total bytes. A batch counts as one request and holds
one worker for its full duration. `PdfRenderQueueFullMode.Wait` applies
asynchronous backpressure, while `PdfRenderQueueFullMode.Reject` rejects excess work. Failed requests are never
automatically retried because a retry could repeat expensive work or duplicate an external write.

A streaming batch continues to occupy its worker while its consumer is paused. Ending enumeration or canceling its
token interrupts pipe reading, kills the worker, and waits for replacement before the enumerator finishes. Discarding
the remaining private-protocol frames is necessary because a persistent pipe cannot be realigned reliably after a
partially consumed response.

## Timeouts, failures, and replacement

The optional request timeout begins when a request is dispatched to a worker; queue waiting time is excluded. It
covers request transfer, input streaming, native rendering, encoding, and output transfer. At the deadline, the
orchestrator cancels its pipe and caller-stream operations, faults the request with `PdfWorkerTimeoutException`, kills
the worker to interrupt native code, and starts a replacement when more work can be accepted or remains queued.

Arbitrary caller-provided `Stream` implementations may ignore cancellation. A timed-out request task can therefore
finish before its caller-side I/O unwinds. Shutdown tracks and waits for that cleanup instead of disposing a stream
while it is still in use.

Failure handling distinguishes these cases:

- A normal rendering or encoding error is returned in an `Error` frame as `PdfWorkerRemoteException`. The healthy
  worker remains available.
- A worker process exit, broken pipe, or premature end-of-stream becomes `PdfWorkerCrashedException` for the active
  request.
- Malformed or unexpected communication becomes `PdfWorkerProtocolException`.
- A configured input, bitmap, or aggregate output limit becomes `PdfRenderResourceLimitException`. A worker is
  replaced when needed because a limit can be detected while a streamed protocol sequence is still in flight.
- A crash, hard timeout, or protocol/transport fault terminates and replaces that worker when the orchestrator still
  needs workers. Other workers and their requests are unaffected unless worker replacement reaches a terminal failure.
- Replacement attempts use the snapshotted `WorkerRestartDelays` sequence. If every attempt fails, the orchestrator
  enters a terminal faulted state and stops accepting work.

`CompleteAsync()` stops admission, drains accepted work, sends `Shutdown`, and waits for worker exits. An accepted
streaming batch must therefore be consumed or explicitly ended; its bounded result channel deliberately prevents an
unattended consumer from allowing unbounded buffering. `CancelAsync()` and `Dispose()` cancel queued and streaming
work, wait for active uninterruptible cleanup, and stop the workers. If a worker does not exit after a graceful
shutdown request, the orchestrator kills it.

## Trust and security boundary

The transport is intended only for the local worker processes launched by the orchestrator. Both pipe endpoints
require the current operating-system user, pipe names are unique per worker, the worker connects to the local machine,
and the startup token prevents a connection from being accepted as the expected child without knowing that token. The
pipe protocol is not exposed publicly. The user restriction and token are complementary: the former rejects a
different account, while the latter identifies the specific child launched for a worker slot.

This design is **not** a security sandbox and the pipe is not an encrypted application protocol. Workers inherit the
application's operating-system identity, environment context, working directory, and filesystem permissions. A worker
can access any path the application identity can access. Inputs, passwords, bitmap pixels, and encoded output passing
through the pipe should therefore be treated as local in-process-equivalent data, not as data sent to an untrusted
renderer or remote service.

## Worker packaging and discovery

At pack time, the repository publishes one self-contained single-file worker for each supported runtime identifier.
The backward-compatible `PdfiumRaster.Orchestrator` package places all of them under `tools/<rid>/`. Each
`PdfiumRaster.Orchestrator.<rid>` slim package places only its matching worker there. Both package forms contain the
same client assembly and build-transitive target; consumers choose exactly one. The target resolves the worker using
the consuming project's `RuntimeIdentifier`, falling back to the SDK host RID, and copies it beside build and publish
outputs.

## Operational diagnostics

Observability is emitted through four complementary standard .NET mechanisms. An optional `ILoggerFactory` produces
structured orchestrator, request, and worker lifecycle logs. `ActivitySource` creates one internal activity per request
using the caller's parent context and covering both queue and execution time. `Meter` reports bounded-cardinality
request, duration, queue, worker, restart, and rejection measurements suitable for continuous collection. The existing
internal `EventSource` named `PdfiumRaster-Orchestrator` remains available for on-demand `dotnet-trace` diagnostics.

Request telemetry includes process-local correlation IDs and timing where the signal permits it but excludes paths,
passwords, pipe names, tokens, standard error, and document bytes. Worker logs and events include the worker index and
process ID; failures include only the managed exception type. Request and process IDs are deliberately excluded from
metric tags. This keeps the transport private while allowing queue delay, execution time, crashes, timeouts, worker
availability, and replacement activity to be observed with standard .NET and OpenTelemetry tooling.

The optional .NET health-check integration reads the same in-memory admission, startup, terminal-failure, and
worker-connection state. It reports degraded during initial hosted startup or while any worker slot is disconnected
during replacement, and unhealthy after admission closes or a terminal failure occurs. The check does not add
protocol messages, render a probe document, access input paths, or create a separate worker process.
