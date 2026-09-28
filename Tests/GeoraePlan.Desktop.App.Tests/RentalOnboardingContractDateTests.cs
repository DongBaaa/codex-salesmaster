using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalOnboardingContractDateTests
{
    [Fact]
    public async Task NextFromAssetStep_PreservesManualDateWithoutLinkedAssets()
    {
        await using var f = await Fixture.Create();
        f.Vm.CustomerName = "ITWORLD monthly billing";
        f.Vm.LinkAssetsLater = true;
        f.Vm.BillingAdvanceMode = "당월";
        f.Vm.BillingDayMode = RentalBillingScheduleRules.BillingDayModeNoFixedDay;
        f.Vm.BillingStartDate = new DateTime(2026, 9, 1);
        f.Vm.CurrentStepIndex = 3;
        await f.Vm.NextStepCommand.ExecuteAsync(null);
        Assert.Equal(4, f.Vm.CurrentStepIndex);
        Assert.Equal(new DateTime(2026, 9, 1), f.Vm.BillingStartDate);
        Assert.Equal("2026-09", f.Vm.BillingPreviewPeriod);
        Assert.False(f.Vm.IsContractDateMissing);
    }

    [Fact]
    public async Task SourceRefresh_UpdatesAutomaticDate_ButPreservesManualOverrideAndClear()
    {
        await using var f = await Fixture.Create();
        f.Vm.CustomerId = f.Customer.Id;
        await f.Refresh();
        Assert.Equal(new DateTime(2026, 8, 1), f.Vm.BillingStartDate);
        f.Contract.SignedDate = new DateOnly(2026, 8, 2);
        await f.Db.SaveChangesAsync();
        await f.Refresh();
        Assert.Equal(new DateTime(2026, 8, 2), f.Vm.BillingStartDate);
        f.Vm.BillingStartDate = new DateTime(2026, 9, 1);
        await f.Refresh();
        Assert.Equal(new DateTime(2026, 9, 1), f.Vm.BillingStartDate);
        f.Vm.BillingStartDate = null;
        await f.Refresh();
        Assert.Null(f.Vm.BillingStartDate);
    }

    [Fact]
    public async Task SelectingAnotherCustomer_ReplacesPreviousManualDate()
    {
        await using var f = await Fixture.Create();
        f.Vm.BillingStartDate = new DateTime(2026, 9, 1);
        f.Vm.ApplySelectedCustomer(f.Customer);
        await f.Refresh();
        Assert.Equal(new DateTime(2026, 8, 1), f.Vm.BillingStartDate);
        f.Vm.BillingStartDate = new DateTime(2026, 9, 2);
        f.Vm.ApplySelectedCustomer(f.Customer);
        await f.Refresh();
        Assert.Equal(new DateTime(2026, 9, 2), f.Vm.BillingStartDate);
        f.Vm.ApplySelectedCustomer(new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "No contract" });
        await f.Refresh();
        Assert.Null(f.Vm.BillingStartDate);
    }

    [Fact]
    public async Task ManualEditWhileSourceQueryIsPending_WinsOverLateResult()
    {
        var gate = new ContractQueryGate();
        await using var f = await Fixture.Create(gate);
        f.Vm.CustomerId = f.Customer.Id;
        gate.Enabled = true;
        var refresh = f.Refresh();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Vm.BillingStartDate = new DateTime(2026, 9, 1);
        }
        finally { gate.Release.TrySetResult(); }
        await refresh;
        Assert.Equal(new DateTime(2026, 9, 1), f.Vm.BillingStartDate);
    }

    private sealed class ContractQueryGate : DbCommandInterceptor
    {
        public bool Enabled;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("CustomerContracts", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection;
        public required LocalDbContext Db;
        public required RentalCustomerOnboardingViewModel Vm;
        public required LocalCustomer Customer;
        public required LocalCustomerContract Contract;

        public static async Task<Fixture> Create(IInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            var db = new LocalDbContext(options.Options);
            await db.Database.EnsureCreatedAsync();
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "contract-date-test",
                Role = DomainConstants.RoleAdmin, TenantCode = "ITWORLD", OfficeCode = "ITWORLD",
                ScopeType = TenantScopeCatalog.ScopeTenantAll });
            session.SetBusinessDatabase("ITWORLD");
            var customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "Contract customer",
                TenantCode = "ITWORLD", OfficeCode = "ITWORLD", ResponsibleOfficeCode = "ITWORLD" };
            var contract = new LocalCustomerContract { Id = Guid.NewGuid(), CustomerId = customer.Id,
                SignedDate = new DateOnly(2026, 8, 1), IsPrimary = true };
            db.Customers.Add(customer);
            db.CustomerContracts.Add(contract);
            await db.SaveChangesAsync();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var vm = new RentalCustomerOnboardingViewModel(new RentalStateService(db, local), local, session);
            typeof(RentalCustomerOnboardingViewModel).GetMethod("BeginAutoSaveSuppression",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
            vm.OfficeCode = "ITWORLD";
            return new Fixture { Connection = connection, Db = db, Vm = vm, Customer = customer, Contract = contract };
        }

        public Task Refresh() => (Task)typeof(RentalCustomerOnboardingViewModel)
            .GetMethod("RefreshContractDateFromSourcesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Vm, [false, CancellationToken.None])!;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
