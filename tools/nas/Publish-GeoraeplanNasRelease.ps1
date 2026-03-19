[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$NasRoot = '\\192.0.2.10\docker\georaeplan',
    [string]$Configuration = 'Release',
    [string]$ReleaseId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [switch]$SkipBuild,
    [switch]$SkipConfigSync,
    [switch]$MirrorToLive
)

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Force $Destination | Out-Null
    & robocopy $Source $Destination /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed ($LASTEXITCODE): $Source -> $Destination"
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
}

$solutionPath = (Get-ChildItem -LiteralPath $ProjectRoot -File -Filter '*.sln' | Select-Object -First 1 -ExpandProperty FullName)
$serverProject = (Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'Server') -Recurse -File -Filter '*.Server.Api.csproj' | Select-Object -First 1 -ExpandProperty FullName)
$releaseRoot = Join-Path $NasRoot "releases\$ReleaseId"
$liveRoot = Join-Path $NasRoot 'app\live'
$tempPublishRoot = Join-Path ([System.IO.Path]::GetTempPath()) "georaeplan-$ReleaseId"
$metadataPath = Join-Path $tempPublishRoot 'release-info.txt'

if (-not (Test-Path -LiteralPath $serverProject)) {
    throw "Server project not found: $serverProject"
}

if (-not $solutionPath) {
    throw "Solution file not found under: $ProjectRoot"
}

Remove-Item $tempPublishRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $tempPublishRoot | Out-Null

if (-not $SkipBuild) {
    & dotnet build $solutionPath -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed."
    }
}

& dotnet publish $serverProject -c $Configuration -o $tempPublishRoot
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

$commit = (& git -C $ProjectRoot rev-parse HEAD 2>$null)
@(
    "release_id=$ReleaseId"
    "built_at=$([DateTimeOffset]::Now.ToString('o'))"
    "configuration=$Configuration"
    "commit=$commit"
) | Set-Content -Path $metadataPath -Encoding UTF8

if (-not $SkipConfigSync) {
    & (Join-Path $scriptRoot 'Sync-GeoraeplanNasConfig.ps1') -ProjectRoot $ProjectRoot -NasRoot $NasRoot
    if (-not $?) {
        throw "NAS config sync failed."
    }
}

Invoke-RobocopyMirror -Source $tempPublishRoot -Destination $releaseRoot

if ($MirrorToLive) {
    Invoke-RobocopyMirror -Source $tempPublishRoot -Destination $liveRoot
}

Remove-Item $tempPublishRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "publish_done release_id=$ReleaseId release_path=$releaseRoot"
if ($MirrorToLive) {
    Write-Host "live_mirror_done live_path=$liveRoot"
}
