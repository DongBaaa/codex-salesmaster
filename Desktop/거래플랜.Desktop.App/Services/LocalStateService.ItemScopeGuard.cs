using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    public Task<LocalItem> UpsertItemAsync(LocalItem item, SessionState session, CancellationToken ct = default)
        => UpsertItemAsync(item, session, preferredOfficeCode: null, ct);

    public async Task<LocalItem> UpsertItemAsync(
        LocalItem item,
        SessionState session,
        string? preferredOfficeCode,
        CancellationToken ct = default)
        => await UpsertItemAsync(item, session, preferredOfficeCode, itemPriceGrades: null, ct);

    public async Task<LocalItem> UpsertItemAsync(
        LocalItem item,
        SessionState session,
        string? preferredOfficeCode,
        IEnumerable<LocalItemPriceGrade>? itemPriceGrades,
        CancellationToken ct = default)
    {
        EnsureCanUpsertItem(item, session, preferredOfficeCode);

        return await UpsertItemWithDeletedRestorePolicyAsync(
            item,
            session,
            preferredOfficeCode,
            itemPriceGrades,
            allowDeletedRestore: false,
            ct);
    }

    public Task<LocalItem> SaveInventoryItemAsync(
        LocalItem item,
        SessionState session,
        string? preferredOfficeCode,
        IEnumerable<LocalItemPriceGrade>? itemPriceGrades,
        CancellationToken ct = default)
    {
        EnsureCanUpsertItem(item, session, preferredOfficeCode);
        return UpsertItemWithDeletedRestorePolicyAsync(
            item, session, preferredOfficeCode, itemPriceGrades,
            allowDeletedRestore: false, ct, preserveInventoryEditorHiddenFields: true);
    }

    private async Task<LocalItem> RestoreDeletedMissingItemAsync(
        LocalItem item,
        SessionState session,
        string? preferredOfficeCode,
        CancellationToken ct)
    {
        EnsureCanUpsertItem(item, session, preferredOfficeCode);
        return await UpsertItemWithDeletedRestorePolicyAsync(
            item,
            session,
            preferredOfficeCode,
            itemPriceGrades: null,
            allowDeletedRestore: true,
            ct);
    }

    private async Task<LocalItem> UpsertItemWithDeletedRestorePolicyAsync(
        LocalItem item,
        SessionState session,
        string? preferredOfficeCode,
        IEnumerable<LocalItemPriceGrade>? itemPriceGrades,
        bool allowDeletedRestore,
        CancellationToken ct,
        bool preserveInventoryEditorHiddenFields = false)
    {
        var amountAccess = new ItemAmountWriteAccess(session);
        if (_db.Database.CurrentTransaction is not null)
            return await SaveItemAndPriceGradesAsync(item, session, preferredOfficeCode, itemPriceGrades, allowDeletedRestore, ct, preserveInventoryEditorHiddenFields, amountAccess);

        await using var transaction = await _db.BeginRuntimeMutationTransactionAsync(ct);
        try
        {
            var saved = await SaveItemAndPriceGradesAsync(item, session, preferredOfficeCode, itemPriceGrades, allowDeletedRestore, ct, preserveInventoryEditorHiddenFields, amountAccess);
            using (await session.AcquireSyncScopeCommitLeaseAsync(ct))
            {
                amountAccess.EnsureCurrent();
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            return saved;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<LocalItem> SaveItemAndPriceGradesAsync(
        LocalItem item,
        SessionState session,
        string? preferredOfficeCode,
        IEnumerable<LocalItemPriceGrade>? itemPriceGrades,
        bool allowDeletedRestore,
        CancellationToken ct,
        bool preserveInventoryEditorHiddenFields,
        ItemAmountWriteAccess amountAccess)
    {
        var saved = await UpsertItemAsync(
            item,
            preferredOfficeCode,
            synchronizeLinkedRentalAssets: CanEditRentalAssets(session),
            preserveExistingInventoryStock: true,
            allowDeletedRestore,
            ct,
            preserveInventoryEditorHiddenFields,
            amountAccess);
        amountAccess.EnsureCurrent();
        if (itemPriceGrades is not null && amountAccess.CanViewSales && !saved.SalesAmountsHidden)
            await SaveItemPriceGradesForItemAsync(saved.Id, itemPriceGrades, ct);
        amountAccess.EnsureCurrent();
        return saved;
    }

    private sealed class ItemAmountWriteAccess(SessionState session)
    {
        private readonly FinancialAmountVisibility.AccessKey _access = FinancialAmountVisibility.CaptureAccess(session);
        public bool CanViewSales => _access.Sales;
        public void EnsureCurrent()
        {
            if (_access != FinancialAmountVisibility.CaptureAccess(session))
                throw new UnauthorizedAccessException("계정 또는 접근 권한이 변경되었습니다. 품목을 다시 조회한 뒤 저장해 주세요.");
        }
        public void PreserveHiddenPrices(LocalItem candidate, LocalItem? persisted)
        {
            EnsureCurrent();
            if (!_access.Purchase || candidate.PurchaseAmountsHidden || persisted?.PurchaseAmountsHidden == true)
            {
                candidate.PurchasePrice = persisted?.PurchasePrice ?? 0m;
                candidate.PurchaseAmountsHidden = persisted?.PurchaseAmountsHidden ?? candidate.PurchaseAmountsHidden;
            }
            if (_access.Sales && !candidate.SalesAmountsHidden && persisted?.SalesAmountsHidden != true) return;
            candidate.SalesAmountsHidden = persisted?.SalesAmountsHidden ?? candidate.SalesAmountsHidden;
            candidate.SalePrice = persisted?.SalePrice ?? 0m;
            candidate.RetailPrice = persisted?.RetailPrice ?? 0m;
            candidate.PriceGradeA = persisted?.PriceGradeA ?? 0m;
            candidate.PriceGradeB = persisted?.PriceGradeB ?? 0m;
            candidate.PriceGradeC = persisted?.PriceGradeC ?? 0m;
        }
    }

    public void EnsureCanUpsertItem(LocalItem item, SessionState session, string? preferredOfficeCode = null)
    {
        if (!CanEditItems(session))
            throw new UnauthorizedAccessException("현재 계정은 품목을 저장할 권한이 없습니다.");

        NormalizeItemOperationalState(item);
        NormalizeItemScope(item, preferredOfficeCode);

        if (CanWriteItemScope(item, session))
            return;

        throw new UnauthorizedAccessException(BuildItemScopeDeniedMessage(item, session));
    }

    private static string BuildItemScopeDeniedMessage(LocalItem item, SessionState session)
    {
        var currentOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, DomainConstants.OfficeUsenet);
        var currentTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);
        var currentScopeDisplay = $"{OfficeCodeCatalog.GetOfficeDisplayName(currentOfficeCode)} / {TenantScopeCatalog.GetTenantDisplayName(currentTenantCode)}";

        var targetOfficeCode = NormalizeOfficeScope(item.OfficeCode, OfficeCodeCatalog.Shared);
        var targetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            item.TenantCode,
            item.OfficeCode,
            session.TenantCode,
            session.OfficeCode);

        var targetScopeDisplay = string.Equals(targetOfficeCode, OfficeCodeCatalog.Shared, StringComparison.OrdinalIgnoreCase)
            ? $"{TenantScopeCatalog.GetTenantDisplayName(targetTenantCode)} 공용"
            : $"{OfficeCodeCatalog.GetOfficeDisplayName(targetOfficeCode)} / {TenantScopeCatalog.GetTenantDisplayName(targetTenantCode)}";

        return $"이 품목은 {targetScopeDisplay} 범위입니다. 현재 로그인({currentScopeDisplay})으로는 저장할 수 없습니다. 해당 범위를 처리할 수 있는 계정으로 다시 로그인한 뒤 저장하세요.";
    }
}
