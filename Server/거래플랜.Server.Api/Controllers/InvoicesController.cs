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
using System.Text.Json;

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

    public InvoicesController(
        AppDbContext dbContext,
        ICurrentUserContext currentUserContext,
        IInvoiceNumberService invoiceNumberService,
        OfficeScopeService officeScopeService,
        InventoryLedgerService inventoryLedgerService,
        InvoiceStockSnapshotService invoiceStockSnapshotService)
    {
        _dbContext = dbContext;
        _currentUserContext = currentUserContext;
        _invoiceNumberService = invoiceNumberService;
        _officeScopeService = officeScopeService;
        _inventoryLedgerService = inventoryLedgerService;
        _invoiceStockSnapshotService = invoiceStockSnapshotService;
    }

    [HttpGet]
    public async Task<ActionResult<List<InvoiceDto>>> GetAll(
        [FromQuery] Guid? customerId,
        [FromQuery] string? q,
        [FromQuery] int take = 200,
        CancellationToken cancellationToken = default)
    {
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
                (x.Customer != null && x.Customer.NameOriginal.Contains(q)));
        }

        return Ok(await query.OrderByDescending(x => x.InvoiceDate)
            .Take(Math.Min(take, 500)).Select(x => x.ToDto()).ToListAsync(cancellationToken));
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
        return entity is null ? NotFound() : Ok(entity.ToDto());
    }

    [HttpPost]
    [Authorize(Policy = PermissionNames.InvoiceEdit)]
    public async Task<ActionResult<InvoiceDto>> Create([FromBody] InvoiceDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditInvoices())
            return Forbid();

        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
        if (customer is null)
            return BadRequest("Referenced customer was not found.");
        if (!_officeScopeService.CanReadOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode))
            return Forbid();

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
        if (await ValidateInvoiceLineItemScopeAsync(dto.Lines, cancellationToken) is { } lineScopeError)
            return lineScopeError;
        if (await ValidateLinkedRentalBillingProfileScopeAsync(dto, cancellationToken) is { } rentalProfileScopeError)
            return rentalProfileScopeError;

        var entity = new Invoice { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        entity.Apply(dto);
        if (string.IsNullOrWhiteSpace(entity.InvoiceNumber))
        {
            entity.InvoiceNumber = await _invoiceNumberService.GenerateAsync(entity.CustomerId, entity.InvoiceDate, cancellationToken);
        }

        ApplyInvoiceLines(entity, dto.Lines);
        var currentStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);

        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
            currentStockDeltas,
            cancellationToken);

        _dbContext.Invoices.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionNames.InvoiceEdit)]
    public async Task<ActionResult<InvoiceDto>> Update(Guid id, [FromBody] InvoiceDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditInvoices())
            return Forbid();

        var entity = await _dbContext.Invoices.Include(x => x.Customer).Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!_officeScopeService.CanWriteOfficeForInvoices(entity.ResponsibleOfficeCode, entity.TenantCode))
            return Forbid();
        if (OptimisticConcurrencyGuard.Check(this, entity, dto, nameof(Invoice)) is { } conflict)
            return conflict;

        var previousStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);
        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
        if (customer is null)
            return BadRequest("Referenced customer was not found.");
        if (!_officeScopeService.CanReadOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode))
            return Forbid();

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
        if (await ValidateInvoiceLineItemScopeAsync(dto.Lines, cancellationToken) is { } lineScopeError)
            return lineScopeError;
        if (await ValidateLinkedRentalBillingProfileScopeAsync(dto, cancellationToken) is { } rentalProfileScopeError)
            return rentalProfileScopeError;

        entity.Apply(dto);
        _dbContext.InvoiceLines.RemoveRange(entity.Lines);
        entity.Lines.Clear();
        ApplyInvoiceLines(entity, dto.Lines);
        var currentStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);

        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            previousStockDeltas,
            currentStockDeltas,
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionNames.InvoiceEdit)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] long? expectedRevision, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditInvoices())
            return Forbid();

        var entity = await _dbContext.Invoices.Include(x => x.Customer).Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!_officeScopeService.CanWriteOfficeForInvoices(entity.ResponsibleOfficeCode, entity.TenantCode))
            return Forbid();
        if (OptimisticConcurrencyGuard.Check(this, entity, expectedRevision, nameof(Invoice)) is { } conflict)
            return conflict;

        var rentalSettlementTargets = await LoadRentalSettlementTargetsForInvoiceDeleteAsync([id], cancellationToken);
        var previousStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);
        entity.IsDeleted = true;
        foreach (var line in entity.Lines)
        {
            line.IsDeleted = true;
        }
        await DetachTransactionsFromInvoicesAsync([id], cancellationToken);
        await MarkPaymentsDeletedForInvoicesAsync([id], cancellationToken);

        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            previousStockDeltas,
            new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateRentalSettlementsAsync(rentalSettlementTargets, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        return NoContent();
    }

    private async Task<List<(Guid ProfileId, Guid? RunId)>> LoadRentalSettlementTargetsForInvoiceDeleteAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return [];

        var invoiceTargets = await _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(invoice =>
                invoiceIds.Contains(invoice.Id) &&
                invoice.LinkedRentalBillingProfileId.HasValue &&
                invoice.LinkedRentalBillingProfileId.Value != Guid.Empty)
            .Select(invoice => new
            {
                ProfileId = invoice.LinkedRentalBillingProfileId!.Value,
                RunId = invoice.LinkedRentalBillingRunId
            })
            .ToListAsync(cancellationToken);

        var transactionTargets = await _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedInvoiceId.HasValue &&
                invoiceIds.Contains(transaction.LinkedInvoiceId.Value) &&
                transaction.LinkedRentalBillingProfileId.HasValue &&
                transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
            .Select(transaction => new
            {
                ProfileId = transaction.LinkedRentalBillingProfileId!.Value,
                RunId = transaction.LinkedRentalBillingRunId
            })
            .ToListAsync(cancellationToken);

        return invoiceTargets
            .Concat(transactionTargets)
            .Select(target => (target.ProfileId, target.RunId))
            .Where(target => target.ProfileId != Guid.Empty)
            .Distinct()
            .ToList();
    }

    private async Task DetachTransactionsFromInvoicesAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return;

        var transactions = await _dbContext.Transactions.IgnoreQueryFilters()
            .Where(transaction =>
                transaction.LinkedInvoiceId.HasValue &&
                invoiceIds.Contains(transaction.LinkedInvoiceId.Value))
            .ToListAsync(cancellationToken);

        foreach (var transaction in transactions)
        {
            transaction.LinkedInvoiceId = null;
            transaction.LinkedInvoiceNumber = string.Empty;
            transaction.SettlementAmount = 0m;
            if (string.Equals(transaction.TransactionKind, "전표수금", StringComparison.OrdinalIgnoreCase))
                transaction.TransactionKind = "일반수금";
            else if (string.Equals(transaction.TransactionKind, "전표지급", StringComparison.OrdinalIgnoreCase))
                transaction.TransactionKind = "일반지급";
        }
    }

    private async Task MarkPaymentsDeletedForInvoicesAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return;

        var payments = await _dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => invoiceIds.Contains(payment.InvoiceId))
            .ToListAsync(cancellationToken);
        foreach (var payment in payments)
        {
            payment.IsDeleted = true;
        }
    }

    private async Task RecalculateRentalSettlementsAsync(
        IEnumerable<(Guid ProfileId, Guid? RunId)> targets,
        CancellationToken cancellationToken)
    {
        var distinctTargets = targets
            .Where(target => target.ProfileId != Guid.Empty)
            .Distinct()
            .ToList();

        foreach (var target in distinctTargets)
        {
            await RecalculateRentalSettlementAsync(target.ProfileId, target.RunId, cancellationToken);
        }
    }

    private async Task RecalculateRentalSettlementAsync(
        Guid billingProfileId,
        Guid? billingRunId,
        CancellationToken cancellationToken)
    {
        var profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, cancellationToken);
        if (profile is null)
            return;

        var settledAmount = await GetRentalSettledAmountCoreAsync(billingProfileId, billingRunId, cancellationToken);
        var billedAmount = ResolveBillingRunAmount(profile, billingRunId);
        profile.SettledAmount = settledAmount;
        profile.OutstandingAmount = Math.Max(0m, billedAmount - settledAmount);
        profile.SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount);
        profile.CompletionStatus = profile.OutstandingAmount <= 0m ? "완료" : "미완료";

        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
        {
            var runs = DeserializeBillingRuns(profile.BillingRunsJson);
            var run = runs.FirstOrDefault(current => current.RunId == billingRunId.Value);
            if (run is not null)
            {
                run.BilledAmount = billedAmount;
                run.SettledAmount = settledAmount;
                run.SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount);
                run.Status = profile.OutstandingAmount <= 0m
                    ? "완료"
                    : string.Equals(run.Status, "보류", StringComparison.OrdinalIgnoreCase)
                        ? "보류"
                        : "청구중";
                run.SettledDate = settledAmount > 0m
                    ? await GetRentalLastSettledDateCoreAsync(billingProfileId, billingRunId, cancellationToken)
                    : null;
                if (profile.OutstandingAmount <= 0m)
                    profile.LastBilledDate = run.ScheduledDate;
                profile.BillingRunsJson = JsonSerializer.Serialize(runs, RentalBillingJsonOptions);
            }
        }

        if (profile.CompletionStatus == "완료")
        {
            profile.BillingStatus = "완료";
            profile.LastSettledDate = await GetRentalLastSettledDateCoreAsync(billingProfileId, billingRunId, cancellationToken);
        }
        else if (!string.Equals(profile.BillingStatus, "보류", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(profile.BillingStatus, "취소", StringComparison.OrdinalIgnoreCase))
        {
            profile.BillingStatus = "청구중";
            profile.LastSettledDate = settledAmount > 0m
                ? await GetRentalLastSettledDateCoreAsync(billingProfileId, billingRunId, cancellationToken)
                : null;
        }
    }

    private async Task<decimal> GetRentalSettledAmountCoreAsync(
        Guid billingProfileId,
        Guid? billingRunId,
        CancellationToken cancellationToken)
    {
        var transactionQuery = _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(transaction => !transaction.IsDeleted && transaction.LinkedRentalBillingProfileId == billingProfileId);
        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
            transactionQuery = transactionQuery.Where(transaction => transaction.LinkedRentalBillingRunId == billingRunId.Value);

        var transactionSettledAmount = (await transactionQuery
            .Select(transaction => transaction.SettlementAmount)
            .ToListAsync(cancellationToken)).Sum();

        var directPaymentQuery =
            from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking()
            join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
                on payment.InvoiceId equals invoice.Id
            where !payment.IsDeleted &&
                  !invoice.IsDeleted &&
                  invoice.LinkedRentalBillingProfileId == billingProfileId &&
                  !_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().Any(transaction =>
                      !transaction.IsDeleted &&
                      transaction.Id == payment.Id &&
                      transaction.LinkedRentalBillingProfileId == billingProfileId)
            select new
            {
                payment.Amount,
                invoice.LinkedRentalBillingRunId
            };
        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
            directPaymentQuery = directPaymentQuery.Where(row => row.LinkedRentalBillingRunId == billingRunId.Value);

        var directPaymentSettledAmount = (await directPaymentQuery
            .Select(row => row.Amount)
            .ToListAsync(cancellationToken)).Sum();

        return transactionSettledAmount + directPaymentSettledAmount;
    }

    private async Task<DateOnly?> GetRentalLastSettledDateCoreAsync(
        Guid billingProfileId,
        Guid? billingRunId,
        CancellationToken cancellationToken)
    {
        var transactionQuery = _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(transaction => !transaction.IsDeleted && transaction.LinkedRentalBillingProfileId == billingProfileId);
        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
            transactionQuery = transactionQuery.Where(transaction => transaction.LinkedRentalBillingRunId == billingRunId.Value);

        var transactionDates = await transactionQuery
            .Select(transaction => transaction.TransactionDate)
            .ToListAsync(cancellationToken);

        var directPaymentQuery =
            from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking()
            join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
                on payment.InvoiceId equals invoice.Id
            where !payment.IsDeleted &&
                  !invoice.IsDeleted &&
                  invoice.LinkedRentalBillingProfileId == billingProfileId &&
                  !_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().Any(transaction =>
                      !transaction.IsDeleted &&
                      transaction.Id == payment.Id &&
                      transaction.LinkedRentalBillingProfileId == billingProfileId)
            select new
            {
                payment.PaymentDate,
                invoice.LinkedRentalBillingRunId
            };
        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
            directPaymentQuery = directPaymentQuery.Where(row => row.LinkedRentalBillingRunId == billingRunId.Value);

        var directPaymentDates = await directPaymentQuery
            .Select(row => row.PaymentDate)
            .ToListAsync(cancellationToken);

        return transactionDates
            .Concat(directPaymentDates)
            .OrderByDescending(date => date)
            .Cast<DateOnly?>()
            .FirstOrDefault();
    }

    private static decimal ResolveBillingRunAmount(RentalBillingProfile profile, Guid? billingRunId)
    {
        if (!billingRunId.HasValue || billingRunId.Value == Guid.Empty)
            return Math.Max(0m, profile.MonthlyAmount);

        var run = DeserializeBillingRuns(profile.BillingRunsJson)
            .FirstOrDefault(current => current.RunId == billingRunId.Value);
        return run is null ? Math.Max(0m, profile.MonthlyAmount) : Math.Max(0m, run.BilledAmount);
    }

    private static List<RentalBillingRunSnapshot> DeserializeBillingRuns(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<RentalBillingRunSnapshot>>(json, RentalBillingJsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string DetermineRentalSettlementStatus(string? billingMethod, decimal settledAmount, decimal billedAmount)
    {
        if (settledAmount <= 0m)
            return GetPendingSettlementStatus(billingMethod);
        if (settledAmount < billedAmount)
            return "부분입금";
        return GetDisplaySettlementCompleteStatus(billingMethod);
    }

    private static string GetPendingSettlementStatus(string? billingMethod)
        => (billingMethod ?? string.Empty).Trim() switch
        {
            "카드" => "카드결제대기",
            "CMS" => "CMS대기",
            _ => "확인대기"
        };

    private static string GetDisplaySettlementCompleteStatus(string? billingMethod)
        => (billingMethod ?? string.Empty).Trim() switch
        {
            "카드" => "카드승인완료",
            _ => "입금확인"
        };

    private static readonly JsonSerializerOptions RentalBillingJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class RentalBillingRunSnapshot
    {
        public Guid RunId { get; set; }
        public string RunKey { get; set; } = string.Empty;
        public DateOnly ScheduledDate { get; set; }
        public DateOnly PeriodStartDate { get; set; }
        public DateOnly PeriodEndDate { get; set; }
        public string PeriodLabel { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal BilledAmount { get; set; }
        public decimal SettledAmount { get; set; }
        public string SettlementStatus { get; set; } = string.Empty;
        public DateOnly? SettledDate { get; set; }
    }

    private async Task<ActionResult?> ValidateInvoiceLineItemScopeAsync(
        IEnumerable<InvoiceLineDto>? lines,
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
                continue;

            if (!_officeScopeService.CanReadOfficeForItems(item.OfficeCode, item.TenantCode))
                return Forbid();
        }

        return null;
    }

    private async Task<ActionResult?> ValidateLinkedRentalBillingProfileScopeAsync(
        InvoiceDto dto,
        CancellationToken cancellationToken)
    {
        if (!dto.LinkedRentalBillingProfileId.HasValue || dto.LinkedRentalBillingProfileId.Value == Guid.Empty)
            return null;

        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id == dto.LinkedRentalBillingProfileId.Value)
            .Select(current => new
            {
                current.IsDeleted,
                current.ResponsibleOfficeCode,
                current.TenantCode
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (profile is null || profile.IsDeleted)
            return BadRequest("Referenced rental billing profile was not found.");

        if (!_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode))
            return Forbid();

        return null;
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
        entity.OrderIndex = line.OrderIndex > 0 ? line.OrderIndex : fallbackOrderIndex;
        entity.ItemTrackingType = ItemTrackingTypes.Normalize(line.ItemTrackingType);
        entity.IsDeleted = line.IsDeleted;
    }
}
