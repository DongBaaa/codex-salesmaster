using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    [Theory]
    [InlineData("고정일", "당월", 0, true)]
    [InlineData("고정일", "당월", 1, true)]
    [InlineData("고정일", "당월", 2, false)]
    [InlineData("지정일 없음", "당월", 1, true)]
    [InlineData("지정일 없음", "당월", 2, false)]
    [InlineData("지정일 없음", "후불", 1, false)]
    public async Task CurrentMonthBilling_RoundTripProtectsAgainstOlderClientRewrites(
        string dayMode, string advanceMode, int capability, bool blocked)
    {
        var profile = new RentalBillingProfile
        {
            ProfileKey = $"current-month-{Guid.NewGuid():N}",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, CustomerName = "Current month fixture",
            ItemName = "Rental fee", MonthlyAmount = 55000, BillingCycleMonths = 1,
            BillingDayMode = dayMode, BillingDay = dayMode == "지정일 없음" ? 0 : 25,
            BillingAdvanceMode = advanceMode, IsActive = true
        };
        _dbContext.RentalBillingProfiles.Add(profile);
        await _dbContext.SaveChangesAsync();
        var originalRevision = profile.Revision;
        var pull = await _controller.Pull(0, CancellationToken.None, rentalBillingScheduleVersion: capability);
        var dto = profile.ToDto();
        dto.ExpectedRevision = dto.Revision;
        dto.MutationId = $"current-month-rewrite-{Guid.NewGuid():N}";
        // An old client silently rewrites an unfamiliar value to its default.
        if (blocked) dto.BillingAdvanceMode = "후불";
        var push = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "current-month-client", RentalBillingScheduleVersion = capability,
            RentalBillingProfiles = [dto]
        }, CancellationToken.None);
        if (blocked)
        {
            Assert.IsType<ConflictObjectResult>(pull.Result);
            Assert.IsType<ConflictObjectResult>(push.Result);
            Assert.False(await _dbContext.ProcessedSyncMutations.AnyAsync(m => m.MutationId == dto.MutationId));
        }
        else
        {
            var pulled = Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>(pull.Result).Value);
            Assert.Equal(advanceMode, Assert.Single(pulled.RentalBillingProfiles).BillingAdvanceMode);
            var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(push.Result).Value);
            Assert.Equal(1, result.AcceptedCount);
            Assert.Empty(result.Conflicts);
        }
        _dbContext.ChangeTracker.Clear();
        var stored = await _dbContext.RentalBillingProfiles.AsNoTracking().SingleAsync(p => p.Id == profile.Id);
        Assert.Equal(advanceMode, stored.BillingAdvanceMode);
        Assert.Equal(55000m, stored.MonthlyAmount);
        if (blocked) Assert.Equal(originalRevision, stored.Revision);
    }

    [Theory]
    [InlineData("visible", true)]
    [InlineData("other-office", false)]
    [InlineData("other-tenant", false)]
    public async Task CurrentMonthBilling_GuardOnlyAppliesToReadableScope(string scopeCase, bool blocked)
    {
        var profile = new RentalBillingProfile
        {
            ProfileKey = $"current-month-scope-{Guid.NewGuid():N}",
            TenantCode = scopeCase == "other-tenant" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
            OfficeCode = scopeCase == "other-tenant" ? OfficeCodeCatalog.Itworld : OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = scopeCase == "other-tenant" ? OfficeCodeCatalog.Itworld
                : scopeCase == "other-office" ? OfficeCodeCatalog.Yeonsu : OfficeCodeCatalog.Usenet,
            BillingAdvanceMode = "당월", BillingDay = 25, IsActive = true
        };
        _dbContext.RentalBillingProfiles.Add(profile);
        await _dbContext.SaveChangesAsync();
        var user = new TestCurrentUserContext
        {
            Username = "previous-client", TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };
        await using var db = CreateDbContext(user);
        var controller = CreateController(db, user);
        var pull = await controller.Pull(0, CancellationToken.None, rentalBillingScheduleVersion: 1);
        var push = await controller.Push(new SyncPushRequest { DeviceId = "previous", RentalBillingScheduleVersion = 1 }, CancellationToken.None);
        if (blocked)
        {
            Assert.IsType<ConflictObjectResult>(pull.Result);
            Assert.IsType<ConflictObjectResult>(push.Result);
        }
        else
        {
            Assert.IsType<OkObjectResult>(pull.Result);
            Assert.IsType<OkObjectResult>(push.Result);
        }
    }
}
