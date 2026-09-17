using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingCompletionPreviewTests
{
    [Theory]
    [InlineData("완료", false)]
    [InlineData("완료", true)]
    [InlineData("예정", false)]
    [InlineData("예정", true)]
    [InlineData("미수", true)]
    public async Task StatusFilter_UsesCurrentRunBeforeCustomerGrouping(string status, bool individual)
    {
        await using var db = await CreateDbAsync();
        var preview = CreateProfile(null, 0m);
        preview.CompletionStatus = PaymentFlowConstants.CompletionDone;
        preview.BillingStatus = PaymentFlowConstants.BillingStatusCompleted;
        var completed = CreateProfile(PaymentFlowConstants.BillingStatusInProgress, 100000m);
        var partial = CreateProfile(PaymentFlowConstants.BillingStatusInProgress, 50000m);
        db.RentalBillingProfiles.AddRange(preview, completed, partial);
        await db.SaveChangesAsync();

        var rows = await new RentalStateService(db).GetBillingRowsAsync(new RentalBillingFilter
        {
            Status = status, ReferenceDate = new DateOnly(2026, 9, 7), ExpandCustomerSummaryRows = individual
        }, CreateSession());

        var row = Assert.Single(rows);
        var expected = status == "완료" ? completed.Id : status == "예정" ? preview.Id : partial.Id;
        Assert.Equal(new[] { expected }, row.GroupedPersistedProfileIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("예정")]
    public async Task PreviewDisplay_DoesNotInheritPreviousCompletionOrSettlement(string? runStatus)
    {
        await using var db = await CreateDbAsync();
        var profile = CreateProfile(runStatus, 0m);
        profile.CompletionStatus = PaymentFlowConstants.CompletionDone;
        profile.BillingStatus = PaymentFlowConstants.BillingStatusCompleted;
        profile.SettlementStatus = PaymentFlowConstants.SettlementStatusConfirmed;
        profile.SettledAmount = 100000m;
        db.RentalBillingProfiles.Add(profile);
        await db.SaveChangesAsync();
        var original = JsonSerializer.Serialize(await db.RentalBillingProfiles.AsNoTracking().SingleAsync());

        var row = await new RentalStateService(db).GetBillingRowAsync(profile.Id, CreateSession(), new DateOnly(2026, 9, 7));

        Assert.NotNull(row);
        Assert.Equal("D-18", row.DisplayStatus);
        Assert.Equal(0m, row.SettledAmount);
        Assert.Equal(PaymentFlowConstants.CompletionPending, row.CompletionStatus);
        Assert.Equal(original, JsonSerializer.Serialize(await db.RentalBillingProfiles.AsNoTracking().SingleAsync()));
        Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
    }

    [Theory]
    [InlineData("보류", 0, "보류")]
    [InlineData("취소", 0, "취소")]
    [InlineData("청구중", 50000, "부분수금")]
    [InlineData("청구중", 100000, "완료")]
    [InlineData("보류", 50000, "보류")]
    [InlineData("취소", 50000, "취소")]
    [InlineData("보류", 100000, "완료")]
    [InlineData("취소", 100000, "완료")]
    public async Task CurrentRunDisplay_UsesCurrentOperationAndFinancialEvidence(string status, int settled, string expected)
    {
        await using var db = await CreateDbAsync();
        var profile = CreateProfile(status, settled);
        profile.CompletionStatus = PaymentFlowConstants.CompletionDone;
        profile.SettlementStatus = PaymentFlowConstants.SettlementStatusConfirmed;
        db.RentalBillingProfiles.Add(profile);
        await db.SaveChangesAsync();

        var row = await new RentalStateService(db).GetBillingRowAsync(profile.Id, CreateSession(), new DateOnly(2026, 9, 7));

        Assert.NotNull(row);
        Assert.Equal(expected, row.DisplayStatus);
        Assert.Equal(settled == 100000 ? PaymentFlowConstants.BillingStatusCompleted : status, row.CurrentBillingRunStatus);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("예정", false)]
    [InlineData("보류", false)]
    [InlineData(null, true)]
    [InlineData("예정", true)]
    [InlineData("보류", true)]
    public async Task PreviewWithoutFinancialEvidence_IsNotCompleted(string? runStatus, bool legacyCompleted)
    {
        await using var db = await CreateDbAsync();
        var profile = CreateProfile(runStatus, 0m);
        profile.CompletionStatus = legacyCompleted ? PaymentFlowConstants.CompletionDone : PaymentFlowConstants.CompletionPending;
        db.RentalBillingProfiles.Add(profile);
        await db.SaveChangesAsync();
        var originalRuns = profile.BillingRunsJson;

        var row = await new RentalStateService(db).GetBillingRowAsync(profile.Id, CreateSession(), new DateOnly(2026, 9, 7));

        Assert.NotNull(row);
        Assert.Equal(0m, row.OutstandingAmount);
        Assert.Equal(PaymentFlowConstants.CompletionPending, row.CompletionStatus);
        var stored = await db.RentalBillingProfiles.AsNoTracking().SingleAsync();
        Assert.Equal(profile.CompletionStatus, stored.CompletionStatus);
        Assert.Equal(originalRuns, stored.BillingRunsJson);
        Assert.False(stored.IsDirty);
        Assert.Empty(await db.Invoices.ToListAsync());
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(50000, false, false)]
    [InlineData(100000, true, false)]
    [InlineData(120000, true, false)]
    [InlineData(0, false, true)]
    [InlineData(50000, false, true)]
    public async Task ActualBillingEvidence_UsesRemainingBalance(int settledAmount, bool completed, bool legacyCompleted)
    {
        await using var db = await CreateDbAsync();
        var profile = CreateProfile(PaymentFlowConstants.BillingStatusInProgress, settledAmount);
        profile.CompletionStatus = legacyCompleted ? PaymentFlowConstants.CompletionDone : PaymentFlowConstants.CompletionPending;
        db.RentalBillingProfiles.Add(profile);
        await db.SaveChangesAsync();

        var row = await new RentalStateService(db).GetBillingRowAsync(profile.Id, CreateSession(), new DateOnly(2026, 9, 25));

        Assert.NotNull(row);
        Assert.Equal(Math.Max(0m, 100000m - settledAmount), row.OutstandingAmount);
        Assert.Equal(completed ? PaymentFlowConstants.CompletionDone : PaymentFlowConstants.CompletionPending, row.CompletionStatus);
        var stored = await db.RentalBillingProfiles.AsNoTracking().SingleAsync();
        Assert.Equal(profile.CompletionStatus, stored.CompletionStatus);
        Assert.False(stored.IsDirty);
    }

    private static LocalRentalBillingProfile CreateProfile(string? runStatus, decimal settledAmount)
    {
        var profileId = Guid.NewGuid();
        return new LocalRentalBillingProfile
        {
            Id = profileId, ProfileKey = $"completion-{profileId:N}", CustomerName = "Completion preview customer",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            BillingType = "묶음", BillingAdvanceMode = "후불", BillingCycleMonths = 1, BillingDay = 25,
            BillingStartDate = new DateOnly(2026, 9, 1), ContractStartDate = new DateOnly(2026, 9, 1),
            MonthlyAmount = 100000m, BillingStatus = runStatus ?? PaymentFlowConstants.BillingStatusPlanned,
            CompletionStatus = PaymentFlowConstants.CompletionPending, IsActive = true, IsDeleted = false, IsDirty = false,
            CreatedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            BillingRunsJson = runStatus is null ? "[]" : JsonSerializer.Serialize(new[]
            {
                new RentalBillingRunModel
                {
                    RunId = Guid.NewGuid(), RunKey = "20260901-20260930", PeriodLabel = "2026-09",
                    ScheduledDate = new DateOnly(2026, 9, 25), PeriodStartDate = new DateOnly(2026, 9, 1),
                    PeriodEndDate = new DateOnly(2026, 9, 30), Status = runStatus,
                    BilledAmount = 100000m, SettledAmount = settledAmount,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPending
                }
            })
        };
    }

    private static SessionState CreateSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin", Role = DomainConstants.RoleAdmin, ScopeType = TenantScopeCatalog.ScopeAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet
        });
        return session;
    }

    private static async Task<LocalDbContext> CreateDbAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"georaeplan-completion-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "test.db")};Pooling=False").Options;
        var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
