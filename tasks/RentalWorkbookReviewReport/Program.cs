using System.Text;
using System.Text.Json;
using 거래플랜.Desktop.App.Services;

var auditPath = args
    .FirstOrDefault(arg => arg.StartsWith("--audit=", StringComparison.OrdinalIgnoreCase))
    ?.Split('=', 2)[1]
    ?? @"D:\거래플랜\tasks\ManualAudit\outputs\rental-workbook-audit-final.json";

var outputPath = args
    .FirstOrDefault(arg => arg.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
    ?.Split('=', 2)[1]
    ?? @"D:\거래플랜\tasks\ManualAudit\outputs\rental-workbook-review-report.md";

if (!File.Exists(auditPath))
    throw new FileNotFoundException("감사 결과 파일을 찾을 수 없습니다.", auditPath);

var audit = JsonSerializer.Deserialize<RentalWorkbookAuditResult>(
    await File.ReadAllTextAsync(auditPath, Encoding.UTF8),
    new JsonSerializerOptions(JsonSerializerDefaults.Web))
    ?? throw new InvalidOperationException("감사 결과를 역직렬화하지 못했습니다.");

var blankInstallHoldEntries = audit.Entries
    .Where(entry =>
        entry.Differences.Contains("설치위치", StringComparer.Ordinal) &&
        string.IsNullOrWhiteSpace(entry.WorkbookInstallLocation))
    .OrderBy(entry => entry.RowNumber)
    .ToList();

var unresolvedCustomers = audit.Entries
    .SelectMany(entry => entry.Warnings)
    .Where(warning => warning.StartsWith("고객 마스터를 찾지 못했습니다:", StringComparison.Ordinal))
    .Select(warning => warning["고객 마스터를 찾지 못했습니다:".Length..].Trim())
    .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
    .Select(group => new { Name = group.Key, Count = group.Count() })
    .OrderByDescending(group => group.Count)
    .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
    .ToList();

var ambiguousEntries = audit.Entries
    .Where(entry => string.Equals(entry.Action, "Ambiguous", StringComparison.Ordinal))
    .OrderBy(entry => entry.RowNumber)
    .ToList();

var sb = new StringBuilder();
sb.AppendLine("# 렌탈 대장 후속 검토 보고서");
sb.AppendLine();
sb.AppendLine($"- 생성 시각(UTC): {DateTime.UtcNow:O}");
sb.AppendLine($"- 원본 워크북: `{audit.WorkbookPath}`");
sb.AppendLine($"- 감사 파일: `{auditPath}`");
sb.AppendLine();
sb.AppendLine("## 요약");
sb.AppendLine();
sb.AppendLine($"- 처리 행수: **{audit.ProcessedRowCount}**");
sb.AppendLine($"- ExactMatch: **{audit.ExactMatchCount}**");
sb.AppendLine($"- UpdateSafe: **{audit.UpdateSafeCount}**");
sb.AppendLine($"- Ambiguous: **{audit.AmbiguousCount}**");
sb.AppendLine($"- MissingInWorkbook: **{audit.MissingInWorkbookCount}**");
sb.AppendLine($"- UnresolvedCustomer: **{audit.UnresolvedCustomerCount}**");
sb.AppendLine($"- 설치위치 공란 보류: **{blankInstallHoldEntries.Count}**");
sb.AppendLine();
sb.AppendLine("## 설치위치 공란 보류 목록");
sb.AppendLine();
sb.AppendLine("| 행 | 관리번호 | 거래처 | 품명 | 현재 DB 차이 |");
sb.AppendLine("| --- | --- | --- | --- | --- |");
foreach (var entry in blankInstallHoldEntries.Take(80))
    sb.AppendLine($"| {entry.RowNumber} | {Escape(entry.WorkbookManagementNumber)} | {Escape(entry.WorkbookCustomerName)} | {Escape(entry.WorkbookItemName)} | {Escape(string.Join(", ", entry.Differences))} |");
if (blankInstallHoldEntries.Count > 80)
{
    sb.AppendLine();
    sb.AppendLine($"- 나머지 {blankInstallHoldEntries.Count - 80}건은 JSON 감사 파일에서 확인");
}

sb.AppendLine();
sb.AppendLine("## 고객 마스터 미해결 별칭 상위 목록");
sb.AppendLine();
sb.AppendLine("| 건수 | 워크북 거래처명 |");
sb.AppendLine("| --- | --- |");
foreach (var group in unresolvedCustomers.Take(80))
    sb.AppendLine($"| {group.Count} | {Escape(group.Name)} |");

sb.AppendLine();
sb.AppendLine("## 애매한(Ambiguous) 행 목록");
sb.AppendLine();
sb.AppendLine("| 행 | 관리번호 | 관리ID | 거래처 | 품명 | 경고 |");
sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
foreach (var entry in ambiguousEntries)
    sb.AppendLine($"| {entry.RowNumber} | {Escape(entry.WorkbookManagementNumber)} | {Escape(entry.WorkbookManagementId)} | {Escape(entry.WorkbookCustomerName)} | {Escape(entry.WorkbookItemName)} | {Escape(string.Join(" / ", entry.Warnings))} |");

sb.AppendLine();
sb.AppendLine("## 워크북 미존재 자산 목록");
sb.AppendLine();
sb.AppendLine("| 관리번호 | 관리ID | 거래처 | 품명 | 기계번호 | 설치위치 |");
sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
foreach (var asset in audit.MissingInWorkbookAssets.Take(120))
    sb.AppendLine($"| {Escape(asset.ManagementNumber)} | {Escape(asset.ManagementId)} | {Escape(asset.CustomerName)} | {Escape(asset.ItemName)} | {Escape(asset.MachineNumber)} | {Escape(asset.InstallLocation)} |");
if (audit.MissingInWorkbookAssets.Count > 120)
{
    sb.AppendLine();
    sb.AppendLine($"- 나머지 {audit.MissingInWorkbookAssets.Count - 120}건은 JSON 감사 파일에서 확인");
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
Console.WriteLine(outputPath);

static string Escape(string? value)
    => (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);
