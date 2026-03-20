using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("users")]
public sealed class UsersController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ICurrentUserContext _currentUserContext;

    public UsersController(AppDbContext dbContext, ICurrentUserContext currentUserContext)
    {
        _dbContext = dbContext;
        _currentUserContext = currentUserContext;
    }

    [HttpGet]
    public async Task<ActionResult<List<UserAccountDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _dbContext.Users.Include(x => x.Permissions).AsNoTracking()
            .Select(x => x.ToDto()).ToListAsync(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<UserAccountDto>> Create(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var username = request.Username.Trim();
        if (string.IsNullOrWhiteSpace(username))
            return BadRequest("Username is required.");

        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("Password is required.");

        var exists = await _dbContext.Users
            .IgnoreQueryFilters()
            .AnyAsync(user => user.Username == username, cancellationToken);
        if (exists)
            return Conflict("Username already exists.");

        if (!TryNormalizeOfficeCode(request.OfficeCode, out var normalizedOfficeCode))
            return BadRequest("OfficeCode must be one of USENET, ITWORLD, YEONSU.");

        var normalizedTenantCode = NormalizeTenantCode(request.TenantCode, normalizedOfficeCode);
        if (!TenantScopeCatalog.TenantContainsOffice(normalizedTenantCode, normalizedOfficeCode))
            return BadRequest("TenantCode and OfficeCode are not compatible.");

        var normalizedScopeType = NormalizeScopeType(request.ScopeType, request.Role);

        var user = new UserAccount
        {
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = NormalizeRole(request.Role),
            TenantCode = normalizedTenantCode,
            OfficeCode = normalizedOfficeCode,
            ScopeType = normalizedScopeType,
            IsActive = request.IsActive
        };

        ApplyPermissions(user, request.Permissions);
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetAll), new { id = user.Id }, user.ToDto());
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserAccountDto>> Update(
        Guid id,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users
            .Include(x => x.Permissions)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null)
            return NotFound();

        var username = request.Username.Trim();
        if (string.IsNullOrWhiteSpace(username))
            return BadRequest("Username is required.");

        var duplicated = await _dbContext.Users
            .IgnoreQueryFilters()
            .AnyAsync(current => current.Id != id && current.Username == username, cancellationToken);
        if (duplicated)
            return Conflict("Username already exists.");

        if (!TryNormalizeOfficeCode(request.OfficeCode, out var normalizedOfficeCode))
            return BadRequest("OfficeCode must be one of USENET, ITWORLD, YEONSU.");

        var normalizedTenantCode = NormalizeTenantCode(request.TenantCode, normalizedOfficeCode);
        if (!TenantScopeCatalog.TenantContainsOffice(normalizedTenantCode, normalizedOfficeCode))
            return BadRequest("TenantCode and OfficeCode are not compatible.");

        var normalizedScopeType = NormalizeScopeType(request.ScopeType, request.Role);

        user.Username = username;
        user.Role = NormalizeRole(request.Role);
        user.TenantCode = normalizedTenantCode;
        user.OfficeCode = normalizedOfficeCode;
        user.ScopeType = normalizedScopeType;
        user.IsActive = request.IsActive;
        ApplyPermissions(user, request.Permissions);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(user.ToDto());
    }

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

    [HttpPut("{id:guid}/password")]
    public async Task<ActionResult> UpdatePassword(
        Guid id,
        [FromBody] UpdateUserPasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("Password is required.");

        var user = await _dbContext.Users
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null)
            return NotFound();

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users
            .Include(x => x.Permissions)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null)
            return NotFound();

        if (_currentUserContext.UserId == id)
            return BadRequest("You cannot delete the currently signed-in account.");

        _dbContext.UserPermissions.RemoveRange(user.Permissions);
        _dbContext.Users.Remove(user);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string NormalizeRole(string? role)
        => string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase)
            ? "Admin"
            : "User";

    private static bool TryNormalizeOfficeCode(string? officeCode, out string normalized)
        => OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out normalized);

    private static string NormalizeTenantCode(string? tenantCode, string officeCode)
        => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode);

    private static string NormalizeScopeType(string? scopeType, string? role)
        => string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase)
            ? TenantScopeCatalog.ScopeAdmin
            : TenantScopeCatalog.NormalizeScopeTypeOrDefault(scopeType, TenantScopeCatalog.ScopeOfficeOnly);

    private void ApplyPermissions(UserAccount user, IEnumerable<string> permissions)
    {
        var normalizedPermissions = permissions
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _dbContext.UserPermissions.RemoveRange(user.Permissions);
        user.Permissions.Clear();
        foreach (var permission in normalizedPermissions)
        {
            user.Permissions.Add(new UserPermission
            {
                UserId = user.Id,
                Permission = permission
            });
        }
    }
}

