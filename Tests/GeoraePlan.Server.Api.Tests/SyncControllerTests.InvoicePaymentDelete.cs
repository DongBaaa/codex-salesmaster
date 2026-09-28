using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Push_AfterInvoiceOnlyDeletion_RequiresExplicitCurrentWritableReceiptPair(
        bool includeTransaction, bool staleTransaction, bool outsideOffice)
    {
        var customer = new Customer { Id = Guid.NewGuid(), NameOriginal = "Detached receipt test",
            NameMatchKey = "DETACHEDRECEIPTTEST", TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
        var invoice = new Invoice { Id = Guid.NewGuid(), CustomerId = customer.Id,
            TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode, InvoiceNumber = "DETACHED-DELETE",
            InvoiceDate = new DateOnly(2026, 9, 22), VoucherType = VoucherType.Sales,
            VersionNumber = 1, IsLatestVersion = true, TotalAmount = 55_000m,
            SupplyAmount = 50_000m, VatAmount = 5_000m };
        invoice.VersionGroupId = invoice.Id;
        var payment = new Payment { Id = Guid.NewGuid(), InvoiceId = invoice.Id,
            PaymentDate = invoice.InvoiceDate, Amount = 55_000m };
        var transaction = new TransactionRecord { Id = payment.Id, CustomerId = customer.Id,
            TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode, TransactionDate = payment.PaymentDate,
            TransactionKind = "전표수금", LinkedInvoiceId = invoice.Id, LinkedInvoiceNumber = invoice.InvoiceNumber,
            BankReceipt = payment.Amount, ReceiptTotal = payment.Amount, SettlementAmount = payment.Amount };
        _dbContext.AddRange(customer, invoice, payment, transaction);
        await _dbContext.SaveChangesAsync();
        var invoiceDto = invoice.ToDto();
        invoiceDto.IsDeleted = true;
        invoiceDto.ExpectedRevision = invoice.Revision;
        invoiceDto.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
        invoiceDto.MutationId = Guid.NewGuid().ToString("N");
        invoiceDto.Payments = [];
        var first = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>((await _controller.Push(
            new SyncPushRequest { DeviceId = "detached-receipt-test", Invoices = [invoiceDto] },
            CancellationToken.None)).Result).Value);
        Assert.Equal(0, first.ConflictCount);
        _dbContext.ChangeTracker.Clear();
        payment = await _dbContext.Payments.IgnoreQueryFilters().SingleAsync(x => x.Id == payment.Id);
        transaction = await _dbContext.Transactions.IgnoreQueryFilters().SingleAsync(x => x.Id == transaction.Id);
        Assert.True(payment.IsDeleted);
        Assert.False(transaction.IsDeleted);
        Assert.Null(transaction.LinkedInvoiceId);
        var paymentDto = payment.ToDto();
        var transactionDto = transaction.ToDto();
        foreach (var dto in new SyncEntityDto[] { paymentDto, transactionDto })
        {
            dto.IsDeleted = true;
            dto.ExpectedRevision = dto.Revision;
            dto.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(2);
            dto.MutationId = Guid.NewGuid().ToString("N");
        }
        if (staleTransaction)
        {
            transaction.BankReceipt = transaction.ReceiptTotal = 60_000m;
            await _dbContext.SaveChangesAsync();
        }
        var user = new TestCurrentUserContext { Username = "receipt-other-office",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.PaymentEdit] };
        await using var scopedDb = CreateDbContext(user);
        var controller = outsideOffice ? CreateController(scopedDb, user) : _controller;
        var request = new SyncPushRequest { DeviceId = "detached-receipt-test",
            Payments = [paymentDto], Transactions = includeTransaction ? [transactionDto] : [] };
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>((await controller.Push(
            request, CancellationToken.None)).Result).Value);
        var shouldDelete = includeTransaction && !staleTransaction && !outsideOffice;
        Assert.True(shouldDelete ? result.ConflictCount == 0 : result.ConflictCount > 0,
            string.Join(" | ", result.Conflicts.Select(c => c.Reason)));
        _dbContext.ChangeTracker.Clear();
        var saved = await _dbContext.Transactions.IgnoreQueryFilters().SingleAsync(x => x.Id == transaction.Id);
        Assert.Equal(shouldDelete, saved.IsDeleted);
        Assert.Equal(staleTransaction ? 60_000m : 55_000m, saved.ReceiptTotal);
        Assert.Null(saved.LinkedInvoiceId);
        Assert.Equal("일반수금", saved.TransactionKind);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Push_InvoiceDeleteAndExplicitReceiptDelete_PreservesRequestedDeletionSemantics(
        bool deleteReceipt, int concurrentEdit)
    {
        var customer = new Customer { Id = Guid.NewGuid(), NameOriginal = "Composite deletion test",
            NameMatchKey = "COMPOSITEDELETIONTEST", TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
        var invoice = new Invoice { Id = Guid.NewGuid(), CustomerId = customer.Id,
            TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode, InvoiceNumber = "COMPOSITE-DELETE",
            InvoiceDate = new DateOnly(2026, 9, 22), VoucherType = VoucherType.Sales,
            VersionNumber = 1, IsLatestVersion = true, TotalAmount = 55_000m,
            SupplyAmount = 50_000m, VatAmount = 5_000m };
        invoice.VersionGroupId = invoice.Id;
        var payment = new Payment { Id = Guid.NewGuid(), InvoiceId = invoice.Id,
            PaymentDate = invoice.InvoiceDate, Amount = 55_000m };
        var transaction = new TransactionRecord { Id = payment.Id, CustomerId = customer.Id,
            TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode, TransactionDate = payment.PaymentDate,
            TransactionKind = "전표수금", LinkedInvoiceId = invoice.Id, LinkedInvoiceNumber = invoice.InvoiceNumber,
            BankReceipt = payment.Amount, ReceiptTotal = payment.Amount, SettlementAmount = payment.Amount };
        _dbContext.AddRange(customer, invoice, payment, transaction);
        await _dbContext.SaveChangesAsync();
        var invoiceDto = invoice.ToDto();
        var paymentDto = payment.ToDto();
        var transactionDto = transaction.ToDto();
        foreach (var dto in new SyncEntityDto[] { invoiceDto, paymentDto, transactionDto })
        {
            dto.IsDeleted = true;
            dto.ExpectedRevision = dto.Revision;
            dto.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
            dto.MutationId = Guid.NewGuid().ToString("N");
        }
        invoiceDto.Payments = [];
        if (concurrentEdit == 1)
        {
            transaction.BankReceipt = transaction.ReceiptTotal = 60_000m;
            transaction.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1);
        }
        if (concurrentEdit == 2)
        {
            payment.Note = "Other PC changed the receipt";
            payment.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1);
        }
        await _dbContext.SaveChangesAsync();
        var request = new SyncPushRequest { DeviceId = "composite-delete-test", Invoices = [invoiceDto],
            Payments = deleteReceipt ? [paymentDto] : [], Transactions = deleteReceipt ? [transactionDto] : [] };
        var response = await _controller.Push(request, CancellationToken.None);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        if (concurrentEdit == 0)
            Assert.True(result.ConflictCount == 0, string.Join(" | ", result.Conflicts.Select(c => c.Reason)));
        else
            Assert.True(result.ConflictCount >= 2, "A stale receipt pair must be rejected together.");
        _dbContext.ChangeTracker.Clear();
        var savedInvoice = await _dbContext.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice.Id);
        var savedPayment = await _dbContext.Payments.IgnoreQueryFilters().SingleAsync(p => p.Id == payment.Id);
        var savedTransaction = await _dbContext.Transactions.IgnoreQueryFilters().SingleAsync(t => t.Id == transaction.Id);
        Assert.True(savedInvoice.IsDeleted);
        Assert.True(savedPayment.IsDeleted);
        Assert.Equal(deleteReceipt && concurrentEdit == 0, savedTransaction.IsDeleted);
        Assert.Equal(concurrentEdit == 1 ? 60_000m : 55_000m, savedTransaction.ReceiptTotal);
        if (concurrentEdit == 2)
            Assert.Equal("Other PC changed the receipt", savedPayment.Note);
        if (!deleteReceipt || concurrentEdit != 0)
        {
            Assert.Null(savedTransaction.LinkedInvoiceId);
            Assert.Equal("일반수금", savedTransaction.TransactionKind);
        }
    }
}
