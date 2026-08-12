using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Storage;

namespace GeoraePlan.Mobile.App.Services;

internal enum MobileUpdateGatePolicyLoadStatus
{
    Absent,
    Valid,
    Unreadable
}

internal sealed record MobileUpdateGatePolicyLoadResult(
    MobileUpdateGatePolicyLoadStatus Status,
    MobileCachedUpdateRequirement? Requirement,
    string Detail)
{
    public static MobileUpdateGatePolicyLoadResult Absent()
        => new(
            MobileUpdateGatePolicyLoadStatus.Absent,
            Requirement: null,
            Detail: string.Empty);

    public static MobileUpdateGatePolicyLoadResult Valid(
        MobileCachedUpdateRequirement requirement)
        => new(
            MobileUpdateGatePolicyLoadStatus.Valid,
            requirement,
            Detail: string.Empty);

    public static MobileUpdateGatePolicyLoadResult Unreadable(string detail)
        => new(
            MobileUpdateGatePolicyLoadStatus.Unreadable,
            Requirement: null,
            detail);
}

internal interface IMobileUpdateGatePolicyStore
{
    Task<MobileUpdateGatePolicyLoadResult> LoadAsync(
        CancellationToken ct = default);
    Task SaveAsync(
        MobileCachedUpdateRequirement requirement,
        CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

internal sealed class MobileUpdateGatePolicyStore
    : IMobileUpdateGatePolicyStore
{
    private const string LegacyPolicyKey =
        "updates.required_policy.owner_neutral.v1";
    private const string PresenceKey =
        "updates.required_policy.owner_neutral.v2.presence";
    private const string ActiveSlotKey =
        "updates.required_policy.owner_neutral.v2.active";
    private const string SlotAKey =
        "updates.required_policy.owner_neutral.v2.slot_a";
    private const string SlotBKey =
        "updates.required_policy.owner_neutral.v2.slot_b";
    private const string PresenceValue = "required";
    private const int EnvelopeSchemaVersion = 1;
    private const int MaximumPersistedPolicyLength = 64 * 1024;
    private const int MaximumEnvelopeLength =
        MaximumPersistedPolicyLength * 2;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            WriteIndented = false
        };

    private static readonly HashSet<string> LegacyRequirementProperties =
    [
        "schemaVersion",
        "policyVersion",
        "latestVersion",
        "latestBuild",
        "minimumVersion",
        "minimumBuild",
        "minimumProtocolVersion",
        "mandatory",
        "requiresUserAction",
        "message",
        "package"
    ];

    private static readonly HashSet<string> CurrentRequirementProperties =
    [
        .. LegacyRequirementProperties,
        "opaqueServerEnforced",
        "observedClientVersion",
        "observedClientBuild",
        "observedClientProtocolVersion"
    ];

    private static readonly HashSet<string> PackageProperties =
    [
        "platform",
        "version",
        "build",
        "protocolVersion",
        "mandatory",
        "minimumSupportedVersion",
        "minimumSupportedBuild",
        "minimumSupportedProtocolVersion",
        "policyVersion",
        "requiresUserAction",
        "compatibilityPolicy",
        "packageUrl",
        "fileName",
        "sha256",
        "fileSize",
        "notes",
        "releasedAtUtc",
        "installers"
    ];

    private static readonly HashSet<string> EnvelopeProperties =
    [
        "schemaVersion",
        "generation",
        "payload",
        "sha256"
    ];

    public Task<MobileUpdateGatePolicyLoadResult> LoadAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(LoadCore(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MobileAppLogger.Warn(
                "UPDATE",
                $"Required update policy storage could not be read ({ex.GetType().Name}).");
            return Task.FromResult(
                MobileUpdateGatePolicyLoadResult.Unreadable(
                    "policy-store-read-failed"));
        }
    }

    public Task SaveAsync(
        MobileCachedUpdateRequirement requirement,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ct.ThrowIfCancellationRequested();
        SaveCore(requirement, ct);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Presence is established before cleanup and removed last. A crash or
        // Preferences failure anywhere in between therefore remains fail-closed.
        Preferences.Default.Set(PresenceKey, PresenceValue);
        Preferences.Default.Remove(SlotAKey);
        Preferences.Default.Remove(SlotBKey);
        Preferences.Default.Remove(ActiveSlotKey);
        Preferences.Default.Remove(LegacyPolicyKey);
        Preferences.Default.Remove(PresenceKey);
        return Task.CompletedTask;
    }

    private static MobileUpdateGatePolicyLoadResult LoadCore(
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var presence = Preferences.Default.Get(PresenceKey, string.Empty);
        var legacyJson =
            Preferences.Default.Get(LegacyPolicyKey, string.Empty);

        if (string.IsNullOrEmpty(presence))
        {
            if (string.IsNullOrWhiteSpace(legacyJson))
                return MobileUpdateGatePolicyLoadResult.Absent();

            if (!TryDeserializeRequirement(
                    legacyJson,
                    allowLegacySchema: true,
                    out var legacyRequirement))
            {
                return MobileUpdateGatePolicyLoadResult.Unreadable(
                    "legacy-policy-invalid");
            }

            TryMigrateLegacyPolicy(legacyRequirement!, ct);
            return MobileUpdateGatePolicyLoadResult.Valid(
                legacyRequirement!);
        }

        if (!string.Equals(
                presence,
                PresenceValue,
                StringComparison.Ordinal))
        {
            return MobileUpdateGatePolicyLoadResult.Unreadable(
                "policy-presence-invalid");
        }

        var slotA = ReadSlot(SlotAKey);
        var slotB = ReadSlot(SlotBKey);
        var selected = SelectNewestValidSlot(slotA, slotB);
        if (selected?.Requirement is not null)
        {
            return MobileUpdateGatePolicyLoadResult.Valid(
                selected.Requirement);
        }

        // During v1 migration presence is written before the first v2 slot.
        // Keeping the valid v1 payload as a fallback closes that crash window.
        if (!string.IsNullOrWhiteSpace(legacyJson) &&
            TryDeserializeRequirement(
                legacyJson,
                allowLegacySchema: true,
                out var fallback))
        {
            return MobileUpdateGatePolicyLoadResult.Valid(fallback!);
        }

        return MobileUpdateGatePolicyLoadResult.Unreadable(
            "required-policy-evidence-unreadable");
    }

    private static void SaveCore(
        MobileCachedUpdateRequirement requirement,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        requirement.SchemaVersion =
            MobileCachedUpdateRequirement.CurrentSchemaVersion;
        if (!MobileUpdateGatePolicy.IsValidCachedRequirementShape(requirement))
        {
            throw new InvalidOperationException(
                "Required update policy has an invalid shape.");
        }

        var payload = JsonSerializer.Serialize(requirement, JsonOptions);
        if (payload.Length > MaximumPersistedPolicyLength)
        {
            throw new InvalidOperationException(
                "Required update policy exceeds the storage limit.");
        }

        // The marker is intentionally the first durable write.
        Preferences.Default.Set(PresenceKey, PresenceValue);

        var slotA = ReadSlot(SlotAKey);
        var slotB = ReadSlot(SlotBKey);
        var generation = Math.Max(
                slotA?.Generation ?? 0,
                slotB?.Generation ?? 0) +
            1;
        var active = Preferences.Default.Get(
            ActiveSlotKey,
            string.Empty);
        var targetSlot = string.Equals(
                active,
                "a",
                StringComparison.Ordinal)
            ? "b"
            : "a";
        var targetKey =
            targetSlot == "a" ? SlotAKey : SlotBKey;
        var envelope = new PersistedPolicyEnvelope
        {
            SchemaVersion = EnvelopeSchemaVersion,
            Generation = generation,
            Payload = payload,
            Sha256 = ComputeSha256(payload)
        };
        var envelopeJson =
            JsonSerializer.Serialize(envelope, JsonOptions);
        if (envelopeJson.Length > MaximumEnvelopeLength)
        {
            throw new InvalidOperationException(
                "Required update policy envelope exceeds the storage limit.");
        }

        Preferences.Default.Set(targetKey, envelopeJson);
        Preferences.Default.Set(ActiveSlotKey, targetSlot);
        Preferences.Default.Remove(LegacyPolicyKey);
    }

    private static void TryMigrateLegacyPolicy(
        MobileCachedUpdateRequirement requirement,
        CancellationToken ct)
    {
        try
        {
            SaveCore(requirement, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MobileAppLogger.Warn(
                "UPDATE",
                $"Required update policy v1 migration was deferred ({ex.GetType().Name}).");
        }
    }

    private static PersistedPolicySlot? ReadSlot(string key)
    {
        var envelopeJson =
            Preferences.Default.Get(key, string.Empty);
        if (string.IsNullOrWhiteSpace(envelopeJson) ||
            envelopeJson.Length > MaximumEnvelopeLength)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                envelopeJson,
                StrictDocumentOptions());
            if (!HasExactProperties(
                    document.RootElement,
                    EnvelopeProperties))
            {
                return null;
            }

            var envelope =
                JsonSerializer.Deserialize<PersistedPolicyEnvelope>(
                    envelopeJson,
                    JsonOptions);
            if (envelope is null ||
                envelope.SchemaVersion != EnvelopeSchemaVersion ||
                envelope.Generation <= 0 ||
                string.IsNullOrWhiteSpace(envelope.Payload) ||
                envelope.Payload.Length >
                MaximumPersistedPolicyLength ||
                !IsSha256(envelope.Sha256) ||
                !string.Equals(
                    ComputeSha256(envelope.Payload),
                    envelope.Sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !TryDeserializeRequirement(
                    envelope.Payload,
                    allowLegacySchema: false,
                    out var requirement))
            {
                return null;
            }

            return new PersistedPolicySlot(
                envelope.Generation,
                requirement!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PersistedPolicySlot? SelectNewestValidSlot(
        PersistedPolicySlot? slotA,
        PersistedPolicySlot? slotB)
    {
        if (slotA is null)
            return slotB;
        if (slotB is null)
            return slotA;
        return slotA.Generation >= slotB.Generation
            ? slotA
            : slotB;
    }

    private static bool TryDeserializeRequirement(
        string json,
        bool allowLegacySchema,
        out MobileCachedUpdateRequirement? requirement)
    {
        requirement = null;
        if (string.IsNullOrWhiteSpace(json) ||
            json.Length > MaximumPersistedPolicyLength)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                StrictDocumentOptions());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(
                    "schemaVersion",
                    out var schemaElement) ||
                !schemaElement.TryGetInt32(out var schemaVersion))
            {
                return false;
            }

            var expectedProperties =
                schemaVersion ==
                MobileCachedUpdateRequirement.CurrentSchemaVersion
                    ? CurrentRequirementProperties
                    : LegacyRequirementProperties;
            if (!HasExactProperties(root, expectedProperties) ||
                !HasExactPackageShape(root) ||
                (schemaVersion !=
                     MobileCachedUpdateRequirement.CurrentSchemaVersion &&
                 (!allowLegacySchema || schemaVersion != 1)))
            {
                return false;
            }

            requirement =
                JsonSerializer.Deserialize<MobileCachedUpdateRequirement>(
                    json,
                    JsonOptions);
            if (requirement is null)
                return false;

            if (schemaVersion == 1)
            {
                requirement.SchemaVersion =
                    MobileCachedUpdateRequirement.CurrentSchemaVersion;
                requirement.OpaqueServerEnforced = false;
                requirement.ObservedClientVersion = string.Empty;
                requirement.ObservedClientBuild = null;
                requirement.ObservedClientProtocolVersion = null;
            }

            return MobileUpdateGatePolicy
                .IsValidCachedRequirementShape(requirement);
        }
        catch (JsonException)
        {
            requirement = null;
            return false;
        }
    }

    private static bool HasExactPackageShape(JsonElement root)
    {
        if (!root.TryGetProperty("package", out var package))
            return false;
        if (package.ValueKind == JsonValueKind.Null)
            return true;
        if (!HasExactProperties(package, PackageProperties))
            return false;
        if (!package.TryGetProperty("installers", out var installers) ||
            installers.ValueKind != JsonValueKind.Array ||
            installers.GetArrayLength() != 0)
        {
            return false;
        }

        return true;
    }

    private static bool HasExactProperties(
        JsonElement element,
        IReadOnlySet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) ||
                !seen.Add(property.Name))
            {
                return false;
            }
        }

        return seen.SetEquals(expected);
    }

    private static JsonDocumentOptions StrictDocumentOptions()
        => new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        };

    private static string ComputeSha256(string value)
        => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsSha256(string? value)
        => value is { Length: 64 } &&
           value.All(Uri.IsHexDigit);

    private sealed class PersistedPolicyEnvelope
    {
        public int SchemaVersion { get; set; }
        public long Generation { get; set; }
        public string Payload { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed record PersistedPolicySlot(
        long Generation,
        MobileCachedUpdateRequirement Requirement);
}
