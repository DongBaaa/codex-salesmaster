using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("invoices")]
public sealed class InvoicesController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IInvoiceNumberService _invoiceNumberService;

    public InvoicesController(AppDbContext dbContext, ICurrentUserContext currentUserContext, IInvoiceNumberService invoiceNumberService)
    {
        _dbContext = dbContext;
        _currentUserContext = currentUserContext;
        _invoiceNumberService = invoiceNumberService;
    }

    [HttpGet]
    public async Task<ActionResult<List<InvoiceDto>>> GetAll(
        [FromQuery] Guid? customerId,
        [FromQuery] string? q,
        [FromQuery] int take = 200,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Invoices.Include(x => x.Lines).AsNoTracking();
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(x => x.InvoiceNumber.Contains(q) || x.Memo.Contains(q));
        }

        return Ok(await query.OrderByDescending(x => x.InvoiceDate)
            .Take(Math.Min(take, 500)).Select(x => x.ToDto()).ToListAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InvoiceDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Invoices.Include(x => x.Lines)
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity.ToDto());
    }

    [HttpPost]
    public async Task<ActionResult<InvoiceDto>> Create([FromBody] InvoiceDto dto, CancellationToken cancellationToken)
    {
        var entity = new Invoice { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        entity.Apply(dto);
        if (string.IsNullOrWhiteSpace(entity.InvoiceNumber))
        {
            entity.InvoiceNumber = await _invoiceNumberService.GenerateAsync(entity.CustomerId, entity.InvoiceDate, cancellationToken);
        }

        foreach (var line in dto.Lines)
        {
            entity.Lines.Add(new InvoiceLine
            {
                Id = line.Id == Guid.Empty ? Guid.NewGuid() : line.Id,
                InvoiceId = entity.Id, ItemId = line.ItemId,
                ItemNameOriginal = line.ItemNameOriginal, SpecificationOriginal = line.SpecificationOriginal,
                Unit = line.Unit, Quantity = line.Quantity, UnitPrice = line.UnitPrice,
                LineAmount = line.LineAmount == 0 ? line.Quantity * line.UnitPrice : line.LineAmount,
                Remark = line.Remark
            });
        }

        _dbContext.Invoices.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<InvoiceDto>> Update(Guid id, [FromBody] InvoiceDto dto, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Invoices.Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        entity.Apply(dto);
        _dbContext.InvoiceLines.RemoveRange(entity.Lines);
        entity.Lines.Clear();
        foreach (var line in dto.Lines)
        {
            entity.Lines.Add(new InvoiceLine
            {
                Id = line.Id == Guid.Empty ? Guid.NewGuid() : line.Id,
                InvoiceId = entity.Id, ItemId = line.ItemId,
                ItemNameOriginal = line.ItemNameOriginal, SpecificationOriginal = line.SpecificationOriginal,
                Unit = line.Unit, Quantity = line.Quantity, UnitPrice = line.UnitPrice,
                LineAmount = line.LineAmount == 0 ? line.Quantity * line.UnitPrice : line.LineAmount,
                Remark = line.Remark
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Invoices.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        entity.IsDeleted = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
