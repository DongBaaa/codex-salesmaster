[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot
$enc = [System.Text.Encoding]::GetEncoding(949)

# Find all FR3 directories (not backups or invalid)
$dirs = Get-ChildItem -Path $scriptDir -Recurse -Directory | Where-Object {
    $_.FullName -notmatch '_backup|_invalid|_tmp' -and
    (Get-ChildItem $_.FullName -Filter "P_*.fr3" -ErrorAction SilentlyContinue).Count -gt 0
}

foreach ($dir in $dirs) {
    Write-Host "`n=== Directory: $($dir.FullName) ==="
    $file = Get-ChildItem -Path $dir.FullName -Filter "P_*A4_1.fr3" | Select-Object -First 1
    if (-not $file) { continue }

    Write-Host "File: $($file.Name)"
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $text = $enc.GetString($bytes)

    $m2 = [regex]::Matches($text, 'DataSetName="([^"]+)"')
    $dsNames = $m2 | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    Write-Host "DataSetName: $($dsNames -join ', ')"

    Add-Type -AssemblyName System.Web
    $htmlDecoded = [System.Web.HttpUtility]::HtmlDecode($text)
    $m3 = [regex]::Matches($htmlDecoded, '\[([^\.\[\]"]+)\."([^"]+)"\]')
    $refs = $m3 | ForEach-Object { "[$($_.Groups[1].Value).`"$($_.Groups[2].Value)`"]" } | Sort-Object -Unique
    Write-Host "Fields: $($refs -join ', ')"
}
