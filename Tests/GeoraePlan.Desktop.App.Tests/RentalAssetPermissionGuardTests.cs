using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetPermissionGuardTests
{
    [Fact]
    public async Task SaveAssetAsync_DeniesUserWithoutRentalAssetEditAndDoesNotCreateDirtyAsset()
    {
        PrepareAppRoot("georaeplan-rental-asset-save-permission");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateOfficeSession();
            var asset = CreateAsset(Guid.NewGuid());

            var result = await new RentalStateService(db).SaveAssetAsync(asset, session, allowCategoryRecovery: true);

            Assert.False(result.Success);
            Assert.Contains("권한", result.Message, StringComparison.Ordinal);
            Assert.Empty(await db.RentalAssets.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteAssetAsync_DeniesUserWithoutRentalAssetEditAndLeavesAssetClean()
    {
        PrepareAppRoot("georaeplan-rental-asset-delete-permission");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateAsset(assetId));
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).DeleteAssetAsync(assetId, CreateOfficeSession());

            Assert.False(result.Success);
            Assert.Contains("권한", result.Message, StringComparison.Ordinal);
            var stored = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            Assert.False(stored.IsDeleted);
            Assert.False(stored.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAssetAsync_DeniesActiveProfileTemplateReferenceWithoutChanges(bool crossTenant)
    {
        PrepareAppRoot($"georaeplan-rental-asset-delete-template-reference-{crossTenant}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var asset = CreateAsset(assetId);
            if (crossTenant)
            {
                asset.TenantCode = TenantScopeCatalog.Itworld;
                asset.OfficeCode = OfficeCodeCatalog.Itworld;
                asset.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
                asset.ManagementCompanyCode = OfficeCodeCatalog.Itworld;
            }

            var templateJson = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new { IncludedAssetIds = new[] { assetId } }
            });
            db.RentalAssets.Add(asset);
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"DELETE-GUARD-{profileId:N}",
                CustomerName = "Delete Guard Customer",
                ItemName = "Delete Guard Item",
                BillingTemplateJson = templateJson,
                BillingDay = 25,
                MonthlyAmount = 100_000m,
                IsActive = true,
                IsDeleted = false,
                IsDirty = false
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var result = await new RentalStateService(db).DeleteAssetAsync(
                assetId,
                CreateAdminSession());

            Assert.False(result.Success);
            Assert.Contains("\uCCAD\uAD6C\uAD00\uB9AC", result.Message, StringComparison.Ordinal);
            var unchangedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == assetId);
            Assert.False(unchangedAsset.IsDeleted);
            Assert.False(unchangedAsset.IsDirty);
            var unchangedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.False(unchangedProfile.IsDirty);
            Assert.Equal(templateJson, unchangedProfile.BillingTemplateJson);
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("malformed-d")]
    [InlineData("malformed-n")]
    [InlineData("duplicate-property")]
    [InlineData("object-wrapper-b")]
    [InlineData("object-wrapper-p")]
    [InlineData("unparseable-unicode-d")]
    [InlineData("unparseable-unicode-n")]
    [InlineData("unparseable-unicode-b")]
    [InlineData("unparseable-unicode-p")]
    [InlineData("unclosed-d")]
    [InlineData("unclosed-n")]
    [InlineData("unclosed-b")]
    [InlineData("unclosed-p")]
    [InlineData("unclosed-unicode-d")]
    [InlineData("unclosed-unicode-n")]
    [InlineData("unclosed-unicode-b")]
    [InlineData("unclosed-unicode-p")]
    [InlineData("encoded-property-d")]
    [InlineData("encoded-property-middle-n")]
    public async Task DeleteAssetAsync_DeniesMalformedTemplateContainingTargetGuidWithoutChanges(
        string templateKind)
    {
        PrepareAppRoot($"georaeplan-rental-asset-delete-malformed-reference-{templateKind}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var guidFormat = templateKind.EndsWith("-n", StringComparison.Ordinal) ? "N" :
                templateKind.EndsWith("-b", StringComparison.Ordinal) ? "B" :
                templateKind.EndsWith("-p", StringComparison.Ordinal) ? "P" :
                "D";
            var formattedAssetId = assetId.ToString(guidFormat);
            var unicodeEscapedAssetId = string.Concat(
                formattedAssetId.Select(character => $"\\u{(int)character:x4}"));
            var templateJson = templateKind switch
            {
                "malformed-d" => $"[{{\"IncludedAssetIds\":[\"{assetId:D}\"]",
                "malformed-n" => $"[{{\"IncludedAssetIds\":[\"{assetId:N}\"]",
                "duplicate-property" => $"[{{\"IncludedAssetIds\":[\"{assetId:D}\"],\"includedassetids\":[\"{assetId:D}\"]}}]",
                "object-wrapper-b" => $"[{{\"IncludedAssetIds\":{{\"$values\":[\"{assetId:B}\"]}}}}]",
                "object-wrapper-p" => $"[{{\"IncludedAssetIds\":{{\"0\":\"{assetId:P}\"}}}}]",
                "unparseable-unicode-d" or
                "unparseable-unicode-n" or
                "unparseable-unicode-b" or
                "unparseable-unicode-p" => $"[{{\"IncludedAssetIds\":[\"{unicodeEscapedAssetId}\"]",
                "unclosed-d" or
                "unclosed-n" or
                "unclosed-b" or
                "unclosed-p" => $"[{{\"IncludedAssetIds\":[\"{formattedAssetId}",
                "encoded-property-d" => $"[{{\"\\u0049ncludedAssetIds\":[\"{assetId:D}\"]",
                "encoded-property-middle-n" => $"[{{\"Incl\\u0075dedAssetIds\":[\"{assetId:N}\"]",
                _ => $"[{{\"IncludedAssetIds\":[\"{unicodeEscapedAssetId}"
            };
            db.AddRange(
                CreateAsset(assetId),
                CreateActiveBillingProfile(profileId, templateJson));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var result = await new RentalStateService(db).DeleteAssetAsync(assetId, CreateAdminSession());

            Assert.False(result.Success);
            Assert.Contains("\uCCAD\uAD6C\uAD00\uB9AC", result.Message, StringComparison.Ordinal);
            var unchangedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == assetId);
            Assert.False(unchangedAsset.IsDeleted);
            Assert.False(unchangedAsset.IsDirty);
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAssetAsync_AllowsUnparseableTemplateWhenTargetGuidIsOnlyInUnrelatedField(
        bool unicodeEscaped)
    {
        PrepareAppRoot($"georaeplan-rental-asset-delete-unrelated-target-guid-{unicodeEscaped}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            var targetText = assetId.ToString("D");
            if (unicodeEscaped)
            {
                targetText = string.Concat(
                    targetText.Select(character => $"\\u{(int)character:x4}"));
            }

            db.AddRange(
                CreateAsset(assetId),
                CreateActiveBillingProfile(
                    Guid.NewGuid(),
                    $"[{{\"Memo\":\"{targetText}\",\"IncludedAssetIds\":["));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var result = await new RentalStateService(db).DeleteAssetAsync(assetId, CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var deletedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == assetId);
            Assert.True(deletedAsset.IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteAssetAsync_AllowsMalformedTemplateWithEscapedBackslashUnicodeLiteral()
    {
        PrepareAppRoot("georaeplan-rental-asset-delete-escaped-unicode-literal");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            var unicodeEscapedAssetId = string.Concat(
                assetId.ToString("D").Select(character => $"\\u{(int)character:x4}"));
            var escapedBackslashLiteral = unicodeEscapedAssetId.Replace("\\u", "\\\\u", StringComparison.Ordinal);
            db.AddRange(
                CreateAsset(assetId),
                CreateActiveBillingProfile(
                    Guid.NewGuid(),
                    $"[{{\"IncludedAssetIds\":[\"{escapedBackslashLiteral}\"]"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var result = await new RentalStateService(db).DeleteAssetAsync(assetId, CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var deletedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == assetId);
            Assert.True(deletedAsset.IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("unicode-backslash-boundary")]
    [InlineData("unicode-quote-fake-property")]
    [InlineData("escaped-quote-fake-property")]
    [InlineData("escaped-unicode-property-literal")]
    [InlineData("prefixed-d")]
    [InlineData("extended-n")]
    public async Task DeleteAssetAsync_AllowsMalformedTemplateWithoutExactIncludedAssetReference(
        string templateKind)
    {
        PrepareAppRoot($"georaeplan-rental-asset-delete-malformed-non-reference-{templateKind}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            var otherAssetId = Guid.NewGuid();
            var templateJson = templateKind switch
            {
                "unicode-backslash-boundary" =>
                    $"[{{\"IncludedAssetIds\":[\"{otherAssetId:D}\\u005c\"],\"Memo\":\"{assetId:D}\"",
                "unicode-quote-fake-property" =>
                    $"[{{\"Memo\":\"\\u0022IncludedAssetIds\\u0022:[\\\"{assetId:D}\"",
                "escaped-quote-fake-property" =>
                    $"[{{\"Memo\":\"\\\"IncludedAssetIds\\\":[\\\"{assetId:D}\"",
                "escaped-unicode-property-literal" =>
                    $"[{{\"\\\\u0049ncludedAssetIds\":[\"{assetId:D}\"]",
                "prefixed-d" =>
                    $"[{{\"IncludedAssetIds\":[\"archive-{{{assetId:D}}}-old\"]",
                _ =>
                    $"[{{\"IncludedAssetIds\":[\"f{assetId:N}a\"]"
            };
            db.AddRange(
                CreateAsset(assetId),
                CreateActiveBillingProfile(Guid.NewGuid(), templateJson));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var result = await new RentalStateService(db).DeleteAssetAsync(assetId, CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var deletedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == assetId);
            Assert.True(deletedAsset.IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteAssetAsync_AllowsUnrelatedMalformedTemplate()
    {
        PrepareAppRoot("georaeplan-rental-asset-delete-unrelated-malformed-template");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            db.AddRange(
                CreateAsset(assetId),
                CreateActiveBillingProfile(
                    Guid.NewGuid(),
                    $"[{{\"IncludedAssetIds\":[\"{Guid.NewGuid():D}\"]"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var result = await new RentalStateService(db).DeleteAssetAsync(assetId, CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var deletedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == assetId);
            Assert.True(deletedAsset.IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteAssetAsync_AllowsDeleteAfterProfileTemplateReferenceIsRemoved()
    {
        PrepareAppRoot("georaeplan-rental-asset-delete-after-template-unlink");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var asset = CreateAsset(assetId);
            asset.BillingProfileId = profileId;
            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"DELETE-AFTER-UNLINK-{profileId:N}",
                CustomerName = "Delete After Unlink Customer",
                ItemName = "Delete After Unlink Item",
                BillingTemplateJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new { IncludedAssetIds = new[] { assetId } }
                }),
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 100_000m,
                IsActive = true,
                IsDeleted = false,
                IsDirty = false
            };
            db.AddRange(asset, profile);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var editedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            editedProfile.BillingTemplateJson = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new { IncludedAssetIds = Array.Empty<Guid>() }
            });
            var session = CreateAdminSession();
            var service = new RentalStateService(db);
            var profileSave = await service.SaveBillingProfileAsync(editedProfile, session);
            Assert.True(profileSave.Success, profileSave.Message);

            var deleteResult = await service.DeleteAssetAsync(assetId, session);

            Assert.True(deleteResult.Success, deleteResult.Message);
            var deletedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == assetId);
            Assert.True(deletedAsset.IsDeleted);
            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(
                RentalBillingTemplateAssetCoverage.NoExplicitCoverage,
                RentalBillingTemplateAssetCoverageRules.Evaluate(storedProfile.BillingTemplateJson, assetId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RentalAssetViewModel_CreateAndSaveCommandsFollowRentalAssetEditPermission()
    {
        PrepareAppRoot("georaeplan-rental-asset-command-permission");

        try
        {
            using var blockedDb = new LocalDbContext();
            using var allowedDb = new LocalDbContext();
            var blockedSession = CreateOfficeSession();
            var allowedSession = CreateOfficeSession(AppPermissionNames.RentalAssetEdit);

            var blockedViewModel = CreateViewModel(blockedDb, blockedSession);
            var allowedViewModel = CreateViewModel(allowedDb, allowedSession);

            Assert.False(blockedViewModel.CanCreateAsset);
            Assert.False(blockedViewModel.CanSelectAssetsForMutation);
            Assert.False(blockedViewModel.CanEditAssetDetails);
            Assert.False(blockedViewModel.CanSave);
            Assert.False(blockedViewModel.NewAssetCommand.CanExecute(null));
            Assert.False(blockedViewModel.SaveCommand.CanExecute(null));
            Assert.Contains("업체·지점 공유 조회", blockedViewModel.RentalScopeGuidanceText, StringComparison.Ordinal);
            Assert.Contains("조회 전용", blockedViewModel.RentalScopeGuidanceText, StringComparison.Ordinal);
            Assert.Contains("저장·삭제할 수 없습니다", blockedViewModel.RentalScopeGuidanceText, StringComparison.Ordinal);

            Assert.True(allowedViewModel.CanCreateAsset);
            Assert.True(allowedViewModel.CanSelectAssetsForMutation);
            Assert.True(allowedViewModel.CanEditAssetDetails);
            Assert.True(allowedViewModel.CanSave);
            Assert.True(allowedViewModel.NewAssetCommand.CanExecute(null));
            Assert.True(allowedViewModel.SaveCommand.CanExecute(null));
            Assert.Contains("업체·지점 공유 조회", allowedViewModel.RentalScopeGuidanceText, StringComparison.Ordinal);
            Assert.Contains("권한 있는 담당 범위", allowedViewModel.RentalScopeGuidanceText, StringComparison.Ordinal);
            Assert.DoesNotContain("조회 전용", allowedViewModel.RentalScopeGuidanceText, StringComparison.Ordinal);

            SetSelectedRowWithoutLoading(allowedViewModel, new RentalAssetViewRow
            {
                Source = CreateAsset(Guid.NewGuid()),
                HasFullDetail = false
            });
            Assert.False(allowedViewModel.CanEditAssetDetails);

            SetSelectedRowWithoutLoading(allowedViewModel, new RentalAssetViewRow
            {
                Source = CreateAsset(Guid.NewGuid()),
                HasFullDetail = true
            });
            Assert.True(allowedViewModel.CanEditAssetDetails);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static RentalAssetViewModel CreateViewModel(LocalDbContext db, SessionState session)
    {
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var rental = new RentalStateService(db, local);
        return new RentalAssetViewModel(rental, local, new RentalDocumentService(), null!, session);
    }

    private static void SetSelectedRowWithoutLoading(
        RentalAssetViewModel viewModel,
        RentalAssetViewRow row)
    {
        var field = typeof(RentalAssetViewModel).GetField(
            "_selectedRow",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(viewModel, row);
    }

    private static LocalRentalAsset CreateAsset(Guid assetId)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = $"PERMISSION-ASSET-{assetId:N}",
            ManagementNumber = $"PERM-{assetId:N}",
            ManagementId = $"PERM-ID-{assetId:N}",
            MachineNumber = $"PERM-SN-{assetId:N}",
            CustomerName = "권한 테스트 거래처",
            CurrentCustomerName = "권한 테스트 거래처",
            InstallLocation = "본관",
            InstallSiteName = "본관",
            ItemCategoryName = "복합기",
            ItemName = "권한 테스트 장비",
            AssetStatus = "임대진행중",
            BillingEligibilityStatus = "미확인",
            IsDirty = false,
            IsDeleted = false
        };

    private static LocalRentalBillingProfile CreateActiveBillingProfile(Guid profileId, string templateJson)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"DELETE-MALFORMED-GUARD-{profileId:N}",
            CustomerName = "Delete Malformed Guard Customer",
            ItemName = "Delete Malformed Guard Item",
            BillingTemplateJson = templateJson,
            BillingDay = 25,
            MonthlyAmount = 100_000m,
            IsActive = true,
            IsDeleted = false,
            IsDirty = false
        };

    private static SessionState CreateOfficeSession(params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"rental-asset-permission-{Guid.NewGuid():N}",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"rental-asset-admin-{Guid.NewGuid():N}",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
