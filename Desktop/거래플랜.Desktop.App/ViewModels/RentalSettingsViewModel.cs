using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalSettingsViewModel : ObservableObject
{
    private readonly RentalStateService _rental;
    private readonly LocalStateService _local;
    private readonly SessionState _session;

    [ObservableProperty] private string _alertDaysText = "7,3,1,0";
    [ObservableProperty] private string _billingWorkbookPath = string.Empty;
    [ObservableProperty] private string _assetWorkbookPath = string.Empty;
    [ObservableProperty] private string _statusMessage = "렌탈 기준설정을 불러오는 중입니다.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _customerLinkCleanupSummary = "메인 거래처명 기준 정리 진단을 불러오는 중입니다.";

    public ObservableCollection<LocalOffice> Offices { get; } = new();
    public ObservableCollection<RentalCustomerLinkCleanupRow> CustomerLinkCleanupRows { get; } = new();

    public bool CanManage => _session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.RentalSettingsEdit);
    public bool CanImport => _session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.RentalImport);

    public RentalSettingsViewModel(RentalStateService rental, LocalStateService local, SessionState session)
    {
        _rental = rental;
        _local = local;
        _session = session;
    }

    public async Task LoadAsync()
    {
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            AlertDaysText = await _rental.GetAlertDaysTextAsync();
            var paths = await _rental.GetImportPathsAsync();
            BillingWorkbookPath = paths.BillingPath;
            AssetWorkbookPath = paths.AssetPath;

            Offices.Clear();
            foreach (var office in await _local.GetOfficesAsync())
                Offices.Add(office);

            await ReloadCustomerLinkCleanupAsync();
            StatusMessage = $"렌탈 기준설정을 불러왔습니다. 거래처명 정리 검토 {CustomerLinkCleanupRows.Count:N0}건입니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAlertSettingsAsync()
    {
        if (!CanManage)
        {
            StatusMessage = "렌탈 기준설정 저장은 관리자 또는 렌탈 관리 권한이 있는 계정만 처리할 수 있습니다.";
            return;
        }

        await _rental.SaveAlertDaysTextAsync(AlertDaysText);
        await _rental.SaveImportPathsAsync(BillingWorkbookPath, AssetWorkbookPath);
        StatusMessage = "렌탈 알림/가져오기 경로 설정을 저장했습니다.";
    }

    [RelayCommand]
    private async Task ImportBillingWorkbookAsync()
    {
        if (!CanImport)
        {
            StatusMessage = "렌탈 청구 엑셀 가져오기는 관리자 또는 렌탈 가져오기 권한이 있는 계정만 사용할 수 있습니다.";
            return;
        }

        var result = await _rental.ImportBillingWorkbookAsync(BillingWorkbookPath, _session);
        StatusMessage = $"렌탈 청구 가져오기 완료: {result.Summary}";
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ImportAssetWorkbookAsync()
    {
        if (!CanImport)
        {
            StatusMessage = "렌탈 자산 엑셀 가져오기는 관리자 또는 렌탈 가져오기 권한이 있는 계정만 사용할 수 있습니다.";
            return;
        }

        var result = await _rental.ImportAssetWorkbookAsync(AssetWorkbookPath, _session);
        StatusMessage = $"렌탈 자산 가져오기 완료: {result.Summary}";
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ReloadCustomerLinkCleanupAsync()
    {
        var rows = await _rental.GetRentalCustomerLinkCleanupRowsAsync(_session);
        CustomerLinkCleanupRows.Clear();
        foreach (var row in rows)
            CustomerLinkCleanupRows.Add(row);

        var autoCount = rows.Count(row => row.CanAutoNormalize);
        CustomerLinkCleanupSummary = rows.Count == 0
            ? "메인 거래처명 기준으로 추가 정리가 필요한 렌탈 데이터가 없습니다."
            : $"검토 {rows.Count:N0}건 / 자동 동기화 가능 {autoCount:N0}건";
    }

    [RelayCommand]
    private async Task NormalizeCustomerLinksAsync()
    {
        if (!CanManage)
        {
            StatusMessage = "렌탈 거래처명 정리는 관리자 또는 렌탈 기준설정 권한이 있는 계정만 실행할 수 있습니다.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _rental.NormalizeRentalCustomerLinksAsync(_session);
            await ReloadCustomerLinkCleanupAsync();
            StatusMessage = $"메인 거래처명 동기화 완료: 청구프로필 {result.UpdatedProfileCount:N0}건, 설치자산 {result.UpdatedAssetCount:N0}건을 정리했습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BrowseBillingWorkbook()
    {
        var dialog = new OpenFileDialog
        {
            Title = "렌탈 청구 엑셀 선택",
            Filter = "Excel 파일|*.xlsx;*.xlsb;*.xls|모든 파일|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
            BillingWorkbookPath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseAssetWorkbook()
    {
        var dialog = new OpenFileDialog
        {
            Title = "렌탈 자산 엑셀 선택",
            Filter = "Excel 파일|*.xlsx;*.xlsb;*.xls|모든 파일|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
            AssetWorkbookPath = dialog.FileName;
    }
}
