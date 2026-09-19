[CmdletBinding()]
param(
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'IndependentVirtualDesktop.sln'
$appProject = Join-Path $repositoryRoot 'src\IndependentVirtualDesktop.App\IndependentVirtualDesktop.App.csproj'

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
        Write-Host "Standalone application: $(Join-Path $publishDirectory 'IndependentVirtualDesktop.exe')"
    }
}
finally {
    Pop-Location
}
