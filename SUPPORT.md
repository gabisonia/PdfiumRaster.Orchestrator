# Support

## Getting help

Use GitHub issues for reproducible defects and focused feature proposals. Include the package version, operating
system, process architecture, deployment runtime identifier, and a minimal reproduction when possible. Use GitHub
Discussions for usage questions if Discussions are enabled for the repository.

Do not attach confidential PDFs, passwords, filesystem paths, pipe tokens, or complete worker standard-error output.
Follow [SECURITY.md](SECURITY.md) for suspected vulnerabilities.

## Supported versions

Fixes are released for the latest available PdfiumRaster.Orchestrator version. Older versions are not guaranteed to
receive bug or security backports. Supported worker platforms and package choices are listed in
[the API guide](docs/API.md#supported-worker-platforms).

Workers are trusted local child processes, not a security sandbox. Resource isolation and policies for untrusted PDFs
remain the responsibility of the consuming application and its operating environment.
