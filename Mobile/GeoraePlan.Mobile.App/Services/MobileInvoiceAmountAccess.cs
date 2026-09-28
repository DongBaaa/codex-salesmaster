using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public static class MobileInvoiceAmountAccess
{
    public static bool CanView(VoucherType type, bool authenticated, string role, IEnumerable<string> permissions)
    {
        var access = MobileItemAmountAccess.Capture(authenticated, role, permissions, false);
        return type switch
        {
            VoucherType.Sales or VoucherType.Collection => access.Sales,
            VoucherType.Purchase or VoucherType.Procurement or VoucherType.Expense => access.Purchase,
            _ => false
        };
    }
}
