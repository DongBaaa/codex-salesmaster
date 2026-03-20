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
[Authorize]
[Route("customer-masters")]
public sealed class CustomerMastersController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly OfficeScopeService _officeScopeService;

    public CustomerMastersController(AppDbContext dbContext, OfficeScopeService officeScopeService)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
    }

    [HttpGet]
    public async Task<ActionResult<List<CustomerMasterDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _officeScopeService.ApplyCustomerMasterScope(_dbContext.CustomerMasters.AsNoTracking())
            .Select(x => x.ToDto()).ToListAsync(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<CustomerMasterDto>> Create([FromBody] CustomerMasterDto dto, CancellationToken cancellationToken)
    {
        var entity = new CustomerMaster { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode);
        entity.Apply(dto);
        _dbContext.CustomerMasters.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }
}
