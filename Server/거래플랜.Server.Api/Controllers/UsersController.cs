using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize(Policy = "AdminOrGod")]
[Route("users")]
public sealed class UsersController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly OfficeScopeService _officeScopeService;

    public UsersController(
        AppDbContext dbContext,
        ICurrentUserContext currentUserContext,
        OfficeScopeService officeScopeService)
    {
        _dbContext = dbContext;
        _currentUserContext = currentUserContext;
        _officeScopeService = officeScopeService;
    }

    [HttpGet]
    public async Task<ActionResult<List<UserAccountDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await ApplyUserManagementScope(_dbContext.Users.Include(x => x.Permissions).AsNoTracking())
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

        var normalizedRole = NormalizeRole(request.Role);
        var normalizedScopeType = NormalizeScopeType(request.ScopeType, normalizedRole);
        if (EnsureCanManageUserAssignment(normalizedTenantCode, normalizedOfficeCode, normalizedScopeType) is { } forbidden)
            return forbidden;

        var user = new UserAccount
        {
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = normalizedRole,
            TenantCode = normalizedTenantCode,
            OfficeCode = normalizedOfficeCode,
            ScopeType = normalizedScopeType,
            IsActive = request.IsActive
        };

        ApplyPermissions(user, request.Permissions);
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await PersistScopeTypeAsync(user.Id, normalizedScopeType, cancellationToken);
        await _dbContext.Entry(user).ReloadAsync(cancellationToken);

        return CreatedAtAction(nameof(GetAll), new { id = user.Id }, user.ToDto());
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserAccountDto>> Update(
        Guid id,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await ApplyUserManagementScope(_dbContext.Users
            .Include(x => x.Permissions)
            .AsQueryable())
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null)
            return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, user, request.ExpectedRevision, nameof(UserAccount)) is { } conflict)
            return conflict;

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

        var normalizedRole = NormalizeRole(request.Role);
        var normalizedScopeType = NormalizeScopeType(request.ScopeType, normalizedRole);
        if (EnsureCanManageUserAssignment(normalizedTenantCode, normalizedOfficeCode, normalizedScopeType) is { } forbidden)
            return forbidden;

        user.Username = username;
        user.Role = normalizedRole;
        user.TenantCode = normalizedTenantCode;
        user.OfficeCode = normalizedOfficeCode;
        user.ScopeType = normalizedScopeType;
        user.IsActive = request.IsActive;
        ApplyPermissions(user, request.Permissions);

        // An accepted aggregate update must always return a fresh concurrency token,
        // including scalar no-op and permission-only requests used before a password change.
        _dbContext.Entry(user)
            .Property(current => current.UpdatedAtUtc)
            .IsModified = true;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await PersistScopeTypeAsync(user.Id, normalizedScopeType, cancellationToken);
        await _dbContext.Entry(user).ReloadAsync(cancellationToken);
        return Ok(user.ToDto());
    }

    [HttpPut("{id:guid}/permissions")]
    public async Task<ActionResult<UserAccountDto>> UpdatePermissions(
        Guid id, [FromBody] UpdateUserPermissionsRequest request, CancellationToken cancellationToken)
    {
        var user = await ApplyUserManagementScope(_dbContext.Users.Include(x => x.Permissions).AsQueryable())
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null) return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, user, request.ExpectedRevision, nameof(UserAccount)) is { } conflict)
            return conflict;

        _dbContext.UserPermissions.RemoveRange(user.Permissions);
        foreach (var perm in request.Permissions.Distinct())
        {
            user.Permissions.Add(new UserPermission { UserId = user.Id, Permission = perm });
        }

        // Permissions are part of the user aggregate. Mark the aggregate itself
        // as modified so its optimistic-concurrency revision advances with them.
        _dbContext.Entry(user)
            .Property(current => current.UpdatedAtUtc)
            .IsModified = true;
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

        var user = await ApplyUserManagementScope(_dbContext.Users.AsQueryable())
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null)
            return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, user, request.ExpectedRevision, nameof(UserAccount)) is { } conflict)
            return conflict;

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, [FromQuery] long? expectedRevision, CancellationToken cancellationToken)
    {
        var user = await ApplyUserManagementScope(_dbContext.Users
            .Include(x => x.Permissions)
            .AsQueryable())
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null)
            return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, user, expectedRevision, nameof(UserAccount)) is { } conflict)
            return conflict;

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
    {
        if (TenantScopeCatalog.TryNormalizeScopeType(scopeType, out var normalized))
            return normalized;

        return string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase)
            ? TenantScopeCatalog.ScopeOfficeOnly
            : TenantScopeCatalog.ScopeOfficeOnly;
    }

    private async Task PersistScopeTypeAsync(Guid userId, string normalizedScopeType, CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Users" SET "ScopeType" = {normalizedScopeType} WHERE "Id" = {userId};""",
            cancellationToken);
    }

    private IQueryable<UserAccount> ApplyUserManagementScope(IQueryable<UserAccount> query)
    {
        if (_officeScopeService.HasSystemConfigurationScope)
            return query;

        var tenantCode = _officeScopeService.CurrentTenantCode;
        var manageableOffices = ResolveIntrinsicManageableOfficeCodes();
        return query.Where(user =>
            user.TenantCode == tenantCode &&
            manageableOffices.Contains(user.OfficeCode) &&
            user.ScopeType != TenantScopeCatalog.ScopeAdmin);
    }

    private ActionResult? EnsureCanManageUserAssignment(
        string tenantCode,
        string officeCode,
        string scopeType)
    {
        if (_officeScopeService.HasSystemConfigurationScope)
            return null;

        if (!string.Equals(tenantCode, _officeScopeService.CurrentTenantCode, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        if (string.Equals(scopeType, TenantScopeCatalog.ScopeAdmin, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        if (string.Equals(scopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_officeScopeService.CurrentScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var actorManageableOffices = ResolveIntrinsicManageableOfficeCodes();
        var requestedManageableOffices = TenantScopeCatalog.ResolveScopedOfficeCodes(
            officeCode,
            tenantCode,
            scopeType);
        if (!requestedManageableOffices.All(requestedOffice =>
                actorManageableOffices.Contains(requestedOffice, StringComparer.OrdinalIgnoreCase)))
            return Forbid();

        return null;
    }

    private IReadOnlyCollection<string> ResolveIntrinsicManageableOfficeCodes()
        => TenantScopeCatalog.ResolveScopedOfficeCodes(
            _officeScopeService.CurrentOfficeCode,
            _officeScopeService.CurrentTenantCode,
            _officeScopeService.CurrentScopeType);

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
