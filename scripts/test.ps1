[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$StartupOnly,
    [switch]$MovesOnly,
    [switch]$TaskbarOnly,
    [switch]$DesktopNamesOnly,
    [switch]$DisplaySwitchingOnly,
    [switch]$MoveFollowOnly,
    [switch]$DesktopReorderOnly,
    [switch]$SettingsOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (@(@($StartupOnly, $MovesOnly, $TaskbarOnly, $DesktopNamesOnly, $DisplaySwitchingOnly, $MoveFollowOnly, $DesktopReorderOnly, $SettingsOnly) | Where-Object { $_ }).Count -gt 1) { throw 'Choose only one focused test mode.' }

if ($env:OS -ne 'Windows_NT') {
    throw 'Application self-tests require an interactive Windows desktop session.'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$coreTests = Join-Path $repositoryRoot 'tests\DisplaySpaces.Core.Tests\DisplaySpaces.Core.Tests.csproj'
$appExecutable = Join-Path $repositoryRoot 'src\DisplaySpaces.App\bin\Release\net10.0-windows\DisplaySpaces.exe'
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
    $testMode = if ($StartupOnly) { '--self-test-startup' } elseif ($MovesOnly) { '--self-test-moves' } elseif ($TaskbarOnly) { '--self-test-taskbar' } elseif ($DesktopNamesOnly) { '--self-test-desktop-names' } elseif ($DisplaySwitchingOnly) { '--self-test-display-switching' } elseif ($MoveFollowOnly) { '--self-test-move-follow' } elseif ($DesktopReorderOnly) { '--self-test-desktop-reorder' } elseif ($SettingsOnly) { '--self-test-settings' } else { '--self-test' }
    $testProcess = Start-Process -FilePath $appExecutable `
        -ArgumentList @($testMode, ('"' + $outputDirectory + '"')) `
        -WorkingDirectory $repositoryRoot -WindowStyle Hidden -Wait -PassThru
    try {
        if ($testProcess.ExitCode -ne 0) {
            throw "Application self-tests failed with exit code $($testProcess.ExitCode). See $outputDirectory."
        }
    }
    finally {
        $testProcess.Dispose()
    }

    if ($StartupOnly) {
        Get-Content -LiteralPath (Join-Path $outputDirectory 'startup-results.txt')
        return
    }
    if ($MovesOnly) {
        Get-Content -LiteralPath (Join-Path $outputDirectory 'move-results.txt')
        return
    }
    if ($TaskbarOnly) {
        Get-Content -LiteralPath (Join-Path $outputDirectory 'taskbar-results.txt')
        return
    }
    if ($DesktopNamesOnly) {
        Get-Content -LiteralPath (Join-Path $outputDirectory 'desktop-name-results.txt')
        return
    }
    if ($DisplaySwitchingOnly) {
        Get-Content -LiteralPath (Join-Path $outputDirectory 'display-switching-results.txt')
        return
    }
    if ($MoveFollowOnly) {
        Get-Content -LiteralPath (Join-Path $outputDirectory 'window-move-follow-results.txt')
        return
    }
    if ($DesktopReorderOnly) {
        Get-Content -LiteralPath (Join-Path $outputDirectory 'desktop-reorder-results.txt')
        return
    }
    if ($SettingsOnly) {
        Get-Content -LiteralPath (Join-Path $outputDirectory 'settings-results.txt')
        return
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
