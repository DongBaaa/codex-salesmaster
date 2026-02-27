using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

[ValueConversion(typeof(decimal), typeof(string))]
public sealed class DecimalToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is decimal d ? d.ToString("N0") : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => decimal.TryParse(value?.ToString()?.Replace(",", ""), out var d) ? d : 0m;
}

[ValueConversion(typeof(DateOnly), typeof(DateTime?))]
public sealed class DateOnlyConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateOnly d ? new DateTime(d.Year, d.Month, d.Day) : (DateTime?)null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTime dt ? DateOnly.FromDateTime(dt) : DateOnly.FromDateTime(DateTime.Today);
}

[ValueConversion(typeof(VoucherType), typeof(string))]
public sealed class VoucherTypeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is VoucherType vt ? vt switch
        {
            VoucherType.Sales       => "매출",
            VoucherType.Purchase    => "매입",
            VoucherType.Procurement => "발주",
            VoucherType.Expense     => "경비",
            VoucherType.Collection  => "수금",
            _                       => value.ToString()!
        } : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
