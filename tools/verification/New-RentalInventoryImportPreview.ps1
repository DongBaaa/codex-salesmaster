param(
    [string]$ProjectRoot,
    [string]$WorkbookPath,
    [string]$SheetName = "",
    [int]$SheetIndex = 2,
    [string]$DatabasePath,
    [string]$OutputDirectory,
    [string]$PythonPath = "python",
    [switch]$KeepIntermediate
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}

if ([string]::IsNullOrWhiteSpace($WorkbookPath)) {
    $workbookCandidate = Get-ChildItem -LiteralPath $ProjectRoot -Recurse -Filter "*.xlsb" |
        Where-Object { -not $_.Name.StartsWith("~$") } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $workbookCandidate) {
        throw "No .xlsb workbook was found under ProjectRoot. Pass -WorkbookPath explicitly."
    }
    $WorkbookPath = $workbookCandidate.FullName
}

if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
    $databaseCandidate = Get-ChildItem -LiteralPath $ProjectRoot -Recurse -Filter "*.db" |
        Where-Object { $_.FullName -like "*AppData*data*" -and $_.Length -gt 5000000 } |
        Sort-Object Length -Descending |
        Select-Object -First 1
    if ($null -ne $databaseCandidate) {
        $DatabasePath = $databaseCandidate.FullName
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $ProjectRoot "audit-output\rental-inventory-import-preview-$stamp"
}

$WorkbookPath = (Resolve-Path -LiteralPath $WorkbookPath).Path
if (-not [string]::IsNullOrWhiteSpace($DatabasePath) -and (Test-Path -LiteralPath $DatabasePath)) {
    $DatabasePath = (Resolve-Path -LiteralPath $DatabasePath).Path
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

$sourceTsv = Join-Path $OutputDirectory "rental-inventory-source.tsv"
$analyzer = Join-Path $PSScriptRoot "rental_inventory_import_preview.py"

Write-Output "rental_inventory_preview_workbook=$WorkbookPath"
Write-Output "rental_inventory_preview_sheet_name=$SheetName"
Write-Output "rental_inventory_preview_sheet_index=$SheetIndex"
Write-Output "rental_inventory_preview_database=$DatabasePath"
Write-Output "rental_inventory_preview_output=$OutputDirectory"

$excel = $null
$workbook = $null
try {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $workbook = $excel.Workbooks.Open($WorkbookPath, 0, $true)

    if ([string]::IsNullOrWhiteSpace($SheetName)) {
        $sheet = $workbook.Worksheets.Item($SheetIndex)
        $SheetName = [string]$sheet.Name
    }
    else {
        $sheet = $workbook.Worksheets.Item($SheetName)
    }

    $used = $sheet.UsedRange
    $rowCount = [int]$used.Rows.Count
    $colCount = [int]$used.Columns.Count
    if ($rowCount -lt 4 -or $colCount -lt 1) {
        throw "The target sheet does not contain enough rows for the expected header. rows=$rowCount cols=$colCount"
    }

    # Header is row 4 and data starts at row 5 in the target workbook.
    # Use Value2 array to reduce COM round trips. The Python analyzer normalizes Excel date serials.
    $range = $sheet.Range($sheet.Cells.Item(4, 1), $sheet.Cells.Item($rowCount, $colCount))
    $values = $range.Value2
    $lines = New-Object System.Collections.Generic.List[string]

    for ($r = 1; $r -le ($rowCount - 3); $r++) {
        $cells = New-Object System.Collections.Generic.List[string]
        for ($c = 1; $c -le $colCount; $c++) {
            $value = $values[$r, $c]
            if ($null -eq $value) {
                $text = ""
            }
            else {
                $text = [string]$value
            }
            $text = ($text -replace "`r|`n", " ") -replace "`t", " "
            $cells.Add($text.Trim())
        }
        $lines.Add(($cells -join "`t"))
    }

    [System.IO.File]::WriteAllLines($sourceTsv, $lines, [System.Text.UTF8Encoding]::new($false))
    Write-Output "rental_inventory_preview_resolved_sheet=$SheetName"
    Write-Output "rental_inventory_preview_source_tsv=$sourceTsv"
    Write-Output "rental_inventory_preview_used_range_rows=$rowCount"
    Write-Output "rental_inventory_preview_used_range_cols=$colCount"
}
finally {
    if ($workbook -ne $null) {
        $workbook.Close($false) | Out-Null
        [System.Runtime.Interopservices.Marshal]::ReleaseComObject($workbook) | Out-Null
    }
    if ($excel -ne $null) {
        $excel.Quit() | Out-Null
        [System.Runtime.Interopservices.Marshal]::ReleaseComObject($excel) | Out-Null
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}

$pythonArgs = @(
    $analyzer,
    "--source-tsv", $sourceTsv,
    "--output", $OutputDirectory,
    "--workbook", $WorkbookPath,
    "--sheet", $SheetName
)

if (-not [string]::IsNullOrWhiteSpace($DatabasePath) -and (Test-Path -LiteralPath $DatabasePath)) {
    $pythonArgs += @("--database", $DatabasePath)
}

& $PythonPath @pythonArgs
if ($LASTEXITCODE -ne 0) {
    throw "Rental inventory preview analyzer failed. exit=$LASTEXITCODE"
}

if (-not $KeepIntermediate) {
    # The TSV is intentionally kept in the output directory for auditability.
}

Write-Output "rental_inventory_preview_done=$OutputDirectory"
