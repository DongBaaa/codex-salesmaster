using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

// Project untracked aggregates once, before either the screen or workbook can consume them.
internal static class PeriodLedgerAmountProjection
{
    internal static PeriodLedgerBuildResult Apply(PeriodLedgerBuildResult result, SessionState session,
        IReadOnlyList<LocalInvoice> invoices, IReadOnlyList<LocalTransaction> transactions,
        IReadOnlyList<LocalInvoice>? chartInvoices = null)
    {
        var invoiceMap = invoices.ToDictionary(i => i.Id);
        var transactionMap = transactions.Where(t => !t.IsDeleted).ToDictionary(t => t.Id);
        bool InvoiceVisible(LocalInvoice i) => !i.AmountsHidden &&
            !i.Lines.Any(l => !l.IsDeleted && l.AmountsHidden) &&
            FinancialAmountVisibility.CanViewInvoice(session, i.VoucherType);
        bool TransactionVisible(LocalTransaction t) => FinancialAmountVisibility.CanViewTransaction(session, t,
            t.LinkedInvoiceId is Guid id && invoiceMap.TryGetValue(id, out var i) ? i.VoucherType : null) &&
            (t.LinkedInvoiceId is not Guid linked || linked == Guid.Empty ||
             invoiceMap.TryGetValue(linked, out var parent) && InvoiceVisible(parent));
        bool RowVisible(Guid? invoiceId, Guid? paymentId, Guid? transactionId)
        {
            if (invoiceId is Guid iid && (!invoiceMap.TryGetValue(iid, out var invoice) || !InvoiceVisible(invoice))) return false;
            if (paymentId is Guid pid && (!invoiceId.HasValue ||
                !invoiceMap[invoiceId.Value].Payments.Any(p => p.Id == pid && !p.IsDeleted && !p.AmountsHidden))) return false;
            if (transactionId is Guid tid && (!transactionMap.TryGetValue(tid, out var tx) || !TransactionVisible(tx))) return false;
            return invoiceId.HasValue || transactionId.HasValue;
        }
        var sales = session.HasPermission(AppPermissionNames.AmountViewSales);
        var purchase = session.HasPermission(AppPermissionNames.AmountViewPurchase);
        var profitHidden = !sales || !purchase || invoices.Any(i =>
            i.VoucherType is VoucherType.Sales or VoucherType.Purchase && !InvoiceVisible(i));
        var blocks = result.Blocks.Select(block =>
        {
            var runningKnown = true;
            var rows = block.Rows.Select(row =>
            {
                var known = RowVisible(row.InvoiceId, row.PaymentId, row.TransactionId);
                runningKnown &= known;
                return row with
                {
                    TradeAmount = known ? row.TradeAmount : null,
                    ReceiptAmount = known ? row.ReceiptAmount : null,
                    PaymentAmount = known ? row.PaymentAmount : null,
                    RunningBalance = runningKnown ? row.RunningBalance : null,
                    ReceivableBalance = runningKnown && sales ? row.ReceivableBalance : null,
                    ProfitAmount = !profitHidden && known ? row.ProfitAmount : null,
                    SubTotalAmount = known ? row.SubTotalAmount : null,
                    SubTotalVat = known ? row.SubTotalVat : null,
                    Items = row.Items.Select(item => known ? item : item with
                    { UnitPrice = null, LineAmount = null, VatAmount = null }).ToList()
                };
            }).ToList();
            return block with { Rows = rows, Totals = MaskTotals(block.Totals, !runningKnown, profitHidden, sales) };
        }).ToList();

        // Receipt ledger balances depend on sales outside the displayed receipt rows as well.
        var hiddenBalanceCustomers = invoices.Where(i => i.VoucherType == VoucherType.Sales && !InvoiceVisible(i))
            .Select(i => i.CustomerId).Concat(result.PaymentRows.Where(r => !RowVisible(r.InvoiceId, r.PaymentId, r.TransactionId))
                .Select(r => r.CustomerId)).ToHashSet();
        var paymentBalanceKnown = sales && hiddenBalanceCustomers.Count == 0;
        var payments = result.PaymentRows.Select(row =>
        {
            var known = RowVisible(row.InvoiceId, row.PaymentId, row.TransactionId);
            var balanceKnown = sales && !hiddenBalanceCustomers.Contains(row.CustomerId);
            return row with
            {
                TradeAmount = known ? row.TradeAmount : null,
                ReceiptAmount = known ? row.ReceiptAmount : null,
                PaymentAmount = known ? row.PaymentAmount : null,
                RunningBalance = balanceKnown ? row.RunningBalance : null,
                ReceivableBalance = balanceKnown ? row.ReceivableBalance : null
            };
        }).ToList();
        var deliveries = result.YeonsuDeliveryRows.Select(row => row with
        { TotalAmount = RowVisible(row.InvoiceId, null, null) ? row.TotalAmount : null }).ToList();

        var chartSource = chartInvoices ?? invoices;
        var hiddenMonths = chartSource.Where(i => i.VoucherType == VoucherType.Sales && !InvoiceVisible(i))
            .Select(i => new DateOnly(i.InvoiceDate.Year, i.InvoiceDate.Month, 1)).ToHashSet();
        var chart = result.MonthlySalesChartPoints.Select(point =>
        {
            var known = sales && !hiddenMonths.Contains(point.Month);
            return point with
            {
                SalesAmount = known ? point.SalesAmount : null,
                SalesAmountText = known ? point.SalesAmountText : "비공개",
                // Relative heights also reveal the largest month's amount.
                BarHeight = sales && hiddenMonths.Count == 0 ? point.BarHeight : 0
            };
        }).ToList();
        var missingGrant = result.Query.LedgerType switch
        {
            PeriodLedgerType.SalesOnly or PeriodLedgerType.YeonsuDelivery => !sales,
            PeriodLedgerType.PurchaseOnly => !purchase,
            _ => !sales || !purchase
        };
        var anyHidden = missingGrant || blocks.Any(b => b.Rows.Any(r => !r.TradeAmount.HasValue)) ||
            payments.Any(r => !r.TradeAmount.HasValue) || deliveries.Any(r => !r.TotalAmount.HasValue);
        var totals = MaskTotals(result.Totals, anyHidden, profitHidden, sales);
        if (result.Query.LedgerType == PeriodLedgerType.ReceiptPayment && !paymentBalanceKnown)
            totals = totals with { RunningBalance = null, ReceivableBalance = null };
        return result with { Blocks = blocks, PaymentRows = payments, YeonsuDeliveryRows = deliveries,
            MonthlySalesChartPoints = chart, Totals = totals, ProfitAmountsHidden = profitHidden,
            ProfitWarningMessage = profitHidden && result.Query.IncludeProfit ? "순이익 금액은 비공개입니다." : result.ProfitWarningMessage };
    }

    private static PeriodLedgerTotals MaskTotals(PeriodLedgerTotals t, bool hidden, bool profitHidden, bool sales)
        => t with { TradeAmount = hidden ? null : t.TradeAmount, ReceiptAmount = hidden ? null : t.ReceiptAmount,
            PaymentAmount = hidden ? null : t.PaymentAmount, RunningBalance = hidden ? null : t.RunningBalance,
            ReceivableBalance = hidden || !sales ? null : t.ReceivableBalance,
            ProfitAmount = hidden || profitHidden ? null : t.ProfitAmount };
}
