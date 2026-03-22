[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$NasRoot = '\\192.0.2.10\docker\georaeplan',
    [string]$Configuration = 'Release',
    [string]$ReleaseId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [switch]$SkipBuild,
    [switch]$SkipConfigSync,
    [switch]$MirrorToLive,
    [switch]$AllowLegacyLiveMirror,
    [string]$NasSshHost,
    [string]$NasSshUser,
    [int]$NasSshPort = 0,
    [string]$NasSshKeyPath,
    [string]$NasRemoteOpsPath,
    [switch]$AllowScheduledApplyTrigger
)

function Resolve-DotnetCommand {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    $candidates = @(
        $env:DOTNET_EXE,
        'C:\Users\beene\AppData\Local\GeoraePlan.Android\dotnet8\dotnet.exe',
        'C:\Program Files\dotnet\dotnet.exe'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            continue
        }

        try {
            & $candidate --version *> $null
            if ($LASTEXITCODE -eq 0) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }
        catch {
            continue
        }
    }

    throw "Unable to locate a working dotnet executable for NAS publish under $ProjectRoot."
}

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

function Get-FirstConfiguredValue {
    param([string[]]$Candidates)

    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            return $candidate.Trim()
        }
    }

    return ''
}

function Get-NasHostFromRoot {
    param([Parameter(Mandatory = $true)][string]$NasRoot)

    if ($NasRoot -match '^\\\\([^\\]+)\\') {
        return $matches[1]
    }

    return ''
}

function Resolve-NasSshConfig {
    param(
        [Parameter(Mandatory = $true)][string]$NasRoot,
        [Parameter(Mandatory = $true)][hashtable]$NasEnv,
        [string]$NasSshHost,
        [string]$NasSshUser,
        [int]$NasSshPort,
        [string]$NasSshKeyPath,
        [string]$NasRemoteOpsPath
    )

    $sshHost = Get-FirstConfiguredValue @(
        $NasSshHost,
        $env:NAS_SSH_HOST,
        ($NasEnv['NAS_SSH_HOST']),
        (Get-NasHostFromRoot -NasRoot $NasRoot)
    )

    $user = Get-FirstConfiguredValue @(
        $NasSshUser,
        $env:NAS_SSH_USER,
        ($NasEnv['NAS_SSH_USER'])
    )

    $remoteOpsPath = Get-FirstConfiguredValue @(
        $NasRemoteOpsPath,
        $env:NAS_REMOTE_OPS_PATH,
        ($NasEnv['NAS_REMOTE_OPS_PATH']),
        '/volume1/docker/georaeplan/ops'
    )

    $portText = Get-FirstConfiguredValue @(
        ($(if ($NasSshPort -gt 0) { $NasSshPort.ToString() } else { '' })),
        $env:NAS_SSH_PORT,
        ($NasEnv['NAS_SSH_PORT']),
        '22'
    )
    $port = 22
    [void][int]::TryParse($portText, [ref]$port)

    $keyPath = Get-FirstConfiguredValue @(
        $NasSshKeyPath,
        $env:NAS_SSH_KEY_PATH,
        ($NasEnv['NAS_SSH_KEY_PATH'])
    )

    if (-not [string]::IsNullOrWhiteSpace($keyPath) -and -not [System.IO.Path]::IsPathRooted($keyPath)) {
        $keyPath = Join-Path $env:USERPROFILE $keyPath
    }

    return [pscustomobject]@{
        Host = $sshHost
        User = $user
        Port = $port
        KeyPath = $keyPath
        RemoteOpsPath = $remoteOpsPath
    }
}

function Test-NasSshConfigComplete {
    param([Parameter(Mandatory = $true)]$Config)

    return -not [string]::IsNullOrWhiteSpace($Config.Host) -and
           -not [string]::IsNullOrWhiteSpace($Config.User) -and
           -not [string]::IsNullOrWhiteSpace($Config.RemoteOpsPath)
}

function Test-NasScheduledApplyEnabled {
    param([Parameter(Mandatory = $true)][hashtable]$NasEnv)

    $value = Get-FirstConfiguredValue @(
        $env:NAS_SCHEDULED_APPLY_ENABLED,
        ($NasEnv['NAS_SCHEDULED_APPLY_ENABLED'])
    )

    if ([string]::IsNullOrWhiteSpace($value)) {
        return $false
    }

    return @('1', 'true', 'yes', 'on') -contains $value.Trim().ToLowerInvariant()
}

function Queue-NasScheduledApply {
    param(
        [Parameter(Mandatory = $true)][string]$NasRoot,
        [Parameter(Mandatory = $true)][string]$ReleaseId,
        [Parameter(Mandatory = $true)][hashtable]$NasEnv
    )

    $stateRelativePath = Get-FirstConfiguredValue @(
        ($NasEnv['STATE_DIR']),
        'ops/state'
    )

    $pendingRelativePath = Get-FirstConfiguredValue @(
        ($NasEnv['PENDING_RELEASE_FILE'])
    )

    if (-not [string]::IsNullOrWhiteSpace($pendingRelativePath)) {
        if ([System.IO.Path]::IsPathRooted($pendingRelativePath)) {
            if ($pendingRelativePath -like '/volume1/*') {
                $pendingRelativePath = $pendingRelativePath -replace '^/volume1/', ''
                $pendingRelativePath = $pendingRelativePath -replace '/', '\'
            }
        }
    }
    else {
        $pendingRelativePath = Join-Path $stateRelativePath 'pending-release.txt'
    }

    $pendingPath = if ($pendingRelativePath -like '\\*') {
        $pendingRelativePath
    }
    else {
        Join-Path $NasRoot $pendingRelativePath
    }

    $pendingDir = Split-Path -Parent $pendingPath
    if (-not [string]::IsNullOrWhiteSpace($pendingDir)) {
        New-Item -ItemType Directory -Force $pendingDir | Out-Null
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($pendingPath, "$ReleaseId`n", $utf8NoBom)

    return $pendingPath
}

function Wait-NasScheduledApply {
    param(
        [Parameter(Mandatory = $true)][string]$NasRoot,
        [Parameter(Mandatory = $true)][string]$ReleaseId,
        [Parameter(Mandatory = $true)][hashtable]$NasEnv,
        [int]$TimeoutSeconds = 150
    )

    $stateRelativePath = Get-FirstConfiguredValue @(
        ($NasEnv['STATE_DIR']),
        'ops/state'
    )
    $currentReleasePath = Join-Path $NasRoot (Join-Path $stateRelativePath 'current-release.txt')

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $currentRelease = ''
        if (Test-Path -LiteralPath $currentReleasePath) {
            $currentRelease = (Get-Content -LiteralPath $currentReleasePath -ErrorAction SilentlyContinue | Select-Object -First 1).Trim()
        }

        if ($currentRelease -eq $ReleaseId) {
            return $true
        }

        Start-Sleep -Seconds 5
    }

    return $false
}

function Invoke-NasApplyRelease {
    param(
        [Parameter(Mandatory = $true)][string]$ReleaseId,
        [Parameter(Mandatory = $true)]$Config
    )

    $sshCommand = Get-Command ssh -ErrorAction SilentlyContinue
    if ($null -eq $sshCommand) {
        throw 'ssh command not found. Install OpenSSH client or configure a reachable SSH client first.'
    }

    $sshArgs = @(
        '-o', 'StrictHostKeyChecking=accept-new',
        '-o', 'ConnectTimeout=10',
        '-o', 'PreferredAuthentications=publickey,password,keyboard-interactive'
    )

    if ($Config.Port -gt 0) {
        $sshArgs += @('-p', $Config.Port.ToString())
    }

    if (-not [string]::IsNullOrWhiteSpace($Config.KeyPath)) {
        if (-not (Test-Path -LiteralPath $Config.KeyPath)) {
            throw "NAS_SSH_KEY_PATH not found: $($Config.KeyPath)"
        }

        $sshArgs += @('-i', $Config.KeyPath)
    }

    $remoteCommand = "cd '$($Config.RemoteOpsPath)' && chmod +x ./apply-release.sh ./health-check.sh ./_common.sh && ./apply-release.sh '$ReleaseId'"
    $sshArgs += ('{0}@{1}' -f $Config.User, $Config.Host)
    $sshArgs += $remoteCommand

    & $sshCommand.Source @sshArgs
    if ($LASTEXITCODE -ne 0) {
        throw "NAS apply-release.sh execution failed with exit code $LASTEXITCODE."
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
}

$dotnetExe = Resolve-DotnetCommand -ProjectRoot $ProjectRoot
$env:DOTNET_EXE = $dotnetExe

$solutionPath = (Get-ChildItem -LiteralPath $ProjectRoot -File -Filter '*.sln' | Select-Object -First 1 -ExpandProperty FullName)
$serverProject = (Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'Server') -Recurse -File -Filter '*.Server.Api.csproj' | Select-Object -First 1 -ExpandProperty FullName)
$releaseRoot = Join-Path $NasRoot "releases\$ReleaseId"
$liveRoot = Join-Path $NasRoot 'app\live'
$opsEnvPath = Join-Path $NasRoot 'ops\.env'
$tempPublishRoot = Join-Path ([System.IO.Path]::GetTempPath()) "georaeplan-$ReleaseId"
$metadataPath = Join-Path $tempPublishRoot 'release-info.txt'
$nasEnv = Get-NasEnvMap -EnvPath $opsEnvPath
$sshConfig = Resolve-NasSshConfig -NasRoot $NasRoot -NasEnv $nasEnv -NasSshHost $NasSshHost -NasSshUser $NasSshUser -NasSshPort $NasSshPort -NasSshKeyPath $NasSshKeyPath -NasRemoteOpsPath $NasRemoteOpsPath
$scheduledApplyEnabled = Test-NasScheduledApplyEnabled -NasEnv $nasEnv

if (-not (Test-Path -LiteralPath $serverProject)) {
    throw "Server project not found: $serverProject"
}

if (-not $solutionPath) {
    throw "Solution file not found under: $ProjectRoot"
}

Remove-Item $tempPublishRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $tempPublishRoot | Out-Null

if (-not $SkipBuild) {
    & $dotnetExe build $solutionPath -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet build failed.'
    }
}

& $dotnetExe publish $serverProject -c $Configuration -o $tempPublishRoot
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed.'
}

$updateAssetScript = Join-Path $ProjectRoot 'tools\release\Publish-GeoraePlanUpdateAssets.ps1'
if (Test-Path -LiteralPath $updateAssetScript) {
    & $updateAssetScript -ProjectRoot $ProjectRoot -OutputRoot (Join-Path $tempPublishRoot 'updates')
    if ($LASTEXITCODE -ne 0) {
        throw 'Update asset publish failed.'
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
        throw 'NAS config sync failed.'
    }
}

Invoke-RobocopyMirror -Source $tempPublishRoot -Destination $releaseRoot

$appliedRemotely = $false
$queuedForNasApply = $false
$queuedTriggerPath = ''
if ($MirrorToLive) {
    if (Test-NasSshConfigComplete -Config $sshConfig) {
        Write-Host "nas_apply_release_mode=ssh host=$($sshConfig.Host) user=$($sshConfig.User) port=$($sshConfig.Port)"
        try {
            Invoke-NasApplyRelease -ReleaseId $ReleaseId -Config $sshConfig
            $appliedRemotely = $true
        }
        catch {
            if ($AllowScheduledApplyTrigger -or $scheduledApplyEnabled) {
                Write-Warning "SSH apply-release failed and the script will fall back to the NAS scheduled trigger. Reason: $($_.Exception.Message)"
                $queuedTriggerPath = Queue-NasScheduledApply -NasRoot $NasRoot -ReleaseId $ReleaseId -NasEnv $nasEnv
                Write-Host "nas_apply_release_mode=scheduled-trigger pending_path=$queuedTriggerPath"
                if (-not (Wait-NasScheduledApply -NasRoot $NasRoot -ReleaseId $ReleaseId -NasEnv $nasEnv)) {
                    throw "NAS scheduled apply trigger was queued after SSH fallback, but release '$ReleaseId' was not applied within the timeout. Confirm the NAS scheduled task runs auto-apply-release.sh and check ops/state/auto-apply.log."
                }
                $queuedForNasApply = $true
            }
            else {
                throw
            }
        }
    }
    elseif ($AllowScheduledApplyTrigger -or $scheduledApplyEnabled) {
        $queuedTriggerPath = Queue-NasScheduledApply -NasRoot $NasRoot -ReleaseId $ReleaseId -NasEnv $nasEnv
        Write-Host "nas_apply_release_mode=scheduled-trigger pending_path=$queuedTriggerPath"
        if (-not (Wait-NasScheduledApply -NasRoot $NasRoot -ReleaseId $ReleaseId -NasEnv $nasEnv)) {
            throw "NAS scheduled apply trigger was queued but release '$ReleaseId' was not applied within the timeout. Confirm the NAS scheduled task runs auto-apply-release.sh and check ops/state/auto-apply.log."
        }
        $queuedForNasApply = $true
    }
    elseif ($AllowLegacyLiveMirror) {
        Write-Warning 'NAS SSH settings are missing. Because AllowLegacyLiveMirror was specified, the script will only copy the release and mirror app\\live directly.'
        Write-Warning 'Recommended: configure NAS_SSH_USER/HOST/PORT/KEY_PATH/REMOTE_OPS_PATH for SSH apply, or enable NAS_SCHEDULED_APPLY_ENABLED with auto-apply-release.sh so the NAS can run apply-release.sh locally after the release is copied.'
        Invoke-RobocopyMirror -Source $tempPublishRoot -Destination $liveRoot
    }
    else {
        throw 'MirrorToLive was requested, but NAS SSH settings are incomplete and NAS scheduled apply is disabled. Configure NAS_SSH_USER/HOST/PORT/KEY_PATH/REMOTE_OPS_PATH, set NAS_SCHEDULED_APPLY_ENABLED=true with auto-apply-release.sh configured on the NAS, or use -AllowLegacyLiveMirror only as a temporary fallback.'
    }
}

Remove-Item $tempPublishRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "publish_done release_id=$ReleaseId release_path=$releaseRoot"
if ($MirrorToLive) {
    if ($appliedRemotely) {
        Write-Host "nas_apply_release_done release_id=$ReleaseId host=$($sshConfig.Host) user=$($sshConfig.User)"
    }
    elseif ($queuedForNasApply) {
        Write-Host "nas_apply_release_done release_id=$ReleaseId mode=scheduled-trigger pending_path=$queuedTriggerPath"
    }
    elseif ($AllowLegacyLiveMirror) {
        Write-Host "live_mirror_done live_path=$liveRoot"
    }
}
