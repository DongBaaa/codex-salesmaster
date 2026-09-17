namespace 거래플랜.Desktop.App.ViewModels;

internal sealed record SyncRepairCompletion(bool Succeeded, string Message)
{
    internal static SyncRepairCompletion Evaluate(
        bool allStepsSucceeded,
        bool verificationPerformed,
        bool requiresManualReview,
        int dirtyCount,
        int unacknowledgedCount,
        int unconfirmedTargetCount)
    {
        if (!allStepsSucceeded)
            return new(false, "일부 복구 작업에 실패했습니다. 미전송 변경은 보존되며, 아래 실행 결과와 동기화 진단을 확인하세요.");
        if (dirtyCount > 0 || unacknowledgedCount > 0)
            return new(false, $"복구 작업 후에도 미전송 변경 {dirtyCount:N0}건, 전송 미완료 {unacknowledgedCount:N0}건이 남아 있습니다. 내용을 확인한 뒤 다시 시도하세요.");
        if (!verificationPerformed || requiresManualReview || unconfirmedTargetCount > 0)
            return new(false, "복구 작업 실행은 끝났지만 대상 항목의 해결은 확인되지 않았습니다. 수동 확인이 필요합니다.");
        return new(true, "대상 항목의 복구와 현재 범위의 전송 완료를 확인했습니다.");
    }
}
