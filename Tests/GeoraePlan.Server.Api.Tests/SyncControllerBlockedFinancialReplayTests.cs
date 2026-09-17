using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    [Theory]
    [InlineData("exact")]
    [InlineData("changed-payload")]
    [InlineData("blank-hash")]
    [InlineData("other-device")]
    [InlineData("novel-mutation")]
    [InlineData("wrong-receipt-revision")]
    [InlineData("wrong-receipt-entity")]
    [InlineData("missing-dependent")]
    [InlineData("current-office-denied")]
    public async Task Push_BlockedProfileGeneration_FinancialReceiptReplayPreservesState(string mode)
    {
        const string device = "blocked-financial-replay-device";
        var customer = new Customer
        {
            Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Financial replay fixture", NameMatchKey = "FINANCIALREPLAY"
        };
        var profile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(), TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode, ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            CustomerId = customer.Id, ProfileKey = "FINANCIAL-REPLAY-" + Guid.NewGuid().ToString("N"),
            CustomerName = customer.NameOriginal, ItemName = "Service", IsActive = true,
            MonthlyAmount = 110_000m
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, Customer = customer,
            TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            LinkedRentalBillingProfileId = profile.Id, InvoiceNumber = "REPLAY-001",
            TaxInvoiceNumber = "TAX-REPLAY-001", TaxInvoiceIssued = true, TotalAmount = 110_000m,
            SupplyAmount = 100_000m, VatAmount = 10_000m, IsLatestVersion = true
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(), InvoiceId = invoice.Id, Invoice = invoice,
            Amount = 10_000m, Note = "Recorded payment"
        };
        var transaction = new TransactionRecord
        {
            Id = payment.Id, CustomerId = customer.Id,
            TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            LinkedInvoiceId = invoice.Id, LinkedRentalBillingProfileId = profile.Id,
            TransactionKind = "수금", CashReceipt = 10_000m, ReceiptTotal = 10_000m,
            Note = "Recorded transaction"
        };
        _dbContext.AddRange(customer, profile, invoice, payment, transaction);
        await _dbContext.SaveChangesAsync();
        var profileDto = profile.ToDto();
        // A prior successful write advanced the server revision. The exact old
        // parent command cannot prove the current generation from revision alone.
        profileDto.Revision = profile.Revision - 1;
        profileDto.ExpectedRevision = profileDto.Revision;
        profileDto.UpdatedAtUtc = profile.UpdatedAtUtc.AddMinutes(-1);
        var invoiceDto = invoice.ToDto();
        invoiceDto.InvoiceNumber = string.Empty;
        invoiceDto.TaxInvoiceNumber = string.Empty;
        var paymentDto = payment.ToDto();
        var transactionDto = transaction.ToDto();
        SyncEntityDto[] dependents = [invoiceDto, paymentDto, transactionDto];
        foreach (var dto in dependents)
        {
            dto.ExpectedRevision = 0;
            dto.Revision = 0;
        }
        (SyncEntityDto Dto, string Name)[] mutations =
        [
            (profileDto, nameof(RentalBillingProfile)), (invoiceDto, nameof(Invoice)),
            (paymentDto, nameof(Payment)), (transactionDto, nameof(TransactionRecord))
        ];
        foreach (var (dto, name) in mutations)
        {
            dto.MutationId = "financial-replay-" + Guid.NewGuid().ToString("N");
            dto.MutationCreatedAtUtc = dto.UpdatedAtUtc;
            _dbContext.ProcessedSyncMutations.Add(new ProcessedSyncMutation
            {
                MutationId = dto.MutationId, DeviceId = name != nameof(RentalBillingProfile) && mode == "other-device" ? "other-device" : device,
                EntityName = name,
                EntityId = name != nameof(RentalBillingProfile) && mode == "wrong-receipt-entity" ? Guid.NewGuid().ToString("D") : dto.Id.ToString("D"),
                ExpectedRevision = name != nameof(RentalBillingProfile) && mode == "wrong-receipt-revision" ? dto.ExpectedRevision + 1 : dto.ExpectedRevision,
                PayloadHash = name != nameof(RentalBillingProfile) && mode == "blank-hash" ? string.Empty : SyncMutationPayloadHasher.Compute(dto)
            });
        }
        await _dbContext.SaveChangesAsync();
        if (mode == "changed-payload")
        {
            invoiceDto.Memo = "Changed invoice";
            paymentDto.Note = "Changed payment";
            transactionDto.Note = "Changed transaction";
        }
        if (mode == "novel-mutation")
            foreach (var dto in dependents) dto.MutationId = "new-command-" + Guid.NewGuid().ToString("N");
        if (mode == "missing-dependent")
        {
            _dbContext.RemoveRange(payment, transaction, invoice);
            await _dbContext.SaveChangesAsync();
        }
        if (mode == "current-office-denied")
        {
            invoice.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
            transaction.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
            await _dbContext.SaveChangesAsync();
        }
        _dbContext.ChangeTracker.Clear();

        async Task<string> SnapshotAsync() => JsonSerializer.Serialize(new
        {
            Profiles = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Invoices = await _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Payments = await _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Transactions = await _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Receipts = await _dbContext.ProcessedSyncMutations.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Ledger = await _dbContext.InventoryLedgerEntries.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
        });
        var before = await SnapshotAsync();
        var controller = mode == "current-office-denied"
            ? CreateController(_dbContext, new TestCurrentUserContext
            {
                Username = "office-only-admin", TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                IsAdmin = true
            })
            : _controller;
        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = device, RentalBillingScheduleVersion = 2,
            RentalBillingProfiles = [profileDto], Invoices = [invoiceDto],
            Payments = [paymentDto], Transactions = [transactionDto]
        }, CancellationToken.None);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        _dbContext.ChangeTracker.Clear();
        Assert.Equal(before, await SnapshotAsync());
        if (mode == "exact")
        {
            Assert.Empty(result.Conflicts);
            Assert.Equal(4, result.AcceptedCount);
            Assert.Equal(4, result.DuplicateMutationCount);
            foreach (var (dto, name) in mutations)
                Assert.Single(result.AcceptedRevisions, x => x.EntityName == name && x.EntityId == dto.Id);
            Assert.Equal(invoice.InvoiceNumber, result.AssignedInvoiceNumbers[invoice.Id]);
            Assert.Equal(invoice.TaxInvoiceNumber, result.AssignedTaxInvoiceNumbers[invoice.Id]);
        }
        else
        {
            Assert.Equal(1, result.AcceptedCount);
            Assert.Equal(1, result.DuplicateMutationCount);
            Assert.Equal(3, result.ConflictCount);
            foreach (var (_, name) in mutations.Skip(1))
                Assert.Single(result.Conflicts, x => x.EntityName == name && x.Reason.Contains("unproven generation", StringComparison.Ordinal));
        }
    }
}
