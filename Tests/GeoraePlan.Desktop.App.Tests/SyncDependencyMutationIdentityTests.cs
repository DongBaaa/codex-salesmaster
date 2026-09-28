using System.Reflection;
using System.Text.Json;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncDependencyMutationIdentityTests
{
    [Fact]
    public void ReferenceRetry_UsesStableSeparateIdentity_WithoutChangingPayloadOrDirtyMutation()
    {
        var reference = Item();
        var dirty = Item();
        dirty.MutationId = "existing-durable-user-mutation";
        var request = new SyncPushRequest { Items = [reference, dirty] };
        var legacy = reference.MutationId;
        var referenceBefore = JsonSerializer.Serialize(reference);
        var dirtyBefore = JsonSerializer.Serialize(dirty);

        Stamp(request, "device-one", "georaeplan_usenet", reference.Id);
        var identity = reference.MutationId;
        Assert.StartsWith("dependency-v1:", identity);
        Assert.NotEqual(legacy, identity);
        Stamp(request, "device-one", "georaeplan_usenet", reference.Id);
        Assert.Equal(identity, reference.MutationId);
        Assert.Equal(dirtyBefore, JsonSerializer.Serialize(dirty));

        reference.MutationId = legacy;
        Assert.Equal(referenceBefore, JsonSerializer.Serialize(reference));
    }

    [Fact]
    public void ReferenceIdentity_ChangesForDifferentPayloadAtSameRevisionAndTimestamp()
    {
        var item = Item();
        var request = new SyncPushRequest { Items = [item] };
        var revision = item.ExpectedRevision;
        var updated = item.UpdatedAtUtc;
        Stamp(request, "device-one", "georaeplan_usenet", item.Id);
        var original = item.MutationId;

        item.NameOriginal = "Changed reference projection";
        Stamp(request, "device-one", "georaeplan_usenet", item.Id);
        Assert.NotEqual(original, item.MutationId);
        item.NameOriginal = "Reference item";
        Stamp(request, "device-one", "georaeplan_usenet", item.Id);
        Assert.Equal(original, item.MutationId);
        Assert.Equal(revision, item.ExpectedRevision);
        Assert.Equal(updated, item.UpdatedAtUtc);
    }

    [Fact]
    public void ReferenceIdentity_IsBoundedAndSeparatedByDeviceAndBusinessDatabase()
    {
        var item = Item();
        var request = new SyncPushRequest { Items = [item] };
        var device = new string('x', 512);
        Stamp(request, device, "georaeplan_usenet", item.Id);
        var usenet = item.MutationId;
        Assert.InRange(usenet.Length, 1, 128);
        Stamp(request, device, "georaeplan_itworld", item.Id);
        Assert.NotEqual(usenet, item.MutationId);
        Stamp(request, "other-device", "georaeplan_usenet", item.Id);
        Assert.NotEqual(usenet, item.MutationId);
        Stamp(request, device, "georaeplan_usenet", item.Id);
        Assert.Equal(usenet, item.MutationId);
    }

    private static ItemDto Item() => new()
    {
        Id = Guid.NewGuid(), NameOriginal = "Reference item",
        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
        Revision = 71, ExpectedRevision = 71,
        CreatedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAtUtc = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
        MutationCreatedAtUtc = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
        MutationId = "legacy-clean-item-mutation"
    };

    private static void Stamp(SyncPushRequest request, string device, string database, Guid referenceId)
    {
        var keyType = typeof(SyncService).GetNestedType("SyncEntityKey", BindingFlags.NonPublic)!;
        var setType = typeof(HashSet<>).MakeGenericType(keyType);
        var set = Activator.CreateInstance(setType)!;
        setType.GetMethod("Add")!.Invoke(set, [Activator.CreateInstance(keyType, "Item", referenceId)]);
        var method = typeof(SyncService).GetMethod("StampDependencyOnlyMutations", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(SyncService), "StampDependencyOnlyMutations");
        method.Invoke(null, [request, device, database, set]);
    }
}
