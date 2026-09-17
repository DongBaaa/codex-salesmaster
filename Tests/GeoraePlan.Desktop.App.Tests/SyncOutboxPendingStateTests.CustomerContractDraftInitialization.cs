using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData("save", false)]
    [InlineData("save-new", false)]
    [InlineData("close", false)]
    [InlineData("close", true)]
    public async Task CustomerContractDraftInitialization_OrdinaryCustomerSaveDoesNotCreateContract(string operation, bool unchanged)
    {
        PrepareAppRoot("customer-contract-empty-editor");
        try
        {
            await using var db = CreateContractDraftTestDb();
            await db.Database.EnsureCreatedAsync();
            var session = CreateCustomerScopeSession();
            session.SetOfflineSession(session.User!);
            var customer = CustomerScopeFixture(Guid.NewGuid(), 5, false);
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var vm = new CustomerEditViewModel(local, session);
            await vm.LoadAsync(await local.GetCustomerAsync(customer.Id, session));

            Assert.False(vm.HasPendingChanges);
            if (!unchanged)
                vm.Notes = "ordinary customer note edit";
            if (operation == "save")
                await vm.SaveCommand.ExecuteAsync(null);
            else if (operation == "save-new")
                await vm.SaveAndNewCommand.ExecuteAsync(null);
            else
                Assert.True(await vm.TryAutoSaveOnCloseAsync());

            Assert.Empty(await db.CustomerContracts.IgnoreQueryFilters().ToListAsync());
            var saved = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == customer.Id);
            Assert.Equal(unchanged ? customer.Notes : "ordinary customer note edit", saved.Notes);
            Assert.False(saved.IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CustomerContractDraftInitialization_IntentionalDraftStillPersists()
    {
        PrepareAppRoot("customer-contract-intentional-draft");
        try
        {
            await using var db = CreateContractDraftTestDb();
            await db.Database.EnsureCreatedAsync();
            var session = CreateCustomerScopeSession();
            session.SetOfflineSession(session.User!);
            var customer = CustomerScopeFixture(Guid.NewGuid(), 5, false);
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var vm = new CustomerEditViewModel(local, session);
            await vm.LoadAsync(await local.GetCustomerAsync(customer.Id, session));
            vm.ContractDescription = "explicit draft for a later PDF";
            vm.ContractExpireDate = new DateOnly(2027, 1, 31);
            Assert.True(await vm.TryAutoSaveOnCloseAsync());
            var contract = Assert.Single(await db.CustomerContracts.AsNoTracking().ToListAsync());
            Assert.Equal(customer.Id, contract.CustomerId);
            Assert.Equal("explicit draft for a later PDF", contract.Description);
            Assert.Equal(new DateOnly(2027, 1, 31), contract.ExpireDate);
            Assert.True(contract.IsPrimary);
            Assert.False(vm.HasPendingChanges);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalDbContext CreateContractDraftTestDb()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!, "contracts.db");
        return new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={path}").Options);
    }
}
