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
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            BusinessDatabaseName = ResolveRecycleBinBusinessDatabaseName(
                customer.TenantCode,
                customer.OfficeCode,
                customer.ResponsibleOfficeCode),
            Title = customer.NameOriginal,
            Subtitle = JoinSegments(customer.BusinessNumber, customer.Phone),
            Detail = JoinSegments(customer.Address, customer.ContactPerson, customer.Notes),
            DeletedAtUtc = customer.UpdatedAtUtc,
            Revision = customer.Revision
        }));

        var deletedItems = (await ApplyItemScope(
                _db.Items
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(item => item.IsDeleted),
                session)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToListAsync(ct))
            .Where(item => CanWriteItemScope(item, session))
            .ToList();

        entries.AddRange(deletedItems.Select(item => new RecycleBinEntry
        {
            EntityId = item.Id,
            Kind = RecycleBinEntityKind.Item,
            TenantCode = item.TenantCode,
            OfficeCode = item.OfficeCode,
            BusinessDatabaseName = ResolveRecycleBinBusinessDatabaseName(item.TenantCode, item.OfficeCode),
            Title = item.NameOriginal,
            Subtitle = JoinSegments(item.SpecificationOriginal, item.CategoryName, item.Unit),
            Detail = JoinSegments(
                item.CurrentStock != 0m ? $"현재고 {item.CurrentStock:N0}" : null,
                item.SalePrice != 0m ? $"매출단가 {item.SalePrice:N0}원" : null,
                item.Notes),
            DeletedAtUtc = item.UpdatedAtUtc,
            Revision = item.Revision
        }));

        if (CanManageSharedRecycleBin(session))
        {
            var deletedCompanyProfiles = await _db.CompanyProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => profile.IsDeleted)
                .OrderByDescending(profile => profile.UpdatedAtUtc)
                .ToListAsync(ct);

            entries.AddRange(deletedCompanyProfiles.Select(profile => new RecycleBinEntry
            {
                EntityId = profile.Id,
                Kind = RecycleBinEntityKind.CompanyProfile,
                Title = string.IsNullOrWhiteSpace(profile.TradeName) ? "(회사설정)" : profile.TradeName,
                Subtitle = JoinSegments(profile.ProfileName, profile.OfficeCode, profile.BusinessNumber),
                Detail = JoinSegments(profile.Representative, profile.ContactNumber, profile.Email),
                DeletedAtUtc = profile.UpdatedAtUtc,
                Revision = profile.Revision
            }));

            var deletedCustomerCategories = await _db.CustomerCategories
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(category => category.IsDeleted)
                .OrderByDescending(category => category.UpdatedAtUtc)
                .ToListAsync(ct);

            entries.AddRange(deletedCustomerCategories.Select(category => new RecycleBinEntry
            {
                EntityId = category.Id,
                Kind = RecycleBinEntityKind.CustomerCategory,
                Title = category.Name,
                Subtitle = category.IsSystemDefault ? "기본 고객분류" : "사용자 고객분류",
                Detail = string.Empty,
                DeletedAtUtc = category.UpdatedAtUtc,
                Revision = category.Revision
            }));

            var deletedPriceGradeOptions = await _db.PriceGradeOptions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(option => option.IsDeleted)
                .OrderByDescending(option => option.UpdatedAtUtc)
                .ToListAsync(ct);

            entries.AddRange(deletedPriceGradeOptions.Select(option => new RecycleBinEntry
            {
                EntityId = option.Id,
                Kind = RecycleBinEntityKind.PriceGradeOption,
                Title = option.Name,
                Subtitle = JoinSegments(option.PriceSourceDisplay, option.IsSystemDefault ? "기본 가격등급" : null),
                Detail = option.SortOrder != 0 ? $"정렬순서 {option.SortOrder}" : string.Empty,
                DeletedAtUtc = option.UpdatedAtUtc,
                Revision = option.Revision
            }));

            var deletedTradeTypeOptions = await _db.TradeTypeOptions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(option => option.IsDeleted)
                .OrderByDescending(option => option.UpdatedAtUtc)
                .ToListAsync(ct);

            entries.AddRange(deletedTradeTypeOptions.Select(option => new RecycleBinEntry
            {
                EntityId = option.Id,
                Kind = RecycleBinEntityKind.TradeTypeOption,
                Title = option.Name,
                Subtitle = JoinSegments(option.AllowsSales ? "매출" : null, option.AllowsPurchase ? "매입" : null),
                Detail = option.IsSystemDefault ? "기본 거래구분" : string.Empty,
                DeletedAtUtc = option.UpdatedAtUtc,
                Revision = option.Revision
            }));

            var deletedItemCategoryOptions = await _db.ItemCategoryOptions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(option => option.IsDeleted)
                .OrderByDescending(option => option.UpdatedAtUtc)
                .ToListAsync(ct);

            entries.AddRange(deletedItemCategoryOptions.Select(option => new RecycleBinEntry
            {
                EntityId = option.Id,
                Kind = RecycleBinEntityKind.ItemCategoryOption,
                Title = option.Name,
                Subtitle = option.IsSystemDefault ? "기본 품목분류" : string.Empty,
                Detail = option.SortOrder != 0 ? $"정렬순서 {option.SortOrder}" : string.Empty,
                DeletedAtUtc = option.UpdatedAtUtc,
                Revision = option.Revision
            }));
        }

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
                TenantCode = invoice.TenantCode,
                OfficeCode = invoice.OfficeCode,
                ResponsibleOfficeCode = invoice.ResponsibleOfficeCode,
                BusinessDatabaseName = ResolveRecycleBinBusinessDatabaseName(
                    invoice.TenantCode,
                    invoice.OfficeCode,
                    invoice.ResponsibleOfficeCode),
                Title = $"{customerName} · {invoice.InvoiceDate:yyyy-MM-dd}",
                Subtitle = JoinSegments(GetVoucherTypeLabel(invoice.VoucherType), displayNumber),
                Detail = JoinSegments(
                    $"{invoice.TotalAmount:N0}원",
                    group.Count() > 1 ? $"버전 {group.Count():N0}건" : null,
                    string.IsNullOrWhiteSpace(invoice.Memo) ? null : invoice.Memo),
                DeletedAtUtc = group.Max(current => current.UpdatedAtUtc),
                Revision = invoice.Revision
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
                TenantCode = customer.TenantCode,
                OfficeCode = customer.OfficeCode,
                ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                BusinessDatabaseName = ResolveRecycleBinBusinessDatabaseName(
                    customer.TenantCode,
                    customer.OfficeCode,
                    customer.ResponsibleOfficeCode),
                Title = $"{customer.NameOriginal} · {contract.FileName}",
                Subtitle = JoinSegments(contract.ContractType, contract.IsPrimary ? "대표 계약서" : null),
                Detail = JoinSegments(
                    contract.SignedDate.HasValue ? $"체결일 {contract.SignedDate:yyyy-MM-dd}" : null,
                    contract.ExpireDate.HasValue ? $"만료일 {contract.ExpireDate:yyyy-MM-dd}" : null,
                    contract.FileSize > 0 ? $"{contract.FileSize / 1024m:N0} KB" : null,
                    contract.Description),
                DeletedAtUtc = contract.UpdatedAtUtc,
            Revision = contract.Revision
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
                TenantCode = invoice.TenantCode,
                OfficeCode = invoice.OfficeCode,
                ResponsibleOfficeCode = invoice.ResponsibleOfficeCode,
                BusinessDatabaseName = ResolveRecycleBinBusinessDatabaseName(
                    invoice.TenantCode,
                    invoice.OfficeCode,
                    invoice.ResponsibleOfficeCode),
                Title = $"{customerName} · {payment.Amount:N0}원",
                Subtitle = JoinSegments($"전표 {displayNumber}", payment.PaymentDate.ToString("yyyy-MM-dd")),
                Detail = string.IsNullOrWhiteSpace(payment.Note) ? "삭제된 수금/지급 기록" : payment.Note,
                DeletedAtUtc = payment.UpdatedAtUtc,
            Revision = payment.Revision
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
                TenantCode = transaction.TenantCode,
                OfficeCode = transaction.OfficeCode,
                ResponsibleOfficeCode = transaction.ResponsibleOfficeCode,
                BusinessDatabaseName = ResolveRecycleBinBusinessDatabaseName(
                    transaction.TenantCode,
                    transaction.OfficeCode,
                    transaction.ResponsibleOfficeCode),
                Title = $"{customerName} · {GetTransactionKindLabel(transaction.TransactionKind)}",
                Subtitle = JoinSegments(transaction.TransactionDate.ToString("yyyy-MM-dd"), totalAmount > 0m ? $"{totalAmount:N0}원" : null),
                Detail = JoinSegments(transaction.Note, transaction.Memo),
                DeletedAtUtc = transaction.UpdatedAtUtc,
            Revision = transaction.Revision
            };
        }));

        var deletedTransfers = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(transfer => transfer.Lines.Where(line => !line.IsDeleted))
            .Where(transfer => transfer.IsDeleted)
            .OrderByDescending(transfer => transfer.UpdatedAtUtc)
            .ToListAsync(ct);

        entries.AddRange(deletedTransfers
            .Where(transfer => CanAccessInventoryTransferForRecycleBin(transfer, session))
            .Select(transfer => new RecycleBinEntry
            {
                EntityId = transfer.Id,
                Kind = RecycleBinEntityKind.InventoryTransfer,
                Title = string.IsNullOrWhiteSpace(transfer.TransferNumber) ? "(재고이동)" : transfer.TransferNumber,
                Subtitle = JoinSegments(transfer.TransferDate.ToString("yyyy-MM-dd"), transfer.TransferStatus),
                Detail = JoinSegments(
                    transfer.Lines.Count > 0 ? $"품목 {transfer.Lines.Count:N0}건" : null,
                    string.IsNullOrWhiteSpace(transfer.Memo) ? null : transfer.Memo),
                DeletedAtUtc = transfer.UpdatedAtUtc,
            Revision = transfer.Revision
            }));

        var deletedManagementCompanies = await _db.RentalManagementCompanies
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(company => company.IsDeleted)
            .OrderByDescending(company => company.UpdatedAtUtc)
            .ToListAsync(ct);

        entries.AddRange(deletedManagementCompanies
            .Where(company => CanManageRentalSettingsRecycleBinScope(session, company.Code))
            .Select(company => new RecycleBinEntry
            {
                EntityId = company.Id,
                Kind = RecycleBinEntityKind.RentalManagementCompany,
                OfficeCode = company.Code,
                ResponsibleOfficeCode = company.Code,
                ManagementCompanyCode = company.Code,
                BusinessDatabaseName = ResolveRecycleBinBusinessDatabaseName(null, company.Code, company.Code, company.Code),
                Title = string.IsNullOrWhiteSpace(company.Name) ? company.Code : company.Name,
                Subtitle = company.Code,
                Detail = company.IsSystemDefault ? "기본 렌탈 관리업체" : string.Empty,
                DeletedAtUtc = company.UpdatedAtUtc,
            Revision = company.Revision
            }));

        var deletedRentalProfiles = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => profile.IsDeleted)
            .OrderByDescending(profile => profile.UpdatedAtUtc)
            .ToListAsync(ct);

        entries.AddRange(deletedRentalProfiles
            .Where(profile => CanWriteRentalRecycleBinScope(
                session,
                profile.TenantCode,
                profile.OfficeCode,
                profile.ResponsibleOfficeCode,
                profile.ManagementCompanyCode))
            .Select(profile => new RecycleBinEntry
            {
                EntityId = profile.Id,
                Kind = RecycleBinEntityKind.RentalBillingProfile,
                TenantCode = profile.TenantCode,
                OfficeCode = profile.OfficeCode,
                ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
                ManagementCompanyCode = profile.ManagementCompanyCode,
                BusinessDatabaseName = ResolveRecycleBinBusinessDatabaseName(
                    profile.TenantCode,
                    profile.OfficeCode,
                    profile.ResponsibleOfficeCode,
                    profile.ManagementCompanyCode),
                Title = string.IsNullOrWhiteSpace(profile.CustomerName) ? "(거래처 미상)" : profile.CustomerName,
                Subtitle = JoinSegments(profile.InstallSiteName, profile.ItemName),
                Detail = JoinSegments(
                    string.IsNullOrWhiteSpace(profile.BusinessNumber) ? null : $"사업자번호 {profile.BusinessNumber}",
                    string.IsNullOrWhiteSpace(profile.BillingType) ? null : $"청구유형 {profile.BillingType}",
                    profile.MonthlyAmount > 0m ? $"월기준금액 {profile.MonthlyAmount:N0}원" : null),
                DeletedAtUtc = profile.UpdatedAtUtc,
            Revision = profile.Revision
            }));

        var deletedRentalAssets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => asset.IsDeleted)
            .OrderByDescending(asset => asset.UpdatedAtUtc)
            .ToListAsync(ct);

        entries.AddRange(deletedRentalAssets
            .Where(asset => CanWriteRentalRecycleBinScope(
                session,
                asset.TenantCode,
                asset.OfficeCode,
                asset.ResponsibleOfficeCode,
                asset.ManagementCompanyCode))
            .Select(asset => new RecycleBinEntry
            {
                EntityId = asset.Id,
                Kind = RecycleBinEntityKind.RentalAsset,
                TenantCode = asset.TenantCode,
                OfficeCode = asset.OfficeCode,
                ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
                ManagementCompanyCode = asset.ManagementCompanyCode,
                BusinessDatabaseName = ResolveRecycleBinBusinessDatabaseName(
                    asset.TenantCode,
                    asset.OfficeCode,
                    asset.ResponsibleOfficeCode,
                    asset.ManagementCompanyCode),
                Title = string.IsNullOrWhiteSpace(asset.ManagementNumber)
                    ? string.IsNullOrWhiteSpace(asset.ItemName) ? "(렌탈 자산)" : asset.ItemName
                    : $"{asset.ManagementNumber} · {asset.ItemName}".Trim(),
                Subtitle = JoinSegments(asset.CustomerName, string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation),
                Detail = JoinSegments(
                    string.IsNullOrWhiteSpace(asset.MachineNumber) ? null : $"기계번호 {asset.MachineNumber}",
                    string.IsNullOrWhiteSpace(asset.AssetStatus) ? null : $"상태 {asset.AssetStatus}",
                    asset.MonthlyFee > 0m ? $"월요금 {asset.MonthlyFee:N0}원" : null),
                DeletedAtUtc = asset.UpdatedAtUtc,
            Revision = asset.Revision
            }));

        var deletedRentalLogs = await _db.RentalBillingLogs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(log => log.IsDeleted)
            .OrderByDescending(log => log.UpdatedAtUtc)
            .ToListAsync(ct);

        var deletedRentalLogProfileIds = deletedRentalLogs
            .Select(log => log.BillingProfileId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var deletedRentalLogProfiles = deletedRentalLogProfileIds.Count == 0
            ? new Dictionary<Guid, LocalRentalBillingProfile>()
            : await _db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => deletedRentalLogProfileIds.Contains(profile.Id))
                .ToDictionaryAsync(profile => profile.Id, ct);

        entries.AddRange(deletedRentalLogs
            .Where(log =>
            {
                deletedRentalLogProfiles.TryGetValue(log.BillingProfileId, out var profile);
                return CanWriteRentalRecycleBinScope(
                    session,
                    profile?.TenantCode ?? log.TenantCode,
                    profile?.OfficeCode ?? log.OfficeCode,
                    profile?.ResponsibleOfficeCode ?? log.ResponsibleOfficeCode,
                    profile?.ManagementCompanyCode);
            })
            .Select(log =>
            {
                deletedRentalLogProfiles.TryGetValue(log.BillingProfileId, out var profile);
                var title = profile is null
                    ? $"청구로그 {log.BillingYearMonth}"
                    : $"{(string.IsNullOrWhiteSpace(profile.CustomerName) ? "(거래처 미상)" : profile.CustomerName)} · {log.BillingYearMonth}";
                return new RecycleBinEntry
                {
                    EntityId = log.Id,
                    Kind = RecycleBinEntityKind.RentalBillingLog,
                    TenantCode = profile?.TenantCode ?? log.TenantCode,
                    OfficeCode = profile?.OfficeCode ?? log.OfficeCode,
                    ResponsibleOfficeCode = profile?.ResponsibleOfficeCode ?? log.ResponsibleOfficeCode,
                    ManagementCompanyCode = profile?.ManagementCompanyCode ?? string.Empty,
                    BusinessDatabaseName = ResolveRecycleBinBusinessDatabaseName(
                        profile?.TenantCode ?? log.TenantCode,
                        profile?.OfficeCode ?? log.OfficeCode,
                        profile?.ResponsibleOfficeCode ?? log.ResponsibleOfficeCode,
                        profile?.ManagementCompanyCode),
                    Title = title,
                    Subtitle = JoinSegments(
                        log.ScheduledDate.ToString("yyyy-MM-dd"),
                        string.IsNullOrWhiteSpace(log.Status) ? null : log.Status),
                    Detail = JoinSegments(
                        log.BilledAmount > 0m ? $"청구금액 {log.BilledAmount:N0}원" : null,
                        string.IsNullOrWhiteSpace(log.Note) ? null : log.Note),
                    DeletedAtUtc = log.UpdatedAtUtc,
            Revision = log.Revision
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
            RecycleBinEntityKind.CompanyProfile => RestoreCompanyProfileAsync(entityId, session, ct),
            RecycleBinEntityKind.CustomerCategory => RestoreCustomerCategoryAsync(entityId, session, ct),
            RecycleBinEntityKind.PriceGradeOption => RestorePriceGradeOptionAsync(entityId, session, ct),
            RecycleBinEntityKind.TradeTypeOption => RestoreTradeTypeOptionAsync(entityId, session, ct),
            RecycleBinEntityKind.ItemCategoryOption => RestoreItemCategoryOptionAsync(entityId, session, ct),
            RecycleBinEntityKind.Invoice => RestoreInvoiceAsync(entityId, session, ct),
            RecycleBinEntityKind.Payment => RestoreDeletedPaymentAsync(entityId, session, ct),
            RecycleBinEntityKind.Transaction => RestoreTransactionAsync(entityId, session, ct),
            RecycleBinEntityKind.InventoryTransfer => RestoreInventoryTransferAsync(entityId, session, ct),
            RecycleBinEntityKind.RentalManagementCompany => RestoreRentalManagementCompanyAsync(entityId, session, ct),
            RecycleBinEntityKind.RentalBillingProfile => RestoreRentalBillingProfileAsync(entityId, session, ct),
            RecycleBinEntityKind.RentalAsset => RestoreRentalAssetAsync(entityId, session, ct),
            RecycleBinEntityKind.RentalBillingLog => RestoreRentalBillingLogAsync(entityId, session, ct),
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
            RecycleBinEntityKind.CompanyProfile => PermanentlyDeleteCompanyProfileAsync(entityId, session, ct),
            RecycleBinEntityKind.CustomerCategory => PermanentlyDeleteCustomerCategoryAsync(entityId, session, ct),
            RecycleBinEntityKind.PriceGradeOption => PermanentlyDeletePriceGradeOptionAsync(entityId, session, ct),
            RecycleBinEntityKind.TradeTypeOption => PermanentlyDeleteTradeTypeOptionAsync(entityId, session, ct),
            RecycleBinEntityKind.ItemCategoryOption => PermanentlyDeleteItemCategoryOptionAsync(entityId, session, ct),
            RecycleBinEntityKind.Invoice => PermanentlyDeleteInvoiceAsync(entityId, session, ct),
            RecycleBinEntityKind.Payment => PermanentlyDeletePaymentAsync(entityId, session, ct),
            RecycleBinEntityKind.Transaction => PermanentlyDeleteTransactionAsync(entityId, session, ct),
            RecycleBinEntityKind.InventoryTransfer => PermanentlyDeleteInventoryTransferAsync(entityId, session, ct),
            RecycleBinEntityKind.RentalManagementCompany => PermanentlyDeleteRentalManagementCompanyAsync(entityId, session, ct),
            RecycleBinEntityKind.RentalBillingProfile => PermanentlyDeleteRentalBillingProfileAsync(entityId, session, ct),
            RecycleBinEntityKind.RentalAsset => PermanentlyDeleteRentalAssetAsync(entityId, session, ct),
            RecycleBinEntityKind.RentalBillingLog => PermanentlyDeleteRentalBillingLogAsync(entityId, session, ct),
            _ => Task.FromResult(OfficeMutationResult.Denied("영구삭제할 수 없는 휴지통 항목입니다."))
        };
    }

    public Task<OfficeMutationResult> ApplyServerPurgeRecycleBinEntryAsync(
        RecycleBinEntityKind kind,
        Guid entityId,
        CancellationToken ct = default)
    {
        return kind switch
        {
            RecycleBinEntityKind.Customer => ApplyServerPurgedCustomerAsync(entityId, ct),
            RecycleBinEntityKind.CustomerContract => ApplyServerPurgedCustomerContractAsync(entityId, ct),
            RecycleBinEntityKind.Item => ApplyServerPurgedItemAsync(entityId, ct),
            RecycleBinEntityKind.CompanyProfile => ApplyServerPurgedCompanyProfileAsync(entityId, ct),
            RecycleBinEntityKind.CustomerCategory => ApplyServerPurgedCustomerCategoryAsync(entityId, ct),
            RecycleBinEntityKind.PriceGradeOption => ApplyServerPurgedPriceGradeOptionAsync(entityId, ct),
            RecycleBinEntityKind.TradeTypeOption => ApplyServerPurgedTradeTypeOptionAsync(entityId, ct),
            RecycleBinEntityKind.ItemCategoryOption => ApplyServerPurgedItemCategoryOptionAsync(entityId, ct),
            RecycleBinEntityKind.Invoice => ApplyServerPurgedInvoiceAsync(entityId, ct),
            RecycleBinEntityKind.Payment => ApplyServerPurgedPaymentAsync(entityId, ct),
            RecycleBinEntityKind.Transaction => ApplyServerPurgedTransactionAsync(entityId, ct),
            RecycleBinEntityKind.InventoryTransfer => ApplyServerPurgedInventoryTransferAsync(entityId, ct),
            RecycleBinEntityKind.RentalManagementCompany => ApplyServerPurgedRentalManagementCompanyAsync(entityId, ct),
            RecycleBinEntityKind.RentalBillingProfile => ApplyServerPurgedRentalBillingProfileAsync(entityId, ct),
            RecycleBinEntityKind.RentalAsset => ApplyServerPurgedRentalAssetAsync(entityId, ct),
            RecycleBinEntityKind.RentalBillingLog => ApplyServerPurgedRentalBillingLogAsync(entityId, ct),
            _ => Task.FromResult(OfficeMutationResult.Denied("서버 영구삭제 반영을 지원하지 않는 휴지통 항목입니다."))
        };
    }

    public async Task MarkRecycleBinServerMutationCleanAsync(
        RecycleBinEntityKind kind,
        Guid entityId,
        CancellationToken ct = default)
    {
        var customerIds = new HashSet<Guid>();
        var invoiceIds = new HashSet<Guid>();
        var rentalProfileIds = new HashSet<Guid>();

        switch (kind)
        {
            case RecycleBinEntityKind.Customer:
                MarkServerMirroredClean(await FindSyncEntityAsync(_db.Customers, entityId, ct));
                break;
            case RecycleBinEntityKind.CustomerContract:
            {
                var contract = await FindSyncEntityAsync(_db.CustomerContracts, entityId, ct);
                MarkServerMirroredClean(contract);
                if (contract is not null)
                    customerIds.Add(contract.CustomerId);
                break;
            }
            case RecycleBinEntityKind.Item:
                MarkServerMirroredClean(await FindSyncEntityAsync(_db.Items, entityId, ct));
                break;
            case RecycleBinEntityKind.CompanyProfile:
                MarkServerMirroredClean(await FindSyncEntityAsync(_db.CompanyProfiles, entityId, ct));
                break;
            case RecycleBinEntityKind.CustomerCategory:
                MarkServerMirroredClean(await FindSyncEntityAsync(_db.CustomerCategories, entityId, ct));
                break;
            case RecycleBinEntityKind.PriceGradeOption:
                MarkServerMirroredClean(await FindSyncEntityAsync(_db.PriceGradeOptions, entityId, ct));
                break;
            case RecycleBinEntityKind.TradeTypeOption:
                MarkServerMirroredClean(await FindSyncEntityAsync(_db.TradeTypeOptions, entityId, ct));
                break;
            case RecycleBinEntityKind.ItemCategoryOption:
                MarkServerMirroredClean(await FindSyncEntityAsync(_db.ItemCategoryOptions, entityId, ct));
                break;
            case RecycleBinEntityKind.Invoice:
                await MarkInvoiceGroupServerMirroredCleanAsync(entityId, customerIds, ct);
                break;
            case RecycleBinEntityKind.Payment:
            {
                var payment = await FindSyncEntityAsync(_db.Payments, entityId, ct);
                MarkServerMirroredClean(payment);
                if (payment is not null)
                    invoiceIds.Add(payment.InvoiceId);
                break;
            }
            case RecycleBinEntityKind.Transaction:
            {
                var transaction = await FindSyncEntityAsync(_db.Transactions, entityId, ct);
                MarkServerMirroredClean(transaction);
                if (transaction is not null)
                {
                    customerIds.Add(transaction.CustomerId);
                    if (transaction.LinkedInvoiceId.HasValue && transaction.LinkedInvoiceId.Value != Guid.Empty)
                        invoiceIds.Add(transaction.LinkedInvoiceId.Value);
                    if (transaction.LinkedRentalBillingProfileId.HasValue && transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
                        rentalProfileIds.Add(transaction.LinkedRentalBillingProfileId.Value);
                }
                break;
            }
            case RecycleBinEntityKind.InventoryTransfer:
                MarkServerMirroredClean(await FindSyncEntityAsync(_db.InventoryTransfers, entityId, ct));
                break;
            case RecycleBinEntityKind.RentalManagementCompany:
                MarkServerMirroredClean(await FindSyncEntityAsync(_db.RentalManagementCompanies, entityId, ct));
                break;
            case RecycleBinEntityKind.RentalBillingProfile:
            {
                var profile = await FindSyncEntityAsync(_db.RentalBillingProfiles, entityId, ct);
                MarkServerMirroredClean(profile);
                if (profile?.CustomerId is Guid customerId && customerId != Guid.Empty)
                    customerIds.Add(customerId);
                break;
            }
            case RecycleBinEntityKind.RentalAsset:
            {
                var asset = await FindSyncEntityAsync(_db.RentalAssets, entityId, ct);
                MarkServerMirroredClean(asset);
                if (asset?.CustomerId is Guid customerId && customerId != Guid.Empty)
                    customerIds.Add(customerId);
                if (asset?.BillingProfileId is Guid profileId && profileId != Guid.Empty)
                    rentalProfileIds.Add(profileId);
                break;
            }
            case RecycleBinEntityKind.RentalBillingLog:
            {
                var log = await FindSyncEntityAsync(_db.RentalBillingLogs, entityId, ct);
                MarkServerMirroredClean(log);
                if (log is not null && log.BillingProfileId != Guid.Empty)
                    rentalProfileIds.Add(log.BillingProfileId);
                break;
            }
        }

        foreach (var invoiceId in invoiceIds)
            await MarkInvoiceGroupServerMirroredCleanAsync(invoiceId, customerIds, ct);

        if (rentalProfileIds.Count > 0)
        {
            var profiles = await _db.RentalBillingProfiles.IgnoreQueryFilters()
                .Where(profile => rentalProfileIds.Contains(profile.Id))
                .ToListAsync(ct);
            foreach (var profile in profiles)
            {
                MarkServerMirroredClean(profile);
                if (profile.CustomerId is Guid customerId && customerId != Guid.Empty)
                    customerIds.Add(customerId);
            }
        }

        if (customerIds.Count > 0)
        {
            var customers = await _db.Customers.IgnoreQueryFilters()
                .Where(customer => customerIds.Contains(customer.Id))
                .ToListAsync(ct);
            foreach (var customer in customers)
                MarkServerMirroredClean(customer);
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkInvoiceGroupServerMirroredCleanAsync(
        Guid invoiceId,
        HashSet<Guid> customerIds,
        CancellationToken ct)
    {
        var target = await FindSyncEntityAsync(_db.Invoices, invoiceId, ct);
        if (target is null)
            return;

        var versionGroupId = target.VersionGroupId == Guid.Empty ? target.Id : target.VersionGroupId;
        var groupInvoices = await _db.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoice.Id == target.Id || invoice.VersionGroupId == versionGroupId)
            .ToListAsync(ct);
        foreach (var invoice in groupInvoices)
        {
            MarkServerMirroredClean(invoice);
            customerIds.Add(invoice.CustomerId);
        }
    }

    private static async Task<T?> FindSyncEntityAsync<T>(
        DbSet<T> set,
        Guid entityId,
        CancellationToken ct)
        where T : LocalSyncEntity
        => await set.IgnoreQueryFilters().FirstOrDefaultAsync(entity => entity.Id == entityId, ct);

    private static void MarkServerMirroredClean(LocalSyncEntity? entity)
    {
        if (entity is not null)
            entity.IsDirty = false;
    }

    private async Task<OfficeMutationResult> RestoreCompanyProfileAsync(
        Guid profileId,
        SessionState session,
        CancellationToken ct)
    {
        if (!CanManageSharedRecycleBin(session))
            return OfficeMutationResult.Denied("권한이 없어 해당 회사설정을 복원할 수 없습니다.");

        var profile = await _db.CompanyProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, ct);
        if (profile is null)
            return OfficeMutationResult.Missing("복원할 회사설정을 찾을 수 없습니다.");
        if (!profile.IsDeleted)
            return OfficeMutationResult.Ok(profile.Id, "이미 활성 상태인 회사설정입니다.");

        var now = DateTime.UtcNow;
        RestoreEntity(profile, now);
        profile.IsActive = true;
        AddRestoreAudit(nameof(LocalCompanyProfile), profile.Id, new
        {
            profile.ProfileName,
            profile.OfficeCode,
            profile.TradeName
        }, session, now);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(profile.Id, "회사설정을 휴지통에서 복원했습니다.");
    }

    private async Task<OfficeMutationResult> RestoreCustomerCategoryAsync(
        Guid categoryId,
        SessionState session,
        CancellationToken ct)
    {
        if (!CanManageSharedRecycleBin(session))
            return OfficeMutationResult.Denied("권한이 없어 해당 고객분류를 복원할 수 없습니다.");

        var category = await _db.CustomerCategories
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == categoryId, ct);
        if (category is null)
            return OfficeMutationResult.Missing("복원할 고객분류를 찾을 수 없습니다.");
        if (!category.IsDeleted)
            return OfficeMutationResult.Ok(category.Id, "이미 활성 상태인 고객분류입니다.");

        var normalizedName = DefaultCustomerCategories.NormalizeName(category.Name);
        var customerCategories = await _db.CustomerCategories
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id != category.Id && !current.IsDeleted)
            .ToListAsync(ct);
        var hasActiveDuplicate = customerCategories.Any(current =>
            string.Equals(DefaultCustomerCategories.NormalizeName(current.Name), normalizedName, StringComparison.CurrentCultureIgnoreCase));
        if (hasActiveDuplicate)
            return OfficeMutationResult.Denied("같은 이름의 고객분류가 이미 있어 복원할 수 없습니다.");

        var now = DateTime.UtcNow;
        RestoreEntity(category, now);
        AddRestoreAudit(nameof(LocalCustomerCategory), category.Id, new
        {
            category.Name,
            category.IsSystemDefault
        }, session, now);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(category.Id, "고객분류를 휴지통에서 복원했습니다.");
    }

    private async Task<OfficeMutationResult> RestorePriceGradeOptionAsync(
        Guid optionId,
        SessionState session,
        CancellationToken ct)
    {
        if (!CanManageSharedRecycleBin(session))
            return OfficeMutationResult.Denied("권한이 없어 해당 가격등급을 복원할 수 없습니다.");

        var option = await _db.PriceGradeOptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == optionId, ct);
        if (option is null)
            return OfficeMutationResult.Missing("복원할 가격등급을 찾을 수 없습니다.");
        if (!option.IsDeleted)
            return OfficeMutationResult.Ok(option.Id, "이미 활성 상태인 가격등급입니다.");

        var normalizedName = (option.Name ?? string.Empty).Trim();
        var priceGradeOptions = await _db.PriceGradeOptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id != option.Id && !current.IsDeleted)
            .ToListAsync(ct);
        var hasActiveDuplicate = priceGradeOptions.Any(current =>
            string.Equals((current.Name ?? string.Empty).Trim(), normalizedName, StringComparison.CurrentCultureIgnoreCase));
        if (hasActiveDuplicate)
            return OfficeMutationResult.Denied("같은 이름의 가격등급이 이미 있어 복원할 수 없습니다.");

        var now = DateTime.UtcNow;
        RestoreEntity(option, now);
        option.IsActive = true;
        AddRestoreAudit(nameof(LocalPriceGradeOption), option.Id, new
        {
            option.Name,
            option.PriceSource,
            option.SortOrder
        }, session, now);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(option.Id, "가격등급을 휴지통에서 복원했습니다.");
    }

    private async Task<OfficeMutationResult> RestoreTradeTypeOptionAsync(
        Guid optionId,
        SessionState session,
        CancellationToken ct)
    {
        if (!CanManageSharedRecycleBin(session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래구분을 복원할 수 없습니다.");

        var option = await _db.TradeTypeOptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == optionId, ct);
        if (option is null)
            return OfficeMutationResult.Missing("복원할 거래구분을 찾을 수 없습니다.");
        if (!option.IsDeleted)
            return OfficeMutationResult.Ok(option.Id, "이미 활성 상태인 거래구분입니다.");

        if (CustomerClassificationNormalizer.TradeTypeDefinition.Find(option.Name) is null)
            return OfficeMutationResult.Denied("거래구분 기준값이 아니어서 복원할 수 없습니다.");

        var tradeTypeOptions = await _db.TradeTypeOptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id != option.Id && !current.IsDeleted)
            .ToListAsync(ct);
        var hasActiveDuplicate = tradeTypeOptions.Any(current =>
            string.Equals(current.Name, option.Name, StringComparison.CurrentCultureIgnoreCase));
        if (hasActiveDuplicate)
            return OfficeMutationResult.Denied("같은 이름의 거래구분이 이미 있어 복원할 수 없습니다.");

        var now = DateTime.UtcNow;
        RestoreEntity(option, now);
        option.IsActive = true;
        AddRestoreAudit(nameof(LocalTradeTypeOption), option.Id, new
        {
            option.Name,
            option.AllowsSales,
            option.AllowsPurchase
        }, session, now);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(option.Id, "거래구분을 휴지통에서 복원했습니다.");
    }

    private async Task<OfficeMutationResult> RestoreItemCategoryOptionAsync(
        Guid optionId,
        SessionState session,
        CancellationToken ct)
    {
        if (!CanManageSharedRecycleBin(session))
            return OfficeMutationResult.Denied("권한이 없어 해당 품목분류를 복원할 수 없습니다.");

        var option = await _db.ItemCategoryOptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == optionId, ct);
        if (option is null)
            return OfficeMutationResult.Missing("복원할 품목분류를 찾을 수 없습니다.");
        if (!option.IsDeleted)
            return OfficeMutationResult.Ok(option.Id, "이미 활성 상태인 품목분류입니다.");

        var normalizedNameKey = RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name);
        var itemCategoryOptions = await _db.ItemCategoryOptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id != option.Id && !current.IsDeleted)
            .ToListAsync(ct);
        var hasActiveDuplicate = itemCategoryOptions.Any(current =>
            string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(current.Name), normalizedNameKey, StringComparison.OrdinalIgnoreCase));
        if (hasActiveDuplicate)
            return OfficeMutationResult.Denied("같은 이름의 품목분류가 이미 있어 복원할 수 없습니다.");

        var now = DateTime.UtcNow;
        RestoreEntity(option, now);
        option.IsActive = true;
        AddRestoreAudit(nameof(LocalItemCategoryOption), option.Id, new
        {
            option.Name,
            option.SortOrder
        }, session, now);
        await _db.SaveChangesAsync(ct);
        RaiseInventoryStateChanged();
        return OfficeMutationResult.Ok(option.Id, "품목분류를 휴지통에서 복원했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedCompanyProfileAsync(
        Guid profileId,
        CancellationToken ct)
    {
        var profile = await _db.CompanyProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, ct);
        if (profile is null)
            return OfficeMutationResult.Ok(profileId, "회사설정 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");

        _db.CompanyProfiles.Remove(profile);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(profileId, "회사설정 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedCustomerCategoryAsync(
        Guid categoryId,
        CancellationToken ct)
    {
        var category = await _db.CustomerCategories
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == categoryId, ct);
        if (category is null)
            return OfficeMutationResult.Ok(categoryId, "고객분류 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");

        _db.CustomerCategories.Remove(category);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(categoryId, "고객분류 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedPriceGradeOptionAsync(
        Guid optionId,
        CancellationToken ct)
    {
        var option = await _db.PriceGradeOptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == optionId, ct);
        if (option is null)
            return OfficeMutationResult.Ok(optionId, "가격등급 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");

        _db.PriceGradeOptions.Remove(option);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(optionId, "가격등급 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedTradeTypeOptionAsync(
        Guid optionId,
        CancellationToken ct)
    {
        var option = await _db.TradeTypeOptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == optionId, ct);
        if (option is null)
            return OfficeMutationResult.Ok(optionId, "거래구분 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");

        _db.TradeTypeOptions.Remove(option);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(optionId, "거래구분 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedItemCategoryOptionAsync(
        Guid optionId,
        CancellationToken ct)
    {
        var option = await _db.ItemCategoryOptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == optionId, ct);
        if (option is null)
            return OfficeMutationResult.Ok(optionId, "품목분류 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");

        _db.ItemCategoryOptions.Remove(option);
        await _db.SaveChangesAsync(ct);
        RaiseInventoryStateChanged();
        return OfficeMutationResult.Ok(optionId, "품목분류 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedInventoryTransferAsync(
        Guid transferId,
        CancellationToken ct)
    {
        var transfer = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(current => current.Lines)
            .FirstOrDefaultAsync(current => current.Id == transferId, ct);
        if (transfer is null)
            return OfficeMutationResult.Ok(transferId, "재고이동 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");

        if (!string.IsNullOrWhiteSpace(transfer.ReceiveEvidencePath))
            TryDeleteLocalFile(transfer.ReceiveEvidencePath);

        _db.InventoryTransferLines.RemoveRange(transfer.Lines);
        _db.InventoryTransfers.Remove(transfer);
        await _db.SaveChangesAsync(ct);
        await RebuildInventorySnapshotsAsync(CreateServerPurgeInvoiceSaveContext(), ct);
        return OfficeMutationResult.Ok(transferId, "재고이동 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedRentalManagementCompanyAsync(
        Guid companyId,
        CancellationToken ct)
    {
        var company = await _db.RentalManagementCompanies
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == companyId, ct);
        if (company is null)
            return OfficeMutationResult.Ok(companyId, "렌탈 관리업체 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");

        _db.RentalManagementCompanies.Remove(company);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(companyId, "렌탈 관리업체 서버 영구삭제를 로컬에 반영했습니다.");
    }

    public async Task<OfficeMutationResult> RestoreCustomerAsync(
        Guid customerId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanManageCustomerContracts(session))
            return OfficeMutationResult.Denied("현재 계정은 거래처를 복원할 권한이 없습니다.");

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

    private async Task<OfficeMutationResult> ApplyServerPurgedCustomerAsync(
        Guid customerId,
        CancellationToken ct)
    {
        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == customerId, ct);

        var contracts = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == customerId)
            .ToListAsync(ct);

        var assignmentHistories = await _db.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == customerId)
            .ToListAsync(ct);

        if (customer is null && contracts.Count == 0 && assignmentHistories.Count == 0)
            return OfficeMutationResult.Ok(customerId, "거래처 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");

        var now = DateTime.UtcNow;
        foreach (var history in assignmentHistories)
        {
            history.CustomerId = null;
            history.IsDirty = false;
            history.UpdatedAtUtc = now;
        }

        _db.CustomerContracts.RemoveRange(contracts);
        if (customer is not null)
            _db.Customers.Remove(customer);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(customerId, "거래처 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedCustomerContractAsync(
        Guid contractId,
        CancellationToken ct)
    {
        var contract = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contractId, ct);
        if (contract is null)
            return OfficeMutationResult.Ok(contractId, "계약서 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");

        _db.CustomerContracts.Remove(contract);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(contractId, "계약서 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedItemAsync(
        Guid itemId,
        CancellationToken ct)
    {
        var item = await _db.Items
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == itemId, ct);

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

        var movements = await _db.InventoryMovements.Where(current => current.ItemId == itemId).ToListAsync(ct);
        var stockLayers = await _db.StockLayers.Where(current => current.ItemId == itemId).ToListAsync(ct);
        var warehouseStocks = await _db.ItemWarehouseStocks.Where(current => current.ItemId == itemId).ToListAsync(ct);
        var rentalAssets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .Where(current => current.ItemId == itemId)
            .ToListAsync(ct);
        var rentalBillingProfiles = await GetBillingProfilesContainingItemIdAsync(itemId, ct);
        if (item is null &&
            invoiceLines.Count == 0 &&
            transferLines.Count == 0 &&
            invoiceLineSerials.Count == 0 &&
            serialLedgers.Count == 0 &&
            movements.Count == 0 &&
            stockLayers.Count == 0 &&
            warehouseStocks.Count == 0 &&
            rentalAssets.Count == 0 &&
            rentalBillingProfiles.Count == 0)
        {
            return OfficeMutationResult.Ok(itemId, "품목 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");
        }

        var now = DateTime.UtcNow;
        foreach (var asset in rentalAssets)
        {
            asset.ItemId = null;
            asset.IsDirty = false;
            asset.UpdatedAtUtc = now;
        }

        foreach (var profile in rentalBillingProfiles)
        {
            var normalizedJson = RemoveItemId(profile.BillingTemplateJson, itemId);
            if (string.Equals(normalizedJson, profile.BillingTemplateJson, StringComparison.Ordinal))
                continue;

            profile.BillingTemplateJson = normalizedJson;
            profile.IsDirty = false;
            profile.UpdatedAtUtc = now;
        }

        _db.InventoryMovements.RemoveRange(movements);
        _db.StockLayers.RemoveRange(stockLayers);
        _db.ItemWarehouseStocks.RemoveRange(warehouseStocks);
        if (item is not null)
            _db.Items.Remove(item);
        await _db.SaveChangesAsync(ct);

        await RebuildInventorySnapshotsAsync(CreateServerPurgeInvoiceSaveContext(), ct);
        RaiseInventoryStateChanged();
        return OfficeMutationResult.Ok(itemId, "품목 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedInvoiceAsync(
        Guid invoiceId,
        CancellationToken ct)
    {
        var target = await _db.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == invoiceId, ct);

        var groupInvoices = target is null
            ? await _db.Invoices
                .IgnoreQueryFilters()
                .Where(current => current.Id == invoiceId || current.VersionGroupId == invoiceId)
                .ToListAsync(ct)
            : await _db.Invoices
                .IgnoreQueryFilters()
                .Where(current =>
                    current.Id == target.Id ||
                    current.VersionGroupId == (target.VersionGroupId == Guid.Empty ? target.Id : target.VersionGroupId))
                .ToListAsync(ct);
        var invoiceIds = groupInvoices
            .Select(current => current.Id)
            .Append(invoiceId)
            .Distinct()
            .ToList();
        var linkedPayments = await _db.Payments
            .IgnoreQueryFilters()
            .Where(current => invoiceIds.Contains(current.InvoiceId))
            .ToListAsync(ct);
        var staleLinkedTransactions = await _db.Transactions
            .IgnoreQueryFilters()
            .Where(current => current.LinkedInvoiceId.HasValue &&
                              invoiceIds.Contains(current.LinkedInvoiceId.Value) &&
                              current.IsDeleted)
            .ToListAsync(ct);
        var staleLinkedTransactionIds = staleLinkedTransactions
            .Select(current => current.Id)
            .Distinct()
            .ToList();
        var staleLinkedTransactionAttachments = staleLinkedTransactionIds.Count == 0
            ? new List<LocalTransactionAttachment>()
            : await _db.TransactionAttachments
                .IgnoreQueryFilters()
                .Where(current => staleLinkedTransactionIds.Contains(current.TransactionId))
                .ToListAsync(ct);
        var invoiceLines = await _db.InvoiceLines
            .IgnoreQueryFilters()
            .Where(current => invoiceIds.Contains(current.InvoiceId))
            .ToListAsync(ct);
        var hasInventoryResidue = await _db.InvoiceLineSerials.AnyAsync(current => invoiceIds.Contains(current.InvoiceId), ct) ||
                                  await _db.InventoryMovements.AnyAsync(current => current.InvoiceId.HasValue && invoiceIds.Contains(current.InvoiceId.Value), ct) ||
                                  await _db.StockLayers.AnyAsync(current => current.SourceInvoiceId.HasValue && invoiceIds.Contains(current.SourceInvoiceId.Value), ct) ||
                                  await _db.CostAllocations.AnyAsync(current =>
                                      invoiceIds.Contains(current.SalesInvoiceId) ||
                                      (current.PurchaseInvoiceId.HasValue && invoiceIds.Contains(current.PurchaseInvoiceId.Value)), ct) ||
                                  await _db.SerialLedgers.AnyAsync(current =>
                                      (current.SourcePurchaseInvoiceId.HasValue && invoiceIds.Contains(current.SourcePurchaseInvoiceId.Value)) ||
                                      (current.SourceSalesInvoiceId.HasValue && invoiceIds.Contains(current.SourceSalesInvoiceId.Value)) ||
                                      (current.LastInvoiceId.HasValue && invoiceIds.Contains(current.LastInvoiceId.Value)), ct);
        if (groupInvoices.Count == 0 &&
            linkedPayments.Count == 0 &&
            staleLinkedTransactions.Count == 0 &&
            staleLinkedTransactionAttachments.Count == 0 &&
            invoiceLines.Count == 0 &&
            !hasInventoryResidue)
        {
            return OfficeMutationResult.Ok(invoiceId, "전표 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");
        }

        _db.Payments.RemoveRange(linkedPayments);
        _db.TransactionAttachments.RemoveRange(staleLinkedTransactionAttachments);
        _db.Transactions.RemoveRange(staleLinkedTransactions);
        _db.InvoiceLines.RemoveRange(invoiceLines);
        _db.Invoices.RemoveRange(groupInvoices);
        await _db.SaveChangesAsync(ct);

        await RebuildInventorySnapshotsAsync(CreateServerPurgeInvoiceSaveContext(), ct);
        foreach (var attachment in staleLinkedTransactionAttachments)
            TryDeleteAttachmentFile(attachment);

        return OfficeMutationResult.Ok(invoiceId, "전표 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedPaymentAsync(
        Guid paymentId,
        CancellationToken ct)
    {
        var payment = await _db.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == paymentId, ct);
        var sameIdTransaction = await _db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == paymentId, ct);
        if (sameIdTransaction is { IsDeleted: false })
            return OfficeMutationResult.Ok(paymentId, "활성 거래내역에서 생성된 전표 수금/지급 기록이어서 서버 영구삭제 반영을 보류했습니다.");

        var transactionAttachments = await _db.TransactionAttachments
            .IgnoreQueryFilters()
            .Where(current => current.TransactionId == paymentId)
            .ToListAsync(ct);
        if (payment is null && sameIdTransaction is null && transactionAttachments.Count == 0)
            return OfficeMutationResult.Ok(paymentId, "수금/지급 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");

        Guid? rentalBillingProfileId = sameIdTransaction?.LinkedRentalBillingProfileId;
        Guid? rentalBillingRunId = sameIdTransaction?.LinkedRentalBillingRunId;
        if ((!rentalBillingProfileId.HasValue || rentalBillingProfileId.Value == Guid.Empty) &&
            payment is not null)
        {
            var linkedInvoice = await _db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == payment.InvoiceId, ct);
            rentalBillingProfileId = linkedInvoice?.LinkedRentalBillingProfileId;
            rentalBillingRunId = linkedInvoice?.LinkedRentalBillingRunId;
        }

        if (payment is not null)
            _db.Payments.Remove(payment);
        _db.TransactionAttachments.RemoveRange(transactionAttachments);
        if (sameIdTransaction is not null)
            _db.Transactions.Remove(sameIdTransaction);
        await _db.SaveChangesAsync(ct);

        if (rentalBillingProfileId.HasValue && rentalBillingProfileId.Value != Guid.Empty)
            await RecalculateRentalSettlementAsync(rentalBillingProfileId.Value, rentalBillingRunId, ct);

        foreach (var attachment in transactionAttachments)
            TryDeleteAttachmentFile(attachment);

        return OfficeMutationResult.Ok(paymentId, "수금/지급 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedTransactionAsync(
        Guid transactionId,
        CancellationToken ct)
    {
        var transaction = await _db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transactionId, ct);

        var attachments = await _db.TransactionAttachments
            .IgnoreQueryFilters()
            .Where(current => current.TransactionId == transactionId)
            .ToListAsync(ct);

        var linkedPayment = await _db.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transactionId, ct);
        if (transaction is null && attachments.Count == 0 && linkedPayment is null)
            return OfficeMutationResult.Ok(transactionId, "거래내역 서버 영구삭제 상태가 이미 로컬에 반영되어 있습니다.");

        Guid? rentalBillingProfileId = transaction?.LinkedRentalBillingProfileId;
        Guid? rentalBillingRunId = transaction?.LinkedRentalBillingRunId;
        if ((!rentalBillingProfileId.HasValue || rentalBillingProfileId.Value == Guid.Empty) &&
            linkedPayment is not null)
        {
            var linkedInvoice = await _db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == linkedPayment.InvoiceId, ct);
            rentalBillingProfileId = linkedInvoice?.LinkedRentalBillingProfileId;
            rentalBillingRunId = linkedInvoice?.LinkedRentalBillingRunId;
        }

        if (linkedPayment is not null)
            _db.Payments.Remove(linkedPayment);

        _db.TransactionAttachments.RemoveRange(attachments);
        if (transaction is not null)
            _db.Transactions.Remove(transaction);
        await _db.SaveChangesAsync(ct);

        if (rentalBillingProfileId.HasValue && rentalBillingProfileId.Value != Guid.Empty)
            await RecalculateRentalSettlementAsync(rentalBillingProfileId.Value, rentalBillingRunId, ct);

        foreach (var attachment in attachments)
            TryDeleteAttachmentFile(attachment);

        return OfficeMutationResult.Ok(transactionId, "거래내역 서버 영구삭제를 로컬에 반영했습니다.");
    }

    public async Task<OfficeMutationResult> PermanentlyDeleteCustomerAsync(
        Guid customerId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanManageCustomerContracts(session))
            return OfficeMutationResult.Denied("현재 계정은 거래처를 영구삭제할 권한이 없습니다.");

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

        var hasRentalAssets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .AnyAsync(current => current.CustomerId == customerId, ct);
        if (hasRentalAssets)
            return OfficeMutationResult.Denied("연결된 렌탈 자산이 남아 있어 거래처를 영구삭제할 수 없습니다.");

        var hasCurrentAssignmentHistories = await _db.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .AnyAsync(current => current.CustomerId == customerId && !current.IsDeleted && current.IsCurrent, ct);
        if (hasCurrentAssignmentHistories)
            return OfficeMutationResult.Denied("현재 설치이력이 남아 있어 거래처를 영구삭제할 수 없습니다.");

        var contracts = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == customerId)
            .ToListAsync(ct);
        if (contracts.Any(current => !current.IsDeleted))
            return OfficeMutationResult.Denied("활성 계약서가 남아 있어 거래처를 영구삭제할 수 없습니다.");

        var now = DateTime.UtcNow;
        var assignmentHistories = await _db.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == customerId)
            .ToListAsync(ct);
        foreach (var history in assignmentHistories)
        {
            history.CustomerId = null;
            history.IsDirty = true;
            history.UpdatedAtUtc = now;
        }

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
        if (!CanManageCustomerContracts(session))
            return OfficeMutationResult.Denied("현재 계정은 거래처 계약서를 복원할 권한이 없습니다.");

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
        if (!CanManageCustomerContracts(session))
            return OfficeMutationResult.Denied("현재 계정은 거래처 계약서를 영구삭제할 권한이 없습니다.");

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
        if (!CanEditItems(session))
            return OfficeMutationResult.Denied("현재 계정은 품목을 복원할 권한이 없습니다.");

        var item = await _db.Items
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == itemId, ct);
        if (item is null)
            return OfficeMutationResult.Missing("복원할 품목을 찾을 수 없습니다.");
        if (!CanWriteItemScope(item, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 품목을 복원할 수 없습니다.");
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

        await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
        {
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            ForceOverride = false
        }, ct);

        RaiseInventoryStateChanged();
        return OfficeMutationResult.Ok(item.Id, "품목을 휴지통에서 복원했습니다.");
    }

    public async Task<OfficeMutationResult> PermanentlyDeleteItemAsync(
        Guid itemId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanEditItems(session))
            return OfficeMutationResult.Denied("현재 계정은 품목을 영구삭제할 권한이 없습니다.");

        var item = await _db.Items
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == itemId, ct);
        if (item is null)
            return OfficeMutationResult.Missing("영구삭제할 품목을 찾을 수 없습니다.");
        if (!CanWriteItemScope(item, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 품목을 영구삭제할 수 없습니다.");
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
        if (!CanWriteOfficeScope(session, target.ResponsibleOfficeCode, target.OfficeCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 전표를 복원할 수 없습니다.");

        var invoiceGroupRestore = await RestoreInvoiceGroupCoreAsync(target, session, ct);
        if (!invoiceGroupRestore.Success)
            return OfficeMutationResult.Denied(invoiceGroupRestore.Message);

        var linkedPaymentRestore = await RestoreDeletedPaymentsForRestoredInvoiceGroupAsync(target, session, ct);
        if (!linkedPaymentRestore.Success)
            return OfficeMutationResult.Denied(linkedPaymentRestore.Message);

        return OfficeMutationResult.Ok(
            target.Id,
            linkedPaymentRestore.RestoredOrRelinked
                ? "전표를 복원하고 연결된 수금/지급 기록과 거래내역도 함께 활성화했습니다."
                : invoiceGroupRestore.CustomerRestored
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
        if (!CanWriteOfficeScope(session, target.ResponsibleOfficeCode, target.OfficeCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 전표를 영구삭제할 수 없습니다.");

        var groupInvoices = await LoadInvoiceGroupForRecycleBinAsync(target, ct);
        var groupScopeFailure = EnsureCanWriteInvoiceGroupForRecycleBin(groupInvoices, session, "영구삭제");
        if (groupScopeFailure is not null)
            return groupScopeFailure;

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

        var deletedPayments = await _db.Payments
            .IgnoreQueryFilters()
            .Where(current => invoiceIds.Contains(current.InvoiceId) && current.IsDeleted)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var payment in deletedPayments)
        {
            AddPurgeAudit(nameof(LocalPayment), payment.Id, new
            {
                payment.InvoiceId,
                payment.PaymentDate,
                payment.Amount,
                Reason = "LinkedInvoicePurge"
            }, session, now);
        }

        _db.Payments.RemoveRange(deletedPayments);
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

        var linkedDeletedTransaction = await _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current =>
                current.Id == paymentId &&
                current.IsDeleted &&
                current.LinkedInvoiceId == payment.InvoiceId, ct);
        if (linkedDeletedTransaction is not null)
            return await RestoreTransactionAsync(paymentId, session, ct);

        var invoice = await _db.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == payment.InvoiceId, ct);
        if (invoice is null)
            return OfficeMutationResult.Missing("연결된 전표를 찾을 수 없습니다.");
        if (!CanWriteOfficeScope(session, invoice.ResponsibleOfficeCode, invoice.OfficeCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 수금/지급 기록을 복원할 수 없습니다.");

        var customerRestored = false;
        var invoiceRestored = false;
        if (invoice.IsDeleted)
        {
            var invoiceGroupRestore = await RestoreInvoiceGroupCoreAsync(invoice, session, ct);
            if (!invoiceGroupRestore.Success)
                return OfficeMutationResult.Denied(invoiceGroupRestore.Message);

            customerRestored = invoiceGroupRestore.CustomerRestored;
            invoiceRestored = true;
        }

        var linkedPaymentRestore = await RestoreDeletedPaymentsForRestoredInvoiceGroupAsync(invoice, session, ct, paymentId);
        if (!linkedPaymentRestore.Success)
            return OfficeMutationResult.Denied(linkedPaymentRestore.Message);

        return OfficeMutationResult.Ok(
            payment.Id,
            linkedPaymentRestore.RestoredOrRelinked || customerRestored || invoiceRestored
                ? "수금/지급 기록을 복원하고 연결 전표/거래내역도 함께 활성화했습니다."
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
        if (invoice is null || !CanWriteOfficeScope(session, invoice.ResponsibleOfficeCode, invoice.OfficeCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 수금/지급 기록을 영구삭제할 수 없습니다.");
        if (await _db.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == paymentId, ct))
            return OfficeMutationResult.Denied("연동 거래내역이 남아 있어 수금/지급 기록만 영구삭제할 수 없습니다. 거래내역 영구삭제를 실행하면 연동 수금/지급도 함께 제거됩니다.");

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
            if (linkedInvoice is null)
                return OfficeMutationResult.Missing("연결된 전표를 찾을 수 없습니다.");
            if (!CanWriteOfficeScope(session, linkedInvoice.ResponsibleOfficeCode, linkedInvoice.OfficeCode))
                return OfficeMutationResult.Denied("권한이 없어 연결 전표를 복원하거나 수금/지급과 동기화할 수 없습니다.");

            if (linkedInvoice is not null && linkedInvoice.IsDeleted)
            {
                var invoiceGroupRestore = await RestoreInvoiceGroupCoreAsync(linkedInvoice, session, ct);
                if (!invoiceGroupRestore.Success)
                    return OfficeMutationResult.Denied(invoiceGroupRestore.Message);

                customerRestored = customerRestored || invoiceGroupRestore.CustomerRestored;
                invoiceRestored = true;
                linkedInvoice = await _db.Invoices
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(current => current.Id == transaction.LinkedInvoiceId.Value, ct);
            }

            if (linkedInvoice is not null)
            {
                var linkedInvoiceCustomer = await _db.Customers
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(current => current.Id == linkedInvoice.CustomerId, ct);
                if (linkedInvoiceCustomer is null)
                    return OfficeMutationResult.Missing("연결 전표의 거래처를 찾을 수 없습니다.");
                if (!CanWriteOfficeScope(session, linkedInvoiceCustomer.ResponsibleOfficeCode, linkedInvoiceCustomer.OfficeCode))
                    return OfficeMutationResult.Denied("권한이 없어 연결 전표의 거래처를 복원할 수 없습니다.");
                if (linkedInvoiceCustomer.IsDeleted)
                {
                    RestoreCustomerCore(linkedInvoiceCustomer, session, now);
                    AddRestoreAudit(nameof(LocalCustomer), linkedInvoiceCustomer.Id, new
                    {
                        linkedInvoiceCustomer.NameOriginal,
                        Reason = "TransactionRestoreLinkedInvoice"
                    }, session, now);
                    customerRestored = true;
                }
                if (transaction.CustomerId != linkedInvoiceCustomer.Id)
                    transaction.CustomerId = linkedInvoiceCustomer.Id;
                customer = linkedInvoiceCustomer;

                if (linkedInvoice.LinkedRentalBillingProfileId is Guid invoiceRentalProfileId && invoiceRentalProfileId != Guid.Empty)
                {
                    transaction.LinkedRentalBillingProfileId = invoiceRentalProfileId;
                    transaction.LinkedRentalBillingRunId = linkedInvoice.LinkedRentalBillingRunId;
                    if (!PaymentFlowConstants.IsRentalSettlementKind(transaction.TransactionKind))
                        transaction.TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt;
                }
            }
        }

        RestoreEntity(transaction, now);
        AddRestoreAudit(nameof(LocalTransaction), transaction.Id, new
        {
            transaction.CustomerId,
            transaction.TransactionDate,
            transaction.TransactionKind
        }, session, now);
        await RestoreTransactionAttachmentsAsync(transaction.Id, now, ct);

        await _db.SaveChangesAsync(ct);

        if (linkedInvoice is not null && !linkedInvoice.IsDeleted)
            await SyncInvoicePaymentFromTransactionAsync(transaction, linkedInvoice, ct);

        if (transaction.LinkedRentalBillingProfileId.HasValue && transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
            await RecalculateRentalSettlementAsync(transaction.LinkedRentalBillingProfileId.Value, transaction.LinkedRentalBillingRunId, ct);

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
        if (!CanWriteOfficeScope(session, transaction.ResponsibleOfficeCode, transaction.OfficeCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래내역을 영구삭제할 수 없습니다.");

        var attachments = await _db.TransactionAttachments
            .IgnoreQueryFilters()
            .Where(current => current.TransactionId == transactionId)
            .ToListAsync(ct);

        var linkedPayment = await _db.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transactionId, ct);
        if (linkedPayment is not null)
        {
            if (!linkedPayment.IsDeleted)
                return OfficeMutationResult.Denied("활성 연동 수금/지급 기록이 남아 있어 거래내역을 영구삭제할 수 없습니다.");

            var linkedPaymentInvoice = await _db.Invoices
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == linkedPayment.InvoiceId, ct);
            if (linkedPaymentInvoice is null || !CanWriteOfficeScope(session, linkedPaymentInvoice.ResponsibleOfficeCode, linkedPaymentInvoice.OfficeCode))
                return OfficeMutationResult.Denied("권한이 없어 연동 수금/지급 기록을 영구삭제할 수 없습니다.");
        }

        var now = DateTime.UtcNow;
        if (linkedPayment is not null)
        {
            _db.Payments.Remove(linkedPayment);
            AddPurgeAudit(nameof(LocalPayment), linkedPayment.Id, new
            {
                linkedPayment.InvoiceId,
                linkedPayment.PaymentDate,
                linkedPayment.Amount,
                Reason = "LinkedTransactionPurge"
            }, session, now);
        }

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
            await RecalculateRentalSettlementAsync(transaction.LinkedRentalBillingProfileId.Value, transaction.LinkedRentalBillingRunId, ct);

        foreach (var attachment in attachments)
            TryDeleteAttachmentFile(attachment);

        return OfficeMutationResult.Ok(transaction.Id, "거래내역을 휴지통에서 영구삭제했습니다.");
    }

    private async Task<(bool Success, bool CustomerRestored, string Message)> RestoreInvoiceGroupCoreAsync(
        LocalInvoice target,
        SessionState session,
        CancellationToken ct)
    {
        var groupInvoices = await LoadInvoiceGroupForRecycleBinAsync(target, ct);
        var groupScopeFailure = EnsureCanWriteInvoiceGroupForRecycleBin(groupInvoices, session, "복원");
        if (groupScopeFailure is not null)
            return (false, false, groupScopeFailure.Message);

        var now = DateTime.UtcNow;
        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == target.CustomerId, ct);
        if (customer is null)
            return (false, false, "연결된 거래처를 찾을 수 없습니다.");

        var customerRestored = false;
        if (customer.IsDeleted)
        {
            if (!CanWriteOfficeScope(session, customer.ResponsibleOfficeCode, customer.OfficeCode))
                return (false, false, "현재 계정으로 연결된 거래처를 복원할 수 없습니다.");

            RestoreCustomerCore(customer, session, now);
            AddRestoreAudit(nameof(LocalCustomer), customer.Id, new
            {
                customer.NameOriginal,
                Reason = "InvoiceRestore"
            }, session, now);
            customerRestored = true;
        }

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

        return (true, customerRestored, string.Empty);
    }

    private async Task<(bool Success, bool RestoredOrRelinked, string Message)> RestoreDeletedPaymentsForRestoredInvoiceGroupAsync(
        LocalInvoice target,
        SessionState session,
        CancellationToken ct,
        Guid? onlyPaymentId = null)
    {
        var groupInvoices = await LoadInvoiceGroupForRecycleBinAsync(target, ct);
        var invoiceIds = groupInvoices.Select(current => current.Id).Distinct().ToList();
        if (invoiceIds.Count == 0)
            return (true, false, string.Empty);

        var invoiceMap = groupInvoices.ToDictionary(current => current.Id);
        var deletedPayments = await _db.Payments
            .IgnoreQueryFilters()
            .Where(current =>
                invoiceIds.Contains(current.InvoiceId) &&
                current.IsDeleted &&
                (!onlyPaymentId.HasValue || current.Id == onlyPaymentId.Value))
            .ToListAsync(ct);
        if (deletedPayments.Count == 0)
            return (true, false, string.Empty);

        var now = DateTime.UtcNow;
        var restoredOrRelinked = false;
        var rentalSettlementTargets = new List<(Guid ProfileId, Guid? RunId)>();
        foreach (var invoice in groupInvoices)
        {
            if (invoice.LinkedRentalBillingProfileId is Guid profileId && profileId != Guid.Empty)
                rentalSettlementTargets.Add((profileId, invoice.LinkedRentalBillingRunId));
        }

        foreach (var payment in deletedPayments)
        {
            if (!invoiceMap.TryGetValue(payment.InvoiceId, out var invoice))
                continue;
            if (!CanWriteOfficeScope(session, invoice.ResponsibleOfficeCode, invoice.OfficeCode))
                return (false, false, "권한이 없어 연결 수금/지급 기록을 복원할 수 없습니다.");

            var linkedTransaction = await _db.Transactions
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == payment.Id, ct);
            if (linkedTransaction is not null)
            {
                if (!CanWriteOfficeScope(session, linkedTransaction.ResponsibleOfficeCode, linkedTransaction.OfficeCode))
                    return (false, false, "권한이 없어 연결 거래내역을 복원하거나 전표와 재연결할 수 없습니다.");

                var transactionRelinked = false;
                if (linkedTransaction.LinkedInvoiceId != payment.InvoiceId)
                {
                    if (linkedTransaction.LinkedInvoiceId.HasValue && linkedTransaction.LinkedInvoiceId.Value != Guid.Empty)
                        return (false, false, "연동 거래내역의 전표 연결이 수금/지급 기록과 일치하지 않아 복원할 수 없습니다.");

                    linkedTransaction.LinkedInvoiceId = payment.InvoiceId;
                    linkedTransaction.LinkedInvoiceNumber = string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
                        ? invoice.LocalTempNumber
                        : invoice.InvoiceNumber;
                    transactionRelinked = true;
                }

                if (linkedTransaction.SettlementAmount != payment.Amount)
                {
                    linkedTransaction.SettlementAmount = payment.Amount;
                    transactionRelinked = true;
                }

                if (linkedTransaction.CustomerId != invoice.CustomerId)
                {
                    linkedTransaction.CustomerId = invoice.CustomerId;
                    transactionRelinked = true;
                }

                if (invoice.LinkedRentalBillingProfileId is Guid invoiceRentalProfileId && invoiceRentalProfileId != Guid.Empty)
                {
                    if (linkedTransaction.LinkedRentalBillingProfileId != invoiceRentalProfileId)
                    {
                        linkedTransaction.LinkedRentalBillingProfileId = invoiceRentalProfileId;
                        transactionRelinked = true;
                    }
                    if (linkedTransaction.LinkedRentalBillingRunId != invoice.LinkedRentalBillingRunId)
                    {
                        linkedTransaction.LinkedRentalBillingRunId = invoice.LinkedRentalBillingRunId;
                        transactionRelinked = true;
                    }
                    if (!PaymentFlowConstants.IsRentalSettlementKind(linkedTransaction.TransactionKind))
                    {
                        linkedTransaction.TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt;
                        transactionRelinked = true;
                    }
                }
                else
                {
                    var preferInvoicePayment = invoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement;
                    var normalizedKind = NormalizeLinkedInvoiceTransactionKind(linkedTransaction.TransactionKind, preferInvoicePayment);
                    if (!string.Equals(linkedTransaction.TransactionKind, normalizedKind, StringComparison.OrdinalIgnoreCase))
                    {
                        linkedTransaction.TransactionKind = normalizedKind;
                        transactionRelinked = true;
                    }
                }

                if (linkedTransaction.IsDeleted)
                {
                    RestoreEntity(linkedTransaction, now);
                    AddRestoreAudit(nameof(LocalTransaction), linkedTransaction.Id, new
                    {
                        linkedTransaction.CustomerId,
                        linkedTransaction.TransactionDate,
                        linkedTransaction.TransactionKind,
                        Reason = "InvoiceRestore"
                    }, session, now);
                    await RestoreTransactionAttachmentsAsync(linkedTransaction.Id, now, ct);
                    restoredOrRelinked = true;
                }
                else if (transactionRelinked)
                {
                    linkedTransaction.IsDirty = true;
                    linkedTransaction.UpdatedAtUtc = now;
                    restoredOrRelinked = true;
                }

                if (linkedTransaction.LinkedRentalBillingProfileId is Guid linkedRentalProfileId && linkedRentalProfileId != Guid.Empty)
                    rentalSettlementTargets.Add((linkedRentalProfileId, linkedTransaction.LinkedRentalBillingRunId));
            }

            RestoreEntity(payment, now);
            AddRestoreAudit(nameof(LocalPayment), payment.Id, new
            {
                payment.InvoiceId,
                payment.PaymentDate,
                payment.Amount,
                Reason = "InvoiceRestore"
            }, session, now);
            restoredOrRelinked = true;
        }

        await _db.SaveChangesAsync(ct);
        foreach (var targetKey in rentalSettlementTargets
                     .Where(current => current.ProfileId != Guid.Empty)
                     .Distinct())
        {
            await RecalculateRentalSettlementAsync(targetKey.ProfileId, targetKey.RunId, ct);
        }

        return (true, restoredOrRelinked, string.Empty);
    }

    private async Task RestoreTransactionAttachmentsAsync(
        Guid transactionId,
        DateTime now,
        CancellationToken ct)
    {
        var attachments = await _db.TransactionAttachments
            .IgnoreQueryFilters()
            .Where(current => current.TransactionId == transactionId && current.IsDeleted)
            .ToListAsync(ct);

        foreach (var attachment in attachments)
            RestoreEntity(attachment, now);
    }

    private Task<List<LocalInvoice>> LoadInvoiceGroupForRecycleBinAsync(LocalInvoice target, CancellationToken ct)
    {
        var versionGroupId = target.VersionGroupId == Guid.Empty ? target.Id : target.VersionGroupId;
        return _db.Invoices
            .IgnoreQueryFilters()
            .Where(current =>
                current.Id == target.Id ||
                current.Id == versionGroupId ||
                current.VersionGroupId == versionGroupId)
            .ToListAsync(ct);
    }

    private static OfficeMutationResult? EnsureCanWriteInvoiceGroupForRecycleBin(
        IEnumerable<LocalInvoice> invoiceGroup,
        SessionState session,
        string actionText)
    {
        foreach (var invoice in invoiceGroup)
        {
            if (!CanWriteOfficeScope(session, invoice.ResponsibleOfficeCode, invoice.OfficeCode))
                return OfficeMutationResult.Denied($"권한이 없어 전표 묶음의 모든 버전을 {actionText}할 수 없습니다.");
        }

        return null;
    }

    private static InvoiceSaveContext CreateServerPurgeInvoiceSaveContext() => new()
    {
        Username = "server-sync",
        Role = DomainConstants.RoleAdmin,
        OfficeCode = DomainConstants.OfficeUsenet,
        ForceOverride = true
    };

    private void RestoreCustomerCore(
        LocalCustomer customer,
        SessionState session,
        DateTime now)
    {
        RestoreEntity(customer, now);

        if (!session.HasAdministrativePrivileges &&
            !string.Equals(
                ResolveResponsibleOfficeScopeForAccess(customer.ResponsibleOfficeCode, customer.OfficeCode),
                NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet),
                StringComparison.OrdinalIgnoreCase))
        {
            _officeAccess.GrantTemporaryCustomerAccess(session, customer.Id);
        }
    }

    private async Task<OfficeMutationResult> RestoreRentalBillingProfileAsync(
        Guid profileId,
        SessionState session,
        CancellationToken ct)
    {
        var profile = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, ct);
        if (profile is null)
            return OfficeMutationResult.Missing("복원할 렌탈 청구프로필을 찾을 수 없습니다.");
        if (!profile.IsDeleted)
            return OfficeMutationResult.Ok(profile.Id, "이미 활성 상태인 렌탈 청구프로필입니다.");
        if (!CanWriteRentalRecycleBinScope(
                session,
                profile.TenantCode,
                profile.OfficeCode,
                profile.ResponsibleOfficeCode,
                profile.ManagementCompanyCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 렌탈 청구프로필을 복원할 수 없습니다.");

        var now = DateTime.UtcNow;
        if (profile.CustomerId.HasValue && profile.CustomerId.Value != Guid.Empty)
        {
            var customer = await _db.Customers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == profile.CustomerId.Value, ct);
            if (customer is not null && customer.IsDeleted && CanAccessCustomer(customer, session))
            {
                RestoreCustomerCore(customer, session, now);
                AddRestoreAudit(nameof(LocalCustomer), customer.Id, new
                {
                    customer.NameOriginal,
                    Reason = "RentalBillingProfileRestore"
                }, session, now);
            }
        }

        RestoreEntity(profile, now);
        AddRestoreAudit(nameof(LocalRentalBillingProfile), profile.Id, new
        {
            profile.CustomerName,
            profile.InstallSiteName,
            profile.MonthlyAmount
        }, session, now);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(profile.Id, "렌탈 청구프로필을 복원했습니다.");
    }

    private async Task<OfficeMutationResult> RestoreRentalAssetAsync(
        Guid assetId,
        SessionState session,
        CancellationToken ct)
    {
        var asset = await _db.RentalAssets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == assetId, ct);
        if (asset is null)
            return OfficeMutationResult.Missing("복원할 렌탈 자산을 찾을 수 없습니다.");
        if (!asset.IsDeleted)
            return OfficeMutationResult.Ok(asset.Id, "이미 활성 상태인 렌탈 자산입니다.");
        if (!CanWriteRentalRecycleBinScope(
                session,
                asset.TenantCode,
                asset.OfficeCode,
                asset.ResponsibleOfficeCode,
                asset.ManagementCompanyCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 렌탈 자산을 복원할 수 없습니다.");

        var activeConflict = await FindActiveRentalAssetRestoreConflictAsync(asset, ct);
        if (activeConflict is not null)
            return OfficeMutationResult.Denied(
                $"같은 렌탈 자산 식별값을 가진 활성 자산이 있어 복원할 수 없습니다. 활성 자산: {BuildRentalAssetConflictDisplay(activeConflict)}");

        var now = DateTime.UtcNow;
        if (asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
        {
            var customer = await _db.Customers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == asset.CustomerId.Value, ct);
            if (customer is not null && customer.IsDeleted && CanAccessCustomer(customer, session))
            {
                RestoreCustomerCore(customer, session, now);
                AddRestoreAudit(nameof(LocalCustomer), customer.Id, new
                {
                    customer.NameOriginal,
                    Reason = "RentalAssetRestore"
                }, session, now);
            }
        }

        RestoreEntity(asset, now);
        AddRestoreAudit(nameof(LocalRentalAsset), asset.Id, new
        {
            asset.ManagementNumber,
            asset.CustomerName,
            asset.ItemName,
            asset.InstallLocation,
            asset.MachineNumber
        }, session, now);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(asset.Id, "렌탈 자산을 복원했습니다.");
    }

    private async Task<LocalRentalAsset?> FindActiveRentalAssetRestoreConflictAsync(
        LocalRentalAsset target,
        CancellationToken ct)
    {
        var candidates = await _db.RentalAssets
            .IgnoreQueryFilters()
            .Where(current => current.Id != target.Id && !current.IsDeleted)
            .ToListAsync(ct);

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

    private static string BuildRentalAssetConflictDisplay(LocalRentalAsset asset)
        => JoinSegments(
            string.IsNullOrWhiteSpace(asset.ManagementNumber) ? null : $"관리번호 {asset.ManagementNumber}",
            string.IsNullOrWhiteSpace(asset.ManagementId) ? null : $"관리ID {asset.ManagementId}",
            string.IsNullOrWhiteSpace(asset.AssetKey) ? null : $"자산키 {asset.AssetKey}",
            string.IsNullOrWhiteSpace(asset.ItemName) ? null : asset.ItemName);

    private async Task<OfficeMutationResult> RestoreRentalBillingLogAsync(
        Guid logId,
        SessionState session,
        CancellationToken ct)
    {
        var log = await _db.RentalBillingLogs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == logId, ct);
        if (log is null)
            return OfficeMutationResult.Missing("복원할 렌탈 청구로그를 찾을 수 없습니다.");
        if (!log.IsDeleted)
            return OfficeMutationResult.Ok(log.Id, "이미 활성 상태인 렌탈 청구로그입니다.");
        if (!CanWriteRentalRecycleBinScope(
                session,
                log.TenantCode,
                log.OfficeCode,
                log.ResponsibleOfficeCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 렌탈 청구로그를 복원할 수 없습니다.");

        var profile = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == log.BillingProfileId, ct);
        if (profile is not null &&
            profile.IsDeleted &&
            CanWriteRentalRecycleBinScope(
                session,
                profile.TenantCode,
                profile.OfficeCode,
                profile.ResponsibleOfficeCode,
                profile.ManagementCompanyCode))
        {
            RestoreEntity(profile, DateTime.UtcNow);
        }

        var now = DateTime.UtcNow;
        RestoreEntity(log, now);
        AddRestoreAudit(nameof(LocalRentalBillingLog), log.Id, new
        {
            log.BillingProfileId,
            log.BillingYearMonth,
            log.ScheduledDate,
            log.BilledAmount
        }, session, now);
        await _db.SaveChangesAsync(ct);
        if (profile is not null)
            await RecalculateRentalSettlementAsync(profile.Id, ct);
        return OfficeMutationResult.Ok(log.Id, "렌탈 청구로그를 복원했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedRentalBillingProfileAsync(
        Guid profileId,
        CancellationToken ct)
    {
        var profile = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, ct);

        var linkedAssets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .Where(current => current.BillingProfileId == profileId)
            .ToListAsync(ct);
        var logs = await _db.RentalBillingLogs
            .IgnoreQueryFilters()
            .Where(current => current.BillingProfileId == profileId)
            .ToListAsync(ct);
        var assignmentHistories = await _db.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .Where(current => current.BillingProfileId == profileId)
            .ToListAsync(ct);
        if (profile is null && linkedAssets.Count == 0 && logs.Count == 0 && assignmentHistories.Count == 0)
            return OfficeMutationResult.Ok(profileId, "렌탈 청구프로필 서버 영구삭제 상태가 이미 반영되어 있습니다.");

        foreach (var asset in linkedAssets)
        {
            asset.BillingProfileId = null;
            asset.BillingEligibilityStatus = GetBillingEligibilityStatusAfterProfilePurge(asset.AssetStatus);
            if (!string.Equals(asset.BillingEligibilityStatus, "청구제외", StringComparison.OrdinalIgnoreCase))
                asset.BillingExclusionReason = string.Empty;
            asset.IsDirty = false;
            asset.UpdatedAtUtc = DateTime.UtcNow;
        }

        var now = DateTime.UtcNow;
        foreach (var history in assignmentHistories)
        {
            history.BillingProfileId = null;
            history.IsDirty = false;
            history.UpdatedAtUtc = now;
        }

        _db.RentalBillingLogs.RemoveRange(logs);
        if (profile is not null)
            _db.RentalBillingProfiles.Remove(profile);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(profileId, "렌탈 청구프로필 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedRentalAssetAsync(
        Guid assetId,
        CancellationToken ct)
    {
        var asset = await _db.RentalAssets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == assetId, ct);

        var profiles = await GetBillingProfilesContainingAssetIdAsync(assetId, ct);
        var assignmentHistories = await _db.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .Where(current => current.AssetId == assetId)
            .ToListAsync(ct);
        if (asset is null && profiles.Count == 0 && assignmentHistories.Count == 0)
            return OfficeMutationResult.Ok(assetId, "렌탈 자산 서버 영구삭제 상태가 이미 반영되어 있습니다.");

        foreach (var profile in profiles)
        {
            var normalizedJson = RemoveIncludedAssetId(profile.BillingTemplateJson, assetId);
            if (string.Equals(normalizedJson, profile.BillingTemplateJson, StringComparison.Ordinal))
                continue;

            profile.BillingTemplateJson = normalizedJson;
            profile.IsDirty = false;
            profile.UpdatedAtUtc = DateTime.UtcNow;
        }

        _db.RentalAssetAssignmentHistories.RemoveRange(assignmentHistories);
        if (asset is not null)
            _db.RentalAssets.Remove(asset);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(assetId, "렌탈 자산 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> ApplyServerPurgedRentalBillingLogAsync(
        Guid logId,
        CancellationToken ct)
    {
        var log = await _db.RentalBillingLogs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == logId, ct);
        if (log is null)
            return OfficeMutationResult.Ok(logId, "렌탈 청구로그 서버 영구삭제 상태가 이미 반영되어 있습니다.");

        var billingProfileId = log.BillingProfileId;
        _db.RentalBillingLogs.Remove(log);
        await _db.SaveChangesAsync(ct);
        if (billingProfileId != Guid.Empty)
            await RecalculateRentalSettlementAsync(billingProfileId, ct);
        return OfficeMutationResult.Ok(logId, "렌탈 청구로그 서버 영구삭제를 로컬에 반영했습니다.");
    }

    private async Task<OfficeMutationResult> PermanentlyDeleteRentalBillingProfileAsync(
        Guid profileId,
        SessionState session,
        CancellationToken ct)
    {
        var profile = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, ct);
        if (profile is null)
            return OfficeMutationResult.Missing("영구삭제할 렌탈 청구프로필을 찾을 수 없습니다.");
        if (!profile.IsDeleted)
            return OfficeMutationResult.Denied("활성 상태의 렌탈 청구프로필은 휴지통에서 영구삭제할 수 없습니다.");
        if (!CanWriteRentalRecycleBinScope(
                session,
                profile.TenantCode,
                profile.OfficeCode,
                profile.ResponsibleOfficeCode,
                profile.ManagementCompanyCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 렌탈 청구프로필을 영구삭제할 수 없습니다.");

        var hasInvoices = await _db.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(current => current.LinkedRentalBillingProfileId == profileId, ct);
        if (hasInvoices)
            return OfficeMutationResult.Denied("연결된 전표가 남아 있어 렌탈 청구프로필을 영구삭제할 수 없습니다.");

        var hasTransactions = await _db.Transactions
            .IgnoreQueryFilters()
            .AnyAsync(current => current.LinkedRentalBillingProfileId == profileId, ct);
        if (hasTransactions)
            return OfficeMutationResult.Denied("연결된 수금/거래내역이 남아 있어 렌탈 청구프로필을 영구삭제할 수 없습니다.");

        var now = DateTime.UtcNow;
        var linkedAssets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .Where(current => current.BillingProfileId == profileId)
            .ToListAsync(ct);
        foreach (var asset in linkedAssets)
        {
            asset.BillingProfileId = null;
            asset.BillingEligibilityStatus = GetBillingEligibilityStatusAfterProfilePurge(asset.AssetStatus);
            if (!string.Equals(asset.BillingEligibilityStatus, "청구제외", StringComparison.OrdinalIgnoreCase))
                asset.BillingExclusionReason = string.Empty;
            asset.IsDirty = true;
            asset.UpdatedAtUtc = now;
        }

        var logs = await _db.RentalBillingLogs
            .IgnoreQueryFilters()
            .Where(current => current.BillingProfileId == profileId)
            .ToListAsync(ct);
        var assignmentHistories = await _db.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .Where(current => current.BillingProfileId == profileId)
            .ToListAsync(ct);
        foreach (var history in assignmentHistories)
        {
            history.BillingProfileId = null;
            history.IsDirty = true;
            history.UpdatedAtUtc = now;
        }

        _db.RentalBillingLogs.RemoveRange(logs);
        _db.RentalBillingProfiles.Remove(profile);
        AddPurgeAudit(nameof(LocalRentalBillingProfile), profile.Id, new
        {
            profile.CustomerName,
            profile.InstallSiteName,
            RemovedLogCount = logs.Count,
            ClearedAssignmentHistoryCount = assignmentHistories.Count,
            UnlinkedAssetCount = linkedAssets.Count
        }, session, now);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(profile.Id, "렌탈 청구프로필을 영구삭제했습니다.");
    }

    private async Task<OfficeMutationResult> PermanentlyDeleteRentalAssetAsync(
        Guid assetId,
        SessionState session,
        CancellationToken ct)
    {
        var asset = await _db.RentalAssets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == assetId, ct);
        if (asset is null)
            return OfficeMutationResult.Missing("영구삭제할 렌탈 자산을 찾을 수 없습니다.");
        if (!asset.IsDeleted)
            return OfficeMutationResult.Denied("활성 상태의 렌탈 자산은 휴지통에서 영구삭제할 수 없습니다.");
        if (!CanWriteRentalRecycleBinScope(
                session,
                asset.TenantCode,
                asset.OfficeCode,
                asset.ResponsibleOfficeCode,
                asset.ManagementCompanyCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 렌탈 자산을 영구삭제할 수 없습니다.");

        var now = DateTime.UtcNow;
        var profiles = await GetBillingProfilesContainingAssetIdAsync(assetId, ct);
        foreach (var profile in profiles)
        {
            var normalizedJson = RemoveIncludedAssetId(profile.BillingTemplateJson, assetId);
            if (string.Equals(normalizedJson, profile.BillingTemplateJson, StringComparison.Ordinal))
                continue;

            profile.BillingTemplateJson = normalizedJson;
            profile.IsDirty = true;
            profile.UpdatedAtUtc = now;
        }

        var assignmentHistories = await _db.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .Where(current => current.AssetId == assetId)
            .ToListAsync(ct);

        _db.RentalAssetAssignmentHistories.RemoveRange(assignmentHistories);
        _db.RentalAssets.Remove(asset);
        AddPurgeAudit(nameof(LocalRentalAsset), asset.Id, new
        {
            asset.ManagementNumber,
            asset.CustomerName,
            asset.ItemName,
            asset.InstallLocation,
            RemovedAssignmentHistoryCount = assignmentHistories.Count,
            UpdatedProfileCount = profiles.Count
        }, session, now);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(asset.Id, "렌탈 자산을 영구삭제했습니다.");
    }

    private async Task<OfficeMutationResult> PermanentlyDeleteRentalBillingLogAsync(
        Guid logId,
        SessionState session,
        CancellationToken ct)
    {
        var log = await _db.RentalBillingLogs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == logId, ct);
        if (log is null)
            return OfficeMutationResult.Missing("영구삭제할 렌탈 청구로그를 찾을 수 없습니다.");
        if (!log.IsDeleted)
            return OfficeMutationResult.Denied("활성 상태의 렌탈 청구로그는 휴지통에서 영구삭제할 수 없습니다.");
        if (!CanWriteRentalRecycleBinScope(
                session,
                log.TenantCode,
                log.OfficeCode,
                log.ResponsibleOfficeCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 렌탈 청구로그를 영구삭제할 수 없습니다.");

        var billingProfileId = log.BillingProfileId;
        var now = DateTime.UtcNow;
        _db.RentalBillingLogs.Remove(log);
        AddPurgeAudit(nameof(LocalRentalBillingLog), log.Id, new
        {
            log.BillingProfileId,
            log.BillingYearMonth,
            log.ScheduledDate,
            log.BilledAmount
        }, session, now);
        await _db.SaveChangesAsync(ct);
        if (billingProfileId != Guid.Empty)
            await RecalculateRentalSettlementAsync(billingProfileId, ct);
        return OfficeMutationResult.Ok(log.Id, "렌탈 청구로그를 영구삭제했습니다.");
    }

    private async Task<OfficeMutationResult> RestoreInventoryTransferAsync(
        Guid transferId,
        SessionState session,
        CancellationToken ct)
    {
        var transfer = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(current => current.Lines)
            .FirstOrDefaultAsync(current => current.Id == transferId, ct);
        if (transfer is null)
            return OfficeMutationResult.Missing("복원할 재고이동을 찾을 수 없습니다.");
        if (!transfer.IsDeleted)
            return OfficeMutationResult.Ok(transfer.Id, "이미 활성 상태인 재고이동입니다.");
        if (!CanAccessInventoryTransferForRecycleBin(transfer, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 재고이동을 복원할 수 없습니다.");

        var now = DateTime.UtcNow;
        var activeLines = transfer.Lines
            .Where(line => !line.IsDeleted)
            .ToList();
        var itemTrackingMap = await BuildItemTrackingMapAsync(ct);
        var normalizedTransferStatus = InventoryTransferStatusNormalizer.Normalize(
            transfer.TransferStatus,
            transfer.ReceivedByUsername,
            transfer.ReceivedAtUtc,
            transfer.RejectedByUsername,
            transfer.RejectedAtUtc);
        var shortages = await FindTransferStockShortagesAsync(
            null,
            transfer.FromWarehouseCode,
            transfer.ToWarehouseCode,
            normalizedTransferStatus,
            activeLines,
            itemTrackingMap,
            ct);
        if (shortages.Count > 0)
            return OfficeMutationResult.Denied(
                FormatStockShortageMessage("재고가 부족하여 재고이동을 복원할 수 없습니다.", shortages));

        RestoreEntity(transfer, now);
        AddRestoreAudit(nameof(LocalInventoryTransfer), transfer.Id, new
        {
            transfer.TransferNumber,
            transfer.TransferDate,
            transfer.TransferStatus,
            LineCount = transfer.Lines.Count(line => !line.IsDeleted)
        }, session, now);

        await _db.SaveChangesAsync(ct);
        await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
        {
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            ForceOverride = false
        }, ct);
        return OfficeMutationResult.Ok(transfer.Id, "재고이동을 휴지통에서 복원했습니다.");
    }

    private async Task<OfficeMutationResult> RestoreRentalManagementCompanyAsync(
        Guid companyId,
        SessionState session,
        CancellationToken ct)
    {
        var company = await _db.RentalManagementCompanies
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == companyId, ct);
        if (company is null)
            return OfficeMutationResult.Missing("복원할 렌탈 관리업체를 찾을 수 없습니다.");
        if (!CanManageRentalSettingsRecycleBinScope(session, company.Code))
            return OfficeMutationResult.Denied("권한이 없어 해당 렌탈 관리업체를 복원할 수 없습니다.");
        if (!company.IsDeleted)
            return OfficeMutationResult.Ok(company.Id, "이미 활성 상태인 렌탈 관리업체입니다.");

        var now = DateTime.UtcNow;
        RestoreEntity(company, now);
        company.IsActive = true;
        AddRestoreAudit(nameof(LocalRentalManagementCompany), company.Id, new
        {
            company.Code,
            company.Name
        }, session, now);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(company.Id, "렌탈 관리업체를 휴지통에서 복원했습니다.");
    }

    private async Task<OfficeMutationResult> PermanentlyDeleteCompanyProfileAsync(
        Guid profileId,
        SessionState session,
        CancellationToken ct)
    {
        if (!CanManageSharedRecycleBin(session))
            return OfficeMutationResult.Denied("권한이 없어 해당 회사설정을 영구삭제할 수 없습니다.");

        return await NormalizeLocalMutationResultAsync(async () =>
        {
            var profile = await _db.CompanyProfiles
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == profileId, ct);
            if (profile is null)
                return LocalMutationResult.Missing("영구삭제할 회사설정을 찾을 수 없습니다.");
            if (!profile.IsDeleted)
                return LocalMutationResult.Denied("활성 상태 회사설정은 휴지통에서 영구삭제할 수 없습니다.");

            var assignedCount = await _db.Settings
                .AsNoTracking()
                .CountAsync(setting => setting.Key.StartsWith(CompanyProfileAssignmentPrefix) && setting.Value == profileId.ToString("D"), ct);
            if (assignedCount > 0)
                return LocalMutationResult.Denied("사용자별 회사설정 연결이 남아 있어 영구삭제할 수 없습니다.");

            var now = DateTime.UtcNow;
            _db.CompanyProfiles.Remove(profile);
            AddPurgeAudit(nameof(LocalCompanyProfile), profile.Id, new
            {
                profile.ProfileName,
                profile.OfficeCode,
                profile.TradeName
            }, session, now);
            await _db.SaveChangesAsync(ct);
            return LocalMutationResult.Ok(profile.Id, "회사설정을 영구삭제했습니다.");
        });
    }

    private async Task<OfficeMutationResult> PermanentlyDeleteCustomerCategoryAsync(
        Guid categoryId,
        SessionState session,
        CancellationToken ct)
    {
        if (!CanManageSharedRecycleBin(session))
            return OfficeMutationResult.Denied("권한이 없어 해당 고객분류를 영구삭제할 수 없습니다.");

        return await NormalizeLocalMutationResultAsync(async () =>
        {
            var category = await _db.CustomerCategories
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == categoryId, ct);
            if (category is null)
                return LocalMutationResult.Missing("영구삭제할 고객분류를 찾을 수 없습니다.");
            if (!category.IsDeleted)
                return LocalMutationResult.Denied("활성 상태 고객분류는 휴지통에서 영구삭제할 수 없습니다.");

            var inUse = await _db.Customers
                .IgnoreQueryFilters()
                .AnyAsync(customer => customer.CategoryId == categoryId, ct);
            if (!inUse)
            {
                inUse = await _db.CustomerMasters
                    .IgnoreQueryFilters()
                    .AnyAsync(customer => customer.CategoryId == categoryId, ct);
            }

            if (inUse)
                return LocalMutationResult.Denied("연결된 거래처가 남아 있어 고객분류를 영구삭제할 수 없습니다.");

            var now = DateTime.UtcNow;
            _db.CustomerCategories.Remove(category);
            AddPurgeAudit(nameof(LocalCustomerCategory), category.Id, new
            {
                category.Name,
                category.IsSystemDefault
            }, session, now);
            await _db.SaveChangesAsync(ct);
            return LocalMutationResult.Ok(category.Id, "고객분류를 영구삭제했습니다.");
        });
    }

    private async Task<OfficeMutationResult> PermanentlyDeletePriceGradeOptionAsync(
        Guid optionId,
        SessionState session,
        CancellationToken ct)
    {
        if (!CanManageSharedRecycleBin(session))
            return OfficeMutationResult.Denied("권한이 없어 해당 가격등급을 영구삭제할 수 없습니다.");

        return await NormalizeLocalMutationResultAsync(async () =>
        {
            var option = await _db.PriceGradeOptions
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == optionId, ct);
            if (option is null)
                return LocalMutationResult.Missing("영구삭제할 가격등급을 찾을 수 없습니다.");
            if (!option.IsDeleted)
                return LocalMutationResult.Denied("활성 상태 가격등급은 휴지통에서 영구삭제할 수 없습니다.");

            var inUse = await _db.Customers
                .IgnoreQueryFilters()
                .AnyAsync(customer => customer.PriceGrade == option.Name, ct);
            if (inUse)
                return LocalMutationResult.Denied("연결된 거래처가 남아 있어 가격등급을 영구삭제할 수 없습니다.");

            var now = DateTime.UtcNow;
            _db.PriceGradeOptions.Remove(option);
            AddPurgeAudit(nameof(LocalPriceGradeOption), option.Id, new
            {
                option.Name,
                option.PriceSource,
                option.SortOrder
            }, session, now);
            await _db.SaveChangesAsync(ct);
            return LocalMutationResult.Ok(option.Id, "가격등급을 영구삭제했습니다.");
        });
    }

    private async Task<OfficeMutationResult> PermanentlyDeleteTradeTypeOptionAsync(
        Guid optionId,
        SessionState session,
        CancellationToken ct)
    {
        if (!CanManageSharedRecycleBin(session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래구분을 영구삭제할 수 없습니다.");

        return await NormalizeLocalMutationResultAsync(async () =>
        {
            var option = await _db.TradeTypeOptions
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == optionId, ct);
            if (option is null)
                return LocalMutationResult.Missing("영구삭제할 거래구분을 찾을 수 없습니다.");
            if (!option.IsDeleted)
                return LocalMutationResult.Denied("활성 상태 거래구분은 휴지통에서 영구삭제할 수 없습니다.");

            var inUse = await _db.Customers
                .IgnoreQueryFilters()
                .AnyAsync(customer => customer.TradeType == option.Name, ct);
            if (inUse)
                return LocalMutationResult.Denied("연결된 거래처가 남아 있어 거래구분을 영구삭제할 수 없습니다.");

            var now = DateTime.UtcNow;
            _db.TradeTypeOptions.Remove(option);
            AddPurgeAudit(nameof(LocalTradeTypeOption), option.Id, new
            {
                option.Name,
                option.AllowsSales,
                option.AllowsPurchase
            }, session, now);
            await _db.SaveChangesAsync(ct);
            return LocalMutationResult.Ok(option.Id, "거래구분을 영구삭제했습니다.");
        });
    }

    private async Task<OfficeMutationResult> PermanentlyDeleteItemCategoryOptionAsync(
        Guid optionId,
        SessionState session,
        CancellationToken ct)
    {
        if (!CanManageSharedRecycleBin(session))
            return OfficeMutationResult.Denied("권한이 없어 해당 품목분류를 영구삭제할 수 없습니다.");

        return await NormalizeLocalMutationResultAsync(async () =>
        {
            var option = await _db.ItemCategoryOptions
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == optionId, ct);
            if (option is null)
                return LocalMutationResult.Missing("영구삭제할 품목분류를 찾을 수 없습니다.");
            if (!option.IsDeleted)
                return LocalMutationResult.Denied("활성 상태 품목분류는 휴지통에서 영구삭제할 수 없습니다.");

            var optionKey = RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name);
            var itemInUse = (await _db.Items.IgnoreQueryFilters().ToListAsync(ct))
                .Any(item => string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(item.CategoryName), optionKey, StringComparison.OrdinalIgnoreCase));
            var rentalInUse = (await _db.RentalAssets.IgnoreQueryFilters().ToListAsync(ct))
                .Any(asset => string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.ItemCategoryName), optionKey, StringComparison.OrdinalIgnoreCase));
            if (itemInUse || rentalInUse)
                return LocalMutationResult.Denied("연결된 품목 또는 렌탈 자산이 남아 있어 품목분류를 영구삭제할 수 없습니다.");

            var now = DateTime.UtcNow;
            _db.ItemCategoryOptions.Remove(option);
            AddPurgeAudit(nameof(LocalItemCategoryOption), option.Id, new
            {
                option.Name,
                option.SortOrder
            }, session, now);
            await _db.SaveChangesAsync(ct);
            RaiseInventoryStateChanged();
            return LocalMutationResult.Ok(option.Id, "품목분류를 영구삭제했습니다.");
        });
    }

    private async Task<OfficeMutationResult> PermanentlyDeleteInventoryTransferAsync(
        Guid transferId,
        SessionState session,
        CancellationToken ct)
    {
        var transfer = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(current => current.Lines)
            .FirstOrDefaultAsync(current => current.Id == transferId, ct);
        if (transfer is null)
            return OfficeMutationResult.Missing("영구삭제할 재고이동을 찾을 수 없습니다.");
        if (!transfer.IsDeleted)
            return OfficeMutationResult.Denied("활성 상태 재고이동은 휴지통에서 영구삭제할 수 없습니다.");
        if (!CanAccessInventoryTransferForRecycleBin(transfer, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 재고이동을 영구삭제할 수 없습니다.");

        var now = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(transfer.ReceiveEvidencePath))
            TryDeleteLocalFile(transfer.ReceiveEvidencePath);

        _db.InventoryTransferLines.RemoveRange(transfer.Lines);
        _db.InventoryTransfers.Remove(transfer);
        AddPurgeAudit(nameof(LocalInventoryTransfer), transfer.Id, new
        {
            transfer.TransferNumber,
            transfer.TransferDate,
            transfer.TransferStatus,
            LineCount = transfer.Lines.Count(line => !line.IsDeleted)
        }, session, now);
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(transfer.Id, "재고이동을 영구삭제했습니다.");
    }

    private async Task<OfficeMutationResult> PermanentlyDeleteRentalManagementCompanyAsync(
        Guid companyId,
        SessionState session,
        CancellationToken ct)
    {
        return await NormalizeLocalMutationResultAsync(async () =>
        {
            var company = await _db.RentalManagementCompanies
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == companyId, ct);
            if (company is null)
                return LocalMutationResult.Missing("영구삭제할 렌탈 관리업체를 찾을 수 없습니다.");
            if (!CanManageRentalSettingsRecycleBinScope(session, company.Code))
                return LocalMutationResult.Denied("권한이 없어 해당 렌탈 관리업체를 영구삭제할 수 없습니다.");
            if (!company.IsDeleted)
                return LocalMutationResult.Denied("활성 상태 렌탈 관리업체는 휴지통에서 영구삭제할 수 없습니다.");

            var billingProfileCount = await _db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .CountAsync(current => current.ManagementCompanyCode == company.Code, ct);
            var rentalAssetCount = await _db.RentalAssets
                .IgnoreQueryFilters()
                .CountAsync(current => current.ManagementCompanyCode == company.Code, ct);
            if (billingProfileCount > 0 || rentalAssetCount > 0)
                return LocalMutationResult.Denied("연결된 렌탈 청구프로필 또는 렌탈 자산이 남아 있어 렌탈 관리업체를 영구삭제할 수 없습니다.");

            var now = DateTime.UtcNow;
            _db.RentalManagementCompanies.Remove(company);
            AddPurgeAudit(nameof(LocalRentalManagementCompany), company.Id, new
            {
                company.Code,
                company.Name
            }, session, now);
            await _db.SaveChangesAsync(ct);
            return LocalMutationResult.Ok(company.Id, "렌탈 관리업체를 영구삭제했습니다.");
        });
    }

    private static bool CanManageSharedRecycleBin(SessionState? session)
        => session is not null && session.IsLoggedIn && (session.HasAdministrativePrivileges || session.HasGlobalDataScope);

    private static bool CanManageRentalSettingsRecycleBinScope(SessionState? session, string? managementCompanyCode)
        => CanManageRentalSettingsForRecycleBin(session) &&
           RecycleBinEntityBelongsToCurrentBusinessDatabase(
               session,
               tenantCode: null,
               officeCode: managementCompanyCode,
               responsibleOfficeCode: managementCompanyCode,
               managementCompanyCode: managementCompanyCode);

    private static bool CanWriteRentalRecycleBinScope(
        SessionState? session,
        string? tenantCode,
        string? officeCode,
        string? responsibleOfficeCode,
        string? managementCompanyCode = null)
    {
        if (!RecycleBinEntityBelongsToCurrentBusinessDatabase(
                session,
                tenantCode,
                officeCode,
                responsibleOfficeCode,
                managementCompanyCode))
        {
            return false;
        }

        return CanWriteRentalScope(session, responsibleOfficeCode, managementCompanyCode);
    }

    private static bool RecycleBinEntityBelongsToCurrentBusinessDatabase(
        SessionState? session,
        string? tenantCode,
        string? officeCode,
        string? responsibleOfficeCode = null,
        string? managementCompanyCode = null)
    {
        if (session is null || !session.IsLoggedIn)
            return false;

        var currentDatabaseName = ResolveSessionRecycleBinBusinessDatabaseName(session);
        var entityDatabaseName = ResolveRecycleBinBusinessDatabaseName(
            tenantCode,
            officeCode,
            responsibleOfficeCode,
            managementCompanyCode);
        return string.Equals(currentDatabaseName, entityDatabaseName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSessionRecycleBinBusinessDatabaseName(SessionState session)
    {
        if (!string.IsNullOrWhiteSpace(session.SelectedBusinessDatabaseName))
            return TenantScopeCatalog.GetDatabaseName(session.SelectedBusinessDatabaseName);

        return ResolveRecycleBinBusinessDatabaseName(session.TenantCode, session.OfficeCode);
    }

    private static string ResolveRecycleBinBusinessDatabaseName(
        string? tenantCode,
        string? officeCode,
        string? responsibleOfficeCode = null,
        string? managementCompanyCode = null)
    {
        var fallbackOfficeCode = !string.IsNullOrWhiteSpace(responsibleOfficeCode)
            ? responsibleOfficeCode
            : managementCompanyCode;
        return TenantScopeCatalog.GetDatabaseName(
            TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                tenantCode,
                officeCode,
                fallbackOfficeCode: fallbackOfficeCode));
    }

    private static OfficeMutationResult NormalizeLocalMutationResult(LocalMutationResult result)
    {
        if (result.Success)
            return OfficeMutationResult.Ok(result.EntityId, result.Message);
        if (result.PermissionDenied)
            return OfficeMutationResult.Denied(result.Message);
        if (result.NotFound)
            return OfficeMutationResult.Missing(result.Message);

        return OfficeMutationResult.Denied(string.IsNullOrWhiteSpace(result.Message)
            ? "작업을 완료할 수 없습니다."
            : result.Message);
    }

    private static async Task<OfficeMutationResult> NormalizeLocalMutationResultAsync(Func<Task<LocalMutationResult>> action)
        => NormalizeLocalMutationResult(await action());

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

    private static string RemoveItemId(string? templateJson, Guid itemId)
    {
        if (itemId == Guid.Empty)
            return templateJson ?? "[]";

        try
        {
            var items = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(templateJson ?? "[]") ?? new List<RentalBillingTemplateItemModel>();
            foreach (var item in items)
            {
                if (item.ItemId == itemId)
                    item.ItemId = Guid.Empty;
            }

            return JsonSerializer.Serialize(items);
        }
        catch
        {
            return templateJson ?? "[]";
        }
    }

    private async Task<List<LocalRentalBillingProfile>> GetBillingProfilesContainingAssetIdAsync(
        Guid assetId,
        CancellationToken ct)
    {
        if (assetId == Guid.Empty)
            return [];

        var profiles = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        return profiles
            .Where(profile => BillingTemplateContainsAssetId(profile.BillingTemplateJson, assetId))
            .ToList();
    }

    private async Task<List<LocalRentalBillingProfile>> GetBillingProfilesContainingItemIdAsync(
        Guid itemId,
        CancellationToken ct)
    {
        if (itemId == Guid.Empty)
            return [];

        var profiles = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        return profiles
            .Where(profile => BillingTemplateContainsItemId(profile.BillingTemplateJson, itemId))
            .ToList();
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

    private static bool BillingTemplateContainsItemId(string? templateJson, Guid itemId)
    {
        if (itemId == Guid.Empty || string.IsNullOrWhiteSpace(templateJson))
            return false;

        try
        {
            var items = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(templateJson) ?? [];
            return items.Any(item => item.ItemId == itemId);
        }
        catch
        {
            return false;
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

    private static void TryDeleteLocalFile(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 파일 정리 실패는 영구삭제 자체를 막지 않는다.
        }
    }

    private static string JoinSegments(params string?[] segments)
        => string.Join(" / ", segments.Where(segment => !string.IsNullOrWhiteSpace(segment)).Select(segment => segment!.Trim()));

    private static string GetBillingEligibilityStatusAfterProfilePurge(string? assetStatus)
    {
        if (RentalAssetStatusRules.IsNonOperating(assetStatus))
            return "청구제외";

        return "미확인";
    }
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
        var trimmed = (transactionKind ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "거래내역";

        return trimmed switch
        {
            var kind when string.Equals(kind, PaymentFlowConstants.TransactionKindReceipt, StringComparison.OrdinalIgnoreCase) => "일반수금",
            var kind when string.Equals(kind, PaymentFlowConstants.TransactionKindPayment, StringComparison.OrdinalIgnoreCase) => "일반지급",
            var kind when string.Equals(kind, PaymentFlowConstants.TransactionKindAdvanceDeposit, StringComparison.OrdinalIgnoreCase) => "선수금입금",
            var kind when string.Equals(kind, PaymentFlowConstants.TransactionKindAdvanceRefund, StringComparison.OrdinalIgnoreCase) => "선수금환불",
            var kind when string.Equals(kind, PaymentFlowConstants.TransactionKindAdvanceApply, StringComparison.OrdinalIgnoreCase) => "선수금차감",
            var kind when string.Equals(kind, PaymentFlowConstants.TransactionKindInvoiceReceipt, StringComparison.OrdinalIgnoreCase) => "전표수금",
            var kind when string.Equals(kind, PaymentFlowConstants.TransactionKindInvoicePayment, StringComparison.OrdinalIgnoreCase) => "전표지급",
            var kind when string.Equals(kind, PaymentFlowConstants.TransactionKindRentalReceipt, StringComparison.OrdinalIgnoreCase) => "렌탈수금",
            _ => trimmed
        };
    }
}
