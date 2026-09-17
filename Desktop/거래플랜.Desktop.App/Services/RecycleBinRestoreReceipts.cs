using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

// One instance per database and user operation. Never reuse receipts for a later restore/retry.
internal sealed class RecycleBinRestoreReceipts
{
    private readonly Dictionary<(Guid Id, string Kind, long PreviousRevision), long> _committed = new();

    public void Remember(RecycleBinMutationResultDto? result)
    {
        if (result?.CommittedRestores is not { Count: > 0 } receipts)
            return;
        var keys = new HashSet<(Guid, string, long)>();
        foreach (var receipt in receipts)
        {
            if (receipt is null || receipt.EntityId == Guid.Empty || string.IsNullOrWhiteSpace(receipt.Kind) ||
                receipt.PreviousRevision <= 0 || receipt.Revision <= receipt.PreviousRevision ||
                !keys.Add((receipt.EntityId, receipt.Kind, receipt.PreviousRevision)))
                return; // Ignore malformed optional metadata; normal per-item revision checks still apply.
        }
        foreach (var receipt in receipts)
            _committed[(receipt.EntityId, receipt.Kind, receipt.PreviousRevision)] = receipt.Revision;
    }

    public bool Covers(RecycleBinMutationTargetDto target) =>
        _committed.ContainsKey((target.EntityId, target.Kind, target.ExpectedRevision));
}
