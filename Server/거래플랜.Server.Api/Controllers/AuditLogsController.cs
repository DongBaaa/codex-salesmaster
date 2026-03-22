using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
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
    public AuditLogsController(AppDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    public async Task<ActionResult<List<AuditLogDto>>> GetAll(
        [FromQuery] string? entityName,
        [FromQuery] int take = 200,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityName))
            query = query.Where(x => x.EntityName == entityName);
        return Ok(await query.OrderByDescending(x => x.CreatedAtUtc)
            .Take(Math.Min(take, 1000)).Select(x => x.ToDto()).ToListAsync(cancellationToken));
    }
}
