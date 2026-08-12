param(
    [string]$AppExe = 'D:\거래플랜\테스트 시행\실행환경\App\거래플랜.Desktop.App.exe',
    [string]$Username = '',
    [string]$Password = '',
    [switch]$EnableEphemeralAdminBootstrap,
    [string]$EphemeralAdminPassword = '',
    [string]$BootstrapContractPath = '',
    [string]$BootstrapContractSha256 = '',
    [switch]$ValidateEphemeralBootstrapContractOnly,
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'output\desktop-ui-smoke'),
    [int]$TimeoutSec = 45,
    [ValidateRange(1, 1800)]
    [int]$InAppSelfTestTimeoutSec = 160,
    [switch]$StartServer,
    [string]$DotnetExe = 'D:\.dotnet-sdk\dotnet.exe',
    [string]$ServerDir = 'D:\거래플랜\테스트 시행\실행환경\Server',
    [string]$ServerDataRoot = 'D:\거래플랜\테스트 시행\실행환경\ServerData',
    [string]$AppDataRoot = 'D:\거래플랜\테스트 시행\실행환경\AppData',
    [int]$ServerPort = 19080,
    [switch]$UseInAppSelfTest,
    [string]$InAppSelfTestReportPath,
    [string]$LoginInputMutexName = '',
    [switch]$AttachExisting,
    [switch]$KeepAppOpen
)

$ErrorActionPreference = 'Stop'
foreach ($seedEnvironmentName in @(
    [Environment]::GetEnvironmentVariables('Process').Keys)) {
    if (([string]$seedEnvironmentName).StartsWith(
        'SeedUsers__',
        [System.StringComparison]::OrdinalIgnoreCase)) {
        [Environment]::SetEnvironmentVariable(
            [string]$seedEnvironmentName,
            $null,
            'Process')
    }
}
foreach ($credentialEnvironmentName in @(
    'GEORAEPLAN_TEST_USERNAME',
    'GEORAEPLAN_TEST_PASSWORD'
)) {
    [Environment]::SetEnvironmentVariable(
        $credentialEnvironmentName,
        $null,
        'Process')
}

function Get-BootstrapStringSha256 {
    param([Parameter(Mandatory = $true)][string]$Value)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return ([System.BitConverter]::ToString(
            $sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-BootstrapDatabaseFileSetSha256 {
    param([Parameter(Mandatory = $true)][string]$DatabasePath)

    $entries = New-Object System.Collections.Generic.List[string]
    foreach ($suffix in @('', '-wal', '-shm', '-journal')) {
        $path = "$DatabasePath$suffix"
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $item = Get-Item -LiteralPath $path -Force
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            $entries.Add("$suffix`t$($item.Length)`t$hash")
        }
    }
    if ($entries.Count -eq 0) {
        throw 'Ephemeral admin bootstrap contract validation failed.'
    }
    return Get-BootstrapStringSha256 -Value ([string]::Join("`n", $entries))
}

function Assert-NoBootstrapReparsePoint {
    param([Parameter(Mandatory = $true)][string]$Path)

    $normalizedPath = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($normalizedPath)
    $current = $root
    foreach ($segment in $normalizedPath.Substring($root.Length).Split(
        [char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar),
        [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) {
            throw 'Ephemeral admin bootstrap contract validation failed.'
        }
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Ephemeral admin bootstrap contract validation failed.'
        }
    }
}

function Read-BootstrapMarkerValues {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    foreach ($rawLine in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $line = ([string]$rawLine).Trim()
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $separatorIndex = $line.IndexOf('=')
        if ($separatorIndex -le 0 -or $separatorIndex -eq ($line.Length - 1)) {
            throw 'Ephemeral admin bootstrap contract validation failed.'
        }
        $key = $line.Substring(0, $separatorIndex).Trim()
        if ($values.ContainsKey($key)) {
            throw 'Ephemeral admin bootstrap contract validation failed.'
        }
        $values[$key] = $line.Substring($separatorIndex + 1).Trim()
    }
    return $values
}

function Remove-SeedUsersEnvironmentVariablesFromStartInfo {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.ProcessStartInfo]$StartInfo
    )

    foreach ($environmentName in @($StartInfo.EnvironmentVariables.Keys)) {
        if (([string]$environmentName).StartsWith(
            'SeedUsers__',
            [System.StringComparison]::OrdinalIgnoreCase)) {
            [void]$StartInfo.EnvironmentVariables.Remove(
                [string]$environmentName)
        }
    }
}

function Set-EphemeralAdminSeedEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.ProcessStartInfo]$StartInfo,
        [Parameter(Mandatory = $true)][string]$AdminPassword
    )

    Remove-SeedUsersEnvironmentVariablesFromStartInfo -StartInfo $StartInfo
    $adminOnlySeedEnvironment = [ordered]@{
        'SeedUsers__EnableSeedUsers' = 'true'
        'SeedUsers__WarnOnDefaultPasswords' = 'false'
        'SeedUsers__AdminOnlyBootstrap' = 'true'
        'SeedUsers__AdminPassword' = $AdminPassword
        'SeedUsers__UpdateExistingAdminPassword' = 'true'
        'SeedUsers__UserPassword' = ''
        'SeedUsers__ItwPassword' = ''
        'SeedUsers__UpdateExistingItwPassword' = 'false'
        'SeedUsers__UsenetUsername' = ''
        'SeedUsers__UsenetPassword' = ''
        'SeedUsers__UpdateExistingUsenetPassword' = 'false'
    }
    foreach ($entry in $adminOnlySeedEnvironment.GetEnumerator()) {
        $StartInfo.EnvironmentVariables[[string]$entry.Key] = [string]$entry.Value
    }
}

function Assert-EphemeralAdminBootstrapContract {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptRoot,
        [Parameter(Mandatory = $true)][string]$ServerDir,
        [Parameter(Mandatory = $true)][string]$ServerDataRoot,
        [Parameter(Mandatory = $true)][string]$ContractPath,
        [Parameter(Mandatory = $true)][string]$ContractSha256
    )

    try {
        $canonicalProjectRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $ScriptRoot '..\..'))
        $canonicalExecutionRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $canonicalProjectRoot '테스트 시행\실행환경'))
        $canonicalServerDir = [System.IO.Path]::GetFullPath(
            (Join-Path $canonicalExecutionRoot 'Server'))
        $canonicalServerDataRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $canonicalExecutionRoot 'ServerData'))
        if (
            -not [string]::Equals(
                [System.IO.Path]::GetFullPath($ServerDir),
                $canonicalServerDir,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [System.IO.Path]::GetFullPath($ServerDataRoot),
                $canonicalServerDataRoot,
                [System.StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'invalid'
        }

        Assert-NoBootstrapReparsePoint -Path $ContractPath
        $contract = Get-Content -LiteralPath $ContractPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
        $runId = [string]$contract.RunId
        if ($runId -notmatch '^[0-9a-f]{32}$') { throw 'invalid' }
        $canonicalRollbackRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $canonicalExecutionRoot "MultiPC\.rollback\$runId"))
        $canonicalContractPath = Join-Path $canonicalRollbackRoot 'bootstrap-contract.json'
        if (
            -not [string]::Equals(
                [System.IO.Path]::GetFullPath($ContractPath),
                $canonicalContractPath,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            $ContractSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
            -not [string]::Equals(
                (Get-FileHash -LiteralPath $ContractPath -Algorithm SHA256).Hash,
                $ContractSha256,
                [System.StringComparison]::OrdinalIgnoreCase)
        ) { throw 'invalid' }

        $canonicalMarkerPath = Join-Path $canonicalExecutionRoot '.georaeplan-runtime-ready'
        $canonicalSnapshotPath = Join-Path $canonicalRollbackRoot 'server-before.db'
        $canonicalServerDatabasePath = Join-Path $canonicalServerDir '거래플랜-local.db'
        if (-not [string]::Equals(
            [string]$contract.SchemaVersion,
            '1',
            [System.StringComparison]::Ordinal)) { throw 'invalid' }
        $contractChecks = @(
            @([string]$contract.ExecutionRoot, $canonicalExecutionRoot),
            @([string]$contract.ServerDirectory, $canonicalServerDir),
            @([string]$contract.ServerDataRoot, $canonicalServerDataRoot),
            @([string]$contract.RuntimeMarkerPath, $canonicalMarkerPath),
            @([string]$contract.SnapshotPath, $canonicalSnapshotPath)
        )
        foreach ($check in $contractChecks) {
            if (-not [string]::Equals(
                [System.IO.Path]::GetFullPath([string]$check[0]),
                [System.IO.Path]::GetFullPath([string]$check[1]),
                [System.StringComparison]::OrdinalIgnoreCase)) {
                throw 'invalid'
            }
        }

        $serverDllPath = [System.IO.Path]::GetFullPath([string]$contract.ServerDllPath)
        if (
            -not [string]::Equals(
                (Split-Path -Parent $serverDllPath),
                $canonicalServerDir,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            -not $serverDllPath.EndsWith(
                '.Server.Api.dll',
                [System.StringComparison]::OrdinalIgnoreCase)
        ) { throw 'invalid' }
        foreach ($path in @(
            $canonicalExecutionRoot,
            $canonicalServerDir,
            $canonicalServerDataRoot,
            $canonicalMarkerPath,
            $serverDllPath,
            $canonicalRollbackRoot,
            $canonicalSnapshotPath,
            $canonicalServerDatabasePath)) {
            Assert-NoBootstrapReparsePoint -Path $path
        }

        $marker = Read-BootstrapMarkerValues -Path $canonicalMarkerPath
        if (
            -not [string]::Equals([string]$contract.Role, 'A', [System.StringComparison]::Ordinal) -or
            -not [string]::Equals([Environment]::GetEnvironmentVariable('GEORAEPLAN_MULTI_PC_E2E_ROLE', 'Process'), 'A', [System.StringComparison]::Ordinal) -or
            -not [string]::Equals([Environment]::GetEnvironmentVariable('GEORAEPLAN_MULTI_PC_RUNTIME_ROOT', 'Process'), $canonicalExecutionRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([Environment]::GetEnvironmentVariable('GEORAEPLAN_MULTI_PC_CERTIFICATION_ID', 'Process'), [string]$contract.CertificationId, [System.StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$marker.runtime_ready, 'True', [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string]$marker.runtime_root, $canonicalExecutionRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string]$marker.runtime_physical_root, $canonicalExecutionRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string]$marker.certification_id, [string]$contract.CertificationId, [System.StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$marker.server_dll_sha256, [string]$contract.ServerDllSha256, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals((Get-FileHash -LiteralPath $canonicalMarkerPath -Algorithm SHA256).Hash, [string]$contract.RuntimeMarkerSha256, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals((Get-FileHash -LiteralPath $serverDllPath -Algorithm SHA256).Hash, [string]$contract.ServerDllSha256, [System.StringComparison]::OrdinalIgnoreCase)
        ) { throw 'invalid' }

        $snapshotSha256 = Get-BootstrapDatabaseFileSetSha256 -DatabasePath $canonicalSnapshotPath
        if (
            -not [string]::Equals($snapshotSha256, [string]$contract.SnapshotSha256, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals((Get-BootstrapDatabaseFileSetSha256 -DatabasePath $canonicalServerDatabasePath), $snapshotSha256, [System.StringComparison]::OrdinalIgnoreCase)
        ) { throw 'invalid' }

        $createdAtUtc = [DateTimeOffset]::Parse(
            [string]$contract.CreatedAtUtc,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind)
        $snapshotLastWriteUtc = [DateTimeOffset](Get-Item -LiteralPath $canonicalSnapshotPath).LastWriteTimeUtc
        if (
            $createdAtUtc -lt $snapshotLastWriteUtc.AddSeconds(-2) -or
            $createdAtUtc -lt [DateTimeOffset]::UtcNow.AddMinutes(-5) -or
            $createdAtUtc -gt [DateTimeOffset]::UtcNow.AddMinutes(5)
        ) { throw 'invalid' }

        return [pscustomobject]@{
            ServerDllPath = $serverDllPath
            ExecutionRoot = $canonicalExecutionRoot
            SnapshotPath = $canonicalSnapshotPath
            SnapshotSha256 = $snapshotSha256
        }
    }
    catch {
        throw 'Ephemeral admin bootstrap contract validation failed.'
    }
}

if ($ValidateEphemeralBootstrapContractOnly) {
    [void](Assert-EphemeralAdminBootstrapContract `
        -ScriptRoot $PSScriptRoot `
        -ServerDir $ServerDir `
        -ServerDataRoot $ServerDataRoot `
        -ContractPath $BootstrapContractPath `
        -ContractSha256 $BootstrapContractSha256)
    $probeStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    Set-EphemeralAdminSeedEnvironment `
        -StartInfo $probeStartInfo `
        -AdminPassword 'validation-only'
    $seedKeys = @(
        $probeStartInfo.EnvironmentVariables.Keys |
            Where-Object {
                ([string]$_).StartsWith(
                    'SeedUsers__',
                    [System.StringComparison]::OrdinalIgnoreCase)
            } |
            Sort-Object)
    $expectedSeedKeys = @(
        'SeedUsers__AdminOnlyBootstrap',
        'SeedUsers__AdminPassword',
        'SeedUsers__EnableSeedUsers',
        'SeedUsers__ItwPassword',
        'SeedUsers__UpdateExistingAdminPassword',
        'SeedUsers__UpdateExistingItwPassword',
        'SeedUsers__UpdateExistingUsenetPassword',
        'SeedUsers__UsenetPassword',
        'SeedUsers__UsenetUsername',
        'SeedUsers__UserPassword',
        'SeedUsers__WarnOnDefaultPasswords'
    ) | Sort-Object
    if ([string]::Join("`n", $seedKeys) -cne [string]::Join("`n", $expectedSeedKeys)) {
        throw 'Ephemeral admin bootstrap seed environment validation failed.'
    }
    Write-Output 'PASS'
    return
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class GeoraePlanDesktopUiSmokeMouse {
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
"@

function Get-FreePort {
    param([int]$StartingPort = 19080)

    $port = $StartingPort
    while ($true) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
            $listener.Start()
            return $port
        }
        catch {
            $port++
        }
        finally {
            if ($null -ne $listener) {
                try { $listener.Stop() } catch { }
            }
        }
    }
}

function Test-LooksLikeTestRuntimePath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    try {
        $normalized = [System.IO.Path]::GetFullPath($Path)
    }
    catch {
        $normalized = $Path
    }

    return $normalized.IndexOf('테스트 시행', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Wait-HttpReady {
    param(
        [string]$Url,
        [int]$TimeoutSeconds = 60
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                return $true
            }
        }
        catch {
            # wait and retry
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Stop-ExactProcessAndWait {
    param(
        [int]$ProcessId,
        [long]$ExpectedStartTimeUtcTicks,
        [int]$TimeoutMilliseconds = 15000
    )

    if ($ProcessId -le 0 -or $ExpectedStartTimeUtcTicks -le 0) {
        throw '정리 대상 process identity가 유효하지 않습니다.'
    }

    $currentProcess = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $currentProcess) {
        return
    }

    $actualStartTimeUtcTicks = $currentProcess.StartTime.ToUniversalTime().Ticks
    if ($actualStartTimeUtcTicks -ne $ExpectedStartTimeUtcTicks) {
        return
    }

    $currentProcess.Kill()
    if (-not $currentProcess.WaitForExit($TimeoutMilliseconds)) {
        throw "exact process 종료 시간이 초과되었습니다: pid=$ProcessId"
    }
    $currentProcess.Refresh()
    if (-not $currentProcess.HasExited) {
        throw "exact process가 종료되지 않았습니다: pid=$ProcessId"
    }
}

function Wait-LoopbackPortReleased {
    param(
        [int]$Port,
        [int]$TimeoutSeconds = 30
    )

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

function Invoke-HiddenSetApiBaseUrl {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string[]]$AppSettingsPaths
    )

    $toPowerShellLiteral = {
        param([Parameter(Mandatory = $true)][string]$Value)

        return "'" + $Value.Replace("'", "''") + "'"
    }
    $scriptLiteral =
        & $toPowerShellLiteral ([IO.Path]::GetFullPath($ScriptPath))
    $baseUrlLiteral = & $toPowerShellLiteral $BaseUrl
    $appSettingsLiterals = @(
        $AppSettingsPaths |
            ForEach-Object {
                & $toPowerShellLiteral ([IO.Path]::GetFullPath($_))
            }
    )
    if ($appSettingsLiterals.Count -eq 0) {
        throw 'At least one appsettings path is required.'
    }

    $command = (
        '$ProgressPreference = ''SilentlyContinue''; ' +
        '$ErrorActionPreference = ''Stop''; & ' +
        $scriptLiteral +
        ' -BaseUrl ' +
        $baseUrlLiteral +
        ' -AppSettingsPaths @(' +
        ($appSettingsLiterals -join ',') +
        ') 3>&1 4>&1 5>&1 6>&1'
    )
    $encodedCommand = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($command))
    $windowsPowerShellPath = Join-Path `
        ([Environment]::SystemDirectory) `
        'WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $windowsPowerShellPath -PathType Leaf)) {
        throw 'The absolute Windows PowerShell path was not found.'
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $windowsPowerShellPath
    $startInfo.Arguments = (
        '-NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
        "-EncodedCommand $encodedCommand"
    )
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'The hidden Set-ApiBaseUrl process did not start.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $stdout
            StandardError = $stderr
        }
    }
    finally {
        $process.Dispose()
    }
}

function Wait-FileReady {
    param(
        [string]$Path,
        [int]$TimeoutSeconds = 120
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            try {
                $content = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
                if (-not [string]::IsNullOrWhiteSpace($content)) {
                    return $true
                }
            }
            catch {
                # 파일 기록 중일 수 있으므로 재시도합니다.
            }
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Start-IsolatedTestServer {
    param(
        [string]$DotnetExe,
        [string]$ServerDir,
        [string]$ServerDataRoot,
        [string]$ServerDllPath,
        [string]$ServerUrl,
        [bool]$EnableEphemeralAdminBootstrap,
        [string]$EphemeralAdminPassword
    )

    if (-not (Test-Path -LiteralPath $DotnetExe)) {
        throw "dotnet not found: $DotnetExe"
    }

    $serverDll = if ([string]::IsNullOrWhiteSpace($ServerDllPath)) {
        Get-ChildItem -LiteralPath $ServerDir -Filter '*.Server.Api.dll' -File |
            Select-Object -First 1 -ExpandProperty FullName
    }
    else {
        [System.IO.Path]::GetFullPath($ServerDllPath)
    }
    if ([string]::IsNullOrWhiteSpace($serverDll) -or -not (Test-Path -LiteralPath $serverDll)) {
        throw "Server dll not found in $ServerDir"
    }

    $serverEnv = @{
        'ASPNETCORE_ENVIRONMENT' = 'Development'
        'DOTNET_ENVIRONMENT' = 'Development'
        'ASPNETCORE_URLS' = $ServerUrl
        'Kestrel__Endpoints__Http__Url' = $ServerUrl
        'ERP_DB_FALLBACK_SQLITE' = '1'
        'Logging__LogLevel__Default' = 'Warning'
        'Logging__LogLevel__Microsoft' = 'Warning'
        'Logging__LogLevel__Microsoft.EntityFrameworkCore' = 'Warning'
        'FileStorage__RootPath' = (Join-Path $ServerDataRoot 'FileStore')
        'Updates__StorageRoot' = (Join-Path $ServerDataRoot 'updates')
    }
    if ($EnableEphemeralAdminBootstrap -and
        [string]::IsNullOrWhiteSpace($EphemeralAdminPassword)) {
        throw 'Ephemeral admin bootstrap configuration is incomplete.'
    }
    if (-not $EnableEphemeralAdminBootstrap -and
        -not [string]::IsNullOrEmpty($EphemeralAdminPassword)) {
        throw 'Ephemeral admin bootstrap configuration is incomplete.'
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    try {
        $startInfo.FileName = $DotnetExe
        $startInfo.Arguments = ('"{0}" --environment Development' -f $serverDll.Replace('"', '""'))
        $startInfo.WorkingDirectory = $ServerDir
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        Remove-SeedUsersEnvironmentVariablesFromStartInfo -StartInfo $startInfo
        foreach ($key in $serverEnv.Keys) {
            $startInfo.EnvironmentVariables[$key] = [string]$serverEnv[$key]
        }
        if ($EnableEphemeralAdminBootstrap) {
            Set-EphemeralAdminSeedEnvironment `
                -StartInfo $startInfo `
                -AdminPassword $EphemeralAdminPassword
        }
        $serverProcess = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $serverProcess) {
            throw 'The isolated test server process did not start.'
        }
        return $serverProcess
    }
    finally {
        Remove-SeedUsersEnvironmentVariablesFromStartInfo -StartInfo $startInfo
        $EphemeralAdminPassword = $null
    }
}

function New-DirectoryIfMissing {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Convert-OutputText {
    param([object[]]$Output)
    ($Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
}

function New-Condition {
    param(
        [System.Windows.Automation.AutomationProperty]$Property,
        [object]$Value
    )
    New-Object System.Windows.Automation.PropertyCondition($Property, $Value)
}

function New-AndCondition {
    param([System.Windows.Automation.Condition[]]$Conditions)
    New-Object System.Windows.Automation.AndCondition(, $Conditions)
}

function Get-ProcessWindow {
    param(
        [int]$ProcessId,
        [string]$Name,
        [switch]$Contains,
        [int]$TimeoutSec = 20
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $condition = New-AndCondition @(
        (New-Condition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty) $ProcessId),
        (New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) ([System.Windows.Automation.ControlType]::Window))
    )

    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $processWindow = $null
        try {
            $process = Get-Process -Id $ProcessId -ErrorAction Stop
            if ($process.MainWindowHandle -ne 0) {
                $processWindow =
                    [System.Windows.Automation.AutomationElement]::FromHandle(
                        $process.MainWindowHandle)
            }
        }
        catch {
            $processWindow = $null
        }

        if ($null -ne $processWindow) {
            $processWindowName = [string]$processWindow.Current.Name
            if (
                ($Contains -and
                 $processWindowName.IndexOf(
                     $Name,
                     [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
                (-not $Contains -and
                 [string]::Equals(
                     $processWindowName,
                     $Name,
                     [System.StringComparison]::OrdinalIgnoreCase))
            ) {
                return $processWindow
            }
        }

        $topLevelWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)

        $nestedWindows = New-Object System.Collections.Generic.List[System.Windows.Automation.AutomationElement]
        foreach ($topLevelWindow in $topLevelWindows) {
            $descendantWindows = $topLevelWindow.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) ([System.Windows.Automation.ControlType]::Window)))
            foreach ($descendantWindow in $descendantWindows) {
                if ($descendantWindow.Current.ProcessId -eq $ProcessId) {
                    $nestedWindows.Add($descendantWindow) | Out-Null
                }
            }
        }

        $windows = @($topLevelWindows) + @($nestedWindows)
        foreach ($window in $windows) {
            $currentName = [string]$window.Current.Name
            if ($Contains) {
                if ($currentName.IndexOf($Name, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    return $window
                }
            }
            elseif ([string]::Equals($currentName, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $window
            }
        }

        foreach ($window in $nestedWindows) {
            if (Test-NameExists -Root $window -Name $Name) {
                return $window
            }
        }

        Start-Sleep -Milliseconds 300
    }

    return $null
}

function Test-IsLoginWindow {
    param(
        [System.Windows.Automation.AutomationElement]$Window
    )

    if ($null -eq $Window) {
        return $false
    }

    try {
        $windowName = [string]$Window.Current.Name
        if ($windowName.IndexOf('로그인', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }

        $usernameBox = Find-FirstByAutomationId -Root $Window -AutomationId 'UsernameBox'
        $passwordBox = Find-FirstByAutomationId -Root $Window -AutomationId 'PasswordBox'
        return $null -ne $usernameBox -and $null -ne $passwordBox
    }
    catch {
        return $false
    }
}

function Test-IsMainWindow {
    param(
        [System.Windows.Automation.AutomationElement]$Window
    )

    if ($null -eq $Window -or (Test-IsLoginWindow -Window $Window)) {
        return $false
    }

    try {
        $customerSettingsButton =
            Find-FirstByAutomationId -Root $Window -AutomationId 'CustomerSettingsButton'
        $rentalManagementButton =
            Find-FirstByAutomationId -Root $Window -AutomationId 'RentalManagementButton'
        return $null -ne $customerSettingsButton -and $null -ne $rentalManagementButton
    }
    catch {
        return $false
    }
}

function Get-ProcessWindowNames {
    param([int]$ProcessId)

    $condition = New-AndCondition @(
        (New-Condition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty) $ProcessId),
        (New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) ([System.Windows.Automation.ControlType]::Window))
    )
    $topLevelWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)

    $names = @()
    foreach ($window in $topLevelWindows) {
        $name = [string]$window.Current.Name
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $names += $name
        }

        $descendantWindows = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) ([System.Windows.Automation.ControlType]::Window)))
        foreach ($descendantWindow in $descendantWindows) {
            if ($descendantWindow.Current.ProcessId -ne $ProcessId) {
                continue
            }

            $descendantName = [string]$descendantWindow.Current.Name
            if ([string]::IsNullOrWhiteSpace($descendantName)) {
                $descendantName = (($descendantWindow.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition) |
                    ForEach-Object { [string]$_.Current.Name } |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                    Select-Object -First 1) -join '')
            }

            if (-not [string]::IsNullOrWhiteSpace($descendantName)) {
                $names += $descendantName
            }
        }
    }

    $names
}

function Wait-LoginOrMainWindow {
    param(
        [int]$ProcessId,
        [int]$TimeoutSec = 45
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $loginWindow = Get-ProcessWindow -ProcessId $ProcessId -Name '로그인' -Contains -TimeoutSec 1
        if ($null -ne $loginWindow -and (Test-IsLoginWindow -Window $loginWindow)) {
            return [pscustomobject]@{ Kind = 'Login'; Window = $loginWindow }
        }

        $mainWindow = Get-ProcessWindow -ProcessId $ProcessId -Name '거래플랜' -Contains -TimeoutSec 1
        if ($null -ne $mainWindow) {
            if (Test-IsLoginWindow -Window $mainWindow) {
                return [pscustomobject]@{ Kind = 'Login'; Window = $mainWindow }
            }

            if (Test-IsMainWindow -Window $mainWindow) {
                return [pscustomobject]@{ Kind = 'Main'; Window = $mainWindow }
            }
        }

        Start-Sleep -Milliseconds 300
    }

    return [pscustomobject]@{ Kind = 'None'; Window = $null }
}

function Wait-MainWindowOnly {
    param(
        [int]$ProcessId,
        [int]$TimeoutSec = 90
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $mainWindow = Get-ProcessWindow -ProcessId $ProcessId -Name '거래플랜' -Contains -TimeoutSec 1
        if ($null -ne $mainWindow -and (Test-IsMainWindow -Window $mainWindow)) {
            return $mainWindow
        }

        Start-Sleep -Milliseconds 300
    }

    return $null
}

function Find-RedirectedAppProcess {
    param(
        [string]$OriginalExe,
        [datetime]$StartedAfter
    )

    $normalizedOriginalExe = ''
    try {
        $normalizedOriginalExe = [System.IO.Path]::GetFullPath($OriginalExe)
    }
    catch {
        $normalizedOriginalExe = $OriginalExe
    }

    Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                if ($_.HasExited -or [string]::IsNullOrWhiteSpace($_.Path)) {
                    return $false
                }

                $processPath = [System.IO.Path]::GetFullPath($_.Path)
                if ([string]::Equals($processPath, $normalizedOriginalExe, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $false
                }

                if ($_.StartTime -lt $StartedAfter.AddSeconds(-5)) {
                    return $false
                }

                $nameLooksLikeApp = $_.ProcessName.IndexOf('거래플랜', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                    $_.ProcessName.IndexOf('tradeplan', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
                $windowLooksLikeApp = ([string]$_.MainWindowTitle).IndexOf('거래플랜', [System.StringComparison]::OrdinalIgnoreCase) -ge 0

                return ($nameLooksLikeApp -or $windowLooksLikeApp)
            }
            catch {
                return $false
            }
        } |
        Sort-Object StartTime -Descending -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

function Find-FirstByName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )

    $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Condition ([System.Windows.Automation.AutomationElement]::NameProperty) $Name))
}

function Find-FirstByNameAndControlType {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [System.Windows.Automation.ControlType]$ControlType
    )

    $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-AndCondition @(
            (New-Condition ([System.Windows.Automation.AutomationElement]::NameProperty) $Name),
            (New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) $ControlType)
        )))
}

function Find-FirstByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )

    $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Condition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) $AutomationId))
}

function Find-AllByControlType {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType
    )

    $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) $ControlType))
}

function Find-RootByNameAndControlType {
    param(
        [string]$Name,
        [System.Windows.Automation.ControlType]$ControlType
    )

    [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-AndCondition @(
            (New-Condition ([System.Windows.Automation.AutomationElement]::NameProperty) $Name),
            (New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) $ControlType)
        )))
}

function Normalize-WindowForSmoke {
    param([System.Windows.Automation.AutomationElement]$Window)
    if ($null -eq $Window) { return }

    try {
        $handle = [IntPtr]$Window.Current.NativeWindowHandle
        if ($handle -eq [IntPtr]::Zero) {
            return
        }

        [void][GeoraePlanDesktopUiSmokeMouse]::ShowWindow($handle, 9) # SW_RESTORE
        Start-Sleep -Milliseconds 150

        $workingArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
        $width = [Math]::Max(900, [Math]::Min(1800, $workingArea.Width - 40))
        $height = [Math]::Max(700, [Math]::Min(1000, $workingArea.Height - 40))
        [void][GeoraePlanDesktopUiSmokeMouse]::SetWindowPos(
            $handle,
            [IntPtr]::Zero,
            $workingArea.Left + 20,
            $workingArea.Top + 20,
            $width,
            $height,
            0x0004) # SWP_NOZORDER
        [void][GeoraePlanDesktopUiSmokeMouse]::SetForegroundWindow($handle)
        Start-Sleep -Milliseconds 300
    }
    catch {
        # UI 스모크 보조 동작이므로 창 위치 보정 실패는 본 검증 흐름을 막지 않습니다.
    }
}

function Invoke-Element {
    param([System.Windows.Automation.AutomationElement]$Element)
    if ($null -eq $Element) { return $false }

    try {
        $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        if ($null -ne $pattern) {
            $pattern.Invoke()
            return $true
        }
    }
    catch {
        return $false
    }

    return $false
}

function Click-Element {
    param([System.Windows.Automation.AutomationElement]$Element)
    if ($null -eq $Element) { return $false }

    if (Invoke-Element $Element) {
        Start-Sleep -Milliseconds 700
        return $true
    }

    try {
        $Element.SetFocus()
        Start-Sleep -Milliseconds 100
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Start-Sleep -Milliseconds 700
        return $true
    }
    catch {
        # 일부 WPF 요소는 포커스 설정이 제한될 수 있으므로 좌표 클릭을 계속 시도합니다.
    }

    try {
        $rect = $Element.Current.BoundingRectangle
        if ($rect.Width -gt 1 -and $rect.Height -gt 1) {
            $x = [int]($rect.Left + ($rect.Width / 2))
            $y = [int]($rect.Top + ($rect.Height / 2))
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
            Start-Sleep -Milliseconds 100
            [GeoraePlanDesktopUiSmokeMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 80
            [GeoraePlanDesktopUiSmokeMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 500
            return $true
        }
    }
    catch {
        # 좌표 클릭 실패 시 InvokePattern으로 fallback합니다.
    }

    return Invoke-Element $Element
}

function Click-ElementByCoordinates {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [int]$DelayMilliseconds = 700
    )
    if ($null -eq $Element) { return $false }

    try {
        $rect = $Element.Current.BoundingRectangle
        if ($rect.Width -le 1 -or $rect.Height -le 1) {
            return $false
        }

        $x = [int]($rect.Left + ($rect.Width / 2))
        $y = [int]($rect.Top + ($rect.Height / 2))
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
        Start-Sleep -Milliseconds 120
        [GeoraePlanDesktopUiSmokeMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 90
        [GeoraePlanDesktopUiSmokeMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds $DelayMilliseconds
        return $true
    }
    catch {
        return $false
    }
}

function Invoke-CustomerManagementMenuItem {
    param([int]$TimeoutSeconds = 4)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $menuItem = Find-RootByNameAndControlType -Name '거래처 관리' -ControlType ([System.Windows.Automation.ControlType]::MenuItem)
        if ($null -ne $menuItem) {
            if (Click-Element $menuItem) {
                return $true
            }

            return (Click-ElementByCoordinates -Element $menuItem -DelayMilliseconds 700)
        }

        Start-Sleep -Milliseconds 200
    }

    return $false
}

function Test-SameAutomationElement {
    param(
        [System.Windows.Automation.AutomationElement]$Left,
        [System.Windows.Automation.AutomationElement]$Right
    )

    if ($null -eq $Left -or $null -eq $Right) {
        return $false
    }

    try {
        $leftId = @($Left.GetRuntimeId())
        $rightId = @($Right.GetRuntimeId())
        if ($leftId.Count -ne $rightId.Count) {
            return $false
        }
        for ($index = 0; $index -lt $leftId.Count; $index++) {
            if ([int]$leftId[$index] -ne [int]$rightId[$index]) {
                return $false
            }
        }
        return $true
    }
    catch {
        return $false
    }
}

function Assert-LoginInputTarget {
    param(
        [System.Windows.Automation.AutomationElement]$LoginWindow,
        [System.Windows.Automation.AutomationElement]$Element,
        [int]$ExpectedProcessId,
        [string]$ExpectedAutomationId
    )

    if ($null -eq $LoginWindow -or $null -eq $Element) {
        throw '로그인 입력 대상이 없습니다.'
    }

    $windowHandle = [IntPtr]$LoginWindow.Current.NativeWindowHandle
    if (
        $windowHandle -eq [IntPtr]::Zero -or
        [int]$LoginWindow.Current.ProcessId -ne $ExpectedProcessId -or
        [int]$Element.Current.ProcessId -ne $ExpectedProcessId -or
        -not [string]::Equals(
            [string]$Element.Current.AutomationId,
            $ExpectedAutomationId,
            [System.StringComparison]::Ordinal)
    ) {
        throw '로그인 입력 대상의 HWND/PID/AutomationId identity가 일치하지 않습니다.'
    }

    $nativeProcessId = [uint32]0
    [void][GeoraePlanDesktopUiSmokeMouse]::GetWindowThreadProcessId(
        $windowHandle,
        [ref]$nativeProcessId)
    if ([int]$nativeProcessId -ne $ExpectedProcessId) {
        throw '로그인 창 HWND의 native process identity가 일치하지 않습니다.'
    }

    $resolvedElement = $LoginWindow.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-AndCondition @(
            (New-Condition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty) $ExpectedProcessId),
            (New-Condition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) $ExpectedAutomationId)
        )))
    if (-not (Test-SameAutomationElement -Left $Element -Right $resolvedElement)) {
        throw '로그인 입력 대상이 attested 로그인 창의 해당 AutomationId descendant가 아닙니다.'
    }

    return $windowHandle
}

function Set-ElementText {
    param(
        [System.Windows.Automation.AutomationElement]$LoginWindow,
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Text,
        [int]$ExpectedProcessId,
        [string]$ExpectedAutomationId
    )

    if ($null -eq $Element -or [string]::IsNullOrEmpty($Text)) {
        return $false
    }

    [void](Assert-LoginInputTarget `
        -LoginWindow $LoginWindow `
        -Element $Element `
        -ExpectedProcessId $ExpectedProcessId `
        -ExpectedAutomationId $ExpectedAutomationId)

    try {
        $valuePattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($null -eq $valuePattern -or $valuePattern.Current.IsReadOnly) {
            return $false
        }
        $valuePattern.SetValue($Text)
    }
    catch {
        return $false
    }

    Start-Sleep -Milliseconds 100
    [void](Assert-LoginInputTarget `
        -LoginWindow $LoginWindow `
        -Element $Element `
        -ExpectedProcessId $ExpectedProcessId `
        -ExpectedAutomationId $ExpectedAutomationId)
    return $true
}

function Close-Window {
    param([System.Windows.Automation.AutomationElement]$Window)
    if ($null -eq $Window) { return }

    try {
        $pattern = $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        if ($null -ne $pattern) {
            $pattern.Close()
            Start-Sleep -Milliseconds 500
            return
        }
    }
    catch {
        # fallback below
    }

    try {
        $Window.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('%{F4}')
        Start-Sleep -Milliseconds 500
    }
    catch {
        # ignore close fallback failure; caller will terminate process if needed.
    }
}

function Test-NameExists {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )

    $null -ne (Find-FirstByName -Root $Root -Name $Name)
}

function Get-DescendantNames {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [int]$Limit = 120
    )

    $results = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Root) {
        return @()
    }

    $elements = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $elements) {
        $name = [string]$element.Current.Name
        if (-not [string]::IsNullOrWhiteSpace($name) -and -not $results.Contains($name)) {
            $results.Add($name) | Out-Null
            if ($results.Count -ge $Limit) {
                break
            }
        }
    }

    $results.ToArray()
}

function Add-Step {
    param(
        [System.Collections.Generic.List[object]]$Steps,
        [string]$Name,
        [bool]$Passed,
        [string]$Detail
    )

    $Steps.Add([pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Detail = $Detail
    }) | Out-Null
}

function Open-And-VerifyChildWindow {
    param(
        [System.Windows.Automation.AutomationElement]$MainWindow,
        [int]$ProcessId,
        [string]$ButtonName,
        [string]$WindowTitle,
        [string[]]$RequiredNames,
        [System.Collections.Generic.List[object]]$Steps
    )

    Normalize-WindowForSmoke -Window $MainWindow

    $button = Find-FirstByNameAndControlType -Root $MainWindow -Name $ButtonName -ControlType ([System.Windows.Automation.ControlType]::Button)
    if ($null -eq $button) {
        $button = Find-FirstByName -Root $MainWindow -Name $ButtonName
    }

    if ($null -eq $button) {
        Add-Step -Steps $Steps -Name "open-$ButtonName" -Passed $false -Detail 'button not found'
        return $false
    }

    $enabledDeadline = (Get-Date).AddSeconds(30)
    while (-not $button.Current.IsEnabled -and (Get-Date) -lt $enabledDeadline) {
        Start-Sleep -Milliseconds 500
    }

    if (-not $button.Current.IsEnabled) {
        $rect = $button.Current.BoundingRectangle
        Add-Step -Steps $Steps -Name "open-$ButtonName" -Passed $false -Detail "button disabled; rect=$([int]$rect.Left),$([int]$rect.Top),$([int]$rect.Width),$([int]$rect.Height)"
        return $false
    }

    $buttonRect = $button.Current.BoundingRectangle
    $patternNames = @()
    try {
        foreach ($pattern in $button.GetSupportedPatterns()) {
            $patternNames += $pattern.ProgrammaticName
        }
    }
    catch {
        $patternNames += 'patterns-unavailable'
    }
    Add-Step -Steps $Steps -Name "button-$ButtonName" -Passed $true -Detail "enabled=True; control=$($button.Current.ControlType.ProgrammaticName); aid=$($button.Current.AutomationId); class=$($button.Current.ClassName); framework=$($button.Current.FrameworkId); rect=$([int]$buttonRect.Left),$([int]$buttonRect.Top),$([int]$buttonRect.Width),$([int]$buttonRect.Height); offscreen=$($button.Current.IsOffscreen); patterns=$($patternNames -join '+')"

    try {
        $handle = [IntPtr]$MainWindow.Current.NativeWindowHandle
        if ($handle -ne [IntPtr]::Zero) {
            [void][GeoraePlanDesktopUiSmokeMouse]::SetForegroundWindow($handle)
            Start-Sleep -Milliseconds 300
        }
        $MainWindow.SetFocus()
    }
    catch {
        # foreground 전환 실패 시에도 UIA/좌표 클릭을 계속 시도합니다.
    }

    $openedViaInvoke = Click-Element $button
    if (-not $openedViaInvoke -and -not (Click-ElementByCoordinates -Element $button)) {
        Add-Step -Steps $Steps -Name "open-$ButtonName" -Passed $false -Detail 'button click failed'
        return $false
    }

    $menuClicked = $true
    if ($ButtonName -eq '거래처 관리') {
        $menuClicked = Invoke-CustomerManagementMenuItem
    }

    $child = Get-ProcessWindow -ProcessId $ProcessId -Name $WindowTitle -Contains -TimeoutSec 8
    if ($null -eq $child) {
        try {
            $handle = [IntPtr]$MainWindow.Current.NativeWindowHandle
            if ($handle -ne [IntPtr]::Zero) {
                [void][GeoraePlanDesktopUiSmokeMouse]::SetForegroundWindow($handle)
                Start-Sleep -Milliseconds 300
            }
            $MainWindow.SetFocus()
        }
        catch {
        }

        $fallbackClicked = Click-ElementByCoordinates -Element $button -DelayMilliseconds 900
        if ($fallbackClicked -and $ButtonName -eq '거래처 관리') {
            $menuClicked = Invoke-CustomerManagementMenuItem
        }

        $child = Get-ProcessWindow -ProcessId $ProcessId -Name $WindowTitle -Contains -TimeoutSec 25
    }

    if ($null -eq $child) {
        $windowNames = Get-ProcessWindowNames -ProcessId $ProcessId
        Add-Step -Steps $Steps -Name "open-$ButtonName" -Passed $false -Detail "window not found: $WindowTitle; windows=$($windowNames -join ', '); invokeClicked=$openedViaInvoke; customerMenuClicked=$menuClicked"
        return $false
    }

    $missing = @()
    foreach ($required in $RequiredNames) {
        if (-not (Test-NameExists -Root $child -Name $required)) {
            $missing += $required
        }
    }

    $passed = $missing.Count -eq 0
    Add-Step -Steps $Steps -Name "window-$WindowTitle" -Passed $passed -Detail ($(if ($passed) { 'required controls found' } else { 'missing: ' + ($missing -join ', ') }))
    Close-Window $child
    return $passed
}

New-DirectoryIfMissing $EvidenceDirectory
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportPath = Join-Path $EvidenceDirectory "desktop-ui-smoke-$timestamp.md"
$jsonPath = Join-Path $EvidenceDirectory "desktop-ui-smoke-$timestamp.json"
if ($UseInAppSelfTest -and [string]::IsNullOrWhiteSpace($InAppSelfTestReportPath)) {
    $InAppSelfTestReportPath = Join-Path $EvidenceDirectory "desktop-ui-inapp-selftest-$timestamp.md"
}

if (-not (Test-Path -LiteralPath $AppExe)) {
    throw "앱 실행 파일을 찾지 못했습니다: $AppExe"
}

$steps = New-Object System.Collections.Generic.List[object]
$process = $null
$serverProcess = $null
$serverProcessStartTimeUtcTicks = 0L
$previousAppEnv = @{}
$passed = $false
$errorText = ''
$processLaunchStartedAt = $null

try {
    $normalizedAppExe = [System.IO.Path]::GetFullPath($AppExe)
    $validatedBootstrapContract = $null
    if ($EnableEphemeralAdminBootstrap) {
        if (-not $StartServer -or
            [string]::IsNullOrWhiteSpace($EphemeralAdminPassword)) {
            throw 'Ephemeral admin bootstrap configuration is incomplete.'
        }
        $validatedBootstrapContract = Assert-EphemeralAdminBootstrapContract `
            -ScriptRoot $PSScriptRoot `
            -ServerDir $ServerDir `
            -ServerDataRoot $ServerDataRoot `
            -ContractPath $BootstrapContractPath `
            -ContractSha256 $BootstrapContractSha256
    }
    if ($StartServer) {
        $port = Get-FreePort -StartingPort $ServerPort
        $serverUrl = "http://127.0.0.1:$port"
        $appSettings = Join-Path (Split-Path -Parent $AppExe) 'appsettings.json'
        $testRoot = Split-Path -Parent (Split-Path -Parent $AppExe)
        $setApiScript = Join-Path $testRoot 'Set-ApiBaseUrl.ps1'
        if ((Test-Path -LiteralPath $appSettings) -and (Test-Path -LiteralPath $setApiScript)) {
            $setApiResult = Invoke-HiddenSetApiBaseUrl `
                -ScriptPath $setApiScript `
                -BaseUrl $serverUrl `
                -AppSettingsPaths @($appSettings)
            $setApiStandardErrorPresent =
                -not [string]::IsNullOrWhiteSpace(
                    $setApiResult.StandardError)
            if (
                $setApiResult.ExitCode -ne 0 -or
                $setApiStandardErrorPresent
            ) {
                throw (
                    '테스트 앱 Api.BaseUrl 설정에 실패했습니다. ' +
                    "exitCode=$($setApiResult.ExitCode) " +
                    "stderrPresent=$setApiStandardErrorPresent")
            }
        }

        $serverProcess = Start-IsolatedTestServer `
            -DotnetExe $DotnetExe `
            -ServerDir $ServerDir `
            -ServerDataRoot $ServerDataRoot `
            -ServerDllPath $(if ($null -eq $validatedBootstrapContract) { '' } else { [string]$validatedBootstrapContract.ServerDllPath }) `
            -ServerUrl $serverUrl `
            -EnableEphemeralAdminBootstrap ([bool]$EnableEphemeralAdminBootstrap) `
            -EphemeralAdminPassword $EphemeralAdminPassword
        $EphemeralAdminPassword = $null
        $serverProcessStartTimeUtcTicks =
            $serverProcess.StartTime.ToUniversalTime().Ticks
        Add-Step -Steps $steps -Name 'start-server' -Passed $true -Detail "pid=$($serverProcess.Id), url=$serverUrl"
        if (-not (Wait-HttpReady -Url ($serverUrl + '/healthz') -TimeoutSeconds 90)) {
            throw "테스트 서버 healthz 대기 실패: $serverUrl"
        }

        Add-Step -Steps $steps -Name 'server-health' -Passed $true -Detail $serverUrl
    }

    $existingProcesses = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                -not $_.HasExited -and
                -not [string]::IsNullOrWhiteSpace($_.Path) -and
                [string]::Equals([System.IO.Path]::GetFullPath($_.Path), $normalizedAppExe, [System.StringComparison]::OrdinalIgnoreCase)
            }
            catch {
                $false
            }
        })

    if ($AttachExisting) {
        $process = $existingProcesses |
            Sort-Object StartTime -Descending -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $process) {
            throw "기존 테스트 앱 프로세스를 찾지 못했습니다: $AppExe"
        }

        Add-Step -Steps $steps -Name 'attach-process' -Passed $true -Detail "pid=$($process.Id)"
    }
    else {
        $existingProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500

        $appEnv = @{
            'GEORAEPLAN_APP_ROOT' = $AppDataRoot
            'GEORAEPLAN_DISABLE_LEGACY_MERGE' = '1'
        }
        $shouldUseTestMode = [bool]$StartServer -or (Test-LooksLikeTestRuntimePath -Path $normalizedAppExe)
        $appEnv['GEORAEPLAN_TEST_MODE'] = if ($shouldUseTestMode) { '1' } else { '' }
        if ($UseInAppSelfTest) {
            $appEnv['GEORAEPLAN_DESKTOP_UI_SMOKE_REPORT'] = $InAppSelfTestReportPath
        }
        Add-Step -Steps $steps -Name 'test-mode-env' -Passed $true -Detail "GEORAEPLAN_TEST_MODE=$($appEnv['GEORAEPLAN_TEST_MODE']); appExe=$normalizedAppExe"
        foreach ($key in $appEnv.Keys) {
            $previousAppEnv[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
            [Environment]::SetEnvironmentVariable($key, [string]$appEnv[$key], 'Process')
        }

        $processLaunchStartedAt = Get-Date
        $process = Start-Process -FilePath $AppExe -WorkingDirectory (Split-Path -Parent $AppExe) -PassThru
        Add-Step -Steps $steps -Name 'start-process' -Passed $true -Detail "pid=$($process.Id)"
    }

    $startupWindow = Wait-LoginOrMainWindow -ProcessId $process.Id -TimeoutSec $TimeoutSec
    if ($startupWindow.Kind -eq 'None' -and -not $AttachExisting -and $null -ne $processLaunchStartedAt) {
        $redirectedProcess = Find-RedirectedAppProcess -OriginalExe $normalizedAppExe -StartedAfter $processLaunchStartedAt
        if ($null -ne $redirectedProcess) {
            Add-Step -Steps $steps -Name 'redirect-process' -Passed $true -Detail "originalPid=$($process.Id); redirectedPid=$($redirectedProcess.Id); path=$($redirectedProcess.Path)"
            $process = $redirectedProcess
            $startupWindow = Wait-LoginOrMainWindow -ProcessId $process.Id -TimeoutSec ([Math]::Max(15, [int]($TimeoutSec / 2)))
        }
    }

    $mainWindow = $null
    if ($startupWindow.Kind -eq 'Login' -or (Test-IsLoginWindow -Window $startupWindow.Window)) {
        $loginWindow = $startupWindow.Window
        Add-Step -Steps $steps -Name 'login-window' -Passed $true -Detail "found; title=$([string]$loginWindow.Current.Name)"

        if (
            [string]::IsNullOrWhiteSpace($Username) -or
            [string]::IsNullOrWhiteSpace($Password)
        ) {
            throw '로그인 자격 증명은 명시적으로 전달되어야 합니다.'
        }

        $usernameBox = Find-FirstByAutomationId -Root $loginWindow -AutomationId 'UsernameBox'
        $passwordBox = Find-FirstByAutomationId -Root $loginWindow -AutomationId 'PasswordBox'
        if ($null -eq $usernameBox -or $null -eq $passwordBox) {
            throw 'AutomationId로 로그인 입력칸을 찾지 못했습니다.'
        }

        if ([string]::IsNullOrWhiteSpace($LoginInputMutexName)) {
            $LoginInputMutexName = 'Local\GeoraePlan.DesktopUiSmoke.LoginInput.Standalone'
        }
        if ($LoginInputMutexName -notmatch '^Local\\GeoraePlan\.[A-Za-z0-9._-]{1,180}$') {
            throw '로그인 입력 mutex 이름이 허용된 Local run scope 형식이 아닙니다.'
        }

        $loginInputMutex =
            [System.Threading.Mutex]::new($false, $LoginInputMutexName)
        $loginInputMutexAcquired = $false
        try {
            try {
                $loginInputMutexAcquired =
                    $loginInputMutex.WaitOne([TimeSpan]::FromSeconds(120))
            }
            catch [System.Threading.AbandonedMutexException] {
                $loginInputMutexAcquired = $true
            }
            if (-not $loginInputMutexAcquired) {
                throw 'run-scoped 로그인 입력 mutex 획득 시간이 초과되었습니다.'
            }

            Normalize-WindowForSmoke -Window $loginWindow
            if (
                -not (Set-ElementText `
                    -LoginWindow $loginWindow `
                    -Element $usernameBox `
                    -Text $Username `
                    -ExpectedProcessId $process.Id `
                    -ExpectedAutomationId 'UsernameBox')
            ) {
                throw '아이디 입력 실패'
            }
            if (
                -not (Set-ElementText `
                    -LoginWindow $loginWindow `
                    -Element $passwordBox `
                    -Text $Password `
                    -ExpectedProcessId $process.Id `
                    -ExpectedAutomationId 'PasswordBox')
            ) {
                throw '비밀번호 입력 실패'
            }

            [void](Assert-LoginInputTarget `
                -LoginWindow $loginWindow `
                -Element $passwordBox `
                -ExpectedProcessId $process.Id `
                -ExpectedAutomationId 'PasswordBox')
            $loginButton = Find-FirstByName -Root $loginWindow -Name '로그인'
            if (
                $null -eq $loginButton -or
                [int]$loginButton.Current.ProcessId -ne [int]$process.Id -or
                -not (Invoke-Element $loginButton)
            ) {
                throw '로그인 버튼 실행 실패'
            }
            Add-Step -Steps $steps -Name 'login-submit' -Passed $true -Detail 'submitted'
            $Username = $null
            $Password = $null
        }
        finally {
            if ($loginInputMutexAcquired) {
                try { $loginInputMutex.ReleaseMutex() } catch { }
            }
            $loginInputMutex.Dispose()
        }

        $mainWindow = Wait-MainWindowOnly -ProcessId $process.Id -TimeoutSec 90
    }
    elseif ($startupWindow.Kind -eq 'Main') {
        $mainWindow = $startupWindow.Window
        Add-Step -Steps $steps -Name 'login-window' -Passed $true -Detail 'main window opened directly'
    }
    else {
        $windowNames = Get-ProcessWindowNames -ProcessId $process.Id
        throw "로그인/메인 창을 찾지 못했습니다. windows=$($windowNames -join ', ')"
    }

    if ($null -eq $mainWindow) {
        $loginWindowAfterFailure = Get-ProcessWindow -ProcessId $process.Id -Name '로그인' -Contains -TimeoutSec 1
        $loginWindowRemained = $null -ne $loginWindowAfterFailure
        throw "로그인 제출 후 메인 창을 찾지 못했습니다. pid=$($process.Id); loginWindowRemained=$loginWindowRemained"
    }

    Normalize-WindowForSmoke -Window $mainWindow

    $requiredMainButtons = @(
        '품목/재고 관리',
        '신규 렌탈 등록',
        '거래처 관리',
        '매입/매출 장부',
        '기간별 집계',
        '렌탈 업무',
        '환경설정',
        '휴지통',
        '판매작성',
        '구매작성',
        '수금 입력',
        '전표 인쇄[F9]'
    )

    $missingMainButtons = @()
    $buttonWaitDeadline = (Get-Date).AddSeconds(45)
    do {
        $missingMainButtons = @()
        foreach ($buttonName in $requiredMainButtons) {
            if (-not (Test-NameExists -Root $mainWindow -Name $buttonName)) {
                $missingMainButtons += $buttonName
            }
        }

        if ($missingMainButtons.Count -eq 0) {
            break
        }

        Start-Sleep -Milliseconds 750
    }
    while ((Get-Date) -lt $buttonWaitDeadline)

    Add-Step -Steps $steps -Name 'main-buttons' -Passed ($missingMainButtons.Count -eq 0) -Detail ($(if ($missingMainButtons.Count -eq 0) { 'all found' } else { 'missing: ' + ($missingMainButtons -join ', ') }))

    if ($UseInAppSelfTest) {
        if (-not (Wait-FileReady -Path $InAppSelfTestReportPath -TimeoutSeconds $InAppSelfTestTimeoutSec)) {
            Add-Step -Steps $steps -Name 'in-app-self-test' -Passed $false -Detail "report not created: $InAppSelfTestReportPath"
        }
        else {
            $inAppJsonPath = [System.IO.Path]::ChangeExtension($InAppSelfTestReportPath, '.json')
            $inAppPayload = $null
            if (Test-Path -LiteralPath $inAppJsonPath) {
                $inAppPayload = Get-Content -LiteralPath $inAppJsonPath -Raw | ConvertFrom-Json
            }

            $inAppPassed = $null -ne $inAppPayload -and [string]$inAppPayload.Result -eq 'PASS'
            Add-Step -Steps $steps -Name 'in-app-self-test' -Passed $inAppPassed -Detail "result=$($inAppPayload.Result); report=$InAppSelfTestReportPath"
            if ($null -ne $inAppPayload -and $null -ne $inAppPayload.Steps) {
                foreach ($inAppStep in @($inAppPayload.Steps)) {
                    Add-Step -Steps $steps -Name ("in-app-" + [string]$inAppStep.Name) -Passed ([bool]$inAppStep.Passed) -Detail ([string]$inAppStep.Detail)
                }
            }
        }
    }
    else {
        $childResults = @()
        $childResults += Open-And-VerifyChildWindow -MainWindow $mainWindow -ProcessId $process.Id -ButtonName '거래처 관리' -WindowTitle '거래처 관리' -RequiredNames @('새 거래처 등록', '선택 거래처 수정', '선택 거래처 삭제') -Steps $steps
        $childResults += Open-And-VerifyChildWindow -MainWindow $mainWindow -ProcessId $process.Id -ButtonName '품목/재고 관리' -WindowTitle '품목/재고 관리' -RequiredNames @('신규 품목', '품목 저장', '선택 재고 초기화', '닫기 (F12)') -Steps $steps
        $childResults += Open-And-VerifyChildWindow -MainWindow $mainWindow -ProcessId $process.Id -ButtonName '판매작성' -WindowTitle '판매(매출)' -RequiredNames @('수금 입력', '항목추가') -Steps $steps
        $childResults += Open-And-VerifyChildWindow -MainWindow $mainWindow -ProcessId $process.Id -ButtonName '구매작성' -WindowTitle '구매(매입)' -RequiredNames @('지급 입력', '항목추가') -Steps $steps
    }

    $passed = ($steps | Where-Object { -not $_.Passed }).Count -eq 0
}
catch {
    $errorText = $_.Exception.Message
    Add-Step -Steps $steps -Name 'exception' -Passed $false -Detail $errorText
}
finally {
    $EphemeralAdminPassword = $null
    $Username = $null
    $Password = $null

    if (-not $KeepAppOpen -and -not $AttachExisting -and $null -ne $process -and -not $process.HasExited) {
        try {
            $process.CloseMainWindow() | Out-Null
            if (-not $process.WaitForExit(30000)) {
                $process.Kill()
                $process.WaitForExit(5000) | Out-Null
            }
        }
        catch {
            # ignore cleanup failure
        }
    }

    if ($null -ne $serverProcess) {
        try {
            Stop-ExactProcessAndWait `
                -ProcessId $serverProcess.Id `
                -ExpectedStartTimeUtcTicks $serverProcessStartTimeUtcTicks `
                -TimeoutMilliseconds 15000
            if (-not (Wait-LoopbackPortReleased -Port $port -TimeoutSeconds 30)) {
                throw "테스트 서버 loopback 포트가 해제되지 않았습니다: $port"
            }
            Add-Step `
                -Steps $steps `
                -Name 'server-process-cleanup' `
                -Passed $true `
                -Detail "exact PID/start-time process exited; loopback port released"
        }
        catch {
            $passed = $false
            if ([string]::IsNullOrWhiteSpace($errorText)) {
                $errorText = $_.Exception.Message
            }
            Add-Step `
                -Steps $steps `
                -Name 'server-process-cleanup' `
                -Passed $false `
                -Detail $_.Exception.Message
        }
    }

    foreach ($key in $previousAppEnv.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousAppEnv[$key], 'Process')
    }
}

$payload = [pscustomobject]@{
    CreatedAt = (Get-Date).ToString('O')
    AppExe = $AppExe
    Result = if ($passed) { 'PASS' } else { 'FAIL' }
    Error = $errorText
    Steps = $steps
}
$payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# 거래플랜 Desktop UI Smoke') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- 작성시각: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))") | Out-Null
$lines.Add("- AppExe: $AppExe") | Out-Null
$lines.Add("- 결과: **$($payload.Result)**") | Out-Null
if (-not [string]::IsNullOrWhiteSpace($errorText)) {
    $lines.Add("- 오류: $errorText") | Out-Null
}
$lines.Add('') | Out-Null
$lines.Add('| 단계 | 결과 | 상세 |') | Out-Null
$lines.Add('|---|---|---|') | Out-Null
foreach ($step in $steps) {
    $lines.Add("| $($step.Name) | $(if ($step.Passed) { 'PASS' } else { 'FAIL' }) | $(([string]$step.Detail).Replace('|', '\|')) |") | Out-Null
}
$lines.Add('') | Out-Null
$lines.Add("JSON: $jsonPath") | Out-Null
$lines | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host "desktop_ui_smoke_report=$reportPath"
Write-Host "desktop_ui_smoke_json=$jsonPath"
Write-Host "result=$($payload.Result)"

if (-not $passed) {
    throw "Desktop UI smoke failed. Report: $reportPath"
}
