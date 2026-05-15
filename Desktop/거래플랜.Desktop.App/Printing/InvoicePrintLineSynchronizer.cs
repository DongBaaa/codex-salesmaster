using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Printing;

public static class InvoicePrintLineSynchronizer
{
    public static void AlignToInvoiceLineOrder(InvoicePrintModel target, InvoicePrintModel currentDefault)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(currentDefault);

        target.Lines = AlignToInvoiceLineOrder(target.Lines, currentDefault.Lines);
    }

    public static List<InvoicePrintLineModel> AlignToInvoiceLineOrder(
        IReadOnlyList<InvoicePrintLineModel>? savedLines,
        IReadOnlyList<InvoicePrintLineModel>? currentLines)
    {
        var current = currentLines?.Where(IsMeaningful).ToList() ?? new List<InvoicePrintLineModel>();
        if (current.Count == 0)
            return Renumber(savedLines?.Where(IsMeaningful).Select(CloneLine).ToList() ?? new List<InvoicePrintLineModel>());

        var saved = savedLines?.Where(IsMeaningful).Select(CloneLine).ToList() ?? new List<InvoicePrintLineModel>();
        if (saved.Count == 0)
            return Renumber(current.Select(CloneLine).ToList());

        var used = new bool[saved.Count];
        var ordered = new List<InvoicePrintLineModel>(current.Count);

        foreach (var currentLine in current)
        {
            var matchIndex = FindSourceLineMatch(saved, used, currentLine);
            if (matchIndex < 0)
                matchIndex = FindContentMatch(saved, used, currentLine);

            if (matchIndex >= 0)
            {
                used[matchIndex] = true;
                var matched = CloneLine(saved[matchIndex]);
                matched.SourceLineId = currentLine.SourceLineId ?? matched.SourceLineId;
                ordered.Add(matched);
            }
            else
            {
                ordered.Add(CloneLine(currentLine));
            }
        }

        return Renumber(ordered);
    }

    public static InvoicePrintLineModel FromInvoiceLine(LocalInvoiceLine line, int no)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new InvoicePrintLineModel
        {
            SourceLineId = line.Id,
            No = no,
            ItemName = line.ItemNameOriginal ?? string.Empty,
            Specification = line.SpecificationOriginal ?? string.Empty,
            Unit = line.Unit ?? string.Empty,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            Amount = line.LineAmount,
            Remark = line.Remark ?? string.Empty
        };
    }

    public static InvoicePrintLineModel CloneLine(InvoicePrintLineModel line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new InvoicePrintLineModel
        {
            SourceLineId = line.SourceLineId,
            No = line.No,
            ItemName = line.ItemName ?? string.Empty,
            Specification = line.Specification ?? string.Empty,
            Unit = line.Unit ?? string.Empty,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            Amount = line.Amount,
            Remark = line.Remark ?? string.Empty
        };
    }

    public static decimal ResolvePrintedSupplyUnitPrice(
        decimal unitPrice,
        decimal quantity,
        decimal lineAmount,
        string? vatMode)
    {
        if (InvoiceVatModes.IsNone(vatMode))
            return unitPrice;

        if (quantity != 0m && lineAmount != 0m)
        {
            var lineSupplyAmount = ResolvePrintedSupplyAmount(lineAmount, vatMode);
            return Math.Round(lineSupplyAmount / quantity, 0, MidpointRounding.AwayFromZero);
        }

        return InvoiceVatModes.SplitLineAmount(unitPrice, vatMode).SupplyAmount;
    }

    public static decimal ResolvePrintedSupplyAmount(decimal lineAmount, string? vatMode)
        => InvoiceVatModes.SplitLineAmount(lineAmount, vatMode).SupplyAmount;

    public static decimal ResolvePrintedVatAmount(decimal lineAmount, string? vatMode)
        => InvoiceVatModes.SplitLineAmount(lineAmount, vatMode).VatAmount;

    private static int FindSourceLineMatch(
        IReadOnlyList<InvoicePrintLineModel> saved,
        IReadOnlyList<bool> used,
        InvoicePrintLineModel currentLine)
    {
        var currentSourceLineId = currentLine.SourceLineId.GetValueOrDefault();
        if (currentSourceLineId == Guid.Empty)
            return -1;

        for (var i = 0; i < saved.Count; i++)
        {
            var savedSourceLineId = saved[i].SourceLineId.GetValueOrDefault();
            if (!used[i] &&
                savedSourceLineId != Guid.Empty &&
                savedSourceLineId == currentSourceLineId)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindContentMatch(
        IReadOnlyList<InvoicePrintLineModel> saved,
        IReadOnlyList<bool> used,
        InvoicePrintLineModel currentLine)
    {
        for (var i = 0; i < saved.Count; i++)
        {
            if (!used[i] && HasSameBusinessContent(saved[i], currentLine))
                return i;
        }

        return -1;
    }

    private static bool HasSameBusinessContent(InvoicePrintLineModel left, InvoicePrintLineModel right)
        => StringEquals(left.ItemName, right.ItemName) &&
           StringEquals(left.Specification, right.Specification) &&
           StringEquals(left.Unit, right.Unit) &&
           left.Quantity == right.Quantity &&
           left.UnitPrice == right.UnitPrice &&
           left.Amount == right.Amount &&
           StringEquals(left.Remark, right.Remark);

    private static bool StringEquals(string? left, string? right)
        => string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.CurrentCultureIgnoreCase);

    private static bool IsMeaningful(InvoicePrintLineModel line)
        => !string.IsNullOrWhiteSpace(line.ItemName) ||
           !string.IsNullOrWhiteSpace(line.Specification) ||
           line.Quantity != 0 ||
           line.UnitPrice != 0 ||
           line.Amount != 0 ||
           !string.IsNullOrWhiteSpace(line.Remark);

    private static List<InvoicePrintLineModel> Renumber(List<InvoicePrintLineModel> lines)
    {
        for (var i = 0; i < lines.Count; i++)
            lines[i].No = i + 1;

        return lines;
    }
}
