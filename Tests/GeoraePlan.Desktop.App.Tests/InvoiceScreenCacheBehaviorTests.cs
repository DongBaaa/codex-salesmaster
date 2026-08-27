using System.Data.Common;
using System.Net;
using System.Net.Http;
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

public sealed class InvoiceScreenCacheBehaviorTests
{
    [Fact]
    public async Task CustomerInvoiceLookupViewModel_ReusesCachedRowsAndFinancialSummaryUntilExplicitRefresh()
    {
        var dbRoot = PrepareDatabaseRoot("lookup-ledger-cache");

        try
        {
            await using var db = CreateDbContext(dbRoot);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("Lookup cache customer");
            var invoiceA = CreateInvoice(customer.Id, "LOOKUP-001", new DateOnly(2026, 7, 1), 120_000m);
            var transactionA = CreateStandaloneTransaction(customer.Id, new DateOnly(2026, 7, 2), 30_000m, 100m, "Lookup cache receipt A");
            db.Customers.Add(customer);
            db.Invoices.Add(invoiceA);
            db.InvoiceLines.Add(CreateInvoiceLine(invoiceA.Id, "Lookup item A"));
            db.Transactions.Add(transactionA);
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var viewModel = new CustomerInvoiceLookupViewModel(local, session);

            await viewModel.LoadAsync(initialCustomerId: customer.Id);

            Assert.Equal(2, viewModel.InvoiceRows.Count);
            Assert.Equal(100m, viewModel.PreviewCustomerAdvanceBalance);
            Assert.Contains(viewModel.InvoiceRows, row => row.DisplayNumber == "LOOKUP-001");

            var invoiceB = CreateInvoice(customer.Id, "LOOKUP-002", new DateOnly(2026, 7, 3), 80_000m);
            var transactionB = CreateStandaloneTransaction(customer.Id, new DateOnly(2026, 7, 4), 20_000m, 50m, "Lookup cache receipt B");
            db.Invoices.Add(invoiceB);
            db.InvoiceLines.Add(CreateInvoiceLine(invoiceB.Id, "Lookup item B"));
            db.Transactions.Add(transactionB);
            await db.SaveChangesAsync();

            await InvokeNonPublicTaskAsync(viewModel, "LoadInvoiceRowsCoreAsync", false);

            Assert.Equal(2, viewModel.InvoiceRows.Count);
            Assert.Equal(100m, viewModel.PreviewCustomerAdvanceBalance);
            Assert.DoesNotContain(viewModel.InvoiceRows, row => row.DisplayNumber == "LOOKUP-002");
            Assert.DoesNotContain(viewModel.InvoiceRows, row => row.IsTransactionRow && row.InvoiceDate == new DateOnly(2026, 7, 4));

            await viewModel.RefreshRowsAsync();

            Assert.Equal(4, viewModel.InvoiceRows.Count);
            Assert.Equal(150m, viewModel.PreviewCustomerAdvanceBalance);
            Assert.Contains(viewModel.InvoiceRows, row => row.DisplayNumber == "LOOKUP-002");
            Assert.Contains(viewModel.InvoiceRows, row => row.IsTransactionRow && row.InvoiceDate == new DateOnly(2026, 7, 4));
        }
        finally
        {
            CleanupDatabaseRoot(dbRoot);
        }
    }

    [Fact]
    public async Task CustomerInvoiceLookupViewModel_ReloadsRenamedCustomerBeforeRefreshingRows()
    {
        var dbRoot = PrepareDatabaseRoot("lookup-customer-refresh");

        try
        {
            await using var db = CreateDbContext(dbRoot);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("Before rename");
            var invoice = CreateInvoice(customer.Id, "RENAME-001", new DateOnly(2026, 7, 5), 55_000m);
            db.Customers.Add(customer);
            db.Invoices.Add(invoice);
            db.InvoiceLines.Add(CreateInvoiceLine(invoice.Id, "Rename item"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var viewModel = new CustomerInvoiceLookupViewModel(local, session);
            await viewModel.LoadAsync(initialCustomerId: customer.Id);

            Assert.Equal("Before rename", viewModel.SelectedCustomer?.NameOriginal);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Customers SET NameOriginal = {"After rename"}, NameMatchKey = {"AFTER RENAME"} WHERE Id = {customer.Id}");
            db.ChangeTracker.Clear();

            await viewModel.RefreshCustomersAndRowsAsync();

            Assert.Equal("After rename", viewModel.SelectedCustomer?.NameOriginal);
            Assert.Equal("After rename", viewModel.PreviewCustomerName);
            Assert.Contains(viewModel.FilteredCustomers, current => current.Id == customer.Id && current.NameOriginal == "After rename");
            Assert.Contains(viewModel.InvoiceRows, row => row.DisplayNumber == "RENAME-001");

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Customers SET OfficeCode = {OfficeCodeCatalog.Itworld}, ResponsibleOfficeCode = {OfficeCodeCatalog.Itworld} WHERE Id = {customer.Id}");
            db.ChangeTracker.Clear();

            await viewModel.RefreshCustomersAndRowsAsync();

            Assert.Null(viewModel.SelectedCustomer);
            Assert.DoesNotContain(viewModel.FilteredCustomers, current => current.Id == customer.Id);
            Assert.Null(viewModel.ResolveActionCustomer());
        }
        finally
        {
            CleanupDatabaseRoot(dbRoot);
        }
    }

    [Fact]
    public async Task MainViewModel_ReusesSelectedCustomerCacheAndInvalidatesOnPassiveSyncReload()
    {
        var dbRoot = PrepareDatabaseRoot("main-ledger-cache");

        try
        {
            await using var db = CreateDbContext(dbRoot);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("Main cache customer");
            var invoiceA = CreateInvoice(customer.Id, "MAIN-001", new DateOnly(2026, 7, 1), 210_000m);
            db.Customers.Add(customer);
            db.Invoices.Add(invoiceA);
            db.InvoiceLines.Add(CreateInvoiceLine(invoiceA.Id, "Main item A"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient(new StubHttpMessageHandler()) { BaseAddress = new Uri("http://localhost/") }, session);
            var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            var viewModel = new MainViewModel(local, sync, new BackupService(), rental, diagnostics, api, session);

            try
            {
                await viewModel.LoadAsync();

                Assert.Single(viewModel.InvoiceRows);
                var selectedCustomer = Assert.Single(viewModel.FilteredCustomers);
                viewModel.SelectedCustomerFilter = selectedCustomer;
                await InvokeNonPublicTaskAsync(
                    viewModel,
                    "LoadInvoiceListCoreAsync",
                    false,
                    CancellationToken.None,
                    false);
                Assert.Single(viewModel.InvoiceRows);

                var invoiceB = CreateInvoice(customer.Id, "MAIN-002", new DateOnly(2026, 7, 2), 95_000m);
                db.Invoices.Add(invoiceB);
                db.InvoiceLines.Add(CreateInvoiceLine(invoiceB.Id, "Main item B"));
                await db.SaveChangesAsync();

                viewModel.SelectedCustomerFilter = null;
                await InvokeNonPublicTaskAsync(
                    viewModel,
                    "LoadInvoiceListCoreAsync",
                    false,
                    CancellationToken.None,
                    false);
                Assert.Single(viewModel.InvoiceRows);
                Assert.DoesNotContain(viewModel.InvoiceRows, row => row.DisplayNumber == "MAIN-002");

                viewModel.SelectedCustomerFilter = Assert.Single(viewModel.FilteredCustomers);
                await InvokeNonPublicTaskAsync(
                    viewModel,
                    "LoadInvoiceListCoreAsync",
                    false,
                    CancellationToken.None,
                    false);
                Assert.Single(viewModel.InvoiceRows);
                Assert.DoesNotContain(viewModel.InvoiceRows, row => row.DisplayNumber == "MAIN-002");

                await viewModel.ReloadAfterPassiveSyncAsync();

                Assert.Equal(2, viewModel.InvoiceRows.Count);
                Assert.Contains(viewModel.InvoiceRows, row => row.DisplayNumber == "MAIN-002");
                Assert.Equal(customer.Id, viewModel.SelectedCustomerFilter?.Id);
            }
            finally
            {
                await viewModel.DrainPendingBackgroundWorkForShutdownAsync();
            }
        }
        finally
        {
            CleanupDatabaseRoot(dbRoot);
        }
    }

    [Fact]
    public async Task MainViewModel_SerializesContractPreviewAndInvoiceReloadOnSharedLocalContext()
    {
        var dbRoot = PrepareDatabaseRoot("main-shared-context-gate");
        var queryGate = new ReadQueryGate("CustomerContracts");

        try
        {
            await using var db = CreateDbContext(dbRoot, queryGate);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("Shared context customer");
            var invoice = CreateInvoice(customer.Id, "GATE-001", new DateOnly(2026, 7, 3), 180_000m);
            db.Customers.Add(customer);
            db.Invoices.Add(invoice);
            db.InvoiceLines.Add(CreateInvoiceLine(invoice.Id, "Gate item"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient(new StubHttpMessageHandler()) { BaseAddress = new Uri("http://localhost/") }, session);
            var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            var viewModel = new MainViewModel(local, sync, new BackupService(), rental, diagnostics, api, session);
            Task? contractPreviewTask = null;
            Task? invoiceReloadTask = null;

            try
            {
                await viewModel.LoadAsync();

                queryGate.Arm();
                contractPreviewTask = InvokeNonPublicTaskAsync(
                    viewModel,
                    "RefreshPreviewCustomerContractAsync",
                    customer,
                    int.MinValue,
                    CancellationToken.None);
                await queryGate.WaitUntilBlockedAsync(TimeSpan.FromSeconds(5));

                invoiceReloadTask = InvokeNonPublicTaskAsync(
                    viewModel,
                    "LoadInvoiceListCoreAsync",
                    true,
                    CancellationToken.None,
                    false);

                var firstCompleted = await Task.WhenAny(invoiceReloadTask, Task.Delay(TimeSpan.FromMilliseconds(150)));
                Assert.NotSame(invoiceReloadTask, firstCompleted);

                queryGate.Release();
                await Task.WhenAll(contractPreviewTask, invoiceReloadTask);
                Assert.Single(viewModel.InvoiceRows);
            }
            finally
            {
                queryGate.Release();
                await viewModel.DrainPendingBackgroundWorkForShutdownAsync();
                await ObserveFailureAsync(contractPreviewTask);
                await ObserveFailureAsync(invoiceReloadTask);
            }
        }
        finally
        {
            CleanupDatabaseRoot(dbRoot);
        }
    }

    [Fact]
    public async Task MainViewModel_SerializesSelectedInvoiceOpenBehindRunningPreview()
    {
        var dbRoot = PrepareDatabaseRoot("selected-invoice-preview-gate");
        var queryGate = new ReadQueryGate("Invoices");

        try
        {
            await using var db = CreateDbContext(dbRoot, queryGate);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("Selected invoice gate customer");
            var invoice = CreateInvoice(customer.Id, "OPEN-GATE-001", new DateOnly(2026, 7, 5), 125_000m);
            db.Customers.Add(customer);
            db.Invoices.Add(invoice);
            db.InvoiceLines.Add(CreateInvoiceLine(invoice.Id, "Open gate item"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient(new StubHttpMessageHandler()) { BaseAddress = new Uri("http://localhost/") }, session);
            var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            var viewModel = new MainViewModel(local, sync, new BackupService(), rental, diagnostics, api, session);
            Task<LocalInvoice?>? openTask = null;

            try
            {
                await viewModel.LoadAsync();

                queryGate.Arm();
                viewModel.SelectedInvoiceRow = Assert.Single(viewModel.InvoiceRows);
                await queryGate.WaitUntilBlockedAsync(TimeSpan.FromSeconds(5));

                openTask = viewModel.GetLatestSelectedInvoiceAsync();
                var firstCompleted = await Task.WhenAny(openTask, Task.Delay(TimeSpan.FromMilliseconds(150)));
                Assert.NotSame(openTask, firstCompleted);

                queryGate.Release();
                var resolved = await openTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(resolved);
                Assert.Equal(invoice.Id, resolved!.Id);
            }
            finally
            {
                queryGate.Release();
                await viewModel.DrainPendingBackgroundWorkForShutdownAsync();
                await ObserveFailureAsync(openTask);
            }
        }
        finally
        {
            CleanupDatabaseRoot(dbRoot);
        }
    }

    [Fact]
    public async Task MainViewModel_BusinessDatabaseReload_CleansHiddenFiltersWithoutReenteringDataGate()
    {
        var dbRoot = PrepareDatabaseRoot("business-reload-filter-gate");

        try
        {
            await using var db = CreateDbContext(dbRoot);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("Business reload customer");
            var invoice = CreateInvoice(customer.Id, "BUSINESS-001", new DateOnly(2026, 7, 4), 75_000m);
            db.Customers.Add(customer);
            db.Invoices.Add(invoice);
            db.InvoiceLines.Add(CreateInvoiceLine(invoice.Id, "Business reload item"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient(new StubHttpMessageHandler()) { BaseAddress = new Uri("http://localhost/") }, session);
            var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            var viewModel = new MainViewModel(local, sync, new BackupService(), rental, diagnostics, api, session);
            var accountKey = $"InvoiceFilter.CustomerName.{TenantScopeCatalog.GetDatabaseName(session.SelectedBusinessDatabaseName)}.cache-admin";

            try
            {
                await local.SetSettingAsync(accountKey, "legacy hidden customer filter");

                await viewModel.RunBusinessDatabaseTransitionAsync(
                        viewModel.ReloadForBusinessDatabaseChangeAsync)
                    .WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(string.Empty, await local.GetSettingAsync(accountKey));
                Assert.Single(viewModel.InvoiceRows);
            }
            finally
            {
                await viewModel.DrainPendingBackgroundWorkForShutdownAsync();
            }
        }
        finally
        {
            CleanupDatabaseRoot(dbRoot);
        }
    }

    [Fact]
    public async Task MainViewModel_ClearCustomerFilterCommand_ClearsSearchAndRestoresAllCustomersImmediately()
    {
        var dbRoot = PrepareDatabaseRoot("main-clear-customer-filter");

        try
        {
            await using var db = CreateDbContext(dbRoot);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var alphaCustomer = CreateCustomer("Alpha customer");
            var betaCustomer = CreateCustomer("Beta customer");
            db.Customers.AddRange(alphaCustomer, betaCustomer);
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient(new StubHttpMessageHandler()) { BaseAddress = new Uri("http://localhost/") }, session);
            var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            var viewModel = new MainViewModel(local, sync, new BackupService(), rental, diagnostics, api, session);

            try
            {
                await viewModel.LoadAsync();
                Assert.Equal(2, viewModel.FilteredCustomers.Count);

                viewModel.CustomerFilterText = "Alpha";
                await InvokeNonPublicTaskAsync(viewModel, "ApplyCustomerFilter");
                viewModel.SelectedCustomerFilter = Assert.Single(viewModel.FilteredCustomers);

                viewModel.ClearCustomerFilterCommand.Execute(null);

                Assert.Equal(string.Empty, viewModel.CustomerFilterText);
                Assert.Null(viewModel.SelectedCustomerFilter);
                Assert.Equal(2, viewModel.FilteredCustomers.Count);
                Assert.Contains(viewModel.FilteredCustomers, customer => customer.Id == alphaCustomer.Id);
                Assert.Contains(viewModel.FilteredCustomers, customer => customer.Id == betaCustomer.Id);
            }
            finally
            {
                await viewModel.DrainPendingBackgroundWorkForShutdownAsync();
            }
        }
        finally
        {
            CleanupDatabaseRoot(dbRoot);
        }
    }

    private static async Task InvokeNonPublicTaskAsync(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(target, args);
        switch (result)
        {
            case Task task:
                await task;
                break;
            case null:
                return;
            default:
                throw new InvalidOperationException($"{methodName} did not return Task.");
        }
    }

    private static LocalCustomer CreateCustomer(string name)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = name.ToUpperInvariant(),
            BusinessNumber = "123-45-67890",
            Phone = "02-1234-5678"
        };

    private static LocalInvoice CreateInvoice(Guid customerId, string invoiceNumber, DateOnly invoiceDate, decimal totalAmount)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerId = customerId,
            InvoiceNumber = invoiceNumber,
            LocalTempNumber = invoiceNumber,
            InvoiceDate = invoiceDate,
            VoucherType = VoucherType.Sales,
            VersionGroupId = Guid.NewGuid(),
            VersionNumber = 1,
            IsLatestVersion = true,
            VatMode = InvoiceVatModes.Included,
            TotalAmount = totalAmount,
            SupplyAmount = Math.Round(totalAmount / 1.1m, 0, MidpointRounding.AwayFromZero),
            VatAmount = totalAmount - Math.Round(totalAmount / 1.1m, 0, MidpointRounding.AwayFromZero),
            IsDeleted = false,
            IsDirty = false,
            UpdatedAtUtc = new DateTime(2026, 7, invoiceDate.Day, 12, 0, 0, DateTimeKind.Utc),
            LastSavedAtUtc = new DateTime(2026, 7, invoiceDate.Day, 12, 0, 0, DateTimeKind.Utc)
        };

    private static LocalInvoiceLine CreateInvoiceLine(Guid invoiceId, string itemName)
        => new()
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            ItemNameOriginal = itemName,
            Quantity = 1m,
            UnitPrice = 1m,
            LineAmount = 1m,
            OrderIndex = 1,
            IsDeleted = false
        };

    private static LocalTransaction CreateStandaloneTransaction(
        Guid customerId,
        DateOnly transactionDate,
        decimal receiptAmount,
        decimal advanceDelta,
        string note)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerId = customerId,
            TransactionDate = transactionDate,
            TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
            ReceiptTotal = receiptAmount,
            CashReceipt = receiptAmount,
            AdvanceDelta = advanceDelta,
            Note = note,
            IsDeleted = false,
            IsDirty = false,
            UpdatedAtUtc = new DateTime(2026, 7, transactionDate.Day, 13, 0, 0, DateTimeKind.Utc)
        };

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "cache-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = []
        });
        return session;
    }

    private static string PrepareDatabaseRoot(string prefix)
    {
        var root = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "invoice-cache-tests",
            $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static LocalDbContext CreateDbContext(
        string root,
        DbCommandInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "거래플랜-tests.db")}");
        if (interceptor is not null)
            optionsBuilder.AddInterceptors(interceptor);

        var options = optionsBuilder.Options;
        return new LocalDbContext(options);
    }

    private static async Task ObserveFailureAsync(Task? task)
    {
        if (task is null)
            return;

        try
        {
            await task;
        }
        catch
        {
            // Preserve the assertion that initiated cleanup while observing any losing task failure.
        }
    }

    private sealed class ReadQueryGate(string tableName) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public Task WaitUntilBlockedAsync(TimeSpan timeout)
            => _blocked.Task.WaitAsync(timeout);

        public void Release() => _released.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 &&
                command.CommandText.Contains(tableName, StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _blocked.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private static void CleanupDatabaseRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
    }
}
