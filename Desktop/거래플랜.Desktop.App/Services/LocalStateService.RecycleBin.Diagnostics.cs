using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

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
            RecycleBinEntityKind.CompanyProfile => await GetCompanyProfileRecycleBinDependencyInfoAsync(entityId, ct),
            RecycleBinEntityKind.PriceGradeOption => await GetPriceGradeOptionRecycleBinDependencyInfoAsync(entityId, ct),
            RecycleBinEntityKind.TradeTypeOption => await GetTradeTypeOptionRecycleBinDependencyInfoAsync(entityId, ct),
            RecycleBinEntityKind.ItemCategoryOption => await GetItemCategoryOptionRecycleBinDependencyInfoAsync(entityId, ct),
            RecycleBinEntityKind.RentalBillingProfile => await GetRentalBillingProfileRecycleBinDependencyInfoAsync(entityId, session, ct),
            RecycleBinEntityKind.RentalAsset => await GetRentalAssetRecycleBinDependencyInfoAsync(entityId, session, ct),
            RecycleBinEntityKind.RentalBillingLog => await GetRentalBillingLogRecycleBinDependencyInfoAsync(entityId, session, ct),
            RecycleBinEntityKind.InventoryTransfer => await GetInventoryTransferRecycleBinDependencyInfoAsync(entityId, session, ct),
            RecycleBinEntityKind.RentalManagementCompany => await GetRentalManagementCompanyRecycleBinDependencyInfoAsync(entityId, session, ct),
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

    private async Task<RecycleBinDependencyInfo> GetCompanyProfileRecycleBinDependencyInfoAsync(Guid profileId, CancellationToken ct)
    {
        var profile = await _db.CompanyProfiles.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(current => current.Id == profileId, ct);
        if (profile is null)
            return new RecycleBinDependencyInfo { CanPurge = false, Summary = "삭제 차단 사유를 확인할 회사설정을 찾을 수 없습니다." };

        var assignedSettingCount = await _db.Settings.AsNoTracking()
            .CountAsync(setting => setting.Key.StartsWith(CompanyProfileAssignmentPrefix) && setting.Value == profileId.ToString("D"), ct);
        var defaultOfficeCount = await _db.CompanyProfiles.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(current => current.Id != profileId && !current.IsDeleted && current.IsActive && string.Equals(current.OfficeCode, profile.OfficeCode, StringComparison.OrdinalIgnoreCase) && current.IsDefaultForOffice, ct);

        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "사용자 지정", assignedSettingCount, "사용자별 회사설정 연결을 먼저 해제해야 합니다.");
        AppendDependency(dependencies, "같은 지점 기본 회사설정", defaultOfficeCount, "같은 지점의 다른 기본 회사설정이 있으면 복원 시 기본 상태를 다시 검토해야 합니다.");

        return new RecycleBinDependencyInfo
        {
            CanPurge = assignedSettingCount == 0,
            Summary = assignedSettingCount == 0
                ? "이 회사설정은 현재 영구삭제 가능합니다."
                : "사용자별 회사설정 연결이 남아 있어 영구삭제할 수 없습니다.",
            Dependencies = dependencies
        };
    }

    private async Task<RecycleBinDependencyInfo> GetPriceGradeOptionRecycleBinDependencyInfoAsync(Guid optionId, CancellationToken ct)
    {
        var option = await _db.PriceGradeOptions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(current => current.Id == optionId, ct);
        if (option is null)
            return new RecycleBinDependencyInfo { CanPurge = false, Summary = "삭제 차단 사유를 확인할 가격등급을 찾을 수 없습니다." };

        var customerCount = await _db.Customers.IgnoreQueryFilters().CountAsync(current => current.PriceGrade == option.Name, ct);
        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "연결 거래처", customerCount, "이 가격등급을 사용하는 거래처가 있으면 영구삭제할 수 없습니다.");

        return new RecycleBinDependencyInfo
        {
            CanPurge = customerCount == 0,
            Summary = customerCount == 0
                ? "이 가격등급은 현재 영구삭제 가능합니다."
                : "연결 거래처가 남아 있어 영구삭제할 수 없습니다.",
            Dependencies = dependencies
        };
    }

    private async Task<RecycleBinDependencyInfo> GetTradeTypeOptionRecycleBinDependencyInfoAsync(Guid optionId, CancellationToken ct)
    {
        var option = await _db.TradeTypeOptions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(current => current.Id == optionId, ct);
        if (option is null)
            return new RecycleBinDependencyInfo { CanPurge = false, Summary = "삭제 차단 사유를 확인할 거래구분을 찾을 수 없습니다." };

        var customerCount = await _db.Customers.IgnoreQueryFilters().CountAsync(current => current.TradeType == option.Name, ct);
        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "연결 거래처", customerCount, "이 거래구분을 사용하는 거래처가 있으면 영구삭제할 수 없습니다.");

        return new RecycleBinDependencyInfo
        {
            CanPurge = customerCount == 0,
            Summary = customerCount == 0
                ? "이 거래구분은 현재 영구삭제 가능합니다."
                : "연결 거래처가 남아 있어 영구삭제할 수 없습니다.",
            Dependencies = dependencies
        };
    }

    private async Task<RecycleBinDependencyInfo> GetItemCategoryOptionRecycleBinDependencyInfoAsync(Guid optionId, CancellationToken ct)
    {
        var option = await _db.ItemCategoryOptions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(current => current.Id == optionId, ct);
        if (option is null)
            return new RecycleBinDependencyInfo { CanPurge = false, Summary = "삭제 차단 사유를 확인할 품목분류를 찾을 수 없습니다." };

        var optionKey = RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name);
        var itemCount = (await _db.Items.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct))
            .Count(item => string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(item.CategoryName), optionKey, StringComparison.OrdinalIgnoreCase));
        var rentalAssetCount = (await _db.RentalAssets.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct))
            .Count(asset => string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.ItemCategoryName), optionKey, StringComparison.OrdinalIgnoreCase));

        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "연결 품목", itemCount, "이 품목분류를 사용하는 품목이 있으면 영구삭제할 수 없습니다.");
        AppendDependency(dependencies, "연결 렌탈 자산", rentalAssetCount, "이 품목분류를 사용하는 렌탈 자산이 있으면 영구삭제할 수 없습니다.");

        return new RecycleBinDependencyInfo
        {
            CanPurge = itemCount + rentalAssetCount == 0,
            Summary = itemCount + rentalAssetCount == 0
                ? "이 품목분류는 현재 영구삭제 가능합니다."
                : "연결 품목 또는 렌탈 자산이 남아 있어 영구삭제할 수 없습니다.",
            Dependencies = dependencies
        };
    }

    private async Task<RecycleBinDependencyInfo> GetRentalBillingProfileRecycleBinDependencyInfoAsync(Guid profileId, SessionState session, CancellationToken ct)
    {
        var profile = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == profileId, ct);
        if (profile is null || !CanAccessRental(
                string.IsNullOrWhiteSpace(profile.ResponsibleOfficeCode)
                    ? profile.ManagementCompanyCode
                    : profile.ResponsibleOfficeCode,
                session))
        {
            return new RecycleBinDependencyInfo
            {
                CanPurge = false,
                Summary = "삭제 차단 사유를 확인할 렌탈 청구 프로필을 찾을 수 없습니다."
            };
        }

        var invoiceCount = await _db.Invoices.IgnoreQueryFilters()
            .CountAsync(current => current.LinkedRentalBillingProfileId == profileId, ct);
        var transactionCount = await _db.Transactions.IgnoreQueryFilters()
            .CountAsync(current => current.LinkedRentalBillingProfileId == profileId, ct);
        var assetCount = await _db.RentalAssets.IgnoreQueryFilters()
            .CountAsync(current => current.BillingProfileId == profileId, ct);
        var logCount = await _db.RentalBillingLogs.IgnoreQueryFilters()
            .CountAsync(current => current.BillingProfileId == profileId, ct);

        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "연결 전표", invoiceCount, "연결된 전표가 남아 있으면 렌탈 청구 프로필을 영구삭제할 수 없습니다.");
        AppendDependency(dependencies, "연결 거래내역", transactionCount, "연결된 거래내역이 남아 있으면 렌탈 청구 프로필을 영구삭제할 수 없습니다.");
        AppendDependency(dependencies, "연결 렌탈 자산", assetCount, "영구삭제 시 연결 자산은 자동으로 프로필 연결이 해제됩니다.");
        AppendDependency(dependencies, "연결 청구로그", logCount, "영구삭제 시 연결 청구로그는 함께 정리됩니다.");

        var blockingCount = invoiceCount + transactionCount;
        return new RecycleBinDependencyInfo
        {
            CanPurge = blockingCount == 0,
            Summary = blockingCount == 0
                ? "이 렌탈 청구 프로필은 현재 영구삭제 가능합니다. 연결 자산/청구로그는 자동 정리됩니다."
                : "연결된 전표 또는 거래내역이 남아 있어 렌탈 청구 프로필을 영구삭제할 수 없습니다.",
            Dependencies = dependencies
        };
    }

    private async Task<RecycleBinDependencyInfo> GetRentalAssetRecycleBinDependencyInfoAsync(Guid assetId, SessionState session, CancellationToken ct)
    {
        var asset = await _db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == assetId, ct);
        if (asset is null || !CanAccessRental(
                string.IsNullOrWhiteSpace(asset.ResponsibleOfficeCode)
                    ? asset.ManagementCompanyCode
                    : asset.ResponsibleOfficeCode,
                session))
        {
            return new RecycleBinDependencyInfo
            {
                CanPurge = false,
                Summary = "삭제 차단 사유를 확인할 렌탈 자산을 찾을 수 없습니다."
            };
        }

        var directProfileCount = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .CountAsync(current => current.Id == asset.BillingProfileId, ct);
        var templateProfileCount = (await _db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync(ct))
            .Count(current => BillingTemplateContainsAssetId(current.BillingTemplateJson, assetId));

        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "현재 연결 프로필", directProfileCount, "현재 연결된 렌탈 청구 프로필이 있으면 영구삭제 시 자동으로 연결이 해제됩니다.");
        AppendDependency(dependencies, "청구항목 포함 프로필", templateProfileCount, "청구항목 내부 포함 장비에서 이 자산을 참조하는 프로필은 자동으로 정리됩니다.");

        return new RecycleBinDependencyInfo
        {
            CanPurge = true,
            Summary = templateProfileCount + directProfileCount > 0
                ? "이 렌탈 자산은 현재 영구삭제 가능합니다. 연결된 청구 프로필 참조는 자동 정리됩니다."
                : "이 렌탈 자산은 현재 영구삭제 가능합니다.",
            Dependencies = dependencies
        };
    }

    private async Task<RecycleBinDependencyInfo> GetRentalBillingLogRecycleBinDependencyInfoAsync(Guid logId, SessionState session, CancellationToken ct)
    {
        var log = await _db.RentalBillingLogs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == logId, ct);
        if (log is null || !CanAccessRental(log.ResponsibleOfficeCode, session))
        {
            return new RecycleBinDependencyInfo
            {
                CanPurge = false,
                Summary = "삭제 차단 사유를 확인할 렌탈 청구로그를 찾을 수 없습니다."
            };
        }

        var linkedProfileCount = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .CountAsync(current => current.Id == log.BillingProfileId, ct);

        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "연결 렌탈 청구 프로필", linkedProfileCount, "청구로그는 연결 프로필과 독립적으로 영구삭제할 수 있습니다.");

        return new RecycleBinDependencyInfo
        {
            CanPurge = true,
            Summary = "이 렌탈 청구로그는 현재 영구삭제 가능합니다.",
            Dependencies = dependencies
        };
    }

    private async Task<RecycleBinDependencyInfo> GetInventoryTransferRecycleBinDependencyInfoAsync(Guid transferId, SessionState session, CancellationToken ct)
    {
        var transfer = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(current => current.Lines)
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == transferId, ct);
        if (transfer is null || !CanAccessInventoryTransferForRecycleBin(transfer, session))
        {
            return new RecycleBinDependencyInfo
            {
                CanPurge = false,
                Summary = "삭제 차단 사유를 확인할 재고이동을 찾을 수 없습니다."
            };
        }

        var lineCount = transfer.Lines.Count(line => !line.IsDeleted);
        var receiveEvidence = string.IsNullOrWhiteSpace(transfer.ReceiveEvidencePath) ? 0 : 1;
        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "이동 라인", lineCount, "영구삭제 시 재고이동 라인도 함께 제거됩니다.");
        AppendDependency(dependencies, "수령 증빙", receiveEvidence, "영구삭제 시 수령 증빙 경로도 함께 정리됩니다.");

        return new RecycleBinDependencyInfo
        {
            CanPurge = true,
            Summary = lineCount > 0 || receiveEvidence > 0
                ? "이 재고이동은 현재 영구삭제 가능합니다. 연결 라인과 증빙도 함께 정리됩니다."
                : "이 재고이동은 현재 영구삭제 가능합니다.",
            Dependencies = dependencies
        };
    }

    private async Task<RecycleBinDependencyInfo> GetRentalManagementCompanyRecycleBinDependencyInfoAsync(Guid companyId, SessionState session, CancellationToken ct)
    {
        var company = await _db.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(current => current.Id == companyId, ct);
        if (company is null || !CanManageRentalSettingsForRecycleBin(session))
        {
            return new RecycleBinDependencyInfo
            {
                CanPurge = false,
                Summary = "삭제 차단 사유를 확인할 렌탈 관리업체를 찾을 수 없습니다."
            };
        }

        var billingProfileCount = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .CountAsync(current => current.ManagementCompanyCode == company.Code, ct);
        var rentalAssetCount = await _db.RentalAssets.IgnoreQueryFilters()
            .CountAsync(current => current.ManagementCompanyCode == company.Code, ct);

        var dependencies = new List<RecycleBinDependencyItem>();
        AppendDependency(dependencies, "연결 렌탈 청구프로필", billingProfileCount, "이 관리업체를 사용하는 렌탈 청구프로필이 있으면 영구삭제할 수 없습니다.");
        AppendDependency(dependencies, "연결 렌탈 자산", rentalAssetCount, "이 관리업체를 사용하는 렌탈 자산이 있으면 영구삭제할 수 없습니다.");

        return new RecycleBinDependencyInfo
        {
            CanPurge = billingProfileCount + rentalAssetCount == 0,
            Summary = billingProfileCount + rentalAssetCount == 0
                ? "이 렌탈 관리업체는 현재 영구삭제 가능합니다."
                : "연결 렌탈 데이터가 남아 있어 영구삭제할 수 없습니다.",
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

    private static bool CanManageRentalSettingsForRecycleBin(SessionState? session)
        => session is not null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.RentalSettingsEdit));

    private bool CanAccessInventoryTransferForRecycleBin(LocalInventoryTransfer transfer, SessionState session)
    {
        if (HasFullAccess(session))
            return true;

        var readableOffices = GetReadableOfficeCodes(session);
        var fromOfficeCode = ResolveOfficeCodeFromWarehouseCode(transfer.FromWarehouseCode);
        var toOfficeCode = ResolveOfficeCodeFromWarehouseCode(transfer.ToWarehouseCode);
        return readableOffices.Contains(fromOfficeCode) || readableOffices.Contains(toOfficeCode);
    }

    private static bool CanAccessRental(string? officeCode, SessionState session)
    {
        if (CanManageAllRentalScope(session))
            return true;

        var normalizedOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet);
        return GetReadableOfficeCodes(session).Contains(normalizedOfficeCode);
    }
}
