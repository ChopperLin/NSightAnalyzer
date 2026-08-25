param(
    [string]$SingleFrameTrace,
    [string]$MultiFrameTrace,
    [string]$ViewerPath,
    [switch]$IncludeSlowViewer,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$solutionPath = Join-Path $repositoryRoot 'NSightAnalyzer.slnx'
$cliPath = Join-Path $repositoryRoot `
    "src\NsightAnalyzer\bin\$Configuration\net9.0-windows\nsight-analyzer.exe"

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "Verification failed: $Message"
    }
}

function Invoke-JsonCli {
    param([string[]]$Arguments, [int]$ExpectedExitCode)

    $lines = & $cliPath @Arguments
    $exitCode = $LASTEXITCODE
    $raw = $lines -join "`n"
    Assert-True ($exitCode -eq $ExpectedExitCode) `
        "'$($Arguments -join ' ')' exited $exitCode; expected $ExpectedExitCode."
    try {
        $document = $raw | ConvertFrom-Json
    }
    catch {
        throw "Verification failed: '$($Arguments -join ' ')' did not return one JSON document."
    }
    Assert-True ($null -ne $document.operation) 'Response has no operation.'
    Assert-True ($document.schemaVersion.major -eq 1) `
        'Unexpected response schema major version.'
    return [pscustomobject]@{ Raw = $raw; Document = $document }
}

function Add-ViewerArgument {
    param([System.Collections.ArrayList]$Arguments)
    if ($ViewerPath) {
        [void]$Arguments.Add('--viewer')
        [void]$Arguments.Add((Resolve-Path -LiteralPath $ViewerPath).Path)
    }
}

dotnet build $solutionPath -c $Configuration
Assert-True ($LASTEXITCODE -eq 0) 'dotnet build failed.'
Assert-True (Test-Path -LiteralPath $cliPath -PathType Leaf) `
    'CLI executable was not produced.'

$capabilities = Invoke-JsonCli -Arguments @('capabilities', '--compact') -ExpectedExitCode 0
Assert-True $capabilities.Document.result.isSuccess 'capabilities failed.'
Assert-True ($capabilities.Document.result.value.operations.totalCount -eq 16) `
    'Unexpected operation count.'
Assert-True (@($capabilities.Document.result.value.operations.items |
        Where-Object layer -eq 'atom').Count -eq 12) `
    'Unexpected atom count.'
Assert-True (@($capabilities.Document.result.value.operations.items |
        Where-Object layer -eq 'wrapper').Count -eq 4) `
    'Unexpected wrapper count.'
Assert-True ($capabilities.Document.result.value.decoder.bridgeVersion -eq 'probe-0.47') `
    'Unexpected bridge version.'
Assert-True (@($capabilities.Document.result.value.operations.items |
        Where-Object { -not $_.parameters -or $_.parameters.Count -eq 0 }).Count -eq 0) `
    'An operation was published without machine-readable parameters.'

# An agent that does not know this CLI reaches for these first; each must
# answer with the catalog instead of an argument error.
foreach ($helpToken in @('--help', '-h', 'help')) {
    $help = Invoke-JsonCli -Arguments @($helpToken, '--compact') -ExpectedExitCode 0
    Assert-True $help.Document.result.isSuccess "'$helpToken' did not return the catalog."
    Assert-True `
        ($help.Document.result.value.operations.totalCount -eq
            $capabilities.Document.result.value.operations.totalCount) `
        "'$helpToken' returned a different catalog than 'capabilities'."
}

$occurrenceParameter = $capabilities.Document.result.value.operations.items |
    Where-Object id -eq 'resolve-event' |
    ForEach-Object { $_.parameters } |
    Where-Object name -eq '--event-occurrence'
Assert-True ($occurrenceParameter.required -eq $true) `
    'resolve-event no longer requires an explicit occurrence.'
Assert-True ($occurrenceParameter.description -match 'zero-based') `
    'The occurrence parameter does not state its base.'

$invalidArguments = Invoke-JsonCli `
    -Arguments @('capabilities', '--cursor', '0', '--compact') `
    -ExpectedExitCode 2
Assert-True ($invalidArguments.Document.result.error.category -eq 'invalidInput') `
    'Invalid arguments were not classified as invalidInput.'

$missingPath = Join-Path $repositoryRoot `
    '.local\verification\definitely-missing.ngfx-gputrace'
$missing = Invoke-JsonCli `
    -Arguments @('trace.info', $missingPath, '--compact') `
    -ExpectedExitCode 3
Assert-True ($missing.Document.result.error.code -eq 'trace.not_found') `
    'Missing trace was not classified correctly.'

$missingResolveOccurrence = Invoke-JsonCli `
    -Arguments @(
        'resolve-event', $missingPath,
        '--event-name', 'GBufferPass', '--compact') `
    -ExpectedExitCode 2
Assert-True ($missingResolveOccurrence.Document.result.error.category -eq 'invalidInput') `
    'resolve-event accepted an implicit occurrence.'

$missingResolve = Invoke-JsonCli `
    -Arguments @(
        'resolve-event', $missingPath,
        '--event-name', 'GBufferPass', '--event-occurrence', '0', '--compact') `
    -ExpectedExitCode 3
Assert-True ($missingResolve.Document.result.error.code -eq 'trace.not_found') `
    'resolve-event did not preserve the atom trace-not-found failure.'

$missingCompareBaseline = Invoke-JsonCli `
    -Arguments @(
        'compare-ranges', $missingPath,
        '--event-ordinal', '1', '--compact') `
    -ExpectedExitCode 2
Assert-True ($missingCompareBaseline.Document.result.error.category -eq 'invalidInput') `
    'compare-ranges accepted a missing baseline EventKey.'

$missingFrameTimingContext = Invoke-JsonCli `
    -Arguments @(
        'compare-frame-timing', $missingPath,
        '--event-ordinal', '1', '--target-frame-index', '13',
        '--baseline-event-ordinal', '2', '--baseline-frame-index', '29',
        '--compact') `
    -ExpectedExitCode 2
Assert-True `
    ($missingFrameTimingContext.Document.result.error.category -eq 'invalidInput') `
    'compare-frame-timing accepted implicit Trace Analysis or Present context.'

$missingShaderKey = Invoke-JsonCli `
    -Arguments @(
        'trace.shader-source', $missingPath,
        '--event-path', '0', '--compact') `
    -ExpectedExitCode 2
Assert-True ($missingShaderKey.Document.result.error.category -eq 'invalidInput') `
    'Missing shader key was not rejected before Viewer launch.'

$singleChecked = $false
$multiChecked = $false
$slowChecked = $false
if ($SingleFrameTrace) {
    $single = (Resolve-Path -LiteralPath $SingleFrameTrace).Path
    $infoArgs = [Collections.ArrayList]@('trace.info', $single, '--compact')
    Add-ViewerArgument $infoArgs
    $info = Invoke-JsonCli -Arguments $infoArgs -ExpectedExitCode 0
    Assert-True $info.Document.result.isSuccess 'trace.info failed.'
    Assert-True ($info.Document.result.value.decoder.productBuild -eq '37991608') `
        'trace.info used an unexpected Viewer build.'

    $eventArgs = [Collections.ArrayList]@(
        'trace.events', $single, '--cursor', '0', '--limit', '10', '--compact')
    Add-ViewerArgument $eventArgs
    $events = Invoke-JsonCli -Arguments $eventArgs -ExpectedExitCode 0
    Assert-True ($events.Document.result.value.events.totalCount -eq 4951) `
        'Single-frame event total changed.'

    $outlineArgs = [Collections.ArrayList]@(
        'trace.outline', $single, '--limit', '500', '--compact')
    Add-ViewerArgument $outlineArgs
    $outline = Invoke-JsonCli -Arguments $outlineArgs -ExpectedExitCode 0
    Assert-True ($outline.Document.result.value.totalEventCount -eq 4951) `
        'Outline visited an unexpected number of events.'
    Assert-True ($outline.Document.result.value.ranges.totalCount -eq 493) `
        'Single-frame range-grain count changed.'
    Assert-True (@($outline.Document.result.value.ranges.items |
            Where-Object { $_.key.eventRange -notlike '*-*' }).Count -eq 0) `
        'Outline returned a row that is not a range.'
    # An outline ordinal must address the same event as trace.events, or every
    # follow-up call would need a second lookup.
    $outlineGBuffer = $outline.Document.result.value.ranges.items |
        Where-Object { $_.key.description -eq 'GBufferPass' } | Select-Object -First 1
    Assert-True ($outlineGBuffer.key.preorderOrdinal -eq 1773) `
        'Outline did not preserve the true preorder ordinal.'

    $parameterArgs = [Collections.ArrayList]@(
        'trace.event-parameters', $single,
        '--event-path', '0.2.13.25.0', '--compact')
    Add-ViewerArgument $parameterArgs
    $parameters = Invoke-JsonCli -Arguments $parameterArgs -ExpectedExitCode 0
    $instanceCount = $parameters.Document.result.value.parameters |
        Where-Object name -eq 'InstanceCount' | Select-Object -First 1
    Assert-True ($instanceCount.value -eq 94206) `
        'DrawIndexedInstanced InstanceCount changed.'

    $invalidDispatchArgs = [Collections.ArrayList]@(
        'trace.event-parameters', $single,
        '--event-ordinal', '3528', '--compact')
    Add-ViewerArgument $invalidDispatchArgs
    $invalidDispatch = Invoke-JsonCli `
        -Arguments $invalidDispatchArgs `
        -ExpectedExitCode 5
    Assert-True `
        ($invalidDispatch.Document.result.error.category -eq 'unavailable') `
        'Out-of-domain Dispatch parameters were not classified as unavailable.'
    Assert-True `
        ($invalidDispatch.Document.result.error.code -eq `
            'trace.event_parameter_out_of_domain') `
        'Out-of-domain Dispatch parameters returned an unexpected error code.'

    $metricArgs = [Collections.ArrayList]@(
        'trace.range-metrics', $single,
        '--event-path', '0.2.12.71.0',
        '--table', 'SM Register Occupancy',
        '--table', 'VidL2 Sector Hit-Rate (Total)',
        '--limit', '500', '--compact')
    Add-ViewerArgument $metricArgs
    $metrics = Invoke-JsonCli -Arguments $metricArgs -ExpectedExitCode 0
    Assert-True ($metrics.Document.result.value.tableCount -eq 2) `
        'Focused range metric table count changed.'

    $shaderArgs = [Collections.ArrayList]@(
        'trace.range-shaders', $single,
        '--event-path', '0.2.12.71.0',
        '--shader-hash', '0xc7096045a5804ec3',
        '--shader-occurrence', '0', '--limit', '1', '--compact')
    Add-ViewerArgument $shaderArgs
    $shader = Invoke-JsonCli -Arguments $shaderArgs -ExpectedExitCode 0
    $shaderFact = $shader.Document.result.value.shaders.items[0]
    Assert-True ($shaderFact.sampleCount -eq 3592) 'Focused shader sample count changed.'
    Assert-True ($shaderFact.staticRegisters -eq 30) 'Focused shader register count changed.'

    $singleChecked = $true
    if ($IncludeSlowViewer) {
        $resolveArgs = [Collections.ArrayList]@(
            'resolve-event', $single,
            '--event-name', 'GBufferPass', '--event-occurrence', '0',
            '--within-event-ordinal', '1680', '--compact')
        Add-ViewerArgument $resolveArgs
        $resolved = Invoke-JsonCli -Arguments $resolveArgs -ExpectedExitCode 0
        Assert-True ($resolved.Document.result.value.event.key.preorderOrdinal -eq 1773) `
            'resolve-event did not close the exact GBuffer EventKey.'
        Assert-True ($resolved.Document.result.value.matchCount -eq 1) `
            'resolve-event exact occurrence count changed.'
        Assert-True ($resolved.Document.result.value.execution.atomCallCount -eq 1) `
            'Scoped single-frame resolve-event did not start at its ancestor ordinal.'
        Assert-True ($resolved.Document.result.value.execution.scannedEventCount -eq 101) `
            'Scoped single-frame resolve-event did not stop at its subtree boundary.'

        $inspectArgs = [Collections.ArrayList]@(
            'inspect-pass', $single,
            '--event-ordinal', '1773',
            '--table', 'SM Register Occupancy',
            '--top-shaders', '1', '--compact')
        Add-ViewerArgument $inspectArgs
        $inspection = Invoke-JsonCli -Arguments $inspectArgs -ExpectedExitCode 0
        Assert-True ($inspection.Document.result.value.metrics.totalCount -eq 8) `
            'inspect-pass focused metric closure changed.'
        Assert-True ($inspection.Document.result.value.shaders.totalCount -eq 676) `
            'inspect-pass shader inventory closure changed.'
        Assert-True ($inspection.Document.result.value.instructionMix.totalCount -eq 56) `
            'inspect-pass instruction closure changed.'

        $compareArgs = [Collections.ArrayList]@(
            'compare-ranges', $single,
            '--event-ordinal', '1773',
            '--baseline-event-ordinal', '3901',
            '--top-shaders', '1', '--limit', '100', '--compact')
        Add-ViewerArgument $compareArgs
        $comparison = Invoke-JsonCli -Arguments $compareArgs -ExpectedExitCode 0
        Assert-True ($comparison.Document.result.value.metrics.totalCount -eq 801) `
            'compare-ranges metric identity closure changed.'
        Assert-True ($comparison.Document.result.value.metrics.matchedCount -eq 801) `
            'compare-ranges failed to join stable metric identities.'
        Assert-True ($comparison.Document.result.value.metrics.deltas.nextCursor -eq 100) `
            'compare-ranges metric delta paging changed.'
        Assert-True ($comparison.Document.result.value.shaders.matchedCount -eq 676) `
            'compare-ranges shader identity closure changed.'

        $instructionArgs = [Collections.ArrayList]@(
            'trace.range-instruction-mix', $single,
            '--event-path', '0.2.12.71.0', '--limit', '100', '--compact')
        Add-ViewerArgument $instructionArgs
        $instructions = Invoke-JsonCli -Arguments $instructionArgs -ExpectedExitCode 0
        Assert-True ($instructions.Document.result.value.instructions.totalCount -eq 56) `
            'Range Instruction Mix category count changed.'
        $fma = $instructions.Document.result.value.instructions.items |
            Where-Object { $_.pipe -eq 'FMA' -and $_.family -eq 'FP32 Math' } |
            Select-Object -First 1
        Assert-True ($fma.sampleCount -eq 10343 -and $fma.instructionCount -eq 61179) `
            'FMA / FP32 Math range instruction closure changed.'

        $sourceArgs = [Collections.ArrayList]@(
            'trace.shader-source', $single,
            '--event-path', '0.2.12.71.0',
            '--shader-hash', '0xc7096045a5804ec3',
            '--shader-occurrence', '0', '--limit', '1', '--compact')
        Add-ViewerArgument $sourceArgs
        $source = Invoke-JsonCli -Arguments $sourceArgs -ExpectedExitCode 0
        Assert-True ($source.Document.result.value.rows.totalCount -eq 965) `
            'Focused shader source row total changed.'
        $dxil = $source.Document.result.value.views |
            Where-Object kind -eq 'dxil' | Select-Object -First 1
        $sass = $source.Document.result.value.views |
            Where-Object kind -eq 'sass' | Select-Object -First 1
        Assert-True ($dxil.rowCount -eq 480 -and $dxil.attributedSampleCount -eq 3592) `
            'DXIL source closure changed.'
        Assert-True ($sass.addressRowCount -eq 242) `
            'SASS address closure changed.'

        $counterArgs = [Collections.ArrayList]@(
            'trace.counter-catalog', $single,
            '--event-path', '0.2.12.71.0', '--limit', '1', '--compact')
        Add-ViewerArgument $counterArgs
        $catalog = Invoke-JsonCli -Arguments $counterArgs -ExpectedExitCode 0
        Assert-True ($catalog.Document.result.value.export.rangeCount -eq 226) `
            'Counter export range count changed.'
        Assert-True ($catalog.Document.result.value.export.counterCount -eq 646) `
            'Counter export column count changed.'
        Assert-True ($catalog.Document.result.value.export.traceCopyCleanup -eq 'removed') `
            'Viewer-generated trace copy was not cleaned up.'

        $registerCounter = `
            'Top_Level_Triage.tpc__sm_rf_registers_allocated_shader_ps_realtime.avg.pct_of_peak_sustained_elapsed'
        $gbufferRange = `
            'FrameTime.GPU/UniversalRenderPipeline.RenderSingleCameraInternal: Main Camera/ExecuteRenderGraph/GBufferPass'
        $rangeCounterArgs = [Collections.ArrayList]@(
            'trace.range-counters', $single,
            '--event-path', '0.2.12.71.0',
            '--counter', $registerCounter, '--range', $gbufferRange,
            '--limit', '1', '--compact')
        Add-ViewerArgument $rangeCounterArgs
        $rangeCounter = Invoke-JsonCli -Arguments $rangeCounterArgs -ExpectedExitCode 0
        Assert-True ($rangeCounter.Document.result.value.selectedRangeCount -eq 1) `
            'Exact exported range filter did not close.'
        Assert-True ($rangeCounter.Document.result.value.selectedCounterCount -eq 1) `
            'Exact counter filter did not close.'
        $registerValue = $rangeCounter.Document.result.value.values.items[0].numericValue
        Assert-True ([Math]::Abs($registerValue - 62.7392) -lt 0.00001) `
            'Exported GBuffer PS register allocation changed.'
        $slowChecked = $true
    }

    $singleCloseArgs = [Collections.ArrayList]@(
        'viewer-session.close', $single, '--timeout-ms', '30000', '--compact')
    Add-ViewerArgument $singleCloseArgs
    $singleClose = Invoke-JsonCli -Arguments $singleCloseArgs -ExpectedExitCode 0
    Assert-True $singleClose.Document.result.value.hadSession `
        'Single-frame Viewer session was not found during cleanup.'
    Assert-True ($singleClose.Document.result.value.shutdown -eq 'graceful') `
        'Single-frame Viewer session did not shut down gracefully.'
}

if ($MultiFrameTrace) {
    $multi = (Resolve-Path -LiteralPath $MultiFrameTrace).Path
    $multiInfoArgs = [Collections.ArrayList]@('trace.info', $multi, '--compact')
    Add-ViewerArgument $multiInfoArgs
    $multiInfo = Invoke-JsonCli -Arguments $multiInfoArgs -ExpectedExitCode 0
    Assert-True `
        ($multiInfo.Document.result.value.trace.fileName -eq [IO.Path]::GetFileName($multi)) `
        'Multi-frame trace identity did not preserve the exact Unicode file name.'

    $lastPageArgs = [Collections.ArrayList]@(
        'trace.events', $multi,
        '--cursor', '306570', '--limit', '10', '--timeout-ms', '240000', '--compact')
    Add-ViewerArgument $lastPageArgs
    $lastPage = Invoke-JsonCli -Arguments $lastPageArgs -ExpectedExitCode 0
    Assert-True ($lastPage.Document.result.value.events.totalCount -eq 306576) `
        'Multi-frame event total changed.'
    Assert-True ($lastPage.Document.result.value.events.returnedCount -eq 6) `
        'Multi-frame final event page did not close.'

    $multiChecked = $true
    if ($IncludeSlowViewer) {
        $analysisArgs = [Collections.ArrayList]@(
            'trace.analysis', $multi,
            '--event-path', '0.0.35.2.0',
            '--limit', '3', '--timeout-ms', '240000', '--compact')
        Add-ViewerArgument $analysisArgs
        $analysis = Invoke-JsonCli -Arguments $analysisArgs -ExpectedExitCode 0
        Assert-True ($analysis.Document.result.value.ranges.totalCount -eq 30) `
            'Trace Analysis frame count changed.'
        Assert-True ($analysis.Document.result.value.ranges.items[0].frameIndex -eq 0) `
            'Trace Analysis first frame identity changed.'

        $resolveArgs = [Collections.ArrayList]@(
            'resolve-event', $multi,
            '--event-name', 'GBufferPass', '--event-occurrence', '0',
            '--within-event-ordinal', '131987',
            '--timeout-ms', '240000', '--compact')
        Add-ViewerArgument $resolveArgs
        $resolved = Invoke-JsonCli -Arguments $resolveArgs -ExpectedExitCode 0
        Assert-True ($resolved.Document.result.value.withinScope.key.preorderOrdinal -eq 131987) `
            'resolve-event did not verify the requested frame 13 scope root.'
        Assert-True ($resolved.Document.result.value.event.key.preorderOrdinal -eq 136633) `
            'resolve-event did not close frame 13 GBufferPass.'
        Assert-True ($resolved.Document.result.value.ancestors[0].key.preorderOrdinal -eq 131987) `
            'Scoped resolve-event did not root its ancestor chain at the requested range.'
        Assert-True ($resolved.Document.result.value.execution.atomCallCount -eq 20) `
            'Scoped resolve-event did not start paging at the requested ancestor ordinal.'
        Assert-True ($resolved.Document.result.value.execution.scannedEventCount -eq 9910) `
            'Scoped resolve-event did not stop at the exact frame 13 subtree boundary.'

        $frameTimingArgs = [Collections.ArrayList]@(
            'compare-frame-timing', $multi,
            '--event-ordinal', '131987', '--target-frame-index', '13',
            '--baseline-event-ordinal', '295573', '--baseline-frame-index', '29',
            '--analysis-seed-event-ordinal', '136633',
            '--present-queue-event-ordinal', '306395',
            '--timeout-ms', '240000', '--compact')
        Add-ViewerArgument $frameTimingArgs
        $frameTiming = Invoke-JsonCli -Arguments $frameTimingArgs -ExpectedExitCode 0
        $timingValue = $frameTiming.Document.result.value
        Assert-True ($timingValue.sequence.presentEventCount -eq 30) `
            'Frame timing Present sequence count changed.'
        Assert-True ($timingValue.sequence.traceAnalysisFrameCount -eq 30) `
            'Frame timing Trace Analysis sequence count changed.'
        Assert-True `
            ($timingValue.target.previousPresentEvent.key.preorderOrdinal -eq 306473 -and
                $timingValue.target.presentEvent.key.preorderOrdinal -eq 306479) `
            'Frame 13 Present identities changed.'
        Assert-True `
            ($timingValue.baseline.previousPresentEvent.key.preorderOrdinal -eq 306569 -and
                $timingValue.baseline.presentEvent.key.preorderOrdinal -eq 306575) `
            'Frame 29 Present identities changed.'
        Assert-True `
            ($timingValue.target.traceAnalysisAlignment.state -eq
                'withinViewerDisplayPrecision' -and
                $timingValue.baseline.traceAnalysisAlignment.state -eq
                'withinViewerDisplayPrecision') `
            'Trace Analysis did not align with consecutive Present starts.'
        $presentDelta = $timingValue.deltas |
            Where-Object field -eq 'presentStartInterval' | Select-Object -First 1
        $leadingDelta = $timingValue.deltas |
            Where-Object field -eq 'previousPresentToSelectedFrameStart' |
            Select-Object -First 1
        $eventDelta = $timingValue.deltas |
            Where-Object field -eq 'selectedFrameEventStartEndInterval' |
            Select-Object -First 1
        $trailingDelta = $timingValue.deltas |
            Where-Object field -eq 'selectedFrameEndToPresent' | Select-Object -First 1
        Assert-True ($presentDelta.deltaMilliseconds -eq 5.43) `
            'Frame timing Present interval delta changed.'
        Assert-True ($leadingDelta.deltaMilliseconds -eq 5.19) `
            'Frame timing leading interval delta changed.'
        Assert-True ($eventDelta.deltaMilliseconds -eq 0.26) `
            'Frame timing selected-event interval delta changed.'
        Assert-True ($trailingDelta.deltaMilliseconds -eq -0.02) `
            'Frame timing trailing interval delta changed.'
        Assert-True ($timingValue.execution.atomCallCount -eq 4) `
            'Frame timing wrapper atom-call closure changed.'
        Assert-True ($timingValue.execution.scannedEventCount -eq 183) `
            'Frame timing wrapper event scan closure changed.'
        $slowChecked = $true
    }

    $multiCloseArgs = [Collections.ArrayList]@(
        'viewer-session.close', $multi, '--timeout-ms', '30000', '--compact')
    Add-ViewerArgument $multiCloseArgs
    $multiClose = Invoke-JsonCli -Arguments $multiCloseArgs -ExpectedExitCode 0
    Assert-True $multiClose.Document.result.value.hadSession `
        'Multi-frame Viewer session was not found during cleanup.'
    Assert-True ($multiClose.Document.result.value.shutdown -eq 'graceful') `
        'Multi-frame Viewer session did not shut down gracefully.'
}

[pscustomobject]@{
    status = 'passed'
    configuration = $Configuration
    bridgeVersion = 'probe-0.47'
    singleFrameChecked = $singleChecked
    multiFrameChecked = $multiChecked
    slowViewerChecked = $slowChecked
} | ConvertTo-Json
