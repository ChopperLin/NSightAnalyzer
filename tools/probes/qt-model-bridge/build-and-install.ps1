<#
.SYNOPSIS
Builds the version-pinned SolidProbe Viewer plugin and installs it into the
matching Nsight Graphics installation.

.DESCRIPTION
The adapter loads this plugin into an explicitly verified Nsight Graphics
Viewer build and reads the decoded Qt item models. Installing writes into the Nsight
installation directory, so the previously installed DLL is always backed up
under .local/bridge-backups first and can be restored with -Rollback.

The script refuses to install a plugin whose reported version does not match
the version the product adapter expects, because a mismatch is only detectable
at runtime otherwise.

.EXAMPLE
pwsh -File tools/probes/qt-model-bridge/build-and-install.ps1 -WhatIf
Builds and reports what would be installed without touching Nsight.

.EXAMPLE
pwsh -File tools/probes/qt-model-bridge/build-and-install.ps1 -Rollback
Restores the most recent backup.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Qt6Dir,

    [string] $ViewerPath,

    [string] $TargetProductVersion,

    [ValidateSet('Release', 'Debug', 'RelWithDebInfo')]
    [string] $Configuration = 'Release',

    [switch] $SkipInstall,

    [switch] $Rollback
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$sourceDirectory = $PSScriptRoot
$buildDirectory = Join-Path $repositoryRoot '.local\build\qt-model-bridge'
$backupRoot = Join-Path $repositoryRoot '.local\bridge-backups'
$pluginSource = Join-Path $sourceDirectory 'solid_probe_plugin.cpp'
$expectedVersionSource = Join-Path `
    $repositoryRoot 'src\NsightAnalyzer\Adapters\NsightViewer2026_2\ViewerProbeRunner.cs'
$viewerHostTable = Join-Path `
    $repositoryRoot 'src\NsightAnalyzer\Adapters\NsightViewer2026_2\viewer-host-targets.json'
$viewerHostTargets = @(Get-Content -LiteralPath $viewerHostTable -Raw | ConvertFrom-Json)
if ($viewerHostTargets.Count -eq 0) {
    throw 'The Viewer host target table is empty.'
}

function Get-PluginVersion {
    # constexpr auto kPluginVersion = "probe-0.45";
    $match = Select-String -LiteralPath $pluginSource `
        -Pattern 'kPluginVersion\s*=\s*"([^"]+)"' | Select-Object -First 1
    if (-not $match) {
        throw 'Could not read kPluginVersion from the plugin source.'
    }
    return $match.Matches[0].Groups[1].Value
}

function Get-ExpectedBridgeVersion {
    # public const string ExpectedBridgeVersion = "probe-0.45";
    $match = Select-String -LiteralPath $expectedVersionSource `
        -Pattern 'ExpectedBridgeVersion\s*=\s*"([^"]+)"' | Select-Object -First 1
    if (-not $match) {
        throw 'Could not read ExpectedBridgeVersion from the adapter source.'
    }
    return $match.Matches[0].Groups[1].Value
}

function Resolve-ViewerTarget {
    $targets = $viewerHostTargets
    $discoveryTargets = @($targets | Where-Object {
            [string]::IsNullOrWhiteSpace($TargetProductVersion) -or
            $_.productVersion -ceq $TargetProductVersion
        })
    if ($discoveryTargets.Count -eq 0) {
        throw "No verified compatibility entry has product version '$TargetProductVersion'."
    }
    $candidate = $ViewerPath
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = ($discoveryTargets | Where-Object {
                Test-Path -LiteralPath $_.defaultViewerPath -PathType Leaf
            } | Select-Object -First 1).defaultViewerPath
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            $candidate = $discoveryTargets[0].defaultViewerPath
        }
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "No explicitly supported Viewer was found at '$candidate'."
    }
    $resolved = (Resolve-Path -LiteralPath $candidate).Path
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($resolved)
    $candidates = @($targets | Where-Object {
        $_.fileProductVersion -ceq $version.ProductVersion
    })
    if (-not [string]::IsNullOrWhiteSpace($TargetProductVersion)) {
        $candidates = @($candidates | Where-Object {
                $_.productVersion -ceq $TargetProductVersion
            })
    }
    if ($candidates.Count -eq 0) {
        throw "Viewer product version '$($version.ProductVersion)' has no verified compatibility entry."
    }
    $viewerDirectory = Split-Path -Parent $resolved
    $qtPath = Join-Path $viewerDirectory 'Qt6Core.dll'
    if (-not (Test-Path -LiteralPath $qtPath -PathType Leaf)) {
        throw "The selected Viewer has no Qt6Core.dll at '$qtPath'."
    }
    $qt = [Diagnostics.FileVersionInfo]::GetVersionInfo($qtPath)
    $observedQt = '{0}.{1}.{2}' -f $qt.FileMajorPart, $qt.FileMinorPart, $qt.FileBuildPart
    $candidates = @($candidates | Where-Object { $_.qtRuntimeVersion -ceq $observedQt })
    if ($candidates.Count -eq 0) {
        throw "Viewer Qt '$observedQt' has no verified compatibility entry for this product version."
    }
    $pathMatches = @($candidates | Where-Object {
            [string]::Equals([IO.Path]::GetFullPath($_.defaultViewerPath), $resolved,
                [StringComparison]::OrdinalIgnoreCase)
        })
    $target = if ($pathMatches.Count -eq 1) {
        $pathMatches[0]
    }
    elseif ($candidates.Count -eq 1) {
        $candidates[0]
    }
    else {
        $null
    }
    if (-not $target) {
        $versions = ($candidates | ForEach-Object productVersion) -join ', '
        throw ("Viewer target is ambiguous after file-version and Qt checks. " +
            "Pass -TargetProductVersion with one of: $versions")
    }
    return [pscustomobject]@{
        viewerPath = $resolved
        viewerDirectory = $viewerDirectory
        fileProductVersion = $target.fileProductVersion
        productVersion = $target.productVersion
        build = $target.productBuild
        qtRuntime = $target.qtRuntimeVersion
        compatibilityProfile = $target.compatibilityProfile
    }
}

function Resolve-InstallPath {
    $target = Resolve-ViewerTarget
    return Join-Path $target.viewerDirectory 'plugins\generic\solidprobe.dll'
}

function Resolve-BackupDirectory {
    $target = Resolve-ViewerTarget
    $material = $target.viewerPath.ToUpperInvariant()
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($material))).Substring(0, 16)
    return Join-Path $backupRoot ("viewer-{0}-{1}" -f $target.fileProductVersion.Replace('.', '_'), $hash)
}

function Invoke-Rollback {
    $installPath = Resolve-InstallPath
    $backupDirectory = Resolve-BackupDirectory
    if (-not (Test-Path -LiteralPath $backupDirectory)) {
        throw "No backup directory at '$backupDirectory'."
    }
    $backup = Get-ChildItem -LiteralPath $backupDirectory -Filter 'solidprobe.*.dll' |
        # Copy-Item preserves the source DLL's LastWriteTime and the version
        # segment does not sort chronologically. Extract the timestamp suffix.
        Sort-Object {
            if ($_.Name -match '\.(\d{8}-\d{6})\.dll$') {
                return $Matches[1]
            }
            return ''
        } -Descending | Select-Object -First 1
    if (-not $backup) {
        throw "No solidprobe backup found under '$backupDirectory'."
    }
    if ($PSCmdlet.ShouldProcess($installPath, "restore $($backup.Name)")) {
        Copy-Item -LiteralPath $backup.FullName -Destination $installPath -Force
        Write-Host "Restored $($backup.Name) -> $installPath"
    }
}

function Find-CMake {
    $command = Get-Command cmake -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    # Visual Studio ships CMake but does not put it on PATH.
    $candidates = @(
        'Community', 'Professional', 'Enterprise', 'BuildTools'
    ) | ForEach-Object {
        Join-Path ${env:ProgramFiles} ("Microsoft Visual Studio\2022\$_" +
            '\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe')
        Join-Path ${env:ProgramFiles(x86)} ("Microsoft Visual Studio\2022\$_" +
            '\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe')
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'cmake was not found on PATH or in a Visual Studio 2022 installation.'
}

function Resolve-Qt6Dir {
    if ($Qt6Dir) {
        if (-not (Test-Path -LiteralPath $Qt6Dir -PathType Container)) {
            throw "The supplied -Qt6Dir '$Qt6Dir' does not exist."
        }
        return (Resolve-Path -LiteralPath $Qt6Dir).Path
    }
    # The bridge is pinned to the Qt version the Viewer ships (6.8.1).
    $local = Join-Path $repositoryRoot '.local\Qt\6.8.1\msvc2022_64\lib\cmake\Qt6'
    if (Test-Path -LiteralPath $local -PathType Container) {
        return (Resolve-Path -LiteralPath $local).Path
    }
    $cached = Join-Path $buildDirectory 'CMakeCache.txt'
    if (Test-Path -LiteralPath $cached -PathType Leaf) {
        $match = Select-String -LiteralPath $cached -Pattern '^Qt6_DIR:PATH=(.+)$' |
            Select-Object -First 1
        if ($match) { return $match.Matches[0].Groups[1].Value }
    }
    throw 'Qt 6.8.1 was not found. Pass -Qt6Dir <path to lib/cmake/Qt6>.'
}

if ($Rollback) {
    Invoke-Rollback
    return
}

$pluginVersion = Get-PluginVersion
$expectedVersion = Get-ExpectedBridgeVersion
if ($pluginVersion -ne $expectedVersion) {
    throw ("Plugin version '$pluginVersion' does not match the adapter's " +
        "ExpectedBridgeVersion '$expectedVersion'. Update both together.")
}
Write-Host "Bridge version: $pluginVersion"

$cmake = Find-CMake
$qt6 = Resolve-Qt6Dir
Write-Host "cmake:  $cmake"
Write-Host "Qt6Dir: $qt6"

$configureArguments = @(
    '-S', $sourceDirectory,
    '-B', $buildDirectory,
    '-G', 'Visual Studio 17 2022',
    "-DQt6_DIR=$qt6"
)
# Re-configuring an existing cache with a different generator platform is a
# hard CMake error, so only pass -A when starting from a clean build tree.
if (-not (Test-Path -LiteralPath (Join-Path $buildDirectory 'CMakeCache.txt'))) {
    $configureArguments += @('-A', 'x64')
}

& $cmake @configureArguments
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed ($LASTEXITCODE)." }

& $cmake --build $buildDirectory --config $Configuration --target solidprobe
if ($LASTEXITCODE -ne 0) { throw "cmake build failed ($LASTEXITCODE)." }

$built = Join-Path $buildDirectory 'out\generic\solidprobe.dll'
if (-not (Test-Path -LiteralPath $built -PathType Leaf)) {
    throw "The build did not produce '$built'."
}
$builtInfo = Get-Item -LiteralPath $built
Write-Host ("Built:  {0} ({1:N0} bytes)" -f $built, $builtInfo.Length)

if ($SkipInstall) {
    Write-Host 'Skipping install (-SkipInstall).'
    return
}

$installPath = Resolve-InstallPath
$viewerTarget = Resolve-ViewerTarget
$backupDirectory = Resolve-BackupDirectory
$installDirectory = Split-Path -Parent $installPath
if (-not (Test-Path -LiteralPath $installDirectory -PathType Container)) {
    throw "The Viewer plugin directory '$installDirectory' does not exist."
}

# Installing writes under Program Files. Fail with an actionable message
# instead of a raw access-denied part way through. The probe file is created
# and deleted through the .NET API so that it is never left behind by -WhatIf,
# which would suppress a Remove-Item but not the write itself.
$probeFile = Join-Path $installDirectory ('.write-probe-' + [Guid]::NewGuid().ToString('N'))
try {
    [System.IO.File]::WriteAllText($probeFile, 'probe')
}
catch {
    throw ("Cannot write to '$installDirectory'. Re-run this script from an " +
        'elevated PowerShell session.')
}
finally {
    if ([System.IO.File]::Exists($probeFile)) {
        [System.IO.File]::Delete($probeFile)
    }
}

if (Test-Path -LiteralPath $installPath -PathType Leaf) {
    New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupPath = Join-Path $backupDirectory "solidprobe.$pluginVersion.$stamp.dll"
    if ($PSCmdlet.ShouldProcess($installPath, "back up to $backupPath")) {
        Copy-Item -LiteralPath $installPath -Destination $backupPath -Force
        Write-Host "Backup: $backupPath"
    }
}

if ($PSCmdlet.ShouldProcess($installPath, 'install solidprobe.dll')) {
    Copy-Item -LiteralPath $built -Destination $installPath -Force
    $installedHash = (Get-FileHash -LiteralPath $installPath -Algorithm SHA256).Hash
    $builtHash = (Get-FileHash -LiteralPath $built -Algorithm SHA256).Hash
    if ($installedHash -ne $builtHash) {
        throw 'The installed plugin does not match the build output.'
    }
    Write-Host "Installed: $installPath"
    Write-Host ("Viewer:    {0} build {1}; Qt runtime {2}" -f `
        $viewerTarget.productVersion, $viewerTarget.build, $viewerTarget.qtRuntime)
    Write-Host "SHA-256:   $installedHash"
    Write-Host ''
    Write-Host 'Verify with: dotnet test; pwsh tools/verification/verify-cli.ps1'
}
