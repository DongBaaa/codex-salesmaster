using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    [Theory]
    [InlineData("ok")]
    [InlineData("old-revision")]
    [InlineData("target-revision")]
    [InlineData("asset-revision")]
    [InlineData("target-missing")]
    [InlineData("old-missing")]
    [InlineData("duplicate-coverage")]
    [InlineData("target-deleted")]
    [InlineData("purchase-hidden")]
    [InlineData("target-outside")]
    public Task Push_RentalRelink_AllParticipantsCommitOrRemainUnchanged(string fault)
        => VerifyRentalRelinkAsync(_dbContext, user => CreateDbContext(user), fault);

    [PostgreSqlFact]
    public async Task PostgreSql_RentalRelink_AllParticipantsCommitOrRemainUnchanged()
    {
        var maintenance = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(
            PostgreSqlSyncPushMutationIdempotencyTests.ConnectionVariableName))
            { Database = "postgres", Pooling = false, IncludeErrorDetail = false };
        await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync();
        foreach (var fault in new[] { "ok", "old-revision", "target-revision", "asset-revision", "target-missing",
            "old-missing", "duplicate-coverage", "target-deleted", "purchase-hidden", "target-outside" })
        {
            var name = "gpv1_relink_" + Guid.NewGuid().ToString("N");
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection)) await create.ExecuteNonQueryAsync();
            try
            {
                var configured = new NpgsqlConnectionStringBuilder(maintenance.ConnectionString) { Database = name };
                var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(configured.ConnectionString).Options;
                AppDbContext Create(TestCurrentUserContext user) => new(options, user, new RevisionClock());
                await using var seed = Create(new TestCurrentUserContext { Username = "admin", IsAdmin = true,
                    ScopeType = TenantScopeCatalog.ScopeAdmin, TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet });
                await seed.Database.EnsureCreatedAsync();
                await VerifyRentalRelinkAsync(seed, Create, fault);
            }
            finally
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task VerifyRentalRelinkAsync(AppDbContext seed, Func<TestCurrentUserContext, AppDbContext> create, string fault)
    {
        var oldCustomer = new Customer { Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "RELINK-OLD", NameMatchKey = "RELINKOLD" };
        var nextCustomer = new Customer { Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "RELINK-NEXT", NameMatchKey = "RELINKNEXT" };
        var assetId = Guid.NewGuid();
        var oldProfile = Profile(oldCustomer, "OLD", [assetId]);
        var nextProfile = Profile(nextCustomer, "NEXT", []);
        if (fault == "target-outside")
        {
            nextCustomer.TenantCode = nextProfile.TenantCode = TenantScopeCatalog.Itworld;
            nextCustomer.OfficeCode = nextCustomer.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
            nextProfile.OfficeCode = nextProfile.ResponsibleOfficeCode = nextProfile.ManagementCompanyCode = OfficeCodeCatalog.Itworld;
        }
        var asset = new RentalAsset { Id = assetId, TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet, CustomerId = oldCustomer.Id,
            CustomerName = oldCustomer.NameOriginal, CurrentCustomerName = oldCustomer.NameOriginal,
            BillingProfileId = oldProfile.Id, AssetKey = "ATOMIC-RELINK", ManagementNumber = "ATOMIC-RELINK",
            ItemName = "RELINK-ASSET", AssetStatus = "임대진행중", BillingEligibilityStatus = "청구대상",
            PurchasePrice = 123456m, SalePrice = 234567m, MonthlyFee = 300m, DepositText = "50000",
            ContractMonths = 24, Notes = "원본", CreatedAtUtc = DateTime.UtcNow.AddDays(-1), UpdatedAtUtc = DateTime.UtcNow.AddDays(-1) };
        seed.AddRange(oldCustomer, nextCustomer, oldProfile, nextProfile, asset,
            new RentalManagementCompany { Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
                Code = OfficeCodeCatalog.Usenet, Name = "유즈넷", IsActive = true, IsSystemDefault = true });
        await seed.SaveChangesAsync(); seed.ChangeTracker.Clear();
        oldProfile = await seed.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == oldProfile.Id);
        nextProfile = await seed.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == nextProfile.Id);
        asset = await seed.RentalAssets.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == assetId);
        var before = new[] { JsonSerializer.Serialize(oldProfile.ToDto()), JsonSerializer.Serialize(nextProfile.ToDto()), JsonSerializer.Serialize(asset.ToDto()) };
        var oldDto = oldProfile.ToDto(); var nextDto = nextProfile.ToDto(); var assetDto = asset.ToDto();
        oldDto.BillingTemplateJson = Template([]);
        nextDto.BillingTemplateJson = Template(fault == "duplicate-coverage" ? [assetId, assetId] : [assetId]);
        assetDto.BillingProfileId = nextProfile.Id; assetDto.CustomerId = nextCustomer.Id;
        assetDto.CustomerName = assetDto.CurrentCustomerName = nextCustomer.NameOriginal; assetDto.Notes = "이전 후";
        foreach (var dto in new SyncEntityDto[] { oldDto, nextDto, assetDto })
        {
            dto.ExpectedRevision = dto.Revision;
            dto.MutationId = "atomic-relink-" + Guid.NewGuid().ToString("N");
            dto.UpdatedAtUtc = DateTime.UtcNow;
        }
        if (fault == "old-revision") oldDto.ExpectedRevision += 999;
        if (fault == "target-revision") nextDto.ExpectedRevision += 999;
        if (fault == "asset-revision") assetDto.ExpectedRevision += 999;
        if (fault == "target-deleted") nextDto.IsDeleted = true;
        var request = new SyncPushRequest { DeviceId = "atomic-relink",
            RentalBillingScheduleVersion = RentalBillingScheduleRules.ScheduleCapabilityVersion,
            RentalBillingProfiles = [oldDto, nextDto], RentalAssets = [assetDto] };
        if (fault == "old-missing") request.RentalBillingProfiles.Remove(oldDto);
        if (fault == "target-missing") request.RentalBillingProfiles.Remove(nextDto);
        var user = new TestCurrentUserContext { Username = "relink-user", TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = fault == "purchase-hidden"
                ? [PermissionNames.RentalEditAll, PermissionNames.AmountViewSales]
                : [PermissionNames.RentalEditAll, PermissionNames.AmountViewSales, PermissionNames.AmountViewPurchase] };
        await using var db = create(user);
        var controller = CreateController(db, user);
        var replayJson = JsonSerializer.Serialize(request);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>((await controller.Push(request, CancellationToken.None)).Result).Value);
        db.ChangeTracker.Clear();
        var savedOld = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == oldProfile.Id);
        var savedNext = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == nextProfile.Id);
        var savedAsset = await db.RentalAssets.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == assetId);
        if (fault != "ok")
        {
            Assert.True(result.Conflicts.Count > 0 || result.Notices.Count > 0);
            Assert.Equal(0, result.AcceptedCount);
            Assert.Equal(before[0], JsonSerializer.Serialize(savedOld.ToDto()));
            Assert.Equal(before[1], JsonSerializer.Serialize(savedNext.ToDto()));
            Assert.Equal(before[2], JsonSerializer.Serialize(savedAsset.ToDto()));
            var ids = new[] { oldDto.MutationId, nextDto.MutationId, assetDto.MutationId };
            Assert.False(await db.ProcessedSyncMutations.AnyAsync(x => ids.Contains(x.MutationId)));
            return;
        }
        Assert.Empty(result.Conflicts); Assert.Equal(3, result.AcceptedCount);
        Assert.Equal(nextProfile.Id, savedAsset.BillingProfileId); Assert.Equal(nextCustomer.Id, savedAsset.CustomerId);
        Assert.Equal(asset.PurchasePrice, savedAsset.PurchasePrice); Assert.Equal(asset.SalePrice, savedAsset.SalePrice);
        Assert.Equal(asset.MonthlyFee, savedAsset.MonthlyFee); Assert.Equal(asset.DepositText, savedAsset.DepositText);
        Assert.Equal(asset.ContractMonths, savedAsset.ContractMonths); Assert.Equal(asset.TenantCode, savedAsset.TenantCode);
        Assert.Equal(asset.OfficeCode, savedAsset.OfficeCode); Assert.Equal("이전 후", savedAsset.Notes);
        Assert.NotEqual(RentalBillingTemplateAssetCoverage.UniqueReference,
            RentalBillingTemplateAssetCoverageRules.Evaluate(savedOld.BillingTemplateJson, assetId));
        Assert.Equal(RentalBillingTemplateAssetCoverage.UniqueReference,
            RentalBillingTemplateAssetCoverageRules.Evaluate(savedNext.BillingTemplateJson, assetId));
        var after = new[] { JsonSerializer.Serialize(savedOld.ToDto()), JsonSerializer.Serialize(savedNext.ToDto()), JsonSerializer.Serialize(savedAsset.ToDto()) };
        var historyCount = await db.RentalAssetAssignmentHistories.CountAsync(x => x.AssetId == assetId);
        var replay = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>((await controller.Push(
            JsonSerializer.Deserialize<SyncPushRequest>(replayJson)!, CancellationToken.None)).Result).Value);
        Assert.Empty(replay.Conflicts); db.ChangeTracker.Clear();
        Assert.Equal(after[0], JsonSerializer.Serialize((await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == oldProfile.Id)).ToDto()));
        Assert.Equal(after[1], JsonSerializer.Serialize((await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == nextProfile.Id)).ToDto()));
        Assert.Equal(after[2], JsonSerializer.Serialize((await db.RentalAssets.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == assetId)).ToDto()));
        Assert.Equal(historyCount, await db.RentalAssetAssignmentHistories.CountAsync(x => x.AssetId == assetId));

        static string Template(Guid[] ids) => JsonSerializer.Serialize(new[] { new { ItemId = Guid.Parse("6f516c1e-f2cd-47ec-98bd-63f4c72eed18"),
            DisplayItemName = "RELINK-ASSET", BillingLineMode = "묶음", Quantity = 1m, UnitPrice = 300m, Amount = 300m, IncludedAssetIds = ids } });
        static RentalBillingProfile Profile(Customer customer, string key, Guid[] ids) => new() {
            Id = Guid.NewGuid(), TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode, ManagementCompanyCode = customer.OfficeCode,
            CustomerId = customer.Id, CustomerName = customer.NameOriginal, ProfileKey = "ATOMIC-" + key,
            ItemName = "RELINK-ASSET", BillingTemplateJson = Template(ids), MonthlyAmount = 300m, BillingDay = 25,
            ContractDate = new(2026, 9, 1), ContractStartDate = new(2026, 9, 1), IsActive = true,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1), UpdatedAtUtc = DateTime.UtcNow.AddDays(-1) };
    }
}
