using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobileOwnerStateLivenessTests
{
    [Fact]
    public void MobilePageLifecycleGate_DisappearAfterCallbackStartSuppressesLateEffect()
    {
        var lifecycle = new MobilePageLifecycleGate();
        lifecycle.Enter();
        var callbackEpoch = lifecycle.Capture();
        var effectCount = 0;

        lifecycle.Exit();
        var committed = lifecycle.TryCommit(
            callbackEpoch,
            () => effectCount++);

        Assert.False(committed);
        Assert.Equal(0, effectCount);
    }

    [Fact]
    public async Task MobileOwnerOperationGate_AtomicStartSerializesBusyUiBeforeOwnerSwitch()
    {
        var session = CreateSession(
            "alice",
            "generation-a");
        var gate = new MobileOwnerOperationGate(
            session);
        var startEntered = new TaskCompletionSource(
            TaskCreationOptions
                .RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(
            TaskCreationOptions
                .RunContinuationsAsynchronously);
        var busyUi = false;
        var startTask = Task.Run(
            async () => await gate.TryBeginAsync(
                session.CaptureOwner(),
                () => { },
                deferRefreshWhenBusy: false,
                () =>
                {
                    startEntered.TrySetResult();
                    releaseStart.Task
                        .GetAwaiter()
                        .GetResult();
                    busyUi = true;
                }));
        await startEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        var switchTask = session.ReplaceSnapshotAsync(
            Snapshot("alice", "generation-b"));
        Assert.NotSame(
            switchTask,
            await Task.WhenAny(
                switchTask,
                Task.Delay(
                    TimeSpan.FromMilliseconds(100))));
        Assert.False(busyUi);

        releaseStart.TrySetResult();
        Assert.NotNull(await startTask);
        Assert.True(busyUi);
        await switchTask;
        Assert.Equal(
            "generation-b",
            session.Snapshot.SessionGeneration);
    }

    [Fact]
    public async Task MobileOwnerOperationGate_AtomicCommitSerializesOwnerSwitchUntilUiBatchCompletes()
    {
        var session = CreateSession(
            "alice",
            "generation-a");
        var gate = new MobileOwnerOperationGate(session);
        var operation = Assert.IsType<
            MobileOwnerUiOperation>(
            gate.TryBegin(
                () => { },
                deferRefreshWhenBusy: false));
        var effectEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEffect = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var status = "before";
        var commitTask = Task.Run(
            async () => await gate.TryCommitAsync(
                operation,
                () =>
                {
                    effectEntered.TrySetResult();
                    releaseEffect.Task.GetAwaiter().GetResult();
                    status = "owner-a-committed";
                }));
        await effectEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        var switchTask = session.ReplaceSnapshotAsync(
            Snapshot("alice", "generation-b"));
        Assert.NotSame(
            switchTask,
            await Task.WhenAny(
                switchTask,
                Task.Delay(TimeSpan.FromMilliseconds(100))));
        Assert.Equal("before", status);

        releaseEffect.TrySetResult();
        Assert.True(await commitTask);
        await switchTask;
        Assert.Equal("owner-a-committed", status);
    }

    [Fact]
    public async Task MobileOwnerCallbackContext_OwnerSwitchBeforeNavigationCommit_SkipsOldNavigation()
    {
        var session = CreateSession(
            "alice",
            "generation-a");
        var gate = new MobileOwnerOperationGate(session);
        var operation = Assert.IsType<
            MobileOwnerUiOperation>(
            gate.TryBegin(
                () => { },
                deferRefreshWhenBusy: false));
        var context = gate.CreateCallbackContext(operation);
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowNavigationCommit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var navigationStarts = 0;

        async Task PageCallbackAsync()
        {
            callbackEntered.TrySetResult();
            await allowNavigationCommit.Task;
            await context.TryCommitAsync(
                () => Interlocked.Increment(
                    ref navigationStarts));
        }

        var callbackTask =
            await gate.TryStartCallbackAsync(
                operation,
                PageCallbackAsync);
        Assert.NotNull(callbackTask);
        await callbackEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        await session.ReplaceSnapshotAsync(
            Snapshot("alice", "generation-b"));
        allowNavigationCommit.TrySetResult();
        await callbackTask!;

        Assert.Equal(0, navigationStarts);
    }

    [Fact]
    public void PaymentDraftPage_ConsumesOwnerCallbackContextAtActualNavigationStart()
    {
        var source = ReadMobileSource(
                "Pages",
                "PaymentDraftPage.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var handler = ExtractMethod(
            source,
            "private async Task HandleSavedSuccessfullyAsync(");

        AssertInOrder(
            handler,
            "MobileOwnerCallbackContext ownerContext",
            "ownerContext.TryCommitAsync(",
            "_lifecycle.TryCommitTopPage(",
            "lifecycleEpoch,",
            "Navigation.NavigationStack,",
            "this",
            "Navigation.PopAsync()",
            "if (started && navigationTask is not null)",
            "await navigationTask");
        Assert.DoesNotContain(
            "Shell.Current.Navigation.PopAsync()",
            handler,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PaymentDraftPage_WhenAnotherPageIsTop_PopEffectDoesNotStart()
    {
        var lifecycle = new MobilePageLifecycleGate();
        var epoch = lifecycle.Enter();
        var paymentPage = new object();
        var anotherPage = new object();
        IReadOnlyList<object> navigationStack =
            [paymentPage, anotherPage];
        var popStarts = 0;

        var committed = lifecycle.TryCommitTopPage(
            epoch,
            navigationStack,
            paymentPage,
            () => popStarts++);

        Assert.False(committed);
        Assert.Equal(0, popStarts);
    }

    [Fact]
    public async Task MobileOwnerOperationGate_OwnerSwitchDuringAwait_OldCompletionCannotCommitOrReleaseNewBusy()
    {
        var session = CreateSession(
            "alice",
            "generation-a");
        var gate = new MobileOwnerOperationGate(session);
        var resetOwners = new List<string>();
        void ResetForOwner()
            => resetOwners.Add(
                session.Snapshot.SessionGeneration);

        gate.EnsureCurrentOwner(ResetForOwner);
        var aliceOperation = Assert.IsType<
            MobileOwnerUiOperation>(
            gate.TryBegin(
                ResetForOwner,
                deferRefreshWhenBusy: false));
        var aliceStarted =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var allowAliceCompletion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var aliceCommitted = false;
        var aliceReleasedBusy = true;
        var aliceTask = Task.Run(async () =>
        {
            aliceStarted.TrySetResult(true);
            await allowAliceCompletion.Task;
            aliceCommitted = gate.CanCommit(
                aliceOperation);
            aliceReleasedBusy = gate.Complete(
                aliceOperation,
                ResetForOwner);
        });

        await aliceStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        session.Snapshot = Snapshot(
            "alice",
            "generation-b");
        gate.EnsureCurrentOwner(ResetForOwner);
        var bobOperation = Assert.IsType<
            MobileOwnerUiOperation>(
            gate.TryBegin(
                ResetForOwner,
                deferRefreshWhenBusy: false));
        Assert.True(gate.IsBusy);
        Assert.Equal(
            new[] { "generation-a", "generation-b" },
            resetOwners);

        allowAliceCompletion.TrySetResult(true);
        await aliceTask.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.False(aliceCommitted);
        Assert.False(aliceReleasedBusy);
        Assert.True(gate.IsBusy);
        Assert.True(gate.CanCommit(bobOperation));

        Assert.False(gate.Complete(
            bobOperation,
            ResetForOwner));
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task MobileOwnerOperationGate_RefreshDuringBusy_IsDeferredForCurrentOwner()
    {
        var session = CreateSession(
            "alice",
            "generation-a");
        var gate = new MobileOwnerOperationGate(session);
        var resetCount = 0;
        void ResetForOwner()
            => Interlocked.Increment(
                ref resetCount);

        var detailOperation = Assert.IsType<
            MobileOwnerUiOperation>(
            gate.TryBegin(
                ResetForOwner,
                deferRefreshWhenBusy: false));
        var detailStarted =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDetailCompletion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var shouldRunDeferredRefresh = false;
        var detailTask = Task.Run(async () =>
        {
            detailStarted.TrySetResult(true);
            await allowDetailCompletion.Task;
            shouldRunDeferredRefresh = gate.Complete(
                detailOperation,
                ResetForOwner);
        });

        await detailStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.Null(gate.TryBegin(
            ResetForOwner,
            deferRefreshWhenBusy: true));
        Assert.True(gate.IsBusy);

        allowDetailCompletion.TrySetResult(true);
        await detailTask.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.True(shouldRunDeferredRefresh);
        Assert.False(gate.IsBusy);
        Assert.Equal(1, resetCount);
    }

    [Theory]
    [InlineData("CustomerContractsViewModel.cs")]
    [InlineData("PaymentAttachmentsViewModel.cs")]
    [InlineData("PaymentDraftViewModel.cs")]
    public async Task DownloadScreenOwnerOperation_AtoBInterleaving_OldCompletionCannotCommitOrReleaseNewBusy(
        string viewModelFile)
    {
        var session = CreateSession(
            "alice",
            "generation-a");
        var gate = new MobileOwnerOperationGate(session);
        var resetCount = 0;
        void ResetForOwner()
            => Interlocked.Increment(
                ref resetCount);

        var ownerAOperation = Assert.IsType<
            MobileOwnerUiOperation>(
            gate.TryBegin(
                ResetForOwner,
                deferRefreshWhenBusy: false));
        var ownerAStarted =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOwnerACompletion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerACommitted = true;
        var ownerAReleasedBusy = true;
        var ownerATask = Task.Run(async () =>
        {
            ownerAStarted.TrySetResult(true);
            await allowOwnerACompletion.Task;
            ownerACommitted = gate.CanCommit(
                ownerAOperation);
            ownerAReleasedBusy = gate.Complete(
                ownerAOperation,
                ResetForOwner);
        });

        await ownerAStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        session.Snapshot = Snapshot(
            "bob",
            "generation-b");
        gate.EnsureCurrentOwner(
            ResetForOwner);
        var ownerBOperation = Assert.IsType<
            MobileOwnerUiOperation>(
            gate.TryBegin(
                ResetForOwner,
                deferRefreshWhenBusy: false));

        allowOwnerACompletion.TrySetResult(true);
        await ownerATask.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.False(ownerACommitted);
        Assert.False(ownerAReleasedBusy);
        Assert.True(gate.IsBusy);
        Assert.True(gate.CanCommit(ownerBOperation));
        Assert.Equal(2, resetCount);

        var source = ReadMobileSource(
            "ViewModels",
            viewModelFile);
        Assert.Contains(
            "MobileOwnerOperationGate",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.Contains(
                "_ownerOperations.CanCommit(operation)",
                StringComparison.Ordinal) ||
            source.Contains(
                "_ownerOperations.TryCommitAsync(",
                StringComparison.Ordinal));
        Assert.True(
            source.Contains(
                "_ownerOperations.Complete(",
                StringComparison.Ordinal) ||
            source.Contains(
                "_ownerOperations.CompleteAsync(",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AuthenticatedDownloads_UseHashedOwnerNamespaceAndOwnerBoundAtomicPublish()
    {
        var source = ReadMobileSource(
            "Services",
            "GeoraePlanApiClient.cs");
        var contractDownload = ExtractMethod(
            source,
            "public async Task<string> DownloadCustomerContractAsync(");
        var paymentDownload = ExtractMethod(
            source,
            "public async Task<string> DownloadPaymentAttachmentAsync(");
        var streamPublish = ExtractMethod(
            source,
            "private async Task DownloadFileToCacheAsync(");
        var inlinePublish = ExtractMethod(
            source,
            "private async Task WriteBytesToCacheAsync(");
        var ownerRoot = ExtractMethod(
            source,
            "private static string ResolveAuthenticatedDownloadCacheRoot(");

        Assert.Contains(
            "SHA256.HashData(",
            ownerRoot,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"authenticated-downloads\"",
            ownerRoot,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Username",
            ownerRoot,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Path.Combine(FileSystem.CacheDirectory, \"customer-contracts\")",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Path.Combine(FileSystem.CacheDirectory, \"payment-attachments\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_r{contract.Revision}",
            contractDownload,
            StringComparison.Ordinal);
        Assert.Contains(
            "_r{attachment.Revision}",
            paymentDownload,
            StringComparison.Ordinal);

        AssertInOrder(
            contractDownload,
            "_sessionStore.ThrowIfOwnerChanged(owner);",
            "ResolveAuthenticatedDownloadCacheRoot(",
            "IsCachedDownloadValidAsync(",
            "DownloadFileToCacheAsync(",
            "_sessionStore.ThrowIfOwnerChanged(owner);",
            "return cachedPath;");
        AssertInOrder(
            paymentDownload,
            "_sessionStore.ThrowIfOwnerChanged(owner);",
            "ResolveAuthenticatedDownloadCacheRoot(",
            "IsCachedDownloadValidAsync(",
            "WriteBytesToCacheAsync(",
            "DownloadFileToCacheAsync(");
        AssertInOrder(
            streamPublish,
            "EnsureSuccessForOwnerAsync(",
            "ReadAsStreamAsync(ct)",
            "CopyToAsync(target, ct)",
            "ValidateDownloadedFileAsync(",
            "AcquireOwnerCommitLeaseAsync(",
            "File.Move(",
            "ThrowIfOwnerChanged(");
        AssertInOrder(
            inlinePublish,
            "WriteAllBytesAsync(",
            "ValidateDownloadedFileAsync(",
            "AcquireOwnerCommitLeaseAsync(",
            "File.Move(",
            "ThrowIfOwnerChanged(");
        Assert.Contains(
            ".download.{Guid.NewGuid():N}",
            streamPublish,
            StringComparison.Ordinal);
        Assert.Contains(
            ".download.{Guid.NewGuid():N}",
            inlinePublish,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "CustomerContractsViewModel.cs",
        "GetCustomerContractsAsync(",
        "DownloadCustomerContractAsync(")]
    [InlineData(
        "PaymentAttachmentsViewModel.cs",
        "GetPaymentAttachmentsAsync(",
        "DownloadPaymentAttachmentAsync(")]
    public void DownloadViewModels_BindApiAndUiCommitsToOneOwnerOperation(
        string viewModelFile,
        string queryCall,
        string downloadCall)
    {
        var source = ReadMobileSource(
            "ViewModels",
            viewModelFile);

        Assert.Contains(
            "MobileSessionOwner contextOwner",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsContextOwner(operation.Owner)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            queryCall,
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            downloadCall,
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "operation.Owner",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_ownerOperations.CanCommit(operation)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Launcher.Default.OpenAsync",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResetForOwner",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CustomerContractsPage.cs")]
    [InlineData("PaymentAttachmentsPage.cs")]
    public void DownloadPages_CaptureOwnerBeforeDeferringLifecycleInitialization(
        string pageFile)
    {
        var source = ReadMobileSource(
            "Pages",
            pageFile);

        AssertInOrder(
            source,
            "_pageOwner = ServiceHelper",
            ".CaptureOwner();",
            "OnAppearing()",
            "_viewModel.EnsureContextOwnerCurrent();",
            "_viewModel.InitializeAsync(",
            "_pageOwner");
    }

    [Theory]
    [InlineData("generation-b", "USENET_GROUP", "BUSAN")]
    [InlineData("generation-a", "KT_GROUP", "KT")]
    [InlineData("generation-b", "KT_GROUP", "KT")]
    public void MobileOwnerOperationGate_GenerationTenantOrOfficeChange_ResetsOwnerState(
        string generation,
        string tenantCode,
        string officeCode)
    {
        var session = CreateSession(
            "alice",
            "generation-a");
        var gate = new MobileOwnerOperationGate(session);
        var resetCount = 0;
        gate.EnsureCurrentOwner(
            () => resetCount++);

        session.Snapshot = new SessionSnapshot
        {
            IsAuthenticated = true,
            Username = "alice",
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            SessionGeneration = generation
        };
        gate.EnsureCurrentOwner(
            () => resetCount++);

        Assert.Equal(2, resetCount);
        Assert.False(gate.IsBusy);
    }

    [Theory]
    [InlineData("ItemsViewModel.cs")]
    [InlineData("InvoicesViewModel.cs")]
    [InlineData("InventoryTransfersViewModel.cs")]
    [InlineData("RentalsViewModel.cs")]
    public void CoreSingletonViewModels_UseOwnerOperationGateAndResetVisibleOwnerState(
        string fileName)
    {
        var source = ReadMobileSource(
            "ViewModels",
            fileName);

        Assert.Contains(
            "MobileOwnerOperationGate",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "EnsureCurrentOwner()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CanCommit(operation)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Complete(\n                operation,\n                ResetForOwner)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void ResetForOwner()",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ItemsViewModel_ItemExistenceCheckBelongsToSelectOperation()
    {
        var source = ReadMobileSource(
            "ViewModels",
            "ItemsViewModel.cs");
        var search = ExtractMethod(
            source,
            "public async Task SearchItemsAsync()");
        var select = ExtractMethod(
            source,
            "public async Task SelectItemAsync(");

        Assert.DoesNotContain(
            "currentItem",
            search,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "candidate => candidate.Id == item.Id",
            search,
            StringComparison.Ordinal);
        AssertInOrder(
            select,
            "var currentItem = Items.FirstOrDefault(",
            "candidate => candidate.Id == item.Id",
            "if (currentItem is null)",
            "StatusMessage = $\"{currentItem.NameOriginal}");
    }

    [Theory]
    [InlineData("ItemsPage.cs")]
    [InlineData("InvoicesPage.cs")]
    [InlineData("InventoryTransfersPage.cs")]
    [InlineData("RentalsPage.cs")]
    public void CoreSingletonPages_CaptureOwnerBeforeLifecycleRefreshAndGuardVersionCommit(
        string fileName)
    {
        var source = ReadMobileSource(
            "Pages",
            fileName);

        Assert.Contains(
            "var owner = _viewModel.EnsureCurrentOwner();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_viewModel.IsCurrentOwner(owner)",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("customer", false)]
    [InlineData("customer", true)]
    [InlineData("item", false)]
    [InlineData("item", true)]
    public async Task CustomerItemPendingQueue_StaleCapturedOwnerNeverCommitsIntoReplacementOwner(
        string entityKind,
        bool isDeleted)
    {
        var aliceSnapshot = Snapshot(
            "alice",
            "generation-a");
        var session = CreateSession(
            "alice",
            "generation-a");
        using var store = new JsonSyncStateStore(
            session,
            StateFor(
                aliceSnapshot,
                revision: 7));
        var cacheRoot = CreateTestRoot();
        try
        {
            var coordinator = new SyncCoordinator(
                store,
                new GeoraePlanApiClient(),
                new PaymentAttachmentDraftStore(),
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);
            var owner = MobileSessionOwner.Capture(
                aliceSnapshot);
            var entityId = Guid.NewGuid();

            session.Snapshot = Snapshot(
                "bob",
                "generation-b");
            if (string.Equals(
                    entityKind,
                    "customer",
                    StringComparison.Ordinal))
            {
                await Assert.ThrowsAsync<
                    StaleMobileSessionOwnerException>(
                    () => coordinator.QueueCustomerDraftAsync(
                        new CustomerDto
                        {
                            Id = entityId,
                            IsDeleted = isDeleted
                        },
                        owner,
                        "retryable failure"));
            }
            else
            {
                await Assert.ThrowsAsync<
                    StaleMobileSessionOwnerException>(
                    () => coordinator.QueueItemDraftAsync(
                        new ItemDto
                        {
                            Id = entityId,
                            IsDeleted = isDeleted
                        },
                        owner,
                        "retryable failure"));
            }

            var alice = await store.LoadForOwnerAsync(
                owner);
            var bob = await store.LoadForOwnerAsync(
                MobileSessionOwner.Capture(
                    session.Snapshot));
            Assert.DoesNotContain(
                alice.PendingPush.Customers,
                value => value.Id == entityId);
            Assert.DoesNotContain(
                alice.PendingPush.Items,
                value => value.Id == entityId);
            Assert.DoesNotContain(
                bob.PendingPush.Customers,
                value => value.Id == entityId);
            Assert.DoesNotContain(
                bob.PendingPush.Items,
                value => value.Id == entityId);
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("item")]
    public async Task CustomerItemPendingQueue_OwnerSwitchWhileWaitingForSyncLockNeverCommitsIntoReplacementOwner(
        string entityKind)
    {
        var aliceSnapshot = Snapshot(
            "alice",
            "generation-a");
        var session = CreateSession(
            "alice",
            "generation-a");
        using var store = new JsonSyncStateStore(
            session,
            StateFor(
                aliceSnapshot,
                revision: 7));
        var pullEntered =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var releasePull =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var api = new GeoraePlanApiClient(
            new SyncPullResponse
            {
                CurrentServerRevision = 8
            })
        {
            BeforePullReturnAsync =
                async () =>
                {
                    pullEntered.TrySetResult();
                    await releasePull.Task;
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
            var owner = MobileSessionOwner.Capture(
                aliceSnapshot);
            var entityId = Guid.NewGuid();
            var pullTask = coordinator.PullAsync();
            await pullEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            var queueTask = string.Equals(
                    entityKind,
                    "customer",
                    StringComparison.Ordinal)
                ? coordinator.QueueCustomerDraftAsync(
                    new CustomerDto
                    {
                        Id = entityId
                    },
                    owner,
                    "retryable failure")
                : coordinator.QueueItemDraftAsync(
                    new ItemDto
                    {
                        Id = entityId
                    },
                    owner,
                    "retryable failure");

            session.Snapshot = Snapshot(
                "bob",
                "generation-b");
            releasePull.TrySetResult();

            await Assert.ThrowsAsync<
                StaleMobileSessionOwnerException>(
                () => pullTask);
            await Assert.ThrowsAsync<
                StaleMobileSessionOwnerException>(
                () => queueTask);
            var alice = await store.LoadForOwnerAsync(owner);
            var bob = await store.LoadForOwnerAsync(
                MobileSessionOwner.Capture(
                    session.Snapshot));
            Assert.DoesNotContain(
                alice.PendingPush.Customers,
                value => value.Id == entityId);
            Assert.DoesNotContain(
                alice.PendingPush.Items,
                value => value.Id == entityId);
            Assert.DoesNotContain(
                bob.PendingPush.Customers,
                value => value.Id == entityId);
            Assert.DoesNotContain(
                bob.PendingPush.Items,
                value => value.Id == entityId);
        }
        finally
        {
            releasePull.TrySetResult();
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task DiscardedPaymentAttachmentQueue_OwnerSwitchDuringDelete_NeverDrainsThroughReplacementOwner()
    {
        var aliceSnapshot = Snapshot(
            "alice",
            "generation-a");
        var session = CreateSession(
            "alice",
            "generation-a");
        using var store = new JsonSyncStateStore(
            session,
            StateFor(
                aliceSnapshot,
                revision: 7));
        var api = new GeoraePlanApiClient(
            new SyncPullResponse
            {
                CurrentServerRevision = 8
            });
        var attachmentStore =
            new PaymentAttachmentDraftStore();
        var firstRemovalStarted =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstRemoval =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var removalAttempt = 0;
        attachmentStore.BeforeRemoveAsync =
            async (owner, attachment) =>
            {
                if (Interlocked.Increment(
                        ref removalAttempt) != 1)
                {
                    return;
                }

                firstRemovalStarted.TrySetResult(true);
                await allowFirstRemoval.Task;
                throw new IOException(
                    "simulated delete interruption");
            };
        var cacheRoot = CreateTestRoot();
        try
        {
            var attachmentPath =
                Path.Combine(
                    cacheRoot,
                    "owner-a.pdf");
            await File.WriteAllBytesAsync(
                attachmentPath,
                [1]);
            var coordinator = new SyncCoordinator(
                store,
                api,
                attachmentStore,
                new CustomerContractCacheStore(
                    session,
                    cacheRoot,
                    beforeAtomicPublishAsync: null),
                session);
            var attachment = new PendingPaymentAttachmentRecord
            {
                LocalId = Guid.NewGuid(),
                FileName = "owner-a.pdf",
                StoredPath = attachmentPath
            };
            var saveTask =
                coordinator.SavePaymentImmediatelyAsync(
                    new PaymentDto
                    {
                        Id = Guid.NewGuid()
                    },
                    [attachment]);

            await firstRemovalStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            session.Snapshot = Snapshot(
                "bob",
                "generation-b");
            allowFirstRemoval.TrySetResult(true);
            await Assert.ThrowsAsync<
                StaleMobileSessionOwnerException>(
                () => saveTask);

            await coordinator.QueueInvoiceDraftAsync(
                new InvoiceDto
                {
                    Id = Guid.NewGuid()
                });

            var aliceOwner =
                MobileSessionOwner.Capture(
                    aliceSnapshot);
            Assert.Single(
                attachmentStore.RemovalAttempts);
            Assert.True(
                attachmentStore.RemovalAttempts[0]
                    .Owner.Matches(aliceSnapshot));
            Assert.Empty(
                attachmentStore.RemovedDrafts);

            session.Snapshot = aliceSnapshot;
            await coordinator.QueueInvoiceDraftAsync(
                new InvoiceDto
                {
                    Id = Guid.NewGuid()
                });

            Assert.Equal(
                2,
                attachmentStore.RemovalAttempts.Count);
            Assert.All(
                attachmentStore.RemovalAttempts,
                attempt =>
                    Assert.Equal(
                        aliceOwner.BuildStateKey(),
                        attempt.Owner.BuildStateKey()));
            Assert.Single(
                attachmentStore.RemovedDrafts);
            Assert.Equal(
                attachment.LocalId,
                attachmentStore.RemovedDrafts[0]
                    .LocalId);
        }
        finally
        {
            allowFirstRemoval.TrySetResult(true);
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public void PaymentAttachmentDraftStore_UsesOwnerNamespaceAndMigratesOnlyReferencedLegacyFiles()
    {
        var source = ReadMobileSource(
            "Services",
            "PaymentAttachmentDraftStore.cs");

        Assert.Contains(
            "Path.Combine(\n            DraftDirectory,\n            OwnerDirectoryName,\n            ownerHash)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "attachment.LocalId.ToString(\"N\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await ResolveOwnedPathAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Directory.EnumerateFiles(\n                     ownerDirectory,",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Directory.EnumerateFiles(\n                     DraftDirectory,",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_OwnerSwitchDuringApiWait_DoesNotSaveOldOwnerStateIntoNewOwnerSlot()
    {
        var session = CreateSession("alice", "generation-a");
        var initial = StateFor(session.Snapshot, revision: 17);
        using var store = new JsonSyncStateStore(session, initial);
        var api = new GeoraePlanApiClient(
            new SyncPullResponse { CurrentServerRevision = 31 })
        {
            BeforePullReturnAsync = () =>
            {
                session.Snapshot = Snapshot("bob", "generation-b");
                return Task.CompletedTask;
            }
        };
        var cacheRoot = CreateTestRoot();
        try
        {
            var cache = new CustomerContractCacheStore(
                session,
                cacheRoot,
                beforeAtomicPublishAsync: null);
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                cache,
                session);

            await Assert.ThrowsAsync<StaleMobileSessionOwnerException>(
                () => coordinator.PullAsync());

            var alice = await store.LoadForOwnerAsync(
                MobileSessionOwner.Capture(Snapshot(
                    "alice",
                    "generation-a")));
            var bob = await store.LoadForOwnerAsync(
                MobileSessionOwner.Capture(Snapshot(
                    "bob",
                    "generation-b")));
            Assert.Equal(17, alice.LastRevision);
            Assert.Equal(0, bob.LastRevision);
            Assert.DoesNotContain(
                store.SavedOwners,
                owner => owner.Username == "bob" &&
                         owner.SessionGeneration == "generation-a");
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task PushAsync_OwnerSwitchAfterServerAcceptance_DoesNotPersistAcceptedAStateUnderBOrLoseAPending()
    {
        var session = CreateSession("alice", "generation-a");
        var initial = StateFor(session.Snapshot, revision: 7);
        var pendingCustomerId = Guid.NewGuid();
        initial.PendingPush.Customers.Add(new CustomerDto
        {
            Id = pendingCustomerId,
            Revision = 1,
            NameOriginal = "alice pending"
        });
        using var store = new JsonSyncStateStore(session, initial);
        var api = new GeoraePlanApiClient
        {
            PushResult = new SyncPushResult(),
            BeforePushReturnAsync = () =>
            {
                session.Snapshot = Snapshot("bob", "generation-b");
                return Task.CompletedTask;
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

            await Assert.ThrowsAsync<StaleMobileSessionOwnerException>(
                () => coordinator.PushAsync());

            var alice = await store.LoadForOwnerAsync(
                MobileSessionOwner.Capture(Snapshot(
                    "alice",
                    "generation-a")));
            var bob = await store.LoadForOwnerAsync(
                MobileSessionOwner.Capture(Snapshot(
                    "bob",
                    "generation-b")));
            Assert.Contains(
                alice.PendingPush.Customers,
                customer => customer.Id == pendingCustomerId);
            Assert.Empty(bob.PendingPush.Customers);
            Assert.Single(api.SubmittedPushes);
        }
        finally
        {
            DeleteTestRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task RemoveContractAsync_PurgeDeletesUnreferencedCanonicalObject()
    {
        var root = CreateTestRoot();
        try
        {
            var cache = new CustomerContractCacheStore(root);
            var customerId = Guid.NewGuid();
            var contract = Contract(
                customerId,
                Guid.NewGuid(),
                revision: 1,
                [1, 3, 5, 7]);
            await cache.SaveContractsAsync(customerId, [contract]);
            var objectPath = await cache.EnsureCachedPdfAsync(
                customerId,
                contract);
            Assert.NotNull(objectPath);
            Assert.True(File.Exists(objectPath));

            await cache.RemoveContractAsync(
                contract.Id,
                purgeRevision: 2);

            Assert.False(File.Exists(objectPath));
            Assert.Empty(await cache.LoadContractsAsync(customerId));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task RemoveContractAsync_PurgeRetainsObjectReferencedByAnotherContract()
    {
        var root = CreateTestRoot();
        try
        {
            var cache = new CustomerContractCacheStore(root);
            var customerId = Guid.NewGuid();
            var sharedBytes = new byte[] { 2, 4, 6, 8 };
            var first = Contract(
                customerId,
                Guid.NewGuid(),
                revision: 1,
                sharedBytes);
            var second = Contract(
                customerId,
                Guid.NewGuid(),
                revision: 1,
                sharedBytes);
            await cache.SaveContractsAsync(
                customerId,
                [first, second]);
            var firstObject = await cache.EnsureCachedPdfAsync(
                customerId,
                first);
            var secondObject = await cache.EnsureCachedPdfAsync(
                customerId,
                second);
            Assert.Equal(firstObject, secondObject);

            await cache.RemoveContractAsync(
                first.Id,
                purgeRevision: 2);

            Assert.True(File.Exists(secondObject));
            Assert.Single(await cache.LoadContractsAsync(customerId));
            Assert.NotNull(await cache.EnsureCachedPdfAsync(
                customerId,
                second));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task PullAsync_ContractObjectDeleteFailure_KeepsCursorAndHidesPurgedPdf()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTestRoot();
        try
        {
            const long initialRevision = 23;
            const long purgeRevision = 29;
            var session = CreateSession("alice", "generation-a");
            var initial = StateFor(session.Snapshot, initialRevision);
            using var store = new JsonSyncStateStore(session, initial);
            var customerId = Guid.NewGuid();
            var contract = Contract(
                customerId,
                Guid.NewGuid(),
                revision: 1,
                [9, 7, 5, 3]);
            var cache = new CustomerContractCacheStore(
                session,
                root,
                beforeAtomicPublishAsync: null);
            var owner = cache.CaptureOwnerSession();
            await cache.SaveContractsAsync(
                owner,
                customerId,
                [contract]);
            var objectPath = await cache.EnsureCachedPdfAsync(
                owner,
                customerId,
                contract);
            Assert.NotNull(objectPath);
            var response = new SyncPullResponse
            {
                CurrentServerRevision = purgeRevision,
                PurgeRecords =
                [
                    new RecycleBinPurgeRecordDto
                    {
                        Kind = "contract",
                        EntityId = contract.Id,
                        Revision = purgeRevision
                    }
                ]
            };
            var api = new GeoraePlanApiClient(response, response);
            var coordinator = new SyncCoordinator(
                store,
                api,
                new PaymentAttachmentDraftStore(),
                cache,
                session);

            MobileSyncState failed;
            using (File.Open(
                       objectPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                failed = await coordinator.PullAsync();
            }

            Assert.Equal(initialRevision, failed.LastRevision);
            Assert.NotEmpty(failed.LastError);
            Assert.Empty(await cache.LoadContractsAsync(
                cache.CaptureOwnerSession(),
                customerId));
            Assert.Null(await cache.EnsureCachedPdfAsync(
                cache.CaptureOwnerSession(),
                customerId,
                contract));

            var replayed = await coordinator.PullAsync();
            Assert.Equal(purgeRevision, replayed.LastRevision);
            Assert.False(File.Exists(objectPath));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void SessionStore_ClearIfCurrent_CheckAndClearShareOneOwnerMutationLease()
    {
        var source = ReadMobileSource(
            "Services",
            "SessionStore.cs");
        var method = ExtractMethod(
            source,
            "public async Task<bool> ClearIfCurrentAsync(");

        AssertInOrder(
            method,
            "await _ownerMutationGate.WaitAsync",
            "if (!IsOwnerCurrent(owner))",
            "await ClearCoreAsync()");
        Assert.DoesNotContain(
            "await ClearAsync()",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStore_StaleReadPaths_NeverClearAReplacementOwner()
    {
        var source = ReadMobileSource(
            "Services",
            "SessionStore.cs");
        var usableSession = ExtractMethod(
            source,
            "public async Task<bool> HasUsableSessionAsync()");
        var getToken = ExtractMethod(
            source,
            "public async Task<string?> GetTokenAsync(");

        foreach (var method in new[] { usableSession, getToken })
        {
            Assert.Contains(
                "CaptureOwner()",
                method,
                StringComparison.Ordinal);
            Assert.Contains(
                "IsOwnerCurrent(owner)",
                method,
                StringComparison.Ordinal);
            Assert.Contains(
                "ClearIfCurrentAsync(owner)",
                method,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "ClearAsync()",
                method,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SessionStore_SavePublishesOwnerMetadataWithCommitMarkerLast()
    {
        var source = ReadMobileSource(
            "Services",
            "SessionStore.cs");
        var save = ExtractMethod(
            source,
            "public async Task SaveAsync(");
        var resolveExpiration = ExtractMethod(
            source,
            "private DateTime? ResolveExpirationUtc(");

        AssertInOrder(
            save,
            "Preferences.Default.Remove(HasSessionKey)",
            "SecureStorage.Default.SetAsync(",
            "Preferences.Default.Set(UsernameKey",
            "Preferences.Default.Set(HasSessionKey, true)");
        Assert.DoesNotContain(
            "Preferences.Default.Set(",
            resolveExpiration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CustomersRefresh_OwnerSwitch_ReleasesOldBusyWithoutMutatingNewOwnerUi()
    {
        var source = ReadMobileSource(
            "ViewModels",
            "CustomersViewModel.cs");
        Assert.Contains(
            "BeginRefreshOperation(ownerSession)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompleteRefreshOperation(operation)",
            source,
            StringComparison.Ordinal);
        AssertInOrder(
            ExtractMethod(source, "private void CompleteRefreshOperation"),
            "if (_refreshOperationToken != operation.Token)",
            "return;",
            "IsBusy = false;");
    }

    [Fact]
    public void CustomersDetail_AutoRecoveryGenerationChange_DoesNotLatchBusy()
    {
        var source = ReadMobileSource(
            "ViewModels",
            "CustomersViewModel.cs");
        Assert.Contains(
            "BeginDetailOperation(ownerSession)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompleteDetailOperation(operation)",
            source,
            StringComparison.Ordinal);
        AssertInOrder(
            ExtractMethod(source, "private void ResetForOwner"),
            "_refreshOperationToken++;",
            "_detailOperationToken++;",
            "IsBusy = false;",
            "IsDetailBusy = false;");
    }

    [Fact]
    public void CustomersViewModel_NewOwnerStartsWithEmptyCollections()
    {
        var source = ReadMobileSource(
            "ViewModels",
            "CustomersViewModel.cs");
        var reset = ExtractMethod(
            source,
            "private void ResetForOwner");
        AssertInOrder(
            reset,
            "Customers.Clear();",
            "ClearSelectedCustomer();",
            "_lastRefreshUtc = null;");
    }

    [Fact]
    public async Task CustomerSaveDuringRefresh_StaleRefreshCannotOverwriteSavedRevision()
    {
        var root = CreateTestRoot();
        try
        {
            var cache = new CustomerContractCacheStore(root);
            var customerId = Guid.NewGuid();
            var staleResponse = new CustomerDto
            {
                Id = customerId,
                Revision = 4,
                NameOriginal = "stale refresh"
            };
            var saved = new CustomerDto
            {
                Id = customerId,
                Revision = 5,
                NameOriginal = "saved"
            };

            await cache.SaveCustomersAsync([saved]);
            await cache.SaveCustomersAsync([staleResponse]);

            var current = Assert.Single(
                await cache.LoadCustomersAsync());
            Assert.Equal(5, current.Revision);
            Assert.Equal("saved", current.NameOriginal);
            var source = ReadMobileSource(
                "ViewModels",
                "CustomersViewModel.cs");
            Assert.Contains(
                "RequestDeferredRefresh(ownerSession)",
                source,
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task SaveCustomersAsync_LowerRevisionAfterHigherRevision_DoesNotDowngrade()
    {
        var root = CreateTestRoot();
        try
        {
            var cache = new CustomerContractCacheStore(root);
            var customerId = Guid.NewGuid();
            await cache.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = customerId,
                    Revision = 12,
                    NameOriginal = "higher"
                }
            ]);
            await cache.SaveCustomersAsync(
            [
                new CustomerDto
                {
                    Id = customerId,
                    Revision = 11,
                    NameOriginal = "lower"
                }
            ]);

            var current = Assert.Single(
                await cache.LoadCustomersAsync());
            Assert.Equal(12, current.Revision);
            Assert.Equal("higher", current.NameOriginal);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task SaveContractsAsync_LowerRevisionAfterHigherRevision_PreservesManifestAndPdf()
    {
        var root = CreateTestRoot();
        try
        {
            var cache = new CustomerContractCacheStore(root);
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var higher = Contract(
                customerId,
                contractId,
                revision: 8,
                [8, 8, 8, 8]);
            var lower = Contract(
                customerId,
                contractId,
                revision: 7,
                [7, 7, 7, 7]);
            await cache.SaveContractsAsync(customerId, [higher]);
            var higherPath = await cache.EnsureCachedPdfAsync(
                customerId,
                higher);
            var higherBytes = await File.ReadAllBytesAsync(higherPath!);

            await cache.SaveContractsAsync(customerId, [lower]);

            var current = Assert.Single(
                await cache.LoadContractsAsync(customerId));
            Assert.Equal(8, current.Revision);
            Assert.Equal(higher.FileHash, current.FileHash);
            var currentPath = await cache.EnsureCachedPdfAsync(
                customerId,
                current);
            Assert.Equal(higherPath, currentPath);
            Assert.Equal(
                higherBytes,
                await File.ReadAllBytesAsync(currentPath!));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static SessionStore CreateSession(
        string username,
        string generation)
        => new()
        {
            Snapshot = Snapshot(username, generation)
        };

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

    private static CustomerContractDto Contract(
        Guid customerId,
        Guid contractId,
        long revision,
        byte[] bytes)
        => new()
        {
            Id = contractId,
            CustomerId = customerId,
            Revision = revision,
            FileName = $"contract-{revision}.pdf",
            FileSize = bytes.LongLength,
            FileHash = Convert.ToHexString(
                    SHA256.HashData(bytes))
                .ToLowerInvariant(),
            FileContent = bytes
        };

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

    private static string CreateTestRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-mobile-owner-liveness-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
