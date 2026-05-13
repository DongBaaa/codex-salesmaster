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
    {
        EnsureCanUpsertItem(item, session, preferredOfficeCode);
        return await UpsertItemAsync(
            item,
            preferredOfficeCode,
            synchronizeLinkedRentalAssets: CanEditRentalAssets(session),
            ct);
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
