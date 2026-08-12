using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetAutoBillingProfileScopeTests
{
    [Fact]
    public async Task SaveAsset_AutoLinksBillingProfileWhenTenantAndOfficeMatch()
    {
        PrepareAppRoot("georaeplan-rental-asset-auto-profile-positive");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "USENET 자동연결 거래처",
                NameMatchKey = "USENET 자동연결 거래처",
                BusinessNumber = "333-33-33333",
                IsDeleted = false
            };
            var profile = CreateBillingProfile(
                customer.Id,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet);

            db.Customers.Add(customer);
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var asset = CreateAsset(customer);
            var session = CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);

            var result = await new RentalStateService(db).SaveAssetAsync(
                asset,
                session,
                allowCategoryRecovery: true);

            Assert.True(result.Success, result.Message);
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == asset.Id);
            Assert.Equal(profile.Id, storedAsset.BillingProfileId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveAsset_DoesNotAutoLinkBillingProfileWhoseTenantConflictsWithAsset()
    {
        PrepareAppRoot("georaeplan-rental-asset-auto-profile-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "USENET 자동연결 거래처",
                NameMatchKey = "USENET 자동연결 거래처",
                BusinessNumber = "333-33-33333",
                IsDeleted = false
            };
            var conflictingProfile = CreateBillingProfile(
                customer.Id,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Usenet);

            db.Customers.Add(customer);
            db.RentalBillingProfiles.Add(conflictingProfile);
            await db.SaveChangesAsync();

            var asset = CreateAsset(customer);
            var session = CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);

            var result = await new RentalStateService(db).SaveAssetAsync(
                asset,
                session,
                allowCategoryRecovery: true);

            Assert.True(result.Success, result.Message);
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == asset.Id);
            Assert.Null(storedAsset.BillingProfileId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveAsset_DoesNotAutoLinkProfileThatAlreadyHasExplicitTemplateAssets()
    {
        PrepareAppRoot("georaeplan-rental-asset-auto-profile-explicit-template");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "USENET 자동연결 거래처",
                NameMatchKey = "USENET 자동연결 거래처",
                BusinessNumber = "333-33-33333",
                IsDeleted = false
            };
            var profile = CreateBillingProfile(
                customer.Id,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet);
            var explicitAssetId = Guid.NewGuid();
            profile.BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "기존 명시 연결 장비",
                    BillingLineMode = "묶음",
                    RepresentativeAssetId = explicitAssetId,
                    Quantity = 1m,
                    UnitPrice = 10_000m,
                    Amount = 10_000m,
                    IncludedAssetIds = [explicitAssetId]
                }
            });

            db.Customers.Add(customer);
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateLinkedAsset(customer, explicitAssetId, profile.Id));
            await db.SaveChangesAsync();

            var newAsset = CreateAsset(customer);
            var session = CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);

            var result = await new RentalStateService(db).SaveAssetAsync(
                newAsset,
                session,
                allowCategoryRecovery: true);

            Assert.True(result.Success, result.Message);
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == newAsset.Id);
            Assert.Null(storedAsset.BillingProfileId);

            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profile.Id);
            var templateItems = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(storedProfile.BillingTemplateJson) ?? [];
            var templateItem = Assert.Single(templateItems);
            Assert.Equal([explicitAssetId], templateItem.IncludedAssetIds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveAsset_AutoLinksExplicitProfileWhenAssetAppearsExactlyOnce()
    {
        PrepareAppRoot("georaeplan-rental-asset-auto-profile-explicit-exact");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Explicit exact customer",
                NameMatchKey = "EXPLICITEXACTCUSTOMER",
                BusinessNumber = "444-44-44444",
                IsDeleted = false
            };
            var asset = CreateAsset(customer);
            var profile = CreateBillingProfile(
                customer.Id,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet);
            profile.CustomerName = customer.NameOriginal;
            profile.BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new() { IncludedAssetIds = [asset.Id] }
            });

            db.Customers.Add(customer);
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).SaveAssetAsync(
                asset,
                CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet),
                allowCategoryRecovery: true);

            Assert.True(result.Success, result.Message);
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == asset.Id);
            Assert.Equal(profile.Id, storedAsset.BillingProfileId);
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
    public async Task SaveAsset_RejectsExplicitProfileWhenAssetReferenceIsAmbiguous(bool acrossMultipleLines)
    {
        PrepareAppRoot("georaeplan-rental-asset-explicit-profile-ambiguous");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Ambiguous template customer",
                NameMatchKey = "AMBIGUOUSTEMPLATECUSTOMER",
                BusinessNumber = "555-55-55555",
                IsDeleted = false
            };
            var asset = CreateAsset(customer);
            var profile = CreateBillingProfile(
                customer.Id,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet);
            profile.CustomerName = customer.NameOriginal;
            profile.BillingTemplateJson = JsonSerializer.Serialize(
                acrossMultipleLines
                    ? new List<RentalBillingTemplateItemModel>
                    {
                        new() { IncludedAssetIds = [asset.Id] },
                        new() { IncludedAssetIds = [asset.Id] }
                    }
                    :
                    [
                        new RentalBillingTemplateItemModel
                        {
                            IncludedAssetIds = [asset.Id, asset.Id]
                        }
                    ]);
            asset.BillingProfileId = profile.Id;

            db.Customers.Add(customer);
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).SaveAssetAsync(
                asset,
                CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet),
                allowCategoryRecovery: true);

            Assert.False(result.Success);
            Assert.Contains("청구관리", result.Message, StringComparison.Ordinal);
            Assert.False(await db.RentalAssets.IgnoreQueryFilters()
                .AnyAsync(current => current.Id == asset.Id));
            Assert.Empty(await db.Items.IgnoreQueryFilters().ToListAsync());
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
    public async Task SaveAsset_RejectsLeavingPreviousExplicitProfileUntilBillingManagementRemovesAsset(bool moveToAnotherProfile)
    {
        PrepareAppRoot("georaeplan-rental-asset-leave-explicit-profile");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Previous explicit profile customer",
                NameMatchKey = "PREVIOUSEXPLICITPROFILECUSTOMER",
                BusinessNumber = "666-66-66666",
                IsDeleted = false
            };
            var assetId = Guid.NewGuid();
            var previousProfile = CreateBillingProfile(
                customer.Id,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet);
            previousProfile.BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new() { IncludedAssetIds = [assetId] }
            });
            var nextProfile = CreateBillingProfile(
                customer.Id,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet);
            var storedAsset = CreateLinkedAsset(customer, assetId, previousProfile.Id);

            db.Customers.Add(customer);
            db.RentalBillingProfiles.AddRange(previousProfile, nextProfile);
            db.RentalAssets.Add(storedAsset);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var candidate = CreateLinkedAsset(customer, assetId, previousProfile.Id);
            candidate.BillingProfileId = moveToAnotherProfile ? nextProfile.Id : null;
            var result = await new RentalStateService(db).SaveAssetAsync(
                candidate,
                CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet),
                allowCategoryRecovery: true);

            Assert.False(result.Success);
            Assert.Contains("청구관리", result.Message, StringComparison.Ordinal);
            db.ChangeTracker.Clear();
            var unchangedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == assetId);
            Assert.Equal(previousProfile.Id, unchangedAsset.BillingProfileId);
            Assert.Empty(await db.Items.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("IncludedAssetIds", "IncludedAssetIds", true)]
    [InlineData("includedassetids", "IncludedAssetIds", false)]
    [InlineData("IncludedAssetIds", "INCLUDEDASSETIDS", true)]
    public void RentalBillingTemplateAssetCoverageRules_DuplicateIncludedAssetIdsPropertyIsMalformed(
        string firstPropertyName,
        string secondPropertyName,
        bool targetAppearsFirst)
    {
        var targetAssetId = Guid.NewGuid();
        var otherAssetId = Guid.NewGuid();
        var firstAssetId = targetAppearsFirst ? targetAssetId : otherAssetId;
        var secondAssetId = targetAppearsFirst ? otherAssetId : targetAssetId;
        var json =
            $"[{{\"{firstPropertyName}\":[\"{firstAssetId:D}\"],\"{secondPropertyName}\":[\"{secondAssetId:D}\"]}}]";

        Assert.Equal(
            RentalBillingTemplateAssetCoverage.MalformedTemplate,
            RentalBillingTemplateAssetCoverageRules.Evaluate(json, targetAssetId));
        Assert.False(RentalBillingTemplateAssetCoverageRules.AllowsLink(json, targetAssetId));
    }

    [Fact]
    public async Task SaveAsset_DoesNotAutoRelinkExistingAssetWhenBillingProfileIsExplicitlyCleared()
    {
        PrepareAppRoot("georaeplan-rental-asset-explicit-null-unlink");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Explicit null unlink customer",
                NameMatchKey = "EXPLICITNULLUNLINKCUSTOMER",
                BusinessNumber = "777-77-77777",
                IsDeleted = false
            };
            var profile = CreateBillingProfile(
                customer.Id,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet);
            profile.CustomerName = customer.NameOriginal;
            profile.BillingTemplateJson = "[]";
            var asset = CreateLinkedAsset(customer, Guid.NewGuid(), profile.Id);
            asset.ItemName = string.Empty;
            asset.ItemCategoryName = string.Empty;

            db.Customers.Add(customer);
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();
            var originalProfileRevision = profile.Revision;
            var originalProfileDirty = profile.IsDirty;
            db.ChangeTracker.Clear();

            var candidate = await db.RentalAssets.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == asset.Id);
            candidate.BillingProfileId = null;
            var result = await new RentalStateService(db).SaveAssetAsync(
                candidate,
                CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet),
                allowCategoryRecovery: true);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == asset.Id);
            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profile.Id);
            Assert.Null(storedAsset.BillingProfileId);
            Assert.Equal("[]", storedProfile.BillingTemplateJson);
            Assert.Equal(originalProfileRevision, storedProfile.Revision);
            Assert.Equal(originalProfileDirty, storedProfile.IsDirty);
            Assert.Empty(await db.Items.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalRentalBillingProfile CreateBillingProfile(
        Guid customerId,
        string tenantCode,
        string ownerOfficeCode,
        string responsibleOfficeCode)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = ownerOfficeCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            ManagementCompanyCode = ownerOfficeCode,
            CustomerId = customerId,
            ProfileKey = $"AUTO-LINK-PROFILE-{Guid.NewGuid():N}",
            CustomerName = "USENET 자동연결 거래처",
            BusinessNumber = "333-33-33333",
            InstallSiteName = "본관 1층",
            ItemName = "자동연결 테스트 장비",
            MonthlyAmount = 10_000m,
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            BillingMethod = "현금",
            BillingDay = 25,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = 1,
            BillingAnchorMonth = 1,
            BillingStatus = "청구중",
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
            IsActive = true,
            IsDeleted = false,
            IsDirty = false
        };

    private static LocalRentalAsset CreateAsset(LocalCustomer customer)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            CurrentCustomerName = customer.NameOriginal,
            InstallLocation = "본관 1층",
            InstallSiteName = "본관 1층",
            ItemCategoryName = "복합기",
            ItemName = "자동연결 테스트 장비",
            ManagementNumber = $"AUTO-LINK-{Guid.NewGuid():N}",
            ManagementId = $"AUTO-LINK-ID-{Guid.NewGuid():N}",
            MachineNumber = $"AUTO-LINK-SN-{Guid.NewGuid():N}",
            AssetStatus = "임대진행중",
            BillingEligibilityStatus = "대상"
        };

    private static LocalRentalAsset CreateLinkedAsset(LocalCustomer customer, Guid assetId, Guid profileId)
    {
        var asset = CreateAsset(customer);
        asset.Id = assetId;
        asset.BillingProfileId = profileId;
        asset.ManagementNumber = $"EXPLICIT-{assetId:N}";
        asset.ManagementId = $"EXPLICIT-ID-{assetId:N}";
        asset.MachineNumber = $"EXPLICIT-SN-{assetId:N}";
        return asset;
    }

    private static SessionState CreateOfficeSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"{officeCode.ToLowerInvariant()}-asset-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [AppPermissionNames.RentalAssetEdit]
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
