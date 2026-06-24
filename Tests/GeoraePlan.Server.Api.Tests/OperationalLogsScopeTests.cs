using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class OperationalLogsScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public OperationalLogsScopeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task AuditLogsController_FiltersTargetEntityByCurrentScope_ForTenantAdmin()
    {
        var currentUser = CreateTenantAdmin();
        await using var dbContext = CreateDbContext(currentUser);
        var (visibleCustomer, hiddenCustomer, _, _) = await SeedVisibleAndHiddenBusinessRowsAsync(dbContext);

        dbContext.AuditLogs.AddRange(
            new AuditLog
            {
                EntityName = nameof(Customer),
                EntityId = hiddenCustomer.Id.ToString("D"),
                Action = "Modified",
                BeforeJson = """{"NameOriginal":"hidden-before"}""",
                AfterJson = """{"NameOriginal":"hidden-after"}""",
                CreatedAtUtc = new DateTime(2026, 6, 24, 2, 0, 0, DateTimeKind.Utc)
            },
            new AuditLog
            {
                EntityName = nameof(Customer),
                EntityId = visibleCustomer.Id.ToString("D"),
                Action = "Modified",
                BeforeJson = """{"NameOriginal":"visible-before"}""",
                AfterJson = """{"NameOriginal":"visible-after"}""",
                CreatedAtUtc = new DateTime(2026, 6, 24, 1, 0, 0, DateTimeKind.Utc)
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateAuditController(dbContext, currentUser);

        var response = await controller.GetAll(nameof(Customer), take: 10, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var rows = Assert.IsType<List<AuditLogDto>>(ok.Value);

        var row = Assert.Single(rows);
        Assert.Equal(visibleCustomer.Id.ToString("D"), row.EntityId);
        Assert.DoesNotContain("hidden", row.BeforeJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden", row.AfterJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConflictLogsController_FiltersAndProtectsResolveByTargetScope_ForTenantAdmin()
    {
        var currentUser = CreateTenantAdmin();
        await using var dbContext = CreateDbContext(currentUser);
        var (_, _, visibleInvoice, hiddenInvoice) = await SeedVisibleAndHiddenBusinessRowsAsync(dbContext);

        var hiddenConflict = new ConflictLog
        {
            EntityName = nameof(Invoice),
            EntityId = hiddenInvoice.Id.ToString("D"),
            Reason = "hidden",
            ClientJson = """{"InvoiceNumber":"hidden-client"}""",
            ServerJson = """{"InvoiceNumber":"hidden-server"}""",
            Status = "Open",
            CreatedAtUtc = new DateTime(2026, 6, 24, 2, 0, 0, DateTimeKind.Utc)
        };
        var visibleConflict = new ConflictLog
        {
            EntityName = nameof(Invoice),
            EntityId = visibleInvoice.Id.ToString("D"),
            Reason = "visible",
            ClientJson = """{"InvoiceNumber":"visible-client"}""",
            ServerJson = """{"InvoiceNumber":"visible-server"}""",
            Status = "Open",
            CreatedAtUtc = new DateTime(2026, 6, 24, 1, 0, 0, DateTimeKind.Utc)
        };
        dbContext.ConflictLogs.AddRange(hiddenConflict, visibleConflict);
        await dbContext.SaveChangesAsync();

        var controller = CreateConflictController(dbContext, currentUser);

        var response = await controller.GetAll(includeResolved: false, take: 10, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var rows = Assert.IsType<List<ConflictLogDto>>(ok.Value);

        var row = Assert.Single(rows);
        Assert.Equal(visibleConflict.Id, row.Id);
        Assert.DoesNotContain("hidden", row.ServerJson, StringComparison.OrdinalIgnoreCase);

        var hiddenResolve = await controller.Resolve(hiddenConflict.Id, "must not resolve", CancellationToken.None);
        Assert.IsType<NotFoundResult>(hiddenResolve.Result);
        Assert.Equal(
            "Open",
            await dbContext.ConflictLogs
                .Where(conflict => conflict.Id == hiddenConflict.Id)
                .Select(conflict => conflict.Status)
                .SingleAsync());

        var visibleResolve = await controller.Resolve(visibleConflict.Id, "resolved in scope", CancellationToken.None);
        var visibleResolveOk = Assert.IsType<OkObjectResult>(visibleResolve.Result);
        var visibleResolveDto = Assert.IsType<ConflictLogDto>(visibleResolveOk.Value);
        Assert.Equal("Resolved", visibleResolveDto.Status);
        Assert.Equal("resolved in scope", visibleResolveDto.ResolutionNote);
    }

    private async Task<(Customer VisibleCustomer, Customer HiddenCustomer, Invoice VisibleInvoice, Invoice HiddenInvoice)>
        SeedVisibleAndHiddenBusinessRowsAsync(AppDbContext dbContext)
    {
        var visibleCustomer = new Customer
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "visible customer",
            NameMatchKey = "visible customer"
        };
        var hiddenCustomer = new Customer
        {
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "hidden customer",
            NameMatchKey = "hidden customer"
        };
        var visibleInvoice = new Invoice
        {
            Customer = visibleCustomer,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "VISIBLE-001",
            VersionGroupId = Guid.NewGuid(),
            VoucherType = VoucherType.Sales
        };
        var hiddenInvoice = new Invoice
        {
            Customer = hiddenCustomer,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            InvoiceNumber = "HIDDEN-001",
            VersionGroupId = Guid.NewGuid(),
            VoucherType = VoucherType.Sales
        };

        dbContext.Customers.AddRange(visibleCustomer, hiddenCustomer);
        dbContext.Invoices.AddRange(visibleInvoice, hiddenInvoice);
        await dbContext.SaveChangesAsync();

        dbContext.AuditLogs.RemoveRange(dbContext.AuditLogs);
        await dbContext.SaveChangesAsync();

        return (visibleCustomer, hiddenCustomer, visibleInvoice, hiddenInvoice);
    }

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        var dbContext = new AppDbContext(options, currentUser, new RevisionClock());
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static AuditLogsController CreateAuditController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
    {
        var scopeService = new OfficeScopeService(currentUser, dbContext);
        return new AuditLogsController(
            dbContext,
            new OperationalLogScopeService(dbContext, scopeService));
    }

    private static ConflictLogsController CreateConflictController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
    {
        var scopeService = new OfficeScopeService(currentUser, dbContext);
        return new ConflictLogsController(
            dbContext,
            new OperationalLogScopeService(dbContext, scopeService));
    }

    private static TestCurrentUserContext CreateTenantAdmin()
        => new()
        {
            Username = "tenant-admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsAdmin = true
        };

    public void Dispose()
        => _connection.Dispose();

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode;
    }
}
