#Requires -Version 7.0
param(
    [string] $PackagePath = (Join-Path $PSScriptRoot '../../dist/nsight-analyzer'),
    [string] $ViewerPath
)
$ErrorActionPreference = 'Stop'
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$testRoot = Join-Path $repoRoot ('.local/verification/bridge-' + [Guid]::NewGuid().ToString('N'))
$workspace = Join-Path $testRoot 'task'
$fixture = Join-Path $testRoot 'viewer'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$targets = @(Get-Content -LiteralPath (Join-Path $PackagePath 'bridge/viewer-host-targets.json') -Raw | ConvertFrom-Json)
if (-not $ViewerPath) {
    $ViewerPath = ($targets | Where-Object { Test-Path -LiteralPath $_.defaultViewerPath } | Select-Object -First 1).defaultViewerPath
}
if (-not $ViewerPath) { throw 'A supported Viewer is required for file-metadata fixtures.' }
# Only copy metadata-bearing binaries into an isolated fixture. Never start it or
# alter the real NVIDIA installation. Fixture artifacts are retained for inspection.
Copy-Item -LiteralPath $ViewerPath -Destination (Join-Path $fixture 'ngfx-ui.exe')
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $ViewerPath) 'Qt6Core.dll') -Destination $fixture
$fixtureViewer = Join-Path $fixture 'ngfx-ui.exe'
$installed = Join-Path $fixture 'Plugins/generic/solidprobe.dll'
$helper = Join-Path $PackagePath 'scripts/ensure-viewer-bridge.ps1'
$dll = Join-Path $PackagePath 'bridge/solidprobe.dll'
$expectedHash = (Get-FileHash -LiteralPath $dll).Hash

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Verification failed: $Message" }
}
function Invoke-Helper([string[]] $Extra = @(), [bool] $Success = $true, [string] $HelperPath = $helper) {
    $raw = & pwsh -NoProfile -File $HelperPath -Workspace $workspace -ViewerPath $fixtureViewer @Extra
    $exitCode = $LASTEXITCODE
    $result = ($raw -join "`n") | ConvertFrom-Json
    Assert (($exitCode -eq 0) -eq $Success) "exit $exitCode; $raw"
    Assert ($result.isSuccess -eq $Success) "success flag; $raw"
    return $result
}

$check = Invoke-Helper -Extra @('-CheckOnly')
Assert ($check.state -eq 'missing' -and -not (Test-Path -LiteralPath $installed)) 'check-only must not install'
$fresh = Invoke-Helper
Assert ($fresh.state -eq 'installed' -and -not $fresh.backupPath) 'fresh install'
Assert ((Get-FileHash -LiteralPath $installed).Hash -eq $expectedHash) 'fresh install hash'
$written = (Get-Item -LiteralPath $installed).LastWriteTimeUtc
$current = Invoke-Helper
Assert ($current.state -eq 'current' -and (Get-Item -LiteralPath $installed).LastWriteTimeUtc -eq $written) 'matching install must be a no-op'

[IO.File]::WriteAllBytes($installed, [byte[]](1, 2, 3, 4))
$oldHash = (Get-FileHash -LiteralPath $installed).Hash
$check = Invoke-Helper -Extra @('-CheckOnly')
Assert ($check.state -eq 'mismatch' -and (Get-FileHash -LiteralPath $installed).Hash -eq $oldHash) 'check-only must preserve old DLL'
$repair = Invoke-Helper
Assert ($repair.state -eq 'installed' -and (Get-FileHash -LiteralPath $repair.backupPath).Hash -eq $oldHash) 'repair must preserve exact backup'
Assert ((Get-FileHash -LiteralPath $installed).Hash -eq $expectedHash) 'repair hash'

[IO.File]::WriteAllBytes($installed, [byte[]](5, 6, 7, 8))
$lockedHash = (Get-FileHash -LiteralPath $installed).Hash
$lock = [IO.File]::Open($installed, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $failed = Invoke-Helper -Success $false
    Assert ($failed.error.code -eq 'setup.install_failed') 'locked destination must fail'
    Assert ((Get-FileHash -LiteralPath $installed).Hash -eq $lockedHash) 'failed replacement must preserve old DLL'
}
finally { $lock.Dispose() }
Assert (@(Get-ChildItem -LiteralPath (Split-Path -Parent $installed) -Filter '*.tmp').Count -eq 0) 'failed replacement must clean staging file'

# A tampered release must be rejected before any destination changes.
$badPackage = Join-Path $testRoot 'tampered-package'
Copy-Item -LiteralPath $PackagePath -Destination $badPackage -Recurse
[IO.File]::AppendAllText((Join-Path $badPackage 'bridge/solidprobe.dll'), 'tampered')
$bad = Invoke-Helper -Success $false -HelperPath (Join-Path $badPackage 'scripts/ensure-viewer-bridge.ps1')
Assert ($bad.error.code -eq 'setup.package_invalid') 'tampered bundle must fail verification'
Assert ((Get-FileHash -LiteralPath $installed).Hash -eq $lockedHash) 'tampered bundle must not modify destination'

# Exact host metadata is required even when the requested path is named ngfx-ui.exe.
$unsupported = Join-Path $testRoot 'unsupported'
New-Item -ItemType Directory -Path $unsupported | Out-Null
Copy-Item -LiteralPath (Join-Path $PSHOME 'pwsh.exe') -Destination (Join-Path $unsupported 'ngfx-ui.exe')
Copy-Item -LiteralPath (Join-Path $fixture 'Qt6Core.dll') -Destination $unsupported
$fixtureViewer = Join-Path $unsupported 'ngfx-ui.exe'
$badViewer = Invoke-Helper -Success $false
Assert ($badViewer.error.code -eq 'setup.viewer_unsupported') 'unsupported Viewer must fail'
Assert (-not (Test-Path -LiteralPath (Join-Path $unsupported 'Plugins'))) 'unsupported Viewer must remain untouched'

[pscustomobject]@{ status = 'passed'; cases = 7; artifacts = $testRoot } | ConvertTo-Json
