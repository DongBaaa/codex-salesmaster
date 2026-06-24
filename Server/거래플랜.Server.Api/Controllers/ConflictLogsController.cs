using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize(Policy = "AdminOrGod")]
[Route("conflicts")]
public sealed class ConflictLogsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly OperationalLogScopeService _operationalLogScopeService;

    public ConflictLogsController(
        AppDbContext dbContext,
        OperationalLogScopeService operationalLogScopeService)
    {
        _dbContext = dbContext;
        _operationalLogScopeService = operationalLogScopeService;
    }

    [HttpGet]
    public async Task<ActionResult<List<ConflictLogDto>>> GetAll(
        [FromQuery] bool includeResolved = false,
        [FromQuery] int take = 200,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.ConflictLogs.AsNoTracking();
        if (!includeResolved)
        {
            query = query.Where(x => x.Status != "Resolved");
        }

        var rows = await _operationalLogScopeService.TakeVisibleConflictLogsAsync(
            query
                .OrderBy(x => x.Status == "Resolved" ? 1 : 0)
                .ThenByDescending(x => x.CreatedAtUtc),
            take,
            cancellationToken);

        return Ok(rows.Select(x => x.ToDto()).ToList());
    }

    [HttpPost("{id:guid}/resolve")]
    public async Task<ActionResult<ConflictLogDto>> Resolve(
        Guid id,
        [FromQuery] string? note = null,
        CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.ConflictLogs.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();

        if (!await _operationalLogScopeService.CanReadLogTargetAsync(
                entity.EntityName,
                entity.EntityId,
                cancellationToken))
        {
            return NotFound();
        }

        entity.Status = "Resolved";
        entity.ResolvedAtUtc = DateTime.UtcNow;
        entity.ResolutionNote = (note ?? string.Empty).Trim();
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }
}
