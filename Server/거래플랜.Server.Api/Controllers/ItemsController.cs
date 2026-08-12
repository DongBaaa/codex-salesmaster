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
[Route("items")]
public sealed class ItemsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly OfficeScopeService _officeScopeService;
    private readonly ItemDuplicateMergeService? _duplicateMergeService;

    public ItemsController(AppDbContext dbContext, OfficeScopeService officeScopeService)
        : this(dbContext, officeScopeService, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public ItemsController(
        AppDbContext dbContext,
        OfficeScopeService officeScopeService,
        ItemDuplicateMergeService? duplicateMergeService)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
        _duplicateMergeService = duplicateMergeService;
    }

    [HttpPost("duplicate-merge/preview")]
    [Authorize(Policy = PermissionNames.ItemEdit)]
    public async Task<ActionResult<ItemDuplicateMergePreviewDto>> PreviewDuplicateMerge(
        [FromBody] ItemDuplicateMergePreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditItems())
            return Forbid();
        if (_duplicateMergeService is null)
            return Problem("Item duplicate merge service is unavailable.");

        var outcome = await _duplicateMergeService.PreviewOutcomeAsync(request, cancellationToken);
        return outcome.Status switch
        {
            ItemDuplicateMergeStatus.Success => Ok(outcome.Preview),
            ItemDuplicateMergeStatus.Forbidden => Forbid(),
            _ => BadRequest(new { error = outcome.Error, message = outcome.Message })
        };
    }

    [HttpPost("duplicate-merge")]
    [Authorize(Policy = PermissionNames.ItemEdit)]
    public async Task<ActionResult<ItemDuplicateMergeResultDto>> MergeDuplicates(
        [FromBody] ItemDuplicateMergeRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditItems())
            return Forbid();
        if (_duplicateMergeService is null)
            return Problem("Item duplicate merge service is unavailable.");

        var outcome = await _duplicateMergeService.MergeAsync(request, cancellationToken);
        return outcome.Status switch
        {
            ItemDuplicateMergeStatus.Success => Ok(outcome.Result),
            ItemDuplicateMergeStatus.Forbidden => Forbid(),
            ItemDuplicateMergeStatus.Invalid => BadRequest(new { error = outcome.Error, message = outcome.Message }),
            _ => Conflict(new
            {
                error = outcome.Error,
                message = outcome.Message,
                preview = outcome.Preview
            })
        };
    }

    [HttpGet]
    public async Task<ActionResult<List<ItemDto>>> GetAll(
        [FromQuery] string? q,
        [FromQuery] string? category,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        const int maxTake = 5000;
        var activeCategoryNames = (await _dbContext.ItemCategoryOptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(option => option.IsActive && !option.IsDeleted)
            .Select(option => option.Name)
            .ToListAsync(cancellationToken))
            .Where(name => !IsInvalidCategoryName(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
            {
                query = query.Where(x =>
                    string.IsNullOrWhiteSpace(x.CategoryName) ||
                    !activeCategoryNames.Contains(x.CategoryName.Trim()));
            }
            else
                query = query.Where(x => x.CategoryName == category);
        }

        query = query.OrderBy(x => x.NameOriginal);

        var normalizedSkip = Math.Max(skip.GetValueOrDefault(), 0);
        if (normalizedSkip > 0)
            query = query.Skip(normalizedSkip);

        if (take is > 0)
            query = query.Take(Math.Min(take.Value, maxTake));

        return Ok(await query.Select(x => x.ToDto()).ToListAsync(cancellationToken));
    }

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories(CancellationToken cancellationToken)
    {
        var scopedItems = _officeScopeService.ApplyItemScope(_dbContext.Items.AsNoTracking());
        var masterCategories = (await _dbContext.ItemCategoryOptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(option => option.IsActive && !option.IsDeleted)
            .OrderBy(option => option.SortOrder)
            .ThenBy(option => option.Name)
            .Select(option => new { option.Name, option.SortOrder })
            .ToListAsync(cancellationToken))
            .Where(option => !IsInvalidCategoryName(option.Name))
            .Select(option => new { Name = option.Name!.Trim(), option.SortOrder })
            .ToList();
        var activeCategoryNames = masterCategories
            .Select(option => option.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rawCounts = await scopedItems
            .GroupBy(item => item.CategoryName)
            .Select(group => new
            {
                Name = group.Key,
                ItemCount = group.Count()
            })
            .ToListAsync(cancellationToken);

        var result = masterCategories
            .Select(option => new ItemCategorySummaryDto
            {
                Name = option.Name,
                ItemCount = rawCounts
                    .Where(count => string.Equals(count.Name, option.Name, StringComparison.OrdinalIgnoreCase))
                    .Sum(count => count.ItemCount)
            })
            .ToList();

        var uncategorizedCount = rawCounts
            .Where(count => string.IsNullOrWhiteSpace(count.Name) || !activeCategoryNames.Contains(count.Name!.Trim()))
            .Sum(count => count.ItemCount);

        if (uncategorizedCount > 0 || result.Count == 0)
        {
            result.Add(new ItemCategorySummaryDto
            {
                Name = "미분류",
                ItemCount = uncategorizedCount
            });
        }

        return Ok(result);
    }

    private static bool IsInvalidCategoryName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var normalized = value.Trim();
        return normalized.All(ch => ch == '?' || ch == '\uFFFD');
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
                UpdatedAtUtc = entity.UpdatedAtUtc,
                Revision = entity.Revision,
                ExpectedRevision = entity.Revision
            });
        }

        return Ok(new ItemDetailDto
        {
            Item = entity.ToDto(),
            BranchStocks = stocks
        });
    }

    [HttpPost]
    [Authorize(Policy = PermissionNames.ItemEdit)]
    public async Task<ActionResult<ItemDto>> Create([FromBody] ItemDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditItems())
            return Forbid();
        if (dto.IsDeleted)
            return SoftDeleteMutationGuard.RejectCreate("품목");

        await using var transaction = await InventoryMutationTransactionScope.BeginAsync(
            _dbContext,
            serializeInventoryMutations: true,
            cancellationToken);

        var entity = new Item { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode);
        dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode);
        var requestedTrackingType = ItemOperationalPolicy.NormalizeTrackingType(
            dto.TrackingType,
            dto.ItemKind,
            dto.CategoryName,
            dto.IsRental);

        dto.CategoryName = await ItemCategoryOptionGuard.EnsureActiveOptionAsync(
            _dbContext,
            dto.CategoryName,
            cancellationToken);
        var mutationCheck = await ProcessedSyncMutationRecorder.CheckAsync(
            _dbContext,
            dto,
            nameof(Item),
            cancellationToken);
        if (mutationCheck.Status == DirectMutationStatus.Conflict)
            return Conflict(ProcessedSyncMutationRecorder.BuildConflictResponse(mutationCheck));
        if (mutationCheck.Status == DirectMutationStatus.Duplicate)
            return await ResolveDuplicateItemAsync(mutationCheck, cancellationToken);

        if (await ValidateCurrentStockMatchesWarehouseTotalAsync(
                entity.Id,
                requestedTrackingType,
                dto.CurrentStock,
                cancellationToken) is { } stockError)
        {
            return stockError;
        }

        entity.Apply(dto);
        await RemoveWarehouseStocksIfNonInventoryAsync(entity, cancellationToken);
        _dbContext.Items.Add(entity);
        ProcessedSyncMutationRecorder.Record(_dbContext, mutationCheck, entity.Id);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionNames.ItemEdit)]
    public async Task<ActionResult<ItemDto>> Update(Guid id, [FromBody] ItemDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditItems())
            return Forbid();
        if (dto.Id != Guid.Empty && dto.Id != id)
            return BadRequest("Item route id must match the body id.");

        dto.Id = id;
        await using var transaction = await InventoryMutationTransactionScope.BeginAsync(
            _dbContext,
            serializeInventoryMutations: true,
            cancellationToken);

        var entity = await _dbContext.Items.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!_officeScopeService.CanWriteOfficeForItems(entity.OfficeCode, entity.TenantCode))
            return Forbid();
        if (dto.IsDeleted)
            return SoftDeleteMutationGuard.RejectUpdate("품목");
        if (!TryEvaluateRequestedItemScope(dto, entity, out var requestsDifferentScope, out var scopeError))
            return BadRequest(scopeError);
        if (requestsDifferentScope)
            return BadRequest("Item tenant/office scope cannot be changed for an existing item.");

        var previouslySupportedInventory = ItemOperationalPolicy.SupportsInventory(entity.TrackingType);
        dto.TenantCode = entity.TenantCode;
        dto.OfficeCode = entity.OfficeCode;
        var requestedTrackingType = ItemOperationalPolicy.NormalizeTrackingType(
            dto.TrackingType,
            dto.ItemKind,
            dto.CategoryName,
            dto.IsRental);

        dto.CategoryName = await ItemCategoryOptionGuard.EnsureActiveOptionAsync(
            _dbContext,
            dto.CategoryName,
            cancellationToken);
        var mutationCheck = await ProcessedSyncMutationRecorder.CheckAsync(
            _dbContext,
            dto,
            nameof(Item),
            cancellationToken);
        if (mutationCheck.Status == DirectMutationStatus.Conflict)
            return Conflict(ProcessedSyncMutationRecorder.BuildConflictResponse(mutationCheck));
        if (mutationCheck.Status == DirectMutationStatus.Duplicate)
            return await ResolveDuplicateItemAsync(mutationCheck, cancellationToken);
        if (OptimisticConcurrencyGuard.Check(this, entity, dto, nameof(Item)) is { } conflict)
            return conflict;

        if (await ValidateCurrentStockMatchesWarehouseTotalAsync(
                entity.Id,
                requestedTrackingType,
                dto.CurrentStock,
                cancellationToken) is { } stockError)
        {
            return stockError;
        }

        entity.Apply(dto);
        var inventorySupportChanged =
            previouslySupportedInventory != ItemOperationalPolicy.SupportsInventory(entity.TrackingType);
        await RemoveWarehouseStocksIfNonInventoryAsync(entity, cancellationToken);
        await RemoveInventoryLedgerEntriesIfNonInventoryAsync(entity, cancellationToken);
        ProcessedSyncMutationRecorder.Record(_dbContext, mutationCheck, entity.Id);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (inventorySupportChanged)
            await new InventoryLedgerService(_dbContext).RebuildAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    private static bool TryEvaluateRequestedItemScope(
        ItemDto dto,
        Item entity,
        out bool requestsDifferentScope,
        out string scopeError)
    {
        requestsDifferentScope = false;
        scopeError = string.Empty;
        var existingOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(entity.OfficeCode);
        var requestedOfficeCode = existingOfficeCode;
        if (!string.IsNullOrWhiteSpace(dto.OfficeCode) &&
            !OfficeCodeCatalog.TryNormalizeScope(dto.OfficeCode, out requestedOfficeCode))
        {
            scopeError = "Item office scope is invalid.";
            return false;
        }

        var existingTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(
            entity.TenantCode,
            TenantScopeCatalog.GetTenantCodeForOffice(existingOfficeCode));
        var requestedTenantCode = existingTenantCode;
        if (!string.IsNullOrWhiteSpace(dto.TenantCode) &&
            !TenantScopeCatalog.TryNormalizeTenantCode(dto.TenantCode, out requestedTenantCode))
        {
            scopeError = "Item tenant scope is invalid.";
            return false;
        }

        requestsDifferentScope =
            !string.Equals(
                requestedOfficeCode,
                existingOfficeCode,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                requestedTenantCode,
                existingTenantCode,
                StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private async Task<ActionResult?> ValidateCurrentStockMatchesWarehouseTotalAsync(
        Guid itemId,
        string trackingType,
        decimal requestedCurrentStock,
        CancellationToken cancellationToken)
    {
        if (!ItemOperationalPolicy.SupportsInventory(trackingType))
            return null;

        var warehouseQuantities = await _dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock => stock.ItemId == itemId)
            .Select(stock => stock.Quantity)
            .ToListAsync(cancellationToken);
        var warehouseTotal = warehouseQuantities.Sum();
        if (requestedCurrentStock == warehouseTotal)
            return null;

        return BadRequest(
            $"Current stock must match the warehouse stock total ({warehouseTotal}). Save warehouse stock separately.");
    }

    private async Task RemoveWarehouseStocksIfNonInventoryAsync(Item entity, CancellationToken cancellationToken)
    {
        if (ItemOperationalPolicy.SupportsInventory(entity.TrackingType))
            return;

        var warehouseStocks = await _dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == entity.Id)
            .ToListAsync(cancellationToken);
        if (warehouseStocks.Count > 0)
            _dbContext.ItemWarehouseStocks.RemoveRange(warehouseStocks);
    }

    private async Task RemoveInventoryLedgerEntriesIfNonInventoryAsync(
        Item entity,
        CancellationToken cancellationToken)
    {
        if (!entity.IsDeleted && ItemOperationalPolicy.SupportsInventory(entity.TrackingType))
            return;

        var ledgerEntries = await _dbContext.InventoryLedgerEntries
            .Where(entry => entry.ItemId == entity.Id)
            .ToListAsync(cancellationToken);
        if (ledgerEntries.Count > 0)
            _dbContext.InventoryLedgerEntries.RemoveRange(ledgerEntries);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionNames.ItemEdit)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] long? expectedRevision, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditItems())
            return Forbid();

        await using var transaction = await InventoryMutationTransactionScope.BeginAsync(
            _dbContext,
            serializeInventoryMutations: true,
            cancellationToken);

        var entity = await _dbContext.Items
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!_officeScopeService.CanWriteOfficeForItems(entity.OfficeCode, entity.TenantCode))
            return Forbid();
        if (entity.IsDeleted)
            return NoContent();
        if (OptimisticConcurrencyGuard.Check(this, entity, expectedRevision, nameof(Item)) is { } conflict)
            return conflict;

        var referenceBlockMessage = await ItemDeletionReferenceGuard.BuildActiveReferenceBlockMessageAsync(
            _dbContext,
            id,
            cancellationToken);
        if (referenceBlockMessage is not null)
        {
            return Conflict(new
            {
                error = ItemDeletionReferenceGuard.ConflictCode,
                message = referenceBlockMessage
            });
        }

        entity.IsDeleted = true;
        var warehouseStocks = await _dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == id)
            .ToListAsync(cancellationToken);
        if (warehouseStocks.Count > 0)
            _dbContext.ItemWarehouseStocks.RemoveRange(warehouseStocks);

        await RemoveInventoryLedgerEntriesIfNonInventoryAsync(entity, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private async Task<ActionResult<ItemDto>> ResolveDuplicateItemAsync(
        DirectMutationCheck mutationCheck,
        CancellationToken cancellationToken)
    {
        if (mutationCheck.ExistingReceipt is not null &&
            Guid.TryParse(mutationCheck.ExistingReceipt.EntityId, out var entityId))
        {
            var entity = await _officeScopeService.ApplyItemScope(
                    _dbContext.Items.AsNoTracking())
                .FirstOrDefaultAsync(
                    current => current.Id == entityId,
                    cancellationToken);
            if (entity is not null)
                return Ok(entity.ToDto());
        }

        return Conflict(new DirectMutationConflictResponse
        {
            MutationId = mutationCheck.MutationId,
            EntityName = nameof(Item),
            EntityId = mutationCheck.RequestedEntityId,
            Reason = "The processed mutation is unavailable in the current scope."
        });
    }
}
