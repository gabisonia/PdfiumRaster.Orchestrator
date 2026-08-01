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
make release-check PACKAGE_VERSION=0.4.0
```

This runs automated tests with the enforced line and branch coverage thresholds, publishes every supported
self-contained worker, creates the NuGet and symbol packages, verifies package contents, installs the package in a
clean app, and renders a page through a packaged worker.

The enforced scenarios and platform-specific coverage strategy are listed in [the testing guide](TESTING.md).

The package must contain:

- `lib/netstandard2.1/PdfiumRaster.Orchestrator.dll` and XML documentation;
- the root `README.md`, `CHANGELOG.md`, and MIT license metadata;
- `buildTransitive/PdfiumRaster.Orchestrator.targets`;
- worker executables under all supported `tools/<rid>/` directories;
- a dependency on the intended PdfiumRaster version range.

CI additionally downloads the package built on Linux and runs the packaged-worker smoke test on Linux, Windows, and
macOS. This is distinct from the normal test suite, which runs a framework-dependent worker from the build output.

## Release checklist

1. Update `VersionPrefix` in the client project, the Makefile package default, CI package-smoke versions, publishing
   examples, and this guide to the intended stable version.
2. Move APIs that shipped in the previous release from `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt`. Review any
   APIs newly proposed for this release and move them only when the release surface is final.
3. Change the matching `CHANGELOG.md` heading from `Unreleased` to the release date and confirm that the NuGet release
   notes link points to the changelog.
4. Run `make release-check PACKAGE_VERSION=<version>` and inspect the generated `.nupkg` and `.snupkg` files.
5. Push the release commit and require the Linux, Windows, macOS, coverage, package, and packaged-worker smoke jobs to
   pass.
6. Create and push an annotated `v<version>` tag from that exact commit, then dispatch the stable publishing workflow
   for the same version and Git ref.
7. After publishing, verify the NuGet dependency range, README, symbols, and a clean installation from NuGet.org.

## GitHub Actions publishing

The `Publish NuGet` workflow is manually dispatched with a stable or beta channel, package version, and NuGet.org
profile. Configure the repository's `nuget` environment and create a NuGet trusted-publishing policy for this
repository and workflow before the first release. The workflow uses OIDC, runs the same tests and package smoke checks,
uploads the artifacts, and pushes `.nupkg` and `.snupkg` files to NuGet.org.

Use a SemVer value such as `0.4.0` for stable releases. Beta input may include a suffix such as `0.5.0-beta.1`; if the
suffix is omitted, the workflow appends a run-number beta suffix.
