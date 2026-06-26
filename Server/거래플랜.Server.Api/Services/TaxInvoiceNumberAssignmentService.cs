using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public static class TaxInvoiceNumberAssignmentService
{
    private const string Prefix = "TAX";

    public static async Task<string?> EnsureAssignedAsync(
        AppDbContext dbContext,
        Invoice invoice,
        CancellationToken cancellationToken = default)
    {
        if (!invoice.TaxInvoiceIssued)
        {
            invoice.TaxInvoiceNumber = string.Empty;
            return null;
        }

        var current = invoice.TaxInvoiceNumber?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(current))
        {
            invoice.TaxInvoiceNumber = current;
            return null;
        }

        var assigned = await GenerateAsync(dbContext, invoice.InvoiceDate, cancellationToken);
        invoice.TaxInvoiceNumber = assigned;
        return assigned;
    }

    public static async Task<string> GenerateAsync(
        AppDbContext dbContext,
        DateOnly invoiceDate,
        CancellationToken cancellationToken = default)
    {
        var prefix = $"{Prefix}-{invoiceDate:yyyyMM}-";
        var numbers = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.TaxInvoiceNumber.StartsWith(prefix))
            .Select(invoice => invoice.TaxInvoiceNumber)
            .ToListAsync(cancellationToken);

        var maxSequence = 0;
        foreach (var number in numbers)
        {
            if (TryParseSequence(number, prefix, out var sequence))
                maxSequence = Math.Max(maxSequence, sequence);
        }

        return $"{prefix}{maxSequence + 1:0000}";
    }

    private static bool TryParseSequence(string? number, string prefix, out int sequence)
    {
        sequence = 0;
        if (string.IsNullOrWhiteSpace(number) ||
            !number.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(number[prefix.Length..], out sequence);
    }
}
