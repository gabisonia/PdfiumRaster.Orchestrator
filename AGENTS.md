# AGENTS.md

Guidance for agents working in this repository.

## Project Scope

PdfiumRaster.Orchestrator is a multi-process orchestration layer for PdfiumRaster. Keep PDF rendering in the
PdfiumRaster dependency and keep this repository focused on worker lifecycle, bounded scheduling, local named-pipe
communication, crash isolation, hard timeouts, and packaging the worker executables.

## Repository Layout

- Client library code lives in `src/PdfiumRaster.Orchestrator` and targets `netstandard2.1`.
- Worker code lives in `src/PdfiumRaster.Orchestrator.Worker` and targets `net10.0`.
- Tests live in `tests/PdfiumRaster.Orchestrator.Tests`.
- Documentation lives in `README.md`, `docs/`, and `samples/`.
- Use `Directory.Packages.props` for every NuGet version.
- Keep generated outputs out of Git.

## Coding and Protocol Rules

- Keep nullable reference types enabled and validate public arguments.
- Add useful XML documentation to every public type and member.
- Treat page indexes as zero-based and state stream ownership and memory behavior.
- Preserve public API and worker protocol compatibility once released.
- Never expose the private pipe protocol as public API.
- Workers are trusted local child processes, not a security sandbox.
- Path inputs are preferred for large PDFs. Byte arrays and streams cross the pipe and are spooled by the worker.
- Failed requests are not automatically retried.

## Tests and Packaging

- Run `make test` for automated coverage.
- Use `make test-manual PDF=/path/to/file.pdf` only for local visual inspection; it is excluded by default.
- Run `make pack` before considering release-related work complete.
- The package must include XML documentation, the root README, the build-transitive worker discovery target, and all
  supported self-contained worker executables.
- If MSBuild fails in a sandbox because named pipes or sockets are restricted, rerun with the required approval.

## Documentation

- Update `README.md` for user-facing behavior.
- Update `docs/API.md` for public API changes.
- Update `docs/ARCHITECTURE.md` for protocol or worker-lifecycle changes.
- Update `docs/RELEASING.md` for release changes.
