using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using ExcelDataReader;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class RentalStateService
{
    public const string AutoCreatedRentalItemMemo = "렌탈 자산/설치현황 자동 동기화 생성";
    public const int AssetListResultLimit = 1200;
    public const int AssetSearchResultLimit = 300;
    public const int AssetLinkCandidateResultLimit = 600;
    public const int BillingProfileListResultLimit = AssetListResultLimit;
    public const int BillingProfileSearchResultLimit = AssetSearchResultLimit;
    public const int BillingUnlinkedDefaultResultLimit = 300;
    public const int BillingUnlinkedFocusedResultLimit = AssetListResultLimit;
    public const int EquipmentDetailAssetLimit = 300;
    private const int BillingAssetCandidateResultLimit = 300;
    private const int LocalQueryContainsBatchSize = 500;
    private const int BillingRunReferenceBatchSize = 500;
    private const int AssetSearchCustomerMatchLimit = 600;
    private const string AlertDaysSettingKey = "Rental.AlertDaysBefore";
    private const string BillingWorkbookPathSettingKey = "Rental.ImportBillingWorkbookPath";
    private const string AssetWorkbookPathSettingKey = "Rental.ImportAssetWorkbookPath";
    private const string BillingEditorDraftSettingPrefix = "Rental.BillingEditorDraft";
    private const string OnboardingDraftSettingPrefix = "Rental.OnboardingDraft";
    private const string BillingEligibilityExcluded = "청구제외";
    private const string BillingEligibilityTarget = "청구대상";
    private const string BillingEligibilityUnconfirmed = "미확인";
    private const string BillingListCleanupExclusionReason = "청구관리 목록 정리";
    private const string BillingProfileDeleteExclusionReason = "청구 프로필 삭제로 청구목록 제외";
    private static readonly TimeZoneInfo KoreaTimeZone = ResolveKoreaTimeZone();
    private static readonly SemaphoreSlim AssetSaveLock = new(1, 1);
    private static readonly JsonSerializerOptions RentalJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private sealed record RentalBillingCustomerLookup(Guid Id, string? NameOriginal, string? BusinessNumber);

    private sealed record RentalBillingProfileDisplayLookup(
        Guid Id,
        Guid? CustomerId,
        string? CustomerName,
        string? ProfileKey,
        string? InstallSiteName);

    private sealed record RentalCustomerCandidateLookup(Guid Id, string? NameOriginal, string? BusinessNumber);

    private static readonly IReadOnlyDictionary<string, string> ImportLocationStatusMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["렌탈"] = "임대진행중",
        ["창고"] = "창고",
        ["판매"] = "판매",
        ["폐기"] = "폐기"
    };
    private static readonly string[] NonOperatingAssetStatusQueryValues =
    [
        "미배정",
        "대기",
        "회수",
        "창고",
        "폐기"
    ];
    private static readonly IReadOnlyList<string> RentalEquipmentReplacementCandidateStatusValues =
        ExpandAssetStatusFilterValues(["창고"])
            .Concat(["", "점검중", "설치처 불명"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    private static readonly IReadOnlyDictionary<string, string> ImportManagementOfficeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["아이티월드"] = DomainConstants.OfficeItworld,
        ["ITWORLD"] = DomainConstants.OfficeItworld,
        ["유즈넷"] = DomainConstants.OfficeUsenet,
        ["USENET"] = DomainConstants.OfficeUsenet,
        ["연수구"] = DomainConstants.OfficeYeonsu,
        ["YEONSU"] = DomainConstants.OfficeYeonsu
    };
    private static readonly IReadOnlyDictionary<string, string[]> RentalOfficeQueryAliasMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        [DomainConstants.OfficeUsenet] = [string.Empty, DomainConstants.OfficeUsenet, "UZNET", "유즈넷"],
        [DomainConstants.OfficeItworld] = [DomainConstants.OfficeItworld, "아이티월드"],
        [DomainConstants.OfficeYeonsu] = [DomainConstants.OfficeYeonsu, "연수구", "연수구 사무실"]
    };
    private static readonly IReadOnlyDictionary<string, string[]> WorkbookCustomerAliasMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["[검사소]서인천"] = ["[한국교통안전공단]서인천검사소"],
        ["[검사소]인천"] = ["[한국교통안전공단]서인천검사소"],
        ["[검사소]부천"] = ["[한국교통안전공단]부천검사소"],
        ["[연수구]세무과"] = ["연수구청[세무1과]"],
        ["[연수구]송도행정과"] = ["연수구청[송도행정과]"],
        ["[연수구]자치행정과"] = ["연수구청[자치행정과]"],
        ["[연수구]노인장애인과"] = ["연수구청[노인장애인과]"],
        ["[연수구]사회보장과"] = ["연수구청[사회보장과]"],
        ["[연수구]출산보육과"] = ["연수구청[출산보육과]"],
        ["[연수구]복지정책과"] = ["연수구청[복지정책과]"],
        ["[연수구]토지정보과"] = ["연수구청[토지정보과]"],
        ["[연수구]문화관광과"] = ["연수구청[문화관광과]"],
        ["[연수구]민원여권과"] = ["연수구청[민원여권과]"],
        ["[연수구]송도도시관리과"] = ["연수구청[송도도시관리과]"],
        ["[연수구]송도생활지원과"] = ["연수구청[송도생활지원과]"],
        ["[연수구]송도국제도시도서관"] = ["연수구립도서관[송도국제도시도서관]"],
        ["[연수구]청학도서관"] = ["연수구립도서관[청학도서관]"],
        ["[연수구]연수2동행정복지센터"] = ["연수구청[연수2동행정복지센터]"],
        ["[연수구]여성아동과"] = ["연수구청[여성아동과]"],
        ["[연수구]주택과"] = ["연수구청[주택과]"],
        ["[연수구]청소행정과"] = ["연수구청[청소행정과]"],
        ["[연수구]홍보소통실"] = ["연수구청[홍보소통실]"],
        ["[보건환경연구원]총무과"] = ["인천보건환경연구원[총무과]"],
        ["[보건환경연구원]식품분석과"] = ["인천보건환경연구원[식품분석과]"],
        ["[보건환경연구원]대기평가과"] = ["인천보건환경연구원[대기평가과]"],
        ["[보건환경연구원]산업환경과"] = ["인천보건환경연구원[산업환경과]"],
        ["[보건환경연구원]삼산동농산물검사소"] = ["인천보건환경연구원[삼산동농산물검사소]"],
        ["[보건환경연구원]신종감염병과"] = ["인천보건환경연구원[신종감염병과]"],
        ["[보건환경연구원]해양조사과"] = ["인천보건환경연구원[해양조사과]"],
        ["[보건환경연구원]환경조사과"] = ["인천보건환경연구원[환경조사과]"],
        ["[종합건설본부]토목부"] = ["종합건설본부 토목부[도로건설4팀]"],
        ["[상수도사업소]중부수도사업소"] = ["상수도사업본부 중부수도사업소"],
        ["[상수도사업소]수도시설관리소"] = ["상수도사업본부 수도시설관리소", "유즈넷-[상수도]수도시설관리소"],
        ["[상수도사업소]맑은물연구소"] = ["상수도사업본부 맑은물연구소", "유즈넷-[상수도]맑은물연구소"],
        ["[미추홀구]시설관리공단-견인차량보관소"] = ["미추홀구 시설관리공단"],
        ["[미추홀구]시설관리공단-국민체육센터"] = ["미추홀구시설관리공단[국민체육센터]"],
        ["대한미용사회"] = ["사)대한미용사회 인천미추홀구지회"],
        ["[경제청]미디어문화과-기자실"] = ["[경제청]미디어문화과"],
        ["[미추홀구]안전총괄과-CCTV"] = ["[미추홀구]안전총괄과-CCTV관제센터"],
        ["[연수구]노인복지관"] = ["연수노인복지관", "유즈넷-[연수구]노인복지관"],
        ["[연수구]보건소-건강증진과"] = ["연수구청[건강증진과]"],
        ["[연수구]장애인체육회"] = ["인천광역시 연수구장애인체육회"],
        ["DWL관세사무소"] = ["DWL 관세사무소"],
        ["나인정보기술"] = ["(주)나인정보기술"],
        ["㈜대우로지스틱스 인천지사"] = ["(주)대우로지스틱스인천지사"],
        ["㈜유와이에스오션"] = ["(주)유와이에스오션"],
        ["㈜코세스"] = ["(주)코세스"],
        ["명성다이케스팅"] = ["명성다이캐스팅"],
        ["[보건환경연구원]기후대기과"] = ["인천보건환경연구원[기후대기과]"],
        ["[보건환경연구원]약품분석과"] = ["인천보건환경연구원[약품분석과]"],
        ["[보건환경연구원]질병조사과"] = ["인천보건환경연구원[질병조사과]"]
    };
    private readonly LocalDbContext _db;
    private readonly LocalStateService? _local;
    private readonly IServiceProvider? _serviceProvider;
    private bool _legacyAssignedUsernameCleanupCompleted;
    private IReadOnlyDictionary<string, string>? _officeMapCache;

    public RentalStateService(LocalDbContext db)
        : this(db, null, null)
    {
    }

    public RentalStateService(LocalDbContext db, LocalStateService? local)
        : this(db, local, null)
    {
    }

    public RentalStateService(LocalDbContext db, LocalStateService? local, IServiceProvider? serviceProvider)
    {
        _db = db;
        _local = local;
        _serviceProvider = serviceProvider;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<IReadOnlyList<LocalRentalManagementCompany>> GetManagementCompaniesAsync(CancellationToken ct = default)
        => await _db.RentalManagementCompanies
            .AsNoTracking()
            .OrderBy(company => company.Name)
            .ToListAsync(ct);

    public async Task<int> CleanupLegacyAssignedUsernamesAsync(CancellationToken ct = default)
    {
        if (_legacyAssignedUsernameCleanupCompleted)
            return 0;

        var hasProfileColumn = await HasLegacyAssignedUsernameColumnAsync("RentalBillingProfiles", ct);
        var hasAssetColumn = await HasLegacyAssignedUsernameColumnAsync("RentalAssets", ct);
        var hasLogColumn = await HasLegacyAssignedUsernameColumnAsync("RentalBillingLogs", ct);
        if (!hasProfileColumn && !hasAssetColumn && !hasLogColumn)
        {
            _legacyAssignedUsernameCleanupCompleted = true;
            return 0;
        }

        var now = DateTime.UtcNow;
        var changed = 0;
        if (hasProfileColumn)
        {
            changed += await _db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""RentalBillingProfiles""
SET ""AssignedUsername"" = '', ""UpdatedAtUtc"" = {now}, ""IsDirty"" = 1
WHERE ""AssignedUsername"" <> '';", ct);
        }

        if (hasAssetColumn)
        {
            changed += await _db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""RentalAssets""
SET ""AssignedUsername"" = '', ""UpdatedAtUtc"" = {now}, ""IsDirty"" = 1
WHERE ""AssignedUsername"" <> '';", ct);
        }

        if (hasLogColumn)
        {
            changed += await _db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""RentalBillingLogs""
SET ""AssignedUsername"" = '', ""UpdatedAtUtc"" = {now}, ""IsDirty"" = 1
WHERE ""AssignedUsername"" <> '';", ct);
        }

        _legacyAssignedUsernameCleanupCompleted = true;
        return changed;
    }

    private async Task<bool> HasLegacyAssignedUsernameColumnAsync(string tableName, CancellationToken ct)
    {
        await using var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader[1]?.ToString(), "AssignedUsername", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public async Task<string> GetAlertDaysTextAsync(CancellationToken ct = default)
        => await _db.Settings.AsNoTracking()
               .Where(setting => setting.Key == AlertDaysSettingKey)
               .Select(setting => setting.Value)
               .FirstOrDefaultAsync(ct)
           ?? "7,3,1,0";

    public async Task SaveAlertDaysTextAsync(string value, CancellationToken ct = default)
    {
        var normalized = NormalizeAlertDaysText(value);
        await UpsertSettingAsync(AlertDaysSettingKey, normalized, ct);
    }

    public async Task<(string BillingPath, string AssetPath)> GetImportPathsAsync(CancellationToken ct = default)
    {
        var billingPath = await _db.Settings.AsNoTracking()
            .Where(setting => setting.Key == BillingWorkbookPathSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct)
            ?? string.Empty;
        var assetPath = await _db.Settings.AsNoTracking()
            .Where(setting => setting.Key == AssetWorkbookPathSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct)
            ?? string.Empty;
        return (billingPath, assetPath);
    }

    public async Task SaveImportPathsAsync(string billingPath, string assetPath, CancellationToken ct = default)
    {
        await UpsertSettingAsync(BillingWorkbookPathSettingKey, billingPath ?? string.Empty, ct);
        await UpsertSettingAsync(AssetWorkbookPathSettingKey, assetPath ?? string.Empty, ct);
    }

    public async Task<RentalBillingEditorDraftModel?> GetBillingEditorDraftAsync(SessionState session, CancellationToken ct = default)
        => await GetDraftAsync<RentalBillingEditorDraftModel>(BuildDraftSettingKey(BillingEditorDraftSettingPrefix, session), ct);

    public async Task SaveBillingEditorDraftAsync(RentalBillingEditorDraftModel draft, SessionState session, CancellationToken ct = default)
        => await UpsertSettingAsync(
            BuildDraftSettingKey(BillingEditorDraftSettingPrefix, session),
            JsonSerializer.Serialize(draft, RentalJsonOptions),
            ct);

    public async Task ClearBillingEditorDraftAsync(SessionState session, CancellationToken ct = default)
        => await RemoveSettingAsync(BuildDraftSettingKey(BillingEditorDraftSettingPrefix, session), ct);

    public async Task<RentalCustomerOnboardingDraftModel?> GetOnboardingDraftAsync(SessionState session, CancellationToken ct = default)
        => await GetDraftAsync<RentalCustomerOnboardingDraftModel>(BuildDraftSettingKey(OnboardingDraftSettingPrefix, session), ct);

    public async Task SaveOnboardingDraftAsync(RentalCustomerOnboardingDraftModel draft, SessionState session, CancellationToken ct = default)
        => await UpsertSettingAsync(
            BuildDraftSettingKey(OnboardingDraftSettingPrefix, session),
            JsonSerializer.Serialize(draft, RentalJsonOptions),
            ct);

    public async Task ClearOnboardingDraftAsync(SessionState session, CancellationToken ct = default)
        => await RemoveSettingAsync(BuildDraftSettingKey(OnboardingDraftSettingPrefix, session), ct);

    private async Task EnsureAdministrativeBusinessCachesAsync(SessionState session, CancellationToken ct)
    {
        if (!CanAdministrativelyViewAllRental(session) || _serviceProvider is null)
            return;

        var syncService = _serviceProvider.GetService<SyncService>();
        if (syncService is null)
            return;

        await syncService.EnsureAdministrativeBusinessCachesAsync(ct);
    }

    public async Task<RentalDashboardSummary> GetDashboardSummaryAsync(
        SessionState session,
        DateOnly referenceDate,
        CancellationToken ct = default)
    {
        var alertDays = await GetAlertDayValuesAsync(ct);
        var alertWindow = alertDays.Count == 0 ? 7 : alertDays.Max();

        var offices = await GetOfficeMapAsync(ct);
        var profiles = await ApplyBillingScope(_db.RentalBillingProfiles.AsNoTracking(), session)
            .Where(profile => profile.IsActive)
            .ToListAsync(ct);
        var assets = await ApplyAssetScope(_db.RentalAssets.AsNoTracking(), session)
            .Where(asset => asset.AssetStatus != "폐기")
            .ToListAsync(ct);
        var customerQuery = _db.Customers.AsNoTracking().Where(customer => !customer.IsDeleted);
        var currentTenantCode = ResolveCurrentRentalTenantCode(session);
        if (CanAdministrativelyViewAllRental(session))
        {
            // 전체 범위 admin만 로컬 캐시에 남아 있는 타 tenant 데이터까지 조회할 수 있다.
        }
        else if (CanViewAllRental(session))
        {
            customerQuery = customerQuery.Where(customer => customer.TenantCode == currentTenantCode);
        }
        else
        {
            var readableOfficeCodes = GetReadableOfficeCodes(session);
            customerQuery = customerQuery.Where(customer =>
                customer.TenantCode == currentTenantCode &&
                readableOfficeCodes.Contains(customer.ResponsibleOfficeCode));
        }

        var customers = await customerQuery
            .Select(customer => new RentalCustomerCandidateLookup(
                customer.Id,
                customer.NameOriginal,
                customer.BusinessNumber))
            .ToListAsync(ct);
        var assetsByProfile = assets
            .Where(asset => asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty)
            .GroupBy(asset => asset.BillingProfileId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var alertItems = profiles
            .Select(profile => ToAlertItem(profile, offices, referenceDate))
            .Where(item => item is not null)
            .Cast<RentalAlertItem>()
            .Where(item => item.DaysRemaining <= alertWindow)
            .OrderBy(item => item.DaysRemaining)
            .ThenBy(item => item.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .Take(30)
            .ToList();

        var expiringAssets = assets
            .Where(asset => asset.RentalEndDate.HasValue)
            .Select(asset => ToExpiringAssetItem(asset, offices, referenceDate))
            .Where(item => item is not null && item.DaysRemaining <= 30)
            .Cast<RentalExpiringAssetItem>()
            .OrderBy(item => item.DaysRemaining)
            .ThenBy(item => item.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .Take(20)
            .ToList();

        var billingCustomerUnlinked = profiles
            .Where(profile => !profile.CustomerId.HasValue || profile.CustomerId.Value == Guid.Empty)
            .ToList();
        var assetCustomerUnlinked = assets
            .Where(asset => !asset.CustomerId.HasValue || asset.CustomerId.Value == Guid.Empty)
            .ToList();
        var assetBillingUnlinked = assets
            .Where(asset => (!asset.BillingProfileId.HasValue || asset.BillingProfileId.Value == Guid.Empty) &&
                            !RentalAssetStatusRules.IsNonOperating(asset.AssetStatus))
            .ToList();
        var assetlessProfiles = profiles
            .Where(profile => !assetsByProfile.TryGetValue(profile.Id, out var linkedAssets) || linkedAssets.Count == 0)
            .ToList();

        var unresolvedLinkItems = new List<RentalLinkReviewItem>();
        unresolvedLinkItems.AddRange(billingCustomerUnlinked
            .OrderBy(profile => profile.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .Take(12)
            .Select(profile => new RentalLinkReviewItem
            {
                QueueType = "프로필 고객 미연결",
                ResponsibleOfficeName = ResolveOfficeDisplayName(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode, offices),
                CustomerName = ResolvePrimaryBillingCustomerName(profile),
                ItemName = profile.ItemName,
                InstallLocation = profile.InstallSiteName,
                CandidateCount = CountCustomerCandidates(customers, profile.BusinessNumber, profile.CustomerName),
                ReviewNote = string.Join(" / ", BuildBillingDataIssues(profile, assetsByProfile.GetValueOrDefault(profile.Id, []), GetBillingTemplateItems(profile, assetsByProfile.GetValueOrDefault(profile.Id, []))))
            }));
        unresolvedLinkItems.AddRange(assetCustomerUnlinked
            .OrderBy(asset => asset.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
            .Take(12)
            .Select(asset => new RentalLinkReviewItem
            {
                QueueType = "자산 고객 미연결",
                ResponsibleOfficeName = ResolveOfficeDisplayName(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, offices),
                CustomerName = ResolvePrimaryAssetCustomerName(asset),
                ItemName = asset.ItemName,
                InstallLocation = string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation,
                CandidateCount = CountCustomerCandidates(customers, null, asset.CustomerName, asset.CurrentCustomerName),
                ReviewNote = string.Join(" / ", BuildAssetDataIssues(asset))
            }));
        unresolvedLinkItems.AddRange(assetBillingUnlinked
            .OrderBy(asset => asset.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
            .Take(12)
            .Select(asset => new RentalLinkReviewItem
            {
                QueueType = "자산 청구 미연결",
                ResponsibleOfficeName = ResolveOfficeDisplayName(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, offices),
                CustomerName = ResolvePrimaryAssetCustomerName(asset),
                ItemName = asset.ItemName,
                InstallLocation = string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation,
                CandidateCount = CountBillingProfileCandidates(profiles, asset),
                ReviewNote = string.Join(" / ", BuildAssetDataIssues(asset).DefaultIfEmpty("자동 연결 후보 확인 필요"))
            }));
        unresolvedLinkItems.AddRange(assetlessProfiles
            .OrderBy(profile => profile.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .Take(12)
            .Select(profile => new RentalLinkReviewItem
            {
                QueueType = "자산 없는 청구프로필",
                ResponsibleOfficeName = ResolveOfficeDisplayName(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode, offices),
                CustomerName = ResolvePrimaryBillingCustomerName(profile),
                ItemName = profile.ItemName,
                InstallLocation = profile.InstallSiteName,
                CandidateCount = 0,
                ReviewNote = "보류 상태 유지 / 연결 자산 확인 필요"
            }));

        var summary = new RentalDashboardSummary
        {
            DueTodayCount = alertItems.Count(item => item.DaysRemaining == 0),
            UpcomingCount = alertItems.Count(item => item.DaysRemaining is > 0),
            OverdueCount = alertItems.Count(item => item.DaysRemaining < 0),
            ActiveAssetCount = assets.Count,
            ExpiringContractCount = expiringAssets.Count,
            UnassignedCount = billingCustomerUnlinked.Count + assetCustomerUnlinked.Count + assetBillingUnlinked.Count + assetlessProfiles.Count,
            BillingCustomerUnlinkedCount = billingCustomerUnlinked.Count,
            AssetCustomerUnlinkedCount = assetCustomerUnlinked.Count,
            AssetBillingUnlinkedCount = assetBillingUnlinked.Count,
            AssetlessBillingProfileCount = assetlessProfiles.Count,
            AlertItems = alertItems,
            ExpiringAssets = expiringAssets,
            UnresolvedLinkItems = unresolvedLinkItems
                .OrderBy(item => item.QueueType, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.ResponsibleOfficeName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.CustomerName, StringComparer.CurrentCultureIgnoreCase)
                .Take(30)
                .ToList(),
            AlertPopupMessage = BuildAlertPopupMessage(alertItems, expiringAssets)
        };

        return summary;
    }

    private static string ResolvePrimaryBillingCustomerName(LocalRentalBillingProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.CustomerName))
            return profile.CustomerName;
        return string.Empty;
    }

    private static string ResolvePrimaryAssetCustomerName(LocalRentalAsset asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.CurrentCustomerName))
            return asset.CurrentCustomerName;
        if (!string.IsNullOrWhiteSpace(asset.CustomerName))
            return asset.CustomerName;
        return string.Empty;
    }

    private async Task<Dictionary<Guid, string>> GetCustomerNameMapAsync(IEnumerable<Guid> customerIds, CancellationToken ct)
    {
        var ids = customerIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        var result = new Dictionary<Guid, string>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds;
            var customers = await _db.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => scopedBatchIds.Contains(customer.Id) && !customer.IsDeleted)
                .Select(customer => new
                {
                    customer.Id,
                    customer.NameOriginal
                })
                .ToListAsync(ct);

            foreach (var customer in customers)
                result[customer.Id] = customer.NameOriginal;
        }

        return result;
    }

    private async Task<List<Guid>> GetBoundedAssetSearchCustomerIdsAsync(
        string keyword,
        string normalizedKeyword,
        CancellationToken ct)
    {
        if (keyword.Trim().Length < 2 && normalizedKeyword.Trim().Length < 2)
            return new List<Guid>();

        var customers = _db.Customers
            .AsNoTracking()
            .Where(customer => !customer.IsDeleted);

        IQueryable<LocalCustomer> prefixMatchedCustomers;
        if (string.IsNullOrWhiteSpace(normalizedKeyword))
        {
            prefixMatchedCustomers = customers.Where(customer => customer.NameOriginal.StartsWith(keyword));
        }
        else
        {
            prefixMatchedCustomers = customers.Where(customer =>
                customer.NameOriginal.StartsWith(keyword) ||
                (customer.NameMatchKey ?? string.Empty).StartsWith(normalizedKeyword));
        }

        var customerIds = await prefixMatchedCustomers
            .OrderBy(customer => customer.NameOriginal)
            .Select(customer => customer.Id)
            .Distinct()
            .Take(AssetSearchCustomerMatchLimit)
            .ToListAsync(ct);
        if (customerIds.Count >= AssetSearchCustomerMatchLimit)
            return customerIds;

        IQueryable<LocalCustomer> containsMatchedCustomers;
        if (string.IsNullOrWhiteSpace(normalizedKeyword))
        {
            containsMatchedCustomers = customers.Where(customer => customer.NameOriginal.Contains(keyword));
        }
        else
        {
            containsMatchedCustomers = customers.Where(customer =>
                customer.NameOriginal.Contains(keyword) ||
                (customer.NameMatchKey ?? string.Empty).Contains(normalizedKeyword));
        }

        if (customerIds.Count > 0)
            containsMatchedCustomers = containsMatchedCustomers.Where(customer => !customerIds.Contains(customer.Id));

        var remainingLimit = AssetSearchCustomerMatchLimit - customerIds.Count;
        var containsCustomerIds = await containsMatchedCustomers
            .OrderBy(customer => customer.NameOriginal)
            .Select(customer => customer.Id)
            .Distinct()
            .Take(remainingLimit)
            .ToListAsync(ct);
        customerIds.AddRange(containsCustomerIds);
        return customerIds;
    }

    private static string ResolveAssetCustomerDisplayName(
        LocalRentalAsset asset,
        IReadOnlyDictionary<Guid, string> customerNameMap)
    {
        if (asset.CustomerId.HasValue &&
            asset.CustomerId.Value != Guid.Empty &&
            customerNameMap.TryGetValue(asset.CustomerId.Value, out var linkedCustomerName) &&
            !string.IsNullOrWhiteSpace(linkedCustomerName))
        {
            return linkedCustomerName.Trim();
        }

        return string.IsNullOrWhiteSpace(asset.CurrentCustomerName)
            ? RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CustomerName)
            : RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CurrentCustomerName);
    }

    private static void ApplyResolvedAssetCustomerDisplayName(
        LocalRentalAsset asset,
        IReadOnlyDictionary<Guid, string> customerNameMap)
    {
        var displayName = ResolveAssetCustomerDisplayName(asset, customerNameMap);
        if (string.IsNullOrWhiteSpace(displayName))
            return;

        asset.CurrentCustomerName = displayName;
        if (asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
            asset.CustomerName = displayName;
    }

    private async Task NormalizeAssetCustomerDisplayNamesAsync(
        IReadOnlyList<LocalRentalAsset> assets,
        CancellationToken ct)
    {
        if (assets.Count == 0)
            return;

        var customerIdsNeedingLookup = assets
            .Where(asset =>
                asset.CustomerId.HasValue &&
                asset.CustomerId.Value != Guid.Empty &&
                string.IsNullOrWhiteSpace(asset.CurrentCustomerName) &&
                string.IsNullOrWhiteSpace(asset.CustomerName))
            .Select(asset => asset.CustomerId!.Value)
            .Distinct()
            .ToList();
        var customerNameMap = customerIdsNeedingLookup.Count == 0
            ? new Dictionary<Guid, string>()
            : await GetCustomerNameMapAsync(customerIdsNeedingLookup, ct);

        foreach (var asset in assets)
        {
            asset.AssetStatus = RentalAssetStatusRules.Normalize(asset.AssetStatus);
            ApplyResolvedAssetCustomerDisplayName(asset, customerNameMap);
        }
    }

    private static string BuildBillingProfileDisplayName(
        LocalRentalBillingProfile profile,
        IReadOnlyDictionary<Guid, string> customerNameMap)
        => BuildBillingProfileDisplayName(
            profile.CustomerId,
            profile.CustomerName,
            profile.ProfileKey,
            profile.InstallSiteName,
            customerNameMap);

    private static string BuildBillingProfileDisplayName(
        RentalBillingProfileDisplayLookup profile,
        IReadOnlyDictionary<Guid, string> customerNameMap)
        => BuildBillingProfileDisplayName(
            profile.CustomerId,
            profile.CustomerName,
            profile.ProfileKey,
            profile.InstallSiteName,
            customerNameMap);

    private static string BuildBillingProfileDisplayName(
        Guid? customerId,
        string? customerNameValue,
        string? profileKey,
        string? installSiteNameValue,
        IReadOnlyDictionary<Guid, string> customerNameMap)
    {
        var customerName = ResolveBillingProfileCustomerDisplayName(
            customerId,
            customerNameValue,
            profileKey,
            customerNameMap);
        var installSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(installSiteNameValue);
        return string.IsNullOrWhiteSpace(installSiteName)
            ? customerName
            : $"{customerName} / {installSiteName}";
    }

    private static int CountCustomerCandidates(
        IReadOnlyCollection<RentalCustomerCandidateLookup> customers,
        string? businessNumber,
        params string?[] names)
    {
        var candidateNames = names
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var normalizedBusinessNumber = (businessNumber ?? string.Empty).Trim();

        var matches = customers
            .Where(customer =>
                (!string.IsNullOrWhiteSpace(normalizedBusinessNumber) &&
                 string.Equals((customer.BusinessNumber ?? string.Empty).Trim(), normalizedBusinessNumber, StringComparison.OrdinalIgnoreCase)) ||
                candidateNames.Contains(customer.NameOriginal, StringComparer.CurrentCultureIgnoreCase))
            .Select(customer => customer.Id)
            .Distinct()
            .Count();
        return matches;
    }

    private static int CountBillingProfileCandidates(
        IReadOnlyCollection<LocalRentalBillingProfile> profiles,
        LocalRentalAsset asset)
    {
        var candidateNames = new[]
            {
                asset.CustomerName,
                asset.CurrentCustomerName
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var installLocation = (asset.InstallLocation ?? string.Empty).Trim();

        var candidates = profiles
            .Where(profile =>
                (asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty && profile.CustomerId == asset.CustomerId) ||
                candidateNames.Contains(profile.CustomerName, StringComparer.CurrentCultureIgnoreCase))
            .Where(profile =>
                string.IsNullOrWhiteSpace(installLocation) ||
                string.Equals((profile.InstallSiteName ?? string.Empty).Trim(), installLocation, StringComparison.CurrentCultureIgnoreCase) ||
                string.Equals((profile.ItemName ?? string.Empty).Trim(), (asset.ItemName ?? string.Empty).Trim(), StringComparison.CurrentCultureIgnoreCase))
            .Select(profile => profile.Id)
            .Distinct()
            .Count();
        return candidates;
    }

    public async Task<IReadOnlyList<RentalBillingViewRow>> GetBillingRowsAsync(
        RentalBillingFilter filter,
        SessionState session,
        CancellationToken ct = default)
    {
        if (IsRentalSearchTextTooShort(filter.SearchText))
            return Array.Empty<RentalBillingViewRow>();

        var totalStopwatch = Stopwatch.StartNew();
        var stepStopwatch = Stopwatch.StartNew();
        await EnsureAdministrativeBusinessCachesAsync(session, ct);
        LogRentalLoadStep("Rental billing admin cache", stepStopwatch, BuildBillingFilterTimingDetail(filter));

        stepStopwatch.Restart();
        var offices = await GetOfficeMapAsync(ct);
        var canLoadUnlinkedAssets = ShouldLoadUnlinkedBillingAssets(filter);
        var searchKeyword = (filter.SearchText ?? string.Empty).Trim();
        var baseFilter = string.IsNullOrWhiteSpace(searchKeyword)
            ? filter
            : CreateBillingFilterWithoutSearch(filter);
        var query = ApplyBillingScope(_db.RentalBillingProfiles.AsNoTracking(), session);
        query = ApplyBillingFilter(query, baseFilter, session);
        var profileResultLimit = ResolveBillingProfileResultLimit(filter);
        var profiles = string.IsNullOrWhiteSpace(searchKeyword)
            ? await LoadBillingProfilesAsync(query, profileResultLimit, ct)
            : await LoadBillingProfileSearchResultsAsync(query, searchKeyword, profileResultLimit, ct);
        LogRentalLoadStep("Rental billing profile query", stepStopwatch, $"profiles={profiles.Count:N0}, limit={profileResultLimit?.ToString("N0", CultureInfo.CurrentCulture) ?? "none"}, {BuildBillingFilterTimingDetail(filter)}");

        stepStopwatch.Restart();
        var alertWindow = (await GetAlertDayValuesAsync(ct)).DefaultIfEmpty(7).Max();
        var profileCountBeforeDuePrefilter = profiles.Count;
        profiles = ApplyDueOnlyProfilePrefilter(profiles, filter, alertWindow, filter.ReferenceDate);
        if (ShouldPrefilterDueOnlyBillingProfiles(filter))
        {
            LogRentalLoadStep(
                "Rental billing due profile prefilter",
                stepStopwatch,
                $"profiles={profiles.Count:N0}/{profileCountBeforeDuePrefilter:N0}, alertWindow={alertWindow:N0}, {BuildBillingFilterTimingDetail(filter)}");
        }

        stepStopwatch.Restart();
        var profileCountBeforePastDuePrefilter = profiles.Count;
        profiles = await ApplyPastDueOnlyProfilePrefilterAsync(profiles, filter, filter.ReferenceDate, ct);
        if (ShouldPrefilterPastDueOnlyBillingProfiles(filter))
        {
            LogRentalLoadStep(
                "Rental billing past due profile prefilter",
                stepStopwatch,
                $"profiles={profiles.Count:N0}/{profileCountBeforePastDuePrefilter:N0}, {BuildBillingFilterTimingDetail(filter)}");
        }

        stepStopwatch.Restart();
        var includeUnlinkedAssets = canLoadUnlinkedAssets &&
                                    !ShouldDeferSupplementalUnlinkedBillingAssets(filter, profileResultLimit, profiles.Count);
        var unlinkedAssetLimit = includeUnlinkedAssets ? ResolveUnlinkedBillingAssetResultLimit(filter) : 0;
        if (canLoadUnlinkedAssets && !includeUnlinkedAssets)
        {
            LogRentalLoadStep(
                "Rental billing unlinked asset query deferred",
                stepStopwatch,
                $"profiles={profiles.Count:N0}, profileLimit={profileResultLimit?.ToString("N0", CultureInfo.CurrentCulture) ?? "none"}, {BuildBillingFilterTimingDetail(filter)}");
        }

        stepStopwatch.Restart();
        var unlinkedAssetBaseQuery = includeUnlinkedAssets
            ? ApplyUnlinkedBillingAssetFilter(
                ApplyAssetScope(_db.RentalAssets.AsNoTracking(), session)
                    .Where(asset => !asset.IsDeleted)
                    .Where(asset => !asset.BillingProfileId.HasValue || asset.BillingProfileId == Guid.Empty)
                    .Where(asset => asset.BillingEligibilityStatus == null || asset.BillingEligibilityStatus != BillingEligibilityExcluded)
                    .Where(asset => !NonOperatingAssetStatusQueryValues.Contains(asset.AssetStatus)),
                baseFilter)
            : null;
        var unlinkedAssets = includeUnlinkedAssets
            ? string.IsNullOrWhiteSpace(searchKeyword)
                ? await SelectBillingAssetListProjection(unlinkedAssetBaseQuery!)
                    .OrderBy(asset => asset.CustomerName)
                    .ThenBy(asset => asset.CurrentCustomerName)
                    .ThenBy(asset => asset.ManagementNumber)
                    .Take(unlinkedAssetLimit)
                    .ToListAsync(ct)
                : await LoadUnlinkedBillingAssetSearchResultsAsync(unlinkedAssetBaseQuery!, searchKeyword, unlinkedAssetLimit, ct)
            : new List<LocalRentalAsset>();
        LogRentalLoadStep("Rental billing unlinked asset query", stepStopwatch, $"assets={unlinkedAssets.Count:N0}, include={includeUnlinkedAssets}, limit={unlinkedAssetLimit:N0}");

        stepStopwatch.Restart();
        var rows = await BuildBillingProfileRowsAsync(profiles, session, offices, filter.ReferenceDate, filter.IncludeHistoryRows, ct);
        LogRentalLoadStep("Rental billing row build", stepStopwatch, $"rows={rows.Count:N0}, profiles={profiles.Count:N0}");

        if (includeUnlinkedAssets)
        {
            stepStopwatch.Restart();
            var unlinkedCustomerIds = CollectUnlinkedBillingCustomerIds(unlinkedAssets);
            var unlinkedCustomersById = await GetRentalBillingCustomerLookupMapAsync(unlinkedCustomerIds, ct);

            if (unlinkedAssets.Count > 0)
                rows.EnsureCapacity(rows.Count + unlinkedAssets.Count);
            foreach (var asset in unlinkedAssets)
            {
                rows.Add(CreateUnlinkedBillingViewRow(
                    asset,
                    unlinkedCustomersById,
                    offices,
                    filter.ReferenceDate));
            }
            LogRentalLoadStep("Rental billing unlinked row build", stepStopwatch, $"rows={rows.Count:N0}, customers={unlinkedCustomersById.Count:N0}");
        }

        stepStopwatch.Restart();
        if (!filter.ExpandCustomerSummaryRows)
            rows = GroupBillingRowsByCustomer(rows);

        rows = ApplyBillingFinalRowFilters(rows, filter, alertWindow);

        var result = SortBillingRowsForDisplay(rows);
        LogRentalLoadStep("Rental billing final filter/sort", stepStopwatch, $"rows={result.Count:N0}");
        OperationTiming.LogIfSlow(
            "DATA",
            "Rental billing total load",
            totalStopwatch.Elapsed,
            $"rows={result.Count:N0}, profiles={profiles.Count:N0}, unlinkedAssets={unlinkedAssets.Count:N0}",
            infoThreshold: TimeSpan.FromMilliseconds(600),
            warningThreshold: TimeSpan.FromSeconds(2));
        return result;
    }

    public async Task<RentalBillingViewRow?> GetBillingRowAsync(
        Guid profileId,
        SessionState session,
        DateOnly referenceDate,
        CancellationToken ct = default)
    {
        if (profileId == Guid.Empty)
            return null;

        await EnsureAdministrativeBusinessCachesAsync(session, ct);

        var offices = await GetOfficeMapAsync(ct);
        var profile = await ApplyBillingScope(_db.RentalBillingProfiles.AsNoTracking(), session)
            .FirstOrDefaultAsync(current => current.Id == profileId, ct);
        if (profile is null)
            return null;

        var rows = await BuildBillingProfileRowsAsync([profile], session, offices, referenceDate, includeHistoryRows: true, ct);
        return rows.FirstOrDefault();
    }

    private static RentalBillingFilter CreateBillingFilterWithoutSearch(RentalBillingFilter filter)
        => new()
        {
            SearchText = string.Empty,
            OfficeCode = filter.OfficeCode,
            Status = filter.Status,
            DueOnly = filter.DueOnly,
            PastDueOnly = filter.PastDueOnly,
            ExpandCustomerSummaryRows = filter.ExpandCustomerSummaryRows,
            IncludeHistoryRows = filter.IncludeHistoryRows,
            ReferenceDate = filter.ReferenceDate
        };

    private static async Task<List<LocalRentalBillingProfile>> LoadBillingProfilesAsync(
        IQueryable<LocalRentalBillingProfile> query,
        int? profileResultLimit,
        CancellationToken ct)
    {
        IQueryable<LocalRentalBillingProfile> profileQuery = query
            .OrderBy(profile => profile.CustomerName)
            .ThenBy(profile => profile.ItemName);
        if (profileResultLimit.HasValue)
            profileQuery = profileQuery.Take(profileResultLimit.Value);

        return await profileQuery.ToListAsync(ct);
    }

    private static async Task<List<LocalRentalBillingProfile>> LoadBillingProfileSearchResultsAsync(
        IQueryable<LocalRentalBillingProfile> baseQuery,
        string keyword,
        int? profileResultLimit,
        CancellationToken ct)
    {
        if (!profileResultLimit.HasValue)
            return await ApplyBillingProfileSearchContainsFilter(baseQuery, keyword).ToListAsync(ct);

        var profiles = new List<LocalRentalBillingProfile>(profileResultLimit.Value);
        await AddDistinctBillingProfileSearchResultsAsync(
            profiles,
            ApplyBillingProfileSearchPrefixFilter(baseQuery, keyword),
            profileResultLimit.Value,
            orderByListColumns: true,
            ct);

        if (profiles.Count < profileResultLimit.Value)
        {
            await AddDistinctBillingProfileSearchResultsAsync(
                profiles,
                ApplyBillingProfileSearchContainsFilter(baseQuery, keyword),
                profileResultLimit.Value,
                orderByListColumns: false,
                ct);
        }

        return profiles;
    }

    private static async Task AddDistinctBillingProfileSearchResultsAsync(
        List<LocalRentalBillingProfile> profiles,
        IQueryable<LocalRentalBillingProfile> query,
        int maxResults,
        bool orderByListColumns,
        CancellationToken ct)
    {
        var remaining = maxResults - profiles.Count;
        if (remaining <= 0)
            return;

        var existingIds = profiles.Select(profile => profile.Id).ToList();
        if (existingIds.Count > 0)
            query = query.Where(profile => !existingIds.Contains(profile.Id));

        if (orderByListColumns)
            query = query.OrderBy(profile => profile.CustomerName).ThenBy(profile => profile.ItemName);

        var nextProfiles = await query
            .Take(remaining)
            .ToListAsync(ct);
        profiles.AddRange(nextProfiles);
    }

    private static IQueryable<LocalRentalBillingProfile> ApplyBillingProfileSearchPrefixFilter(
        IQueryable<LocalRentalBillingProfile> query,
        string keyword)
        => query.Where(profile =>
            profile.CustomerName.StartsWith(keyword) ||
            profile.BusinessNumber.StartsWith(keyword) ||
            profile.ItemName.StartsWith(keyword) ||
            profile.Notes.StartsWith(keyword));

    private static IQueryable<LocalRentalBillingProfile> ApplyBillingProfileSearchContainsFilter(
        IQueryable<LocalRentalBillingProfile> query,
        string keyword)
        => query.Where(profile =>
            profile.CustomerName.Contains(keyword) ||
            profile.BusinessNumber.Contains(keyword) ||
            profile.ItemName.Contains(keyword) ||
            profile.Notes.Contains(keyword));

    public Task<IReadOnlyList<RentalBillingHistoryRow>> GetBillingHistoryRowsAsync(
        IReadOnlyCollection<Guid> profileIds,
        SessionState session,
        DateOnly referenceDate,
        CancellationToken ct = default)
        => GetBillingHistoryRowsAsync(profileIds, session, referenceDate, maxDisplayRows: 0, ct);

    public async Task<IReadOnlyList<RentalBillingHistoryRow>> GetBillingHistoryRowsAsync(
        IReadOnlyCollection<Guid> profileIds,
        SessionState session,
        DateOnly referenceDate,
        int maxDisplayRows,
        CancellationToken ct = default)
    {
        var ids = profileIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return Array.Empty<RentalBillingHistoryRow>();

        await EnsureAdministrativeBusinessCachesAsync(session, ct);

        var profiles = await ApplyBillingScope(_db.RentalBillingProfiles.AsNoTracking(), session)
            .Where(profile => ids.Contains(profile.Id))
            .OrderBy(profile => profile.CustomerName)
            .ThenBy(profile => profile.ItemName)
            .ToListAsync(ct);
        if (profiles.Count == 0)
            return Array.Empty<RentalBillingHistoryRow>();

        var customerNameMap = await GetBillingProfileCustomerNameMapAsync(profiles, ct);
        var billingRunsByProfile = profiles.ToDictionary(
            profile => profile.Id,
            profile => DeduplicateBillingRuns(GetBillingRuns(profile)));
        var displayBillingRunsByProfile = maxDisplayRows > 0
            ? LimitBillingRunsForHistoryDisplay(billingRunsByProfile, maxDisplayRows)
            : billingRunsByProfile;
        var allRunIds = displayBillingRunsByProfile.Values
            .SelectMany(runs => runs)
            .Select(run => run.RunId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var (settlementByRun, invoiceByRun) = await LoadBillingRunReferencesAsync(allRunIds, ct);

        var rows = new List<RentalBillingHistoryRow>();
        foreach (var profile in profiles)
        {
            ct.ThrowIfCancellationRequested();
            var customerDisplayName = ResolveBillingProfileCustomerDisplayName(profile, customerNameMap);
            displayBillingRunsByProfile.TryGetValue(profile.Id, out var profileRuns);
            profileRuns ??= new List<RentalBillingRunModel>();
            rows.AddRange(BuildBillingHistoryRows(
                profile,
                customerDisplayName,
                profileRuns,
                settlementByRun,
                invoiceByRun,
                referenceDate));
        }

        return rows
            .OrderByDescending(history => history.ScheduledDate)
            .ThenBy(history => history.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxDisplayRows > 0 ? maxDisplayRows : int.MaxValue)
            .ToList();
    }

    private async Task<List<RentalBillingViewRow>> BuildBillingProfileRowsAsync(
        IReadOnlyList<LocalRentalBillingProfile> profiles,
        SessionState session,
        IReadOnlyDictionary<string, string> offices,
        DateOnly referenceDate,
        bool includeHistoryRows,
        CancellationToken ct)
    {
        if (profiles.Count == 0)
            return new List<RentalBillingViewRow>();

        var stepStopwatch = Stopwatch.StartNew();
        var profileIds = profiles.Select(profile => profile.Id).ToList();
        var billingAssets = await LoadBillingAssetsForProfilesAsync(profileIds, session, ct);
        ct.ThrowIfCancellationRequested();
        LogRentalLoadStep("Rental billing linked asset query", stepStopwatch, $"assets={billingAssets.Count:N0}, profiles={profiles.Count:N0}");

        stepStopwatch.Restart();
        var assetsByProfile = billingAssets
            .GroupBy(asset => asset.BillingProfileId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        LogRentalLoadStep("Rental billing linked asset grouping", stepStopwatch, $"assetGroups={assetsByProfile.Count:N0}");

        stepStopwatch.Restart();
        var customerNameMap = await GetBillingProfileCustomerNameMapAsync(profiles, ct);
        ct.ThrowIfCancellationRequested();
        LogRentalLoadStep("Rental billing customer lookup", stepStopwatch, $"customers={customerNameMap.Count:N0}");

        stepStopwatch.Restart();
        var preparedProfiles = new List<RentalBillingPreparedProfile>(profiles.Count);
        var preparedBillingRunCount = 0;
        foreach (var profile in profiles)
        {
            ct.ThrowIfCancellationRequested();
            assetsByProfile.TryGetValue(profile.Id, out var profileAssets);
            profileAssets ??= new List<LocalRentalAsset>();
            var templateItems = GetBillingTemplateItems(profile, profileAssets);
            var runs = GetBillingRuns(profile);
            var previewRun = GetOrCreateBillingRun(profile, referenceDate, persistChanges: false, templateItems, runs);
            var billingRuns = ResolveBillingRunsForRowBuild(
                runs,
                referenceDate,
                includeHistoryRows);
            preparedProfiles.Add(new RentalBillingPreparedProfile(
                profile,
                profileAssets,
                templateItems,
                previewRun,
                billingRuns));
            preparedBillingRunCount += billingRuns.Count;
        }
        LogRentalLoadStep("Rental billing template/run preparation", stepStopwatch, $"profiles={preparedProfiles.Count:N0}, runs={preparedBillingRunCount:N0}");

        stepStopwatch.Restart();
        var referenceRunIds = ResolveBillingRunReferenceIds(
            preparedProfiles,
            referenceDate,
            includeHistoryRows);
        var (settlementByRun, invoiceByRun) = await LoadBillingRunReferencesAsync(referenceRunIds, ct);
        ct.ThrowIfCancellationRequested();
        LogRentalLoadStep("Rental billing run reference query", stepStopwatch, $"runs={referenceRunIds.Count:N0}, settlements={settlementByRun.Count:N0}, invoices={invoiceByRun.Count:N0}, history={includeHistoryRows}");

        stepStopwatch.Restart();
        var rows = new List<RentalBillingViewRow>(preparedProfiles.Count);
        foreach (var preparedProfile in preparedProfiles)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(CreateBillingViewRow(
                preparedProfile,
                customerNameMap,
                settlementByRun,
                invoiceByRun,
                offices,
                referenceDate,
                includeHistoryRows));
        }
        LogRentalLoadStep("Rental billing row projection", stepStopwatch, $"rows={rows.Count:N0}");
        return rows;
    }

    private async Task<List<LocalRentalAsset>> LoadBillingAssetsForProfilesAsync(
        IReadOnlyCollection<Guid> profileIds,
        SessionState session,
        CancellationToken ct)
    {
        var ids = profileIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return new List<LocalRentalAsset>();

        var assets = new List<LocalRentalAsset>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds;
            var batchAssets = await SelectBillingLinkedAssetRowProjection(ApplyAssetScope(_db.RentalAssets.AsNoTracking(), session)
                    .Where(asset => !asset.IsDeleted &&
                                    asset.BillingProfileId.HasValue &&
                                    scopedBatchIds.Contains(asset.BillingProfileId.Value)))
                .ToListAsync(ct);
            assets.AddRange(batchAssets);
        }

        return assets;
    }

    private static IQueryable<LocalRentalAsset> SelectBillingLinkedAssetRowProjection(IQueryable<LocalRentalAsset> query)
        => query.Select(asset => new LocalRentalAsset
        {
            Id = asset.Id,
            BillingProfileId = asset.BillingProfileId,
            InstallSiteName = asset.InstallSiteName,
            BillingEligibilityStatus = asset.BillingEligibilityStatus,
            InstallLocation = asset.InstallLocation,
            MonthlyFee = asset.MonthlyFee,
            AssetStatus = asset.AssetStatus
        });

    private static IQueryable<LocalRentalAsset> SelectBillingAssetListProjection(IQueryable<LocalRentalAsset> query)
        => query.Select(asset => new LocalRentalAsset
        {
            Id = asset.Id,
            IsDeleted = asset.IsDeleted,
            CreatedAtUtc = asset.CreatedAtUtc,
            UpdatedAtUtc = asset.UpdatedAtUtc,
            Revision = asset.Revision,
            IsDirty = asset.IsDirty,
            TenantCode = asset.TenantCode,
            OfficeCode = asset.OfficeCode,
            CustomerId = asset.CustomerId,
            BillingProfileId = asset.BillingProfileId,
            ManagementNumber = asset.ManagementNumber,
            ManagementCompanyCode = asset.ManagementCompanyCode,
            CurrentCustomerName = asset.CurrentCustomerName,
            InstallSiteName = asset.InstallSiteName,
            BillingEligibilityStatus = asset.BillingEligibilityStatus,
            ItemName = asset.ItemName,
            MachineNumber = asset.MachineNumber,
            CustomerName = asset.CustomerName,
            InstallLocation = asset.InstallLocation,
            MonthlyFee = asset.MonthlyFee,
            ContractDate = asset.ContractDate,
            InstallDate = asset.InstallDate,
            ContractStartDate = asset.ContractStartDate,
            RentalEndDate = asset.RentalEndDate,
            ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            AssetStatus = asset.AssetStatus,
            Notes = asset.Notes
        });

    private async Task<Dictionary<Guid, string>> GetBillingProfileCustomerNameMapAsync(
        IReadOnlyList<LocalRentalBillingProfile> profiles,
        CancellationToken ct)
    {
        var customerIds = profiles
            .Where(profile =>
                profile.CustomerId.HasValue &&
                profile.CustomerId.Value != Guid.Empty &&
                NeedsBillingProfileCustomerNameLookup(profile))
            .Select(profile => profile.CustomerId!.Value)
            .Distinct()
            .ToList();
        if (customerIds.Count == 0)
            return new Dictionary<Guid, string>();

        return await GetCustomerNameMapAsync(customerIds, ct);
    }

    private async Task<Dictionary<Guid, RentalBillingCustomerLookup>> GetRentalBillingCustomerLookupMapAsync(
        IEnumerable<Guid> customerIds,
        CancellationToken ct)
    {
        var ids = customerIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, RentalBillingCustomerLookup>();

        var result = new Dictionary<Guid, RentalBillingCustomerLookup>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds;
            var customers = await _db.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => scopedBatchIds.Contains(customer.Id) && !customer.IsDeleted)
                .Select(customer => new RentalBillingCustomerLookup(
                    customer.Id,
                    customer.NameOriginal,
                    customer.BusinessNumber))
                .ToListAsync(ct);

            foreach (var customer in customers)
                result[customer.Id] = customer;
        }

        return result;
    }

    private static IReadOnlyList<Guid> ResolveBillingRunReferenceIds(
        IReadOnlyList<RentalBillingPreparedProfile> preparedProfiles,
        DateOnly referenceDate,
        bool includeHistoryRows)
    {
        var ids = new HashSet<Guid>();
        var referenceMonth = new DateOnly(referenceDate.Year, referenceDate.Month, 1);

        foreach (var preparedProfile in preparedProfiles)
        {
            foreach (var run in preparedProfile.BillingRuns)
            {
                if (run.RunId == Guid.Empty)
                    continue;

                if (includeHistoryRows || IsPastBillingRun(run, referenceMonth))
                    ids.Add(run.RunId);
            }
        }

        if (!includeHistoryRows)
        {
            foreach (var preparedProfile in preparedProfiles)
            {
                var previewRun = preparedProfile.PreviewRun;
                if (previewRun is not null && previewRun.RunId != Guid.Empty)
                    ids.Add(previewRun.RunId);
            }
        }

        return ids.ToList();
    }

    private static bool IsPastBillingRun(RentalBillingRunModel run, DateOnly referenceMonth)
    {
        var runMonth = new DateOnly(run.ScheduledDate.Year, run.ScheduledDate.Month, 1);
        return runMonth.DayNumber < referenceMonth.DayNumber;
    }

    private static List<RentalBillingRunModel> ResolveBillingRunsForRowBuild(
        IEnumerable<RentalBillingRunModel> runs,
        DateOnly referenceDate,
        bool includeHistoryRows)
    {
        if (includeHistoryRows)
            return DeduplicateBillingRuns(runs);

        var referenceMonth = new DateOnly(referenceDate.Year, referenceDate.Month, 1);
        var result = new List<RentalBillingRunModel>();
        var seenRunIds = new HashSet<Guid>();
        foreach (var run in runs)
        {
            if (run.RunId == Guid.Empty || !seenRunIds.Add(run.RunId))
                continue;

            if (IsPastBillingRun(run, referenceMonth))
                result.Add(run);
        }

        return result;
    }

    private static bool NeedsBillingProfileCustomerNameLookup(LocalRentalBillingProfile profile)
    {
        if (!profile.CustomerId.HasValue || profile.CustomerId.Value == Guid.Empty)
            return false;

        if (string.IsNullOrWhiteSpace(profile.CustomerName))
            return true;

        return !string.IsNullOrWhiteSpace(profile.ProfileKey);
    }

    private RentalBillingViewRow CreateBillingViewRow(
        RentalBillingPreparedProfile preparedProfile,
        IReadOnlyDictionary<Guid, string> customerNameMap,
        IReadOnlyDictionary<Guid, RentalBillingRunSettlementInfo> settlementByRun,
        IReadOnlyDictionary<Guid, RentalBillingRunInvoiceInfo> invoiceByRun,
        IReadOnlyDictionary<string, string> offices,
        DateOnly referenceDate,
        bool includeHistoryRows)
    {
        var profile = preparedProfile.Profile;
        var profileAssets = preparedProfile.ProfileAssets;
        var templateItems = preparedProfile.TemplateItems;
        var currentRun = preparedProfile.PreviewRun;
        var profileRuns = preparedProfile.BillingRuns;
        var customerDisplayName = ResolveBillingProfileCustomerDisplayName(profile, customerNameMap);
        var explicitIncludedAssetCount = CountDistinctTemplateIncludedAssets(templateItems);
        var includedAssetCount = explicitIncludedAssetCount > 0 ? explicitIncludedAssetCount : profileAssets.Count;
        var assetSummary = BuildBillingAssetRowSummary(profile, profileAssets);
        var nextBillingDate = GetNextBillingDate(profile, referenceDate);
        var documentIssueDate = nextBillingDate.HasValue
            ? RentalBillingScheduleRules.CalculateDocumentIssueDate(nextBillingDate, profile.DocumentIssueMode, profile.DocumentLeadDays)
            : null;
        var alertDate = nextBillingDate.HasValue
            ? RentalBillingScheduleRules.ResolveAlertDate(nextBillingDate.Value, documentIssueDate)
            : (DateOnly?)null;
        var daysRemaining = alertDate.HasValue
            ? alertDate.Value.DayNumber - referenceDate.DayNumber
            : nextBillingDate.HasValue
                ? nextBillingDate.Value.DayNumber - referenceDate.DayNumber
                : (int?)null;
        var billedAmount = currentRun?.BilledAmount ?? profile.MonthlyAmount;
        var settledAmount = currentRun is not null
            ? settlementByRun.TryGetValue(currentRun.RunId, out var runSettlementInfo)
                ? runSettlementInfo.SettledAmount
                : Math.Max(0m, currentRun.SettledAmount)
            : Math.Max(0m, profile.SettledAmount);
        var outstandingAmount = Math.Max(0m, billedAmount - settledAmount);
        var currentRunStatus = currentRun?.Status ?? string.Empty;
        if (string.IsNullOrWhiteSpace(currentRunStatus) || string.Equals(currentRunStatus, PaymentFlowConstants.BillingStatusPlanned, StringComparison.OrdinalIgnoreCase))
        {
            currentRunStatus = outstandingAmount <= 0m && billedAmount > 0m
                ? PaymentFlowConstants.BillingStatusCompleted
                : settledAmount > 0m
                    ? PaymentFlowConstants.BillingStatusInProgress
                    : PaymentFlowConstants.BillingStatusPlanned;
        }

        var dataIssues = BuildBillingDataIssues(profile, assetSummary, profileAssets, templateItems);
        var historyRows = includeHistoryRows
            ? BuildBillingHistoryRows(
                profile,
                customerDisplayName,
                profileRuns,
                settlementByRun,
                invoiceByRun,
                referenceDate)
            : new List<RentalBillingHistoryRow>();
        var historySummary = includeHistoryRows
            ? BuildBillingHistorySummary(historyRows)
            : BuildBillingHistorySummary(profile, profileRuns, settlementByRun, invoiceByRun, referenceDate);
        return new RentalBillingViewRow
        {
            SelectionId = profile.Id,
            HasPersistedProfile = true,
            Source = profile,
            GroupedSourceCount = 1,
            GroupedPersistedProfileCount = 1,
            GroupedUnlinkedAssetCount = 0,
            GroupedSelectionIds = new List<Guid> { profile.Id },
            GroupedPersistedProfileIds = new List<Guid> { profile.Id },
            GroupedProfileRevisions = new Dictionary<Guid, long> { [profile.Id] = profile.Revision },
            CustomerDisplayName = customerDisplayName,
            BillingCycleDisplay = profile.BillingCycleMonths > 0 ? $"{profile.BillingCycleMonths}개월" : string.Empty,
            ResponsibleOfficeName = ResolveOfficeDisplayName(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode, offices),
            NextBillingDate = nextBillingDate,
            DaysRemaining = daysRemaining,
            DisplayStatus = BuildBillingDisplayStatus(profile, alertDate ?? nextBillingDate, daysRemaining),
            SettlementStatus = currentRun is not null
                ? DetermineBillingSettlementStatus(profile, settledAmount, billedAmount)
                : PaymentFlowConstants.NormalizeSettlementStatus(profile.SettlementStatus),
            CompletionStatus = outstandingAmount <= 0m
                ? PaymentFlowConstants.CompletionDone
                : PaymentFlowConstants.NormalizeCompletionStatus(profile.CompletionStatus),
            SettledAmount = settledAmount,
            OutstandingAmount = outstandingAmount,
            RequiresFollowUp = profile.RequiresFollowUp || outstandingAmount > 0m,
            LastSettledDate = profile.LastSettledDate,
            AssetCount = assetSummary.AssetCount,
            TemplateItemCount = templateItems.Count,
            IncludedAssetCount = includedAssetCount,
            BillingType = string.IsNullOrWhiteSpace(profile.BillingType) ? "묶음" : profile.BillingType,
            InstallSiteName = profile.InstallSiteName ?? string.Empty,
            InstallLocationDisplay = assetSummary.InstallLocationDisplay,
            BillingAdvanceMode = string.IsNullOrWhiteSpace(profile.BillingAdvanceMode) ? "후불" : profile.BillingAdvanceMode,
            BillingDayMode = RentalBillingScheduleRules.NormalizeBillingDayMode(profile.BillingDayMode),
            BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
                profile.BillingCycleMonths,
                profile.BillingAnchorMonth,
                profile.BillingAnchorDate,
                profile.BillingStartDate,
                profile.ContractStartDate,
                profile.ContractDate,
                profile.LastBilledDate,
                referenceDate),
            DocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(profile.DocumentIssueMode),
            DocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(profile.DocumentLeadDays),
            DocumentIssueDate = documentIssueDate,
            AlertDate = alertDate,
            AlertReason = nextBillingDate.HasValue
                ? RentalBillingScheduleRules.ResolveAlertReason(nextBillingDate.Value, documentIssueDate)
                : string.Empty,
            CurrentBillingRunId = currentRun?.RunId,
            CurrentBillingPeriodLabel = currentRun?.PeriodLabel ?? string.Empty,
            CurrentBillingRunStatus = currentRunStatus,
            CurrentBilledAmount = billedAmount,
            BillingHistoryRows = historyRows,
            PastUnresolvedCount = historySummary.PastUnresolvedCount,
            PastUnresolvedAmount = historySummary.PastUnresolvedAmount,
            OldestPastUnresolvedScheduledDate = historySummary.OldestPastUnresolvedScheduledDate,
            OldestPastUnresolvedPeriodLabel = historySummary.OldestPastUnresolvedPeriodLabel,
            HasDataIssue = dataIssues.Count > 0,
            DataIssueSummary = dataIssues.Count == 0 ? string.Empty : string.Join(" / ", dataIssues)
        };
    }

    private async Task<(Dictionary<Guid, RentalBillingRunSettlementInfo> SettlementByRun, Dictionary<Guid, RentalBillingRunInvoiceInfo> InvoiceByRun)> LoadBillingRunReferencesAsync(
        IReadOnlyCollection<Guid> runIds,
        CancellationToken ct)
    {
        var ids = runIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return (new Dictionary<Guid, RentalBillingRunSettlementInfo>(), new Dictionary<Guid, RentalBillingRunInvoiceInfo>());

        var settlementByRun = new Dictionary<Guid, RentalBillingRunSettlementInfo>();
        var invoiceCandidatesByRun = new Dictionary<Guid, RentalBillingRunInvoiceLookup>();
        foreach (var batchIds in ids.Chunk(BillingRunReferenceBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds;
            var settlementRows = await _db.Transactions.AsNoTracking()
                .Where(transaction => !transaction.IsDeleted &&
                                      transaction.LinkedRentalBillingRunId.HasValue &&
                                      scopedBatchIds.Contains(transaction.LinkedRentalBillingRunId.Value))
                .Select(transaction => new RentalBillingRunSettlementLookup(
                    transaction.LinkedRentalBillingRunId!.Value,
                    transaction.SettlementAmount,
                    transaction.TransactionDate))
                .ToListAsync(ct);
            foreach (var row in settlementRows)
            {
                if (settlementByRun.TryGetValue(row.RunId, out var existing))
                {
                    var lastSettledDate = existing.LastSettledDate.HasValue &&
                                          existing.LastSettledDate.Value >= row.TransactionDate
                        ? existing.LastSettledDate.Value
                        : row.TransactionDate;
                    settlementByRun[row.RunId] = new RentalBillingRunSettlementInfo(
                        existing.SettledAmount + row.SettlementAmount,
                        lastSettledDate);
                }
                else
                {
                    settlementByRun[row.RunId] = new RentalBillingRunSettlementInfo(
                        row.SettlementAmount,
                        row.TransactionDate);
                }
            }

            var invoiceRows = await _db.Invoices.AsNoTracking()
                .Where(invoice => !invoice.IsDeleted &&
                                  invoice.LinkedRentalBillingRunId.HasValue &&
                                  scopedBatchIds.Contains(invoice.LinkedRentalBillingRunId.Value))
                .Select(invoice => new RentalBillingRunInvoiceLookup(
                    invoice.LinkedRentalBillingRunId!.Value,
                    invoice.Id,
                    invoice.TotalAmount,
                    invoice.UpdatedAtUtc))
                .ToListAsync(ct);
            foreach (var row in invoiceRows)
            {
                if (!invoiceCandidatesByRun.TryGetValue(row.RunId, out var existing) ||
                    row.UpdatedAtUtc > existing.UpdatedAtUtc)
                {
                    invoiceCandidatesByRun[row.RunId] = row;
                }
            }
        }

        var invoiceByRun = invoiceCandidatesByRun.ToDictionary(
            pair => pair.Key,
            pair => new RentalBillingRunInvoiceInfo(pair.Value.InvoiceId, pair.Value.TotalAmount));

        return (settlementByRun, invoiceByRun);
    }

    private readonly record struct RentalBillingRunSettlementLookup(
        Guid RunId,
        decimal SettlementAmount,
        DateOnly TransactionDate);

    private readonly record struct RentalBillingRunSettlementInfo(decimal SettledAmount, DateOnly? LastSettledDate);

    private readonly record struct RentalBillingRunInvoiceInfo(Guid InvoiceId, decimal TotalAmount);

    private readonly record struct RentalBillingRunInvoiceLookup(
        Guid RunId,
        Guid InvoiceId,
        decimal TotalAmount,
        DateTime UpdatedAtUtc);

    private readonly record struct RentalBillingHistorySummary(
        int PastUnresolvedCount,
        decimal PastUnresolvedAmount,
        DateOnly? OldestPastUnresolvedScheduledDate,
        string OldestPastUnresolvedPeriodLabel);

    private readonly record struct GroupedBillingRowMetrics(
        int GroupedUnlinkedAssetCount,
        int GroupedSourceCount,
        DateOnly? NextBillingDate,
        DateOnly? DocumentIssueDate,
        DateOnly? AlertDate,
        DateOnly? LastSettledDate,
        int? DaysRemaining,
        bool AllRowsCompleted,
        decimal SettledAmount,
        decimal OutstandingAmount,
        bool RequiresFollowUp,
        int AssetCount,
        int TemplateItemCount,
        int IncludedAssetCount,
        decimal CurrentBilledAmount,
        bool HasDataIssue,
        RentalBillingHistorySummary HistorySummary);

    private readonly record struct GroupedBillingTextMetrics(
        List<string> DistinctCycles,
        List<string> DistinctBillingTypes,
        List<string> DistinctAdvanceModes,
        List<string> DistinctRunStatuses,
        List<string> DistinctPeriodLabels,
        List<string> DistinctSettlementStatuses,
        List<string> DistinctDisplayStatuses,
        List<string> DistinctInstallLocations,
        List<string> DataIssues);

    private readonly record struct GroupedBillingIdentityMetrics(
        List<Guid> GroupedSelectionIds,
        List<Guid> GroupedPersistedProfileIds,
        Dictionary<Guid, long> GroupedProfileRevisions);

    private readonly record struct RentalBillingAssetRowSummary(
        int AssetCount,
        string InstallLocationDisplay,
        bool HasMissingMonthlyFee,
        bool HasEligibilityReviewRequired);

    private readonly record struct RentalBillingTemplateIssueSummary(
        int TemplateItemCount,
        bool HasUnlinkedTemplateItem,
        bool HasMissingBillingLineMode);

    private readonly record struct RentalBillingPreparedProfile(
        LocalRentalBillingProfile Profile,
        List<LocalRentalAsset> ProfileAssets,
        List<RentalBillingTemplateItemModel> TemplateItems,
        RentalBillingRunModel? PreviewRun,
        List<RentalBillingRunModel> BillingRuns);

    private static List<RentalBillingHistoryRow> BuildBillingHistoryRows(
        LocalRentalBillingProfile profile,
        string customerDisplayName,
        IReadOnlyCollection<RentalBillingRunModel> runs,
        IReadOnlyDictionary<Guid, RentalBillingRunSettlementInfo> settlementByRun,
        IReadOnlyDictionary<Guid, RentalBillingRunInvoiceInfo> invoiceByRun,
        DateOnly referenceDate)
    {
        if (runs.Count == 0)
            return new List<RentalBillingHistoryRow>();

        var referenceMonth = new DateOnly(referenceDate.Year, referenceDate.Month, 1);
        return runs
            .Where(run => run.RunId != Guid.Empty)
            .OrderByDescending(run => run.ScheduledDate)
            .ThenByDescending(run => run.PeriodEndDate)
            .GroupBy(run => run.RunId)
            .Select(group => group.First())
            .Select(run =>
            {
                var billedAmount = Math.Max(0m, run.BilledAmount);
                if (invoiceByRun.TryGetValue(run.RunId, out var invoiceInfo) && invoiceInfo.TotalAmount > 0m)
                    billedAmount = invoiceInfo.TotalAmount;

                var settlementInfo = settlementByRun.TryGetValue(run.RunId, out var foundSettlement)
                    ? foundSettlement
                    : new RentalBillingRunSettlementInfo(Math.Max(0m, run.SettledAmount), run.SettledDate);
                var settledAmount = Math.Max(0m, settlementInfo.SettledAmount);
                var outstandingAmount = Math.Max(0m, billedAmount - settledAmount);
                var runMonth = new DateOnly(run.ScheduledDate.Year, run.ScheduledDate.Month, 1);
                var isPastUnresolved = runMonth < referenceMonth && billedAmount > 0m && outstandingAmount > 0m;
                var settlementStatus = ResolveBillingHistorySettlementStatus(profile, run, settledAmount, billedAmount, outstandingAmount);

                return new RentalBillingHistoryRow
                {
                    BillingProfileId = profile.Id,
                    BillingRunId = run.RunId,
                    CustomerName = customerDisplayName,
                    PeriodLabel = string.IsNullOrWhiteSpace(run.PeriodLabel)
                        ? BuildBillingPeriodLabel(run.PeriodStartDate, run.PeriodEndDate)
                        : run.PeriodLabel,
                    ScheduledDate = run.ScheduledDate,
                    BilledAmount = billedAmount,
                    SettledAmount = settledAmount,
                    OutstandingAmount = outstandingAmount,
                    SettledDate = settlementInfo.LastSettledDate ?? run.SettledDate,
                    BillingStatus = ResolveBillingHistoryStatus(run, outstandingAmount, settledAmount),
                    SettlementStatus = settlementStatus,
                    HasInvoice = invoiceByRun.ContainsKey(run.RunId),
                    InvoiceId = invoiceByRun.TryGetValue(run.RunId, out var invoice) ? invoice.InvoiceId : null,
                    IsPastUnresolved = isPastUnresolved
                };
            })
            .ToList();
    }

    private static RentalBillingHistorySummary BuildBillingHistorySummary(
        IReadOnlyCollection<RentalBillingHistoryRow> historyRows)
    {
        if (historyRows.Count == 0)
            return default;

        var count = 0;
        var amount = 0m;
        DateOnly? oldestScheduledDate = null;
        string oldestPeriodLabel = string.Empty;
        foreach (var history in historyRows)
        {
            if (!history.IsPastUnresolved)
                continue;

            count++;
            amount += history.OutstandingAmount;
            if (!oldestScheduledDate.HasValue || history.ScheduledDate < oldestScheduledDate.Value)
            {
                oldestScheduledDate = history.ScheduledDate;
                oldestPeriodLabel = history.PeriodLabel;
            }
        }

        return new RentalBillingHistorySummary(
            count,
            amount,
            oldestScheduledDate,
            oldestPeriodLabel);
    }

    private static RentalBillingHistorySummary BuildBillingHistorySummary(
        LocalRentalBillingProfile profile,
        IReadOnlyCollection<RentalBillingRunModel> runs,
        IReadOnlyDictionary<Guid, RentalBillingRunSettlementInfo> settlementByRun,
        IReadOnlyDictionary<Guid, RentalBillingRunInvoiceInfo> invoiceByRun,
        DateOnly referenceDate)
    {
        if (runs.Count == 0)
            return default;

        var referenceMonth = new DateOnly(referenceDate.Year, referenceDate.Month, 1);
        var pastUnresolvedCount = 0;
        var pastUnresolvedAmount = 0m;
        DateOnly? oldestScheduledDate = null;
        var oldestPeriodLabel = string.Empty;

        var seenRunIds = new HashSet<Guid>();
        foreach (var run in runs)
        {
            if (run.RunId == Guid.Empty || !seenRunIds.Add(run.RunId))
                continue;

            var billedAmount = Math.Max(0m, run.BilledAmount);
            if (invoiceByRun.TryGetValue(run.RunId, out var invoiceInfo) && invoiceInfo.TotalAmount > 0m)
                billedAmount = invoiceInfo.TotalAmount;

            var settlementInfo = settlementByRun.TryGetValue(run.RunId, out var foundSettlement)
                ? foundSettlement
                : new RentalBillingRunSettlementInfo(Math.Max(0m, run.SettledAmount), run.SettledDate);
            var settledAmount = Math.Max(0m, settlementInfo.SettledAmount);
            var outstandingAmount = Math.Max(0m, billedAmount - settledAmount);
            var runMonth = new DateOnly(run.ScheduledDate.Year, run.ScheduledDate.Month, 1);
            if (runMonth.DayNumber >= referenceMonth.DayNumber || billedAmount <= 0m || outstandingAmount <= 0m)
                continue;

            pastUnresolvedCount++;
            pastUnresolvedAmount += outstandingAmount;
            if (!oldestScheduledDate.HasValue || run.ScheduledDate.DayNumber < oldestScheduledDate.Value.DayNumber)
            {
                oldestScheduledDate = run.ScheduledDate;
                oldestPeriodLabel = string.IsNullOrWhiteSpace(run.PeriodLabel)
                    ? BuildBillingPeriodLabel(run.PeriodStartDate, run.PeriodEndDate)
                    : run.PeriodLabel;
            }
        }

        return new RentalBillingHistorySummary(
            pastUnresolvedCount,
            pastUnresolvedAmount,
            oldestScheduledDate,
            oldestPeriodLabel);
    }

    private static List<RentalBillingRunModel> DeduplicateBillingRuns(IEnumerable<RentalBillingRunModel> runs)
    {
        var result = new List<RentalBillingRunModel>();
        var seenRunIds = new HashSet<Guid>();
        foreach (var run in runs)
        {
            if (run.RunId == Guid.Empty || !seenRunIds.Add(run.RunId))
                continue;

            result.Add(run);
        }

        return result;
    }

    private static Dictionary<Guid, List<RentalBillingRunModel>> LimitBillingRunsForHistoryDisplay(
        IReadOnlyDictionary<Guid, List<RentalBillingRunModel>> billingRunsByProfile,
        int maxDisplayRows)
    {
        if (maxDisplayRows <= 0)
            return billingRunsByProfile.ToDictionary(pair => pair.Key, pair => pair.Value);

        var normalizedDisplayLimit = Math.Clamp(maxDisplayRows, 1, 5_000);
        return billingRunsByProfile
            .SelectMany(pair => pair.Value
                .Where(run => run.RunId != Guid.Empty)
                .Select(run => new
                {
                    ProfileId = pair.Key,
                    Run = run
                }))
            .OrderByDescending(entry => entry.Run.ScheduledDate)
            .ThenByDescending(entry => entry.Run.PeriodEndDate)
            .ThenBy(entry => entry.ProfileId)
            .Take(normalizedDisplayLimit)
            .GroupBy(entry => entry.ProfileId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Run).ToList());
    }

    private static GroupedBillingRowMetrics BuildGroupedBillingRowMetrics(
        IReadOnlyList<RentalBillingViewRow> rows)
    {
        var groupedUnlinkedAssetCount = 0;
        var groupedSourceCount = 0;
        DateOnly? nextBillingDate = null;
        DateOnly? documentIssueDate = null;
        DateOnly? alertDate = null;
        DateOnly? lastSettledDate = null;
        int? daysRemaining = null;
        var allRowsCompleted = true;
        var settledAmount = 0m;
        var outstandingAmount = 0m;
        var requiresFollowUp = false;
        var assetCount = 0;
        var templateItemCount = 0;
        var includedAssetCount = 0;
        var currentBilledAmount = 0m;
        var hasDataIssue = false;
        var pastUnresolvedCount = 0;
        var pastUnresolvedAmount = 0m;
        DateOnly? oldestScheduledDate = null;
        string oldestPeriodLabel = string.Empty;
        foreach (var row in rows)
        {
            groupedUnlinkedAssetCount += row.GroupedUnlinkedAssetCount;
            groupedSourceCount += Math.Max(1, row.GroupedSourceCount);
            if (row.NextBillingDate.HasValue &&
                (!nextBillingDate.HasValue || row.NextBillingDate.Value < nextBillingDate.Value))
            {
                nextBillingDate = row.NextBillingDate.Value;
            }

            if (row.DocumentIssueDate.HasValue &&
                (!documentIssueDate.HasValue || row.DocumentIssueDate.Value < documentIssueDate.Value))
            {
                documentIssueDate = row.DocumentIssueDate.Value;
            }

            if (row.AlertDate.HasValue &&
                (!alertDate.HasValue || row.AlertDate.Value < alertDate.Value))
            {
                alertDate = row.AlertDate.Value;
            }

            if (row.LastSettledDate.HasValue &&
                (!lastSettledDate.HasValue || row.LastSettledDate.Value > lastSettledDate.Value))
            {
                lastSettledDate = row.LastSettledDate.Value;
            }

            if (row.DaysRemaining.HasValue &&
                (!daysRemaining.HasValue || row.DaysRemaining.Value < daysRemaining.Value))
            {
                daysRemaining = row.DaysRemaining.Value;
            }

            if (!string.Equals(row.CompletionStatus, PaymentFlowConstants.CompletionDone, StringComparison.OrdinalIgnoreCase))
                allRowsCompleted = false;
            settledAmount += row.SettledAmount;
            outstandingAmount += row.OutstandingAmount;
            requiresFollowUp |= row.RequiresFollowUp;
            assetCount += row.AssetCount;
            templateItemCount += row.TemplateItemCount;
            includedAssetCount += row.IncludedAssetCount;
            currentBilledAmount += row.CurrentBilledAmount;
            hasDataIssue |= row.HasDataIssue;

            pastUnresolvedCount += row.PastUnresolvedCount;
            pastUnresolvedAmount += row.PastUnresolvedAmount;
            if (row.OldestPastUnresolvedScheduledDate.HasValue)
            {
                var scheduledDate = row.OldestPastUnresolvedScheduledDate.Value;
                if (!oldestScheduledDate.HasValue || scheduledDate < oldestScheduledDate.Value)
                {
                    oldestScheduledDate = scheduledDate;
                    oldestPeriodLabel = row.OldestPastUnresolvedPeriodLabel;
                }
            }
        }

        var historySummary = new RentalBillingHistorySummary(
            pastUnresolvedCount,
            pastUnresolvedAmount,
            oldestScheduledDate,
            oldestPeriodLabel);
        return new GroupedBillingRowMetrics(
            groupedUnlinkedAssetCount,
            groupedSourceCount,
            nextBillingDate,
            documentIssueDate,
            alertDate,
            lastSettledDate,
            daysRemaining,
            allRowsCompleted,
            settledAmount,
            outstandingAmount,
            requiresFollowUp,
            assetCount,
            templateItemCount,
            includedAssetCount,
            currentBilledAmount,
            hasDataIssue,
            historySummary);
    }

    private static GroupedBillingTextMetrics BuildGroupedBillingTextMetrics(
        IReadOnlyList<RentalBillingViewRow> rows)
    {
        var distinctCycles = new List<string>();
        var distinctCycleKeys = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var distinctBillingTypes = new List<string>();
        var distinctBillingTypeKeys = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var distinctAdvanceModes = new List<string>();
        var distinctAdvanceModeKeys = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var distinctRunStatuses = new List<string>();
        var distinctRunStatusKeys = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var distinctPeriodLabels = new List<string>();
        var distinctPeriodLabelKeys = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var distinctSettlementStatuses = new List<string>();
        var distinctSettlementStatusKeys = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var distinctDisplayStatuses = new List<string>();
        var distinctDisplayStatusKeys = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var distinctInstallLocations = new List<string>();
        var distinctInstallLocationKeys = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var dataIssues = new List<string>();
        var dataIssueKeys = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var row in rows)
        {
            AddDistinctTrimmed(distinctCycles, distinctCycleKeys, row.BillingCycleDisplay);
            AddDistinctTrimmed(distinctBillingTypes, distinctBillingTypeKeys, row.BillingType);
            AddDistinctTrimmed(distinctAdvanceModes, distinctAdvanceModeKeys, row.BillingAdvanceMode);
            AddDistinctTrimmed(distinctRunStatuses, distinctRunStatusKeys, row.CurrentBillingRunStatus);
            AddDistinctTrimmed(distinctPeriodLabels, distinctPeriodLabelKeys, row.CurrentBillingPeriodLabel);
            AddDistinctTrimmed(distinctSettlementStatuses, distinctSettlementStatusKeys, row.SettlementStatus);
            AddDistinctTrimmed(distinctDisplayStatuses, distinctDisplayStatusKeys, row.DisplayStatus);
            AddDistinctTrimmed(
                distinctInstallLocations,
                distinctInstallLocationKeys,
                string.IsNullOrWhiteSpace(row.InstallLocationDisplay)
                    ? row.InstallSiteName
                    : row.InstallLocationDisplay);
            foreach (var dataIssue in ExtractBillingDataIssueTokens(row))
                AddDistinctTrimmed(dataIssues, dataIssueKeys, dataIssue);
        }

        return new GroupedBillingTextMetrics(
            distinctCycles,
            distinctBillingTypes,
            distinctAdvanceModes,
            distinctRunStatuses,
            distinctPeriodLabels,
            distinctSettlementStatuses,
            distinctDisplayStatuses,
            distinctInstallLocations,
            dataIssues);
    }

    private static GroupedBillingIdentityMetrics BuildGroupedBillingIdentityMetrics(
        IReadOnlyList<RentalBillingViewRow> rows)
    {
        var groupedSelectionIds = new List<Guid>();
        var groupedSelectionIdSet = new HashSet<Guid>();
        var groupedPersistedProfileIds = new List<Guid>();
        var groupedPersistedProfileIdSet = new HashSet<Guid>();
        var groupedProfileRevisions = new Dictionary<Guid, long>();

        foreach (var row in rows)
        {
            if (row.GroupedSelectionIds.Count == 0)
            {
                AddDistinctGuid(groupedSelectionIds, groupedSelectionIdSet, row.SelectionId);
            }
            else
            {
                foreach (var id in row.GroupedSelectionIds)
                    AddDistinctGuid(groupedSelectionIds, groupedSelectionIdSet, id);
            }

            foreach (var id in row.GroupedPersistedProfileIds)
                AddDistinctGuid(groupedPersistedProfileIds, groupedPersistedProfileIdSet, id);

            if (row.GroupedProfileRevisions.Count > 0)
            {
                foreach (var pair in row.GroupedProfileRevisions)
                    AddMaxRevision(groupedProfileRevisions, pair.Key, pair.Value);
            }
            else if (row.HasPersistedProfile && row.Source.Id != Guid.Empty)
            {
                AddMaxRevision(groupedProfileRevisions, row.Source.Id, row.Source.Revision);
            }
        }

        return new GroupedBillingIdentityMetrics(
            groupedSelectionIds,
            groupedPersistedProfileIds,
            groupedProfileRevisions);
    }

    private static void AddDistinctGuid(
        List<Guid> values,
        HashSet<Guid> keys,
        Guid value)
    {
        if (value != Guid.Empty && keys.Add(value))
            values.Add(value);
    }

    private static void AddMaxRevision(
        Dictionary<Guid, long> revisions,
        Guid id,
        long revision)
    {
        if (id == Guid.Empty)
            return;

        if (!revisions.TryGetValue(id, out var existing) || revision > existing)
            revisions[id] = revision;
    }

    private static void AddDistinctTrimmed(
        List<string> values,
        HashSet<string> keys,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var trimmed = value.Trim();
        if (trimmed.Length > 0 && keys.Add(trimmed))
            values.Add(trimmed);
    }

    private static void AddDistinctTrimmed(
        List<string> values,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var trimmed = value.Trim();
        if (trimmed.Length > 0 && !values.Contains(trimmed, StringComparer.CurrentCultureIgnoreCase))
            values.Add(trimmed);
    }

    private static string ResolveBillingHistoryStatus(
        RentalBillingRunModel run,
        decimal outstandingAmount,
        decimal settledAmount)
    {
        if (outstandingAmount <= 0m && run.BilledAmount > 0m)
            return PaymentFlowConstants.BillingStatusCompleted;
        if (settledAmount > 0m)
            return PaymentFlowConstants.BillingStatusInProgress;

        var normalized = (run.Status ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? PaymentFlowConstants.BillingStatusPlanned
            : normalized;
    }

    private static string ResolveBillingHistorySettlementStatus(
        LocalRentalBillingProfile profile,
        RentalBillingRunModel run,
        decimal settledAmount,
        decimal billedAmount,
        decimal outstandingAmount)
    {
        if (outstandingAmount <= 0m && billedAmount > 0m)
            return PaymentFlowConstants.SettlementStatusConfirmed;
        if (settledAmount > 0m)
            return PaymentFlowConstants.SettlementStatusPartial;

        var normalized = PaymentFlowConstants.NormalizeSettlementStatus(run.SettlementStatus);
        return string.IsNullOrWhiteSpace(normalized)
            ? PaymentFlowConstants.NormalizeSettlementStatus(profile.SettlementStatus)
            : normalized;
    }

    private IQueryable<LocalRentalAsset> ApplyUnlinkedBillingAssetFilter(
        IQueryable<LocalRentalAsset> query,
        RentalBillingFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var keyword = filter.SearchText.Trim();
            query = query.Where(asset =>
                asset.CustomerName.Contains(keyword) ||
                asset.CurrentCustomerName.Contains(keyword) ||
                asset.ItemName.Contains(keyword) ||
                asset.InstallLocation.Contains(keyword) ||
                asset.InstallSiteName.Contains(keyword) ||
                asset.ManagementNumber.Contains(keyword) ||
                asset.MachineNumber.Contains(keyword));
        }

        if (!string.IsNullOrWhiteSpace(filter.OfficeCode))
        {
            query = query.Where(asset =>
                asset.ResponsibleOfficeCode == filter.OfficeCode ||
                ((asset.ResponsibleOfficeCode == null ||
                  asset.ResponsibleOfficeCode == string.Empty ||
                  asset.ResponsibleOfficeCode == OfficeCodeCatalog.Shared) &&
                 asset.ManagementCompanyCode == filter.OfficeCode));
        }

        return query;
    }

    private static async Task<List<LocalRentalAsset>> LoadUnlinkedBillingAssetSearchResultsAsync(
        IQueryable<LocalRentalAsset> baseQuery,
        string keyword,
        int maxResults,
        CancellationToken ct)
    {
        var assets = new List<LocalRentalAsset>(maxResults);
        await AddDistinctUnlinkedBillingAssetSearchResultsAsync(
            assets,
            ApplyUnlinkedBillingAssetSearchPrefixFilter(baseQuery, keyword),
            maxResults,
            orderByListColumns: true,
            ct);

        if (assets.Count < maxResults)
        {
            await AddDistinctUnlinkedBillingAssetSearchResultsAsync(
                assets,
                ApplyUnlinkedBillingAssetSearchContainsFilter(baseQuery, keyword),
                maxResults,
                orderByListColumns: false,
                ct);
        }

        return assets;
    }

    private static async Task AddDistinctUnlinkedBillingAssetSearchResultsAsync(
        List<LocalRentalAsset> assets,
        IQueryable<LocalRentalAsset> query,
        int maxResults,
        bool orderByListColumns,
        CancellationToken ct)
    {
        var remaining = maxResults - assets.Count;
        if (remaining <= 0)
            return;

        var existingIds = assets.Select(asset => asset.Id).ToList();
        if (existingIds.Count > 0)
            query = query.Where(asset => !existingIds.Contains(asset.Id));

        IQueryable<LocalRentalAsset> projectedQuery = SelectBillingAssetListProjection(query);
        if (orderByListColumns)
        {
            projectedQuery = projectedQuery
                .OrderBy(asset => asset.CustomerName)
                .ThenBy(asset => asset.CurrentCustomerName)
                .ThenBy(asset => asset.ManagementNumber);
        }

        var nextAssets = await projectedQuery
            .Take(remaining)
            .ToListAsync(ct);
        assets.AddRange(nextAssets);
    }

    private static IQueryable<LocalRentalAsset> ApplyUnlinkedBillingAssetSearchPrefixFilter(
        IQueryable<LocalRentalAsset> query,
        string keyword)
        => query.Where(asset =>
            asset.CustomerName.StartsWith(keyword) ||
            asset.CurrentCustomerName.StartsWith(keyword) ||
            asset.ItemName.StartsWith(keyword) ||
            asset.InstallLocation.StartsWith(keyword) ||
            asset.InstallSiteName.StartsWith(keyword) ||
            asset.ManagementNumber.StartsWith(keyword) ||
            asset.MachineNumber.StartsWith(keyword));

    private static IQueryable<LocalRentalAsset> ApplyUnlinkedBillingAssetSearchContainsFilter(
        IQueryable<LocalRentalAsset> query,
        string keyword)
        => query.Where(asset =>
            asset.CustomerName.Contains(keyword) ||
            asset.CurrentCustomerName.Contains(keyword) ||
            asset.ItemName.Contains(keyword) ||
            asset.InstallLocation.Contains(keyword) ||
            asset.InstallSiteName.Contains(keyword) ||
            asset.ManagementNumber.Contains(keyword) ||
            asset.MachineNumber.Contains(keyword));

    private static bool ShouldIncludeUnlinkedBillingAssets(string? status)
        => string.IsNullOrWhiteSpace(status) ||
           IsUnlinkedBillingStatusFilter(status);

    private static bool ShouldLoadUnlinkedBillingAssets(RentalBillingFilter filter)
    {
        if (filter.DueOnly || filter.PastDueOnly)
            return false;

        return ShouldIncludeUnlinkedBillingAssets(filter.Status);
    }

    private static bool ShouldDeferSupplementalUnlinkedBillingAssets(
        RentalBillingFilter filter,
        int? profileResultLimit,
        int profileCount)
    {
        if (!profileResultLimit.HasValue)
            return false;

        if (profileCount < profileResultLimit.Value)
            return false;

        return !IsUnlinkedBillingStatusFilter(filter.Status);
    }

    private static List<LocalRentalBillingProfile> ApplyDueOnlyProfilePrefilter(
        List<LocalRentalBillingProfile> profiles,
        RentalBillingFilter filter,
        int alertWindow,
        DateOnly referenceDate)
    {
        if (!ShouldPrefilterDueOnlyBillingProfiles(filter) || profiles.Count == 0)
            return profiles;

        var dueProfileIds = profiles
            .Where(profile => IsBillingProfileDueWithinAlertWindow(profile, alertWindow, referenceDate))
            .Select(profile => profile.Id)
            .ToHashSet();
        if (dueProfileIds.Count == 0)
            return new List<LocalRentalBillingProfile>();

        if (filter.ExpandCustomerSummaryRows)
            return profiles
                .Where(profile => dueProfileIds.Contains(profile.Id))
                .ToList();

        var dueGroupKeys = profiles
            .Where(profile => dueProfileIds.Contains(profile.Id))
            .Select(BuildBillingProfileGroupKey)
            .ToHashSet(StringComparer.Ordinal);
        return profiles
            .Where(profile => dueGroupKeys.Contains(BuildBillingProfileGroupKey(profile)))
            .ToList();
    }

    private static bool ShouldPrefilterDueOnlyBillingProfiles(RentalBillingFilter filter)
        => filter.DueOnly;

    private static bool IsBillingProfileDueWithinAlertWindow(
        LocalRentalBillingProfile profile,
        int alertWindow,
        DateOnly referenceDate)
    {
        var daysRemaining = ResolveBillingAlertDaysRemaining(profile, referenceDate);
        return daysRemaining.HasValue && daysRemaining.Value <= alertWindow;
    }

    private static int? ResolveBillingAlertDaysRemaining(LocalRentalBillingProfile profile, DateOnly referenceDate)
    {
        var nextBillingDate = ResolveNextBillingDateForAlertFilter(profile, referenceDate);
        if (!nextBillingDate.HasValue)
            return null;

        var documentIssueDate = RentalBillingScheduleRules.CalculateDocumentIssueDate(
            nextBillingDate,
            profile.DocumentIssueMode,
            profile.DocumentLeadDays);
        var alertDate = RentalBillingScheduleRules.ResolveAlertDate(nextBillingDate.Value, documentIssueDate);
        return alertDate.DayNumber - NormalizeReferenceDate(referenceDate).DayNumber;
    }

    private static DateOnly? ResolveNextBillingDateForAlertFilter(
        LocalRentalBillingProfile profile,
        DateOnly referenceDate)
    {
        if (profile is null || !profile.IsActive)
            return null;

        NormalizeBillingSchedule(profile, referenceDate);
        return RentalBillingScheduleRules.ResolveApplicableBillingDate(
            profile.BillingDay,
            profile.BillingDayMode,
            profile.BillingCycleMonths,
            profile.BillingAnchorMonth,
            NormalizeReferenceDate(referenceDate),
            profile.LastBilledDate);
    }

    private async Task<List<LocalRentalBillingProfile>> ApplyPastDueOnlyProfilePrefilterAsync(
        List<LocalRentalBillingProfile> profiles,
        RentalBillingFilter filter,
        DateOnly referenceDate,
        CancellationToken ct)
    {
        if (!ShouldPrefilterPastDueOnlyBillingProfiles(filter) || profiles.Count == 0)
            return profiles;

        var runsByProfile = profiles.ToDictionary(
            profile => profile.Id,
            profile => DeduplicateBillingRuns(GetBillingRuns(profile)));
        var referenceMonth = new DateOnly(referenceDate.Year, referenceDate.Month, 1);
        var pastRunIds = runsByProfile.Values
            .SelectMany(runs => runs)
            .Where(run => run.RunId != Guid.Empty)
            .Where(run => IsPastBillingRun(run, referenceMonth))
            .Select(run => run.RunId)
            .Distinct()
            .ToList();
        if (pastRunIds.Count == 0)
            return new List<LocalRentalBillingProfile>();

        var (settlementByRun, invoiceByRun) = await LoadBillingRunReferencesAsync(pastRunIds, ct);
        ct.ThrowIfCancellationRequested();

        var pastDueProfileIds = profiles
            .Where(profile =>
            {
                if (!runsByProfile.TryGetValue(profile.Id, out var runs) || runs.Count == 0)
                    return false;

                var summary = BuildBillingHistorySummary(
                    profile,
                    runs,
                    settlementByRun,
                    invoiceByRun,
                    referenceDate);
                return summary.PastUnresolvedCount > 0 || summary.PastUnresolvedAmount > 0m;
            })
            .Select(profile => profile.Id)
            .ToHashSet();
        if (pastDueProfileIds.Count == 0)
            return new List<LocalRentalBillingProfile>();

        if (filter.ExpandCustomerSummaryRows)
            return profiles
                .Where(profile => pastDueProfileIds.Contains(profile.Id))
                .ToList();

        var pastDueGroupKeys = profiles
            .Where(profile => pastDueProfileIds.Contains(profile.Id))
            .Select(BuildBillingProfileGroupKey)
            .ToHashSet(StringComparer.Ordinal);
        return profiles
            .Where(profile => pastDueGroupKeys.Contains(BuildBillingProfileGroupKey(profile)))
            .ToList();
    }

    private static bool ShouldPrefilterPastDueOnlyBillingProfiles(RentalBillingFilter filter)
        => filter.PastDueOnly;

    private static int ResolveUnlinkedBillingAssetResultLimit(RentalBillingFilter filter)
    {
        if (IsUnlinkedBillingStatusFilter(filter.Status))
            return BillingUnlinkedFocusedResultLimit;

        return string.IsNullOrWhiteSpace(filter.SearchText)
            ? BillingUnlinkedDefaultResultLimit
            : AssetSearchResultLimit;
    }

    private static int? ResolveBillingProfileResultLimit(RentalBillingFilter filter)
    {
        if (filter.DueOnly || filter.PastDueOnly || IsUnlinkedBillingStatusFilter(filter.Status))
            return null;

        return string.IsNullOrWhiteSpace(filter.SearchText)
            ? BillingProfileListResultLimit
            : BillingProfileSearchResultLimit;
    }

    private static bool IsUnlinkedBillingStatusFilter(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        var normalized = status.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        return string.Equals(normalized, "미연결", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "생성필요", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "청구설정필요", StringComparison.OrdinalIgnoreCase);
    }

    private RentalBillingViewRow CreateUnlinkedBillingViewRow(
        LocalRentalAsset asset,
        IReadOnlyDictionary<Guid, RentalBillingCustomerLookup> customersById,
        IReadOnlyDictionary<string, string> offices,
        DateOnly referenceDate)
    {
        var officeCode = NormalizeOfficeCode(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode);
        if (string.IsNullOrWhiteSpace(officeCode))
            officeCode = DomainConstants.OfficeUsenet;

        var customer = asset.CustomerId.HasValue &&
                       asset.CustomerId.Value != Guid.Empty &&
                       customersById.TryGetValue(asset.CustomerId.Value, out var linkedCustomer)
            ? linkedCustomer
            : null;
        var customerDisplayName = customer is not null && !string.IsNullOrWhiteSpace(customer.NameOriginal)
            ? customer.NameOriginal.Trim()
            : ResolvePrimaryAssetCustomerName(asset).Trim();
        if (string.IsNullOrWhiteSpace(customerDisplayName))
            customerDisplayName = "(거래처 미지정)";

        var installLocation = string.IsNullOrWhiteSpace(asset.InstallLocation)
            ? asset.InstallSiteName
            : asset.InstallLocation;
        var monthlyAmount = Math.Max(0m, asset.MonthlyFee);
        var contractDates = RentalContractDateRules.Resolve(
            null,
            asset.ContractDate,
            asset.ContractStartDate,
            asset.InstallDate);
        var billingStartDate = contractDates.ContractDate
                               ?? contractDates.ContractStartDate
                               ?? referenceDate;
        var templateItems = new List<RentalBillingTemplateItemModel>
        {
            new()
            {
                DisplayItemName = string.IsNullOrWhiteSpace(asset.ItemName) ? "렌탈 임대료" : asset.ItemName.Trim(),
                BillingLineMode = "묶음",
                RepresentativeAssetId = asset.Id == Guid.Empty ? null : asset.Id,
                Quantity = 1m,
                UnitPrice = monthlyAmount,
                Amount = monthlyAmount,
                IncludedAssetIds = asset.Id == Guid.Empty ? new List<Guid>() : new List<Guid> { asset.Id }
            }
        };

        var syntheticProfile = new LocalRentalBillingProfile
        {
            Id = Guid.Empty,
            CustomerId = customer?.Id ?? asset.CustomerId,
            CustomerName = customerDisplayName,
            BusinessNumber = customer?.BusinessNumber?.Trim() ?? string.Empty,
            ItemName = string.IsNullOrWhiteSpace(asset.ItemName) ? "렌탈 임대료" : asset.ItemName.Trim(),
            BillingType = "묶음",
            InstallSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(installLocation),
            BillingAdvanceMode = "후불",
            ManagementCompanyCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            BillingMethod = string.Empty,
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            BillingDay = 25,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = 1,
            BillingAnchorMonth = billingStartDate.Month,
            DocumentIssueMode = RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate,
            MonthlyAmount = monthlyAmount,
            BillingAnchorDate = billingStartDate,
            BillingStartDate = billingStartDate,
            ContractDate = contractDates.ContractDate,
            ContractStartDate = contractDates.ContractStartDate,
            ContractEndDate = asset.RentalEndDate,
            Notes = asset.Notes ?? string.Empty,
            BillingTemplateJson = SerializeBillingTemplateItems(templateItems),
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = asset.CreatedAtUtc,
            UpdatedAtUtc = asset.UpdatedAtUtc
        };

        var dataIssues = BuildUnlinkedBillingDataIssues(asset);
        return new RentalBillingViewRow
        {
            SelectionId = asset.Id,
            HasPersistedProfile = false,
            Source = syntheticProfile,
            GroupedSourceCount = 1,
            GroupedPersistedProfileCount = 0,
            GroupedUnlinkedAssetCount = 1,
            GroupedSelectionIds = asset.Id == Guid.Empty ? new List<Guid>() : new List<Guid> { asset.Id },
            GroupedPersistedProfileIds = new List<Guid>(),
            CustomerDisplayName = customerDisplayName,
            BillingCycleDisplay = syntheticProfile.BillingCycleMonths > 0 ? $"{syntheticProfile.BillingCycleMonths}개월" : string.Empty,
            ResponsibleOfficeName = ResolveOfficeDisplayName(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, offices),
            NextBillingDate = null,
            DaysRemaining = null,
            DisplayStatus = "미연결",
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            SettledAmount = 0m,
            OutstandingAmount = 0m,
            RequiresFollowUp = true,
            LastSettledDate = null,
            AssetCount = 1,
            TemplateItemCount = templateItems.Count,
            IncludedAssetCount = CountDistinctTemplateIncludedAssets(templateItems),
            BillingType = syntheticProfile.BillingType,
            InstallSiteName = syntheticProfile.InstallSiteName,
            InstallLocationDisplay = string.IsNullOrWhiteSpace(installLocation) ? syntheticProfile.InstallSiteName : installLocation,
            BillingAdvanceMode = syntheticProfile.BillingAdvanceMode,
            BillingDayMode = syntheticProfile.BillingDayMode,
            BillingAnchorMonth = syntheticProfile.BillingAnchorMonth,
            DocumentIssueMode = syntheticProfile.DocumentIssueMode,
            DocumentLeadDays = syntheticProfile.DocumentLeadDays,
            DocumentIssueDate = null,
            AlertDate = null,
            AlertReason = "청구 프로필 생성 필요",
            CurrentBillingRunId = null,
            CurrentBillingPeriodLabel = "프로필 생성 필요",
            CurrentBillingRunStatus = PaymentFlowConstants.BillingStatusPlanned,
            CurrentBilledAmount = monthlyAmount,
            HasDataIssue = dataIssues.Count > 0,
            DataIssueSummary = dataIssues.Count == 0 ? string.Empty : string.Join(" / ", dataIssues)
        };
    }

    private List<RentalBillingViewRow> GroupBillingRowsByCustomer(IReadOnlyList<RentalBillingViewRow> rows)
    {
        if (rows.Count <= 1)
            return rows.ToList();

        var groupedRows = new List<RentalBillingViewRow>(rows.Count);
        var groupsByKey = new Dictionary<string, List<RentalBillingViewRow>>(rows.Count);
        var groupsInOrder = new List<List<RentalBillingViewRow>>();
        foreach (var row in rows)
        {
            var groupKey = BuildBillingCustomerGroupKey(row);
            if (!groupsByKey.TryGetValue(groupKey, out var groupRows))
            {
                groupRows = new List<RentalBillingViewRow>();
                groupsByKey.Add(groupKey, groupRows);
                groupsInOrder.Add(groupRows);
            }

            groupRows.Add(row);
        }

        foreach (var groupRows in groupsInOrder)
        {
            if (groupRows.Count <= 1)
            {
                groupedRows.Add(groupRows[0]);
                continue;
            }

            groupRows.Sort(CompareGroupedBillingRows);
            groupedRows.Add(CreateGroupedBillingViewRow(groupRows));
        }

        return groupedRows;
    }

    private static int CompareGroupedBillingRows(RentalBillingViewRow left, RentalBillingViewRow right)
    {
        var persistedComparison = right.HasPersistedProfile.CompareTo(left.HasPersistedProfile);
        return persistedComparison != 0
            ? persistedComparison
            : left.SelectionId.CompareTo(right.SelectionId);
    }

    private static string BuildBillingProfileGroupKey(LocalRentalBillingProfile profile)
        => BuildBillingProfileGroupKey(
            profile,
            ResolveBillingProfileCustomerDisplayName(profile, new Dictionary<Guid, string>()),
            profile.Id);

    private static string BuildBillingProfileGroupKey(
        LocalRentalBillingProfile profile,
        string? customerDisplayName,
        Guid fallbackSelectionId)
    {
        var officeCode = NormalizeOfficeCode(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode);
        if (string.IsNullOrWhiteSpace(officeCode))
            officeCode = DomainConstants.OfficeUsenet;

        string customerKey;
        if (profile.CustomerId.HasValue && profile.CustomerId.Value != Guid.Empty)
        {
            customerKey = $"ID:{profile.CustomerId.Value:D}";
        }
        else
        {
            var businessNumber = NormalizeDigitsOnly(profile.BusinessNumber);
            if (!string.IsNullOrWhiteSpace(businessNumber))
            {
                customerKey = $"BIZ:{businessNumber}";
            }
            else
            {
                var displayName = RentalCatalogValueNormalizer.NormalizeLooseKey(
                    string.IsNullOrWhiteSpace(customerDisplayName)
                        ? profile.CustomerName
                        : customerDisplayName);
                customerKey = string.IsNullOrWhiteSpace(displayName)
                    ? $"ROW:{fallbackSelectionId:D}"
                    : $"NAME:{displayName}";
            }
        }

        return $"{NormalizeProfileKeyPart(officeCode)}|{customerKey}";
    }

    private string BuildBillingCustomerGroupKey(RentalBillingViewRow row)
        => BuildBillingProfileGroupKey(row.Source, row.CustomerDisplayName, row.SelectionId);

    private RentalBillingViewRow CreateGroupedBillingViewRow(IReadOnlyList<RentalBillingViewRow> rows)
    {
        var representative = rows[0];
        var textMetrics = BuildGroupedBillingTextMetrics(rows);
        var distinctCycles = textMetrics.DistinctCycles;
        var distinctBillingTypes = textMetrics.DistinctBillingTypes;
        var distinctAdvanceModes = textMetrics.DistinctAdvanceModes;
        var distinctRunStatuses = textMetrics.DistinctRunStatuses;
        var distinctPeriodLabels = textMetrics.DistinctPeriodLabels;
        var distinctSettlementStatuses = textMetrics.DistinctSettlementStatuses;
        var distinctDisplayStatuses = textMetrics.DistinctDisplayStatuses;
        var distinctInstallLocations = textMetrics.DistinctInstallLocations;
        var identityMetrics = BuildGroupedBillingIdentityMetrics(rows);
        var groupedSelectionIds = identityMetrics.GroupedSelectionIds;
        var groupedPersistedProfileIds = identityMetrics.GroupedPersistedProfileIds;
        var groupedProfileRevisions = identityMetrics.GroupedProfileRevisions;
        var groupedPersistedProfileCount = groupedPersistedProfileIds.Count;
        var groupedMetrics = BuildGroupedBillingRowMetrics(rows);
        var groupedUnlinkedAssetCount = groupedMetrics.GroupedUnlinkedAssetCount;
        var groupedSourceCount = groupedMetrics.GroupedSourceCount;
        var installLocationDisplay = BuildGroupedInstallLocationDisplay(distinctInstallLocations);
        var aggregateSummary = BuildGroupedBillingAggregateSummary(groupedPersistedProfileCount, groupedUnlinkedAssetCount);
        var historyRows = BuildGroupedBillingHistoryRows(rows);
        var historySummary = groupedMetrics.HistorySummary;
        var dataIssues = textMetrics.DataIssues;
        if (groupedPersistedProfileCount > 1)
            AddDistinctTrimmed(dataIssues, $"청구프로필 {groupedPersistedProfileCount:N0}건 묶음 표시");
        if (groupedUnlinkedAssetCount > 0)
            AddDistinctTrimmed(dataIssues, $"청구설정 필요 장비 {groupedUnlinkedAssetCount:N0}대 포함");
        if (distinctCycles.Count > 1)
            AddDistinctTrimmed(dataIssues, "청구주기 상이");

        return new RentalBillingViewRow
        {
            SelectionId = representative.SelectionId,
            HasPersistedProfile = groupedPersistedProfileCount > 0,
            Source = representative.Source,
            GroupedSourceCount = groupedSourceCount,
            GroupedPersistedProfileCount = groupedPersistedProfileCount,
            GroupedUnlinkedAssetCount = groupedUnlinkedAssetCount,
            GroupedSelectionIds = groupedSelectionIds,
            GroupedPersistedProfileIds = groupedPersistedProfileIds,
            GroupedProfileRevisions = groupedProfileRevisions,
            AggregateSummary = aggregateSummary,
            CustomerDisplayName = representative.CustomerDisplayName,
            BillingCycleDisplay = distinctCycles.Count <= 1 ? distinctCycles.FirstOrDefault() ?? string.Empty : $"다중({distinctCycles.Count:N0})",
            ResponsibleOfficeName = representative.ResponsibleOfficeName,
            NextBillingDate = groupedMetrics.NextBillingDate,
            DaysRemaining = groupedMetrics.DaysRemaining,
            DisplayStatus = BuildGroupedBillingDisplayStatus(distinctDisplayStatuses, groupedPersistedProfileCount, groupedUnlinkedAssetCount),
            SettlementStatus = BuildGroupedSettlementStatus(distinctSettlementStatuses, groupedPersistedProfileCount, groupedUnlinkedAssetCount),
            CompletionStatus = groupedMetrics.AllRowsCompleted
                ? PaymentFlowConstants.CompletionDone
                : PaymentFlowConstants.CompletionPending,
            SettledAmount = groupedMetrics.SettledAmount,
            OutstandingAmount = groupedMetrics.OutstandingAmount,
            RequiresFollowUp = groupedMetrics.RequiresFollowUp,
            LastSettledDate = groupedMetrics.LastSettledDate,
            AssetCount = groupedMetrics.AssetCount,
            TemplateItemCount = groupedMetrics.TemplateItemCount,
            IncludedAssetCount = groupedMetrics.IncludedAssetCount,
            BillingType = distinctBillingTypes.Count <= 1 ? distinctBillingTypes.FirstOrDefault() ?? representative.BillingType : "혼합",
            InstallSiteName = representative.InstallSiteName,
            InstallLocationDisplay = installLocationDisplay,
            BillingAdvanceMode = distinctAdvanceModes.Count <= 1 ? distinctAdvanceModes.FirstOrDefault() ?? representative.BillingAdvanceMode : "복합",
            BillingDayMode = representative.BillingDayMode,
            BillingAnchorMonth = representative.BillingAnchorMonth,
            DocumentIssueMode = representative.DocumentIssueMode,
            DocumentLeadDays = representative.DocumentLeadDays,
            DocumentIssueDate = groupedMetrics.DocumentIssueDate,
            AlertDate = groupedMetrics.AlertDate,
            AlertReason = groupedPersistedProfileCount > 1
                ? "복수 청구 프로필 묶음"
                : groupedUnlinkedAssetCount > 0
                    ? "청구설정 필요 장비 포함"
                    : representative.AlertReason,
            CurrentBillingRunId = groupedPersistedProfileCount == 1 && groupedUnlinkedAssetCount == 0 ? representative.CurrentBillingRunId : null,
            CurrentBillingPeriodLabel = distinctPeriodLabels.Count <= 1
                ? distinctPeriodLabels.FirstOrDefault() ?? string.Empty
                : "복수 프로필",
            CurrentBillingRunStatus = distinctRunStatuses.Count <= 1
                ? distinctRunStatuses.FirstOrDefault() ?? representative.CurrentBillingRunStatus
                : "요약",
            CurrentBilledAmount = groupedMetrics.CurrentBilledAmount,
            BillingHistoryRows = historyRows,
            PastUnresolvedCount = historySummary.PastUnresolvedCount,
            PastUnresolvedAmount = historySummary.PastUnresolvedAmount,
            OldestPastUnresolvedScheduledDate = historySummary.OldestPastUnresolvedScheduledDate,
            OldestPastUnresolvedPeriodLabel = historySummary.OldestPastUnresolvedPeriodLabel,
            HasDataIssue = groupedMetrics.HasDataIssue || groupedSourceCount > 1,
            DataIssueSummary = dataIssues.Count == 0 ? aggregateSummary : string.Join(" / ", dataIssues)
        };
    }

    private static List<RentalBillingHistoryRow> BuildGroupedBillingHistoryRows(
        IReadOnlyList<RentalBillingViewRow> rows)
    {
        var historyRows = new List<RentalBillingHistoryRow>();
        foreach (var row in rows)
        {
            if (row.BillingHistoryRows.Count == 0)
                continue;

            historyRows.EnsureCapacity(historyRows.Count + row.BillingHistoryRows.Count);
            foreach (var history in row.BillingHistoryRows)
                historyRows.Add(history);
        }

        if (historyRows.Count <= 1 || AreGroupedBillingHistoryRowsSorted(historyRows))
            return historyRows;

        return historyRows
            .OrderByDescending(history => history.ScheduledDate)
            .ThenBy(history => history.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool AreGroupedBillingHistoryRowsSorted(IReadOnlyList<RentalBillingHistoryRow> historyRows)
    {
        for (var index = 1; index < historyRows.Count; index++)
        {
            if (CompareGroupedBillingHistoryRows(historyRows[index - 1], historyRows[index]) > 0)
                return false;
        }

        return true;
    }

    private static int CompareGroupedBillingHistoryRows(RentalBillingHistoryRow left, RentalBillingHistoryRow right)
    {
        var scheduledDateComparison = right.ScheduledDate.CompareTo(left.ScheduledDate);
        return scheduledDateComparison != 0
            ? scheduledDateComparison
            : StringComparer.CurrentCultureIgnoreCase.Compare(left.CustomerName, right.CustomerName);
    }

    private static string BuildGroupedInstallLocationDisplay(IReadOnlyList<string> locations)
    {
        if (locations.Count == 0)
            return string.Empty;
        if (locations.Count == 1)
            return locations[0];

        return $"{locations[0]} 외 {locations.Count - 1}곳";
    }

    private static string BuildGroupedBillingAggregateSummary(int groupedPersistedProfileCount, int groupedUnlinkedAssetCount)
    {
        var parts = new List<string>();
        if (groupedPersistedProfileCount > 0)
            parts.Add($"청구프로필 {groupedPersistedProfileCount:N0}건");
        if (groupedUnlinkedAssetCount > 0)
            parts.Add($"청구설정 필요 장비 {groupedUnlinkedAssetCount:N0}대");

        return parts.Count == 0 ? string.Empty : string.Join(" + ", parts);
    }

    private static IEnumerable<string> ExtractBillingDataIssueTokens(RentalBillingViewRow row)
        => string.IsNullOrWhiteSpace(row.DataIssueSummary)
            ? Array.Empty<string>()
            : row.DataIssueSummary
                .Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim());

    private static string BuildGroupedBillingDisplayStatus(
        IReadOnlyCollection<string> distinctDisplayStatuses,
        int groupedPersistedProfileCount,
        int groupedUnlinkedAssetCount)
    {
        if (groupedUnlinkedAssetCount > 0 && groupedPersistedProfileCount > 0)
            return "미연결 포함";
        if (distinctDisplayStatuses.Count == 1)
            return distinctDisplayStatuses.First();
        if (distinctDisplayStatuses.Contains("미수", StringComparer.CurrentCultureIgnoreCase))
            return "미수";
        if (distinctDisplayStatuses.Contains("청구중", StringComparer.CurrentCultureIgnoreCase))
            return "청구중";
        return "복합";
    }

    private static string BuildGroupedSettlementStatus(
        IReadOnlyCollection<string> distinctSettlementStatuses,
        int groupedPersistedProfileCount,
        int groupedUnlinkedAssetCount)
    {
        if (groupedUnlinkedAssetCount > 0 && groupedPersistedProfileCount > 0)
            return "혼합";
        if (distinctSettlementStatuses.Count == 1)
            return distinctSettlementStatuses.First();
        return "다중";
    }

    private static string NormalizeDigitsOnly(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsDigit).ToArray());

    private static string ResolveBillingProfileCustomerDisplayName(
        LocalRentalBillingProfile profile,
        IReadOnlyDictionary<Guid, string> customerNameMap)
        => ResolveBillingProfileCustomerDisplayName(
            profile.CustomerId,
            profile.CustomerName,
            profile.ProfileKey,
            customerNameMap);

    private static string ResolveBillingProfileCustomerDisplayName(
        Guid? customerId,
        string? customerNameValue,
        string? profileKey,
        IReadOnlyDictionary<Guid, string> customerNameMap)
    {
        var linkedCustomerName = string.Empty;
        if (customerId.HasValue &&
            customerId.Value != Guid.Empty &&
            customerNameMap.TryGetValue(customerId.Value, out var customerName) &&
            !string.IsNullOrWhiteSpace(customerName))
        {
            linkedCustomerName = customerName.Trim();
        }

        var profileCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(customerNameValue);
        if (!string.IsNullOrWhiteSpace(profileCustomerName))
        {
            if (!string.IsNullOrWhiteSpace(linkedCustomerName) &&
                string.Equals(
                    RentalCatalogValueNormalizer.NormalizeLooseKey(profileCustomerName),
                    RentalCatalogValueNormalizer.NormalizeLooseKey(linkedCustomerName),
                    StringComparison.OrdinalIgnoreCase))
            {
                var legacyAlias = TryResolveBillingProfileAliasFromProfileKey(profileKey, linkedCustomerName);
                if (!string.IsNullOrWhiteSpace(legacyAlias))
                    return legacyAlias;
            }

            return profileCustomerName;
        }

        if (!string.IsNullOrWhiteSpace(linkedCustomerName))
            return linkedCustomerName;

        return "(거래처 미지정)";
    }

    private static string TryResolveBillingProfileAliasFromProfileKey(string? profileKey, string linkedCustomerName)
    {
        var linkedDisplayName = RentalCatalogValueNormalizer.NormalizeDisplayText(linkedCustomerName);
        var linkedKey = RentalCatalogValueNormalizer.NormalizeLooseKey(linkedDisplayName);
        if (string.IsNullOrWhiteSpace(profileKey) || string.IsNullOrWhiteSpace(linkedDisplayName) || string.IsNullOrWhiteSpace(linkedKey))
            return string.Empty;

        foreach (var rawPart in profileKey.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.StartsWith("NAME:", StringComparison.OrdinalIgnoreCase)
                ? rawPart[5..].Trim()
                : rawPart.Trim();
            var partKey = RentalCatalogValueNormalizer.NormalizeLooseKey(part);
            if (partKey.Length <= linkedKey.Length ||
                !partKey.Contains(linkedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = string.Empty;
            if (part.StartsWith(linkedDisplayName, StringComparison.OrdinalIgnoreCase) && part.Length > linkedDisplayName.Length)
                suffix = part[linkedDisplayName.Length..].Trim();

            if (string.IsNullOrWhiteSpace(suffix))
            {
                var index = partKey.IndexOf(linkedKey, StringComparison.OrdinalIgnoreCase);
                if (index == 0 && partKey.Length > linkedKey.Length)
                    suffix = partKey[linkedKey.Length..].Trim();
            }

            return string.IsNullOrWhiteSpace(suffix)
                ? part
                : $"{linkedDisplayName}[{suffix}]";
        }

        return string.Empty;
    }

    private static string ResolveBillingProfileInstallLocationDisplay(
        LocalRentalBillingProfile profile,
        IReadOnlyList<LocalRentalAsset> profileAssets)
        => BuildBillingAssetRowSummary(profile, profileAssets).InstallLocationDisplay;

    private static RentalBillingAssetRowSummary BuildBillingAssetRowSummary(
        LocalRentalBillingProfile profile,
        IReadOnlyList<LocalRentalAsset> profileAssets)
    {
        var assetLocations = new List<string>();
        var hasMissingMonthlyFee = false;
        var hasEligibilityReviewRequired = false;
        foreach (var asset in profileAssets)
        {
            var location = string.IsNullOrWhiteSpace(asset.InstallLocation)
                ? asset.InstallSiteName
                : asset.InstallLocation;
            location = location?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(location) &&
                !assetLocations.Contains(location, StringComparer.CurrentCultureIgnoreCase))
            {
                assetLocations.Add(location);
            }

            if (!RentalAssetStatusRules.IsNonOperating(asset.AssetStatus) &&
                Math.Max(0m, asset.MonthlyFee) <= 0m)
            {
                hasMissingMonthlyFee = true;
            }

            if (string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus) ||
                string.Equals(asset.BillingEligibilityStatus, BillingEligibilityUnconfirmed, StringComparison.OrdinalIgnoreCase))
            {
                hasEligibilityReviewRequired = true;
            }
        }

        string installLocationDisplay;
        if (assetLocations.Count == 1)
            installLocationDisplay = assetLocations[0];
        else if (assetLocations.Count > 1)
            installLocationDisplay = $"{assetLocations[0]} 외 {assetLocations.Count - 1}곳";
        else
            installLocationDisplay = profile.InstallSiteName ?? string.Empty;

        return new RentalBillingAssetRowSummary(
            profileAssets.Count,
            installLocationDisplay,
            hasMissingMonthlyFee,
            hasEligibilityReviewRequired);
    }

    public async Task<IReadOnlyList<RentalAssetViewRow>> GetAssetRowsAsync(
        RentalAssetFilter filter,
        SessionState session,
        CancellationToken ct = default)
    {
        if (IsRentalSearchTextTooShort(filter.SearchText))
            return Array.Empty<RentalAssetViewRow>();

        var totalStopwatch = Stopwatch.StartNew();
        var stepStopwatch = Stopwatch.StartNew();
        await EnsureAdministrativeBusinessCachesAsync(session, ct);
        LogRentalLoadStep("Rental asset admin cache", stepStopwatch, BuildAssetFilterTimingDetail(filter));

        stepStopwatch.Restart();
        var offices = await GetOfficeMapAsync(ct);
        var referenceDate = DateOnly.FromDateTime(DateTime.Today);
        var query = ApplySharedAssetViewScope(_db.RentalAssets.AsNoTracking(), session);
        LogRentalLoadStep("Rental asset reference data", stepStopwatch, $"offices={offices.Count:N0}");

        var searchKeyword = (filter.SearchText ?? string.Empty).Trim();

        query = ApplyAssetFilter(query, new RentalAssetFilter
        {
            SearchText = string.Empty,
            ItemCategoryNames = filter.ItemCategoryNames,
            OfficeCodes = filter.OfficeCodes,
            AssetStatuses = filter.AssetStatuses
        }, session);

        stepStopwatch.Restart();
        var maxResults = ResolveAssetQueryResultLimit(filter);
        var assets = string.IsNullOrWhiteSpace(searchKeyword)
            ? await ApplyAssetListOrdering(
                    SelectAssetListProjection(query),
                    filter.PinnedAssetId)
                .Take(maxResults)
                .ToListAsync(ct)
            : await LoadAssetSearchResultAssetsAsync(
                query,
                searchKeyword,
                maxResults,
                filter.PinnedAssetId,
                ct);
        if (!string.IsNullOrWhiteSpace(searchKeyword) && assets.Count < maxResults)
        {
            stepStopwatch.Restart();
            var normalizedKeyword = RentalCatalogValueNormalizer.NormalizeLooseKey(searchKeyword);
            var linkedCustomerIds = await GetBoundedAssetSearchCustomerIdsAsync(searchKeyword, normalizedKeyword, ct);
            LogRentalLoadStep(
                "Rental asset customer search match",
                stepStopwatch,
                linkedCustomerIds.Count == 0
                    ? "linkedCustomerSearch=none"
                    : $"linkedCustomerSearch=bounded, matches={linkedCustomerIds.Count:N0}, cap={AssetSearchCustomerMatchLimit:N0}, directAssets={assets.Count:N0}/{maxResults:N0}");

            if (linkedCustomerIds.Count > 0 && assets.Count < maxResults)
            {
                stepStopwatch.Restart();
                await AddLinkedCustomerAssetSearchResultsAsync(
                    assets,
                    query,
                    linkedCustomerIds,
                    maxResults,
                    filter.PinnedAssetId,
                    ct);
                LogRentalLoadStep(
                    "Rental asset linked customer fallback",
                    stepStopwatch,
                    $"assets={assets.Count:N0}/{maxResults:N0}, linkedCustomers={linkedCustomerIds.Count:N0}");
            }
        }
        LogRentalLoadStep("Rental asset DB query", stepStopwatch, $"assets={assets.Count:N0}, {BuildAssetFilterTimingDetail(filter, maxResults)}");

        stepStopwatch.Restart();
        await NormalizeAssetCustomerDisplayNamesAsync(assets, ct);
        LogRentalLoadStep("Rental asset customer display normalize", stepStopwatch, $"assets={assets.Count:N0}");

        stepStopwatch.Restart();
        var result = assets
            .Select(asset => CreateAssetViewRow(asset, offices, referenceDate, hasFullDetail: false))
            .OrderBy(row => row.CurrentCustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Source.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        LogRentalLoadStep("Rental asset row build/sort", stepStopwatch, $"rows={result.Count:N0}");
        OperationTiming.LogIfSlow(
            "DATA",
            "Rental asset total load",
            totalStopwatch.Elapsed,
            $"rows={result.Count:N0}, {BuildAssetFilterTimingDetail(filter)}",
            infoThreshold: TimeSpan.FromMilliseconds(600),
            warningThreshold: TimeSpan.FromSeconds(2));
        return result;
    }

    public async Task<RentalAssetViewRow?> GetAssetRowAsync(
        Guid assetId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (assetId == Guid.Empty)
            return null;

        await EnsureAdministrativeBusinessCachesAsync(session, ct);

        var offices = await GetOfficeMapAsync(ct);
        var referenceDate = DateOnly.FromDateTime(DateTime.Today);
        var asset = await ApplySharedAssetViewScope(_db.RentalAssets.AsNoTracking(), session)
            .FirstOrDefaultAsync(current => current.Id == assetId, ct);
        if (asset is null)
            return null;

        await NormalizeAssetCustomerDisplayNamesAsync([asset], ct);
        return CreateAssetViewRow(asset, offices, referenceDate);
    }

    private static IQueryable<LocalRentalAsset> SelectAssetListProjection(IQueryable<LocalRentalAsset> query)
        => query.Select(asset => new LocalRentalAsset
        {
            Id = asset.Id,
            IsDeleted = asset.IsDeleted,
            CreatedAtUtc = asset.CreatedAtUtc,
            UpdatedAtUtc = asset.UpdatedAtUtc,
            Revision = asset.Revision,
            IsDirty = asset.IsDirty,
            TenantCode = asset.TenantCode,
            OfficeCode = asset.OfficeCode,
            AssetKey = asset.AssetKey,
            CustomerId = asset.CustomerId,
            ItemId = asset.ItemId,
            BillingProfileId = asset.BillingProfileId,
            ManagementId = asset.ManagementId,
            ManagementNumber = asset.ManagementNumber,
            ManagementCompanyCode = asset.ManagementCompanyCode,
            CurrentCustomerName = asset.CurrentCustomerName,
            InstallSiteName = asset.InstallSiteName,
            BillingEligibilityStatus = asset.BillingEligibilityStatus,
            ItemCategoryName = asset.ItemCategoryName,
            ItemName = asset.ItemName,
            MachineNumber = asset.MachineNumber,
            CustomerName = asset.CustomerName,
            InstallLocation = asset.InstallLocation,
            MonthlyFee = asset.MonthlyFee,
            RentalEndDate = asset.RentalEndDate,
            ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            AssetStatus = asset.AssetStatus
        });

    private async Task<List<LocalRentalAsset>> LoadAssetSearchResultAssetsAsync(
        IQueryable<LocalRentalAsset> baseQuery,
        string keyword,
        int maxResults,
        Guid? pinnedAssetId,
        CancellationToken ct)
    {
        var assets = new List<LocalRentalAsset>(maxResults);
        if (pinnedAssetId.HasValue && pinnedAssetId.Value != Guid.Empty)
        {
            var pinnedId = pinnedAssetId.Value;
            var pinnedAsset = await SelectAssetListProjection(ApplyAssetSearchContainsFilter(
                    baseQuery.Where(asset => asset.Id == pinnedId),
                    keyword))
                .FirstOrDefaultAsync(ct);
            if (pinnedAsset is not null)
                assets.Add(pinnedAsset);
        }

        if (assets.Count < maxResults)
        {
            var prefixQuery = ApplyAssetSearchPrefixFilter(baseQuery, keyword);
            await AddDistinctAssetSearchResultsAsync(
                assets,
                prefixQuery,
                maxResults,
                orderByListColumns: true,
                ct);
        }

        if (assets.Count < maxResults)
        {
            var containsQuery = ApplyAssetSearchContainsFilter(baseQuery, keyword);
            await AddDistinctAssetSearchResultsAsync(
                assets,
                containsQuery,
                maxResults,
                orderByListColumns: false,
                ct);
        }

        return assets;
    }

    private async Task AddLinkedCustomerAssetSearchResultsAsync(
        List<LocalRentalAsset> assets,
        IQueryable<LocalRentalAsset> baseQuery,
        IReadOnlyCollection<Guid> linkedCustomerIds,
        int maxResults,
        Guid? pinnedAssetId,
        CancellationToken ct)
    {
        if (linkedCustomerIds.Count == 0 || assets.Count >= maxResults)
            return;

        if (pinnedAssetId.HasValue &&
            pinnedAssetId.Value != Guid.Empty &&
            assets.All(asset => asset.Id != pinnedAssetId.Value))
        {
            var pinnedId = pinnedAssetId.Value;
            var pinnedAsset = await SelectAssetListProjection(ApplyAssetLinkedCustomerSearchFilter(
                    baseQuery.Where(asset => asset.Id == pinnedId),
                    linkedCustomerIds))
                .FirstOrDefaultAsync(ct);
            if (pinnedAsset is not null)
                assets.Add(pinnedAsset);
        }

        if (assets.Count >= maxResults)
            return;

        await AddDistinctAssetSearchResultsAsync(
            assets,
            ApplyAssetLinkedCustomerSearchFilter(baseQuery, linkedCustomerIds),
            maxResults,
            orderByListColumns: true,
            ct);
    }

    private static async Task AddDistinctAssetSearchResultsAsync(
        List<LocalRentalAsset> assets,
        IQueryable<LocalRentalAsset> query,
        int maxResults,
        bool orderByListColumns,
        CancellationToken ct)
    {
        var remaining = maxResults - assets.Count;
        if (remaining <= 0)
            return;

        var existingIds = assets.Select(asset => asset.Id).ToList();
        if (existingIds.Count > 0)
            query = query.Where(asset => !existingIds.Contains(asset.Id));

        IQueryable<LocalRentalAsset> projectedQuery = SelectAssetListProjection(query);
        if (orderByListColumns)
            projectedQuery = ApplyAssetListOrdering(projectedQuery, pinnedAssetId: null);

        var nextAssets = await projectedQuery
            .Take(remaining)
            .ToListAsync(ct);
        assets.AddRange(nextAssets);
    }

    private static IQueryable<LocalRentalAsset> ApplyAssetSearchPrefixFilter(
        IQueryable<LocalRentalAsset> query,
        string keyword)
        => query.Where(asset =>
            asset.ManagementNumber.StartsWith(keyword) ||
            asset.CustomerName.StartsWith(keyword) ||
            asset.CurrentCustomerName.StartsWith(keyword) ||
            asset.ItemCategoryName.StartsWith(keyword) ||
            asset.ItemName.StartsWith(keyword) ||
            asset.MachineNumber.StartsWith(keyword) ||
            asset.InstallLocation.StartsWith(keyword));

    private static IQueryable<LocalRentalAsset> ApplyAssetSearchContainsFilter(
        IQueryable<LocalRentalAsset> query,
        string keyword)
        => query.Where(asset =>
            asset.ManagementNumber.Contains(keyword) ||
            asset.CustomerName.Contains(keyword) ||
            asset.CurrentCustomerName.Contains(keyword) ||
            asset.ItemCategoryName.Contains(keyword) ||
            asset.ItemName.Contains(keyword) ||
            asset.MachineNumber.Contains(keyword) ||
            asset.InstallLocation.Contains(keyword));

    private static IQueryable<LocalRentalAsset> ApplyAssetLinkedCustomerSearchFilter(
        IQueryable<LocalRentalAsset> query,
        IReadOnlyCollection<Guid> linkedCustomerIds)
        => linkedCustomerIds.Count == 0
            ? query.Where(_ => false)
            : query.Where(asset => asset.CustomerId.HasValue && linkedCustomerIds.Contains(asset.CustomerId.Value));

    private static IOrderedQueryable<LocalRentalAsset> ApplyAssetListOrdering(
        IQueryable<LocalRentalAsset> query,
        Guid? pinnedAssetId)
    {
        if (pinnedAssetId.HasValue && pinnedAssetId.Value != Guid.Empty)
        {
            var pinnedId = pinnedAssetId.Value;
            return query
                .OrderByDescending(asset => asset.Id == pinnedId)
                .ThenBy(asset => asset.CustomerName)
                .ThenBy(asset => asset.ManagementNumber);
        }

        return query
            .OrderBy(asset => asset.CustomerName)
            .ThenBy(asset => asset.ManagementNumber);
    }

    private RentalAssetViewRow CreateAssetViewRow(
        LocalRentalAsset asset,
        IReadOnlyDictionary<string, string> offices,
        DateOnly referenceDate,
        bool hasFullDetail = true)
    {
        var issues = BuildAssetDataIssues(asset);
        return new RentalAssetViewRow
        {
            Source = asset,
            HasFullDetail = hasFullDetail,
            ResponsibleOfficeName = ResolveOfficeDisplayName(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, offices),
            DaysRemaining = asset.RentalEndDate.HasValue
                ? asset.RentalEndDate.Value.DayNumber - referenceDate.DayNumber
                : null,
            CurrentCustomerName = ResolvePrimaryAssetCustomerName(asset),
            InstallLocationDisplay = string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation,
            BillingEligibilityStatus = string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus) ? GetDefaultBillingEligibilityStatus(asset) : asset.BillingEligibilityStatus,
            HasDataIssue = issues.Count > 0
        };
    }

    private static void LogRentalLoadStep(string operation, Stopwatch stopwatch, string? detail = null)
    {
        stopwatch.Stop();
        OperationTiming.LogIfSlow(
            "DATA",
            operation,
            stopwatch.Elapsed,
            detail,
            infoThreshold: TimeSpan.FromMilliseconds(300),
            warningThreshold: TimeSpan.FromSeconds(2));
    }

    private static string BuildBillingFilterTimingDetail(RentalBillingFilter filter)
    {
        var office = string.IsNullOrWhiteSpace(filter.OfficeCode) ? "all" : filter.OfficeCode.Trim();
        var status = string.IsNullOrWhiteSpace(filter.Status) ? "all" : filter.Status.Trim();
        return $"office={office}, status={status}, dueOnly={filter.DueOnly}, pastDueOnly={filter.PastDueOnly}, expand={filter.ExpandCustomerSummaryRows}, history={filter.IncludeHistoryRows}, search={HasSearchText(filter.SearchText)}";
    }

    private static int ResolveAssetQueryResultLimit(RentalAssetFilter filter)
    {
        var defaultLimit = string.IsNullOrWhiteSpace(filter.SearchText)
            ? AssetListResultLimit
            : AssetSearchResultLimit;
        var requestedMaxResults = filter.MaxResults <= 0 ? defaultLimit : filter.MaxResults;
        var cap = string.IsNullOrWhiteSpace(filter.SearchText)
            ? AssetListResultLimit
            : AssetSearchResultLimit;
        return Math.Clamp(requestedMaxResults, 100, cap);
    }

    private static string BuildAssetFilterTimingDetail(RentalAssetFilter filter, int? effectiveMaxResults = null)
        => $"officeFilters={CountFilterValues(filter.OfficeCodes)}, categoryFilters={CountFilterValues(filter.ItemCategoryNames)}, statusFilters={CountFilterValues(filter.AssetStatuses)}, search={HasSearchText(filter.SearchText)}, max={effectiveMaxResults ?? filter.MaxResults}, requestedMax={filter.MaxResults}, pinned={(filter.PinnedAssetId.HasValue && filter.PinnedAssetId.Value != Guid.Empty ? "Y" : "N")}";

    private static int CountFilterValues(IEnumerable<string>? values)
        => (values ?? Array.Empty<string>())
            .Count(value => !string.IsNullOrWhiteSpace(value));

    private static string HasSearchText(string? searchText)
        => string.IsNullOrWhiteSpace(searchText) ? "N" : "Y";

    private static bool IsRentalSearchTextTooShort(string? searchText)
    {
        var keyword = (searchText ?? string.Empty).Trim();
        return keyword.Length > 0 && keyword.Length < 2;
    }

    public async Task<IReadOnlyList<LocalRentalAsset>> GetAssetsForEquipmentDetailAsync(
        LocalRentalAsset anchorAsset,
        SessionState session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(anchorAsset);

        var query = ApplySharedAssetViewScope(_db.RentalAssets.AsNoTracking(), session);
        var officeCode = NormalizeOfficeCode(anchorAsset.ResponsibleOfficeCode, anchorAsset.ManagementCompanyCode);
        if (!string.IsNullOrWhiteSpace(officeCode))
        {
            query = query.Where(asset =>
                asset.ResponsibleOfficeCode == officeCode ||
                asset.ManagementCompanyCode == officeCode);
        }

        if (anchorAsset.BillingProfileId.HasValue)
        {
            var billingProfileId = anchorAsset.BillingProfileId.Value;
            query = query.Where(asset => asset.BillingProfileId == billingProfileId);
        }
        else if (anchorAsset.CustomerId.HasValue)
        {
            var customerId = anchorAsset.CustomerId.Value;
            query = query.Where(asset => asset.CustomerId == customerId);
        }
        else
        {
            var customerName = (anchorAsset.CustomerName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(customerName))
                return [];

            query = query.Where(asset => asset.CustomerName == customerName);
        }

        var anchorAssetId = anchorAsset.Id;
        return await query
            .OrderByDescending(asset => asset.Id == anchorAssetId)
            .ThenBy(asset => asset.ManagementNumber)
            .ThenBy(asset => asset.ItemName)
            .Take(EquipmentDetailAssetLimit)
            .ToListAsync(ct);
    }

    public async Task<LocalMutationResult> SaveManagementCompanyAsync(
        LocalRentalManagementCompany company,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanEditRentalSettings(session))
            return LocalMutationResult.Denied("권한이 없어 렌탈 기준설정을 저장할 수 없습니다.");

        var code = NormalizeOfficeCode(company.Code, string.Empty);
        var name = (company.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
            return LocalMutationResult.Denied("관리업체 코드를 입력하세요.");
        if (string.IsNullOrWhiteSpace(name))
            return LocalMutationResult.Denied("관리업체 이름을 입력하세요.");

        var duplicate = await _db.RentalManagementCompanies.IgnoreQueryFilters()
            .AnyAsync(current => current.Id != company.Id && current.Code == code, ct);
        if (duplicate)
            return LocalMutationResult.Denied("같은 관리업체 코드가 이미 있습니다.");

        var now = DateTime.UtcNow;
        var existing = await _db.RentalManagementCompanies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == company.Id, ct);
        if (existing is null)
        {
            company.Id = company.Id == Guid.Empty ? Guid.NewGuid() : company.Id;
            company.Code = code;
            company.Name = name;
            company.IsActive = true;
            company.IsDeleted = false;
            company.IsDirty = true;
            company.CreatedAtUtc = now;
            company.UpdatedAtUtc = now;
            _db.RentalManagementCompanies.Add(company);
        }
        else
        {
            existing.Code = code;
            existing.Name = name;
            existing.IsActive = company.IsActive;
            existing.IsSystemDefault = existing.IsSystemDefault || company.IsSystemDefault;
            existing.IsDeleted = false;
            existing.IsDirty = true;
            existing.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(company.Id, "관리업체를 저장했습니다.");
    }

    public async Task<LocalMutationResult> DeleteManagementCompanyAsync(
        Guid companyId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanEditRentalSettings(session))
            return LocalMutationResult.Denied("권한이 없어 렌탈 기준설정을 수정할 수 없습니다.");

        var company = await _db.RentalManagementCompanies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == companyId, ct);
        if (company is null)
            return LocalMutationResult.Missing("관리업체를 찾을 수 없습니다.");
        if (company.IsSystemDefault)
            return LocalMutationResult.Denied("기본 관리업체는 삭제할 수 없습니다.");

        var inUse = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .AnyAsync(profile => profile.ManagementCompanyCode == company.Code, ct)
            || await _db.RentalAssets.IgnoreQueryFilters()
                .AnyAsync(asset => asset.ManagementCompanyCode == company.Code, ct);
        if (inUse)
            return LocalMutationResult.Denied("사용 중인 관리업체는 삭제할 수 없습니다.");

        company.IsDeleted = true;
        company.IsDirty = true;
        company.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(company.Id, "관리업체를 삭제했습니다.");
    }

    public async Task<LocalMutationResult> SaveBillingProfileAsync(
        LocalRentalBillingProfile profile,
        SessionState session,
        IReadOnlyList<RentalBillingAssetLinkEdit>? assetLinkEdits = null,
        CancellationToken ct = default)
    {
        if (profile is null)
            throw new ArgumentNullException(nameof(profile));

        var existing = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profile.Id, ct);
        if (existing is not null && !CanEditRental(
                RentalScopeNormalizer.ResolveResponsibleOfficeCode(
                    existing.TenantCode,
                    existing.OfficeCode,
                    existing.ManagementCompanyCode,
                    existing.ResponsibleOfficeCode,
                    session.OfficeCode),
                session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구 데이터를 수정할 수 없습니다.");

        var customerName = (profile.CustomerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(customerName))
            return LocalMutationResult.Denied("거래처명을 입력하세요.");

        var officeCode = await ResolveRentalOfficeCodeAsync(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode, session.OfficeCode, ct);
        if (string.IsNullOrWhiteSpace(officeCode))
            return LocalMutationResult.Denied("담당지점을 선택하세요.");

        officeCode = RentalScopeNormalizer.ResolveResponsibleOfficeCode(
            profile.TenantCode,
            profile.OfficeCode,
            profile.ManagementCompanyCode,
            officeCode,
            session.OfficeCode);

        var profileScope = ResolveRentalOwnerScopeForResponsibleOffice(profile.OfficeCode, officeCode, session);
        profile.OfficeCode = profileScope.OwnerOfficeCode;
        profile.TenantCode = profileScope.TenantCode;
        profile.CustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(customerName);
        profile.InstallSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(profile.InstallSiteName);
        if (profile.CustomerId is null || profile.CustomerId == Guid.Empty)
            profile.CustomerId = await ResolveCustomerIdAsync(
                profile.CustomerName,
                profile.BusinessNumber,
                ct,
                preferredTenantCode: profile.TenantCode);

        var linkedCustomer = await GetRentalLinkedCustomerAsync(profile.CustomerId, ct);
        var linkedCustomerTenantMismatch = linkedCustomer is not null &&
            !MatchesPreferredCustomerTenant(
                linkedCustomer,
                profile.TenantCode,
                customer => customer.TenantCode);
        if (linkedCustomer is not null &&
            (linkedCustomerTenantMismatch ||
             (!string.IsNullOrWhiteSpace(profile.CustomerName) &&
              !CustomerMatchesAnyCandidateName(
                  linkedCustomer,
                  BuildWorkbookCustomerNameCandidates(profile.CustomerName).ToList(),
                  customer => customer.NameOriginal,
                  customer => customer.NameMatchKey))))
        {
            var correctedCustomerId = await ResolveCustomerIdAsync(
                profile.CustomerName,
                profile.BusinessNumber,
                ct,
                allowWorkbookNameVariants: true,
                preferredOfficeCode: officeCode,
                preferredTenantCode: profile.TenantCode);
            if (correctedCustomerId.HasValue &&
                correctedCustomerId.Value != Guid.Empty &&
                correctedCustomerId.Value != profile.CustomerId)
            {
                profile.CustomerId = correctedCustomerId.Value;
                linkedCustomer = await GetRentalLinkedCustomerAsync(profile.CustomerId, ct);
            }

            linkedCustomerTenantMismatch = linkedCustomer is not null &&
                !MatchesPreferredCustomerTenant(
                    linkedCustomer,
                    profile.TenantCode,
                    customer => customer.TenantCode);
            if (linkedCustomer is not null &&
                (linkedCustomerTenantMismatch ||
                 !CustomerMatchesAnyCandidateName(
                     linkedCustomer,
                     BuildWorkbookCustomerNameCandidates(profile.CustomerName).ToList(),
                     customer => customer.NameOriginal,
                     customer => customer.NameMatchKey)))
            {
                return LocalMutationResult.Denied(
                    $"청구 프로필 거래처명 '{profile.CustomerName}'과 연결된 거래처 '{linkedCustomer.NameOriginal}'의 범위가 다릅니다. 부서별 거래처를 먼저 등록하거나 정확한 거래처를 선택하세요.");
            }
        }

        if (linkedCustomer is not null)
        {
            officeCode = NormalizeOfficeCode(linkedCustomer.ResponsibleOfficeCode, session.OfficeCode);
            officeCode = RentalScopeNormalizer.ResolveResponsibleOfficeCode(
                profile.TenantCode,
                profile.OfficeCode,
                profile.ManagementCompanyCode,
                officeCode,
                session.OfficeCode);
            profileScope = ResolveRentalOwnerScopeForResponsibleOffice(profile.OfficeCode, officeCode, session);
            profile.OfficeCode = profileScope.OwnerOfficeCode;
            profile.TenantCode = profileScope.TenantCode;
            var normalizedCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(linkedCustomer.NameOriginal);
            if (string.IsNullOrWhiteSpace(profile.CustomerName))
                profile.CustomerName = normalizedCustomerName;
            if (string.IsNullOrWhiteSpace(profile.BusinessNumber))
                profile.BusinessNumber = linkedCustomer.BusinessNumber?.Trim() ?? string.Empty;
        }

        profile.BillingType = NormalizeBillingType(profile.BillingType);
        profile.BillingAdvanceMode = NormalizeBillingAdvanceMode(profile.BillingAdvanceMode);
        NormalizeBillingSchedule(profile, DateOnly.FromDateTime(DateTime.Today));
        profile.BillingMethod = NormalizeBillingMethod(profile.BillingMethod);
        profile.BillingStatus = PaymentFlowConstants.NormalizeBillingStatus(profile.BillingStatus);
        profile.SettlementStatus = PaymentFlowConstants.NormalizeSettlementStatus(profile.SettlementStatus);
        profile.CompletionStatus = PaymentFlowConstants.NormalizeCompletionStatus(profile.CompletionStatus);
        profile.SettledAmount = Math.Max(0m, profile.SettledAmount);
        profile.OutstandingAmount = Math.Max(0m, profile.OutstandingAmount);
        profile.RequiresFollowUp = profile.RequiresFollowUp || profile.OutstandingAmount > 0m;
        profile.ResponsibleOfficeCode = officeCode;
        profile.ManagementCompanyCode = profileScope.OwnerOfficeCode;
        if (!CanEditRental(profile.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구 데이터를 저장할 수 없습니다.");

        var templateItems = GetBillingTemplateItems(profile, Array.Empty<LocalRentalAsset>());
        profile.BillingType = ResolveProfileBillingTypeFromTemplateItems(templateItems, profile.BillingType);
        // 렌탈 자산은 설치/소유 기준으로 전 업체가 공유 조회될 수 있고,
        // 청구 프로필은 외부 자산을 "원본 자산 변경 없는 참조"로 포함할 수 있다.
        // 저장 시 범위 검사용 자산 일괄 조회를 하지 않아 대량 장비 포함 저장 지연을 막는다.

        profile.BillingTemplateJson = SerializeBillingTemplateItems(templateItems);
        profile.MonthlyAmount = templateItems.Count == 0
            ? Math.Max(0m, profile.MonthlyAmount)
            : templateItems.Sum(item => ResolveTemplateMonthlyAmount(item));
        profile.ItemName = BuildProfileItemName(profile, templateItems);
        profile.ProfileKey = BuildProfileKey(
            profile.ManagementCompanyCode,
            profile.CustomerId,
            profile.BusinessNumber,
            profile.CustomerName,
            profile.BillingType,
            profile.BillingAdvanceMode,
            profile.BillingDay,
            profile.BillingCycleMonths,
            profile.BillingMethod);
        var legacyProfileKey = BuildLegacyProfileKey(
            profile.ManagementCompanyCode,
            profile.CustomerId,
            profile.BusinessNumber,
            profile.CustomerName,
            profile.BillingType,
            profile.BillingAdvanceMode,
            profile.BillingDay,
            profile.BillingCycleMonths,
            profile.BillingMethod);
        var canUseLegacyProfileKeyLookup =
            !string.Equals(profile.ProfileKey, legacyProfileKey, StringComparison.Ordinal) &&
            !IsDistinctBillingCustomerAlias(profile.CustomerName, linkedCustomer?.NameOriginal);
        profile.IsActive = true;
        profile.IsDeleted = false;

        var duplicate = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current =>
                current.Id != profile.Id &&
                (current.ProfileKey == profile.ProfileKey ||
                 (canUseLegacyProfileKeyLookup && current.ProfileKey == legacyProfileKey)), ct);
        if (existing is null && duplicate is not null)
        {
            existing = duplicate;
            profile.Id = duplicate.Id;
        }
        else if (existing is not null && duplicate is not null && duplicate.Id != existing.Id)
            return LocalMutationResult.Denied("같은 청구 프로필이 이미 존재합니다.");

        var now = DateTime.UtcNow;
        await LocalEntityConcurrencyGuard.TryRebaseCandidateRevisionFromAcknowledgedLocalMutationAsync(_db, profile, existing, ct);
        if (!LocalEntityConcurrencyGuard.TryPrepareForSave(profile, existing, "렌탈 청구", now, out var conflictMessage))
            return LocalMutationResult.Conflict(conflictMessage);

        if (existing is null)
        {
            var deterministicProfileId = SyncIdentityGenerator.CreateRentalBillingProfileId(profile.ProfileKey);
            profile.Id = profile.Id == Guid.Empty
                ? (deterministicProfileId == Guid.Empty ? Guid.NewGuid() : deterministicProfileId)
                : profile.Id;
            _db.RentalBillingProfiles.Add(profile);
        }
        else
        {
            _db.Entry(existing).CurrentValues.SetValues(profile);
        }

        await _db.SaveChangesAsync(ct);
        await SyncBillingProfileAssetsAsync(profile, templateItems, assetLinkEdits, ct);
        return LocalMutationResult.Ok(profile.Id, "렌탈 청구 프로필을 저장했습니다.");
    }

    public async Task<long> GetBillingProfileRevisionAsync(Guid profileId, CancellationToken ct = default)
    {
        if (profileId == Guid.Empty)
            return 0;

        return await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .Where(profile => profile.Id == profileId)
            .Select(profile => profile.Revision)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<LocalMutationResult> DeleteBillingProfileAsync(
        Guid profileId,
        SessionState session,
        long? expectedRevision = null,
        CancellationToken ct = default)
    {
        var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");
        if (!CanEditRental(
                string.IsNullOrWhiteSpace(profile.ResponsibleOfficeCode)
                    ? profile.ManagementCompanyCode
                    : profile.ResponsibleOfficeCode,
                session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구 데이터를 삭제할 수 없습니다.");

        if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(profile, expectedRevision, "렌탈 청구", out var conflictMessage))
            return LocalMutationResult.Conflict(conflictMessage);

        var now = DateTime.UtcNow;
        var linkedAssets = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId == profileId)
            .ToListAsync(ct);
        foreach (var asset in linkedAssets)
        {
            var previousBillingProfileId = asset.BillingProfileId;
            await ApplyAssignmentClearedSnapshotAsync(asset, previousBillingProfileId, now, ct);
            asset.BillingEligibilityStatus = BillingEligibilityExcluded;
            asset.BillingExclusionReason = BillingProfileDeleteExclusionReason;
            asset.IsDirty = true;
            asset.UpdatedAtUtc = now;
        }

        profile.IsDeleted = true;
        profile.IsDirty = true;
        profile.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        await RefreshLocalRentalAssetAssignmentHistoriesAsync(linkedAssets.Select(asset => asset.Id), now, "청구 프로필 삭제", ct);
        return LocalMutationResult.Ok(profileId, "렌탈 청구 프로필을 삭제했습니다.");
    }

    public async Task<LocalMutationResult> ExcludeUnlinkedBillingAssetFromBillingListAsync(
        Guid assetId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (assetId == Guid.Empty)
            return LocalMutationResult.Missing("청구 목록에서 제외할 렌탈 자산을 찾을 수 없습니다.");

        var asset = await _db.RentalAssets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == assetId, ct);
        if (asset is null || asset.IsDeleted)
            return LocalMutationResult.Missing("청구 목록에서 제외할 렌탈 자산을 찾을 수 없습니다.");

        var officeCode = RentalScopeNormalizer.ResolveResponsibleOfficeCode(
            asset.TenantCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode,
            asset.ResponsibleOfficeCode,
            session.OfficeCode);
        if (!CanEditAssetScope(officeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산을 청구 목록에서 제외할 수 없습니다.");

        if (asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty)
            return LocalMutationResult.Denied("이미 청구 프로필에 연결된 장비입니다. 청구 프로필 삭제 또는 내부 포함 장비 삭제로 정리하세요.");

        asset.BillingEligibilityStatus = BillingEligibilityExcluded;
        asset.BillingExclusionReason = BillingListCleanupExclusionReason;
        asset.IsDirty = true;
        asset.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(assetId, "청구설정 필요 장비를 청구 목록에서 제외했습니다. 자산 정보는 삭제되지 않습니다.");
    }

    public async Task<LocalMutationResult> StartBillingAsync(
        Guid billingProfileId,
        DateOnly referenceDate,
        SessionState session,
        CancellationToken ct = default,
        long? expectedRevision = null)
    {
        var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");
        if (!CanEditRental(
                string.IsNullOrWhiteSpace(profile.ResponsibleOfficeCode)
                    ? profile.ManagementCompanyCode
                    : profile.ResponsibleOfficeCode,
                session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구를 시작할 수 없습니다.");
        if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(profile, expectedRevision, "렌탈 청구", out var conflictMessage))
            return LocalMutationResult.Conflict(conflictMessage);
        if (_local is null)
            return LocalMutationResult.Denied("렌탈 청구 전표 저장 서비스를 사용할 수 없습니다.");
        NormalizeBillingSchedule(profile, referenceDate);
        // 실제 작업일이 청구 예정 월/일과 달라도 사용자가 선택한 청구월 전표를 만들 수 있어야 합니다.
        // GetOrCreateBillingRun이 referenceDate와 LastBilledDate 기준으로 이번/다음 미청구월 예정일을 계산합니다.
        var currentRun = GetOrCreateBillingRun(profile, referenceDate, persistChanges: true);
        if (currentRun is null)
            return LocalMutationResult.Denied("선택한 청구월 정보를 만들 수 없습니다.");

        var templateItems = GetBillingTemplateItems(profile);
        var linkedInvoice = await GetActiveBillingInvoiceAsync(currentRun.RunId, ct);
        var reusedExistingInvoice = linkedInvoice is not null;
        Guid invoiceId;
        decimal billedAmount;
        if (linkedInvoice is not null)
        {
            var lineBuildResult = await BuildRentalBillingInvoiceLinesAsync(profile, currentRun, templateItems, session, ct);
            if (!lineBuildResult.Success)
                return LocalMutationResult.Denied(lineBuildResult.Message);

            if (ShouldRebuildRentalBillingInvoiceLines(linkedInvoice, lineBuildResult.Lines))
            {
                if (HasRentalInvoiceSettlement(linkedInvoice))
                {
                    return LocalMutationResult.Denied("이미 수금 또는 세금계산서 처리된 렌탈 청구 전표는 자동으로 다시 만들 수 없습니다. 기존 전표를 확인한 뒤 필요한 경우 새 청구로 처리하세요.");
                }

                linkedInvoice.Lines = lineBuildResult.Lines;
                var rebuiltInvoiceResult = await SaveRentalBillingInvoiceAsync(linkedInvoice, session, ct);
                if (!rebuiltInvoiceResult.Success || rebuiltInvoiceResult.Invoice is null)
                    return LocalMutationResult.Denied(rebuiltInvoiceResult.Message);
                var rebuiltInvoice = rebuiltInvoiceResult.Invoice;
                invoiceId = rebuiltInvoice.Id;
                billedAmount = rebuiltInvoice.TotalAmount;
                currentRun.Items = CloneTemplateItemsForRun(templateItems, Math.Max(1, currentRun.CycleMonths));
                currentRun.BilledAmount = billedAmount;
            }
            else if (NormalizeRentalBillingInvoiceLineItemNames(linkedInvoice, currentRun))
            {
                var normalizedInvoiceResult = await SaveRentalBillingInvoiceAsync(linkedInvoice, session, ct);
                if (!normalizedInvoiceResult.Success || normalizedInvoiceResult.Invoice is null)
                    return LocalMutationResult.Denied(normalizedInvoiceResult.Message);
                var normalizedInvoice = normalizedInvoiceResult.Invoice;
                invoiceId = normalizedInvoice.Id;
                billedAmount = normalizedInvoice.TotalAmount;
            }
            else
            {
                invoiceId = linkedInvoice.Id;
                billedAmount = linkedInvoice.TotalAmount;
            }
        }
        else
        {
            var customerId = profile.CustomerId;
            if (!customerId.HasValue || customerId.Value == Guid.Empty)
                customerId = await ResolveCustomerIdAsync(
                    profile.CustomerName,
                    profile.BusinessNumber,
                    ct,
                    preferredTenantCode: profile.TenantCode);
            if (!customerId.HasValue || customerId.Value == Guid.Empty)
                return LocalMutationResult.Denied("렌탈 청구 전표를 만들 거래처를 찾을 수 없습니다.");

            profile.CustomerId = customerId;

            templateItems = currentRun.Items.Count > 0
                ? currentRun.Items
                : GetBillingTemplateItems(profile);
            var lineBuildResult = await BuildRentalBillingInvoiceLinesAsync(profile, currentRun, templateItems, session, ct);
            if (!lineBuildResult.Success)
                return LocalMutationResult.Denied(lineBuildResult.Message);

            var serializedTemplateItems = SerializeBillingTemplateItems(templateItems);
            if (!string.Equals(serializedTemplateItems, profile.BillingTemplateJson ?? string.Empty, StringComparison.Ordinal))
            {
                profile.BillingTemplateJson = serializedTemplateItems;
                profile.MonthlyAmount = templateItems.Sum(ResolveTemplateMonthlyAmount);
                profile.ItemName = BuildProfileItemName(profile, templateItems);
                await SyncBillingProfileAssetsAsync(profile, templateItems, null, ct);
            }

            var officeCode = NormalizeOfficeCode(profile.ResponsibleOfficeCode, DomainConstants.OfficeUsenet);
            var invoice = new LocalInvoice
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId.Value,
                VoucherType = VoucherType.Sales,
                InvoiceDate = currentRun.ScheduledDate,
                Memo = string.Empty,
                ResponsibleOfficeCode = officeCode,
                SourceWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(null, officeCode, officeCode),
                TaxInvoiceIssued = false,
                LinkedRentalBillingProfileId = profile.Id,
                LinkedRentalBillingRunId = currentRun.RunId,
                Lines = lineBuildResult.Lines
            };

            var savedInvoiceResult = await SaveRentalBillingInvoiceAsync(invoice, session, ct);
            if (!savedInvoiceResult.Success || savedInvoiceResult.Invoice is null)
                return LocalMutationResult.Denied(savedInvoiceResult.Message);
            var savedInvoice = savedInvoiceResult.Invoice;
            invoiceId = savedInvoice.Id;
            billedAmount = savedInvoice.TotalAmount;
        }

        // LocalStateService.SaveInvoiceAsync rebuilds inventory snapshots and clears the EF change tracker.
        // Reload the billing profile before writing the post-invoice status/run fields; otherwise the first
        // generated invoice is saved but the rental billing row can remain "예정", causing repeated versions
        // or false concurrent-state warnings on the next click.
        profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");

        profile.BillingStatus = PaymentFlowConstants.BillingStatusInProgress;
        profile.CompletionStatus = PaymentFlowConstants.CompletionPending;
        var runSettledAmount = await GetRentalBillingRunSettledAmountAsync(profile.Id, currentRun.RunId, ct);
        if (runSettledAmount <= 0m)
            runSettledAmount = Math.Max(0m, currentRun.SettledAmount);
        profile.SettledAmount = runSettledAmount;
        profile.OutstandingAmount = Math.Max(0m, billedAmount - runSettledAmount);
        profile.SettlementStatus = DetermineBillingSettlementStatus(profile, runSettledAmount, billedAmount);
        if (string.Equals(profile.SettlementStatus, PaymentFlowConstants.SettlementStatusUnpaid, StringComparison.OrdinalIgnoreCase))
            profile.SettlementStatus = PaymentFlowConstants.SettlementStatusPending;
        profile.RequiresFollowUp = profile.RequiresFollowUp || profile.OutstandingAmount > 0m;
        currentRun.Status = PaymentFlowConstants.BillingStatusInProgress;
        currentRun.BilledAmount = billedAmount;
        currentRun.SettledAmount = runSettledAmount;
        currentRun.SettlementStatus = profile.SettlementStatus;
        UpsertBillingRun(profile, currentRun);
        profile.IsDirty = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(
            billingProfileId,
            reusedExistingInvoice ? "이미 생성된 렌탈 청구 전표를 열었습니다." : "렌탈 청구를 시작했습니다.",
            invoiceId);
    }

    private async Task<(bool Success, string Message, LocalInvoice? Invoice)> SaveRentalBillingInvoiceAsync(
        LocalInvoice invoice,
        SessionState session,
        CancellationToken ct)
    {
        if (_local is null)
            return (false, "렌탈 청구 전표 저장 서비스를 사용할 수 없습니다.", null);

        var saveContext = new InvoiceSaveContext
        {
            Username = session.User?.Username ?? "rental-billing",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet),
            ForceOverride = true
        };
        var saveResult = await _local.SaveInvoiceAsync(invoice, saveContext, session, ct);
        if (!saveResult.Success)
        {
            var message = string.IsNullOrWhiteSpace(saveResult.Message)
                ? "렌탈 청구 전표를 저장할 수 없습니다."
                : saveResult.Message;
            return (false, message, null);
        }

        var savedInvoice = await _local.GetInvoiceAsync(saveResult.SavedInvoiceId, ct);
        return savedInvoice is null
            ? (false, "저장한 렌탈 청구 전표를 다시 불러올 수 없습니다.", null)
            : (true, string.Empty, savedInvoice);
    }

    private async Task<decimal> GetRentalBillingRunSettledAmountAsync(
        Guid billingProfileId,
        Guid billingRunId,
        CancellationToken ct)
    {
        if (billingProfileId == Guid.Empty || billingRunId == Guid.Empty)
            return 0m;

        var amounts = await _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedRentalBillingProfileId == billingProfileId &&
                transaction.LinkedRentalBillingRunId == billingRunId)
            .Select(transaction => transaction.SettlementAmount)
            .ToListAsync(ct);
        return Math.Max(0m, amounts.Sum());
    }

    public async Task<LocalMutationResult> HoldBillingAsync(
        Guid billingProfileId,
        string note,
        SessionState session,
        CancellationToken ct = default,
        long? expectedRevision = null)
        => await HoldBillingAsync(
            billingProfileId,
            DateOnly.FromDateTime(DateTime.Today),
            note,
            session,
            ct,
            expectedRevision);

    public async Task<LocalMutationResult> HoldBillingAsync(
        Guid billingProfileId,
        DateOnly referenceDate,
        string note,
        SessionState session,
        CancellationToken ct = default,
        long? expectedRevision = null)
    {
        var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");
        if (!CanEditRental(
                string.IsNullOrWhiteSpace(profile.ResponsibleOfficeCode)
                    ? profile.ManagementCompanyCode
                    : profile.ResponsibleOfficeCode,
                session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구를 보류할 수 없습니다.");
        if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(profile, expectedRevision, "렌탈 청구", out var conflictMessage))
            return LocalMutationResult.Conflict(conflictMessage);

        NormalizeBillingSchedule(profile, referenceDate);
        profile.BillingStatus = PaymentFlowConstants.BillingStatusOnHold;
        profile.CompletionStatus = PaymentFlowConstants.CompletionPending;
        profile.RequiresFollowUp = true;
        var normalizedNote = (note ?? string.Empty).Trim();
        var currentRun = GetOrCreateBillingRun(profile, referenceDate, persistChanges: true);
        if (currentRun is not null)
        {
            currentRun.Status = PaymentFlowConstants.BillingStatusOnHold;
            currentRun.Note = normalizedNote;
            UpsertBillingRun(profile, currentRun);
        }
        profile.IsDirty = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(billingProfileId, "렌탈 청구를 보류했습니다.");
    }

    public async Task<LocalMutationResult> RegisterBillingSettlementAsync(
        Guid billingProfileId,
        DateOnly referenceDate,
        decimal? settledAmount,
        string note,
        SessionState session,
        CancellationToken ct = default,
        long? expectedRevision = null,
        Guid? billingRunId = null)
    {
        var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");
        if (!CanEditRental(
                string.IsNullOrWhiteSpace(profile.ResponsibleOfficeCode)
                    ? profile.ManagementCompanyCode
                    : profile.ResponsibleOfficeCode,
                session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구의 수금을 등록할 수 없습니다.");
        if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(profile, expectedRevision, "렌탈 청구", out var conflictMessage))
            return LocalMutationResult.Conflict(conflictMessage);

        NormalizeBillingSchedule(profile, referenceDate);
        var currentRun = FindBillingRunById(profile, billingRunId);
        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty && currentRun is null)
            return LocalMutationResult.Denied("선택한 청구월 정보를 찾을 수 없습니다. 목록을 새로고침한 뒤 다시 시도하세요.");

        currentRun ??= GetOrCreateBillingRun(profile, referenceDate, persistChanges: true);
        var billedAmount = currentRun?.BilledAmount ?? profile.MonthlyAmount;
        var amount = settledAmount.GetValueOrDefault(billedAmount);
        if (amount < 0m)
            amount = 0m;

        profile.SettledAmount = amount;
        profile.OutstandingAmount = Math.Max(0m, billedAmount - amount);
        profile.SettlementStatus = profile.OutstandingAmount <= 0m
            ? PaymentFlowConstants.SettlementStatusConfirmed
            : PaymentFlowConstants.SettlementStatusPartial;
        profile.LastSettledDate = referenceDate;
        profile.RequiresFollowUp = profile.OutstandingAmount > 0m;
        var normalizedNote = (note ?? string.Empty).Trim();
        if (!string.Equals(profile.BillingStatus, PaymentFlowConstants.BillingStatusCompleted, StringComparison.OrdinalIgnoreCase))
        {
            profile.BillingStatus = PaymentFlowConstants.BillingStatusInProgress;
        }
        if (currentRun is not null)
        {
            currentRun.BilledAmount = billedAmount;
            currentRun.SettledAmount = amount;
            currentRun.SettlementStatus = profile.SettlementStatus;
            currentRun.SettledDate = referenceDate;
            currentRun.Note = normalizedNote;
            currentRun.Status = profile.OutstandingAmount <= 0m
                ? PaymentFlowConstants.BillingStatusCompleted
                : PaymentFlowConstants.BillingStatusInProgress;
            UpsertBillingRun(profile, currentRun);
        }

        var scheduledDate = currentRun?.ScheduledDate
            ?? GetNextBillingDate(profile, referenceDate)
            ?? RentalBillingScheduleRules.BuildBillingDate(referenceDate.Year, referenceDate.Month, profile.BillingDay, profile.BillingDayMode);
        var billingYearMonth = $"{scheduledDate.Year:0000}-{scheduledDate.Month:00}";
        var log = await _db.RentalBillingLogs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.BillingProfileId == billingProfileId && current.BillingYearMonth == billingYearMonth, ct);

        var now = DateTime.UtcNow;
        if (log is null)
        {
            log = new LocalRentalBillingLog
            {
                Id = Guid.NewGuid(),
                BillingProfileId = billingProfileId,
                BillingYearMonth = billingYearMonth,
                ScheduledDate = scheduledDate,
                ProcessedDate = referenceDate,
                ProcessedByUsername = session.User?.Username ?? string.Empty,
                Status = profile.SettlementStatus,
                BilledAmount = billedAmount,
                Note = normalizedNote,
                ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                IsDirty = true
            };
            _db.RentalBillingLogs.Add(log);
        }
        else
        {
            log.ProcessedDate = referenceDate;
            log.ProcessedByUsername = session.User?.Username ?? string.Empty;
            log.Status = profile.SettlementStatus;
            log.BilledAmount = billedAmount;
            log.Note = normalizedNote;
            log.ResponsibleOfficeCode = profile.ResponsibleOfficeCode;
            log.UpdatedAtUtc = now;
            log.IsDirty = true;
            log.IsDeleted = false;
        }

        profile.IsDirty = true;
        profile.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(billingProfileId, "수금을 등록했습니다.");
    }

    public async Task<LocalMutationResult> DeleteBillingHistoryAsync(
        Guid billingProfileId,
        Guid billingRunId,
        SessionState session,
        long? expectedRevision = null,
        CancellationToken ct = default)
    {
        if (billingProfileId == Guid.Empty || billingRunId == Guid.Empty)
            return LocalMutationResult.Missing("삭제할 청구/입금 내역을 찾을 수 없습니다.");

        var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");
        if (!CanEditRental(
                string.IsNullOrWhiteSpace(profile.ResponsibleOfficeCode)
                    ? profile.ManagementCompanyCode
                    : profile.ResponsibleOfficeCode,
                session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구/입금 내역을 삭제할 수 없습니다.");
        if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(profile, expectedRevision, "렌탈 청구", out var conflictMessage))
            return LocalMutationResult.Conflict(conflictMessage);

        var run = FindBillingRunById(profile, billingRunId);
        if (run is null)
            return LocalMutationResult.Missing("선택한 청구월 정보를 찾을 수 없습니다. 목록을 새로고침한 뒤 다시 시도하세요.");

        var linkedInvoices = await _db.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                !invoice.IsDeleted &&
                invoice.VoucherType == VoucherType.Sales &&
                invoice.LinkedRentalBillingProfileId == billingProfileId &&
                invoice.LinkedRentalBillingRunId == billingRunId)
            .Select(invoice => new
            {
                invoice.Id,
                invoice.VersionGroupId,
                invoice.IsLatestVersion,
                invoice.UpdatedAtUtc
            })
            .ToListAsync(ct);
        var linkedInvoiceIds = linkedInvoices
            .Select(invoice => invoice.Id)
            .Distinct()
            .ToList();
        var invoiceDeleteTargetIds = linkedInvoices
            .GroupBy(invoice => invoice.VersionGroupId == Guid.Empty ? invoice.Id : invoice.VersionGroupId)
            .Select(group => group
                .OrderByDescending(invoice => invoice.IsLatestVersion)
                .ThenByDescending(invoice => invoice.UpdatedAtUtc)
                .First()
                .Id)
            .ToList();

        var linkedTransactions = await LoadBillingHistoryDeleteTransactionsAsync(
            billingProfileId,
            billingRunId,
            linkedInvoiceIds,
            ct);

        if ((linkedTransactions.Count > 0 || invoiceDeleteTargetIds.Count > 0) && _local is null)
            return LocalMutationResult.Denied("연결된 판매전표/입금 내역 삭제 서비스를 사용할 수 없습니다.");
        if (linkedTransactions.Count > 0 && !CanDeleteRentalBillingTransactions(session))
            return LocalMutationResult.Denied("권한이 없어 연결된 입금 내역을 삭제할 수 없습니다. 수금/지급 편집 권한이 필요합니다.");
        if (linkedTransactions.Count > 0 && !session.HasAdministrativePrivileges)
        {
            var transactionIds = linkedTransactions.Select(transaction => transaction.Id).ToList();
            var attachmentStatuses = await LoadBillingHistoryDeleteAttachmentStatusesAsync(transactionIds, ct);
            if (attachmentStatuses.Any(status => string.Equals(status, "확인완료", StringComparison.OrdinalIgnoreCase)))
                return LocalMutationResult.Denied("확인완료된 증빙이 있는 입금 내역은 관리자만 삭제할 수 있습니다.");
        }

        foreach (var transaction in linkedTransactions)
        {
            var deleteTransactionResult = await _local!.DeleteTransactionAsync(transaction.Id, session, transaction.Revision, ct);
            if (!deleteTransactionResult.Success)
                return ConvertOfficeMutationResult(deleteTransactionResult, "연결된 입금 내역을 삭제할 수 없습니다.");
        }

        foreach (var invoiceId in invoiceDeleteTargetIds)
            await _local!.DeleteInvoiceAsync(invoiceId, ct);

        profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");

        var now = DateTime.UtcNow;
        var runs = GetBillingRuns(profile);
        var removedRunCount = runs.RemoveAll(current => current.RunId == billingRunId);
        await RefreshBillingProfileAfterHistoryDeleteAsync(profile, runs, ct);

        var billingYearMonth = $"{run.ScheduledDate.Year:0000}-{run.ScheduledDate.Month:00}";
        var logs = await _db.RentalBillingLogs.IgnoreQueryFilters()
            .Where(log => log.BillingProfileId == billingProfileId && log.BillingYearMonth == billingYearMonth)
            .ToListAsync(ct);
        var deletedLogCount = 0;
        foreach (var log in logs)
        {
            if (!log.IsDeleted)
                deletedLogCount++;

            log.IsDeleted = true;
            log.IsDirty = true;
            log.UpdatedAtUtc = now;
        }

        profile.IsDirty = true;
        profile.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);

        if (removedRunCount == 0 &&
            linkedTransactions.Count == 0 &&
            invoiceDeleteTargetIds.Count == 0 &&
            deletedLogCount == 0)
            return LocalMutationResult.Missing("삭제할 청구/입금 내역을 찾을 수 없습니다.");

        var deletedParts = new List<string>();
        if (invoiceDeleteTargetIds.Count > 0)
            deletedParts.Add($"판매전표 {invoiceDeleteTargetIds.Count:N0}건");
        if (linkedTransactions.Count > 0)
            deletedParts.Add($"입금 내역 {linkedTransactions.Count:N0}건");
        if (removedRunCount > 0 || deletedLogCount > 0)
            deletedParts.Add("청구월 기록");
        var deletedSummary = deletedParts.Count == 0 ? "선택 내역" : string.Join(", ", deletedParts);
        return LocalMutationResult.Ok(billingProfileId, $"{deletedSummary}을 삭제했습니다.");
    }

    private async Task<List<RentalBillingHistoryDeleteTransactionLookup>> LoadBillingHistoryDeleteTransactionsAsync(
        Guid billingProfileId,
        Guid billingRunId,
        IReadOnlyCollection<Guid> linkedInvoiceIds,
        CancellationToken ct)
    {
        var transactionsById = new Dictionary<Guid, RentalBillingHistoryDeleteTransactionLookup>();
        var directTransactions = await _db.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedRentalBillingProfileId == billingProfileId &&
                transaction.LinkedRentalBillingRunId == billingRunId)
            .Select(transaction => new RentalBillingHistoryDeleteTransactionLookup(
                transaction.Id,
                transaction.Revision,
                transaction.TransactionDate))
            .ToListAsync(ct);
        foreach (var transaction in directTransactions)
            transactionsById[transaction.Id] = transaction;

        var invoiceIds = linkedInvoiceIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        foreach (var batchIds in invoiceIds.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds;
            var invoiceLinkedTransactions = await _db.Transactions.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(transaction =>
                    !transaction.IsDeleted &&
                    transaction.LinkedInvoiceId.HasValue &&
                    scopedBatchIds.Contains(transaction.LinkedInvoiceId.Value))
                .Select(transaction => new RentalBillingHistoryDeleteTransactionLookup(
                    transaction.Id,
                    transaction.Revision,
                    transaction.TransactionDate))
                .ToListAsync(ct);
            foreach (var transaction in invoiceLinkedTransactions)
                transactionsById[transaction.Id] = transaction;
        }

        return transactionsById.Values
            .OrderBy(transaction => transaction.TransactionDate)
            .ThenBy(transaction => transaction.Id)
            .ToList();
    }

    private async Task<List<string>> LoadBillingHistoryDeleteAttachmentStatusesAsync(
        IReadOnlyCollection<Guid> transactionIds,
        CancellationToken ct)
    {
        var ids = transactionIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return new List<string>();

        var statuses = new List<string>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds;
            statuses.AddRange(await _db.TransactionAttachments.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(attachment => !attachment.IsDeleted && scopedBatchIds.Contains(attachment.TransactionId))
                .Select(attachment => attachment.VerificationStatus)
                .ToListAsync(ct));
        }

        return statuses;
    }

    private readonly record struct RentalBillingHistoryDeleteTransactionLookup(
        Guid Id,
        long Revision,
        DateOnly TransactionDate);

    private static bool CanDeleteRentalBillingTransactions(SessionState? session)
        => session is not null &&
           (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.PaymentEdit));

    private static LocalMutationResult ConvertOfficeMutationResult(OfficeMutationResult result, string fallbackMessage)
    {
        var message = string.IsNullOrWhiteSpace(result.Message) ? fallbackMessage : result.Message;
        if (result.ConcurrencyConflict)
            return LocalMutationResult.Conflict(message);
        if (result.NotFound)
            return LocalMutationResult.Missing(message);
        return LocalMutationResult.Denied(message);
    }

    private async Task RefreshBillingProfileAfterHistoryDeleteAsync(
        LocalRentalBillingProfile profile,
        List<RentalBillingRunModel> remainingRuns,
        CancellationToken ct)
    {
        var activeRuns = remainingRuns
            .Where(run => run.RunId != Guid.Empty)
            .OrderByDescending(run => run.ScheduledDate)
            .ThenByDescending(run => run.PeriodEndDate)
            .ToList();
        if (activeRuns.Count == 0)
        {
            profile.BillingRunsJson = JsonSerializer.Serialize(activeRuns, RentalJsonOptions);
            profile.BillingStatus = PaymentFlowConstants.BillingStatusPlanned;
            profile.SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid;
            profile.CompletionStatus = PaymentFlowConstants.CompletionPending;
            profile.SettledAmount = 0m;
            profile.OutstandingAmount = 0m;
            profile.LastBilledDate = null;
            profile.LastSettledDate = null;
            profile.RequiresFollowUp = false;
            return;
        }

        var runIds = activeRuns.Select(run => run.RunId).Distinct().ToList();
        var (settlementByRun, invoiceByRun) = await LoadBillingRunReferencesAsync(runIds, ct);
        var invoiceTotalsByRun = invoiceByRun.ToDictionary(
            pair => pair.Key,
            pair => Math.Max(0m, pair.Value.TotalAmount));

        var activeRunIds = new HashSet<Guid>(
            settlementByRun.Where(pair => pair.Value.SettledAmount > 0m).Select(pair => pair.Key)
                .Concat(invoiceTotalsByRun.Keys));
        foreach (var run in activeRuns)
        {
            var billedAmount = invoiceTotalsByRun.TryGetValue(run.RunId, out var invoiceTotal) && invoiceTotal > 0m
                ? invoiceTotal
                : Math.Max(0m, run.BilledAmount);
            var settlementInfo = settlementByRun.TryGetValue(run.RunId, out var foundSettlement)
                ? foundSettlement
                : new RentalBillingRunSettlementInfo(0m, null);
            var settledAmount = Math.Max(0m, settlementInfo.SettledAmount);
            var outstandingAmount = Math.Max(0m, billedAmount - settledAmount);
            run.BilledAmount = billedAmount;
            run.SettledAmount = settledAmount;
            run.SettledDate = settlementInfo.LastSettledDate;
            run.SettlementStatus = ResolveBillingHistorySettlementStatus(profile, run, settledAmount, billedAmount, outstandingAmount);
            run.Status = ResolveBillingHistoryStatus(run, outstandingAmount, settledAmount);
        }

        var representativeRun = activeRuns.FirstOrDefault(run => activeRunIds.Contains(run.RunId)) ?? activeRuns.First();
        var representativeBilledAmount = Math.Max(0m, representativeRun.BilledAmount);
        var representativeSettledAmount = Math.Max(0m, representativeRun.SettledAmount);
        var representativeOutstandingAmount = Math.Max(0m, representativeBilledAmount - representativeSettledAmount);
        profile.BillingRunsJson = JsonSerializer.Serialize(activeRuns, RentalJsonOptions);
        profile.BillingStatus = representativeRun.Status;
        profile.SettlementStatus = representativeRun.SettlementStatus;
        profile.CompletionStatus = representativeOutstandingAmount <= 0m && representativeBilledAmount > 0m
            ? PaymentFlowConstants.CompletionDone
            : PaymentFlowConstants.CompletionPending;
        profile.SettledAmount = representativeSettledAmount;
        profile.OutstandingAmount = representativeOutstandingAmount;
        profile.LastBilledDate = activeRuns
            .Where(run => activeRunIds.Contains(run.RunId) || !IsMutableBillingRun(run))
            .Select(run => (DateOnly?)run.ScheduledDate)
            .OrderByDescending(date => date)
            .FirstOrDefault();
        profile.LastSettledDate = settlementByRun.Values
            .OrderByDescending(settlement => settlement.LastSettledDate)
            .Select(settlement => settlement.LastSettledDate)
            .FirstOrDefault();
        profile.RequiresFollowUp = activeRuns.Any(run =>
        {
            var billedAmount = Math.Max(0m, run.BilledAmount);
            var outstandingAmount = Math.Max(0m, billedAmount - Math.Max(0m, run.SettledAmount));
            return outstandingAmount > 0m &&
                   (activeRunIds.Contains(run.RunId) ||
                    string.Equals(run.Status, PaymentFlowConstants.BillingStatusInProgress, StringComparison.OrdinalIgnoreCase));
        });
    }

    public async Task<IReadOnlyList<LocalRentalAsset>> GetBillingAssetCandidatesAsync(
        Guid? billingProfileId,
        Guid? customerId,
        string? customerName,
        string? officeCode,
        bool includeOfficePoolAssets,
        SessionState session,
        CancellationToken ct = default)
    {
        await EnsureAdministrativeBusinessCachesAsync(session, ct);

        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, session.OfficeCode);
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            null,
            normalizedOfficeCode,
            session.TenantCode,
            session.OfficeCode);
        var resolvedCustomerId = customerId;
        if ((!resolvedCustomerId.HasValue || resolvedCustomerId.Value == Guid.Empty) &&
            !string.IsNullOrWhiteSpace(customerName))
        {
            resolvedCustomerId = await ResolveCustomerIdAsync(
                customerName,
                null,
                ct,
                preferredOfficeCode: normalizedOfficeCode,
                preferredTenantCode: normalizedTenantCode);
        }

        var normalizedCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(customerName);
        var query = ApplyAssetScope(_db.RentalAssets.AsNoTracking(), session)
            .Where(asset => !asset.IsDeleted)
            .Where(asset => asset.TenantCode == normalizedTenantCode)
            .Where(asset => !NonOperatingAssetStatusQueryValues.Contains(asset.AssetStatus))
            .Where(asset => !asset.BillingProfileId.HasValue || asset.BillingProfileId == Guid.Empty);

        if (!string.IsNullOrWhiteSpace(normalizedOfficeCode))
            query = query.Where(asset =>
                asset.ResponsibleOfficeCode == normalizedOfficeCode ||
                asset.ManagementCompanyCode == normalizedOfficeCode);

        if (!includeOfficePoolAssets)
        {
            if ((!resolvedCustomerId.HasValue || resolvedCustomerId.Value == Guid.Empty) &&
                string.IsNullOrWhiteSpace(normalizedCustomerName))
            {
                return Array.Empty<LocalRentalAsset>();
            }

            if (resolvedCustomerId.HasValue && resolvedCustomerId.Value != Guid.Empty)
            {
                var customerKey = resolvedCustomerId.Value;
                query = query.Where(asset => asset.CustomerId == customerKey);
            }
            else
            {
                query = query.Where(asset =>
                    asset.CustomerName == normalizedCustomerName ||
                    asset.CurrentCustomerName == normalizedCustomerName);
            }

            var customerScopedAssets = await SelectAssetLinkCandidateProjection(query
                .OrderBy(asset => asset.CustomerName)
                .ThenBy(asset => asset.ManagementNumber)
                .Take(200))
                .ToListAsync(ct);
            await NormalizeAssetCustomerDisplayNamesAsync(customerScopedAssets, ct);
            return customerScopedAssets
                .OrderBy(asset => ResolvePrimaryAssetCustomerName(asset), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(asset => asset.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        var candidateAssets = new List<LocalRentalAsset>(BillingAssetCandidateResultLimit);
        var matchedQuery = BuildBillingCandidateCustomerMatchQuery(query, resolvedCustomerId, normalizedCustomerName);
        if (matchedQuery is not null)
        {
            candidateAssets.AddRange(await SelectAssetLinkCandidateProjection(matchedQuery
                .OrderBy(asset => asset.CustomerName)
                .ThenBy(asset => asset.ManagementNumber)
                .Take(BillingAssetCandidateResultLimit))
                .ToListAsync(ct));
        }

        if (candidateAssets.Count < BillingAssetCandidateResultLimit)
        {
            var remainingQuery = query;
            var selectedIds = candidateAssets.Select(asset => asset.Id).ToList();
            if (selectedIds.Count > 0)
                remainingQuery = remainingQuery.Where(asset => !selectedIds.Contains(asset.Id));

            candidateAssets.AddRange(await SelectAssetLinkCandidateProjection(remainingQuery
                .OrderBy(asset => asset.CustomerName)
                .ThenBy(asset => asset.ManagementNumber)
                .Take(BillingAssetCandidateResultLimit - candidateAssets.Count))
                .ToListAsync(ct));
        }

        await NormalizeAssetCustomerDisplayNamesAsync(candidateAssets, ct);
        return candidateAssets
            .OrderByDescending(asset => IsBillingCandidateCustomerMatch(asset, resolvedCustomerId, normalizedCustomerName))
            .ThenBy(asset => ResolvePrimaryAssetCustomerName(asset), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<LocalRentalAsset>> GetIncludedBillingAssetsAsync(
        Guid? billingProfileId,
        IEnumerable<Guid>? includedAssetIds,
        Guid? customerId,
        string? officeCode,
        SessionState session,
        CancellationToken ct = default)
    {
        var assetIds = (includedAssetIds ?? Enumerable.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if ((!billingProfileId.HasValue || billingProfileId.Value == Guid.Empty) && assetIds.Count == 0)
            return Array.Empty<LocalRentalAsset>();

        await EnsureAdministrativeBusinessCachesAsync(session, ct);

        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, session.OfficeCode);
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            null,
            normalizedOfficeCode,
            session.TenantCode,
            session.OfficeCode);
        var query = ApplySharedAssetViewScope(_db.RentalAssets.AsNoTracking(), session)
            .Where(asset => !asset.IsDeleted);

        var includedAssets = await LoadIncludedBillingAssetsAsync(
            query,
            assetIds,
            billingProfileId,
            normalizedTenantCode,
            normalizedOfficeCode,
            ct);

        await NormalizeAssetCustomerDisplayNamesAsync(includedAssets, ct);
        return includedAssets
            .OrderBy(asset => ResolvePrimaryAssetCustomerName(asset), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static async Task<List<LocalRentalAsset>> LoadIncludedBillingAssetsAsync(
        IQueryable<LocalRentalAsset> query,
        IReadOnlyCollection<Guid> assetIds,
        Guid? billingProfileId,
        string normalizedTenantCode,
        string normalizedOfficeCode,
        CancellationToken ct)
    {
        var assetsById = new Dictionary<Guid, LocalRentalAsset>();
        if (assetIds.Count > 0)
        {
            foreach (var batchIds in assetIds.Chunk(LocalQueryContainsBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var scopedBatchIds = batchIds;
                var explicitAssets = await SelectAssetLinkCandidateProjection(query
                        .Where(asset => scopedBatchIds.Contains(asset.Id)))
                    .ToListAsync(ct);

                foreach (var asset in explicitAssets)
                    assetsById[asset.Id] = asset;
            }
        }

        var profileId = billingProfileId.GetValueOrDefault();
        var hasProfileId = billingProfileId.HasValue && profileId != Guid.Empty;
        if (hasProfileId)
        {
            var linkedAssets = await SelectAssetLinkCandidateProjection(query
                    .Where(asset =>
                        asset.BillingProfileId == profileId &&
                        asset.TenantCode == normalizedTenantCode &&
                        (
                            asset.ResponsibleOfficeCode == normalizedOfficeCode ||
                            asset.ManagementCompanyCode == normalizedOfficeCode
                        ))
                    .OrderBy(asset => asset.CustomerName)
                    .ThenBy(asset => asset.ManagementNumber)
                    .Take(300))
                .ToListAsync(ct);

            foreach (var asset in linkedAssets)
                assetsById[asset.Id] = asset;
        }

        return assetsById.Values.ToList();
    }

    private async Task<List<LocalRentalAsset>> LoadRentalAssetsByIdsAsync(
        IReadOnlyCollection<Guid> assetIds,
        bool ignoreQueryFilters,
        bool asNoTracking,
        bool excludeDeleted,
        CancellationToken ct)
    {
        var ids = assetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var assetsById = new Dictionary<Guid, LocalRentalAsset>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds;
            IQueryable<LocalRentalAsset> query = _db.RentalAssets;
            if (ignoreQueryFilters)
                query = query.IgnoreQueryFilters();
            if (asNoTracking)
                query = query.AsNoTracking();

            query = query.Where(asset => scopedBatchIds.Contains(asset.Id));
            if (excludeDeleted)
                query = query.Where(asset => !asset.IsDeleted);

            var assets = await query.ToListAsync(ct);
            foreach (var asset in assets)
                assetsById[asset.Id] = asset;
        }

        return assetsById.Values.ToList();
    }

    private async Task<List<LocalRentalAssetAssignmentHistory>> LoadRentalAssignmentHistoriesByAssetIdsAsync(
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken ct)
    {
        var ids = assetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var histories = new List<LocalRentalAssetAssignmentHistory>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds;
            var batchHistories = await _db.RentalAssetAssignmentHistories
                .Where(history => scopedBatchIds.Contains(history.AssetId))
                .ToListAsync(ct);
            histories.AddRange(batchHistories);
        }

        return histories;
    }

    public async Task<IReadOnlyList<RentalAssetLinkCandidate>> GetAssetLinkCandidatesAsync(
        Guid? currentBillingProfileId,
        Guid? customerId,
        string? customerName,
        string? officeCode,
        SessionState session,
        bool includeOtherOfficeAssets = false,
        string? searchText = null,
        int maxResults = AssetLinkCandidateResultLimit,
        CancellationToken ct = default)
    {
        await EnsureAdministrativeBusinessCachesAsync(session, ct);

        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, session.OfficeCode);
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            null,
            normalizedOfficeCode,
            session.TenantCode,
            session.OfficeCode);
        var resolvedCustomerId = customerId;
        if ((!resolvedCustomerId.HasValue || resolvedCustomerId.Value == Guid.Empty) &&
            !string.IsNullOrWhiteSpace(customerName))
        {
            resolvedCustomerId = await ResolveCustomerIdAsync(
                customerName,
                null,
                ct,
                preferredOfficeCode: normalizedOfficeCode,
                preferredTenantCode: normalizedTenantCode);
        }

        var normalizedCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(customerName);
        var query = (includeOtherOfficeAssets
                ? ApplySharedAssetViewScope(_db.RentalAssets.AsNoTracking(), session)
                : ApplyAssetScope(_db.RentalAssets.AsNoTracking(), session))
            .Where(asset => !asset.IsDeleted);

        if (!includeOtherOfficeAssets)
            query = query.Where(asset => asset.TenantCode == normalizedTenantCode);

        if (!includeOtherOfficeAssets && !string.IsNullOrWhiteSpace(normalizedOfficeCode))
            query = query.Where(asset =>
                asset.ResponsibleOfficeCode == normalizedOfficeCode ||
                asset.ManagementCompanyCode == normalizedOfficeCode);

        var normalizedSearchText = NormalizeAssetLinkSearchText(searchText);
        if (!string.IsNullOrWhiteSpace(normalizedSearchText))
            query = ApplyAssetLinkSearchFilter(query, normalizedSearchText);

        var cappedMaxResults = Math.Clamp(maxResults, 50, AssetLinkCandidateResultLimit);

        var orderedCandidateQuery = ApplyAssetLinkCandidateOrdering(
            query,
            currentBillingProfileId,
            resolvedCustomerId,
            normalizedCustomerName);
        var assets = await SelectAssetLinkCandidateProjection(orderedCandidateQuery
            .Take(cappedMaxResults))
            .ToListAsync(ct);

        var offices = await GetOfficeMapAsync(ct);
        var linkedProfileIds = assets
            .Where(asset => asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty)
            .Select(asset => asset.BillingProfileId!.Value)
            .Distinct()
            .ToList();
        var profilesById = await GetBillingProfileDisplayLookupMapAsync(linkedProfileIds, ct);
        var customerNameMap = await GetCustomerNameMapAsync(
            assets
                .Where(asset => asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
                .Select(asset => asset.CustomerId!.Value)
                .Concat(profilesById.Values
                    .Where(profile => profile.CustomerId.HasValue && profile.CustomerId.Value != Guid.Empty)
                    .Select(profile => profile.CustomerId!.Value)),
            ct);

        return assets
            .Select(asset =>
            {
                asset.AssetStatus = RentalAssetStatusRules.Normalize(asset.AssetStatus);
                ApplyResolvedAssetCustomerDisplayName(asset, customerNameMap);
                var currentProfileDisplay = asset.BillingProfileId.HasValue &&
                                            asset.BillingProfileId.Value != Guid.Empty &&
                                            profilesById.TryGetValue(asset.BillingProfileId.Value, out var linkedProfile)
                    ? BuildBillingProfileDisplayName(linkedProfile, customerNameMap)
                    : string.Empty;

                var responsibleOfficeName = ResolveOfficeDisplayName(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, offices);
                var managementCompanyName = ResolveOfficeDisplayName(asset.ManagementCompanyCode, asset.OfficeCode, offices);
                var isOutsideCurrentOffice = IsOutsideCurrentAssetOffice(asset, normalizedOfficeCode);

                return new RentalAssetLinkCandidate
                {
                    Source = asset,
                    CustomerDisplayName = ResolvePrimaryAssetCustomerName(asset),
                    ResponsibleOfficeName = responsibleOfficeName,
                    ManagementCompanyName = managementCompanyName,
                    AssetScopeDisplay = BuildAssetScopeDisplay(responsibleOfficeName, managementCompanyName),
                    IsOutsideCurrentOffice = isOutsideCurrentOffice,
                    BillingProfileId = asset.BillingProfileId,
                    CurrentBillingProfileDisplay = currentProfileDisplay
                };
            })
            .OrderByDescending(candidate => IsBillingCandidateCustomerMatch(candidate.Source, resolvedCustomerId, normalizedCustomerName))
            .ThenBy(candidate => candidate.BillingProfileId.HasValue && candidate.BillingProfileId != currentBillingProfileId ? 1 : 0)
            .ThenBy(candidate => candidate.CustomerDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Source.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<Dictionary<Guid, RentalBillingProfileDisplayLookup>> GetBillingProfileDisplayLookupMapAsync(
        IEnumerable<Guid> profileIds,
        CancellationToken ct)
    {
        var ids = profileIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, RentalBillingProfileDisplayLookup>();

        var result = new Dictionary<Guid, RentalBillingProfileDisplayLookup>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds;
            var profiles = await _db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => scopedBatchIds.Contains(profile.Id) && !profile.IsDeleted)
                .Select(profile => new
                {
                    profile.Id,
                    profile.CustomerId,
                    profile.CustomerName,
                    profile.ProfileKey,
                    profile.InstallSiteName
                })
                .ToListAsync(ct);

            foreach (var profile in profiles)
            {
                result[profile.Id] = new RentalBillingProfileDisplayLookup(
                    profile.Id,
                    profile.CustomerId,
                    profile.CustomerName,
                    profile.ProfileKey,
                    profile.InstallSiteName);
            }
        }

        return result;
    }

    private static IOrderedQueryable<LocalRentalAsset> ApplyAssetLinkCandidateOrdering(
        IQueryable<LocalRentalAsset> query,
        Guid? currentBillingProfileId,
        Guid? resolvedCustomerId,
        string? normalizedCustomerName)
    {
        IOrderedQueryable<LocalRentalAsset>? ordered = null;

        if (currentBillingProfileId.HasValue && currentBillingProfileId.Value != Guid.Empty)
        {
            var currentProfileId = currentBillingProfileId.Value;
            ordered = query.OrderByDescending(asset => asset.BillingProfileId == currentProfileId);
        }

        if (resolvedCustomerId.HasValue && resolvedCustomerId.Value != Guid.Empty)
        {
            var customerId = resolvedCustomerId.Value;
            ordered = ordered is null
                ? query.OrderByDescending(asset => asset.CustomerId == customerId)
                : ordered.ThenByDescending(asset => asset.CustomerId == customerId);
        }

        if (!string.IsNullOrWhiteSpace(normalizedCustomerName))
        {
            var customerName = normalizedCustomerName.Trim();
            ordered = ordered is null
                ? query.OrderByDescending(asset =>
                    asset.CustomerName == customerName ||
                    asset.CurrentCustomerName == customerName)
                : ordered.ThenByDescending(asset =>
                    asset.CustomerName == customerName ||
                    asset.CurrentCustomerName == customerName);
        }

        return (ordered is null
                ? query.OrderBy(asset => asset.CustomerName)
                : ordered.ThenBy(asset => asset.CustomerName))
            .ThenBy(asset => asset.ManagementNumber);
    }

    private static IQueryable<LocalRentalAsset> SelectAssetLinkCandidateProjection(IQueryable<LocalRentalAsset> query)
        => query.Select(asset => new LocalRentalAsset
        {
            Id = asset.Id,
            TenantCode = asset.TenantCode,
            OfficeCode = asset.OfficeCode,
            CustomerId = asset.CustomerId,
            BillingProfileId = asset.BillingProfileId,
            ManagementNumber = asset.ManagementNumber,
            ManagementCompanyCode = asset.ManagementCompanyCode,
            ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            CurrentCustomerName = asset.CurrentCustomerName,
            CustomerName = asset.CustomerName,
            InstallSiteName = asset.InstallSiteName,
            InstallLocation = asset.InstallLocation,
            BillingEligibilityStatus = asset.BillingEligibilityStatus,
            ItemCategoryName = asset.ItemCategoryName,
            ItemName = asset.ItemName,
            Manufacturer = asset.Manufacturer,
            MachineNumber = asset.MachineNumber,
            AssetStatus = asset.AssetStatus,
            Notes = asset.Notes,
            MonthlyFee = asset.MonthlyFee,
            ContractDate = asset.ContractDate,
            ContractStartDate = asset.ContractStartDate,
            InstallDate = asset.InstallDate,
            PurchaseDate = asset.PurchaseDate
        });

    public Task<IReadOnlyList<RentalAssetAssignmentHistoryViewItem>> GetAssetAssignmentHistoriesAsync(
        Guid assetId,
        CancellationToken ct = default)
        => GetAssetAssignmentHistoriesAsync(assetId, maxDisplayRows: 0, ct);

    public async Task<IReadOnlyList<RentalAssetAssignmentHistoryViewItem>> GetAssetAssignmentHistoriesAsync(
        Guid assetId,
        int maxDisplayRows,
        CancellationToken ct = default)
    {
        if (assetId == Guid.Empty)
            return [];

        IQueryable<LocalRentalAssetAssignmentHistory> query = _db.RentalAssetAssignmentHistories
            .AsNoTracking()
            .Where(history => history.AssetId == assetId)
            .OrderByDescending(history => history.IsCurrent)
            .ThenByDescending(history => history.LinkedAtUtc);

        var rawFetchLimit = ResolveAssignmentHistoryRawFetchLimit(maxDisplayRows);
        if (rawFetchLimit.HasValue)
            query = query.Take(rawFetchLimit.Value);

        var histories = await query
            .ToListAsync(ct);
        if (histories.Count == 0)
            return [];

        var profileIds = histories
            .Where(history => history.BillingProfileId.HasValue && history.BillingProfileId.Value != Guid.Empty)
            .Select(history => history.BillingProfileId!.Value)
            .Distinct()
            .ToList();
        var profileDisplayLookup = await GetBillingProfileDisplayTextMapAsync(profileIds, ct);
        var asset = await _db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == assetId, ct);

        IEnumerable<LocalRentalAssetAssignmentHistory> displayHistories = histories
            .GroupBy(BuildAssignmentHistoryLogicalKey)
            .Select(group => group
                .OrderByDescending(history => history.IsCurrent)
                .ThenByDescending(history => !string.IsNullOrWhiteSpace(history.BillingProfileDisplay))
                .ThenByDescending(history => history.UpdatedAtUtc)
                .First())
            .OrderByDescending(history => history.IsCurrent)
            .ThenByDescending(history => history.LinkedAtUtc);

        if (maxDisplayRows > 0)
            displayHistories = displayHistories.Take(maxDisplayRows);

        return displayHistories
            .Select(history => BuildAssignmentHistoryViewItem(history, asset, profileDisplayLookup))
            .ToList();
    }

    private async Task<Dictionary<Guid, string>> GetBillingProfileDisplayTextMapAsync(
        IEnumerable<Guid> profileIds,
        CancellationToken ct)
    {
        var ids = profileIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        var result = new Dictionary<Guid, string>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds;
            var profiles = await _db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => scopedBatchIds.Contains(profile.Id))
                .Select(profile => new
                {
                    profile.Id,
                    profile.CustomerName,
                    profile.ItemName
                })
                .ToListAsync(ct);

            foreach (var profile in profiles)
                result[profile.Id] = BuildBillingProfileDisplay(profile.CustomerName, profile.ItemName);
        }

        return result;
    }

    private static int? ResolveAssignmentHistoryRawFetchLimit(int maxDisplayRows)
    {
        if (maxDisplayRows <= 0)
            return null;

        var normalizedDisplayLimit = Math.Clamp(maxDisplayRows, 1, 5_000);
        return Math.Min(normalizedDisplayLimit * 3, normalizedDisplayLimit + 600);
    }

    public async Task<RentalAssetAssignmentHistoryEditRequest?> CreateAssetAssignmentHistoryEditRequestAsync(
        Guid assetId,
        Guid? historyId = null,
        CancellationToken ct = default)
    {
        if (assetId == Guid.Empty)
            return null;

        var asset = await _db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == assetId, ct);
        if (asset is null)
            return null;

        if (historyId.HasValue && historyId.Value != Guid.Empty)
        {
            var history = await _db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == historyId.Value && current.AssetId == assetId, ct);
            if (history is null)
                return null;

            return new RentalAssetAssignmentHistoryEditRequest
            {
                HistoryId = history.Id,
                AssetId = history.AssetId,
                IsCurrent = history.IsCurrent,
                LinkedAtLocal = NormalizeHistoryUtc(history.LinkedAtUtc).ToLocalTime(),
                UnlinkedAtLocal = history.UnlinkedAtUtc.HasValue ? NormalizeHistoryUtc(history.UnlinkedAtUtc.Value).ToLocalTime() : null,
                CustomerName = history.CustomerName,
                InstallLocation = history.InstallLocation,
                BillingProfileDisplay = history.BillingProfileDisplay,
                ItemName = FirstNonEmpty(history.ItemName, asset.ItemName),
                MachineNumber = FirstNonEmpty(history.MachineNumber, asset.MachineNumber),
                ManagementNumber = FirstNonEmpty(history.ManagementNumber, asset.ManagementNumber),
                MonthlyFee = history.MonthlyFee > 0m ? history.MonthlyFee : Math.Max(0m, asset.MonthlyFee),
                ChangeReason = history.ChangeReason
            };
        }

        var customerName = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
            asset.CurrentCustomerName,
            asset.CustomerName));
        var installLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
            asset.InstallLocation,
            asset.InstallSiteName));

        return new RentalAssetAssignmentHistoryEditRequest
        {
            AssetId = asset.Id,
            IsCurrent = false,
            LinkedAtLocal = DateTime.Today,
            UnlinkedAtLocal = DateTime.Today,
            CustomerName = customerName,
            InstallLocation = installLocation,
            BillingProfileDisplay = await ResolveLastBillingProfileDisplayAsync(asset.BillingProfileId, asset, ct),
            ItemName = asset.ItemName,
            MachineNumber = asset.MachineNumber,
            ManagementNumber = asset.ManagementNumber,
            MonthlyFee = Math.Max(0m, asset.MonthlyFee),
            ChangeReason = "수동 추가"
        };
    }

    public async Task<LocalMutationResult> SaveAssetAssignmentHistoryAsync(
        RentalAssetAssignmentHistoryEditRequest request,
        SessionState session,
        CancellationToken ct = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.AssetId == Guid.Empty)
            return LocalMutationResult.Missing("렌탈 자산을 찾을 수 없습니다.");

        var asset = await _db.RentalAssets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == request.AssetId, ct);
        if (asset is null)
            return LocalMutationResult.Missing("렌탈 자산을 찾을 수 없습니다.");

        if (!CanEditAssetScope(asset.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산의 임대이력을 수정할 수 없습니다.");

        LocalRentalAssetAssignmentHistory? existing = null;
        if (request.HistoryId != Guid.Empty)
        {
            existing = await _db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == request.HistoryId && current.AssetId == request.AssetId, ct);
            if (existing is null)
                return LocalMutationResult.Missing("임대이력을 찾을 수 없습니다.");
        }

        var now = DateTime.UtcNow;
        var linkedAtUtc = ConvertLocalHistoryDateToUtc(request.LinkedAtLocal);
        var unlinkedAtUtc = request.IsCurrent || request.UnlinkedAtLocal is null
            ? (DateTime?)null
            : ConvertLocalHistoryDateToUtc(request.UnlinkedAtLocal.Value);
        if (!request.IsCurrent && unlinkedAtUtc is null)
            unlinkedAtUtc = linkedAtUtc;
        if (unlinkedAtUtc.HasValue && linkedAtUtc > unlinkedAtUtc.Value)
            linkedAtUtc = unlinkedAtUtc.Value.AddSeconds(-1);

        var history = existing ?? new LocalRentalAssetAssignmentHistory
        {
            Id = request.HistoryId == Guid.Empty ? Guid.NewGuid() : request.HistoryId,
            AssetId = asset.Id,
            CreatedAtUtc = now
        };

        history.AssetId = asset.Id;
        history.BillingProfileId = asset.BillingProfileId;
        history.CustomerId = asset.CustomerId;
        history.TenantCode = asset.TenantCode;
        history.ResponsibleOfficeCode = asset.ResponsibleOfficeCode;
        history.CustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(request.CustomerName);
        history.InstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(request.InstallLocation);
        history.BillingProfileDisplay = RentalCatalogValueNormalizer.NormalizeDisplayText(request.BillingProfileDisplay);
        history.ItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(FirstNonEmpty(request.ItemName, asset.ItemName));
        history.MachineNumber = (FirstNonEmpty(request.MachineNumber, asset.MachineNumber) ?? string.Empty).Trim();
        history.ManagementNumber = (FirstNonEmpty(request.ManagementNumber, asset.ManagementNumber) ?? string.Empty).Trim();
        history.MonthlyFee = Math.Max(0m, request.MonthlyFee);
        history.ContractStartDate = asset.ContractStartDate ?? asset.InstallDate;
        history.ContractEndDate = asset.RentalEndDate;
        history.ChangeReason = RentalCatalogValueNormalizer.NormalizeDisplayText(request.ChangeReason);
        history.IsCurrent = request.IsCurrent;
        history.LinkedAtUtc = linkedAtUtc;
        history.UnlinkedAtUtc = unlinkedAtUtc;
        history.IsDeleted = false;
        history.IsDirty = true;
        history.UpdatedAtUtc = now;

        if (existing is null)
            _db.RentalAssetAssignmentHistories.Add(history);

        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(history.Id, "임대이력을 저장했습니다.");
    }

    public async Task<LocalMutationResult> DeleteAssetAssignmentHistoryAsync(
        Guid historyId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (historyId == Guid.Empty)
            return LocalMutationResult.Missing("임대이력을 찾을 수 없습니다.");

        var history = await _db.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == historyId, ct);
        if (history is null)
            return LocalMutationResult.Missing("임대이력을 찾을 수 없습니다.");

        var asset = await _db.RentalAssets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == history.AssetId, ct);
        if (asset is null)
            return LocalMutationResult.Missing("렌탈 자산을 찾을 수 없습니다.");

        if (!CanEditAssetScope(asset.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산의 임대이력을 삭제할 수 없습니다.");

        history.IsDeleted = true;
        history.IsDirty = true;
        history.IsCurrent = false;
        history.UnlinkedAtUtc ??= DateTime.UtcNow;
        history.ChangeReason = string.IsNullOrWhiteSpace(history.ChangeReason)
            ? "수동 삭제"
            : history.ChangeReason;
        history.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(history.Id, "임대이력을 삭제했습니다.");
    }

    public async Task<LocalMutationResult> SaveAssetAsync(
        LocalRentalAsset asset,
        SessionState session,
        CancellationToken ct = default,
        bool allowWorkbookNameVariants = true,
        bool allowCategoryRecovery = false)
    {
        if (asset is null)
            throw new ArgumentNullException(nameof(asset));

        await AssetSaveLock.WaitAsync(ct);
        try
        {
            var existing = await _db.RentalAssets.IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == asset.Id, ct);
            if (existing is not null && !CanEditAssetScope(
                    RentalScopeNormalizer.ResolveResponsibleOfficeCode(
                        existing.TenantCode,
                        existing.OfficeCode,
                        existing.ManagementCompanyCode,
                        existing.ResponsibleOfficeCode,
                        session.OfficeCode),
                    session))
                return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산을 수정할 수 없습니다.");

            var officeCode = await ResolveRentalOfficeCodeAsync(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, session.OfficeCode, ct);
            if (string.IsNullOrWhiteSpace(officeCode))
                return LocalMutationResult.Denied("담당지점을 선택하세요.");

            officeCode = RentalScopeNormalizer.ResolveResponsibleOfficeCode(
                asset.TenantCode,
                asset.OfficeCode,
                asset.ManagementCompanyCode,
                officeCode,
                session.OfficeCode);

            if (!CanManageAllAssetScope(session) &&
                !string.Equals(officeCode, GetDefaultAssetOfficeCode(session), StringComparison.OrdinalIgnoreCase))
            {
                return LocalMutationResult.Denied("일반 사용자는 본인 담당지점 자산만 등록/수정할 수 있습니다.");
            }

            ApplyAssetOfficeScope(asset, officeCode);
            if (!CanEditAssetScope(officeCode, session))
                return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산을 저장할 수 없습니다.");
            asset.ManagementNumber = string.IsNullOrWhiteSpace(asset.ManagementNumber)
                ? (existing?.ManagementNumber ?? string.Empty).Trim()
                : asset.ManagementNumber.Trim();
            asset.ManagementId = string.IsNullOrWhiteSpace(asset.ManagementId)
                ? (existing?.ManagementId ?? string.Empty).Trim()
                : asset.ManagementId.Trim();
            asset.CustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CustomerName);
            asset.CurrentCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ? asset.CustomerName : asset.CurrentCustomerName);
            asset.CurrentLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CurrentLocation);
            asset.InstallSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
                asset.InstallSiteName,
                asset.CurrentCustomerName,
                asset.CustomerName,
                existing?.InstallSiteName,
                existing?.CurrentCustomerName,
                existing?.CustomerName,
                asset.InstallLocation));
            asset.ItemCategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(asset.ItemCategoryName);
            asset.Manufacturer = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.Manufacturer);
            asset.ItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
            asset.MachineNumber = (asset.MachineNumber ?? string.Empty).Trim();
            asset.PurchaseVendor = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.PurchaseVendor);
            asset.InstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallLocation);
            asset.DepositText = (asset.DepositText ?? string.Empty).Trim();
            asset.AssetStatus = ResolveAssetStatus(asset.AssetStatus, asset.CurrentLocation, asset.DisposalDate);
            await ApplyNonOperatingAssetStateRulesAsync(asset, existing, ct);
            asset.BillingEligibilityStatus = string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus)
                ? GetDefaultBillingEligibilityStatus(asset)
                : asset.BillingEligibilityStatus.Trim();
            asset.BillingExclusionReason = (asset.BillingExclusionReason ?? string.Empty).Trim();
            await EnsureAssetManagementIdentifiersAsync(
                asset,
                existing,
                existing?.CreatedAtUtc ?? DateTime.UtcNow,
                ct);
            try
            {
                await EnrichAssetReferencesAsync(
                    asset,
                    ct,
                    allowCategoryRecovery: allowCategoryRecovery,
                    allowWorkbookNameVariants: allowWorkbookNameVariants);
            }
            catch (InvalidOperationException ex)
            {
                return LocalMutationResult.Denied(ex.Message);
            }

            var linkedCustomer = await GetRentalLinkedCustomerAsync(asset.CustomerId, ct);
            if (linkedCustomer is not null)
            {
                officeCode = NormalizeOfficeCode(linkedCustomer.ResponsibleOfficeCode, session.OfficeCode);
                officeCode = RentalScopeNormalizer.ResolveResponsibleOfficeCode(
                    asset.TenantCode,
                    asset.OfficeCode,
                    asset.ManagementCompanyCode,
                    officeCode,
                    session.OfficeCode);
                ApplyAssetOfficeScope(asset, officeCode);
                var normalizedLinkedCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(linkedCustomer.NameOriginal);
                asset.CustomerName = normalizedLinkedCustomerName;
                asset.CurrentCustomerName = normalizedLinkedCustomerName;
                if (!CanManageAllAssetScope(session) &&
                    !string.Equals(officeCode, GetDefaultAssetOfficeCode(session), StringComparison.OrdinalIgnoreCase))
                {
                    return LocalMutationResult.Denied("일반 사용자는 본인 담당지점 자산만 등록/수정할 수 있습니다.");
                }

                if (!CanEditAssetScope(officeCode, session))
                    return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산을 저장할 수 없습니다.");
            }

            if (!asset.BillingProfileId.HasValue && asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
                asset.BillingProfileId = await FindMatchingBillingProfileIdAsync(asset, ct);

            var baseAssetKey = BuildAssetKey(asset.ManagementCompanyCode, asset.ManagementNumber, asset.ManagementId, asset.MachineNumber, asset.CustomerName, asset.ItemName);
            asset.AssetKey = baseAssetKey;
            asset.IsDeleted = false;

            var duplicate = await _db.RentalAssets.IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id != asset.Id && current.AssetKey == asset.AssetKey, ct);
            if (duplicate is not null && existing is not null)
            {
                asset.AssetKey = BuildLegacyCollisionAssetKey(baseAssetKey, existing.Id);
                duplicate = await _db.RentalAssets.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(current => current.Id != asset.Id && current.AssetKey == asset.AssetKey, ct);
            }

            if (duplicate is not null)
                return LocalMutationResult.Denied("같은 렌탈 자산이 이미 존재합니다.");

            var now = DateTime.UtcNow;
            await LocalEntityConcurrencyGuard.TryRebaseCandidateRevisionFromAcknowledgedLocalMutationAsync(_db, asset, existing, ct);
            if (!LocalEntityConcurrencyGuard.TryPrepareForSave(asset, existing, "렌탈 자산", now, out var conflictMessage))
                return LocalMutationResult.Conflict(conflictMessage);

            if (existing is null)
            {
                asset.Id = asset.Id == Guid.Empty ? Guid.NewGuid() : asset.Id;
                _db.RentalAssets.Add(asset);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(asset);
            }

            await _db.SaveChangesAsync(ct);
            await SyncLinkedBillingProfileMonthlyFeeFromAssetAsync(asset.Id, ct);
            await RefreshLocalRentalAssetAssignmentHistoriesAsync([asset.Id], DateTime.UtcNow, "자산 저장", ct);
            return LocalMutationResult.Ok(asset.Id, "렌탈 자산을 저장했습니다.");
        }
        finally
        {
            AssetSaveLock.Release();
        }
    }

    public async Task<IReadOnlyList<LocalRentalAsset>> GetRentalEquipmentReplacementCandidatesAsync(
        Guid currentAssetId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (currentAssetId == Guid.Empty)
            return Array.Empty<LocalRentalAsset>();

        var candidates = await ApplySharedAssetViewScope(_db.RentalAssets.AsNoTracking(), session)
            .Where(asset => asset.Id != currentAssetId && !asset.IsDeleted)
            .Where(asset => !asset.BillingProfileId.HasValue && !asset.CustomerId.HasValue)
            .Where(asset =>
                (asset.CurrentCustomerName ?? string.Empty).Replace("\t", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim() == string.Empty &&
                (asset.CustomerName ?? string.Empty).Replace("\t", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim() == string.Empty &&
                (asset.InstallLocation ?? string.Empty).Replace("\t", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim() == string.Empty &&
                (asset.InstallSiteName ?? string.Empty).Replace("\t", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim() == string.Empty)
            .Where(asset => RentalEquipmentReplacementCandidateStatusValues.Contains(
                (asset.AssetStatus ?? string.Empty).Replace("\t", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim()))
            .ToListAsync(ct);

        return candidates
            .Where(IsRentalEquipmentReplacementCandidate)
            .Where(asset => CanEditAssetScope(ResolveAssetResponsibleOfficeCodeForPermission(asset, session), session))
            .OrderBy(asset => asset.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.MachineNumber, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.Id)
            .ToList();
    }

    public async Task<LocalMutationResult> ReplaceRentalEquipmentAsync(
        RentalEquipmentReplacementRequest request,
        SessionState session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.OriginalAssetId == Guid.Empty || request.ReplacementAssetId == Guid.Empty)
            return LocalMutationResult.Missing("렌탈 장비 교체 대상 장비를 선택하세요.");
        if (request.OriginalAssetId == request.ReplacementAssetId)
            return LocalMutationResult.Denied("기존 장비와 새 장비가 같습니다. 다른 장비를 선택하세요.");

        await AssetSaveLock.WaitAsync(ct);
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var original = await _db.RentalAssets
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(asset => asset.Id == request.OriginalAssetId && !asset.IsDeleted, ct);
            if (original is null)
                return LocalMutationResult.Missing("기존 렌탈 장비를 찾을 수 없습니다.");

            var replacement = await _db.RentalAssets
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(asset => asset.Id == request.ReplacementAssetId && !asset.IsDeleted, ct);
            if (replacement is null)
                return LocalMutationResult.Missing("새로 연결할 렌탈 장비를 찾을 수 없습니다.");

            var originalOfficeCode = ResolveAssetResponsibleOfficeCodeForPermission(original, session);
            var replacementOfficeCode = ResolveAssetResponsibleOfficeCodeForPermission(replacement, session);
            if (!CanEditAssetScope(originalOfficeCode, session) || !CanEditAssetScope(replacementOfficeCode, session))
                return LocalMutationResult.Denied("기존 장비와 새 장비를 모두 수정할 권한이 있어야 렌탈 장비 교체를 진행할 수 있습니다.");

            if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(original, request.OriginalAssetRevision, "렌탈 자산", out var originalConflictMessage))
                return LocalMutationResult.Conflict(originalConflictMessage);
            if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(replacement, request.ReplacementAssetRevision, "렌탈 자산", out var replacementConflictMessage))
                return LocalMutationResult.Conflict(replacementConflictMessage);

            if (!HasActiveRentalEquipmentAssignment(original))
                return LocalMutationResult.Denied("기존 장비에 거래처/청구 연결이 없어 렌탈 장비 교체를 진행할 수 없습니다.");
            if (!IsRentalEquipmentReplacementCandidate(replacement))
                return LocalMutationResult.Denied("새 장비는 거래처/청구 연결이 없는 창고·점검중 장비만 선택할 수 있습니다.");

            var replacementDate = request.ReplacementDate == default
                ? DateOnly.FromDateTime(DateTime.Today)
                : request.ReplacementDate;
            var replacementUtc = CreateUtcDate(replacementDate);
            var now = DateTime.UtcNow;
            var changeReason = RentalCatalogValueNormalizer.NormalizeDisplayText(request.ChangeReason);
            if (string.IsNullOrWhiteSpace(changeReason))
                changeReason = "렌탈 장비 교체";

            var originalNextStatus = ResolveAssetStatus(
                string.IsNullOrWhiteSpace(request.OriginalAssetNextStatus)
                    ? "창고"
                    : request.OriginalAssetNextStatus,
                "창고",
                null);

            var previousBillingProfileId = original.BillingProfileId;
            LocalRentalBillingProfile? profile = null;
            if (previousBillingProfileId.HasValue && previousBillingProfileId.Value != Guid.Empty)
            {
                profile = await _db.RentalBillingProfiles
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(current => current.Id == previousBillingProfileId.Value && !current.IsDeleted, ct);
                if (profile is null)
                    return LocalMutationResult.Missing("기존 장비의 청구 프로필을 찾을 수 없습니다. 청구 연결을 먼저 정리한 뒤 렌탈 장비 교체를 다시 시도하세요.");

                var profileOfficeCode = NormalizeOfficeCode(
                    string.IsNullOrWhiteSpace(profile.ResponsibleOfficeCode)
                        ? profile.ManagementCompanyCode
                        : profile.ResponsibleOfficeCode,
                    originalOfficeCode);
                if (!CanEditRental(profileOfficeCode, session))
                    return LocalMutationResult.Denied("연결된 렌탈 청구 프로필을 수정할 권한이 없어 렌탈 장비 교체를 진행할 수 없습니다.");
            }

            var carriedCustomerId = original.CustomerId ?? profile?.CustomerId;
            var carriedCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
                original.CurrentCustomerName,
                original.CustomerName,
                profile?.CustomerName));
            var carriedInstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
                original.InstallLocation,
                original.InstallSiteName,
                profile?.InstallSiteName));
            var carriedInstallSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
                original.InstallSiteName,
                profile?.InstallSiteName,
                carriedInstallLocation,
                carriedCustomerName));
            var carriedBillingProfileId = original.BillingProfileId;
            var carriedMonthlyFee = Math.Max(0m, original.MonthlyFee);
            var carriedDepositText = (original.DepositText ?? string.Empty).Trim();
            var carriedContractMonths = original.ContractMonths;
            var carriedContractDate = original.ContractDate ?? profile?.ContractDate;
            var carriedRentalEndDate = original.RentalEndDate ?? profile?.ContractEndDate;
            var carriedFreeSupplyItems = original.FreeSupplyItems;
            var carriedPaidSupplyItems = original.PaidSupplyItems;
            var originalLabel = BuildRentalEquipmentReplacementAssetLabel(original);
            var replacementLabel = BuildRentalEquipmentReplacementAssetLabel(replacement);

            await ApplyAssignmentClearedSnapshotAsync(original, previousBillingProfileId, replacementUtc, ct);
            original.CustomerName = string.Empty;
            original.CurrentCustomerName = string.Empty;
            original.InstallLocation = string.Empty;
            original.InstallSiteName = string.Empty;
            original.AssetStatus = originalNextStatus;
            original.CurrentLocation = originalNextStatus;
            original.RentalEndDate = replacementDate;
            if (string.Equals(originalNextStatus, "폐기", StringComparison.OrdinalIgnoreCase))
                original.DisposalDate ??= replacementDate;
            original.BillingEligibilityStatus = BillingEligibilityExcluded;
            original.BillingExclusionReason = RentalAssetStatusRules.BuildAutoExclusionReason(originalNextStatus);
            original.AssetKey = BuildAssetKey(
                original.ManagementCompanyCode,
                original.ManagementNumber,
                original.ManagementId,
                original.MachineNumber,
                original.CustomerName,
                original.ItemName);
            await EnsureUniqueRentalAssetKeyAsync(original, ct);
            original.Notes = AppendRentalEquipmentReplacementNote(
                original.Notes,
                $"{replacementDate:yyyy-MM-dd} 렌탈 장비 교체로 {replacementLabel} 장비에 임대 연결을 승계했습니다.");
            original.IsDirty = true;
            original.UpdatedAtUtc = now;

            var targetResponsibleOfficeCode = NormalizeOfficeCode(FirstNonEmpty(
                profile?.ResponsibleOfficeCode,
                original.ResponsibleOfficeCode,
                replacement.ResponsibleOfficeCode),
                session.OfficeCode);
            replacement.ResponsibleOfficeCode = targetResponsibleOfficeCode;
            replacement.ManagementCompanyCode = ResolveLinkedAssetManagementCompanyCode(replacement, targetResponsibleOfficeCode);
            replacement.OfficeCode = replacement.ManagementCompanyCode;
            replacement.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                profile?.TenantCode ?? replacement.TenantCode,
                replacement.OfficeCode,
                original.TenantCode,
                targetResponsibleOfficeCode);
            replacement.BillingProfileId = carriedBillingProfileId;
            replacement.CustomerId = carriedCustomerId;
            replacement.CustomerName = carriedCustomerName;
            replacement.CurrentCustomerName = carriedCustomerName;
            replacement.InstallLocation = carriedInstallLocation;
            replacement.InstallSiteName = carriedInstallSiteName;
            replacement.DepositText = carriedDepositText;
            replacement.MonthlyFee = carriedMonthlyFee;
            replacement.ContractMonths = carriedContractMonths;
            replacement.ContractDate = carriedContractDate;
            replacement.InstallDate = replacementDate;
            replacement.ContractStartDate = replacementDate;
            replacement.RentalEndDate = carriedRentalEndDate;
            replacement.FreeSupplyItems = carriedFreeSupplyItems;
            replacement.PaidSupplyItems = carriedPaidSupplyItems;
            replacement.AssetStatus = "임대진행중";
            replacement.CurrentLocation = string.IsNullOrWhiteSpace(original.CurrentLocation) || string.Equals(original.CurrentLocation, originalNextStatus, StringComparison.OrdinalIgnoreCase)
                ? "렌탈"
                : original.CurrentLocation;
            replacement.BillingEligibilityStatus = BillingEligibilityTarget;
            replacement.BillingExclusionReason = string.Empty;
            replacement.AssetKey = BuildAssetKey(
                replacement.ManagementCompanyCode,
                replacement.ManagementNumber,
                replacement.ManagementId,
                replacement.MachineNumber,
                replacement.CustomerName,
                replacement.ItemName);
            await EnsureUniqueRentalAssetKeyAsync(replacement, ct);
            replacement.Notes = AppendRentalEquipmentReplacementNote(
                replacement.Notes,
                $"{replacementDate:yyyy-MM-dd} 렌탈 장비 교체: {originalLabel} 장비의 거래처/청구 연결을 승계했습니다.");
            replacement.IsDirty = true;
            replacement.UpdatedAtUtc = now;

            if (profile is not null && ReplaceBillingProfileTemplateAsset(profile, original.Id, replacement.Id))
            {
                profile.IsDirty = true;
                profile.UpdatedAtUtc = now;
            }

            await _db.SaveChangesAsync(ct);
            if (profile is not null)
                await SyncLinkedBillingProfileMonthlyFeeFromAssetAsync(replacement.Id, ct);
            await RefreshLocalRentalAssetAssignmentHistoriesAsync([original.Id, replacement.Id], replacementUtc, changeReason, ct);
            await tx.CommitAsync(ct);

            return LocalMutationResult.Ok(
                original.Id,
                $"렌탈 장비 교체를 완료했습니다. {originalLabel} → {replacementLabel}",
                replacement.Id);
        }
        finally
        {
            AssetSaveLock.Release();
        }
    }

    private static string ResolveAssetResponsibleOfficeCodeForPermission(LocalRentalAsset asset, SessionState session)
        => RentalScopeNormalizer.ResolveResponsibleOfficeCode(
            asset.TenantCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode,
            asset.ResponsibleOfficeCode,
            session.OfficeCode);

    private static bool HasActiveRentalEquipmentAssignment(LocalRentalAsset asset)
    {
        if (asset.BillingProfileId.HasValue || asset.CustomerId.HasValue)
            return true;

        return !string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ||
               !string.IsNullOrWhiteSpace(asset.CustomerName) ||
               !string.IsNullOrWhiteSpace(asset.InstallLocation) ||
               !string.IsNullOrWhiteSpace(asset.InstallSiteName);
    }

    private static bool IsRentalEquipmentReplacementCandidate(LocalRentalAsset asset)
    {
        if (asset.IsDeleted ||
            asset.BillingProfileId.HasValue ||
            asset.CustomerId.HasValue ||
            !string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ||
            !string.IsNullOrWhiteSpace(asset.CustomerName) ||
            !string.IsNullOrWhiteSpace(asset.InstallLocation) ||
            !string.IsNullOrWhiteSpace(asset.InstallSiteName))
        {
            return false;
        }

        var status = (asset.AssetStatus ?? string.Empty).Trim();
        var normalizedStatus = RentalAssetStatusRules.Normalize(status);
        return string.IsNullOrWhiteSpace(normalizedStatus) ||
               string.Equals(normalizedStatus, "창고", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "점검중", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "설치처 불명", StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureUniqueRentalAssetKeyAsync(LocalRentalAsset asset, CancellationToken ct)
    {
        var candidate = string.IsNullOrWhiteSpace(asset.AssetKey)
            ? BuildAssetKey(
                asset.ManagementCompanyCode,
                asset.ManagementNumber,
                asset.ManagementId,
                asset.MachineNumber,
                asset.CustomerName,
                asset.ItemName)
            : asset.AssetKey;

        var duplicate = await _db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(current => current.Id != asset.Id && current.AssetKey == candidate, ct);
        if (duplicate)
            candidate = BuildLegacyCollisionAssetKey(candidate, asset.Id);

        asset.AssetKey = candidate;
    }

    private bool ReplaceBillingProfileTemplateAsset(
        LocalRentalBillingProfile profile,
        Guid originalAssetId,
        Guid replacementAssetId)
    {
        var templateItems = GetBillingTemplateItems(profile, Array.Empty<LocalRentalAsset>());
        var changed = false;
        foreach (var item in templateItems)
        {
            var includedAssetIds = (item.IncludedAssetIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty)
                .ToList();
            if (!includedAssetIds.Contains(originalAssetId))
                continue;

            var replacedIds = includedAssetIds
                .Select(id => id == originalAssetId ? replacementAssetId : id)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            if (!replacedIds.SequenceEqual(item.IncludedAssetIds ?? new List<Guid>()))
            {
                item.IncludedAssetIds = replacedIds;
                changed = true;
            }

            if (item.RepresentativeAssetId == originalAssetId)
            {
                item.RepresentativeAssetId = replacementAssetId;
                changed = true;
            }
        }

        if (!changed)
            return false;

        profile.BillingTemplateJson = SerializeBillingTemplateItems(templateItems);
        profile.MonthlyAmount = templateItems.Sum(ResolveTemplateMonthlyAmount);
        profile.ItemName = BuildProfileItemName(profile, templateItems);
        return true;
    }

    private static string BuildRentalEquipmentReplacementAssetLabel(LocalRentalAsset asset)
    {
        var identifier = FirstNonEmpty(asset.ManagementNumber, asset.ManagementId, asset.MachineNumber);
        var itemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
        if (!string.IsNullOrWhiteSpace(identifier) && !string.IsNullOrWhiteSpace(itemName))
            return $"{identifier} / {itemName}";
        return FirstNonEmpty(identifier, itemName, asset.Id.ToString("D"));
    }

    private static string AppendRentalEquipmentReplacementNote(string? existingNotes, string note)
    {
        var current = (existingNotes ?? string.Empty).Trim();
        var normalizedNote = RentalCatalogValueNormalizer.NormalizeDisplayText(note);
        if (string.IsNullOrWhiteSpace(normalizedNote))
            return current;
        if (string.IsNullOrWhiteSpace(current))
            return normalizedNote;
        return $"{current}{Environment.NewLine}{normalizedNote}";
    }

    private static void ApplyAssetOfficeScope(LocalRentalAsset asset, string officeCode)
    {
        var normalizedResponsibleOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet);
        var ownerOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
            null,
            normalizedResponsibleOfficeCode,
            DomainConstants.OfficeUsenet);
        asset.ManagementCompanyCode = ownerOfficeCode;
        asset.ResponsibleOfficeCode = normalizedResponsibleOfficeCode;
        asset.OfficeCode = ownerOfficeCode;
        asset.TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(ownerOfficeCode);
    }

    private async Task RefreshLocalRentalAssetAssignmentHistoriesAsync(
        IEnumerable<Guid> assetIds,
        DateTime nowUtc,
        string reason,
        CancellationToken ct)
    {
        var targetAssetIds = assetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (targetAssetIds.Count == 0)
            return;

        nowUtc = NormalizeHistoryUtc(nowUtc);
        var assets = await LoadRentalAssetsByIdsAsync(
            targetAssetIds,
            ignoreQueryFilters: true,
            asNoTracking: false,
            excludeDeleted: false,
            ct);
        if (assets.Count == 0)
            return;

        var histories = await LoadRentalAssignmentHistoriesByAssetIdsAsync(targetAssetIds, ct);
        var currentByAssetId = histories
            .Where(history => history.IsCurrent)
            .GroupBy(history => history.AssetId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(history => history.LinkedAtUtc).ToList());
        var hasChanges = false;

        foreach (var asset in assets)
        {
            currentByAssetId.TryGetValue(asset.Id, out var currentRows);
            currentRows ??= [];

            var desiredCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(
                string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ? asset.CustomerName : asset.CurrentCustomerName);
            var desiredInstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(
                string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation);
            var hasDesiredAssignment = !asset.IsDeleted && HasCurrentRentalAssignment(asset, desiredCustomerName, desiredInstallLocation);

            var matchingCurrent = currentRows.FirstOrDefault(history =>
                history.BillingProfileId == asset.BillingProfileId &&
                history.CustomerId == asset.CustomerId &&
                string.Equals(history.CustomerName, desiredCustomerName, StringComparison.Ordinal) &&
                string.Equals(history.InstallLocation, desiredInstallLocation, StringComparison.Ordinal) &&
                string.Equals(history.TenantCode, asset.TenantCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(history.ResponsibleOfficeCode, asset.ResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase));

            if (!hasDesiredAssignment)
            {
                foreach (var stale in currentRows)
                    hasChanges |= CloseAssignmentHistory(stale, asset.LastAssignmentClearedAtUtc ?? nowUtc, reason, nowUtc, asset);

                if (currentRows.Count == 0)
                    hasChanges |= await EnsureEndedHistoryFromClearedSnapshotAsync(asset, histories, nowUtc, reason, ct);

                continue;
            }

            foreach (var stale in currentRows.Where(history => matchingCurrent is null || history.Id != matchingCurrent.Id))
                hasChanges |= CloseAssignmentHistory(stale, nowUtc, "재임대/연결 변경", nowUtc, asset);

            if (matchingCurrent is not null)
            {
                hasChanges |= await PopulateAssignmentHistorySnapshotAsync(matchingCurrent, asset, desiredCustomerName, desiredInstallLocation, reason, nowUtc, ct);
                continue;
            }

            var linkedAtUtc = ResolveAssignmentLinkedAtUtc(asset, nowUtc);
            var historyId = SyncIdentityGenerator.CreateRentalAssetAssignmentHistoryId(
                asset.Id,
                linkedAtUtc,
                asset.BillingProfileId,
                asset.CustomerId,
                desiredCustomerName,
                desiredInstallLocation);
            var existingHistory = await FindRentalAssetAssignmentHistoryByIdAsync(historyId, histories, ct);
            if (existingHistory is not null)
            {
                hasChanges |= PrepareCurrentAssignmentHistory(
                    existingHistory,
                    asset,
                    desiredCustomerName,
                    desiredInstallLocation,
                    linkedAtUtc,
                    reason,
                    nowUtc);
                hasChanges |= await PopulateAssignmentHistorySnapshotAsync(
                    existingHistory,
                    asset,
                    desiredCustomerName,
                    desiredInstallLocation,
                    reason,
                    nowUtc,
                    ct);
                continue;
            }

            var newHistory = new LocalRentalAssetAssignmentHistory
            {
                Id = historyId == Guid.Empty ? Guid.NewGuid() : historyId,
                AssetId = asset.Id,
                BillingProfileId = asset.BillingProfileId,
                CustomerId = asset.CustomerId,
                TenantCode = asset.TenantCode,
                ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
                CustomerName = desiredCustomerName,
                InstallLocation = desiredInstallLocation,
                IsCurrent = true,
                LinkedAtUtc = linkedAtUtc,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
                ChangeReason = reason
            };
            await PopulateAssignmentHistorySnapshotAsync(newHistory, asset, desiredCustomerName, desiredInstallLocation, reason, nowUtc, ct);
            _db.RentalAssetAssignmentHistories.Add(newHistory);
            histories.Add(newHistory);
            hasChanges = true;
        }

        if (hasChanges)
            await _db.SaveChangesAsync(ct);
    }

    private static bool HasCurrentRentalAssignment(LocalRentalAsset asset, string customerName, string installLocation)
    {
        if (asset.BillingProfileId.HasValue || asset.CustomerId.HasValue)
            return true;
        if (asset.LastAssignmentClearedAtUtc.HasValue)
            return false;
        return !string.IsNullOrWhiteSpace(customerName) || !string.IsNullOrWhiteSpace(installLocation);
    }

    private async Task<LocalRentalAssetAssignmentHistory?> FindRentalAssetAssignmentHistoryByIdAsync(
        Guid historyId,
        List<LocalRentalAssetAssignmentHistory> histories,
        CancellationToken ct)
    {
        if (historyId == Guid.Empty)
            return null;

        var existing = histories.FirstOrDefault(history => history.Id == historyId);
        if (existing is not null)
            return existing;

        existing = _db.RentalAssetAssignmentHistories.Local.FirstOrDefault(history => history.Id == historyId);
        if (existing is not null)
        {
            histories.Add(existing);
            return existing;
        }

        existing = await _db.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(history => history.Id == historyId, ct);
        if (existing is not null)
            histories.Add(existing);
        return existing;
    }

    private static bool PrepareCurrentAssignmentHistory(
        LocalRentalAssetAssignmentHistory history,
        LocalRentalAsset asset,
        string customerName,
        string installLocation,
        DateTime linkedAtUtc,
        string reason,
        DateTime nowUtc)
    {
        var changed = false;
        changed |= SetIfDifferent(value => history.AssetId = value, history.AssetId, asset.Id);
        changed |= SetIfDifferent(value => history.BillingProfileId = value, history.BillingProfileId, asset.BillingProfileId);
        changed |= SetIfDifferent(value => history.CustomerId = value, history.CustomerId, asset.CustomerId);
        changed |= SetIfDifferent(value => history.TenantCode = value, history.TenantCode, asset.TenantCode);
        changed |= SetIfDifferent(value => history.ResponsibleOfficeCode = value, history.ResponsibleOfficeCode, asset.ResponsibleOfficeCode);
        changed |= SetIfDifferent(value => history.CustomerName = value, history.CustomerName, customerName);
        changed |= SetIfDifferent(value => history.InstallLocation = value, history.InstallLocation, installLocation);
        changed |= SetIfDifferent(value => history.ChangeReason = value, history.ChangeReason, reason);
        changed |= SetIfDifferent(value => history.IsCurrent = value, history.IsCurrent, true);
        changed |= SetIfDifferent(value => history.LinkedAtUtc = value, history.LinkedAtUtc, linkedAtUtc);
        changed |= SetIfDifferent<DateTime?>(value => history.UnlinkedAtUtc = value, history.UnlinkedAtUtc, null);
        changed |= SetIfDifferent(value => history.IsDeleted = value, history.IsDeleted, false);
        if (changed)
        {
            history.IsDirty = true;
            history.UpdatedAtUtc = nowUtc;
        }

        return changed;
    }

    private async Task<bool> PopulateAssignmentHistorySnapshotAsync(
        LocalRentalAssetAssignmentHistory history,
        LocalRentalAsset asset,
        string customerName,
        string installLocation,
        string reason,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var changed = false;
        changed |= SetIfDifferent(value => history.AssetId = value, history.AssetId, asset.Id);
        changed |= SetIfDifferent(value => history.BillingProfileId = value, history.BillingProfileId, asset.BillingProfileId);
        changed |= SetIfDifferent(value => history.CustomerId = value, history.CustomerId, asset.CustomerId);
        changed |= SetIfDifferent(value => history.TenantCode = value, history.TenantCode, asset.TenantCode);
        changed |= SetIfDifferent(value => history.ResponsibleOfficeCode = value, history.ResponsibleOfficeCode, asset.ResponsibleOfficeCode);
        changed |= SetIfDifferent(value => history.CustomerName = value, history.CustomerName, customerName);
        changed |= SetIfDifferent(value => history.InstallLocation = value, history.InstallLocation, installLocation);
        changed |= SetIfDifferent(value => history.BillingProfileDisplay = value, history.BillingProfileDisplay, await ResolveLastBillingProfileDisplayAsync(asset.BillingProfileId, asset, ct));
        changed |= SetIfDifferent(value => history.ItemName = value, history.ItemName, asset.ItemName);
        changed |= SetIfDifferent(value => history.MachineNumber = value, history.MachineNumber, asset.MachineNumber);
        changed |= SetIfDifferent(value => history.ManagementNumber = value, history.ManagementNumber, asset.ManagementNumber);
        changed |= SetIfDifferent(value => history.MonthlyFee = value, history.MonthlyFee, Math.Max(0m, asset.MonthlyFee));
        changed |= SetIfDifferent(value => history.ContractStartDate = value, history.ContractStartDate, asset.ContractStartDate ?? asset.InstallDate);
        changed |= SetIfDifferent(value => history.ContractEndDate = value, history.ContractEndDate, asset.RentalEndDate);
        changed |= SetIfDifferent(value => history.ChangeReason = value, history.ChangeReason, reason);
        if (changed)
        {
            history.IsDirty = true;
            history.UpdatedAtUtc = nowUtc;
        }
        return changed;
    }

    private async Task<bool> EnsureEndedHistoryFromClearedSnapshotAsync(
        LocalRentalAsset asset,
        List<LocalRentalAssetAssignmentHistory> histories,
        DateTime nowUtc,
        string reason,
        CancellationToken ct)
    {
        var unlinkedAtUtc = NormalizeHistoryUtc(asset.LastAssignmentClearedAtUtc ?? nowUtc);
        var customerName = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
            asset.LastCustomerName,
            asset.CurrentCustomerName,
            asset.CustomerName));
        var installLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
            asset.LastInstallLocation,
            asset.InstallLocation,
            asset.InstallSiteName));
        var billingProfileId = asset.LastBillingProfileId ?? asset.BillingProfileId;
        if (string.IsNullOrWhiteSpace(customerName) &&
            string.IsNullOrWhiteSpace(installLocation) &&
            !billingProfileId.HasValue)
        {
            return false;
        }

        var exists = histories.Any(history =>
            history.AssetId == asset.Id &&
            !history.IsCurrent &&
            history.BillingProfileId == billingProfileId &&
            string.Equals(history.CustomerName, customerName, StringComparison.Ordinal) &&
            string.Equals(history.InstallLocation, installLocation, StringComparison.Ordinal) &&
            NullableHistoryUtcEquals(history.UnlinkedAtUtc, unlinkedAtUtc));
        if (exists)
            return false;

        var linkedAtUtc = ResolveAssignmentLinkedAtUtc(asset, unlinkedAtUtc);
        if (linkedAtUtc >= unlinkedAtUtc)
            linkedAtUtc = NormalizeHistoryUtc(asset.CreatedAtUtc == default ? unlinkedAtUtc.AddMinutes(-1) : asset.CreatedAtUtc);

        var historyId = SyncIdentityGenerator.CreateRentalAssetAssignmentHistoryId(
            asset.Id,
            linkedAtUtc,
            billingProfileId,
            asset.CustomerId,
            customerName,
            installLocation);
        var existingHistory = await FindRentalAssetAssignmentHistoryByIdAsync(historyId, histories, ct);
        if (existingHistory is not null)
        {
            return await PopulateEndedAssignmentHistoryFromClearedSnapshotAsync(
                existingHistory,
                asset,
                billingProfileId,
                customerName,
                installLocation,
                linkedAtUtc,
                unlinkedAtUtc,
                reason,
                nowUtc,
                ct);
        }

        var history = new LocalRentalAssetAssignmentHistory
        {
            Id = historyId == Guid.Empty ? Guid.NewGuid() : historyId,
            AssetId = asset.Id,
            BillingProfileId = billingProfileId,
            CustomerId = asset.CustomerId,
            TenantCode = asset.TenantCode,
            ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            CustomerName = customerName,
            InstallLocation = installLocation,
            BillingProfileDisplay = string.IsNullOrWhiteSpace(asset.LastBillingProfileDisplay)
                ? await ResolveLastBillingProfileDisplayAsync(billingProfileId, asset, ct)
                : asset.LastBillingProfileDisplay,
            ItemName = asset.ItemName,
            MachineNumber = asset.MachineNumber,
            ManagementNumber = asset.ManagementNumber,
            MonthlyFee = Math.Max(0m, asset.MonthlyFee),
            ContractStartDate = asset.ContractStartDate ?? asset.InstallDate,
            ContractEndDate = asset.RentalEndDate,
            ChangeReason = reason,
            IsCurrent = false,
            LinkedAtUtc = linkedAtUtc,
            UnlinkedAtUtc = unlinkedAtUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
        _db.RentalAssetAssignmentHistories.Add(history);
        histories.Add(history);
        return true;
    }

    private async Task<bool> PopulateEndedAssignmentHistoryFromClearedSnapshotAsync(
        LocalRentalAssetAssignmentHistory history,
        LocalRentalAsset asset,
        Guid? billingProfileId,
        string customerName,
        string installLocation,
        DateTime linkedAtUtc,
        DateTime unlinkedAtUtc,
        string reason,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var billingProfileDisplay = string.IsNullOrWhiteSpace(asset.LastBillingProfileDisplay)
            ? await ResolveLastBillingProfileDisplayAsync(billingProfileId, asset, ct)
            : asset.LastBillingProfileDisplay;

        var changed = false;
        changed |= SetIfDifferent(value => history.AssetId = value, history.AssetId, asset.Id);
        changed |= SetIfDifferent(value => history.BillingProfileId = value, history.BillingProfileId, billingProfileId);
        changed |= SetIfDifferent(value => history.CustomerId = value, history.CustomerId, asset.CustomerId);
        changed |= SetIfDifferent(value => history.TenantCode = value, history.TenantCode, asset.TenantCode);
        changed |= SetIfDifferent(value => history.ResponsibleOfficeCode = value, history.ResponsibleOfficeCode, asset.ResponsibleOfficeCode);
        changed |= SetIfDifferent(value => history.CustomerName = value, history.CustomerName, customerName);
        changed |= SetIfDifferent(value => history.InstallLocation = value, history.InstallLocation, installLocation);
        changed |= SetIfDifferent(value => history.BillingProfileDisplay = value, history.BillingProfileDisplay, billingProfileDisplay);
        changed |= SetIfDifferent(value => history.ItemName = value, history.ItemName, asset.ItemName);
        changed |= SetIfDifferent(value => history.MachineNumber = value, history.MachineNumber, asset.MachineNumber);
        changed |= SetIfDifferent(value => history.ManagementNumber = value, history.ManagementNumber, asset.ManagementNumber);
        changed |= SetIfDifferent(value => history.MonthlyFee = value, history.MonthlyFee, Math.Max(0m, asset.MonthlyFee));
        changed |= SetIfDifferent(value => history.ContractStartDate = value, history.ContractStartDate, asset.ContractStartDate ?? asset.InstallDate);
        changed |= SetIfDifferent(value => history.ContractEndDate = value, history.ContractEndDate, asset.RentalEndDate);
        changed |= SetIfDifferent(value => history.ChangeReason = value, history.ChangeReason, reason);
        changed |= SetIfDifferent(value => history.IsCurrent = value, history.IsCurrent, false);
        changed |= SetIfDifferent(value => history.LinkedAtUtc = value, history.LinkedAtUtc, linkedAtUtc);
        changed |= SetIfDifferent<DateTime?>(value => history.UnlinkedAtUtc = value, history.UnlinkedAtUtc, unlinkedAtUtc);
        changed |= SetIfDifferent(value => history.IsDeleted = value, history.IsDeleted, false);
        if (changed)
        {
            history.IsDirty = true;
            history.UpdatedAtUtc = nowUtc;
        }

        return changed;
    }

    private static bool CloseAssignmentHistory(
        LocalRentalAssetAssignmentHistory history,
        DateTime unlinkedAtUtc,
        string reason,
        DateTime nowUtc,
        LocalRentalAsset asset)
    {
        var normalizedUnlinkedAtUtc = NormalizeHistoryUtc(unlinkedAtUtc);
        var changed = false;
        changed |= SetIfDifferent(value => history.IsCurrent = value, history.IsCurrent, false);
        changed |= SetIfDifferent(value => history.UnlinkedAtUtc = value, history.UnlinkedAtUtc, normalizedUnlinkedAtUtc);
        changed |= SetIfDifferent(value => history.ChangeReason = value, history.ChangeReason, reason);
        changed |= SetIfDifferent(value => history.ItemName = value, history.ItemName, asset.ItemName);
        changed |= SetIfDifferent(value => history.MachineNumber = value, history.MachineNumber, asset.MachineNumber);
        changed |= SetIfDifferent(value => history.ManagementNumber = value, history.ManagementNumber, asset.ManagementNumber);
        if (changed)
        {
            history.IsDirty = true;
            history.UpdatedAtUtc = nowUtc;
        }
        return changed;
    }

    private async Task ApplyAssignmentClearedSnapshotAsync(
        LocalRentalAsset asset,
        Guid? previousBillingProfileId,
        DateTime clearedAtUtc,
        CancellationToken ct)
    {
        var snapshotCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
            asset.CurrentCustomerName,
            asset.CustomerName,
            asset.LastCustomerName));
        var snapshotInstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
            asset.InstallLocation,
            asset.InstallSiteName,
            asset.LastInstallLocation));
        var snapshotBillingProfileId = previousBillingProfileId ?? asset.BillingProfileId ?? asset.LastBillingProfileId;

        if (!string.IsNullOrWhiteSpace(snapshotCustomerName) ||
            !string.IsNullOrWhiteSpace(snapshotInstallLocation) ||
            snapshotBillingProfileId.HasValue)
        {
            asset.LastCustomerName = snapshotCustomerName;
            asset.LastInstallLocation = snapshotInstallLocation;
            asset.LastBillingProfileId = snapshotBillingProfileId;
            asset.LastBillingProfileDisplay = await ResolveLastBillingProfileDisplayAsync(snapshotBillingProfileId, asset, ct);
            asset.LastAssignmentClearedAtUtc = NormalizeHistoryUtc(clearedAtUtc);
        }

        asset.BillingProfileId = null;
        asset.CustomerId = null;
    }

    private static DateTime ResolveAssignmentLinkedAtUtc(LocalRentalAsset asset, DateTime fallbackUtc)
    {
        if (asset.InstallDate.HasValue)
            return CreateUtcDate(asset.InstallDate.Value);
        if (asset.ContractStartDate.HasValue)
            return CreateUtcDate(asset.ContractStartDate.Value);
        if (asset.UpdatedAtUtc != default)
            return NormalizeHistoryUtc(asset.UpdatedAtUtc);
        if (asset.CreatedAtUtc != default)
            return NormalizeHistoryUtc(asset.CreatedAtUtc);
        return NormalizeHistoryUtc(fallbackUtc);
    }

    private static RentalAssetAssignmentHistoryViewItem BuildAssignmentHistoryViewItem(
        LocalRentalAssetAssignmentHistory history,
        LocalRentalAsset? asset,
        IReadOnlyDictionary<Guid, string> profileDisplayLookup)
    {
        var profileDisplay = string.Empty;
        if (history.BillingProfileId.HasValue)
            profileDisplayLookup.TryGetValue(history.BillingProfileId.Value, out profileDisplay);

        var billingProfileDisplay = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
            history.BillingProfileDisplay,
            profileDisplay,
            history.BillingProfileId?.ToString("D")));
        var linkedAtUtc = NormalizeHistoryUtc(history.LinkedAtUtc);
        var unlinkedAtUtc = history.UnlinkedAtUtc.HasValue ? NormalizeHistoryUtc(history.UnlinkedAtUtc.Value) : (DateTime?)null;
        var responsibleOfficeCode = string.IsNullOrWhiteSpace(history.ResponsibleOfficeCode)
            ? asset?.ResponsibleOfficeCode
            : history.ResponsibleOfficeCode;
        var isLinkedAtEstimated = IsEstimatedAssignmentHistoryStart(history, linkedAtUtc, unlinkedAtUtc);

        return new RentalAssetAssignmentHistoryViewItem
        {
            HistoryId = history.Id,
            AssetId = history.AssetId,
            IsCurrent = history.IsCurrent,
            IsLinkedAtEstimated = isLinkedAtEstimated,
            LinkedAtLocal = linkedAtUtc.ToLocalTime(),
            UnlinkedAtLocal = unlinkedAtUtc?.ToLocalTime(),
            CustomerName = FirstNonEmpty(history.CustomerName, asset?.CurrentCustomerName, asset?.CustomerName),
            InstallLocation = FirstNonEmpty(history.InstallLocation, asset?.InstallLocation, asset?.InstallSiteName),
            BillingProfileDisplay = billingProfileDisplay,
            ResponsibleOfficeName = OfficeCodeCatalog.GetOfficeDisplayName(responsibleOfficeCode ?? DomainConstants.OfficeUsenet),
            ItemName = FirstNonEmpty(history.ItemName, asset?.ItemName),
            MachineNumber = FirstNonEmpty(history.MachineNumber, asset?.MachineNumber),
            ManagementNumber = FirstNonEmpty(history.ManagementNumber, asset?.ManagementNumber),
            MonthlyFee = history.MonthlyFee > 0m ? history.MonthlyFee : Math.Max(0m, asset?.MonthlyFee ?? 0m),
            ChangeReason = history.ChangeReason
        };
    }

    private static bool IsEstimatedAssignmentHistoryStart(
        LocalRentalAssetAssignmentHistory history,
        DateTime linkedAtUtc,
        DateTime? unlinkedAtUtc)
    {
        if (history.IsCurrent || !unlinkedAtUtc.HasValue)
            return false;

        var elapsed = unlinkedAtUtc.Value - linkedAtUtc;
        if (elapsed <= TimeSpan.Zero || elapsed > TimeSpan.FromMinutes(1))
            return false;

        return (history.ChangeReason ?? string.Empty).Contains("회수이력", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAssignmentHistoryLogicalKey(LocalRentalAssetAssignmentHistory history)
    {
        var unlinked = history.UnlinkedAtUtc.HasValue
            ? NormalizeHistoryUtc(history.UnlinkedAtUtc.Value).ToString("O", CultureInfo.InvariantCulture)
            : string.Empty;
        return string.Join(
            "|",
            history.AssetId.ToString("D"),
            history.BillingProfileId?.ToString("D") ?? string.Empty,
            history.CustomerId?.ToString("D") ?? string.Empty,
            RentalCatalogValueNormalizer.NormalizeDisplayText(history.CustomerName),
            RentalCatalogValueNormalizer.NormalizeDisplayText(history.InstallLocation),
            NormalizeHistoryUtc(history.LinkedAtUtc).ToString("O", CultureInfo.InvariantCulture),
            unlinked,
            history.IsCurrent ? "1" : "0");
    }

    private static string BuildBillingProfileDisplay(LocalRentalBillingProfile? profile)
        => profile is null
            ? string.Empty
            : BuildBillingProfileDisplay(profile.CustomerName, profile.ItemName);

    private static string BuildBillingProfileDisplay(string? customerNameValue, string? itemNameValue)
    {
        var customerName = RentalCatalogValueNormalizer.NormalizeDisplayText(customerNameValue);
        var itemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(itemNameValue);
        if (!string.IsNullOrWhiteSpace(customerName) && !string.IsNullOrWhiteSpace(itemName))
            return $"{customerName} · {itemName}";
        return FirstNonEmpty(customerName, itemName);
    }

    private static bool SetIfDifferent<T>(Action<T> setter, T current, T next)
    {
        if (EqualityComparer<T>.Default.Equals(current, next))
            return false;
        setter(next);
        return true;
    }

    private static DateTime NormalizeHistoryUtc(DateTime value)
    {
        if (value == default)
            return DateTime.UtcNow;
        if (value.Kind == DateTimeKind.Utc)
            return value;
        if (value.Kind == DateTimeKind.Local)
            return value.ToUniversalTime();
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static DateTime ConvertLocalHistoryDateToUtc(DateTime value)
    {
        if (value == default)
            return DateTime.UtcNow;
        if (value.Kind == DateTimeKind.Utc)
            return value;
        if (value.Kind == DateTimeKind.Local)
            return value.ToUniversalTime();
        return DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
    }

    private static bool NullableHistoryUtcEquals(DateTime? current, DateTime expected)
        => current.HasValue && NormalizeHistoryUtc(current.Value) == NormalizeHistoryUtc(expected);

    private static DateTime CreateUtcDate(DateOnly date)
        => new(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc);

    public async Task<LocalMutationResult> DeleteAssetAsync(
        Guid assetId,
        SessionState session,
        long? expectedRevision = null,
        CancellationToken ct = default)
    {
        var asset = await _db.RentalAssets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == assetId, ct);
        if (asset is null)
            return LocalMutationResult.Missing("렌탈 자산을 찾을 수 없습니다.");
        if (!CanEditAssetScope(asset.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산을 삭제할 수 없습니다.");

        if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(asset, expectedRevision, "렌탈 자산", out var conflictMessage))
            return LocalMutationResult.Conflict(conflictMessage);

        asset.IsDeleted = true;
        asset.IsDirty = true;
        asset.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await RefreshLocalRentalAssetAssignmentHistoriesAsync([assetId], DateTime.UtcNow, "자산 삭제", ct);
        return LocalMutationResult.Ok(assetId, "렌탈 자산을 삭제했습니다.");
    }

    public Task<RentalCatalogRepairResult> RepairRentalCatalogLinksAsync(CancellationToken ct = default)
        => RepairRentalCatalogLinksAsync(assetIds: null, ct);

    public async Task<RentalCatalogRepairResult> RepairRentalCatalogLinksAsync(
        IReadOnlyCollection<Guid>? assetIds,
        CancellationToken ct = default)
    {
        await AssetSaveLock.WaitAsync(ct);
        try
        {
            var result = new RentalCatalogRepairResult();
            var activeItems = await GetActiveItemsAsync(ct);
            List<LocalRentalAsset> assets;
            if (assetIds is { Count: > 0 })
            {
                var candidateIds = assetIds
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();

                if (candidateIds.Count == 0)
                    return result;

                assets = await LoadRentalAssetsByIdsAsync(
                    candidateIds,
                    ignoreQueryFilters: true,
                    asNoTracking: false,
                    excludeDeleted: true,
                    ct);
            }
            else
            {
                assets = await _db.RentalAssets.IgnoreQueryFilters()
                    .Where(asset => !asset.IsDeleted)
                    .OrderBy(asset => asset.CustomerName)
                    .ThenBy(asset => asset.ManagementNumber)
                    .ToListAsync(ct);
            }

            assets = assets
                .OrderBy(asset => asset.CustomerName)
                .ThenBy(asset => asset.ManagementNumber)
                .ToList();

            result.ScannedAssetCount = assets.Count;

            var beforeSignatureByAssetId = new Dictionary<Guid, string>();
            var assetBaseKeyById = new Dictionary<Guid, string>();
            var displacedItemIds = new HashSet<Guid>();

            foreach (var asset in assets)
            {
                var beforeSignature = BuildAssetRepairSignature(asset);
                beforeSignatureByAssetId[asset.Id] = beforeSignature;
                var previousItemId = asset.ItemId;

                asset.CustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CustomerName);
                asset.CurrentLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CurrentLocation);
                asset.ItemCategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(asset.ItemCategoryName);
                asset.Manufacturer = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.Manufacturer);
                asset.ItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
                asset.MachineNumber = (asset.MachineNumber ?? string.Empty).Trim();
                asset.PurchaseVendor = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.PurchaseVendor);
                asset.InstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallLocation);
                asset.DepositText = (asset.DepositText ?? string.Empty).Trim();

                await EnrichAssetReferencesAsync(asset, ct, result, activeItems);
                if (previousItemId.HasValue &&
                    previousItemId.Value != Guid.Empty &&
                    previousItemId != asset.ItemId)
                {
                    displacedItemIds.Add(previousItemId.Value);
                }

                assetBaseKeyById[asset.Id] = BuildAssetKey(asset.ManagementCompanyCode, asset.ManagementNumber, asset.ManagementId, asset.MachineNumber, asset.CustomerName, asset.ItemName);
                asset.AssetStatus = ResolveAssetStatus(asset.AssetStatus, asset.CurrentLocation, asset.DisposalDate);
            }

            AssignUniqueAssetKeysForRepair(assets, assetBaseKeyById);

            foreach (var asset in assets)
            {
                var beforeSignature = beforeSignatureByAssetId[asset.Id];
                if (!string.Equals(beforeSignature, BuildAssetRepairSignature(asset), StringComparison.Ordinal))
                {
                    result.UpdatedAssetCount++;
                    asset.IsDirty = true;
                    asset.UpdatedAtUtc = DateTime.UtcNow;
                }
            }

            foreach (var autoCreatedItemId in activeItems
                         .Where(IsAutoCreatedRentalItem)
                         .Select(item => item.Id))
            {
                displacedItemIds.Add(autoCreatedItemId);
            }

            await _db.SaveChangesAsync(ct);
            await RetireOrphanedAutoCreatedRentalItemsAsync(displacedItemIds, ct);

            SortAndDistinct(result.AddedCategoryNames);
            SortAndDistinct(result.AddedItemNames);
            SortAndDistinct(result.AmbiguousItemNames);

            await _db.SaveChangesAsync(ct);
            return result;
        }
        finally
        {
            AssetSaveLock.Release();
        }
    }

    public async Task<LocalMutationResult> MarkBillingCompletedAsync(
        Guid billingProfileId,
        DateOnly referenceDate,
        string status,
        string note,
        SessionState session,
        CancellationToken ct = default,
        long? expectedRevision = null,
        Guid? billingRunId = null)
    {
        var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");
        if (!CanEditRental(
                string.IsNullOrWhiteSpace(profile.ResponsibleOfficeCode)
                    ? profile.ManagementCompanyCode
                    : profile.ResponsibleOfficeCode,
                session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구를 처리할 수 없습니다.");
        if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(profile, expectedRevision, "렌탈 청구", out var conflictMessage))
            return LocalMutationResult.Conflict(conflictMessage);

        NormalizeBillingSchedule(profile, referenceDate);

        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "완료" : status.Trim();
        var currentRun = FindBillingRunById(profile, billingRunId);
        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty && currentRun is null)
            return LocalMutationResult.Denied("선택한 청구월 정보를 찾을 수 없습니다. 목록을 새로고침한 뒤 다시 시도하세요.");

        currentRun ??= GetOrCreateBillingRun(profile, referenceDate, persistChanges: false);
        var scheduledDate = currentRun?.ScheduledDate
            ?? GetNextBillingDate(profile, referenceDate)
            ?? RentalBillingScheduleRules.BuildBillingDate(referenceDate.Year, referenceDate.Month, profile.BillingDay, profile.BillingDayMode);
        var billedAmount = currentRun?.BilledAmount ?? profile.MonthlyAmount;
        IQueryable<LocalTransaction> settlementQuery = _db.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => !current.IsDeleted && current.LinkedRentalBillingProfileId == billingProfileId);
        if (currentRun?.RunId is Guid currentRunId && currentRunId != Guid.Empty)
            settlementQuery = settlementQuery.Where(current => current.LinkedRentalBillingRunId == currentRunId);

        var settledAmountForCompletion = (await settlementQuery
            .Select(current => current.SettlementAmount)
            .ToListAsync(ct))
            .Sum();
        settledAmountForCompletion = Math.Max(0m, settledAmountForCompletion);

        var remainingAmount = Math.Max(0m, billedAmount - settledAmountForCompletion);
        if (remainingAmount > 0m)
            return LocalMutationResult.Denied($"미수금 {remainingAmount:N0}원이 남아 있어 완납 처리할 수 없습니다. 먼저 '입금 등록'으로 수금을 완료하세요.");

        var billingYearMonth = $"{scheduledDate.Year:0000}-{scheduledDate.Month:00}";
        var log = await _db.RentalBillingLogs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.BillingProfileId == billingProfileId && current.BillingYearMonth == billingYearMonth, ct);
        var now = DateTime.UtcNow;
        if (log is null)
        {
            log = new LocalRentalBillingLog
            {
                Id = Guid.NewGuid(),
                BillingProfileId = billingProfileId,
                BillingYearMonth = billingYearMonth,
                ScheduledDate = scheduledDate,
                ProcessedDate = referenceDate,
                ProcessedByUsername = session.User?.Username ?? string.Empty,
                Status = normalizedStatus,
                BilledAmount = billedAmount,
                Note = (note ?? string.Empty).Trim(),
                ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                IsDirty = true
            };
            _db.RentalBillingLogs.Add(log);
        }
        else
        {
            log.ProcessedDate = referenceDate;
            log.ProcessedByUsername = session.User?.Username ?? string.Empty;
            log.Status = normalizedStatus;
            log.BilledAmount = billedAmount;
            log.Note = (note ?? string.Empty).Trim();
            log.ResponsibleOfficeCode = profile.ResponsibleOfficeCode;
            log.UpdatedAtUtc = now;
            log.IsDirty = true;
            log.IsDeleted = false;
        }

        profile.LastBilledDate = scheduledDate;
        profile.BillingStatus = PaymentFlowConstants.BillingStatusCompleted;
        profile.CompletionStatus = PaymentFlowConstants.NormalizeCompletionStatus(normalizedStatus);
        profile.SettledAmount = settledAmountForCompletion;
        profile.OutstandingAmount = 0m;
        profile.SettlementStatus = PaymentFlowConstants.SettlementStatusConfirmed;
        profile.RequiresFollowUp = false;
        if (currentRun is not null)
        {
            currentRun.BilledAmount = billedAmount;
            currentRun.SettledAmount = settledAmountForCompletion;
            currentRun.SettlementStatus = profile.SettlementStatus;
            currentRun.SettledDate = profile.LastSettledDate;
            currentRun.Status = profile.BillingStatus;
            currentRun.Note = (note ?? string.Empty).Trim();
            UpsertBillingRun(profile, currentRun);
        }
        profile.UpdatedAtUtc = now;
        profile.IsDirty = true;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(billingProfileId, "렌탈 청구 처리 이력을 저장했습니다.");
    }

    public async Task<RentalImportResult> ImportBillingWorkbookAsync(
        string path,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanImportRental(session))
            throw new InvalidOperationException("권한이 없어 렌탈 청구 엑셀을 가져올 수 없습니다.");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("렌탈 청구 엑셀 파일을 찾을 수 없습니다.", path);

        await UpsertSettingAsync(BillingWorkbookPathSettingKey, path, ct);
        var result = new RentalImportResult { SourcePath = path };
        var workbook = ReadWorkbook(path);
        var sheetInfos = workbook.Tables
            .Cast<DataTable>()
            .Select(table => new { Table = table, Anchor = ParseSheetAnchorDate(table.TableName, table) })
            .Where(item => item.Table.TableName.Contains("렌탈판매 청구 리스트", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Anchor ?? DateOnly.MinValue)
            .ToList();

        if (sheetInfos.Count == 0)
            throw new InvalidOperationException("렌탈 청구 시트를 찾지 못했습니다.");

        foreach (var sheetInfo in sheetInfos)
        {
            var headerRowIndex = FindHeaderRow(sheetInfo.Table, "청구일", "거래처명");
            if (headerRowIndex < 0)
            {
                result.Messages.Add($"{sheetInfo.Table.TableName}: 헤더를 찾지 못해 건너뜀");
                continue;
            }

            var headerMap = BuildHeaderMap(sheetInfo.Table, headerRowIndex);
            for (var rowIndex = headerRowIndex + 1; rowIndex < sheetInfo.Table.Rows.Count; rowIndex++)
            {
                var row = sheetInfo.Table.Rows[rowIndex];
                var customerName = GetCellString(row, headerMap, "거래처명");
                var itemName = GetCellString(row, headerMap, "품명", "모델명");
                if (string.IsNullOrWhiteSpace(customerName) && string.IsNullOrWhiteSpace(itemName))
                    continue;

                try
                {
                    var officeValue = GetCellString(row, headerMap, "담당지점", "관리업체");
                    var officeCode = await ResolveRentalOfficeCodeAsync(officeValue, officeValue, session.OfficeCode, ct);
                    var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                        null,
                        officeCode,
                        session.TenantCode,
                        session.OfficeCode);
                    var billingDay = ParseIntValue(GetCellValue(row, headerMap, "청구일")) ?? 25;
                    var billingCycleMonths = ParseIntValue(GetCellValue(row, headerMap, "청구기간[개월수]")) ?? 1;
                    var businessNumber = GetCellString(row, headerMap, "사업자번호");
                    var customerId = await ResolveCustomerIdAsync(
                        customerName,
                        businessNumber,
                        ct,
                        preferredOfficeCode: officeCode,
                        preferredTenantCode: tenantCode);
                    var billingDayMode = billingDay >= 31
                        ? RentalBillingScheduleRules.BillingDayModeEndOfMonth
                        : RentalBillingScheduleRules.BillingDayModeFixedDay;
                    var anchorDate = sheetInfo.Anchor.HasValue
                        ? RentalBillingScheduleRules.BuildBillingDate(sheetInfo.Anchor.Value.Year, sheetInfo.Anchor.Value.Month, billingDay, billingDayMode)
                        : (DateOnly?)null;

                    var profileKey = BuildProfileKey(
                        officeCode,
                        customerId,
                        businessNumber,
                        customerName,
                        "묶음",
                        "후불",
                        RentalBillingScheduleRules.NormalizeBillingDay(billingDay),
                        RentalBillingScheduleRules.NormalizeCycleMonths(billingCycleMonths),
                        NormalizeBillingMethod(GetCellString(row, headerMap, "청구방식").Trim()));
                    var existing = await _db.RentalBillingProfiles.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(current => current.ProfileKey == profileKey, ct);

                    var profile = existing ?? new LocalRentalBillingProfile
                    {
                        Id = SyncIdentityGenerator.CreateRentalBillingProfileId(profileKey),
                        ProfileKey = profileKey,
                        CreatedAtUtc = DateTime.UtcNow
                    };

                    profile.CustomerName = customerName.Trim();
                    profile.BusinessNumber = businessNumber.Trim();
                    profile.ItemName = itemName.Trim();
                    profile.TenantCode = tenantCode;
                    profile.OfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(profile.OfficeCode, officeCode, session.OfficeCode);
                    profile.ManagementCompanyCode = officeCode;
                    profile.ResponsibleOfficeCode = officeCode;
                    profile.BillingMethod = NormalizeBillingMethod(GetCellString(row, headerMap, "청구방식").Trim());
                    profile.BillingStatus = string.IsNullOrWhiteSpace(GetCellString(row, headerMap, "청구상태"))
                        ? profile.BillingStatus
                        : GetCellString(row, headerMap, "청구상태").Trim();
                    profile.Email = GetCellString(row, headerMap, "E-Mail").Trim();
                    profile.BillingDayMode = billingDayMode;
                    profile.BillingDay = RentalBillingScheduleRules.NormalizeBillingDay(billingDay);
                    profile.BillingCycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(billingCycleMonths);
                    profile.MonthlyAmount = ParseDecimalValue(GetCellValue(row, headerMap, "월청구대금"));
                    profile.SubmissionDocuments = GetCellString(row, headerMap, "추가제출서류").Trim();
                    profile.Notes = GetCellString(row, headerMap, "비고").Trim();
                    profile.BillingAnchorDate = anchorDate ?? profile.BillingAnchorDate;
                    profile.BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
                        profile.BillingCycleMonths,
                        profile.BillingAnchorMonth,
                        profile.BillingAnchorDate,
                        profile.BillingStartDate,
                        profile.ContractStartDate,
                        profile.ContractDate,
                        profile.LastBilledDate,
                        sheetInfo.Anchor ?? DateOnly.FromDateTime(DateTime.Today));
                    profile.DocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(profile.DocumentIssueMode);
                    profile.DocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(profile.DocumentLeadDays);
                    if (existing is not null)
                    {
                        profile.SettlementStatus = existing.SettlementStatus;
                        profile.CompletionStatus = existing.CompletionStatus;
                        profile.SettledAmount = existing.SettledAmount;
                        profile.OutstandingAmount = existing.OutstandingAmount;
                        profile.RequiresFollowUp = existing.RequiresFollowUp;
                        profile.LastSettledDate = existing.LastSettledDate;
                    }
                    else
                    {
                        profile.SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid;
                        profile.CompletionStatus = PaymentFlowConstants.CompletionPending;
                        profile.SettledAmount = 0m;
                        profile.OutstandingAmount = 0m;
                        profile.RequiresFollowUp = false;
                        profile.LastSettledDate = null;
                    }
                    profile.CustomerId = existing?.CustomerId ?? customerId;
                    profile.IsActive = true;
                    profile.IsDeleted = false;
                    profile.UpdatedAtUtc = DateTime.UtcNow;
                    profile.IsDirty = true;

                    if (existing is null)
                    {
                        _db.RentalBillingProfiles.Add(profile);
                        result.CreatedCount++;
                    }
                    else
                    {
                        _db.Entry(existing).CurrentValues.SetValues(profile);
                        result.UpdatedCount++;
                    }
                }
                catch (Exception ex)
                {
                    result.ErrorCount++;
                    result.Messages.Add($"{sheetInfo.Table.TableName} {rowIndex + 1}행: {ex.Message}");
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<RentalImportResult> ImportAssetWorkbookAsync(
        string path,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanImportRental(session))
            throw new InvalidOperationException("권한이 없어 렌탈 자산 엑셀을 가져올 수 없습니다.");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("렌탈 자산 엑셀 파일을 찾을 수 없습니다.", path);

        await UpsertSettingAsync(AssetWorkbookPathSettingKey, path, ct);
        var result = new RentalImportResult { SourcePath = path };
        var workbook = ReadWorkbook(path);
        var table = workbook.Tables.Cast<DataTable>()
            .FirstOrDefault(current => string.Equals(current.TableName, "렌탈재고관리", StringComparison.OrdinalIgnoreCase));
        if (table is null)
            throw new InvalidOperationException("렌탈재고관리 시트를 찾지 못했습니다.");

        var headerRowIndex = FindHeaderRow(table, "관리번호", "고객명");
        if (headerRowIndex < 0)
            throw new InvalidOperationException("렌탈자산 헤더를 찾지 못했습니다.");

        var headerMap = BuildHeaderMap(table, headerRowIndex);
        for (var rowIndex = headerRowIndex + 1; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var sourceManagementId = GetCellString(row, headerMap, "관리ID");
            var sourceManagementNumber = GetCellString(row, headerMap, "관리번호");
            var officeValue = GetCellString(row, headerMap, "관리업체", "담당지점");
            var currentLocation = GetCellString(row, headerMap, "현재위치").Trim();
            var itemCategoryName = GetCellString(row, headerMap, "품목분류", "상품분류");
            var manufacturer = GetCellString(row, headerMap, "제조사");
            var customerName = GetCellString(row, headerMap, "고객명");
            var itemName = GetCellString(row, headerMap, "품명", "모델명");
            var machineNumber = GetCellString(row, headerMap, "기계번호");
            var installLocation = GetCellString(row, headerMap, "설치위치");
            if (string.IsNullOrWhiteSpace(sourceManagementNumber) && string.IsNullOrWhiteSpace(customerName) && string.IsNullOrWhiteSpace(itemName))
                continue;
            if (IsRentalAssetSummaryRow(
                    sourceManagementId,
                    sourceManagementNumber,
                    officeValue,
                    currentLocation,
                    itemCategoryName,
                    manufacturer,
                    itemName,
                    machineNumber,
                    customerName,
                    installLocation))
            {
                continue;
            }

            try
            {
                if (!TryResolveImportManagementOfficeCode(officeValue, out var officeCode, out var officeError))
                    throw new InvalidOperationException(officeError);

                var disposalDate = ParseDateValue(GetCellValue(row, headerMap, "폐기일"));
                if (!TryResolveImportAssetStatus(currentLocation, disposalDate, out var assetStatus, out var statusError))
                    throw new InvalidOperationException(statusError);

                var existing = await FindExistingAssetForImportAsync(
                    officeCode,
                    sourceManagementId,
                    sourceManagementNumber,
                    machineNumber,
                    customerName,
                    itemName,
                    installLocation,
                    ct);
                var wasExisting = existing is not null;

                var asset = new LocalRentalAsset
                {
                    Id = existing?.Id ?? Guid.NewGuid(),
                    CustomerId = existing?.CustomerId,
                    ItemId = existing?.ItemId,
                    BillingProfileId = existing?.BillingProfileId,
                    ManagementId = existing?.ManagementId ?? string.Empty,
                    ManagementNumber = existing?.ManagementNumber ?? string.Empty,
                    CreatedAtUtc = existing?.CreatedAtUtc ?? DateTime.UtcNow,
                    Notes = BuildImportedAssetNotes(existing?.Notes, sourceManagementId, sourceManagementNumber, row, headerMap)
                };

                asset.ManagementNumber = !string.IsNullOrWhiteSpace(sourceManagementNumber)
                    ? sourceManagementNumber.Trim()
                    : (existing?.ManagementNumber ?? string.Empty).Trim();
                asset.ManagementCompanyCode = officeCode;
                asset.ResponsibleOfficeCode = officeCode;
                asset.CurrentLocation = currentLocation;
                asset.ItemCategoryName = itemCategoryName.Trim();
                asset.Manufacturer = manufacturer.Trim();
                asset.ItemName = itemName.Trim();
                asset.MachineNumber = machineNumber.Trim();
                asset.PurchaseVendor = GetCellString(row, headerMap, "매입처").Trim();
                asset.PurchaseDate = ParseDateValue(GetCellValue(row, headerMap, "매입일"));
                asset.DisposalDate = disposalDate;
                asset.PurchasePrice = ParseDecimalValue(GetCellValue(row, headerMap, "매입가"));
                asset.SalePrice = ParseDecimalValue(GetCellValue(row, headerMap, "판매가"));
                asset.CustomerName = customerName.Trim();
                asset.CurrentCustomerName = asset.CustomerName;
                asset.InstallSiteName = asset.CustomerName;
                asset.InstallLocation = installLocation.Trim();
                asset.DepositText = GetCellString(row, headerMap, "보증금").Trim();
                asset.MonthlyFee = ParseDecimalValue(GetCellValue(row, headerMap, "렌탈요금"));
                asset.ContractMonths = ParseIntValue(GetCellValue(row, headerMap, "계약기간")) ?? 0;
                asset.ContractDate = ParseDateValue(GetCellValue(row, headerMap, "계약일"));
                asset.InstallDate = ParseDateValue(GetCellValue(row, headerMap, "설치일"));
                asset.ContractStartDate = ParseDateValue(GetCellValue(row, headerMap, "계약시작"));
                asset.RentalEndDate = ParseDateValue(GetCellValue(row, headerMap, "렌탈만료"));
                asset.FreeSupplyItems = GetCellString(row, headerMap, "무상품목").Trim();
                asset.PaidSupplyItems = GetCellString(row, headerMap, "유상품목").Trim();
                asset.AssetStatus = assetStatus;
                asset.CustomerId = await ResolveCustomerIdAsync(
                    asset.CustomerName,
                    null,
                    ct,
                    allowWorkbookNameVariants: false,
                    preferredOfficeCode: officeCode,
                    preferredTenantCode: asset.TenantCode);

                var saveResult = await SaveAssetAsync(
                    asset,
                    session,
                    ct,
                    allowWorkbookNameVariants: false,
                    allowCategoryRecovery: true);
                if (!saveResult.Success)
                    throw new InvalidOperationException(saveResult.Message);

                if (wasExisting)
                    result.UpdatedCount++;
                else
                    result.CreatedCount++;
            }
            catch (Exception ex)
            {
                result.ErrorCount++;
                result.Messages.Add($"렌탈재고관리 {rowIndex + 1}행: {ex.Message}");
            }
        }

        return result;
    }

    private IQueryable<LocalRentalBillingProfile> ApplyBillingFilter(
        IQueryable<LocalRentalBillingProfile> query,
        RentalBillingFilter filter,
        SessionState session)
    {
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var keyword = filter.SearchText.Trim();
            query = query.Where(profile =>
                profile.CustomerName.Contains(keyword) ||
                profile.BusinessNumber.Contains(keyword) ||
                profile.ItemName.Contains(keyword) ||
                profile.Notes.Contains(keyword));
        }

        if (!string.IsNullOrWhiteSpace(filter.OfficeCode))
            query = query.Where(profile =>
                profile.ResponsibleOfficeCode == filter.OfficeCode ||
                ((profile.ResponsibleOfficeCode == null ||
                  profile.ResponsibleOfficeCode == string.Empty ||
                  profile.ResponsibleOfficeCode == OfficeCodeCatalog.Shared) &&
                 profile.ManagementCompanyCode == filter.OfficeCode));

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            if (filter.Status == "활성")
                query = query.Where(profile => profile.IsActive);
            else if (filter.Status == "비활성")
                query = query.Where(profile => !profile.IsActive);
            else if (IsUnlinkedBillingStatusFilter(filter.Status))
                query = query.Where(_ => false);
            else
                query = query.Where(profile => profile.BillingStatus == filter.Status);
        }

        return query;
    }

    private IQueryable<LocalRentalAsset> ApplyAssetFilter(
        IQueryable<LocalRentalAsset> query,
        RentalAssetFilter filter,
        SessionState session)
    {
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var keyword = filter.SearchText.Trim();
            query = query.Where(asset =>
                asset.ManagementNumber.Contains(keyword) ||
                asset.CustomerName.Contains(keyword) ||
                asset.ItemCategoryName.Contains(keyword) ||
                asset.ItemName.Contains(keyword) ||
                asset.MachineNumber.Contains(keyword) ||
                asset.InstallLocation.Contains(keyword));
        }

        var officeCodes = NormalizeFilterValues(filter.OfficeCodes)
            .Select(code => NormalizeOfficeCode(code, code))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (officeCodes.Count > 0)
            query = query.Where(asset =>
                officeCodes.Contains(asset.ResponsibleOfficeCode) ||
                officeCodes.Contains(asset.ManagementCompanyCode) ||
                officeCodes.Contains(
                    ((asset.ResponsibleOfficeCode ?? string.Empty).Trim() == string.Empty
                        ? (asset.ManagementCompanyCode ?? string.Empty)
                        : asset.ResponsibleOfficeCode!)
                    .Trim()
                    .ToUpper()));

        var itemCategoryNames = NormalizeFilterValues(filter.ItemCategoryNames);
        if (itemCategoryNames.Count > 0)
            query = query.Where(asset => itemCategoryNames.Contains(asset.ItemCategoryName));

        var assetStatuses = NormalizeAssetStatusFilterValues(filter.AssetStatuses);
        if (assetStatuses.Count > 0)
        {
            var expandedAssetStatuses = ExpandAssetStatusFilterValues(assetStatuses);
            query = query.Where(asset => expandedAssetStatuses.Contains(asset.AssetStatus));
        }

        return query;
    }

    private static List<string> NormalizeFilterValues(IEnumerable<string>? values)
        => (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> NormalizeAssetStatusFilterValues(IEnumerable<string>? values)
        => (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(RentalAssetStatusRules.Normalize)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> ExpandAssetStatusFilterValues(IEnumerable<string> normalizedValues)
        => normalizedValues
            .SelectMany(RentalAssetStatusNormalizer.ExpandForFilter)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private IQueryable<LocalRentalBillingProfile> ApplyBillingScope(
        IQueryable<LocalRentalBillingProfile> query,
        SessionState session)
    {
        if (CanAdministrativelyViewAllRental(session))
            return query;

        var currentTenantCode = ResolveCurrentRentalTenantCode(session);
        if (CanViewAllRental(session))
            return query.Where(profile => profile.TenantCode == currentTenantCode);

        var readableOfficeAliases = BuildReadableRentalOfficeQueryAliases(session);
        return query.Where(profile =>
            profile.TenantCode == currentTenantCode &&
            (
                readableOfficeAliases.Contains(profile.ResponsibleOfficeCode) ||
                readableOfficeAliases.Contains(profile.ManagementCompanyCode) ||
                readableOfficeAliases.Contains(((profile.ResponsibleOfficeCode ?? string.Empty).Trim().ToUpper())) ||
                readableOfficeAliases.Contains(((profile.ManagementCompanyCode ?? string.Empty).Trim().ToUpper()))
            ));
    }

    private IQueryable<LocalRentalAsset> ApplyAssetScope(
        IQueryable<LocalRentalAsset> query,
        SessionState session)
    {
        if (CanAdministrativelyViewAllRental(session))
            return query;

        var currentTenantCode = ResolveCurrentRentalTenantCode(session);
        if (CanViewAllRental(session))
            return query.Where(asset => asset.TenantCode == currentTenantCode);

        var readableOfficeAliases = BuildReadableRentalOfficeQueryAliases(session);
        return query.Where(asset =>
            asset.TenantCode == currentTenantCode &&
            (
                readableOfficeAliases.Contains(asset.ResponsibleOfficeCode) ||
                readableOfficeAliases.Contains(asset.ManagementCompanyCode) ||
                readableOfficeAliases.Contains(((asset.ResponsibleOfficeCode ?? string.Empty).Trim().ToUpper())) ||
                readableOfficeAliases.Contains(((asset.ManagementCompanyCode ?? string.Empty).Trim().ToUpper()))
            ));
    }

    private IQueryable<LocalRentalAsset> ApplySharedAssetViewScope(
        IQueryable<LocalRentalAsset> query,
        SessionState session)
    {
        if (CanViewAllAssetScope(session))
            return query;

        return query.Where(_ => false);
    }

    private bool CanAccessRental(string? officeCode, SessionState session)
    {
        if (CanAdministrativelyViewAllRental(session))
            return true;

        return GetReadableRentalOfficeCodes(session).Contains(NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet));
    }

    private bool CanEditRental(string? officeCode, SessionState session)
    {
        if (!CanEditRentalProfiles(session))
            return false;

        if (CanEditAllRental(session) || session.HasGlobalDataScope)
            return true;

        return GetWritableRentalOfficeCodes(session).Contains(NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet));
    }

    private bool CanViewAllRental(SessionState? session)
        => session is not null && session.IsLoggedIn && (
            session.HasAdministrativePrivileges ||
            session.HasGlobalDataScope ||
            session.HasAssignedPermission(AppPermissionNames.RentalViewAll) ||
            session.HasAssignedPermission(AppPermissionNames.RentalEditAll));

    private static bool CanAdministrativelyViewAllRental(SessionState? session)
        => session is not null &&
           session.IsLoggedIn &&
           (session.HasAdministrativePrivileges || session.HasGlobalDataScope);

    private static bool CanViewTenantRental(SessionState? session)
        => session is not null &&
           session.IsLoggedIn &&
           !session.HasGlobalDataScope &&
           string.Equals(session.ScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase);

    private bool CanEditAllRental(SessionState? session)
        => session is not null && session.IsLoggedIn && (
            session.HasAdministrativePrivileges ||
            session.HasPermission(AppPermissionNames.RentalEditAll));

    private static bool CanEditRentalProfiles(SessionState? session)
        => session is not null && session.IsLoggedIn && (
            session.HasAdministrativePrivileges ||
            session.HasPermission(AppPermissionNames.RentalEditAll) ||
            session.HasPermission(AppPermissionNames.RentalProfileEdit));

    public bool CanViewAllAssetScope(SessionState? session)
        => session is not null && session.IsLoggedIn;

    public bool CanManageAllAssetScope(SessionState? session)
        => session is not null && session.IsLoggedIn && (
            session.HasAdministrativePrivileges ||
            session.HasPermission(AppPermissionNames.RentalEditAll));

    public HashSet<string> GetReadableAssetOfficeCodes(SessionState? session)
    {
        if (session is null || !session.IsLoggedIn)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return OfficeCodeCatalog.All
            .Select(officeCode => NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet))
            .Where(officeCode => !string.IsNullOrWhiteSpace(officeCode))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public HashSet<string> GetWritableAssetOfficeCodes(SessionState? session)
    {
        if (session is null || !session.IsLoggedIn)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (CanManageAllAssetScope(session))
        {
            return OfficeCodeCatalog.All
                .Select(officeCode => NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet))
                .Where(officeCode => !string.IsNullOrWhiteSpace(officeCode))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return GetWritableRentalOfficeCodes(session);
    }

    public string GetDefaultAssetOfficeCode(SessionState session)
        => NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet);

    public bool CanEditAssetScope(string? officeCode, SessionState? session)
    {
        if (session is null || !session.IsLoggedIn)
            return false;

        if (CanManageAllAssetScope(session))
            return true;

        var normalizedOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet);
        return GetWritableRentalOfficeCodes(session).Contains(normalizedOfficeCode);
    }

    private List<string> BuildReadableRentalOfficeQueryAliases(SessionState session)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var officeCode in GetReadableRentalOfficeCodes(session))
        {
            if (RentalOfficeQueryAliasMap.TryGetValue(officeCode, out var officeAliases))
            {
                foreach (var alias in officeAliases)
                    aliases.Add((alias ?? string.Empty).Trim().ToUpperInvariant());
                continue;
            }

            aliases.Add((officeCode ?? string.Empty).Trim().ToUpperInvariant());
        }

        return aliases.ToList();
    }

    private List<string> BuildReadableAssetOfficeQueryAliases(SessionState session)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var officeCode in GetReadableAssetOfficeCodes(session))
        {
            if (RentalOfficeQueryAliasMap.TryGetValue(officeCode, out var officeAliases))
            {
                foreach (var alias in officeAliases)
                    aliases.Add((alias ?? string.Empty).Trim().ToUpperInvariant());
                continue;
            }

            aliases.Add((officeCode ?? string.Empty).Trim().ToUpperInvariant());
        }

        return aliases.ToList();
    }

    private HashSet<string> GetReadableRentalOfficeCodes(SessionState session)
        => TenantScopeCatalog.ResolveScopedOfficeCodes(
            session.OfficeCode,
            session.TenantCode,
            session.ScopeType,
            hasGlobalScope: CanAdministrativelyViewAllRental(session),
            hasTenantScope: CanViewAllRental(session));

    private static string ResolveCurrentRentalTenantCode(SessionState session)
        => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            session.TenantCode,
            session.OfficeCode);

    private HashSet<string> GetWritableRentalOfficeCodes(SessionState session)
        => TenantScopeCatalog.ResolveScopedOfficeCodes(
            session.OfficeCode,
            session.TenantCode,
            session.ScopeType,
            hasGlobalScope: session.HasGlobalDataScope,
            hasTenantScope: CanEditAllRental(session));

    private static HashSet<string> GetReadableOfficeCodes(SessionState session)
        => TenantScopeCatalog.ResolveScopedOfficeCodes(
            session.OfficeCode,
            session.TenantCode,
            session.ScopeType,
            hasGlobalScope: session.HasGlobalDataScope);

    private bool CanEditRentalSettings(SessionState? session)
        => session is not null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.RentalSettingsEdit));

    private bool CanImportRental(SessionState? session)
        => session is not null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.RentalImport));

    private static string NormalizeAlertDaysText(string value)
    {
        var parsed = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) ? day : (int?)null)
            .Where(day => day.HasValue && day.Value >= 0)
            .Select(day => day!.Value)
            .Distinct()
            .OrderByDescending(day => day)
            .ToList();

        if (parsed.Count == 0)
            parsed.AddRange([7, 3, 1, 0]);

        return string.Join(',', parsed);
    }

    private async Task EnsureAssetManagementIdentifiersAsync(
        LocalRentalAsset asset,
        LocalRentalAsset? existing,
        DateTime registeredAtUtc,
        CancellationToken ct)
    {
        if (existing is not null)
        {
            asset.ManagementId = string.IsNullOrWhiteSpace(asset.ManagementId)
                ? existing.ManagementId
                : asset.ManagementId.Trim();
            asset.ManagementNumber = string.IsNullOrWhiteSpace(asset.ManagementNumber)
                ? existing.ManagementNumber
                : asset.ManagementNumber.Trim();
        }

        if (string.IsNullOrWhiteSpace(asset.ManagementId))
            asset.ManagementId = await GenerateNextManagementIdAsync(asset.Id, ct);

        if (string.IsNullOrWhiteSpace(asset.ManagementNumber))
            asset.ManagementNumber = await GenerateNextManagementNumberAsync(asset.Id, registeredAtUtc, ct);
    }

    private async Task<string> GenerateNextManagementIdAsync(Guid currentAssetId, CancellationToken ct)
    {
        var usedManagementIds = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id != currentAssetId)
            .Select(asset => asset.ManagementId)
            .ToListAsync(ct);

        var nextValue = usedManagementIds
            .Select(ParseManagementId)
            .DefaultIfEmpty(0)
            .Max() + 1;

        return nextValue.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<string> GenerateNextManagementNumberAsync(Guid currentAssetId, DateTime registeredAtUtc, CancellationToken ct)
    {
        var registeredLocalDate = ConvertUtcToKoreaDate(registeredAtUtc);
        var prefix = registeredLocalDate.ToString("yyMM", CultureInfo.InvariantCulture);
        var usedManagementNumbers = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id != currentAssetId)
            .Select(asset => asset.ManagementNumber)
            .ToListAsync(ct);

        var nextSequence = usedManagementNumbers
            .Select(number => ParseManagementNumberSequence(number, prefix))
            .DefaultIfEmpty(0)
            .Max() + 1;

        return $"{prefix}-{nextSequence:000}";
    }

    private async Task<LocalRentalAsset?> FindExistingAssetForImportAsync(
        string officeCode,
        string? sourceManagementId,
        string? sourceManagementNumber,
        string? machineNumber,
        string? customerName,
        string? itemName,
        string? installLocation,
        CancellationToken ct)
    {
        var normalizedOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet);
        var candidates = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset =>
                asset.ManagementCompanyCode == normalizedOfficeCode ||
                asset.ResponsibleOfficeCode == normalizedOfficeCode)
            .ToListAsync(ct);

        var normalizedManagementNumber = NormalizeProfileKeyPart(sourceManagementNumber);
        if (!string.IsNullOrWhiteSpace(normalizedManagementNumber))
        {
            var byManagementNumber = candidates.FirstOrDefault(asset =>
                string.Equals(NormalizeProfileKeyPart(asset.ManagementNumber), normalizedManagementNumber, StringComparison.Ordinal));
            if (byManagementNumber is not null)
                return byManagementNumber;

            var bySourceManagementNumber = candidates.FirstOrDefault(asset =>
                HasImportedSourceIdentifier(asset.Notes, "원본 관리번호", sourceManagementNumber));
            if (bySourceManagementNumber is not null)
                return bySourceManagementNumber;
        }

        var normalizedManagementId = NormalizeProfileKeyPart(sourceManagementId);
        if (!string.IsNullOrWhiteSpace(normalizedManagementId))
        {
            var byManagementId = candidates.FirstOrDefault(asset =>
                string.Equals(NormalizeProfileKeyPart(asset.ManagementId), normalizedManagementId, StringComparison.Ordinal));
            if (byManagementId is not null)
                return byManagementId;

            var bySourceManagementId = candidates.FirstOrDefault(asset =>
                HasImportedSourceIdentifier(asset.Notes, "원본 관리ID", sourceManagementId));
            if (bySourceManagementId is not null)
                return bySourceManagementId;
        }

        var normalizedMachineNumber = NormalizeProfileKeyPart(machineNumber);
        if (!string.IsNullOrWhiteSpace(normalizedMachineNumber))
        {
            var byMachineNumber = candidates.FirstOrDefault(asset =>
                string.Equals(NormalizeProfileKeyPart(asset.MachineNumber), normalizedMachineNumber, StringComparison.Ordinal));
            if (byMachineNumber is not null)
                return byMachineNumber;

            return null;
        }

        var normalizedCustomerName = RentalCatalogValueNormalizer.NormalizeLooseKey(customerName);
        var normalizedItemName = RentalCatalogValueNormalizer.NormalizeLooseKey(itemName);
        var normalizedInstallLocation = RentalCatalogValueNormalizer.NormalizeLooseKey(installLocation);
        if (string.IsNullOrWhiteSpace(normalizedCustomerName) ||
            string.IsNullOrWhiteSpace(normalizedItemName) ||
            string.IsNullOrWhiteSpace(normalizedInstallLocation))
        return null;

        return candidates.FirstOrDefault(asset =>
            string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.CustomerName), normalizedCustomerName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.ItemName), normalizedItemName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.InstallLocation), normalizedInstallLocation, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveImportManagementOfficeCode(string? rawValue, out string officeCode, out string errorMessage)
    {
        var normalizedValue = (rawValue ?? string.Empty).Trim();
        if (ImportManagementOfficeMap.TryGetValue(normalizedValue, out var mappedOfficeCode))
        {
            officeCode = mappedOfficeCode;
            errorMessage = string.Empty;
            return true;
        }

        officeCode = string.Empty;
        errorMessage = string.IsNullOrWhiteSpace(normalizedValue)
            ? "관리업체 값이 비어 있어 담당지점을 변환할 수 없습니다."
            : $"관리업체 '{normalizedValue}'는 담당지점 변환 규칙에 없습니다.";
        return false;
    }

    private static bool IsRentalAssetSummaryRow(
        string? sourceManagementId,
        string? sourceManagementNumber,
        string? officeValue,
        string? currentLocation,
        string? itemCategoryName,
        string? manufacturer,
        string? itemName,
        string? machineNumber,
        string? customerName,
        string? installLocation)
    {
        if (!string.IsNullOrWhiteSpace(sourceManagementId) ||
            !string.IsNullOrWhiteSpace(sourceManagementNumber) ||
            !string.IsNullOrWhiteSpace(officeValue) ||
            !string.IsNullOrWhiteSpace(itemCategoryName) ||
            !string.IsNullOrWhiteSpace(manufacturer) ||
            !string.IsNullOrWhiteSpace(machineNumber) ||
            !string.IsNullOrWhiteSpace(customerName) ||
            !string.IsNullOrWhiteSpace(installLocation))
        {
            return false;
        }

        return IsBlankOrNumericSummaryValue(currentLocation) &&
               IsBlankOrNumericSummaryValue(itemName);
    }

    private static bool TryResolveImportAssetStatus(
        string? currentLocation,
        DateOnly? disposalDate,
        out string assetStatus,
        out string errorMessage)
    {
        var normalizedLocation = (currentLocation ?? string.Empty).Trim();
        if (ImportLocationStatusMap.TryGetValue(normalizedLocation, out var mappedStatus))
        {
            assetStatus = mappedStatus;
            errorMessage = string.Empty;
            return true;
        }

        if (disposalDate.HasValue)
        {
            assetStatus = "폐기";
            errorMessage = string.Empty;
            return true;
        }

        assetStatus = string.Empty;
        errorMessage = string.IsNullOrWhiteSpace(normalizedLocation)
            ? "현재 위치 값이 비어 있어 상태를 변환할 수 없습니다."
            : $"현재 위치 '{normalizedLocation}'는 상태 변환 규칙에 없습니다.";
        return false;
    }

    private static bool IsBlankOrNumericSummaryValue(string? value)
    {
        var normalizedValue = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue))
            return true;

        normalizedValue = normalizedValue.Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(normalizedValue, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
    }

    private static int ParseManagementId(string? managementId)
        => int.TryParse((managementId ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static int ParseManagementNumberSequence(string? managementNumber, string prefix)
    {
        var normalizedValue = (managementNumber ?? string.Empty).Trim();
        if (!normalizedValue.StartsWith($"{prefix}-", StringComparison.OrdinalIgnoreCase))
            return 0;

        var sequenceText = normalizedValue[(prefix.Length + 1)..];
        return int.TryParse(sequenceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static DateOnly ConvertUtcToKoreaDate(DateTime utcDateTime)
    {
        var normalizedUtc = utcDateTime.Kind == DateTimeKind.Utc
            ? utcDateTime
            : utcDateTime.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc)
                : utcDateTime.ToUniversalTime();
        var koreaDateTime = TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, KoreaTimeZone);
        return DateOnly.FromDateTime(koreaDateTime);
    }

    private static TimeZoneInfo ResolveKoreaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        }
    }

    private static string BuildImportedAssetNotes(
        string? existingNotes,
        string? sourceManagementId,
        string? sourceManagementNumber,
        DataRow? row,
        IReadOnlyDictionary<string, int>? headerMap)
    {
        var lines = new List<string>();
        AddDistinctNoteLines(lines, existingNotes);

        if (!string.IsNullOrWhiteSpace(sourceManagementId))
            AddDistinctNoteLine(lines, $"원본 관리ID: {sourceManagementId.Trim()}");
        if (!string.IsNullOrWhiteSpace(sourceManagementNumber))
            AddDistinctNoteLine(lines, $"원본 관리번호: {sourceManagementNumber.Trim()}");

        if (row is not null && headerMap is not null)
        {
            AppendWorkbookNoteLine(lines, row, headerMap, "K제한", "K제한", "K 제한");
            AppendWorkbookNoteLine(lines, row, headerMap, "C제한", "C제한", "C 제한");
            AppendWorkbookNoteLine(lines, row, headerMap, "K추가", "K추가", "K 추가");
            AppendWorkbookNoteLine(lines, row, headerMap, "C추가", "C추가", "C 추가");
            AppendWorkbookNoteLine(lines, row, headerMap, "기타사항", "기타사항", "비고");
            AppendWorkbookNoteLine(lines, row, headerMap, "회수1", "회수1");
            AppendWorkbookNoteLine(lines, row, headerMap, "렌탈1", "렌탈1");
            AppendWorkbookNoteLine(lines, row, headerMap, "회수2", "회수2");
            AppendWorkbookNoteLine(lines, row, headerMap, "렌탈2", "렌탈2");
            AppendWorkbookNoteLine(lines, row, headerMap, "회수3", "회수3");
            AppendWorkbookNoteLine(lines, row, headerMap, "렌탈3", "렌탈3");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendWorkbookNoteLine(
        ICollection<string> lines,
        DataRow row,
        IReadOnlyDictionary<string, int> headerMap,
        string label,
        params string[] headers)
    {
        var value = GetCellString(row, headerMap, headers);
        if (!string.IsNullOrWhiteSpace(value))
            AddDistinctNoteLine(lines, $"{label}: {value.Trim()}");
    }

    private static void AddDistinctNoteLines(ICollection<string> lines, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return;

        foreach (var line in source
                     .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(current => current.Trim())
                     .Where(current => !string.IsNullOrWhiteSpace(current)))
        {
            AddDistinctNoteLine(lines, line);
        }
    }

    private static void AddDistinctNoteLine(ICollection<string> lines, string line)
    {
        if (!lines.Any(existing => string.Equals(existing, line, StringComparison.OrdinalIgnoreCase)))
            lines.Add(line);
    }

    private static bool HasImportedSourceIdentifier(string? notes, string label, string? value)
    {
        var normalizedValue = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue) || string.IsNullOrWhiteSpace(notes))
            return false;

        var extractedValue = ExtractImportedSourceValue(notes, label);
        return string.Equals(extractedValue, normalizedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractImportedSourceValue(string? notes, string label)
    {
        if (string.IsNullOrWhiteSpace(notes) || string.IsNullOrWhiteSpace(label))
            return string.Empty;

        var normalizedNotes = notes
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        var labelIndex = normalizedNotes.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (labelIndex < 0)
            return string.Empty;

        var colonIndex = normalizedNotes.IndexOf(':', labelIndex);
        if (colonIndex < 0)
            return string.Empty;

        var tail = normalizedNotes[(colonIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(tail))
            return string.Empty;

        if (label.Contains("관리번호", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var token in tail.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (LooksLikeImportedManagementNumber(token))
                    return token;
            }

            return string.Empty;
        }

        if (label.Contains("관리ID", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var token in tail.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalizedToken = token.Trim();
                if (normalizedToken.All(char.IsDigit))
                    return normalizedToken;
            }

            return string.Empty;
        }

        return tail.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
    }

    private static bool LooksLikeImportedManagementNumber(string token)
        => !string.IsNullOrWhiteSpace(token) &&
           token.Length == 8 &&
           token[4] == '-' &&
           token[..4].All(char.IsDigit) &&
           token[5..].All(char.IsDigit);

    private static IEnumerable<string> BuildStrictCustomerNameCandidates(string? customerName)
    {
        var normalizedName = RentalCatalogValueNormalizer.NormalizeDisplayText(customerName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in EnumerateStrictCustomerNameCandidates(normalizedName))
        {
            var normalizedCandidate = RentalCatalogValueNormalizer.NormalizeDisplayText(candidate);
            if (!string.IsNullOrWhiteSpace(normalizedCandidate) && seen.Add(normalizedCandidate))
                yield return normalizedCandidate;
        }
    }

    private static IEnumerable<string> EnumerateStrictCustomerNameCandidates(string normalizedName)
    {
        yield return normalizedName;

        var normalizedBracketCandidate = normalizedName
            .Replace('｛', '[')
            .Replace('｝', ']')
            .Replace('{', '[')
            .Replace('}', ']')
            .Trim();
        if (!string.Equals(normalizedBracketCandidate, normalizedName, StringComparison.Ordinal))
            yield return normalizedBracketCandidate;

        if (!WorkbookCustomerAliasMap.TryGetValue(normalizedName, out var aliases))
            yield break;

        foreach (var alias in aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias.Trim();
        }
    }

    private async Task<List<int>> GetAlertDayValuesAsync(CancellationToken ct)
    {
        var text = await GetAlertDaysTextAsync(ct);
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) ? day : -1)
            .Where(day => day >= 0)
            .Distinct()
            .OrderBy(day => day)
            .ToList();
    }

    private async Task UpsertSettingAsync(string key, string value, CancellationToken ct)
    {
        var setting = await _db.Settings.FindAsync([key], ct);
        if (setting is null)
            _db.Settings.Add(new LocalSetting { Key = key, Value = value });
        else
            setting.Value = value;

        await _db.SaveChangesAsync(ct);
    }

    private async Task RemoveSettingAsync(string key, CancellationToken ct)
    {
        var setting = await _db.Settings.FindAsync([key], ct);
        if (setting is null)
            return;

        _db.Settings.Remove(setting);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<TDraft?> GetDraftAsync<TDraft>(string key, CancellationToken ct)
        where TDraft : class
    {
        var payload = await _db.Settings.AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TDraft>(payload, RentalJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private string BuildDraftSettingKey(string prefix, SessionState session)
    {
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, DomainConstants.OfficeUsenet);
        var username = (session.User?.Username ?? "anonymous").Trim();
        if (string.IsNullOrWhiteSpace(username))
            username = "anonymous";

        return $"{prefix}.{officeCode}.{username}".ToUpperInvariant();
    }

    private async Task<IReadOnlyDictionary<string, string>> GetOfficeMapAsync(CancellationToken ct)
    {
        if (_officeMapCache is not null)
            return _officeMapCache;

        var offices = await _db.Offices.AsNoTracking().ToListAsync(ct);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var office in offices)
        {
            var code = NormalizeOfficeCode(office.Code, string.Empty);
            if (string.IsNullOrWhiteSpace(code))
                continue;

            map[code] = string.IsNullOrWhiteSpace(office.Name)
                ? ResolveDefaultOfficeName(code)
                : office.Name.Trim();
        }

        _officeMapCache = map;
        return _officeMapCache;
    }

    private string ResolveOfficeDisplayName(
        string? responsibleOfficeCode,
        string? legacyManagementCompanyCode,
        IReadOnlyDictionary<string, string> officeMap)
    {
        foreach (var candidate in new[] { responsibleOfficeCode, legacyManagementCompanyCode })
        {
            var normalizedCode = NormalizeOfficeCode(candidate, string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedCode))
                continue;

            if (officeMap.TryGetValue(normalizedCode, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;

            return ResolveDefaultOfficeName(normalizedCode);
        }

        return string.Empty;
    }

    private RentalAlertItem? ToAlertItem(
        LocalRentalBillingProfile profile,
        IReadOnlyDictionary<string, string> officeMap,
        DateOnly referenceDate)
    {
        NormalizeBillingSchedule(profile, referenceDate);
        var nextBillingDate = GetNextBillingDate(profile, referenceDate);
        if (!nextBillingDate.HasValue)
            return null;

        var documentIssueDate = RentalBillingScheduleRules.CalculateDocumentIssueDate(nextBillingDate, profile.DocumentIssueMode, profile.DocumentLeadDays);
        var alertDate = RentalBillingScheduleRules.ResolveAlertDate(nextBillingDate.Value, documentIssueDate);
        var alertReason = RentalBillingScheduleRules.ResolveAlertReason(nextBillingDate.Value, documentIssueDate);

        return new RentalAlertItem
        {
            BillingProfileId = profile.Id,
            ResponsibleOfficeName = ResolveOfficeDisplayName(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode, officeMap),
            CustomerName = profile.CustomerName,
            ItemName = profile.ItemName,
            MonthlyAmount = profile.MonthlyAmount,
            NextBillingDate = nextBillingDate.Value,
            DocumentIssueDate = documentIssueDate,
            AlertDate = alertDate,
            AlertReason = alertReason,
            DaysRemaining = alertDate.DayNumber - referenceDate.DayNumber,
            Severity = alertDate.DayNumber < referenceDate.DayNumber
                ? "지연"
                : alertDate == referenceDate ? "오늘" : "예정"
        };
    }

    private RentalExpiringAssetItem? ToExpiringAssetItem(
        LocalRentalAsset asset,
        IReadOnlyDictionary<string, string> officeMap,
        DateOnly referenceDate)
    {
        if (!asset.RentalEndDate.HasValue)
            return null;

        return new RentalExpiringAssetItem
        {
            AssetId = asset.Id,
            ManagementNumber = asset.ManagementNumber,
            ResponsibleOfficeName = ResolveOfficeDisplayName(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, officeMap),
            CustomerName = asset.CustomerName,
            ItemName = asset.ItemName,
            InstallLocation = asset.InstallLocation,
            RentalEndDate = asset.RentalEndDate,
            DaysRemaining = asset.RentalEndDate.Value.DayNumber - referenceDate.DayNumber
        };
    }

    private string BuildAlertPopupMessage(
        IReadOnlyList<RentalAlertItem> alertItems,
        IReadOnlyList<RentalExpiringAssetItem> expiringAssets)
    {
        if (alertItems.Count == 0 && expiringAssets.Count == 0)
            return string.Empty;

        var overdue = alertItems.Count(item => item.DaysRemaining < 0);
        var today = alertItems.Count(item => item.DaysRemaining == 0);
        var upcoming = alertItems.Count(item => item.DaysRemaining > 0);
        var expiring = expiringAssets.Count;

        return $"렌탈 알림\n" +
               $"- 지연 청구 {overdue:N0}건\n" +
               $"- 오늘 청구 {today:N0}건\n" +
               $"- 예정 청구 {upcoming:N0}건\n" +
               $"- 30일 내 만료 {expiring:N0}건";
    }

    public List<RentalBillingTemplateItemModel> GetBillingTemplateItems(LocalRentalBillingProfile profile, IReadOnlyList<LocalRentalAsset>? assets = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        assets ??= Array.Empty<LocalRentalAsset>();
        var profileBillingType = NormalizeBillingType(profile.BillingType);
        List<Guid>? legacyIncludedAssetIds = null;
        List<Guid> ResolveLegacyIncludedAssetIds()
        {
            legacyIncludedAssetIds ??= assets
                .Where(asset => asset.BillingProfileId == profile.Id)
                .Select(asset => asset.Id)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            return legacyIncludedAssetIds;
        }

        List<RentalBillingTemplateItemModel>? parsed = null;
        if (!string.IsNullOrWhiteSpace(profile.BillingTemplateJson))
        {
            try
            {
                parsed = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(profile.BillingTemplateJson, RentalJsonOptions);
            }
            catch
            {
                parsed = null;
            }
        }

        if (parsed is null || parsed.Count == 0)
        {
            var fallbackIncludedAssetIds = ResolveLegacyIncludedAssetIds();
            var representativeAssetId = profileBillingType == "묶음"
                ? fallbackIncludedAssetIds.FirstOrDefault(id => id != Guid.Empty)
                : Guid.Empty;
            parsed =
            [
                new RentalBillingTemplateItemModel
                {
                    DisplayItemName = string.IsNullOrWhiteSpace(profile.ItemName) ? "렌탈 임대료" : profile.ItemName,
                    BillingLineMode = ResolveTemplateBillingLineMode(string.Empty, profileBillingType),
                    RepresentativeAssetId = representativeAssetId == Guid.Empty ? null : representativeAssetId,
                    Quantity = 1m,
                    UnitPrice = Math.Max(0m, profile.MonthlyAmount),
                    Amount = Math.Max(0m, profile.MonthlyAmount),
                    IncludedAssetIds = fallbackIncludedAssetIds
                }
            ];
        }

        var normalized = new List<RentalBillingTemplateItemModel>();
        foreach (var current in parsed)
        {
            if (current is null)
                continue;

            var displayItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(current.DisplayItemName);
            var quantity = current.Quantity <= 0m ? 1m : current.Quantity;
            var inputAmount = Math.Max(0m, current.Amount);
            var unitPrice = ResolveTemplateUnitPrice(quantity, current.UnitPrice, inputAmount);
            var amount = CalculateTemplateLineAmount(quantity, unitPrice);
            var includedAssetIds = (current.IncludedAssetIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            var billingLineMode = ResolveTemplateBillingLineMode(current.BillingLineMode, profileBillingType);
            var representativeAssetId = current.RepresentativeAssetId.HasValue &&
                                        includedAssetIds.Contains(current.RepresentativeAssetId.Value) &&
                                        string.Equals(billingLineMode, "묶음", StringComparison.OrdinalIgnoreCase)
                ? current.RepresentativeAssetId
                : null;
            normalized.Add(new RentalBillingTemplateItemModel
            {
                ItemId = current.ItemId == Guid.Empty ? Guid.NewGuid() : current.ItemId,
                DisplayItemName = string.IsNullOrWhiteSpace(displayItemName) ? "렌탈 임대료" : displayItemName,
                BillingLineMode = billingLineMode,
                Specification = (current.Specification ?? string.Empty).Trim(),
                Unit = (current.Unit ?? string.Empty).Trim(),
                MaterialNumber = (current.MaterialNumber ?? string.Empty).Trim(),
                RepresentativeAssetId = representativeAssetId,
                Quantity = quantity,
                UnitPrice = unitPrice,
                Amount = Math.Max(0m, amount),
                Note = (current.Note ?? string.Empty).Trim(),
                IncludedAssetIds = includedAssetIds
            });
        }

        if (normalized.Count == 1 &&
            normalized.All(item => item.IncludedAssetIds.Count == 0))
        {
            var fallbackIncludedAssetIds = ResolveLegacyIncludedAssetIds();
            if (fallbackIncludedAssetIds.Count > 0)
            {
                normalized[0].IncludedAssetIds = fallbackIncludedAssetIds;
                var currentRepresentativeAssetId = normalized[0].RepresentativeAssetId;
                if (string.Equals(normalized[0].BillingLineMode, "묶음", StringComparison.OrdinalIgnoreCase) &&
                    (!currentRepresentativeAssetId.HasValue ||
                     !fallbackIncludedAssetIds.Contains(currentRepresentativeAssetId.GetValueOrDefault())))
                {
                    var representativeAssetId = fallbackIncludedAssetIds.FirstOrDefault(id => id != Guid.Empty);
                    normalized[0].RepresentativeAssetId = representativeAssetId == Guid.Empty ? null : representativeAssetId;
                }
            }
        }

        if (normalized.Count == 0)
        {
            var fallbackIncludedAssetIds = ResolveLegacyIncludedAssetIds();
            var representativeAssetId = profileBillingType == "묶음"
                ? fallbackIncludedAssetIds.FirstOrDefault(id => id != Guid.Empty)
                : Guid.Empty;
            normalized.Add(new RentalBillingTemplateItemModel
            {
                DisplayItemName = string.IsNullOrWhiteSpace(profile.ItemName) ? "렌탈 임대료" : profile.ItemName,
                BillingLineMode = ResolveTemplateBillingLineMode(string.Empty, profileBillingType),
                RepresentativeAssetId = representativeAssetId == Guid.Empty ? null : representativeAssetId,
                Quantity = 1m,
                UnitPrice = Math.Max(0m, profile.MonthlyAmount),
                Amount = Math.Max(0m, profile.MonthlyAmount),
                IncludedAssetIds = fallbackIncludedAssetIds
            });
        }

        return normalized;
    }

    public string SerializeBillingTemplateItems(IEnumerable<RentalBillingTemplateItemModel> items)
        => JsonSerializer.Serialize((items ?? Enumerable.Empty<RentalBillingTemplateItemModel>()).ToList(), RentalJsonOptions);

    public List<RentalBillingRunModel> GetBillingRuns(LocalRentalBillingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.BillingRunsJson))
            return new List<RentalBillingRunModel>();

        try
        {
            return JsonSerializer.Deserialize<List<RentalBillingRunModel>>(profile.BillingRunsJson, RentalJsonOptions) ?? new List<RentalBillingRunModel>();
        }
        catch
        {
            return new List<RentalBillingRunModel>();
        }
    }

    public RentalBillingRunModel? GetOrCreateBillingRun(
        LocalRentalBillingProfile profile,
        DateOnly referenceDate,
        bool persistChanges)
        => GetOrCreateBillingRun(profile, referenceDate, persistChanges, null, null);

    private RentalBillingRunModel? GetOrCreateBillingRun(
        LocalRentalBillingProfile profile,
        DateOnly referenceDate,
        bool persistChanges,
        IReadOnlyList<RentalBillingTemplateItemModel>? templateItemsOverride,
        List<RentalBillingRunModel>? runsOverride)
    {
        ArgumentNullException.ThrowIfNull(profile);
        NormalizeBillingSchedule(profile, referenceDate);
        var scheduledDate = GetNextBillingDate(profile, referenceDate);
        if (!scheduledDate.HasValue)
            return null;

        var templateItems = templateItemsOverride?.ToList() ?? GetBillingTemplateItems(profile);
        var cycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(profile.BillingCycleMonths);
        var period = ResolveBillingPeriod(profile, scheduledDate.Value, cycleMonths);
        var runKey = $"{period.StartDate:yyyyMMdd}-{period.EndDate:yyyyMMdd}";
        var runs = runsOverride ?? GetBillingRuns(profile);
        var existing = runs.FirstOrDefault(run => string.Equals(run.RunKey, runKey, StringComparison.OrdinalIgnoreCase));
        var billedAmount = templateItems.Sum(item => ResolveTemplateMonthlyAmount(item)) * cycleMonths;
        var deterministicRunId = SyncIdentityGenerator.CreateRentalBillingRunId(profile.Id, runKey);
        if (existing is null)
        {
            existing = new RentalBillingRunModel
            {
                RunId = deterministicRunId == Guid.Empty ? Guid.NewGuid() : deterministicRunId,
                RunKey = runKey,
                ScheduledDate = scheduledDate.Value,
                PeriodStartDate = period.StartDate,
                PeriodEndDate = period.EndDate,
                CycleMonths = cycleMonths,
                PeriodLabel = BuildBillingPeriodLabel(period.StartDate, period.EndDate),
                Status = PaymentFlowConstants.BillingStatusPlanned,
                BilledAmount = billedAmount,
                SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
                Items = CloneTemplateItemsForRun(templateItems, cycleMonths)
            };
            runs.Add(existing);
        }
        else
        {
            var canRefreshExistingRun = IsMutableBillingRun(existing);
            existing.RunId = existing.RunId == Guid.Empty
                ? (deterministicRunId == Guid.Empty ? Guid.NewGuid() : deterministicRunId)
                : existing.RunId;
            if (canRefreshExistingRun)
            {
                existing.ScheduledDate = scheduledDate.Value;
                existing.PeriodStartDate = period.StartDate;
                existing.PeriodEndDate = period.EndDate;
                existing.CycleMonths = cycleMonths;
                existing.PeriodLabel = BuildBillingPeriodLabel(period.StartDate, period.EndDate);
                existing.BilledAmount = billedAmount;
                existing.Items = CloneTemplateItemsForRun(templateItems, cycleMonths);
            }
            else
            {
                if (existing.BilledAmount <= 0m)
                    existing.BilledAmount = billedAmount;
                if (existing.Items.Count == 0)
                    existing.Items = CloneTemplateItemsForRun(templateItems, existing.CycleMonths <= 0 ? cycleMonths : existing.CycleMonths);
            }
        }

        if (persistChanges)
            profile.BillingRunsJson = JsonSerializer.Serialize(runs, RentalJsonOptions);

        return existing;
    }

    private RentalBillingRunModel? FindBillingRunById(LocalRentalBillingProfile profile, Guid? billingRunId)
    {
        if (!billingRunId.HasValue || billingRunId.Value == Guid.Empty)
            return null;

        return GetBillingRuns(profile)
            .FirstOrDefault(run => run.RunId == billingRunId.Value);
    }

    private static bool IsMutableBillingRun(RentalBillingRunModel run)
    {
        var status = (run.Status ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(status) ||
               string.Equals(status, PaymentFlowConstants.BillingStatusPlanned, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "예정", StringComparison.OrdinalIgnoreCase);
    }

    private static List<RentalBillingTemplateItemModel> CloneTemplateItemsForRun(
        IEnumerable<RentalBillingTemplateItemModel> items,
        int cycleMonths)
        => (items ?? Enumerable.Empty<RentalBillingTemplateItemModel>())
            .Select(item => new RentalBillingTemplateItemModel
            {
                ItemId = item.ItemId == Guid.Empty ? Guid.NewGuid() : item.ItemId,
                DisplayItemName = item.DisplayItemName,
                BillingLineMode = item.BillingLineMode,
                Specification = item.Specification,
                Unit = item.Unit,
                MaterialNumber = item.MaterialNumber,
                RepresentativeAssetId = item.RepresentativeAssetId,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                Amount = ResolveTemplateMonthlyAmount(item),
                Note = item.Note,
                IncludedAssetIds = item.IncludedAssetIds?.Distinct().ToList() ?? new List<Guid>()
            })
            .ToList();

    private static decimal ResolveTemplateMonthlyAmount(RentalBillingTemplateItemModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var quantity = NormalizeTemplateQuantity(item.Quantity);
        var unitPrice = ResolveTemplateUnitPrice(quantity, item.UnitPrice, item.Amount);
        return CalculateTemplateLineAmount(quantity, unitPrice);
    }

    private static decimal NormalizeTemplateQuantity(decimal quantity)
        => quantity <= 0m ? 1m : quantity;

    private static decimal ResolveTemplateUnitPrice(decimal quantity, decimal unitPrice, decimal amount)
    {
        var normalizedUnitPrice = Math.Max(0m, unitPrice);
        if (normalizedUnitPrice > 0m)
            return normalizedUnitPrice;

        var normalizedAmount = Math.Max(0m, amount);
        return normalizedAmount > 0m
            ? normalizedAmount / NormalizeTemplateQuantity(quantity)
            : 0m;
    }

    private static decimal CalculateTemplateLineAmount(decimal quantity, decimal unitPrice)
        => Math.Max(0m, NormalizeTemplateQuantity(quantity)) * Math.Max(0m, unitPrice);

    private static string ResolveTemplateBillingLineMode(string? itemBillingLineMode, string? defaultBillingType)
    {
        var normalizedItemMode = NormalizeBillingLineMode(itemBillingLineMode);
        if (!string.IsNullOrWhiteSpace(normalizedItemMode))
            return normalizedItemMode;

        var normalizedDefault = NormalizeBillingType(defaultBillingType);
        return string.Equals(normalizedDefault, "혼합", StringComparison.OrdinalIgnoreCase)
            ? "묶음"
            : normalizedDefault;
    }

    private static string ResolveProfileBillingTypeFromTemplateItems(
        IEnumerable<RentalBillingTemplateItemModel> templateItems,
        string? defaultBillingType)
    {
        var modes = (templateItems ?? Enumerable.Empty<RentalBillingTemplateItemModel>())
            .Select(item => ResolveTemplateBillingLineMode(item.BillingLineMode, defaultBillingType))
            .Where(mode => !string.IsNullOrWhiteSpace(mode))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return modes.Count switch
        {
            0 => ResolveTemplateBillingLineMode(null, defaultBillingType),
            1 => modes[0],
            _ => "혼합"
        };
    }

    private static string NormalizeBillingLineMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized switch
        {
            "개별" => "개별",
            "묶음" => "묶음",
            _ => string.Empty
        };
    }

    private async Task<LocalInvoice?> GetActiveBillingInvoiceAsync(Guid billingRunId, CancellationToken ct)
    {
        if (billingRunId == Guid.Empty)
            return null;

        return await _db.Invoices
            .Include(invoice => invoice.Lines.Where(line => !line.IsDeleted))
            .Include(invoice => invoice.Payments.Where(payment => !payment.IsDeleted))
            .AsNoTracking()
            .Where(invoice => !invoice.IsDeleted &&
                              invoice.IsLatestVersion &&
                              invoice.LinkedRentalBillingRunId == billingRunId)
            .OrderByDescending(invoice => invoice.LastSavedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<(bool Success, string Message, List<LocalInvoiceLine> Lines)> BuildRentalBillingInvoiceLinesAsync(
        LocalRentalBillingProfile profile,
        RentalBillingRunModel run,
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems,
        SessionState session,
        CancellationToken ct)
    {
        if (templateItems.Count == 0)
            return (false, "청구항목이 없어 판매전표를 만들 수 없습니다.", new List<LocalInvoiceLine>());

        var billingMonths = BuildBillingMonths(run);
        if (billingMonths.Count == 0)
            return (false, "청구월 정보를 계산할 수 없어 판매전표를 만들 수 없습니다.", new List<LocalInvoiceLine>());
        if (billingMonths.Count != Math.Max(1, run.CycleMonths))
        {
            return (false,
                $"청구월 계산 결과({billingMonths.Count}개월)가 청구주기({Math.Max(1, run.CycleMonths)}개월)와 맞지 않습니다.",
                new List<LocalInvoiceLine>());
        }

        var includedAssetIds = templateItems
            .SelectMany(item => item.IncludedAssetIds ?? Enumerable.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        var includedAssets = await LoadRentalAssetsByIdsAsync(
            includedAssetIds,
            ignoreQueryFilters: false,
            asNoTracking: true,
            excludeDeleted: true,
            ct);
        var assetsById = includedAssets.ToDictionary(asset => asset.Id);

        var lines = new List<LocalInvoiceLine>();
        var profileBillingType = NormalizeBillingType(profile.BillingType);
        IReadOnlyList<LocalRentalAsset>? includedBillingAssets = null;
        IReadOnlyList<LocalRentalAsset>? billingAssetCandidates = null;

        foreach (var templateItem in templateItems)
        {
            var lineMode = ResolveTemplateBillingLineMode(templateItem.BillingLineMode, profileBillingType);

            if (string.IsNullOrWhiteSpace(lineMode))
            {
                return (false,
                    $"청구항목 '{templateItem.DisplayItemName}'의 청구 유형이 지정되지 않아 판매전표를 만들 수 없습니다.",
                    new List<LocalInvoiceLine>());
            }

            var templateAssetIds = (templateItem.IncludedAssetIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (templateAssetIds.Count == 0)
            {
                includedBillingAssets ??= await GetIncludedBillingAssetsAsync(
                    profile.Id,
                    Array.Empty<Guid>(),
                    profile.CustomerId,
                    profile.ResponsibleOfficeCode,
                    session,
                    ct);

                if (includedBillingAssets.Count > 0)
                {
                    templateItem.IncludedAssetIds ??= new List<Guid>();
                    foreach (var linkedAsset in includedBillingAssets)
                    {
                        if (!templateItem.IncludedAssetIds.Contains(linkedAsset.Id))
                            templateItem.IncludedAssetIds.Add(linkedAsset.Id);

                        if (!assetsById.ContainsKey(linkedAsset.Id))
                            assetsById[linkedAsset.Id] = linkedAsset;
                    }

                    templateAssetIds = templateItem.IncludedAssetIds
                        .Where(id => id != Guid.Empty)
                        .Distinct()
                        .ToList();
                }
            }

            if (templateAssetIds.Count == 0)
            {
                billingAssetCandidates ??= await GetBillingAssetCandidatesAsync(
                    profile.Id,
                    profile.CustomerId,
                    profile.CustomerName,
                    profile.ResponsibleOfficeCode,
                    includeOfficePoolAssets: false,
                    session,
                    ct);

                if (billingAssetCandidates.Count == 1 || CanAutoLinkAllBillingCandidates(profile.CustomerId, billingAssetCandidates))
                {
                    templateItem.IncludedAssetIds ??= new List<Guid>();
                    foreach (var candidateAsset in billingAssetCandidates)
                    {
                        if (!templateItem.IncludedAssetIds.Contains(candidateAsset.Id))
                            templateItem.IncludedAssetIds.Add(candidateAsset.Id);

                        if (!assetsById.ContainsKey(candidateAsset.Id))
                            assetsById[candidateAsset.Id] = candidateAsset;
                    }

                    templateAssetIds = templateItem.IncludedAssetIds
                        .Where(id => id != Guid.Empty)
                        .Distinct()
                        .ToList();
                }

                if (templateAssetIds.Count == 0 && billingAssetCandidates.Count > 0)
                {
                    return (false,
                $"청구항목 '{templateItem.DisplayItemName}'에 연결된 설치장비가 없습니다. '새 장비연결'에서 설치현황 자산을 연결한 뒤 다시 시도하세요.",
                        new List<LocalInvoiceLine>());
                }

                if (templateAssetIds.Count == 0)
                {
                    return (false,
                        $"청구항목 '{templateItem.DisplayItemName}'에 연결된 설치장비가 없어 판매전표를 만들 수 없습니다.",
                        new List<LocalInvoiceLine>());
                }
            }

            var templateAssets = new List<LocalRentalAsset>();
            foreach (var assetId in templateAssetIds)
            {
                if (!assetsById.TryGetValue(assetId, out var asset))
                {
                    return (false,
                        $"청구항목 '{templateItem.DisplayItemName}'에 연결된 장비를 찾을 수 없어 판매전표를 만들 수 없습니다.",
                        new List<LocalInvoiceLine>());
                }

                if (asset.BillingProfileId.HasValue &&
                    asset.BillingProfileId.Value != profile.Id &&
                    RentalAssetCanTransferToBillingProfileScope(asset, profile.TenantCode))
                {
                    return (false,
                        $"장비 '{asset.ItemName}'가 다른 렌탈 청구설정에 연결되어 있어 판매전표를 만들 수 없습니다.",
                        new List<LocalInvoiceLine>());
                }

                var eligibilityStatus = string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus)
                    ? GetDefaultBillingEligibilityStatus(asset)
                    : asset.BillingEligibilityStatus.Trim();

                if (string.Equals(eligibilityStatus, BillingEligibilityExcluded, StringComparison.OrdinalIgnoreCase) ||
                    RentalAssetStatusRules.IsNonOperating(asset.AssetStatus))
                {
                    return (false,
                        $"장비 '{asset.ItemName}'는 청구제외 상태라 판매전표를 만들 수 없습니다.",
                        new List<LocalInvoiceLine>());
                }

                templateAssets.Add(asset);
            }

            if (string.Equals(lineMode, "묶음", StringComparison.OrdinalIgnoreCase))
            {
                var representativeAsset = SelectRepresentativeBillingAsset(templateAssets, templateItem.RepresentativeAssetId);
                if (representativeAsset is null)
                {
                    return (false,
                        $"청구항목 '{templateItem.DisplayItemName}'의 대표 장비명을 정할 수 없어 판매전표를 만들 수 없습니다.",
                        new List<LocalInvoiceLine>());
                }

                var monthlyAmount = ResolveTemplateMonthlyAmount(templateItem);
                foreach (var billingMonth in billingMonths)
                {
                    var specification = ResolveBundleInvoiceSpecification(templateItem, representativeAsset, templateAssets);
                    lines.Add(new LocalInvoiceLine
                    {
                        Id = Guid.NewGuid(),
                        ItemId = null,
                        ItemTrackingType = ItemTrackingTypes.NonStock,
                        ItemNameOriginal = BuildMonthlyRentalInvoiceItemName(billingMonth, templateItem.DisplayItemName),
                        SpecificationOriginal = specification,
                        Unit = (templateItem.Unit ?? string.Empty).Trim(),
                        Quantity = 1m,
                        UnitPrice = monthlyAmount,
                        LineAmount = monthlyAmount,
                        OrderIndex = lines.Count + 1,
                        MaterialNumber = FirstNonEmpty(templateItem.MaterialNumber, representativeAsset.ManagementNumber),
                        SerialNumber = representativeAsset.MachineNumber?.Trim() ?? string.Empty,
                        InstallLocation = FirstNonEmpty(representativeAsset.InstallLocation, representativeAsset.InstallSiteName),
                        Remark = (templateItem.Note ?? string.Empty).Trim()
                    });
                }

                continue;
            }

            foreach (var asset in templateAssets)
            {
                if (asset.MonthlyFee <= 0m)
                {
                    return (false,
                        $"장비 '{asset.ItemName}'의 월 청구금액이 없어 판매전표를 만들 수 없습니다.",
                        new List<LocalInvoiceLine>());
                }
            }

            foreach (var billingMonth in billingMonths)
            {
                foreach (var asset in templateAssets)
                {
                    var quantity = 1m;
                    var unitPrice = asset.MonthlyFee;
                    lines.Add(new LocalInvoiceLine
                    {
                        Id = Guid.NewGuid(),
                        ItemId = null,
                        ItemTrackingType = ItemTrackingTypes.NonStock,
                        ItemNameOriginal = BuildMonthlyRentalInvoiceItemName(billingMonth, templateItem.DisplayItemName),
                        SpecificationOriginal = ResolveIndividualInvoiceSpecification(templateItem, asset),
                        Unit = (templateItem.Unit ?? string.Empty).Trim(),
                        Quantity = quantity,
                        UnitPrice = unitPrice,
                        LineAmount = quantity * unitPrice,
                        OrderIndex = lines.Count + 1,
                        MaterialNumber = FirstNonEmpty(templateItem.MaterialNumber, asset.ManagementNumber),
                        SerialNumber = asset.MachineNumber?.Trim() ?? string.Empty,
                        InstallLocation = FirstNonEmpty(asset.InstallLocation, asset.InstallSiteName),
                        Remark = (templateItem.Note ?? string.Empty).Trim()
                    });
                }
            }
        }

        if (lines.Count == 0)
            return (false, "판매전표에 넣을 청구라인을 만들지 못했습니다.", new List<LocalInvoiceLine>());

        return (true, string.Empty, lines);
    }

    private static bool CanAutoLinkAllBillingCandidates(Guid? customerId, IReadOnlyCollection<LocalRentalAsset> candidates)
    {
        if (!customerId.HasValue || customerId.Value == Guid.Empty || candidates.Count <= 1)
            return false;

        var expectedCustomerId = customerId.Value;
        return candidates.All(asset => asset.CustomerId == expectedCustomerId);
    }

    private static List<DateOnly> BuildBillingMonths(RentalBillingRunModel run)
    {
        var months = new List<DateOnly>();
        var current = new DateOnly(run.PeriodStartDate.Year, run.PeriodStartDate.Month, 1);
        var end = new DateOnly(run.PeriodEndDate.Year, run.PeriodEndDate.Month, 1);
        while (current <= end)
        {
            months.Add(current);
            current = current.AddMonths(1);
        }

        return months;
    }

    private static LocalRentalAsset? SelectRepresentativeBillingAsset(IReadOnlyList<LocalRentalAsset> assets)
        => assets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.ItemName))
            .OrderBy(asset => asset.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.MachineNumber, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();

    private static LocalRentalAsset? SelectRepresentativeBillingAsset(
        IReadOnlyList<LocalRentalAsset> assets,
        Guid? representativeAssetId)
    {
        if (representativeAssetId.HasValue && representativeAssetId.Value != Guid.Empty)
        {
            var selected = assets.FirstOrDefault(asset => asset.Id == representativeAssetId.Value);
            if (selected is not null)
                return selected;
        }

        return SelectRepresentativeBillingAsset(assets);
    }

    private static string BuildMonthlyRentalInvoiceItemName(DateOnly billingMonth, string? displayItemName = null)
    {
        // 렌탈 청구 전표의 품명은 장비 모델명이 아니라 거래명세서에서 통일해 쓰는 청구 항목명으로 고정한다.
        return $"사무기기 렌탈대금[{billingMonth.Month}월]";
    }

    private static bool NormalizeRentalBillingInvoiceLineItemNames(LocalInvoice invoice, RentalBillingRunModel run)
    {
        var billingMonths = BuildBillingMonths(run);
        if (billingMonths.Count == 0)
            return false;

        var activeLines = (invoice.Lines ?? new List<LocalInvoiceLine>())
            .Where(line => !line.IsDeleted)
            .OrderBy(BuildRentalBillingInvoiceLineComparisonKey, StringComparer.Ordinal)
            .ToList();
        if (activeLines.Count == 0)
            return false;

        var linesPerMonth = activeLines.Count >= billingMonths.Count && activeLines.Count % billingMonths.Count == 0
            ? Math.Max(1, activeLines.Count / billingMonths.Count)
            : 1;

        var changed = false;
        for (var index = 0; index < activeLines.Count; index++)
        {
            var monthIndex = Math.Min(index / linesPerMonth, billingMonths.Count - 1);
            var expectedName = BuildMonthlyRentalInvoiceItemName(billingMonths[monthIndex]);
            if (string.Equals(activeLines[index].ItemNameOriginal, expectedName, StringComparison.Ordinal))
                continue;

            activeLines[index].ItemNameOriginal = expectedName;
            changed = true;
        }

        return changed;
    }

    private static bool ShouldRebuildRentalBillingInvoiceLines(
        LocalInvoice invoice,
        IReadOnlyList<LocalInvoiceLine> expectedLines)
    {
        var activeLines = (invoice.Lines ?? new List<LocalInvoiceLine>())
            .Where(line => !line.IsDeleted)
            .OrderBy(ResolveRentalInvoiceLineSortOrder)
            .ThenBy(BuildRentalBillingInvoiceLineComparisonKey, StringComparer.Ordinal)
            .ToList();
        var expectedOrderedLines = expectedLines
            .OrderBy(ResolveRentalInvoiceLineSortOrder)
            .ThenBy(BuildRentalBillingInvoiceLineComparisonKey, StringComparer.Ordinal)
            .ToList();
        if (activeLines.Count != expectedOrderedLines.Count)
            return true;

        for (var index = 0; index < activeLines.Count; index++)
        {
            if (!AreRentalBillingInvoiceLinesEquivalent(activeLines[index], expectedOrderedLines[index]))
                return true;
        }

        return false;
    }

    private static string BuildRentalBillingInvoiceLineComparisonKey(LocalInvoiceLine line)
        => string.Join(
            "\u001f",
            (line.ItemNameOriginal ?? string.Empty).Trim(),
            (line.SpecificationOriginal ?? string.Empty).Trim(),
            (line.Unit ?? string.Empty).Trim(),
            line.Quantity.ToString(CultureInfo.InvariantCulture),
            line.UnitPrice.ToString(CultureInfo.InvariantCulture),
            line.LineAmount.ToString(CultureInfo.InvariantCulture),
            (line.MaterialNumber ?? string.Empty).Trim(),
            (line.SerialNumber ?? string.Empty).Trim(),
            (line.InstallLocation ?? string.Empty).Trim(),
            (line.Remark ?? string.Empty).Trim());

    private static int ResolveRentalInvoiceLineSortOrder(LocalInvoiceLine line)
        => line.OrderIndex > 0 ? line.OrderIndex : int.MaxValue;

    private static bool AreRentalBillingInvoiceLinesEquivalent(LocalInvoiceLine current, LocalInvoiceLine expected)
        => string.Equals((current.ItemNameOriginal ?? string.Empty).Trim(), (expected.ItemNameOriginal ?? string.Empty).Trim(), StringComparison.Ordinal) &&
           string.Equals((current.SpecificationOriginal ?? string.Empty).Trim(), (expected.SpecificationOriginal ?? string.Empty).Trim(), StringComparison.Ordinal) &&
           string.Equals((current.Unit ?? string.Empty).Trim(), (expected.Unit ?? string.Empty).Trim(), StringComparison.Ordinal) &&
           current.Quantity == expected.Quantity &&
           current.UnitPrice == expected.UnitPrice &&
           current.LineAmount == expected.LineAmount &&
           string.Equals((current.MaterialNumber ?? string.Empty).Trim(), (expected.MaterialNumber ?? string.Empty).Trim(), StringComparison.Ordinal) &&
           string.Equals((current.SerialNumber ?? string.Empty).Trim(), (expected.SerialNumber ?? string.Empty).Trim(), StringComparison.Ordinal) &&
           string.Equals((current.InstallLocation ?? string.Empty).Trim(), (expected.InstallLocation ?? string.Empty).Trim(), StringComparison.Ordinal) &&
           string.Equals((current.Remark ?? string.Empty).Trim(), (expected.Remark ?? string.Empty).Trim(), StringComparison.Ordinal);

    private static bool HasRentalInvoiceSettlement(LocalInvoice invoice)
        => invoice.TaxInvoiceIssued ||
           (invoice.Payments ?? new List<LocalPayment>())
           .Where(payment => !payment.IsDeleted)
           .Sum(payment => payment.Amount) > 0m;

    private static string ResolveIndividualInvoiceSpecification(RentalBillingTemplateItemModel templateItem, LocalRentalAsset asset)
    {
        var generatedSpecification = BuildAssetInvoiceSpecification(asset);
        var explicitSpecification = (templateItem.Specification ?? string.Empty).Trim();
        var legacySpecification = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
        return ShouldUseExplicitInvoiceSpecification(explicitSpecification, generatedSpecification, legacySpecification)
            ? explicitSpecification
            : FirstNonEmpty(generatedSpecification, explicitSpecification, legacySpecification);
    }

    private static string ResolveBundleInvoiceSpecification(
        RentalBillingTemplateItemModel templateItem,
        LocalRentalAsset representativeAsset,
        IReadOnlyCollection<LocalRentalAsset> templateAssets)
    {
        var generatedSpecification = BuildBundleInvoiceSpecification(
            representativeAsset,
            templateAssets,
            templateItem.DisplayItemName);
        var explicitSpecification = (templateItem.Specification ?? string.Empty).Trim();
        var legacySpecification = BuildLegacyBundleInvoiceSpecification(representativeAsset, templateAssets);
        if (ShouldUseExplicitInvoiceSpecification(explicitSpecification, generatedSpecification, legacySpecification))
        {
            return ShouldSuppressExplicitBundleInvoiceSpecification(
                    explicitSpecification,
                    representativeAsset,
                    templateAssets,
                    templateItem.DisplayItemName)
                ? generatedSpecification
                : explicitSpecification;
        }

        return generatedSpecification;
    }

    private static string BuildBundleInvoiceSpecification(
        LocalRentalAsset representativeAsset,
        IReadOnlyCollection<LocalRentalAsset> templateAssets,
        string? fallbackItemName = null)
    {
        var representativeName = FirstNonEmpty(
            BuildAssetInvoiceSpecification(representativeAsset),
            RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(fallbackItemName ?? string.Empty),
            "대표 장비");
        var otherAssets = templateAssets
            .Where(asset => asset.Id != representativeAsset.Id)
            .ToList();
        if (otherAssets.Count == 0)
            return representativeName;

        return $"{representativeName} 외";
    }

    private static bool ShouldSuppressExplicitBundleInvoiceSpecification(
        string explicitSpecification,
        LocalRentalAsset representativeAsset,
        IReadOnlyCollection<LocalRentalAsset> templateAssets,
        string? fallbackItemName = null)
    {
        if (string.IsNullOrWhiteSpace(explicitSpecification))
            return false;

        var otherAssets = templateAssets
            .Where(asset => asset.Id != representativeAsset.Id)
            .ToList();
        if (otherAssets.Count == 0)
            return false;

        var representativeName = FirstNonEmpty(
            BuildAssetInvoiceSpecification(representativeAsset),
            RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(fallbackItemName ?? string.Empty),
            "대표 장비");
        var legacyRepresentativeName = FirstNonEmpty(
            RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(representativeAsset.ItemName),
            RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(fallbackItemName ?? string.Empty),
            "대표 장비");

        return IsAutoGeneratedBundleSpecification(explicitSpecification, representativeName) ||
               IsAutoGeneratedBundleSpecification(explicitSpecification, legacyRepresentativeName);
    }

    private static bool IsAutoGeneratedBundleSpecification(string specification, string representativeName)
    {
        if (string.IsNullOrWhiteSpace(specification) || string.IsNullOrWhiteSpace(representativeName))
            return false;

        var current = specification.Trim();
        var baseName = representativeName.Trim();
        return string.Equals(current, baseName, StringComparison.CurrentCultureIgnoreCase) ||
               current.StartsWith($"{baseName} 외 ", StringComparison.CurrentCultureIgnoreCase);
    }

    private static string BuildLegacyBundleInvoiceSpecification(
        LocalRentalAsset representativeAsset,
        IReadOnlyCollection<LocalRentalAsset> templateAssets)
    {
        var representativeName = FirstNonEmpty(
            RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(representativeAsset.ItemName),
            "대표 장비");
        var otherAssets = templateAssets
            .Where(asset => asset.Id != representativeAsset.Id)
            .ToList();
        if (otherAssets.Count == 0)
            return representativeName;

        var distinctOtherCategories = otherAssets
            .Select(asset => (asset.ItemCategoryName ?? string.Empty).Trim())
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (distinctOtherCategories.Count == 1)
            return $"{representativeName} 외 {distinctOtherCategories[0]}";

        return $"{representativeName} 외 {otherAssets.Count:N0}대";
    }

    private static bool ShouldUseExplicitInvoiceSpecification(
        string explicitSpecification,
        string generatedSpecification,
        string legacySpecification)
    {
        if (string.IsNullOrWhiteSpace(explicitSpecification))
            return false;

        if (string.Equals(explicitSpecification, generatedSpecification, StringComparison.CurrentCultureIgnoreCase))
            return true;

        if (IsInvoiceSpecificationPlaceholder(explicitSpecification))
            return false;

        return string.IsNullOrWhiteSpace(legacySpecification) ||
               string.Equals(legacySpecification, generatedSpecification, StringComparison.CurrentCultureIgnoreCase) ||
               !string.Equals(explicitSpecification, legacySpecification, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsInvoiceSpecificationPlaceholder(string specification)
        => string.Equals(specification, "대표 장비", StringComparison.CurrentCultureIgnoreCase) ||
           string.Equals(specification.Trim(), "대표 장비 외", StringComparison.CurrentCultureIgnoreCase) ||
           specification.Trim().StartsWith("대표 장비 외 ", StringComparison.CurrentCultureIgnoreCase) ||
           string.Equals(specification, "장비별 개별 표시", StringComparison.CurrentCultureIgnoreCase);

    private static string BuildAssetInvoiceSpecification(LocalRentalAsset asset)
    {
        var normalizedItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
        var normalizedManufacturer = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.Manufacturer);
        if (string.IsNullOrWhiteSpace(normalizedManufacturer) || string.IsNullOrWhiteSpace(normalizedItemName))
            return normalizedItemName;

        var itemKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedItemName);
        var manufacturerKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedManufacturer);
        if (string.IsNullOrWhiteSpace(manufacturerKey) ||
            itemKey.StartsWith(manufacturerKey, StringComparison.OrdinalIgnoreCase) ||
            itemKey.Contains(manufacturerKey, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedItemName;
        }

        return $"{normalizedManufacturer} {normalizedItemName}".Trim();
    }

    private static string BuildProfileItemName(LocalRentalBillingProfile profile, IReadOnlyList<RentalBillingTemplateItemModel> templateItems)
    {
        if (templateItems.Count == 0)
            return RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(profile.ItemName);

        if (templateItems.Count == 1)
            return templateItems[0].DisplayItemName;

        return $"{templateItems[0].DisplayItemName} 외 {templateItems.Count - 1}건";
    }

    private static string NormalizeBillingType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized switch
        {
            "개별" => "개별",
            "혼합" => "혼합",
            _ => "묶음"
        };
    }

    private static string NormalizeBillingAdvanceMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.Equals(normalized, "선불", StringComparison.OrdinalIgnoreCase) ? "선불" : "후불";
    }

    private static DateOnly NormalizeReferenceDate(DateOnly referenceDate)
        => referenceDate == default
            ? DateOnly.FromDateTime(DateTime.Today)
            : referenceDate;

    private static void NormalizeBillingSchedule(LocalRentalBillingProfile profile, DateOnly referenceDate)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var normalizedReference = NormalizeReferenceDate(referenceDate);
        profile.BillingDayMode = RentalBillingScheduleRules.NormalizeBillingDayMode(profile.BillingDayMode);
        profile.BillingDay = RentalBillingScheduleRules.NormalizeBillingDay(profile.BillingDay);
        profile.BillingCycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(profile.BillingCycleMonths);
        profile.BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            profile.BillingCycleMonths,
            profile.BillingAnchorMonth,
            profile.BillingAnchorDate,
            profile.BillingStartDate,
            profile.ContractStartDate,
            profile.ContractDate,
            profile.LastBilledDate,
            normalizedReference);
        profile.DocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(profile.DocumentIssueMode);
        profile.DocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(profile.DocumentLeadDays);
    }

    private static string BuildBillingMonthDeniedMessage(
        LocalRentalBillingProfile profile,
        DateOnly referenceDate,
        DateOnly? nextBillingDate)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var dayModeText = string.Equals(
            RentalBillingScheduleRules.NormalizeBillingDayMode(profile.BillingDayMode),
            RentalBillingScheduleRules.BillingDayModeEndOfMonth,
            StringComparison.Ordinal)
            ? "말일"
            : $"매월 {RentalBillingScheduleRules.NormalizeBillingDay(profile.BillingDay)}일";
        var nextDateText = nextBillingDate.HasValue
            ? $"다음 결제예정일은 {nextBillingDate.Value:yyyy-MM-dd}입니다."
            : "다음 결제예정일을 계산할 수 없습니다.";
        return $"이 렌탈은 {RentalBillingScheduleRules.NormalizeCycleMonths(profile.BillingCycleMonths)}개월 {NormalizeBillingAdvanceMode(profile.BillingAdvanceMode)} / {dayModeText} / 기준월 {profile.BillingAnchorMonth}월로 계산되어 {NormalizeReferenceDate(referenceDate):yyyy-MM-dd}에는 청구할 수 없습니다. {nextDateText}";
    }

    private static (DateOnly StartDate, DateOnly EndDate) ResolveBillingPeriod(LocalRentalBillingProfile profile, DateOnly scheduledDate, int cycleMonths)
        => RentalBillingScheduleRules.ResolveBillingPeriod(
            RentalBillingScheduleRules.NormalizeCycleMonths(cycleMonths),
            NormalizeBillingAdvanceMode(profile.BillingAdvanceMode),
            scheduledDate);

    private static string BuildBillingPeriodLabel(DateOnly startDate, DateOnly endDate)
        => startDate == endDate || (startDate.Year == endDate.Year && startDate.Month == endDate.Month)
            ? $"{startDate:yyyy-MM}"
            : $"{startDate:yyyy-MM} ~ {endDate:yyyy-MM}";

    private static string DetermineBillingSettlementStatus(LocalRentalBillingProfile profile, decimal settledAmount, decimal billedAmount)
    {
        if (settledAmount <= 0m)
            return PaymentFlowConstants.GetPendingSettlementStatus(profile.BillingMethod);
        if (settledAmount < billedAmount)
            return PaymentFlowConstants.SettlementStatusPartial;
            return PaymentFlowConstants.GetDisplaySettlementCompleteStatus(profile.BillingMethod);
    }

    private List<string> BuildBillingDataIssues(
        LocalRentalBillingProfile profile,
        IReadOnlyList<LocalRentalAsset> assets,
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems)
        => BuildBillingDataIssues(
            profile,
            BuildBillingAssetRowSummary(profile, assets),
            assets,
            templateItems);

    private List<string> BuildBillingDataIssues(
        LocalRentalBillingProfile profile,
        RentalBillingAssetRowSummary assetSummary,
        IReadOnlyList<LocalRentalAsset> assets,
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems)
    {
        var issues = new List<string>();
        var profileBillingType = NormalizeBillingType(profile.BillingType);
        var templateIssueSummary = BuildBillingTemplateIssueSummary(templateItems, profileBillingType);
        if (templateIssueSummary.TemplateItemCount == 0)
            issues.Add("표시품목 없음");
        if (assetSummary.AssetCount == 0)
            issues.Add("연결장비 없음");
        if (string.IsNullOrWhiteSpace(profile.InstallSiteName))
            issues.Add("설치위치 미설정");
        if (templateIssueSummary.HasUnlinkedTemplateItem)
            issues.Add("장비 미연결 품목");
        if (assetSummary.HasMissingMonthlyFee)
            issues.Add("장비 월요금 없음");
        if (HasBillingAssetMonthlyFeeMismatch(assets, templateItems))
            issues.Add("자산/청구 월요금 불일치");
        if (templateIssueSummary.HasMissingBillingLineMode)
        {
            issues.Add("청구 유형 미지정");
        }
        if (assetSummary.HasEligibilityReviewRequired)
            issues.Add("청구대상 검토 필요");
        return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static RentalBillingTemplateIssueSummary BuildBillingTemplateIssueSummary(
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems,
        string profileBillingType)
    {
        var hasUnlinkedTemplateItem = false;
        var hasMissingBillingLineMode = false;

        foreach (var item in templateItems)
        {
            if (item.IncludedAssetIds.Count == 0)
                hasUnlinkedTemplateItem = true;

            if (string.IsNullOrWhiteSpace(ResolveTemplateBillingLineMode(item.BillingLineMode, profileBillingType)))
                hasMissingBillingLineMode = true;

            if (hasUnlinkedTemplateItem && hasMissingBillingLineMode)
                break;
        }

        return new RentalBillingTemplateIssueSummary(
            templateItems.Count,
            hasUnlinkedTemplateItem,
            hasMissingBillingLineMode);
    }

    private static int CountDistinctTemplateIncludedAssets(
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems)
    {
        HashSet<Guid>? includedAssetIds = null;
        foreach (var templateItem in templateItems)
        {
            if (templateItem.IncludedAssetIds.Count == 0)
                continue;

            includedAssetIds ??= new HashSet<Guid>();
            foreach (var assetId in templateItem.IncludedAssetIds)
                includedAssetIds.Add(assetId);
        }

        return includedAssetIds?.Count ?? 0;
    }

    private static List<Guid> CollectUnlinkedBillingCustomerIds(
        IReadOnlyList<LocalRentalAsset> unlinkedAssets)
    {
        HashSet<Guid>? customerIds = null;
        foreach (var asset in unlinkedAssets)
        {
            if (!asset.CustomerId.HasValue || asset.CustomerId.Value == Guid.Empty)
                continue;

            customerIds ??= new HashSet<Guid>();
            customerIds.Add(asset.CustomerId.Value);
        }

        return customerIds?.ToList() ?? new List<Guid>();
    }

    private static List<RentalBillingViewRow> ApplyBillingFinalRowFilters(
        List<RentalBillingViewRow> rows,
        RentalBillingFilter filter,
        int alertWindow)
    {
        if (rows.Count == 0 || (!filter.DueOnly && !filter.PastDueOnly))
            return rows;

        var filteredRows = new List<RentalBillingViewRow>(rows.Count);
        foreach (var row in rows)
        {
            if (filter.DueOnly &&
                (!row.DaysRemaining.HasValue || row.DaysRemaining.Value > alertWindow))
            {
                continue;
            }

            if (filter.PastDueOnly && !row.HasPastUnresolved)
                continue;

            filteredRows.Add(row);
        }

        return filteredRows;
    }

    private static List<RentalBillingViewRow> SortBillingRowsForDisplay(
        List<RentalBillingViewRow> rows)
    {
        if (rows.Count <= 1)
            return rows;

        return rows
            .OrderBy(row => row.DaysRemaining ?? int.MaxValue)
            .ThenBy(row => row.CustomerDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool HasBillingAssetMonthlyFeeMismatch(
        IReadOnlyList<LocalRentalAsset> assets,
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems)
    {
        if (templateItems.Count == 0)
            return false;

        Dictionary<Guid, decimal>? assetMonthlyFeeById = null;
        foreach (var templateItem in templateItems)
        {
            var includedAssetIds = (templateItem.IncludedAssetIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            if (includedAssetIds.Count == 0)
                continue;

            var templateMonthlyAmount = ResolveTemplateMonthlyAmount(templateItem);
            if (templateMonthlyAmount <= 0m)
                continue;

            assetMonthlyFeeById ??= assets
                .Where(asset => asset.Id != Guid.Empty && !RentalAssetStatusRules.IsNonOperating(asset.AssetStatus))
                .GroupBy(asset => asset.Id)
                .ToDictionary(
                    group => group.Key,
                    group => Math.Max(0m, group.First().MonthlyFee));
            if (assetMonthlyFeeById.Count == 0)
                return false;

            var assetMonthlyTotal = 0m;
            foreach (var assetId in includedAssetIds)
            {
                if (assetMonthlyFeeById.TryGetValue(assetId, out var assetMonthlyFee))
                    assetMonthlyTotal += assetMonthlyFee;
            }
            if (assetMonthlyTotal <= 0m)
                continue;

            if (assetMonthlyTotal != templateMonthlyAmount)
            {
                return true;
            }
        }

        return false;
    }

    private List<string> BuildUnlinkedBillingDataIssues(LocalRentalAsset asset)
    {
        var issues = new List<string> { "청구 프로필 미생성" };
        issues.AddRange(BuildAssetDataIssues(asset));
        return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> BuildAssetDataIssues(LocalRentalAsset asset)
    {
        var issues = new List<string>();
        if (!RentalAssetStatusRules.IsNonOperating(asset.AssetStatus) &&
            string.IsNullOrWhiteSpace(asset.CurrentCustomerName) && string.IsNullOrWhiteSpace(asset.CustomerName))
            issues.Add("현재거래처 없음");
        if (!RentalAssetStatusRules.IsNonOperating(asset.AssetStatus) &&
            string.IsNullOrWhiteSpace(asset.InstallSiteName) && string.IsNullOrWhiteSpace(asset.InstallLocation))
            issues.Add("설치위치 불명");
        if (!RentalAssetStatusRules.IsNonOperating(asset.AssetStatus) &&
            string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus))
            issues.Add("청구상태 미확정");
        if (!RentalAssetStatusRules.IsNonOperating(asset.AssetStatus) &&
            Math.Max(0m, asset.MonthlyFee) <= 0m)
            issues.Add("월요금 없음");
        if (RentalAssetStatusRules.IsNonOperating(asset.AssetStatus) && asset.BillingProfileId.HasValue)
            issues.Add("비운용장비 청구연결");
        return issues;
    }

    private async Task ApplyNonOperatingAssetStateRulesAsync(
        LocalRentalAsset asset,
        LocalRentalAsset? existing,
        CancellationToken ct)
    {
        if (!RentalAssetStatusRules.IsNonOperating(asset.AssetStatus))
        {
            PreserveLastAssignmentSnapshot(asset, existing);
            return;
        }

        var snapshotCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
            asset.CurrentCustomerName,
            asset.CustomerName,
            existing?.CurrentCustomerName,
            existing?.CustomerName));
        var snapshotInstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
            asset.InstallLocation,
            asset.InstallSiteName,
            existing?.InstallLocation,
            existing?.InstallSiteName));
        var snapshotBillingProfileId = asset.BillingProfileId ?? existing?.BillingProfileId;
        var hasAssignmentSnapshot = !string.IsNullOrWhiteSpace(snapshotCustomerName)
            || !string.IsNullOrWhiteSpace(snapshotInstallLocation)
            || snapshotBillingProfileId.HasValue;

        if (hasAssignmentSnapshot)
        {
            asset.LastCustomerName = snapshotCustomerName;
            asset.LastInstallLocation = snapshotInstallLocation;
            asset.LastBillingProfileId = snapshotBillingProfileId;
            asset.LastBillingProfileDisplay = await ResolveLastBillingProfileDisplayAsync(snapshotBillingProfileId, existing, ct);
            asset.LastAssignmentClearedAtUtc = DateTime.UtcNow;
        }
        else
        {
            PreserveLastAssignmentSnapshot(asset, existing);
        }

        asset.CustomerId = null;
        asset.CustomerName = string.Empty;
        asset.CurrentCustomerName = string.Empty;
        asset.InstallLocation = string.Empty;
        asset.InstallSiteName = string.Empty;
        asset.BillingProfileId = null;
        asset.BillingEligibilityStatus = BillingEligibilityExcluded;

        if (string.IsNullOrWhiteSpace(asset.BillingExclusionReason))
            asset.BillingExclusionReason = RentalAssetStatusRules.BuildAutoExclusionReason(asset.AssetStatus);
    }

    private static void PreserveLastAssignmentSnapshot(LocalRentalAsset asset, LocalRentalAsset? existing)
    {
        asset.LastCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(
            string.IsNullOrWhiteSpace(asset.LastCustomerName)
                ? existing?.LastCustomerName
                : asset.LastCustomerName);
        asset.LastInstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(
            string.IsNullOrWhiteSpace(asset.LastInstallLocation)
                ? existing?.LastInstallLocation
                : asset.LastInstallLocation);
        asset.LastBillingProfileId ??= existing?.LastBillingProfileId;
        asset.LastBillingProfileDisplay = RentalCatalogValueNormalizer.NormalizeDisplayText(
            string.IsNullOrWhiteSpace(asset.LastBillingProfileDisplay)
                ? existing?.LastBillingProfileDisplay
                : asset.LastBillingProfileDisplay);
        asset.LastAssignmentClearedAtUtc ??= existing?.LastAssignmentClearedAtUtc;
    }

    private async Task<string> ResolveLastBillingProfileDisplayAsync(
        Guid? billingProfileId,
        LocalRentalAsset? existing,
        CancellationToken ct)
    {
        if (!billingProfileId.HasValue || billingProfileId.Value == Guid.Empty)
            return string.Empty;

        if (existing is not null &&
            existing.LastBillingProfileId == billingProfileId &&
            !string.IsNullOrWhiteSpace(existing.LastBillingProfileDisplay))
        {
            return RentalCatalogValueNormalizer.NormalizeDisplayText(existing.LastBillingProfileDisplay);
        }

        var profile = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId.Value, ct);

        if (profile is null)
            return billingProfileId.Value.ToString("D");

        var customerName = RentalCatalogValueNormalizer.NormalizeDisplayText(profile.CustomerName);
        var itemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(profile.ItemName);
        if (!string.IsNullOrWhiteSpace(customerName) && !string.IsNullOrWhiteSpace(itemName))
            return $"{customerName} · {itemName}";
        if (!string.IsNullOrWhiteSpace(customerName))
            return customerName;
        if (!string.IsNullOrWhiteSpace(itemName))
            return itemName;

        return billingProfileId.Value.ToString("D");
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string GetDefaultBillingEligibilityStatus(LocalRentalAsset asset)
    {
        if (asset.BillingProfileId.HasValue && !RentalAssetStatusRules.IsNonOperating(asset.AssetStatus))
            return BillingEligibilityTarget;
        if (RentalAssetStatusRules.IsNonOperating(asset.AssetStatus))
            return BillingEligibilityExcluded;
        return BillingEligibilityUnconfirmed;
    }

    private static bool RentalAssetCanTransferToBillingProfileScope(
        LocalRentalAsset asset,
        string? profileTenantCode)
    {
        var assetOwnerOfficeCode = ResolveLinkedAssetManagementCompanyCode(asset, asset.ResponsibleOfficeCode);
        var assetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            asset.TenantCode,
            assetOwnerOfficeCode,
            asset.TenantCode,
            asset.ResponsibleOfficeCode);
        var normalizedProfileTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(
            profileTenantCode,
            assetTenantCode);

        return string.Equals(assetTenantCode, normalizedProfileTenantCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLinkedAssetManagementCompanyCode(LocalRentalAsset asset, string? fallbackOfficeCode)
    {
        var normalizedResponsibleOfficeCode = NormalizeOfficeCode(asset.ResponsibleOfficeCode, fallbackOfficeCode);
        var ownerOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
            FirstNonEmpty(asset.ManagementCompanyCode, asset.OfficeCode),
            normalizedResponsibleOfficeCode,
            fallbackOfficeCode);

        return string.IsNullOrWhiteSpace(ownerOfficeCode)
            ? NormalizeOfficeCode(fallbackOfficeCode, DomainConstants.OfficeUsenet)
            : ownerOfficeCode;
    }

    private async Task SyncLinkedBillingProfileMonthlyFeeFromAssetAsync(
        Guid assetId,
        CancellationToken ct)
    {
        if (assetId == Guid.Empty)
            return;

        var asset = await _db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == assetId && !current.IsDeleted, ct);
        if (asset is null ||
            !asset.BillingProfileId.HasValue ||
            asset.BillingProfileId.Value == Guid.Empty)
        {
            return;
        }

        var profile = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == asset.BillingProfileId.Value && !current.IsDeleted, ct);
        if (profile is null)
            return;

        var templateItems = GetBillingTemplateItems(profile);
        var templateAssetIds = templateItems
            .SelectMany(item => item.IncludedAssetIds ?? Enumerable.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToHashSet();
        templateAssetIds.Add(asset.Id);

        var profileAssetsById = new Dictionary<Guid, LocalRentalAsset>();
        var linkedProfileAssets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => !current.IsDeleted && current.BillingProfileId == profile.Id)
            .ToListAsync(ct);
        foreach (var profileAsset in linkedProfileAssets)
            profileAssetsById[profileAsset.Id] = profileAsset;

        var templateAssets = await LoadRentalAssetsByIdsAsync(
            templateAssetIds,
            ignoreQueryFilters: true,
            asNoTracking: true,
            excludeDeleted: true,
            ct);
        foreach (var templateAsset in templateAssets)
            profileAssetsById[templateAsset.Id] = templateAsset;

        var profileAssets = profileAssetsById.Values.ToList();

        templateItems = GetBillingTemplateItems(profile, profileAssets);
        if (!ApplyAssetMonthlyFeesToBillingTemplate(profile, templateItems, profileAssets))
            return;

        var now = DateTime.UtcNow;
        profile.BillingTemplateJson = SerializeBillingTemplateItems(templateItems);
        profile.MonthlyAmount = templateItems.Sum(ResolveTemplateMonthlyAmount);
        profile.ItemName = BuildProfileItemName(profile, templateItems);
        profile.IsDirty = true;
        profile.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
    }

    private static bool ApplyAssetMonthlyFeesToBillingTemplate(
        LocalRentalBillingProfile profile,
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems,
        IReadOnlyList<LocalRentalAsset> profileAssets)
    {
        if (templateItems.Count == 0 || profileAssets.Count == 0)
            return false;

        var assetsById = profileAssets
            .Where(asset => asset.Id != Guid.Empty && !asset.IsDeleted)
            .GroupBy(asset => asset.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var billableAssetIds = assetsById.Values
            .Where(asset => !RentalAssetStatusRules.IsNonOperating(asset.AssetStatus))
            .Select(asset => asset.Id)
            .Distinct()
            .ToList();
        var changed = NormalizeTemplateAssetCoverage(templateItems, billableAssetIds);
        var profileBillingType = NormalizeBillingType(profile.BillingType);

        foreach (var templateItem in templateItems)
        {
            var includedAssetIds = (templateItem.IncludedAssetIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            if (includedAssetIds.Count == 0)
                continue;

            var itemAssets = includedAssetIds
                .Where(assetsById.ContainsKey)
                .Select(id => assetsById[id])
                .Where(asset => !RentalAssetStatusRules.IsNonOperating(asset.AssetStatus))
                .ToList();
            if (itemAssets.Count == 0)
                continue;

            var totalMonthlyFee = itemAssets.Sum(asset => Math.Max(0m, asset.MonthlyFee));
            var effectiveLineMode = ResolveTemplateBillingLineMode(templateItem.BillingLineMode, profileBillingType);
            var distinctPositiveFees = itemAssets
                .Select(asset => Math.Max(0m, asset.MonthlyFee))
                .Where(fee => fee > 0m)
                .Distinct()
                .ToList();

            decimal quantity;
            decimal unitPrice;
            if (itemAssets.Count == 1 ||
                string.Equals(effectiveLineMode, "묶음", StringComparison.OrdinalIgnoreCase) ||
                distinctPositiveFees.Count != 1)
            {
                quantity = 1m;
                unitPrice = totalMonthlyFee;
            }
            else
            {
                quantity = itemAssets.Count;
                unitPrice = distinctPositiveFees[0];
            }

            var amount = CalculateTemplateLineAmount(quantity, unitPrice);
            if (templateItem.Quantity != quantity ||
                templateItem.UnitPrice != unitPrice ||
                templateItem.Amount != amount)
            {
                templateItem.Quantity = quantity;
                templateItem.UnitPrice = unitPrice;
                templateItem.Amount = amount;
                changed = true;
            }
        }

        return changed;
    }

    private static bool NormalizeTemplateAssetCoverage(
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems,
        IReadOnlyList<Guid> linkedAssetIds)
    {
        if (templateItems.Count == 0 || linkedAssetIds.Count == 0)
            return false;

        var changed = false;
        var linkedIdSet = linkedAssetIds.ToHashSet();
        var assignedIds = new HashSet<Guid>();
        var targetIndex = -1;

        for (var i = 0; i < templateItems.Count; i++)
        {
            var item = templateItems[i];
            var normalizedIds = (item.IncludedAssetIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty && linkedIdSet.Contains(id))
                .Distinct()
                .Where(id => assignedIds.Add(id))
                .ToList();

            if (targetIndex < 0 && normalizedIds.Count > 0)
                targetIndex = i;

            if (!normalizedIds.SequenceEqual(item.IncludedAssetIds ?? new List<Guid>()))
            {
                item.IncludedAssetIds = normalizedIds;
                changed = true;
            }
        }

        var missingIds = linkedAssetIds
            .Where(id => id != Guid.Empty && !assignedIds.Contains(id))
            .ToList();
        if (missingIds.Count == 0)
            return changed;

        if (targetIndex < 0)
            targetIndex = 0;

        templateItems[targetIndex].IncludedAssetIds.AddRange(missingIds);
        return true;
    }

    private static Dictionary<Guid, decimal> BuildTemplateAssetMonthlyFeeMap(
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems)
    {
        var monthlyFeeByAssetId = new Dictionary<Guid, decimal>();
        foreach (var templateItem in templateItems)
        {
            var includedAssetIds = (templateItem.IncludedAssetIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            if (includedAssetIds.Count == 0)
                continue;

            decimal? monthlyFee = null;
            if (includedAssetIds.Count == 1)
            {
                monthlyFee = ResolveTemplateMonthlyAmount(templateItem);
            }
            else
            {
                var quantity = NormalizeTemplateQuantity(templateItem.Quantity);
                var unitPrice = ResolveTemplateUnitPrice(quantity, templateItem.UnitPrice, templateItem.Amount);
                if (unitPrice > 0m && quantity == includedAssetIds.Count)
                    monthlyFee = unitPrice;
            }

            if (!monthlyFee.HasValue)
                continue;

            foreach (var assetId in includedAssetIds)
                monthlyFeeByAssetId[assetId] = Math.Max(0m, monthlyFee.Value);
        }

        return monthlyFeeByAssetId;
    }

    private async Task SyncBillingProfileAssetsAsync(
        LocalRentalBillingProfile profile,
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems,
        IReadOnlyList<RentalBillingAssetLinkEdit>? assetLinkEdits,
        CancellationToken ct)
    {
        var includedAssetIds = templateItems
            .SelectMany(item => item.IncludedAssetIds ?? Enumerable.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToHashSet();
        var assetLinkEditMap = (assetLinkEdits ?? Array.Empty<RentalBillingAssetLinkEdit>())
            .Where(edit => edit is not null && edit.AssetId != Guid.Empty)
            .GroupBy(edit => edit.AssetId)
            .ToDictionary(group => group.Key, group => group.Last());
        foreach (var assetId in assetLinkEditMap.Keys)
            includedAssetIds.Add(assetId);

        var hasAssetLinkInstruction = includedAssetIds.Count > 0 || assetLinkEdits is not null;
        if (!hasAssetLinkInstruction)
            return;

        var normalizedProfileCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(profile.CustomerName);
        var normalizedInstallSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(profile.InstallSiteName);
        var normalizedOfficeCode = NormalizeOfficeCode(
            string.IsNullOrWhiteSpace(profile.ResponsibleOfficeCode)
                ? profile.ManagementCompanyCode
                : profile.ResponsibleOfficeCode,
            DomainConstants.OfficeUsenet);
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            profile.TenantCode,
            normalizedOfficeCode,
            profile.TenantCode,
            normalizedOfficeCode);

        var linkedAssetsById = new Dictionary<Guid, LocalRentalAsset>();
        var profileLinkedAssets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .Where(asset => asset.BillingProfileId == profile.Id)
            .ToListAsync(ct);
        foreach (var asset in profileLinkedAssets)
            linkedAssetsById[asset.Id] = asset;

        var includedAssets = await LoadRentalAssetsByIdsAsync(
            includedAssetIds,
            ignoreQueryFilters: true,
            asNoTracking: false,
            excludeDeleted: false,
            ct);
        foreach (var asset in includedAssets)
            linkedAssetsById[asset.Id] = asset;

        var linkedAssets = linkedAssetsById.Values.ToList();

        if (linkedAssets.Count == 0)
            return;

        var relinkedProfileIds = new HashSet<Guid>();
        var touchedAssetIds = new HashSet<Guid>();
        var templateMonthlyFeeByAssetId = BuildTemplateAssetMonthlyFeeMap(templateItems);
        var now = DateTime.UtcNow;
        foreach (var asset in linkedAssets)
        {
            var previousBillingProfileId = asset.BillingProfileId;
            var shouldInclude = includedAssetIds.Contains(asset.Id);
            assetLinkEditMap.TryGetValue(asset.Id, out var edit);
            var matchesProfileTenant = RentalAssetCanTransferToBillingProfileScope(asset, normalizedTenantCode);

            if (shouldInclude && !matchesProfileTenant)
            {
                // 외부 업체 자산은 청구 프로필 템플릿의 IncludedAssetIds로만 참조한다.
                // 자산 원본의 거래처/품목/소유 지점/청구 프로필 연결은 해당 업체 데이터로
                // 남겨 두어야 계정별 동기화와 이력이 섞이지 않는다.
                continue;
            }

            if (!matchesProfileTenant)
            {
                if (asset.BillingProfileId == profile.Id)
                {
                    await ApplyAssignmentClearedSnapshotAsync(asset, previousBillingProfileId, now, ct);
                    asset.BillingEligibilityStatus = RentalAssetStatusRules.IsNonOperating(asset.AssetStatus)
                        ? BillingEligibilityExcluded
                        : BillingEligibilityUnconfirmed;
                    asset.IsDirty = true;
                    asset.UpdatedAtUtc = now;
                    touchedAssetIds.Add(asset.Id);
                }

                continue;
            }

            if (shouldInclude)
            {
                touchedAssetIds.Add(asset.Id);
                asset.BillingProfileId = profile.Id;
                asset.CustomerId = profile.CustomerId;
                asset.CustomerName = normalizedProfileCustomerName;
                asset.CurrentCustomerName = normalizedProfileCustomerName;

                var normalizedAssetInstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(
                    edit?.InstallLocation);
                if (string.IsNullOrWhiteSpace(normalizedAssetInstallLocation))
                {
                    normalizedAssetInstallLocation = string.IsNullOrWhiteSpace(asset.InstallLocation)
                        ? normalizedInstallSiteName
                        : RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallLocation);
                }

                asset.InstallLocation = normalizedAssetInstallLocation;
                asset.InstallSiteName = string.IsNullOrWhiteSpace(normalizedProfileCustomerName)
                    ? RentalCatalogValueNormalizer.NormalizeDisplayText(FirstNonEmpty(
                        asset.InstallSiteName,
                        normalizedInstallSiteName,
                        normalizedAssetInstallLocation))
                    : normalizedProfileCustomerName;

                if (edit?.MonthlyFee is decimal monthlyFee)
                    asset.MonthlyFee = Math.Max(0m, monthlyFee);
                else if (templateMonthlyFeeByAssetId.TryGetValue(asset.Id, out var templateMonthlyFee))
                    asset.MonthlyFee = Math.Max(0m, templateMonthlyFee);
                if (profile.ContractDate.HasValue)
                    asset.ContractDate = profile.ContractDate;
                if (edit?.ContractStartDate.HasValue == true)
                    asset.ContractStartDate = edit.ContractStartDate;
                if (edit is not null)
                    asset.Notes = (edit.Notes ?? string.Empty).Trim();

                var linkedAssetOwnerOfficeCode = ResolveLinkedAssetManagementCompanyCode(asset, normalizedOfficeCode);
                asset.ResponsibleOfficeCode = normalizedOfficeCode;
                asset.ManagementCompanyCode = linkedAssetOwnerOfficeCode;
                asset.OfficeCode = linkedAssetOwnerOfficeCode;
                asset.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                    asset.TenantCode,
                    linkedAssetOwnerOfficeCode,
                    normalizedTenantCode,
                    normalizedOfficeCode);
                asset.BillingEligibilityStatus = RentalAssetStatusRules.IsNonOperating(asset.AssetStatus)
                    ? BillingEligibilityExcluded
                    : BillingEligibilityTarget;
                if (string.Equals(asset.BillingEligibilityStatus, BillingEligibilityTarget, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(asset.BillingExclusionReason))
                {
                    asset.BillingExclusionReason = string.Empty;
                }

                if (previousBillingProfileId.HasValue &&
                    previousBillingProfileId.Value != Guid.Empty &&
                    previousBillingProfileId.Value != profile.Id)
                {
                    relinkedProfileIds.Add(previousBillingProfileId.Value);
                }
            }
            else if (asset.BillingProfileId == profile.Id)
            {
                await ApplyAssignmentClearedSnapshotAsync(asset, previousBillingProfileId, now, ct);
                asset.BillingEligibilityStatus = RentalAssetStatusRules.IsNonOperating(asset.AssetStatus)
                    ? BillingEligibilityExcluded
                    : BillingEligibilityUnconfirmed;
                touchedAssetIds.Add(asset.Id);
            }

            asset.IsDirty = true;
            asset.UpdatedAtUtc = now;
        }

        if (relinkedProfileIds.Count > 0)
        {
            var relinkedProfiles = await _db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .Where(current => relinkedProfileIds.Contains(current.Id) && !current.IsDeleted)
                .ToListAsync(ct);
            foreach (var relinkedProfile in relinkedProfiles)
            {
                var relinkedTemplateItems = GetBillingTemplateItems(relinkedProfile, Array.Empty<LocalRentalAsset>());
                var hasTemplateChange = false;
                foreach (var templateItem in relinkedTemplateItems)
                {
                    var removedCount = templateItem.IncludedAssetIds.RemoveAll(id => includedAssetIds.Contains(id));
                    hasTemplateChange |= removedCount > 0;
                }

                if (!hasTemplateChange)
                    continue;

                relinkedProfile.BillingTemplateJson = SerializeBillingTemplateItems(relinkedTemplateItems);
                relinkedProfile.MonthlyAmount = relinkedTemplateItems.Sum(ResolveTemplateMonthlyAmount);
                relinkedProfile.ItemName = BuildProfileItemName(relinkedProfile, relinkedTemplateItems);
                relinkedProfile.IsDirty = true;
                relinkedProfile.UpdatedAtUtc = now;
            }
        }

        await _db.SaveChangesAsync(ct);
        await RefreshLocalRentalAssetAssignmentHistoriesAsync(touchedAssetIds, now, "청구 연결 변경", ct);
    }

    private static IQueryable<LocalRentalAsset>? BuildBillingCandidateCustomerMatchQuery(
        IQueryable<LocalRentalAsset> query,
        Guid? customerId,
        string normalizedCustomerName)
    {
        var hasCustomerId = customerId.HasValue && customerId.Value != Guid.Empty;
        var hasCustomerName = !string.IsNullOrWhiteSpace(normalizedCustomerName);
        if (!hasCustomerId && !hasCustomerName)
            return null;

        if (hasCustomerId && hasCustomerName)
        {
            var customerKey = customerId!.Value;
            return query.Where(asset =>
                asset.CustomerId == customerKey ||
                asset.CustomerName == normalizedCustomerName ||
                asset.CurrentCustomerName == normalizedCustomerName);
        }

        if (hasCustomerId)
        {
            var customerKey = customerId!.Value;
            return query.Where(asset => asset.CustomerId == customerKey);
        }

        return query.Where(asset =>
            asset.CustomerName == normalizedCustomerName ||
            asset.CurrentCustomerName == normalizedCustomerName);
    }

    private static IQueryable<LocalRentalAsset> ApplyAssetLinkSearchFilter(
        IQueryable<LocalRentalAsset> query,
        string searchText)
        => query.Where(asset =>
            (asset.ManagementNumber != null && asset.ManagementNumber.Contains(searchText)) ||
            (asset.ManagementId != null && asset.ManagementId.Contains(searchText)) ||
            (asset.AssetKey != null && asset.AssetKey.Contains(searchText)) ||
            (asset.ItemName != null && asset.ItemName.Contains(searchText)) ||
            (asset.ItemCategoryName != null && asset.ItemCategoryName.Contains(searchText)) ||
            (asset.Manufacturer != null && asset.Manufacturer.Contains(searchText)) ||
            (asset.MachineNumber != null && asset.MachineNumber.Contains(searchText)) ||
            (asset.CustomerName != null && asset.CustomerName.Contains(searchText)) ||
            (asset.CurrentCustomerName != null && asset.CurrentCustomerName.Contains(searchText)) ||
            (asset.InstallLocation != null && asset.InstallLocation.Contains(searchText)) ||
            (asset.InstallSiteName != null && asset.InstallSiteName.Contains(searchText)) ||
            (asset.ResponsibleOfficeCode != null && asset.ResponsibleOfficeCode.Contains(searchText)) ||
            (asset.ManagementCompanyCode != null && asset.ManagementCompanyCode.Contains(searchText)));

    private static string NormalizeAssetLinkSearchText(string? searchText)
        => (searchText ?? string.Empty).Trim();

    private static bool IsBillingCandidateCustomerMatch(
        LocalRentalAsset asset,
        Guid? customerId,
        string normalizedCustomerName)
    {
        if (customerId.HasValue && customerId.Value != Guid.Empty && asset.CustomerId == customerId.Value)
            return true;

        if (string.IsNullOrWhiteSpace(normalizedCustomerName))
            return false;

        return string.Equals(
                   RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CustomerName),
                   normalizedCustomerName,
                   StringComparison.CurrentCultureIgnoreCase) ||
               string.Equals(
                   RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CurrentCustomerName),
                   normalizedCustomerName,
                   StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsOutsideCurrentAssetOffice(LocalRentalAsset asset, string? normalizedOfficeCode)
    {
        if (string.IsNullOrWhiteSpace(normalizedOfficeCode))
            return false;

        var responsibleOfficeCode = NormalizeOfficeCode(asset.ResponsibleOfficeCode, string.Empty);
        var managementCompanyCode = NormalizeOfficeCode(asset.ManagementCompanyCode, string.Empty);

        return !string.Equals(responsibleOfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(managementCompanyCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAssetScopeDisplay(string responsibleOfficeName, string managementCompanyName)
    {
        var responsible = string.IsNullOrWhiteSpace(responsibleOfficeName) ? "-" : responsibleOfficeName.Trim();
        var management = string.IsNullOrWhiteSpace(managementCompanyName) ? "-" : managementCompanyName.Trim();
        return string.Equals(responsible, management, StringComparison.CurrentCultureIgnoreCase)
            ? responsible
            : $"담당 {responsible} / 관리 {management}";
    }

    private void UpsertBillingRun(LocalRentalBillingProfile profile, RentalBillingRunModel run)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(run);

        var runs = GetBillingRuns(profile);
        var existingIndex = runs.FindIndex(current => current.RunId == run.RunId);
        if (existingIndex >= 0)
            runs[existingIndex] = run;
        else
            runs.Add(run);

        profile.BillingRunsJson = JsonSerializer.Serialize(runs, RentalJsonOptions);
    }

    public DateOnly? GetNextBillingDate(LocalRentalBillingProfile profile, DateOnly referenceDate)
    {
        if (profile is null || !profile.IsActive)
            return null;

        NormalizeBillingSchedule(profile, referenceDate);
        return RentalBillingScheduleRules.ResolveApplicableBillingDate(
            profile.BillingDay,
            profile.BillingDayMode,
            profile.BillingCycleMonths,
            profile.BillingAnchorMonth,
            NormalizeReferenceDate(referenceDate),
            profile.LastBilledDate);
    }

    public bool IsBillingMonth(LocalRentalBillingProfile profile, DateOnly referenceDate)
    {
        NormalizeBillingSchedule(profile, referenceDate);
        return RentalBillingScheduleRules.IsBillingMonth(
            profile.BillingCycleMonths,
            profile.BillingAnchorMonth,
            NormalizeReferenceDate(referenceDate));
    }

    private static DateOnly GetCycleAnchor(LocalRentalBillingProfile profile, DateOnly referenceDate)
    {
        var normalizedReference = NormalizeReferenceDate(referenceDate);
        var anchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            profile.BillingCycleMonths,
            profile.BillingAnchorMonth,
            profile.BillingAnchorDate,
            profile.BillingStartDate,
            profile.ContractStartDate,
            profile.ContractDate,
            profile.LastBilledDate,
            normalizedReference);
        return profile.BillingAnchorDate
               ?? profile.BillingStartDate
               ?? profile.ContractStartDate
               ?? profile.ContractDate
               ?? profile.LastBilledDate
               ?? new DateOnly(normalizedReference.Year, anchorMonth, 1);
    }

    private static DateOnly BuildBillingDate(int year, int month, int billingDay)
        => RentalBillingScheduleRules.BuildBillingDate(year, month, billingDay, RentalBillingScheduleRules.BillingDayModeFixedDay);

    private async Task<string> ResolveRentalOfficeCodeAsync(
        string? officeCode,
        string? officeCodeOrName,
        string? fallbackOfficeCode,
        CancellationToken ct)
    {
        var offices = await _db.Offices.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        foreach (var candidate in new[] { officeCode, officeCodeOrName })
        {
            var resolved = ResolveOfficeCodeCandidate(candidate, offices);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }

        var fallback = ResolveOfficeCodeCandidate(fallbackOfficeCode, offices);
        return string.IsNullOrWhiteSpace(fallback)
            ? DomainConstants.OfficeUsenet
            : fallback;
    }

    private static string ResolveOfficeCodeCandidate(string? value, IReadOnlyCollection<LocalOffice> offices)
    {
        var normalized = NormalizeOfficeToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var exactCode = offices.FirstOrDefault(office =>
            string.Equals(NormalizeOfficeCode(office.Code, string.Empty), normalized, StringComparison.OrdinalIgnoreCase));
        if (exactCode is not null)
            return NormalizeOfficeCode(exactCode.Code, normalized);

        var exactName = offices.FirstOrDefault(office =>
            string.Equals((office.Name ?? string.Empty).Trim(), (value ?? string.Empty).Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (exactName is not null)
            return NormalizeOfficeCode(exactName.Code, normalized);

        var containedName = offices.FirstOrDefault(office =>
            !string.IsNullOrWhiteSpace(office.Name) &&
            (value ?? string.Empty).Contains(office.Name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (containedName is not null)
            return NormalizeOfficeCode(containedName.Code, normalized);

        return normalized;
    }

    private static string NormalizeOfficeToken(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        return OfficeCodeCatalog.NormalizeOfficeCodeLoose(trimmed, null, string.Empty);
    }

    private static string NormalizeBillingMethod(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        if (trimmed.Contains("CMS", StringComparison.OrdinalIgnoreCase))
            return "CMS";
        if (trimmed.Contains("전자", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("세금", StringComparison.OrdinalIgnoreCase))
            return "전자세금계산서";
        if (trimmed.Contains("카드", StringComparison.OrdinalIgnoreCase))
            return "카드";
        if (trimmed.Contains("현금", StringComparison.OrdinalIgnoreCase))
            return "현금";

        return trimmed;
    }

    private static string NormalizeOfficeCode(string? officeCode, string? fallback)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, fallback);

    private static (string OwnerOfficeCode, string TenantCode) ResolveRentalOwnerScopeForResponsibleOffice(
        string? ownerOfficeCode,
        string? responsibleOfficeCode,
        SessionState session)
    {
        var normalizedResponsibleOfficeCode = NormalizeOfficeCode(responsibleOfficeCode, session.OfficeCode);
        var resolvedOwnerOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
            ownerOfficeCode,
            normalizedResponsibleOfficeCode,
            session.OfficeCode);
        var resolvedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            null,
            resolvedOwnerOfficeCode,
            session.TenantCode,
            session.OfficeCode);

        if (TenantScopeCatalog.TenantContainsOffice(resolvedTenantCode, normalizedResponsibleOfficeCode))
            return (resolvedOwnerOfficeCode, resolvedTenantCode);

        resolvedOwnerOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
            null,
            normalizedResponsibleOfficeCode,
            session.OfficeCode);
        resolvedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            null,
            resolvedOwnerOfficeCode,
            session.TenantCode,
            session.OfficeCode);

        return (resolvedOwnerOfficeCode, resolvedTenantCode);
    }

    private static string ResolveDefaultOfficeName(string officeCode)
        => OfficeCodeCatalog.GetOfficeDisplayName(officeCode);

    private async Task<Guid?> ResolveCustomerIdAsync(
        string? customerName,
        string? businessNumber,
        CancellationToken ct,
        bool allowWorkbookNameVariants = true,
        string? preferredOfficeCode = null,
        string? preferredTenantCode = null)
    {
        var normalizedName = (customerName ?? string.Empty).Trim();
        var normalizedBusinessNumber = (businessNumber ?? string.Empty).Trim();
        var normalizedPreferredOfficeCode = ResolveCustomerRentalOfficeCode(preferredOfficeCode);
        var normalizedPreferredTenantCode = (preferredTenantCode ?? string.Empty).Trim();
        var nameCandidates = (allowWorkbookNameVariants
                ? BuildWorkbookCustomerNameCandidates(normalizedName)
                : BuildStrictCustomerNameCandidates(normalizedName))
            .ToList();

        if (!string.IsNullOrWhiteSpace(normalizedBusinessNumber))
        {
            var businessMatches = await _db.Customers.AsNoTracking()
                .Where(customer => customer.BusinessNumber == normalizedBusinessNumber)
                .Select(customer => new { customer.Id, customer.NameOriginal, customer.NameMatchKey, customer.UpdatedAtUtc, customer.ResponsibleOfficeCode, customer.OfficeCode, customer.TenantCode })
                .ToListAsync(ct);
            if (businessMatches.Count == 1 &&
                MatchesPreferredCustomerTenant(
                    businessMatches[0],
                    normalizedPreferredTenantCode,
                    customer => customer.TenantCode) &&
                MatchesPreferredCustomerOffice(
                    businessMatches[0],
                    normalizedPreferredOfficeCode,
                    customer => customer.OfficeCode,
                    customer => customer.ResponsibleOfficeCode) &&
                (nameCandidates.Count == 0 ||
                 CustomerMatchesAnyCandidateName(
                     businessMatches[0],
                     nameCandidates,
                     customer => customer.NameOriginal,
                     customer => customer.NameMatchKey)))
            {
                return businessMatches[0].Id;
            }

            if (!string.IsNullOrWhiteSpace(normalizedPreferredTenantCode) && businessMatches.Count > 1)
            {
                var tenantBusinessMatches = PreferCustomerMatchesByTenant(
                    businessMatches,
                    normalizedPreferredTenantCode,
                    customer => customer.TenantCode);
                if (tenantBusinessMatches.Count == 1)
                    return tenantBusinessMatches[0].Id;
                businessMatches = tenantBusinessMatches;
            }

            if (!string.IsNullOrWhiteSpace(normalizedPreferredOfficeCode) && businessMatches.Count > 1)
            {
                var officeBusinessMatches = PreferCustomerMatchesByOffice(
                    businessMatches,
                    normalizedPreferredOfficeCode,
                    customer => customer.OfficeCode,
                    customer => customer.ResponsibleOfficeCode);
                if (officeBusinessMatches.Count == 1)
                    return officeBusinessMatches[0].Id;
                if (officeBusinessMatches.Count > 1)
                    businessMatches = officeBusinessMatches;
            }

            if (businessMatches.Count > 1 && nameCandidates.Count > 0)
            {
                var businessExactMatches = businessMatches
                    .Where(customer => nameCandidates.Contains(customer.NameOriginal, StringComparer.CurrentCultureIgnoreCase))
                    .Select(customer => customer.Id)
                    .Distinct()
                    .ToList();
                if (businessExactMatches.Count == 1)
                    return businessExactMatches[0];

                var candidateKeys = nameCandidates
                    .Select(RentalCatalogValueNormalizer.NormalizeLooseKey)
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var businessNormalizedMatches = businessMatches
                    .Select(customer => new
                    {
                        customer.Id,
                        MatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(
                            string.IsNullOrWhiteSpace(customer.NameMatchKey) ? customer.NameOriginal : customer.NameMatchKey)
                    })
                    .Where(customer => candidateKeys.Any(key =>
                        string.Equals(customer.MatchKey, key, StringComparison.OrdinalIgnoreCase)))
                    .Select(customer => customer.Id)
                    .Distinct()
                    .ToList();
                if (businessNormalizedMatches.Count == 1)
                    return businessNormalizedMatches[0];
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedName))
            return null;

        var directMatches = await _db.Customers.AsNoTracking()
            .Where(customer => nameCandidates.Contains(customer.NameOriginal))
            .Select(customer => new { customer.Id, customer.UpdatedAtUtc, customer.ResponsibleOfficeCode, customer.OfficeCode, customer.TenantCode })
            .OrderByDescending(customer => customer.UpdatedAtUtc)
            .ToListAsync(ct);
        var directMatchIds = directMatches.Select(customer => customer.Id).Distinct().ToList();
        if (directMatchIds.Count == 1)
        {
            var directMatch = directMatches.FirstOrDefault(customer => customer.Id == directMatchIds[0]);
            if (directMatch is not null &&
                MatchesPreferredCustomerTenant(
                    directMatch,
                    normalizedPreferredTenantCode,
                    customer => customer.TenantCode) &&
                MatchesPreferredCustomerOffice(
                    directMatch,
                    normalizedPreferredOfficeCode,
                    customer => customer.OfficeCode,
                    customer => customer.ResponsibleOfficeCode))
            {
                return directMatchIds[0];
            }
        }
        if (!string.IsNullOrWhiteSpace(normalizedPreferredTenantCode) && directMatches.Count > 1)
        {
            directMatches = PreferCustomerMatchesByTenant(
                    directMatches,
                    normalizedPreferredTenantCode,
                    customer => customer.TenantCode)
                .ToList();
            var tenantDirectMatchIds = directMatches
                .Select(customer => customer.Id)
                .Distinct()
                .ToList();
            if (tenantDirectMatchIds.Count == 1)
                return tenantDirectMatchIds[0];
        }
        if (!string.IsNullOrWhiteSpace(normalizedPreferredOfficeCode) && directMatches.Count > 1)
        {
            var officeDirectMatchIds = PreferCustomerMatchesByOffice(
                    directMatches,
                    normalizedPreferredOfficeCode,
                    customer => customer.OfficeCode,
                    customer => customer.ResponsibleOfficeCode)
                .Select(customer => customer.Id)
                .Distinct()
                .ToList();
            if (officeDirectMatchIds.Count == 1)
                return officeDirectMatchIds[0];
        }

        var normalizedNameKeys = nameCandidates
            .Select(RentalCatalogValueNormalizer.NormalizeLooseKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedNameKeys.Count == 0)
            return null;

        var keyMatches = await _db.Customers.AsNoTracking()
            .Where(customer => customer.NameMatchKey != string.Empty)
            .Select(customer => new { customer.Id, customer.NameMatchKey, customer.NameOriginal, customer.UpdatedAtUtc, customer.ResponsibleOfficeCode, customer.OfficeCode, customer.TenantCode })
            .ToListAsync(ct);

        var normalizedMatches = keyMatches
            .Select(customer => new
            {
                customer.Id,
                customer.NameOriginal,
                customer.UpdatedAtUtc,
                customer.ResponsibleOfficeCode,
                customer.OfficeCode,
                customer.TenantCode,
                MatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(
                    string.IsNullOrWhiteSpace(customer.NameMatchKey) ? customer.NameOriginal : customer.NameMatchKey)
            })
            .Where(customer => !string.IsNullOrWhiteSpace(customer.MatchKey))
            .ToList();

        var exactMatch = normalizedMatches
            .Where(customer => normalizedNameKeys.Any(key => string.Equals(customer.MatchKey, key, StringComparison.OrdinalIgnoreCase)))
            .Select(customer => customer.Id)
            .Distinct()
            .ToList();
        if (exactMatch.Count == 1)
        {
            var exactCustomer = normalizedMatches.FirstOrDefault(customer => customer.Id == exactMatch[0]);
            if (exactCustomer is not null &&
                MatchesPreferredCustomerTenant(
                    exactCustomer,
                    normalizedPreferredTenantCode,
                    customer => customer.TenantCode) &&
                MatchesPreferredCustomerOffice(
                    exactCustomer,
                    normalizedPreferredOfficeCode,
                    customer => customer.OfficeCode,
                    customer => customer.ResponsibleOfficeCode))
            {
                return exactMatch[0];
            }
        }
        if (!string.IsNullOrWhiteSpace(normalizedPreferredTenantCode) && exactMatch.Count > 1)
        {
            exactMatch = PreferCustomerMatchesByTenant(
                    normalizedMatches
                        .Where(customer => normalizedNameKeys.Any(key => string.Equals(customer.MatchKey, key, StringComparison.OrdinalIgnoreCase))),
                    normalizedPreferredTenantCode,
                    customer => customer.TenantCode)
                .Select(customer => customer.Id)
                .Distinct()
                .ToList();
            if (exactMatch.Count == 1)
                return exactMatch[0];
        }
        if (!string.IsNullOrWhiteSpace(normalizedPreferredOfficeCode) && exactMatch.Count > 1)
        {
            var officeExactMatches = PreferCustomerMatchesByOffice(
                    normalizedMatches
                        .Where(customer =>
                            exactMatch.Contains(customer.Id) &&
                            normalizedNameKeys.Any(key => string.Equals(customer.MatchKey, key, StringComparison.OrdinalIgnoreCase))),
                    normalizedPreferredOfficeCode,
                    customer => customer.OfficeCode,
                    customer => customer.ResponsibleOfficeCode)
                .Select(customer => customer.Id)
                .Distinct()
                .ToList();
            if (officeExactMatches.Count == 1)
                return officeExactMatches[0];
        }

        return null;
    }

    private static List<T> PreferCustomerMatchesByTenant<T>(
        IEnumerable<T> matches,
        string? preferredTenantCode,
        Func<T, string?> tenantCodeSelector)
    {
        var result = matches.ToList();
        if (result.Count <= 1 || string.IsNullOrWhiteSpace(preferredTenantCode))
            return result;

        var tenantMatches = result
            .Where(customer => string.Equals(
                (tenantCodeSelector(customer) ?? string.Empty).Trim(),
                preferredTenantCode.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        return tenantMatches;
    }

    private static List<T> PreferCustomerMatchesByOffice<T>(
        IEnumerable<T> matches,
        string? preferredOfficeCode,
        Func<T, string?> officeCodeSelector,
        Func<T, string?> responsibleOfficeCodeSelector)
    {
        var result = matches.ToList();
        if (result.Count <= 1 || string.IsNullOrWhiteSpace(preferredOfficeCode))
            return result;

        var normalizedPreferredOfficeCode = NormalizeOfficeCode(preferredOfficeCode, preferredOfficeCode);
        var exactOfficeMatches = result
            .Where(customer => string.Equals(
                NormalizeOfficeCode(officeCodeSelector(customer), responsibleOfficeCodeSelector(customer)),
                normalizedPreferredOfficeCode,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exactOfficeMatches.Count > 0)
            return exactOfficeMatches;

        var responsibleOfficeMatches = result
            .Where(customer => string.Equals(
                ResolveCustomerRentalOfficeCode(responsibleOfficeCodeSelector(customer)),
                normalizedPreferredOfficeCode,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        return responsibleOfficeMatches.Count > 0
            ? responsibleOfficeMatches
            : result;
    }

    private static bool MatchesPreferredCustomerOffice<T>(
        T customer,
        string? preferredOfficeCode,
        Func<T, string?> officeCodeSelector,
        Func<T, string?> responsibleOfficeCodeSelector)
    {
        if (string.IsNullOrWhiteSpace(preferredOfficeCode))
            return true;

        return string.Equals(
            NormalizeOfficeCode(officeCodeSelector(customer), responsibleOfficeCodeSelector(customer)),
            NormalizeOfficeCode(preferredOfficeCode, preferredOfficeCode),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPreferredCustomerTenant<T>(
        T customer,
        string? preferredTenantCode,
        Func<T, string?> tenantCodeSelector)
    {
        if (string.IsNullOrWhiteSpace(preferredTenantCode))
            return true;

        return string.Equals(
            (tenantCodeSelector(customer) ?? string.Empty).Trim(),
            preferredTenantCode.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool CustomerMatchesAnyCandidateName<T>(
        T customer,
        IReadOnlyCollection<string> candidateNames,
        Func<T, string?> nameOriginalSelector,
        Func<T, string?> nameMatchKeySelector)
    {
        if (candidateNames.Count == 0)
            return true;

        var candidateDisplays = candidateNames
            .Select(RentalCatalogValueNormalizer.NormalizeDisplayText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var customerDisplay = RentalCatalogValueNormalizer.NormalizeDisplayText(nameOriginalSelector(customer));
        if (!string.IsNullOrWhiteSpace(customerDisplay) && candidateDisplays.Contains(customerDisplay))
            return true;

        var candidateKeys = candidateNames
            .Select(RentalCatalogValueNormalizer.NormalizeLooseKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidateKeys.Count == 0)
            return false;

        var customerNameKey = RentalCatalogValueNormalizer.NormalizeLooseKey(nameOriginalSelector(customer));
        if (!string.IsNullOrWhiteSpace(customerNameKey) && candidateKeys.Contains(customerNameKey))
            return true;

        var customerMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(nameMatchKeySelector(customer));
        return !string.IsNullOrWhiteSpace(customerMatchKey) && candidateKeys.Contains(customerMatchKey);
    }

    private async Task<LocalCustomer?> GetRentalLinkedCustomerAsync(Guid? customerId, CancellationToken ct)
    {
        if (!customerId.HasValue || customerId.Value == Guid.Empty)
            return null;

        var customer = await _db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == customerId.Value && !current.IsDeleted, ct);
        return customer;
    }

    private async Task EnrichAssetReferencesAsync(
        LocalRentalAsset asset,
        CancellationToken ct,
        RentalCatalogRepairResult? repairResult = null,
        List<LocalItem>? activeItems = null,
        bool allowCategoryRecovery = false,
        bool allowDerivedAssetBackfill = true,
        bool allowWorkbookNameVariants = true)
    {
        asset.ItemCategoryName = await EnsureRentalItemCategoryOptionAsync(asset.ItemCategoryName, repairResult, ct, allowCategoryRecovery);

        LocalCustomer? customer = null;
        if (asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
        {
            customer = await _db.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == asset.CustomerId.Value, ct);
        }

        var customerNameCandidates = (allowWorkbookNameVariants
                ? BuildWorkbookCustomerNameCandidates(asset.CustomerName)
                : BuildStrictCustomerNameCandidates(asset.CustomerName))
            .ToList();
        var linkedAssetCustomerTenantMismatch = customer is not null &&
            !MatchesPreferredCustomerTenant(
                customer,
                asset.TenantCode,
                current => current.TenantCode);
        if (customer is not null &&
            (linkedAssetCustomerTenantMismatch ||
             (customerNameCandidates.Count > 0 &&
              !CustomerMatchesAnyCandidateName(
                  customer,
                  customerNameCandidates,
                  current => current.NameOriginal,
                  current => current.NameMatchKey))))
        {
            var correctedCustomerId = await ResolveCustomerIdAsync(
                asset.CustomerName,
                null,
                ct,
                allowWorkbookNameVariants,
                asset.ResponsibleOfficeCode,
                asset.TenantCode);
            if (correctedCustomerId.HasValue &&
                correctedCustomerId.Value != Guid.Empty &&
                correctedCustomerId.Value != asset.CustomerId)
            {
                asset.CustomerId = correctedCustomerId.Value;
                customer = await _db.Customers
                    .AsNoTracking()
                    .FirstOrDefaultAsync(current => current.Id == asset.CustomerId.Value, ct);
            }
            else
            {
                asset.CustomerId = null;
                customer = null;
            }
        }

        if (customer is null)
            asset.CustomerId = await ResolveCustomerIdAsync(
                asset.CustomerName,
                null,
                ct,
                allowWorkbookNameVariants,
                preferredTenantCode: asset.TenantCode);

        if (asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
        {
            customer ??= await _db.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == asset.CustomerId.Value, ct);
            if (customer is not null)
            {
                var normalizedCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(customer.NameOriginal);
                asset.CustomerName = normalizedCustomerName;
                asset.CurrentCustomerName = normalizedCustomerName;
            }
            else
            {
                asset.CustomerId = null;
            }
        }

        var item = await EnsureRentalItemAsync(asset, repairResult, ct, activeItems);
        if (item is null)
            return;

        ApplyRentalItemMetadata(asset, item, allowDerivedAssetBackfill);

        if (allowDerivedAssetBackfill && string.IsNullOrWhiteSpace(asset.PurchaseVendor))
            asset.PurchaseVendor = await ResolveLatestPurchaseVendorNameAsync(item.Id, ct);
    }

    private async Task<LocalItem?> ResolveItemAsync(
        Guid? itemId,
        string? itemName,
        string? itemCategoryName,
        string? managementNumber,
        string? machineNumber,
        string preferredOfficeCode,
        string preferredTenantCode,
        CancellationToken ct,
        IReadOnlyList<LocalItem>? activeItems = null)
    {
        var availableItems = activeItems ?? await GetActiveItemsAsync(ct);
        var scopedAssetItems = availableItems
            .Where(item =>
                ItemOperationalPolicy.IsAsset(item.TrackingType) &&
                MatchesRentalItemScope(item, preferredOfficeCode, preferredTenantCode))
            .ToList();

        if (itemId.HasValue && itemId.Value != Guid.Empty)
        {
            var direct = scopedAssetItems.FirstOrDefault(item => item.Id == itemId.Value);
            if (direct is not null && IsRentalItemIdentifierCompatible(direct, managementNumber, machineNumber))
                return direct;
        }

        var normalizedMaterialNumber = (managementNumber ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedMaterialNumber))
        {
            var materialMatch = scopedAssetItems
                .Where(item =>
                    string.Equals((item.MaterialNumber ?? string.Empty).Trim(), normalizedMaterialNumber, StringComparison.OrdinalIgnoreCase) &&
                    IsRentalItemIdentifierCompatible(item, managementNumber, machineNumber))
                .OrderByDescending(item => item.UpdatedAtUtc)
                .FirstOrDefault();
            if (materialMatch is not null)
                return materialMatch;
        }

        var normalizedMachineNumber = (machineNumber ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedMachineNumber))
        {
            var serialMatch = scopedAssetItems
                .Where(item =>
                    string.Equals((item.SerialNumber ?? string.Empty).Trim(), normalizedMachineNumber, StringComparison.OrdinalIgnoreCase) &&
                    IsRentalItemIdentifierCompatible(item, managementNumber, machineNumber))
                .OrderByDescending(item => item.UpdatedAtUtc)
                .FirstOrDefault();
            if (serialMatch is not null)
                return serialMatch;
        }

        if (!string.IsNullOrWhiteSpace(normalizedMaterialNumber) ||
            !string.IsNullOrWhiteSpace(normalizedMachineNumber))
        {
            return null;
        }

        var normalizedItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(itemName);
        if (string.IsNullOrWhiteSpace(normalizedItemName))
            return null;

        var matches = await GetNameMatchedItemsAsync(normalizedItemName, ct, availableItems);
        var assetMatches = matches
            .Where(item =>
                ItemOperationalPolicy.IsAsset(item.TrackingType) &&
                MatchesRentalItemScope(item, preferredOfficeCode, preferredTenantCode))
            .ToList();

        if (assetMatches.Count == 1)
            return assetMatches[0];

        var normalizedCategoryKey = RentalCatalogValueNormalizer.NormalizeLooseKey(itemCategoryName);
        if (!string.IsNullOrWhiteSpace(normalizedCategoryKey))
        {
            var categoryMatches = assetMatches
                .Where(item =>
                    string.Equals(
                        RentalCatalogValueNormalizer.NormalizeLooseKey(item.CategoryName),
                        normalizedCategoryKey,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.LastPurchaseDate)
                .ThenByDescending(item => item.UpdatedAtUtc)
                .ToList();

            if (categoryMatches.Count == 1)
                return categoryMatches[0];
        }

        var displayMatches = assetMatches
            .Where(item =>
                string.Equals(
                    RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.NameOriginal),
                    normalizedItemName,
                    StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(item => item.LastPurchaseDate)
            .ThenByDescending(item => item.UpdatedAtUtc)
            .ToList();

        return displayMatches.Count == 1 ? displayMatches[0] : null;
    }

    private async Task<string> EnsureRentalItemCategoryOptionAsync(
        string? itemCategoryName,
        RentalCatalogRepairResult? repairResult,
        CancellationToken ct,
        bool allowCreateOrReactivate = false)
    {
        var normalizedName = SelectionOptionDefaults.NormalizeItemCategoryName(itemCategoryName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return string.Empty;

        var normalizedKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedName);
        var options = _db.ItemCategoryOptions.Local
            .Concat(await _db.ItemCategoryOptions.IgnoreQueryFilters().ToListAsync(ct))
            .GroupBy(option => option.Id)
            .Select(group => group.First())
            .ToList();
        var existing = options.FirstOrDefault(option =>
            string.Equals(
                RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name),
                normalizedKey,
                StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            var existingName = string.IsNullOrWhiteSpace(existing.Name) ? normalizedName : existing.Name;
            if (existing.IsActive && !existing.IsDeleted)
                return existingName;

            if (!allowCreateOrReactivate)
            {
                TryAddUnique(repairResult?.MissingCategoryNames, existingName);
                if (repairResult is not null)
                    return existingName;

                throw new InvalidOperationException($"삭제되었거나 비활성화된 품목분류 '{existingName}'입니다. 선택값 관리에서 먼저 복구하세요.");
            }

            existing.IsActive = true;
            existing.IsDeleted = false;
            existing.IsDirty = true;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            TryAddUnique(repairResult?.AddedCategoryNames, existingName);
            return existingName;
        }

        if (!allowCreateOrReactivate)
        {
            TryAddUnique(repairResult?.MissingCategoryNames, normalizedName);
            if (repairResult is not null)
                return normalizedName;

            throw new InvalidOperationException($"등록되지 않은 품목분류 '{normalizedName}'입니다. 선택값 관리에서 먼저 추가하세요.");
        }

        var now = DateTime.UtcNow;
        var nextSortOrder = options
            .Where(option => !option.IsDeleted)
            .Select(option => option.SortOrder)
            .DefaultIfEmpty(0)
            .Max() + 10;

        _db.ItemCategoryOptions.Add(new LocalItemCategoryOption
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            SortOrder = nextSortOrder,
            IsSystemDefault = false,
            IsActive = true,
            IsDeleted = false,
            IsDirty = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

        TryAddUnique(repairResult?.AddedCategoryNames, normalizedName);
        return normalizedName;
    }

    private async Task<LocalItem?> EnsureRentalItemAsync(
        LocalRentalAsset asset,
        RentalCatalogRepairResult? repairResult,
        CancellationToken ct,
        List<LocalItem>? activeItems = null)
    {
        var assetOfficeCode = ResolveAssetItemOfficeCode(asset);
        var assetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, assetOfficeCode);
        var item = await ResolveItemAsync(
            asset.ItemId,
            asset.ItemName,
            asset.ItemCategoryName,
            asset.ManagementNumber,
            asset.MachineNumber,
            assetOfficeCode,
            assetTenantCode,
            ct,
            activeItems);
        if (item is not null)
            return item;

        var normalizedItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
        if (string.IsNullOrWhiteSpace(normalizedItemName))
            return null;

        var nameMatches = (await GetNameMatchedItemsAsync(normalizedItemName, ct, activeItems))
            .Where(item => ItemOperationalPolicy.IsAsset(item.TrackingType))
            .ToList();
        var scopedNameMatches = nameMatches
            .Where(item =>
                MatchesRentalItemScope(item, assetOfficeCode, assetTenantCode) &&
                IsRentalItemIdentifierCompatible(item, asset.ManagementNumber, asset.MachineNumber))
            .ToList();

        if (scopedNameMatches.Count == 1)
            return scopedNameMatches[0];

        if (scopedNameMatches.Count > 1)
        {
            TryAddUnique(repairResult?.AmbiguousItemNames, normalizedItemName);
            return null;
        }

        var now = DateTime.UtcNow;
        var created = new LocalItem
        {
            Id = Guid.NewGuid(),
            TenantCode = assetTenantCode,
            OfficeCode = assetOfficeCode,
            NameOriginal = normalizedItemName,
            NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedItemName),
            SpecificationOriginal = string.Empty,
            SpecificationMatchKey = string.Empty,
            CategoryName = asset.ItemCategoryName,
            ItemKind = ItemKinds.Asset,
            TrackingType = ItemTrackingTypes.Asset,
            StorageLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CurrentLocation),
            PurchasePrice = asset.PurchasePrice,
            SalePrice = asset.SalePrice,
            RetailPrice = asset.SalePrice,
            SimpleMemo = AutoCreatedRentalItemMemo,
            IsRental = true,
            IsSale = false,
            SerialNumber = (asset.MachineNumber ?? string.Empty).Trim(),
            MaterialNumber = (asset.ManagementNumber ?? string.Empty).Trim(),
            InstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallLocation),
            RentalStartDate = asset.ContractStartDate ?? asset.InstallDate ?? asset.ContractDate,
            RentalEndDate = asset.RentalEndDate,
            Notes = BuildAutoCreatedRentalItemNote(asset),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsDirty = true
        };

        _db.Items.Add(created);
        activeItems?.Add(created);
        TryAddUnique(repairResult?.AddedItemNames, created.NameOriginal);
        return created;
    }

    private static bool MatchesRentalItemScope(LocalItem item, string preferredOfficeCode, string preferredTenantCode)
    {
        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(item.OfficeCode, OfficeCodeCatalog.Shared);
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            item.TenantCode,
            item.OfficeCode,
            preferredTenantCode,
            preferredOfficeCode);

        return string.Equals(normalizedOfficeCode, preferredOfficeCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(normalizedTenantCode, preferredTenantCode, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RetireOrphanedAutoCreatedRentalItemsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct)
    {
        if (itemIds.Count == 0)
            return;

        var candidateIds = itemIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (candidateIds.Count == 0)
            return;

        var referencedItemIds = await LoadReferencedRentalItemIdsAsync(candidateIds, ct);
        var candidateItems = await LoadActiveItemsByIdsAsync(candidateIds, ct);
        foreach (var item in candidateItems)
        {
            if (referencedItemIds.Contains(item.Id))
                continue;
            if (!IsAutoCreatedRentalItem(item))
                continue;

            item.IsDeleted = true;
            item.IsDirty = false;
            item.CurrentStock = 0m;
            item.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private async Task<HashSet<Guid>> LoadReferencedRentalItemIdsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct)
    {
        var ids = itemIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var referencedItemIds = new HashSet<Guid>();
        if (ids.Count == 0)
            return referencedItemIds;

        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds;

            foreach (var assetItemId in await _db.RentalAssets.IgnoreQueryFilters()
                         .Where(asset => !asset.IsDeleted &&
                                         asset.ItemId.HasValue &&
                                         scopedBatchIds.Contains(asset.ItemId.Value))
                         .Select(asset => asset.ItemId!.Value)
                         .Distinct()
                         .ToListAsync(ct))
            {
                referencedItemIds.Add(assetItemId);
            }

            foreach (var invoiceItemId in await _db.InvoiceLines.IgnoreQueryFilters()
                         .Where(line => !line.IsDeleted &&
                                        line.ItemId.HasValue &&
                                        scopedBatchIds.Contains(line.ItemId.Value))
                         .Select(line => line.ItemId!.Value)
                         .Distinct()
                         .ToListAsync(ct))
            {
                referencedItemIds.Add(invoiceItemId);
            }

            foreach (var stockItemId in await _db.ItemWarehouseStocks
                         .Where(stock => scopedBatchIds.Contains(stock.ItemId))
                         .Select(stock => stock.ItemId)
                         .Distinct()
                         .ToListAsync(ct))
            {
                referencedItemIds.Add(stockItemId);
            }
        }

        return referencedItemIds;
    }

    private async Task<List<LocalItem>> LoadActiveItemsByIdsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct)
    {
        var ids = itemIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var itemsById = new Dictionary<Guid, LocalItem>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds;
            var batchItems = await _db.Items.IgnoreQueryFilters()
                .Where(current => !current.IsDeleted && scopedBatchIds.Contains(current.Id))
                .ToListAsync(ct);
            foreach (var item in batchItems)
                itemsById[item.Id] = item;
        }

        return itemsById.Values.ToList();
    }

    private async Task<List<LocalItem>> GetNameMatchedItemsAsync(string? itemName, CancellationToken ct, IReadOnlyList<LocalItem>? activeItems = null)
    {
        var normalizedKey = RentalCatalogValueNormalizer.NormalizeLooseKey(itemName);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return [];

        var availableItems = activeItems ?? await GetActiveItemsAsync(ct);
        return availableItems
            .Where(item =>
                string.Equals(
                    RentalCatalogValueNormalizer.NormalizeLooseKey(item.NameOriginal),
                    normalizedKey,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool IsRentalItemIdentifierCompatible(LocalItem item, string? managementNumber, string? machineNumber)
    {
        var expectedMaterialNumber = (managementNumber ?? string.Empty).Trim();
        var expectedSerialNumber = (machineNumber ?? string.Empty).Trim();
        var itemMaterialNumber = (item.MaterialNumber ?? string.Empty).Trim();
        var itemSerialNumber = (item.SerialNumber ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(expectedMaterialNumber) &&
            !string.IsNullOrWhiteSpace(itemMaterialNumber) &&
            !string.Equals(itemMaterialNumber, expectedMaterialNumber, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedSerialNumber) &&
            !string.IsNullOrWhiteSpace(itemSerialNumber) &&
            !string.Equals(itemSerialNumber, expectedSerialNumber, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsAutoCreatedRentalItem(LocalItem item)
        => !item.IsDeleted &&
           ItemOperationalPolicy.IsAsset(item.TrackingType) &&
           string.Equals(item.SimpleMemo, AutoCreatedRentalItemMemo, StringComparison.Ordinal);

    private async Task<List<LocalItem>> GetActiveItemsAsync(CancellationToken ct)
    {
        return _db.Items.Local
            .Concat(await _db.Items.IgnoreQueryFilters().Where(item => !item.IsDeleted).ToListAsync(ct))
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .Where(item => !item.IsDeleted)
            .OrderByDescending(item => item.LastPurchaseDate)
            .ThenByDescending(item => item.UpdatedAtUtc)
            .ToList();
    }

    private static void ApplyRentalItemMetadata(LocalRentalAsset asset, LocalItem item, bool allowAssetBackfill = true)
    {
        var itemChanged = false;
        var now = DateTime.UtcNow;
        var assetOfficeCode = ResolveAssetItemOfficeCode(asset);
        var assetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, assetOfficeCode);
        var normalizedCategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(item.CategoryName);
        var normalizedAssetCategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(asset.ItemCategoryName);

        if (string.IsNullOrWhiteSpace(normalizedCategoryName) && !string.IsNullOrWhiteSpace(normalizedAssetCategoryName))
        {
            item.CategoryName = normalizedAssetCategoryName;
            normalizedCategoryName = normalizedAssetCategoryName;
            itemChanged = true;
        }

        var normalizedAssetItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
        if (!string.Equals(item.NameOriginal, normalizedAssetItemName, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(item.NameOriginal))
        {
            item.NameOriginal = normalizedAssetItemName;
            item.NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedAssetItemName);
            itemChanged = true;
        }

        if (string.IsNullOrWhiteSpace(item.MaterialNumber) && !string.IsNullOrWhiteSpace(asset.ManagementNumber))
        {
            item.MaterialNumber = asset.ManagementNumber.Trim();
            itemChanged = true;
        }

        if (string.IsNullOrWhiteSpace(item.SerialNumber) && !string.IsNullOrWhiteSpace(asset.MachineNumber))
        {
            item.SerialNumber = asset.MachineNumber.Trim();
            itemChanged = true;
        }

        if (string.IsNullOrWhiteSpace(item.InstallLocation) && !string.IsNullOrWhiteSpace(asset.InstallLocation))
        {
            item.InstallLocation = asset.InstallLocation.Trim();
            itemChanged = true;
        }

        if (string.IsNullOrWhiteSpace(item.StorageLocation) && !string.IsNullOrWhiteSpace(asset.CurrentLocation))
        {
            item.StorageLocation = asset.CurrentLocation.Trim();
            itemChanged = true;
        }

        if (item.PurchasePrice <= 0m && asset.PurchasePrice > 0m)
        {
            item.PurchasePrice = asset.PurchasePrice;
            itemChanged = true;
        }

        if (item.SalePrice <= 0m && asset.SalePrice > 0m)
        {
            item.SalePrice = asset.SalePrice;
            if (item.RetailPrice <= 0m)
                item.RetailPrice = asset.SalePrice;
            itemChanged = true;
        }

        var rentalStartDate = asset.ContractStartDate ?? asset.InstallDate ?? asset.ContractDate;
        if (!item.RentalStartDate.HasValue && rentalStartDate.HasValue)
        {
            item.RentalStartDate = rentalStartDate;
            itemChanged = true;
        }

        if (!item.RentalEndDate.HasValue && asset.RentalEndDate.HasValue)
        {
            item.RentalEndDate = asset.RentalEndDate;
            itemChanged = true;
        }

        if (!item.IsRental)
        {
            item.IsRental = true;
            itemChanged = true;
        }

        if (!string.Equals(item.TrackingType, ItemTrackingTypes.Asset, StringComparison.Ordinal))
        {
            item.TrackingType = ItemTrackingTypes.Asset;
            itemChanged = true;
        }

        if (!string.Equals(item.ItemKind, ItemKinds.Asset, StringComparison.Ordinal))
        {
            item.ItemKind = ItemKinds.Asset;
            itemChanged = true;
        }

        if (item.IsSale)
        {
            item.IsSale = false;
            itemChanged = true;
        }

        if (!string.Equals(item.OfficeCode, assetOfficeCode, StringComparison.OrdinalIgnoreCase))
        {
            item.OfficeCode = assetOfficeCode;
            itemChanged = true;
        }

        if (!string.Equals(item.TenantCode, assetTenantCode, StringComparison.OrdinalIgnoreCase))
        {
            item.TenantCode = assetTenantCode;
            itemChanged = true;
        }

        if (string.IsNullOrWhiteSpace(item.SimpleMemo))
        {
            item.SimpleMemo = AutoCreatedRentalItemMemo;
            itemChanged = true;
        }

        asset.ItemId = item.Id;
        asset.ItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.NameOriginal);
        asset.ItemCategoryName = normalizedCategoryName;
        if (asset.PurchasePrice <= 0m && item.PurchasePrice > 0m)
            asset.PurchasePrice = item.PurchasePrice;
        if (allowAssetBackfill && asset.SalePrice <= 0m && item.SalePrice > 0m)
            asset.SalePrice = item.SalePrice;
        if (allowAssetBackfill && string.IsNullOrWhiteSpace(asset.InstallLocation) && !string.IsNullOrWhiteSpace(item.InstallLocation))
            asset.InstallLocation = item.InstallLocation.Trim();
        if (allowAssetBackfill && string.IsNullOrWhiteSpace(asset.CurrentLocation) && !string.IsNullOrWhiteSpace(item.StorageLocation))
            asset.CurrentLocation = item.StorageLocation.Trim();
        if (allowAssetBackfill && string.IsNullOrWhiteSpace(asset.MachineNumber) && !string.IsNullOrWhiteSpace(item.SerialNumber))
            asset.MachineNumber = item.SerialNumber.Trim();

        if (itemChanged)
        {
            item.IsDirty = true;
            item.UpdatedAtUtc = now;
        }
    }

    private static string ResolveAssetItemOfficeCode(LocalRentalAsset asset)
        => OfficeCodeCatalog.NormalizeOfficeCodeLoose(
            asset.ResponsibleOfficeCode,
            asset.ManagementCompanyCode,
            DomainConstants.OfficeUsenet);

    private static string BuildAutoCreatedRentalItemNote(LocalRentalAsset asset)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(asset.CustomerName))
            parts.Add($"거래처:{asset.CustomerName.Trim()}");
        if (!string.IsNullOrWhiteSpace(asset.ManagementNumber))
            parts.Add($"관리번호:{asset.ManagementNumber.Trim()}");
        if (!string.IsNullOrWhiteSpace(asset.MachineNumber))
            parts.Add($"기계번호:{asset.MachineNumber.Trim()}");
        return string.Join(" / ", parts);
    }

    private static string BuildAssetRepairSignature(LocalRentalAsset asset)
        => string.Join("|",
            asset.CustomerId?.ToString("N") ?? string.Empty,
            asset.ItemId?.ToString("N") ?? string.Empty,
            asset.AssetKey,
            asset.CustomerName,
            asset.ItemCategoryName,
            asset.ItemName,
            asset.ManagementNumber,
            asset.ManagementId,
            asset.MachineNumber,
            asset.PurchaseVendor,
            asset.InstallLocation,
            asset.CurrentLocation,
            asset.AssetStatus,
            asset.PurchasePrice.ToString(CultureInfo.InvariantCulture),
            asset.SalePrice.ToString(CultureInfo.InvariantCulture));

    private static void TryAddUnique(ICollection<string>? values, string? value)
    {
        if (values is null || string.IsNullOrWhiteSpace(value))
            return;

        if (values.Any(existing =>
                string.Equals(
                    RentalCatalogValueNormalizer.NormalizeLooseKey(existing),
                    RentalCatalogValueNormalizer.NormalizeLooseKey(value),
                    StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        values.Add(value.Trim());
    }

    private static void SortAndDistinct(List<string> values)
    {
        if (values.Count <= 1)
            return;

        var ordered = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        values.Clear();
        values.AddRange(ordered);
    }

    private async Task<string> ResolveLatestPurchaseVendorNameAsync(Guid itemId, CancellationToken ct)
    {
        if (itemId == Guid.Empty)
            return string.Empty;

        var vendorName = await (
                from line in _db.InvoiceLines.AsNoTracking()
                join invoice in _db.Invoices.AsNoTracking() on line.InvoiceId equals invoice.Id
                join customer in _db.Customers.AsNoTracking() on invoice.CustomerId equals customer.Id
                where !line.IsDeleted
                      && !invoice.IsDeleted
                      && invoice.IsLatestVersion
                      && invoice.IsConfirmed
                      && line.ItemId == itemId
                      && (invoice.VoucherType == 거래플랜.Shared.Contracts.VoucherType.Purchase ||
                          invoice.VoucherType == 거래플랜.Shared.Contracts.VoucherType.Procurement)
                orderby invoice.InvoiceDate descending, invoice.UpdatedAtUtc descending
                select customer.NameOriginal)
            .FirstOrDefaultAsync(ct);

        return vendorName?.Trim() ?? string.Empty;
    }

    private async Task<Guid?> FindMatchingBillingProfileIdAsync(LocalRentalAsset asset, CancellationToken ct)
    {
        if (!asset.CustomerId.HasValue || asset.CustomerId.Value == Guid.Empty)
            return null;

        var normalizedOfficeCode = NormalizeOfficeCode(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode);
        var siteKeys = new[] { asset.InstallLocation, asset.InstallSiteName }
            .Select(RentalCatalogValueNormalizer.NormalizeLooseKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var profiles = _db.RentalBillingProfiles.AsNoTracking()
            .Where(profile => !profile.IsDeleted)
            .Where(profile => profile.CustomerId == asset.CustomerId.Value);

        if (!string.IsNullOrWhiteSpace(normalizedOfficeCode))
            profiles = profiles.Where(profile => profile.ResponsibleOfficeCode == normalizedOfficeCode);

        var candidates = await profiles.ToListAsync(ct);

        if (candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0].Id;

        if (siteKeys.Count > 0)
        {
            var siteMatches = candidates
                .Where(profile =>
                {
                    var profileSiteKey = RentalCatalogValueNormalizer.NormalizeLooseKey(profile.InstallSiteName);
                    return !string.IsNullOrWhiteSpace(profileSiteKey) &&
                           siteKeys.Contains(profileSiteKey, StringComparer.OrdinalIgnoreCase);
                })
                .ToList();
            if (siteMatches.Count == 1)
                return siteMatches[0].Id;
        }

        return null;
    }

    private static string BuildProfileKey(
        string managementCompanyCode,
        Guid? customerId,
        string? businessNumber,
        string? customerName,
        string? billingType,
        string? billingAdvanceMode,
        int billingDay,
        int billingCycleMonths,
        string? billingMethod)
        => RentalDuplicateNormalizer.BuildProfileKey(
            managementCompanyCode,
            customerId,
            businessNumber,
            customerName,
            billingType,
            billingAdvanceMode,
            billingDay,
            billingCycleMonths,
            billingMethod);

    private static string BuildLegacyProfileKey(
        string managementCompanyCode,
        Guid? customerId,
        string? businessNumber,
        string? customerName,
        string? billingType,
        string? billingAdvanceMode,
        int billingDay,
        int billingCycleMonths,
        string? billingMethod)
        => RentalDuplicateNormalizer.BuildLegacyProfileKey(
            managementCompanyCode,
            customerId,
            businessNumber,
            customerName,
            billingType,
            billingAdvanceMode,
            billingDay,
            billingCycleMonths,
            billingMethod);

    private static bool IsDistinctBillingCustomerAlias(string? profileCustomerName, string? linkedCustomerName)
    {
        var profileNameKey = RentalDuplicateNormalizer.NormalizeProfileKeyPart(profileCustomerName);
        var linkedNameKey = RentalDuplicateNormalizer.NormalizeProfileKeyPart(linkedCustomerName);
        return !string.IsNullOrWhiteSpace(profileNameKey) &&
               !string.IsNullOrWhiteSpace(linkedNameKey) &&
               !string.Equals(profileNameKey, linkedNameKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAssetKey(
        string managementCompanyCode,
        string? managementNumber,
        string? managementId,
        string? machineNumber,
        string? customerName,
        string? itemName)
    {
        var primary = !string.IsNullOrWhiteSpace(managementNumber)
            ? managementNumber
            : !string.IsNullOrWhiteSpace(managementId)
                ? managementId
                : machineNumber;

        return string.Join('|',
            NormalizeProfileKeyPart(managementCompanyCode),
            NormalizeProfileKeyPart(primary),
            NormalizeProfileKeyPart(customerName),
            NormalizeProfileKeyPart(itemName));
    }

    private static void AssignUniqueAssetKeysForRepair(
        IReadOnlyList<LocalRentalAsset> assets,
        IReadOnlyDictionary<Guid, string> assetBaseKeyById)
    {
        var assignedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in assets
                     .GroupBy(asset => assetBaseKeyById.TryGetValue(asset.Id, out var key) ? key : string.Empty, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var orderedAssets = group
                .OrderBy(asset => asset.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(asset => asset.ManagementId, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(asset => asset.CustomerName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(asset => asset.ItemName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(asset => asset.Id)
                .ToList();

            var baseKey = group.Key;
            for (var index = 0; index < orderedAssets.Count; index++)
            {
                var asset = orderedAssets[index];
                var candidate = index == 0 && !string.IsNullOrWhiteSpace(baseKey)
                    ? baseKey
                    : BuildLegacyCollisionAssetKey(baseKey, asset.Id);

                if (assignedKeys.Contains(candidate))
                    candidate = BuildLegacyCollisionAssetKey(candidate, asset.Id);

                asset.AssetKey = candidate;
                assignedKeys.Add(candidate);
            }
        }
    }

    private static string BuildLegacyCollisionAssetKey(string? baseKey, Guid assetId)
    {
        var normalizedBaseKey = (baseKey ?? string.Empty).Trim();
        var idSuffix = assetId == Guid.Empty ? Guid.NewGuid().ToString("N") : assetId.ToString("N");
        return string.IsNullOrWhiteSpace(normalizedBaseKey)
            ? idSuffix
            : $"{normalizedBaseKey}|{idSuffix}";
    }

    private static string NormalizeProfileKeyPart(string? value)
        => new string((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(ch => !char.IsWhiteSpace(ch) && ch != '[' && ch != ']')
            .ToArray());

    private static string ResolveAssetStatus(string? requestedStatus, string? currentLocation, DateOnly? disposalDate)
    {
        var status = (requestedStatus ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(status))
            return RentalAssetStatusRules.Normalize(status);

        if (disposalDate.HasValue)
            return "폐기";

        var location = (currentLocation ?? string.Empty).Trim();
        if (location.Contains("판매", StringComparison.OrdinalIgnoreCase))
            return "판매";
        if (location.Contains("폐기", StringComparison.OrdinalIgnoreCase))
            return "폐기";
        if (location.Contains("창고", StringComparison.OrdinalIgnoreCase))
            return "창고";
        if (location.Contains("회수", StringComparison.OrdinalIgnoreCase))
            return "창고";
        if (location.Contains("대기", StringComparison.OrdinalIgnoreCase))
            return "창고";
        return "임대진행중";
    }

    private static DataSet ReadWorkbook(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        return reader.AsDataSet(new ExcelDataSetConfiguration
        {
            ConfigureDataTable = _ => new ExcelDataTableConfiguration
            {
                UseHeaderRow = false
            }
        });
    }

    private static int FindHeaderRow(DataTable table, params string[] requiredHeaders)
    {
        var maxScan = Math.Min(20, table.Rows.Count);
        for (var rowIndex = 0; rowIndex < maxScan; rowIndex++)
        {
            var rowValues = Enumerable.Range(0, table.Columns.Count)
                .Select(columnIndex => Convert.ToString(table.Rows[rowIndex][columnIndex], CultureInfo.CurrentCulture)?.Trim() ?? string.Empty)
                .ToList();

            if (requiredHeaders.All(header => rowValues.Any(value => string.Equals(value, header, StringComparison.OrdinalIgnoreCase))))
                return rowIndex;
        }

        return -1;
    }

    private static Dictionary<string, int> BuildHeaderMap(DataTable table, int headerRowIndex)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
        {
            var header = Convert.ToString(table.Rows[headerRowIndex][columnIndex], CultureInfo.CurrentCulture)?.Trim();
            if (string.IsNullOrWhiteSpace(header) || map.ContainsKey(header))
                continue;
            map[header] = columnIndex;
        }

        return map;
    }

    private static object? GetCellValue(DataRow row, IReadOnlyDictionary<string, int> headerMap, params string[] headers)
    {
        foreach (var header in headers)
        {
            if (headerMap.TryGetValue(header, out var columnIndex))
                return row[columnIndex];
        }

        return null;
    }

    private static string GetCellString(DataRow row, IReadOnlyDictionary<string, int> headerMap, params string[] headers)
    {
        var value = GetCellValue(row, headerMap, headers);
        return Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
    }

    private static DateOnly? ParseSheetAnchorDate(string sheetName, DataTable table)
    {
        for (var rowIndex = 0; rowIndex < Math.Min(8, table.Rows.Count); rowIndex++)
        {
            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                var date = ParseDateValue(table.Rows[rowIndex][columnIndex]);
                if (date.HasValue)
                    return date;
            }
        }

        var monthToken = new string(sheetName.Where(ch => char.IsDigit(ch)).ToArray());
        if (int.TryParse(monthToken, out var month) && month is >= 1 and <= 12)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            return new DateOnly(today.Year, month, 1);
        }

        return null;
    }

    private static DateOnly? ParseDateValue(object? value)
    {
        if (value is null || value == DBNull.Value)
            return null;
        if (value is DateOnly dateOnly)
            return dateOnly;
        if (value is DateTime dateTime)
            return DateOnly.FromDateTime(dateTime);
        if (value is double oaDate)
        {
            try
            {
                return DateOnly.FromDateTime(DateTime.FromOADate(oaDate));
            }
            catch
            {
                return null;
            }
        }

        var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Replace('.', '-').Replace('/', '-');

        var formats = new[]
        {
            "yyyy-MM-dd",
            "yyyy-M-d",
            "yyyy년 M월 d일",
            "yyyy년 MM월 dd일",
            "yyyyMMdd"
        };

        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(text, format, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed))
                return DateOnly.FromDateTime(parsed);
        }

        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var fallback)
            ? DateOnly.FromDateTime(fallback)
            : null;
    }

    private static int? ParseIntValue(object? value)
    {
        if (value is null || value == DBNull.Value)
            return null;
        if (value is int intValue)
            return intValue;
        if (value is long longValue)
            return (int)longValue;
        if (value is double doubleValue)
            return (int)Math.Round(doubleValue, MidpointRounding.AwayFromZero);

        var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Replace(",", string.Empty).Replace("개월", string.Empty).Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed)
            ? parsed
            : null;
    }

    private static decimal ParseDecimalValue(object? value)
    {
        if (value is null || value == DBNull.Value)
            return 0m;
        if (value is decimal decimalValue)
            return decimalValue;
        if (value is double doubleValue)
            return Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
        if (value is float floatValue)
            return Convert.ToDecimal(floatValue, CultureInfo.InvariantCulture);
        if (value is int intValue)
            return intValue;
        if (value is long longValue)
            return longValue;

        var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) ||
            text.Contains("면제", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("무료", StringComparison.OrdinalIgnoreCase) ||
            text == "-" ||
            text == "무")
        {
            return 0m;
        }

        var digits = new string(text.Where(ch => char.IsDigit(ch) || ch == '.' || ch == '-').ToArray());
        return decimal.TryParse(digits, NumberStyles.Number | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private static string BuildBillingDisplayStatus(LocalRentalBillingProfile profile, DateOnly? nextBillingDate, int? daysRemaining)
    {
        if (!profile.IsActive)
            return "비활성";

        var completionStatus = PaymentFlowConstants.NormalizeCompletionStatus(profile.CompletionStatus);
        if (string.Equals(completionStatus, PaymentFlowConstants.CompletionDone, StringComparison.OrdinalIgnoreCase))
            return "완료";

        var billingStatus = PaymentFlowConstants.NormalizeBillingStatus(profile.BillingStatus);
        if (string.Equals(billingStatus, PaymentFlowConstants.BillingStatusOnHold, StringComparison.OrdinalIgnoreCase))
            return "보류";
        var settlementStatus = PaymentFlowConstants.NormalizeSettlementStatus(profile.SettlementStatus);
        if (string.Equals(settlementStatus, PaymentFlowConstants.SettlementStatusPartial, StringComparison.OrdinalIgnoreCase))
            return "부분수금";
        if (string.Equals(settlementStatus, PaymentFlowConstants.SettlementStatusConfirmed, StringComparison.OrdinalIgnoreCase))
            return "수금완료";
        if (string.Equals(settlementStatus, PaymentFlowConstants.SettlementStatusPending, StringComparison.OrdinalIgnoreCase))
            return "수금대기";
        if (string.Equals(settlementStatus, PaymentFlowConstants.SettlementStatusUnpaid, StringComparison.OrdinalIgnoreCase))
            return "미수";
        if (string.Equals(billingStatus, PaymentFlowConstants.BillingStatusInProgress, StringComparison.OrdinalIgnoreCase))
            return "청구중";

        if (!nextBillingDate.HasValue || !daysRemaining.HasValue)
            return string.IsNullOrWhiteSpace(billingStatus) ? "예정" : billingStatus;
        if (daysRemaining.Value < 0)
            return $"지연 {Math.Abs(daysRemaining.Value)}일";
        if (daysRemaining.Value == 0)
            return "오늘";
        return $"D-{daysRemaining.Value}";
    }
}
