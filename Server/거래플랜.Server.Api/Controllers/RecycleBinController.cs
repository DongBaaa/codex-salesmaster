using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("recycle-bin")]
public sealed class RecycleBinController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly OfficeScopeService _officeScopeService;

    public RecycleBinController(AppDbContext dbContext, OfficeScopeService officeScopeService)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
    }

    [HttpGet]
    public async Task<ActionResult<List<RecycleBinEntryDto>>> GetAll(
        [FromQuery] string? kind,
        [FromQuery] string? q,
        CancellationToken cancellationToken)
    {
        var normalizedKind = NormalizeKind(kind);
        var entries = new List<RecycleBinEntryDto>();

        if (ShouldIncludeKind(normalizedKind, "customer"))
        {
            var deletedCustomers = await _officeScopeService.ApplyCustomerScope(_dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => customer.IsDeleted))
                .OrderByDescending(customer => customer.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            entries.AddRange(deletedCustomers.Select(customer => new RecycleBinEntryDto
            {
                EntityId = customer.Id,
                Kind = "customer",
                KindText = "거래처",
                Title = customer.NameOriginal,
                Subtitle = JoinSegments(customer.BusinessNumber, customer.Phone),
                Detail = JoinSegments(customer.Address, customer.ContactPerson, customer.Notes),
                DeletedAtUtc = customer.UpdatedAtUtc
            }));
        }

        if (ShouldIncludeKind(normalizedKind, "contract"))
        {
            var deletedContracts = await _officeScopeService.ApplyCustomerContractScope(_dbContext.CustomerContracts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(contract => contract.IsDeleted))
                .OrderByDescending(contract => contract.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            var customerMap = await _dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => deletedContracts.Select(contract => contract.CustomerId).Contains(customer.Id))
                .ToDictionaryAsync(customer => customer.Id, customer => customer.NameOriginal, cancellationToken);

            entries.AddRange(deletedContracts.Select(contract =>
            {
                customerMap.TryGetValue(contract.CustomerId, out var customerName);
                return new RecycleBinEntryDto
                {
                    EntityId = contract.Id,
                    Kind = "contract",
                    KindText = "계약서",
                    Title = $"{customerName ?? "(삭제된 거래처)"} · {contract.FileName}",
                    Subtitle = JoinSegments(contract.ContractType, contract.IsPrimary ? "대표" : null),
                    Detail = JoinSegments(
                        contract.SignedDate.HasValue ? $"체결일 {contract.SignedDate:yyyy-MM-dd}" : null,
                        contract.ExpireDate.HasValue ? $"만료일 {contract.ExpireDate:yyyy-MM-dd}" : null,
                        contract.Description),
                    DeletedAtUtc = contract.UpdatedAtUtc
                };
            }));
        }

        if (ShouldIncludeKind(normalizedKind, "item"))
        {
            var deletedItems = await _officeScopeService.ApplyItemScope(_dbContext.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => item.IsDeleted))
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            entries.AddRange(deletedItems.Select(item => new RecycleBinEntryDto
            {
                EntityId = item.Id,
                Kind = "item",
                KindText = "품목",
                Title = item.NameOriginal,
                Subtitle = JoinSegments(item.SpecificationOriginal, item.Unit),
                Detail = JoinSegments(item.SerialNumber, item.MaterialNumber, item.Notes),
                DeletedAtUtc = item.UpdatedAtUtc
            }));
        }

        if (ShouldIncludeKind(normalizedKind, "invoice"))
        {
            var deletedInvoices = await _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => invoice.IsDeleted))
                .OrderByDescending(invoice => invoice.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            var customerMap = await _dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => deletedInvoices.Select(invoice => invoice.CustomerId).Contains(customer.Id))
                .ToDictionaryAsync(customer => customer.Id, customer => customer.NameOriginal, cancellationToken);

            entries.AddRange(deletedInvoices.Select(invoice =>
            {
                customerMap.TryGetValue(invoice.CustomerId, out var customerName);
                return new RecycleBinEntryDto
                {
                    EntityId = invoice.Id,
                    Kind = "invoice",
                    KindText = "전표",
                    Title = $"{customerName ?? "(삭제된 거래처)"} · {invoice.InvoiceDate:yyyy-MM-dd}",
                    Subtitle = JoinSegments(GetVoucherTypeLabel(invoice.VoucherType), invoice.InvoiceNumber, invoice.LocalTempNumber),
                    Detail = JoinSegments($"{invoice.TotalAmount:N0}원", invoice.Memo),
                    DeletedAtUtc = invoice.UpdatedAtUtc
                };
            }));
        }

        if (ShouldIncludeKind(normalizedKind, "payment"))
        {
            var deletedPayments = await _officeScopeService.ApplyPaymentScope(_dbContext.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(payment => payment.IsDeleted))
                .OrderByDescending(payment => payment.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            var invoiceMap = await _dbContext.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => deletedPayments.Select(payment => payment.InvoiceId).Contains(invoice.Id))
                .ToDictionaryAsync(invoice => invoice.Id, cancellationToken);

            var customerMap = await _dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => invoiceMap.Values.Select(invoice => invoice.CustomerId).Contains(customer.Id))
                .ToDictionaryAsync(customer => customer.Id, customer => customer.NameOriginal, cancellationToken);

            entries.AddRange(deletedPayments.Select(payment =>
            {
                invoiceMap.TryGetValue(payment.InvoiceId, out var invoice);
                var customerName = invoice is not null && customerMap.TryGetValue(invoice.CustomerId, out var resolvedName)
                    ? resolvedName
                    : "(삭제된 거래처)";

                return new RecycleBinEntryDto
                {
                    EntityId = payment.Id,
                    Kind = "payment",
                    KindText = "수금/지급",
                    Title = $"{customerName} · {payment.Amount:N0}원",
                    Subtitle = JoinSegments(
                        invoice is null ? null : $"전표 {invoice.InvoiceNumber}",
                        payment.PaymentDate.ToString("yyyy-MM-dd")),
                    Detail = string.IsNullOrWhiteSpace(payment.Note) ? "삭제된 수금/지급 기록" : payment.Note,
                    DeletedAtUtc = payment.UpdatedAtUtc
                };
            }));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var searchText = q.Trim();
            entries = entries
                .Where(entry =>
                    entry.KindText.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                    entry.Title.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                    entry.Subtitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                    entry.Detail.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        }

        return Ok(entries
            .OrderByDescending(entry => entry.DeletedAtUtc)
            .ThenBy(entry => entry.KindText, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }

    [HttpPost("restore")]
    public async Task<ActionResult<RecycleBinMutationResultDto>> Restore(
        [FromBody] RecycleBinMutationRequest request,
        CancellationToken cancellationToken)
    {
        var targets = request.Items
            .Where(item => item.EntityId != Guid.Empty && !string.IsNullOrWhiteSpace(item.Kind))
            .DistinctBy(item => (item.EntityId, NormalizeKind(item.Kind)))
            .ToList();

        var result = new RecycleBinMutationResultDto
        {
            RequestedCount = targets.Count
        };

        foreach (var target in targets)
        {
            var mutation = await RestoreCoreAsync(target, cancellationToken);
            result.Messages.Add(mutation.Message);
            if (mutation.Success)
                result.SucceededCount++;
        }

        return Ok(result);
    }

    [HttpPost("purge")]
    public async Task<ActionResult<RecycleBinMutationResultDto>> Purge(
        [FromBody] RecycleBinMutationRequest request,
        CancellationToken cancellationToken)
    {
        var targets = request.Items
            .Where(item => item.EntityId != Guid.Empty && !string.IsNullOrWhiteSpace(item.Kind))
            .DistinctBy(item => (item.EntityId, NormalizeKind(item.Kind)))
            .OrderBy(item => GetPurgeOrder(NormalizeKind(item.Kind)))
            .ToList();

        var result = new RecycleBinMutationResultDto
        {
            RequestedCount = targets.Count
        };

        foreach (var target in targets)
        {
            var mutation = await PurgeCoreAsync(target, cancellationToken);
            result.Messages.Add(mutation.Message);
            if (mutation.Success)
                result.SucceededCount++;
        }

        return Ok(result);
    }

    private async Task<(bool Success, string Message)> RestoreCoreAsync(
        RecycleBinMutationTargetDto target,
        CancellationToken cancellationToken)
    {
        return NormalizeKind(target.Kind) switch
        {
            "customer" => await RestoreCustomerAsync(target.EntityId, cancellationToken),
            "contract" => await RestoreContractAsync(target.EntityId, cancellationToken),
            "item" => await RestoreItemAsync(target.EntityId, cancellationToken),
            "invoice" => await RestoreInvoiceAsync(target.EntityId, cancellationToken),
            "payment" => await RestorePaymentAsync(target.EntityId, cancellationToken),
            _ => (false, $"지원하지 않는 휴지통 종류입니다: {target.Kind}")
        };
    }

    private async Task<(bool Success, string Message)> PurgeCoreAsync(
        RecycleBinMutationTargetDto target,
        CancellationToken cancellationToken)
    {
        return NormalizeKind(target.Kind) switch
        {
            "customer" => await PurgeCustomerAsync(target.EntityId, cancellationToken),
            "contract" => await PurgeContractAsync(target.EntityId, cancellationToken),
            "item" => await PurgeItemAsync(target.EntityId, cancellationToken),
            "invoice" => await PurgeInvoiceAsync(target.EntityId, cancellationToken),
            "payment" => await PurgePaymentAsync(target.EntityId, cancellationToken),
            _ => (false, $"지원하지 않는 휴지통 종류입니다: {target.Kind}")
        };
    }

    private async Task<(bool Success, string Message)> RestoreCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == customerId, cancellationToken);
        if (customer is null)
            return (false, "복원할 거래처를 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForCustomers(customer.OfficeCode, customer.TenantCode))
            return (false, "현재 계정으로 복원할 수 없는 거래처입니다.");
        if (!customer.IsDeleted)
            return (true, "이미 활성 상태인 거래처입니다.");

        customer.IsDeleted = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "거래처를 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestoreContractAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await _dbContext.CustomerContracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contractId, cancellationToken);
        if (contract is null)
            return (false, "복원할 계약서를 찾을 수 없습니다.");
        if (!contract.IsDeleted)
            return (true, "이미 활성 상태인 계약서입니다.");

        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contract.CustomerId, cancellationToken);
        if (customer is null)
            return (false, "계약서와 연결된 거래처를 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForContracts(customer.OfficeCode, customer.TenantCode))
            return (false, "현재 계정으로 복원할 수 없는 계약서입니다.");

        if (customer.IsDeleted)
            customer.IsDeleted = false;

        if (contract.IsPrimary)
        {
            var otherPrimaryContracts = await _dbContext.CustomerContracts
                .IgnoreQueryFilters()
                .Where(current => current.CustomerId == contract.CustomerId && current.Id != contract.Id && current.IsPrimary)
                .ToListAsync(cancellationToken);
            foreach (var other in otherPrimaryContracts)
                other.IsPrimary = false;
        }

        contract.IsDeleted = false;
        if (contract.FileContent.Length > 0 && contract.FileSize <= 0)
            contract.FileSize = contract.FileContent.LongLength;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "계약서를 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestoreItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await _dbContext.Items
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == itemId, cancellationToken);
        if (item is null)
            return (false, "복원할 품목을 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForItems(item.OfficeCode, item.TenantCode))
            return (false, "현재 계정으로 복원할 수 없는 품목입니다.");
        if (!item.IsDeleted)
            return (true, "이미 활성 상태인 품목입니다.");

        item.IsDeleted = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "품목을 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestoreInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == invoiceId, cancellationToken);
        if (invoice is null)
            return (false, "복원할 전표를 찾을 수 없습니다.");
        if (!invoice.IsDeleted)
            return (true, "이미 활성 상태인 전표입니다.");

        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == invoice.CustomerId, cancellationToken);
        if (customer is null)
            return (false, "전표와 연결된 거래처를 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForInvoices(invoice.OfficeCode, invoice.TenantCode))
            return (false, "현재 계정으로 복원할 수 없는 전표입니다.");

        if (customer.IsDeleted)
            customer.IsDeleted = false;

        invoice.IsDeleted = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "전표를 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestorePaymentAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await _dbContext.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == paymentId, cancellationToken);
        if (payment is null)
            return (false, "복원할 수금/지급 기록을 찾을 수 없습니다.");
        if (!payment.IsDeleted)
            return (true, "이미 활성 상태인 수금/지급 기록입니다.");

        var invoice = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == payment.InvoiceId, cancellationToken);
        if (invoice is null)
            return (false, "연결된 전표를 찾을 수 없습니다.");

        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == invoice.CustomerId, cancellationToken);
        if (customer is null)
            return (false, "연결된 거래처를 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForPayments(invoice.OfficeCode, invoice.TenantCode))
            return (false, "현재 계정으로 복원할 수 없는 수금/지급 기록입니다.");

        if (customer.IsDeleted)
            customer.IsDeleted = false;
        if (invoice.IsDeleted)
            invoice.IsDeleted = false;

        payment.IsDeleted = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "수금/지급 기록을 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == customerId, cancellationToken);
        if (customer is null)
            return (false, "영구삭제할 거래처를 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForCustomers(customer.OfficeCode, customer.TenantCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 거래처입니다.");
        if (!customer.IsDeleted)
            return (false, "활성 상태 거래처는 휴지통에서 영구삭제할 수 없습니다.");

        var hasInvoices = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(current => current.CustomerId == customerId, cancellationToken);
        if (hasInvoices)
            return (false, "연결된 전표가 남아 있어 거래처를 영구삭제할 수 없습니다.");

        _dbContext.Customers.Remove(customer);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "거래처를 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeContractAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await _dbContext.CustomerContracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contractId, cancellationToken);
        if (contract is null)
            return (false, "영구삭제할 계약서를 찾을 수 없습니다.");
        var contractCustomer = await _dbContext.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Id == contract.CustomerId, cancellationToken);
        if (contractCustomer is null || !_officeScopeService.CanWriteOfficeForContracts(contractCustomer.OfficeCode, contractCustomer.TenantCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 계약서입니다.");
        if (!contract.IsDeleted)
            return (false, "활성 상태 계약서는 휴지통에서 영구삭제할 수 없습니다.");

        _dbContext.CustomerContracts.Remove(contract);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "계약서를 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await _dbContext.Items
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == itemId, cancellationToken);
        if (item is null)
            return (false, "영구삭제할 품목을 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForItems(item.OfficeCode, item.TenantCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 품목입니다.");
        if (!item.IsDeleted)
            return (false, "활성 상태 품목은 휴지통에서 영구삭제할 수 없습니다.");

        _dbContext.Items.Remove(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "품목을 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == invoiceId, cancellationToken);
        if (invoice is null)
            return (false, "영구삭제할 전표를 찾을 수 없습니다.");
        var invoiceCustomer = await _dbContext.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Id == invoice.CustomerId, cancellationToken);
        if (invoiceCustomer is null || !_officeScopeService.CanWriteOfficeForInvoices(invoice.OfficeCode, invoice.TenantCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 전표입니다.");
        if (!invoice.IsDeleted)
            return (false, "활성 상태 전표는 휴지통에서 영구삭제할 수 없습니다.");

        var hasActivePayments = await _dbContext.Payments
            .IgnoreQueryFilters()
            .AnyAsync(current => current.InvoiceId == invoiceId && !current.IsDeleted, cancellationToken);
        if (hasActivePayments)
            return (false, "활성 수금/지급 기록이 남아 있어 전표를 영구삭제할 수 없습니다.");

        _dbContext.Invoices.Remove(invoice);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "전표를 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgePaymentAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await _dbContext.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == paymentId, cancellationToken);
        if (payment is null)
            return (false, "영구삭제할 수금/지급 기록을 찾을 수 없습니다.");
        var purgeInvoice = await _dbContext.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Id == payment.InvoiceId, cancellationToken);
        var purgeCustomer = purgeInvoice is null ? null : await _dbContext.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Id == purgeInvoice.CustomerId, cancellationToken);
        if (purgeInvoice is null || purgeCustomer is null || !_officeScopeService.CanWriteOfficeForPayments(purgeInvoice.OfficeCode, purgeInvoice.TenantCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 수금/지급 기록입니다.");
        if (!payment.IsDeleted)
            return (false, "활성 상태 수금/지급 기록은 휴지통에서 영구삭제할 수 없습니다.");

        _dbContext.Payments.Remove(payment);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "수금/지급 기록을 영구삭제했습니다.");
    }

    private static string NormalizeKind(string? kind)
    {
        return (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "customer" or "customers" or "거래처" => "customer",
            "contract" or "contracts" or "customercontract" or "계약서" => "contract",
            "item" or "items" or "품목" => "item",
            "invoice" or "invoices" or "전표" => "invoice",
            "payment" or "payments" or "수금" or "지급" or "수금/지급" => "payment",
            _ => string.Empty
        };
    }

    private static bool ShouldIncludeKind(string? normalizedKind, string candidate)
        => string.IsNullOrWhiteSpace(normalizedKind) || string.Equals(normalizedKind, candidate, StringComparison.Ordinal);

    private static int GetPurgeOrder(string? normalizedKind)
    {
        return normalizedKind switch
        {
            "payment" => 0,
            "contract" => 1,
            "invoice" => 2,
            "item" => 3,
            "customer" => 4,
            _ => 99
        };
    }

    private static string JoinSegments(params string?[] segments)
        => string.Join(" / ", segments.Where(segment => !string.IsNullOrWhiteSpace(segment)).Select(segment => segment!.Trim()));

    private static string GetVoucherTypeLabel(VoucherType voucherType)
    {
        return voucherType switch
        {
            VoucherType.Sales => "매출",
            VoucherType.Purchase => "매입",
            VoucherType.Procurement => "발주",
            VoucherType.Expense => "경비",
            VoucherType.Collection => "수금",
            _ => voucherType.ToString()
        };
    }
}
