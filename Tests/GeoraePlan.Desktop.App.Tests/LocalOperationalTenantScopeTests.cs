using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalOperationalTenantScopeTests
{
    [Fact]
    public async Task InvoiceAndTransactionQueries_RequireCurrentTenantEvenWhenOfficeMatches()
    {
        PrepareAppRoot("georaeplan-operational-tenant-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetCustomerId = Guid.NewGuid();
            var itworldCustomerId = Guid.NewGuid();
            db.Invoices.AddRange(
                CreateInvoice(
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet,
                    customerId: usenetCustomerId,
                    invoiceNumber: "USENET-INV"),
                CreateInvoice(
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Usenet,
                    customerId: itworldCustomerId,
                    invoiceNumber: "ITWORLD-MISMATCH-INV"));
            db.Transactions.AddRange(
                CreateTransaction(
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet,
                    customerId: usenetCustomerId,
                    note: "USENET transaction"),
                CreateTransaction(
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Usenet,
                    customerId: itworldCustomerId,
                    note: "ITWORLD mismatch transaction"));
            await db.SaveChangesAsync();

            var session = CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var invoices = await service.GetInvoicesAsync(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                null,
                session);
            var transactions = await service.GetTransactionsAsync(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                null,
                session);
            var report = await service.BuildIntegrityReportAsync(session);

            var invoice = Assert.Single(invoices);
            Assert.Equal("USENET-INV", invoice.InvoiceNumber);
            var transaction = Assert.Single(transactions);
            Assert.Equal("USENET transaction", transaction.Note);

            var invoiceScopeIssue = Assert.Single(report.Issues, issue => issue.Code == "out_of_scope_invoices");
            Assert.Equal(1, invoiceScopeIssue.Count);
            var transactionScopeIssue = Assert.Single(report.Issues, issue => issue.Code == "out_of_scope_transactions");
            Assert.Equal(1, transactionScopeIssue.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalInvoice CreateInvoice(
        string tenantCode,
        string officeCode,
        Guid customerId,
        string invoiceNumber)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            CustomerId = customerId,
            InvoiceNumber = invoiceNumber,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 15),
            VersionGroupId = Guid.NewGuid(),
            IsLatestVersion = true,
            IsDeleted = false,
            IsDirty = false
        };

    private static LocalTransaction CreateTransaction(
        string tenantCode,
        string officeCode,
        Guid customerId,
        string note)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            CustomerId = customerId,
            TransactionDate = new DateOnly(2026, 6, 15),
            TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
            SettlementAmount = 1000m,
            Note = note,
            IsDeleted = false,
            IsDirty = false
        };

    private static SessionState CreateOfficeSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"{officeCode.ToLowerInvariant()}-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
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
