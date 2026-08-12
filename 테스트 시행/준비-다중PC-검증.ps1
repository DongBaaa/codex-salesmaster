[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ExecutionRoot,
    [string]$MultiPcRoot,
    [switch]$ResetClientData,
    [switch]$LaunchServer,
    [switch]$LaunchClients
)

$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptRoot)
    return (Resolve-Path (Join-Path $ScriptRoot '..')).Path
}

function New-Utf8NoBomEncoding {
    return New-Object System.Text.UTF8Encoding($false)
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, $Content, (New-Utf8NoBomEncoding))
}

function Get-NormalizedFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Assert-PhysicalTree {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label 경로가 없습니다: $Path"
    }

    $rootItem = Get-Item -LiteralPath $Path -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label 경로는 reparse point를 사용할 수 없습니다: $($rootItem.FullName)"
    }

    if (-not $rootItem.PSIsContainer) {
        return
    }

    $pending = New-Object System.Collections.Generic.Queue[string]
    $pending.Enqueue($rootItem.FullName)
    while ($pending.Count -gt 0) {
        $currentDirectory = $pending.Dequeue()
        foreach ($child in Get-ChildItem -LiteralPath $currentDirectory -Force) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Label 트리에 reparse point가 포함되어 있습니다: $($child.FullName)"
            }

            if ($child.PSIsContainer) {
                $pending.Enqueue($child.FullName)
            }
        }
    }
}

function Get-AppTreeSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-PhysicalTree -Path $Root -Label $Label
    $normalizedRoot = Get-NormalizedFullPath -Path $Root
    $rootPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    $entries = New-Object System.Collections.Generic.List[string]
    $pending = New-Object System.Collections.Generic.Queue[string]
    $pending.Enqueue($normalizedRoot)

    while ($pending.Count -gt 0) {
        $currentDirectory = $pending.Dequeue()
        foreach ($child in Get-ChildItem -LiteralPath $currentDirectory -Force) {
            if ($child.PSIsContainer) {
                $pending.Enqueue($child.FullName)
                continue
            }

            $relativePath = $child.FullName.Substring($rootPrefix.Length).Replace(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar)
            $fileHash = (Get-FileHash -LiteralPath $child.FullName -Algorithm SHA256).Hash
            $entries.Add("$relativePath`t$fileHash")
        }
    }

    $entries.Sort([System.StringComparer]::Ordinal)
    $payload = [System.Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $entries))
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha256.ComputeHash($payload))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Assert-DistinctRoots {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Roots
    )

    $seen = @{}
    $normalizedEntries = New-Object System.Collections.Generic.List[object]
    foreach ($entry in $Roots.GetEnumerator()) {
        $normalizedPath = Get-NormalizedFullPath -Path $entry.Value
        $key = $normalizedPath.ToUpperInvariant()
        if ($seen.ContainsKey($key)) {
            throw "격리 경로가 중복됩니다: $($seen[$key]) / $($entry.Key) = $normalizedPath"
        }

        $seen[$key] = $entry.Key
        $normalizedEntries.Add([pscustomobject]@{
            Name = $entry.Key
            Path = $normalizedPath
        })
    }

    for ($leftIndex = 0; $leftIndex -lt $normalizedEntries.Count; $leftIndex++) {
        for ($rightIndex = $leftIndex + 1; $rightIndex -lt $normalizedEntries.Count; $rightIndex++) {
            $left = $normalizedEntries[$leftIndex]
            $right = $normalizedEntries[$rightIndex]
            $leftPrefix = $left.Path + [System.IO.Path]::DirectorySeparatorChar
            $rightPrefix = $right.Path + [System.IO.Path]::DirectorySeparatorChar
            if ($left.Path.StartsWith($rightPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
                $right.Path.StartsWith($leftPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "격리 경로가 서로 포함 관계입니다: $($left.Name)=$($left.Path) / $($right.Name)=$($right.Path)"
            }
        }
    }
}

function Assert-LoopbackApiBaseUrl {
    param([Parameter(Mandatory = $true)][string]$AppRoot)

    $appSettingsPath = Join-Path $AppRoot 'appsettings.json'
    if (-not (Test-Path -LiteralPath $appSettingsPath -PathType Leaf)) {
        throw "원본 App appsettings.json이 없습니다: $appSettingsPath"
    }

    Assert-PhysicalTree -Path $appSettingsPath -Label '원본 App appsettings.json'
    try {
        $appSettings = Get-Content -LiteralPath $appSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "원본 App appsettings.json 파싱에 실패했습니다: $($_.Exception.Message)"
    }

    $baseUrl = [string]$appSettings.Api.BaseUrl
    $uri = $null
    $isAbsoluteHttpUrl = (
        -not [string]::IsNullOrWhiteSpace($baseUrl) -and
        [System.Uri]::TryCreate($baseUrl, [System.UriKind]::Absolute, [ref]$uri) -and
        ($uri.Scheme -eq [System.Uri]::UriSchemeHttp -or $uri.Scheme -eq [System.Uri]::UriSchemeHttps))
    if (-not $isAbsoluteHttpUrl -or -not $uri.IsLoopback) {
        throw '다중 PC 검증 App의 Api.BaseUrl은 loopback HTTP(S) 주소여야 합니다. live/외부 URL은 준비하지 않습니다.'
    }
}

function Remove-ClonedSyncDeviceId {
    param(
        [Parameter(Mandatory = $true)][string]$AppRoot,
        [Parameter(Mandatory = $true)][string]$AppDataRoot,
        [Parameter(Mandatory = $true)][string]$ClientCode
    )

    $databasePath = Join-Path $AppDataRoot 'data\거래플랜.db'
    $sqliteLibraryPath = Join-Path $AppRoot 'runtimes\win-x64\native\e_sqlite3.dll'
    foreach ($requiredPath in @($databasePath, $sqliteLibraryPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "$ClientCode Sync.DeviceId 초기화에 필요한 파일이 없습니다: $requiredPath"
        }

        Assert-PhysicalTree -Path $requiredPath -Label "$ClientCode Sync.DeviceId 초기화 입력"
    }

    if (-not ('GeoraePlanMultiPcSqliteSettingSanitizer' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class GeoraePlanMultiPcSqliteSettingSanitizer
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string libraryPath);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sqlite3_open16(string filename, out IntPtr database);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr database);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int sqlite3_exec(
        IntPtr database,
        string sql,
        IntPtr callback,
        IntPtr callbackArgument,
        out IntPtr errorMessage);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int sqlite3_prepare_v2(
        IntPtr database,
        string sql,
        int sqlByteCount,
        out IntPtr statement,
        IntPtr tail);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_int(IntPtr statement, int columnIndex);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr database);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr pointer);

    public static void RemoveSyncDeviceId(string databasePath, string nativeLibraryPath)
    {
        if (LoadLibrary(nativeLibraryPath) == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Failed to load the isolated App SQLite library.");
        }

        IntPtr database;
        var openResult = sqlite3_open16(databasePath, out database);
        if (openResult != SqliteOk)
        {
            var detail = database == IntPtr.Zero ? "unknown SQLite open error" : GetError(database);
            if (database != IntPtr.Zero)
            {
                sqlite3_close_v2(database);
            }

            throw new InvalidOperationException("Failed to open the cloned local SQLite database: " + detail);
        }

        try
        {
            EnsureOk(database, sqlite3_busy_timeout(database, 5000), "set SQLite busy timeout");
            if (GetScalarInt(database, "SELECT COUNT(*) FROM pragma_table_info('Settings') WHERE name = 'Key' AND pk = 1;") != 1 ||
                GetScalarInt(database, "SELECT COUNT(*) FROM pragma_table_info('Settings') WHERE name = 'Value';") != 1)
            {
                throw new InvalidOperationException(
                    "The cloned SQLite Settings schema is not the expected Key primary key / Value layout.");
            }

            Execute(database, "BEGIN IMMEDIATE;");
            try
            {
                var matchingRows = GetScalarInt(
                    database,
                    "SELECT COUNT(*) FROM \"Settings\" WHERE \"Key\" = 'Sync.DeviceId';");
                if (matchingRows > 1)
                {
                    throw new InvalidOperationException(
                        "More than one Sync.DeviceId row exists in the cloned SQLite Settings table.");
                }

                Execute(database, "DELETE FROM \"Settings\" WHERE \"Key\" = 'Sync.DeviceId';");
                if (GetScalarInt(
                        database,
                        "SELECT COUNT(*) FROM \"Settings\" WHERE \"Key\" = 'Sync.DeviceId';") != 0)
                {
                    throw new InvalidOperationException(
                        "Sync.DeviceId still exists after the isolated SQLite delete.");
                }

                Execute(database, "COMMIT;");
            }
            catch
            {
                try
                {
                    Execute(database, "ROLLBACK;");
                }
                catch
                {
                }

                throw;
            }
        }
        finally
        {
            sqlite3_close_v2(database);
        }
    }

    private static int GetScalarInt(IntPtr database, string sql)
    {
        IntPtr statement;
        EnsureOk(
            database,
            sqlite3_prepare_v2(database, sql, -1, out statement, IntPtr.Zero),
            "prepare SQLite scalar");
        try
        {
            var stepResult = sqlite3_step(statement);
            if (stepResult != SqliteRow)
            {
                throw new InvalidOperationException(
                    "SQLite scalar query did not return a row: " + GetError(database));
            }

            return sqlite3_column_int(statement, 0);
        }
        finally
        {
            sqlite3_finalize(statement);
        }
    }

    private static void Execute(IntPtr database, string sql)
    {
        IntPtr errorMessage;
        var result = sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, out errorMessage);
        if (result == SqliteOk)
        {
            return;
        }

        var detail = errorMessage == IntPtr.Zero
            ? GetError(database)
            : Marshal.PtrToStringAnsi(errorMessage);
        if (errorMessage != IntPtr.Zero)
        {
            sqlite3_free(errorMessage);
        }

        throw new InvalidOperationException("SQLite statement failed: " + detail);
    }

    private static void EnsureOk(IntPtr database, int result, string operation)
    {
        if (result != SqliteOk)
        {
            throw new InvalidOperationException(operation + " failed: " + GetError(database));
        }
    }

    private static string GetError(IntPtr database)
    {
        var message = sqlite3_errmsg(database);
        return message == IntPtr.Zero ? "unknown SQLite error" : Marshal.PtrToStringAnsi(message);
    }
}
'@
    }

    [GeoraePlanMultiPcSqliteSettingSanitizer]::RemoveSyncDeviceId(
        (Get-NormalizedFullPath -Path $databasePath),
        (Get-NormalizedFullPath -Path $sqliteLibraryPath))
    Write-Host "- $ClientCode cloned AppData: Sync.DeviceId 설정만 제거했습니다." -ForegroundColor Green
}

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string[]]$ExcludeDirectories = @()
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $arguments = @(
        $Source,
        $Destination,
        '/MIR',
        '/R:2',
        '/W:2',
        '/NFL',
        '/NDL',
        '/NJH',
        '/NJS',
        '/NP'
    )

    if ($ExcludeDirectories.Count -gt 0) {
        $arguments += '/XD'
        $arguments += $ExcludeDirectories
    }

    & robocopy @arguments | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed: $Source -> $Destination"
    }
}

function Reset-TransientAppDataDirectories {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    foreach ($child in @('backup', 'diagnostics', 'logs', 'temp')) {
        $path = Join-Path $Root $child
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
}

function New-ClientRunScript {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AppFolderName,
        [Parameter(Mandatory = $true)][string]$AppDataFolderName,
        [Parameter(Mandatory = $true)][string]$TempFolderName,
        [Parameter(Mandatory = $true)][string]$DownloadsFolderName,
        [Parameter(Mandatory = $true)][string]$WindowTitle
    )

    $content = @"
@echo off
setlocal EnableExtensions
set "APP_DIR=%~dp0$AppFolderName"
set "APP_ROOT=%~dp0$AppDataFolderName"
set "TEMP_ROOT=%~dp0$TempFolderName"
set "DOWNLOADS_ROOT=%~dp0$DownloadsFolderName"
set "APP_EXE="
for %%I in ("%APP_DIR%\*.Desktop.App.exe") do if not defined APP_EXE set "APP_EXE=%%~fI"
for %%I in ("%APP_DIR%\*.App.exe") do if not defined APP_EXE set "APP_EXE=%%~fI"
if not defined APP_EXE (
  echo [GeoraePlan] App exe not found in %APP_DIR% 1>&2
  exit /b 1
)
set "GEORAEPLAN_APP_ROOT=%APP_ROOT%"
set "GEORAEPLAN_TEMP_ROOT=%TEMP_ROOT%"
set "GEORAEPLAN_DOWNLOADS_ROOT=%DOWNLOADS_ROOT%"
set "GEORAEPLAN_DISABLE_LEGACY_MERGE=1"
set "GEORAEPLAN_TEST_MODE=1"
start "$WindowTitle" /D "%APP_DIR%" "%APP_EXE%"
set "RUN_EXIT=%ERRORLEVEL%"
endlocal & exit /b %RUN_EXIT%
"@

    Write-Utf8File -Path $Path -Content $content
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-ProjectRoot -ScriptRoot $scriptRoot
}
if ([string]::IsNullOrWhiteSpace($ExecutionRoot)) {
    $ExecutionRoot = Join-Path $scriptRoot '실행환경'
}
if ([string]::IsNullOrWhiteSpace($MultiPcRoot)) {
    $MultiPcRoot = Join-Path $ExecutionRoot 'MultiPC'
}

$executionRoot = (Resolve-Path -LiteralPath $ExecutionRoot).Path
$appRoot = Join-Path $executionRoot 'App'
$baseAppDataRoot = Join-Path $executionRoot 'AppData'
$runServerCmd = Join-Path $executionRoot 'Run-Server.cmd'

foreach ($requiredPath in @($appRoot, $baseAppDataRoot, $runServerCmd)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "다중 PC 검증 준비 전에 테스트 실행환경이 먼저 필요합니다: $requiredPath"
    }
}

Assert-LoopbackApiBaseUrl -AppRoot $appRoot
$sourceAppHash = Get-AppTreeSha256 -Root $appRoot -Label '원본 App'
Assert-PhysicalTree -Path $baseAppDataRoot -Label '기본 AppData'
Assert-PhysicalTree -Path $runServerCmd -Label 'Run-Server.cmd'

$MultiPcRoot = Get-NormalizedFullPath -Path $MultiPcRoot
if (Test-Path -LiteralPath $MultiPcRoot) {
    Assert-PhysicalTree -Path $MultiPcRoot -Label 'MultiPC'
}
New-Item -ItemType Directory -Force -Path $MultiPcRoot | Out-Null
Assert-PhysicalTree -Path $MultiPcRoot -Label 'MultiPC'

$clients = @(
    [pscustomobject]@{
        Code = 'PC-A'
        AppRoot = Join-Path $MultiPcRoot 'App-PC-A'
        DataRoot = Join-Path $MultiPcRoot 'AppData-PC-A'
        TempRoot = Join-Path $MultiPcRoot 'Temp-PC-A'
        DownloadsRoot = Join-Path $MultiPcRoot 'Downloads-PC-A'
        ScriptPath = Join-Path $MultiPcRoot 'Run-App-PC-A.cmd'
        WindowTitle = 'GeoraePlan Test App PC-A'
    },
    [pscustomobject]@{
        Code = 'PC-B'
        AppRoot = Join-Path $MultiPcRoot 'App-PC-B'
        DataRoot = Join-Path $MultiPcRoot 'AppData-PC-B'
        TempRoot = Join-Path $MultiPcRoot 'Temp-PC-B'
        DownloadsRoot = Join-Path $MultiPcRoot 'Downloads-PC-B'
        ScriptPath = Join-Path $MultiPcRoot 'Run-App-PC-B.cmd'
        WindowTitle = 'GeoraePlan Test App PC-B'
    }
)

Assert-DistinctRoots -Roots @{
    '원본 App' = $appRoot
    '기본 AppData' = $baseAppDataRoot
    'PC-A App' = $clients[0].AppRoot
    'PC-A AppData' = $clients[0].DataRoot
    'PC-A Temp' = $clients[0].TempRoot
    'PC-A Downloads' = $clients[0].DownloadsRoot
    'PC-B App' = $clients[1].AppRoot
    'PC-B AppData' = $clients[1].DataRoot
    'PC-B Temp' = $clients[1].TempRoot
    'PC-B Downloads' = $clients[1].DownloadsRoot
}

foreach ($client in $clients) {
    if (Test-Path -LiteralPath $client.AppRoot) {
        Assert-PhysicalTree -Path $client.AppRoot -Label "$($client.Code) App"
    }
    Invoke-RobocopyMirror -Source $appRoot -Destination $client.AppRoot
    $clientAppHash = Get-AppTreeSha256 -Root $client.AppRoot -Label "$($client.Code) App"
    if (-not [string]::Equals($sourceAppHash, $clientAppHash, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$($client.Code) App 복사본의 SHA-256 트리 해시가 원본과 다릅니다. 원본=$sourceAppHash, 복사본=$clientAppHash"
    }

    if (Test-Path -LiteralPath $client.DataRoot) {
        Assert-PhysicalTree -Path $client.DataRoot -Label "$($client.Code) AppData"
    }
    if ($ResetClientData -or -not (Test-Path -LiteralPath $client.DataRoot)) {
        Invoke-RobocopyMirror -Source $baseAppDataRoot -Destination $client.DataRoot -ExcludeDirectories @('backup', 'diagnostics', 'logs', 'temp')
        Reset-TransientAppDataDirectories -Root $client.DataRoot
        Remove-ClonedSyncDeviceId -AppRoot $client.AppRoot -AppDataRoot $client.DataRoot -ClientCode $client.Code
    }
    Assert-PhysicalTree -Path $client.DataRoot -Label "$($client.Code) AppData"

    foreach ($isolatedRoot in @($client.TempRoot, $client.DownloadsRoot)) {
        if (Test-Path -LiteralPath $isolatedRoot) {
            Assert-PhysicalTree -Path $isolatedRoot -Label "$($client.Code) 격리 쓰기 경로"
        }
        New-Item -ItemType Directory -Force -Path $isolatedRoot | Out-Null
        Assert-PhysicalTree -Path $isolatedRoot -Label "$($client.Code) 격리 쓰기 경로"
    }

    New-ClientRunScript `
        -Path $client.ScriptPath `
        -AppFolderName ([System.IO.Path]::GetFileName($client.AppRoot)) `
        -AppDataFolderName ([System.IO.Path]::GetFileName($client.DataRoot)) `
        -TempFolderName ([System.IO.Path]::GetFileName($client.TempRoot)) `
        -DownloadsFolderName ([System.IO.Path]::GetFileName($client.DownloadsRoot)) `
        -WindowTitle $client.WindowTitle
}

$runServerWrapper = @"
@echo off
setlocal EnableExtensions
set "EXEC_ROOT=%~dp0.."
start "" /B /D "%EXEC_ROOT%" "%ComSpec%" /D /C call "%EXEC_ROOT%\Run-Server.cmd"
set "RUN_EXIT=%ERRORLEVEL%"
endlocal & exit /b %RUN_EXIT%
"@
Write-Utf8File -Path (Join-Path $MultiPcRoot 'Run-Server.cmd') -Content $runServerWrapper

$runAllContent = @"
@echo off
setlocal EnableExtensions
call "%~dp0Run-Server.cmd"
set "RUN_EXIT=%ERRORLEVEL%"
if not "%RUN_EXIT%"=="0" goto :launcher_failed
timeout /t 4 /nobreak >nul
call "%~dp0Run-App-PC-A.cmd"
set "RUN_EXIT=%ERRORLEVEL%"
if not "%RUN_EXIT%"=="0" goto :launcher_failed
timeout /t 2 /nobreak >nul
call "%~dp0Run-App-PC-B.cmd"
set "RUN_EXIT=%ERRORLEVEL%"
if not "%RUN_EXIT%"=="0" goto :launcher_failed
endlocal & exit /b 0

:launcher_failed
endlocal & exit /b %RUN_EXIT%
"@
Write-Utf8File -Path (Join-Path $MultiPcRoot 'Run-All-MultiPC.cmd') -Content $runAllContent

$resetPs1Content = @'
param()
& "$PSScriptRoot\..\..\Prepare-MultiPC.ps1" -ExecutionRoot "$PSScriptRoot\.." -MultiPcRoot "$PSScriptRoot" -ResetClientData
'@
Write-Utf8File -Path (Join-Path $MultiPcRoot 'Reset-ClientData.ps1') -Content $resetPs1Content
Remove-Item -LiteralPath (Join-Path $MultiPcRoot 'Reset-ClientData.cmd') -Force -ErrorAction SilentlyContinue

$readmeContent = @(
    '# 다중 PC 검증 실행 파일',
    '',
    '- Run-Server.cmd : 테스트 서버만 실행',
    '- Run-App-PC-A.cmd : PC-A 전용 AppData로 데스크톱 실행',
    '- Run-App-PC-B.cmd : PC-B 전용 AppData로 데스크톱 실행',
    '- Run-All-MultiPC.cmd : 서버 + PC-A + PC-B 순서로 실행',
    '- Reset-ClientData.ps1 : 기본 AppData 스냅샷으로 PC-A/PC-B 데이터를 다시 복사',
    '',
    "- 원본 앱 폴더(실행하지 않음): $appRoot",
    "- 원본 App 트리 SHA-256: $sourceAppHash",
    "- PC-A App 복사본: $($clients[0].AppRoot)",
    "- PC-B App 복사본: $($clients[1].AppRoot)",
    "- 기본 스냅샷: $baseAppDataRoot",
    "- PC-A AppData: $($clients[0].DataRoot)",
    "- PC-B AppData: $($clients[1].DataRoot)",
    "- PC-A Temp: $($clients[0].TempRoot)",
    "- PC-B Temp: $($clients[1].TempRoot)",
    "- PC-A Downloads: $($clients[0].DownloadsRoot)",
    "- PC-B Downloads: $($clients[1].DownloadsRoot)"
) -join [Environment]::NewLine
Write-Utf8File -Path (Join-Path $MultiPcRoot 'README.txt') -Content $readmeContent

if ($LaunchServer) {
    Start-Process `
        -FilePath (Join-Path $MultiPcRoot 'Run-Server.cmd') `
        -WindowStyle Hidden
}
if ($LaunchClients) {
    if (-not $LaunchServer) {
        Start-Sleep -Seconds 1
    }

    Start-Process `
        -FilePath $clients[0].ScriptPath `
        -WindowStyle Hidden
    Start-Sleep -Seconds 2
    Start-Process `
        -FilePath $clients[1].ScriptPath `
        -WindowStyle Hidden
}

Write-Host '다중 PC 검증 실행환경을 준비했습니다.' -ForegroundColor Green
Write-Host "- 실행 루트: $MultiPcRoot" -ForegroundColor Green
Write-Host "- 원본 App 트리 SHA-256: $sourceAppHash" -ForegroundColor Green
Write-Host "- PC-A App: $($clients[0].AppRoot)" -ForegroundColor Green
Write-Host "- PC-B App: $($clients[1].AppRoot)" -ForegroundColor Green
Write-Host "- PC-A AppData: $($clients[0].DataRoot)" -ForegroundColor Green
Write-Host "- PC-B AppData: $($clients[1].DataRoot)" -ForegroundColor Green
Write-Host '- Run-All-MultiPC.cmd 또는 각 Run-App-PC-*.cmd를 사용하세요.' -ForegroundColor Green
