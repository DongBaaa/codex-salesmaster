using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class SyncCoordinator
{
    private readonly JsonSyncStateStore _store;
    private readonly GeoraePlanApiClient _api;
    private readonly PaymentAttachmentDraftStore _attachmentStore;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public SyncCoordinator(JsonSyncStateStore store, GeoraePlanApiClient api, PaymentAttachmentDraftStore attachmentStore)
    {
        _store = store;
        _api = api;
        _attachmentStore = attachmentStore;
    }

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
                var response = await _api.PullAsync(state.LastRevision, ct);
                if (response is not null)
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

    public async Task<MobileSyncState> TryBackgroundSyncAsync(string reason, TimeSpan minInterval, CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            var now = DateTime.UtcNow;
            if (state.LastBackgroundSyncUtc.HasValue && now - state.LastBackgroundSyncUtc.Value < minInterval)
                return state;

            state.LastBackgroundSyncUtc = now;
            await _store.SaveAsync(state, ct);

            state = await PushInternalAsync(state, ct);
            if (string.IsNullOrWhiteSpace(state.LastError))
                state = await UploadPendingPaymentAttachmentsInternalAsync(state, ct);
            if (string.IsNullOrWhiteSpace(state.LastError))
                state = await PullInternalAsync(state, ct);

            await _store.SaveAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> QueueInvoiceDraftAsync(InvoiceDto invoice, CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);
        state.PendingPush.Invoices.RemoveAll(x => x.Id == invoice.Id);
        state.PendingPush.Invoices.Add(invoice);
        await _store.SaveAsync(state, ct);
        return state;
    }

    public async Task<MobileSyncState> QueuePaymentDraftAsync(PaymentDto payment, CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);
        state.PendingPush.Payments.RemoveAll(x => x.Id == payment.Id);
        state.PendingPush.Payments.Add(payment);
        await _store.SaveAsync(state, ct);
        return state;
    }

    public async Task<MobileSyncState> QueueTransactionDraftAsync(TransactionDto transaction, CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);
        state.PendingPush.Transactions.RemoveAll(x => x.Id == transaction.Id);
        state.PendingPush.Transactions.Add(transaction);
        await _store.SaveAsync(state, ct);
        return state;
    }

    public async Task<MobileSyncState> QueueTransactionAttachmentDraftAsync(TransactionAttachmentDto attachment, CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);
        state.PendingPush.TransactionAttachments.RemoveAll(x => x.Id == attachment.Id);
        state.PendingPush.TransactionAttachments.Add(attachment);
        await _store.SaveAsync(state, ct);
        return state;
    }

    public async Task<MobileSyncState> QueueInventoryTransferDraftAsync(InventoryTransferDto transfer, CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);
        state.PendingPush.InventoryTransfers.RemoveAll(x => x.Id == transfer.Id);
        state.PendingPush.InventoryTransfers.Add(transfer);
        await _store.SaveAsync(state, ct);
        return state;
    }

    public async Task<MobileSyncState> QueueRentalManagementCompanyDraftAsync(RentalManagementCompanyDto company, CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);
        state.PendingPush.RentalManagementCompanies.RemoveAll(x => x.Id == company.Id);
        state.PendingPush.RentalManagementCompanies.Add(company);
        await _store.SaveAsync(state, ct);
        return state;
    }

    public async Task<MobileSyncState> QueueRentalBillingProfileDraftAsync(RentalBillingProfileDto profile, CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);
        state.PendingPush.RentalBillingProfiles.RemoveAll(x => x.Id == profile.Id);
        state.PendingPush.RentalBillingProfiles.Add(profile);
        await _store.SaveAsync(state, ct);
        return state;
    }

    public async Task<MobileSyncState> QueueRentalAssetDraftAsync(RentalAssetDto asset, CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);
        state.PendingPush.RentalAssets.RemoveAll(x => x.Id == asset.Id);
        state.PendingPush.RentalAssets.Add(asset);
        await _store.SaveAsync(state, ct);
        return state;
    }

    public async Task<MobileSyncState> QueueRentalBillingLogDraftAsync(RentalBillingLogDto log, CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);
        state.PendingPush.RentalBillingLogs.RemoveAll(x => x.Id == log.Id);
        state.PendingPush.RentalBillingLogs.Add(log);
        await _store.SaveAsync(state, ct);
        return state;
    }

    public async Task<MobileSyncState> QueuePaymentAttachmentsAsync(
        Guid paymentId,
        IEnumerable<PendingPaymentAttachmentRecord> attachments,
        CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);
        foreach (var attachment in attachments)
        {
            attachment.PaymentId = paymentId;
            state.PendingPaymentAttachments.RemoveAll(x => x.LocalId == attachment.LocalId);
            state.PendingPaymentAttachments.Add(attachment);
        }

        await _store.SaveAsync(state, ct);
        return state;
    }

    private async Task<MobileSyncState> PullInternalAsync(MobileSyncState state, CancellationToken ct)
    {
        state.LastAttemptUtc = DateTime.UtcNow;

        try
        {
            var response = await _api.PullAsync(state.LastRevision, ct);
            if (response is not null)
            {
                ApplyPullResponse(state, response);
            }
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
                var result = await _api.PushAsync(state.PendingPush, ct);
                if (result is not null)
                    state.LastRevision = Math.Max(state.LastRevision, result.CurrentServerRevision);

                state.PendingPush = new SyncPushRequest { DeviceId = state.DeviceId };
            }

            state.LastSuccessUtc = DateTime.UtcNow;
            state.LastError = string.Empty;
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

    private async Task<MobileSyncState> UploadPendingPaymentAttachmentsInternalAsync(MobileSyncState state, CancellationToken ct)
    {
        state.LastAttemptUtc = DateTime.UtcNow;
        var pending = state.PendingPaymentAttachments.ToList();
        if (pending.Count == 0)
        {
            state.LastError = string.Empty;
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

                await _api.UploadPaymentAttachmentAsync(attachment.PaymentId, attachment, ct);
                uploadedIds.Add(attachment.LocalId);
                await _attachmentStore.RemoveAsync(attachment, ct);
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
            state.ConsecutiveFailureCount = 0;
        }
        else
        {
            state.LastError = errors[0];
            state.ConsecutiveFailureCount++;
        }

        return state;
    }

    private static void MarkFailure(MobileSyncState state, Exception ex)
    {
        state.LastError = ex.Message;
        state.ConsecutiveFailureCount++;
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
        state.ConsecutiveFailureCount = 0;
        state.LastPulledCustomerCount = response.Customers.Count;
        state.LastPulledItemCount = response.Items.Count;
        state.LastPulledInvoiceCount = response.Invoices.Count;
        state.LastPulledPaymentCount = response.Payments.Count;
        state.LastPulledTransactionCount = response.Transactions.Count;
        state.LastPulledTransactionAttachmentCount = response.TransactionAttachments.Count;
        state.LastPulledInventoryTransferCount = response.InventoryTransfers.Count;
        state.LastPulledRentalManagementCompanyCount = response.RentalManagementCompanies.Count;
        state.LastPulledRentalBillingProfileCount = response.RentalBillingProfiles.Count;
        state.LastPulledRentalAssetCount = response.RentalAssets.Count;
        state.LastPulledRentalBillingLogCount = response.RentalBillingLogs.Count;
        state.SyncedTransactions = MergeById(state.SyncedTransactions, response.Transactions);
        state.SyncedTransactionAttachments = MergeById(state.SyncedTransactionAttachments, response.TransactionAttachments);
        state.SyncedInventoryTransfers = MergeById(state.SyncedInventoryTransfers, response.InventoryTransfers);
        state.SyncedRentalManagementCompanies = MergeById(state.SyncedRentalManagementCompanies, response.RentalManagementCompanies);
        state.SyncedRentalBillingProfiles = MergeById(state.SyncedRentalBillingProfiles, response.RentalBillingProfiles);
        state.SyncedRentalAssets = MergeById(state.SyncedRentalAssets, response.RentalAssets);
        state.SyncedRentalBillingLogs = MergeById(state.SyncedRentalBillingLogs, response.RentalBillingLogs);
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
}
