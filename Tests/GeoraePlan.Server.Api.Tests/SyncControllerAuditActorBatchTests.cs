using System.Reflection;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    [Fact]
    public async Task ConflictActors_ExactKeysCutoffAndTiesRemainCorrectAcrossBatches()
    {
        var user = new TestCurrentUserContext
        {
            UserId = Guid.NewGuid(), Username = "actor-batch-admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin, IsAdmin = true
        };
        var counter = new AuditActorQueryCountingInterceptor();
        await using var db = CreateDbContext(user, counter);
        var controller = CreateController(db, user);
        var cutoff = DateTime.UtcNow;
        var conflicts = new List<ConflictLogDto>();
        var expected = new List<(Guid UserId, string Username)>();
        for (var i = 0; i < 103; i++)
        {
            var entityName = i % 2 == 0 ? nameof(Customer) : nameof(Item);
            var entityId = Guid.NewGuid().ToString("D");
            var actorId = Guid.NewGuid();
            var username = $"expected-{i}";
            expected.Add((actorId, username));
            conflicts.Add(new ConflictLogDto
            {
                EntityName = " " + entityName + " ", EntityId = " " + entityId + " ", ServerJson = "{}"
            });
            db.AuditLogs.AddRange(
                new AuditLog
                {
                    Id = Guid.Parse($"00000000-0000-0000-0000-{i * 10 + 1:D12}"),
                    EntityName = entityName, EntityId = entityId,
                    UserId = Guid.NewGuid(), Username = "older-tie", Action = "Modified",
                    CreatedAtUtc = cutoff.AddMinutes(-1)
                },
                new AuditLog
                {
                    Id = Guid.Parse($"00000000-0000-0000-0000-{i * 10 + 2:D12}"),
                    EntityName = entityName, EntityId = entityId,
                    UserId = actorId, Username = username, Action = "Modified",
                    CreatedAtUtc = cutoff.AddMinutes(-1)
                },
                new AuditLog
                {
                    Id = Guid.NewGuid(), EntityName = entityName, EntityId = entityId,
                    UserId = Guid.NewGuid(), Username = "at-cutoff-excluded", Action = "Modified",
                    CreatedAtUtc = cutoff
                },
                new AuditLog
                {
                    Id = Guid.NewGuid(), EntityName = i % 2 == 0 ? nameof(Item) : nameof(Customer),
                    EntityId = entityId, UserId = Guid.NewGuid(), Username = "different-entity-kind",
                    Action = "Modified", CreatedAtUtc = cutoff.AddSeconds(-1)
                });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        // Duplicate conflicts must share the same actor without adding a batch.
        conflicts.Add(new ConflictLogDto
        {
            EntityName = conflicts[0].EntityName, EntityId = conflicts[0].EntityId, ServerJson = "{}"
        });
        var noServerSnapshot = new ConflictLogDto
        {
            EntityName = conflicts[0].EntityName, EntityId = conflicts[0].EntityId, ServerJson = ""
        };
        conflicts.Add(noServerSnapshot);
        counter.Reset();
        var method = typeof(SyncController).GetMethod("PopulateServerConflictActorsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(controller, [conflicts, cutoff, CancellationToken.None])!;
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].UserId, conflicts[i].ServerUserId);
            Assert.Equal(expected[i].Username, conflicts[i].ServerUsername);
        }
        Assert.Equal(expected[0].UserId, conflicts[^2].ServerUserId);
        Assert.Null(noServerSnapshot.ServerUserId);
        Assert.True(string.IsNullOrWhiteSpace(noServerSnapshot.ServerUsername));
        Assert.Equal(2, counter.AuditLogSelectCount);
    }
}
