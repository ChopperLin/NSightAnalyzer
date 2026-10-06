param(
    [Parameter(Mandatory)][string]$SingleFrameTrace,
    [string]$ViewerPath,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$CliPath,
    [switch]$CacheOnly
)

# Closure for the frozen single-frame oracle documented in docs/testing.md.
# All generated evidence is isolated; the caller's report is checked but never rewritten.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$verifyRepo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$verifyCli = if ($CliPath) { (Resolve-Path -LiteralPath $CliPath).Path } else {
    Join-Path $verifyRepo "src\NsightAnalyzer\bin\$Configuration\net9.0-windows\nsight-analyzer.exe"
}
$verifyTrace = (Resolve-Path -LiteralPath $SingleFrameTrace).Path
if (-not (Test-Path -LiteralPath $verifyCli -PathType Leaf)) { throw 'Build the CLI before verification.' }
$verifyWorkspace = Join-Path $verifyRepo ('.local\verification\agent-v21-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $verifyWorkspace -Force | Out-Null
$verifyInitialHash = (Get-FileHash -LiteralPath $verifyTrace -Algorithm SHA256).Hash
$verifyStats = [Collections.Generic.List[object]]::new()
$verifyRunsRoot = Join-Path $verifyWorkspace '.local\runs'
$verifySessionsRoot = Join-Path $verifyWorkspace '.local\sessions'
$script:verifyLastRunDirectories = @()
$script:verifyLastResult = $null
$script:verifyExpectedBuild = $null
$script:verifyExpectedQt = $null
$script:verifyExpectedMetricCount = $null
function Read-CheckedJson {
    param([string]$Path,[long]$MaximumBytes=1048576)
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.Length -le 0 -or $item.Length -gt $MaximumBytes) { throw "Invalid JSON artifact size: $Path" }
    return Get-Content -LiteralPath $item.FullName -Raw | ConvertFrom-Json -Depth 100
}
function Get-RunDirectories {
    if (Test-Path -LiteralPath $verifyRunsRoot -PathType Container) {
        Get-ChildItem -LiteralPath $verifyRunsRoot -Directory | ForEach-Object FullName
    }
}
function Get-OwnerRecords {
    if (-not (Test-Path -LiteralPath $verifySessionsRoot -PathType Container)) { return }
    foreach ($file in Get-ChildItem -LiteralPath $verifySessionsRoot -Recurse -Filter owner.json -File) {
        $owner = Read-CheckedJson $file.FullName
        if ($owner.schema -ne 'NsightAnalyzerViewerSessionOwnerV1' -or $owner.pid -le 0 -or
            $owner.processStartUtcTicks -le 0 -or -not [IO.Path]::IsPathFullyQualified($owner.viewerPath)) {
            throw "Invalid session owner: $($file.FullName)"
        }
        [pscustomobject]@{owner=$owner;directory=$file.DirectoryName}
    }
}
function Run-Check {
    param([string]$Name,[string[]]$Arguments,[int]$ExpectedExit=0)
    if ($ViewerPath -and $Arguments[0] -notin @('version', 'capabilities')) {
        $Arguments += @('--viewer', (Resolve-Path -LiteralPath $ViewerPath).Path)
    }
    $beforeRuns = @(Get-RunDirectories)
    $verifyTimer = [Diagnostics.Stopwatch]::StartNew()
    $verifyRaw = (& $verifyCli @Arguments --workspace $verifyWorkspace | Out-String).Trim()
    $verifyCode = $LASTEXITCODE
    $script:verifyLastRunDirectories = @(Get-RunDirectories | Where-Object { $_ -notin $beforeRuns })
    [IO.File]::WriteAllText((Join-Path $verifyWorkspace ($Name + '.json')),$verifyRaw,[Text.UTF8Encoding]::new($false))
    $verifyJson = $verifyRaw | ConvertFrom-Json -Depth 100
    $script:verifyLastResult = $verifyJson
    $entry = [pscustomobject]@{name=$Name;ms=$verifyTimer.ElapsedMilliseconds;bytes=[Text.Encoding]::UTF8.GetByteCount($verifyRaw);exit=$verifyCode;success=$verifyJson.result.isSuccess;error=$verifyJson.result.error.code}
    $verifyStats.Add($entry)
    $entry | ConvertTo-Json -Compress | Write-Host
    if ($verifyCode -ne $ExpectedExit) {throw "Unexpected exit for $Name"}
    if ($verifyJson.operation -ne $Arguments[0] -or $verifyJson.schemaVersion.major -ne 2 -or
        $verifyJson.schemaVersion.minor -lt 1 -or $verifyJson.result.isSuccess -ne ($ExpectedExit -eq 0)) {
        throw "Invalid result envelope for $Name"
    }
    return $verifyJson
}
function Latest-MetricReceipt {
    $items = @(foreach ($directory in $script:verifyLastRunDirectories) {
        Get-ChildItem -LiteralPath $directory -Filter metric-snapshot.json -File
    })
    if ($items.Count -ne 1) {throw "The last call must create exactly one metric receipt; observed $($items.Count)"}
    $receipt = Read-CheckedJson $items[0].FullName
    $bridge = Read-CheckedJson (Join-Path $items[0].DirectoryName 'bridge-output.json') 134217728
    $scope = $script:verifyLastResult.result.value.scope
    if ($receipt.schema -ne 'NsightAnalyzerRangeMetricSnapshotV1' -or $receipt.hit -isnot [bool] -or
        $receipt.validation -ne 'freshMetricCatalogAndExactLiveSession' -or
        $bridge.schema -ne 'NsightSolidProbeSelectionMetricsV1' -or
        $bridge.requestId -notmatch '^[0-9a-f]{32}$' -or $bridge.status -ne 'complete' -or
        -not $bridge.metricsStable -or -not $bridge.selectionMatchesTarget -or $bridge.settleTimedOut -or
        -not $bridge.currentSelection.valid -or -not $bridge.targetSelection.valid -or
        $bridge.eventSelector.kind -ne 'ordinal' -or $bridge.eventSelector.value -ne $scope.preorderOrdinal -or
        ($bridge.currentSelection.path -join '.') -ne ($bridge.targetSelection.path -join '.') -or
        ($bridge.currentSelection.path -join '.') -ne ($scope.treePath -join '.') -or
        $bridge.currentSelection.cells[0] -ne $scope.description -or $bridge.currentSelection.cells[1] -ne $scope.eventRange) {
        throw 'The new metric receipt does not close against its adjacent bridge selection and result scope'
    }
    $matchingOwners = @(Get-OwnerRecords | Where-Object { $_.owner.pid -eq $bridge.pid -and $_.owner.reportId -eq $bridge.reportId })
    if ($matchingOwners.Count -ne 1) {throw 'Metric receipt does not identify one exact session owner'}
    $session = Read-CheckedJson (Join-Path $matchingOwners[0].directory 'session.json')
    if ($session.schema -ne 'NsightSolidProbeSessionV1' -or $session.status -ne 'ready' -or
        $session.pid -ne $bridge.pid -or $session.reportId -ne $bridge.reportId -or
        $session.sessionId -ne $matchingOwners[0].owner.sessionId -or $session.lastRequestId -ne $bridge.requestId -or
        $bridge.pluginVersion -ne 'probe-0.52' -or $bridge.qtCompileVersion -ne '6.8.1' -or
        $bridge.qtRuntimeVersion -ne $script:verifyExpectedQt -or
        $session.qtCompileVersion -ne '6.8.1' -or
        $bridge.verifiedHostTarget.nsightBuild -ne $script:verifyExpectedBuild) {
        throw 'Metric receipt request identity, decoder or completed session does not match'
    }
    return $receipt
}
function Get-OwnedProcessSnapshot {
    param([object[]]$Owners)
    $viewers = [Collections.Generic.List[object]]::new()
    $reporters = [Collections.Generic.List[object]]::new()
    if ($Owners.Count -gt 0) {
        $filter = "Name = 'CrashReporter.exe'" + (($Owners | ForEach-Object { ' OR ProcessId = ' + [int]$_.owner.pid }) -join '')
        $processes = @(Get-CimInstance -ClassName Win32_Process -Filter $filter -OperationTimeoutSec 10)
        $snapshotTicks = [DateTime]::UtcNow.Ticks
        foreach ($record in $Owners) {
            $owner = $record.owner
            $candidate = $processes | Where-Object ProcessId -eq $owner.pid | Select-Object -First 1
            $viewerMatches = $false
            $upperTicks = $snapshotTicks
            if ($null -ne $candidate) {
                $candidateTicks = $candidate.CreationDate.ToUniversalTime().Ticks
                # Never gate the exact identity check on CIM's timestamp precision.
                $process = $null
                try {
                    $process = [Diagnostics.Process]::GetProcessById([int]$owner.pid)
                    $candidateTicks = $process.StartTime.ToUniversalTime().Ticks
                    $viewerMatches = $candidateTicks -eq [long]$owner.processStartUtcTicks -and
                        $process.MainModule.FileName -eq $owner.viewerPath
                }
                catch [ArgumentException] { }
                catch [InvalidOperationException] { }
                finally { if ($null -ne $process) { $process.Dispose() } }
                if ($viewerMatches) { $viewers.Add($candidate) }
                elseif ($candidateTicks -gt [long]$owner.processStartUtcTicks) {
                    # A reused parent PID cannot claim children born to its newer process instance.
                    $upperTicks = $candidateTicks
                }
            }
            $crashPath = Join-Path (Split-Path -Parent $owner.viewerPath) 'CrashReporter.exe'
            foreach ($child in $processes | Where-Object { $_.ParentProcessId -eq $owner.pid -and $_.ExecutablePath -eq $crashPath }) {
                $childTicks = $child.CreationDate.ToUniversalTime().Ticks
                if ($childTicks -lt [long]$owner.processStartUtcTicks -or $childTicks -ge $upperTicks) { continue }
                $reporters.Add([pscustomobject]@{
                    pid=[int]$child.ProcessId;parentPid=[int]$child.ParentProcessId;path=$child.ExecutablePath
                    creationUtcTicks=$childTicks;baseline=([int]$child.ProcessId -in @($owner.baselineCrashReporterPids))
                })
            }
        }
    }
    return [pscustomobject]@{viewers=@($viewers.ToArray());reporters=@($reporters.ToArray())}
}
function Assert-ClosedSession {
    param([string]$Name)
    $owners = @(Get-OwnerRecords)
    $before = $null
    $inspectionError = $null
    try { $before = Get-OwnedProcessSnapshot $owners }
    catch { $inspectionError = $_ }
    $closeError = $null
    try { $closed = Run-Check $Name @('viewer-session.close',$verifyTrace) }
    catch { $closeError = $_ }
    $after = Get-OwnedProcessSnapshot $owners
    if ($after.viewers.Count -gt 0 -or $after.reporters.Count -gt 0) {
        throw 'The exact owned Viewer or its CrashReporter children (including new/replacement children) survived close'
    }
    if ($null -ne $inspectionError) { throw $inspectionError }
    if (@($before.reporters | Where-Object { -not $_.baseline }).Count -gt 0) {
        throw 'An additional/replacement CrashReporter was observed before close'
    }
    if ($null -ne $closeError) { throw $closeError }
}
try {
    $version = Run-Check 'version' @('version')
    $doctor = Run-Check 'doctor' @('doctor')
    $runtimeTarget = $doctor.result.value.checks |
        Where-Object component -eq 'viewerRuntime' | Select-Object -First 1
    if ($runtimeTarget.expected -notmatch 'build=(?<build>\d+).*qt=(?<qt>\d+\.\d+\.\d+)') {
        throw 'Doctor did not identify one exact Viewer runtime target'
    }
    $script:verifyExpectedBuild = $Matches.build
    $script:verifyExpectedQt = $Matches.qt
    $script:verifyExpectedMetricCount = if ($Matches.build -eq '38722833') { 3237 } else { 801 }
    $timing = Run-Check 'timing' @('compare-ranges',$verifyTrace,'--event-ordinal','1773','--baseline-event-ordinal','3901','--sections','timing')
    if ($timing.result.value.execution.atomCallCount -ne 2 -or $null -ne $timing.result.value.metrics) {throw 'Timing activated metric models'}
    $first = Run-Check 'metrics-first' @('trace.range-metrics',$verifyTrace,'--event-ordinal','1773')
    if ((Latest-MetricReceipt).hit) {throw 'Cold snapshot should miss'}
    $next = Run-Check 'metrics-next' @('trace.range-metrics',$verifyTrace,'--event-ordinal','1773','--cursor','20')
    if (-not (Latest-MetricReceipt).hit) {throw 'Second metric page did not reuse verified snapshot'}
    if ($next.result.value.metrics.totalCount -ne $script:verifyExpectedMetricCount -or
        $next.result.value.metrics.returnedCount -ne 20) {throw 'Metric paging closure changed'}
    $other = Run-Check 'metrics-other-scope' @('trace.range-metrics',$verifyTrace,'--event-ordinal','3901','--table','SM Register Occupancy')
    $again = Run-Check 'metrics-original-scope' @('trace.range-metrics',$verifyTrace,'--event-ordinal','1773','--table','SM Register Occupancy')
    if (-not (Latest-MetricReceipt).hit -or $again.result.value.scope.preorderOrdinal -ne 1773) {throw 'Alternating scope cache mismatch'}
    $missing = Run-Check 'mixed-missing-table' @('trace.range-metrics',$verifyTrace,'--event-ordinal','1773','--table','SM Register Occupancy','--table','Definitely Missing Table') 3
    if ($missing.result.isSuccess) {throw 'Partial table request was treated as complete'}
    $search = Run-Check 'find-metrics' @('find-metrics',$verifyTrace,'--event-ordinal','1773','--name-contains','register')
    $metric = $search.result.value.metrics.items[0]
    $exact = Run-Check 'exact-metric' @('find-metrics',$verifyTrace,'--event-ordinal','1773','--source-ordinal',"$($metric.sourceOrdinal)")
    if ($exact.result.value.metrics.returnedCount -ne 1 -or $exact.result.value.metrics.items[0].rowName -ne $metric.rowName) {throw 'Exact metric identity changed'}
    if (-not $CacheOnly) {
    $pairsPath = Join-Path $verifyWorkspace 'pairs.json'
    [IO.File]::WriteAllText($pairsPath, '{"pairs":[{"targetEventOrdinal":1773,"baselineEventOrdinal":1773,"label":"GBuffer control"},{"targetEventOrdinal":3901,"baselineEventOrdinal":3901,"label":"Deferred control"}]}', [Text.UTF8Encoding]::new($false))
    $batch = Run-Check 'batch-timings' @('compare-timings',$verifyTrace,'--pairs-file',$pairsPath,'--limit','1')
    if ($batch.result.value.pairs.returnedCount -ne 1 -or $batch.result.value.pairs.totalCount -ne 2 -or $batch.result.value.execution.atomCallCount -ne 2 -or $batch.result.value.pairs.nextCursor -ne 1) {throw 'Timing batch performed work outside its page'}
    $batchNext = Run-Check 'batch-next' @('compare-timings',$verifyTrace,'--pairs-file',$pairsPath,'--cursor','1','--limit','1')
    if ($batchNext.result.value.pairs.items[0].sourceOrdinal -ne 1 -or $batchNext.result.value.execution.atomCallCount -ne 2 -or $batchNext.result.value.statisticsBasis -ne 'currentPage') {throw 'Timing batch repeated earlier page or lost its source ordinal'}
    $profile = Run-Check 'profile' @('trace.shader-profile',$verifyTrace,'--event-ordinal','1773','--shader-stage','Pixel','--shader-hash','0xc7096045a5804ec3')
    if ($profile.result.value.shader.sampleCount -ne 3592) {throw 'Profile sample oracle changed'}
    $ambiguous = Run-Check 'ambiguous-shader-join' @('compare-ranges',$verifyTrace,'--event-ordinal','1773','--baseline-event-ordinal','1773','--sections','shaders') 5
    if ($ambiguous.result.error.code -ne 'wrapper.shader_comparison_ambiguous') {throw 'Shader comparison ambiguity changed'}
    $hotspots = Run-Check 'source-hotspots' @('find-source-hotspots',$verifyTrace,'--event-ordinal','1773','--shader-stage','Pixel','--shader-hash','0xc7096045a5804ec3','--view','dxil')
    if ($hotspots.result.value.returnedSampleCount -ne 2416 -or $hotspots.result.value.sampleCoveragePercent -ne 67.260579) {throw 'Source hotspot oracle changed'}
    }
    $ownersBefore = @(Get-ChildItem -LiteralPath (Join-Path $verifyWorkspace '.local\sessions') -Recurse -Filter owner.json | ForEach-Object {(Get-Content $_.FullName -Raw | ConvertFrom-Json).pid})
    $otherCwd = Join-Path $verifyWorkspace 'other-client'
    New-Item -ItemType Directory -Path $otherCwd | Out-Null
    Push-Location -LiteralPath $otherCwd
    try { $cross = Run-Check 'cross-cwd' @('find-metrics',$verifyTrace,'--event-ordinal','1773','--source-ordinal',"$($metric.sourceOrdinal)") }
    finally { Pop-Location }
    $ownersAfter = @(Get-ChildItem -LiteralPath (Join-Path $verifyWorkspace '.local\sessions') -Recurse -Filter owner.json | ForEach-Object {(Get-Content $_.FullName -Raw | ConvertFrom-Json).pid})
    if (($ownersBefore -join ',') -ne ($ownersAfter -join ',')) {throw 'Workspace did not preserve Viewer across cwd change'}
    Assert-ClosedSession 'close-before-reopen'
    $reopened = Run-Check 'metrics-reopened' @('trace.range-metrics',$verifyTrace,'--event-ordinal','1773')
    if ((Latest-MetricReceipt).hit) {throw 'New session reused old instance snapshot'}
    $repeated = Run-Check 'metrics-reopened-next' @('trace.range-metrics',$verifyTrace,'--event-ordinal','1773','--cursor','20')
    if (-not (Latest-MetricReceipt).hit) {throw 'New session failed to admit its own snapshot'}
}
finally {
    try { Assert-ClosedSession 'close' }
    finally {
        try {
            if ((Get-FileHash -LiteralPath $verifyTrace -Algorithm SHA256).Hash -ne $verifyInitialHash) {throw 'Original report changed'}
        }
        finally {
            $verifyStats | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $verifyWorkspace 'summary.json') -Encoding utf8
            Write-Host "Evidence: $verifyWorkspace"
        }
    }
}
