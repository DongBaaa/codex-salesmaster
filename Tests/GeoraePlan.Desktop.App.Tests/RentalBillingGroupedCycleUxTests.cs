using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingGroupedCycleUxTests
{
    [Fact]
    public async Task GroupedRows_ShowCycleCounts_AndExpandOnlySelectedCustomerInline()
    {
        var tempRoot = CreateTempRoot("grouped-cycle-display");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var otherCustomerId = Guid.Parse("12111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            db.Customers.AddRange(
                CreateCustomer(customerId, "주기 표시 거래처"),
                CreateCustomer(otherCustomerId, "다른 거래처"));
            db.RentalBillingProfiles.AddRange(
                CreateProfile(Guid.Parse("21111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), customerId, "PROFILE-CYCLE-1-A", 1, 25),
                CreateProfile(Guid.Parse("22111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), customerId, "PROFILE-CYCLE-1-B", 1, 26),
                CreateProfile(Guid.Parse("23111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), customerId, "PROFILE-CYCLE-3", 3, 27),
                CreateProfile(Guid.Parse("24111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), otherCustomerId, "PROFILE-OTHER-6-A", 6, 28, "다른 거래처"),
                CreateProfile(Guid.Parse("25111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), otherCustomerId, "PROFILE-OTHER-6-B", 6, 29, "다른 거래처"));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var collapsedRows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ExpandCustomerSummaryRows = false,
                    ReferenceDate = new DateOnly(2026, 7, 12)
                },
                CreateAdminSession());

            Assert.Equal(2, collapsedRows.Count);
            var aggregate = Assert.Single(collapsedRows, row => row.CustomerDisplayName == "주기 표시 거래처");
            var otherAggregate = Assert.Single(collapsedRows, row => row.CustomerDisplayName == "다른 거래처");
            Assert.True(aggregate.IsAggregateRow);
            Assert.Equal("매월 2건 · 3개월 1건", aggregate.BillingCycleDisplay);
            Assert.Equal(2, aggregate.GroupedBillingCycleCounts[1]);
            Assert.Equal(1, aggregate.GroupedBillingCycleCounts[3]);
            Assert.Equal(1, aggregate.PrimaryBillingCycleMonths);
            Assert.False(string.IsNullOrWhiteSpace(aggregate.CustomerGroupKey));

            var expandedRows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ExpandCustomerSummaryRows = false,
                    ExpandedCustomerGroupKeys = [aggregate.CustomerGroupKey],
                    ReferenceDate = new DateOnly(2026, 7, 12)
                },
                CreateAdminSession());

            Assert.Equal(5, expandedRows.Count);
            var aggregateIndex = expandedRows.ToList().FindIndex(row => row.CustomerGroupKey == aggregate.CustomerGroupKey && row.IsAggregateRow);
            Assert.True(aggregateIndex >= 0);
            Assert.True(expandedRows[aggregateIndex].IsCustomerGroupExpanded);
            var expandedChildren = expandedRows.Skip(aggregateIndex + 1).Take(3).ToList();
            Assert.All(expandedChildren, row =>
            {
                Assert.False(row.IsAggregateRow);
                Assert.True(row.IsCustomerGroupChild);
                Assert.Equal(aggregate.CustomerGroupKey, row.CustomerGroupKey);
                Assert.StartsWith("↳ ", row.CustomerDisplayLabel, StringComparison.Ordinal);
            });
            Assert.Equal(3, expandedChildren.Select(row => row.Source.Id).Distinct().Count());
            Assert.DoesNotContain(expandedRows, row => row.CustomerGroupKey == otherAggregate.CustomerGroupKey && row.IsCustomerGroupChild);
            Assert.False(Assert.Single(expandedRows, row => row.CustomerGroupKey == otherAggregate.CustomerGroupKey).IsCustomerGroupExpanded);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task ExpandedCustomerGroup_DueOnly_DoesNotReinsertProfilesOutsideDueFilter()
    {
        var tempRoot = CreateTempRoot("grouped-cycle-due-filter");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("b1111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            db.Customers.Add(CreateCustomer(customerId, "알림 필터 거래처"));
            var dueProfile = CreateProfile(
                Guid.Parse("b2111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                customerId,
                "PROFILE-DUE",
                1,
                12,
                "알림 필터 거래처");
            dueProfile.BillingAnchorMonth = 7;
            var notDueProfile = CreateProfile(
                Guid.Parse("b3111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                customerId,
                "PROFILE-NOT-DUE",
                1,
                25,
                "알림 필터 거래처");
            notDueProfile.BillingAnchorMonth = 7;
            db.RentalBillingProfiles.AddRange(dueProfile, notDueProfile);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var collapsed = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    DueOnly = true,
                    ExpandCustomerSummaryRows = false,
                    ReferenceDate = new DateOnly(2026, 7, 12)
                },
                CreateAdminSession());
            var aggregate = Assert.Single(collapsed);
            Assert.True(aggregate.IsAggregateRow);

            var expanded = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    DueOnly = true,
                    ExpandCustomerSummaryRows = false,
                    ExpandedCustomerGroupKeys = [aggregate.CustomerGroupKey],
                    ReferenceDate = new DateOnly(2026, 7, 12)
                },
                CreateAdminSession());

            Assert.Equal(2, expanded.Count);
            var child = Assert.Single(expanded, row => row.IsCustomerGroupChild);
            Assert.Equal(dueProfile.Id, child.Source.Id);
            Assert.DoesNotContain(expanded, row => row.Source.Id == notDueProfile.Id && row.IsCustomerGroupChild);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task ExpandedCustomerGroup_PastDueOnly_DoesNotReinsertCurrentProfiles()
    {
        var tempRoot = CreateTempRoot("grouped-cycle-past-due-filter");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("c1111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            db.Customers.Add(CreateCustomer(customerId, "과거 미처리 필터 거래처"));
            var pastDueProfile = CreateProfile(
                Guid.Parse("c2111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                customerId,
                "PROFILE-PAST-DUE",
                1,
                25,
                "과거 미처리 필터 거래처");
            pastDueProfile.BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                CreateBillingRun(
                    Guid.Parse("c3111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    new DateOnly(2026, 6, 25),
                    "2026-06")
            });
            var currentProfile = CreateProfile(
                Guid.Parse("c4111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                customerId,
                "PROFILE-CURRENT",
                1,
                26,
                "과거 미처리 필터 거래처");
            currentProfile.BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                CreateBillingRun(
                    Guid.Parse("c5111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    new DateOnly(2026, 7, 26),
                    "2026-07")
            });
            db.RentalBillingProfiles.AddRange(pastDueProfile, currentProfile);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var collapsed = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    PastDueOnly = true,
                    ExpandCustomerSummaryRows = false,
                    ReferenceDate = new DateOnly(2026, 7, 12)
                },
                CreateAdminSession());
            var aggregate = Assert.Single(collapsed);
            Assert.True(aggregate.IsAggregateRow);

            var expanded = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    PastDueOnly = true,
                    ExpandCustomerSummaryRows = false,
                    ExpandedCustomerGroupKeys = [aggregate.CustomerGroupKey],
                    ReferenceDate = new DateOnly(2026, 7, 12)
                },
                CreateAdminSession());

            Assert.Equal(2, expanded.Count);
            var child = Assert.Single(expanded, row => row.IsCustomerGroupChild);
            Assert.Equal(pastDueProfile.Id, child.Source.Id);
            Assert.DoesNotContain(expanded, row => row.Source.Id == currentProfile.Id && row.IsCustomerGroupChild);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UpdateBillingProfileCycles_ChangesOnlyScheduleFields_AndKeepsHistory()
    {
        var tempRoot = CreateTempRoot("grouped-cycle-update");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("31111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var firstId = Guid.Parse("41111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var secondId = Guid.Parse("42111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            const string firstHistory = "[{\"runId\":\"51111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"periodLabel\":\"2026-05\"}]";
            const string secondHistory = "[{\"runId\":\"52111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"periodLabel\":\"2026-06\"}]";

            var first = CreateProfile(firstId, customerId, "PROFILE-UPDATE-A", 1, 25);
            first.BillingAnchorMonth = 3;
            first.BillingRunsJson = firstHistory;
            first.Revision = 11;
            first.IsDirty = false;
            var second = CreateProfile(secondId, customerId, "PROFILE-UPDATE-B", 3, 26);
            second.BillingAnchorMonth = 4;
            second.BillingRunsJson = secondHistory;
            second.Revision = 22;
            second.IsDirty = false;
            db.RentalBillingProfiles.AddRange(first, second);
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).UpdateBillingProfileCyclesAsync(
                new Dictionary<Guid, long>
                {
                    [firstId] = 11,
                    [secondId] = 22
                },
                2,
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var saved = await db.RentalBillingProfiles
                .AsNoTracking()
                .Where(profile => profile.Id == firstId || profile.Id == secondId)
                .OrderBy(profile => profile.Id)
                .ToListAsync();
            Assert.Equal(2, saved.Count);
            Assert.All(saved, profile =>
            {
                Assert.Equal(2, profile.BillingCycleMonths);
                Assert.True(profile.IsDirty);
            });
            Assert.Equal(3, saved.Single(profile => profile.Id == firstId).BillingAnchorMonth);
            Assert.Equal(4, saved.Single(profile => profile.Id == secondId).BillingAnchorMonth);
            Assert.Equal(firstHistory, saved.Single(profile => profile.Id == firstId).BillingRunsJson);
            Assert.Equal(secondHistory, saved.Single(profile => profile.Id == secondId).BillingRunsJson);
            Assert.Equal(2, saved.Select(profile => profile.ProfileKey).Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(saved, profile => profile.ProfileKey is "PROFILE-UPDATE-A" or "PROFILE-UPDATE-B");
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UpdateBillingProfileCycles_RejectsProfileKeyCollisionWithoutPartialChanges()
    {
        var tempRoot = CreateTempRoot("grouped-cycle-collision");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("61111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var firstId = Guid.Parse("71111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var secondId = Guid.Parse("72111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var first = CreateProfile(firstId, customerId, "PROFILE-COLLISION-A", 1, 25);
            first.Revision = 31;
            first.IsDirty = false;
            var second = CreateProfile(secondId, customerId, "PROFILE-COLLISION-B", 3, 25);
            second.Revision = 32;
            second.IsDirty = false;
            db.RentalBillingProfiles.AddRange(first, second);
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).UpdateBillingProfileCyclesAsync(
                new Dictionary<Guid, long>
                {
                    [firstId] = 31,
                    [secondId] = 32
                },
                2,
                CreateAdminSession());

            Assert.False(result.Success);
            Assert.True(result.PermissionDenied);
            Assert.Contains("식별값", result.Message, StringComparison.Ordinal);
            var savedCycles = await db.RentalBillingProfiles
                .AsNoTracking()
                .OrderBy(profile => profile.Id)
                .Select(profile => profile.BillingCycleMonths)
                .ToListAsync();
            Assert.Equal(new[] { 1, 3 }, savedCycles);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UpdateBillingProfileCycles_RejectsStaleRevisionWithoutChangingAnyProfile()
    {
        var tempRoot = CreateTempRoot("grouped-cycle-concurrency");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("81111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var firstId = Guid.Parse("91111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var secondId = Guid.Parse("92111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var first = CreateProfile(firstId, customerId, "PROFILE-REVISION-A", 1, 25);
            first.Revision = 41;
            first.IsDirty = false;
            var second = CreateProfile(secondId, customerId, "PROFILE-REVISION-B", 3, 26);
            second.Revision = 42;
            second.IsDirty = false;
            db.RentalBillingProfiles.AddRange(first, second);
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).UpdateBillingProfileCyclesAsync(
                new Dictionary<Guid, long>
                {
                    [firstId] = 40,
                    [secondId] = 42
                },
                2,
                CreateAdminSession());

            Assert.False(result.Success);
            Assert.True(result.ConcurrencyConflict);
            var savedCycles = await db.RentalBillingProfiles
                .AsNoTracking()
                .OrderBy(profile => profile.Id)
                .Select(profile => profile.BillingCycleMonths)
                .ToListAsync();
            Assert.Equal(new[] { 1, 3 }, savedCycles);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task UpdateBillingProfileCycles_RejectsProfileOutsideWritableOfficeScope()
    {
        var tempRoot = CreateTempRoot("grouped-cycle-permission");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("a1111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var profile = CreateProfile(
                profileId,
                Guid.Parse("a2111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                "PROFILE-PERMISSION",
                1,
                25);
            profile.Revision = 51;
            profile.IsDirty = false;
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).UpdateBillingProfileCyclesAsync(
                new Dictionary<Guid, long> { [profileId] = 51 },
                3,
                CreateOfficeUserSession(
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Yeonsu,
                    AppPermissionNames.RentalProfileEdit));

            Assert.False(result.Success);
            Assert.True(result.PermissionDenied);
            Assert.Equal(1, await db.RentalBillingProfiles.AsNoTracking().Select(row => row.BillingCycleMonths).SingleAsync());
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static LocalCustomer CreateCustomer(Guid id, string name)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = name.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant(),
            IsDirty = false
        };

    private static LocalRentalBillingProfile CreateProfile(
        Guid id,
        Guid customerId,
        string profileKey,
        int cycleMonths,
        int billingDay,
        string customerName = "주기 표시 거래처")
        => new()
        {
            Id = id,
            CustomerId = customerId,
            CustomerName = customerName,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = profileKey,
            ItemName = $"렌탈 {profileKey}",
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            BillingMethod = "전자세금계산서",
            BillingDay = billingDay,
            BillingCycleMonths = cycleMonths,
            BillingAnchorMonth = 3,
            MonthlyAmount = 100_000m,
            IsActive = true,
            IsDeleted = false
        };

    private static RentalBillingRunModel CreateBillingRun(Guid runId, DateOnly scheduledDate, string periodLabel)
        => new()
        {
            RunId = runId,
            ScheduledDate = scheduledDate,
            PeriodStartDate = new DateOnly(scheduledDate.Year, scheduledDate.Month, 1),
            PeriodEndDate = new DateOnly(
                scheduledDate.Year,
                scheduledDate.Month,
                DateTime.DaysInMonth(scheduledDate.Year, scheduledDate.Month)),
            PeriodLabel = periodLabel,
            BilledAmount = 100_000m,
            SettledAmount = 0m,
            Status = PaymentFlowConstants.BillingStatusPlanned
        };

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "group-cycle-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            Permissions = []
        });
        return session;
    }

    private static SessionState CreateOfficeUserSession(
        string tenantCode,
        string officeCode,
        params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "group-cycle-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static string CreateTempRoot(string scenario)
    {
        var path = Path.Combine(Path.GetTempPath(), $"georaeplan-{scenario}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", path);
        return path;
    }

    private static void CleanupTempRoot(string tempRoot)
    {
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
        SqliteConnection.ClearAllPools();
        _ = tempRoot;
    }
}
