using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("runtime/edit-sessions")]
public sealed class RuntimeEditSessionsController : ControllerBase
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(2);

    private readonly AppDbContext _dbContext;
    private readonly ICurrentUserContext _currentUserContext;

    public RuntimeEditSessionsController(
        AppDbContext dbContext,
        ICurrentUserContext currentUserContext)
    {
        _dbContext = dbContext;
        _currentUserContext = currentUserContext;
    }

    [HttpGet("active")]
    [ProducesResponseType(typeof(EditSessionLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EditSessionLookupResponse>> GetActiveAsync(
        [FromQuery] string entityType,
        [FromQuery] string entityId,
        [FromQuery] Guid? excludeAppSessionId,
        CancellationToken cancellationToken)
    {
        var normalizedEntityType = NormalizeRequiredText(entityType, 80);
        var normalizedEntityId = NormalizeRequiredText(entityId, 128);
        if (string.IsNullOrWhiteSpace(normalizedEntityType) || string.IsNullOrWhiteSpace(normalizedEntityId))
            return ValidationProblem("편집 세션 조회 대상 정보가 비어 있습니다.");

        var nowUtc = DateTime.UtcNow;
        var cleaned = await CleanupExpiredSessionsAsync(nowUtc, cancellationToken);
        if (cleaned)
            await _dbContext.SaveChangesAsync(cancellationToken);

        var currentTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            _currentUserContext.TenantCode,
            _currentUserContext.OfficeCode);

        var sessions = await _dbContext.ActiveEditSessions
            .AsNoTracking()
            .Where(entity =>
                entity.EntityType == normalizedEntityType &&
                entity.EntityId == normalizedEntityId &&
                entity.TenantCode == currentTenantCode &&
                entity.ExpiresAtUtc > nowUtc &&
                (!excludeAppSessionId.HasValue || entity.AppSessionId != excludeAppSessionId.Value))
            .OrderBy(entity => entity.Username)
            .ThenBy(entity => entity.MachineName)
            .ToListAsync(cancellationToken);

        return Ok(new EditSessionLookupResponse
        {
            ServerUtc = nowUtc,
            ActiveEditors = sessions.Select(entity => new EditSessionParticipantDto
            {
                EditSessionId = entity.Id,
                AppSessionId = entity.AppSessionId,
                Username = entity.Username,
                OfficeCode = entity.OfficeCode,
                TenantCode = entity.TenantCode,
                ScreenName = entity.ScreenName,
                EntityType = entity.EntityType,
                EntityId = entity.EntityId,
                EntityDisplayName = entity.EntityDisplayName,
                MachineName = entity.MachineName,
                OpenedAtUtc = entity.OpenedAtUtc,
                LastHeartbeatUtc = entity.LastHeartbeatUtc
            }).ToList()
        });
    }

    [HttpPost("heartbeat")]
    [ProducesResponseType(typeof(EditSessionHeartbeatResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EditSessionHeartbeatResponse>> HeartbeatAsync(
        [FromBody] EditSessionHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EditSessionId == Guid.Empty)
            return ValidationProblem("편집 세션 ID가 비어 있습니다.");

        var normalizedEntityType = NormalizeRequiredText(request.EntityType, 80);
        var normalizedEntityId = NormalizeRequiredText(request.EntityId, 128);
        if (string.IsNullOrWhiteSpace(normalizedEntityType) || string.IsNullOrWhiteSpace(normalizedEntityId))
            return ValidationProblem("편집 대상 정보가 비어 있습니다.");

        var nowUtc = DateTime.UtcNow;
        await CleanupExpiredSessionsAsync(nowUtc, cancellationToken);

        var session = await _dbContext.ActiveEditSessions
            .FirstOrDefaultAsync(entity => entity.Id == request.EditSessionId, cancellationToken);

        if (session is null)
        {
            session = new ActiveEditSession
            {
                Id = request.EditSessionId,
                OpenedAtUtc = nowUtc
            };
            _dbContext.ActiveEditSessions.Add(session);
        }

        session.AppSessionId = request.AppSessionId == Guid.Empty ? request.EditSessionId : request.AppSessionId;
        session.Username = NormalizeOptionalText(_currentUserContext.Username, 120);
        session.OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_currentUserContext.OfficeCode);
        session.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(_currentUserContext.TenantCode, session.OfficeCode);
        session.ScreenName = NormalizeOptionalText(request.ScreenName, 120);
        session.EntityType = normalizedEntityType;
        session.EntityId = normalizedEntityId;
        session.EntityDisplayName = NormalizeOptionalText(request.EntityDisplayName, 200);
        session.MachineName = NormalizeOptionalText(request.MachineName, 120);
        session.LastHeartbeatUtc = nowUtc;
        session.ExpiresAtUtc = nowUtc.Add(SessionTtl);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var others = await _dbContext.ActiveEditSessions
            .AsNoTracking()
            .Where(entity =>
                entity.EntityType == normalizedEntityType &&
                entity.EntityId == normalizedEntityId &&
                entity.ExpiresAtUtc > nowUtc &&
                entity.Id != request.EditSessionId)
            .OrderBy(entity => entity.Username)
            .ThenBy(entity => entity.MachineName)
            .ToListAsync(cancellationToken);

        return Ok(new EditSessionHeartbeatResponse
        {
            ServerUtc = nowUtc,
            OtherEditors = others.Select(entity => new EditSessionParticipantDto
            {
                EditSessionId = entity.Id,
                AppSessionId = entity.AppSessionId,
                Username = entity.Username,
                OfficeCode = entity.OfficeCode,
                TenantCode = entity.TenantCode,
                ScreenName = entity.ScreenName,
                EntityType = entity.EntityType,
                EntityId = entity.EntityId,
                EntityDisplayName = entity.EntityDisplayName,
                MachineName = entity.MachineName,
                OpenedAtUtc = entity.OpenedAtUtc,
                LastHeartbeatUtc = entity.LastHeartbeatUtc
            }).ToList()
        });
    }

    [HttpPost("release")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReleaseAsync(
        [FromBody] EditSessionReleaseRequest request,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var hasChanges = await CleanupExpiredSessionsAsync(nowUtc, cancellationToken);

        if (request.EditSessionId != Guid.Empty)
        {
            var session = await _dbContext.ActiveEditSessions
                .FirstOrDefaultAsync(entity => entity.Id == request.EditSessionId, cancellationToken);
            if (session is not null)
            {
                _dbContext.ActiveEditSessions.Remove(session);
                hasChanges = true;
            }
        }

        if (hasChanges)
            await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok();
    }

    private async Task<bool> CleanupExpiredSessionsAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var expiredSessions = await _dbContext.ActiveEditSessions
            .Where(entity => entity.ExpiresAtUtc <= nowUtc)
            .ToListAsync(cancellationToken);

        if (expiredSessions.Count == 0)
            return false;

        _dbContext.ActiveEditSessions.RemoveRange(expiredSessions);
        return true;
    }

    private static string NormalizeRequiredText(string? value, int maxLength)
        => NormalizeOptionalText(value, maxLength);

    private static string NormalizeOptionalText(string? value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length <= maxLength)
            return trimmed;

        return trimmed[..maxLength];
    }
}
