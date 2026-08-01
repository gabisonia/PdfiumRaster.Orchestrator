# Contributing

Contributions are welcome. Install the .NET 10 SDK, clone the repository, and use the root Makefile:

```bash
make restore
make build
make test
make coverage
```

Keep the client library compatible with `netstandard2.1`. Keep worker/protocol changes backward compatible and add
focused tests for queue behavior, process failures, timeouts, cancellation, and protocol validation. Public APIs need
nullable validation and XML documentation. Public surface changes must update `PublicAPI.Unshipped.txt`; never remove
an entry from `PublicAPI.Shipped.txt` without an explicitly approved breaking release.

`make coverage` requires at least 90% line coverage and 80% branch coverage for the application-facing library. New
public API must include success, validation, cancellation, ownership, failure, and lifecycle coverage as applicable.
See the [testing and coverage guide](docs/TESTING.md) for the complete behavioral matrix and platform strategy.

For a visual end-to-end check with a local PDF:

```bash
make test-manual PDF=/absolute/path/to/input.pdf
```

Generated images are written under `tests/PdfiumRaster.Orchestrator.Tests/ManualOutput/` and are ignored by Git.
Before submitting release-related changes, run `make pack` and `make inspect-package`.
