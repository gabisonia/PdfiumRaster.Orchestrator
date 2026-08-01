SHELL := /bin/bash
SOLUTION := PdfiumRaster.Orchestrator.slnx
PROJECT := src/PdfiumRaster.Orchestrator/PdfiumRaster.Orchestrator.csproj
WORKER_PROJECT := src/PdfiumRaster.Orchestrator.Worker/PdfiumRaster.Orchestrator.Worker.csproj
TEST_PROJECT := tests/PdfiumRaster.Orchestrator.Tests/PdfiumRaster.Orchestrator.Tests.csproj
CONFIGURATION := Release
ARTIFACTS_DIR := artifacts
WORKER_ARTIFACTS_DIR := $(ARTIFACTS_DIR)/workers
WORKER_RIDS := win-x86 win-x64 win-arm64 linux-arm linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64 osx-x64 osx-arm64
PACKAGE_VERSION ?= 0.3.0
PACKAGE := $(ARTIFACTS_DIR)/PdfiumRaster.Orchestrator.$(PACKAGE_VERSION).nupkg

.PHONY: help restore build test coverage test-local test-manual publish-workers pack inspect-package verify-package smoke-package release-check clean

help:
	@printf '%s\n' \
		'Available targets:' \
		'  make restore          Restore NuGet packages' \
		'  make build            Build the solution in Release mode' \
		'  make test             Run automated tests, excluding local-only tests' \
		'  make coverage         Run tests with enforced line and branch coverage thresholds' \
		'  make test-local       Run all local-only tests' \
		'  make test-manual PDF=<path> Render every PDF page for visual inspection' \
		'  make publish-workers  Publish all supported self-contained workers' \
		'  make pack             Create the PdfiumRaster.Orchestrator NuGet package' \
		'  make inspect-package  List package contents and nuspec metadata' \
		'  make verify-package   Assert all required package assets are present' \
		'  make smoke-package    Install the local package in a fresh app and render a page' \
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

smoke-package: $(PACKAGE)
	set -euo pipefail; \
	repo="$$(pwd)"; \
	tmpdir="$$(mktemp -d)"; \
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
		'  <config>' \
		'    <add key="globalPackagesFolder" value=".packages" />' \
		'  </config>' \
		'</configuration>' > NuGet.config; \
	dotnet add package PdfiumRaster.Orchestrator --version $(PACKAGE_VERSION) >/dev/null; \
	cp "$$repo/tests/PdfiumRaster.Orchestrator.Tests/TestAssets/smoke.pdf" ./input.pdf; \
	printf '%s\n' \
		'using PdfiumRaster;' \
		'using PdfiumRaster.Orchestration;' \
		'' \
		'using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions { WorkerCount = 1 });' \
		'await orchestrator.SavePageAsync("input.pdf", pageIndex: 0, "page.png", new PdfImageConversionOptions' \
		'{' \
		'    Render = PdfPageRenderOptions.ScreenPreview,' \
		'    Format = PdfImageOutputFormat.Png,' \
		'});' \
		'await orchestrator.CompleteAsync();' \
		'' \
		'if (!File.Exists("page.png") || new FileInfo("page.png").Length == 0)' \
		'{' \
		'    throw new InvalidOperationException("Smoke test did not generate page.png.");' \
		'}' > Program.cs; \
	dotnet run --configuration Release

release-check: coverage pack verify-package inspect-package smoke-package

clean:
	dotnet clean $(SOLUTION)
	rm -rf $(ARTIFACTS_DIR) src/PdfiumRaster.Orchestrator/bin src/PdfiumRaster.Orchestrator/obj src/PdfiumRaster.Orchestrator.Worker/bin src/PdfiumRaster.Orchestrator.Worker/obj tests/PdfiumRaster.Orchestrator.Tests/bin tests/PdfiumRaster.Orchestrator.Tests/obj tests/PdfiumRaster.Orchestrator.Tests/ManualOutput samples/ParallelPageExport/bin samples/ParallelPageExport/obj samples/AspNetLifecycle/bin samples/AspNetLifecycle/obj
