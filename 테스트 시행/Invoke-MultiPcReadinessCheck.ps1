[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$ExecutionRoot = "",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

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
        return ([System.BitConverter]::ToString($sha256.ComputeHash($payload))).Replace("-", "")
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-DesktopAppExecutable {
    param([Parameter(Mandatory = $true)][string]$Root)

    $candidates = @(
        Get-ChildItem -LiteralPath $Root -File -Filter "*.Desktop.App.exe" -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $Root -File -Filter "*.App.exe" -ErrorAction SilentlyContinue
    ) | Sort-Object FullName -Unique
    return $candidates | Select-Object -First 1
}

function Add-CheckResult {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Results,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [Parameter(Mandatory = $true)][string]$Detail
    )

    $Results.Add([pscustomobject]@{
        Name = $Name
        Path = $Path
        Exists = $Passed
        Detail = $Detail
    }) | Out-Null
}

function Test-LoopbackBaseUrl {
    param(
        [Parameter(Mandatory = $true)][string]$AppSettingsPath,
        [Parameter(Mandatory = $true)][string]$ClientCode,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Results
    )

    if (-not (Test-Path -LiteralPath $AppSettingsPath -PathType Leaf)) {
        Add-CheckResult -Results $Results -Name "$ClientCode 테스트 앱 BaseUrl" -Path $AppSettingsPath -Passed $false -Detail "appsettings.json 누락"
        return
    }

    try {
        $appSettings = Get-Content -LiteralPath $AppSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $baseUrl = [string]$appSettings.Api.BaseUrl
        $uri = $null
        $isAbsoluteHttpUrl = (
            -not [string]::IsNullOrWhiteSpace($baseUrl) -and
            [System.Uri]::TryCreate($baseUrl, [System.UriKind]::Absolute, [ref]$uri) -and
            ($uri.Scheme -eq [System.Uri]::UriSchemeHttp -or $uri.Scheme -eq [System.Uri]::UriSchemeHttps))
        $isSafe = $isAbsoluteHttpUrl -and $uri.IsLoopback
        $detail = if ([string]::IsNullOrWhiteSpace($baseUrl)) {
            "Api.BaseUrl 없음"
        }
        elseif (-not $isAbsoluteHttpUrl) {
            "유효한 절대 HTTP(S) URL이 아님: $baseUrl"
        }
        elseif (-not $uri.IsLoopback) {
            "비-loopback/live URL 거부: $baseUrl"
        }
        else {
            $baseUrl
        }
        Add-CheckResult -Results $Results -Name "$ClientCode 테스트 앱 BaseUrl" -Path $AppSettingsPath -Passed $isSafe -Detail $detail
    }
    catch {
        Add-CheckResult -Results $Results -Name "$ClientCode 테스트 앱 BaseUrl" -Path $AppSettingsPath -Passed $false -Detail "appsettings.json 파싱 실패: $($_.Exception.Message)"
    }
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $scriptRoot = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $PSScriptRoot
    }
    else {
        Split-Path -Parent $MyInvocation.MyCommand.Path
    }

    $ProjectRoot = Split-Path -Parent $scriptRoot
}

if ([string]::IsNullOrWhiteSpace($ExecutionRoot)) {
    $ExecutionRoot = Join-Path $ProjectRoot "테스트 시행\실행환경"
}

$multiPcRoot = Join-Path $ExecutionRoot "MultiPC"
$appRoot = Join-Path $ExecutionRoot "App"
$clientRoots = @(
    [pscustomobject]@{
        Code = "PC-A"
        AppRoot = Join-Path $multiPcRoot "App-PC-A"
        DataRoot = Join-Path $multiPcRoot "AppData-PC-A"
        TempRoot = Join-Path $multiPcRoot "Temp-PC-A"
        DownloadsRoot = Join-Path $multiPcRoot "Downloads-PC-A"
        Launcher = Join-Path $multiPcRoot "Run-App-PC-A.cmd"
    },
    [pscustomobject]@{
        Code = "PC-B"
        AppRoot = Join-Path $multiPcRoot "App-PC-B"
        DataRoot = Join-Path $multiPcRoot "AppData-PC-B"
        TempRoot = Join-Path $multiPcRoot "Temp-PC-B"
        DownloadsRoot = Join-Path $multiPcRoot "Downloads-PC-B"
        Launcher = Join-Path $multiPcRoot "Run-App-PC-B.cmd"
    }
)

$checks = @(
    @{ Name = "MultiPC 폴더"; Path = $multiPcRoot; Type = "Container" },
    @{ Name = "Run-All-MultiPC.cmd"; Path = (Join-Path $multiPcRoot "Run-All-MultiPC.cmd"); Type = "Leaf" },
    @{ Name = "Run-App-PC-A.cmd"; Path = (Join-Path $multiPcRoot "Run-App-PC-A.cmd"); Type = "Leaf" },
    @{ Name = "Run-App-PC-B.cmd"; Path = (Join-Path $multiPcRoot "Run-App-PC-B.cmd"); Type = "Leaf" },
    @{ Name = "Run-Server.cmd"; Path = (Join-Path $multiPcRoot "Run-Server.cmd"); Type = "Leaf" },
    @{ Name = "Reset-ClientData.ps1"; Path = (Join-Path $multiPcRoot "Reset-ClientData.ps1"); Type = "Leaf" },
    @{ Name = "원본 App publish"; Path = $appRoot; Type = "Container" },
    @{ Name = "PC-A App 복사본"; Path = $clientRoots[0].AppRoot; Type = "Container" },
    @{ Name = "PC-B App 복사본"; Path = $clientRoots[1].AppRoot; Type = "Container" },
    @{ Name = "PC-A AppData"; Path = $clientRoots[0].DataRoot; Type = "Container" },
    @{ Name = "PC-B AppData"; Path = $clientRoots[1].DataRoot; Type = "Container" },
    @{ Name = "PC-A Temp"; Path = $clientRoots[0].TempRoot; Type = "Container" },
    @{ Name = "PC-B Temp"; Path = $clientRoots[1].TempRoot; Type = "Container" },
    @{ Name = "PC-A Downloads"; Path = $clientRoots[0].DownloadsRoot; Type = "Container" },
    @{ Name = "PC-B Downloads"; Path = $clientRoots[1].DownloadsRoot; Type = "Container" }
)

$results = New-Object System.Collections.Generic.List[object]

foreach ($check in $checks) {
    $exists = Test-Path -LiteralPath $check.Path -PathType $check.Type
    Add-CheckResult -Results $results -Name $check.Name -Path $check.Path -Passed $exists -Detail $(if ($exists) { "OK" } else { "누락" })
}

foreach ($physicalRoot in @(
    [pscustomobject]@{ Name = "MultiPC 물리 경로"; Path = $multiPcRoot },
    [pscustomobject]@{ Name = "원본 App 물리 트리"; Path = $appRoot }
) + @($clientRoots | ForEach-Object {
    @(
        [pscustomobject]@{ Name = "$($_.Code) App 물리 트리"; Path = $_.AppRoot },
        [pscustomobject]@{ Name = "$($_.Code) AppData 물리 트리"; Path = $_.DataRoot },
        [pscustomobject]@{ Name = "$($_.Code) Temp 물리 트리"; Path = $_.TempRoot },
        [pscustomobject]@{ Name = "$($_.Code) Downloads 물리 트리"; Path = $_.DownloadsRoot }
    )
})) {
    try {
        Assert-PhysicalTree -Path $physicalRoot.Path -Label $physicalRoot.Name
        Add-CheckResult -Results $results -Name $physicalRoot.Name -Path $physicalRoot.Path -Passed $true -Detail "reparse point 없음"
    }
    catch {
        Add-CheckResult -Results $results -Name $physicalRoot.Name -Path $physicalRoot.Path -Passed $false -Detail $_.Exception.Message
    }
}

$allIsolatedRoots = @($clientRoots | ForEach-Object { $_.AppRoot; $_.DataRoot; $_.TempRoot; $_.DownloadsRoot })
$normalizedIsolatedRoots = @($allIsolatedRoots | ForEach-Object { (Get-NormalizedFullPath -Path $_).ToUpperInvariant() })
$normalizedMultiPcRoot = (Get-NormalizedFullPath -Path $multiPcRoot) + [System.IO.Path]::DirectorySeparatorChar
$allUnderMultiPc = @($allIsolatedRoots | Where-Object {
    -not (Get-NormalizedFullPath -Path $_).StartsWith($normalizedMultiPcRoot, [System.StringComparison]::OrdinalIgnoreCase)
}).Count -eq 0
$allDistinct = @($normalizedIsolatedRoots | Select-Object -Unique).Count -eq $normalizedIsolatedRoots.Count
Add-CheckResult `
    -Results $results `
    -Name "PC-A/PC-B 격리 루트" `
    -Path $multiPcRoot `
    -Passed ($allDistinct -and $allUnderMultiPc) `
    -Detail $(if (-not $allDistinct) { "App/AppData/Temp/Downloads 경로 중복" } elseif (-not $allUnderMultiPc) { "MultiPC 밖의 격리 경로 감지" } else { "8개 루트가 서로 다르고 MultiPC 내부에 있음" })

foreach ($client in $clientRoots) {
    if (Test-Path -LiteralPath $client.Launcher -PathType Leaf) {
        $launcherContent = Get-Content -LiteralPath $client.Launcher -Raw -Encoding UTF8
        $requiredLines = @(
            "set `"APP_DIR=%~dp0App-$($client.Code)`"",
            "set `"APP_ROOT=%~dp0AppData-$($client.Code)`"",
            "set `"TEMP_ROOT=%~dp0Temp-$($client.Code)`"",
            "set `"DOWNLOADS_ROOT=%~dp0Downloads-$($client.Code)`"",
            "set `"GEORAEPLAN_APP_ROOT=%APP_ROOT%`"",
            "set `"GEORAEPLAN_TEMP_ROOT=%TEMP_ROOT%`"",
            "set `"GEORAEPLAN_DOWNLOADS_ROOT=%DOWNLOADS_ROOT%`"",
            "set `"GEORAEPLAN_TEST_MODE=1`"",
            "set `"GEORAEPLAN_DISABLE_LEGACY_MERGE=1`""
        )
        $missingLines = @($requiredLines | Where-Object { -not $launcherContent.Contains($_) })
        Add-CheckResult `
            -Results $results `
            -Name "$($client.Code) launcher 격리 환경변수" `
            -Path $client.Launcher `
            -Passed ($missingLines.Count -eq 0) `
            -Detail $(if ($missingLines.Count -eq 0) { "OK" } else { "누락: $($missingLines -join ', ')" })
    }
}

$sourceTreeHash = $null
try {
    $sourceTreeHash = Get-AppTreeSha256 -Root $appRoot -Label "원본 App"
    Add-CheckResult -Results $results -Name "원본 App 트리 SHA-256" -Path $appRoot -Passed $true -Detail $sourceTreeHash
}
catch {
    Add-CheckResult -Results $results -Name "원본 App 트리 SHA-256" -Path $appRoot -Passed $false -Detail $_.Exception.Message
}

$sourceExecutable = if (Test-Path -LiteralPath $appRoot -PathType Container) { Get-DesktopAppExecutable -Root $appRoot } else { $null }
$sourceExecutableHash = if ($null -ne $sourceExecutable) { (Get-FileHash -LiteralPath $sourceExecutable.FullName -Algorithm SHA256).Hash } else { $null }
Add-CheckResult `
    -Results $results `
    -Name "원본 App 실행 파일" `
    -Path $(if ($null -ne $sourceExecutable) { $sourceExecutable.FullName } else { $appRoot }) `
    -Passed ($null -ne $sourceExecutable) `
    -Detail $(if ($null -ne $sourceExecutable) { $sourceExecutableHash } else { "*.Desktop.App.exe 또는 *.App.exe 누락" })

foreach ($client in $clientRoots) {
    try {
        $clientTreeHash = Get-AppTreeSha256 -Root $client.AppRoot -Label "$($client.Code) App"
        $hashMatches = (
            -not [string]::IsNullOrWhiteSpace($sourceTreeHash) -and
            [string]::Equals($sourceTreeHash, $clientTreeHash, [System.StringComparison]::OrdinalIgnoreCase))
        Add-CheckResult `
            -Results $results `
            -Name "$($client.Code) App 트리 SHA-256 parity" `
            -Path $client.AppRoot `
            -Passed $hashMatches `
            -Detail $(if ($hashMatches) { $clientTreeHash } else { "원본=$sourceTreeHash, $($client.Code)=$clientTreeHash" })
    }
    catch {
        Add-CheckResult -Results $results -Name "$($client.Code) App 트리 SHA-256 parity" -Path $client.AppRoot -Passed $false -Detail $_.Exception.Message
    }

    $clientExecutablePath = if ($null -ne $sourceExecutable) {
        Join-Path $client.AppRoot $sourceExecutable.Name
    }
    else {
        $client.AppRoot
    }
    $clientExecutableExists = Test-Path -LiteralPath $clientExecutablePath -PathType Leaf
    $clientExecutableHash = if ($clientExecutableExists) { (Get-FileHash -LiteralPath $clientExecutablePath -Algorithm SHA256).Hash } else { $null }
    $clientExecutableMatches = (
        $clientExecutableExists -and
        -not [string]::IsNullOrWhiteSpace($sourceExecutableHash) -and
        [string]::Equals($sourceExecutableHash, $clientExecutableHash, [System.StringComparison]::OrdinalIgnoreCase))
    Add-CheckResult `
        -Results $results `
        -Name "$($client.Code) 물리 App 실행 파일/hash" `
        -Path $clientExecutablePath `
        -Passed $clientExecutableMatches `
        -Detail $(if ($clientExecutableMatches) { $clientExecutableHash } elseif (-not $clientExecutableExists) { "실행 파일 누락" } else { "원본 실행 파일 hash 불일치" })

    Test-LoopbackBaseUrl `
        -AppSettingsPath (Join-Path $client.AppRoot "appsettings.json") `
        -ClientCode $client.Code `
        -Results $results
}

$failed = $results | Where-Object { -not $_.Exists }
$overallStatus = if ($failed.Count -eq 0) { "PASS" } else { "FAIL" }

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $reportDirectory = Join-Path $ProjectRoot "테스트 시행\기록"
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $OutputPath = Join-Path $reportDirectory ("multi-pc-readiness-{0}.md" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
}
else {
    $reportDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# 다중 PC 준비 점검 리포트") | Out-Null
$lines.Add("") | Out-Null
$lines.Add("- 실행시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')") | Out-Null
$lines.Add("- 결과: **$overallStatus**") | Out-Null
$lines.Add("- 실행환경 루트: $ExecutionRoot") | Out-Null
$lines.Add("") | Out-Null
$lines.Add("| 항목 | 결과 | 상세 | 경로 |") | Out-Null
$lines.Add("| --- | --- | --- | --- |") | Out-Null

foreach ($result in $results) {
    $status = if ($result.Exists) { "OK" } else { "FAIL" }
    $detail = ([string]$result.Detail).Replace("|", "\|")
    $pathCell = ([string]$result.Path).Replace("|", "\|")
    $lines.Add("| $($result.Name) | $status | $detail | $pathCell |") | Out-Null
}

[System.IO.File]::WriteAllText(
    $OutputPath,
    ($lines -join [Environment]::NewLine),
    (New-Object System.Text.UTF8Encoding($true)))

Write-Host "다중 PC 준비 리포트 저장: $OutputPath"
Write-Host "결과: $overallStatus"

if ($failed.Count -gt 0) {
    throw "다중 PC 준비 점검에서 실패가 확인되었습니다. 리포트: $OutputPath"
}
