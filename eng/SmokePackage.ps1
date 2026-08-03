param(
    [Parameter(Mandatory = $true)]
    [string] $RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [string] $PackageVersion,

    [string] $PackageId = 'PdfiumRaster.Orchestrator'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("pdfium-raster-smoke-" + [Guid]::NewGuid().ToString('N'))
$projectDirectory = Join-Path $temporaryRoot 'PdfiumRasterOrchestratorSmoke'
$originalNugetPackages = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = Join-Path $temporaryRoot 'nuget-packages'

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

        dotnet add package $PackageId --version $PackageVersion --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Package installation failed.' }

        dotnet restore --configfile nuget.config
        if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }

        Copy-Item (Join-Path $repositoryRoot 'tests/PdfiumRaster.Orchestrator.Tests/TestAssets/smoke.pdf') 'input.pdf'
        @'
using PdfiumRaster;
using PdfiumRaster.Orchestration;

await using var orchestrator = await PdfRenderOrchestrator.CreateAsync(
    new PdfRenderOrchestratorOptions { WorkerCount = 1 });
var pageCount = await orchestrator.GetPageCountAsync("input.pdf");
var pageSizes = await orchestrator.GetPageSizesAsync("input.pdf");
await orchestrator.SavePageAsync("input.pdf", pageIndex: 0, "page.png", new PdfImageConversionOptions
{
    Render = PdfPageRenderOptions.ScreenPreview,
    Format = PdfImageOutputFormat.Png,
});
var streamedPages = 0;
await foreach (var page in orchestrator.RenderPagesStreamAsync("input.pdf", new[] { 0 }))
{
    if (page.Position != 0 || page.PageIndex != 0 || page.Bitmap.Pixels.Length == 0)
    {
        throw new InvalidOperationException("Streaming smoke test returned an invalid page.");
    }

    streamedPages++;
}
await orchestrator.CompleteAsync();

if (pageCount != 1 || pageSizes.Count != 1 || pageSizes[0].Width <= 0 || pageSizes[0].Height <= 0 ||
    streamedPages != 1 || !File.Exists("page.png") || new FileInfo("page.png").Length == 0)
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
    if ($null -eq $originalNugetPackages) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $originalNugetPackages
    }

    if (Test-Path $temporaryRoot) {
        Remove-Item $temporaryRoot -Recurse -Force
    }
}
