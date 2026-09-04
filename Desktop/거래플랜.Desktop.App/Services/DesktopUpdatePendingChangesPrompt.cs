using System.Windows;

namespace 거래플랜.Desktop.App.Services;

internal static class DesktopUpdatePendingChangesPrompt
{
    internal static bool ConfirmForceInstall(
        UpdateReadinessResult readiness,
        string targetVersion)
    {
        if (!readiness.CanForceProceed)
            return false;

        var answer = MessageBox.Show(
            BuildForceInstallMessage(readiness, targetVersion),
            "동기화가 남아 있어도 업데이트",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
            return false;

        AppLogger.Warn(
            "UPDATE",
            $"사용자가 미동기화 자료를 보존한 채 강제 업데이트를 확인했습니다. " +
            $"version={targetVersion}, dirty={readiness.RemainingDirtyCount}, " +
            $"outboxPending={readiness.RemainingPendingOutboxCount}, " +
            $"outboxFailed={readiness.RemainingFailedOutboxCount}");
        return true;
    }

    internal static string BuildForceInstallMessage(
        UpdateReadinessResult readiness,
        string targetVersion)
    {
        var details = new List<string>();
        if (readiness.RemainingDirtyCount > 0)
            details.Add($"이 PC에만 있는 미전송 자료: {readiness.RemainingDirtyCount:N0}건");
        if (readiness.RemainingPendingOutboxCount > 0)
            details.Add($"서버 전송 대기: {readiness.RemainingPendingOutboxCount:N0}건");
        if (readiness.RemainingFailedOutboxCount > 0)
            details.Add($"위 대기 항목 중 전송 실패: {readiness.RemainingFailedOutboxCount:N0}건");

        var detailText = details.Count == 0
            ? "- 미전송 자료가 남아 있습니다."
            : string.Join(Environment.NewLine, details.Select(static detail => $"- {detail}"));

        return
            "동기화를 마치지 못한 자료가 남아 있습니다." +
            Environment.NewLine + Environment.NewLine +
            detailText +
            Environment.NewLine + Environment.NewLine +
            "업데이트를 계속해도 이 자료와 전송 기록은 삭제되지 않고 이 PC에 그대로 남습니다." +
            Environment.NewLine +
            "다만 업데이트 후 동기화 재시도가 완료되기 전까지 다른 PC에서 최신 내용이 보이지 않을 수 있습니다." +
            Environment.NewLine + Environment.NewLine +
            $"그래도 PC 버전 {targetVersion} 설치를 계속하시겠습니까?" +
            Environment.NewLine +
            "[예] 미전송 자료를 보존한 채 업데이트" +
            Environment.NewLine +
            "[아니오] 설치 취소";
    }
}
