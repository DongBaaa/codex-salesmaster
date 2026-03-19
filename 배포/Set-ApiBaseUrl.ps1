param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [string[]]$AppSettingsPaths
)

$normalizedBaseUrl = $BaseUrl.Trim().TrimEnd('/')

if (-not $AppSettingsPaths -or $AppSettingsPaths.Count -eq 0) {
    $AppSettingsPaths = @((Join-Path $PSScriptRoot 'App\appsettings.json'))

    Get-ChildItem -LiteralPath $PSScriptRoot -Directory | ForEach-Object {
        $candidate = Join-Path $_.FullName 'appsettings.json'
        $hasClientCmd = @(Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.cmd' -ErrorAction SilentlyContinue).Count -gt 0
        $hasDbFiles = @(Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.db' -ErrorAction SilentlyContinue).Count -gt 0
        $hasApiSection = $false

        if (Test-Path -LiteralPath $candidate) {
            try {
                $probeJson = Get-Content -LiteralPath $candidate -Raw | ConvertFrom-Json
                $hasApiSection = $null -ne $probeJson.PSObject.Properties['Api']
            } catch {
                $hasApiSection = $false
            }
        }

        if ($_.Name -ne 'App' -and $hasClientCmd -and $hasApiSection -and -not $hasDbFiles) {
            $AppSettingsPaths += $candidate
        }
    }
}

foreach ($path in ($AppSettingsPaths | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "appsettings.json not found: $path"
    }

    $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json

    if ($null -eq $json.PSObject.Properties['Api']) {
        $json | Add-Member -NotePropertyName Api -NotePropertyValue ([pscustomobject]@{ BaseUrl = $normalizedBaseUrl })
    } elseif ($null -eq $json.Api.PSObject.Properties['BaseUrl']) {
        $json.Api | Add-Member -NotePropertyName BaseUrl -NotePropertyValue $normalizedBaseUrl
    }

    $json.Api.BaseUrl = $normalizedBaseUrl
    $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding UTF8

    Write-Host "Updated Api.BaseUrl to $normalizedBaseUrl"
    Write-Host "Target file: $path"
}
