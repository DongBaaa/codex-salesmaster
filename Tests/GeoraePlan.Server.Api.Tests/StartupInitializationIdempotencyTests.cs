using System.Reflection;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class StartupInitializationIdempotencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _dbContext;

    public StartupInitializationIdempotencyTests()
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

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new AppDbContext(options, currentUser, new RevisionClock());
        _dbContext.Database.EnsureCreated();
    }

    [Fact]
    public void NormalizeRentalAssetScope_CanonicalSharedOwnerScope_IsUnchanged()
    {
        var asset = new RentalAsset
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ManagementCompanyCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet
        };
        var before = (
            asset.TenantCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode,
            asset.ResponsibleOfficeCode);

        var changed = Assert.IsType<bool>(InvokePrivate(
            "NormalizeRentalAssetScope",
            asset));

        Assert.False(changed);
        Assert.Equal(
            before,
            (
                asset.TenantCode,
                asset.OfficeCode,
                asset.ManagementCompanyCode,
                asset.ResponsibleOfficeCode));
    }

    [Fact]
    public async Task EnsureDefaultCompanyProfilesAsync_SecondRun_DoesNotChangeTrackedStateOrPersistence()
    {
        await InvokePrivateTaskAsync(
            "EnsureDefaultCompanyProfilesAsync",
            _dbContext,
            CancellationToken.None);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var profilesBefore = await ReadCompanyProfileVersionsAsync();
        var auditCountBefore = await _dbContext.AuditLogs.CountAsync();
        _dbContext.ChangeTracker.Clear();

        await InvokePrivateTaskAsync(
            "EnsureDefaultCompanyProfilesAsync",
            _dbContext,
            CancellationToken.None);

        AssertNoPendingChanges();
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        Assert.Equal(profilesBefore, await ReadCompanyProfileVersionsAsync());
        Assert.Equal(auditCountBefore, await _dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task EnsureDefaultTenantConfigurationAsync_SecondRun_DoesNotChangeTrackedStateOrPersistence()
    {
        await InvokePrivateTaskAsync(
            "EnsureDefaultTenantConfigurationAsync",
            _dbContext,
            CancellationToken.None);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var tenantsBefore = await ReadTenantDefinitionVersionsAsync();
        var officesBefore = await ReadTenantOfficeDefinitionVersionsAsync();
        var policiesBefore = await ReadDataSharingPolicyVersionsAsync();
        var auditCountBefore = await _dbContext.AuditLogs.CountAsync();
        _dbContext.ChangeTracker.Clear();

        await InvokePrivateTaskAsync(
            "EnsureDefaultTenantConfigurationAsync",
            _dbContext,
            CancellationToken.None);

        AssertNoPendingChanges();
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        Assert.Equal(tenantsBefore, await ReadTenantDefinitionVersionsAsync());
        Assert.Equal(officesBefore, await ReadTenantOfficeDefinitionVersionsAsync());
        Assert.Equal(policiesBefore, await ReadDataSharingPolicyVersionsAsync());
        Assert.Equal(auditCountBefore, await _dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task EnsureDefaultTenantConfigurationAsync_PreservesAdministratorStateAndTombstones()
    {
        await InvokePrivateTaskAsync(
            "EnsureDefaultTenantConfigurationAsync",
            _dbContext,
            CancellationToken.None);
        await _dbContext.SaveChangesAsync();

        var tenant = await _dbContext.TenantDefinitions
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.TenantCode == TenantScopeCatalog.Itworld);
        tenant.DisplayName = "administrator-disabled-tenant";
        tenant.Description = "administrator-owned description";
        tenant.IsActive = false;
        tenant.IsDeleted = true;

        var office = await _dbContext.TenantOfficeDefinitions
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.OfficeCode == OfficeCodeCatalog.Yeonsu);
        office.DisplayName = "administrator-disabled-office";
        office.IsHeadOffice = true;
        office.IsActive = false;
        office.IsDeleted = true;

        var defaultPolicy = await _dbContext.DataSharingPolicies
            .IgnoreQueryFilters()
            .SingleAsync(entity =>
                entity.SourceOfficeCode == OfficeCodeCatalog.Yeonsu &&
                entity.TargetOfficeCode == OfficeCodeCatalog.Usenet);
        defaultPolicy.ShareCustomers = false;
        defaultPolicy.ShareItems = false;
        defaultPolicy.ShareInvoices = false;
        defaultPolicy.SharePayments = false;
        defaultPolicy.ShareContracts = false;
        defaultPolicy.ShareReports = false;
        defaultPolicy.ShareRentals = false;
        defaultPolicy.ShareDeliveries = false;
        defaultPolicy.Note = "administrator-disabled-policy";
        defaultPolicy.IsActive = false;
        defaultPolicy.IsDeleted = true;

        var reversePolicy = new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            ShareItems = true,
            IsActive = true,
            IsDeleted = false,
            Note = "administrator-enabled-reverse-policy"
        };
        _dbContext.DataSharingPolicies.Add(reversePolicy);

        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var tenantsBefore = await ReadTenantDefinitionVersionsAsync();
        var officesBefore = await ReadTenantOfficeDefinitionVersionsAsync();
        var policiesBefore = await ReadDataSharingPolicyVersionsAsync();
        var auditCountBefore = await _dbContext.AuditLogs.CountAsync();
        _dbContext.ChangeTracker.Clear();

        await InvokePrivateTaskAsync(
            "EnsureDefaultTenantConfigurationAsync",
            _dbContext,
            CancellationToken.None);

        AssertNoPendingChanges();
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        Assert.Equal(tenantsBefore, await ReadTenantDefinitionVersionsAsync());
        Assert.Equal(officesBefore, await ReadTenantOfficeDefinitionVersionsAsync());
        Assert.Equal(policiesBefore, await ReadDataSharingPolicyVersionsAsync());
        Assert.Equal(auditCountBefore, await _dbContext.AuditLogs.CountAsync());

        var preservedTenant = await _dbContext.TenantDefinitions.IgnoreQueryFilters()
            .SingleAsync(entity => entity.TenantCode == TenantScopeCatalog.Itworld);
        Assert.Equal("administrator-disabled-tenant", preservedTenant.DisplayName);
        Assert.Equal("administrator-owned description", preservedTenant.Description);
        Assert.False(preservedTenant.IsActive);
        Assert.True(preservedTenant.IsDeleted);

        var preservedOffice = await _dbContext.TenantOfficeDefinitions.IgnoreQueryFilters()
            .SingleAsync(entity => entity.OfficeCode == OfficeCodeCatalog.Yeonsu);
        Assert.Equal("administrator-disabled-office", preservedOffice.DisplayName);
        Assert.True(preservedOffice.IsHeadOffice);
        Assert.False(preservedOffice.IsActive);
        Assert.True(preservedOffice.IsDeleted);

        var preservedDefaultPolicy = await _dbContext.DataSharingPolicies.IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == defaultPolicy.Id);
        Assert.False(preservedDefaultPolicy.ShareCustomers);
        Assert.False(preservedDefaultPolicy.ShareItems);
        Assert.False(preservedDefaultPolicy.ShareInvoices);
        Assert.False(preservedDefaultPolicy.SharePayments);
        Assert.False(preservedDefaultPolicy.ShareContracts);
        Assert.False(preservedDefaultPolicy.ShareReports);
        Assert.False(preservedDefaultPolicy.ShareRentals);
        Assert.False(preservedDefaultPolicy.ShareDeliveries);
        Assert.Equal("administrator-disabled-policy", preservedDefaultPolicy.Note);
        Assert.False(preservedDefaultPolicy.IsActive);
        Assert.True(preservedDefaultPolicy.IsDeleted);

        var preservedReversePolicy = await _dbContext.DataSharingPolicies.IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == reversePolicy.Id);
        Assert.True(preservedReversePolicy.IsActive);
        Assert.False(preservedReversePolicy.IsDeleted);
        Assert.Equal("administrator-enabled-reverse-policy", preservedReversePolicy.Note);
    }

    [Fact]
    public async Task EnsureSeedUserAsync_SameValidPasswordOnSecondRun_DoesNotChangeHashOrPersistence()
    {
        const string username = "startup-idempotency-user";
        const string password = "valid-test-password";

        await InvokeEnsureSeedUserAsync(username, password);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var userBefore = await ReadUserVersionAsync(username);
        Assert.True(BCrypt.Net.BCrypt.Verify(password, userBefore.PasswordHash));
        var auditCountBefore = await _dbContext.AuditLogs.CountAsync();
        _dbContext.ChangeTracker.Clear();

        await InvokeEnsureSeedUserAsync(username, password);

        AssertNoPendingChanges();
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        Assert.Equal(userBefore, await ReadUserVersionAsync(username));
        Assert.Equal(auditCountBefore, await _dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task EnsureSeedUserAsync_MalformedStoredHash_IsRepairedThenIdempotent()
    {
        const string username = "startup-malformed-hash-user";
        const string password = "valid-test-password";
        const string malformedHash = "$2a$";

        await InvokeEnsureSeedUserAsync(username, password);
        await _dbContext.SaveChangesAsync();

        var user = await _dbContext.Users
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Username == username);
        user.PasswordHash = malformedHash;
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await InvokeEnsureSeedUserAsync(username, password);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var repairedUser = await ReadUserVersionAsync(username);
        Assert.NotEqual(malformedHash, repairedUser.PasswordHash);
        Assert.True(BCrypt.Net.BCrypt.Verify(password, repairedUser.PasswordHash));
        var auditCountBeforeSecondRun = await _dbContext.AuditLogs.CountAsync();
        _dbContext.ChangeTracker.Clear();

        await InvokeEnsureSeedUserAsync(username, password);

        AssertNoPendingChanges();
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        Assert.Equal(repairedUser, await ReadUserVersionAsync(username));
        Assert.Equal(auditCountBeforeSecondRun, await _dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task EnsureRentalAssetsTableAsync_SecondRun_DoesNotChangeSqliteSchemaVersionOrIndexes()
    {
        await InvokePrivateTaskAsync(
            "EnsureRentalAssetsTableAsync",
            _dbContext,
            NullLogger.Instance,
            CancellationToken.None);

        var schemaVersionBefore = await ReadSchemaVersionAsync();
        var indexDefinitionsBefore = await ReadRentalAssetUniqueIndexDefinitionsAsync();
        Assert.Equal(3, indexDefinitionsBefore.Length);

        await InvokePrivateTaskAsync(
            "EnsureRentalAssetsTableAsync",
            _dbContext,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(schemaVersionBefore, await ReadSchemaVersionAsync());
        Assert.Equal(indexDefinitionsBefore, await ReadRentalAssetUniqueIndexDefinitionsAsync());
    }

    [Fact]
    public async Task EnsureRentalAssetsTableAsync_WhenReplacementCannotEnforceUniqueness_FailsAndPreservesLegacyIndex()
    {
        await InvokePrivateTaskAsync(
            "EnsureRentalAssetsTableAsync",
            _dbContext,
            NullLogger.Instance,
            CancellationToken.None);
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP INDEX "IX_RentalAssets_ManagementId";
            CREATE INDEX "IX_RentalAssets_ManagementId"
                ON "RentalAssets" ("ManagementId");
            """);

        _dbContext.RentalAssets.AddRange(
            CreateRentalAsset("atomic-index-a", "duplicate-management-id", "atomic-number-a"),
            CreateRentalAsset("atomic-index-b", "duplicate-management-id", "atomic-number-b"));
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var schemaVersionBefore = await ReadSchemaVersionAsync();
        var indexDefinitionBefore = await ReadIndexDefinitionAsync(
            "IX_RentalAssets_ManagementId");
        Assert.NotNull(indexDefinitionBefore);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokePrivateTaskAsync(
                "EnsureRentalAssetsTableAsync",
                _dbContext,
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Equal(schemaVersionBefore, await ReadSchemaVersionAsync());
        Assert.Equal(
            indexDefinitionBefore,
            await ReadIndexDefinitionAsync("IX_RentalAssets_ManagementId"));
    }

    [Fact]
    public async Task EnsureRentalAssetsTableAsync_WhenRequiredUniqueIndexCannotBeCreated_FailsWithIndexMissing()
    {
        await InvokePrivateTaskAsync(
            "EnsureRentalAssetsTableAsync",
            _dbContext,
            NullLogger.Instance,
            CancellationToken.None);
        await _dbContext.Database.ExecuteSqlRawAsync(
            "DROP INDEX \"IX_RentalAssets_ManagementId\";");

        _dbContext.RentalAssets.AddRange(
            CreateRentalAsset("missing-index-a", "duplicate-missing-index", "missing-number-a"),
            CreateRentalAsset("missing-index-b", "duplicate-missing-index", "missing-number-b"));
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var schemaVersionBefore = await ReadSchemaVersionAsync();
        Assert.Null(await ReadIndexDefinitionAsync("IX_RentalAssets_ManagementId"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokePrivateTaskAsync(
                "EnsureRentalAssetsTableAsync",
                _dbContext,
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Equal(schemaVersionBefore, await ReadSchemaVersionAsync());
        Assert.Null(await ReadIndexDefinitionAsync("IX_RentalAssets_ManagementId"));
    }

    [Fact]
    public async Task EnsureBusinessDatabaseSchemaAsync_CompleteSqliteSchema_DoesNotLogDatabaseCommandErrors()
    {
        var commandErrors = new List<string>();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .LogTo(
                commandErrors.Add,
                [RelationalEventId.CommandError],
                LogLevel.Error)
            .Options;
        await using var dbContext = new AppDbContext(options, currentUser, new RevisionClock());
        await dbContext.Database.EnsureCreatedAsync();
        commandErrors.Clear();

        await InvokePrivateTaskAsync(
            "EnsureBusinessDatabaseSchemaAsync",
            dbContext,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Empty(commandErrors);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private void AssertNoPendingChanges()
    {
        _dbContext.ChangeTracker.DetectChanges();

        var pendingChanges = _dbContext.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => $"{entry.Metadata.ClrType.Name}:{entry.State}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(pendingChanges);
        Assert.False(_dbContext.ChangeTracker.HasChanges());
    }

    private async Task<(Guid Id, long Revision, DateTime UpdatedAtUtc)[]> ReadCompanyProfileVersionsAsync()
        => await _dbContext.CompanyProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(entity => entity.Id)
            .Select(entity => new ValueTuple<Guid, long, DateTime>(
                entity.Id,
                entity.Revision,
                entity.UpdatedAtUtc))
            .ToArrayAsync();

    private async Task<(Guid Id, long Revision, DateTime UpdatedAtUtc)[]> ReadTenantDefinitionVersionsAsync()
        => await _dbContext.TenantDefinitions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(entity => entity.Id)
            .Select(entity => new ValueTuple<Guid, long, DateTime>(
                entity.Id,
                entity.Revision,
                entity.UpdatedAtUtc))
            .ToArrayAsync();

    private async Task<(Guid Id, long Revision, DateTime UpdatedAtUtc)[]> ReadTenantOfficeDefinitionVersionsAsync()
        => await _dbContext.TenantOfficeDefinitions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(entity => entity.Id)
            .Select(entity => new ValueTuple<Guid, long, DateTime>(
                entity.Id,
                entity.Revision,
                entity.UpdatedAtUtc))
            .ToArrayAsync();

    private async Task<(Guid Id, long Revision, DateTime UpdatedAtUtc)[]> ReadDataSharingPolicyVersionsAsync()
        => await _dbContext.DataSharingPolicies
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(entity => entity.Id)
            .Select(entity => new ValueTuple<Guid, long, DateTime>(
                entity.Id,
                entity.Revision,
                entity.UpdatedAtUtc))
            .ToArrayAsync();

    private async Task<(string PasswordHash, long Revision, DateTime UpdatedAtUtc)> ReadUserVersionAsync(
        string username)
    {
        var user = await _dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(entity => entity.Username == username);

        return (user.PasswordHash, user.Revision, user.UpdatedAtUtc);
    }

    private async Task<long> ReadSchemaVersionAsync()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA schema_version;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<string[]> ReadRentalAssetUniqueIndexDefinitionsAsync()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT "name" || ':' || COALESCE("sql", '')
            FROM "sqlite_master"
            WHERE "type" = 'index'
              AND "name" IN (
                  'IX_RentalAssets_TenantCode_AssetKey',
                  'IX_RentalAssets_ManagementId',
                  'IX_RentalAssets_ManagementNumber')
            ORDER BY "name";
            """;

        await using var reader = await command.ExecuteReaderAsync();
        var definitions = new List<string>();
        while (await reader.ReadAsync())
            definitions.Add(reader.GetString(0));

        return definitions.ToArray();
    }

    private async Task<string?> ReadIndexDefinitionAsync(string indexName)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"type\" = 'index' AND \"name\" = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", indexName);
        return await command.ExecuteScalarAsync() as string;
    }

    private static RentalAsset CreateRentalAsset(
        string assetKey,
        string managementId,
        string managementNumber)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = assetKey,
            ManagementId = managementId,
            ManagementNumber = managementNumber,
            ItemName = assetKey,
            IsDeleted = false
        };

    private Task InvokeEnsureSeedUserAsync(string username, string password)
        => InvokePrivateTaskAsync(
            "EnsureSeedUserAsync",
            _dbContext,
            NullLogger.Instance,
            username,
            password,
            "Admin",
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeAdmin,
            false,
            true,
            CancellationToken.None);

    private static object? InvokePrivate(string methodName, params object?[] arguments)
    {
        var method = typeof(DbInitializer).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return method!.Invoke(null, arguments);
    }

    private static async Task InvokePrivateTaskAsync(string methodName, params object?[] arguments)
    {
        var task = Assert.IsAssignableFrom<Task>(InvokePrivate(methodName, arguments));
        await task;
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
