[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The build script publishes a self-contained Windows x64 app. Read the actual
# bundled versions rather than assuming that they match the installed SDK.
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory -ErrorAction Stop).ProviderPath
$runtimeConfigPath = Join-Path $publishRoot 'BetterVirtualDesktop.runtimeconfig.json'
$assetsPath = Join-Path $repositoryRoot 'src\BetterVirtualDesktop.App\obj\project.assets.json'
if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
    throw "Published runtime configuration was not found: $runtimeConfigPath"
}
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
    throw "NuGet restore metadata was not found: $assetsPath. Restore or publish the app first."
}

$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
$runtimeOptions = $runtimeConfig.PSObject.Properties['runtimeOptions']
if ($null -eq $runtimeOptions -or $null -eq $runtimeOptions.Value.PSObject.Properties['includedFrameworks']) {
    throw "The published app must be self-contained; includedFrameworks is missing from $runtimeConfigPath."
}
$frameworks = @($runtimeOptions.Value.includedFrameworks)
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$foldersProperty = $assets.PSObject.Properties['packageFolders']
if ($null -eq $foldersProperty) {
    throw "NuGet packageFolders is missing from $assetsPath. Restore the app first."
}
$packageFolders = @($foldersProperty.Value.PSObject.Properties | ForEach-Object { $_.Name })
if ($packageFolders.Count -eq 0) {
    throw "No NuGet package cache was recorded in $assetsPath."
}

$requirements = @(
    @{ Framework = 'Microsoft.NETCore.App'; Package = 'microsoft.netcore.app.runtime.win-x64';
       Files = @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') },
    @{ Framework = 'Microsoft.WindowsDesktop.App'; Package = 'microsoft.windowsdesktop.app.runtime.win-x64';
       Files = @('LICENSE') }
)
$copies = @()
foreach ($requirement in $requirements) {
    $matching = @($frameworks | Where-Object { $_.name -eq $requirement.Framework })
    if ($matching.Count -ne 1 -or $null -eq $matching[0].PSObject.Properties['version']) {
        throw "Expected one bundled $($requirement.Framework) version in $runtimeConfigPath."
    }
    $version = [string]$matching[0].version
    if ($version -notmatch '^10\.0\.\d+$') {
        throw "Unsupported $($requirement.Framework) version '$version'; this helper supports stable .NET 10 Windows x64 runtimes."
    }

    $packageDirectory = $null
    foreach ($cache in $packageFolders) {
        $candidate = Join-Path (Join-Path $cache $requirement.Package) $version
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $packageDirectory = $candidate
            break
        }
    }
    if ($null -eq $packageDirectory) {
        throw "Runtime notices are unavailable: $($requirement.Package) $version was not found in the restored NuGet caches. Run dotnet publish with --self-contained true -r win-x64 first."
    }

    foreach ($file in $requirement.Files) {
        $source = Join-Path $packageDirectory $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Required upstream runtime notice is missing: $source. Restore the exact runtime package before distributing this build."
        }
        $noticeName = if ($file -eq 'LICENSE') { 'LICENSE.TXT' } else { $file }
        $copies += [pscustomobject]@{
            Source = $source
            Name = "$($requirement.Framework)-$noticeName"
        }
    }
}

# Resolve and validate every upstream file before writing the distribution.
# Copy bytes unchanged; these are runtime notices, not the app's own license.
$licensesDirectory = Join-Path $publishRoot 'licenses'
[void](New-Item -ItemType Directory -Path $licensesDirectory -Force)
foreach ($copy in $copies) {
    Copy-Item -LiteralPath $copy.Source -Destination (Join-Path $licensesDirectory $copy.Name) -Force
}
Write-Host "Runtime license notices: $licensesDirectory"
