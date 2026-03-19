[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$OutputPath,
    [string]$JavaSdkDirectory,
    [string]$KeytoolPath,
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

function Get-ResolvedJavaSdkDirectory {
    param(
        [string]$RequestedPath
    )

    $candidates = [System.Collections.Generic.List[string]]::new()

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates.Add($RequestedPath) | Out-Null
    }

    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $candidates.Add($env:JAVA_HOME) | Out-Null
    }

    foreach ($commandName in @('javac', 'keytool', 'java')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            $candidates.Add((Split-Path -Parent (Split-Path -Parent $command.Source))) | Out-Null
        }
    }

    foreach ($pattern in @(
        (Join-Path $env:USERPROFILE '.antigravity\extensions\*\jre\*\bin\keytool.exe'),
        'C:\Program Files\Microsoft\jdk*\bin\keytool.exe',
        'C:\Program Files\Java\*\bin\keytool.exe',
        'C:\Deployment Tool\jre8\bin\keytool.exe'
    )) {
        $match = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $match) {
            $candidates.Add((Split-Path -Parent (Split-Path -Parent $match.FullName))) | Out-Null
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath (Join-Path $candidate 'bin\keytool.exe'))) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
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

if ([string]::IsNullOrWhiteSpace($KeytoolPath)) {
    if (-not [string]::IsNullOrWhiteSpace($JavaSdkDirectory)) {
        $candidate = Join-Path $JavaSdkDirectory 'bin\keytool.exe'
        if (Test-Path -LiteralPath $candidate) {
            $KeytoolPath = $candidate
        }
    }

    if ([string]::IsNullOrWhiteSpace($KeytoolPath)) {
        $JavaSdkDirectory = Get-ResolvedJavaSdkDirectory -RequestedPath $JavaSdkDirectory
        if (-not [string]::IsNullOrWhiteSpace($JavaSdkDirectory)) {
            $KeytoolPath = Join-Path $JavaSdkDirectory 'bin\keytool.exe'
        }
    }
}

if ([string]::IsNullOrWhiteSpace($KeytoolPath) -or -not (Test-Path -LiteralPath $KeytoolPath)) {
    throw 'keytool not found. Install JDK 17+ or pass -JavaSdkDirectory / -KeytoolPath.'
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

& $KeytoolPath @arguments
if ($LASTEXITCODE -ne 0) {
    throw "keytool failed with exit code $LASTEXITCODE"
}

Write-Host "keystore_ready=$OutputPath"
Write-Host "keytool_path=$KeytoolPath"
