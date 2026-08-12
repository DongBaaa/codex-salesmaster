using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PostgreSqlStoredFileReferenceReconcilerTests
{
    [PostgreSqlFact]
    public async Task DeleteUnreferencedAsync_UsesAllPhysicalDatabasesAndHostPathCaseSemantics()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(
            PostgreSqlSyncPushMutationIdempotencyTests.ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseSuffix = Guid.NewGuid().ToString("N");
        var centralDatabaseName = $"gpv1_case_{databaseSuffix}";
        var dedicatedDatabaseName = centralDatabaseName.ToUpperInvariant();
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var centralBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = centralDatabaseName,
            IncludeErrorDetail = false
        };
        var dedicatedBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = dedicatedDatabaseName,
            IncludeErrorDetail = false
        };
        var centralCreated = false;
        var dedicatedCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, centralDatabaseName);
            centralCreated = true;
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, dedicatedDatabaseName);
            dedicatedCreated = true;

            var currentUser = new TestCurrentUserContext();
            var revisionClock = new RevisionClock();
            var centralOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(centralBuilder.ConnectionString)
                .Options;
            var dedicatedOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(dedicatedBuilder.ConnectionString)
                .Options;

            Assert.NotEqual(
                PhysicalDatabaseIdentity.FromConnectionInfo(
                    CreateConnectionInfo(
                        centralBuilder.ConnectionString,
                        TenantScopeCatalog.UsenetGroup)),
                PhysicalDatabaseIdentity.FromConnectionInfo(
                    CreateConnectionInfo(
                        dedicatedBuilder.ConnectionString,
                        TenantScopeCatalog.Itworld)));

            await using (var centralDb = new AppDbContext(centralOptions, currentUser, revisionClock))
            {
                await centralDb.Database.EnsureCreatedAsync();
                await using var namespaceDedicatedDb =
                    new AppDbContext(dedicatedOptions, currentUser, revisionClock);
                Assert.NotEqual(
                    PhysicalDatabaseIdentity.GetStorageNamespace(centralDb),
                    PhysicalDatabaseIdentity.GetStorageNamespace(namespaceDedicatedDb));
            }

            var root = OperatingSystem.IsWindows()
                ? @"D:\GeoraePlan\FileStore"
                : "/var/lib/georaeplan/files";
            var exactReference = Path.Combine(root, "contracts", "exact-reference.pdf");
            var storedCaseReference = Path.Combine(root, "contracts", "Case-Reference.pdf");
            var differentlyCasedCandidate = storedCaseReference.ToUpperInvariant();
            var orphanPath = Path.Combine(root, "contracts", "orphan.pdf");

            await using (var dedicatedDb = new AppDbContext(dedicatedOptions, currentUser, revisionClock))
            {
                await dedicatedDb.Database.EnsureCreatedAsync();
                var customer = new Customer
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = "PostgreSQL stored file reference customer",
                    NameMatchKey = "POSTGRESQLSTOREDFILEREFERENCECUSTOMER",
                    TradeType = CustomerClassificationNormalizer.Sales
                };
                dedicatedDb.AddRange(
                    customer,
                    new CustomerContract
                    {
                        Id = Guid.NewGuid(),
                        CustomerId = customer.Id,
                        StoragePath = exactReference
                    },
                    new CustomerContract
                    {
                        Id = Guid.NewGuid(),
                        CustomerId = customer.Id,
                        StoragePath = storedCaseReference
                    });
                await dedicatedDb.SaveChangesAsync();
            }

            var centralInfo = CreateConnectionInfo(
                centralBuilder.ConnectionString,
                TenantScopeCatalog.UsenetGroup,
                isControlPlane: true);
            var dedicatedInfo = CreateConnectionInfo(
                dedicatedBuilder.ConnectionString,
                TenantScopeCatalog.Itworld,
                isDedicated: true);
            var storage = new RecordingCentralFileStorage(root);
            var reconciler = new StoredFileReferenceReconciler(
                new TestServiceScopeFactory(currentUser),
                storage,
                new TestTenantDatabaseConnectionResolver(centralInfo, dedicatedInfo),
                revisionClock);

            await reconciler.DeleteUnreferencedAsync(
                [exactReference, differentlyCasedCandidate, orphanPath],
                CancellationToken.None);

            if (OperatingSystem.IsWindows())
                Assert.Equal([orphanPath], storage.DeletedPaths);
            else
                Assert.Equal([differentlyCasedCandidate, orphanPath], storage.DeletedPaths);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (dedicatedCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, dedicatedDatabaseName);
            if (centralCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, centralDatabaseName);
        }
    }

    private static TenantDatabaseConnectionInfo CreateConnectionInfo(
        string connectionString,
        string tenantCode,
        bool isControlPlane = false,
        bool isDedicated = false)
        => new()
        {
            UseSqlite = false,
            ConnectionString = connectionString,
            TenantCode = tenantCode,
            IsControlPlane = isControlPlane,
            IsDedicatedBusinessDatabase = isDedicated
        };

    private static async Task CreateDatabaseAsync(string maintenanceConnection, string databaseName)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string maintenanceConnection, string databaseName)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestTenantDatabaseConnectionResolver(
        TenantDatabaseConnectionInfo central,
        TenantDatabaseConnectionInfo dedicated) : ITenantDatabaseConnectionResolver
    {
        public TenantDatabaseConnectionInfo ResolveCurrent() => central;
        public TenantDatabaseConnectionInfo ResolveCentral() => central;
        public TenantDatabaseConnectionInfo ResolveBusinessTenant(string? tenantCode) => dedicated;
        public IReadOnlyList<TenantDatabaseConnectionInfo> GetDedicatedBusinessConnections() => [dedicated];
    }

    private sealed class TestServiceScopeFactory(ICurrentUserContext currentUser) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new TestServiceScope(currentUser);
    }

    private sealed class TestServiceScope(ICurrentUserContext currentUser) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } = new TestServiceProvider(currentUser);
        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestServiceProvider(ICurrentUserContext currentUser) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(ICurrentUserContext) ? currentUser : null;
    }

    private sealed class RecordingCentralFileStorage(string rootPath) : ICentralFileStorage
    {
        public string RootPath { get; } = rootPath;
        public List<string> DeletedPaths { get; } = [];

        public Task<string> SaveBytesAsync(
            string area,
            string ownerId,
            Guid fileId,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, area, ownerId, $"{fileId:N}__{fileName}"));

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null) => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
            if (!string.IsNullOrWhiteSpace(storedPath))
                DeletedPaths.Add(storedPath);
        }
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Username => "postgres-file-reconciler";
        public string TenantCode => TenantScopeCatalog.UsenetGroup;
        public string OfficeCode => OfficeCodeCatalog.Usenet;
        public string ScopeType => TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin => true;
        public bool IsGodMode => false;
        public IReadOnlyCollection<string> Permissions => [];
        public bool HasPermission(string permission) => true;
    }
}
