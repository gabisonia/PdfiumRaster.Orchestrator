# Testing and coverage

Run the automated suite with:

```bash
make test
```

Run the same suite with coverage collection and enforcement with:

```bash
make coverage
```

The coverage target collects Cobertura output under `artifacts/coverage/` and fails below 90% line coverage or 80%
branch coverage for the application-facing library. Generated reports remain outside Git. The thresholds are a
regression floor rather than a target: new behavior should cover every reachable success, validation, cancellation,
failure, ownership, and lifecycle path it introduces.

## Coverage matrix

| Area | Automated coverage |
| --- | --- |
| Public rendering API | Every path, `byte[]`, and `Stream` input overload returns a bitmap; batch overloads preserve item count and order. |
| Public saving API | Every combination of path, `byte[]`, or `Stream` input with path or `Stream` output is executed, including all multi-file batch inputs. |
| Options and limits | Defaults, valid assignments, invalid ranges, enum validation, copied restart-delay collections, input/bitmap/output byte enforcement, temporary placement, and atomic output preservation. |
| Ownership | Owned and caller-owned input streams across success, validation failure, cancellation, rejection, and timeout; output streams always remain caller-owned. |
| Scheduling | Wait-mode backpressure, reject mode, bounded queueing, queued cancellation, and multi-worker concurrency. |
| Lifetime | Graceful draining, cancellation shutdown, repeated completion, repeated disposal, post-completion rejection, and post-disposal rejection. |
| Hosting and health | Singleton and hosted-service registration, automatic logging, graceful and canceled host shutdown, async disposal, and healthy, degraded, recovered, stopped, and terminal health states. |
| Worker failures | Startup exit and timeout, invalid handshake, crash, bounded standard error, hard request timeout, protocol failure, successful replacement, and exhausted replacement attempts. |
| Exceptions | Public constructors and exposed crash, timeout, remote-error, startup, queue-full, and protocol-error properties. |
| Pipe protocol | Golden version-two vectors, batch and resource-limit fields, fragmented frames, malformed or oversized frames, handshake validation, request/options round trips, and valid and invalid response sequences. |
| Diagnostics | Stable EventSource identity, level, and payload shape; structured log lifecycle events; correlated request activities; operational metric emission; and sensitive-data exclusion across every signal. |
| Packaging | Required NuGet assets, all-runtime and one-worker RID packages, `netstandard2.1` consumption, and both package shapes rendering on Linux, Windows, and macOS. |

The coverage percentage from one machine does not execute operating-system and architecture rejection branches that
cannot occur on that machine. CI therefore runs the behavioral suite and packaged-worker smoke test on Linux, Windows,
and macOS. Defensive process-cleanup catch blocks are validated through their observable outcomes rather than by
injecting operating-system failures into framework types.

Manual visual rendering remains separate:

```bash
make test-manual PDF=/absolute/path/to/input.pdf
```

Manual tests are tagged `Category=Local` and are excluded from automated coverage.
