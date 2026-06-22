using System.Net;
using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class SyncCoordinator
{
    private readonly JsonSyncStateStore _store;
    private readonly GeoraePlanApiClient _api;
    private readonly PaymentAttachmentDraftStore _attachmentStore;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public const string ConcurrencyConflictUserMessage = "다른 PC/모바일에서 먼저 수정되어 최신값을 다시 불러왔습니다. 내용을 확인한 뒤 다시 저장해 주세요.";

    public SyncCoordinator(JsonSyncStateStore store, GeoraePlanApiClient api, PaymentAttachmentDraftStore attachmentStore)
    {
        _store = store;
        _api = api;
        _attachmentStore = attachmentStore;
    }

    public static bool IsConcurrencyConflictState(MobileSyncState state)
        => state is not null && IsConcurrencyConflictMessage(state.LastError);

    public static bool IsConcurrencyConflictMessage(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           message.Contains("다른 PC/모바일에서 먼저 수정", StringComparison.Ordinal);

    public Task<MobileSyncState> LoadAsync(CancellationToken ct = default)
        => _store.LoadAsync(ct);

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
                ApplyPullResponse(state, response);
            }
            catch (Exception ex)
            {
                MarkFailure(state, ex);
            }

            await _store.SaveAsync(state, ct);
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
            state = await PushInternalAsync(state, ct);
            if (!string.IsNullOrWhiteSpace(state.LastError))
                return state;

            state = await UploadPendingPaymentAttachmentsInternalAsync(state, ct);
            if (!string.IsNullOrWhiteSpace(state.LastError))
            {
                await _store.SaveAsync(state, ct);
                return state;
            }

            state = await PullInternalAsync(state, ct);
            await _store.SaveAsync(state, ct);
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

            if (HasPendingServerSyncPayload(state) || state.PendingPaymentAttachments.Count > 0)
            {
                state = await PushInternalAsync(state, ct);
                if (string.IsNullOrWhiteSpace(state.LastError))
                    state = await UploadPendingPaymentAttachmentsInternalAsync(state, ct);
                if (string.IsNullOrWhiteSpace(state.LastError))
                    state = await PullInternalAsync(state, ct);

                await _store.SaveAsync(state, ct);
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

            await _store.SaveAsync(state, ct);
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

            await _store.SaveAsync(state, ct);
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
            var attachmentUploadErrors = new List<string>();

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
                        QueueUnacceptedLinkedPaymentConflict(state.PendingPush, payment, linkedTransaction, result.AcceptedRevisions);
                        await MarkPushConflictAndRefreshAsync(state, result, ct);
                        await _store.SaveAsync(state, ct);
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
                    catch (Exception uploadEx)
                    {
                        state.PendingPaymentAttachments.Add(attachment);
                        attachmentUploadErrors.Add(uploadEx.Message);
                        if (string.IsNullOrWhiteSpace(state.LastError))
                        {
                            state.LastError = uploadEx.Message;
                            state.LastFailureAllowsCachedDisplay = true;
                        }
                    }
                }

                state.LastSuccessUtc = DateTime.UtcNow;
                state.LastFailureAllowsCachedDisplay = true;
                state.ConsecutiveFailureCount = 0;
                state = await PullInternalAsync(state, ct);
                RestorePaymentAttachmentUploadErrorsAfterPull(state, attachmentUploadErrors);
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
                foreach (var attachment in attachmentList)
                {
                    attachment.PaymentId = payment.Id;
                    state.PendingPaymentAttachments.RemoveAll(x => x.LocalId == attachment.LocalId);
                    state.PendingPaymentAttachments.Add(attachment);
                }

                MarkFailure(state, ex);
            }

            await _store.SaveAsync(state, ct);
            await RemoveUploadedPaymentAttachmentDraftsAsync(uploadedAttachments, ct);
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
            foreach (var attachment in attachmentList)
            {
                attachment.PaymentId = paymentId;
                state.PendingPaymentAttachments.RemoveAll(x => x.LocalId == attachment.LocalId);
                state.PendingPaymentAttachments.Add(attachment);
            }
        }, ct);
    }

    private async Task<MobileSyncState> MutateStoredStateAsync(Action<MobileSyncState> mutate, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            state.Normalize();
            mutate(state);
            await _store.SaveAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<MobileSyncState> PullInternalAsync(MobileSyncState state, CancellationToken ct)
    {
        state.LastAttemptUtc = DateTime.UtcNow;

        try
        {
            var response = EnsurePullResponse(await _api.PullAsync(state.LastRevision, ct));
            ApplyPullResponse(state, response);
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
            if (HasPendingServerSyncPayload(state))
            {
                var result = EnsurePushResult(await _api.PushAsync(state.PendingPush, ct));
                state.LastRevision = Math.Max(state.LastRevision, result.CurrentServerRevision);
                if (result.ConflictCount > 0)
                {
                    RemoveAcceptedPendingMutations(state.PendingPush, result.AcceptedRevisions);
                    await MarkPushConflictAndRefreshAsync(state, result, ct);
                    await _store.SaveAsync(state, ct);
                    return state;
                }

                state.PendingPush = new SyncPushRequest { DeviceId = state.DeviceId };
            }

            state.LastSuccessUtc = DateTime.UtcNow;
            state.LastError = string.Empty;
            state.LastFailureAllowsCachedDisplay = true;
            state.ConsecutiveFailureCount = 0;
            await _store.SaveAsync(state, ct);
            return state;
        }
        catch (Exception ex)
        {
            MarkFailure(state, ex);
            await _store.SaveAsync(state, ct);
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

    private async Task<MobileSyncState> UploadPendingPaymentAttachmentsInternalAsync(MobileSyncState state, CancellationToken ct)
    {
        state.LastAttemptUtc = DateTime.UtcNow;
        var pending = state.PendingPaymentAttachments.ToList();
        if (pending.Count == 0)
        {
            state.LastError = string.Empty;
            state.LastFailureAllowsCachedDisplay = true;
            return state;
        }

        var uploadedIds = new List<Guid>();
        var uploadedAttachments = new List<PendingPaymentAttachmentRecord>();
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
                uploadedAttachments.Add(attachment);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        state.PendingPaymentAttachments.RemoveAll(x => uploadedIds.Contains(x.LocalId));
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

        await _store.SaveAsync(state, ct);
        await RemoveUploadedPaymentAttachmentDraftsAsync(uploadedAttachments, ct);
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

    private async Task RemoveUploadedPaymentAttachmentDraftsAsync(
        IEnumerable<PendingPaymentAttachmentRecord> attachments,
        CancellationToken ct)
    {
        foreach (var attachment in attachments)
        {
            try
            {
                await _attachmentStore.RemoveAsync(attachment, ct);
            }
            catch (Exception ex)
            {
                MobileAppLogger.Warn("SYNC", $"업로드 완료 첨부 임시 파일 정리 실패: {attachment.FileName} / {ex.Message}");
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
                ApplyPullResponse(state, response);
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
        var reason = firstConflict?.Reason?.Trim();
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
        return (state.PendingPush.CompanyProfiles?.Count ?? 0) > 0
            || (state.PendingPush.Units?.Count ?? 0) > 0
            || (state.PendingPush.CustomerCategories?.Count ?? 0) > 0
            || (state.PendingPush.PriceGradeOptions?.Count ?? 0) > 0
            || (state.PendingPush.TradeTypeOptions?.Count ?? 0) > 0
            || (state.PendingPush.ItemCategoryOptions?.Count ?? 0) > 0
            || (state.PendingPush.CustomerMasters?.Count ?? 0) > 0
            || (state.PendingPush.Customers?.Count ?? 0) > 0
            || (state.PendingPush.CustomerContracts?.Count ?? 0) > 0
            || (state.PendingPush.Items?.Count ?? 0) > 0
            || (state.PendingPush.ItemWarehouseStocks?.Count ?? 0) > 0
            || (state.PendingPush.Transactions?.Count ?? 0) > 0
            || (state.PendingPush.TransactionAttachments?.Count ?? 0) > 0
            || (state.PendingPush.InventoryTransfers?.Count ?? 0) > 0
            || (state.PendingPush.RentalManagementCompanies?.Count ?? 0) > 0
            || (state.PendingPush.RentalBillingProfiles?.Count ?? 0) > 0
            || (state.PendingPush.RentalAssets?.Count ?? 0) > 0
            || (state.PendingPush.RentalAssetAssignmentHistories?.Count ?? 0) > 0
            || (state.PendingPush.RentalBillingLogs?.Count ?? 0) > 0
            || (state.PendingPush.Invoices?.Count ?? 0) > 0
            || (state.PendingPush.Payments?.Count ?? 0) > 0;
    }

    private static void ApplyPullResponse(MobileSyncState state, SyncPullResponse response)
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
        ApplyPurgeRecords(state, response.PurgeRecords);
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

    private static void ApplyPurgeRecords(MobileSyncState state, IEnumerable<RecycleBinPurgeRecordDto>? purgeRecords)
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
            ApplyPurgeRecord(state, NormalizePurgeRecordKind(record.Kind), record.EntityId, record.Revision);
    }

    private static void ApplyPurgeRecord(MobileSyncState state, string normalizedKind, Guid entityId, long purgeRevision)
    {
        switch (normalizedKind)
        {
            case "customer":
                RemoveEntityById(state.SyncedCustomers, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.Customers, entityId, purgeRevision);
                ClearRentalAssignmentHistoryCustomerReferences(state.SyncedRentalAssetAssignmentHistories, entityId, purgeRevision);
                ClearRentalAssignmentHistoryCustomerReferences(state.PendingPush.RentalAssetAssignmentHistories, entityId, purgeRevision);
                break;
            case "item":
                RemoveEntityById(state.SyncedItems, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.Items, entityId, purgeRevision);
                state.SyncedItemWarehouseStocks.RemoveAll(stock => stock.ItemId == entityId);
                state.PendingPush.ItemWarehouseStocks.RemoveAll(stock => stock.ItemId == entityId);
                break;
            case "pricegradeoption":
            case "price-grade-option":
                RemoveEntityById(state.SyncedPriceGradeOptions, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.PriceGradeOptions, entityId, purgeRevision);
                break;
            case "invoice":
                RemoveEntityById(state.SyncedInvoices, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.Invoices, entityId, purgeRevision);
                state.SyncedPayments.RemoveAll(payment => payment.InvoiceId == entityId && !IsEntityNewerThanPurge(payment, purgeRevision));
                state.PendingPush.Payments.RemoveAll(payment => payment.InvoiceId == entityId && !IsEntityNewerThanPurge(payment, purgeRevision));
                state.SyncedTransactions.RemoveAll(transaction => transaction.LinkedInvoiceId == entityId && !IsEntityNewerThanPurge(transaction, purgeRevision));
                state.PendingPush.Transactions.RemoveAll(transaction => transaction.LinkedInvoiceId == entityId && !IsEntityNewerThanPurge(transaction, purgeRevision));
                break;
            case "payment":
                RemoveEntityById(state.SyncedPayments, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.Payments, entityId, purgeRevision);
                state.PendingPaymentAttachments.RemoveAll(attachment => attachment.PaymentId == entityId);
                break;
            case "transaction":
                RemoveEntityById(state.SyncedTransactions, entityId, purgeRevision);
                RemoveEntityById(state.PendingPush.Transactions, entityId, purgeRevision);
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

    private static void ClearRentalBillingProfileReferences(
        List<RentalAssetDto> values,
        Guid profileId,
        long purgeRevision)
    {
        foreach (var value in values)
        {
            if (value.BillingProfileId == profileId)
                value.BillingProfileId = null;
        }
    }

    private static void ClearRentalAssignmentHistoryBillingProfileReferences(
        List<RentalAssetAssignmentHistoryDto> values,
        Guid profileId,
        long purgeRevision)
    {
        foreach (var value in values)
        {
            if (value.BillingProfileId == profileId)
                value.BillingProfileId = null;
        }
    }

    private static void ClearRentalAssignmentHistoryCustomerReferences(
        List<RentalAssetAssignmentHistoryDto> values,
        Guid customerId,
        long purgeRevision)
    {
        foreach (var value in values)
        {
            if (value.CustomerId == customerId)
                value.CustomerId = null;
        }
    }

    private static bool IsEntityNewerThanPurge(SyncEntityDto entity, long purgeRevision)
        => !entity.IsDeleted && entity.Revision > purgeRevision;

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
            "price-grade-option" => 10,
            "pricegradeoption" => 10,
            _ => 99
        };
}
