using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Push_ExactReplayResolvesOnlyConflictsProvenByItsReceipt(bool hasPayloadHash)
    {
        var fixture = await SeedPaymentAtomicityFixtureAsync("replay-proof");
        _dbContext.ChangeTracker.Clear();
        var customer = await _dbContext.Customers.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == fixture.CustomerId);
        var accepted = customer.ToDto();
        accepted.ExpectedRevision = customer.Revision;
        accepted.MutationId = "replay-proof-accepted";
        accepted.MutationCreatedAtUtc = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var json = JsonSerializer.Serialize(accepted);
        _dbContext.ProcessedSyncMutations.Add(new ProcessedSyncMutation
        {
            MutationId = accepted.MutationId,
            EntityName = nameof(Customer),
            EntityId = accepted.Id.ToString(),
            ExpectedRevision = accepted.ExpectedRevision,
            PayloadHash = hasPayloadHash ? SyncMutationPayloadHasher.Compute(accepted) : string.Empty,
            ProcessedAtUtc = accepted.MutationCreatedAtUtc.Value
        });
        ConflictLog Conflict(string clientJson, string reason) => new()
        {
            EntityName = nameof(Customer), EntityId = customer.Id.ToString(),
            ClientJson = clientJson, Reason = reason, Status = "Open",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        var matching = Conflict(json, "matching accepted command");
        var otherPayload = JsonSerializer.Deserialize<CustomerDto>(json)!;
        otherPayload.NameOriginal = "unaccepted name with reused mutation id";
        var otherMutation = JsonSerializer.Deserialize<CustomerDto>(json)!;
        otherMutation.MutationId = "replay-proof-other";
        var preserved = new[]
        {
            Conflict(JsonSerializer.Serialize(otherPayload), "different payload"),
            Conflict(JsonSerializer.Serialize(otherMutation), "different mutation"),
            Conflict("", "legacy missing payload"),
            Conflict("{", "malformed payload"),
            Conflict("{}", "missing mutation"),
            Conflict("null", "null payload"),
            Conflict("{\"MutationId\":42}", "invalid mutation type"),
            Conflict(json, "different entity kind"),
            Conflict(json, "different entity id")
        };
        preserved[^2].EntityName = nameof(Invoice);
        preserved[^1].EntityId = Guid.NewGuid().ToString();
        _dbContext.ConflictLogs.Add(matching);
        _dbContext.ConflictLogs.AddRange(preserved);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var before = await _dbContext.ConflictLogs.AsNoTracking().ToDictionaryAsync(x => x.Id);

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "replay-proof-device", Customers = [JsonSerializer.Deserialize<CustomerDto>(json)!]
        }, CancellationToken.None);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(1, result.DuplicateMutationCount);
        Assert.Equal(0, result.ConflictCount);
        _dbContext.ChangeTracker.Clear();
        var after = await _dbContext.ConflictLogs.AsNoTracking().ToDictionaryAsync(x => x.Id);
        Assert.Equal(before.Count, after.Count);
        foreach (var conflict in preserved)
            Assert.Equal(JsonSerializer.Serialize(before[conflict.Id]), JsonSerializer.Serialize(after[conflict.Id]));
        if (hasPayloadHash)
        {
            Assert.Equal("Resolved", after[matching.Id].Status);
            Assert.NotNull(after[matching.Id].ResolvedAtUtc);
            Assert.Equal(json, after[matching.Id].ClientJson);
        }
        else
            Assert.Equal(JsonSerializer.Serialize(before[matching.Id]), JsonSerializer.Serialize(after[matching.Id]));
        var unchanged = await _dbContext.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == customer.Id);
        Assert.Equal(JsonSerializer.Serialize(customer), JsonSerializer.Serialize(unchanged));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Push_ExactReplayPreservesConflictFromLaterDistinctMutation(bool invoice)
    {
        var fixture = await SeedPaymentAtomicityFixtureAsync("replay-conflict-preservation");
        _dbContext.ChangeTracker.Clear();
        SyncPushRequest winner;
        SyncPushRequest loser;
        Guid entityId;
        string entityName;
        if (invoice)
        {
            var entity = await _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
                .Include(x => x.Customer).Include(x => x.Lines).Include(x => x.Payments)
                .SingleAsync(x => x.Id == fixture.InvoiceId);
            var a = entity.ToDto();
            var b = entity.ToDto();
            a.ExpectedRevision = b.ExpectedRevision = entity.Revision;
            a.MutationId = "replay-preserve-invoice-winner";
            b.MutationId = "replay-preserve-invoice-loser";
            a.Memo = "accepted memo";
            b.Memo = "unaccepted memo";
            a.UpdatedAtUtc = b.UpdatedAtUtc = DateTime.UtcNow;
            winner = new SyncPushRequest { DeviceId = "replay-preserve-a", Invoices = [a] };
            loser = new SyncPushRequest { DeviceId = "replay-preserve-b", Invoices = [b] };
            entityId = entity.Id;
            entityName = nameof(Invoice);
        }
        else
        {
            var entity = await _dbContext.Customers.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == fixture.CustomerId);
            var a = entity.ToDto();
            var b = entity.ToDto();
            a.ExpectedRevision = b.ExpectedRevision = entity.Revision;
            a.MutationId = "replay-preserve-customer-winner";
            b.MutationId = "replay-preserve-customer-loser";
            a.NameOriginal = "accepted customer name";
            b.NameOriginal = "unaccepted customer name";
            a.UpdatedAtUtc = b.UpdatedAtUtc = DateTime.UtcNow;
            winner = new SyncPushRequest { DeviceId = "replay-preserve-a", Customers = [a] };
            loser = new SyncPushRequest { DeviceId = "replay-preserve-b", Customers = [b] };
            entityId = entity.Id;
            entityName = nameof(Customer);
        }

        // Reconstruct each HTTP-shaped request so server normalization cannot
        // accidentally change the payload used for the exact receipt replay.
        var winnerWire = JsonSerializer.Serialize(winner);
        async Task<SyncPushResult> Push(string wire)
        {
            var response = await _controller.Push(JsonSerializer.Deserialize<SyncPushRequest>(wire)!, CancellationToken.None);
            return Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        }

        var accepted = await Push(winnerWire);
        Assert.Equal(1, accepted.AcceptedCount);
        Assert.Equal(0, accepted.ConflictCount);
        var rejected = await Push(JsonSerializer.Serialize(loser));
        Assert.Equal(0, rejected.AcceptedCount);
        var conflictDto = Assert.Single(rejected.Conflicts);
        Assert.Equal(entityName, conflictDto.EntityName);
        Assert.Equal(entityId.ToString(), conflictDto.EntityId);
        _dbContext.ChangeTracker.Clear();
        var before = await _dbContext.ConflictLogs.AsNoTracking().SingleAsync(x => x.Id == conflictDto.Id);
        Assert.Equal("Open", before.Status);

        var replay = await Push(winnerWire);
        Assert.Equal(1, replay.AcceptedCount);
        Assert.Equal(1, replay.DuplicateMutationCount);
        Assert.Equal(0, replay.ConflictCount);
        _dbContext.ChangeTracker.Clear();
        var after = await _dbContext.ConflictLogs.AsNoTracking().SingleAsync(x => x.Id == before.Id);
        Assert.Equal("Open", after.Status);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }
}
