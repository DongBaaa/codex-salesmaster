using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private static readonly IReadOnlySet<RecycleBinEntityKind>
        ServerPurgePlanGuardKinds =
            new HashSet<RecycleBinEntityKind>
            {
                RecycleBinEntityKind.Customer,
                RecycleBinEntityKind.CustomerContract,
                RecycleBinEntityKind.Item,
                RecycleBinEntityKind.CompanyProfile,
                RecycleBinEntityKind.CustomerCategory,
                RecycleBinEntityKind.PriceGradeOption,
                RecycleBinEntityKind.TradeTypeOption,
                RecycleBinEntityKind.ItemCategoryOption,
                RecycleBinEntityKind.Payment,
                RecycleBinEntityKind.Transaction,
                RecycleBinEntityKind.InventoryTransfer,
                RecycleBinEntityKind.RentalManagementCompany,
                RecycleBinEntityKind.RentalBillingProfile,
                RecycleBinEntityKind.RentalAsset,
                RecycleBinEntityKind.RentalBillingLog
            };

    private static IReadOnlySet<RecycleBinEntityKind>
        GetServerPurgePlanGuardKinds()
        => ServerPurgePlanGuardKinds;

    private async Task<string?> BuildServerPurgePlanBlockReasonAsync(
        RecycleBinEntityKind kind,
        Guid entityId,
        long? purgeRevision,
        string? expectedBusinessDatabaseName,
        CancellationToken ct)
    {
        if (!ServerPurgePlanGuardKinds.Contains(kind))
        {
            return
                "실제 로컬 삭제 범위를 확인할 수 없는 휴지통 항목이어서 서버 영구삭제 반영을 보류했습니다.";
        }

        var plan = await LoadServerPurgeGuardPlanAsync(
            kind,
            entityId,
            ct);
        var normalizedExpectedBusinessDatabaseName =
            string.IsNullOrWhiteSpace(
                expectedBusinessDatabaseName)
                ? null
                : TenantScopeCatalog.GetDatabaseName(
                    expectedBusinessDatabaseName);
        var normalizedPlanBusinessDatabaseName =
            string.IsNullOrWhiteSpace(
                plan.BusinessDatabaseName)
                ? null
                : TenantScopeCatalog.GetDatabaseName(
                    plan.BusinessDatabaseName);
        if (normalizedExpectedBusinessDatabaseName is not null &&
            normalizedPlanBusinessDatabaseName is not null &&
            !string.Equals(
                normalizedExpectedBusinessDatabaseName,
                normalizedPlanBusinessDatabaseName,
                StringComparison.OrdinalIgnoreCase))
        {
            return
                "서버 영구삭제 영수증의 업무 DB와 로컬 삭제 대상 범위가 달라 반영을 보류했습니다.";
        }

        var businessDatabaseName =
            normalizedExpectedBusinessDatabaseName ??
            normalizedPlanBusinessDatabaseName ??
            ResolveSessionRecycleBinBusinessDatabaseName(
                _session);
        if (plan.ScopeEvidenceBusinessDatabaseNames.Any(
                current =>
                    !string.Equals(
                        TenantScopeCatalog.GetDatabaseName(
                            current),
                        businessDatabaseName,
                        StringComparison.OrdinalIgnoreCase)))
        {
            return
                "실제 로컬 삭제 대상의 참조 데이터가 다른 업무 DB에 속해 서버 영구삭제 반영을 보류했습니다.";
        }
        if (plan.Entities.Any(current =>
                IsServerPurgeGuardEntityFromAnotherBusinessDatabase(
                    current.Entity,
                    businessDatabaseName)))
        {
            return
                "실제 로컬 삭제 대상에 다른 업무 DB 범위의 데이터가 포함되어 서버 영구삭제 반영을 보류했습니다.";
        }

        if (plan.Entities.Any(current =>
                current.Entity.IsDirty))
        {
            return
                "실제 로컬 삭제 대상에 서버로 전송되지 않은 변경이 있어 서버 영구삭제 반영을 보류했습니다.";
        }
        if (purgeRevision.HasValue &&
            plan.Entities.Any(current =>
                current.Entity.Revision >
                purgeRevision.Value))
        {
            return
                "실제 로컬 삭제 대상에 서버 영구삭제 기록보다 최신인 데이터가 있어 반영을 보류했습니다.";
        }

        var allowedEntityNamesById =
            new Dictionary<Guid, HashSet<string>>();
        foreach (var planned in plan.Entities)
        {
            if (!allowedEntityNamesById.TryGetValue(
                    planned.Entity.Id,
                    out var allowedNames))
            {
                allowedNames = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                allowedEntityNamesById.Add(
                    planned.Entity.Id,
                    allowedNames);
            }

            allowedNames.UnionWith(
                planned.OutboxEntityNames);
        }

        if (!allowedEntityNamesById.ContainsKey(entityId))
        {
            var fallbackNames =
                GetRecycleBinOutboxEntityNames(kind);
            if (fallbackNames.Count > 0)
            {
                allowedEntityNamesById[entityId] =
                    new HashSet<string>(
                        fallbackNames,
                        StringComparer.OrdinalIgnoreCase);
            }
        }

        var plannedEntityIds =
            allowedEntityNamesById.Keys.ToList();
        if (plannedEntityIds.Count == 0)
            return null;

        var pendingOutboxCandidates =
            await _db.SyncOutboxEntries
                .AsNoTracking()
                .Where(current =>
                    plannedEntityIds.Contains(
                        current.EntityId) &&
                    current.Status != "Acknowledged")
                .ToListAsync(ct);
        var hasPendingOutbox =
            pendingOutboxCandidates.Any(current =>
                allowedEntityNamesById.TryGetValue(
                    current.EntityId,
                    out var allowedNames) &&
                allowedNames.Contains(
                    current.EntityName) &&
                string.Equals(
                    ResolveOutboxBusinessDatabaseName(
                        current),
                    businessDatabaseName,
                    StringComparison.OrdinalIgnoreCase));
        return hasPendingOutbox
            ? "실제 로컬 삭제 대상에 처리 중인 동일 업무 DB 동기화 작업이 있어 서버 영구삭제 반영을 보류했습니다."
            : null;
    }

    private async Task<ServerPurgeGuardPlan>
        LoadServerPurgeGuardPlanAsync(
            RecycleBinEntityKind kind,
            Guid entityId,
            CancellationToken ct)
    {
        var entities =
            new List<ServerPurgeGuardEntity>();
        var scopeEvidenceBusinessDatabaseNames =
            new List<string>();
        LocalSyncEntity? target = null;
        string? businessDatabaseName = null;

        switch (kind)
        {
            case RecycleBinEntityKind.Customer:
            {
                var customer = await _db.Customers
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        current =>
                            current.Id == entityId,
                        ct);
                var contracts =
                    await _db.CustomerContracts
                        .IgnoreQueryFilters()
                        .Where(current =>
                            current.CustomerId ==
                            entityId)
                        .ToListAsync(ct);
                var assignmentHistories =
                    await _db
                        .RentalAssetAssignmentHistories
                        .IgnoreQueryFilters()
                        .Where(current =>
                            current.CustomerId ==
                            entityId)
                        .ToListAsync(ct);
                target = customer;
                AddServerPurgeGuardEntity(
                    entities,
                    customer,
                    nameof(LocalCustomer),
                    "Customer");
                AddServerPurgeGuardEntities(
                    entities,
                    contracts,
                    nameof(LocalCustomerContract),
                    "CustomerContract");
                AddServerPurgeGuardEntities(
                    entities,
                    assignmentHistories,
                    nameof(
                        LocalRentalAssetAssignmentHistory),
                    "RentalAssetAssignmentHistory");
                if (customer is not null)
                {
                    businessDatabaseName =
                        ResolveRecycleBinBusinessDatabaseName(
                            customer.TenantCode,
                            customer.OfficeCode,
                            customer.ResponsibleOfficeCode);
                }

                break;
            }
            case RecycleBinEntityKind.CustomerContract:
            {
                var contract =
                    await _db.CustomerContracts
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(
                            current =>
                                current.Id ==
                                entityId,
                            ct);
                target = contract;
                AddServerPurgeGuardEntity(
                    entities,
                    contract,
                    nameof(LocalCustomerContract),
                    "CustomerContract");
                if (contract is not null)
                {
                    var customer =
                        await _db.Customers
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .FirstOrDefaultAsync(
                                current =>
                                    current.Id ==
                                    contract.CustomerId,
                                ct);
                    if (customer is not null)
                    {
                        businessDatabaseName =
                            ResolveRecycleBinBusinessDatabaseName(
                                customer.TenantCode,
                                customer.OfficeCode,
                                customer
                                    .ResponsibleOfficeCode);
                    }
                }

                break;
            }
            case RecycleBinEntityKind.Item:
            {
                var item = await _db.Items
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        current =>
                            current.Id == entityId,
                        ct);
                target = item;
                AddServerPurgeGuardEntity(
                    entities,
                    item,
                    nameof(LocalItem),
                    "Item");
                if (item is not null)
                {
                    businessDatabaseName =
                        ResolveRecycleBinBusinessDatabaseName(
                            item.TenantCode,
                            item.OfficeCode);
                }

                var itemPriceGrades =
                    await _db.ItemPriceGrades
                        .IgnoreQueryFilters()
                        .Where(current =>
                            current.ItemId ==
                            entityId)
                        .ToListAsync(ct);
                var rentalAssets =
                    await _db.RentalAssets
                        .IgnoreQueryFilters()
                        .Where(current =>
                            current.ItemId ==
                            entityId)
                        .ToListAsync(ct);
                var rentalProfiles =
                    await GetBillingProfilesContainingCatalogItemIdAsync(
                        entityId,
                        ct);
                AddServerPurgeGuardEntities(
                    entities,
                    itemPriceGrades,
                    nameof(LocalItemPriceGrade),
                    "ItemPriceGrade");
                AddServerPurgeGuardEntities(
                    entities,
                    rentalAssets,
                    nameof(LocalRentalAsset),
                    "RentalAsset");
                AddServerPurgeGuardEntities(
                    entities,
                    rentalProfiles,
                    nameof(LocalRentalBillingProfile),
                    "RentalBillingProfile");

                var invoiceIds = (await _db.InvoiceLines
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(current =>
                            current.ItemId ==
                            entityId)
                        .Select(current =>
                            current.InvoiceId)
                        .ToListAsync(ct))
                    .Concat(
                        await _db.InvoiceLineSerials
                            .AsNoTracking()
                            .Where(current =>
                                current.ItemId ==
                                entityId)
                            .Select(current =>
                                current.InvoiceId)
                            .ToListAsync(ct))
                    .Distinct()
                    .ToList();
                if (invoiceIds.Count > 0)
                {
                    var invoices =
                        await _db.Invoices
                            .IgnoreQueryFilters()
                            .Where(current =>
                                invoiceIds.Contains(
                                    current.Id))
                            .ToListAsync(ct);
                    AddServerPurgeGuardEntities(
                        entities,
                        invoices,
                        nameof(LocalInvoice),
                        "Invoice");
                }

                var transferIds =
                    await _db.InventoryTransferLines
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(current =>
                            current.ItemId ==
                            entityId)
                        .Select(current =>
                            current.TransferId)
                        .Distinct()
                        .ToListAsync(ct);
                if (transferIds.Count > 0)
                {
                    var transfers =
                        await _db.InventoryTransfers
                            .IgnoreQueryFilters()
                            .Where(current =>
                                transferIds.Contains(
                                    current.Id))
                            .ToListAsync(ct);
                    AddServerPurgeGuardEntities(
                        entities,
                        transfers,
                        nameof(LocalInventoryTransfer),
                        "InventoryTransfer");
                }

                break;
            }
            case RecycleBinEntityKind.Payment:
            {
                var payment = await _db.Payments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        current =>
                            current.Id == entityId,
                        ct);
                var sameIdTransaction =
                    await _db.Transactions
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(
                            current =>
                                current.Id ==
                                entityId,
                            ct);
                var attachments =
                    await _db.TransactionAttachments
                        .IgnoreQueryFilters()
                        .Where(current =>
                            current.TransactionId ==
                            entityId)
                        .ToListAsync(ct);
                target = payment;
                AddServerPurgeGuardEntity(
                    entities,
                    payment,
                    nameof(LocalPayment),
                    "Payment");
                AddServerPurgeGuardEntity(
                    entities,
                    sameIdTransaction,
                    nameof(LocalTransaction),
                    "TransactionRecord",
                    "Transaction");
                AddServerPurgeGuardEntities(
                    entities,
                    attachments,
                    nameof(LocalTransactionAttachment),
                    "TransactionAttachment");
                LocalInvoice? linkedInvoice = null;
                if (payment is not null)
                {
                    linkedInvoice = await _db.Invoices
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            current =>
                                current.Id ==
                                payment.InvoiceId,
                            ct);
                }
                AddServerPurgeScopeEvidence(
                    scopeEvidenceBusinessDatabaseNames,
                    linkedInvoice);

                businessDatabaseName =
                    ResolvePaymentBusinessDatabaseName(
                        linkedInvoice,
                        sameIdTransaction);
                var profileId =
                    sameIdTransaction
                        ?.LinkedRentalBillingProfileId ??
                    linkedInvoice
                        ?.LinkedRentalBillingProfileId;
                await AddServerPurgeGuardRentalProfileAsync(
                    entities,
                    profileId,
                    ct);
                break;
            }
            case RecycleBinEntityKind.Transaction:
            {
                var transaction =
                    await _db.Transactions
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(
                            current =>
                                current.Id ==
                                entityId,
                            ct);
                var linkedPayment =
                    await _db.Payments
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(
                            current =>
                                current.Id ==
                                entityId,
                            ct);
                var attachments =
                    await _db.TransactionAttachments
                        .IgnoreQueryFilters()
                        .Where(current =>
                            current.TransactionId ==
                            entityId)
                        .ToListAsync(ct);
                target = transaction;
                AddServerPurgeGuardEntity(
                    entities,
                    transaction,
                    nameof(LocalTransaction),
                    "TransactionRecord",
                    "Transaction");
                AddServerPurgeGuardEntity(
                    entities,
                    linkedPayment,
                    nameof(LocalPayment),
                    "Payment");
                AddServerPurgeGuardEntities(
                    entities,
                    attachments,
                    nameof(LocalTransactionAttachment),
                    "TransactionAttachment");
                LocalInvoice? linkedInvoice = null;
                if (linkedPayment is not null)
                {
                    linkedInvoice = await _db.Invoices
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            current =>
                                current.Id ==
                                linkedPayment.InvoiceId,
                            ct);
                }
                AddServerPurgeScopeEvidence(
                    scopeEvidenceBusinessDatabaseNames,
                    linkedInvoice);

                businessDatabaseName =
                    ResolvePaymentOrTransactionBusinessDatabaseName(
                        transaction,
                        linkedInvoice);
                var profileId =
                    transaction
                        ?.LinkedRentalBillingProfileId ??
                    linkedInvoice
                        ?.LinkedRentalBillingProfileId;
                await AddServerPurgeGuardRentalProfileAsync(
                    entities,
                    profileId,
                    ct);
                break;
            }
            case RecycleBinEntityKind.RentalBillingProfile:
            {
                var profile =
                    await _db.RentalBillingProfiles
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(
                            current =>
                                current.Id ==
                                entityId,
                            ct);
                var linkedAssets =
                    await _db.RentalAssets
                        .IgnoreQueryFilters()
                        .Where(current =>
                            current.BillingProfileId ==
                            entityId)
                        .ToListAsync(ct);
                var logs =
                    await _db.RentalBillingLogs
                        .IgnoreQueryFilters()
                        .Where(current =>
                            current.BillingProfileId ==
                            entityId)
                        .ToListAsync(ct);
                var assignmentHistories =
                    await _db
                        .RentalAssetAssignmentHistories
                        .IgnoreQueryFilters()
                        .Where(current =>
                            current.BillingProfileId ==
                            entityId)
                        .ToListAsync(ct);
                target = profile;
                AddServerPurgeGuardEntity(
                    entities,
                    profile,
                    nameof(LocalRentalBillingProfile),
                    "RentalBillingProfile");
                AddServerPurgeGuardEntities(
                    entities,
                    linkedAssets,
                    nameof(LocalRentalAsset),
                    "RentalAsset");
                AddServerPurgeGuardEntities(
                    entities,
                    logs,
                    nameof(LocalRentalBillingLog),
                    "RentalBillingLog");
                AddServerPurgeGuardEntities(
                    entities,
                    assignmentHistories,
                    nameof(
                        LocalRentalAssetAssignmentHistory),
                    "RentalAssetAssignmentHistory");
                if (profile is not null)
                {
                    businessDatabaseName =
                        ResolveRecycleBinBusinessDatabaseName(
                            profile.TenantCode,
                            profile.OfficeCode,
                            profile.ResponsibleOfficeCode,
                            profile.ManagementCompanyCode);
                }

                break;
            }
            case RecycleBinEntityKind.RentalAsset:
            {
                var asset = await _db.RentalAssets
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        current =>
                            current.Id == entityId,
                        ct);
                var profiles =
                    await GetBillingProfilesContainingAssetIdAsync(
                        entityId,
                        ct);
                var assignmentHistories =
                    await _db
                        .RentalAssetAssignmentHistories
                        .IgnoreQueryFilters()
                        .Where(current =>
                            current.AssetId ==
                            entityId)
                        .ToListAsync(ct);
                target = asset;
                AddServerPurgeGuardEntity(
                    entities,
                    asset,
                    nameof(LocalRentalAsset),
                    "RentalAsset");
                AddServerPurgeGuardEntities(
                    entities,
                    profiles,
                    nameof(LocalRentalBillingProfile),
                    "RentalBillingProfile");
                AddServerPurgeGuardEntities(
                    entities,
                    assignmentHistories,
                    nameof(
                        LocalRentalAssetAssignmentHistory),
                    "RentalAssetAssignmentHistory");
                if (asset is not null)
                {
                    businessDatabaseName =
                        ResolveRecycleBinBusinessDatabaseName(
                            asset.TenantCode,
                            asset.OfficeCode,
                            asset.ResponsibleOfficeCode,
                            asset.ManagementCompanyCode);
                }

                break;
            }
            case RecycleBinEntityKind.RentalBillingLog:
            {
                var log = await _db.RentalBillingLogs
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        current =>
                            current.Id == entityId,
                        ct);
                target = log;
                AddServerPurgeGuardEntity(
                    entities,
                    log,
                    nameof(LocalRentalBillingLog),
                    "RentalBillingLog");
                if (log is not null)
                {
                    businessDatabaseName =
                        ResolveRecycleBinBusinessDatabaseName(
                            log.TenantCode,
                            log.OfficeCode,
                            log.ResponsibleOfficeCode);
                    await AddServerPurgeGuardRentalProfileAsync(
                        entities,
                        log.BillingProfileId,
                        ct);
                }

                break;
            }
            default:
            {
                target = await LoadSimpleServerPurgeTargetAsync(
                    kind,
                    entityId,
                    ct);
                AddServerPurgeGuardEntity(
                    entities,
                    target,
                    GetRecycleBinOutboxEntityNames(kind)
                        .ToArray());
                if (target is LocalCompanyProfile profile)
                {
                    businessDatabaseName =
                        ResolveRecycleBinBusinessDatabaseName(
                            tenantCode: null,
                            profile.OfficeCode);
                }

                break;
            }
        }

        return new ServerPurgeGuardPlan(
            target,
            businessDatabaseName,
            entities,
            scopeEvidenceBusinessDatabaseNames);
    }

    private async Task<LocalSyncEntity?>
        LoadSimpleServerPurgeTargetAsync(
            RecycleBinEntityKind kind,
            Guid entityId,
            CancellationToken ct)
        => kind switch
        {
            RecycleBinEntityKind.CompanyProfile =>
                await FindSyncEntityAsync(
                    _db.CompanyProfiles,
                    entityId,
                    ct),
            RecycleBinEntityKind.CustomerCategory =>
                await FindSyncEntityAsync(
                    _db.CustomerCategories,
                    entityId,
                    ct),
            RecycleBinEntityKind.PriceGradeOption =>
                await FindSyncEntityAsync(
                    _db.PriceGradeOptions,
                    entityId,
                    ct),
            RecycleBinEntityKind.TradeTypeOption =>
                await FindSyncEntityAsync(
                    _db.TradeTypeOptions,
                    entityId,
                    ct),
            RecycleBinEntityKind.ItemCategoryOption =>
                await FindSyncEntityAsync(
                    _db.ItemCategoryOptions,
                    entityId,
                    ct),
            RecycleBinEntityKind.InventoryTransfer =>
                await FindSyncEntityAsync(
                    _db.InventoryTransfers,
                    entityId,
                    ct),
            RecycleBinEntityKind.RentalManagementCompany =>
                await FindSyncEntityAsync(
                    _db.RentalManagementCompanies,
                    entityId,
                    ct),
            _ => null
        };

    private async Task AddServerPurgeGuardRentalProfileAsync(
        ICollection<ServerPurgeGuardEntity> entities,
        Guid? profileId,
        CancellationToken ct)
    {
        if (!profileId.HasValue ||
            profileId.Value == Guid.Empty)
        {
            return;
        }

        var profile =
            await _db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    current =>
                        current.Id == profileId.Value,
                    ct);
        AddServerPurgeGuardEntity(
            entities,
            profile,
            nameof(LocalRentalBillingProfile),
            "RentalBillingProfile");
    }

    private static string?
        ResolvePaymentBusinessDatabaseName(
            LocalInvoice? invoice,
            LocalTransaction? sameIdTransaction)
    {
        if (invoice is not null)
        {
            return ResolveRecycleBinBusinessDatabaseName(
                invoice.TenantCode,
                invoice.OfficeCode,
                invoice.ResponsibleOfficeCode);
        }

        return sameIdTransaction is null
            ? null
            : ResolveRecycleBinBusinessDatabaseName(
                sameIdTransaction.TenantCode,
                sameIdTransaction.OfficeCode,
                sameIdTransaction.ResponsibleOfficeCode);
    }

    private static void AddServerPurgeScopeEvidence(
        ICollection<string> destination,
        LocalInvoice? invoice)
    {
        if (invoice is null)
            return;

        destination.Add(
            ResolveRecycleBinBusinessDatabaseName(
                invoice.TenantCode,
                invoice.OfficeCode,
                invoice.ResponsibleOfficeCode));
    }

    private static string?
        ResolvePaymentOrTransactionBusinessDatabaseName(
            LocalTransaction? transaction,
            LocalInvoice? invoice)
    {
        if (transaction is not null)
        {
            return ResolveRecycleBinBusinessDatabaseName(
                transaction.TenantCode,
                transaction.OfficeCode,
                transaction.ResponsibleOfficeCode);
        }

        return invoice is null
            ? null
            : ResolveRecycleBinBusinessDatabaseName(
                invoice.TenantCode,
                invoice.OfficeCode,
                invoice.ResponsibleOfficeCode);
    }

    private static bool
        IsServerPurgeGuardEntityFromAnotherBusinessDatabase(
            LocalSyncEntity entity,
            string expectedBusinessDatabaseName)
    {
        if (entity is LocalInventoryTransfer transfer)
        {
            var sourceBusinessDatabaseName =
                ResolveRecycleBinBusinessDatabaseName(
                    tenantCode: null,
                    ResolveOfficeCodeFromWarehouseCode(
                        transfer.FromWarehouseCode));
            var targetBusinessDatabaseName =
                ResolveRecycleBinBusinessDatabaseName(
                    tenantCode: null,
                    ResolveOfficeCodeFromWarehouseCode(
                        transfer.ToWarehouseCode));
            return !string.Equals(
                       sourceBusinessDatabaseName,
                       expectedBusinessDatabaseName,
                       StringComparison.OrdinalIgnoreCase) ||
                   !string.Equals(
                       targetBusinessDatabaseName,
                       expectedBusinessDatabaseName,
                       StringComparison.OrdinalIgnoreCase);
        }

        var entityBusinessDatabaseName =
            entity switch
            {
                LocalCompanyProfile current =>
                    ResolveRecycleBinBusinessDatabaseName(
                        tenantCode: null,
                        current.OfficeCode),
                LocalCustomer current =>
                    ResolveRecycleBinBusinessDatabaseName(
                        current.TenantCode,
                        current.OfficeCode,
                        current.ResponsibleOfficeCode),
                LocalItem current =>
                    ResolveRecycleBinBusinessDatabaseName(
                        current.TenantCode,
                        current.OfficeCode),
                LocalInvoice current =>
                    ResolveRecycleBinBusinessDatabaseName(
                        current.TenantCode,
                        current.OfficeCode,
                        current.ResponsibleOfficeCode),
                LocalTransaction current =>
                    ResolveRecycleBinBusinessDatabaseName(
                        current.TenantCode,
                        current.OfficeCode,
                        current.ResponsibleOfficeCode),
                LocalRentalBillingProfile current =>
                    ResolveRecycleBinBusinessDatabaseName(
                        current.TenantCode,
                        current.OfficeCode,
                        current.ResponsibleOfficeCode,
                        current.ManagementCompanyCode),
                LocalRentalAsset current =>
                    ResolveRecycleBinBusinessDatabaseName(
                        current.TenantCode,
                        current.OfficeCode,
                        current.ResponsibleOfficeCode,
                        current.ManagementCompanyCode),
                LocalRentalAssetAssignmentHistory current =>
                    ResolveRecycleBinBusinessDatabaseName(
                        current.TenantCode,
                        officeCode: null,
                        current.ResponsibleOfficeCode),
                LocalRentalBillingLog current =>
                    ResolveRecycleBinBusinessDatabaseName(
                        current.TenantCode,
                        current.OfficeCode,
                        current.ResponsibleOfficeCode),
                LocalRentalManagementCompany current =>
                    ResolveRecycleBinBusinessDatabaseName(
                        tenantCode: null,
                        current.Code,
                        current.Code,
                        current.Code),
                _ => null
            };
        return entityBusinessDatabaseName is not null &&
               !string.Equals(
                   entityBusinessDatabaseName,
                   expectedBusinessDatabaseName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void AddServerPurgeGuardEntity<T>(
        ICollection<ServerPurgeGuardEntity> destination,
        T? entity,
        params string[] outboxEntityNames)
        where T : LocalSyncEntity
    {
        if (entity is null)
            return;

        destination.Add(
            new ServerPurgeGuardEntity(
                entity,
                outboxEntityNames));
    }

    private static void AddServerPurgeGuardEntities<T>(
        ICollection<ServerPurgeGuardEntity> destination,
        IEnumerable<T> entities,
        params string[] outboxEntityNames)
        where T : LocalSyncEntity
    {
        foreach (var entity in entities)
        {
            AddServerPurgeGuardEntity(
                destination,
                entity,
                outboxEntityNames);
        }
    }

    private sealed record ServerPurgeGuardPlan(
        LocalSyncEntity? Target,
        string? BusinessDatabaseName,
        IReadOnlyList<ServerPurgeGuardEntity> Entities,
        IReadOnlyList<string>
            ScopeEvidenceBusinessDatabaseNames);

    private sealed record ServerPurgeGuardEntity(
        LocalSyncEntity Entity,
        IReadOnlyList<string> OutboxEntityNames);
}
