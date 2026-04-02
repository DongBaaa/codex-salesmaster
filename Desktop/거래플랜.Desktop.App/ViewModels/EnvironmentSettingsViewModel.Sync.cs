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
        SyncSummaryText = summary.OpenIssueCount == 0
            ? "현재 미해결 동기화 오류가 없습니다."
            : $"현재 미해결 동기화 오류 {summary.OpenIssueCount:N0}건 / 자동 복구 가능 {summary.RecoverableIssueCount:N0}건";
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

            var isShared = string.Equals(scopeKey, "SHARED", StringComparison.OrdinalIgnoreCase);
            var officeCode = isShared
                ? string.Empty
                : scopeKey.StartsWith("OFFICE:", StringComparison.OrdinalIgnoreCase)
                    ? scopeKey[7..]
                    : string.Empty;
            credentialMap.TryGetValue(officeCode, out var storedCredential);
            var isCurrentScope = !isShared &&
                                 !string.IsNullOrWhiteSpace(currentOfficeCode) &&
                                 string.Equals(currentOfficeCode, officeCode, StringComparison.OrdinalIgnoreCase);

            SyncScopeStatuses.Add(new SyncScopeStatusRow
            {
                ScopeKey = scopeKey,
                ScopeDisplayName = isShared ? "공용 마스터" : OfficeCodeCatalog.GetOfficeDisplayName(officeCode),
                ScopeTypeText = isShared ? "공용" : "지점",
                PendingCount = pendingCount,
                PendingSummary = pendingSummaryText,
                CredentialStatusText = ResolveSyncScopeCredentialStatus(isShared, isCurrentScope, pendingCount, storedCredential),
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

        if (storedCredential is not null)
            return pendingCount > 0 ? "저장된 계정으로 추가 처리" : "저장된 계정 준비";

        return pendingCount > 0 ? "지점 로그인 필요" : "저장 계정 없음";
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
            var dirtyCount = await _local.CountDirtyAsync();
            if (syncOk && dirtyCount == 0)
                await _sync.RefreshSharedMirrorFromServerAsync();

            await RefreshSyncStateAsync();
            StatusMessage = dirtyCount > 0
                ? await _local.GetPendingSyncWaitingMessageAsync("동기화 작업은 완료됐지만")
                    ?? $"동기화 작업은 완료됐지만 서버 반영 대기 데이터 {dirtyCount:N0}건이 남아 있습니다. 동기화 진단을 확인하세요."
                : syncOk
                    ? "동기화를 완료했습니다."
                    : "동기화가 완료되었지만 일부 오류가 남아 있습니다. 동기화 진단을 확인하세요.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenSyncDiagnosticsAsync()
    {
        if (IsBusy)
            return;

        var diagnosticsViewModel = new SyncDiagnosticsViewModel(_diagnostics, _sync, _local, _rental, _session);
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
        StatusMessage = "동기화 진단 창을 열었습니다.";
    }

    [RelayCommand]
    private async Task RunBackupAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var ok = await _backup.BackupNowAsync();
            await RefreshSyncStateAsync();
            StatusMessage = ok ? "백업을 완료했습니다." : "백업 중 오류가 발생했습니다.";
            MessageBox.Show(
                ok ? "백업이 완료되었습니다." : "백업 중 오류가 발생했습니다.",
                "백업",
                MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
