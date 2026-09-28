using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;
namespace GeoraePlan.Mobile.App.Services;
public sealed class GeoraePlanApiClient
{
    private readonly Queue<SyncPullResponse?> _pullResponses;

    public GeoraePlanApiClient(params SyncPullResponse?[] pullResponses)
    {
        _pullResponses = new Queue<SyncPullResponse?>(pullResponses);
    }

    public List<long> RequestedPullRevisions { get; } = new();
    public List<SyncPushRequest> SubmittedPushes { get; } =
        new();
    public Func<Task>? BeforePullReturnAsync { get; set; }
    public Func<Task>? BeforePushReturnAsync { get; set; }
    public Func<Task>? BeforeInvoiceReturnAsync { get; set; }
    public Func<Task>? BeforePaymentReturnAsync { get; set; }
    public Func<
        MobileSessionOwner,
        PendingPaymentAttachmentRecord,
        Task>? BeforePaymentAttachmentUploadAsync { get; set; }
    public SyncPushResult PushResult { get; set; } = new();
    public Exception? PaymentAttachmentUploadException { get; set; }
    public int PaymentAttachmentUploadAttempts { get; private set; }
    public List<MobileSessionOwner> SubmittedInvoiceOwners { get; } =
        [];
    public List<MobileSessionOwner> SubmittedPaymentOwners { get; } =
        [];
    public List<(MobileSessionOwner Owner, Guid LocalId)>
        SubmittedPaymentAttachmentOwners { get; } = [];

    public Task<SyncPullResponse?> PullAsync(long sinceRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequestedPullRevisions.Add(sinceRevision);
        return Task.FromResult(
            _pullResponses.Count > 0
                ? _pullResponses.Dequeue()
                : null);
    }

    public async Task<SyncPullResponse?> PullAsync(
        long sinceRevision,
        MobileSessionOwner owner,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequestedPullRevisions.Add(sinceRevision);
        var response = _pullResponses.Count > 0
            ? _pullResponses.Dequeue()
            : null;
        if (BeforePullReturnAsync is not null)
            await BeforePullReturnAsync();
        return response;
    }

    public Task<SyncPushResult?> PushAsync(
        SyncPushRequest request,
        CancellationToken ct = default)
        => Task.FromResult<SyncPushResult?>(new SyncPushResult());

    public async Task<SyncPushResult?> PushAsync(
        SyncPushRequest request,
        MobileSessionOwner owner,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SubmittedPushes.Add(request);
        if (BeforePushReturnAsync is not null)
            await BeforePushReturnAsync();
        return PushResult;
    }

    public Task<SyncStatusDto?> GetSyncStatusAsync(CancellationToken ct = default)
        => Task.FromResult<SyncStatusDto?>(new SyncStatusDto());

    public Task<SyncStatusDto?> GetSyncStatusAsync(
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => GetSyncStatusAsync(ct);

    public Task<InvoiceDto?> CreateInvoiceAsync(
        InvoiceDto invoice,
        CancellationToken ct = default)
        => Task.FromResult<InvoiceDto?>(invoice);

    public Task<InvoiceDto?> CreateInvoiceAsync(
        InvoiceDto invoice,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => ReturnInvoiceAsync(
            invoice,
            owner,
            ct);

    public Task<InvoiceDto?> UpdateInvoiceAsync(
        InvoiceDto invoice,
        CancellationToken ct = default)
        => Task.FromResult<InvoiceDto?>(invoice);

    public Task<InvoiceDto?> UpdateInvoiceAsync(
        InvoiceDto invoice,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => ReturnInvoiceAsync(
            invoice,
            owner,
            ct);

    public Task<PaymentDto?> CreatePaymentAsync(
        PaymentDto payment,
        CancellationToken ct = default)
        => Task.FromResult<PaymentDto?>(payment);

    public Task<PaymentDto?> CreatePaymentAsync(
        PaymentDto payment,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => ReturnPaymentAsync(
            payment,
            owner,
            ct);

    private async Task<PaymentDto?> ReturnPaymentAsync(
        PaymentDto payment,
        MobileSessionOwner owner,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SubmittedPaymentOwners.Add(owner);
        if (BeforePaymentReturnAsync is not null)
            await BeforePaymentReturnAsync();
        return payment;
    }

    private async Task<InvoiceDto?> ReturnInvoiceAsync(
        InvoiceDto invoice,
        MobileSessionOwner owner,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SubmittedInvoiceOwners.Add(owner);
        if (BeforeInvoiceReturnAsync is not null)
            await BeforeInvoiceReturnAsync();
        return invoice;
    }

    public Task<PaymentAttachmentDto?> UploadPaymentAttachmentAsync(
        Guid paymentId,
        PendingPaymentAttachmentRecord attachment,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        PaymentAttachmentUploadAttempts++;
        return PaymentAttachmentUploadException is null
            ? Task.FromResult<PaymentAttachmentDto?>(
                new PaymentAttachmentDto())
            : Task.FromException<PaymentAttachmentDto?>(
                PaymentAttachmentUploadException);
    }

    public async Task<PaymentAttachmentDto?> UploadPaymentAttachmentAsync(
        Guid paymentId,
        PendingPaymentAttachmentRecord attachment,
        MobileSessionOwner owner,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SubmittedPaymentAttachmentOwners.Add((
            owner,
            attachment.LocalId));
        if (BeforePaymentAttachmentUploadAsync is not null)
        {
            await BeforePaymentAttachmentUploadAsync(
                owner,
                attachment);
        }

        return await UploadPaymentAttachmentAsync(
            paymentId,
            attachment,
            ct);
    }
}

public sealed class PaymentAttachmentDraftStore
{
    public Func<
        MobileSessionOwner,
        PendingPaymentAttachmentRecord,
        Task>? BeforeRemoveAsync { get; set; }

    public List<(
        MobileSessionOwner Owner,
        Guid LocalId)> RemovedDrafts { get; } = [];

    public List<(
        MobileSessionOwner Owner,
        Guid LocalId)> RemovalAttempts { get; } = [];

    public List<MobileSessionOwner>
        OrphanCleanupOwners { get; } = [];

    public Task<bool> PrepareOwnedDraftsAsync(
        MobileSessionOwner owner,
        IEnumerable<PendingPaymentAttachmentRecord>? attachments,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task<string?> ResolveOwnedPathAsync(
        MobileSessionOwner owner,
        PendingPaymentAttachmentRecord attachment,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(
            attachment.StoredPath);
    }

    public async Task RemoveAsync(
        MobileSessionOwner owner,
        PendingPaymentAttachmentRecord attachment,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RemovalAttempts.Add((
            owner,
            attachment.LocalId));
        if (BeforeRemoveAsync is not null)
        {
            await BeforeRemoveAsync(
                owner,
                attachment);
        }

        RemovedDrafts.Add((
            owner,
            attachment.LocalId));
    }

    public Task<int> RemoveOrphanDraftsAsync(
        MobileSessionOwner owner,
        IEnumerable<PendingPaymentAttachmentRecord> retainedAttachments,
        TimeSpan minimumAge,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        OrphanCleanupOwners.Add(owner);
        return Task.FromResult(0);
    }
}

