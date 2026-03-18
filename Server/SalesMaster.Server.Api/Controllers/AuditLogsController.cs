using SalesMaster.Server.Api.Data;
using SalesMaster.Server.Api.Mappings;
using SalesMaster.Server.Api.Security;
using SalesMaster.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SalesMaster.Server.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
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
