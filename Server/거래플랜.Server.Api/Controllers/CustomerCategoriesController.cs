using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("customer-categories")]
public sealed class CustomerCategoriesController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    public CustomerCategoriesController(AppDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    public async Task<ActionResult<List<CustomerCategoryDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _dbContext.CustomerCategories.AsNoTracking().Select(x => x.ToDto()).ToListAsync(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<CustomerCategoryDto>> Create([FromBody] CustomerCategoryDto dto, CancellationToken cancellationToken)
    {
        var entity = new CustomerCategory { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        entity.Apply(dto);
        _dbContext.CustomerCategories.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CustomerCategoryDto>> Update(Guid id, [FromBody] CustomerCategoryDto dto, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.CustomerCategories.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, entity, dto, nameof(CustomerCategory)) is { } conflict)
            return conflict;
        entity.Apply(dto);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }
}
