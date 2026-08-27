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
    param([string[]]$Arguments, [int[]]$ExpectedExitCode)

    $lines = & $cliPath @Arguments
    $exitCode = $LASTEXITCODE
    $raw = $lines -join "`n"
    Assert-True ($ExpectedExitCode -contains $exitCode) `
        "'$($Arguments -join ' ')' exited $exitCode; expected $($ExpectedExitCode -join ' or ')."
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

function Get-LatestBridgeDocument {
    param([datetime]$NotBeforeUtc)

    $configured = [Environment]::GetEnvironmentVariable('NSIGHT_ANALYZER_RUN_ROOT')
    $runRoot = if ([string]::IsNullOrWhiteSpace($configured)) {
        Join-Path $repositoryRoot '.local\runs'
    }
    else {
        [IO.Path]::GetFullPath($configured)
    }
    $output = Get-ChildItem -LiteralPath $runRoot -Recurse `
            -Filter 'bridge-output.json' -File |
        Where-Object LastWriteTimeUtc -ge $NotBeforeUtc.AddSeconds(-1) |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    Assert-True ($null -ne $output) 'No fresh bridge output was found.'
    return Get-Content -LiteralPath $output.FullName -Raw | ConvertFrom-Json -Depth 100
}

dotnet build $solutionPath -c $Configuration
Assert-True ($LASTEXITCODE -eq 0) 'dotnet build failed.'
Assert-True (Test-Path -LiteralPath $cliPath -PathType Leaf) `
    'CLI executable was not produced.'

$capabilities = Invoke-JsonCli -Arguments @('capabilities', '--compact') -ExpectedExitCode 0
Assert-True $capabilities.Document.result.isSuccess 'capabilities failed.'
Assert-True ($capabilities.Document.result.value.operations.totalCount -eq 19) `
    'Unexpected operation count.'
Assert-True (@($capabilities.Document.result.value.operations.items |
        Where-Object layer -eq 'atom').Count -eq 14) `
    'Unexpected atom count.'
Assert-True (@($capabilities.Document.result.value.operations.items |
        Where-Object layer -eq 'wrapper').Count -eq 5) `
    'Unexpected wrapper count.'
Assert-True ($capabilities.Document.result.value.decoder.bridgeVersion -eq 'probe-0.51') `
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

    # Grain must partition the skeleton exactly: a caller that reads one grain
    # and derives "N of M" from its total is entitled to have the two grains
    # add up to the unfiltered count, with no row in both.
    $grainTotals = @{}
    foreach ($grain in @('marker', 'container')) {
        $grainArgs = [Collections.ArrayList]@(
            'trace.outline', $single, '--grain', $grain, '--limit', '500', '--compact')
        Add-ViewerArgument $grainArgs
        $grainPage = Invoke-JsonCli -Arguments $grainArgs -ExpectedExitCode 0
        $grainTotals[$grain] = $grainPage.Document.result.value.ranges.totalCount
        Assert-True (@($grainPage.Document.result.value.ranges.items |
                Where-Object { $_.grain -ne $grain }).Count -eq 0) `
            "trace.outline --grain $grain returned another grain."
    }
    Assert-True (($grainTotals['marker'] + $grainTotals['container']) -eq 493) `
        'Outline grains did not partition the range-grain skeleton.'

    $findArgs = [Collections.ArrayList]@(
        'find-ranges', $single, '--grain', 'marker', '--limit', '20', '--compact')
    Add-ViewerArgument $findArgs
    $foundRanges = Invoke-JsonCli -Arguments $findArgs -ExpectedExitCode 0
    $foundValue = $foundRanges.Document.result.value
    Assert-True ($foundValue.execution.atomCallCount -eq 1) `
        'find-ranges did not use one decoder request.'
    Assert-True ($foundValue.execution.scannedEventCount -eq 4951) `
        'find-ranges did not report its one complete Event List traversal.'
    Assert-True ($foundValue.ranges.returnedCount -eq 20) `
        'find-ranges did not return the requested bounded candidate count.'
    for ($index = 1; $index -lt $foundValue.ranges.items.Count; $index++) {
        Assert-True `
            ($foundValue.ranges.items[$index - 1].duration.milliseconds -ge
                $foundValue.ranges.items[$index].duration.milliseconds) `
            'find-ranges candidates are not ordered by Viewer duration.'
    }
    $scopedFindArgs = [Collections.ArrayList]@(
        'find-ranges', $single,
        '--name-contains', 'GBuffer', '--grain', 'marker',
        '--within-event-ordinal', '1680', '--limit', '20', '--compact')
    Add-ViewerArgument $scopedFindArgs
    $scopedFind = Invoke-JsonCli `
        -Arguments $scopedFindArgs -ExpectedExitCode 0
    Assert-True ($scopedFind.Document.result.value.withinScope.key.preorderOrdinal -eq 1680) `
        'Scoped find-ranges did not verify its exact ancestor.'
    Assert-True ($scopedFind.Document.result.value.ranges.totalCount -eq 1) `
        'Scoped find-ranges returned an unexpected candidate count.'
    Assert-True `
        ($scopedFind.Document.result.value.ranges.items[0].range.key.preorderOrdinal -eq 1773) `
        'Scoped find-ranges did not return the exact GBuffer range.'
    Assert-True (@($scopedFind.Document.result.value.ancestors |
            Where-Object { $_.key.treePath.Count -le
                $scopedFind.Document.result.value.withinScope.key.treePath.Count }).Count -eq 0) `
        'Scoped find-ranges returned shared ancestors above its exact scope.'

    # The published maximum must be one the operation can actually serve; a
    # schema that promises a page size which fails on serialization spends the
    # Viewer work before reporting the refusal.
    $shaderLimit = ($capabilities.Document.result.value.operations.items |
        Where-Object id -eq 'trace.range-shaders').parameters |
        Where-Object name -eq '--limit'
    $overLimitArgs = [Collections.ArrayList]@(
        'trace.range-shaders', $single, '--event-ordinal', '1773',
        '--limit', [string]($shaderLimit.maximum + 1), '--compact')
    Invoke-JsonCli -Arguments $overLimitArgs -ExpectedExitCode 2 | Out-Null
    $atLimitArgs = [Collections.ArrayList]@(
        'trace.range-shaders', $single, '--event-ordinal', '1773',
        '--limit', [string]$shaderLimit.maximum)
    Add-ViewerArgument $atLimitArgs
    $atLimit = Invoke-JsonCli -Arguments $atLimitArgs -ExpectedExitCode 0
    Assert-True $atLimit.Document.result.isSuccess `
        'trace.range-shaders cannot serve the page size its schema publishes.'

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

    $metricCatalogArgs = [Collections.ArrayList]@(
        'trace.range-metric-catalog', $single,
        '--event-path', '0.2.12.71.0', '--limit', '100', '--compact')
    Add-ViewerArgument $metricCatalogArgs
    $metricCatalogStartedUtc = [DateTime]::UtcNow
    $metricCatalog = Invoke-JsonCli `
        -Arguments $metricCatalogArgs -ExpectedExitCode 0
    Assert-True ($metricCatalog.Document.result.value.tables.totalCount -eq 88) `
        'Range metric catalog table count changed.'
    Assert-True (@($metricCatalog.Document.result.value.tables.items |
            Where-Object name -eq 'SM Register Occupancy').Count -eq 1) `
        'Range metric catalog lost SM Register Occupancy.'
    $metricCatalogBridge = Get-LatestBridgeDocument `
        -NotBeforeUtc $metricCatalogStartedUtc
    Assert-True (@($metricCatalogBridge.metricViews |
            Where-Object { -not $_.export.catalogOnly }).Count -eq 0) `
        'Range metric catalog did not use header-only bridge exports.'
    Assert-True (@($metricCatalogBridge.metricViews |
            Where-Object { $null -ne $_.export.nodes }).Count -eq 0) `
        'Range metric catalog carried metric cell payload.'

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

    # The first call above proves and admits the full stable shader snapshot.
    # A byte-independent repeat must be served from that exact session key,
    # while still re-establishing and verifying the requested EventKey.
    $cacheRequestStartedUtc = [DateTime]::UtcNow
    $shaderCached = Invoke-JsonCli -Arguments $shaderArgs -ExpectedExitCode 0
    $shaderBridge = Get-LatestBridgeDocument -NotBeforeUtc $cacheRequestStartedUtc
    Assert-True $shaderBridge.sessionCache.hit `
        'The repeated range-shader request did not hit the Viewer-process cache.'
    Assert-True ($shaderBridge.sessionCache.policy -eq 'range-shaders-v1') `
        'The range-shader response reported an unexpected cache policy.'
    Assert-True $shaderBridge.selectionMatchesTarget `
        'The cached range-shader request did not verify its exact EventKey.'
    Assert-True `
        (($shaderBridge.targetSelection.path -join '/') -eq
            ($shaderBridge.currentSelection.path -join '/')) `
        'The cached range-shader response selection does not match its target.'
    Assert-True `
        (($shader.Document.result.value | ConvertTo-Json -Depth 100 -Compress) -eq
            ($shaderCached.Document.result.value | ConvertTo-Json -Depth 100 -Compress)) `
        'The cached range-shader projection changed the semantic result.'

    $profileArgs = [Collections.ArrayList]@(
        'trace.shader-profile', $single,
        '--event-path', '0.2.12.71.0',
        '--shader-stage', $shaderFact.key.stage,
        '--shader-hash', '0xc7096045a5804ec3',
        '--shader-occurrence', '0', '--compact')
    Add-ViewerArgument $profileArgs
    $profileStartedUtc = [DateTime]::UtcNow
    $profile = Invoke-JsonCli -Arguments $profileArgs -ExpectedExitCode 0
    $profileBridge = Get-LatestBridgeDocument -NotBeforeUtc $profileStartedUtc
    Assert-True ($profile.Document.result.value.shader.sampleCount -eq 3592) `
        'Focused shader profile sample count changed.'
    Assert-True ($profile.Document.result.value.shader.staticRegisters -eq 30) `
        'Focused shader profile register count changed.'
    Assert-True $profileBridge.sessionCache.hit `
        'Focused shader profile did not reuse the exact range-shader snapshot.'
    Assert-True $profileBridge.selectionMatchesTarget `
        'Cached shader profile did not verify its exact EventKey.'

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
            'resolve-event did not push the name down into one decoder request.'
        # The decoder visits the whole tree once and returns only the matches,
        # so this reports the events actually traversed rather than the size of
        # the requested subtree.
        Assert-True ($resolved.Document.result.value.execution.scannedEventCount -eq 4951) `
            'resolve-event did not report the traversed event count.'
        Assert-True (@($resolved.Document.result.value.ancestors |
                Where-Object { $_.key.preorderOrdinal -lt 1680 }).Count -eq 0) `
            'Scoped resolve-event returned ancestors above its scope root.'
        Assert-True ($resolved.Document.result.value.ancestors[-1].key.preorderOrdinal -eq 1772) `
            'Scoped resolve-event lost the immediate parent of its match.'

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
        $inspectionMix = $inspection.Document.result.value.instructionMix
        Assert-True ($inspectionMix.availability -eq 'unavailable') `
            'inspect-pass unexpectedly coupled v1 to Instruction Mix state.'
        Assert-True `
            ($inspectionMix.error.code -eq `
                'trace.range_instruction_mix_not_requested') `
            'inspect-pass returned an unexpected Instruction Mix availability reason.'
        Assert-True `
            ($null -eq $inspectionMix.totalCount -and $null -eq $inspectionMix.items) `
            'inspect-pass disguised an unrequested Instruction Mix as successful empty data.'

        $compareArgs = [Collections.ArrayList]@(
            'compare-ranges', $single,
            '--event-ordinal', '1773',
            '--baseline-event-ordinal', '3901',
            '--top-shaders', '1', '--limit', '100', '--compact')
        Add-ViewerArgument $compareArgs
        $comparison = Invoke-JsonCli -Arguments $compareArgs -ExpectedExitCode @(0, 5)
        if ($comparison.Document.result.isSuccess) {
            Assert-True ($comparison.Document.result.value.metrics.totalCount -eq 801) `
                'compare-ranges metric identity closure changed.'
            Assert-True ($comparison.Document.result.value.metrics.matchedCount -eq 801) `
                'compare-ranges failed to join stable metric identities.'
            Assert-True ($comparison.Document.result.value.metrics.deltas.nextCursor -eq 100) `
                'compare-ranges metric delta paging changed.'
            Assert-True ($comparison.Document.result.value.shaders.matchedCount -eq 676) `
                'compare-ranges shader identity closure changed.'
        }
        else {
            Assert-True `
                ($comparison.Document.result.error.code -eq `
                    'trace.range_instruction_mix_not_loaded') `
                'compare-ranges did not preserve its current complete-comparison requirement.'
        }

        $instructionArgs = [Collections.ArrayList]@(
            'trace.range-instruction-mix', $single,
            '--event-path', '0.2.12.71.0', '--limit', '100', '--compact')
        Add-ViewerArgument $instructionArgs
        $instructions = Invoke-JsonCli -Arguments $instructionArgs -ExpectedExitCode @(0, 5)
        if ($instructions.Document.result.isSuccess) {
            Assert-True ($instructions.Document.result.value.instructions.totalCount -eq 56) `
                'Range Instruction Mix category count changed.'
            $fma = $instructions.Document.result.value.instructions.items |
                Where-Object { $_.pipe -eq 'FMA' -and $_.family -eq 'FP32 Math' } |
                Select-Object -First 1
            Assert-True `
                ($fma.sampleCount -eq 10343 -and $fma.instructionCount -eq 61179) `
                'FMA / FP32 Math range instruction closure changed.'
        }
        else {
            Assert-True `
                ($instructions.Document.result.error.code -eq `
                    'trace.range_instruction_mix_not_loaded') `
                'Range Instruction Mix not-loaded state changed.'
        }

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
    bridgeVersion = 'probe-0.51'
    singleFrameChecked = $singleChecked
    multiFrameChecked = $multiChecked
    slowViewerChecked = $slowChecked
} | ConvertTo-Json
