using SalesMaster.Desktop.App.Data;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.Services;

public sealed class PeriodLedgerAggregationService
{
    private readonly LocalStateService _local;

    public PeriodLedgerAggregationService(LocalStateService local)
    {
        _local = local;
    }

    public async Task<PeriodLedgerBuildResult> BuildAsync(
        PeriodLedgerQuery query,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (query.To < query.From)
            throw new InvalidOperationException("조회 종료일은 시작일보다 빠를 수 없습니다.");

        progress?.Report("조회 중...");

        var customers = await _local.GetCustomersAsync(ct);
        var customerMap = customers.ToDictionary(c => c.Id, c => c, EqualityComparer<Guid>.Default);

        var effectiveCustomerId = query.Scope == PeriodLedgerScope.AllCustomers
            ? (Guid?)null
            : query.CustomerId;

        var invoices = await _local.GetInvoicesAsync(query.From, query.To, effectiveCustomerId, ct);
        var transactions = await _local.GetTransactionsAsync(query.From, query.To, effectiveCustomerId, ct);

        return query.LedgerType switch
        {
            PeriodLedgerType.ReceiptPayment => BuildPaymentLedgerResult(query, invoices, transactions, customerMap),
            _ => BuildBlockLedgerResult(query, invoices, customerMap)
        };
    }

    private static bool IsSalesOrPurchase(VoucherType voucherType)
        => voucherType is VoucherType.Sales or VoucherType.Purchase;

    private static string ResolveLedgerTitle(PeriodLedgerType type)
        => type switch
        {
            PeriodLedgerType.SalesPurchase => "기간내 판매+구매 거래원장",
            PeriodLedgerType.SalesOnly => "기간내 판매/매출 거래원장",
            PeriodLedgerType.PurchaseOnly => "기간내 구매/매입 거래원장",
            PeriodLedgerType.ReceiptPayment => "기간내 수금/지불 거래원장",
            _ => "거래원장"
        };

    private static string ResolveScopeLabel(PeriodLedgerScope scope)
        => scope == PeriodLedgerScope.AllCustomers
            ? "전체업체집계"
            : "개별업체집계";

    private PeriodLedgerBuildResult BuildBlockLedgerResult(
        PeriodLedgerQuery query,
        IReadOnlyList<LocalInvoice> invoices,
        IReadOnlyDictionary<Guid, LocalCustomer> customerMap)
    {
        var filtered = invoices
            .Where(i => IsMatchLedgerType(query.LedgerType, i.VoucherType))
            .ToList();

        var profitContext = query.IncludeProfit
            ? BuildProfitContext(invoices)
            : null;

        var rawByCustomer = new Dictionary<Guid, List<PeriodLedgerRawEvent>>();

        foreach (var invoice in filtered)
        {
            if (!customerMap.TryGetValue(invoice.CustomerId, out var customer))
                continue;

            var customerName = string.IsNullOrWhiteSpace(customer.NameOriginal)
                ? "(미지정 거래처)"
                : customer.NameOriginal.Trim();

            var lineRows = invoice.Lines
                .Where(l => !l.IsDeleted)
                .Select(ToItemRow)
                .ToList();

            var summary = BuildInvoiceSummary(invoice, lineRows.Count);
            var division = invoice.VoucherType == VoucherType.Purchase ? "구매" : "판매";
            var tradeAmount = invoice.VoucherType == VoucherType.Purchase
                ? -Math.Abs(invoice.TotalAmount)
                : Math.Abs(invoice.TotalAmount);

            decimal? profitAmount = null;
            if (query.IncludeProfit && invoice.VoucherType == VoucherType.Sales && profitContext is not null)
                profitAmount = TryCalculateProfit(invoice, lineRows, profitContext);

            AddRawEvent(rawByCustomer, new PeriodLedgerRawEvent
            {
                CustomerId = invoice.CustomerId,
                CustomerName = customerName,
                Date = invoice.InvoiceDate,
                Division = division,
                Summary = summary,
                TradeAmount = tradeAmount,
                ReceiptAmount = 0m,
                PaymentAmount = 0m,
                ProfitAmount = profitAmount,
                Note = invoice.Memo?.Trim() ?? string.Empty,
                IsInvoiceSummary = true,
                InvoiceId = invoice.Id,
                Items = lineRows
            });

            foreach (var payment in invoice.Payments.Where(p => !p.IsDeleted && p.Amount > 0))
            {
                var isPurchaseInvoice = invoice.VoucherType == VoucherType.Purchase;
                AddRawEvent(rawByCustomer, new PeriodLedgerRawEvent
                {
                    CustomerId = invoice.CustomerId,
                    CustomerName = customerName,
                    Date = payment.PaymentDate,
                    Division = isPurchaseInvoice ? "지불(매입전표)" : "수금(판매전표)",
                    Summary = string.IsNullOrWhiteSpace(payment.Note)
                        ? isPurchaseInvoice ? $"{summary} 지급" : $"{summary} 입금"
                        : payment.Note.Trim(),
                    TradeAmount = 0m,
                    ReceiptAmount = isPurchaseInvoice ? 0m : payment.Amount,
                    PaymentAmount = isPurchaseInvoice ? payment.Amount : 0m,
                    ProfitAmount = null,
                    Note = invoice.Memo?.Trim() ?? string.Empty,
                    IsInvoiceSummary = false,
                    InvoiceId = invoice.Id,
                    Items = []
                });
            }
        }

        var blocks = BuildCustomerBlocks(query, rawByCustomer);
        if (query.Scope == PeriodLedgerScope.SingleCustomer && query.CustomerId.HasValue && blocks.Count == 0)
        {
            var name = customerMap.TryGetValue(query.CustomerId.Value, out var c)
                ? c.NameOriginal
                : "(선택 거래처)";
            blocks.Add(new PeriodLedgerCustomerBlock
            {
                CustomerId = query.CustomerId.Value,
                CustomerName = name,
                Rows = [],
                Totals = new PeriodLedgerTotals(),
                LatestDate = null
            });
        }

        var totals = SummarizeBlockTotals(blocks, query.IncludeProfit);
        return new PeriodLedgerBuildResult
        {
            Query = query,
            Title = ResolveLedgerTitle(query.LedgerType),
            ScopeLabel = ResolveScopeLabel(query.Scope),
            Blocks = blocks,
            PaymentRows = [],
            Totals = totals,
            ProfitWarningMessage = query.IncludeProfit && profitContext?.MissingCostDataFound == true
                ? "일부 품목은 매입 데이터가 없어 순이익이 비어있습니다."
                : null
        };
    }

    private static bool IsMatchLedgerType(PeriodLedgerType type, VoucherType voucherType)
        => type switch
        {
            PeriodLedgerType.SalesPurchase => voucherType is VoucherType.Sales or VoucherType.Purchase,
            PeriodLedgerType.SalesOnly => voucherType == VoucherType.Sales,
            PeriodLedgerType.PurchaseOnly => voucherType == VoucherType.Purchase,
            _ => false
        };

    private static PeriodLedgerItemRow ToItemRow(LocalInvoiceLine line)
    {
        var vat = Math.Max(0m, line.LineAmount - Math.Round(line.LineAmount / 1.1m, 0, MidpointRounding.AwayFromZero));
        return new PeriodLedgerItemRow
        {
            ItemName = line.ItemNameOriginal?.Trim() ?? string.Empty,
            Specification = line.SpecificationOriginal?.Trim() ?? string.Empty,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            LineAmount = line.LineAmount,
            VatAmount = vat
        };
    }

    private static string BuildInvoiceSummary(LocalInvoice invoice, int lineCount)
    {
        if (lineCount <= 0)
            return string.IsNullOrWhiteSpace(invoice.Memo) ? "(품목 없음)" : invoice.Memo.Trim();

        var first = invoice.Lines.FirstOrDefault(l => !l.IsDeleted)?.ItemNameOriginal?.Trim();
        if (string.IsNullOrWhiteSpace(first))
            first = "(품목)";

        return lineCount == 1 ? first : $"{first} 외 {lineCount - 1}건";
    }

    private static void AddRawEvent(
        IDictionary<Guid, List<PeriodLedgerRawEvent>> byCustomer,
        PeriodLedgerRawEvent row)
    {
        if (!byCustomer.TryGetValue(row.CustomerId, out var list))
        {
            list = [];
            byCustomer[row.CustomerId] = list;
        }

        list.Add(row);
    }

    private static List<PeriodLedgerCustomerBlock> BuildCustomerBlocks(
        PeriodLedgerQuery query,
        IDictionary<Guid, List<PeriodLedgerRawEvent>> rawByCustomer)
    {
        var blocks = new List<PeriodLedgerCustomerBlock>();

        foreach (var pair in rawByCustomer)
        {
            var sorted = pair.Value
                .OrderByDescending(r => r.Date)
                .ThenBy(r => ResolveRowPriority(r.Division))
                .ThenBy(r => r.Summary, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var renderedRows = new List<PeriodLedgerRow>();
            decimal cumulativeTrade = 0m;
            decimal cumulativeReceipt = 0m;
            decimal cumulativePayment = 0m;
            decimal cumulativeSales = 0m;
            decimal cumulativeReceiptsAgainstSales = 0m;

            foreach (var row in sorted)
            {
                cumulativeTrade += row.TradeAmount;
                cumulativeReceipt += row.ReceiptAmount;
                cumulativePayment += row.PaymentAmount;

                if (row.Division == "판매" && row.TradeAmount > 0)
                    cumulativeSales += row.TradeAmount;
                if (row.ReceiptAmount > 0)
                    cumulativeReceiptsAgainstSales += row.ReceiptAmount;

                var running = cumulativeTrade - cumulativeReceipt + cumulativePayment;
                var receivable = cumulativeSales - cumulativeReceiptsAgainstSales;

                renderedRows.Add(new PeriodLedgerRow
                {
                    Date = row.Date,
                    Division = row.Division,
                    Summary = row.Summary,
                    TradeAmount = row.TradeAmount,
                    ReceiptAmount = row.ReceiptAmount,
                    PaymentAmount = row.PaymentAmount,
                    RunningBalance = running,
                    ReceivableBalance = receivable,
                    ProfitAmount = row.ProfitAmount,
                    Note = row.Note,
                    IsInvoiceSummary = row.IsInvoiceSummary,
                    IsSubTotal = false,
                    InvoiceId = row.InvoiceId,
                    SubTotalQuantity = null,
                    SubTotalAmount = null,
                    SubTotalVat = null,
                    Items = row.Items
                });

                if (row.IsInvoiceSummary && row.Items.Count > 0)
                {
                    renderedRows.Add(new PeriodLedgerRow
                    {
                        Date = row.Date,
                        Division = string.Empty,
                        Summary = "(소계)",
                        TradeAmount = 0m,
                        ReceiptAmount = 0m,
                        PaymentAmount = 0m,
                        RunningBalance = running,
                        ReceivableBalance = receivable,
                        ProfitAmount = null,
                        Note = string.Empty,
                        IsInvoiceSummary = false,
                        IsSubTotal = true,
                        InvoiceId = row.InvoiceId,
                        SubTotalQuantity = row.Items.Sum(i => i.Quantity),
                        SubTotalAmount = row.Items.Sum(i => i.LineAmount),
                        SubTotalVat = row.Items.Sum(i => i.VatAmount),
                        Items = []
                    });
                }
            }

            var blockTotals = new PeriodLedgerTotals
            {
                TradeAmount = renderedRows.Where(r => !r.IsSubTotal).Sum(r => r.TradeAmount),
                ReceiptAmount = renderedRows.Where(r => !r.IsSubTotal).Sum(r => r.ReceiptAmount),
                PaymentAmount = renderedRows.Where(r => !r.IsSubTotal).Sum(r => r.PaymentAmount),
                RunningBalance = renderedRows.LastOrDefault(r => !r.IsSubTotal)?.RunningBalance ?? 0m,
                ReceivableBalance = renderedRows.LastOrDefault(r => !r.IsSubTotal)?.ReceivableBalance ?? 0m,
                ProfitAmount = query.IncludeProfit
                    ? renderedRows.Where(r => !r.IsSubTotal).Sum(r => r.ProfitAmount ?? 0m)
                    : null
            };

            blocks.Add(new PeriodLedgerCustomerBlock
            {
                CustomerId = pair.Key,
                CustomerName = pair.Value.First().CustomerName,
                Rows = renderedRows,
                Totals = blockTotals,
                LatestDate = renderedRows.FirstOrDefault()?.Date
            });
        }

        var ordered = query.Scope == PeriodLedgerScope.AllCustomers && query.SortByCustomerName
            ? blocks.OrderBy(b => b.CustomerName, StringComparer.CurrentCultureIgnoreCase).ToList()
            : blocks
                .OrderByDescending(b => b.LatestDate ?? DateOnly.MinValue)
                .ThenBy(b => b.CustomerName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        return ordered;
    }

    private static int ResolveRowPriority(string division)
        => division switch
        {
            "판매" => 0,
            "구매" => 1,
            "수금(판매전표)" => 2,
            "수금" => 3,
            "지불" => 4,
            _ => 9
        };

    private static PeriodLedgerTotals SummarizeBlockTotals(
        IReadOnlyList<PeriodLedgerCustomerBlock> blocks,
        bool includeProfit)
    {
        if (blocks.Count == 0)
            return new PeriodLedgerTotals();

        var allRows = blocks.SelectMany(b => b.Rows).Where(r => !r.IsSubTotal).ToList();
        return new PeriodLedgerTotals
        {
            TradeAmount = allRows.Sum(r => r.TradeAmount),
            ReceiptAmount = allRows.Sum(r => r.ReceiptAmount),
            PaymentAmount = allRows.Sum(r => r.PaymentAmount),
            RunningBalance = allRows.Sum(r => r.TradeAmount) - allRows.Sum(r => r.ReceiptAmount) + allRows.Sum(r => r.PaymentAmount),
            ReceivableBalance = allRows.Where(r => r.Division == "판매").Sum(r => r.TradeAmount) - allRows.Sum(r => r.ReceiptAmount),
            ProfitAmount = includeProfit ? allRows.Sum(r => r.ProfitAmount ?? 0m) : null
        };
    }

    private static PeriodProfitContext BuildProfitContext(IReadOnlyList<LocalInvoice> invoices)
    {
        var purchaseLines = invoices
            .Where(i => i.VoucherType == VoucherType.Purchase)
            .SelectMany(i => i.Lines.Where(l => !l.IsDeleted && l.Quantity > 0))
            .ToList();

        var avg = purchaseLines
            .GroupBy(GetItemKey)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var qty = g.Sum(x => x.Quantity);
                    if (qty <= 0)
                        return 0m;

                    var amount = g.Sum(x => x.LineAmount);
                    return amount / qty;
                },
                StringComparer.OrdinalIgnoreCase);

        return new PeriodProfitContext
        {
            PurchaseAverageByItemKey = avg,
            MissingCostDataFound = false
        };
    }

    private static decimal? TryCalculateProfit(
        LocalInvoice invoice,
        IReadOnlyList<PeriodLedgerItemRow> lineRows,
        PeriodProfitContext ctx)
    {
        if (lineRows.Count == 0)
            return null;

        decimal cost = 0m;
        var missing = false;

        foreach (var line in invoice.Lines.Where(l => !l.IsDeleted))
        {
            var key = GetItemKey(line);
            if (!ctx.PurchaseAverageByItemKey.TryGetValue(key, out var avgUnit) || avgUnit <= 0)
            {
                missing = true;
                continue;
            }

            cost += line.Quantity * avgUnit;
        }

        if (missing)
        {
            ctx.MissingCostDataFound = true;
            return null;
        }

        return invoice.TotalAmount - cost;
    }

    private static string GetItemKey(LocalInvoiceLine line)
    {
        if (line.ItemId.HasValue)
            return line.ItemId.Value.ToString("N");

        return $"{line.ItemNameOriginal}|{line.SpecificationOriginal}".Trim().ToUpperInvariant();
    }

    private PeriodLedgerBuildResult BuildPaymentLedgerResult(
        PeriodLedgerQuery query,
        IReadOnlyList<LocalInvoice> invoices,
        IReadOnlyList<LocalTransaction> transactions,
        IReadOnlyDictionary<Guid, LocalCustomer> customerMap)
    {
        var allEvents = new List<PeriodPaymentEvent>();
        var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var invoice in invoices)
        {
            if (!customerMap.TryGetValue(invoice.CustomerId, out var customer))
                continue;

            var customerName = string.IsNullOrWhiteSpace(customer.NameOriginal)
                ? "(미지정 거래처)"
                : customer.NameOriginal.Trim();

            var summary = BuildInvoiceSummary(invoice, invoice.Lines.Count(l => !l.IsDeleted));

            foreach (var payment in invoice.Payments.Where(p => !p.IsDeleted && p.Amount > 0))
            {
                var key = BuildDedupKey(invoice.CustomerId, payment.PaymentDate, payment.Amount, "R", ParseMethod(payment.Note));
                if (!dedup.Add(key))
                    continue;

                allEvents.Add(new PeriodPaymentEvent
                {
                    CustomerId = invoice.CustomerId,
                    CustomerName = customerName,
                    Date = payment.PaymentDate,
                    Division = "수금(판매전표)",
                    Summary = summary,
                    TradeAmount = 0m,
                    ReceiptAmount = payment.Amount,
                    PaymentAmount = 0m,
                    Note = invoice.Memo?.Trim() ?? string.Empty,
                    DedupKey = key,
                    Priority = 1
                });
            }

            if (invoice.VoucherType == VoucherType.Collection)
            {
                var amount = Math.Abs(invoice.TotalAmount);
                if (amount <= 0)
                    continue;

                var key = BuildDedupKey(invoice.CustomerId, invoice.InvoiceDate, amount, "R", ParseMethod(invoice.Memo));
                if (!dedup.Add(key))
                    continue;

                allEvents.Add(new PeriodPaymentEvent
                {
                    CustomerId = invoice.CustomerId,
                    CustomerName = customerName,
                    Date = invoice.InvoiceDate,
                    Division = "수금",
                    Summary = summary,
                    TradeAmount = 0m,
                    ReceiptAmount = amount,
                    PaymentAmount = 0m,
                    Note = invoice.Memo?.Trim() ?? string.Empty,
                    DedupKey = key,
                    Priority = 2
                });
            }
        }

        foreach (var tx in transactions)
        {
            if (!customerMap.TryGetValue(tx.CustomerId, out var customer))
                continue;

            var customerName = string.IsNullOrWhiteSpace(customer.NameOriginal)
                ? "(미지정 거래처)"
                : customer.NameOriginal.Trim();

            if (tx.ReceiptTotal > 0)
            {
                var method = ResolveTransactionMethod(tx, receipt: true);
                var key = BuildDedupKey(tx.CustomerId, tx.TransactionDate, tx.ReceiptTotal, "R", method);
                if (dedup.Add(key))
                {
                    allEvents.Add(new PeriodPaymentEvent
                    {
                        CustomerId = tx.CustomerId,
                        CustomerName = customerName,
                        Date = tx.TransactionDate,
                        Division = "수금",
                        Summary = BuildTransactionSummary(tx),
                        TradeAmount = 0m,
                        ReceiptAmount = tx.ReceiptTotal,
                        PaymentAmount = 0m,
                        Note = tx.Memo?.Trim() ?? string.Empty,
                        DedupKey = key,
                        Priority = 3
                    });
                }
            }

            if (tx.PaymentTotal > 0)
            {
                var method = ResolveTransactionMethod(tx, receipt: false);
                var key = BuildDedupKey(tx.CustomerId, tx.TransactionDate, tx.PaymentTotal, "P", method);
                if (dedup.Add(key))
                {
                    allEvents.Add(new PeriodPaymentEvent
                    {
                        CustomerId = tx.CustomerId,
                        CustomerName = customerName,
                        Date = tx.TransactionDate,
                        Division = "지불",
                        Summary = BuildTransactionSummary(tx),
                        TradeAmount = 0m,
                        ReceiptAmount = 0m,
                        PaymentAmount = tx.PaymentTotal,
                        Note = tx.Memo?.Trim() ?? string.Empty,
                        DedupKey = key,
                        Priority = 3
                    });
                }
            }
        }

        var ordered = allEvents
            .OrderByDescending(e => e.Date)
            .ThenBy(e => e.Priority)
            .ThenBy(e => e.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var salesTotalsByCustomer = invoices
            .Where(i => i.VoucherType == VoucherType.Sales)
            .GroupBy(i => i.CustomerId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.TotalAmount));

        var runningByCustomer = new Dictionary<Guid, (decimal Trade, decimal Receipt, decimal Payment)>();
        var rows = new List<PeriodLedgerPaymentRow>();

        for (var i = 0; i < ordered.Count; i++)
        {
            var ev = ordered[i];
            runningByCustomer.TryGetValue(ev.CustomerId, out var running);

            running.Trade += ev.TradeAmount;
            running.Receipt += ev.ReceiptAmount;
            running.Payment += ev.PaymentAmount;
            runningByCustomer[ev.CustomerId] = running;

            salesTotalsByCustomer.TryGetValue(ev.CustomerId, out var periodSalesTotal);

            rows.Add(new PeriodLedgerPaymentRow
            {
                No = i + 1,
                Date = ev.Date,
                Division = ev.Division,
                Summary = ev.Summary,
                TradeAmount = ev.TradeAmount,
                ReceiptAmount = ev.ReceiptAmount,
                PaymentAmount = ev.PaymentAmount,
                RunningBalance = running.Trade - running.Receipt + running.Payment,
                ReceivableBalance = periodSalesTotal - running.Receipt,
                CustomerName = ev.CustomerName,
                Note = ev.Note
            });
        }

        var finalReceivableByCustomer = runningByCustomer.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                salesTotalsByCustomer.TryGetValue(pair.Key, out var totalSales);
                return totalSales - pair.Value.Receipt;
            });

        var totals = new PeriodLedgerTotals
        {
            TradeAmount = rows.Sum(r => r.TradeAmount),
            ReceiptAmount = rows.Sum(r => r.ReceiptAmount),
            PaymentAmount = rows.Sum(r => r.PaymentAmount),
            RunningBalance = rows.Sum(r => r.TradeAmount) - rows.Sum(r => r.ReceiptAmount) + rows.Sum(r => r.PaymentAmount),
            ReceivableBalance = finalReceivableByCustomer.Values.Sum(),
            ProfitAmount = null
        };

        return new PeriodLedgerBuildResult
        {
            Query = query,
            Title = ResolveLedgerTitle(query.LedgerType),
            ScopeLabel = ResolveScopeLabel(query.Scope),
            Blocks = [],
            PaymentRows = rows,
            Totals = totals,
            ProfitWarningMessage = null
        };
    }

    private static string BuildDedupKey(
        Guid customerId,
        DateOnly date,
        decimal amount,
        string direction,
        string method)
    {
        return $"{customerId:N}|{date:yyyyMMdd}|{amount:0.##}|{direction}|{method}";
    }

    private static string ParseMethod(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return string.Empty;

        var n = note.Trim();
        if (n.Contains("카드", StringComparison.OrdinalIgnoreCase)) return "카드";
        if (n.Contains("통장", StringComparison.OrdinalIgnoreCase) || n.Contains("계좌", StringComparison.OrdinalIgnoreCase)) return "통장";
        if (n.Contains("현금", StringComparison.OrdinalIgnoreCase)) return "현금";
        return n.Length > 24 ? n[..24] : n;
    }

    private static string ResolveTransactionMethod(LocalTransaction tx, bool receipt)
    {
        var tags = new List<string>();
        if (receipt)
        {
            if (tx.CashReceipt > 0) tags.Add("현금");
            if (tx.CardReceipt > 0) tags.Add("카드");
            if (tx.BankReceipt > 0) tags.Add("통장");
        }
        else
        {
            if (tx.CashPayment > 0) tags.Add("현금");
            if (tx.CardPayment > 0) tags.Add("카드");
            if (tx.BankPayment > 0) tags.Add("통장");
        }

        return string.Join("+", tags);
    }

    private static string BuildTransactionSummary(LocalTransaction tx)
    {
        if (!string.IsNullOrWhiteSpace(tx.Note))
            return tx.Note.Trim();
        if (!string.IsNullOrWhiteSpace(tx.Memo))
            return tx.Memo.Trim();
        return "수금/지불 전표";
    }
}
