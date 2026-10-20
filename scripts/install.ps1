[CmdletBinding()]
param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [string]$InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\DisplaySpaces'),
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-NoReparsePoints {
    param([string]$Path, [switch]$IncludeChildren)
    $current = [System.IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Installation paths must not contain symbolic links or junctions: $current"
            }
        }
        $parent = [System.IO.Directory]::GetParent($current)
        $current = if ($null -eq $parent) { $null } else { $parent.FullName }
    }
    if ($IncludeChildren -and (Test-Path -LiteralPath $Path -PathType Container)) {
        foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force -Recurse)) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Installation contents must not contain symbolic links or junctions: $($item.FullName)"
            }
        }
    }
}

function Assert-PublishedApplication {
    param([string]$Path)
    foreach ($name in @(
        'DisplaySpaces.exe', 'DisplaySpaces.dll', 'DisplaySpaces.deps.json',
        'DisplaySpaces.runtimeconfig.json', 'DisplaySpaces.Core.dll',
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Windows.Forms.dll',
        'licenses\Microsoft.NETCore.App-LICENSE.TXT',
        'licenses\Microsoft.NETCore.App-THIRD-PARTY-NOTICES.TXT',
        'licenses\Microsoft.WindowsDesktop.App-LICENSE.TXT'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $name) -PathType Leaf)) {
            throw "The self-contained publish folder is incomplete; missing $name in $Path. Run scripts\build.ps1 -Publish first."
        }
    }
    $configuration = Get-Content -LiteralPath (Join-Path $Path 'DisplaySpaces.runtimeconfig.json') -Raw | ConvertFrom-Json
    $options = $configuration.PSObject.Properties['runtimeOptions']
    if ($null -eq $options -or $null -eq $options.Value.PSObject.Properties['includedFrameworks']) {
        throw "Only a self-contained published application can be installed: $Path"
    }
    $frameworks = @($options.Value.includedFrameworks | ForEach-Object { $_.name })
    foreach ($required in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        if ($frameworks -notcontains $required) {
            throw "The publish folder does not include the required $required runtime."
        }
    }
}

function Assert-ApplicationStopped {
    param([string]$Executable, [switch]$BeforeLaunch)
    foreach ($process in @(Get-Process -Name 'DisplaySpaces', 'BetterVirtualDesktop', 'IndependentVirtualDesktop' -ErrorAction SilentlyContinue)) {
        try {
            $processPath = $process.Path
            if ($BeforeLaunch -or [string]::IsNullOrEmpty($processPath) -or
                [string]::Equals($processPath, $Executable, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Close DisplaySpaces and any older BetterVirtualDesktop or IndependentVirtualDesktop app using its tray menu (Exit and restore all windows), then retry. Process $($process.Id) is still running."
            }
        }
        finally {
            $process.Dispose()
        }
    }
}

function Remove-InstallerDirectory {
    param([string]$Path, [string]$Parent, [string]$ExpectedLeaf)
    # Every recursive removal is restricted to an exact, generated sibling.
    $absolute = [System.IO.Path]::GetFullPath($Path)
    $expected = [System.IO.Path]::GetFullPath((Join-Path $Parent $ExpectedLeaf))
    if (-not [string]::Equals($absolute, $expected, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([System.IO.Path]::GetDirectoryName($absolute), $Parent, [StringComparison]::OrdinalIgnoreCase) -or
        $ExpectedLeaf -notmatch '^\.DisplaySpaces\.(stage|backup)-[0-9a-f]{32}$') {
        throw "Refusing to remove an unexpected installer path: $absolute"
    }
    if (Test-Path -LiteralPath $absolute) {
        Assert-NoReparsePoints -Path $absolute -IncludeChildren
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}

$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).ProviderPath.TrimEnd('\', '/')
$installRoot = [System.IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\', '/')
if ([System.IO.Path]::GetFileName($installRoot) -ne 'DisplaySpaces') {
    throw 'The installation directory must be named DisplaySpaces. Use one stable directory for every update.'
}
$installParent = [System.IO.Path]::GetDirectoryName($installRoot)
if ([string]::IsNullOrEmpty($installParent) -or
    [string]::Equals($installRoot, $publishRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $publishRoot.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $installRoot.StartsWith($publishRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish and installation directories must be separate and must not contain each other.'
}
Assert-NoReparsePoints -Path $publishRoot -IncludeChildren
Assert-NoReparsePoints -Path $installRoot -IncludeChildren
Assert-PublishedApplication -Path $publishRoot

# A marker prevents updates from replacing an unrelated existing directory.
$markerName = '.displayspaces-install.json'
if (Test-Path -LiteralPath $installRoot) {
    if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) {
        throw "The installation path is not a directory: $installRoot"
    }
    if (@(Get-ChildItem -LiteralPath $installRoot -Force).Count -gt 0) {
        $markerPath = Join-Path $installRoot $markerName
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw "The existing directory is not managed by this installer: $installRoot"
        }
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        if ($null -eq $marker.PSObject.Properties['product'] -or $marker.product -ne 'DisplaySpaces' -or
            $null -eq $marker.PSObject.Properties['schemaVersion'] -or $marker.schemaVersion -ne 1) {
            throw "The existing directory has an unrecognized installation marker: $installRoot"
        }
        Assert-PublishedApplication -Path $installRoot
    }
}

$installedExecutable = Join-Path $installRoot 'DisplaySpaces.exe'
Assert-ApplicationStopped -Executable $installedExecutable -BeforeLaunch:$Launch
$installationLock = [System.Threading.Mutex]::new($false, 'Local\DisplaySpaces.Install')
$lockHeld = $false
$stageLeaf = '.DisplaySpaces.stage-' + [guid]::NewGuid().ToString('N')
$backupLeaf = '.DisplaySpaces.backup-' + [guid]::NewGuid().ToString('N')
$stagePath = Join-Path $installParent $stageLeaf
$backupPath = Join-Path $installParent $backupLeaf
$previousMoved = $false
$installed = $false
try {
    try { $lockHeld = $installationLock.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $lockHeld = $true }
    if (-not $lockHeld) { throw 'Another DisplaySpaces installation is already in progress.' }
    [void][System.IO.Directory]::CreateDirectory($installParent)
    [void][System.IO.Directory]::CreateDirectory($stagePath)
    foreach ($item in @(Get-ChildItem -LiteralPath $publishRoot -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $stagePath -Recurse -Force
    }
    Assert-PublishedApplication -Path $stagePath
    @{ product = 'DisplaySpaces'; schemaVersion = 1 } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stagePath $markerName) -Encoding UTF8

    # Recheck immediately before swapping. Never terminate a process that may own hidden windows.
    Assert-ApplicationStopped -Executable $installedExecutable -BeforeLaunch:$Launch
    Assert-NoReparsePoints -Path $installRoot -IncludeChildren
    if (Test-Path -LiteralPath $installRoot) {
        Move-Item -LiteralPath $installRoot -Destination $backupPath
        $previousMoved = $true
    }
    try {
        Move-Item -LiteralPath $stagePath -Destination $installRoot
        $installed = $true
    }
    catch {
        if ($previousMoved -and -not (Test-Path -LiteralPath $installRoot)) {
            Move-Item -LiteralPath $backupPath -Destination $installRoot
            $previousMoved = $false
        }
        throw
    }
    if ($previousMoved) {
        try { Remove-InstallerDirectory -Path $backupPath -Parent $installParent -ExpectedLeaf $backupLeaf }
        catch { Write-Warning "The update is installed, but its previous files remain at $backupPath. $($_.Exception.Message)" }
    }
}
finally {
    try {
        if (-not $installed -and (Test-Path -LiteralPath $stagePath)) {
            Remove-InstallerDirectory -Path $stagePath -Parent $installParent -ExpectedLeaf $stageLeaf
        }
    }
    finally {
        if ($lockHeld) { $installationLock.ReleaseMutex() }
        $installationLock.Dispose()
    }
}

Write-Host "Installed application: $installedExecutable"
Write-Host 'Use this same location for every update to avoid duplicate Windows system tray history entries.'
Write-Host 'If Windows startup was enabled from another folder, enable it again from the installed app tray menu.'
if ($Launch) {
    Assert-ApplicationStopped -Executable $installedExecutable -BeforeLaunch
    Start-Process -FilePath $installedExecutable -ArgumentList '--startup' -WorkingDirectory $installRoot -WindowStyle Hidden
}
