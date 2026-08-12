using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;

namespace GeoraePlan.Tools.SyncDiag;

internal static class IsolatedSeedSyncFailureDiagnostics
{
    private static readonly HashSet<string> AllowedEntityNames = new(
        StringComparer.Ordinal)
    {
        nameof(LocalCustomerMaster),
        nameof(LocalCustomer),
        nameof(LocalCustomerContract),
        nameof(LocalItem),
        nameof(LocalInvoice),
        nameof(LocalPayment),
        nameof(LocalTransaction),
        nameof(LocalTransactionAttachment),
        nameof(LocalInventoryTransfer),
        nameof(LocalRentalManagementCompany),
        nameof(LocalRentalBillingProfile),
        nameof(LocalRentalAsset),
        nameof(LocalRentalAssetAssignmentHistory),
        nameof(LocalRentalBillingLog)
    };

    private static readonly HashSet<string> AllowedStatuses = new(
        StringComparer.Ordinal)
    {
        "Prepared",
        "Sent",
        "Failed"
    };

    internal static async Task<IReadOnlyList<string>> BuildLinesAsync(
        LocalDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var lines = new List<string>();
        var outboxRows = await db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry => entry.Status != "Acknowledged")
            .Select(entry => new
            {
                entry.EntityName,
                entry.Status,
                entry.ErrorMessage
            })
            .ToListAsync(cancellationToken);

        foreach (var group in outboxRows
                     .GroupBy(entry => new
                     {
                         Entity = SafeEntityName(entry.EntityName),
                         Status = SafeStatus(entry.Status),
                         ErrorKind = ClassifyError(entry.ErrorMessage)
                     })
                     .OrderBy(group => group.Key.Entity, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.Status, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.ErrorKind, StringComparer.Ordinal))
        {
            lines.Add(
                $"seed_sync_outbox_group entity={group.Key.Entity} " +
                $"status={group.Key.Status} error_kind={group.Key.ErrorKind} " +
                $"count={group.Count()}");
        }

        await AddDirtyCountAsync(
            lines,
            nameof(LocalCustomerMaster),
            db.CustomerMasters.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalCustomer),
            db.Customers.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalCustomerContract),
            db.CustomerContracts.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalItem),
            db.Items.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalInvoice),
            db.Invoices.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalPayment),
            db.Payments.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalTransaction),
            db.Transactions.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalTransactionAttachment),
            db.TransactionAttachments.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalInventoryTransfer),
            db.InventoryTransfers.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalRentalManagementCompany),
            db.RentalManagementCompanies.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalRentalBillingProfile),
            db.RentalBillingProfiles.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalRentalAsset),
            db.RentalAssets.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalRentalAssetAssignmentHistory),
            db.RentalAssetAssignmentHistories.IgnoreQueryFilters(),
            cancellationToken);
        await AddDirtyCountAsync(
            lines,
            nameof(LocalRentalBillingLog),
            db.RentalBillingLogs.IgnoreQueryFilters(),
            cancellationToken);

        return lines;
    }

    private static async Task AddDirtyCountAsync<TEntity>(
        ICollection<string> lines,
        string entityName,
        IQueryable<TEntity> query,
        CancellationToken cancellationToken)
        where TEntity : class, ILocalSyncEntity
    {
        var count = await query.CountAsync(
            entity => entity.IsDirty,
            cancellationToken);
        if (count > 0)
            lines.Add($"seed_sync_dirty_entity entity={entityName} count={count}");
    }

    private static string SafeEntityName(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return AllowedEntityNames.Contains(normalized) ? normalized : "unknown";
    }

    private static string SafeStatus(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return AllowedStatuses.Contains(normalized) ? normalized : "unknown";
    }

    private static string ClassifyError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "none";

        var normalized = value.Trim();
        if (ContainsAny(
                normalized,
                "OPERATION_ID_REUSED_WITH_DIFFERENT_PAYLOAD"))
            return "operation_id_reused";
        if (ContainsAny(
                normalized,
                "Mutation id was already processed with a different entity, expected revision, or payload"))
            return "mutation_replay_mismatch";
        if (ContainsAny(
                normalized,
                "Mutation id is duplicated",
                "mutation id is reused by conflicting rows"))
            return "mutation_duplicate";
        if (ContainsAny(
                normalized,
                "same invoice id",
                "protected invoice"))
            return "protected_invoice_structure";
        if (ContainsAny(
                normalized,
                "Referenced rental asset is outside",
                "Referenced rental asset was not found"))
            return "rental_asset_reference";
        if (ContainsAny(
                normalized,
                "Referenced rental billing profile"))
            return "rental_billing_profile_reference";
        if (ContainsAny(
                normalized,
                "Referenced customer"))
            return "customer_reference";
        if (ContainsAny(
                normalized,
                "Current account cannot modify this office scope"))
            return "office_scope";
        if (ContainsAny(
                normalized,
                "invoice version metadata",
                "invoice version chain"))
            return "invoice_version_metadata";
        if (ContainsAny(
                normalized,
                "requires exactly one Transaction and one Payment",
                "paired Payment and Transaction"))
            return "payment_transaction_pair";
        if (ContainsAny(
                normalized,
                "inventory transfer stock rollback",
                "재고이동 재고 롤백"))
            return "inventory_transfer_atomicity";
        if (ContainsAny(
                normalized,
                "서버에서 삭제된 재고이동",
                "inventory transfer tombstone"))
            return "inventory_transfer_tombstone";
        if (ContainsAny(
                normalized,
                "revision",
                "리비전",
                "버전 충돌",
                "동시 수정"))
            return "revision_conflict";
        if (ContainsAny(
                normalized,
                "foreign key",
                "reference constraint",
                "참조 무결성",
                "참조 제약"))
            return "reference_constraint";
        if (ContainsAny(
                normalized,
                "401",
                "403",
                "unauthorized",
                "forbidden",
                "인증",
                "권한"))
            return "authorization";
        if (ContainsAny(
                normalized,
                "409",
                "conflict",
                "충돌"))
            return "conflict";
        if (ContainsAny(
                normalized,
                "400",
                "422",
                "validation",
                "invalid",
                "유효하지",
                "필수"))
            return "validation";
        if (ContainsAny(normalized, "timeout", "timed out", "시간 초과"))
            return "timeout";
        if (ContainsAny(
                normalized,
                "http",
                "socket",
                "connection",
                "network",
                "연결",
                "네트워크"))
            return "transport";

        return "other";
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
