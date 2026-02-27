using System.Text;
using 외부 리포팅 도구;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var templateDir = GetArg(args, "--template-dir")
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "양식"));
var csvPath = GetArg(args, "--csv");
var overwrite = HasFlag(args, "--overwrite");
var updateCsv = HasFlag(args, "--update-csv");

if (!Directory.Exists(templateDir))
{
    Console.Error.WriteLine($"Template directory not found: {templateDir}");
    return 2;
}

if (string.IsNullOrWhiteSpace(csvPath))
{
    csvPath = Directory.EnumerateFiles(templateDir, "FR3_TO_FRX_*.csv", SearchOption.TopDirectoryOnly)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
}

if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
{
    Console.Error.WriteLine($"CSV file not found under: {templateDir}");
    return 2;
}

var rows = ReadRows(csvPath)
    .OrderBy(r => r.Order)
    .ToList();

if (rows.Count == 0)
{
    Console.Error.WriteLine($"No rows found in CSV: {csvPath}");
    return 2;
}

var backupDir = Path.Combine(templateDir, "_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
Directory.CreateDirectory(backupDir);

var converted = new List<string>();
var skipped = new List<string>();
var failed = new List<string>();

foreach (var row in rows)
{
    var fr3Path = Path.Combine(templateDir, row.TemplateFr3);
    var frxPath = Path.Combine(templateDir, row.TargetFrx);

    if (!File.Exists(fr3Path))
    {
        failed.Add($"MISSING FR3: {row.TemplateFr3}");
        if (updateCsv)
            row.Status = "MISSING_FR3";
        continue;
    }

    if (File.Exists(frxPath) && !overwrite)
    {
        skipped.Add($"EXISTS: {row.TargetFrx}");
        continue;
    }

    File.Copy(fr3Path, Path.Combine(backupDir, row.TemplateFr3), overwrite: true);

    try
    {
        using (var report = new Report())
        {
            report.Load(fr3Path);
            report.Save(frxPath);
        }

        using (var smoke = new Report())
        {
            smoke.Load(frxPath);
        }

        if (!IsValidConvertedFrx(frxPath))
        {
            var invalidPath = frxPath + ".invalid";
            if (File.Exists(invalidPath))
                File.Delete(invalidPath);
            File.Move(frxPath, invalidPath);
            throw new InvalidOperationException("Converted FRX is invalid/empty. Moved to .invalid");
        }

        if (updateCsv)
            row.Status = "DONE";

        converted.Add($"{row.TemplateFr3} -> {row.TargetFrx}");
    }
    catch (Exception ex)
    {
        if (updateCsv)
            row.Status = "BLOCKED";

        failed.Add($"FAILED: {row.TemplateFr3} -> {row.TargetFrx} | {ex.Message}");
    }
}

if (updateCsv)
    WriteRows(csvPath, rows);

Console.WriteLine();
Console.WriteLine("=== FR3 -> FRX Conversion ===");
Console.WriteLine($"TemplateDir: {templateDir}");
Console.WriteLine($"CSV: {csvPath}");
Console.WriteLine($"BackupDir: {backupDir}");
Console.WriteLine($"Converted: {converted.Count}, Skipped: {skipped.Count}, Failed: {failed.Count}");

if (converted.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("[Converted]");
    foreach (var line in converted)
        Console.WriteLine($" - {line}");
}

if (skipped.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("[Skipped]");
    foreach (var line in skipped)
        Console.WriteLine($" - {line}");
}

if (failed.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("[Failed]");
    foreach (var line in failed)
        Console.WriteLine($" - {line}");
    return 3;
}

return 0;

static bool HasFlag(string[] args, string key)
    => args.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));

static string? GetArg(string[] args, string key)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    return null;
}

static List<CsvRow> ReadRows(string csvPath)
{
    var lines = File.ReadAllLines(csvPath, Encoding.UTF8);
    if (lines.Length == 0)
        return new List<CsvRow>();

    var header = ParseCsvLine(lines[0]);
    var index = BuildIndex(header);
    var rows = new List<CsvRow>();

    for (var i = 1; i < lines.Length; i++)
    {
        if (string.IsNullOrWhiteSpace(lines[i]))
            continue;

        var values = ParseCsvLine(lines[i]);
        rows.Add(new CsvRow
        {
            Order = ParseInt(Get(values, index, "Order"), i),
            Phase = Get(values, index, "Phase"),
            Priority = Get(values, index, "Priority"),
            TemplateFr3 = Get(values, index, "TemplateFr3"),
            TargetFrx = Get(values, index, "TargetFrx"),
            Status = Get(values, index, "Status"),
            UseCase = Get(values, index, "UseCase"),
            QaChecklist = Get(values, index, "QaChecklist"),
            Notes = Get(values, index, "Notes")
        });
    }

    return rows;
}

static void WriteRows(string csvPath, List<CsvRow> rows)
{
    var output = new List<string>
    {
        "Order,Phase,Priority,TemplateFr3,TargetFrx,Status,UseCase,QaChecklist,Notes"
    };

    foreach (var row in rows.OrderBy(r => r.Order))
    {
        output.Add(string.Join(",",
            Csv(row.Order.ToString()),
            Csv(row.Phase),
            Csv(row.Priority),
            Csv(row.TemplateFr3),
            Csv(row.TargetFrx),
            Csv(row.Status),
            Csv(row.UseCase),
            Csv(row.QaChecklist),
            Csv(row.Notes)));
    }

    File.WriteAllLines(csvPath, output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
}

static string Csv(string? value)
{
    var text = value ?? string.Empty;
    if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    return text;
}

static Dictionary<string, int> BuildIndex(IReadOnlyList<string> header)
{
    var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < header.Count; i++)
        map[header[i].Trim()] = i;
    return map;
}

static string Get(IReadOnlyList<string> values, Dictionary<string, int> index, string key)
{
    if (!index.TryGetValue(key, out var idx))
        return string.Empty;
    return idx < values.Count ? values[idx].Trim() : string.Empty;
}

static int ParseInt(string? raw, int fallback)
    => int.TryParse(raw, out var value) ? value : fallback;

static List<string> ParseCsvLine(string line)
{
    var result = new List<string>();
    var sb = new StringBuilder();
    var inQuotes = false;

    for (var i = 0; i < line.Length; i++)
    {
        var ch = line[i];
        if (ch == '"')
        {
            if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
            {
                sb.Append('"');
                i++;
            }
            else
            {
                inQuotes = !inQuotes;
            }

            continue;
        }

        if (ch == ',' && !inQuotes)
        {
            result.Add(sb.ToString());
            sb.Clear();
            continue;
        }

        sb.Append(ch);
    }

    result.Add(sb.ToString());
    return result;
}

static bool IsValidConvertedFrx(string path)
{
    var info = new FileInfo(path);
    if (!info.Exists || info.Length < 512)
        return false;

    var text = File.ReadAllText(path, Encoding.UTF8);
    if (!text.Contains("<Report", StringComparison.OrdinalIgnoreCase))
        return false;

    return text.Contains("<ReportPage", StringComparison.OrdinalIgnoreCase)
        || text.Contains("<DataBand", StringComparison.OrdinalIgnoreCase)
        || text.Contains("<TextObject", StringComparison.OrdinalIgnoreCase);
}

sealed class CsvRow
{
    public int Order { get; set; }
    public string Phase { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string TemplateFr3 { get; set; } = string.Empty;
    public string TargetFrx { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string UseCase { get; set; } = string.Empty;
    public string QaChecklist { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
