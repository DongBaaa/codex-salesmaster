using System.Text.Json;
using GeoraePlan.Tools.SyncDiag;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class IsolatedSeedRetryRentalAssetReconcilerTests
{
    [Fact]
    public async Task ReconcileAsync_NormalizesOnlySafeUnlinkedOperatingBlankBillingStatus()
    {
        var databasePath = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"seed-retry-rental-asset-unknown-status-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var nowUtc = new DateTime(2026, 8, 5, 3, 0, 0, DateTimeKind.Utc);

        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var historyId = Guid.NewGuid();
            var unrelatedTemplateAssetId = Guid.NewGuid();
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = "USENET_GROUP",
                OfficeCode = "USENET",
                ResponsibleOfficeCode = "USENET",
                NameOriginal = "안전 정규화 거래처",
                NameMatchKey = "안전정규화거래처",
                IsDirty = false,
                Revision = 40
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = "USENET_GROUP",
                OfficeCode = "USENET",
                ResponsibleOfficeCode = "USENET",
                AssetKey = $"ASSET-{assetId:N}",
                BillingProfileId = null,
                CustomerId = customerId,
                CustomerName = "안전 정규화 거래처",
                CurrentCustomerName = "안전 정규화 거래처",
                InstallLocation = "안전 설치처",
                InstallSiteName = "안전 설치처",
                AssetStatus = "운용중",
                BillingEligibilityStatus = "",
                BillingExclusionReason = "",
                MonthlyFee = 0m,
                ItemName = "안전 장비",
                MachineNumber = "SAFE-MACHINE",
                ManagementNumber = "SAFE-MANAGEMENT",
                IsDirty = false,
                Revision = 42,
                CreatedAtUtc = nowUtc.AddYears(-1),
                UpdatedAtUtc = nowUtc.AddDays(-1)
            });
            db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
            {
                Id = historyId,
                AssetId = assetId,
                BillingProfileId = null,
                CustomerId = customerId,
                TenantCode = "USENET_GROUP",
                ResponsibleOfficeCode = "USENET",
                CustomerName = "안전 정규화 거래처",
                InstallLocation = "안전 설치처",
                MonthlyFee = 0m,
                IsCurrent = true,
                LinkedAtUtc = nowUtc.AddYears(-1),
                IsDirty = false,
                Revision = 43
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = Guid.NewGuid(),
                TenantCode = "USENET_GROUP",
                OfficeCode = "USENET",
                ResponsibleOfficeCode = "USENET",
                ProfileKey = $"UNRELATED-{Guid.NewGuid():N}",
                BillingType = "묶음",
                MonthlyAmount = 330_000m,
                BillingTemplateJson = SerializeTemplate(unrelatedTemplateAssetId),
                IsActive = true,
                IsDirty = false,
                Revision = 44
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var beforeAsset = await db.RentalAssets.AsNoTracking().SingleAsync(asset => asset.Id == assetId);
            var beforeAssetWithoutStatus = SerializeAssetWithoutBillingStatus(beforeAsset);
            var beforeHistory = JsonSerializer.Serialize(
                await db.RentalAssetAssignmentHistories.AsNoTracking().SingleAsync(history => history.Id == historyId));
            var beforeCustomer = JsonSerializer.Serialize(
                await db.Customers.AsNoTracking().SingleAsync(customer => customer.Id == customerId));
            var beforeProfile = JsonSerializer.Serialize(
                await db.RentalBillingProfiles.AsNoTracking().SingleAsync());

            var trackedCustomer = await db.Customers.SingleAsync(customer => customer.Id == customerId);
            trackedCustomer.NameOriginal = "저장되면 안 되는 추적 변경";
            var normalizedCount = await IsolatedSeedRentalAssetBillingStatusNormalizer.NormalizeAsync(db);

            db.ChangeTracker.Clear();
            Assert.Equal(1, normalizedCount);
            var storedAsset = await db.RentalAssets.AsNoTracking().SingleAsync(asset => asset.Id == assetId);
            Assert.Equal("미확인", storedAsset.BillingEligibilityStatus);
            Assert.Equal(beforeAssetWithoutStatus, SerializeAssetWithoutBillingStatus(storedAsset));
            Assert.Equal(beforeHistory, JsonSerializer.Serialize(
                await db.RentalAssetAssignmentHistories.AsNoTracking().SingleAsync(history => history.Id == historyId)));
            Assert.Equal(beforeCustomer, JsonSerializer.Serialize(
                await db.Customers.AsNoTracking().SingleAsync(customer => customer.Id == customerId)));
            Assert.Equal(beforeProfile, JsonSerializer.Serialize(
                await db.RentalBillingProfiles.AsNoTracking().SingleAsync()));

            var replayCount = await IsolatedSeedRentalAssetBillingStatusNormalizer.NormalizeAsync(db);
            Assert.Equal(0, replayCount);
            db.ChangeTracker.Clear();
            Assert.Equal("미확인", (await db.RentalAssets.AsNoTracking()
                .SingleAsync(asset => asset.Id == assetId)).BillingEligibilityStatus);

            await db.RentalAssets
                .Where(asset => asset.Id == assetId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(asset => asset.IsDirty, true)
                    .SetProperty(asset => asset.UpdatedAtUtc, nowUtc));
            await using var freshDb = new LocalDbContext(options);
            var preparedForFirstSync = await freshDb.RentalAssets
                .AsNoTracking()
                .SingleAsync(asset => asset.Id == assetId);
            Assert.Equal("미확인", preparedForFirstSync.BillingEligibilityStatus);
            Assert.True(preparedForFirstSync.IsDirty);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public void MarkAllDirty_NormalizesBeforeOutboxResetAndFirstSeedSync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var programSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "SyncDiag",
            "Program.cs"));
        var preparationSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "테스트 시행",
            "테스트-환경-준비.ps1"));

        var markMethodIndex = programSource.IndexOf(
            "static async Task<int> MarkAllDirtyAsync",
            StringComparison.Ordinal);
        var normalizeIndex = programSource.IndexOf(
            "IsolatedSeedRentalAssetBillingStatusNormalizer.NormalizeAsync(db)",
            markMethodIndex,
            StringComparison.Ordinal);
        var outboxResetIndex = programSource.IndexOf(
            "db.SyncOutboxEntries.ExecuteDeleteAsync()",
            markMethodIndex,
            StringComparison.Ordinal);
        var rentalDirtyIndex = programSource.IndexOf(
            "MarkDirtyAsync(db.RentalAssets.IgnoreQueryFilters())",
            markMethodIndex,
            StringComparison.Ordinal);
        Assert.True(markMethodIndex >= 0, "The mark-all-dirty preparation method was not found.");
        Assert.True(
            normalizeIndex > markMethodIndex &&
            outboxResetIndex > normalizeIndex &&
            rentalDirtyIndex > outboxResetIndex,
            "Unknown rental billing status must be normalized before outbox reset and the dirty stamp used by the first seed sync.");

        var markCommandIndex = preparationSource.IndexOf(
            "@('run', '--project', $SyncDiagProject, '--', 'mark-all-dirty')",
            StringComparison.Ordinal);
        var firstSyncLoopIndex = preparationSource.IndexOf(
            "for ($seedSyncAttempt = 1; $seedSyncAttempt -le $maxSeedSyncAttempts; $seedSyncAttempt++)",
            StringComparison.Ordinal);
        Assert.True(
            markCommandIndex >= 0 && firstSyncLoopIndex > markCommandIndex,
            "The canonical status preparation must run even when the first seed sync attempt succeeds.");
    }

    [Fact]
    public async Task ReconcileAsync_UnknownBillingStatusNormalization_FailsClosedAndPreservesExplicitWarningAndBundleAmounts()
    {
        var databasePath = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"seed-retry-rental-asset-unknown-status-guards-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var nowUtc = new DateTime(2026, 8, 5, 3, 30, 0, DateTimeKind.Utc);

        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var safeId = Guid.NewGuid();
            var acknowledgedReplayId = Guid.NewGuid();
            var explicitTargetId = Guid.NewGuid();
            var profileLinkedId = Guid.NewGuid();
            var templateReferencedId = Guid.NewGuid();
            var dirtyId = Guid.NewGuid();
            var preparedOutboxId = Guid.NewGuid();
            var failedOutboxId = Guid.NewGuid();
            var sentOutboxId = Guid.NewGuid();
            var revisionMismatchId = Guid.NewGuid();
            var outsideTenantId = Guid.NewGuid();
            var outsideResponsibleOfficeId = Guid.NewGuid();
            var zeroRevisionId = Guid.NewGuid();
            var nonOperatingId = Guid.NewGuid();
            var negativeFeeId = Guid.NewGuid();
            var exclusionReasonId = Guid.NewGuid();
            var unrelatedBundleAssetId = Guid.NewGuid();
            var bundleProfileId = Guid.NewGuid();

            var assets = new[]
            {
                CreateUnknownBillingStatusAsset(safeId),
                CreateUnknownBillingStatusAsset(acknowledgedReplayId, revision: 52),
                CreateUnknownBillingStatusAsset(explicitTargetId, billingEligibilityStatus: "청구대상"),
                CreateUnknownBillingStatusAsset(profileLinkedId, billingProfileId: bundleProfileId),
                CreateUnknownBillingStatusAsset(templateReferencedId),
                CreateUnknownBillingStatusAsset(dirtyId, isDirty: true),
                CreateUnknownBillingStatusAsset(preparedOutboxId, revision: 61),
                CreateUnknownBillingStatusAsset(failedOutboxId, revision: 62),
                CreateUnknownBillingStatusAsset(sentOutboxId, revision: 63),
                CreateUnknownBillingStatusAsset(revisionMismatchId, revision: 64),
                CreateUnknownBillingStatusAsset(
                    outsideTenantId,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld),
                CreateUnknownBillingStatusAsset(
                    outsideResponsibleOfficeId,
                    responsibleOfficeCode: OfficeCodeCatalog.Yeonsu),
                CreateUnknownBillingStatusAsset(zeroRevisionId, revision: 0),
                CreateUnknownBillingStatusAsset(nonOperatingId, assetStatus: RentalAssetStatusNormalizer.Warehouse),
                CreateUnknownBillingStatusAsset(negativeFeeId, monthlyFee: -1m),
                CreateUnknownBillingStatusAsset(
                    exclusionReasonId,
                    billingExclusionReason: "부분 설정 사유")
            };
            db.RentalAssets.AddRange(assets);

            var bundleTemplateJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    ItemName = "보호 묶음 항목",
                    Quantity = 2m,
                    UnitPrice = 165_000m,
                    Amount = 330_000m,
                    IncludedAssetIds = new[] { templateReferencedId, unrelatedBundleAssetId }
                }
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = bundleProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"BUNDLE-{bundleProfileId:N}",
                BillingType = "묶음",
                MonthlyAmount = 330_000m,
                BillingTemplateJson = bundleTemplateJson,
                IsActive = true,
                IsDirty = false,
                Revision = 70
            });
            db.SyncOutboxEntries.AddRange(
                CreateOutbox(acknowledgedReplayId, "Acknowledged", expectedRevision: 52),
                CreateOutbox(preparedOutboxId, "Prepared", expectedRevision: 61),
                CreateOutbox(failedOutboxId, "Failed", expectedRevision: 62),
                CreateOutbox(sentOutboxId, "Sent", expectedRevision: 63),
                CreateOutbox(revisionMismatchId, "Acknowledged", expectedRevision: 63));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var beforeAssetsWithoutStatus = (await db.RentalAssets.AsNoTracking().ToListAsync())
                .ToDictionary(asset => asset.Id, SerializeAssetWithoutBillingStatus);
            var beforeStatuses = (await db.RentalAssets.AsNoTracking().ToListAsync())
                .ToDictionary(asset => asset.Id, asset => asset.BillingEligibilityStatus);
            var beforeProfile = JsonSerializer.Serialize(await db.RentalBillingProfiles.AsNoTracking().SingleAsync());
            var beforeOutbox = JsonSerializer.Serialize(await db.SyncOutboxEntries.AsNoTracking()
                .OrderBy(entry => entry.Id)
                .ToListAsync());

            var normalizedCount = await IsolatedSeedRentalAssetBillingStatusNormalizer.NormalizeAsync(db);

            Assert.Equal(2, normalizedCount);
            db.ChangeTracker.Clear();
            var storedAssets = (await db.RentalAssets.AsNoTracking().ToListAsync())
                .ToDictionary(asset => asset.Id);
            Assert.Equal("미확인", storedAssets[safeId].BillingEligibilityStatus);
            Assert.Equal("미확인", storedAssets[acknowledgedReplayId].BillingEligibilityStatus);

            var excludedIds = new[]
            {
                explicitTargetId,
                profileLinkedId,
                templateReferencedId,
                dirtyId,
                preparedOutboxId,
                failedOutboxId,
                sentOutboxId,
                revisionMismatchId,
                outsideTenantId,
                outsideResponsibleOfficeId,
                zeroRevisionId,
                nonOperatingId,
                negativeFeeId,
                exclusionReasonId
            };
            Assert.All(excludedIds, id =>
                Assert.Equal(beforeStatuses[id], storedAssets[id].BillingEligibilityStatus));
            Assert.All(storedAssets, pair =>
                Assert.Equal(beforeAssetsWithoutStatus[pair.Key], SerializeAssetWithoutBillingStatus(pair.Value)));
            Assert.Equal(beforeProfile, JsonSerializer.Serialize(
                await db.RentalBillingProfiles.AsNoTracking().SingleAsync()));
            Assert.Equal(beforeOutbox, JsonSerializer.Serialize(await db.SyncOutboxEntries.AsNoTracking()
                .OrderBy(entry => entry.Id)
                .ToListAsync()));

            var scan = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());
            Assert.Contains(scan.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee &&
                issue.EntityId == explicitTargetId);

            var afterFirstRun = JsonSerializer.Serialize(await db.RentalAssets.AsNoTracking()
                .OrderBy(asset => asset.Id)
                .ToListAsync());
            var replayCount = await IsolatedSeedRentalAssetBillingStatusNormalizer.NormalizeAsync(db);
            Assert.Equal(0, replayCount);
            db.ChangeTracker.Clear();
            Assert.Equal(afterFirstRun, JsonSerializer.Serialize(await db.RentalAssets.AsNoTracking()
                .OrderBy(asset => asset.Id)
                .ToListAsync()));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ReconcileAsync_UnlinksOnlyExcludedZeroFeeMissingExplicitAsset_AndPreservesLineage()
    {
        var databasePath = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"seed-retry-rental-asset-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var nowUtc = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var candidateId = Guid.NewGuid();
            var coveredAssetId = Guid.NewGuid();
            var historyId = Guid.NewGuid();
            var unrelatedOutboxId = Guid.NewGuid();
            var templateJson = JsonSerializer.Serialize(new[]
            {
                new { IncludedAssetIds = new[] { coveredAssetId } }
            });

            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = "USENET_GROUP",
                OfficeCode = "USENET",
                ResponsibleOfficeCode = "USENET",
                ProfileKey = $"PROFILE-{profileId:N}",
                CustomerId = customerId,
                CustomerName = "테스트 거래처",
                ItemName = "렌탈 묶음",
                BillingTemplateJson = templateJson,
                IsActive = true,
                IsDirty = false,
                Revision = 41
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = candidateId,
                TenantCode = "USENET_GROUP",
                OfficeCode = "USENET",
                ResponsibleOfficeCode = "USENET",
                AssetKey = $"ASSET-{candidateId:N}",
                BillingProfileId = profileId,
                CustomerId = customerId,
                CustomerName = "테스트 거래처",
                CurrentCustomerName = "테스트 거래처",
                InstallLocation = "테스트 설치처",
                InstallSiteName = "테스트 설치처",
                BillingEligibilityStatus = "청구제외",
                BillingExclusionReason = "사용자 제외",
                MonthlyFee = 0m,
                ItemName = "테스트 장비",
                MachineNumber = "MACHINE-1",
                ManagementNumber = "MANAGEMENT-1",
                IsDirty = true,
                Revision = 42
            });
            db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
            {
                Id = historyId,
                AssetId = candidateId,
                BillingProfileId = profileId,
                CustomerId = customerId,
                TenantCode = "USENET_GROUP",
                ResponsibleOfficeCode = "USENET",
                CustomerName = "테스트 거래처",
                InstallLocation = "테스트 설치처",
                BillingProfileDisplay = "테스트 거래처 · 렌탈 묶음",
                ItemName = "테스트 장비",
                MachineNumber = "MACHINE-1",
                ManagementNumber = "MANAGEMENT-1",
                MonthlyFee = 0m,
                IsCurrent = true,
                LinkedAtUtc = nowUtc.AddYears(-1),
                IsDirty = true,
                Revision = 43
            });
            db.SyncOutboxEntries.AddRange(
                CreateFailedOutbox(nameof(LocalRentalAsset), candidateId),
                CreateFailedOutbox(nameof(LocalRentalAssetAssignmentHistory), historyId),
                CreateFailedOutbox(nameof(LocalRentalAsset), unrelatedOutboxId));
            await db.SaveChangesAsync();

            var result = await IsolatedSeedRetryRentalAssetReconciler
                .ReconcileAsync(db, nowUtc);

            Assert.Equal(1, result.UnlinkedAssets);
            Assert.Equal(1, result.ClosedAssignmentHistories);
            Assert.Equal(2, result.RemovedStaleOutbox);

            db.ChangeTracker.Clear();
            var storedAsset = await db.RentalAssets
                .IgnoreQueryFilters()
                .SingleAsync(asset => asset.Id == candidateId);
            Assert.Null(storedAsset.BillingProfileId);
            Assert.Null(storedAsset.CustomerId);
            Assert.Equal(profileId, storedAsset.LastBillingProfileId);
            Assert.Equal("테스트 거래처", storedAsset.LastCustomerName);
            Assert.Equal("테스트 설치처", storedAsset.LastInstallLocation);
            Assert.Equal("테스트 거래처 · 렌탈 묶음", storedAsset.LastBillingProfileDisplay);
            Assert.Equal(nowUtc, storedAsset.LastAssignmentClearedAtUtc);
            Assert.Equal("청구제외", storedAsset.BillingEligibilityStatus);
            Assert.Equal("사용자 제외", storedAsset.BillingExclusionReason);
            Assert.True(storedAsset.IsDirty);
            Assert.Equal(nowUtc, storedAsset.UpdatedAtUtc);

            var storedHistory = await db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .SingleAsync(history => history.Id == historyId);
            Assert.False(storedHistory.IsCurrent);
            Assert.Equal(nowUtc, storedHistory.UnlinkedAtUtc);
            Assert.Equal(
                IsolatedSeedRetryRentalAssetReconciler.AssignmentChangeReason,
                storedHistory.ChangeReason);
            Assert.True(storedHistory.IsDirty);
            Assert.Equal(nowUtc, storedHistory.UpdatedAtUtc);

            var remainingOutbox = await db.SyncOutboxEntries
                .AsNoTracking()
                .Select(entry => new { entry.EntityName, entry.EntityId })
                .ToListAsync();
            var remaining = Assert.Single(remainingOutbox);
            Assert.Equal(nameof(LocalRentalAsset), remaining.EntityName);
            Assert.Equal(unrelatedOutboxId, remaining.EntityId);

            var storedProfile = await db.RentalBillingProfiles
                .AsNoTracking()
                .SingleAsync(profile => profile.Id == profileId);
            Assert.Equal(templateJson, storedProfile.BillingTemplateJson);
            Assert.False(storedProfile.IsDirty);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ReconcileAsync_FailsClosedForUnsafeCandidateStates()
    {
        var databasePath = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"seed-retry-rental-asset-guards-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var nowUtc = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var fixtures = new List<UnsafeFixture>();
            fixtures.Add(AddUnsafeFixture(
                db,
                nowUtc,
                templateFactory: assetId => SerializeTemplate(assetId)));
            fixtures.Add(AddUnsafeFixture(
                db,
                nowUtc,
                billingEligibilityStatus: "청구가능"));
            fixtures.Add(AddUnsafeFixture(db, nowUtc, monthlyFee: 1m));
            fixtures.Add(AddUnsafeFixture(
                db,
                nowUtc,
                templateFactory: _ => "{"));
            fixtures.Add(AddUnsafeFixture(
                db,
                nowUtc,
                templateFactory: assetId => SerializeTemplate(assetId, assetId)));
            fixtures.Add(AddUnsafeFixture(
                db,
                nowUtc,
                templateFactory: _ => "[]"));
            fixtures.Add(AddUnsafeFixture(db, nowUtc, profileActive: false));
            fixtures.Add(AddUnsafeFixture(db, nowUtc, profileDirty: true));
            fixtures.Add(AddUnsafeFixture(
                db,
                nowUtc,
                assetTenantCode: "ITWORLD"));
            fixtures.Add(AddUnsafeFixture(db, nowUtc, currentHistoryCount: 0));
            fixtures.Add(AddUnsafeFixture(db, nowUtc, currentHistoryCount: 2));
            fixtures.Add(AddUnsafeFixture(db, nowUtc, currentHistoryDirty: false));
            await db.SaveChangesAsync();
            var outboxCountBefore = await db.SyncOutboxEntries.CountAsync();

            var result = await IsolatedSeedRetryRentalAssetReconciler
                .ReconcileAsync(db, nowUtc);

            Assert.Equal(0, result.UnlinkedAssets);
            Assert.Equal(0, result.ClosedAssignmentHistories);
            Assert.Equal(0, result.RemovedStaleOutbox);

            db.ChangeTracker.Clear();
            var fixtureAssetIds = fixtures.Select(fixture => fixture.AssetId).ToList();
            var storedAssets = await db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => fixtureAssetIds.Contains(asset.Id))
                .ToListAsync();
            Assert.Equal(fixtures.Count, storedAssets.Count);
            Assert.All(
                storedAssets,
                asset => Assert.True(asset.BillingProfileId.HasValue));

            var fixtureHistoryIds = fixtures
                .SelectMany(fixture => fixture.HistoryIds)
                .ToList();
            var storedHistories = await db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(history => fixtureHistoryIds.Contains(history.Id))
                .ToListAsync();
            Assert.Equal(fixtureHistoryIds.Count, storedHistories.Count);
            Assert.All(storedHistories, history => Assert.True(history.IsCurrent));
            Assert.All(storedHistories, history => Assert.Null(history.UnlinkedAtUtc));
            Assert.Equal(outboxCountBefore, await db.SyncOutboxEntries.CountAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ReconcileAsync_RejectsNonUtcTimestampBeforeMutation()
    {
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var db = new LocalDbContext(options);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            IsolatedSeedRetryRentalAssetReconciler.ReconcileAsync(
                db,
                new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Unspecified)));
    }

    private static UnsafeFixture AddUnsafeFixture(
        LocalDbContext db,
        DateTime nowUtc,
        Func<Guid, string>? templateFactory = null,
        string billingEligibilityStatus = "청구제외",
        decimal monthlyFee = 0m,
        bool profileActive = true,
        bool profileDirty = false,
        string assetTenantCode = "USENET_GROUP",
        int currentHistoryCount = 1,
        bool currentHistoryDirty = true)
    {
        var profileId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var historyIds = new List<Guid>();
        var templateJson = templateFactory?.Invoke(assetId) ??
            SerializeTemplate(Guid.NewGuid());

        db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
        {
            Id = profileId,
            TenantCode = "USENET_GROUP",
            OfficeCode = "USENET",
            ResponsibleOfficeCode = "USENET",
            ProfileKey = $"PROFILE-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "보호 대상 거래처",
            ItemName = "보호 대상 묶음",
            BillingTemplateJson = templateJson,
            IsActive = profileActive,
            IsDirty = profileDirty,
            Revision = 100
        });
        db.RentalAssets.Add(new LocalRentalAsset
        {
            Id = assetId,
            TenantCode = assetTenantCode,
            OfficeCode = "USENET",
            ResponsibleOfficeCode = "USENET",
            AssetKey = $"ASSET-{assetId:N}",
            BillingProfileId = profileId,
            CustomerId = customerId,
            CustomerName = "보호 대상 거래처",
            CurrentCustomerName = "보호 대상 거래처",
            InstallLocation = "보호 대상 설치처",
            BillingEligibilityStatus = billingEligibilityStatus,
            MonthlyFee = monthlyFee,
            IsDirty = true,
            Revision = 101
        });
        db.SyncOutboxEntries.Add(
            CreateFailedOutbox(nameof(LocalRentalAsset), assetId));

        for (var index = 0; index < currentHistoryCount; index++)
        {
            var historyId = Guid.NewGuid();
            historyIds.Add(historyId);
            db.RentalAssetAssignmentHistories.Add(
                new LocalRentalAssetAssignmentHistory
                {
                    Id = historyId,
                    AssetId = assetId,
                    BillingProfileId = profileId,
                    CustomerId = customerId,
                    TenantCode = assetTenantCode,
                    ResponsibleOfficeCode = "USENET",
                    CustomerName = "보호 대상 거래처",
                    InstallLocation = "보호 대상 설치처",
                    IsCurrent = true,
                    LinkedAtUtc = nowUtc.AddYears(-1).AddMinutes(index),
                    IsDirty = currentHistoryDirty,
                    Revision = 102 + index
                });
            db.SyncOutboxEntries.Add(
                CreateFailedOutbox(
                    nameof(LocalRentalAssetAssignmentHistory),
                    historyId));
        }

        return new UnsafeFixture(assetId, historyIds);
    }

    private static string SerializeTemplate(params Guid[] includedAssetIds)
        => JsonSerializer.Serialize(new[] { new { IncludedAssetIds = includedAssetIds } });

    private static LocalRentalAsset CreateUnknownBillingStatusAsset(
        Guid id,
        string billingEligibilityStatus = "",
        Guid? billingProfileId = null,
        bool isDirty = false,
        long revision = 51,
        string tenantCode = TenantScopeCatalog.UsenetGroup,
        string officeCode = OfficeCodeCatalog.Usenet,
        string responsibleOfficeCode = OfficeCodeCatalog.Usenet,
        string assetStatus = RentalAssetStatusNormalizer.Active,
        decimal monthlyFee = 0m,
        string billingExclusionReason = "")
        => new()
        {
            Id = id,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            AssetKey = $"UNKNOWN-STATUS-{id:N}",
            BillingProfileId = billingProfileId,
            AssetStatus = assetStatus,
            BillingEligibilityStatus = billingEligibilityStatus,
            BillingExclusionReason = billingExclusionReason,
            MonthlyFee = monthlyFee,
            ItemName = "보호 장비",
            MachineNumber = $"M-{id:N}",
            ManagementNumber = $"G-{id:N}",
            IsDirty = isDirty,
            Revision = revision,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    private static string SerializeAssetWithoutBillingStatus(LocalRentalAsset asset)
        => JsonSerializer.Serialize(
            typeof(LocalRentalAsset)
                .GetProperties()
                .Where(property => property.Name != nameof(LocalRentalAsset.BillingEligibilityStatus))
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToDictionary(
                    property => property.Name,
                    property => property.GetValue(asset)));

    private static LocalSyncOutboxEntry CreateFailedOutbox(
        string entityName,
        Guid entityId)
        => new()
        {
            EntityName = entityName,
            EntityId = entityId,
            MutationId = $"fixture:{entityName}:{entityId:N}",
            Status = "Failed"
        };

    private static LocalSyncOutboxEntry CreateOutbox(
        Guid entityId,
        string status,
        long expectedRevision)
        => new()
        {
            EntityName = nameof(LocalRentalAsset),
            EntityId = entityId,
            MutationId = $"unknown-status:{entityId:N}:{status}",
            Status = status,
            ExpectedRevision = expectedRevision
        };

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

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "tools", "SyncDiag", "Program.cs")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record UnsafeFixture(
        Guid AssetId,
        IReadOnlyList<Guid> HistoryIds);
}
