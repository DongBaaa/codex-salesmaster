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
[Route("customer-categories")]
public sealed class CustomerCategoriesController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    public CustomerCategoriesController(AppDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    public async Task<ActionResult<List<CustomerCategoryDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _dbContext.CustomerCategories.AsNoTracking().Select(x => x.ToDto()).ToListAsync(cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionNames.SettingsEdit)]
    public async Task<ActionResult<CustomerCategoryDto>> Create([FromBody] CustomerCategoryDto dto, CancellationToken cancellationToken)
    {
        var normalizedName = DefaultCustomerCategories.NormalizeName(dto.Name);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return BadRequest("고객분류명은 필수입니다.");
        if (dto.IsDeleted)
            return SoftDeleteMutationGuard.RejectCreate("거래처분류");

        var entity = new CustomerCategory { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        if (!dto.IsDeleted && await HasActiveDuplicateNameAsync(normalizedName, entity.Id, cancellationToken))
            return Conflict("같은 고객분류명이 이미 존재합니다.");

        dto.Name = normalizedName;
        entity.Apply(dto);
        _dbContext.CustomerCategories.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionNames.SettingsEdit)]
    public async Task<ActionResult<CustomerCategoryDto>> Update(Guid id, [FromBody] CustomerCategoryDto dto, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.CustomerCategories.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, entity, dto, nameof(CustomerCategory)) is { } conflict)
            return conflict;
        if (dto.IsDeleted)
            return SoftDeleteMutationGuard.RejectUpdate("거래처분류");
        var normalizedName = DefaultCustomerCategories.NormalizeName(dto.Name);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return BadRequest("고객분류명은 필수입니다.");
        if (!dto.IsDeleted && await HasActiveDuplicateNameAsync(normalizedName, id, cancellationToken))
            return Conflict("같은 고객분류명이 이미 존재합니다.");

        dto.Name = normalizedName;
        entity.Apply(dto);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionNames.SettingsEdit)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] long? expectedRevision, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.CustomerCategories.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, entity, expectedRevision, nameof(CustomerCategory)) is { } conflict)
            return conflict;
        var referenceBlockMessage = await CustomerCategoryDeletionReferenceGuard.BuildReferenceBlockMessageAsync(
            _dbContext,
            entity.Id,
            cancellationToken);
        if (referenceBlockMessage is not null)
            return Conflict(new
            {
                error = CustomerCategoryDeletionReferenceGuard.ConflictCode,
                message = referenceBlockMessage
            });

        entity.IsDeleted = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<bool> HasActiveDuplicateNameAsync(string normalizedName, Guid excludeId, CancellationToken cancellationToken)
    {
        var candidates = await _dbContext.CustomerCategories
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(category => category.Id != excludeId && !category.IsDeleted)
            .Select(category => category.Name)
            .ToListAsync(cancellationToken);

        return candidates.Any(name =>
            string.Equals(DefaultCustomerCategories.NormalizeName(name), normalizedName, StringComparison.CurrentCultureIgnoreCase));
    }
}
