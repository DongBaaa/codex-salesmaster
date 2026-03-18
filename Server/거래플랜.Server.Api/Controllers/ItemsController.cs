using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("items")]
public sealed class ItemsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    public ItemsController(AppDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    public async Task<ActionResult<List<ItemDto>>> GetAll([FromQuery] string? q, CancellationToken cancellationToken)
    {
        var query = _dbContext.Items.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(x => x.NameOriginal.Contains(q) || x.SpecificationOriginal.Contains(q));
        }

        return Ok(await query.OrderBy(x => x.NameOriginal).Take(200).Select(x => x.ToDto()).ToListAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ItemDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity.ToDto());
    }

    [HttpPost]
    public async Task<ActionResult<ItemDto>> Create([FromBody] ItemDto dto, CancellationToken cancellationToken)
    {
        var entity = new Item { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        entity.Apply(dto);
        _dbContext.Items.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ItemDto>> Update(Guid id, [FromBody] ItemDto dto, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Items.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        entity.Apply(dto);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Items.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        entity.IsDeleted = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
