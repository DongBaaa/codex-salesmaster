using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
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

    public CompanyProfileController(AppDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    public async Task<ActionResult<CompanyProfileDto>> Get(CancellationToken cancellationToken)
    {
        var profile = await _dbContext.CompanyProfiles.AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        return profile is null ? NotFound() : Ok(profile.ToDto());
    }

    [HttpPut]
    [Authorize(Policy = PermissionNames.CompanyProfileEdit)]
    public async Task<ActionResult<CompanyProfileDto>> Upsert([FromBody] CompanyProfileDto dto, CancellationToken cancellationToken)
    {
        var profile = await _dbContext.CompanyProfiles.FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            profile = new Domain.CompanyProfile { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
            profile.Apply(dto);
            _dbContext.CompanyProfiles.Add(profile);
        }
        else
        {
            profile.Apply(dto);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(profile.ToDto());
    }
}
