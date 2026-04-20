namespace 거래플랜.Desktop.App.Services;

public sealed record UpdateReadinessResult(
    bool CanProceed,
    int InitialDirtyCount,
    int RemainingDirtyCount,
    int InitialPendingOutboxCount,
    int RemainingPendingOutboxCount,
    int RemainingFailedOutboxCount,
    bool SyncAttempted,
    string Message);

public static class UpdateReadinessService
{
    public static async Task<UpdateReadinessResult> EnsureReadyForUpdateAsync(
        LocalStateService local,
        SyncService sync,
        SessionState session,
        CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
        {
            return new UpdateReadinessResult(
                CanProceed: false,
                InitialDirtyCount: 0,
                RemainingDirtyCount: 0,
                InitialPendingOutboxCount: 0,
                RemainingPendingOutboxCount: 0,
                RemainingFailedOutboxCount: 0,
                SyncAttempted: false,
                Message: "로그인 세션을 확인할 수 없어 업데이트를 시작할 수 없습니다.");
        }

        if (session.IsOfflineMode)
        {
            return new UpdateReadinessResult(
                CanProceed: false,
                InitialDirtyCount: 0,
                RemainingDirtyCount: 0,
                InitialPendingOutboxCount: 0,
                RemainingPendingOutboxCount: 0,
                RemainingFailedOutboxCount: 0,
                SyncAttempted: false,
                Message: "오프라인 모드에서는 dirty 데이터와 sync outbox를 모두 서버에 반영할 수 없어 업데이트를 시작할 수 없습니다. 온라인 상태에서 다시 시도하세요.");
        }

        var initialDirtyCount = await local.CountDirtyAsync(ct);
        var initialOutboxSummary = await local.GetSyncOutboxSummaryAsync(ct);
        if (initialDirtyCount <= 0 && initialOutboxSummary.PendingCount <= 0)
        {
            return new UpdateReadinessResult(
                CanProceed: true,
                InitialDirtyCount: 0,
                RemainingDirtyCount: 0,
                InitialPendingOutboxCount: 0,
                RemainingPendingOutboxCount: 0,
                RemainingFailedOutboxCount: 0,
                SyncAttempted: false,
                Message: "미동기화 dirty 데이터와 sync outbox 대기 항목이 없습니다.");
        }

        try
        {
            await sync.FlushPendingChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            var remainingAfterCancel = await local.CountDirtyAsync(CancellationToken.None);
            var remainingOutboxAfterCancel = await local.GetSyncOutboxSummaryAsync(CancellationToken.None);
            return new UpdateReadinessResult(
                CanProceed: false,
                InitialDirtyCount: initialDirtyCount,
                RemainingDirtyCount: remainingAfterCancel,
                InitialPendingOutboxCount: initialOutboxSummary.PendingCount,
                RemainingPendingOutboxCount: remainingOutboxAfterCancel.PendingCount,
                RemainingFailedOutboxCount: remainingOutboxAfterCancel.FailedCount,
                SyncAttempted: true,
                Message: await BuildPendingMessageAsync(
                    local,
                    session,
                    "업데이트 전 동기화 시간이 초과되었습니다.",
                    remainingAfterCancel,
                    remainingOutboxAfterCancel,
                    CancellationToken.None));
        }
        catch (Exception ex)
        {
            var remainingAfterFailure = await local.CountDirtyAsync(CancellationToken.None);
            var remainingOutboxAfterFailure = await local.GetSyncOutboxSummaryAsync(CancellationToken.None);
            return new UpdateReadinessResult(
                CanProceed: false,
                InitialDirtyCount: initialDirtyCount,
                RemainingDirtyCount: remainingAfterFailure,
                InitialPendingOutboxCount: initialOutboxSummary.PendingCount,
                RemainingPendingOutboxCount: remainingOutboxAfterFailure.PendingCount,
                RemainingFailedOutboxCount: remainingOutboxAfterFailure.FailedCount,
                SyncAttempted: true,
                Message: BuildFailureMessage(
                    $"업데이트 전 동기화 중 오류가 발생했습니다: {ex.Message}",
                    remainingAfterFailure,
                    remainingOutboxAfterFailure));
        }

        var remainingDirtyCount = await local.CountDirtyAsync(ct);
        var remainingOutboxSummary = await local.GetSyncOutboxSummaryAsync(ct);
        if (remainingDirtyCount <= 0 && remainingOutboxSummary.PendingCount <= 0)
        {
            return new UpdateReadinessResult(
                CanProceed: true,
                InitialDirtyCount: initialDirtyCount,
                RemainingDirtyCount: 0,
                InitialPendingOutboxCount: initialOutboxSummary.PendingCount,
                RemainingPendingOutboxCount: 0,
                RemainingFailedOutboxCount: 0,
                SyncAttempted: true,
                Message: BuildSuccessMessage(initialDirtyCount, initialOutboxSummary.PendingCount));
        }

        return new UpdateReadinessResult(
            CanProceed: false,
            InitialDirtyCount: initialDirtyCount,
            RemainingDirtyCount: remainingDirtyCount,
            InitialPendingOutboxCount: initialOutboxSummary.PendingCount,
            RemainingPendingOutboxCount: remainingOutboxSummary.PendingCount,
            RemainingFailedOutboxCount: remainingOutboxSummary.FailedCount,
            SyncAttempted: true,
            Message: await BuildPendingMessageAsync(
                local,
                session,
                "업데이트 전 동기화가 끝나지 않았습니다.",
                remainingDirtyCount,
                remainingOutboxSummary,
                ct));
    }

    private static async Task<string> BuildPendingMessageAsync(
        LocalStateService local,
        SessionState session,
        string prefix,
        int remainingDirtyCount,
        SyncOutboxSummary outboxSummary,
        CancellationToken ct)
    {
        var parts = new List<string>();
        if (remainingDirtyCount > 0)
        {
            var pendingMessage = await local.GetPendingSyncWaitingMessageAsync(prefix, ct);
            parts.Add(string.IsNullOrWhiteSpace(pendingMessage)
                ? $"{prefix} 미동기화 dirty 데이터 {remainingDirtyCount:N0}건이 남아 있습니다."
                : pendingMessage);

            var blockingReason = await local.GetPrimaryPendingSyncBlockingReasonAsync(session, ct);
            if (!string.IsNullOrWhiteSpace(blockingReason?.Message))
                parts.Add(blockingReason.Message);
        }

        if (outboxSummary.PendingCount > 0)
        {
            var outboxText = outboxSummary.FailedCount > 0
                ? $"sync outbox 대기 {outboxSummary.PendingCount:N0}건(실패 {outboxSummary.FailedCount:N0}건 포함)이 남아 있습니다."
                : $"sync outbox 대기 {outboxSummary.PendingCount:N0}건이 남아 있습니다.";
            parts.Add(outboxText);
        }

        return parts.Count == 0 ? prefix : string.Join(" ", parts);
    }

    private static string BuildFailureMessage(
        string prefix,
        int remainingDirtyCount,
        SyncOutboxSummary outboxSummary)
    {
        var details = new List<string>();
        if (remainingDirtyCount > 0)
            details.Add($"dirty {remainingDirtyCount:N0}건");
        if (outboxSummary.PendingCount > 0)
            details.Add($"outbox 대기 {outboxSummary.PendingCount:N0}건");
        if (outboxSummary.FailedCount > 0)
            details.Add($"outbox 실패 {outboxSummary.FailedCount:N0}건");

        return details.Count == 0
            ? prefix
            : $"{prefix} (남은 항목: {string.Join(", ", details)})";
    }

    private static string BuildSuccessMessage(int initialDirtyCount, int initialPendingOutboxCount)
    {
        var details = new List<string>();
        if (initialDirtyCount > 0)
            details.Add($"dirty 데이터 {initialDirtyCount:N0}건");
        if (initialPendingOutboxCount > 0)
            details.Add($"sync outbox {initialPendingOutboxCount:N0}건");

        return details.Count == 0
            ? "업데이트 전 정리할 항목이 없습니다."
            : $"{string.Join(" 및 ", details)}을(를) 모두 정리했습니다.";
    }
}
