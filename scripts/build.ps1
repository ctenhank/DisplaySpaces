[CmdletBinding()]
param(
    [switch]$Publish,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'DisplaySpaces.sln'
$appProject = Join-Path $repositoryRoot 'src\DisplaySpaces.App\DisplaySpaces.App.csproj'

Push-Location -LiteralPath $repositoryRoot
try {
    & dotnet build $solutionPath -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Solution build failed with exit code $LASTEXITCODE."
    }

    if ($Publish -or $Install) {
        $publishDirectory = Join-Path $repositoryRoot 'artifacts\publish'
        & dotnet publish $appProject --self-contained true -r win-x64 -c Release -o $publishDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "Publish failed with exit code $LASTEXITCODE."
        }
        & (Join-Path $PSScriptRoot 'copy-runtime-notices.ps1') -PublishDirectory $publishDirectory
        Write-Host "Standalone application: $(Join-Path $publishDirectory 'DisplaySpaces.exe')"
        if ($Install) {
            & (Join-Path $PSScriptRoot 'install.ps1') -PublishDirectory $publishDirectory
        }
    }
}
finally {
    Pop-Location
}
