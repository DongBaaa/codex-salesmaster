using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    public async Task<List<PaymentHistoryRow>> GetPaymentHistoryAsync(Guid customerId, SessionState session,
        CancellationToken ct = default)
    {
        var access = FinancialAmountVisibility.CaptureAccess(session);
        var transactions = await ApplyTransactionScope(_db.Transactions.AsNoTracking()
            .Where(row => row.CustomerId == customerId), session).ToListAsync(ct);
        var types = new Dictionary<Guid, VoucherType>();
        foreach (var ids in transactions.Select(row => row.LinkedInvoiceId).OfType<Guid>()
                     .Where(id => id != Guid.Empty).Distinct().Chunk(500))
            foreach (var row in await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                         .Where(invoice => ids.Contains(invoice.Id)).Select(invoice => new { invoice.Id, invoice.VoucherType }).ToListAsync(ct))
                types[row.Id] = row.VoucherType;
        if (access != FinancialAmountVisibility.CaptureAccess(session)) return [];
        return transactions.OrderByDescending(row => row.TransactionDate).ThenByDescending(row => row.UpdatedAtUtc)
            .Select(row =>
            {
                var visible = FinancialAmountVisibility.CanViewTransaction(session, row,
                    row.LinkedInvoiceId is Guid id && types.TryGetValue(id, out var type) ? type : null);
                // Preserve historical display labels only after evaluating the stored kind.
                return PaymentHistoryRow.From(NormalizeLinkedPaymentTransactionsForDisplay([row])[0], visible);
            }).ToList();
    }

    public async Task<LocalTransaction?> GetTransactionForPaymentEditingAsync(Guid id, SessionState session,
        CancellationToken ct = default)
    {
        var access = FinancialAmountVisibility.CaptureAccess(session);
        var transaction = await ApplyTransactionScope(_db.Transactions.AsNoTracking()
            .Where(row => row.Id == id), session).FirstOrDefaultAsync(ct);
        if (transaction is null || !CanEditPayments(session) ||
            !await CanViewTransactionAmountsAsync(transaction, session, ct) ||
            access != FinancialAmountVisibility.CaptureAccess(session)) return null;
        return NormalizeLinkedPaymentTransactionsForDisplay([transaction])[0];
    }

    private async Task<bool> CanViewTransactionAmountsAsync(LocalTransaction transaction, SessionState session, CancellationToken ct)
    {
        if (transaction.AmountsHidden) return false;
        VoucherType? type = transaction.LinkedInvoiceId is Guid id && id != Guid.Empty
            ? await _db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(invoice => invoice.Id == id)
                .Select(invoice => (VoucherType?)invoice.VoucherType).FirstOrDefaultAsync(ct)
            : null;
        return FinancialAmountVisibility.CanViewTransaction(session, transaction, type);
    }
}
