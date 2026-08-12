using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public enum ItemDuplicateMergeStatus
{
    Success,
    Invalid,
    Forbidden,
    Conflict
}

public sealed record ItemDuplicateMergeOutcome(
    ItemDuplicateMergeStatus Status,
    ItemDuplicateMergeResultDto? Result = null,
    ItemDuplicateMergePreviewDto? Preview = null,
    string Error = "",
    string Message = "");

/// <summary>
/// Server-authoritative, fail-closed duplicate item merge. This deliberately does not reuse
/// startup cleanup code: every decision is recomputed after the serialized transaction starts.
/// </summary>
public sealed class ItemDuplicateMergeService
{
    internal const string ReceiptEntityName = "ItemDuplicateMergeCommand";
    private const string CatalogItemIdPropertyName = "CatalogItemId";
    private const string DisplayItemNamePropertyName = "DisplayItemName";
    private const string SpecificationPropertyName = "Specification";
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppDbContext _db;
    private readonly OfficeScopeService _scope;
    private readonly ICurrentUserContext _user;

    internal Func<CancellationToken, Task>? TestOnlyBeforeSaveAsync { get; set; }
    internal Func<CancellationToken, Task>? TestOnlyAfterSaveBeforeCommitAsync { get; set; }

    public ItemDuplicateMergeService(AppDbContext db, OfficeScopeService scope, ICurrentUserContext user)
    {
        _db = db;
        _scope = scope;
        _user = user;
    }

    internal async Task<ItemDuplicateMergePreviewDto> PreviewAsync(
        ItemDuplicateMergePreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.CandidateItemIds);
        if (ids.Count < 2)
            return BlockedPreview(request.CanonicalItemId, "At least two distinct candidate item ids are required.");

        return await BuildPreviewAsync(ids, request.CanonicalItemId, cancellationToken);
    }

    public async Task<ItemDuplicateMergeOutcome> PreviewOutcomeAsync(
        ItemDuplicateMergePreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.CandidateItemIds);
        if (ids.Count < 2)
            return Invalid("invalid_candidates", "At least two distinct candidate item ids are required.");
        if (!await HasCandidateWriteAccessAsync(ids, allowDeleted: false, cancellationToken))
            return new ItemDuplicateMergeOutcome(ItemDuplicateMergeStatus.Forbidden);

        var preview = await BuildPreviewAsync(ids, request.CanonicalItemId, cancellationToken);
        return new ItemDuplicateMergeOutcome(ItemDuplicateMergeStatus.Success, Preview: preview);
    }

    public async Task<ItemDuplicateMergeOutcome> MergeAsync(
        ItemDuplicateMergeRequestDto request,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.CandidateItemIds);
        if (ids.Count < 2 || request.CanonicalItemId == Guid.Empty || !ids.Contains(request.CanonicalItemId))
            return Invalid("invalid_candidates", "Candidate ids must contain at least two items and the explicit canonical item.");

        var mutationId = ProcessedSyncMutationRecorder.NormalizeMutationId(request.MutationId);
        if (string.IsNullOrWhiteSpace(mutationId))
            return Invalid("mutation_id_required", "MutationId is required.");
        if (ItemWarehouseStockMutationReceipt.IsReservedMutationId(mutationId))
            return Invalid("mutation_id_reserved", "MutationId uses a server-reserved receipt namespace.");
        if (string.IsNullOrWhiteSpace(request.ExpectedServerSnapshotToken))
            return Invalid("snapshot_token_required", "ExpectedServerSnapshotToken is required.");

        await using var transaction = await InventoryMutationTransactionScope.BeginAsync(
            _db,
            serializeInventoryMutations: true,
            cancellationToken);

        if (!await HasCandidateWriteAccessAsync(ids, allowDeleted: true, cancellationToken))
            return new ItemDuplicateMergeOutcome(ItemDuplicateMergeStatus.Forbidden);

        var payloadHash = ComputePayloadHash(ids, request.CanonicalItemId, request.ExpectedServerSnapshotToken);
        var existingReceipt = await _db.ProcessedSyncMutations
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.MutationId.Trim().ToLower() == mutationId, cancellationToken);
        if (existingReceipt is not null)
        {
            if (!string.Equals(existingReceipt.EntityName, ReceiptEntityName, StringComparison.Ordinal) ||
                !string.Equals(existingReceipt.EntityId, request.CanonicalItemId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                !FixedTimeEquals(existingReceipt.PayloadHash, payloadHash))
            {
                return Conflict("mutation_id_conflict", "MutationId was already used with a different duplicate-merge payload.");
            }

            return new ItemDuplicateMergeOutcome(
                ItemDuplicateMergeStatus.Success,
                new ItemDuplicateMergeResultDto
                {
                    CanonicalItemId = request.CanonicalItemId,
                    TombstonedItemIds = ids.Where(id => id != request.CanonicalItemId).ToList(),
                    ServerSnapshotToken = request.ExpectedServerSnapshotToken,
                    IsReplay = true
                });
        }

        var preview = await BuildPreviewAsync(ids, request.CanonicalItemId, cancellationToken);
        if (!FixedTimeEquals(preview.ServerSnapshotToken, request.ExpectedServerSnapshotToken))
            return new ItemDuplicateMergeOutcome(ItemDuplicateMergeStatus.Conflict, Preview: preview,
                Error: "stale_snapshot", Message: "The duplicate item group changed after preview.");
        if (!preview.CanMerge)
            return new ItemDuplicateMergeOutcome(
                preview.BlockingReasons.Any(reason =>
                    reason.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                    reason.Contains("scope", StringComparison.OrdinalIgnoreCase))
                    ? ItemDuplicateMergeStatus.Forbidden
                    : ItemDuplicateMergeStatus.Conflict,
                Preview: preview,
                Error: "merge_blocked", Message: string.Join("; ", preview.BlockingReasons));

        var items = await _db.Items.IgnoreQueryFilters()
            .Where(item => ids.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var canonical = items.Single(item => item.Id == request.CanonicalItemId);
        var duplicateIds = ids.Where(id => id != canonical.Id).ToHashSet();
        var now = DateTime.UtcNow;
        foreach (var duplicate in items.Where(item => duplicateIds.Contains(item.Id)))
            MergeItemValues(canonical, duplicate);

        var invoiceLines = await _db.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.ItemId.HasValue && duplicateIds.Contains(line.ItemId.Value))
            .ToListAsync(cancellationToken);
        var invoiceIds = invoiceLines.Select(line => line.InvoiceId).Distinct().ToList();
        var invoices = await _db.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoiceIds.Contains(invoice.Id))
            .ToListAsync(cancellationToken);
        if (invoices.Count != invoiceIds.Count)
            return Conflict("missing_parent", "An invoice line has no parent invoice.");
        foreach (var line in invoiceLines)
        {
            line.ItemId = canonical.Id;
            line.ItemNameOriginal = canonical.NameOriginal;
            line.SpecificationOriginal = canonical.SpecificationOriginal;
        }
        foreach (var invoice in invoices)
            invoice.UpdatedAtUtc = now;

        var assets = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.ItemId.HasValue && duplicateIds.Contains(asset.ItemId.Value))
            .ToListAsync(cancellationToken);
        var assetIds = assets.Select(asset => asset.Id).ToList();
        var histories = await _db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
            .Where(history => assetIds.Contains(history.AssetId))
            .ToListAsync(cancellationToken);
        foreach (var asset in assets)
        {
            asset.ItemId = canonical.Id;
            asset.ItemName = canonical.NameOriginal;
            if (string.IsNullOrWhiteSpace(asset.ItemCategoryName))
                asset.ItemCategoryName = canonical.CategoryName;
        }
        foreach (var history in histories)
            history.ItemName = canonical.NameOriginal;

        var profiles = await LoadProfilesReferencingAsync(ids, failOnMalformed: true, cancellationToken);
        if (profiles is null)
            return Conflict("malformed_template", "A rental billing template is malformed or has an unsupported CatalogItemId value.");
        var updatedProfileCount = 0;
        foreach (var profile in profiles)
        {
            if (!TryRewriteTemplate(profile.BillingTemplateJson, duplicateIds, canonical, out var rewritten, out var changed))
                return Conflict("malformed_template", "A rental billing template is malformed or has an unsupported CatalogItemId value.");
            if (!changed)
                continue;
            profile.BillingTemplateJson = rewritten;
            updatedProfileCount++;
        }

        var transferLines = await _db.InventoryTransferLines.IgnoreQueryFilters()
            .Where(line => line.ItemId.HasValue && duplicateIds.Contains(line.ItemId.Value))
            .ToListAsync(cancellationToken);
        var transferIds = transferLines.Select(line => line.TransferId).Distinct().ToList();
        var transfers = await _db.InventoryTransfers.IgnoreQueryFilters()
            .Where(transfer => transferIds.Contains(transfer.Id))
            .ToListAsync(cancellationToken);
        if (transfers.Count != transferIds.Count)
            return Conflict("missing_parent", "An inventory transfer line has no parent transfer.");
        foreach (var line in transferLines)
        {
            line.ItemId = canonical.Id;
            line.ItemNameOriginal = canonical.NameOriginal;
            line.SpecificationOriginal = canonical.SpecificationOriginal;
        }
        foreach (var transfer in transfers)
            transfer.UpdatedAtUtc = now;

        var mergedWarehouseStockRowCount = await RemapWarehouseStocksAsync(
            canonical,
            duplicateIds,
            now,
            cancellationToken);

        canonical.NameMatchKey = MatchKeyNormalizer.Normalize(canonical.NameOriginal);
        canonical.SpecificationMatchKey = MatchKeyNormalizer.Normalize(canonical.SpecificationOriginal);
        canonical.UpdatedAtUtc = now;
        foreach (var duplicate in items.Where(item => duplicateIds.Contains(item.Id)))
        {
            duplicate.IsDeleted = true;
            duplicate.UpdatedAtUtc = now;
        }

        _db.ProcessedSyncMutations.Add(new ProcessedSyncMutation
        {
            MutationId = mutationId,
            DeviceId = ProcessedSyncMutationRecorder.DirectApiDeviceId,
            EntityName = ReceiptEntityName,
            EntityId = canonical.Id.ToString("D"),
            ExpectedRevision = 0,
            PayloadHash = payloadHash,
            ProcessedAtUtc = now
        });

        if (TestOnlyBeforeSaveAsync is not null)
            await TestOnlyBeforeSaveAsync(cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await new InventoryLedgerService(_db).RebuildAsync(cancellationToken);
        if (TestOnlyAfterSaveBeforeCommitAsync is not null)
            await TestOnlyAfterSaveBeforeCommitAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ItemDuplicateMergeOutcome(
            ItemDuplicateMergeStatus.Success,
            new ItemDuplicateMergeResultDto
            {
                CanonicalItemId = canonical.Id,
                TombstonedItemIds = duplicateIds.Order().ToList(),
                ServerSnapshotToken = preview.ServerSnapshotToken,
                MovedInvoiceLineCount = invoiceLines.Count,
                MovedRentalAssetCount = assets.Count,
                UpdatedRentalBillingProfileCount = updatedProfileCount,
                MovedInventoryTransferLineCount = transferLines.Count,
                MergedWarehouseStockRowCount = mergedWarehouseStockRowCount
            });
    }

    private async Task<ItemDuplicateMergePreviewDto> BuildPreviewAsync(
        IReadOnlyList<Guid> requestedIds,
        Guid canonicalId,
        CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        void Block(string reason)
        {
            if (!reasons.Contains(reason, StringComparer.Ordinal)) reasons.Add(reason);
        }

        var items = await _db.Items.IgnoreQueryFilters().AsNoTracking()
            .Where(item => requestedIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        if (items.Count != requestedIds.Count || items.Any(item => item.IsDeleted))
            Block("One or more candidates are missing or deleted.");
        if (!requestedIds.Contains(canonicalId))
            Block("The explicit canonical item is outside the candidate set.");

        if (items.Count > 0)
        {
            var first = items[0];
            var tenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(first.TenantCode, first.OfficeCode);
            var office = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(first.OfficeCode, OfficeCodeCatalog.Shared);
            var nameKey = (first.NameOriginal ?? string.Empty).Trim();
            var specificationKey = (first.SpecificationOriginal ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(nameKey) || items.Any(item =>
                    !string.Equals(TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(item.TenantCode, item.OfficeCode), tenant, StringComparison.Ordinal) ||
                    !string.Equals(OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(item.OfficeCode, OfficeCodeCatalog.Shared), office, StringComparison.Ordinal) ||
                    !string.Equals((item.NameOriginal ?? string.Empty).Trim(), nameKey, StringComparison.Ordinal) ||
                    !string.Equals((item.SpecificationOriginal ?? string.Empty).Trim(), specificationKey, StringComparison.Ordinal)))
                Block("Candidates are not one exact tenant/office/name/specification group.");
            else
            {
                var completeGroupIds = await _db.Items.IgnoreQueryFilters().AsNoTracking()
                    .Where(item => !item.IsDeleted &&
                                   item.NameOriginal.Trim() == nameKey &&
                                   item.SpecificationOriginal.Trim() == specificationKey)
                    .Select(item => new { item.Id, item.TenantCode, item.OfficeCode, item.NameOriginal, item.SpecificationOriginal })
                    .ToListAsync(cancellationToken);
                var exactIds = completeGroupIds.Where(item =>
                        string.Equals(TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(item.TenantCode, item.OfficeCode), tenant, StringComparison.Ordinal) &&
                        string.Equals(OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(item.OfficeCode, OfficeCodeCatalog.Shared), office, StringComparison.Ordinal) &&
                        string.Equals((item.NameOriginal ?? string.Empty).Trim(), nameKey, StringComparison.Ordinal) &&
                        string.Equals((item.SpecificationOriginal ?? string.Empty).Trim(), specificationKey, StringComparison.Ordinal))
                    .Select(item => item.Id).Order().ToList();
                if (!exactIds.SequenceEqual(requestedIds.Order()))
                    Block("The candidate set omits or adds an item from the exact duplicate group.");
            }

            foreach (var item in items)
            {
                if (!_scope.CanWriteOfficeForItems(item.OfficeCode, item.TenantCode))
                    Block("Item write scope is missing for one or more candidates.");
            }
            AddSemanticBlockers(items, Block);
        }

        var ids = items.Select(item => item.Id).ToList();
        var now = DateTime.UtcNow;
        var activeItemEditors = await _db.ActiveEditSessions.AsNoTracking()
            .Where(session => session.ExpiresAtUtc > now)
            .Select(session => new { session.EntityType, session.EntityId })
            .ToListAsync(cancellationToken);
        if (activeItemEditors.Any(session =>
                (string.Equals(session.EntityType, "Item", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(session.EntityType, "ItemDto", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(session.EntityType, "LocalItem", StringComparison.OrdinalIgnoreCase)) &&
                Guid.TryParse(session.EntityId, out var editedItemId) && ids.Contains(editedItemId)))
            Block("An unexpired active editor is open for a candidate item.");

        var invoiceLines = await _db.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
            .Where(line => line.ItemId.HasValue && ids.Contains(line.ItemId.Value))
            .ToListAsync(cancellationToken);
        var invoiceIds = invoiceLines.Select(line => line.InvoiceId).Distinct().ToList();
        var invoices = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(invoice => invoiceIds.Contains(invoice.Id)).ToListAsync(cancellationToken);
        if (invoices.Count != invoiceIds.Count) Block("An invoice line has no parent invoice.");
        if (invoiceLines.Count > 0 && !_user.HasPermission(PermissionNames.InvoiceEdit) && !_scope.HasAdministrativeWriteAccess)
            Block("InvoiceEdit permission is required for referenced invoice lines.");
        if (invoices.Any(invoice => !_scope.CanWriteOfficeForInvoices(invoice.ResponsibleOfficeCode, invoice.TenantCode, invoice.OfficeCode)))
            Block("Invoice write scope is missing for a referenced invoice.");

        var assets = await _db.RentalAssets.IgnoreQueryFilters().AsNoTracking()
            .Where(asset => asset.ItemId.HasValue && ids.Contains(asset.ItemId.Value)).ToListAsync(cancellationToken);
        var assetIds = assets.Select(asset => asset.Id).ToList();
        var histories = await _db.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking()
            .Where(history => assetIds.Contains(history.AssetId)).ToListAsync(cancellationToken);
        if (assets.Count > 0 && !_scope.CanEditRentalAssets()) Block("RentalAssetEdit permission is required for referenced assets.");
        if (assets.Any(asset => !_scope.CanWriteOfficeForRentals(asset.ResponsibleOfficeCode, asset.TenantCode, asset.OfficeCode)))
            Block("Rental asset write scope is missing.");
        if (histories.Any(history => !_scope.CanWriteOfficeForRentals(history.ResponsibleOfficeCode, history.TenantCode, history.OfficeCode)))
            Block("Rental assignment history write scope is missing.");

        var profiles = await LoadProfilesReferencingAsync(ids, failOnMalformed: true, cancellationToken);
        if (profiles is null)
        {
            Block("A rental billing template is malformed or unsupported.");
            profiles = [];
        }
        else if (items.FirstOrDefault(item => item.Id == canonicalId) is { } previewCanonical)
        {
            var previewDuplicateIds = ids
                .Where(id => id != canonicalId)
                .ToHashSet();
            if (profiles.Any(profile =>
                    !TryRewriteTemplate(
                        profile.BillingTemplateJson,
                        previewDuplicateIds,
                        previewCanonical,
                        out _,
                        out _)))
            {
                Block("A rental billing template contains a candidate item reference outside a supported CatalogItemId.");
            }
        }
        if (profiles.Count > 0 && !_scope.CanEditRentalProfiles()) Block("RentalProfileEdit permission is required for referenced billing templates.");
        if (profiles.Any(profile => !_scope.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode)))
            Block("Rental billing profile write scope is missing.");

        var transferLines = await _db.InventoryTransferLines.IgnoreQueryFilters().AsNoTracking()
            .Where(line => line.ItemId.HasValue && ids.Contains(line.ItemId.Value)).ToListAsync(cancellationToken);
        var transferIds = transferLines.Select(line => line.TransferId).Distinct().ToList();
        var transfers = await _db.InventoryTransfers.IgnoreQueryFilters().AsNoTracking()
            .Where(transfer => transferIds.Contains(transfer.Id)).ToListAsync(cancellationToken);
        if (transfers.Count != transferIds.Count) Block("An inventory transfer line has no parent transfer.");
        if (transferLines.Count > 0 && !_scope.CanEditDeliveries()) Block("DeliveryEdit permission is required for transfer references.");
        if (transfers.Any(transfer =>
                !_scope.CanWriteOfficeForDeliveries(transfer.SourceOfficeCode, transfer.TenantCode) ||
                !_scope.CanWriteOfficeForDeliveries(transfer.TargetOfficeCode, transfer.TenantCode)))
            Block("Write scope for both transfer offices is required.");

        var stocks = await _db.ItemWarehouseStocks.IgnoreQueryFilters().AsNoTracking().Where(stock => ids.Contains(stock.ItemId)).ToListAsync(cancellationToken);
        var grades = await _db.ItemPriceGrades.IgnoreQueryFilters().AsNoTracking()
            .Where(grade => ids.Contains(grade.ItemId)).ToListAsync(cancellationToken);
        var ledgers = await _db.InventoryLedgerEntries.AsNoTracking().Where(entry => ids.Contains(entry.ItemId)).ToListAsync(cancellationToken);
        if (items.Any(item => item.CurrentStock != 0m)) Block("Current stock must be zero for every candidate.");
        if (stocks.Any(stock => stock.Quantity != 0m))
            Block("Every warehouse stock row must have zero quantity before merge.");
        if (stocks.Any(stock =>
                items.FirstOrDefault(item => item.Id == stock.ItemId) is not { } item ||
                !_scope.CanWriteWarehouse(stock.WarehouseCode, item.OfficeCode)))
            Block("Warehouse stock write scope is missing.");
        if (grades.Count > 0) Block("Item price grades must be resolved before merge.");

        var candidates = items.Select(item => new ItemDuplicateMergeCandidateDto
        {
            ItemId = item.Id,
            TenantCode = item.TenantCode,
            OfficeCode = item.OfficeCode,
            Revision = item.Revision,
            NameOriginal = item.NameOriginal,
            SpecificationOriginal = item.SpecificationOriginal,
            CurrentStock = item.CurrentStock,
            WarehouseStock = stocks.Where(stock => stock.ItemId == item.Id).Sum(stock => stock.Quantity),
            InvoiceLineCount = invoiceLines.Count(line => line.ItemId == item.Id),
            RentalAssetCount = assets.Count(asset => asset.ItemId == item.Id),
            RentalAssignmentHistoryCount = assets
                .Where(asset => asset.ItemId == item.Id)
                .Sum(asset => histories.Count(history => history.AssetId == asset.Id)),
            RentalBillingTemplateCount = profiles.Count(profile => TemplateContains(profile.BillingTemplateJson, item.Id)),
            InventoryTransferLineCount = transferLines.Count(line => line.ItemId == item.Id),
            ItemWarehouseStockRowCount = stocks.Count(stock => stock.ItemId == item.Id),
            ItemPriceGradeCount = grades.Count(grade => grade.ItemId == item.Id),
            InventoryLedgerEntryCount = ledgers.Count(entry => entry.ItemId == item.Id)
        }).ToList();
        foreach (var candidate in candidates)
            candidate.TotalReferenceCount = candidate.InvoiceLineCount + candidate.RentalAssetCount + candidate.RentalAssignmentHistoryCount +
                                            candidate.RentalBillingTemplateCount + candidate.InventoryTransferLineCount +
                                            candidate.ItemWarehouseStockRowCount + candidate.ItemPriceGradeCount +
                                            candidate.InventoryLedgerEntryCount;

        var token = BuildServerSnapshotToken(items, invoiceLines, invoices, assets, histories, profiles, transferLines, transfers, stocks, grades, ledgers);
        return new ItemDuplicateMergePreviewDto
        {
            Candidates = candidates,
            CanonicalItemId = canonicalId,
            ServerSnapshotToken = token,
            CanMerge = reasons.Count == 0,
            BlockingReasons = reasons
        };
    }

    private async Task<bool> HasCandidateWriteAccessAsync(
        IReadOnlyCollection<Guid> candidateIds,
        bool allowDeleted,
        CancellationToken cancellationToken)
    {
        if (!_scope.CanEditItems())
            return false;

        var items = await _db.Items.IgnoreQueryFilters().AsNoTracking()
            .Where(item => candidateIds.Contains(item.Id))
            .Select(item => new { item.Id, item.TenantCode, item.OfficeCode, item.IsDeleted })
            .ToListAsync(cancellationToken);
        return items.Count == candidateIds.Count &&
               items.All(item => (allowDeleted || !item.IsDeleted) &&
                                 _scope.CanWriteOfficeForItems(item.OfficeCode, item.TenantCode));
    }

    private async Task<List<RentalBillingProfile>?> LoadProfilesReferencingAsync(
        IReadOnlyCollection<Guid> itemIds,
        bool failOnMalformed,
        CancellationToken cancellationToken)
    {
        var profiles = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        var result = new List<RentalBillingProfile>();
        foreach (var profile in profiles)
        {
            if (!TryReadTemplateIds(profile.BillingTemplateJson, out var referencedIds))
            {
                if (failOnMalformed && ContainsCandidateGuid(profile.BillingTemplateJson, itemIds))
                    return null;
                continue;
            }
            if (referencedIds.Overlaps(itemIds) ||
                ContainsCandidateGuid(profile.BillingTemplateJson, itemIds))
            {
                result.Add(profile);
            }
        }
        return result;
    }

    private static bool TryReadTemplateIds(string? json, out HashSet<Guid> ids)
    {
        ids = [];
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var root = JsonNode.Parse(json);
            if (root is not JsonArray array) return false;
            foreach (var node in array)
            {
                if (node is not JsonObject obj) return false;
                if (!TryFindUniquePropertyIgnoreCase(obj, CatalogItemIdPropertyName, out var catalogItemProperty) ||
                    !TryFindUniquePropertyIgnoreCase(obj, DisplayItemNamePropertyName, out _) ||
                    !TryFindUniquePropertyIgnoreCase(obj, SpecificationPropertyName, out _))
                {
                    return false;
                }
                if (catalogItemProperty.Key is null || catalogItemProperty.Value is null) continue;
                if (catalogItemProperty.Value is not JsonValue idValue ||
                    !idValue.TryGetValue<string>(out var rawId) ||
                    !Guid.TryParse(rawId, out var id))
                {
                    return false;
                }
                if (id != Guid.Empty) ids.Add(id);
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool ContainsCandidateGuid(string? json, IReadOnlyCollection<Guid> itemIds)
        => JsonGuidTokenSafety.ContainsExactGuidToken(json, itemIds);

    private static bool TryRewriteTemplate(
        string json,
        IReadOnlySet<Guid> duplicateIds,
        Item canonical,
        out string rewritten,
        out bool changed)
    {
        rewritten = json;
        changed = false;
        try
        {
            if (JsonNode.Parse(json) is not JsonArray array) return false;
            foreach (var node in array)
            {
                if (node is not JsonObject obj) return false;
                if (!TryFindUniquePropertyIgnoreCase(obj, CatalogItemIdPropertyName, out var catalogItemProperty) ||
                    !TryFindUniquePropertyIgnoreCase(obj, DisplayItemNamePropertyName, out _) ||
                    !TryFindUniquePropertyIgnoreCase(obj, SpecificationPropertyName, out _))
                {
                    return false;
                }
                if (catalogItemProperty.Key is null || catalogItemProperty.Value is null) continue;
                if (catalogItemProperty.Value is not JsonValue idValue ||
                    !idValue.TryGetValue<string>(out var rawId) ||
                    !Guid.TryParse(rawId, out var id))
                {
                    return false;
                }
                if (!duplicateIds.Contains(id)) continue;
                obj[catalogItemProperty.Key] = canonical.Id.ToString("D");
                SetExistingPropertyIgnoreCase(
                    obj,
                    DisplayItemNamePropertyName,
                    canonical.NameOriginal);
                SetExistingPropertyIgnoreCase(
                    obj,
                    SpecificationPropertyName,
                    canonical.SpecificationOriginal);
                changed = true;
            }
            if (changed) rewritten = array.ToJsonString();
            return !ContainsCandidateGuid(rewritten, duplicateIds);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryFindUniquePropertyIgnoreCase(
        JsonObject obj,
        string propertyName,
        out KeyValuePair<string, JsonNode?> property)
    {
        property = default;
        var matchCount = 0;
        foreach (var candidate in obj)
        {
            if (!string.Equals(candidate.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            property = candidate;
            matchCount++;
            if (matchCount > 1)
                return false;
        }

        return true;
    }

    private static void SetExistingPropertyIgnoreCase(JsonObject obj, string propertyName, string value)
    {
        if (TryFindUniquePropertyIgnoreCase(obj, propertyName, out var property) && property.Key is not null)
            obj[property.Key] = value;
    }

    private static bool TemplateContains(string? json, Guid itemId)
        => TryReadTemplateIds(json, out var ids) && ids.Contains(itemId);

    private async Task<int> RemapWarehouseStocksAsync(
        Item canonical,
        IReadOnlySet<Guid> duplicateIds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var rows = await _db.ItemWarehouseStocks.IgnoreQueryFilters()
            .Where(stock => stock.ItemId == canonical.Id || duplicateIds.Contains(stock.ItemId))
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return 0;

        var changed = 0;
        var canonicalRows = new List<ItemWarehouseStock>();
        foreach (var warehouseGroup in rows.GroupBy(
                     stock => (stock.WarehouseCode ?? string.Empty).Trim(),
                     StringComparer.OrdinalIgnoreCase))
        {
            var groupRows = warehouseGroup.ToList();
            var canonicalStock = groupRows.FirstOrDefault(stock => stock.ItemId == canonical.Id);
            if (canonicalStock is null)
            {
                canonicalStock = new ItemWarehouseStock
                {
                    ItemId = canonical.Id,
                    WarehouseCode = groupRows[0].WarehouseCode ?? string.Empty,
                    UpdatedAtUtc = now
                };
                _db.ItemWarehouseStocks.Add(canonicalStock);
            }

            canonicalStock.Quantity = groupRows.Sum(stock => stock.Quantity);
            canonicalStock.UpdatedAtUtc = now;
            canonicalRows.Add(canonicalStock);
            foreach (var redundant in groupRows.Where(stock => !ReferenceEquals(stock, canonicalStock)))
            {
                _db.ItemWarehouseStocks.Remove(redundant);
                changed++;
            }
        }

        canonical.CurrentStock = canonicalRows.Sum(stock => stock.Quantity);
        return changed;
    }

    private static void MergeItemValues(Item canonical, Item duplicate)
    {
        FillIfBlank(value => canonical.SpecificationOriginal = value, canonical.SpecificationOriginal, duplicate.SpecificationOriginal);
        FillIfBlank(value => canonical.CategoryName = value, canonical.CategoryName, duplicate.CategoryName);
        FillIfBlank(value => canonical.ItemKind = value, canonical.ItemKind, duplicate.ItemKind);
        FillIfBlank(value => canonical.TrackingType = value, canonical.TrackingType, duplicate.TrackingType);
        FillIfBlank(value => canonical.Unit = value, canonical.Unit, duplicate.Unit);
        FillIfBlank(value => canonical.StorageLocation = value, canonical.StorageLocation, duplicate.StorageLocation);
        FillIfBlank(value => canonical.SimpleMemo = value, canonical.SimpleMemo, duplicate.SimpleMemo);
        FillIfBlank(value => canonical.SerialNumber = value, canonical.SerialNumber, duplicate.SerialNumber);
        FillIfBlank(value => canonical.MaterialNumber = value, canonical.MaterialNumber, duplicate.MaterialNumber);
        FillIfBlank(value => canonical.InstallLocation = value, canonical.InstallLocation, duplicate.InstallLocation);
        FillIfBlank(value => canonical.Notes = value, canonical.Notes, duplicate.Notes);
        if (canonical.BoxQuantity == 0m) canonical.BoxQuantity = duplicate.BoxQuantity;
        if (canonical.SafetyStock == 0m) canonical.SafetyStock = duplicate.SafetyStock;
        if (canonical.PurchasePrice == 0m) canonical.PurchasePrice = duplicate.PurchasePrice;
        if (canonical.SalePrice == 0m) canonical.SalePrice = duplicate.SalePrice;
        if (canonical.RetailPrice == 0m) canonical.RetailPrice = duplicate.RetailPrice;
        if (canonical.PriceGradeA == 0m) canonical.PriceGradeA = duplicate.PriceGradeA;
        if (canonical.PriceGradeB == 0m) canonical.PriceGradeB = duplicate.PriceGradeB;
        if (canonical.PriceGradeC == 0m) canonical.PriceGradeC = duplicate.PriceGradeC;
        canonical.LastPurchaseDate ??= duplicate.LastPurchaseDate;
        canonical.LastSaleDate ??= duplicate.LastSaleDate;
        canonical.RentalStartDate ??= duplicate.RentalStartDate;
        canonical.RentalEndDate ??= duplicate.RentalEndDate;
        canonical.IsRental |= duplicate.IsRental;
        canonical.IsSale |= duplicate.IsSale;
    }

    private static void FillIfBlank(Action<string> assign, string? current, string? source)
    {
        if (string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(source))
            assign(source.Trim());
    }

    private static void AddSemanticBlockers(IReadOnlyList<Item> items, Action<string> block)
    {
        static bool DifferentText(IEnumerable<string?> values) =>
            values.Select(value => (value ?? string.Empty).Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
        static bool DifferentNumber(IEnumerable<decimal> values) => values.Where(value => value != 0m).Distinct().Skip(1).Any();
        static bool DifferentDate(IEnumerable<DateOnly?> values) => values.Where(value => value.HasValue).Distinct().Skip(1).Any();
        static bool Different<T>(IEnumerable<T> values) => values.Distinct().Skip(1).Any();

        if (DifferentText(items.Select(item => item.CategoryName))) block("Category differs between candidates.");
        if (DifferentText(items.Select(item => item.ItemKind))) block("Item kind differs between candidates.");
        if (DifferentText(items.Select(item => item.TrackingType))) block("Tracking type differs between candidates.");
        if (DifferentText(items.Select(item => item.Unit))) block("Unit differs between candidates.");
        if (DifferentText(items.Select(item => item.StorageLocation))) block("Storage location differs between candidates.");
        if (DifferentText(items.Select(item => item.SerialNumber))) block("Serial number differs between candidates.");
        if (DifferentText(items.Select(item => item.MaterialNumber))) block("Material number differs between candidates.");
        if (DifferentText(items.Select(item => item.InstallLocation))) block("Install location differs between candidates.");
        if (DifferentText(items.Select(item => item.SimpleMemo))) block("Simple memo differs between candidates.");
        if (DifferentText(items.Select(item => item.Notes))) block("Notes differ between candidates.");
        if (DifferentNumber(items.Select(item => item.BoxQuantity))) block("Box quantity differs between candidates.");
        if (DifferentNumber(items.Select(item => item.SafetyStock))) block("Safety stock differs between candidates.");
        if (DifferentNumber(items.Select(item => item.PurchasePrice))) block("Purchase price differs between candidates.");
        if (DifferentNumber(items.Select(item => item.SalePrice))) block("Sale price differs between candidates.");
        if (DifferentNumber(items.Select(item => item.RetailPrice))) block("Retail price differs between candidates.");
        if (DifferentNumber(items.Select(item => item.PriceGradeA))) block("Price grade A differs between candidates.");
        if (DifferentNumber(items.Select(item => item.PriceGradeB))) block("Price grade B differs between candidates.");
        if (DifferentNumber(items.Select(item => item.PriceGradeC))) block("Price grade C differs between candidates.");
        if (DifferentDate(items.Select(item => item.LastPurchaseDate))) block("Last purchase date differs between candidates.");
        if (DifferentDate(items.Select(item => item.LastSaleDate))) block("Last sale date differs between candidates.");
        if (Different(items.Select(item => item.IsRental))) block("Rental flag differs between candidates.");
        if (Different(items.Select(item => item.IsSale))) block("Sale flag differs between candidates.");
        if (DifferentDate(items.Select(item => item.RentalStartDate))) block("Rental start date differs between candidates.");
        if (DifferentDate(items.Select(item => item.RentalEndDate))) block("Rental end date differs between candidates.");
    }

    private static string BuildServerSnapshotToken(
        IReadOnlyCollection<Item> items,
        IReadOnlyCollection<InvoiceLine> invoiceLines,
        IReadOnlyCollection<Invoice> invoices,
        IReadOnlyCollection<RentalAsset> assets,
        IReadOnlyCollection<RentalAssetAssignmentHistory> histories,
        IReadOnlyCollection<RentalBillingProfile> profiles,
        IReadOnlyCollection<InventoryTransferLine> transferLines,
        IReadOnlyCollection<InventoryTransfer> transfers,
        IReadOnlyCollection<ItemWarehouseStock> stocks,
        IReadOnlyCollection<ItemPriceGrade> grades,
        IReadOnlyCollection<InventoryLedgerEntry> ledgers)
    {
        var snapshot = new
        {
            Items = items.OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.TenantCode, row.OfficeCode, row.NameOriginal, row.SpecificationOriginal,
                row.CategoryName, row.ItemKind, row.TrackingType, row.Unit, row.BoxQuantity,
                row.StorageLocation, row.CurrentStock, row.SafetyStock, row.PurchasePrice, row.SalePrice,
                row.RetailPrice, row.PriceGradeA, row.PriceGradeB, row.PriceGradeC, row.LastPurchaseDate,
                row.LastSaleDate, row.SimpleMemo, row.IsRental, row.IsSale, row.SerialNumber,
                row.MaterialNumber, row.InstallLocation, row.RentalStartDate, row.RentalEndDate,
                row.Notes, row.IsDeleted, row.Revision, UpdatedAtUtcTicks = row.UpdatedAtUtc.ToUniversalTime().Ticks
            }),
            InvoiceLines = invoiceLines.OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.InvoiceId, row.ItemId, row.ItemNameOriginal, row.SpecificationOriginal, row.IsDeleted
            }),
            Invoices = invoices.OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode, row.IsDeleted,
                row.Revision, UpdatedAtUtcTicks = row.UpdatedAtUtc.ToUniversalTime().Ticks
            }),
            Assets = assets.OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.ItemId, row.ItemName, row.ItemCategoryName, row.TenantCode, row.OfficeCode,
                row.ResponsibleOfficeCode, row.IsDeleted, row.Revision,
                UpdatedAtUtcTicks = row.UpdatedAtUtc.ToUniversalTime().Ticks
            }),
            Histories = histories.OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.AssetId, row.ItemName, row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode,
                row.IsDeleted, row.Revision, UpdatedAtUtcTicks = row.UpdatedAtUtc.ToUniversalTime().Ticks
            }),
            Profiles = profiles.OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.BillingTemplateJson, row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode,
                row.IsDeleted, row.Revision, UpdatedAtUtcTicks = row.UpdatedAtUtc.ToUniversalTime().Ticks
            }),
            TransferLines = transferLines.OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.TransferId, row.ItemId, row.ItemNameOriginal, row.SpecificationOriginal, row.IsDeleted
            }),
            Transfers = transfers.OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.TenantCode, row.SourceOfficeCode, row.TargetOfficeCode, row.IsDeleted,
                row.Revision, UpdatedAtUtcTicks = row.UpdatedAtUtc.ToUniversalTime().Ticks
            }),
            Stocks = stocks.OrderBy(row => row.ItemId).ThenBy(row => row.WarehouseCode, StringComparer.Ordinal)
                .Select(row => new { row.ItemId, row.WarehouseCode, row.Quantity, row.Revision, UpdatedAtUtcTicks = row.UpdatedAtUtc.ToUniversalTime().Ticks }),
            Grades = grades.OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.ItemId, row.PriceGradeOptionId, row.PriceGradeName, row.UnitPrice,
                row.IsActive, row.IsDeleted, row.Revision, UpdatedAtUtcTicks = row.UpdatedAtUtc.ToUniversalTime().Ticks
            }),
            Ledgers = ledgers.OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.ItemId, row.WarehouseCode, row.SourceType, row.SourceDocumentId, row.SourceLineId,
                row.QuantityDelta, row.OccurredDate, row.CreatedAtUtc
            })
        };
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, SnapshotJsonOptions)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ComputePayloadHash(IReadOnlyCollection<Guid> ids, Guid canonicalId, string token)
    {
        var payload = new { CandidateItemIds = ids.Order().ToArray(), CanonicalItemId = canonicalId, SnapshotToken = token.Trim().ToLowerInvariant() };
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, SnapshotJsonOptions)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string? left, string? right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left?.Trim().ToLowerInvariant() ?? string.Empty);
        var rightBytes = Encoding.UTF8.GetBytes(right?.Trim().ToLowerInvariant() ?? string.Empty);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static List<Guid> NormalizeIds(IEnumerable<Guid>? ids)
        => (ids ?? []).Where(id => id != Guid.Empty).Distinct().Order().ToList();

    private static ItemDuplicateMergePreviewDto BlockedPreview(Guid canonicalId, string reason)
        => new() { CanonicalItemId = canonicalId, CanMerge = false, BlockingReasons = [reason] };

    private static ItemDuplicateMergeOutcome Invalid(string error, string message)
        => new(ItemDuplicateMergeStatus.Invalid, Error: error, Message: message);

    private static ItemDuplicateMergeOutcome Conflict(string error, string message)
        => new(ItemDuplicateMergeStatus.Conflict, Error: error, Message: message);
}
