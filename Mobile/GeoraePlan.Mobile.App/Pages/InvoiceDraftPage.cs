using GeoraePlan.Mobile.App.ViewModels;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class InvoiceDraftPage : ContentPage
{
    private readonly InvoiceDraftViewModel _viewModel;

    public InvoiceDraftPage()
    {
        Title = "전표 작성";
        _viewModel = ServiceHelper.GetRequiredService<InvoiceDraftViewModel>();
        BindingContext = _viewModel;

        var customerPicker = new Picker { Title = "거래처 선택" };
        customerPicker.SetBinding(Picker.ItemsSourceProperty, nameof(InvoiceDraftViewModel.Customers));
        customerPicker.SetBinding(Picker.SelectedItemProperty, nameof(InvoiceDraftViewModel.SelectedCustomer));
        customerPicker.ItemDisplayBinding = new Binding(nameof(CustomerDto.NameOriginal));

        var itemPicker = new Picker { Title = "품목 선택(선택사항)" };
        itemPicker.SetBinding(Picker.ItemsSourceProperty, nameof(InvoiceDraftViewModel.Items));
        itemPicker.SetBinding(Picker.SelectedItemProperty, nameof(InvoiceDraftViewModel.SelectedItem));
        itemPicker.ItemDisplayBinding = new Binding(nameof(ItemDto.NameOriginal));

        var datePicker = new DatePicker();
        datePicker.SetBinding(DatePicker.DateProperty, nameof(InvoiceDraftViewModel.InvoiceDate));

        var quantityEntry = new Entry { Keyboard = Keyboard.Numeric, Placeholder = "수량" };
        quantityEntry.SetBinding(Entry.TextProperty, nameof(InvoiceDraftViewModel.QuantityText));

        var unitPriceEntry = new Entry { Keyboard = Keyboard.Numeric, Placeholder = "단가" };
        unitPriceEntry.SetBinding(Entry.TextProperty, nameof(InvoiceDraftViewModel.UnitPriceText));

        var memoEditor = new Editor { AutoSize = EditorAutoSizeOption.TextChanges, Placeholder = "메모" };
        memoEditor.SetBinding(Editor.TextProperty, nameof(InvoiceDraftViewModel.Memo));

        var saveButton = new Button { Text = "전표 임시저장" };
        saveButton.SetBinding(Button.CommandProperty, nameof(InvoiceDraftViewModel.SaveDraftCommand));

        var statusLabel = new Label { TextColor = Colors.DimGray };
        statusLabel.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.StatusMessage));

        var activity = new ActivityIndicator();
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(InvoiceDraftViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(InvoiceDraftViewModel.IsBusy));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 12,
                Children =
                {
                    new Label { Text = "거래처", FontAttributes = FontAttributes.Bold },
                    customerPicker,
                    new Label { Text = "품목", FontAttributes = FontAttributes.Bold },
                    itemPicker,
                    new Label { Text = "전표일자", FontAttributes = FontAttributes.Bold },
                    datePicker,
                    new Label { Text = "수량", FontAttributes = FontAttributes.Bold },
                    quantityEntry,
                    new Label { Text = "단가", FontAttributes = FontAttributes.Bold },
                    unitPriceEntry,
                    new Label { Text = "메모", FontAttributes = FontAttributes.Bold },
                    memoEditor,
                    saveButton,
                    activity,
                    statusLabel
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }
}
