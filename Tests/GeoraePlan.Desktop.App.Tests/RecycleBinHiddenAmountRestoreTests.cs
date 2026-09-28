using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RecycleBinHiddenAmountRestoreTests
{
    public static IEnumerable<object[]> RestoreCases()
    {
        foreach (var kind in new[] { RecycleBinEntityKind.Invoice, RecycleBinEntityKind.Payment, RecycleBinEntityKind.Transaction })
        foreach (var paymentHidden in new[] { false, true })
        foreach (var transactionHidden in new[] { false, true })
        foreach (var invoiceDeleted in new[] { false, true })
            if (invoiceDeleted || kind != RecycleBinEntityKind.Invoice)
                yield return new object[] { kind, paymentHidden, transactionHidden, invoiceDeleted };
    }

    [Theory]
    [MemberData(nameof(RestoreCases))]
    public async Task ConfirmedRestorePreservesUndisclosedMoneyAndIndependentRevisions(
        RecycleBinEntityKind kind, bool paymentHidden, bool transactionHidden, bool invoiceDeleted)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var session = CreateSession();
        var customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "복원 시험", NameMatchKey = "RESTORE",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
        var invoice = new LocalInvoice { Id = Guid.NewGuid(), CustomerId = customer.Id, InvoiceNumber = "HIDDEN-RESTORE",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 9, 24), IsLatestVersion = true, IsDeleted = invoiceDeleted,
            AmountsHidden = paymentHidden || transactionHidden, Revision = 6,
            TotalAmount = paymentHidden || transactionHidden ? 0 : 100,
            SupplyAmount = paymentHidden || transactionHidden ? 0 : 100 };
        var transaction = new LocalTransaction { Id = Guid.NewGuid(), CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, LinkedInvoiceId = invoice.Id,
            LinkedInvoiceNumber = invoice.InvoiceNumber, TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
            TransactionDate = invoice.InvoiceDate, IsDeleted = true, Revision = 8, AmountsHidden = transactionHidden,
            SettlementAmount = transactionHidden ? 0 : 100, BankReceipt = transactionHidden ? 0 : 100,
            ReceiptTotal = transactionHidden ? 0 : 100 };
        var payment = new LocalPayment { Id = transaction.Id, InvoiceId = invoice.Id,
            PaymentDate = invoice.InvoiceDate, IsDeleted = true, Revision = 7,
            AmountsHidden = paymentHidden, Amount = paymentHidden ? 0 : 100 };
        var unrelated = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "보존 중인 편집", NameMatchKey = "PENDING",
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, IsDirty = true, Revision = 11 };
        foreach (var entity in new LocalSyncEntity[] { customer, invoice, transaction, payment }) entity.IsDirty = false;
        db.AddRange(customer, invoice, transaction, payment, unrelated);
        await db.SaveChangesAsync();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var entityId = kind == RecycleBinEntityKind.Invoice ? invoice.Id : transaction.Id;

        var refreshed = false;
        var reloaded = false;
        // Exercise the actual server-confirmed UI orchestration with a refresh callback.
        // This verifies ordering; it is not a live server or network-pull test.
        var result = await EnvironmentSettingsViewModel.ApplyConfirmedServerRestoresLocallyAsync(
            [new RecycleBinEntry { Kind = kind, EntityId = entityId }], false,
            entry => local.RestoreRecycleBinEntryAsync(entry.Kind, entry.EntityId, session),
            entry => local.MarkRecycleBinServerMutationCleanAsync(entry.Kind, entry.EntityId),
            async () =>
            {
                Assert.False(await db.Transactions.IgnoreQueryFilters().AsNoTracking().Select(x => x.IsDirty).SingleAsync());
                Assert.False(await db.Payments.IgnoreQueryFilters().AsNoTracking().Select(x => x.IsDirty).SingleAsync());
                refreshed = true;
                return true;
            },
            () => { Assert.True(refreshed); reloaded = true; return Task.CompletedTask; });
        Assert.False(result.HasLocalApplyFailure, string.Join("; ", result.Failures));
        Assert.Equal(1, result.SucceededCount);
        Assert.True(result.AuthoritativeRefreshSucceeded); Assert.True(reloaded);
        db.ChangeTracker.Clear();
        var restoredInvoice = await db.Invoices.IgnoreQueryFilters().SingleAsync();
        var restoredTransaction = await db.Transactions.IgnoreQueryFilters().SingleAsync();
        var restoredPayment = await db.Payments.IgnoreQueryFilters().SingleAsync();
        Assert.False(restoredInvoice.IsDeleted, "invoice still deleted");
        Assert.False(restoredTransaction.IsDeleted, "transaction still deleted");
        Assert.False(restoredPayment.IsDeleted, "payment still deleted");
        Assert.False(restoredInvoice.IsDirty); Assert.False(restoredTransaction.IsDirty); Assert.False(restoredPayment.IsDirty);
        Assert.Equal(6, restoredInvoice.Revision); Assert.Equal(8, restoredTransaction.Revision); Assert.Equal(7, restoredPayment.Revision);
        Assert.Equal(paymentHidden, restoredPayment.AmountsHidden); Assert.Equal(transactionHidden, restoredTransaction.AmountsHidden);
        Assert.Equal(paymentHidden ? 0m : 100m, restoredPayment.Amount);
        Assert.Equal(transactionHidden ? 0m : 100m, restoredTransaction.SettlementAmount);
        Assert.Equal(transactionHidden ? 0m : 100m, restoredTransaction.ReceiptTotal);
        Assert.Equal(transactionHidden ? 0m : 100m, restoredTransaction.BankReceipt);
        Assert.Equal(paymentHidden ? (decimal?)null : 100m, LocalMappings.ToDto(restoredPayment).Amount);
        Assert.Equal(transactionHidden ? (decimal?)null : 100m, LocalMappings.ToDto(restoredTransaction).SettlementAmount);
        Assert.True((await db.Customers.SingleAsync(x => x.Id == unrelated.Id)).IsDirty);
        Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
    }

    [Theory]
    [InlineData(true, false, false, false)] // Unknown settlement does not create a zero payment.
    [InlineData(true, true, false, false)]  // Hidden transaction can restore a disclosed payment.
    [InlineData(false, true, true, false)] // Disclosed zero cannot overwrite an undisclosed payment.
    [InlineData(false, true, false, false)] // Genuine disclosed zero retains the existing deletion rule.
    [InlineData(true, true, true, true)] // Conflicting links roll back the entire restore.
    [InlineData(false, true, false, true)]
    public async Task TransactionRestoreDistinguishesUnknownFromZeroAndRejectsConflictingLinks(
        bool transactionHidden, bool paymentExists, bool paymentHidden, bool conflictingLink)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var session = CreateSession();
        var customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "복원 경계 시험", NameMatchKey = "EDGE",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, IsDirty = false, IsDeleted = true };
        var invoice = new LocalInvoice { Id = Guid.NewGuid(), CustomerId = customer.Id, InvoiceNumber = "RESTORE-EDGE",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 9, 24), IsLatestVersion = true, IsDeleted = true, IsDirty = false, Revision = 6 };
        var otherInvoice = new LocalInvoice { Id = Guid.NewGuid(), CustomerId = customer.Id, InvoiceNumber = "OTHER",
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, IsDirty = false };
        var transaction = new LocalTransaction { Id = Guid.NewGuid(), CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, LinkedInvoiceId = invoice.Id,
            TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt, TransactionDate = invoice.InvoiceDate,
            IsDeleted = true, IsDirty = false, Revision = 8, AmountsHidden = transactionHidden, SettlementAmount = 0 };
        var payment = new LocalPayment { Id = transaction.Id, InvoiceId = conflictingLink ? otherInvoice.Id : invoice.Id,
            PaymentDate = invoice.InvoiceDate, IsDeleted = true, IsDirty = false, Revision = 7,
            AmountsHidden = paymentHidden, Amount = paymentHidden ? 0 : 100, Note = "보존 메모" };
        db.AddRange(customer, invoice, otherInvoice, transaction);
        if (paymentExists) db.Add(payment);
        await db.SaveChangesAsync();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var result = await local.RestoreRecycleBinEntryAsync(RecycleBinEntityKind.Transaction, transaction.Id, session);
        Assert.Equal(!conflictingLink, result.Success);
        db.ChangeTracker.Clear();
        Assert.Equal(conflictingLink, (await db.Transactions.IgnoreQueryFilters().SingleAsync()).IsDeleted);
        Assert.Equal(conflictingLink, (await db.Invoices.IgnoreQueryFilters().SingleAsync(x => x.Id == invoice.Id)).IsDeleted);
        Assert.Equal(conflictingLink, (await db.Customers.IgnoreQueryFilters().SingleAsync()).IsDeleted);
        if (!paymentExists) Assert.Empty(await db.Payments.IgnoreQueryFilters().ToListAsync());
        else
        {
            var restored = await db.Payments.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(conflictingLink || (!transactionHidden && !paymentHidden), restored.IsDeleted);
            Assert.Equal(payment.Amount, restored.Amount); Assert.Equal(paymentHidden, restored.AmountsHidden);
            Assert.Equal(payment.InvoiceId, restored.InvoiceId); Assert.Equal(7, restored.Revision);
            Assert.Equal("보존 메모", restored.Note);
        }
        if (conflictingLink)
        {
            Assert.Empty(await db.AuditLogs.ToListAsync());
            Assert.False(await db.Transactions.IgnoreQueryFilters().AnyAsync(x => x.IsDirty));
        }
    }

    private static SessionState CreateSession()
    {
        var session = new SessionState();
        session.SetSession("isolated-test", new UserSessionDto { UserId = Guid.NewGuid(), Username = "restore-test",
            Role = DomainConstants.RoleAdmin, TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeAdmin,
            Permissions = [AppPermissionNames.PaymentEdit] });
        return session;
    }
}
