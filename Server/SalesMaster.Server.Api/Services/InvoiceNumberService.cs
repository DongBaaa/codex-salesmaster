using SalesMaster.Server.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace SalesMaster.Server.Api.Services;

public interface IInvoiceNumberService
{
    Task<string> GenerateAsync(Guid customerId, DateOnly invoiceDate, CancellationToken cancellationToken = default);
}

public sealed class InvoiceNumberService : IInvoiceNumberService
{
    private readonly AppDbContext _dbContext;

    public InvoiceNumberService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<string> GenerateAsync(Guid customerId, DateOnly invoiceDate, CancellationToken cancellationToken = default)
    {
        var prefix = $"{invoiceDate:yyyyMM}-";
        var numbers = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(x => x.CustomerId == customerId && x.InvoiceNumber.StartsWith(prefix))
            .Select(x => x.InvoiceNumber)
            .ToListAsync(cancellationToken);

        var maxSequence = 0;
        foreach (var number in numbers)
        {
            var split = number.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (split.Length == 2 && int.TryParse(split[1], out var seq))
            {
                maxSequence = Math.Max(maxSequence, seq);
            }
        }

        return $"{prefix}{(maxSequence + 1):0000}";
    }
}
