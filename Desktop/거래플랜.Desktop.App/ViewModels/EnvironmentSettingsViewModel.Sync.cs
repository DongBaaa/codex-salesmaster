using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed class SyncStoredCredentialRow
{
    public string OfficeCode { get; init; } = string.Empty;
    public string OfficeDisplayName { get; init; } = string.Empty;
    public string TenantDisplayName { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public DateTime SavedAtUtc { get; init; }
    public string SavedAtText { get; init; } = "없음";
    public string StatusText { get; init; } = string.Empty;
    public bool IsCurrentOffice { get; init; }
}

public sealed class SyncScopeStatusRow
{
    public string ScopeKey { get; init; } = string.Empty;
    public string ScopeDisplayName { get; init; } = string.Empty;
    public string ScopeTypeText { get; init; } = string.Empty;
    public string RequiredOfficeCode { get; init; } = string.Empty;
    public string TargetTenantCode { get; init; } = string.Empty;
    public int PendingCount { get; init; }
    public string PendingSummary { get; init; } = string.Empty;
    public string CredentialStatusText { get; init; } = string.Empty;
    public string CredentialUsername { get; init; } = string.Empty;
    public string SavedAtText { get; init; } = "없음";
    public bool HasStoredCredential { get; init; }
    public bool IsCurrentSessionScope { get; init; }
}

public sealed partial class EnvironmentSettingsViewModel
{
    [ObservableProperty] private string _syncModeText = "동기화 상태 확인 대기";
    [ObservableProperty] private string _syncDatabaseText = "-";
    [ObservableProperty] private string _syncPendingChangesText = "미동기화 변경 확인 대기";
    [ObservableProperty] private string _syncSummaryText = "동기화, 동기화 진단, 백업을 한 곳에서 실행할 수 있습니다.";
    [ObservableProperty] private string _syncLastSuccessText = "없음";
    [ObservableProperty] private string _syncLastFailureText = "없음";
    [ObservableProperty] private string _syncLastErrorText = "없음";
    [ObservableProperty] private string _syncStoredCredentialsSummaryText = "저장된 지점 로그인 없음";
    [ObservableProperty] private string _syncScopeStatusSummaryText = "지점별 동기화 상태 확인 대기";
    [ObservableProperty] private string _syncCredentialOfficeCode = DomainConstants.OfficeUsenet;
    [ObservableProperty] private string _syncCredentialUsername = string.Empty;
    [ObservableProperty] private string _syncCredentialPassword = string.Empty;
    [ObservableProperty] private SyncStoredCredentialRow? _selectedStoredSyncCredential;
    [ObservableProperty] private SyncScopeStatusRow? _selectedSyncScopeStatus;

    public ObservableCollection<SyncStoredCredentialRow> StoredSyncCredentials { get; } = new();
    public ObservableCollection<SyncScopeStatusRow> SyncScopeStatuses { get; } = new();

    private void InitializeSyncState()
    {
        SyncModeText = _session.IsOfflineMode ? "오프라인 모드" : "온라인 동기화";
        SyncDatabaseText = _session.SelectedBusinessDatabaseLabel;
        SyncPendingChangesText = "미동기화 변경 확인 대기";
        SyncSummaryText = _session.IsOfflineMode
            ? "오프라인 모드에서는 로컬 데이터 확인과 백업 중심으로 작업합니다."
            : "동기화, 동기화 진단, 백업을 이 탭에서 바로 실행할 수 있습니다.";
        SyncLastSuccessText = "없음";
        SyncLastFailureText = "없음";
        SyncLastErrorText = "없음";
        SyncStoredCredentialsSummaryText = "저장된 지점 로그인 없음";
        SyncScopeStatusSummaryText = "지점별 동기화 상태 확인 대기";
        SyncCredentialOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
        SyncCredentialUsername = _session.User?.Username ?? string.Empty;
        SyncCredentialPassword = string.Empty;
        StoredSyncCredentials.Clear();
        SyncScopeStatuses.Clear();
    }

    partial void OnSelectedStoredSyncCredentialChanged(SyncStoredCredentialRow? value)
    {
        if (value is null)
            return;

        SyncCredentialOfficeCode = value.OfficeCode;
        SyncCredentialUsername = value.Username;
        SyncCredentialPassword = string.Empty;
    }

    partial void OnSelectedSyncScopeStatusChanged(SyncScopeStatusRow? value)
    {
        if (value is null)
            return;

        if (!string.IsNullOrWhiteSpace(value.RequiredOfficeCode))
            SyncCredentialOfficeCode = value.RequiredOfficeCode;

        if (!string.IsNullOrWhiteSpace(value.CredentialUsername))
            SyncCredentialUsername = value.CredentialUsername;
        else if (value.IsCurrentSessionScope)
            SyncCredentialUsername = _session.User?.Username ?? string.Empty;

        SyncCredentialPassword = string.Empty;
    }

    private async Task RefreshSyncStateAsync()
    {
        SyncModeText = _session.IsOfflineMode ? "오프라인 모드" : "온라인 동기화";
        SyncDatabaseText = _session.SelectedBusinessDatabaseLabel;

        await _local.ClearInvalidOfficeSyncCredentialsAsync();

        var pendingSummary = await _local.GetPendingSyncSummaryAsync();
        SyncPendingChangesText = pendingSummary.TotalCount == 0
            ? "미동기화 변경 없음"
            : pendingSummary.BuildWaitingMessage();

        var summary = await _diagnostics.GetSummaryAsync();
        var manualReviewIssueCount = Math.Max(0, summary.OpenIssueCount - summary.RecoverableIssueCount);
        var syncSummaryText = summary.OpenIssueCount == 0
            ? "현재 미해결 동기화 확인 항목이 없습니다."
            : $"현재 미해결 동기화 확인 항목 {summary.OpenIssueCount:N0}건 / 자동 복구 가능 {summary.RecoverableIssueCount:N0}건 / 수동 확인 필요 {manualReviewIssueCount:N0}건";
        var lastConflictSummary = await _local.GetSettingAsync("Sync.LastConflictSummary");
        if (!string.IsNullOrWhiteSpace(lastConflictSummary))
            syncSummaryText += Environment.NewLine + lastConflictSummary.Trim();

        SyncSummaryText = syncSummaryText;
        SyncLastSuccessText = summary.LastSuccessAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "없음";
        SyncLastFailureText = summary.LastFailureAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "없음";
        SyncLastErrorText = string.IsNullOrWhiteSpace(summary.LastError) ? "없음" : summary.LastError;

        var storedCredentials = await _local.GetStoredSyncCredentialsAsync();
        ApplyStoredSyncCredentials(storedCredentials);
        ApplySyncScopeStatuses(pendingSummary, storedCredentials);
    }

    private void ApplyStoredSyncCredentials(IReadOnlyList<StoredSyncCredential> credentials)
    {
        StoredSyncCredentials.Clear();
        foreach (var credential in credentials.OrderByDescending(current => current.SavedAtUtc))
        {
            var officeDisplayName = OfficeCodeCatalog.GetOfficeDisplayName(credential.OfficeCode);
            var isCurrentOffice = string.Equals(
                OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, string.Empty),
                credential.OfficeCode,
                StringComparison.OrdinalIgnoreCase);
            StoredSyncCredentials.Add(new SyncStoredCredentialRow
            {
                OfficeCode = credential.OfficeCode,
                OfficeDisplayName = officeDisplayName,
                TenantDisplayName = TenantScopeCatalog.GetTenantDisplayName(credential.TenantCode),
                Username = credential.Username,
                SavedAtUtc = credential.SavedAtUtc,
                SavedAtText = credential.SavedAtUtc == default
                    ? "없음"
                    : credential.SavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                StatusText = isCurrentOffice ? "현재 로그인 지점" : "저장된 지점 로그인",
                IsCurrentOffice = isCurrentOffice
            });
        }

        SyncStoredCredentialsSummaryText = StoredSyncCredentials.Count == 0
            ? "저장된 지점 로그인 없음"
            : $"저장된 지점 로그인 {StoredSyncCredentials.Count:N0}건 / {string.Join(", ", StoredSyncCredentials.Select(current => current.OfficeDisplayName))}";
    }

    private void ApplySyncScopeStatuses(PendingSyncSummary pendingSummary, IReadOnlyList<StoredSyncCredential> storedCredentials)
    {
        SyncScopeStatuses.Clear();

        var credentialMap = storedCredentials
            .GroupBy(current => current.OfficeCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(current => current.SavedAtUtc).First(), StringComparer.OrdinalIgnoreCase);

        var groupedBuckets = pendingSummary.Buckets
            .GroupBy(bucket => bucket.ScopeKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var scopeKeys = new HashSet<string>(groupedBuckets.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var officeCode in credentialMap.Keys)
            scopeKeys.Add($"OFFICE:{officeCode}");

        var currentOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, string.Empty);
        var currentTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(_session.TenantCode, _session.OfficeCode);
        if (!string.IsNullOrWhiteSpace(currentOfficeCode))
            scopeKeys.Add($"OFFICE:{currentOfficeCode}");

        if (pendingSummary.Buckets.Any(bucket => string.Equals(bucket.ScopeKey, "SHARED", StringComparison.OrdinalIgnoreCase)))
            scopeKeys.Add("SHARED");

        foreach (var scopeKey in scopeKeys
                     .OrderByDescending(scope => groupedBuckets.TryGetValue(scope, out var buckets) ? buckets.Sum(bucket => bucket.Count) : 0)
                     .ThenBy(scope => scope, StringComparer.OrdinalIgnoreCase))
        {
            groupedBuckets.TryGetValue(scopeKey, out var buckets);
            buckets ??= [];

            var pendingCount = buckets.Sum(bucket => bucket.Count);
            var pendingSummaryText = pendingCount == 0
                ? "대기 변경 없음"
                : string.Join(", ",
                    buckets.OrderByDescending(bucket => bucket.Count)
                        .ThenBy(bucket => bucket.EntityDisplayName, StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .Select(bucket => $"{bucket.EntityDisplayName} {bucket.Count:N0}건"));

            var scopeMetadata = ResolveSyncScopeMetadata(scopeKey);
            credentialMap.TryGetValue(scopeMetadata.RequiredOfficeCode, out var storedCredential);
            var isCurrentScope = scopeMetadata.IsShared
                ? _session.HasAdministrativePrivileges
                : scopeMetadata.IsOfficeScope
                    ? !string.IsNullOrWhiteSpace(currentOfficeCode) &&
                      string.Equals(currentOfficeCode, scopeMetadata.RequiredOfficeCode, StringComparison.OrdinalIgnoreCase)
                    : !string.IsNullOrWhiteSpace(currentTenantCode) &&
                      string.Equals(currentTenantCode, scopeMetadata.TargetTenantCode, StringComparison.OrdinalIgnoreCase);

            SyncScopeStatuses.Add(new SyncScopeStatusRow
            {
                ScopeKey = scopeKey,
                ScopeDisplayName = scopeMetadata.ScopeDisplayName,
                ScopeTypeText = scopeMetadata.ScopeTypeText,
                RequiredOfficeCode = scopeMetadata.RequiredOfficeCode,
                TargetTenantCode = scopeMetadata.TargetTenantCode,
                PendingCount = pendingCount,
                PendingSummary = pendingSummaryText,
                CredentialStatusText = ResolveSyncScopeCredentialStatus(
                    scopeMetadata.IsShared,
                    scopeMetadata.ScopeDisplayName,
                    scopeMetadata.RequiredOfficeCode,
                    isCurrentScope,
                    pendingCount,
                    storedCredential),
                CredentialUsername = storedCredential?.Username ?? (isCurrentScope ? (_session.User?.Username ?? string.Empty) : string.Empty),
                SavedAtText = storedCredential is null
                    ? "없음"
                    : storedCredential.SavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                HasStoredCredential = storedCredential is not null,
                IsCurrentSessionScope = isCurrentScope
            });
        }

        SyncScopeStatusSummaryText = SyncScopeStatuses.Count == 0
            ? "지점별 동기화 상태 없음"
            : string.Join(", ", SyncScopeStatuses
                .Where(current => current.PendingCount > 0)
                .Select(current => $"{current.ScopeDisplayName} {current.PendingCount:N0}건")
                .DefaultIfEmpty("대기 변경 없음"));
    }

    private string ResolveSyncScopeCredentialStatus(
        bool isShared,
        string scopeDisplayName,
        string requiredOfficeCode,
        bool isCurrentScope,
        int pendingCount,
        StoredSyncCredential? storedCredential)
    {
        if (isShared)
        {
            if (_session.HasAdministrativePrivileges)
                return pendingCount > 0 ? "현재 관리자 세션으로 처리" : "공용 변경 없음";

            return pendingCount > 0 ? "관리자 로그인 필요" : "공용 변경 없음";
        }

        if (isCurrentScope)
            return pendingCount > 0 ? "현재 로그인으로 처리" : "현재 로그인 지점";

        var targetCredentialDisplayName = string.IsNullOrWhiteSpace(requiredOfficeCode)
            ? scopeDisplayName
            : OfficeCodeCatalog.GetOfficeDisplayName(requiredOfficeCode);

        if (storedCredential is not null)
            return pendingCount > 0 ? $"저장된 {targetCredentialDisplayName} 계정으로 추가 처리" : $"저장된 {targetCredentialDisplayName} 계정 준비";

        return pendingCount > 0 ? $"{targetCredentialDisplayName} 로그인 필요" : "저장 계정 없음";
    }

    private static (bool IsShared, bool IsOfficeScope, string RequiredOfficeCode, string TargetTenantCode, string ScopeDisplayName, string ScopeTypeText) ResolveSyncScopeMetadata(string scopeKey)
    {
        if (string.Equals(scopeKey, "SHARED", StringComparison.OrdinalIgnoreCase))
            return (true, false, string.Empty, string.Empty, "공용 마스터", "공용");

        if (scopeKey.StartsWith("OFFICE:", StringComparison.OrdinalIgnoreCase))
        {
            var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(scopeKey[7..], string.Empty);
            return (false, true, officeCode, string.Empty, OfficeCodeCatalog.GetOfficeDisplayName(officeCode), "지점");
        }

        if (scopeKey.StartsWith("TENANT:", StringComparison.OrdinalIgnoreCase))
        {
            var tenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(scopeKey[7..], string.Empty);
            var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                TenantScopeCatalog.GetOfficeCodesForTenant(tenantCode).FirstOrDefault(),
                string.Empty);
            return (false, false, officeCode, tenantCode, TenantScopeCatalog.GetTenantDisplayName(tenantCode), "업체");
        }

        return (false, false, string.Empty, string.Empty, scopeKey, "범위");
    }

    [RelayCommand]
    private async Task SaveSyncCredentialAsync()
    {
        if (IsBusy)
            return;

        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(SyncCredentialOfficeCode, string.Empty);
        if (string.IsNullOrWhiteSpace(officeCode))
        {
            StatusMessage = "저장할 지점을 선택하세요.";
            return;
        }

        var username = (SyncCredentialUsername ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(SyncCredentialPassword))
        {
            StatusMessage = "지점 로그인 아이디와 비밀번호를 입력하세요.";
            return;
        }

        IsBusy = true;
        try
        {
            var login = await _api.LoginAsync(username, SyncCredentialPassword);
            if (login?.User is null || string.IsNullOrWhiteSpace(login.Token))
            {
                StatusMessage = "지점 로그인 검증에 실패했습니다. 아이디/비밀번호를 확인하세요.";
                return;
            }

            var loginOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                login.User.OfficeCode,
                TenantScopeCatalog.GetOfficeCodesForTenant(login.User.TenantCode).FirstOrDefault() ?? DomainConstants.OfficeUsenet);
            if (!string.Equals(loginOfficeCode, officeCode, StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = $"선택한 지점은 {OfficeCodeCatalog.GetOfficeDisplayName(officeCode)}인데, 입력한 계정은 {OfficeCodeCatalog.GetOfficeDisplayName(loginOfficeCode)} 지점입니다.";
                return;
            }

            await _local.SaveOfficeSyncCredentialAsync(login.User, username, SyncCredentialPassword);
            SyncCredentialPassword = string.Empty;
            await RefreshSyncStateAsync();
            StatusMessage = $"{OfficeCodeCatalog.GetOfficeDisplayName(officeCode)} 지점 로그인 정보를 저장했습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSyncCredentialAsync()
    {
        var target = SelectedStoredSyncCredential;
        if (target is null)
        {
            StatusMessage = "삭제할 저장 지점 로그인을 선택하세요.";
            return;
        }

        await _local.ClearOfficeSyncCredentialAsync(target.OfficeCode);
        SelectedStoredSyncCredential = null;
        await RefreshSyncStateAsync();
        StatusMessage = $"{target.OfficeDisplayName} 저장 로그인 정보를 삭제했습니다.";
    }

    [RelayCommand]
    private async Task RunSyncAsync()
    {
        if (IsBusy)
            return;

        if (_session.IsOfflineMode)
        {
            StatusMessage = "오프라인 모드에서는 서버 동기화를 실행할 수 없습니다.";
            await RefreshSyncStateAsync();
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "동기화를 실행하는 중...";
            var syncOk = await _sync.TrySyncAsync();
            var dirtyCount = await _local.CountDirtyAsync(_session);
            if (syncOk && dirtyCount == 0)
                await _sync.RefreshSharedMirrorFromServerAsync();

            await RefreshSyncStateAsync();
            StatusMessage = dirtyCount > 0
                ? await _local.GetPendingSyncWaitingMessageAsync(_session, "동기화 작업은 완료됐지만")
                    ?? $"동기화 작업은 완료됐지만 서버 반영 대기 데이터 {dirtyCount:N0}건이 남아 있습니다. 동기화 진단을 확인하세요."
                : syncOk
                    ? "동기화를 완료했습니다."
                    : "동기화가 완료되었지만 확인이 필요한 항목이 남아 있습니다. 동기화 진단을 확인하세요.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveSelectedSyncScopeCredentialAsync()
    {
        var target = SelectedSyncScopeStatus;
        if (target is null)
        {
            StatusMessage = "계정을 저장할 범위를 선택하세요.";
            return;
        }

        if (string.Equals(target.ScopeKey, "SHARED", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "공용 마스터 범위는 별도 지점 계정 저장 대상이 아닙니다. 관리자 전체 범위 로그인으로 처리합니다.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(target.RequiredOfficeCode))
            SyncCredentialOfficeCode = target.RequiredOfficeCode;
        if (!string.IsNullOrWhiteSpace(target.CredentialUsername))
            SyncCredentialUsername = target.CredentialUsername;

        if (string.IsNullOrWhiteSpace(SyncCredentialPassword))
        {
            StatusMessage = $"{OfficeCodeCatalog.GetOfficeDisplayName(SyncCredentialOfficeCode)} 계정 비밀번호를 입력한 뒤 저장하세요.";
            return;
        }

        await SaveSyncCredentialAsync();
    }

    [RelayCommand]
    private async Task RunSelectedSyncScopeSyncAsync()
    {
        if (IsBusy)
            return;

        var target = SelectedSyncScopeStatus;
        if (target is null)
        {
            StatusMessage = "동기화할 범위를 선택하세요.";
            return;
        }

        if (_session.IsOfflineMode)
        {
            StatusMessage = "오프라인 모드에서는 선택 범위 동기화를 실행할 수 없습니다.";
            await RefreshSyncStateAsync();
            return;
        }

        if (target.PendingCount <= 0)
        {
            StatusMessage = $"{target.ScopeDisplayName} 범위에는 남은 변경이 없습니다.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = $"{target.ScopeDisplayName} 범위 동기화를 실행하는 중...";
            var result = await _sync.TrySyncScopeAsync(target.ScopeKey);
            var dirtyCount = await _local.CountDirtyAsync();
            if (result.Succeeded && dirtyCount == 0)
                await _sync.RefreshSharedMirrorFromServerAsync();

            await RefreshSyncStateAsync();
            StatusMessage = result.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ViewSelectedSyncScopePendingAsync()
    {
        var target = SelectedSyncScopeStatus;
        if (target is null)
        {
            StatusMessage = "확인할 범위를 선택하세요.";
            return;
        }

        if (target.PendingCount <= 0)
        {
            StatusMessage = $"{target.ScopeDisplayName} 범위에는 남은 dirty가 없습니다.";
            return;
        }

        var blockingReason = await _local.GetPendingSyncBlockingReasonAsync(_session, target.ScopeKey);
        await OpenSyncDiagnosticsWindowAsync(target);
        if (blockingReason is not null)
            StatusMessage = blockingReason.Message;
    }

    [RelayCommand]
    private async Task OpenSyncDiagnosticsAsync()
    {
        if (IsBusy)
            return;

        await OpenSyncDiagnosticsWindowAsync();
    }

    [RelayCommand]
    private async Task OpenDataIntegrityAlertAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        DataIntegrityScanResult result;
        try
        {
            StatusMessage = "운영 점검 알림을 불러오는 중...";
            result = await _dataIntegrity.ScanAsync(_session);
        }
        finally
        {
            IsBusy = false;
        }

        if (!result.HasIssues)
        {
            var ownerWindow = ResolveActiveWindow();
            const string noIssuesMessage = "현재 확인된 운영 위험 신호가 없습니다.";
            if (ownerWindow is not null)
            {
                MessageBox.Show(
                    ownerWindow,
                    noIssuesMessage,
                    "운영 점검 알림",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    noIssuesMessage,
                    "운영 점검 알림",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            StatusMessage = noIssuesMessage;
            return;
        }

        var owner = ResolveActiveWindow();
        var window = new DataIntegrityAlertWindow
        {
            DataContext = new DataIntegrityAlertViewModel(result)
        };

        if (owner is not null)
            window.Owner = owner;

        window.NonClosingActionRequested += async (_, args) =>
        {
            try
            {
                await HandleDataIntegrityAlertActionAsync(args.Action, args.Summary, window, result);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    window,
                    $"운영 점검 바로가기를 열지 못했습니다.{Environment.NewLine}{ex.Message}",
                    "운영 점검",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        };

        if (window.ShowDialog() == true)
            await HandleDataIntegrityAlertActionAsync(window.RequestedAction, window.RequestedSummary, owner, result);
        else
            StatusMessage = $"운영 점검 알림 {result.TotalIssueCount:N0}건을 확인했습니다.";

        await RefreshSyncStateAsync();
    }

    private async Task HandleDataIntegrityAlertActionAsync(
        DataIntegrityAlertAction action,
        DataIntegrityIssueSummary? summary,
        Window? ownerOverride = null,
        DataIntegrityScanResult? existingScanResult = null)
    {
        if (action == DataIntegrityAlertAction.None)
            return;

        if (_isDataIntegrityNavigationBusy)
            return;

        _isDataIntegrityNavigationBusy = true;
        try
        {
            if (action == DataIntegrityAlertAction.Details)
            {
                await OpenDataIntegrityIssueWindowAsync(summary?.Code, ownerOverride, existingScanResult);
                return;
            }

            if (action != DataIntegrityAlertAction.Fix)
                return;

            if (summary is null)
            {
                await OpenDataIntegrityIssueWindowAsync(null, ownerOverride, existingScanResult);
                return;
            }

            var scan = existingScanResult ?? await _dataIntegrity.ScanAsync(_session);
            var issues = scan.Issues
                .Where(issue => string.Equals(issue.Code, summary.Code, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (issues.Count == 1)
            {
                await OpenDataIntegrityFixTargetAsync(issues[0], ownerOverride);
                return;
            }

            await OpenDataIntegrityIssueWindowAsync(summary.Code, ownerOverride, scan);
        }
        finally
        {
            _isDataIntegrityNavigationBusy = false;
        }
    }

    private async Task OpenDataIntegrityIssueWindowAsync(
        string? initialCode,
        Window? ownerOverride = null,
        DataIntegrityScanResult? initialScanResult = null)
    {
        var viewModel = new DataIntegrityIssueViewModel(_dataIntegrity, _session, initialCode, initialScanResult);
        await viewModel.LoadAsync();

        var owner = ownerOverride ?? ResolveActiveWindow();
        var window = new DataIntegrityIssueWindow(viewModel);
        if (owner is not null)
            window.Owner = owner;

        if (window.ShowDialog() == true && window.RequestedIssue is not null)
            await OpenDataIntegrityFixTargetAsync(window.RequestedIssue, ownerOverride);
        else
            StatusMessage = string.IsNullOrWhiteSpace(initialCode)
                ? "운영 점검 상세 창을 열었습니다."
                : "선택한 운영 점검 유형 상세를 열었습니다.";
    }

    private async Task OpenDataIntegrityFixTargetAsync(DataIntegrityIssueDetail issue, Window? ownerOverride = null)
    {
        var owner = ownerOverride ?? ResolveActiveWindow();
        switch (issue.DirectActionKind)
        {
            case DataIntegrityDirectActionKind.OpenRentalBillingProfile when issue.ProfileId.HasValue:
            {
                var billingViewModel = new RentalBillingViewModel(_rental, _local, _session);
                await billingViewModel.LoadAndSelectProfileAsync(issue.ProfileId.Value);
                var window = new RentalBillingWindow(billingViewModel);
                if (owner is not null)
                    window.Owner = owner;

                window.ShowDialog();
                StatusMessage = "운영 점검 항목의 렌탈 청구관리 화면을 열었습니다.";
                break;
            }
            case DataIntegrityDirectActionKind.OpenRentalAsset when issue.AssetId.HasValue:
            case DataIntegrityDirectActionKind.OpenRentalBillingProfile when issue.AssetId.HasValue:
            {
                var assetViewModel = new RentalAssetViewModel(_rental, _local, _rentalDocuments, _invoicePrintService, _session);
                await assetViewModel.LoadAndSelectAssetAsync(issue.AssetId.Value);
                var window = new RentalAssetWindow(assetViewModel);
                if (owner is not null)
                    window.Owner = owner;

                window.ShowDialog();
                StatusMessage = "운영 점검 항목의 렌탈 자산 화면을 열었습니다.";
                break;
            }
            case DataIntegrityDirectActionKind.OpenInventoryItem when issue.EntityId.HasValue:
            {
                var inventoryViewModel = new InventoryViewModel(_local, _session);
                await inventoryViewModel.LoadAndSelectItemAsync(issue.EntityId.Value);
                var window = new InventoryWindow(inventoryViewModel);
                if (owner is not null)
                    window.Owner = owner;

                window.ShowDialog();
                StatusMessage = "운영 점검 항목의 품목/재고 화면을 열었습니다.";
                break;
            }
            case DataIntegrityDirectActionKind.OpenCustomer when issue.EntityId.HasValue:
            {
                var customer = await _local.GetCustomerAsync(issue.EntityId.Value, _session);
                if (customer is null)
                {
                    ShowDataIntegrityNavigationMessage(owner, "거래처를 찾을 수 없어 거래처 수정창을 열 수 없습니다.");
                    StatusMessage = "운영 점검 항목의 거래처를 찾지 못했습니다.";
                    break;
                }

                var customerViewModel = new CustomerEditViewModel(_local, _session);
                await customerViewModel.LoadAsync(customer);
                var window = new CustomerEditWindow(customerViewModel);
                if (owner is not null)
                    window.Owner = owner;

                window.ShowDialog();
                StatusMessage = "운영 점검 항목의 거래처 수정창을 열었습니다.";
                break;
            }
            case DataIntegrityDirectActionKind.OpenInvoice when issue.EntityId.HasValue:
            {
                var invoice = await _local.GetInvoiceAsync(issue.EntityId.Value, _session);
                if (invoice is null)
                {
                    ShowDataIntegrityNavigationMessage(owner, "전표를 찾을 수 없어 전표 작성창을 열 수 없습니다.");
                    StatusMessage = "운영 점검 항목의 전표를 찾지 못했습니다.";
                    break;
                }

                var entryType = invoice.VoucherType switch
                {
                    VoucherType.Purchase => VoucherType.Purchase,
                    VoucherType.Procurement => VoucherType.Procurement,
                    _ => VoucherType.Sales
                };
                var invoiceViewModel = new SalesViewModel(_local, _print, _invoicePrintService, _session, entryType);
                await invoiceViewModel.LoadAsync();
                await invoiceViewModel.LoadInvoiceAsync(invoice);
                var window = new SalesWindow(invoiceViewModel);
                if (owner is not null)
                    window.Owner = owner;

                window.ShowDialog();
                StatusMessage = "운영 점검 항목의 전표 작성창을 열었습니다.";
                break;
            }
            case DataIntegrityDirectActionKind.OpenPaymentForInvoice when issue.EntityId.HasValue:
            {
                var invoice = await _local.GetInvoiceAsync(issue.EntityId.Value, _session);
                if (invoice is null)
                {
                    ShowDataIntegrityNavigationMessage(owner, "전표를 찾을 수 없어 수금/지급 창을 열 수 없습니다.");
                    StatusMessage = "운영 점검 항목의 전표를 찾지 못했습니다.";
                    break;
                }

                var customer = await _local.GetCustomerAsync(invoice.CustomerId, _session);
                var paymentViewModel = new PaymentViewModel(_local, _session);
                await paymentViewModel.LoadAsync(customer);
                await paymentViewModel.ConfigureForInvoiceAsync(invoice);
                var window = new PaymentWindow(paymentViewModel);
                if (owner is not null)
                    window.Owner = owner;

                window.ShowDialog();
                StatusMessage = "운영 점검 항목의 수금/지급 창을 열었습니다.";
                break;
            }
            case DataIntegrityDirectActionKind.OpenSyncDiagnostics:
            {
                var diagnosticsViewModel = new SyncDiagnosticsViewModel(_diagnostics, _sync, _api, _local, _rental, _session);
                await diagnosticsViewModel.LoadAsync();
                var window = new SyncDiagnosticsWindow(diagnosticsViewModel);
                if (owner is not null)
                    window.Owner = owner;

                window.ShowDialog();
                StatusMessage = "동기화 진단 창을 열었습니다.";
                break;
            }
            case DataIntegrityDirectActionKind.OpenEnvironmentSettings:
                StatusMessage = "현재 환경설정 화면에서 창고/동기화 설정을 확인하세요.";
                break;
            default:
                ShowDataIntegrityNavigationMessage(owner, "이 항목은 원본 화면 바로가기를 지원하지 않습니다. 상세 내용을 기준으로 수동 확인하세요.");
                StatusMessage = "운영 점검 상세에서 수동 확인이 필요한 항목입니다.";
                break;
        }
    }

    private static void ShowDataIntegrityNavigationMessage(Window? owner, string message)
    {
        if (owner is not null)
        {
            MessageBox.Show(owner, message, "운영 점검", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(message, "운영 점검", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static Window? ResolveActiveWindow()
        => Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive)
            ?? Application.Current?.MainWindow;

    [RelayCommand]
    private async Task RunFullResyncAsync()
    {
        if (IsBusy)
            return;

        if (_session.IsOfflineMode)
        {
            StatusMessage = "오프라인 모드에서는 중앙 서버 기준 전체 재동기화를 실행할 수 없습니다.";
            await RefreshSyncStateAsync();
            return;
        }

        var dirtyCount = await _local.CountDirtyAsync(_session);
        if (dirtyCount > 0)
        {
            var waitingMessage = await _local.GetPendingSyncWaitingMessageAsync(_session, "미동기화 변경이 남아 있어 전체 재동기화를 바로 실행할 수 없습니다.");
            StatusMessage = waitingMessage ?? $"미동기화 변경 {dirtyCount:N0}건이 남아 있어 전체 재동기화를 바로 실행할 수 없습니다.";
            MessageBox.Show(
                StatusMessage + Environment.NewLine + Environment.NewLine + "먼저 동기화를 완료한 뒤 다시 실행하세요.",
                "전체 재동기화 보류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            await RefreshSyncStateAsync();
            return;
        }

        if (MessageBox.Show(
                "현재 로컬 공유 캐시를 중앙 서버 기준으로 다시 내려받습니다." + Environment.NewLine +
                "실행 전에 현재 로컬 DB 백업을 만든 뒤 진행합니다." + Environment.NewLine + Environment.NewLine +
                "계속하시겠습니까?",
                "전체 재동기화",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            StatusMessage = "전체 재동기화를 취소했습니다.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "전체 재동기화 전 백업을 생성하는 중...";
            var backupPath = await _backup.BackupNowWithPathAsync();
            var ok = await _sync.RefreshSharedMirrorFromServerAsync();
            await RefreshSyncStateAsync();

            StatusMessage = ok
                ? string.IsNullOrWhiteSpace(backupPath)
                    ? "중앙 서버 기준 전체 재동기화를 완료했습니다."
                    : $"중앙 서버 기준 전체 재동기화를 완료했습니다. 백업: {System.IO.Path.GetFileName(backupPath)}"
                : "중앙 서버 기준 전체 재동기화에 실패했습니다. 동기화 진단을 확인하세요.";

            MessageBox.Show(
                StatusMessage,
                "전체 재동기화",
                MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunBackupAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var backupPath = await _backup.BackupNowWithPathAsync();
            var ok = !string.IsNullOrWhiteSpace(backupPath);
            await RefreshSyncStateAsync();
            await ReloadBackupSnapshotsAsync();
            StatusMessage = ok
                ? $"백업을 완료했습니다: {System.IO.Path.GetFileName(backupPath)}"
                : "백업 중 오류가 발생했습니다.";
            MessageBox.Show(
                ok ? $"백업이 완료되었습니다.{Environment.NewLine}{System.IO.Path.GetFileName(backupPath)}" : "백업 중 오류가 발생했습니다.",
                "백업",
                MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenSyncDiagnosticsWindowAsync(SyncScopeStatusRow? scope = null)
    {
        var diagnosticsViewModel = new SyncDiagnosticsViewModel(_diagnostics, _sync, _api, _local, _rental, _session);
        if (scope is not null)
        {
            diagnosticsViewModel.SearchText = BuildSyncDiagnosticScopeSearchText(scope);
            diagnosticsViewModel.SelectedStatus = "Open";
        }

        await diagnosticsViewModel.LoadAsync();

        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive)
            ?? Application.Current?.MainWindow;

        var window = new SyncDiagnosticsWindow(diagnosticsViewModel)
        {
            Owner = owner
        };
        window.ShowDialog();
        await RefreshSyncStateAsync();
        StatusMessage = scope is null
            ? "동기화 진단 창을 열었습니다."
            : $"{scope.ScopeDisplayName} 범위 기준으로 동기화 진단 창을 열었습니다.";
    }

    private static string BuildSyncDiagnosticScopeSearchText(SyncScopeStatusRow scope)
        => !string.IsNullOrWhiteSpace(scope.RequiredOfficeCode)
            ? scope.RequiredOfficeCode
            : scope.ScopeKey;
}
