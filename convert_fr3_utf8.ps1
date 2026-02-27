[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = "Stop"

$enc949 = [Text.Encoding]::GetEncoding(949)
$utf8NoBom = [Text.UTF8Encoding]::new($false)

# Find all FR3 files, excluding backups
$allFr3 = Get-ChildItem -Path $PSScriptRoot -Recurse -Filter "*.fr3" | Where-Object {
    $_.FullName -notmatch "_backup|_invalid|_tmp"
}

$converted = 0
$skipped = 0

foreach ($f in $allFr3) {
    try {
        $bytes = [IO.File]::ReadAllBytes($f.FullName)

        # Check if already UTF-8 by trying to detect BOM or valid UTF-8
        # If file has Korean as proper UTF-8 (AC 00+ range), skip conversion
        # Simple heuristic: if the file contains bytes that form EUC-KR sequences
        # (bytes in 0xB0-0xC8 range followed by 0xA1-0xFE), it's likely EUC-KR

        # Detect EUC-KR: look for characteristic 2-byte Korean sequences
        $isEucKr = $false
        for ($i = 0; $i -lt $bytes.Length - 1; $i++) {
            $b1 = $bytes[$i]
            $b2 = $bytes[$i+1]
            # EUC-KR Korean character range: first byte 0xB0-0xC8, second byte 0xA1-0xFE
            if ($b1 -ge 0xB0 -and $b1 -le 0xC8 -and $b2 -ge 0xA1 -and $b2 -le 0xFE) {
                $isEucKr = $true
                break
            }
        }

        if (-not $isEucKr) {
            Write-Host "SKIP (already UTF-8 or ASCII): $($f.Name)"
            $skipped++
            continue
        }

        # Read as EUC-KR, write as UTF-8
        $text = $enc949.GetString($bytes)
        [IO.File]::WriteAllText($f.FullName, $text, $utf8NoBom)
        Write-Host "CONVERTED: $($f.FullName)"
        $converted++
    }
    catch {
        Write-Host "ERROR: $($f.FullName) - $_"
    }
}

Write-Host ""
Write-Host "Done. Converted: $converted, Skipped: $skipped"
