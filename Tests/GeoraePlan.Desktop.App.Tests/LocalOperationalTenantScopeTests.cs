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
            var usenetInvoice = CreateInvoice(
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                customerId: usenetCustomerId,
                invoiceNumber: "USENET-INV");
            var itworldInvoice = CreateInvoice(
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Usenet,
                customerId: itworldCustomerId,
                invoiceNumber: "ITWORLD-MISMATCH-INV");
            usenetInvoice.IsDirty = true;
            itworldInvoice.IsDirty = true;
            var usenetTransaction = CreateTransaction(
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                customerId: usenetCustomerId,
                note: "USENET transaction");
            var itworldTransaction = CreateTransaction(
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Usenet,
                customerId: itworldCustomerId,
                note: "ITWORLD mismatch transaction");
            usenetTransaction.IsDirty = true;
            itworldTransaction.IsDirty = true;
            var usenetPayment = new LocalPayment
            {
                Id = Guid.NewGuid(),
                InvoiceId = usenetInvoice.Id,
                PaymentDate = new DateOnly(2026, 6, 15),
                Amount = 1000m,
                IsDirty = true
            };
            var itworldPayment = new LocalPayment
            {
                Id = Guid.NewGuid(),
                InvoiceId = itworldInvoice.Id,
                PaymentDate = new DateOnly(2026, 6, 15),
                Amount = 1000m,
                IsDirty = true
            };
            var usenetAttachment = new LocalTransactionAttachment
            {
                Id = Guid.NewGuid(),
                TransactionId = usenetTransaction.Id,
                FileName = "usenet.pdf",
                StoredFileName = "usenet.pdf",
                StoredPath = "test/usenet.pdf",
                MimeType = "application/pdf",
                FileSize = 1,
                IsDirty = true
            };
            var itworldAttachment = new LocalTransactionAttachment
            {
                Id = Guid.NewGuid(),
                TransactionId = itworldTransaction.Id,
                FileName = "itworld.pdf",
                StoredFileName = "itworld.pdf",
                StoredPath = "test/itworld.pdf",
                MimeType = "application/pdf",
                FileSize = 1,
                IsDirty = true
            };
            db.Invoices.AddRange(usenetInvoice, itworldInvoice);
            db.Transactions.AddRange(usenetTransaction, itworldTransaction);
            db.Payments.AddRange(usenetPayment, itworldPayment);
            db.TransactionAttachments.AddRange(usenetAttachment, itworldAttachment);
            await db.SaveChangesAsync();

            var session = CreateOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.InvoiceEdit,
                AppPermissionNames.PaymentEdit);
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

            var hiddenInvoice = await service.GetInvoiceAsync(itworldInvoice.Id, session);
            var hiddenLatestInvoice = await service.GetLatestInvoiceVersionAsync(itworldInvoice.Id, session);
            var hiddenInvoiceVersions = await service.GetInvoiceVersionsAsync(itworldInvoice.Id, session);
            var hiddenSettlement = await service.GetInvoiceSettlementSummaryAsync(itworldInvoice.Id, session);
            var hiddenAttachments = await service.GetTransactionAttachmentsAsync(itworldTransaction.Id, session);
            var dirtyInvoices = await service.GetDirtyInvoicesForSyncAsync(session);
            var dirtyTransactions = await service.GetDirtyTransactionsForSyncAsync(session);
            var dirtyPayments = await service.GetDirtyPaymentsForSyncAsync(session);
            var dirtyAttachments = await service.GetDirtyTransactionAttachmentsForSyncAsync(session);

            Assert.Null(hiddenInvoice);
            Assert.Null(hiddenLatestInvoice);
            Assert.Empty(hiddenInvoiceVersions);
            Assert.Equal(0m, hiddenSettlement.InvoiceTotal);
            Assert.Empty(hiddenAttachments);
            Assert.Contains(dirtyInvoices, invoice => invoice.Id == usenetInvoice.Id);
            Assert.DoesNotContain(dirtyInvoices, invoice => invoice.Id == itworldInvoice.Id);
            Assert.Contains(dirtyTransactions, current => current.Id == usenetTransaction.Id);
            Assert.DoesNotContain(dirtyTransactions, current => current.Id == itworldTransaction.Id);
            Assert.Contains(dirtyPayments, payment => payment.Id == usenetPayment.Id);
            Assert.DoesNotContain(dirtyPayments, payment => payment.Id == itworldPayment.Id);
            Assert.Contains(dirtyAttachments, attachment => attachment.Id == usenetAttachment.Id);
            Assert.DoesNotContain(dirtyAttachments, attachment => attachment.Id == itworldAttachment.Id);

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

    [Fact]
    public async Task DeliveryViewAllInvoiceQueries_StayWithinCurrentTenant()
    {
        PrepareAppRoot("georaeplan-delivery-tenant-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetInvoice = CreateInvoice(
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                customerId: Guid.NewGuid(),
                invoiceNumber: "USENET-DELIVERY-INV");
            var yeonsuInvoice = CreateInvoice(
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Yeonsu,
                customerId: Guid.NewGuid(),
                invoiceNumber: "YEONSU-DELIVERY-INV");
            var itworldInvoice = CreateInvoice(
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Itworld,
                customerId: Guid.NewGuid(),
                invoiceNumber: "ITWORLD-DELIVERY-INV");
            foreach (var invoice in new[] { usenetInvoice, yeonsuInvoice, itworldInvoice })
                invoice.IsConfirmed = true;
            db.Invoices.AddRange(usenetInvoice, yeonsuInvoice, itworldInvoice);
            await db.SaveChangesAsync();

            var session = CreateOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.DeliveryViewAll);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var invoices = await service.GetYeonsuDeliveryInvoicesAsync(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                null,
                null,
                null,
                session);

            Assert.Contains(invoices, invoice => invoice.InvoiceNumber == "USENET-DELIVERY-INV");
            Assert.Contains(invoices, invoice => invoice.InvoiceNumber == "YEONSU-DELIVERY-INV");
            Assert.DoesNotContain(invoices, invoice => invoice.InvoiceNumber == "ITWORLD-DELIVERY-INV");
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

    private static SessionState CreateOfficeSession(string tenantCode, string officeCode, params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"{officeCode.ToLowerInvariant()}-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
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
