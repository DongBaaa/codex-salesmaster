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
[Route("customers")]
public sealed class CustomersController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly OfficeScopeService _officeScopeService;

    public CustomersController(AppDbContext dbContext, OfficeScopeService officeScopeService)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
    }

    [HttpGet]
    public async Task<ActionResult<List<CustomerDto>>> GetAll([FromQuery] string? q, CancellationToken cancellationToken)
    {
        var query = _officeScopeService.ApplyCustomerScope(_dbContext.Customers.AsNoTracking());
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(x => x.NameOriginal.Contains(q) || x.Phone.Contains(q) || x.BusinessNumber.Contains(q));
        }

        return Ok(await query.OrderBy(x => x.NameOriginal).Take(200).Select(x => x.ToDto()).ToListAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CustomerDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _officeScopeService.ApplyCustomerScope(_dbContext.Customers.AsNoTracking())
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity.ToDto());
    }

    [HttpGet("{id:guid}/detail")]
    public async Task<ActionResult<CustomerDetailDto>> GetDetail(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _officeScopeService.ApplyCustomerScope(_dbContext.Customers.AsNoTracking())
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();

        var recentInvoices = await _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .ThenInclude(payment => payment.Attachments)
            .Where(invoice => invoice.CustomerId == id))
            .OrderByDescending(invoice => invoice.InvoiceDate)
            .ThenByDescending(invoice => invoice.UpdatedAtUtc)
            .Take(20)
            .Select(invoice => invoice.ToDto())
            .ToListAsync(cancellationToken);

        return Ok(new CustomerDetailDto
        {
            Customer = entity.ToDto(),
            RecentInvoices = recentInvoices
        });
    }

    [HttpGet("{id:guid}/contracts")]
    public async Task<ActionResult<List<CustomerContractDto>>> GetContracts(Guid id, CancellationToken cancellationToken)
    {
        var customerExists = await _officeScopeService.ApplyCustomerScope(_dbContext.Customers.AsNoTracking())
            .AnyAsync(x => x.Id == id, cancellationToken);
        if (!customerExists)
            return NotFound();

        var contracts = await _officeScopeService.ApplyCustomerContractScope(_dbContext.CustomerContracts
            .AsNoTracking()
            .Where(contract => contract.CustomerId == id))
            .OrderByDescending(contract => contract.IsPrimary)
            .ThenByDescending(contract => contract.SignedDate)
            .ThenByDescending(contract => contract.UploadedAtUtc)
            .Select(contract => contract.ToDto(false))
            .ToListAsync(cancellationToken);

        return Ok(contracts);
    }

    [HttpGet("contracts/{contractId:guid}/content")]
    public async Task<IActionResult> DownloadContractContent(Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await _officeScopeService.ApplyCustomerContractScope(_dbContext.CustomerContracts
            .AsNoTracking()
            .Include(x => x.Customer))
            .FirstOrDefaultAsync(x => x.Id == contractId, cancellationToken);
        if (contract is null || contract.FileContent is not { Length: > 0 })
            return NotFound();

        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(contract.FileName)
            ? $"{contract.Id:N}.pdf"
            : contract.FileName);
        var contentType = string.Equals(contract.MimeType?.Trim(), "application/pdf", StringComparison.OrdinalIgnoreCase)
            ? "application/pdf"
            : "application/octet-stream";

        return File(contract.FileContent, contentType, fileName);
    }

    [HttpPost]
    public async Task<ActionResult<CustomerDto>> Create([FromBody] CustomerDto dto, CancellationToken cancellationToken)
    {
        var entity = new Customer { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode);
        dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode);
        entity.Apply(dto);
        _dbContext.Customers.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CustomerDto>> Update(Guid id, [FromBody] CustomerDto dto, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Customers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!_officeScopeService.CanWriteOfficeForCustomers(entity.OfficeCode, entity.TenantCode))
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
        var entity = await _dbContext.Customers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!_officeScopeService.CanWriteOfficeForCustomers(entity.OfficeCode, entity.TenantCode))
            return Forbid();

        entity.IsDeleted = true;

        var contracts = await _officeScopeService.ApplyCustomerContractScope(_dbContext.CustomerContracts
            .IgnoreQueryFilters()
            .Include(contract => contract.Customer)
            .Where(contract => contract.CustomerId == id && !contract.IsDeleted))
            .ToListAsync(cancellationToken);
        foreach (var contract in contracts)
        {
            contract.IsDeleted = true;
            contract.IsPrimary = false;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
