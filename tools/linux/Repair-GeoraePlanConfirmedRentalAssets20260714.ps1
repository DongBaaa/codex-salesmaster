[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Rollback,
    [string]$ProjectRoot = '',
    [string]$LinuxSshHost = '192.168.0.199',
    [string]$LinuxSshUser = 'itw',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$RemoteRoot = '/srv/georaeplan',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'

if ($Apply -and $Rollback) {
    throw '-Apply and -Rollback cannot be used together.'
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
}
else {
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
}

if (-not (Test-Path -LiteralPath $LinuxSshKeyPath)) {
    throw "Linux SSH key not found: $LinuxSshKeyPath"
}

$ssh = 'C:\Windows\System32\OpenSSH\ssh.exe'
$scp = 'C:\Windows\System32\OpenSSH\scp.exe'
if (-not (Test-Path -LiteralPath $ssh) -or -not (Test-Path -LiteralPath $scp)) {
    throw 'Windows OpenSSH client is required.'
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $ProjectRoot "temp\operations\confirmed-rental-assets-20260714-$timestamp"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$logPath = Join-Path $OutputDirectory 'operation.log'

$filePrefix = Join-Path $PSScriptRoot 'Repair-GeoraePlanConfirmedRentalAssets20260714'
$localFiles = [ordered]@{
    ItworldApply   = "$filePrefix.itworld.sql"
    MainApply      = "$filePrefix.georaeplan.sql"
    ItworldVerify  = "$filePrefix.itworld.verify.sql"
    MainVerify     = "$filePrefix.georaeplan.verify.sql"
    ItworldRollback = "$filePrefix.itworld.rollback.sql"
    MainRollback   = "$filePrefix.georaeplan.rollback.sql"
}

foreach ($path in $localFiles.Values) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required SQL file not found: $path"
    }
}

$target = "$LinuxSshUser@$LinuxSshHost"
$sshBaseArgs = @(
    '-i', $LinuxSshKeyPath,
    '-p', $LinuxSshPort,
    '-o', 'BatchMode=yes',
    '-o', 'StrictHostKeyChecking=yes'
)
$scpBaseArgs = @(
    '-i', $LinuxSshKeyPath,
    '-P', $LinuxSshPort,
    '-o', 'BatchMode=yes',
    '-o', 'StrictHostKeyChecking=yes'
)

function Write-OperationLog {
    param([Parameter(Mandatory = $true)][string]$Message)
    $line = "[$(Get-Date -Format o)] $Message"
    $line | Tee-Object -FilePath $logPath -Append
}

function Invoke-Remote {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string]$Step
    )

    Write-OperationLog "$Step start"
    & $ssh @sshBaseArgs $target $Command 2>&1 | Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE. Log: $logPath"
    }
    Write-OperationLog "$Step complete"
}

function Invoke-RemoteSql {
    param(
        [Parameter(Mandatory = $true)][string]$Database,
        [Parameter(Mandatory = $true)][string]$RemotePath,
        [Parameter(Mandatory = $true)][string]$Step,
        [int]$ApplyValue = 0
    )

    $command = "docker exec -i georaeplan-postgres-1 psql -U georaeplan -d $Database -v ON_ERROR_STOP=1 -v apply=$ApplyValue -P pager=off < '$RemotePath'"
    Invoke-Remote -Command $command -Step $Step
}

Write-OperationLog "mode=$(if ($Rollback) { 'rollback' } elseif ($Apply) { 'apply' } else { 'dry-run' })"

$health = Invoke-RestMethod -Uri 'https://trade.2884.kr/healthz' -Method Get -TimeoutSec 30
if (-not $health) {
    throw 'trade.2884.kr health check returned an empty response.'
}
Write-OperationLog 'trade.2884.kr preflight health check passed'

Invoke-Remote -Step 'linux service preflight' -Command @"
set -euo pipefail
cd '$RemoteRoot'
test -f ops/docker-compose.yml
docker compose -f ops/docker-compose.yml ps api postgres
test "`$(docker inspect -f '{{.State.Running}}' georaeplan-api-1)" = true
test "`$(docker inspect -f '{{.State.Health.Status}}' georaeplan-postgres-1)" = healthy
df -Pk '$RemoteRoot'
"@

$remoteDirectory = "/tmp/georaeplan-confirmed-rental-assets-$timestamp"
Invoke-Remote -Command "set -euo pipefail; mkdir -p '$remoteDirectory'; chmod 700 '$remoteDirectory'" -Step 'create remote maintenance directory'

$remoteFiles = @{}
try {
    foreach ($entry in $localFiles.GetEnumerator()) {
        $remotePath = "$remoteDirectory/$([IO.Path]::GetFileName($entry.Value))"
        Write-OperationLog "upload $($entry.Key) start"
        & $scp @scpBaseArgs $entry.Value "${target}:$remotePath" 2>&1 | Tee-Object -FilePath $logPath -Append
        if ($LASTEXITCODE -ne 0) {
            throw "Upload failed for $($entry.Value). Log: $logPath"
        }
        $remoteFiles[$entry.Key] = $remotePath
        Write-OperationLog "upload $($entry.Key) complete"
    }

    if ($Rollback) {
        Invoke-RemoteSql -Database 'georaeplan' -RemotePath $remoteFiles.MainRollback -Step 'rollback georaeplan'
        Invoke-RemoteSql -Database 'georaeplan_itworld' -RemotePath $remoteFiles.ItworldRollback -Step 'rollback georaeplan_itworld'
        Write-OperationLog 'rollback completed'
        return
    }

    if (-not $Apply) {
        Invoke-RemoteSql -Database 'georaeplan_itworld' -RemotePath $remoteFiles.ItworldApply -Step 'dry-run georaeplan_itworld' -ApplyValue 0
        Invoke-RemoteSql -Database 'georaeplan' -RemotePath $remoteFiles.MainApply -Step 'dry-run georaeplan' -ApplyValue 0
        Write-OperationLog 'dry-run completed with rollback in both databases'
        return
    }

    $backupDirectory = "$RemoteRoot/backups/maintenance/$timestamp-confirmed-rental-assets"
    Invoke-Remote -Step 'create and validate database backups' -Command @"
set -euo pipefail
mkdir -p '$backupDirectory'
chmod 700 '$backupDirectory'
docker exec georaeplan-postgres-1 pg_dump -U georaeplan -d georaeplan -Fc > '$backupDirectory/georaeplan.dump'
docker exec georaeplan-postgres-1 pg_dump -U georaeplan -d georaeplan_itworld -Fc > '$backupDirectory/georaeplan_itworld.dump'
docker exec -i georaeplan-postgres-1 pg_restore -l < '$backupDirectory/georaeplan.dump' > /dev/null
docker exec -i georaeplan-postgres-1 pg_restore -l < '$backupDirectory/georaeplan_itworld.dump' > /dev/null
sha256sum '$backupDirectory/georaeplan.dump' '$backupDirectory/georaeplan_itworld.dump' | tee '$backupDirectory/SHA256SUMS'
chmod 600 '$backupDirectory/'*.dump '$backupDirectory/SHA256SUMS'
"@
    Write-OperationLog "backup_directory=$backupDirectory"

    $itworldApplied = $false
    $mainApplied = $false
    try {
        Invoke-RemoteSql -Database 'georaeplan_itworld' -RemotePath $remoteFiles.ItworldApply -Step 'apply georaeplan_itworld' -ApplyValue 1
        $itworldApplied = $true

        Invoke-RemoteSql -Database 'georaeplan' -RemotePath $remoteFiles.MainApply -Step 'apply georaeplan' -ApplyValue 1
        $mainApplied = $true

        Invoke-RemoteSql -Database 'georaeplan' -RemotePath $remoteFiles.MainVerify -Step 'verify georaeplan'
        Invoke-RemoteSql -Database 'georaeplan_itworld' -RemotePath $remoteFiles.ItworldVerify -Step 'verify georaeplan_itworld'
    }
    catch {
        Write-OperationLog "apply or verification failed: $($_.Exception.Message)"
        if ($mainApplied) {
            try {
                Invoke-RemoteSql -Database 'georaeplan' -RemotePath $remoteFiles.MainRollback -Step 'automatic rollback georaeplan'
            }
            catch {
                Write-OperationLog "automatic georaeplan rollback failed: $($_.Exception.Message)"
            }
        }
        if ($itworldApplied) {
            try {
                Invoke-RemoteSql -Database 'georaeplan_itworld' -RemotePath $remoteFiles.ItworldRollback -Step 'automatic rollback georaeplan_itworld'
            }
            catch {
                Write-OperationLog "automatic georaeplan_itworld rollback failed: $($_.Exception.Message)"
            }
        }
        throw
    }

    $postHealth = Invoke-RestMethod -Uri 'https://trade.2884.kr/healthz' -Method Get -TimeoutSec 30
    if (-not $postHealth) {
        throw 'trade.2884.kr post-apply health check returned an empty response.'
    }
    Write-OperationLog 'apply, verification, and post-apply health check completed'
}
finally {
    try {
        Invoke-Remote -Command "rm -rf '$remoteDirectory'" -Step 'remove remote maintenance directory'
    }
    catch {
        Write-OperationLog "remote maintenance directory cleanup warning: $($_.Exception.Message)"
    }
}
