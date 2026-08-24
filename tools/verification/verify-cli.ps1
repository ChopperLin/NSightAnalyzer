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
Assert-True ($capabilities.Document.result.value.operations.totalCount -eq 11) `
    'Unexpected operation count.'
Assert-True ($capabilities.Document.result.value.decoder.bridgeVersion -eq 'probe-0.44') `
    'Unexpected bridge version.'

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

    $parameterArgs = [Collections.ArrayList]@(
        'trace.event-parameters', $single,
        '--event-path', '0.2.13.25.0', '--compact')
    Add-ViewerArgument $parameterArgs
    $parameters = Invoke-JsonCli -Arguments $parameterArgs -ExpectedExitCode 0
    $instanceCount = $parameters.Document.result.value.parameters |
        Where-Object name -eq 'InstanceCount' | Select-Object -First 1
    Assert-True ($instanceCount.value -eq 94206) `
        'DrawIndexedInstanced InstanceCount changed.'

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
}

if ($MultiFrameTrace) {
    $multi = (Resolve-Path -LiteralPath $MultiFrameTrace).Path
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
        $slowChecked = $true
    }
}

[pscustomobject]@{
    status = 'passed'
    configuration = $Configuration
    bridgeVersion = 'probe-0.44'
    singleFrameChecked = $singleChecked
    multiFrameChecked = $multiChecked
    slowViewerChecked = $slowChecked
} | ConvertTo-Json
