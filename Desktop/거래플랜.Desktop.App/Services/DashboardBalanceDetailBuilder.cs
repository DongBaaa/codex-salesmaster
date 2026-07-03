using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public static class DashboardBalanceDetailBuilder
{
    public static IReadOnlyList<DashboardBalanceDetailRow> BuildRows(
        IEnumerable<LocalInvoiceListSummary> invoices,
        IReadOnlyDictionary<Guid, string> customerNameById,
        VoucherType voucherType)
    {
        var outstandingInvoices = invoices
            .Where(invoice => invoice.VoucherType == voucherType)
            .Select(invoice => new
            {
                Invoice = invoice,
                BalanceAmount = Math.Max(0m, invoice.TotalAmount - invoice.SettledAmount)
            })
            .Where(row => row.BalanceAmount > 0m)
            .ToList();

        var customerBalanceMap = outstandingInvoices
            .GroupBy(row => row.Invoice.CustomerId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.BalanceAmount));

        return outstandingInvoices
            .Select(row =>
            {
                var invoice = row.Invoice;
                var customerName = ResolveCustomerName(invoice.CustomerId, customerNameById);
                var invoiceNumber = !string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
                    ? invoice.InvoiceNumber.Trim()
                    : !string.IsNullOrWhiteSpace(invoice.LocalTempNumber)
                        ? invoice.LocalTempNumber.Trim()
                        : "(번호 없음)";

                return new DashboardBalanceDetailRow
                {
                    CustomerId = invoice.CustomerId,
                    CustomerName = customerName,
                    CustomerBalance = customerBalanceMap.TryGetValue(invoice.CustomerId, out var customerBalance)
                        ? customerBalance
                        : row.BalanceAmount,
                    InvoiceId = invoice.Id,
                    InvoiceNumberDisplay = invoiceNumber,
                    InvoiceDate = invoice.InvoiceDate,
                    VoucherType = invoice.VoucherType,
                    FirstItemSummary = string.IsNullOrWhiteSpace(invoice.FirstItemSummary)
                        ? "(품목 없음)"
                        : invoice.FirstItemSummary.Trim(),
                    TotalAmount = invoice.TotalAmount,
                    SettledAmount = invoice.SettledAmount,
                    BalanceAmount = row.BalanceAmount,
                    ResponsibleOfficeCode = invoice.ResponsibleOfficeCode,
                    Revision = invoice.Revision
                };
            })
            .OrderBy(row => row.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(row => row.InvoiceDate)
            .ThenBy(row => row.InvoiceNumberDisplay, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string ResolveCustomerName(Guid customerId, IReadOnlyDictionary<Guid, string> customerNameById)
    {
        if (customerId != Guid.Empty &&
            customerNameById.TryGetValue(customerId, out var name) &&
            !string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        return "(거래처 미지정)";
    }
}
