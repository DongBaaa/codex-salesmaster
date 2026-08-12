using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class EnvironmentSettingsViewModel
{
    private readonly Func<Task>? _applyBusinessDatabaseChangeAsync;
    private readonly Func<Func<Task>, Task>? _runBusinessDatabaseTransitionAsync;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadSelectedBusinessDatabase))]
    private BusinessDatabaseOption? _selectedBusinessDatabaseOption;

    [ObservableProperty] private string _currentBusinessDatabaseLabel = string.Empty;

    public ObservableCollection<BusinessDatabaseOption> BusinessDatabaseOptions { get; } = new();
    public bool CanManageBusinessDatabaseSelection => _session.HasSystemConfigurationScope && !_session.IsOfflineMode;
    public bool CanLoadSelectedBusinessDatabase =>
        CanManageBusinessDatabaseSelection
        && SelectedBusinessDatabaseOption is not null
        && !string.Equals(SelectedBusinessDatabaseOption.DatabaseName, _session.SelectedBusinessDatabaseName, StringComparison.OrdinalIgnoreCase);
    public bool BusinessDatabaseChanged { get; private set; }

    private void InitializeBusinessDatabaseSelection()
    {
        CurrentBusinessDatabaseLabel = _session.SelectedBusinessDatabaseLabel;
    }

    private void RefreshBusinessDatabaseOptions()
    {
        BusinessDatabaseOptions.Clear();

        foreach (var tenant in TenantDefinitions.OrderBy(current => current.TenantCode, StringComparer.OrdinalIgnoreCase))
        {
            var databaseName = TenantScopeCatalog.GetDatabaseName(tenant.TenantCode);
            var companyName = TenantScopeCatalog.GetBusinessDatabaseDisplayName(databaseName);

            BusinessDatabaseOptions.Add(new BusinessDatabaseOption
            {
                TenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(tenant.TenantCode),
                DatabaseName = databaseName,
                CompanyName = companyName,
                DisplayName = TenantScopeCatalog.FormatBusinessDatabaseLabel(companyName, databaseName)
            });
        }

        if (BusinessDatabaseOptions.Count == 0)
        {
            var databaseName = _session.SelectedBusinessDatabaseName;
            BusinessDatabaseOptions.Add(new BusinessDatabaseOption
            {
                TenantCode = _session.TenantCode,
                DatabaseName = databaseName,
                CompanyName = _session.SelectedBusinessDatabaseDisplayName,
                DisplayName = _session.SelectedBusinessDatabaseLabel
            });
        }

        SelectedBusinessDatabaseOption = BusinessDatabaseOptions.FirstOrDefault(current =>
                                         string.Equals(current.DatabaseName, _session.SelectedBusinessDatabaseName, StringComparison.OrdinalIgnoreCase))
                                     ?? BusinessDatabaseOptions.FirstOrDefault();
        CurrentBusinessDatabaseLabel = _session.SelectedBusinessDatabaseLabel;
        OnPropertyChanged(nameof(CanLoadSelectedBusinessDatabase));
    }

    [RelayCommand]
    private async Task LoadSelectedBusinessDatabaseAsync()
    {
        if (!CanManageBusinessDatabaseSelection)
        {
            StatusMessage = "업체 DB 선택은 관리자 권한이 있는 계정만 변경할 수 있습니다.";
            return;
        }

        if (SelectedBusinessDatabaseOption is null)
        {
            StatusMessage = "불러올 업체 DB를 선택하세요.";
            return;
        }

        var target = SelectedBusinessDatabaseOption;
        var previousDatabaseName = _session.SelectedBusinessDatabaseName;
        var previousDisplayName = _session.SelectedBusinessDatabaseDisplayName;
        var businessCacheReplacementCommitted = false;

        try
        {
            IsBusy = true;

            async Task ApplyTransitionAsync()
            {
                try
                {
                    if (await _local.HasPendingSyncChangesAsync())
                    {
                        StatusMessage = "미동기화 변경사항이 있어 업체 DB를 전환할 수 없습니다. 먼저 동기화를 완료하세요.";
                        return;
                    }

                    _session.SetBusinessDatabase(target.DatabaseName, target.CompanyName);
                    var cacheReplaced = await _sync
                        .ReplaceCurrentBusinessScopeCacheFromServerAsync();
                    if (!cacheReplaced)
                    {
                        throw new InvalidOperationException(
                            "대상 업체 DB 데이터를 안전하게 받지 못해 전환을 중단했습니다. 기존 업체 데이터는 유지됩니다.");
                    }

                    businessCacheReplacementCommitted = true;
                    if (_applyBusinessDatabaseChangeAsync is not null)
                        await _applyBusinessDatabaseChangeAsync.Invoke();

                    BusinessDatabaseChanged = true;
                    CurrentBusinessDatabaseLabel = _session.SelectedBusinessDatabaseLabel;
                    await ReloadCompanyProfilesAsync();
                    await LoadCurrentUserCompanyProfileAsync();
                    OnPropertyChanged(nameof(CanLoadSelectedBusinessDatabase));
                    StatusMessage = $"업체 DB를 {target.DisplayName} 기준으로 불러왔습니다.";
                }
                catch
                {
                    if (!businessCacheReplacementCommitted)
                    {
                        // Keep rollback inside the transition coordinator's data gate so
                        // no passive reload can publish the failed target scope first.
                        _session.SetBusinessDatabase(previousDatabaseName, previousDisplayName);
                    }
                    else
                    {
                        // The target cache is already committed atomically. Reverting only
                        // the session would pair the previous scope with target data.
                        BusinessDatabaseChanged = true;
                    }

                    CurrentBusinessDatabaseLabel = _session.SelectedBusinessDatabaseLabel;
                    OnPropertyChanged(nameof(CanLoadSelectedBusinessDatabase));
                    throw;
                }
            }

            if (_runBusinessDatabaseTransitionAsync is not null)
                await _runBusinessDatabaseTransitionAsync.Invoke(ApplyTransitionAsync);
            else
                await ApplyTransitionAsync();
        }
        catch (Exception ex)
        {
            if (!businessCacheReplacementCommitted)
                _session.SetBusinessDatabase(previousDatabaseName, previousDisplayName);
            else
                BusinessDatabaseChanged = true;

            CurrentBusinessDatabaseLabel = _session.SelectedBusinessDatabaseLabel;
            OnPropertyChanged(nameof(CanLoadSelectedBusinessDatabase));
            StatusMessage = businessCacheReplacementCommitted
                ? $"업체 DB 데이터 전환은 완료됐지만 화면을 모두 다시 불러오지 못했습니다. 앱을 다시 열거나 동기화를 재시도하세요: {ex.Message}"
                : $"업체 DB 불러오기 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
