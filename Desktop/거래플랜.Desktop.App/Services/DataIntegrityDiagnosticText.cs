using System.Globalization;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

internal enum DiagnosticAmountScope { Both, Sales, Purchase, None }

internal readonly record struct DiagnosticMoney(decimal? Value, DiagnosticAmountScope Scope,
    bool RowExists = true, bool Currency = false)
{
    public override string ToString() => RowExists ? "비공개" : "행 없음";
}

// Preserve nonfinancial arguments and field boundaries; never redact digits in a rendered string.
internal readonly struct DiagnosticText
{
    private readonly string? _literal;
    private readonly FormattableString? _format;
    private readonly DiagnosticText[]? _parts;
    private readonly string? _separator;
    private DiagnosticText(string literal) { _literal = literal; }
    private DiagnosticText(FormattableString format) { _format = format; }
    private DiagnosticText(string separator, DiagnosticText[] parts) { _separator = separator; _parts = parts; }
    public static implicit operator DiagnosticText(string value) => new(value ?? string.Empty);
    internal static DiagnosticText Text(FormattableString value) => new(value);
    internal static DiagnosticText Join(string separator, IEnumerable<DiagnosticText> values) => new(separator, values.ToArray());
    internal string Render(bool sales, bool purchase) => _format is not null
        ? _format.ToString(new Formatter(sales, purchase))
        : _parts is not null ? string.Join(_separator, _parts.Select(p => p.Render(sales, purchase))) : _literal ?? string.Empty;
    public override string ToString() => Render(false, false);

    private sealed class Formatter(bool sales, bool purchase) : IFormatProvider, ICustomFormatter
    {
        public object? GetFormat(Type? type) => type == typeof(ICustomFormatter) ? this : null;
        public string Format(string? format, object? arg, IFormatProvider? provider)
        {
            if (arg is DiagnosticText text) return text.Render(sales, purchase);
            if (arg is DiagnosticMoney money)
            {
                if (!money.RowExists) return "행 없음";
                var allowed = money.Scope switch { DiagnosticAmountScope.Sales => sales, DiagnosticAmountScope.Purchase => purchase, DiagnosticAmountScope.None => false, _ => sales && purchase };
                return allowed && money.Value.HasValue
                    ? money.Value.Value.ToString(format ?? "N0", CultureInfo.CurrentCulture) + (money.Currency ? "원" : "") : "비공개";
            }
            return arg is IFormattable value ? value.ToString(format, CultureInfo.CurrentCulture) : arg?.ToString() ?? string.Empty;
        }
    }
    internal static DiagnosticAmountScope ForInvoice(VoucherType type) => type switch
    {
        VoucherType.Sales or VoucherType.Collection => DiagnosticAmountScope.Sales,
        VoucherType.Purchase or VoucherType.Procurement or VoucherType.Expense => DiagnosticAmountScope.Purchase,
        _ => DiagnosticAmountScope.Both
    };
    internal static DiagnosticAmountScope ForTransaction(SessionState session, 거래플랜.Desktop.App.Data.LocalTransaction row, VoucherType? type)
        => !FinancialAmountVisibility.CanViewTransaction(session, row, type) ? DiagnosticAmountScope.None
            : session.HasPermission(AppPermissionNames.AmountViewSales) ? DiagnosticAmountScope.Sales : DiagnosticAmountScope.Purchase;
}

public sealed partial class DataIntegrityIssueDetail
{
    private DiagnosticText _currentText, _expectedText, _messageText, _reviewText;
    private SessionState? _amountSession;
    private FinancialAmountVisibility.AccessKey _amountAccess;
    internal DiagnosticText CurrentText { init => _currentText = value; }
    internal DiagnosticText ExpectedText { init => _expectedText = value; }
    internal DiagnosticText MessageText { init => _messageText = value; }
    internal DiagnosticText ReviewText { init => _reviewText = value; }
    internal void ProtectAmounts(SessionState session, FinancialAmountVisibility.AccessKey access)
    {
        if (_amountSession is not null) return;
        _amountSession = session; _amountAccess = access;
    }
    private string Render(DiagnosticText text)
    {
        var current = _amountSession?.IsLoggedIn == true && FinancialAmountVisibility.CaptureAccess(_amountSession) == _amountAccess;
        return text.Render(current && _amountAccess.Sales, current && _amountAccess.Purchase);
    }
}
