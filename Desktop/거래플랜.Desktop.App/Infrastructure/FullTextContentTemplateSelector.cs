using System.Windows;
using System.Windows.Controls;

namespace 거래플랜.Desktop.App.Infrastructure;

public sealed class FullTextContentTemplateSelector : DataTemplateSelector
{
    public DataTemplate? StringTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
        => item is string ? StringTemplate : null;
}
