using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    public async Task<RecycleBinDependencyInfo> GetRecycleBinDependencyInfoAsync(
        RecycleBinEntityKind kind,
        Guid entityId,
        SessionState session,
        CancellationToken ct = default)
    {
        return kind switch
        {
            RecycleBinEntityKind.Customer => await GetCustomerRecycleBinDependencyInfoAsync(entityId, session, ct),
            RecycleBinEntityKind.Invoice => await GetInvoiceRecycleBinDependencyInfoAsync(entityId, session, ct),
            RecycleBinEntityKind.Payment => await GetPaymentRecycleBinDependencyInfoAsync(entityId, session, ct),
            RecycleBinEntityKind.Transaction => await GetTransactionRecycleBinDependencyInfoAsync(entityId, session, ct),
            RecycleBinEntityKind.CustomerContract => await GetContractRecycleBinDependencyInfoAsync(entityId, session, ct),
            RecycleBinEntityKind.Item => await GetItemRecycleBinDependencyInfoAsync(entityId, session, ct),
            _ => new RecycleBinDependencyInfo
            {
                CanPurge = false,
                Summary = "삭제 차단 사유를 확인할 수 없습니다."
            }
        };
    }

    public async Task<List<RecycleBinCustomerMergeCandidate>> GetRecycleBinCustomerMergeCandidatesAsync(
        Guid deletedCustomerId,
        SessionState session,
        CancellationToken ct = default)
    {
        var source = await _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == deletedCustomerId, ct);
        if (source is null || !source.IsDeleted || !CanAccessCustomer(source, session))
            return new List<RecycleBinCustomerMergeCandidate>();

        var sourceNameKey = (source.NameMatchKey ?? string.Empty).Trim();
        var sourceBusinessDigits = NormalizeDigits(source.BusinessNumber);
        var sourcePhoneDigits = NormalizeDigits(source.Phone);

        var activeCandidates = await ApplyCustomerScope(
                _db.Customers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(current => !current.IsDeleted && current.Id != deletedCustomerId),
                session)
            .ToListAsync(ct);

        return activeCandidates
            .Select(current => new
            {
                Customer = current,
                Score =
                    (string.Equals(current.NameMatchKey, sourceNameKey, StringComparison.OrdinalIgnoreCase) ? 100 : 0) +
                    (!string.IsNullOrWhiteSpace(sourceBusinessDigits) && NormalizeDigits(current.BusinessNumber) == sourceBusinessDigits ? 70 : 0) +
                    (!string.IsNullOrWhiteSpace(sourcePhoneDigits) && NormalizeDigits(current.Phone) == sourcePhoneDigits ? 30 : 0)
            })
            .Where(current => current.Score > 0)
            .OrderByDescending(current => current.Score)
            .ThenBy(current => current.Customer.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .Select(current => new RecycleBinCustomerMergeCandidate
            {
                CustomerId = current.Customer.Id,
                Name = current.Customer.NameOriginal,
                BusinessNumber = current.Customer.BusinessNumber,
                Phone = current.Customer.Phone,
                ResponsibleOfficeCode = current.Customer.ResponsibleOfficeCode
            })
            .ToList();
    }

    public async Task<OfficeMutationResult> MergeDeletedCustomerIntoAsync(
        Guid sourceDeletedCustomerId,
        Guid targetCustomerId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (sourceDeletedCustomerId == Guid.Empty || targetCustomerId == Guid.Empty)
            return OfficeMutationResult.Denied("병합 대상 거래처를 확인할 수 없습니다.");
        if (sourceDeletedCustomerId == targetCustomerId)
            return OfficeMutationResult.Denied("같은 거래처로는 병합할 수 없습니다.");

        var source = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == sourceDeletedCustomerId, ct);
        if (source is null)
            return OfficeMutationResult.Missing("연결을 옮길 삭제 거래처를 찾을 수 없습니다.");
        if (!source.IsDeleted)
            return OfficeMutationResult.Denied("삭제된 거래처만 병합 정리할 수 있습니다.");
        if (!CanAccessCustomer(source, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 삭제 거래처를 정리할 수 없습니다.");

        var target = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == targetCustomerId, ct);
        if (target is null)
            return OfficeMutationResult.Missing("연결을 옮길 활성 거래처를 찾을 수 없습니다.");
        if (target.IsDeleted)
            return OfficeMutationResult.Denied("삭제된 거래처로는 연결을 옮길 수 없습니다.");
        if (!CanAccessCustomer(target, session))
            return OfficeMutationResult.Denied("권한이 없어 대상 거래처로 연결을 옮길 수 없습니다.");
        if (!CanWriteCustomerScope(session, target.ResponsibleOfficeCode, target.TenantCode))
            return OfficeMutationResult.Denied("권한이 없어 대상 거래처에 연결 데이터를 저장할 수 없습니다.");

        var now = DateTime.UtcNow;
        var movedContractCount = 0;
        var movedInvoiceCount = 0;
        var movedTransactionCount = 0;
        var movedRentalProfileCount = 0;
        var movedRentalAssetCount = 0;

        var targetHasActivePrimaryContract = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .AnyAsync(current => current.CustomerId == targetCustomerId && !current.IsDeleted && current.IsPrimary, ct);

        var contracts = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == sourceDeletedCustomerId)
            .ToListAsync(ct);
        foreach (var contract in contracts)
        {
            contract.CustomerId = targetCustomerId;
            if (contract.IsPrimary && targetHasActivePrimaryContract)
                contract.IsPrimary = false;
            MarkRecycleBinMutation(contract, now);
            movedContractCount++;
        }

        var invoices = await _db.Invoices
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == sourceDeletedCustomerId)
            .ToListAsync(ct);
        foreach (var invoice in invoices)
        {
            invoice.CustomerId = targetCustomerId;
            MarkRecycleBinMutation(invoice, now);
            movedInvoiceCount++;
        }

        var transactions = await _db.Transactions
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == sourceDeletedCustomerId)
            .ToListAsync(ct);
        foreach (var transaction in transactions)
        {
            transaction.CustomerId = targetCustomerId;
            MarkRecycleBinMutation(transaction, now);
            movedTransactionCount++;
        }

        var rentalProfiles = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == sourceDeletedCustomerId)
            .ToListAsync(ct);
        foreach (var profile in rentalProfiles)
        {
            profile.CustomerId = targetCustomerId;
            if (ShouldReplaceDisplayValue(profile.CustomerName, source.NameOriginal))
                profile.CustomerName = target.NameOriginal;
            if (ShouldReplaceDisplayValue(profile.RealCustomerName, source.NameOriginal))
                profile.RealCustomerName = target.NameOriginal;
            if (ShouldReplaceDisplayValue(profile.BillToCustomerName, source.NameOriginal))
                profile.BillToCustomerName = target.NameOriginal;
            if (ShouldReplaceDigits(profile.BusinessNumber, source.BusinessNumber))
                profile.BusinessNumber = target.BusinessNumber;
            MarkRecycleBinMutation(profile, now);
            movedRentalProfileCount++;
        }

        var rentalAssets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == sourceDeletedCustomerId)
            .ToListAsync(ct);
        foreach (var asset in rentalAssets)
        {
            asset.CustomerId = targetCustomerId;
            if (ShouldReplaceDisplayValue(asset.CustomerName, source.NameOriginal))
                asset.CustomerName = target.NameOriginal;
            if (ShouldReplaceDisplayValue(asset.CurrentCustomerName, source.NameOriginal))
                asset.CurrentCustomerName = target.NameOriginal;
            if (ShouldReplaceDisplayValue(asset.BillToCustomerName, source.NameOriginal))
                asset.BillToCustomerName = target.NameOriginal;
            MarkRecycleBinMutation(asset, now);
            movedRentalAssetCount++;
        }

        if (movedContractCount + movedInvoiceCount + movedTransactionCount + movedRentalProfileCount + movedRentalAssetCount == 0)
            return OfficeMutationResult.Denied("옮길 연결 데이터가 없습니다.");

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalCustomer),
            EntityId = sourceDeletedCustomerId.ToString("D"),
            Action = "RecycleBinMerge",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = string.Empty,
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                SourceCustomerId = sourceDeletedCustomerId,
                TargetCustomerId = targetCustomerId,
                movedContractCount,
                movedInvoiceCount,
                movedTransactionCount,
                movedRentalProfileCount,
                movedRentalAssetCount
            }),
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);
        _officeAccess.RevokeTemporaryCustomerAccess(session, sourceDeletedCustomerId);

        return OfficeMutationResult.Ok(
            sourceDeletedCustomerId,
            $"연결 이동을 완료했습니다. 계약서 {movedContractCount:N0}건, 전표 {movedInvoiceCount:N0}건, 거래내역 {movedTransactionCount:N0}건, 렌탈 청구 {movedRentalProfileCount:N0}건, 렌탈 자산 {movedRentalAssetCount:N0}건을 옮겼습니다.");
    }

    private async Task<RecycleBinDependencyInfo> GetCustomerRecycleBinDependencyInfoAsync(Guid customerId, SessionState session, CancellationToken ct)
    {
        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == customerId, ct);
        if (customer is null || !CanAccessCustomer(customer, session))
            return new RecycleBinDependencyInfo { CanPurge = false, Summary = "삭제 차단 사유를 확인할 거래처를 찾을 수 없습니다." };

        var invoiceCount = await _db.Invoices.IgnoreQueryFilters().CountAsync(current => current.CustomerId == customerId, ct);
        var transactionCount = await _db.Transactions.IgnoreQueryFilters().CountAsync(current => current.CustomerId == customerId, ct);
        var rentalProfileCount = await _db.RentalBillingProfiles.IgnoreQueryFilters().CountAsync(current => current.CustomerId == customerId, ct);
        var activeContractCount = await _db.CustomerContracts.IgnoreQueryFilters().CountAsync(current => current.CustomerId == customerId && !current.IsDeleted, ct);
        var rentalAssetCount = await _db.RentalAssets.IgnoreQueryFilters().CountAsync(current => current.CustomerId == customerId, ct);

        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "연결 전표", invoiceCount, "전표가 남아 있으면 거래처를 영구삭제할 수 없습니다.");
        AppendDependency(dependencies, "연결 거래내역", transactionCount, "거래내역이 남아 있으면 거래처를 영구삭제할 수 없습니다.");
        AppendDependency(dependencies, "연결 렌탈 청구 프로필", rentalProfileCount, "렌탈 청구 프로필이 남아 있으면 거래처를 영구삭제할 수 없습니다.");
        AppendDependency(dependencies, "활성 계약서", activeContractCount, "활성 계약서를 먼저 정리해야 합니다.");
        AppendDependency(dependencies, "연결 렌탈 자산", rentalAssetCount, "렌탈 자산 연결을 먼저 옮기거나 정리해야 합니다.");

        var blockingCount = invoiceCount + transactionCount + rentalProfileCount + activeContractCount + rentalAssetCount;
        return new RecycleBinDependencyInfo
        {
            CanPurge = blockingCount == 0,
            Summary = blockingCount == 0
                ? "이 거래처는 현재 영구삭제 가능합니다."
                : "연결된 전표/거래내역/렌탈 데이터가 남아 있어 영구삭제할 수 없습니다.",
            Dependencies = dependencies
        };
    }

    private async Task<RecycleBinDependencyInfo> GetInvoiceRecycleBinDependencyInfoAsync(Guid invoiceId, SessionState session, CancellationToken ct)
    {
        var invoice = await _db.Invoices.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(current => current.Id == invoiceId, ct);
        if (invoice is null || !CanAccessInvoice(invoice, session))
            return new RecycleBinDependencyInfo { CanPurge = false, Summary = "삭제 차단 사유를 확인할 전표를 찾을 수 없습니다." };

        var versionGroupId = invoice.VersionGroupId == Guid.Empty ? invoice.Id : invoice.VersionGroupId;
        var invoiceIds = await _db.Invoices.IgnoreQueryFilters()
            .Where(current => current.Id == invoice.Id || current.VersionGroupId == versionGroupId)
            .Select(current => current.Id)
            .Distinct()
            .ToListAsync(ct);

        var transactionCount = await _db.Transactions.IgnoreQueryFilters()
            .CountAsync(current => current.LinkedInvoiceId.HasValue && invoiceIds.Contains(current.LinkedInvoiceId.Value), ct);
        var activePaymentCount = await _db.Payments.IgnoreQueryFilters()
            .CountAsync(current => invoiceIds.Contains(current.InvoiceId) && !current.IsDeleted, ct);

        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "연결 거래내역", transactionCount, "거래내역을 먼저 정리해야 전표를 영구삭제할 수 있습니다.");
        AppendDependency(dependencies, "활성 수금/지급", activePaymentCount, "활성 수금/지급 기록이 남아 있으면 영구삭제할 수 없습니다.");

        var blockingCount = transactionCount + activePaymentCount;
        return new RecycleBinDependencyInfo
        {
            CanPurge = blockingCount == 0,
            Summary = blockingCount == 0
                ? "이 전표는 현재 영구삭제 가능합니다."
                : "연결된 거래내역 또는 수금/지급 기록이 남아 있어 영구삭제할 수 없습니다.",
            Dependencies = dependencies
        };
    }

    private async Task<RecycleBinDependencyInfo> GetPaymentRecycleBinDependencyInfoAsync(Guid paymentId, SessionState session, CancellationToken ct)
    {
        var payment = await _db.Payments.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(current => current.Id == paymentId, ct);
        if (payment is null)
            return new RecycleBinDependencyInfo { CanPurge = false, Summary = "삭제 차단 사유를 확인할 수금/지급 기록을 찾을 수 없습니다." };

        var invoice = await _db.Invoices.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(current => current.Id == payment.InvoiceId, ct);
        if (invoice is null || !CanAccessInvoice(invoice, session))
            return new RecycleBinDependencyInfo { CanPurge = false, Summary = "권한이 없어 삭제 차단 사유를 확인할 수 없습니다." };

        var dependencies = new List<RecycleBinDependencyItem>();

        return new RecycleBinDependencyInfo
        {
            CanPurge = true,
            Summary = "이 수금/지급 기록은 현재 영구삭제 가능합니다.",
            Dependencies = dependencies
        };
    }

    private async Task<RecycleBinDependencyInfo> GetTransactionRecycleBinDependencyInfoAsync(Guid transactionId, SessionState session, CancellationToken ct)
    {
        var transaction = await _db.Transactions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(current => current.Id == transactionId, ct);
        if (transaction is null || !CanAccessTransaction(transaction, session))
            return new RecycleBinDependencyInfo { CanPurge = false, Summary = "삭제 차단 사유를 확인할 거래내역을 찾을 수 없습니다." };

        var attachmentCount = await _db.TransactionAttachments.IgnoreQueryFilters().CountAsync(current => current.TransactionId == transactionId, ct);
        var linkedPaymentCount = await _db.Payments.IgnoreQueryFilters().CountAsync(current => current.Id == transactionId, ct);
        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "첨부 파일", attachmentCount, "영구삭제 시 첨부 파일도 함께 제거됩니다.");
        AppendDependency(dependencies, "연결 수금/지급", linkedPaymentCount, "같은 ID의 수금/지급 기록이 있으면 함께 제거됩니다.");

        return new RecycleBinDependencyInfo
        {
            CanPurge = true,
            Summary = linkedPaymentCount > 0 || attachmentCount > 0
                ? "영구삭제 시 연결 첨부/수금 정보를 함께 정리합니다."
                : "이 거래내역은 현재 영구삭제 가능합니다.",
            Dependencies = dependencies
        };
    }

    private async Task<RecycleBinDependencyInfo> GetContractRecycleBinDependencyInfoAsync(Guid contractId, SessionState session, CancellationToken ct)
    {
        var contract = await _db.CustomerContracts.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(current => current.Id == contractId, ct);
        if (contract is null)
            return new RecycleBinDependencyInfo { CanPurge = false, Summary = "삭제 차단 사유를 확인할 계약서를 찾을 수 없습니다." };
        var customer = await _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(current => current.Id == contract.CustomerId, ct);
        if (customer is null || !CanAccessCustomer(customer, session))
            return new RecycleBinDependencyInfo { CanPurge = false, Summary = "권한이 없어 삭제 차단 사유를 확인할 수 없습니다." };

        return new RecycleBinDependencyInfo
        {
            CanPurge = true,
            Summary = "이 계약서는 현재 영구삭제 가능합니다.",
            Dependencies = new List<RecycleBinDependencyItem>()
        };
    }

    private async Task<RecycleBinDependencyInfo> GetItemRecycleBinDependencyInfoAsync(Guid itemId, SessionState session, CancellationToken ct)
    {
        var item = await _db.Items.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(current => current.Id == itemId, ct);
        if (item is null || !CanWriteItemScope(item, session))
            return new RecycleBinDependencyInfo { CanPurge = false, Summary = "삭제 차단 사유를 확인할 품목을 찾을 수 없습니다." };

        var invoiceLineCount = await _db.InvoiceLines.IgnoreQueryFilters().CountAsync(current => current.ItemId == itemId, ct);
        var transferLineCount = await _db.InventoryTransferLines.IgnoreQueryFilters().CountAsync(current => current.ItemId == itemId, ct);
        var serialLedgerCount = await _db.SerialLedgers.IgnoreQueryFilters().CountAsync(current => current.ItemId == itemId, ct);

        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "전표 라인 참조", invoiceLineCount, "영구삭제 시 품목 연결만 해제됩니다.");
        AppendDependency(dependencies, "재고이동 라인 참조", transferLineCount, "영구삭제 시 품목 연결만 해제됩니다.");
        AppendDependency(dependencies, "시리얼 원장 참조", serialLedgerCount, "영구삭제 시 관련 원장 데이터도 함께 정리됩니다.");

        return new RecycleBinDependencyInfo
        {
            CanPurge = true,
            Summary = "이 품목은 현재 영구삭제 가능합니다. 필요한 참조 연결은 자동 정리됩니다.",
            Dependencies = dependencies
        };
    }

    private static void AppendDependency(ICollection<RecycleBinDependencyItem> items, string label, int count, string detail)
    {
        if (count <= 0)
            return;

        items.Add(new RecycleBinDependencyItem
        {
            Label = label,
            Count = count,
            Detail = detail
        });
    }

    private static void MarkRecycleBinMutation(LocalSyncEntity entity, DateTime now)
    {
        entity.IsDirty = true;
        entity.UpdatedAtUtc = now;
    }

    private static bool ShouldReplaceDisplayValue(string? currentValue, string? sourceName)
    {
        var current = (currentValue ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(current))
            return true;

        var source = (sourceName ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(source) && string.Equals(current, source, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool ShouldReplaceDigits(string? currentValue, string? sourceValue)
    {
        var currentDigits = NormalizeDigits(currentValue);
        if (string.IsNullOrWhiteSpace(currentDigits))
            return true;

        var sourceDigits = NormalizeDigits(sourceValue);
        return !string.IsNullOrWhiteSpace(sourceDigits) && string.Equals(currentDigits, sourceDigits, StringComparison.Ordinal);
    }

    private static string NormalizeDigits(string? value)
        => string.Concat((value ?? string.Empty).Where(char.IsDigit));
}
