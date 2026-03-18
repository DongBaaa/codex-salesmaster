using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Desktop.App.ViewModels;

namespace SalesMaster.Desktop.App.Views;

public partial class SalesWindow : Window
{
    private readonly SalesViewModel _vm;
    private bool _allowCloseWithoutSave;

    public SalesWindow(SalesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
        {
            Close();
            e.Handled = true;
        }

        if (e.Key == Key.F6)
        {
            _vm.StartNewInvoiceCommand.Execute(null);
            e.Handled = true;
        }

        if (e.Key == Key.F9)
        {
            _vm.PrintCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void PaymentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedCustomer is null)
        {
            MessageBox.Show("먼저 거래처를 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_vm.HasPendingChanges)
        {
            var saveFirst = MessageBox.Show(
                "수금/지급을 열기 전에 현재 전표를 저장하시겠습니까?",
                "전표 저장",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (saveFirst != MessageBoxResult.Yes)
                return;

            await _vm.SaveCommand.ExecuteAsync(null);
            if (_vm.HasPendingChanges)
                return;
        }

        var savedInvoice = await _vm.LocalStateService.GetInvoiceAsync(_vm.InvoiceId, _vm.SessionState);
        if (savedInvoice is null)
        {
            MessageBox.Show(
                "저장된 전표를 찾을 수 없어 수금/지급을 열 수 없습니다.",
                "알림",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var paymentVm = new PaymentViewModel(_vm.LocalStateService, _vm.SessionState);
        await paymentVm.LoadAsync(_vm.SelectedCustomer);
        await paymentVm.ConfigureForInvoiceAsync(savedInvoice);

        var paymentWindow = new PaymentWindow(paymentVm)
        {
            Owner = this
        };

        paymentWindow.ShowDialog();
        await _vm.RefreshPaymentSummaryAsync();
    }

    private async void LoadPreviousHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedCustomer is null)
        {
            MessageBox.Show("거래처를 먼저 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var invoices = await _vm.GetPreviousInvoicesAsync();
        if (invoices.Count == 0)
        {
            MessageBox.Show("불러올 이전 기록이 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rows = invoices
            .Select(invoice =>
            {
                var activeLines = invoice.Lines.Where(line => !line.IsDeleted).ToList();
                var displayNumber = string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
                    ? invoice.LocalTempNumber
                    : invoice.InvoiceNumber;
                var summary = string.Join(", ", activeLines.Select(line => line.ItemNameOriginal).Where(name => !string.IsNullOrWhiteSpace(name)).Take(4));
                if (activeLines.Count > 4)
                    summary += " ...";

                return new InvoiceHistorySelectionRow
                {
                    InvoiceId = invoice.Id,
                    InvoiceDate = invoice.InvoiceDate,
                    InvoiceNumber = displayNumber,
                    TotalAmount = invoice.TotalAmount,
                    LineCount = activeLines.Count,
                    Summary = summary,
                    Memo = invoice.Memo ?? string.Empty
                };
            })
            .ToList();

        var dialog = new InvoiceHistoryWindow(rows)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        if (_vm.Lines.Any(line => !string.IsNullOrWhiteSpace(line.ItemName)))
        {
            var replaceExisting = MessageBox.Show(
                "현재 입력된 항목을 선택한 이전 기록으로 교체합니다. 계속할까요?",
                "이전기록불러오기",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (replaceExisting != MessageBoxResult.Yes)
                return;
        }

        var selectedIds = dialog.SelectedInvoiceIds.ToHashSet();
        var selectedInvoices = invoices.Where(invoice => selectedIds.Contains(invoice.Id)).ToList();
        _vm.ImportPreviousInvoices(selectedInvoices, replaceExistingLines: true);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowCloseWithoutSave) return;
        if (!_vm.HasMeaningfulDraftContentForClose) return;
        if (!_vm.HasPendingChanges) return;

        var saved = false;
        try
        {
            saved = _vm.TryAutoSaveOnCloseAsync().GetAwaiter().GetResult();
        }
        catch
        {
            saved = false;
        }

        if (saved) return;

        var discard = MessageBox.Show(
            "자동저장에 실패했습니다. 저장 없이 닫을까요?",
            "확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (discard == MessageBoxResult.Yes)
        {
            _allowCloseWithoutSave = true;
            return;
        }

        e.Cancel = true;
    }

    private void CustomerSelectButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new LookupWindow(
            "거래처 검색",
            BuildCustomerRows(),
            "거래처 등록",
            async () =>
            {
                var customerVm = new CustomerEditViewModel(_vm.LocalStateService, _vm.SessionState);
                await customerVm.LoadAsync();
                var customerWindow = new CustomerEditWindow(customerVm) { Owner = this };
                customerWindow.ShowDialog();

                await _vm.ReloadCustomersAsync();
                return BuildCustomerRows();
            })
        { Owner = this };

        if (dlg.ShowDialog() == true && dlg.SelectedRow?.Tag is LocalCustomer selected)
        {
            _vm.SetCustomer(selected);
        }
    }

    private List<LookupRow> BuildCustomerRows()
    {
        return _vm.GetSelectableCustomers()
            .Select(c => new LookupRow
            {
                Id = c.Id,
                PrimaryText = c.NameOriginal,
                SecondaryText = $"{CustomerTradeTypes.Normalize(c.TradeType)} | {c.Phone}",
                Tag = c
            })
            .ToList();
    }

    private void InputItemNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        var keyword = InputItemNameTextBox.Text.Trim();
        var matches = _vm.FindItemsForQuickInput(keyword);
        if (matches.Count == 0)
        {
            _vm.StatusMessage = "입력한 품명과 일치하는 상품이 없습니다.";
            return;
        }

        if (matches.Count == 1)
        {
            _vm.ApplyInputItem(matches[0]);
            _vm.StatusMessage = "상품 정보를 자동으로 입력했습니다.";
            return;
        }

        ShowItemLookup(matches, "재고 선택");
    }

    private void ItemLookupButton_Click(object sender, RoutedEventArgs e)
    {
        var keyword = InputItemNameTextBox.Text.Trim();
        var items = _vm.FindItemsForQuickInput(keyword);
        if (items.Count == 0)
        {
            items = _vm.GetAllItems();
        }

        ShowItemLookup(items, "상품 목록");
    }

    private void ItemSearchResultsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedInputItem is null) return;
        _vm.ApplyInputItem(_vm.SelectedInputItem);
        _vm.StatusMessage = "상품 정보를 입력칸으로 반영했습니다.";
    }

    private void ShowItemLookup(IReadOnlyList<LocalItem> items, string title)
    {
        var rows = BuildItemRows(items);

        Func<Task<IReadOnlyList<LookupRow>>>? registerAction = null;
        string? registerButtonText = null;

        if (title.Contains("상품", StringComparison.Ordinal))
        {
            registerButtonText = "상품 등록";
            registerAction = async () =>
            {
                var inventoryVm = new InventoryViewModel(_vm.LocalStateService, _vm.SessionState);
                await inventoryVm.LoadAsync();
                inventoryVm.NewItemCommand.Execute(null);

                var inventoryWindow = new InventoryWindow(inventoryVm) { Owner = this };
                inventoryWindow.ShowDialog();

                await _vm.ReloadItemsAsync();
                return BuildItemRows(_vm.GetAllItems());
            };
        }

        var dlg = new LookupWindow(title, rows, registerButtonText, registerAction) { Owner = this };

        if (dlg.ShowDialog() == true && dlg.SelectedRow?.Tag is LocalItem selected)
        {
            _vm.ApplyInputItem(selected);
            _vm.StatusMessage = "상품을 선택해 입력했습니다.";
        }
    }

    private static List<LookupRow> BuildItemRows(IEnumerable<LocalItem> items)
    {
        return items.Select(i => new LookupRow
            {
                Id = i.Id,
                PrimaryText = i.NameOriginal,
                SecondaryText = $"{i.SpecificationOriginal} | 재고 {i.CurrentStock:N0}",
                Tag = i
            })
            .ToList();
    }
}
