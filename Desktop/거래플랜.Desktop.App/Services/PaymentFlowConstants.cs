namespace 거래플랜.Desktop.App.Services;

public static class PaymentFlowConstants
{
    public const string TransactionKindReceipt = "일반수금";
    public const string TransactionKindPayment = "일반지급";
    public const string TransactionKindInvoiceReceipt = "전표수금";
    public const string TransactionKindInvoicePayment = "전표지급";
    public const string TransactionKindAdvanceDeposit = "선수금입금";
    public const string TransactionKindAdvanceRefund = "선수금환불";
    public const string TransactionKindAdvanceApply = "선수금차감";
    public const string TransactionKindRentalReceipt = "렌탈수금";

    public const string BillingStatusPlanned = "예정";
    public const string BillingStatusInProgress = "청구중";
    public const string BillingStatusOnHold = "보류";
    public const string BillingStatusCancelled = "취소";
    public const string BillingStatusCompleted = "완료";

    public const string SettlementStatusUnpaid = "미입금";
    public const string SettlementStatusPending = "확인대기";
    public const string SettlementStatusPartial = "부분입금";
    public const string SettlementStatusConfirmed = "입금확인";
    public const string SettlementStatusCardPending = "카드결제대기";
    public const string SettlementStatusCardApproved = "카드승인완료";
    public const string SettlementStatusCmsPending = "CMS대기";
    public const string SettlementStatusCmsFailed = "CMS실패";
    public const string SettlementStatusRefunded = "환불";

    public const string CompletionPending = "미완료";
    public const string CompletionDone = "완료";

    public static bool IsAdvanceKind(string? kind)
        => string.Equals(kind, TransactionKindAdvanceDeposit, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, TransactionKindAdvanceRefund, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, TransactionKindAdvanceApply, StringComparison.OrdinalIgnoreCase);

    public static bool IsInvoiceSettlementKind(string? kind)
        => string.Equals(kind, TransactionKindInvoiceReceipt, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, TransactionKindInvoicePayment, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, TransactionKindAdvanceApply, StringComparison.OrdinalIgnoreCase);

    public static bool IsGeneralSettlementKind(string? kind)
        => string.Equals(kind, TransactionKindReceipt, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, TransactionKindPayment, StringComparison.OrdinalIgnoreCase);

    public static bool IsRentalSettlementKind(string? kind)
        => string.Equals(kind, TransactionKindRentalReceipt, StringComparison.OrdinalIgnoreCase);

    public static bool IsReceiptKind(string? kind)
        => NormalizeTransactionKind(kind) switch
        {
            TransactionKindReceipt => true,
            TransactionKindInvoiceReceipt => true,
            TransactionKindAdvanceDeposit => true,
            TransactionKindAdvanceApply => true,
            TransactionKindRentalReceipt => true,
            _ => false
        };

    public static bool IsPaymentKind(string? kind)
        => NormalizeTransactionKind(kind) switch
        {
            TransactionKindPayment => true,
            TransactionKindInvoicePayment => true,
            TransactionKindAdvanceRefund => true,
            _ => false
        };

    public static string NormalizeTransactionKind(string? kind, bool preferPayment = false)
    {
        var trimmed = (kind ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return preferPayment ? TransactionKindPayment : TransactionKindReceipt;

        return trimmed switch
        {
            TransactionKindReceipt => TransactionKindReceipt,
            TransactionKindPayment => TransactionKindPayment,
            TransactionKindInvoiceReceipt => TransactionKindInvoiceReceipt,
            TransactionKindInvoicePayment => TransactionKindInvoicePayment,
            TransactionKindAdvanceDeposit => TransactionKindAdvanceDeposit,
            TransactionKindAdvanceRefund => TransactionKindAdvanceRefund,
            TransactionKindAdvanceApply => TransactionKindAdvanceApply,
            TransactionKindRentalReceipt => TransactionKindRentalReceipt,
            _ => preferPayment ? TransactionKindPayment : TransactionKindReceipt
        };
    }

    public static string GetTransactionKindDisplayName(string? kind)
    {
        var normalized = NormalizeTransactionKind(kind);
        return normalized switch
        {
            TransactionKindAdvanceRefund => "선수금 사용",
            _ => normalized
        };
    }

    public static string NormalizeBillingStatus(string? status)
    {
        var trimmed = (status ?? string.Empty).Trim();
        return trimmed switch
        {
            BillingStatusPlanned => BillingStatusPlanned,
            BillingStatusInProgress => BillingStatusInProgress,
            BillingStatusOnHold => BillingStatusOnHold,
            BillingStatusCancelled => BillingStatusCancelled,
            BillingStatusCompleted => BillingStatusCompleted,
            _ => BillingStatusPlanned
        };
    }

    public static string NormalizeSettlementStatus(string? status)
    {
        var trimmed = (status ?? string.Empty).Trim();
        return trimmed switch
        {
            SettlementStatusUnpaid => SettlementStatusUnpaid,
            SettlementStatusPending => SettlementStatusPending,
            SettlementStatusPartial => SettlementStatusPartial,
            SettlementStatusConfirmed => SettlementStatusConfirmed,
            SettlementStatusCardPending => SettlementStatusCardPending,
            SettlementStatusCardApproved => SettlementStatusCardApproved,
            SettlementStatusCmsPending => SettlementStatusCmsPending,
            SettlementStatusCmsFailed => SettlementStatusCmsFailed,
            SettlementStatusRefunded => SettlementStatusRefunded,
            _ => SettlementStatusUnpaid
        };
    }

    public static string NormalizeCompletionStatus(string? status)
    {
        var trimmed = (status ?? string.Empty).Trim();
        return string.Equals(trimmed, CompletionDone, StringComparison.OrdinalIgnoreCase)
            ? CompletionDone
            : CompletionPending;
    }

    public static string GetPendingSettlementStatus(string? billingMethod)
    {
        var method = (billingMethod ?? string.Empty).Trim();
        return method switch
        {
            "카드" => SettlementStatusCardPending,
            "CMS" => SettlementStatusCmsPending,
            _ => SettlementStatusPending
        };
    }

    public static string GetDisplaySettlementCompleteStatus(string? billingMethod)
    {
        var method = (billingMethod ?? string.Empty).Trim();
        return method switch
        {
            "카드" => SettlementStatusCardApproved,
            _ => SettlementStatusConfirmed
        };
    }
}
