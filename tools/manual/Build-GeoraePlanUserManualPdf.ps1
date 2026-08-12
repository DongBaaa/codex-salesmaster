[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$PythonPath,
    [switch]$SkipDependencyInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\..'))
}

$resolvedProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$generatorPath = Join-Path $PSScriptRoot 'build_user_manual_pdf.py'
$requirementsPath = Join-Path $PSScriptRoot 'requirements.lock.txt'

if (-not (Test-Path -LiteralPath (Join-Path $resolvedProjectRoot 'README.md') -PathType Leaf)) {
    throw "The requested path is not the GeoraePlan repository root: $resolvedProjectRoot"
}
if (-not (Test-Path -LiteralPath $generatorPath -PathType Leaf)) {
    throw "The user manual PDF generator was not found: $generatorPath"
}
if (-not (Test-Path -LiteralPath $requirementsPath -PathType Leaf)) {
    throw "The user manual dependency lock was not found: $requirementsPath"
}

if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    $venvRoot = Join-Path $resolvedProjectRoot '.tooling\manual-pdf'
    $PythonPath = Join-Path $venvRoot 'Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) {
        $pythonLauncher = Get-Command 'py.exe' -ErrorAction SilentlyContinue
        if ($null -eq $pythonLauncher) {
            throw 'Python 3.13 launcher (py.exe) was not found. Pass a Python 3.13 executable with -PythonPath.'
        }

        & $pythonLauncher.Source -3.13 -m venv $venvRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create the Python 3.13 virtual environment: exit=$LASTEXITCODE"
        }
    }
}

$resolvedPythonPath = (Resolve-Path -LiteralPath $PythonPath).Path
$pythonVersion = & $resolvedPythonPath --version 2>&1
if ($LASTEXITCODE -ne 0 -or $pythonVersion -notmatch '^Python 3\.13(?:\.|$)') {
    throw "The locked user manual wheels require Python 3.13: actual=$pythonVersion"
}

if (-not $SkipDependencyInstall) {
    & $resolvedPythonPath -m pip install `
        --disable-pip-version-check `
        --require-hashes `
        --requirement $requirementsPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install locked user manual dependencies: exit=$LASTEXITCODE"
    }
}

& $resolvedPythonPath $generatorPath --project-root $resolvedProjectRoot
if ($LASTEXITCODE -ne 0) {
    throw "User manual PDF generation or validation failed: exit=$LASTEXITCODE"
}

$verificationPath = Join-Path $resolvedProjectRoot 'output\pdf\georaeplan-user-manual.verification.json'
if (-not (Test-Path -LiteralPath $verificationPath -PathType Leaf)) {
    throw "The user manual PDF verification record was not found: $verificationPath"
}

Write-Output 'verification=output/pdf/georaeplan-user-manual.verification.json'
