using GeoraePlan.Mobile.App.ViewModels;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class PaymentDraftPage : ContentPage
{
    private readonly PaymentDraftViewModel _viewModel;

    public PaymentDraftPage()
    {
        Title = "수금 입력";
        _viewModel = ServiceHelper.GetRequiredService<PaymentDraftViewModel>();
        BindingContext = _viewModel;

        var invoicePicker = new Picker { Title = "전표 선택" };
        invoicePicker.SetBinding(Picker.ItemsSourceProperty, nameof(PaymentDraftViewModel.Invoices));
        invoicePicker.SetBinding(Picker.SelectedItemProperty, nameof(PaymentDraftViewModel.SelectedInvoice));
        invoicePicker.ItemDisplayBinding = new Binding(nameof(InvoiceDto.InvoiceNumber));

        var datePicker = new DatePicker();
        datePicker.SetBinding(DatePicker.DateProperty, nameof(PaymentDraftViewModel.PaymentDate));

        var amountEntry = new Entry { Keyboard = Keyboard.Numeric, Placeholder = "수금 금액" };
        amountEntry.SetBinding(Entry.TextProperty, nameof(PaymentDraftViewModel.AmountText));

        var noteEditor = new Editor { AutoSize = EditorAutoSizeOption.TextChanges, Placeholder = "비고" };
        noteEditor.SetBinding(Editor.TextProperty, nameof(PaymentDraftViewModel.Note));

        var saveButton = new Button { Text = "수금 임시저장" };
        saveButton.SetBinding(Button.CommandProperty, nameof(PaymentDraftViewModel.SaveDraftCommand));

        var statusLabel = new Label { TextColor = Colors.DimGray };
        statusLabel.SetBinding(Label.TextProperty, nameof(PaymentDraftViewModel.StatusMessage));

        var activity = new ActivityIndicator();
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(PaymentDraftViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(PaymentDraftViewModel.IsBusy));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 12,
                Children =
                {
                    new Label { Text = "전표", FontAttributes = FontAttributes.Bold },
                    invoicePicker,
                    new Label { Text = "수금일자", FontAttributes = FontAttributes.Bold },
                    datePicker,
                    new Label { Text = "금액", FontAttributes = FontAttributes.Bold },
                    amountEntry,
                    new Label { Text = "비고", FontAttributes = FontAttributes.Bold },
                    noteEditor,
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
