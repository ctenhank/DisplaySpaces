[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# File operations run only in this unique workspace fixture. Mock process discovery
# and the one injected rename failure; never launch, stop, or update the real app.
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureRoot = Join-Path $repositoryRoot ('artifacts\installer-tests\' + [guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $fixtureRoot 'publish'
$targetRoot = Join-Path $fixtureRoot 'Programs\BetterVirtualDesktop'
$installer = Join-Path $PSScriptRoot 'install.ps1'
$installerTestState = [pscustomobject]@{ Processes = @(); FailSwap = $false }
$script:checks = 0

Set-Item Function:Get-Process -Value ({
    param([string[]]$Name, [string]$ErrorAction)
    foreach ($entry in $installerTestState.Processes) {
        $process = [pscustomobject]@{ Id = 987654; Path = $entry }
        $process | Add-Member -MemberType ScriptMethod -Name Dispose -Value { }
        $process
    }
}.GetNewClosure())

Set-Item Function:Move-Item -Value ({
    param([string]$LiteralPath, [string]$Destination)
    if ($installerTestState.FailSwap -and
        [System.IO.Path]::GetFileName($LiteralPath).StartsWith('.BetterVirtualDesktop.stage-')) {
        throw 'Injected destination rename failure.'
    }
    Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
}.GetNewClosure())

function Assert {
    param([bool]$Condition, [string]$Description)
    if (-not $Condition) { throw "FAIL: $Description" }
    $script:checks++
    Write-Host "PASS: $Description"
}

function Assert-Rejected {
    param([scriptblock]$Action, [string]$Message, [string]$Description)
    $failed = $false
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $failed = $true
    }
    Assert $failed $Description
}

[void][System.IO.Directory]::CreateDirectory((Join-Path $sourceRoot 'licenses'))
foreach ($file in @(
    'BetterVirtualDesktop.exe', 'BetterVirtualDesktop.dll', 'BetterVirtualDesktop.deps.json',
    'BetterVirtualDesktop.Core.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
    'System.Windows.Forms.dll', 'licenses\Microsoft.NETCore.App-LICENSE.TXT',
    'licenses\Microsoft.NETCore.App-THIRD-PARTY-NOTICES.TXT',
    'licenses\Microsoft.WindowsDesktop.App-LICENSE.TXT'
)) {
    Set-Content -LiteralPath (Join-Path $sourceRoot $file) -Value 'fixture-version-one' -Encoding UTF8
}
$runtimeConfig = '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.6"},{"name":"Microsoft.WindowsDesktop.App","version":"10.0.6"}]}}'
Set-Content -LiteralPath (Join-Path $sourceRoot 'BetterVirtualDesktop.runtimeconfig.json') -Value $runtimeConfig -Encoding UTF8
$settingsPath = Join-Path $fixtureRoot 'settings.json'
Set-Content -LiteralPath $settingsPath -Value '{"keep":"existing user preferences"}' -Encoding UTF8
$settingsBefore = [System.IO.File]::ReadAllText($settingsPath)

& $installer -PublishDirectory $sourceRoot -InstallDirectory $targetRoot
Assert (Test-Path -LiteralPath (Join-Path $targetRoot '.bettervirtualdesktop-install.json')) 'first installation creates an ownership marker'
Assert ((Get-Content -LiteralPath (Join-Path $targetRoot 'BetterVirtualDesktop.dll') -Raw).Trim() -eq 'fixture-version-one') 'first installation copies the complete payload'
Assert (Test-Path -LiteralPath (Join-Path $targetRoot 'licenses\Microsoft.NETCore.App-THIRD-PARTY-NOTICES.TXT')) 'runtime notices are installed'

Set-Content -LiteralPath (Join-Path $targetRoot 'obsolete.dll') -Value 'old distribution file'
Set-Content -LiteralPath (Join-Path $sourceRoot 'BetterVirtualDesktop.dll') -Value 'fixture-version-two'
& $installer -PublishDirectory $sourceRoot -InstallDirectory $targetRoot
Assert ((Get-Content -LiteralPath (Join-Path $targetRoot 'BetterVirtualDesktop.dll') -Raw).Trim() -eq 'fixture-version-two') 'updates reuse the same executable directory'
Assert (-not (Test-Path -LiteralPath (Join-Path $targetRoot 'obsolete.dll'))) 'updates remove obsolete distribution files'
Assert ([System.IO.File]::ReadAllText($settingsPath) -eq $settingsBefore) 'installation leaves adjacent user settings unchanged'
Assert (@(Get-ChildItem -LiteralPath (Split-Path -Parent $targetRoot) -Force | Where-Object Name -Like '.BetterVirtualDesktop.*').Count -eq 0) 'successful updates leave no staging or backup directories'

$installerTestState.Processes = @(Join-Path $targetRoot 'BetterVirtualDesktop.exe')
Assert-Rejected { & $installer -PublishDirectory $sourceRoot -InstallDirectory $targetRoot } 'still running' 'updates refuse to replace a running target'
$installerTestState.Processes = @(Join-Path $fixtureRoot 'other-version\BetterVirtualDesktop.exe')
& $installer -PublishDirectory $sourceRoot -InstallDirectory $targetRoot
Assert $true 'installation is permitted while an app at another path is running'
Assert-Rejected { & $installer -PublishDirectory $sourceRoot -InstallDirectory $targetRoot -Launch } 'still running' 'launch refuses another running BetterVirtualDesktop version'
$installerTestState.Processes = @(Join-Path $fixtureRoot 'legacy\IndependentVirtualDesktop.exe')
Assert-Rejected { & $installer -PublishDirectory $sourceRoot -InstallDirectory $targetRoot -Launch } 'still running' 'launch refuses a running IndependentVirtualDesktop version'
$installerTestState.Processes = @()

$unrelatedRoot = Join-Path $fixtureRoot 'Unrelated\BetterVirtualDesktop'
[void][System.IO.Directory]::CreateDirectory($unrelatedRoot)
Set-Content -LiteralPath (Join-Path $unrelatedRoot 'important.txt') -Value 'preserve this'
Assert-Rejected { & $installer -PublishDirectory $sourceRoot -InstallDirectory $unrelatedRoot } 'not managed by this installer' 'installer refuses an unrelated nonempty directory'
Assert ((Get-Content -LiteralPath (Join-Path $unrelatedRoot 'important.txt') -Raw).Trim() -eq 'preserve this') 'unrelated files remain intact'
Assert-Rejected { & $installer -PublishDirectory $sourceRoot -InstallDirectory $fixtureRoot } 'must be named' 'installer refuses a parent or arbitrary directory'
Assert-Rejected { & $installer -PublishDirectory $sourceRoot -InstallDirectory (Join-Path $sourceRoot 'BetterVirtualDesktop') } 'must be separate' 'installer refuses nested source and destination'

$notice = Join-Path $sourceRoot 'licenses\Microsoft.NETCore.App-LICENSE.TXT'
Remove-Item -LiteralPath $notice
Assert-Rejected { & $installer -PublishDirectory $sourceRoot -InstallDirectory $targetRoot } 'incomplete' 'incomplete distributions are rejected before changing the installation'
Set-Content -LiteralPath $notice -Value 'fixture notice'
Set-Content -LiteralPath (Join-Path $sourceRoot 'BetterVirtualDesktop.runtimeconfig.json') -Value '{"runtimeOptions":{"frameworks":[]}}'
Assert-Rejected { & $installer -PublishDirectory $sourceRoot -InstallDirectory $targetRoot } 'self-contained' 'framework-dependent distributions are rejected'
Set-Content -LiteralPath (Join-Path $sourceRoot 'BetterVirtualDesktop.runtimeconfig.json') -Value $runtimeConfig

Set-Content -LiteralPath (Join-Path $sourceRoot 'BetterVirtualDesktop.dll') -Value 'fixture-version-three'
$installerTestState.FailSwap = $true
Assert-Rejected { & $installer -PublishDirectory $sourceRoot -InstallDirectory $targetRoot } 'Injected destination rename failure' 'destination rename failure is surfaced'
$installerTestState.FailSwap = $false
Assert ((Get-Content -LiteralPath (Join-Path $targetRoot 'BetterVirtualDesktop.dll') -Raw).Trim() -eq 'fixture-version-two') 'failed update restores the previous installation'
Assert (@(Get-ChildItem -LiteralPath (Split-Path -Parent $targetRoot) -Force | Where-Object Name -Like '.BetterVirtualDesktop.*').Count -eq 0) 'failed update removes only its generated staging directory'
Assert ([System.IO.File]::ReadAllText($settingsPath) -eq $settingsBefore) 'failed updates preserve user settings'

Write-Host "$script:checks installer checks passed. Isolated fixture: $fixtureRoot"
