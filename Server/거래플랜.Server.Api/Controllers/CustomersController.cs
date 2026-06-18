using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;


namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("customers")]
public sealed class CustomersController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly OfficeScopeService _officeScopeService;
    private readonly ICentralFileStorage _fileStorage;

    public CustomersController(AppDbContext dbContext, OfficeScopeService officeScopeService, ICentralFileStorage fileStorage)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
        _fileStorage = fileStorage;
    }

    [HttpGet]
    public async Task<ActionResult<List<CustomerDto>>> GetAll(
        [FromQuery] string? q,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        const int maxTake = 5000;
        var query = _officeScopeService.ApplyCustomerScope(_dbContext.Customers.AsNoTracking());
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(x => x.NameOriginal.Contains(q) || x.Phone.Contains(q) || x.BusinessNumber.Contains(q));
        }

        query = query.OrderBy(x => x.NameOriginal);

        var normalizedSkip = Math.Max(skip.GetValueOrDefault(), 0);
        if (normalizedSkip > 0)
            query = query.Skip(normalizedSkip);

        if (take is > 0)
            query = query.Take(Math.Min(take.Value, maxTake));

        return Ok(await query.Select(x => x.ToDto()).ToListAsync(cancellationToken));
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
        if (contract is null || (string.IsNullOrWhiteSpace(contract.StoragePath) && contract.FileContent is not { Length: > 0 }))
            return NotFound();

        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(contract.FileName)
            ? $"{contract.Id:N}.pdf"
            : contract.FileName);
        var contentType = string.Equals(contract.MimeType?.Trim(), "application/pdf", StringComparison.OrdinalIgnoreCase)
            ? "application/pdf"
            : "application/octet-stream";
        var bytes = _fileStorage.ReadBytes(contract.StoragePath, contract.FileContent);
        if (contract.FileSize > 0 && bytes.LongLength != contract.FileSize)
        {
            return NotFound(new
            {
                error = "contract_content_unavailable",
                message = "계약서 파일 내용을 찾을 수 없습니다."
            });
        }

        return File(bytes, contentType, fileName);
    }

    [HttpPost]
    [Authorize(Policy = PermissionNames.CustomerEdit)]
    public async Task<ActionResult<CustomerDto>> Create([FromBody] CustomerDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditCustomers())
            return Forbid();

        NormalizeCustomerClassification(dto);
        var entity = new Customer { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        dto.ResponsibleOfficeCode = _officeScopeService.ResolveCustomerResponsibleScopeForCreate(
            dto.ResponsibleOfficeCode,
            dto.OfficeCode);
        dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            dto.OfficeCode,
            dto.ResponsibleOfficeCode,
            dto.OfficeCode);
        dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode);
        entity.Apply(dto);
        _dbContext.Customers.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionNames.CustomerEdit)]
    public async Task<ActionResult<CustomerDto>> Update(Guid id, [FromBody] CustomerDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditCustomers())
            return Forbid();

        var entity = await _dbContext.Customers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!_officeScopeService.CanWriteOfficeForCustomers(entity.ResponsibleOfficeCode, entity.TenantCode))
            return Forbid();
        if (OptimisticConcurrencyGuard.Check(this, entity, dto, nameof(Customer)) is { } conflict)
            return conflict;

        PreserveCustomerTextWhenIncomingLooksLossy(dto, entity);
        NormalizeCustomerClassification(dto);
        dto.ResponsibleOfficeCode = _officeScopeService.ResolveCustomerResponsibleScopeForCreate(
            dto.ResponsibleOfficeCode,
            entity.ResponsibleOfficeCode);
        dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            dto.OfficeCode,
            dto.ResponsibleOfficeCode,
            entity.OfficeCode);
        dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
            dto.TenantCode,
            dto.OfficeCode,
            entity.TenantCode,
            entity.OfficeCode);
        entity.Apply(dto);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionNames.CustomerEdit)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] long? expectedRevision, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditCustomers())
            return Forbid();

        var entity = await _dbContext.Customers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!_officeScopeService.CanWriteOfficeForCustomers(entity.ResponsibleOfficeCode, entity.TenantCode))
            return Forbid();
        if (OptimisticConcurrencyGuard.Check(this, entity, expectedRevision, nameof(Customer)) is { } conflict)
            return conflict;

        var referenceBlockMessage = await CustomerDeletionReferenceGuard.BuildActiveReferenceBlockMessageAsync(
            _dbContext,
            id,
            cancellationToken);
        if (referenceBlockMessage is not null)
        {
            return Conflict(new
            {
                error = CustomerDeletionReferenceGuard.ConflictCode,
                message = referenceBlockMessage
            });
        }

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

    private static void PreserveCustomerTextWhenIncomingLooksLossy(CustomerDto dto, Customer entity)
    {
        var preservedName = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.NameOriginal, dto.NameOriginal);
        if (!string.Equals(preservedName, dto.NameOriginal, StringComparison.Ordinal))
        {
            dto.NameOriginal = preservedName;
            dto.NameMatchKey = MatchKeyNormalizer.Normalize(preservedName);
        }

        dto.TradeType = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.TradeType, dto.TradeType);
        dto.Department = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.Department, dto.Department);
        dto.ContactPerson = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.ContactPerson, dto.ContactPerson);
        dto.Representative = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.Representative, dto.Representative);
        dto.BusinessNumber = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.BusinessNumber, dto.BusinessNumber);
        dto.BusinessType = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.BusinessType, dto.BusinessType);
        dto.BusinessItem = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.BusinessItem, dto.BusinessItem);
        dto.Address = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.Address, dto.Address);
        dto.DetailAddress = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.DetailAddress, dto.DetailAddress);
        dto.Notes = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.Notes, dto.Notes);
        dto.Phone = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.Phone, dto.Phone);
        dto.MobilePhone = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.MobilePhone, dto.MobilePhone);
        dto.FaxNumber = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.FaxNumber, dto.FaxNumber);
        dto.Email = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.Email, dto.Email);
        dto.HomePage = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.HomePage, dto.HomePage);
        dto.Recipient = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.Recipient, dto.Recipient);
        dto.PriceGrade = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(entity.PriceGrade, dto.PriceGrade);
    }

    private static void NormalizeCustomerClassification(CustomerDto dto)
    {
        var rawTradeType = (dto.TradeType ?? string.Empty).Trim();

        if (CustomerClassificationNormalizer.TryExtractCompositeCategoryAndTradeType(rawTradeType, out var category, out var normalizedCompositeTradeType))
        {
            if (!dto.CategoryId.HasValue || dto.CategoryId == Guid.Empty)
                dto.CategoryId = category.Id;

            dto.TradeType = normalizedCompositeTradeType;
            return;
        }

        if (CustomerClassificationNormalizer.TryResolveCategory(rawTradeType, out var standaloneCategory))
        {
            if (!dto.CategoryId.HasValue || dto.CategoryId == Guid.Empty)
                dto.CategoryId = standaloneCategory.Id;

            dto.TradeType = CustomerClassificationNormalizer.Sales;
            return;
        }

        dto.TradeType = CustomerClassificationNormalizer.NormalizeTradeTypeOrDefault(rawTradeType);
    }
}


