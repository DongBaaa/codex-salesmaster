using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public enum DataIntegrityDirectActionKind
{
    None,
    OpenRentalBillingProfile,
    OpenRentalAsset
}

public static class DataIntegrityIssueCodes
{
    public const string RentalBillingTemplateInvalid = "rental_billing_template_invalid";
    public const string RentalProfileTemplateEmpty = "rental_profile_template_empty";
    public const string RentalProfileMonthlyAmountMismatch = "rental_profile_monthly_amount_mismatch";
    public const string RentalTemplateItemWithoutAsset = "rental_template_item_without_asset";
    public const string RentalTemplateMissingAsset = "rental_template_missing_asset";
    public const string RentalAssetTemplateMonthlyMismatch = "rental_asset_template_monthly_mismatch";
    public const string RentalAssetProfileScopeMismatch = "rental_asset_profile_scope_mismatch";
    public const string RentalOperationalScopeMismatch = "rental_operational_scope_mismatch";
    public const string RentalAssetInMultipleProfileTemplates = "rental_asset_in_multiple_profile_templates";
    public const string RentalProfileWithoutLinkedAssets = "rental_profile_without_linked_assets";
    public const string RentalBillableAssetWithoutMonthlyFee = "rental_billable_asset_without_monthly_fee";
    public const string RentalAssetMissingBillingProfile = "rental_asset_missing_billing_profile";
}

public sealed class DataIntegrityIssueDefinition
{
    public DataIntegrityIssueDefinition(string code, string title, string severity, string area, string description, string suggestedAction)
    {
        Code = code;
        Title = title;
        Severity = severity;
        Area = area;
        Description = description;
        SuggestedAction = suggestedAction;
    }

    public string Code { get; }
    public string Title { get; }
    public string Severity { get; }
    public string Area { get; }
    public string Description { get; }
    public string SuggestedAction { get; }
}

public sealed class DataIntegrityIssueSummary
{
    public string Code { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Severity { get; init; } = "Warning";
    public string Area { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SuggestedAction { get; init; } = string.Empty;
    public int Count { get; init; }
    public bool HasDirectAction { get; init; }

    public string CountText => $"{Count:N0}건";
    public string SeverityDisplay => string.Equals(Severity, "Error", StringComparison.OrdinalIgnoreCase) ? "오류" : "주의";
}

public sealed class DataIntegrityIssueDetail
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Code { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Severity { get; init; } = "Warning";
    public string Area { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public Guid? EntityId { get; init; }
    public Guid? ProfileId { get; init; }
    public Guid? AssetId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string ItemName { get; init; } = string.Empty;
    public string AssetDisplayName { get; init; } = string.Empty;
    public string OfficeCode { get; init; } = string.Empty;
    public string CurrentValue { get; init; } = string.Empty;
    public string ExpectedValue { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string SuggestedAction { get; init; } = string.Empty;
    public DataIntegrityDirectActionKind DirectActionKind { get; init; }

    public bool HasDirectAction => DirectActionKind != DataIntegrityDirectActionKind.None;
    public string SeverityDisplay => string.Equals(Severity, "Error", StringComparison.OrdinalIgnoreCase) ? "오류" : "주의";
    public string DirectActionText => DirectActionKind switch
    {
        DataIntegrityDirectActionKind.OpenRentalAsset => "자산 바로가기",
        DataIntegrityDirectActionKind.OpenRentalBillingProfile => "청구관리 바로가기",
        _ => "수동 확인"
    };
}

public sealed class DataIntegrityIssueFilterOption
{
    public string Code { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    public override string ToString() => DisplayName;
}

public sealed class DataIntegrityScanResult
{
    public DataIntegrityScanResult(DateTime scannedAtLocal, IReadOnlyList<DataIntegrityIssueSummary> summaries, IReadOnlyList<DataIntegrityIssueDetail> issues)
    {
        ScannedAtLocal = scannedAtLocal;
        Summaries = summaries;
        Issues = issues;
        IssueSignature = string.Join("|", summaries.OrderBy(summary => summary.Code).Select(summary => $"{summary.Code}:{summary.Count}"));
    }

    public DateTime ScannedAtLocal { get; }
    public IReadOnlyList<DataIntegrityIssueSummary> Summaries { get; }
    public IReadOnlyList<DataIntegrityIssueDetail> Issues { get; }
    public int TotalIssueCount => Issues.Count;
    public bool HasIssues => Issues.Count > 0;
    public string IssueSignature { get; }
    public string ScannedAtText => ScannedAtLocal.ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class DataIntegrityIssueService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyDictionary<string, DataIntegrityIssueDefinition> Definitions = new Dictionary<string, DataIntegrityIssueDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        [DataIntegrityIssueCodes.RentalBillingTemplateInvalid] = new(
            DataIntegrityIssueCodes.RentalBillingTemplateInvalid,
            "청구 품목 데이터 손상",
            "Error",
            "렌탈 청구",
            "청구 프로필의 품목 JSON을 해석할 수 없어 청구 금액 계산이 불안정합니다.",
            "청구관리에서 해당 프로필을 열고 품목을 다시 저장하세요."),
        [DataIntegrityIssueCodes.RentalProfileTemplateEmpty] = new(
            DataIntegrityIssueCodes.RentalProfileTemplateEmpty,
            "청구 품목 없음",
            "Warning",
            "렌탈 청구",
            "청구 프로필은 있으나 청구서 표시 품목이 비어 있습니다.",
            "청구관리에서 표시 품목과 연결 자산을 확인하세요."),
        [DataIntegrityIssueCodes.RentalProfileMonthlyAmountMismatch] = new(
            DataIntegrityIssueCodes.RentalProfileMonthlyAmountMismatch,
            "월 기준금액 불일치",
            "Warning",
            "렌탈 청구",
            "청구 프로필 월 기준금액과 품목별 수량×단가 합계가 다릅니다.",
            "청구관리에서 품목 단가/수량을 저장해 월 기준금액을 재계산하세요."),
        [DataIntegrityIssueCodes.RentalTemplateItemWithoutAsset] = new(
            DataIntegrityIssueCodes.RentalTemplateItemWithoutAsset,
            "품목-자산 연결 없음",
            "Warning",
            "렌탈 청구",
            "청구서 표시 품목에 연결된 렌탈 자산이 없습니다.",
            "청구관리에서 품목별 연결 자산을 지정하세요."),
        [DataIntegrityIssueCodes.RentalTemplateMissingAsset] = new(
            DataIntegrityIssueCodes.RentalTemplateMissingAsset,
            "삭제/누락 자산 참조",
            "Error",
            "렌탈 청구",
            "청구 품목이 존재하지 않거나 삭제된 자산 ID를 참조합니다.",
            "청구관리에서 해당 품목의 연결 자산을 다시 지정하세요."),
        [DataIntegrityIssueCodes.RentalAssetTemplateMonthlyMismatch] = new(
            DataIntegrityIssueCodes.RentalAssetTemplateMonthlyMismatch,
            "자산 월요금-품목 금액 불일치",
            "Warning",
            "렌탈 청구",
            "연결 자산의 월요금 합계와 청구 품목 금액이 다릅니다.",
            "자산 월요금 또는 청구 품목 단가를 하나의 기준으로 맞춘 뒤 저장하세요."),
        [DataIntegrityIssueCodes.RentalAssetProfileScopeMismatch] = new(
            DataIntegrityIssueCodes.RentalAssetProfileScopeMismatch,
            "자산-청구 프로필 범위 불일치",
            "Error",
            "렌탈 연결",
            "자산에 저장된 청구 프로필/지점/업체 범위가 청구 품목 참조와 다릅니다.",
            "청구관리 또는 자산 화면에서 연결 프로필을 다시 지정하세요."),
        [DataIntegrityIssueCodes.RentalOperationalScopeMismatch] = new(
            DataIntegrityIssueCodes.RentalOperationalScopeMismatch,
            "렌탈 scope 자체 불일치",
            "Error",
            "렌탈 범위",
            "청구 프로필 또는 자산의 tenant·owner·담당지점 값이 서로 맞지 않아 다른 점검 기준도 왜곡될 수 있습니다.",
            "청구관리 또는 자산 화면에서 담당지점을 다시 저장해 canonical scope로 맞추세요."),
        [DataIntegrityIssueCodes.RentalAssetInMultipleProfileTemplates] = new(
            DataIntegrityIssueCodes.RentalAssetInMultipleProfileTemplates,
            "자산 중복 청구 연결",
            "Error",
            "렌탈 연결",
            "하나의 자산이 여러 청구 프로필 품목에 동시에 포함되어 중복 청구 위험이 있습니다.",
            "자산 또는 청구관리에서 실제 청구 대상 프로필 하나만 남기세요."),
        [DataIntegrityIssueCodes.RentalProfileWithoutLinkedAssets] = new(
            DataIntegrityIssueCodes.RentalProfileWithoutLinkedAssets,
            "프로필 연결 자산 없음",
            "Warning",
            "렌탈 연결",
            "청구 프로필에 연결된 자산이 없습니다.",
            "청구관리에서 자산을 연결하거나 더 이상 쓰지 않는 프로필이면 보류/삭제 검토하세요."),
        [DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee] = new(
            DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee,
            "청구대상 자산 월요금 없음",
            "Warning",
            "렌탈 자산",
            "청구대상 자산인데 월요금이 0원입니다.",
            "자산 화면에서 월요금을 입력한 뒤 연결 청구 프로필에 반영하세요."),
        [DataIntegrityIssueCodes.RentalAssetMissingBillingProfile] = new(
            DataIntegrityIssueCodes.RentalAssetMissingBillingProfile,
            "자산의 청구 프로필 누락",
            "Error",
            "렌탈 연결",
            "자산에 저장된 청구 프로필 ID가 현재 DB에 없습니다.",
            "자산 화면에서 청구 연결을 해제하거나 올바른 청구 프로필로 다시 연결하세요.")
    };

    private readonly LocalDbContext _db;

    public DataIntegrityIssueService(LocalDbContext db)
    {
        _db = db;
    }

    public async Task<DataIntegrityScanResult> ScanAsync(SessionState session, CancellationToken ct = default)
    {
        var activeProfiles = await _db.RentalBillingProfiles
            .AsNoTracking()
            .Where(profile => !profile.IsDeleted && profile.IsActive)
            .ToListAsync(ct);
        var activeAssets = await _db.RentalAssets
            .AsNoTracking()
            .Where(asset => !asset.IsDeleted)
            .ToListAsync(ct);

        var scopedProfiles = activeProfiles
            .Where(profile =>
            {
                var profileScope = ResolveProfileScope(profile);
                return IsInSessionScope(profileScope.TenantCode, profileScope.ResponsibleOfficeCode, session);
            })
            .ToList();
        var scopedAssets = activeAssets
            .Where(asset =>
            {
                var assetScope = ResolveAssetScope(asset);
                return IsInSessionScope(assetScope.TenantCode, assetScope.ResponsibleOfficeCode, session);
            })
            .ToList();

        var allAssetsById = activeAssets.ToDictionary(asset => asset.Id);
        var activeProfilesById = activeProfiles.ToDictionary(profile => profile.Id);
        var details = new List<DataIntegrityIssueDetail>();
        var assetTemplateRefs = new Dictionary<Guid, List<AssetTemplateReference>>();

        foreach (var profile in scopedProfiles)
        {
            if (IsProfileScopeInconsistent(profile))
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalOperationalScopeMismatch, profile, null,
                    entityType: "청구 프로필",
                    entityId: profile.Id,
                    currentValue: BuildStoredProfileScopeDisplay(profile),
                    expectedValue: BuildProfileScopeDisplay(profile),
                    message: $"{BuildProfileDisplay(profile)} 프로필의 tenant/owner/담당지점 범위가 내부적으로 섞여 있습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
            }

            var parsed = ParseTemplateItems(profile);
            if (!parsed.Success)
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalBillingTemplateInvalid, profile, null,
                    entityType: "청구 프로필",
                    entityId: profile.Id,
                    currentValue: "템플릿 해석 실패",
                    expectedValue: "정상 JSON",
                    message: $"{BuildProfileDisplay(profile)} 청구 품목 데이터를 해석할 수 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
                continue;
            }

            var templateItems = parsed.Items;
            var profileAssetIds = new HashSet<Guid>();
            var linkedAssets = scopedAssets
                .Where(asset => asset.BillingProfileId == profile.Id)
                .ToList();

            if (templateItems.Count == 0)
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalProfileTemplateEmpty, profile, null,
                    entityType: "청구 프로필",
                    entityId: profile.Id,
                    currentValue: "0개",
                    expectedValue: "1개 이상",
                    message: $"{BuildProfileDisplay(profile)} 청구서 표시 품목이 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
            }

            var templateMonthly = templateItems.Sum(ResolveTemplateMonthlyAmount);
            if (templateItems.Count > 0 && AmountDiffers(profile.MonthlyAmount, templateMonthly))
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalProfileMonthlyAmountMismatch, profile, null,
                    entityType: "청구 프로필",
                    entityId: profile.Id,
                    currentValue: FormatMoney(profile.MonthlyAmount),
                    expectedValue: FormatMoney(templateMonthly),
                    message: $"{BuildProfileDisplay(profile)} 월 기준금액이 품목 합계와 다릅니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
            }

            foreach (var item in templateItems)
            {
                var itemAssetIds = item.IncludedAssetIds?
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList() ?? new List<Guid>();

                if (itemAssetIds.Count == 0)
                {
                    AddIssue(details, DataIntegrityIssueCodes.RentalTemplateItemWithoutAsset, profile, null,
                        entityType: "청구 품목",
                        entityId: profile.Id,
                        itemName: item.DisplayItemName,
                        currentValue: "연결 자산 0개",
                        expectedValue: "연결 자산 1개 이상",
                        message: $"{BuildProfileDisplay(profile)} / {NormalizeDisplay(item.DisplayItemName, "품목")} 품목에 연결 자산이 없습니다.",
                        directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
                    continue;
                }

                var existingItemAssets = new List<LocalRentalAsset>();
                foreach (var assetId in itemAssetIds)
                {
                    profileAssetIds.Add(assetId);
                    if (!allAssetsById.TryGetValue(assetId, out var asset))
                    {
                        AddIssue(details, DataIntegrityIssueCodes.RentalTemplateMissingAsset, profile, null,
                            entityType: "청구 품목",
                            entityId: profile.Id,
                            itemName: item.DisplayItemName,
                            currentValue: assetId.ToString("D"),
                            expectedValue: "활성 렌탈 자산",
                            message: $"{BuildProfileDisplay(profile)} / {NormalizeDisplay(item.DisplayItemName, "품목")} 품목이 누락 자산을 참조합니다.",
                            directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
                        continue;
                    }

                    existingItemAssets.Add(asset);
                    if (!assetTemplateRefs.TryGetValue(asset.Id, out var refs))
                    {
                        refs = new List<AssetTemplateReference>();
                        assetTemplateRefs[asset.Id] = refs;
                    }

                    refs.Add(new AssetTemplateReference(profile.Id, BuildProfileDisplay(profile), item.DisplayItemName));

                    var profileScope = ResolveProfileScope(profile);
                    var assetScope = ResolveAssetScope(asset);
                    var profileIdMatches = !asset.BillingProfileId.HasValue || asset.BillingProfileId == profile.Id;
                    var tenantMatches = string.Equals(profileScope.TenantCode, assetScope.TenantCode, StringComparison.OrdinalIgnoreCase);
                    var officeMatches = string.Equals(profileScope.ResponsibleOfficeCode, assetScope.ResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase);
                    if (!profileIdMatches || !tenantMatches || !officeMatches)
                    {
                        AddIssue(details, DataIntegrityIssueCodes.RentalAssetProfileScopeMismatch, profile, asset,
                            entityType: "자산 연결",
                            entityId: asset.Id,
                            itemName: item.DisplayItemName,
                            currentValue: BuildAssetScopeDisplay(asset),
                            expectedValue: BuildProfileScopeDisplay(profile),
                            message: $"{BuildAssetDisplay(asset)} 자산의 청구 연결/범위가 {BuildProfileDisplay(profile)} 프로필과 다릅니다.",
                            directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
                    }
                }

                var assetMonthlySum = existingItemAssets.Sum(asset => Math.Max(0m, asset.MonthlyFee));
                var itemMonthly = ResolveTemplateMonthlyAmount(item);
                if (existingItemAssets.Count > 0 && assetMonthlySum > 0m && AmountDiffers(assetMonthlySum, itemMonthly))
                {
                    AddIssue(details, DataIntegrityIssueCodes.RentalAssetTemplateMonthlyMismatch, profile, existingItemAssets.FirstOrDefault(),
                        entityType: "청구 품목",
                        entityId: profile.Id,
                        itemName: item.DisplayItemName,
                        currentValue: FormatMoney(itemMonthly),
                        expectedValue: FormatMoney(assetMonthlySum),
                        message: $"{BuildProfileDisplay(profile)} / {NormalizeDisplay(item.DisplayItemName, "품목")} 품목 금액이 연결 자산 월요금 합계와 다릅니다.",
                        directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
                }
            }

            if (linkedAssets.Count == 0 && profileAssetIds.Count == 0)
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalProfileWithoutLinkedAssets, profile, null,
                    entityType: "청구 프로필",
                    entityId: profile.Id,
                    currentValue: "연결 자산 0개",
                    expectedValue: "연결 자산 1개 이상",
                    message: $"{BuildProfileDisplay(profile)} 프로필에 연결된 자산이 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
            }
        }

        foreach (var group in assetTemplateRefs.Where(pair => pair.Value.Select(reference => reference.ProfileId).Distinct().Count() > 1))
        {
            if (!allAssetsById.TryGetValue(group.Key, out var asset))
                continue;
            var assetScope = ResolveAssetScope(asset);
            if (!IsInSessionScope(assetScope.TenantCode, assetScope.ResponsibleOfficeCode, session))
                continue;

            var distinctProfiles = group.Value
                .GroupBy(reference => reference.ProfileId)
                .Select(grouping => grouping.First().ProfileDisplayName)
                .ToList();
            AddIssue(details, DataIntegrityIssueCodes.RentalAssetInMultipleProfileTemplates, null, asset,
                entityType: "렌탈 자산",
                entityId: asset.Id,
                currentValue: string.Join(" / ", distinctProfiles.Take(3)),
                expectedValue: "청구 프로필 1개",
                message: $"{BuildAssetDisplay(asset)} 자산이 여러 청구 프로필 품목에 포함되어 있습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenRentalAsset);
        }

        foreach (var asset in scopedAssets)
        {
            if (IsAssetScopeInconsistent(asset))
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalOperationalScopeMismatch, null, asset,
                    entityType: "렌탈 자산",
                    entityId: asset.Id,
                    currentValue: BuildStoredAssetScopeDisplay(asset),
                    expectedValue: BuildAssetScopeDisplay(asset),
                    message: $"{BuildAssetDisplay(asset)} 자산의 tenant/owner/담당지점 범위가 내부적으로 섞여 있습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalAsset);
            }

            if (asset.BillingProfileId.HasValue && !activeProfilesById.ContainsKey(asset.BillingProfileId.Value))
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalAssetMissingBillingProfile, null, asset,
                    entityType: "렌탈 자산",
                    entityId: asset.Id,
                    currentValue: asset.BillingProfileId.Value.ToString("D"),
                    expectedValue: "활성 청구 프로필",
                    message: $"{BuildAssetDisplay(asset)} 자산에 저장된 청구 프로필을 찾을 수 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalAsset);
            }

            if (IsBillableOperatingAsset(asset) && asset.MonthlyFee <= 0m)
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee, null, asset,
                    entityType: "렌탈 자산",
                    entityId: asset.Id,
                    currentValue: FormatMoney(asset.MonthlyFee),
                    expectedValue: "1원 이상",
                    message: $"{BuildAssetDisplay(asset)} 자산은 청구대상이나 월요금이 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalAsset);
            }
        }

        var summaries = details
            .GroupBy(issue => issue.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var definition = GetDefinition(group.Key);
                return new DataIntegrityIssueSummary
                {
                    Code = definition.Code,
                    Title = definition.Title,
                    Severity = definition.Severity,
                    Area = definition.Area,
                    Description = definition.Description,
                    SuggestedAction = definition.SuggestedAction,
                    Count = group.Count(),
                    HasDirectAction = group.Any(issue => issue.HasDirectAction)
                };
            })
            .OrderByDescending(summary => string.Equals(summary.Severity, "Error", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(summary => summary.Count)
            .ThenBy(summary => summary.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new DataIntegrityScanResult(DateTime.Now, summaries, details);
    }

    public static DataIntegrityIssueDefinition GetDefinition(string code)
        => Definitions.TryGetValue(code, out var definition)
            ? definition
            : new DataIntegrityIssueDefinition(code, code, "Warning", "기타", "정의되지 않은 점검 항목입니다.", "상세 내용을 확인하세요.");

    private static void AddIssue(
        ICollection<DataIntegrityIssueDetail> issues,
        string code,
        LocalRentalBillingProfile? profile,
        LocalRentalAsset? asset,
        string entityType,
        Guid? entityId,
        string? itemName = null,
        string currentValue = "",
        string expectedValue = "",
        string message = "",
        DataIntegrityDirectActionKind directActionKind = DataIntegrityDirectActionKind.None)
    {
        var definition = GetDefinition(code);
        issues.Add(new DataIntegrityIssueDetail
        {
            Code = definition.Code,
            Title = definition.Title,
            Severity = definition.Severity,
            Area = definition.Area,
            EntityType = entityType,
            EntityId = entityId,
            ProfileId = profile?.Id,
            AssetId = asset?.Id,
            CustomerName = profile?.CustomerName ?? asset?.CurrentCustomerName ?? asset?.CustomerName ?? string.Empty,
            ItemName = NormalizeDisplay(itemName, profile?.ItemName ?? asset?.ItemName ?? string.Empty),
            AssetDisplayName = asset is null ? string.Empty : BuildAssetDisplay(asset),
            OfficeCode = profile is null ? (asset is null ? string.Empty : ResolveAssetOfficeCode(asset)) : ResolveProfileOfficeCode(profile),
            CurrentValue = currentValue,
            ExpectedValue = expectedValue,
            Message = message,
            SuggestedAction = definition.SuggestedAction,
            DirectActionKind = directActionKind
        });
    }

    private static ParsedTemplateItems ParseTemplateItems(LocalRentalBillingProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.BillingTemplateJson))
            return new ParsedTemplateItems(true, []);

        try
        {
            var parsed = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(profile.BillingTemplateJson, JsonOptions) ?? [];
            var normalized = parsed
                .Where(item => item is not null)
                .Select(item => new RentalBillingTemplateItemModel
                {
                    ItemId = item.ItemId == Guid.Empty ? Guid.NewGuid() : item.ItemId,
                    DisplayItemName = NormalizeDisplay(item.DisplayItemName, profile.ItemName),
                    BillingLineMode = item.BillingLineMode ?? string.Empty,
                    Quantity = item.Quantity <= 0m ? 1m : item.Quantity,
                    UnitPrice = Math.Max(0m, item.UnitPrice),
                    Amount = Math.Max(0m, item.Amount),
                    Note = item.Note ?? string.Empty,
                    IncludedAssetIds = item.IncludedAssetIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? []
                })
                .ToList();
            return new ParsedTemplateItems(true, normalized);
        }
        catch
        {
            return new ParsedTemplateItems(false, []);
        }
    }

    private static bool IsInSessionScope(string? tenantCode, string? officeCode, SessionState session)
    {
        if (session.HasGlobalDataScope)
            return true;

        var normalizedOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, session.OfficeCode);
        var normalizedTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, normalizedOffice);
        var sessionTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);
        if (!string.Equals(normalizedTenant, sessionTenant, StringComparison.OrdinalIgnoreCase))
            return false;

        var offices = TenantScopeCatalog.ResolveScopedOfficeCodes(
            session.OfficeCode,
            session.TenantCode,
            session.ScopeType,
            hasGlobalScope: false,
            hasTenantScope: string.Equals(TenantScopeCatalog.NormalizeScopeTypeOrDefault(session.ScopeType), TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase));
        return offices.Contains(normalizedOffice);
    }

    private static bool IsBillableOperatingAsset(LocalRentalAsset asset)
    {
        var status = (asset.AssetStatus ?? string.Empty).Trim();
        if (RentalAssetStatusRules.IsNonOperating(status))
            return false;

        var eligibility = (asset.BillingEligibilityStatus ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(eligibility)
               || string.Equals(eligibility, "청구대상", StringComparison.OrdinalIgnoreCase)
               || string.Equals(eligibility, "청구 대상", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal ResolveTemplateMonthlyAmount(RentalBillingTemplateItemModel item)
    {
        var quantity = item.Quantity <= 0m ? 1m : item.Quantity;
        var unitPrice = Math.Max(0m, item.UnitPrice);
        var calculated = quantity * unitPrice;
        return calculated > 0m ? calculated : Math.Max(0m, item.Amount);
    }

    private static bool AmountDiffers(decimal left, decimal right)
        => Math.Abs(left - right) >= 1m;

    private static RentalOperationalScope ResolveProfileScope(LocalRentalBillingProfile profile)
        => RentalScopeNormalizer.ResolveScope(
            profile.TenantCode,
            profile.OfficeCode,
            profile.ManagementCompanyCode,
            profile.ResponsibleOfficeCode,
            OfficeCodeCatalog.Usenet);

    private static RentalOperationalScope ResolveAssetScope(LocalRentalAsset asset)
        => RentalScopeNormalizer.ResolveScope(
            asset.TenantCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode,
            asset.ResponsibleOfficeCode,
            OfficeCodeCatalog.Usenet);

    private static string ResolveProfileOfficeCode(LocalRentalBillingProfile profile)
        => ResolveProfileScope(profile).ResponsibleOfficeCode;

    private static string ResolveAssetOfficeCode(LocalRentalAsset asset)
        => ResolveAssetScope(asset).ResponsibleOfficeCode;

    private static string BuildProfileDisplay(LocalRentalBillingProfile profile)
    {
        var customer = NormalizeDisplay(profile.CustomerName, "거래처 미지정");
        var site = NormalizeDisplay(profile.InstallSiteName, string.Empty);
        var item = NormalizeDisplay(profile.ItemName, string.Empty);
        return string.Join(" / ", new[] { customer, site, item }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildAssetDisplay(LocalRentalAsset asset)
    {
        var number = NormalizeDisplay(asset.ManagementNumber, asset.ManagementId);
        var customer = NormalizeDisplay(asset.CurrentCustomerName, asset.CustomerName);
        var item = NormalizeDisplay(asset.ItemName, "품목 미지정");
        return string.Join(" / ", new[] { number, customer, item }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static bool IsProfileScopeInconsistent(LocalRentalBillingProfile profile)
    {
        var canonicalScope = ResolveProfileScope(profile);
        return RequiresExactTenantCode(profile.TenantCode, canonicalScope.TenantCode) ||
               RequiresExactOfficeCode(profile.OfficeCode, canonicalScope.OwnerOfficeCode) ||
               RequiresExactOfficeCode(profile.ManagementCompanyCode, canonicalScope.OwnerOfficeCode) ||
               RequiresExactOfficeCode(profile.ResponsibleOfficeCode, canonicalScope.ResponsibleOfficeCode);
    }

    private static bool IsAssetScopeInconsistent(LocalRentalAsset asset)
    {
        var canonicalScope = ResolveAssetScope(asset);
        return RequiresExactTenantCode(asset.TenantCode, canonicalScope.TenantCode) ||
               RequiresExactOfficeCode(asset.OfficeCode, canonicalScope.OwnerOfficeCode) ||
               RequiresExactOfficeCode(asset.ManagementCompanyCode, canonicalScope.OwnerOfficeCode) ||
               RequiresExactOfficeCode(asset.ResponsibleOfficeCode, canonicalScope.ResponsibleOfficeCode);
    }

    private static string BuildProfileScopeDisplay(LocalRentalBillingProfile profile)
    {
        var scope = ResolveProfileScope(profile);
        return $"{scope.TenantCode} / {scope.OwnerOfficeCode} / {scope.ResponsibleOfficeCode} / 프로필 {profile.Id:D}";
    }

    private static string BuildStoredProfileScopeDisplay(LocalRentalBillingProfile profile)
        => $"{NormalizeTenantForDisplay(profile.TenantCode, profile.OfficeCode, profile.ResponsibleOfficeCode)} / {NormalizeOfficeForDisplay(profile.OfficeCode, profile.ManagementCompanyCode)} / {NormalizeOfficeForDisplay(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode)} / 프로필 {profile.Id:D}";

    private static string BuildAssetScopeDisplay(LocalRentalAsset asset)
    {
        var scope = ResolveAssetScope(asset);
        var billingProfileText = asset.BillingProfileId.HasValue ? asset.BillingProfileId.Value.ToString("D") : "미연결";
        return $"{scope.TenantCode} / {scope.OwnerOfficeCode} / {scope.ResponsibleOfficeCode} / 프로필 {billingProfileText}";
    }

    private static string BuildStoredAssetScopeDisplay(LocalRentalAsset asset)
    {
        var billingProfileText = asset.BillingProfileId.HasValue ? asset.BillingProfileId.Value.ToString("D") : "미연결";
        return $"{NormalizeTenantForDisplay(asset.TenantCode, asset.OfficeCode, asset.ResponsibleOfficeCode)} / {NormalizeOfficeForDisplay(asset.OfficeCode, asset.ManagementCompanyCode)} / {NormalizeOfficeForDisplay(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode)} / 프로필 {billingProfileText}";
    }

    private static string NormalizeOfficeForDisplay(string? officeCode, string? fallbackOfficeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, fallbackOfficeCode);

    private static string NormalizeTenantForDisplay(string? tenantCode, string? officeCode, string? responsibleOfficeCode)
        => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode, tenantCode, responsibleOfficeCode);

    private static bool RequiresExactOfficeCode(string? currentOfficeCode, string expectedOfficeCode)
        => !OfficeCodeCatalog.TryNormalizeOfficeCode(currentOfficeCode, out var normalizedOfficeCode) ||
           !string.Equals(normalizedOfficeCode, expectedOfficeCode, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals((currentOfficeCode ?? string.Empty).Trim(), expectedOfficeCode, StringComparison.OrdinalIgnoreCase);

    private static bool RequiresExactTenantCode(string? currentTenantCode, string expectedTenantCode)
        => !TenantScopeCatalog.TryNormalizeTenantCode(currentTenantCode, out var normalizedTenantCode) ||
           !string.Equals(normalizedTenantCode, expectedTenantCode, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals((currentTenantCode ?? string.Empty).Trim(), expectedTenantCode, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDisplay(string? value, string fallback)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
    }

    private static string FormatMoney(decimal value)
        => $"{value:N0}원";

    private sealed record ParsedTemplateItems(bool Success, List<RentalBillingTemplateItemModel> Items);

    private sealed record AssetTemplateReference(Guid ProfileId, string ProfileDisplayName, string ItemName);
}
