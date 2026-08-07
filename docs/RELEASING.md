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
make release-check PACKAGE_VERSION=1.1.0
```

This runs automated tests with the enforced line and branch coverage thresholds, publishes every supported
self-contained worker, creates the all-runtime and RID-specific NuGet and symbol packages, verifies package contents,
installs both applicable package shapes in clean apps, and renders through their packaged workers.

Stable release checks also run the 16 MiB pipe-transfer benchmark against the local `1.0.0` tag on the same machine.
The candidate must reduce synchronous framing allocations by at least 50% and keep each end-to-end median at or below
105% of the baseline. Use `make benchmark` for a candidate-only report or
`make performance-check BASE_REF=1.0.0` for the enforced comparison.

The enforced scenarios and platform-specific coverage strategy are listed in [the testing guide](TESTING.md).

The package must contain:

- `lib/netstandard2.1/PdfiumRaster.Orchestrator.dll` and XML documentation;
- the root `README.md`, `CHANGELOG.md`, and MIT license metadata;
- `buildTransitive/PdfiumRaster.Orchestrator.targets` in the all-runtime package and the package-ID-matching
  `buildTransitive/PdfiumRaster.Orchestrator.<rid>.targets` in each slim package;
- worker executables under all supported `tools/<rid>/` directories in the all-runtime package;
- exactly one matching `tools/<rid>/` worker in each `PdfiumRaster.Orchestrator.<rid>` package;
- a dependency on the intended PdfiumRaster version range.

CI additionally downloads packages built on Linux and runs all-runtime and matching slim-package worker smoke tests on
Linux, Windows, and macOS. This is distinct from the normal test suite, which runs a framework-dependent worker from
the build output.

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

Use a SemVer value such as `1.1.0` for stable releases. Beta input may include a suffix such as `1.1.0-beta.1`; if the
suffix is omitted, the workflow appends a run-number beta suffix.

## OpenSSF Best Practices badge

The root `.bestpractices.json` contains proposed answers backed by public repository evidence. It does not grant a
badge by itself: an authorized maintainer must sign in to [OpenSSF Best Practices](https://www.bestpractices.dev/),
add `https://github.com/gabisonia/PdfiumRaster.Orchestrator` as a Metal-series project, review every automated answer,
and save the assessment. Do not accept a proposal if repository settings or actual maintainer practice do not support
it.

After registration assigns a numeric project ID, add the service's ID-based badge to `README.md`. Use **Save and
continue** in the assessment after material process, security, test, or release changes so the service reloads
`.bestpractices.json`. The OpenSSF Scorecard `CII-Best-Practices` check will discover the registered assessment through
the repository URL; a README badge alone does not satisfy that check.
