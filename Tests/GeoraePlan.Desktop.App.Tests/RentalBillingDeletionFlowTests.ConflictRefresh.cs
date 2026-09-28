using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class RentalBillingDeletionFlowTests
{
    [Theory]
    [InlineData("USENET", false)]
    [InlineData("USENET", true)]
    [InlineData("ITWORLD", false)]
    [InlineData("ITWORLD", true)]
    public async Task DeleteHistoryConflict_RefreshesWinnerAndHistory_WithoutDeletingUntilExplicitRetry(
        string office, bool invoiceConflict)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var session = new SessionState();
        var tenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, office);
        session.SetOfflineSession(new UserSessionDto { Username = "conflict-test", Role = DomainConstants.RoleAdmin,
            TenantCode = tenant, OfficeCode = office, ScopeType = TenantScopeCatalog.ScopeAdmin });
        var customer = CreateCustomer(Guid.NewGuid(), "Conflict refresh fixture", office);
        var assetId = Guid.NewGuid();
        var profile = CreateBillingProfile(Guid.NewGuid(), assetId, customer.NameOriginal);
        profile.CustomerId = customer.Id;
        profile.Revision = 101L;
        profile.TenantCode = tenant;
        profile.OfficeCode = profile.ResponsibleOfficeCode = profile.ManagementCompanyCode = office;
        var asset = CreateRentalAsset(assetId, customer.NameOriginal, profile.Id, "청구대상");
        asset.TenantCode = tenant;
        asset.OfficeCode = asset.ResponsibleOfficeCode = asset.ManagementCompanyCode = office;
        db.Customers.Add(customer);
        db.RentalBillingProfiles.Add(profile);
        db.RentalAssets.Add(asset);
        await db.SaveChangesAsync();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var rental = new RentalStateService(db, local);
        var started = await rental.StartBillingAsync(profile.Id, new DateOnly(2026, 5, 25), session);
        Assert.True(started.Success, started.Message);
        var payment = await local.SaveTransactionAsync(new LocalTransaction
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, TransactionDate = new DateOnly(2026, 5, 27),
            TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
            LinkedInvoiceId = started.RelatedEntityId, BankReceipt = 20_000m,
            ReceiptTotal = 20_000m, SettlementAmount = 20_000m
        }, session);
        Assert.True(payment.Success, payment.Message);
        var vm = new RentalBillingViewModel(rental, local, session);
        void SetField(string name, object value) => typeof(RentalBillingViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        try
        {
            SetField("_suppressFilterReload", true);
            vm.ShowIndividualProfiles = true;
            vm.ReferenceDate = new DateOnly(2026, 5, 31);
            SetField("_suppressFilterReload", false);
            SetField("_autoSaveSuppressionCount", 2);
            SetField("_suppressAutomaticSelectionLoads", true);
            await InvokePrivateInstanceTaskAsync(vm, "ReloadAsync");
            vm.SelectedRow = Assert.Single(vm.Rows, row => row.Source.Id == profile.Id);
            await InvokePrivateInstanceTaskAsync(vm, "RefreshBillingHistoryRowsForProfileAsync", profile.Id);
            var history = Assert.Single(vm.BillingHistoryRows, row => row.InvoiceId == started.RelatedEntityId);
            vm.SelectedBillingHistory = history;
            var expectedRevision = vm.SelectedRow.Source.Revision;
            Assert.True(expectedRevision > 0);
            db.ChangeTracker.Clear();
            var current = await db.RentalBillingProfiles.SingleAsync(p => p.Id == profile.Id);
            var invoice = await db.Invoices.SingleAsync(i => i.Id == started.RelatedEntityId);
            if (invoiceConflict)
            {
                invoice.Revision++;
                invoice.Memo = "Concurrent invoice winner";
            }
            else
            {
                current.Revision++;
                current.Notes = "Concurrent profile winner";
            }
            await db.SaveChangesAsync();
            var winnerRevision = current.Revision;
            var winnerInvoiceRevision = invoice.Revision;
            var winnerNotes = current.Notes;
            var winnerMemo = invoice.Memo;
            var winnerRuns = current.BillingRunsJson;
            db.ChangeTracker.Clear();

            var method = typeof(RentalBillingViewModel).GetMethod("DeleteConfirmedBillingHistoryAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = await (Task<LocalMutationResult>)method.Invoke(vm, [profile.Id, history, expectedRevision])!;
            Assert.True(result.ConcurrencyConflict, result.Message);
            Assert.False(result.Success);
            Assert.False(vm.IsBusy);
            Assert.DoesNotContain("현재 조회 중", vm.StatusMessage);
            Assert.Equal(winnerRevision, vm.SelectedRow!.Source.Revision);
            Assert.Equal(winnerNotes, vm.SelectedRow.Source.Notes);
            var refreshed = Assert.Single(vm.BillingHistoryRows, row => row.InvoiceId == invoice.Id);
            Assert.Equal(winnerInvoiceRevision, refreshed.InvoiceRevision);
            var persisted = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
            Assert.False(persisted.IsDeleted);
            Assert.Equal(winnerMemo, persisted.Memo);
            Assert.False((await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.EntityId)).IsDeleted);
            Assert.Equal(winnerRuns, (await db.RentalBillingProfiles.AsNoTracking().SingleAsync(p => p.Id == profile.Id)).BillingRunsJson);

            // Only a fresh, explicit retry may remove the now-current invoice and receipt.
            var retry = await (Task<LocalMutationResult>)method.Invoke(vm,
                [profile.Id, refreshed, vm.SelectedRow.Source.Revision])!;
            Assert.True(retry.Success, retry.Message);
            Assert.False(vm.IsBusy);
            Assert.DoesNotContain(vm.BillingHistoryRows, row => row.InvoiceId == invoice.Id);
            Assert.True((await db.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == invoice.Id)).IsDeleted);
            Assert.True((await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == payment.EntityId)).IsDeleted);
        }
        finally { vm.CancelPendingBackgroundWork(); }
    }
}
