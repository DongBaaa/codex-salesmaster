using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.Views;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class EnvironmentSettingsViewModel
{
    public bool CanOpenAuditLogLookup =>
        _session.IsLoggedIn &&
        (_session.HasAdministrativePrivileges ||
         _session.HasPermission(AppPermissionNames.DataBackupRestore));

    public string AuditLogLookupHint => CanOpenAuditLogLookup
        ? "현재 계정의 tenant/office 범위 안에서 로컬 작업 이력을 읽기 전용으로 조회합니다."
        : "관리자 또는 Data.BackupRestore 권한이 있는 계정만 작업 이력을 조회할 수 있습니다.";

    [RelayCommand]
    private void OpenAuditLogLookup()
    {
        if (!CanOpenAuditLogLookup)
        {
            StatusMessage = "작업 이력 조회는 관리자 또는 Data.BackupRestore 권한이 있는 계정만 사용할 수 있습니다.";
            return;
        }

        var viewModel = new AuditLogLookupViewModel(_local, _session);
        var window = new AuditLogLookupWindow(viewModel);
        var owner = ResolveActiveWindow();
        if (owner is not null)
            window.Owner = owner;

        WindowShowHelper.ShowModelessWithDeferredLoad(
            window,
            viewModel.InitializeAsync,
            "작업 이력 조회",
            "작업 이력 데이터를 불러오지 못했습니다.",
            messageOwner: owner);
        StatusMessage = "읽기 전용 작업 이력 조회창을 열었습니다.";
    }
}
