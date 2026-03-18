using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("conflicts")]
public sealed class ConflictLogsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    public ConflictLogsController(AppDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    public async Task<ActionResult<List<ConflictLogDto>>> GetAll(
        [FromQuery] int take = 200,
        CancellationToken cancellationToken = default)
        => Ok(await _dbContext.ConflictLogs.AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(Math.Min(take, 500)).Select(x => x.ToDto()).ToListAsync(cancellationToken));
}
