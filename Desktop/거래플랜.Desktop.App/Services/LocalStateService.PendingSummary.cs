using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

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
            (await _db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => entity.IsDirty)
                .Select(entity => new
                {
                    entity.TenantCode,
                    entity.OfficeCode,
                    entity.ManagementCompanyCode,
                    entity.ResponsibleOfficeCode
                })
                .ToListAsync(ct))
                .Select(entity => ResolveRentalDirtyScope(
                    entity.TenantCode,
                    entity.OfficeCode,
                    entity.ManagementCompanyCode,
                    entity.ResponsibleOfficeCode)),
            "렌탈 청구설정 변경");

        AppendBuckets(
            buckets,
            (await _db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => entity.IsDirty)
                .Select(entity => new
                {
                    entity.TenantCode,
                    entity.OfficeCode,
                    entity.ManagementCompanyCode,
                    entity.ResponsibleOfficeCode
                })
                .ToListAsync(ct))
                .Select(entity => ResolveRentalDirtyScope(
                    entity.TenantCode,
                    entity.OfficeCode,
                    entity.ManagementCompanyCode,
                    entity.ResponsibleOfficeCode)),
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

    public async Task<string?> GetPendingSyncWaitingMessageAsync(
        SessionState session,
        string? prefix = null,
        CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return await GetPendingSyncWaitingMessageAsync(prefix, ct);

        var summary = await GetPendingSyncSummaryAsync(ct);
        if (summary.TotalCount <= 0)
            return null;

        var scopedBuckets = summary.Buckets
            .Where(bucket => IsCurrentSessionPendingBucket(bucket, session))
            .ToList();
        if (scopedBuckets.Count == 0)
            return null;

        return new PendingSyncSummary(
                scopedBuckets.Sum(bucket => bucket.Count),
                scopedBuckets)
            .BuildWaitingMessage(prefix);
    }

    public async Task<PendingSyncBlockingReason?> GetPendingSyncBlockingReasonAsync(
        SessionState session,
        string scopeKey,
        CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn || string.IsNullOrWhiteSpace(scopeKey))
            return null;

        var summary = await GetPendingSyncSummaryAsync(ct);
        var scopedBuckets = summary.Buckets
            .Where(bucket => string.Equals(bucket.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (scopedBuckets.Count == 0)
            return null;

        var representativeBucket = scopedBuckets
            .OrderByDescending(bucket => bucket.Count)
            .ThenBy(bucket => bucket.EntityDisplayName, StringComparer.Ordinal)
            .First();
        var pendingCount = scopedBuckets.Sum(bucket => bucket.Count);
        return await BuildPendingSyncBlockingReasonAsync(session, representativeBucket, pendingCount, ct);
    }

    public async Task<PendingSyncBlockingReason?> GetPrimaryPendingSyncBlockingReasonAsync(
        SessionState session,
        CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return null;

        var summary = await GetPendingSyncSummaryAsync(ct);
        var primary = summary.Buckets
            .Where(bucket => IsCurrentSessionPendingBucket(bucket, session))
            .OrderByDescending(bucket => bucket.Count)
            .ThenBy(bucket => bucket.ScopeDisplayName, StringComparer.Ordinal)
            .ThenBy(bucket => bucket.EntityDisplayName, StringComparer.Ordinal)
            .FirstOrDefault();
        if (primary is null)
            return null;

        return await BuildPendingSyncBlockingReasonAsync(session, primary, primary.Count, ct);
    }

    private async Task<PendingSyncBlockingReason?> BuildPendingSyncBlockingReasonAsync(
        SessionState session,
        PendingSyncBucket bucket,
        int pendingCount,
        CancellationToken ct)
    {
        if (string.Equals(bucket.ScopeKey, "SHARED", StringComparison.OrdinalIgnoreCase))
        {
            var sharedMessage = session.HasAdministrativePrivileges
                ? "원인: 공용 마스터 변경이 아직 서버 반영 대기 중입니다. 동기화를 다시 실행하거나 동기화 진단에서 남은 항목을 확인하세요."
                : "원인: 남은 변경은 공용 마스터 범위입니다. 관리자 전체 범위 세션으로 로그인한 뒤 다시 동기화해야 합니다.";
            return new PendingSyncBlockingReason(
                bucket.ScopeKey,
                bucket.ScopeDisplayName,
                bucket.EntityDisplayName,
                pendingCount,
                sharedMessage,
                string.Empty,
                session.HasAdministrativePrivileges,
                session.HasAdministrativePrivileges);
        }

        var currentOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, string.Empty);
        var currentTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);
        var requiredOfficeCode = ResolveRequiredOfficeCode(bucket.ScopeKey);
        var isCurrentScope = IsCurrentPendingScope(bucket.ScopeKey, currentOfficeCode, currentTenantCode);

        var hasStoredCredential = isCurrentScope;
        if (!hasStoredCredential && !string.IsNullOrWhiteSpace(requiredOfficeCode))
        {
            hasStoredCredential = (await GetStoredSyncCredentialsAsync(ct))
                .Any(credential => string.Equals(
                    OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(credential.OfficeCode, credential.OfficeCode),
                    requiredOfficeCode,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (!isCurrentScope && !hasStoredCredential)
        {
            var targetCredentialDisplayName = ResolveCredentialDisplayName(requiredOfficeCode, bucket.ScopeDisplayName);
            return new PendingSyncBlockingReason(
                bucket.ScopeKey,
                bucket.ScopeDisplayName,
                bucket.EntityDisplayName,
                pendingCount,
                $"원인: 저장된 {targetCredentialDisplayName} 동기화 계정이 없어 해당 범위 변경을 자동 업로드하지 못했습니다. 환경설정 > 동기화에서 {targetCredentialDisplayName} 계정을 저장한 뒤 다시 시도하세요.",
                requiredOfficeCode,
                false,
                false);
        }

        if (!isCurrentScope && hasStoredCredential)
        {
            var currentScopeDisplay = $"{OfficeCodeCatalog.GetOfficeDisplayName(currentOfficeCode)} / {TenantScopeCatalog.GetTenantDisplayName(currentTenantCode)}";
            var targetCredentialDisplayName = ResolveCredentialDisplayName(requiredOfficeCode, bucket.ScopeDisplayName);
            return new PendingSyncBlockingReason(
                bucket.ScopeKey,
                bucket.ScopeDisplayName,
                bucket.EntityDisplayName,
                pendingCount,
                $"원인: 남은 변경은 {bucket.ScopeDisplayName} 범위입니다. 현재 로그인({currentScopeDisplay})과 다른 범위라 저장된 {targetCredentialDisplayName} 계정으로 추가 동기화가 필요합니다.",
                requiredOfficeCode,
                true,
                false);
        }

        return new PendingSyncBlockingReason(
            bucket.ScopeKey,
            bucket.ScopeDisplayName,
            bucket.EntityDisplayName,
            pendingCount,
            $"원인: {bucket.ScopeDisplayName} 범위 변경이 아직 서버 반영 대기 중입니다. 동기화를 다시 실행하거나 동기화 진단에서 남은 항목을 확인하세요.",
            requiredOfficeCode,
            hasStoredCredential,
            true);
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

    private static DirtyScopeRow ResolveRentalDirtyScope(
        string? tenantCode,
        string? officeCode,
        string? managementCompanyCode,
        string? responsibleOfficeCode)
    {
        var scope = RentalScopeNormalizer.ResolveScope(
            tenantCode,
            officeCode,
            managementCompanyCode,
            responsibleOfficeCode,
            officeCode);
        return new DirtyScopeRow(scope.ResponsibleOfficeCode, scope.TenantCode);
    }

    private static string ResolveRequiredOfficeCode(string scopeKey)
    {
        if (scopeKey.StartsWith("OFFICE:", StringComparison.OrdinalIgnoreCase))
            return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(scopeKey[7..], string.Empty);

        if (scopeKey.StartsWith("TENANT:", StringComparison.OrdinalIgnoreCase))
        {
            var tenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(scopeKey[7..], string.Empty);
            return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                TenantScopeCatalog.GetOfficeCodesForTenant(tenantCode).FirstOrDefault(),
                string.Empty);
        }

        return string.Empty;
    }

    private static bool IsCurrentPendingScope(string scopeKey, string currentOfficeCode, string currentTenantCode)
    {
        if (scopeKey.StartsWith("OFFICE:", StringComparison.OrdinalIgnoreCase))
        {
            var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(scopeKey[7..], string.Empty);
            return !string.IsNullOrWhiteSpace(officeCode) &&
                   string.Equals(officeCode, currentOfficeCode, StringComparison.OrdinalIgnoreCase);
        }

        if (scopeKey.StartsWith("TENANT:", StringComparison.OrdinalIgnoreCase))
        {
            var tenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(scopeKey[7..], string.Empty);
            return !string.IsNullOrWhiteSpace(tenantCode) &&
                   string.Equals(tenantCode, currentTenantCode, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsCurrentSessionPendingBucket(PendingSyncBucket bucket, SessionState session)
    {
        if (string.Equals(bucket.ScopeKey, "SHARED", StringComparison.OrdinalIgnoreCase))
            return session.HasAdministrativePrivileges || CanWriteSharedOfficeScope(session);

        var writableOfficeCodes = GetCurrentLoginSyncOfficeCodes(session);

        if (bucket.ScopeKey.StartsWith("OFFICE:", StringComparison.OrdinalIgnoreCase))
        {
            var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(bucket.ScopeKey[7..], string.Empty);
            return !string.IsNullOrWhiteSpace(officeCode) && writableOfficeCodes.Contains(officeCode);
        }

        if (bucket.ScopeKey.StartsWith("TENANT:", StringComparison.OrdinalIgnoreCase))
        {
            var tenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(bucket.ScopeKey[7..], string.Empty);
            return TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode)
                .Any(writableOfficeCodes.Contains);
        }

        var currentOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, string.Empty);
        var currentTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);
        return IsCurrentPendingScope(bucket.ScopeKey, currentOfficeCode, currentTenantCode);
    }

    private static HashSet<string> GetCurrentLoginSyncOfficeCodes(SessionState session)
    {
        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            session.AuthenticatedTenantCode,
            session.OfficeCode);
        var scopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(session.ScopeType);

        if (session.HasAdministrativePrivileges ||
            string.Equals(scopeType, TenantScopeCatalog.ScopeAdmin, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(scopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
        {
            return TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return
        [
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, string.Empty)
        ];
    }

    private static string ResolveCredentialDisplayName(string requiredOfficeCode, string fallbackScopeDisplayName)
    {
        if (string.IsNullOrWhiteSpace(requiredOfficeCode))
            return fallbackScopeDisplayName;

        return OfficeCodeCatalog.GetOfficeDisplayName(requiredOfficeCode);
    }
}
