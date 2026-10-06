[CmdletBinding()]
param(
    [switch] $SkipBuild,
    [switch] $SkipHubPublish,
    [string] $HubPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'dist'))
$expectedDistRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'dist'))
if ($distRoot -ne $expectedDistRoot -or $distRoot -eq $repoRoot) {
    throw "Refusing to replace unexpected dist directory: $distRoot"
}
if (Test-Path -LiteralPath $distRoot) {
    Remove-Item -LiteralPath $distRoot -Recurse -Force
}

$publishDirectory = Join-Path $distRoot '.publish'
$packageDirectory = Join-Path $distRoot 'nsight-analyzer'
New-Item -ItemType Directory -Path $publishDirectory, $packageDirectory -Force | Out-Null

$project = Join-Path $repoRoot 'src\NsightAnalyzer\NsightAnalyzer.csproj'
if ($SkipBuild) {
    $binaryDirectory = Join-Path $repoRoot `
        'src\NsightAnalyzer\bin\Release\net9.0-windows\win-x64\publish'
}
else {
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugSymbols=false -p:DebugType=None -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed.'
    }
    $binaryDirectory = $publishDirectory
}

foreach ($name in @('nsight-analyzer.exe')) {
    $source = Join-Path $binaryDirectory $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Published NSightAnalyzer file is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $packageDirectory
}

$skillSource = Join-Path $repoRoot 'packaging\skill\nsight-analyzer'
Get-ChildItem -LiteralPath $skillSource | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $packageDirectory -Recurse
}
foreach ($required in @('SKILL.md', 'references\analysis.md', 'references\recovery.md', 'references\setup.md', 'references\cli.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageDirectory $required) -PathType Leaf)) {
        throw "Required packaged Skill resource is missing: $required"
    }
}
$skillText = Get-Content -LiteralPath (Join-Path $packageDirectory 'SKILL.md') -Raw
foreach ($reference in [regex]::Matches($skillText, '\]\((references/[^)]+)\)')) {
    $referencePath = [IO.Path]::GetFullPath((Join-Path $packageDirectory $reference.Groups[1].Value))
    if (-not $referencePath.StartsWith($packageDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        throw "Invalid packaged Skill reference: $($reference.Groups[1].Value)"
    }
}
$packageExecutable = Join-Path $packageDirectory 'nsight-analyzer.exe'
$decoderCatalogRaw = ((& $packageExecutable capabilities | Out-String).Trim())
if ($LASTEXITCODE -ne 0) {
    throw 'Packaged NSightAnalyzer failed the pre-manifest decoder catalog check.'
}
$decoderCatalog = $decoderCatalogRaw | ConvertFrom-Json
if (-not $decoderCatalog.result.isSuccess -or
    @($decoderCatalog.result.value.decoder.targets).Count -eq 0) {
    throw 'Packaged NSightAnalyzer returned no exact Viewer compatibility targets.'
}
$decoder = $decoderCatalog.result.value.decoder

$gitRevision = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Could not resolve the Git revision.'
}
$dirty = @(& git -C $repoRoot status --porcelain).Count -ne 0
$packageRevision = $gitRevision + $(if ($dirty) { '-dirty' } else { '' })
$files = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($packageDirectory, $_.FullName).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
$fingerprintRows = [string[]]@($files | ForEach-Object {
    $_.path + "`t" + $_.size.ToString([Globalization.CultureInfo]::InvariantCulture) + "`t" + $_.sha256
})
[Array]::Sort($fingerprintRows, [StringComparer]::Ordinal)
$contentFingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $fingerprintRows)))).ToLowerInvariant()
$manifest = [ordered]@{
    schemaVersion = 1
    product = 'NSightAnalyzer'
    skill = 'nsight-analyzer'
    gitRevision = $packageRevision
    target = 'windows-x64'
    contentFingerprint = $contentFingerprint
    fingerprintBasis = 'sha256 of ordinal-sorted path<TAB>byteLength<TAB>sha256 rows joined by LF'
    setupReference = 'references/setup.md'
    prerequisites = [ordered]@{
        dotnet = '9 x64 bundled self-contained; no external runtime required'
        viewerTargets = @($decoder.targets | ForEach-Object {
            [ordered]@{
                compatibilityProfile = $_.compatibilityProfile
                productVersion = $_.productVersion
                build = $_.productBuild
                sku = $_.productSku
                qtRuntime = $_.qtRuntimeVersion
            }
        })
        bridgeCompileQt = (@($decoder.targets.qtCompileVersion | Sort-Object -Unique) -join ' or ')
        bridge = $decoder.bridgeVersion
    }
    files = $files
} | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText(
    (Join-Path $packageDirectory 'PACKAGE.json'),
    $manifest + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

$capabilitiesRaw = ((& $packageExecutable capabilities | Out-String).Trim())
if ($LASTEXITCODE -ne 0) {
    throw 'Packaged NSightAnalyzer failed the capabilities smoke check.'
}
$capabilities = $capabilitiesRaw | ConvertFrom-Json
if (-not $capabilities.result.isSuccess -or
    $capabilities.schemaVersion.major -ne 2) {
    throw 'Packaged NSightAnalyzer returned an invalid capabilities result.'
}
$versionRaw = ((& $packageExecutable --version | Out-String).Trim())
if ($LASTEXITCODE -ne 0) { throw 'Packaged NSightAnalyzer failed the version smoke check.' }
$version = $versionRaw | ConvertFrom-Json
if (-not $version.result.isSuccess -or $version.result.value.packageState -ne 'verified' -or
    $version.result.value.contentFingerprint -ne $contentFingerprint -or
    $capabilities.result.value.tool.contentFingerprint -ne $contentFingerprint) {
    throw 'The packaged version does not verify its content fingerprint.'
}
foreach ($operation in @('doctor', 'find-metrics', 'find-source-hotspots', 'compare-ranges', 'compare-timings', 'viewer-session.close')) {
    $description = ((& $packageExecutable describe $operation | Out-String).Trim()) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $description.result.isSuccess -or
        '--workspace' -notin @($description.result.value.parameters.name)) {
        throw "Packaged operation discovery failed for $operation."
    }
}
$doctorRaw = ((& $packageExecutable doctor --viewer (Join-Path $publishDirectory 'missing-viewer.exe') | Out-String).Trim())
if ($LASTEXITCODE -ne 0) { throw 'Packaged NSightAnalyzer failed the read-only doctor smoke check.' }
$doctor = $doctorRaw | ConvertFrom-Json
if (-not $doctor.result.isSuccess -or $doctor.result.value.localPrerequisitesPresent -or
    $doctor.result.value.runtimeValidation -ne 'notChecked') {
    throw 'Packaged doctor incorrectly claimed runtime readiness.'
}

$resolvedPublishDirectory = [IO.Path]::GetFullPath($publishDirectory)
if (-not $resolvedPublishDirectory.StartsWith($distRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $resolvedPublishDirectory -eq $distRoot) {
    throw "Refusing to remove unexpected publish directory: $resolvedPublishDirectory"
}
Remove-Item -LiteralPath $resolvedPublishDirectory -Recurse -Force
$shortRevision = $gitRevision.Substring(0, 12) + $(if ($dirty) { '-dirty' } else { '' })
$archive = Join-Path $distRoot "nsight-analyzer-$shortRevision-windows-x64.zip"
Compress-Archive -LiteralPath $packageDirectory -DestinationPath $archive -CompressionLevel Optimal

if (-not $SkipHubPublish) {
    $resolvedHub = if ([string]::IsNullOrWhiteSpace($HubPath)) {
        [IO.Path]::GetFullPath((Join-Path $repoRoot '..\lx6-hub'))
    }
    else {
        [IO.Path]::GetFullPath($HubPath)
    }
    $publisher = Join-Path $resolvedHub 'publish.ps1'
    if (-not (Test-Path -LiteralPath $publisher -PathType Leaf)) {
        throw "lx6-hub publisher not found at '$publisher'. Pass -HubPath or -SkipHubPublish."
    }
    & $publisher -Kind skill -Name nsight-analyzer -Source $packageDirectory | Out-Host
}

[pscustomobject]@{
    package = $packageDirectory
    archive = $archive
    sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    revision = $packageRevision
    contentFingerprint = $contentFingerprint
}
