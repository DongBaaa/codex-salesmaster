using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class CustomerEditWindow : Window
{
    private readonly CustomerEditViewModel _vm;
    private bool _allowCloseWithoutSave;
    private bool _closeInProgress;

    public CustomerEditWindow(CustomerEditViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Closing += Window_Closing;

        vm.SavedAndClose += HandleSavedAndClose;
        vm.SavedAndNew += () =>
        {
            // 저장 후 폼 초기화 완료 — 창은 유지
        };
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12) { DialogWindowCloseHelper.Close(this); e.Handled = true; }
        if (e.Key == Key.F6 && _vm.SaveAndNewCommand.CanExecute(null))
        {
            _vm.SaveAndNewCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogWindowCloseHelper.Close(this);

    private void OpenRentalAssetsButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(this, OpenRentalAssetsAsync, "UI", "거래처 연결 렌탈 자산 보기", "렌탈 자산 창을 여는 중 오류가 발생했습니다.");

    private void HandleSavedAndClose()
    {
        DialogWindowCloseHelper.Close(this, true);
    }

    private async Task OpenRentalAssetsAsync()
    {
        if (string.IsNullOrWhiteSpace(_vm.Name))
        {
            MessageBox.Show(
                this,
                "거래처명이 비어 있습니다. 거래처명을 먼저 입력한 뒤 다시 시도하세요.",
                "렌탈 자산 보기",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var mainWindow = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
        if (mainWindow is null)
        {
            MessageBox.Show(
                this,
                "메인 창 정보를 찾지 못해 렌탈 자산 창을 열 수 없습니다.",
                "렌탈 자산 보기",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var customer = new LocalCustomer
        {
            Id = _vm.CustomerId,
            NameOriginal = _vm.Name,
            ResponsibleOfficeCode = _vm.ResponsibleOfficeCode
        };

        var rentalAssetViewModel = new RentalAssetViewModel(
            mainWindow.RentalStateService,
            mainWindow.LocalStateService,
            mainWindow.RentalDocumentService,
            mainWindow.InvoicePrintService,
            mainWindow.SessionState);

        await rentalAssetViewModel.LoadAsync();
        await rentalAssetViewModel.ApplyInitialCustomerFilterAsync(customer);

        var rentalAssetWindow = new RentalAssetWindow(rentalAssetViewModel)
        {
            Owner = this
        };
        rentalAssetWindow.ShowDialog();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            if (_allowCloseWithoutSave)
                return;

            if (_closeInProgress)
            {
                e.Cancel = true;
                return;
            }

            if (!_vm.HasMeaningfulDraftContentForClose || !_vm.HasPendingChanges)
                return;

            e.Cancel = true;
            _closeInProgress = true;
            var requestDeferredClose = false;
            var previousCursor = Mouse.OverrideCursor;
            try
            {
                IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;

                var saved = await _vm.TryAutoSaveOnCloseAsync();
                if (saved)
                {
                    _allowCloseWithoutSave = true;
                    requestDeferredClose = true;
                }
                else
                {
                    var discard = MessageBox.Show(
                        $"{_vm.StatusMessage}\n\n저장되지 않은 변경사항이 있습니다. 저장 없이 닫을까요?",
                        "확인",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (discard == MessageBoxResult.Yes)
                    {
                        _allowCloseWithoutSave = true;
                        requestDeferredClose = true;
                    }
                }
            }
            finally
            {
                Mouse.OverrideCursor = previousCursor;
                if (!_allowCloseWithoutSave)
                    IsEnabled = true;
                _closeInProgress = false;
            }

            if (requestDeferredClose)
                _ = Dispatcher.BeginInvoke(new Action(() => DialogWindowCloseHelper.Close(this, _vm.HasPendingChanges ? false : true)));
        }
        catch (Exception ex)
        {
            AppLogger.Error("UI", "거래처 편집 창 닫기 처리 실패", ex);
            e.Cancel = true;
            IsEnabled = true;
            Mouse.OverrideCursor = null;
            _closeInProgress = false;

            MessageBox.Show(
                this,
                $"거래처 편집 창을 닫는 중 오류가 발생했습니다.{Environment.NewLine}{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
