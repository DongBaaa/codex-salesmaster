using System.Text.Json;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static void Main()
    {
        PrepareIsolatedLocalAppData();

        using var db = new LocalDbContext();
        LocalDbInitializer.InitializeAsync(db).GetAwaiter().GetResult();

        var session = BuildAdminSession();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

        var customer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "ZZZ-계약서보기-점검용",
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            Phone = "032-555-0000",
            BusinessNumber = "000-00-00000",
            Address = "테스트 주소"
        };

        var customerSave = local.UpsertCustomerAsync(customer, session).GetAwaiter().GetResult();

        var pdfPath = Path.Combine(AppPaths.TempDir, "contract-preview-smoke.pdf");
        File.WriteAllBytes(pdfPath, CreateMinimalPdfBytes());

        var contractSave = local.SaveCustomerContractAsync(
            customer.Id,
            pdfPath,
            "거래계약서",
            DateOnly.FromDateTime(DateTime.Today),
            DateOnly.FromDateTime(DateTime.Today.AddYears(1)),
            "계약서 보기 점검용",
            isPrimary: true,
            session).GetAwaiter().GetResult();

        var preferredContract = local.GetPreferredCustomerContractAsync(customer.Id, session).GetAwaiter().GetResult();
        var previewPath = preferredContract is null ? null : CustomerContractPreviewService.MaterializePreviewFile(preferredContract);

        var output = new
        {
            CustomerSave = new
            {
                customerSave.Success,
                customerSave.Message,
                customer.Id,
                customer.NameOriginal
            },
            ContractSave = new
            {
                contractSave.Success,
                contractSave.Message,
                ContractId = contractSave.EntityId
            },
            PreferredContract = preferredContract is null
                ? null
                : new
                {
                    preferredContract.Id,
                    preferredContract.CustomerId,
                    preferredContract.FileName,
                    preferredContract.ContractType,
                    preferredContract.IsPrimary,
                    FileContentLength = preferredContract.FileContent?.Length ?? 0
                },
            Preview = new
            {
                Path = previewPath,
                Exists = !string.IsNullOrWhiteSpace(previewPath) && File.Exists(previewPath),
                Size = !string.IsNullOrWhiteSpace(previewPath) && File.Exists(previewPath)
                    ? new FileInfo(previewPath).Length
                    : 0
            }
        };

        Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
    }

    private static void PrepareIsolatedLocalAppData()
    {
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "runtime", "contract-preview-smoke");
        var localAppData = Path.Combine(runtimeRoot, "LocalAppData");
        var georaePlanRoot = Path.Combine(runtimeRoot, "거래플랜");

        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", georaePlanRoot, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", "1", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", "1", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData, EnvironmentVariableTarget.Process);

        if (Directory.Exists(runtimeRoot))
            Directory.Delete(runtimeRoot, recursive: true);

        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(georaePlanRoot);
    }

    private static SessionState BuildAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "manual-audit-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = DomainConstants.OfficeUsenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            Permissions = new List<string>()
        });
        return session;
    }

    private static byte[] CreateMinimalPdfBytes()
    {
        const string pdf = """
%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Count 1 /Kids [3 0 R] >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>
endobj
4 0 obj
<< /Length 44 >>
stream
BT /F1 12 Tf 20 120 Td (Contract Preview Smoke) Tj ET
endstream
endobj
5 0 obj
<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>
endobj
xref
0 6
0000000000 65535 f 
0000000010 00000 n 
0000000063 00000 n 
0000000122 00000 n 
0000000248 00000 n 
0000000342 00000 n 
trailer
<< /Root 1 0 R /Size 6 >>
startxref
412
%%EOF
""";
        return System.Text.Encoding.ASCII.GetBytes(pdf);
    }
}
