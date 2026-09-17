using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    [Theory]
    [InlineData("후불")]
    [InlineData("당월")]
    public async Task PushAndPull_NoFixedDay_PreserveModeAndZeroDayThroughScheduleMaintenance(string advanceMode)
        => await VerifyNoFixedDayRoundTripAsync(_dbContext, _controller, advanceMode);

    private static async Task VerifyNoFixedDayRoundTripAsync(AppDbContext _dbContext, SyncController _controller, string advanceMode)
    {
        var profile = new RentalBillingProfile
        {
            ProfileKey = $"NO-FIXED-DAY-{Guid.NewGuid():N}",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = "No fixed day API fixture", ItemName = "Rental fee", MonthlyAmount = 55000,
            BillingCycleMonths = 1, BillingDay = 25, IsActive = true
        };
        _dbContext.RentalBillingProfiles.Add(profile);
        await _dbContext.SaveChangesAsync();
        var dto = profile.ToDto();
        dto.ExpectedRevision = dto.Revision;
        dto.MutationId = $"no-fixed-day:{Guid.NewGuid():N}";
        dto.BillingDayMode = RentalBillingScheduleRules.BillingDayModeNoFixedDay;
        dto.BillingDay = 31;
        dto.BillingAdvanceMode = advanceMode;
        dto.UpdatedAtUtc = DateTime.UtcNow;
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "manual-billing-test", RentalBillingProfiles = [dto],
            RentalBillingScheduleVersion = RentalBillingScheduleRules.ScheduleCapabilityVersion
        }, CancellationToken.None);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Empty(result.Conflicts);
        Assert.Equal(1, result.AcceptedCount);
        _dbContext.ChangeTracker.Clear();
        var stored = await _dbContext.RentalBillingProfiles.SingleAsync(p => p.Id == profile.Id);
        Assert.Equal(0, stored.BillingDay);
        Assert.Equal(advanceMode, stored.BillingAdvanceMode);
        Assert.Equal(RentalBillingScheduleRules.BillingDayModeNoFixedDay, stored.BillingDayMode);

        // Old numeric day data must not turn an explicitly manual policy into end-of-month.
        stored.BillingDay = 31;
        await _dbContext.SaveChangesAsync();
        var maintenance = typeof(DbInitializer).GetMethod("NormalizeRentalBillingScheduleRulesAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        await (Task)maintenance.Invoke(null, new object[] { _dbContext, CancellationToken.None })!;
        _dbContext.ChangeTracker.Clear();
        var legacyPull = await _controller.Pull(0, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(legacyPull.Result);
        var legacyPush = await _controller.Push(new SyncPushRequest { DeviceId = "legacy" }, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(legacyPush.Result);
        var legacyRewrite = stored.ToDto();
        legacyRewrite.ExpectedRevision = stored.Revision;
        legacyRewrite.BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay;
        legacyRewrite.BillingDay = 25;
        legacyRewrite.MutationId = "legacy-default-date-rewrite";
        var rejectedRewrite = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "legacy", RentalBillingProfiles = [legacyRewrite]
        }, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(rejectedRewrite.Result);
        Assert.False(await _dbContext.ProcessedSyncMutations.AnyAsync(r => r.MutationId == legacyRewrite.MutationId));
        _dbContext.ChangeTracker.Clear();
        Assert.Equal(RentalBillingScheduleRules.BillingDayModeNoFixedDay,
            (await _dbContext.RentalBillingProfiles.AsNoTracking().SingleAsync(p => p.Id == profile.Id)).BillingDayMode);
        var pullResponse = await _controller.Pull(0, CancellationToken.None,
            rentalBillingScheduleVersion: RentalBillingScheduleRules.ScheduleCapabilityVersion);
        var pull = Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>(pullResponse.Result).Value);
        var pulled = Assert.Single(pull.RentalBillingProfiles, p => p.Id == profile.Id);
        Assert.Equal(RentalBillingScheduleRules.BillingDayModeNoFixedDay, pulled.BillingDayMode);
        Assert.Equal(0, pulled.BillingDay);
        Assert.Equal(advanceMode, pulled.BillingAdvanceMode);
        Assert.Equal(55000m, pulled.MonthlyAmount);
        var plan = RentalBillingScheduleRules.ResolveConfiguredBillingPlan(pulled.BillingDay, pulled.BillingDayMode,
            pulled.BillingCycleMonths, 9, new DateOnly(2026, 9, 17));
        Assert.Null(plan.BillingDate);
        Assert.Equal("20260901-20260930", plan.RunKey);
    }

    [Theory]
    [InlineData("visible", true)]
    [InlineData("other-office", false)]
    [InlineData("other-tenant", false)]
    public async Task NoFixedDay_LegacyGuard_IsLimitedToReadableRentalScope(string scopeCase, bool blocked)
    {
        var profile = new RentalBillingProfile
        {
            ProfileKey = $"scope-{Guid.NewGuid():N}",
            TenantCode = scopeCase == "other-tenant" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
            OfficeCode = scopeCase == "other-tenant" ? OfficeCodeCatalog.Itworld : OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = scopeCase == "other-tenant" ? OfficeCodeCatalog.Itworld
                : scopeCase == "other-office" ? OfficeCodeCatalog.Yeonsu : OfficeCodeCatalog.Usenet,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeNoFixedDay,
            BillingDay = 0, IsActive = true
        };
        _dbContext.RentalBillingProfiles.Add(profile);
        await _dbContext.SaveChangesAsync();
        var user = new TestCurrentUserContext
        {
            Username = "legacy-office-user", TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };
        await using var db = CreateDbContext(user);
        var controller = CreateController(db, user);
        var pull = await controller.Pull(0, CancellationToken.None);
        var push = await controller.Push(new SyncPushRequest { DeviceId = "legacy-office-device" }, CancellationToken.None);
        if (blocked)
        {
            Assert.IsType<ConflictObjectResult>(pull.Result);
            Assert.IsType<ConflictObjectResult>(push.Result);
        }
        else
        {
            var result = Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>(pull.Result).Value);
            Assert.DoesNotContain(result.RentalBillingProfiles, p => p.Id == profile.Id);
            Assert.IsType<OkObjectResult>(push.Result);
        }
    }

    [PostgreSqlFact]
    public async Task PostgreSql_NoFixedDay_RoundTripMaintenanceAndLegacyProtection()
    {
        var configured = Environment.GetEnvironmentVariable(PostgreSqlSyncPushMutationIdempotencyTests.ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configured));
        var maintenance = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", IncludeErrorDetail = false };
        var databaseName = $"gpv1_no_fixed_day_{Guid.NewGuid():N}";
        var created = false;
        await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection))
                await create.ExecuteNonQueryAsync();
            created = true;
            var testConnection = new NpgsqlConnectionStringBuilder(maintenance.ConnectionString) { Database = databaseName };
            var user = new TestCurrentUserContext
            {
                Username = "admin", IsAdmin = true, ScopeType = TenantScopeCatalog.ScopeAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet
            };
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testConnection.ConnectionString).Options, user, new RevisionClock());
            await db.Database.EnsureCreatedAsync();
            await VerifyNoFixedDayRoundTripAsync(db, CreateController(db, user), "후불");
            await VerifyNoFixedDayRoundTripAsync(db, CreateController(db, user), "당월");
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
}
