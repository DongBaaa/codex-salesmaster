[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$OutputPath = "",
    [switch]$RequirePrinter,
    [switch]$RequireOnlinePrinter,
    [switch]$FailOnWarnings
)

$ErrorActionPreference = 'Stop'

function Resolve-DefaultProjectRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptPath)
    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Add-Issue {
    param(
        [System.Collections.Generic.List[string]]$Issues,
        [string]$Message
    )
    if (-not [string]::IsNullOrWhiteSpace($Message)) {
        $Issues.Add($Message) | Out-Null
    }
}

function Read-TextFileSafely {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return ''
    }

    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8
}

function Test-SourceFallbackSupport {
    param([Parameter(Mandatory = $true)][string]$Root)

    $warnings = [System.Collections.Generic.List[string]]::new()
    $notes = [System.Collections.Generic.List[string]]::new()
    $failures = [System.Collections.Generic.List[string]]::new()
    $xamlPath = Join-Path $Root 'Desktop\거래플랜.Desktop.App\Views\TradePrintWindow.xaml'
    $codeBehindPath = Join-Path $Root 'Desktop\거래플랜.Desktop.App\Views\TradePrintWindow.xaml.cs'
    $executorPath = Join-Path $Root 'Desktop\거래플랜.Desktop.App\Services\TradePrintExecutor.cs'
    $xaml = Read-TextFileSafely -Path $xamlPath
    $codeBehind = Read-TextFileSafely -Path $codeBehindPath
    $executor = Read-TextFileSafely -Path $executorPath

    if ([string]::IsNullOrWhiteSpace($xaml)) {
        Add-Issue -Issues $failures -Message "전용 인쇄창 XAML을 찾지 못했습니다: $xamlPath"
    }
    if ([string]::IsNullOrWhiteSpace($codeBehind)) {
        Add-Issue -Issues $failures -Message "전용 인쇄창 code-behind를 찾지 못했습니다: $codeBehindPath"
    }
    if ([string]::IsNullOrWhiteSpace($executor)) {
        Add-Issue -Issues $failures -Message "인쇄 실행 서비스를 찾지 못했습니다: $executorPath"
    }

    foreach ($check in @(
        @{ Source = $xaml; Needle = 'PDF 저장'; Label = 'PDF 저장 버튼' },
        @{ Source = $xaml; Needle = '파일 저장(XPS)'; Label = 'XPS 파일 저장 버튼' },
        @{ Source = $xaml; Needle = '거래플랜 전용 인쇄'; Label = '전용 인쇄창 제목' },
        @{ Source = $codeBehind; Needle = 'OnRefreshPrintersClick'; Label = '프린터 새로고침 동작' },
        @{ Source = $codeBehind; Needle = 'OnOpenPrinterManagementClick'; Label = 'Windows 프린터 관리 열기 동작' },
        @{ Source = $executor; Needle = 'SaveDocumentAsPdf'; Label = 'PDF 저장 구현' },
        @{ Source = $executor; Needle = 'SaveDocumentAsXps'; Label = 'XPS 저장 구현' },
        @{ Source = $executor; Needle = 'LoadInstalledPrintQueues'; Label = '프린터 목록 로딩 구현' },
        @{ Source = $executor; Needle = 'EnumeratedPrintQueueTypes.DirectPrinting'; Label = '직접 연결 프린터 검색' }
    )) {
        if ([string]::IsNullOrWhiteSpace([string]$check.Source) -or ([string]$check.Source).IndexOf([string]$check.Needle, [System.StringComparison]::Ordinal) -lt 0) {
            Add-Issue -Issues $failures -Message "인쇄 fallback/source guard 누락: $($check.Label)"
        }
    }

    if ($executor.IndexOf('new PrintDialog', [System.StringComparison]::Ordinal) -ge 0 -or
        $executor.IndexOf('System.Windows.Controls.PrintDialog', [System.StringComparison]::Ordinal) -ge 0 -or
        $codeBehind.IndexOf('new PrintDialog', [System.StringComparison]::Ordinal) -ge 0 -or
        $codeBehind.IndexOf('System.Windows.Controls.PrintDialog', [System.StringComparison]::Ordinal) -ge 0) {
        Add-Issue -Issues $failures -Message '기본 WPF PrintDialog 직접 호출이 감지되었습니다. 거래플랜 전용 인쇄창을 우회하면 안 됩니다.'
    }

    if ($failures.Count -eq 0) {
        Add-Issue -Issues $notes -Message '소스 기준 전용 인쇄창/PDF/XPS fallback은 확인됐습니다. 실제 종이 출력은 현장 장치 상태에 따라 별도 확인이 필요합니다.'
    }

    return [pscustomobject]@{
        SourceFallbackOk = $failures.Count -eq 0
        Warnings = @($warnings)
        Notes = @($notes)
        Failures = @($failures)
    }
}

function Get-PrinterSnapshot {
    $warnings = [System.Collections.Generic.List[string]]::new()
    $failures = [System.Collections.Generic.List[string]]::new()
    $printers = [System.Collections.Generic.List[object]]::new()
    $defaultPrinterName = ''

    try {
        Add-Type -AssemblyName System.Printing -ErrorAction Stop | Out-Null
        $server = [System.Printing.LocalPrintServer]::new()
        try {
            $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            $addQueueSnapshot = {
                param([object]$Queue)

                if ($null -eq $Queue) {
                    return
                }

                $fullName = [string]$Queue.FullName
                if ([string]::IsNullOrWhiteSpace($fullName)) {
                    $fullName = [string]$Queue.Name
                }
                if ([string]::IsNullOrWhiteSpace($fullName) -or -not $seen.Add($fullName)) {
                    return
                }

                $isOffline = $false
                $status = ''
                $location = ''
                $comment = ''
                try { $isOffline = [bool]$Queue.IsOffline } catch { }
                try { $status = [string]$Queue.QueueStatus } catch { }
                try { $location = [string]$Queue.Location } catch { }
                try { $comment = [string]$Queue.Comment } catch { }

                $printers.Add([pscustomobject]@{
                    Name = $fullName
                    IsDefault = [string]::Equals($fullName, $defaultPrinterName, [System.StringComparison]::OrdinalIgnoreCase)
                    IsOffline = $isOffline
                    Status = $status
                    Location = $location
                    Comment = $comment
                }) | Out-Null
            }

            try {
                $defaultQueue = $server.DefaultPrintQueue
                if ($null -ne $defaultQueue) {
                    $defaultPrinterName = [string]$defaultQueue.FullName
                    & $addQueueSnapshot $defaultQueue
                }
            }
            catch {
                Add-Issue -Issues $warnings -Message "기본 프린터를 확인하지 못했습니다: $($_.Exception.Message)"
            }

            $queueTypeGroups = @(
                @([System.Printing.EnumeratedPrintQueueTypes]::Local, [System.Printing.EnumeratedPrintQueueTypes]::Connections, [System.Printing.EnumeratedPrintQueueTypes]::Shared),
                @([System.Printing.EnumeratedPrintQueueTypes]::DirectPrinting),
                @([System.Printing.EnumeratedPrintQueueTypes]::PushedMachineConnection),
                @([System.Printing.EnumeratedPrintQueueTypes]::PushedUserConnection),
                @([System.Printing.EnumeratedPrintQueueTypes]::WorkOffline)
            )
            foreach ($group in $queueTypeGroups) {
                try {
                    foreach ($queue in $server.GetPrintQueues($group)) {
                        & $addQueueSnapshot $queue
                    }
                }
                catch {
                    Add-Issue -Issues $warnings -Message "프린터 그룹 조회 실패($($group -join ', ')): $($_.Exception.Message)"
                }
            }
        }
        finally {
            $server.Dispose()
        }
    }
    catch {
        Add-Issue -Issues $failures -Message "Windows 프린터 시스템을 읽지 못했습니다: $($_.Exception.Message)"
    }

    return [pscustomobject]@{
        DefaultPrinterName = $defaultPrinterName
        Printers = @($printers)
        Warnings = @($warnings)
        Failures = @($failures)
    }
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputDirectory = Join-Path $ProjectRoot 'output'
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $OutputPath = Join-Path $outputDirectory ("print-environment-{0}.md" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
else {
    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }
}

$sourceFallback = Test-SourceFallbackSupport -Root $ProjectRoot
$printerSnapshot = Get-PrinterSnapshot
$warnings = [System.Collections.Generic.List[string]]::new()
$notes = [System.Collections.Generic.List[string]]::new()
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($warning in @($sourceFallback.Warnings + $printerSnapshot.Warnings)) {
    Add-Issue -Issues $warnings -Message ([string]$warning)
}
foreach ($note in @($sourceFallback.Notes)) {
    Add-Issue -Issues $notes -Message ([string]$note)
}
foreach ($failure in @($sourceFallback.Failures + $printerSnapshot.Failures)) {
    Add-Issue -Issues $failures -Message ([string]$failure)
}

$printerCount = @($printerSnapshot.Printers).Count
$onlinePrinterCount = @($printerSnapshot.Printers | Where-Object { -not $_.IsOffline }).Count
if ($printerCount -eq 0) {
    Add-Issue -Issues $warnings -Message '등록된 Windows 프린터가 없습니다. 거래플랜 전용 인쇄창에서 PDF/XPS로 저장한 뒤 복합기가 연결된 PC에서 출력해야 합니다.'
}
if ($RequirePrinter -and $printerCount -eq 0) {
    Add-Issue -Issues $failures -Message 'RequirePrinter가 지정되었지만 등록된 Windows 프린터가 없습니다.'
}
if ($RequireOnlinePrinter -and $onlinePrinterCount -eq 0) {
    Add-Issue -Issues $failures -Message 'RequireOnlinePrinter가 지정되었지만 온라인 상태로 보이는 프린터가 없습니다.'
}
if ($printerCount -gt 0 -and $onlinePrinterCount -eq 0) {
    Add-Issue -Issues $warnings -Message '등록된 프린터는 있으나 모두 오프라인으로 보입니다. 복합기 전원/네트워크/드라이버 상태를 확인해야 합니다.'
}

$result = if ($failures.Count -gt 0) {
    'FAIL'
}
elseif ($warnings.Count -gt 0) {
    if ($FailOnWarnings) { 'FAIL' } else { 'WARN' }
}
else {
    'PASS'
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# 거래플랜 인쇄 환경 점검 리포트') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- 실행시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')") | Out-Null
$lines.Add("- 결과: **$result**") | Out-Null
$lines.Add("- ProjectRoot: $ProjectRoot") | Out-Null
$lines.Add("- RequirePrinter: $([bool]$RequirePrinter)") | Out-Null
$lines.Add("- RequireOnlinePrinter: $([bool]$RequireOnlinePrinter)") | Out-Null
$lines.Add("- FailOnWarnings: $([bool]$FailOnWarnings)") | Out-Null
$lines.Add("- 전용 인쇄창/PDF/XPS fallback source: $($sourceFallback.SourceFallbackOk)") | Out-Null
$lines.Add("- 기본 프린터: $($printerSnapshot.DefaultPrinterName)") | Out-Null
$lines.Add("- 프린터 수: $printerCount") | Out-Null
$lines.Add("- 온라인으로 보이는 프린터 수: $onlinePrinterCount") | Out-Null
$lines.Add('') | Out-Null
$lines.Add('## 프린터 목록') | Out-Null
$lines.Add('') | Out-Null
$lines.Add('| 기본 | 오프라인 | 이름 | 상태 | 위치 | 비고 |') | Out-Null
$lines.Add('| --- | --- | --- | --- | --- | --- |') | Out-Null
foreach ($printer in @($printerSnapshot.Printers)) {
    $name = ([string]$printer.Name).Replace('|', '\|')
    $status = ([string]$printer.Status).Replace('|', '\|')
    $location = ([string]$printer.Location).Replace('|', '\|')
    $comment = ([string]$printer.Comment).Replace('|', '\|')
    $lines.Add("| $($printer.IsDefault) | $($printer.IsOffline) | $name | $status | $location | $comment |") | Out-Null
}
if ($printerCount -eq 0) {
    $lines.Add('| - | - | 등록된 프린터 없음 | - | - | PDF/XPS 저장 fallback 사용 |') | Out-Null
}

if ($warnings.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## 경고') | Out-Null
    foreach ($warning in $warnings) {
        $lines.Add("- $warning") | Out-Null
    }
}
if ($notes.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## 참고') | Out-Null
    foreach ($note in $notes) {
        $lines.Add("- $note") | Out-Null
    }
}
if ($failures.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## 실패') | Out-Null
    foreach ($failure in $failures) {
        $lines.Add("- $failure") | Out-Null
    }
}

[System.IO.File]::WriteAllText($OutputPath, ($lines -join [Environment]::NewLine), [System.Text.UTF8Encoding]::new($true))
Write-Host "Print environment report: $OutputPath"
Write-Host "Result: $result"
Write-Host "PrinterCount: $printerCount"
Write-Host "OnlinePrinterCount: $onlinePrinterCount"

if ($result -eq 'FAIL') {
    exit 1
}
exit 0
