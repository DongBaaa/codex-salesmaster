using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RecycleBinTransactionLinkConsistencyTests
{
    [Fact]
    public async Task RestoreTransactionAsync_AlignsRentalLinkToLinkedInvoice()
    {
        PrepareAppRoot("georaeplan-recycle-transaction-rental-link");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceProfileId = Guid.NewGuid();
            var wrongProfileId = Guid.NewGuid();
            var invoiceRunId = Guid.NewGuid();
            var wrongRunId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();

            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(CreateProfile(invoiceProfileId, customerId, "restore invoice profile"));
            db.RentalBillingProfiles.Add(CreateProfile(wrongProfileId, customerId, "restore wrong profile"));
            db.Invoices.Add(CreateInvoice(invoiceId, customerId, invoiceProfileId, invoiceRunId));
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 23),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedInvoiceId = invoiceId,
                LinkedInvoiceNumber = "RESTORE-TX-RENTAL-LINK",
                LinkedRentalBillingProfileId = wrongProfileId,
                LinkedRentalBillingRunId = wrongRunId,
                BankReceipt = 40_000m,
                ReceiptTotal = 40_000m,
                SettlementAmount = 40_000m,
                IsDeleted = true
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var result = await local.RestoreTransactionAsync(transactionId, session);

            Assert.True(result.Success, result.Message);
            var restored = await db.Transactions.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == transactionId);
            Assert.False(restored.IsDeleted);
            Assert.Equal(invoiceId, restored.LinkedInvoiceId);
            Assert.Equal(invoiceProfileId, restored.LinkedRentalBillingProfileId);
            Assert.Equal(invoiceRunId, restored.LinkedRentalBillingRunId);

            var payment = await db.Payments.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == transactionId);
            Assert.False(payment.IsDeleted);
            Assert.Equal(invoiceId, payment.InvoiceId);
            Assert.Equal(40_000m, payment.Amount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalCustomer CreateCustomer(Guid id)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "restore rental customer",
            NameMatchKey = "RESTORERENTALCUSTOMER",
            IsDeleted = false
        };

    private static LocalInvoice CreateInvoice(Guid id, Guid customerId, Guid profileId, Guid runId)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "RESTORE-TX-RENTAL-LINK",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 23),
            TotalAmount = 100_000m,
            SupplyAmount = 100_000m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            IsLatestVersion = true,
            IsDeleted = false
        };

    private static LocalRentalBillingProfile CreateProfile(Guid id, Guid customerId, string customerName)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"restore-profile-{id:N}",
            CustomerName = customerName,
            ItemName = "restore rental item",
            BillingType = "individual",
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
            IsActive = true,
            IsDeleted = false
        };

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "recycle-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            Permissions = [AppPermissionNames.PaymentEdit]
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
