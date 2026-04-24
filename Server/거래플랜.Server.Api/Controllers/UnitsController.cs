using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("units")]
public sealed class UnitsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    public UnitsController(AppDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    public async Task<ActionResult<List<UnitDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _dbContext.Units.AsNoTracking().Select(x => x.ToDto()).ToListAsync(cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionNames.SettingsEdit)]
    public async Task<ActionResult<UnitDto>> Create([FromBody] UnitDto dto, CancellationToken cancellationToken)
    {
        var entity = new Unit { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        entity.Apply(dto);
        _dbContext.Units.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionNames.SettingsEdit)]
    public async Task<ActionResult<UnitDto>> Update(Guid id, [FromBody] UnitDto dto, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Units.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, entity, dto, nameof(Unit)) is { } conflict)
            return conflict;
        entity.Apply(dto);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionNames.SettingsEdit)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] long? expectedRevision, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Units.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, entity, expectedRevision, nameof(Unit)) is { } conflict)
            return conflict;
        entity.IsDeleted = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
