param(
    [Parameter(Mandatory = $true)]
    [string] $RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [string] $PackageVersion
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("pdfium-raster-smoke-" + [Guid]::NewGuid().ToString('N'))
$projectDirectory = Join-Path $temporaryRoot 'PdfiumRasterOrchestratorSmoke'

try {
    dotnet new console -n PdfiumRasterOrchestratorSmoke -o $projectDirectory --framework net10.0
    if ($LASTEXITCODE -ne 0) { throw 'dotnet new failed.' }

    Push-Location $projectDirectory
    try {
        dotnet new nugetconfig --force
        if ($LASTEXITCODE -ne 0) { throw 'NuGet configuration creation failed.' }

        dotnet nuget add source (Join-Path $repositoryRoot 'artifacts') `
            --name local `
            --configfile nuget.config
        if ($LASTEXITCODE -ne 0) { throw 'Local package source configuration failed.' }

        dotnet add package PdfiumRaster.Orchestrator --version $PackageVersion --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Package installation failed.' }

        dotnet restore --configfile nuget.config
        if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }

        Copy-Item (Join-Path $repositoryRoot 'tests/PdfiumRaster.Orchestrator.Tests/TestAssets/smoke.pdf') 'input.pdf'
        @'
using PdfiumRaster;
using PdfiumRaster.Orchestration;

using var orchestrator = new PdfRenderOrchestrator(new PdfRenderOrchestratorOptions { WorkerCount = 1 });
await orchestrator.SavePageAsync("input.pdf", pageIndex: 0, "page.png", new PdfImageConversionOptions
{
    Render = PdfPageRenderOptions.ScreenPreview,
    Format = PdfImageOutputFormat.Png,
});
await orchestrator.CompleteAsync();

if (!File.Exists("page.png") || new FileInfo("page.png").Length == 0)
{
    throw new InvalidOperationException("Smoke test did not generate page.png.");
}
'@ | Set-Content -Path 'Program.cs' -Encoding utf8

        dotnet run --configuration Release
        if ($LASTEXITCODE -ne 0) { throw 'Packaged-worker smoke test failed.' }
    }
    finally {
        Pop-Location
    }
}
finally {
    if (Test-Path $temporaryRoot) {
        Remove-Item $temporaryRoot -Recurse -Force
    }
}
