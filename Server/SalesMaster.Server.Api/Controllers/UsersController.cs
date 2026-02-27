using SalesMaster.Server.Api.Data;
using SalesMaster.Server.Api.Domain;
using SalesMaster.Server.Api.Mappings;
using SalesMaster.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SalesMaster.Server.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("users")]
public sealed class UsersController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    public UsersController(AppDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    public async Task<ActionResult<List<UserAccountDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _dbContext.Users.Include(x => x.Permissions).AsNoTracking()
            .Select(x => x.ToDto()).ToListAsync(cancellationToken));

    [HttpPut("{id:guid}/permissions")]
    public async Task<ActionResult<UserAccountDto>> UpdatePermissions(
        Guid id, [FromBody] UpdateUserPermissionsRequest request, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.Include(x => x.Permissions)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null) return NotFound();

        _dbContext.UserPermissions.RemoveRange(user.Permissions);
        foreach (var perm in request.Permissions.Distinct())
        {
            user.Permissions.Add(new UserPermission { UserId = user.Id, Permission = perm });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(user.ToDto());
    }
}
