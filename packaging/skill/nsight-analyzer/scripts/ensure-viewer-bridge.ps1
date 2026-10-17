#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Workspace,
    [string] $ViewerPath,
    [switch] $CheckOnly
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$packageRoot = Split-Path -Parent $PSScriptRoot
$mutex = $null
$ownsMutex = $false
$stagePath = $null
$backupPath = $null
$installPath = $null
$previousHash = $null
$replaced = $false

function Fail([string] $Code, [string] $Message) {
    throw [InvalidOperationException]::new("${Code}: $Message")
}
function Hash([string] $Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
    return $null
}

try {
    if (-not [IO.Path]::IsPathFullyQualified($Workspace)) {
        Fail 'setup.workspace_not_absolute' 'Pass the same absolute workspace used for report operations.'
    }
    # Verify the entire package before trusting its DLL or compatibility table.
    $cli = Join-Path $packageRoot 'nsight-analyzer.exe'
    $version = ((& $cli version --workspace $Workspace) -join "`n") | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $version.result.isSuccess -or
        $version.result.value.packageState -ne 'verified') {
        Fail 'setup.package_invalid' 'Package verification failed; obtain a complete matching release.'
    }
    $manifest = Get-Content -LiteralPath (Join-Path $packageRoot 'PACKAGE.json') -Raw | ConvertFrom-Json
    foreach ($required in @('bridge/solidprobe.dll', 'bridge/viewer-host-targets.json')) {
        if (@($manifest.files | Where-Object path -CEQ $required).Count -ne 1) {
            Fail 'setup.package_incomplete' "Package manifest must include $required."
        }
    }
    $bridge = Join-Path $packageRoot 'bridge/solidprobe.dll'
    $expectedHash = Hash $bridge
    $targets = @(Get-Content -LiteralPath (Join-Path $packageRoot 'bridge/viewer-host-targets.json') -Raw | ConvertFrom-Json)
    if ([string]::IsNullOrWhiteSpace($ViewerPath)) {
        $ViewerPath = ($targets | Where-Object {
            Test-Path -LiteralPath $_.defaultViewerPath -PathType Leaf
        } | Select-Object -First 1).defaultViewerPath
    }
    if (-not $ViewerPath -or -not (Test-Path -LiteralPath $ViewerPath -PathType Leaf)) {
        Fail 'setup.viewer_missing' 'Install a supported NVIDIA Viewer or pass its exact executable with -ViewerPath.'
    }
    $ViewerPath = (Resolve-Path -LiteralPath $ViewerPath).Path
    $viewerDirectory = Split-Path -Parent $ViewerPath
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($ViewerPath)
    $qtPath = Join-Path $viewerDirectory 'Qt6Core.dll'
    if (-not (Test-Path -LiteralPath $qtPath -PathType Leaf)) {
        Fail 'setup.viewer_unsupported' "No Qt6Core.dll next to $ViewerPath."
    }
    $qt = [Diagnostics.FileVersionInfo]::GetVersionInfo($qtPath)
    $qtVersion = '{0}.{1}.{2}' -f $qt.FileMajorPart, $qt.FileMinorPart, $qt.FileBuildPart
    $viewerCandidates = @($targets | Where-Object {
        $_.fileProductVersion -ceq $fileVersion.ProductVersion -and $_.qtRuntimeVersion -ceq $qtVersion
    })
    if ($fileVersion.ProductName -cne 'Nsight Graphics' -or $viewerCandidates.Count -ne 1) {
        Fail 'setup.viewer_unsupported' "No unique supported Viewer/Qt entry for '$($fileVersion.ProductVersion)' / '$qtVersion'."
    }
    $target = $viewerCandidates[0]
    $installDirectory = Join-Path $viewerDirectory 'Plugins/generic'
    $installPath = Join-Path $installDirectory 'solidprobe.dll'
    $identity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($ViewerPath.ToUpperInvariant()))).Substring(0, 16)
    $mutex = [Threading.Mutex]::new($false, "Local\NSightAnalyzerBridge-$identity")
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { Fail 'setup.install_busy' 'Another installer is updating this Viewer; retry after it finishes.' }
    $previousHash = Hash $installPath
    $state = if ($previousHash -eq $expectedHash) { 'current' } elseif ($previousHash) { 'mismatch' } else { 'missing' }
    if ($state -ne 'current' -and -not $CheckOnly) {
        # Loaded DLLs cannot be repaired in place. Never close a user's Viewer.
        $running = @(Get-Process -Name ngfx-ui -ErrorAction SilentlyContinue | Where-Object {
            -not $_.Path -or [string]::Equals($_.Path, $ViewerPath, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($running.Count) {
            Fail 'setup.viewer_in_use' 'Close owned analyzer sessions first; the selected Viewer is still running. No files changed.'
        }
        New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
        $stagePath = Join-Path $installDirectory ('.solidprobe-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        [IO.File]::Copy($bridge, $stagePath, $false)
        if ((Hash $stagePath) -ne $expectedHash) { Fail 'setup.copy_invalid' 'Staged DLL hash does not match the package.' }
        if ($previousHash) {
            $backupDirectory = Join-Path $Workspace ".local/bridge-backups/$identity"
            New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
            $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
            $backupPath = Join-Path $backupDirectory "solidprobe.$stamp.$previousHash.dll"
            [IO.File]::Copy($installPath, $backupPath, $false)
            if ((Hash $backupPath) -ne $previousHash) { Fail 'setup.backup_invalid' 'Backup hash mismatch; installation stopped.' }
            if ((Hash $installPath) -ne $previousHash) { Fail 'setup.install_changed' 'Installed DLL changed during backup; installation stopped.' }
            [IO.File]::Replace($stagePath, $installPath, [NullString]::Value)
        }
        else {
            [IO.File]::Move($stagePath, $installPath)
        }
        $replaced = $true
        if ((Hash $installPath) -ne $expectedHash) { Fail 'setup.install_invalid' 'Installed DLL hash mismatch.' }
        $state = 'installed'
    }
    [ordered]@{
        isSuccess = $true; state = $state; viewerPath = $ViewerPath
        expectedViewerVersion = $target.productVersion; expectedViewerBuild = $target.productBuild
        bridgeVersion = $manifest.prerequisites.bridge; bridgePath = $installPath
        expectedSha256 = $expectedHash; installedSha256 = (Hash $installPath)
        backupPath = $backupPath; runtimeValidation = 'notChecked'
    } | ConvertTo-Json -Depth 4
}
catch {
    $message = $_.Exception.Message
    $code = if ($message -match '^(setup\.[a-z_]+):') { $Matches[1] } else { 'setup.install_failed' }
    $rollback = 'notNeeded'
    if ($replaced) {
        try {
            if ($backupPath) {
                [IO.File]::Copy($backupPath, $installPath, $true)
                if ((Hash $installPath) -ne $previousHash) { throw 'Restored DLL hash mismatch.' }
            }
            else { [IO.File]::Delete($installPath) }
            $rollback = 'restored'
        }
        catch { $rollback = 'failed: ' + $_.Exception.Message }
    }
    [ordered]@{ isSuccess = $false; error = @{ code = $code; message = $message }
        backupPath = $backupPath; rollback = $rollback } | ConvertTo-Json -Depth 4
    exit 1
}
finally {
    if ($stagePath -and [IO.File]::Exists($stagePath)) { [IO.File]::Delete($stagePath) }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
