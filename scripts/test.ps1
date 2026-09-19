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
$coreTests = Join-Path $repositoryRoot 'tests\IndependentVirtualDesktop.Core.Tests\IndependentVirtualDesktop.Core.Tests.csproj'
$appExecutable = Join-Path $repositoryRoot 'src\IndependentVirtualDesktop.App\bin\Release\net10.0-windows\IndependentVirtualDesktop.exe'
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
    foreach ($artifactPath in @($resultsPath, $imagePath)) {
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
            throw "Application self-test did not produce its expected artifact: $artifactPath"
        }
    }

    Get-Content -LiteralPath $resultsPath
    Write-Host "Overview image: $imagePath"
}
finally {
    Pop-Location
}
