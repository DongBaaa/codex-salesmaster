using System.Security.Cryptography;
using System.Text.Json;
using GeoraePlan.Tools.SyncDiag;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class IsolatedSeedRetryRentalCompanyReconcilerTests
{
    [Theory]
    [InlineData("USENET", "USENET_GROUP", false)]
    [InlineData("YEONSU", "USENET_GROUP", false)]
    [InlineData("ITWORLD", "ITWORLD", false)]
    [InlineData("USENET", "USENET_GROUP", true)]
    [InlineData("YEONSU", "USENET_GROUP", true)]
    [InlineData("ITWORLD", "ITWORLD", true)]
    public async Task Retry_PreservesBusinessStateAndLocalId_AndRequiresAFreshAcknowledgement(string code, string tenant, bool aggregateError)
    {
        await using var fixture = await Fixture.Create(code, tenant);
        if (aggregateError)
        {
            // Push failure handling persists one summary on every failed mutation;
            // the first conflict's ID can belong to a different company.
            var failed = await fixture.Db.SyncOutboxEntries.SingleAsync(x => x.Id == fixture.FailedId);
            failed.ErrorMessage = $"동기화 충돌 80건: RentalManagementCompany {Guid.NewGuid()} - Expected revision mismatch. client=7, server=55";
            await fixture.Db.SaveChangesAsync();
        }
        await using var tx = await fixture.Db.Database.BeginTransactionAsync();
        var result = await IsolatedSeedRetryRentalCompanyReconciler.ReconcileAsync(fixture.Db, fixture.ServerPath);
        await tx.CommitAsync();
        Assert.Equal(1, result.RebasedCompanies);
        Assert.Equal(1, result.RemovedStaleOutbox);
        fixture.Db.ChangeTracker.Clear();
        var company = await fixture.Db.RentalManagementCompanies.SingleAsync();
        Assert.Equal(55, company.Revision);
        Assert.True(company.IsDirty);
        company.Revision = 7;
        Assert.Equal(fixture.BeforeCompany, JsonSerializer.Serialize(company));
        var remaining = await fixture.Db.SyncOutboxEntries.ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, x => x.Status == "Acknowledged" && x.EntityId == company.Id);
        Assert.Contains(remaining, x => x.ErrorMessage == "unrelated failure");
        Assert.Equal(fixture.ServerHash, Hash(fixture.ServerPath));
    }

    [Theory]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-database")]
    [InlineData("stale-local-revision")]
    [InlineData("not-dirty")]
    [InlineData("deleted-local")]
    [InlineData("deleted-server")]
    [InlineData("missing-server")]
    [InlineData("duplicate-server")]
    [InlineData("duplicate-outbox")]
    [InlineData("non-default")]
    public async Task Retry_RejectsAmbiguityWithoutChangingBusinessRowsOrReceipts(string invalid)
    {
        await using var fixture = await Fixture.Create("USENET", "USENET_GROUP");
        var company = await fixture.Db.RentalManagementCompanies.SingleAsync();
        var row = await fixture.Db.SyncOutboxEntries.SingleAsync(x => x.Id == fixture.FailedId);
        switch (invalid)
        {
            case "wrong-tenant": row.TenantCode = "ITWORLD"; break;
            case "wrong-database": row.BusinessDatabaseName = "ITWORLD"; break;
            case "stale-local-revision": company.Revision++; break;
            case "not-dirty": company.IsDirty = false; break;
            case "deleted-local": company.IsDeleted = true; break;
            case "non-default": company.IsSystemDefault = false; break;
            case "duplicate-outbox": fixture.Db.SyncOutboxEntries.Add(Fixture.Outbox(company.Id, "Failed", row.ErrorMessage, "USENET_GROUP", "USENET")); break;
            case "deleted-server": await fixture.ServerSql("UPDATE RentalManagementCompanies SET IsDeleted=1"); break;
            case "missing-server": await fixture.ServerSql("DELETE FROM RentalManagementCompanies"); break;
            case "duplicate-server": await fixture.ServerSql("INSERT INTO RentalManagementCompanies SELECT '77777777-7777-7777-7777-777777777777', TenantCode, Code, Revision, IsSystemDefault, IsDeleted FROM RentalManagementCompanies"); break;
        }
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var before = JsonSerializer.Serialize(await fixture.Db.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking().SingleAsync());
        var receipts = JsonSerializer.Serialize(await fixture.Db.SyncOutboxEntries.AsNoTracking().OrderBy(x => x.Id).ToListAsync());
        await using (var tx = await fixture.Db.Database.BeginTransactionAsync())
            await Assert.ThrowsAsync<InvalidOperationException>(() => IsolatedSeedRetryRentalCompanyReconciler.ReconcileAsync(fixture.Db, fixture.ServerPath));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(before, JsonSerializer.Serialize(await fixture.Db.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking().SingleAsync()));
        Assert.Equal(receipts, JsonSerializer.Serialize(await fixture.Db.SyncOutboxEntries.AsNoTracking().OrderBy(x => x.Id).ToListAsync()));
    }

    [Fact]
    public async Task Retry_RequiresTransaction()
    {
        await using var fixture = await Fixture.Create("USENET", "USENET_GROUP");
        await Assert.ThrowsAsync<InvalidOperationException>(() => IsolatedSeedRetryRentalCompanyReconciler.ReconcileAsync(fixture.Db, fixture.ServerPath));
    }

    [Theory]
    [InlineData("Failed", "동기화 충돌 1건: RentalManagementCompany - network failure")]
    [InlineData("Failed", "Unrecognized message: Expected revision mismatch.")]
    [InlineData("Prepared", "Expected revision mismatch. client=7, server=55")]
    public async Task Retry_LeavesOtherFailureKindsAndNonFailedReceiptsUntouched(string status, string error)
    {
        await using var fixture = await Fixture.Create("USENET", "USENET_GROUP");
        var row = await fixture.Db.SyncOutboxEntries.SingleAsync(x => x.Id == fixture.FailedId);
        row.Status = status;
        row.ErrorMessage = error;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var receipts = JsonSerializer.Serialize(await fixture.Db.SyncOutboxEntries.AsNoTracking().OrderBy(x => x.Id).ToListAsync());
        await using var tx = await fixture.Db.Database.BeginTransactionAsync();
        var result = await IsolatedSeedRetryRentalCompanyReconciler.ReconcileAsync(fixture.Db, fixture.ServerPath);
        await tx.CommitAsync();
        Assert.Equal(new IsolatedSeedRetryRentalCompanyResult(0, 0), result);
        Assert.Equal(fixture.BeforeCompany, JsonSerializer.Serialize(await fixture.Db.RentalManagementCompanies.AsNoTracking().SingleAsync()));
        Assert.Equal(receipts, JsonSerializer.Serialize(await fixture.Db.SyncOutboxEntries.AsNoTracking().OrderBy(x => x.Id).ToListAsync()));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class Fixture : IAsyncDisposable
    {
        public required LocalDbContext Db { get; init; }
        public required SqliteConnection LocalConnection { get; init; }
        public required string ServerPath { get; init; }
        public required string ServerHash { get; init; }
        public required string BeforeCompany { get; init; }
        public required Guid FailedId { get; init; }

        public static async Task<Fixture> Create(string code, string tenant)
        {
            var local = new SqliteConnection("Data Source=:memory:");
            await local.OpenAsync();
            var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(local).Options);
            await db.Database.EnsureCreatedAsync();
            var company = new LocalRentalManagementCompany
            {
                Code = code, Name = "복원할 저장 업체명", IsSystemDefault = true,
                IsActive = false, IsDeleted = false, IsDirty = true, Revision = 7,
                CreatedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc)
            };
            var business = code == "ITWORLD" ? "ITWORLD" : "USENET";
            var failed = Outbox(company.Id, "Failed", "Expected revision mismatch. client=7, server=55", tenant, business);
            db.RentalManagementCompanies.Add(company);
            db.SyncOutboxEntries.AddRange(failed, Outbox(company.Id, "Acknowledged", "", tenant, business),
                Outbox(Guid.NewGuid(), "Failed", "unrelated failure", tenant, business));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var before = JsonSerializer.Serialize(await db.RentalManagementCompanies.AsNoTracking().SingleAsync());
            var serverPath = Path.Combine(Path.GetTempPath(), "company-seed-retry-" + Guid.NewGuid().ToString("N") + ".db");
            await using (var server = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = serverPath, Pooling = false }.ToString()))
            {
                await server.OpenAsync();
                await using var command = server.CreateCommand();
                command.CommandText = """
                    CREATE TABLE RentalManagementCompanies(Id TEXT PRIMARY KEY, TenantCode TEXT, Code TEXT, Revision INTEGER, IsSystemDefault INTEGER, IsDeleted INTEGER);
                    INSERT INTO RentalManagementCompanies VALUES ($id,$tenant,$code,55,1,0);
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$tenant", tenant);
                command.Parameters.AddWithValue("$code", code);
                await command.ExecuteNonQueryAsync();
            }
            return new() { Db = db, LocalConnection = local, ServerPath = serverPath, ServerHash = Hash(serverPath), BeforeCompany = before, FailedId = failed.Id };
        }

        public static LocalSyncOutboxEntry Outbox(Guid id, string status, string error, string tenant, string business) => new()
        {
            EntityId = id, EntityName = nameof(LocalRentalManagementCompany), MutationId = Guid.NewGuid().ToString("N"),
            DeviceId = "isolated-test", SessionId = Guid.NewGuid(), UserId = Guid.NewGuid(), ExpectedRevision = 7,
            TenantCode = tenant, BusinessDatabaseName = business, OfficeCode = "SHARED", ResponsibleOfficeCode = "USENET",
            Status = status, ErrorMessage = error
        };

        public async Task ServerSql(string sql)
        {
            await using var server = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ServerPath, Pooling = false }.ToString());
            await server.OpenAsync();
            await using var command = server.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); await LocalConnection.DisposeAsync(); File.Delete(ServerPath);
        }
    }
}
