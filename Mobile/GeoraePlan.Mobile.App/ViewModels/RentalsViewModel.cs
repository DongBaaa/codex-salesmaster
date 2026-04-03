using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public enum RentalMobileSection
{
    BillingProfiles = 0,
    Assets = 1,
    BillingLogs = 2
}

public sealed class RentalsViewModel : ObservableObject
{
    private readonly JsonSyncStateStore _syncStateStore;
    private readonly SyncCoordinator _syncCoordinator;

    private string _searchText = string.Empty;
    private string _statusMessage = "렌탈 서버 동기화 데이터를 불러올 준비가 되었습니다.";
    private bool _isBusy;
    private DateTime? _lastRefreshUtc;
    private RentalMobileSection _selectedSection = RentalMobileSection.BillingProfiles;

    public RentalsViewModel(JsonSyncStateStore syncStateStore, SyncCoordinator syncCoordinator)
    {
        _syncStateStore = syncStateStore;
        _syncCoordinator = syncCoordinator;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        SyncNowCommand = new AsyncCommand(SyncNowAsync);
    }

    public ObservableCollection<RentalBillingProfileDto> BillingProfiles { get; } = new();
    public ObservableCollection<RentalAssetDto> RentalAssets { get; } = new();
    public ObservableCollection<RentalBillingLogDisplayRow> BillingLogs { get; } = new();

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SyncNowCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public RentalMobileSection SelectedSection
    {
        get => _selectedSection;
        private set
        {
            if (!SetProperty(ref _selectedSection, value))
                return;

            OnPropertyChanged(nameof(IsProfilesSection));
            OnPropertyChanged(nameof(IsAssetsSection));
            OnPropertyChanged(nameof(IsBillingLogsSection));
            OnPropertyChanged(nameof(ProfilesButtonColor));
            OnPropertyChanged(nameof(AssetsButtonColor));
            OnPropertyChanged(nameof(BillingLogsButtonColor));
            OnPropertyChanged(nameof(CurrentSectionTitle));
            OnPropertyChanged(nameof(CurrentSectionSummary));
            OnPropertyChanged(nameof(CurrentListHeight));
        }
    }

    public bool IsProfilesSection => SelectedSection == RentalMobileSection.BillingProfiles;
    public bool IsAssetsSection => SelectedSection == RentalMobileSection.Assets;
    public bool IsBillingLogsSection => SelectedSection == RentalMobileSection.BillingLogs;

    public Color ProfilesButtonColor => IsProfilesSection ? Theme.GeoraePlanTheme.Accent : Theme.GeoraePlanTheme.SecondaryButton;
    public Color AssetsButtonColor => IsAssetsSection ? Theme.GeoraePlanTheme.Accent : Theme.GeoraePlanTheme.SecondaryButton;
    public Color BillingLogsButtonColor => IsBillingLogsSection ? Theme.GeoraePlanTheme.Accent : Theme.GeoraePlanTheme.SecondaryButton;

    public string CurrentSectionTitle => SelectedSection switch
    {
        RentalMobileSection.BillingProfiles => "청구프로필",
        RentalMobileSection.Assets => "렌탈자산",
        RentalMobileSection.BillingLogs => "청구로그",
        _ => "렌탈"
    };

    public string CurrentSectionSummary => SelectedSection switch
    {
        RentalMobileSection.BillingProfiles => $"청구프로필 {BillingProfiles.Count:N0}건",
        RentalMobileSection.Assets => $"렌탈자산 {RentalAssets.Count:N0}건",
        RentalMobileSection.BillingLogs => $"청구로그 {BillingLogs.Count:N0}건",
        _ => string.Empty
    };

    public double CurrentListHeight => SelectedSection switch
    {
        RentalMobileSection.BillingProfiles => CalculateListHeight(BillingProfiles.Count, 116, 56, 5),
        RentalMobileSection.Assets => CalculateListHeight(RentalAssets.Count, 116, 56, 5),
        RentalMobileSection.BillingLogs => CalculateListHeight(BillingLogs.Count, 112, 56, 5),
        _ => 56
    };

    public bool NeedsRefresh(TimeSpan maxAge)
        => !_lastRefreshUtc.HasValue || DateTime.UtcNow - _lastRefreshUtc.Value >= maxAge;

    public void ShowBillingProfiles() => SelectedSection = RentalMobileSection.BillingProfiles;
    public void ShowRentalAssets() => SelectedSection = RentalMobileSection.Assets;
    public void ShowBillingLogs() => SelectedSection = RentalMobileSection.BillingLogs;

    public bool TryNavigateBackOneStep()
    {
        if (SelectedSection == RentalMobileSection.BillingProfiles)
            return false;

        ShowBillingProfiles();
        StatusMessage = "렌탈 기본 목록으로 돌아왔습니다.";
        return true;
    }

    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "렌탈 서버 동기화 데이터를 확인하고 있습니다.";
            await _syncCoordinator.RefreshIfServerChangedAsync("rentals-refresh", TimeSpan.FromSeconds(5));
            await LoadFromStateAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"렌탈 화면 초기화 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SyncNowAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "렌탈 데이터를 서버와 동기화하는 중입니다.";
            var state = await _syncCoordinator.SynchronizeNowAsync();
            if (!string.IsNullOrWhiteSpace(state.LastError))
                StatusMessage = $"동기화 주의: {state.LastError}";

            await LoadFromStateAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"렌탈 동기화 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadFromStateAsync()
    {
        var state = await _syncStateStore.LoadAsync();
        state.Normalize();

        var companyMap = state.SyncedRentalManagementCompanies
            .GroupBy(x => NormalizeKey(x.Code))
            .ToDictionary(group => group.Key, group => Normalize(group.First().Name, group.First().Code), StringComparer.OrdinalIgnoreCase);
        var profileMap = state.SyncedRentalBillingProfiles
            .ToDictionary(x => x.Id, x => x, EqualityComparer<Guid>.Default);

        var filteredProfiles = state.SyncedRentalBillingProfiles
            .Where(profile => MatchesProfile(profile, companyMap))
            .OrderBy(profile => profile.CustomerName)
            .ThenBy(profile => profile.ProfileKey)
            .ToList();
        var filteredAssets = state.SyncedRentalAssets
            .Where(asset => MatchesAsset(asset, companyMap, profileMap))
            .OrderBy(asset => asset.CustomerName)
            .ThenBy(asset => asset.ItemName)
            .ToList();
        var filteredLogs = state.SyncedRentalBillingLogs
            .Where(log => MatchesBillingLog(log, profileMap))
            .OrderByDescending(log => log.BillingYearMonth)
            .ThenByDescending(log => log.ScheduledDate)
            .Select(log => RentalBillingLogDisplayRow.From(log, profileMap.TryGetValue(log.BillingProfileId, out var profile) ? profile : null))
            .ToList();

        Replace(BillingProfiles, filteredProfiles);
        Replace(RentalAssets, filteredAssets);
        Replace(BillingLogs, filteredLogs);

        _lastRefreshUtc = DateTime.UtcNow;
        StatusMessage = filteredProfiles.Count + filteredAssets.Count + filteredLogs.Count == 0
            ? "동기화된 렌탈 데이터가 없습니다."
            : $"렌탈 동기화 데이터 총 {filteredProfiles.Count + filteredAssets.Count + filteredLogs.Count:N0}건을 불러왔습니다.";
        OnPropertyChanged(nameof(CurrentSectionSummary));
        OnPropertyChanged(nameof(CurrentListHeight));
    }

    private bool MatchesProfile(RentalBillingProfileDto profile, IReadOnlyDictionary<string, string> companyMap)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var q = SearchText.Trim();
        return Contains(profile.ProfileKey, q)
               || Contains(profile.CustomerName, q)
               || Contains(profile.ItemName, q)
               || Contains(profile.BillingStatus, q)
               || Contains(profile.ResponsibleOfficeCode, q)
               || Contains(profile.ManagementCompanyCode, q)
               || Contains(ResolveCompanyName(profile.ManagementCompanyCode, companyMap), q);
    }

    private bool MatchesAsset(
        RentalAssetDto asset,
        IReadOnlyDictionary<string, string> companyMap,
        IReadOnlyDictionary<Guid, RentalBillingProfileDto> profileMap)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var q = SearchText.Trim();
        return Contains(asset.AssetKey, q)
               || Contains(asset.CustomerName, q)
               || Contains(asset.ItemName, q)
               || Contains(asset.AssetStatus, q)
               || Contains(asset.CurrentLocation, q)
               || Contains(asset.InstallLocation, q)
               || Contains(asset.ResponsibleOfficeCode, q)
               || Contains(asset.ManagementCompanyCode, q)
               || Contains(ResolveCompanyName(asset.ManagementCompanyCode, companyMap), q)
               || (asset.BillingProfileId.HasValue &&
                   profileMap.TryGetValue(asset.BillingProfileId.Value, out var profile) &&
                   (Contains(profile.ProfileKey, q) || Contains(profile.CustomerName, q)));
    }

    private bool MatchesBillingLog(RentalBillingLogDto log, IReadOnlyDictionary<Guid, RentalBillingProfileDto> profileMap)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var q = SearchText.Trim();
        profileMap.TryGetValue(log.BillingProfileId, out var profile);
        return Contains(log.BillingYearMonth, q)
               || Contains(log.Status, q)
               || Contains(log.Note, q)
               || Contains(log.ProcessedByUsername, q)
               || Contains(log.ResponsibleOfficeCode, q)
               || Contains(profile?.ProfileKey, q)
               || Contains(profile?.CustomerName, q);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    private static string ResolveCompanyName(string? code, IReadOnlyDictionary<string, string> companyMap)
    {
        var key = NormalizeKey(code);
        return companyMap.TryGetValue(key, out var name)
            ? name
            : Normalize(code, "미지정 관리회사");
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static bool Contains(string? source, string query)
        => !string.IsNullOrWhiteSpace(source) &&
           source.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static double CalculateListHeight(int count, double rowHeight, double emptyHeight, int maxVisibleRows)
    {
        if (count <= 0)
            return emptyHeight;

        var visibleRows = Math.Min(count, maxVisibleRows);
        return (visibleRows * rowHeight) + Math.Max(0, visibleRows - 1) * 8;
    }
}

public sealed class RentalBillingLogDisplayRow
{
    public required RentalBillingLogDto Log { get; init; }
    public required string ProfileKey { get; init; }
    public required string CustomerName { get; init; }

    public string Title => string.IsNullOrWhiteSpace(CustomerName)
        ? $"청구로그 {Log.BillingYearMonth}"
        : $"{CustomerName} · {Log.BillingYearMonth}";

    public string Subtitle => $"{Normalize(ProfileKey, "프로필 미지정")} · {Normalize(Log.Status, "예정")} · {Log.BilledAmount:N0}원";

    public string Meta => $"예정일 {Log.ScheduledDate:yyyy-MM-dd} / 처리일 {FormatDate(Log.ProcessedDate)} / 지점 {Normalize(Log.ResponsibleOfficeCode, "미지정")}";

    public string Note => Normalize(Log.Note, "메모 없음");

    public static RentalBillingLogDisplayRow From(RentalBillingLogDto log, RentalBillingProfileDto? profile)
        => new()
        {
            Log = log,
            ProfileKey = profile?.ProfileKey ?? string.Empty,
            CustomerName = profile?.CustomerName ?? string.Empty
        };

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FormatDate(DateOnly? value)
        => value.HasValue ? value.Value.ToString("yyyy-MM-dd") : "미처리";
}
