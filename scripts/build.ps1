[CmdletBinding()]
param(
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'BetterVirtualDesktop.sln'
$appProject = Join-Path $repositoryRoot 'src\BetterVirtualDesktop.App\BetterVirtualDesktop.App.csproj'

Push-Location -LiteralPath $repositoryRoot
try {
    & dotnet build $solutionPath -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Solution build failed with exit code $LASTEXITCODE."
    }

    if ($Publish) {
        $publishDirectory = Join-Path $repositoryRoot 'artifacts\publish'
        & dotnet publish $appProject --self-contained true -r win-x64 -c Release -o $publishDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "Publish failed with exit code $LASTEXITCODE."
        }
        & (Join-Path $PSScriptRoot 'copy-runtime-notices.ps1') -PublishDirectory $publishDirectory
        Write-Host "Standalone application: $(Join-Path $publishDirectory 'BetterVirtualDesktop.exe')"
    }
}
finally {
    Pop-Location
}
