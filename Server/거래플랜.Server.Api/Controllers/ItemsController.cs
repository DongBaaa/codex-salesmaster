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
[Route("items")]
public sealed class ItemsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly OfficeScopeService _officeScopeService;

    public ItemsController(AppDbContext dbContext, OfficeScopeService officeScopeService)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
    }

    [HttpGet]
    public async Task<ActionResult<List<ItemDto>>> GetAll(
        [FromQuery] string? q,
        [FromQuery] string? category,
        CancellationToken cancellationToken)
    {
        var query = _officeScopeService.ApplyItemScope(_dbContext.Items.AsNoTracking());
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(x =>
                x.NameOriginal.Contains(q) ||
                x.SpecificationOriginal.Contains(q) ||
                x.MaterialNumber.Contains(q) ||
                x.CategoryName.Contains(q));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            if (string.Equals(category.Trim(), "미분류", StringComparison.OrdinalIgnoreCase))
                query = query.Where(x => string.IsNullOrWhiteSpace(x.CategoryName));
            else
                query = query.Where(x => x.CategoryName == category);
        }

        return Ok(await query.OrderBy(x => x.NameOriginal).Take(200).Select(x => x.ToDto()).ToListAsync(cancellationToken));
    }

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories(CancellationToken cancellationToken)
    {
        var scopedItems = _officeScopeService.ApplyItemScope(_dbContext.Items.AsNoTracking());

        var result = await scopedItems
            .Where(item => !string.IsNullOrWhiteSpace(item.CategoryName))
            .GroupBy(item => item.CategoryName)
            .OrderBy(group => group.Key)
            .Select(group => new ItemCategorySummaryDto
            {
                Name = group.Key ?? string.Empty,
                ItemCount = group.Count()
            })
            .ToListAsync(cancellationToken);

        var uncategorizedCount = await scopedItems.CountAsync(item => string.IsNullOrWhiteSpace(item.CategoryName), cancellationToken);
        if (uncategorizedCount > 0)
        {
            result.Add(new ItemCategorySummaryDto
            {
                Name = "미분류",
                ItemCount = uncategorizedCount
            });
        }

        return Ok(result.OrderBy(item => item.Name == "미분류" ? "zzz" : item.Name).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ItemDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _officeScopeService.ApplyItemScope(_dbContext.Items.AsNoTracking())
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity.ToDto());
    }

    [HttpGet("{id:guid}/detail")]
    public async Task<ActionResult<ItemDetailDto>> GetDetail(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _officeScopeService.ApplyItemScope(_dbContext.Items.AsNoTracking())
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();

        var stocks = await _officeScopeService.ApplyWarehouseScope(_dbContext.ItemWarehouseStocks.AsNoTracking())
            .Where(stock => stock.ItemId == id)
            .OrderBy(stock => stock.WarehouseCode)
            .Select(stock => stock.ToDto())
            .ToListAsync(cancellationToken);

        if (stocks.Count == 0)
        {
            stocks.Add(new ItemWarehouseStockDto
            {
                ItemId = entity.Id,
                WarehouseCode = "전체",
                Quantity = entity.CurrentStock,
                UpdatedAtUtc = entity.UpdatedAtUtc
            });
        }

        return Ok(new ItemDetailDto
        {
            Item = entity.ToDto(),
            BranchStocks = stocks
        });
    }

    [HttpPost]
    public async Task<ActionResult<ItemDto>> Create([FromBody] ItemDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.HasAdministrativeWriteAccess)
            return Forbid();

        var entity = new Item { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode);
        dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode);
        entity.Apply(dto);
        _dbContext.Items.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ItemDto>> Update(Guid id, [FromBody] ItemDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.HasAdministrativeWriteAccess)
            return Forbid();

        var entity = await _dbContext.Items.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!_officeScopeService.CanWriteOfficeForItems(entity.OfficeCode, entity.TenantCode))
            return Forbid();

        dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode, entity.TenantCode, entity.OfficeCode);
        dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode, entity.OfficeCode);
        entity.Apply(dto);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.HasAdministrativeWriteAccess)
            return Forbid();

        var entity = await _dbContext.Items.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!_officeScopeService.CanWriteOfficeForItems(entity.OfficeCode, entity.TenantCode))
            return Forbid();

        entity.IsDeleted = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
