using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class MainViewModel
{
    private int _previewCustomerContractVersion;

    [ObservableProperty]
    private LocalCustomerContract? _previewCustomerContract;

    public bool HasPreviewCustomerContract => PreviewCustomerContract is not null;

    public string PreviewCustomerContractButtonToolTip => PreviewCustomerContract is null
        ? "연결된 계약서가 없습니다."
        : $"{PreviewCustomerContract.ContractType} / {PreviewCustomerContract.FileName}";

    partial void OnPreviewCustomerContractChanged(LocalCustomerContract? value)
    {
        OnPropertyChanged(nameof(HasPreviewCustomerContract));
        OnPropertyChanged(nameof(PreviewCustomerContractButtonToolTip));
        OpenPreviewCustomerContractCommand.NotifyCanExecuteChanged();
    }

    private void RequestRefreshPreviewCustomerContract(LocalCustomer? customer)
    {
        var version = Interlocked.Increment(ref _previewCustomerContractVersion);
        UiTaskHelper.Forget(
            RefreshPreviewCustomerContractAsync(customer, version),
            "MAIN",
            "거래처 대표 계약서 미리보기 갱신",
            ex =>
            {
                if (IsCurrentPreviewCustomerContract(version))
                    AppLogger.Warn("MAIN", $"거래처 계약서 미리보기 갱신 실패: {ex.Message}");
            });
    }

    private async Task RefreshPreviewCustomerContractAsync(LocalCustomer? customer, int version)
    {
        if (customer is null)
        {
            if (!IsCurrentPreviewCustomerContract(version))
                return;

            PreviewCustomerContract = null;
            return;
        }

        var contract = await _local.GetPreferredCustomerContractAsync(customer.Id, _session);
        if (!IsCurrentPreviewCustomerContract(version))
            return;

        PreviewCustomerContract = contract;
    }

    private bool IsCurrentPreviewCustomerContract(int version)
        => version == Volatile.Read(ref _previewCustomerContractVersion);

    private bool CanOpenPreviewCustomerContract() => PreviewCustomerContract is not null;

    [RelayCommand(CanExecute = nameof(CanOpenPreviewCustomerContract))]
    private void OpenPreviewCustomerContract()
    {
        var contract = PreviewCustomerContract;
        if (contract is null)
            return;

        try
        {
            CustomerContractPreviewService.Open(contract);
        }
        catch (Exception ex)
        {
            AppLogger.Error("MAIN", "대표 계약서 열기 실패", ex);
            MessageBox.Show(
                $"계약서를 열지 못했습니다.\n{ex.Message}",
                "계약서 보기",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
