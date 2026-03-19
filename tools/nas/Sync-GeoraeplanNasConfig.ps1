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

Copy-TextFileLf -Source (Join-Path $sourceNasRoot 'docker-compose.yml') -Destination (Join-Path $nasOpsRoot 'docker-compose.yml')
Copy-TextFileLf -Source (Join-Path $sourceNasRoot '.env.example') -Destination (Join-Path $nasOpsRoot '.env.example')

$realExample = Join-Path $sourceNasRoot '.env.api.example.invalid.example'
if (Test-Path -LiteralPath $realExample) {
    Copy-TextFileLf -Source $realExample -Destination (Join-Path $nasOpsRoot '.env.api.example.invalid.example')
}

if (-not (Test-Path -LiteralPath (Join-Path $nasOpsRoot '.env'))) {
    Write-Host "NAS .env is missing. Copy '$nasOpsRoot\\.env.example' to '$nasOpsRoot\\.env' and edit it first."
}

Write-Host "sync_done ops=$nasOpsRoot"
