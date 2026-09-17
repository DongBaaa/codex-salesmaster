using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    public Func<bool> CreateInvoicePrintAuthorization(Guid invoiceId, Guid customerId, SessionState session, Func<bool>? preparedByOwner = null)
    {
        var sessionId = session.SessionId;
        var scopeEpoch = session.SyncScopeEpoch;
        // A separate read-only connection sees committed scope snapshots without sharing the sync DbContext.
        var connectionString = new SqliteConnectionStringBuilder(_db.Database.GetDbConnection().ConnectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 1
        }.ToString();
        bool SameOwner() => (preparedByOwner?.Invoke() ?? true) && session.IsLoggedIn && session.SessionId == sessionId && session.SyncScopeEpoch == scopeEpoch;
        return () =>
        {
            if (!SameOwner()) return false;
            using var readDb = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connectionString).Options);
            var local = new LocalStateService(readDb, _officeAccess, _syncRequestDispatcher, session, _timeProvider, _maximumOfflineGrace);
            var invoice = readDb.Invoices.IgnoreQueryFilters().AsNoTracking().SingleOrDefault(row => row.Id == invoiceId && !row.IsDeleted);
            if (invoice is null || invoice.CustomerId != customerId || !local.CanAccessInvoice(invoice, session)) return false;
            var customer = readDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleOrDefault(row => row.Id == customerId && !row.IsDeleted);
            return customer is not null && local.CanAccessCustomer(customer, session) && SameOwner();
        };
    }
}
