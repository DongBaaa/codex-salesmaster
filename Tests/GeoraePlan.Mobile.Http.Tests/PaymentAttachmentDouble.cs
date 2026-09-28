using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;
namespace GeoraePlan.Mobile.App.Services;
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

