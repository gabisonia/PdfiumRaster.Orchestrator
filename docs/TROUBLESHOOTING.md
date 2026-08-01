# Troubleshooting

This guide covers worker startup, local named-pipe communication, crashes, timeouts, filesystem behavior, and
diagnostic collection. Workers are trusted local child processes running with the application's operating-system
identity; they are not remote services or a security sandbox.

## Quick triage

| Symptom | First checks |
| --- | --- |
| `PdfWorkerStartupException` | Confirm that the packaged worker was copied beside the application, the runtime identifier is supported, and the process identity can execute the worker and write to the system temporary directory. |
| `PdfWorkerCrashedException` | Inspect `ExitCode` and the bounded `StandardError` tail, then check native dependency loading, memory pressure, and operating-system termination logs. |
| `PdfWorkerTimeoutException` | Compare `RequestTimeout` with PDF size, rendering dimensions, encoding cost, and stream throughput. The failed request is not retried. |
| `PdfWorkerProtocolException` | Ensure the client library and packaged worker came from the same compatible package; remove stale copied workers and rebuild the application output. |
| `PdfWorkerRemoteException` | Inspect `RemoteExceptionType` and the exception message. The worker remained connected and reported a rendering, validation, or encoding failure. |
| `PdfRenderResourceLimitException` | Inspect `Resource`, `Limit`, and `Observed`; reduce input size/render dimensions/batch size or deliberately raise the corresponding byte limit. |
| `SocketException` or `UnauthorizedAccessException` mentioning pipes | Check sandbox policy, service identity, temporary-directory access, and whether local named pipes or their platform backing primitives are prohibited. |

## Worker discovery and execution

The NuGet package's build-transitive target selects the worker for the consuming application's runtime identifier and
copies it to the build and publish output. A startup error saying that the bundled worker was not found usually means
that build assets were disabled, publish output was copied incompletely, or the application is running from a
different base directory.

Check that:

- the application uses a supported runtime identifier listed in the [API guide](API.md#supported-worker-platforms);
- `PdfiumRaster.Orchestrator.Worker.exe` exists beside the application on Windows, or
  `PdfiumRaster.Orchestrator.Worker` exists there on Linux/macOS;
- deployment tooling copied the complete publish output rather than only managed assemblies;
- antivirus or endpoint protection did not quarantine the worker or its native PDFium dependencies;
- the application identity can execute files from the deployment location;
- Linux/macOS mounts containing the application are not configured with `noexec`.

Do not copy a worker from a different package version over the packaged worker. The startup handshake checks protocol
compatibility, but using the matching library and worker is the supported configuration.

## Startup timeout and replacement policy

Worker connection and handshake default to 15 seconds. Slow process creation, cold storage, aggressive antivirus
scanning, or overloaded hosts can require a larger value:

```csharp
var options = new PdfRenderOrchestratorOptions
{
    WorkerStartupTimeout = TimeSpan.FromSeconds(30),
    WorkerRestartDelays = new[]
    {
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(8),
    },
};
```

`WorkerRestartDelays` supplies one delay per replacement attempt. A zero delay is valid. The orchestrator snapshots
both settings during construction. If every replacement attempt fails, the orchestrator enters a terminal faulted
state and stops accepting submissions.

`RequestTimeout`, `WorkerStartupTimeout`, and each restart delay use portable framework timers. Non-null timeout values
cannot exceed approximately 49.7 days. Values beyond that limit are rejected during option assignment instead of
failing later when a request or worker operation creates its timer.

Increasing the startup timeout does not fix a missing executable, unsupported platform, denied pipe creation, or
worker crash. Diagnose those conditions instead of masking them with a long timeout.

## Named-pipe and sandbox restrictions

Each orchestrator worker uses one local, bidirectional named pipe. Both endpoints specify
`PipeOptions.CurrentUserOnly`, so the application and worker must run as the same operating-system user. The
orchestrator launches workers with its own identity; identity mismatches normally arise only from unusual process
launch interception or impersonation.

Some test runners, application sandboxes, and hardened containers prohibit named pipes, local sockets, or child
processes. Symptoms can occur in MSBuild itself before project tests run. Permit local named-pipe/socket creation and
child processes for the build and application, or run outside that sandbox. Do not weaken the pipe user restriction
to work around an environment that launches the worker under a different account.

In containers, also confirm that the application can create and remove entries in the system temporary directory and
that the worker executable matches the container architecture and libc variant.

## Temporary files and permissions

Path inputs are opened directly by the worker. `byte[]` and `Stream` inputs cross the pipe and are spooled to a
worker-owned temporary PDF to give PDFium random access. On Unix, the worker directory is explicitly owner-only. The
request file is removed after processing and the directory is removed when the worker connection is disposed.

If spooling fails, check temporary-volume capacity, inode availability, filesystem permissions, quotas, and cleanup
policies. A process crash can briefly leave files until orchestrator cleanup runs. Applications with stronger
confidentiality requirements should place the system temporary directory on encrypted storage.

Set `PdfRenderOrchestratorOptions.TemporaryDirectory` to move worker spool directories to a controlled volume. The
orchestrator creates the parent if necessary; each child directory is still owner-only on Unix and is deleted when its
worker connection is disposed. This controls placement, not capacity. Use `MaximumInputBytes` and filesystem quotas or
container limits to bound consumption.

Path outputs are encoded to a temporary file in the destination directory and atomically moved into place only after
encoding and `MaximumOutputBytes` validation succeed. Ensure that destination volumes have room for the staged file.
Earlier files from a multi-page save batch remain committed if a later page fails.
A process kill or operating-system crash can leave a hidden file matching `.<name>.<id>.tmp<extension>` beside the
destination; it is never treated as a completed output and can be removed after confirming no worker is active.

## Crashes, protocol failures, and timeouts

A worker crash or broken pipe faults only its active request with `PdfWorkerCrashedException`. A malformed frame,
unexpected message, invalid bitmap header, or excess bitmap data faults the request with
`PdfWorkerProtocolException`. A configured request deadline kills the worker and returns
`PdfWorkerTimeoutException`. These conditions trigger worker replacement while the orchestrator still needs workers.

The failed request is never retried automatically. A path or stream target may already contain partial output, so the
caller must decide whether cleanup and retry are safe. Use atomic application-level output patterns when partial files
are unacceptable.

`PdfWorkerRemoteException` is different: the worker caught an ordinary rendering or encoding exception and returned
its managed type and message. That worker remains healthy and is reused.

## Diagnostic events

The library emits an internal `EventSource` provider named `PdfiumRaster-Orchestrator`. It reports:

- orchestrator start, stop, and terminal faults;
- request submission, worker assignment, completion, cancellation, and failure;
- submission-to-execution and execution durations;
- worker process IDs, exits, replacement attempts, and replacement delays;
- exception type names for failures.

The provider has no custom keywords. Enable it at `Verbose` to receive every event, including request submission.
Event IDs and payloads are:

| ID | Event | Level | Payload | Meaning |
| ---: | --- | --- | --- | --- |
| 1 | `OrchestratorStarted` | Informational | `workerCount` (`Int32`), `queueCapacity` (`Int32`) | A new orchestrator started its fixed worker set. |
| 2 | `RequestSubmitted` | Verbose | `requestId` (`Int64`), `operationKind` (`Int32`) | A request entered submission; operation `1` renders one bitmap, `2` saves one image, `3` renders a bitmap batch, and `4` saves a file batch. |
| 3 | `RequestStarted` | Informational | `requestId` (`Int64`), `workerIndex` (`Int32`), `submissionDelayMilliseconds` (`Double`) | A zero-based worker slot received the request. Delay is elapsed milliseconds since submission. |
| 4 | `RequestCompleted` | Informational | `requestId` (`Int64`), `workerIndex` (`Int32`), `executionMilliseconds` (`Double`) | The request completed successfully. Duration starts at worker assignment. |
| 5 | `RequestFailed` | Warning | `requestId` (`Int64`), `workerIndex` (`Int32`), `exceptionType` (`String`), `executionMilliseconds` (`Double`) | The request failed. The type is the fully qualified managed exception name when available. |
| 6 | `RequestCanceled` | Informational | `requestId` (`Int64`), `workerIndex` (`Int32`), `executionMilliseconds` (`Double`) | The request observed caller or orchestrator cancellation. |
| 7 | `WorkerStarted` | Informational | `workerIndex` (`Int32`), `processId` (`Int32`) | A worker connected, passed the handshake, and became available. |
| 8 | `WorkerRestarting` | Warning | `workerIndex` (`Int32`), `attempt` (`Int32`), `delayMilliseconds` (`Int64`), `reasonType` (`String`) | A one-based replacement attempt is waiting for its configured delay. |
| 9 | `WorkerStopped` | Informational | `workerIndex` (`Int32`), `processId` (`Int32`) | A worker connection and process were released. |
| 10 | `WorkerStartFailed` | Error | `workerIndex` (`Int32`), `exceptionType` (`String`) | Initial worker startup or a replacement attempt failed. |
| 11 | `OrchestratorFaulted` | Error | `exceptionType` (`String`) | A terminal error stopped admission and faulted the orchestrator. |
| 12 | `OrchestratorStopping` | Informational | `cancel` (`Boolean`) | Shutdown began; `true` selects cancellation and `false` selects graceful draining. |

`requestId` values are unique only within one orchestrator process. Correlate request events by that value and worker
events by the zero-based `workerIndex`; process IDs can change after replacement. Durations use the monotonic runtime
timestamp and are expressed as elapsed milliseconds, not wall-clock timestamps.

Events never contain PDF or image paths, passwords, pipe names, handshake tokens, standard-error text, or document
payloads. Request IDs are process-local correlation values and operation kinds are numeric (`1` for bitmap render and
`2` for save).

Collect the provider from a running process with `dotnet-trace`:

```bash
dotnet-trace collect --process-id <application-pid> --providers PdfiumRaster-Orchestrator
```

Or collect while launching an application:

```bash
dotnet-trace collect --providers PdfiumRaster-Orchestrator -- dotnet MyApplication.dll
```

An in-process diagnostics adapter can instead derive from `EventListener` and enable the provider at the desired
`EventLevel`. Keep failure exception objects in application logs separately when stack traces or remote error messages
are required; the EventSource intentionally records only non-sensitive classifications.

## Information to include in a report

Include the package version, operating system, architecture/runtime identifier, deployment model, exception type and
stack trace, worker exit code, bounded `StandardError`, configured timeouts/restart delays, and relevant diagnostic
events. Do not attach confidential PDFs, passwords, pipe tokens, or full production paths to a public issue.
