[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$NasRoot = '\\192.0.2.10\docker\georaeplan'
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

if (-not (Test-Path -LiteralPath $sourceNasRoot)) {
    throw "NAS config source not found: $sourceNasRoot"
}

New-Item -ItemType Directory -Force $nasOpsRoot | Out-Null

Get-ChildItem -LiteralPath $sourceNasRoot -File | ForEach-Object {
    Copy-TextFileLf -Source $_.FullName -Destination (Join-Path $nasOpsRoot $_.Name)
}

if (-not (Test-Path -LiteralPath (Join-Path $nasOpsRoot '.env'))) {
    Write-Host "NAS .env is missing. Copy '$nasOpsRoot\\.env.example' to '$nasOpsRoot\\.env' and edit it first."
}

Write-Host "sync_done ops=$nasOpsRoot"
