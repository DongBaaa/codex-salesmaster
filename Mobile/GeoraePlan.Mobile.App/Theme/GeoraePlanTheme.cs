using Microsoft.Maui.Controls.Shapes;

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
            CancelButtonColor = Accent
        };

    public static Entry CreateEntry(string placeholder, bool isPassword = false)
        => new()
        {
            Placeholder = placeholder,
            IsPassword = isPassword,
            BackgroundColor = InputBackground,
            TextColor = Colors.Black,
            PlaceholderColor = Colors.Gray,
            ClearButtonVisibility = ClearButtonVisibility.WhileEditing
        };

    public static Entry CreateCompactEntry(string placeholder, bool isPassword = false)
    {
        var entry = CreateEntry(placeholder, isPassword);
        entry.HeightRequest = 36;
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
            TitleColor = Colors.Gray
        };

    public static Picker CreateCompactPicker(string title)
    {
        var picker = CreatePicker(title);
        picker.HeightRequest = 36;
        picker.Margin = Thickness.Zero;
        picker.FontSize = 14;
        return picker;
    }

    public static Button CreateButton(string text, Color backgroundColor)
        => new()
        {
            Text = text,
            BackgroundColor = backgroundColor,
            TextColor = Colors.White,
            FontAttributes = FontAttributes.Bold,
            CornerRadius = 10,
            HeightRequest = 44
        };

    public static Button CreateCompactButton(string text, Color backgroundColor)
    {
        var button = CreateButton(text, backgroundColor);
        button.HeightRequest = 36;
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
            FontAttributes = FontAttributes.Bold
        };

    public static Label CreateBodyText(string text, bool muted = true, double fontSize = 13)
        => new()
        {
            Text = text,
            TextColor = muted ? TextSecondary : TextPrimary,
            FontSize = fontSize
        };

    public static Label CreateFieldLabel(string text)
        => new()
        {
            Text = text,
            TextColor = TextSecondary,
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            Margin = Thickness.Zero
        };

    public static Label CreateStatusLabel()
        => new()
        {
            TextColor = TextSecondary,
            FontSize = 12
        };

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


