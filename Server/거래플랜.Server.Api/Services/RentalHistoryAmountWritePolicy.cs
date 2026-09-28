using System.Globalization;
using System.Text.Json;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

// Called after the existing tenant, office and reference authorization checks.
internal static class RentalHistoryAmountWritePolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string? Normalize(RentalAssetAssignmentHistoryDto dto, RentalAssetAssignmentHistory? existing,
        RentalAsset? asset, OfficeScopeService scope)
    {
        if (existing is null && dto.IsDeleted) return null;
        if (scope.CanViewSalesAmounts())
            return dto.MonthlyFee is null ? "비공개 이력 금액은 현재 권한으로 다시 조회한 뒤 저장해 주세요." : null;
        if (existing is not null)
        {
            if (dto.AssetId != existing.AssetId ||
                (dto.BillingProfileId.HasValue && dto.BillingProfileId != existing.BillingProfileId) ||
                (dto.CustomerId.HasValue && dto.CustomerId != existing.CustomerId))
                return "금액 비공개 상태에서 과거 배치 이력을 다른 자산·거래처·청구에 옮길 수 없습니다.";
            dto.MonthlyFee = existing.MonthlyFee;
            return null;
        }
        if (asset is null || asset.IsDeleted || !dto.IsCurrent || dto.IsDeleted ||
            dto.AssetId != asset.Id || dto.BillingProfileId != asset.BillingProfileId || dto.CustomerId != asset.CustomerId ||
            dto.ContractStartDate != asset.ContractStartDate || dto.ContractEndDate != asset.RentalEndDate)
            return "신규 배치 이력의 금액을 확정할 현재 서버 배치를 확인할 수 없습니다. 과거 계약 기준을 확인해 주세요.";
        dto.MonthlyFee = asset.MonthlyFee;
        return null;
    }

    public static string? Normalize(RentalBillingLogDto dto, RentalBillingLog? existing,
        RentalBillingProfile profile, OfficeScopeService scope)
    {
        if (existing is null && dto.IsDeleted) return null;
        if (scope.CanViewSalesAmounts())
            return dto.BilledAmount is null ? "비공개 청구 금액은 현재 권한으로 다시 조회한 뒤 저장해 주세요." : null;
        if (existing is not null)
        {
            if (dto.BillingProfileId != existing.BillingProfileId || dto.BillingYearMonth?.Trim() != existing.BillingYearMonth?.Trim() ||
                dto.ScheduledDate != existing.ScheduledDate)
                return "금액 비공개 상태에서 청구 로그를 다른 청구·월·예정일로 옮길 수 없습니다.";
            dto.BilledAmount = existing.BilledAmount;
            dto.Status = existing.Status;
            dto.ProcessedDate = existing.ProcessedDate;
            dto.ProcessedByUsername = existing.ProcessedByUsername;
            return null;
        }
        const string unavailable = "신규 청구 로그의 금액을 확정할 서버 청구 이력이 없거나 불명확합니다. 최신 청구를 다시 조회해 주세요.";
        if (dto.IsDeleted || dto.BillingYearMonth?.Trim() != dto.ScheduledDate.ToString("yyyy-MM", CultureInfo.InvariantCulture) ||
            !RentalBillingRunTombstonePolicy.ValidateForServerMutation(profile.BillingRunsJson).IsValid)
            return unavailable;
        try
        {
            var runs = JsonSerializer.Deserialize<List<Run>>(profile.BillingRunsJson, JsonOptions);
            var matches = runs?.Where(run => !run.IsTombstoned && run.ScheduledDate == dto.ScheduledDate).ToList();
            if (matches is not { Count: 1 } || matches[0].BilledAmount is not decimal amount || amount < 0m)
                return unavailable;
            var source = matches[0];
            var status = string.IsNullOrWhiteSpace(source.SettlementStatus) ? source.Status : source.SettlementStatus;
            if (string.IsNullOrWhiteSpace(status)) return unavailable;
            dto.BilledAmount = amount;
            dto.Status = status;
            dto.ProcessedDate = source.SettledDate;
            dto.ProcessedByUsername = string.Empty;
            return null;
        }
        catch (JsonException) { return unavailable; }
    }

    private sealed class Run
    {
        public DateOnly? ScheduledDate { get; set; }
        public decimal? BilledAmount { get; set; }
        public string Status { get; set; } = string.Empty;
        public string SettlementStatus { get; set; } = string.Empty;
        public DateOnly? SettledDate { get; set; }
        public bool IsTombstoned { get; set; }
    }
}
