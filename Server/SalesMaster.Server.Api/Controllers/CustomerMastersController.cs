using SalesMaster.Server.Api.Data;
using SalesMaster.Server.Api.Domain;
using SalesMaster.Server.Api.Mappings;
using SalesMaster.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SalesMaster.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("customer-masters")]
public sealed class CustomerMastersController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    public CustomerMastersController(AppDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    public async Task<ActionResult<List<CustomerMasterDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _dbContext.CustomerMasters.AsNoTracking().Select(x => x.ToDto()).ToListAsync(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<CustomerMasterDto>> Create([FromBody] CustomerMasterDto dto, CancellationToken cancellationToken)
    {
        var entity = new CustomerMaster { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        entity.Apply(dto);
        _dbContext.CustomerMasters.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }
}
