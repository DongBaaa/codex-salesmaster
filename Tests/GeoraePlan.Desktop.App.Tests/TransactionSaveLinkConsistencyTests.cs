using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class TransactionSaveLinkConsistencyTests
{
    [Fact]
    public async Task SaveTransactionAsync_RelinksCustomerToLinkedInvoiceCustomer()
    {
        PrepareAppRoot("georaeplan-transaction-invoice-customer-link");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var wrongCustomerId = Guid.NewGuid();
            var invoiceCustomerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            db.Customers.AddRange(
                CreateCustomer(wrongCustomerId, "Wrong payment customer"),
                CreateCustomer(invoiceCustomerId, "Invoice customer"));
            db.Invoices.Add(CreateInvoice(invoiceId, invoiceCustomerId));
            await db.SaveChangesAsync();

            var session = CreatePaymentEditorSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var transactionId = Guid.NewGuid();

            var result = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = wrongCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 23),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoiceId,
                BankReceipt = 700m,
                ReceiptTotal = 700m,
                SettlementAmount = 700m,
                Note = "invoice customer relink"
            }, session);

            Assert.True(result.Success, result.Message);
            var stored = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.Equal(invoiceCustomerId, stored.CustomerId);
            Assert.Equal(invoiceId, stored.LinkedInvoiceId);

            var payment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.Equal(invoiceId, payment.InvoiceId);
            Assert.Equal(700m, payment.Amount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveTransactionAsync_RelinksMissingCustomerToLinkedInvoiceCustomer()
    {
        PrepareAppRoot("georaeplan-transaction-missing-customer-link");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var invoiceCustomerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(invoiceCustomerId, "Invoice customer from missing transaction"));
            db.Invoices.Add(CreateInvoice(invoiceId, invoiceCustomerId));
            await db.SaveChangesAsync();

            var session = CreatePaymentEditorSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var transactionId = Guid.NewGuid();

            var result = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 23),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoiceId,
                BankReceipt = 500m,
                ReceiptTotal = 500m,
                SettlementAmount = 500m,
                Note = "missing customer relink"
            }, session);

            Assert.True(result.Success, result.Message);
            var stored = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.Equal(invoiceCustomerId, stored.CustomerId);
            Assert.Equal(invoiceId, stored.LinkedInvoiceId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveTransactionAsync_UsesLinkedInvoiceRentalProfileWhenRequestedProfileDiffers()
    {
        PrepareAppRoot("georaeplan-transaction-invoice-rental-profile-link");

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
            db.Customers.Add(CreateCustomer(customerId, "Rental invoice customer"));
            db.RentalBillingProfiles.Add(CreateProfile(invoiceProfileId, customerId, "Invoice profile"));
            db.RentalBillingProfiles.Add(CreateProfile(wrongProfileId, customerId, "Wrong profile"));
            db.Invoices.Add(CreateInvoice(invoiceId, customerId, invoiceProfileId, invoiceRunId));
            await db.SaveChangesAsync();

            var session = CreatePaymentEditorSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var transactionId = Guid.NewGuid();

            var result = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 23),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedInvoiceId = invoiceId,
                LinkedRentalBillingProfileId = wrongProfileId,
                LinkedRentalBillingRunId = wrongRunId,
                BankReceipt = 500m,
                ReceiptTotal = 500m,
                SettlementAmount = 500m,
                Note = "rental profile relink"
            }, session);

            Assert.True(result.Success, result.Message);
            var stored = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.Equal(invoiceProfileId, stored.LinkedRentalBillingProfileId);
            Assert.Equal(invoiceRunId, stored.LinkedRentalBillingRunId);
            Assert.Equal(invoiceId, stored.LinkedInvoiceId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveTransactionAsync_RejectsRentalReceiptAboveLinkedInvoiceOutstanding()
    {
        PrepareAppRoot("georaeplan-transaction-rental-over-outstanding");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, "Rental over outstanding customer"));
            db.RentalBillingProfiles.Add(CreateProfile(profileId, customerId, "Rental over outstanding profile"));
            db.Invoices.Add(CreateInvoice(invoiceId, customerId, profileId, runId));
            await db.SaveChangesAsync();

            var session = CreatePaymentEditorSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var transactionId = Guid.NewGuid();

            var result = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 23),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedInvoiceId = invoiceId,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                BankReceipt = 1500m,
                ReceiptTotal = 1500m,
                SettlementAmount = 1500m,
                Note = "rental over outstanding"
            }, session);

            Assert.False(result.Success);
            Assert.Empty(await db.Transactions.IgnoreQueryFilters().AsNoTracking().Where(current => current.Id == transactionId).ToListAsync());
            Assert.Empty(await db.Payments.IgnoreQueryFilters().AsNoTracking().Where(current => current.Id == transactionId).ToListAsync());
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
            NameMatchKey = name.Replace(" ", string.Empty).ToUpperInvariant(),
            IsDeleted = false
        };

    private static LocalInvoice CreateInvoice(
        Guid id,
        Guid customerId,
        Guid? rentalProfileId = null,
        Guid? rentalRunId = null)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 23),
            TotalAmount = 1000m,
            SupplyAmount = 1000m,
            LinkedRentalBillingProfileId = rentalProfileId,
            LinkedRentalBillingRunId = rentalRunId,
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
            ProfileKey = $"test-profile-{id:N}",
            CustomerName = customerName,
            ItemName = "Rental item",
            BillingType = "individual",
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
            IsActive = true,
            IsDeleted = false
        };

    private static SessionState CreatePaymentEditorSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "payment-link-editor",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
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
