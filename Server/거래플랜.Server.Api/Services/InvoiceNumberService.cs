using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public interface IInvoiceNumberService
{
    Task<string> GenerateAsync(Guid customerId, DateOnly invoiceDate, CancellationToken cancellationToken = default);

    Task<string> GenerateAsync(
        Guid customerId,
        DateOnly invoiceDate,
        IEnumerable<string> reservedInvoiceNumbers,
        CancellationToken cancellationToken = default) =>
        GenerateAsync(customerId, invoiceDate, cancellationToken);
}

public sealed class InvoiceNumberService : IInvoiceNumberService
{
    private readonly AppDbContext _dbContext;

    public InvoiceNumberService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<string> GenerateAsync(
        Guid customerId,
        DateOnly invoiceDate,
        CancellationToken cancellationToken = default) =>
        GenerateAsync(customerId, invoiceDate, [], cancellationToken);

    public async Task<string> GenerateAsync(
        Guid customerId,
        DateOnly invoiceDate,
        IEnumerable<string> reservedInvoiceNumbers,
        CancellationToken cancellationToken = default)
    {
        var prefix = $"{invoiceDate:yyyyMM}-";
        var numbers = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(x => x.CustomerId == customerId && x.InvoiceNumber.StartsWith(prefix))
            .Select(x => x.InvoiceNumber)
            .ToListAsync(cancellationToken);
        numbers.AddRange(
            _dbContext.ChangeTracker
                .Entries<Invoice>()
                .Where(entry => entry.State != EntityState.Detached)
                .Select(entry => entry.Entity)
                .Where(invoice =>
                    invoice.CustomerId == customerId &&
                    invoice.InvoiceNumber?.StartsWith(prefix, StringComparison.Ordinal) == true)
                .Select(invoice => invoice.InvoiceNumber));
        numbers.AddRange(reservedInvoiceNumbers ?? []);

        var maxSequence = 0;
        foreach (var number in numbers)
        {
            if (TryParseSequence(number, prefix, out var sequence))
                maxSequence = Math.Max(maxSequence, sequence);
        }

        if (maxSequence == int.MaxValue)
            throw new InvalidOperationException($"Invoice number sequence is exhausted for prefix '{prefix}'.");

        return $"{prefix}{maxSequence + 1:0000}";
    }

    private static bool TryParseSequence(
        string? number,
        string prefix,
        out int sequence)
    {
        sequence = 0;
        if (string.IsNullOrWhiteSpace(number) ||
            !number.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(number[prefix.Length..], out sequence);
    }
}
