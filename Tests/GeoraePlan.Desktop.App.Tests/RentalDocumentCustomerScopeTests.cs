using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalDocumentCustomerScopeTests
{
    [Theory]
    [InlineData("foreign-only", "ITWORLD", "ITWORLD")]
    [InlineData("foreign-only", "USENET_GROUP", "USENET")]
    [InlineData("own-and-foreign", "ITWORLD", "ITWORLD")]
    [InlineData("same-tenant-other-office", "USENET_GROUP", "USENET")]
    [InlineData("ambiguous-own-name", "ITWORLD", "ITWORLD")]
    [InlineData("explicit-link", "ITWORLD", "ITWORLD")]
    public async Task DocumentCustomer_NameFallbackRequiresUniqueAssetTenantMatch(
        string mode, string tenant, string office)
    {
        var previousRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        var root = Path.Combine(Path.GetTempPath(), $"georaeplan-document-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
        RentalAssetViewModel? vm = null;
        try
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var foreignTenant = tenant == TenantScopeCatalog.Itworld ? TenantScopeCatalog.UsenetGroup : TenantScopeCatalog.Itworld;
            var foreignOffice = foreignTenant == TenantScopeCatalog.Itworld ? OfficeCodeCatalog.Itworld : OfficeCodeCatalog.Usenet;
            var foreign = Customer(foreignTenant, foreignOffice, "FOREIGN-NUMBER");
            db.Customers.Add(foreign);
            LocalCustomer? expected = null;
            if (mode is "own-and-foreign" or "same-tenant-other-office" or "ambiguous-own-name")
            {
                expected = Customer(tenant, mode == "same-tenant-other-office" ? OfficeCodeCatalog.Yeonsu : office, "OWN-NUMBER");
                db.Customers.Add(expected);
                if (mode == "ambiguous-own-name")
                {
                    db.Customers.Add(Customer(tenant, office, "OTHER-OWN-NUMBER"));
                    expected = null;
                }
            }
            if (mode == "explicit-link") expected = foreign;
            await db.SaveChangesAsync();
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                UserId = Guid.NewGuid(), Username = "document-scope-fixture", Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            vm = new RentalAssetViewModel(new RentalStateService(db, local), local, new RentalDocumentService(), null!, session);
            var asset = new LocalRentalAsset
            {
                Id = Guid.NewGuid(), TenantCode = tenant, OfficeCode = office, ResponsibleOfficeCode = office,
                CustomerName = " SAME CUSTOMER ", CustomerId = mode == "explicit-link" ? foreign.Id : null,
                MonthlyFee = 55000, IsDirty = false
            };
            var method = typeof(RentalAssetViewModel).GetMethod("ResolveDocumentCustomerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = await (Task<LocalCustomer?>)method.Invoke(vm, [asset])!;
            Assert.Equal(expected?.Id, result?.Id);
            Assert.Equal(expected?.BusinessNumber, result?.BusinessNumber);
            Assert.Equal(tenant, asset.TenantCode);
            Assert.Equal(55000, asset.MonthlyFee);
            Assert.False(asset.IsDirty);
            Assert.False(db.ChangeTracker.HasChanges());
        }
        finally
        {
            vm?.CancelPendingBackgroundWork();
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", previousRoot);
        }
    }

    private static LocalCustomer Customer(string tenant, string office, string number) => new()
    {
        Id = Guid.NewGuid(), TenantCode = tenant, OfficeCode = office, ResponsibleOfficeCode = office,
        NameOriginal = "Same Customer", BusinessNumber = number, IsDeleted = false, IsDirty = false
    };
}
