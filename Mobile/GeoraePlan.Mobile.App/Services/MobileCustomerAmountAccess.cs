using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

/// <summary>Display copies only; original API/cache data remains unchanged.</summary>
public static class MobileCustomerAmountAccess
{
    public static InvoiceDto Display(InvoiceDto invoice, SessionSnapshot session)
    {
        if (CanView(invoice.VoucherType, session) && !invoice.AmountsHidden) return invoice;
        var copy = JsonSerializer.Deserialize<InvoiceDto>(JsonSerializer.Serialize(invoice))!;
        copy.TotalAmount = copy.SupplyAmount = copy.VatAmount = null;
        foreach (var line in copy.Lines ?? []) line.UnitPrice = line.LineAmount = null;
        foreach (var payment in copy.Payments ?? []) payment.Amount = null;
        return copy;
    }

    public static CustomerPaymentHistoryRow Display(CustomerPaymentHistoryRow row, SessionSnapshot session)
        => CanView(row.VoucherType, session) ? row : new()
        {
            PaymentId = row.PaymentId, InvoiceId = row.InvoiceId, InvoiceNumber = row.InvoiceNumber,
            VoucherType = row.VoucherType, PaymentDate = row.PaymentDate, Amount = null,
            Note = row.Note, AttachmentCount = row.AttachmentCount, Attachments = row.Attachments,
            UpdatedAtUtc = row.UpdatedAtUtc
        };

    private static bool CanView(VoucherType type, SessionSnapshot session)
        => MobileInvoiceAmountAccess.CanView(type, session.IsAuthenticated, session.Role, session.Permissions);
}
