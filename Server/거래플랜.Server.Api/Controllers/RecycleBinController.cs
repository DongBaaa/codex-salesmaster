using System.Text.Json;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
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
    private readonly ICentralFileStorage _fileStorage;
    private readonly InventoryLedgerService _inventoryLedgerService;
    private readonly InvoiceStockSnapshotService _invoiceStockSnapshotService;
    private readonly RentalSettlementRecalculationService _rentalSettlementRecalculationService;

    public RecycleBinController(
        AppDbContext dbContext,
        OfficeScopeService officeScopeService,
        ICentralFileStorage fileStorage,
        InventoryLedgerService inventoryLedgerService,
        InvoiceStockSnapshotService invoiceStockSnapshotService,
        RentalSettlementRecalculationService rentalSettlementRecalculationService)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
        _fileStorage = fileStorage;
        _inventoryLedgerService = inventoryLedgerService;
        _invoiceStockSnapshotService = invoiceStockSnapshotService;
        _rentalSettlementRecalculationService = rentalSettlementRecalculationService;
    }

    [HttpGet]
    [Authorize(Policy = PermissionNames.DataBackupRestore)]
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
                DeletedAtUtc = customer.UpdatedAtUtc,
                Revision = customer.Revision
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
                    DeletedAtUtc = contract.UpdatedAtUtc,
                Revision = contract.Revision
                };
            }));
        }

        if (ShouldIncludeKind(normalizedKind, "item"))
        {
            var deletedItems = (await _officeScopeService.ApplyItemScope(_dbContext.Items
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(item => item.IsDeleted))
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ToListAsync(cancellationToken))
                .Where(item => _officeScopeService.CanWriteOfficeForItems(item.OfficeCode, item.TenantCode))
                .ToList();

            entries.AddRange(deletedItems.Select(item => new RecycleBinEntryDto
            {
                EntityId = item.Id,
                Kind = "item",
                KindText = "품목",
                Title = item.NameOriginal,
                Subtitle = JoinSegments(item.SpecificationOriginal, item.Unit),
                Detail = JoinSegments(item.SerialNumber, item.MaterialNumber, item.Notes),
                DeletedAtUtc = item.UpdatedAtUtc,
                Revision = item.Revision
            }));
        }

        var canManageSharedSettings = await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken);

        if (canManageSharedSettings && ShouldIncludeKind(normalizedKind, "company-profile"))
        {
            var deletedCompanyProfiles = await _dbContext.CompanyProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => profile.IsDeleted)
                .OrderByDescending(profile => profile.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            entries.AddRange(deletedCompanyProfiles.Select(profile => new RecycleBinEntryDto
            {
                EntityId = profile.Id,
                Kind = "company-profile",
                KindText = "회사설정",
                Title = string.IsNullOrWhiteSpace(profile.TradeName) ? "(회사설정)" : profile.TradeName,
                Subtitle = JoinSegments(profile.BusinessNumber, profile.Representative),
                Detail = JoinSegments(profile.ContactNumber, profile.Email, profile.Address),
                DeletedAtUtc = profile.UpdatedAtUtc,
                Revision = profile.Revision
            }));
        }

        if (canManageSharedSettings && ShouldIncludeKind(normalizedKind, "customer-category"))
        {
            var deletedCustomerCategories = await _dbContext.CustomerCategories
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(category => category.IsDeleted)
                .OrderByDescending(category => category.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            entries.AddRange(deletedCustomerCategories.Select(category => new RecycleBinEntryDto
            {
                EntityId = category.Id,
                Kind = "customer-category",
                KindText = "고객분류",
                Title = category.Name,
                Subtitle = category.IsSystemDefault ? "기본 고객분류" : "사용자 고객분류",
                Detail = string.Empty,
                DeletedAtUtc = category.UpdatedAtUtc,
                Revision = category.Revision
            }));
        }

        if (canManageSharedSettings && ShouldIncludeKind(normalizedKind, "price-grade-option"))
        {
            var deletedPriceGradeOptions = await _dbContext.PriceGradeOptions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(option => option.IsDeleted)
                .OrderByDescending(option => option.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            entries.AddRange(deletedPriceGradeOptions.Select(option => new RecycleBinEntryDto
            {
                EntityId = option.Id,
                Kind = "price-grade-option",
                KindText = "가격등급",
                Title = option.Name,
                Subtitle = option.PriceSource,
                Detail = option.IsSystemDefault ? "기본 가격등급" : "사용자 가격등급",
                DeletedAtUtc = option.UpdatedAtUtc,
                Revision = option.Revision
            }));
        }

        if (canManageSharedSettings && ShouldIncludeKind(normalizedKind, "trade-type-option"))
        {
            var deletedTradeTypeOptions = await _dbContext.TradeTypeOptions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(option => option.IsDeleted)
                .OrderByDescending(option => option.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            entries.AddRange(deletedTradeTypeOptions.Select(option => new RecycleBinEntryDto
            {
                EntityId = option.Id,
                Kind = "trade-type-option",
                KindText = "거래구분",
                Title = option.Name,
                Subtitle = JoinSegments(option.AllowsSales ? "매출" : null, option.AllowsPurchase ? "매입" : null),
                Detail = option.IsSystemDefault ? "기본 거래구분" : "사용자 거래구분",
                DeletedAtUtc = option.UpdatedAtUtc,
                Revision = option.Revision
            }));
        }

        if (canManageSharedSettings && ShouldIncludeKind(normalizedKind, "item-category-option"))
        {
            var deletedItemCategoryOptions = await _dbContext.ItemCategoryOptions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(option => option.IsDeleted)
                .OrderByDescending(option => option.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            entries.AddRange(deletedItemCategoryOptions.Select(option => new RecycleBinEntryDto
            {
                EntityId = option.Id,
                Kind = "item-category-option",
                KindText = "품목분류",
                Title = option.Name,
                Subtitle = option.IsSystemDefault ? "기본 품목분류" : "사용자 품목분류",
                Detail = option.SortOrder != 0 ? $"정렬순서 {option.SortOrder}" : string.Empty,
                DeletedAtUtc = option.UpdatedAtUtc,
                Revision = option.Revision
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
                    DeletedAtUtc = invoice.UpdatedAtUtc,
                Revision = invoice.Revision
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
                    DeletedAtUtc = payment.UpdatedAtUtc,
                Revision = payment.Revision
                };
            }));
        }

        if (ShouldIncludeKind(normalizedKind, "transaction"))
        {
            var deletedTransactions = await _officeScopeService.ApplyTransactionScope(_dbContext.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(transaction => transaction.IsDeleted))
                .OrderByDescending(transaction => transaction.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            var customerMap = await _dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => deletedTransactions.Select(transaction => transaction.CustomerId).Contains(customer.Id))
                .ToDictionaryAsync(customer => customer.Id, customer => customer.NameOriginal, cancellationToken);

            entries.AddRange(deletedTransactions.Select(transaction =>
            {
                customerMap.TryGetValue(transaction.CustomerId, out var customerName);
                var totalAmount = transaction.ReceiptTotal > 0m
                    ? transaction.ReceiptTotal
                    : transaction.PaymentTotal;

                return new RecycleBinEntryDto
                {
                    EntityId = transaction.Id,
                    Kind = "transaction",
                    KindText = "거래내역",
                    Title = $"{customerName ?? "(삭제된 거래처)"} · {GetTransactionKindLabel(transaction.TransactionKind)}",
                    Subtitle = JoinSegments(
                        transaction.TransactionDate.ToString("yyyy-MM-dd"),
                        totalAmount > 0m ? $"{totalAmount:N0}원" : null),
                    Detail = JoinSegments(transaction.Note, transaction.Memo),
                    DeletedAtUtc = transaction.UpdatedAtUtc,
                Revision = transaction.Revision
                };
            }));
        }

        if (ShouldIncludeKind(normalizedKind, "rental-billing-profile"))
        {
            var deletedProfiles = await _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => profile.IsDeleted))
                .OrderByDescending(profile => profile.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            entries.AddRange(deletedProfiles.Select(profile => new RecycleBinEntryDto
            {
                EntityId = profile.Id,
                Kind = "rental-billing-profile",
                KindText = "렌탈 청구프로필",
                Title = string.IsNullOrWhiteSpace(profile.CustomerName) ? "(거래처 미상)" : profile.CustomerName,
                Subtitle = JoinSegments(profile.InstallSiteName, profile.ItemName),
                Detail = JoinSegments(
                    string.IsNullOrWhiteSpace(profile.BusinessNumber) ? null : $"사업자번호 {profile.BusinessNumber}",
                    string.IsNullOrWhiteSpace(profile.BillingType) ? null : $"청구유형 {profile.BillingType}",
                    profile.MonthlyAmount > 0m ? $"월기준금액 {profile.MonthlyAmount:N0}원" : null),
                DeletedAtUtc = profile.UpdatedAtUtc,
                Revision = profile.Revision
            }));
        }

        if (ShouldIncludeKind(normalizedKind, "rental-asset"))
        {
            var deletedAssets = await _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => asset.IsDeleted))
                .OrderByDescending(asset => asset.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            entries.AddRange(deletedAssets.Select(asset => new RecycleBinEntryDto
            {
                EntityId = asset.Id,
                Kind = "rental-asset",
                KindText = "렌탈 자산",
                Title = string.IsNullOrWhiteSpace(asset.ManagementNumber)
                    ? string.IsNullOrWhiteSpace(asset.ItemName) ? "(렌탈 자산)" : asset.ItemName
                    : $"{asset.ManagementNumber} · {asset.ItemName}".Trim(),
                Subtitle = JoinSegments(
                    asset.CustomerName,
                    string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation),
                Detail = JoinSegments(
                    string.IsNullOrWhiteSpace(asset.MachineNumber) ? null : $"기계번호 {asset.MachineNumber}",
                    string.IsNullOrWhiteSpace(asset.AssetStatus) ? null : $"상태 {asset.AssetStatus}",
                    asset.MonthlyFee > 0m ? $"월요금 {asset.MonthlyFee:N0}원" : null),
                DeletedAtUtc = asset.UpdatedAtUtc,
                Revision = asset.Revision
            }));
        }

        if (ShouldIncludeKind(normalizedKind, "rental-billing-log"))
        {
            var deletedLogs = await _officeScopeService.ApplyRentalBillingLogScope(_dbContext.RentalBillingLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(log => log.IsDeleted))
                .OrderByDescending(log => log.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            var deletedProfileIds = deletedLogs
                .Select(log => log.BillingProfileId)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            var profileMap = deletedProfileIds.Count == 0
                ? new Dictionary<Guid, RentalBillingProfile>()
                : await _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(profile => deletedProfileIds.Contains(profile.Id)))
                    .ToDictionaryAsync(profile => profile.Id, cancellationToken);

            entries.AddRange(deletedLogs.Select(log =>
            {
                profileMap.TryGetValue(log.BillingProfileId, out var profile);
                var title = profile is null
                    ? $"청구로그 {log.BillingYearMonth}"
                    : $"{(string.IsNullOrWhiteSpace(profile.CustomerName) ? "(거래처 미상)" : profile.CustomerName)} · {log.BillingYearMonth}";
                return new RecycleBinEntryDto
                {
                    EntityId = log.Id,
                    Kind = "rental-billing-log",
                    KindText = "렌탈 청구로그",
                    Title = title,
                    Subtitle = JoinSegments(
                        profile?.CustomerName,
                        log.ScheduledDate.ToString("yyyy-MM-dd"),
                        string.IsNullOrWhiteSpace(log.Status) ? null : log.Status),
                    Detail = JoinSegments(
                        log.BilledAmount > 0m ? $"청구금액 {log.BilledAmount:N0}원" : null,
                        string.IsNullOrWhiteSpace(log.Note) ? null : log.Note),
                    DeletedAtUtc = log.UpdatedAtUtc,
                Revision = log.Revision
                };
            }));
        }

        if (ShouldIncludeKind(normalizedKind, "inventory-transfer"))
        {
            var deletedTransfers = await _officeScopeService.ApplyInventoryTransferScope(_dbContext.InventoryTransfers
                .IgnoreQueryFilters()
                .Include(transfer => transfer.Lines.Where(line => !line.IsDeleted))
                .AsNoTracking()
                .Where(transfer => transfer.IsDeleted))
                .OrderByDescending(transfer => transfer.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            entries.AddRange(deletedTransfers.Select(transfer => new RecycleBinEntryDto
            {
                EntityId = transfer.Id,
                Kind = "inventory-transfer",
                KindText = "재고이동",
                Title = string.IsNullOrWhiteSpace(transfer.TransferNumber) ? "(재고이동)" : transfer.TransferNumber,
                Subtitle = JoinSegments(transfer.TransferDate.ToString("yyyy-MM-dd"), transfer.TransferStatus),
                Detail = JoinSegments(
                    $"{transfer.FromWarehouseCode} → {transfer.ToWarehouseCode}",
                    transfer.Lines.Count > 0 ? $"라인 {transfer.Lines.Count:N0}건" : null,
                    transfer.Memo),
                DeletedAtUtc = transfer.UpdatedAtUtc,
                Revision = transfer.Revision
            }));
        }

        if (ShouldIncludeKind(normalizedKind, "rental-management-company"))
        {
            var deletedManagementCompanies = await _officeScopeService.ApplyRentalManagementCompanyScope(_dbContext.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(company => company.IsDeleted))
                .OrderByDescending(company => company.UpdatedAtUtc)
                .ToListAsync(cancellationToken);

            entries.AddRange(deletedManagementCompanies.Select(company => new RecycleBinEntryDto
            {
                EntityId = company.Id,
                Kind = "rental-management-company",
                KindText = "렌탈 관리업체",
                Title = string.IsNullOrWhiteSpace(company.Name) ? company.Code : company.Name,
                Subtitle = company.Code,
                Detail = company.IsSystemDefault ? "기본 렌탈 관리업체" : "사용자 렌탈 관리업체",
                DeletedAtUtc = company.UpdatedAtUtc,
                Revision = company.Revision
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
    [Authorize(Policy = PermissionNames.DataBackupRestore)]
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
            var mutation = await TryRecycleBinMutationAsync(
                () => RestoreCoreAsync(target, cancellationToken),
                "복원 처리 중 오류가 발생했습니다.");
            result.Messages.Add(mutation.Message);
            result.Results.Add(new RecycleBinMutationItemResultDto
            {
                EntityId = target.EntityId,
                Kind = NormalizeKind(target.Kind),
                Success = mutation.Success,
                Message = mutation.Message
            });
            if (mutation.Success)
                result.SucceededCount++;
        }

        return Ok(result);
    }

    [HttpPost("purge")]
    [Authorize(Policy = PermissionNames.DataBackupRestore)]
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
            var mutation = await TryRecycleBinMutationAsync(
                () => PurgeCoreAsync(target, cancellationToken),
                "영구삭제 처리 중 오류가 발생했습니다.");
            result.Messages.Add(mutation.Message);
            result.Results.Add(new RecycleBinMutationItemResultDto
            {
                EntityId = target.EntityId,
                Kind = NormalizeKind(target.Kind),
                Success = mutation.Success,
                Message = mutation.Message
            });
            if (mutation.Success)
                result.SucceededCount++;
        }

        return Ok(result);
    }

    private static async Task<(bool Success, string Message)> TryRecycleBinMutationAsync(
        Func<Task<(bool Success, string Message)>> mutation,
        string fallbackMessage)
    {
        try
        {
            return await mutation();
        }
        catch (DbUpdateException ex)
        {
            return (false, $"{fallbackMessage} {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"{fallbackMessage} {ex.Message}");
        }
    }

    private async Task<(bool Success, string Message)> RestoreCoreAsync(
        RecycleBinMutationTargetDto target,
        CancellationToken cancellationToken)
    {
        return NormalizeKind(target.Kind) switch
        {
            "customer" => await RestoreCustomerAsync(target, cancellationToken),
            "contract" => await RestoreContractAsync(target, cancellationToken),
            "item" => await RestoreItemAsync(target, cancellationToken),
            "company-profile" => await RestoreCompanyProfileAsync(target, cancellationToken),
            "customer-category" => await RestoreCustomerCategoryAsync(target, cancellationToken),
            "price-grade-option" => await RestorePriceGradeOptionAsync(target, cancellationToken),
            "trade-type-option" => await RestoreTradeTypeOptionAsync(target, cancellationToken),
            "item-category-option" => await RestoreItemCategoryOptionAsync(target, cancellationToken),
            "invoice" => await RestoreInvoiceAsync(target, cancellationToken),
            "payment" => await RestorePaymentAsync(target, cancellationToken),
            "transaction" => await RestoreTransactionAsync(target, cancellationToken),
            "inventory-transfer" => await RestoreInventoryTransferAsync(target, cancellationToken),
            "rental-management-company" => await RestoreRentalManagementCompanyAsync(target, cancellationToken),
            "rental-billing-profile" => await RestoreRentalBillingProfileAsync(target, cancellationToken),
            "rental-asset" => await RestoreRentalAssetAsync(target, cancellationToken),
            "rental-billing-log" => await RestoreRentalBillingLogAsync(target, cancellationToken),
            _ => (false, $"지원하지 않는 휴지통 종류입니다: {target.Kind}")
        };
    }

    private async Task<(bool Success, string Message)> PurgeCoreAsync(
        RecycleBinMutationTargetDto target,
        CancellationToken cancellationToken)
    {
        return NormalizeKind(target.Kind) switch
        {
            "customer" => await PurgeCustomerAsync(target, cancellationToken),
            "contract" => await PurgeContractAsync(target, cancellationToken),
            "item" => await PurgeItemAsync(target, cancellationToken),
            "company-profile" => await PurgeCompanyProfileAsync(target, cancellationToken),
            "customer-category" => await PurgeCustomerCategoryAsync(target, cancellationToken),
            "price-grade-option" => await PurgePriceGradeOptionAsync(target, cancellationToken),
            "trade-type-option" => await PurgeTradeTypeOptionAsync(target, cancellationToken),
            "item-category-option" => await PurgeItemCategoryOptionAsync(target, cancellationToken),
            "invoice" => await PurgeInvoiceAsync(target, cancellationToken),
            "payment" => await PurgePaymentAsync(target, cancellationToken),
            "transaction" => await PurgeTransactionAsync(target, cancellationToken),
            "inventory-transfer" => await PurgeInventoryTransferAsync(target, cancellationToken),
            "rental-management-company" => await PurgeRentalManagementCompanyAsync(target, cancellationToken),
            "rental-billing-profile" => await PurgeRentalBillingProfileAsync(target, cancellationToken),
            "rental-asset" => await PurgeRentalAssetAsync(target, cancellationToken),
            "rental-billing-log" => await PurgeRentalBillingLogAsync(target, cancellationToken),
            _ => (false, $"지원하지 않는 휴지통 종류입니다: {target.Kind}")
        };
    }

    private async Task<(bool Success, string Message)> RestoreCustomerAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var customerId = target.EntityId;
        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == customerId, cancellationToken);
        if (customer is null)
            return (false, "복원할 거래처를 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
            return (false, "현재 계정으로 복원할 수 없는 거래처입니다.");
        if (!customer.IsDeleted)
            return (true, "이미 활성 상태인 거래처입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(customer, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var deletedAtUtc = customer.UpdatedAtUtc;
        var restoredContractCount = await RestoreContractsDeletedWithCustomerAsync(customer, deletedAtUtc, cancellationToken);

        customer.IsDeleted = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, restoredContractCount > 0
            ? $"거래처를 복원하고 함께 삭제된 계약서 {restoredContractCount:N0}건도 복원했습니다."
            : "거래처를 복원했습니다.");
    }

    private async Task<int> RestoreContractsDeletedWithCustomerAsync(
        Customer customer,
        DateTime customerDeletedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanWriteOfficeForContracts(
                customer.ResponsibleOfficeCode,
                customer.TenantCode,
                customer.OfficeCode))
            return 0;

        var contracts = await _dbContext.CustomerContracts
            .IgnoreQueryFilters()
            .Where(contract =>
                contract.CustomerId == customer.Id &&
                contract.IsDeleted &&
                contract.UpdatedAtUtc == customerDeletedAtUtc)
            .ToListAsync(cancellationToken);
        if (contracts.Count == 0)
            return 0;

        if (contracts.Any(contract => contract.IsPrimary))
        {
            var restoringIds = contracts.Select(contract => contract.Id).ToList();
            var otherActivePrimaryContracts = await _dbContext.CustomerContracts
                .IgnoreQueryFilters()
                .Where(contract =>
                    contract.CustomerId == customer.Id &&
                    !restoringIds.Contains(contract.Id) &&
                    !contract.IsDeleted &&
                    contract.IsPrimary)
                .ToListAsync(cancellationToken);
            foreach (var other in otherActivePrimaryContracts)
                other.IsPrimary = false;
        }

        foreach (var contract in contracts)
            contract.IsDeleted = false;

        return contracts.Count;
    }

    private async Task<(bool Success, string Message)> RestoreContractAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var contractId = target.EntityId;
        var contract = await _dbContext.CustomerContracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contractId, cancellationToken);
        if (contract is null)
            return (false, "복원할 계약서를 찾을 수 없습니다.");

        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contract.CustomerId, cancellationToken);
        if (customer is null)
            return (false, "계약서와 연결된 거래처를 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForContracts(
                customer.ResponsibleOfficeCode,
                customer.TenantCode,
                customer.OfficeCode))
            return (false, "현재 계정으로 복원할 수 없는 계약서입니다.");
        if (!contract.IsDeleted)
            return (true, "이미 활성 상태인 계약서입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(contract, target);
        if (!revisionCheck.Success)
            return revisionCheck;

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

    private async Task<(bool Success, string Message)> RestoreItemAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var itemId = target.EntityId;
        var item = await _dbContext.Items
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == itemId, cancellationToken);
        if (item is null)
            return (false, "복원할 품목을 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForItems(item.OfficeCode, item.TenantCode))
            return (false, "현재 계정으로 복원할 수 없는 품목입니다.");
        if (!item.IsDeleted)
            return (true, "이미 활성 상태인 품목입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(item, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        item.IsDeleted = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "품목을 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestoreCompanyProfileAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var profileId = target.EntityId;
        if (!await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken))
            return (false, "현재 계정으로 복원할 수 없는 회사설정입니다.");

        var profile = await _dbContext.CompanyProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, cancellationToken);
        if (profile is null)
            return (false, "복원할 회사설정을 찾을 수 없습니다.");
        if (!profile.IsDeleted)
            return (true, "이미 활성 상태인 회사설정입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(profile, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        profile.IsDeleted = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "회사설정을 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestoreCustomerCategoryAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var categoryId = target.EntityId;
        if (!await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken))
            return (false, "현재 계정으로 복원할 수 없는 고객분류입니다.");

        var category = await _dbContext.CustomerCategories
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == categoryId, cancellationToken);
        if (category is null)
            return (false, "복원할 고객분류를 찾을 수 없습니다.");
        if (!category.IsDeleted)
            return (true, "이미 활성 상태인 고객분류입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(category, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var normalizedName = DefaultCustomerCategories.NormalizeName(category.Name);
        var customerCategories = await _dbContext.CustomerCategories
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id != category.Id && !current.IsDeleted)
            .ToListAsync(cancellationToken);
        var hasActiveDuplicate = customerCategories.Any(current =>
            string.Equals(DefaultCustomerCategories.NormalizeName(current.Name), normalizedName, StringComparison.CurrentCultureIgnoreCase));
        if (hasActiveDuplicate)
            return (false, "같은 이름의 고객분류가 이미 있어 복원할 수 없습니다.");

        category.IsDeleted = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "고객분류를 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestorePriceGradeOptionAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var optionId = target.EntityId;
        if (!await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken))
            return (false, "현재 계정으로 복원할 수 없는 가격등급입니다.");

        var option = await _dbContext.PriceGradeOptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == optionId, cancellationToken);
        if (option is null)
            return (false, "복원할 가격등급을 찾을 수 없습니다.");
        if (!option.IsDeleted)
            return (true, "이미 활성 상태인 가격등급입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(option, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var normalizedName = (option.Name ?? string.Empty).Trim();
        var priceGradeOptions = await _dbContext.PriceGradeOptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id != option.Id && !current.IsDeleted)
            .ToListAsync(cancellationToken);
        var hasActiveDuplicate = priceGradeOptions.Any(current =>
            string.Equals((current.Name ?? string.Empty).Trim(), normalizedName, StringComparison.CurrentCultureIgnoreCase));
        if (hasActiveDuplicate)
            return (false, "같은 이름의 가격등급이 이미 있어 복원할 수 없습니다.");

        option.IsDeleted = false;
        option.IsActive = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "가격등급을 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestoreTradeTypeOptionAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var optionId = target.EntityId;
        if (!await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken))
            return (false, "현재 계정으로 복원할 수 없는 거래구분입니다.");

        var option = await _dbContext.TradeTypeOptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == optionId, cancellationToken);
        if (option is null)
            return (false, "복원할 거래구분을 찾을 수 없습니다.");
        if (!option.IsDeleted)
            return (true, "이미 활성 상태인 거래구분입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(option, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        if (CustomerClassificationNormalizer.TradeTypeDefinition.Find(option.Name) is null)
            return (false, "거래구분 기준값이 아니어서 복원할 수 없습니다.");

        var tradeTypeOptions = await _dbContext.TradeTypeOptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id != option.Id && !current.IsDeleted)
            .ToListAsync(cancellationToken);
        var hasActiveDuplicate = tradeTypeOptions.Any(current =>
            string.Equals(current.Name, option.Name, StringComparison.CurrentCultureIgnoreCase));
        if (hasActiveDuplicate)
            return (false, "같은 이름의 거래구분이 이미 있어 복원할 수 없습니다.");

        option.IsDeleted = false;
        option.IsActive = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "거래구분을 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestoreItemCategoryOptionAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var optionId = target.EntityId;
        if (!await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken))
            return (false, "현재 계정으로 복원할 수 없는 품목분류입니다.");

        var option = await _dbContext.ItemCategoryOptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == optionId, cancellationToken);
        if (option is null)
            return (false, "복원할 품목분류를 찾을 수 없습니다.");
        if (!option.IsDeleted)
            return (true, "이미 활성 상태인 품목분류입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(option, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var normalizedNameKey = RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name);
        var itemCategoryOptions = await _dbContext.ItemCategoryOptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id != option.Id && !current.IsDeleted)
            .ToListAsync(cancellationToken);
        var hasActiveDuplicate = itemCategoryOptions.Any(current =>
            string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(current.Name), normalizedNameKey, StringComparison.OrdinalIgnoreCase));
        if (hasActiveDuplicate)
            return (false, "같은 이름의 품목분류가 이미 있어 복원할 수 없습니다.");

        option.IsDeleted = false;
        option.IsActive = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "품목분류를 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestoreInvoiceAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var invoiceId = target.EntityId;
        var invoice = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == invoiceId, cancellationToken);
        if (invoice is null)
            return (false, "복원할 전표를 찾을 수 없습니다.");

        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == invoice.CustomerId, cancellationToken);
        if (customer is null)
            return (false, "전표와 연결된 거래처를 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForInvoices(invoice.ResponsibleOfficeCode, invoice.TenantCode, invoice.OfficeCode))
            return (false, "현재 계정으로 복원할 수 없는 전표입니다.");
        if (!invoice.IsDeleted)
            return (true, "이미 활성 상태인 전표입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(invoice, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var invoiceRentalProfileScopeCheck = await EnsureCanWriteRentalSettlementProfileAsync(
            invoice.LinkedRentalBillingProfileId,
            cancellationToken);
        if (!invoiceRentalProfileScopeCheck.Success)
            return (false, invoiceRentalProfileScopeCheck.Message);

        var invoiceGroupRestore = await RestoreInvoiceGroupAsync(invoice, customer, cancellationToken);
        if (!invoiceGroupRestore.Success)
            return (false, invoiceGroupRestore.Message);

        var invoiceGroup = await GetInvoiceGroupAsync(invoice, cancellationToken);
        var invoiceIds = invoiceGroup
            .Select(current => current.Id)
            .Distinct()
            .ToList();
        var invoiceMap = invoiceGroup.ToDictionary(current => current.Id);
        var rentalSettlementTargets = new List<(Guid ProfileId, Guid? RunId)>();
        foreach (var current in invoiceGroup)
            AddRentalSettlementTarget(current, rentalSettlementTargets);

        var deletedPayments = await _dbContext.Payments
            .IgnoreQueryFilters()
            .Where(current => invoiceIds.Contains(current.InvoiceId) && current.IsDeleted)
            .ToListAsync(cancellationToken);
        var restoredLinkedPaymentCount = 0;
        var restoredLinkedTransactionCount = 0;
        var relinkedTransactionCount = 0;
        foreach (var payment in deletedPayments)
        {
            if (!invoiceMap.TryGetValue(payment.InvoiceId, out var paymentInvoice))
                continue;

            var linkedTransactionRestore = await RestoreLinkedTransactionForPaymentAsync(payment, paymentInvoice, cancellationToken);
            if (!linkedTransactionRestore.Success)
                return (false, linkedTransactionRestore.Message);
            if (linkedTransactionRestore.RentalProfileId is Guid linkedRentalProfileId && linkedRentalProfileId != Guid.Empty)
                rentalSettlementTargets.Add((linkedRentalProfileId, linkedTransactionRestore.RentalRunId));

            payment.IsDeleted = false;
            await RestorePaymentAttachmentsAsync(payment.Id, cancellationToken);
            restoredLinkedPaymentCount++;
            if (linkedTransactionRestore.TransactionRestored)
                restoredLinkedTransactionCount++;
            if (linkedTransactionRestore.TransactionRelinked)
                relinkedTransactionCount++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _rentalSettlementRecalculationService.RecalculateRentalSettlementsAsync(rentalSettlementTargets, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        return (true, restoredLinkedPaymentCount > 0 || restoredLinkedTransactionCount > 0 || relinkedTransactionCount > 0
            ? "전표를 복원하고 연결된 수금/지급 기록과 거래내역도 함께 활성화했습니다."
            : invoiceGroupRestore.CustomerRestored
            ? "전표를 복원하고 연결된 거래처도 함께 활성화했습니다."
            : "전표를 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestorePaymentAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var paymentId = target.EntityId;
        var payment = await _dbContext.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == paymentId, cancellationToken);
        if (payment is null)
            return (false, "복원할 수금/지급 기록을 찾을 수 없습니다.");

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
        if (!_officeScopeService.CanWriteOfficeForPayments(invoice.ResponsibleOfficeCode, invoice.TenantCode, invoice.OfficeCode))
            return (false, "현재 계정으로 복원할 수 없는 수금/지급 기록입니다.");
        if (!payment.IsDeleted)
            return (true, "이미 활성 상태인 수금/지급 기록입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(payment, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var invoiceRentalProfileScopeCheck = await EnsureCanWriteRentalSettlementProfileAsync(
            invoice.LinkedRentalBillingProfileId,
            cancellationToken);
        if (!invoiceRentalProfileScopeCheck.Success)
            return (false, invoiceRentalProfileScopeCheck.Message);

        var customerRestored = false;
        var invoiceRestored = false;
        var shouldRebuildInventoryLedger = false;
        if (invoice.IsDeleted)
        {
            var invoiceGroupRestore = await RestoreInvoiceGroupAsync(invoice, customer, cancellationToken);
            if (!invoiceGroupRestore.Success)
                return (false, invoiceGroupRestore.Message);

            customerRestored = invoiceGroupRestore.CustomerRestored;
            invoiceRestored = true;
            shouldRebuildInventoryLedger = true;
        }
        else if (customer.IsDeleted)
        {
            var customerScopeCheck = EnsureCanRestoreLinkedCustomer(customer);
            if (!customerScopeCheck.Success)
                return customerScopeCheck;

            customer.IsDeleted = false;
            customerRestored = true;
        }

        var rentalSettlementTargets = new List<(Guid ProfileId, Guid? RunId)>();
        AddRentalSettlementTarget(invoice, rentalSettlementTargets);

        var linkedTransactionRestore = await RestoreLinkedTransactionForPaymentAsync(payment, invoice, cancellationToken);
        if (!linkedTransactionRestore.Success)
            return (false, linkedTransactionRestore.Message);
        customerRestored = linkedTransactionRestore.CustomerRestored || customerRestored;
        if (linkedTransactionRestore.RentalProfileId is Guid linkedRentalProfileId && linkedRentalProfileId != Guid.Empty)
            rentalSettlementTargets.Add((linkedRentalProfileId, linkedTransactionRestore.RentalRunId));

        payment.IsDeleted = false;
        await RestorePaymentAttachmentsAsync(payment.Id, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _rentalSettlementRecalculationService.RecalculateRentalSettlementsAsync(rentalSettlementTargets, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (shouldRebuildInventoryLedger)
            await _inventoryLedgerService.RebuildAsync(cancellationToken);
        return (true, customerRestored || invoiceRestored || linkedTransactionRestore.TransactionRestored || linkedTransactionRestore.TransactionRelinked
            ? "수금/지급 기록을 복원하고 연결 거래내역/전표도 함께 활성화했습니다."
            : "수금/지급 기록을 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestoreTransactionAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var transactionId = target.EntityId;
        var transaction = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transactionId, cancellationToken);
        if (transaction is null)
            return (false, "복원할 거래내역을 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForPayments(transaction.ResponsibleOfficeCode, transaction.TenantCode, transaction.OfficeCode))
            return (false, "현재 계정으로 복원할 수 없는 거래내역입니다.");
        if (!transaction.IsDeleted)
            return (true, "이미 활성 상태인 거래내역입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(transaction, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transaction.CustomerId, cancellationToken);
        if (customer is null)
            return (false, "연결된 거래처를 찾을 수 없습니다.");

        var transactionRentalProfileScopeCheck = await EnsureCanWriteRentalSettlementProfileAsync(
            transaction.LinkedRentalBillingProfileId,
            cancellationToken);
        if (!transactionRentalProfileScopeCheck.Success)
            return (false, transactionRentalProfileScopeCheck.Message);

        var customerRestored = false;
        var invoiceRestored = false;
        var shouldRebuildInventoryLedger = false;

        if (transaction.LinkedInvoiceId is Guid linkedInvoiceId && linkedInvoiceId != Guid.Empty)
        {
            var linkedInvoice = await _dbContext.Invoices
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == linkedInvoiceId, cancellationToken);
            if (linkedInvoice is not null)
            {
                if (!_officeScopeService.CanWriteOfficeForInvoices(linkedInvoice.ResponsibleOfficeCode, linkedInvoice.TenantCode, linkedInvoice.OfficeCode))
                    return (false, "현재 계정으로 연결 전표를 복원할 수 없습니다.");

                var linkedInvoiceRentalProfileScopeCheck = await EnsureCanWriteRentalSettlementProfileAsync(
                    linkedInvoice.LinkedRentalBillingProfileId,
                    cancellationToken);
                if (!linkedInvoiceRentalProfileScopeCheck.Success)
                    return (false, linkedInvoiceRentalProfileScopeCheck.Message);

                var linkedInvoiceCustomer = await _dbContext.Customers
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(current => current.Id == linkedInvoice.CustomerId, cancellationToken);
                if (linkedInvoiceCustomer is null)
                    return (false, "연결 전표의 거래처를 찾을 수 없습니다.");

                if (linkedInvoice.IsDeleted)
                {
                    var invoiceGroupRestore = await RestoreInvoiceGroupAsync(linkedInvoice, linkedInvoiceCustomer, cancellationToken);
                    if (!invoiceGroupRestore.Success)
                        return (false, invoiceGroupRestore.Message);

                    invoiceRestored = true;
                    customerRestored = invoiceGroupRestore.CustomerRestored || customerRestored;
                    shouldRebuildInventoryLedger = true;
                }
                else if (linkedInvoiceCustomer.IsDeleted)
                {
                    var customerScopeCheck = EnsureCanRestoreLinkedCustomer(linkedInvoiceCustomer);
                    if (!customerScopeCheck.Success)
                        return customerScopeCheck;

                    linkedInvoiceCustomer.IsDeleted = false;
                    customerRestored = true;
                }

                if (transaction.CustomerId != linkedInvoiceCustomer.Id)
                    transaction.CustomerId = linkedInvoiceCustomer.Id;
                customer = linkedInvoiceCustomer;

                if (linkedInvoice.LinkedRentalBillingProfileId is Guid invoiceRentalProfileId && invoiceRentalProfileId != Guid.Empty)
                {
                    transaction.LinkedRentalBillingProfileId = invoiceRentalProfileId;
                    transaction.LinkedRentalBillingRunId = linkedInvoice.LinkedRentalBillingRunId;
                    if (!string.Equals(transaction.TransactionKind, "렌탈수금", StringComparison.OrdinalIgnoreCase))
                        transaction.TransactionKind = "렌탈수금";
                }
            }
        }

        if (customer.IsDeleted)
        {
            var customerScopeCheck = EnsureCanRestoreLinkedCustomer(customer);
            if (!customerScopeCheck.Success)
                return customerScopeCheck;

            customer.IsDeleted = false;
            customerRestored = true;
        }

        var linkedPaymentRestore = await RestoreLinkedPaymentForTransactionAsync(transaction, cancellationToken);
        if (!linkedPaymentRestore.Success)
            return (false, linkedPaymentRestore.Message);

        var rentalSettlementTargets = new List<(Guid ProfileId, Guid? RunId)>();
        AddRentalSettlementTarget(transaction, rentalSettlementTargets);

        transaction.IsDeleted = false;
        await RestoreTransactionAttachmentsAsync(transaction.Id, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _rentalSettlementRecalculationService.RecalculateRentalSettlementsAsync(rentalSettlementTargets, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (shouldRebuildInventoryLedger)
            await _inventoryLedgerService.RebuildAsync(cancellationToken);
        return (true, customerRestored || invoiceRestored || linkedPaymentRestore.PaymentRestored
            ? "거래내역을 복원하고 연결된 거래처/전표/수금·지급 기록을 함께 활성화했습니다."
            : "거래내역을 복원했습니다.");
    }

    private async Task<(bool Success, bool TransactionRestored, bool TransactionRelinked, bool CustomerRestored, Guid? RentalProfileId, Guid? RentalRunId, string Message)> RestoreLinkedTransactionForPaymentAsync(
        Payment payment,
        Invoice invoice,
        CancellationToken cancellationToken)
    {
        var linkedTransaction = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == payment.Id, cancellationToken);
        if (linkedTransaction is null)
            return (true, false, false, false, null, null, string.Empty);

        var invoiceRentalProfileScopeCheck = await EnsureCanWriteRentalSettlementProfileAsync(
            invoice.LinkedRentalBillingProfileId,
            cancellationToken);
        if (!invoiceRentalProfileScopeCheck.Success)
            return (false, false, false, false, null, null, invoiceRentalProfileScopeCheck.Message);

        var transactionRentalProfileScopeCheck = await EnsureCanWriteRentalSettlementProfileAsync(
            linkedTransaction.LinkedRentalBillingProfileId,
            cancellationToken);
        if (!transactionRentalProfileScopeCheck.Success)
            return (false, false, false, false, null, null, transactionRentalProfileScopeCheck.Message);

        var transactionRelinked = false;
        if (linkedTransaction.LinkedInvoiceId != payment.InvoiceId)
        {
            if (linkedTransaction.LinkedInvoiceId.HasValue && linkedTransaction.LinkedInvoiceId.Value != Guid.Empty)
                return (false, false, false, false, null, null, "연동 거래내역의 전표 연결이 수금/지급 기록과 일치하지 않아 복원할 수 없습니다.");

            linkedTransaction.LinkedInvoiceId = payment.InvoiceId;
            linkedTransaction.LinkedInvoiceNumber = invoice.InvoiceNumber;
            transactionRelinked = true;
            if (invoice.LinkedRentalBillingProfileId is Guid invoiceRentalProfileId && invoiceRentalProfileId != Guid.Empty)
            {
                linkedTransaction.LinkedRentalBillingProfileId = invoiceRentalProfileId;
                linkedTransaction.LinkedRentalBillingRunId = invoice.LinkedRentalBillingRunId;
                linkedTransaction.TransactionKind = "렌탈수금";
            }
            else if (invoice.VoucherType == VoucherType.Purchase)
            {
                linkedTransaction.TransactionKind = "전표지급";
            }
            else
            {
                linkedTransaction.TransactionKind = "전표수금";
            }
        }
        if (linkedTransaction.SettlementAmount != payment.Amount)
        {
            linkedTransaction.SettlementAmount = payment.Amount;
            transactionRelinked = true;
        }
        if (invoice.LinkedRentalBillingProfileId is Guid linkedInvoiceRentalProfileId && linkedInvoiceRentalProfileId != Guid.Empty)
        {
            if (linkedTransaction.LinkedRentalBillingProfileId != linkedInvoiceRentalProfileId)
            {
                linkedTransaction.LinkedRentalBillingProfileId = linkedInvoiceRentalProfileId;
                transactionRelinked = true;
            }
            if (linkedTransaction.LinkedRentalBillingRunId != invoice.LinkedRentalBillingRunId)
            {
                linkedTransaction.LinkedRentalBillingRunId = invoice.LinkedRentalBillingRunId;
                transactionRelinked = true;
            }
            if (!string.Equals(linkedTransaction.TransactionKind, "렌탈수금", StringComparison.OrdinalIgnoreCase))
            {
                linkedTransaction.TransactionKind = "렌탈수금";
                transactionRelinked = true;
            }
        }
        if (!_officeScopeService.CanWriteOfficeForPayments(linkedTransaction.ResponsibleOfficeCode, linkedTransaction.TenantCode, linkedTransaction.OfficeCode))
            return (false, false, false, false, null, null, "현재 계정으로 연동 거래내역을 복원할 수 없습니다.");
        if (linkedTransaction.CustomerId != invoice.CustomerId)
        {
            linkedTransaction.CustomerId = invoice.CustomerId;
            transactionRelinked = true;
        }

        if (!linkedTransaction.IsDeleted)
            return (true, false, transactionRelinked, false, linkedTransaction.LinkedRentalBillingProfileId, linkedTransaction.LinkedRentalBillingRunId, string.Empty);

        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == linkedTransaction.CustomerId, cancellationToken);
        if (customer is null)
            return (false, false, false, false, null, null, "연동 거래내역의 거래처를 찾을 수 없습니다.");

        var customerRestored = false;
        if (customer.IsDeleted)
        {
            var customerScopeCheck = EnsureCanRestoreLinkedCustomer(customer);
            if (!customerScopeCheck.Success)
                return (false, false, false, false, null, null, customerScopeCheck.Message);

            customer.IsDeleted = false;
            customerRestored = true;
        }

        linkedTransaction.IsDeleted = false;
        await RestoreTransactionAttachmentsAsync(linkedTransaction.Id, cancellationToken);
        return (true, true, transactionRelinked, customerRestored, linkedTransaction.LinkedRentalBillingProfileId, linkedTransaction.LinkedRentalBillingRunId, string.Empty);
    }

    private async Task<(bool Success, bool PaymentRestored, string Message)> RestoreLinkedPaymentForTransactionAsync(
        TransactionRecord transaction,
        CancellationToken cancellationToken)
    {
        var linkedPayment = await _dbContext.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transaction.Id, cancellationToken);
        if (linkedPayment is null)
            return (true, false, string.Empty);

        if (!transaction.LinkedInvoiceId.HasValue || transaction.LinkedInvoiceId.Value == Guid.Empty || linkedPayment.InvoiceId != transaction.LinkedInvoiceId.Value)
            return (false, false, "연동 수금/지급 기록의 전표 연결이 거래내역과 일치하지 않아 복원할 수 없습니다.");

        var linkedPaymentInvoice = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == linkedPayment.InvoiceId, cancellationToken);
        if (linkedPaymentInvoice is null)
            return (false, false, "연동 수금/지급 기록의 전표를 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForPayments(linkedPaymentInvoice.ResponsibleOfficeCode, linkedPaymentInvoice.TenantCode, linkedPaymentInvoice.OfficeCode))
            return (false, false, "현재 계정으로 연동 수금/지급 기록을 복원할 수 없습니다.");
        if (!linkedPayment.IsDeleted)
            return (true, false, string.Empty);

        linkedPayment.IsDeleted = false;
        await RestorePaymentAttachmentsAsync(linkedPayment.Id, cancellationToken);
        return (true, true, string.Empty);
    }

    private async Task RestorePaymentAttachmentsAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var attachments = await _dbContext.PaymentAttachments
            .IgnoreQueryFilters()
            .Where(current => current.PaymentId == paymentId && current.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var attachment in attachments)
            attachment.IsDeleted = false;
    }

    private async Task RestoreTransactionAttachmentsAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var attachments = await _dbContext.TransactionAttachments
            .IgnoreQueryFilters()
            .Where(current => current.TransactionId == transactionId && current.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var attachment in attachments)
            attachment.IsDeleted = false;
    }

    private static void AddRentalSettlementTarget(
        Invoice? invoice,
        ICollection<(Guid ProfileId, Guid? RunId)> targets)
    {
        if (invoice?.LinkedRentalBillingProfileId is Guid profileId && profileId != Guid.Empty)
            targets.Add((profileId, invoice.LinkedRentalBillingRunId));
    }

    private static void AddRentalSettlementTarget(
        TransactionRecord? transaction,
        ICollection<(Guid ProfileId, Guid? RunId)> targets)
    {
        if (transaction?.LinkedRentalBillingProfileId is Guid profileId && profileId != Guid.Empty)
            targets.Add((profileId, transaction.LinkedRentalBillingRunId));
    }

    private async Task<(bool Success, string Message)> RestoreRentalBillingProfileAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var profileId = target.EntityId;
        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, cancellationToken);
        if (profile is null)
            return (false, "복원할 렌탈 청구프로필을 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode))
            return (false, "현재 계정으로 복원할 수 없는 렌탈 청구프로필입니다.");
        if (!profile.IsDeleted)
            return (true, "이미 활성 상태인 렌탈 청구프로필입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(profile, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var customerRestored = false;
        if (profile.CustomerId.HasValue && profile.CustomerId.Value != Guid.Empty)
        {
            var customer = await _dbContext.Customers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == profile.CustomerId.Value, cancellationToken);
            if (customer is not null && customer.IsDeleted)
            {
                if (!_officeScopeService.CanWriteOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
                    return (false, "현재 계정으로 연결된 거래처를 복원할 수 없습니다.");

                customer.IsDeleted = false;
                customerRestored = true;
            }
        }

        profile.IsDeleted = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, customerRestored
            ? "렌탈 청구프로필을 복원하고 연결된 거래처도 함께 활성화했습니다."
            : "렌탈 청구프로필을 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestoreRentalAssetAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var assetId = target.EntityId;
        var asset = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == assetId, cancellationToken);
        if (asset is null)
            return (false, "복원할 렌탈 자산을 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForRentals(asset.ResponsibleOfficeCode, asset.TenantCode, asset.OfficeCode))
            return (false, "현재 계정으로 복원할 수 없는 렌탈 자산입니다.");
        if (!asset.IsDeleted)
            return (true, "이미 활성 상태인 렌탈 자산입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(asset, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var activeConflict = await FindActiveRentalAssetRestoreConflictAsync(asset, cancellationToken);
        if (activeConflict is not null)
        {
            var message = "같은 렌탈 자산 식별값을 가진 활성 자산이 있어 복원할 수 없습니다.";
            if (_officeScopeService.CanReadOfficeForRentals(
                    activeConflict.ResponsibleOfficeCode,
                    activeConflict.TenantCode,
                    activeConflict.OfficeCode))
            {
                message += $" 활성 자산: {BuildRentalAssetConflictDisplay(activeConflict)}";
            }

            return (false, message);
        }

        var customerRestored = false;
        if (asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
        {
            var customer = await _dbContext.Customers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == asset.CustomerId.Value, cancellationToken);
            if (customer is not null && customer.IsDeleted)
            {
                if (!_officeScopeService.CanWriteOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
                    return (false, "현재 계정으로 연결된 거래처를 복원할 수 없습니다.");

                customer.IsDeleted = false;
                customerRestored = true;
            }
        }

        asset.IsDeleted = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, customerRestored
            ? "렌탈 자산을 복원하고 연결된 거래처도 함께 활성화했습니다."
            : "렌탈 자산을 복원했습니다.");
    }

    private async Task<RentalAsset?> FindActiveRentalAssetRestoreConflictAsync(
        RentalAsset target,
        CancellationToken cancellationToken)
    {
        var candidates = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .Where(current => current.Id != target.Id && !current.IsDeleted)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(candidate =>
            RentalAssetRestoreKeysMatch(candidate.ManagementNumber, target.ManagementNumber) ||
            RentalAssetRestoreKeysMatch(candidate.ManagementId, target.ManagementId) ||
            RentalAssetRestoreKeysMatch(candidate.AssetKey, target.AssetKey));
    }

    private static bool RentalAssetRestoreKeysMatch(string? left, string? right)
    {
        var normalizedLeft = (left ?? string.Empty).Trim();
        var normalizedRight = (right ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(normalizedLeft) &&
               string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRentalAssetConflictDisplay(RentalAsset asset)
        => JoinSegments(
            string.IsNullOrWhiteSpace(asset.ManagementNumber) ? null : $"관리번호 {asset.ManagementNumber}",
            string.IsNullOrWhiteSpace(asset.ManagementId) ? null : $"관리ID {asset.ManagementId}",
            string.IsNullOrWhiteSpace(asset.AssetKey) ? null : $"자산키 {asset.AssetKey}",
            string.IsNullOrWhiteSpace(asset.ItemName) ? null : asset.ItemName);

    private async Task<(bool Success, string Message)> RestoreRentalBillingLogAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var logId = target.EntityId;
        var log = await _dbContext.RentalBillingLogs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == logId, cancellationToken);
        if (log is null)
            return (false, "복원할 렌탈 청구로그를 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForRentals(log.ResponsibleOfficeCode, log.TenantCode, log.OfficeCode))
            return (false, "현재 계정으로 복원할 수 없는 렌탈 청구로그입니다.");
        if (!log.IsDeleted)
            return (true, "이미 활성 상태인 렌탈 청구로그입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(log, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var profileRestored = false;
        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == log.BillingProfileId, cancellationToken);
        if (profile is not null && profile.IsDeleted)
        {
            if (!_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode))
                return (false, "현재 계정으로 연결된 렌탈 청구프로필을 복원할 수 없습니다.");

            profile.IsDeleted = false;
            profileRestored = true;
        }

        log.IsDeleted = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, profileRestored
            ? "렌탈 청구로그를 복원하고 연결된 렌탈 청구프로필도 함께 활성화했습니다."
            : "렌탈 청구로그를 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestoreInventoryTransferAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var transferId = target.EntityId;
        var transfer = await _dbContext.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(current => current.Lines)
            .FirstOrDefaultAsync(current => current.Id == transferId, cancellationToken);
        if (transfer is null)
            return (false, "복원할 재고이동을 찾을 수 없습니다.");
        if (!CanMutateInventoryTransferFromRecycleBin(transfer, out var scopeMessage))
        {
            return (false, scopeMessage);
        }
        if (!transfer.IsDeleted)
            return (true, "이미 활성 상태인 재고이동입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(transfer, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var originalTransferDeleted = transfer.IsDeleted;
        var originalLineDeletedStates = transfer.Lines
            .ToDictionary(line => line.Id, line => line.IsDeleted);
        transfer.IsDeleted = false;
        foreach (var line in transfer.Lines)
            line.IsDeleted = false;

        var stockDeltas = await _invoiceStockSnapshotService.BuildInventoryTransferStockDeltasAsync(transfer, cancellationToken);
        var stockShortages = await _invoiceStockSnapshotService.FindStockShortagesAsync(
            new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
            stockDeltas,
            cancellationToken);
        if (stockShortages.Count > 0)
        {
            transfer.IsDeleted = originalTransferDeleted;
            foreach (var line in transfer.Lines)
            {
                if (originalLineDeletedStates.TryGetValue(line.Id, out var wasDeleted))
                    line.IsDeleted = wasDeleted;
            }

            return (false, InvoiceStockSnapshotService.FormatStockShortageMessage(stockShortages));
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
            stockDeltas,
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (true, "재고이동을 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> RestoreRentalManagementCompanyAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var companyId = target.EntityId;
        if (!await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken))
            return (false, "현재 계정으로 복원할 수 없는 렌탈 관리업체입니다.");

        var company = await _dbContext.RentalManagementCompanies
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == companyId, cancellationToken);
        if (company is null)
            return (false, "복원할 렌탈 관리업체를 찾을 수 없습니다.");
        if (!company.IsDeleted)
            return (true, "이미 활성 상태인 렌탈 관리업체입니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(company, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        company.IsDeleted = false;
        company.IsActive = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "렌탈 관리업체를 복원했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeCustomerAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var customerId = target.EntityId;
        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == customerId, cancellationToken);
        if (customer is null)
            return (false, "영구삭제할 거래처를 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 거래처입니다.");
        if (!customer.IsDeleted)
            return (false, "활성 상태 거래처는 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(customer, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var hasInvoices = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(current => current.CustomerId == customerId, cancellationToken);
        if (hasInvoices)
            return (false, "연결된 전표가 남아 있어 거래처를 영구삭제할 수 없습니다.");

        var hasTransactions = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .AnyAsync(current => current.CustomerId == customerId, cancellationToken);
        if (hasTransactions)
            return (false, "연결된 거래내역이 남아 있어 거래처를 영구삭제할 수 없습니다.");

        var hasRentalProfiles = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AnyAsync(current => current.CustomerId == customerId, cancellationToken);
        if (hasRentalProfiles)
            return (false, "연결된 렌탈 청구 프로필이 남아 있어 거래처를 영구삭제할 수 없습니다.");

        var contracts = await _dbContext.CustomerContracts
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == customerId)
            .ToListAsync(cancellationToken);
        if (contracts.Any(current => !current.IsDeleted))
            return (false, "활성 계약서가 남아 있어 거래처를 영구삭제할 수 없습니다.");
        var contractStoragePaths = contracts
            .Select(current => current.StoragePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        var hasRentalAssets = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .AnyAsync(current => current.CustomerId == customerId, cancellationToken);
        if (hasRentalAssets)
            return (false, "연결된 렌탈 자산이 남아 있어 거래처를 영구삭제할 수 없습니다.");

        var hasCurrentAssignmentHistories = await _dbContext.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .AnyAsync(current => current.CustomerId == customerId && !current.IsDeleted && current.IsCurrent, cancellationToken);
        if (hasCurrentAssignmentHistories)
            return (false, "현재 설치이력이 남아 있어 거래처를 영구삭제할 수 없습니다.");

        var assignmentHistories = await _dbContext.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == customerId)
            .ToListAsync(cancellationToken);
        if (assignmentHistories.Any(history => !_officeScopeService.CanWriteOfficeForRentals(history.ResponsibleOfficeCode, history.TenantCode, history.OfficeCode)))
            return (false, "현재 계정으로 연결된 렌탈 임대이력을 변경할 수 없어 거래처를 영구삭제할 수 없습니다.");

        foreach (var history in assignmentHistories)
            history.CustomerId = null;

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("customer", customer.Id, customer.TenantCode, customer.ResponsibleOfficeCode, customer.OfficeCode)
        ], cancellationToken);
        _dbContext.CustomerContracts.RemoveRange(contracts);
        _dbContext.Customers.Remove(customer);
        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var storagePath in contractStoragePaths)
            _fileStorage.DeleteIfExists(storagePath);

        return (true, "거래처를 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeContractAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var contractId = target.EntityId;
        var contract = await _dbContext.CustomerContracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contractId, cancellationToken);
        if (contract is null)
            return (false, "영구삭제할 계약서를 찾을 수 없습니다.");
        var contractCustomer = await _dbContext.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Id == contract.CustomerId, cancellationToken);
        if (contractCustomer is null ||
            !_officeScopeService.CanWriteOfficeForContracts(
                contractCustomer.ResponsibleOfficeCode,
                contractCustomer.TenantCode,
                contractCustomer.OfficeCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 계약서입니다.");
        if (!contract.IsDeleted)
            return (false, "활성 상태 계약서는 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(contract, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("contract", contract.Id, contractCustomer.TenantCode, contractCustomer.ResponsibleOfficeCode, contractCustomer.OfficeCode)
        ], cancellationToken);
        var storagePath = contract.StoragePath;
        _dbContext.CustomerContracts.Remove(contract);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _fileStorage.DeleteIfExists(storagePath);

        return (true, "계약서를 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeItemAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var itemId = target.EntityId;
        var item = await _dbContext.Items
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == itemId, cancellationToken);
        if (item is null)
            return (false, "영구삭제할 품목을 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForItems(item.OfficeCode, item.TenantCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 품목입니다.");
        if (!item.IsDeleted)
            return (false, "활성 상태 품목은 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(item, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var referenceCheck = await EnsureItemHasNoRemainingBusinessReferencesAsync(itemId, cancellationToken);
        if (!referenceCheck.Success)
            return referenceCheck;

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("item", item.Id, item.TenantCode, item.OfficeCode)
        ], cancellationToken);
        _dbContext.Items.Remove(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "품목을 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> EnsureItemHasNoRemainingBusinessReferencesAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var hasInvoiceLines = await _dbContext.InvoiceLines
            .IgnoreQueryFilters()
            .AnyAsync(line => line.ItemId == itemId, cancellationToken);
        if (hasInvoiceLines)
            return (false, "연결된 전표 라인이 남아 있어 품목을 영구삭제할 수 없습니다.");

        var hasInventoryTransferLines = await _dbContext.InventoryTransferLines
            .IgnoreQueryFilters()
            .AnyAsync(line => line.ItemId == itemId, cancellationToken);
        if (hasInventoryTransferLines)
            return (false, "연결된 재고이동 라인이 남아 있어 품목을 영구삭제할 수 없습니다.");

        var hasRentalAssets = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .AnyAsync(asset => asset.ItemId == itemId, cancellationToken);
        if (hasRentalAssets)
            return (false, "연결된 렌탈 자산이 남아 있어 품목을 영구삭제할 수 없습니다.");

        var rentalBillingTemplates = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => !profile.IsDeleted && !string.IsNullOrWhiteSpace(profile.BillingTemplateJson))
            .Select(profile => profile.BillingTemplateJson)
            .ToListAsync(cancellationToken);
        var hasRentalBillingTemplateReference = rentalBillingTemplates
            .Any(templateJson => ItemDeletionReferenceGuard.BillingTemplateReferencesItem(templateJson, itemId));
        if (hasRentalBillingTemplateReference)
            return (false, "연결된 렌탈 청구프로필 청구품목이 남아 있어 품목을 영구삭제할 수 없습니다.");

        return (true, string.Empty);
    }

    private async Task<(bool Success, string Message)> PurgeCompanyProfileAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var profileId = target.EntityId;
        if (!await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken))
            return (false, "현재 계정으로 영구삭제할 수 없는 회사설정입니다.");

        var profile = await _dbContext.CompanyProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, cancellationToken);
        if (profile is null)
            return (false, "영구삭제할 회사설정을 찾을 수 없습니다.");
        if (!profile.IsDeleted)
            return (false, "활성 상태 회사설정은 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(profile, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord(
                "company-profile",
                profile.Id,
                TenantScopeCatalog.GetTenantCodeForOffice(profile.OfficeCode),
                profile.OfficeCode)
        ], cancellationToken);
        _dbContext.CompanyProfiles.Remove(profile);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "회사설정을 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeCustomerCategoryAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var categoryId = target.EntityId;
        if (!await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken))
            return (false, "현재 계정으로 영구삭제할 수 없는 고객분류입니다.");

        var category = await _dbContext.CustomerCategories
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == categoryId, cancellationToken);
        if (category is null)
            return (false, "영구삭제할 고객분류를 찾을 수 없습니다.");
        if (!category.IsDeleted)
            return (false, "활성 상태 고객분류는 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(category, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var inUse = await _dbContext.Customers
            .IgnoreQueryFilters()
            .AnyAsync(customer => customer.CategoryId == categoryId, cancellationToken);
        if (!inUse)
        {
            inUse = await _dbContext.CustomerMasters
                .IgnoreQueryFilters()
                .AnyAsync(customer => customer.CategoryId == categoryId, cancellationToken);
        }

        if (inUse)
            return (false, "연결된 거래처가 남아 있어 고객분류를 영구삭제할 수 없습니다.");

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("customer-category", category.Id, null, null)
        ], cancellationToken);
        _dbContext.CustomerCategories.Remove(category);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "고객분류를 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgePriceGradeOptionAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var optionId = target.EntityId;
        if (!await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken))
            return (false, "현재 계정으로 영구삭제할 수 없는 가격등급입니다.");

        var option = await _dbContext.PriceGradeOptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == optionId, cancellationToken);
        if (option is null)
            return (false, "영구삭제할 가격등급을 찾을 수 없습니다.");
        if (!option.IsDeleted)
            return (false, "활성 상태 가격등급은 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(option, target);
        if (!revisionCheck.Success)
            return revisionCheck;
        if (await _dbContext.Customers.IgnoreQueryFilters().AnyAsync(customer => customer.PriceGrade == option.Name, cancellationToken))
            return (false, "연결된 거래처가 남아 있어 가격등급을 영구삭제할 수 없습니다.");

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("price-grade-option", option.Id, null, null)
        ], cancellationToken);
        _dbContext.PriceGradeOptions.Remove(option);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "가격등급을 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeTradeTypeOptionAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var optionId = target.EntityId;
        if (!await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken))
            return (false, "현재 계정으로 영구삭제할 수 없는 거래구분입니다.");

        var option = await _dbContext.TradeTypeOptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == optionId, cancellationToken);
        if (option is null)
            return (false, "영구삭제할 거래구분을 찾을 수 없습니다.");
        if (!option.IsDeleted)
            return (false, "활성 상태 거래구분은 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(option, target);
        if (!revisionCheck.Success)
            return revisionCheck;
        if (await _dbContext.Customers.IgnoreQueryFilters().AnyAsync(customer => customer.TradeType == option.Name, cancellationToken))
            return (false, "연결된 거래처가 남아 있어 거래구분을 영구삭제할 수 없습니다.");

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("trade-type-option", option.Id, null, null)
        ], cancellationToken);
        _dbContext.TradeTypeOptions.Remove(option);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "거래구분을 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeItemCategoryOptionAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var optionId = target.EntityId;
        if (!await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken))
            return (false, "현재 계정으로 영구삭제할 수 없는 품목분류입니다.");

        var option = await _dbContext.ItemCategoryOptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == optionId, cancellationToken);
        if (option is null)
            return (false, "영구삭제할 품목분류를 찾을 수 없습니다.");
        if (!option.IsDeleted)
            return (false, "활성 상태 품목분류는 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(option, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var optionKey = RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name);
        var itemInUse = (await _dbContext.Items.IgnoreQueryFilters().ToListAsync(cancellationToken))
            .Any(item => string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(item.CategoryName), optionKey, StringComparison.OrdinalIgnoreCase));
        var rentalInUse = (await _dbContext.RentalAssets.IgnoreQueryFilters().ToListAsync(cancellationToken))
            .Any(asset => string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.ItemCategoryName), optionKey, StringComparison.OrdinalIgnoreCase));
        if (itemInUse || rentalInUse)
            return (false, "연결된 품목 또는 렌탈 자산이 남아 있어 품목분류를 영구삭제할 수 없습니다.");

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("item-category-option", option.Id, null, null)
        ], cancellationToken);
        _dbContext.ItemCategoryOptions.Remove(option);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "품목분류를 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeInvoiceAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var invoiceId = target.EntityId;
        var invoice = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == invoiceId, cancellationToken);
        if (invoice is null)
            return (false, "영구삭제할 전표를 찾을 수 없습니다.");
        var invoiceCustomer = await _dbContext.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Id == invoice.CustomerId, cancellationToken);
        if (invoiceCustomer is null || !_officeScopeService.CanWriteOfficeForInvoices(invoice.ResponsibleOfficeCode, invoice.TenantCode, invoice.OfficeCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 전표입니다.");
        if (!invoice.IsDeleted)
            return (false, "활성 상태 전표는 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(invoice, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var invoiceGroup = await GetInvoiceGroupAsync(invoice, cancellationToken);
        var invoiceGroupScopeCheck = EnsureCanWriteInvoiceGroup(invoiceGroup, "영구삭제");
        if (!invoiceGroupScopeCheck.Success)
            return invoiceGroupScopeCheck;

        var invoiceIds = invoiceGroup.Select(current => current.Id).Distinct().ToList();

        var hasTransactions = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .AnyAsync(current => current.LinkedInvoiceId.HasValue && invoiceIds.Contains(current.LinkedInvoiceId.Value), cancellationToken);
        if (hasTransactions)
            return (false, "연결된 거래내역이 남아 있어 전표를 영구삭제할 수 없습니다.");

        var hasActivePayments = await _dbContext.Payments
            .IgnoreQueryFilters()
            .AnyAsync(current => invoiceIds.Contains(current.InvoiceId) && !current.IsDeleted, cancellationToken);
        if (hasActivePayments)
            return (false, "활성 수금/지급 기록이 남아 있어 전표를 영구삭제할 수 없습니다.");

        var deletedPayments = await _dbContext.Payments
            .IgnoreQueryFilters()
            .Where(current => invoiceIds.Contains(current.InvoiceId) && current.IsDeleted)
            .ToListAsync(cancellationToken);
        var deletedPaymentIds = deletedPayments.Select(current => current.Id).Distinct().ToList();
        var paymentAttachments = deletedPaymentIds.Count == 0
            ? new List<PaymentAttachment>()
            : await _dbContext.PaymentAttachments
                .IgnoreQueryFilters()
                .Where(current => deletedPaymentIds.Contains(current.PaymentId))
                .ToListAsync(cancellationToken);
        var paymentAttachmentPaths = paymentAttachments
            .Select(current => current.StoragePath)
            .ToList();

        var purgeRecords = invoiceGroup
            .Select(current => CreatePurgeRecord("invoice", current.Id, current.TenantCode, current.ResponsibleOfficeCode, current.OfficeCode))
            .ToList();
        purgeRecords.AddRange(deletedPayments.Select(payment =>
        {
            var paymentInvoice = invoiceGroup.First(current => current.Id == payment.InvoiceId);
            return CreatePurgeRecord("payment", payment.Id, paymentInvoice.TenantCode, paymentInvoice.ResponsibleOfficeCode, paymentInvoice.OfficeCode);
        }));

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await TouchPurgeRecordsAsync(purgeRecords, cancellationToken);
        _dbContext.PaymentAttachments.RemoveRange(paymentAttachments);
        _dbContext.Payments.RemoveRange(deletedPayments);
        _dbContext.Invoices.RemoveRange(invoiceGroup);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var attachmentPath in paymentAttachmentPaths)
            _fileStorage.DeleteIfExists(attachmentPath);

        return (true, "전표를 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgePaymentAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var paymentId = target.EntityId;
        var payment = await _dbContext.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == paymentId, cancellationToken);
        if (payment is null)
            return (false, "영구삭제할 수금/지급 기록을 찾을 수 없습니다.");
        var purgeInvoice = await _dbContext.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Id == payment.InvoiceId, cancellationToken);
        var purgeCustomer = purgeInvoice is null ? null : await _dbContext.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Id == purgeInvoice.CustomerId, cancellationToken);
        if (purgeInvoice is null || purgeCustomer is null || !_officeScopeService.CanWriteOfficeForPayments(purgeInvoice.ResponsibleOfficeCode, purgeInvoice.TenantCode, purgeInvoice.OfficeCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 수금/지급 기록입니다.");
        if (!payment.IsDeleted)
            return (false, "활성 상태 수금/지급 기록은 휴지통에서 영구삭제할 수 없습니다.");
        if (await _dbContext.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == paymentId, cancellationToken))
            return (false, "연동 거래내역이 남아 있어 수금/지급 기록만 영구삭제할 수 없습니다. 거래내역 영구삭제를 실행하면 연동 수금/지급도 함께 제거됩니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(payment, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var attachments = await _dbContext.PaymentAttachments
            .IgnoreQueryFilters()
            .Where(current => current.PaymentId == paymentId)
            .ToListAsync(cancellationToken);
        var attachmentPaths = attachments
            .Select(current => current.StoragePath)
            .ToList();

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("payment", payment.Id, purgeInvoice.TenantCode, purgeInvoice.ResponsibleOfficeCode, purgeInvoice.OfficeCode)
        ], cancellationToken);
        _dbContext.PaymentAttachments.RemoveRange(attachments);
        _dbContext.Payments.Remove(payment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var attachmentPath in attachmentPaths)
            _fileStorage.DeleteIfExists(attachmentPath);

        return (true, "수금/지급 기록을 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeTransactionAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var transactionId = target.EntityId;
        var transaction = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transactionId, cancellationToken);
        if (transaction is null)
            return (false, "영구삭제할 거래내역을 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForPayments(transaction.ResponsibleOfficeCode, transaction.TenantCode, transaction.OfficeCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 거래내역입니다.");
        if (!transaction.IsDeleted)
            return (false, "활성 상태 거래내역은 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(transaction, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var attachments = await _dbContext.TransactionAttachments
            .IgnoreQueryFilters()
            .Where(current => current.TransactionId == transactionId)
            .ToListAsync(cancellationToken);
        var attachmentPaths = attachments
            .Select(current => current.StoragePath)
            .ToList();

        var linkedPayment = await _dbContext.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transactionId, cancellationToken);
        var purgeRecords = new List<RecycleBinPurgeRecordDto>
        {
            CreatePurgeRecord("transaction", transaction.Id, transaction.TenantCode, transaction.ResponsibleOfficeCode, transaction.OfficeCode)
        };
        var linkedPaymentAttachments = new List<PaymentAttachment>();
        if (linkedPayment is not null)
        {
            var linkedPaymentInvoice = await _dbContext.Invoices
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == linkedPayment.InvoiceId, cancellationToken);
            if (linkedPaymentInvoice is null || !_officeScopeService.CanWriteOfficeForPayments(linkedPaymentInvoice.ResponsibleOfficeCode, linkedPaymentInvoice.TenantCode, linkedPaymentInvoice.OfficeCode))
                return (false, "현재 계정으로 연동 수금/지급 기록을 영구삭제할 수 없어 거래내역을 영구삭제할 수 없습니다.");
            if (!linkedPayment.IsDeleted)
                return (false, "활성 연동 수금/지급 기록이 남아 있어 거래내역을 영구삭제할 수 없습니다.");

            linkedPaymentAttachments = await _dbContext.PaymentAttachments
                .IgnoreQueryFilters()
                .Where(current => current.PaymentId == linkedPayment.Id)
                .ToListAsync(cancellationToken);
            attachmentPaths.AddRange(linkedPaymentAttachments.Select(current => current.StoragePath));

            purgeRecords.Add(CreatePurgeRecord("payment", linkedPayment.Id, linkedPaymentInvoice.TenantCode, linkedPaymentInvoice.ResponsibleOfficeCode, linkedPaymentInvoice.OfficeCode));
            _dbContext.PaymentAttachments.RemoveRange(linkedPaymentAttachments);
            _dbContext.Payments.Remove(linkedPayment);
        }

        await TouchPurgeRecordsAsync(purgeRecords, cancellationToken);
        _dbContext.TransactionAttachments.RemoveRange(attachments);
        _dbContext.Transactions.Remove(transaction);
        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var attachmentPath in attachmentPaths)
            _fileStorage.DeleteIfExists(attachmentPath);

        return (true, "거래내역을 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeRentalBillingProfileAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var profileId = target.EntityId;
        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, cancellationToken);
        if (profile is null)
            return (false, "영구삭제할 렌탈 청구프로필을 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 렌탈 청구프로필입니다.");
        if (!profile.IsDeleted)
            return (false, "활성 상태 렌탈 청구프로필은 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(profile, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var hasInvoices = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(current => current.LinkedRentalBillingProfileId == profileId, cancellationToken);
        if (hasInvoices)
            return (false, "연결된 전표가 남아 있어 렌탈 청구프로필을 영구삭제할 수 없습니다.");

        var hasTransactions = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .AnyAsync(current => current.LinkedRentalBillingProfileId == profileId, cancellationToken);
        if (hasTransactions)
            return (false, "연결된 수금/거래내역이 남아 있어 렌탈 청구프로필을 영구삭제할 수 없습니다.");

        var linkedAssets = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .Where(current => current.BillingProfileId == profileId)
            .ToListAsync(cancellationToken);
        if (linkedAssets.Any(asset => !_officeScopeService.CanWriteOfficeForRentals(asset.ResponsibleOfficeCode, asset.TenantCode, asset.OfficeCode)))
            return (false, "현재 계정으로 연결된 렌탈 자산을 변경할 수 없어 렌탈 청구프로필을 영구삭제할 수 없습니다.");

        foreach (var asset in linkedAssets)
        {
            asset.BillingProfileId = null;
            asset.BillingEligibilityStatus = GetBillingEligibilityStatusAfterProfilePurge(asset.AssetStatus);
            if (!string.Equals(asset.BillingEligibilityStatus, "청구제외", StringComparison.OrdinalIgnoreCase))
                asset.BillingExclusionReason = string.Empty;
        }

        var logs = await _dbContext.RentalBillingLogs
            .IgnoreQueryFilters()
            .Where(current => current.BillingProfileId == profileId)
            .ToListAsync(cancellationToken);
        if (logs.Any(log => !_officeScopeService.CanWriteOfficeForRentals(log.ResponsibleOfficeCode, log.TenantCode, log.OfficeCode)))
            return (false, "현재 계정으로 연결된 렌탈 청구로그를 변경할 수 없어 렌탈 청구프로필을 영구삭제할 수 없습니다.");

        var assignmentHistories = await _dbContext.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .Where(current => current.BillingProfileId == profileId)
            .ToListAsync(cancellationToken);
        if (assignmentHistories.Any(history => !_officeScopeService.CanWriteOfficeForRentals(history.ResponsibleOfficeCode, history.TenantCode, history.OfficeCode)))
            return (false, "현재 계정으로 연결된 렌탈 임대이력을 변경할 수 없어 렌탈 청구프로필을 영구삭제할 수 없습니다.");

        foreach (var history in assignmentHistories)
            history.BillingProfileId = null;

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("rental-billing-profile", profile.Id, profile.TenantCode, profile.ResponsibleOfficeCode, profile.OfficeCode)
        ], cancellationToken);
        _dbContext.RentalBillingLogs.RemoveRange(logs);
        _dbContext.RentalBillingProfiles.Remove(profile);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "렌탈 청구프로필을 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeRentalAssetAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var assetId = target.EntityId;
        var asset = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == assetId, cancellationToken);
        if (asset is null)
            return (false, "영구삭제할 렌탈 자산을 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForRentals(asset.ResponsibleOfficeCode, asset.TenantCode, asset.OfficeCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 렌탈 자산입니다.");
        if (!asset.IsDeleted)
            return (false, "활성 상태 렌탈 자산은 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(asset, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var profiles = (await _dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken))
            .Where(current => BillingTemplateContainsAssetId(current.BillingTemplateJson, assetId))
            .ToList();
        if (profiles.Any(profile => !_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode)))
            return (false, "현재 계정으로 연결된 렌탈 청구프로필을 변경할 수 없어 렌탈 자산을 영구삭제할 수 없습니다.");

        foreach (var profile in profiles)
        {
            var normalizedJson = RemoveIncludedAssetId(profile.BillingTemplateJson, assetId);
            if (!string.Equals(normalizedJson, profile.BillingTemplateJson, StringComparison.Ordinal))
                profile.BillingTemplateJson = normalizedJson;
        }

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("rental-asset", asset.Id, asset.TenantCode, asset.ResponsibleOfficeCode, asset.OfficeCode)
        ], cancellationToken);
        var assignmentHistories = await _dbContext.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .Where(current => current.AssetId == assetId)
            .ToListAsync(cancellationToken);
        if (assignmentHistories.Any(history => !_officeScopeService.CanWriteOfficeForRentals(history.ResponsibleOfficeCode, history.TenantCode, history.OfficeCode)))
            return (false, "현재 계정으로 연결된 렌탈 임대이력을 삭제할 수 없어 렌탈 자산을 영구삭제할 수 없습니다.");

        _dbContext.RentalAssetAssignmentHistories.RemoveRange(assignmentHistories);
        _dbContext.RentalAssets.Remove(asset);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "렌탈 자산을 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeRentalBillingLogAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var logId = target.EntityId;
        var log = await _dbContext.RentalBillingLogs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == logId, cancellationToken);
        if (log is null)
            return (false, "영구삭제할 렌탈 청구로그를 찾을 수 없습니다.");
        if (!_officeScopeService.CanWriteOfficeForRentals(log.ResponsibleOfficeCode, log.TenantCode, log.OfficeCode))
            return (false, "현재 계정으로 영구삭제할 수 없는 렌탈 청구로그입니다.");
        if (!log.IsDeleted)
            return (false, "활성 상태 렌탈 청구로그는 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(log, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("rental-billing-log", log.Id, log.TenantCode, log.ResponsibleOfficeCode, log.OfficeCode)
        ], cancellationToken);
        _dbContext.RentalBillingLogs.Remove(log);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "렌탈 청구로그를 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeInventoryTransferAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var transferId = target.EntityId;
        var transfer = await _dbContext.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(current => current.Lines)
            .FirstOrDefaultAsync(current => current.Id == transferId, cancellationToken);
        if (transfer is null)
            return (false, "영구삭제할 재고이동을 찾을 수 없습니다.");
        if (!CanMutateInventoryTransferFromRecycleBin(transfer, out var scopeMessage))
        {
            return (false, scopeMessage);
        }
        if (!transfer.IsDeleted)
            return (false, "활성 상태 재고이동은 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(transfer, target);
        if (!revisionCheck.Success)
            return revisionCheck;

        var receiveEvidencePath = transfer.ReceiveEvidencePath;
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("inventory-transfer", transfer.Id, transfer.TenantCode, OfficeCodeCatalog.Shared, transfer.SourceOfficeCode)
        ], cancellationToken);
        _dbContext.InventoryTransferLines.RemoveRange(transfer.Lines);
        _dbContext.InventoryTransfers.Remove(transfer);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _fileStorage.DeleteIfExists(receiveEvidencePath);

        return (true, "재고이동을 영구삭제했습니다.");
    }

    private async Task<(bool Success, string Message)> PurgeRentalManagementCompanyAsync(RecycleBinMutationTargetDto target, CancellationToken cancellationToken)
    {
        var companyId = target.EntityId;
        if (!await _officeScopeService.HasAdministrativeWriteAccessAsync(cancellationToken))
            return (false, "현재 계정으로 영구삭제할 수 없는 렌탈 관리업체입니다.");

        var company = await _dbContext.RentalManagementCompanies
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == companyId, cancellationToken);
        if (company is null)
            return (false, "영구삭제할 렌탈 관리업체를 찾을 수 없습니다.");
        if (!company.IsDeleted)
            return (false, "활성 상태 렌탈 관리업체는 휴지통에서 영구삭제할 수 없습니다.");

        var revisionCheck = EnsureRecycleBinMutationRevision(company, target);
        if (!revisionCheck.Success)
            return revisionCheck;
        if (await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(profile => profile.ManagementCompanyCode == company.Code, cancellationToken) ||
            await _dbContext.RentalAssets.IgnoreQueryFilters().AnyAsync(asset => asset.ManagementCompanyCode == company.Code, cancellationToken))
        {
            return (false, "연결된 렌탈 데이터가 남아 있어 렌탈 관리업체를 영구삭제할 수 없습니다.");
        }

        await TouchPurgeRecordsAsync(
        [
            CreatePurgeRecord("rental-management-company", company.Id, company.TenantCode, OfficeCodeCatalog.Shared)
        ], cancellationToken);
        _dbContext.RentalManagementCompanies.Remove(company);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, "렌탈 관리업체를 영구삭제했습니다.");
    }

    private async Task<List<Invoice>> GetInvoiceGroupAsync(Invoice invoice, CancellationToken cancellationToken)
    {
        var versionGroupId = invoice.VersionGroupId == Guid.Empty ? invoice.Id : invoice.VersionGroupId;
        return await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(current => current.Lines)
            .Where(current =>
                current.Id == invoice.Id ||
                current.Id == versionGroupId ||
                current.VersionGroupId == versionGroupId)
            .ToListAsync(cancellationToken);
    }

    private async Task<(bool Success, bool CustomerRestored, string Message)> RestoreInvoiceGroupAsync(
        Invoice invoice,
        Customer customer,
        CancellationToken cancellationToken)
    {
        var invoiceGroup = await GetInvoiceGroupAsync(invoice, cancellationToken);
        var invoiceGroupScopeCheck = EnsureCanWriteInvoiceGroup(invoiceGroup, "복원");
        if (!invoiceGroupScopeCheck.Success)
            return (false, false, invoiceGroupScopeCheck.Message);

        var rentalProfileScopeCheck = await EnsureCanWriteRentalSettlementProfilesAsync(invoiceGroup, cancellationToken);
        if (!rentalProfileScopeCheck.Success)
            return (false, false, rentalProfileScopeCheck.Message);

        var lineItemScopeCheck = await EnsureCanReadInvoiceGroupLineItemsAsync(invoiceGroup, cancellationToken);
        if (!lineItemScopeCheck.Success)
            return (false, false, lineItemScopeCheck.Message);

        var previousStockDeltas = await BuildInvoiceGroupStockDeltasAsync(invoiceGroup, cancellationToken);
        var customerRestored = false;
        if (customer.IsDeleted)
        {
            var customerScopeCheck = EnsureCanRestoreLinkedCustomer(customer);
            if (!customerScopeCheck.Success)
                return (false, false, customerScopeCheck.Message);

            customer.IsDeleted = false;
            customerRestored = true;
        }

        var latestVersion = invoiceGroup
            .Where(current => !current.IsDeleted)
            .OrderByDescending(current => current.VersionNumber)
            .ThenByDescending(current => current.UpdatedAtUtc)
            .FirstOrDefault();
        var resolvedGroupId = latestVersion?.VersionGroupId == Guid.Empty
            ? latestVersion?.Id ?? invoice.Id
            : latestVersion?.VersionGroupId ?? invoice.Id;

        foreach (var current in invoiceGroup)
        {
            current.IsDeleted = false;
            current.VersionGroupId = current.VersionGroupId == Guid.Empty ? resolvedGroupId : current.VersionGroupId;
            current.VersionNumber = current.VersionNumber <= 0 ? 1 : current.VersionNumber;
        }

        var maxVersionNumber = invoiceGroup.Max(candidate => candidate.VersionNumber <= 0 ? 1 : candidate.VersionNumber);

        foreach (var current in invoiceGroup)
        {
            current.VersionGroupId = resolvedGroupId;
            current.IsLatestVersion = current.VersionNumber == maxVersionNumber;
        }

        await RestoreInvoiceLinesAsync(invoiceGroup, cancellationToken);
        var currentStockDeltas = await BuildInvoiceGroupStockDeltasAsync(invoiceGroup, cancellationToken);
        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            previousStockDeltas,
            currentStockDeltas,
            cancellationToken);
        return (true, customerRestored, string.Empty);
    }

    private async Task<IReadOnlyDictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>> BuildInvoiceGroupStockDeltasAsync(
        IEnumerable<Invoice> invoices,
        CancellationToken cancellationToken)
    {
        var merged = new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>();
        foreach (var invoice in invoices)
        {
            var deltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(invoice, cancellationToken);
            foreach (var (key, quantity) in deltas)
            {
                merged[key] = merged.TryGetValue(key, out var current)
                    ? current + quantity
                    : quantity;
            }
        }

        return merged;
    }

    private async Task<(bool Success, string Message)> EnsureCanReadInvoiceGroupLineItemsAsync(
        IEnumerable<Invoice> invoiceGroup,
        CancellationToken cancellationToken)
    {
        var invoiceIds = invoiceGroup
            .Select(current => current.Id)
            .Distinct()
            .ToList();
        if (invoiceIds.Count == 0)
            return (true, string.Empty);

        var itemIds = await _dbContext.InvoiceLines
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(line => invoiceIds.Contains(line.InvoiceId) && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
            .Select(line => line.ItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (itemIds.Count == 0)
            return (true, string.Empty);

        var items = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) && !item.IsDeleted)
            .Select(item => new { item.Id, item.OfficeCode, item.TenantCode })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        foreach (var itemId in itemIds)
        {
            if (!items.TryGetValue(itemId, out var item))
                return (false, $"Referenced invoice line item was not found: {itemId}.");

            if (!_officeScopeService.CanReadOfficeForItems(item.OfficeCode, item.TenantCode))
                return (false, $"Referenced invoice line item is outside the readable office scope: {itemId}.");
        }

        return (true, string.Empty);
    }

    private async Task RestoreInvoiceLinesAsync(IEnumerable<Invoice> invoices, CancellationToken cancellationToken)
    {
        var invoiceIds = invoices
            .Select(current => current.Id)
            .Distinct()
            .ToList();
        if (invoiceIds.Count == 0)
            return;

        var lines = await _dbContext.InvoiceLines
            .IgnoreQueryFilters()
            .Where(current => invoiceIds.Contains(current.InvoiceId) && current.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var line in lines)
            line.IsDeleted = false;
    }

    private (bool Success, string Message) EnsureCanWriteInvoiceGroup(IEnumerable<Invoice> invoiceGroup, string actionText)
    {
        foreach (var current in invoiceGroup)
        {
            if (!_officeScopeService.CanWriteOfficeForInvoices(current.ResponsibleOfficeCode, current.TenantCode, current.OfficeCode))
                return (false, $"현재 계정으로 전표 묶음의 모든 버전을 {actionText}할 수 없습니다.");
        }

        return (true, string.Empty);
    }

    private bool CanMutateInventoryTransferFromRecycleBin(InventoryTransfer transfer, out string message)
    {
        if (!_officeScopeService.CanReadInventoryTransferRoute(
                transfer.SourceOfficeCode,
                transfer.TargetOfficeCode,
                transfer.TenantCode))
        {
            message = "Current account cannot access this inventory transfer route.";
            return false;
        }

        if (!_officeScopeService.CanWriteOfficeForDeliveries(transfer.SourceOfficeCode, transfer.TenantCode))
        {
            message = $"Inventory transfer source office is outside the writable delivery scope: {transfer.SourceOfficeCode}.";
            return false;
        }

        var normalizedStatus = InventoryTransferStatusNormalizer.Normalize(
            transfer.TransferStatus,
            transfer.ReceivedByUsername,
            transfer.ReceivedAtUtc,
            transfer.RejectedByUsername,
            transfer.RejectedAtUtc);
        if (IsFinalInventoryTransferStatus(normalizedStatus) &&
            !_officeScopeService.CanWriteOfficeForDeliveries(transfer.TargetOfficeCode, transfer.TenantCode))
        {
            message = $"Inventory transfer target office is outside the writable delivery scope: {transfer.TargetOfficeCode}.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool IsFinalInventoryTransferStatus(string? status)
        => string.Equals(status, InventoryTransferStatusNormalizer.Received, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, InventoryTransferStatusNormalizer.Rejected, StringComparison.OrdinalIgnoreCase);

    private (bool Success, string Message) EnsureCanRestoreLinkedCustomer(Customer customer)
        => !customer.IsDeleted || _officeScopeService.CanWriteOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode)
            ? (true, string.Empty)
            : (false, "현재 계정으로 연결된 거래처를 복원할 수 없습니다.");

    private async Task<(bool Success, string Message)> EnsureCanWriteRentalSettlementProfilesAsync(
        IEnumerable<Invoice> invoices,
        CancellationToken cancellationToken)
    {
        var profileIds = (invoices ?? Enumerable.Empty<Invoice>())
            .Select(invoice => invoice.LinkedRentalBillingProfileId)
            .Where(profileId => profileId.HasValue && profileId.Value != Guid.Empty)
            .Select(profileId => profileId!.Value)
            .Distinct()
            .ToList();

        foreach (var profileId in profileIds)
        {
            var check = await EnsureCanWriteRentalSettlementProfileAsync(profileId, cancellationToken);
            if (!check.Success)
                return check;
        }

        return (true, string.Empty);
    }

    private async Task<(bool Success, string Message)> EnsureCanWriteRentalSettlementProfileAsync(
        Guid? profileId,
        CancellationToken cancellationToken)
    {
        if (!profileId.HasValue || profileId.Value == Guid.Empty)
            return (true, string.Empty);

        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == profileId.Value, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return (false, "연결된 렌탈 청구프로필을 찾을 수 없어 복원할 수 없습니다.");

        return _officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode)
            ? (true, string.Empty)
            : (false, "현재 계정으로 연결된 렌탈 청구프로필을 변경할 수 없어 복원할 수 없습니다.");
    }

    private static (bool Success, string Message) EnsureRecycleBinMutationRevision(
        TrackedEntity entity,
        RecycleBinMutationTargetDto target)
    {
        if (target.ExpectedRevision <= 0 || entity.Revision == target.ExpectedRevision)
            return (true, string.Empty);

        var kindText = GetRecycleBinKindText(target.Kind);
        var reason = OptimisticConcurrencyGuard.BuildExpectedRevisionConflictReason(target.ExpectedRevision, entity.Revision);
        return (false, $"{kindText} 항목이 다른 PC에서 변경되어 휴지통 작업을 중단했습니다. 새로고침 후 다시 시도하세요. ({reason})");
    }

    private static string GetRecycleBinKindText(string? kind)
    {
        return NormalizeKind(kind) switch
        {
            "customer" => "거래처",
            "contract" => "계약서",
            "item" => "품목",
            "company-profile" => "회사설정",
            "customer-category" => "고객분류",
            "price-grade-option" => "가격등급",
            "trade-type-option" => "거래구분",
            "item-category-option" => "품목분류",
            "invoice" => "전표",
            "payment" => "수금/지급",
            "transaction" => "거래내역",
            "inventory-transfer" => "재고이동",
            "rental-management-company" => "렌탈 관리업체",
            "rental-billing-profile" => "렌탈 청구프로필",
            "rental-asset" => "렌탈 자산",
            "rental-billing-log" => "렌탈 청구로그",
            _ => "휴지통"
        };
    }

    private static string NormalizeKind(string? kind)
    {
        return (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "customer" or "customers" or "거래처" => "customer",
            "contract" or "contracts" or "customercontract" or "계약서" => "contract",
            "item" or "items" or "품목" => "item",
            "company-profile" or "companyprofile" or "companyprofiles" or "회사설정" => "company-profile",
            "customer-category" or "customercategory" or "customercategories" or "고객분류" => "customer-category",
            "price-grade-option" or "pricegradeoption" or "pricegradeoptions" or "가격등급" => "price-grade-option",
            "trade-type-option" or "tradetypeoption" or "tradetypeoptions" or "거래구분" => "trade-type-option",
            "item-category-option" or "itemcategoryoption" or "itemcategoryoptions" or "품목분류" => "item-category-option",
            "invoice" or "invoices" or "전표" => "invoice",
            "payment" or "payments" or "수금" or "지급" or "수금/지급" => "payment",
            "transaction" or "transactions" or "거래내역" => "transaction",
            "inventory-transfer" or "inventorytransfer" or "inventorytransfers" or "재고이동" => "inventory-transfer",
            "rental-management-company" or "rentalmanagementcompany" or "rentalmanagementcompanies" or "렌탈관리업체" or "렌탈 관리업체" => "rental-management-company",
            "rental-billing-profile" or "rentalbillingprofile" or "rental-profile" or "rentalprofile" or "렌탈청구프로필" or "렌탈 청구프로필" => "rental-billing-profile",
            "rental-asset" or "rentalasset" or "렌탈자산" or "렌탈 자산" => "rental-asset",
            "rental-billing-log" or "rentalbillinglog" or "rental-log" or "rentallog" or "렌탈청구로그" or "렌탈 청구로그" => "rental-billing-log",
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
            "transaction" => 1,
            "rental-billing-log" => 2,
            "inventory-transfer" => 3,
            "contract" => 4,
            "invoice" => 5,
            "rental-asset" => 6,
            "item" => 7,
            "customer-category" => 8,
            "item-category-option" => 9,
            "price-grade-option" => 10,
            "trade-type-option" => 11,
            "rental-management-company" => 12,
            "rental-billing-profile" => 13,
            "company-profile" => 14,
            "customer" => 15,
            _ => 99
        };
    }

    private static string RemoveIncludedAssetId(string? templateJson, Guid assetId)
    {
        if (assetId == Guid.Empty)
            return templateJson ?? "[]";

        try
        {
            var items = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(templateJson ?? "[]") ?? new List<RentalBillingTemplateItemModel>();
            foreach (var item in items)
            {
                item.IncludedAssetIds = item.IncludedAssetIds
                    .Where(id => id != Guid.Empty && id != assetId)
                    .Distinct()
                    .ToList();
            }

            return JsonSerializer.Serialize(items);
        }
        catch
        {
            return templateJson ?? "[]";
        }
    }

    private static bool BillingTemplateContainsAssetId(string? templateJson, Guid assetId)
    {
        if (assetId == Guid.Empty || string.IsNullOrWhiteSpace(templateJson))
            return false;

        try
        {
            var items = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(templateJson) ?? [];
            return items.Any(item => item.IncludedAssetIds.Any(id => id == assetId));
        }
        catch
        {
            return false;
        }
    }

    private static string GetBillingEligibilityStatusAfterProfilePurge(string? assetStatus)
    {
        if (RentalAssetStatusNormalizer.IsNonOperating(assetStatus))
            return "청구제외";

        return "미확인";
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

    private static string NormalizeTransactionKind(string? transactionKind)
        => (transactionKind ?? string.Empty).Trim().ToLowerInvariant();

    private static string GetTransactionKindLabel(string? transactionKind)
    {
        return NormalizeTransactionKind(transactionKind) switch
        {
            "receipt" => "일반수금",
            "payment" => "일반지급",
            "advance-deposit" => "선수금입금",
            "advance-refund" => "선수금환불",
            "advance-apply" => "선수금차감",
            "invoice-receipt" => "전표수금",
            "invoice-payment" => "전표지급",
            "rental-receipt" => "렌탈수금",
            _ => string.IsNullOrWhiteSpace(transactionKind) ? "거래내역" : transactionKind.Trim()
        };
    }

    private static RecycleBinPurgeRecordDto CreatePurgeRecord(
        string kind,
        Guid entityId,
        string? tenantCode,
        string? officeCode,
        string? fallbackOfficeCode = null)
    {
        var resolvedOfficeCode = ResolvePurgeRecordOfficeCode(officeCode, fallbackOfficeCode);
        return new RecycleBinPurgeRecordDto
        {
            Id = Guid.NewGuid(),
            Kind = NormalizeKind(kind),
            EntityId = entityId,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                tenantCode,
                resolvedOfficeCode,
                TenantScopeCatalog.UsenetGroup,
                resolvedOfficeCode),
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(resolvedOfficeCode, OfficeCodeCatalog.Shared),
            PurgedAtUtc = DateTime.UtcNow,
            IsDeleted = false
        };
    }

    private static string ResolvePurgeRecordOfficeCode(string? officeCode, string? fallbackOfficeCode)
    {
        if (OfficeCodeCatalog.TryNormalizeScope(officeCode, out var normalizedOfficeCode))
            return normalizedOfficeCode;

        if (OfficeCodeCatalog.TryNormalizeScope(fallbackOfficeCode, out var normalizedFallbackOfficeCode))
            return normalizedFallbackOfficeCode;

        return OfficeCodeCatalog.Shared;
    }

    private async Task TouchPurgeRecordsAsync(
        IReadOnlyList<RecycleBinPurgeRecordDto> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
            return;

        foreach (var dto in records
                     .Where(current => current.EntityId != Guid.Empty && !string.IsNullOrWhiteSpace(current.Kind))
                     .GroupBy(current => (NormalizeKind(current.Kind), current.EntityId))
                     .Select(group => group
                         .OrderByDescending(current => current.PurgedAtUtc)
                         .ThenByDescending(current => current.Revision)
                         .First()))
        {
            var kind = NormalizeKind(dto.Kind);
            if (string.IsNullOrWhiteSpace(kind))
                continue;

            var existing = await _dbContext.RecycleBinPurgeRecords
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Kind == kind && current.EntityId == dto.EntityId, cancellationToken);

            if (existing is null)
            {
                existing = new RecycleBinPurgeRecord
                {
                    Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id
                };
                dto.Kind = kind;
                existing.Apply(dto);
                _dbContext.RecycleBinPurgeRecords.Add(existing);
                continue;
            }

            dto.Kind = kind;
            existing.Apply(dto);
            existing.IsDeleted = false;
        }
    }

    private sealed class RentalBillingTemplateItemModel
    {
        public List<Guid> IncludedAssetIds { get; set; } = new();
    }
}
