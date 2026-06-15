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
    [int]$KeepNasReleaseCount = 2,
    [string]$DeploymentTargetName = 'NAS',
    [string]$LogPrefix = 'nas',
    [switch]$AllowScheduledApplyTrigger,
    [switch]$SkipPreDeployOperationalGate,
    [switch]$SkipPostDeployOperationalGate,
    [string]$PreDeployBaseUrl = "",
    [string]$PreDeploySecretPath = "",
    [string]$PreDeployOutputDirectory = "",
    [string[]]$PreDeployAllowedIntegrityWarningCodes = @(),
    [string]$PostDeployBaseUrl = "",
    [string]$PostDeploySecretPath = "",
    [string]$PostDeployOutputDirectory = "",
    [string[]]$PostDeployAllowedIntegrityWarningCodes = @()
)

function Resolve-DotnetCommand {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

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

    throw "Unable to locate a working dotnet executable for $DeploymentTargetName publish under $ProjectRoot."
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

function Remove-OldNasReleaseDirectories {
    param(
        [Parameter(Mandatory = $true)][string]$ReleasesRoot,
        [Parameter(Mandatory = $true)][string]$CurrentReleaseId,
        [Parameter(Mandatory = $true)][int]$KeepReleaseCount
    )

    if ($KeepReleaseCount -lt 1 -or -not (Test-Path -LiteralPath $ReleasesRoot)) {
        return @()
    }

    $resolvedReleasesRoot = (Resolve-Path -LiteralPath $ReleasesRoot).ProviderPath.TrimEnd('\') + '\'
    $currentReleasePath = Join-Path $ReleasesRoot $CurrentReleaseId
    $resolvedCurrentReleasePath = if (Test-Path -LiteralPath $currentReleasePath) {
        (Resolve-Path -LiteralPath $currentReleasePath).ProviderPath
    }
    else {
        ''
    }

    $directories = Get-ChildItem -LiteralPath $ReleasesRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object -Property @{ Expression = 'LastWriteTimeUtc'; Descending = $true }, @{ Expression = 'Name'; Descending = $true }

    $preserve = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    if (-not [string]::IsNullOrWhiteSpace($resolvedCurrentReleasePath)) {
        [void]$preserve.Add($resolvedCurrentReleasePath)
    }

    foreach ($directory in $directories) {
        if ($preserve.Count -ge $KeepReleaseCount) {
            break
        }

        [void]$preserve.Add((Resolve-Path -LiteralPath $directory.FullName).ProviderPath)
    }

    $removed = New-Object System.Collections.Generic.List[string]
    foreach ($directory in $directories) {
        $resolvedDirectory = (Resolve-Path -LiteralPath $directory.FullName).ProviderPath
        if ($preserve.Contains($resolvedDirectory)) {
            continue
        }

        if (-not $resolvedDirectory.StartsWith($resolvedReleasesRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$DeploymentTargetName release prune target is outside releases root: $resolvedDirectory"
        }

        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force -ErrorAction Stop
        $removed.Add($directory.Name) | Out-Null
    }

    return $removed
}

function Resolve-SshExecutable {
    $windowsPath = Join-Path $env:WINDIR 'System32\OpenSSH\ssh.exe'
    if (Test-Path -LiteralPath $windowsPath) {
        return $windowsPath
    }

    $sshCommand = Get-Command ssh -ErrorAction SilentlyContinue
    if ($null -ne $sshCommand) {
        return $sshCommand.Source
    }

    throw 'ssh command not found. Install OpenSSH client first.'
}

function Resolve-TarExecutable {
    $tarCommand = Get-Command tar.exe -ErrorAction SilentlyContinue
    if ($null -ne $tarCommand) {
        return $tarCommand.Source
    }

    $tarCommand = Get-Command tar -ErrorAction SilentlyContinue
    if ($null -ne $tarCommand) {
        return $tarCommand.Source
    }

    throw 'tar command not found. Windows tar.exe is required for SSH upload fallback.'
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
        if (-not (Test-Path -LiteralPath $Config.KeyPath)) {
            throw "$DeploymentTargetName SSH key path not found: $($Config.KeyPath)"
        }

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

    $stdoutPath = Join-Path ([System.IO.Path]::GetTempPath()) ("georaeplan-ssh-out-" + [Guid]::NewGuid().ToString('N') + '.log')
    $stderrPath = Join-Path ([System.IO.Path]::GetTempPath()) ("georaeplan-ssh-err-" + [Guid]::NewGuid().ToString('N') + '.log')

    try {
        $process = Start-Process -FilePath $sshExe -ArgumentList $arguments -NoNewWindow -PassThru -Wait -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        $stdout = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath -Raw } else { '' }
        $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { '' }

        if (-not $IgnoreExitCode -and $process.ExitCode -ne 0) {
            $message = if ([string]::IsNullOrWhiteSpace($stderr)) { $stdout } else { $stderr }
            throw "ssh command failed with exit code $($process.ExitCode): $message"
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut   = $stdout
            StdErr   = $stderr
        }
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-NasEnvMapViaSsh {
    param([Parameter(Mandatory = $true)]$Config)

    if (-not (Test-NasSshConfigComplete -Config $Config)) {
        return @{}
    }

    $result = Invoke-SshCommand -Config $Config -Command "test -f '$($Config.RemoteOpsPath)/.env' && cat '$($Config.RemoteOpsPath)/.env'" -IgnoreExitCode -BatchMode
    if ($result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.StdOut)) {
        return @{}
    }

    $map = @{}
    foreach ($line in ($result.StdOut -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line -match '^\s*#' -or $line -notmatch '=') {
            continue
        }

        $parts = $line -split '=', 2
        $key = $parts[0].Trim()
        if ([string]::IsNullOrWhiteSpace($key)) {
            continue
        }

        $map[$key] = $parts[1].Trim()
    }

    return $map
}

function Invoke-SshTarUpload {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$RemoteDirectory,
        [Parameter(Mandatory = $true)]$Config,
        [switch]$MarkShellScriptsExecutable
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory)) {
        throw "SSH upload source directory not found: $SourceDirectory"
    }

    $tarExe = Resolve-TarExecutable
    $sshExe = Resolve-SshExecutable
    $archivePath = Join-Path ([System.IO.Path]::GetTempPath()) ("georaeplan-upload-" + [Guid]::NewGuid().ToString('N') + '.tar')

    try {
        & $tarExe -C $SourceDirectory -cf $archivePath .
        if ($LASTEXITCODE -ne 0) {
            throw "tar archive creation failed for $SourceDirectory"
        }

        $remoteCommand = if ($MarkShellScriptsExecutable) {
            "mkdir -p '$RemoteDirectory' && tar -xf - -C '$RemoteDirectory' && find '$RemoteDirectory' -maxdepth 1 -name '*.sh' -exec chmod +x {} +"
        }
        else {
            "mkdir -p '$RemoteDirectory' && tar -xf - -C '$RemoteDirectory'"
        }

        $argumentString = ((New-SshArgumentList -Config $Config) + @($remoteCommand) | ForEach-Object { Quote-ProcessArgument $_ }) -join ' '
        $cmdLine = "`"$sshExe`" $argumentString < `"$archivePath`""
        $commandOutput = cmd /c $cmdLine 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            throw "ssh upload failed with exit code ${LASTEXITCODE}: $commandOutput"
        }
    }
    finally {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
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

    $defaultKeyPath = Join-Path $env:USERPROFILE '.ssh\tradeplan_nas_ed25519'

    $user = Get-FirstConfiguredValue @(
        $NasSshUser,
        $env:NAS_SSH_USER,
        ($NasEnv['NAS_SSH_USER']),
        'root'
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
        '429',
        '22'
    )
    $port = 22
    [void][int]::TryParse($portText, [ref]$port)

    $keyPath = Get-FirstConfiguredValue @(
        $NasSshKeyPath,
        $env:NAS_SSH_KEY_PATH,
        ($NasEnv['NAS_SSH_KEY_PATH']),
        ($(if (Test-Path -LiteralPath $defaultKeyPath) { $defaultKeyPath } else { '' }))
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

function Convert-ToPosixSingleQuotedLiteral {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)

    $quoteEscape = "'`"'`"'"
    return "'" + ($Value -replace "'", $quoteEscape) + "'"
}

function Join-RemoteUnixPath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$ChildPath
    )

    $normalizedBase = ($BasePath -replace '\\', '/').TrimEnd('/')
    $normalizedChild = ($ChildPath -replace '\\', '/').TrimStart('/')
    if ([string]::IsNullOrWhiteSpace($normalizedBase)) {
        return '/' + $normalizedChild
    }

    if ([string]::IsNullOrWhiteSpace($normalizedChild)) {
        return $normalizedBase
    }

    return "$normalizedBase/$normalizedChild"
}

function Ensure-NasRuntimeStorageDirectories {
    param(
        [Parameter(Mandatory = $true)][string]$NasRoot,
        [Parameter(Mandatory = $true)]$Config
    )

    $relativeDirectories = @(
        'storage/files',
        'storage/data-protection-keys'
    )

    if (Test-Path -LiteralPath $NasRoot) {
        foreach ($relativeDirectory in $relativeDirectories) {
            New-Item -ItemType Directory -Force -Path (Join-Path $NasRoot $relativeDirectory) | Out-Null
        }

        return
    }

    if (-not (Test-NasSshConfigComplete -Config $Config)) {
        return
    }

    $remoteRoot = (($Config.RemoteOpsPath -replace '/ops/?$', '').TrimEnd('/'))
    if ([string]::IsNullOrWhiteSpace($remoteRoot)) {
        return
    }

    $remoteDirectories = $relativeDirectories |
        ForEach-Object { Convert-ToPosixSingleQuotedLiteral (Join-RemoteUnixPath -BasePath $remoteRoot -ChildPath $_) }
    $dataProtectionDirectory = Convert-ToPosixSingleQuotedLiteral (Join-RemoteUnixPath -BasePath $remoteRoot -ChildPath 'storage/data-protection-keys')
    $remoteCommand = "mkdir -p $($remoteDirectories -join ' ') && chmod 700 $dataProtectionDirectory"
    Invoke-SshCommand -Config $Config -Command $remoteCommand -BatchMode | Out-Null
}

function Invoke-NasRemoteDirectoryPrune {
    param(
        [Parameter(Mandatory = $true)]$Config,
        [Parameter(Mandatory = $true)][string]$RemoteRoot,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][int]$KeepCount,
        [string]$Label = 'storage'
    )

    if ($KeepCount -lt 1 -or -not (Test-NasSshConfigComplete -Config $Config) -or [string]::IsNullOrWhiteSpace($RemoteRoot)) {
        return @()
    }

    $normalizedRemoteRoot = ($RemoteRoot -replace '\\', '/').TrimEnd('/')
    if ([string]::IsNullOrWhiteSpace($normalizedRemoteRoot) -or -not $normalizedRemoteRoot.StartsWith('/')) {
        return @()
    }

    $remoteDirectory = Join-RemoteUnixPath -BasePath $normalizedRemoteRoot -ChildPath $RelativePath
    $safeRemoteRoot = Convert-ToPosixSingleQuotedLiteral $normalizedRemoteRoot
    $safeRemoteDirectory = Convert-ToPosixSingleQuotedLiteral $remoteDirectory
    $safePattern = Convert-ToPosixSingleQuotedLiteral $Pattern
    $remoteNameFilter = if ($Pattern -eq '*') { '' } else { "-name $safePattern" }

    $remoteCommand = @"
set -eu
base=$safeRemoteRoot
root=$safeRemoteDirectory
keep=$KeepCount
case "`$root" in
  "`$base"/releases|"`$base"/app/backups) ;;
  *) echo "refuse_root=`$root" >&2; exit 90 ;;
esac
if [ ! -d "`$root" ]; then
  echo "pruned label=$Label root=`$root total=0 keep=`$keep removed=0"
  exit 0
fi
tmp="`$(mktemp)"
trap 'rm -f "`$tmp"' EXIT
find "`$root" -maxdepth 1 -mindepth 1 -type d $remoteNameFilter -printf '%T@ %p\n' | sort -rn > "`$tmp"
total="`$(wc -l < "`$tmp" | tr -d ' ')"
removed=0
if [ "`$total" -gt "`$keep" ]; then
  removed=`$((total - keep))
  tail -n "`$removed" "`$tmp" | cut -d ' ' -f2- | while IFS= read -r path; do
    case "`$path" in
      "`$root"/*) ;;
      *) echo "refuse_target=`$path" >&2; exit 91 ;;
    esac
    echo "removed=`$path"
    rm -rf -- "`$path"
  done
fi
echo "pruned label=$Label root=`$root total=`$total keep=`$keep removed=`$removed"
"@

    $result = Invoke-SshCommand -Config $Config -Command $remoteCommand -BatchMode
    $removed = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($result.StdOut -split "`r?`n")) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        if ($trimmed -like 'removed=*') {
            $removed.Add($trimmed.Substring(8)) | Out-Null
        }
        elseif ($trimmed -like 'pruned *') {
            Write-Host "${LogPrefix}_remote_prune $trimmed"
        }
    }

    return $removed.ToArray()
}

function Resolve-NasScheduledStatePaths {
    param(
        [Parameter(Mandatory = $true)][hashtable]$NasEnv,
        [Parameter(Mandatory = $true)]$Config
    )

    $stateDir = Get-FirstConfiguredValue @(
        ($NasEnv['STATE_DIR']),
        (Join-RemoteUnixPath -BasePath $Config.RemoteOpsPath -ChildPath 'state')
    )
    if (-not $stateDir.StartsWith('/')) {
        $stateDir = Join-RemoteUnixPath -BasePath $Config.RemoteOpsPath -ChildPath $stateDir
    }

    $pendingReleasePath = Get-FirstConfiguredValue @(
        ($NasEnv['PENDING_RELEASE_FILE']),
        (Join-RemoteUnixPath -BasePath $stateDir -ChildPath 'pending-release.txt')
    )
    if (-not $pendingReleasePath.StartsWith('/')) {
        $pendingReleasePath = Join-RemoteUnixPath -BasePath $stateDir -ChildPath $pendingReleasePath
    }

    $currentReleasePath = Get-FirstConfiguredValue @(
        ($NasEnv['CURRENT_RELEASE_FILE']),
        (Join-RemoteUnixPath -BasePath $stateDir -ChildPath 'current-release.txt')
    )
    if (-not $currentReleasePath.StartsWith('/')) {
        $currentReleasePath = Join-RemoteUnixPath -BasePath $stateDir -ChildPath $currentReleasePath
    }

    $failedReleasePath = Get-FirstConfiguredValue @(
        ($NasEnv['FAILED_RELEASE_FILE']),
        (Join-RemoteUnixPath -BasePath $stateDir -ChildPath 'failed-release.txt')
    )
    if (-not $failedReleasePath.StartsWith('/')) {
        $failedReleasePath = Join-RemoteUnixPath -BasePath $stateDir -ChildPath $failedReleasePath
    }

    return [pscustomobject]@{
        StateDir = $stateDir
        PendingReleasePath = $pendingReleasePath
        CurrentReleasePath = $currentReleasePath
        FailedReleasePath = $failedReleasePath
    }
}

function Queue-NasScheduledApply {
    param(
        [Parameter(Mandatory = $true)][string]$NasRoot,
        [Parameter(Mandatory = $true)][string]$ReleaseId,
        [Parameter(Mandatory = $true)][hashtable]$NasEnv,
        [Parameter(Mandatory = $true)]$Config
    )

    if (Test-NasSshConfigComplete -Config $Config) {
        $statePaths = Resolve-NasScheduledStatePaths -NasEnv $NasEnv -Config $Config
        $remoteCommand = @(
            "mkdir -p $(Convert-ToPosixSingleQuotedLiteral $statePaths.StateDir)",
            "printf '%s\n' $(Convert-ToPosixSingleQuotedLiteral $ReleaseId) > $(Convert-ToPosixSingleQuotedLiteral $statePaths.PendingReleasePath)"
        ) -join ' && '
        Invoke-SshCommand -Config $Config -Command $remoteCommand -BatchMode | Out-Null
        return "ssh://$($Config.Host)$($statePaths.PendingReleasePath)"
    }

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
                $pendingRelativePath = $pendingRelativePath -replace '/', '\\'
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
        [Parameter(Mandatory = $true)]$Config,
        [int]$TimeoutSeconds = 900
    )

    if (Test-NasSshConfigComplete -Config $Config) {
        $statePaths = Resolve-NasScheduledStatePaths -NasEnv $NasEnv -Config $Config
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            $remoteCommand = @(
                "if test -f $(Convert-ToPosixSingleQuotedLiteral $statePaths.CurrentReleasePath); then printf 'current='; cat $(Convert-ToPosixSingleQuotedLiteral $statePaths.CurrentReleasePath); fi",
                "if test -f $(Convert-ToPosixSingleQuotedLiteral $statePaths.FailedReleasePath); then printf 'failed='; cat $(Convert-ToPosixSingleQuotedLiteral $statePaths.FailedReleasePath); fi"
            ) -join '; '
            $probe = Invoke-SshCommand -Config $Config -Command $remoteCommand -IgnoreExitCode -BatchMode
            $currentRelease = ''
            $failedRelease = ''
            foreach ($line in ($probe.StdOut -split "`r?`n")) {
                if ($line -like 'current=*') {
                    $currentRelease = $line.Substring(8).Trim()
                }
                elseif ($line -like 'failed=*') {
                    $failedRelease = $line.Substring(7).Trim()
                }
            }

            if ($currentRelease -eq $ReleaseId) {
                return $true
            }

            if ($failedRelease -eq $ReleaseId) {
                return $false
            }

            Start-Sleep -Seconds 5
        }

        return $false
    }

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

    $sshExe = Resolve-SshExecutable
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
            throw "$DeploymentTargetName SSH key path not found: $($Config.KeyPath)"
        }

        $sshArgs += @('-i', $Config.KeyPath)
    }

    $remoteCommand = "cd '$($Config.RemoteOpsPath)' && HEALTH_CHECK_RETRIES=900 /bin/bash ./apply-release.sh '$ReleaseId'"
    $sshArgs += ('{0}@{1}' -f $Config.User, $Config.Host)
    $sshArgs += $remoteCommand

    & $sshExe @sshArgs
    if ($LASTEXITCODE -ne 0) {
        throw "$DeploymentTargetName apply-release.sh execution failed with exit code $LASTEXITCODE."
    }
}

function Resolve-OperationalGateBaseUrl {
    param(
        [string]$PrimaryBaseUrl,
        [string]$FallbackBaseUrl,
        [hashtable]$NasEnv
    )

    $publicBaseUrl = if ($NasEnv.ContainsKey('PUBLIC_BASE_URL')) {
        "$($NasEnv['PUBLIC_BASE_URL'])".Trim()
    }
    else {
        ''
    }

    return Get-FirstConfiguredValue @(
        $PrimaryBaseUrl,
        $FallbackBaseUrl,
        $publicBaseUrl
    )
}

function Resolve-OperationalGatePlatformStateRoot {
    param([Parameter(Mandatory = $true)][string]$Root)

    if ([string]::IsNullOrWhiteSpace($Root)) {
        return ''
    }

    $stateRoot = Join-Path $Root 'ops\state'
    if (Test-Path -LiteralPath $stateRoot) {
        return $stateRoot
    }

    return ''
}

function Invoke-ReleaseOperationalGate {
    param(
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [string]$SecretPath = '',
        [string]$PlatformStateRoot = '',
        [string]$OutputDirectory = '',
        [string[]]$AllowedIntegrityWarningCodes = @(),
        [string]$ReleaseId = ''
    )

    $operationalGateScript = Join-Path $ProjectRoot 'tools\ops\Invoke-GeoraePlanOperationalGate.ps1'
    if (-not (Test-Path -LiteralPath $operationalGateScript)) {
        throw "$Phase operational gate script not found: $operationalGateScript"
    }

    if ([string]::IsNullOrWhiteSpace($BaseUrl) -or $BaseUrl -eq 'https://api.example.invalid') {
        throw "$Phase operational gate cannot run because PUBLIC_BASE_URL/BaseUrl is missing or placeholder."
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $safePhase = ($Phase -replace '[^A-Za-z0-9_-]', '-').Trim('-').ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($safePhase)) {
            $safePhase = 'operational'
        }
        $suffix = if ([string]::IsNullOrWhiteSpace($ReleaseId)) { Get-Date -Format 'yyyyMMdd-HHmmss' } else { $ReleaseId }
        $OutputDirectory = Join-Path $ProjectRoot ("audit-output\$safePhase-operational-gate-$suffix")
    }

    $gateArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $operationalGateScript,
        '-ProjectRoot', $ProjectRoot,
        '-BaseUrl', $BaseUrl,
        '-OutputDirectory', $OutputDirectory,
        '-FailOnIntegrityWarnings',
        '-SkipWriteSafetyChecks'
    )
    if (-not [string]::IsNullOrWhiteSpace($SecretPath)) {
        $gateArgs += @('-SecretPath', $SecretPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($PlatformStateRoot)) {
        $gateArgs += @('-PlatformStateRoot', $PlatformStateRoot)
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
$bootstrapSshConfig = Resolve-NasSshConfig -NasRoot $NasRoot -NasEnv @{} -NasSshHost $NasSshHost -NasSshUser $NasSshUser -NasSshPort $NasSshPort -NasSshKeyPath $NasSshKeyPath -NasRemoteOpsPath $NasRemoteOpsPath
$nasEnv = Get-NasEnvMap -EnvPath $opsEnvPath
if ($nasEnv.Count -eq 0) {
    $nasEnv = Get-NasEnvMapViaSsh -Config $bootstrapSshConfig
}
$sshConfig = Resolve-NasSshConfig -NasRoot $NasRoot -NasEnv $nasEnv -NasSshHost $NasSshHost -NasSshUser $NasSshUser -NasSshPort $NasSshPort -NasSshKeyPath $NasSshKeyPath -NasRemoteOpsPath $NasRemoteOpsPath
$scheduledApplyEnabled = Test-NasScheduledApplyEnabled -NasEnv $nasEnv

if (-not (Test-Path -LiteralPath $serverProject)) {
    throw "Server project not found: $serverProject"
}

if (-not $solutionPath) {
    throw "Solution file not found under: $ProjectRoot"
}

if ($MirrorToLive -and -not $SkipPreDeployOperationalGate.IsPresent) {
    $resolvedPreDeployBaseUrl = Resolve-OperationalGateBaseUrl `
        -PrimaryBaseUrl $PreDeployBaseUrl `
        -FallbackBaseUrl $PostDeployBaseUrl `
        -NasEnv $nasEnv

    Invoke-ReleaseOperationalGate `
        -Phase 'pre-deploy' `
        -ProjectRoot $ProjectRoot `
        -BaseUrl $resolvedPreDeployBaseUrl `
        -SecretPath (Get-FirstConfiguredValue @($PreDeploySecretPath, $PostDeploySecretPath)) `
        -PlatformStateRoot (Resolve-OperationalGatePlatformStateRoot -Root $NasRoot) `
        -OutputDirectory $PreDeployOutputDirectory `
        -AllowedIntegrityWarningCodes $PreDeployAllowedIntegrityWarningCodes `
        -ReleaseId $ReleaseId
}
elseif ($MirrorToLive -and $SkipPreDeployOperationalGate.IsPresent) {
    Write-Warning 'Pre-deploy operational gate was skipped by request. Use only when a separate strict gate has already passed.'
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
    & (Join-Path $scriptRoot 'Sync-GeoraeplanNasConfig.ps1') `
        -ProjectRoot $ProjectRoot `
        -NasRoot $NasRoot `
        -NasSshHost $sshConfig.Host `
        -NasSshUser $sshConfig.User `
        -NasSshPort $sshConfig.Port `
        -NasSshKeyPath $sshConfig.KeyPath `
        -NasRemoteOpsPath $sshConfig.RemoteOpsPath `
        -UseSshFallback
    if (-not $?) {
        throw "$DeploymentTargetName config sync failed."
    }
}

if (Test-Path -LiteralPath $NasRoot) {
    Invoke-RobocopyMirror -Source $tempPublishRoot -Destination $releaseRoot
}
elseif (Test-NasSshConfigComplete -Config $sshConfig) {
    $remoteBaseRoot = (($sshConfig.RemoteOpsPath -replace '/ops/?$', '').TrimEnd('/'))
    $removedRemoteBackupsBeforeUpload = Invoke-NasRemoteDirectoryPrune `
        -Config $sshConfig `
        -RemoteRoot $remoteBaseRoot `
        -RelativePath 'app/backups' `
        -Pattern 'live-*' `
        -KeepCount $KeepNasReleaseCount `
        -Label 'live-backups'
    if ($removedRemoteBackupsBeforeUpload.Count -gt 0) {
        Write-Host "${LogPrefix}_remote_live_backups_pruned_before_upload=$($removedRemoteBackupsBeforeUpload.Count)"
    }

    $removedRemoteReleasesBeforeUpload = Invoke-NasRemoteDirectoryPrune `
        -Config $sshConfig `
        -RemoteRoot $remoteBaseRoot `
        -RelativePath 'releases' `
        -Pattern '*' `
        -KeepCount $KeepNasReleaseCount `
        -Label 'releases'
    if ($removedRemoteReleasesBeforeUpload.Count -gt 0) {
        Write-Host "${LogPrefix}_remote_releases_pruned_before_upload=$($removedRemoteReleasesBeforeUpload.Count)"
    }

    $remoteReleaseRoot = "{0}/releases/{1}" -f $remoteBaseRoot, $ReleaseId
    Write-Warning "$DeploymentTargetName local mirror path is unavailable. Falling back to SSH upload: $remoteReleaseRoot"
    Invoke-SshTarUpload -SourceDirectory $tempPublishRoot -RemoteDirectory $remoteReleaseRoot -Config $sshConfig
}
else {
    throw "$DeploymentTargetName release upload failed: local mirror path '$NasRoot' is unavailable and SSH configuration is incomplete."
}

Ensure-NasRuntimeStorageDirectories -NasRoot $NasRoot -Config $sshConfig

$appliedRemotely = $false
$queuedForNasApply = $false
$queuedTriggerPath = ''
if ($MirrorToLive) {
    if (Test-NasSshConfigComplete -Config $sshConfig) {
        Write-Host "${LogPrefix}_apply_release_mode=ssh host=$($sshConfig.Host) user=$($sshConfig.User) port=$($sshConfig.Port)"
        try {
            Invoke-NasApplyRelease -ReleaseId $ReleaseId -Config $sshConfig
            $appliedRemotely = $true
        }
        catch {
            if ($AllowScheduledApplyTrigger -or $scheduledApplyEnabled) {
                Write-Warning "SSH apply-release failed and the script will fall back to the $DeploymentTargetName scheduled trigger. Reason: $($_.Exception.Message)"
                $queuedTriggerPath = Queue-NasScheduledApply -NasRoot $NasRoot -ReleaseId $ReleaseId -NasEnv $nasEnv -Config $sshConfig
                Write-Host "${LogPrefix}_apply_release_mode=scheduled-trigger pending_path=$queuedTriggerPath"
                if (-not (Wait-NasScheduledApply -NasRoot $NasRoot -ReleaseId $ReleaseId -NasEnv $nasEnv -Config $sshConfig)) {
                    throw "$DeploymentTargetName scheduled apply trigger was queued after SSH fallback, but release '$ReleaseId' was not applied within the timeout. Confirm the scheduled task runs auto-apply-release.sh and check ops/state/auto-apply.log."
                }
                $queuedForNasApply = $true
            }
            else {
                throw
            }
        }
    }
    elseif ($AllowScheduledApplyTrigger -or $scheduledApplyEnabled) {
        $queuedTriggerPath = Queue-NasScheduledApply -NasRoot $NasRoot -ReleaseId $ReleaseId -NasEnv $nasEnv -Config $sshConfig
        Write-Host "${LogPrefix}_apply_release_mode=scheduled-trigger pending_path=$queuedTriggerPath"
        if (-not (Wait-NasScheduledApply -NasRoot $NasRoot -ReleaseId $ReleaseId -NasEnv $nasEnv -Config $sshConfig)) {
            throw "$DeploymentTargetName scheduled apply trigger was queued but release '$ReleaseId' was not applied within the timeout. Confirm the scheduled task runs auto-apply-release.sh and check ops/state/auto-apply.log."
        }
        $queuedForNasApply = $true
    }
    elseif ($AllowLegacyLiveMirror) {
        Write-Warning "$DeploymentTargetName SSH settings are missing. Because AllowLegacyLiveMirror was specified, the script will only copy the release and mirror app\\live directly."
        Write-Warning "Recommended: configure SSH user/host/port/key/remote ops path for SSH apply, or enable scheduled apply with auto-apply-release.sh so $DeploymentTargetName can run apply-release.sh locally after the release is copied."
        Invoke-RobocopyMirror -Source $tempPublishRoot -Destination $liveRoot
    }
    else {
        throw "MirrorToLive was requested, but $DeploymentTargetName SSH settings are incomplete and scheduled apply is disabled. Configure SSH user/host/port/key/remote ops path, enable scheduled apply with auto-apply-release.sh, or use -AllowLegacyLiveMirror only as a temporary fallback."
    }
}

if ($MirrorToLive -and -not $SkipPostDeployOperationalGate.IsPresent) {
    $resolvedPostDeployBaseUrl = Resolve-OperationalGateBaseUrl `
        -PrimaryBaseUrl $PostDeployBaseUrl `
        -FallbackBaseUrl $PreDeployBaseUrl `
        -NasEnv $nasEnv

    Invoke-ReleaseOperationalGate `
        -Phase 'post-deploy' `
        -ProjectRoot $ProjectRoot `
        -BaseUrl $resolvedPostDeployBaseUrl `
        -SecretPath $PostDeploySecretPath `
        -PlatformStateRoot (Resolve-OperationalGatePlatformStateRoot -Root $NasRoot) `
        -OutputDirectory $PostDeployOutputDirectory `
        -AllowedIntegrityWarningCodes $PostDeployAllowedIntegrityWarningCodes `
        -ReleaseId $ReleaseId
}

if ($MirrorToLive -and $SkipPostDeployOperationalGate.IsPresent) {
    Write-Warning 'Post-deploy operational gate was skipped by request. Use only when a separate strict gate has already passed.'
}

if ($MirrorToLive -and (Test-Path -LiteralPath $NasRoot)) {
    $removedNasReleases = Remove-OldNasReleaseDirectories -ReleasesRoot (Join-Path $NasRoot 'releases') -CurrentReleaseId $ReleaseId -KeepReleaseCount $KeepNasReleaseCount
    if ($removedNasReleases.Count -gt 0) {
        Write-Host "${LogPrefix}_releases_pruned=$($removedNasReleases.Count)"
    }
}
elseif ($MirrorToLive -and (Test-NasSshConfigComplete -Config $sshConfig)) {
    $remoteBaseRoot = (($sshConfig.RemoteOpsPath -replace '/ops/?$', '').TrimEnd('/'))
    $removedRemoteReleases = Invoke-NasRemoteDirectoryPrune `
        -Config $sshConfig `
        -RemoteRoot $remoteBaseRoot `
        -RelativePath 'releases' `
        -Pattern '*' `
        -KeepCount $KeepNasReleaseCount `
        -Label 'releases'
    if ($removedRemoteReleases.Count -gt 0) {
        Write-Host "${LogPrefix}_remote_releases_pruned=$($removedRemoteReleases.Count)"
    }

    $removedRemoteBackups = Invoke-NasRemoteDirectoryPrune `
        -Config $sshConfig `
        -RemoteRoot $remoteBaseRoot `
        -RelativePath 'app/backups' `
        -Pattern 'live-*' `
        -KeepCount $KeepNasReleaseCount `
        -Label 'live-backups'
    if ($removedRemoteBackups.Count -gt 0) {
        Write-Host "${LogPrefix}_remote_live_backups_pruned=$($removedRemoteBackups.Count)"
    }
}

Remove-Item $tempPublishRoot -Recurse -Force -ErrorAction SilentlyContinue

$displayReleasePath = $releaseRoot
if (-not (Test-Path -LiteralPath $NasRoot) -and (Test-NasSshConfigComplete -Config $sshConfig)) {
    $displayReleasePath = (($sshConfig.RemoteOpsPath -replace '/ops/?$', '').TrimEnd('/') + "/releases/$ReleaseId")
}
Write-Host "publish_done release_id=$ReleaseId release_path=$displayReleasePath"
if ($MirrorToLive) {
    if ($appliedRemotely) {
        Write-Host "${LogPrefix}_apply_release_done release_id=$ReleaseId host=$($sshConfig.Host) user=$($sshConfig.User)"
    }
    elseif ($queuedForNasApply) {
        Write-Host "${LogPrefix}_apply_release_done release_id=$ReleaseId mode=scheduled-trigger pending_path=$queuedTriggerPath"
    }
    elseif ($AllowLegacyLiveMirror) {
        Write-Host "live_mirror_done live_path=$liveRoot"
    }
}
