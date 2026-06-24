using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public static class MobileSessionScopeFilter
{
    public static bool CanAccessCustomer(SessionSnapshot snapshot, CustomerDto? customer)
        => customer is not null &&
           CanAccessOperationalScope(
               snapshot,
               customer.ResponsibleOfficeCode,
               customer.TenantCode,
               customer.OfficeCode,
               allowSharedOffice: false);

    public static bool CanAccessItem(SessionSnapshot snapshot, ItemDto? item)
        => item is not null &&
           CanAccessOperationalScope(
               snapshot,
               item.OfficeCode,
               item.TenantCode,
               fallbackOfficeCode: null,
               allowSharedOffice: true);

    public static bool CanAccessInvoice(SessionSnapshot snapshot, InvoiceDto? invoice)
        => invoice is not null &&
           CanAccessOperationalScope(
               snapshot,
               invoice.ResponsibleOfficeCode,
               invoice.TenantCode,
               invoice.OfficeCode,
               allowSharedOffice: false);

    public static bool CanAccessTransaction(SessionSnapshot snapshot, TransactionDto? transaction)
        => transaction is not null &&
           CanAccessOperationalScope(
               snapshot,
               transaction.ResponsibleOfficeCode,
               transaction.TenantCode,
               transaction.OfficeCode,
               allowSharedOffice: false);

    public static bool CanAccessRentalBillingProfile(SessionSnapshot snapshot, RentalBillingProfileDto? profile)
        => profile is not null &&
           CanAccessOperationalScope(
               snapshot,
               profile.ResponsibleOfficeCode,
               profile.TenantCode,
               profile.OfficeCode,
               allowSharedOffice: false);

    public static bool CanAccessRentalAsset(SessionSnapshot snapshot, RentalAssetDto? asset)
        => asset is not null &&
           CanAccessOperationalScope(
               snapshot,
               asset.ResponsibleOfficeCode,
               asset.TenantCode,
               asset.OfficeCode,
               allowSharedOffice: false);

    public static bool CanAccessRentalAssetAssignmentHistory(SessionSnapshot snapshot, RentalAssetAssignmentHistoryDto? history)
        => history is not null &&
           CanAccessOperationalScope(
               snapshot,
               history.ResponsibleOfficeCode,
               history.TenantCode,
               history.OfficeCode,
               allowSharedOffice: false);

    public static bool CanAccessRentalBillingLog(SessionSnapshot snapshot, RentalBillingLogDto? log)
        => log is not null &&
           CanAccessOperationalScope(
               snapshot,
               log.ResponsibleOfficeCode,
               log.TenantCode,
               log.OfficeCode,
               allowSharedOffice: false);

    public static bool CanAccessInventoryTransfer(SessionSnapshot snapshot, InventoryTransferDto? transfer)
        => transfer is not null &&
           (CanAccessWarehouse(snapshot, transfer.FromWarehouseCode) ||
            CanAccessWarehouse(snapshot, transfer.ToWarehouseCode) ||
            CanAccessOperationalScope(snapshot, transfer.SourceOfficeCode, transfer.TenantCode) ||
            CanAccessOperationalScope(snapshot, transfer.TargetOfficeCode, transfer.TenantCode));

    public static bool CanAccessWarehouse(SessionSnapshot snapshot, string? warehouseCode)
    {
        if (IsGlobalAdminScope(snapshot))
            return true;

        if (!TryResolveOfficeCodeFromWarehouse(warehouseCode, out var officeCode))
            return false;

        return GetReadableOfficeCodes(snapshot).Contains(officeCode);
    }

    public static bool CanAccessOperationalScope(
        SessionSnapshot snapshot,
        string? responsibleOfficeCode,
        string? tenantCode = null,
        string? fallbackOfficeCode = null,
        bool allowSharedOffice = false)
    {
        if (!snapshot.IsAuthenticated)
            return false;

        if (IsGlobalAdminScope(snapshot))
            return true;

        if (OfficeCodeCatalog.IsSharedOfficeCode(responsibleOfficeCode) ||
            OfficeCodeCatalog.IsSharedOfficeCode(fallbackOfficeCode))
        {
            if (!allowSharedOffice)
                return false;

            var sessionOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(snapshot.OfficeCode);
            var sessionTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(snapshot.TenantCode, sessionOfficeCode);
            var entityTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, responsibleOfficeCode, fallbackOfficeCode: fallbackOfficeCode);
            return string.Equals(entityTenantCode, sessionTenantCode, StringComparison.OrdinalIgnoreCase);
        }

        if (!TryResolveOfficeCode(responsibleOfficeCode, fallbackOfficeCode, out var resolvedOfficeCode))
            return false;

        if (!GetReadableOfficeCodes(snapshot).Contains(resolvedOfficeCode))
            return false;

        if (string.Equals(GetNormalizedScopeType(snapshot), TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
        {
            var sessionTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(snapshot.TenantCode, snapshot.OfficeCode);
            var entityTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, resolvedOfficeCode);
            return string.Equals(entityTenantCode, sessionTenantCode, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    public static HashSet<string> GetReadableOfficeCodes(SessionSnapshot snapshot)
    {
        if (!snapshot.IsAuthenticated)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (IsGlobalAdminScope(snapshot))
            return OfficeCodeCatalog.All.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sessionOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(snapshot.OfficeCode);
        var sessionTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(snapshot.TenantCode, sessionOfficeCode);
        if (string.Equals(GetNormalizedScopeType(snapshot), TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
            return TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(sessionTenantCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            sessionOfficeCode
        };
    }

    private static bool IsGlobalAdminScope(SessionSnapshot snapshot)
        => snapshot.IsAuthenticated &&
           snapshot.IsAdmin &&
           string.Equals(GetNormalizedScopeType(snapshot), TenantScopeCatalog.ScopeAdmin, StringComparison.OrdinalIgnoreCase);

    private static string GetNormalizedScopeType(SessionSnapshot snapshot)
        => TenantScopeCatalog.NormalizeScopeTypeOrDefault(
            snapshot.ScopeType,
            snapshot.IsAdmin ? TenantScopeCatalog.ScopeAdmin : TenantScopeCatalog.ScopeOfficeOnly);

    private static bool TryResolveOfficeCode(string? officeCode, string? fallbackOfficeCode, out string resolvedOfficeCode)
    {
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out resolvedOfficeCode))
            return true;

        if (OfficeCodeCatalog.TryNormalizeOfficeCode(fallbackOfficeCode, out resolvedOfficeCode))
            return true;

        resolvedOfficeCode = string.Empty;
        return false;
    }

    private static bool TryResolveOfficeCodeFromWarehouse(string? warehouseCode, out string officeCode)
    {
        var normalized = (warehouseCode ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            officeCode = string.Empty;
            return false;
        }

        if (normalized.Contains(OfficeCodeCatalog.Itworld, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, OfficeCodeCatalog.ItworldMainWarehouse, StringComparison.OrdinalIgnoreCase))
        {
            officeCode = OfficeCodeCatalog.Itworld;
            return true;
        }

        if (normalized.Contains(OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, OfficeCodeCatalog.YeonsuMainWarehouse, StringComparison.OrdinalIgnoreCase))
        {
            officeCode = OfficeCodeCatalog.Yeonsu;
            return true;
        }

        if (normalized.Contains(OfficeCodeCatalog.Usenet, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("UZNET", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, OfficeCodeCatalog.UsenetMainWarehouse, StringComparison.OrdinalIgnoreCase))
        {
            officeCode = OfficeCodeCatalog.Usenet;
            return true;
        }

        officeCode = string.Empty;
        return false;
    }
}
