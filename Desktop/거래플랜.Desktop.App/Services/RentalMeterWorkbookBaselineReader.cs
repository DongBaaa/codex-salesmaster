using System.Data;
using System.Globalization;

namespace 거래플랜.Desktop.App.Services;

internal sealed record RentalMeterWorkbookBaselineCandidate(
    string ManagementNumber,
    DateOnly ReadingDate,
    long BlackMeter,
    long ColorMeter,
    string SheetName);

internal sealed class RentalMeterWorkbookBaselineReadResult
{
    public List<RentalMeterWorkbookBaselineCandidate> Candidates { get; } = new();
    public List<string> Messages { get; } = new();
}

internal static class RentalMeterWorkbookBaselineReader
{
    private static readonly string[] ClosingDateLabels =
    [
        "작성일",
        "작성일자",
        "청구일",
        "청구일자",
        "마감일"
    ];

    public static RentalMeterWorkbookBaselineReadResult Read(DataSet workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);

        var result = new RentalMeterWorkbookBaselineReadResult();
        var workbookClosingDate = workbook.Tables
            .Cast<DataTable>()
            .Select(FindLabeledDate)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .OrderByDescending(value => value)
            .FirstOrDefault();

        var parsed = new List<RentalMeterWorkbookBaselineCandidate>();
        foreach (var table in workbook.Tables.Cast<DataTable>())
        {
            var managementNumber = FindManagementNumber(table);
            if (string.IsNullOrWhiteSpace(managementNumber))
                continue;

            var readingDate = FindLabeledDate(table) ??
                              (workbookClosingDate == default ? null : workbookClosingDate);
            if (!readingDate.HasValue)
            {
                result.Messages.Add($"{table.TableName}: 확정 마감일을 찾지 못해 건너뛰었습니다.");
                continue;
            }

            if (!TryReadCurrentA4Meters(table, out var blackMeter, out var colorMeter, out var error))
            {
                result.Messages.Add($"{table.TableName}({managementNumber}): {error}");
                continue;
            }

            parsed.Add(new RentalMeterWorkbookBaselineCandidate(
                managementNumber.Trim(),
                readingDate.Value,
                blackMeter,
                colorMeter,
                table.TableName));
        }

        foreach (var group in parsed.GroupBy(
                     candidate => NormalizeIdentifier(candidate.ManagementNumber),
                     StringComparer.Ordinal))
        {
            var latestDate = group.Max(candidate => candidate.ReadingDate);
            var latest = group.Where(candidate => candidate.ReadingDate == latestDate).ToList();
            var distinctValues = latest
                .Select(candidate => (candidate.BlackMeter, candidate.ColorMeter))
                .Distinct()
                .ToList();
            if (distinctValues.Count != 1)
            {
                result.Messages.Add(
                    $"관리번호 {latest[0].ManagementNumber}: 같은 마감일의 검침값이 서로 달라 자동 반영하지 않았습니다.");
                continue;
            }

            result.Candidates.Add(latest
                .OrderBy(candidate => candidate.SheetName, StringComparer.CurrentCultureIgnoreCase)
                .First());
        }

        result.Candidates.Sort((left, right) => string.Compare(
            left.ManagementNumber,
            right.ManagementNumber,
            StringComparison.CurrentCultureIgnoreCase));
        if (result.Candidates.Count == 0 && result.Messages.Count == 0)
            result.Messages.Add("관리번호와 현재 검침값이 있는 장비 시트를 찾지 못했습니다.");

        return result;
    }

    private static string FindManagementNumber(DataTable table)
    {
        var maxRows = Math.Min(table.Rows.Count, 12);
        for (var rowIndex = 0; rowIndex < maxRows; rowIndex++)
        {
            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                var label = NormalizeLabel(GetCellText(table, rowIndex, columnIndex));
                if (!string.Equals(label, "관리번호", StringComparison.Ordinal))
                    continue;

                for (var valueColumn = columnIndex + 1;
                     valueColumn < Math.Min(table.Columns.Count, columnIndex + 4);
                     valueColumn++)
                {
                    var value = GetCellText(table, rowIndex, valueColumn).Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
        }

        return string.Empty;
    }

    private static DateOnly? FindLabeledDate(DataTable table)
    {
        var maxRows = Math.Min(table.Rows.Count, 12);
        for (var rowIndex = 0; rowIndex < maxRows; rowIndex++)
        {
            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                var label = NormalizeLabel(GetCellText(table, rowIndex, columnIndex));
                if (!ClosingDateLabels.Contains(label, StringComparer.Ordinal))
                    continue;

                for (var valueColumn = columnIndex + 1;
                     valueColumn < Math.Min(table.Columns.Count, columnIndex + 6);
                     valueColumn++)
                {
                    if (TryReadDate(table.Rows[rowIndex][valueColumn], out var parsed))
                        return parsed;
                }
            }
        }

        return null;
    }

    private static bool TryReadCurrentA4Meters(
        DataTable table,
        out long blackMeter,
        out long colorMeter,
        out string error)
    {
        blackMeter = 0;
        colorMeter = 0;
        error = string.Empty;

        var counterRow = FindCurrentCounterRow(table);
        if (counterRow < 0 || table.Columns.Count < 4)
        {
            error = "현재 검침값 행을 찾지 못해 건너뛰었습니다.";
            return false;
        }

        var values = new long[8];
        var populated = false;
        for (var offset = 0; offset < values.Length; offset++)
        {
            if (offset + 1 >= table.Columns.Count)
                continue;
            var cell = table.Rows[counterRow][offset + 1];
            if (cell is null || cell == DBNull.Value || string.IsNullOrWhiteSpace(Convert.ToString(cell, CultureInfo.CurrentCulture)))
                continue;

            populated = true;
            if (!TryReadNonNegativeWholeNumber(cell, out values[offset]))
            {
                error = $"현재 검침값에 0 이상의 정수가 아닌 값이 있습니다(열 {offset + 2}).";
                return false;
            }
        }

        if (!populated)
        {
            error = "현재 검침값이 모두 비어 있습니다.";
            return false;
        }

        try
        {
            blackMeter = checked(values[0] + 2 * values[1] + 2 * values[4] + 4 * values[5]);
            colorMeter = checked(values[2] + 2 * values[3] + 2 * values[6] + 4 * values[7]);
            return true;
        }
        catch (OverflowException)
        {
            error = "A4 환산 검침값이 허용 범위를 초과합니다.";
            return false;
        }
    }

    private static int FindCurrentCounterRow(DataTable table)
    {
        // 기존 임대카운터 원본은 18행(B:I)에 당월 마감 검침값을 둡니다.
        var availableCounterColumns = Math.Min(8, Math.Max(0, table.Columns.Count - 1));
        if (table.Rows.Count > 17 && availableCounterColumns >= 3 &&
            Enumerable.Range(1, availableCounterColumns).Any(column => HasValue(table.Rows[17][column])))
        {
            return 17;
        }

        var candidates = new List<int>();
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            if (availableCounterColumns < 3)
                break;

            var firstCell = NormalizeLabel(GetCellText(table, rowIndex, 0));
            if (!firstCell.Contains("카운트", StringComparison.Ordinal))
                continue;
            if (Enumerable.Range(1, availableCounterColumns).Any(column => HasValue(table.Rows[rowIndex][column])))
                candidates.Add(rowIndex);
        }

        return candidates.Count == 0 ? -1 : candidates[^1];
    }

    private static bool TryReadDate(object? value, out DateOnly parsed)
    {
        switch (value)
        {
            case DateTime dateTime:
                parsed = DateOnly.FromDateTime(dateTime);
                return true;
            case DateOnly dateOnly:
                parsed = dateOnly;
                return true;
            case double number when number is > 0 and < 2_958_466:
                parsed = DateOnly.FromDateTime(DateTime.FromOADate(number));
                return true;
        }

        var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var currentCultureDate) ||
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out currentCultureDate))
        {
            parsed = DateOnly.FromDateTime(currentCultureDate);
            return true;
        }

        parsed = default;
        return false;
    }

    private static bool TryReadNonNegativeWholeNumber(object? value, out long parsed)
    {
        var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
        text = text.Replace(",", string.Empty, StringComparison.Ordinal);
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) &&
            number >= 0 &&
            number <= long.MaxValue &&
            decimal.Truncate(number) == number)
        {
            parsed = decimal.ToInt64(number);
            return true;
        }

        parsed = 0;
        return false;
    }

    private static string GetCellText(DataTable table, int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || rowIndex >= table.Rows.Count || columnIndex < 0 || columnIndex >= table.Columns.Count)
            return string.Empty;
        return Convert.ToString(table.Rows[rowIndex][columnIndex], CultureInfo.CurrentCulture) ?? string.Empty;
    }

    private static bool HasValue(object? value)
        => value is not null && value != DBNull.Value &&
           !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.CurrentCulture));

    private static string NormalizeLabel(string? value)
        => string.Concat((value ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character) && character != ':'));

    internal static string NormalizeIdentifier(string? value)
        => string.Concat((value ?? string.Empty)
                .Trim()
                .Where(char.IsLetterOrDigit))
            .ToUpperInvariant();
}
