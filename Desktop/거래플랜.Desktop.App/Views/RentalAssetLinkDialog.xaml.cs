using System.Windows;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalAssetLinkDialog : Window
{
    public RentalAssetLinkDialog(RentalAssetLinkDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RentalAssetLinkDialogViewModel viewModel)
            return;

        var relinkSelectionCount = viewModel.GetRelinkSelectionCount();
        if (relinkSelectionCount > 0)
        {
            var confirmation = MessageBox.Show(
                $"이미 다른 청구프로필에 연결된 장비 {relinkSelectionCount:N0}대가 포함되어 있습니다. 현재 거래처 청구로 재연결하시겠습니까?",
                "렌탈 자산 재연결 확인",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.OK)
                return;
        }

        DialogResult = true;
        Close();
    }
}
