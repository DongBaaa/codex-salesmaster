using System.Text.RegularExpressions;
using System.Security.Cryptography;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobileMutationOwnerBoundaryTests
{
    [Fact]
    public async Task PaymentAttachmentUploadIntegrity_TamperedSameSizeFileFailsClosed()
    {
        var root = CreateTestRoot();
        var path = Path.Combine(root, "attachment.bin");
        var original = new byte[] { 1, 2, 3, 4, 5, 6 };
        await File.WriteAllBytesAsync(path, original);
        var attachment = new PendingPaymentAttachmentRecord
        {
            FileName = "attachment.bin",
            StoredPath = path,
            FileSize = original.Length,
            FileHash = Convert.ToHexString(
                SHA256.HashData(original))
        };
        await File.WriteAllBytesAsync(
            path,
            new byte[] { 6, 5, 4, 3, 2, 1 });

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => PaymentAttachmentUploadIntegrity
                    .ValidateAndRewindAsync(
                        stream,
                        attachment,
                        CancellationToken.None));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task OwnerBoundFileMutation_GenerationSwitchWaitsForPublishAndStaleDeleteIsSkipped()
    {
        var root = CreateTestRoot();
        var temporaryPath = Path.Combine(root, "draft.tmp");
        var targetPath = Path.Combine(root, "draft.bin");
        await File.WriteAllBytesAsync(
            temporaryPath,
            [1, 2, 3]);
        var ownerGate = new SemaphoreSlim(1, 1);
        var generation = "generation-a";
        var leaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IDisposable> AcquireAAsync(
            CancellationToken ct)
        {
            await ownerGate.WaitAsync(ct);
            if (!string.Equals(
                    generation,
                    "generation-a",
                    StringComparison.Ordinal))
            {
                ownerGate.Release();
                throw new InvalidOperationException(
                    "stale owner");
            }

            leaseEntered.TrySetResult();
            await releaseLease.Task.WaitAsync(ct);
            return new ActionDisposable(
                () => ownerGate.Release());
        }

        void ValidateA()
        {
            if (!string.Equals(
                    generation,
                    "generation-a",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "stale owner");
            }
        }

        try
        {
            var publish = OwnerBoundFileMutation.PublishAsync(
                temporaryPath,
                targetPath,
                overwrite: false,
                AcquireAAsync,
                ValidateA,
                CancellationToken.None);
            await leaseEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            var switchOwner = Task.Run(
                async () =>
                {
                    await ownerGate.WaitAsync();
                    try
                    {
                        generation = "generation-b";
                    }
                    finally
                    {
                        ownerGate.Release();
                    }
                });
            Assert.NotSame(
                switchOwner,
                await Task.WhenAny(
                    switchOwner,
                    Task.Delay(
                        TimeSpan.FromMilliseconds(100))));

            releaseLease.TrySetResult();
            await publish;
            await switchOwner;
            Assert.True(File.Exists(targetPath));
            Assert.Equal(
                new byte[] { 1, 2, 3 },
                await File.ReadAllBytesAsync(targetPath));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => OwnerBoundFileMutation.DeleteIfExistsAsync(
                    targetPath,
                    AcquireAAsync,
                    ValidateA,
                    CancellationToken.None));
            Assert.True(File.Exists(targetPath));
        }
        finally
        {
            releaseLease.TrySetResult();
            ownerGate.Dispose();
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void PaymentAttachmentUploadRequest_ReopensAndRevalidatesEveryRetryUsingOneLockedHandle()
    {
        var source = ReadMobileSource(
            "Services",
            "GeoraePlanApiClient.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var upload = ExtractMethod(
            source,
            "public async Task<PaymentAttachmentDto?> UploadPaymentAttachmentAsync(\n        Guid paymentId,\n        PendingPaymentAttachmentRecord attachment,\n        MobileSessionOwner expectedOwner");
        var factory = ExtractMethod(
            source,
            "private async Task<HttpRequestMessage> CreatePaymentAttachmentUploadRequestAsync(");

        Assert.Contains(
            "owner => CreatePaymentAttachmentUploadRequestAsync(",
            upload,
            StringComparison.Ordinal);
        AssertInOrder(
            factory,
            "new FileStream(",
            "FileShare.Read",
            "PaymentAttachmentUploadIntegrity.ValidateAndRewindAsync(",
            "new StreamContent(fileStream)",
            "request.Content = form");
        Assert.DoesNotContain(
            "File.ReadAllBytes",
            factory,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PaymentDraft_SaveCapturesOneOwnerSnapshotAndGatesEveryLateUiCommit()
    {
        var source = ReadMobileSource(
                "ViewModels",
                "PaymentDraftViewModel.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var save = ExtractMethod(
            source,
            "public async Task SaveDraftAsync()");
        var refresh = ExtractMethod(
            source,
            "private async Task<InvoiceDto?> RefreshSelectedInvoiceForSaveAsync(");

        AssertInOrder(
            save,
            "var sessionSnapshot = _sessionStore.GetSnapshot();",
            "var owner = MobileSessionOwner.Capture(sessionSnapshot);",
            "_ownerOperations.TryBeginAsync(",
            "() => IsBusy = true",
            "CloneInvoiceForPaymentDraft(",
            ".Select(ClonePendingPaymentAttachment)",
            "RefreshSelectedInvoiceForSaveAsync(",
            "selectedInvoice,",
            "owner,",
            "sessionSnapshot",
            "SavePaymentWithOutcomeImmediatelyAsync(",
            "payment,",
            "owner,",
            "attachments,",
            "linkedTransaction",
            "_sessionStore.ThrowIfOwnerChanged(owner)",
            "_ownerOperations.TryCommitAsync(",
            "_ownerOperations.CreateCallbackContext(",
            "_ownerOperations.TryStartCallbackAsync(",
            "SavedSuccessfully?.Invoke(");
        Assert.DoesNotContain(
            "var savedSuccessfully = SavedSuccessfully;",
            save,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (StaleMobileSessionOwnerException)",
            save,
            StringComparison.Ordinal);
        AssertInOrder(
            save,
            "_ownerOperations.CompleteAsync(",
            "operation,",
            "IsBusy = false");
        Assert.DoesNotContain(
            "foreach (var attachment in Attachments)",
            save,
            StringComparison.Ordinal);
        AssertInOrder(
            refresh,
            "GetInvoiceByIdAsync(",
            "invoice.Id,",
            "owner",
            "_syncCoordinator.LoadAsync(owner)",
            "_sessionStore.ThrowIfOwnerChanged(owner)");
        Assert.DoesNotContain(
            "ReplaceInvoiceSnapshot(",
            refresh,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SyncLoadAndPaymentSave_CheckCapturedOwnerImmediatelyAfterSyncLock()
    {
        var source = ReadMobileSource(
                "Services",
                "SyncCoordinator.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var load = ExtractMethod(
            source,
            "public async Task<MobileSyncState> LoadAsync(\n        MobileSessionOwner owner");
        var save = ExtractMethod(
            source,
            "public async Task<MobileSyncState> SavePaymentImmediatelyAsync(\n        PaymentDto payment,\n        MobileSessionOwner owner");

        AssertInOrder(
            load,
            "await _syncLock.WaitAsync(ct)",
            "_sessionStore.ThrowIfOwnerChanged(owner)",
            "_store.LoadAsync(owner, ct)",
            "_sessionStore.ThrowIfOwnerChanged(owner)",
            "_attachmentStore.PrepareOwnedDraftsAsync(",
            "_sessionStore.ThrowIfOwnerChanged(owner)",
            "_store.SaveAsync(",
            "_sessionStore.ThrowIfOwnerChanged(owner)",
            "CleanupOrphanPaymentAttachmentDraftsAsync(",
            "_sessionStore.ThrowIfOwnerChanged(owner)",
            "return state");
        AssertInOrder(
            save,
            "await _syncLock.WaitAsync(ct)",
            "_sessionStore.ThrowIfOwnerChanged(owner)",
            "_store.LoadAsync(owner, ct)");
    }

    [Fact]
    public void PaymentAttachmentDraftStore_FinalPublishesAndDeletesRequireOwnerCommitLease()
    {
        var source = ReadMobileSource(
                "Services",
                "PaymentAttachmentDraftStore.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var save = ExtractMethod(
            source,
            "private async Task<PendingPaymentAttachmentRecord> SaveStreamAsync(");
        var remove = ExtractMethod(
            source,
            "public async Task RemoveAsync(");
        var orphan = ExtractMethod(
            source,
            "public async Task<int> RemoveOrphanDraftsAsync(");
        var resolve = ExtractMethod(
            source,
            "public async Task<string?> ResolveOwnedPathAsync(");

        AssertInOrder(
            save,
            "temporaryPath",
            "SHA256.HashDataAsync(verify, ct)",
            "OwnerBoundFileMutation.PublishAsync(",
            "temporaryPath,",
            "storedPath",
            "AcquireOwnerCommitLeaseAsync(");
        AssertInOrder(
            remove,
            "ResolveOwnedPathAsync(",
            "OwnerBoundFileMutation.DeleteIfExistsAsync(",
            "AcquireOwnerCommitLeaseAsync(");
        AssertInOrder(
            orphan,
            "OwnerBoundFileMutation.DeleteIfExistsAsync(",
            "AcquireOwnerCommitLeaseAsync(");
        AssertInOrder(
            resolve,
            "AcquireOwnerCommitLeaseAsync(owner, ct)",
            "MigrateLegacyDraftAsync(",
            "attachment.StoredPath = destinationPath");
    }

    [Fact]
    public async Task SavePaymentImmediately_OwnerSwitchDuringApiWait_NeverCommitsIntoReplacementOwner()
    {
        var aliceSnapshot = Snapshot("alice", "generation-a");
        var session = new SessionStore { Snapshot = aliceSnapshot };
        using var store = new JsonSyncStateStore(
            session,
            StateFor(aliceSnapshot, revision: 7));
        var apiEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApi = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new GeoraePlanApiClient
        {
            BeforePaymentReturnAsync = async () =>
            {
                apiEntered.TrySetResult();
                await releaseApi.Task;
            }
        };
        var cacheRoot = CreateTestRoot();
        try
        {
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);
            var payment = new PaymentDto
            {
                Id = Guid.NewGuid(),
                InvoiceId = Guid.NewGuid()
            };

            var saveTask =
                coordinator.SavePaymentImmediatelyAsync(payment);
            await apiEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(3));
            session.Snapshot = Snapshot(
                "bob",
                "generation-b");
            releaseApi.TrySetResult();

            await Assert.ThrowsAsync<
                StaleMobileSessionOwnerException>(
                () => saveTask);
            var bob = await store.LoadForOwnerAsync(
                MobileSessionOwner.Capture(session.Snapshot));
            Assert.Empty(bob.PendingPush.Payments);
            var submittedOwner = Assert.Single(
                api.SubmittedPaymentOwners);
            Assert.Equal("alice", submittedOwner.Username);
            Assert.Equal(
                "generation-a",
                submittedOwner.SessionGeneration);
        }
        finally
        {
            releaseApi.TrySetResult();
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task PaymentWriteAhead_AcceptedThenAttachment401_RecoversForSameLogicalOwnerOnly()
    {
        var aliceSnapshot = Snapshot("alice", "generation-a");
        var session = new SessionStore { Snapshot = aliceSnapshot };
        using var store = new JsonSyncStateStore(
            session,
            StateFor(aliceSnapshot, revision: 7));
        var cacheRoot = CreateTestRoot();
        var draftPath = Path.Combine(cacheRoot, "evidence.pdf");
        await File.WriteAllBytesAsync(draftPath, [1, 2, 3]);
        var savedCountAtPaymentSend = -1;
        MobileSyncState? acceptedStateAtUpload = null;
        var api = new GeoraePlanApiClient
        {
            BeforePaymentReturnAsync = () =>
            {
                savedCountAtPaymentSend = store.SavedStates.Count;
                return Task.CompletedTask;
            },
            BeforePaymentAttachmentUploadAsync = (_, _) =>
            {
                acceptedStateAtUpload =
                    store.SavedStates.Last();
                session.Snapshot = new SessionSnapshot
                {
                    IsAuthenticated = false,
                    Username = string.Empty,
                    TenantCode = string.Empty,
                    OfficeCode = string.Empty,
                    SessionGeneration = "signed-out"
                };
                throw new MobileAuthenticationException();
            }
        };
        try
        {
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);
            var payment = new PaymentDto
            {
                Id = Guid.NewGuid(),
                InvoiceId = Guid.NewGuid(),
                MutationId = "payment-wal-test"
            };
            var attachment = new PendingPaymentAttachmentRecord
            {
                LocalId = Guid.NewGuid(),
                FileName = "evidence.pdf",
                StoredPath = draftPath,
                FileSize = 3,
                FileHash = Convert.ToHexString(
                    SHA256.HashData([1, 2, 3]))
            };

            await Assert.ThrowsAsync<
                StaleMobileSessionOwnerException>(
                () => coordinator.SavePaymentImmediatelyAsync(
                    payment,
                    [attachment]));

            Assert.True(savedCountAtPaymentSend >= 1);
            var writeAhead = store.SavedStates[0];
            Assert.Contains(
                writeAhead.PendingPush.Payments,
                pending =>
                    pending.Id == payment.Id &&
                    pending.MutationId == payment.MutationId);
            Assert.Contains(
                writeAhead.PendingPaymentAttachments,
                pending =>
                    pending.LocalId == attachment.LocalId &&
                    pending.PaymentId == payment.Id);
            Assert.NotNull(acceptedStateAtUpload);
            Assert.DoesNotContain(
                acceptedStateAtUpload!.PendingPush.Payments,
                pending => pending.Id == payment.Id);
            Assert.Contains(
                acceptedStateAtUpload.SyncedPayments,
                synced =>
                    synced.Id == payment.Id &&
                    synced.MutationId == payment.MutationId);
            Assert.Contains(
                acceptedStateAtUpload.PendingPaymentAttachments,
                pending => pending.LocalId == attachment.LocalId);

            session.Snapshot = Snapshot(
                "alice",
                "generation-b");
            var aliceRecovery = await store.LoadForOwnerAsync(
                session.CaptureOwner());
            Assert.DoesNotContain(
                aliceRecovery.PendingPush.Payments,
                pending => pending.Id == payment.Id);
            Assert.Contains(
                aliceRecovery.PendingPaymentAttachments,
                pending => pending.LocalId == attachment.LocalId);

            var bobSnapshot = Snapshot(
                "bob",
                "generation-b");
            var bob = await store.LoadForOwnerAsync(
                MobileSessionOwner.Capture(bobSnapshot));
            Assert.Empty(bob.PendingPaymentAttachments);
            Assert.Empty(bob.PendingPush.Payments);

            api.BeforePaymentAttachmentUploadAsync = null;
            var recovered =
                await coordinator.SynchronizeNowAsync();
            Assert.DoesNotContain(
                recovered.PendingPaymentAttachments,
                pending => pending.LocalId == attachment.LocalId);
            Assert.Contains(
                api.SubmittedPaymentAttachmentOwners,
                submitted =>
                    submitted.LocalId == attachment.LocalId &&
                    submitted.Owner.Matches(session.Snapshot));
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task PaymentWriteAhead_AcceptedThenPullFailureNeverRequeuesPaymentOrUploadedAttachment()
    {
        var snapshot = Snapshot(
            "alice",
            "generation-a");
        var session = new SessionStore
        {
            Snapshot = snapshot
        };
        using var store = new JsonSyncStateStore(
            session,
            StateFor(snapshot, revision: 7));
        var cacheRoot = CreateTestRoot();
        var firstPath = Path.Combine(
            cacheRoot,
            "uploaded-first.pdf");
        var secondPath = Path.Combine(
            cacheRoot,
            "retry-second.pdf");
        await File.WriteAllBytesAsync(
            firstPath,
            [1, 2, 3]);
        await File.WriteAllBytesAsync(
            secondPath,
            [4, 5, 6]);
        var firstAttachment =
            new PendingPaymentAttachmentRecord
            {
                LocalId = Guid.NewGuid(),
                FileName = "uploaded-first.pdf",
                StoredPath = firstPath,
                FileSize = 3,
                FileHash = Convert.ToHexString(
                    SHA256.HashData([1, 2, 3]))
            };
        var secondAttachment =
            new PendingPaymentAttachmentRecord
            {
                LocalId = Guid.NewGuid(),
                FileName = "retry-second.pdf",
                StoredPath = secondPath,
                FileSize = 3,
                FileHash = Convert.ToHexString(
                    SHA256.HashData([4, 5, 6]))
            };
        var api = new GeoraePlanApiClient
        {
            BeforePaymentAttachmentUploadAsync =
                (_, attachment) =>
                    attachment.LocalId ==
                    secondAttachment.LocalId
                        ? throw new MobileAuthenticationException()
                        : Task.CompletedTask,
            BeforePullReturnAsync = () =>
                throw new HttpRequestException(
                    "retryable pull failure")
        };
        try
        {
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);
            var payment = new PaymentDto
            {
                Id = Guid.NewGuid(),
                InvoiceId = Guid.NewGuid(),
                MutationId =
                    "accepted-pull-failure"
            };

            var failedPull =
                await coordinator.SavePaymentImmediatelyAsync(
                    payment,
                    [
                        firstAttachment,
                        secondAttachment
                    ]);

            Assert.Single(api.SubmittedPaymentOwners);
            Assert.DoesNotContain(
                failedPull.PendingPush.Payments,
                pending => pending.Id == payment.Id);
            Assert.DoesNotContain(
                failedPull.PendingPaymentAttachments,
                pending =>
                    pending.LocalId ==
                    firstAttachment.LocalId);
            Assert.Contains(
                failedPull.PendingPaymentAttachments,
                pending =>
                    pending.LocalId ==
                    secondAttachment.LocalId);

            api.BeforePaymentAttachmentUploadAsync =
                null;
            api.BeforePullReturnAsync = null;
            var recovered =
                await coordinator.SynchronizeNowAsync();

            Assert.Single(api.SubmittedPaymentOwners);
            Assert.DoesNotContain(
                recovered.PendingPush.Payments,
                pending => pending.Id == payment.Id);
            Assert.Empty(
                recovered.PendingPaymentAttachments);
            Assert.Equal(
                2,
                api.PaymentAttachmentUploadAttempts);
            Assert.Equal(
                1,
                api.SubmittedPaymentAttachmentOwners.Count(
                    submitted =>
                        submitted.LocalId ==
                        firstAttachment.LocalId));
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task PaymentAttachment_PostUploadSaveFailureRecoversWithoutReuploadOrPrematureDraftDelete()
    {
        var snapshot = Snapshot(
            "alice",
            "generation-a");
        var session = new SessionStore
        {
            Snapshot = snapshot
        };
        var saveAttempt = 0;
        using var store = new JsonSyncStateStore(
            session,
            StateFor(snapshot, revision: 7))
        {
            BeforeOwnerSaveAsync = (_, _) =>
            {
                saveAttempt++;
                return saveAttempt == 3
                    ? Task.FromException(
                        new IOException(
                            "post-acceptance save failure"))
                    : Task.CompletedTask;
            }
        };
        var cacheRoot = CreateTestRoot();
        var draftPath = Path.Combine(
            cacheRoot,
            "accepted-persistence.pdf");
        await File.WriteAllBytesAsync(
            draftPath,
            [8, 8, 8]);
        var api = new GeoraePlanApiClient();
        var attachmentStore =
            new PaymentAttachmentDraftStore();
        try
        {
            var coordinator = new SyncCoordinator(
                store,
                api,
                attachmentStore,
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);
            var payment = new PaymentDto
            {
                Id = Guid.NewGuid(),
                InvoiceId = Guid.NewGuid(),
                MutationId =
                    "accepted-persistence-failure"
            };
            var attachment =
                new PendingPaymentAttachmentRecord
                {
                    LocalId = Guid.NewGuid(),
                    FileName =
                        "accepted-persistence.pdf",
                    StoredPath = draftPath,
                    FileSize = 3,
                    FileHash = Convert.ToHexString(
                        SHA256.HashData([8, 8, 8]))
                };

            var result =
                await coordinator.SavePaymentImmediatelyAsync(
                    payment,
                    [attachment]);

            Assert.Single(api.SubmittedPaymentOwners);
            Assert.DoesNotContain(
                result.PendingPush.Payments,
                pending => pending.Id == payment.Id);
            var pendingCommit = Assert.Single(
                result.PendingPaymentAttachments,
                pending =>
                    pending.LocalId ==
                    attachment.LocalId);
            Assert.NotNull(
                pendingCommit.ServerUploadAcceptedAtUtc);
            Assert.Empty(
                attachmentStore.RemovedDrafts);

            await coordinator.SynchronizeNowAsync();
            Assert.Single(api.SubmittedPaymentOwners);
            Assert.Equal(
                1,
                api.PaymentAttachmentUploadAttempts);
            Assert.Contains(
                attachmentStore.RemovedDrafts,
                removed =>
                    removed.LocalId ==
                    attachment.LocalId);
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task BackgroundPaymentAttachment_AckOrFinalSaveFailure_RestartFinalizesWithoutReupload(
        int failingSaveAttempt)
    {
        var snapshot = new SessionSnapshot
        {
            IsAuthenticated = true,
            Username = "alice",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            SessionGeneration = "generation-a"
        };
        var session = new SessionStore
        {
            Snapshot = snapshot
        };
        var owner = MobileSessionOwner.Capture(snapshot);
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var initial = StateFor(
            snapshot,
            revision: 7);
        initial.SyncedInvoices.Add(
            new InvoiceDto
            {
                Id = invoiceId,
                TenantCode = snapshot.TenantCode,
                OfficeCode = snapshot.OfficeCode,
                ResponsibleOfficeCode =
                    snapshot.OfficeCode
            });
        initial.SyncedPayments.Add(
            new PaymentDto
            {
                Id = paymentId,
                InvoiceId = invoiceId
            });
        var cacheRoot = CreateTestRoot();
        var draftPath = Path.Combine(
            cacheRoot,
            "background-accepted.pdf");
        await File.WriteAllBytesAsync(
            draftPath,
            [4, 4, 4]);
        var attachment =
            new PendingPaymentAttachmentRecord
            {
                LocalId = Guid.NewGuid(),
                PaymentId = paymentId,
                FileName = "background-accepted.pdf",
                StoredPath = draftPath,
                FileSize = 3,
                FileHash = Convert.ToHexString(
                    SHA256.HashData([4, 4, 4]))
            };
        initial.PendingPaymentAttachments.Add(
            attachment);
        var saveAttempt = 0;
        using var store = new JsonSyncStateStore(
            session,
            initial)
        {
            BeforeOwnerSaveAsync = (_, _) =>
            {
                saveAttempt++;
                return saveAttempt ==
                       failingSaveAttempt
                    ? Task.FromException(
                        new IOException(
                            "injected background save failure"))
                    : Task.CompletedTask;
            }
        };
        var api = new GeoraePlanApiClient();
        var attachmentStore =
            new PaymentAttachmentDraftStore();
        try
        {
            var coordinator = new SyncCoordinator(
                store,
                api,
                attachmentStore,
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);

            if (failingSaveAttempt == 3)
            {
                await Assert.ThrowsAsync<IOException>(
                    () => coordinator
                        .SynchronizeNowAsync());
            }
            else
            {
                await coordinator.SynchronizeNowAsync();
            }

            var durableAfterFailure =
                await store.LoadForOwnerAsync(owner);
            var durablePending = Assert.Single(
                durableAfterFailure
                    .PendingPaymentAttachments,
                pending =>
                    pending.LocalId ==
                    attachment.LocalId);
            Assert.NotNull(
                durablePending
                    .ServerUploadAcceptedAtUtc);
            Assert.Equal(
                1,
                api.PaymentAttachmentUploadAttempts);
            Assert.Empty(
                attachmentStore.RemovedDrafts);

            using var recoveredStore =
                new JsonSyncStateStore(
                    session,
                    durableAfterFailure);
            var recoveredCoordinator =
                new SyncCoordinator(
                    recoveredStore,
                    api,
                    attachmentStore,
                    new CustomerContractCacheStore(
                        session,
                        cacheRoot,
                        beforeAtomicPublishAsync: null),
                    session);
            var recovered =
                await recoveredCoordinator
                    .SynchronizeNowAsync();

            Assert.DoesNotContain(
                recovered.PendingPaymentAttachments,
                pending =>
                    pending.LocalId ==
                    attachment.LocalId);
            Assert.Equal(
                1,
                api.PaymentAttachmentUploadAttempts);
            Assert.Contains(
                attachmentStore.RemovedDrafts,
                removed =>
                    removed.LocalId ==
                    attachment.LocalId);
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task PaymentWriteAhead_FirstDurableSaveFailurePreventsEveryNetworkMutation()
    {
        var snapshot = Snapshot(
            "alice",
            "generation-a");
        var session = new SessionStore
        {
            Snapshot = snapshot
        };
        using var store = new JsonSyncStateStore(
            session,
            StateFor(snapshot, revision: 7))
        {
            BeforeOwnerSaveAsync = (_, _) =>
                throw new IOException(
                    "write-ahead persistence failed")
        };
        var api = new GeoraePlanApiClient();
        var cacheRoot = CreateTestRoot();
        try
        {
            var draftPath = Path.Combine(
                cacheRoot,
                "wal-save-failure.pdf");
            await File.WriteAllBytesAsync(
                draftPath,
                [1, 3, 5]);
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);
            var payment = new PaymentDto
            {
                Id = Guid.NewGuid(),
                InvoiceId = Guid.NewGuid(),
                MutationId = "wal-save-failure"
            };
            var attachment =
                new PendingPaymentAttachmentRecord
                {
                    LocalId = Guid.NewGuid(),
                    FileName = "wal-save-failure.pdf",
                    StoredPath = draftPath,
                    FileSize = 3,
                    FileHash = Convert.ToHexString(
                        SHA256.HashData([1, 3, 5]))
                };

            await Assert.ThrowsAsync<IOException>(
                () => coordinator.SavePaymentImmediatelyAsync(
                    payment,
                    [attachment]));

            Assert.Empty(store.SavedStates);
            Assert.Empty(api.SubmittedPaymentOwners);
            Assert.Empty(api.SubmittedPushes);
            Assert.Equal(
                0,
                api.PaymentAttachmentUploadAttempts);
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task PaymentWriteAhead_AcceptedThirdSaveFailureReturnsAcceptedWithoutRequeue()
    {
        var snapshot = Snapshot(
            "alice",
            "generation-a");
        var session = new SessionStore
        {
            Snapshot = snapshot
        };
        var saveAttempt = 0;
        using var store = new JsonSyncStateStore(
            session,
            StateFor(snapshot, revision: 7))
        {
            BeforeOwnerSaveAsync = (_, _) =>
            {
                if (Interlocked.Increment(
                        ref saveAttempt) == 3)
                {
                    throw new IOException(
                        "post-accept persistence failed");
                }

                return Task.CompletedTask;
            }
        };
        var api = new GeoraePlanApiClient();
        var cacheRoot = CreateTestRoot();
        try
        {
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);
            var payment = new PaymentDto
            {
                Id = Guid.NewGuid(),
                InvoiceId = Guid.NewGuid(),
                MutationId = "accepted-third-save-failure"
            };

            var result = await coordinator
                .SavePaymentWithOutcomeImmediatelyAsync(
                    payment,
                    MobileSessionOwner.Capture(snapshot),
                    attachments: null,
                    linkedTransaction: null);

            Assert.Equal(
                MobileImmediateMutationOutcome.Accepted,
                result.PaymentOutcome);
            Assert.True(result.CanInvokeSuccessCallback);
            Assert.Single(api.SubmittedPaymentOwners);
            var durable = await store.LoadForOwnerAsync(
                MobileSessionOwner.Capture(snapshot));
            Assert.DoesNotContain(
                durable.PendingPush.Payments,
                pending => pending.Id == payment.Id);
            Assert.Contains(
                "다음 동기화에서 복구",
                result.State.LastError,
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task PaymentWriteAhead_UnknownPaymentOutcomeRetainsOneIdempotentJournalEntry()
    {
        var snapshot = Snapshot(
            "alice",
            "generation-a");
        var session = new SessionStore
        {
            Snapshot = snapshot
        };
        using var store = new JsonSyncStateStore(
            session,
            StateFor(snapshot, revision: 7));
        var api = new GeoraePlanApiClient
        {
            BeforePaymentReturnAsync = () =>
                throw new HttpRequestException(
                    "response lost after send")
        };
        var cacheRoot = CreateTestRoot();
        try
        {
            var draftPath = Path.Combine(
                cacheRoot,
                "unknown-outcome.pdf");
            await File.WriteAllBytesAsync(
                draftPath,
                [2, 4, 6]);
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);
            var payment = new PaymentDto
            {
                Id = Guid.NewGuid(),
                InvoiceId = Guid.NewGuid(),
                MutationId = "unknown-outcome"
            };
            var attachment =
                new PendingPaymentAttachmentRecord
                {
                    LocalId = Guid.NewGuid(),
                    FileName = "unknown-outcome.pdf",
                    StoredPath = draftPath,
                    FileSize = 3,
                    FileHash = Convert.ToHexString(
                        SHA256.HashData([2, 4, 6]))
                };

            var outcome =
                await coordinator
                    .SavePaymentWithOutcomeImmediatelyAsync(
                    payment,
                    MobileSessionOwner.Capture(snapshot),
                    [attachment],
                    linkedTransaction: null);
            var result = outcome.State;

            Assert.Single(api.SubmittedPaymentOwners);
            Assert.Equal(
                MobileImmediateMutationOutcome.Unknown,
                outcome.PaymentOutcome);
            Assert.True(
                outcome.CanInvokeSuccessCallback);
            Assert.Single(
                result.PendingPush.Payments,
                pending =>
                    pending.MutationId ==
                    payment.MutationId);
            Assert.Single(
                result.PendingPaymentAttachments,
                pending =>
                    pending.LocalId ==
                    attachment.LocalId);
            Assert.Equal(
                0,
                api.PaymentAttachmentUploadAttempts);
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task PaymentWriteAhead_AcceptedConflictRecoversAttachmentWithoutRepushingPayment()
    {
        var snapshot = Snapshot(
            "alice",
            "generation-a");
        var session = new SessionStore
        {
            Snapshot = snapshot
        };
        using var store = new JsonSyncStateStore(
            session,
            StateFor(snapshot, revision: 7));
        var cacheRoot = CreateTestRoot();
        var draftPath = Path.Combine(
            cacheRoot,
            "conflict-evidence.pdf");
        await File.WriteAllBytesAsync(
            draftPath,
            [7, 8, 9]);
        var payment = new PaymentDto
        {
            Id = Guid.NewGuid(),
            InvoiceId = Guid.NewGuid(),
            MutationId = "accepted-conflict-payment"
        };
        var linkedTransaction = new TransactionDto
        {
            Id = payment.Id,
            MutationId = "conflicted-linked-transaction",
            TenantCode = snapshot.TenantCode,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode =
                snapshot.OfficeCode
        };
        var attachment =
            new PendingPaymentAttachmentRecord
            {
                LocalId = Guid.NewGuid(),
                FileName = "conflict-evidence.pdf",
                StoredPath = draftPath,
                FileSize = 3,
                FileHash = Convert.ToHexString(
                    SHA256.HashData([7, 8, 9]))
            };
        var api = new GeoraePlanApiClient
        {
            PushResult = new SyncPushResult
            {
                ConflictCount = 1,
                AcceptedCount = 1,
                AcceptedRevisions =
                [
                    new SyncAcceptedRevisionDto
                    {
                        EntityName = "Payment",
                        EntityId = payment.Id,
                        Revision = 8
                    }
                ]
            }
        };
        try
        {
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);

            var saveResult =
                await coordinator.SavePaymentWithOutcomeImmediatelyAsync(
                    payment,
                    MobileSessionOwner.Capture(snapshot),
                    [attachment],
                    linkedTransaction);
            var conflicted = saveResult.State;
            Assert.Equal(
                MobileImmediateMutationOutcome.Accepted,
                saveResult.PaymentOutcome);
            Assert.Equal(
                MobileImmediateMutationOutcome.Rejected,
                saveResult.LinkedTransactionOutcome);
            Assert.True(saveResult.PaymentAccepted);
            Assert.True(
                saveResult.LinkedTransactionNeedsRecovery);
            Assert.True(
                saveResult.CanInvokeSuccessCallback);
            Assert.Contains(
                "연결 거래 복구 대기",
                saveResult.BuildStatusMessage("수금"),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                conflicted.PendingPush.Payments,
                pending => pending.Id == payment.Id);
            Assert.Contains(
                conflicted.PendingPush.Transactions,
                pending =>
                    pending.Id ==
                    linkedTransaction.Id);
            Assert.Contains(
                conflicted.PendingPaymentAttachments,
                pending =>
                    pending.LocalId ==
                    attachment.LocalId);

            api.PushResult = new SyncPushResult();
            var recoveryPushExcludedPayment = false;
            var recoveryPushIncludedTransaction = false;
            api.BeforePushReturnAsync = () =>
            {
                var recoveryPush =
                    api.SubmittedPushes.Last();
                recoveryPushExcludedPayment =
                    recoveryPush.Payments.Count == 0;
                recoveryPushIncludedTransaction =
                    recoveryPush.Transactions.Any(
                        pending =>
                            pending.Id ==
                            linkedTransaction.Id);
                return Task.CompletedTask;
            };
            var recovered =
                await coordinator.SynchronizeNowAsync();

            Assert.DoesNotContain(
                recovered.PendingPaymentAttachments,
                pending =>
                    pending.LocalId ==
                    attachment.LocalId);
            Assert.True(recoveryPushExcludedPayment);
            Assert.True(recoveryPushIncludedTransaction);
            Assert.Equal(
                1,
                api.PaymentAttachmentUploadAttempts);
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public void MobilePaymentWriteAheadJournal_TransitionsAreIdempotentByMutationAndClientAttachmentIds()
    {
        var state = new MobileSyncState();
        state.Normalize();
        var payment = new PaymentDto
        {
            Id = Guid.NewGuid(),
            MutationId = "payment-idempotency"
        };
        var transaction = new TransactionDto
        {
            Id = payment.Id,
            MutationId = "transaction-idempotency"
        };
        var attachment = new PendingPaymentAttachmentRecord
        {
            LocalId = Guid.NewGuid()
        };
        var replayedPayment = new PaymentDto
        {
            Id = Guid.NewGuid(),
            MutationId = payment.MutationId
        };
        var replayedTransaction = new TransactionDto
        {
            Id = Guid.NewGuid(),
            MutationId = transaction.MutationId
        };
        var replayedAttachment =
            new PendingPaymentAttachmentRecord
            {
                LocalId = attachment.LocalId
            };

        MobilePaymentWriteAheadJournal
            .PrepareBeforeNetworkMutation(
                state,
                payment,
                transaction,
                [attachment]);
        MobilePaymentWriteAheadJournal
            .PrepareBeforeNetworkMutation(
                state,
                replayedPayment,
                replayedTransaction,
                [replayedAttachment]);

        Assert.Single(state.PendingPush.Payments);
        Assert.Single(state.PendingPush.Transactions);
        Assert.Single(state.PendingPaymentAttachments);
        Assert.Equal(
            replayedPayment.Id,
            state.PendingPaymentAttachments[0].PaymentId);
        Assert.Equal(
            replayedPayment.Id,
            state.PendingPush.Payments[0].Id);
        Assert.Equal(
            replayedTransaction.Id,
            state.PendingPush.Transactions[0].Id);

        MobilePaymentWriteAheadJournal.MarkServerAccepted(
            state,
            payment,
            transaction);
        Assert.Empty(state.PendingPush.Payments);
        Assert.Empty(state.PendingPush.Transactions);
        Assert.Contains(
            state.SyncedPayments,
            current => current.Id == payment.Id);
        Assert.Contains(
            state.SyncedTransactions,
            current => current.Id == transaction.Id);
        Assert.Single(state.PendingPaymentAttachments);
        Assert.True(
            MobilePaymentWriteAheadJournal
                .MarkAttachmentUploadedOrTerminal(
                    state,
                    replayedAttachment.LocalId));
        Assert.False(
            MobilePaymentWriteAheadJournal
                .MarkAttachmentUploadedOrTerminal(
                    state,
                    replayedAttachment.LocalId));
    }

    [Fact]
    public async Task SavePaymentImmediately_OwnerCapturedBeforeSyncLockWait_IsRejectedAfterOwnerSwitch()
    {
        var aliceSnapshot = Snapshot("alice", "generation-a");
        var session = new SessionStore { Snapshot = aliceSnapshot };
        var initial = StateFor(aliceSnapshot, revision: 7);
        initial.PendingPush.Invoices.Add(
            new InvoiceDto { Id = Guid.NewGuid() });
        using var store = new JsonSyncStateStore(session, initial);
        var pushEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePush = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new GeoraePlanApiClient
        {
            BeforePushReturnAsync = async () =>
            {
                pushEntered.TrySetResult();
                await releasePush.Task;
            }
        };
        var cacheRoot = CreateTestRoot();
        try
        {
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);
            var owner = session.CaptureOwner();
            var pushTask = coordinator.PushAsync();
            await pushEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(3));
            var saveTask = coordinator.SavePaymentImmediatelyAsync(
                new PaymentDto
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = Guid.NewGuid()
                },
                owner);
            session.Snapshot = Snapshot(
                "bob",
                "generation-b");
            releasePush.TrySetResult();

            await Assert.ThrowsAsync<
                StaleMobileSessionOwnerException>(
                () => pushTask);
            await Assert.ThrowsAsync<
                StaleMobileSessionOwnerException>(
                () => saveTask);
            Assert.Empty(api.SubmittedPaymentOwners);
        }
        finally
        {
            releasePush.TrySetResult();
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task SaveInvoiceImmediately_OwnerSwitchDuringApiWait_PreservesOriginalOwnerPendingState()
    {
        var aliceSnapshot = Snapshot(
            "alice",
            "generation-a");
        var session = new SessionStore
        {
            Snapshot = aliceSnapshot
        };
        var invoice = new InvoiceDto
        {
            Id = Guid.NewGuid(),
            Revision = 0,
            CustomerName = "alice invoice"
        };
        var initial = StateFor(
            aliceSnapshot,
            revision: 7);
        initial.PendingPush.Invoices.Add(invoice);
        using var store = new JsonSyncStateStore(
            session,
            initial);
        var apiEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApi = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new GeoraePlanApiClient
        {
            BeforeInvoiceReturnAsync = async () =>
            {
                apiEntered.TrySetResult();
                await releaseApi.Task;
            }
        };
        var cacheRoot = CreateTestRoot();
        try
        {
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);

            var saveTask =
                coordinator.SaveInvoiceImmediatelyAsync(invoice);
            await apiEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(3));
            session.Snapshot = Snapshot(
                "bob",
                "generation-b");
            releaseApi.TrySetResult();

            await Assert.ThrowsAsync<
                StaleMobileSessionOwnerException>(
                () => saveTask);

            var alice = await store.LoadForOwnerAsync(
                MobileSessionOwner.Capture(
                    aliceSnapshot));
            var bob = await store.LoadForOwnerAsync(
                MobileSessionOwner.Capture(
                    session.Snapshot));
            Assert.Contains(
                alice.PendingPush.Invoices,
                pending => pending.Id == invoice.Id);
            Assert.Empty(bob.PendingPush.Invoices);
            var submittedOwner = Assert.Single(
                api.SubmittedInvoiceOwners);
            Assert.Equal(
                "alice",
                submittedOwner.Username);
            Assert.Equal(
                "generation-a",
                submittedOwner.SessionGeneration);
        }
        finally
        {
            releaseApi.TrySetResult();
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public void ApiClient_AllAuthenticatedMutationFamiliesExposeCapturedOwnerOverloads()
    {
        var source = ReadMobileSource(
            "Services",
            "GeoraePlanApiClient.cs");

        foreach (var methodName in new[]
                 {
                     "CreateCustomerAsync",
                     "UpdateCustomerAsync",
                     "DeleteCustomerAsync",
                     "CreateItemAsync",
                     "UpdateItemAsync",
                     "DeleteItemAsync",
                     "RestoreRecycleBinAsync",
                     "PurgeRecycleBinAsync",
                     "PushAsync",
                     "CreateInvoiceAsync",
                     "UpdateInvoiceAsync",
                     "CreatePaymentAsync",
                     "UploadPaymentAttachmentAsync"
                 })
        {
            Assert.Matches(
                new Regex(
                    $@"public\s+(?:async\s+)?Task(?:<[^>]+>)?\s+{methodName}\s*\([\s\S]*?MobileSessionOwner\s+(?:owner|expectedOwner)[\s\S]*?\)",
                    RegexOptions.CultureInvariant),
                source);
        }
    }

    [Fact]
    public void ApiClient_OwnerBoundSendRejectsOwnerSwitchBeforeSendAndBeforeResultCommit()
    {
        var source = ReadMobileSource(
            "Services",
            "GeoraePlanApiClient.cs");
        var normalizedSource = source.Replace(
            "\r\n",
            "\n",
            StringComparison.Ordinal);
        var send = ExtractMethod(
            source,
            "private async Task<HttpResponseMessage> SendAsync(");
        var post = ExtractMethod(
            source,
            "private async Task<TResponse?> PostAsync<");
        var put = ExtractMethod(
            source,
            "private async Task<TResponse?> PutAsync<");
        var delete = ExtractMethod(
            source,
            "private async Task DeleteAsync(");
        var createRequest = ExtractMethod(
            normalizedSource,
            "private async Task<HttpRequestMessage>\n        CreateOwnerBoundRequestAsync(");

        AssertInOrder(
            send,
            "_sessionStore.ThrowIfOwnerChanged(expectedOwner)",
            "SendCoreAsync(",
            "_sessionStore.IsOwnerCurrent(expectedOwner)");
        AssertInOrder(
            createRequest,
            "requestFactory(expectedOwner)",
            "_sessionStore.ThrowIfOwnerChanged(",
            "expectedOwner");
        foreach (var mutation in new[] { post, put, delete })
        {
            Assert.Contains(
                "expectedOwner",
                mutation,
                StringComparison.Ordinal);
            Assert.Contains(
                "_sessionStore.ThrowIfOwnerChanged(expectedOwner)",
                mutation,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ApiClient_OwnerBoundUnauthorizedResponse_ClearsOnlyTheCapturedOwner()
    {
        var source = ReadMobileSource(
            "Services",
            "GeoraePlanApiClient.cs");
        var ensureSuccessForOwner = ExtractMethod(
            source,
            "private async Task EnsureSuccessForOwnerAsync(");
        var ensureSuccess = ExtractMethod(
            source,
            "private async Task EnsureSuccessAsync(");
        var handleAuthenticationFailure = ExtractMethod(
            source,
            "private async Task HandleAuthenticationFailureAsync(");

        AssertInOrder(
            ensureSuccessForOwner,
            "await EnsureSuccessAsync(",
            "ct,",
            "expectedOwner);",
            "_sessionStore.ThrowIfOwnerChanged(",
            "expectedOwner");
        Assert.Contains(
            "catch (MobileAuthenticationException)",
            ensureSuccessForOwner,
            StringComparison.Ordinal);
        Assert.Contains(
            "_sessionStore.GetSnapshot().IsAuthenticated",
            ensureSuccessForOwner,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "finally",
            ensureSuccessForOwner,
            StringComparison.Ordinal);
        AssertInOrder(
            ensureSuccess,
            "response.StatusCode == HttpStatusCode.Unauthorized",
            "await HandleAuthenticationFailureAsync(",
            "expectedOwner);",
            "throw new MobileAuthenticationException");
        Assert.DoesNotContain(
            "HandleAuthenticationFailureAsync();",
            ensureSuccess,
            StringComparison.Ordinal);
        AssertInOrder(
            handleAuthenticationFailure,
            "expectedOwner is null",
            "ClearUnconditionallyAsync()",
            "_sessionStore.ClearIfCurrentAsync(",
            "expectedOwner");
    }

    [Fact]
    public void DirectMutationCallers_UseOneCapturedOwnerForPayloadSendAndFollowUpRead()
    {
        var customer = ReadMobileSource(
            "Pages",
            "CustomerEditPage.cs");
        var item = ReadMobileSource(
            "Pages",
            "ItemEditPage.cs");
        var recycleBin = ReadMobileSource(
            "ViewModels",
            "RecycleBinViewModel.cs");
        var invoiceDraft = ReadMobileSource(
            "ViewModels",
            "InvoiceDraftViewModel.cs");

        Assert.Contains(
            "CreateCustomerAsync(dto, apiOwner)",
            customer,
            StringComparison.Ordinal);
        Assert.Contains(
            "UpdateCustomerAsync(dto, apiOwner)",
            customer,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetCustomerByIdAsync(\n                source.Id,\n                apiOwner)",
            customer.Replace("\r\n", "\n"),
            StringComparison.Ordinal);

        Assert.Contains(
            "CreateItemAsync(dto, apiOwner)",
            item,
            StringComparison.Ordinal);
        Assert.Contains(
            "UpdateItemAsync(dto, apiOwner)",
            item,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetItemDetailAsync(\n                source.Id,\n                apiOwner)",
            item.Replace("\r\n", "\n"),
            StringComparison.Ordinal);

        foreach (var methodName in new[]
                 {
                     "RestoreRecycleBinAsync",
                     "PurgeRecycleBinAsync",
                     "GetRecycleBinAsync"
                 })
        {
            Assert.Matches(
                new Regex(
                    $@"_api\.{methodName}\s*\([\s\S]*?\bowner\b",
                    RegexOptions.CultureInvariant),
                recycleBin);
        }
        Assert.Contains(
            "_sessionStore.ThrowIfOwnerChanged(owner)",
            recycleBin,
            StringComparison.Ordinal);

        AssertInOrder(
            ExtractMethod(
                invoiceDraft,
                "public async Task SaveDraftAsync()"),
            "var owner = _sessionStore.CaptureOwner();",
            "var invoice = BuildCurrentInvoiceDto(forSave: true);",
            "_sessionStore.ThrowIfOwnerChanged(owner);",
            "SaveInvoiceImmediatelyAsync(",
            "invoice,",
            "owner)");
    }

    [Fact]
    public void CustomerItemFallbackQueues_KeepCapturedOwnerThroughPendingStateCommit()
    {
        var customer = ReadMobileSource(
                "Pages",
                "CustomerEditPage.cs")
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
        var item = ReadMobileSource(
                "Pages",
                "ItemEditPage.cs")
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
        var coordinator = ReadMobileSource(
                "Services",
                "SyncCoordinator.cs")
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
        var customerSave = ExtractMethod(
            customer,
            "private async Task QueuePendingSaveAsync(");
        var customerDelete = ExtractMethod(
            customer,
            "private async Task QueuePendingDeleteAsync(");
        var itemSave = ExtractMethod(
            item,
            "private async Task QueuePendingSaveAsync(");
        var itemDelete = ExtractMethod(
            item,
            "private async Task QueuePendingDeleteAsync(");
        var ownerlessMutation = ExtractMethod(
            coordinator,
            "private Task<MobileSyncState> MutateStoredStateAsync(\n        Action<MobileSyncState> mutate");
        var ownerBoundMutation = ExtractMethod(
            coordinator,
            "private async Task<MobileSyncState> MutateStoredStateAsync(\n        MobileSessionOwner owner");

        foreach (var helper in new[]
                 {
                     customerSave,
                     customerDelete
                 })
        {
            AssertInOrder(
                helper,
                "MobileSessionOwner apiOwner",
                "_sessionStore.ThrowIfOwnerChanged(apiOwner)",
                "QueueCustomerDraftAsync(",
                "dto,",
                "apiOwner,",
                "reason",
                "_sessionStore.ThrowIfOwnerChanged(apiOwner)");
            Assert.Contains(
                "catch (StaleMobileSessionOwnerException)",
                helper,
                StringComparison.Ordinal);
        }

        foreach (var helper in new[]
                 {
                     itemSave,
                     itemDelete
                 })
        {
            AssertInOrder(
                helper,
                "MobileSessionOwner apiOwner",
                "_sessionStore.ThrowIfOwnerChanged(apiOwner)",
                "QueueItemDraftAsync(",
                "dto,",
                "apiOwner,",
                "reason",
                "_sessionStore.ThrowIfOwnerChanged(apiOwner)");
            Assert.Contains(
                "catch (StaleMobileSessionOwnerException)",
                helper,
                StringComparison.Ordinal);
        }

        AssertInOrder(
            ownerlessMutation,
            "_sessionStore.CaptureOwner()",
            "MutateStoredStateAsync(",
            "owner,",
            "mutate,",
            "ct");
        AssertInOrder(
            ownerBoundMutation,
            "await _syncLock.WaitAsync(ct)",
            "_sessionStore.ThrowIfOwnerChanged(owner)",
            "_store.LoadAsync(owner, ct)",
            "_sessionStore.ThrowIfOwnerChanged(owner)",
            "SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(",
            "owner,");
    }

    [Fact]
    public void CustomerItemPostSaveUi_RevalidatesOwnerAcrossAlertCallbackAndModalCompletion()
    {
        var customer = ReadMobileSource(
                "Pages",
                "CustomerEditPage.cs")
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
        var item = ReadMobileSource(
                "Pages",
                "ItemEditPage.cs")
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);

        foreach (var source in new[] { customer, item })
        {
            var save = ExtractMethod(
                source,
                "private async Task SaveAsync()");
            var delete = ExtractMethod(
                source,
                "private async Task DeleteAsync()");
            var pendingSave = ExtractMethod(
                source,
                "private async Task QueuePendingSaveAsync(");
            var pendingDelete = ExtractMethod(
                source,
                "private async Task QueuePendingDeleteAsync(");
            var conflict = ExtractMethod(
                source,
                "private async Task HandleConcurrencyConflictAsync(");
            var close = ExtractMethod(
                source,
                "private Task CloseAsync(\n        MobileSessionOwner apiOwner)");

            foreach (var direct in new[] { save, delete })
            {
                AssertInOrder(
                    direct,
                    "InvokeAfterSavedAsync(",
                    "apiOwner);",
                    "await CloseAsync(apiOwner);");
            }

            foreach (var fallback in new[]
                     {
                         pendingSave,
                         pendingDelete
                     })
            {
                AssertInOrder(
                    fallback,
                    "await DisplayAlert(",
                    "\"확인\");",
                    "_sessionStore.ThrowIfOwnerChanged(apiOwner);",
                    "await CloseAsync(apiOwner);",
                    "_sessionStore.ThrowIfOwnerChanged(apiOwner);",
                    "MobileErrorHandler.FireAndForget(",
                    "InvokeAfterSavedAsync(",
                    "apiOwner)");
            }

            AssertInOrder(
                conflict,
                "await DisplayAlert(",
                "\"확인\");",
                "_sessionStore.ThrowIfOwnerChanged(apiOwner);",
                "await InvokeAfterSavedAsync(",
                "apiOwner);",
                "await CloseAsync(apiOwner);");
            AssertInOrder(
                close,
                "_sessionStore.ThrowIfOwnerChanged(apiOwner);",
                "return Navigation.PopModalAsync();");
        }
    }

    [Fact]
    public void CustomerItemPostSaveCallbacks_RejectReplacementOwnerBeforeAndAfterCallback()
    {
        var customer = ReadMobileSource(
                "Pages",
                "CustomerEditPage.cs")
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
        var item = ReadMobileSource(
                "Pages",
                "ItemEditPage.cs")
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
        var itemsPage = ReadMobileSource(
                "Pages",
                "ItemsPage.cs")
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
        var customerCallback = ExtractMethod(
            customer,
            "private async Task InvokeAfterSavedAsync(");
        var itemCallback = ExtractMethod(
            item,
            "private async Task InvokeAfterSavedAsync(");
        var openNew = ExtractMethod(
            itemsPage,
            "private async Task OpenNewItemAsync()");
        var openEdit = ExtractMethod(
            itemsPage,
            "private async Task OpenEditItemAsync()");
        var openDelete = ExtractMethod(
            itemsPage,
            "private async Task OpenDeleteItemAsync()");

        AssertInOrder(
            customerCallback,
            "_sessionStore.ThrowIfOwnerChanged(apiOwner);",
            "await _afterSaved(",
            "_sessionStore.ThrowIfOwnerChanged(apiOwner);");
        AssertInOrder(
            itemCallback,
            "_sessionStore.ThrowIfOwnerChanged(apiOwner);",
            "await _afterSaved(saved, apiOwner);",
            "_sessionStore.ThrowIfOwnerChanged(apiOwner);");

        foreach (var callbackOwner in new[]
                 {
                     openNew,
                     openEdit,
                     openDelete
                 })
        {
            AssertInOrder(
                callbackOwner,
                "async (saved, apiOwner) =>",
                "if (!_viewModel.IsCurrentOwner(apiOwner))",
                "await _viewModel.RefreshAsync();",
                "if (!_viewModel.IsCurrentOwner(apiOwner))",
                "RebuildCategoryButtons();");
        }
    }

    [Fact]
    public void CustomerSaveDelete_BindCacheAndApiOwnerPairBeforeMutation()
    {
        var source = ReadMobileSource(
                "Pages",
                "CustomerEditPage.cs")
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
        var save = ExtractMethod(
            source,
            "private async Task SaveAsync()");
        var delete = ExtractMethod(
            source,
            "private async Task DeleteAsync()");
        var pairGuard = ExtractMethod(
            source,
            "private static void ThrowIfOwnerPairChanged(");

        AssertInOrder(
            pairGuard,
            "cacheOwner.HasSameOwnerAndSession(apiOwner)",
            "throw new StaleCacheOwnerSessionException(");

        AssertInOrder(
            save,
            "var apiOwner = _sessionStore.CaptureOwner();",
            "_cacheStore.CaptureOwnerSession();",
            "ThrowIfOwnerPairChanged(",
            "requestOwnerSession,",
            "apiOwner);",
            "_cacheStore.ThrowIfOwnerSessionStale(",
            "requestOwnerSession);",
            "_sessionStore.ThrowIfOwnerChanged(apiOwner);",
            "CreateCustomerAsync(dto, apiOwner)",
            "UpdateCustomerAsync(dto, apiOwner)");

        AssertInOrder(
            delete,
            "var apiOwner = _sessionStore.CaptureOwner();",
            "_cacheStore.CaptureOwnerSession();",
            "ThrowIfOwnerPairChanged(",
            "requestOwnerSession,",
            "apiOwner);",
            "_cacheStore.ThrowIfOwnerSessionStale(",
            "requestOwnerSession);",
            "_sessionStore.ThrowIfOwnerChanged(apiOwner);",
            "DeleteCustomerAsync(",
            "apiOwner);");
    }

    [Fact]
    public async Task PaymentAttachmentOwnerOperation_RejectsPickerReturnAfterOwnerSwitch()
    {
        var ownerA = Snapshot("alice", "generation-a");
        var ownerB = Snapshot("bob", "generation-b");
        var session = new SessionStore
        {
            Snapshot = ownerA
        };
        var gate = new GeoraePlan.Mobile.App.ViewModels
            .MobileOwnerOperationGate(session);
        var visibleAttachments = new List<string>();
        gate.EnsureCurrentOwner(
            () => visibleAttachments.Clear());
        var operation = await gate.TryBeginAsync(
            MobileSessionOwner.Capture(ownerA),
            () => visibleAttachments.Clear(),
            deferRefreshWhenBusy: false,
            startCurrentUi: () => { });
        Assert.NotNull(operation);

        var pickerResult =
            new TaskCompletionSource<string?>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var externalAwait =
            gate.AwaitExternalResultAsync(
                operation!,
                () => pickerResult.Task);

        // The production external-await helper must reject the result after
        // the authenticated owner and operation epoch change.
        session.Snapshot = ownerB;
        gate.EnsureCurrentOwner(
            () => visibleAttachments.Clear());
        pickerResult.SetResult("alice-evidence.pdf");

        await Assert.ThrowsAsync<
            StaleMobileSessionOwnerException>(
            () => externalAwait);
        var deferredRefresh =
            await gate.CompleteAsync(
                operation!,
                _ => visibleAttachments.Add(
                    "unexpected-cleanup-commit"));

        Assert.False(deferredRefresh);
        Assert.False(gate.IsBusy);
        Assert.Empty(visibleAttachments);
    }

    [Fact]
    public void PaymentAttachmentPickers_CaptureOwnerOperationBeforeExternalAwait()
    {
        var source = ReadMobileSource(
            "ViewModels",
            "PaymentDraftViewModel.cs");
        var pdf = ExtractMethod(
            source,
            "public async Task AddPdfAttachmentAsync()");
        var camera = ExtractMethod(
            source,
            "public async Task CaptureAttachmentAsync()");
        var shared = ExtractMethod(
            source,
            "internal async Task AddAttachmentFromExternalPickerAsync(");

        Assert.Contains(
            "AddAttachmentFromExternalPickerAsync(",
            pdf,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddAttachmentFromExternalPickerAsync(",
            camera,
            StringComparison.Ordinal);
        AssertInOrder(
            shared,
            "var owner = _sessionStore.CaptureOwner();",
            "_ownerOperations.TryBeginAsync(",
            ".AwaitExternalResultAsync(",
            "operation,",
            "pickAsync);",
            "_attachmentStore.ImportAsync(",
            "_ownerOperations.TryCommitAsync(",
            "Attachments.Add(imported)");
        Assert.Contains(
            "catch (StaleMobileSessionOwnerException)",
            shared,
            StringComparison.Ordinal);
    }

    private static string ReadMobileSource(
        string directory,
        string fileName)
        => File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            directory,
            fileName));

    private static string ExtractMethod(
        string source,
        string signature)
    {
        var start = source.IndexOf(
            signature,
            StringComparison.Ordinal);
        Assert.True(
            start >= 0,
            $"Method signature not found: {signature}");
        var bodyStart = source.IndexOf(
            '{',
            start);
        Assert.True(
            bodyStart >= 0,
            $"Method body not found: {signature}");

        var depth = 0;
        for (var index = bodyStart;
             index < source.Length;
             index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(
                        start,
                        index - start + 1);
                }
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

    private static SessionSnapshot Snapshot(
        string username,
        string generation)
        => new()
        {
            IsAuthenticated = true,
            Username = username,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            SessionGeneration = generation
        };

    private static MobileSyncState StateFor(
        SessionSnapshot snapshot,
        long revision)
    {
        var state = new MobileSyncState
        {
            OwnerUsername = snapshot.Username,
            OwnerTenantCode = snapshot.TenantCode,
            OwnerOfficeCode = snapshot.OfficeCode,
            OwnerSessionGeneration =
                snapshot.SessionGeneration,
            LastRevision = revision
        };
        state.Normalize();
        return state;
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-mobile-mutation-owner-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(
        string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class ActionDisposable : IDisposable
    {
        private Action? _dispose;

        public ActionDisposable(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            Interlocked.Exchange(
                ref _dispose,
                null)?.Invoke();
        }
    }
}
