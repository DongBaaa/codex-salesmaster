using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public ObservableCollection<TenantDefinitionDto> TenantDefinitions { get; } = new();
    public ObservableCollection<TenantOfficeDefinitionDto> TenantOfficeDefinitions { get; } = new();
    public ObservableCollection<DataSharingPolicyDto> SharingPolicies { get; } = new();
    public ObservableCollection<DisplayOption> SharingSourceTenantOptions { get; } = new();
    public ObservableCollection<DisplayOption> SharingSourceOfficeOptions { get; } = new();
    public ObservableCollection<DisplayOption> SharingTargetTenantOptions { get; } = new();
    public ObservableCollection<DisplayOption> SharingTargetOfficeOptions { get; } = new();

    public string TenantConfigurationHint => CanManageTenantConfiguration
        ? "관리자는 업체 권역, 지점 역할, 지점 간 데이터 연동 정책을 직접 수정할 수 있습니다."
        : _session.IsOfflineMode
            ? "오프라인 모드에서는 업체 / 데이터 권한 설정을 불러오거나 변경할 수 없습니다."
            : "일반 사용자는 업체 / 데이터 권한 설정을 조회만 할 수 있고 변경은 관리자만 가능합니다.";

    public bool CanDeleteSelectedSharingPolicy => CanManageTenantConfiguration && SelectedSharingPolicy is not null;

    public IReadOnlyList<DisplayOption> TenantStorageModeOptions { get; } =
    [
        new() { Value = TenantScopeCatalog.StorageSharedDatabase, DisplayName = "공용 업무 DB" },
        new() { Value = TenantScopeCatalog.StorageDedicatedDatabase, DisplayName = "별도 업무 DB" }
    ];

    [RelayCommand]
    private async Task ReloadTenantConfigurationAsync()
    {
        TenantDefinitions.Clear();
        TenantOfficeDefinitions.Clear();
        SharingPolicies.Clear();
        SharingSourceTenantOptions.Clear();
        SharingTargetTenantOptions.Clear();

        if (!CanManageTenantConfiguration || _session.IsOfflineMode)
        {
            NewSharingPolicy();
            return;
        }

        TenantConfigurationSnapshotDto? snapshot;
        try
        {
            snapshot = await _api.GetTenantConfigurationAsync();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden || ex.Message.Contains("403", StringComparison.Ordinal))
        {
            StatusMessage = "업체/데이터 권한은 관리자 또는 god 권한 계정만 조회할 수 있습니다.";
            NewSharingPolicy();
            return;
        }

        if (snapshot is null)
        {
            StatusMessage = "업체/데이터 권한 설정을 불러오지 못했습니다.";
            return;
        }

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
            string.Equals(current.TenantCode, EditingTenantCode, StringComparison.OrdinalIgnoreCase))
            ?? TenantDefinitions.FirstOrDefault();
        SelectedTenantOfficeDefinition = TenantOfficeDefinitions.FirstOrDefault(current =>
            string.Equals(current.OfficeCode, EditingOfficeCode, StringComparison.OrdinalIgnoreCase))
            ?? TenantOfficeDefinitions.FirstOrDefault();
        RefreshSharingOfficeOptions();
        SelectedSharingPolicy = SharingPolicies.FirstOrDefault();
        if (SelectedSharingPolicy is null)
            NewSharingPolicy();
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
            await _api.UpdateTenantDefinitionAsync(
                EditingTenantCode,
                new UpdateTenantDefinitionRequest
                {
                    DisplayName = EditingTenantDisplayName,
                    StorageMode = EditingTenantStorageMode,
                    Description = EditingTenantDescription,
                    IsActive = EditingTenantIsActive
                });
            await ReloadTenantConfigurationAsync();
            StatusMessage = "업체 권역 설정을 저장했습니다.";
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
            await _api.UpdateTenantOfficeDefinitionAsync(
                EditingOfficeCode,
                new UpdateTenantOfficeDefinitionRequest
                {
                    DisplayName = EditingOfficeDisplayName,
                    IsHeadOffice = EditingOfficeIsHeadOffice,
                    IsActive = EditingOfficeIsActive
                });
            await ReloadTenantConfigurationAsync();
            StatusMessage = "지점 구성을 저장했습니다.";
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
            if (EditingSharingPolicyId == Guid.Empty)
            {
                await _api.CreateSharingPolicyAsync(request);
                StatusMessage = "연동 정책을 추가했습니다.";
            }
            else
            {
                await _api.UpdateSharingPolicyAsync(EditingSharingPolicyId, request);
                StatusMessage = "연동 정책을 저장했습니다.";
            }

            await ReloadTenantConfigurationAsync();
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
            await _api.DeleteSharingPolicyAsync(SelectedSharingPolicy.Id);
            StatusMessage = "연동 정책을 삭제했습니다.";
            await ReloadTenantConfigurationAsync();
            NewSharingPolicy();
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
