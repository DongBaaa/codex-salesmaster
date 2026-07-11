using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class AuditLogLookupViewModel : ObservableObject
{
    private static DateOnly DefaultFilterFrom => DateOnly.FromDateTime(DateTime.Today.AddDays(-30));
    private static DateOnly DefaultFilterTo => DateOnly.FromDateTime(DateTime.Today);

    private readonly LocalStateService _local;
    private readonly SessionState _session;

    public AuditLogLookupViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;

        EntityOptions = new ObservableCollection<DisplayOption>
        {
            new() { Value = string.Empty, DisplayName = "전체 엔티티" },
            new() { Value = "LocalCustomer", DisplayName = "거래처" },
            new() { Value = "LocalCustomerContract", DisplayName = "거래처 계약" },
            new() { Value = "LocalItem", DisplayName = "품목" },
            new() { Value = "LocalInvoice", DisplayName = "전표" },
            new() { Value = "LocalInvoiceLine", DisplayName = "전표 항목" },
            new() { Value = "LocalPayment", DisplayName = "수금·지급" },
            new() { Value = "LocalTransaction", DisplayName = "거래 전표" },
            new() { Value = "LocalTransactionAttachment", DisplayName = "거래 증빙" },
            new() { Value = "LocalInventoryTransfer", DisplayName = "재고이동" },
            new() { Value = "LocalRentalBillingProfile", DisplayName = "렌탈 청구 프로필" },
            new() { Value = "LocalRentalAsset", DisplayName = "렌탈 자산" },
            new() { Value = "LocalRentalAssetAssignmentHistory", DisplayName = "자산 배정 이력" },
            new() { Value = "LocalRentalBillingLog", DisplayName = "렌탈 청구 이력" },
            new() { Value = "LocalCompanyProfile", DisplayName = "회사 설정" },
            new() { Value = "LocalCustomerCategory", DisplayName = "거래처 분류" },
            new() { Value = "LocalPriceGradeOption", DisplayName = "가격 등급" },
            new() { Value = "LocalTradeTypeOption", DisplayName = "거래 유형" },
            new() { Value = "LocalItemCategoryOption", DisplayName = "품목 분류" },
            new() { Value = "LocalRentalManagementCompany", DisplayName = "렌탈 관리업체" }
        };
        SelectedEntityOption = EntityOptions[0];
    }

    public ObservableCollection<LocalStateService.AuditLogLookupRow> Rows { get; } =
        new ResettableObservableCollection<LocalStateService.AuditLogLookupRow>();

    public ObservableCollection<DisplayOption> EntityOptions { get; }

    [ObservableProperty] private DateOnly? _filterFrom = DefaultFilterFrom;
    [ObservableProperty] private DateOnly? _filterTo = DefaultFilterTo;
    [ObservableProperty] private string _usernameFilter = string.Empty;
    [ObservableProperty] private DisplayOption? _selectedEntityOption;
    [ObservableProperty] private string _actionFilter = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isTruncated;
    [ObservableProperty] private bool _isScanLimitReached;
    [ObservableProperty] private string _statusText = "작업 이력을 불러올 준비가 되었습니다.";
    [ObservableProperty] private string _limitStatusText = "최신순으로 최대 1,000건을 표시합니다.";
    [ObservableProperty] private string _emptyMessage = "조건에 맞는 작업 이력이 없습니다.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    [NotifyPropertyChangedFor(nameof(IsSelectionEmpty))]
    private LocalStateService.AuditLogLookupRow? _selectedRow;

    public bool CanLookupAuditLogs =>
        _session.IsLoggedIn &&
        (_session.HasAdministrativePrivileges ||
         _session.HasPermission(AppPermissionNames.DataBackupRestore));

    public bool HasSelectedRow => SelectedRow is not null;
    public bool IsSelectionEmpty => SelectedRow is null;

    public Task InitializeAsync()
        => SearchCoreAsync();

    [RelayCommand]
    private Task SearchAsync()
        => SearchCoreAsync();

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        FilterFrom = DefaultFilterFrom;
        FilterTo = DefaultFilterTo;
        UsernameFilter = string.Empty;
        SelectedEntityOption = EntityOptions[0];
        ActionFilter = string.Empty;
        SearchText = string.Empty;
        await SearchCoreAsync();
    }

    private async Task SearchCoreAsync()
    {
        if (IsBusy)
            return;

        if (!CanLookupAuditLogs)
        {
            Rows.Clear();
            SelectedRow = null;
            IsEmpty = true;
            IsTruncated = false;
            IsScanLimitReached = false;
            StatusText = "작업 이력 조회는 관리자 또는 Data.BackupRestore 권한이 있는 계정만 사용할 수 있습니다.";
            LimitStatusText = "조회 권한이 없습니다.";
            EmptyMessage = "작업 이력 조회 권한이 없습니다.";
            return;
        }

        IsBusy = true;
        StatusText = "작업 이력을 조회하는 중입니다.";
        var selectedId = SelectedRow?.Id;
        try
        {
            var result = await _local.LookupAuditLogsAsync(new LocalStateService.AuditLogLookupRequest
            {
                FromDate = FilterFrom,
                ToDate = FilterTo,
                Username = UsernameFilter,
                EntityName = SelectedEntityOption?.Value ?? string.Empty,
                Action = ActionFilter,
                SearchText = SearchText
            });

            if (!result.IsAuthorized)
            {
                Rows.Clear();
                SelectedRow = null;
                IsEmpty = true;
                IsTruncated = false;
                IsScanLimitReached = false;
                StatusText = "작업 이력 조회 권한이 없습니다.";
                LimitStatusText = "조회 권한이 없습니다.";
                EmptyMessage = "작업 이력 조회 권한이 없습니다.";
                return;
            }

            Rows.ReplaceWith(result.Rows);
            SelectedRow = selectedId.HasValue
                ? Rows.FirstOrDefault(row => row.Id == selectedId.Value) ?? Rows.FirstOrDefault()
                : Rows.FirstOrDefault();
            IsEmpty = Rows.Count == 0;
            IsTruncated = result.IsTruncated;
            IsScanLimitReached = result.IsScanLimitReached;
            if (result.IsScanLimitReached)
            {
                StatusText = $"최신 {result.ScannedCount:N0}건까지만 검사해 범위 내 작업 이력 {Rows.Count:N0}건을 표시합니다. 날짜나 필터를 좁혀 다시 조회하세요.";
                LimitStatusText = $"스캔 상한 {result.ScanLimit:N0}건 적용됨";
                EmptyMessage = $"최신 {result.ScanLimit:N0}건 안에서 표시할 작업 이력이 없습니다. 조회 기간을 좁혀 다시 조회하세요.";
            }
            else if (result.IsTruncated)
            {
                StatusText = $"조건에 맞는 최신 {Rows.Count:N0}건을 표시합니다. 결과가 더 있어 {result.Limit:N0}건에서 잘렸습니다.";
                LimitStatusText = $"최신 {result.Limit:N0}건 제한 적용됨";
                EmptyMessage = "조건에 맞는 작업 이력이 없습니다.";
            }
            else
            {
                StatusText = $"조건에 맞는 작업 이력 {Rows.Count:N0}건을 최신순으로 표시합니다.";
                LimitStatusText = $"최신순 / 최대 {result.Limit:N0}건";
                EmptyMessage = "조건에 맞는 작업 이력이 없습니다.";
            }
        }
        catch (Exception ex)
        {
            Rows.Clear();
            SelectedRow = null;
            IsEmpty = true;
            IsTruncated = false;
            IsScanLimitReached = false;
            StatusText = $"작업 이력을 조회하지 못했습니다. {ex.Message}";
            LimitStatusText = "조회 실패";
            EmptyMessage = "작업 이력을 표시할 수 없습니다.";
            AppLogger.Warn("AUDIT", $"작업 이력 조회 실패: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
