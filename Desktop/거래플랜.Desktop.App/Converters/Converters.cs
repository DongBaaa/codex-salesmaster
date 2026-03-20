using System.Globalization;
using System.Windows;
using System.Windows.Data;
using 거래플랜.Shared.Contracts;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Converters;

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
    {
        if (value is not decimal d)
            return string.Empty;

        if (string.Equals(parameter as string, "emptyWhenZero", StringComparison.OrdinalIgnoreCase) && d == 0m)
            return string.Empty;

        return d.ToString("N0");
    }

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

[ValueConversion(typeof(DateOnly?), typeof(DateTime?))]
public sealed class NullableDateOnlyConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateOnly d ? new DateTime(d.Year, d.Month, d.Day) : (DateTime?)null;

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTime dt ? DateOnly.FromDateTime(dt) : null;
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

[ValueConversion(typeof(string), typeof(string))]
public sealed class WarehouseCodeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var code = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(code))
            return string.Empty;

        return code.ToUpperInvariant() switch
        {
            var warehouse when warehouse == DomainConstants.WarehouseUsenetMain => DomainConstants.OfficeUsenet,
            var warehouse when warehouse == DomainConstants.WarehouseItworldMain => DomainConstants.OfficeItworld,
            var warehouse when warehouse == DomainConstants.WarehouseYeonsuMain => DomainConstants.OfficeYeonsu,
            _ => code
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() ?? string.Empty;
}
