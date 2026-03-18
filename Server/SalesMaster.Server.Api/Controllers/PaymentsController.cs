using SalesMaster.Server.Api.Data;
using SalesMaster.Server.Api.Domain;
using SalesMaster.Server.Api.Mappings;
using SalesMaster.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SalesMaster.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("payments")]
public sealed class PaymentsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    public PaymentsController(AppDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    public async Task<ActionResult<List<PaymentDto>>> GetByInvoice([FromQuery] Guid invoiceId, CancellationToken cancellationToken)
        => Ok(await _dbContext.Payments.AsNoTracking()
            .Where(x => x.InvoiceId == invoiceId)
            .OrderByDescending(x => x.PaymentDate)
            .Select(x => x.ToDto()).ToListAsync(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<PaymentDto>> Create([FromBody] PaymentDto dto, CancellationToken cancellationToken)
    {
        var entity = new Payment { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        entity.Apply(dto);
        _dbContext.Payments.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PaymentDto>> Update(Guid id, [FromBody] PaymentDto dto, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Payments.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        entity.Apply(dto);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Payments.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        entity.IsDeleted = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
