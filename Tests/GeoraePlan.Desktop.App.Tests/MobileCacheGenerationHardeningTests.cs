using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobileCacheGenerationHardeningTests
{
    [Fact]
    public void CacheMutationFamilies_AcquireOwnerCommitLeaseBeforeRootResolution()
    {
        var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "CustomerContractCacheStore.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var signature in new[]
                 {
                     "public async Task SaveCustomersAsync(\n        CacheOwnerSession ownerSession",
                     "public async Task SaveContractsAsync(\n        CacheOwnerSession ownerSession",
                     "public async Task RemoveCustomerAsync(\n        CacheOwnerSession ownerSession",
                     "public async Task RemoveContractAsync(\n        CacheOwnerSession ownerSession",
                     "public async Task<string?> EnsureCachedPdfAsync(\n        CacheOwnerSession ownerSession",
                     "public async Task<string> CachePdfAsync(\n        CacheOwnerSession ownerSession"
                 })
        {
            var method = ExtractMethod(source, signature);
            AssertInOrder(
                method,
                "AcquireOwnerCommitLeaseAsync(ownerSession, ct)",
                "ResolveRootDirectory(");
        }
    }

    [Fact]
    public void GenericHttp426_PreservesPaymentAttachmentForRetry()
    {
        var method = typeof(SyncCoordinator).GetMethod(
            "ShouldRetryPaymentAttachmentUpload",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "Payment attachment retry classifier was not found.");
        var shouldRetry = (bool)(method.Invoke(
            null,
            [
                new HttpRequestException(
                    "upgrade required",
                    inner: null,
                    statusCode: (System.Net.HttpStatusCode)426)
            ]) ?? false);

        Assert.True(shouldRetry);
    }

    [Fact]
    public async Task GenericHttp426_UploadAttemptKeepsPendingAttachmentAndDraftFile()
    {
        var cacheRoot = CreateTestRoot();
        var draftPath = Path.Combine(
            cacheRoot,
            "pending-attachment.pdf");
        try
        {
            await File.WriteAllBytesAsync(
                draftPath,
                [4, 2, 6]);
            var attachmentLocalId = Guid.NewGuid();
            var state = new GeoraePlan.Mobile.App.Models.MobileSyncState
            {
                PendingPaymentAttachments =
                [
                    new GeoraePlan.Mobile.App.Models.PendingPaymentAttachmentRecord
                    {
                        LocalId = attachmentLocalId,
                        PaymentId = Guid.NewGuid(),
                        FileName = "pending-attachment.pdf",
                        StoredPath = draftPath
                    }
                ]
            };
            state.Normalize();
            using var store = new JsonSyncStateStore(state);
            var api = new GeoraePlanApiClient
            {
                PaymentAttachmentUploadException =
                    new HttpRequestException(
                        "upgrade required",
                        inner: null,
                        statusCode:
                            (System.Net.HttpStatusCode)426)
            };
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                new CustomerContractCacheStore(cacheRoot),
                new SessionStore());

            var result =
                await coordinator.SynchronizeNowAsync();

            Assert.Equal(
                1,
                api.PaymentAttachmentUploadAttempts);
            Assert.Contains(
                result.PendingPaymentAttachments,
                attachment =>
                    attachment.LocalId == attachmentLocalId);
            Assert.True(File.Exists(draftPath));
            Assert.NotEmpty(result.LastError);
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task PullApply_DiscardsResponseWhenCapturedSessionIsStale()
    {
        var appDataRoot = CreateTestRoot();
        try
        {
            var session = CreateSession(
                "TENANT",
                "OFFICE",
                "Alice",
                "generation-a");
            var cache = new CustomerContractCacheStore(
                session,
                appDataRoot,
                beforeAtomicPublishAsync: null);
            var captured = cache.CaptureOwnerSession();
            var owner = session.CaptureOwner();
            var state = new GeoraePlan.Mobile.App.Models.MobileSyncState
            {
                LastRevision = 7,
                LastPulledCustomerCount = 3
            };
            state.Normalize();
            using var store = new JsonSyncStateStore(state);
            var coordinator = new SyncCoordinator(
                store,
                new GeoraePlanApiClient(),
                new PaymentAttachmentDraftStore(),
                cache,
                session);
            session.Snapshot = new SessionSnapshot
            {
                TenantCode = "TENANT",
                OfficeCode = "OFFICE",
                Username = "Alice",
                SessionGeneration = "generation-b"
            };
            var response = new SyncPullResponse
            {
                CurrentServerRevision = 99,
                Customers =
                [
                    Customer(Guid.NewGuid(), revision: 99)
                ]
            };
            var apply = typeof(SyncCoordinator).GetMethod(
                "ApplyPullResponseAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    "Pull apply method was not found.");
            var task = (Task)(apply.Invoke(
                coordinator,
                [owner, state, response, captured, CancellationToken.None])
                ?? throw new InvalidOperationException(
                    "Pull apply did not return a task."));

            await Assert.ThrowsAsync<StaleCacheOwnerSessionException>(
                async () => await task);
            Assert.Equal(7, state.LastRevision);
            Assert.Equal(3, state.LastPulledCustomerCount);
            Assert.Empty(state.SyncedCustomers);
        }
        finally
        {
            DeleteTestRoot(appDataRoot);
        }
    }

    [Fact]
    public async Task PullAsync_DiscardsPurgeWhenSessionChangesAtCacheCommit()
    {
        var appDataRoot = CreateTestRoot();
        try
        {
            var customerId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();
            var attachmentLocalId = Guid.NewGuid();
            var switchGenerationAtCommit = false;
            var session = CreateSession(
                "TENANT",
                "OFFICE",
                "Alice",
                "generation-a");
            var cache = new CustomerContractCacheStore(
                session,
                appDataRoot,
                (targetPath, _) =>
                {
                    if (switchGenerationAtCommit &&
                        string.Equals(
                            Path.GetFileName(targetPath),
                            ".purge-watermarks.json",
                            StringComparison.Ordinal))
                    {
                        session.Snapshot = new SessionSnapshot
                        {
                            TenantCode = "TENANT",
                            OfficeCode = "OFFICE",
                            Username = "Alice",
                            SessionGeneration = "generation-b"
                        };
                    }

                    return Task.CompletedTask;
                });
            var owner = cache.CaptureOwnerSession();
            await cache.SaveCustomersAsync(
                owner,
            [
                Customer(customerId, revision: 1)
            ]);
            var initialState = new GeoraePlan.Mobile.App.Models.MobileSyncState
            {
                LastRevision = 7,
                SyncedCustomers =
                [
                    Customer(customerId, revision: 1)
                ],
                PendingPaymentAttachments =
                [
                    new GeoraePlan.Mobile.App.Models.PendingPaymentAttachmentRecord
                    {
                        LocalId = attachmentLocalId,
                        PaymentId = paymentId
                    }
                ]
            };
            initialState.Normalize();
            using var store = new JsonSyncStateStore(initialState);
            var response = new SyncPullResponse
            {
                CurrentServerRevision = 9,
                PurgeRecords =
                [
                    new RecycleBinPurgeRecordDto
                    {
                        Kind = "payment",
                        EntityId = paymentId,
                        Revision = 9,
                        PurgedAtUtc = DateTime.UtcNow
                    },
                    new RecycleBinPurgeRecordDto
                    {
                        Kind = "customer",
                        EntityId = customerId,
                        Revision = 9,
                        PurgedAtUtc = DateTime.UtcNow
                    }
                ]
            };
            var coordinator = new SyncCoordinator(
                store,
                new GeoraePlanApiClient(response),
                new PaymentAttachmentDraftStore(),
                cache,
                session);

            switchGenerationAtCommit = true;
            await Assert.ThrowsAsync<StaleMobileSessionOwnerException>(
                () => coordinator.PullAsync());
            var result = await store.LoadAsync();

            Assert.Equal(7, result.LastRevision);
            Assert.Contains(
                result.SyncedCustomers,
                customer => customer.Id == customerId);
            Assert.Contains(
                result.PendingPaymentAttachments,
                attachment => attachment.LocalId == attachmentLocalId);
            Assert.Empty(result.LastError);
            Assert.Empty(store.SavedStates);
            Assert.False(File.Exists(Path.Combine(
                owner.RootDirectory,
                ".purge-watermarks.json")));
            var oldOwnerCache = new CustomerContractCacheStore(
                owner.RootDirectory);
            Assert.Contains(
                await oldOwnerCache.LoadCustomersAsync(),
                customer => customer.Id == customerId);
        }
        finally
        {
            DeleteTestRoot(appDataRoot);
        }
    }

    [Theory]
    [InlineData("TENANT", "A/B", "Alice", "TENANT", "A_B", "Alice")]
    [InlineData("TENANT", "OFFICE", "Alice", "TENANT", "OFFICE", "alice")]
    public async Task OwnerPath_UsesCollisionFreeExactIdentityAndValidatesOwnerManifest(
        string tenantA,
        string officeA,
        string usernameA,
        string tenantB,
        string officeB,
        string usernameB)
    {
        var appDataRoot = CreateTestRoot();
        try
        {
            var firstSession = CreateSession(
                tenantA,
                officeA,
                usernameA,
                "session-a");
            var secondSession = CreateSession(
                tenantB,
                officeB,
                usernameB,
                "session-b");
            var first = new CustomerContractCacheStore(
                firstSession,
                appDataRoot,
                beforeAtomicPublishAsync: null);
            var second = new CustomerContractCacheStore(
                secondSession,
                appDataRoot,
                beforeAtomicPublishAsync: null);

            await first.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = Guid.NewGuid(),
                    Revision = 1,
                    NameOriginal = "first"
                }
            ]);
            await second.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = Guid.NewGuid(),
                    Revision = 1,
                    NameOriginal = "second"
                }
            ]);

            var ownerRoots = Directory.GetDirectories(
                Path.Combine(appDataRoot, "contract-cache"));
            Assert.Equal(2, ownerRoots.Length);
            Assert.Equal(
                2,
                ownerRoots
                    .Select(Path.GetFileName)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            foreach (var ownerRoot in ownerRoots)
            {
                var manifestPath = Path.Combine(ownerRoot, ".owner.json");
                Assert.True(File.Exists(manifestPath));
                using var document = JsonDocument.Parse(
                    await File.ReadAllTextAsync(manifestPath));
                Assert.Equal(
                    "georaeplan-cache-scope-v2",
                    document.RootElement.GetProperty("schema").GetString());
                Assert.Equal(
                    Path.GetFileName(ownerRoot)["owner-".Length..],
                    document.RootElement.GetProperty("ownerHash").GetString());
            }

            var firstOwner = first.CaptureOwnerSession();
            var ownerManifestPath = Path.Combine(
                firstOwner.RootDirectory,
                ".owner.json");
            var ownerManifest = await File.ReadAllTextAsync(ownerManifestPath);
            await File.WriteAllTextAsync(
                ownerManifestPath,
                ownerManifest.Replace(
                    $"\"username\": \"{usernameA}\"",
                    "\"username\": \"tampered\"",
                    StringComparison.Ordinal));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => first.LoadCustomersAsync(firstOwner));
        }
        finally
        {
            DeleteTestRoot(appDataRoot);
        }
    }

    [Fact]
    public async Task CapturedOwnerSession_RejectsResponseAfterSessionGenerationChanges()
    {
        var appDataRoot = CreateTestRoot();
        try
        {
            var session = CreateSession(
                "TENANT",
                "OFFICE",
                "Alice",
                "generation-a");
            var cache = new CustomerContractCacheStore(
                session,
                appDataRoot,
                beforeAtomicPublishAsync: null);
            var owner = cache.CaptureOwnerSession();
            session.Snapshot = new SessionSnapshot
            {
                TenantCode = "TENANT",
                OfficeCode = "OFFICE",
                Username = "Alice",
                SessionGeneration = "generation-b"
            };

            await Assert.ThrowsAsync<StaleCacheOwnerSessionException>(
                () => cache.SaveCustomersAsync(
                    owner,
                    [
                        new CustomerDto
                        {
                            Id = Guid.NewGuid(),
                            Revision = 1,
                            NameOriginal = "stale"
                        }
                    ]));
            Assert.False(File.Exists(Path.Combine(
                owner.RootDirectory,
                "customers.json")));
        }
        finally
        {
            DeleteTestRoot(appDataRoot);
        }
    }

    [Fact]
    public async Task JsonPublish_HoldsOwnerCommitLeaseAcrossGenerationSwitch()
    {
        var appDataRoot = CreateTestRoot();
        var publishEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var session = CreateSession(
                "TENANT",
                "OFFICE",
                "Alice",
                "generation-a");
            var checkpoint = 0;
            var cache = new CustomerContractCacheStore(
                session,
                appDataRoot,
                async (targetPath, ct) =>
                {
                    if (!string.Equals(
                            Path.GetFileName(targetPath),
                            "customers.json",
                            StringComparison.Ordinal) ||
                        Interlocked.Increment(ref checkpoint) != 1)
                    {
                        return;
                    }

                    publishEntered.TrySetResult();
                    await releasePublish.Task.WaitAsync(ct);
                });
            var customerId = Guid.NewGuid();
            var ownerA = cache.CaptureOwnerSession();
            var saveA = cache.SaveCustomersAsync(
                ownerA,
                [Customer(customerId, revision: 1)]);
            await publishEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            var switchToB = session.ReplaceSnapshotAsync(
                CreateSession(
                    "TENANT",
                    "OFFICE",
                    "Alice",
                    "generation-b").Snapshot);
            Assert.NotSame(
                switchToB,
                await Task.WhenAny(
                    switchToB,
                    Task.Delay(TimeSpan.FromMilliseconds(100))));

            releasePublish.TrySetResult();
            await saveA;
            await switchToB;
            var ownerB = cache.CaptureOwnerSession();
            Assert.Equal(
                ownerA.RootDirectory,
                ownerB.RootDirectory,
                ignoreCase: true);
            await cache.SaveCustomersAsync(
                ownerB,
                [Customer(customerId, revision: 2)]);

            var saved = Assert.Single(
                await cache.LoadCustomersAsync(ownerB));
            Assert.Equal(2, saved.Revision);
        }
        finally
        {
            releasePublish.TrySetResult();
            DeleteTestRoot(appDataRoot);
        }
    }

    [Fact]
    public async Task PdfPublish_HoldsOwnerCommitLeaseAcrossGenerationSwitch()
    {
        var appDataRoot = CreateTestRoot();
        var pdfWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePdfWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var session = CreateSession(
                "TENANT",
                "OFFICE",
                "Alice",
                "generation-a");
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var bytesA = new byte[] { 1, 2, 3 };
            var sourceA = Path.Combine(appDataRoot, "source-a.pdf");
            await File.WriteAllBytesAsync(sourceA, bytesA);
            var contractA = new CustomerContractDto
            {
                Id = contractId,
                CustomerId = customerId,
                Revision = 1,
                FileName = "contract.pdf",
                FileSize = bytesA.Length,
                FileHash = Convert.ToHexString(
                    SHA256.HashData(bytesA))
            };
            var seed = new CustomerContractCacheStore(
                session,
                appDataRoot,
                beforeAtomicPublishAsync: null);
            var ownerA = seed.CaptureOwnerSession();
            await seed.SaveContractsAsync(
                ownerA,
                customerId,
                [contractA]);

            var checkpoint = 0;
            var cache = new CustomerContractCacheStore(
                session,
                appDataRoot,
                beforeAtomicPublishAsync: null,
                beforePdfWriteAsync: async (_, ct) =>
                {
                    if (Interlocked.Increment(ref checkpoint) != 1)
                        return;
                    pdfWriteEntered.TrySetResult();
                    await releasePdfWrite.Task.WaitAsync(ct);
                });
            var cacheA = cache.CachePdfAsync(
                ownerA,
                customerId,
                contractA,
                sourceA);
            await pdfWriteEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            var switchToB = session.ReplaceSnapshotAsync(
                CreateSession(
                    "TENANT",
                    "OFFICE",
                    "Alice",
                    "generation-b").Snapshot);
            Assert.NotSame(
                switchToB,
                await Task.WhenAny(
                    switchToB,
                    Task.Delay(TimeSpan.FromMilliseconds(100))));

            releasePdfWrite.TrySetResult();
            var publishedA = await cacheA;
            await switchToB;
            Assert.Equal(bytesA, await File.ReadAllBytesAsync(publishedA));

            var bytesB = new byte[] { 4, 5, 6 };
            var contractB = new CustomerContractDto
            {
                Id = contractId,
                CustomerId = customerId,
                Revision = 2,
                FileName = "contract.pdf",
                FileSize = bytesB.Length,
                FileHash = Convert.ToHexString(
                    SHA256.HashData(bytesB)),
                FileContent = bytesB
            };
            var ownerB = cache.CaptureOwnerSession();
            Assert.Equal(
                ownerA.RootDirectory,
                ownerB.RootDirectory,
                ignoreCase: true);
            await cache.SaveContractsAsync(
                ownerB,
                customerId,
                [contractB]);
            var publishedB = await cache.EnsureCachedPdfAsync(
                ownerB,
                customerId,
                contractB);
            Assert.NotNull(publishedB);
            Assert.Equal(
                bytesB,
                await File.ReadAllBytesAsync(publishedB!));
        }
        finally
        {
            releasePdfWrite.TrySetResult();
            DeleteTestRoot(appDataRoot);
        }
    }

    [Fact]
    public async Task DurablePurgeWatermark_PreventsStaleCustomerAndContractResurrection()
    {
        var cacheRoot = CreateTestRoot();
        try
        {
            var cache = new CustomerContractCacheStore(cacheRoot);
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            await cache.SaveCustomersAsync(
            [
                Customer(customerId, revision: 1)
            ]);
            await cache.SaveContractsAsync(
                customerId,
            [
                Contract(customerId, contractId, revision: 1, [1, 2, 3])
            ]);

            await cache.RemoveCustomerAsync(
                customerId,
                purgeRevision: 5);
            await cache.SaveCustomersAsync(
            [
                Customer(customerId, revision: 4)
            ]);
            await cache.SaveContractsAsync(
                customerId,
            [
                Contract(customerId, contractId, revision: 4, [4, 4, 4])
            ]);

            Assert.DoesNotContain(
                await cache.LoadCustomersAsync(),
                customer => customer.Id == customerId);
            Assert.Empty(await cache.LoadContractsAsync(customerId));
            Assert.True(File.Exists(Path.Combine(
                cacheRoot,
                ".purge-watermarks.json")));

            await cache.SaveCustomersAsync(
            [
                Customer(customerId, revision: 6)
            ]);
            await cache.SaveContractsAsync(
                customerId,
            [
                Contract(customerId, contractId, revision: 6, [6, 6, 6])
            ]);

            Assert.Contains(
                await cache.LoadCustomersAsync(),
                customer => customer.Id == customerId &&
                            customer.Revision == 6);
            Assert.Contains(
                await cache.LoadContractsAsync(customerId),
                contract => contract.Id == contractId &&
                            contract.Revision == 6);
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task DurablePurgeWatermark_HidesStaleArtifactsWhenPhysicalCleanupFails()
    {
        var cacheRoot = CreateTestRoot();
        var failCustomerManifestCleanup = false;
        try
        {
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var cache = new CustomerContractCacheStore(
                cacheRoot,
                (targetPath, _) =>
                {
                    if (failCustomerManifestCleanup &&
                        string.Equals(
                            Path.GetFileName(targetPath),
                            "customers.json",
                            StringComparison.Ordinal))
                    {
                        throw new IOException(
                            "injected physical purge cleanup failure");
                    }

                    return Task.CompletedTask;
                });
            await cache.SaveCustomersAsync(
            [
                Customer(customerId, revision: 1)
            ]);
            await cache.SaveContractsAsync(
                customerId,
            [
                Contract(
                    customerId,
                    contractId,
                    revision: 1,
                    [1, 2, 3, 4])
            ]);
            var contract = Assert.Single(
                await cache.LoadContractsAsync(customerId));
            var stalePdfPath = await cache.EnsureCachedPdfAsync(
                customerId,
                contract);
            Assert.NotNull(stalePdfPath);

            failCustomerManifestCleanup = true;
            await Assert.ThrowsAsync<IOException>(
                () => cache.RemoveCustomerAsync(
                    customerId,
                    purgeRevision: 5));

            Assert.True(File.Exists(Path.Combine(
                cacheRoot,
                "customers.json")));
            Assert.True(File.Exists(Path.Combine(
                cacheRoot,
                customerId.ToString("N"),
                "contracts.json")));
            Assert.True(File.Exists(stalePdfPath));
            Assert.DoesNotContain(
                await cache.LoadCustomersAsync(),
                customer => customer.Id == customerId);
            Assert.Empty(await cache.LoadContractsAsync(customerId));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => cache.EnsureCachedPdfAsync(
                    customerId,
                    contract));
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task SaveContracts_PublishesPdfSetOnlyWithOneManifestCommit()
    {
        var cacheRoot = CreateTestRoot();
        var failCommit = false;
        try
        {
            var customerId = Guid.NewGuid();
            var firstContractId = Guid.NewGuid();
            var secondContractId = Guid.NewGuid();
            var cache = new CustomerContractCacheStore(
                cacheRoot,
                (targetPath, _) =>
                {
                    if (failCommit &&
                        string.Equals(
                            Path.GetFileName(targetPath),
                            "contracts.json",
                            StringComparison.Ordinal))
                    {
                        throw new IOException("injected manifest commit failure");
                    }

                    return Task.CompletedTask;
                });
            await cache.SaveContractsAsync(
                customerId,
            [
                Contract(
                    customerId,
                    firstContractId,
                    revision: 1,
                    [1, 1, 1]),
                Contract(
                    customerId,
                    secondContractId,
                    revision: 1,
                    [2, 2, 2])
            ]);
            var currentContracts =
                await cache.LoadContractsAsync(customerId);
            var oldFirstPath = await cache.EnsureCachedPdfAsync(
                customerId,
                currentContracts.Single(contract =>
                    contract.Id == firstContractId));
            var oldSecondPath = await cache.EnsureCachedPdfAsync(
                customerId,
                currentContracts.Single(contract =>
                    contract.Id == secondContractId));

            failCommit = true;
            await Assert.ThrowsAsync<IOException>(
                () => cache.SaveContractsAsync(
                    customerId,
                    [
                        Contract(
                            customerId,
                            firstContractId,
                            revision: 2,
                            [7, 7, 7]),
                        Contract(
                            customerId,
                            secondContractId,
                            revision: 2,
                            [8, 8, 8])
                    ]));

            Assert.All(
                await cache.LoadContractsAsync(customerId),
                contract => Assert.Equal(1, contract.Revision));
            Assert.Equal(
                new byte[] { 1, 1, 1 },
                await File.ReadAllBytesAsync(oldFirstPath!));
            Assert.Equal(
                new byte[] { 2, 2, 2 },
                await File.ReadAllBytesAsync(oldSecondPath!));
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task CachePdf_BindsBytesToExactCurrentManifestRevisionHashAndSize()
    {
        var cacheRoot = CreateTestRoot();
        var sourcePath = Path.Combine(cacheRoot, "download.pdf");
        try
        {
            var cache = new CustomerContractCacheStore(cacheRoot);
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var bytes = new byte[] { 9, 8, 7, 6 };
            var hash = Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant();
            await cache.SaveContractsAsync(
                customerId,
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 2,
                    FileName = "current.pdf",
                    FileSize = bytes.LongLength,
                    FileHash = hash
                }
            ]);
            await File.WriteAllBytesAsync(sourcePath, bytes);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => cache.CachePdfAsync(
                    customerId,
                    new CustomerContractDto
                    {
                        Id = contractId,
                        CustomerId = customerId,
                        Revision = 1,
                        FileName = "stale.pdf",
                        FileSize = bytes.LongLength,
                        FileHash = hash
                    },
                    sourcePath));

            var current = await cache.CachePdfAsync(
                customerId,
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customerId,
                    Revision = 2,
                    FileName = "current.pdf",
                    FileSize = bytes.LongLength,
                    FileHash = hash
                },
                sourcePath);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(current));
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task PartialLeaseBinding_IsQuarantinedAndReinitialized()
    {
        var cacheRoot = CreateTestRoot();
        try
        {
            var cache = new CustomerContractCacheStore(cacheRoot);
            await cache.SaveCustomersAsync(Array.Empty<CustomerDto>());
            var lockPath = Directory.GetFiles(
                Path.Combine(cacheRoot, ".manifest-locks"),
                "*.lock",
                SearchOption.TopDirectoryOnly)
                .Single(path =>
                    Encoding.UTF8.GetString(
                            File.ReadAllBytes(path))
                        .EndsWith(
                            Path.GetFullPath(Path.Combine(
                                cacheRoot,
                                "customers.json"))
                            .ToUpperInvariant(),
                            StringComparison.Ordinal));
            var completeBinding = await File.ReadAllBytesAsync(lockPath);
            await File.WriteAllBytesAsync(
                lockPath,
                completeBinding[..Math.Max(1, completeBinding.Length / 2)]);

            await cache.SaveCustomersAsync(
            [
                Customer(Guid.NewGuid(), revision: 1)
            ]);

            Assert.Equal(
                completeBinding,
                await File.ReadAllBytesAsync(lockPath));
            Assert.Single(Directory.GetFiles(
                Path.Combine(cacheRoot, ".manifest-locks", ".quarantine"),
                "*.partial",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task TruncatedAtomicOwnerMarker_IsQuarantinedAndCacheReinitializes()
    {
        var cacheRoot = CreateTestRoot();
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
            var backupPath = Path.Combine(
                customerDirectory,
                $"{ownedPrefix}.bak");
            await File.WriteAllTextAsync(markerPath, "{");
            await File.WriteAllTextAsync(
                temporaryPath,
                "crash-temporary");
            await File.WriteAllTextAsync(
                backupPath,
                "crash-backup");

            await cache.SaveContractsAsync(
                customerId,
                Array.Empty<CustomerContractDto>());

            Assert.False(File.Exists(markerPath));
            Assert.False(File.Exists(temporaryPath));
            Assert.False(File.Exists(backupPath));
            Assert.Equal(
                3,
                Directory.GetFiles(
                    Path.Combine(
                        customerDirectory,
                        ".atomic-quarantine"),
                    "*.quarantined",
                    SearchOption.TopDirectoryOnly).Length);
            Assert.Empty(
                await cache.LoadContractsAsync(customerId));
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task RemoveCustomerFromIndex_IsOneLockedReadModifyWrite()
    {
        var cacheRoot = CreateTestRoot();
        try
        {
            var cache = new CustomerContractCacheStore(cacheRoot);
            var removedId = Guid.NewGuid();
            var retainedId = Guid.NewGuid();
            await cache.SaveCustomersAsync(
            [
                Customer(removedId, revision: 1),
                Customer(retainedId, revision: 1)
            ]);

            await cache.RemoveCustomerFromIndexAsync(removedId);

            var customers = await cache.LoadCustomersAsync();
            Assert.DoesNotContain(
                customers,
                customer => customer.Id == removedId);
            Assert.Contains(
                customers,
                customer => customer.Id == retainedId);
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task InProcessManifestLocks_AreRetiredAfterHighCardinalityUse()
    {
        var cacheRoot = CreateTestRoot();
        try
        {
            var cache = new CustomerContractCacheStore(cacheRoot);
            for (var index = 0; index < 64; index++)
            {
                await cache.SaveContractsAsync(
                    Guid.NewGuid(),
                    Array.Empty<CustomerContractDto>());
            }

            Assert.Equal(
                0,
                CustomerContractCacheStore
                    .CountInProcessManifestLocksForRoot(
                        cacheRoot));
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task OwnerRootJunction_IsRejected()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var appDataRoot = CreateTestRoot();
        var externalRoot = CreateTestRoot();
        try
        {
            var session = CreateSession(
                "TENANT",
                "OFFICE",
                "Alice",
                "generation-a");
            var cache = new CustomerContractCacheStore(
                session,
                appDataRoot,
                beforeAtomicPublishAsync: null);
            var owner = cache.CaptureOwnerSession();
            Directory.CreateDirectory(Path.GetDirectoryName(
                owner.RootDirectory)!);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments =
                    $"/d /c mklink /J \"{owner.RootDirectory}\" \"{externalRoot}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                return;

            await Assert.ThrowsAnyAsync<IOException>(
                () => cache.SaveCustomersAsync(
                    owner,
                    Array.Empty<CustomerDto>()));
            Assert.False(File.Exists(Path.Combine(
                externalRoot,
                "customers.json")));
        }
        finally
        {
            DeleteTestRoot(appDataRoot);
            DeleteTestRoot(externalRoot);
        }
    }

    private static string ExtractMethod(
        string source,
        string signature)
    {
        var start = source.IndexOf(
            signature,
            StringComparison.Ordinal);
        Assert.True(start >= 0);
        var bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart >= 0);
        var depth = 0;
        for (var index = bodyStart;
             index < source.Length;
             index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' &&
                     --depth == 0)
            {
                return source.Substring(
                    start,
                    index - start + 1);
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Method closing brace not found: {signature}");
    }

    private static void AssertInOrder(
        string source,
        params string[] values)
    {
        var position = -1;
        foreach (var value in values)
        {
            var next = source.IndexOf(
                value,
                position + 1,
                StringComparison.Ordinal);
            Assert.True(
                next > position,
                $"Expected '{value}' after offset {position}.");
            position = next;
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(
            AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(
                    current.FullName,
                    "Mobile",
                    "GeoraePlan.Mobile.App")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Repository root not found.");
    }

    private static SessionStore CreateSession(
        string tenant,
        string office,
        string username,
        string generation)
        => new()
        {
            Snapshot = new SessionSnapshot
            {
                TenantCode = tenant,
                OfficeCode = office,
                Username = username,
                SessionGeneration = generation
            }
        };

    private static CustomerDto Customer(Guid customerId, long revision)
        => new()
        {
            Id = customerId,
            Revision = revision,
            NameOriginal = $"customer-{revision}"
        };

    private static CustomerContractDto Contract(
        Guid customerId,
        Guid contractId,
        long revision,
        byte[]? fileContent)
    {
        var content = fileContent ?? Array.Empty<byte>();
        return new CustomerContractDto
        {
            Id = contractId,
            CustomerId = customerId,
            Revision = revision,
            FileName = $"contract-{revision}.pdf",
            FileSize = content.LongLength,
            FileHash = content.Length == 0
                ? string.Empty
                : Convert.ToHexString(SHA256.HashData(content))
                    .ToLowerInvariant(),
            FileContent = content
        };
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-mobile-cache-generation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var directory in Directory.EnumerateDirectories(
                     root,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderByDescending(path => path.Length))
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(directory);
        }

        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
