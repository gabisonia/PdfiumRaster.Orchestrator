# Contributing

Contributions are welcome. Use GitHub issues for reproducible defects and focused feature proposals before starting a
large or compatibility-sensitive change. Security vulnerabilities and sensitive PDFs must follow
[SECURITY.md](SECURITY.md) instead of a public issue.

## Development workflow

Install the .NET 10 SDK, clone the repository, create a focused branch, and use the root Makefile:

```bash
make restore
make build
make test
make coverage
```

Submit changes through a pull request. Keep each pull request focused, explain the behavior and motivation, and note
any compatibility, memory, stream-ownership, or packaging consequences. CI must pass before merge. Non-trivial
changes should receive human review when another maintainer is available; authors should address review findings or
record why a finding does not apply. Release builds should remain warning-free. Resolve CodeQL findings before merge
or document why a finding is not exploitable or does not apply.

## Coding and compatibility standards

Keep the client library compatible with `netstandard2.1`. Keep worker/protocol changes backward compatible and add
focused tests for queue behavior, process failures, timeouts, cancellation, and protocol validation. Public APIs need
nullable validation and XML documentation. Public surface changes must update `PublicAPI.Unshipped.txt`; never remove
an entry from `PublicAPI.Shipped.txt` without an explicitly approved breaking release.

Follow the existing C# style, keep nullable reference types enabled, validate public arguments, and keep PDF rendering
inside the PdfiumRaster dependency. Page indexes are zero-based. Public stream APIs must state ownership and memory
behavior. Prefer bounded data structures and cancellation-aware asynchronous I/O at process and pipe boundaries.

## Tests and documentation

`make coverage` requires at least 90% line coverage and 80% branch coverage for the application-facing library. New
public API must include success, validation, cancellation, ownership, failure, and lifecycle coverage as applicable.
See the [testing and coverage guide](docs/TESTING.md) for the complete behavioral matrix and platform strategy.

For a visual end-to-end check with a local PDF:

```bash
make test-manual PDF=/absolute/path/to/input.pdf
```

Generated images are written under `tests/PdfiumRaster.Orchestrator.Tests/ManualOutput/` and are ignored by Git.
Update `README.md` for user-visible behavior, `docs/API.md` for public API changes, and `docs/ARCHITECTURE.md` for pipe
protocol or worker-lifecycle changes. Before submitting release-related changes, run `make release-check`.
