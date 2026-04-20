[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$OutputPath = "",
    [string]$MarkdownOutputPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $scriptRoot = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $PSScriptRoot
    }
    else {
        Split-Path -Parent $MyInvocation.MyCommand.Path
    }

    $ProjectRoot = Split-Path -Parent $scriptRoot
}

function Resolve-DotnetCommand {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $candidates = @(
        $env:DOTNET_EXE,
        'D:\.dotnet-sdk\dotnet.exe',
        'C:\Users\beene\.dotnet-sdk\dotnet.exe',
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

    throw "다중 PC 충돌 점검용 dotnet 실행 파일을 찾지 못했습니다. ProjectRoot=$ProjectRoot"
}

$dotnet = Resolve-DotnetCommand -ProjectRoot $ProjectRoot
$projectPath = Join-Path $ProjectRoot "tasks\MultiPcConflictVerifier\MultiPcConflictVerifier.csproj"
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "MultiPcConflictVerifier 프로젝트를 찾을 수 없습니다: $projectPath"
}

$reportDirectory = Join-Path $ProjectRoot "테스트 시행\기록"
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $reportDirectory "multi-pc-conflict-$timestamp.json"
}

if ([string]::IsNullOrWhiteSpace($MarkdownOutputPath)) {
    $MarkdownOutputPath = Join-Path $reportDirectory "multi-pc-conflict-$timestamp.md"
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$markdownDirectory = Split-Path -Parent $MarkdownOutputPath
if (-not [string]::IsNullOrWhiteSpace($markdownDirectory)) {
    New-Item -ItemType Directory -Path $markdownDirectory -Force | Out-Null
}

$args = @(
    'run',
    '--project', $projectPath,
    '--',
    '--project-root', $ProjectRoot,
    '--output', $OutputPath,
    '--markdown-output', $MarkdownOutputPath
)

& $dotnet @args
if ($LASTEXITCODE -ne 0) {
    throw "다중 PC 충돌 자동 점검 실행이 실패했습니다. exit=$LASTEXITCODE"
}

Write-Host "다중 PC 충돌 JSON 리포트: $OutputPath"
Write-Host "다중 PC 충돌 Markdown 리포트: $MarkdownOutputPath"
