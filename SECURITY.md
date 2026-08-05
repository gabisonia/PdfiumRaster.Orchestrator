# Security Policy

## Supported versions

Security fixes are released in the latest available PdfiumRaster.Orchestrator version. Older versions are not
guaranteed to receive backports. The package also executes native PDFium through its PdfiumRaster dependency, so core
and native dependency updates can be security-relevant even when the orchestrator API is unchanged.

## Reporting a vulnerability

Do not disclose a suspected vulnerability or a proof-of-concept document in a public issue.

If it is available for the repository, use
[GitHub's private vulnerability reporting](https://github.com/gabisonia/PdfiumRaster.Orchestrator/security/advisories/new).
Otherwise,
open a public issue asking the maintainer for a private contact channel, without including exploit details or a
sensitive PDF. Include the affected version, operating system and architecture, impact, reproduction conditions, and
whether the report may be disclosed after a fix is available.

There is no guaranteed response or remediation SLA. The maintainer aims to acknowledge vulnerability reports within
7 days, provide an initial assessment within 14 days, and coordinate disclosure with the reporter. When a fix is
needed, the target is to release it and disclose the vulnerability within 90 days; complex or upstream issues may
require a different timeline. Reports will be assessed based on reproducibility, impact, and whether the issue is in
the orchestrator, PdfiumRaster, native dependencies, or the consuming application.

## Isolation boundary

Workers run under the same operating-system identity and filesystem permissions as the application. Named-pipe
endpoints are restricted to that operating-system user and use an unpredictable per-worker connection token. These
controls prevent a different user or an unrelated connection without the token from completing the handshake, but
workers remain trusted local child processes—not a security sandbox. A worker can read any path and write any output
path accessible to the application.

Process isolation contains ordinary worker crashes and enables termination at a request deadline. It does not enforce
memory, CPU, filesystem, network, or page-complexity limits. Applications processing untrusted PDFs should keep this
package and PdfiumRaster current, constrain accepted document and render dimensions, use path allowlists where
appropriate, and apply operating-system or container resource and sandbox policies outside this library.

Byte-array and stream inputs are spooled inside randomly named, owner-only temporary directories and removed after the
request or worker cleanup. Applications should still treat the system temporary volume as sensitive storage and use
encrypted storage when their threat model requires it.
