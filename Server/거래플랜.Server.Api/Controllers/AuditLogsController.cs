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
[Route("audit")]
public sealed class AuditLogsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly OperationalLogScopeService _operationalLogScopeService;

    public AuditLogsController(
        AppDbContext dbContext,
        OperationalLogScopeService operationalLogScopeService)
    {
        _dbContext = dbContext;
        _operationalLogScopeService = operationalLogScopeService;
    }

    [HttpGet]
    public async Task<ActionResult<List<AuditLogDto>>> GetAll(
        [FromQuery] string? entityName,
        [FromQuery] int take = 200,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityName))
            query = query.Where(x => x.EntityName == entityName);

        var rows = await _operationalLogScopeService.TakeVisibleAuditLogsAsync(
            query.OrderByDescending(x => x.CreatedAtUtc),
            take,
            cancellationToken);

        return Ok(rows.Select(x => x.ToDto()).ToList());
    }
}
