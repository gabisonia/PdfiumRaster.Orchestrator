# Releasing

PdfiumRaster.Orchestrator releases independently from PdfiumRaster. Its centrally managed `PdfiumRaster` dependency
range is the compatibility contract and must be reviewed whenever either package changes its public API.

The `Microsoft.CodeAnalysis.PublicApiAnalyzers` baseline is enforced during every build. Existing released symbols are
listed in `src/PdfiumRaster.Orchestrator/PublicAPI.Shipped.txt`; additions awaiting release are listed in
`PublicAPI.Unshipped.txt`. Review unshipped entries for XML documentation and compatibility, then move them to the
shipped file as part of the release commit. Do not delete or alter shipped entries to make an unintended breaking
change compile.

## Local release check

Install the .NET 10 SDK, then run:

```bash
make release-check PACKAGE_VERSION=0.3.0
```

This runs automated tests, publishes every supported self-contained worker, creates the NuGet and symbol packages,
verifies package contents, installs the package in a clean app, and renders a page through a packaged worker.

The package must contain:

- `lib/netstandard2.1/PdfiumRaster.Orchestrator.dll` and XML documentation;
- the root `README.md` and MIT license metadata;
- `buildTransitive/PdfiumRaster.Orchestrator.targets`;
- worker executables under all supported `tools/<rid>/` directories;
- a dependency on the intended PdfiumRaster version range.

CI additionally downloads the package built on Linux and runs the packaged-worker smoke test on Linux, Windows, and
macOS. This is distinct from the normal test suite, which runs a framework-dependent worker from the build output.

## GitHub Actions publishing

The `Publish NuGet` workflow is manually dispatched with a stable or beta channel, package version, and NuGet.org
profile. Configure the repository's `nuget` environment and create a NuGet trusted-publishing policy for this
repository and workflow before the first release. The workflow uses OIDC, runs the same tests and package smoke checks,
uploads the artifacts, and pushes `.nupkg` and `.snupkg` files to NuGet.org.

Use a SemVer value such as `0.3.0` for stable releases. Beta input may include a suffix such as `0.4.0-beta.1`; if the
suffix is omitted, the workflow appends a run-number beta suffix.
