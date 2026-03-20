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

function Get-NasEnvMap {
    param(
        [Parameter(Mandatory = $true)][string]$EnvPath
    )

    if (-not (Test-Path -LiteralPath $EnvPath)) {
        return @{}
    }

    $map = @{}
    foreach ($line in Get-Content -LiteralPath $EnvPath) {
        if ($line -match '^\s*#') {
            continue
        }

        if ($line -match '^([^=]+)=(.*)$') {
            $map[$matches[1].Trim()] = $matches[2]
        }
    }

    return $map
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
}

$solutionPath = (Get-ChildItem -LiteralPath $ProjectRoot -File -Filter '*.sln' | Select-Object -First 1 -ExpandProperty FullName)
$serverProject = (Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'Server') -Recurse -File -Filter '*.Server.Api.csproj' | Select-Object -First 1 -ExpandProperty FullName)
$releaseRoot = Join-Path $NasRoot "releases\$ReleaseId"
$liveRoot = Join-Path $NasRoot 'app\live'
$opsEnvPath = Join-Path $NasRoot 'ops\.env'
$tempPublishRoot = Join-Path ([System.IO.Path]::GetTempPath()) "georaeplan-$ReleaseId"
$metadataPath = Join-Path $tempPublishRoot 'release-info.txt'
$nasEnv = Get-NasEnvMap -EnvPath $opsEnvPath

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

$updateAssetScript = Join-Path $ProjectRoot 'tools\release\Publish-GeoraePlanUpdateAssets.ps1'
if (Test-Path -LiteralPath $updateAssetScript) {
    & $updateAssetScript -ProjectRoot $ProjectRoot -OutputRoot (Join-Path $tempPublishRoot 'updates')
    if ($LASTEXITCODE -ne 0) {
        throw "Update asset publish failed."
    }
}

$publishedAppSettingsPath = Join-Path $tempPublishRoot 'appsettings.json'
if (Test-Path -LiteralPath $publishedAppSettingsPath) {
    $publishedSettings = Get-Content -LiteralPath $publishedAppSettingsPath -Raw | ConvertFrom-Json

    if (-not $publishedSettings.PSObject.Properties['Kestrel']) {
        $publishedSettings | Add-Member -NotePropertyName Kestrel -NotePropertyValue ([pscustomobject]@{})
    }
    if (-not $publishedSettings.Kestrel.PSObject.Properties['Endpoints']) {
        $publishedSettings.Kestrel | Add-Member -NotePropertyName Endpoints -NotePropertyValue ([pscustomobject]@{})
    }
    if (-not $publishedSettings.Kestrel.Endpoints.PSObject.Properties['Http']) {
        $publishedSettings.Kestrel.Endpoints | Add-Member -NotePropertyName Http -NotePropertyValue ([pscustomobject]@{})
    }
    if (-not $publishedSettings.Kestrel.Endpoints.Http.PSObject.Properties['Url']) {
        $publishedSettings.Kestrel.Endpoints.Http | Add-Member -NotePropertyName Url -NotePropertyValue 'http://0.0.0.0:8080'
    }

    $publishedSettings.Kestrel.Endpoints.Http.Url = 'http://0.0.0.0:8080'

    if (-not $publishedSettings.PSObject.Properties['ConnectionStrings']) {
        $publishedSettings | Add-Member -NotePropertyName ConnectionStrings -NotePropertyValue ([pscustomobject]@{})
    }

    $postgresPassword = if ($nasEnv.ContainsKey('POSTGRES_PASSWORD')) { "$($nasEnv['POSTGRES_PASSWORD'])".Trim() } else { '' }
    $postgresUser = if ($nasEnv.ContainsKey('POSTGRES_USER')) { "$($nasEnv['POSTGRES_USER'])".Trim() } else { 'georaeplan' }
    $itworldDbName = if ($nasEnv.ContainsKey('ITWORLD_POSTGRES_DB')) { "$($nasEnv['ITWORLD_POSTGRES_DB'])".Trim() } else { 'georaeplan_itworld' }
    if (-not [string]::IsNullOrWhiteSpace($postgresPassword) -and -not [string]::IsNullOrWhiteSpace($itworldDbName)) {
        $itworldConnection = "Host=postgres;Port=5432;Database=$itworldDbName;Username=$postgresUser;Password=$postgresPassword"
        if ($publishedSettings.ConnectionStrings.PSObject.Properties['ITWORLD']) {
            $publishedSettings.ConnectionStrings.ITWORLD = $itworldConnection
        }
        else {
            $publishedSettings.ConnectionStrings | Add-Member -NotePropertyName ITWORLD -NotePropertyValue $itworldConnection
        }
    }

    $publishedSettings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $publishedAppSettingsPath -Encoding UTF8
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
