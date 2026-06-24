using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("company-profile")]
public sealed class CompanyProfileController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly OfficeScopeService _officeScopeService;

    public CompanyProfileController(AppDbContext dbContext, OfficeScopeService officeScopeService)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
    }

    [HttpGet]
    public async Task<ActionResult<CompanyProfileDto>> Get([FromQuery] string? officeCode, CancellationToken cancellationToken)
    {
        var normalizedOfficeCode = ResolveRequestedOfficeCode(officeCode);
        if (!_officeScopeService.CanReadOfficeForCompanyProfiles(normalizedOfficeCode))
            return Forbid();

        var scopedProfiles = _officeScopeService.ApplyCompanyProfileScope(_dbContext.CompanyProfiles.AsNoTracking());
        var profile = await scopedProfiles
            .Where(x => x.OfficeCode == normalizedOfficeCode && x.IsActive)
            .OrderByDescending(x => x.IsDefaultForOffice)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return profile is null ? NotFound() : Ok(profile.ToDto());
    }

    [HttpPut]
    [Authorize(Policy = PermissionNames.CompanyProfileEdit)]
    public async Task<ActionResult<CompanyProfileDto>> Upsert([FromBody] CompanyProfileDto dto, CancellationToken cancellationToken)
    {
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(dto.OfficeCode, out var requestedOfficeCode) &&
            !_officeScopeService.CanWriteOfficeForCompanyProfiles(requestedOfficeCode))
        {
            return Forbid();
        }

        var normalizedOfficeCode = ResolveWritableOfficeCode(dto.OfficeCode, null);
        if (!_officeScopeService.CanWriteOfficeForCompanyProfiles(normalizedOfficeCode))
            return Forbid();

        if (dto.IsDeleted)
            return SoftDeleteMutationGuard.RejectUpdate("회사 프로필");

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
            if (!_officeScopeService.CanWriteOfficeForCompanyProfiles(profile.OfficeCode))
                return Forbid();

            if (OptimisticConcurrencyGuard.Check(this, profile, dto, nameof(Domain.CompanyProfile)) is { } conflict)
                return conflict;

            dto.OfficeCode = ResolveWritableOfficeCode(dto.OfficeCode, profile.OfficeCode);
            if (!_officeScopeService.CanWriteOfficeForCompanyProfiles(dto.OfficeCode))
                return Forbid();

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

    private string ResolveRequestedOfficeCode(string? requestedOfficeCode)
    {
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(requestedOfficeCode, out var normalizedRequested))
            return normalizedRequested;

        return _officeScopeService.CurrentOfficeCode;
    }

    private string ResolveWritableOfficeCode(string? requestedOfficeCode, string? fallbackOfficeCode)
    {
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(requestedOfficeCode, out var normalizedRequested) &&
            _officeScopeService.CanWriteOfficeForCompanyProfiles(normalizedRequested))
        {
            return normalizedRequested;
        }

        if (OfficeCodeCatalog.TryNormalizeOfficeCode(fallbackOfficeCode, out var normalizedFallback) &&
            _officeScopeService.CanWriteOfficeForCompanyProfiles(normalizedFallback))
        {
            return normalizedFallback;
        }

        return _officeScopeService.CurrentOfficeCode;
    }
}
