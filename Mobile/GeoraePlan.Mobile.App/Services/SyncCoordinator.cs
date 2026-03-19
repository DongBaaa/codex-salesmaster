using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class SyncCoordinator
{
    private readonly JsonSyncStateStore _store;
    private readonly GeoraePlanApiClient _api;

    public SyncCoordinator(JsonSyncStateStore store, GeoraePlanApiClient api)
    {
        _store = store;
        _api = api;
    }

    public Task<MobileSyncState> LoadAsync(CancellationToken ct = default)
        => _store.LoadAsync(ct);

    public async Task<MobileSyncState> PullAsync(CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);

        try
        {
            var response = await _api.PullAsync(state.LastRevision, ct);
            if (response is not null)
            {
                state.LastRevision = Math.Max(state.LastRevision, response.CurrentServerRevision);
                state.LastSuccessUtc = DateTime.UtcNow;
                state.LastError = string.Empty;
                state.LastPulledCustomerCount = response.Customers.Count;
                state.LastPulledItemCount = response.Items.Count;
                state.LastPulledInvoiceCount = response.Invoices.Count;
                state.LastPulledPaymentCount = response.Payments.Count;
            }
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
        }

        await _store.SaveAsync(state, ct);
        return state;
    }

    public async Task<MobileSyncState> PushAsync(CancellationToken ct = default)
    {
        var state = await _store.LoadAsync(ct);

        try
        {
            state.Normalize();
            if (state.PendingInvoiceCount > 0 || state.PendingPaymentCount > 0)
            {
                var result = await _api.PushAsync(state.PendingPush, ct);
                if (result is not null)
                    state.LastRevision = Math.Max(state.LastRevision, result.CurrentServerRevision);

                state.PendingPush = new SyncPushRequest { DeviceId = state.DeviceId };
            }

            state.LastSuccessUtc = DateTime.UtcNow;
            state.LastError = string.Empty;
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
        }

        await _store.SaveAsync(state, ct);
        return state;
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
}
