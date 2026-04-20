[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$NasRoot = '\\192.0.2.10\docker\georaeplan',
    [string]$NasSshHost,
    [string]$NasSshUser = 'root',
    [int]$NasSshPort = 429,
    [string]$NasSshKeyPath,
    [string]$NasRemoteOpsPath = '/volume1/docker/georaeplan/ops',
    [switch]$UseSshFallback
)

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
}

$sourceNasRoot = Join-Path $ProjectRoot 'infra\nas'
$nasOpsRoot = Join-Path $NasRoot 'ops'

function Copy-TextFileLf {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $content = [System.IO.File]::ReadAllText($Source)
    $content = $content -replace "`r`n?", "`n"
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    $parent = Split-Path -Parent $Destination
    if ($parent) {
        New-Item -ItemType Directory -Force $parent | Out-Null
    }

    [System.IO.File]::WriteAllText($Destination, $content, $utf8NoBom)
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

function New-SshConfig {
    $defaultKeyPath = Join-Path $env:USERPROFILE '.ssh\tradeplan_nas_ed25519'
    $keyPath = Get-FirstConfiguredValue @(
        $NasSshKeyPath,
        $env:NAS_SSH_KEY_PATH,
        ($(if (Test-Path -LiteralPath $defaultKeyPath) { $defaultKeyPath } else { '' }))
    )

    if (-not [string]::IsNullOrWhiteSpace($keyPath) -and -not [System.IO.Path]::IsPathRooted($keyPath)) {
        $keyPath = Join-Path $env:USERPROFILE $keyPath
    }

    return [pscustomobject]@{
        Host = Get-FirstConfiguredValue @($NasSshHost, $env:NAS_SSH_HOST, (Get-NasHostFromRoot -NasRoot $NasRoot))
        User = Get-FirstConfiguredValue @($NasSshUser, $env:NAS_SSH_USER, 'root')
        Port = $(if ($NasSshPort -gt 0) { $NasSshPort } elseif ($env:NAS_SSH_PORT) { [int]$env:NAS_SSH_PORT } else { 429 })
        KeyPath = $keyPath
        RemoteOpsPath = Get-FirstConfiguredValue @($NasRemoteOpsPath, $env:NAS_REMOTE_OPS_PATH, '/volume1/docker/georaeplan/ops')
    }
}

function Test-SshConfigComplete {
    param([Parameter(Mandatory = $true)]$Config)

    return -not [string]::IsNullOrWhiteSpace($Config.Host) -and
           -not [string]::IsNullOrWhiteSpace($Config.User) -and
           -not [string]::IsNullOrWhiteSpace($Config.RemoteOpsPath) -and
           -not [string]::IsNullOrWhiteSpace($Config.KeyPath) -and
           (Test-Path -LiteralPath $Config.KeyPath)
}

function New-SshArgumentList {
    param([Parameter(Mandatory = $true)]$Config)

    $args = @(
        '-o', 'StrictHostKeyChecking=accept-new',
        '-o', 'ConnectTimeout=15'
    )

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
        [switch]$IgnoreExitCode
    )

    $sshExe = Resolve-SshExecutable
    $arguments = New-SshArgumentList -Config $Config
    $arguments += $Command

    $stdoutPath = Join-Path ([System.IO.Path]::GetTempPath()) ("georaeplan-sync-out-" + [Guid]::NewGuid().ToString('N') + '.log')
    $stderrPath = Join-Path ([System.IO.Path]::GetTempPath()) ("georaeplan-sync-err-" + [Guid]::NewGuid().ToString('N') + '.log')

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

function Invoke-SshTarUpload {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$RemoteDirectory,
        [Parameter(Mandatory = $true)]$Config
    )

    $tarExe = Resolve-TarExecutable
    $sshExe = Resolve-SshExecutable
    $archivePath = Join-Path ([System.IO.Path]::GetTempPath()) ("georaeplan-sync-" + [Guid]::NewGuid().ToString('N') + '.tar')

    try {
        & $tarExe -C $SourceDirectory -cf $archivePath .
        if ($LASTEXITCODE -ne 0) {
            throw "tar archive creation failed for $SourceDirectory"
        }

        $remoteCommand = "mkdir -p '$RemoteDirectory' && tar -xf - -C '$RemoteDirectory' && find '$RemoteDirectory' -maxdepth 1 -name '*.sh' -exec chmod +x {} +"

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

if (-not (Test-Path -LiteralPath $sourceNasRoot)) {
    throw "NAS config source not found: $sourceNasRoot"
}

if (Test-Path -LiteralPath $NasRoot) {
    New-Item -ItemType Directory -Force $nasOpsRoot | Out-Null

    Get-ChildItem -LiteralPath $sourceNasRoot -File | ForEach-Object {
        Copy-TextFileLf -Source $_.FullName -Destination (Join-Path $nasOpsRoot $_.Name)
    }

    if (-not (Test-Path -LiteralPath (Join-Path $nasOpsRoot '.env'))) {
        Write-Host "NAS .env is missing. Copy '$nasOpsRoot\\.env.example' to '$nasOpsRoot\\.env' and edit it first."
    }

    Write-Host "sync_done ops=$nasOpsRoot mode=unc"
    return
}

$sshConfig = New-SshConfig
if (-not $UseSshFallback -or -not (Test-SshConfigComplete -Config $sshConfig)) {
    throw "NAS config sync failed: UNC path '$NasRoot' is unavailable and SSH fallback is not ready."
}

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("georaeplan-nas-config-" + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force $stagingRoot | Out-Null
    Get-ChildItem -LiteralPath $sourceNasRoot -File | ForEach-Object {
        Copy-TextFileLf -Source $_.FullName -Destination (Join-Path $stagingRoot $_.Name)
    }

    Invoke-SshTarUpload -SourceDirectory $stagingRoot -RemoteDirectory $sshConfig.RemoteOpsPath -Config $sshConfig

    $envCheck = Invoke-SshCommand -Config $sshConfig -Command "test -f '$($sshConfig.RemoteOpsPath)/.env' && echo env_ok || echo env_missing" -IgnoreExitCode
    if ($envCheck.StdOut -match 'env_missing') {
        Write-Host "NAS .env is missing. Copy '$($sshConfig.RemoteOpsPath)/.env.example' to '$($sshConfig.RemoteOpsPath)/.env' and edit it first."
    }

    Write-Host "sync_done ops=$($sshConfig.RemoteOpsPath) mode=ssh"
}
finally {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
}
