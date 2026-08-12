using GeoraePlan.Mobile.App.Models;

namespace GeoraePlan.Mobile.App.Services;

internal enum MobileImmediateMutationOutcome
{
    NotApplicable = 0,
    Unknown = 1,
    Accepted = 2,
    Rejected = 3
}

internal sealed record MobileImmediatePaymentSaveResult(
    MobileSyncState State,
    MobileImmediateMutationOutcome PaymentOutcome,
    MobileImmediateMutationOutcome LinkedTransactionOutcome)
{
    public bool PaymentAccepted =>
        PaymentOutcome ==
        MobileImmediateMutationOutcome.Accepted;

    public bool PaymentRejected =>
        PaymentOutcome ==
        MobileImmediateMutationOutcome.Rejected;

    public bool CanInvokeSuccessCallback =>
        PaymentOutcome is
            MobileImmediateMutationOutcome.Accepted or
            MobileImmediateMutationOutcome.Unknown;

    public bool LinkedTransactionNeedsRecovery =>
        PaymentAccepted &&
        LinkedTransactionOutcome is
            MobileImmediateMutationOutcome.Unknown or
            MobileImmediateMutationOutcome.Rejected;

    public string BuildStatusMessage(
        string paymentActionText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            paymentActionText);

        if (PaymentRejected)
        {
            return
                $"{paymentActionText}이 저장되지 않았습니다. {State.LastError}";
        }

        if (!PaymentAccepted)
        {
            return
                $"{paymentActionText} 저장 완료(동기화/첨부 대기): {State.LastError}";
        }

        if (LinkedTransactionNeedsRecovery)
        {
            return
                $"{paymentActionText} 저장 및 서버 반영 완료 / 연결 거래 복구 대기: {State.LastError}";
        }

        if (State.PendingPaymentAttachmentCount > 0)
        {
            return
                $"{paymentActionText} 저장 완료 / 첨부 {State.PendingPaymentAttachmentCount:N0}건은 네트워크 복구 후 자동 업로드됩니다.";
        }

        return string.IsNullOrWhiteSpace(State.LastError)
            ? $"{paymentActionText} 저장 및 서버 반영 완료"
            : $"{paymentActionText} 저장 완료 / 최신 데이터 새로고침 대기: {State.LastError}";
    }
}
