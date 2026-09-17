using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InvoicePrintCustomerScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedRevokedCustomer_RemainsPreservedButCurrentSessionCannotReadIt(bool dirty)
    {
        var root = Path.Combine(Path.GetTempPath(), "trade-print-customer-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite("Data Source=" + Path.Combine(root, "scope.db")).Options;
        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var session = CreateSession();
            var customer = new LocalCustomer
            {
                Id = Guid.NewGuid(), NameOriginal = "PRIVATE-CUSTOMER", NameMatchKey = "PRIVATE-CUSTOMER",
                BusinessNumber = "PRIVATE-BUSINESS-NUMBER", Phone = "PRIVATE-PHONE", Address = "PRIVATE-ADDRESS",
                TenantCode = session.TenantCode, OfficeCode = session.OfficeCode, ResponsibleOfficeCode = session.OfficeCode,
                Revision = 5, IsDirty = dirty, Notes = "preserve unsynchronized edits"
            };
            var invoice = new LocalInvoice
            {
                Id = Guid.NewGuid(), CustomerId = customer.Id, TenantCode = session.TenantCode,
                OfficeCode = session.OfficeCode, ResponsibleOfficeCode = session.OfficeCode,
                VersionGroupId = Guid.NewGuid(), IsLatestVersion = true, InvoiceDate = new DateOnly(2026, 9, 8),
                InvoiceNumber = "PRINT-SCOPE-ONLY", VoucherType = VoucherType.Sales,
                SupplyAmount = 1000m, VatAmount = 100m, TotalAmount = 1100m, Revision = 5, IsDirty = false
            };
            db.Customers.Add(customer); db.Invoices.Add(invoice);
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var cached = Assert.IsType<LocalCustomer>(await local.GetCustomerAsync(customer.Id, session));
            await ApplySnapshot(db, local, session);

            // This is the real authoritative pull reconciliation: it preserves local rows and dirty edits.
            Assert.DoesNotContain(await local.GetCustomersAsync(session), row => row.Id == customer.Id);
            Assert.Null(await local.GetCustomerAsync(customer.Id, session));
            var readableInvoice = Assert.IsType<LocalInvoice>(await local.GetLatestInvoiceVersionAsync(invoice.Id, session));
            var unscoped = Assert.IsType<LocalCustomer>(await local.GetCustomerAsync(customer.Id));
            Assert.Equal(dirty, unscoped.IsDirty); Assert.False(unscoped.IsDeleted);
            Assert.Equal(5, unscoped.Revision); Assert.Equal(customer.Notes, unscoped.Notes);
            Assert.Equal("PRIVATE-CUSTOMER", cached.NameOriginal);

            // Both branches formerly used by F9 can still construct private party fields after revocation.
            foreach (var retained in new[] { cached, unscoped })
            {
                var model = new WpfInvoicePrintService().CreateDefaultModel(readableInvoice, retained,
                    new LocalCompanyProfile { TradeName = "TEST COMPANY" }, true, true);
                Assert.Equal("PRIVATE-CUSTOMER", model.BuyerName);
                Assert.Equal("PRIVATE-ADDRESS", model.BuyerAddress);
                Assert.Equal("PRIVATE-PHONE", model.BuyerPhone);
            }
            await using (var reopened = new LocalDbContext(options))
            {
                var reloaded = new LocalStateService(reopened, new OfficeAccessService(), new SyncRequestDispatcher(), session);
                Assert.Null(await reloaded.GetCustomerAsync(customer.Id, session));
                // Exclusion belongs to one authenticated account, not every user on the PC.
                Assert.NotNull(await reloaded.GetCustomerAsync(customer.Id, CreateSession()));
            }
            await ApplySnapshot(db, local, session, customer.Id);
            Assert.NotNull(await local.GetCustomerAsync(customer.Id, session));
            Assert.Equal(dirty, (await db.Customers.AsNoTracking().SingleAsync()).IsDirty);
            Assert.Single(await db.Invoices.AsNoTracking().ToListAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void StatementEntryPoint_RechecksCurrentCustomerScopeInsteadOfTrustingCachedRows()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Desktop"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "Desktop", "거래플랜.Desktop.App", "ViewModels", "MainViewModel.cs"));
        var start = source.IndexOf("private async Task PrintStatementAsync()", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<InvoicePrintModel>", start, StringComparison.Ordinal);
        var entryPoint = source[start..end];
        Assert.Contains("await _local.GetCustomerAsync(inv.CustomerId, _session)", entryPoint);
        Assert.DoesNotContain("_allCustomers", entryPoint);
    }

    private static async Task ApplySnapshot(LocalDbContext db, LocalStateService local, SessionState session, params Guid[] visible)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await local.ApplyServerCustomerScopeSnapshotAsync(new CustomerScopeSnapshotDto
        {
            Version = 1, UserId = session.User!.UserId, TenantCode = session.AuthenticatedTenantCode,
            OfficeCode = session.OfficeCode, ScopeType = session.ScopeType, VisibleCustomerIds = visible.ToList()
        }, session, CancellationToken.None);
        await tx.CommitAsync();
    }

    private static SessionState CreateSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(), Username = "print-scope-user", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly, Permissions = [AppPermissionNames.InvoiceEdit]
        });
        return session;
    }
}
