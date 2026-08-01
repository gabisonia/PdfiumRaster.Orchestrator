# Architecture

PdfiumRaster.Orchestrator is a process-isolation and scheduling layer over
[`PdfiumRaster`](https://github.com/gabisonia/PdfiumRaster). The client library targets `netstandard2.1`; packaged
self-contained .NET 10 workers perform rendering through their PdfiumRaster dependency.

```text
application
    |
    | bounded request queue
    v
PdfRenderOrchestrator
    |-- private named pipe --> worker 1 --> PdfiumRaster --> PDFium
    |-- private named pipe --> worker 2 --> PdfiumRaster --> PDFium
    `-- private named pipe --> worker N --> PdfiumRaster --> PDFium
```

Each worker owns one independent native PDFium runtime. PDFium calls remain serialized inside each process, while
separate workers can render concurrently. Workers inherit the application's operating-system identity and filesystem
permissions; process isolation is not a security sandbox.

PDFium exposes process-global initialization and destruction, and its public API requires calls to be single-threaded
or externally serialized. PdfiumRaster therefore reference-counts one initialized runtime per process and protects
native calls with a process-wide lock. Multiple managed initialization handles share that runtime and do not create
parallel PDFium instances. Separate operating-system processes are the concurrency boundary: every worker has distinct
native globals and its own serialization lock.

The orchestrator starts a fixed worker count and performs a versioned handshake over a private local named pipe. The
protocol uses bounded, length-prefixed messages and validates operation kinds, payload sizes, page indexes, render
options, and worker responses. It is an implementation detail, not a public integration surface.

Path inputs let workers open PDFs directly. Byte-array and stream inputs cross the pipe and are spooled to files inside
an owner-only worker temporary directory to provide random access without retaining another full managed copy.
Worker-owned temporary files are removed after the request. Outputs can return bitmap bytes through the pipe or be
encoded directly to a path or stream.

The client uses a bounded channel for admission control. A request is assigned to one worker, and each worker handles
one request at a time. A crash, timeout, or protocol fault terminates and replaces that worker. Requests are not
automatically retried. Graceful completion drains accepted work; cancellation stops queued work and waits for active
native operations before final process cleanup.

The optional request timeout starts after queue admission and covers client-to-worker transfer, native rendering and
encoding, and worker-to-client transfer. The client cancels pipe and caller-stream operations and kills the worker at
the deadline. Because arbitrary `Stream` implementations may ignore cancellation, a timed-out request task can finish
before its caller-side I/O unwinds; shutdown tracks and waits for that cleanup instead of disposing a stream while it
is still in use.

At pack time, the repository publishes one self-contained single-file worker for each supported runtime identifier.
The NuGet package places them under `tools/<rid>/`. A build-transitive target resolves the matching executable for the
consumer's runtime and makes its path available to the client library.
