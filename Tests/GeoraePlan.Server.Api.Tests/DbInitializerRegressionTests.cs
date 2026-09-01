using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class DbInitializerRegressionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _dbContext;

    public DbInitializerRegressionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var currentUser = new TestCurrentUserContext
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new AppDbContext(options, currentUser, revisionClock);
        _dbContext.Database.EnsureCreated();
    }

    [Fact]
    public void SeedUsersOptions_UpdateExistingAdminPassword_DefaultsToFalse()
    {
        var options = new SeedUsersOptions();
        Assert.False(options.AdminOnlyBootstrap);
        Assert.False(options.UpdateExistingAdminPassword);
    }

    [Fact]
    public async Task EnsureConfiguredSeedUsersAsync_AdminOnlyBootstrapChangesOnlyAdminHash()
    {
        static UserAccount CreateUser(string username, string password) => new()
        {
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = string.Equals(username, "admin", StringComparison.Ordinal) ? "Admin" : "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = string.Equals(username, "admin", StringComparison.Ordinal)
                ? TenantScopeCatalog.ScopeAdmin
                : TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true
        };

        var admin = CreateUser("admin", "Original-Admin!9aA");
        var user = CreateUser("user", "Original-User!9aA");
        var itw = CreateUser("itw", "Original-Itw!9aA");
        var usenet = CreateUser("usenet", "Original-Usenet!9aA");
        _dbContext.Users.AddRange(admin, user, itw, usenet);
        await _dbContext.SaveChangesAsync();
        var unrelatedHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [user.Username] = user.PasswordHash,
            [itw.Username] = itw.PasswordHash,
            [usenet.Username] = usenet.PasswordHash
        };

        var options = new SeedUsersOptions
        {
            AdminOnlyBootstrap = true,
            UpdateExistingAdminPassword = true,
            AdminPassword = "Ephemeral-Admin!9aA",
            UserPassword = "Inherited-User!9aA",
            ItwPassword = "Inherited-Itw!9aA",
            UpdateExistingItwPassword = true,
            UsenetUsername = "inherited-extra-admin",
            UsenetPassword = "Inherited-Extra!9aA",
            UpdateExistingUsenetPassword = true
        };
        var method = typeof(DbInitializer).GetMethod(
            "EnsureConfiguredSeedUsersAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var invocation = method!.Invoke(
            null,
            [_dbContext, NullLogger.Instance, options, CancellationToken.None]);
        await Assert.IsAssignableFrom<Task>(invocation);

        Assert.True(BCrypt.Net.BCrypt.Verify("Ephemeral-Admin!9aA", admin.PasswordHash));
        Assert.All(
            new[] { user, itw, usenet },
            account => Assert.Equal(unrelatedHashes[account.Username], account.PasswordHash));
        Assert.DoesNotContain(
            _dbContext.Users.Local,
            account => string.Equals(
                account.Username,
                "inherited-extra-admin",
                StringComparison.OrdinalIgnoreCase));
        Assert.Null(options.AdminPassword);
    }

    [Fact]
    public async Task EnsureSeedUserAsync_ExistingAdminPasswordChangesOnlyWhenExplicitlyEnabled()
    {
        const string originalPassword = "Original-Admin-Password!9aA";
        const string replacementPassword = "Ephemeral-Admin-Password!9aA";
        var originalHash = BCrypt.Net.BCrypt.HashPassword(originalPassword);
        var admin = new UserAccount
        {
            Username = "admin",
            PasswordHash = originalHash,
            Role = "Admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsActive = true
        };
        _dbContext.Users.Add(admin);
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "EnsureSeedUserAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        async Task InvokeAsync(bool updatePasswordIfExists)
        {
            var invocation = method!.Invoke(
                null,
                [
                    _dbContext,
                    NullLogger.Instance,
                    "admin",
                    replacementPassword,
                    "Admin",
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    TenantScopeCatalog.ScopeAdmin,
                    true,
                    updatePasswordIfExists,
                    CancellationToken.None
                ]);
            await Assert.IsAssignableFrom<Task>(invocation);
        }

        await InvokeAsync(updatePasswordIfExists: false);
        Assert.Equal(originalHash, admin.PasswordHash);
        Assert.True(BCrypt.Net.BCrypt.Verify(originalPassword, admin.PasswordHash));

        await InvokeAsync(updatePasswordIfExists: true);
        Assert.NotEqual(originalHash, admin.PasswordHash);
        Assert.True(BCrypt.Net.BCrypt.Verify(replacementPassword, admin.PasswordHash));
    }

    [Fact]
    public void ResolveDefaultOperationalPermissions_YeonsuCanEditOwnRentalProfilesAndAssetsWithoutWideScope()
    {
        var method = typeof(DbInitializer).GetMethod(
            "ResolveDefaultOperationalPermissions",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var user = new UserAccount
        {
            Username = "yeonsu",
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        var permissions = Assert.IsAssignableFrom<IReadOnlyCollection<string>>(method!.Invoke(null, [user]));

        Assert.Contains(PermissionNames.RentalProfileEdit, permissions);
        Assert.Contains(PermissionNames.RentalAssetEdit, permissions);
        Assert.DoesNotContain(PermissionNames.RentalViewAll, permissions);
        Assert.DoesNotContain(PermissionNames.RentalEditAll, permissions);
    }

    [Fact]
    public async Task EnsureOperationalPermissionDefaultsAsync_PersistsYeonsuRentalPermissions()
    {
        var user = new UserAccount
        {
            Username = "yeonsu",
            PasswordHash = "test",
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "EnsureOperationalPermissionDefaultsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(null, [_dbContext, CancellationToken.None]));
        await task;
        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();
        var persisted = await _dbContext.Users
            .Include(current => current.Permissions)
            .SingleAsync(current => current.Id == user.Id);

        Assert.Contains(persisted.Permissions, permission => permission.Permission == PermissionNames.RentalProfileEdit);
        Assert.Contains(persisted.Permissions, permission => permission.Permission == PermissionNames.RentalAssetEdit);
        Assert.DoesNotContain(persisted.Permissions, permission => permission.Permission == PermissionNames.RentalEditAll);
    }

    [Fact]
    public async Task EnsureOperationalPermissionDefaultsAsync_IncludesNewlyTrackedSeedUserBeforeFirstSave()
    {
        var user = new UserAccount
        {
            Username = "user",
            PasswordHash = "test",
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };
        _dbContext.Users.Add(user);

        var method = typeof(DbInitializer).GetMethod(
            "EnsureOperationalPermissionDefaultsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(null, [_dbContext, CancellationToken.None]));
        await task;
        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();
        var persisted = await _dbContext.Users
            .Include(current => current.Permissions)
            .SingleAsync(current => current.Id == user.Id);

        Assert.Contains(persisted.Permissions, permission => permission.Permission == PermissionNames.RentalProfileEdit);
        Assert.Contains(persisted.Permissions, permission => permission.Permission == PermissionNames.RentalAssetEdit);
    }

    [Fact]
    public async Task EnsureDefaultRentalManagementCompaniesAsync_CreatesCanonicalProfileReferences()
    {
        await InvokeEnsureDefaultRentalManagementCompaniesAsync();
        await _dbContext.SaveChangesAsync();

        var companies = await _dbContext.RentalManagementCompanies
            .IgnoreQueryFilters()
            .OrderBy(company => company.TenantCode)
            .ThenBy(company => company.Code)
            .ToListAsync();

        Assert.Collection(
            companies,
            company =>
            {
                Assert.Equal(TenantScopeCatalog.Itworld, company.TenantCode);
                Assert.Equal(OfficeCodeCatalog.Itworld, company.Code);
                Assert.True(company.IsSystemDefault);
                Assert.True(company.IsActive);
                Assert.False(company.IsDeleted);
            },
            company =>
            {
                Assert.Equal(TenantScopeCatalog.UsenetGroup, company.TenantCode);
                Assert.Equal(OfficeCodeCatalog.Usenet, company.Code);
                Assert.Equal(OfficeCodeCatalog.GetOfficeDisplayName(OfficeCodeCatalog.Usenet), company.Name);
                Assert.True(company.IsSystemDefault);
                Assert.True(company.IsActive);
                Assert.False(company.IsDeleted);
            },
            company =>
            {
                Assert.Equal(TenantScopeCatalog.UsenetGroup, company.TenantCode);
                Assert.Equal(OfficeCodeCatalog.Yeonsu, company.Code);
                Assert.True(company.IsSystemDefault);
                Assert.True(company.IsActive);
                Assert.False(company.IsDeleted);
            });
    }

    [Fact]
    public async Task EnsureDefaultRentalManagementCompaniesAsync_SecondRunIsNoOp()
    {
        await InvokeEnsureDefaultRentalManagementCompaniesAsync();
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var before = await _dbContext.RentalManagementCompanies
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToDictionaryAsync(
                company => company.Id,
                company => (company.Revision, company.UpdatedAtUtc));

        await InvokeEnsureDefaultRentalManagementCompaniesAsync();

        Assert.False(_dbContext.ChangeTracker.HasChanges());
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var after = await _dbContext.RentalManagementCompanies
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToDictionaryAsync(
                company => company.Id,
                company => (company.Revision, company.UpdatedAtUtc));

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task EnsureDefaultRentalManagementCompaniesAsync_RepairsDeletedCodeLabelWithoutOverwritingCustomName()
    {
        var deletedUsenet = new RentalManagementCompany
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            Code = OfficeCodeCatalog.Usenet,
            Name = OfficeCodeCatalog.Usenet,
            IsSystemDefault = false,
            IsActive = false,
            IsDeleted = true
        };
        var customItworld = new RentalManagementCompany
        {
            TenantCode = TenantScopeCatalog.Itworld,
            Code = OfficeCodeCatalog.Itworld,
            Name = "아이티월드 렌탈 전용",
            IsSystemDefault = true,
            IsActive = true,
            IsDeleted = false
        };
        _dbContext.RentalManagementCompanies.AddRange(deletedUsenet, customItworld);
        await _dbContext.SaveChangesAsync();

        await InvokeEnsureDefaultRentalManagementCompaniesAsync();
        await _dbContext.SaveChangesAsync();

        Assert.Equal(OfficeCodeCatalog.GetOfficeDisplayName(OfficeCodeCatalog.Usenet), deletedUsenet.Name);
        Assert.True(deletedUsenet.IsSystemDefault);
        Assert.True(deletedUsenet.IsActive);
        Assert.False(deletedUsenet.IsDeleted);
        Assert.Equal("아이티월드 렌탈 전용", customItworld.Name);
    }

    [Fact]
    public async Task EnsureDefaultCompanyProfilesAsync_CreatesUsenetTradeNameInKorean()
    {
        await InvokeEnsureDefaultCompanyProfilesAsync();

        var profile = await _dbContext.CompanyProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == OfficeCodeCatalog.UsenetDefaultCompanyProfileId);
        Assert.Equal(OfficeCodeCatalog.Usenet, profile.OfficeCode);
        Assert.Equal("유즈넷 기본", profile.ProfileName);
        Assert.Equal("유즈넷", profile.TradeName);
    }

    [Fact]
    public async Task EnsureDefaultCompanyProfilesAsync_RepairsLegacyUsenetCodeTradeName_WithoutOverwritingCustomName()
    {
        _dbContext.CompanyProfiles.AddRange(
            new CompanyProfile
            {
                Id = OfficeCodeCatalog.UsenetDefaultCompanyProfileId,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ProfileName = "유즈넷 기본",
                TradeName = "USENET",
                IsDefaultForOffice = true,
                IsActive = true,
                IsDeleted = false
            },
            new CompanyProfile
            {
                Id = OfficeCodeCatalog.ItworldDefaultCompanyProfileId,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ProfileName = "ITWORLD 실사용 회사설정",
                TradeName = "ITWORLD 실제 상호",
                IsDefaultForOffice = true,
                IsActive = true,
                IsDeleted = false
            });
        await _dbContext.SaveChangesAsync();

        await InvokeEnsureDefaultCompanyProfilesAsync();

        var repaired = await _dbContext.CompanyProfiles.IgnoreQueryFilters()
            .SingleAsync(profile => profile.Id == OfficeCodeCatalog.UsenetDefaultCompanyProfileId);
        var custom = await _dbContext.CompanyProfiles.IgnoreQueryFilters()
            .SingleAsync(profile => profile.Id == OfficeCodeCatalog.ItworldDefaultCompanyProfileId);
        Assert.Equal("유즈넷", repaired.TradeName);
        Assert.Equal("ITWORLD 실제 상호", custom.TradeName);
    }

    [Fact]
    public async Task EnsureInvoiceVersionColumnsAsync_DoesNotRequire_SourceWarehouseCode_BeforeRuntimeSchema()
    {
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"InvoiceLines\";");
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Payments\";");
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Invoices\";");
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "Invoices" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Invoices" PRIMARY KEY,
                "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                "CreatedAtUtc" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                "Revision" INTEGER NOT NULL DEFAULT 0
            );
            """);
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var invoiceId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);
        var updatedAt = createdAt.AddMinutes(5);
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "Invoices" ("Id", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc", "Revision")
             VALUES ({invoiceId.ToString()}, 0, {createdAt}, {updatedAt}, 1);
             """);

        var method = typeof(DbInitializer).GetMethod(
            "EnsureInvoiceVersionColumnsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT \"VersionGroupId\", \"VersionNumber\", \"IsLatestVersion\" FROM \"Invoices\" WHERE \"Id\" = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", invoiceId.ToString());

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(invoiceId.ToString(), reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task BackfillCustomerScopeFieldsAsync_UsesPersistedRevisionForConcurrencyCheck()
    {
        var customer = CreateDuplicateMergeCustomer(
            Guid.NewGuid(),
            "123-45-67890");
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();
        var persistedRevision = customer.Revision;
        Assert.True(persistedRevision > 0);

        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE "Customers"
             SET "OfficeCode" = {string.Empty}, "TenantCode" = {string.Empty}
             WHERE "Id" = {customer.Id};
             """);
        _dbContext.ChangeTracker.Clear();

        var method = typeof(DbInitializer).GetMethod(
            "BackfillCustomerScopeFieldsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(
            method!.Invoke(null, [_dbContext, CancellationToken.None]));

        await task;

        _dbContext.ChangeTracker.Clear();
        var repaired = await _dbContext.Customers
            .IgnoreQueryFilters()
            .SingleAsync(current => current.Id == customer.Id);
        Assert.Equal(OfficeCodeCatalog.Shared, repaired.OfficeCode);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, repaired.TenantCode);
        Assert.True(repaired.Revision >= persistedRevision);
    }

    [Fact]
    public async Task EnsureInvoiceVersionColumnsAsync_UsesPersistedRevisionForStubUpdates()
    {
        var customer = CreateDuplicateMergeCustomer(
            Guid.NewGuid(),
            "987-65-43210");
        var versionGroupId = Guid.NewGuid();
        var older = CreateInitializerInvoice(
            Guid.NewGuid(),
            customer.Id,
            "REVISION-STUB-OLD");
        older.VersionGroupId = versionGroupId;
        older.VersionNumber = 1;
        older.IsLatestVersion = true;
        older.UpdatedAtUtc = new DateTime(2026, 7, 22, 1, 0, 0, DateTimeKind.Utc);
        var latest = CreateInitializerInvoice(
            Guid.NewGuid(),
            customer.Id,
            "REVISION-STUB-NEW");
        latest.VersionGroupId = versionGroupId;
        latest.VersionNumber = 2;
        latest.IsLatestVersion = true;
        latest.UpdatedAtUtc = new DateTime(2026, 7, 22, 2, 0, 0, DateTimeKind.Utc);
        _dbContext.AddRange(customer, older, latest);
        await _dbContext.SaveChangesAsync();
        Assert.True(older.Revision > 0);
        Assert.True(latest.Revision > 0);
        _dbContext.ChangeTracker.Clear();

        var method = typeof(DbInitializer).GetMethod(
            "EnsureInvoiceVersionColumnsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(
            method!.Invoke(null, [_dbContext, CancellationToken.None]));

        await task;

        _dbContext.ChangeTracker.Clear();
        var versions = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(current => current.VersionGroupId == versionGroupId)
            .OrderBy(current => current.VersionNumber)
            .ToListAsync();
        Assert.Collection(
            versions,
            first => Assert.False(first.IsLatestVersion),
            second => Assert.True(second.IsLatestVersion));
    }

    [Fact]
    public async Task EnsureInvoiceVersionColumnsAsync_NormalizesLatestVersionPerOperationalScope()
    {
        var primaryCustomer = CreateDuplicateMergeCustomer(
            Guid.NewGuid(),
            "111-22-33333");
        var otherCustomer = CreateDuplicateMergeCustomer(
            Guid.NewGuid(),
            "444-55-66666");
        otherCustomer.NameOriginal = "OTHER SCOPE CUSTOMER";
        otherCustomer.NameMatchKey = "OTHERSCOPECUSTOMER";

        var versionGroupId = Guid.NewGuid();
        var legacyScopeOlder = CreateVersion(
            primaryCustomer.Id,
            "SCOPE-LEGACY-OLDER",
            1,
            tenantCode: string.Empty,
            officeCode: string.Empty,
            responsibleOfficeCode: string.Empty,
            isLatestVersion: true);
        var legacyScopeLatest = CreateVersion(
            primaryCustomer.Id,
            "SCOPE-LEGACY-LATEST",
            2,
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            OfficeCodeCatalog.Usenet,
            isLatestVersion: false);
        var legacyScopeDeleted = CreateVersion(
            primaryCustomer.Id,
            "SCOPE-LEGACY-DELETED",
            11,
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            OfficeCodeCatalog.Usenet,
            isLatestVersion: true,
            isDeleted: true);

        var responsibleScopeOlder = CreateVersion(
            primaryCustomer.Id,
            "SCOPE-RESPONSIBLE-OLDER",
            3,
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            OfficeCodeCatalog.Yeonsu,
            isLatestVersion: true);
        var responsibleScopeLatest = CreateVersion(
            primaryCustomer.Id,
            "SCOPE-RESPONSIBLE-LATEST",
            4,
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            OfficeCodeCatalog.Yeonsu,
            isLatestVersion: false);

        var owningScopeOlder = CreateVersion(
            primaryCustomer.Id,
            "SCOPE-OWNER-OLDER",
            5,
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Shared,
            OfficeCodeCatalog.Usenet,
            isLatestVersion: true);
        var owningScopeLatest = CreateVersion(
            primaryCustomer.Id,
            "SCOPE-OWNER-LATEST",
            6,
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Shared,
            OfficeCodeCatalog.Usenet,
            isLatestVersion: false);

        var tenantScopeOlder = CreateVersion(
            primaryCustomer.Id,
            "SCOPE-TENANT-OLDER",
            7,
            TenantScopeCatalog.Itworld,
            OfficeCodeCatalog.Shared,
            OfficeCodeCatalog.Usenet,
            isLatestVersion: true);
        var tenantScopeLatest = CreateVersion(
            primaryCustomer.Id,
            "SCOPE-TENANT-LATEST",
            8,
            TenantScopeCatalog.Itworld,
            OfficeCodeCatalog.Shared,
            OfficeCodeCatalog.Usenet,
            isLatestVersion: false);

        var customerScopeOlder = CreateVersion(
            otherCustomer.Id,
            "SCOPE-CUSTOMER-OLDER",
            9,
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            OfficeCodeCatalog.Usenet,
            isLatestVersion: true);
        var customerScopeLatest = CreateVersion(
            otherCustomer.Id,
            "SCOPE-CUSTOMER-LATEST",
            10,
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            OfficeCodeCatalog.Usenet,
            isLatestVersion: false);

        _dbContext.AddRange(
            primaryCustomer,
            otherCustomer,
            legacyScopeOlder,
            legacyScopeLatest,
            legacyScopeDeleted,
            responsibleScopeOlder,
            responsibleScopeLatest,
            owningScopeOlder,
            owningScopeLatest,
            tenantScopeOlder,
            tenantScopeLatest,
            customerScopeOlder,
            customerScopeLatest);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var method = typeof(DbInitializer).GetMethod(
            "EnsureInvoiceVersionColumnsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(
            method!.Invoke(null, [_dbContext, CancellationToken.None]));

        await task;

        _dbContext.ChangeTracker.Clear();
        var latestFlags = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(current => current.VersionGroupId == versionGroupId)
            .ToDictionaryAsync(current => current.Id, current => current.IsLatestVersion);

        Assert.False(latestFlags[legacyScopeOlder.Id]);
        Assert.True(latestFlags[legacyScopeLatest.Id]);
        Assert.False(latestFlags[legacyScopeDeleted.Id]);
        Assert.False(latestFlags[responsibleScopeOlder.Id]);
        Assert.True(latestFlags[responsibleScopeLatest.Id]);
        Assert.False(latestFlags[owningScopeOlder.Id]);
        Assert.True(latestFlags[owningScopeLatest.Id]);
        Assert.False(latestFlags[tenantScopeOlder.Id]);
        Assert.True(latestFlags[tenantScopeLatest.Id]);
        Assert.False(latestFlags[customerScopeOlder.Id]);
        Assert.True(latestFlags[customerScopeLatest.Id]);

        Invoice CreateVersion(
            Guid customerId,
            string invoiceNumber,
            int versionNumber,
            string tenantCode,
            string officeCode,
            string responsibleOfficeCode,
            bool isLatestVersion,
            bool isDeleted = false)
        {
            var invoice = CreateInitializerInvoice(Guid.NewGuid(), customerId, invoiceNumber);
            invoice.VersionGroupId = versionGroupId;
            invoice.VersionNumber = versionNumber;
            invoice.IsLatestVersion = isLatestVersion;
            invoice.IsDeleted = isDeleted;
            invoice.TenantCode = tenantCode;
            invoice.OfficeCode = officeCode;
            invoice.ResponsibleOfficeCode = responsibleOfficeCode;
            invoice.UpdatedAtUtc = new DateTime(2026, 7, 30, versionNumber, 0, 0, DateTimeKind.Utc);
            return invoice;
        }
    }

    [Fact]
    public async Task BackfillOperationalOfficeOwnershipThenEnsureInvoiceVersions_ExplicitTenantMismatchRawCollision_PreservesIndependentLatestStockAndRentalState()
    {
        var customer = CreateDuplicateMergeCustomer(
            Guid.NewGuid(),
            "555-66-77777");
        customer.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;

        var mismatchedItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "MISMATCHED TENANT CHAIN STOCK",
            NameMatchKey = "MISMATCHEDTENANTCHAINSTOCK",
            SpecificationOriginal = "EA",
            SpecificationMatchKey = "EA",
            Unit = "EA",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 13m
        };
        var canonicalItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "CANONICAL TENANT CHAIN STOCK",
            NameMatchKey = "CANONICALTENANTCHAINSTOCK",
            SpecificationOriginal = "EA",
            SpecificationMatchKey = "EA",
            Unit = "EA",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 23m
        };
        var canonicalProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "CANONICAL-COLLISION-RENTAL",
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            MonthlyAmount = 700m,
            BillingMethod = "CASH",
            BillingStatus = "COMPLETED",
            SettlementStatus = "SETTLED",
            CompletionStatus = "COMPLETED",
            SettledAmount = 200m,
            OutstandingAmount = 0m,
            BillingRunsJson = "[]",
            IsActive = true
        };

        var versionGroupId = Guid.NewGuid();
        var mismatchedOlder = CreateStockVersion(
            "MISMATCHED-COLLISION-V1",
            1,
            TenantScopeCatalog.Itworld,
            mismatchedItem.Id,
            2m,
            200m);
        var mismatchedLatest = CreateStockVersion(
            "MISMATCHED-COLLISION-V2",
            2,
            TenantScopeCatalog.Itworld,
            mismatchedItem.Id,
            5m,
            500m);
        var canonicalLatest = CreateStockVersion(
            "CANONICAL-COLLISION-V1",
            1,
            TenantScopeCatalog.UsenetGroup,
            canonicalItem.Id,
            7m,
            700m);
        canonicalLatest.LinkedRentalBillingProfileId = canonicalProfile.Id;
        canonicalLatest.LinkedRentalBillingRunId = Guid.NewGuid();

        _dbContext.AddRange(
            customer,
            mismatchedItem,
            canonicalItem,
            canonicalProfile,
            mismatchedOlder,
            mismatchedLatest,
            canonicalLatest,
            new ItemWarehouseStock
            {
                ItemId = mismatchedItem.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 13m
            },
            new ItemWarehouseStock
            {
                ItemId = canonicalItem.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 23m
            });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var beforeCanonicalInvoice = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => invoice.Id == canonicalLatest.Id)
            .Select(invoice => new
            {
                invoice.TenantCode,
                invoice.OfficeCode,
                invoice.ResponsibleOfficeCode,
                invoice.IsLatestVersion,
                invoice.IsDeleted,
                invoice.Revision,
                invoice.UpdatedAtUtc
            })
            .SingleAsync();
        var beforeCanonicalStock = await _dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock =>
                stock.ItemId == canonicalItem.Id &&
                stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => new { stock.Quantity, stock.Revision })
            .SingleAsync();
        var beforeCanonicalItem = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.Id == canonicalItem.Id)
            .Select(item => new { item.CurrentStock, item.Revision })
            .SingleAsync();
        var beforeCanonicalProfile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => profile.Id == canonicalProfile.Id)
            .Select(profile => new
            {
                profile.BillingStatus,
                profile.SettlementStatus,
                profile.CompletionStatus,
                profile.SettledAmount,
                profile.OutstandingAmount,
                profile.BillingRunsJson,
                profile.Revision
            })
            .SingleAsync();

        await InvokeBackfillOperationalOfficeOwnershipAsync();
        await InvokeEnsureInvoiceVersionColumnsAsync();

        _dbContext.ChangeTracker.Clear();
        var firstRunInvoices = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => invoice.VersionGroupId == versionGroupId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.False(firstRunInvoices[mismatchedOlder.Id].IsLatestVersion);
        Assert.True(firstRunInvoices[mismatchedLatest.Id].IsLatestVersion);
        Assert.True(firstRunInvoices[canonicalLatest.Id].IsLatestVersion);
        Assert.Equal(TenantScopeCatalog.Itworld, firstRunInvoices[mismatchedOlder.Id].TenantCode);
        Assert.Equal(TenantScopeCatalog.Itworld, firstRunInvoices[mismatchedLatest.Id].TenantCode);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, firstRunInvoices[canonicalLatest.Id].TenantCode);
        Assert.Equal(
            beforeCanonicalInvoice,
            await _dbContext.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => invoice.Id == canonicalLatest.Id)
                .Select(invoice => new
                {
                    invoice.TenantCode,
                    invoice.OfficeCode,
                    invoice.ResponsibleOfficeCode,
                    invoice.IsLatestVersion,
                    invoice.IsDeleted,
                    invoice.Revision,
                    invoice.UpdatedAtUtc
                })
                .SingleAsync());

        Assert.Equal(
            15m,
            await _dbContext.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock =>
                    stock.ItemId == mismatchedItem.Id &&
                    stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.Equal(
            15m,
            await _dbContext.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => item.Id == mismatchedItem.Id)
                .Select(item => item.CurrentStock)
                .SingleAsync());
        Assert.Equal(
            beforeCanonicalStock,
            await _dbContext.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock =>
                    stock.ItemId == canonicalItem.Id &&
                    stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => new { stock.Quantity, stock.Revision })
                .SingleAsync());
        Assert.Equal(
            beforeCanonicalItem,
            await _dbContext.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => item.Id == canonicalItem.Id)
                .Select(item => new { item.CurrentStock, item.Revision })
                .SingleAsync());
        Assert.Equal(
            beforeCanonicalProfile,
            await _dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => profile.Id == canonicalProfile.Id)
                .Select(profile => new
                {
                    profile.BillingStatus,
                    profile.SettlementStatus,
                    profile.CompletionStatus,
                    profile.SettledAmount,
                    profile.OutstandingAmount,
                    profile.BillingRunsJson,
                    profile.Revision
                })
                .SingleAsync());

        var firstRunRevisions = firstRunInvoices.ToDictionary(
            current => current.Key,
            current => current.Value.Revision);
        var firstRunStocks = await _dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock =>
                stock.ItemId == mismatchedItem.Id ||
                stock.ItemId == canonicalItem.Id)
            .ToDictionaryAsync(
                stock => (stock.ItemId, stock.WarehouseCode),
                stock => (stock.Quantity, stock.Revision));

        await InvokeBackfillOperationalOfficeOwnershipAsync();
        await InvokeEnsureInvoiceVersionColumnsAsync();

        _dbContext.ChangeTracker.Clear();
        Assert.Equal(
            firstRunRevisions,
            await _dbContext.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => invoice.VersionGroupId == versionGroupId)
                .ToDictionaryAsync(invoice => invoice.Id, invoice => invoice.Revision));
        Assert.Equal(
            firstRunStocks,
            await _dbContext.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock =>
                    stock.ItemId == mismatchedItem.Id ||
                    stock.ItemId == canonicalItem.Id)
                .ToDictionaryAsync(
                    stock => (stock.ItemId, stock.WarehouseCode),
                    stock => (stock.Quantity, stock.Revision)));
        Assert.Equal(
            beforeCanonicalProfile,
            await _dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => profile.Id == canonicalProfile.Id)
                .Select(profile => new
                {
                    profile.BillingStatus,
                    profile.SettlementStatus,
                    profile.CompletionStatus,
                    profile.SettledAmount,
                    profile.OutstandingAmount,
                    profile.BillingRunsJson,
                    profile.Revision
                })
                .SingleAsync());

        Invoice CreateStockVersion(
            string invoiceNumber,
            int versionNumber,
            string tenantCode,
            Guid itemId,
            decimal quantity,
            decimal totalAmount)
        {
            var invoice = CreateInitializerInvoice(
                Guid.NewGuid(),
                customer.Id,
                invoiceNumber);
            invoice.TenantCode = tenantCode;
            invoice.OfficeCode = OfficeCodeCatalog.Usenet;
            invoice.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
            invoice.VersionGroupId = versionGroupId;
            invoice.VersionNumber = versionNumber;
            invoice.IsLatestVersion = true;
            invoice.SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse;
            invoice.TotalAmount = totalAmount;
            invoice.SupplyAmount = totalAmount;
            invoice.Lines.Add(new InvoiceLine
            {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                ItemNameOriginal = invoiceNumber,
                SpecificationOriginal = "EA",
                Unit = "EA",
                Quantity = quantity,
                UnitPrice = totalAmount / quantity,
                LineAmount = totalAmount,
                ItemTrackingType = ItemTrackingTypes.Stock
            });
            return invoice;
        }
    }

    [Fact]
    public async Task StartupScopeBackfill_NormalizesLegacyInvoiceChainAgainstCustomerScope_AndIsIdempotent()
    {
        var customer = CreateDuplicateMergeCustomer(
            Guid.NewGuid(),
            "777-88-99999");
        customer.TenantCode = " itworld ";
        customer.OfficeCode = " shared ";
        customer.ResponsibleOfficeCode = " ITWORLD ";

        var versionGroupId = Guid.NewGuid();
        var legacyVersion = CreateInitializerInvoice(
            Guid.NewGuid(),
            customer.Id,
            "STARTUP-SCOPE-LEGACY");
        legacyVersion.VersionGroupId = versionGroupId;
        legacyVersion.VersionNumber = 1;
        legacyVersion.IsLatestVersion = true;
        legacyVersion.TenantCode = string.Empty;
        legacyVersion.OfficeCode = string.Empty;
        legacyVersion.ResponsibleOfficeCode = string.Empty;
        legacyVersion.UpdatedAtUtc = new DateTime(2026, 7, 30, 1, 0, 0, DateTimeKind.Utc);

        var canonicalVersion = CreateInitializerInvoice(
            Guid.NewGuid(),
            customer.Id,
            "STARTUP-SCOPE-CANONICAL");
        canonicalVersion.VersionGroupId = versionGroupId;
        canonicalVersion.VersionNumber = 2;
        canonicalVersion.IsLatestVersion = false;
        canonicalVersion.TenantCode = " usenet ";
        canonicalVersion.OfficeCode = " shared ";
        canonicalVersion.ResponsibleOfficeCode = " itworld ";
        canonicalVersion.UpdatedAtUtc = new DateTime(2026, 7, 30, 2, 0, 0, DateTimeKind.Utc);

        _dbContext.AddRange(customer, legacyVersion, canonicalVersion);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await InvokeStartupScopeRepairSequenceAsync();

        _dbContext.ChangeTracker.Clear();
        var firstRun = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(current => current.VersionGroupId == versionGroupId)
            .OrderBy(current => current.VersionNumber)
            .ToListAsync();
        Assert.Collection(
            firstRun,
            first =>
            {
                Assert.False(first.IsLatestVersion);
                Assert.Equal(TenantScopeCatalog.UsenetGroup, first.TenantCode);
                Assert.Equal(OfficeCodeCatalog.Shared, first.OfficeCode);
                Assert.Equal(OfficeCodeCatalog.Itworld, first.ResponsibleOfficeCode);
            },
            second =>
            {
                Assert.True(second.IsLatestVersion);
                Assert.Equal(TenantScopeCatalog.UsenetGroup, second.TenantCode);
                Assert.Equal(OfficeCodeCatalog.Shared, second.OfficeCode);
                Assert.Equal(OfficeCodeCatalog.Itworld, second.ResponsibleOfficeCode);
            });
        Assert.Single(firstRun, current => current.IsLatestVersion);
        var firstRunRevisions = firstRun.ToDictionary(current => current.Id, current => current.Revision);

        await InvokeStartupScopeRepairSequenceAsync();

        _dbContext.ChangeTracker.Clear();
        var secondRun = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(current => current.VersionGroupId == versionGroupId)
            .OrderBy(current => current.VersionNumber)
            .ToListAsync();
        Assert.Collection(
            secondRun,
            first => Assert.False(first.IsLatestVersion),
            second => Assert.True(second.IsLatestVersion));
        Assert.Equal(
            firstRunRevisions,
            secondRun.ToDictionary(current => current.Id, current => current.Revision));
    }

    [Fact]
    public async Task EnsureInvoiceVersionColumnsAsync_PartialSchemaPreservesAvailableCustomerScope_AndIsIdempotent()
    {
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"InvoiceLines\";");
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Payments\";");
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Invoices\";");
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "Invoices" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Invoices" PRIMARY KEY,
                "CustomerId" TEXT NOT NULL,
                "TenantCode" TEXT NOT NULL DEFAULT '',
                "OfficeCode" TEXT NOT NULL DEFAULT '',
                "VersionGroupId" TEXT NULL,
                "VersionNumber" INTEGER NOT NULL DEFAULT 1,
                "IsLatestVersion" INTEGER NOT NULL DEFAULT 1,
                "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                "CreatedAtUtc" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                "Revision" INTEGER NOT NULL DEFAULT 0
            );
            """);
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var versionGroupId = Guid.NewGuid();
        var firstCustomerId = Guid.NewGuid();
        var secondCustomerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 7, 30, 3, 0, 0, DateTimeKind.Utc);
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "Invoices"
                 ("Id", "CustomerId", "TenantCode", "OfficeCode", "VersionGroupId", "VersionNumber",
                  "IsLatestVersion", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc", "Revision")
             VALUES
                 ({firstInvoiceId.ToString()}, {firstCustomerId.ToString()}, {TenantScopeCatalog.UsenetGroup},
                  {OfficeCodeCatalog.Usenet}, {versionGroupId.ToString()}, 1, 1, 0, {createdAt}, {createdAt}, 1),
                 ({secondInvoiceId.ToString()}, {secondCustomerId.ToString()}, {TenantScopeCatalog.UsenetGroup},
                  {OfficeCodeCatalog.Usenet}, {versionGroupId.ToString()}, 2, 1, 0, {createdAt}, {createdAt.AddMinutes(1)}, 2);
             """);

        await InvokeEnsureInvoiceVersionColumnsAsync();

        var firstRun = await ReadPartialInvoiceLatestFlagsAsync();
        Assert.True(firstRun[firstInvoiceId]);
        Assert.True(firstRun[secondInvoiceId]);

        await InvokeEnsureInvoiceVersionColumnsAsync();

        Assert.Equal(firstRun, await ReadPartialInvoiceLatestFlagsAsync());

        async Task<Dictionary<Guid, bool>> ReadPartialInvoiceLatestFlagsAsync()
        {
            var result = new Dictionary<Guid, bool>();
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT \"Id\", \"IsLatestVersion\" FROM \"Invoices\" ORDER BY \"Id\";";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result[Guid.Parse(reader.GetString(0))] = reader.GetInt64(1) != 0;
            }

            return result;
        }
    }

    [Fact]
    public async Task EnsureInvoiceVersionColumnsAsync_PartialSideEffectSchema_SkipsDuplicateLatestRepairAtomically()
    {
        var customer = CreateDuplicateMergeCustomer(
            Guid.NewGuid(),
            "999-00-11111");
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PARTIAL SIDE EFFECT STOCK",
            NameMatchKey = "PARTIALSIDEEFFECTSTOCK",
            SpecificationOriginal = "EA",
            SpecificationMatchKey = "EA",
            Unit = "EA",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 13m
        };
        var versionGroupId = Guid.NewGuid();
        var firstVersion = CreateInitializerInvoice(
            Guid.NewGuid(),
            customer.Id,
            "PARTIAL-SIDE-EFFECT-V1");
        firstVersion.VersionGroupId = versionGroupId;
        firstVersion.VersionNumber = 1;
        firstVersion.IsLatestVersion = true;
        firstVersion.SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse;
        firstVersion.Lines.Add(new InvoiceLine
        {
            ItemId = item.Id,
            ItemNameOriginal = item.NameOriginal,
            SpecificationOriginal = "EA",
            Unit = "EA",
            Quantity = 2m,
            UnitPrice = 100m,
            LineAmount = 200m,
            ItemTrackingType = ItemTrackingTypes.Stock
        });
        var secondVersion = CreateInitializerInvoice(
            Guid.NewGuid(),
            customer.Id,
            "PARTIAL-SIDE-EFFECT-V2");
        secondVersion.VersionGroupId = versionGroupId;
        secondVersion.VersionNumber = 2;
        secondVersion.IsLatestVersion = true;
        secondVersion.SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse;
        secondVersion.Lines.Add(new InvoiceLine
        {
            ItemId = item.Id,
            ItemNameOriginal = item.NameOriginal,
            SpecificationOriginal = "EA",
            Unit = "EA",
            Quantity = 5m,
            UnitPrice = 100m,
            LineAmount = 500m,
            ItemTrackingType = ItemTrackingTypes.Stock
        });
        _dbContext.AddRange(
            customer,
            item,
            firstVersion,
            secondVersion,
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 13m
            });
        await _dbContext.SaveChangesAsync();
        await new InventoryLedgerService(_dbContext).RebuildAsync();
        _dbContext.ChangeTracker.Clear();

        var beforeVersions = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => invoice.VersionGroupId == versionGroupId)
            .ToDictionaryAsync(
                invoice => invoice.Id,
                invoice => (invoice.IsLatestVersion, invoice.Revision, invoice.UpdatedAtUtc));
        var beforeStock = await _dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock =>
                stock.ItemId == item.Id &&
                stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => new { stock.Quantity, stock.Revision })
            .SingleAsync();
        var beforeItem = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id == item.Id)
            .Select(current => new { current.CurrentStock, current.Revision })
            .SingleAsync();
        var beforeLedger = await _dbContext.InventoryLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.ItemId == item.Id)
            .OrderBy(entry => entry.SourceDocumentId)
            .Select(entry => new
            {
                entry.Id,
                entry.SourceDocumentId,
                entry.SourceLineId,
                entry.QuantityDelta
            })
            .ToListAsync();

        await _dbContext.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"RentalBillingProfiles\" DROP COLUMN \"BillingRunsJson\";");

        await InvokeEnsureInvoiceVersionColumnsAsync();

        _dbContext.ChangeTracker.Clear();
        var afterVersions = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => invoice.VersionGroupId == versionGroupId)
            .ToDictionaryAsync(
                invoice => invoice.Id,
                invoice => (invoice.IsLatestVersion, invoice.Revision, invoice.UpdatedAtUtc));
        var afterStock = await _dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock =>
                stock.ItemId == item.Id &&
                stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => new { stock.Quantity, stock.Revision })
            .SingleAsync();
        var afterItem = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id == item.Id)
            .Select(current => new { current.CurrentStock, current.Revision })
            .SingleAsync();
        var afterLedger = await _dbContext.InventoryLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.ItemId == item.Id)
            .OrderBy(entry => entry.SourceDocumentId)
            .Select(entry => new
            {
                entry.Id,
                entry.SourceDocumentId,
                entry.SourceLineId,
                entry.QuantityDelta
            })
            .ToListAsync();

        Assert.All(afterVersions.Values, version => Assert.True(version.IsLatestVersion));
        Assert.Equal(beforeVersions, afterVersions);
        Assert.Equal(beforeStock, afterStock);
        Assert.Equal(beforeItem, afterItem);
        Assert.Equal(beforeLedger, afterLedger);
    }

    [Fact]
    public async Task EnsureInvoiceVersionColumnsAsync_AllDeletedAndTiedChains_AreDeterministic()
    {
        var customer = CreateDuplicateMergeCustomer(
            Guid.NewGuid(),
            "222-33-44444");
        var allDeletedGroupId = Guid.NewGuid();
        var deletedOlder = CreateInitializerInvoice(
            Guid.NewGuid(),
            customer.Id,
            "ALL-DELETED-OLDER");
        deletedOlder.VersionGroupId = allDeletedGroupId;
        deletedOlder.VersionNumber = 1;
        deletedOlder.IsDeleted = true;
        deletedOlder.IsLatestVersion = true;
        var deletedNewer = CreateInitializerInvoice(
            Guid.NewGuid(),
            customer.Id,
            "ALL-DELETED-NEWER");
        deletedNewer.VersionGroupId = allDeletedGroupId;
        deletedNewer.VersionNumber = 2;
        deletedNewer.IsDeleted = true;
        deletedNewer.IsLatestVersion = true;

        var tiedGroupId = Guid.NewGuid();
        var lowerId = CreateInitializerInvoice(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            customer.Id,
            "TIED-LOWER-ID");
        lowerId.VersionGroupId = tiedGroupId;
        lowerId.VersionNumber = 5;
        lowerId.UpdatedAtUtc = new DateTime(2026, 7, 30, 4, 0, 0, DateTimeKind.Utc);
        lowerId.IsLatestVersion = true;
        var higherId = CreateInitializerInvoice(
            Guid.Parse("f0000000-0000-0000-0000-000000000001"),
            customer.Id,
            "TIED-HIGHER-ID");
        higherId.VersionGroupId = tiedGroupId;
        higherId.VersionNumber = 5;
        higherId.UpdatedAtUtc = lowerId.UpdatedAtUtc;
        higherId.IsLatestVersion = true;

        _dbContext.AddRange(customer, deletedOlder, deletedNewer, lowerId, higherId);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await InvokeEnsureInvoiceVersionColumnsAsync();

        _dbContext.ChangeTracker.Clear();
        var invoices = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(current =>
                current.VersionGroupId == allDeletedGroupId ||
                current.VersionGroupId == tiedGroupId)
            .ToDictionaryAsync(current => current.Id);
        Assert.False(invoices[deletedOlder.Id].IsLatestVersion);
        Assert.False(invoices[deletedNewer.Id].IsLatestVersion);
        Assert.False(invoices[lowerId.Id].IsLatestVersion);
        Assert.True(invoices[higherId.Id].IsLatestVersion);

        var firstRunRevisions = invoices.ToDictionary(current => current.Key, current => current.Value.Revision);
        var firstRunUpdatedAtUtc = invoices.ToDictionary(current => current.Key, current => current.Value.UpdatedAtUtc);

        await InvokeEnsureInvoiceVersionColumnsAsync();

        _dbContext.ChangeTracker.Clear();
        var secondRunInvoices = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(current =>
                current.VersionGroupId == allDeletedGroupId ||
                current.VersionGroupId == tiedGroupId)
            .ToDictionaryAsync(current => current.Id);
        Assert.False(secondRunInvoices[deletedOlder.Id].IsLatestVersion);
        Assert.False(secondRunInvoices[deletedNewer.Id].IsLatestVersion);
        Assert.False(secondRunInvoices[lowerId.Id].IsLatestVersion);
        Assert.True(secondRunInvoices[higherId.Id].IsLatestVersion);
        Assert.Equal(
            firstRunRevisions,
            secondRunInvoices.ToDictionary(current => current.Key, current => current.Value.Revision));
        Assert.Equal(
            firstRunUpdatedAtUtc,
            secondRunInvoices.ToDictionary(current => current.Key, current => current.Value.UpdatedAtUtc));
    }

    [Fact]
    public async Task FullStartup_DuplicateLatestInvoiceVersions_ReconcilesStockLedgerAndRentalSettlement_Idempotently()
    {
        var customer = CreateDuplicateMergeCustomer(
            Guid.NewGuid(),
            "333-44-55555");
        var salesItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "STARTUP SALES STOCK",
            NameMatchKey = "STARTUPSALESSTOCK",
            SpecificationOriginal = "EA",
            SpecificationMatchKey = "EA",
            Unit = "EA",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 13m
        };
        var purchaseItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "STARTUP PURCHASE STOCK",
            NameMatchKey = "STARTUPPURCHASESTOCK",
            SpecificationOriginal = "EA",
            SpecificationMatchKey = "EA",
            Unit = "EA",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 10m
        };
        var rentalProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "STARTUP-LATEST-PARITY",
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            MonthlyAmount = 500m,
            BillingMethod = "\uD604\uAE08",
            BillingStatus = "\uC644\uB8CC",
            SettlementStatus = "\uC785\uAE08\uD655\uC778",
            CompletionStatus = "\uC644\uB8CC",
            SettledAmount = 200m,
            OutstandingAmount = 0m,
            BillingRunsJson = "[]",
            IsActive = true
        };
        var rentalRunId = Guid.NewGuid();

        var salesGroupId = Guid.NewGuid();
        var olderSales = CreateStockInvoice(
            "STARTUP-SALES-V1",
            VoucherType.Sales,
            salesGroupId,
            versionNumber: 1,
            salesItem.Id,
            quantity: 2m,
            warehouseCode: OfficeCodeCatalog.UsenetMainWarehouse,
            totalAmount: 200m);
        olderSales.LinkedRentalBillingProfileId = rentalProfile.Id;
        olderSales.LinkedRentalBillingRunId = rentalRunId;
        olderSales.UpdatedAtUtc = new DateTime(2026, 7, 31, 1, 0, 0, DateTimeKind.Utc);
        var newerSales = CreateStockInvoice(
            "STARTUP-SALES-V2",
            VoucherType.Sales,
            salesGroupId,
            versionNumber: 2,
            salesItem.Id,
            quantity: 5m,
            warehouseCode: OfficeCodeCatalog.UsenetMainWarehouse,
            totalAmount: 500m);
        newerSales.LinkedRentalBillingProfileId = rentalProfile.Id;
        newerSales.LinkedRentalBillingRunId = rentalRunId;
        newerSales.UpdatedAtUtc = new DateTime(2026, 7, 31, 2, 0, 0, DateTimeKind.Utc);

        var purchaseGroupId = Guid.NewGuid();
        var olderPurchase = CreateStockInvoice(
            "STARTUP-PURCHASE-V1",
            VoucherType.Purchase,
            purchaseGroupId,
            versionNumber: 1,
            purchaseItem.Id,
            quantity: 3m,
            warehouseCode: OfficeCodeCatalog.UsenetMainWarehouse,
            totalAmount: 300m);
        olderPurchase.PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed;
        olderPurchase.UpdatedAtUtc = new DateTime(2026, 7, 31, 3, 0, 0, DateTimeKind.Utc);
        var newerPurchase = CreateStockInvoice(
            "STARTUP-PURCHASE-V2",
            VoucherType.Purchase,
            purchaseGroupId,
            versionNumber: 2,
            purchaseItem.Id,
            quantity: 7m,
            warehouseCode: OfficeCodeCatalog.YeonsuMainWarehouse,
            totalAmount: 700m);
        newerPurchase.PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed;
        newerPurchase.UpdatedAtUtc = new DateTime(2026, 7, 31, 4, 0, 0, DateTimeKind.Utc);

        var olderPayment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = olderSales.Id,
            PaymentDate = new DateOnly(2026, 7, 31),
            Amount = 200m,
            Note = "stale duplicate-latest settlement"
        };
        _dbContext.AddRange(
            customer,
            salesItem,
            purchaseItem,
            rentalProfile,
            olderSales,
            newerSales,
            olderPurchase,
            newerPurchase,
            olderPayment,
            new ItemWarehouseStock
            {
                ItemId = salesItem.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 13m
            },
            new ItemWarehouseStock
            {
                ItemId = purchaseItem.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 3m
            },
            new ItemWarehouseStock
            {
                ItemId = purchaseItem.Id,
                WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Quantity = 7m
            });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await InvokeStartupScopeRepairSequenceAsync();

        _dbContext.ChangeTracker.Clear();
        var firstRunInvoices = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(invoice =>
                invoice.VersionGroupId == salesGroupId ||
                invoice.VersionGroupId == purchaseGroupId)
            .OrderBy(invoice => invoice.InvoiceNumber)
            .ToListAsync();
        Assert.False(firstRunInvoices.Single(invoice => invoice.Id == olderSales.Id).IsLatestVersion);
        Assert.True(firstRunInvoices.Single(invoice => invoice.Id == newerSales.Id).IsLatestVersion);
        Assert.False(firstRunInvoices.Single(invoice => invoice.Id == olderPurchase.Id).IsLatestVersion);
        Assert.True(firstRunInvoices.Single(invoice => invoice.Id == newerPurchase.Id).IsLatestVersion);

        var firstRunStocks = await _dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock => stock.ItemId == salesItem.Id || stock.ItemId == purchaseItem.Id)
            .ToDictionaryAsync(
                stock => (stock.ItemId, stock.WarehouseCode),
                stock => (stock.Quantity, stock.Revision));
        Assert.Equal(
            15m,
            firstRunStocks[(salesItem.Id, OfficeCodeCatalog.UsenetMainWarehouse)].Quantity);
        Assert.Equal(
            0m,
            firstRunStocks[(purchaseItem.Id, OfficeCodeCatalog.UsenetMainWarehouse)].Quantity);
        Assert.Equal(
            7m,
            firstRunStocks[(purchaseItem.Id, OfficeCodeCatalog.YeonsuMainWarehouse)].Quantity);

        var firstRunItems = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.Id == salesItem.Id || item.Id == purchaseItem.Id)
            .ToDictionaryAsync(item => item.Id, item => (item.CurrentStock, item.Revision));
        Assert.Equal(15m, firstRunItems[salesItem.Id].CurrentStock);
        Assert.Equal(7m, firstRunItems[purchaseItem.Id].CurrentStock);

        var firstRunLedger = await _dbContext.InventoryLedgerEntries
            .AsNoTracking()
            .Where(entry =>
                entry.SourceDocumentId == olderSales.Id ||
                entry.SourceDocumentId == newerSales.Id ||
                entry.SourceDocumentId == olderPurchase.Id ||
                entry.SourceDocumentId == newerPurchase.Id)
            .OrderBy(entry => entry.SourceDocumentId)
            .ToListAsync();
        Assert.Collection(
            firstRunLedger.OrderBy(entry => entry.QuantityDelta),
            entry =>
            {
                Assert.Equal(newerSales.Id, entry.SourceDocumentId);
                Assert.Equal(-5m, entry.QuantityDelta);
            },
            entry =>
            {
                Assert.Equal(newerPurchase.Id, entry.SourceDocumentId);
                Assert.Equal(7m, entry.QuantityDelta);
            });

        var firstRunProfile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == rentalProfile.Id);
        Assert.Equal(0m, firstRunProfile.SettledAmount);
        Assert.Equal(500m, firstRunProfile.OutstandingAmount);
        Assert.Equal("\uD655\uC778\uB300\uAE30", firstRunProfile.SettlementStatus);
        Assert.Equal("\uBBF8\uC644\uB8CC", firstRunProfile.CompletionStatus);
        Assert.Equal("\uCCAD\uAD6C\uC911", firstRunProfile.BillingStatus);

        var firstRunInvoiceRevisions = firstRunInvoices.ToDictionary(invoice => invoice.Id, invoice => invoice.Revision);
        var firstRunLedgerIds = firstRunLedger.Select(entry => entry.Id).Order().ToArray();
        var firstRunProfileRevision = firstRunProfile.Revision;

        await InvokeStartupScopeRepairSequenceAsync();

        _dbContext.ChangeTracker.Clear();
        var secondRunStocks = await _dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock => stock.ItemId == salesItem.Id || stock.ItemId == purchaseItem.Id)
            .ToDictionaryAsync(
                stock => (stock.ItemId, stock.WarehouseCode),
                stock => (stock.Quantity, stock.Revision));
        var secondRunItems = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.Id == salesItem.Id || item.Id == purchaseItem.Id)
            .ToDictionaryAsync(item => item.Id, item => (item.CurrentStock, item.Revision));
        var secondRunInvoices = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.VersionGroupId == salesGroupId ||
                invoice.VersionGroupId == purchaseGroupId)
            .ToDictionaryAsync(invoice => invoice.Id, invoice => invoice.Revision);
        var secondRunLedgerIds = await _dbContext.InventoryLedgerEntries
            .AsNoTracking()
            .Where(entry =>
                entry.SourceDocumentId == newerSales.Id ||
                entry.SourceDocumentId == newerPurchase.Id)
            .Select(entry => entry.Id)
            .OrderBy(id => id)
            .ToArrayAsync();
        var secondRunProfile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == rentalProfile.Id);

        Assert.Equal(firstRunStocks, secondRunStocks);
        Assert.Equal(firstRunItems, secondRunItems);
        Assert.Equal(firstRunInvoiceRevisions, secondRunInvoices);
        Assert.Equal(firstRunLedgerIds, secondRunLedgerIds);
        Assert.Equal(firstRunProfileRevision, secondRunProfile.Revision);
        Assert.Equal(firstRunProfile.SettledAmount, secondRunProfile.SettledAmount);
        Assert.Equal(firstRunProfile.OutstandingAmount, secondRunProfile.OutstandingAmount);
        Assert.Equal(firstRunProfile.SettlementStatus, secondRunProfile.SettlementStatus);
        Assert.Equal(firstRunProfile.CompletionStatus, secondRunProfile.CompletionStatus);
        Assert.Equal(firstRunProfile.BillingStatus, secondRunProfile.BillingStatus);

        Invoice CreateStockInvoice(
            string invoiceNumber,
            VoucherType voucherType,
            Guid versionGroupId,
            int versionNumber,
            Guid itemId,
            decimal quantity,
            string warehouseCode,
            decimal totalAmount)
        {
            var invoice = CreateInitializerInvoice(Guid.NewGuid(), customer.Id, invoiceNumber);
            invoice.VoucherType = voucherType;
            invoice.VersionGroupId = versionGroupId;
            invoice.VersionNumber = versionNumber;
            invoice.IsLatestVersion = true;
            invoice.SourceWarehouseCode = warehouseCode;
            invoice.TotalAmount = totalAmount;
            invoice.SupplyAmount = totalAmount;
            invoice.Lines.Add(new InvoiceLine
            {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                ItemNameOriginal = invoiceNumber,
                SpecificationOriginal = "EA",
                Unit = "EA",
                Quantity = quantity,
                UnitPrice = totalAmount / quantity,
                LineAmount = totalAmount,
                ItemTrackingType = ItemTrackingTypes.Stock
            });
            return invoice;
        }
    }

    [PostgreSqlFact]
    public async Task BackfillOperationalOwnershipThenEnsureInvoiceVersions_PostgreSql_SeparatesExplicitTenantMismatchAndReconcilesStockIdempotently()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(
            PostgreSqlSyncPushMutationIdempotencyTests.ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await using (var maintenanceConnection = new NpgsqlConnection(maintenanceBuilder.ConnectionString))
            {
                await maintenanceConnection.OpenAsync();
                await using var createCommand = maintenanceConnection.CreateCommand();
                createCommand.CommandText = $"CREATE DATABASE \"{databaseName}\";";
                await createCommand.ExecuteNonQueryAsync();
            }
            databaseCreated = true;

            var currentUser = new TestCurrentUserContext
            {
                Username = "admin",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin,
                IsAdmin = true
            };
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .Options;
            await using var dbContext = new AppDbContext(options, currentUser, new RevisionClock());
            await dbContext.Database.EnsureCreatedAsync();

            var customer = CreateDuplicateMergeCustomer(Guid.NewGuid(), "666-77-88888");
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "POSTGRES STARTUP PARITY",
                NameMatchKey = "POSTGRESSTARTUPPARITY",
                SpecificationOriginal = "EA",
                SpecificationMatchKey = "EA",
                Unit = "EA",
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 13m
            };
            var versionGroupId = Guid.NewGuid();
            var firstVersion = CreateInitializerInvoice(
                Guid.Parse("10000000-0000-0000-0000-000000000010"),
                customer.Id,
                "PG-STARTUP-V1");
            firstVersion.VersionGroupId = versionGroupId;
            firstVersion.VersionNumber = 1;
            firstVersion.IsLatestVersion = true;
            firstVersion.TenantCode = TenantScopeCatalog.Itworld;
            firstVersion.OfficeCode = OfficeCodeCatalog.Usenet;
            firstVersion.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
            firstVersion.SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse;
            firstVersion.Lines.Add(new InvoiceLine
            {
                ItemId = item.Id,
                ItemNameOriginal = item.NameOriginal,
                SpecificationOriginal = "EA",
                Unit = "EA",
                Quantity = 2m,
                UnitPrice = 100m,
                LineAmount = 200m,
                ItemTrackingType = ItemTrackingTypes.Stock
            });
            var secondVersion = CreateInitializerInvoice(
                Guid.Parse("f0000000-0000-0000-0000-000000000010"),
                customer.Id,
                "PG-STARTUP-V2");
            secondVersion.VersionGroupId = versionGroupId;
            secondVersion.VersionNumber = 2;
            secondVersion.IsLatestVersion = true;
            secondVersion.TenantCode = TenantScopeCatalog.Itworld;
            secondVersion.OfficeCode = OfficeCodeCatalog.Usenet;
            secondVersion.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
            secondVersion.SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse;
            secondVersion.Lines.Add(new InvoiceLine
            {
                ItemId = item.Id,
                ItemNameOriginal = item.NameOriginal,
                SpecificationOriginal = "EA",
                Unit = "EA",
                Quantity = 5m,
                UnitPrice = 100m,
                LineAmount = 500m,
                ItemTrackingType = ItemTrackingTypes.Stock
            });
            var canonicalTenantVersion = CreateInitializerInvoice(
                Guid.NewGuid(),
                customer.Id,
                "PG-CANONICAL-TENANT-V1");
            canonicalTenantVersion.VersionGroupId = versionGroupId;
            canonicalTenantVersion.VersionNumber = 1;
            canonicalTenantVersion.IsLatestVersion = true;
            canonicalTenantVersion.TenantCode = TenantScopeCatalog.UsenetGroup;
            canonicalTenantVersion.OfficeCode = OfficeCodeCatalog.Usenet;
            canonicalTenantVersion.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
            dbContext.AddRange(
                customer,
                item,
                firstVersion,
                secondVersion,
                canonicalTenantVersion,
                new ItemWarehouseStock
                {
                    ItemId = item.Id,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 13m
                });
            await dbContext.SaveChangesAsync();
            dbContext.ChangeTracker.Clear();

            var operationalOwnershipMethod = typeof(DbInitializer).GetMethod(
                "BackfillOperationalOfficeOwnershipAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            var initializerMethod = typeof(DbInitializer).GetMethod(
                "EnsureInvoiceVersionColumnsAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(operationalOwnershipMethod);
            Assert.NotNull(initializerMethod);
            var firstOwnershipTask = Assert.IsAssignableFrom<Task>(
                operationalOwnershipMethod!.Invoke(null, [dbContext, CancellationToken.None]));
            await firstOwnershipTask;
            var firstTask = Assert.IsAssignableFrom<Task>(
                initializerMethod!.Invoke(null, [dbContext, CancellationToken.None]));
            await firstTask;
            dbContext.ChangeTracker.Clear();

            var firstRunVersions = await dbContext.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => invoice.VersionGroupId == versionGroupId)
                .ToDictionaryAsync(invoice => invoice.Id);
            Assert.False(firstRunVersions[firstVersion.Id].IsLatestVersion);
            Assert.True(firstRunVersions[secondVersion.Id].IsLatestVersion);
            Assert.True(firstRunVersions[canonicalTenantVersion.Id].IsLatestVersion);
            Assert.Equal(TenantScopeCatalog.Itworld, firstRunVersions[firstVersion.Id].TenantCode);
            Assert.Equal(TenantScopeCatalog.Itworld, firstRunVersions[secondVersion.Id].TenantCode);
            Assert.Equal(
                TenantScopeCatalog.UsenetGroup,
                firstRunVersions[canonicalTenantVersion.Id].TenantCode);
            Assert.Equal(
                15m,
                await dbContext.ItemWarehouseStocks
                    .AsNoTracking()
                    .Where(stock =>
                        stock.ItemId == item.Id &&
                        stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                    .Select(stock => stock.Quantity)
                    .SingleAsync());
            Assert.Equal(
                15m,
                await dbContext.Items
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(current => current.Id == item.Id)
                    .Select(current => current.CurrentStock)
                    .SingleAsync());
            var firstRunLedger = Assert.Single(
                await dbContext.InventoryLedgerEntries
                    .AsNoTracking()
                    .Where(entry => entry.ItemId == item.Id)
                    .ToListAsync());
            Assert.Equal(secondVersion.Id, firstRunLedger.SourceDocumentId);
            Assert.Equal(-5m, firstRunLedger.QuantityDelta);
            var firstRunRevisions = firstRunVersions.ToDictionary(
                current => current.Key,
                current => current.Value.Revision);
            var firstRunStockRevision = await dbContext.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock =>
                    stock.ItemId == item.Id &&
                    stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Revision)
                .SingleAsync();

            var secondOwnershipTask = Assert.IsAssignableFrom<Task>(
                operationalOwnershipMethod.Invoke(null, [dbContext, CancellationToken.None]));
            await secondOwnershipTask;
            var secondTask = Assert.IsAssignableFrom<Task>(
                initializerMethod.Invoke(null, [dbContext, CancellationToken.None]));
            await secondTask;
            dbContext.ChangeTracker.Clear();

            Assert.Equal(
                firstRunRevisions,
                await dbContext.Invoices
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(invoice => invoice.VersionGroupId == versionGroupId)
                    .ToDictionaryAsync(invoice => invoice.Id, invoice => invoice.Revision));
            Assert.Equal(
                firstRunStockRevision,
                await dbContext.ItemWarehouseStocks
                    .AsNoTracking()
                    .Where(stock =>
                        stock.ItemId == item.Id &&
                        stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                    .Select(stock => stock.Revision)
                    .SingleAsync());
            Assert.Equal(
                firstRunLedger.Id,
                await dbContext.InventoryLedgerEntries
                    .AsNoTracking()
                    .Where(entry => entry.ItemId == item.Id)
                    .Select(entry => entry.Id)
                    .SingleAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
            {
                await using var maintenanceConnection = new NpgsqlConnection(maintenanceBuilder.ConnectionString);
                await maintenanceConnection.OpenAsync();
                await using var dropCommand = maintenanceConnection.CreateCommand();
                dropCommand.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);";
                await dropCommand.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task EnsureOperationalRuntimeSchemaAsync_DoesNotDispose_DbConnection()
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureOperationalRuntimeSchemaAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;

        await _dbContext.Database.ExecuteSqlRawAsync("SELECT 1;");
    }

    [Fact]
    public async Task EnsureBusinessDatabaseSchemaAsync_AddsOptionalItemCatalogColumnsToLegacySqlite_AndIsIdempotent()
    {
        var itemId = Guid.NewGuid();
        _dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "LEGACY OPTIONAL CATALOG ITEM",
            NameMatchKey = "LEGACYOPTIONALCATALOGITEM",
            SpecificationOriginal = "LEGACY-SPEC",
            SpecificationMatchKey = "LEGACYSPEC",
            Unit = "EA",
            BoxQuantity = 24m,
            StorageLocation = "REMOVED-LEGACY-LOCATION",
            CurrentStock = 12.5m,
            SimpleMemo = "preserve existing item data",
            LastPurchaseDate = new DateOnly(2026, 7, 1),
            LastSaleDate = new DateOnly(2026, 7, 2)
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await _dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE \"Items\" DROP COLUMN \"BoxQuantity\";");
        await _dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE \"Items\" DROP COLUMN \"StorageLocation\";");
        await _dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE \"Items\" DROP COLUMN \"LastPurchaseDate\";");
        await _dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE \"Items\" DROP COLUMN \"LastSaleDate\";");
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var legacyColumns = await ReadItemColumnNamesAsync();
        Assert.DoesNotContain("BoxQuantity", legacyColumns);
        Assert.DoesNotContain("StorageLocation", legacyColumns);
        Assert.DoesNotContain("LastPurchaseDate", legacyColumns);
        Assert.DoesNotContain("LastSaleDate", legacyColumns);

        await InvokeEnsureBusinessDatabaseSchemaAsync();

        var columnsAfterFirstRun = await ReadItemColumnNamesAsync();
        Assert.Contains("BoxQuantity", columnsAfterFirstRun);
        Assert.Contains("StorageLocation", columnsAfterFirstRun);
        Assert.Contains("LastPurchaseDate", columnsAfterFirstRun);
        Assert.Contains("LastSaleDate", columnsAfterFirstRun);

        _dbContext.ChangeTracker.Clear();
        var migrated = await _dbContext.Items
            .IgnoreQueryFilters()
            .SingleAsync(item => item.Id == itemId);
        Assert.Equal("LEGACY OPTIONAL CATALOG ITEM", migrated.NameOriginal);
        Assert.Equal("LEGACY-SPEC", migrated.SpecificationOriginal);
        Assert.Equal("EA", migrated.Unit);
        Assert.Equal(12.5m, migrated.CurrentStock);
        Assert.Equal("preserve existing item data", migrated.SimpleMemo);
        Assert.Equal(0m, migrated.BoxQuantity);
        Assert.Equal(string.Empty, migrated.StorageLocation);
        Assert.Null(migrated.LastPurchaseDate);
        Assert.Null(migrated.LastSaleDate);

        await InvokeEnsureBusinessDatabaseSchemaAsync();

        Assert.Equal(columnsAfterFirstRun, await ReadItemColumnNamesAsync());
        _dbContext.ChangeTracker.Clear();
        var migratedAfterSecondRun = await _dbContext.Items
            .IgnoreQueryFilters()
            .SingleAsync(item => item.Id == itemId);
        Assert.Equal("LEGACY OPTIONAL CATALOG ITEM", migratedAfterSecondRun.NameOriginal);
        Assert.Equal(12.5m, migratedAfterSecondRun.CurrentStock);
        Assert.Equal(0m, migratedAfterSecondRun.BoxQuantity);
        Assert.Equal(string.Empty, migratedAfterSecondRun.StorageLocation);
        Assert.Null(migratedAfterSecondRun.LastPurchaseDate);
        Assert.Null(migratedAfterSecondRun.LastSaleDate);
    }

    [Fact]
    public async Task EnsureOperationalRuntimeSchemaAsync_AddsPayloadHash_ToLegacyMutationReceipts()
    {
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"ProcessedSyncMutations\";");
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "ProcessedSyncMutations" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "MutationId" TEXT NOT NULL DEFAULT '',
                "DeviceId" TEXT NOT NULL DEFAULT '',
                "EntityName" TEXT NOT NULL DEFAULT '',
                "EntityId" TEXT NOT NULL DEFAULT '',
                "ExpectedRevision" INTEGER NOT NULL DEFAULT 0,
                "ProcessedAtUtc" TEXT NOT NULL
            );
            """);
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "ProcessedSyncMutations"
                 ("Id", "MutationId", "DeviceId", "EntityName", "EntityId", "ExpectedRevision", "ProcessedAtUtc")
             VALUES
                 ({Guid.NewGuid().ToString()}, {"legacy-mutation"}, {"legacy-device"}, {nameof(Customer)},
                  {Guid.NewGuid().ToString()}, {3L}, {new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc)});
             """);

        var method = typeof(DbInitializer).GetMethod(
            "EnsureOperationalRuntimeSchemaAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT \"PayloadHash\" FROM \"ProcessedSyncMutations\" WHERE \"MutationId\" = 'legacy-mutation';";
        var payloadHash = await command.ExecuteScalarAsync();
        Assert.Equal(string.Empty, Assert.IsType<string>(payloadHash));

        var duplicateCaseException = await Assert.ThrowsAsync<SqliteException>(
            () => _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO "ProcessedSyncMutations"
                     ("Id", "MutationId", "DeviceId", "EntityName", "EntityId",
                      "ExpectedRevision", "PayloadHash", "ProcessedAtUtc")
                 VALUES
                     ({Guid.NewGuid().ToString()}, {"LEGACY-MUTATION"}, {"case-variant-device"}, {nameof(Customer)},
                      {Guid.NewGuid().ToString()}, {3L}, {string.Empty}, {DateTime.UtcNow});
                 """));
        Assert.Equal(19, duplicateCaseException.SqliteErrorCode);

        await _dbContext.Database.ExecuteSqlRawAsync("SELECT 1;");
    }

    [Fact]
    public async Task EnsureOperationalRuntimeSchemaAsync_AddsAuditActorLookupIndexToExistingDatabase()
    {
        const string indexName = "IX_AuditLogs_EntityName_EntityId_CreatedAtUtc";
        await _dbContext.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{indexName}\";");

        await InvokeEnsureOperationalRuntimeSchemaAsync();

        await using var command = _connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info('{indexName}');";
        await using var reader = await command.ExecuteReaderAsync();
        var indexedColumns = new List<string>();
        while (await reader.ReadAsync())
            indexedColumns.Add(reader.GetString(2));

        Assert.Equal(
            new[] { "EntityName", "EntityId", "CreatedAtUtc" },
            indexedColumns);
    }

    [Fact]
    public async Task EnsureOperationalRuntimeSchemaAsync_PreservesRetainedLegacyIds_AndSentinelsCanonicalDuplicates()
    {
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"ProcessedSyncMutations\";");
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "ProcessedSyncMutations" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "MutationId" TEXT NOT NULL DEFAULT '',
                "DeviceId" TEXT NOT NULL DEFAULT '',
                "EntityName" TEXT NOT NULL DEFAULT '',
                "EntityId" TEXT NOT NULL DEFAULT '',
                "ExpectedRevision" INTEGER NOT NULL DEFAULT 0,
                "PayloadHash" TEXT NOT NULL DEFAULT '',
                "ProcessedAtUtc" TEXT NOT NULL
            );
            """);

        var retainedId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var duplicateId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var standaloneId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var sentinelCollisionId = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var entityId = Guid.NewGuid();
        var occupiedSentinel =
            $"__legacy_duplicate__:{duplicateId:N}:legacy-case-receipt";
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "ProcessedSyncMutations"
                 ("Id", "MutationId", "DeviceId", "EntityName", "EntityId",
                  "ExpectedRevision", "PayloadHash", "ProcessedAtUtc")
             VALUES
                 ({retainedId}, {"  Legacy-Case-Receipt  "}, {"first-device"}, {nameof(Customer)},
                  {entityId.ToString("D")}, {7L}, {"retained-legacy-payload-hash"},
                  {new DateTime(2026, 7, 10, 1, 0, 0, DateTimeKind.Utc)}),
                 ({duplicateId}, {"LEGACY-CASE-RECEIPT"}, {"second-device"}, {nameof(Customer)},
                  {entityId.ToString("D")}, {7L}, {"duplicate-legacy-payload-hash"},
                  {new DateTime(2026, 7, 10, 2, 0, 0, DateTimeKind.Utc)}),
                 ({standaloneId}, {"  Standalone-Legacy-Id  "}, {"standalone-device"}, {nameof(Customer)},
                  {Guid.NewGuid().ToString("D")}, {2L}, {"standalone-legacy-payload-hash"},
                  {new DateTime(2026, 7, 10, 3, 0, 0, DateTimeKind.Utc)}),
                 ({sentinelCollisionId}, {occupiedSentinel}, {"sentinel-device"}, {nameof(Customer)},
                  {Guid.NewGuid().ToString("D")}, {4L}, {"sentinel-payload-hash"},
                  {new DateTime(2026, 7, 10, 4, 0, 0, DateTimeKind.Utc)});
             """);

        await InvokeEnsureOperationalRuntimeSchemaAsync();

        _dbContext.ChangeTracker.Clear();
        var migrated = await _dbContext.ProcessedSyncMutations
            .AsNoTracking()
            .OrderBy(receipt => receipt.ProcessedAtUtc)
            .ToListAsync();
        Assert.Collection(
            migrated,
            retained =>
            {
                Assert.Equal(retainedId, retained.Id);
                Assert.Equal("  Legacy-Case-Receipt  ", retained.MutationId);
                Assert.Equal("first-device", retained.DeviceId);
                Assert.Equal(entityId.ToString("D"), retained.EntityId);
                Assert.Equal(7, retained.ExpectedRevision);
                Assert.Equal("retained-legacy-payload-hash", retained.PayloadHash);
            },
            duplicate =>
            {
                Assert.Equal(duplicateId, duplicate.Id);
                Assert.Equal($"{occupiedSentinel}:1", duplicate.MutationId);
                Assert.Equal("second-device", duplicate.DeviceId);
                Assert.Equal(entityId.ToString("D"), duplicate.EntityId);
                Assert.Equal(7, duplicate.ExpectedRevision);
                Assert.Equal("duplicate-legacy-payload-hash", duplicate.PayloadHash);
            },
            standalone =>
            {
                Assert.Equal(standaloneId, standalone.Id);
                Assert.Equal("  Standalone-Legacy-Id  ", standalone.MutationId);
                Assert.Equal("standalone-device", standalone.DeviceId);
                Assert.Equal(2, standalone.ExpectedRevision);
                Assert.Equal("standalone-legacy-payload-hash", standalone.PayloadHash);
            },
            sentinel =>
            {
                Assert.Equal(sentinelCollisionId, sentinel.Id);
                Assert.Equal(occupiedSentinel, sentinel.MutationId);
                Assert.Equal("sentinel-device", sentinel.DeviceId);
                Assert.Equal(4, sentinel.ExpectedRevision);
                Assert.Equal("sentinel-payload-hash", sentinel.PayloadHash);
            });

        var mutationIdsAfterFirstRun = migrated
            .Select(receipt => receipt.MutationId)
            .ToArray();
        await InvokeEnsureOperationalRuntimeSchemaAsync();
        _dbContext.ChangeTracker.Clear();
        Assert.Equal(
            mutationIdsAfterFirstRun,
            await _dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .OrderBy(receipt => receipt.ProcessedAtUtc)
                .Select(receipt => receipt.MutationId)
                .ToArrayAsync());

        var duplicateWhitespaceCaseException = await Assert.ThrowsAsync<SqliteException>(
            () => _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO "ProcessedSyncMutations"
                     ("Id", "MutationId", "DeviceId", "EntityName", "EntityId",
                      "ExpectedRevision", "PayloadHash", "ProcessedAtUtc")
                 VALUES
                     ({Guid.NewGuid()}, {"  LEGACY-CASE-RECEIPT  "}, {"third-device"}, {nameof(Customer)},
                      {entityId.ToString("D")}, {7L}, {string.Empty}, {DateTime.UtcNow});
                 """));
        Assert.Equal(19, duplicateWhitespaceCaseException.SqliteErrorCode);
    }

    [Fact]
    public async Task EnsureItemCategoryOptionsForExistingReferencesAsync_CreatesOrReactivatesReferencedCategories()
    {
        _dbContext.Items.Add(new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Referenced item category",
            NameMatchKey = "REFERENCEDITEMCATEGORY",
            CategoryName = "A3 Copier",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock
        });
        _dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "ASSET-CCTV-001",
            ManagementNumber = "ASSET-CCTV-001",
            ItemCategoryName = "CCTV Recorder",
            ItemName = "CCTV Recorder",
            AssetStatus = "ACTIVE"
        });
        _dbContext.ItemCategoryOptions.Add(new ItemCategoryOption
        {
            Id = Guid.NewGuid(),
            Name = "CCTV Recorder",
            IsActive = false,
            IsDeleted = true
        });
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "EnsureItemCategoryOptionsForExistingReferencesAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var options = await _dbContext.ItemCategoryOptions.IgnoreQueryFilters().ToDictionaryAsync(option => option.Name);
        Assert.True(options["A3 Copier"].IsActive);
        Assert.False(options["A3 Copier"].IsDeleted);
        Assert.True(options["CCTV Recorder"].IsActive);
        Assert.False(options["CCTV Recorder"].IsDeleted);
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_RepointsRentalBillingTemplateWithoutUnlinkingAssets()
    {
        var customerId = Guid.Parse("93111111-1111-1111-1111-111111111111");
        var canonicalItemId = Guid.Parse("93222222-2222-2222-2222-222222222222");
        var duplicateItemId = Guid.Parse("93333333-3333-3333-3333-333333333333");
        var profileId = Guid.Parse("93444444-4444-4444-4444-444444444444");
        var assetId = Guid.Parse("93555555-5555-5555-5555-555555555555");
        var firstInvoiceId = Guid.Parse("93666666-6666-6666-6666-666666666666");
        var secondInvoiceId = Guid.Parse("93777777-7777-7777-7777-777777777777");

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server Initializer Customer",
            NameMatchKey = "SERVERINITIALIZERCUSTOMER"
        });
        _dbContext.Items.AddRange(
            new Item
            {
                Id = canonicalItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Server Duplicate Item",
                NameMatchKey = "SERVERDUPLICATEITEM",
                SpecificationOriginal = "A4",
                SpecificationMatchKey = "A4",
                TrackingType = ItemTrackingTypes.Stock
            },
            new Item
            {
                Id = duplicateItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Server Duplicate Item",
                NameMatchKey = "SERVERDUPLICATEITEM",
                SpecificationOriginal = "A4",
                SpecificationMatchKey = "A4",
                TrackingType = ItemTrackingTypes.Stock
            });
        _dbContext.Invoices.AddRange(
            new Invoice
            {
                Id = firstInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "SERVER-INIT-ITEM-MERGE-1",
                InvoiceDate = new DateOnly(2026, 6, 24),
                Lines =
                {
                    new InvoiceLine
                    {
                        InvoiceId = firstInvoiceId,
                        ItemId = canonicalItemId,
                        ItemNameOriginal = "Server Duplicate Item",
                        SpecificationOriginal = "A4",
                        Quantity = 1m
                    },
                    new InvoiceLine
                    {
                        InvoiceId = firstInvoiceId,
                        ItemId = canonicalItemId,
                        ItemNameOriginal = "Server Duplicate Item",
                        SpecificationOriginal = "A4",
                        Quantity = 1m
                    }
                }
            },
            new Invoice
            {
                Id = secondInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "SERVER-INIT-ITEM-MERGE-2",
                InvoiceDate = new DateOnly(2026, 6, 24),
                Lines =
                {
                    new InvoiceLine
                    {
                        InvoiceId = secondInvoiceId,
                        ItemId = canonicalItemId,
                        ItemNameOriginal = "Server Duplicate Item",
                        SpecificationOriginal = "A4",
                        Quantity = 1m
                    }
                }
            });
        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerId = customerId,
            CustomerName = "Server Initializer Customer",
            ItemName = "Server Duplicate Item",
            BillingTemplateJson = JsonSerializer.Serialize(new object[]
            {
                new
                {
                    ItemId = Guid.Parse("93888888-8888-8888-8888-888888888888"),
                    CatalogItemId = duplicateItemId,
                    DisplayItemName = "Server Duplicate Item",
                    BillingLineMode = "묶음",
                    RepresentativeAssetId = assetId,
                    Quantity = 1m,
                    UnitPrice = 15000m,
                    Amount = 15000m,
                    IncludedAssetIds = new[] { assetId }
                }
            })
        });
        _dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "USENET|SERVER|INIT-ITEM-MERGE",
            CustomerId = customerId,
            CustomerName = "Server Initializer Customer",
            CurrentCustomerName = "Server Initializer Customer",
            BillingProfileId = null,
            ItemId = duplicateItemId,
            ItemName = "Server Duplicate Item",
            MonthlyFee = 15000m
        });
        await _dbContext.SaveChangesAsync();
        var revisionBeforeMerge = await _dbContext.Items.IgnoreQueryFilters()
            .MaxAsync(item => item.Revision);

        var method = typeof(DbInitializer).GetMethod(
            "MergeDuplicateItemsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var duplicateTombstone = await _dbContext.Items.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == duplicateItemId);
        Assert.True(duplicateTombstone.IsDeleted);
        Assert.True(duplicateTombstone.Revision > revisionBeforeMerge);
        Assert.Equal(canonicalItemId, (await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId)).ItemId);

        var storedProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == profileId);
        using var document = JsonDocument.Parse(storedProfile.BillingTemplateJson);
        var templateItem = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(Guid.Parse("93888888-8888-8888-8888-888888888888"), templateItem.GetProperty("ItemId").GetGuid());
        Assert.Equal(canonicalItemId, templateItem.GetProperty("CatalogItemId").GetGuid());
        Assert.Contains(
            assetId,
            templateItem.GetProperty("IncludedAssetIds").EnumerateArray().Select(element => element.GetGuid()));
    }

    [Fact]
    public async Task VerifyRequiredOperationalSchemaAsync_Throws_WhenCriticalSchemaColumnMissing()
    {
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"ItemWarehouseStocks\";");
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "ItemWarehouseStocks" (
                "ItemId" TEXT NOT NULL,
                "WarehouseCode" TEXT NOT NULL,
                "Quantity" REAL NOT NULL DEFAULT 0,
                "UpdatedAtUtc" TEXT NOT NULL,
                CONSTRAINT "PK_ItemWarehouseStocks" PRIMARY KEY ("ItemId", "WarehouseCode")
            );
            """);

        var method = typeof(DbInitializer).GetMethod(
            "VerifyRequiredOperationalSchemaAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => task!);
        Assert.Contains("ItemWarehouseStocks", exception.Message);
        Assert.Contains("Revision", exception.Message);
    }

    [Fact]
    public async Task NormalizeInventoryTransferIntegrityAsync_RemovesCrossTenantTransfers()
    {
        var transferId = Guid.NewGuid();
        _dbContext.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Itworld,
            TransferNumber = "TR-INVALID-001",
            TransferDate = new DateOnly(2026, 4, 13),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });
        _dbContext.InventoryTransferLines.Add(new InventoryTransferLine
        {
            Id = Guid.NewGuid(),
            TransferId = transferId,
            ItemNameOriginal = "테스트 품목",
            Unit = "EA",
            Quantity = 1m
        });
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "NormalizeInventoryTransferIntegrityAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        Assert.False(await _dbContext.InventoryTransfers.IgnoreQueryFilters().AnyAsync(current => current.Id == transferId));
        Assert.False(await _dbContext.InventoryTransferLines.IgnoreQueryFilters().AnyAsync(current => current.TransferId == transferId));
    }

    [Fact]
    public async Task CleanupDeletedInvoiceChainAsync_MarksActiveLinesUnderDeletedInvoices()
    {
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Deleted invoice cleanup customer",
            NameMatchKey = "DELETEDINVOICECLEANUPCUSTOMER",
            TradeType = "Sales"
        });
        _dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-DELETED-CLEANUP",
            InvoiceDate = new DateOnly(2026, 5, 28),
            IsDeleted = true
        });
        _dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = lineId,
            InvoiceId = invoiceId,
            ItemNameOriginal = "active line under deleted invoice",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 1000m,
            LineAmount = 1000m,
            IsDeleted = false
        });
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "CleanupDeletedInvoiceChainAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        Assert.True(await _dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.Id == lineId)
            .Select(line => line.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task RepairDeletedItemCurrentStockResiduesAsync_ZeroesOnlyDeletedItems()
    {
        var deletedItemId = Guid.NewGuid();
        var activeItemId = Guid.NewGuid();
        _dbContext.Items.AddRange(
            new Item
            {
                Id = deletedItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Deleted stock residue item",
                NameMatchKey = "DELETEDSTOCKRESIDUEITEM",
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 12m,
                IsDeleted = true
            },
            new Item
            {
                Id = activeItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Active stock item",
                NameMatchKey = "ACTIVESTOCKITEM",
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 8m,
                IsDeleted = false
            });
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "RepairDeletedItemCurrentStockResiduesAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task<int>;
        Assert.NotNull(task);
        var repairedCount = await task!;
        await _dbContext.SaveChangesAsync();

        Assert.Equal(1, repairedCount);
        Assert.Equal(0m, await _dbContext.Items.IgnoreQueryFilters()
            .Where(item => item.Id == deletedItemId)
            .Select(item => item.CurrentStock)
            .SingleAsync());
        Assert.Equal(8m, await _dbContext.Items.IgnoreQueryFilters()
            .Where(item => item.Id == activeItemId)
            .Select(item => item.CurrentStock)
            .SingleAsync());
    }

    [Fact]
    public async Task MergeBusinessDuplicateCustomersAsync_UsesResponsibleOfficeCode_ForDuplicateKey()
    {
        var duplicateA = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "연수 테스트 거래처",
            NameMatchKey = "연수테스트거래처",
            BusinessNumber = "123-45-67890",
            TradeType = "매출",
            Address = "인천 연수구 테스트로 1",
            Phone = "032-000-0000",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
        };
        var duplicateB = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "연수 테스트 거래처",
            NameMatchKey = "연수테스트거래처",
            BusinessNumber = "123-45-67890",
            TradeType = "매출",
            Address = "인천 연수구 테스트로 1",
            Phone = "032-000-0000",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };

        _dbContext.Customers.AddRange(duplicateA, duplicateB);
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "MergeBusinessDuplicateCustomersAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var customers = await _dbContext.Customers.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted && current.NameOriginal == "연수 테스트 거래처")
            .ToListAsync();

        var remaining = Assert.Single(customers);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, remaining.ResponsibleOfficeCode);
    }

    [Fact]
    public async Task MergeBusinessDuplicateCustomersAsync_RepointsRentalAssignmentHistoryCustomerReferences()
    {
        var source = CreateDuplicateMergeCustomer(Guid.Parse("12111111-1111-1111-1111-111111111111"), businessNumber: "123-45-67890");
        var target = CreateDuplicateMergeCustomer(Guid.Parse("12222222-2222-2222-2222-222222222222"), businessNumber: "123-45-67890");
        var historyId = Guid.Parse("12333333-3333-3333-3333-333333333333");
        _dbContext.Customers.AddRange(source, target);
        _dbContext.Invoices.AddRange(
            CreateInitializerInvoice(Guid.Parse("12444444-4444-4444-4444-444444444444"), target.Id, "INIT-MERGE-BIZ-1"),
            CreateInitializerInvoice(Guid.Parse("12555555-5555-5555-5555-555555555555"), target.Id, "INIT-MERGE-BIZ-2"));
        _dbContext.RentalAssetAssignmentHistories.Add(CreateInitializerAssignmentHistory(historyId, source.Id));
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "MergeBusinessDuplicateCustomersAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        Assert.False(await _dbContext.Customers.IgnoreQueryFilters().AnyAsync(customer => customer.Id == source.Id));
        var remaining = Assert.Single(await _dbContext.Customers.IgnoreQueryFilters()
            .Where(customer => !customer.IsDeleted && customer.NameOriginal == source.NameOriginal)
            .ToListAsync());
        Assert.Equal(target.Id, remaining.Id);

        var history = await _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == historyId);
        Assert.Equal(target.Id, history.CustomerId);
        Assert.Equal("AUTO MERGE CUSTOMER", history.CustomerName);
    }

    [Fact]
    public async Task MergeDuplicateCustomersAsync_RepointsRentalAssignmentHistoryCustomerReferences()
    {
        var source = CreateDuplicateMergeCustomer(Guid.Parse("13111111-1111-1111-1111-111111111111"), businessNumber: string.Empty);
        var target = CreateDuplicateMergeCustomer(Guid.Parse("13222222-2222-2222-2222-222222222222"), businessNumber: string.Empty);
        var historyId = Guid.Parse("13333333-3333-3333-3333-333333333333");
        _dbContext.Customers.AddRange(source, target);
        _dbContext.Invoices.AddRange(
            CreateInitializerInvoice(Guid.Parse("13444444-4444-4444-4444-444444444444"), target.Id, "INIT-MERGE-GENERIC-1"),
            CreateInitializerInvoice(Guid.Parse("13555555-5555-5555-5555-555555555555"), target.Id, "INIT-MERGE-GENERIC-2"));
        _dbContext.RentalAssetAssignmentHistories.Add(CreateInitializerAssignmentHistory(historyId, source.Id));
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "MergeDuplicateCustomersAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        Assert.False(await _dbContext.Customers.IgnoreQueryFilters().AnyAsync(customer => customer.Id == source.Id));
        var remaining = Assert.Single(await _dbContext.Customers.IgnoreQueryFilters()
            .Where(customer => !customer.IsDeleted && customer.NameOriginal == source.NameOriginal)
            .ToListAsync());
        Assert.Equal(target.Id, remaining.Id);

        var history = await _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == historyId);
        Assert.Equal(target.Id, history.CustomerId);
        Assert.Equal("AUTO MERGE CUSTOMER", history.CustomerName);
    }

    [Fact]
    public async Task MergeDuplicateRentalAssetsAsync_PreservesDistinctWarehouseAssetsWithDifferentManagementNumbers()
    {
        static RentalAsset CreateWarehouseAsset(Guid id, string managementId, string managementNumber) => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            ManagementCompanyCode = OfficeCodeCatalog.Itworld,
            ManagementId = managementId,
            ManagementNumber = managementNumber,
            AssetKey = $"ITWORLD|{managementNumber}||SL-M3820ND",
            CurrentLocation = "창고",
            InstallSiteName = string.Empty,
            InstallLocation = string.Empty,
            CustomerName = string.Empty,
            CurrentCustomerName = string.Empty,
            ItemCategoryName = "프린터",
            ItemName = "SL-M3820ND",
            Manufacturer = "삼성",
            MachineNumber = string.Empty,
            MonthlyFee = 0m,
            ContractMonths = 0,
            AssetStatus = "창고"
        };

        var firstId = Guid.Parse("13611111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("13622222-2222-2222-2222-222222222222");
        _dbContext.RentalAssets.AddRange(
            CreateWarehouseAsset(firstId, "WAREHOUSE-001", "2012-004"),
            CreateWarehouseAsset(secondId, "WAREHOUSE-002", "2012-005"));
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "MergeDuplicateRentalAssetsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var remainingIds = await _dbContext.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id == firstId || asset.Id == secondId)
            .Select(asset => asset.Id)
            .ToListAsync();
        Assert.Contains(firstId, remainingIds);
        Assert.Contains(secondId, remainingIds);
    }

    [Fact]
    public async Task EnsureRentalAssetsTableAsync_AllowsDeletedNaturalKeyDuplicates_ButBlocksActiveDuplicates()
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureRentalAssetsTableAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, NullLogger.Instance, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;

        var activeAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "asset-index-duplicate",
            ManagementId = "MID-INDEX-DUP",
            ManagementNumber = "MN-INDEX-DUP",
            ItemName = "active asset",
            IsDeleted = false
        };
        var deletedAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "asset-index-duplicate",
            ManagementId = "MID-INDEX-DUP",
            ManagementNumber = "MN-INDEX-DUP",
            ItemName = "deleted asset",
            IsDeleted = true
        };

        _dbContext.RentalAssets.AddRange(activeAsset, deletedAsset);
        await _dbContext.SaveChangesAsync();

        _dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "asset-index-duplicate-other",
            ManagementId = "MID-INDEX-DUP",
            ManagementNumber = "MN-INDEX-DUP-OTHER",
            ItemName = "second active asset",
            IsDeleted = false
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => _dbContext.SaveChangesAsync());
        Assert.Contains("UNIQUE", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepairRentalCustomerLinkageAsync_NormalizesItworldRentalScope_AndPreservesYeonsuScope()
    {
        var brokenProfileId = Guid.Parse("91111111-1111-1111-1111-111111111111");
        var brokenAssetId = Guid.Parse("92222222-2222-2222-2222-222222222222");
        var brokenLogId = Guid.Parse("93333333-3333-3333-3333-333333333333");
        var yeonsuProfileId = Guid.Parse("94444444-4444-4444-4444-444444444444");
        var yeonsuAssetId = Guid.Parse("95555555-5555-5555-5555-555555555555");
        var wrongUsenetCustomerId = Guid.Parse("96666666-6666-6666-6666-666666666666");

        _dbContext.Customers.Add(new Customer
        {
            Id = wrongUsenetCustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Wrong USENET Customer",
            NameMatchKey = "WRONGUSENETCUSTOMER"
        });

        _dbContext.RentalBillingProfiles.AddRange(
            new RentalBillingProfile
            {
                Id = brokenProfileId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerId = wrongUsenetCustomerId,
                CustomerName = "Broken ITWORLD Customer",
                InstallSiteName = "ITWORLD Site",
                ItemName = "Printer",
                MonthlyAmount = 120000m,
                BillingTemplateJson = "[]"
            },
            new RentalBillingProfile
            {
                Id = yeonsuProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                CustomerName = "YEONSU Customer",
                InstallSiteName = "YEONSU Site",
                ItemName = "Copier",
                MonthlyAmount = 90000m,
                BillingTemplateJson = "[]"
            });

        _dbContext.RentalAssets.AddRange(
            new RentalAsset
            {
                Id = brokenAssetId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerId = wrongUsenetCustomerId,
                BillingProfileId = brokenProfileId,
                AssetKey = "ITWORLD|BROKEN-001|SN-BROKEN",
                CustomerName = "Broken ITWORLD Customer",
                CurrentCustomerName = "Broken ITWORLD Customer",
                InstallSiteName = "ITWORLD Site",
                InstallLocation = "ITWORLD Site",
                ItemName = "Printer",
                ManagementNumber = "BROKEN-001",
                MachineNumber = "SN-BROKEN",
                AssetStatus = "ACTIVE",
                BillingEligibilityStatus = string.Empty,
                MonthlyFee = 120000m
            },
            new RentalAsset
            {
                Id = yeonsuAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                BillingProfileId = yeonsuProfileId,
                AssetKey = "USENET|YEONSU-001|SN-YEONSU",
                CustomerName = "YEONSU Customer",
                CurrentCustomerName = "YEONSU Customer",
                InstallSiteName = "YEONSU Site",
                InstallLocation = "YEONSU Site",
                ItemName = "Copier",
                ManagementNumber = "YEONSU-001",
                MachineNumber = "SN-YEONSU",
                AssetStatus = "ACTIVE",
                BillingEligibilityStatus = string.Empty,
                MonthlyFee = 90000m
            });

        _dbContext.RentalBillingLogs.Add(new RentalBillingLog
        {
            Id = brokenLogId,
            BillingProfileId = brokenProfileId,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BillingYearMonth = "202604",
            Status = "PENDING",
            BilledAmount = 120000m
        });

        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "RepairRentalCustomerLinkageAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var fixedProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == brokenProfileId);
        var fixedAsset = await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == brokenAssetId);
        var fixedLog = await _dbContext.RentalBillingLogs.IgnoreQueryFilters().SingleAsync(log => log.Id == brokenLogId);
        var yeonsuProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == yeonsuProfileId);
        var yeonsuAsset = await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == yeonsuAssetId);

        Assert.Equal(TenantScopeCatalog.Itworld, fixedProfile.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedProfile.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedProfile.ManagementCompanyCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedProfile.ResponsibleOfficeCode);

        Assert.Equal(TenantScopeCatalog.Itworld, fixedAsset.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedAsset.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedAsset.ManagementCompanyCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedAsset.ResponsibleOfficeCode);

        Assert.Equal(TenantScopeCatalog.Itworld, fixedLog.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedLog.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedLog.ResponsibleOfficeCode);

        Assert.Equal(TenantScopeCatalog.UsenetGroup, yeonsuProfile.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuProfile.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuProfile.ManagementCompanyCode);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, yeonsuProfile.ResponsibleOfficeCode);

        Assert.Equal(TenantScopeCatalog.UsenetGroup, yeonsuAsset.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuAsset.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuAsset.ManagementCompanyCode);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, yeonsuAsset.ResponsibleOfficeCode);
    }

    [Fact]
    public async Task RepairRentalCustomerLinkageAsync_RecalculatesExplicitBillingTemplateWithoutAddingProfileOnlyAssets()
    {
        var customerId = Guid.Parse("96666666-6666-6666-6666-666666666667");
        var profileId = Guid.Parse("97777777-7777-7777-7777-777777777777");
        var firstAssetId = Guid.Parse("98888888-8888-8888-8888-888888888888");
        var secondAssetId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var unlinkedAssetId = Guid.Parse("99999999-9999-9999-9999-999999999998");

        var templateJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                ItemId = Guid.Parse("9aaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                DisplayItemName = "??? ???",
                BillingLineMode = "??",
                Quantity = 1m,
                UnitPrice = 100m,
                Amount = 100m,
                IncludedAssetIds = new[] { firstAssetId }
            }
        });

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "?? ??? ??? ???",
            NameMatchKey = "??? ??????? ???????"
        });

        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = "?? ??? ??? ???",
            InstallSiteName = "?? ??? ??? ???",
            ItemName = "??? ???",
            BillingType = "??",
            MonthlyAmount = 100m,
            BillingTemplateJson = templateJson,
            IsActive = true
        });

        _dbContext.RentalAssets.AddRange(
            new RentalAsset
            {
                Id = firstAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = profileId,
                CustomerId = customerId,
                AssetKey = "USENET|MONTHLY-001|SN-001",
                CustomerName = "?? ??? ??? ???",
                CurrentCustomerName = "?? ??? ??? ???",
                InstallSiteName = "??? ???",
                InstallLocation = "??? ???",
                ItemName = "???",
                ManagementNumber = "MONTHLY-001",
                MachineNumber = "SN-001",
                AssetStatus = "ACTIVE",
                MonthlyFee = 110000m
            },
            new RentalAsset
            {
                Id = secondAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = profileId,
                CustomerId = customerId,
                AssetKey = "USENET|MONTHLY-002|SN-002",
                CustomerName = "?? ??? ??? ???",
                CurrentCustomerName = "?? ??? ??? ???",
                InstallSiteName = "??? ???",
                InstallLocation = "??? ???",
                ItemName = "???",
                ManagementNumber = "MONTHLY-002",
                MachineNumber = "SN-002",
                AssetStatus = "ACTIVE",
                MonthlyFee = 220000m
            },
            new RentalAsset
            {
                Id = unlinkedAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = null,
                CustomerId = customerId,
                AssetKey = "USENET|MONTHLY-003|SN-003",
                CustomerName = "?? ??? ??? ???",
                CurrentCustomerName = "?? ??? ??? ???",
                InstallSiteName = "??? ???",
                InstallLocation = "??? ???",
                ItemName = "???",
                ManagementNumber = "MONTHLY-003",
                MachineNumber = "SN-003",
                AssetStatus = "ACTIVE",
                MonthlyFee = 330000m
            });

        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "RepairRentalCustomerLinkageAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
        Assert.Equal(110000m, profile.MonthlyAmount);

        using var document = JsonDocument.Parse(profile.BillingTemplateJson);
        var item = document.RootElement.EnumerateArray().Single();
        Assert.Equal(1m, item.GetProperty("Quantity").GetDecimal());
        Assert.Equal(110000m, item.GetProperty("UnitPrice").GetDecimal());
        Assert.Equal(110000m, item.GetProperty("Amount").GetDecimal());
        var includedAssetIds = item.GetProperty("IncludedAssetIds")
            .EnumerateArray()
            .Select(value => value.GetGuid())
            .OrderBy(value => value)
            .ToList();
        Assert.Equal(new[] { firstAssetId }.OrderBy(value => value), includedAssetIds);
        Assert.DoesNotContain(secondAssetId, includedAssetIds);
        Assert.DoesNotContain(unlinkedAssetId, includedAssetIds);

        var firstAsset = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .SingleAsync(asset => asset.Id == firstAssetId);
        var secondAsset = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .SingleAsync(asset => asset.Id == secondAssetId);
        var unlinkedAsset = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .SingleAsync(asset => asset.Id == unlinkedAssetId);
        Assert.Equal(profileId, firstAsset.BillingProfileId);
        Assert.Null(secondAsset.BillingProfileId);
        Assert.Null(unlinkedAsset.BillingProfileId);
    }

    [Fact]
    public async Task RepairRentalCustomerLinkageAsync_PreservesExplicitMultiLineTemplateAssets()
    {
        var customerId = Guid.Parse("97777777-7777-7777-7777-777777777701");
        var profileId = Guid.Parse("97777777-7777-7777-7777-777777777702");
        var firstAssetId = Guid.Parse("97777777-7777-7777-7777-777777777711");
        var missingAssetId = Guid.Parse("97777777-7777-7777-7777-777777777712");
        var secondLineAssetId = Guid.Parse("97777777-7777-7777-7777-777777777713");

        var templateJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                ItemId = Guid.Parse("97777777-7777-7777-7777-777777777721"),
                DisplayItemName = "복합기 렌탈료",
                BillingLineMode = "묶음",
                Quantity = 1m,
                UnitPrice = 110000m,
                Amount = 110000m,
                IncludedAssetIds = new[] { firstAssetId }
            },
            new
            {
                ItemId = Guid.Parse("97777777-7777-7777-7777-777777777722"),
                DisplayItemName = "프린터 렌탈료",
                BillingLineMode = "묶음",
                Quantity = 1m,
                UnitPrice = 220000m,
                Amount = 220000m,
                IncludedAssetIds = new[] { secondLineAssetId }
            }
        });

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "렌탈 월요금 검증 거래처",
            NameMatchKey = "렌탈월요금검증거래처"
        });

        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = "렌탈 월요금 검증 거래처",
            InstallSiteName = "본관",
            ItemName = "렌탈료",
            BillingType = "묶음",
            MonthlyAmount = 330000m,
            BillingTemplateJson = templateJson,
            IsActive = true
        });

        _dbContext.RentalAssets.AddRange(
            BuildRentalAsset(firstAssetId, profileId, customerId, "MONTHLY-MULTI-001", 110000m),
            BuildRentalAsset(missingAssetId, profileId, customerId, "MONTHLY-MULTI-002", 40000m),
            BuildRentalAsset(secondLineAssetId, profileId, customerId, "MONTHLY-MULTI-003", 220000m));

        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "RepairRentalCustomerLinkageAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
        Assert.Equal(330000m, profile.MonthlyAmount);

        using var document = JsonDocument.Parse(profile.BillingTemplateJson);
        var items = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(110000m, items[0].GetProperty("Amount").GetDecimal());
        Assert.Equal(220000m, items[1].GetProperty("Amount").GetDecimal());

        var allIncludedAssetIds = items
            .SelectMany(item => item.GetProperty("IncludedAssetIds").EnumerateArray().Select(value => value.GetGuid()))
            .OrderBy(value => value)
            .ToList();
        Assert.Equal(new[] { firstAssetId, secondLineAssetId }.OrderBy(value => value), allIncludedAssetIds);
        Assert.DoesNotContain(missingAssetId, allIncludedAssetIds);
    }

    private static RentalAsset BuildRentalAsset(
        Guid assetId,
        Guid profileId,
        Guid customerId,
        string managementNumber,
        decimal monthlyFee)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BillingProfileId = profileId,
            CustomerId = customerId,
            AssetKey = $"USENET|{managementNumber}|SN-{managementNumber}",
            CustomerName = "렌탈 월요금 검증 거래처",
            CurrentCustomerName = "렌탈 월요금 검증 거래처",
            InstallSiteName = "본관",
            InstallLocation = "본관",
            ItemName = "복합기",
            ManagementNumber = managementNumber,
            MachineNumber = $"SN-{managementNumber}",
            AssetStatus = "임대",
            MonthlyFee = monthlyFee
        };

    [Fact]
    public async Task RepairRentalCustomerLinkageAsync_ResolvesProfileCustomerFromTemplateLinkedAsset()
    {
        var customerId = Guid.Parse("9bbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var profileId = Guid.Parse("9ccccccc-cccc-cccc-cccc-cccccccccccc");
        var assetId = Guid.Parse("9ddddddd-dddd-dddd-dddd-dddddddddddd");

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "??? ????[??? ?????]",
            NameMatchKey = "??? ??????? ?????"
        });

        var templateJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                ItemId = Guid.Parse("9eeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                DisplayItemName = "IMC2010",
                BillingLineMode = "??",
                Quantity = 1m,
                UnitPrice = 240000m,
                Amount = 240000m,
                IncludedAssetIds = new[] { assetId }
            }
        });

        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = "[???]???-??? ?????",
            InstallSiteName = "[???]???-??? ?????",
            ItemName = "IMC2010",
            BillingType = "??",
            MonthlyAmount = 240000m,
            BillingTemplateJson = templateJson,
            IsActive = true
        });

        _dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            CustomerId = customerId,
            AssetKey = "USENET|HEALTH-001|SN-HEALTH",
            CustomerName = "??? ????[??? ?????]",
            CurrentCustomerName = "??? ????[??? ?????]",
            InstallSiteName = "?.??",
            InstallLocation = "?.??",
            ItemName = "IMC2010",
            ManagementNumber = "HEALTH-001",
            MachineNumber = "SN-HEALTH",
            AssetStatus = "ACTIVE",
            MonthlyFee = 90000m
        });

        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "RepairRentalCustomerLinkageAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
        Assert.Equal(customerId, profile.CustomerId);
        Assert.Equal("??? ????[??? ?????]", profile.CustomerName);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, profile.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, profile.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, profile.ManagementCompanyCode);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, profile.ResponsibleOfficeCode);
        Assert.Equal(90000m, profile.MonthlyAmount);
    }

    [Fact]
    public async Task RepairRentalCustomerLinkageAsync_ResolvesUniqueCustomerAcrossResponsibleOfficeByName()
    {
        var customerId = Guid.Parse("9f111111-1111-1111-1111-111111111111");
        var profileId = Guid.Parse("9f222222-2222-2222-2222-222222222222");
        var assetId = Guid.Parse("9f333333-3333-3333-3333-333333333333");

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "연수구청[여성아동과]",
            NameMatchKey = "연수구청[여성아동과]"
        });

        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = "연수구청[여성아동과]",
            InstallSiteName = "사무실",
            ItemName = "IMC2010",
            BillingType = "묶음",
            MonthlyAmount = 300000m,
            BillingTemplateJson = "[]",
            IsActive = true
        });

        _dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BillingProfileId = null,
            AssetKey = "USENET|YEONSU-DEPT-001|SN-DEPT",
            CustomerName = "연수구청[여성아동과]",
            CurrentCustomerName = "연수구청[여성아동과]",
            InstallSiteName = "사무실",
            InstallLocation = "사무실",
            ItemName = "IMC2010",
            ManagementNumber = "YEONSU-DEPT-001",
            MachineNumber = "SN-DEPT",
            AssetStatus = "ACTIVE",
            MonthlyFee = 300000m
        });

        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "RepairRentalCustomerLinkageAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
        var asset = await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);

        Assert.Equal(customerId, profile.CustomerId);
        Assert.Equal("연수구청[여성아동과]", profile.CustomerName);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, profile.ResponsibleOfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, profile.OfficeCode);
        Assert.Equal(customerId, asset.CustomerId);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, asset.ResponsibleOfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, asset.OfficeCode);
        Assert.Equal(profileId, asset.BillingProfileId);
    }

    [Fact]
    public async Task RepairRentalCustomerLinkageAsync_ResolvesKnownPublicOfficeAliasNames()
    {
        var waterCustomerId = Guid.Parse("9f444444-4444-4444-4444-444444444444");
        var waterProfileId = Guid.Parse("9f555555-5555-5555-5555-555555555555");
        var waterAssetId = Guid.Parse("9f666666-6666-6666-6666-666666666666");
        var healthCustomerId = Guid.Parse("9f777777-7777-7777-7777-777777777777");
        var healthProfileId = Guid.Parse("9f888888-8888-8888-8888-888888888888");
        var healthAssetId = Guid.Parse("9f999999-9999-9999-9999-999999999999");

        const string waterCustomerName = "\uC0C1\uC218\uB3C4\uC0AC\uC5C5\uBCF8\uBD80 \uB9D1\uC740\uBB3C\uC5F0\uAD6C\uC18C";
        const string waterAliasName = "[\uC0C1\uC218\uB3C4\uC0AC\uC5C5\uC18C]\uB9D1\uC740\uBB3C\uC5F0\uAD6C\uC18C";
        const string healthCustomerName = "\uC5F0\uC218\uAD6C\uCCAD[\uAC74\uAC15\uC99D\uC9C4\uACFC]";
        const string healthAliasName = "[\uC5F0\uC218\uAD6C]\uBCF4\uAC74\uC18C-\uAC74\uAC15\uC99D\uC9C4\uACFC";

        _dbContext.Customers.AddRange(
            new Customer
            {
                Id = waterCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = waterCustomerName,
                NameMatchKey = waterCustomerName
            },
            new Customer
            {
                Id = healthCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = healthCustomerName,
                NameMatchKey = healthCustomerName
            });

        _dbContext.RentalBillingProfiles.AddRange(
            new RentalBillingProfile
            {
                Id = waterProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "PUBLIC-ALIAS-WATER",
                CustomerName = waterAliasName,
                InstallSiteName = "\uC218\uC9C8\uBD84\uC11D\uD300",
                ItemName = "IMC2010",
                BillingType = "\uBB36\uC74C",
                MonthlyAmount = 110000m,
                BillingTemplateJson = "[]",
                IsActive = true
            },
            new RentalBillingProfile
            {
                Id = healthProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "PUBLIC-ALIAS-HEALTH",
                CustomerName = healthAliasName,
                InstallSiteName = healthAliasName,
                ItemName = "IMC2010",
                BillingType = "\uBB36\uC74C",
                MonthlyAmount = 240000m,
                BillingTemplateJson = "[]",
                IsActive = true
            });

        _dbContext.RentalAssets.AddRange(
            new RentalAsset
            {
                Id = waterAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = waterProfileId,
                AssetKey = "USENET|2311-005|WATER|IMC2010",
                CustomerName = waterAliasName,
                CurrentCustomerName = waterAliasName,
                InstallSiteName = "\uC218\uC9C8\uBD84\uC11D\uD300",
                InstallLocation = "\uC218\uC9C8\uBD84\uC11D\uD300",
                ItemName = "IMC2010",
                ManagementNumber = "2311-005",
                MachineNumber = "WATER-001",
                AssetStatus = "ACTIVE",
                MonthlyFee = 110000m
            },
            new RentalAsset
            {
                Id = healthAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = healthProfileId,
                AssetKey = "USENET|2401-011|HEALTH|IMC2010",
                CustomerName = healthAliasName,
                CurrentCustomerName = healthAliasName,
                InstallSiteName = "\uC2E4.\uACFC\uB0B4",
                InstallLocation = "\uC2E4.\uACFC\uB0B4",
                ItemName = "IMC2010",
                ManagementNumber = "2401-011",
                MachineNumber = "HEALTH-001",
                AssetStatus = "ACTIVE",
                MonthlyFee = 90000m
            });

        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "RepairRentalCustomerLinkageAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var waterProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == waterProfileId);
        var healthProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == healthProfileId);
        var waterAsset = await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == waterAssetId);
        var healthAsset = await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == healthAssetId);

        Assert.Equal(waterCustomerId, waterProfile.CustomerId);
        Assert.Equal(waterCustomerName, waterProfile.CustomerName);
        Assert.Equal(waterCustomerId, waterAsset.CustomerId);
        Assert.Equal(OfficeCodeCatalog.Usenet, waterProfile.ResponsibleOfficeCode);

        Assert.Equal(healthCustomerId, healthProfile.CustomerId);
        Assert.Equal(healthCustomerName, healthProfile.CustomerName);
        Assert.Equal(healthCustomerId, healthAsset.CustomerId);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, healthProfile.ResponsibleOfficeCode);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, healthAsset.ResponsibleOfficeCode);
    }

    [Fact]
    public void DbInitializerBestEffortFailures_AreLoggedInsteadOfSilentlySwallowed()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dbInitializerSources = Directory
            .GetFiles(
                Path.Combine(repositoryRoot.FullName, "Server"),
                "DbInitializer*.cs",
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText)
            .ToArray();

        var combinedSource = string.Join(Environment.NewLine, dbInitializerSources);

        Assert.DoesNotMatch(
            new Regex(@"catch\s*(\([^)]*\))?\s*\{\s*\}", RegexOptions.Multiline),
            combinedSource);
        Assert.Contains("TraceIgnoredDbInitializerException", combinedSource, StringComparison.Ordinal);
        Assert.Contains("LogBestEffortSchemaWarning", combinedSource, StringComparison.Ordinal);
        Assert.Contains("Best-effort schema operation failed", combinedSource, StringComparison.Ordinal);
    }

    private static Customer CreateDuplicateMergeCustomer(Guid id, string businessNumber)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "AUTO MERGE CUSTOMER",
            NameMatchKey = "AUTOMERGECUSTOMER",
            BusinessNumber = businessNumber,
            TradeType = CustomerClassificationNormalizer.Sales,
            CreatedAtUtc = new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc)
        };

    private static Invoice CreateInitializerInvoice(Guid id, Guid customerId, string invoiceNumber)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = invoiceNumber,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 22),
            TotalAmount = 1000m,
            SupplyAmount = 1000m,
            IsDeleted = false
        };

    private static RentalAssetAssignmentHistory CreateInitializerAssignmentHistory(Guid id, Guid customerId)
        => new()
        {
            Id = id,
            AssetId = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = "AUTO MERGE CUSTOMER",
            InstallLocation = "Initializer history site",
            ItemName = "Initializer history item",
            ManagementNumber = "INIT-HISTORY-001",
            IsCurrent = false,
            IsDeleted = false
        };

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private async Task InvokeEnsureOperationalRuntimeSchemaAsync()
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureOperationalRuntimeSchemaAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private async Task InvokeEnsureBusinessDatabaseSchemaAsync()
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureBusinessDatabaseSchemaAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(
            null,
            new object?[] { _dbContext, NullLogger.Instance, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private async Task InvokeEnsureInvoiceVersionColumnsAsync()
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureInvoiceVersionColumnsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(
            null,
            new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private async Task InvokeBackfillOperationalOfficeOwnershipAsync()
    {
        var method = typeof(DbInitializer).GetMethod(
            "BackfillOperationalOfficeOwnershipAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(
            null,
            new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private async Task InvokeStartupScopeRepairSequenceAsync()
    {
        await InvokeEnsureBusinessDatabaseSchemaAsync();
        await InvokeEnsureOperationalRuntimeSchemaAsync();

        var verifyMethod = typeof(DbInitializer).GetMethod(
            "VerifyRequiredOperationalSchemaAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(verifyMethod);
        var verifyTask = Assert.IsAssignableFrom<Task>(
            verifyMethod!.Invoke(null, [_dbContext, CancellationToken.None]));
        await verifyTask;

        var customerScopeMethod = typeof(DbInitializer).GetMethod(
            "BackfillCustomerScopeFieldsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(customerScopeMethod);
        var customerScopeTask = Assert.IsAssignableFrom<Task>(
            customerScopeMethod!.Invoke(null, [_dbContext, CancellationToken.None]));
        await customerScopeTask;

        await InvokeBackfillOperationalOfficeOwnershipAsync();

        await InvokeEnsureInvoiceVersionColumnsAsync();
        await new InventoryLedgerService(_dbContext).RebuildAsync();
    }

    private async Task<string[]> ReadItemColumnNamesAsync()
    {
        var names = new List<string>();
        await using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"Items\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(reader.GetOrdinal("name")));

        return names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    private async Task InvokeEnsureDefaultRentalManagementCompaniesAsync()
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureDefaultRentalManagementCompaniesAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private async Task InvokeEnsureDefaultCompanyProfilesAsync()
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureDefaultCompanyProfilesAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (directory.GetFiles("*.sln").Length > 0)
                return directory;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; }
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public bool HasPermission(string permission) => IsAdmin;
    }
}
