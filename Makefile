SHELL := /bin/bash
SOLUTION := PdfiumRaster.Orchestrator.slnx
PROJECT := src/PdfiumRaster.Orchestrator/PdfiumRaster.Orchestrator.csproj
WORKER_PROJECT := src/PdfiumRaster.Orchestrator.Worker/PdfiumRaster.Orchestrator.Worker.csproj
TEST_PROJECT := tests/PdfiumRaster.Orchestrator.Tests/PdfiumRaster.Orchestrator.Tests.csproj
CONFIGURATION := Release
ARTIFACTS_DIR := artifacts
WORKER_ARTIFACTS_DIR := $(ARTIFACTS_DIR)/workers
WORKER_RIDS := win-x86 win-x64 win-arm64 linux-arm linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64 osx-x64 osx-arm64
PACKAGE_VERSION ?= 1.1.0
PACKAGE_ID ?= PdfiumRaster.Orchestrator
PACKAGE := $(ARTIFACTS_DIR)/PdfiumRaster.Orchestrator.$(PACKAGE_VERSION).nupkg
BENCHMARK_PROJECT := benchmarks/PdfiumRaster.Orchestrator.Benchmarks/PdfiumRaster.Orchestrator.Benchmarks.csproj
FAKE_WORKER_PROJECT := tests/PdfiumRaster.Orchestrator.FakeWorker/PdfiumRaster.Orchestrator.FakeWorker.csproj
BASE_REF ?= 1.0.0

.PHONY: help restore build test coverage benchmark performance-check test-local test-manual publish-workers pack inspect-package verify-package smoke-package smoke-slim-package smoke-slim-package-mismatch release-check clean

help:
	@printf '%s\n' \
		'Available targets:' \
		'  make restore          Restore NuGet packages' \
		'  make build            Build the solution in Release mode' \
		'  make test             Run automated tests, excluding local-only tests' \
		'  make coverage         Run tests with enforced line and branch coverage thresholds' \
		'  make benchmark        Run the current 16 MiB pipe-transfer benchmarks' \
		'  make performance-check Compare transfer performance with BASE_REF (default: 1.0.0)' \
		'  make test-local       Run all local-only tests' \
		'  make test-manual PDF=<path> Render every PDF page for visual inspection' \
		'  make publish-workers  Publish all supported self-contained workers' \
		'  make pack             Create the all-runtime and RID-specific NuGet packages' \
		'  make inspect-package  List package contents and nuspec metadata' \
		'  make verify-package   Assert all required package assets are present' \
		'  make smoke-package    Install the local package in a fresh app and render a page' \
		'  make smoke-slim-package Smoke test the current platform slim package' \
		'  make smoke-slim-package-mismatch Verify a slim package rejects a mismatched RID' \
		'  make release-check    Run tests, pack, inspect, and the package smoke test' \
		'  make clean            Remove build and package outputs'

restore:
	dotnet restore $(SOLUTION)

build:
	dotnet build $(SOLUTION) -c $(CONFIGURATION) --no-restore

test:
	dotnet test $(SOLUTION) -c $(CONFIGURATION) --filter "Category!=Local"

coverage:
	rm -rf $(ARTIFACTS_DIR)/coverage
	dotnet test $(TEST_PROJECT) -c $(CONFIGURATION) --filter "Category!=Local" \
		--collect "XPlat Code Coverage" \
		--results-directory $(ARTIFACTS_DIR)/coverage
	bash eng/AssertCoverage.sh $(ARTIFACTS_DIR)/coverage 90 80

benchmark:
	dotnet build $(FAKE_WORKER_PROJECT) -c $(CONFIGURATION) --no-restore
	PDFIUMRASTER_BENCHMARK_WORKER_PATH="$$(pwd)/tests/PdfiumRaster.Orchestrator.FakeWorker/bin/$(CONFIGURATION)/net10.0/PdfiumRaster.Orchestrator.FakeWorker.dll" \
		dotnet run --project $(BENCHMARK_PROJECT) -c $(CONFIGURATION) -- \
		--filter '*' --artifacts "$(ARTIFACTS_DIR)/benchmarks"

performance-check:
	@set -euo pipefail; \
	repo="$$(pwd)"; \
	git rev-parse --verify "$(BASE_REF)^{commit}" >/dev/null; \
	tmpdir="$$(mktemp -d)"; \
	trap 'rm -rf "$$tmpdir"' EXIT; \
	mkdir -p "$$tmpdir/baseline" "$$tmpdir/baseline/benchmarks/PdfiumRaster.Orchestrator.Benchmarks"; \
	git archive "$(BASE_REF)" | tar -x -C "$$tmpdir/baseline"; \
	cp "$$repo/Directory.Packages.props" "$$tmpdir/baseline/Directory.Packages.props"; \
	cp "$$repo/src/PdfiumRaster.Orchestrator/PdfiumRaster.Orchestrator.csproj" \
		"$$tmpdir/baseline/src/PdfiumRaster.Orchestrator/PdfiumRaster.Orchestrator.csproj"; \
	cp "$$repo/benchmarks/PdfiumRaster.Orchestrator.Benchmarks/PdfiumRaster.Orchestrator.Benchmarks.csproj" \
		"$$repo/benchmarks/PdfiumRaster.Orchestrator.Benchmarks/Program.cs" \
		"$$repo/benchmarks/PdfiumRaster.Orchestrator.Benchmarks/packages.lock.json" \
		"$$tmpdir/baseline/benchmarks/PdfiumRaster.Orchestrator.Benchmarks/"; \
	dotnet build "$$repo/$(FAKE_WORKER_PROJECT)" -c $(CONFIGURATION) --no-restore; \
	worker="$$repo/tests/PdfiumRaster.Orchestrator.FakeWorker/bin/$(CONFIGURATION)/net10.0/PdfiumRaster.Orchestrator.FakeWorker.dll"; \
	PDFIUMRASTER_BENCHMARK_WORKER_PATH="$$worker" PDFIUMRASTER_BENCHMARK_BASELINE=1 dotnet run \
		--project "$$tmpdir/baseline/benchmarks/PdfiumRaster.Orchestrator.Benchmarks/PdfiumRaster.Orchestrator.Benchmarks.csproj" \
		-c $(CONFIGURATION) -- --filter '*' --artifacts "$$tmpdir/baseline-results"; \
	PDFIUMRASTER_BENCHMARK_WORKER_PATH="$$worker" dotnet run --project "$$repo/$(BENCHMARK_PROJECT)" \
		-c $(CONFIGURATION) -- --filter '*' --artifacts "$$tmpdir/candidate-results"; \
	dotnet run --project "$$repo/$(BENCHMARK_PROJECT)" -c $(CONFIGURATION) --no-build -- \
		compare "$$tmpdir/baseline-results" "$$tmpdir/candidate-results"

test-local:
	dotnet test $(SOLUTION) -c $(CONFIGURATION) --filter "Category=Local"

test-manual:
	@if [[ -z "$(PDF)" ]]; then echo 'Usage: make test-manual PDF=<pdf-path>' >&2; exit 2; fi; \
	PDFIUMRASTER_MANUAL_PDF="$(PDF)" dotnet test $(TEST_PROJECT) -c $(CONFIGURATION) \
		--filter "FullyQualifiedName~PdfiumRaster.Orchestration.Tests.ManualOrchestratorRenderingTests"

publish-workers:
	@set -euo pipefail; \
	for rid in $(WORKER_RIDS); do \
		dotnet publish $(WORKER_PROJECT) \
			-c $(CONFIGURATION) \
			-r "$$rid" \
			--self-contained true \
			/p:Version=$(PACKAGE_VERSION) \
			-o "$(WORKER_ARTIFACTS_DIR)/$$rid"; \
	done

pack: publish-workers
	dotnet pack $(PROJECT) -c $(CONFIGURATION) -o $(ARTIFACTS_DIR) \
		/p:PackageVersion=$(PACKAGE_VERSION) \
		/p:PdfiumRasterOrchestratorWorkerArtifacts="$$(pwd)/$(WORKER_ARTIFACTS_DIR)"
	@set -euo pipefail; \
	for rid in $(WORKER_RIDS); do \
		dotnet pack $(PROJECT) -c $(CONFIGURATION) -o $(ARTIFACTS_DIR) --no-build \
			/p:PackageVersion=$(PACKAGE_VERSION) \
			/p:PdfiumRasterOrchestratorPackageRid="$$rid" \
			/p:PdfiumRasterOrchestratorWorkerArtifacts="$$(pwd)/$(WORKER_ARTIFACTS_DIR)"; \
	done

inspect-package: $(PACKAGE)
	unzip -l $(PACKAGE)
	unzip -p $(PACKAGE) PdfiumRaster.Orchestrator.nuspec

verify-package: $(PACKAGE)
	@set -euo pipefail; \
	entries="$$(unzip -Z1 "$(PACKAGE)")"; \
	require_entry() { \
		if ! grep -Fqx "$$1" <<<"$$entries"; then \
			echo "Required package entry is missing: $$1" >&2; \
			exit 1; \
		fi; \
	}; \
	require_entry 'README.md'; \
	require_entry 'CHANGELOG.md'; \
	require_entry 'lib/netstandard2.1/PdfiumRaster.Orchestrator.dll'; \
	require_entry 'lib/netstandard2.1/PdfiumRaster.Orchestrator.xml'; \
	require_entry 'buildTransitive/PdfiumRaster.Orchestrator.targets'; \
	for rid in $(WORKER_RIDS); do \
		case "$$rid" in win-*) worker='PdfiumRaster.Orchestrator.Worker.exe' ;; \
		*) worker='PdfiumRaster.Orchestrator.Worker' ;; \
		esac; \
		require_entry "tools/$$rid/$$worker"; \
	done; \
	nuspec="$$(unzip -p "$(PACKAGE)" PdfiumRaster.Orchestrator.nuspec)"; \
	if ! grep -Eq 'id="PdfiumRaster" version="\[2\.0\.1, ?3\.0\.0\)"' <<<"$$nuspec"; then \
		echo 'The PdfiumRaster dependency range is missing or unexpected.' >&2; \
		exit 1; \
	fi; \
	echo "Verified required assets in $(PACKAGE)."
	@set -euo pipefail; \
	for rid in $(WORKER_RIDS); do \
		package="$(ARTIFACTS_DIR)/PdfiumRaster.Orchestrator.$$rid.$(PACKAGE_VERSION).nupkg"; \
		if [[ ! -f "$$package" ]]; then echo "RID package is missing: $$package" >&2; exit 1; fi; \
		entries="$$(unzip -Z1 "$$package")"; \
		for entry in README.md CHANGELOG.md lib/netstandard2.1/PdfiumRaster.Orchestrator.dll lib/netstandard2.1/PdfiumRaster.Orchestrator.xml "buildTransitive/PdfiumRaster.Orchestrator.$$rid.targets"; do \
			if ! grep -Fqx "$$entry" <<<"$$entries"; then echo "Required package entry is missing from $$package: $$entry" >&2; exit 1; fi; \
		done; \
		case "$$rid" in win-*) worker='PdfiumRaster.Orchestrator.Worker.exe' ;; *) worker='PdfiumRaster.Orchestrator.Worker' ;; esac; \
		if ! grep -Fqx "tools/$$rid/$$worker" <<<"$$entries"; then echo "Worker is missing from $$package" >&2; exit 1; fi; \
		worker_count="$$(grep -Ec '^tools/.*/PdfiumRaster\.Orchestrator\.Worker(\.exe)?$$' <<<"$$entries")"; \
		if [[ "$$worker_count" -ne 1 ]]; then echo "$$package must contain exactly one worker, found $$worker_count" >&2; exit 1; fi; \
		nuspec="$$(unzip -p "$$package" '*.nuspec')"; \
		if ! grep -Eq 'id="PdfiumRaster" version="\[2\.0\.1, ?3\.0\.0\)"' <<<"$$nuspec"; then echo "PdfiumRaster dependency range is unexpected in $$package" >&2; exit 1; fi; \
	done; \
	echo 'Verified all RID-specific packages.'

smoke-package: $(PACKAGE)
	set -euo pipefail; \
	repo="$$(pwd)"; \
	tmpdir="$$(mktemp -d)"; \
	export NUGET_PACKAGES="$$tmpdir/nuget-packages"; \
	trap 'rm -rf "$$tmpdir"' EXIT; \
	dotnet new console -n PdfiumRasterOrchestratorSmoke -o "$$tmpdir/PdfiumRasterOrchestratorSmoke" --framework net10.0 >/dev/null; \
	cd "$$tmpdir/PdfiumRasterOrchestratorSmoke"; \
	printf '%s\n' \
		'<?xml version="1.0" encoding="utf-8"?>' \
		'<configuration>' \
		'  <packageSources>' \
		'    <clear />' \
		"    <add key=\"local\" value=\"$$repo/$(ARTIFACTS_DIR)\" />" \
		'    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' \
		'  </packageSources>' \
		'</configuration>' > NuGet.config; \
	dotnet add package $(PACKAGE_ID) --version $(PACKAGE_VERSION); \
	cp "$$repo/tests/PdfiumRaster.Orchestrator.Tests/TestAssets/smoke.pdf" ./input.pdf; \
	printf '%s\n' \
		'using PdfiumRaster;' \
		'using PdfiumRaster.Orchestration;' \
		'' \
		'await using var orchestrator = await PdfRenderOrchestrator.CreateAsync(new PdfRenderOrchestratorOptions { WorkerCount = 1 });' \
		'var status = orchestrator.GetStatus();' \
		'var document = await orchestrator.InspectDocumentAsync("input.pdf");' \
		'await orchestrator.SavePageAsync("input.pdf", pageIndex: 0, "page.png", new PdfImageConversionOptions' \
		'{' \
		'    Render = PdfPageRenderOptions.ScreenPreview,' \
		'    Format = PdfImageOutputFormat.Png,' \
		'});' \
		'var streamedPages = 0;' \
		'await foreach (var page in orchestrator.RenderPagesStreamAsync("input.pdf", new[] { 0 }))' \
		'{' \
		'    if (page.Position != 0 || page.PageIndex != 0 || page.Bitmap.Pixels.Length == 0)' \
		'    {' \
		'        throw new InvalidOperationException("Streaming smoke test returned an invalid page.");' \
		'    }' \
		'    streamedPages++;' \
		'}' \
		'await orchestrator.CompleteAsync();' \
		'var stopped = orchestrator.GetStatus();' \
		'' \
		'if (status.State != PdfRenderOrchestratorState.Healthy || status.AvailableWorkerCount != 1 ||' \
		'    status.WorkerCount != 1 || stopped.State != PdfRenderOrchestratorState.Stopped ||' \
		'    document.PageCount != 1 || document.PageSizes.Count != 1 ||' \
		'    document.PageSizes[0].Width <= 0 || document.PageSizes[0].Height <= 0 ||' \
		'    streamedPages != 1 || !File.Exists("page.png") || new FileInfo("page.png").Length == 0)' \
		'{' \
		'    throw new InvalidOperationException("Smoke test did not generate page.png.");' \
		'}' > Program.cs; \
	dotnet run --configuration Release

smoke-slim-package: $(PACKAGE)
	@set -euo pipefail; \
	rid="$$(dotnet --info | awk '/RID:/{print $$2; exit}')"; \
	if [[ -z "$$rid" ]]; then echo 'Could not determine the current .NET runtime identifier.' >&2; exit 1; fi; \
	$(MAKE) smoke-package PACKAGE_ID="PdfiumRaster.Orchestrator.$$rid" PACKAGE_VERSION=$(PACKAGE_VERSION)

smoke-slim-package-mismatch: $(PACKAGE)
	@set -euo pipefail; \
	repo="$$(pwd)"; \
	rid="$$(dotnet --info | awk '/RID:/{print $$2; exit}')"; \
	if [[ -z "$$rid" ]]; then echo 'Could not determine the current .NET runtime identifier.' >&2; exit 1; fi; \
	case "$$rid" in win-*) mismatch_rid='linux-x64' ;; *) mismatch_rid='win-x64' ;; esac; \
	package="$(ARTIFACTS_DIR)/PdfiumRaster.Orchestrator.$$rid.$(PACKAGE_VERSION).nupkg"; \
	if [[ ! -f "$$package" ]]; then echo "Current-platform slim package is missing: $$package" >&2; exit 1; fi; \
	tmpdir="$$(mktemp -d)"; \
	export NUGET_PACKAGES="$$tmpdir/nuget-packages"; \
	trap 'rm -rf "$$tmpdir"' EXIT; \
	dotnet new console -n PdfiumRasterOrchestratorRidMismatch -o "$$tmpdir/app" --framework net10.0 >/dev/null; \
	cd "$$tmpdir/app"; \
	dotnet new nugetconfig --force >/dev/null; \
	dotnet nuget add source "$$repo/$(ARTIFACTS_DIR)" --name local --configfile nuget.config >/dev/null; \
	dotnet add package "PdfiumRaster.Orchestrator.$$rid" --version "$(PACKAGE_VERSION)" --no-restore >/dev/null; \
	dotnet restore --configfile nuget.config >/dev/null; \
	set +e; \
	output="$$(dotnet build --configuration Release --runtime "$$mismatch_rid" --no-restore 2>&1)"; \
	status=$$?; \
	set -e; \
	if [[ $$status -eq 0 ]]; then echo 'A mismatched slim package unexpectedly built successfully.' >&2; exit 1; fi; \
	if ! grep -Fq 'does not match target runtime' <<<"$$output"; then printf '%s\n' "$$output" >&2; exit 1; fi; \
	echo "Verified that PdfiumRaster.Orchestrator.$$rid rejects target runtime $$mismatch_rid."

release-check: coverage performance-check pack verify-package inspect-package smoke-package smoke-slim-package smoke-slim-package-mismatch

clean:
	dotnet clean $(SOLUTION)
	rm -rf $(ARTIFACTS_DIR) src/PdfiumRaster.Orchestrator/bin src/PdfiumRaster.Orchestrator/obj src/PdfiumRaster.Orchestrator.Worker/bin src/PdfiumRaster.Orchestrator.Worker/obj tests/PdfiumRaster.Orchestrator.Tests/bin tests/PdfiumRaster.Orchestrator.Tests/obj tests/PdfiumRaster.Orchestrator.Tests/ManualOutput samples/ParallelPageExport/bin samples/ParallelPageExport/obj samples/AspNetLifecycle/bin samples/AspNetLifecycle/obj
