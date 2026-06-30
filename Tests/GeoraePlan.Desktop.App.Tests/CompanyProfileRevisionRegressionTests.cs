using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CompanyProfileRevisionRegressionTests
{
    [Fact]
    public async Task LocalStateService_SaveCompanyProfile_AllowsCleanRowSave_WhenEditorRevisionIsAheadOfLocalRow()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-company-profile-save-revision-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var createdAt = DateTime.UtcNow.AddHours(-2);
            var updatedAt = DateTime.UtcNow.AddHours(-1);
            db.CompanyProfiles.Add(new LocalCompanyProfile
            {
                Id = profileId,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ProfileName = "기존 회사설정",
                TradeName = "기존 상호",
                Representative = "기존 대표",
                Revision = 100,
                IsDirty = false,
                IsActive = true,
                IsDeleted = false,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = updatedAt
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var candidate = new LocalCompanyProfile
            {
                Id = profileId,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ProfileName = "수정 회사설정",
                TradeName = "수정 상호",
                Representative = "수정 대표",
                Revision = 200,
                IsActive = true,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = updatedAt
            };

            await service.SaveCompanyProfileAsync(candidate);

            db.ChangeTracker.Clear();
            var saved = await db.CompanyProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == profileId);
            Assert.Equal(100, saved.Revision);
            Assert.True(saved.IsDirty);
            Assert.Equal("수정 상호", saved.TradeName);
            Assert.Equal("수정 대표", saved.Representative);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_UpsertPulledCompanyProfiles_DoesNotDowngradeCleanLocalRevision()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-company-profile-revision-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = OfficeCodeCatalog.GetDefaultCompanyProfileId(OfficeCodeCatalog.Usenet);
            var localUpdatedAt = DateTime.UtcNow;
            db.CompanyProfiles.Add(new LocalCompanyProfile
            {
                Id = profileId,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ProfileName = "유즈넷 기본",
                TradeName = "로컬 최신 상호",
                Representative = "로컬 대표",
                IsActive = true,
                IsDefaultForOffice = true,
                IsDeleted = false,
                IsDirty = false,
                Revision = 200,
                CreatedAtUtc = localUpdatedAt.AddDays(-1),
                UpdatedAtUtc = localUpdatedAt
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var staleServerProfile = new CompanyProfileDto
            {
                Id = profileId,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ProfileName = "유즈넷 기본",
                TradeName = "서버 이전 상호",
                Representative = "서버 이전 대표",
                IsActive = true,
                IsDefaultForOffice = true,
                IsDeleted = false,
                Revision = 100,
                CreatedAtUtc = localUpdatedAt.AddDays(-1),
                UpdatedAtUtc = localUpdatedAt.AddHours(-1)
            };

            await InvokePrivateInstanceTaskAsync(
                sync,
                "UpsertPulledCompanyProfilesAsync",
                new object?[] { new List<CompanyProfileDto> { staleServerProfile }, CancellationToken.None });

            db.ChangeTracker.Clear();
            var current = await db.CompanyProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == profileId);
            Assert.Equal(200, current.Revision);
            Assert.Equal("로컬 최신 상호", current.TradeName);
            Assert.Equal("로컬 대표", current.Representative);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbInitializer_KeepsUsenetDefaultTradeNameInKorean()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-company-profile-default-korean-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();

            await LocalDbInitializer.InitializeAsync(db);

            db.ChangeTracker.Clear();
            var profile = await db.CompanyProfiles.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == OfficeCodeCatalog.UsenetDefaultCompanyProfileId);
            Assert.Equal(OfficeCodeCatalog.Usenet, profile.OfficeCode);
            Assert.Equal("유즈넷 기본", profile.ProfileName);
            Assert.Equal("유즈넷", profile.TradeName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalCompanyProfileMaintenance_RepairsLegacyUsenetCodeTradeName_WithoutOverwritingCustomName()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-company-profile-default-repair-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var legacyDefaultId = OfficeCodeCatalog.UsenetDefaultCompanyProfileId;
            var customId = Guid.NewGuid();
            db.CompanyProfiles.AddRange(
                new LocalCompanyProfile
                {
                    Id = legacyDefaultId,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ProfileName = "유즈넷 기본",
                    TradeName = "USENET",
                    IsDefaultForOffice = true,
                    IsActive = true,
                    IsDeleted = false,
                    IsDirty = false
                },
                new LocalCompanyProfile
                {
                    Id = customId,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ProfileName = "ITWORLD 실사용 회사설정",
                    TradeName = "ITWORLD 실제 상호",
                    IsDefaultForOffice = true,
                    IsActive = true,
                    IsDeleted = false,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            await service.EnsureCompanyProfilesHealthyAsync();

            db.ChangeTracker.Clear();
            var repaired = await db.CompanyProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == legacyDefaultId);
            var custom = await db.CompanyProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == customId);
            Assert.Equal("유즈넷", repaired.TradeName);
            Assert.Equal("ITWORLD 실제 상호", custom.TradeName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_GetCompanyProfile_IgnoresAssignedProfileFromDifferentOffice()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-company-profile-office-scope-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetProfileId = Guid.NewGuid();
            var yeonsuProfileId = Guid.NewGuid();
            db.CompanyProfiles.AddRange(
                new LocalCompanyProfile
                {
                    Id = usenetProfileId,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ProfileName = "유즈넷 기본",
                    TradeName = "유즈넷",
                    BankAccountText = "USENET-ACCOUNT",
                    IsActive = true,
                    IsDefaultForOffice = true,
                    IsDeleted = false
                },
                new LocalCompanyProfile
                {
                    Id = yeonsuProfileId,
                    OfficeCode = OfficeCodeCatalog.Yeonsu,
                    ProfileName = "YEONSU 기본",
                    TradeName = "연수",
                    BankAccountText = "YEONSU-ACCOUNT",
                    IsActive = true,
                    IsDefaultForOffice = true,
                    IsDeleted = false
                });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(
                username: "yeonsu-user",
                officeCode: OfficeCodeCatalog.Yeonsu);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            await service.SetAssignedCompanyProfileAsync("yeonsu-user", usenetProfileId);

            var profileFromSession = await service.GetCompanyProfileAsync(session);
            var profileFromExplicitOffice = await service.GetCompanyProfileAsync("yeonsu-user", OfficeCodeCatalog.Yeonsu);

            Assert.NotNull(profileFromSession);
            Assert.NotNull(profileFromExplicitOffice);
            Assert.Equal(yeonsuProfileId, profileFromSession!.Id);
            Assert.Equal(yeonsuProfileId, profileFromExplicitOffice!.Id);
            Assert.Equal("YEONSU-ACCOUNT", profileFromSession.BankAccountText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void EnvironmentSettingsViewModel_FiltersAndValidatesCompanyProfilesByOffice()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "EnvironmentSettingsViewModel.cs"));

        Assert.Contains("IsCompanyProfileVisibleToCurrentSession(profile)", source, StringComparison.Ordinal);
        Assert.Contains("TryResolveCompanyProfileForOffice(CurrentUserCompanyProfileId, _session.OfficeCode", source, StringComparison.Ordinal);
        Assert.Contains("TryResolveCompanyProfileForOffice(EditingUserCompanyProfileId, EditingUserOfficeCode", source, StringComparison.Ordinal);
        Assert.Contains("TryResolveCompanyProfileForOffice(assignedIdText, officeCode", source, StringComparison.Ordinal);
    }

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

    private static SessionState CreateOfficeUserSession(string username, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = username,
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        });
        return session;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "거래플랜.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜.sln을 찾을 수 없습니다.");
    }

    private static async Task InvokePrivateInstanceTaskAsync(object target, string methodName, params object?[]? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method!.Invoke(target, args);
        Assert.NotNull(result);
        var task = Assert.IsAssignableFrom<Task>(result);
        await task;
    }
}
