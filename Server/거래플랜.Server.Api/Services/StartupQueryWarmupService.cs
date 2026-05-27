using System.Diagnostics;
using 거래플랜.Server.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

/// <summary>
/// Runs a small set of read-only queries after database initialization so that
/// the first real user/diagnostic request does not pay the full EF/database
/// cold-start cost. This service does not mutate business data.
/// </summary>
public sealed class StartupQueryWarmupService : BackgroundService
{
    private static readonly TimeSpan InitializationPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan InitializationWaitDeadline = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DatabaseInitializationState _databaseInitializationState;
    private readonly ILogger<StartupQueryWarmupService> _logger;

    public StartupQueryWarmupService(
        IServiceScopeFactory scopeFactory,
        DatabaseInitializationState databaseInitializationState,
        ILogger<StartupQueryWarmupService> logger)
    {
        _scopeFactory = scopeFactory;
        _databaseInitializationState = databaseInitializationState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!await WaitForDatabaseInitializationAsync(stoppingToken))
                return;

            await WarmupCoreQueriesAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 정상 종료 경로입니다.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup query warmup failed. The server can continue serving requests.");
        }
    }

    private async Task<bool> WaitForDatabaseInitializationAsync(CancellationToken stoppingToken)
    {
        var deadline = DateTime.UtcNow.Add(InitializationWaitDeadline);
        while (!stoppingToken.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            var snapshot = _databaseInitializationState.CreateSnapshot();
            if (snapshot.Completed)
                return true;

            if (snapshot.Failed)
            {
                _logger.LogWarning(
                    "Startup query warmup skipped because database initialization failed: {ErrorMessage}",
                    snapshot.ErrorMessage);
                return false;
            }

            await Task.Delay(InitializationPollInterval, stoppingToken);
        }

        _logger.LogWarning("Startup query warmup skipped because database initialization did not complete within {Timeout}.", InitializationWaitDeadline);
        return false;
    }

    private async Task WarmupCoreQueriesAsync(CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await dbContext.Database.CanConnectAsync(stoppingToken))
        {
            _logger.LogWarning("Startup query warmup skipped because the database connection is not available.");
            return;
        }

        var userCount = await dbContext.Users
            .AsNoTracking()
            .CountAsync(stoppingToken);

        var activeCustomerCount = await dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(customer => !customer.IsDeleted, stoppingToken);

        var activeItemCount = await dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(item => !item.IsDeleted, stoppingToken);

        var activeInvoiceCount = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(invoice => !invoice.IsDeleted, stoppingToken);

        var activePaymentCount = await dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(payment => !payment.IsDeleted, stoppingToken);

        var activeRentalProfileCount = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(profile => !profile.IsDeleted, stoppingToken);

        var activeRentalAssetCount = await dbContext.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(asset => !asset.IsDeleted, stoppingToken);

        var warehouseStockCount = await dbContext.ItemWarehouseStocks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(stoppingToken);

        _logger.LogInformation(
            "Startup query warmup completed in {ElapsedMilliseconds} ms. users={UserCount}, customers={CustomerCount}, items={ItemCount}, invoices={InvoiceCount}, payments={PaymentCount}, rentalProfiles={RentalProfileCount}, rentalAssets={RentalAssetCount}, warehouseStocks={WarehouseStockCount}",
            stopwatch.ElapsedMilliseconds,
            userCount,
            activeCustomerCount,
            activeItemCount,
            activeInvoiceCount,
            activePaymentCount,
            activeRentalProfileCount,
            activeRentalAssetCount,
            warehouseStockCount);
    }
}
