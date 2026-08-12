using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeoraePlan.Mobile.App.Services;
using Microsoft.Maui.Storage;
using Xunit;
using 거래플랜.Shared.Contracts;

namespace Microsoft.Maui.ApplicationModel
{
    public interface IAppInfo
    {
        string PackageName { get; }
        string VersionString { get; }
        string BuildString { get; }
    }

    public sealed class TestAppInfo : IAppInfo
    {
        public string PackageName { get; init; } = "kr.georaeplan.mobile";
        public string VersionString { get; init; } = "1.0.0";
        public string BuildString { get; init; } = "1";
    }

    public static class AppInfo
    {
        public static IAppInfo Current { get; set; } = new TestAppInfo();
    }
}

namespace Microsoft.Maui.Storage
{
    public interface IPreferences
    {
        string Get(string key, string defaultValue);
        void Set(string key, string value);
        void Remove(string key);
    }

    public static class Preferences
    {
        public static IPreferences Default { get; set; } =
            new EmptyPreferences();

        private sealed class EmptyPreferences : IPreferences
        {
            public string Get(string key, string defaultValue) => defaultValue;
            public void Set(string key, string value)
            {
            }

            public void Remove(string key)
            {
            }
        }
    }
}

namespace GeoraePlan.Mobile.App.Services
{
    public sealed class MobileAppUpdateService
    {
        public Task<MobileAppUpdateCheckResult> CheckForUpdatesAsync(
            string channel = "stable",
            CancellationToken ct = default)
            => throw new NotSupportedException(
                "Gate tests use the internal function seam.");

        internal Task<MobileAppUpdateCheckResult>
            CheckCompatibilityRecoveryAsync(
                CancellationToken ct = default)
            => throw new NotSupportedException(
                "Gate tests use the internal function seam.");
    }
}

namespace GeoraePlan.Desktop.App.Tests
{
    public sealed class MobileHardUpdateGateTests
    {
        private static readonly MobileClientRuntimeIdentity OldClient =
            new("1.0.0", 10, 1);

        [Fact]
        public void PolicyEvaluator_LegacyMandatoryManifestRemainsBlocking()
        {
            var manifest = CreateManifest(
                latestVersion: "2.0.0",
                mandatory: true);

            var result = MobileUpdateGatePolicy.EvaluateManifest(
                manifest,
                OldClient);

            Assert.True(result.ManifestVerified);
            Assert.True(result.IsUpdateAvailable);
            Assert.True(result.IsBelowMinimumSupportedVersion);
            Assert.True(result.RequiresImmediateUpdate);
            Assert.Equal("2.0.0", result.MinimumSupportedVersion);
            var cached = MobileUpdateGatePolicy.CreateCachedRequirement(result);
            Assert.NotNull(cached);
            Assert.True(
                MobileUpdateGatePolicy.IsValidCachedRequirementShape(cached));
        }

        [Fact]
        public void PolicyEvaluator_HonorsPositiveBuildProtocolAndPolicyFields()
        {
            var manifest = CreateManifest(
                latestVersion: "1.0.0",
                latestBuild: 20,
                minimumBuild: 11,
                minimumProtocol: 2,
                latestProtocol: 2,
                policyVersion: 7,
                requiresUserAction: true);

            var result = MobileUpdateGatePolicy.EvaluateManifest(
                manifest,
                OldClient);

            Assert.True(result.ManifestVerified);
            Assert.True(result.IsUpdateAvailable);
            Assert.True(result.IsBelowMinimumSupportedBuild);
            Assert.True(result.IsBelowMinimumSupportedProtocol);
            Assert.True(result.RequiresImmediateUpdate);
            Assert.Equal(7, result.PolicyVersion);
        }

        [Fact]
        public void PolicyEvaluator_OptionalNewerReleaseRemainsDismissible()
        {
            var result = MobileUpdateGatePolicy.EvaluateManifest(
                CreateManifest(
                    latestVersion: "2.0.0",
                    latestBuild: 20,
                    policyVersion: 3,
                    requiresUserAction: false),
                OldClient);

            Assert.True(result.ManifestVerified);
            Assert.True(result.IsUpdateAvailable);
            Assert.False(result.RequiresImmediateUpdate);
        }

        [Fact]
        public void PolicyEvaluator_RejectsZeroOrInconsistentModernFields()
        {
            var invalidManifests = new[]
            {
                CreateManifest("2.0.0", latestBuild: 0),
                CreateManifest("2.0.0", policyVersion: 0),
                CreateManifest("2.0.0", minimumProtocol: 0),
                CreateManifest(
                    "2.0.0",
                    minimumVersion: "3.0.0")
            };

            foreach (var manifest in invalidManifests)
            {
                var result = MobileUpdateGatePolicy.EvaluateManifest(
                    manifest,
                    OldClient);
                Assert.False(result.ManifestVerified);
                Assert.False(result.RequiresImmediateUpdate);
            }
        }

        [Theory]
        [InlineData("Stable", "android")]
        [InlineData(" stable", "android")]
        [InlineData("stable", "Android")]
        [InlineData("stable", " android")]
        [InlineData("stable", "")]
        public void PolicyEvaluator_RequiresExactStableAndroid(
            string channel,
            string platform)
        {
            var manifest = CreateManifest(
                "2.0.0",
                latestBuild: 20,
                policyVersion: 3);
            manifest.Channel = channel;
            manifest.Android!.Platform = platform;

            var result = MobileUpdateGatePolicy
                .EvaluateManifest(
                    manifest,
                    OldClient,
                    "stable");

            Assert.False(result.ManifestVerified);
            Assert.Null(result.Package);
        }

        [Fact]
        public void RecoveryVerifier_RequiresExactPolicyAgreement()
        {
            var manifest = CreateManifest(
                "2.0.0",
                latestBuild: 20,
                minimumVersion: "2.0.0",
                minimumBuild: 20,
                minimumProtocol: 1,
                latestProtocol: 1,
                policyVersion: 3,
                requiresUserAction: true);
            manifest.CompatibilityPolicy = "minimum";
            manifest.Android!.CompatibilityPolicy = "different";

            var result = MobileUpdateGatePolicy
                .EvaluateStableAndroidManifest(
                    manifest,
                    OldClient);

            Assert.False(result.ManifestVerified);
            Assert.Null(result.Package);
        }

        [Fact]
        public void ManualSettingsInstallPath_ExecutesExactProductionPolicyHelpers()
        {
            var exactManifest = CreateManifest(
                "2.0.0",
                latestBuild: 20,
                policyVersion: 3);
            var exact = MobileUpdateGatePolicy
                .EvaluateManualSettingsManifest(
                    exactManifest,
                    OldClient,
                    "stable");
            Assert.True(exact.ManifestVerified);

            var wrongChannel = MobileUpdateGatePolicy
                .EvaluateManualSettingsManifest(
                    exactManifest,
                    OldClient,
                    "Stable");
            Assert.False(wrongChannel.ManifestVerified);

            var wrongPlatformManifest = CreateManifest(
                "2.0.0",
                latestBuild: 20,
                policyVersion: 3);
            wrongPlatformManifest.Android!.Platform =
                "Android";
            var wrongPlatform = MobileUpdateGatePolicy
                .EvaluateManualSettingsManifest(
                    wrongPlatformManifest,
                    OldClient,
                    "stable");
            Assert.False(wrongPlatform.ManifestVerified);

            MobileUpdateGatePolicy
                .EnsureExactAndroidInstallerPackage(
                    exactManifest.Android!);
            foreach (var invalidPlatform in new[]
                     {
                         "Android",
                         " android",
                         "android "
                     })
            {
                var invalidPackage =
                    CreateManifest(
                        "2.0.0",
                        latestBuild: 20,
                        policyVersion: 3)
                    .Android!;
                invalidPackage.Platform = invalidPlatform;
                Assert.Throws<InvalidOperationException>(
                    () => MobileUpdateGatePolicy
                        .EnsureExactAndroidInstallerPackage(
                            invalidPackage));
            }

            var service = NormalizeSource(
                RepositoryFile(
                    "Mobile",
                    "GeoraePlan.Mobile.App",
                    "Services",
                    "MobileAppUpdateService.cs"));

            Assert.Contains(
                ".EvaluateManualSettingsManifest(\n                manifest,\n                current,\n                channel)",
                service,
                StringComparison.Ordinal);
            Assert.Contains(
                ".EnsureExactAndroidInstallerPackage(package)",
                service,
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task PolicyStore_RejectsParseableInvalidAndUnknownJson()
        {
            var preferences = new RecordingPreferences();
            Preferences.Default = preferences;
            var store = new MobileUpdateGatePolicyStore();
            var requirement = CreateCachedRequirement(policyVersion: 5);

            await store.SaveAsync(requirement);
            Assert.All(
                preferences.Values.Keys,
                key =>
                {
                    Assert.DoesNotContain(
                        "tenant",
                        key,
                        StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(
                        "office",
                        key,
                        StringComparison.OrdinalIgnoreCase);
                });
            var valid = await store.LoadAsync();
            Assert.Equal(
                MobileUpdateGatePolicyLoadStatus.Valid,
                valid.Status);
            Assert.NotNull(valid.Requirement);

            var slotKey = Assert.Single(
                preferences.Values.Keys,
                key => key.EndsWith(
                           ".slot_a",
                           StringComparison.Ordinal) ||
                       key.EndsWith(
                           ".slot_b",
                           StringComparison.Ordinal));
            preferences.Values[slotKey] = "{\"schemaVersion\":1";
            var corrupt = await store.LoadAsync();
            Assert.Equal(
                MobileUpdateGatePolicyLoadStatus.Unreadable,
                corrupt.Status);
            Assert.Contains(
                preferences.Values.Keys,
                key => key.EndsWith(
                    ".presence",
                    StringComparison.Ordinal));

            preferences.Values.Clear();
            var legacy = JsonNode.Parse(
                JsonSerializer.Serialize(
                    CreateLegacyRequirement(),
                    new JsonSerializerOptions(
                        JsonSerializerDefaults.Web)))!
                .AsObject();
            legacy["unexpectedProperty"] = true;
            preferences.Values[
                "updates.required_policy.owner_neutral.v1"] =
                legacy.ToJsonString();
            var unknown = await store.LoadAsync();
            Assert.Equal(
                MobileUpdateGatePolicyLoadStatus.Unreadable,
                unknown.Status);
            Assert.Contains(
                "updates.required_policy.owner_neutral.v1",
                preferences.Values.Keys);
        }

        [Fact]
        public async Task PolicyStore_MigratesValidV1AndClearsPresenceLast()
        {
            var preferences = new RecordingPreferences();
            Preferences.Default = preferences;
            preferences.Values[
                "updates.required_policy.owner_neutral.v1"] =
                JsonSerializer.Serialize(
                    CreateLegacyRequirement(),
                    new JsonSerializerOptions(
                        JsonSerializerDefaults.Web));
            var store = new MobileUpdateGatePolicyStore();

            var migrated = await store.LoadAsync();

            Assert.Equal(
                MobileUpdateGatePolicyLoadStatus.Valid,
                migrated.Status);
            Assert.Equal(
                MobileCachedUpdateRequirement.CurrentSchemaVersion,
                migrated.Requirement!.SchemaVersion);
            Assert.Contains(
                preferences.Values.Keys,
                key => key.EndsWith(
                    ".presence",
                    StringComparison.Ordinal));

            preferences.Operations.Clear();
            await store.ClearAsync();

            Assert.Empty(preferences.Values);
            Assert.EndsWith(
                ".presence",
                Assert.Single(
                    preferences.Operations
                        .Where(operation =>
                            operation.StartsWith(
                                "remove:",
                                StringComparison.Ordinal))
                        .TakeLast(1)),
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task PolicyStore_DualSlotFallsBackAndInterruptedSaveStaysPresent()
        {
            var preferences = new RecordingPreferences();
            Preferences.Default = preferences;
            var store = new MobileUpdateGatePolicyStore();

            await store.SaveAsync(
                CreateCachedRequirement(policyVersion: 5));
            await store.SaveAsync(
                CreateCachedRequirement(policyVersion: 6));
            var active = preferences.Values.Single(
                pair => pair.Key.EndsWith(
                    ".active",
                    StringComparison.Ordinal));
            var activeSlotSuffix =
                active.Value == "a" ? ".slot_a" : ".slot_b";
            var activeSlotKey = preferences.Values.Keys.Single(
                key => key.EndsWith(
                    activeSlotSuffix,
                    StringComparison.Ordinal));
            preferences.Values[activeSlotKey] =
                "{\"schemaVersion\":1";

            var recovered = await store.LoadAsync();

            Assert.Equal(
                MobileUpdateGatePolicyLoadStatus.Valid,
                recovered.Status);
            Assert.Equal(5, recovered.Requirement!.PolicyVersion);

            var interruptedPreferences =
                new RecordingPreferences
                {
                    ThrowOnSetKeySuffix = ".slot_a"
                };
            Preferences.Default = interruptedPreferences;
            var interruptedStore =
                new MobileUpdateGatePolicyStore();

            await Assert.ThrowsAsync<IOException>(
                async () => await interruptedStore.SaveAsync(
                    CreateCachedRequirement(policyVersion: 7)));

            Assert.Contains(
                interruptedPreferences.Values.Keys,
                key => key.EndsWith(
                    ".presence",
                    StringComparison.Ordinal));
            var interrupted = await interruptedStore.LoadAsync();
            Assert.Equal(
                MobileUpdateGatePolicyLoadStatus.Unreadable,
                interrupted.Status);
            var firstSet = interruptedPreferences.Operations.First(
                operation => operation.StartsWith(
                    "set:",
                    StringComparison.Ordinal));
            Assert.EndsWith(
                ".presence",
                firstSet,
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task Gate_PreferenceGetOrCorruptEvidenceFailureBlocksWhenOffline()
        {
            var getFailurePreferences = new RecordingPreferences
            {
                ThrowOnGet = true
            };
            Preferences.Default = getFailurePreferences;
            var getFailureGate = CreateGate(
                _ => throw new HttpRequestException("offline"),
                OldClient,
                new MobileUpdateGatePolicyStore());

            var getFailureOutcome = await getFailureGate.CheckAsync();

            Assert.True(getFailureOutcome.IsBlocked);
            Assert.True(getFailureOutcome.NetworkUnavailable);
            Assert.Equal(
                "unreadable-required-policy",
                getFailureOutcome.Source);

            var removeFailurePreferences = new RecordingPreferences
            {
                ThrowOnRemove = true
            };
            removeFailurePreferences.Values[
                "updates.required_policy.owner_neutral.v1"] =
                "{\"schemaVersion\":1}";
            Preferences.Default = removeFailurePreferences;
            var removeFailureGate = CreateGate(
                _ => throw new HttpRequestException("offline"),
                OldClient,
                new MobileUpdateGatePolicyStore());

            var removeFailureOutcome = await removeFailureGate.CheckAsync();

            Assert.True(removeFailureOutcome.IsBlocked);
            Assert.True(removeFailureOutcome.NetworkUnavailable);
            Assert.Equal(
                "unreadable-required-policy",
                removeFailureOutcome.Source);
        }

        [Fact]
        public async Task Gate_OfflineWithoutCacheAllowsButVerifiedCacheRemainsBlocking()
        {
            var noCacheStore = new MemoryPolicyStore();
            var noCacheGate = CreateGate(
                _ => throw new HttpRequestException("offline"),
                OldClient,
                noCacheStore);

            var noCache = await noCacheGate.CheckAsync();

            Assert.False(noCache.IsBlocked);
            Assert.True(noCache.NetworkUnavailable);

            var cachedStore = new MemoryPolicyStore
            {
                Value = CreateCachedRequirement(policyVersion: 5)
            };
            var cachedGate = CreateGate(
                _ => throw new HttpRequestException("offline"),
                OldClient,
                cachedStore);

            var cached = await cachedGate.CheckAsync();

            Assert.True(cached.IsBlocked);
            Assert.True(cached.NetworkUnavailable);
            Assert.Equal("cached-required-policy", cached.Source);
        }

        [Fact]
        public async Task Gate_VerifiedCompatibleResultClearsUnreadableEvidence()
        {
            var store = new MemoryPolicyStore
            {
                LoadStatus =
                    MobileUpdateGatePolicyLoadStatus.Unreadable
            };
            var gate = CreateGate(
                _ => Task.FromResult(
                    CreateCompatibleResult(policyVersion: 9)),
                OldClient,
                store);

            var outcome = await gate.CheckAsync();

            Assert.False(outcome.IsBlocked);
            Assert.True(outcome.Update.ManifestVerified);
            Assert.Equal(1, store.ClearCount);
            Assert.Equal(
                MobileUpdateGatePolicyLoadStatus.Absent,
                store.LoadStatus);
        }

        [Fact]
        public async Task Gate_ClearFailureKeepsUnreadableEvidenceBlocked()
        {
            var store = new MemoryPolicyStore
            {
                LoadStatus =
                    MobileUpdateGatePolicyLoadStatus.Unreadable,
                ThrowOnClear = true
            };
            var gate = CreateGate(
                _ => Task.FromResult(
                    CreateCompatibleResult(policyVersion: 9)),
                OldClient,
                store);

            var outcome = await gate.CheckAsync();

            Assert.True(outcome.IsBlocked);
            Assert.Equal(
                "unreadable-required-policy",
                outcome.Source);
        }

        [Fact]
        public async Task Gate_SelfUpdatedCurrentIdentityClearsCachedBlockWhileOffline()
        {
            var store = new MemoryPolicyStore
            {
                Value = CreateCachedRequirement(policyVersion: 5)
            };
            var gate = CreateGate(
                _ => throw new HttpRequestException("offline"),
                new MobileClientRuntimeIdentity("2.0.0", 20, 1),
                store);

            var outcome = await gate.CheckAsync();

            Assert.False(outcome.IsBlocked);
            Assert.True(outcome.NetworkUnavailable);
            Assert.Null(store.Value);
            Assert.Equal(1, store.ClearCount);
        }

        [Fact]
        public async Task Gate_SamePolicyRelaxationIsIgnoredButHigherPolicyClears()
        {
            var samePolicyStore = new MemoryPolicyStore
            {
                Value = CreateCachedRequirement(policyVersion: 5)
            };
            var samePolicyGate = CreateGate(
                _ => Task.FromResult(CreateCompatibleResult(policyVersion: 5)),
                OldClient,
                samePolicyStore);

            var samePolicy = await samePolicyGate.CheckAsync();

            Assert.True(samePolicy.IsBlocked);
            Assert.NotNull(samePolicyStore.Value);
            Assert.Equal(0, samePolicyStore.ClearCount);

            var higherPolicyStore = new MemoryPolicyStore
            {
                Value = CreateCachedRequirement(policyVersion: 5)
            };
            var higherPolicyGate = CreateGate(
                _ => Task.FromResult(CreateCompatibleResult(policyVersion: 6)),
                OldClient,
                higherPolicyStore);

            var higherPolicy = await higherPolicyGate.CheckAsync();

            Assert.False(higherPolicy.IsBlocked);
            Assert.Null(higherPolicyStore.Value);
            Assert.Equal(1, higherPolicyStore.ClearCount);
        }

        [Fact]
        public void SamePolicyAndReleaseRequiredEvidence_UsesThresholdPartialOrder()
        {
            var existing = CreateThresholdRequirement(
                minimumVersion: "1.5.0",
                minimumBuild: 15,
                minimumProtocol: 2,
                packageMarker: "existing");
            var weaker = CreateThresholdRequirement(
                minimumVersion: "1.4.0",
                minimumBuild: 14,
                minimumProtocol: 1,
                packageMarker: "weaker");
            var stronger = CreateThresholdRequirement(
                minimumVersion: "1.6.0",
                minimumBuild: 16,
                minimumProtocol: 3,
                packageMarker: "stronger");
            var equal = CreateThresholdRequirement(
                minimumVersion: "1.5.0",
                minimumBuild: 15,
                minimumProtocol: 2,
                packageMarker: "equal");
            var incomparable = CreateThresholdRequirement(
                minimumVersion: "1.6.0",
                minimumBuild: 14,
                minimumProtocol: 3,
                packageMarker: "incoming");
            existing.Package!.Sha256 = new string('B', 64);
            incomparable.Package!.Sha256 = new string('C', 64);
            incomparable.Package.FileSize = 2048;

            Assert.Same(
                existing,
                MobileUpdateGatePolicy
                    .ResolveRequiredEvidenceForPersistence(
                        weaker,
                        existing));
            Assert.Same(
                stronger,
                MobileUpdateGatePolicy
                    .ResolveRequiredEvidenceForPersistence(
                        stronger,
                        existing));
            Assert.Same(
                equal,
                MobileUpdateGatePolicy
                    .ResolveRequiredEvidenceForPersistence(
                        equal,
                        existing));

            var merged = MobileUpdateGatePolicy
                .ResolveRequiredEvidenceForPersistence(
                    incomparable,
                    existing);

            Assert.NotSame(incomparable, merged);
            Assert.NotSame(existing, merged);
            Assert.Equal("1.6.0", merged.MinimumVersion);
            Assert.Equal(15, merged.MinimumBuild);
            Assert.Equal(3, merged.MinimumProtocolVersion);
            Assert.NotNull(merged.Package);
            Assert.EndsWith(
                "-incoming.apk",
                merged.Package!.FileName,
                StringComparison.Ordinal);
            Assert.EndsWith(
                "-incoming.apk",
                merged.Package.PackageUrl,
                StringComparison.Ordinal);
            Assert.Equal(new string('C', 64), merged.Package.Sha256);
            Assert.Equal(2048, merged.Package.FileSize);
            Assert.Equal(
                merged.MinimumVersion,
                merged.Package.MinimumSupportedVersion);
            Assert.Equal(
                merged.MinimumBuild,
                merged.Package.MinimumSupportedBuild);
            Assert.Equal(
                merged.MinimumProtocolVersion,
                merged.Package.MinimumSupportedProtocolVersion);
            Assert.True(
                MobileUpdateGatePolicy.IsValidCachedRequirementShape(
                    merged));
            Assert.True(
                MobileUpdateGatePolicy.IsRequiredFor(
                    merged,
                    new MobileClientRuntimeIdentity(
                        "2.0.0",
                        20,
                        2)));
            Assert.False(
                MobileUpdateGatePolicy.IsRequiredFor(
                    merged,
                    new MobileClientRuntimeIdentity(
                        "2.0.0",
                        20,
                        3)));
        }

        [Fact]
        public async Task GateStore_SamePolicyEvidenceCannotWeakenAndMergesMixedThresholds()
        {
            var store = new MemoryPolicyStore
            {
                Value = CreateThresholdRequirement(
                    minimumVersion: "1.5.0",
                    minimumBuild: 15,
                    minimumProtocol: 2)
            };
            var weakerGate = CreateGate(
                _ => Task.FromResult(
                    CreateVerifiedRequiredResult(
                        minimumVersion: "1.4.0",
                        minimumBuild: 14,
                        minimumProtocol: 1)),
                OldClient,
                store);

            var weakerOutcome = await weakerGate.CheckAsync();

            Assert.True(weakerOutcome.IsBlocked);
            Assert.Equal(0, store.SaveCount);
            Assert.NotNull(store.Value);
            Assert.Equal("1.5.0", store.Value!.MinimumVersion);
            Assert.Equal(15, store.Value.MinimumBuild);
            Assert.Equal(2, store.Value.MinimumProtocolVersion);

            var mixedGate = CreateGate(
                _ => Task.FromResult(
                    CreateVerifiedRequiredResult(
                        minimumVersion: "1.6.0",
                        minimumBuild: 14,
                        minimumProtocol: 3)),
                OldClient,
                store);

            var mixedOutcome = await mixedGate.CheckAsync();

            Assert.True(mixedOutcome.IsBlocked);
            Assert.Equal(1, store.SaveCount);
            Assert.NotNull(store.Value);
            Assert.Equal("1.6.0", store.Value!.MinimumVersion);
            Assert.Equal(15, store.Value.MinimumBuild);
            Assert.Equal(3, store.Value.MinimumProtocolVersion);
            Assert.Equal(
                store.Value.MinimumVersion,
                mixedOutcome.Update.MinimumSupportedVersion);
            Assert.Equal(
                store.Value.MinimumBuild,
                mixedOutcome.Update.MinimumSupportedBuild);
            Assert.Equal(
                store.Value.MinimumProtocolVersion,
                mixedOutcome.Update.MinimumSupportedProtocolVersion);
        }

        [Fact]
        public async Task GateActivation_MergesCachedEvidenceEvenWhenCurrentIdentityMeetsIt()
        {
            var current =
                new MobileClientRuntimeIdentity("2.0.0", 15, 2);
            var cached = CreateThresholdRequirement(
                minimumVersion: "1.5.0",
                minimumBuild: 15,
                minimumProtocol: 2,
                includePackage: false);
            cached.LatestBuild = null;
            var store = new MemoryPolicyStore
            {
                Value = cached
            };
            Assert.False(
                MobileUpdateGatePolicy.IsRequiredFor(
                    cached,
                    current));
            var gate = CreateGate(
                _ => throw new HttpRequestException("offline"),
                current,
                store);
            var exception = new MobileClientUpgradeRequiredException(
                "sync/push",
                new ClientUpgradeRequiredResponse
                {
                    Message = "update required",
                    Required = new ClientCompatibilityPolicyDto
                    {
                        PolicyVersion = 5,
                        RequiresUserAction = true,
                        MinimumVersion = "1.6.0",
                        MinimumBuild = 14,
                        MinimumProtocolVersion = 3,
                        LatestVersion = "2.0.0"
                    }
                });

            var outcome = await gate.ActivateAsync(exception);

            Assert.True(outcome.IsBlocked);
            Assert.Equal(1, store.SaveCount);
            Assert.NotNull(store.Value);
            Assert.Equal("1.6.0", store.Value!.MinimumVersion);
            Assert.Equal(15, store.Value.MinimumBuild);
            Assert.Equal(3, store.Value.MinimumProtocolVersion);
            Assert.True(
                MobileUpdateGatePolicy.IsRequiredFor(
                    store.Value,
                    new MobileClientRuntimeIdentity(
                        "2.0.0",
                        14,
                        3)));
        }

        [Fact]
        public async Task Gate_DeduplicatesConcurrentChecks()
        {
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var callCount = 0;
            var gate = CreateGate(
                async _ =>
                {
                    Interlocked.Increment(ref callCount);
                    entered.TrySetResult();
                    await release.Task;
                    return CreateCompatibleResult(policyVersion: 1);
                },
                OldClient,
                new MemoryPolicyStore());

            var first = gate.CheckAsync();
            await entered.Task;
            var second = gate.CheckAsync();
            Assert.Equal(1, Volatile.Read(ref callCount));

            release.TrySetResult();
            var outcomes = await Task.WhenAll(first, second);

            Assert.Equal(1, Volatile.Read(ref callCount));
            Assert.All(outcomes, outcome => Assert.False(outcome.IsBlocked));
        }

        [Fact]
        public async Task Gate_ReusesFreshResultAndForceRefreshBypassesWindow()
        {
            var callCount = 0;
            var now = new DateTimeOffset(
                2026,
                7,
                28,
                0,
                0,
                0,
                TimeSpan.Zero);
            var gate = new MobileCompatibilityGateService(
                _ =>
                {
                    Interlocked.Increment(ref callCount);
                    return Task.FromResult(
                        CreateCompatibleResult(policyVersion: callCount));
                },
                OldClient,
                new MemoryPolicyStore(),
                () => now,
                TimeSpan.FromSeconds(10));

            var first = await gate.CheckAsync();
            var second = await gate.CheckAsync();

            Assert.Equal(1, Volatile.Read(ref callCount));
            Assert.Same(first, second);

            now = now.AddSeconds(11);
            var expired = await gate.CheckAsync();
            Assert.Equal(2, Volatile.Read(ref callCount));
            Assert.NotSame(first, expired);

            var forced = await gate.CheckAsync(forceRefresh: true);
            Assert.Equal(3, Volatile.Read(ref callCount));
            Assert.NotSame(expired, forced);
        }

        [Fact]
        public void Gate_FreshOutcomeAndObservationTimeUseOneAtomicSnapshot()
        {
            var fields = typeof(MobileCompatibilityGateService).GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            var snapshotField = Assert.Single(
                fields,
                field => field.Name == "_latestSnapshot");

            Assert.NotNull(snapshotField.FieldType.GetProperty("Outcome"));
            Assert.NotNull(
                snapshotField.FieldType.GetProperty("ObservedAtUtcTicks"));
            Assert.DoesNotContain(
                fields,
                field => field.Name is
                    "_latestOutcome" or
                    "_latestOutcomeAtUtcTicks" or
                    "_isBlocking");
        }

        [Fact]
        public async Task Gate_DeduplicatesRepeatedUpgradeActivation()
        {
            var store = new MemoryPolicyStore();
            var gate = CreateGate(
                _ => Task.FromResult(CreateCompatibleResult(policyVersion: 1)),
                OldClient,
                store);
            var exception = CreateUpgradeRequiredException(policyVersion: 5);

            var first = await gate.ActivateAsync(exception);
            var second = await gate.ActivateAsync(exception);

            Assert.True(first.IsBlocked);
            Assert.Same(first, second);
            Assert.Equal(1, store.SaveCount);
        }

        [Fact]
        public async Task Gate_NewerCompatibleOutcomeIsNotOverwrittenByStale426()
        {
            var store = new MemoryPolicyStore();
            var gate = CreateGate(
                _ => Task.FromResult(CreateCompatibleResult(policyVersion: 6)),
                OldClient,
                store);

            var compatible = await gate.CheckAsync();
            var stale = await gate.ActivateAsync(
                CreateUpgradeRequiredException(policyVersion: 5));

            Assert.False(compatible.IsBlocked);
            Assert.Same(compatible, stale);
            Assert.False(gate.IsBlocking);
            Assert.Equal(0, store.SaveCount);
        }

        [Fact]
        public void UpgradeSignal_IsolatesThrowingSubscriberAndContinuesDelivery()
        {
            var delivered = 0;
            Action<MobileClientUpgradeRequiredException> throwing =
                _ => throw new InvalidOperationException("observer failed");
            Action<MobileClientUpgradeRequiredException> succeeding =
                _ => Interlocked.Increment(ref delivered);
            MobileClientUpgradeRequiredSignal.Raised += throwing;
            MobileClientUpgradeRequiredSignal.Raised += succeeding;

            try
            {
                MobileClientUpgradeRequiredSignal.Publish(
                    CreateUpgradeRequiredException(policyVersion: 5));
            }
            finally
            {
                MobileClientUpgradeRequiredSignal.Raised -= throwing;
                MobileClientUpgradeRequiredSignal.Raised -= succeeding;
            }

            Assert.Equal(1, delivered);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("{\"schemaVersion\":99,\"rawSecret\":\"do-not-display\"}")]
        [InlineData("{\"error\":\"future_upgrade_error\",\"message\":\"do-not-display\",\"required\":{\"policyVersion\":9,\"minimumBuild\":11}}")]
        public async Task Upgrade426Parser_AlwaysCreatesTypedExceptionAndPublishes(
            string body)
        {
            var delivered = 0;
            MobileClientUpgradeRequiredException? observed = null;
            Action<MobileClientUpgradeRequiredException> subscriber =
                exception =>
                {
                    observed = exception;
                    Interlocked.Increment(ref delivered);
                };
            MobileClientUpgradeRequiredSignal.Raised += subscriber;

            try
            {
                using var content = new StringContent(
                    body,
                    Encoding.UTF8,
                    "application/json");
                var exception =
                    await MobileUpgradeRequiredResponseParser
                        .CreateExceptionAndPublishAsync(
                            "sync/push",
                            content);

                Assert.IsType<MobileClientUpgradeRequiredException>(
                    exception);
                Assert.Equal(
                    HttpStatusCode.UpgradeRequired,
                    exception.StatusCode);
                Assert.Equal(
                    "client_upgrade_required",
                    exception.Response.Error);
                Assert.DoesNotContain(
                    "do-not-display",
                    exception.Message,
                    StringComparison.Ordinal);
                Assert.Same(exception, observed);
                Assert.Equal(1, delivered);
            }
            finally
            {
                MobileClientUpgradeRequiredSignal.Raised -= subscriber;
            }
        }

        [Fact]
        public async Task Opaque426EvidencePersistsOfflineUntilHigherIdentity()
        {
            var store = new MemoryPolicyStore();
            var gate = CreateGate(
                _ => throw new HttpRequestException("offline"),
                OldClient,
                store);
            var fallbackException =
                new MobileClientUpgradeRequiredException(
                    "sync/push",
                    new ClientUpgradeRequiredResponse
                    {
                        Message = "local fallback",
                        Required =
                            new ClientCompatibilityPolicyDto
                            {
                                RequiresUserAction = true
                            }
                    });

            var activated =
                await gate.ActivateAsync(fallbackException);

            Assert.True(activated.IsBlocked);
            Assert.NotNull(store.Value);
            Assert.True(store.Value!.OpaqueServerEnforced);

            var restarted = CreateGate(
                _ => throw new HttpRequestException("offline"),
                OldClient,
                store);
            var offline = await restarted.CheckAsync();
            Assert.True(offline.IsBlocked);

            var updated = CreateGate(
                _ => throw new HttpRequestException("offline"),
                new MobileClientRuntimeIdentity("2.0.0", 20, 1),
                store);
            var afterRealUpdate = await updated.CheckAsync();
            Assert.False(afterRealUpdate.IsBlocked);
        }

        [Theory]
        [InlineData("2.0.0", 9, 2, true)]
        [InlineData("1.0.0", 11, 1, true)]
        [InlineData("1.0.0", 10, 2, true)]
        [InlineData("0.9.0", 11, 3, true)]
        [InlineData("2.0.0", 10, 2, false)]
        [InlineData("1.0.0", 11, 2, false)]
        [InlineData("1.0.0", 10, 3, false)]
        [InlineData("2.0.0", 11, 3, false)]
        public void Opaque426Evidence_ReleasesOnlyAfterMonotonicRuntimeAdvance(
            string currentVersion,
            int currentBuild,
            int currentProtocol,
            bool expectedRequired)
        {
            var requirement = new MobileCachedUpdateRequirement
            {
                PolicyVersion = 0,
                LatestVersion = "1.0.0",
                Mandatory = true,
                RequiresUserAction = true,
                OpaqueServerEnforced = true,
                ObservedClientVersion = "1.0.0",
                ObservedClientBuild = 10,
                ObservedClientProtocolVersion = 2,
                Message = "opaque server compatibility evidence"
            };
            var current = new MobileClientRuntimeIdentity(
                currentVersion,
                currentBuild,
                currentProtocol);

            var required = MobileUpdateGatePolicy.IsRequiredFor(
                requirement,
                current);

            Assert.Equal(expectedRequired, required);
        }

        [Fact]
        public async Task RequiredPolicySaveFailureKeepsCurrentRunBlocked()
        {
            var store = new MemoryPolicyStore
            {
                ThrowOnSave = true
            };
            var gate = CreateGate(
                _ => throw new HttpRequestException("offline"),
                OldClient,
                store);

            var activated = await gate.ActivateAsync(
                CreateUpgradeRequiredException(policyVersion: 5));
            var forcedOffline =
                await gate.CheckAsync(forceRefresh: true);

            Assert.True(activated.IsBlocked);
            Assert.True(forcedOffline.IsBlocked);
            Assert.Equal(
                "in-memory-required-policy",
                forcedOffline.Source);
        }

        [Fact]
        public async Task RequiredPolicySaveFailureRejectsOlderVerifiedClearButAcceptsNewerVerifiedClear()
        {
            var store = new MemoryPolicyStore
            {
                ThrowOnSave = true
            };
            var policyVersion = 1;
            var gate = CreateGate(
                _ => Task.FromResult(
                    CreateCompatibleResult(policyVersion)),
                OldClient,
                store);

            var activated = await gate.ActivateAsync(
                CreateUpgradeRequiredException(policyVersion: 5));
            var olderCompatible =
                await gate.CheckAsync(forceRefresh: true);
            policyVersion = 6;
            var newerCompatible =
                await gate.CheckAsync(forceRefresh: true);

            Assert.True(activated.IsBlocked);
            Assert.True(olderCompatible.IsBlocked);
            Assert.Equal(
                "in-memory-required-policy",
                olderCompatible.Source);
            Assert.Equal(5, olderCompatible.Update.PolicyVersion);
            Assert.False(newerCompatible.IsBlocked);
            Assert.Equal(6, newerCompatible.Update.PolicyVersion);
        }

        [Fact]
        public async Task PersistedOpaquePolicyRejectsOlderVerifiedClearButAcceptsStrictlyNewerVerifiedClear()
        {
            var store = new MemoryPolicyStore();
            var policyVersion = 1;
            var activator = CreateGate(
                _ => throw new HttpRequestException(
                    "offline"),
                OldClient,
                store);
            var activated = await activator.ActivateAsync(
                CreateOpaqueUpgradeRequiredException(
                    policyVersion: 5));
            Assert.True(activated.IsBlocked);
            Assert.False(
                activated.Update.ManifestVerified);
            Assert.NotNull(store.Value);
            Assert.True(
                store.Value!.OpaqueServerEnforced);
            Assert.Equal(
                5,
                store.Value.PolicyVersion);
            var gate = CreateGate(
                _ => Task.FromResult(
                    CreateCompatibleResult(policyVersion)),
                OldClient,
                store);

            var olderCompatible =
                await gate.CheckAsync(forceRefresh: true);
            Assert.True(olderCompatible.IsBlocked);
            Assert.Equal(
                "cached-required-policy",
                olderCompatible.Source);
            Assert.NotNull(store.Value);
            policyVersion = 6;
            var newerCompatible =
                await gate.CheckAsync(forceRefresh: true);

            Assert.False(newerCompatible.IsBlocked);
            Assert.Null(store.Value);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OpaqueUnorderedPolicyCannotBeClearedByArbitraryCompatibleResult(
            bool throwOnSave)
        {
            var store = new MemoryPolicyStore
            {
                ThrowOnSave = throwOnSave
            };
            var gate = CreateGate(
                _ => Task.FromResult(
                    CreateCompatibleResult(
                        policyVersion: 999)),
                OldClient,
                store);

            var activated = await gate.ActivateAsync(
                CreateOpaqueUnorderedUpgradeRequiredException());
            var checkGate = throwOnSave
                ? gate
                : CreateGate(
                    _ => Task.FromResult(
                        CreateCompatibleResult(
                            policyVersion: 999)),
                    OldClient,
                    store);
            var compatible =
                await checkGate.CheckAsync(
                    forceRefresh: true);

            Assert.True(activated.IsBlocked);
            Assert.False(activated.Update.ManifestVerified);
            Assert.True(compatible.IsBlocked);
            Assert.Equal(
                throwOnSave
                    ? "in-memory-required-policy"
                    : "cached-required-policy",
                compatible.Source);
            if (!throwOnSave)
            {
                Assert.NotNull(store.Value);
                Assert.True(
                    store.Value!.OpaqueServerEnforced);
            }
        }

        [Fact]
        public async Task OpaqueUnorderedPolicyClearsOnlyAfterRuntimeIdentityAdvances()
        {
            var store = new MemoryPolicyStore();
            var oldGate = CreateGate(
                _ => Task.FromResult(
                    CreateCompatibleResult(
                        policyVersion: 999)),
                OldClient,
                store);
            await oldGate.ActivateAsync(
                CreateOpaqueUnorderedUpgradeRequiredException());
            Assert.True(
                (await oldGate.CheckAsync(
                    forceRefresh: true)).IsBlocked);

            var updatedGate = CreateGate(
                _ => throw new HttpRequestException(
                    "offline"),
                new MobileClientRuntimeIdentity(
                    "2.0.0",
                    20,
                    2),
                store);
            var updated =
                await updatedGate.CheckAsync();

            Assert.False(updated.IsBlocked);
            Assert.Null(store.Value);
        }

        [Fact]
        public async Task OpaqueUnorderedPolicy_SameRuntimeRequiredThenCompatibleChainRemainsBlocked()
        {
            var store = new MemoryPolicyStore();
            var checkCount = 0;
            var gate = CreateGate(
                _ =>
                {
                    checkCount++;
                    if (checkCount == 1)
                    {
                        return Task.FromResult(
                            MobileUpdateGatePolicy
                                .EvaluateUpgradeRequired(
                                    CreateUpgradeRequiredException(
                                            policyVersion: 1)
                                        .Response,
                                    OldClient));
                    }

                    return Task.FromResult(
                        CreateCompatibleResult(
                            policyVersion: 2));
                },
                OldClient,
                store);

            var activated = await gate.ActivateAsync(
                CreateOpaqueUnorderedUpgradeRequiredException());
            var required =
                await gate.CheckAsync(forceRefresh: true);
            var compatible =
                await gate.CheckAsync(forceRefresh: true);

            Assert.True(activated.IsBlocked);
            Assert.True(required.IsBlocked);
            Assert.True(compatible.IsBlocked);
            Assert.Equal(
                "server-426",
                required.Source);
            Assert.Equal(
                "in-memory-required-policy",
                compatible.Source);
            Assert.NotNull(store.Value);
            Assert.True(store.Value!.OpaqueServerEnforced);
            Assert.Equal(0, store.Value.PolicyVersion);

            var updatedGate = CreateGate(
                _ => throw new HttpRequestException("offline"),
                new MobileClientRuntimeIdentity(
                    "2.0.0",
                    20,
                    2),
                store);
            var afterRuntimeAdvance =
                await updatedGate.CheckAsync();

            Assert.False(afterRuntimeAdvance.IsBlocked);
            Assert.Null(store.Value);
        }

        [Fact]
        public async Task OpaqueUnorderedPolicy_RestartedGateRequiredThenCompatibleChainRemainsBlocked()
        {
            var store = new MemoryPolicyStore();
            var activator = CreateGate(
                _ => throw new HttpRequestException(
                    "offline"),
                OldClient,
                store);
            await activator.ActivateAsync(
                CreateOpaqueUnorderedUpgradeRequiredException());
            var checkCount = 0;
            var restartedGate = CreateGate(
                _ =>
                {
                    checkCount++;
                    if (checkCount == 1)
                    {
                        return Task.FromResult(
                            MobileUpdateGatePolicy
                                .EvaluateUpgradeRequired(
                                    CreateUpgradeRequiredException(
                                            policyVersion: 1)
                                        .Response,
                                    OldClient));
                    }

                    return Task.FromResult(
                        CreateCompatibleResult(
                            policyVersion: 2));
                },
                OldClient,
                store);

            var required =
                await restartedGate.CheckAsync(
                    forceRefresh: true);
            var compatible =
                await restartedGate.CheckAsync(
                    forceRefresh: true);

            Assert.True(required.IsBlocked);
            Assert.False(
                required.Update.ManifestVerified);
            Assert.True(compatible.IsBlocked);
            Assert.Equal(
                "in-memory-required-policy",
                compatible.Source);
            Assert.NotNull(store.Value);
            Assert.True(
                store.Value!.OpaqueServerEnforced);
            Assert.Equal(
                0,
                store.Value.PolicyVersion);
        }

        [Fact]
        public async Task Gate_UpdateCheckTyped426ReturnsBlockedSynchronously()
        {
            var store = new MemoryPolicyStore();
            var gate = CreateGate(
                _ => throw CreateUpgradeRequiredException(
                    policyVersion: 5),
                OldClient,
                store);

            var outcome = await gate.CheckAsync();

            Assert.True(outcome.IsBlocked);
            Assert.Equal("server-426", outcome.Source);
            Assert.NotNull(store.Value);
            Assert.Equal(1, store.SaveCount);
        }

        [Fact]
        public void ClientIdentity_NormalizesPrereleaseAndFallbackToSingleHeaders()
        {
            var provider = new MobileClientIdentityProvider(
                "kr.georaeplan.mobile?!",
                "android!",
                "v1.2.3-preview+build.9",
                "not-a-positive-build",
                protocolVersion: 0);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://trade.example.test/healthz");

            provider.Apply(request);
            provider.Apply(request);

            AssertSingleHeader(
                request,
                ClientCompatibilityHeaders.AppId,
                "kr.georaeplan.mobile");
            AssertSingleHeader(
                request,
                ClientCompatibilityHeaders.Platform,
                "android");
            AssertSingleHeader(
                request,
                ClientCompatibilityHeaders.Version,
                "1.2.3");
            AssertSingleHeader(
                request,
                ClientCompatibilityHeaders.Build,
                "1");
            AssertSingleHeader(
                request,
                ClientCompatibilityHeaders.Protocol,
                "1");
        }

        [Fact]
        public async Task ApkDownloadClient_SendsExactUriAndFiveSingleIdentityHeaders()
        {
            var handler = new CapturingHandler();
            using var http = new HttpClient(handler);
            var provider = new MobileClientIdentityProvider(
                "kr.georaeplan.mobile",
                "android",
                "1.2.3-preview",
                "42",
                protocolVersion: 1);
            var client = new MobilePackageDownloadClient(http, provider);
            var uri = new Uri(
                "https://trade.example.test/updates/download/android/tradeplan-android-v2.0.0.apk");
            var sha256 = new string('A', 64);

            using var response = await client.SendAsync(
                uri,
                sha256,
                HttpCompletionOption.ResponseHeadersRead,
                CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(uri, handler.RequestUri);
            Assert.Equal(1, handler.RequestCount);
            AssertCapturedSingleHeader(
                handler,
                ClientCompatibilityHeaders.AppId,
                "kr.georaeplan.mobile");
            AssertCapturedSingleHeader(
                handler,
                ClientCompatibilityHeaders.Platform,
                "android");
            AssertCapturedSingleHeader(
                handler,
                ClientCompatibilityHeaders.Version,
                "1.2.3");
            AssertCapturedSingleHeader(
                handler,
                ClientCompatibilityHeaders.Build,
                "42");
            AssertCapturedSingleHeader(
                handler,
                ClientCompatibilityHeaders.Protocol,
                "1");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.SendAsync(
                    uri,
                    "bad-sha",
                    HttpCompletionOption.ResponseHeadersRead,
                    CancellationToken.None));
            Assert.Equal(1, handler.RequestCount);

            var serviceSource = File.ReadAllText(RepositoryFile(
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "MobileAppUpdateService.cs"));
            Assert.Contains(
                "new Uri(packageUrl, UriKind.Absolute),\n                expectedSha256,",
                serviceSource.Replace("\r\n", "\n", StringComparison.Ordinal),
                StringComparison.Ordinal);
        }

        [Fact]
        public void LifecycleWiring_GatesBeforeRecoveryShellRealtimeAndSync()
        {
            var appSource = NormalizeSource(RepositoryFile(
                "Mobile",
                "GeoraePlan.Mobile.App",
                "App.cs"));
            var apiSource = NormalizeSource(RepositoryFile(
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "GeoraePlanApiClient.cs"));
            var syncSource = NormalizeSource(RepositoryFile(
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "SyncCoordinator.cs"));

            var startup = ExtractBetween(
                appSource,
                "private async Task InitializeRootAsync()",
                "public static void ShowShell()");
            AssertBefore(
                startup,
                "EnsureCompatibilityGatePassedAsync(",
                "DebugSessionBootstrap");
            AssertBefore(
                startup,
                "EnsureCompatibilityGatePassedAsync(",
                "TryRestoreSessionAsync(\"app-startup\")");

            var beforeShell = ExtractBetween(
                appSource,
                "private async Task ShowShellAfterCompatibilityGateAsync()",
                "public static void ShowLogin()");
            AssertBefore(
                beforeShell,
                "EnsureCompatibilityGatePassedAsync(",
                "MainPage = new AppShell()");

            var background = ExtractBetween(
                appSource,
                "private async Task StartBackgroundServicesAsync()",
                "private async Task RunResumeRevisionSyncAsync");
            AssertBefore(
                background,
                "EnsureCompatibilityGatePassedAsync(",
                "_realtimeSyncService.Start()");
            AssertBefore(
                background,
                "EnsureCompatibilityGatePassedAsync(",
                "RunLaunchSyncAsync()");

            var resume = ExtractBetween(
                appSource,
                "private async Task RunResumeRevisionSyncAsync",
                "private async Task RunUpdatePromptAsync");
            AssertBefore(
                resume,
                "_realtimeSyncService.Stop()",
                "EnsureCompatibilityGatePassedAsync(reason)");
            AssertBefore(
                resume,
                "EnsureCompatibilityGatePassedAsync(reason)",
                "TryRestoreSessionAsync(reason)");
            AssertBefore(
                resume,
                "EnsureCompatibilityGatePassedAsync(reason)",
                "RefreshIfServerChangedAsync(reason");

            var ensureGate = ExtractBetween(
                appSource,
                "private async Task<bool> EnsureCompatibilityGatePassedAsync",
                "private async Task ShowLatestCompatibilityGateAsync");
            Assert.Contains(
                "_compatibilityGate.CheckAsync()",
                ensureGate,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "forceRefresh: true",
                ensureGate,
                StringComparison.Ordinal);
            var installGate = ExtractBetween(
                appSource,
                "private async Task HandleRequiredUpdateInstallAsync",
                "private async Task HandleRequiredUpdateRetryAsync");
            Assert.Contains(
                "CheckAsync(forceRefresh: true)",
                installGate,
                StringComparison.Ordinal);
            var retryGate = ExtractBetween(
                appSource,
                "private async Task HandleRequiredUpdateRetryAsync",
                "private async Task ResumeAfterCompatibilityGateAsync");
            Assert.Contains(
                "CheckAsync(forceRefresh: true)",
                retryGate,
                StringComparison.Ordinal);

            var signalHandler = ExtractBetween(
                appSource,
                "private void HandleClientUpgradeRequired(",
                "private async Task HandleClientUpgradeRequiredAsync(");
            AssertBefore(
                signalHandler,
                "_realtimeSyncService.Stop()",
                "HandleClientUpgradeRequiredAsync(exception)");
            Assert.Contains(
                ".CreateExceptionAndPublishAsync(",
                apiSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "throw upgradeException;",
                apiSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "ex is MobileClientUpgradeRequiredException ||",
                syncSource,
                StringComparison.Ordinal);
        }

        [Fact]
        public void UpdateRequiredPage_IsNonDismissibleAndDoesNotClearBusinessState()
        {
            var pageSource = NormalizeSource(RepositoryFile(
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Pages",
                "UpdateRequiredPage.cs"));
            var appSource = NormalizeSource(RepositoryFile(
                "Mobile",
                "GeoraePlan.Mobile.App",
                "App.cs"));
            var gateSource = NormalizeSource(RepositoryFile(
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "MobileCompatibilityGateService.cs"));
            var combined = string.Join("\n", pageSource, appSource, gateSource);

            Assert.Contains("\"업데이트 설치\"", pageSource, StringComparison.Ordinal);
            Assert.Contains("\"다시 확인\"", pageSource, StringComparison.Ordinal);
            Assert.Contains(
                "protected override bool OnBackButtonPressed()\n        => true;",
                pageSource,
                StringComparison.Ordinal);
            Assert.DoesNotContain("\"나중에\"", pageSource, StringComparison.Ordinal);
            Assert.DoesNotContain("\"닫기\"", pageSource, StringComparison.Ordinal);
            Assert.Contains("미전송 자료와 첨부파일은 그대로 보존", pageSource, StringComparison.Ordinal);
            Assert.DoesNotContain("_sessionStore.ClearAsync", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("PendingPush.Clear", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("PendingPaymentAttachments.Remove", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("_attachmentStore.Remove", combined, StringComparison.Ordinal);
        }

        private static MobileCompatibilityGateService CreateGate(
            Func<CancellationToken, Task<MobileAppUpdateCheckResult>> check,
            MobileClientRuntimeIdentity identity,
            IMobileUpdateGatePolicyStore store)
            => new(check, identity, store);

        private static AppUpdateManifestDto CreateManifest(
            string latestVersion,
            bool mandatory = false,
            string minimumVersion = "",
            int? latestBuild = null,
            int? minimumBuild = null,
            int? minimumProtocol = null,
            int? latestProtocol = null,
            int? policyVersion = null,
            bool? requiresUserAction = null)
        {
            var fileVersion = latestVersion
                .Replace("+", "-", StringComparison.Ordinal)
                .Replace(".", "-", StringComparison.Ordinal);
            var fileName = $"tradeplan-android-v{fileVersion}.apk";
            return new AppUpdateManifestDto
            {
                Channel = "stable",
                ProtocolVersion = latestProtocol,
                PolicyVersion = policyVersion,
                RequiresUserAction = requiresUserAction,
                Android = new AppUpdatePackageDto
                {
                    Platform = "android",
                    Version = latestVersion,
                    Build = latestBuild,
                    ProtocolVersion = latestProtocol,
                    Mandatory = mandatory,
                    MinimumSupportedVersion = minimumVersion,
                    MinimumSupportedBuild = minimumBuild,
                    MinimumSupportedProtocolVersion = minimumProtocol,
                    PolicyVersion = policyVersion,
                    RequiresUserAction = requiresUserAction,
                    PackageUrl = $"/updates/download/android/{fileName}",
                    FileName = fileName,
                    Sha256 = new string('A', 64),
                    FileSize = 1024
                }
            };
        }

        private static MobileCachedUpdateRequirement CreateCachedRequirement(
            int policyVersion)
            => new()
            {
                PolicyVersion = policyVersion,
                LatestVersion = "2.0.0",
                MinimumVersion = "2.0.0",
                Mandatory = true,
                RequiresUserAction = true,
                Message = "업데이트 필요"
            };

        private static MobileCachedUpdateRequirement
            CreateThresholdRequirement(
                string minimumVersion,
                int minimumBuild,
                int minimumProtocol,
                string packageMarker = "",
                bool includePackage = true)
        {
            var fileMarker = string.IsNullOrWhiteSpace(packageMarker)
                ? string.Empty
                : $"-{packageMarker}";
            var fileName =
                $"tradeplan-android-v2.0.0{fileMarker}.apk";
            var requirement = new MobileCachedUpdateRequirement
            {
                PolicyVersion = 5,
                LatestVersion = "2.0.0",
                LatestBuild = 20,
                MinimumVersion = minimumVersion,
                MinimumBuild = minimumBuild,
                MinimumProtocolVersion = minimumProtocol,
                Mandatory = true,
                RequiresUserAction = true,
                Message = "update required"
            };
            if (includePackage)
            {
                requirement.Package = new AppUpdatePackageDto
                {
                    Platform = "android",
                    Version = "2.0.0",
                    Build = 20,
                    ProtocolVersion = 4,
                    Mandatory = true,
                    MinimumSupportedVersion = minimumVersion,
                    MinimumSupportedBuild = minimumBuild,
                    MinimumSupportedProtocolVersion =
                        minimumProtocol,
                    PolicyVersion = 5,
                    RequiresUserAction = true,
                    PackageUrl =
                        $"/updates/download/android/{fileName}",
                    FileName = fileName,
                    Sha256 = new string('A', 64),
                    FileSize = 1024
                };
            }

            return requirement;
        }

        private static MobileAppUpdateCheckResult
            CreateVerifiedRequiredResult(
                string minimumVersion,
                int minimumBuild,
                int minimumProtocol)
            => new()
            {
                CurrentVersion = OldClient.Version,
                CurrentBuild = OldClient.Build,
                CurrentProtocolVersion = OldClient.ProtocolVersion,
                LatestVersion = "2.0.0",
                LatestBuild = 20,
                MinimumSupportedVersion = minimumVersion,
                MinimumSupportedBuild = minimumBuild,
                MinimumSupportedProtocolVersion = minimumProtocol,
                PolicyVersion = 5,
                RequiresUserAction = true,
                ManifestVerified = true,
                IsUpdateAvailable = true,
                IsBelowMinimumSupportedVersion = true,
                IsBelowMinimumSupportedBuild = true,
                IsBelowMinimumSupportedProtocol = true,
                CanPersistRequiredPolicy = true,
                Message = "update required"
            };

        private static Dictionary<string, object?>
            CreateLegacyRequirement()
            => new(StringComparer.Ordinal)
            {
                ["schemaVersion"] = 1,
                ["policyVersion"] = 5,
                ["latestVersion"] = "2.0.0",
                ["latestBuild"] = null,
                ["minimumVersion"] = "2.0.0",
                ["minimumBuild"] = null,
                ["minimumProtocolVersion"] = null,
                ["mandatory"] = true,
                ["requiresUserAction"] = true,
                ["message"] = "update required",
                ["package"] = null
            };

        private static MobileAppUpdateCheckResult CreateCompatibleResult(
            int policyVersion)
            => new()
            {
                CurrentVersion = OldClient.Version,
                CurrentBuild = OldClient.Build,
                CurrentProtocolVersion = OldClient.ProtocolVersion,
                LatestVersion = OldClient.Version,
                PolicyVersion = policyVersion,
                ManifestVerified = true,
                Message = "현재 앱은 호환됩니다."
            };

        private static MobileClientUpgradeRequiredException
            CreateUpgradeRequiredException(int policyVersion)
            => new(
                "sync/push",
                new ClientUpgradeRequiredResponse
                {
                    Message = "필수 업데이트가 필요합니다.",
                    Required = new ClientCompatibilityPolicyDto
                    {
                        PolicyVersion = policyVersion,
                        RequiresUserAction = true,
                        MinimumVersion = "2.0.0",
                        LatestVersion = "2.0.0",
                        LatestBuild = 20,
                        UpdateUrl =
                            "/updates/download/android/tradeplan-android-v2.0.0.apk"
                    }
                });

        private static MobileClientUpgradeRequiredException
            CreateOpaqueUnorderedUpgradeRequiredException()
            => new(
                "sync/push",
                new ClientUpgradeRequiredResponse
                {
                    Message = "필수 업데이트가 필요합니다.",
                    Required = new ClientCompatibilityPolicyDto
                    {
                        PolicyVersion = 0,
                        RequiresUserAction = true,
                        UpdateUrl =
                            "/updates/download/android/tradeplan-android.apk"
                    }
                });

        private static MobileClientUpgradeRequiredException
            CreateOpaqueUpgradeRequiredException(
                int policyVersion)
            => new(
                "sync/push",
                new ClientUpgradeRequiredResponse
                {
                    Message = "?꾩닔 ?낅뜲?댄듃媛 ?꾩슂?⑸땲??",
                    Required =
                        new ClientCompatibilityPolicyDto
                        {
                            PolicyVersion =
                                policyVersion,
                            RequiresUserAction = true,
                            UpdateUrl =
                                "/updates/download/android/tradeplan-android.apk"
                        }
                });

        private static void AssertSingleHeader(
            HttpRequestMessage request,
            string name,
            string expected)
        {
            var values = request.Headers.GetValues(name).ToArray();
            Assert.Single(values);
            Assert.Equal(expected, values[0]);
        }

        private static void AssertCapturedSingleHeader(
            CapturingHandler handler,
            string name,
            string expected)
        {
            Assert.True(handler.Headers.TryGetValue(name, out var values));
            Assert.Single(values!);
            Assert.Equal(expected, values![0]);
        }

        private static string NormalizeSource(string path)
            => File.ReadAllText(path)
                .Replace("\r\n", "\n", StringComparison.Ordinal);

        private static string ExtractBetween(
            string source,
            string start,
            string end)
        {
            var startIndex = source.IndexOf(start, StringComparison.Ordinal);
            Assert.True(startIndex >= 0, $"Start token not found: {start}");
            var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
            Assert.True(endIndex > startIndex, $"End token not found: {end}");
            return source[startIndex..endIndex];
        }

        private static void AssertBefore(
            string source,
            string first,
            string second)
        {
            var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
            var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
            Assert.True(firstIndex >= 0, $"Token not found: {first}");
            Assert.True(secondIndex > firstIndex, $"{first} must precede {second}.");
        }

        private static string RepositoryFile(params string[] segments)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null &&
                   !File.Exists(Path.Combine(directory.FullName, "거래플랜.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine([directory!.FullName, .. segments]);
        }

        private sealed class MemoryPolicyStore : IMobileUpdateGatePolicyStore
        {
            private MobileCachedUpdateRequirement? _value;

            public MobileCachedUpdateRequirement? Value
            {
                get => _value;
                set
                {
                    _value = value;
                    LoadStatus = value is null
                        ? MobileUpdateGatePolicyLoadStatus.Absent
                        : MobileUpdateGatePolicyLoadStatus.Valid;
                }
            }

            public MobileUpdateGatePolicyLoadStatus LoadStatus
            {
                get;
                set;
            } = MobileUpdateGatePolicyLoadStatus.Absent;
            public int SaveCount { get; private set; }
            public int ClearCount { get; private set; }
            public bool ThrowOnSave { get; init; }
            public bool ThrowOnClear { get; init; }

            public Task<MobileUpdateGatePolicyLoadResult> LoadAsync(
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(
                    LoadStatus switch
                    {
                        MobileUpdateGatePolicyLoadStatus.Valid
                            when Value is not null =>
                            MobileUpdateGatePolicyLoadResult.Valid(
                                Value),
                        MobileUpdateGatePolicyLoadStatus.Unreadable =>
                            MobileUpdateGatePolicyLoadResult.Unreadable(
                                "test-unreadable"),
                        _ =>
                            MobileUpdateGatePolicyLoadResult.Absent()
                    });
            }

            public Task SaveAsync(
                MobileCachedUpdateRequirement requirement,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (ThrowOnSave)
                    throw new IOException("save failed");
                Value = requirement;
                SaveCount++;
                return Task.CompletedTask;
            }

            public Task ClearAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (ThrowOnClear)
                    throw new IOException("clear failed");
                Value = null;
                ClearCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingPreferences : IPreferences
        {
            public Dictionary<string, string> Values { get; } =
                new(StringComparer.Ordinal);
            public List<string> Operations { get; } = [];
            public bool ThrowOnGet { get; init; }
            public bool ThrowOnSet { get; init; }
            public string ThrowOnSetKeySuffix { get; init; } =
                string.Empty;
            public bool ThrowOnRemove { get; init; }

            public string Get(string key, string defaultValue)
            {
                Operations.Add($"get:{key}");
                if (ThrowOnGet)
                    throw new IOException("preference get failed");
                return Values.TryGetValue(key, out var value)
                    ? value
                    : defaultValue;
            }

            public void Set(string key, string value)
            {
                Operations.Add($"set:{key}");
                if (ThrowOnSet ||
                    (!string.IsNullOrEmpty(
                         ThrowOnSetKeySuffix) &&
                     key.EndsWith(
                         ThrowOnSetKeySuffix,
                         StringComparison.Ordinal)))
                    throw new IOException("preference set failed");
                Values[key] = value;
            }

            public void Remove(string key)
            {
                Operations.Add($"remove:{key}");
                if (ThrowOnRemove)
                    throw new IOException("preference remove failed");
                Values.Remove(key);
            }
        }

        private sealed class CapturingHandler : HttpMessageHandler
        {
            public int RequestCount { get; private set; }
            public Uri? RequestUri { get; private set; }
            public Dictionary<string, string[]> Headers { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestCount++;
                RequestUri = request.RequestUri;
                Headers.Clear();
                foreach (var header in request.Headers)
                    Headers[header.Key] = header.Value.ToArray();

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        Encoding.UTF8.GetBytes("fake-apk"))
                });
            }
        }
    }
}
