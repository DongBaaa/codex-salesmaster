using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    public async Task<List<RecycleBinEntry>> GetRecycleBinEntriesAsync(
        SessionState session,
        CancellationToken ct = default)
    {
        var entries = new List<RecycleBinEntry>();

        var deletedCustomers = await ApplyCustomerScope(
                _db.Customers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(customer => customer.IsDeleted),
                session)
            .OrderByDescending(customer => customer.UpdatedAtUtc)
            .ToListAsync(ct);

        entries.AddRange(deletedCustomers.Select(customer => new RecycleBinEntry
        {
            EntityId = customer.Id,
            Kind = RecycleBinEntityKind.Customer,
            Title = customer.NameOriginal,
            Subtitle = JoinSegments(customer.BusinessNumber, customer.Phone),
            Detail = JoinSegments(customer.Address, customer.ContactPerson, customer.Notes),
            DeletedAtUtc = customer.UpdatedAtUtc
        }));

        var deletedItems = await _db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.IsDeleted)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToListAsync(ct);

        entries.AddRange(deletedItems.Select(item => new RecycleBinEntry
        {
            EntityId = item.Id,
            Kind = RecycleBinEntityKind.Item,
            Title = item.NameOriginal,
            Subtitle = JoinSegments(item.SpecificationOriginal, item.CategoryName, item.Unit),
            Detail = JoinSegments(
                item.CurrentStock != 0m ? $"현재고 {item.CurrentStock:N0}" : null,
                item.SalePrice != 0m ? $"매출단가 {item.SalePrice:N0}원" : null,
                item.Notes),
            DeletedAtUtc = item.UpdatedAtUtc
        }));

        var deletedInvoices = await ApplyInvoiceScope(
                _db.Invoices
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(invoice => invoice.IsDeleted),
                session)
            .OrderByDescending(invoice => invoice.UpdatedAtUtc)
            .ToListAsync(ct);

        var invoiceCustomerNames = await GetCustomerNameMapAsync(
            deletedInvoices.Select(invoice => invoice.CustomerId),
            ct);

        foreach (var group in deletedInvoices
                     .GroupBy(invoice => invoice.VersionGroupId == Guid.Empty ? invoice.Id : invoice.VersionGroupId))
        {
            var invoice = group
                .OrderByDescending(current => current.VersionNumber)
                .ThenByDescending(current => current.UpdatedAtUtc)
                .First();

            var customerName = invoiceCustomerNames.TryGetValue(invoice.CustomerId, out var resolvedName)
                ? resolvedName
                : "(삭제된 거래처)";
            var displayNumber = string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
                ? invoice.LocalTempNumber
                : invoice.InvoiceNumber;

            entries.Add(new RecycleBinEntry
            {
                EntityId = invoice.Id,
                Kind = RecycleBinEntityKind.Invoice,
                Title = $"{customerName} · {invoice.InvoiceDate:yyyy-MM-dd}",
                Subtitle = JoinSegments(GetVoucherTypeLabel(invoice.VoucherType), displayNumber),
                Detail = JoinSegments(
                    $"{invoice.TotalAmount:N0}원",
                    group.Count() > 1 ? $"버전 {group.Count():N0}건" : null,
                    string.IsNullOrWhiteSpace(invoice.Memo) ? null : invoice.Memo),
                DeletedAtUtc = group.Max(current => current.UpdatedAtUtc)
            });
        }

        var deletedContractRows = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(contract => contract.IsDeleted)
            .OrderByDescending(contract => contract.UpdatedAtUtc)
            .ToListAsync(ct);

        var deletedContractCustomerIds = deletedContractRows
            .Select(contract => contract.CustomerId)
            .Distinct()
            .ToList();
        var deletedContractCustomers = deletedContractCustomerIds.Count == 0
            ? new Dictionary<Guid, LocalCustomer>()
            : await _db.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => deletedContractCustomerIds.Contains(customer.Id))
                .ToDictionaryAsync(customer => customer.Id, ct);

        foreach (var contract in deletedContractRows)
        {
            if (!deletedContractCustomers.TryGetValue(contract.CustomerId, out var customer) ||
                !CanAccessCustomer(customer, session))
            {
                continue;
            }

            entries.Add(new RecycleBinEntry
            {
                EntityId = contract.Id,
                Kind = RecycleBinEntityKind.CustomerContract,
                Title = $"{customer.NameOriginal} · {contract.FileName}",
                Subtitle = JoinSegments(contract.ContractType, contract.IsPrimary ? "대표 계약서" : null),
                Detail = JoinSegments(
                    contract.SignedDate.HasValue ? $"체결일 {contract.SignedDate:yyyy-MM-dd}" : null,
                    contract.ExpireDate.HasValue ? $"만료일 {contract.ExpireDate:yyyy-MM-dd}" : null,
                    contract.FileSize > 0 ? $"{contract.FileSize / 1024m:N0} KB" : null,
                    contract.Description),
                DeletedAtUtc = contract.UpdatedAtUtc
            });
        }

        var deletedPayments = await _db.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(payment => payment.IsDeleted)
            .OrderByDescending(payment => payment.UpdatedAtUtc)
            .ToListAsync(ct);

        var paymentInvoiceIds = deletedPayments
            .Select(payment => payment.InvoiceId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var paymentInvoices = paymentInvoiceIds.Count == 0
            ? new Dictionary<Guid, LocalInvoice>()
            : await _db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => paymentInvoiceIds.Contains(invoice.Id))
                .ToDictionaryAsync(invoice => invoice.Id, ct);
        var paymentCustomerNames = await GetCustomerNameMapAsync(
            paymentInvoices.Values.Select(invoice => invoice.CustomerId),
            ct);

        foreach (var payment in deletedPayments)
        {
            if (!paymentInvoices.TryGetValue(payment.InvoiceId, out var invoice) ||
                !CanAccessInvoice(invoice, session))
            {
                continue;
            }

            var customerName = paymentCustomerNames.TryGetValue(invoice.CustomerId, out var resolvedName)
                ? resolvedName
                : "(거래처 미상)";
            var displayNumber = string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
                ? invoice.LocalTempNumber
                : invoice.InvoiceNumber;

            entries.Add(new RecycleBinEntry
            {
                EntityId = payment.Id,
                Kind = RecycleBinEntityKind.Payment,
                Title = $"{customerName} · {payment.Amount:N0}원",
                Subtitle = JoinSegments($"전표 {displayNumber}", payment.PaymentDate.ToString("yyyy-MM-dd")),
                Detail = string.IsNullOrWhiteSpace(payment.Note) ? "삭제된 수금/지급 기록" : payment.Note,
                DeletedAtUtc = payment.UpdatedAtUtc
            });
        }

        var deletedTransactions = await ApplyTransactionScope(
                _db.Transactions
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(transaction => transaction.IsDeleted),
                session)
            .OrderByDescending(transaction => transaction.UpdatedAtUtc)
            .ToListAsync(ct);

        var transactionCustomerNames = await GetCustomerNameMapAsync(
            deletedTransactions.Select(transaction => transaction.CustomerId),
            ct);

        entries.AddRange(deletedTransactions.Select(transaction =>
        {
            var customerName = transactionCustomerNames.TryGetValue(transaction.CustomerId, out var resolvedName)
                ? resolvedName
                : "(거래처 미상)";
            var totalAmount = transaction.ReceiptTotal > 0m
                ? transaction.ReceiptTotal
                : transaction.PaymentTotal;

            return new RecycleBinEntry
            {
                EntityId = transaction.Id,
                Kind = RecycleBinEntityKind.Transaction,
                Title = $"{customerName} · {GetTransactionKindLabel(transaction.TransactionKind)}",
                Subtitle = JoinSegments(transaction.TransactionDate.ToString("yyyy-MM-dd"), totalAmount > 0m ? $"{totalAmount:N0}원" : null),
                Detail = JoinSegments(transaction.Note, transaction.Memo),
                DeletedAtUtc = transaction.UpdatedAtUtc
            };
        }));

        return entries
            .OrderByDescending(entry => entry.DeletedAtUtc)
            .ThenBy(entry => entry.KindText, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task<OfficeMutationResult> RestoreRecycleBinEntryAsync(
        RecycleBinEntityKind kind,
        Guid entityId,
        SessionState session,
        CancellationToken ct = default)
    {
        return kind switch
        {
            RecycleBinEntityKind.Customer => RestoreCustomerAsync(entityId, session, ct),
            RecycleBinEntityKind.CustomerContract => RestoreCustomerContractAsync(entityId, session, ct),
            RecycleBinEntityKind.Item => RestoreItemAsync(entityId, session, ct),
            RecycleBinEntityKind.Invoice => RestoreInvoiceAsync(entityId, session, ct),
            RecycleBinEntityKind.Payment => RestoreDeletedPaymentAsync(entityId, session, ct),
            RecycleBinEntityKind.Transaction => RestoreTransactionAsync(entityId, session, ct),
            _ => Task.FromResult(OfficeMutationResult.Denied("복원할 수 없는 휴지통 항목입니다."))
        };
    }

    public Task<OfficeMutationResult> PermanentlyDeleteRecycleBinEntryAsync(
        RecycleBinEntityKind kind,
        Guid entityId,
        SessionState session,
        CancellationToken ct = default)
    {
        return kind switch
        {
            RecycleBinEntityKind.Customer => PermanentlyDeleteCustomerAsync(entityId, session, ct),
            RecycleBinEntityKind.CustomerContract => PermanentlyDeleteCustomerContractAsync(entityId, session, ct),
            RecycleBinEntityKind.Item => PermanentlyDeleteItemAsync(entityId, session, ct),
            RecycleBinEntityKind.Invoice => PermanentlyDeleteInvoiceAsync(entityId, session, ct),
            RecycleBinEntityKind.Payment => PermanentlyDeletePaymentAsync(entityId, session, ct),
            RecycleBinEntityKind.Transaction => PermanentlyDeleteTransactionAsync(entityId, session, ct),
            _ => Task.FromResult(OfficeMutationResult.Denied("영구삭제할 수 없는 휴지통 항목입니다."))
        };
    }

    public async Task<OfficeMutationResult> RestoreCustomerAsync(
        Guid customerId,
        SessionState session,
        CancellationToken ct = default)
    {
        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == customerId, ct);
        if (customer is null)
            return OfficeMutationResult.Missing("복원할 거래처를 찾을 수 없습니다.");
        if (!customer.IsDeleted)
            return OfficeMutationResult.Ok(customerId, "이미 활성 상태인 거래처입니다.");
        if (!CanAccessCustomer(customer, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래처를 복원할 수 없습니다.");

        var now = DateTime.UtcNow;
        RestoreCustomerCore(customer, session, now);
        AddRestoreAudit(nameof(LocalCustomer), customer.Id, new
        {
            customer.NameOriginal,
            customer.BusinessNumber,
            customer.Phone
        }, session, now);

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(customer.Id, "거래처를 휴지통에서 복원했습니다.");
    }

    public async Task<OfficeMutationResult> PermanentlyDeleteCustomerAsync(
        Guid customerId,
        SessionState session,
        CancellationToken ct = default)
    {
        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == customerId, ct);
        if (customer is null)
            return OfficeMutationResult.Missing("영구삭제할 거래처를 찾을 수 없습니다.");
        if (!customer.IsDeleted)
            return OfficeMutationResult.Denied("활성 상태 거래처는 휴지통에서 영구삭제할 수 없습니다.");
        if (!CanAccessCustomer(customer, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래처를 영구삭제할 수 없습니다.");

        var hasInvoices = await _db.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(current => current.CustomerId == customerId, ct);
        if (hasInvoices)
            return OfficeMutationResult.Denied("연결된 전표가 남아 있어 거래처를 영구삭제할 수 없습니다. 전표를 먼저 정리하세요.");

        var hasTransactions = await _db.Transactions
            .IgnoreQueryFilters()
            .AnyAsync(current => current.CustomerId == customerId, ct);
        if (hasTransactions)
            return OfficeMutationResult.Denied("연결된 거래내역이 남아 있어 거래처를 영구삭제할 수 없습니다. 거래내역을 먼저 정리하세요.");

        var hasRentalProfiles = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AnyAsync(current => current.CustomerId == customerId, ct);
        if (hasRentalProfiles)
            return OfficeMutationResult.Denied("연결된 렌탈 청구 프로필이 남아 있어 거래처를 영구삭제할 수 없습니다.");

        var contracts = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == customerId)
            .ToListAsync(ct);
        if (contracts.Any(current => !current.IsDeleted))
            return OfficeMutationResult.Denied("활성 계약서가 남아 있어 거래처를 영구삭제할 수 없습니다.");

        var now = DateTime.UtcNow;
        _db.CustomerContracts.RemoveRange(contracts);
        _db.Customers.Remove(customer);
        AddPurgeAudit(nameof(LocalCustomer), customer.Id, new
        {
            customer.NameOriginal,
            customer.BusinessNumber,
            customer.Phone
        }, session, now);

        await _db.SaveChangesAsync(ct);
        _officeAccess.RevokeTemporaryCustomerAccess(session, customerId);
        return OfficeMutationResult.Ok(customer.Id, "거래처를 휴지통에서 영구삭제했습니다.");
    }

    public async Task<OfficeMutationResult> RestoreCustomerContractAsync(
        Guid contractId,
        SessionState session,
        CancellationToken ct = default)
    {
        var contract = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contractId, ct);
        if (contract is null)
            return OfficeMutationResult.Missing("복원할 계약서를 찾을 수 없습니다.");
        if (!contract.IsDeleted)
            return OfficeMutationResult.Ok(contractId, "이미 활성 상태인 계약서입니다.");

        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contract.CustomerId, ct);
        if (customer is null)
            return OfficeMutationResult.Missing("계약서와 연결된 거래처를 찾을 수 없습니다.");
        if (!CanAccessCustomer(customer, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 계약서를 복원할 수 없습니다.");

        var now = DateTime.UtcNow;
        var restoredCustomer = false;
        if (customer.IsDeleted)
        {
            RestoreCustomerCore(customer, session, now);
            AddRestoreAudit(nameof(LocalCustomer), customer.Id, new
            {
                customer.NameOriginal,
                Reason = "ContractRestore"
            }, session, now);
            restoredCustomer = true;
        }

        if (contract.IsPrimary)
            await ClearPrimaryCustomerContractAsync(contract.CustomerId, contract.Id, ct);

        RestoreEntity(contract, now);
        if (contract.FileContent.Length > 0 && contract.FileSize <= 0)
            contract.FileSize = contract.FileContent.LongLength;
        if (contract.FileContent.Length > 0 && string.IsNullOrWhiteSpace(contract.FileHash))
            contract.FileHash = ComputeFileHash(contract.FileContent);

        AddRestoreAudit(nameof(LocalCustomerContract), contract.Id, new
        {
            customer.NameOriginal,
            contract.ContractType,
            contract.FileName
        }, session, now);

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(
            contract.Id,
            restoredCustomer
                ? "계약서를 복원하고 연결된 거래처도 함께 활성화했습니다."
                : "계약서를 휴지통에서 복원했습니다.");
    }

    public async Task<OfficeMutationResult> PermanentlyDeleteCustomerContractAsync(
        Guid contractId,
        SessionState session,
        CancellationToken ct = default)
    {
        var contract = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contractId, ct);
        if (contract is null)
            return OfficeMutationResult.Missing("영구삭제할 계약서를 찾을 수 없습니다.");
        if (!contract.IsDeleted)
            return OfficeMutationResult.Denied("활성 상태 계약서는 휴지통에서 영구삭제할 수 없습니다.");

        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contract.CustomerId, ct);
        if (customer is null)
            return OfficeMutationResult.Missing("계약서와 연결된 거래처를 찾을 수 없습니다.");
        if (!CanAccessCustomer(customer, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 계약서를 영구삭제할 수 없습니다.");

        var now = DateTime.UtcNow;
        _db.CustomerContracts.Remove(contract);
        AddPurgeAudit(nameof(LocalCustomerContract), contract.Id, new
        {
            customer.NameOriginal,
            contract.ContractType,
            contract.FileName
        }, session, now);

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(contract.Id, "계약서를 휴지통에서 영구삭제했습니다.");
    }

    public async Task<OfficeMutationResult> RestoreItemAsync(
        Guid itemId,
        SessionState session,
        CancellationToken ct = default)
    {
        var item = await _db.Items
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == itemId, ct);
        if (item is null)
            return OfficeMutationResult.Missing("복원할 품목을 찾을 수 없습니다.");
        if (!item.IsDeleted)
            return OfficeMutationResult.Ok(itemId, "이미 활성 상태인 품목입니다.");

        var now = DateTime.UtcNow;
        RestoreEntity(item, now);
        AddRestoreAudit(nameof(LocalItem), item.Id, new
        {
            item.NameOriginal,
            item.SpecificationOriginal,
            item.CategoryName
        }, session, now);

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(item.Id, "품목을 휴지통에서 복원했습니다.");
    }

    public async Task<OfficeMutationResult> PermanentlyDeleteItemAsync(
        Guid itemId,
        SessionState session,
        CancellationToken ct = default)
    {
        var item = await _db.Items
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == itemId, ct);
        if (item is null)
            return OfficeMutationResult.Missing("영구삭제할 품목을 찾을 수 없습니다.");
        if (!item.IsDeleted)
            return OfficeMutationResult.Denied("활성 상태 품목은 휴지통에서 영구삭제할 수 없습니다.");

        var invoiceLines = await _db.InvoiceLines
            .IgnoreQueryFilters()
            .Where(current => current.ItemId == itemId)
            .ToListAsync(ct);
        foreach (var line in invoiceLines)
            line.ItemId = null;

        var transferLines = await _db.InventoryTransferLines
            .IgnoreQueryFilters()
            .Where(current => current.ItemId == itemId)
            .ToListAsync(ct);
        foreach (var line in transferLines)
            line.ItemId = null;

        var invoiceLineSerials = await _db.InvoiceLineSerials
            .Where(current => current.ItemId == itemId)
            .ToListAsync(ct);
        foreach (var serial in invoiceLineSerials)
            serial.ItemId = null;

        var serialLedgers = await _db.SerialLedgers
            .Where(current => current.ItemId == itemId)
            .ToListAsync(ct);
        foreach (var ledger in serialLedgers)
            ledger.ItemId = null;

        var movements = await _db.InventoryMovements
            .Where(current => current.ItemId == itemId)
            .ToListAsync(ct);
        var stockLayers = await _db.StockLayers
            .Where(current => current.ItemId == itemId)
            .ToListAsync(ct);
        var warehouseStocks = await _db.ItemWarehouseStocks
            .Where(current => current.ItemId == itemId)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        _db.InventoryMovements.RemoveRange(movements);
        _db.StockLayers.RemoveRange(stockLayers);
        _db.ItemWarehouseStocks.RemoveRange(warehouseStocks);
        _db.Items.Remove(item);
        AddPurgeAudit(nameof(LocalItem), item.Id, new
        {
            item.NameOriginal,
            item.SpecificationOriginal,
            item.CategoryName
        }, session, now);

        await _db.SaveChangesAsync(ct);

        await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
        {
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            ForceOverride = false
        }, ct);

        RaiseInventoryStateChanged();
        return OfficeMutationResult.Ok(item.Id, "품목을 휴지통에서 영구삭제했습니다.");
    }

    public async Task<OfficeMutationResult> RestoreInvoiceAsync(
        Guid invoiceId,
        SessionState session,
        CancellationToken ct = default)
    {
        var target = await _db.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == invoiceId, ct);
        if (target is null)
            return OfficeMutationResult.Missing("복원할 전표를 찾을 수 없습니다.");
        if (!target.IsDeleted)
            return OfficeMutationResult.Ok(invoiceId, "이미 활성 상태인 전표입니다.");
        if (!CanAccessInvoice(target, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 전표를 복원할 수 없습니다.");

        var customerRestored = await RestoreInvoiceGroupCoreAsync(target, session, ct);
        return OfficeMutationResult.Ok(
            target.Id,
            customerRestored
                ? "전표를 복원하고 연결된 거래처도 함께 활성화했습니다."
                : "전표를 휴지통에서 복원했습니다.");
    }

    public async Task<OfficeMutationResult> PermanentlyDeleteInvoiceAsync(
        Guid invoiceId,
        SessionState session,
        CancellationToken ct = default)
    {
        var target = await _db.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == invoiceId, ct);
        if (target is null)
            return OfficeMutationResult.Missing("영구삭제할 전표를 찾을 수 없습니다.");
        if (!target.IsDeleted)
            return OfficeMutationResult.Denied("활성 상태 전표는 휴지통에서 영구삭제할 수 없습니다.");
        if (!CanAccessInvoice(target, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 전표를 영구삭제할 수 없습니다.");

        var versionGroupId = target.VersionGroupId == Guid.Empty ? target.Id : target.VersionGroupId;
        var groupInvoices = await _db.Invoices
            .IgnoreQueryFilters()
            .Where(current => current.Id == target.Id || current.VersionGroupId == versionGroupId)
            .ToListAsync(ct);
        var invoiceIds = groupInvoices.Select(current => current.Id).Distinct().ToList();

        var hasTransactions = await _db.Transactions
            .IgnoreQueryFilters()
            .AnyAsync(current =>
                current.LinkedInvoiceId.HasValue &&
                invoiceIds.Contains(current.LinkedInvoiceId.Value), ct);
        if (hasTransactions)
            return OfficeMutationResult.Denied("연결된 거래내역이 남아 있어 전표를 영구삭제할 수 없습니다. 거래내역을 먼저 정리하세요.");

        var hasActivePayments = await _db.Payments
            .IgnoreQueryFilters()
            .AnyAsync(current => invoiceIds.Contains(current.InvoiceId) && !current.IsDeleted, ct);
        if (hasActivePayments)
            return OfficeMutationResult.Denied("활성 수금/지급 기록이 남아 있어 전표를 영구삭제할 수 없습니다.");

        var now = DateTime.UtcNow;
        _db.Invoices.RemoveRange(groupInvoices);
        AddPurgeAudit(nameof(LocalInvoice), target.Id, new
        {
            target.CustomerId,
            target.InvoiceDate,
            target.InvoiceNumber,
            VersionCount = groupInvoices.Count
        }, session, now);

        await _db.SaveChangesAsync(ct);

        await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
        {
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            ForceOverride = false
        }, ct);

        return OfficeMutationResult.Ok(target.Id, "전표를 휴지통에서 영구삭제했습니다.");
    }

    public async Task<OfficeMutationResult> RestoreDeletedPaymentAsync(
        Guid paymentId,
        SessionState session,
        CancellationToken ct = default)
    {
        var payment = await _db.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == paymentId, ct);
        if (payment is null)
            return OfficeMutationResult.Missing("복원할 수금/지급 기록을 찾을 수 없습니다.");
        if (!payment.IsDeleted)
            return OfficeMutationResult.Ok(paymentId, "이미 활성 상태인 수금/지급 기록입니다.");

        var invoice = await _db.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == payment.InvoiceId, ct);
        if (invoice is null)
            return OfficeMutationResult.Missing("연결된 전표를 찾을 수 없습니다.");
        if (!CanAccessInvoice(invoice, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 수금/지급 기록을 복원할 수 없습니다.");

        var customerRestored = false;
        var invoiceRestored = false;
        if (invoice.IsDeleted)
        {
            customerRestored = await RestoreInvoiceGroupCoreAsync(invoice, session, ct);
            invoiceRestored = true;
        }

        var now = DateTime.UtcNow;
        RestoreEntity(payment, now);
        AddRestoreAudit(nameof(LocalPayment), payment.Id, new
        {
            payment.InvoiceId,
            payment.PaymentDate,
            payment.Amount
        }, session, now);

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(
            payment.Id,
            customerRestored || invoiceRestored
                ? "수금/지급 기록을 복원하고 연결 전표도 함께 활성화했습니다."
                : "수금/지급 기록을 휴지통에서 복원했습니다.");
    }

    public async Task<OfficeMutationResult> PermanentlyDeletePaymentAsync(
        Guid paymentId,
        SessionState session,
        CancellationToken ct = default)
    {
        var payment = await _db.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == paymentId, ct);
        if (payment is null)
            return OfficeMutationResult.Missing("영구삭제할 수금/지급 기록을 찾을 수 없습니다.");
        if (!payment.IsDeleted)
            return OfficeMutationResult.Denied("활성 상태 수금/지급 기록은 휴지통에서 영구삭제할 수 없습니다.");

        var invoice = await _db.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == payment.InvoiceId, ct);
        if (invoice is not null && !CanAccessInvoice(invoice, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 수금/지급 기록을 영구삭제할 수 없습니다.");

        var now = DateTime.UtcNow;
        _db.Payments.Remove(payment);
        AddPurgeAudit(nameof(LocalPayment), payment.Id, new
        {
            payment.InvoiceId,
            payment.PaymentDate,
            payment.Amount
        }, session, now);

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(payment.Id, "수금/지급 기록을 휴지통에서 영구삭제했습니다.");
    }

    public async Task<OfficeMutationResult> RestoreTransactionAsync(
        Guid transactionId,
        SessionState session,
        CancellationToken ct = default)
    {
        var transaction = await _db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transactionId, ct);
        if (transaction is null)
            return OfficeMutationResult.Missing("복원할 거래내역을 찾을 수 없습니다.");
        if (!transaction.IsDeleted)
            return OfficeMutationResult.Ok(transactionId, "이미 활성 상태인 거래내역입니다.");
        if (!CanAccessTransaction(transaction, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래내역을 복원할 수 없습니다.");

        var now = DateTime.UtcNow;
        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transaction.CustomerId, ct);
        if (customer is null)
            return OfficeMutationResult.Missing("연결된 거래처를 찾을 수 없습니다.");

        var customerRestored = false;
        var invoiceRestored = false;
        if (customer.IsDeleted)
        {
            RestoreCustomerCore(customer, session, now);
            AddRestoreAudit(nameof(LocalCustomer), customer.Id, new
            {
                customer.NameOriginal,
                Reason = "TransactionRestore"
            }, session, now);
            customerRestored = true;
        }

        LocalInvoice? linkedInvoice = null;
        if (transaction.LinkedInvoiceId.HasValue && transaction.LinkedInvoiceId.Value != Guid.Empty)
        {
            linkedInvoice = await _db.Invoices
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == transaction.LinkedInvoiceId.Value, ct);
            if (linkedInvoice is not null && linkedInvoice.IsDeleted)
            {
                customerRestored = customerRestored || await RestoreInvoiceGroupCoreAsync(linkedInvoice, session, ct);
                invoiceRestored = true;
                linkedInvoice = await _db.Invoices
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(current => current.Id == transaction.LinkedInvoiceId.Value, ct);
            }
        }

        RestoreEntity(transaction, now);
        AddRestoreAudit(nameof(LocalTransaction), transaction.Id, new
        {
            transaction.CustomerId,
            transaction.TransactionDate,
            transaction.TransactionKind
        }, session, now);

        await _db.SaveChangesAsync(ct);

        if (linkedInvoice is not null && !linkedInvoice.IsDeleted)
            await SyncInvoicePaymentFromTransactionAsync(transaction, linkedInvoice, ct);

        if (transaction.LinkedRentalBillingProfileId.HasValue && transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
            await RecalculateRentalSettlementAsync(transaction.LinkedRentalBillingProfileId.Value, ct);

        return OfficeMutationResult.Ok(
            transaction.Id,
            customerRestored || invoiceRestored
                ? "거래내역을 복원하고 연결된 거래처/전표를 함께 활성화했습니다."
                : "거래내역을 휴지통에서 복원했습니다.");
    }

    public async Task<OfficeMutationResult> PermanentlyDeleteTransactionAsync(
        Guid transactionId,
        SessionState session,
        CancellationToken ct = default)
    {
        var transaction = await _db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transactionId, ct);
        if (transaction is null)
            return OfficeMutationResult.Missing("영구삭제할 거래내역을 찾을 수 없습니다.");
        if (!transaction.IsDeleted)
            return OfficeMutationResult.Denied("활성 상태 거래내역은 휴지통에서 영구삭제할 수 없습니다.");
        if (!CanAccessTransaction(transaction, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래내역을 영구삭제할 수 없습니다.");

        var attachments = await _db.TransactionAttachments
            .IgnoreQueryFilters()
            .Where(current => current.TransactionId == transactionId)
            .ToListAsync(ct);
        foreach (var attachment in attachments)
            TryDeleteAttachmentFile(attachment);

        var linkedPayment = await _db.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transactionId, ct);

        var now = DateTime.UtcNow;
        if (linkedPayment is not null)
            _db.Payments.Remove(linkedPayment);

        _db.TransactionAttachments.RemoveRange(attachments);
        _db.Transactions.Remove(transaction);
        AddPurgeAudit(nameof(LocalTransaction), transaction.Id, new
        {
            transaction.CustomerId,
            transaction.TransactionDate,
            transaction.TransactionKind
        }, session, now);

        await _db.SaveChangesAsync(ct);

        if (transaction.LinkedRentalBillingProfileId.HasValue && transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
            await RecalculateRentalSettlementAsync(transaction.LinkedRentalBillingProfileId.Value, ct);

        return OfficeMutationResult.Ok(transaction.Id, "거래내역을 휴지통에서 영구삭제했습니다.");
    }

    private async Task<bool> RestoreInvoiceGroupCoreAsync(
        LocalInvoice target,
        SessionState session,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == target.CustomerId, ct);
        if (customer is null)
            throw new InvalidOperationException("연결된 거래처를 찾을 수 없습니다.");

        var customerRestored = false;
        if (customer.IsDeleted)
        {
            RestoreCustomerCore(customer, session, now);
            AddRestoreAudit(nameof(LocalCustomer), customer.Id, new
            {
                customer.NameOriginal,
                Reason = "InvoiceRestore"
            }, session, now);
            customerRestored = true;
        }

        var versionGroupId = target.VersionGroupId == Guid.Empty ? target.Id : target.VersionGroupId;
        var groupInvoices = await _db.Invoices
            .IgnoreQueryFilters()
            .Where(current => current.Id == target.Id || current.VersionGroupId == versionGroupId)
            .ToListAsync(ct);

        var latestVersionId = groupInvoices
            .OrderByDescending(current => current.VersionNumber)
            .ThenByDescending(current => current.UpdatedAtUtc)
            .Select(current => current.Id)
            .FirstOrDefault();

        foreach (var invoice in groupInvoices)
        {
            invoice.IsDeleted = false;
            invoice.IsDirty = true;
            invoice.IsLatestVersion = invoice.Id == latestVersionId;
            invoice.UpdatedAtUtc = now;
            invoice.LastSavedAtUtc = now;
        }

        AddRestoreAudit(nameof(LocalInvoice), target.Id, new
        {
            target.CustomerId,
            target.InvoiceDate,
            target.InvoiceNumber,
            VersionCount = groupInvoices.Count
        }, session, now);

        await _db.SaveChangesAsync(ct);

        await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
        {
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            ForceOverride = false
        }, ct);

        return customerRestored;
    }

    private void RestoreCustomerCore(
        LocalCustomer customer,
        SessionState session,
        DateTime now)
    {
        RestoreEntity(customer, now);

        if (!session.HasAdministrativePrivileges &&
            !string.Equals(
                NormalizeOfficeCode(customer.ResponsibleOfficeCode, DomainConstants.OfficeUsenet),
                NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet),
                StringComparison.OrdinalIgnoreCase))
        {
            _officeAccess.GrantTemporaryCustomerAccess(session, customer.Id);
        }
    }

    private static void RestoreEntity(
        LocalSyncEntity entity,
        DateTime now)
    {
        entity.IsDeleted = false;
        entity.IsDirty = true;
        entity.UpdatedAtUtc = now;
    }

    private void AddRestoreAudit(
        string entityName,
        Guid entityId,
        object payload,
        SessionState session,
        DateTime now)
    {
        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = entityName,
            EntityId = entityId.ToString("D"),
            Action = "Restore",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = string.Empty,
            AfterJson = JsonSerializer.Serialize(payload, AuditJsonOptions),
            CreatedAtUtc = now
        });
    }

    private void AddPurgeAudit(
        string entityName,
        Guid entityId,
        object payload,
        SessionState session,
        DateTime now)
    {
        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = entityName,
            EntityId = entityId.ToString("D"),
            Action = "Purge",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = JsonSerializer.Serialize(payload, AuditJsonOptions),
            AfterJson = string.Empty,
            CreatedAtUtc = now
        });
    }

    private static void TryDeleteAttachmentFile(LocalTransactionAttachment attachment)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(attachment.StoredPath) && File.Exists(attachment.StoredPath))
                File.Delete(attachment.StoredPath);

            var directory = Path.GetDirectoryName(attachment.StoredPath);
            if (!string.IsNullOrWhiteSpace(directory) &&
                Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
        catch
        {
            // 파일 정리 실패는 영구삭제 자체를 막지 않는다.
        }
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

    private static string GetTransactionKindLabel(string? transactionKind)
    {
        return PaymentFlowConstants.NormalizeTransactionKind(transactionKind) switch
        {
            var kind when kind == PaymentFlowConstants.TransactionKindReceipt => "일반수금",
            var kind when kind == PaymentFlowConstants.TransactionKindPayment => "일반지급",
            var kind when kind == PaymentFlowConstants.TransactionKindAdvanceDeposit => "선수금입금",
            var kind when kind == PaymentFlowConstants.TransactionKindAdvanceRefund => "선수금환불",
            var kind when kind == PaymentFlowConstants.TransactionKindAdvanceApply => "선수금차감",
            var kind when kind == PaymentFlowConstants.TransactionKindInvoiceReceipt => "전표수금",
            var kind when kind == PaymentFlowConstants.TransactionKindInvoicePayment => "전표지급",
            var kind when kind == PaymentFlowConstants.TransactionKindRentalReceipt => "렌탈수금",
            _ => string.IsNullOrWhiteSpace(transactionKind) ? "거래내역" : transactionKind.Trim()
        };
    }
}
