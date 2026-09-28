using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PostgreSqlInvoiceAuthorSchemaTests
{
    [PostgreSqlFact]
    public async Task ExistingInvoiceAuthorSchemaUpgradeIsIdempotentAndDoesNotInventAuthors()
    {
        var configured = Environment.GetEnvironmentVariable(PostgreSqlSyncPushMutationIdempotencyTests.ConnectionVariableName);
        var admin = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false };
        var database = "gp_author_" + Guid.NewGuid().ToString("N");
        var target = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = database };
        await using var maintenance = new NpgsqlConnection(admin.ConnectionString);
        await maintenance.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", maintenance)) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(target.ConnectionString).Options, new User(), new RevisionClock());
            await db.Database.EnsureCreatedAsync();
            var customer = new Customer { Id = Guid.NewGuid(), NameOriginal = "author migration", NameMatchKey = "AUTHORMIGRATION" };
            var invoice = new Invoice { Id = Guid.NewGuid(), CustomerId = customer.Id, Memo = "preserved invoice", TotalAmount = 123m };
            invoice.VersionGroupId = invoice.Id;
            db.Customers.Add(customer); db.Invoices.Add(invoice); await db.SaveChangesAsync();
            var revision = invoice.Revision; db.ChangeTracker.Clear();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Invoices\" DROP COLUMN \"CreatedByUsername\", DROP COLUMN \"LastSavedByUsername\", DROP COLUMN \"LastSavedAtUtc\";");
            // Run the real schema entry point, not a standalone DDL imitation.
            var upgrade = typeof(DbInitializer).GetMethod("EnsureBusinessDatabaseSchemaAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            for (var i = 0; i < 2; i++)
                await (Task)upgrade.Invoke(null, [db, NullLogger.Instance, CancellationToken.None])!;
            var migrated = await db.Invoices.SingleAsync(x => x.Id == invoice.Id);
            Assert.Equal(revision, migrated.Revision); Assert.Equal(123m, migrated.TotalAmount); Assert.Equal("preserved invoice", migrated.Memo);
            Assert.Equal("", migrated.CreatedByUsername); Assert.Equal("", migrated.LastSavedByUsername); Assert.Null(migrated.LastSavedAtUtc);
            migrated.LastSavedByUsername = "verified-editor"; migrated.LastSavedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            Assert.Equal("verified-editor", (await db.Invoices.SingleAsync(x => x.Id == invoice.Id)).LastSavedByUsername);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", maintenance);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class User : ICurrentUserContext
    {
        public Guid? UserId => null;
        public string Username => "schema-test";
        public string TenantCode => TenantScopeCatalog.UsenetGroup;
        public string OfficeCode => OfficeCodeCatalog.Usenet;
        public string ScopeType => TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin => true;
        public bool IsGodMode => false;
        public bool HasPermission(string permission) => true;
    }
}
