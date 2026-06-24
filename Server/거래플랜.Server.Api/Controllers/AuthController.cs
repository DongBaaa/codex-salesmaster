using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Security;
using 거래플랜.Shared.Contracts;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Route("auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IJwtTokenFactory _jwtTokenFactory;

    public AuthController(AppDbContext dbContext, IJwtTokenFactory jwtTokenFactory)
    {
        _dbContext = dbContext;
        _jwtTokenFactory = jwtTokenFactory;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return Unauthorized();
        }

        var username = request.Username.Trim();
        var user = await _dbContext.Users
            .IgnoreQueryFilters()
            .Include(x => x.Permissions)
            .FirstOrDefaultAsync(x => x.Username == username && x.IsActive && !x.IsDeleted, cancellationToken);

        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized();
        }

        var response = _jwtTokenFactory.Create(user);
        return Ok(response);
    }

    [HttpPost("refresh")]
    [Authorize]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Refresh(CancellationToken cancellationToken)
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out var userId))
            return Unauthorized();

        var user = await _dbContext.Users
            .IgnoreQueryFilters()
            .Include(x => x.Permissions)
            .FirstOrDefaultAsync(x => x.Id == userId && x.IsActive && !x.IsDeleted, cancellationToken);

        if (user is null)
            return Unauthorized();

        var response = _jwtTokenFactory.Create(user);
        return Ok(response);
    }
}
