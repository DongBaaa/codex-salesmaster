[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string[]]$Paths = @(),
    [switch]$RequireSigned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..')).Path
}
else {
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
}

if ($Paths.Count -eq 0) {
    $deploymentRoot = Join-Path $ProjectRoot '배포'
    $Paths = @(
        (Join-Path $deploymentRoot '거래플랜-PC-설치패키지.exe'),
        (Join-Path $deploymentRoot '관리자용\거래플랜-PC-설치패키지.msi'),
        (Join-Path $deploymentRoot '관리자용\거래플랜-PC-설치패키지\App\거래플랜.exe'),
        (Join-Path $deploymentRoot '관리자용\거래플랜-PC-설치패키지\App\Updater\거래플랜.Updater.exe')
    )
}

$results = New-Object System.Collections.Generic.List[object]
foreach ($path in $Paths) {
    if (-not (Test-Path -LiteralPath $path)) {
        $results.Add([pscustomobject]@{
            Path = $path
            Exists = $false
            Status = 'Missing'
            Signer = ''
        }) | Out-Null
        continue
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $path
    $results.Add([pscustomobject]@{
        Path = (Resolve-Path -LiteralPath $path).Path
        Exists = $true
        Status = [string]$signature.Status
        Signer = if ($null -eq $signature.SignerCertificate) { '' } else { [string]$signature.SignerCertificate.Subject }
    }) | Out-Null
}

$results | Format-Table -AutoSize | Out-Host
$failed = @($results | Where-Object { -not $_.Exists -or $_.Status -ne 'Valid' })
if ($failed.Count -eq 0) {
    Write-Host 'windows_authenticode=PASS'
    exit 0
}

if ($RequireSigned) {
    Write-Error "Windows Authenticode 검증 실패: 서명 누락/무효 파일 $($failed.Count)개"
    exit 1
}

Write-Warning "Windows Authenticode 서명이 아직 준비되지 않은 파일이 $($failed.Count)개 있습니다. 외부 유료 납품 전 코드서명 인증서를 적용하세요."
Write-Host 'windows_authenticode=WARNING_UNSIGNED'
