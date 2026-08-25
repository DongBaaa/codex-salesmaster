[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$ExecutionRoot = "",
    [string]$EvidenceDirectory = "",
    [int]$StartingServerPort = 19080,
    [int]$TimeoutSeconds = 900,
    [switch]$ValidateSeedEnvironmentIsolationOnly
)

$ErrorActionPreference = "Stop"
foreach ($seedEnvironmentName in @(
    [Environment]::GetEnvironmentVariables("Process").Keys)) {
    if (([string]$seedEnvironmentName).StartsWith(
        "SeedUsers__",
        [System.StringComparison]::OrdinalIgnoreCase)) {
        [Environment]::SetEnvironmentVariable(
            [string]$seedEnvironmentName,
            $null,
            "Process")
    }
}
$credentialEnvironmentNames = @(
    "GEORAEPLAN_TEST_USERNAME",
    "GEORAEPLAN_TEST_PASSWORD"
)
$ephemeralBootstrapEnvironmentName =
    "GEORAEPLAN_TEST_EPHEMERAL_ADMIN_BOOTSTRAP"
$loginUsername =
    [Environment]::GetEnvironmentVariable("GEORAEPLAN_TEST_USERNAME", "Process")
$loginPassword =
    [Environment]::GetEnvironmentVariable("GEORAEPLAN_TEST_PASSWORD", "Process")
foreach ($credentialEnvironmentName in $credentialEnvironmentNames) {
    [Environment]::SetEnvironmentVariable(
        $credentialEnvironmentName,
        $null,
        "Process")
}
[Environment]::SetEnvironmentVariable(
    $ephemeralBootstrapEnvironmentName,
    $null,
    "Process")
$hasExternalLoginUsername =
    -not [string]::IsNullOrWhiteSpace($loginUsername)
$hasExternalLoginPassword =
    -not [string]::IsNullOrWhiteSpace($loginPassword)
$useEphemeralAdminBootstrap = $false
if ($hasExternalLoginUsername -xor $hasExternalLoginPassword) {
    $loginUsername = $null
    $loginPassword = $null
    throw "Multi-PC login credential configuration is incomplete."
}
if (-not $hasExternalLoginUsername) {
    $loginUsername = "admin"
    $useEphemeralAdminBootstrap = $true
}

$roleUiStartupTimeoutSeconds = 90
$roleInAppSelfTestTimeoutSeconds = 480
$serverReadyTimeoutSeconds = 90
$shutdownAndReleaseBudgetSeconds = 120
$minimumExternalTimeoutSeconds = [Math]::Max(
    900,
    $serverReadyTimeoutSeconds +
        $roleUiStartupTimeoutSeconds +
        $roleInAppSelfTestTimeoutSeconds +
        $shutdownAndReleaseBudgetSeconds)
if ($TimeoutSeconds -lt $minimumExternalTimeoutSeconds) {
    $loginUsername = $null
    $loginPassword = $null
    throw "TimeoutSeconds must cover the internal stage budget and be at least $minimumExternalTimeoutSeconds seconds."
}

$scriptRoot = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $scriptRoot
}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

if ([string]::IsNullOrWhiteSpace($ExecutionRoot)) {
    $ExecutionRoot = Join-Path $ProjectRoot "테스트 시행\실행환경"
}
$ExecutionRoot = [System.IO.Path]::GetFullPath($ExecutionRoot)

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $recordsRoot = Join-Path $ProjectRoot "테스트 시행\기록"
    $EvidenceDirectory = Join-Path $recordsRoot "multi-pc-desktop-e2e-$timestamp"
}
$EvidenceDirectory = [System.IO.Path]::GetFullPath($EvidenceDirectory)

function Get-NormalizedFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $normalizedCandidate = Get-NormalizedFullPath -Path $Candidate
    $normalizedRoot = Get-NormalizedFullPath -Path $Root
    return (
        [string]::Equals($normalizedCandidate, $normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $normalizedCandidate.StartsWith(
            $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase))
}

function Assert-NoReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $normalizedPath = Get-NormalizedFullPath -Path $Path
    $root = [System.IO.Path]::GetPathRoot($normalizedPath)
    if ([string]::IsNullOrWhiteSpace($root)) {
        throw "$Label 경로의 루트를 확인할 수 없습니다: $Path"
    }

    $current = $root
    $relative = $normalizedPath.Substring($root.Length)
    foreach ($segment in $relative.Split(
        [char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar),
        [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) {
            break
        }

        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label 경로에 reparse point가 포함되어 있습니다: $($item.FullName)"
        }
    }
}

function Get-FreeLoopbackPort {
    param([int]$StartingPort)

    for ($port = $StartingPort; $port -le 65535; $port++) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new(
                [System.Net.IPAddress]::Loopback,
                $port)
            $listener.Start()
            return $port
        }
        catch {
            continue
        }
        finally {
            if ($null -ne $listener) {
                try { $listener.Stop() } catch { }
            }
        }
    }

    throw "사용 가능한 loopback 포트를 찾지 못했습니다."
}

function Wait-LoopbackPortReleased {
    param(
        [int]$Port,
        [int]$TimeoutSeconds = 30
    )

    if ($Port -le 0) {
        return $true
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new(
                [System.Net.IPAddress]::Loopback,
                $Port)
            $listener.Start()
            return $true
        }
        catch {
            Start-Sleep -Milliseconds 100
        }
        finally {
            if ($null -ne $listener) {
                try { $listener.Stop() } catch { }
            }
        }
    }

    return $false
}

function Wait-DatabaseFileSetUnlocked {
    param(
        [Parameter(Mandatory = $true)][string]$DatabasePath,
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $leases = New-Object System.Collections.Generic.List[System.IO.FileStream]
        $allOpened = $true
        try {
            foreach ($suffix in @("", "-wal", "-shm", "-journal")) {
                $path = "$DatabasePath$suffix"
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                    continue
                }
                $leases.Add([System.IO.File]::Open(
                    $path,
                    [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::ReadWrite,
                    [System.IO.FileShare]::None))
            }
        }
        catch {
            $allOpened = $false
        }
        finally {
            foreach ($lease in $leases) {
                $lease.Dispose()
            }
        }

        if ($allOpened) {
            return $true
        }
        Start-Sleep -Milliseconds 100
    }

    return $false
}

function Wait-ReadyEndpoint {
    param(
        [Parameter(Mandatory = $true)][string]$ServerUrl,
        [Parameter(Mandatory = $true)][string]$ExpectedInstanceSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedCertificationId,
        [Parameter(Mandatory = $true)][string]$ExpectedServerDllSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedMarkerSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedAssemblyPathSha256,
        [Parameter(Mandatory = $true)][DateTimeOffset]$ContractCreatedAtUtc,
        [Parameter(Mandatory = $true)][DateTimeOffset]$ContractExpiresAtUtc,
        [int]$TimeoutSeconds = 90
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = ""
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest `
                -Uri ($ServerUrl.TrimEnd("/") + "/readyz") `
                -UseBasicParsing `
                -TimeoutSec 3
            if ($response.StatusCode -eq 200) {
                $payload = $response.Content | ConvertFrom-Json
                $attestation = $payload.testRuntimeAttestation
                $processId = if ($null -eq $attestation) { 0 } else { [int]$attestation.processId }
                $processStartTimeUtc = if (
                    $null -eq $attestation -or
                    [string]::IsNullOrWhiteSpace([string]$attestation.processStartTimeUtc)
                ) {
                    [DateTimeOffset]::MinValue
                }
                else {
                    [DateTimeOffset]::Parse(
                        [string]$attestation.processStartTimeUtc,
                        [System.Globalization.CultureInfo]::InvariantCulture,
                        [System.Globalization.DateTimeStyles]::RoundtripKind)
                }
                if (
                    [string]$payload.status -eq "ready" -and
                    [bool]$payload.databaseInitialization.completed -and
                    -not [bool]$payload.databaseInitialization.failed -and
                    $null -ne $attestation -and
                    [string]::Equals(
                        [string]$attestation.instanceSha256,
                        $ExpectedInstanceSha256,
                        [System.StringComparison]::OrdinalIgnoreCase) -and
                    [string]::Equals(
                        [string]$attestation.certificationId,
                        $ExpectedCertificationId,
                        [System.StringComparison]::Ordinal) -and
                    [string]::Equals(
                        [string]$attestation.serverDllSha256,
                        $ExpectedServerDllSha256,
                        [System.StringComparison]::OrdinalIgnoreCase) -and
                    [string]::Equals(
                        [string]$attestation.runtimeReadyMarkerSha256,
                        $ExpectedMarkerSha256,
                        [System.StringComparison]::OrdinalIgnoreCase) -and
                    [string]::Equals(
                        [string]$attestation.role,
                        "A",
                        [System.StringComparison]::Ordinal) -and
                    [string]::Equals(
                        [string]$attestation.assemblyPathSha256,
                        $ExpectedAssemblyPathSha256,
                        [System.StringComparison]::OrdinalIgnoreCase) -and
                    $processId -gt 0 -and
                    $processStartTimeUtc -ge $ContractCreatedAtUtc.AddSeconds(-5) -and
                    $processStartTimeUtc -le $ContractExpiresAtUtc) {
                    return $payload
                }
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }

        Start-Sleep -Milliseconds 500
    }

    throw "runner-owned test server readyz 대기 실패: $ServerUrl; lastError=$lastError"
}

function Get-DesktopAppExecutable {
    param([Parameter(Mandatory = $true)][string]$AppRoot)

    $candidate = @(
        Get-ChildItem -LiteralPath $AppRoot -File -Filter "*.Desktop.App.exe" -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $AppRoot -File -Filter "*.App.exe" -ErrorAction SilentlyContinue
    ) | Sort-Object FullName -Unique | Select-Object -First 1
    if ($null -eq $candidate) {
        throw "Desktop 실행 파일을 찾지 못했습니다: $AppRoot"
    }

    return $candidate.FullName
}

function Get-AppTreeSha256 {
    param([Parameter(Mandatory = $true)][string]$Root)

    Assert-NoReparsePoint -Path $Root -Label "App tree"
    $normalizedRoot = Get-NormalizedFullPath -Path $Root
    $prefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    $entries = New-Object System.Collections.Generic.List[string]
    foreach ($file in Get-ChildItem -LiteralPath $normalizedRoot -Recurse -File -Force | Sort-Object FullName) {
        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "App tree에 reparse point 파일이 포함되어 있습니다: $($file.FullName)"
        }

        $relativePath = $file.FullName.Substring($prefix.Length).Replace(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        $fileHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $entries.Add("$relativePath`t$fileHash")
    }

    $payload = [System.Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $entries))
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha256.ComputeHash($payload))).Replace("-", "")
    }
    finally {
        $sha256.Dispose()
    }
}

function Assert-NoRunningTestRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutionRoot,
        [Parameter(Mandatory = $true)][string]$MultiPcRoot
    )

    $runningDesktop = @(
        Get-Process -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    -not $_.HasExited -and
                    -not [string]::IsNullOrWhiteSpace($_.Path) -and
                    (Test-PathWithin -Candidate $_.Path -Root $ExecutionRoot) -and
                    $_.Path.EndsWith(".exe", [System.StringComparison]::OrdinalIgnoreCase) -and
                    [System.IO.Path]::GetFileName($_.Path).IndexOf(
                        "Desktop.App",
                        [System.StringComparison]::OrdinalIgnoreCase) -ge 0
                }
                catch {
                    $false
                }
            })
    if ($runningDesktop.Count -gt 0) {
        throw "테스트 실행환경 Desktop 프로세스가 이미 실행 중입니다. 먼저 정상 종료하세요. pid=$($runningDesktop.Id -join ',')"
    }

    try {
        $serverPathMarker = (Get-NormalizedFullPath -Path (Join-Path $ExecutionRoot "Server"))
        $runningServer = @(
            Get-CimInstance Win32_Process -ErrorAction Stop |
                Where-Object {
                    -not [string]::IsNullOrWhiteSpace($_.CommandLine) -and
                    $_.CommandLine.IndexOf($serverPathMarker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                    $_.CommandLine.IndexOf("Server.Api.dll", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
                })
        if ($runningServer.Count -gt 0) {
            throw "테스트 실행환경 서버 프로세스가 이미 실행 중입니다. 먼저 정상 종료하세요. pid=$($runningServer.ProcessId -join ',')"
        }
    }
    catch {
        if ($_.Exception.Message -like "테스트 실행환경 서버 프로세스가 이미 실행 중*") {
            throw
        }
        throw "테스트 서버 프로세스 사전 점검을 완료하지 못했습니다: $($_.Exception.Message)"
    }

    if (Test-Path -LiteralPath $MultiPcRoot) {
        Assert-NoReparsePoint -Path $MultiPcRoot -Label "MultiPC"
    }
}

function ConvertTo-EncodedPowerShell {
    param([Parameter(Mandatory = $true)][string]$Script)

    return [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($Script))
}

function ConvertTo-SingleQuotedLiteral {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    return "'" + $Value.Replace("'", "''") + "'"
}

function Remove-SeedUsersEnvironmentVariables {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.ProcessStartInfo]$StartInfo
    )

    foreach ($environmentName in @($StartInfo.EnvironmentVariables.Keys)) {
        if (([string]$environmentName).StartsWith(
            "SeedUsers__",
            [System.StringComparison]::OrdinalIgnoreCase)) {
            [void]$StartInfo.EnvironmentVariables.Remove(
                [string]$environmentName)
        }
    }
}

function Get-StringSha256 {
    param([Parameter(Mandatory = $true)][string]$Value)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return ([System.BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace("-", "")
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-NormalizedPathSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return Get-StringSha256 -Value (
        (Get-NormalizedFullPath -Path $Path).ToUpperInvariant())
}

function Assert-ServerProcessOwnedByRoleHost {
    param(
        [Parameter(Mandatory = $true)][object]$ReadyPayload,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$RoleHostProcess,
        [Parameter(Mandatory = $true)][string]$ExpectedServerDllPath
    )

    $attestation = $ReadyPayload.testRuntimeAttestation
    $serverProcessId = [int]$attestation.processId
    if ($serverProcessId -le 0) {
        throw "Server attestation process ID is invalid."
    }

    $serverProcess = Get-Process -Id $serverProcessId -ErrorAction Stop
    $attestedStartTime = [DateTimeOffset]::Parse(
        [string]$attestation.processStartTimeUtc,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind)
    $actualStartTime = [DateTimeOffset]$serverProcess.StartTime.ToUniversalTime()
    if ([Math]::Abs(($actualStartTime - $attestedStartTime).TotalSeconds) -gt 2) {
        throw "Server attestation start time does not match the running process."
    }

    $allProcesses = @(Get-CimInstance Win32_Process -ErrorAction Stop)
    $serverCim = $allProcesses |
        Where-Object { [int]$_.ProcessId -eq $serverProcessId } |
        Select-Object -First 1
    if (
        $null -eq $serverCim -or
        [string]::IsNullOrWhiteSpace([string]$serverCim.CommandLine) -or
        ([string]$serverCim.CommandLine).IndexOf(
            (Get-NormalizedFullPath -Path $ExpectedServerDllPath),
            [System.StringComparison]::OrdinalIgnoreCase) -lt 0
    ) {
        throw "Attested server process command line does not contain the certified Server.Api DLL."
    }

    $processById = @{}
    foreach ($processInfo in $allProcesses) {
        $processById[[int]$processInfo.ProcessId] = $processInfo
    }

    $currentId = $serverProcessId
    $owned = $false
    for ($depth = 0; $depth -lt 32; $depth++) {
        if (-not $processById.ContainsKey($currentId)) {
            break
        }

        $parentId = [int]$processById[$currentId].ParentProcessId
        if ($parentId -eq $RoleHostProcess.Id) {
            $owned = $true
            break
        }
        if ($parentId -le 0 -or $parentId -eq $currentId) {
            break
        }
        $currentId = $parentId
    }

    if (-not $owned) {
        throw "Attested server process is not a descendant of the runner-owned PC-A role host."
    }

    return [pscustomobject]@{
        ProcessId = $serverProcessId
        StartTimeUtcTicks = $serverProcess.StartTime.ToUniversalTime().Ticks
    }
}

function Read-RuntimeCertificationMarker {
    param(
        [Parameter(Mandatory = $true)][string]$RuntimeRoot,
        [Parameter(Mandatory = $true)][string]$ServerDirectory
    )

    $markerPath = Join-Path $RuntimeRoot ".georaeplan-runtime-ready"
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "인증된 테스트 runtime marker가 없습니다: $markerPath"
    }

    $values = @{}
    foreach ($rawLine in Get-Content -LiteralPath $markerPath -Encoding UTF8) {
        $line = ([string]$rawLine).Trim()
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $separatorIndex = $line.IndexOf("=")
        if ($separatorIndex -le 0 -or $separatorIndex -eq ($line.Length - 1)) {
            throw "테스트 runtime marker 형식이 올바르지 않습니다."
        }

        $key = $line.Substring(0, $separatorIndex).Trim()
        $value = $line.Substring($separatorIndex + 1).Trim()
        if ($values.ContainsKey($key)) {
            throw "테스트 runtime marker에 중복 키가 있습니다: $key"
        }
        $values[$key] = $value
    }

    foreach ($requiredKey in @(
        "runtime_ready",
        "runtime_root",
        "runtime_physical_root",
        "certification_id",
        "server_dll_sha256")) {
        if (-not $values.ContainsKey($requiredKey) -or [string]::IsNullOrWhiteSpace([string]$values[$requiredKey])) {
            throw "테스트 runtime marker 필수 키가 없습니다: $requiredKey"
        }
    }

    if (
        -not [bool]::Parse([string]$values.runtime_ready) -or
        -not [string]::Equals(
            (Get-NormalizedFullPath -Path ([string]$values.runtime_root)),
            (Get-NormalizedFullPath -Path $RuntimeRoot),
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            (Get-NormalizedFullPath -Path ([string]$values.runtime_physical_root)),
            (Get-NormalizedFullPath -Path $RuntimeRoot),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "테스트 runtime marker가 현재 실행환경 루트와 일치하지 않습니다."
    }

    $serverDll = Get-ChildItem -LiteralPath $ServerDirectory -File -Filter "*.Server.Api.dll" |
        Select-Object -First 1
    if ($null -eq $serverDll) {
        throw "인증 대상 Server.Api DLL이 없습니다: $ServerDirectory"
    }
    $actualServerDllHash = (Get-FileHash -LiteralPath $serverDll.FullName -Algorithm SHA256).Hash
    if (-not [string]::Equals(
        $actualServerDllHash,
        [string]$values.server_dll_sha256,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "현재 Server.Api DLL hash가 runtime certification marker와 다릅니다."
    }

    return [pscustomobject]@{
        MarkerPath = $markerPath
        MarkerSha256 = (Get-FileHash -LiteralPath $markerPath -Algorithm SHA256).Hash
        CertificationId = [string]$values.certification_id
        ServerDllSha256 = $actualServerDllHash
        ServerDllPath = $serverDll.FullName
    }
}

function Start-RoleHost {
    param(
        [Parameter(Mandatory = $true)][string]$Role,
        [Parameter(Mandatory = $true)][string]$AppExe,
        [Parameter(Mandatory = $true)][string]$AppDataRoot,
        [Parameter(Mandatory = $true)][string]$TempRoot,
        [Parameter(Mandatory = $true)][string]$DownloadsRoot,
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)][string]$Nonce,
        [Parameter(Mandatory = $true)][string]$RuntimeRoot,
        [Parameter(Mandatory = $true)][string]$CertificationId,
        [Parameter(Mandatory = $true)][string]$UiSmokeScript,
        [Parameter(Mandatory = $true)][string]$RoleEvidenceDirectory,
        [Parameter(Mandatory = $true)][string]$InAppReportPath,
        [Parameter(Mandatory = $true)][string]$LoginInputMutexName,
        [Parameter(Mandatory = $true)][string]$LoginUsername,
        [Parameter(Mandatory = $true)][string]$LoginPassword,
        [Parameter(Mandatory = $true)][int]$UiStartupTimeoutSec,
        [Parameter(Mandatory = $true)][int]$InAppSelfTestTimeoutSec,
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$ServerDirectory,
        [Parameter(Mandatory = $true)][string]$ServerDataRoot,
        [Parameter(Mandatory = $true)][int]$ServerPort,
        [Parameter(Mandatory = $true)][bool]$StartServer,
        [Parameter(Mandatory = $true)][bool]$EnableEphemeralAdminBootstrap,
        [string]$BootstrapContractPath = "",
        [string]$BootstrapContractSha256 = ""
    )

    if ($EnableEphemeralAdminBootstrap -and (-not $StartServer -or $Role -ne "A")) {
        throw "Ephemeral admin bootstrap is restricted to the isolated PC-A server host."
    }

    New-Item -ItemType Directory -Path $RoleEvidenceDirectory -Force | Out-Null
    $stdoutPath = Join-Path $RoleEvidenceDirectory "role-host.stdout.log"
    $stderrPath = Join-Path $RoleEvidenceDirectory "role-host.stderr.log"

    $roleLiteral = ConvertTo-SingleQuotedLiteral -Value $Role
    $appExeLiteral = ConvertTo-SingleQuotedLiteral -Value $AppExe
    $appDataLiteral = ConvertTo-SingleQuotedLiteral -Value $AppDataRoot
    $tempLiteral = ConvertTo-SingleQuotedLiteral -Value $TempRoot
    $downloadsLiteral = ConvertTo-SingleQuotedLiteral -Value $DownloadsRoot
    $runRootLiteral = ConvertTo-SingleQuotedLiteral -Value $RunRoot
    $nonceLiteral = ConvertTo-SingleQuotedLiteral -Value $Nonce
    $runtimeRootLiteral = ConvertTo-SingleQuotedLiteral -Value $RuntimeRoot
    $certificationIdLiteral = ConvertTo-SingleQuotedLiteral -Value $CertificationId
    $uiSmokeLiteral = ConvertTo-SingleQuotedLiteral -Value $UiSmokeScript
    $roleEvidenceLiteral = ConvertTo-SingleQuotedLiteral -Value $RoleEvidenceDirectory
    $inAppReportLiteral = ConvertTo-SingleQuotedLiteral -Value $InAppReportPath
    $loginInputMutexLiteral = ConvertTo-SingleQuotedLiteral -Value $LoginInputMutexName
    $dotnetLiteral = ConvertTo-SingleQuotedLiteral -Value $DotnetExe
    $serverDirectoryLiteral = ConvertTo-SingleQuotedLiteral -Value $ServerDirectory
    $serverDataRootLiteral = ConvertTo-SingleQuotedLiteral -Value $ServerDataRoot
    $bootstrapContractPathLiteral = ConvertTo-SingleQuotedLiteral -Value $BootstrapContractPath
    $bootstrapContractSha256Literal = ConvertTo-SingleQuotedLiteral -Value $BootstrapContractSha256
    $startServerLiteral = if ($StartServer) { '$true' } else { '$false' }

    $roleScript = @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'
foreach (`$seedEnvironmentName in @(
    [Environment]::GetEnvironmentVariables('Process').Keys)) {
    if (([string]`$seedEnvironmentName).StartsWith(
        'SeedUsers__',
        [System.StringComparison]::OrdinalIgnoreCase)) {
        [Environment]::SetEnvironmentVariable(
            [string]`$seedEnvironmentName,
            `$null,
            'Process')
    }
}
`$roleLoginUsername = [Environment]::GetEnvironmentVariable('GEORAEPLAN_TEST_USERNAME', 'Process')
`$roleLoginPassword = [Environment]::GetEnvironmentVariable('GEORAEPLAN_TEST_PASSWORD', 'Process')
`$roleEphemeralAdminBootstrap = [string]::Equals(
    [Environment]::GetEnvironmentVariable('$ephemeralBootstrapEnvironmentName', 'Process'),
    '1',
    [System.StringComparison]::Ordinal)
[Environment]::SetEnvironmentVariable('GEORAEPLAN_TEST_USERNAME', `$null, 'Process')
[Environment]::SetEnvironmentVariable('GEORAEPLAN_TEST_PASSWORD', `$null, 'Process')
[Environment]::SetEnvironmentVariable('$ephemeralBootstrapEnvironmentName', `$null, 'Process')
if (
    [string]::IsNullOrWhiteSpace(`$roleLoginUsername) -or
    [string]::IsNullOrWhiteSpace(`$roleLoginPassword)
) {
    throw 'Role host did not receive both login credential values.'
}
`$env:GEORAEPLAN_MULTI_PC_E2E_ROLE = $roleLiteral
`$env:GEORAEPLAN_MULTI_PC_E2E_RUN_ROOT = $runRootLiteral
`$env:GEORAEPLAN_MULTI_PC_E2E_NONCE = $nonceLiteral
`$env:GEORAEPLAN_MULTI_PC_RUNTIME_ROOT = $runtimeRootLiteral
`$env:GEORAEPLAN_MULTI_PC_CERTIFICATION_ID = $certificationIdLiteral
`$env:GEORAEPLAN_TEMP_ROOT = $tempLiteral
`$env:GEORAEPLAN_DOWNLOADS_ROOT = $downloadsLiteral
`$invokeParameters = @{
    AppExe = $appExeLiteral
    EvidenceDirectory = $roleEvidenceLiteral
    TimeoutSec = $UiStartupTimeoutSec
    InAppSelfTestTimeoutSec = $InAppSelfTestTimeoutSec
    AppDataRoot = $appDataLiteral
    UseInAppSelfTest = `$true
    InAppSelfTestReportPath = $inAppReportLiteral
    VerifyPriorityWindowsWithInAppSelfTest = `$true
    MultiPcUiRole = $roleLiteral
    MultiPcUiBarrierRoot = $runRootLiteral
    LoginInputMutexName = $loginInputMutexLiteral
    Username = `$roleLoginUsername
    Password = `$roleLoginPassword
    DotnetExe = $dotnetLiteral
    ServerDir = $serverDirectoryLiteral
    ServerDataRoot = $serverDataRootLiteral
    ServerPort = $ServerPort
    StartServer = $startServerLiteral
}
if (`$roleEphemeralAdminBootstrap) {
    `$invokeParameters.EnableEphemeralAdminBootstrap = `$true
    `$invokeParameters.EphemeralAdminPassword = `$roleLoginPassword
    `$invokeParameters.BootstrapContractPath = $bootstrapContractPathLiteral
    `$invokeParameters.BootstrapContractSha256 = $bootstrapContractSha256Literal
}
try {
    & $uiSmokeLiteral @invokeParameters 6>&1
}
finally {
    `$invokeParameters.Username = `$null
    `$invokeParameters.Password = `$null
    `$invokeParameters.EphemeralAdminPassword = `$null
    `$roleLoginUsername = `$null
    `$roleLoginPassword = `$null
    `$roleEphemeralAdminBootstrap = `$false
}
"@

    $encoded = ConvertTo-EncodedPowerShell -Script $roleScript
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "powershell.exe"
    $startInfo.Arguments =
        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    Remove-SeedUsersEnvironmentVariables -StartInfo $startInfo
    [void]$startInfo.EnvironmentVariables.Remove(
        $ephemeralBootstrapEnvironmentName)
    $startInfo.EnvironmentVariables["GEORAEPLAN_TEST_USERNAME"] = $LoginUsername
    $startInfo.EnvironmentVariables["GEORAEPLAN_TEST_PASSWORD"] = $LoginPassword
    if ($EnableEphemeralAdminBootstrap) {
        $startInfo.EnvironmentVariables[$ephemeralBootstrapEnvironmentName] = "1"
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $processStarted = $false
    try {
        if (-not $process.Start()) {
            throw "Role host process did not start."
        }
        $processStarted = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
    }
    catch {
        if ($processStarted -and -not $process.HasExited) {
            try {
                $process.Kill()
                [void]$process.WaitForExit(10000)
            }
            catch { }
        }
        throw
    }
    finally {
        [void]$startInfo.EnvironmentVariables.Remove(
            "GEORAEPLAN_TEST_USERNAME")
        [void]$startInfo.EnvironmentVariables.Remove(
            "GEORAEPLAN_TEST_PASSWORD")
        [void]$startInfo.EnvironmentVariables.Remove(
            $ephemeralBootstrapEnvironmentName)
        $LoginUsername = $null
        $LoginPassword = $null
    }

    return [pscustomobject]@{
        Role = $Role
        Process = $process
        ProcessStartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks
        StdoutPath = $stdoutPath
        StderrPath = $stderrPath
        StdoutTask = $stdoutTask
        StderrTask = $stderrTask
        OutputCaptured = $false
        EvidenceDirectory = $RoleEvidenceDirectory
        InAppReportPath = $InAppReportPath
        UiSmokeJsonPath = ""
    }
}

function Complete-RoleHostOutputCapture {
    param(
        [Parameter(Mandatory = $true)][object]$RoleHost,
        [ValidateRange(100, 30000)][int]$TimeoutMilliseconds = 5000
    )

    if ([bool]$RoleHost.OutputCaptured) {
        return
    }

    try {
        $RoleHost.Process.Refresh()
    }
    catch {
        throw "role process 상태를 확인할 수 없습니다."
    }
    if (-not $RoleHost.Process.HasExited) {
        throw "role process가 아직 실행 중이어서 출력을 수집할 수 없습니다."
    }

    $drainStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $stdoutWaitMilliseconds = [Math]::Max(
        0,
        $TimeoutMilliseconds - [int]$drainStopwatch.ElapsedMilliseconds)
    if (-not $RoleHost.StdoutTask.Wait($stdoutWaitMilliseconds)) {
        throw "role stdout 수집 제한 시간을 초과했습니다."
    }
    $stderrWaitMilliseconds = [Math]::Max(
        0,
        $TimeoutMilliseconds - [int]$drainStopwatch.ElapsedMilliseconds)
    if (-not $RoleHost.StderrTask.Wait($stderrWaitMilliseconds)) {
        throw "role stderr 수집 제한 시간을 초과했습니다."
    }

    $stdoutText = $RoleHost.StdoutTask.GetAwaiter().GetResult()
    $stderrText = $RoleHost.StderrTask.GetAwaiter().GetResult()
    $utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText(
        $RoleHost.StdoutPath,
        [string]$stdoutText,
        $utf8WithoutBom)
    [System.IO.File]::WriteAllText(
        $RoleHost.StderrPath,
        [string]$stderrText,
        $utf8WithoutBom)
    $stdoutText = $null
    $stderrText = $null
    $RoleHost.OutputCaptured = $true
}

function Wait-RoleHosts {
    param(
        [Parameter(Mandatory = $true)][object[]]$RoleHosts,
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $running = @($RoleHosts | Where-Object {
            try {
                $_.Process.Refresh()
                -not $_.Process.HasExited
            }
            catch {
                $false
            }
        })
        if ($running.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 500
    }

    $stillRunning = @($RoleHosts | Where-Object {
        try {
            $_.Process.Refresh()
            -not $_.Process.HasExited
        }
        catch {
            $false
        }
    })
    throw "Multi-PC role host timeout: roles=$($stillRunning.Role -join ',')"
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "필수 JSON 증거가 없습니다: $Path"
    }
    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Wait-MultiPcJsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateRange(1, 600)][int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)][scriptblock]$Predicate
    )

    $deadline = (Get-Date).ToUniversalTime().AddSeconds($TimeoutSeconds)
    $lastError = $null
    while ((Get-Date).ToUniversalTime() -lt $deadline) {
        try {
            if (Test-Path -LiteralPath $Path -PathType Leaf) {
                $payload = Read-JsonFile -Path $Path
                if (& $Predicate $payload) {
                    return $payload
                }
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }

        Start-Sleep -Milliseconds 100
    }

    $detail = if ([string]::IsNullOrWhiteSpace([string]$lastError)) {
        ""
    }
    else {
        "; lastError=$lastError"
    }
    throw "Multi-PC coordination JSON wait timeout: $Path$detail"
}

function Write-MultiPcJsonAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Payload,
        [ValidateRange(2, 20)][int]$Depth = 10
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        throw "Atomic JSON target directory does not exist: $directory"
    }
    if (Test-Path -LiteralPath $Path) {
        throw "Atomic JSON evidence target already exists: $Path"
    }

    $tempPath = Join-Path $directory (
        ".$([System.IO.Path]::GetFileName($Path)).$PID.$([Guid]::NewGuid().ToString('N')).tmp")
    try {
        $json = $Payload | ConvertTo-Json -Depth $Depth
        [System.IO.File]::WriteAllText(
            $tempPath,
            $json,
            [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::Move($tempPath, $Path)
    }
    finally {
        if (Test-Path -LiteralPath $tempPath -PathType Leaf) {
            Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-UiaRuntimeIdText {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Element
    )

    $runtimeId = @($Element.GetRuntimeId())
    if ($runtimeId.Count -eq 0) {
        throw "UIA element runtime ID is empty."
    }
    return [string]::Join(".", $runtimeId)
}

function Get-InventoryTransferListUiaObservation {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][long]$ExpectedProcessStartTimeUtcTicks,
        [AllowEmptyString()][string]$ExpectedTransferAutomationId = ""
    )

    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    $process.Refresh()
    if (
        $process.HasExited -or
        $process.StartTime.ToUniversalTime().Ticks -ne $ExpectedProcessStartTimeUtcTicks
    ) {
        throw "PC-B Desktop process identity changed before UIA observation."
    }

    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    $automationIdCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        "InventoryTransferWindow")
    $windowTypeCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $windowIdentityCondition = [System.Windows.Automation.AndCondition]::new(
        $processCondition,
        $automationIdCondition,
        $windowTypeCondition)
    $inventoryTransferWindows = @(
        [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $windowIdentityCondition)
    )
    if ($inventoryTransferWindows.Count -ne 1) {
        throw "PC-B visible InventoryTransferWindow UIA identity count must be exactly one; actual=$($inventoryTransferWindows.Count)."
    }

    $window = $inventoryTransferWindows[0]
    $windowRect = $window.Current.BoundingRectangle
    if (
        $window.Current.IsOffscreen -or
        $window.Current.NativeWindowHandle -le 0 -or
        $windowRect.Width -le 0 -or
        $windowRect.Height -le 0
    ) {
        throw "PC-B InventoryTransferWindow exists but is not visibly rendered."
    }

    $gridCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        "TransferListGrid")
    $grids = @($window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $gridCondition))
    if ($grids.Count -ne 1) {
        throw "PC-B TransferListGrid UIA identity count must be exactly one; actual=$($grids.Count)."
    }

    $grid = $grids[0]
    $gridRect = $grid.Current.BoundingRectangle
    if (
        $grid.Current.IsOffscreen -or
        $gridRect.Width -le 0 -or
        $gridRect.Height -le 0
    ) {
        throw "PC-B TransferListGrid exists but is not visibly rendered."
    }

    $gridPattern = $null
    if (-not $grid.TryGetCurrentPattern(
        [System.Windows.Automation.GridPattern]::Pattern,
        [ref]$gridPattern)) {
        throw "PC-B TransferListGrid does not expose GridPattern row count."
    }
    $rowCount = [int]$gridPattern.Current.RowCount
    if ($rowCount -lt 0) {
        throw "PC-B TransferListGrid returned an invalid row count."
    }

    $matchingRows = @()
    if (-not [string]::IsNullOrWhiteSpace($ExpectedTransferAutomationId)) {
        $rowCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $ExpectedTransferAutomationId)
        $matchingRows = @(
            $grid.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $rowCondition) |
                Where-Object {
                    $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::DataItem -and
                    -not $_.Current.IsOffscreen
                }
        )
        if ($matchingRows.Count -ne 1) {
            throw "PC-B visible transfer row UIA identity count must be exactly one; id=$ExpectedTransferAutomationId; actual=$($matchingRows.Count)."
        }
    }

    return [ordered]@{
        Classification = "out-of-process UIAutomationClient observation"
        ObserverProcessId = $PID
        ProcessId = $ProcessId
        ProcessStartTimeUtcTicks = $ExpectedProcessStartTimeUtcTicks
        CapturedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
        WindowAutomationId = $window.Current.AutomationId
        WindowName = $window.Current.Name
        WindowNativeHandle = [long]$window.Current.NativeWindowHandle
        WindowRuntimeId = Get-UiaRuntimeIdText -Element $window
        WindowIsOffscreen = [bool]$window.Current.IsOffscreen
        ListAutomationId = $grid.Current.AutomationId
        ListRuntimeId = Get-UiaRuntimeIdText -Element $grid
        ListIsOffscreen = [bool]$grid.Current.IsOffscreen
        RowCount = $rowCount
        TransferAutomationId = $ExpectedTransferAutomationId
        VisibleTransferRowCount = $matchingRows.Count
    }
}

function Wait-InventoryTransferListUiaObservation {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][long]$ExpectedProcessStartTimeUtcTicks,
        [AllowEmptyString()][string]$ExpectedTransferAutomationId = "",
        [int]$TimeoutSeconds = 15
    )

    $wait = [System.Diagnostics.Stopwatch]::StartNew()
    $lastObservationError = ""
    while ($wait.Elapsed -lt [TimeSpan]::FromSeconds($TimeoutSeconds)) {
        try {
            return Get-InventoryTransferListUiaObservation `
                -ProcessId $ProcessId `
                -ExpectedProcessStartTimeUtcTicks $ExpectedProcessStartTimeUtcTicks `
                -ExpectedTransferAutomationId $ExpectedTransferAutomationId
        }
        catch {
            $lastObservationError = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 100
    }
    $wait.Stop()

    throw (
        "Timed out waiting for the PC-B visible InventoryTransferWindow UIA observation; " +
        "processId=$ProcessId; timeoutSeconds=$TimeoutSeconds; " +
        "lastObservation=$lastObservationError")
}

function Invoke-InventoryTransferListUiaGate {
    param(
        [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
        [Parameter(Mandatory = $true)][string]$RunId,
        [Parameter(Mandatory = $true)][string]$Nonce,
        [Parameter(Mandatory = $true)][object]$RoleA,
        [Parameter(Mandatory = $true)][object]$RoleB,
        [Parameter(Mandatory = $true)][string]$ExpectedAppAExe,
        [Parameter(Mandatory = $true)][string]$ExpectedAppBExe,
        [Parameter(Mandatory = $true)][string]$EvidencePath
    )

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $sessionA = Wait-MultiPcJsonFile `
        -Path (Join-Path $EvidenceDirectory "session-A.json") `
        -TimeoutSeconds 180 `
        -Predicate {
            param($payload)
            [string]$payload.RunId -eq $RunId -and
            [string]$payload.Nonce -eq $Nonce -and
            [string]$payload.Role -eq "A" -and
            [int]$payload.ProcessId -gt 0
        }
    $sessionB = Wait-MultiPcJsonFile `
        -Path (Join-Path $EvidenceDirectory "session-B.json") `
        -TimeoutSeconds 180 `
        -Predicate {
            param($payload)
            [string]$payload.RunId -eq $RunId -and
            [string]$payload.Nonce -eq $Nonce -and
            [string]$payload.Role -eq "B" -and
            [int]$payload.ProcessId -gt 0
        }
    $appAProcessId = [int]$sessionA.ProcessId
    $appBProcessId = [int]$sessionB.ProcessId
    if ($appAProcessId -eq $appBProcessId) {
        throw "PC-A and PC-B Desktop process IDs are not distinct."
    }

    $appAProcess = Get-Process -Id $appAProcessId -ErrorAction Stop
    $appBProcess = Get-Process -Id $appBProcessId -ErrorAction Stop
    if (
        -not [string]::Equals(
            (Get-NormalizedFullPath -Path $appAProcess.Path),
            (Get-NormalizedFullPath -Path $ExpectedAppAExe),
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            (Get-NormalizedFullPath -Path $appBProcess.Path),
            (Get-NormalizedFullPath -Path $ExpectedAppBExe),
            [System.StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "PC-A/PC-B session PID executable path does not match the isolated App clone."
    }
    $appAStartTicks = $appAProcess.StartTime.ToUniversalTime().Ticks
    $appBStartTicks = $appBProcess.StartTime.ToUniversalTime().Ticks

    $readySignal = Wait-MultiPcJsonFile `
        -Path (Join-Path $EvidenceDirectory "transfer-b-list-ready.json") `
        -TimeoutSeconds 420 `
        -Predicate {
            param($payload)
            [string]$payload.RunId -eq $RunId -and
            [string]$payload.Nonce -eq $Nonce -and
            [string]$payload.Role -eq "B" -and
            [int]$payload.ProcessId -eq $appBProcessId -and
            [Guid]$payload.InventoryTransferId -eq [Guid]::Empty -and
            [long]$payload.Revision -eq 0 -and
            [int]$payload.BeforeRowCount -ge 0 -and
            [int]$payload.AfterRowCount -eq [int]$payload.BeforeRowCount -and
            [long]$payload.WindowNativeHandle -gt 0 -and
            [bool]$payload.RealtimeRevisionMonitorActive
        }

    $before = Wait-InventoryTransferListUiaObservation `
        -ProcessId $appBProcessId `
        -ExpectedProcessStartTimeUtcTicks $appBStartTicks `
        -TimeoutSeconds 15
    if (
        [long]$before.WindowNativeHandle -ne [long]$readySignal.WindowNativeHandle -or
        [int]$before.RowCount -ne [int]$readySignal.BeforeRowCount
    ) {
        throw "PC-B before-create UIA observation disagrees with the in-process list readiness signal."
    }

    $beforeGatePath = Join-Path $EvidenceDirectory "transfer-b-list-uia-ready.json"
    $beforeGate = [ordered]@{
        RunId = $RunId
        Nonce = $Nonce
        Role = "RUNNER"
        ProcessId = $PID
        CapturedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
        Phase = "before-create"
        TargetRole = "B"
        TargetProcessId = $appBProcessId
        WindowNativeHandle = [long]$before.WindowNativeHandle
        WindowAutomationId = [string]$before.WindowAutomationId
        WindowRuntimeId = [string]$before.WindowRuntimeId
        ListAutomationId = [string]$before.ListAutomationId
        ListRuntimeId = [string]$before.ListRuntimeId
        BeforeRowCount = [int]$before.RowCount
        AfterRowCount = [int]$before.RowCount
        InventoryTransferId = [Guid]::Empty
        ServerRevision = 0
    }
    Write-MultiPcJsonAtomic -Path $beforeGatePath -Payload $beforeGate

    $createdSignal = Wait-MultiPcJsonFile `
        -Path (Join-Path $EvidenceDirectory "transfer-a-created.json") `
        -TimeoutSeconds 120 `
        -Predicate {
            param($payload)
            [string]$payload.RunId -eq $RunId -and
            [string]$payload.Nonce -eq $Nonce -and
            [string]$payload.Role -eq "A" -and
            [int]$payload.ProcessId -eq $appAProcessId -and
            [Guid]$payload.InventoryTransferId -ne [Guid]::Empty -and
            [long]$payload.Revision -gt 0 -and
            [DateTimeOffset]$payload.CapturedAtUtc -gt [DateTimeOffset]$beforeGate.CapturedAtUtc
        }
    $transferId = [Guid]$createdSignal.InventoryTransferId
    $serverRevision = [long]$createdSignal.Revision
    $expectedTransferAutomationId = $transferId.ToString("D")
    $revisionWait = [System.Diagnostics.Stopwatch]::StartNew()
    $after = $null
    $lastObservationError = ""
    while ($revisionWait.Elapsed -lt [TimeSpan]::FromSeconds(60)) {
        try {
            $candidate = Get-InventoryTransferListUiaObservation `
                -ProcessId $appBProcessId `
                -ExpectedProcessStartTimeUtcTicks $appBStartTicks `
                -ExpectedTransferAutomationId $expectedTransferAutomationId
            if (
                [long]$candidate.WindowNativeHandle -eq [long]$before.WindowNativeHandle -and
                [string]$candidate.WindowRuntimeId -eq [string]$before.WindowRuntimeId -and
                [string]$candidate.ListRuntimeId -eq [string]$before.ListRuntimeId -and
                [int]$candidate.RowCount -eq ([int]$before.RowCount + 1)
            ) {
                $after = $candidate
                break
            }
            $lastObservationError = "sameWindow=$([long]$candidate.WindowNativeHandle -eq [long]$before.WindowNativeHandle); sameWindowRuntime=$([string]$candidate.WindowRuntimeId -eq [string]$before.WindowRuntimeId); sameListRuntime=$([string]$candidate.ListRuntimeId -eq [string]$before.ListRuntimeId); rowCount=$($candidate.RowCount)"
        }
        catch {
            $lastObservationError = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 100
    }
    $revisionWait.Stop()
    if ($null -eq $after) {
        throw "PC-B visible list did not expose the committed transfer revision through the realtime monitor within 60 seconds; transferId=$expectedTransferAutomationId; revision=$serverRevision; lastObservation=$lastObservationError"
    }

    $vmUpdatedSignal = Wait-MultiPcJsonFile `
        -Path (Join-Path $EvidenceDirectory "transfer-b-list-vm-updated.json") `
        -TimeoutSeconds 5 `
        -Predicate {
            param($payload)
            [string]$payload.RunId -eq $RunId -and
            [string]$payload.Nonce -eq $Nonce -and
            [string]$payload.Role -eq "B" -and
            [int]$payload.ProcessId -eq $appBProcessId -and
            [Guid]$payload.InventoryTransferId -eq $transferId -and
            [long]$payload.Revision -eq $serverRevision -and
            [int]$payload.BeforeRowCount -eq [int]$before.RowCount -and
            [int]$payload.AfterRowCount -eq ([int]$before.RowCount + 1) -and
            [long]$payload.WindowNativeHandle -eq [long]$before.WindowNativeHandle -and
            [bool]$payload.RealtimeRevisionMonitorActive -and
            [DateTimeOffset]$payload.PassiveRefreshCompletedAtUtc -gt [DateTimeOffset]$createdSignal.CapturedAtUtc
        }

    $uiaEvidence = [ordered]@{
        SchemaVersion = "1"
        Result = "PASS"
        RunId = $RunId
        EvidenceClassification = [ordered]@{
            BeforeAndAfter = "out-of-process UIAutomationClient observations"
            VmSignal = "in-process ViewModel coordination only; not UIA evidence"
        }
        Observer = [ordered]@{
            RunnerProcessId = $PID
            RoleAProcessId = $appAProcessId
            RoleAProcessStartTimeUtcTicks = $appAStartTicks
            RoleBProcessId = $appBProcessId
            RoleBProcessStartTimeUtcTicks = $appBStartTicks
            RoleAHostProcessId = $RoleA.Process.Id
            RoleBHostProcessId = $RoleB.Process.Id
        }
        ServerCommit = [ordered]@{
            TransferId = $expectedTransferAutomationId
            Revision = $serverRevision
            CapturedAtUtc = [string]$createdSignal.CapturedAtUtc
        }
        Before = $before
        After = $after
        RowCountDelta = ([int]$after.RowCount - [int]$before.RowCount)
        SameWindowNativeHandle = ([long]$after.WindowNativeHandle -eq [long]$before.WindowNativeHandle)
        SameWindowRuntimeId = ([string]$after.WindowRuntimeId -eq [string]$before.WindowRuntimeId)
        SameListRuntimeId = ([string]$after.ListRuntimeId -eq [string]$before.ListRuntimeId)
        RevisionSignalToVisibleElapsedMilliseconds = [long]([Math]::Max(
            0,
            (([DateTimeOffset]$after.CapturedAtUtc -
              [DateTimeOffset]$createdSignal.CapturedAtUtc).TotalMilliseconds)))
        ObservationWaitElapsedMilliseconds = [long]$revisionWait.ElapsedMilliseconds
        InProcessVmSignal = [ordered]@{
            ProcessId = [int]$vmUpdatedSignal.ProcessId
            BeforeRowCount = [int]$vmUpdatedSignal.BeforeRowCount
            AfterRowCount = [int]$vmUpdatedSignal.AfterRowCount
            RealtimeRevisionMonitorActive = [bool]$vmUpdatedSignal.RealtimeRevisionMonitorActive
            PassiveRefreshCompletedAtUtc = [string]$vmUpdatedSignal.PassiveRefreshCompletedAtUtc
            CapturedAtUtc = [string]$vmUpdatedSignal.CapturedAtUtc
        }
    }
    Write-MultiPcJsonAtomic -Path $EvidencePath -Payload $uiaEvidence -Depth 12

    $afterGatePath = Join-Path $EvidenceDirectory "transfer-b-list-uia-updated.json"
    $afterGate = [ordered]@{
        RunId = $RunId
        Nonce = $Nonce
        Role = "RUNNER"
        ProcessId = $PID
        CapturedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
        Phase = "after-create"
        TargetRole = "B"
        TargetProcessId = $appBProcessId
        WindowNativeHandle = [long]$after.WindowNativeHandle
        WindowAutomationId = [string]$after.WindowAutomationId
        WindowRuntimeId = [string]$after.WindowRuntimeId
        ListAutomationId = [string]$after.ListAutomationId
        ListRuntimeId = [string]$after.ListRuntimeId
        BeforeRowCount = [int]$before.RowCount
        AfterRowCount = [int]$after.RowCount
        InventoryTransferId = $transferId
        ServerRevision = $serverRevision
    }
    Write-MultiPcJsonAtomic -Path $afterGatePath -Payload $afterGate
    return $uiaEvidence
}

function Get-ProcessByIdSafe {
    param([int]$ProcessId)

    if ($ProcessId -le 0) {
        return $null
    }
    return Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
}

function Test-ExactProcessStillAlive {
    param(
        [int]$ProcessId,
        [long]$ExpectedStartTimeUtcTicks
    )

    $candidate = Get-ProcessByIdSafe -ProcessId $ProcessId
    if ($null -eq $candidate) {
        return $false
    }

    try {
        $candidate.Refresh()
        if ($candidate.HasExited) {
            return $false
        }
        return (
            $candidate.StartTime.ToUniversalTime().Ticks -eq
            $ExpectedStartTimeUtcTicks)
    }
    catch {
        # 종료 경쟁 조건이면 release gate가 포트와 DB handle 해제를 별도로 확인합니다.
        return $false
    }
}

function Restore-FileBytesPreservingMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][byte[]]$Bytes
    )

    $normalizedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $normalizedPath -PathType Leaf)) {
        throw "Restore target file does not exist: $normalizedPath"
    }

    $stream = [System.IO.FileStream]::new(
        $normalizedPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None,
        4096,
        [System.IO.FileOptions]::WriteThrough)
    try {
        $stream.SetLength(0)
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function New-ServerDatabaseRollbackSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$DatabasePath,
        [Parameter(Mandatory = $true)][string]$SnapshotPath
    )

    if (-not (Test-Path -LiteralPath $DatabasePath -PathType Leaf)) {
        throw "Isolated server database was not found: $DatabasePath"
    }

    $snapshotDirectory = Split-Path -Parent $SnapshotPath
    New-Item -ItemType Directory -Path $snapshotDirectory -Force | Out-Null
    Assert-NoReparsePoint -Path $snapshotDirectory -Label "Server DB rollback snapshot"
    foreach ($suffix in @("", "-wal", "-shm", "-journal")) {
        $sourcePath = "$DatabasePath$suffix"
        if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
            Copy-Item -LiteralPath $sourcePath -Destination "$SnapshotPath$suffix"
        }
    }
    return Get-DatabaseFileSetSha256 -DatabasePath $SnapshotPath
}

function Get-DatabaseFileSetSha256 {
    param([Parameter(Mandatory = $true)][string]$DatabasePath)

    $entries = New-Object System.Collections.Generic.List[string]
    foreach ($suffix in @("", "-wal", "-shm", "-journal")) {
        $path = "$DatabasePath$suffix"
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $item = Get-Item -LiteralPath $path
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            $entries.Add("$suffix`t$($item.Length)`t$hash")
        }
    }
    if ($entries.Count -eq 0) {
        throw "SQLite database file set is empty: $DatabasePath"
    }
    return Get-StringSha256 -Value ([string]::Join("`n", $entries))
}

function Restore-ServerDatabaseRollbackSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$DatabasePath,
        [Parameter(Mandatory = $true)][string]$SnapshotPath,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$ServerDirectory,
        [Parameter(Mandatory = $true)][string]$MultiPcRoot
    )

    if (-not (Test-Path -LiteralPath $SnapshotPath -PathType Leaf)) {
        throw "Server DB rollback snapshot is missing: $SnapshotPath"
    }
    if (
        -not (Test-PathWithin -Candidate $DatabasePath -Root $ServerDirectory) -or
        -not (Test-PathWithin -Candidate $SnapshotPath -Root $MultiPcRoot)
    ) {
        throw "Server DB rollback path escaped the isolated runtime."
    }

    foreach ($sidecarPath in @(
        "$DatabasePath-wal",
        "$DatabasePath-shm",
        "$DatabasePath-journal")) {
        if (Test-Path -LiteralPath $sidecarPath -PathType Leaf) {
            Remove-Item -LiteralPath $sidecarPath -Force
        }
    }

    $databaseDirectory = Split-Path -Parent $DatabasePath
    $restoreTempPath = Join-Path $databaseDirectory (
        ".$([System.IO.Path]::GetFileName($DatabasePath)).rollback.$([Guid]::NewGuid().ToString('N')).tmp")
    $databaseBackupPath = Join-Path $databaseDirectory (
        ".$([System.IO.Path]::GetFileName($DatabasePath)).backup.$([Guid]::NewGuid().ToString('N')).tmp")
    try {
        Copy-Item -LiteralPath $SnapshotPath -Destination $restoreTempPath
        [System.IO.File]::Replace(
            $restoreTempPath,
            $DatabasePath,
            $databaseBackupPath,
            $true)
    }
    finally {
        if (Test-Path -LiteralPath $restoreTempPath -PathType Leaf) {
            Remove-Item -LiteralPath $restoreTempPath -Force
        }
        if (Test-Path -LiteralPath $databaseBackupPath -PathType Leaf) {
            Remove-Item -LiteralPath $databaseBackupPath -Force
        }
    }

    foreach ($suffix in @("-wal", "-shm", "-journal")) {
        $snapshotSidecarPath = "$SnapshotPath$suffix"
        if (Test-Path -LiteralPath $snapshotSidecarPath -PathType Leaf) {
            Copy-Item `
                -LiteralPath $snapshotSidecarPath `
                -Destination "$DatabasePath$suffix"
        }
    }

    $restoredSha256 = Get-DatabaseFileSetSha256 -DatabasePath $DatabasePath
    if (-not [string]::Equals(
        $restoredSha256,
        $ExpectedSha256,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Isolated server DB rollback hash mismatch."
    }

    foreach ($suffix in @("", "-wal", "-shm", "-journal")) {
        $snapshotFilePath = "$SnapshotPath$suffix"
        if (Test-Path -LiteralPath $snapshotFilePath -PathType Leaf) {
            Remove-Item -LiteralPath $snapshotFilePath -Force
        }
    }
    return $restoredSha256
}

function Stop-ExactOwnedProcesses {
    param(
        [object[]]$RoleHosts,
        [string]$MultiPcRoot,
        [string]$ServerDirectory,
        [int]$AttestedServerProcessId,
        [long]$AttestedServerStartTimeUtcTicks
    )

    $ownedIds = New-Object System.Collections.Generic.HashSet[int]
    $ownedStartTimeTicks = @{}
    if (
        $AttestedServerProcessId -gt 0 -and
        $AttestedServerStartTimeUtcTicks -gt 0
    ) {
        [void]$ownedIds.Add($AttestedServerProcessId)
        $ownedStartTimeTicks[$AttestedServerProcessId] =
            $AttestedServerStartTimeUtcTicks
    }
    foreach ($roleHost in @($RoleHosts)) {
        if ($null -ne $roleHost -and $null -ne $roleHost.Process) {
            try {
                $roleHost.Process.Refresh()
                $roleHostId = [int]$roleHost.Process.Id
                [void]$ownedIds.Add($roleHostId)
                $ownedStartTimeTicks[$roleHostId] =
                    [long]$roleHost.ProcessStartTimeUtcTicks
            }
            catch { }
        }
    }

    try {
        $cimProcesses = @(Get-CimInstance Win32_Process -ErrorAction Stop)
        $frontier = @($ownedIds)
        while ($frontier.Count -gt 0) {
            $parentId = [int]$frontier[0]
            if ($frontier.Count -eq 1) {
                $frontier = @()
            }
            else {
                $frontier = @($frontier[1..($frontier.Count - 1)])
            }

            foreach ($child in @($cimProcesses | Where-Object { [int]$_.ParentProcessId -eq $parentId })) {
                $childId = [int]$child.ProcessId
                $isAllowedApp = (
                    -not [string]::IsNullOrWhiteSpace([string]$child.ExecutablePath) -and
                    (Test-PathWithin -Candidate ([string]$child.ExecutablePath) -Root $MultiPcRoot))
                $isAllowedServer = (
                    -not [string]::IsNullOrWhiteSpace([string]$child.CommandLine) -and
                    ([string]$child.CommandLine).IndexOf(
                        (Get-NormalizedFullPath -Path $ServerDirectory),
                        [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                    ([string]$child.CommandLine).IndexOf(
                        "Server.Api.dll",
                        [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
                if (($isAllowedApp -or $isAllowedServer) -and $ownedIds.Add($childId)) {
                    $childProcess = Get-Process -Id $childId -ErrorAction SilentlyContinue
                    if ($null -eq $childProcess) {
                        [void]$ownedIds.Remove($childId)
                        continue
                    }
                    $ownedStartTimeTicks[$childId] =
                        $childProcess.StartTime.ToUniversalTime().Ticks
                    $frontier += $childId
                }
            }
        }
    }
    catch {
        # 이미 확보한 exact PID만으로 정리를 계속합니다.
    }

    $cleanupFailures = New-Object System.Collections.Generic.List[string]
    foreach ($processId in @($ownedIds | Sort-Object -Descending)) {
        $process = Get-ProcessByIdSafe -ProcessId $processId
        if ($null -eq $process) {
            continue
        }

        try {
            if (
                -not $ownedStartTimeTicks.ContainsKey($processId) -or
                $process.StartTime.ToUniversalTime().Ticks -ne
                    [long]$ownedStartTimeTicks[$processId]
            ) {
                continue
            }
        }
        catch {
            continue
        }

        try {
            if ($process.MainWindowHandle -ne 0) {
                [void]$process.CloseMainWindow()
                if ($process.WaitForExit(1500)) {
                    continue
                }
            }
        }
        catch { }

        try {
            $process.Kill()
            if (-not $process.WaitForExit(10000)) {
                if (Test-ExactProcessStillAlive `
                    -ProcessId $processId `
                    -ExpectedStartTimeUtcTicks (
                        [long]$ownedStartTimeTicks[$processId])) {
                    $cleanupFailures.Add(
                        "exact process exit timeout: pid=$processId")
                }
            }
            elseif (Test-ExactProcessStillAlive `
                -ProcessId $processId `
                -ExpectedStartTimeUtcTicks (
                    [long]$ownedStartTimeTicks[$processId])) {
                $cleanupFailures.Add(
                    "exact process remains alive: pid=$processId")
            }
        }
        catch {
            if (Test-ExactProcessStillAlive `
                -ProcessId $processId `
                -ExpectedStartTimeUtcTicks (
                    [long]$ownedStartTimeTicks[$processId])) {
                $cleanupFailures.Add(
                    "exact process cleanup failed: pid=$processId")
            }
        }
    }

    if ($cleanupFailures.Count -gt 0) {
        throw ([string]::Join("; ", $cleanupFailures))
    }
}

function Add-Step {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Steps,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [Parameter(Mandatory = $true)][string]$Detail
    )

    $Steps.Add([pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Detail = $Detail.Replace("`r", " ").Replace("`n", " ")
    }) | Out-Null
}

if ($ValidateSeedEnvironmentIsolationOnly) {
    foreach ($role in @("A", "B")) {
        $probeStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
        Remove-SeedUsersEnvironmentVariables -StartInfo $probeStartInfo
        $remainingSeedKeys = @(
            $probeStartInfo.EnvironmentVariables.Keys |
                Where-Object {
                    ([string]$_).StartsWith(
                        "SeedUsers__",
                        [System.StringComparison]::OrdinalIgnoreCase)
                })
        if ($remainingSeedKeys.Count -ne 0) {
            throw "Role host seed environment isolation validation failed."
        }
    }
    Write-Output "PASS"
    return
}

$recordsRoot = Join-Path $ProjectRoot "테스트 시행\기록"
if (-not (Test-PathWithin -Candidate $EvidenceDirectory -Root $recordsRoot)) {
    throw "EvidenceDirectory는 테스트 시행\기록 아래여야 합니다: $EvidenceDirectory"
}
Assert-NoReparsePoint -Path $recordsRoot -Label "테스트 증거 루트"
if (Test-Path -LiteralPath $EvidenceDirectory) {
    if (-not (Test-Path -LiteralPath $EvidenceDirectory -PathType Container)) {
        throw "EvidenceDirectory가 디렉터리가 아닙니다: $EvidenceDirectory"
    }
    $existingEvidence = @(Get-ChildItem -LiteralPath $EvidenceDirectory -Force)
    if ($existingEvidence.Count -gt 0) {
        throw "EvidenceDirectory는 신규 또는 빈 디렉터리여야 합니다: $EvidenceDirectory"
    }
}
else {
    New-Item -ItemType Directory -Path $EvidenceDirectory | Out-Null
}
Assert-NoReparsePoint -Path $EvidenceDirectory -Label "Multi-PC E2E 증거 루트"

$multiPcRoot = Join-Path $ExecutionRoot "MultiPC"
$sourceAppRoot = Join-Path $ExecutionRoot "App"
$serverDirectory = Join-Path $ExecutionRoot "Server"
$serverDataRoot = Join-Path $ExecutionRoot "ServerData"
$serverDatabasePath = Join-Path $serverDirectory "거래플랜-local.db"
$prepareScript = Join-Path $scriptRoot "준비-다중PC-검증.ps1"
$readinessScript = Join-Path $scriptRoot "Invoke-MultiPcReadinessCheck.ps1"
$uiSmokeScript = Join-Path $ProjectRoot "tools\verification\Invoke-GeoraePlanDesktopUiSmoke.ps1"
$setApiScript = Join-Path $ExecutionRoot "Set-ApiBaseUrl.ps1"
$dotnetExe = if (Test-Path -LiteralPath "D:\.dotnet-sdk\dotnet.exe") {
    "D:\.dotnet-sdk\dotnet.exe"
}
elseif (Test-Path -LiteralPath "C:\Users\beene\.dotnet-sdk\dotnet.exe") {
    "C:\Users\beene\.dotnet-sdk\dotnet.exe"
}
else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

foreach ($requiredPath in @(
    $sourceAppRoot,
    $serverDirectory,
    $serverDataRoot,
    $serverDatabasePath,
    $prepareScript,
    $readinessScript,
    $uiSmokeScript,
    $setApiScript,
    $dotnetExe)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Multi-PC E2E 필수 경로가 없습니다: $requiredPath"
    }
}

$steps = New-Object System.Collections.Generic.List[object]
$roleHosts = @()
$knownAppProcessIds = @()
$serverPort = 0
$serverUrl = ""
$sourceAppSettings = Join-Path $sourceAppRoot "appsettings.json"
$sourceAppSettingsOriginalBytes = $null
$sourceAppSettingsOriginalSha256 = ""
$sourceAppSettingsOriginalSddl = ""
$serverDatabaseSnapshotPath = ""
$serverDatabaseOriginalSha256 = ""
$bootstrapContractPath = ""
$bootstrapContractSha256 = ""
$ownedServerProcessId = 0
$ownedServerStartTimeUtcTicks = 0L
$runId = [Guid]::NewGuid().ToString("N")
$loginInputMutexName = "Local\GeoraePlan.MultiPc.LoginInput.$runId"
$nonceBytes = New-Object byte[] 32
$nonceGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $nonceGenerator.GetBytes($nonceBytes)
}
finally {
    $nonceGenerator.Dispose()
}
$nonce = [Convert]::ToBase64String($nonceBytes)
$contractPath = Join-Path $EvidenceDirectory "run-contract.json"
$inventoryTransferUiaEvidencePath = Join-Path $EvidenceDirectory "inventory-transfer-list-uia.json"
$summaryPath = Join-Path $EvidenceDirectory "multi-pc-desktop-e2e.json"
$markdownPath = Join-Path $EvidenceDirectory "multi-pc-desktop-e2e.md"
$overall = "FAIL"
$failureMessage = ""

try {
    Assert-NoRunningTestRuntime -ExecutionRoot $ExecutionRoot -MultiPcRoot $multiPcRoot
    Add-Step -Steps $steps -Name "preflight-no-running-runtime" -Passed $true -Detail "Desktop/server test runtime process=0"

    $serverPort = Get-FreeLoopbackPort -StartingPort $StartingServerPort
    $serverUrl = "http://127.0.0.1:$serverPort"

    $sourceAppSettingsOriginalBytes =
        [System.IO.File]::ReadAllBytes($sourceAppSettings)
    $sourceAppSettingsOriginalSha256 =
        (Get-FileHash -LiteralPath $sourceAppSettings -Algorithm SHA256).Hash
    $sourceAppSettingsOriginalSddl =
        (Get-Acl -LiteralPath $sourceAppSettings).Sddl
    & $setApiScript -BaseUrl $serverUrl -AppSettingsPaths @($sourceAppSettings) | Out-Null
    & $prepareScript -ProjectRoot $ProjectRoot -ExecutionRoot $ExecutionRoot -MultiPcRoot $multiPcRoot -ResetClientData
    Add-Step -Steps $steps -Name "prepare-isolated-runtime" -Passed $true -Detail "physical App/AppData/temp/download roots prepared; cloned Sync.DeviceId removed"

    $readinessPath = Join-Path $EvidenceDirectory "multi-pc-readiness.md"
    & $readinessScript -ProjectRoot $ProjectRoot -ExecutionRoot $ExecutionRoot -OutputPath $readinessPath
    Add-Step -Steps $steps -Name "readiness" -Passed $true -Detail $readinessPath

    $certification = Read-RuntimeCertificationMarker `
        -RuntimeRoot $ExecutionRoot `
        -ServerDirectory $serverDirectory
    $serverAssemblyPathSha256 =
        Get-NormalizedPathSha256 -Path $certification.ServerDllPath
    $serverInstanceSha256 = Get-StringSha256 -Value (
        [string]::Join(
            "`n",
            @(
                $nonce,
                (Get-NormalizedFullPath -Path $EvidenceDirectory),
                $certification.CertificationId,
                "A",
                $serverAssemblyPathSha256)))
    Add-Step -Steps $steps -Name "runtime-certification" -Passed $true -Detail "certificationId=$($certification.CertificationId); serverDll=$($certification.ServerDllSha256); marker=$($certification.MarkerSha256); assemblyPath=$serverAssemblyPathSha256"

    $serverDatabaseSnapshotPath = Join-Path `
        (Join-Path $multiPcRoot ".rollback\$runId") `
        "server-before.db"
    $serverDatabaseOriginalSha256 =
        New-ServerDatabaseRollbackSnapshot `
            -DatabasePath $serverDatabasePath `
            -SnapshotPath $serverDatabaseSnapshotPath
    Add-Step -Steps $steps -Name "server-db-rollback-snapshot" -Passed $true -Detail "isolated DB SHA-256 $serverDatabaseOriginalSha256"

    $bootstrapContractPath = Join-Path `
        (Split-Path -Parent $serverDatabaseSnapshotPath) `
        "bootstrap-contract.json"
    $bootstrapContract = [ordered]@{
        SchemaVersion = "1"
        RunId = $runId
        Role = "A"
        ExecutionRoot = (Get-NormalizedFullPath -Path $ExecutionRoot)
        ServerDirectory = (Get-NormalizedFullPath -Path $serverDirectory)
        ServerDataRoot = (Get-NormalizedFullPath -Path $serverDataRoot)
        ServerDllPath = (Get-NormalizedFullPath -Path $certification.ServerDllPath)
        ServerDllSha256 = $certification.ServerDllSha256
        RuntimeMarkerPath = (Get-NormalizedFullPath -Path $certification.MarkerPath)
        RuntimeMarkerSha256 = $certification.MarkerSha256
        CertificationId = $certification.CertificationId
        SnapshotPath = (Get-NormalizedFullPath -Path $serverDatabaseSnapshotPath)
        SnapshotSha256 = $serverDatabaseOriginalSha256
        CreatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    }
    $bootstrapContract |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $bootstrapContractPath -Encoding UTF8
    $bootstrapContractSha256 =
        (Get-FileHash -LiteralPath $bootstrapContractPath -Algorithm SHA256).Hash

    if ($useEphemeralAdminBootstrap) {
        $passwordBytes = New-Object byte[] 48
        $passwordGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try {
            $passwordGenerator.GetBytes($passwordBytes)
            $loginPassword = [Convert]::ToBase64String($passwordBytes)
        }
        finally {
            $passwordGenerator.Dispose()
            [Array]::Clear($passwordBytes, 0, $passwordBytes.Length)
            $passwordBytes = $null
        }
    }

    $clients = @(
        [pscustomobject]@{
            Role = "A"
            AppRoot = Join-Path $multiPcRoot "App-PC-A"
            AppDataRoot = Join-Path $multiPcRoot "AppData-PC-A"
            TempRoot = Join-Path $multiPcRoot "Temp-PC-A"
            DownloadsRoot = Join-Path $multiPcRoot "Downloads-PC-A"
            AppExe = ""
        },
        [pscustomobject]@{
            Role = "B"
            AppRoot = Join-Path $multiPcRoot "App-PC-B"
            AppDataRoot = Join-Path $multiPcRoot "AppData-PC-B"
            TempRoot = Join-Path $multiPcRoot "Temp-PC-B"
            DownloadsRoot = Join-Path $multiPcRoot "Downloads-PC-B"
            AppExe = ""
        }
    )

    $sourceTreeHash = Get-AppTreeSha256 -Root $sourceAppRoot
    foreach ($client in $clients) {
        $client.AppExe = Get-DesktopAppExecutable -AppRoot $client.AppRoot
        $clientTreeHash = Get-AppTreeSha256 -Root $client.AppRoot
        if (-not [string]::Equals($sourceTreeHash, $clientTreeHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "PC-$($client.Role) App tree hash가 원본과 다릅니다."
        }

        $clientSettings = Get-Content -LiteralPath (Join-Path $client.AppRoot "appsettings.json") -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not [string]::Equals(
            ([string]$clientSettings.Api.BaseUrl).TrimEnd("/"),
            $serverUrl,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "PC-$($client.Role) Api.BaseUrl이 runner-owned server와 다릅니다."
        }
    }
    Add-Step -Steps $steps -Name "app-tree-parity" -Passed $true -Detail "source=PC-A=PC-B SHA-256 $sourceTreeHash"

    $contract = [ordered]@{
        SchemaVersion = "1"
        RunId = $runId
        Nonce = $nonce
        CreatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
        ExpiresAtUtc = (Get-Date).ToUniversalTime().AddMinutes(30).ToString("O")
        ApiBaseUrl = $serverUrl
        MultiPcRoot = $multiPcRoot
        CertificationId = $certification.CertificationId
        ServerDllSha256 = $certification.ServerDllSha256
        RuntimeReadyMarkerSha256 = $certification.MarkerSha256
        ServerAssemblyPathSha256 = $serverAssemblyPathSha256
        ServerInstanceSha256 = $serverInstanceSha256
        RunnerProcessId = $PID
    }
    $contract | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $contractPath -Encoding UTF8
    Add-Step -Steps $steps -Name "run-contract" -Passed $true -Detail "runId=$runId; api=$serverUrl; expires<=30m"

    $roleA = $clients[0]
    $roleAEvidence = Join-Path $EvidenceDirectory "PC-A"
    $roleAInAppReport = Join-Path $roleAEvidence "multi-pc-inapp-A.md"
    $roleHosts += Start-RoleHost `
        -Role "A" `
        -AppExe $roleA.AppExe `
        -AppDataRoot $roleA.AppDataRoot `
        -TempRoot $roleA.TempRoot `
        -DownloadsRoot $roleA.DownloadsRoot `
        -RunRoot $EvidenceDirectory `
        -Nonce $nonce `
        -RuntimeRoot $ExecutionRoot `
        -CertificationId $certification.CertificationId `
        -UiSmokeScript $uiSmokeScript `
        -RoleEvidenceDirectory $roleAEvidence `
        -InAppReportPath $roleAInAppReport `
        -LoginInputMutexName $loginInputMutexName `
        -LoginUsername $loginUsername `
        -LoginPassword $loginPassword `
        -UiStartupTimeoutSec $roleUiStartupTimeoutSeconds `
        -InAppSelfTestTimeoutSec $roleInAppSelfTestTimeoutSeconds `
        -DotnetExe $dotnetExe `
        -ServerDirectory $serverDirectory `
        -ServerDataRoot $serverDataRoot `
        -ServerPort $serverPort `
        -StartServer $true `
        -EnableEphemeralAdminBootstrap $useEphemeralAdminBootstrap `
        -BootstrapContractPath $bootstrapContractPath `
        -BootstrapContractSha256 $bootstrapContractSha256

    $readyPayload = Wait-ReadyEndpoint `
        -ServerUrl $serverUrl `
        -ExpectedInstanceSha256 $serverInstanceSha256 `
        -ExpectedCertificationId $certification.CertificationId `
        -ExpectedServerDllSha256 $certification.ServerDllSha256 `
        -ExpectedMarkerSha256 $certification.MarkerSha256 `
        -ExpectedAssemblyPathSha256 $serverAssemblyPathSha256 `
        -ContractCreatedAtUtc ([DateTimeOffset]$contract.CreatedAtUtc) `
        -ContractExpiresAtUtc ([DateTimeOffset]$contract.ExpiresAtUtc) `
        -TimeoutSeconds $serverReadyTimeoutSeconds
    $ownedServerIdentity = Assert-ServerProcessOwnedByRoleHost `
        -ReadyPayload $readyPayload `
        -RoleHostProcess $roleHosts[0].Process `
        -ExpectedServerDllPath $certification.ServerDllPath
    $ownedServerProcessId = [int]$ownedServerIdentity.ProcessId
    $ownedServerStartTimeUtcTicks =
        [long]$ownedServerIdentity.StartTimeUtcTicks
    Add-Step -Steps $steps -Name "runner-owned-server-ready" -Passed $true -Detail "url=$serverUrl; pid=$ownedServerProcessId; role=A; status=$($readyPayload.status); databaseCompleted=$($readyPayload.databaseInitialization.completed)"

    $roleB = $clients[1]
    $roleBEvidence = Join-Path $EvidenceDirectory "PC-B"
    $roleBInAppReport = Join-Path $roleBEvidence "multi-pc-inapp-B.md"
    $roleHosts += Start-RoleHost `
        -Role "B" `
        -AppExe $roleB.AppExe `
        -AppDataRoot $roleB.AppDataRoot `
        -TempRoot $roleB.TempRoot `
        -DownloadsRoot $roleB.DownloadsRoot `
        -RunRoot $EvidenceDirectory `
        -Nonce $nonce `
        -RuntimeRoot $ExecutionRoot `
        -CertificationId $certification.CertificationId `
        -UiSmokeScript $uiSmokeScript `
        -RoleEvidenceDirectory $roleBEvidence `
        -InAppReportPath $roleBInAppReport `
        -LoginInputMutexName $loginInputMutexName `
        -LoginUsername $loginUsername `
        -LoginPassword $loginPassword `
        -UiStartupTimeoutSec $roleUiStartupTimeoutSeconds `
        -InAppSelfTestTimeoutSec $roleInAppSelfTestTimeoutSeconds `
        -DotnetExe $dotnetExe `
        -ServerDirectory $serverDirectory `
        -ServerDataRoot $serverDataRoot `
        -ServerPort $serverPort `
        -StartServer $false `
        -EnableEphemeralAdminBootstrap $false

    $loginUsername = $null
    $loginPassword = $null
    $inventoryTransferUiaEvidence = Invoke-InventoryTransferListUiaGate `
        -EvidenceDirectory $EvidenceDirectory `
        -RunId $runId `
        -Nonce $nonce `
        -RoleA $roleHosts[0] `
        -RoleB $roleHosts[1] `
        -ExpectedAppAExe $roleA.AppExe `
        -ExpectedAppBExe $roleB.AppExe `
        -EvidencePath $inventoryTransferUiaEvidencePath
    Add-Step `
        -Steps $steps `
        -Name "inventory-transfer-open-list-live-uia" `
        -Passed $true `
        -Detail "out-of-process UIA: PC-B pid=$($inventoryTransferUiaEvidence.Observer.RoleBProcessId); same HWND/runtime/list=true; rows $($inventoryTransferUiaEvidence.Before.RowCount)->$($inventoryTransferUiaEvidence.After.RowCount); transfer=$($inventoryTransferUiaEvidence.ServerCommit.TransferId); revision=$($inventoryTransferUiaEvidence.ServerCommit.Revision); observationWaitMs=$($inventoryTransferUiaEvidence.ObservationWaitElapsedMilliseconds)"
    Wait-RoleHosts -RoleHosts $roleHosts -TimeoutSeconds $TimeoutSeconds
    foreach ($roleHost in $roleHosts) {
        [void]$roleHost.Process.WaitForExit()
        Complete-RoleHostOutputCapture -RoleHost $roleHost
        $roleHost.Process.Refresh()
        $roleHostExitCode = [int]$roleHost.Process.ExitCode
        if ($roleHostExitCode -ne 0) {
            $stderrTail = if (Test-Path -LiteralPath $roleHost.StderrPath) {
                ((Get-Content -LiteralPath $roleHost.StderrPath -Tail 20) -join " / ")
            }
            else {
                ""
            }
            throw "PC-$($roleHost.Role) UI host 실패: exit=$roleHostExitCode; stderrTail=$stderrTail"
        }

        $uiSmokeReports = @(
            Get-ChildItem `
                -LiteralPath $roleHost.EvidenceDirectory `
                -File `
                -Filter "desktop-ui-smoke-*.json"
        )
        if ($uiSmokeReports.Count -ne 1) {
            throw "PC-$($roleHost.Role) UI smoke JSON evidence count must be exactly one."
        }
        $uiSmokePayload = Read-JsonFile -Path $uiSmokeReports[0].FullName
        if ([string]$uiSmokePayload.Result -ne "PASS") {
            throw "PC-$($roleHost.Role) UI smoke JSON result is not PASS."
        }
        $requiredUiSteps = @(
            "login-window",
            "login-submit",
            "main-buttons",
            "window-거래처 관리",
            "close-거래처 관리",
            "window-품목/재고 관리",
            "close-품목/재고 관리",
            "window-판매(매출)",
            "close-판매(매출)",
            "window-구매(매입)",
            "close-구매(매입)",
            "initial-responsive-환경설정",
            "ready-환경설정",
            "window-환경설정",
            "close-환경설정",
            "ready-렌탈 청구관리",
            "window-렌탈 청구관리",
            "close-렌탈 청구관리",
            "ready-렌탈 자산/설치현황",
            "window-렌탈 자산/설치현황",
            "close-렌탈 자산/설치현황")
        foreach ($requiredUiStep in $requiredUiSteps) {
            $matchingUiSteps = @(
                $uiSmokePayload.Steps |
                    Where-Object {
                        [string]$_.Name -eq $requiredUiStep -and
                        [bool]$_.Passed
                    }
            )
            if ($matchingUiSteps.Count -ne 1) {
                throw "PC-$($roleHost.Role) actual UIA evidence step is missing or duplicated: $requiredUiStep"
            }
        }
        $loginWindowUiStep = @(
            $uiSmokePayload.Steps |
                Where-Object { [string]$_.Name -eq "login-window" }
        )[0]
        if (
            -not ([string]$loginWindowUiStep.Detail).StartsWith(
                "found; title=",
                [System.StringComparison]::Ordinal)
        ) {
            throw "PC-$($roleHost.Role) did not exercise the actual login-window credential input path."
        }
        $roleHost.UiSmokeJsonPath = $uiSmokeReports[0].FullName
    }
    Add-Step -Steps $steps -Name "role-hosts" -Passed $true -Detail "PC-A/PC-B UI host exit=0"
    Add-Step -Steps $steps -Name "login-main-window-uia" -Passed $true -Detail "actual out-of-process WPF UI Automation evidence: attested login fields, credential submission, main window, and required main controls for PC-A/PC-B"

    $inAppReports = @()
    foreach ($roleHost in $roleHosts) {
        $inAppJsonPath = [System.IO.Path]::ChangeExtension($roleHost.InAppReportPath, ".json")
        $payload = Read-JsonFile -Path $inAppJsonPath
        if ([string]$payload.Result -ne "PASS") {
            throw "PC-$($roleHost.Role) in-app E2E가 PASS가 아닙니다: $($payload.Result)"
        }
        if ([string]$payload.RunId -ne $runId -or [string]$payload.Role -ne $roleHost.Role) {
            throw "PC-$($roleHost.Role) in-app E2E report identity가 run contract와 다릅니다."
        }
        if ([string]$payload.Scenario -ne "customer-and-item-stale-autosave-and-delete-propagation-with-rental-and-pending-inventory-transfer") {
            throw "PC-$($roleHost.Role) in-app E2E report scenario가 customer/item/rental/pending-inventory-transfer 전체 계약과 다릅니다: $($payload.Scenario)"
        }
        $requiredItemStepNames = if ($roleHost.Role -eq "A") {
            @(
                "item-actual-server-stale-push",
                "item-actual-server-stale-delete",
                "item-local-stale-delete-conflict",
                "item-stale-autosave-conflict",
                "item-delete-propagation",
                "item-fixture-purge-no-residue"
            )
        }
        else {
            @(
                "item-winner-save-and-sync",
                "item-delete-and-sync",
                "server-item-fixture-purge",
                "item-fixture-purge-no-residue"
            )
        }
        $failedOrMissingItemSteps = @(
            foreach ($requiredStepName in $requiredItemStepNames) {
                $matchingStep = @($payload.Steps | Where-Object {
                    [string]$_.Name -eq $requiredStepName
                })
                if ($matchingStep.Count -ne 1 -or -not [bool]$matchingStep[0].Passed) {
                    $requiredStepName
                }
            }
        )
        if ($failedOrMissingItemSteps.Count -gt 0) {
            throw "PC-$($roleHost.Role) 필수 품목 E2E 단계가 누락 또는 실패했습니다: $($failedOrMissingItemSteps -join ',')"
        }
        $requiredRentalBillingStepNames = if ($roleHost.Role -eq "A") {
            @(
                "rental-billing-create-and-sync",
                "rental-billing-stale-edit-staged",
                "rental-billing-actual-server-stale-save",
                "rental-billing-pull-reload-retry-save",
                "rental-billing-server-idempotent-stale-delete-no-op",
                "rental-billing-idempotent-delete-propagation",
                "rental-billing-fixture-purge-no-residue"
            )
        }
        else {
            @(
                "rental-billing-cross-client-pull",
                "rental-billing-winner-save-and-sync",
                "rental-billing-delete-and-sync",
                "server-rental-billing-fixture-purge"
            )
        }
        $failedOrMissingRentalBillingSteps = @(
            foreach ($requiredStepName in $requiredRentalBillingStepNames) {
                $matchingStep = @($payload.Steps | Where-Object {
                    [string]$_.Name -eq $requiredStepName
                })
                if ($matchingStep.Count -ne 1 -or -not [bool]$matchingStep[0].Passed) {
                    $requiredStepName
                }
            }
        )
        if ($failedOrMissingRentalBillingSteps.Count -gt 0) {
            throw "PC-$($roleHost.Role) 필수 렌탈 청구 E2E 단계가 누락 또는 실패했습니다: $($failedOrMissingRentalBillingSteps -join ',')"
        }
        $requiredRentalAssetStepNames = if ($roleHost.Role -eq "A") {
            @("rental-asset-create-and-sync","rental-asset-stale-edit-staged","rental-asset-stale-autosave-conflict","rental-asset-pull-reload-retry-save","rental-asset-server-idempotent-stale-delete-no-op","rental-asset-idempotent-delete-propagation","rental-asset-fixture-purge-no-residue")
        } else {
            @("rental-asset-cross-client-pull","rental-asset-winner-save-and-sync","rental-asset-delete-and-sync","server-rental-asset-fixture-purge")
        }
        $failedOrMissingRentalAssetSteps = @(
            foreach ($requiredStepName in $requiredRentalAssetStepNames) {
                $matchingStep = @($payload.Steps | Where-Object { [string]$_.Name -eq $requiredStepName })
                if ($matchingStep.Count -ne 1 -or -not [bool]$matchingStep[0].Passed) { $requiredStepName }
            }
        )
        if ($failedOrMissingRentalAssetSteps.Count -gt 0) { throw "PC-$($roleHost.Role) 필수 렌탈 자산 E2E 단계가 누락 또는 실패했습니다: $($failedOrMissingRentalAssetSteps -join ',')" }
        $requiredInventoryTransferStepNames = if ($roleHost.Role -eq "A") {
            @("inventory-transfer-pending-create-source-outbound","inventory-transfer-stale-edit-staged","inventory-transfer-stale-autosave-conflict","inventory-transfer-pull-reload-retry-save","inventory-transfer-server-idempotent-stale-delete-no-op","inventory-transfer-idempotent-delete-propagation","inventory-transfer-fixture-purge-no-residue")
        } else {
            @("inventory-transfer-cross-client-pull","inventory-transfer-winner-save-and-sync","inventory-transfer-delete-and-sync","server-inventory-transfer-fixture-purge")
        }
        $failedOrMissingInventoryTransferSteps = @(
            foreach ($requiredStepName in $requiredInventoryTransferStepNames) {
                $matchingStep = @($payload.Steps | Where-Object { [string]$_.Name -eq $requiredStepName })
                if ($matchingStep.Count -ne 1 -or -not [bool]$matchingStep[0].Passed) { $requiredStepName }
            }
        )
        if ($failedOrMissingInventoryTransferSteps.Count -gt 0) { throw "PC-$($roleHost.Role) 필수 pending 재고이동 E2E 단계가 누락 또는 실패했습니다: $($failedOrMissingInventoryTransferSteps -join ',')" }
        $inAppReports += $payload
    }
    Add-Step -Steps $steps -Name "in-app-customer-conflict" -Passed $true -Detail "PC-A/PC-B reports PASS; stale autosave preserves draft; PC-B server value wins"
    Add-Step -Steps $steps -Name "in-app-item-conflict" -Passed $true -Detail "PC-A/PC-B reports PASS; item stale autosave preserves draft and selection; PC-B server value wins"
    Add-Step -Steps $steps -Name "in-app-required-item-steps" -Passed $true -Detail "PC-A/PC-B required stale save/delete/autosave/purge-residue steps present exactly once and PASS"
    Add-Step -Steps $steps -Name "in-process-rental-billing-integration" -Passed $true -Detail "in-process ViewModel/API/DB integration (not UIA interaction evidence); profile stale save conflict, reload/retry, idempotent delete no-op and purge verified; no billing run/invoice/payment path"
    Add-Step -Steps $steps -Name "in-process-rental-asset-integration" -Passed $true -Detail "in-process ViewModel/API/DB integration (not UIA interaction evidence); profile-free asset stale autosave/delete conflicts, reload/retry and marker-bound purge verified"
    Add-Step -Steps $steps -Name "in-process-inventory-transfer-integration" -Passed $true -Detail "in-process ViewModel/API/DB integration (not UIA interaction evidence); pending source-only OUT is exactly once, retry is non-duplicating, idempotent stale delete preserves tombstone, delete/purge restores baseline"

    $sessionA = Read-JsonFile -Path (Join-Path $EvidenceDirectory "session-A.json")
    $sessionB = Read-JsonFile -Path (Join-Path $EvidenceDirectory "session-B.json")
    foreach ($sessionContract in @(
        [pscustomobject]@{ Role = "A"; Payload = $sessionA },
        [pscustomobject]@{ Role = "B"; Payload = $sessionB }
    )) {
        $sessionPayload = $sessionContract.Payload
        if (
            [string]$sessionPayload.RunId -ne $runId -or
            [string]$sessionPayload.Nonce -ne $nonce -or
            [string]$sessionPayload.Role -ne $sessionContract.Role -or
            [int]$sessionPayload.ProcessId -le 0) {
            throw "PC-$($sessionContract.Role) session coordination contract가 run/nonce/role/process identity와 다릅니다."
        }
    }
    $knownAppProcessIds = @([int]$sessionA.ProcessId, [int]$sessionB.ProcessId)
    if (
        [int]$sessionA.ProcessId -eq [int]$sessionB.ProcessId -or
        [string]$sessionA.InstallRootHash -eq [string]$sessionB.InstallRootHash -or
        [string]$sessionA.AppRootHash -eq [string]$sessionB.AppRootHash -or
        [string]$sessionA.TempRootHash -eq [string]$sessionB.TempRootHash -or
        [string]$sessionA.DownloadsRootHash -eq [string]$sessionB.DownloadsRootHash -or
        [string]$sessionA.DeviceIdHash -eq [string]$sessionB.DeviceIdHash) {
        throw "PC-A/PC-B process/install/AppData/temp/download/device 격리 증거가 유일하지 않습니다."
    }
    Add-Step -Steps $steps -Name "two-live-desktop-identities" -Passed $true -Detail "pids=$($sessionA.ProcessId),$($sessionB.ProcessId); install/AppData/temp/download/device all distinct"

    $activeFixtureSignals = @(
        "a-created.json",
        "b-loaded.json",
        "a-staged.json",
        "b-written.json",
        "a-conflict.json",
        "b-deleted.json",
        "a-delete-observed.json",
        "b-purged.json",
        "a-clean.json",
        "b-complete.json",
        "item-a-created.json",
        "item-b-loaded.json",
        "item-a-staged.json",
        "item-b-written.json",
        "item-a-conflict.json",
        "item-b-deleted.json",
        "item-a-delete-observed.json",
        "item-b-purged.json",
        "item-a-clean.json",
        "item-b-complete.json",
        "rental-a-created.json",
        "rental-b-loaded.json",
        "rental-a-staged.json",
        "rental-b-written.json",
        "rental-a-retried.json",
        "rental-b-deleted.json",
        "rental-a-delete-observed.json",
        "rental-b-purged.json",
        "rental-a-clean.json",
        "asset-a-created.json",
        "asset-b-loaded.json",
        "asset-a-staged.json",
        "asset-b-written.json",
        "asset-a-conflict.json",
        "asset-a-retried.json",
        "asset-b-deleted.json",
        "asset-a-delete-observed.json",
        "asset-b-purged.json",
        "asset-a-clean.json",
        "transfer-a-created.json",
        "transfer-b-loaded.json",
        "transfer-a-staged.json",
        "transfer-b-written.json",
        "transfer-a-conflict.json",
        "transfer-a-retried.json",
        "transfer-b-deleted.json",
        "transfer-a-delete-observed.json",
        "transfer-b-purged.json",
        "transfer-a-clean.json"
    )
    $missingSignals = @($activeFixtureSignals | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $EvidenceDirectory $_) -PathType Leaf)
    })
    if ($missingSignals.Count -gt 0) {
        throw "필수 coordination evidence 누락: $($missingSignals -join ',')"
    }
    $uniqueFixtureSignals = @($activeFixtureSignals | Select-Object -Unique)
    if ($activeFixtureSignals.Count -ne 49 -or $uniqueFixtureSignals.Count -ne 49) {
        throw "coordination evidence manifest는 중복 없는 정확히 49개 신호여야 합니다."
    }

    $signalPayloads = @{}
    $entityIdByScenario = @{}
    $transferScopeContract = $null
    foreach ($signalName in $activeFixtureSignals) {
        $signalPath = Join-Path $EvidenceDirectory $signalName
        $signalPayload = Read-JsonFile -Path $signalPath
        $expectedRole = if ($signalName -match "(^|-)a-") { "A" } elseif ($signalName -match "(^|-)b-") { "B" } else { "" }
        if ([string]::IsNullOrWhiteSpace($expectedRole)) {
            throw "coordination evidence role을 파일명에서 결정할 수 없습니다: $signalName"
        }

        $expectedProcessId = if ($expectedRole -eq "A") { [int]$sessionA.ProcessId } else { [int]$sessionB.ProcessId }
        $capturedAtText = [string]$signalPayload.CapturedAtUtc
        $capturedAtUtc = [DateTimeOffset]::MinValue
        $capturedAtValid = $false
        if ($capturedAtText -cmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$') {
            $capturedAtValid = [DateTimeOffset]::TryParse(
                $capturedAtText,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$capturedAtUtc)
        }
        if (
            [string]$signalPayload.RunId -ne $runId -or
            [string]$signalPayload.Nonce -ne $nonce -or
            [string]$signalPayload.Role -ne $expectedRole -or
            [int]$signalPayload.ProcessId -ne $expectedProcessId -or
            -not $capturedAtValid -or
            $capturedAtUtc.Offset -ne [TimeSpan]::Zero) {
            throw "coordination evidence common contract가 run/nonce/role/process/UTC timestamp와 다릅니다: $signalName"
        }

        $scenarioKey = "customer"
        $entityProperty = "CustomerId"
        if ($signalName.StartsWith("item-", [System.StringComparison]::Ordinal)) {
            $scenarioKey = "item"
            $entityProperty = "ItemId"
        }
        elseif ($signalName.StartsWith("rental-", [System.StringComparison]::Ordinal)) {
            $scenarioKey = "rental"
            $entityProperty = "RentalBillingProfileId"
        }
        elseif ($signalName.StartsWith("asset-", [System.StringComparison]::Ordinal)) {
            $scenarioKey = "asset"
            $entityProperty = "RentalAssetId"
        }
        elseif ($signalName.StartsWith("transfer-", [System.StringComparison]::Ordinal)) {
            $scenarioKey = "transfer"
            $entityProperty = "InventoryTransferId"
        }

        if (-not ($signalPayload.PSObject.Properties.Name -contains $entityProperty)) {
            throw "coordination evidence entity property가 없습니다: file=$signalName; property=$entityProperty"
        }
        $entityId = [Guid]::Empty
        if (
            -not [Guid]::TryParse([string]$signalPayload.$entityProperty, [ref]$entityId) -or
            $entityId -eq [Guid]::Empty) {
            throw "coordination evidence entity id가 유효하지 않습니다: file=$signalName; property=$entityProperty"
        }
        if ($entityIdByScenario.ContainsKey($scenarioKey)) {
            if ([Guid]$entityIdByScenario[$scenarioKey] -ne $entityId) {
                throw "coordination evidence entity id가 시나리오 내부에서 바뀌었습니다: scenario=$scenarioKey; file=$signalName"
            }
        }
        else {
            $entityIdByScenario[$scenarioKey] = $entityId
        }

        $revision = 0L
        if (
            -not [long]::TryParse(
                [string]$signalPayload.Revision,
                [System.Globalization.NumberStyles]::Integer,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [ref]$revision) -or
            $revision -le 0L) {
            throw "coordination evidence revision이 양의 정수가 아닙니다: $signalName"
        }

        if ($scenarioKey -eq "transfer") {
            $transferItemId = [Guid]::Empty
            if (
                -not [Guid]::TryParse([string]$signalPayload.ItemId, [ref]$transferItemId) -or
                $transferItemId -eq [Guid]::Empty -or
                [string]::IsNullOrWhiteSpace([string]$signalPayload.TenantCode) -or
                [string]::IsNullOrWhiteSpace([string]$signalPayload.FromWarehouseCode) -or
                [string]::IsNullOrWhiteSpace([string]$signalPayload.ToWarehouseCode) -or
                [string]::Equals(
                    [string]$signalPayload.FromWarehouseCode,
                    [string]$signalPayload.ToWarehouseCode,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "pending 재고이동 coordination scope가 item/tenant/from/to 계약과 다릅니다: $signalName"
            }

            $currentTransferScope = (
                "{0:D}|{1}|{2}|{3}" -f
                    $transferItemId,
                    ([string]$signalPayload.TenantCode).Trim().ToUpperInvariant(),
                    ([string]$signalPayload.FromWarehouseCode).Trim().ToUpperInvariant(),
                    ([string]$signalPayload.ToWarehouseCode).Trim().ToUpperInvariant())
            if ($null -eq $transferScopeContract) {
                $transferScopeContract = $currentTransferScope
            }
            elseif (-not [string]::Equals(
                [string]$transferScopeContract,
                $currentTransferScope,
                [System.StringComparison]::Ordinal)) {
                throw "pending 재고이동 coordination scope가 신호 사이에서 바뀌었습니다: $signalName"
            }
        }

        $signalPayloads[$signalName] = $signalPayload
    }

    $transferCreatedRevision = [long]$signalPayloads["transfer-a-created.json"].Revision
    $transferWrittenRevision = [long]$signalPayloads["transfer-b-written.json"].Revision
    $transferConflictRevision = [long]$signalPayloads["transfer-a-conflict.json"].Revision
    $transferRetriedRevision = [long]$signalPayloads["transfer-a-retried.json"].Revision
    $transferDeletedRevision = [long]$signalPayloads["transfer-b-deleted.json"].Revision
    $transferInitialValue = [string]$signalPayloads["transfer-a-created.json"].Value
    if (-not $transferInitialValue.EndsWith("|INITIAL", [System.StringComparison]::Ordinal)) {
        throw "pending 재고이동 coordination initial marker가 계약과 다릅니다."
    }
    $transferMarker = $transferInitialValue.Substring(
        0,
        $transferInitialValue.Length - "|INITIAL".Length)
    if (
        [string]::IsNullOrWhiteSpace($transferMarker) -or
        [long]$signalPayloads["transfer-b-loaded.json"].Revision -ne $transferCreatedRevision -or
        [long]$signalPayloads["transfer-a-staged.json"].Revision -ne $transferCreatedRevision -or
        $transferWrittenRevision -le $transferCreatedRevision -or
        $transferConflictRevision -ne $transferWrittenRevision -or
        $transferRetriedRevision -le $transferWrittenRevision -or
        $transferDeletedRevision -le $transferRetriedRevision -or
        [long]$signalPayloads["transfer-a-delete-observed.json"].Revision -ne $transferDeletedRevision -or
        [long]$signalPayloads["transfer-b-purged.json"].Revision -ne $transferDeletedRevision -or
        [long]$signalPayloads["transfer-a-clean.json"].Revision -ne $transferDeletedRevision -or
        [string]$signalPayloads["transfer-b-loaded.json"].Value -ne $transferInitialValue -or
        [string]$signalPayloads["transfer-a-staged.json"].Value -ne ($transferMarker + "|A-PENDING") -or
        [string]$signalPayloads["transfer-b-written.json"].Value -ne ($transferMarker + "|B-WINS") -or
        [string]$signalPayloads["transfer-a-conflict.json"].Value -ne ($transferMarker + "|A-PENDING") -or
        [string]$signalPayloads["transfer-a-retried.json"].Value -ne ($transferMarker + "|A-RETRY") -or
        [string]$signalPayloads["transfer-b-deleted.json"].Value -ne "deleted" -or
        [string]$signalPayloads["transfer-a-delete-observed.json"].Value -ne "deleted" -or
        [string]$signalPayloads["transfer-b-purged.json"].Value -ne "purged" -or
        [string]$signalPayloads["transfer-a-clean.json"].Value -ne "purged") {
        throw "pending 재고이동 coordination revision graph가 create/winner/conflict/retry/delete/purge 순서와 다릅니다."
    }

    $assetCreatedRevision = [long]$signalPayloads["asset-a-created.json"].Revision
    $assetWrittenRevision = [long]$signalPayloads["asset-b-written.json"].Revision
    $assetConflictRevision = [long]$signalPayloads["asset-a-conflict.json"].Revision
    $assetRetriedRevision = [long]$signalPayloads["asset-a-retried.json"].Revision
    $assetDeletedRevision = [long]$signalPayloads["asset-b-deleted.json"].Revision
    $assetInitialValue = [string]$signalPayloads["asset-a-created.json"].Value
    if (-not $assetInitialValue.EndsWith("|INITIAL", [System.StringComparison]::Ordinal)) {
        throw "렌탈 자산 coordination initial marker가 계약과 다릅니다."
    }
    $assetMarker = $assetInitialValue.Substring(0, $assetInitialValue.Length - "|INITIAL".Length)
    if (
        [string]::IsNullOrWhiteSpace($assetMarker) -or
        [long]$signalPayloads["asset-b-loaded.json"].Revision -ne $assetCreatedRevision -or
        [long]$signalPayloads["asset-a-staged.json"].Revision -ne $assetCreatedRevision -or
        $assetWrittenRevision -le $assetCreatedRevision -or
        $assetConflictRevision -ne $assetWrittenRevision -or
        $assetRetriedRevision -le $assetWrittenRevision -or
        $assetDeletedRevision -le $assetRetriedRevision -or
        [long]$signalPayloads["asset-a-delete-observed.json"].Revision -ne $assetDeletedRevision -or
        [long]$signalPayloads["asset-b-purged.json"].Revision -ne $assetDeletedRevision -or
        [long]$signalPayloads["asset-a-clean.json"].Revision -ne $assetDeletedRevision -or
        [string]$signalPayloads["asset-b-loaded.json"].Value -ne $assetInitialValue -or
        [string]$signalPayloads["asset-a-staged.json"].Value -ne ($assetMarker + "|A-PENDING") -or
        [string]$signalPayloads["asset-b-written.json"].Value -ne ($assetMarker + "|B-WINS") -or
        [string]$signalPayloads["asset-a-conflict.json"].Value -ne ($assetMarker + "|A-PENDING") -or
        [string]$signalPayloads["asset-a-retried.json"].Value -ne ($assetMarker + "|A-RETRY") -or
        [string]$signalPayloads["asset-b-deleted.json"].Value -ne "deleted" -or
        [string]$signalPayloads["asset-a-delete-observed.json"].Value -ne "deleted" -or
        [string]$signalPayloads["asset-b-purged.json"].Value -ne "purged" -or
        [string]$signalPayloads["asset-a-clean.json"].Value -ne "purged") {
        throw "렌탈 자산 coordination revision/value graph가 create/winner/conflict/retry/delete/purge 순서와 다릅니다."
    }

    $rentalCreatedRevision = [long]$signalPayloads["rental-a-created.json"].Revision
    $rentalWrittenRevision = [long]$signalPayloads["rental-b-written.json"].Revision
    $rentalRetriedRevision = [long]$signalPayloads["rental-a-retried.json"].Revision
    $rentalDeletedRevision = [long]$signalPayloads["rental-b-deleted.json"].Revision
    if (
        [long]$signalPayloads["rental-b-loaded.json"].Revision -ne $rentalCreatedRevision -or
        [long]$signalPayloads["rental-a-staged.json"].Revision -ne $rentalCreatedRevision -or
        $rentalWrittenRevision -le $rentalCreatedRevision -or
        $rentalRetriedRevision -le $rentalWrittenRevision -or
        $rentalDeletedRevision -le $rentalRetriedRevision -or
        [long]$signalPayloads["rental-a-delete-observed.json"].Revision -ne $rentalDeletedRevision -or
        [long]$signalPayloads["rental-b-purged.json"].Revision -ne $rentalDeletedRevision -or
        [long]$signalPayloads["rental-a-clean.json"].Revision -ne $rentalDeletedRevision -or
        [string]$signalPayloads["rental-a-created.json"].Value -ne ("INITIAL-RENTAL-" + $runId) -or
        [string]$signalPayloads["rental-b-loaded.json"].Value -ne ("INITIAL-RENTAL-" + $runId) -or
        [string]$signalPayloads["rental-a-staged.json"].Value -ne ("A-PENDING-RENTAL-" + $runId) -or
        [string]$signalPayloads["rental-b-written.json"].Value -ne ("B-WINS-RENTAL-" + $runId) -or
        [string]$signalPayloads["rental-a-retried.json"].Value -ne ("A-RETRY-RENTAL-" + $runId) -or
        [string]$signalPayloads["rental-b-deleted.json"].Value -ne "deleted" -or
        [string]$signalPayloads["rental-a-delete-observed.json"].Value -ne "deleted" -or
        [string]$signalPayloads["rental-b-purged.json"].Value -ne "purged" -or
        [string]$signalPayloads["rental-a-clean.json"].Value -ne "purged") {
        throw "렌탈 청구 coordination revision/value graph가 create/winner/retry/delete/purge 순서와 다릅니다."
    }
    Add-Step -Steps $steps -Name "coordination-contract" -Passed $true -Detail "20/20 required nonce-bound signals present (legacy customer/item); 49/49 required nonce-bound signals present including rental billing, rental asset, and pending inventory transfer"

    $transientBackupArtifacts = @(
        foreach ($client in $clients) {
            if (-not (Test-Path -LiteralPath $client.AppDataRoot -PathType Container)) {
                continue
            }

            Get-ChildItem -LiteralPath $client.AppDataRoot -Recurse -Force -ErrorAction Stop |
                Where-Object {
                    $_.Name -like ".gp-stage-*" -or
                    $_.Name -like ".gp-validate-*"
                }
        }
    )
    if ($transientBackupArtifacts.Count -gt 0) {
        throw "Desktop 정상 종료 후 임시 백업 산출물이 남았습니다: count=$($transientBackupArtifacts.Count)"
    }
    Add-Step -Steps $steps -Name "transient-backup-cleanup" -Passed $true -Detail "PC-A/PC-B .gp-stage/.gp-validate artifact count=0"

    foreach ($processId in $knownAppProcessIds) {
        if ($null -ne (Get-ProcessByIdSafe -ProcessId $processId)) {
            throw "Desktop 프로세스가 E2E 종료 후 남았습니다: pid=$processId"
        }
    }

    $portProbe = $null
    try {
        $portProbe = [System.Net.Sockets.TcpListener]::new(
            [System.Net.IPAddress]::Loopback,
            $serverPort)
        $portProbe.Start()
    }
    catch {
        throw "runner-owned server port가 E2E 종료 후 해제되지 않았습니다: $serverPort"
    }
    finally {
        if ($null -ne $portProbe) {
            try { $portProbe.Stop() } catch { }
        }
    }
    Add-Step -Steps $steps -Name "process-and-listener-cleanup" -Passed $true -Detail "Desktop pid count=0; loopback listener count=0"

    $overall = "PASS"
}
catch {
    $failureMessage = $_.Exception.Message
    Add-Step -Steps $steps -Name "exception" -Passed $false -Detail $failureMessage
}
finally {
    foreach ($credentialEnvironmentName in $credentialEnvironmentNames) {
        [Environment]::SetEnvironmentVariable(
            $credentialEnvironmentName,
            $null,
            "Process")
    }
    [Environment]::SetEnvironmentVariable(
        $ephemeralBootstrapEnvironmentName,
        $null,
        "Process")
    $loginUsername = $null
    $loginPassword = $null
    $useEphemeralAdminBootstrap = $false

    $runtimeReleaseConfirmed = $false
    try {
        Stop-ExactOwnedProcesses `
            -RoleHosts $roleHosts `
            -MultiPcRoot $multiPcRoot `
            -ServerDirectory $serverDirectory `
            -AttestedServerProcessId $ownedServerProcessId `
            -AttestedServerStartTimeUtcTicks $ownedServerStartTimeUtcTicks
        if (-not (Wait-LoopbackPortReleased -Port $serverPort -TimeoutSeconds 30)) {
            throw "runner-owned server port release timeout: $serverPort"
        }
        if (
            -not [string]::IsNullOrWhiteSpace($serverDatabaseSnapshotPath) -and
            -not (Wait-DatabaseFileSetUnlocked `
                -DatabasePath $serverDatabasePath `
                -TimeoutSeconds 30)
        ) {
            throw "isolated server SQLite file set remained locked after exact process cleanup."
        }

        $runtimeReleaseConfirmed = $true
        Add-Step `
            -Steps $steps `
            -Name "runtime-release-before-rollback" `
            -Passed $true `
            -Detail "attested PID/start-time process stopped; role hosts stopped; loopback port and SQLite file handles released"
    }
    catch {
        $overall = "FAIL"
        $releaseFailure = "Runtime release before rollback failed: $($_.Exception.Message)"
        $failureMessage = if ([string]::IsNullOrWhiteSpace($failureMessage)) {
            $releaseFailure
        }
        else {
            "$failureMessage | $releaseFailure"
        }
        Add-Step `
            -Steps $steps `
            -Name "runtime-release-before-rollback" `
            -Passed $false `
            -Detail $releaseFailure
    }

    foreach ($roleHost in @($roleHosts)) {
        try {
            Complete-RoleHostOutputCapture -RoleHost $roleHost
        }
        catch {
            $overall = "FAIL"
            $outputFailure =
                "PC-$($roleHost.Role) role output capture failed."
            $failureMessage = if ([string]::IsNullOrWhiteSpace($failureMessage)) {
                $outputFailure
            }
            else {
                "$failureMessage | $outputFailure"
            }
            Add-Step `
                -Steps $steps `
                -Name "role-output-capture" `
                -Passed $false `
                -Detail $outputFailure
        }
    }

    if (
        -not [string]::IsNullOrWhiteSpace($serverDatabaseSnapshotPath) -and
        (Test-Path -LiteralPath $serverDatabaseSnapshotPath -PathType Leaf)
    ) {
        if (-not $runtimeReleaseConfirmed) {
            $overall = "FAIL"
            Add-Step `
                -Steps $steps `
                -Name "server-db-rollback" `
                -Passed $false `
                -Detail "rollback skipped because exact process/port/SQLite handle release was not confirmed"
        }
        else {
            try {
            $restoredServerDatabaseSha256 =
                Restore-ServerDatabaseRollbackSnapshot `
                    -DatabasePath $serverDatabasePath `
                    -SnapshotPath $serverDatabaseSnapshotPath `
                    -ExpectedSha256 $serverDatabaseOriginalSha256 `
                    -ServerDirectory $serverDirectory `
                    -MultiPcRoot $multiPcRoot
            Add-Step -Steps $steps -Name "server-db-rollback" -Passed $true -Detail "isolated DB restored exactly; SHA-256 $restoredServerDatabaseSha256"
            if (Test-Path -LiteralPath $bootstrapContractPath -PathType Leaf) {
                Remove-Item -LiteralPath $bootstrapContractPath -Force
            }
            }
            catch {
                $overall = "FAIL"
                $rollbackFailure = "Server DB rollback failed: $($_.Exception.Message)"
                $failureMessage = if ([string]::IsNullOrWhiteSpace($failureMessage)) {
                    $rollbackFailure
                }
                else {
                    "$failureMessage | $rollbackFailure"
                }
                Add-Step -Steps $steps -Name "server-db-rollback" -Passed $false -Detail $rollbackFailure
            }
        }
    }

    if ($null -ne $sourceAppSettingsOriginalBytes) {
        try {
            Restore-FileBytesPreservingMetadata `
                -Path $sourceAppSettings `
                -Bytes $sourceAppSettingsOriginalBytes
            $restoredAppSettingsSha256 =
                (Get-FileHash -LiteralPath $sourceAppSettings -Algorithm SHA256).Hash
            if (-not [string]::Equals(
                $restoredAppSettingsSha256,
                $sourceAppSettingsOriginalSha256,
                [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Source App appsettings restore hash mismatch."
            }
            $restoredAppSettingsSddl =
                (Get-Acl -LiteralPath $sourceAppSettings).Sddl
            if ($restoredAppSettingsSddl -cne $sourceAppSettingsOriginalSddl) {
                throw "Source App appsettings restore ACL mismatch."
            }
            Add-Step -Steps $steps -Name "source-appsettings-rollback" -Passed $true -Detail "source App settings bytes and ACL restored exactly; SHA-256 $restoredAppSettingsSha256"
        }
        catch {
            $overall = "FAIL"
            $settingsFailure = "Source App settings rollback failed: $($_.Exception.Message)"
            $failureMessage = if ([string]::IsNullOrWhiteSpace($failureMessage)) {
                $settingsFailure
            }
            else {
                "$failureMessage | $settingsFailure"
            }
            Add-Step -Steps $steps -Name "source-appsettings-rollback" -Passed $false -Detail $settingsFailure
        }
    }
}

$artifacts = New-Object System.Collections.Generic.List[object]
$artifactPaths = @(
    $contractPath,
    $inventoryTransferUiaEvidencePath,
    (Join-Path $EvidenceDirectory "transfer-b-list-ready.json"),
    (Join-Path $EvidenceDirectory "transfer-b-list-vm-updated.json"),
    (Join-Path $EvidenceDirectory "transfer-b-list-uia-ready.json"),
    (Join-Path $EvidenceDirectory "transfer-b-list-uia-updated.json"),
    (Join-Path $EvidenceDirectory "multi-pc-readiness.md"),
    (Join-Path $EvidenceDirectory "session-A.json"),
    (Join-Path $EvidenceDirectory "session-B.json"),
    (Join-Path $EvidenceDirectory "PC-A\multi-pc-inapp-A.json"),
    (Join-Path $EvidenceDirectory "PC-B\multi-pc-inapp-B.json"),
    (Join-Path $EvidenceDirectory "PC-A\role-host.stdout.log"),
    (Join-Path $EvidenceDirectory "PC-A\role-host.stderr.log"),
    (Join-Path $EvidenceDirectory "PC-B\role-host.stdout.log"),
    (Join-Path $EvidenceDirectory "PC-B\role-host.stderr.log"))
$artifactPaths += @(
    $roleHosts |
        ForEach-Object { [string]$_.UiSmokeJsonPath } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
foreach ($artifactPath in $artifactPaths) {
    if (Test-Path -LiteralPath $artifactPath -PathType Leaf) {
        $item = Get-Item -LiteralPath $artifactPath
        $artifacts.Add([pscustomobject]@{
            Path = $item.FullName
            Length = $item.Length
            Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        }) | Out-Null
    }
}

$summary = [ordered]@{
    SchemaVersion = "1"
    GeneratedAt = (Get-Date).ToString("O")
    Title = "Multi-PC Desktop integration and login/main-window UIA verification"
    Result = $overall
    RunId = $runId
    ApiBaseUrl = $serverUrl
    ProjectRoot = $ProjectRoot
    ExecutionRoot = $ExecutionRoot
    MultiPcRoot = $multiPcRoot
    EvidenceDirectory = $EvidenceDirectory
    EvidenceClassification = [ordered]@{
        LoginAndMainWindow = "actual out-of-process WPF UI Automation"
        InventoryTransferOpenListLiveUpdate = "actual out-of-process WPF UI Automation before/after observations; in-process VM/API/DB signals are separately labeled"
        RentalBillingRentalAssetAndRemainingInventoryTransferFlow = "in-process ViewModel/API/DB integration (not UIA interaction evidence)"
        DeferredInteractionGate = "actual rental billing/rental asset and remaining inventory-transfer interactions remain a separate Codex UI automation gate"
    }
    Failure = $failureMessage
    TotalSteps = $steps.Count
    PassedSteps = @($steps | Where-Object { $_.Passed }).Count
    FailedSteps = @($steps | Where-Object { -not $_.Passed }).Count
    Steps = @($steps.ToArray())
    Artifacts = @($artifacts.ToArray())
}
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Multi-PC Desktop integration and login/main-window UIA verification") | Out-Null
$lines.Add("") | Out-Null
$lines.Add("- 작성시각: $($summary.GeneratedAt)") | Out-Null
$lines.Add("- 결과: **$overall**") | Out-Null
$lines.Add("- RunId: $runId") | Out-Null
$lines.Add("- API: $serverUrl") | Out-Null
$lines.Add("- live/Git/deployment: 실행하지 않음") | Out-Null
$lines.Add("- 로그인/메인 창 증거: actual out-of-process WPF UI Automation") | Out-Null
$lines.Add("- 재고이동 열린 목록 갱신 증거: actual out-of-process WPF UI Automation before/after (동일 HWND/RuntimeId, row count, transfer GUID 행)") | Out-Null
$lines.Add("- 렌탈 청구/렌탈 자산 및 나머지 재고이동 흐름: in-process ViewModel/API/DB integration (UIA 조작 증거 아님)") | Out-Null
$lines.Add("- 후속 gate: 렌탈 청구/렌탈 자산 및 나머지 재고이동 실제 UI 조작은 별도 Codex UI 자동화로 검증") | Out-Null
if (-not [string]::IsNullOrWhiteSpace($failureMessage)) {
    $lines.Add("- 오류: $($failureMessage.Replace('|', '\|'))") | Out-Null
}
$lines.Add("") | Out-Null
$lines.Add("| 단계 | 결과 | 상세 |") | Out-Null
$lines.Add("|---|---|---|") | Out-Null
foreach ($step in $steps) {
    $lines.Add("| $($step.Name) | $(if ($step.Passed) { 'PASS' } else { 'FAIL' }) | $(([string]$step.Detail).Replace('|', '\|')) |") | Out-Null
}
$lines.Add("") | Out-Null
$lines.Add("## 증거 파일") | Out-Null
$lines.Add("") | Out-Null
foreach ($artifact in $artifacts) {
    $lines.Add("- $($artifact.Path) · $($artifact.Length) bytes · SHA-256 $($artifact.Sha256)") | Out-Null
}
$lines.Add("") | Out-Null
$lines.Add("JSON: $summaryPath") | Out-Null
$lines | Set-Content -LiteralPath $markdownPath -Encoding UTF8

Write-Host "Multi-PC Desktop integration/UIA verification: $overall"
Write-Host "JSON report: $summaryPath"
Write-Host "Markdown report: $markdownPath"

if ($overall -ne "PASS") {
    throw "Multi-PC Desktop integration/UIA verification failed. See report: $markdownPath"
}
