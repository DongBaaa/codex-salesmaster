using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.ViewModels;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingHistoryDisplayLimitTests
{
    [Fact]
    public void LimitBillingHistoryRowsForDisplay_ShowsRecentBoundedRows()
    {
        var rows = Enumerable.Range(0, 650)
            .Select(index => new RentalBillingHistoryRow
            {
                BillingRunId = Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
                ScheduledDate = DateOnly.FromDateTime(new DateTime(2026, 6, 1).AddDays(-index)),
                PeriodLabel = $"period-{index:D3}"
            })
            .ToList();
        var method = typeof(RentalBillingViewModel).GetMethod(
            "LimitBillingHistoryRowsForDisplay",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = Assert.IsAssignableFrom<IReadOnlyList<RentalBillingHistoryRow>>(
            method!.Invoke(null, [rows]));

        Assert.Equal(600, result.Count);
        Assert.Equal("period-000", result[0].PeriodLabel);
        Assert.Equal("period-599", result[^1].PeriodLabel);
    }

    [Fact]
    public async Task RentalStateService_GetBillingHistoryRowsAsync_WithDisplayLimit_BoundsRowsBeforeReferenceLookup()
    {
        PrepareAppRoot("georaeplan-rental-billing-history-service-limit");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var runs = Enumerable.Range(0, 650)
                .Select(index =>
                {
                    var scheduledDate = DateOnly.FromDateTime(new DateTime(2026, 6, 1).AddDays(-index));
                    return new RentalBillingRunModel
                    {
                        RunId = Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
                        RunKey = $"run-{index:D3}",
                        ScheduledDate = scheduledDate,
                        PeriodStartDate = scheduledDate.AddMonths(-1),
                        PeriodEndDate = scheduledDate.AddDays(-1),
                        PeriodLabel = $"period-{index:D3}",
                        BilledAmount = 100_000m + index
                    };
                })
                .ToList();

            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "BILLING-HISTORY-LIMIT",
                CustomerName = "Customer A",
                ItemName = "Printer",
                BillingRunsJson = JsonSerializer.Serialize(runs),
                IsActive = true
            });
            db.Transactions.AddRange(runs.Select((run, index) => new LocalTransaction
            {
                Id = Guid.Parse($"10000000-0000-0000-0000-{index + 1:000000000000}"),
                CustomerId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = run.ScheduledDate,
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = run.RunId,
                SettlementAmount = 10m,
                ReceiptTotal = 10m,
                BankReceipt = 10m
            }));
            await db.SaveChangesAsync();

            var rows = await new RentalStateService(db).GetBillingHistoryRowsAsync(
                new[] { profileId },
                CreateAdminSession(),
                new DateOnly(2026, 6, 11),
                maxDisplayRows: 600);

            Assert.Equal(600, rows.Count);
            Assert.Equal("period-000", rows[0].PeriodLabel);
            Assert.Equal("period-599", rows[^1].PeriodLabel);
            Assert.Equal(10m, rows[0].SettledAmount);
            Assert.Equal(10m, rows[^1].SettledAmount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }
}
