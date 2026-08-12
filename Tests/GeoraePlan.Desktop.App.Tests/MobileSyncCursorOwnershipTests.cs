using System.Text.RegularExpressions;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobileSyncCursorOwnershipTests
{
    [Fact]
    public void MobileSyncCoordinator_AdvancesPullCursorOnlyFromSuccessfulPullResponse()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));

        var cursorAssignments = Regex.Matches(
            source,
            @"state\.LastRevision\s*=",
            RegexOptions.CultureInvariant);

        Assert.Single(cursorAssignments.Cast<Match>());
        Assert.Contains(
            "state.LastRevision = Math.Max(state.LastRevision, response.CurrentServerRevision);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "state.LastRevision = Math.Max(state.LastRevision, saved.Revision);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "state.LastRevision = Math.Max(state.LastRevision, result.CurrentServerRevision);",
            source,
            StringComparison.Ordinal);

        var applyPullResponse = ExtractMethod(
            source,
            "private async Task ApplyPullResponseAsync");
        AssertInOrder(
            applyPullResponse,
            "await ApplyPurgeRecordsAsync(",
            "state.LastRevision = Math.Max(state.LastRevision, response.CurrentServerRevision);");
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("contract")]
    public async Task PullAsync_PurgeCacheFailureKeepsCursorAndRetriesSameRevision(
        string purgeKind)
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-contract-cache-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        try
        {
        const long initialRevision = 41;
        const long serverRevision = 99;
        var initialSuccess = DateTime.UtcNow.AddHours(-2);
        var pendingItemId = Guid.NewGuid();
        var pendingAttachmentId = Guid.NewGuid();
        var state = new MobileSyncState
        {
            DeviceId = "cursor-atomicity-device",
            OwnerUsername = "cursor-owner",
            OwnerTenantCode = "USENET-GROUP",
            OwnerOfficeCode = "USENET",
            LastRevision = initialRevision,
            LastSuccessUtc = initialSuccess,
            LastPulledCustomerCount = 7,
            ConsecutiveFailureCount = 0,
            PendingPaymentAttachments =
            [
                new PendingPaymentAttachmentRecord
                {
                    LocalId = pendingAttachmentId,
                    PaymentId = Guid.NewGuid()
                }
            ]
        };
        state.Normalize();
        state.PendingPush.Items.Add(new ItemDto { Id = pendingItemId });

        var purgedEntityId = Guid.NewGuid();
        var firstResponse = CreatePullResponse(
            serverRevision,
            purgeKind,
            purgedEntityId);
        var retryResponse = CreatePullResponse(
            serverRevision,
            purgeKind,
            purgedEntityId);
        using var store = new JsonSyncStateStore(state);
        var api = new GeoraePlanApiClient(firstResponse, retryResponse);
        var contractCache = new CustomerContractCacheStore(cacheRoot);
        string canonicalManifestPath;
        string? cachedPdfPath = null;
        if (string.Equals(purgeKind, "customer", StringComparison.Ordinal))
        {
            await contractCache.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = purgedEntityId,
                    Revision = 1,
                    NameOriginal = "purged customer"
                }
            ]);
            canonicalManifestPath = Path.Combine(cacheRoot, "customers.json");
        }
        else
        {
            var customerId = Guid.NewGuid();
            var pdfBytes = new byte[] { 1, 2, 3, 4 };
            await contractCache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = purgedEntityId,
                    CustomerId = customerId,
                    Revision = 1,
                    FileName = "purged.pdf",
                    FileSize = pdfBytes.LongLength,
                    FileContent = pdfBytes
                }
            ]);
            var customerDirectory = Path.Combine(
                cacheRoot,
                customerId.ToString("N"));
            canonicalManifestPath = Path.Combine(
                customerDirectory,
                "contracts.json");
            cachedPdfPath = Path.Combine(
                customerDirectory,
                $"{purgedEntityId:N}.pdf");
        }
        var canonicalBytes = await File.ReadAllBytesAsync(canonicalManifestPath);
        var coordinator = new SyncCoordinator(
            store,
            api,
            new PaymentAttachmentDraftStore(),
            contractCache,
            new SessionStore());

        MobileSyncState failed;
        using (File.Open(
                   canonicalManifestPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            failed = await coordinator.PullAsync();
        }

        Assert.Equal(initialRevision, failed.LastRevision);
        Assert.Equal(initialSuccess, failed.LastSuccessUtc);
        Assert.Equal(7, failed.LastPulledCustomerCount);
        Assert.NotEmpty(failed.LastError);
        Assert.Equal(
            canonicalBytes,
            await File.ReadAllBytesAsync(canonicalManifestPath));
        if (cachedPdfPath is not null)
            Assert.True(File.Exists(cachedPdfPath));
        Assert.Equal("cursor-atomicity-device", failed.DeviceId);
        Assert.Equal("cursor-owner", failed.OwnerUsername);
        Assert.Equal("USENET-GROUP", failed.OwnerTenantCode);
        Assert.Equal("USENET", failed.OwnerOfficeCode);
        Assert.Contains(failed.PendingPush.Items, item => item.Id == pendingItemId);
        Assert.Contains(
            failed.PendingPaymentAttachments,
            attachment => attachment.LocalId == pendingAttachmentId);
        Assert.Equal(initialRevision, store.SavedStates.Single().LastRevision);
        var durableReload = await store.LoadAsync();
        Assert.NotSame(failed, durableReload);
        Assert.Equal("cursor-atomicity-device", durableReload.DeviceId);
        Assert.Equal("cursor-owner", durableReload.OwnerUsername);
        Assert.Equal("USENET-GROUP", durableReload.OwnerTenantCode);
        Assert.Equal("USENET", durableReload.OwnerOfficeCode);
        Assert.Contains(
            durableReload.PendingPush.Items,
            item => item.Id == pendingItemId);
        Assert.Contains(
            durableReload.PendingPaymentAttachments,
            attachment => attachment.LocalId == pendingAttachmentId);

        var retried = await coordinator.PullAsync();

        Assert.Equal(
            new[] { initialRevision, initialRevision },
            api.RequestedPullRevisions);
        Assert.Equal(serverRevision, retried.LastRevision);
        Assert.Equal(string.Empty, retried.LastError);
        Assert.Equal(0, retried.LastPulledCustomerCount);
        Assert.Equal("cursor-atomicity-device", retried.DeviceId);
        Assert.Contains(retried.PendingPush.Items, item => item.Id == pendingItemId);
        Assert.Contains(
            retried.PendingPaymentAttachments,
            attachment => attachment.LocalId == pendingAttachmentId);
        if (string.Equals(purgeKind, "customer", StringComparison.Ordinal))
        {
            Assert.DoesNotContain(
                await contractCache.LoadCustomersAsync(),
                customer => customer.Id == purgedEntityId);
        }
        else
        {
            var customerDirectory = Path.GetDirectoryName(
                canonicalManifestPath)!;
            var customerId = Guid.ParseExact(
                Path.GetFileName(customerDirectory),
                "N");
            Assert.DoesNotContain(
                await contractCache.LoadContractsAsync(customerId),
                contract => contract.Id == purgedEntityId);
            Assert.False(File.Exists(cachedPdfPath));
        }
        AssertNoAtomicResidue(cacheRoot);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PullAsync_CustomerDirectoryDeleteFailureKeepsCursorUntilReplayCleansIt()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-customer-cache-delete-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        try
        {
            const long initialRevision = 29;
            const long serverRevision = 103;
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var pdfBytes = new byte[] { 5, 6, 7, 8 };
            var cache = new CustomerContractCacheStore(cacheRoot);
            await cache.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = customerId,
                    Revision = 1,
                    NameOriginal = "customer with locked contract cache"
                }
            ]);
            await cache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 1,
                    FileName = "locked.pdf",
                    FileSize = pdfBytes.LongLength,
                    FileContent = pdfBytes
                }
            ]);
            var customerDirectory = Path.Combine(
                cacheRoot,
                customerId.ToString("N"));
            var pdfPath = Path.Combine(
                customerDirectory,
                $"{contractId:N}.pdf");
            var state = new MobileSyncState
            {
                LastRevision = initialRevision,
                LastSuccessUtc = DateTime.UtcNow.AddHours(-4),
                LastPulledCustomerCount = 4
            };
            state.Normalize();
            using var store = new JsonSyncStateStore(state);
            var api = new GeoraePlanApiClient(
                CreatePullResponse(serverRevision, "customer", customerId),
                CreatePullResponse(serverRevision, "customer", customerId));
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                cache,
                new SessionStore());

            MobileSyncState failed;
            using (File.Open(
                       pdfPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                failed = await coordinator.PullAsync();
            }

            Assert.Equal(initialRevision, failed.LastRevision);
            Assert.NotEmpty(failed.LastError);
            Assert.True(Directory.Exists(customerDirectory));
            Assert.True(File.Exists(pdfPath));
            Assert.DoesNotContain(
                await cache.LoadCustomersAsync(),
                customer => customer.Id == customerId);

            var retried = await coordinator.PullAsync();

            Assert.Equal(
                new[] { initialRevision, initialRevision },
                api.RequestedPullRevisions);
            Assert.Equal(serverRevision, retried.LastRevision);
            Assert.Empty(retried.LastError);
            Assert.False(Directory.Exists(customerDirectory));
            AssertNoAtomicResidue(cacheRoot);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("tmp")]
    [InlineData("bak")]
    public async Task ContractManifestSave_PreservesUnownedDecoyResidue(
        string residueExtension)
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-contract-cache-residue-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var mutationCreatedAtUtc =
                new DateTime(2026, 7, 28, 1, 2, 3, DateTimeKind.Utc);
            var cache = new CustomerContractCacheStore(cacheRoot);
            await cache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 1,
                    FileName = "canonical.pdf"
                }
            ]);
            var customerDirectory = Path.Combine(
                cacheRoot,
                customerId.ToString("N"));
            var manifestPath = Path.Combine(
                customerDirectory,
                "contracts.json");
            var canonicalBytes = await File.ReadAllBytesAsync(manifestPath);
            var residuePath = Path.Combine(
                customerDirectory,
                $".contracts.json.injected.{residueExtension}");
            var decoyBytes = System.Text.Encoding.UTF8.GetBytes(
                "unowned decoy residue");
            await File.WriteAllBytesAsync(residuePath, decoyBytes);

            await cache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 2,
                    ExpectedRevision = 1,
                    MutationId = "contract-cache-v2",
                    MutationCreatedAtUtc = mutationCreatedAtUtc,
                    FileName = "canonical-v2.pdf"
                }
            ]);

            var updatedContract = Assert.Single(
                await cache.LoadContractsAsync(customerId));
            Assert.Equal(contractId, updatedContract.Id);
            Assert.Equal(2, updatedContract.Revision);
            Assert.Equal(1, updatedContract.ExpectedRevision);
            Assert.Equal("contract-cache-v2", updatedContract.MutationId);
            Assert.Equal(
                mutationCreatedAtUtc,
                updatedContract.MutationCreatedAtUtc);
            Assert.Equal("canonical-v2.pdf", updatedContract.FileName);
            Assert.Equal(
                decoyBytes,
                await File.ReadAllBytesAsync(residuePath));
            Assert.NotEqual(
                canonicalBytes,
                await File.ReadAllBytesAsync(manifestPath));
            AssertNoOwnedAtomicResidue(cacheRoot);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ContractManifestSave_RecoversOnlyStrictOwnedResidue()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-contract-cache-owned-residue-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var customerId = Guid.NewGuid();
            var cache = new CustomerContractCacheStore(cacheRoot);
            await cache.SaveContractsAsync(
                customerId,
                Array.Empty<CustomerContractDto>());
            var customerDirectory = Path.Combine(
                cacheRoot,
                customerId.ToString("N"));
            var operationId = Guid.NewGuid();
            var ownedPrefix =
                $".contracts.json.georaeplan-cache-owner-v1.{operationId:N}";
            var markerPath = Path.Combine(
                customerDirectory,
                $"{ownedPrefix}.owner.json");
            var temporaryPath = Path.Combine(
                customerDirectory,
                $"{ownedPrefix}.tmp");
            await File.WriteAllTextAsync(
                markerPath,
                $$"""
                  {
                    "schema": "georaeplan-cache-owner-v1",
                    "targetFileName": "contracts.json",
                    "operationId": "{{operationId:D}}"
                  }
                  """);
            await File.WriteAllTextAsync(
                temporaryPath,
                "provably owned residue");

            await cache.SaveContractsAsync(
                customerId,
                Array.Empty<CustomerContractDto>());

            Assert.False(File.Exists(markerPath));
            Assert.False(File.Exists(temporaryPath));
            AssertNoAtomicResidue(cacheRoot);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ContractManifestSave_FailsClosedForManagedResidueWithoutOwnerMarker()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-contract-cache-unowned-managed-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var customerId = Guid.NewGuid();
            var cache = new CustomerContractCacheStore(cacheRoot);
            await cache.SaveContractsAsync(
                customerId,
                Array.Empty<CustomerContractDto>());
            var customerDirectory = Path.Combine(
                cacheRoot,
                customerId.ToString("N"));
            var manifestPath = Path.Combine(
                customerDirectory,
                "contracts.json");
            var canonicalBytes = await File.ReadAllBytesAsync(manifestPath);
            var unownedManagedResidue = Path.Combine(
                customerDirectory,
                $".contracts.json.georaeplan-cache-owner-v1.{Guid.NewGuid():N}.tmp");
            var residueBytes = System.Text.Encoding.UTF8.GetBytes(
                "managed-looking residue without proof");
            await File.WriteAllBytesAsync(
                unownedManagedResidue,
                residueBytes);

            await Assert.ThrowsAnyAsync<IOException>(
                () => cache.SaveContractsAsync(
                    customerId,
                    Array.Empty<CustomerContractDto>()));

            Assert.Equal(
                canonicalBytes,
                await File.ReadAllBytesAsync(manifestPath));
            Assert.Equal(
                residueBytes,
                await File.ReadAllBytesAsync(unownedManagedResidue));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ContractManifestResidueFailure_DoesNotMutateCanonicalPdf()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-contract-cache-pdf-fail-closed-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var oldPdfBytes = new byte[] { 1, 2, 3, 4 };
            var newPdfBytes = new byte[] { 9, 8, 7, 6 };
            var cache = new CustomerContractCacheStore(cacheRoot);
            await cache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 1,
                    FileName = "canonical.pdf",
                    FileSize = oldPdfBytes.LongLength,
                    FileContent = oldPdfBytes
                }
            ]);
            var customerDirectory = Path.Combine(
                cacheRoot,
                customerId.ToString("N"));
            var pdfPath = Path.Combine(
                customerDirectory,
                $"{contractId:N}.pdf");
            var unownedManifestResidue = Path.Combine(
                customerDirectory,
                $".contracts.json.georaeplan-cache-owner-v1.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(
                unownedManifestResidue,
                "manifest recovery must fail before PDF publish");

            await Assert.ThrowsAnyAsync<IOException>(
                () => cache.SaveContractsAsync(
                    customerId,
                [
                    new CustomerContractDto
                    {
                        Id = contractId,
                        CustomerId = customerId,
                        Revision = 2,
                        FileName = "replacement.pdf",
                        FileSize = newPdfBytes.LongLength,
                        FileContent = newPdfBytes
                    }
                ]));

            Assert.Equal(
                oldPdfBytes,
                await File.ReadAllBytesAsync(pdfPath));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PdfValidationFailure_PreservesExistingCanonicalPdf()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-pdf-stage-validation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        var sourcePath = Path.Combine(
            cacheRoot,
            "downloaded-source.pdf");
        try
        {
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var canonicalBytes = new byte[] { 3, 1, 4, 1, 5 };
            var invalidBytes = new byte[] { 2, 7, 1, 8 };
            var cache = new CustomerContractCacheStore(cacheRoot);
            await cache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 1,
                    FileName = "canonical.pdf",
                    FileSize = canonicalBytes.LongLength,
                    FileContent = canonicalBytes
                }
            ]);
            var pdfPath = Path.Combine(
                cacheRoot,
                customerId.ToString("N"),
                $"{contractId:N}.pdf");

            var ensureResult = await cache.EnsureCachedPdfAsync(
                customerId,
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    FileName = "invalid-size.pdf",
                    FileSize = invalidBytes.LongLength + 1,
                    FileContent = invalidBytes
                });

            Assert.Null(ensureResult);
            Assert.Equal(
                canonicalBytes,
                await File.ReadAllBytesAsync(pdfPath));

            await File.WriteAllBytesAsync(sourcePath, invalidBytes);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => cache.CachePdfAsync(
                    customerId,
                    new CustomerContractDto
                    {
                        Id = contractId,
                        CustomerId = customerId,
                        FileName = "invalid-hash.pdf",
                        FileSize = invalidBytes.LongLength,
                        FileHash = new string('0', 64)
                    },
                    sourcePath));

            Assert.Equal(
                canonicalBytes,
                await File.ReadAllBytesAsync(pdfPath));
            AssertNoAtomicResidue(cacheRoot);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ContractManifestSave_RejectsNonExactOwnerMarkerJson()
    {
        var invalidMarkers = new[]
        {
            """
            {
              "schema": "georaeplan-cache-owner-v1",
              "targetFileName": "contracts.json",
              "operationId": "{0}",
              "unknown": true
            }
            """,
            """
            {
              "schema": "georaeplan-cache-owner-v1",
              "schema": "georaeplan-cache-owner-v1",
              "targetFileName": "contracts.json",
              "operationId": "{0}"
            }
            """,
            """
            {
              "Schema": "georaeplan-cache-owner-v1",
              "targetFileName": "contracts.json",
              "operationId": "{0}"
            }
            """,
            """
            {
              "schema": "georaeplan-cache-owner-v1",
              "targetFileName": "contracts.json",
              "operationId": 7
            }
            """
        };
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-contract-cache-strict-marker-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        try
        {
            foreach (var invalidMarker in invalidMarkers)
            {
                var customerId = Guid.NewGuid();
                var cache = new CustomerContractCacheStore(cacheRoot);
                await cache.SaveContractsAsync(
                    customerId,
                    Array.Empty<CustomerContractDto>());
                var customerDirectory = Path.Combine(
                    cacheRoot,
                    customerId.ToString("N"));
                var operationId = Guid.NewGuid();
                var ownedPrefix =
                    $".contracts.json.georaeplan-cache-owner-v1.{operationId:N}";
                var markerPath = Path.Combine(
                    customerDirectory,
                    $"{ownedPrefix}.owner.json");
                var temporaryPath = Path.Combine(
                    customerDirectory,
                    $"{ownedPrefix}.tmp");
                await File.WriteAllTextAsync(
                    markerPath,
                    invalidMarker.Replace(
                        "{0}",
                        operationId.ToString("D"),
                        StringComparison.Ordinal));
                await File.WriteAllTextAsync(
                    temporaryPath,
                    "must remain after strict validation failure");

                await Assert.ThrowsAnyAsync<InvalidDataException>(
                    () => cache.SaveContractsAsync(
                        customerId,
                        Array.Empty<CustomerContractDto>()));

                Assert.True(File.Exists(markerPath));
                Assert.True(File.Exists(temporaryPath));
            }
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ContractManifestRecovery_ValidatesAllMarkersBeforeDeletingAnyResidue()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-contract-cache-two-phase-recovery-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var customerId = Guid.NewGuid();
            var cache = new CustomerContractCacheStore(cacheRoot);
            await cache.SaveContractsAsync(
                customerId,
                Array.Empty<CustomerContractDto>());
            var customerDirectory = Path.Combine(
                cacheRoot,
                customerId.ToString("N"));
            var validOperationId = Guid.Parse(
                "00000000-0000-0000-0000-000000000001");
            var invalidOperationId = Guid.Parse(
                "ffffffff-ffff-ffff-ffff-ffffffffffff");
            var validPrefix =
                $".contracts.json.georaeplan-cache-owner-v1.{validOperationId:N}";
            var invalidPrefix =
                $".contracts.json.georaeplan-cache-owner-v1.{invalidOperationId:N}";
            var validMarkerPath = Path.Combine(
                customerDirectory,
                $"{validPrefix}.owner.json");
            var validTemporaryPath = Path.Combine(
                customerDirectory,
                $"{validPrefix}.tmp");
            var invalidMarkerPath = Path.Combine(
                customerDirectory,
                $"{invalidPrefix}.owner.json");
            var invalidTemporaryPath = Path.Combine(
                customerDirectory,
                $"{invalidPrefix}.tmp");
            await File.WriteAllTextAsync(
                validMarkerPath,
                $$"""
                  {
                    "schema": "georaeplan-cache-owner-v1",
                    "targetFileName": "contracts.json",
                    "operationId": "{{validOperationId:D}}"
                  }
                  """);
            await File.WriteAllTextAsync(
                validTemporaryPath,
                "valid owned residue");
            await File.WriteAllTextAsync(
                invalidMarkerPath,
                $$"""
                  {
                    "schema": "wrong-schema",
                    "targetFileName": "contracts.json",
                    "operationId": "{{invalidOperationId:D}}"
                  }
                  """);
            await File.WriteAllTextAsync(
                invalidTemporaryPath,
                "invalid owned residue");

            await Assert.ThrowsAnyAsync<InvalidDataException>(
                () => cache.SaveContractsAsync(
                    customerId,
                    Array.Empty<CustomerContractDto>()));

            Assert.True(File.Exists(validMarkerPath));
            Assert.True(File.Exists(validTemporaryPath));
            Assert.True(File.Exists(invalidMarkerPath));
            Assert.True(File.Exists(invalidTemporaryPath));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CustomerSaveAndPurge_InterleavingSerializesWholeReadModifyWrite()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-customer-cache-interleaving-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        var publishEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var customerId = Guid.NewGuid();
            var seedCache = new CustomerContractCacheStore(cacheRoot);
            await seedCache.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = customerId,
                    Revision = 1,
                    NameOriginal = "seed"
                }
            ]);

            var checkpointCount = 0;
            var cache = new CustomerContractCacheStore(
                cacheRoot,
                async (targetPath, ct) =>
                {
                    if (!string.Equals(
                            Path.GetFileName(targetPath),
                            "customers.json",
                            StringComparison.Ordinal) ||
                        Interlocked.Increment(ref checkpointCount) != 1)
                    {
                        return;
                    }

                    publishEntered.TrySetResult();
                    await releasePublish.Task.WaitAsync(ct);
                });

            var saveTask = cache.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = customerId,
                    Revision = 2,
                    NameOriginal = "concurrent save"
                }
            ]);
            await publishEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var purgeTask = cache.RemoveCustomerAsync(
                customerId,
                purgeRevision: 2);
            Assert.NotSame(
                purgeTask,
                await Task.WhenAny(
                    purgeTask,
                    Task.Delay(TimeSpan.FromMilliseconds(100))));

            releasePublish.TrySetResult();
            await Task.WhenAll(saveTask, purgeTask);

            Assert.DoesNotContain(
                await cache.LoadCustomersAsync(),
                customer => customer.Id == customerId);
            AssertNoAtomicResidue(cacheRoot);
        }
        finally
        {
            releasePublish.TrySetResult();
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CustomerManifestProcessLease_SerializesHelperProcessAndRecoversAfterCrash()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-manifest-process-lease-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        System.Diagnostics.Process? helperProcess = null;
        try
        {
            var customerId = Guid.NewGuid();
            var cache = new CustomerContractCacheStore(cacheRoot);
            await cache.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = customerId,
                    Revision = 1,
                    NameOriginal = "canonical"
                }
            ]);
            var manifestPath = Path.Combine(
                cacheRoot,
                "customers.json");
            var canonicalBytes =
                await File.ReadAllBytesAsync(manifestPath);
            var normalizedBindingPath =
                Path.GetFullPath(manifestPath).ToUpperInvariant();
            var lockPath = Directory.GetFiles(
                Path.Combine(cacheRoot, ".manifest-locks"),
                "*.lock",
                SearchOption.TopDirectoryOnly)
                .Single(path =>
                    System.Text.Encoding.UTF8
                        .GetString(File.ReadAllBytes(path))
                        .EndsWith(
                            normalizedBindingPath,
                            StringComparison.Ordinal));
            var expectedBindingBytes =
                System.Text.Encoding.UTF8.GetBytes(
                    $"georaeplan-cache-manifest-lease-v1\n{normalizedBindingPath}");
            var expectedLockHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(
                            normalizedBindingPath)))
                .ToLowerInvariant();
            Assert.Equal(
                $"{expectedLockHash}.lock",
                Path.GetFileName(lockPath));
            Assert.Equal(
                expectedBindingBytes,
                await File.ReadAllBytesAsync(lockPath));
            var readyPath = Path.Combine(
                cacheRoot,
                "lease-helper.ready");
            var helperScriptPath = Path.Combine(
                cacheRoot,
                "hold-manifest-lease.ps1");
            await File.WriteAllTextAsync(
                helperScriptPath,
                """
                param(
                    [Parameter(Mandatory = $true)][string]$LockPath,
                    [Parameter(Mandatory = $true)][string]$ReadyPath
                )
                $lease = [System.IO.File]::Open(
                    $LockPath,
                    [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::ReadWrite,
                    [System.IO.FileShare]::None)
                [System.IO.File]::WriteAllText($ReadyPath, "ready")
                Start-Sleep -Seconds 30
                """);
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(helperScriptPath);
            startInfo.ArgumentList.Add("-LockPath");
            startInfo.ArgumentList.Add(lockPath);
            startInfo.ArgumentList.Add("-ReadyPath");
            startInfo.ArgumentList.Add(readyPath);
            helperProcess = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Manifest lease helper process did not start.");
            var readyDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(readyPath) &&
                   DateTime.UtcNow < readyDeadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }
            Assert.True(
                File.Exists(readyPath),
                "Manifest lease helper did not acquire the lock.");

            using (var cancellation = new CancellationTokenSource(
                       TimeSpan.FromMilliseconds(250)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => cache.SaveCustomersAsync(
                    [
                        new CustomerDto
                        {
                            Id = customerId,
                            Revision = 2,
                            NameOriginal = "must wait"
                        }
                    ],
                    cancellation.Token));
            }

            Assert.Equal(
                canonicalBytes,
                await File.ReadAllBytesAsync(manifestPath));

            helperProcess.Kill(entireProcessTree: true);
            await helperProcess.WaitForExitAsync();
            helperProcess.Dispose();
            helperProcess = null;

            await cache.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = customerId,
                    Revision = 2,
                    NameOriginal = "after release"
                }
            ]);

            Assert.Contains(
                await cache.LoadCustomersAsync(),
                customer =>
                    customer.Id == customerId &&
                    customer.Revision == 2);
            var caseVariantCache =
                new CustomerContractCacheStore(
                    cacheRoot.ToUpperInvariant());
            await caseVariantCache.SaveCustomersAsync(
                await cache.LoadCustomersAsync());
            Assert.Contains(
                lockPath,
                Directory.GetFiles(
                    Path.Combine(cacheRoot, ".manifest-locks"),
                    "*.lock",
                    SearchOption.TopDirectoryOnly),
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal(
                expectedBindingBytes,
                await File.ReadAllBytesAsync(lockPath));
            AssertNoAtomicResidue(cacheRoot);
        }
        finally
        {
            if (helperProcess is not null)
            {
                if (!helperProcess.HasExited)
                    helperProcess.Kill(entireProcessTree: true);
                await helperProcess.WaitForExitAsync();
                helperProcess.Dispose();
            }
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CustomerPurge_HoldsStableCompoundLocksUntilContractDirectoryDecision()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-customer-compound-lock-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        var purgePublishEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePurgePublish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var seedCache = new CustomerContractCacheStore(cacheRoot);
            await seedCache.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = customerId,
                    Revision = 1,
                    NameOriginal = "old generation"
                }
            ]);
            await seedCache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 1,
                    FileName = "old.pdf",
                    FileContent = [1, 1, 1]
                }
            ]);

            var checkpointCount = 0;
            var cache = new CustomerContractCacheStore(
                cacheRoot,
                async (targetPath, ct) =>
                {
                    if (!string.Equals(
                            Path.GetFileName(targetPath),
                            "customers.json",
                            StringComparison.Ordinal) ||
                        Interlocked.Increment(ref checkpointCount) != 1)
                    {
                        return;
                    }

                    purgePublishEntered.TrySetResult();
                    await releasePurgePublish.Task.WaitAsync(ct);
                });
            var purgeTask = cache.RemoveCustomerAsync(
                customerId,
                purgeRevision: 5);
            await purgePublishEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            var saveCustomerTask = cache.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = customerId,
                    Revision = 6,
                    NameOriginal = "new generation"
                }
            ]);
            var saveContractTask = cache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 6,
                    FileName = "new.pdf",
                    FileContent = [6, 6, 6]
                }
            ]);
            var concurrentSaveTasks = Task.WhenAll(
                saveCustomerTask,
                saveContractTask);
            var earlyCompletion = await Task.WhenAny(
                concurrentSaveTasks,
                Task.Delay(TimeSpan.FromMilliseconds(100)));
            Assert.NotSame(concurrentSaveTasks, earlyCompletion);

            releasePurgePublish.TrySetResult();
            await Task.WhenAll(
                purgeTask,
                saveCustomerTask,
                saveContractTask);

            Assert.Contains(
                await cache.LoadCustomersAsync(),
                customer =>
                    customer.Id == customerId &&
                    customer.Revision == 6);
            Assert.Contains(
                await cache.LoadContractsAsync(customerId),
                contract =>
                    contract.Id == contractId &&
                    contract.Revision == 6);
            AssertNoAtomicResidue(cacheRoot);
        }
        finally
        {
            releasePurgePublish.TrySetResult();
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CustomerPurge_HidesStaleCustomerButPreservesContractFirstNewGeneration()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-contract-first-generation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var cache = new CustomerContractCacheStore(cacheRoot);
            await cache.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = customerId,
                    Revision = 1,
                    NameOriginal = "old customer generation"
                }
            ]);
            await cache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 6,
                    FileName = "new-generation.pdf",
                    FileContent = [6, 0, 6]
                }
            ]);

            await cache.RemoveCustomerAsync(
                customerId,
                purgeRevision: 5);

            Assert.DoesNotContain(
                await cache.LoadCustomersAsync(),
                customer => customer.Id == customerId);
            Assert.Contains(
                await cache.LoadContractsAsync(customerId),
                contract =>
                    contract.Id == contractId &&
                    contract.Revision == 6);
            Assert.True(Directory.Exists(Path.Combine(
                cacheRoot,
                customerId.ToString("N"))));
            AssertNoAtomicResidue(cacheRoot);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ContractSaveAndPurge_InterleavingSerializesWholeReadModifyWrite()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-contract-cache-interleaving-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        var publishEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var seedCache = new CustomerContractCacheStore(cacheRoot);
            await seedCache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 1,
                    FileName = "seed.pdf",
                    FileContent = [1, 2, 3]
                }
            ]);

            var checkpointCount = 0;
            var cache = new CustomerContractCacheStore(
                cacheRoot,
                async (targetPath, ct) =>
                {
                    if (!string.Equals(
                            Path.GetFileName(targetPath),
                            "contracts.json",
                            StringComparison.Ordinal) ||
                        Interlocked.Increment(ref checkpointCount) != 1)
                    {
                        return;
                    }

                    publishEntered.TrySetResult();
                    await releasePublish.Task.WaitAsync(ct);
                });

            var saveTask = cache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 2,
                    FileName = "concurrent.pdf",
                    FileContent = [4, 5, 6]
                }
            ]);
            await publishEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var purgeTask = cache.RemoveContractAsync(
                contractId,
                purgeRevision: 2);
            Assert.NotSame(
                purgeTask,
                await Task.WhenAny(
                    purgeTask,
                    Task.Delay(TimeSpan.FromMilliseconds(100))));

            releasePublish.TrySetResult();
            await Task.WhenAll(saveTask, purgeTask);

            Assert.DoesNotContain(
                await cache.LoadContractsAsync(customerId),
                contract => contract.Id == contractId);
            Assert.False(File.Exists(Path.Combine(
                cacheRoot,
                customerId.ToString("N"),
                $"{contractId:N}.pdf")));
            AssertNoAtomicResidue(cacheRoot);
        }
        finally
        {
            releasePublish.TrySetResult();
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PdfWriteAndCustomerPurge_UseTheSameCustomerManifestLock()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-pdf-customer-purge-interleaving-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        var pdfWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePdfWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var seedCache = new CustomerContractCacheStore(cacheRoot);
            await seedCache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 1,
                    FileName = "interleaving.pdf",
                    FileSize = 3
                }
            ]);
            var cache = new CustomerContractCacheStore(
                cacheRoot,
                beforeAtomicPublishAsync: null,
                beforePdfWriteAsync: async (_, ct) =>
                {
                    pdfWriteEntered.TrySetResult();
                    await releasePdfWrite.Task.WaitAsync(ct);
                });
            var contract = new CustomerContractDto
            {
                Id = contractId,
                CustomerId = customerId,
                Revision = 1,
                FileName = "interleaving.pdf",
                FileSize = 3,
                FileContent = [1, 2, 3]
            };

            var pdfWriteTask = cache.EnsureCachedPdfAsync(
                customerId,
                contract);
            await pdfWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var purgeTask = cache.RemoveCustomerContractsAsync(customerId);
            Assert.NotSame(
                purgeTask,
                await Task.WhenAny(
                    purgeTask,
                    Task.Delay(TimeSpan.FromMilliseconds(100))));

            releasePdfWrite.TrySetResult();
            await Task.WhenAll(pdfWriteTask, purgeTask);

            Assert.False(Directory.Exists(Path.Combine(
                cacheRoot,
                customerId.ToString("N"))));
            AssertNoAtomicResidue(cacheRoot);
        }
        finally
        {
            releasePdfWrite.TrySetResult();
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomerPurge_DiscardsStaleOwnerSessionWhenSessionChangesDuringPublish(
        bool lockAtomicResidueDuringFailure)
    {
        var appDataRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-cache-owner-root-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDataRoot);
        FileStream? lockedAtomicResidue = null;
        try
        {
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var mutationCreatedAtUtc =
                new DateTime(2026, 7, 28, 4, 5, 6, DateTimeKind.Utc);
            var session = new SessionStore
            {
                Snapshot = new SessionSnapshot
                {
                    Username = "owner-a",
                    TenantCode = "TENANT-A",
                    OfficeCode = "OFFICE-A"
                }
            };
            var seedCache = new CustomerContractCacheStore(
                session,
                appDataRoot,
                beforeAtomicPublishAsync: null);
            await seedCache.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = customerId,
                    Revision = 1,
                    ExpectedRevision = 7,
                    MutationId = "owner-a-customer",
                    MutationCreatedAtUtc = mutationCreatedAtUtc,
                    NameOriginal = "owner-a customer"
                }
            ]);
            await seedCache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 1,
                    FileName = "owner-a.pdf",
                    FileContent = [7, 8, 9]
                }
            ]);
            var ownerARoot = Directory.GetDirectories(
                Path.Combine(appDataRoot, "contract-cache")).Single();
            var ownerAContractDirectory = Path.Combine(
                ownerARoot,
                customerId.ToString("N"));
            var switched = 0;
            var cache = new CustomerContractCacheStore(
                session,
                appDataRoot,
                (targetPath, _) =>
                {
                    if (string.Equals(
                            Path.GetFileName(targetPath),
                            "customers.json",
                            StringComparison.Ordinal) &&
                        Interlocked.Exchange(ref switched, 1) == 0)
                    {
                        session.Snapshot = new SessionSnapshot
                        {
                            Username = "owner-b",
                            TenantCode = "TENANT-B",
                            OfficeCode = "OFFICE-B"
                        };
                        if (lockAtomicResidueDuringFailure)
                        {
                            var directoryPath =
                                Path.GetDirectoryName(targetPath)
                                ?? throw new InvalidOperationException(
                                    "Customer cache target has no parent.");
                            var temporaryPath = Directory
                                .EnumerateFiles(
                                    directoryPath,
                                    ".customers.json.georaeplan-cache-owner-v1.*.tmp",
                                    SearchOption.TopDirectoryOnly)
                                .Single();
                            lockedAtomicResidue = new FileStream(
                                temporaryPath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.None);
                        }
                    }

                    return Task.CompletedTask;
                });

            var purgeFailure = await Record.ExceptionAsync(
                () => cache.RemoveCustomerAsync(
                    customerId,
                    purgeRevision: 1));
            Assert.NotNull(purgeFailure);
            if (lockAtomicResidueDuringFailure)
            {
                var aggregate = Assert.IsType<AggregateException>(
                    purgeFailure);
                Assert.Contains(
                    aggregate.Flatten().InnerExceptions,
                    exception =>
                        exception is StaleCacheOwnerSessionException);
            }
            else
            {
                Assert.IsType<StaleCacheOwnerSessionException>(
                    purgeFailure);
            }

            lockedAtomicResidue?.Dispose();
            lockedAtomicResidue = null;

            Assert.Equal(1, switched);
            Assert.True(Directory.Exists(ownerAContractDirectory));
            var ownerACache = new CustomerContractCacheStore(ownerARoot);
            var restoredCustomer = Assert.Single(
                await ownerACache.LoadCustomersAsync());
            Assert.Equal(customerId, restoredCustomer.Id);
            Assert.Equal(7, restoredCustomer.ExpectedRevision);
            Assert.Equal(
                "owner-a-customer",
                restoredCustomer.MutationId);
            Assert.Equal(
                mutationCreatedAtUtc,
                restoredCustomer.MutationCreatedAtUtc);
            Assert.Single(Directory.GetDirectories(
                Path.Combine(appDataRoot, "contract-cache")));
            AssertNoAtomicResidue(appDataRoot);
        }
        finally
        {
            lockedAtomicResidue?.Dispose();
            if (Directory.Exists(appDataRoot))
                Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PullAsync_CorruptContractManifestKeepsCursorUntilCanonicalIsRepaired()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-contract-cache-corrupt-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        try
        {
            const long initialRevision = 23;
            const long serverRevision = 101;
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var customerDirectory = Path.Combine(
                cacheRoot,
                customerId.ToString("N"));
            Directory.CreateDirectory(customerDirectory);
            var manifestPath = Path.Combine(
                customerDirectory,
                "contracts.json");
            var corruptBytes = System.Text.Encoding.UTF8.GetBytes(
                "{not-valid-json");
            await File.WriteAllBytesAsync(manifestPath, corruptBytes);

            var state = new MobileSyncState
            {
                LastRevision = initialRevision,
                LastSuccessUtc = DateTime.UtcNow.AddHours(-3),
                LastPulledCustomerCount = 6
            };
            state.Normalize();
            using var store = new JsonSyncStateStore(state);
            var api = new GeoraePlanApiClient(
                CreatePullResponse(serverRevision, "contract", contractId),
                CreatePullResponse(serverRevision, "contract", contractId));
            var cache = new CustomerContractCacheStore(cacheRoot);
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                cache,
                new SessionStore());

            var failed = await coordinator.PullAsync();

            Assert.Equal(initialRevision, failed.LastRevision);
            Assert.NotEmpty(failed.LastError);
            Assert.Equal(
                corruptBytes,
                await File.ReadAllBytesAsync(manifestPath));

            File.Delete(manifestPath);
            await cache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 1,
                    FileName = "repaired.pdf"
                }
            ]);

            var retried = await coordinator.PullAsync();

            Assert.Equal(
                new[] { initialRevision, initialRevision },
                api.RequestedPullRevisions);
            Assert.Equal(serverRevision, retried.LastRevision);
            Assert.Empty(retried.LastError);
            Assert.DoesNotContain(
                await cache.LoadContractsAsync(customerId),
                contract => contract.Id == contractId);
            AssertNoAtomicResidue(cacheRoot);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PullAsync_MergeFailureKeepsCursorAndPreviousSuccessMetadata()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-contract-cache-merge-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
        const long initialRevision = 17;
        var initialSuccess = DateTime.UtcNow.AddHours(-1);
        var duplicateCustomerId = Guid.NewGuid();
        var state = new MobileSyncState
        {
            LastRevision = initialRevision,
            LastSuccessUtc = initialSuccess,
            LastPulledCustomerCount = 5,
            SyncedCustomers =
            [
                new CustomerDto { Id = duplicateCustomerId },
                new CustomerDto { Id = duplicateCustomerId }
            ]
        };
        state.Normalize();

        using var store = new JsonSyncStateStore(state);
        var api = new GeoraePlanApiClient(new SyncPullResponse
        {
            CurrentServerRevision = 88
        });
        var coordinator = new SyncCoordinator(
            store,
            api,
            new PaymentAttachmentDraftStore(),
            new CustomerContractCacheStore(cacheRoot),
            new SessionStore());

        var failed = await coordinator.PullAsync();

        Assert.Equal(initialRevision, failed.LastRevision);
        Assert.Equal(initialSuccess, failed.LastSuccessUtc);
        Assert.Equal(5, failed.LastPulledCustomerCount);
        Assert.NotEmpty(failed.LastError);
        Assert.Equal(initialRevision, store.SavedStates.Single().LastRevision);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    private static SyncPullResponse CreatePullResponse(
        long serverRevision,
        string purgeKind,
        Guid entityId)
        => new()
        {
            CurrentServerRevision = serverRevision,
            PurgeRecords =
            [
                new RecycleBinPurgeRecordDto
                {
                    Kind = purgeKind,
                    EntityId = entityId,
                    Revision = serverRevision,
                    PurgedAtUtc = DateTime.UtcNow
                }
            ]
        };

    private static void AssertNoAtomicResidue(string root)
    {
        Assert.Empty(Directory.EnumerateFiles(
            root,
            "*.tmp",
            SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(
            root,
            "*.bak",
            SearchOption.AllDirectories));
        AssertNoOwnedAtomicResidue(root);
    }

    private static void AssertNoOwnedAtomicResidue(string root)
    {
        Assert.Empty(Directory.EnumerateFiles(
            root,
            "*.georaeplan-cache-owner-v1.*.owner.json",
            SearchOption.AllDirectories));
    }

    private static string ExtractMethod(string source, string signature)
    {
        var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Method signature not found: {signature}");

        var openBraceIndex = source.IndexOf('{', signatureIndex);
        Assert.True(openBraceIndex > signatureIndex, $"Method body not found: {signature}");

        var depth = 0;
        for (var index = openBraceIndex; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[signatureIndex..(index + 1)];
        }

        throw new InvalidOperationException($"Method closing brace not found: {signature}");
    }

    private static void AssertInOrder(string source, params string[] fragments)
    {
        var previous = -1;
        foreach (var fragment in fragments)
        {
            var current = source.IndexOf(
                fragment,
                previous + 1,
                StringComparison.Ordinal);
            Assert.True(
                current > previous,
                $"Expected fragment after index {previous}: {fragment}");
            previous = current;
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (current.GetFiles("*.sln").Length > 0)
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
