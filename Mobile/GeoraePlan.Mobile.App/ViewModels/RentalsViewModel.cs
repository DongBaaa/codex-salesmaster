using System.Collections.ObjectModel;
using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public enum RentalMobileSection
{
    BillingProfiles = 0,
    Assets = 1,
    BillingLogs = 2,
    AssignmentHistories = 3
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
    public ObservableCollection<RentalBillingHistoryDisplayRow> BillingLogs { get; } = new();
    public ObservableCollection<RentalAssignmentHistoryDisplayRow> AssignmentHistories { get; } = new();

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
            OnPropertyChanged(nameof(IsAssignmentHistoriesSection));
            OnPropertyChanged(nameof(ProfilesButtonColor));
            OnPropertyChanged(nameof(AssetsButtonColor));
            OnPropertyChanged(nameof(BillingLogsButtonColor));
            OnPropertyChanged(nameof(AssignmentHistoriesButtonColor));
            OnPropertyChanged(nameof(CurrentSectionTitle));
            OnPropertyChanged(nameof(CurrentSectionSummary));
            OnPropertyChanged(nameof(CurrentListHeight));
        }
    }

    public bool IsProfilesSection => SelectedSection == RentalMobileSection.BillingProfiles;
    public bool IsAssetsSection => SelectedSection == RentalMobileSection.Assets;
    public bool IsBillingLogsSection => SelectedSection == RentalMobileSection.BillingLogs;
    public bool IsAssignmentHistoriesSection => SelectedSection == RentalMobileSection.AssignmentHistories;

    public Color ProfilesButtonColor => IsProfilesSection ? Theme.GeoraePlanTheme.Accent : Theme.GeoraePlanTheme.SecondaryButton;
    public Color AssetsButtonColor => IsAssetsSection ? Theme.GeoraePlanTheme.Accent : Theme.GeoraePlanTheme.SecondaryButton;
    public Color BillingLogsButtonColor => IsBillingLogsSection ? Theme.GeoraePlanTheme.Accent : Theme.GeoraePlanTheme.SecondaryButton;
    public Color AssignmentHistoriesButtonColor => IsAssignmentHistoriesSection ? Theme.GeoraePlanTheme.Accent : Theme.GeoraePlanTheme.SecondaryButton;

    public string CurrentSectionTitle => SelectedSection switch
    {
        RentalMobileSection.BillingProfiles => "청구프로필",
        RentalMobileSection.Assets => "렌탈자산",
        RentalMobileSection.BillingLogs => "청구 이력",
        RentalMobileSection.AssignmentHistories => "설치 이력",
        _ => "렌탈"
    };

    public string CurrentSectionSummary => SelectedSection switch
    {
        RentalMobileSection.BillingProfiles => $"청구프로필 {BillingProfiles.Count:N0}건",
        RentalMobileSection.Assets => $"렌탈자산 {RentalAssets.Count:N0}건",
        RentalMobileSection.BillingLogs => $"청구 이력 {BillingLogs.Count:N0}건",
        RentalMobileSection.AssignmentHistories => $"설치 이력 {AssignmentHistories.Count:N0}건",
        _ => string.Empty
    };

    public double CurrentListHeight => SelectedSection switch
    {
        RentalMobileSection.BillingProfiles => CalculateListHeight(BillingProfiles.Count, 116, 56, 5),
        RentalMobileSection.Assets => CalculateListHeight(RentalAssets.Count, 116, 56, 5),
        RentalMobileSection.BillingLogs => CalculateListHeight(BillingLogs.Count, 112, 56, 5),
        RentalMobileSection.AssignmentHistories => CalculateListHeight(AssignmentHistories.Count, 118, 56, 5),
        _ => 56
    };

    public bool NeedsRefresh(TimeSpan maxAge)
        => !_lastRefreshUtc.HasValue || DateTime.UtcNow - _lastRefreshUtc.Value >= maxAge;

    public void ShowBillingProfiles() => SelectedSection = RentalMobileSection.BillingProfiles;
    public void ShowRentalAssets() => SelectedSection = RentalMobileSection.Assets;
    public void ShowBillingLogs() => SelectedSection = RentalMobileSection.BillingLogs;
    public void ShowAssignmentHistories() => SelectedSection = RentalMobileSection.AssignmentHistories;

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
            var state = await _syncCoordinator.RefreshIfServerChangedAsync("rentals-refresh", TimeSpan.FromSeconds(5));
            if (ShouldHideCachedDataAfterSyncFailure(state))
            {
                ClearRentalDisplay($"렌탈 데이터를 표시할 수 없습니다. {state.LastError}");
                return;
            }

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
            {
                StatusMessage = $"동기화 주의: {state.LastError}";
                if (ShouldHideCachedDataAfterSyncFailure(state))
                {
                    ClearRentalDisplay($"렌탈 데이터를 표시할 수 없습니다. {state.LastError}");
                    return;
                }
            }

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

        var effectiveBillingProfiles = MergeForDisplay(
            state.SyncedRentalBillingProfiles,
            state.PendingPush.RentalBillingProfiles);
        var effectiveRentalAssets = MergeForDisplay(
            state.SyncedRentalAssets,
            state.PendingPush.RentalAssets);
        var effectiveAssignmentHistories = MergeForDisplay(
            state.SyncedRentalAssetAssignmentHistories,
            state.PendingPush.RentalAssetAssignmentHistories);
        var effectiveBillingLogs = MergeForDisplay(
            state.SyncedRentalBillingLogs,
            state.PendingPush.RentalBillingLogs);
        var effectiveInvoices = MergeForDisplay(
            state.SyncedInvoices,
            state.PendingPush.Invoices);
        var effectiveTransactions = MergeForDisplay(
            state.SyncedTransactions,
            state.PendingPush.Transactions);
        var effectivePayments = MergeForDisplay(
            state.SyncedPayments,
            state.PendingPush.Payments);

        var companyMap = state.SyncedRentalManagementCompanies
            .GroupBy(x => NormalizeKey(x.Code))
            .ToDictionary(group => group.Key, group => Normalize(group.First().Name, group.First().Code), StringComparer.OrdinalIgnoreCase);
        var profileMap = effectiveBillingProfiles
            .ToDictionary(x => x.Id, x => x, EqualityComparer<Guid>.Default);
        var assetMap = effectiveRentalAssets
            .GroupBy(x => x.Id)
            .ToDictionary(group => group.Key, group => group.First(), EqualityComparer<Guid>.Default);

        var filteredProfiles = effectiveBillingProfiles
            .Where(profile => MatchesProfile(profile, companyMap))
            .OrderBy(profile => profile.CustomerName)
            .ThenBy(profile => profile.ProfileKey)
            .ToList();
        var filteredAssets = effectiveRentalAssets
            .Where(asset => MatchesAsset(asset, companyMap, profileMap))
            .OrderBy(asset => asset.CustomerName)
            .ThenBy(asset => asset.ItemName)
            .ToList();
        var filteredLogs = BuildBillingHistoryRows(
                effectiveBillingProfiles,
                effectiveInvoices,
                effectiveTransactions,
                effectivePayments,
                effectiveBillingLogs,
                profileMap)
            .Where(MatchesBillingHistory)
            .OrderByDescending(row => row.SortDate)
            .ThenBy(row => row.CustomerName)
            .ThenBy(row => row.ProfileKey)
            .ToList();
        var filteredAssignmentHistories = BuildAssignmentHistoryRows(effectiveAssignmentHistories, profileMap, assetMap)
            .Where(MatchesAssignmentHistory)
            .OrderByDescending(row => row.SortDate)
            .ThenBy(row => row.CustomerName)
            .ThenBy(row => row.ManagementNumber)
            .ToList();

        Replace(BillingProfiles, filteredProfiles);
        Replace(RentalAssets, filteredAssets);
        Replace(BillingLogs, filteredLogs);
        Replace(AssignmentHistories, filteredAssignmentHistories);

        var totalCount = filteredProfiles.Count + filteredAssets.Count + filteredLogs.Count + filteredAssignmentHistories.Count;
        _lastRefreshUtc = DateTime.UtcNow;
        StatusMessage = totalCount == 0
            ? "동기화된 렌탈 데이터가 없습니다."
            : $"렌탈 동기화 데이터 총 {totalCount:N0}건을 불러왔습니다.";
        OnPropertyChanged(nameof(CurrentSectionSummary));
        OnPropertyChanged(nameof(CurrentListHeight));
    }

    private void ClearRentalDisplay(string message)
    {
        BillingProfiles.Clear();
        RentalAssets.Clear();
        BillingLogs.Clear();
        AssignmentHistories.Clear();
        _lastRefreshUtc = DateTime.UtcNow;
        StatusMessage = message;
        OnPropertyChanged(nameof(CurrentSectionSummary));
        OnPropertyChanged(nameof(CurrentListHeight));
    }

    private static bool ShouldHideCachedDataAfterSyncFailure(MobileSyncState state)
        => !string.IsNullOrWhiteSpace(state.LastError) && !state.LastFailureAllowsCachedDisplay;

    private static List<T> MergeForDisplay<T>(
        IEnumerable<T>? synced,
        IEnumerable<T>? pending)
        where T : SyncEntityDto
    {
        var map = new Dictionary<Guid, T>();

        foreach (var entity in synced ?? Enumerable.Empty<T>())
            AddDisplayEntity(map, entity, preferIncoming: false);

        foreach (var entity in pending ?? Enumerable.Empty<T>())
            AddDisplayEntity(map, entity, preferIncoming: true);

        return map.Values.ToList();
    }

    private static void AddDisplayEntity<T>(
        IDictionary<Guid, T> map,
        T entity,
        bool preferIncoming)
        where T : SyncEntityDto
    {
        if (entity.Id == Guid.Empty)
            return;

        if (entity.IsDeleted)
        {
            map.Remove(entity.Id);
            return;
        }

        if (preferIncoming || !map.TryGetValue(entity.Id, out var current) || IsNewerDisplayEntity(entity, current))
            map[entity.Id] = entity;
    }

    private static bool IsNewerDisplayEntity<T>(T candidate, T current)
        where T : SyncEntityDto
    {
        if (candidate.Revision != current.Revision)
            return candidate.Revision > current.Revision;

        return candidate.UpdatedAtUtc >= current.UpdatedAtUtc;
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
               || Contains(profile.OfficeCode, q)
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
               || Contains(asset.OfficeCode, q)
               || Contains(asset.ManagementCompanyCode, q)
               || Contains(ResolveCompanyName(asset.ManagementCompanyCode, companyMap), q)
               || (asset.BillingProfileId.HasValue &&
                   profileMap.TryGetValue(asset.BillingProfileId.Value, out var profile) &&
                   (Contains(profile.ProfileKey, q) || Contains(profile.CustomerName, q)));
    }

    private bool MatchesBillingHistory(RentalBillingHistoryDisplayRow row)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var q = SearchText.Trim();
        return Contains(row.Title, q)
               || Contains(row.Subtitle, q)
               || Contains(row.Meta, q)
               || Contains(row.Note, q)
               || Contains(row.ProfileKey, q)
               || Contains(row.CustomerName, q)
               || Contains(row.Status, q)
               || Contains(row.SourceLabel, q)
               || Contains(row.OfficeCode, q);
    }

    private bool MatchesAssignmentHistory(RentalAssignmentHistoryDisplayRow row)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var q = SearchText.Trim();
        return Contains(row.Title, q)
               || Contains(row.Subtitle, q)
               || Contains(row.Meta, q)
               || Contains(row.Note, q)
               || Contains(row.ProfileKey, q)
               || Contains(row.CustomerName, q)
               || Contains(row.ManagementNumber, q)
               || Contains(row.MachineNumber, q)
               || Contains(row.OfficeCode, q);
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

    private static string ResolveOffice(string? responsibleOfficeCode, string? ownerOfficeCode)
        => !string.IsNullOrWhiteSpace(responsibleOfficeCode)
            ? responsibleOfficeCode.Trim()
            : Normalize(ownerOfficeCode, "미지정");

    private static double CalculateListHeight(int count, double rowHeight, double emptyHeight, int maxVisibleRows)
    {
        if (count <= 0)
            return emptyHeight;

        var visibleRows = Math.Min(count, maxVisibleRows);
        return (visibleRows * rowHeight) + Math.Max(0, visibleRows - 1) * 8;
    }

    private static List<RentalAssignmentHistoryDisplayRow> BuildAssignmentHistoryRows(
        IReadOnlyCollection<RentalAssetAssignmentHistoryDto> assignmentHistories,
        IReadOnlyDictionary<Guid, RentalBillingProfileDto> profileMap,
        IReadOnlyDictionary<Guid, RentalAssetDto> assetMap)
    {
        var rows = new List<RentalAssignmentHistoryDisplayRow>();
        foreach (var history in assignmentHistories.Where(history => !history.IsDeleted))
        {
            RentalBillingProfileDto? profile = null;
            if (history.BillingProfileId.HasValue)
                profileMap.TryGetValue(history.BillingProfileId.Value, out profile);

            assetMap.TryGetValue(history.AssetId, out var asset);
            rows.Add(RentalAssignmentHistoryDisplayRow.FromHistory(history, profile, asset));
        }

        return rows;
    }

    private static List<RentalBillingHistoryDisplayRow> BuildBillingHistoryRows(
        IReadOnlyCollection<RentalBillingProfileDto> billingProfiles,
        IReadOnlyCollection<InvoiceDto> invoices,
        IReadOnlyCollection<TransactionDto> transactions,
        IReadOnlyCollection<PaymentDto> payments,
        IReadOnlyCollection<RentalBillingLogDto> billingLogs,
        IReadOnlyDictionary<Guid, RentalBillingProfileDto> profileMap)
    {
        var rows = new List<RentalBillingHistoryDisplayRow>();
        foreach (var log in billingLogs.Where(log => !log.IsDeleted))
        {
            rows.Add(RentalBillingHistoryDisplayRow.FromLog(
                log,
                profileMap.TryGetValue(log.BillingProfileId, out var profile) ? profile : null));
        }

        var evidenceByRun = new Dictionary<(Guid ProfileId, Guid RunId), MobileRentalBillingRunEvidence>();

        MobileRentalBillingRunEvidence GetEvidence(Guid profileId, Guid runId)
        {
            var key = (profileId, runId);
            if (!evidenceByRun.TryGetValue(key, out var evidence))
            {
                evidence = new MobileRentalBillingRunEvidence(profileId, runId);
                evidenceByRun[key] = evidence;
            }

            return evidence;
        }

        foreach (var profile in billingProfiles.Where(profile => !profile.IsDeleted))
        {
            foreach (var run in ParseBillingRuns(profile.BillingRunsJson))
                GetEvidence(profile.Id, run.RunId).AddRun(profile, run);
        }

        foreach (var invoice in invoices.Where(invoice =>
                     !invoice.IsDeleted &&
                     invoice.IsLatestVersion &&
                     invoice.VoucherType == VoucherType.Sales &&
                     invoice.LinkedRentalBillingProfileId.HasValue &&
                     invoice.LinkedRentalBillingRunId.HasValue))
        {
            AddInvoiceBillingRunEvidence(
                GetEvidence(invoice.LinkedRentalBillingProfileId!.Value, invoice.LinkedRentalBillingRunId!.Value),
                invoice);
        }

        foreach (var transaction in transactions.Where(transaction =>
                      !transaction.IsDeleted &&
                      transaction.LinkedRentalBillingProfileId.HasValue &&
                      transaction.LinkedRentalBillingRunId.HasValue &&
                      transaction.SettlementAmount > 0m))
        {
            AddTransactionBillingRunEvidence(
                GetEvidence(transaction.LinkedRentalBillingProfileId!.Value, transaction.LinkedRentalBillingRunId!.Value),
                transaction);
        }

        var invoiceById = invoices
            .Where(invoice =>
                !invoice.IsDeleted &&
                invoice.IsLatestVersion &&
                invoice.VoucherType == VoucherType.Sales &&
                invoice.LinkedRentalBillingProfileId.HasValue &&
                invoice.LinkedRentalBillingRunId.HasValue)
            .GroupBy(invoice => invoice.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var transactionIds = transactions
            .Where(transaction => !transaction.IsDeleted)
            .Select(transaction => transaction.Id)
            .ToHashSet();

        foreach (var payment in payments.Where(payment => !payment.IsDeleted))
        {
            if (payment.Amount <= 0m)
                continue;

            if (!invoiceById.TryGetValue(payment.InvoiceId, out var invoice))
                continue;
            if (transactionIds.Contains(payment.Id))
                continue;

            AddPaymentBillingRunEvidence(
                GetEvidence(invoice.LinkedRentalBillingProfileId!.Value, invoice.LinkedRentalBillingRunId!.Value),
                payment);
        }

        rows.AddRange(evidenceByRun.Values
            .Where(evidence => profileMap.ContainsKey(evidence.ProfileId))
            .Select(evidence => RentalBillingHistoryDisplayRow.FromRunEvidence(
                evidence,
                profileMap[evidence.ProfileId])));

        return rows
            .GroupBy(row => row.UniqueKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(row => row.SourcePriority)
                .ThenByDescending(row => row.SortDate)
                .First())
            .ToList();
    }

    private static void AddInvoiceBillingRunEvidence(MobileRentalBillingRunEvidence evidence, InvoiceDto invoice)
    {
        evidence.InvoiceAmount += Math.Max(0m, invoice.TotalAmount);
        evidence.InvoiceDate = Max(evidence.InvoiceDate, invoice.InvoiceDate);
        evidence.OfficeCode = ResolveOffice(invoice.ResponsibleOfficeCode, invoice.OfficeCode);
        evidence.HasInvoice = true;
    }

    private static void AddTransactionBillingRunEvidence(MobileRentalBillingRunEvidence evidence, TransactionDto transaction)
    {
        if (transaction.SettlementAmount <= 0m)
            return;

        evidence.SettlementAmount += Math.Max(0m, transaction.SettlementAmount);
        evidence.SettledDate = Max(evidence.SettledDate, transaction.TransactionDate);
        evidence.OfficeCode = ResolveOffice(transaction.ResponsibleOfficeCode, transaction.OfficeCode);
        evidence.HasTransaction = true;
    }

    private static void AddPaymentBillingRunEvidence(MobileRentalBillingRunEvidence evidence, PaymentDto payment)
    {
        if (payment.Amount <= 0m)
            return;

        evidence.SettlementAmount += Math.Max(0m, payment.Amount);
        evidence.SettledDate = Max(evidence.SettledDate, payment.PaymentDate);
        evidence.HasPayment = true;
    }

    private static IReadOnlyList<MobileRentalRunSnapshot> ParseBillingRuns(string? billingRunsJson)
    {
        if (string.IsNullOrWhiteSpace(billingRunsJson))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<MobileRentalRunSnapshot>>(billingRunsJson)?
                .Where(run => run.RunId != Guid.Empty)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static DateOnly? Max(DateOnly? current, DateOnly candidate)
        => !current.HasValue || candidate > current.Value ? candidate : current;
}

public sealed class RentalAssignmentHistoryDisplayRow
{
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Meta { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string ProfileKey { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string ManagementNumber { get; init; } = string.Empty;
    public string MachineNumber { get; init; } = string.Empty;
    public string OfficeCode { get; init; } = string.Empty;
    public DateTime SortDate { get; init; }

    public static RentalAssignmentHistoryDisplayRow FromHistory(
        RentalAssetAssignmentHistoryDto history,
        RentalBillingProfileDto? profile,
        RentalAssetDto? asset)
    {
        var customerName = FirstText(history.CustomerName, profile?.CustomerName, asset?.CustomerName, "거래처 미지정");
        var itemName = FirstText(history.ItemName, profile?.ItemName, asset?.ItemName, "품목 미지정");
        var profileKey = FirstText(history.BillingProfileDisplay, profile?.ProfileKey, "프로필 미지정");
        var managementNumber = FirstText(history.ManagementNumber, asset?.ManagementNumber, asset?.AssetKey, "관리번호 미지정");
        var machineNumber = FirstText(history.MachineNumber, asset?.MachineNumber, "기계번호 미지정");
        var installLocation = FirstText(history.InstallLocation, asset?.InstallLocation, asset?.CurrentLocation, "설치 위치 미지정");
        var officeCode = FirstText(history.ResponsibleOfficeCode, asset?.ResponsibleOfficeCode, profile?.ResponsibleOfficeCode, history.OfficeCode, "미정");
        var stateLabel = history.IsCurrent ? "현재" : "과거";
        var linkedAt = history.LinkedAtUtc == default ? DateTime.MinValue : history.LinkedAtUtc;

        return new RentalAssignmentHistoryDisplayRow
        {
            Title = $"{stateLabel} · {customerName} · {itemName}",
            Subtitle = $"관리번호 {managementNumber} / 기계번호 {machineNumber}",
            Meta = $"연결 {FormatDateTime(history.LinkedAtUtc)} / 해제 {FormatDateTime(history.UnlinkedAtUtc)} / 월 {history.MonthlyFee:N0}원 / 지점 {officeCode}",
            Note = string.IsNullOrWhiteSpace(history.ChangeReason)
                ? $"{profileKey} · {installLocation}"
                : $"{history.ChangeReason.Trim()} · {profileKey} · {installLocation}",
            ProfileKey = profileKey,
            CustomerName = customerName,
            ManagementNumber = managementNumber,
            MachineNumber = machineNumber,
            OfficeCode = officeCode,
            SortDate = linkedAt
        };
    }

    private static string FirstText(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FormatDateTime(DateTime? value)
    {
        if (!value.HasValue || value.Value == default)
            return "미정";

        var dateTime = value.Value.Kind == DateTimeKind.Utc
            ? value.Value.ToLocalTime()
            : value.Value;
        return dateTime.ToString("yyyy-MM-dd HH:mm");
    }
}

public sealed class MobileRentalBillingRunEvidence
{
    public MobileRentalBillingRunEvidence(Guid profileId, Guid runId)
    {
        ProfileId = profileId;
        RunId = runId;
    }

    public Guid ProfileId { get; }
    public Guid RunId { get; }
    public MobileRentalRunSnapshot? Run { get; private set; }
    public decimal RunBilledAmount { get; private set; }
    public decimal RunSettledAmount { get; private set; }
    public decimal InvoiceAmount { get; set; }
    public decimal SettlementAmount { get; set; }
    public DateOnly? InvoiceDate { get; set; }
    public DateOnly? SettledDate { get; set; }
    public string OfficeCode { get; set; } = string.Empty;
    public bool HasInvoice { get; set; }
    public bool HasTransaction { get; set; }
    public bool HasPayment { get; set; }

    public void AddRun(RentalBillingProfileDto profile, MobileRentalRunSnapshot run)
    {
        Run = run;
        RunBilledAmount = Math.Max(RunBilledAmount, Math.Max(0m, run.BilledAmount));
        RunSettledAmount = Math.Max(RunSettledAmount, Math.Max(0m, run.SettledAmount));
        SettledDate = Max(SettledDate, run.SettledDate);
        OfficeCode = ResolveOffice(profile.ResponsibleOfficeCode, profile.OfficeCode);
    }

    private static DateOnly? Max(DateOnly? current, DateOnly? candidate)
        => candidate.HasValue && (!current.HasValue || candidate.Value > current.Value)
            ? candidate
            : current;

    private static string ResolveOffice(string? responsibleOfficeCode, string? ownerOfficeCode)
        => !string.IsNullOrWhiteSpace(responsibleOfficeCode)
            ? responsibleOfficeCode.Trim()
            : Normalize(ownerOfficeCode, "미지정");

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

public sealed class RentalBillingHistoryDisplayRow
{
    public required string UniqueKey { get; init; }
    public required string SourceLabel { get; init; }
    public required int SourcePriority { get; init; }
    public required string ProfileKey { get; init; }
    public required string CustomerName { get; init; }
    public required string Status { get; init; }
    public required string OfficeCode { get; init; }
    public required DateOnly SortDate { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Meta { get; init; }
    public required string Note { get; init; }

    public static RentalBillingHistoryDisplayRow FromLog(RentalBillingLogDto log, RentalBillingProfileDto? profile)
        => new()
        {
            UniqueKey = $"log:{log.Id:D}",
            SourceLabel = "청구로그",
            SourcePriority = 1,
            ProfileKey = profile?.ProfileKey ?? string.Empty,
            CustomerName = profile?.CustomerName ?? string.Empty,
            Status = Normalize(log.Status, "예정"),
            OfficeCode = ResolveOffice(log.ResponsibleOfficeCode, log.OfficeCode),
            SortDate = log.ProcessedDate ?? log.ScheduledDate,
            Title = string.IsNullOrWhiteSpace(profile?.CustomerName)
                ? $"청구로그 {log.BillingYearMonth}"
                : $"{profile.CustomerName} · {log.BillingYearMonth}",
            Subtitle = $"{Normalize(profile?.ProfileKey, "프로필 미지정")} · {Normalize(log.Status, "예정")} · {log.BilledAmount:N0}원",
            Meta = $"청구로그 / 예정일 {log.ScheduledDate:yyyy-MM-dd} / 처리일 {FormatDate(log.ProcessedDate)} / 지점 {ResolveOffice(log.ResponsibleOfficeCode, log.OfficeCode)}",
            Note = Normalize(log.Note, "메모 없음")
        };

    public static RentalBillingHistoryDisplayRow FromRunEvidence(
        MobileRentalBillingRunEvidence evidence,
        RentalBillingProfileDto profile)
    {
        var run = evidence.Run;
        var cycleMonths = Math.Max(1, run?.CycleMonths ?? profile.BillingCycleMonths);
        var scheduledDate = run?.ScheduledDate
                            ?? evidence.InvoiceDate
                            ?? evidence.SettledDate
                            ?? profile.LastSettledDate
                            ?? profile.LastBilledDate
                            ?? DateOnly.FromDateTime(DateTime.Today);
        var settledAmount = evidence.SettlementAmount > 0m
            ? evidence.SettlementAmount
            : evidence.RunSettledAmount;
        var billedAmount = evidence.InvoiceAmount > 0m
            ? evidence.InvoiceAmount
            : evidence.RunBilledAmount > 0m
                ? evidence.RunBilledAmount
                : Math.Max(Math.Max(0m, profile.MonthlyAmount) * cycleMonths, settledAmount);
        var outstandingAmount = Math.Max(0m, billedAmount - settledAmount);
        var source = ResolveEvidenceSource(evidence);

        return new RentalBillingHistoryDisplayRow
        {
            UniqueKey = $"run:{evidence.ProfileId:D}:{evidence.RunId:D}",
            SourceLabel = source,
            SourcePriority = 2,
            ProfileKey = profile.ProfileKey,
            CustomerName = profile.CustomerName,
            Status = RentalBillingEvidenceStatusResolver.Resolve(
                run?.Status,
                evidence.HasInvoice || evidence.HasTransaction || evidence.HasPayment,
                settledAmount,
                outstandingAmount,
                billedAmount),
            OfficeCode = Normalize(evidence.OfficeCode, ResolveOffice(profile.ResponsibleOfficeCode, profile.OfficeCode)),
            SortDate = evidence.SettledDate ?? evidence.InvoiceDate ?? scheduledDate,
            Title = string.IsNullOrWhiteSpace(profile.CustomerName)
                ? $"청구회차 {scheduledDate:yyyy-MM-dd}"
                : $"{profile.CustomerName} · {scheduledDate:yyyy-MM-dd}",
            Subtitle = $"{Normalize(profile.ProfileKey, "프로필 미지정")} · {source} · 청구 {billedAmount:N0}원 · 수금 {settledAmount:N0}원",
            Meta = $"기간 {Normalize(run?.PeriodLabel, "기간 미지정")} / 예정일 {scheduledDate:yyyy-MM-dd} / 미수 {outstandingAmount:N0}원 / 지점 {Normalize(evidence.OfficeCode, ResolveOffice(profile.ResponsibleOfficeCode, profile.OfficeCode))}",
            Note = run is null
                ? "전표/수금 근거로 복원된 청구 이력"
                : "청구회차와 전표/수금 근거를 함께 표시합니다."
        };
    }

    private static string ResolveEvidenceSource(MobileRentalBillingRunEvidence evidence)
    {
        if (evidence.HasInvoice && evidence.HasTransaction)
            return "전표·수금";
        if (evidence.HasInvoice && evidence.HasPayment)
            return "전표·결제";
        if (evidence.HasInvoice)
            return "전표";
        if (evidence.HasTransaction)
            return "수금";
        if (evidence.HasPayment)
            return "결제";
        return "청구회차";
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string ResolveOffice(string? responsibleOfficeCode, string? ownerOfficeCode)
        => !string.IsNullOrWhiteSpace(responsibleOfficeCode)
            ? responsibleOfficeCode.Trim()
            : Normalize(ownerOfficeCode, "미지정");

    private static string FormatDate(DateOnly? value)
        => value.HasValue ? value.Value.ToString("yyyy-MM-dd") : "미처리";
}

public sealed class MobileRentalRunSnapshot
{
    public Guid RunId { get; set; }
    public string RunKey { get; set; } = string.Empty;
    public DateOnly ScheduledDate { get; set; }
    public DateOnly PeriodStartDate { get; set; }
    public DateOnly PeriodEndDate { get; set; }
    public int CycleMonths { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal BilledAmount { get; set; }
    public decimal SettledAmount { get; set; }
    public DateOnly? SettledDate { get; set; }
}
