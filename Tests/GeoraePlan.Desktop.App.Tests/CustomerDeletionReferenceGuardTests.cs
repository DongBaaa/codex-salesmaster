using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CustomerDeletionReferenceGuardTests
{
    [Fact]
    public async Task DeleteCustomerAsync_BlocksActiveBusinessReferencesAcrossCustomerScreens()
    {
        PrepareAppRoot("georaeplan-customer-delete-reference-block");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customer = CreateCustomer(Guid.NewGuid(), "삭제 차단 거래처");
            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();

            db.Customers.Add(customer);
            db.Invoices.Add(new LocalInvoice
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                TenantCode = customer.TenantCode,
                OfficeCode = customer.OfficeCode,
                ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                InvoiceNumber = "LOCAL-CUSTOMER-DELETE-BLOCK-INVOICE",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 19),
                IsDirty = false
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                TenantCode = customer.TenantCode,
                OfficeCode = customer.OfficeCode,
                ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                TransactionDate = new DateOnly(2026, 6, 19),
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                IsDirty = false
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = customer.TenantCode,
                OfficeCode = customer.OfficeCode,
                ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customer.Id,
                CustomerName = customer.NameOriginal,
                ProfileKey = $"profile-{profileId:N}",
                IsDirty = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = customer.TenantCode,
                OfficeCode = customer.OfficeCode,
                ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = $"asset-{assetId:N}",
                CustomerId = customer.Id,
                CustomerName = customer.NameOriginal,
                CurrentCustomerName = customer.NameOriginal,
                ManagementNumber = "LOCAL-ASSET-001",
                IsDirty = false
            });
            db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                CustomerId = customer.Id,
                TenantCode = customer.TenantCode,
                ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                CustomerName = customer.NameOriginal,
                ManagementNumber = "LOCAL-ASSET-001",
                IsCurrent = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var result = await local.DeleteCustomerAsync(customer.Id, session);

            Assert.False(result.Success);
            Assert.Contains("전표 1건", result.Message, StringComparison.Ordinal);
            Assert.Contains("거래내역 1건", result.Message, StringComparison.Ordinal);
            Assert.Contains("렌탈 청구 1건", result.Message, StringComparison.Ordinal);
            Assert.Contains("렌탈 자산 1건", result.Message, StringComparison.Ordinal);
            Assert.Contains("현재 설치이력 1건", result.Message, StringComparison.Ordinal);
            Assert.False(await db.Customers.IgnoreQueryFilters()
                .Where(current => current.Id == customer.Id)
                .Select(current => current.IsDeleted)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MergeDeletedCustomerIntoAsync_MovesRentalAssignmentHistoryReferences()
    {
        PrepareAppRoot("georaeplan-customer-merge-assignment-history");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var source = CreateCustomer(Guid.NewGuid(), "삭제 거래처");
            source.IsDeleted = true;
            source.IsDirty = false;
            var target = CreateCustomer(Guid.NewGuid(), "대상 거래처");
            var historyId = Guid.NewGuid();

            db.Customers.AddRange(source, target);
            db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
            {
                Id = historyId,
                AssetId = Guid.NewGuid(),
                CustomerId = source.Id,
                TenantCode = source.TenantCode,
                ResponsibleOfficeCode = source.ResponsibleOfficeCode,
                CustomerName = source.NameOriginal,
                ManagementNumber = "MERGE-HISTORY-001",
                IsCurrent = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var result = await local.MergeDeletedCustomerIntoAsync(source.Id, target.Id, session);

            Assert.True(result.Success, result.Message);
            Assert.Contains("설치이력 1건", result.Message, StringComparison.Ordinal);
            var history = await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().SingleAsync(current => current.Id == historyId);
            Assert.Equal(target.Id, history.CustomerId);
            Assert.Equal(target.NameOriginal, history.CustomerName);
            Assert.True(history.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalCustomer CreateCustomer(Guid id, string name)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = name,
            TradeType = CustomerTradeTypes.Sales,
            IsDirty = false
        };

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
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
