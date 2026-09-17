using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    [Theory]
    [InlineData("exact", 3, 0)]
    [InlineData("changed-payload", 1, 2)]
    [InlineData("new-mutation", 1, 2)]
    [InlineData("changed-base", 1, 2)]
    [InlineData("missing-hash", 1, 2)]
    [InlineData("other-office", 0, 3)]
    [InlineData("other-tenant", 0, 3)]
    [InlineData("server-edited", 3, 0)]
    [InlineData("server-deleted", 3, 0)]
    [InlineData("asset-moved-office", 1, 2)]
    [InlineData("new-asset-command", 2, 1)]
    public async Task Push_RentalRetryAfterCommittedSave_AcknowledgesOnlyProvenAuthorizedDuplicates(
        string retryCase, int expectedAccepted, int expectedConflicts)
        => await VerifyCommittedRentalReplayAsync(_dbContext, retryCase, expectedAccepted, expectedConflicts);

    [PostgreSqlFact]
    public async Task PostgreSql_RentalRetryAfterCommittedSave_PreservesDataAndGenerationGuards()
    {
        var configured = Environment.GetEnvironmentVariable(PostgreSqlSyncPushMutationIdempotencyTests.ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configured));
        var maintenance = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = "postgres", IncludeErrorDetail = false
        };
        foreach (var (retryCase, accepted, conflicts) in new[]
                 { ("exact", 3, 0), ("server-deleted", 3, 0), ("new-asset-command", 2, 1), ("asset-moved-office", 1, 2) })
        {
            var databaseName = $"gpv1_rental_replay_{Guid.NewGuid():N}";
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
                await VerifyCommittedRentalReplayAsync(db, retryCase, accepted, conflicts);
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

    private static async Task VerifyCommittedRentalReplayAsync(
        AppDbContext _dbContext, string retryCase, int expectedAccepted, int expectedConflicts)
    {
        var profile = new RentalBillingProfile
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"COMMITTED-REPLAY-{Guid.NewGuid():N}",
            CustomerName = "Replay customer", ItemName = "Replay rental", IsActive = true,
            MonthlyAmount = 55000
        };
        var asset = new RentalAsset
        {
            TenantCode = profile.TenantCode, OfficeCode = profile.OfficeCode,
            ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
            AssetKey = $"COMMITTED-ASSET-{Guid.NewGuid():N}",
            ManagementId = "2412-009", ManagementNumber = "2412-009",
            BillingProfileId = profile.Id, CustomerName = profile.CustomerName,
            CurrentCustomerName = profile.CustomerName, ItemName = profile.ItemName,
            InstallLocation = "Replay site", InstallSiteName = "Replay site", MonthlyFee = 55000
        };
        profile.BillingTemplateJson = JsonSerializer.Serialize(new[]
        {
            new { IncludedAssetIds = new[] { asset.Id } }
        });
        var history = new RentalAssetAssignmentHistory
        {
            TenantCode = profile.TenantCode, OfficeCode = profile.OfficeCode,
            ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
            AssetId = asset.Id, BillingProfileId = profile.Id,
            CustomerName = profile.CustomerName, ItemName = profile.ItemName,
            InstallLocation = asset.InstallLocation, IsCurrent = true,
            LinkedAtUtc = DateTime.UtcNow.AddMonths(-1), MonthlyFee = 55000
        };
        _dbContext.AddRange(profile, asset, history);
        await _dbContext.SaveChangesAsync();
        var profileDto = profile.ToDto();
        var assetDto = asset.ToDto();
        var historyDto = history.ToDto();
        profileDto.MonthlyAmount = assetDto.MonthlyFee = historyDto.MonthlyFee = 66000;
        foreach (var dto in new SyncEntityDto[] { profileDto, assetDto, historyDto })
        {
            dto.ExpectedRevision = dto.Revision;
            dto.MutationId = $"committed-replay:{dto.Id:N}";
            dto.UpdatedAtUtc = DateTime.UtcNow;
            dto.MutationCreatedAtUtc = dto.UpdatedAtUtc;
        }
        var wirePayload = JsonSerializer.Serialize(new SyncPushRequest
        {
            DeviceId = "committed-rental-retry",
            RentalBillingProfiles = [profileDto], RentalAssets = [assetDto],
            RentalAssetAssignmentHistories = [historyDto]
        });
        var first = await PushRentalReplayWireAsync(_dbContext, wirePayload);
        Assert.Empty(first.Conflicts);
        Assert.Equal(3, first.AcceptedCount);
        Assert.Equal(0, first.DuplicateMutationCount);
        _dbContext.ChangeTracker.Clear();
        var storedProfile = await _dbContext.RentalBillingProfiles.SingleAsync(p => p.Id == profile.Id);
        Assert.NotEqual(profileDto.Revision, storedProfile.Revision);
        Assert.Equal(66000, storedProfile.MonthlyAmount);

        if (retryCase == "missing-hash")
        {
            foreach (var receipt in await _dbContext.ProcessedSyncMutations
                         .Where(r => r.EntityName != nameof(RentalBillingProfile)).ToListAsync())
                receipt.PayloadHash = "";
            await _dbContext.SaveChangesAsync();
        }
        if (retryCase is "server-edited" or "server-deleted" or "asset-moved-office")
        {
            var currentAsset = await _dbContext.RentalAssets.SingleAsync(a => a.Id == asset.Id);
            var currentHistory = await _dbContext.RentalAssetAssignmentHistories.SingleAsync(h => h.Id == history.Id);
            if (retryCase == "server-edited")
            {
                storedProfile.MonthlyAmount = currentAsset.MonthlyFee = currentHistory.MonthlyFee = 88000;
            }
            if (retryCase == "server-deleted")
            {
                storedProfile.IsDeleted = currentAsset.IsDeleted = currentHistory.IsDeleted = true;
            }
            if (retryCase == "asset-moved-office")
                currentAsset.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
            await _dbContext.SaveChangesAsync();
        }
        var before = await RentalReplayBusinessSnapshotAsync(_dbContext);
        var retry = JsonSerializer.Deserialize<SyncPushRequest>(wirePayload)!;
        var dependents = new SyncEntityDto[] { retry.RentalAssets.Single(), retry.RentalAssetAssignmentHistories.Single() };
        if (retryCase == "changed-payload")
        {
            retry.RentalAssets.Single().MonthlyFee = 77000;
            retry.RentalAssetAssignmentHistories.Single().MonthlyFee = 77000;
        }
        foreach (var dto in dependents)
        {
            if (retryCase == "new-mutation") dto.MutationId += ":new";
            if (retryCase == "changed-base") dto.ExpectedRevision++;
        }
        if (retryCase == "new-asset-command") retry.RentalAssets.Single().MutationId += ":new";
        var user = new TestCurrentUserContext
        {
            Username = "admin", IsAdmin = true, ScopeType = TenantScopeCatalog.ScopeAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet
        };
        if (retryCase is "other-office" or "other-tenant")
        {
            user = new TestCurrentUserContext
            {
                Username = "restricted-retry", IsAdmin = false,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                OfficeCode = retryCase == "other-office" ? OfficeCodeCatalog.Yeonsu : OfficeCodeCatalog.Itworld,
                TenantCode = retryCase == "other-office" ? TenantScopeCatalog.UsenetGroup : TenantScopeCatalog.Itworld
            };
        }
        if (retryCase == "asset-moved-office")
        {
            user = new TestCurrentUserContext
            {
                Username = "usenet-only-retry", IsAdmin = false,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                OfficeCode = OfficeCodeCatalog.Usenet, TenantCode = TenantScopeCatalog.UsenetGroup
            };
        }
        var replay = await PushRentalReplayWireAsync(_dbContext, JsonSerializer.Serialize(retry), user);
        Assert.Equal(expectedConflicts, replay.ConflictCount);
        Assert.Equal(expectedAccepted, replay.AcceptedCount);
        Assert.Equal(expectedAccepted, replay.DuplicateMutationCount);
        Assert.Equal(before, await RentalReplayBusinessSnapshotAsync(_dbContext));
        if (retryCase == "exact")
        {
            Assert.Contains(replay.AcceptedRevisions, r => r.EntityName == nameof(RentalAsset) && r.EntityId == asset.Id);
            Assert.Contains(replay.AcceptedRevisions, r => r.EntityName == nameof(RentalAssetAssignmentHistory) && r.EntityId == history.Id);
        }
        if (retryCase == "server-deleted")
        {
            Assert.Contains(replay.AcceptedRevisions, r => r.EntityName == nameof(RentalAsset) && r.IsDeleted == true);
            Assert.Contains(replay.AcceptedRevisions, r => r.EntityName == nameof(RentalAssetAssignmentHistory) && r.IsDeleted == true);
        }
    }

    private static async Task<SyncPushResult> PushRentalReplayWireAsync(AppDbContext _dbContext, string payload, TestCurrentUserContext? user = null)
    {
        _dbContext.ChangeTracker.Clear();
        user ??= new TestCurrentUserContext
        {
            Username = "admin", IsAdmin = true, ScopeType = TenantScopeCatalog.ScopeAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet
        };
        var response = await CreateController(_dbContext, user).Push(
            JsonSerializer.Deserialize<SyncPushRequest>(payload)!, CancellationToken.None);
        return Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
    }

    private static async Task<string> RentalReplayBusinessSnapshotAsync(AppDbContext _dbContext)
    {
        _dbContext.ChangeTracker.Clear();
        return JsonSerializer.Serialize(new
        {
            Profiles = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().OrderBy(e => e.Id).ToListAsync(),
            Assets = await _dbContext.RentalAssets.IgnoreQueryFilters().OrderBy(e => e.Id).ToListAsync(),
            Histories = await _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().OrderBy(e => e.Id).ToListAsync(),
            Receipts = await _dbContext.ProcessedSyncMutations.OrderBy(e => e.Id).ToListAsync()
        });
    }
}
