param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [string]$AppSettingsPath = (Join-Path $PSScriptRoot 'App\appsettings.json')
)

$normalizedBaseUrl = $BaseUrl.Trim().TrimEnd('/')

if (-not (Test-Path -LiteralPath $AppSettingsPath)) {
    throw "appsettings.json not found: $AppSettingsPath"
}

$json = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json

if ($null -eq $json.Api) {
    $json | Add-Member -NotePropertyName Api -NotePropertyValue ([pscustomobject]@{})
}

$json.Api.BaseUrl = $normalizedBaseUrl
$json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $AppSettingsPath -Encoding UTF8

Write-Host "Updated Api.BaseUrl to $normalizedBaseUrl"
Write-Host "Target file: $AppSettingsPath"
