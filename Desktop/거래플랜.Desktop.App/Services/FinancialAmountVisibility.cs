namespace 거래플랜.Desktop.App.Services;

internal static class FinancialAmountVisibility
{
    internal readonly record struct AccessKey(Guid SessionId, long Epoch, bool Sales, bool Purchase);

    internal static AccessKey CaptureAccess(SessionState session) => new(session.SessionId, session.SyncScopeEpoch,
        session.HasPermission(AppPermissionNames.AmountViewSales), session.HasPermission(AppPermissionNames.AmountViewPurchase));

    internal static bool CanViewInvoice(SessionState session, 거래플랜.Shared.Contracts.VoucherType type) => type switch
    {
        거래플랜.Shared.Contracts.VoucherType.Sales or 거래플랜.Shared.Contracts.VoucherType.Collection => session.HasPermission(AppPermissionNames.AmountViewSales),
        거래플랜.Shared.Contracts.VoucherType.Purchase or 거래플랜.Shared.Contracts.VoucherType.Procurement or 거래플랜.Shared.Contracts.VoucherType.Expense => session.HasPermission(AppPermissionNames.AmountViewPurchase),
        _ => false
    };

    internal static string Format(decimal? amount, SessionState? session, bool purchase = false, bool currency = true)
        => session?.HasPermission(purchase ? AppPermissionNames.AmountViewPurchase : AppPermissionNames.AmountViewSales) == true && amount.HasValue
            ? amount.Value.ToString("N0") + (currency ? "원" : "") : "비공개";

    internal static bool CanViewTransaction(SessionState session, 거래플랜.Desktop.App.Data.LocalTransaction row,
        거래플랜.Shared.Contracts.VoucherType? linkedType)
    {
        if (row.AmountsHidden) return false;
        var salesPermission = session.HasPermission(AppPermissionNames.AmountViewSales);
        var purchasePermission = session.HasPermission(AppPermissionNames.AmountViewPurchase);
        if (salesPermission && purchasePermission) return true;
        var kind = row.TransactionKind?.Trim();
        var sales = kind is "일반수금" or "전표수금" or "선수금입금" or "선수금환불" or "선수금차감" or "렌탈수금";
        var purchase = kind is "일반지급" or "전표지급";
        if (!sales && !purchase) return false;
        if (row.LinkedInvoiceId is Guid id && id != Guid.Empty)
        {
            if (!linkedType.HasValue || !CanViewInvoice(session, linkedType.Value)) return false;
            if (sales != (linkedType is 거래플랜.Shared.Contracts.VoucherType.Sales or 거래플랜.Shared.Contracts.VoucherType.Collection)) return false;
        }
        // Match server conflict visibility: mixed-direction history requires both grants.
        if (sales && (row.PrepaidDelta != 0m || kind != "선수금환불" &&
            (row.CashPayment != 0m || row.CardPayment != 0m || row.BankPayment != 0m || row.DiscountReceived != 0m || row.PaymentTotal != 0m))) return false;
        if (purchase && (row.AdvanceDelta != 0m || row.CashReceipt != 0m || row.CardReceipt != 0m ||
            row.BankReceipt != 0m || row.DiscountApplied != 0m || row.ReceiptTotal != 0m)) return false;
        return sales ? salesPermission : purchasePermission;
    }
}
