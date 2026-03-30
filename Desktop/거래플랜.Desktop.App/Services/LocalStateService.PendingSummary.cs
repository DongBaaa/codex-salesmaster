using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed record PendingSyncBucket(
    string ScopeKey,
    string ScopeDisplayName,
    string EntityDisplayName,
    int Count);

public sealed record PendingSyncSummary(
    int TotalCount,
    IReadOnlyList<PendingSyncBucket> Buckets)
{
    public PendingSyncBucket? PrimaryBucket => Buckets
        .OrderByDescending(bucket => bucket.Count)
        .ThenBy(bucket => bucket.ScopeDisplayName, StringComparer.Ordinal)
        .ThenBy(bucket => bucket.EntityDisplayName, StringComparer.Ordinal)
        .FirstOrDefault();

    public string BuildWaitingMessage(string? prefix = null)
    {
        if (TotalCount <= 0)
            return string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim();

        var primary = PrimaryBucket;
        var waitingMessage = primary is null
            ? $"서버 반영 대기 데이터 {TotalCount:N0}건이 남아 있습니다."
            : TotalCount == primary.Count
                ? $"{primary.ScopeDisplayName} {primary.EntityDisplayName} {primary.Count:N0}건이 서버 반영 대기 중입니다."
                : $"{primary.ScopeDisplayName} {primary.EntityDisplayName} {primary.Count:N0}건 포함 총 {TotalCount:N0}건이 서버 반영 대기 중입니다.";

        return string.IsNullOrWhiteSpace(prefix)
            ? waitingMessage
            : $"{prefix.Trim()} {waitingMessage}".Trim();
    }
}

public sealed partial class LocalStateService
{
    private sealed record DirtyScopeRow(string? OfficeCode, string? TenantCode);

    public async Task<PendingSyncSummary> GetPendingSyncSummaryAsync(CancellationToken ct = default)
    {
        var buckets = new List<PendingSyncBucket>();

        AppendBuckets(
            buckets,
            await _db.CompanyProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => entity.IsDirty)
                .Select(entity => new DirtyScopeRow(entity.OfficeCode, null))
                .ToListAsync(ct),
            "회사정보 변경");

        AppendBuckets(
            buckets,
            await _db.CustomerMasters
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => entity.IsDirty)
                .Select(entity => new DirtyScopeRow(entity.OfficeCode, entity.TenantCode))
                .ToListAsync(ct),
            "거래처 기준정보 변경");

        AppendBuckets(
            buckets,
            await _db.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => entity.IsDirty)
                .Select(entity => new DirtyScopeRow(entity.ResponsibleOfficeCode, entity.TenantCode))
                .ToListAsync(ct),
            "거래처 변경");

        AppendBuckets(
            buckets,
            await (
                from contract in _db.CustomerContracts.IgnoreQueryFilters().AsNoTracking()
                where contract.IsDirty
                join customer in _db.Customers.IgnoreQueryFilters().AsNoTracking()
                    on contract.CustomerId equals customer.Id into customerGroup
                from customer in customerGroup.DefaultIfEmpty()
                select new DirtyScopeRow(
                    customer != null ? customer.ResponsibleOfficeCode : OfficeCodeCatalog.Shared,
                    customer != null ? customer.TenantCode : string.Empty))
                .ToListAsync(ct),
            "거래처 계약 변경");

        AppendBuckets(
            buckets,
            await _db.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => entity.IsDirty)
                .Select(entity => new DirtyScopeRow(entity.OfficeCode, entity.TenantCode))
                .ToListAsync(ct),
            "품목 변경");

        AppendSharedBucket(
            buckets,
            await _db.ItemCategoryOptions.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct),
            "품목분류 변경");

        AppendBuckets(
            buckets,
            await _db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => entity.IsDirty)
                .Select(entity => new DirtyScopeRow(entity.ResponsibleOfficeCode, null))
                .ToListAsync(ct),
            "전표 변경");

        AppendBuckets(
            buckets,
            await (
                from payment in _db.Payments.IgnoreQueryFilters().AsNoTracking()
                where payment.IsDirty
                join invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                    on payment.InvoiceId equals invoice.Id into invoiceGroup
                from invoice in invoiceGroup.DefaultIfEmpty()
                select new DirtyScopeRow(
                    invoice != null ? invoice.ResponsibleOfficeCode : OfficeCodeCatalog.Shared,
                    null))
                .ToListAsync(ct),
            "수금 변경");

        AppendBuckets(
            buckets,
            await _db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => entity.IsDirty)
                .Select(entity => new DirtyScopeRow(entity.ResponsibleOfficeCode, null))
                .ToListAsync(ct),
            "거래내역 변경");

        AppendBuckets(
            buckets,
            await (
                from attachment in _db.TransactionAttachments.IgnoreQueryFilters().AsNoTracking()
                where attachment.IsDirty
                join transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking()
                    on attachment.TransactionId equals transaction.Id into transactionGroup
                from transaction in transactionGroup.DefaultIfEmpty()
                select new DirtyScopeRow(
                    transaction != null ? transaction.ResponsibleOfficeCode : OfficeCodeCatalog.Shared,
                    null))
                .ToListAsync(ct),
            "거래증빙 변경");

        var dirtyTransfers = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => entity.IsDirty)
            .Select(entity => new { entity.FromWarehouseCode, entity.ToWarehouseCode })
            .ToListAsync(ct);
        AppendBuckets(
            buckets,
            dirtyTransfers.Select(transfer =>
            {
                var fromOfficeCode = ResolveOfficeCodeFromWarehouseCode(transfer.FromWarehouseCode);
                var toOfficeCode = ResolveOfficeCodeFromWarehouseCode(transfer.ToWarehouseCode);
                var officeCode = string.Equals(fromOfficeCode, toOfficeCode, StringComparison.OrdinalIgnoreCase)
                    ? fromOfficeCode
                    : OfficeCodeCatalog.Shared;
                return new DirtyScopeRow(officeCode, null);
            }),
            "재고이동 변경");

        AppendSharedBucket(
            buckets,
            await _db.RentalManagementCompanies.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct),
            "렌탈 관리업체 변경");

        AppendBuckets(
            buckets,
            await _db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => entity.IsDirty)
                .Select(entity => new DirtyScopeRow(entity.ResponsibleOfficeCode, entity.ManagementCompanyCode))
                .ToListAsync(ct),
            "렌탈 청구설정 변경");

        AppendBuckets(
            buckets,
            await _db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => entity.IsDirty)
                .Select(entity => new DirtyScopeRow(entity.ResponsibleOfficeCode, entity.ManagementCompanyCode))
                .ToListAsync(ct),
            "렌탈 자산 변경");

        AppendBuckets(
            buckets,
            await _db.RentalBillingLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => entity.IsDirty)
                .Select(entity => new DirtyScopeRow(entity.ResponsibleOfficeCode, null))
                .ToListAsync(ct),
            "렌탈 청구이력 변경");

        var orderedBuckets = buckets
            .OrderByDescending(bucket => bucket.Count)
            .ThenBy(bucket => bucket.ScopeDisplayName, StringComparer.Ordinal)
            .ThenBy(bucket => bucket.EntityDisplayName, StringComparer.Ordinal)
            .ToList();

        return new PendingSyncSummary(
            orderedBuckets.Sum(bucket => bucket.Count),
            orderedBuckets);
    }

    public async Task<string?> GetPendingSyncWaitingMessageAsync(string? prefix = null, CancellationToken ct = default)
    {
        var summary = await GetPendingSyncSummaryAsync(ct);
        if (summary.TotalCount <= 0)
            return null;

        return summary.BuildWaitingMessage(prefix);
    }

    private static void AppendSharedBucket(ICollection<PendingSyncBucket> buckets, int count, string entityDisplayName)
    {
        if (count <= 0)
            return;

        buckets.Add(new PendingSyncBucket(
            ScopeKey: "SHARED",
            ScopeDisplayName: "공용",
            EntityDisplayName: entityDisplayName,
            Count: count));
    }

    private static void AppendBuckets(
        ICollection<PendingSyncBucket> buckets,
        IEnumerable<DirtyScopeRow> rows,
        string entityDisplayName)
    {
        var groupedRows = rows
            .Select(row => ResolveScope(row.OfficeCode, row.TenantCode))
            .GroupBy(scope => scope.ScopeKey, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groupedRows)
        {
            var first = group.First();
            buckets.Add(new PendingSyncBucket(
                ScopeKey: first.ScopeKey,
                ScopeDisplayName: first.ScopeDisplayName,
                EntityDisplayName: entityDisplayName,
                Count: group.Count()));
        }
    }

    private static (string ScopeKey, string ScopeDisplayName) ResolveScope(string? officeCode, string? tenantCode)
    {
        var normalizedOfficeCode = (officeCode ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedOfficeCode))
        {
            normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(normalizedOfficeCode, OfficeCodeCatalog.Shared);
            if (!OfficeCodeCatalog.IsSharedOfficeCode(normalizedOfficeCode))
                return ($"OFFICE:{normalizedOfficeCode}", OfficeCodeCatalog.GetOfficeDisplayName(normalizedOfficeCode));
        }

        var normalizedTenantCode = (tenantCode ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedTenantCode))
        {
            normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(normalizedTenantCode);
            return ($"TENANT:{normalizedTenantCode}", TenantScopeCatalog.GetTenantDisplayName(normalizedTenantCode));
        }

        return ("SHARED", "공용");
    }
}
