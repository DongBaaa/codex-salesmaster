using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class SyncMutationTimestampHashTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Instant = new(2026, 9, 7, 4, 52, 49, DateTimeKind.Utc);

    [Fact]
    public void EquivalentWireOffsetsHaveTheSameHash()
    {
        var original = CreateTransfer();
        var wire = JsonSerializer.Serialize(original, Web);
        var offset = JsonSerializer.Deserialize<InventoryTransferDto>(
            wire.Replace("2026-09-07T04:52:49Z", "2026-09-07T04:52:49+00:00"), Web)!;
        Assert.Equal(original.ReceivedAtUtc, offset.ReceivedAtUtc!.Value.ToUniversalTime());
        Assert.Equal(SyncMutationPayloadHasher.Compute(original), SyncMutationPayloadHasher.Compute(offset));
    }

    [Fact]
    public void MixedUtcAndLocalTimestampFieldsHaveTheSameHash()
    {
        var original = CreateTransfer();
        var mixed = CreateTransfer();
        mixed.ReceivedAtUtc = Instant.ToLocalTime();
        mixed.LastStatusChangedAtUtc = Instant.ToLocalTime();
        Assert.Equal(SyncMutationPayloadHasher.Compute(original), SyncMutationPayloadHasher.Compute(mixed));
    }

    [Fact]
    public void UnspecifiedUtcContractFieldsUseUtcWithoutChangingTheInput()
    {
        var original = CreateTransfer();
        var unspecified = CreateTransfer();
        unspecified.LastSavedAtUtc = DateTime.SpecifyKind(Instant, DateTimeKind.Unspecified);
        var before = JsonSerializer.Serialize(unspecified, Web);
        Assert.Equal(SyncMutationPayloadHasher.Compute(original), SyncMutationPayloadHasher.Compute(unspecified));
        Assert.Equal(before, JsonSerializer.Serialize(unspecified, Web));
    }

    [Theory]
    [InlineData(nameof(SyncEntityDto.CreatedAtUtc))]
    [InlineData(nameof(SyncEntityDto.UpdatedAtUtc))]
    [InlineData(nameof(SyncEntityDto.MutationCreatedAtUtc))]
    [InlineData(nameof(InventoryTransferDto.LastSavedAtUtc))]
    [InlineData(nameof(InventoryTransferDto.RequestedAtUtc))]
    [InlineData(nameof(InventoryTransferDto.ReceivedAtUtc))]
    [InlineData(nameof(InventoryTransferDto.LastStatusChangedAtUtc))]
    public void AOneTickChangeRemainsADifferentPayload(string property)
    {
        var original = CreateTransfer();
        var changed = CreateTransfer();
        typeof(InventoryTransferDto).GetProperty(property)!.SetValue(changed, Instant.AddTicks(1));
        Assert.NotEqual(SyncMutationPayloadHasher.Compute(original), SyncMutationPayloadHasher.Compute(changed));
    }

    [Fact]
    public void LegacyExactPayloadReplaysWithoutAcceptingAChangedQuantity()
    {
        var dto = CreateTransfer();
        dto.ReceivedAtUtc = Instant.ToLocalTime();
        // Before normalization the receipt hashed this serialized representation.
        var legacyHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(dto, Web)));
        Assert.True(SyncMutationPayloadHasher.Matches(dto, legacyHash, dto.MutationId));
        dto.Lines[0].Quantity += 1;
        Assert.False(SyncMutationPayloadHasher.Matches(dto, legacyHash, dto.MutationId));
    }

    [Fact]
    public void BusinessDateTextAndNullTimestampRemainSignificant()
    {
        var baseline = CreateTransfer();
        baseline.Memo = "2026-09-07T04:52:49Z";
        var changed = CreateTransfer();
        changed.Memo = "2026-09-07T04:52:49+00:00";
        Assert.NotEqual(SyncMutationPayloadHasher.Compute(baseline), SyncMutationPayloadHasher.Compute(changed));
        changed = CreateTransfer();
        changed.TransferDate = changed.TransferDate.AddDays(1);
        Assert.NotEqual(SyncMutationPayloadHasher.Compute(CreateTransfer()), SyncMutationPayloadHasher.Compute(changed));
        changed = CreateTransfer();
        changed.ReceivedAtUtc = null;
        Assert.NotEqual(SyncMutationPayloadHasher.Compute(CreateTransfer()), SyncMutationPayloadHasher.Compute(changed));
    }

    [Fact]
    public void CommonUtcFieldsAreCanonicalForOtherSyncEntities()
    {
        var original = new CustomerDto { Id = Guid.Parse("64000000-0000-0000-0000-000000000001"), UpdatedAtUtc = Instant };
        var local = new CustomerDto { Id = original.Id, UpdatedAtUtc = Instant.ToLocalTime() };
        Assert.Equal(SyncMutationPayloadHasher.Compute(original), SyncMutationPayloadHasher.Compute(local));
    }

    [Fact]
    public void LegacySemanticInvoiceHashWithLocalTimesStillMatches()
    {
        var dto = new InvoiceDto
        {
            Id = Guid.Parse("67000000-0000-0000-0000-000000000001"),
            MutationId = "device-a:legacy-semantic-invoice",
            UpdatedAtUtc = Instant.ToLocalTime(),
            CustomerName = "derived display name",
            Payments = [new PaymentDto { Amount = 123 }]
        };
        var legacyPayload = JsonSerializer.SerializeToNode(dto, Web)!.AsObject();
        legacyPayload["customerName"] = string.Empty;
        legacyPayload["payments"] = new JsonArray();
        var legacyHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacyPayload, Web)));
        Assert.True(SyncMutationPayloadHasher.Matches(dto, legacyHash, dto.MutationId));
        dto.Memo = "different business input";
        Assert.False(SyncMutationPayloadHasher.Matches(dto, legacyHash, dto.MutationId));
    }

    [Fact]
    public void DateTimeContractPropertiesExplicitlyRepresentUtc()
    {
        var dateProperties = typeof(SyncEntityDto).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            .Where(property => property.PropertyType == typeof(DateTime) || property.PropertyType == typeof(DateTime?))
            .ToArray();
        Assert.NotEmpty(dateProperties);
        Assert.All(dateProperties, property => Assert.EndsWith("Utc", property.Name));
    }

    private static InventoryTransferDto CreateTransfer() => new()
    {
        Id = Guid.Parse("65000000-0000-0000-0000-000000000001"),
        MutationId = "device-a:utc-receipt",
        TransferDate = new DateOnly(2026, 9, 7),
        CreatedAtUtc = Instant,
        UpdatedAtUtc = Instant,
        MutationCreatedAtUtc = Instant,
        LastSavedAtUtc = Instant,
        RequestedAtUtc = Instant,
        ReceivedAtUtc = Instant,
        LastStatusChangedAtUtc = Instant,
        Lines = [new InventoryTransferLineDto { Id = Guid.Parse("66000000-0000-0000-0000-000000000001"), Quantity = 2 }]
    };
}
