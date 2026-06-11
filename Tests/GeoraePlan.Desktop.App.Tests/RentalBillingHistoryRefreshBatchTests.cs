using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingHistoryRefreshBatchTests
{
    [Fact]
    public async Task RefreshBillingProfileAfterHistoryDeleteAsync_BatchesManyRemainingRuns()
    {
        PrepareAppRoot("georaeplan-rental-history-refresh-batch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("a9100000-1111-4444-8888-000000000001");
            var targetRunId = Guid.Parse("a9200000-1111-4444-8888-000000000001");
            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Batch History Customer",
                BillingType = "묶음",
                BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
                SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
                CompletionStatus = PaymentFlowConstants.CompletionPending
            };

            var runs = Enumerable.Range(0, 650)
                .Select(index => new RentalBillingRunModel
                {
                    RunId = index == 649 ? targetRunId : Guid.NewGuid(),
                    RunKey = $"2026-{(index % 12) + 1:D2}-{index:D4}",
                    ScheduledDate = new DateOnly(2026, 1, 1).AddMonths(index),
                    PeriodStartDate = new DateOnly(2026, 1, 1).AddMonths(index),
                    PeriodEndDate = new DateOnly(2026, 1, 25).AddMonths(index),
                    Status = PaymentFlowConstants.BillingStatusPlanned,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
                    BilledAmount = 0m,
                    SettledAmount = 0m
                })
                .ToList();

            db.Invoices.Add(new LocalInvoice
            {
                Id = Guid.Parse("a9300000-1111-4444-8888-000000000001"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2080, 2, 1),
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = targetRunId,
                TotalAmount = 200_000m,
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = Guid.Parse("a9400000-1111-4444-8888-000000000001"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2080, 2, 5),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = targetRunId,
                ReceiptTotal = 80_000m,
                SettlementAmount = 80_000m,
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            await InvokeRefreshBillingProfileAfterHistoryDeleteAsync(new RentalStateService(db), profile, runs);

            var refreshedRuns = JsonSerializer.Deserialize<List<RentalBillingRunModel>>(profile.BillingRunsJson) ?? [];
            var targetRun = refreshedRuns.Single(run => run.RunId == targetRunId);
            Assert.Equal(200_000m, targetRun.BilledAmount);
            Assert.Equal(80_000m, targetRun.SettledAmount);
            Assert.Equal(new DateOnly(2080, 2, 5), targetRun.SettledDate);
            Assert.Equal(PaymentFlowConstants.BillingStatusInProgress, profile.BillingStatus);
            Assert.Equal(PaymentFlowConstants.SettlementStatusPartial, profile.SettlementStatus);
            Assert.Equal(80_000m, profile.SettledAmount);
            Assert.Equal(120_000m, profile.OutstandingAmount);
            Assert.Equal(new DateOnly(2080, 2, 5), profile.LastSettledDate);
            Assert.True(profile.RequiresFollowUp);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static async Task InvokeRefreshBillingProfileAfterHistoryDeleteAsync(
        RentalStateService service,
        LocalRentalBillingProfile profile,
        List<RentalBillingRunModel> runs)
    {
        var method = typeof(RentalStateService).GetMethod(
            "RefreshBillingProfileAfterHistoryDeleteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            service,
            [profile, runs, CancellationToken.None]));
        await task;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
