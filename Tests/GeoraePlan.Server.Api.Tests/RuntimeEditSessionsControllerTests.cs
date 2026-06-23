using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RuntimeEditSessionsControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public RuntimeEditSessionsControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task HeartbeatAsync_ReturnsOtherEditors_ForSameEntity()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "alpha",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet
        };

        await using var dbContext = CreateDbContext(currentUser);
        dbContext.ActiveEditSessions.Add(new ActiveEditSession
        {
            Id = Guid.NewGuid(),
            AppSessionId = Guid.NewGuid(),
            Username = "beta",
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            ScreenName = "거래처 등록/수정",
            EntityType = "Customer",
            EntityId = "ENTITY-1",
            EntityDisplayName = "테스트 거래처",
            MachineName = "PC-BETA",
            OpenedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            LastHeartbeatUtc = DateTime.UtcNow.AddSeconds(-10),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.HeartbeatAsync(new EditSessionHeartbeatRequest
        {
            EditSessionId = Guid.NewGuid(),
            AppSessionId = Guid.NewGuid(),
            ScreenName = "거래처 등록/수정",
            EntityType = "Customer",
            EntityId = "ENTITY-1",
            EntityDisplayName = "테스트 거래처",
            MachineName = "PC-ALPHA"
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<EditSessionHeartbeatResponse>(ok.Value);
        var other = Assert.Single(payload.OtherEditors);
        Assert.Equal("beta", other.Username);
        Assert.Equal("PC-BETA", other.MachineName);
    }

    [Fact]
    public async Task HeartbeatAsync_ReplacesSameLocalEditorSessions_AndIgnoresOtherTenants()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "alpha",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet
        };

        await using var dbContext = CreateDbContext(currentUser);
        var selfAppSessionId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        dbContext.ActiveEditSessions.AddRange(
            new ActiveEditSession
            {
                Id = Guid.NewGuid(),
                AppSessionId = selfAppSessionId,
                Username = "alpha",
                OfficeCode = OfficeCodeCatalog.Usenet,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ScreenName = "판매/구매 전표",
                EntityType = "Invoice",
                EntityId = "INVOICE-1",
                EntityDisplayName = "테스트 전표",
                MachineName = "PC-ALPHA",
                OpenedAtUtc = now.AddSeconds(-40),
                LastHeartbeatUtc = now.AddSeconds(-20),
                ExpiresAtUtc = now.AddMinutes(1)
            },
            new ActiveEditSession
            {
                Id = Guid.NewGuid(),
                AppSessionId = Guid.NewGuid(),
                Username = "alpha",
                OfficeCode = OfficeCodeCatalog.Usenet,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ScreenName = "판매/구매 전표",
                EntityType = "Invoice",
                EntityId = "INVOICE-1",
                EntityDisplayName = "테스트 전표",
                MachineName = "PC-ALPHA",
                OpenedAtUtc = now.AddSeconds(-35),
                LastHeartbeatUtc = now.AddSeconds(-15),
                ExpiresAtUtc = now.AddMinutes(1)
            },
            new ActiveEditSession
            {
                Id = Guid.NewGuid(),
                AppSessionId = Guid.NewGuid(),
                Username = "beta",
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ScreenName = "판매/구매 전표",
                EntityType = "Invoice",
                EntityId = "INVOICE-1",
                EntityDisplayName = "테스트 전표",
                MachineName = "PC-BETA",
                OpenedAtUtc = now.AddSeconds(-30),
                LastHeartbeatUtc = now.AddSeconds(-10),
                ExpiresAtUtc = now.AddMinutes(1)
            },
            new ActiveEditSession
            {
                Id = Guid.NewGuid(),
                AppSessionId = Guid.NewGuid(),
                Username = "itworld",
                OfficeCode = OfficeCodeCatalog.Itworld,
                TenantCode = TenantScopeCatalog.Itworld,
                ScreenName = "판매/구매 전표",
                EntityType = "Invoice",
                EntityId = "INVOICE-1",
                EntityDisplayName = "테스트 전표",
                MachineName = "PC-ITWORLD",
                OpenedAtUtc = now.AddSeconds(-30),
                LastHeartbeatUtc = now.AddSeconds(-10),
                ExpiresAtUtc = now.AddMinutes(1)
            });
        await dbContext.SaveChangesAsync();

        var editSessionId = Guid.NewGuid();
        var controller = CreateController(dbContext, currentUser);
        var response = await controller.HeartbeatAsync(new EditSessionHeartbeatRequest
        {
            EditSessionId = editSessionId,
            AppSessionId = selfAppSessionId,
            ScreenName = "판매/구매 전표",
            EntityType = "Invoice",
            EntityId = "INVOICE-1",
            EntityDisplayName = "테스트 전표",
            MachineName = "PC-ALPHA"
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<EditSessionHeartbeatResponse>(ok.Value);
        var other = Assert.Single(payload.OtherEditors);
        Assert.Equal("beta", other.Username);
        Assert.Equal("PC-BETA", other.MachineName);

        var sameLocalRows = await dbContext.ActiveEditSessions
            .AsNoTracking()
            .Where(entity =>
                entity.EntityType == "Invoice" &&
                entity.EntityId == "INVOICE-1" &&
                entity.TenantCode == TenantScopeCatalog.UsenetGroup &&
                entity.Username == "alpha" &&
                entity.MachineName == "PC-ALPHA")
            .ToListAsync();
        var selfRow = Assert.Single(sameLocalRows);
        Assert.Equal(editSessionId, selfRow.Id);
    }

    [Fact]
    public async Task GetActiveAsync_ExcludesSameAppSession_AndOtherTenantRows()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "alpha",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet
        };

        await using var dbContext = CreateDbContext(currentUser);
        var selfAppSessionId = Guid.NewGuid();
        dbContext.ActiveEditSessions.AddRange(
            new ActiveEditSession
            {
                Id = Guid.NewGuid(),
                AppSessionId = selfAppSessionId,
                Username = "alpha",
                OfficeCode = OfficeCodeCatalog.Usenet,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ScreenName = "거래처 등록/수정",
                EntityType = "Customer",
                EntityId = "ENTITY-1",
                EntityDisplayName = "테스트 거래처",
                MachineName = "PC-ALPHA",
                OpenedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                LastHeartbeatUtc = DateTime.UtcNow.AddSeconds(-10),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
            },
            new ActiveEditSession
            {
                Id = Guid.NewGuid(),
                AppSessionId = Guid.NewGuid(),
                Username = "beta",
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ScreenName = "거래처 등록/수정",
                EntityType = "Customer",
                EntityId = "ENTITY-1",
                EntityDisplayName = "테스트 거래처",
                MachineName = "PC-BETA",
                OpenedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                LastHeartbeatUtc = DateTime.UtcNow.AddSeconds(-10),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
            },
            new ActiveEditSession
            {
                Id = Guid.NewGuid(),
                AppSessionId = Guid.NewGuid(),
                Username = "other-tenant",
                OfficeCode = OfficeCodeCatalog.Itworld,
                TenantCode = TenantScopeCatalog.Itworld,
                ScreenName = "거래처 등록/수정",
                EntityType = "Customer",
                EntityId = "ENTITY-1",
                EntityDisplayName = "테스트 거래처",
                MachineName = "PC-ITWORLD",
                OpenedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                LastHeartbeatUtc = DateTime.UtcNow.AddSeconds(-10),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.GetActiveAsync("Customer", "ENTITY-1", selfAppSessionId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<EditSessionLookupResponse>(ok.Value);
        var other = Assert.Single(payload.ActiveEditors);
        Assert.Equal("beta", other.Username);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, other.OfficeCode);
    }

    [Fact]
    public async Task HeartbeatAsync_ForOutOfScopeInvoice_ReturnsForbidAndDoesNotCreateSession()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "alpha",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var dbContext = CreateDbContext(currentUser);
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Out of scope invoice customer",
            NameMatchKey = "OUTOFSCOPEINVOICECUSTOMER",
            TradeType = "매출"
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            InvoiceNumber = "LOCK-SCOPE-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 23)
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.HeartbeatAsync(new EditSessionHeartbeatRequest
        {
            EditSessionId = Guid.NewGuid(),
            AppSessionId = Guid.NewGuid(),
            ScreenName = "판매/구매 전표",
            EntityType = "Invoice",
            EntityId = invoiceId.ToString("D"),
            EntityDisplayName = "범위 밖 전표",
            MachineName = "PC-ALPHA"
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.False(await dbContext.ActiveEditSessions.AsNoTracking()
            .AnyAsync(session => session.EntityId == invoiceId.ToString("D")));
    }

    [Fact]
    public async Task GetActiveAsync_ForOutOfScopeItem_ReturnsForbid()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "alpha",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var dbContext = CreateDbContext(currentUser);
        var itemId = Guid.NewGuid();
        var betaAppSessionId = Guid.NewGuid();
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Out of scope item",
            NameMatchKey = "OUTOFSCOPEITEM",
            TrackingType = ItemTrackingTypes.Stock
        });
        dbContext.ActiveEditSessions.Add(new ActiveEditSession
        {
            Id = Guid.NewGuid(),
            AppSessionId = betaAppSessionId,
            Username = "beta",
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            ScreenName = "품목/재고 관리",
            EntityType = "Item",
            EntityId = itemId.ToString("D"),
            EntityDisplayName = "범위 밖 품목",
            MachineName = "PC-BETA",
            OpenedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            LastHeartbeatUtc = DateTime.UtcNow.AddSeconds(-10),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.GetActiveAsync("Item", itemId.ToString("D"), null, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
    }

    [Fact]
    public async Task ReleaseAsync_RemovesMatchingSession_AndExpiredRows()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "alpha",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet
        };

        await using var dbContext = CreateDbContext(currentUser);
        var releasedId = Guid.NewGuid();
        var releasedAppSessionId = Guid.NewGuid();
        dbContext.ActiveEditSessions.AddRange(
            new ActiveEditSession
            {
                Id = releasedId,
                AppSessionId = releasedAppSessionId,
                Username = "alpha",
                OfficeCode = OfficeCodeCatalog.Usenet,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ScreenName = "렌탈 청구관리",
                EntityType = "RentalBillingProfile",
                EntityId = "PROFILE-1",
                MachineName = "PC-ALPHA",
                OpenedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                LastHeartbeatUtc = DateTime.UtcNow.AddSeconds(-10),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
            },
            new ActiveEditSession
            {
                Id = Guid.NewGuid(),
                AppSessionId = Guid.NewGuid(),
                Username = "stale",
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ScreenName = "렌탈 청구관리",
                EntityType = "RentalBillingProfile",
                EntityId = "PROFILE-1",
                MachineName = "PC-STALE",
                OpenedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                LastHeartbeatUtc = DateTime.UtcNow.AddMinutes(-10),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5)
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.ReleaseAsync(new EditSessionReleaseRequest
        {
            EditSessionId = releasedId,
            AppSessionId = releasedAppSessionId
        }, CancellationToken.None);

        Assert.IsType<OkResult>(response);
        Assert.Empty(await dbContext.ActiveEditSessions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task HeartbeatAsync_ForExistingSessionOwnedByAnotherUser_ReturnsForbid()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "alpha",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet
        };

        await using var dbContext = CreateDbContext(currentUser);
        var stolenSessionId = Guid.NewGuid();
        var stolenAppSessionId = Guid.NewGuid();
        dbContext.ActiveEditSessions.Add(new ActiveEditSession
        {
            Id = stolenSessionId,
            AppSessionId = stolenAppSessionId,
            Username = "beta",
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            ScreenName = "Invoice",
            EntityType = "Invoice",
            EntityId = "INVOICE-LOCK",
            MachineName = "PC-BETA",
            OpenedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            LastHeartbeatUtc = DateTime.UtcNow.AddSeconds(-10),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.HeartbeatAsync(new EditSessionHeartbeatRequest
        {
            EditSessionId = stolenSessionId,
            AppSessionId = stolenAppSessionId,
            ScreenName = "Invoice",
            EntityType = "Invoice",
            EntityId = "INVOICE-LOCK",
            MachineName = "PC-ALPHA"
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        var stored = await dbContext.ActiveEditSessions.AsNoTracking().SingleAsync();
        Assert.Equal("beta", stored.Username);
        Assert.Equal("PC-BETA", stored.MachineName);
    }

    [Fact]
    public async Task ReleaseAsync_DoesNotRemoveSessionOwnedByAnotherUser()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "alpha",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet
        };

        await using var dbContext = CreateDbContext(currentUser);
        var betaSessionId = Guid.NewGuid();
        var betaAppSessionId = Guid.NewGuid();
        dbContext.ActiveEditSessions.Add(new ActiveEditSession
        {
            Id = betaSessionId,
            AppSessionId = betaAppSessionId,
            Username = "beta",
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            ScreenName = "Customer",
            EntityType = "Customer",
            EntityId = "CUSTOMER-LOCK",
            MachineName = "PC-BETA",
            OpenedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            LastHeartbeatUtc = DateTime.UtcNow.AddSeconds(-10),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.ReleaseAsync(new EditSessionReleaseRequest
        {
            EditSessionId = betaSessionId,
            AppSessionId = betaAppSessionId
        }, CancellationToken.None);

        Assert.IsType<OkResult>(response);
        var stored = await dbContext.ActiveEditSessions.AsNoTracking().SingleAsync();
        Assert.Equal("beta", stored.Username);
    }

    private static RuntimeEditSessionsController CreateController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
        => new(dbContext, currentUser, new OfficeScopeService(currentUser, dbContext));

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        var dbContext = new AppDbContext(options, currentUser, revisionClock);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = [];

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }
}
