using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
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
        var normalizedName = UnitCatalogNormalizer.Normalize(dto.Name);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return BadRequest(new { error = "unit_name_required", message = "단위명은 필수입니다." });
        if (dto.IsDeleted)
            return SoftDeleteMutationGuard.RejectCreate("단위");

        var entity = new Unit { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        if (!dto.IsDeleted &&
            dto.IsActive &&
            await HasActiveDuplicateNameAsync(normalizedName, entity.Id, cancellationToken))
        {
            return Conflict(new { error = "unit_name_duplicate", message = "같은 단위명이 이미 존재합니다." });
        }

        dto.Name = normalizedName;
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
        if (dto.IsDeleted)
            return SoftDeleteMutationGuard.RejectUpdate("단위");
        var normalizedName = UnitCatalogNormalizer.Normalize(dto.Name);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return BadRequest(new { error = "unit_name_required", message = "단위명은 필수입니다." });
        if (!dto.IsDeleted &&
            dto.IsActive &&
            await HasActiveDuplicateNameAsync(normalizedName, id, cancellationToken))
        {
            return Conflict(new { error = "unit_name_duplicate", message = "같은 단위명이 이미 존재합니다." });
        }

        dto.Name = normalizedName;
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
        var referenceBlockMessage = await UnitDeletionReferenceGuard.BuildReferenceBlockMessageAsync(
            _dbContext,
            entity.Name,
            cancellationToken);
        if (referenceBlockMessage is not null)
            return Conflict(new
            {
                error = UnitDeletionReferenceGuard.ConflictCode,
                message = referenceBlockMessage
            });

        entity.IsDeleted = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<bool> HasActiveDuplicateNameAsync(
        string normalizedName,
        Guid excludeId,
        CancellationToken cancellationToken)
    {
        var candidates = await _dbContext.Units
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(unit => unit.Id != excludeId && !unit.IsDeleted && unit.IsActive)
            .Select(unit => unit.Name)
            .ToListAsync(cancellationToken);

        return candidates.Any(name =>
            string.Equals(
                UnitCatalogNormalizer.Normalize(name),
                normalizedName,
                StringComparison.Ordinal));
    }
}
