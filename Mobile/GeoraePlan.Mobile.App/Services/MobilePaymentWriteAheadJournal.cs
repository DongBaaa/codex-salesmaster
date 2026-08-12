using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

internal static class MobilePaymentWriteAheadJournal
{
    public static void PrepareBeforeNetworkMutation(
        MobileSyncState state,
        PaymentDto payment,
        TransactionDto? linkedTransaction,
        IReadOnlyList<PendingPaymentAttachmentRecord> attachments)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(attachments);
        state.Normalize();

        state.PendingPush.Payments.RemoveAll(
            current => IsSameMutation(
                current.Id,
                current.MutationId,
                payment.Id,
                payment.MutationId));
        state.PendingPush.Payments.Add(payment);
        if (linkedTransaction is not null)
        {
            state.PendingPush.Transactions.RemoveAll(
                current => IsSameMutation(
                    current.Id,
                    current.MutationId,
                    linkedTransaction.Id,
                    linkedTransaction.MutationId));
            state.PendingPush.Transactions.Add(linkedTransaction);
        }

        foreach (var attachment in attachments)
        {
            attachment.PaymentId = payment.Id;
            state.PendingPaymentAttachments.RemoveAll(
                current => current.LocalId == attachment.LocalId);
            state.PendingPaymentAttachments.Add(attachment);
        }
    }

    public static void MarkServerAccepted(
        MobileSyncState state,
        PaymentDto acceptedPayment,
        TransactionDto? linkedTransaction)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(acceptedPayment);
        state.Normalize();
        var matchingPaymentIds = state.PendingPush.Payments
            .Where(current => IsSameMutation(
                current.Id,
                current.MutationId,
                acceptedPayment.Id,
                acceptedPayment.MutationId))
            .Select(current => current.Id)
            .Append(acceptedPayment.Id)
            .ToHashSet();
        state.PendingPush.Payments.RemoveAll(
            current => IsSameMutation(
                current.Id,
                current.MutationId,
                acceptedPayment.Id,
                acceptedPayment.MutationId));
        state.SyncedPayments.RemoveAll(
            current => IsSameMutation(
                current.Id,
                current.MutationId,
                acceptedPayment.Id,
                acceptedPayment.MutationId));
        state.SyncedPayments.Add(acceptedPayment);
        foreach (var attachment in state.PendingPaymentAttachments
                     .Where(current =>
                         matchingPaymentIds.Contains(
                             current.PaymentId)))
        {
            attachment.PaymentId = acceptedPayment.Id;
        }
        if (linkedTransaction is not null)
        {
            state.PendingPush.Transactions.RemoveAll(
                current => IsSameMutation(
                    current.Id,
                    current.MutationId,
                    linkedTransaction.Id,
                    linkedTransaction.MutationId));
            state.SyncedTransactions.RemoveAll(
                current => IsSameMutation(
                    current.Id,
                    current.MutationId,
                    linkedTransaction.Id,
                    linkedTransaction.MutationId));
            state.SyncedTransactions.Add(
                linkedTransaction);
        }
    }

    public static IReadOnlyList<PendingPaymentAttachmentRecord>
        MarkTerminallyRejected(
            MobileSyncState state,
            PaymentDto payment,
            TransactionDto? linkedTransaction)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(payment);
        state.Normalize();
        var rejectedPaymentIds = state.PendingPush.Payments
            .Where(current => IsSameMutation(
                current.Id,
                current.MutationId,
                payment.Id,
                payment.MutationId))
            .Select(current => current.Id)
            .Append(payment.Id)
            .ToHashSet();
        state.PendingPush.Payments.RemoveAll(
            current => IsSameMutation(
                current.Id,
                current.MutationId,
                payment.Id,
                payment.MutationId));
        if (linkedTransaction is not null)
        {
            state.PendingPush.Transactions.RemoveAll(
                current => IsSameMutation(
                    current.Id,
                    current.MutationId,
                    linkedTransaction.Id,
                    linkedTransaction.MutationId));
        }
        var discarded = state.PendingPaymentAttachments
            .Where(current =>
                rejectedPaymentIds.Contains(
                    current.PaymentId))
            .ToList();
        var discardedIds = discarded
            .Select(current => current.LocalId)
            .ToHashSet();
        state.PendingPaymentAttachments.RemoveAll(
            current => discardedIds.Contains(current.LocalId));
        return discarded;
    }

    public static bool MarkAttachmentUploadedOrTerminal(
        MobileSyncState state,
        Guid localId)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.PendingPaymentAttachments.RemoveAll(
            current => current.LocalId == localId) > 0;
    }

    public static bool MarkAttachmentServerAccepted(
        MobileSyncState state,
        Guid localId,
        DateTime acceptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        var attachment =
            state.PendingPaymentAttachments.FirstOrDefault(
                current => current.LocalId == localId);
        if (attachment is null)
            return false;

        attachment.ServerUploadAcceptedAtUtc =
            acceptedAtUtc;
        return true;
    }

    public static void RestorePendingAttachment(
        MobileSyncState state,
        PendingPaymentAttachmentRecord attachment)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(attachment);
        state.PendingPaymentAttachments.RemoveAll(
            current =>
                current.LocalId ==
                attachment.LocalId);
        state.PendingPaymentAttachments.Add(attachment);
    }

    private static bool IsSameMutation(
        Guid currentId,
        string? currentMutationId,
        Guid requestedId,
        string? requestedMutationId)
        => currentId == requestedId ||
           (!string.IsNullOrWhiteSpace(requestedMutationId) &&
            string.Equals(
                currentMutationId?.Trim(),
                requestedMutationId.Trim(),
                StringComparison.Ordinal));
}
