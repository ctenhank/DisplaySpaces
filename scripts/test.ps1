[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') {
    throw 'Application self-tests require an interactive Windows desktop session.'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$coreTests = Join-Path $repositoryRoot 'tests\BetterVirtualDesktop.Core.Tests\BetterVirtualDesktop.Core.Tests.csproj'
$appExecutable = Join-Path $repositoryRoot 'src\BetterVirtualDesktop.App\bin\Release\net10.0-windows\BetterVirtualDesktop.exe'
$outputDirectory = Join-Path $repositoryRoot 'artifacts\tests'

Push-Location -LiteralPath $repositoryRoot
try {
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot 'build.ps1')
    }

    & dotnet run --project $coreTests -c Release --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "Core tests failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
        throw "Application executable was not found. Build first: $appExecutable"
    }

    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $testProcess = Start-Process -FilePath $appExecutable `
        -ArgumentList @('--self-test', ('"' + $outputDirectory + '"')) `
        -WorkingDirectory $repositoryRoot -WindowStyle Hidden -Wait -PassThru
    try {
        if ($testProcess.ExitCode -ne 0) {
            throw "Application self-tests failed with exit code $($testProcess.ExitCode). See $outputDirectory."
        }
    }
    finally {
        $testProcess.Dispose()
    }

    $resultsPath = Join-Path $outputDirectory 'smoke-results.txt'
    $imagePath = Join-Path $outputDirectory 'overview.png'
    $overlayPath = Join-Path $outputDirectory 'overlay-primary.png'
    $compactOverlayPath = Join-Path $outputDirectory 'overlay-compact.png'
    $desktopNamePath = Join-Path $outputDirectory 'desktop-name-toast.png'
    $languagePaths = @(foreach ($languageCode in @('ko', 'en', 'zh-Hans', 'zh-Hant', 'ja')) {
        Join-Path $outputDirectory "overlay-language-$languageCode.png"
        Join-Path $outputDirectory "overlay-language-$languageCode-compact.png"
        Join-Path $outputDirectory "shortcuts-$languageCode.png"
        Join-Path $outputDirectory "shortcuts-$languageCode-compact.png"
    })
    $expectedArtifacts = @($resultsPath, $imagePath, $overlayPath, $compactOverlayPath, $desktopNamePath) + $languagePaths
    foreach ($artifactPath in $expectedArtifacts) {
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
            throw "Application self-test did not produce its expected artifact: $artifactPath"
        }
    }

    Get-Content -LiteralPath $resultsPath
    Write-Host "Overview image: $imagePath"
    Write-Host "Per-display overlay image: $overlayPath"
    Write-Host "Compact overlay image: $compactOverlayPath"
    Write-Host "Desktop name notification image: $desktopNamePath"
    Write-Host "Language feedback previews: $outputDirectory\overlay-language-*.png"
}
finally {
    Pop-Location
}
