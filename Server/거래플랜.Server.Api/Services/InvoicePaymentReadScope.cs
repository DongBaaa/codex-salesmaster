using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

public static class InvoicePaymentReadScope
{
    public static async Task FilterAsync(
        IReadOnlyCollection<InvoiceDto> invoices,
        AppDbContext dbContext,
        OfficeScopeService officeScopeService,
        CancellationToken cancellationToken)
    {
        var paymentIds = invoices.SelectMany(invoice => invoice.Payments ?? [])
            .Where(payment => payment.Id != Guid.Empty)
            .Select(payment => payment.Id)
            .Distinct()
            .ToList();
        var readableIds = paymentIds.Count == 0
            ? new HashSet<Guid>()
            : (await officeScopeService.ApplyPaymentScope(dbContext.Payments.IgnoreQueryFilters().AsNoTracking())
                .Where(payment => paymentIds.Contains(payment.Id))
                .Select(payment => payment.Id)
                .ToListAsync(cancellationToken)).ToHashSet();

        // Invoice sharing and payment sharing are independent, including nested responses.
        foreach (var invoice in invoices)
            invoice.Payments = (invoice.Payments ?? [])
                .Where(payment => payment.Id != Guid.Empty && readableIds.Contains(payment.Id))
                .ToList();
    }
}
