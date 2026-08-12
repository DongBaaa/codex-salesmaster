using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class SyncPushCoverageTests
{
    [Fact]
    public void SyncPushRequest_CollectionsAreAllCoveredByPermissionAndNormalizationGates()
    {
        var requestCollectionProperties = typeof(SyncPushRequest)
            .GetProperties()
            .Where(property =>
                property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
            .ToArray();
        var requestCollectionNames = requestCollectionProperties
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var mutationBackedCollectionNames = requestCollectionProperties
            .Where(property => typeof(SyncEntityDto).IsAssignableFrom(property.PropertyType.GetGenericArguments()[0]))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(requestCollectionNames);

        var source = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "SyncController.cs");
        var permissionGateSource = ExtractMethodBlock(source, "private string? ValidatePushPermissions");
        var normalizeSource = ExtractMethodBlock(source, "private static void NormalizePushRequest");
        var mutationCacheCoverageSource = ExtractMethodBlock(source, "private static IEnumerable<SyncEntityDto> EnumeratePushMutationDtos");

        foreach (var collectionName in requestCollectionNames)
        {
            Assert.Contains(
                $"request.{collectionName}",
                permissionGateSource,
                StringComparison.Ordinal);
            Assert.Contains(
                $"request.{collectionName} = RemoveNullEntries(request.{collectionName});",
                normalizeSource,
                StringComparison.Ordinal);
            if (mutationBackedCollectionNames.Contains(collectionName))
            {
                Assert.Contains(
                    $"request.{collectionName}",
                    mutationCacheCoverageSource,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void SyncPushMutationReceipts_ArePrefetchedAndResolvedWithoutPerEntityReceiptQueries()
    {
        var source = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "SyncController.cs");
        var initializeCacheSource = ExtractMethodBlock(source, "private async Task InitializeProcessedMutationCacheAsync");
        var loadCacheSource = ExtractMethodBlock(source, "private async Task LoadProcessedMutationCacheEntriesAsync");
        var duplicateSource = ExtractMethodBlock(source, "private bool TryAcceptDuplicateMutation");
        var registerSource = ExtractMethodBlock(source, "private void RegisterProcessedMutation");

        Assert.Contains("LoadProcessedMutationCacheEntriesAsync", initializeCacheSource, StringComparison.Ordinal);
        Assert.Contains("missingMutationIds.Chunk(500)", loadCacheSource, StringComparison.Ordinal);
        Assert.Contains("_processedMutationsById.TryGetValue", duplicateSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessedSyncMutations.Local", duplicateSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstOrDefaultAsync", duplicateSource, StringComparison.Ordinal);
        Assert.Contains("_processedMutationsById.ContainsKey", registerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessedSyncMutations.Local", registerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MutationReceiptExactReplay_ReusesPrecomputedCanonicalPayloadHash()
    {
        var syncControllerSource = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "SyncController.cs");
        var duplicateSource = ExtractMethodBlock(
            syncControllerSource,
            "private bool TryAcceptDuplicateMutation");
        var directRecorderSource = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Utilities",
            "ProcessedSyncMutationRecorder.cs");
        var directCheckSource = ExtractMethodBlock(
            directRecorderSource,
            "public static async Task<DirectMutationCheck> CheckAsync");

        Assert.Equal(
            1,
            CountOccurrences(
                duplicateSource,
                "SyncMutationPayloadHasher.EvaluateForReceiptReplay("));
        Assert.Contains(
            "payloadEvaluation.StoredPayloadMatches",
            duplicateSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SyncMutationPayloadHasher.Compute(dto)",
            duplicateSource,
            StringComparison.Ordinal);

        Assert.Equal(
            1,
            CountOccurrences(
                directCheckSource,
                "SyncMutationPayloadHasher.EvaluateForReceiptReplay("));
        Assert.Contains(
            "payloadEvaluation.StoredPayloadMatches",
            directCheckSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SyncMutationPayloadHasher.Compute(dto)",
            directCheckSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SyncPushGenericUpserts_ResolveHistoricalConflictsInOneBatchPerEntityType()
    {
        var source = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "SyncController.cs");
        var upsertSource = ExtractMethodBlock(
            source,
            "private async Task<List<TDto>> UpsertEntitiesAsync<TEntity, TDto>");
        var batchedResolutionSource = ExtractMethodBlock(
            source,
            "private async Task ResolveHistoricalConflictsAsync");
        var deduplicationSource = ExtractMethodBlock(
            source,
            "private async Task DeduplicateOpenConflictLogsForResultAsync");

        Assert.Contains(
            "acceptedEntityIdsForHistoricalConflictResolution",
            upsertSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "exactReplayEntityIdsForHistoricalConflictResolution",
            upsertSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "requestedEntityIds.Chunk(500)",
            upsertSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "existingEntitiesById.TryGetValue",
            upsertSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FirstOrDefaultAsync",
            upsertSource,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(
                upsertSource,
                "await ResolveHistoricalConflictsAsync("));
        Assert.Contains(
            "entityIdTexts.Chunk(500)",
            batchedResolutionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "batch.Contains(conflict.EntityId)",
            batchedResolutionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExecuteUpdateAsync",
            batchedResolutionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "fingerprints.Chunk(100)",
            deduplicationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "duplicateIds.Chunk(500)",
            deduplicationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "batchFingerprints.Contains(candidate.Fingerprint)",
            deduplicationSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SyncPushServerConflictActorCutoff_IsCapturedAfterMutationLockAndBeforeMutationWork()
    {
        var source = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "SyncController.cs");
        var pushSource = ExtractMethodBlock(
            source,
            "public async Task<ActionResult<SyncPushResult>> Push");

        var lockIndex = pushSource.IndexOf(
            "InventoryMutationTransactionScope.BeginAsync",
            StringComparison.Ordinal);
        var cutoffIndex = pushSource.IndexOf(
            "var pushStartedAtUtc = DateTime.UtcNow;",
            StringComparison.Ordinal);
        var mutationIndex = pushSource.IndexOf(
            "await InitializeProcessedMutationCacheAsync",
            StringComparison.Ordinal);

        Assert.True(lockIndex >= 0);
        Assert.True(cutoffIndex > lockIndex);
        Assert.True(mutationIndex > cutoffIndex);
    }

    [Fact]
    public void SyncPushCustomerReferenceValidation_PreloadsCategoriesAndMastersInBoundedBatches()
    {
        var source = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "SyncController.cs");
        var filterSource = ExtractMethodBlock(
            source,
            "private async Task<List<CustomerDto>> FilterValidCustomersAsync");

        Assert.Contains("requestedCategoryIds.Chunk(500)", filterSource, StringComparison.Ordinal);
        Assert.Contains("requestedCustomerMasterIds.Chunk(500)", filterSource, StringComparison.Ordinal);
        Assert.Contains("activeCategoryIds.UnionWith", filterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ExistsOrTrackedAsync", filterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstOrDefaultAsync", filterSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncPushPermissionGate_CoversWriteCategoriesWithExpectedPolicies()
    {
        var source = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "SyncController.cs");
        var permissionGateSource = ExtractMethodBlock(source, "private string? ValidatePushPermissions");

        Assert.Contains("PermissionNames.CompanyProfileEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.SettingsEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.CustomerEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.ItemEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.InvoiceEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.PaymentEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.DeliveryEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.RentalSettingsEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.RentalProfileEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.RentalAssetEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.RentalEditAll", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("현재 계정 권한으로 서버 동기화 반영이 허용되지 않는 변경이 포함되어 있습니다", permissionGateSource, StringComparison.Ordinal);
    }

    private static string ExtractMethodBlock(string source, string methodSignature)
    {
        var signatureIndex = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Method was not found: {methodSignature}");

        var openBraceIndex = source.IndexOf('{', signatureIndex);
        Assert.True(openBraceIndex >= 0, $"Method body was not found: {methodSignature}");

        var depth = 0;
        for (var index = openBraceIndex; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return source[signatureIndex..(index + 1)];
                    break;
            }
        }

        throw new InvalidOperationException($"Method body was not closed: {methodSignature}");
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var searchIndex = 0;
        while ((searchIndex = source.IndexOf(value, searchIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            searchIndex += value.Length;
        }

        return count;
    }

    private static string ReadRepositoryFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. pathParts]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Server")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Shared")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
