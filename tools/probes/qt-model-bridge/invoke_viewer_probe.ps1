[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ReportPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [Parameter(Mandatory = $true)]
    [ValidateSet(
        'heartbeat',
        'event-discovery',
        'event-export',
        'metrics-discovery',
        'metrics-export',
        'model-catalog',
        'model-export',
        'selection-metrics-export')]
    [string] $Mode,

    [hashtable] $Settings = @{},

    [ValidateRange(5, 600)]
    [int] $TimeoutSeconds = 120,

    [string] $ViewerPath = 'C:\Program Files\NVIDIA Corporation\Nsight Graphics 2026.2.0\host\windows-desktop-nomad-x64\ngfx-ui.exe'
)

$ErrorActionPreference = 'Stop'
$verifiedVersion = '2026.2.0.0'
$verifiedBuild = '37991608'
$runStarted = Get-Date
$timer = [System.Diagnostics.Stopwatch]::StartNew()
$requestId = [Guid]::NewGuid().ToString('N')
$crashReporterCount = 0
$viewerExitCode = $null
$probeDocument = $null
$reportIdentity = $null
$resolvedOutput = $null

function Write-RunResult {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Status,

        [string] $ErrorCode,

        [string] $Message
    )

    $timer.Stop()
    [ordered]@{
        schema = 'NsightSolidProbeRunV1'
        status = $Status
        errorCode = $ErrorCode
        message = $Message
        requestId = $requestId
        reportId = $reportIdentity
        mode = $Mode
        viewerVersion = $verifiedVersion
        viewerBuild = $verifiedBuild
        viewerExitCode = $viewerExitCode
        durationMs = [Math]::Round($timer.Elapsed.TotalMilliseconds)
        crashReporterCount = $crashReporterCount
        outputPath = $resolvedOutput
        outputSchema = if ($null -ne $probeDocument) { $probeDocument.schema } else { $null }
        outputStatus = if ($null -ne $probeDocument) { $probeDocument.status } else { $null }
        outputStage = if ($null -ne $probeDocument) { $probeDocument.stage } else { $null }
    } | ConvertTo-Json -Compress -Depth 8
}

try {
    $report = Get-Item -LiteralPath $ReportPath
    if ($report.PSIsContainer -or $report.Extension -ne '.ngfx-gputrace') {
        throw 'ReportPath must name an existing .ngfx-gputrace file.'
    }

    $viewer = Get-Item -LiteralPath $ViewerPath
    $viewerVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($viewer.FullName)
    if ($viewerVersion.ProductVersion -notmatch '^2026\.2(?:\.|$)') {
        throw "The probe is pinned to Nsight Graphics $verifiedVersion build $verifiedBuild."
    }

    $pluginPath = Join-Path $viewer.DirectoryName 'Plugins\generic\solidprobe.dll'
    if (-not (Test-Path -LiteralPath $pluginPath -PathType Leaf)) {
        throw 'The version-pinned SolidProbe plugin hook is not installed.'
    }

    $resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $resolvedOutput
    if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $outputDirectory -Force
    }

    $reportIdentity = '{0}:{1}:{2}' -f @(
        $report.Name,
        $report.Length,
        $report.LastWriteTimeUtc.Ticks)

    $reservedSettings = @(
        'OUTPUT',
        'MODE',
        'REQUEST_ID',
        'REPORT_ID',
        'QUIT_WHEN_READY',
        'QUIT_AFTER_HEARTBEAT')

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $viewer.FullName
    $startInfo.UseShellExecute = $false
    $startInfo.ArgumentList.Add($report.FullName)
    $startInfo.ArgumentList.Add('-plugin')
    $startInfo.ArgumentList.Add('SolidProbe')
    $startInfo.Environment['NSIGHT_SOLID_PROBE_OUTPUT'] = $resolvedOutput
    $startInfo.Environment['NSIGHT_SOLID_PROBE_MODE'] = $Mode
    $startInfo.Environment['NSIGHT_SOLID_PROBE_REQUEST_ID'] = $requestId
    $startInfo.Environment['NSIGHT_SOLID_PROBE_REPORT_ID'] = $reportIdentity
    if ($Mode -eq 'heartbeat') {
        $startInfo.Environment['NSIGHT_SOLID_PROBE_QUIT_AFTER_HEARTBEAT'] = '1'
    } else {
        $startInfo.Environment['NSIGHT_SOLID_PROBE_QUIT_WHEN_READY'] = '1'
    }

    foreach ($entry in $Settings.GetEnumerator()) {
        $key = $entry.Key.ToString().Trim().ToUpperInvariant()
        if ($key -notmatch '^[A-Z0-9_]+$' -or $reservedSettings -contains $key) {
            throw "Invalid or reserved probe setting: $key"
        }
        $startInfo.Environment["NSIGHT_SOLID_PROBE_$key"] = $entry.Value.ToString()
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'Nsight Viewer did not start.'
    }
    $viewerProcessId = $process.Id

    $finished = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $finished) {
        $process.Kill($true)
        $process.WaitForExit()
        $viewerExitCode = $process.ExitCode
        throw "Nsight Viewer exceeded the ${TimeoutSeconds}s timeout."
    }
    $viewerExitCode = $process.ExitCode

    Start-Sleep -Milliseconds 500
    $expectedCrashReporter = Join-Path $viewer.DirectoryName 'CrashReporter.exe'
    $newCrashReporters = @(
        Get-CimInstance Win32_Process -Filter "Name = 'CrashReporter.exe'" |
            Where-Object {
                $_.ParentProcessId -eq $viewerProcessId -and
                    $_.ExecutablePath -eq $expectedCrashReporter
            } |
            ForEach-Object {
                Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
            })
    $crashReporterCount = $newCrashReporters.Count
    foreach ($crashReporter in $newCrashReporters) {
        $null = $crashReporter.CloseMainWindow()
        if (-not $crashReporter.WaitForExit(500)) {
            $crashReporter.Kill()
            $crashReporter.WaitForExit()
        }
    }

    if ($viewerExitCode -ne 0) {
        throw "Nsight Viewer exited with code $viewerExitCode."
    }
    if ($crashReporterCount -gt 0) {
        throw 'Nsight Viewer spawned CrashReporter during this probe run.'
    }
    if (-not (Test-Path -LiteralPath $resolvedOutput -PathType Leaf)) {
        throw 'The expected probe output was not written.'
    }

    $outputItem = Get-Item -LiteralPath $resolvedOutput
    if ($outputItem.LastWriteTime -lt $runStarted) {
        throw 'The probe output is stale.'
    }
    $probeDocument = Get-Content -LiteralPath $resolvedOutput -Raw | ConvertFrom-Json
    if ($probeDocument.requestId -ne $requestId) {
        throw 'The probe output requestId does not match this run.'
    }
    if ([string]::IsNullOrWhiteSpace($probeDocument.schema)) {
        throw 'The probe output has no schema.'
    }
    if ($probeDocument.applicationVersion -notmatch [Regex]::Escape($verifiedVersion) -or
        $probeDocument.applicationVersion -notmatch "build $verifiedBuild") {
        throw 'The decoding Viewer version/build does not match the probe target.'
    }
    if ($probeDocument.status -in @('error', 'timeout')) {
        Write-RunResult -Status 'error' -ErrorCode "probe.$($probeDocument.stage)" `
            -Message 'The probe returned a structured error.'
        exit 2
    }

    Write-RunResult -Status 'complete'
    exit 0
} catch {
    Write-RunResult -Status 'error' -ErrorCode 'probe.run_failed' -Message $_.Exception.Message
    exit 1
}
