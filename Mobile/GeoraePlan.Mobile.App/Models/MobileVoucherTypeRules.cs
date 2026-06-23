using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Models;

public static class MobileVoucherTypeRules
{
    public static bool IsPaymentVoucher(VoucherType voucherType)
        => voucherType is VoucherType.Purchase or VoucherType.Procurement;

    public static bool IsPaymentVoucher(VoucherType? voucherType)
        => voucherType.HasValue && IsPaymentVoucher(voucherType.Value);

    public static string GetInvoiceKindLabel(VoucherType voucherType)
        => voucherType switch
        {
            VoucherType.Purchase => "구매",
            VoucherType.Procurement => "발주",
            _ => "판매"
        };

    public static string GetDocumentKindLabel(VoucherType voucherType)
        => voucherType switch
        {
            VoucherType.Purchase => "구매(매입)",
            VoucherType.Procurement => "발주",
            _ => "판매(매출)"
        };

    public static string GetPortableFileKind(VoucherType voucherType)
        => voucherType switch
        {
            VoucherType.Purchase => "purchase-invoice",
            VoucherType.Procurement => "procurement-invoice",
            _ => "sales-invoice"
        };
}
