using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    public const int AuditLogLookupLimit = 1000;
    public const int AuditLogLookupScanLimit = 10000;

    private const int AuditLogLookupBatchSize = 250;

    private static readonly JsonSerializerOptions AuditLogLookupJsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly Regex SensitiveJsonFallbackRegex = new(
        @"(?<prefix>[""']?[\w.\-]*(?:password|token|secret|api[_\-.]?key)[\w.\-]*[""']?\s*[:=]\s*)(?<value>""(?:\\.|[^""])*""|'(?:\\.|[^'])*'|[^,}\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public sealed class AuditLogLookupRequest
    {
        public DateOnly? FromDate { get; init; }
        public DateOnly? ToDate { get; init; }
        public string Username { get; init; } = string.Empty;
        public string EntityName { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string SearchText { get; init; } = string.Empty;
    }

    public sealed class AuditLogLookupResult
    {
        public bool IsAuthorized { get; init; }
        public IReadOnlyList<AuditLogLookupRow> Rows { get; init; } = [];
        public bool IsTruncated { get; init; }
        public bool IsScanLimitReached { get; init; }
        public int ScannedCount { get; init; }
        public int Limit => AuditLogLookupLimit;
        public int ScanLimit => AuditLogLookupScanLimit;
    }

    public sealed class AuditLogLookupRow
    {
        public Guid Id { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public string CreatedAtText => FormatAuditLogTimestamp(CreatedAtUtc);
        public string Username { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string OfficeCode { get; init; } = string.Empty;
        public string EntityName { get; init; } = string.Empty;
        public string EntityDisplayName { get; init; } = string.Empty;
        public string EntityId { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string TargetText { get; init; } = string.Empty;
        public string BeforeJson { get; init; } = string.Empty;
        public string AfterJson { get; init; } = string.Empty;
        public string BeforeJsonText => string.IsNullOrWhiteSpace(BeforeJson) ? "(기록 없음)" : BeforeJson;
        public string AfterJsonText => string.IsNullOrWhiteSpace(AfterJson) ? "(기록 없음)" : AfterJson;
        public string DetailHeader => $"{EntityDisplayName} / {Action}";
        public string DetailMetadata => $"{CreatedAtText} / {Username} / {OfficeCode} / {EntityId}";
    }

    private sealed record AuditTargetContext(bool IsVisible, string TargetText);

    private sealed class AuditBatchEntities
    {
        public Dictionary<Guid, LocalCompanyProfile> CompanyProfiles { get; init; } = [];
        public Dictionary<Guid, LocalCustomerCategory> CustomerCategories { get; init; } = [];
        public Dictionary<Guid, LocalPriceGradeOption> PriceGradeOptions { get; init; } = [];
        public Dictionary<Guid, LocalTradeTypeOption> TradeTypeOptions { get; init; } = [];
        public Dictionary<Guid, LocalItemCategoryOption> ItemCategoryOptions { get; init; } = [];
        public Dictionary<Guid, LocalCustomer> Customers { get; init; } = [];
        public Dictionary<Guid, LocalCustomerContract> CustomerContracts { get; init; } = [];
        public Dictionary<Guid, LocalItem> Items { get; init; } = [];
        public Dictionary<Guid, LocalInvoice> Invoices { get; init; } = [];
        public Dictionary<Guid, LocalInvoiceLine> InvoiceLines { get; init; } = [];
        public Dictionary<Guid, LocalPayment> Payments { get; init; } = [];
        public Dictionary<Guid, LocalTransaction> Transactions { get; init; } = [];
        public Dictionary<Guid, LocalTransactionAttachment> TransactionAttachments { get; init; } = [];
        public Dictionary<Guid, LocalInventoryTransfer> InventoryTransfers { get; init; } = [];
        public Dictionary<Guid, LocalRentalManagementCompany> RentalManagementCompanies { get; init; } = [];
        public Dictionary<Guid, LocalRentalBillingProfile> RentalBillingProfiles { get; init; } = [];
        public Dictionary<Guid, LocalRentalAsset> RentalAssets { get; init; } = [];
        public Dictionary<Guid, LocalRentalAssetAssignmentHistory> RentalAssetAssignmentHistories { get; init; } = [];
        public Dictionary<Guid, LocalRentalBillingLog> RentalBillingLogs { get; init; } = [];
    }

    public async Task<AuditLogLookupResult> LookupAuditLogsAsync(
        AuditLogLookupRequest? request,
        CancellationToken ct = default)
    {
        if (!CanLookupAuditLogs())
        {
            return new AuditLogLookupResult
            {
                IsAuthorized = false
            };
        }

        request ??= new AuditLogLookupRequest();
        var (fromDate, toDate) = NormalizeAuditDateRange(request.FromDate, request.ToDate);
        var query = _db.AuditLogs.AsNoTracking();

        if (fromDate.HasValue)
        {
            var fromUtc = ToAuditUtc(fromDate.Value, TimeOnly.MinValue);
            query = query.Where(log => log.CreatedAtUtc >= fromUtc);
        }

        if (toDate.HasValue && toDate.Value < DateOnly.MaxValue)
        {
            var toExclusiveUtc = ToAuditUtc(toDate.Value.AddDays(1), TimeOnly.MinValue);
            query = query.Where(log => log.CreatedAtUtc < toExclusiveUtc);
        }

        var username = request.Username.Trim();
        if (!string.IsNullOrWhiteSpace(username))
        {
            var usernamePattern = BuildAuditLikePattern(username);
            query = query.Where(log => EF.Functions.Like(log.Username, usernamePattern, "\\"));
        }

        var entityNames = ExpandAuditEntityFilter(request.EntityName);
        if (entityNames.Count > 0)
            query = query.Where(log => entityNames.Contains(log.EntityName));

        var action = request.Action.Trim();
        if (!string.IsNullOrWhiteSpace(action))
        {
            var actionPattern = BuildAuditLikePattern(action);
            query = query.Where(log => EF.Functions.Like(log.Action, actionPattern, "\\"));
        }

        var orderedQuery = query
            .OrderByDescending(log => log.CreatedAtUtc)
            .ThenByDescending(log => log.Id);
        var rows = new List<AuditLogLookupRow>(AuditLogLookupLimit + 1);
        var skip = 0;
        var scannedCount = 0;

        while (rows.Count <= AuditLogLookupLimit && skip < AuditLogLookupScanLimit)
        {
            var take = Math.Min(AuditLogLookupBatchSize, AuditLogLookupScanLimit - skip);
            var logs = await orderedQuery
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);
            if (logs.Count == 0)
                break;

            skip += logs.Count;
            var batch = await LoadAuditBatchEntitiesAsync(logs, ct);
            foreach (var log in logs)
            {
                scannedCount++;
                var target = ResolveAuditTargetContext(log, batch);
                if (!target.IsVisible)
                    continue;

                var beforeJson = MaskAndFormatAuditJson(log.BeforeJson);
                var afterJson = MaskAndFormatAuditJson(log.AfterJson);
                var canonicalEntityName = CanonicalizeAuditEntityName(log.EntityName);
                var row = new AuditLogLookupRow
                {
                    Id = log.Id,
                    CreatedAtUtc = log.CreatedAtUtc,
                    Username = log.Username,
                    Role = log.Role,
                    OfficeCode = log.OfficeCode,
                    EntityName = log.EntityName,
                    EntityDisplayName = GetAuditEntityDisplayName(canonicalEntityName),
                    EntityId = log.EntityId,
                    Action = log.Action,
                    TargetText = target.TargetText,
                    BeforeJson = beforeJson,
                    AfterJson = afterJson
                };

                if (!MatchesAuditSearch(row, request.SearchText))
                    continue;

                rows.Add(row);
                if (rows.Count > AuditLogLookupLimit)
                    break;
            }

            if (logs.Count < take)
                break;
        }

        var isTruncated = rows.Count > AuditLogLookupLimit;
        var isScanLimitReached = !isTruncated &&
                                 skip >= AuditLogLookupScanLimit &&
                                 await orderedQuery.Skip(AuditLogLookupScanLimit).AnyAsync(ct);
        if (isTruncated)
            rows.RemoveRange(AuditLogLookupLimit, rows.Count - AuditLogLookupLimit);

        return new AuditLogLookupResult
        {
            IsAuthorized = true,
            Rows = rows,
            IsTruncated = isTruncated,
            IsScanLimitReached = isScanLimitReached,
            ScannedCount = scannedCount
        };
    }

    private bool CanLookupAuditLogs()
        => _session.IsLoggedIn &&
           (_session.HasAdministrativePrivileges ||
            _session.HasPermission(AppPermissionNames.DataBackupRestore));

    private async Task<AuditBatchEntities> LoadAuditBatchEntitiesAsync(
        IReadOnlyCollection<LocalAuditLog> logs,
        CancellationToken ct)
    {
        var idsByEntity = BuildAuditEntityIdMap(logs);

        var customerContracts = await LoadAuditEntitiesAsync<LocalCustomerContract>(GetAuditIds(idsByEntity, nameof(LocalCustomerContract)), ct);
        var invoiceLines = await LoadAuditInvoiceLinesAsync(GetAuditIds(idsByEntity, nameof(LocalInvoiceLine)), ct);
        var payments = await LoadAuditEntitiesAsync<LocalPayment>(GetAuditIds(idsByEntity, nameof(LocalPayment)), ct);
        var transactionAttachments = await LoadAuditEntitiesAsync<LocalTransactionAttachment>(GetAuditIds(idsByEntity, nameof(LocalTransactionAttachment)), ct);
        var inventoryTransfers = await LoadAuditEntitiesAsync<LocalInventoryTransfer>(GetAuditIds(idsByEntity, nameof(LocalInventoryTransfer)), ct);
        var rentalAssets = await LoadAuditEntitiesAsync<LocalRentalAsset>(GetAuditIds(idsByEntity, nameof(LocalRentalAsset)), ct);
        var rentalAssignmentHistories = await LoadAuditEntitiesAsync<LocalRentalAssetAssignmentHistory>(GetAuditIds(idsByEntity, nameof(LocalRentalAssetAssignmentHistory)), ct);
        var rentalBillingLogs = await LoadAuditEntitiesAsync<LocalRentalBillingLog>(GetAuditIds(idsByEntity, nameof(LocalRentalBillingLog)), ct);

        var transactionIds = new HashSet<Guid>(GetAuditIds(idsByEntity, nameof(LocalTransaction)));
        transactionIds.UnionWith(transactionAttachments.Values.Select(attachment => attachment.TransactionId));
        var transactions = await LoadAuditEntitiesAsync<LocalTransaction>(transactionIds, ct);

        var invoiceIds = new HashSet<Guid>(GetAuditIds(idsByEntity, nameof(LocalInvoice)));
        invoiceIds.UnionWith(invoiceLines.Values.Select(line => line.InvoiceId));
        invoiceIds.UnionWith(payments.Values.Select(payment => payment.InvoiceId));
        invoiceIds.UnionWith(transactions.Values
            .Where(transaction => transaction.LinkedInvoiceId.HasValue)
            .Select(transaction => transaction.LinkedInvoiceId!.Value));
        var invoices = await LoadAuditEntitiesAsync<LocalInvoice>(invoiceIds, ct);

        var rentalProfileIds = new HashSet<Guid>(GetAuditIds(idsByEntity, nameof(LocalRentalBillingProfile)));
        rentalProfileIds.UnionWith(rentalBillingLogs.Values.Select(log => log.BillingProfileId));
        rentalProfileIds.UnionWith(rentalAssets.Values
            .Where(asset => asset.BillingProfileId.HasValue)
            .Select(asset => asset.BillingProfileId!.Value));
        rentalProfileIds.UnionWith(rentalAssignmentHistories.Values
            .Where(history => history.BillingProfileId.HasValue)
            .Select(history => history.BillingProfileId!.Value));
        rentalProfileIds.UnionWith(invoices.Values
            .Where(invoice => invoice.LinkedRentalBillingProfileId.HasValue)
            .Select(invoice => invoice.LinkedRentalBillingProfileId!.Value));
        rentalProfileIds.UnionWith(transactions.Values
            .Where(transaction => transaction.LinkedRentalBillingProfileId.HasValue)
            .Select(transaction => transaction.LinkedRentalBillingProfileId!.Value));
        var rentalBillingProfiles = await LoadAuditEntitiesAsync<LocalRentalBillingProfile>(rentalProfileIds, ct);

        var customerIds = new HashSet<Guid>(GetAuditIds(idsByEntity, nameof(LocalCustomer)));
        customerIds.UnionWith(customerContracts.Values.Select(contract => contract.CustomerId));
        customerIds.UnionWith(invoices.Values.Select(invoice => invoice.CustomerId));
        customerIds.UnionWith(transactions.Values.Select(transaction => transaction.CustomerId));
        customerIds.UnionWith(rentalBillingProfiles.Values
            .Where(profile => profile.CustomerId.HasValue)
            .Select(profile => profile.CustomerId!.Value));
        customerIds.UnionWith(rentalAssets.Values
            .Where(asset => asset.CustomerId.HasValue)
            .Select(asset => asset.CustomerId!.Value));
        customerIds.UnionWith(rentalAssignmentHistories.Values
            .Where(history => history.CustomerId.HasValue)
            .Select(history => history.CustomerId!.Value));
        var customers = await LoadAuditEntitiesAsync<LocalCustomer>(customerIds, ct);

        var itemIds = new HashSet<Guid>(GetAuditIds(idsByEntity, nameof(LocalItem)));
        itemIds.UnionWith(invoiceLines.Values
            .Where(line => line.ItemId.HasValue)
            .Select(line => line.ItemId!.Value));
        itemIds.UnionWith(rentalAssets.Values
            .Where(asset => asset.ItemId.HasValue)
            .Select(asset => asset.ItemId!.Value));
        var items = await LoadAuditEntitiesAsync<LocalItem>(itemIds, ct);

        return new AuditBatchEntities
        {
            CompanyProfiles = await LoadAuditEntitiesAsync<LocalCompanyProfile>(GetAuditIds(idsByEntity, nameof(LocalCompanyProfile)), ct),
            CustomerCategories = await LoadAuditEntitiesAsync<LocalCustomerCategory>(GetAuditIds(idsByEntity, nameof(LocalCustomerCategory)), ct),
            PriceGradeOptions = await LoadAuditEntitiesAsync<LocalPriceGradeOption>(GetAuditIds(idsByEntity, nameof(LocalPriceGradeOption)), ct),
            TradeTypeOptions = await LoadAuditEntitiesAsync<LocalTradeTypeOption>(GetAuditIds(idsByEntity, nameof(LocalTradeTypeOption)), ct),
            ItemCategoryOptions = await LoadAuditEntitiesAsync<LocalItemCategoryOption>(GetAuditIds(idsByEntity, nameof(LocalItemCategoryOption)), ct),
            Customers = customers,
            CustomerContracts = customerContracts,
            Items = items,
            Invoices = invoices,
            InvoiceLines = invoiceLines,
            Payments = payments,
            Transactions = transactions,
            TransactionAttachments = transactionAttachments,
            InventoryTransfers = inventoryTransfers,
            RentalManagementCompanies = await LoadAuditEntitiesAsync<LocalRentalManagementCompany>(GetAuditIds(idsByEntity, nameof(LocalRentalManagementCompany)), ct),
            RentalBillingProfiles = rentalBillingProfiles,
            RentalAssets = rentalAssets,
            RentalAssetAssignmentHistories = rentalAssignmentHistories,
            RentalBillingLogs = rentalBillingLogs
        };
    }

    private async Task<Dictionary<Guid, T>> LoadAuditEntitiesAsync<T>(
        IEnumerable<Guid> sourceIds,
        CancellationToken ct)
        where T : LocalSyncEntity
    {
        var ids = sourceIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return [];

        return await _db.Set<T>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => ids.Contains(entity.Id))
            .ToDictionaryAsync(entity => entity.Id, ct);
    }

    private async Task<Dictionary<Guid, LocalInvoiceLine>> LoadAuditInvoiceLinesAsync(
        IEnumerable<Guid> sourceIds,
        CancellationToken ct)
    {
        var ids = sourceIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return [];

        return await _db.InvoiceLines
            .AsNoTracking()
            .Where(line => ids.Contains(line.Id))
            .ToDictionaryAsync(line => line.Id, ct);
    }

    private AuditTargetContext ResolveAuditTargetContext(LocalAuditLog log, AuditBatchEntities batch)
    {
        if (!Guid.TryParse(log.EntityId, out var entityId))
            return ResolveUnverifiableAuditTarget(log);

        var canonicalName = CanonicalizeAuditEntityName(log.EntityName);
        switch (canonicalName)
        {
            case nameof(LocalCustomer):
                if (batch.Customers.TryGetValue(entityId, out var customer))
                {
                    return new AuditTargetContext(
                        CanAccessCustomer(customer, _session),
                        JoinAuditTargetText(
                            customer.NameOriginal,
                            customer.BusinessNumber,
                            customer.Phone,
                            customer.ResponsibleOfficeCode));
                }
                break;

            case nameof(LocalCustomerContract):
                if (batch.CustomerContracts.TryGetValue(entityId, out var contract) &&
                    batch.Customers.TryGetValue(contract.CustomerId, out var contractCustomer))
                {
                    return new AuditTargetContext(
                        CanAccessCustomer(contractCustomer, _session),
                        JoinAuditTargetText(
                            contractCustomer.NameOriginal,
                            contract.ContractType,
                            contract.FileName,
                            contract.Description));
                }
                break;

            case nameof(LocalItem):
                if (batch.Items.TryGetValue(entityId, out var item))
                {
                    return new AuditTargetContext(
                        CanReadItemScope(item, _session),
                        JoinAuditTargetText(
                            item.NameOriginal,
                            item.SpecificationOriginal,
                            item.MaterialNumber,
                            item.SerialNumber,
                            item.InstallLocation));
                }
                break;

            case nameof(LocalInvoice):
                if (batch.Invoices.TryGetValue(entityId, out var invoice))
                {
                    batch.Customers.TryGetValue(invoice.CustomerId, out var invoiceCustomer);
                    return new AuditTargetContext(
                        CanAccessInvoice(invoice, _session),
                        JoinAuditTargetText(
                            invoiceCustomer?.NameOriginal,
                            ResolveAuditInvoiceNumber(invoice),
                            invoice.TaxInvoiceNumber,
                            invoice.InvoiceDate.ToString("yyyy-MM-dd"),
                            invoice.Memo));
                }
                break;

            case nameof(LocalInvoiceLine):
                if (batch.InvoiceLines.TryGetValue(entityId, out var line) &&
                    batch.Invoices.TryGetValue(line.InvoiceId, out var lineInvoice))
                {
                    batch.Customers.TryGetValue(lineInvoice.CustomerId, out var lineCustomer);
                    return new AuditTargetContext(
                        CanAccessInvoice(lineInvoice, _session),
                        JoinAuditTargetText(
                            lineCustomer?.NameOriginal,
                            ResolveAuditInvoiceNumber(lineInvoice),
                            line.ItemNameOriginal,
                            line.SpecificationOriginal,
                            line.MaterialNumber,
                            line.SerialNumber,
                            line.Remark));
                }
                break;

            case nameof(LocalPayment):
                if (batch.Payments.TryGetValue(entityId, out var payment) &&
                    batch.Invoices.TryGetValue(payment.InvoiceId, out var paymentInvoice))
                {
                    batch.Customers.TryGetValue(paymentInvoice.CustomerId, out var paymentCustomer);
                    return new AuditTargetContext(
                        CanAccessInvoice(paymentInvoice, _session),
                        JoinAuditTargetText(
                            paymentCustomer?.NameOriginal,
                            ResolveAuditInvoiceNumber(paymentInvoice),
                            payment.PaymentDate.ToString("yyyy-MM-dd"),
                            payment.Note));
                }
                break;

            case nameof(LocalTransaction):
                if (batch.Transactions.TryGetValue(entityId, out var transaction))
                {
                    batch.Customers.TryGetValue(transaction.CustomerId, out var transactionCustomer);
                    return new AuditTargetContext(
                        CanAccessTransaction(transaction, _session),
                        JoinAuditTargetText(
                            transactionCustomer?.NameOriginal,
                            transaction.LinkedInvoiceNumber,
                            transaction.TransactionKind,
                            transaction.TransactionDate.ToString("yyyy-MM-dd"),
                            transaction.Note,
                            transaction.Memo));
                }
                break;

            case nameof(LocalTransactionAttachment):
                if (batch.TransactionAttachments.TryGetValue(entityId, out var attachment) &&
                    batch.Transactions.TryGetValue(attachment.TransactionId, out var attachmentTransaction))
                {
                    batch.Customers.TryGetValue(attachmentTransaction.CustomerId, out var attachmentCustomer);
                    return new AuditTargetContext(
                        CanAccessTransaction(attachmentTransaction, _session),
                        JoinAuditTargetText(
                            attachmentCustomer?.NameOriginal,
                            attachmentTransaction.LinkedInvoiceNumber,
                            attachment.FileName,
                            attachment.AttachmentType,
                            attachment.VerificationStatus,
                            attachment.Description));
                }
                break;

            case nameof(LocalInventoryTransfer):
                if (batch.InventoryTransfers.TryGetValue(entityId, out var transfer))
                {
                    return new AuditTargetContext(
                        CanReadAuditInventoryTransfer(transfer),
                        JoinAuditTargetText(
                            transfer.TransferNumber,
                            transfer.TransferDate.ToString("yyyy-MM-dd"),
                            $"{transfer.FromWarehouseCode} → {transfer.ToWarehouseCode}",
                            transfer.TransferStatus,
                            transfer.Memo));
                }
                break;

            case nameof(LocalRentalBillingProfile):
                if (batch.RentalBillingProfiles.TryGetValue(entityId, out var profile))
                {
                    return new AuditTargetContext(
                        CanReadAuditRentalScope(profile.TenantCode, profile.ResponsibleOfficeCode, profile.ManagementCompanyCode),
                        JoinAuditTargetText(
                            profile.CustomerName,
                            profile.BusinessNumber,
                            profile.ItemName,
                            profile.InstallSiteName,
                            profile.ProfileKey));
                }
                break;

            case nameof(LocalRentalAsset):
                if (batch.RentalAssets.TryGetValue(entityId, out var asset))
                {
                    var ownerOfficeCode = string.IsNullOrWhiteSpace(asset.ManagementCompanyCode)
                        ? asset.OfficeCode
                        : asset.ManagementCompanyCode;
                    return new AuditTargetContext(
                        CanReadAuditRentalScope(asset.TenantCode, asset.ResponsibleOfficeCode, ownerOfficeCode),
                        JoinAuditTargetText(
                            asset.CustomerName,
                            asset.ItemName,
                            asset.ManagementNumber,
                            asset.MachineNumber,
                            asset.AssetKey,
                            asset.InstallLocation));
                }
                break;

            case nameof(LocalRentalAssetAssignmentHistory):
                if (batch.RentalAssetAssignmentHistories.TryGetValue(entityId, out var history))
                {
                    return new AuditTargetContext(
                        CanReadAuditRentalScope(history.TenantCode, history.ResponsibleOfficeCode),
                        JoinAuditTargetText(
                            history.CustomerName,
                            history.ItemName,
                            history.ManagementNumber,
                            history.MachineNumber,
                            history.InstallLocation,
                            history.ChangeReason));
                }
                break;

            case nameof(LocalRentalBillingLog):
                if (batch.RentalBillingLogs.TryGetValue(entityId, out var billingLog))
                {
                    batch.RentalBillingProfiles.TryGetValue(billingLog.BillingProfileId, out var billingProfile);
                    return new AuditTargetContext(
                        CanReadAuditRentalScope(billingLog.TenantCode, billingLog.ResponsibleOfficeCode, billingLog.OfficeCode),
                        JoinAuditTargetText(
                            billingProfile?.CustomerName,
                            billingProfile?.ItemName,
                            billingLog.BillingYearMonth,
                            billingLog.Status,
                            billingLog.Note));
                }
                break;

            case nameof(LocalCompanyProfile):
                if (batch.CompanyProfiles.TryGetValue(entityId, out var companyProfile))
                {
                    return new AuditTargetContext(
                        CanReadAuditOffice(companyProfile.OfficeCode),
                        JoinAuditTargetText(
                            companyProfile.ProfileName,
                            companyProfile.TradeName,
                            companyProfile.BusinessNumber,
                            companyProfile.OfficeCode));
                }
                break;

            case nameof(LocalCustomerCategory):
                if (batch.CustomerCategories.TryGetValue(entityId, out var category))
                    return ResolveSharedAuditTarget(log, category.Name);
                break;

            case nameof(LocalPriceGradeOption):
                if (batch.PriceGradeOptions.TryGetValue(entityId, out var priceGrade))
                    return ResolveSharedAuditTarget(log, priceGrade.Name, priceGrade.PriceSource);
                break;

            case nameof(LocalTradeTypeOption):
                if (batch.TradeTypeOptions.TryGetValue(entityId, out var tradeType))
                    return ResolveSharedAuditTarget(log, tradeType.Name);
                break;

            case nameof(LocalItemCategoryOption):
                if (batch.ItemCategoryOptions.TryGetValue(entityId, out var itemCategory))
                    return ResolveSharedAuditTarget(log, itemCategory.Name);
                break;

            case nameof(LocalRentalManagementCompany):
                if (batch.RentalManagementCompanies.TryGetValue(entityId, out var rentalCompany))
                    return ResolveSharedAuditTarget(log, rentalCompany.Code, rentalCompany.Name);
                break;
        }

        return ResolveUnverifiableAuditTarget(log);
    }

    private AuditTargetContext ResolveSharedAuditTarget(LocalAuditLog log, params string?[] values)
        => new(CanReadAuditOffice(log.OfficeCode), JoinAuditTargetText(values));

    private AuditTargetContext ResolveUnverifiableAuditTarget(LocalAuditLog log)
        => new(_session.HasGlobalDataScope, log.EntityId);

    private bool CanReadAuditOffice(string? officeCode)
    {
        if (!_session.IsLoggedIn)
            return false;
        if (_session.HasGlobalDataScope)
            return true;

        var normalizedOfficeCode = NormalizeOfficeScope(officeCode, string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedOfficeCode))
            return false;

        var officeTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, normalizedOfficeCode);
        if (!string.Equals(officeTenantCode, ResolveCurrentTenantCode(_session), StringComparison.OrdinalIgnoreCase))
            return false;

        return IsSharedOfficeScope(normalizedOfficeCode) || GetReadableOfficeCodes(_session).Contains(normalizedOfficeCode);
    }

    private bool CanReadAuditInventoryTransfer(LocalInventoryTransfer transfer)
    {
        var tenantWarehouseCodes = GetTenantWarehouseCodes(_session);
        if (!tenantWarehouseCodes.Contains(transfer.FromWarehouseCode, StringComparer.OrdinalIgnoreCase) ||
            !tenantWarehouseCodes.Contains(transfer.ToWarehouseCode, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_session.HasGlobalDataScope || CanViewAllDeliveryScope(_session))
            return true;

        var readableWarehouseCodes = GetReadableOfficeCodes(_session)
            .Select(OfficeCodeCatalog.GetMainWarehouseCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return readableWarehouseCodes.Contains(transfer.FromWarehouseCode) ||
               readableWarehouseCodes.Contains(transfer.ToWarehouseCode);
    }

    private bool CanReadAuditRentalScope(
        string? tenantCode,
        string? responsibleOfficeCode,
        string? managementCompanyCode = null)
    {
        if (!_session.IsLoggedIn)
            return false;
        if (_session.HasGlobalDataScope)
            return true;

        var entityTenantCode = ResolveRentalEntityTenantCode(tenantCode, managementCompanyCode, responsibleOfficeCode);
        if (!string.Equals(entityTenantCode, ResolveCurrentTenantCode(_session), StringComparison.OrdinalIgnoreCase))
            return false;

        if (_session.HasAdministrativePrivileges ||
            _session.HasAssignedPermission(AppPermissionNames.RentalViewAll) ||
            _session.HasAssignedPermission(AppPermissionNames.RentalEditAll))
        {
            return true;
        }

        var officeCode = ResolveResponsibleOfficeScopeForAccess(responsibleOfficeCode, managementCompanyCode);
        return IsSharedOfficeScope(officeCode) || GetReadableRentalOfficeCodes(_session).Contains(officeCode);
    }

    private static Dictionary<string, HashSet<Guid>> BuildAuditEntityIdMap(IEnumerable<LocalAuditLog> logs)
    {
        var result = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in logs)
        {
            if (!Guid.TryParse(log.EntityId, out var id) || id == Guid.Empty)
                continue;

            var canonicalName = CanonicalizeAuditEntityName(log.EntityName);
            if (!result.TryGetValue(canonicalName, out var ids))
            {
                ids = [];
                result[canonicalName] = ids;
            }

            ids.Add(id);
        }

        return result;
    }

    private static IReadOnlyCollection<Guid> GetAuditIds(
        IReadOnlyDictionary<string, HashSet<Guid>> idsByEntity,
        string entityName)
        => idsByEntity.TryGetValue(entityName, out var ids) ? ids : Array.Empty<Guid>();

    private static string CanonicalizeAuditEntityName(string? entityName)
    {
        var normalized = (entityName ?? string.Empty).Trim();
        if (string.Equals(normalized, "Customer", StringComparison.OrdinalIgnoreCase))
            return nameof(LocalCustomer);
        if (string.Equals(normalized, "Item", StringComparison.OrdinalIgnoreCase))
            return nameof(LocalItem);

        return normalized;
    }

    private static List<string> ExpandAuditEntityFilter(string? entityName)
    {
        var canonicalName = CanonicalizeAuditEntityName(entityName);
        if (string.IsNullOrWhiteSpace(canonicalName))
            return [];
        if (string.Equals(canonicalName, nameof(LocalCustomer), StringComparison.OrdinalIgnoreCase))
            return [nameof(LocalCustomer), "Customer"];
        if (string.Equals(canonicalName, nameof(LocalItem), StringComparison.OrdinalIgnoreCase))
            return [nameof(LocalItem), "Item"];

        return [canonicalName];
    }

    private static string GetAuditEntityDisplayName(string canonicalName)
        => canonicalName switch
        {
            nameof(LocalCustomer) => "거래처",
            nameof(LocalCustomerContract) => "거래처 계약",
            nameof(LocalItem) => "품목",
            nameof(LocalInvoice) => "전표",
            nameof(LocalInvoiceLine) => "전표 항목",
            nameof(LocalPayment) => "수금·지급",
            nameof(LocalTransaction) => "거래 전표",
            nameof(LocalTransactionAttachment) => "거래 증빙",
            nameof(LocalInventoryTransfer) => "재고이동",
            nameof(LocalRentalBillingProfile) => "렌탈 청구 프로필",
            nameof(LocalRentalAsset) => "렌탈 자산",
            nameof(LocalRentalAssetAssignmentHistory) => "자산 배정 이력",
            nameof(LocalRentalBillingLog) => "렌탈 청구 이력",
            nameof(LocalCompanyProfile) => "회사 설정",
            nameof(LocalCustomerCategory) => "거래처 분류",
            nameof(LocalPriceGradeOption) => "가격 등급",
            nameof(LocalTradeTypeOption) => "거래 유형",
            nameof(LocalItemCategoryOption) => "품목 분류",
            nameof(LocalRentalManagementCompany) => "렌탈 관리업체",
            _ => string.IsNullOrWhiteSpace(canonicalName) ? "기타" : canonicalName
        };

    private static string ResolveAuditInvoiceNumber(LocalInvoice invoice)
        => !string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            ? invoice.InvoiceNumber
            : invoice.LocalTempNumber;

    private static string JoinAuditTargetText(params string?[] values)
        => string.Join(
            " / ",
            values.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));

    private static bool MatchesAuditSearch(AuditLogLookupRow row, string? searchText)
    {
        var tokens = (searchText ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return true;

        var searchableText = string.Join(
            Environment.NewLine,
            row.EntityName,
            row.EntityDisplayName,
            row.EntityId,
            row.Action,
            row.Username,
            row.OfficeCode,
            row.TargetText,
            row.BeforeJson,
            row.AfterJson);
        return tokens.All(token => searchableText.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string MaskAndFormatAuditJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        var source = json.Trim();
        try
        {
            var node = JsonNode.Parse(source);
            if (node is null)
                return source;

            var maskedNode = MaskSensitiveAuditJsonNode(node);
            return maskedNode?.ToJsonString(AuditLogLookupJsonOptions) ?? string.Empty;
        }
        catch (JsonException)
        {
            return SensitiveJsonFallbackRegex.Replace(
                source,
                match => match.Groups["prefix"].Value + "\"***\"");
        }
    }

    private static JsonNode? MaskSensitiveAuditJsonNode(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var key in jsonObject.Select(pair => pair.Key).ToList())
            {
                if (IsSensitiveAuditJsonKey(key))
                {
                    jsonObject[key] = "***";
                    continue;
                }

                var child = jsonObject[key];
                var maskedChild = MaskSensitiveAuditJsonNode(child);
                if (!ReferenceEquals(child, maskedChild))
                    jsonObject[key] = maskedChild;
            }

            return jsonObject;
        }

        if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                var child = jsonArray[index];
                var maskedChild = MaskSensitiveAuditJsonNode(child);
                if (!ReferenceEquals(child, maskedChild))
                    jsonArray[index] = maskedChild;
            }
            return jsonArray;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            return JsonValue.Create(SensitiveJsonFallbackRegex.Replace(
                text,
                match => match.Groups["prefix"].Value + "***"));
        }

        return node;
    }

    private static bool IsSensitiveAuditJsonKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var normalized = new string(key
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("apikey", StringComparison.Ordinal);
    }

    private static (DateOnly? From, DateOnly? To) NormalizeAuditDateRange(DateOnly? from, DateOnly? to)
        => from.HasValue && to.HasValue && from.Value > to.Value
            ? (to, from)
            : (from, to);

    private static DateTime ToAuditUtc(DateOnly date, TimeOnly time)
        => DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Local).ToUniversalTime();

    private static string BuildAuditLikePattern(string value)
        => $"%{value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)}%";

    private static string FormatAuditLogTimestamp(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }
}
