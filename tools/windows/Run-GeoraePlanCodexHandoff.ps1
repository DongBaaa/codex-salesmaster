[CmdletBinding()]
param(
    [string]$LinuxHost = '192.168.0.199',
    [int]$LinuxPort = 2222,
    [string]$LinuxUser = 'itw',
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$RemoteHandoffRoot = '/home/itw/georaeplan-codex-handoff'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Text.UTF8Encoding]::new($false)

$expectedPublicKey = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBeckyM5refMgMS6K2AP8RpypbN/KyJV6t7dew0DuoB0'
$expectedHostKey = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAINnOyFmmdOR6R8/iJ7oPGwcGKN1v8sHPGo2o+bMpGBp6'
$localRoot = 'D:\GeoraePlan-Codex-Handoff'
$knownHostsPath = Join-Path $localRoot 'itwserver_known_hosts'
$workRoot = Join-Path $localRoot 'jobs'

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory=$true)][string]$Description,
        [Parameter(Mandatory=$true)][string]$FilePath,
        [Parameter(Mandatory=$true)][string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $FilePath @Arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode"
    }
}

function Get-RequiredCommandPath {
    param([Parameter(Mandatory=$true)][string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Required Windows OpenSSH command was not found: $Name"
    }
    return $command.Source
}

if (-not (Test-Path -LiteralPath 'D:\' -PathType Container)) {
    throw 'D: drive is required for the isolated GeoraePlan Windows handoff.'
}
if (-not (Test-Path -LiteralPath $LinuxSshKeyPath -PathType Leaf)) {
    throw "Linux SSH key was not found: $LinuxSshKeyPath"
}
if ($LinuxPort -ne 2222 -or $LinuxHost -ne '192.168.0.199' -or $LinuxUser -ne 'itw') {
    throw 'The handoff endpoint must remain pinned to itw@192.168.0.199:2222.'
}

$sshPath = Get-RequiredCommandPath -Name 'ssh.exe'
$scpPath = Get-RequiredCommandPath -Name 'scp.exe'
$sshKeygenPath = Get-RequiredCommandPath -Name 'ssh-keygen.exe'
New-Item -ItemType Directory -Force -Path $localRoot, $workRoot | Out-Null

$derivedPublicKey = (& $sshKeygenPath -y -f $LinuxSshKeyPath 2>$null | Select-Object -First 1).Trim()
if ($LASTEXITCODE -ne 0 -or $derivedPublicKey -ne $expectedPublicKey) {
    throw 'The configured Linux SSH private key does not match the pinned GeoraePlan Windows handoff key.'
}

$knownHostLine = "[$LinuxHost]:$LinuxPort $expectedHostKey"
[IO.File]::WriteAllText(
    $knownHostsPath,
    $knownHostLine + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

$commonOptions = @(
    '-i', $LinuxSshKeyPath,
    '-o', 'BatchMode=yes',
    '-o', 'IdentitiesOnly=yes',
    '-o', 'StrictHostKeyChecking=yes',
    '-o', "UserKnownHostsFile=$knownHostsPath",
    '-o', 'ConnectTimeout=15',
    '-o', 'LogLevel=ERROR'
)
$sshOptions = @('-p', $LinuxPort.ToString()) + $commonOptions
$scpOptions = @('-P', $LinuxPort.ToString()) + $commonOptions
$remote = "$LinuxUser@$LinuxHost"
$currentRemoteRoot = "$RemoteHandoffRoot/current"

$manifestTempPath = Join-Path $env:TEMP 'georaeplan-codex-handoff-current.json'
Invoke-NativeCommand `
    -Description 'Secure handoff manifest download' `
    -FilePath $scpPath `
    -Arguments ($scpOptions + @(($remote + ':' + $currentRemoteRoot + '/manifest.json'), $manifestTempPath))

$manifest = Get-Content -LiteralPath $manifestTempPath -Raw -Encoding UTF8 | ConvertFrom-Json
$jobId = [string]$manifest.jobId
$bundleFileName = [string]$manifest.bundleFileName
$bundleSha256 = ([string]$manifest.bundleSha256).ToUpperInvariant()
if ([int]$manifest.schemaVersion -ne 1) {
    throw 'Unsupported secure handoff manifest schema.'
}
if ($jobId -notmatch '^[a-z0-9][a-z0-9._-]{0,79}$') {
    throw 'The secure handoff jobId is invalid.'
}
if ($bundleFileName -notmatch '^[a-z0-9][a-z0-9._-]{0,99}\.zip$') {
    throw 'The secure handoff bundle file name is invalid.'
}
if ($bundleSha256 -notmatch '^[A-F0-9]{64}$') {
    throw 'The secure handoff bundle SHA-256 is invalid.'
}

$jobRoot = Join-Path $workRoot $jobId
$bundlePath = Join-Path $jobRoot $bundleFileName
$payloadRoot = Join-Path $jobRoot 'payload'
$uploadRoot = Join-Path $jobRoot 'upload'
if (Test-Path -LiteralPath $jobRoot) {
    Remove-Item -LiteralPath $jobRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $jobRoot, $payloadRoot, $uploadRoot | Out-Null

Invoke-NativeCommand `
    -Description 'Secure handoff bundle download' `
    -FilePath $scpPath `
    -Arguments ($scpOptions + @(($remote + ':' + $currentRemoteRoot + '/' + $bundleFileName), $bundlePath))
$actualBundleSha256 = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash
if ($actualBundleSha256 -ne $bundleSha256) {
    throw "Secure handoff bundle SHA-256 mismatch: $actualBundleSha256"
}
Expand-Archive -LiteralPath $bundlePath -DestinationPath $payloadRoot -Force
$runnerPath = Join-Path $payloadRoot 'run.ps1'
if (-not (Test-Path -LiteralPath $runnerPath -PathType Leaf)) {
    throw 'The verified secure handoff bundle does not contain run.ps1.'
}

$remoteIncomingRoot = "$RemoteHandoffRoot/incoming/$jobId"
Invoke-NativeCommand `
    -Description 'Secure handoff result directory preparation' `
    -FilePath $sshPath `
    -Arguments ($sshOptions + @($remote, "install -d -m 700 '$remoteIncomingRoot'"))

$runnerExitCode = 1
$uploadFailure = $null
try {
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & powershell.exe `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $runnerPath `
            -PayloadDirectory $payloadRoot `
            -UploadDirectory $uploadRoot
        $runnerExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}
finally {
    try {
        foreach ($file in @(Get-ChildItem -LiteralPath $uploadRoot -File -ErrorAction SilentlyContinue)) {
            if ($file.Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$') {
                throw "Unsafe result file name: $($file.Name)"
            }
            Invoke-NativeCommand `
                -Description "Secure result upload: $($file.Name)" `
                -FilePath $scpPath `
                -Arguments ($scpOptions + @($file.FullName, ($remote + ':' + $remoteIncomingRoot + '/' + $file.Name)))
        }
    }
    catch {
        $uploadFailure = $_
    }
}

if ($null -ne $uploadFailure) {
    throw $uploadFailure
}
if ($runnerExitCode -ne 0) {
    throw "GeoraePlan Codex handoff job failed with exit code $runnerExitCode"
}

Write-Host ''
Write-Host "Secure GeoraePlan Codex handoff completed: $jobId" -ForegroundColor Green
Write-Host "Local job directory: $jobRoot"
