[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$Configuration = 'Release',
    [string]$ReleaseId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [switch]$SkipBuild,
    [switch]$MirrorToLive,
    [string]$LinuxSshHost = '192.168.0.199',
    [string]$LinuxSshUser = 'itw',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$LinuxRemoteRoot = '/srv/georaeplan',
    [string]$LinuxRemoteOpsPath = '/srv/georaeplan/ops',
    [int]$KeepReleaseCount = 2,
    [switch]$SkipConfigSync,
    [switch]$AllowLegacyLiveMirror,
    [switch]$AllowScheduledApplyTrigger,
    [switch]$SkipPreDeployOperationalGate,
    [switch]$SkipPostDeployOperationalGate,
    [switch]$SkipPlatformHealthChecks,
    [string]$PreDeployBaseUrl = '',
    [string]$PreDeploySecretPath = '',
    [string]$PreDeployOutputDirectory = '',
    [string[]]$PreDeployAllowedIntegrityWarningCodes = @(),
    [string]$PostDeployBaseUrl = '',
    [string]$PostDeploySecretPath = '',
    [string]$PostDeployOutputDirectory = '',
    [string[]]$PostDeployAllowedIntegrityWarningCodes = @(),
    [string]$DesktopNotes = '',
    [string]$AndroidNotes = ''
)

$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    param([string]$ExplicitProjectRoot)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitProjectRoot)) {
        return (Resolve-Path -LiteralPath $ExplicitProjectRoot).Path
    }

    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
}

function Resolve-DotnetCommand {
    param([Parameter(Mandatory = $true)][string]$Root)

    $candidates = @(
        $env:DOTNET_EXE,
        'D:\.dotnet-sdk\dotnet.exe',
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

    throw "Unable to locate a working dotnet executable under $Root."
}

function Resolve-SshExecutable {
    $windowsSsh = 'C:\Windows\System32\OpenSSH\ssh.exe'
    if (Test-Path -LiteralPath $windowsSsh) {
        return $windowsSsh
    }

    $ssh = Get-Command ssh -ErrorAction SilentlyContinue
    if ($null -ne $ssh) {
        return $ssh.Source
    }

    throw 'ssh executable was not found.'
}

function Resolve-TarExecutable {
    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if ($null -ne $tar) {
        return $tar.Source
    }

    $tar = Get-Command tar -ErrorAction SilentlyContinue
    if ($null -ne $tar) {
        return $tar.Source
    }

    throw 'tar executable was not found.'
}

function Quote-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Argument)

    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    $escaped = $Argument -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Convert-ToSingleQuotedShellLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Assert-SafeReleaseId {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value -notmatch '^[A-Za-z0-9._-]+$') {
        throw "Invalid release id: $Value"
    }
}

function New-LinuxSshConfig {
    param(
        [Parameter(Mandatory = $true)][string]$HostName,
        [Parameter(Mandatory = $true)][string]$UserName,
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string]$KeyPath,
        [Parameter(Mandatory = $true)][string]$RemoteRoot,
        [Parameter(Mandatory = $true)][string]$RemoteOpsPath
    )

    if ([string]::IsNullOrWhiteSpace($HostName) -or [string]::IsNullOrWhiteSpace($UserName)) {
        throw 'Linux PC SSH host/user is required.'
    }
    if ([string]::IsNullOrWhiteSpace($RemoteRoot) -or [string]::IsNullOrWhiteSpace($RemoteOpsPath)) {
        throw 'Linux PC remote root/ops path is required.'
    }
    if (-not (Test-Path -LiteralPath $KeyPath)) {
        throw "Linux PC SSH key was not found: $KeyPath"
    }

    return [pscustomobject]@{
        Host = $HostName.Trim()
        User = $UserName.Trim()
        Port = $Port
        KeyPath = (Resolve-Path -LiteralPath $KeyPath).Path
        RemoteRoot = $RemoteRoot.TrimEnd('/')
        RemoteOpsPath = $RemoteOpsPath.TrimEnd('/')
    }
}

function New-SshArgumentList {
    param(
        [Parameter(Mandatory = $true)]$Config,
        [switch]$BatchMode
    )

    $args = @(
        '-o', 'StrictHostKeyChecking=accept-new',
        '-o', 'ConnectTimeout=15'
    )

    if ($BatchMode) {
        $args += @('-o', 'BatchMode=yes')
    }
    if ($Config.Port -gt 0) {
        $args += @('-p', $Config.Port.ToString())
    }
    if (-not [string]::IsNullOrWhiteSpace($Config.KeyPath)) {
        $args += @('-i', $Config.KeyPath)
    }

    $args += ('{0}@{1}' -f $Config.User, $Config.Host)
    return $args
}

function Invoke-SshCommand {
    param(
        [Parameter(Mandatory = $true)]$Config,
        [Parameter(Mandatory = $true)][string]$Command,
        [switch]$IgnoreExitCode,
        [switch]$BatchMode
    )

    $sshExe = Resolve-SshExecutable
    $arguments = New-SshArgumentList -Config $Config -BatchMode:$BatchMode
    $arguments += $Command

    $stdoutPath = Join-Path ([System.IO.Path]::GetTempPath()) ("georaeplan-linux-ssh-out-" + [Guid]::NewGuid().ToString('N') + '.log')
    $stderrPath = Join-Path ([System.IO.Path]::GetTempPath()) ("georaeplan-linux-ssh-err-" + [Guid]::NewGuid().ToString('N') + '.log')

    try {
        $process = Start-Process -FilePath $sshExe -ArgumentList $arguments -NoNewWindow -PassThru -Wait -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        $stdout = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath -Raw } else { '' }
        $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { '' }

        if (-not $IgnoreExitCode -and $process.ExitCode -ne 0) {
            $message = if ([string]::IsNullOrWhiteSpace($stderr)) { $stdout } else { $stderr }
            throw "Linux PC ssh command failed with exit code $($process.ExitCode): $message"
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut = $stdout
            StdErr = $stderr
        }
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-SshTarUpload {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$RemoteDirectory,
        [Parameter(Mandatory = $true)]$Config
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory)) {
        throw "SSH upload source directory not found: $SourceDirectory"
    }

    $tarExe = Resolve-TarExecutable
    $sshExe = Resolve-SshExecutable
    $archiveDirectory = Split-Path -Parent $SourceDirectory
    if ([string]::IsNullOrWhiteSpace($archiveDirectory) -or -not (Test-Path -LiteralPath $archiveDirectory)) {
        $archiveDirectory = [System.IO.Path]::GetTempPath()
    }
    $archivePath = Join-Path $archiveDirectory ("georaeplan-linux-upload-" + [Guid]::NewGuid().ToString('N') + '.tar')

    try {
        & $tarExe -C $SourceDirectory -cf $archivePath .
        if ($LASTEXITCODE -ne 0) {
            throw "tar archive creation failed for $SourceDirectory"
        }

        $quotedRemoteDirectory = Convert-ToSingleQuotedShellLiteral -Value $RemoteDirectory
        $remoteCommand = "rm -rf $quotedRemoteDirectory && mkdir -p $quotedRemoteDirectory && tar -xf - -C $quotedRemoteDirectory"
        $argumentString = ((New-SshArgumentList -Config $Config) + @($remoteCommand) | ForEach-Object { Quote-ProcessArgument $_ }) -join ' '
        $cmdLine = "`"$sshExe`" $argumentString < `"$archivePath`""
        $commandOutput = cmd /c $cmdLine 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            throw "Linux PC ssh upload failed with exit code ${LASTEXITCODE}: $commandOutput"
        }
    }
    finally {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
    }
}

function Get-RemoteEnvMap {
    param([Parameter(Mandatory = $true)]$Config)

    $envPath = $Config.RemoteOpsPath + '/.env'
    $quotedEnvPath = Convert-ToSingleQuotedShellLiteral -Value $envPath
    $result = Invoke-SshCommand -Config $Config -Command "test -f $quotedEnvPath && cat $quotedEnvPath" -IgnoreExitCode -BatchMode
    $map = @{}
    if ($result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.StdOut)) {
        return $map
    }

    foreach ($line in ($result.StdOut -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line -match '^\s*#' -or $line -notmatch '=') {
            continue
        }

        $parts = $line -split '=', 2
        $key = $parts[0].Trim()
        if (-not [string]::IsNullOrWhiteSpace($key)) {
            $map[$key] = $parts[1].Trim()
        }
    }

    return $map
}

function Invoke-PublicHealthCheck {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Url
    )

    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 15
        if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
            throw "status=$($response.StatusCode)"
        }

        Write-Host "linux_pc_public_health_ok name=$Name status=$($response.StatusCode) url=$Url"
    }
    catch {
        throw "Linux PC public URL check failed: name=$Name url=$Url error=$($_.Exception.Message)"
    }
}

function Invoke-RemoteReadOnlyCheck {
    param([Parameter(Mandatory = $true)]$Config)

    $quotedOpsPath = Convert-ToSingleQuotedShellLiteral -Value $Config.RemoteOpsPath
    $remoteCommand = @(
        'set -e',
        "test -d $quotedOpsPath",
        "test -f $quotedOpsPath/apply-release.sh",
        "bash -n $quotedOpsPath/apply-release.sh",
        "docker ps --format '{{.Names}} {{.Status}}' | grep -E 'georaeplan|workplan' || true"
    ) -join '; '

    $output = Invoke-SshCommand -Config $Config -Command $remoteCommand -BatchMode
    Write-Host 'linux_pc_remote_readonly_check_ok'
    ($output.StdOut -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Write-Host $_ }
}

function Invoke-LinuxPcRemotePrune {
    param(
        [Parameter(Mandatory = $true)]$Config,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][int]$KeepCount,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($KeepCount -lt 1) {
        return
    }

    $root = ($Config.RemoteRoot.TrimEnd('/') + '/' + $RelativePath.Trim('/'))
    $quotedRoot = Convert-ToSingleQuotedShellLiteral -Value $root
    $quotedPattern = Convert-ToSingleQuotedShellLiteral -Value $Pattern
    $quotedLabel = Convert-ToSingleQuotedShellLiteral -Value $Label
    $remoteCommand = @"
set -e
root=$quotedRoot
pattern=$quotedPattern
keep=$KeepCount
label=$quotedLabel
if [ ! -d "`$root" ]; then
  echo "pruned label=`$label root=`$root total=0 keep=`$keep removed=0"
  exit 0
fi
real_root=`$(readlink -f "`$root")
if [ -z "`$real_root" ] || [ ! -d "`$real_root" ]; then
  echo "unsafe prune root: `$root" >&2
  exit 99
fi
tmp=`$(mktemp)
count_file=`$(mktemp)
trap 'rm -f "`$tmp" "`$count_file"' EXIT
find "`$real_root" -mindepth 1 -maxdepth 1 -type d -name "`$pattern" -printf '%T@ %p\n' | sort -rn > "`$tmp"
total=`$(wc -l < "`$tmp" | tr -d ' ')
echo 0 > "`$count_file"
if [ "`$total" -gt "`$keep" ]; then
  tail -n +`$((keep + 1)) "`$tmp" | cut -d' ' -f2- | while IFS= read -r target; do
    [ -z "`$target" ] && continue
    real_target=`$(readlink -f "`$target")
    case "`$real_target" in
      "`$real_root"/*)
        rm -rf -- "`$real_target"
        removed=`$(cat "`$count_file")
        removed=`$((removed + 1))
        echo "`$removed" > "`$count_file"
        ;;
      *)
        echo "unsafe prune target: `$real_target" >&2
        exit 99
        ;;
    esac
  done
fi
removed=`$(cat "`$count_file")
echo "pruned label=`$label root=`$real_root total=`$total keep=`$keep removed=`$removed"
"@

    $result = Invoke-SshCommand -Config $Config -Command $remoteCommand -BatchMode
    ($result.StdOut -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Write-Host "linux_pc_remote_prune $_" }
}

function Invoke-ReleaseOperationalGate {
    param(
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [string]$SecretPath = '',
        [string]$OutputDirectory = '',
        [string[]]$AllowedIntegrityWarningCodes = @(),
        [string]$ReleaseId = ''
    )

    $operationalGateScript = Join-Path $Root 'tools\ops\Invoke-GeoraePlanOperationalGate.ps1'
    if (-not (Test-Path -LiteralPath $operationalGateScript)) {
        throw "$Phase operational gate script not found: $operationalGateScript"
    }
    if ([string]::IsNullOrWhiteSpace($BaseUrl) -or $BaseUrl -eq 'https://api.example.invalid') {
        throw "$Phase operational gate cannot run because BaseUrl is missing or placeholder."
    }
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $safePhase = ($Phase -replace '[^A-Za-z0-9_-]', '-').Trim('-').ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($safePhase)) {
            $safePhase = 'operational'
        }
        $safeReleaseId = if ([string]::IsNullOrWhiteSpace($ReleaseId)) { Get-Date -Format 'yyyyMMdd-HHmmss' } else { $ReleaseId }
        $OutputDirectory = Join-Path $Root ("audit-output\$safePhase-operational-gate-$safeReleaseId")
    }

    $gateArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $operationalGateScript,
        '-ProjectRoot', $Root,
        '-BaseUrl', $BaseUrl,
        '-OutputDirectory', $OutputDirectory,
        '-FailOnIntegrityWarnings',
        '-SkipWriteSafetyChecks'
    )
    if (-not [string]::IsNullOrWhiteSpace($SecretPath)) {
        $gateArgs += @('-SecretPath', $SecretPath)
    }
    if ($AllowedIntegrityWarningCodes.Count -gt 0) {
        $gateArgs += '-AllowedIntegrityWarningCodes'
        $gateArgs += $AllowedIntegrityWarningCodes
    }

    Write-Host "$($Phase)_operational_gate_start base_url=$BaseUrl output=$OutputDirectory"
    & powershell @gateArgs
    if ($LASTEXITCODE -ne 0) {
        throw "$Phase operational gate failed with exit code $LASTEXITCODE. Report directory: $OutputDirectory"
    }
    Write-Host "$($Phase)_operational_gate_done output=$OutputDirectory"
}

function Update-PublishedAppSettings {
    param(
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][hashtable]$RemoteEnv
    )

    $publishedAppSettingsPath = Join-Path $PublishRoot 'appsettings.json'
    if (-not (Test-Path -LiteralPath $publishedAppSettingsPath)) {
        return
    }

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

    $postgresPassword = if ($RemoteEnv.ContainsKey('POSTGRES_PASSWORD')) { "$($RemoteEnv['POSTGRES_PASSWORD'])".Trim() } else { '' }
    $postgresUser = if ($RemoteEnv.ContainsKey('POSTGRES_USER')) { "$($RemoteEnv['POSTGRES_USER'])".Trim() } else { 'georaeplan' }
    $itworldDbName = if ($RemoteEnv.ContainsKey('ITWORLD_POSTGRES_DB')) { "$($RemoteEnv['ITWORLD_POSTGRES_DB'])".Trim() } else { 'georaeplan_itworld' }
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

Assert-SafeReleaseId -Value $ReleaseId
$ProjectRoot = Resolve-ProjectRoot -ExplicitProjectRoot $ProjectRoot
$tempInitializer = Join-Path $ProjectRoot 'tools\common\Initialize-GeoraePlanTemp.ps1'
if (Test-Path -LiteralPath $tempInitializer) {
    . $tempInitializer -ProjectRoot $ProjectRoot
}
$dotnetExe = Resolve-DotnetCommand -Root $ProjectRoot
$env:DOTNET_EXE = $dotnetExe
$linuxConfig = New-LinuxSshConfig `
    -HostName $LinuxSshHost `
    -UserName $LinuxSshUser `
    -Port $LinuxSshPort `
    -KeyPath $LinuxSshKeyPath `
    -RemoteRoot $LinuxRemoteRoot `
    -RemoteOpsPath $LinuxRemoteOpsPath

if ($SkipConfigSync) {
    Write-Host 'linux_pc_config_sync=skip'
}
if ($AllowLegacyLiveMirror) {
    Write-Warning 'AllowLegacyLiveMirror is ignored for Linux PC deploy. SSH apply-release.sh is required.'
}
if ($AllowScheduledApplyTrigger) {
    Write-Warning 'AllowScheduledApplyTrigger is ignored for Linux PC deploy. Direct SSH apply-release.sh is used.'
}

if ($MirrorToLive -and -not $SkipPlatformHealthChecks) {
    Invoke-PublicHealthCheck -Name 'trade' -Url 'https://trade.2884.kr/healthz'
    Invoke-PublicHealthCheck -Name 'work' -Url 'https://work.2884.kr/healthz'
    Invoke-PublicHealthCheck -Name 'itw' -Url 'https://itw.2884.kr/'
    Invoke-RemoteReadOnlyCheck -Config $linuxConfig
}

$remoteEnv = Get-RemoteEnvMap -Config $linuxConfig
$publicBaseUrl = if ($remoteEnv.ContainsKey('PUBLIC_BASE_URL')) { "$($remoteEnv['PUBLIC_BASE_URL'])".Trim() } else { '' }
$resolvedPreDeployBaseUrl = if (-not [string]::IsNullOrWhiteSpace($PreDeployBaseUrl)) { $PreDeployBaseUrl } elseif (-not [string]::IsNullOrWhiteSpace($PostDeployBaseUrl)) { $PostDeployBaseUrl } else { $publicBaseUrl }
$resolvedPostDeployBaseUrl = if (-not [string]::IsNullOrWhiteSpace($PostDeployBaseUrl)) { $PostDeployBaseUrl } elseif (-not [string]::IsNullOrWhiteSpace($PreDeployBaseUrl)) { $PreDeployBaseUrl } else { $publicBaseUrl }
$resolvedPreDeploySecretPath = if (-not [string]::IsNullOrWhiteSpace($PreDeploySecretPath)) { $PreDeploySecretPath } else { $PostDeploySecretPath }

if ($MirrorToLive -and -not $SkipPreDeployOperationalGate.IsPresent) {
    Invoke-ReleaseOperationalGate `
        -Phase 'pre-deploy' `
        -Root $ProjectRoot `
        -BaseUrl $resolvedPreDeployBaseUrl `
        -SecretPath $resolvedPreDeploySecretPath `
        -OutputDirectory $PreDeployOutputDirectory `
        -AllowedIntegrityWarningCodes $PreDeployAllowedIntegrityWarningCodes `
        -ReleaseId $ReleaseId
}
elseif ($MirrorToLive -and $SkipPreDeployOperationalGate.IsPresent) {
    Write-Warning 'Pre-deploy operational gate was skipped by request. Use only when a separate strict gate has already passed.'
}

$solutionPath = Get-ChildItem -LiteralPath $ProjectRoot -File -Filter '*.sln' | Select-Object -First 1 -ExpandProperty FullName
$serverProject = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'Server') -Recurse -File -Filter '*.Server.Api.csproj' | Select-Object -First 1 -ExpandProperty FullName
if (-not $solutionPath) {
    throw "Solution file not found under: $ProjectRoot"
}
if (-not (Test-Path -LiteralPath $serverProject)) {
    throw "Server project not found: $serverProject"
}

$localReleaseWorkRoot = Join-Path $ProjectRoot 'release-temp'
New-Item -ItemType Directory -Force $localReleaseWorkRoot | Out-Null
$tempPublishRoot = Join-Path $localReleaseWorkRoot "linux-$ReleaseId"
$metadataPath = Join-Path $tempPublishRoot 'release-info.txt'
Remove-Item $tempPublishRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $tempPublishRoot | Out-Null

try {
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
        $updateAssetArgs = @{
            ProjectRoot = $ProjectRoot
            OutputRoot = (Join-Path $tempPublishRoot 'updates')
        }
        if (-not [string]::IsNullOrWhiteSpace($DesktopNotes)) {
            $updateAssetArgs.DesktopNotes = $DesktopNotes
        }
        if (-not [string]::IsNullOrWhiteSpace($AndroidNotes)) {
            $updateAssetArgs.AndroidNotes = $AndroidNotes
        }

        & $updateAssetScript @updateAssetArgs
        if ($LASTEXITCODE -ne 0) {
            throw 'Update asset publish failed.'
        }
    }

    Update-PublishedAppSettings -PublishRoot $tempPublishRoot -RemoteEnv $remoteEnv

    $commit = (& git -C $ProjectRoot rev-parse HEAD 2>$null)
    @(
        "release_id=$ReleaseId",
        "built_at=$([DateTimeOffset]::Now.ToString('o'))",
        "configuration=$Configuration",
        "commit=$commit",
        "target=Linux PC",
        "remote_root=$($linuxConfig.RemoteRoot)"
    ) | Set-Content -Path $metadataPath -Encoding UTF8

    $remoteReleaseRoot = $linuxConfig.RemoteRoot + '/releases/' + $ReleaseId

    if ($MirrorToLive) {
        Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'app/backups' -Pattern 'live-*' -KeepCount $KeepReleaseCount -Label 'live-backups'
        Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'releases' -Pattern '*' -KeepCount $KeepReleaseCount -Label 'releases'
    }

    Write-Host "linux_pc_upload_start release_id=$ReleaseId remote_path=$remoteReleaseRoot"
    Invoke-SshTarUpload -SourceDirectory $tempPublishRoot -RemoteDirectory $remoteReleaseRoot -Config $linuxConfig
    Write-Host "linux_pc_upload_done release_id=$ReleaseId remote_path=$remoteReleaseRoot"

    if ($MirrorToLive) {
        Write-Host "linux_pc_apply_release_mode=ssh host=$($linuxConfig.Host) user=$($linuxConfig.User) port=$($linuxConfig.Port)"
        $quotedOps = Convert-ToSingleQuotedShellLiteral -Value $linuxConfig.RemoteOpsPath
        $quotedReleaseId = Convert-ToSingleQuotedShellLiteral -Value $ReleaseId
        $applyCommand = "cd $quotedOps && HEALTH_CHECK_RETRIES=900 /bin/bash ./apply-release.sh $quotedReleaseId"
        $applyResult = Invoke-SshCommand -Config $linuxConfig -Command $applyCommand
        ($applyResult.StdOut -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Write-Host $_ }
        if (-not [string]::IsNullOrWhiteSpace($applyResult.StdErr)) {
            ($applyResult.StdErr -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Write-Warning $_ }
        }

        if (-not $SkipPostDeployOperationalGate.IsPresent) {
            Invoke-ReleaseOperationalGate `
                -Phase 'post-deploy' `
                -Root $ProjectRoot `
                -BaseUrl $resolvedPostDeployBaseUrl `
                -SecretPath $PostDeploySecretPath `
                -OutputDirectory $PostDeployOutputDirectory `
                -AllowedIntegrityWarningCodes $PostDeployAllowedIntegrityWarningCodes `
                -ReleaseId $ReleaseId
        }
        else {
            Write-Warning 'Post-deploy operational gate was skipped by request. Use only when a separate strict gate has already passed.'
        }

        Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'releases' -Pattern '*' -KeepCount $KeepReleaseCount -Label 'releases'
        Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'app/backups' -Pattern 'live-*' -KeepCount $KeepReleaseCount -Label 'live-backups'
    }

    Write-Host "publish_done release_id=$ReleaseId release_path=$remoteReleaseRoot"
    if ($MirrorToLive) {
        Write-Host "linux_pc_apply_release_done release_id=$ReleaseId host=$($linuxConfig.Host) user=$($linuxConfig.User)"
    }
}
finally {
    Remove-Item $tempPublishRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "linux_pc_release_done release_id=$ReleaseId"

if ($MirrorToLive -and -not $SkipPlatformHealthChecks) {
    Invoke-PublicHealthCheck -Name 'trade' -Url 'https://trade.2884.kr/healthz'
    Invoke-PublicHealthCheck -Name 'work' -Url 'https://work.2884.kr/healthz'
    Invoke-PublicHealthCheck -Name 'itw' -Url 'https://itw.2884.kr/'
}
