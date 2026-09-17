using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class PostgreSqlSyncPushMutationIdempotencyTests
{
    [PostgreSqlFact]
    public async Task ConflictActorLookup_ReadsBoundedLatestRowsAcrossBatches_PostgreSql()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configured));
        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenance = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", IncludeErrorDetail = false };
        var isolated = new NpgsqlConnectionStringBuilder(maintenance.ConnectionString) { Database = databaseName };
        var created = false;
        try
        {
            await CreateDatabaseAsync(maintenance.ConnectionString, databaseName);
            created = true;
            var capture = new AuditActorPlanCaptureInterceptor();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(isolated.ConnectionString).AddInterceptors(capture).Options;
            var user = CreateAdminUser();
            await using var db = CreateDbContext(options, user);
            await db.Database.EnsureCreatedAsync();
            var cutoff = DateTime.UtcNow;
            const int rowCount = 200_000;
            const int fingerprintCount = 103;
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "AuditLogs" ("Id","UserId","Username","EntityName","EntityId","Action","BeforeJson","AfterJson","CreatedAtUtc")
                SELECT md5('audit-plan-' || g)::uuid, '11111111-1111-1111-1111-111111111111'::uuid,
                       'actor-' || g, 'Customer', 'audit-key-' || (g % 103), 'Modified', '', '',
                       {0}::timestamptz - (200001-g) * interval '1 second'
                FROM generate_series(1,200000) g;
                ANALYZE "AuditLogs";
                """, cutoff);
            var conflicts = Enumerable.Range(0, fingerprintCount).Select(i => new ConflictLogDto
            {
                EntityName = "Customer", EntityId = $"audit-key-{i}", ServerJson = "{}"
            }).ToList();
            capture.Queries.Clear();
            var controller = CreateController(db, user);
            var method = typeof(SyncController).GetMethod("PopulateServerConflictActorsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(controller, [conflicts, cutoff, CancellationToken.None])!;
            Assert.Equal(2, capture.Queries.Count);
            for (var i = 0; i < fingerprintCount; i++)
            {
                var latest = rowCount - ((rowCount - i) % fingerprintCount);
                Assert.Equal($"actor-{latest}", conflicts[i].ServerUsername);
                Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), conflicts[i].ServerUserId);
            }
            await using var connection = new NpgsqlConnection(isolated.ConnectionString);
            await connection.OpenAsync();
            var auditedRowsRead = 0d;
            foreach (var query in capture.Queries)
            {
                await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, FORMAT JSON) " + query.Sql, connection);
                foreach (var parameter in query.Parameters)
                    explain.Parameters.Add((NpgsqlParameter)((ICloneable)parameter).Clone());
                var json = Assert.IsType<string>(await explain.ExecuteScalarAsync());
                using var plan = JsonDocument.Parse(json);
                Visit(plan.RootElement[0].GetProperty("Plan"));
                void Visit(JsonElement node)
                {
                    if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "AuditLogs")
                    {
                        Assert.NotEqual("Seq Scan", node.GetProperty("Node Type").GetString());
                        auditedRowsRead += node.GetProperty("Actual Rows").GetDouble() * node.GetProperty("Actual Loops").GetDouble();
                    }
                    if (node.TryGetProperty("Plans", out var children))
                        foreach (var child in children.EnumerateArray()) Visit(child);
                }
            }
            // Prove bounded reads on actual PostgreSQL plans, not wall-clock speed.
            Assert.InRange(auditedRowsRead, fingerprintCount, fingerprintCount * 2);
            await using var count = new NpgsqlCommand("SELECT count(*) FROM \"AuditLogs\"", connection);
            Assert.Equal((long)rowCount, await count.ExecuteScalarAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (created) await DropDatabaseAsync(maintenance.ConnectionString, databaseName);
        }
    }

    private sealed class AuditActorPlanCaptureInterceptor : DbCommandInterceptor
    {
        public List<(string Sql, NpgsqlParameter[] Parameters)> Queries { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("AuditLogs", StringComparison.Ordinal))
                Queries.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>()
                    .Select(parameter => (NpgsqlParameter)((ICloneable)parameter).Clone()).ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
