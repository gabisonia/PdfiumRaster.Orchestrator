# Changelog

All notable changes to PdfiumRaster.Orchestrator are documented here. The project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
