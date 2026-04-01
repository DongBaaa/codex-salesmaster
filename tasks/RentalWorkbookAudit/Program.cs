using System.Text.Json;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

var options = ParseArgs(args);
var workbookPath = options.TryGetValue("workbook", out var rawWorkbook) && !string.IsNullOrWhiteSpace(rawWorkbook)
    ? rawWorkbook
    : @"D:\거래플랜\양식\아이티월드 유즈넷 렌탈장비 관리대장.xlsb";
var outputPath = options.TryGetValue("output", out var rawOutput) && !string.IsNullOrWhiteSpace(rawOutput)
    ? rawOutput
    : Path.Combine(AppContext.BaseDirectory, "rental-workbook-audit.json");

await using var db = new LocalDbContext();
await LocalDbInitializer.InitializeAsync(db);
var rental = new RentalStateService(db);
var result = await rental.AuditAssetWorkbookAsync(workbookPath);

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
{
    WriteIndented = true
}));

Console.WriteLine(outputPath);

static Dictionary<string, string> ParseArgs(IEnumerable<string> args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var arg in args)
    {
        if (!arg.StartsWith("--", StringComparison.Ordinal))
            continue;

        var separatorIndex = arg.IndexOf('=');
        if (separatorIndex < 0)
        {
            result[arg[2..]] = "true";
            continue;
        }

        result[arg[2..separatorIndex]] = arg[(separatorIndex + 1)..];
    }

    return result;
}
