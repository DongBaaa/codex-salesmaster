using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CustomerSettlementReflectionTests
{
    [Fact]
    public async Task SaveTransactionAsync_UnlinkedGeneralPaymentAutoLinksOpenPurchaseInvoiceAndUpdatesPayable()
    {
        PrepareAppRoot("georaeplan-customer-general-payment-autolink");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, "코스모스"));
            db.Invoices.Add(CreateInvoice(invoiceId, customerId, VoucherType.Purchase, "PUR-COSMOS-001", new DateOnly(2026, 6, 3), 100_000m));
            await db.SaveChangesAsync();

            var session = CreateOfficeSession(AppPermissionNames.PaymentEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var transactionId = Guid.NewGuid();
            var result = await service.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TransactionKind = PaymentFlowConstants.TransactionKindPayment,
                TransactionDate = new DateOnly(2026, 6, 25),
                BankPayment = 40_000m,
                PaymentTotal = 40_000m,
                Note = "코스모스 미지급금 지급"
            }, session);

            Assert.True(result.Success, result.Message);

            var storedTransaction = await db.Transactions.IgnoreQueryFilters().SingleAsync(transaction => transaction.Id == transactionId);
            Assert.Equal(invoiceId, storedTransaction.LinkedInvoiceId);
            Assert.Equal(40_000m, storedTransaction.SettlementAmount);
            Assert.Equal(PaymentFlowConstants.TransactionKindPayment, storedTransaction.TransactionKind);

            var storedPayment = await db.Payments.IgnoreQueryFilters().SingleAsync(payment => payment.Id == transactionId);
            Assert.False(storedPayment.IsDeleted);
            Assert.Equal(invoiceId, storedPayment.InvoiceId);
            Assert.Equal(40_000m, storedPayment.Amount);

            var summary = await service.GetCustomerFinancialSummaryAsync(customerId, session);
            Assert.Equal(60_000m, summary.PayableAmount);

            var history = await service.GetTransactionsAsync(customerId, session);
            Assert.Contains(history, transaction => transaction.Id == transactionId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CustomerFinancialSummary_AppliesExistingUnlinkedGeneralSettlementsToReceivableAndPayable()
    {
        PrepareAppRoot("georaeplan-customer-general-settlement-summary");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, "코스모스"));
            db.Invoices.Add(CreateInvoice(Guid.NewGuid(), customerId, VoucherType.Sales, "SAL-COSMOS-001", new DateOnly(2026, 6, 1), 100_000m));
            db.Invoices.Add(CreateInvoice(Guid.NewGuid(), customerId, VoucherType.Purchase, "PUR-COSMOS-001", new DateOnly(2026, 6, 2), 70_000m));
            db.Transactions.Add(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                TransactionDate = new DateOnly(2026, 6, 10),
                BankReceipt = 25_000m,
                ReceiptTotal = 25_000m,
                SettlementAmount = 0m
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                TransactionKind = PaymentFlowConstants.TransactionKindPayment,
                TransactionDate = new DateOnly(2026, 6, 11),
                BankPayment = 15_000m,
                PaymentTotal = 15_000m,
                SettlementAmount = 0m
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeSession(AppPermissionNames.PaymentEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var summary = await service.GetCustomerFinancialSummaryAsync(customerId, session);

            Assert.Equal(75_000m, summary.ReceivableAmount);
            Assert.Equal(55_000m, summary.PayableAmount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalCustomer CreateCustomer(Guid customerId, string customerName)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = customerName,
            NameMatchKey = customerName
        };

    private static LocalInvoice CreateInvoice(Guid invoiceId, Guid customerId, VoucherType voucherType, string invoiceNumber, DateOnly invoiceDate, decimal totalAmount)
        => new()
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = voucherType,
            InvoiceNumber = invoiceNumber,
            LocalTempNumber = invoiceNumber,
            InvoiceDate = invoiceDate,
            TotalAmount = totalAmount,
            SupplyAmount = totalAmount,
            VatAmount = 0m,
            VersionGroupId = Guid.NewGuid(),
            IsLatestVersion = true,
            IsConfirmed = true
        };

    private static SessionState CreateOfficeSession(params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "usenet-user",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
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
