using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class EnvironmentSettingsViewModel
{
    [ObservableProperty] private TenantDefinitionDto? _selectedTenantDefinition;
    [ObservableProperty] private string _editingTenantCode = TenantScopeCatalog.UsenetGroup;
    [ObservableProperty] private string _editingTenantDisplayName = string.Empty;
    [ObservableProperty] private string _editingTenantStorageMode = TenantScopeCatalog.StorageSharedDatabase;
    [ObservableProperty] private string _editingTenantDescription = string.Empty;
    [ObservableProperty] private bool _editingTenantIsActive = true;

    [ObservableProperty] private TenantOfficeDefinitionDto? _selectedTenantOfficeDefinition;
    [ObservableProperty] private string _editingOfficeCode = OfficeCodeCatalog.Usenet;
    [ObservableProperty] private string _editingOfficeTenantCode = TenantScopeCatalog.UsenetGroup;
    [ObservableProperty] private string _editingOfficeDisplayName = string.Empty;
    [ObservableProperty] private bool _editingOfficeIsHeadOffice = true;
    [ObservableProperty] private bool _editingOfficeIsActive = true;

    [ObservableProperty] private DataSharingPolicyDto? _selectedSharingPolicy;
    [ObservableProperty] private Guid _editingSharingPolicyId;
    [ObservableProperty] private string _sharingSourceTenantCode = TenantScopeCatalog.UsenetGroup;
    [ObservableProperty] private string _sharingSourceOfficeCode = OfficeCodeCatalog.Yeonsu;
    [ObservableProperty] private string _sharingTargetTenantCode = TenantScopeCatalog.UsenetGroup;
    [ObservableProperty] private string _sharingTargetOfficeCode = OfficeCodeCatalog.Usenet;
    [ObservableProperty] private bool _sharingShareCustomers = true;
    [ObservableProperty] private bool _sharingShareItems = true;
    [ObservableProperty] private bool _sharingShareInvoices = true;
    [ObservableProperty] private bool _sharingSharePayments = true;
    [ObservableProperty] private bool _sharingShareContracts = true;
    [ObservableProperty] private bool _sharingShareReports = true;
    [ObservableProperty] private bool _sharingShareRentals = true;
    [ObservableProperty] private bool _sharingShareDeliveries = true;
    [ObservableProperty] private bool _sharingAllowTargetWrite;
    [ObservableProperty] private bool _sharingIsActive = true;
    [ObservableProperty] private string _sharingNote = string.Empty;
    [ObservableProperty] private string _currentScopeMatrixSummary = "현재 로그인 계정 범위를 아직 불러오지 않았습니다.";
    [ObservableProperty] private string _currentScopeMatrixGeneratedAtText = "미조회";

    public ObservableCollection<TenantDefinitionDto> TenantDefinitions { get; } = new();
    public ObservableCollection<TenantOfficeDefinitionDto> TenantOfficeDefinitions { get; } = new();
    public ObservableCollection<DataSharingPolicyDto> SharingPolicies { get; } = new();
    public ObservableCollection<DisplayOption> SharingSourceTenantOptions { get; } = new();
    public ObservableCollection<DisplayOption> SharingSourceOfficeOptions { get; } = new();
    public ObservableCollection<DisplayOption> SharingTargetTenantOptions { get; } = new();
    public ObservableCollection<DisplayOption> SharingTargetOfficeOptions { get; } = new();
    public ObservableCollection<ScopeMatrixAreaDto> CurrentScopeMatrixAreas { get; } = new();

    public string TenantConfigurationHint => CanManageTenantConfiguration
        ? "관리자는 업체 권역, 지점 역할, 지점 간 데이터 연동 정책을 직접 수정할 수 있습니다."
        : _session.IsOfflineMode
            ? "오프라인 모드에서는 업체 / 데이터 권한 설정을 불러오거나 변경할 수 없습니다."
            : "전역 관리자 범위가 아닌 계정은 업체 / 데이터 권한 설정을 조회하거나 변경할 수 없습니다.";
    public string CurrentScopeMatrixHint => _session.IsOfflineMode
        ? "오프라인 모드에서는 현재 계정의 서버 기준 범위를 조회할 수 없습니다."
        : "현재 로그인 계정이 실제로 읽고 쓸 수 있는 지점 범위를 서버 기준으로 보여줍니다.";

    public bool CanDeleteSelectedSharingPolicy => CanManageTenantConfiguration && SelectedSharingPolicy is not null;

    public IReadOnlyList<DisplayOption> TenantStorageModeOptions { get; } =
    [
        new() { Value = TenantScopeCatalog.StorageSharedDatabase, DisplayName = "공용 업무 DB" },
        new() { Value = TenantScopeCatalog.StorageDedicatedDatabase, DisplayName = "별도 업무 DB" }
    ];

    [RelayCommand]
    private Task ReloadTenantConfigurationAsync()
        => ReloadTenantConfigurationCoreAsync(includeInactive: false);

    private async Task ReloadTenantConfigurationCoreAsync(
        bool includeInactive,
        string? preferredTenantCode = null,
        string? preferredOfficeCode = null,
        Guid? preferredSharingPolicyId = null,
        bool reloadScopeMatrix = true)
    {
        if (_session.IsOfflineMode)
            throw new InvalidOperationException("오프라인 모드에서는 업체/데이터 권한 서버 스냅샷을 새로고침할 수 없습니다.");
        if (!CanManageTenantConfiguration)
            throw new UnauthorizedAccessException("업체/데이터 권한 설정을 조회할 시스템 관리 권한이 없습니다.");

        await FetchAndApplyTenantConfigurationSnapshotAsync(
            () => _api.GetTenantConfigurationAsync(includeInactive),
            snapshot => ApplyTenantConfigurationSnapshot(
                snapshot,
                preferredTenantCode,
                preferredOfficeCode,
                preferredSharingPolicyId));

        if (reloadScopeMatrix)
            await ReloadCurrentScopeMatrixAsync();
    }

    internal static async Task FetchAndApplyTenantConfigurationSnapshotAsync(
        Func<Task<TenantConfigurationSnapshotDto?>> fetchAsync,
        Action<TenantConfigurationSnapshotDto> applySnapshot)
    {
        var snapshot = await fetchAsync();
        if (snapshot is null ||
            snapshot.Tenants is null ||
            snapshot.Offices is null ||
            snapshot.SharingPolicies is null)
        {
            throw new InvalidDataException("업체/데이터 권한 서버 응답이 비어 있거나 완전하지 않습니다.");
        }

        applySnapshot(snapshot);
    }

    private void ApplyTenantConfigurationSnapshot(
        TenantConfigurationSnapshotDto snapshot,
        string? preferredTenantCode,
        string? preferredOfficeCode,
        Guid? preferredSharingPolicyId)
    {
        TenantDefinitions.Clear();
        TenantOfficeDefinitions.Clear();
        SharingPolicies.Clear();
        SharingSourceTenantOptions.Clear();
        SharingTargetTenantOptions.Clear();

        foreach (var tenant in snapshot.Tenants.OrderBy(current => current.TenantCode, StringComparer.OrdinalIgnoreCase))
        {
            TenantDefinitions.Add(tenant);
            var displayName = string.IsNullOrWhiteSpace(tenant.DisplayName)
                ? TenantScopeCatalog.GetTenantDisplayName(tenant.TenantCode)
                : tenant.DisplayName.Trim();
            SharingSourceTenantOptions.Add(new DisplayOption
            {
                Value = tenant.TenantCode,
                DisplayName = $"{displayName} ({tenant.TenantCode})"
            });
            SharingTargetTenantOptions.Add(new DisplayOption
            {
                Value = tenant.TenantCode,
                DisplayName = $"{displayName} ({tenant.TenantCode})"
            });
        }

        foreach (var office in snapshot.Offices
                     .OrderBy(current => current.TenantCode, StringComparer.OrdinalIgnoreCase)
                     .ThenByDescending(current => current.IsHeadOffice)
                     .ThenBy(current => current.OfficeCode, StringComparer.OrdinalIgnoreCase))
        {
            TenantOfficeDefinitions.Add(office);
        }

        foreach (var policy in snapshot.SharingPolicies
                     .OrderBy(current => current.TargetTenantCode, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(current => current.TargetOfficeCode, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(current => current.SourceTenantCode, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(current => current.SourceOfficeCode, StringComparer.OrdinalIgnoreCase))
        {
            SharingPolicies.Add(policy);
        }

        RefreshUserTenantOptions();
        RefreshUserOfficeOptions();
        RefreshBusinessDatabaseOptions();
        SelectedTenantDefinition = TenantDefinitions.FirstOrDefault(current =>
            string.Equals(
                current.TenantCode,
                preferredTenantCode ?? EditingTenantCode,
                StringComparison.OrdinalIgnoreCase))
            ?? TenantDefinitions.FirstOrDefault();
        SelectedTenantOfficeDefinition = TenantOfficeDefinitions.FirstOrDefault(current =>
            string.Equals(
                current.OfficeCode,
                preferredOfficeCode ?? EditingOfficeCode,
                StringComparison.OrdinalIgnoreCase))
            ?? TenantOfficeDefinitions.FirstOrDefault();
        RefreshSharingOfficeOptions();
        SelectedSharingPolicy = preferredSharingPolicyId is { } policyId && policyId != Guid.Empty
            ? SharingPolicies.FirstOrDefault(current => current.Id == policyId)
              ?? SharingPolicies.FirstOrDefault()
            : SharingPolicies.FirstOrDefault();
        if (SelectedSharingPolicy is null)
            NewSharingPolicy();
    }

    internal sealed record TenantMutationExecutionResult(
        bool IsAmbiguous,
        bool CurrentStateMatchesRequest,
        string StatusMessage);

    internal sealed record TenantConfirmedRefreshResult(
        bool RefreshSucceeded,
        string StatusMessage);

    internal static async Task<TenantConfirmedRefreshResult> ReloadAfterConfirmedTenantMutationAsync(
        Func<Task> reloadAsync,
        string successMessage)
    {
        try
        {
            await reloadAsync();
            return new TenantConfirmedRefreshResult(true, successMessage);
        }
        catch (Exception ex)
        {
            return new TenantConfirmedRefreshResult(
                false,
                $"서버 저장은 확정되었지만 화면 새로고침 실패로 최신 상태를 표시하지 못했습니다. 상태를 검토하기 전까지 같은 작업을 반복하지 마세요. 새로고침 오류: {ex.Message}");
        }
    }

    internal static async Task<TenantMutationExecutionResult> ExecuteTenantMutationWithRecoveryAsync(
        Func<Task> sendMutationAsync,
        Func<Task> authoritativeReloadAsync,
        Func<bool> currentStateMatchesRequest)
    {
        try
        {
            await sendMutationAsync();
            return new TenantMutationExecutionResult(false, false, string.Empty);
        }
        catch (AmbiguousMutationOutcomeException ambiguousException)
        {
            try
            {
                await authoritativeReloadAsync();
                var matches = currentStateMatchesRequest();
                var confirmation = matches
                    ? "현재 서버 상태가 요청 내용과 일치하지만, 이 요청으로 반영된 것인지는 식별할 수 없습니다."
                    : "현재 서버 상태가 요청 내용과 일치하는지 확인되지 않았습니다.";
                return new TenantMutationExecutionResult(
                    true,
                    matches,
                    $"서버 결과가 불확실하여 비활성/삭제 항목을 포함한 전체 상태를 다시 불러왔습니다. {confirmation} 검토하기 전까지 같은 작업을 반복하지 마세요.");
            }
            catch (Exception reloadException)
            {
                return new TenantMutationExecutionResult(
                    true,
                    false,
                    $"서버 결과가 불확실하고 최신 상태를 다시 불러오지도 못했습니다. 검토하기 전까지 같은 작업을 반복하지 마세요. 전송 오류: {ambiguousException.Message} / 재조회 오류: {reloadException.Message}");
            }
        }
    }

    private async Task ReloadCurrentScopeMatrixAsync()
    {
        CurrentScopeMatrixAreas.Clear();

        if (_session.IsOfflineMode)
        {
            CurrentScopeMatrixGeneratedAtText = "오프라인";
            CurrentScopeMatrixSummary = "오프라인 모드에서는 현재 계정 범위를 조회할 수 없습니다.";
            return;
        }

        try
        {
            var snapshot = await _api.GetScopeMatrixAsync();
            if (snapshot is null)
            {
                CurrentScopeMatrixGeneratedAtText = "미조회";
                CurrentScopeMatrixSummary = "현재 계정 범위를 불러오지 못했습니다.";
                return;
            }

            foreach (var area in snapshot.Areas
                         .OrderBy(area => area.AreaDisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                CurrentScopeMatrixAreas.Add(area);
            }

            CurrentScopeMatrixGeneratedAtText = snapshot.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            CurrentScopeMatrixSummary =
                $"{snapshot.Username} / {snapshot.TenantCode} / {snapshot.OfficeCode} / {TenantScopeCatalog.GetScopeDisplayName(snapshot.ScopeType)}";
        }
        catch (Exception ex)
        {
            CurrentScopeMatrixGeneratedAtText = "조회 실패";
            CurrentScopeMatrixSummary = $"현재 계정 범위 조회 실패: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveTenantDefinitionAsync()
    {
        if (!CanManageTenantConfiguration)
        {
            StatusMessage = "업체 권역 설정을 변경할 권한이 없습니다.";
            return;
        }

        if (string.IsNullOrWhiteSpace(EditingTenantCode))
        {
            StatusMessage = "업체 권역을 선택하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            var tenantCode = EditingTenantCode;
            var request = new UpdateTenantDefinitionRequest
            {
                ExpectedRevision = SelectedTenantDefinition?.Revision ?? 0,
                DisplayName = EditingTenantDisplayName,
                StorageMode = TenantScopeCatalog.NormalizeStorageModeOrDefault(
                             SelectedTenantDefinition?.StorageMode,
                             EditingTenantStorageMode),
                Description = EditingTenantDescription,
                IsActive = EditingTenantIsActive
            };
            var outcome = await ExecuteTenantMutationWithRecoveryAsync(
                async () => { await _api.UpdateTenantDefinitionAsync(tenantCode, request); },
                () => ReloadTenantConfigurationCoreAsync(
                    includeInactive: true,
                    preferredTenantCode: tenantCode,
                    preferredOfficeCode: SelectedTenantOfficeDefinition?.OfficeCode,
                    preferredSharingPolicyId: SelectedSharingPolicy?.Id,
                    reloadScopeMatrix: false),
                () => TenantDefinitions.Any(current => TenantMatchesRequest(current, tenantCode, request)));
            if (outcome.IsAmbiguous)
            {
                StatusMessage = outcome.StatusMessage;
                return;
            }

            var refresh = await ReloadAfterConfirmedTenantMutationAsync(
                ReloadTenantConfigurationAsync,
                "업체 권역 설정을 저장했습니다.");
            StatusMessage = refresh.StatusMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = $"업체 권역 저장 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveTenantOfficeDefinitionAsync()
    {
        if (!CanManageTenantConfiguration)
        {
            StatusMessage = "지점 구성을 변경할 권한이 없습니다.";
            return;
        }

        if (string.IsNullOrWhiteSpace(EditingOfficeCode))
        {
            StatusMessage = "지점을 선택하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            var officeCode = EditingOfficeCode;
            var request = new UpdateTenantOfficeDefinitionRequest
            {
                ExpectedRevision = SelectedTenantOfficeDefinition?.Revision ?? 0,
                DisplayName = EditingOfficeDisplayName,
                IsHeadOffice = EditingOfficeIsHeadOffice,
                IsActive = EditingOfficeIsActive
            };
            var outcome = await ExecuteTenantMutationWithRecoveryAsync(
                async () => { await _api.UpdateTenantOfficeDefinitionAsync(officeCode, request); },
                () => ReloadTenantConfigurationCoreAsync(
                    includeInactive: true,
                    preferredTenantCode: SelectedTenantDefinition?.TenantCode,
                    preferredOfficeCode: officeCode,
                    preferredSharingPolicyId: SelectedSharingPolicy?.Id,
                    reloadScopeMatrix: false),
                () => TenantOfficeDefinitions.Any(current => OfficeMatchesRequest(current, officeCode, request)));
            if (outcome.IsAmbiguous)
            {
                StatusMessage = outcome.StatusMessage;
                return;
            }

            var refresh = await ReloadAfterConfirmedTenantMutationAsync(
                ReloadTenantConfigurationAsync,
                "지점 구성을 저장했습니다.");
            StatusMessage = refresh.StatusMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = $"지점 구성 저장 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void NewSharingPolicy()
    {
        EditingSharingPolicyId = Guid.Empty;
        SharingSourceTenantCode = TenantDefinitions.FirstOrDefault(current =>
                string.Equals(current.TenantCode, TenantScopeCatalog.UsenetGroup, StringComparison.OrdinalIgnoreCase))?.TenantCode
            ?? TenantScopeCatalog.UsenetGroup;
        SharingTargetTenantCode = SharingSourceTenantCode;
        RefreshSharingOfficeOptions();
        SharingSourceOfficeCode = SharingSourceOfficeOptions.FirstOrDefault(current =>
                string.Equals(current.Value, OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase))?.Value
            ?? SharingSourceOfficeOptions.FirstOrDefault()?.Value
            ?? OfficeCodeCatalog.Yeonsu;
        SharingTargetOfficeCode = SharingTargetOfficeOptions.FirstOrDefault(current =>
                string.Equals(current.Value, OfficeCodeCatalog.Usenet, StringComparison.OrdinalIgnoreCase))?.Value
            ?? SharingTargetOfficeOptions.FirstOrDefault()?.Value
            ?? OfficeCodeCatalog.Usenet;
        SharingShareCustomers = true;
        SharingShareItems = true;
        SharingShareInvoices = true;
        SharingSharePayments = true;
        SharingShareContracts = true;
        SharingShareReports = true;
        SharingShareRentals = true;
        SharingShareDeliveries = true;
        SharingAllowTargetWrite = false;
        SharingIsActive = true;
        SharingNote = string.Empty;
        SelectedSharingPolicy = null;
    }

    [RelayCommand]
    private async Task SaveSharingPolicyAsync()
    {
        if (!CanManageTenantConfiguration)
        {
            StatusMessage = "연동 정책을 변경할 권한이 없습니다.";
            return;
        }

        var request = new UpsertDataSharingPolicyRequest
        {
            ExpectedRevision = SelectedSharingPolicy?.Revision ?? 0,
            SourceTenantCode = SharingSourceTenantCode,
            SourceOfficeCode = SharingSourceOfficeCode,
            TargetTenantCode = SharingTargetTenantCode,
            TargetOfficeCode = SharingTargetOfficeCode,
            ShareCustomers = SharingShareCustomers,
            ShareItems = SharingShareItems,
            ShareInvoices = SharingShareInvoices,
            SharePayments = SharingSharePayments,
            ShareContracts = SharingShareContracts,
            ShareReports = SharingShareReports,
            ShareRentals = SharingShareRentals,
            ShareDeliveries = SharingShareDeliveries,
            AllowTargetWrite = SharingAllowTargetWrite,
            IsActive = SharingIsActive,
            Note = SharingNote
        };

        try
        {
            IsBusy = true;
            var isCreate = EditingSharingPolicyId == Guid.Empty;
            TenantMutationExecutionResult outcome;
            if (isCreate)
            {
                outcome = await ExecuteTenantMutationWithRecoveryAsync(
                    async () => { await _api.CreateSharingPolicyAsync(request); },
                    () => ReloadTenantConfigurationCoreAsync(
                        includeInactive: true,
                        preferredTenantCode: SelectedTenantDefinition?.TenantCode,
                        preferredOfficeCode: SelectedTenantOfficeDefinition?.OfficeCode,
                        reloadScopeMatrix: false),
                    () => SharingPolicies.Any(current => SharingPolicyMatchesRequest(current, request)));
            }
            else
            {
                var policyId = EditingSharingPolicyId;
                outcome = await ExecuteTenantMutationWithRecoveryAsync(
                    async () => { await _api.UpdateSharingPolicyAsync(policyId, request); },
                    () => ReloadTenantConfigurationCoreAsync(
                        includeInactive: true,
                        preferredTenantCode: SelectedTenantDefinition?.TenantCode,
                        preferredOfficeCode: SelectedTenantOfficeDefinition?.OfficeCode,
                        preferredSharingPolicyId: policyId,
                        reloadScopeMatrix: false),
                    () => SharingPolicies.Any(current =>
                        current.Id == policyId && SharingPolicyMatchesRequest(current, request)));
            }

            if (outcome.IsAmbiguous)
            {
                StatusMessage = outcome.StatusMessage;
                return;
            }

            var successMessage = isCreate
                ? "연동 정책을 추가했습니다."
                : "연동 정책을 저장했습니다.";
            var refresh = await ReloadAfterConfirmedTenantMutationAsync(
                ReloadTenantConfigurationAsync,
                successMessage);
            StatusMessage = refresh.StatusMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = $"연동 정책 저장 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSharingPolicyAsync()
    {
        if (!CanManageTenantConfiguration)
        {
            StatusMessage = "연동 정책을 삭제할 권한이 없습니다.";
            return;
        }

        if (SelectedSharingPolicy is null)
        {
            StatusMessage = "삭제할 연동 정책을 선택하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            var policyId = SelectedSharingPolicy.Id;
            var expectedRevision = SelectedSharingPolicy.Revision;
            var outcome = await ExecuteTenantMutationWithRecoveryAsync(
                async () => { await _api.DeleteSharingPolicyAsync(policyId, expectedRevision); },
                () => ReloadTenantConfigurationCoreAsync(
                    includeInactive: true,
                    preferredTenantCode: SelectedTenantDefinition?.TenantCode,
                    preferredOfficeCode: SelectedTenantOfficeDefinition?.OfficeCode,
                    preferredSharingPolicyId: policyId,
                    reloadScopeMatrix: false),
                () => SharingPolicies.Any(current =>
                    current.Id == policyId &&
                    current.IsDeleted &&
                    !current.IsActive &&
                    current.Revision > expectedRevision));
            if (outcome.IsAmbiguous)
            {
                StatusMessage = outcome.StatusMessage;
                return;
            }

            var refresh = await ReloadAfterConfirmedTenantMutationAsync(
                ReloadTenantConfigurationAsync,
                "연동 정책을 삭제했습니다.");
            if (refresh.RefreshSucceeded)
                NewSharingPolicy();
            StatusMessage = refresh.StatusMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = $"연동 정책 삭제 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool TenantMatchesRequest(
        TenantDefinitionDto current,
        string tenantCode,
        UpdateTenantDefinitionRequest request)
    {
        var canonicalTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(tenantCode);
        var expectedDisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? TenantScopeCatalog.GetTenantDisplayName(canonicalTenantCode)
            : request.DisplayName.Trim();
        return string.Equals(current.TenantCode, canonicalTenantCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(current.DisplayName, expectedDisplayName, StringComparison.Ordinal) &&
               string.Equals(
                   TenantScopeCatalog.NormalizeStorageModeOrDefault(current.StorageMode),
                   TenantScopeCatalog.NormalizeStorageModeOrDefault(request.StorageMode),
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(current.Description, request.Description?.Trim() ?? string.Empty, StringComparison.Ordinal) &&
               current.IsActive == request.IsActive &&
               current.IsDeleted != request.IsActive &&
               current.Revision > Math.Max(0, request.ExpectedRevision);
    }

    private static bool OfficeMatchesRequest(
        TenantOfficeDefinitionDto current,
        string officeCode,
        UpdateTenantOfficeDefinitionRequest request)
    {
        var canonicalOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode);
        var expectedDisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? OfficeCodeCatalog.GetOfficeDisplayName(canonicalOfficeCode)
            : request.DisplayName.Trim();
        return string.Equals(current.OfficeCode, canonicalOfficeCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   current.TenantCode,
                   TenantScopeCatalog.GetTenantCodeForOffice(canonicalOfficeCode),
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(current.DisplayName, expectedDisplayName, StringComparison.Ordinal) &&
               current.IsHeadOffice == request.IsHeadOffice &&
               current.IsActive == request.IsActive &&
               current.IsDeleted != request.IsActive &&
               current.Revision > Math.Max(0, request.ExpectedRevision);
    }

    private static bool SharingPolicyMatchesRequest(
        DataSharingPolicyDto current,
        UpsertDataSharingPolicyRequest request)
    {
        var sourceOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(request.SourceOfficeCode);
        var targetOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(request.TargetOfficeCode);
        return string.Equals(
                   current.SourceTenantCode,
                   TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(request.SourceTenantCode, sourceOfficeCode),
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(current.SourceOfficeCode, sourceOfficeCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   current.TargetTenantCode,
                   TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(request.TargetTenantCode, targetOfficeCode),
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(current.TargetOfficeCode, targetOfficeCode, StringComparison.OrdinalIgnoreCase) &&
               current.ShareCustomers == request.ShareCustomers &&
               current.ShareItems == request.ShareItems &&
               current.ShareInvoices == request.ShareInvoices &&
               current.SharePayments == request.SharePayments &&
               current.ShareContracts == request.ShareContracts &&
               current.ShareReports == request.ShareReports &&
               current.ShareRentals == request.ShareRentals &&
               current.ShareDeliveries == request.ShareDeliveries &&
               current.AllowTargetWrite == request.AllowTargetWrite &&
               current.IsActive == request.IsActive &&
               current.IsDeleted != request.IsActive &&
               string.Equals(current.Note, request.Note?.Trim() ?? string.Empty, StringComparison.Ordinal) &&
               current.Revision > Math.Max(0, request.ExpectedRevision);
    }

    partial void OnSelectedTenantDefinitionChanged(TenantDefinitionDto? value)
    {
        if (value is null)
            return;

        EditingTenantCode = value.TenantCode;
        EditingTenantDisplayName = value.DisplayName;
        EditingTenantStorageMode = TenantScopeCatalog.NormalizeStorageModeOrDefault(value.StorageMode);
        EditingTenantDescription = value.Description;
        EditingTenantIsActive = value.IsActive;
    }

    partial void OnSelectedTenantOfficeDefinitionChanged(TenantOfficeDefinitionDto? value)
    {
        if (value is null)
            return;

        EditingOfficeCode = value.OfficeCode;
        EditingOfficeTenantCode = value.TenantCode;
        EditingOfficeDisplayName = value.DisplayName;
        EditingOfficeIsHeadOffice = value.IsHeadOffice;
        EditingOfficeIsActive = value.IsActive;
    }

    partial void OnSelectedSharingPolicyChanged(DataSharingPolicyDto? value)
    {
        if (value is null)
            return;

        EditingSharingPolicyId = value.Id;
        SharingSourceTenantCode = value.SourceTenantCode;
        SharingTargetTenantCode = value.TargetTenantCode;
        RefreshSharingOfficeOptions();
        SharingSourceOfficeCode = value.SourceOfficeCode;
        SharingTargetOfficeCode = value.TargetOfficeCode;
        SharingShareCustomers = value.ShareCustomers;
        SharingShareItems = value.ShareItems;
        SharingShareInvoices = value.ShareInvoices;
        SharingSharePayments = value.SharePayments;
        SharingShareContracts = value.ShareContracts;
        SharingShareReports = value.ShareReports;
        SharingShareRentals = value.ShareRentals;
        SharingShareDeliveries = value.ShareDeliveries;
        SharingAllowTargetWrite = value.AllowTargetWrite;
        SharingIsActive = value.IsActive;
        SharingNote = value.Note;
    }

    partial void OnEditingTenantCodeChanged(string value)
    {
        if (SelectedTenantDefinition is null)
            return;

        if (!string.Equals(SelectedTenantDefinition.TenantCode, value, StringComparison.OrdinalIgnoreCase))
        {
            SelectedTenantDefinition = TenantDefinitions.FirstOrDefault(current =>
                string.Equals(current.TenantCode, value, StringComparison.OrdinalIgnoreCase));
        }
    }

    partial void OnEditingOfficeCodeChanged(string value)
    {
        if (SelectedTenantOfficeDefinition is null)
            return;

        if (!string.Equals(SelectedTenantOfficeDefinition.OfficeCode, value, StringComparison.OrdinalIgnoreCase))
        {
            SelectedTenantOfficeDefinition = TenantOfficeDefinitions.FirstOrDefault(current =>
                string.Equals(current.OfficeCode, value, StringComparison.OrdinalIgnoreCase));
        }
    }

    partial void OnSharingSourceTenantCodeChanged(string value)
    {
        RefreshSharingOfficeOptions();
        if (SharingSourceOfficeOptions.All(current => !string.Equals(current.Value, SharingSourceOfficeCode, StringComparison.OrdinalIgnoreCase)))
            SharingSourceOfficeCode = SharingSourceOfficeOptions.FirstOrDefault()?.Value ?? OfficeCodeCatalog.Yeonsu;
    }

    partial void OnSharingTargetTenantCodeChanged(string value)
    {
        RefreshSharingOfficeOptions();
        if (SharingTargetOfficeOptions.All(current => !string.Equals(current.Value, SharingTargetOfficeCode, StringComparison.OrdinalIgnoreCase)))
            SharingTargetOfficeCode = SharingTargetOfficeOptions.FirstOrDefault()?.Value ?? OfficeCodeCatalog.Usenet;
    }

    private void RefreshSharingOfficeOptions()
    {
        SharingSourceOfficeOptions.Clear();
        foreach (var office in TenantOfficeDefinitions
                     .Where(current => string.Equals(current.TenantCode, SharingSourceTenantCode, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(current => current.IsHeadOffice)
                     .ThenBy(current => current.OfficeCode, StringComparer.OrdinalIgnoreCase))
        {
            SharingSourceOfficeOptions.Add(new DisplayOption
            {
                Value = office.OfficeCode,
                DisplayName = $"{office.DisplayName} ({office.OfficeCode})"
            });
        }

        SharingTargetOfficeOptions.Clear();
        foreach (var office in TenantOfficeDefinitions
                     .Where(current => string.Equals(current.TenantCode, SharingTargetTenantCode, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(current => current.IsHeadOffice)
                     .ThenBy(current => current.OfficeCode, StringComparer.OrdinalIgnoreCase))
        {
            SharingTargetOfficeOptions.Add(new DisplayOption
            {
                Value = office.OfficeCode,
                DisplayName = $"{office.DisplayName} ({office.OfficeCode})"
            });
        }
    }
}
