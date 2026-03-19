[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$OutputPath,
    [string]$Alias = 'georaeplan',
    [string]$StorePass,
    [string]$KeyPass,
    [string]$DistinguishedName = 'CN=GeoraePlan, OU=Private Distribution, O=GeoraePlan, L=Incheon, S=Incheon, C=KR',
    [int]$ValidityDays = 3650,
    [switch]$Force
)

function Resolve-DefaultProjectRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath
    )

    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\signing\georaeplan-release.keystore'
}

if ([string]::IsNullOrWhiteSpace($StorePass)) {
    throw 'StorePass is required.'
}

if ([string]::IsNullOrWhiteSpace($KeyPass)) {
    throw 'KeyPass is required.'
}

$keytoolCommand = Get-Command keytool -ErrorAction SilentlyContinue
if ($null -eq $keytoolCommand) {
    throw 'keytool not found in PATH. Install JDK 17+ and reopen the shell.'
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

if ((Test-Path -LiteralPath $OutputPath) -and -not $Force) {
    throw "Keystore already exists: $OutputPath"
}

$arguments = @(
    '-genkeypair'
    '-v'
    '-keystore', $OutputPath
    '-alias', $Alias
    '-keyalg', 'RSA'
    '-keysize', '2048'
    '-validity', $ValidityDays.ToString()
    '-storepass', $StorePass
    '-keypass', $KeyPass
    '-dname', $DistinguishedName
)

& $keytoolCommand.Source @arguments
if ($LASTEXITCODE -ne 0) {
    throw "keytool failed with exit code $LASTEXITCODE"
}

Write-Host "keystore_ready=$OutputPath"
