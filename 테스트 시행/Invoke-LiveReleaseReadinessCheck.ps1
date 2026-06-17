[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [ValidateSet('Pre','Post')][string]$Mode = 'Pre',
    [string]$Channel = 'stable',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptRoot)
    return (Resolve-Path (Join-Path $ScriptRoot '..')).Path
}

function Get-CsprojPropertyValue {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectFile,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    [xml]$xml = Get-Content -LiteralPath $ProjectFile -Raw
    foreach ($group in $xml.Project.PropertyGroup) {
        $property = $group.$PropertyName
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property)) {
            return ([string]$property).Trim()
        }
    }

    return ''
}

function Get-SafeString {
    param($Value)

    if ($null -eq $Value) {
        return ''
    }

    return [string]$Value
}

function Normalize-VersionText {
    param([string]$Value)

    $normalized = Get-SafeString $Value
    $normalized = $normalized.Trim()
    if ($normalized.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    $plusIndex = $normalized.IndexOf('+')
    if ($plusIndex -ge 0) {
        $normalized = $normalized.Substring(0, $plusIndex)
    }

    return $normalized
}

function Compare-Version {
    param(
        [string]$Left,
        [string]$Right
    )

    $leftVersion = [Version]'0.0.0'
    $rightVersion = [Version]'0.0.0'

    [Version]::TryParse((Normalize-VersionText $Left), [ref]$leftVersion) | Out-Null
    [Version]::TryParse((Normalize-VersionText $Right), [ref]$rightVersion) | Out-Null
    return $leftVersion.CompareTo($rightVersion)
}

function Add-Check {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [Parameter(Mandatory = $true)][string]$Detail
    )

    $script:Checks.Add([pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Detail = $Detail
    }) | Out-Null
}

function Get-ResidueFiles {
    param([string]$RootPath)

    if ([string]::IsNullOrWhiteSpace($RootPath) -or -not (Test-Path -LiteralPath $RootPath)) {
        return @()
    }

    return Get-ChildItem -Path $RootPath -Recurse -File -Include '*.old', '*.bak', '*.deleteme', '*.rollback'
}

function Write-MarkdownReport {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Checks,
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$DesktopVersion,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine('# live 반영 준비 점검')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("- 실행 시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    [void]$builder.AppendLine("- 모드: $Mode")
    [void]$builder.AppendLine("- 채널: $Channel")
    [void]$builder.AppendLine("- 데스크톱 버전: $DesktopVersion")
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('| 결과 | 항목 | 상세 |')
    [void]$builder.AppendLine('| --- | --- | --- |')
    foreach ($check in $Checks) {
        $status = if ($check.Passed) { 'PASS' } else { 'FAIL' }
        $detail = (Get-SafeString $check.Detail).Replace('|', '\|')
        [void]$builder.AppendLine("| $status | $($check.Name) | $detail |")
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $builder.ToString(), $utf8NoBom)
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-ProjectRoot -ScriptRoot $scriptRoot
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $scriptRoot ('기록\live-readiness-' + $Mode.ToLowerInvariant() + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.md')
}

$Checks = New-Object System.Collections.Generic.List[object]
$desktopProject = Join-Path $ProjectRoot 'Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj'
$updaterProject = Join-Path $ProjectRoot 'Updater\거래플랜.Updater\거래플랜.Updater.csproj'
$manifestPath = Join-Path $ProjectRoot ("배포\업데이트\manifest\$Channel.json")
$desktopInstallerPath = Join-Path $ProjectRoot '배포\거래플랜-PC-설치패키지.exe'
$desktopZipCandidates = @(
    (Join-Path $ProjectRoot '배포\관리자용\거래플랜-PC-설치패키지.zip'),
    (Join-Path $ProjectRoot '배포\설치패키지\관리자용\거래플랜-PC-설치패키지.zip'),
    (Join-Path $ProjectRoot '배포\설치패키지\거래플랜-PC-설치패키지.zip')
)
$desktopPackageRootCandidates = @(
    (Join-Path $ProjectRoot '배포\관리자용\거래플랜-PC-설치패키지'),
    (Join-Path $ProjectRoot '배포\설치패키지\관리자용\거래플랜-PC-설치패키지'),
    (Join-Path $ProjectRoot '배포\설치패키지\거래플랜-PC-설치패키지')
)
$desktopPackageRoot = $desktopPackageRootCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$desktopPackageUpdaterPath = if ([string]::IsNullOrWhiteSpace($desktopPackageRoot)) { '' } else { Join-Path $desktopPackageRoot 'App\Updater\거래플랜.Updater.exe' }
$desktopPackageAppSettingsPath = if ([string]::IsNullOrWhiteSpace($desktopPackageRoot)) { '' } else { Join-Path $desktopPackageRoot 'App\appsettings.json' }
$desktopInstallScriptPath = if ([string]::IsNullOrWhiteSpace($desktopPackageRoot)) { '' } else { Join-Path $desktopPackageRoot 'Install-GeoraePlan.ps1' }
$desktopVersion = if (Test-Path -LiteralPath $desktopProject) {
    Get-CsprojPropertyValue -ProjectFile $desktopProject -PropertyName 'Version'
}
else {
    ''
}

Add-Check '데스크톱 프로젝트 버전 확인' (-not [string]::IsNullOrWhiteSpace($desktopVersion)) $(if ([string]::IsNullOrWhiteSpace($desktopVersion)) { "버전을 찾지 못했습니다: $desktopProject" } else { "버전 $desktopVersion" })
Add-Check '업데이터 프로젝트 존재' (Test-Path -LiteralPath $updaterProject) $updaterProject

if ($Mode -eq 'Pre') {
    $requiredScripts = @(
        (Join-Path $ProjectRoot 'tools\release\Build-GeoraePlanDesktopInstaller.ps1'),
        (Join-Path $ProjectRoot 'tools\release\Publish-GeoraePlanUpdateAssets.ps1'),
        (Join-Path $ProjectRoot 'tools\linux\Publish-GeoraeplanLinuxPcRelease.ps1'),
        (Join-Path $scriptRoot 'Deploy-After-Test.ps1')
    )

    foreach ($requiredScript in $requiredScripts) {
        Add-Check ("스크립트 존재: " + [System.IO.Path]::GetFileName($requiredScript)) (Test-Path -LiteralPath $requiredScript) $requiredScript
    }

    $liveChecklistPath = Join-Path $scriptRoot '검증 체크리스트-live반영.md'
    Add-Check 'live 체크리스트 존재' (Test-Path -LiteralPath $liveChecklistPath) $liveChecklistPath

    if (Test-Path -LiteralPath $manifestPath) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $manifestDesktopVersion = Get-SafeString $manifest.desktop.version
        $canCompare = -not [string]::IsNullOrWhiteSpace($desktopVersion) -and -not [string]::IsNullOrWhiteSpace($manifestDesktopVersion)
        $versionComparison = if ($canCompare) { Compare-Version $desktopVersion $manifestDesktopVersion } else { -999 }
        $isVersionReady = $canCompare -and $versionComparison -ge 0
        Add-Check '소스 버전이 현재 local manifest보다 낮지 않음' $isVersionReady $(if (-not $canCompare) { 'manifest 또는 소스 버전 정보를 비교할 수 없습니다.' } elseif ($versionComparison -eq 0) { "소스 $desktopVersion / manifest $manifestDesktopVersion (동일 버전: 실제 live 업데이트 알림이 필요하면 Linux PC 기준 버전 확인 또는 버전 상향이 필요할 수 있습니다.)" } else { "소스 $desktopVersion / manifest $manifestDesktopVersion" })
    }
    else {
        Add-Check '기존 manifest 존재 여부' $true '기존 manifest가 없어도 최초 반영은 가능합니다.'
    }
}
else {
    Add-Check 'manifest 파일 생성' (Test-Path -LiteralPath $manifestPath) $manifestPath

    if (Test-Path -LiteralPath $manifestPath) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $manifestDesktop = $manifest.desktop
        $manifestDesktopVersion = Get-SafeString $manifestDesktop.version
        Add-Check 'manifest desktop 버전 일치' ((-not [string]::IsNullOrWhiteSpace($desktopVersion)) -and ($manifestDesktopVersion -eq $desktopVersion)) ("manifest $manifestDesktopVersion / source $desktopVersion")

        $minimumSupportedVersion = Get-SafeString $manifestDesktop.minimumSupportedVersion
        $mandatoryDesktop = [bool]$manifestDesktop.mandatory
        Add-Check '필수 업데이트 minimumSupportedVersion 설정' ((-not $mandatoryDesktop) -or (-not [string]::IsNullOrWhiteSpace($minimumSupportedVersion))) $(if ($mandatoryDesktop) { "mandatory=$mandatoryDesktop / minimumSupportedVersion=$minimumSupportedVersion" } else { 'desktop mandatory 아님' })

        $downloadedDesktopPackage = if ($manifestDesktop -and -not [string]::IsNullOrWhiteSpace((Get-SafeString $manifestDesktop.fileName))) {
            Join-Path $ProjectRoot ("배포\업데이트\downloads\desktop\" + (Get-SafeString $manifestDesktop.fileName))
        }
        else {
            ''
        }
        Add-Check 'manifest desktop 다운로드 파일 존재' ((-not [string]::IsNullOrWhiteSpace($downloadedDesktopPackage)) -and (Test-Path -LiteralPath $downloadedDesktopPackage)) $downloadedDesktopPackage
    }

    $desktopZipPath = $desktopZipCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    Add-Check '데스크톱 업데이트 zip 존재' (-not [string]::IsNullOrWhiteSpace($desktopZipPath)) $(if ([string]::IsNullOrWhiteSpace($desktopZipPath)) { '생성된 zip을 찾지 못했습니다.' } else { $desktopZipPath })
    Add-Check '설치 패키지 exe 존재' (Test-Path -LiteralPath $desktopInstallerPath) $desktopInstallerPath
    Add-Check '설치 패키지 폴더 존재' (-not [string]::IsNullOrWhiteSpace($desktopPackageRoot)) $(if ([string]::IsNullOrWhiteSpace($desktopPackageRoot)) { '설치 패키지 폴더를 찾지 못했습니다.' } else { $desktopPackageRoot })
    Add-Check '설치 패키지 내 appsettings.json 포함' ((-not [string]::IsNullOrWhiteSpace($desktopPackageAppSettingsPath)) -and (Test-Path -LiteralPath $desktopPackageAppSettingsPath)) $desktopPackageAppSettingsPath
    Add-Check '설치 패키지 내 업데이터 포함' ((-not [string]::IsNullOrWhiteSpace($desktopPackageUpdaterPath)) -and (Test-Path -LiteralPath $desktopPackageUpdaterPath)) $desktopPackageUpdaterPath
    Add-Check '설치 패키지 내 Install-GeoraePlan.ps1 포함' ((-not [string]::IsNullOrWhiteSpace($desktopInstallScriptPath)) -and (Test-Path -LiteralPath $desktopInstallScriptPath)) $desktopInstallScriptPath

    if (-not [string]::IsNullOrWhiteSpace($desktopPackageRoot) -and (Test-Path -LiteralPath $desktopPackageRoot)) {
        $residueFiles = @(Get-ResidueFiles -RootPath $desktopPackageRoot)
        Add-Check '설치 패키지 잔여 파일 없음' ($residueFiles.Count -eq 0) $(if ($residueFiles.Count -eq 0) { '잔여 .old/.bak/.deleteme/.rollback 파일이 없습니다.' } else { ($residueFiles | Select-Object -First 5 -ExpandProperty FullName) -join '; ' })
    }

    if (-not [string]::IsNullOrWhiteSpace($desktopZipPath) -and (Test-Path -LiteralPath $desktopZipPath)) {
        $desktopZipInfo = Get-Item -LiteralPath $desktopZipPath
        Add-Check '데스크톱 업데이트 zip 크기 확인' ($desktopZipInfo.Length -gt 0) ("크기 {0:N0} bytes" -f $desktopZipInfo.Length)
    }

    if (Test-Path -LiteralPath $desktopInstallerPath) {
        $desktopInstallerInfo = Get-Item -LiteralPath $desktopInstallerPath
        Add-Check '설치 패키지 exe 크기 확인' ($desktopInstallerInfo.Length -gt 0) ("크기 {0:N0} bytes" -f $desktopInstallerInfo.Length)
    }
}

Write-MarkdownReport -Path $OutputPath -Checks $Checks -Mode $Mode -DesktopVersion $desktopVersion -Channel $Channel

$failedChecks = @($Checks | Where-Object { -not $_.Passed })
if ($failedChecks.Count -gt 0) {
    Write-Host "live readiness failed: $OutputPath" -ForegroundColor Red
    foreach ($failed in $failedChecks) {
        Write-Host ("- {0}: {1}" -f $failed.Name, $failed.Detail) -ForegroundColor Red
    }
    exit 1
}

Write-Host "live readiness ok: $OutputPath" -ForegroundColor Green
