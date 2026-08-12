using 거래플랜.Server.Api.Services;

namespace GeoraePlan.Server.Api.Tests;

internal sealed class NoOpStoredFileDeferredDeletionQueue
    : IStoredFileDeferredDeletionQueue
{
    public static NoOpStoredFileDeferredDeletionQueue Instance { get; } = new();

    private NoOpStoredFileDeferredDeletionQueue()
    {
    }

    public IStoredFileDeferredDeletionPreparation PrepareForDatabaseCommit(
        IEnumerable<string> candidatePaths)
        => NoOpPreparation.Instance;

    public void Enqueue(IEnumerable<string> candidatePaths)
    {
    }

    public IReadOnlyList<string> TakeBatch(int maximumCount)
        => [];

    public void AcknowledgeCompleted(IEnumerable<string> candidatePaths)
    {
    }

    private sealed class NoOpPreparation
        : IStoredFileDeferredDeletionPreparation
    {
        public static NoOpPreparation Instance { get; } = new();

        public void MarkDatabaseCommitCompleted()
        {
        }

        public void Dispose()
        {
        }
    }
}
