using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AuditLogLookupServiceTests
{
    [Fact]
    public async Task LookupAuditLogs_DeniesAccountWithoutAdminOrBackupRestorePermission()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalCustomer),
            EntityId = Guid.NewGuid().ToString("D"),
            Action = "Update",
            Username = "operator",
            OfficeCode = OfficeCodeCatalog.Usenet,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, CreateOfficeSession());
        var result = await service.LookupAuditLogsAsync(new LocalStateService.AuditLogLookupRequest());

        Assert.False(result.IsAuthorized);
        Assert.Empty(result.Rows);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public async Task LookupAuditLogs_FiltersAndMasksJsonWithoutLeakingTenantOrOfficeScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);

        var usenetCustomer = CreateCustomer(
            "유즈넷 감사 거래처",
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            "101-11-11111");
        var yeonsuCustomer = CreateCustomer(
            "연수 비공개 거래처",
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            "202-22-22222");
        var itworldCustomer = CreateCustomer(
            "타 테넌트 비공개 거래처",
            TenantScopeCatalog.Itworld,
            OfficeCodeCatalog.Itworld,
            "303-33-33333");
        var usenetInvoice = CreateInvoice(usenetCustomer, "US-100");
        var yeonsuInvoice = CreateInvoice(yeonsuCustomer, "YS-200");
        var itworldInvoice = CreateInvoice(itworldCustomer, "IT-300");
        var now = DateTime.UtcNow;

        db.Customers.AddRange(usenetCustomer, yeonsuCustomer, itworldCustomer);
        db.Invoices.AddRange(usenetInvoice, yeonsuInvoice, itworldInvoice);
        db.AuditLogs.AddRange(
            new LocalAuditLog
            {
                EntityName = nameof(LocalInvoice),
                EntityId = usenetInvoice.Id.ToString("D"),
                Action = "UpdateMetadata",
                Username = "operator-usenet",
                Role = DomainConstants.RoleUser,
                OfficeCode = OfficeCodeCatalog.Usenet,
                BeforeJson = "{\"password\":\"old-password\",\"memo\":\"before\"}",
                AfterJson = "{\"invoiceNumber\":\"US-100\",\"password\":\"plain-password\",\"accessToken\":\"token-value\",\"clientSecret\":\"secret-value\",\"apiKey\":\"api-key-value\",\"connection\":\"Server=db;Password=embedded-password;User=app\"}",
                CreatedAtUtc = now
            },
            new LocalAuditLog
            {
                EntityName = nameof(LocalInvoice),
                EntityId = yeonsuInvoice.Id.ToString("D"),
                Action = "UpdateMetadata",
                Username = "operator-yeonsu",
                Role = DomainConstants.RoleUser,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                AfterJson = "{\"invoiceNumber\":\"YS-200\"}",
                CreatedAtUtc = now.AddSeconds(-1)
            },
            new LocalAuditLog
            {
                EntityName = nameof(LocalInvoice),
                EntityId = itworldInvoice.Id.ToString("D"),
                Action = "UpdateMetadata",
                Username = "operator-itworld",
                Role = DomainConstants.RoleUser,
                OfficeCode = OfficeCodeCatalog.Itworld,
                AfterJson = "{\"invoiceNumber\":\"IT-300\"}",
                CreatedAtUtc = now.AddSeconds(-2)
            });
        await db.SaveChangesAsync();

        var session = CreateOfficeSession(AppPermissionNames.DataBackupRestore);
        var service = CreateService(db, session);

        var scopedResult = await service.LookupAuditLogsAsync(new LocalStateService.AuditLogLookupRequest());
        var filteredResult = await service.LookupAuditLogsAsync(new LocalStateService.AuditLogLookupRequest
        {
            FromDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)),
            ToDate = DateOnly.FromDateTime(DateTime.Today),
            Username = "operator",
            EntityName = nameof(LocalInvoice),
            Action = "Update",
            SearchText = "유즈넷 US-100"
        });

        Assert.True(scopedResult.IsAuthorized);
        Assert.Single(scopedResult.Rows);
        Assert.Equal(usenetInvoice.Id.ToString("D"), scopedResult.Rows[0].EntityId);

        var row = Assert.Single(filteredResult.Rows);
        Assert.Equal("operator-usenet", row.Username);
        Assert.Contains("유즈넷 감사 거래처", row.TargetText, StringComparison.Ordinal);
        Assert.Contains("US-100", row.TargetText, StringComparison.Ordinal);
        Assert.Contains("\"password\": \"***\"", row.BeforeJson, StringComparison.Ordinal);
        Assert.Contains("\"password\": \"***\"", row.AfterJson, StringComparison.Ordinal);
        Assert.Contains("\"accessToken\": \"***\"", row.AfterJson, StringComparison.Ordinal);
        Assert.Contains("\"clientSecret\": \"***\"", row.AfterJson, StringComparison.Ordinal);
        Assert.Contains("\"apiKey\": \"***\"", row.AfterJson, StringComparison.Ordinal);
        Assert.DoesNotContain("old-password", row.BeforeJson, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-password", row.AfterJson, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", row.AfterJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", row.AfterJson, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-value", row.AfterJson, StringComparison.Ordinal);
        Assert.DoesNotContain("embedded-password", row.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupAuditLogs_ReturnsNewestOneThousandAndMarksTruncation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);

        var customer = CreateCustomer(
            "제한 확인 거래처",
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            "404-44-44444");
        db.Customers.Add(customer);

        var now = DateTime.UtcNow;
        var logIds = new List<Guid>();
        for (var index = 0; index < 1001; index++)
        {
            var logId = Guid.NewGuid();
            logIds.Add(logId);
            db.AuditLogs.Add(new LocalAuditLog
            {
                Id = logId,
                EntityName = nameof(LocalCustomer),
                EntityId = customer.Id.ToString("D"),
                Action = "Update",
                Username = "auditor",
                Role = DomainConstants.RoleUser,
                OfficeCode = OfficeCodeCatalog.Usenet,
                AfterJson = $"{{\"sequence\":{index}}}",
                CreatedAtUtc = now.AddSeconds(-index)
            });
        }

        await db.SaveChangesAsync();

        var service = CreateService(db, CreateOfficeSession(AppPermissionNames.DataBackupRestore));
        var result = await service.LookupAuditLogsAsync(new LocalStateService.AuditLogLookupRequest());

        Assert.True(result.IsAuthorized);
        Assert.True(result.IsTruncated);
        Assert.False(result.IsScanLimitReached);
        Assert.Equal(LocalStateService.AuditLogLookupLimit, result.Rows.Count);
        Assert.Equal(logIds[0], result.Rows[0].Id);
        Assert.Equal(logIds[999], result.Rows[^1].Id);
        Assert.DoesNotContain(result.Rows, row => row.Id == logIds[1000]);
    }

    [Fact]
    public async Task LookupAuditLogs_StopsAtScanLimitWithoutWeakeningScopeFilter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);

        var visibleCustomer = CreateCustomer(
            "스캔 상한 뒤의 범위 내 거래처",
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            "505-55-55555");
        var hiddenCustomer = CreateCustomer(
            "스캔 상한 범위 밖 거래처",
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            "606-66-66666");
        var hiddenInvoice = CreateInvoice(hiddenCustomer, "YS-HIDDEN");
        db.Customers.AddRange(visibleCustomer, hiddenCustomer);
        db.Invoices.Add(hiddenInvoice);

        var now = DateTime.UtcNow;
        var logs = new List<LocalAuditLog>(LocalStateService.AuditLogLookupScanLimit + 1);
        for (var index = 0; index < LocalStateService.AuditLogLookupScanLimit; index++)
        {
            logs.Add(new LocalAuditLog
            {
                EntityName = nameof(LocalInvoice),
                EntityId = hiddenInvoice.Id.ToString("D"),
                Action = "Update",
                Username = "hidden-operator",
                Role = DomainConstants.RoleUser,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                CreatedAtUtc = now.AddMilliseconds(-index)
            });
        }

        logs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalCustomer),
            EntityId = visibleCustomer.Id.ToString("D"),
            Action = "Update",
            Username = "visible-operator",
            Role = DomainConstants.RoleUser,
            OfficeCode = OfficeCodeCatalog.Usenet,
            CreatedAtUtc = now.AddDays(-1)
        });
        db.AuditLogs.AddRange(logs);
        await db.SaveChangesAsync();

        var service = CreateService(db, CreateOfficeSession(AppPermissionNames.DataBackupRestore));
        var result = await service.LookupAuditLogsAsync(new LocalStateService.AuditLogLookupRequest());

        Assert.True(result.IsAuthorized);
        Assert.True(result.IsScanLimitReached);
        Assert.False(result.IsTruncated);
        Assert.Equal(LocalStateService.AuditLogLookupScanLimit, result.ScanLimit);
        Assert.Equal(LocalStateService.AuditLogLookupScanLimit, result.ScannedCount);
        Assert.Empty(result.Rows);
    }

    private static async Task<LocalDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static LocalStateService CreateService(LocalDbContext db, SessionState session)
        => new(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

    private static SessionState CreateOfficeSession(params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "auditor",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static LocalCustomer CreateCustomer(
        string name,
        string tenantCode,
        string officeCode,
        string businessNumber)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            NameOriginal = name,
            NameMatchKey = name,
            BusinessNumber = businessNumber,
            IsDirty = false
        };

    private static LocalInvoice CreateInvoice(LocalCustomer customer, string invoiceNumber)
        => new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            InvoiceNumber = invoiceNumber,
            LocalTempNumber = invoiceNumber,
            InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
            VersionGroupId = Guid.NewGuid(),
            VersionNumber = 1,
            IsLatestVersion = true,
            IsConfirmed = true,
            IsDirty = false
        };
}
