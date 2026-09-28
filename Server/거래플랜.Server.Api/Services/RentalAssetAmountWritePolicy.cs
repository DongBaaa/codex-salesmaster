using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

// Normalize a scope-checked command, never the tracked entity. The sync receipt
// continues to bind the original client payload before this normalization.
internal static class RentalAssetAmountWritePolicy
{
    public static string? Normalize(RentalAssetDto dto, RentalAsset? existing, OfficeScopeService scope)
    {
        var purchase = scope.CanViewPurchaseAmounts();
        var sales = scope.CanViewSalesAmounts();
        // Missing-row tombstones are acknowledged without creating an asset.
        if (existing is null && dto.IsDeleted) return null;
        if ((purchase && dto.PurchaseAmountsHidden) || (sales && dto.SalesAmountsHidden))
            return "비공개 금액이 포함되어 있습니다. 현재 권한으로 다시 조회한 뒤 저장해 주세요.";
        if (purchase && sales) return null;
        if (existing is null)
            return "신규 자산의 금액을 확정할 서버 계약 기준이 없습니다. 금액 권한이 있는 담당자가 등록해 주세요.";

        // Reference authorization and template coverage run first. Clearing an
        // invalid item link or an authorized unlink retains the stored money.
        // The unchanged server profile may fill a legacy missing customer link.
        var filledCustomerFromSameProfile = existing.CustomerId is null &&
            existing.BillingProfileId.HasValue && existing.BillingProfileId == dto.BillingProfileId;
        if ((dto.ItemId.HasValue && existing.ItemId != dto.ItemId) ||
            (dto.CustomerId.HasValue && existing.CustomerId != dto.CustomerId && !filledCustomerFromSameProfile) ||
            (dto.BillingProfileId.HasValue && existing.BillingProfileId != dto.BillingProfileId) ||
            !SameCode(existing.TenantCode, dto.TenantCode) || !SameCode(existing.OfficeCode, dto.OfficeCode) ||
            !SameCode(existing.ResponsibleOfficeCode, dto.ResponsibleOfficeCode) ||
            !SameCode(existing.ManagementCompanyCode, dto.ManagementCompanyCode))
            return "금액 비공개 상태에서 자산의 품목·거래처·청구 연결·소유 업체를 변경할 수 없습니다. 계약 기준을 확인해 주세요.";

        if (!sales && (existing.ContractMonths != dto.ContractMonths || existing.ContractDate != dto.ContractDate ||
            existing.ContractStartDate != dto.ContractStartDate || existing.RentalEndDate != dto.RentalEndDate ||
            existing.MeterBillingEnabled != dto.MeterBillingEnabled ||
            RentalMeterPolicyModes.Normalize(existing.BlackIncludedMode, existing.BlackIncludedPages) != RentalMeterPolicyModes.Normalize(dto.BlackIncludedMode, dto.BlackIncludedPages) ||
            existing.BlackIncludedPages != dto.BlackIncludedPages ||
            RentalMeterPolicyModes.Normalize(existing.ColorIncludedMode, existing.ColorIncludedPages) != RentalMeterPolicyModes.Normalize(dto.ColorIncludedMode, dto.ColorIncludedPages) ||
            existing.ColorIncludedPages != dto.ColorIncludedPages ||
            !SameCode(existing.BillingEligibilityStatus, dto.BillingEligibilityStatus)))
            return "금액 비공개 상태에서 계약 기간·청구 대상·기본 매수 정책을 변경할 수 없습니다. 계약 기준을 확인해 주세요.";

        if (!purchase) dto.PurchasePrice = existing.PurchasePrice;
        if (!sales)
        {
            dto.SalePrice = existing.SalePrice;
            dto.MonthlyFee = existing.MonthlyFee;
            dto.DepositText = existing.DepositText;
            dto.BlackOverageUnitPrice = existing.BlackOverageUnitPrice;
            dto.ColorOverageUnitPrice = existing.ColorOverageUnitPrice;
        }
        return null;
    }

    private static bool SameCode(string? left, string? right)
        => string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
