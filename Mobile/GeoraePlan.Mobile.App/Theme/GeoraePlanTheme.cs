using Microsoft.Maui.Controls.Shapes;

using System.Runtime.CompilerServices;

namespace GeoraePlan.Mobile.App.Theme;

public static class GeoraePlanTheme
{
    public static Color PageBackground => Color.FromArgb("#151F2E");
    public static Color Surface => Color.FromArgb("#1A2B4A");
    public static Color SurfaceAlt => Color.FromArgb("#1E2D40");
    public static Color Border => Color.FromArgb("#2E4060");
    public static Color Accent => Color.FromArgb("#4FC3F7");
    public static Color TextPrimary => Color.FromArgb("#EAF2FF");
    public static Color TextSecondary => Color.FromArgb("#9FB3C8");
    public static Color InputBackground => Color.FromArgb("#D9E6F5");
    public static Color Success => Color.FromArgb("#1B5E20");
    public static Color SecondaryButton => Color.FromArgb("#37474F");
    public static Color Purple => Color.FromArgb("#5E35B1");
    public static Color Danger => Color.FromArgb("#C62828");
    public static Color Brown => Color.FromArgb("#6D4C41");

    public static void ApplyPage(ContentPage page, string title)
    {
        page.Title = title;
        page.BackgroundColor = PageBackground;
    }

    public static SearchBar CreateSearchBar(string placeholder)
        => new()
        {
            Placeholder = placeholder,
            BackgroundColor = InputBackground,
            TextColor = Colors.Black,
            PlaceholderColor = Colors.Gray,
            CancelButtonColor = Accent,
            HorizontalOptions = LayoutOptions.Fill,
            MinimumWidthRequest = 0,
            MinimumHeightRequest = 44
        };

    public static Entry CreateEntry(string placeholder, bool isPassword = false)
        => new()
        {
            Placeholder = placeholder,
            IsPassword = isPassword,
            BackgroundColor = InputBackground,
            TextColor = Colors.Black,
            PlaceholderColor = Colors.Gray,
            ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
            HorizontalOptions = LayoutOptions.Fill,
            MinimumWidthRequest = 0,
            MinimumHeightRequest = 44
        };

    public static Entry CreateCompactEntry(string placeholder, bool isPassword = false)
    {
        var entry = CreateEntry(placeholder, isPassword);
        entry.MinimumHeightRequest = 36;
        entry.Margin = Thickness.Zero;
        entry.FontSize = 14;
        return entry;
    }

    public static Picker CreatePicker(string title)
        => new()
        {
            Title = title,
            BackgroundColor = InputBackground,
            TextColor = Colors.Black,
            TitleColor = Colors.Gray,
            HorizontalOptions = LayoutOptions.Fill,
            MinimumWidthRequest = 0,
            MinimumHeightRequest = 44
        };

    public static Picker CreateCompactPicker(string title)
    {
        var picker = CreatePicker(title);
        picker.MinimumHeightRequest = 36;
        picker.Margin = Thickness.Zero;
        picker.FontSize = 14;
        return picker;
    }

    public static Button CreateButton(
        string text,
        Color backgroundColor,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = backgroundColor,
            TextColor = Colors.White,
            FontAttributes = FontAttributes.Bold,
            CornerRadius = 10,
            HorizontalOptions = LayoutOptions.Fill,
            MinimumWidthRequest = 0,
            MinimumHeightRequest = 44
        };
#if GEORAEPLAN_MOBILE_UI_MATRIX
        UiMatrix.MobileUiMatrixActionRegistry.RegisterButton(
            button,
            sourceFile,
            sourceLine);
#endif
        return button;
    }

    public static Button CreateCompactButton(
        string text,
        Color backgroundColor,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0)
    {
        var button = CreateButton(
            text,
            backgroundColor,
            sourceFile,
            sourceLine);
        button.MinimumHeightRequest = 36;
        button.CornerRadius = 8;
        button.Padding = new Thickness(10, 0);
        button.FontSize = 13;
        return button;
    }

    public static Label CreateSectionTitle(string text, double fontSize = 16)
        => new()
        {
            Text = text,
            TextColor = TextPrimary,
            FontSize = fontSize,
            FontAttributes = FontAttributes.Bold,
            HorizontalOptions = LayoutOptions.Fill,
            LineBreakMode = LineBreakMode.WordWrap
        };

    public static Label CreateBodyText(string text, bool muted = true, double fontSize = 13)
        => new()
        {
            Text = text,
            TextColor = muted ? TextSecondary : TextPrimary,
            FontSize = fontSize,
            HorizontalOptions = LayoutOptions.Fill,
            LineBreakMode = LineBreakMode.WordWrap
        };

    public static Label CreateFieldLabel(string text)
        => new()
        {
            Text = text,
            TextColor = TextSecondary,
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            Margin = Thickness.Zero,
            HorizontalOptions = LayoutOptions.Fill,
            LineBreakMode = LineBreakMode.WordWrap
        };

    public static Label CreateStatusLabel()
        => new()
        {
            TextColor = TextSecondary,
            FontSize = 12,
            HorizontalOptions = LayoutOptions.Fill,
            LineBreakMode = LineBreakMode.WordWrap
        };

    public static FlexLayout CreateWrappingActions(params View[] children)
    {
        var layout = new FlexLayout
        {
            Direction = Microsoft.Maui.Layouts.FlexDirection.Row,
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            JustifyContent = Microsoft.Maui.Layouts.FlexJustify.Start,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Stretch,
            HorizontalOptions = LayoutOptions.Fill
        };

        foreach (var child in children)
        {
            child.MinimumWidthRequest = Math.Max(child.MinimumWidthRequest, 128);
            child.MinimumHeightRequest = Math.Max(child.MinimumHeightRequest, 80);
            child.Margin = new Thickness(0, 0, 8, 8);
            FlexLayout.SetGrow(child, 1);
            layout.Children.Add(child);
        }

        return layout;
    }

    public static Grid CreateStackedActionLayout(View primary, params View[] actions)
    {
        primary.HorizontalOptions = LayoutOptions.Fill;
        primary.MinimumWidthRequest = 0;

        var grid = new Grid
        {
            HorizontalOptions = LayoutOptions.Fill,
            RowSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            }
        };
        grid.Add(primary);
        grid.Add(CreateHorizontalActionScroller(actions), 0, 1);
        return grid;
    }

    public static ScrollView CreateHorizontalActionScroller(params View[] children)
    {
        var actions = new HorizontalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.Start
        };
        foreach (var child in children)
        {
            child.MinimumWidthRequest = Math.Max(child.MinimumWidthRequest, 84);
            actions.Children.Add(child);
        }

        return new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Always,
            Content = actions
        };
    }

    public static Editor CreateCompactEditor(string placeholder, double minHeight = 68)
        => new()
        {
            AutoSize = EditorAutoSizeOption.TextChanges,
            Placeholder = placeholder,
            BackgroundColor = InputBackground,
            TextColor = Colors.Black,
            PlaceholderColor = Colors.Gray,
            MinimumHeightRequest = minHeight,
            Margin = Thickness.Zero,
            FontSize = 14
        };

    public static Border CreateCard(params View[] children)
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        foreach (var child in children)
            stack.Children.Add(child);

        return new Border
        {
            BackgroundColor = SurfaceAlt,
            Stroke = Border,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Padding = 16,
            Content = stack
        };
    }

    public static Border CreateCompactCard(params View[] children)
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        foreach (var child in children)
            stack.Children.Add(child);

        return new Border
        {
            BackgroundColor = SurfaceAlt,
            Stroke = Border,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Padding = 10,
            Content = stack
        };
    }
}
