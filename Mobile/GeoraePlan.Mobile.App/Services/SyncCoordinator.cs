using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class SyncCoordinator
{
    private readonly JsonSyncStateStore _store;
    private readonly GeoraePlanApiClient _api;
    private readonly PaymentAttachmentDraftStore _attachmentStore;
    private readonly CustomerContractCacheStore _contractCacheStore;
    private readonly SessionStore _sessionStore;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly List<PendingPaymentAttachmentRecord> _discardedPaymentAttachmentDrafts = new();
    private static readonly TimeSpan OrphanPaymentAttachmentDraftMinimumAge = TimeSpan.FromDays(7);

    public const string ConcurrencyConflictUserMessage = "다른 PC/모바일에서 먼저 수정되어 최신값을 다시 불러왔습니다. 내용을 확인한 뒤 다시 저장해 주세요.";

    public SyncCoordinator(JsonSyncStateStore store, GeoraePlanApiClient api, PaymentAttachmentDraftStore attachmentStore, CustomerContractCacheStore contractCacheStore, SessionStore sessionStore)
    {
        _store = store;
        _api = api;
        _attachmentStore = attachmentStore;
        _contractCacheStore = contractCacheStore;
        _sessionStore = sessionStore;
    }

    public static bool IsConcurrencyConflictState(MobileSyncState state)
        => state is not null && IsConcurrencyConflictMessage(state.LastError);

    public static bool IsFailedImmediateSaveWithoutServerAcceptance(MobileSyncState state)
        => state is not null &&
           state.ConsecutiveFailureCount > 0 &&
           !string.IsNullOrWhiteSpace(state.LastError) &&
           !HasServerAcceptanceDuringCurrentAttempt(state);

    private static bool HasServerAcceptanceDuringCurrentAttempt(MobileSyncState state)
        => state.LastSuccessUtc.HasValue &&
           state.LastAttemptUtc.HasValue &&
           state.LastSuccessUtc.Value >= state.LastAttemptUtc.Value;

    public static bool IsConcurrencyConflictMessage(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           message.Contains("다른 PC/모바일에서 먼저 수정", StringComparison.Ordinal);

    public async Task<MobileSyncState> LoadAsync(CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);
        await CleanupOrphanPaymentAttachmentDraftsAsync(state, ct);
        return state;
    }

    public async Task<MobileSyncState> PullAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            state.LastAttemptUtc = DateTime.UtcNow;

            try
            {
                var response = EnsurePullResponse(await _api.PullAsync(state.LastRevision, ct));
                await ApplyPullResponseAsync(state, response, ct);
            }
            catch (Exception ex)
            {
                MarkFailure(state, ex);
            }

            await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> PushAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            return await PushInternalAsync(state, ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> SynchronizeNowAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            var scopedPaymentIdsBeforePush = MobilePendingScopeFilter
                .CreateScopedPushRequest(_sessionStore.GetSnapshot(), state)
                .Payments
                .Select(payment => payment.Id)
                .Where(id => id != Guid.Empty)
                .ToHashSet();
            state = await PushInternalAsync(state, ct);
            if (!string.IsNullOrWhiteSpace(state.LastError))
                return state;

            state = await UploadPendingPaymentAttachmentsInternalAsync(state, ct, scopedPaymentIdsBeforePush);
            if (!string.IsNullOrWhiteSpace(state.LastError))
            {
                await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
                return state;
            }

            state = await PullInternalAsync(state, ct);
            await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> RefreshIfServerChangedAsync(string reason, TimeSpan minInterval, CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            var now = DateTime.UtcNow;
            if (state.LastBackgroundSyncUtc.HasValue && now - state.LastBackgroundSyncUtc.Value < minInterval)
                return state;

            state.LastBackgroundSyncUtc = now;

            var scopedPaymentIdsBeforePush = MobilePendingScopeFilter
                .CreateScopedPushRequest(_sessionStore.GetSnapshot(), state)
                .Payments
                .Select(payment => payment.Id)
                .Where(id => id != Guid.Empty)
                .ToHashSet();
            if (MobilePendingScopeFilter.HasScopedServerSyncPayload(_sessionStore.GetSnapshot(), state) ||
                MobilePendingScopeFilter.GetScopedPaymentAttachments(_sessionStore.GetSnapshot(), state, scopedPaymentIdsBeforePush).Count > 0)
            {
                state = await PushInternalAsync(state, ct);
                if (string.IsNullOrWhiteSpace(state.LastError))
                    state = await UploadPendingPaymentAttachmentsInternalAsync(state, ct, scopedPaymentIdsBeforePush);
                if (string.IsNullOrWhiteSpace(state.LastError))
                    state = await PullInternalAsync(state, ct);

                await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
                return state;
            }

            try
            {
                var syncStatus = EnsureSyncStatus(await _api.GetSyncStatusAsync(ct));
                if (syncStatus.CurrentServerRevision > state.LastRevision)
                    state = await PullInternalAsync(state, ct);
                else
                {
                    state.LastSuccessUtc ??= now;
                    state.LastError = string.Empty;
                    state.LastFailureAllowsCachedDisplay = true;
                }
            }
            catch (Exception ex)
            {
                MarkFailure(state, ex);
            }

            await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> SaveInvoiceImmediatelyAsync(InvoiceDto invoice, CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            state.LastAttemptUtc = DateTime.UtcNow;
            state.Normalize();
            state.PendingPush.Invoices.RemoveAll(x => x.Id == invoice.Id);

            try
            {
                var isExistingInvoice = invoice.Revision > 0 || !string.IsNullOrWhiteSpace(invoice.InvoiceNumber);
                var saved = isExistingInvoice
                    ? await _api.UpdateInvoiceAsync(invoice, ct)
                    : await _api.CreateInvoiceAsync(invoice, ct);
                saved = EnsureEntityResult(saved, "전표 저장");
                state.LastRevision = Math.Max(state.LastRevision, saved.Revision);

                state.LastSuccessUtc = DateTime.UtcNow;
                state.LastError = string.Empty;
                state.LastFailureAllowsCachedDisplay = true;
                state.ConsecutiveFailureCount = 0;
                state = await PullInternalAsync(state, ct);
                state.LastSuccessUtc = DateTime.UtcNow;
            }
            catch (Exception ex) when (IsConcurrencyConflict(ex))
            {
                await MarkConcurrencyConflictAndRefreshAsync(state, ex, ct);
            }
            catch (Exception ex) when (IsNonRetryableClientFailure(ex))
            {
                MarkFailure(state, ex);
            }
            catch (Exception ex)
            {
                state.PendingPush.Invoices.Add(invoice);
                MarkFailure(state, ex);
            }

            await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> SavePaymentImmediatelyAsync(
        PaymentDto payment,
        IEnumerable<PendingPaymentAttachmentRecord>? attachments = null,
        TransactionDto? linkedTransaction = null,
        CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            state.LastAttemptUtc = DateTime.UtcNow;
            state.Normalize();
            state.PendingPush.Payments.RemoveAll(x => x.Id == payment.Id);
            if (linkedTransaction is not null)
                state.PendingPush.Transactions.RemoveAll(x => x.Id == linkedTransaction.Id);

            var attachmentList = attachments?.ToList() ?? [];
            var uploadedAttachments = new List<PendingPaymentAttachmentRecord>();
            var terminalFailedAttachments = new List<PendingPaymentAttachmentRecord>();
            var attachmentUploadErrors = new List<string>();
            var terminalAttachmentUploadErrors = new List<string>();

            try
            {
                if (linkedTransaction is null)
                {
                    var saved = await _api.CreatePaymentAsync(payment, ct);
                    saved = EnsureEntityResult(saved, "입금 저장");
                    state.LastRevision = Math.Max(state.LastRevision, saved.Revision);
                }
                else
                {
                    var request = new SyncPushRequest { DeviceId = state.DeviceId };
                    request.Transactions.Add(linkedTransaction);
                    request.Payments.Add(payment);
                    var result = EnsurePushResult(await _api.PushAsync(request, ct));
                    state.LastRevision = Math.Max(state.LastRevision, result.CurrentServerRevision);
                    if (result.ConflictCount > 0)
                    {
                        QueuePaymentAttachmentsForRetry(state, payment.Id, attachmentList);
                        QueueUnacceptedLinkedPaymentConflict(state.PendingPush, payment, linkedTransaction, result.AcceptedRevisions);
                        await MarkPushConflictAndRefreshAsync(state, result, ct);
                        await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
                        return state;
                    }
                }

                foreach (var attachment in attachmentList)
                {
                    attachment.PaymentId = payment.Id;
                    state.PendingPaymentAttachments.RemoveAll(x => x.LocalId == attachment.LocalId);

                    try
                    {
                        EnsurePaymentAttachmentResult(await _api.UploadPaymentAttachmentAsync(payment.Id, attachment, ct));
                        uploadedAttachments.Add(attachment);
                    }
                    catch (Exception uploadEx) when (ShouldRetryPaymentAttachmentUpload(uploadEx))
                    {
                        state.PendingPaymentAttachments.Add(attachment);
                        attachmentUploadErrors.Add(uploadEx.Message);
                        if (string.IsNullOrWhiteSpace(state.LastError))
                        {
                            state.LastError = uploadEx.Message;
                            state.LastFailureAllowsCachedDisplay = true;
                        }
                    }
                    catch (Exception uploadEx)
                    {
                        terminalAttachmentUploadErrors.Add(BuildTerminalPaymentAttachmentUploadFailureMessage(attachment, uploadEx));
                        terminalFailedAttachments.Add(attachment);
                    }
                }

                state.LastSuccessUtc = DateTime.UtcNow;
                state.LastFailureAllowsCachedDisplay = true;
                state.ConsecutiveFailureCount = 0;
                state = await PullInternalAsync(state, ct);
                state.LastSuccessUtc = DateTime.UtcNow;
                RestorePaymentAttachmentUploadErrorsAfterPull(state, attachmentUploadErrors);
                RestoreTerminalPaymentAttachmentUploadErrorsAfterPull(state, terminalAttachmentUploadErrors);
            }
            catch (Exception ex) when (IsConcurrencyConflict(ex))
            {
                await MarkConcurrencyConflictAndRefreshAsync(state, ex, ct);
            }
            catch (Exception ex) when (IsNonRetryableClientFailure(ex))
            {
                MarkFailure(state, ex);
            }
            catch (Exception ex)
            {
                state.PendingPush.Payments.Add(payment);
                if (linkedTransaction is not null)
                    state.PendingPush.Transactions.Add(linkedTransaction);
                QueuePaymentAttachmentsForRetry(state, payment.Id, attachmentList);

                MarkFailure(state, ex);
            }

            QueueDiscardedPaymentAttachmentDrafts(uploadedAttachments);
            QueueDiscardedPaymentAttachmentDrafts(terminalFailedAttachments);
            await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> TryBackgroundSyncAsync(string reason, TimeSpan minInterval, CancellationToken ct = default)
        => await RefreshIfServerChangedAsync(reason, minInterval, ct);

    public async Task<MobileSyncState> QueueInvoiceDraftAsync(InvoiceDto invoice, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.Invoices.RemoveAll(x => x.Id == invoice.Id);
            state.PendingPush.Invoices.Add(invoice);
        }, ct);

    public async Task<MobileSyncState> QueueCustomerDraftAsync(CustomerDto customer, string? pendingReason = null, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.LastAttemptUtc = DateTime.UtcNow;
            state.PendingPush.Customers.RemoveAll(x => x.Id == customer.Id);
            state.PendingPush.Customers.Add(customer);
            if (!string.IsNullOrWhiteSpace(pendingReason))
            {
                state.LastError = pendingReason.Trim();
                state.LastFailureAllowsCachedDisplay = true;
                state.ConsecutiveFailureCount++;
            }
        }, ct);

    public async Task<MobileSyncState> QueueItemDraftAsync(ItemDto item, string? pendingReason = null, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.LastAttemptUtc = DateTime.UtcNow;
            state.PendingPush.Items.RemoveAll(x => x.Id == item.Id);
            state.PendingPush.Items.Add(item);
            if (!string.IsNullOrWhiteSpace(pendingReason))
            {
                state.LastError = pendingReason.Trim();
                state.LastFailureAllowsCachedDisplay = true;
                state.ConsecutiveFailureCount++;
            }
        }, ct);

    public async Task<MobileSyncState> QueuePaymentDraftAsync(PaymentDto payment, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.Payments.RemoveAll(x => x.Id == payment.Id);
            state.PendingPush.Payments.Add(payment);
        }, ct);

    public async Task<MobileSyncState> QueueTransactionDraftAsync(TransactionDto transaction, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.Transactions.RemoveAll(x => x.Id == transaction.Id);
            state.PendingPush.Transactions.Add(transaction);
        }, ct);

    public async Task<MobileSyncState> QueueTransactionAttachmentDraftAsync(TransactionAttachmentDto attachment, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.TransactionAttachments.RemoveAll(x => x.Id == attachment.Id);
            state.PendingPush.TransactionAttachments.Add(attachment);
        }, ct);

    public async Task<MobileSyncState> QueueInventoryTransferDraftAsync(InventoryTransferDto transfer, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.InventoryTransfers.RemoveAll(x => x.Id == transfer.Id);
            state.PendingPush.InventoryTransfers.Add(transfer);
        }, ct);

    public async Task<MobileSyncState> QueueRentalManagementCompanyDraftAsync(RentalManagementCompanyDto company, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.RentalManagementCompanies.RemoveAll(x => x.Id == company.Id);
            state.PendingPush.RentalManagementCompanies.Add(company);
        }, ct);

    public async Task<MobileSyncState> QueueRentalBillingProfileDraftAsync(RentalBillingProfileDto profile, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.RentalBillingProfiles.RemoveAll(x => x.Id == profile.Id);
            state.PendingPush.RentalBillingProfiles.Add(profile);
        }, ct);

    public async Task<MobileSyncState> QueueRentalAssetDraftAsync(RentalAssetDto asset, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.RentalAssets.RemoveAll(x => x.Id == asset.Id);
            state.PendingPush.RentalAssets.Add(asset);
        }, ct);

    public async Task<MobileSyncState> QueueRentalAssetAssignmentHistoryDraftAsync(RentalAssetAssignmentHistoryDto history, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.RentalAssetAssignmentHistories.RemoveAll(x => x.Id == history.Id);
            state.PendingPush.RentalAssetAssignmentHistories.Add(history);
        }, ct);

    public async Task<MobileSyncState> QueueRentalBillingLogDraftAsync(RentalBillingLogDto log, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.RentalBillingLogs.RemoveAll(x => x.Id == log.Id);
            state.PendingPush.RentalBillingLogs.Add(log);
        }, ct);

    public async Task<MobileSyncState> QueuePaymentAttachmentsAsync(
        Guid paymentId,
        IEnumerable<PendingPaymentAttachmentRecord> attachments,
        CancellationToken ct = default)
    {
        var attachmentList = attachments.ToList();
        return await MutateStoredStateAsync(state =>
        {
            QueuePaymentAttachmentsForRetry(state, paymentId, attachmentList);
        }, ct);
    }

    private static void QueuePaymentAttachmentsForRetry(
        MobileSyncState state,
        Guid paymentId,
        IEnumerable<PendingPaymentAttachmentRecord> attachments)
    {
        foreach (var attachment in attachments)
        {
            attachment.PaymentId = paymentId;
            state.PendingPaymentAttachments.RemoveAll(x => x.LocalId == attachment.LocalId);
            state.PendingPaymentAttachments.Add(attachment);
        }
    }

    private async Task<MobileSyncState> MutateStoredStateAsync(Action<MobileSyncState> mutate, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            state.Normalize();
            mutate(state);
            await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(MobileSyncState state, CancellationToken ct)
    {
        await _store.SaveAsync(state, ct);
        await RemoveDiscardedPaymentAttachmentDraftsAsync(ct);
        await CleanupOrphanPaymentAttachmentDraftsAsync(state, ct);
    }

    private async Task CleanupOrphanPaymentAttachmentDraftsAsync(MobileSyncState state, CancellationToken ct)
    {
        try
        {
            await _attachmentStore.RemoveOrphanDraftsAsync(
                state.PendingPaymentAttachments,
                OrphanPaymentAttachmentDraftMinimumAge,
                ct);
        }
        catch (Exception ex)
        {
            MobileAppLogger.Warn("SYNC", $"고아 수금첨부 임시 파일 정리 실패: {ex.Message}");
        }
    }

    private void RemovePendingPaymentAttachments(
        MobileSyncState state,
        Predicate<PendingPaymentAttachmentRecord> predicate)
    {
        var removed = state.PendingPaymentAttachments
            .Where(attachment => predicate(attachment))
            .ToList();
        if (removed.Count == 0)
            return;

        var removedLocalIds = removed
            .Select(attachment => attachment.LocalId)
            .ToHashSet();
        state.PendingPaymentAttachments.RemoveAll(attachment => removedLocalIds.Contains(attachment.LocalId));
        QueueDiscardedPaymentAttachmentDrafts(removed);
    }

    private void QueueDiscardedPaymentAttachmentDrafts(IEnumerable<PendingPaymentAttachmentRecord> attachments)
    {
        foreach (var attachment in attachments)
        {
            if (attachment is null)
                continue;
            if (_discardedPaymentAttachmentDrafts.Any(current => current.LocalId == attachment.LocalId))
                continue;

            _discardedPaymentAttachmentDrafts.Add(attachment);
        }
    }

    private async Task<MobileSyncState> PullInternalAsync(MobileSyncState state, CancellationToken ct)
    {
        state.LastAttemptUtc = DateTime.UtcNow;

        try
        {
            var response = EnsurePullResponse(await _api.PullAsync(state.LastRevision, ct));
            await ApplyPullResponseAsync(state, response, ct);
        }
        catch (Exception ex)
        {
            MarkFailure(state, ex);
        }

        return state;
    }

    private async Task<MobileSyncState> PushInternalAsync(MobileSyncState state, CancellationToken ct)
    {
        state.LastAttemptUtc = DateTime.UtcNow;

        try
        {
            state.Normalize();
            var scopedPush = MobilePendingScopeFilter.CreateScopedPushRequest(_sessionStore.GetSnapshot(), state);
            if (HasPendingServerSyncPayload(scopedPush))
            {
                var result = EnsurePushResult(await _api.PushAsync(scopedPush, ct));
                state.LastRevision = Math.Max(state.LastRevision, result.CurrentServerRevision);
                if (result.ConflictCount > 0)
                {
                    RemoveAcceptedPendingMutations(state.PendingPush, result.AcceptedRevisions);
                    RemoveSubmittedItemWarehouseStocks(state.PendingPush, scopedPush, result);
                    await MarkPushConflictAndRefreshAsync(state, result, ct);
                    await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
                    return state;
                }

                RemoveSentScopedPendingMutations(state.PendingPush, scopedPush, result);
                if (!HasPendingServerSyncPayload(state))
                    state.PendingPush = new SyncPushRequest { DeviceId = state.DeviceId };
            }

            state.LastSuccessUtc = DateTime.UtcNow;
            state.LastError = string.Empty;
            state.LastFailureAllowsCachedDisplay = true;
            state.ConsecutiveFailureCount = 0;
            await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
            return state;
        }
        catch (Exception ex)
        {
            MarkFailure(state, ex);
            await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
            return state;
        }
    }

    private static SyncPushResult EnsurePushResult(SyncPushResult? result)
        => result ?? throw new HttpRequestException("동기화 push 응답이 비어 있어 서버 반영 여부를 확인할 수 없습니다.");

    private static SyncPullResponse EnsurePullResponse(SyncPullResponse? response)
        => response ?? throw new HttpRequestException("동기화 pull 응답이 비어 있어 최신 데이터 반영 여부를 확인할 수 없습니다.");

    private static SyncStatusDto EnsureSyncStatus(SyncStatusDto? status)
        => status ?? throw new HttpRequestException("동기화 상태 응답이 비어 있어 최신 데이터 여부를 확인할 수 없습니다.");

    private static T EnsureEntityResult<T>(T? result, string operationName)
        where T : SyncEntityDto
        => result ?? throw new HttpRequestException($"{operationName} 응답이 비어 있어 서버 반영 여부를 확인할 수 없습니다.");

    private static PaymentAttachmentDto EnsurePaymentAttachmentResult(PaymentAttachmentDto? result)
        => result ?? throw new HttpRequestException("첨부 업로드 응답이 비어 있어 서버 저장 여부를 확인할 수 없습니다.");

    private static void QueueUnacceptedLinkedPaymentConflict(
        SyncPushRequest pendingPush,
        PaymentDto payment,
        TransactionDto? linkedTransaction,
        IReadOnlyCollection<SyncAcceptedRevisionDto> acceptedRevisions)
    {
        pendingPush.Payments.RemoveAll(current => current.Id == payment.Id);
        if (!WasAccepted(acceptedRevisions, PaymentEntityName, payment.Id))
            pendingPush.Payments.Add(payment);

        if (linkedTransaction is null)
            return;

        pendingPush.Transactions.RemoveAll(current => current.Id == linkedTransaction.Id);
        if (!WasAccepted(acceptedRevisions, TransactionRecordEntityName, linkedTransaction.Id))
            pendingPush.Transactions.Add(linkedTransaction);
    }

    private static void RemoveAcceptedPendingMutations(
        SyncPushRequest pendingPush,
        IReadOnlyCollection<SyncAcceptedRevisionDto> acceptedRevisions)
    {
        if (acceptedRevisions.Count == 0)
            return;

        RemoveAccepted(pendingPush.CompanyProfiles, acceptedRevisions, CompanyProfileEntityName);
        RemoveAccepted(pendingPush.Units, acceptedRevisions, UnitEntityName);
        RemoveAccepted(pendingPush.CustomerCategories, acceptedRevisions, CustomerCategoryEntityName);
        RemoveAccepted(pendingPush.PriceGradeOptions, acceptedRevisions, PriceGradeOptionEntityName);
        RemoveAccepted(pendingPush.TradeTypeOptions, acceptedRevisions, TradeTypeOptionEntityName);
        RemoveAccepted(pendingPush.ItemCategoryOptions, acceptedRevisions, ItemCategoryOptionEntityName);
        RemoveAccepted(pendingPush.CustomerMasters, acceptedRevisions, CustomerMasterEntityName);
        RemoveAccepted(pendingPush.Customers, acceptedRevisions, CustomerEntityName);
        RemoveAccepted(pendingPush.CustomerContracts, acceptedRevisions, CustomerContractEntityName);
        RemoveAccepted(pendingPush.Items, acceptedRevisions, ItemEntityName);
        RemoveAccepted(pendingPush.Transactions, acceptedRevisions, TransactionRecordEntityName);
        RemoveAccepted(pendingPush.TransactionAttachments, acceptedRevisions, TransactionAttachmentEntityName);
        RemoveAccepted(pendingPush.InventoryTransfers, acceptedRevisions, InventoryTransferEntityName);
        RemoveAccepted(pendingPush.RentalManagementCompanies, acceptedRevisions, RentalManagementCompanyEntityName);
        RemoveAccepted(pendingPush.RentalBillingProfiles, acceptedRevisions, RentalBillingProfileEntityName);
        RemoveAccepted(pendingPush.RentalAssets, acceptedRevisions, RentalAssetEntityName);
        RemoveAccepted(pendingPush.RentalAssetAssignmentHistories, acceptedRevisions, RentalAssetAssignmentHistoryEntityName);
        RemoveAccepted(pendingPush.RentalBillingLogs, acceptedRevisions, RentalBillingLogEntityName);
        RemoveAccepted(pendingPush.Invoices, acceptedRevisions, InvoiceEntityName);
        RemoveAccepted(pendingPush.Payments, acceptedRevisions, PaymentEntityName);
    }

    private static void RemoveSentScopedPendingMutations(
        SyncPushRequest pendingPush,
        SyncPushRequest submittedPush,
        SyncPushResult result)
    {
        if (result.ConflictCount == 0)
        {
            RemoveSubmittedById(pendingPush.CompanyProfiles, submittedPush.CompanyProfiles);
            RemoveSubmittedById(pendingPush.Units, submittedPush.Units);
            RemoveSubmittedById(pendingPush.CustomerCategories, submittedPush.CustomerCategories);
            RemoveSubmittedById(pendingPush.PriceGradeOptions, submittedPush.PriceGradeOptions);
            RemoveSubmittedById(pendingPush.TradeTypeOptions, submittedPush.TradeTypeOptions);
            RemoveSubmittedById(pendingPush.ItemCategoryOptions, submittedPush.ItemCategoryOptions);
            RemoveSubmittedById(pendingPush.CustomerMasters, submittedPush.CustomerMasters);
            RemoveSubmittedById(pendingPush.Customers, submittedPush.Customers);
            RemoveSubmittedById(pendingPush.CustomerContracts, submittedPush.CustomerContracts);
            RemoveSubmittedById(pendingPush.Items, submittedPush.Items);
            RemoveSubmittedById(pendingPush.Transactions, submittedPush.Transactions);
            RemoveSubmittedById(pendingPush.TransactionAttachments, submittedPush.TransactionAttachments);
            RemoveSubmittedById(pendingPush.InventoryTransfers, submittedPush.InventoryTransfers);
            RemoveSubmittedById(pendingPush.RentalManagementCompanies, submittedPush.RentalManagementCompanies);
            RemoveSubmittedById(pendingPush.RentalBillingProfiles, submittedPush.RentalBillingProfiles);
            RemoveSubmittedById(pendingPush.RentalAssets, submittedPush.RentalAssets);
            RemoveSubmittedById(pendingPush.RentalAssetAssignmentHistories, submittedPush.RentalAssetAssignmentHistories);
            RemoveSubmittedById(pendingPush.RentalBillingLogs, submittedPush.RentalBillingLogs);
            RemoveSubmittedById(pendingPush.Invoices, submittedPush.Invoices);
            RemoveSubmittedById(pendingPush.Payments, submittedPush.Payments);
        }
        else
        {
            RemoveAcceptedPendingMutations(pendingPush, result.AcceptedRevisions);
        }

        RemoveSubmittedItemWarehouseStocks(pendingPush, submittedPush, result);
    }

    private static void RemoveSubmittedById<T>(List<T> pending, IEnumerable<T> submitted)
        where T : SyncEntityDto
    {
        var submittedIds = submitted
            .Where(entity => entity.Id != Guid.Empty)
            .Select(entity => entity.Id)
            .ToHashSet();

        if (submittedIds.Count == 0)
            return;

        pending.RemoveAll(entity => submittedIds.Contains(entity.Id));
    }

    private static void RemoveSubmittedItemWarehouseStocks(
        SyncPushRequest pendingPush,
        SyncPushRequest submittedPush,
        SyncPushResult result)
    {
        var conflictedStockKeys = result.Conflicts
            .Where(conflict => string.Equals(conflict.EntityName, ItemWarehouseStockEntityName, StringComparison.OrdinalIgnoreCase))
            .Select(conflict => conflict.EntityId?.Trim() ?? string.Empty)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var submittedStockKeys = submittedPush.ItemWarehouseStocks
            .Select(BuildItemWarehouseStockConflictKey)
            .Where(key => !string.IsNullOrWhiteSpace(key) && !conflictedStockKeys.Contains(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (submittedStockKeys.Count == 0)
            return;

        pendingPush.ItemWarehouseStocks.RemoveAll(stock => submittedStockKeys.Contains(BuildItemWarehouseStockConflictKey(stock)));
    }

    private static string BuildItemWarehouseStockConflictKey(ItemWarehouseStockDto stock)
        => $"{stock.ItemId:D}|{OfficeCodeCatalog.NormalizeWarehouseCodeLoose(stock.WarehouseCode)}";

    private static void RemoveAccepted<T>(
        List<T> pending,
        IReadOnlyCollection<SyncAcceptedRevisionDto> acceptedRevisions,
        string entityName)
        where T : SyncEntityDto
    {
        pending.RemoveAll(entity => WasAccepted(acceptedRevisions, entityName, entity.Id));
    }

    private static bool WasAccepted(
        IReadOnlyCollection<SyncAcceptedRevisionDto> acceptedRevisions,
        string entityName,
        Guid entityId)
        => acceptedRevisions.Any(revision =>
            revision.EntityId == entityId &&
            string.Equals(revision.EntityName, entityName, StringComparison.OrdinalIgnoreCase));

    private const string CompanyProfileEntityName = "CompanyProfile";
    private const string UnitEntityName = "Unit";
    private const string CustomerCategoryEntityName = "CustomerCategory";
    private const string PriceGradeOptionEntityName = "PriceGradeOption";
    private const string TradeTypeOptionEntityName = "TradeTypeOption";
    private const string ItemCategoryOptionEntityName = "ItemCategoryOption";
    private const string CustomerMasterEntityName = "CustomerMaster";
    private const string CustomerEntityName = "Customer";
    private const string CustomerContractEntityName = "CustomerContract";
    private const string ItemEntityName = "Item";
    private const string ItemWarehouseStockEntityName = "ItemWarehouseStock";
    private const string TransactionRecordEntityName = "TransactionRecord";
    private const string TransactionAttachmentEntityName = "TransactionAttachment";
    private const string InventoryTransferEntityName = "InventoryTransfer";
    private const string RentalManagementCompanyEntityName = "RentalManagementCompany";
    private const string RentalBillingProfileEntityName = "RentalBillingProfile";
    private const string RentalAssetEntityName = "RentalAsset";
    private const string RentalAssetAssignmentHistoryEntityName = "RentalAssetAssignmentHistory";
    private const string RentalBillingLogEntityName = "RentalBillingLog";
    private const string InvoiceEntityName = "Invoice";
    private const string PaymentEntityName = "Payment";

    private async Task<MobileSyncState> UploadPendingPaymentAttachmentsInternalAsync(
        MobileSyncState state,
        CancellationToken ct,
        IReadOnlySet<Guid>? additionalAllowedPaymentIds = null)
    {
        state.LastAttemptUtc = DateTime.UtcNow;
        var pending = MobilePendingScopeFilter
            .GetScopedPaymentAttachments(_sessionStore.GetSnapshot(), state, additionalAllowedPaymentIds)
            .ToList();
        if (pending.Count == 0)
        {
            state.LastError = string.Empty;
            state.LastFailureAllowsCachedDisplay = true;
            return state;
        }

        var uploadedIds = new List<Guid>();
        var errors = new List<string>();

        foreach (var attachment in pending)
        {
            try
            {
                if (attachment.PaymentId == Guid.Empty)
                {
                    uploadedIds.Add(attachment.LocalId);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(attachment.StoredPath) || !File.Exists(attachment.StoredPath))
                {
                    uploadedIds.Add(attachment.LocalId);
                    errors.Add($"첨부 파일을 찾을 수 없어 정리했습니다: {attachment.FileName}");
                    continue;
                }

                EnsurePaymentAttachmentResult(await _api.UploadPaymentAttachmentAsync(attachment.PaymentId, attachment, ct));
                uploadedIds.Add(attachment.LocalId);
            }
            catch (Exception ex) when (ShouldRetryPaymentAttachmentUpload(ex))
            {
                errors.Add(ex.Message);
            }
            catch (Exception ex)
            {
                uploadedIds.Add(attachment.LocalId);
                errors.Add(BuildTerminalPaymentAttachmentUploadFailureMessage(attachment, ex));
            }
        }

        RemovePendingPaymentAttachments(state, attachment => uploadedIds.Contains(attachment.LocalId));
        if (errors.Count == 0)
        {
            state.LastSuccessUtc = DateTime.UtcNow;
            state.LastError = string.Empty;
            state.LastFailureAllowsCachedDisplay = true;
            state.ConsecutiveFailureCount = 0;
        }
        else
        {
            state.LastError = errors[0];
            state.LastFailureAllowsCachedDisplay = true;
            state.ConsecutiveFailureCount++;
        }

        await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);
        return state;
    }

    private static void RestorePaymentAttachmentUploadErrorsAfterPull(
        MobileSyncState state,
        IReadOnlyList<string> attachmentUploadErrors)
    {
        if (attachmentUploadErrors.Count == 0)
            return;

        var attachmentError = BuildPaymentAttachmentUploadFailureMessage(attachmentUploadErrors);
        if (string.IsNullOrWhiteSpace(state.LastError))
        {
            state.LastError = attachmentError;
        }
        else
        {
            state.LastError = $"{attachmentError} 최신 데이터 새로고침 상태: {state.LastError}";
        }

        state.ConsecutiveFailureCount = Math.Max(1, state.ConsecutiveFailureCount);
        state.LastFailureAllowsCachedDisplay = true;
    }

    private static string BuildPaymentAttachmentUploadFailureMessage(IReadOnlyList<string> attachmentUploadErrors)
    {
        var firstError = attachmentUploadErrors
            .Select(error => error?.Trim())
            .FirstOrDefault(error => !string.IsNullOrWhiteSpace(error));
        var detail = string.IsNullOrWhiteSpace(firstError)
            ? string.Empty
            : $" 첫 오류: {firstError}";
        return $"수금/지급은 저장됐지만 첨부 {attachmentUploadErrors.Count:N0}건 업로드가 실패해 다음 동기화에서 다시 시도합니다.{detail}";
    }

    private static void RestoreTerminalPaymentAttachmentUploadErrorsAfterPull(
        MobileSyncState state,
        IReadOnlyList<string> terminalAttachmentUploadErrors)
    {
        if (terminalAttachmentUploadErrors.Count == 0)
            return;

        var attachmentError = BuildTerminalPaymentAttachmentUploadFailureSummary(terminalAttachmentUploadErrors);
        if (string.IsNullOrWhiteSpace(state.LastError))
        {
            state.LastError = attachmentError;
        }
        else
        {
            state.LastError = $"{attachmentError} 최신 데이터 새로고침 상태: {state.LastError}";
        }

        state.ConsecutiveFailureCount = Math.Max(1, state.ConsecutiveFailureCount);
        state.LastFailureAllowsCachedDisplay = true;
    }

    private static string BuildTerminalPaymentAttachmentUploadFailureSummary(IReadOnlyList<string> terminalAttachmentUploadErrors)
    {
        var firstError = terminalAttachmentUploadErrors
            .Select(error => error?.Trim())
            .FirstOrDefault(error => !string.IsNullOrWhiteSpace(error));
        var detail = string.IsNullOrWhiteSpace(firstError)
            ? string.Empty
            : $" 첫 오류: {firstError}";
        return $"수금/지급은 저장됐지만 첨부 {terminalAttachmentUploadErrors.Count:N0}건은 서버가 계속 받을 수 없어 재시도 대기에서 제외했습니다. 첨부를 확인한 뒤 다시 선택해 주세요.{detail}";
    }

    private static bool ShouldRetryPaymentAttachmentUpload(Exception ex)
        => MobileRetryableNetworkFailure.IsRetryable(ex) ||
           ex is MobileAuthenticationException ||
           ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized };

    private static string BuildTerminalPaymentAttachmentUploadFailureMessage(PendingPaymentAttachmentRecord attachment, Exception ex)
    {
        var fileName = Path.GetFileName(attachment.FileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "첨부파일";

        var detail = TranslateFailureMessage(ex);
        return string.IsNullOrWhiteSpace(detail)
            ? $"첨부 업로드를 계속 재시도하지 않도록 대기 목록에서 제외했습니다: {fileName}. 첨부를 확인한 뒤 다시 선택해 주세요."
            : $"첨부 업로드를 계속 재시도하지 않도록 대기 목록에서 제외했습니다: {fileName}. {detail} 첨부를 확인한 뒤 다시 선택해 주세요.";
    }

    private async Task RemoveDiscardedPaymentAttachmentDraftsAsync(CancellationToken ct)
    {
        if (_discardedPaymentAttachmentDrafts.Count == 0)
            return;

        var attachments = _discardedPaymentAttachmentDrafts
            .GroupBy(attachment => attachment.LocalId)
            .Select(group => group.First())
            .ToList();
        _discardedPaymentAttachmentDrafts.Clear();

        foreach (var attachment in attachments)
        {
            try
            {
                await _attachmentStore.RemoveAsync(attachment, ct);
            }
            catch (Exception ex)
            {
                MobileAppLogger.Warn("SYNC", $"제거된 첨부 임시 파일 정리 실패: {attachment.FileName} / {ex.Message}");
            }
        }
    }

    private async Task MarkConcurrencyConflictAndRefreshAsync(MobileSyncState state, Exception ex, CancellationToken ct)
    {
        var message = BuildConcurrencyConflictMessage(ex);
        await RefreshLatestAfterConflictAsync(state, message, ct);
    }

    private async Task MarkPushConflictAndRefreshAsync(MobileSyncState state, SyncPushResult result, CancellationToken ct)
    {
        var message = BuildPushConflictMessage(result);
        await RefreshLatestAfterConflictAsync(state, message, ct);
    }

    private async Task RefreshLatestAfterConflictAsync(MobileSyncState state, string message, CancellationToken ct)
    {
        var refreshError = string.Empty;
        try
        {
            var response = await _api.PullAsync(0, ct);
            if (response is not null)
                await ApplyPullResponseAsync(state, response, ct);
        }
        catch (Exception refreshEx)
        {
            refreshError = TranslateFailureMessage(refreshEx);
        }

        state.LastError = string.IsNullOrWhiteSpace(refreshError)
            ? message
            : $"{message} 최신 데이터 새로고침은 실패했습니다: {refreshError}";
        state.LastFailureAllowsCachedDisplay = true;
        state.ConsecutiveFailureCount = 0;
        MobileAppLogger.Warn("SYNC", $"모바일 동시 수정 충돌: {state.LastError}");
    }

    private static string BuildConcurrencyConflictMessage(Exception ex)
    {
        var detail = ex.Message?.Trim();
        if (string.IsNullOrWhiteSpace(detail))
            return ConcurrencyConflictUserMessage;

        return detail.Length > 160
            ? $"{ConcurrencyConflictUserMessage} ({detail[..160]}...)"
            : $"{ConcurrencyConflictUserMessage} ({detail})";
    }

    private static string BuildPushConflictMessage(SyncPushResult result)
    {
        var firstConflict = result.Conflicts.FirstOrDefault();
        var reason = ApiConflictReasonTranslator.ToUserMessage(firstConflict?.Reason);
        if (string.IsNullOrWhiteSpace(reason))
            return $"{ConcurrencyConflictUserMessage} (충돌 {result.ConflictCount:N0}건)";

        return reason.Length > 160
            ? $"{ConcurrencyConflictUserMessage} ({reason[..160]}...)"
            : $"{ConcurrencyConflictUserMessage} ({reason})";
    }

    private static bool IsConcurrencyConflict(Exception ex)
        => ex is HttpRequestException { StatusCode: HttpStatusCode.Conflict };

    private static bool IsNonRetryableClientFailure(Exception ex)
        => ex is HttpRequestException
        {
            StatusCode: HttpStatusCode.BadRequest
                or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound
                or HttpStatusCode.UnprocessableEntity
        };

    private static void MarkFailure(MobileSyncState state, Exception ex)
    {
        state.LastError = TranslateFailureMessage(ex);
        state.LastFailureAllowsCachedDisplay = MobileRetryableNetworkFailure.IsRetryable(ex) || IsConcurrencyConflict(ex);
        state.ConsecutiveFailureCount++;
        MobileAppLogger.Warn("SYNC", $"모바일 동기화 실패: {state.LastError}");
    }

    private static string TranslateFailureMessage(Exception ex)
    {
        return ex switch
        {
            MobileAuthenticationException => "인증이 만료되었거나 복구되지 않았습니다. 다시 로그인해 주세요.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.Unauthorized
                => "인증이 만료되었거나 복구되지 않았습니다. 다시 로그인해 주세요.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.InternalServerError
                => "서버 오류(500)가 발생했습니다. 잠시 후 다시 시도해 주세요.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.Conflict
                => ConcurrencyConflictUserMessage,
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.BadRequest
                => BuildClientValidationMessage(httpEx),
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.Forbidden
                => "현재 계정 권한 또는 지점 범위 때문에 저장할 수 없습니다. 담당지점/권한을 확인해 주세요.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.NotFound
                => "저장 대상 데이터를 찾지 못했습니다. 최신 데이터를 다시 불러온 뒤 시도해 주세요.",
            HttpRequestException httpEx when httpEx.StatusCode.HasValue
                => $"서버 요청에 실패했습니다. ({(int)httpEx.StatusCode.Value} {httpEx.StatusCode.Value})",
            TaskCanceledException or TimeoutException
                => "네트워크 응답이 지연되고 있습니다. 잠시 후 다시 시도해 주세요.",
            HttpRequestException
                => "네트워크 연결을 확인한 후 다시 시도해 주세요.",
            _ => string.IsNullOrWhiteSpace(ex.Message)
                ? "알 수 없는 오류가 발생했습니다. 잠시 후 다시 시도해 주세요."
                : ex.Message
        };
    }

    private static string BuildClientValidationMessage(HttpRequestException httpEx)
    {
        var detail = ExtractHttpErrorDetail(httpEx);
        return string.IsNullOrWhiteSpace(detail)
            ? "저장할 수 없습니다. 입력값, 거래처, 품목, 재고 상태를 확인해 주세요."
            : $"저장할 수 없습니다. {detail}";
    }

    private static string ExtractHttpErrorDetail(HttpRequestException httpEx)
    {
        var message = httpEx.Message?.Trim() ?? string.Empty;
        if (message.Length == 0)
            return string.Empty;

        var body = message;
        if (body.StartsWith("400", StringComparison.Ordinal))
        {
            body = body[3..].TrimStart();
            if (body.StartsWith("Bad Request", StringComparison.OrdinalIgnoreCase))
                body = body["Bad Request".Length..].TrimStart();
            else if (body.StartsWith("BadRequest", StringComparison.OrdinalIgnoreCase))
                body = body["BadRequest".Length..].TrimStart();
        }

        if (body.Length == 0)
            return string.Empty;

        if (body[0] == '{')
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("message", out var messageProperty) &&
                    messageProperty.ValueKind == JsonValueKind.String)
                {
                    var parsed = messageProperty.GetString();
                    if (!string.IsNullOrWhiteSpace(parsed))
                        return TrimForStatus(parsed);
                }
            }
            catch (JsonException)
            {
                // Fall through to a plain-text fallback.
            }
        }

        if (body.StartsWith("<", StringComparison.Ordinal))
            return string.Empty;

        return TrimForStatus(body);
    }

    private static string TrimForStatus(string value)
    {
        var normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 160 ? normalized : normalized[..160] + "...";
    }

    private static bool HasPendingServerSyncPayload(MobileSyncState state)
    {
        state.Normalize();
        return HasPendingServerSyncPayload(state.PendingPush);
    }

    private static bool HasPendingServerSyncPayload(SyncPushRequest pendingPush)
        => (pendingPush.CompanyProfiles?.Count ?? 0) > 0
            || (pendingPush.Units?.Count ?? 0) > 0
            || (pendingPush.CustomerCategories?.Count ?? 0) > 0
            || (pendingPush.PriceGradeOptions?.Count ?? 0) > 0
            || (pendingPush.TradeTypeOptions?.Count ?? 0) > 0
            || (pendingPush.ItemCategoryOptions?.Count ?? 0) > 0
            || (pendingPush.CustomerMasters?.Count ?? 0) > 0
            || (pendingPush.Customers?.Count ?? 0) > 0
            || (pendingPush.CustomerContracts?.Count ?? 0) > 0
            || (pendingPush.Items?.Count ?? 0) > 0
            || (pendingPush.ItemWarehouseStocks?.Count ?? 0) > 0
            || (pendingPush.Transactions?.Count ?? 0) > 0
            || (pendingPush.TransactionAttachments?.Count ?? 0) > 0
            || (pendingPush.InventoryTransfers?.Count ?? 0) > 0
            || (pendingPush.RentalManagementCompanies?.Count ?? 0) > 0
            || (pendingPush.RentalBillingProfiles?.Count ?? 0) > 0
            || (pendingPush.RentalAssets?.Count ?? 0) > 0
            || (pendingPush.RentalAssetAssignmentHistories?.Count ?? 0) > 0
            || (pendingPush.RentalBillingLogs?.Count ?? 0) > 0
            || (pendingPush.Invoices?.Count ?? 0) > 0
            || (pendingPush.Payments?.Count ?? 0) > 0;

    private async Task ApplyPullResponseAsync(MobileSyncState state, SyncPullResponse response, CancellationToken ct)
    {
        state.Normalize();
        state.LastRevision = Math.Max(state.LastRevision, response.CurrentServerRevision);
        state.LastSuccessUtc = DateTime.UtcNow;
        state.LastError = string.Empty;
        state.LastFailureAllowsCachedDisplay = true;
        state.ConsecutiveFailureCount = 0;
        state.LastPulledCustomerCount = response.Customers.Count;
        state.LastPulledItemCount = response.Items.Count;
        state.LastPulledItemWarehouseStockCount = response.ItemWarehouseStocks.Count;
        state.LastPulledPriceGradeOptionCount = response.PriceGradeOptions.Count;
        state.LastPulledInvoiceCount = response.Invoices.Count;
        state.LastPulledPaymentCount = response.Payments.Count;
        state.LastPulledTransactionCount = response.Transactions.Count;
        state.LastPulledTransactionAttachmentCount = response.TransactionAttachments.Count;
        state.LastPulledInventoryTransferCount = response.InventoryTransfers.Count;
        state.LastPulledRentalManagementCompanyCount = response.RentalManagementCompanies.Count;
        state.LastPulledRentalBillingProfileCount = response.RentalBillingProfiles.Count;
        state.LastPulledRentalAssetCount = response.RentalAssets.Count;
        state.LastPulledRentalAssetAssignmentHistoryCount = response.RentalAssetAssignmentHistories.Count;
        state.LastPulledRentalBillingLogCount = response.RentalBillingLogs.Count;
        state.SyncedCustomers = MergeById(state.SyncedCustomers, response.Customers);
        state.SyncedItems = MergeById(state.SyncedItems, response.Items);
        state.SyncedItemWarehouseStocks = ReplaceItemWarehouseStocks(response.ItemWarehouseStocks);
        state.SyncedPriceGradeOptions = MergeById(state.SyncedPriceGradeOptions, response.PriceGradeOptions);
        state.SyncedInvoices = MergeById(state.SyncedInvoices, response.Invoices);
        state.SyncedPayments = MergeById(state.SyncedPayments, response.Payments);
        state.SyncedTransactions = MergeById(state.SyncedTransactions, response.Transactions);
        state.SyncedTransactionAttachments = MergeById(state.SyncedTransactionAttachments, response.TransactionAttachments);
        state.SyncedInventoryTransfers = MergeById(state.SyncedInventoryTransfers, response.InventoryTransfers);
        state.SyncedRentalManagementCompanies = MergeById(state.SyncedRentalManagementCompanies, response.RentalManagementCompanies);
        state.SyncedRentalBillingProfiles = MergeById(state.SyncedRentalBillingProfiles, response.RentalBillingProfiles);
        state.SyncedRentalAssets = MergeById(state.SyncedRentalAssets, response.RentalAssets);
        state.SyncedRentalAssetAssignmentHistories = MergeById(state.SyncedRentalAssetAssignmentHistories, response.RentalAssetAssignmentHistories);
        state.SyncedRentalBillingLogs = MergeById(state.SyncedRentalBillingLogs, response.RentalBillingLogs);
        await ApplyPurgeRecordsAsync(state, response.PurgeRecords, ct);
    }

    private static List<T> MergeById<T>(IEnumerable<T>? existing, IEnumerable<T>? incoming) where T : SyncEntityDto
    {
        var map = (existing ?? Enumerable.Empty<T>())
            .Where(item => item.Id != Guid.Empty && !item.IsDeleted)
            .ToDictionary(item => item.Id, item => item);

        foreach (var item in incoming ?? Enumerable.Empty<T>())
        {
            if (item.Id == Guid.Empty)
                continue;

            if (item.IsDeleted)
                map.Remove(item.Id);
            else
                map[item.Id] = item;
        }

        return map.Values.ToList();
    }

    private static List<ItemWarehouseStockDto> ReplaceItemWarehouseStocks(IEnumerable<ItemWarehouseStockDto>? incoming)
        => (incoming ?? Enumerable.Empty<ItemWarehouseStockDto>())
            .Where(stock => stock.ItemId != Guid.Empty && !string.IsNullOrWhiteSpace(stock.WarehouseCode))
            .GroupBy(
                stock => (stock.ItemId, WarehouseCode: stock.WarehouseCode.Trim()),
                stock => stock,
                EqualityComparer<(Guid ItemId, string WarehouseCode)>.Default)
            .Select(group => group
                .OrderByDescending(stock => stock.Revision)
                .ThenByDescending(stock => stock.UpdatedAtUtc)
                .First())
            .OrderBy(stock => stock.ItemId)
            .ThenBy(stock => stock.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task ApplyPurgeRecordsAsync(MobileSyncState state, IEnumerable<RecycleBinPurgeRecordDto>? purgeRecords, CancellationToken ct)
    {
        var records = (purgeRecords ?? Enumerable.Empty<RecycleBinPurgeRecordDto>())
            .Where(record => record.EntityId != Guid.Empty && !string.IsNullOrWhiteSpace(record.Kind))
            .GroupBy(record => (Kind: NormalizePurgeRecordKind(record.Kind), record.EntityId))
            .Select(group => group
                .OrderByDescending(record => record.PurgedAtUtc)
                .ThenByDescending(record => record.Revision)
                .First())
            .OrderBy(record => GetPurgeApplyOrder(NormalizePurgeRecordKind(record.Kind)))
            .ToList();

        if (records.Count == 0)
            return;

        state.Normalize();
        foreach (var record in records)
            await ApplyPurgeRecordAsync(state, NormalizePurgeRecordKind(record.Kind), record.EntityId, record.Revision, ct);
    }

    private async Task ApplyPurgeRecordAsync(MobileSyncState state, string normalizedKind, Guid entityId, long purgeRevision, CancellationToken ct)
    {
        switch (normalizedKind)
        {
            case "companyprofile":
            case "company-profile":
                RemoveEntityById(state.PendingPush.CompanyProfiles, entityId, purgeRevision);
                break;
            case "customercategory":
            case "customer-category":
                RemoveEntityById(state.PendingPush.CustomerCategories, entityId, purgeRevision);
                break;
            case "tradetypeoption":
            case "trade-type-option":
                RemoveEntityById(state.PendingPush.TradeTypeOptions, entityId, purgeRevision);
                break;
            case "itemcategoryoption":
            case "item-category-option":
                RemoveEntityById(state.PendingPush.ItemCategoryOptions, entityId, purgeRevision);
                break;
            case "customer":
                RemoveEntityById(state.SyncedCustomers, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.Customers, entityId, purgeRevision);
                RemoveCustomerContractsForPurgedCustomer(state.PendingPush.CustomerContracts, entityId, purgeRevision);
                await _contractCacheStore.RemoveCustomerAsync(entityId, purgeRevision, ct);
                ClearRentalAssignmentHistoryCustomerReferences(state.SyncedRentalAssetAssignmentHistories, entityId, purgeRevision);
                ClearRentalAssignmentHistoryCustomerReferences(state.PendingPush.RentalAssetAssignmentHistories, entityId, purgeRevision);
                break;
            case "contract":
            case "customercontract":
            case "customer-contract":
                RemoveEntityById(state.PendingPush.CustomerContracts, entityId, purgeRevision);
                await _contractCacheStore.RemoveContractAsync(entityId, purgeRevision, ct);
                break;
            case "item":
                RemoveEntityById(state.SyncedItems, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.Items, entityId, purgeRevision);
                RemoveItemWarehouseStocksForPurgedItem(state.SyncedItemWarehouseStocks, entityId, purgeRevision);
                RemoveItemWarehouseStocksForPurgedItem(state.PendingPush.ItemWarehouseStocks, entityId, purgeRevision);
                ClearInvoiceLineItemReferences(state.SyncedInvoices, entityId, purgeRevision);
                ClearInvoiceLineItemReferences(state.PendingPush.Invoices, entityId, purgeRevision);
                ClearInventoryTransferLineItemReferences(state.SyncedInventoryTransfers, entityId, purgeRevision);
                ClearInventoryTransferLineItemReferences(state.PendingPush.InventoryTransfers, entityId, purgeRevision);
                ClearRentalAssetItemReferences(state.SyncedRentalAssets, entityId, purgeRevision);
                ClearRentalAssetItemReferences(state.PendingPush.RentalAssets, entityId, purgeRevision);
                RemoveItemIdFromBillingTemplates(state.SyncedRentalBillingProfiles, entityId, purgeRevision);
                RemoveItemIdFromBillingTemplates(state.PendingPush.RentalBillingProfiles, entityId, purgeRevision);
                break;
            case "pricegradeoption":
            case "price-grade-option":
                RemoveEntityById(state.SyncedPriceGradeOptions, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.PriceGradeOptions, entityId, purgeRevision);
                break;
            case "invoice":
                RemoveEntityById(state.SyncedInvoices, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.Invoices, entityId, purgeRevision);
                var removedPaymentIds = new HashSet<Guid>();
                RemovePaymentsForPurgedInvoice(state.SyncedPayments, entityId, purgeRevision, removedPaymentIds);
                RemovePaymentsForPurgedInvoice(state.PendingPush.Payments, entityId, purgeRevision, removedPaymentIds);
                RemovePendingPaymentAttachments(state, attachment => removedPaymentIds.Contains(attachment.PaymentId));
                var removedTransactionIds = new HashSet<Guid>();
                RemoveTransactionsForPurgedInvoice(state.SyncedTransactions, entityId, purgeRevision, removedTransactionIds);
                RemoveTransactionsForPurgedInvoice(state.PendingPush.Transactions, entityId, purgeRevision, removedTransactionIds);
                state.SyncedTransactionAttachments.RemoveAll(attachment => removedTransactionIds.Contains(attachment.TransactionId) && !IsEntityNewerThanPurge(attachment, purgeRevision));
                state.PendingPush.TransactionAttachments.RemoveAll(attachment => removedTransactionIds.Contains(attachment.TransactionId) && !IsEntityNewerThanPurge(attachment, purgeRevision));
                break;
            case "payment":
                RemoveEntityById(state.SyncedPayments, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.Payments, entityId, purgeRevision);
                RemovePendingPaymentAttachments(state, attachment => attachment.PaymentId == entityId);
                RemoveEntityById(state.SyncedTransactions, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.Transactions, entityId, purgeRevision);
                state.SyncedTransactionAttachments.RemoveAll(attachment => attachment.TransactionId == entityId && !IsEntityNewerThanPurge(attachment, purgeRevision));
                state.PendingPush.TransactionAttachments.RemoveAll(attachment => attachment.TransactionId == entityId && !IsEntityNewerThanPurge(attachment, purgeRevision));
                break;
            case "transaction":
                RemoveEntityById(state.SyncedTransactions, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.Transactions, entityId, purgeRevision);
                var transactionRemovedPaymentIds = new HashSet<Guid>();
                RemovePaymentForPurgedTransaction(state.SyncedPayments, entityId, purgeRevision, transactionRemovedPaymentIds);
                RemovePaymentForPurgedTransaction(state.PendingPush.Payments, entityId, purgeRevision, transactionRemovedPaymentIds);
                RemovePendingPaymentAttachments(state, attachment => transactionRemovedPaymentIds.Contains(attachment.PaymentId));
                state.SyncedTransactionAttachments.RemoveAll(attachment => attachment.TransactionId == entityId && !IsEntityNewerThanPurge(attachment, purgeRevision));
                state.PendingPush.TransactionAttachments.RemoveAll(attachment => attachment.TransactionId == entityId && !IsEntityNewerThanPurge(attachment, purgeRevision));
                break;
            case "inventorytransfer":
            case "inventory-transfer":
                RemoveEntityById(state.SyncedInventoryTransfers, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.InventoryTransfers, entityId, purgeRevision);
                break;
            case "rentalmanagementcompany":
            case "rental-management-company":
                RemoveEntityById(state.SyncedRentalManagementCompanies, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.RentalManagementCompanies, entityId, purgeRevision);
                break;
            case "rentalbillingprofile":
            case "rental-billing-profile":
                RemoveEntityById(state.SyncedRentalBillingProfiles, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.RentalBillingProfiles, entityId, purgeRevision);
                state.SyncedRentalBillingLogs.RemoveAll(log => log.BillingProfileId == entityId && !IsEntityNewerThanPurge(log, purgeRevision));
                state.PendingPush.RentalBillingLogs.RemoveAll(log => log.BillingProfileId == entityId && !IsEntityNewerThanPurge(log, purgeRevision));
                ClearRentalBillingProfileReferences(state.SyncedRentalAssets, entityId, purgeRevision);
                ClearRentalBillingProfileReferences(state.PendingPush.RentalAssets, entityId, purgeRevision);
                ClearRentalAssignmentHistoryBillingProfileReferences(state.SyncedRentalAssetAssignmentHistories, entityId, purgeRevision);
                ClearRentalAssignmentHistoryBillingProfileReferences(state.PendingPush.RentalAssetAssignmentHistories, entityId, purgeRevision);
                break;
            case "rentalasset":
            case "rental-asset":
                RemoveEntityById(state.SyncedRentalAssets, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.RentalAssets, entityId, purgeRevision);
                RemoveIncludedAssetIdFromBillingTemplates(state.SyncedRentalBillingProfiles, entityId, purgeRevision);
                RemoveIncludedAssetIdFromBillingTemplates(state.PendingPush.RentalBillingProfiles, entityId, purgeRevision);
                state.SyncedRentalAssetAssignmentHistories.RemoveAll(history => history.AssetId == entityId && !IsEntityNewerThanPurge(history, purgeRevision));
                state.PendingPush.RentalAssetAssignmentHistories.RemoveAll(history => history.AssetId == entityId && !IsEntityNewerThanPurge(history, purgeRevision));
                break;
            case "rentalbillinglog":
            case "rental-billing-log":
                RemoveEntityById(state.SyncedRentalBillingLogs, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.RentalBillingLogs, entityId, purgeRevision);
                break;
        }
    }

    private static void RemoveEntityById<T>(List<T> values, Guid entityId, long purgeRevision)
        where T : SyncEntityDto
    {
        if (values.Any(value => value.Id == entityId && IsEntityNewerThanPurge(value, purgeRevision)))
            return;

        values.RemoveAll(value => value.Id == entityId);
    }

    private static void RemoveCustomerContractsForPurgedCustomer(
        List<CustomerContractDto> values,
        Guid customerId,
        long purgeRevision)
    {
        if (customerId == Guid.Empty)
            return;

        values.RemoveAll(contract =>
            contract.CustomerId == customerId &&
            !IsEntityNewerThanPurge(contract, purgeRevision));
    }

    private static void RemoveItemWarehouseStocksForPurgedItem(
        List<ItemWarehouseStockDto> values,
        Guid itemId,
        long purgeRevision)
    {
        if (itemId == Guid.Empty)
            return;

        values.RemoveAll(stock =>
            stock.ItemId == itemId &&
            !IsItemWarehouseStockNewerThanPurge(stock, purgeRevision));
    }

    private static void ClearRentalBillingProfileReferences(
        List<RentalAssetDto> values,
        Guid profileId,
        long purgeRevision)
    {
        foreach (var value in values)
        {
            if (IsEntityNewerThanPurge(value, purgeRevision) || value.BillingProfileId != profileId)
                continue;

            value.BillingProfileId = null;
            value.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static void ClearRentalAssignmentHistoryBillingProfileReferences(
        List<RentalAssetAssignmentHistoryDto> values,
        Guid profileId,
        long purgeRevision)
    {
        foreach (var value in values)
        {
            if (IsEntityNewerThanPurge(value, purgeRevision) || value.BillingProfileId != profileId)
                continue;

            value.BillingProfileId = null;
            value.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static void ClearRentalAssignmentHistoryCustomerReferences(
        List<RentalAssetAssignmentHistoryDto> values,
        Guid customerId,
        long purgeRevision)
    {
        foreach (var value in values)
        {
            if (IsEntityNewerThanPurge(value, purgeRevision) || value.CustomerId != customerId)
                continue;

            value.CustomerId = null;
            value.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static void RemovePaymentsForPurgedInvoice(
        List<PaymentDto> values,
        Guid invoiceId,
        long purgeRevision,
        ISet<Guid> removedPaymentIds)
    {
        if (invoiceId == Guid.Empty)
            return;

        var matchingIds = values
            .Where(payment => payment.InvoiceId == invoiceId && !IsEntityNewerThanPurge(payment, purgeRevision))
            .Select(payment => payment.Id)
            .ToHashSet();
        if (matchingIds.Count == 0)
            return;

        foreach (var paymentId in matchingIds)
            removedPaymentIds.Add(paymentId);

        values.RemoveAll(payment => matchingIds.Contains(payment.Id));
    }

    private static void RemovePaymentForPurgedTransaction(
        List<PaymentDto> values,
        Guid transactionId,
        long purgeRevision,
        ISet<Guid> removedPaymentIds)
    {
        if (transactionId == Guid.Empty)
            return;

        var matchingIds = values
            .Where(payment => payment.Id == transactionId && !IsEntityNewerThanPurge(payment, purgeRevision))
            .Select(payment => payment.Id)
            .ToHashSet();
        if (matchingIds.Count == 0)
            return;

        foreach (var paymentId in matchingIds)
            removedPaymentIds.Add(paymentId);

        values.RemoveAll(payment => matchingIds.Contains(payment.Id));
    }

    private static void RemoveTransactionsForPurgedInvoice(
        List<TransactionDto> values,
        Guid invoiceId,
        long purgeRevision,
        ISet<Guid> removedTransactionIds)
    {
        if (invoiceId == Guid.Empty)
            return;

        var matchingIds = values
            .Where(transaction => transaction.LinkedInvoiceId == invoiceId && !IsEntityNewerThanPurge(transaction, purgeRevision))
            .Select(transaction => transaction.Id)
            .ToHashSet();
        if (matchingIds.Count == 0)
            return;

        foreach (var transactionId in matchingIds)
            removedTransactionIds.Add(transactionId);

        values.RemoveAll(transaction => matchingIds.Contains(transaction.Id));
    }

    private static void ClearInvoiceLineItemReferences(
        List<InvoiceDto> values,
        Guid itemId,
        long purgeRevision)
    {
        if (itemId == Guid.Empty)
            return;

        foreach (var value in values)
        {
            if (IsEntityNewerThanPurge(value, purgeRevision))
                continue;

            var changed = false;
            foreach (var line in value.Lines)
            {
                if (line.ItemId != itemId)
                    continue;

                line.ItemId = null;
                changed = true;
            }

            if (changed)
                value.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static void ClearInventoryTransferLineItemReferences(
        List<InventoryTransferDto> values,
        Guid itemId,
        long purgeRevision)
    {
        if (itemId == Guid.Empty)
            return;

        foreach (var value in values)
        {
            if (IsEntityNewerThanPurge(value, purgeRevision))
                continue;

            var changed = false;
            foreach (var line in value.Lines)
            {
                if (line.ItemId != itemId)
                    continue;

                line.ItemId = null;
                changed = true;
            }

            if (changed)
                value.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static void ClearRentalAssetItemReferences(
        List<RentalAssetDto> values,
        Guid itemId,
        long purgeRevision)
    {
        if (itemId == Guid.Empty)
            return;

        foreach (var value in values)
        {
            if (IsEntityNewerThanPurge(value, purgeRevision) || value.ItemId != itemId)
                continue;

            value.ItemId = null;
            value.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static void RemoveItemIdFromBillingTemplates(
        List<RentalBillingProfileDto> values,
        Guid itemId,
        long purgeRevision)
    {
        if (itemId == Guid.Empty)
            return;

        foreach (var value in values)
        {
            if (IsEntityNewerThanPurge(value, purgeRevision))
                continue;

            var normalizedJson = RemoveItemId(value.BillingTemplateJson, itemId);
            if (string.Equals(normalizedJson, value.BillingTemplateJson, StringComparison.Ordinal))
                continue;

            value.BillingTemplateJson = normalizedJson;
            value.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static void RemoveIncludedAssetIdFromBillingTemplates(
        List<RentalBillingProfileDto> values,
        Guid assetId,
        long purgeRevision)
    {
        if (assetId == Guid.Empty)
            return;

        foreach (var value in values)
        {
            if (IsEntityNewerThanPurge(value, purgeRevision))
                continue;

            var normalizedJson = RemoveIncludedAssetId(value.BillingTemplateJson, assetId);
            if (string.Equals(normalizedJson, value.BillingTemplateJson, StringComparison.Ordinal))
                continue;

            value.BillingTemplateJson = normalizedJson;
            value.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static string RemoveItemId(string? templateJson, Guid itemId)
    {
        if (itemId == Guid.Empty)
            return templateJson ?? "[]";

        try
        {
            var root = JsonNode.Parse(templateJson ?? "[]");
            if (root is not JsonArray items)
                return templateJson ?? "[]";

            foreach (var item in items.OfType<JsonObject>())
            {
                if (IsMatchingJsonGuid(item["ItemId"], itemId))
                    item["ItemId"] = Guid.Empty.ToString("D");
            }

            return items.ToJsonString();
        }
        catch
        {
            return templateJson ?? "[]";
        }
    }

    private static string RemoveIncludedAssetId(string? templateJson, Guid assetId)
    {
        if (assetId == Guid.Empty)
            return templateJson ?? "[]";

        try
        {
            var root = JsonNode.Parse(templateJson ?? "[]");
            if (root is not JsonArray items)
                return templateJson ?? "[]";

            foreach (var item in items.OfType<JsonObject>())
            {
                if (item["IncludedAssetIds"] is not JsonArray includedAssetIds)
                    continue;

                for (var index = includedAssetIds.Count - 1; index >= 0; index--)
                {
                    if (IsMatchingJsonGuid(includedAssetIds[index], assetId))
                        includedAssetIds.RemoveAt(index);
                }
            }

            return items.ToJsonString();
        }
        catch
        {
            return templateJson ?? "[]";
        }
    }

    private static bool IsMatchingJsonGuid(JsonNode? value, Guid expected)
    {
        if (value is not JsonValue jsonValue)
            return false;

        if (jsonValue.TryGetValue<Guid>(out var guid))
            return guid == expected;

        if (jsonValue.TryGetValue<string>(out var text) &&
            Guid.TryParse(text, out var parsed))
            return parsed == expected;

        return false;
    }

    private static bool IsEntityNewerThanPurge(SyncEntityDto entity, long purgeRevision)
        => !entity.IsDeleted && entity.Revision > purgeRevision;

    private static bool IsItemWarehouseStockNewerThanPurge(ItemWarehouseStockDto stock, long purgeRevision)
        => stock.Revision > purgeRevision;

    private static string NormalizePurgeRecordKind(string? kind)
        => (kind ?? string.Empty).Trim().ToLowerInvariant();

    private static int GetPurgeApplyOrder(string normalizedKind)
        => normalizedKind switch
        {
            "payment" => 0,
            "transaction" => 1,
            "rental-billing-log" => 2,
            "rentalbillinglog" => 2,
            "contract" => 3,
            "invoice" => 4,
            "inventory-transfer" => 4,
            "inventorytransfer" => 4,
            "rental-asset" => 5,
            "rentalasset" => 5,
            "item" => 6,
            "rental-billing-profile" => 7,
            "rentalbillingprofile" => 7,
            "rental-management-company" => 7,
            "rentalmanagementcompany" => 7,
            "customer" => 8,
            "company-profile" => 9,
            "companyprofile" => 9,
            "customer-category" => 10,
            "customercategory" => 10,
            "price-grade-option" => 10,
            "pricegradeoption" => 10,
            "trade-type-option" => 10,
            "tradetypeoption" => 10,
            "item-category-option" => 10,
            "itemcategoryoption" => 10,
            _ => 99
        };
}
