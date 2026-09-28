using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    internal async Task<List<LocalTransaction>> GetPeriodLedgerTransactionsAsync(DateOnly from, DateOnly to,
        Guid? customerId, SessionState session, CancellationToken ct)
    {
        // Preserve the original direction for amount authorization; display normalization infers it from money.
        var query = ApplyTransactionScope(_db.Transactions.AsNoTracking(), session)
            .Where(t => t.TransactionDate >= from && t.TransactionDate <= to);
        if (customerId.HasValue) query = query.Where(t => t.CustomerId == customerId.Value);
        return await query.OrderByDescending(t => t.TransactionDate).ThenByDescending(t => t.CreatedAtUtc).ToListAsync(ct);
    }
}
