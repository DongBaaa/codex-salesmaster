using System.Text.Json;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

var options = ParseArgs(args);
var workbookPath = options.TryGetValue("workbook", out var rawWorkbook) && !string.IsNullOrWhiteSpace(rawWorkbook)
    ? rawWorkbook
    : @"D:\거래플랜\양식\아이티월드 유즈넷 렌탈장비 관리대장.xlsb";
var outputPath = options.TryGetValue("output", out var rawOutput) && !string.IsNullOrWhiteSpace(rawOutput)
    ? rawOutput
    : Path.Combine(AppContext.BaseDirectory, "rental-workbook-rebuild.json");

await using var db = new LocalDbContext();
await LocalDbInitializer.InitializeAsync(db);
var rental = new RentalStateService(db);
var session = new SessionState();
session.SetOfflineSession(new UserSessionDto
{
    UserId = Guid.NewGuid(),
    Username = "codex-rebuild",
    Role = DomainConstants.RoleAdmin,
    OfficeCode = DomainConstants.OfficeUsenet,
    ScopeType = TenantScopeCatalog.ScopeAdmin,
    TenantCode = TenantScopeCatalog.UsenetGroup
});

var beforeAudit = await rental.AuditAssetWorkbookAsync(workbookPath);
var rebuildResult = await rental.RebuildAssetsFromWorkbookAsync(workbookPath, session);
var afterAudit = await rental.AuditAssetWorkbookAsync(workbookPath);

var result = new
{
    workbookPath,
    rebuildResult,
    beforeAudit = new
    {
        beforeAudit.ProcessedRowCount,
        beforeAudit.ExactMatchCount,
        beforeAudit.UpdateSafeCount,
        beforeAudit.CreateNewCount,
        beforeAudit.AmbiguousCount,
        beforeAudit.MissingInWorkbookCount,
        beforeAudit.UnresolvedCustomerCount
    },
    afterAudit = new
    {
        afterAudit.ProcessedRowCount,
        afterAudit.ExactMatchCount,
        afterAudit.UpdateSafeCount,
        afterAudit.CreateNewCount,
        afterAudit.AmbiguousCount,
        afterAudit.MissingInWorkbookCount,
        afterAudit.UnresolvedCustomerCount
    }
};

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
