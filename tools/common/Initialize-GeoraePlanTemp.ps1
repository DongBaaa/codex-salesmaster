param(
    [string]$ProjectRoot
)

function Test-GeoraePlanWritableDirectory {
    param(
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    try {
        $resolvedPath = [System.IO.Path]::GetFullPath($Path)
        New-Item -ItemType Directory -Force -Path $resolvedPath | Out-Null
        $probePath = Join-Path $resolvedPath ('.write-test-{0}-{1}.tmp' -f $PID, [Guid]::NewGuid().ToString('N'))
        [System.IO.File]::WriteAllText($probePath, '')
        Remove-Item -LiteralPath $probePath -Force
        return $true
    }
    catch {
        return $false
    }
}

function Resolve-GeoraePlanTempRoot {
    param(
        [string]$ProjectRoot
    )

    $effectiveProjectRoot = $ProjectRoot
    if ([string]::IsNullOrWhiteSpace($effectiveProjectRoot) -and -not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $candidateProjectRoot = Join-Path $PSScriptRoot '..\..'
        if (Test-Path -LiteralPath $candidateProjectRoot) {
            $effectiveProjectRoot = (Resolve-Path -LiteralPath $candidateProjectRoot).Path
        }
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:GEORAEPLAN_TEMP_ROOT)) {
        $candidates.Add($env:GEORAEPLAN_TEMP_ROOT)
    }
    if (-not [string]::IsNullOrWhiteSpace($effectiveProjectRoot)) {
        $candidates.Add((Join-Path $effectiveProjectRoot 'temp'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:TEMP)) {
        $candidates.Add($env:TEMP)
    }

    foreach ($candidate in $candidates) {
        if (Test-GeoraePlanWritableDirectory -Path $candidate) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return [System.IO.Path]::GetTempPath()
}

$resolvedGeoraePlanTempRoot = Resolve-GeoraePlanTempRoot -ProjectRoot $ProjectRoot
$env:GEORAEPLAN_TEMP_ROOT = $resolvedGeoraePlanTempRoot
$env:TEMP = $resolvedGeoraePlanTempRoot
$env:TMP = $resolvedGeoraePlanTempRoot
Write-Host "georaeplan_temp_root=$resolvedGeoraePlanTempRoot"
