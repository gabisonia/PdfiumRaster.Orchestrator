# Changelog

All notable changes to PdfiumRaster.Orchestrator are documented here. The project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## 1.1.0 - 2026-08-07

### Changed

- Reuse per-worker pipe framing and transfer buffers instead of allocating headers and 64 KiB payload arrays for
  every input and output chunk.
- Write byte-array, stream, bitmap, and encoded output segments directly and assemble returned bitmaps directly in
  their final caller-owned pixel arrays.
- Flush named pipes at handshake and logical request/response boundaries instead of after every frame while preserving
  the private version-three wire format and existing timeout, cancellation, and failure behavior.
- Preserve invalid-handshake protocol diagnostics when a short-lived worker exits immediately after connecting,
  avoiding a macOS startup race that could misclassify the failure as a pre-connection exit.

### Documentation

- Add repeatable 16 MiB transfer benchmarks and a stable-release comparison gate against `1.0.0`, requiring at least
  50% lower framing allocations and no more than a 5% median end-to-end latency regression.
- Add machine-readable OpenSSF Best Practices evidence and expand the documented contribution and review process.

## 1.0.0 - 2026-08-04

### Added

- Add `InspectDocumentAsync` for path, byte-array, and stream inputs, returning page count and ordered page sizes from
  one worker-isolated inspection request.
- Add `GetStatus` with a public lifecycle state and point-in-time worker-availability snapshot for applications that
  do not use the .NET health-check integration.

### Changed

- Fail builds and publishes with an actionable error when an installed RID-specific package does not match the target
  runtime identifier.
- Prepare the repository for public contributions with support guidance, issue and pull-request templates, dependency
  update automation, restricted CI permissions, and removal of local or unverified test artifacts.

### Documentation

- Document unified inspection, standalone status monitoring, slim-package validation, and the public support process.

## 0.9.0 - 2026-08-04

### Added

- Add worker-isolated `GetPageCountAsync` and `GetPageSizesAsync` APIs for path, byte-array, and stream inputs, with
  password, cancellation, input-limit, timeout, ownership, logging, tracing, and metrics support.

### Changed

- Advance the private worker protocol to version 3 with explicit operation metadata and bounded page-count/page-size
  response frames.
- Use worker-isolated page-count inspection in the parallel export sample.

### Documentation

- Document inspection units, overloads, ownership, limits, scheduling, telemetry operation names, protocol sequences,
  and testing coverage.

## 0.8.0 - 2026-08-02

### Added

- Add cancellable asynchronous worker startup through `PdfRenderOrchestrator.CreateAsync`; .NET hosting now performs
  the same startup asynchronously as part of host startup.
- Add `RenderPagesStreamAsync` for path, byte-array, and stream inputs, yielding ordered `PdfPageBitmap` results with
  capacity-one consumer backpressure instead of retaining every rendered bitmap until the batch completes.

### Changed

- Report readiness as degraded while hosted workers are still starting.
- Abort an unfinished streaming request and replace its worker when enumeration ends early, preserving private
  protocol alignment for subsequent requests.

### Documentation

- Document asynchronous creation, streaming memory and ownership behavior, cancellation, early-exit replacement, and
  hosted startup readiness semantics.

## 0.7.0 - 2026-08-02

### Added

- Add `AddPdfiumRasterOrchestrator` dependency-injection and hosted-lifecycle registration with automatic host logging,
  graceful draining, and shutdown-deadline cancellation.
- Add a standard .NET health check that reports healthy, degraded during worker replacement, or unhealthy after a
  terminal failure or shutdown without rendering a probe document.
- Add asynchronous disposal through `IAsyncDisposable` and `DisposeAsync()`.

### Documentation

- Update the ASP.NET Core sample and hosting guidance to use the first-class registration and expose a readiness
  endpoint.

## 0.6.0 - 2026-08-02

### Added

- Add optional structured `ILoggerFactory` logging, correlated request activities, and operational metrics for queue,
  request, and worker health while retaining the existing EventSource diagnostics.

### Documentation

- Emphasize choosing exactly one all-runtime or platform-specific package and document every orchestrator option
  default and the standard .NET/OpenTelemetry observability setup.
- Add an ASP.NET Core sample that demonstrates host logging, correlated OpenTelemetry tracing, operational metrics,
  and graceful orchestrator shutdown.

## 0.5.0 - 2026-08-02

### Added

- Add open-once `RenderPagesAsync` and `SavePagesAsync` batches for path, byte-array, and stream PDF inputs.
- Add configurable input, per-bitmap, aggregate-output, batch-page, and worker temporary-directory limits, with
  structured `PdfRenderResourceLimitException` failures.
- Add slim `PdfiumRaster.Orchestrator.<rid>` packages containing one worker while retaining the all-runtime package.

### Changed

- Stage path output beside its destination and atomically replace the destination only after encoding and output-limit
  validation succeeds.
- Advance the private worker protocol to version 2 for batch metadata, resource limits, repeated bitmap results, and
  structured resource-limit responses.

## 0.4.0 - 2026-08-01

### Fixed

- Reject `RequestTimeout` values above the maximum portable framework timer interval during option assignment instead
  of allowing the request to fail later when its deadline timer is created.

### Documentation

- Document the complete `PdfiumRaster-Orchestrator` diagnostic event schema, including stable IDs, levels, payload
  names, types, units, correlation semantics, and data-exclusion guarantees.
- Record the public API shipped in 0.3.0 and expand the release checklist for future versions.

## 0.3.0 - 2026-08-01

### Added

- Configurable worker connection/handshake timeout and worker replacement delay sequence.
- Operational `EventSource` diagnostics for orchestrator, request, and worker lifecycle activity.
- Public API compatibility baselines enforced during builds.
- Fake-worker integration coverage for handshake, crash, timeout, malformed protocol, replacement, and terminal-fault
  behavior.
- Enforced line and branch coverage gates in local, CI, and publishing workflows.

### Changed

- Restricted both local named-pipe endpoints to the current operating-system user.
- Added a protocol-version and per-worker token handshake before accepting a worker connection.
- Expanded API, architecture, security, testing, troubleshooting, and release documentation.
