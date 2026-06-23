using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingProfileCustomerScopeTests
{
    [Fact]
    public async Task SaveBillingProfileAsync_AutoLinksCustomerWhenTenantAndOfficeMatch()
    {
        PrepareAppRoot("georaeplan-billing-profile-customer-positive");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
            await db.SaveChangesAsync();

            var profileId = Guid.NewGuid();
            var profile = CreateProfile(profileId, customerId: null, OfficeCodeCatalog.Usenet);
            var session = CreateAdminSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);

            var result = await new RentalStateService(db).SaveBillingProfileAsync(profile, session);

            Assert.True(result.Success, result.Message);
            var stored = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
            Assert.Equal(customerId, stored.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Usenet, stored.ResponsibleOfficeCode);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, stored.TenantCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveBillingProfileAsync_DoesNotAutoLinkSameTenantCustomerFromDifferentOffice()
    {
        PrepareAppRoot("georaeplan-billing-profile-customer-office-mismatch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var yeonsuCustomerId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(yeonsuCustomerId, OfficeCodeCatalog.Yeonsu, TenantScopeCatalog.UsenetGroup));
            await db.SaveChangesAsync();

            var profileId = Guid.NewGuid();
            var profile = CreateProfile(profileId, customerId: null, OfficeCodeCatalog.Usenet);
            var session = CreateAdminSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);

            var result = await new RentalStateService(db).SaveBillingProfileAsync(profile, session);

            Assert.True(result.Success, result.Message);
            var stored = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
            Assert.Null(stored.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Usenet, stored.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, stored.OfficeCode);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, stored.TenantCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveBillingProfileAsync_RejectsExistingCustomerLinkWhenOfficeDiffers()
    {
        PrepareAppRoot("georaeplan-billing-profile-existing-customer-office-mismatch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var yeonsuCustomerId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(yeonsuCustomerId, OfficeCodeCatalog.Yeonsu, TenantScopeCatalog.UsenetGroup));
            await db.SaveChangesAsync();

            var profile = CreateProfile(Guid.NewGuid(), yeonsuCustomerId, OfficeCodeCatalog.Usenet);
            var session = CreateAdminSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);

            var result = await new RentalStateService(db).SaveBillingProfileAsync(profile, session);

            Assert.False(result.Success);
            Assert.Contains("\uBC94\uC704", result.Message, StringComparison.Ordinal);
            Assert.Empty(await db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task StartBillingAsync_DoesNotCreateInvoiceForSameTenantCustomerFromDifferentOffice()
    {
        PrepareAppRoot("georaeplan-billing-start-customer-office-mismatch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var yeonsuCustomerId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(yeonsuCustomerId, OfficeCodeCatalog.Yeonsu, TenantScopeCatalog.UsenetGroup));

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateAsset(assetId, profileId, OfficeCodeCatalog.Usenet));
            db.RentalBillingProfiles.Add(CreateProfile(profileId, customerId: null, OfficeCodeCatalog.Usenet, assetId));
            await db.SaveChangesAsync();

            var session = CreateAdminSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);

            var result = await rental.StartBillingAsync(profileId, new DateOnly(2026, 6, 25), session);

            Assert.False(result.Success);
            Assert.Contains("\uAC70\uB798\uCC98", result.Message, StringComparison.Ordinal);
            Assert.Empty(await db.Invoices.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalCustomer CreateCustomer(Guid id, string officeCode, string tenantCode)
        => new()
        {
            Id = id,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            NameOriginal = "Shared Scope Customer",
            NameMatchKey = "SHAREDSCOPECUSTOMER",
            BusinessNumber = "555-55-55555",
            IsDeleted = false
        };

    private static LocalRentalBillingProfile CreateProfile(
        Guid id,
        Guid? customerId,
        string officeCode,
        Guid? assetId = null)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = officeCode,
            CustomerId = customerId,
            CustomerName = "Shared Scope Customer",
            BusinessNumber = "555-55-55555",
            InstallSiteName = "Scope Test Site",
            ItemName = "Scope Test Rental",
            BillingType = "bundle",
            BillingAdvanceMode = "postpaid",
            BillingMethod = "cash",
            BillingDay = 25,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = 1,
            BillingAnchorMonth = 1,
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
            MonthlyAmount = 50_000m,
            BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "Scope Test Rental",
                    Quantity = 1m,
                    UnitPrice = 50_000m,
                    Amount = 50_000m,
                    IncludedAssetIds = assetId.HasValue ? [assetId.Value] : []
                }
            }),
            IsActive = true,
            IsDeleted = false
        };

    private static LocalRentalAsset CreateAsset(Guid id, Guid profileId, string officeCode)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = officeCode,
            BillingProfileId = profileId,
            CustomerName = "Shared Scope Customer",
            CurrentCustomerName = "Shared Scope Customer",
            InstallLocation = "Scope Test Site",
            InstallSiteName = "Scope Test Site",
            ItemName = "Scope Test Rental",
            ManagementNumber = $"SCOPE-{Guid.NewGuid():N}",
            ManagementId = $"SCOPE-ID-{Guid.NewGuid():N}",
            MachineNumber = $"SCOPE-SN-{Guid.NewGuid():N}",
            AssetStatus = "\uC784\uB300\uC9C4\uD589\uC911",
            BillingEligibilityStatus = "\uCCAD\uAD6C\uB300\uC0C1",
            MonthlyFee = 50_000m,
            IsDeleted = false
        };

    private static SessionState CreateAdminSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "billing-scope-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            Permissions = []
        });
        return session;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
