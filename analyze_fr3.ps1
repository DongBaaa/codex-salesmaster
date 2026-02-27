# Analyze FR3 file encoding and field names
$path = Join-Path $PSScriptRoot '양식\새 폴더\P_거래명세A4_1.fr3'
Write-Host "Reading: $path"
$enc = [System.Text.Encoding]::GetEncoding(949)
$bytes = [System.IO.File]::ReadAllBytes($path)
$text = $enc.GetString($bytes)

$m = [regex]::Matches($text, 'DataField="([^"]+)"')
Write-Host "`nDataField values:"
$m | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }

$m2 = [regex]::Matches($text, 'DataSetName="([^"]+)"')
Write-Host "`nDataSetName values:"
$m2 | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }

# Extract [Dataset."Field"] patterns from Memo.Text - HTML decoded
$htmlDecoded = [System.Web.HttpUtility]::HtmlDecode($text)
$m3 = [regex]::Matches($htmlDecoded, '\[([^\.\[\]]+)\."([^"]+)"\]')
Write-Host "`nField references [Dataset.Field]:"
$m3 | ForEach-Object { "  [$($_.Groups[1].Value).`"$($_.Groups[2].Value)`"]" } | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
