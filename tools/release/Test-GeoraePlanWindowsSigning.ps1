[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string[]]$Paths = @(),
    [switch]$RequireSigned,
    [switch]$RequireTimestamp,
    [string]$ExpectedSignerThumbprint = '',
    [string[]]$ExpectedSignerSubjectContains = @(),
    [string[]]$ExpectedTimestampSubjectContains = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptPath)

    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Get-DeploymentRoot {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $candidate = Get-ChildItem -LiteralPath $ProjectRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Set-ApiBaseUrl.ps1') } |
        Select-Object -First 1 -ExpandProperty FullName

    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw 'Deployment root not found under project root.'
    }

    return $candidate
}

function Get-DefaultPaths {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $deploymentRoot = Get-DeploymentRoot -ProjectRoot $ProjectRoot
    $adminOutputRoot = Join-Path $deploymentRoot '관리자용'
    $packageRoot = Join-Path $adminOutputRoot '거래플랜-PC-설치패키지'

    return @(
        (Join-Path $deploymentRoot '거래플랜-PC-설치패키지.exe'),
        (Join-Path $adminOutputRoot '거래플랜-PC-설치패키지.msi'),
        (Join-Path $packageRoot 'App\거래플랜.exe'),
        (Join-Path $packageRoot 'App\Updater\거래플랜.Updater.exe')
    )
}

function Normalize-Thumbprint {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ''
    }

    return (($Value -replace '\s+', '')).ToUpperInvariant()
}

function Get-NormalizedStringArray {
    param([object]$Value)

    $items = New-Object System.Collections.Generic.List[string]
    foreach ($entry in @($Value)) {
        if ($null -eq $entry) {
            continue
        }

        $text = ([string]$entry).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        $items.Add($text) | Out-Null
    }

    return $items.ToArray()
}

function Test-SubjectFragments {
    param(
        [string]$Subject,
        [string[]]$Fragments
    )

    if ($Fragments.Count -eq 0) {
        return $true
    }

    if ([string]::IsNullOrWhiteSpace($Subject)) {
        return $false
    }

    foreach ($fragment in $Fragments) {
        if ($Subject.IndexOf($fragment, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    if ($Paths.Count -eq 0) {
        $ProjectRoot = Resolve-ProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
    }
}
else {
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
}

$ExpectedSignerThumbprint = Normalize-Thumbprint -Value $ExpectedSignerThumbprint
$ExpectedSignerSubjectContains = @(Get-NormalizedStringArray -Value $ExpectedSignerSubjectContains)
$ExpectedTimestampSubjectContains = @(Get-NormalizedStringArray -Value $ExpectedTimestampSubjectContains)

if ($Paths.Count -eq 0) {
    if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
        throw 'ProjectRoot is required when Paths are not supplied.'
    }

    $Paths = Get-DefaultPaths -ProjectRoot $ProjectRoot
}

$results = New-Object System.Collections.Generic.List[object]
foreach ($path in $Paths) {
    $notes = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $path)) {
        $notes.Add('missing') | Out-Null
        $results.Add([pscustomobject]@{
                Path      = $path
                Exists    = $false
                Status    = 'Missing'
                Signer    = ''
                Timestamp = ''
                Notes     = ($notes -join '; ')
            }) | Out-Null
        continue
    }

    $resolvedPath = (Resolve-Path -LiteralPath $path).Path
    $signature = Get-AuthenticodeSignature -LiteralPath $resolvedPath

    $signerSubject = ''
    $signerThumbprint = ''
    if ($null -ne $signature.SignerCertificate) {
        $signerSubject = [string]$signature.SignerCertificate.Subject
        $signerThumbprint = Normalize-Thumbprint -Value $signature.SignerCertificate.Thumbprint
    }

    $timestampSubject = ''
    if ($null -ne $signature.TimeStamperCertificate) {
        $timestampSubject = [string]$signature.TimeStamperCertificate.Subject
    }

    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        $notes.Add(('status={0}' -f $signature.Status)) | Out-Null
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint) -and $signerThumbprint -ne $ExpectedSignerThumbprint) {
        $notes.Add(('signer_thumbprint_mismatch expected={0} actual={1}' -f $ExpectedSignerThumbprint, $signerThumbprint)) | Out-Null
    }

    if (-not (Test-SubjectFragments -Subject $signerSubject -Fragments $ExpectedSignerSubjectContains)) {
        if ($ExpectedSignerSubjectContains.Count -gt 0) {
            $notes.Add(('signer_subject_mismatch expected~={0}' -f ($ExpectedSignerSubjectContains -join '|'))) | Out-Null
        }
    }

    if ($RequireTimestamp -and [string]::IsNullOrWhiteSpace($timestampSubject)) {
        $notes.Add('timestamp_missing') | Out-Null
    }

    if (-not (Test-SubjectFragments -Subject $timestampSubject -Fragments $ExpectedTimestampSubjectContains)) {
        if ($ExpectedTimestampSubjectContains.Count -gt 0) {
            $notes.Add(('timestamp_subject_mismatch expected~={0}' -f ($ExpectedTimestampSubjectContains -join '|'))) | Out-Null
        }
    }

    $results.Add([pscustomobject]@{
            Path      = $resolvedPath
            Exists    = $true
            Status    = [string]$signature.Status
            Signer    = $signerSubject
            Timestamp = $timestampSubject
            Notes     = ($notes -join '; ')
        }) | Out-Null
}

$results | Format-Table -AutoSize | Out-Host

$hasStrictExpectations =
    $RequireSigned.IsPresent -or
    $RequireTimestamp.IsPresent -or
    -not [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint) -or
    $ExpectedSignerSubjectContains.Count -gt 0 -or
    $ExpectedTimestampSubjectContains.Count -gt 0

$failed = @($results | Where-Object {
        -not $_.Exists -or
        $_.Status -ne 'Valid' -or
        -not [string]::IsNullOrWhiteSpace($_.Notes)
    })

if ($failed.Count -eq 0) {
    Write-Host 'windows_authenticode=PASS'
    exit 0
}

if ($hasStrictExpectations) {
    [Console]::Error.WriteLine("Windows Authenticode verification failed: {0} file(s) did not satisfy the requested signer/timestamp checks." -f $failed.Count)
    Write-Host 'windows_authenticode=FAIL'
    exit 1
}

Write-Warning ("Windows Authenticode signed artifacts are not fully ready yet. Unsigned or invalid files: {0}." -f $failed.Count)
Write-Host 'windows_authenticode=WARNING_UNSIGNED'
exit 0
