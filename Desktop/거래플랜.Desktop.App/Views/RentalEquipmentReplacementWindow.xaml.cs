using System.Windows;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalEquipmentReplacementWindow : Window
{
    private static readonly string[] OriginalStatusOptions =
    [
        "회수",
        "점검중",
        "대기",
        "폐기"
    ];

    public RentalEquipmentReplacementRequest ReplacementRequest { get; }

    public RentalEquipmentReplacementWindow(
        LocalRentalAsset originalAsset,
        LocalRentalAsset replacementAsset,
        RentalEquipmentReplacementRequest replacementRequest)
    {
        ArgumentNullException.ThrowIfNull(originalAsset);
        ArgumentNullException.ThrowIfNull(replacementAsset);

        ReplacementRequest = replacementRequest ?? throw new ArgumentNullException(nameof(replacementRequest));
        InitializeComponent();

        OriginalSummaryText.Text = BuildSummary(originalAsset);
        OriginalDetailText.Text = BuildDetail(originalAsset);
        ReplacementSummaryText.Text = BuildSummary(replacementAsset);
        ReplacementDetailText.Text = BuildDetail(replacementAsset);
        ReplacementDatePicker.SelectedDate = ReplacementRequest.ReplacementDate == default
            ? DateTime.Today
            : ReplacementRequest.ReplacementDate.ToDateTime(TimeOnly.MinValue);
        OriginalStatusBox.ItemsSource = OriginalStatusOptions;
        OriginalStatusBox.SelectedItem = string.IsNullOrWhiteSpace(ReplacementRequest.OriginalAssetNextStatus)
            ? "회수"
            : ReplacementRequest.OriginalAssetNextStatus.Trim();
        ReasonBox.Text = string.IsNullOrWhiteSpace(ReplacementRequest.ChangeReason)
            ? "렌탈 장비 교체"
            : ReplacementRequest.ChangeReason.Trim();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (ReplacementDatePicker.SelectedDate is not DateTime selectedDate)
        {
            MessageBox.Show("교체일을 입력하세요.", "렌탈 장비 교체", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var nextStatus = (OriginalStatusBox.SelectedItem as string ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(nextStatus))
        {
            MessageBox.Show("기존 장비 상태를 선택하세요.", "렌탈 장비 교체", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var reason = (ReasonBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            MessageBox.Show("사유/메모를 입력하세요.", "렌탈 장비 교체", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ReplacementRequest.ReplacementDate = DateOnly.FromDateTime(selectedDate);
        ReplacementRequest.OriginalAssetNextStatus = nextStatus;
        ReplacementRequest.ChangeReason = reason;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string BuildSummary(LocalRentalAsset asset)
    {
        var managementNumber = FirstNonEmpty(asset.ManagementNumber, asset.ManagementId, asset.MachineNumber);
        var itemName = FirstNonEmpty(asset.ItemName, "품명 미입력");
        return string.IsNullOrWhiteSpace(managementNumber)
            ? itemName
            : $"{managementNumber} / {itemName}";
    }

    private static string BuildDetail(LocalRentalAsset asset)
    {
        var status = FirstNonEmpty(asset.AssetStatus, "상태 미입력");
        var customer = FirstNonEmpty(asset.CurrentCustomerName, asset.CustomerName, "거래처 없음");
        var installLocation = FirstNonEmpty(asset.InstallLocation, asset.InstallSiteName, "설치위치 없음");
        var machineNumber = FirstNonEmpty(asset.MachineNumber, "기계번호 없음");
        return $"상태 {status} · 거래처 {customer} · 위치 {installLocation} · 기계번호 {machineNumber}";
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
