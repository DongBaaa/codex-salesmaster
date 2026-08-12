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
    [Authorize(Policy = PermissionNames.CustomerEdit)]
    public async Task<ActionResult<CustomerMasterDto>> Create([FromBody] CustomerMasterDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditCustomers())
            return Forbid();
        if (dto.IsDeleted)
            return SoftDeleteMutationGuard.RejectCreate("거래처 원장");

        await using var transaction = await InventoryMutationTransactionScope.BeginAsync(
            _dbContext,
            serializeInventoryMutations: true,
            cancellationToken);
        var entity = new CustomerMaster { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode);
        dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode);

        var mutationCheck = await ProcessedSyncMutationRecorder.CheckAsync(
            _dbContext,
            dto,
            nameof(CustomerMaster),
            cancellationToken);
        if (mutationCheck.Status == DirectMutationStatus.Conflict)
            return Conflict(ProcessedSyncMutationRecorder.BuildConflictResponse(mutationCheck));
        if (mutationCheck.Status == DirectMutationStatus.Duplicate)
            return await ResolveDuplicateCustomerMasterAsync(mutationCheck, cancellationToken);

        entity.Apply(dto);
        _dbContext.CustomerMasters.Add(entity);
        ProcessedSyncMutationRecorder.Record(_dbContext, mutationCheck, entity.Id);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    private async Task<ActionResult<CustomerMasterDto>> ResolveDuplicateCustomerMasterAsync(
        DirectMutationCheck mutationCheck,
        CancellationToken cancellationToken)
    {
        if (mutationCheck.ExistingReceipt is not null &&
            Guid.TryParse(mutationCheck.ExistingReceipt.EntityId, out var entityId))
        {
            var entity = await _officeScopeService.ApplyCustomerMasterScope(
                    _dbContext.CustomerMasters.AsNoTracking())
                .FirstOrDefaultAsync(
                    current => current.Id == entityId,
                    cancellationToken);
            if (entity is not null)
                return Ok(entity.ToDto());
        }

        return Conflict(new DirectMutationConflictResponse
        {
            MutationId = mutationCheck.MutationId,
            EntityName = nameof(CustomerMaster),
            EntityId = mutationCheck.RequestedEntityId,
            Reason = "The processed mutation is unavailable in the current scope."
        });
    }
}
