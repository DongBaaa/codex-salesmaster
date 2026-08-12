using System.Reflection;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class StartupRequiredIntegrityRegressionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _dbContext;

    public StartupRequiredIntegrityRegressionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _dbContext = new AppDbContext(
            options,
            new TestCurrentUserContext(),
            new RevisionClock());
        _dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task UserScopeNormalization_WhenSqliteTriggerRejectsInvalidRow_FailsRequiredStartupPath()
    {
        _dbContext.Users.Add(new UserAccount
        {
            Username = "invalid-scope-user",
            PasswordHash = "test-only",
            Role = "User",
            TenantCode = "INVALID_TENANT",
            OfficeCode = "INVALID_OFFICE",
            ScopeType = "INVALID_SCOPE"
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER "TR_Users_RejectOfficeNormalization"
            BEFORE UPDATE OF "OfficeCode" ON "Users"
            BEGIN
                SELECT RAISE(ABORT, 'scope normalization blocked');
            END;
            """);

        var normalizationFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokePrivateTaskAsync(
                "EnsureUserOfficeCodeColumnAsync",
                _dbContext,
                CancellationToken.None));

        Assert.Equal(
            "Required Users office-scope normalization failed.",
            normalizationFailure.Message);
        Assert.IsType<SqliteException>(normalizationFailure.InnerException);

        var verificationFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokePrivateTaskAsync(
                "VerifyNonAdminUserScopeDomainAsync",
                _dbContext,
                CancellationToken.None));
        Assert.Contains("row-domain verification failed", verificationFailure.Message);
        Assert.Empty(await _dbContext.UserPermissions.ToListAsync());
    }

    [Fact]
    public async Task UserTenantScopeNormalization_WhenSqliteTriggerRejectsInvalidRow_FailsRequiredStartupPath()
    {
        _dbContext.Users.Add(new UserAccount
        {
            Username = "invalid-tenant-scope-user",
            PasswordHash = "test-only",
            Role = "User",
            TenantCode = "INVALID_TENANT",
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = "INVALID_SCOPE"
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER "TR_Users_RejectTenantScopeNormalization"
            BEFORE UPDATE OF "TenantCode", "ScopeType" ON "Users"
            BEGIN
                SELECT RAISE(ABORT, 'tenant scope normalization blocked');
            END;
            """);

        var normalizationFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokePrivateTaskAsync(
                "EnsureUserTenantScopeColumnsAsync",
                _dbContext,
                CancellationToken.None));

        Assert.Equal(
            "Required Users tenant/scope normalization failed.",
            normalizationFailure.Message);
        Assert.IsType<SqliteException>(normalizationFailure.InnerException);

        var verificationFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokePrivateTaskAsync(
                "VerifyNonAdminUserScopeDomainAsync",
                _dbContext,
                CancellationToken.None));
        Assert.Contains("row-domain verification failed", verificationFailure.Message);
        Assert.Empty(await _dbContext.UserPermissions.ToListAsync());
    }

    [Fact]
    public async Task UserTenantScopeNormalization_WhenOnlyTenantColumnExists_AddsMissingScopeColumn()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE "Users" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "OfficeCode" TEXT NOT NULL,
                    "TenantCode" TEXT NOT NULL);
                INSERT INTO "Users" ("Id", "OfficeCode", "TenantCode")
                VALUES ('00000000-0000-0000-0000-000000000001', 'USENET', 'INVALID');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new AppDbContext(
            options,
            new TestCurrentUserContext(),
            new RevisionClock());

        await InvokePrivateTaskAsync(
            "EnsureUserTenantScopeColumnsAsync",
            dbContext,
            CancellationToken.None);

        await using var verifyCommand = connection.CreateCommand();
        verifyCommand.CommandText =
            """
            SELECT "TenantCode", "ScopeType"
            FROM "Users"
            WHERE "Id" = '00000000-0000-0000-0000-000000000001';
            """;
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(TenantScopeCatalog.UsenetGroup, reader.GetString(0));
        Assert.Equal(TenantScopeCatalog.ScopeOfficeOnly, reader.GetString(1));
    }

    [Fact]
    public async Task DataSharingPolicyRouteIntegrity_PreservesCanonicalDenyAndInstallsUniqueIndex()
    {
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP INDEX IF EXISTS "IX_DataSharingPolicies_SourceTarget";
            CREATE UNIQUE INDEX "IX_DataSharingPolicies_SourceTarget"
                ON "DataSharingPolicies" ("SourceTenantCode", "SourceOfficeCode", "TargetTenantCode", "TargetOfficeCode")
                WHERE "IsDeleted" = 0;
            """);

        var staleAllow = CreateSharingPolicy(isActive: true, allowTargetWrite: true);
        staleAllow.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        staleAllow.SourceTenantCode = " usenet_group ";
        staleAllow.SourceOfficeCode = " usenet ";
        staleAllow.TargetTenantCode = "usenet_group";
        staleAllow.TargetOfficeCode = "yeonsu";
        _dbContext.DataSharingPolicies.Add(staleAllow);
        await _dbContext.SaveChangesAsync();

        var canonicalDeny = CreateSharingPolicy(isActive: false, allowTargetWrite: false);
        canonicalDeny.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        canonicalDeny.ShareCustomers = false;
        canonicalDeny.ShareItems = false;
        canonicalDeny.ShareInvoices = false;
        canonicalDeny.SharePayments = false;
        canonicalDeny.ShareContracts = false;
        canonicalDeny.ShareReports = false;
        canonicalDeny.ShareRentals = false;
        canonicalDeny.ShareDeliveries = false;
        _dbContext.DataSharingPolicies.Add(canonicalDeny);
        await _dbContext.SaveChangesAsync();
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE "DataSharingPolicies"
            SET "Revision" = 42,
                "CreatedAtUtc" = '2026-07-26T00:00:00.0000000Z',
                "UpdatedAtUtc" = '2026-07-26T00:00:00.0000000Z';
            """);
        _dbContext.ChangeTracker.Clear();

        await InvokePrivateTaskAsync(
            "EnsureDataSharingPoliciesTableAsync",
            _dbContext,
            CancellationToken.None);

        var remaining = await _dbContext.DataSharingPolicies
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(canonicalDeny.Id, remaining.Id);
        Assert.False(remaining.IsActive);
        Assert.False(remaining.AllowTargetWrite);
        Assert.False(remaining.ShareCustomers);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, remaining.SourceTenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, remaining.SourceOfficeCode);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, remaining.TargetTenantCode);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, remaining.TargetOfficeCode);
        Assert.True(await IsUniqueIndexAsync("IX_DataSharingPolicies_SourceTarget"));
        Assert.False(await IsPartialIndexAsync("IX_DataSharingPolicies_SourceTarget"));
        Assert.Equal(
            [
                "SourceTenantCode",
                "SourceOfficeCode",
                "TargetTenantCode",
                "TargetOfficeCode"
            ],
            await ReadIndexColumnsAsync("IX_DataSharingPolicies_SourceTarget"));

        _dbContext.DataSharingPolicies.Add(CreateSharingPolicy(isActive: true, allowTargetWrite: true));
        await Assert.ThrowsAsync<DbUpdateException>(() => _dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task SyncTenantConfiguration_SameRouteDifferentIdAndTargetOnlyAllow_ExactMirrorsThenIsIdempotent()
    {
        await using var sourceConnection = new SqliteConnection("Data Source=:memory:");
        await using var targetConnection = new SqliteConnection("Data Source=:memory:");
        await sourceConnection.OpenAsync();
        await targetConnection.OpenAsync();
        await using var sourceDbContext = CreateIsolatedDbContext(sourceConnection);
        await using var targetDbContext = CreateIsolatedDbContext(targetConnection);
        sourceDbContext.Database.EnsureCreated();
        targetDbContext.Database.EnsureCreated();
        await InvokePrivateTaskAsync(
            "EnsureDataSharingPoliciesTableAsync",
            sourceDbContext,
            CancellationToken.None);
        await InvokePrivateTaskAsync(
            "EnsureDataSharingPoliciesTableAsync",
            targetDbContext,
            CancellationToken.None);

        sourceDbContext.TenantDefinitions.Add(new TenantDefinition
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            DisplayName = "source tenant",
            StorageMode = TenantScopeCatalog.StorageSharedDatabase,
            Description = "source tenant configuration",
            IsActive = true
        });
        sourceDbContext.TenantOfficeDefinitions.Add(new TenantOfficeDefinition
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            DisplayName = "source office",
            IsHeadOffice = true,
            IsActive = true
        });
        var sourceDeny = CreateSharingPolicy(isActive: false, allowTargetWrite: false);
        sourceDeny.Id = Guid.Parse("10000000-0000-0000-0000-000000000001");
        sourceDeny.ShareCustomers = false;
        sourceDeny.ShareItems = false;
        sourceDeny.ShareInvoices = false;
        sourceDeny.SharePayments = false;
        sourceDeny.ShareContracts = false;
        sourceDeny.ShareReports = false;
        sourceDeny.ShareRentals = false;
        sourceDeny.ShareDeliveries = false;
        sourceDeny.Note = "canonical source deny";
        sourceDbContext.DataSharingPolicies.Add(sourceDeny);
        await sourceDbContext.SaveChangesAsync();
        sourceDbContext.ChangeTracker.Clear();

        targetDbContext.TenantDefinitions.Add(new TenantDefinition
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            DisplayName = "stale target tenant",
            StorageMode = TenantScopeCatalog.StorageSharedDatabase,
            Description = "stale target tenant configuration",
            IsActive = true
        });
        targetDbContext.TenantOfficeDefinitions.Add(new TenantOfficeDefinition
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            DisplayName = "stale target office",
            IsHeadOffice = false,
            IsActive = true
        });
        var sameRouteDifferentId = CreateSharingPolicy(isActive: true, allowTargetWrite: true);
        sameRouteDifferentId.Id = Guid.Parse("20000000-0000-0000-0000-000000000001");
        sameRouteDifferentId.Note = "stale same-route allow";
        var targetOnlyStaleAllow = CreateSharingPolicy(isActive: true, allowTargetWrite: true);
        targetOnlyStaleAllow.Id = Guid.Parse("20000000-0000-0000-0000-000000000002");
        targetOnlyStaleAllow.SourceOfficeCode = OfficeCodeCatalog.Yeonsu;
        targetOnlyStaleAllow.TargetOfficeCode = OfficeCodeCatalog.Usenet;
        targetOnlyStaleAllow.Note = "target-only stale allow";
        targetDbContext.DataSharingPolicies.AddRange(sameRouteDifferentId, targetOnlyStaleAllow);
        await targetDbContext.SaveChangesAsync();
        targetDbContext.ChangeTracker.Clear();

        await InvokePrivateTaskAsync(
            "SyncTenantConfigurationAsync",
            sourceDbContext,
            targetDbContext,
            CancellationToken.None);

        Assert.True(targetDbContext.ChangeTracker.HasChanges());
        await using (var beforeSaveObserver = CreateIsolatedDbContext(targetConnection))
        {
            Assert.Equal(
                "stale target tenant",
                await beforeSaveObserver.TenantDefinitions
                    .IgnoreQueryFilters()
                    .Select(definition => definition.DisplayName)
                    .SingleAsync());
            Assert.Equal(
                2,
                await beforeSaveObserver.DataSharingPolicies
                    .IgnoreQueryFilters()
                    .CountAsync());
        }

        await targetDbContext.SaveChangesAsync();
        targetDbContext.ChangeTracker.Clear();

        Assert.Equal(
            await ReadPolicyMirrorAsync(sourceDbContext),
            await ReadPolicyMirrorAsync(targetDbContext));
        var mirroredPolicy = await targetDbContext.DataSharingPolicies
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(sameRouteDifferentId.Id, mirroredPolicy.Id);
        Assert.False(mirroredPolicy.AllowTargetWrite);
        Assert.False(mirroredPolicy.IsActive);
        Assert.False(await targetDbContext.DataSharingPolicies
            .IgnoreQueryFilters()
            .AnyAsync(policy => policy.Id == targetOnlyStaleAllow.Id));
        Assert.Equal(
            "source tenant",
            await targetDbContext.TenantDefinitions
                .IgnoreQueryFilters()
                .Select(definition => definition.DisplayName)
                .SingleAsync());
        Assert.Equal(
            "source office",
            await targetDbContext.TenantOfficeDefinitions
                .IgnoreQueryFilters()
                .Select(definition => definition.DisplayName)
                .SingleAsync());

        var versionsBeforeSecondRun = await ReadConfigurationVersionsAsync(targetDbContext);
        var auditCountBeforeSecondRun = await targetDbContext.AuditLogs.CountAsync();

        await InvokePrivateTaskAsync(
            "SyncTenantConfigurationAsync",
            sourceDbContext,
            targetDbContext,
            CancellationToken.None);
        targetDbContext.ChangeTracker.DetectChanges();

        Assert.False(targetDbContext.ChangeTracker.HasChanges());
        Assert.Equal(0, await targetDbContext.SaveChangesAsync());
        targetDbContext.ChangeTracker.Clear();
        Assert.Equal(
            versionsBeforeSecondRun,
            await ReadConfigurationVersionsAsync(targetDbContext));
        Assert.Equal(auditCountBeforeSecondRun, await targetDbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task RentalBillingLogIntegrity_WhenRequiredIndexIsNonUnique_ReplacesAndVerifiesIt()
    {
        await ReplaceWithNonUniqueBillingLogIndexAsync();

        await InvokePrivateTaskAsync(
            "EnsureRentalBillingLogsTableAsync",
            _dbContext,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.True(await IsUniqueIndexAsync(
            "IX_RentalBillingLogs_BillingProfileId_BillingYearMonth"));
        Assert.Equal(
            ["BillingProfileId", "BillingYearMonth"],
            await ReadIndexColumnsAsync(
                "IX_RentalBillingLogs_BillingProfileId_BillingYearMonth"));
    }

    [Fact]
    public async Task RentalBillingLogIntegrity_WhenRequiredIndexIsPartial_ReplacesWithFullUniqueIndex()
    {
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP INDEX "IX_RentalBillingLogs_BillingProfileId_BillingYearMonth";
            CREATE UNIQUE INDEX "IX_RentalBillingLogs_BillingProfileId_BillingYearMonth"
                ON "RentalBillingLogs" ("BillingProfileId", "BillingYearMonth")
                WHERE "IsDeleted" = 0;
            """);

        await InvokePrivateTaskAsync(
            "EnsureRentalBillingLogsTableAsync",
            _dbContext,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.True(await IsUniqueIndexAsync(
            "IX_RentalBillingLogs_BillingProfileId_BillingYearMonth"));
        Assert.False(await IsPartialIndexAsync(
            "IX_RentalBillingLogs_BillingProfileId_BillingYearMonth"));
    }

    [Fact]
    public async Task RentalBillingLogIntegrity_WhenDuplicatesPreventUniqueIndex_FailsAndRollsBackReplacement()
    {
        await ReplaceWithNonUniqueBillingLogIndexAsync();
        var billingProfileId = Guid.NewGuid();
        _dbContext.RentalBillingLogs.AddRange(
            CreateBillingLog(billingProfileId),
            CreateBillingLog(billingProfileId));
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokePrivateTaskAsync(
                "EnsureRentalBillingLogsTableAsync",
                _dbContext,
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Contains(
            "Required unique index 'IX_RentalBillingLogs_BillingProfileId_BillingYearMonth'",
            failure.Message);
        Assert.False(await IsUniqueIndexAsync(
            "IX_RentalBillingLogs_BillingProfileId_BillingYearMonth"));
        Assert.Equal(
            2,
            await _dbContext.RentalBillingLogs
                .IgnoreQueryFilters()
                .CountAsync(log =>
                    log.BillingProfileId == billingProfileId &&
                    log.BillingYearMonth == "2026-07"));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private static DataSharingPolicy CreateSharingPolicy(
        bool isActive,
        bool allowTargetWrite)
        => new()
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            ShareItems = true,
            ShareInvoices = true,
            SharePayments = true,
            ShareContracts = true,
            ShareReports = true,
            ShareRentals = true,
            ShareDeliveries = true,
            AllowTargetWrite = allowTargetWrite,
            IsActive = isActive
        };

    private static RentalBillingLog CreateBillingLog(Guid billingProfileId)
        => new()
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BillingProfileId = billingProfileId,
            BillingYearMonth = "2026-07",
            ScheduledDate = new DateOnly(2026, 7, 1),
            Status = "예정"
        };

    private static AppDbContext CreateIsolatedDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        return new AppDbContext(
            options,
            new TestCurrentUserContext(),
            new RevisionClock());
    }

    private static async Task<PolicyMirror[]> ReadPolicyMirrorAsync(AppDbContext dbContext)
        => (await dbContext.DataSharingPolicies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync())
            .Select(policy => new PolicyMirror(
                policy.SourceTenantCode,
                policy.SourceOfficeCode,
                policy.TargetTenantCode,
                policy.TargetOfficeCode,
                policy.ShareCustomers,
                policy.ShareItems,
                policy.ShareInvoices,
                policy.SharePayments,
                policy.ShareContracts,
                policy.ShareReports,
                policy.ShareRentals,
                policy.ShareDeliveries,
                policy.AllowTargetWrite,
                policy.Note,
                policy.IsActive,
                policy.IsDeleted))
            .OrderBy(policy => policy.SourceTenantCode, StringComparer.Ordinal)
            .ThenBy(policy => policy.SourceOfficeCode, StringComparer.Ordinal)
            .ThenBy(policy => policy.TargetTenantCode, StringComparer.Ordinal)
            .ThenBy(policy => policy.TargetOfficeCode, StringComparer.Ordinal)
            .ToArray();

    private static async Task<(string Kind, Guid Id, long Revision, DateTime UpdatedAtUtc)[]>
        ReadConfigurationVersionsAsync(AppDbContext dbContext)
    {
        var versions = new List<(string Kind, Guid Id, long Revision, DateTime UpdatedAtUtc)>();
        versions.AddRange((await dbContext.TenantDefinitions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync())
            .Select(entity => ("tenant", entity.Id, entity.Revision, entity.UpdatedAtUtc)));
        versions.AddRange((await dbContext.TenantOfficeDefinitions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync())
            .Select(entity => ("office", entity.Id, entity.Revision, entity.UpdatedAtUtc)));
        versions.AddRange((await dbContext.DataSharingPolicies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync())
            .Select(entity => ("policy", entity.Id, entity.Revision, entity.UpdatedAtUtc)));
        return versions
            .OrderBy(version => version.Kind, StringComparer.Ordinal)
            .ThenBy(version => version.Id)
            .ToArray();
    }

    private Task ReplaceWithNonUniqueBillingLogIndexAsync()
        => _dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP INDEX "IX_RentalBillingLogs_BillingProfileId_BillingYearMonth";
            CREATE INDEX "IX_RentalBillingLogs_BillingProfileId_BillingYearMonth"
                ON "RentalBillingLogs" ("BillingProfileId", "BillingYearMonth");
            """);

    private async Task<bool> IsUniqueIndexAsync(string indexName)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT \"unique\" FROM pragma_index_list('DataSharingPolicies') WHERE \"name\" = $name " +
            "UNION ALL " +
            "SELECT \"unique\" FROM pragma_index_list('RentalBillingLogs') WHERE \"name\" = $name " +
            "LIMIT 1;";
        command.Parameters.AddWithValue("$name", indexName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) != 0;
    }

    private async Task<bool> IsPartialIndexAsync(string indexName)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT \"partial\" FROM pragma_index_list('DataSharingPolicies') WHERE \"name\" = $name " +
            "UNION ALL " +
            "SELECT \"partial\" FROM pragma_index_list('RentalBillingLogs') WHERE \"name\" = $name " +
            "LIMIT 1;";
        command.Parameters.AddWithValue("$name", indexName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) != 0;
    }

    private async Task<string[]> ReadIndexColumnsAsync(string indexName)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            $"SELECT \"name\" FROM pragma_index_info('{indexName}') ORDER BY \"seqno\";";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));

        return columns.ToArray();
    }

    private static async Task InvokePrivateTaskAsync(
        string methodName,
        params object?[] arguments)
    {
        var method = typeof(DbInitializer).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method.Invoke(null, arguments));
        await task;
    }

    private sealed record PolicyMirror(
        string SourceTenantCode,
        string SourceOfficeCode,
        string TargetTenantCode,
        string TargetOfficeCode,
        bool ShareCustomers,
        bool ShareItems,
        bool ShareInvoices,
        bool SharePayments,
        bool ShareContracts,
        bool ShareReports,
        bool ShareRentals,
        bool ShareDeliveries,
        bool AllowTargetWrite,
        string Note,
        bool IsActive,
        bool IsDeleted);

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public string Username => "admin";
        public string TenantCode => TenantScopeCatalog.UsenetGroup;
        public string OfficeCode => OfficeCodeCatalog.Usenet;
        public string ScopeType => TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin => true;
        public bool IsGodMode => false;
        public bool HasPermission(string permission) => true;
    }
}
