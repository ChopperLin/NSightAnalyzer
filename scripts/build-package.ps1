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
    $binaryDirectory = Join-Path $repoRoot 'src\NsightAnalyzer\bin\Release\net9.0-windows'
}
else {
    & dotnet publish $project -c Release --self-contained false `
        -p:DebugSymbols=false -p:DebugType=None -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed.'
    }
    $binaryDirectory = $publishDirectory
}

foreach ($name in @(
    'nsight-analyzer.exe',
    'nsight-analyzer.dll',
    'nsight-analyzer.deps.json',
    'nsight-analyzer.runtimeconfig.json'
)) {
    $source = Join-Path $binaryDirectory $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Published NSightAnalyzer file is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $packageDirectory
}

$skillSource = Join-Path $repoRoot 'packaging\skill\nsight-analyzer'
Copy-Item -LiteralPath (Join-Path $skillSource 'SKILL.md') -Destination $packageDirectory

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
$manifest = [ordered]@{
    schemaVersion = 1
    product = 'NSightAnalyzer'
    skill = 'nsight-analyzer'
    gitRevision = $packageRevision
    target = 'windows-x64'
    files = $files
} | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText(
    (Join-Path $packageDirectory 'PACKAGE.json'),
    $manifest + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

$packageExecutable = Join-Path $packageDirectory 'nsight-analyzer.exe'
$capabilitiesRaw = ((& $packageExecutable capabilities --compact | Out-String).Trim())
if ($LASTEXITCODE -ne 0) {
    throw 'Packaged NSightAnalyzer failed the capabilities smoke check.'
}
$capabilities = $capabilitiesRaw | ConvertFrom-Json
if (-not $capabilities.result.isSuccess -or
    $capabilities.schemaVersion.major -ne 1) {
    throw 'Packaged NSightAnalyzer returned an invalid capabilities result.'
}

Remove-Item -LiteralPath $publishDirectory -Recurse -Force
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
}
