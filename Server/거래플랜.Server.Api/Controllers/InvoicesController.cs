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
[Route("invoices")]
public sealed class InvoicesController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IInvoiceNumberService _invoiceNumberService;
    private readonly OfficeScopeService _officeScopeService;
    private readonly InventoryLedgerService _inventoryLedgerService;
    private readonly InvoiceStockSnapshotService _invoiceStockSnapshotService;
    private readonly RentalSettlementRecalculationService _rentalSettlementRecalculationService;

    public InvoicesController(
        AppDbContext dbContext,
        ICurrentUserContext currentUserContext,
        IInvoiceNumberService invoiceNumberService,
        OfficeScopeService officeScopeService,
        InventoryLedgerService inventoryLedgerService,
        InvoiceStockSnapshotService invoiceStockSnapshotService,
        RentalSettlementRecalculationService rentalSettlementRecalculationService)
    {
        _dbContext = dbContext;
        _currentUserContext = currentUserContext;
        _invoiceNumberService = invoiceNumberService;
        _officeScopeService = officeScopeService;
        _inventoryLedgerService = inventoryLedgerService;
        _invoiceStockSnapshotService = invoiceStockSnapshotService;
        _rentalSettlementRecalculationService = rentalSettlementRecalculationService;
    }

    [HttpGet]
    public async Task<ActionResult<List<InvoiceDto>>> GetAll(
        [FromQuery] Guid? customerId,
        [FromQuery] string? q,
        [FromQuery] int take = 200,
        CancellationToken cancellationToken = default)
    {
        var readableCustomerIds = _officeScopeService.ApplyCustomerScope(_dbContext.Customers.AsNoTracking())
            .Select(customer => customer.Id);
        var query = _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices
            .Include(x => x.Customer)
            .Include(x => x.Lines)
            .Include(x => x.Payments)
            .ThenInclude(payment => payment.Attachments)
            .AsNoTracking());
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(x =>
                x.InvoiceNumber.Contains(q) ||
                x.Memo.Contains(q) ||
                (x.Customer != null &&
                 readableCustomerIds.Contains(x.CustomerId) &&
                 x.Customer.NameOriginal.Contains(q)));
        }

        var invoices = await query.OrderByDescending(x => x.InvoiceDate)
            .Take(Math.Min(take, 500))
            .ToListAsync(cancellationToken);
        var readableCustomerIdSet = await LoadReadableCustomerIdSetAsync(
            invoices.Select(invoice => invoice.CustomerId),
            cancellationToken);

        return Ok(invoices
            .Select(invoice => ToScopedDto(invoice, readableCustomerIdSet))
            .ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InvoiceDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices
            .Include(x => x.Customer)
            .Include(x => x.Lines)
            .Include(x => x.Payments)
            .ThenInclude(payment => payment.Attachments)
            .AsNoTracking())
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();

        var readableCustomerIdSet = await LoadReadableCustomerIdSetAsync([entity.CustomerId], cancellationToken);
        return Ok(ToScopedDto(entity, readableCustomerIdSet));
    }

    private async Task<HashSet<Guid>> LoadReadableCustomerIdSetAsync(
        IEnumerable<Guid> customerIds,
        CancellationToken cancellationToken)
    {
        var ids = customerIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var readableIds = await _officeScopeService.ApplyCustomerScope(_dbContext.Customers.AsNoTracking())
            .Where(customer => ids.Contains(customer.Id))
            .Select(customer => customer.Id)
            .ToListAsync(cancellationToken);
        return readableIds.ToHashSet();
    }

    private static InvoiceDto ToScopedDto(Invoice invoice, IReadOnlySet<Guid> readableCustomerIds)
    {
        var dto = invoice.ToDto();
        if (!readableCustomerIds.Contains(invoice.CustomerId))
            dto.CustomerName = string.Empty;
        return dto;
    }

    [HttpPost]
    [Authorize(Policy = PermissionNames.InvoiceEdit)]
    public async Task<ActionResult<InvoiceDto>> Create([FromBody] InvoiceDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditInvoices())
            return Forbid();
        if (dto.IsDeleted)
            return SoftDeleteMutationGuard.RejectCreate("전표");

        await using var transaction = await InventoryMutationTransactionScope.BeginAsync(
            _dbContext,
            serializeInventoryMutations: true,
            cancellationToken);

        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
        if (customer is null || customer.IsDeleted)
            return BadRequest("Referenced customer was not found.");
        if (!_officeScopeService.CanWriteOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
            return Forbid();
        if (ValidateRequestedInvoiceScopeConsistency(dto, customer) is { } requestedScopeError)
            return requestedScopeError;

        var mutationCheck = await ProcessedSyncMutationRecorder.CheckAsync(
            _dbContext,
            dto,
            nameof(Invoice),
            cancellationToken);
        if (mutationCheck.Status == DirectMutationStatus.Conflict)
            return Conflict(ProcessedSyncMutationRecorder.BuildConflictResponse(mutationCheck));
        if (mutationCheck.Status == DirectMutationStatus.Duplicate)
            return await ResolveDuplicateInvoiceAsync(mutationCheck, cancellationToken);

        dto.ResponsibleOfficeCode = _officeScopeService.ResolveInvoiceResponsibleScopeForCreate(
            dto.ResponsibleOfficeCode,
            customer.ResponsibleOfficeCode);
        dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            dto.OfficeCode,
            dto.ResponsibleOfficeCode,
            customer.OfficeCode);
        dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
            dto.TenantCode,
            dto.OfficeCode,
            customer.TenantCode,
            customer.OfficeCode);
        if (ValidateRequestedInvoiceScopeConsistency(dto, customer) is { } resolvedScopeError)
            return resolvedScopeError;
        if (ValidateAndNormalizeSourceWarehouse(dto) is { } warehouseScopeError)
            return warehouseScopeError;
        if (await ValidateInvoiceLineItemScopeAsync(dto.Lines, cancellationToken) is { } lineScopeError)
            return lineScopeError;
        if (await ValidateLinkedRentalBillingProfileScopeAsync(dto, cancellationToken) is { } rentalProfileScopeError)
            return rentalProfileScopeError;
        if (await ValidateLinkedRentalBillingRunWritableAsync(
                dto.LinkedRentalBillingProfileId,
                dto.LinkedRentalBillingRunId,
                cancellationToken) is { } rentalRunError)
        {
            return rentalRunError;
        }

        var entityId = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id;
        NormalizeNewInvoiceVersionMetadata(dto, entityId);
        var entity = new Invoice { Id = entityId };
        entity.Apply(dto);
        if (string.IsNullOrWhiteSpace(entity.InvoiceNumber))
        {
            entity.InvoiceNumber = await _invoiceNumberService.GenerateAsync(entity.CustomerId, entity.InvoiceDate, cancellationToken);
        }
        await TaxInvoiceNumberAssignmentService.EnsureAssignedAsync(_dbContext, entity, cancellationToken);

        ApplyInvoiceLines(entity, dto.Lines);
        var currentStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);

        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
            currentStockDeltas,
            cancellationToken);

        _dbContext.Invoices.Add(entity);
        ProcessedSyncMutationRecorder.Record(_dbContext, mutationCheck, entity.Id);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateRentalSettlementsForInvoiceSaveAsync(null, entity, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionNames.InvoiceEdit)]
    public async Task<ActionResult<InvoiceDto>> Update(Guid id, [FromBody] InvoiceDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditInvoices())
            return Forbid();
        if (dto.Id != Guid.Empty && dto.Id != id)
            return BadRequest("Invoice route id must match the body id.");

        dto.Id = id;

        await using var transaction = await InventoryMutationTransactionScope.BeginAsync(
            _dbContext,
            serializeInventoryMutations: true,
            cancellationToken);

        var entity = await _dbContext.Invoices.Include(x => x.Customer).Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!CanWriteInvoiceUsingResolvedVersionScope(entity))
            return Forbid();

        var mutationCheck = await ProcessedSyncMutationRecorder.CheckAsync(
            _dbContext,
            dto,
            nameof(Invoice),
            cancellationToken);
        if (mutationCheck.Status == DirectMutationStatus.Conflict)
            return Conflict(ProcessedSyncMutationRecorder.BuildConflictResponse(mutationCheck));
        if (mutationCheck.Status == DirectMutationStatus.Duplicate)
            return await ResolveDuplicateInvoiceAsync(mutationCheck, cancellationToken);

        if (OptimisticConcurrencyGuard.Check(this, entity, dto, nameof(Invoice)) is { } conflict)
            return conflict;
        if (dto.IsDeleted)
            return SoftDeleteMutationGuard.RejectUpdate("전표");
        if (ValidateAndNormalizeExistingInvoiceVersionMetadata(entity, dto) is { } versionMetadataError)
            return versionMetadataError;
        if (await ValidateExistingLinkedRentalBillingProfileScopeAsync(entity.LinkedRentalBillingProfileId, cancellationToken) is { } existingRentalProfileScopeError)
            return existingRentalProfileScopeError;
        if (await ValidateLinkedRentalBillingRunWritableAsync(
                entity.LinkedRentalBillingProfileId,
                entity.LinkedRentalBillingRunId,
                cancellationToken) is { } existingRentalRunError)
        {
            return existingRentalRunError;
        }
        if (ValidateWritableInvoiceStockWarehouse(entity) is { } existingWarehouseScopeError)
            return existingWarehouseScopeError;
        if (await ValidateInvoiceLineItemScopeAsync(entity.Lines, cancellationToken) is { } existingLineScopeError)
            return existingLineScopeError;

        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
        if (customer is null || customer.IsDeleted)
            return BadRequest("Referenced customer was not found.");
        if (!_officeScopeService.CanWriteOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
            return Forbid();
        if (ValidateRequestedInvoiceScopeConsistency(dto, customer) is { } requestedScopeError)
            return requestedScopeError;

        dto.ResponsibleOfficeCode = _officeScopeService.ResolveInvoiceResponsibleScopeForCreate(
            dto.ResponsibleOfficeCode,
            customer.ResponsibleOfficeCode);
        dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            dto.OfficeCode,
            dto.ResponsibleOfficeCode,
            customer.OfficeCode);
        dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
            dto.TenantCode,
            dto.OfficeCode,
            customer.TenantCode,
            customer.OfficeCode);
        if (ValidateRequestedInvoiceScopeConsistency(dto, customer) is { } resolvedScopeError)
            return resolvedScopeError;
        if (ValidateAndNormalizeSourceWarehouse(dto) is { } warehouseScopeError)
            return warehouseScopeError;
        if (await ValidateInvoiceLineItemScopeAsync(dto.Lines, cancellationToken) is { } lineScopeError)
            return lineScopeError;
        if (await ValidateLinkedRentalBillingProfileScopeAsync(dto, cancellationToken) is { } rentalProfileScopeError)
            return rentalProfileScopeError;
        if (await ValidateLinkedRentalBillingRunWritableAsync(
                dto.LinkedRentalBillingProfileId,
                dto.LinkedRentalBillingRunId,
                cancellationToken) is { } rentalRunError)
        {
            return rentalRunError;
        }

        if (await InvoiceStructuralMutationGuard.ShouldProtectExistingInvoiceFromSameIdStructuralMutationAsync(
                _dbContext,
                entity,
                dto,
                cancellationToken,
                protectRentalLinks: false,
                allowSameRentalTargetTransactions: true) &&
            InvoiceStructuralMutationGuard.HasSameIdInvoiceStructuralMutation(entity, dto))
        {
            return Conflict(new ExpectedRevisionConflictResponse
            {
                EntityName = nameof(Invoice),
                EntityId = entity.Id,
                ExpectedRevision = dto.ExpectedRevision > 0 ? dto.ExpectedRevision : dto.Revision,
                CurrentRevision = entity.Revision,
                Reason = ApiConflictReasonTranslator.ProtectedInvoiceSameIdStructuralMutation
            });
        }

        var activeScopedVersions = await LoadActiveInvoiceVersionsInSameScopeAsync(
            entity,
            cancellationToken);
        var versionParticipants = activeScopedVersions
            .Append(entity)
            .GroupBy(candidate => candidate.Id)
            .Select(group => group.First())
            .ToList();
        var deterministicLatestVersion = versionParticipants
            .OrderByDescending(candidate => Math.Max(1, candidate.VersionNumber))
            .ThenByDescending(candidate => candidate.Id)
            .First();
        var beforeLatestVersions = versionParticipants
            .Where(candidate => candidate.IsLatestVersion)
            .ToList();
        var afterLatestVersions = new List<Invoice> { deterministicLatestVersion };
        var stockTransitionVersions = beforeLatestVersions
            .Concat(afterLatestVersions)
            .GroupBy(candidate => candidate.Id)
            .Select(group => group.First())
            .ToList();
        foreach (var candidate in stockTransitionVersions)
        {
            if (ValidateWritableInvoiceStockWarehouse(candidate) is { } candidateWarehouseScopeError)
                return candidateWarehouseScopeError;
            if (await ValidateInvoiceLineItemScopeAsync(candidate.Lines, cancellationToken) is { } candidateLineScopeError)
                return candidateLineScopeError;
        }

        var latestFlagChangingVersions = versionParticipants
            .Where(candidate =>
                candidate.IsLatestVersion !=
                (candidate.Id == deterministicLatestVersion.Id))
            .ToList();
        foreach (var candidate in latestFlagChangingVersions)
        {
            if (await ValidateExistingLinkedRentalBillingProfileScopeAsync(
                    candidate.LinkedRentalBillingProfileId,
                    cancellationToken) is { } candidateRentalProfileScopeError)
            {
                return candidateRentalProfileScopeError;
            }
        }

        var latestFlagChangingInvoiceIds = latestFlagChangingVersions
            .Select(candidate => candidate.Id)
            .Distinct()
            .ToList();
        if (!_currentUserContext.HasPermission(PermissionNames.PaymentEdit) &&
            await HasActivePaymentSideEffectsForInvoiceDeleteAsync(
                latestFlagChangingInvoiceIds,
                cancellationToken))
        {
            return Forbid();
        }
        if (await ValidatePaymentWriteScopesForInvoiceSideEffectsAsync(
                latestFlagChangingInvoiceIds,
                cancellationToken) is { } paymentScopeError)
        {
            return paymentScopeError;
        }
        if (await ValidateLinkedTransactionScopesForInvoiceDeleteAsync(
                [],
                latestFlagChangingInvoiceIds,
                cancellationToken) is { } linkedTransactionScopeError)
        {
            return linkedTransactionScopeError;
        }

        var rentalSettlementTargets = await _rentalSettlementRecalculationService
            .LoadRentalSettlementTargetsForInvoiceDeleteAsync(
                latestFlagChangingInvoiceIds,
                cancellationToken);
        var previousCombinedStockDeltas = await BuildCombinedInvoiceStockDeltasAsync(
            beforeLatestVersions,
            cancellationToken);
        var previousRentalTarget = new Invoice
        {
            LinkedRentalBillingProfileId = entity.LinkedRentalBillingProfileId,
            LinkedRentalBillingRunId = entity.LinkedRentalBillingRunId
        };

        entity.Apply(dto);
        await TaxInvoiceNumberAssignmentService.EnsureAssignedAsync(_dbContext, entity, cancellationToken);
        _dbContext.InvoiceLines.RemoveRange(entity.Lines);
        entity.Lines.Clear();
        ApplyInvoiceLines(entity, dto.Lines);
        foreach (var candidate in versionParticipants)
        {
            candidate.IsLatestVersion =
                candidate.Id == deterministicLatestVersion.Id;
        }
        var currentCombinedStockDeltas = await BuildCombinedInvoiceStockDeltasAsync(
            afterLatestVersions,
            cancellationToken);

        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            previousCombinedStockDeltas,
            currentCombinedStockDeltas,
            cancellationToken);

        ProcessedSyncMutationRecorder.Record(_dbContext, mutationCheck, entity.Id);
        await _dbContext.SaveChangesAsync(cancellationToken);
        AddRentalSettlementTarget(
            rentalSettlementTargets,
            previousRentalTarget.LinkedRentalBillingProfileId,
            previousRentalTarget.LinkedRentalBillingRunId);
        AddRentalSettlementTarget(
            rentalSettlementTargets,
            entity.LinkedRentalBillingProfileId,
            entity.LinkedRentalBillingRunId);
        await _rentalSettlementRecalculationService.RecalculateRentalSettlementsAsync(
            rentalSettlementTargets.Distinct().ToList(),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionNames.InvoiceEdit)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] long? expectedRevision, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditInvoices())
            return Forbid();

        await using var transaction = await InventoryMutationTransactionScope.BeginAsync(
            _dbContext,
            serializeInventoryMutations: true,
            cancellationToken);

        var entity = await _dbContext.Invoices.Include(x => x.Customer).Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!CanWriteInvoiceUsingResolvedVersionScope(entity))
            return Forbid();
        if (OptimisticConcurrencyGuard.Check(this, entity, expectedRevision, nameof(Invoice)) is { } conflict)
            return conflict;
        if (await ValidateInvoiceLineItemScopeAsync(entity.Lines, cancellationToken) is { } lineScopeError)
            return lineScopeError;
        if (await ValidateExistingLinkedRentalBillingProfileScopeAsync(entity.LinkedRentalBillingProfileId, cancellationToken) is { } rentalProfileScopeError)
            return rentalProfileScopeError;
        var activeScopedVersions = await LoadActiveInvoiceVersionsInSameScopeAsync(
            entity,
            cancellationToken);
        var promotedPreviousVersion = activeScopedVersions
            .OrderByDescending(candidate => Math.Max(1, candidate.VersionNumber))
            .ThenByDescending(candidate => candidate.Id)
            .FirstOrDefault();

        var beforeLatestVersions = new List<Invoice>();
        if (entity.IsLatestVersion)
            beforeLatestVersions.Add(entity);
        beforeLatestVersions.AddRange(activeScopedVersions.Where(candidate => candidate.IsLatestVersion));
        var afterLatestVersions = promotedPreviousVersion is null
            ? []
            : new List<Invoice> { promotedPreviousVersion };
        var stockTransitionVersions = beforeLatestVersions
            .Concat(afterLatestVersions)
            .GroupBy(candidate => candidate.Id)
            .Select(group => group.First())
            .ToList();
        foreach (var candidate in stockTransitionVersions)
        {
            if (ValidateWritableInvoiceStockWarehouse(candidate) is { } warehouseScopeError)
                return warehouseScopeError;
            if (await ValidateInvoiceLineItemScopeAsync(candidate.Lines, cancellationToken) is { } candidateLineScopeError)
                return candidateLineScopeError;
        }

        var latestFlagChangingVersions = activeScopedVersions
            .Where(candidate =>
                candidate.IsLatestVersion != (candidate.Id == promotedPreviousVersion?.Id))
            .ToList();
        foreach (var candidate in latestFlagChangingVersions)
        {
            if (await ValidateExistingLinkedRentalBillingProfileScopeAsync(
                    candidate.LinkedRentalBillingProfileId,
                    cancellationToken) is { } candidateRentalProfileScopeError)
            {
                return candidateRentalProfileScopeError;
            }
        }

        var businessEffectInvoiceIds = latestFlagChangingVersions
            .Select(candidate => candidate.Id)
            .Append(id)
            .Distinct()
            .ToList();
        if (!_currentUserContext.HasPermission(PermissionNames.PaymentEdit) &&
            await HasActivePaymentSideEffectsForInvoiceDeleteAsync(businessEffectInvoiceIds, cancellationToken))
        {
            return Forbid();
        }
        if (await ValidatePaymentWriteScopesForInvoiceSideEffectsAsync(
                businessEffectInvoiceIds,
                cancellationToken) is { } paymentScopeError)
        {
            return paymentScopeError;
        }
        if (await ValidateLinkedTransactionScopesForInvoiceDeleteAsync(
                [id],
                latestFlagChangingVersions.Select(candidate => candidate.Id).ToList(),
                cancellationToken) is { } linkedTransactionScopeError)
        {
            return linkedTransactionScopeError;
        }

        var rentalSettlementTargets = await _rentalSettlementRecalculationService
            .LoadRentalSettlementTargetsForInvoiceDeleteAsync(
                businessEffectInvoiceIds,
                cancellationToken);
        var previousStockDeltas = await BuildCombinedInvoiceStockDeltasAsync(
            beforeLatestVersions,
            cancellationToken);
        entity.IsDeleted = true;
        entity.IsLatestVersion = false;
        foreach (var line in entity.Lines)
        {
            line.IsDeleted = true;
        }
        foreach (var candidate in activeScopedVersions)
        {
            candidate.IsLatestVersion = candidate.Id == promotedPreviousVersion?.Id;
        }
        var currentStockDeltas = await BuildCombinedInvoiceStockDeltasAsync(
            afterLatestVersions,
            cancellationToken);
        await _rentalSettlementRecalculationService.DetachTransactionsFromInvoicesAsync([id], cancellationToken);
        await _rentalSettlementRecalculationService.MarkPaymentsDeletedForInvoicesAsync([id], cancellationToken);

        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            previousStockDeltas,
            currentStockDeltas,
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _rentalSettlementRecalculationService.RecalculateRentalSettlementsAsync(rentalSettlementTargets, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private ActionResult? ValidateWritableInvoiceStockWarehouse(Invoice invoice)
    {
        var scope = ResolveInvoiceVersionScope(invoice);
        var warehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
            invoice.SourceWarehouseCode,
            scope.ResponsibleOfficeCode,
            scope.OfficeCode);
        return _officeScopeService.CanWriteWarehouse(warehouseCode, scope.OfficeCode)
            ? null
            : Forbid();
    }

    private bool CanWriteInvoiceUsingResolvedVersionScope(Invoice invoice)
    {
        var scope = ResolveInvoiceVersionScope(invoice);
        return _officeScopeService.CanWriteOfficeForInvoices(
            scope.ResponsibleOfficeCode,
            scope.TenantCode,
            scope.OfficeCode);
    }

    private async Task<Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>>
        BuildCombinedInvoiceStockDeltasAsync(
            IEnumerable<Invoice> invoices,
            CancellationToken cancellationToken)
    {
        var combined = new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>();
        foreach (var invoice in invoices
                     .GroupBy(candidate => candidate.Id)
                     .Select(group => group.First()))
        {
            var deltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(
                invoice,
                cancellationToken);
            foreach (var (key, quantity) in deltas)
            {
                combined[key] = combined.TryGetValue(key, out var current)
                    ? current + quantity
                    : quantity;
            }
        }

        return combined;
    }

    private ActionResult? ValidateAndNormalizeExistingInvoiceVersionMetadata(
        Invoice entity,
        InvoiceDto dto)
    {
        var existingVersionGroupId = entity.VersionGroupId == Guid.Empty
            ? entity.Id
            : entity.VersionGroupId;
        var requestedVersionGroupId = dto.VersionGroupId == Guid.Empty
            ? dto.Id
            : dto.VersionGroupId;
        var existingVersionNumber = Math.Max(1, entity.VersionNumber);
        var requestedVersionNumber = Math.Max(1, dto.VersionNumber);
        var existingPreviousVersionId = NormalizeOptionalGuid(entity.PreviousVersionId);
        var requestedPreviousVersionId = NormalizeOptionalGuid(dto.PreviousVersionId);

        if (requestedVersionGroupId != existingVersionGroupId ||
            requestedVersionNumber != existingVersionNumber ||
            requestedPreviousVersionId != existingPreviousVersionId)
        {
            return BadRequest("Invoice version metadata cannot be changed through this endpoint.");
        }

        dto.VersionGroupId = existingVersionGroupId;
        dto.VersionNumber = existingVersionNumber;
        dto.PreviousVersionId = existingPreviousVersionId;
        dto.IsLatestVersion = entity.IsLatestVersion;
        return null;
    }

    private async Task<List<Invoice>> LoadActiveInvoiceVersionsInSameScopeAsync(
        Invoice invoice,
        CancellationToken cancellationToken)
    {
        var versionGroupId = invoice.VersionGroupId == Guid.Empty
            ? invoice.Id
            : invoice.VersionGroupId;
        var candidates = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(candidate => candidate.Customer)
            .Include(candidate => candidate.Lines)
            .Where(candidate =>
                candidate.Id != invoice.Id &&
                !candidate.IsDeleted &&
                (candidate.VersionGroupId == versionGroupId ||
                 (candidate.VersionGroupId == Guid.Empty && candidate.Id == versionGroupId)))
            .ToListAsync(cancellationToken);

        return candidates
            .Where(candidate => HasSameInvoiceVersionScope(invoice, candidate))
            .ToList();
    }

    private static bool HasSameInvoiceVersionScope(Invoice left, Invoice right)
    {
        if (left.CustomerId != right.CustomerId)
            return false;

        var leftScope = ResolveInvoiceVersionScope(left);
        var rightScope = ResolveInvoiceVersionScope(right);
        return string.Equals(
                   leftScope.TenantCode,
                   rightScope.TenantCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   leftScope.OfficeCode,
                   rightScope.OfficeCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   leftScope.ResponsibleOfficeCode,
                   rightScope.ResponsibleOfficeCode,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static (string TenantCode, string OfficeCode, string ResponsibleOfficeCode) ResolveInvoiceVersionScope(
        Invoice invoice)
    {
        var customerResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeLoose(
            invoice.Customer?.ResponsibleOfficeCode,
            invoice.Customer?.OfficeCode,
            OfficeCodeCatalog.Usenet);
        var customerOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
            invoice.Customer?.OfficeCode,
            customerResponsibleOfficeCode,
            customerResponsibleOfficeCode);
        var customerTenantCode =
            TenantScopeCatalog.TryNormalizeTenantCode(invoice.Customer?.TenantCode, out var normalizedCustomerTenantCode)
                ? normalizedCustomerTenantCode
                : TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                    null,
                    customerOfficeCode,
                    fallbackOfficeCode: customerResponsibleOfficeCode);

        // Preserve explicit, recognized scope values even when they disagree. A
        // mismatched tenant/office tuple must fail authorization instead of being
        // silently normalized into the caller's writable scope. Only missing or
        // unrecognized legacy values fall back to the linked customer.
        var responsibleOfficeCode =
            OfficeCodeCatalog.TryNormalizeOfficeCode(
                invoice.ResponsibleOfficeCode,
                out var normalizedResponsibleOfficeCode)
                ? normalizedResponsibleOfficeCode
                : customerResponsibleOfficeCode;
        var officeCode =
            OfficeCodeCatalog.TryNormalizeScope(invoice.OfficeCode, out var normalizedOfficeCode)
                ? OfficeCodeCatalog.ResolveOwningOfficeCode(
                    normalizedOfficeCode,
                    responsibleOfficeCode,
                    normalizedOfficeCode)
                : customerOfficeCode;
        var tenantCode =
            TenantScopeCatalog.TryNormalizeTenantCode(invoice.TenantCode, out var normalizedTenantCode)
                ? normalizedTenantCode
                : customerTenantCode;
        return (tenantCode, officeCode, responsibleOfficeCode);
    }

    private ActionResult? ValidateRequestedInvoiceScopeConsistency(
        InvoiceDto dto,
        Customer customer)
    {
        var customerScope = ResolveInvoiceVersionScope(new Invoice
        {
            CustomerId = customer.Id,
            Customer = customer
        });
        var tenantCode =
            TenantScopeCatalog.TryNormalizeTenantCode(dto.TenantCode, out var normalizedTenantCode)
                ? normalizedTenantCode
                : customerScope.TenantCode;
        var officeCode =
            OfficeCodeCatalog.TryNormalizeScope(dto.OfficeCode, out var normalizedOfficeCode)
                ? normalizedOfficeCode
                : customerScope.OfficeCode;
        var responsibleOfficeCode =
            OfficeCodeCatalog.TryNormalizeOfficeCode(
                dto.ResponsibleOfficeCode,
                out var normalizedResponsibleOfficeCode)
                ? normalizedResponsibleOfficeCode
                : customerScope.ResponsibleOfficeCode;

        if (OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var concreteOfficeCode) &&
            !TenantScopeCatalog.TenantContainsOffice(tenantCode, concreteOfficeCode))
        {
            return BadRequest("Invoice tenant and office scope values are inconsistent.");
        }

        if (OfficeCodeCatalog.TryNormalizeOfficeCode(
                responsibleOfficeCode,
                out var concreteResponsibleOfficeCode) &&
            !TenantScopeCatalog.TenantContainsOffice(
                tenantCode,
                concreteResponsibleOfficeCode))
        {
            return BadRequest("Invoice tenant and office scope values are inconsistent.");
        }

        return null;
    }

    private static void NormalizeNewInvoiceVersionMetadata(InvoiceDto dto, Guid entityId)
    {
        dto.Id = entityId;
        dto.VersionGroupId = entityId;
        dto.VersionNumber = 1;
        dto.PreviousVersionId = null;
        dto.IsLatestVersion = true;
    }

    private static Guid? NormalizeOptionalGuid(Guid? value)
        => !value.HasValue || value.Value == Guid.Empty
            ? null
            : value.Value;

    private async Task<ActionResult<InvoiceDto>> ResolveDuplicateInvoiceAsync(
        DirectMutationCheck mutationCheck,
        CancellationToken cancellationToken)
    {
        if (mutationCheck.ExistingReceipt is null ||
            !Guid.TryParse(mutationCheck.ExistingReceipt.EntityId, out var entityId))
        {
            return Conflict(new DirectMutationConflictResponse
            {
                MutationId = mutationCheck.MutationId,
                EntityName = nameof(Invoice),
                EntityId = mutationCheck.RequestedEntityId,
                Reason = "The processed mutation receipt does not reference a valid invoice."
            });
        }

        var entity = await _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices
                .Include(invoice => invoice.Customer)
                .Include(invoice => invoice.Lines)
                .Include(invoice => invoice.Payments)
                .ThenInclude(payment => payment.Attachments)
                .AsNoTracking())
            .FirstOrDefaultAsync(invoice => invoice.Id == entityId, cancellationToken);
        if (entity is null)
        {
            return Conflict(new DirectMutationConflictResponse
            {
                MutationId = mutationCheck.MutationId,
                EntityName = nameof(Invoice),
                EntityId = entityId,
                Reason = "The processed mutation receipt exists, but its invoice is unavailable in the current scope."
            });
        }

        var readableCustomerIdSet = await LoadReadableCustomerIdSetAsync(
            [entity.CustomerId],
            cancellationToken);
        return Ok(ToScopedDto(entity, readableCustomerIdSet));
    }

    private async Task RecalculateRentalSettlementsForInvoiceSaveAsync(
        Invoice? previousInvoice,
        Invoice currentInvoice,
        CancellationToken cancellationToken)
    {
        var targets = new List<(Guid ProfileId, Guid? RunId)>();
        AddRentalSettlementTarget(targets, previousInvoice?.LinkedRentalBillingProfileId, previousInvoice?.LinkedRentalBillingRunId);
        AddRentalSettlementTarget(targets, currentInvoice.LinkedRentalBillingProfileId, currentInvoice.LinkedRentalBillingRunId);
        await _rentalSettlementRecalculationService.RecalculateRentalSettlementsAsync(targets.Distinct().ToList(), cancellationToken);
    }

    private async Task<bool> HasActivePaymentSideEffectsForInvoiceDeleteAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return false;

        if (await _dbContext.Payments.IgnoreQueryFilters()
                .AnyAsync(payment => !payment.IsDeleted && invoiceIds.Contains(payment.InvoiceId), cancellationToken))
        {
            return true;
        }

        return await _dbContext.Transactions.IgnoreQueryFilters()
            .AnyAsync(transaction =>
                    !transaction.IsDeleted &&
                    transaction.LinkedInvoiceId.HasValue &&
                    invoiceIds.Contains(transaction.LinkedInvoiceId.Value),
                cancellationToken);
    }

    private async Task<ActionResult?> ValidatePaymentWriteScopesForInvoiceSideEffectsAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return null;

        var paymentInvoiceIds = await _dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(payment =>
                !payment.IsDeleted &&
                invoiceIds.Contains(payment.InvoiceId))
            .Select(payment => payment.InvoiceId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (paymentInvoiceIds.Count == 0)
            return null;

        var paymentInvoices = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .AsNoTracking()
            .Where(invoice => paymentInvoiceIds.Contains(invoice.Id))
            .ToListAsync(cancellationToken);
        if (paymentInvoices.Count != paymentInvoiceIds.Count)
            return Forbid();

        foreach (var invoice in paymentInvoices)
        {
            var scope = ResolveInvoiceVersionScope(invoice);
            if (!_officeScopeService.CanWriteOfficeForPayments(
                    scope.ResponsibleOfficeCode,
                    scope.TenantCode,
                    scope.OfficeCode))
            {
                return Forbid();
            }
        }

        return null;
    }

    private static void AddRentalSettlementTarget(List<(Guid ProfileId, Guid? RunId)> targets, Guid? profileId, Guid? runId)
    {
        if (!profileId.HasValue || profileId.Value == Guid.Empty)
            return;

        targets.Add((profileId.Value, runId));
    }

    private async Task<ActionResult?> ValidateLinkedTransactionScopesForInvoiceDeleteAsync(
        IReadOnlyCollection<Guid> detachedInvoiceIds,
        IReadOnlyCollection<Guid> latestFlagChangingInvoiceIds,
        CancellationToken cancellationToken)
    {
        if (detachedInvoiceIds.Count == 0 && latestFlagChangingInvoiceIds.Count == 0)
            return null;

        var linkedTransactions = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedInvoiceId.HasValue &&
                (detachedInvoiceIds.Contains(transaction.LinkedInvoiceId.Value) ||
                 latestFlagChangingInvoiceIds.Contains(transaction.LinkedInvoiceId.Value)))
            .Select(transaction => new
            {
                transaction.ResponsibleOfficeCode,
                transaction.TenantCode,
                transaction.OfficeCode,
                transaction.LinkedRentalBillingProfileId
            })
            .ToListAsync(cancellationToken);

        foreach (var transaction in linkedTransactions)
        {
            if (!_officeScopeService.CanWriteOfficeForPayments(
                    transaction.ResponsibleOfficeCode,
                    transaction.TenantCode,
                    transaction.OfficeCode))
            {
                return Forbid();
            }
        }

        var profileIds = linkedTransactions
            .Where(transaction =>
                transaction.LinkedRentalBillingProfileId.HasValue &&
                transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
            .Select(transaction => transaction.LinkedRentalBillingProfileId!.Value)
            .Distinct()
            .ToList();

        foreach (var profileId in profileIds)
        {
            if (await ValidateLinkedRentalBillingProfileScopeAsync(
                    profileId,
                    allowMissingOrDeleted: true,
                    cancellationToken) is { } rentalProfileScopeError)
            {
                return rentalProfileScopeError;
            }
        }

        return null;
    }

    private async Task<ActionResult?> ValidateInvoiceLineItemScopeAsync(
        IEnumerable<InvoiceLineDto>? lines,
        CancellationToken cancellationToken)
    {
        var activeLines = (lines ?? [])
            .Where(line => !line.IsDeleted)
            .ToList();
        var invalidQuantityLine = activeLines.FirstOrDefault(line =>
            !DatabaseNumericContract.IsPositiveQuantity18Scale2(line.Quantity));
        if (invalidQuantityLine is not null)
        {
            return BadRequest(
                $"Active invoice line quantity must be greater than zero and fit numeric(18,2): {invalidQuantityLine.Id}.");
        }

        var itemIds = activeLines
            .Where(line => line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
            .Select(line => line.ItemId!.Value)
            .Distinct()
            .ToList();
        if (itemIds.Count == 0)
            return null;

        var items = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) && !item.IsDeleted)
            .Select(item => new { item.Id, item.OfficeCode, item.TenantCode, item.TrackingType })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        foreach (var itemId in itemIds)
        {
            if (!items.TryGetValue(itemId, out var item))
                return BadRequest($"Referenced invoice line item was not found: {itemId}.");

            if (!_officeScopeService.CanReadOfficeForItems(item.OfficeCode, item.TenantCode))
                return Forbid();
        }

        foreach (var line in activeLines.Where(line => line.ItemId.HasValue && line.ItemId.Value != Guid.Empty))
        {
            if (items.TryGetValue(line.ItemId!.Value, out var item))
                line.ItemTrackingType = ItemTrackingTypes.Normalize(item.TrackingType);
        }

        return null;
    }

    private ActionResult? ValidateAndNormalizeSourceWarehouse(InvoiceDto dto)
    {
        var warehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
            dto.SourceWarehouseCode,
            dto.ResponsibleOfficeCode,
            dto.OfficeCode);
        if (!_officeScopeService.CanWriteWarehouse(warehouseCode, dto.OfficeCode))
            return Forbid();

        dto.SourceWarehouseCode = warehouseCode;
        return null;
    }

    private async Task<ActionResult?> ValidateInvoiceLineItemScopeAsync(
        IEnumerable<InvoiceLine>? lines,
        CancellationToken cancellationToken)
    {
        var itemIds = (lines ?? [])
            .Where(line => !line.IsDeleted && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
            .Select(line => line.ItemId!.Value)
            .Distinct()
            .ToList();
        if (itemIds.Count == 0)
            return null;

        var items = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) && !item.IsDeleted)
            .Select(item => new { item.Id, item.OfficeCode, item.TenantCode })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        foreach (var itemId in itemIds)
        {
            if (!items.TryGetValue(itemId, out var item))
                return BadRequest($"Referenced invoice line item was not found: {itemId}.");

            if (!_officeScopeService.CanReadOfficeForItems(item.OfficeCode, item.TenantCode))
                return Forbid();
        }

        return null;
    }

    private async Task<ActionResult?> ValidateLinkedRentalBillingProfileScopeAsync(
        InvoiceDto dto,
        CancellationToken cancellationToken)
        => await ValidateLinkedRentalBillingProfileScopeAsync(
            dto.LinkedRentalBillingProfileId,
            allowMissingOrDeleted: false,
            cancellationToken);

    private async Task<ActionResult?> ValidateExistingLinkedRentalBillingProfileScopeAsync(
        Guid? profileId,
        CancellationToken cancellationToken)
        => await ValidateLinkedRentalBillingProfileScopeAsync(
            profileId,
            allowMissingOrDeleted: true,
            cancellationToken);

    private async Task<ActionResult?> ValidateLinkedRentalBillingProfileScopeAsync(
        Guid? profileId,
        bool allowMissingOrDeleted,
        CancellationToken cancellationToken)
    {
        if (!profileId.HasValue || profileId.Value == Guid.Empty)
            return null;

        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id == profileId.Value)
            .Select(current => new
            {
                current.IsDeleted,
                current.ResponsibleOfficeCode,
                current.TenantCode,
                current.OfficeCode
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (profile is null || profile.IsDeleted)
        {
            if (allowMissingOrDeleted)
                return null;

            return BadRequest("Referenced rental billing profile was not found.");
        }

        if (!_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode))
            return Forbid();

        return null;
    }

    private async Task<ActionResult?> ValidateLinkedRentalBillingRunWritableAsync(
        Guid? profileId,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        if (!profileId.HasValue || profileId.Value == Guid.Empty)
            return null;

        var billingRunsJson = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id == profileId.Value && !current.IsDeleted)
            .Select(current => current.BillingRunsJson)
            .FirstOrDefaultAsync(cancellationToken);
        if (billingRunsJson is null)
            return null;

        var lookup = runId.HasValue && runId.Value != Guid.Empty
            ? RentalBillingRunTombstonePolicy.LookupForServerMutation(billingRunsJson, runId.Value)
            : RentalBillingRunTombstonePolicy.ValidateForServerMutation(billingRunsJson);
        if (!lookup.IsValid)
        {
            return Conflict(
                "Referenced rental billing profile has malformed billing run tombstone JSON.");
        }

        return lookup.IsTombstoned
            ? Conflict("Referenced rental billing run was deleted and cannot be linked to an active invoice.")
            : null;
    }

    private static void ApplyInvoiceLines(Invoice invoice, IEnumerable<InvoiceLineDto>? lines)
    {
        if (invoice.IsDeleted)
            return;

        var order = 1;
        foreach (var line in lines ?? [])
        {
            if (line.IsDeleted)
                continue;

            invoice.Lines.Add(CreateInvoiceLine(invoice.Id, line, line.Id == Guid.Empty ? Guid.NewGuid() : line.Id, order++));
        }
    }

    private static InvoiceLine CreateInvoiceLine(Guid invoiceId, InvoiceLineDto line, Guid resolvedId, int fallbackOrderIndex)
    {
        var entity = new InvoiceLine();
        ApplyInvoiceLine(entity, invoiceId, line, resolvedId, fallbackOrderIndex);
        return entity;
    }

    private static void ApplyInvoiceLine(InvoiceLine entity, Guid invoiceId, InvoiceLineDto line, Guid resolvedId, int fallbackOrderIndex)
    {
        entity.Id = resolvedId;
        entity.InvoiceId = invoiceId;
        entity.ItemId = line.ItemId;
        entity.ItemNameOriginal = line.ItemNameOriginal;
        entity.SpecificationOriginal = line.SpecificationOriginal;
        entity.Unit = line.Unit;
        entity.Quantity = line.Quantity;
        entity.UnitPrice = line.UnitPrice;
        entity.LineAmount = line.LineAmount == 0 ? line.Quantity * line.UnitPrice : line.LineAmount;
        entity.Remark = line.Remark;
        entity.SerialNumber = line.SerialNumber;
        entity.MaterialNumber = line.MaterialNumber;
        entity.InstallLocation = line.InstallLocation;
        entity.RentalStartDate = line.RentalStartDate;
        entity.RentalEndDate = line.RentalEndDate;
        entity.OrderIndex = fallbackOrderIndex;
        entity.ItemTrackingType = ItemTrackingTypes.Normalize(line.ItemTrackingType);
        entity.IsDeleted = line.IsDeleted;
    }
}
