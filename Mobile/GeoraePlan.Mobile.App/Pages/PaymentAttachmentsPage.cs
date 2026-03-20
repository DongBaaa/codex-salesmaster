using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class PaymentAttachmentsPage : ContentPage
{
    private readonly PaymentAttachmentsViewModel _viewModel;
    private readonly Guid _paymentId;
    private readonly string _titleText;
    private bool _initialized;

    public PaymentAttachmentsPage(Guid paymentId, string titleText)
    {
        GeoraePlanTheme.ApplyPage(this, "수금 첨부");

        _paymentId = paymentId;
        _titleText = titleText;
        _viewModel = ServiceHelper.GetRequiredService<PaymentAttachmentsViewModel>();
        BindingContext = _viewModel;

        var titleLabel = GeoraePlanTheme.CreateSectionTitle(string.Empty, 18);
        titleLabel.SetBinding(Label.TextProperty, nameof(PaymentAttachmentsViewModel.TitleText));

        var refreshButton = GeoraePlanTheme.CreateButton("새로고침", GeoraePlanTheme.SecondaryButton);
        refreshButton.SetBinding(Button.CommandProperty, nameof(PaymentAttachmentsViewModel.RefreshCommand));

        var activity = new ActivityIndicator { Color = GeoraePlanTheme.Accent };
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(PaymentAttachmentsViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(PaymentAttachmentsViewModel.IsBusy));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(PaymentAttachmentsViewModel.StatusMessage));

        var collectionView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("첨부 파일이 없습니다."),
            ItemTemplate = new DataTemplate(() =>
            {
                var fileLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 14);
                fileLabel.FontAttributes = FontAttributes.Bold;
                fileLabel.SetBinding(Label.TextProperty, nameof(PaymentAttachmentDto.FileName));

                var metaLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
                metaLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new PaymentAttachmentMetaConverter()));

                var descriptionLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
                descriptionLabel.SetBinding(Label.TextProperty, nameof(PaymentAttachmentDto.Description));

                var openButton = GeoraePlanTheme.CreateButton("열기", GeoraePlanTheme.Purple);
                openButton.Clicked += async (sender, _) =>
                {
                    if (sender is Button button && button.BindingContext is PaymentAttachmentDto attachment)
                        await _viewModel.OpenAttachmentAsync(attachment);
                };

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.SurfaceAlt,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Padding = 14,
                    Margin = new Thickness(0, 0, 0, 8),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 6,
                        Children =
                        {
                            fileLabel,
                            metaLabel,
                            descriptionLabel,
                            openButton
                        }
                    }
                };
            })
        };
        collectionView.SetBinding(ItemsView.ItemsSourceProperty, nameof(PaymentAttachmentsViewModel.Attachments));

        var contentGrid = new Grid
        {
            Padding = 16,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            },
            RowSpacing = 12
        };
        contentGrid.Add(titleLabel);
        Grid.SetRow(titleLabel, 0);
        contentGrid.Add(refreshButton);
        Grid.SetRow(refreshButton, 1);
        contentGrid.Add(activity);
        Grid.SetRow(activity, 2);
        contentGrid.Add(statusLabel);
        Grid.SetRow(statusLabel, 3);
        contentGrid.Add(collectionView);
        Grid.SetRow(collectionView, 4);

        Content = contentGrid;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_initialized)
            return;

        _initialized = true;
        await _viewModel.InitializeAsync(_paymentId, _titleText);
    }

    private sealed class PaymentAttachmentMetaConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not PaymentAttachmentDto attachment)
                return string.Empty;

            return $"{attachment.AttachmentType} / {attachment.FileSize / 1024d:N1} KB / {attachment.UploadedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}
