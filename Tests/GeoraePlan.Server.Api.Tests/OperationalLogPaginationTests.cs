using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class OperationalLogPaginationTests
{
    [PostgreSqlFact]
    public async Task PostgreSql_AuditCursorAndTargetReads_ExecuteAcrossPageBoundary()
    {
        await using var fixture = await Fixture.CreatePostgreSqlAsync();
        var logs = await fixture.SeedAuditAsync(799, 201, equalTime: true);
        fixture.Counter.BeforeSecondPage = () =>
        {
            using var writer = fixture.CreateWriter();
            writer.AuditLogs.Add(fixture.NewAudit(fixture.Hidden.Id, Fixture.Anchor.AddSeconds(1)));
            writer.SaveChanges();
        };
        var rows = await fixture.ReadAuditAsync(200);
        var expected = logs.Where(x => x.EntityId == fixture.Visible.Id.ToString("D"))
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Take(200);
        Assert.Equal(expected.Select(x => x.Id), rows.Select(x => x.Id));
        Assert.Equal(2, fixture.Counter.Pages);
        Assert.InRange(fixture.Counter.CustomerReads, 2, 4);

        var conflicts = Enumerable.Range(0, 516).Select(i => new ConflictLog
        {
            EntityName = nameof(Customer), EntityId = (i < 499 ? fixture.Hidden.Id : fixture.Visible.Id).ToString("D"),
            CreatedAtUtc = Fixture.Anchor.AddSeconds(-i), Status = "Open"
        }).ToList();
        fixture.Db.ConflictLogs.AddRange(conflicts);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        fixture.Counter.Reset();
        fixture.Counter.BeforeSecondPage = () =>
        {
            using var writer = fixture.CreateWriter();
            writer.ConflictLogs.Where(x => x.Id == conflicts[499].Id)
                .ExecuteUpdate(set => set.SetProperty(x => x.Status, "Resolved"));
        };
        var conflictRows = await fixture.Service.TakeVisibleConflictLogsAsync(fixture.Db.ConflictLogs.AsNoTracking(), 200, CancellationToken.None);
        Assert.Equal(conflicts.Skip(499).Select(x => x.Id).Order(), conflictRows.Select(x => x.Id).Order());
        Assert.Equal(17, conflictRows.Count);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("insert")]
    [InlineData("delete")]
    [InlineData("equal-time")]
    public async Task Audit_ReturnsUniqueAuthorizedRows_WhenEarlierRowsChange(string mutation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var logs = await fixture.SeedAuditAsync(799, 201, mutation == "equal-time");
        fixture.Counter.BeforeSecondPage = () =>
        {
            using var writer = fixture.CreateWriter();
            if (mutation == "insert")
            {
                writer.AuditLogs.Add(fixture.NewAudit(fixture.Hidden.Id, Fixture.Anchor.AddSeconds(1)));
                writer.SaveChanges();
            }
            else if (mutation == "delete")
                writer.AuditLogs.Where(x => x.Id == logs[0].Id).ExecuteDelete();
        };

        var rows = await fixture.ReadAuditAsync(200);
        var expected = logs.Where(x => x.EntityId == fixture.Visible.Id.ToString("D"))
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Take(200);
        Assert.Equal(expected.Select(x => x.Id), rows.Select(x => x.Id));
        Assert.Equal(200, rows.Select(x => x.Id).Distinct().Count());
        Assert.Equal(2, fixture.Counter.Pages);
        Assert.InRange(fixture.Counter.CustomerReads, 2, 4);
        Assert.DoesNotContain(fixture.Counter.PageSql, sql => sql.Contains("OFFSET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Audit_RechecksTargetVisibility_OnNextPageAndNextRequest()
    {
        await using var fixture = await Fixture.CreateAsync();
        var logs = await fixture.SeedAuditAsync(799, 201);
        fixture.Counter.BeforeSecondPage = () =>
        {
            using var writer = fixture.CreateWriter();
            writer.Customers.IgnoreQueryFilters().Where(x => x.Id == fixture.Visible.Id)
                .ExecuteUpdate(set => set.SetProperty(x => x.TenantCode, TenantScopeCatalog.Itworld));
        };

        var first = await fixture.ReadAuditAsync(200);
        Assert.Equal(logs[799].Id, Assert.Single(first).Id);
        fixture.Counter.Reset();
        Assert.Empty(await fixture.ReadAuditAsync(200));
        Assert.Equal(2, fixture.Counter.Pages);
        Assert.Equal(3, fixture.Counter.CustomerReads);
    }

    [Theory]
    [InlineData(OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu)]
    [InlineData(OfficeCodeCatalog.Yeonsu, OfficeCodeCatalog.Usenet)]
    public async Task Audit_OfficeOnly_DoesNotReuseOtherOfficeVisibility(string office, string otherOffice)
    {
        await using var fixture = await Fixture.CreateAsync(new CurrentUser
        {
            OfficeCode = office,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        });
        fixture.Visible.ResponsibleOfficeCode = office;
        fixture.Hidden.TenantCode = TenantScopeCatalog.UsenetGroup;
        fixture.Hidden.ResponsibleOfficeCode = otherOffice;
        fixture.Db.Customers.UpdateRange(fixture.Visible, fixture.Hidden);
        await fixture.Db.SaveChangesAsync();
        await fixture.SeedAuditAsync(1000, 200);

        var rows = await fixture.ReadAuditAsync(200);
        Assert.Equal(200, rows.Count);
        Assert.All(rows, x => Assert.Equal(fixture.Visible.Id.ToString("D"), x.EntityId));
        Assert.Equal(3, fixture.Counter.CustomerReads);
    }

    [Fact]
    public async Task Audit_CacheKeyIncludesEntityName_AndRejectsMalformedTargets()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Db.AuditLogs.ExecuteDeleteAsync();
        var hidden = fixture.NewAudit(fixture.Hidden.Id, Fixture.Anchor);
        var shared = fixture.NewAudit(fixture.Hidden.Id, Fixture.Anchor.AddSeconds(-1));
        shared.EntityName = nameof(Unit);
        var malformed = fixture.NewAudit(fixture.Visible.Id, Fixture.Anchor.AddSeconds(-2));
        malformed.EntityName = nameof(ItemWarehouseStock);
        malformed.EntityId = "not-a-composite-key";
        fixture.Db.AuditLogs.AddRange(hidden, shared, malformed);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        fixture.Counter.Reset();

        Assert.Equal(shared.Id, Assert.Single(await fixture.ReadAuditAsync(200)).Id);
    }

    [Fact]
    public async Task Conflict_ResolvedRowMovingToLaterGroup_IsReturnedOnlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var logs = Enumerable.Range(0, 516).Select(i => new ConflictLog
        {
            EntityName = nameof(Customer),
            EntityId = (i < 499 ? fixture.Hidden.Id : fixture.Visible.Id).ToString("D"),
            CreatedAtUtc = Fixture.Anchor.AddSeconds(-i),
            Status = "Open"
        }).ToList();
        fixture.Db.ConflictLogs.AddRange(logs);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        fixture.Counter.Reset();
        fixture.Counter.BeforeSecondPage = () =>
        {
            using var writer = fixture.CreateWriter();
            writer.ConflictLogs.Where(x => x.Id == logs[499].Id)
                .ExecuteUpdate(set => set.SetProperty(x => x.Status, "Resolved"));
        };

        var rows = await fixture.Service.TakeVisibleConflictLogsAsync(
            fixture.Db.ConflictLogs.AsNoTracking(), 200, CancellationToken.None);
        Assert.Equal(17, rows.Count);
        Assert.Equal(logs.Skip(499).Select(x => x.Id).Order(), rows.Select(x => x.Id).Order());
        Assert.Equal(3, fixture.Counter.CustomerReads);
    }

    [Fact]
    public async Task Conflict_PreservesUnresolvedPriority_AndCallerFilter()
    {
        await using var fixture = await Fixture.CreateAsync();
        var open = new ConflictLog { EntityName = nameof(Customer), EntityId = fixture.Visible.Id.ToString("D"), Status = "Open", CreatedAtUtc = Fixture.Anchor };
        var resolved = new ConflictLog { EntityName = nameof(Customer), EntityId = open.EntityId, Status = "Resolved", CreatedAtUtc = Fixture.Anchor.AddDays(1) };
        fixture.Db.ConflictLogs.AddRange(open, resolved);
        await fixture.Db.SaveChangesAsync();
        var all = await fixture.Service.TakeVisibleConflictLogsAsync(fixture.Db.ConflictLogs.AsNoTracking(), 200, CancellationToken.None);
        Assert.Equal(new[] { open.Id, resolved.Id }, all.Select(x => x.Id));
        var filtered = await fixture.Service.TakeVisibleConflictLogsAsync(fixture.Db.ConflictLogs.AsNoTracking().Where(x => x.Status != "Resolved"), 200, CancellationToken.None);
        Assert.Equal(open.Id, Assert.Single(filtered).Id);
    }

    [Fact]
    public async Task Audit_EnforcesTakeAndCancellation_WithoutTargetReadsForGlobalScope()
    {
        await using var fixture = await Fixture.CreateAsync(new CurrentUser { ScopeType = TenantScopeCatalog.ScopeAdmin });
        await fixture.SeedAuditAsync(1, 1001);
        Assert.Empty(await fixture.ReadAuditAsync(0));
        Assert.Empty(await fixture.ReadAuditAsync(-1));
        Assert.Equal(0, fixture.Counter.Pages);
        Assert.Equal(1000, (await fixture.ReadAuditAsync(2000)).Count);
        Assert.Equal(1, fixture.Counter.Pages);
        Assert.Equal(0, fixture.Counter.CustomerReads);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.TakeVisibleAuditLogsAsync(
            fixture.Db.AuditLogs.AsNoTracking(), 200, cancellation.Token));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly DateTime Anchor = new(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly CurrentUser _user;
        private string? _postgresConnectionString;
        private string? _maintenanceConnectionString;
        private string? _databaseName;
        public AppDbContext Db { get; private set; } = null!;
        public Counter Counter { get; } = new();
        public OperationalLogScopeService Service { get; private set; } = null!;
        public Customer Visible { get; } = new() { TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Shared, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, NameOriginal = "visible", NameMatchKey = "visible" };
        public Customer Hidden { get; } = new() { TenantCode = TenantScopeCatalog.Itworld, OfficeCode = OfficeCodeCatalog.Shared, ResponsibleOfficeCode = OfficeCodeCatalog.Itworld, NameOriginal = "hidden", NameMatchKey = "hidden" };

        private Fixture(CurrentUser user) => _user = user;

        public static async Task<Fixture> CreateAsync(CurrentUser? user = null)
        {
            var fixture = new Fixture(user ?? new CurrentUser());
            await fixture._connection.OpenAsync();
            fixture.Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(fixture._connection).AddInterceptors(fixture.Counter).Options, fixture._user, new RevisionClock());
            await fixture.Db.Database.EnsureCreatedAsync();
            fixture.Db.Customers.AddRange(fixture.Visible, fixture.Hidden);
            await fixture.Db.SaveChangesAsync();
            fixture.Service = new OperationalLogScopeService(fixture.Db, new OfficeScopeService(fixture._user, fixture.Db));
            return fixture;
        }

        public static async Task<Fixture> CreatePostgreSqlAsync()
        {
            var configured = Environment.GetEnvironmentVariable(PostgreSqlSyncPushMutationIdempotencyTests.ConnectionVariableName);
            Assert.False(string.IsNullOrWhiteSpace(configured));
            var fixture = new Fixture(new CurrentUser());
            var maintenance = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false };
            fixture._maintenanceConnectionString = maintenance.ConnectionString;
            var name = $"gp_audit_{Guid.NewGuid():N}";
            await using (var connection = new NpgsqlConnection(maintenance.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
                await command.ExecuteNonQueryAsync();
                fixture._databaseName = name;
            }
            try
            {
                fixture._postgresConnectionString = new NpgsqlConnectionStringBuilder(maintenance.ConnectionString) { Database = name }.ConnectionString;
                fixture.Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture._postgresConnectionString).AddInterceptors(fixture.Counter).Options, fixture._user, new RevisionClock());
                await fixture.Db.Database.EnsureCreatedAsync();
                fixture.Db.Customers.AddRange(fixture.Visible, fixture.Hidden);
                await fixture.Db.SaveChangesAsync();
                fixture.Service = new OperationalLogScopeService(fixture.Db, new OfficeScopeService(fixture._user, fixture.Db));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public AppDbContext CreateWriter()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>();
            if (_postgresConnectionString is null) options.UseSqlite(_connection);
            else options.UseNpgsql(_postgresConnectionString);
            return new AppDbContext(options.Options, _user, new RevisionClock());
        }
        public AuditLog NewAudit(Guid target, DateTime date) => new() { EntityName = nameof(Customer), EntityId = target.ToString("D"), CreatedAtUtc = date };
        public async Task<List<AuditLog>> SeedAuditAsync(int hidden, int visible, bool equalTime = false)
        {
            await Db.AuditLogs.ExecuteDeleteAsync();
            var logs = Enumerable.Range(0, hidden + visible).Select(i => NewAudit(i < hidden ? Hidden.Id : Visible.Id, equalTime ? Anchor : Anchor.AddSeconds(-i))).ToList();
            Db.AuditLogs.AddRange(logs);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            Counter.Reset();
            return logs;
        }
        public Task<List<AuditLog>> ReadAuditAsync(int take) => Service.TakeVisibleAuditLogsAsync(Db.AuditLogs.AsNoTracking(), take, CancellationToken.None);
        public async ValueTask DisposeAsync()
        {
            if (Db is not null) await Db.DisposeAsync();
            await _connection.DisposeAsync();
            if (_databaseName is not null)
            {
                await using var connection = new NpgsqlConnection(_maintenanceConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\"", connection);
                await command.ExecuteNonQueryAsync();
                _databaseName = null;
            }
        }
    }

    private sealed class Counter : DbCommandInterceptor
    {
        public int Pages { get; private set; }
        public int CustomerReads { get; private set; }
        public Action? BeforeSecondPage { get; set; }
        public List<string> PageSql { get; } = [];
        public void Reset() { Pages = CustomerReads = 0; BeforeSecondPage = null; PageSql.Clear(); }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) { Count(command); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Count(command); return ValueTask.FromResult(result); }
        private void Count(DbCommand command)
        {
            if (command.CommandText.Contains("FROM \"AuditLogs\"") || command.CommandText.Contains("FROM \"ConflictLogs\""))
            {
                Pages++;
                PageSql.Add(command.CommandText);
                if (Pages == 2) BeforeSecondPage?.Invoke();
            }
            if (command.CommandText.Contains("FROM \"Customers\"")) CustomerReads++;
        }
    }

    private sealed class CurrentUser : ICurrentUserContext
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Username => "pagination-test-admin";
        public string TenantCode => TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeTenantAll;
        public bool IsAdmin => true;
        public bool IsGodMode => false;
        public bool HasPermission(string permission) => true;
    }
}
