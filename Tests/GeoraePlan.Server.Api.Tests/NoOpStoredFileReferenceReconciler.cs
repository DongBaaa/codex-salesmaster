using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;

namespace GeoraePlan.Server.Api.Tests;

internal sealed class NoOpStoredFileReferenceReconciler : IStoredFileReferenceReconciler
{
    public static NoOpStoredFileReferenceReconciler Instance { get; } = new();

    private NoOpStoredFileReferenceReconciler()
    {
    }

    public Task DeleteUnreferencedAsync(
        IEnumerable<string> candidatePaths,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<PaymentAttachment?> FindPaymentAttachmentAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<PaymentAttachment?>(null);
}
