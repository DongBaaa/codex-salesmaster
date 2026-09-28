using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

internal static class RentalAssetAmountPrivacy
{
    // Only detached display rows may be redacted. Persisted slots are preserved on write.
    internal static void Redact(LocalRentalAsset asset, SessionState session)
    {
        asset.PurchaseAmountsHidden |= !session.HasPermission(AppPermissionNames.AmountViewPurchase);
        asset.SalesAmountsHidden |= !session.HasPermission(AppPermissionNames.AmountViewSales);
        if (asset.PurchaseAmountsHidden) asset.PurchasePrice = 0m;
        if (!asset.SalesAmountsHidden) return;
        asset.SalePrice = asset.MonthlyFee = 0m;
        asset.DepositText = string.Empty;
        asset.BlackOverageUnitPrice = asset.ColorOverageUnitPrice = null;
    }

    internal static string? Preserve(LocalRentalAsset candidate, LocalRentalAsset? stored, SessionState session)
    {
        var purchaseHidden = candidate.PurchaseAmountsHidden || stored?.PurchaseAmountsHidden == true || !session.HasPermission(AppPermissionNames.AmountViewPurchase);
        var salesHidden = candidate.SalesAmountsHidden || stored?.SalesAmountsHidden == true || !session.HasPermission(AppPermissionNames.AmountViewSales);
        if (stored is null && (purchaseHidden || salesHidden))
            return "신규 자산의 금액을 확정할 서버 계약 기준이 없습니다. 금액 권한이 있는 담당자가 등록해 주세요.";
        candidate.PurchaseAmountsHidden = purchaseHidden;
        candidate.SalesAmountsHidden = salesHidden;
        if (stored is null) return null;
        if (purchaseHidden) candidate.PurchasePrice = stored.PurchasePrice;
        if (salesHidden)
        {
            candidate.SalePrice = stored.SalePrice;
            candidate.MonthlyFee = stored.MonthlyFee;
            candidate.DepositText = stored.DepositText;
            candidate.BlackOverageUnitPrice = stored.BlackOverageUnitPrice;
            candidate.ColorOverageUnitPrice = stored.ColorOverageUnitPrice;
        }
        return null;
    }

    internal static string? ValidateContractChanges(LocalRentalAsset candidate, LocalRentalAsset? stored)
    {
        if (stored is null || !(candidate.PurchaseAmountsHidden || candidate.SalesAmountsHidden)) return null;
        var filledCustomerFromSameProfile = stored.CustomerId is null && stored.BillingProfileId.HasValue && stored.BillingProfileId == candidate.BillingProfileId;
        if ((candidate.ItemId.HasValue && stored.ItemId != candidate.ItemId) ||
            (candidate.CustomerId.HasValue && stored.CustomerId != candidate.CustomerId && !filledCustomerFromSameProfile) ||
            (candidate.BillingProfileId.HasValue && stored.BillingProfileId != candidate.BillingProfileId) ||
            !Same(stored.TenantCode, candidate.TenantCode) || !Same(stored.OfficeCode, candidate.OfficeCode) ||
            !Same(stored.ResponsibleOfficeCode, candidate.ResponsibleOfficeCode) || !Same(stored.ManagementCompanyCode, candidate.ManagementCompanyCode))
            return "금액 비공개 상태에서 자산의 품목·거래처·청구 연결·소유 업체를 변경할 수 없습니다. 계약 기준을 확인해 주세요.";
        if (candidate.SalesAmountsHidden && (stored.ContractMonths != candidate.ContractMonths || stored.ContractDate != candidate.ContractDate ||
            stored.ContractStartDate != candidate.ContractStartDate || stored.RentalEndDate != candidate.RentalEndDate ||
            stored.MeterBillingEnabled != candidate.MeterBillingEnabled ||
            RentalMeterPolicyModes.Normalize(stored.BlackIncludedMode, stored.BlackIncludedPages) != RentalMeterPolicyModes.Normalize(candidate.BlackIncludedMode, candidate.BlackIncludedPages) ||
            stored.BlackIncludedPages != candidate.BlackIncludedPages ||
            RentalMeterPolicyModes.Normalize(stored.ColorIncludedMode, stored.ColorIncludedPages) != RentalMeterPolicyModes.Normalize(candidate.ColorIncludedMode, candidate.ColorIncludedPages) ||
            stored.ColorIncludedPages != candidate.ColorIncludedPages || !Same(stored.BillingEligibilityStatus, candidate.BillingEligibilityStatus)))
            return "금액 비공개 상태에서 계약 기간·청구 대상·기본 매수 정책을 변경할 수 없습니다. 계약 기준을 확인해 주세요.";
        return null;
    }

    internal static bool AssignmentChanged(LocalRentalAsset? stored, LocalRentalAsset candidate)
        => stored is null || stored.CustomerId != candidate.CustomerId || stored.BillingProfileId != candidate.BillingProfileId ||
           !Same(stored.TenantCode, candidate.TenantCode) || !Same(stored.ResponsibleOfficeCode, candidate.ResponsibleOfficeCode) ||
           stored.CustomerName != candidate.CustomerName || stored.CurrentCustomerName != candidate.CurrentCustomerName ||
           stored.InstallLocation != candidate.InstallLocation || stored.InstallSiteName != candidate.InstallSiteName ||
           stored.AssetStatus != candidate.AssetStatus || stored.LastAssignmentClearedAtUtc != candidate.LastAssignmentClearedAtUtc ||
           stored.ItemName != candidate.ItemName || stored.MachineNumber != candidate.MachineNumber || stored.ManagementNumber != candidate.ManagementNumber ||
           stored.ContractStartDate != candidate.ContractStartDate || stored.InstallDate != candidate.InstallDate || stored.RentalEndDate != candidate.RentalEndDate ||
           !candidate.SalesAmountsHidden && stored.MonthlyFee != candidate.MonthlyFee;

    private static bool Same(string? left, string? right)
        => string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
