using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RentalScheduleIdentity_TwoRunsInSamePeriodKeepDistinctStableKeys(bool existingNeutralizedRuns, bool revisedToZero)
        => await VerifyRentalScheduleIdentityAsync(_dbContext, existingNeutralizedRuns, revisedToZero);

    [PostgreSqlFact]
    public async Task PostgreSql_RentalScheduleIdentity_PreservesDistinctKeysAndExplicitZeroAcrossRecalculations()
    {
        var configured = Environment.GetEnvironmentVariable(PostgreSqlSyncPushMutationIdempotencyTests.ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configured));
        var maintenance = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = "postgres", IncludeErrorDetail = false, Pooling = false
        };
        var databaseName = $"gpv1_schedule_identity_{Guid.NewGuid():N}";
        var created = false;
        await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection))
                await create.ExecuteNonQueryAsync();
            created = true;
            var testConnection = new NpgsqlConnectionStringBuilder(maintenance.ConnectionString) { Database = databaseName };
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(testConnection.ConnectionString).Options;
            var user = new TestCurrentUserContext
            {
                Username = "admin", IsAdmin = true, ScopeType = TenantScopeCatalog.ScopeAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet
            };
            await using var db = new AppDbContext(options, user, new RevisionClock());
            await db.Database.EnsureCreatedAsync();
            foreach (var existingRuns in new[] { false, true })
                foreach (var revisedToZero in new[] { false, true })
                    await VerifyRentalScheduleIdentityAsync(db, existingRuns, revisedToZero);
        }
        finally
        {
            if (created)
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", connection);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private async Task VerifyRentalScheduleIdentityAsync(AppDbContext db, bool existingNeutralizedRuns, bool revisedToZero)
    {
        var profile = await SeedRentalAmountWriteContractAsync(false, db);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var customer = new Customer { NameOriginal = "분리 청구 근거", TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
        db.Customers.Add(customer);
        var entity = await db.RentalBillingProfiles.SingleAsync(x => x.Id == profile.Id);
        entity.BillingDay = 25; entity.BillingCycleMonths = 1;
        entity.BillingRunsJson = existingNeutralizedRuns ? JsonSerializer.Serialize(ids.Select((id, index) => new {
            RunId = id, RunKey = "stable-" + index, ScheduledDate = DateOnly.MinValue,
            PeriodStartDate = DateOnly.MinValue, PeriodEndDate = DateOnly.MinValue,
            PeriodLabel = "", CycleMonths = 1, Status = "예정", BilledAmount = 0m,
            SettledAmount = 0m, SettlementStatus = "미입금" })) : "[]";
        foreach (var id in ids)
            db.Invoices.Add(new Invoice { CustomerId = customer.Id, InvoiceDate = new(2026, 8, 25),
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "SCHEDULE-" + id.ToString("N"), TotalAmount = 100000m, SupplyAmount = 100000m,
                LinkedRentalBillingProfileId = profile.Id, LinkedRentalBillingRunId = id, IsLatestVersion = true });
        await db.SaveChangesAsync();
        var service = new RentalSettlementRecalculationService(db);
        string? first = null;
        for (var repeat = 0; repeat < 2; repeat++)
        {
            if (repeat == 1 && revisedToZero)
            {
                var invoices = await db.Invoices.Where(x => x.LinkedRentalBillingProfileId == profile.Id).ToListAsync();
                foreach (var invoice in invoices) invoice.TotalAmount = invoice.SupplyAmount = invoice.VatAmount = 0m;
                await db.SaveChangesAsync();
            }
            await service.RecalculateRentalSettlementsAsync(ids.Select(id => (profile.Id, (Guid?)id)), CancellationToken.None);
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var saved = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(x => x.Id == profile.Id);
            Assert.True(RentalBillingRunTombstonePolicy.ValidateForServerMutation(saved.BillingRunsJson).IsValid, saved.BillingRunsJson);
            var rows = JsonNode.Parse(saved.BillingRunsJson)!.AsArray(); Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows.Select(x => x!["RunKey"]!.GetValue<string>().Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(ids.Order(), rows.Select(x => x!["RunId"]!.GetValue<Guid>()).Order());
            foreach (var row in rows)
            {
                Assert.Equal("2026-08-25", row!["ScheduledDate"]!.GetValue<string>());
                Assert.Equal(repeat == 1 && revisedToZero ? 0m : 100000m, row["BilledAmount"]!.GetValue<decimal>());
            }
            if (repeat == 0) first = saved.BillingRunsJson;
            else if (!revisedToZero) Assert.Equal(first, saved.BillingRunsJson);
            else Assert.Equal(JsonNode.Parse(first!)!.AsArray().Select(x => x!["RunKey"]!.GetValue<string>()),
                rows.Select(x => x!["RunKey"]!.GetValue<string>()));
        }
    }
}
