using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("company-profile")]
public sealed class CompanyProfileController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public CompanyProfileController(AppDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    public async Task<ActionResult<CompanyProfileDto>> Get([FromQuery] string? officeCode, CancellationToken cancellationToken)
    {
        var normalizedOfficeCode = ResolveRequestedOfficeCode(officeCode, null);
        var profile = await _dbContext.CompanyProfiles.AsNoTracking()
            .Where(x => x.OfficeCode == normalizedOfficeCode && x.IsActive)
            .OrderByDescending(x => x.IsDefaultForOffice)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        profile ??= await _dbContext.CompanyProfiles.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return profile is null ? NotFound() : Ok(profile.ToDto());
    }

    [HttpPut]
    [Authorize(Policy = PermissionNames.CompanyProfileEdit)]
    public async Task<ActionResult<CompanyProfileDto>> Upsert([FromBody] CompanyProfileDto dto, CancellationToken cancellationToken)
    {
        var normalizedOfficeCode = ResolveRequestedOfficeCode(dto.OfficeCode, null);
        dto.OfficeCode = normalizedOfficeCode;

        var profile = dto.Id == Guid.Empty
            ? null
            : await _dbContext.CompanyProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == dto.Id, cancellationToken);

        if (profile is null && dto.Id == Guid.Empty)
        {
            profile = await _dbContext.CompanyProfiles
                .FirstOrDefaultAsync(current =>
                    current.OfficeCode == normalizedOfficeCode &&
                    current.IsActive &&
                    current.IsDefaultForOffice,
                    cancellationToken);
        }

        if (profile is null)
        {
            profile = new Domain.CompanyProfile { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
            profile.Apply(dto);
            _dbContext.CompanyProfiles.Add(profile);
        }
        else
        {
            if (OptimisticConcurrencyGuard.Check(this, profile, dto, nameof(Domain.CompanyProfile)) is { } conflict)
                return conflict;
            profile.Apply(dto);
        }

        if (profile.IsDefaultForOffice)
        {
            var officeCode = profile.OfficeCode;
            foreach (var other in await _dbContext.CompanyProfiles
                         .Where(current =>
                             current.Id != profile.Id &&
                             current.OfficeCode == officeCode &&
                             current.IsActive &&
                             current.IsDefaultForOffice)
                         .ToListAsync(cancellationToken))
            {
                other.IsDefaultForOffice = false;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(profile.ToDto());
    }

    private string ResolveRequestedOfficeCode(string? requestedOfficeCode, string? fallbackOfficeCode)
    {
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(requestedOfficeCode, out var normalizedRequested))
            return normalizedRequested;

        var claimOfficeCode = User.FindFirstValue("office");
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(claimOfficeCode, out var normalizedClaim))
            return normalizedClaim;

        return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(fallbackOfficeCode, OfficeCodeCatalog.Usenet);
    }
}
