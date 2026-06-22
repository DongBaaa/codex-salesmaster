using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class PaymentDraftPage : ContentPage
{
    private readonly PaymentDraftViewModel _viewModel;

    public PaymentDraftPage(InvoiceDto invoice)
        : this()
    {
        _viewModel.ConfigureInitialInvoice(invoice);
    }

    public PaymentDraftPage()
    {
        GeoraePlanTheme.ApplyPage(this, "수금/지급 입력");

        _viewModel = ServiceHelper.GetRequiredService<PaymentDraftViewModel>();
        _viewModel.SavedSuccessfully += HandleSavedSuccessfullyAsync;
        BindingContext = _viewModel;
        SetBinding(TitleProperty, new Binding(nameof(PaymentDraftViewModel.PageTitleText)));

        var invoicePicker = GeoraePlanTheme.CreatePicker("전표 선택");
        invoicePicker.SetBinding(Picker.ItemsSourceProperty, nameof(PaymentDraftViewModel.Invoices));
        invoicePicker.SetBinding(Picker.SelectedItemProperty, nameof(PaymentDraftViewModel.SelectedInvoice));
        invoicePicker.ItemDisplayBinding = new Binding(path: ".", converter: new InvoicePickerDisplayConverter());

        var datePicker = new DatePicker
        {
            BackgroundColor = GeoraePlanTheme.InputBackground,
            TextColor = Colors.Black
        };
        datePicker.SetBinding(DatePicker.DateProperty, nameof(PaymentDraftViewModel.PaymentDate));

        var selectedInvoiceSummary = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        selectedInvoiceSummary.SetBinding(Label.TextProperty, nameof(PaymentDraftViewModel.SelectedInvoiceSummary));

        var paymentMethodPicker = GeoraePlanTheme.CreateCompactPicker("수금/지급 방식 선택");
        paymentMethodPicker.ItemDisplayBinding = new Binding(nameof(MobilePaymentMethodOption.DisplayName));
        paymentMethodPicker.SetBinding(Picker.TitleProperty, nameof(PaymentDraftViewModel.PaymentMethodLabelText));
        paymentMethodPicker.SetBinding(Picker.ItemsSourceProperty, nameof(PaymentDraftViewModel.PaymentMethodOptions));
        paymentMethodPicker.SetBinding(Picker.SelectedItemProperty, nameof(PaymentDraftViewModel.SelectedPaymentMethod));

        var paymentMethodHelpLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        paymentMethodHelpLabel.SetBinding(Label.TextProperty, nameof(PaymentDraftViewModel.PaymentMethodHelpText));

        var amountEntry = GeoraePlanTheme.CreateEntry("수금/지급 금액");
        amountEntry.SetBinding(Entry.PlaceholderProperty, nameof(PaymentDraftViewModel.AmountPlaceholderText));
        amountEntry.Keyboard = Keyboard.Numeric;
        amountEntry.SetBinding(Entry.TextProperty, nameof(PaymentDraftViewModel.AmountText));

        var noteEditor = new Editor
        {
            AutoSize = EditorAutoSizeOption.TextChanges,
            Placeholder = "비고",
            BackgroundColor = GeoraePlanTheme.InputBackground,
            TextColor = Colors.Black,
            PlaceholderColor = Colors.Gray,
            MinimumHeightRequest = 96
        };
        noteEditor.SetBinding(Editor.TextProperty, nameof(PaymentDraftViewModel.Note));

        var attachmentSummary = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
        attachmentSummary.SetBinding(Label.TextProperty, nameof(PaymentDraftViewModel.AttachmentSummary));

        var attachButton = GeoraePlanTheme.CreateButton("내역 첨부하기", GeoraePlanTheme.Purple);
        attachButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await ShowAttachmentMenuAsync(),
                "수금/지급 입력 작업");

        var attachmentsView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            HeightRequest = 180,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("첨부된 파일이 없습니다."),
            ItemTemplate = new DataTemplate(() =>
            {
                var fileLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
                fileLabel.FontAttributes = FontAttributes.Bold;
                fileLabel.SetBinding(Label.TextProperty, nameof(PendingPaymentAttachmentRecord.FileName));

                var metaLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
                metaLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new AttachmentSummaryConverter()));

                var openButton = GeoraePlanTheme.CreateButton("열기", GeoraePlanTheme.SecondaryButton);
                openButton.HeightRequest = 38;
                openButton.Clicked += (sender, _) =>
                    MobileErrorHandler.FireAndForget(
                        async () =>
                        {
                    if (sender is Button button && button.BindingContext is PendingPaymentAttachmentRecord attachment)
                        await _viewModel.OpenAttachmentAsync(attachment);
                },
                        "수금/지급 입력 작업");

                var deleteButton = GeoraePlanTheme.CreateButton("삭제", GeoraePlanTheme.Danger);
                deleteButton.HeightRequest = 38;
                deleteButton.Clicked += (sender, _) =>
                    MobileErrorHandler.FireAndForget(
                        async () =>
                        {
                    if (sender is Button button && button.BindingContext is PendingPaymentAttachmentRecord attachment)
                        await _viewModel.RemoveAttachmentAsync(attachment);
                },
                        "수금/지급 입력 작업");

                var actionGrid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(new GridLength(8)),
                        new ColumnDefinition(GridLength.Star)
                    }
                };
                actionGrid.Add(openButton);
                Grid.SetColumn(openButton, 0);
                actionGrid.Add(deleteButton);
                Grid.SetColumn(deleteButton, 2);

                return GeoraePlanTheme.CreateCard(fileLabel, metaLabel, actionGrid);
            })
        };
        attachmentsView.SetBinding(ItemsView.ItemsSourceProperty, nameof(PaymentDraftViewModel.Attachments));

        var saveButton = GeoraePlanTheme.CreateButton("수금/지급 저장", GeoraePlanTheme.Accent);
        saveButton.SetBinding(Button.TextProperty, nameof(PaymentDraftViewModel.SaveButtonText));
        saveButton.SetBinding(Button.CommandProperty, nameof(PaymentDraftViewModel.SaveDraftCommand));
        saveButton.SetBinding(VisualElement.IsEnabledProperty, nameof(PaymentDraftViewModel.CanCreatePayments));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(PaymentDraftViewModel.StatusMessage));

        var activity = new ActivityIndicator { Color = GeoraePlanTheme.Accent };
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(PaymentDraftViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(PaymentDraftViewModel.IsBusy));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    GeoraePlanTheme.CreateCard(
                        CreateBoundSectionTitle(nameof(PaymentDraftViewModel.PageTitleText)),
                        GeoraePlanTheme.CreateBodyText("판매 전표는 수금, 구매 전표는 지급으로 자동 구분해 저장합니다."),
                        GeoraePlanTheme.CreateSectionTitle("전표", 14),
                        invoicePicker,
                        selectedInvoiceSummary,
                        CreateBoundSectionTitle(nameof(PaymentDraftViewModel.PaymentDateLabelText), 14),
                        datePicker,
                        CreateBoundSectionTitle(nameof(PaymentDraftViewModel.PaymentMethodLabelText), 14),
                        paymentMethodPicker,
                        paymentMethodHelpLabel,
                        GeoraePlanTheme.CreateSectionTitle("금액", 14),
                        amountEntry,
                        GeoraePlanTheme.CreateSectionTitle("비고", 14),
                        noteEditor,
                        CreateBoundSectionTitle(nameof(PaymentDraftViewModel.AttachmentSectionTitle), 14),
                        attachmentSummary,
                        attachButton,
                        attachmentsView,
                        saveButton,
                        activity,
                        statusLabel)
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        await MobileErrorHandler.RunGuardedAsync(
            async () =>
            {
await _viewModel.LoadAsync();
            },
            "수금/지급 입력 화면 초기화");
    }

    protected override void OnDisappearing()
    {
        _viewModel.SavedSuccessfully -= HandleSavedSuccessfullyAsync;
        base.OnDisappearing();
    }

    private async Task ShowAttachmentMenuAsync()
    {
        var action = await DisplayActionSheet("첨부 방식 선택", "취소", null, "PDF 파일 업로드", "카메라 촬영 이미지 업로드");
        if (action == "PDF 파일 업로드")
        {
            await _viewModel.AddPdfAttachmentAsync();
        }
        else if (action == "카메라 촬영 이미지 업로드")
        {
            await _viewModel.CaptureAttachmentAsync();
        }
    }

    private async Task HandleSavedSuccessfullyAsync()
    {
        if (Navigation.NavigationStack.Count > 1)
            await Shell.Current.Navigation.PopAsync();
    }

    private static Label CreateBoundSectionTitle(string bindingPath, double fontSize = 16)
    {
        var label = GeoraePlanTheme.CreateSectionTitle(string.Empty, fontSize);
        label.SetBinding(Label.TextProperty, bindingPath);
        return label;
    }

    private sealed class InvoicePickerDisplayConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not InvoiceDto invoice)
                return string.Empty;

            var kind = invoice.VoucherType == VoucherType.Purchase ? "구매" : "판매";
            var number = string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
                ? string.IsNullOrWhiteSpace(invoice.LocalTempNumber) ? "전표번호 미부여" : invoice.LocalTempNumber
                : invoice.InvoiceNumber;
            var customer = string.IsNullOrWhiteSpace(invoice.CustomerName) ? "거래처 미기록" : invoice.CustomerName;
            return $"{invoice.InvoiceDate:yyyy-MM-dd} {kind} · {customer} · {number} · {invoice.TotalAmount:N0}원";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class AttachmentSummaryConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not PendingPaymentAttachmentRecord attachment)
                return string.Empty;

            return $"{attachment.AttachmentType} / {attachment.FileSize / 1024d:N1} KB";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}
