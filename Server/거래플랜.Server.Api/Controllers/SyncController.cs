using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
[Route("sync")]
public sealed class SyncController : ControllerBase
{
    private const long MaxContractFileSizeBytes = 15L * 1024 * 1024;
    private const string AmbiguousIncomingMutationIdConflictReason =
        "Mutation id is duplicated: Mutation id is reused by conflicting rows within the same Push.";
    private static readonly JsonSerializerOptions ConflictJsonOptions = new() { WriteIndented = false };
    private static readonly TimeZoneInfo KoreaTimeZone = ResolveKoreaTimeZone();
    private static readonly SemaphoreSlim RentalAssetSyncLock = new(1, 1);
    private static readonly HashSet<string> RentalBillingRunCorePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "RunId",
        "RunKey",
        "ScheduledDate",
        "PeriodStartDate",
        "PeriodEndDate",
        "CycleMonths",
        "PeriodLabel",
        "Status",
        "BilledAmount",
        "SettledAmount",
        "SettlementStatus",
        "SettledDate",
        RentalBillingRunTombstonePolicy.IsTombstonedPropertyName,
        RentalBillingRunTombstonePolicy.TombstonedAtUtcPropertyName,
        RentalBillingRunTombstonePolicy.TombstonedByUsernamePropertyName,
        "Note",
        "Items"
    };
    private static readonly HashSet<string> RentalBillingRunItemKnownPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ItemId",
        "CatalogItemId",
        "DisplayItemName",
        "BillingLineMode",
        "IndividualGroupingMode",
        "Specification",
        "Unit",
        "MaterialNumber",
        "RepresentativeAssetId",
        "Quantity",
        "UnitPrice",
        "Amount",
        "Note",
        "IncludedAssetIds"
    };
    private readonly Dictionary<string, string> _incomingMutationPayloadHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProcessedSyncMutation> _processedMutationsById = new(StringComparer.Ordinal);

    private readonly AppDbContext _dbContext;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IInvoiceNumberService _invoiceNumberService;
    private readonly OfficeScopeService _officeScopeService;
    private readonly ICentralFileStorage _fileStorage;
    private readonly IStoredFileReferenceReconciler _storedFileReferenceReconciler;
    private readonly InventoryLedgerService _inventoryLedgerService;
    private readonly InvoiceStockSnapshotService _invoiceStockSnapshotService;
    private readonly RentalAssignmentHistoryService _rentalAssignmentHistoryService;
    private readonly RentalSettlementRecalculationService _rentalSettlementRecalculationService;

    public SyncController(
        AppDbContext dbContext,
        ICurrentUserContext currentUserContext,
        IInvoiceNumberService invoiceNumberService,
        OfficeScopeService officeScopeService,
        ICentralFileStorage fileStorage,
        RevisionClock revisionClock,
        InventoryLedgerService inventoryLedgerService,
        InvoiceStockSnapshotService invoiceStockSnapshotService,
        RentalAssignmentHistoryService rentalAssignmentHistoryService,
        RentalSettlementRecalculationService rentalSettlementRecalculationService)
        : this(
            dbContext,
            currentUserContext,
            invoiceNumberService,
            officeScopeService,
            fileStorage,
            PreserveAllStoredFileReferenceReconciler.Instance,
            revisionClock,
            inventoryLedgerService,
            invoiceStockSnapshotService,
            rentalAssignmentHistoryService,
            rentalSettlementRecalculationService)
    {
    }

    [ActivatorUtilitiesConstructor]
    public SyncController(
        AppDbContext dbContext,
        ICurrentUserContext currentUserContext,
        IInvoiceNumberService invoiceNumberService,
        OfficeScopeService officeScopeService,
        ICentralFileStorage fileStorage,
        IStoredFileReferenceReconciler storedFileReferenceReconciler,
        RevisionClock revisionClock,
        InventoryLedgerService inventoryLedgerService,
        InvoiceStockSnapshotService invoiceStockSnapshotService,
        RentalAssignmentHistoryService rentalAssignmentHistoryService,
        RentalSettlementRecalculationService rentalSettlementRecalculationService)
    {
        _dbContext = dbContext;
        _currentUserContext = currentUserContext;
        _invoiceNumberService = invoiceNumberService;
        _officeScopeService = officeScopeService;
        _fileStorage = fileStorage;
        _storedFileReferenceReconciler = storedFileReferenceReconciler;
        ArgumentNullException.ThrowIfNull(revisionClock);
        _inventoryLedgerService = inventoryLedgerService;
        _invoiceStockSnapshotService = invoiceStockSnapshotService;
        _rentalAssignmentHistoryService = rentalAssignmentHistoryService;
        _rentalSettlementRecalculationService = rentalSettlementRecalculationService;
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(SyncStatusDto), StatusCodes.Status200OK)]
    public ActionResult<SyncStatusDto> GetStatus(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Ok(new SyncStatusDto
        {
            CurrentServerRevision = _dbContext.GetCommittedRevision(),
            ServerUtc = DateTime.UtcNow
        });
    }

    [HttpGet("wait")]
    [ProducesResponseType(typeof(SyncStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SyncStatusDto>> WaitForChange(
        [FromQuery] long sinceRev,
        [FromQuery] int timeoutSeconds = 25,
        CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 30));
        var startedAtUtc = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            var currentRevision = await GetCurrentRevisionAsync(cancellationToken);
            if (currentRevision > sinceRev || DateTime.UtcNow - startedAtUtc >= timeout)
            {
                return Ok(new SyncStatusDto
                {
                    CurrentServerRevision = currentRevision,
                    ServerUtc = DateTime.UtcNow
                });
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return Ok(new SyncStatusDto
        {
            CurrentServerRevision = await GetCurrentRevisionAsync(cancellationToken),
            ServerUtc = DateTime.UtcNow
        });
    }

    [HttpGet("pull")]
    [ProducesResponseType(typeof(SyncPullResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SyncPullResponse>> Pull(
        [FromQuery] long sinceRev,
        CancellationToken cancellationToken,
        [FromQuery] bool rentalAdministrationOnly = false)
    {
        await using var readSnapshot =
            await _dbContext.BeginConsistentReadSnapshotAsync(cancellationToken);
        var upperRevision = await GetCurrentRevisionAsync(cancellationToken);
        var readableRentalAssets = _officeScopeService
            .ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking());
        var readableRentalAssetIds = await readableRentalAssets
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);
        var readableRentalAssignmentHistories = _officeScopeService
            .ApplyRentalAssignmentHistoryScope(_dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking());
        var rentalAdministrationItemIds = rentalAdministrationOnly
            ? await LoadRentalAdministrationItemIdsAsync(readableRentalAssets, cancellationToken)
            : [];

        var response = new SyncPullResponse
        {
            ItemCatalogExtensionVersion = 1,
            CompanyProfiles = rentalAdministrationOnly
                ? []
                : await _officeScopeService.ApplyCompanyProfileScope(_dbContext.CompanyProfiles.IgnoreQueryFilters().AsNoTracking())
                    .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Units = rentalAdministrationOnly
                ? []
                : DeduplicatePulledUnits(await _dbContext.Units.IgnoreQueryFilters().AsNoTracking()
                    .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).Select(x => x.ToDto()).ToListAsync(cancellationToken)),
            CustomerCategories = rentalAdministrationOnly
                ? []
                : await _dbContext.CustomerCategories.IgnoreQueryFilters().AsNoTracking()
                    .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            PriceGradeOptions = rentalAdministrationOnly
                ? []
                : await _dbContext.PriceGradeOptions.IgnoreQueryFilters().AsNoTracking()
                    .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            TradeTypeOptions = rentalAdministrationOnly
                ? []
                : await _dbContext.TradeTypeOptions.IgnoreQueryFilters().AsNoTracking()
                    .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            ItemCategoryOptions = rentalAdministrationOnly
                ? []
                : await _dbContext.ItemCategoryOptions.IgnoreQueryFilters().AsNoTracking()
                    .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            CustomerMasters = rentalAdministrationOnly
                ? []
                : await _officeScopeService.ApplyCustomerMasterScope(_dbContext.CustomerMasters.IgnoreQueryFilters().AsNoTracking())
                    .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Customers = await _officeScopeService.ApplySyncCustomerScope(_dbContext.Customers.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            CustomerContracts = rentalAdministrationOnly
                ? []
                : await _officeScopeService.ApplyCustomerContractScope(_dbContext.CustomerContracts.IgnoreQueryFilters().AsNoTracking().Include(x => x.Customer))
                    .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).Select(x => x.ToDto(false)).ToListAsync(cancellationToken),
            Items = await _officeScopeService.ApplySyncItemScope(_dbContext.Items.IgnoreQueryFilters().AsNoTracking())
                .Where(x => rentalAdministrationOnly
                    ? rentalAdministrationItemIds.Contains(x.Id)
                    : x.Revision > sinceRev && x.Revision <= upperRevision)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            ItemPriceGrades = rentalAdministrationOnly
                ? []
                : await _officeScopeService.ApplyItemPriceGradeScope(_dbContext.ItemPriceGrades.IgnoreQueryFilters().AsNoTracking().Include(x => x.Item))
                    .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).OrderBy(x => x.ItemId).ThenBy(x => x.PriceGradeName)
                    .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            ItemWarehouseStocks = rentalAdministrationOnly
                ? []
                : await _officeScopeService.ApplyItemWarehouseStockScope(_dbContext.ItemWarehouseStocks.AsNoTracking())
                    .OrderBy(x => x.ItemId).ThenBy(x => x.WarehouseCode)
                    .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Transactions = await _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision &&
                    (!rentalAdministrationOnly ||
                     sinceRev > 0 ||
                     x.LinkedRentalBillingProfileId.HasValue ||
                     x.LinkedRentalBillingRunId.HasValue))
                .OrderBy(x => x.TransactionDate).ThenBy(x => x.CreatedAtUtc)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            TransactionAttachments = rentalAdministrationOnly
                ? []
                : await _officeScopeService.ApplyTransactionAttachmentScope(_dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking().Include(x => x.Transaction))
                    .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).OrderBy(x => x.UploadedAtUtc).ThenBy(x => x.SortOrder)
                    .Select(x => x.ToDto(true)).ToListAsync(cancellationToken),
            InventoryTransfers = rentalAdministrationOnly
                ? []
                : await _officeScopeService.ApplyInventoryTransferScope(_dbContext.InventoryTransfers.IgnoreQueryFilters().AsNoTracking().Include(x => x.Lines))
                    .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).OrderBy(x => x.TransferDate).ThenBy(x => x.CreatedAtUtc)
                    .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            RentalManagementCompanies = await _officeScopeService.ApplyRentalManagementCompanyScope(_dbContext.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).OrderBy(x => x.Code).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            RentalBillingProfiles = await _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).OrderBy(x => x.CustomerName).ThenBy(x => x.ProfileKey)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            RentalAssets = await _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).OrderBy(x => x.CustomerName).ThenBy(x => x.AssetKey)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            RentalAssetAssignmentHistories = readableRentalAssetIds.Count == 0
                ? []
                : await readableRentalAssignmentHistories
                    .Where(history => history.Revision > sinceRev &&
                                      history.Revision <= upperRevision &&
                                      readableRentalAssetIds.Contains(history.AssetId))
                    .OrderByDescending(history => history.IsCurrent)
                    .ThenByDescending(history => history.LinkedAtUtc)
                    .Select(history => history.ToDto())
                    .ToListAsync(cancellationToken),
            RentalBillingLogs = await _officeScopeService.ApplyRentalBillingLogScope(_dbContext.RentalBillingLogs.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision).OrderBy(x => x.ScheduledDate).ThenBy(x => x.BillingYearMonth)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Invoices = await _officeScopeService.ApplySyncInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().Include(x => x.Customer).Include(x => x.Lines).Include(x => x.Payments).AsNoTracking())
                .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision &&
                    (!rentalAdministrationOnly ||
                     sinceRev > 0 ||
                     x.LinkedRentalBillingProfileId.HasValue ||
                     x.LinkedRentalBillingRunId.HasValue))
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Payments = await _officeScopeService.ApplyPaymentScope(_dbContext.Payments.IgnoreQueryFilters().Include(x => x.Invoice).ThenInclude(invoice => invoice!.Customer).AsNoTracking())
                .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision &&
                    (!rentalAdministrationOnly ||
                     sinceRev > 0 ||
                     (x.Invoice != null &&
                      (x.Invoice.LinkedRentalBillingProfileId.HasValue || x.Invoice.LinkedRentalBillingRunId.HasValue))))
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            PurgeRecords = (await FilterSupersededPurgeRecordsAsync(
                    (await _dbContext.RecycleBinPurgeRecords
                        .AsNoTracking()
                        .Where(x => x.Revision > sinceRev && x.Revision <= upperRevision)
                        .OrderBy(x => x.Revision)
                        .ToListAsync(cancellationToken))
                    .Where(CanReadPurgeRecord)
                    .ToList(),
                    cancellationToken))
                .Select(x => x.ToDto())
                .ToList()
        };

        await RemoveUnreadableItemLinesFromPullResponseAsync(response, cancellationToken);
        await RemoveUnreadableInvoicePaymentLinksFromPullResponseAsync(response, cancellationToken);
        await RemoveUnreadableRentalSettlementLinksFromPullResponseAsync(response, cancellationToken);

        response.CurrentServerRevision = upperRevision;
        await readSnapshot.CommitAsync(cancellationToken);
        return Ok(response);
    }

    private async Task<List<Guid>> LoadRentalAdministrationItemIdsAsync(
        IQueryable<RentalAsset> readableRentalAssets,
        CancellationToken cancellationToken)
    {
        var rentalAssetItemIds = await readableRentalAssets
            .Where(asset => asset.ItemId.HasValue && asset.ItemId.Value != Guid.Empty)
            .Select(asset => asset.ItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var invoiceItemIds = await _officeScopeService
            .ApplySyncInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
            .Where(invoice => invoice.LinkedRentalBillingProfileId.HasValue || invoice.LinkedRentalBillingRunId.HasValue)
            .SelectMany(invoice => invoice.Lines)
            .Where(line => line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
            .Select(line => line.ItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        return rentalAssetItemIds
            .Concat(invoiceItemIds)
            .Distinct()
            .ToList();
    }

    private async Task RemoveUnreadableItemLinesFromPullResponseAsync(
        SyncPullResponse response,
        CancellationToken cancellationToken)
    {
        var referencedItemIds = response.Invoices
            .SelectMany(invoice => invoice.Lines ?? [])
            .Select(line => line.ItemId)
            .Concat(response.InventoryTransfers
                .SelectMany(transfer => transfer.Lines ?? [])
                .Select(line => line.ItemId))
            .Where(itemId => itemId.HasValue && itemId.Value != Guid.Empty)
            .Select(itemId => itemId!.Value)
            .Distinct()
            .ToList();

        if (referencedItemIds.Count == 0)
            return;

        var referencedItems = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => referencedItemIds.Contains(item.Id) && !item.IsDeleted)
            .Select(item => new { item.Id, item.OfficeCode, item.TenantCode })
            .ToListAsync(cancellationToken);
        var readableItemIds = referencedItems
            .Where(item => _officeScopeService.CanReadOfficeForItems(item.OfficeCode, item.TenantCode))
            .Select(item => item.Id)
            .ToHashSet();

        foreach (var invoice in response.Invoices)
            invoice.Lines = FilterReadableInvoiceLines(invoice.Lines, readableItemIds);

        foreach (var transfer in response.InventoryTransfers)
            transfer.Lines = FilterReadableInventoryTransferLines(transfer.Lines, readableItemIds);
    }

    private static List<InvoiceLineDto> FilterReadableInvoiceLines(
        List<InvoiceLineDto>? lines,
        IReadOnlySet<Guid> readableItemIds)
    {
        if (lines is null || lines.Count == 0)
            return [];

        return lines
            .Where(line => !line.ItemId.HasValue ||
                           line.ItemId.Value == Guid.Empty ||
                           readableItemIds.Contains(line.ItemId.Value))
            .ToList();
    }

    private static List<InventoryTransferLineDto> FilterReadableInventoryTransferLines(
        List<InventoryTransferLineDto>? lines,
        IReadOnlySet<Guid> readableItemIds)
    {
        if (lines is null || lines.Count == 0)
            return [];

        return lines
            .Where(line => !line.ItemId.HasValue ||
                           line.ItemId.Value == Guid.Empty ||
                           readableItemIds.Contains(line.ItemId.Value))
            .ToList();
    }

    private async Task RemoveUnreadableInvoicePaymentLinksFromPullResponseAsync(
        SyncPullResponse response,
        CancellationToken cancellationToken)
    {
        var referencedInvoiceIds = response.Payments
            .Select(payment => payment.InvoiceId)
            .Concat(response.Invoices
                .SelectMany(invoice => invoice.Payments ?? [])
                .Select(payment => payment.InvoiceId))
            .Concat(response.Transactions
                .Where(transaction => transaction.LinkedInvoiceId.HasValue)
                .Select(transaction => transaction.LinkedInvoiceId!.Value))
            .Where(invoiceId => invoiceId != Guid.Empty)
            .Distinct()
            .ToList();

        var readableInvoiceIds = referencedInvoiceIds.Count == 0
            ? []
            : (await _officeScopeService.ApplySyncInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
                .Where(invoice => referencedInvoiceIds.Contains(invoice.Id))
                .Select(invoice => invoice.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        if (response.Payments.Count > 0)
        {
            response.Payments = response.Payments
                .Where(payment => payment.InvoiceId != Guid.Empty && readableInvoiceIds.Contains(payment.InvoiceId))
                .ToList();
        }

        foreach (var transaction in response.Transactions)
        {
            if (!transaction.LinkedInvoiceId.HasValue ||
                transaction.LinkedInvoiceId.Value == Guid.Empty ||
                readableInvoiceIds.Contains(transaction.LinkedInvoiceId.Value))
            {
                continue;
            }

            transaction.LinkedInvoiceId = null;
            transaction.LinkedInvoiceNumber = string.Empty;
            transaction.LinkedRentalBillingProfileId = null;
            transaction.LinkedRentalBillingRunId = null;
        }

        var nestedPaymentIds = response.Invoices
            .SelectMany(invoice => invoice.Payments ?? [])
            .Where(payment => payment.Id != Guid.Empty)
            .Select(payment => payment.Id)
            .Distinct()
            .ToList();

        if (nestedPaymentIds.Count == 0)
            return;

        var readablePaymentIds = (await _officeScopeService
                .ApplyPaymentScope(_dbContext.Payments.IgnoreQueryFilters().AsNoTracking())
                .Where(payment => nestedPaymentIds.Contains(payment.Id))
                .Select(payment => payment.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var invoice in response.Invoices)
            invoice.Payments = FilterReadableInvoicePayments(invoice.Payments, readablePaymentIds);
    }

    private static List<PaymentDto> FilterReadableInvoicePayments(
        List<PaymentDto>? payments,
        IReadOnlySet<Guid> readablePaymentIds)
    {
        if (payments is null || payments.Count == 0)
            return [];

        return payments
            .Where(payment => payment.Id != Guid.Empty && readablePaymentIds.Contains(payment.Id))
            .ToList();
    }

    private async Task RemoveUnreadableRentalSettlementLinksFromPullResponseAsync(
        SyncPullResponse response,
        CancellationToken cancellationToken)
    {
        var referencedProfileIds = response.Invoices
            .Select(invoice => invoice.LinkedRentalBillingProfileId)
            .Concat(response.Transactions.Select(transaction => transaction.LinkedRentalBillingProfileId))
            .Concat(response.RentalAssets.Select(asset => asset.BillingProfileId))
            .Concat(response.RentalAssets.Select(asset => asset.LastBillingProfileId))
            .Concat(response.RentalAssetAssignmentHistories.Select(history => history.BillingProfileId))
            .Concat(response.RentalBillingLogs.Select(log => (Guid?)log.BillingProfileId))
            .Where(profileId => profileId.HasValue && profileId.Value != Guid.Empty)
            .Select(profileId => profileId!.Value)
            .Distinct()
            .ToList();

        if (referencedProfileIds.Count == 0)
            return;

        var profiles = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => referencedProfileIds.Contains(profile.Id) && !profile.IsDeleted)
            .Select(profile => new
            {
                profile.Id,
                profile.ResponsibleOfficeCode,
                profile.TenantCode,
                profile.OfficeCode
            })
            .ToListAsync(cancellationToken);

        var readableProfileIds = profiles
            .Where(profile => _officeScopeService.CanReadOfficeForRentals(
                profile.ResponsibleOfficeCode,
                profile.TenantCode,
                profile.OfficeCode))
            .Select(profile => profile.Id)
            .ToHashSet();

        bool CanReadProfile(Guid? profileId)
            => !profileId.HasValue ||
               profileId.Value == Guid.Empty ||
               readableProfileIds.Contains(profileId.Value);

        foreach (var invoice in response.Invoices)
        {
            if (CanReadProfile(invoice.LinkedRentalBillingProfileId))
                continue;

            invoice.LinkedRentalBillingProfileId = null;
            invoice.LinkedRentalBillingRunId = null;
        }

        foreach (var transaction in response.Transactions)
        {
            if (CanReadProfile(transaction.LinkedRentalBillingProfileId))
                continue;

            transaction.LinkedRentalBillingProfileId = null;
            transaction.LinkedRentalBillingRunId = null;
        }

        foreach (var asset in response.RentalAssets)
        {
            if (!CanReadProfile(asset.BillingProfileId))
                asset.BillingProfileId = null;

            if (!CanReadProfile(asset.LastBillingProfileId))
            {
                asset.LastBillingProfileId = null;
                asset.LastBillingProfileDisplay = string.Empty;
            }
        }

        foreach (var history in response.RentalAssetAssignmentHistories)
        {
            if (CanReadProfile(history.BillingProfileId))
                continue;

            history.BillingProfileId = null;
            history.BillingProfileDisplay = string.Empty;
        }

        response.RentalBillingLogs = response.RentalBillingLogs
            .Where(log => log.BillingProfileId != Guid.Empty && readableProfileIds.Contains(log.BillingProfileId))
            .ToList();
    }

    private static List<UnitDto> DeduplicatePulledUnits(List<UnitDto> units)
    {
        if (units.Count == 0)
            return units;

        var latestById = units
            .GroupBy(unit => unit.Id)
            .Select(group => group
                .OrderByDescending(unit => unit.Revision)
                .ThenByDescending(unit => unit.UpdatedAtUtc)
                .ThenByDescending(unit => unit.CreatedAtUtc)
                .ThenBy(unit => unit.Id)
                .First())
            .ToList();

        var canonicalActiveIds = latestById
            .Where(unit => !unit.IsDeleted && unit.IsActive)
            .GroupBy(unit => UnitCatalogNormalizer.Normalize(unit.Name), StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group
                .OrderByDescending(unit => string.Equals(unit.Name, group.Key, StringComparison.Ordinal))
                .ThenByDescending(unit => unit.Revision)
                .ThenByDescending(unit => unit.UpdatedAtUtc)
                .ThenByDescending(unit => unit.CreatedAtUtc)
                .ThenBy(unit => unit.Id)
                .First()
                .Id)
            .ToHashSet();

        return latestById
            .Where(unit => unit.IsDeleted || !unit.IsActive || canonicalActiveIds.Contains(unit.Id))
            .ToList();
    }

    private string? ValidatePushPermissions(SyncPushRequest request)
    {
        var denied = new List<string>();

        static bool HasAny<T>(IReadOnlyCollection<T>? values) => values is { Count: > 0 };

        void Require(bool hasChanges, string permission, string label)
        {
            if (hasChanges && !HasPermission(permission))
                denied.Add(label);
        }

        void RequireAny(bool hasChanges, string label, params string[] permissions)
        {
            if (hasChanges && !permissions.Any(HasPermission))
                denied.Add(label);
        }

        Require(HasAny(request.CompanyProfiles), PermissionNames.CompanyProfileEdit, "회사설정");
        Require(
            HasAny(request.Units) ||
            HasAny(request.CustomerCategories) ||
            HasAny(request.PriceGradeOptions) ||
            HasAny(request.TradeTypeOptions) ||
            HasAny(request.ItemCategoryOptions),
            PermissionNames.SettingsEdit,
            "환경설정/분류");
        Require(
            HasAny(request.CustomerMasters) ||
            HasAny(request.Customers) ||
            HasAny(request.CustomerContracts),
            PermissionNames.CustomerEdit,
            "거래처");
        Require(
            HasAny(request.Items) ||
            HasAny(request.ItemPriceGrades) ||
            HasAny(request.ItemWarehouseStocks) ||
            HasAny(request.ItemWarehouseStockSnapshotMarkers),
            PermissionNames.ItemEdit,
            "품목/재고");
        Require(HasAny(request.Invoices), PermissionNames.InvoiceEdit, "전표");
        Require(
            HasAny(request.Transactions) ||
            HasAny(request.TransactionAttachments) ||
            HasAny(request.Payments),
            PermissionNames.PaymentEdit,
            "수금/지급");
        Require(HasAny(request.InventoryTransfers), PermissionNames.DeliveryEdit, "납품/재고이동");
        Require(HasAny(request.RentalManagementCompanies), PermissionNames.RentalSettingsEdit, "렌탈 관리업체");
        RequireAny(
            HasAny(request.RentalBillingProfiles) ||
            HasAny(request.RentalBillingLogs),
            "렌탈 청구",
            PermissionNames.RentalProfileEdit,
            PermissionNames.RentalEditAll);
        RequireAny(
            HasAny(request.RentalAssets) ||
            HasAny(request.RentalAssetAssignmentHistories),
            "렌탈 자산",
            PermissionNames.RentalAssetEdit,
            PermissionNames.RentalEditAll);

        if (denied.Count == 0)
            return null;

        return $"현재 계정 권한으로 서버 동기화 반영이 허용되지 않는 변경이 포함되어 있습니다: {string.Join(", ", denied.Distinct(StringComparer.OrdinalIgnoreCase))}";
    }

    private bool HasPermission(string permission)
        => _currentUserContext.HasPermission(permission);

    private static void NormalizePushRequest(SyncPushRequest request)
    {
        request.CompanyProfiles = RemoveNullEntries(request.CompanyProfiles);
        request.Units = RemoveNullEntries(request.Units);
        request.CustomerCategories = RemoveNullEntries(request.CustomerCategories);
        request.PriceGradeOptions = RemoveNullEntries(request.PriceGradeOptions);
        request.TradeTypeOptions = RemoveNullEntries(request.TradeTypeOptions);
        request.ItemCategoryOptions = RemoveNullEntries(request.ItemCategoryOptions);
        request.CustomerMasters = RemoveNullEntries(request.CustomerMasters);
        request.Customers = RemoveNullEntries(request.Customers);
        request.CustomerContracts = RemoveNullEntries(request.CustomerContracts);
        request.Items = RemoveNullEntries(request.Items);
        request.ItemPriceGrades = RemoveNullEntries(request.ItemPriceGrades);
        request.ItemWarehouseStocks = RemoveNullEntries(request.ItemWarehouseStocks);
        request.ItemWarehouseStockSnapshotMarkers = RemoveNullEntries(request.ItemWarehouseStockSnapshotMarkers);
        request.Transactions = RemoveNullEntries(request.Transactions);
        request.TransactionAttachments = RemoveNullEntries(request.TransactionAttachments);
        request.InventoryTransfers = RemoveNullEntries(request.InventoryTransfers);
        request.RentalManagementCompanies = RemoveNullEntries(request.RentalManagementCompanies);
        request.RentalBillingProfiles = RemoveNullEntries(request.RentalBillingProfiles);
        request.RentalAssets = RemoveNullEntries(request.RentalAssets);
        request.RentalAssetAssignmentHistories = RemoveNullEntries(request.RentalAssetAssignmentHistories);
        request.RentalBillingLogs = RemoveNullEntries(request.RentalBillingLogs);
        request.Invoices = RemoveNullEntries(request.Invoices);
        request.Payments = RemoveNullEntries(request.Payments);

        foreach (var invoice in request.Invoices)
        {
            invoice.Lines = RemoveNullEntries(invoice.Lines);
            invoice.Payments = RemoveNullEntries(invoice.Payments);
        }

        foreach (var transfer in request.InventoryTransfers)
            transfer.Lines = RemoveNullEntries(transfer.Lines);
    }

    private static List<T> RemoveNullEntries<T>(List<T>? payload)
        where T : class
    {
        if (payload is null)
            return [];

        payload.RemoveAll(static dto => dto is null);
        return payload;
    }

    private async Task InitializeProcessedMutationCacheAsync(
        SyncPushRequest request,
        CancellationToken cancellationToken)
    {
        _incomingMutationPayloadHashes.Clear();
        _processedMutationsById.Clear();

        var incomingMutationDtos = EnumeratePushMutationDtos(request).ToList();
        foreach (var dto in incomingMutationDtos)
        {
            var mutationId = NormalizeMutationId(dto.MutationId);
            if (!string.IsNullOrWhiteSpace(mutationId))
            {
                _incomingMutationPayloadHashes.TryAdd(
                    mutationId,
                    SyncMutationPayloadHasher.Compute(dto));
            }
        }

        var requestedMutationIds = incomingMutationDtos
            .Select(dto => NormalizeMutationId(dto.MutationId))
            .Where(mutationId => !string.IsNullOrWhiteSpace(mutationId))
            .ToHashSet(StringComparer.Ordinal);
        await LoadProcessedMutationCacheEntriesAsync(
            requestedMutationIds,
            cancellationToken);
    }

    private static HashSet<string> FindAmbiguousIncomingMutationIds(
        SyncPushRequest request)
    {
        var ambiguousMutationIds =
            new HashSet<string>(StringComparer.Ordinal);
        var duplicateMutationGroups =
            EnumeratePushMutationDtos(request)
            .Select(dto => new
            {
                MutationId = NormalizeMutationId(dto.MutationId),
                Dto = dto
            })
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.MutationId))
            .GroupBy(
                entry => entry.MutationId,
                StringComparer.Ordinal)
            .Where(group =>
                group.Skip(1).Any());
        foreach (var group in duplicateMutationGroups)
        {
            var distinctSignatures = group
                .Select(entry => string.Join(
                    "|",
                    entry.Dto.GetType().FullName ??
                    entry.Dto.GetType().Name,
                    entry.Dto.Id.ToString("D"),
                    entry.Dto.ExpectedRevision.ToString(
                        CultureInfo.InvariantCulture),
                    SyncMutationPayloadHasher.Compute(
                        entry.Dto)))
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .Count();
            if (distinctSignatures > 1)
                ambiguousMutationIds.Add(group.Key);
        }

        return ambiguousMutationIds;
    }

    private void RejectAmbiguousIncomingMutationRows(
        SyncPushRequest request,
        IReadOnlySet<string> ambiguousMutationIds,
        SyncPushResult result)
    {
        if (ambiguousMutationIds.Count == 0)
            return;

        RejectAmbiguousIncomingMutationRows(
            request.CompanyProfiles,
            nameof(CompanyProfile),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.Units,
            nameof(Unit),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.CustomerCategories,
            nameof(CustomerCategory),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.PriceGradeOptions,
            nameof(PriceGradeOption),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.TradeTypeOptions,
            nameof(TradeTypeOption),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.ItemCategoryOptions,
            nameof(ItemCategoryOption),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.CustomerMasters,
            nameof(CustomerMaster),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.Customers,
            nameof(Customer),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.CustomerContracts,
            nameof(CustomerContract),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.Items,
            nameof(Item),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.ItemPriceGrades,
            nameof(ItemPriceGrade),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.Transactions,
            nameof(TransactionRecord),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.TransactionAttachments,
            nameof(TransactionAttachment),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.RentalManagementCompanies,
            nameof(RentalManagementCompany),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.RentalBillingProfiles,
            nameof(RentalBillingProfile),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.RentalAssets,
            nameof(RentalAsset),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.RentalAssetAssignmentHistories,
            nameof(RentalAssetAssignmentHistory),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.RentalBillingLogs,
            nameof(RentalBillingLog),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.Invoices,
            nameof(Invoice),
            ambiguousMutationIds,
            result);
        RejectAmbiguousIncomingMutationRows(
            request.Payments,
            nameof(Payment),
            ambiguousMutationIds,
            result);
    }

    private void RejectAmbiguousIncomingMutationRows<TDto>(
        List<TDto> payload,
        string entityName,
        IReadOnlySet<string> ambiguousMutationIds,
        SyncPushResult result)
        where TDto : SyncEntityDto
    {
        foreach (var dto in payload.Where(dto =>
                     ambiguousMutationIds.Contains(
                         NormalizeMutationId(dto.MutationId))))
        {
            AddClientConflict(
                dto,
                entityName,
                AmbiguousIncomingMutationIdConflictReason,
                result);
        }

        payload.RemoveAll(dto =>
            ambiguousMutationIds.Contains(
                NormalizeMutationId(dto.MutationId)));
    }

    private async Task LoadProcessedMutationCacheEntriesAsync(
        IReadOnlyCollection<string> requestedMutationIds,
        CancellationToken cancellationToken)
    {
        if (requestedMutationIds.Count == 0)
            return;

        foreach (var trackedMutation in _dbContext.ProcessedSyncMutations.Local)
        {
            var mutationId = NormalizeMutationId(trackedMutation.MutationId);
            if (requestedMutationIds.Contains(mutationId))
                _processedMutationsById.TryAdd(mutationId, trackedMutation);
        }

        var missingMutationIds = requestedMutationIds
            .Where(mutationId => !_processedMutationsById.ContainsKey(mutationId))
            .ToArray();
        foreach (var mutationIdBatch in missingMutationIds.Chunk(500))
        {
            var batch = mutationIdBatch.ToArray();
            var persistedMutations = await _dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .Where(entity => batch.Contains(entity.MutationId.Trim().ToLower()))
                .ToListAsync(cancellationToken);
            foreach (var persistedMutation in persistedMutations)
            {
                var mutationId = NormalizeMutationId(persistedMutation.MutationId);
                _processedMutationsById.TryAdd(mutationId, persistedMutation);
            }
        }
    }

    private static IEnumerable<SyncEntityDto> EnumeratePushMutationDtos(SyncPushRequest request)
    {
        IEnumerable<SyncEntityDto>[] collections =
        [
            request.CompanyProfiles,
            request.Units,
            request.CustomerCategories,
            request.PriceGradeOptions,
            request.TradeTypeOptions,
            request.ItemCategoryOptions,
            request.CustomerMasters,
            request.Customers,
            request.CustomerContracts,
            request.Items,
            request.ItemPriceGrades,
            request.Transactions,
            request.TransactionAttachments,
            request.InventoryTransfers,
            request.RentalManagementCompanies,
            request.RentalBillingProfiles,
            request.RentalAssets,
            request.RentalAssetAssignmentHistories,
            request.RentalBillingLogs,
            request.Invoices,
            request.Payments
        ];

        return collections.SelectMany(static collection => collection);
    }

    private static bool RequiresSerializedPushMutation(SyncPushRequest _)
        // Even an empty payload can repair duplicate latest invoice versions and
        // their inventory snapshots, so every Push must own the mutation scope.
        => true;

    [HttpPost("push")]
    [ProducesResponseType(typeof(SyncPushResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<SyncPushResult>> Push([FromBody] SyncPushRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest("동기화 요청 본문이 비어 있습니다.");

        NormalizePushRequest(request);

        var result = new SyncPushResult();
        var deviceId = NormalizeDeviceId(request.DeviceId);
        var permissionError = ValidatePushPermissions(request);
        if (!string.IsNullOrWhiteSpace(permissionError))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = permissionError });

        var requiresInventoryLedgerRebuild = false;
        var requiresRentalAssignmentRefresh = false;
        var savedStoragePaths = new List<string>();
        var replacedStoragePaths = new List<string>();
        ExceptionDispatchInfo? pushFailure = null;
        var inventoryTransferStockAtomicityRollback = false;
        var rentalProfileAssetAtomicityRollback = false;
        var rentalProfileAssetAtomicityProfileIds = new List<Guid>();
        await using (var transaction = await InventoryMutationTransactionScope.BeginAsync(
                         _dbContext,
                         RequiresSerializedPushMutation(request),
                         cancellationToken))
        {
            var pushStartedAtUtc = DateTime.UtcNow;
            try
            {
            await InitializeProcessedMutationCacheAsync(request, cancellationToken);
            var ambiguousIncomingMutationIds =
                FindAmbiguousIncomingMutationIds(request);
            var ambiguousIncomingInvoices = (request.Invoices ?? [])
                .Where(invoice =>
                    ambiguousIncomingMutationIds.Contains(
                        NormalizeMutationId(invoice.MutationId)))
                .ToList();
            RejectAmbiguousIncomingMutationRows(
                request,
                ambiguousIncomingMutationIds,
                result);
            var scopedCompanyProfiles = await PrepareScopedCompanyProfilesAsync(request.CompanyProfiles ?? [], result, cancellationToken);
            await UpsertEntitiesAsync(scopedCompanyProfiles, _dbContext.CompanyProfiles,
                (e, d) => e.Apply(d), d => new CompanyProfile { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            await UpsertUnitsAsync(request.Units ?? [], result, deviceId, cancellationToken);
            await UpsertCustomerCategoriesAsync(request.CustomerCategories ?? [], result, deviceId, cancellationToken);
            await UpsertPriceGradeOptionsAsync(request.PriceGradeOptions ?? [], result, deviceId, cancellationToken);
            await UpsertTradeTypeOptionsAsync(request.TradeTypeOptions ?? [], result, deviceId, cancellationToken);
            await UpsertItemCategoryOptionsAsync(request.ItemCategoryOptions ?? [], result, deviceId, cancellationToken);
            var scopedCustomerMasters = await PrepareScopedCustomerMastersAsync(request.CustomerMasters ?? [], result, cancellationToken);
            var validCustomerMasters = await FilterValidCustomerMastersAsync(scopedCustomerMasters, result, cancellationToken);
            await UpsertEntitiesAsync(validCustomerMasters, _dbContext.CustomerMasters,
                (e, d) => e.Apply(d), d => new CustomerMaster { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            if (validCustomerMasters.Count > 0)
                await _dbContext.SaveChangesAsync(cancellationToken);
            var scopedCustomers = await PrepareScopedCustomersAsync(request.Customers ?? [], result, cancellationToken);
            var validCustomers = await FilterValidCustomersAsync(scopedCustomers, result, cancellationToken);
            var customerRestoreGenerations = await CaptureDeletedCustomerRestoreGenerationsAsync(
                validCustomers,
                cancellationToken);
            var acceptedCustomers = await UpsertEntitiesAsync(validCustomers, _dbContext.Customers,
                (e, d) => e.Apply(d), d => new Customer { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            await RestoreAcceptedCustomerDeletionGenerationContractsAsync(
                acceptedCustomers,
                customerRestoreGenerations,
                cancellationToken);
            await CascadeDeletedCustomerContractsAsync(acceptedCustomers, cancellationToken);
            if (validCustomers.Count > 0)
                await _dbContext.SaveChangesAsync(cancellationToken);
            var validCustomerContracts = await FilterValidCustomerContractsAsync(request.CustomerContracts ?? [], result, cancellationToken);
            var acceptedCustomerContracts = await UpsertEntitiesAsync(validCustomerContracts, _dbContext.CustomerContracts,
                (e, d) => e.Apply(d), d => new CustomerContract { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            if (validCustomerContracts.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                await PersistCustomerContractsToStorageAsync(acceptedCustomerContracts, savedStoragePaths, replacedStoragePaths, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            var resolvedIncomingItemIds = new Dictionary<Guid, Guid>();
            var scopeRejectedIncomingItemIds = new HashSet<Guid>();
            var inventoryTrackingTransitionItemIds = new HashSet<Guid>();
            var scopedItems = await PrepareScopedItemsAsync(
                request.Items ?? [],
                resolvedIncomingItemIds,
                scopeRejectedIncomingItemIds,
                inventoryTrackingTransitionItemIds,
                result,
                cancellationToken);
            var resolvedItemWarehouseStockResponseAliases =
                CaptureResolvedItemWarehouseStockResponseAliases(
                    request.ItemWarehouseStocks ?? [],
                    resolvedIncomingItemIds);
            RemapIncomingItemReferences(request, resolvedIncomingItemIds);
            await EnsureItemCategoryOptionsForItemsAsync(scopedItems, cancellationToken);
            var acceptedItems = await UpsertEntitiesAsync(scopedItems, _dbContext.Items,
                (e, d) => e.Apply(d), d => new Item { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            if (scopedItems.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                var deletedItemIds = acceptedItems
                    .Where(item => item.IsDeleted && item.Id != Guid.Empty)
                    .Select(item => item.Id)
                    .Distinct()
                    .ToList();
                await RemoveItemPriceGradesForDeletedItemsAsync(deletedItemIds, cancellationToken);
                await RemoveInventoryRuntimeStateForDisabledItemsAsync(acceptedItems, cancellationToken);
                await RemoveSupersededPurgeRecordsAsync("item", acceptedItems, cancellationToken);
                if (acceptedItems.Any(item => inventoryTrackingTransitionItemIds.Contains(item.Id)))
                    requiresInventoryLedgerRebuild = true;
            }
            var validItemPriceGrades = await FilterValidItemPriceGradesAsync(request.ItemPriceGrades ?? [], result, cancellationToken);
            await UpsertEntitiesAsync(validItemPriceGrades, _dbContext.ItemPriceGrades,
                (e, d) => e.Apply(d), d => new ItemPriceGrade { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            if (validItemPriceGrades.Count > 0)
                await _dbContext.SaveChangesAsync(cancellationToken);
            var requestedInventoryTransfers = request.InventoryTransfers ?? [];
            var scopedInventoryTransfers = await PrepareScopedInventoryTransfersAsync(
                requestedInventoryTransfers,
                result,
                cancellationToken);
            var structurallyValidInventoryTransfers =
                await FilterValidInventoryTransfersAsync(
                    scopedInventoryTransfers,
                    ambiguousIncomingMutationIds,
                    result,
                    cancellationToken);
            var concurrencyValidInventoryTransfers =
                await FilterInventoryTransferConcurrencyConflictsAsync(
                    structurallyValidInventoryTransfers,
                    ambiguousIncomingMutationIds,
                    result,
                    cancellationToken);
            var validInventoryTransfers =
                await FilterInventoryTransferPurgeAndMissingDeleteAcknowledgementsAsync(
                    concurrencyValidInventoryTransfers,
                    result,
                    deviceId,
                    cancellationToken);
            var (
                rejectedInventoryTransferStockKeys,
                rejectedInventoryTransferStockItemIds) =
                await BuildRejectedInventoryTransferStockScopeAsync(
                    requestedInventoryTransfers,
                    validInventoryTransfers,
                    cancellationToken);
            var (
                ambiguousInvoiceStockKeys,
                ambiguousInvoiceStockItemIds) =
                await BuildAmbiguousInvoiceStockScopeAsync(
                    ambiguousIncomingInvoices,
                    resolvedIncomingItemIds,
                    cancellationToken);
            var itemWarehouseStockResult = await UpsertItemWarehouseStocksAsync(
                (request.ItemWarehouseStocks ?? [])
                    .Where(stock =>
                        !scopeRejectedIncomingItemIds.Contains(stock.ItemId) &&
                        !ambiguousInvoiceStockKeys.Contains(
                            BuildItemWarehouseStockSnapshotKey(
                                stock.ItemId,
                                stock.WarehouseCode)) &&
                        !rejectedInventoryTransferStockKeys.Contains(
                            BuildItemWarehouseStockSnapshotKey(
                                stock.ItemId,
                                stock.WarehouseCode)))
                    .ToList(),
                (request.ItemWarehouseStockSnapshotMarkers ?? [])
                    .Where(marker =>
                        !scopeRejectedIncomingItemIds.Contains(marker.ItemId) &&
                        !ambiguousInvoiceStockItemIds.Contains(marker.ItemId) &&
                        !rejectedInventoryTransferStockItemIds.Contains(marker.ItemId))
                    .ToList(),
                result,
                deviceId,
                cancellationToken);
            if (itemWarehouseStockResult.AffectedItemIds.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                await RecalculateItemCurrentStocksFromWarehousesAsync(itemWarehouseStockResult.AffectedItemIds, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            var acceptedItemIds = acceptedItems
                .Where(item => item.Id != Guid.Empty)
                .Select(item => item.Id)
                .Distinct()
                .ToList();
            var acceptedInventoryItems = acceptedItemIds.Count == 0
                ? new List<Item>()
                : await _dbContext.Items
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(item => acceptedItemIds.Contains(item.Id) && !item.IsDeleted)
                    .ToListAsync(cancellationToken);
            var acceptedInventoryItemIds = acceptedInventoryItems
                .Where(item => ItemOperationalPolicy.SupportsInventory(item.TrackingType))
                .Select(item => item.Id)
                .ToList();
            if (acceptedInventoryItemIds.Count > 0)
            {
                await RecalculateItemCurrentStocksFromWarehousesAsync(
                    acceptedInventoryItemIds,
                    cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            var resolvedIncomingRentalManagementCompanyIds = new Dictionary<Guid, Guid>();
            var incomingRentalManagementCompanyMutations = new List<IncomingRentalManagementCompanyMutation>();
            var requestedRentalCompanies = (request.RentalManagementCompanies ?? []).ToList();
            var rawPreflightRentalCompanies = FilterAmbiguousRawIncomingRentalManagementCompanies(
                requestedRentalCompanies,
                result);
            var scopedRentalCompanies = await PrepareScopedRentalManagementCompaniesAsync(
                rawPreflightRentalCompanies,
                resolvedIncomingRentalManagementCompanyIds,
                incomingRentalManagementCompanyMutations,
                result,
                cancellationToken);
            var validRentalCompanies = FilterAmbiguousIncomingRentalManagementCompanies(
                scopedRentalCompanies,
                incomingRentalManagementCompanyMutations,
                result);
            await UpsertEntitiesAsync(validRentalCompanies, _dbContext.RentalManagementCompanies,
                (e, d) => e.Apply(d), d => new RentalManagementCompany { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            if (validRentalCompanies.Count > 0)
                await _dbContext.SaveChangesAsync(cancellationToken);
            var requestedRentalProfiles = (request.RentalBillingProfiles ?? []).ToList();
            var originalRentalProfileIds = CaptureOriginalRentalBillingProfileIds(requestedRentalProfiles);
            var rentalProfilePushSnapshot = requestedRentalProfiles.Count > 0
                ? await BuildRentalBillingProfilePushSnapshotAsync(
                    requestedRentalProfiles,
                    originalRentalProfileIds,
                    request.RentalAssets ?? [],
                    cancellationToken)
                : null;
            var acknowledgedRentalProfileIds = new Dictionary<Guid, Guid>();
            var acceptedActiveRentalProfileIdsForReferences = new Dictionary<Guid, Guid>();
            var rentalProfileTombstoneSettlementTargets = new List<(Guid ProfileId, Guid? RunId)>();
            var blockedPriorGenerationRentalProfileIdentitiesForReferences =
                new HashSet<RentalProfileTenantIdentity>();
            var scopedRentalProfiles = await PrepareScopedRentalBillingProfilesAsync(
                requestedRentalProfiles,
                originalRentalProfileIds,
                blockedPriorGenerationRentalProfileIdentitiesForReferences,
                result,
                deviceId,
                cancellationToken,
                rentalProfilePushSnapshot);
            var unambiguousRentalProfiles = FilterAmbiguousIncomingRentalBillingProfiles(
                scopedRentalProfiles,
                originalRentalProfileIds,
                result);
            var exactReplayRentalProfiles = unambiguousRentalProfiles
                .Where(dto => HasExactProcessedMutationReplay(
                    dto,
                    nameof(RentalBillingProfile),
                    GetOriginalRentalBillingProfileId(dto, originalRentalProfileIds)))
                .ToList();
            var exactReplayRentalProfileSet = exactReplayRentalProfiles
                .ToHashSet(ReferenceEqualityComparer.Instance);
            var nonReplayRentalProfiles = await FilterRentalBillingProfilesWithSafeProjectedAssetCoverageAsync(
                unambiguousRentalProfiles.Where(dto => !exactReplayRentalProfileSet.Contains(dto)),
                request.RentalAssets ?? [],
                result,
                cancellationToken,
                rentalProfilePushSnapshot);
            var rentalProfileRestoreCustomerIds = await BuildRentalBillingProfileRestoreCustomerIdsAsync(
                nonReplayRentalProfiles,
                result,
                cancellationToken,
                rentalProfilePushSnapshot);
            var validRentalProfiles = await FilterValidRentalBillingProfilesAsync(
                nonReplayRentalProfiles,
                result,
                cancellationToken,
                rentalProfilePushSnapshot);
            validRentalProfiles = FilterAmbiguousIncomingRentalBillingProfiles(
                validRentalProfiles,
                originalRentalProfileIds,
                result);
            validRentalProfiles = await FilterRentalBillingProfilesWithValidIncludedAssetReferencesAsync(
                validRentalProfiles,
                request.RentalAssets ?? [],
                originalRentalProfileIds,
                result,
                cancellationToken,
                rentalProfilePushSnapshot);
            validRentalProfiles.AddRange(exactReplayRentalProfiles);
            var acceptedRentalProfiles = await UpsertRentalBillingProfilesAsync(
                validRentalProfiles,
                originalRentalProfileIds,
                acknowledgedRentalProfileIds,
                acceptedActiveRentalProfileIdsForReferences,
                blockedPriorGenerationRentalProfileIdentitiesForReferences,
                result,
                deviceId,
                rentalProfileTombstoneSettlementTargets,
                cancellationToken,
                rentalProfilePushSnapshot);
            await RestoreLinkedDeletedCustomerContractsForRentalBillingProfilesAsync(
                acceptedRentalProfiles,
                rentalProfileRestoreCustomerIds,
                cancellationToken,
                rentalProfilePushSnapshot);
            if (validRentalProfiles.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                var acceptedActiveRentalProfileIds = acceptedRentalProfiles
                    .Where(profile => profile.Id != Guid.Empty && !profile.IsDeleted)
                    .Select(profile => profile.Id)
                    .Distinct()
                    .ToHashSet();
                await RemoveSupersededPurgeRecordsAsync(
                    "rental-billing-profile",
                    acceptedRentalProfiles,
                    cancellationToken);
                if (acceptedActiveRentalProfileIds.Count > 0)
                {
                    result.AcceptedRevisions.RemoveAll(revision =>
                        string.Equals(
                            revision.EntityName,
                            nameof(RentalBillingProfile),
                            StringComparison.OrdinalIgnoreCase) &&
                        acceptedActiveRentalProfileIds.Contains(revision.EntityId));
                }
                if (acceptedRentalProfiles.Count > 0)
                    requiresRentalAssignmentRefresh = true;
            }

            await RejectBlockedPriorGenerationRentalDependentsAsync(
                request,
                blockedPriorGenerationRentalProfileIdentitiesForReferences,
                result,
                cancellationToken);
            var resolvedRentalProfileIds = acceptedActiveRentalProfileIdsForReferences;
            var validInvoices = await FilterValidInvoicesAsync(request.Invoices ?? [], result, cancellationToken);
            var invoiceUpsertResult = await UpsertInvoicesAsync(
                validInvoices,
                result,
                deviceId,
                itemWarehouseStockResult.AppliedStockKeys,
                cancellationToken);
            var invoiceRentalSettlementTargets = invoiceUpsertResult.RentalSettlementTargets;
            if (validInvoices.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                if (invoiceUpsertResult.AcceptedCount > 0)
                    requiresInventoryLedgerRebuild = true;
            }
            var scopedTransactions = await PrepareScopedTransactionsAsync(request.Transactions ?? [], result, cancellationToken);
            var validTransactions = await FilterValidTransactionsAsync(scopedTransactions, result, cancellationToken);
            var validPayments = await FilterValidPaymentsAsync(request.Payments ?? [], result, cancellationToken);
            var paymentTransactionAtomicity = await FilterAtomicPaymentTransactionPairsAsync(
                request.Transactions ?? [],
                validTransactions,
                request.Payments ?? [],
                validPayments,
                result,
                cancellationToken);
            validTransactions = await FilterPaymentControlledTransactionOnlyMutationsAsync(
                paymentTransactionAtomicity.ValidTransactions,
                paymentTransactionAtomicity.RequestedPaymentIds,
                result,
                cancellationToken);
            validPayments = paymentTransactionAtomicity.ValidPayments;
            var existingTransactionRentalSettlementTargets =
                await LoadExistingRentalSettlementTargetsByTransactionIdAsync(validTransactions, cancellationToken);
            var acceptedTransactions = await UpsertEntitiesAsync(validTransactions, _dbContext.Transactions,
                ApplyTransactionMutation,
                d => new TransactionRecord { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id },
                result,
                deviceId,
                cancellationToken,
                preserveOriginalIncomingPayloadHashForReceipt: true);
            var transactionRentalSettlementTargets =
                BuildRentalSettlementTargetsForAcceptedTransactions(acceptedTransactions, existingTransactionRentalSettlementTargets);
            if (validTransactions.Count > 0)
                await _dbContext.SaveChangesAsync(cancellationToken);
            var requestedTransactionAttachments = request.TransactionAttachments ?? [];
            var deferredPaymentTransactionAttachments = requestedTransactionAttachments
                .Where(attachment => paymentTransactionAtomicity.RequestedPaymentIds.Contains(attachment.TransactionId))
                .ToList();
            var immediateTransactionAttachmentPayload = requestedTransactionAttachments
                .Where(attachment => !paymentTransactionAtomicity.RequestedPaymentIds.Contains(attachment.TransactionId))
                .ToList();
            var validTransactionAttachments = await FilterValidTransactionAttachmentsAsync(immediateTransactionAttachmentPayload, result, cancellationToken);
            var acceptedTransactionAttachments = await UpsertEntitiesAsync(validTransactionAttachments, _dbContext.TransactionAttachments,
                (e, d) => e.Apply(d), d => new TransactionAttachment { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            if (validTransactionAttachments.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                await PersistTransactionAttachmentsToStorageAsync(acceptedTransactionAttachments, savedStoragePaths, replacedStoragePaths, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            var acceptedInventoryTransferCount = await UpsertInventoryTransfersAsync(
                validInventoryTransfers,
                result,
                deviceId,
                itemWarehouseStockResult.AppliedStockKeys,
                itemWarehouseStockResult
                    .OriginalQuantitiesByAppliedKey,
                invoiceUpsertResult.StockDeltaDifferences,
                cancellationToken);
            var lateRejectedInventoryTransferIds = result.Conflicts
                .Where(conflict =>
                    string.Equals(
                        conflict.EntityName,
                        nameof(InventoryTransfer),
                        StringComparison.OrdinalIgnoreCase))
                .Select(conflict => conflict.EntityId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var postStockRejectedInventoryTransfers =
                validInventoryTransfers
                    .Where(transfer =>
                        lateRejectedInventoryTransferIds.Contains(
                            transfer.Id.ToString("D")))
                    .ToList();
            if (postStockRejectedInventoryTransfers.Count > 0)
            {
                var postStockAcceptedInventoryTransfers =
                    validInventoryTransfers
                        .Where(transfer =>
                            !postStockRejectedInventoryTransfers.Contains(
                                transfer))
                        .ToList();
                var (lateRejectedStockKeys, _) =
                    await BuildRejectedInventoryTransferStockScopeAsync(
                        validInventoryTransfers,
                        postStockAcceptedInventoryTransfers,
                        cancellationToken);
                if (lateRejectedStockKeys.Overlaps(
                        itemWarehouseStockResult.AppliedStockKeys))
                {
                    inventoryTransferStockAtomicityRollback = true;
                    throw new InventoryTransferStockAtomicityRollbackException();
                }
            }
            if (acceptedInventoryTransferCount > 0)
                requiresInventoryLedgerRebuild = true;
            var requiresRentalAssetLock =
                (request.RentalAssets?.Count ?? 0) > 0 ||
                (request.RentalAssetAssignmentHistories?.Count ?? 0) > 0;
            if (requiresRentalAssetLock)
                await RentalAssetSyncLock.WaitAsync(cancellationToken);

            try
            {
                var scopedRentalAssets = await PrepareScopedRentalAssetsAsync(
                    request.RentalAssets ?? [],
                    result,
                    cancellationToken,
                    rentalProfilePushSnapshot);
                var rentalAssetRestoreCustomerIds = await BuildRentalAssetRestoreCustomerIdsAsync(
                    scopedRentalAssets,
                    result,
                    cancellationToken,
                    rentalProfilePushSnapshot);
                var validRentalAssets = await FilterValidRentalAssetsAsync(
                    scopedRentalAssets,
                    resolvedRentalProfileIds,
                    result,
                    cancellationToken,
                    rentalProfilePushSnapshot);
                var acceptedRentalAssets = await UpsertEntitiesAsync(validRentalAssets, _dbContext.RentalAssets,
                    (e, d) => e.Apply(d), d => new RentalAsset { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
                await RestoreLinkedDeletedCustomerContractsForRentalAssetsAsync(acceptedRentalAssets, rentalAssetRestoreCustomerIds, cancellationToken);
                if (validRentalAssets.Count > 0)
                    await _dbContext.SaveChangesAsync(cancellationToken);
                rentalProfileAssetAtomicityProfileIds = await FindAcceptedRentalBillingProfilesWithUnavailableTemplateAssetsAsync(
                    acceptedRentalProfiles,
                    cancellationToken);
                if (rentalProfileAssetAtomicityProfileIds.Count > 0)
                {
                    rentalProfileAssetAtomicityRollback = true;
                    throw new RentalProfileAssetAtomicityRollbackException();
                }
                var scopedRentalAssignmentHistories = await PrepareScopedRentalAssetAssignmentHistoriesAsync(request.RentalAssetAssignmentHistories ?? [], result, cancellationToken);
                await UpsertEntitiesAsync(scopedRentalAssignmentHistories, _dbContext.RentalAssetAssignmentHistories,
                    (e, d) => e.Apply(d), d => new RentalAssetAssignmentHistory { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
                var scopedRentalBillingLogs = await PrepareScopedRentalBillingLogsAsync(request.RentalBillingLogs ?? [], result, cancellationToken);
                var validRentalBillingLogs = await FilterValidRentalBillingLogsAsync(scopedRentalBillingLogs, result, cancellationToken);
                await UpsertEntitiesAsync(validRentalBillingLogs, _dbContext.RentalBillingLogs,
                    (e, d) => e.Apply(d), d => new RentalBillingLog { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
                var acceptedPayments = await UpsertEntitiesAsync(validPayments, _dbContext.Payments,
                    (e, d) => e.Apply(d), d => new Payment { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
                var paymentRentalSettlementTargets =
                    await LoadRentalSettlementTargetsForPaymentsAsync(acceptedPayments, cancellationToken);
                await SoftDeletePaymentAttachmentsForDeletedPaymentsAsync(acceptedPayments, cancellationToken);
                var paymentLinkedTransactionSyncTargets =
                    await SynchronizeAcceptedPaymentsToLinkedTransactionsAsync(acceptedPayments, cancellationToken);
                var paymentLinkedTransactionSettlementTargets =
                    await CascadeDeletedPaymentsToLinkedTransactionsAsync(acceptedPayments, cancellationToken);

                await SynchronizeAcceptedCustomerRentalLinksAsync(acceptedCustomers, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                var rentalSettlementTargets = invoiceRentalSettlementTargets
                    .Concat(rentalProfileTombstoneSettlementTargets)
                    .Concat(transactionRentalSettlementTargets)
                    .Concat(paymentRentalSettlementTargets)
                    .Concat(paymentLinkedTransactionSyncTargets)
                    .Concat(paymentLinkedTransactionSettlementTargets)
                    .Distinct()
                    .ToList();
                if (rentalSettlementTargets.Count > 0)
                {
                    await _rentalSettlementRecalculationService.RecalculateRentalSettlementsAsync(rentalSettlementTargets, cancellationToken);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
                await PopulateAcceptedRevisionsAsync(result, validRentalAssets, _dbContext.RentalAssets, nameof(RentalAsset), cancellationToken);
                await PopulateAcceptedRevisionsAsync(result, scopedRentalAssignmentHistories, _dbContext.RentalAssetAssignmentHistories, nameof(RentalAssetAssignmentHistory), cancellationToken);
                await PopulateAcceptedRevisionsAsync(result, validRentalBillingLogs, _dbContext.RentalBillingLogs, nameof(RentalBillingLog), cancellationToken);
                await PopulateAcceptedRevisionsAsync(result, validPayments, _dbContext.Payments, nameof(Payment), cancellationToken);
                var acceptedPaymentIds = result.AcceptedRevisions
                    .Where(revision => string.Equals(revision.EntityName, nameof(Payment), StringComparison.OrdinalIgnoreCase))
                    .Select(revision => revision.EntityId)
                    .Where(entityId => entityId != Guid.Empty)
                    .Distinct()
                    .ToList();
                var acceptedPaymentIdSet = acceptedPaymentIds.ToHashSet();
                var deferredAcceptedAttachmentPayload = FilterDeferredPaymentTransactionAttachments(
                    deferredPaymentTransactionAttachments,
                    acceptedPaymentIdSet,
                    result);
                var validDeferredPaymentTransactionAttachments = await FilterValidTransactionAttachmentsAsync(
                    deferredAcceptedAttachmentPayload,
                    result,
                    cancellationToken);
                validTransactionAttachments.AddRange(validDeferredPaymentTransactionAttachments);
                var acceptedDeferredPaymentTransactionAttachments = await UpsertEntitiesAsync(
                    validDeferredPaymentTransactionAttachments,
                    _dbContext.TransactionAttachments,
                    (entity, dto) => entity.Apply(dto),
                    dto => new TransactionAttachment { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id },
                    result,
                    deviceId,
                    cancellationToken);
                if (validDeferredPaymentTransactionAttachments.Count > 0)
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await PersistTransactionAttachmentsToStorageAsync(
                        acceptedDeferredPaymentTransactionAttachments,
                        savedStoragePaths,
                        replacedStoragePaths,
                        cancellationToken);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
                await PopulateAcceptedRevisionsByIdsAsync(
                    result,
                    acceptedPaymentIds,
                    _dbContext.Transactions,
                    nameof(TransactionRecord),
                    cancellationToken);
                if (acceptedRentalAssets.Count > 0)
                    requiresRentalAssignmentRefresh = true;
            }
            finally
            {
                if (requiresRentalAssetLock)
                    RentalAssetSyncLock.Release();
            }

            if (requiresRentalAssignmentRefresh)
                await _rentalAssignmentHistoryService.RefreshAsync(cancellationToken);

            if (requiresInventoryLedgerRebuild)
                await _inventoryLedgerService.RebuildAsync(cancellationToken);

            await PopulateServerConflictActorsAsync(
                result.Conflicts,
                pushStartedAtUtc,
                cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, scopedCompanyProfiles, _dbContext.CompanyProfiles, nameof(CompanyProfile), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, request.Units ?? [], _dbContext.Units, nameof(Unit), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, request.CustomerCategories ?? [], _dbContext.CustomerCategories, nameof(CustomerCategory), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, request.PriceGradeOptions ?? [], _dbContext.PriceGradeOptions, nameof(PriceGradeOption), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, request.TradeTypeOptions ?? [], _dbContext.TradeTypeOptions, nameof(TradeTypeOption), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, request.ItemCategoryOptions ?? [], _dbContext.ItemCategoryOptions, nameof(ItemCategoryOption), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, validCustomerMasters, _dbContext.CustomerMasters, nameof(CustomerMaster), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, validCustomers, _dbContext.Customers, nameof(Customer), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, validCustomerContracts, _dbContext.CustomerContracts, nameof(CustomerContract), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, scopedItems, _dbContext.Items, nameof(Item), cancellationToken);
            PopulateResolvedItemAcceptedRevisionAliases(result, resolvedIncomingItemIds);
            await PopulateAcceptedRevisionsAsync(result, validItemPriceGrades, _dbContext.ItemPriceGrades, nameof(ItemPriceGrade), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, validInvoices, _dbContext.Invoices, nameof(Invoice), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, validTransactions, _dbContext.Transactions, nameof(TransactionRecord), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, validTransactionAttachments, _dbContext.TransactionAttachments, nameof(TransactionAttachment), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, validInventoryTransfers, _dbContext.InventoryTransfers, nameof(InventoryTransfer), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, validRentalCompanies, _dbContext.RentalManagementCompanies, nameof(RentalManagementCompany), cancellationToken);
            PopulateResolvedRentalManagementCompanyAcceptedRevisionAliases(
                result,
                resolvedIncomingRentalManagementCompanyIds);
            await PopulateAcknowledgedRentalBillingProfileRevisionsAsync(
                result,
                acknowledgedRentalProfileIds,
                cancellationToken);
            await DeduplicateOpenConflictLogsForResultAsync(result.Conflicts, cancellationToken);
            RemapResolvedRentalManagementCompanyServerConflicts(
                result,
                incomingRentalManagementCompanyMutations);
            RemapResolvedItemWarehouseStockResponseAliases(
                result,
                resolvedItemWarehouseStockResponseAliases);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (InventoryTransferStockAtomicityRollbackException exception)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    pushFailure = ExceptionDispatchInfo.Capture(
                        new AggregateException(
                            "Inventory-transfer stock atomicity rollback failed.",
                            exception,
                            rollbackException));
                }
                finally
                {
                    _dbContext.ChangeTracker.Clear();
                }
            }
            catch (RentalProfileAssetAtomicityRollbackException exception)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    pushFailure = ExceptionDispatchInfo.Capture(
                        new AggregateException(
                            "Rental-profile asset atomicity rollback failed.",
                            exception,
                            rollbackException));
                }
                finally
                {
                    _dbContext.ChangeTracker.Clear();
                }
            }
            catch (Exception exception)
            {
                pushFailure = ExceptionDispatchInfo.Capture(exception);
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch
                {
                    // The original mutation failure is authoritative. Disposal below
                    // ends any still-active transaction before reference reconciliation.
                }
            }
        }

        if (pushFailure is not null)
        {
            await _storedFileReferenceReconciler.DeleteUnreferencedAsync(
                savedStoragePaths.Concat(replacedStoragePaths),
                CancellationToken.None);
            pushFailure.Throw();
        }

        if (inventoryTransferStockAtomicityRollback)
        {
            await _storedFileReferenceReconciler.DeleteUnreferencedAsync(
                savedStoragePaths.Concat(replacedStoragePaths),
                CancellationToken.None);
            ResetRolledBackSyncPushResult(result);
            AddNotice(
                result,
                nameof(InventoryTransfer),
                Guid.Empty,
                "inventory-transfer-stock-atomicity-rollback",
                "The entire Push was rolled back because a rejected inventory transfer shared an applied client stock snapshot. No mutation or stock acknowledgement was committed.");
            result.CurrentServerRevision =
                await GetCurrentRevisionAsync(cancellationToken);
            return Ok(result);
        }

        if (rentalProfileAssetAtomicityRollback)
        {
            await _storedFileReferenceReconciler.DeleteUnreferencedAsync(
                savedStoragePaths.Concat(replacedStoragePaths),
                CancellationToken.None);
            ResetRolledBackSyncPushResult(result);
            foreach (var profileId in rentalProfileAssetAtomicityProfileIds.Distinct())
            {
                AddNotice(
                    result,
                    nameof(RentalBillingProfile),
                    profileId,
                    "rental-profile-asset-atomicity-rollback",
                    "청구 프로필이 참조한 신규 또는 복원 자산이 같은 동기화에서 승인되지 않아 전체 Push를 롤백했습니다. 자산 충돌을 해결한 뒤 다시 동기화하세요.");
            }
            result.CurrentServerRevision =
                await GetCurrentRevisionAsync(cancellationToken);
            return Ok(result);
        }

        await _storedFileReferenceReconciler.DeleteUnreferencedAsync(
            replacedStoragePaths,
            CancellationToken.None);

        result.CurrentServerRevision = await GetCurrentRevisionAsync(cancellationToken);
        return Ok(result);
    }

    private async Task PopulateAcceptedRevisionsAsync<TEntity, TDto>(
        SyncPushResult result,
        IEnumerable<TDto> payload,
        DbSet<TEntity> dbSet,
        string entityName,
        CancellationToken cancellationToken)
        where TEntity : TrackedEntity
        where TDto : SyncEntityDto
    {
        var payloadRows = payload as IReadOnlyCollection<TDto> ?? payload.ToList();
        var conflictIds = result.Conflicts
            .Where(conflict => string.Equals(conflict.EntityName, entityName, StringComparison.OrdinalIgnoreCase))
            .Select(conflict => conflict.EntityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ids = payloadRows
            .Select(dto => dto.Id)
            .Where(id => id != Guid.Empty && !conflictIds.Contains(id.ToString("D")))
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return;

        var alreadyRecorded = result.AcceptedRevisions
            .Where(revision => string.Equals(revision.EntityName, entityName, StringComparison.OrdinalIgnoreCase))
            .Select(revision => revision.EntityId)
            .ToHashSet();

        ids = ids
            .Where(id => !alreadyRecorded.Contains(id))
            .ToList();

        if (ids.Count == 0)
            return;

        var rows = await dbSet.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => ids.Contains(entity.Id))
            .Select(entity => new SyncAcceptedRevisionDto
            {
                EntityName = entityName,
                EntityId = entity.Id,
                Revision = entity.Revision,
                UpdatedAtUtc = entity.UpdatedAtUtc,
                IsDeleted = entity.IsDeleted
            })
            .ToListAsync(cancellationToken);

        result.AcceptedRevisions.AddRange(rows);
        var returnedIds = rows.Select(row => row.EntityId).ToHashSet();
        var missingDeleteAcknowledgements = payloadRows
            .Where(dto => dto.IsDeleted &&
                          dto.Id != Guid.Empty &&
                          ids.Contains(dto.Id) &&
                          !returnedIds.Contains(dto.Id))
            .GroupBy(dto => dto.Id)
            .Select(group => group.Last())
            .Select(dto => new SyncAcceptedRevisionDto
            {
                EntityName = entityName,
                EntityId = dto.Id,
                Revision = Math.Max(dto.ExpectedRevision, dto.Revision),
                UpdatedAtUtc = NormalizeUtc(dto.UpdatedAtUtc),
                IsDeleted = true
            });
        result.AcceptedRevisions.AddRange(missingDeleteAcknowledgements);
    }

    private async Task PopulateAcceptedRevisionsByIdsAsync<TEntity>(
        SyncPushResult result,
        IEnumerable<Guid> entityIds,
        DbSet<TEntity> dbSet,
        string entityName,
        CancellationToken cancellationToken)
        where TEntity : TrackedEntity
    {
        var conflictIds = result.Conflicts
            .Where(conflict => string.Equals(conflict.EntityName, entityName, StringComparison.OrdinalIgnoreCase))
            .Select(conflict => Guid.TryParse(conflict.EntityId, out var entityId) ? entityId : Guid.Empty)
            .Where(entityId => entityId != Guid.Empty)
            .ToHashSet();
        var alreadyRecordedIds = result.AcceptedRevisions
            .Where(revision => string.Equals(revision.EntityName, entityName, StringComparison.OrdinalIgnoreCase))
            .Select(revision => revision.EntityId)
            .ToHashSet();
        var ids = entityIds
            .Where(entityId => entityId != Guid.Empty &&
                               !conflictIds.Contains(entityId) &&
                               !alreadyRecordedIds.Contains(entityId))
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return;

        var rows = await dbSet.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => ids.Contains(entity.Id))
            .Select(entity => new SyncAcceptedRevisionDto
            {
                EntityName = entityName,
                EntityId = entity.Id,
                Revision = entity.Revision,
                UpdatedAtUtc = entity.UpdatedAtUtc,
                IsDeleted = entity.IsDeleted
            })
            .ToListAsync(cancellationToken);
        result.AcceptedRevisions.AddRange(rows);
    }

    private async Task PopulateAcknowledgedRentalBillingProfileRevisionsAsync(
        SyncPushResult result,
        IReadOnlyDictionary<Guid, Guid> acknowledgedProfileIds,
        CancellationToken cancellationToken)
    {
        var entityName = nameof(RentalBillingProfile);
        var conflictIds = result.Conflicts
            .Where(conflict => string.Equals(conflict.EntityName, entityName, StringComparison.OrdinalIgnoreCase))
            .Select(conflict => Guid.TryParse(conflict.EntityId, out var entityId) ? entityId : Guid.Empty)
            .Where(entityId => entityId != Guid.Empty)
            .ToHashSet();
        result.AcceptedRevisions.RemoveAll(revision =>
            string.Equals(revision.EntityName, entityName, StringComparison.OrdinalIgnoreCase) &&
            conflictIds.Contains(revision.EntityId));

        if (acknowledgedProfileIds.Count == 0)
            return;

        var canonicalIds = acknowledgedProfileIds.Values
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var canonicalRevisionById = result.AcceptedRevisions
            .Where(revision =>
                string.Equals(revision.EntityName, entityName, StringComparison.OrdinalIgnoreCase) &&
                canonicalIds.Contains(revision.EntityId))
            .GroupBy(revision => revision.EntityId)
            .ToDictionary(group => group.Key, group => group.First());
        if (canonicalIds.Count > 0)
        {
            var canonicalRevisions = await _dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => canonicalIds.Contains(profile.Id))
                .Select(profile => new SyncAcceptedRevisionDto
                {
                    EntityName = entityName,
                    EntityId = profile.Id,
                    Revision = profile.Revision,
                    UpdatedAtUtc = profile.UpdatedAtUtc,
                    IsDeleted = profile.IsDeleted
                })
                .ToListAsync(cancellationToken);
            foreach (var canonicalRevision in canonicalRevisions)
                canonicalRevisionById[canonicalRevision.EntityId] = canonicalRevision;
        }

        var alreadyRecordedIds = result.AcceptedRevisions
            .Where(revision => string.Equals(revision.EntityName, entityName, StringComparison.OrdinalIgnoreCase))
            .Select(revision => revision.EntityId)
            .ToHashSet();
        foreach (var canonicalId in canonicalIds)
        {
            if (conflictIds.Contains(canonicalId) ||
                !canonicalRevisionById.TryGetValue(canonicalId, out var canonicalRevision) ||
                !alreadyRecordedIds.Add(canonicalId))
            {
                continue;
            }

            result.AcceptedRevisions.Add(canonicalRevision);
        }

        foreach (var (originalId, canonicalId) in acknowledgedProfileIds)
        {
            if (originalId == Guid.Empty ||
                originalId == canonicalId ||
                conflictIds.Contains(originalId) ||
                !canonicalRevisionById.TryGetValue(canonicalId, out var canonicalRevision) ||
                !alreadyRecordedIds.Add(originalId))
            {
                continue;
            }

            result.AcceptedRevisions.Add(new SyncAcceptedRevisionDto
            {
                EntityName = entityName,
                EntityId = originalId,
                Revision = canonicalRevision.Revision,
                UpdatedAtUtc = canonicalRevision.UpdatedAtUtc,
                IsDeleted = canonicalRevision.IsDeleted
            });
        }

        result.AcceptedRevisions.RemoveAll(revision =>
            string.Equals(revision.EntityName, entityName, StringComparison.OrdinalIgnoreCase) &&
            conflictIds.Contains(revision.EntityId));
    }

    private static void PopulateResolvedItemAcceptedRevisionAliases(
        SyncPushResult result,
        IReadOnlyDictionary<Guid, Guid> resolvedIncomingItemIds)
    {
        if (resolvedIncomingItemIds.Count == 0)
            return;

        var acceptedByCanonicalId = result.AcceptedRevisions
            .Where(revision => string.Equals(revision.EntityName, nameof(Item), StringComparison.OrdinalIgnoreCase))
            .GroupBy(revision => revision.EntityId)
            .ToDictionary(group => group.Key, group => group.First());
        var alreadyRecordedIds = acceptedByCanonicalId.Keys.ToHashSet();

        foreach (var (originalId, canonicalId) in resolvedIncomingItemIds)
        {
            if (originalId == Guid.Empty ||
                originalId == canonicalId ||
                !acceptedByCanonicalId.TryGetValue(canonicalId, out var canonicalRevision) ||
                !alreadyRecordedIds.Add(originalId))
            {
                continue;
            }

            result.AcceptedRevisions.Add(new SyncAcceptedRevisionDto
            {
                EntityName = nameof(Item),
                EntityId = originalId,
                Revision = canonicalRevision.Revision,
                UpdatedAtUtc = canonicalRevision.UpdatedAtUtc,
                IsDeleted = canonicalRevision.IsDeleted
            });
        }
    }

    private static IReadOnlyDictionary<string, ItemWarehouseStockResponseAlias>
        CaptureResolvedItemWarehouseStockResponseAliases(
            IReadOnlyCollection<ItemWarehouseStockDto> submittedStocks,
            IReadOnlyDictionary<Guid, Guid> resolvedIncomingItemIds)
    {
        if (submittedStocks.Count == 0 ||
            resolvedIncomingItemIds.Count == 0)
        {
            return new Dictionary<string, ItemWarehouseStockResponseAlias>(
                StringComparer.OrdinalIgnoreCase);
        }

        var candidates = submittedStocks
            .Where(stock =>
                stock.ItemId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(stock.WarehouseCode))
            .Select(stock =>
            {
                var canonicalItemId =
                    resolvedIncomingItemIds.GetValueOrDefault(
                        stock.ItemId,
                        stock.ItemId);
                return new ItemWarehouseStockResponseAlias(
                    stock.ItemId,
                    canonicalItemId,
                    OfficeCodeCatalog.NormalizeWarehouseCodeLoose(
                        stock.WarehouseCode));
            })
            .ToList();

        var aliases =
            new Dictionary<string, ItemWarehouseStockResponseAlias>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(candidate =>
                     BuildItemWarehouseStockSnapshotKey(
                         candidate.CanonicalItemId,
                         candidate.WarehouseCode),
                     StringComparer.OrdinalIgnoreCase))
        {
            var originalKeys = group
                .Select(candidate =>
                    BuildItemWarehouseStockSnapshotKey(
                        candidate.OriginalItemId,
                        candidate.WarehouseCode))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (originalKeys.Count != 1)
                continue;

            var candidate = group.Last();
            if (candidate.OriginalItemId == candidate.CanonicalItemId)
                continue;

            aliases[group.Key] = candidate;
        }

        return aliases;
    }

    private static void RemapResolvedItemWarehouseStockResponseAliases(
        SyncPushResult result,
        IReadOnlyDictionary<string, ItemWarehouseStockResponseAlias> aliases)
    {
        if (aliases.Count == 0)
            return;

        foreach (var acceptedKey in
                 result.AcceptedItemWarehouseStockKeys ?? [])
        {
            var canonicalKey = BuildItemWarehouseStockSnapshotKey(
                acceptedKey.ItemId,
                acceptedKey.WarehouseCode);
            if (aliases.TryGetValue(canonicalKey, out var alias))
                acceptedKey.ItemId = alias.OriginalItemId;
        }

        foreach (var conflict in result.Conflicts ?? [])
        {
            if (!string.Equals(
                    conflict.EntityName,
                    nameof(ItemWarehouseStock),
                    StringComparison.OrdinalIgnoreCase) ||
                !TryParseItemWarehouseStockResponseIdentity(
                    conflict.EntityId,
                    out var canonicalItemId,
                    out var warehouseCode))
            {
                continue;
            }

            var canonicalKey = BuildItemWarehouseStockSnapshotKey(
                canonicalItemId,
                warehouseCode);
            if (!aliases.TryGetValue(canonicalKey, out var alias))
                continue;

            conflict.EntityId =
                $"{alias.OriginalItemId:D}|{warehouseCode}";
            conflict.ClientJson =
                RemapItemWarehouseStockConflictJson(
                    conflict.ClientJson,
                    canonicalItemId,
                    alias.OriginalItemId);
            conflict.ServerJson =
                RemapItemWarehouseStockConflictJson(
                    conflict.ServerJson,
                    canonicalItemId,
                    alias.OriginalItemId);
        }
    }

    private static bool TryParseItemWarehouseStockResponseIdentity(
        string? value,
        out Guid itemId,
        out string warehouseCode)
    {
        itemId = Guid.Empty;
        warehouseCode = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var separatorIndex = value.IndexOf('|');
        if (separatorIndex <= 0 ||
            separatorIndex >= value.Length - 1 ||
            !Guid.TryParse(value[..separatorIndex], out itemId))
        {
            itemId = Guid.Empty;
            return false;
        }

        warehouseCode = value[(separatorIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(warehouseCode);
    }

    private static string RemapItemWarehouseStockConflictJson(
        string? json,
        Guid canonicalItemId,
        Guid originalItemId)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json ?? string.Empty;

        try
        {
            var snapshot =
                JsonSerializer.Deserialize<ItemWarehouseStockDto>(
                    json,
                    ConflictJsonOptions);
            if (snapshot is null ||
                snapshot.ItemId != canonicalItemId)
            {
                return json;
            }

            snapshot.ItemId = originalItemId;
            return JsonSerializer.Serialize(
                snapshot,
                ConflictJsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private sealed record ItemWarehouseStockResponseAlias(
        Guid OriginalItemId,
        Guid CanonicalItemId,
        string WarehouseCode);

    private static void PopulateResolvedRentalManagementCompanyAcceptedRevisionAliases(
        SyncPushResult result,
        IReadOnlyDictionary<Guid, Guid> resolvedIncomingRentalManagementCompanyIds)
    {
        if (resolvedIncomingRentalManagementCompanyIds.Count == 0)
            return;

        var acceptedByCanonicalId = result.AcceptedRevisions
            .Where(revision => string.Equals(revision.EntityName, nameof(RentalManagementCompany), StringComparison.OrdinalIgnoreCase))
            .GroupBy(revision => revision.EntityId)
            .ToDictionary(group => group.Key, group => group.First());
        var alreadyRecordedIds = acceptedByCanonicalId.Keys.ToHashSet();

        foreach (var (originalId, canonicalId) in resolvedIncomingRentalManagementCompanyIds)
        {
            if (originalId == Guid.Empty ||
                originalId == canonicalId ||
                !acceptedByCanonicalId.TryGetValue(canonicalId, out var canonicalRevision) ||
                !alreadyRecordedIds.Add(originalId))
            {
                continue;
            }

            result.AcceptedRevisions.Add(new SyncAcceptedRevisionDto
            {
                EntityName = nameof(RentalManagementCompany),
                EntityId = originalId,
                Revision = canonicalRevision.Revision,
                UpdatedAtUtc = canonicalRevision.UpdatedAtUtc,
                IsDeleted = canonicalRevision.IsDeleted
            });
        }
    }

    private static void RemapResolvedRentalManagementCompanyServerConflicts(
        SyncPushResult result,
        IReadOnlyCollection<IncomingRentalManagementCompanyMutation> incomingMutations)
    {
        if (!incomingMutations.Any(mutation =>
                mutation.OriginalId != mutation.CanonicalId) ||
            result.Conflicts.Count == 0)
        {
            return;
        }

        var incomingByMutationId = incomingMutations
            .Where(mutation => !string.IsNullOrWhiteSpace(mutation.MutationId))
            .GroupBy(mutation => mutation.MutationId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var conflict in result.Conflicts)
        {
            if (!string.Equals(conflict.EntityName, nameof(RentalManagementCompany), StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(conflict.ServerJson) ||
                string.IsNullOrWhiteSpace(conflict.ClientJson))
            {
                continue;
            }

            var canonicalClient = JsonSerializer.Deserialize<RentalManagementCompanyDto>(
                conflict.ClientJson,
                ConflictJsonOptions);
            if (canonicalClient is null)
                continue;

            var mutationId = NormalizeMutationId(canonicalClient.MutationId);
            IncomingRentalManagementCompanyMutation? resolvedMutation = null;
            if (!string.IsNullOrWhiteSpace(mutationId))
            {
                if (!incomingByMutationId.TryGetValue(
                        mutationId,
                        out resolvedMutation))
                {
                    continue;
                }
            }
            else
            {
                var canonicalMatches = incomingMutations
                    .Where(mutation =>
                        mutation.CanonicalId == canonicalClient.Id)
                    .Take(2)
                    .ToList();
                if (canonicalMatches.Count == 1)
                    resolvedMutation = canonicalMatches[0];
            }

            if (resolvedMutation is null ||
                resolvedMutation.OriginalId == resolvedMutation.CanonicalId ||
                resolvedMutation.CanonicalId != canonicalClient.Id)
            {
                continue;
            }

            conflict.EntityId = resolvedMutation.OriginalId.ToString("D");
            conflict.ClientJson = resolvedMutation.OriginalClientJson;
        }
    }

    private sealed record IncomingRentalManagementCompanyMutation(
        Guid OriginalId,
        Guid CanonicalId,
        string MutationId,
        string OriginalClientJson,
        RentalManagementCompanyDto CanonicalDto);

    private List<RentalManagementCompanyDto> FilterAmbiguousRawIncomingRentalManagementCompanies(
        IReadOnlyCollection<RentalManagementCompanyDto> requestedCompanies,
        SyncPushResult result)
    {
        if (requestedCompanies.Count < 2)
            return requestedCompanies.ToList();

        var duplicateOriginalIdRows = requestedCompanies
            .Where(company => company.Id != Guid.Empty)
            .GroupBy(company => company.Id)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var duplicateMutationIdRows = requestedCompanies
            .Select(company => new
            {
                Company = company,
                MutationId = NormalizeMutationId(company.MutationId)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.MutationId))
            .GroupBy(entry => entry.MutationId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(entry => entry.Company)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        var validCompanies = new List<RentalManagementCompanyDto>(requestedCompanies.Count);
        foreach (var company in requestedCompanies)
        {
            var hasDuplicateOriginalId = duplicateOriginalIdRows.Contains(company);
            var hasDuplicateMutationId = duplicateMutationIdRows.Contains(company);
            if (!hasDuplicateOriginalId && !hasDuplicateMutationId)
            {
                validCompanies.Add(company);
                continue;
            }

            var reason = hasDuplicateOriginalId
                ? "Rental management company original id is duplicated within the incoming payload."
                : "Rental management company mutation id is duplicated within the incoming payload.";
            AddClientConflict(company, nameof(RentalManagementCompany), reason, result);
        }

        return validCompanies;
    }

    private List<RentalManagementCompanyDto> FilterAmbiguousIncomingRentalManagementCompanies(
        IReadOnlyCollection<RentalManagementCompanyDto> scopedCompanies,
        IReadOnlyCollection<IncomingRentalManagementCompanyMutation> incomingMutations,
        SyncPushResult result)
    {
        if (scopedCompanies.Count < 2 || incomingMutations.Count < 2)
            return scopedCompanies.ToList();

        var canonicalCollisionRows = incomingMutations
            .GroupBy(mutation => mutation.CanonicalId)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(mutation => mutation.CanonicalDto)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var duplicateMutationIdRows = incomingMutations
            .Where(mutation => !string.IsNullOrWhiteSpace(mutation.MutationId))
            .GroupBy(mutation => mutation.MutationId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(mutation => mutation.CanonicalDto)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var duplicateOriginalIdRows = incomingMutations
            .Where(mutation => mutation.OriginalId != Guid.Empty)
            .GroupBy(mutation => mutation.OriginalId)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(mutation => mutation.CanonicalDto)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        if (canonicalCollisionRows.Count == 0 &&
            duplicateMutationIdRows.Count == 0 &&
            duplicateOriginalIdRows.Count == 0)
        {
            return scopedCompanies.ToList();
        }

        foreach (var incomingMutation in incomingMutations)
        {
            var hasCanonicalCollision = canonicalCollisionRows.Contains(incomingMutation.CanonicalDto);
            var hasDuplicateMutationId = duplicateMutationIdRows.Contains(incomingMutation.CanonicalDto);
            var hasDuplicateOriginalId = duplicateOriginalIdRows.Contains(incomingMutation.CanonicalDto);
            if (!hasCanonicalCollision && !hasDuplicateMutationId && !hasDuplicateOriginalId)
                continue;

            var reason = hasDuplicateOriginalId
                ? "Rental management company original id is duplicated within the incoming payload."
                : hasCanonicalCollision
                    ? "Multiple incoming rental management company rows resolve to the same canonical company."
                    : "Rental management company mutation id is duplicated within the incoming payload.";
            AddIncomingRentalManagementCompanyClientConflict(incomingMutation, reason, result);
        }

        return scopedCompanies
            .Where(company =>
                !canonicalCollisionRows.Contains(company) &&
                !duplicateMutationIdRows.Contains(company) &&
                !duplicateOriginalIdRows.Contains(company))
            .ToList();
    }

    private void AddIncomingRentalManagementCompanyClientConflict(
        IncomingRentalManagementCompanyMutation incomingMutation,
        string reason,
        SyncPushResult result)
    {
        var conflict = new ConflictLog
        {
            UserId = _currentUserContext.UserId,
            Username = _currentUserContext.Username,
            EntityName = nameof(RentalManagementCompany),
            EntityId = incomingMutation.OriginalId.ToString("D"),
            ClientJson = incomingMutation.OriginalClientJson,
            ServerJson = string.Empty,
            Reason = reason,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.ConflictLogs.Add(conflict);
        result.ConflictCount++;
        result.Conflicts.Add(conflict.ToDto());
    }

    private async Task<List<TDto>> UpsertEntitiesAsync<TEntity, TDto>(
        IEnumerable<TDto> payload, DbSet<TEntity> dbSet,
        Action<TEntity, TDto> apply, Func<TDto, TEntity> create,
        SyncPushResult result, string deviceId, CancellationToken cancellationToken,
        bool preserveOriginalIncomingPayloadHashForReceipt = false)
        where TEntity : TrackedEntity
        where TDto : SyncEntityDto
    {
        var entityName = typeof(TEntity).Name;
        var payloadRows = payload as IReadOnlyCollection<TDto> ?? payload.ToList();
        var requestedEntityIds = payloadRows
            .Select(dto => dto.Id)
            .Where(entityId => entityId != Guid.Empty)
            .Distinct()
            .ToArray();
        var existingEntitiesById = new Dictionary<Guid, TEntity>();
        foreach (var entityIdBatch in requestedEntityIds.Chunk(500))
        {
            var batch = entityIdBatch.ToArray();
            var existingEntities = await dbSet
                .IgnoreQueryFilters()
                .Where(entity => batch.Contains(entity.Id))
                .ToListAsync(cancellationToken);
            foreach (var existingEntity in existingEntities)
                existingEntitiesById.TryAdd(existingEntity.Id, existingEntity);
        }

        var accepted = new List<TDto>();
        var exactReplayEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();
        var acceptedEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();
        foreach (var dto in payloadRows)
        {
            if (TryAcceptDuplicateMutation(
                    dto,
                    entityName,
                    result,
                    exactReplayEntityIdsForHistoricalConflictResolution))
            {
                continue;
            }

            existingEntitiesById.TryGetValue(dto.Id, out var entity);
            if (entity is null)
            {
                if (dto.IsDeleted)
                {
                    if (dto.Id == Guid.Empty)
                    {
                        AddClientConflict(dto, entityName, $"{entityName} delete requires an id.", result);
                        continue;
                    }

                    RegisterProcessedMutation(
                        dto,
                        entityName,
                        deviceId,
                        preserveOriginalIncomingPayloadHashForReceipt);
                    result.AcceptedRevisions.Add(new SyncAcceptedRevisionDto
                    {
                        EntityName = entityName,
                        EntityId = dto.Id,
                        Revision = Math.Max(dto.ExpectedRevision, dto.Revision),
                        UpdatedAtUtc = NormalizeUtc(dto.UpdatedAtUtc),
                        IsDeleted = true
                    });
                    accepted.Add(dto);
                    result.AcceptedCount++;
                    continue;
                }

                var newEntity = create(dto);
                apply(newEntity, dto);
                dbSet.Add(newEntity);
                if (newEntity.Id != Guid.Empty)
                    existingEntitiesById.TryAdd(newEntity.Id, newEntity);
                RegisterProcessedMutation(
                    dto,
                    entityName,
                    deviceId,
                    preserveOriginalIncomingPayloadHashForReceipt);
                if (newEntity.Id != Guid.Empty)
                    acceptedEntityIdsForHistoricalConflictResolution.Add(newEntity.Id);
                accepted.Add(dto);
                result.AcceptedCount++;
                continue;
            }

            if (await TryAcceptAlreadyDeletedMutationAsync(
                    entity,
                    dto,
                    entityName,
                    deviceId,
                    result,
                    cancellationToken,
                    preserveOriginalIncomingPayloadHashForReceipt))
                continue;

            if (HasExpectedRevisionConflict(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, entityName, BuildExpectedRevisionConflictReason(dto.ExpectedRevision, entity.Revision), result, cancellationToken);
                continue;
            }

            if (IsServerEntityNewer(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, entityName, "Server version is newer.", result, cancellationToken);
                continue;
            }

            apply(entity, dto);
            RegisterProcessedMutation(
                dto,
                entityName,
                deviceId,
                preserveOriginalIncomingPayloadHashForReceipt);
            if (entity.Id != Guid.Empty)
                acceptedEntityIdsForHistoricalConflictResolution.Add(entity.Id);
            accepted.Add(dto);
            result.AcceptedCount++;
        }

        await ResolveHistoricalConflictsAsync(
            entityName,
            exactReplayEntityIdsForHistoricalConflictResolution,
            "이미 처리된 동일 mutation 이 확인되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);

        if (acceptedEntityIdsForHistoricalConflictResolution.Count > 0)
        {
            await ResolveHistoricalConflictsAsync(
                entityName,
                acceptedEntityIdsForHistoricalConflictResolution,
                "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.",
                cancellationToken);
        }

        return accepted;
    }

    private async Task SynchronizeAcceptedCustomerRentalLinksAsync(
        IEnumerable<CustomerDto> acceptedCustomers,
        CancellationToken cancellationToken)
    {
        var customerIds = acceptedCustomers
            .Where(customer => !customer.IsDeleted && customer.Id != Guid.Empty)
            .Select(customer => customer.Id)
            .Distinct()
            .ToList();
        if (customerIds.Count == 0)
            return;

        var customers = await _dbContext.Customers
            .IgnoreQueryFilters()
            .Where(customer => customerIds.Contains(customer.Id) && !customer.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var customer in customers)
            await RentalCustomerLinkSynchronizer.SynchronizeAsync(_dbContext, customer, cancellationToken);
    }

    private async Task UpsertPriceGradeOptionsAsync(
        IEnumerable<PriceGradeOptionDto> payload,
        SyncPushResult result,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await UpsertSelectionOptionEntitiesAsync(
            payload,
            _dbContext.PriceGradeOptions,
            entity => entity.Name,
            entity => entity.IsActive,
            dto => dto.Name,
            (entity, dto) => entity.Apply(dto),
            dto => new PriceGradeOption { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id },
            nameof(PriceGradeOption),
            result,
            deviceId,
            cancellationToken);
    }

    private async Task UpsertCustomerCategoriesAsync(
        IEnumerable<CustomerCategoryDto> payload,
        SyncPushResult result,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var existingCategories = await _dbContext.CustomerCategories
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        var exactReplayEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();
        var acceptedEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();

        foreach (var dto in payload)
        {
            if (TryAcceptDuplicateMutation(
                    dto,
                    nameof(CustomerCategory),
                    result,
                    exactReplayEntityIdsForHistoricalConflictResolution))
                continue;

            var normalizedName = DefaultCustomerCategories.NormalizeName(dto.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                AddClientConflict(dto, nameof(CustomerCategory), "Customer category name is required.", result);
                continue;
            }

            var entity = existingCategories.FirstOrDefault(current => current.Id == dto.Id);
            if (entity is not null &&
                await TryAcceptAlreadyDeletedMutationAsync(
                    entity,
                    dto,
                    nameof(CustomerCategory),
                    deviceId,
                    result,
                    cancellationToken))
            {
                continue;
            }

            var activeDuplicate = dto.IsDeleted
                ? null
                : existingCategories
                    .Where(current => current.Id != dto.Id && !current.IsDeleted)
                    .FirstOrDefault(current =>
                        string.Equals(
                            DefaultCustomerCategories.NormalizeName(current.Name),
                            normalizedName,
                        StringComparison.CurrentCultureIgnoreCase));

            if (entity is null && dto.IsDeleted)
            {
                AddClientConflict(dto, nameof(CustomerCategory), "Customer category does not exist on server.", result);
                continue;
            }

            if (dto.IsDeleted && entity is not null && !entity.IsDeleted)
            {
                var referenceBlockMessage = await CustomerCategoryDeletionReferenceGuard.BuildReferenceBlockMessageAsync(
                    _dbContext,
                    entity.Id,
                    cancellationToken);
                if (referenceBlockMessage is not null)
                {
                    AddClientConflict(dto, nameof(CustomerCategory), referenceBlockMessage, result);
                    continue;
                }
            }

            if (activeDuplicate is not null)
            {
                AddClientConflict(dto, nameof(CustomerCategory), "Customer category name already exists.", result);
                continue;
            }

            dto.Name = normalizedName;
            if (entity is null)
            {
                var newEntity = new CustomerCategory { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
                newEntity.Apply(dto);
                _dbContext.CustomerCategories.Add(newEntity);
                existingCategories.Add(newEntity);
                RegisterProcessedMutation(dto, nameof(CustomerCategory), deviceId);
                acceptedEntityIdsForHistoricalConflictResolution.Add(newEntity.Id);
                result.AcceptedCount++;
                continue;
            }

            if (HasExpectedRevisionConflict(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, nameof(CustomerCategory), BuildExpectedRevisionConflictReason(dto.ExpectedRevision, entity.Revision), result, cancellationToken);
                continue;
            }

            if (IsServerEntityNewer(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, nameof(CustomerCategory), "Server version is newer.", result, cancellationToken);
                continue;
            }

            entity.Apply(dto);
            RegisterProcessedMutation(dto, nameof(CustomerCategory), deviceId);
            acceptedEntityIdsForHistoricalConflictResolution.Add(entity.Id);
            result.AcceptedCount++;
        }

        await ResolveHistoricalConflictsAsync(
            nameof(CustomerCategory),
            exactReplayEntityIdsForHistoricalConflictResolution,
            "이미 처리된 동일 mutation 이 확인되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);
        await ResolveHistoricalConflictsAsync(
            nameof(CustomerCategory),
            acceptedEntityIdsForHistoricalConflictResolution,
            "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);
    }

    private async Task UpsertTradeTypeOptionsAsync(
        IEnumerable<TradeTypeOptionDto> payload,
        SyncPushResult result,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var normalizedPayload = payload
            .Select(dto =>
            {
                if (!CustomerClassificationNormalizer.TryNormalizeTradeType(dto.Name, out var normalizedName))
                    return null;

                var definition = CustomerClassificationNormalizer.TradeTypeDefinition.Find(normalizedName);
                if (definition is null)
                    return null;

                dto.Name = definition.Name;
                dto.AllowsSales = definition.AllowsSales;
                dto.AllowsPurchase = definition.AllowsPurchase;
                dto.SortOrder = definition.SortOrder;
                return dto;
            })
            .Where(dto => dto is not null)
            .Cast<TradeTypeOptionDto>()
            .ToList();

        await UpsertSelectionOptionEntitiesAsync(
            normalizedPayload,
            _dbContext.TradeTypeOptions,
            entity => entity.Name,
            entity => entity.IsActive,
            dto => dto.Name,
            (entity, dto) => entity.Apply(dto),
            dto => new TradeTypeOption { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id },
            nameof(TradeTypeOption),
            result,
            deviceId,
            cancellationToken);
    }

    private async Task UpsertItemCategoryOptionsAsync(
        IEnumerable<ItemCategoryOptionDto> payload,
        SyncPushResult result,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await UpsertSelectionOptionEntitiesAsync(
            payload,
            _dbContext.ItemCategoryOptions,
            entity => entity.Name,
            entity => entity.IsActive,
            dto => dto.Name,
            (entity, dto) => entity.Apply(dto),
            dto => new ItemCategoryOption { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id },
            nameof(ItemCategoryOption),
            result,
            deviceId,
            cancellationToken);
    }

    private async Task UpsertUnitsAsync(
        IEnumerable<UnitDto> payload,
        SyncPushResult result,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var dedupedPayload = DeduplicatePulledUnits(payload.ToList());
        var existingUnits = await _dbContext.Units.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var exactReplayEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();
        var acceptedEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();

        foreach (var dto in dedupedPayload)
        {
            if (TryAcceptDuplicateMutation(
                    dto,
                    nameof(Unit),
                    result,
                    exactReplayEntityIdsForHistoricalConflictResolution))
                continue;

            var normalizedName = UnitCatalogNormalizer.Normalize(dto.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                AddClientConflict(dto, nameof(Unit), "Unit name is required.", result);
                continue;
            }

            var entityById = existingUnits.FirstOrDefault(current => current.Id == dto.Id);
            var entity = dto.IsDeleted
                ? entityById
                : entityById ?? existingUnits
                    .Where(current =>
                        string.Equals(
                            UnitCatalogNormalizer.Normalize(current.Name),
                            normalizedName,
                            StringComparison.Ordinal))
                    .OrderByDescending(current => current.UpdatedAtUtc)
                    .ThenByDescending(current => current.Revision)
                    .FirstOrDefault();

            if (entity is not null &&
                await TryAcceptAlreadyDeletedMutationAsync(
                    entity,
                    dto,
                    nameof(Unit),
                    deviceId,
                    result,
                    cancellationToken))
            {
                continue;
            }

            if (entity is null && dto.IsDeleted)
            {
                AddClientConflict(dto, nameof(Unit), "Unit does not exist on server.", result);
                continue;
            }

            if (dto.IsDeleted && entity is not null && !entity.IsDeleted)
            {
                var referenceBlockMessage = await UnitDeletionReferenceGuard.BuildReferenceBlockMessageAsync(
                    _dbContext,
                    entity.Name,
                    cancellationToken);
                if (referenceBlockMessage is not null)
                {
                    AddClientConflict(dto, nameof(Unit), referenceBlockMessage, result);
                    continue;
                }
            }

            if (entity is null)
            {
                var newEntity = new Unit { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
                newEntity.Apply(dto);
                newEntity.Name = normalizedName;
                _dbContext.Units.Add(newEntity);
                existingUnits.Add(newEntity);
                RegisterProcessedMutation(dto, nameof(Unit), deviceId);
                acceptedEntityIdsForHistoricalConflictResolution.Add(newEntity.Id);
                result.AcceptedCount++;
                continue;
            }

            if (HasExpectedRevisionConflict(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, nameof(Unit), BuildExpectedRevisionConflictReason(dto.ExpectedRevision, entity.Revision), result, cancellationToken);
                continue;
            }

            if (IsServerEntityNewer(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, nameof(Unit), "Server version is newer.", result, cancellationToken);
                continue;
            }

            entity.Apply(dto);
            entity.Name = normalizedName;
            RegisterProcessedMutation(dto, nameof(Unit), deviceId);
            acceptedEntityIdsForHistoricalConflictResolution.Add(entity.Id);
            result.AcceptedCount++;
        }

        await ResolveHistoricalConflictsAsync(
            nameof(Unit),
            exactReplayEntityIdsForHistoricalConflictResolution,
            "이미 처리된 동일 mutation 이 확인되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);
        await ResolveHistoricalConflictsAsync(
            nameof(Unit),
            acceptedEntityIdsForHistoricalConflictResolution,
            "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);
    }

    private async Task UpsertSelectionOptionEntitiesAsync<TEntity, TDto>(
        IEnumerable<TDto> payload,
        DbSet<TEntity> dbSet,
        Func<TEntity, string> entityNameSelector,
        Func<TEntity, bool> entityActiveSelector,
        Func<TDto, string> dtoNameSelector,
        Action<TEntity, TDto> apply,
        Func<TDto, TEntity> create,
        string entityName,
        SyncPushResult result,
        string deviceId,
        CancellationToken cancellationToken)
        where TEntity : TrackedEntity
        where TDto : SyncEntityDto
    {
        var existingEntities = await dbSet.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var exactReplayEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();
        var acceptedEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();

        foreach (var dto in payload)
        {
            if (TryAcceptDuplicateMutation(
                    dto,
                    entityName,
                    result,
                    exactReplayEntityIdsForHistoricalConflictResolution))
                continue;

            var normalizedName = NormalizeOptionName(dtoNameSelector(dto));
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                AddClientConflict(dto, entityName, "Option name is required.", result);
                continue;
            }

            var entity = existingEntities.FirstOrDefault(current => current.Id == dto.Id);
            if (entity is not null &&
                await TryAcceptAlreadyDeletedMutationAsync(
                    entity,
                    dto,
                    entityName,
                    deviceId,
                    result,
                    cancellationToken))
            {
                continue;
            }

            var duplicateByName = existingEntities
                .Where(current => current.Id != dto.Id)
                .Where(current =>
                    string.Equals(
                        NormalizeOptionName(entityNameSelector(current)),
                        normalizedName,
                        StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(current => !current.IsDeleted && entityActiveSelector(current))
                .ThenByDescending(current => current.UpdatedAtUtc)
                .ThenByDescending(current => current.Revision)
                .FirstOrDefault();

            if (entity is null && dto.IsDeleted)
            {
                AddClientConflict(dto, entityName, "Option does not exist on server.", result);
                continue;
            }

            if (dto.IsDeleted && entity is not null && !entity.IsDeleted)
            {
                var referenceBlockMessage = await SelectionOptionDeletionReferenceGuard.BuildBlockMessageAsync(
                    _dbContext,
                    entityName,
                    entityNameSelector(entity),
                    cancellationToken);
                if (referenceBlockMessage is not null)
                {
                    AddClientConflict(dto, entityName, referenceBlockMessage, result);
                    continue;
                }
            }

            if (!dto.IsDeleted && duplicateByName is not null)
            {
                var duplicateIsActive = !duplicateByName.IsDeleted && entityActiveSelector(duplicateByName);
                AddClientConflict(
                    dto,
                    entityName,
                    duplicateIsActive
                        ? "Option name already exists."
                        : "Option name exists on a deleted or inactive option. Restore the existing option before reusing the name.",
                    result);
                continue;
            }

            if (entity is null)
            {
                var newEntity = create(dto);
                apply(newEntity, dto);
                dbSet.Add(newEntity);
                existingEntities.Add(newEntity);
                RegisterProcessedMutation(dto, entityName, deviceId);
                acceptedEntityIdsForHistoricalConflictResolution.Add(newEntity.Id);
                result.AcceptedCount++;
                continue;
            }

            if (HasExpectedRevisionConflict(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, entityName, BuildExpectedRevisionConflictReason(dto.ExpectedRevision, entity.Revision), result, cancellationToken);
                continue;
            }

            if (IsServerEntityNewer(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, entityName, "Server version is newer.", result, cancellationToken);
                continue;
            }

            apply(entity, dto);
            RegisterProcessedMutation(dto, entityName, deviceId);
            acceptedEntityIdsForHistoricalConflictResolution.Add(entity.Id);
            result.AcceptedCount++;
        }

        await ResolveHistoricalConflictsAsync(
            entityName,
            exactReplayEntityIdsForHistoricalConflictResolution,
            "이미 처리된 동일 mutation 이 확인되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);
        await ResolveHistoricalConflictsAsync(
            entityName,
            acceptedEntityIdsForHistoricalConflictResolution,
            "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);
    }

    private async Task<(
        List<(Guid ProfileId, Guid? RunId)> RentalSettlementTargets,
        int AcceptedCount,
        Dictionary<
            InvoiceStockSnapshotService.InvoiceStockKey,
            decimal> StockDeltaDifferences)> UpsertInvoicesAsync(
        IEnumerable<InvoiceDto> payload,
        SyncPushResult result,
        string deviceId,
        IReadOnlySet<string> itemWarehouseStockKeysHandledByClient,
        CancellationToken cancellationToken)
    {
        var rentalSettlementTargets = new List<(Guid ProfileId, Guid? RunId)>();
        var acceptedDeletedInvoiceIds = new List<Guid>();
        var touchedVersionScopes =
            new HashSet<InvoiceVersionScopeKey>();
        var deletedVersionScopes =
            new HashSet<InvoiceVersionScopeKey>();
        var exactReplayEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();
        var acceptedEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();
        var createdInvoiceIdsInCurrentPush = new HashSet<Guid>();
        var acceptedStockDeltaDifferences =
            new Dictionary<
                InvoiceStockSnapshotService.InvoiceStockKey,
                decimal>();
        var acceptedCount = 0;
        var orderedPayload = payload
            .OrderBy(dto => dto.VersionNumber <= 0 ? 1 : dto.VersionNumber)
            .ThenBy(dto => dto.CreatedAtUtc)
            .ThenBy(dto => dto.Id)
            .ToList();
        var reservedInvoiceNumbersByCustomerId = orderedPayload
            .Where(dto => !string.IsNullOrWhiteSpace(dto.InvoiceNumber))
            .GroupBy(dto => dto.CustomerId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(dto => dto.InvoiceNumber)
                    .ToArray());
        var reservedTaxInvoiceNumbers = orderedPayload
            .Where(dto =>
                dto.TaxInvoiceIssued &&
                !string.IsNullOrWhiteSpace(dto.TaxInvoiceNumber))
            .Select(dto => dto.TaxInvoiceNumber.Trim())
            .ToArray();
        foreach (var dto in orderedPayload)
        {
            var duplicateMutationCountBefore =
                result.DuplicateMutationCount;
            if (TryAcceptDuplicateMutation(
                    dto,
                    nameof(Invoice),
                    result,
                    exactReplayEntityIdsForHistoricalConflictResolution))
            {
                if (result.DuplicateMutationCount >
                    duplicateMutationCountBefore)
                {
                    await PopulateExactReplayAssignedInvoiceNumbersAsync(
                        dto,
                        result,
                        cancellationToken);
                }

                continue;
            }

            dto.VersionNumber = dto.VersionNumber <= 0 ? 1 : dto.VersionNumber;
            var entity = await _dbContext.Invoices.IgnoreQueryFilters()
                .Include(x => x.Customer)
                .Include(x => x.Lines)
                .Include(x => x.Payments)
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);

            if (entity is null)
            {
                var invoiceId = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id;
                dto.Id = invoiceId;
                if (!dto.IsDeleted)
                {
                    if (!await ValidateNewInvoiceVersionAsync(
                            dto,
                            result,
                            createdInvoiceIdsInCurrentPush,
                            cancellationToken))
                    {
                        continue;
                    }
                }

                entity = new Invoice { Id = invoiceId };
                entity.Apply(dto);
                if (!entity.IsDeleted)
                {
                    if (entity.VersionGroupId == Guid.Empty)
                        entity.VersionGroupId = invoiceId;
                    entity.IsLatestVersion = true;
                }
                if (entity.IsDeleted)
                    IsolateNewInvoiceDeleteTombstone(entity);
                if (string.IsNullOrWhiteSpace(entity.InvoiceNumber))
                {
                    entity.InvoiceNumber = await _invoiceNumberService.GenerateAsync(
                        entity.CustomerId,
                        entity.InvoiceDate,
                        reservedInvoiceNumbersByCustomerId.GetValueOrDefault(
                            entity.CustomerId,
                            []),
                        cancellationToken);
                    result.AssignedInvoiceNumbers[dto.Id] = entity.InvoiceNumber;
                }
                var assignedTaxInvoiceNumber =
                    await TaxInvoiceNumberAssignmentService.EnsureAssignedAsync(
                        _dbContext,
                        entity,
                        reservedTaxInvoiceNumbers,
                        cancellationToken);
                if (!string.IsNullOrWhiteSpace(assignedTaxInvoiceNumber))
                    result.AssignedTaxInvoiceNumbers[dto.Id] = assignedTaxInvoiceNumber;

                ApplyInvoiceLines(entity, dto.Lines ?? []);
                var createdStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);
                AccumulateStockDeltaDifferences(
                    acceptedStockDeltaDifferences,
                    new Dictionary<
                        InvoiceStockSnapshotService.InvoiceStockKey,
                        decimal>(),
                    createdStockDeltas);
                await ApplyStockSnapshotDeltaAsync(
                    new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
                    createdStockDeltas,
                    itemWarehouseStockKeysHandledByClient,
                    cancellationToken);
                _dbContext.Invoices.Add(entity);
                createdInvoiceIdsInCurrentPush.Add(entity.Id);
                AddTouchedInvoiceVersionScope(touchedVersionScopes, entity);
                AddRentalSettlementTarget(rentalSettlementTargets, entity.LinkedRentalBillingProfileId, entity.LinkedRentalBillingRunId);
                if (entity.IsDeleted)
                {
                    acceptedDeletedInvoiceIds.Add(entity.Id);
                    AddTouchedInvoiceVersionScope(deletedVersionScopes, entity);
                }
                RegisterProcessedMutation(dto, nameof(Invoice), deviceId);
                acceptedEntityIdsForHistoricalConflictResolution.Add(entity.Id);
                acceptedCount++;
                result.AcceptedCount++;
                continue;
            }

            if (!CanWriteInvoiceUsingResolvedVersionScope(entity))
            {
                AddClientConflict(dto, nameof(Invoice), "Current account cannot modify this office scope.", result);
                continue;
            }

            var repairsAlreadyDeletedLatestVersion =
                dto.IsDeleted &&
                entity.IsDeleted &&
                entity.IsLatestVersion;
            if (await TryAcceptAlreadyDeletedMutationAsync(entity, dto, nameof(Invoice), deviceId, result, cancellationToken))
            {
                if (repairsAlreadyDeletedLatestVersion)
                {
                    entity.IsLatestVersion = false;
                    AddRentalSettlementTarget(
                        rentalSettlementTargets,
                        entity.LinkedRentalBillingProfileId,
                        entity.LinkedRentalBillingRunId);
                    AddTouchedInvoiceVersionScope(
                        touchedVersionScopes,
                        entity);
                    AddTouchedInvoiceVersionScope(
                        deletedVersionScopes,
                        entity);
                    acceptedCount++;
                }
                continue;
            }

            if (HasExpectedRevisionConflict(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, nameof(Invoice), BuildExpectedRevisionConflictReason(dto.ExpectedRevision, entity.Revision), result, cancellationToken);
                continue;
            }

            if (IsServerEntityNewer(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, nameof(Invoice), "Server version is newer.", result, cancellationToken);
                continue;
            }

            if (!dto.IsDeleted &&
                await InvoiceStructuralMutationGuard.ShouldProtectExistingInvoiceFromSameIdStructuralMutationAsync(_dbContext, entity, dto, cancellationToken) &&
                InvoiceStructuralMutationGuard.HasSameIdInvoiceStructuralMutation(entity, dto))
            {
                AddClientConflict(
                    dto,
                    nameof(Invoice),
                    ApiConflictReasonTranslator.ProtectedInvoiceSameIdStructuralMutation,
                    result);
                continue;
            }

            if (!dto.IsDeleted &&
                !ValidateExistingInvoiceVersionMetadata(
                    entity,
                    dto,
                    result))
            {
                continue;
            }

            var previousStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);
            AddRentalSettlementTarget(rentalSettlementTargets, entity.LinkedRentalBillingProfileId, entity.LinkedRentalBillingRunId);
            if (dto.IsDeleted)
            {
                SoftDeleteInvoicePreservingSnapshot(entity);
            }
            else
            {
                var preservedVersionGroupId =
                    entity.VersionGroupId;
                var preservedVersionNumber =
                    entity.VersionNumber;
                var preservedPreviousVersionId =
                    entity.PreviousVersionId;
                var preservedIsLatestVersion =
                    entity.IsLatestVersion;
                entity.Apply(dto);
                entity.VersionGroupId =
                    preservedVersionGroupId;
                entity.VersionNumber =
                    preservedVersionNumber;
                entity.PreviousVersionId =
                    preservedPreviousVersionId;
                entity.IsLatestVersion =
                    preservedIsLatestVersion;
                if (string.IsNullOrWhiteSpace(entity.InvoiceNumber))
                {
                    entity.InvoiceNumber = await _invoiceNumberService.GenerateAsync(
                        entity.CustomerId,
                        entity.InvoiceDate,
                        reservedInvoiceNumbersByCustomerId.GetValueOrDefault(
                            entity.CustomerId,
                            []),
                        cancellationToken);
                    result.AssignedInvoiceNumbers[dto.Id] = entity.InvoiceNumber;
                }
                var updatedTaxInvoiceNumber =
                    await TaxInvoiceNumberAssignmentService.EnsureAssignedAsync(
                        _dbContext,
                        entity,
                        reservedTaxInvoiceNumbers,
                        cancellationToken);
                if (!string.IsNullOrWhiteSpace(updatedTaxInvoiceNumber))
                    result.AssignedTaxInvoiceNumbers[dto.Id] = updatedTaxInvoiceNumber;

                _dbContext.InvoiceLines.RemoveRange(entity.Lines);
                entity.Lines.Clear();
                ApplyInvoiceLines(entity, dto.Lines ?? []);
            }

            AddTouchedInvoiceVersionScope(touchedVersionScopes, entity);
            AddRentalSettlementTarget(rentalSettlementTargets, entity.LinkedRentalBillingProfileId, entity.LinkedRentalBillingRunId);
            if (entity.IsDeleted)
            {
                acceptedDeletedInvoiceIds.Add(entity.Id);
                AddTouchedInvoiceVersionScope(deletedVersionScopes, entity);
            }

            var updatedInvoiceStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);
            AccumulateStockDeltaDifferences(
                acceptedStockDeltaDifferences,
                previousStockDeltas,
                updatedInvoiceStockDeltas);
            await ApplyStockSnapshotDeltaAsync(
                previousStockDeltas,
                updatedInvoiceStockDeltas,
                itemWarehouseStockKeysHandledByClient,
                cancellationToken);
            RegisterProcessedMutation(dto, nameof(Invoice), deviceId);
            acceptedEntityIdsForHistoricalConflictResolution.Add(entity.Id);
            acceptedCount++;
            result.AcceptedCount++;
        }

        await ResolveHistoricalConflictsAsync(
            nameof(Invoice),
            exactReplayEntityIdsForHistoricalConflictResolution,
            "이미 처리된 동일 mutation 이 확인되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);
        await ResolveHistoricalConflictsAsync(
            nameof(Invoice),
            acceptedEntityIdsForHistoricalConflictResolution,
            "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);

        await NormalizeLatestInvoiceVersionsAsync(
            touchedVersionScopes,
            deletedVersionScopes,
            itemWarehouseStockKeysHandledByClient,
            acceptedStockDeltaDifferences,
            rentalSettlementTargets,
            cancellationToken);

        var distinctDeletedInvoiceIds = acceptedDeletedInvoiceIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (distinctDeletedInvoiceIds.Count > 0)
        {
            rentalSettlementTargets.AddRange(await _rentalSettlementRecalculationService
                .LoadRentalSettlementTargetsForInvoiceDeleteAsync(distinctDeletedInvoiceIds, cancellationToken));
            await _rentalSettlementRecalculationService.DetachTransactionsFromInvoicesAsync(distinctDeletedInvoiceIds, cancellationToken);
            await _rentalSettlementRecalculationService.MarkPaymentsDeletedForInvoicesAsync(distinctDeletedInvoiceIds, cancellationToken);
        }

        return (
            rentalSettlementTargets.Distinct().ToList(),
            acceptedCount,
            acceptedStockDeltaDifferences);
    }

    private async Task PopulateExactReplayAssignedInvoiceNumbersAsync(
        InvoiceDto dto,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        if (dto.Id == Guid.Empty ||
            dto.IsDeleted ||
            (!string.IsNullOrWhiteSpace(dto.InvoiceNumber) &&
             (!dto.TaxInvoiceIssued ||
              !string.IsNullOrWhiteSpace(dto.TaxInvoiceNumber))))
        {
            return;
        }

        var stored = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => invoice.Id == dto.Id)
            .Select(invoice => new
            {
                invoice.InvoiceNumber,
                invoice.TaxInvoiceIssued,
                invoice.TaxInvoiceNumber
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (stored is null)
            return;

        if (string.IsNullOrWhiteSpace(dto.InvoiceNumber) &&
            !string.IsNullOrWhiteSpace(stored.InvoiceNumber))
        {
            result.AssignedInvoiceNumbers[dto.Id] =
                stored.InvoiceNumber;
        }

        if (dto.TaxInvoiceIssued &&
            string.IsNullOrWhiteSpace(dto.TaxInvoiceNumber) &&
            stored.TaxInvoiceIssued &&
            !string.IsNullOrWhiteSpace(stored.TaxInvoiceNumber))
        {
            result.AssignedTaxInvoiceNumbers[dto.Id] =
                stored.TaxInvoiceNumber;
        }
    }

    private async Task<bool> ValidateNewInvoiceVersionAsync(
            InvoiceDto dto,
            SyncPushResult result,
            IReadOnlySet<Guid> createdInvoiceIdsInCurrentPush,
            CancellationToken cancellationToken)
    {
        var requestedCustomer = dto.CustomerId == Guid.Empty
            ? null
            : await _dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    customer => customer.Id == dto.CustomerId,
                    cancellationToken);
        var requestedScope =
            BuildInvoiceVersionScopeKey(
                dto,
                requestedCustomer);
        if (!IsInvoiceVersionScopeInternallyConsistent(
                requestedScope))
        {
            AddClientConflict(
                dto,
                nameof(Invoice),
                "Invoice tenant and office scope values are inconsistent.",
                result);
            return false;
        }

        var versionGroupMembers = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .Where(invoice =>
                invoice.VersionGroupId ==
                    requestedScope.VersionGroupId ||
                (invoice.VersionGroupId == Guid.Empty &&
                 invoice.Id == requestedScope.VersionGroupId))
            .ToListAsync(cancellationToken);
        foreach (var localInvoice in _dbContext.Invoices.Local.Where(
                     invoice =>
                         invoice.Id != Guid.Empty &&
                         (invoice.VersionGroupId ==
                              requestedScope.VersionGroupId ||
                          (invoice.VersionGroupId == Guid.Empty &&
                           invoice.Id ==
                               requestedScope.VersionGroupId))))
        {
            if (versionGroupMembers.All(
                    invoice => invoice.Id != localInvoice.Id))
            {
                versionGroupMembers.Add(localInvoice);
            }
        }

        var scopedVersionGroupMembers =
            versionGroupMembers
                .Where(invoice =>
                    BuildInvoiceVersionScopeKey(invoice) ==
                    requestedScope)
                .ToList();
        if (scopedVersionGroupMembers.Count == 0 &&
            versionGroupMembers.Count > 0)
        {
            AddClientConflict(
                dto,
                nameof(Invoice),
                "Invoice version group is outside the matching customer, tenant, or office scope.",
                result);
            return false;
        }

        if (scopedVersionGroupMembers.Count == 0)
        {
            if (requestedScope.VersionGroupId != dto.Id ||
                dto.VersionNumber != 1 ||
                NormalizeInvoiceVersionReference(
                    dto.PreviousVersionId).HasValue)
            {
                AddClientConflict(
                    dto,
                    nameof(Invoice),
                    "A new invoice version group must use its own invoice id, start at version 1, and have no previous version.",
                    result);
                return false;
            }

            return true;
        }

        var previousVersionId =
            NormalizeInvoiceVersionReference(
                dto.PreviousVersionId);
        var activeVersionGroupMembers =
            scopedVersionGroupMembers
                .Where(invoice => !invoice.IsDeleted)
                .ToList();
        if (!previousVersionId.HasValue ||
            activeVersionGroupMembers.Count == 0)
        {
            AddClientConflict(
                dto,
                nameof(Invoice),
                "A new invoice version must reference the current latest version in the same group and scope.",
                result);
            return false;
        }

        var currentMaxVersion =
            activeVersionGroupMembers.Max(
                invoice =>
                    Math.Max(
                        1,
                        invoice.VersionNumber));
        var previousVersion =
            activeVersionGroupMembers.FirstOrDefault(
                invoice =>
                    invoice.Id ==
                    previousVersionId.Value);
        if (previousVersion is null ||
            !previousVersion.IsLatestVersion ||
            Math.Max(1, previousVersion.VersionNumber) !=
                currentMaxVersion ||
            dto.VersionNumber != currentMaxVersion + 1)
        {
            AddClientConflict(
                dto,
                nameof(Invoice),
                "Previous invoice version must be the current scoped latest version and the new version number must be consecutive.",
                result);
            return false;
        }

        var requestedBaseRevision =
            dto.ExpectedRevision > 0
                ? dto.ExpectedRevision
                : dto.Revision;
        var predecessorWasAddedInCurrentPush =
            createdInvoiceIdsInCurrentPush.Contains(previousVersion.Id) &&
            _dbContext.Entry(previousVersion).State == EntityState.Added;
        if (!predecessorWasAddedInCurrentPush &&
            ((previousVersion.Revision > 0 &&
              requestedBaseRevision !=
                  previousVersion.Revision) ||
             (previousVersion.Revision <= 0 &&
              requestedBaseRevision != 0)))
        {
            AddClientConflict(
                dto,
                nameof(Invoice),
                "New invoice version base revision does not match the current scoped latest version.",
                result);
            return false;
        }

        var latestFlagChangingInvoiceIds =
            activeVersionGroupMembers
                .Where(invoice => invoice.IsLatestVersion)
                .Select(invoice => invoice.Id)
                .ToHashSet();
        if (!await IsInvoiceVersionNormalizationScopeWritableAsync(
                activeVersionGroupMembers,
                latestFlagChangingInvoiceIds,
                latestFlagChangingInvoiceIds,
                cancellationToken))
        {
            AddClientConflict(
                dto,
                nameof(Invoice),
                "Invoice version normalization includes an invoice, warehouse, item, payment, transaction, or rental settlement reference outside the writable scope.",
                result);
            return false;
        }

        return true;
    }

    private bool ValidateExistingInvoiceVersionMetadata(
        Invoice existing,
        InvoiceDto dto,
        SyncPushResult result)
    {
        var existingVersionGroupId =
            existing.VersionGroupId == Guid.Empty
                ? existing.Id
                : existing.VersionGroupId;
        var requestedVersionGroupId =
            dto.VersionGroupId == Guid.Empty
                ? existingVersionGroupId
                : dto.VersionGroupId;
        if (existingVersionGroupId ==
                requestedVersionGroupId &&
            Math.Max(1, existing.VersionNumber) ==
                Math.Max(1, dto.VersionNumber) &&
            NormalizeInvoiceVersionReference(
                existing.PreviousVersionId) ==
                NormalizeInvoiceVersionReference(
                    dto.PreviousVersionId))
        {
            return true;
        }

        AddClientConflict(
            dto,
            nameof(Invoice),
            "Existing invoice version metadata cannot be changed in place.",
            result);
        return false;
    }

    private static Guid? NormalizeInvoiceVersionReference(
        Guid? value)
        => value.HasValue && value.Value != Guid.Empty
            ? value.Value
            : null;

    private static InvoiceVersionScopeKey
        BuildInvoiceVersionScopeKey(Invoice invoice)
        => BuildInvoiceVersionScopeKey(
            invoice.VersionGroupId == Guid.Empty
                ? invoice.Id
                : invoice.VersionGroupId,
            invoice.CustomerId,
            invoice.TenantCode,
            invoice.OfficeCode,
            invoice.ResponsibleOfficeCode,
            invoice.Customer);

    private static InvoiceVersionScopeKey
        BuildInvoiceVersionScopeKey(
            InvoiceDto invoice,
            Customer? customer)
        => BuildInvoiceVersionScopeKey(
            invoice.VersionGroupId == Guid.Empty
                ? invoice.Id
                : invoice.VersionGroupId,
            invoice.CustomerId,
            invoice.TenantCode,
            invoice.OfficeCode,
            invoice.ResponsibleOfficeCode,
            customer);

    private static InvoiceVersionScopeKey
        BuildInvoiceVersionScopeKey(
            Guid versionGroupId,
            Guid customerId,
            string? tenantCode,
            string? officeCode,
            string? responsibleOfficeCode,
            Customer? customer)
    {
        var customerScope =
            ResolveFinalInvoiceCustomerScope(customer);
        var normalizedResponsibleOfficeCode =
            OfficeCodeCatalog.TryNormalize(
                responsibleOfficeCode,
                out var explicitResponsibleOfficeCode)
                ? explicitResponsibleOfficeCode
                : customerScope.ResponsibleOfficeCode;
        var normalizedOwningOfficeCode =
            OfficeCodeCatalog.TryNormalizeScope(
                officeCode,
                out var explicitOwningOfficeCode)
                ? explicitOwningOfficeCode
                : customerScope.OwningOfficeCode;
        var normalizedTenantCode =
            TenantScopeCatalog.TryNormalizeTenantCode(
                tenantCode,
                out var explicitTenantCode)
                ? explicitTenantCode
                : customerScope.TenantCode;
        return new InvoiceVersionScopeKey(
            versionGroupId,
            customerId,
            normalizedTenantCode,
            normalizedOwningOfficeCode,
            normalizedResponsibleOfficeCode);
    }

    private static InvoiceVersionOperationalScope
        ResolveFinalInvoiceCustomerScope(
            Customer? customer)
    {
        if (customer is null)
        {
            return new InvoiceVersionOperationalScope(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet);
        }

        var backfilledOfficeCode =
            OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                customer.OfficeCode,
                OfficeCodeCatalog.Shared);
        var backfilledTenantCode =
            TenantScopeCatalog
                .NormalizeTenantCodeForOfficeOrDefault(
                    customer.TenantCode,
                    backfilledOfficeCode,
                    TenantScopeCatalog.UsenetGroup,
                    backfilledOfficeCode);
        var responsibleOfficeCode =
            OfficeCodeCatalog.NormalizeOfficeCodeLoose(
                customer.ResponsibleOfficeCode,
                backfilledOfficeCode,
                OfficeCodeCatalog.Usenet);
        var owningOfficeCode =
            OfficeCodeCatalog.ResolveOwningOfficeCode(
                backfilledOfficeCode,
                responsibleOfficeCode,
                OfficeCodeCatalog.Shared);
        var normalizedTenantCode =
            TenantScopeCatalog
                .NormalizeTenantCodeForOfficeOrDefault(
                    backfilledTenantCode,
                    owningOfficeCode,
                    backfilledTenantCode,
                    responsibleOfficeCode);
        return new InvoiceVersionOperationalScope(
            normalizedTenantCode,
            owningOfficeCode,
            responsibleOfficeCode);
    }

    private static bool IsInvoiceVersionScopeInternallyConsistent(
        InvoiceVersionScopeKey scope)
    {
        if (!TenantScopeCatalog.TryNormalizeTenantCode(
                scope.TenantCode,
                out var tenantCode))
        {
            return false;
        }

        if (OfficeCodeCatalog.TryNormalize(
                scope.OwningOfficeCode,
                out var owningOfficeCode) &&
            !TenantScopeCatalog.TenantContainsOffice(
                tenantCode,
                owningOfficeCode))
        {
            return false;
        }

        return !OfficeCodeCatalog.TryNormalize(
                   scope.ResponsibleOfficeCode,
                   out var responsibleOfficeCode) ||
               TenantScopeCatalog.TenantContainsOffice(
                   tenantCode,
                   responsibleOfficeCode);
    }

    private bool CanWriteInvoiceUsingResolvedVersionScope(
        Invoice invoice)
    {
        var scope = BuildInvoiceVersionScopeKey(invoice);
        return _officeScopeService.CanWriteOfficeForInvoices(
            scope.ResponsibleOfficeCode,
            scope.TenantCode,
            scope.OwningOfficeCode);
    }

    private async Task<bool> ValidateInvoiceVersionNormalizationScopeAsync(
        Invoice anchor,
        InvoiceDto dto,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var versionScope = BuildInvoiceVersionScopeKey(anchor);
        var participants = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .Where(invoice =>
                !invoice.IsDeleted &&
                (invoice.VersionGroupId ==
                     versionScope.VersionGroupId ||
                 (invoice.VersionGroupId == Guid.Empty &&
                  invoice.Id == versionScope.VersionGroupId)))
            .ToListAsync(cancellationToken);
        foreach (var localInvoice in _dbContext.Invoices.Local.Where(
                     invoice =>
                         invoice.Id != Guid.Empty &&
                         !invoice.IsDeleted &&
                         (invoice.VersionGroupId ==
                              versionScope.VersionGroupId ||
                          (invoice.VersionGroupId == Guid.Empty &&
                           invoice.Id ==
                               versionScope.VersionGroupId))))
        {
            if (participants.All(
                    invoice => invoice.Id != localInvoice.Id))
            {
                participants.Add(localInvoice);
            }
        }

        participants = participants
            .Where(invoice =>
                BuildInvoiceVersionScopeKey(invoice) ==
                versionScope)
            .ToList();
        if (participants.All(
                invoice => invoice.Id != anchor.Id))
        {
            participants.Add(anchor);
        }

        var activeAfterMutation = participants
            .Where(invoice =>
                !invoice.IsDeleted &&
                (invoice.Id != anchor.Id ||
                 !dto.IsDeleted))
            .ToList();
        if (!dto.IsDeleted &&
            activeAfterMutation.All(
                invoice => invoice.Id != anchor.Id))
        {
            activeAfterMutation.Add(anchor);
        }

        var latestFlagChangingInvoiceIds =
            GetLatestFlagChangingInvoiceIds(
                activeAfterMutation);
        if (dto.IsDeleted &&
            anchor.IsLatestVersion)
        {
            latestFlagChangingInvoiceIds.Add(anchor.Id);
        }

        var stockScopeInvoiceIds =
            latestFlagChangingInvoiceIds.ToHashSet();
        stockScopeInvoiceIds.Add(anchor.Id);
        var financialEffectInvoiceIds =
            latestFlagChangingInvoiceIds.ToHashSet();
        if (dto.IsDeleted &&
            !anchor.IsDeleted)
        {
            financialEffectInvoiceIds.Add(anchor.Id);
        }

        if (await IsInvoiceVersionNormalizationScopeWritableAsync(
                participants,
                stockScopeInvoiceIds,
                financialEffectInvoiceIds,
                cancellationToken))
        {
            return true;
        }

        AddClientConflict(
            dto,
            nameof(Invoice),
            "Invoice version normalization includes an invoice, warehouse, item, payment, transaction, or rental settlement reference outside the writable scope.",
            result);
        return false;
    }

    private static HashSet<Guid>
        GetLatestFlagChangingInvoiceIds(
            IReadOnlyCollection<Invoice> activeCandidates)
    {
        if (activeCandidates.Count == 0)
            return [];

        var latest = activeCandidates
            .OrderByDescending(invoice =>
                Math.Max(1, invoice.VersionNumber))
            .ThenByDescending(invoice => invoice.Id)
            .First();
        return activeCandidates
            .Where(invoice =>
                invoice.IsLatestVersion !=
                (invoice.Id == latest.Id))
            .Select(invoice => invoice.Id)
            .ToHashSet();
    }

    private async Task<bool>
        IsInvoiceVersionNormalizationScopeWritableAsync(
            IReadOnlyCollection<Invoice> participants,
            IReadOnlyCollection<Guid> stockScopeInvoiceIds,
            IReadOnlyCollection<Guid> financialEffectInvoiceIds,
            CancellationToken cancellationToken)
    {
        var participantsById = participants
            .Where(invoice => invoice.Id != Guid.Empty)
            .GroupBy(invoice => invoice.Id)
            .ToDictionary(
                group => group.Key,
                group => group.First());
        var stockScopeIds = stockScopeInvoiceIds
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var financialEffectIds = financialEffectInvoiceIds
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var requiredParticipantIds = stockScopeIds
            .Concat(financialEffectIds)
            .ToHashSet();
        if (requiredParticipantIds.Any(id =>
                !participantsById.ContainsKey(id)))
        {
            return false;
        }

        var stockScopeParticipants = stockScopeIds
            .Select(id => participantsById[id])
            .ToList();
        foreach (var invoice in stockScopeParticipants)
        {
            var scope = BuildInvoiceVersionScopeKey(invoice);
            if (!_officeScopeService.CanWriteOfficeForInvoices(
                    scope.ResponsibleOfficeCode,
                    scope.TenantCode,
                    scope.OwningOfficeCode))
            {
                return false;
            }

            var warehouseCode =
                OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
                    invoice.SourceWarehouseCode,
                    scope.ResponsibleOfficeCode,
                    scope.OwningOfficeCode);
            if (!_officeScopeService.CanWriteWarehouse(
                    warehouseCode,
                    scope.OwningOfficeCode))
            {
                return false;
            }
        }

        var itemIds = stockScopeParticipants
            .SelectMany(invoice => invoice.Lines)
            .Where(line =>
                !line.IsDeleted &&
                line.ItemId.HasValue &&
                line.ItemId.Value != Guid.Empty)
            .Select(line => line.ItemId!.Value)
            .Distinct()
            .ToList();
        if (itemIds.Count > 0)
        {
            var items = await _dbContext.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item =>
                    itemIds.Contains(item.Id) &&
                    !item.IsDeleted)
                .Select(item => new
                {
                    item.Id,
                    item.OfficeCode,
                    item.TenantCode
                })
                .ToDictionaryAsync(
                    item => item.Id,
                    cancellationToken);
            foreach (var itemId in itemIds)
            {
                if (!items.TryGetValue(itemId, out var item) ||
                    !_officeScopeService.CanReadOfficeForItems(
                        item.OfficeCode,
                        item.TenantCode))
                {
                    return false;
                }
            }
        }

        if (financialEffectIds.Count == 0)
            return true;

        var financialEffectParticipants =
            financialEffectIds
                .Select(id => participantsById[id])
                .ToList();
        foreach (var invoice in financialEffectParticipants)
        {
            if (invoice.LinkedRentalBillingRunId.HasValue &&
                invoice.LinkedRentalBillingRunId.Value !=
                    Guid.Empty &&
                (!invoice.LinkedRentalBillingProfileId.HasValue ||
                 invoice.LinkedRentalBillingProfileId.Value ==
                     Guid.Empty))
            {
                return false;
            }
        }

        var activePaymentInvoiceIds = await _dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(payment =>
                !payment.IsDeleted &&
                financialEffectIds.Contains(
                    payment.InvoiceId))
            .Select(payment => payment.InvoiceId)
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var paymentInvoiceId in activePaymentInvoiceIds)
        {
            var paymentInvoice =
                participantsById[paymentInvoiceId];
            var paymentInvoiceScope =
                BuildInvoiceVersionScopeKey(paymentInvoice);
            if (!_officeScopeService.CanWriteOfficeForPayments(
                    paymentInvoiceScope.ResponsibleOfficeCode,
                    paymentInvoiceScope.TenantCode,
                    paymentInvoiceScope.OwningOfficeCode))
            {
                return false;
            }
        }

        if (!HasPermission(PermissionNames.PaymentEdit) &&
            await HasActivePaymentSideEffectsForInvoiceDeleteAsync(
                financialEffectIds,
                cancellationToken))
        {
            return false;
        }

        var linkedTransactions = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedInvoiceId.HasValue &&
                financialEffectIds.Contains(
                    transaction.LinkedInvoiceId.Value))
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
                return false;
            }
        }

        var profileIds = financialEffectParticipants
            .Where(invoice =>
                invoice.LinkedRentalBillingProfileId.HasValue &&
                invoice.LinkedRentalBillingProfileId.Value !=
                    Guid.Empty)
            .Select(invoice =>
                invoice.LinkedRentalBillingProfileId!.Value)
            .Concat(
                linkedTransactions
                    .Where(transaction =>
                        transaction
                            .LinkedRentalBillingProfileId
                            .HasValue &&
                        transaction
                            .LinkedRentalBillingProfileId
                            .Value != Guid.Empty)
                    .Select(transaction =>
                        transaction
                            .LinkedRentalBillingProfileId!
                            .Value))
            .Distinct()
            .ToList();
        if (profileIds.Count == 0)
            return true;

        var profiles = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => profileIds.Contains(profile.Id))
            .ToDictionaryAsync(profile => profile.Id, cancellationToken);
        foreach (var profileId in profileIds)
        {
            if (!profiles.TryGetValue(
                    profileId,
                    out var profile) ||
                profile.IsDeleted ||
                !_officeScopeService.CanWriteOfficeForRentals(
                    profile.ResponsibleOfficeCode,
                    profile.TenantCode,
                    profile.OfficeCode))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddTouchedInvoiceVersionScope(
        ISet<InvoiceVersionScopeKey> versionScopes,
        Invoice invoice)
    {
        var versionScope = BuildInvoiceVersionScopeKey(invoice);
        if (versionScope.VersionGroupId != Guid.Empty)
            versionScopes.Add(versionScope);
    }

    private async Task<bool> NormalizeLatestInvoiceVersionsAsync(
        IReadOnlyCollection<InvoiceVersionScopeKey> versionScopes,
        IReadOnlyCollection<InvoiceVersionScopeKey> singletonPromotionScopes,
        IReadOnlySet<string> itemWarehouseStockKeysHandledByClient,
        IDictionary<
            InvoiceStockSnapshotService.InvoiceStockKey,
            decimal> acceptedStockDeltaDifferences,
        List<(Guid ProfileId, Guid? RunId)> rentalSettlementTargets,
        CancellationToken cancellationToken)
    {
        if (versionScopes.Count == 0)
            return false;

        var changedAnyVersionGroup = false;
        foreach (var versionScope in versionScopes
                     .Where(scope => scope.VersionGroupId != Guid.Empty)
                     .Distinct())
        {
            var invoices = await _dbContext.Invoices.IgnoreQueryFilters()
                .Include(invoice => invoice.Customer)
                .Include(invoice => invoice.Lines)
                .Where(invoice => !invoice.IsDeleted &&
                                  (invoice.VersionGroupId == versionScope.VersionGroupId ||
                                   (invoice.VersionGroupId == Guid.Empty &&
                                    invoice.Id == versionScope.VersionGroupId)))
                .ToListAsync(cancellationToken);
            foreach (var localInvoice in _dbContext.Invoices.Local
                         .Where(invoice => invoice.Id != Guid.Empty &&
                                           !invoice.IsDeleted &&
                                           (invoice.VersionGroupId == versionScope.VersionGroupId ||
                                            (invoice.VersionGroupId == Guid.Empty &&
                                             invoice.Id == versionScope.VersionGroupId))))
            {
                if (invoices.All(invoice => invoice.Id != localInvoice.Id))
                    invoices.Add(localInvoice);
            }
            invoices = invoices
                .Where(invoice =>
                    !invoice.IsDeleted &&
                    BuildInvoiceVersionScopeKey(invoice) == versionScope)
                .ToList();
            if (invoices.Count == 0 ||
                (invoices.Count == 1 &&
                 !singletonPromotionScopes.Contains(versionScope)))
                continue;
            var latest = invoices
                .OrderByDescending(invoice =>
                    Math.Max(1, invoice.VersionNumber))
                .ThenByDescending(invoice => invoice.Id)
                .First();
            var latestFlagChangingInvoiceIds = invoices
                .Where(invoice =>
                    invoice.IsLatestVersion !=
                    (invoice.Id == latest.Id))
                .Select(invoice => invoice.Id)
                .ToHashSet();
            if (!await IsInvoiceVersionNormalizationScopeWritableAsync(
                    invoices,
                    latestFlagChangingInvoiceIds,
                    latestFlagChangingInvoiceIds,
                    cancellationToken))
            {
                continue;
            }

            var previousStockDeltas = await BuildCombinedInvoiceStockDeltasAsync(invoices, cancellationToken);

            var changedVersionGroup = false;
            foreach (var invoice in invoices)
            {
                var shouldBeLatest = invoice.Id == latest.Id;
                if (invoice.VersionGroupId == Guid.Empty)
                {
                    invoice.VersionGroupId =
                        versionScope.VersionGroupId;
                    changedVersionGroup = true;
                }
                if (invoice.IsLatestVersion != shouldBeLatest)
                {
                    AddRentalSettlementTarget(
                        rentalSettlementTargets,
                        invoice.LinkedRentalBillingProfileId,
                        invoice.LinkedRentalBillingRunId);
                    invoice.IsLatestVersion = shouldBeLatest;
                    changedVersionGroup = true;
                }
            }

            if (!changedVersionGroup)
                continue;

            var currentStockDeltas = await BuildCombinedInvoiceStockDeltasAsync(invoices, cancellationToken);
            AccumulateStockDeltaDifferences(
                acceptedStockDeltaDifferences,
                previousStockDeltas,
                currentStockDeltas);
            await ApplyStockSnapshotDeltaAsync(
                previousStockDeltas,
                currentStockDeltas,
                itemWarehouseStockKeysHandledByClient,
                cancellationToken);
            changedAnyVersionGroup = true;
        }

        return changedAnyVersionGroup;
    }

    private readonly record struct InvoiceVersionScopeKey(
        Guid VersionGroupId,
        Guid CustomerId,
        string TenantCode,
        string OwningOfficeCode,
        string ResponsibleOfficeCode);

    private readonly record struct InvoiceVersionOperationalScope(
        string TenantCode,
        string OwningOfficeCode,
        string ResponsibleOfficeCode);

    private async Task<Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>> BuildCombinedInvoiceStockDeltasAsync(
        IEnumerable<Invoice> invoices,
        CancellationToken cancellationToken)
    {
        var combined = new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>();
        foreach (var invoice in invoices)
        {
            var invoiceDeltas = await _invoiceStockSnapshotService
                .BuildInvoiceStockDeltasAsync(invoice, cancellationToken);
            foreach (var (key, quantity) in invoiceDeltas)
            {
                combined[key] = combined.TryGetValue(key, out var existingQuantity)
                    ? existingQuantity + quantity
                    : quantity;
            }
        }

        return combined;
    }

    private static void AccumulateStockDeltaDifferences(
        IDictionary<
            InvoiceStockSnapshotService.InvoiceStockKey,
            decimal> accumulator,
        IReadOnlyDictionary<
            InvoiceStockSnapshotService.InvoiceStockKey,
            decimal> previous,
        IReadOnlyDictionary<
            InvoiceStockSnapshotService.InvoiceStockKey,
            decimal> current)
    {
        foreach (var key in previous.Keys
                     .Concat(current.Keys)
                     .Distinct())
        {
            previous.TryGetValue(
                key,
                out var previousQuantity);
            current.TryGetValue(
                key,
                out var currentQuantity);
            var difference =
                currentQuantity - previousQuantity;
            if (difference == 0m)
                continue;

            accumulator[key] =
                accumulator.TryGetValue(
                    key,
                    out var accumulated)
                    ? accumulated + difference
                    : difference;
            if (accumulator[key] == 0m)
                accumulator.Remove(key);
        }
    }

    private async Task<Dictionary<Guid, List<(Guid ProfileId, Guid? RunId)>>> LoadExistingRentalSettlementTargetsByTransactionIdAsync(
        IEnumerable<TransactionDto> payload,
        CancellationToken cancellationToken)
    {
        var transactionIds = (payload ?? Enumerable.Empty<TransactionDto>())
            .Select(transaction => transaction.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (transactionIds.Count == 0)
            return [];

        var existingTargets = await _dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction => transactionIds.Contains(transaction.Id) &&
                                  transaction.LinkedRentalBillingProfileId.HasValue &&
                                  transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
            .Select(transaction => new
            {
                transaction.Id,
                ProfileId = transaction.LinkedRentalBillingProfileId!.Value,
                RunId = transaction.LinkedRentalBillingRunId
            })
            .ToListAsync(cancellationToken);

        return existingTargets
            .GroupBy(target => target.Id)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(target => (target.ProfileId, target.RunId))
                    .Distinct()
                    .ToList());
    }

    private static List<(Guid ProfileId, Guid? RunId)> BuildRentalSettlementTargetsForAcceptedTransactions(
        IEnumerable<TransactionDto> acceptedTransactions,
        IReadOnlyDictionary<Guid, List<(Guid ProfileId, Guid? RunId)>> existingTargetsByTransactionId)
    {
        var targets = new List<(Guid ProfileId, Guid? RunId)>();
        foreach (var transaction in acceptedTransactions ?? Enumerable.Empty<TransactionDto>())
        {
            if (transaction.Id != Guid.Empty &&
                existingTargetsByTransactionId.TryGetValue(transaction.Id, out var existingTargets))
            {
                targets.AddRange(existingTargets);
            }

            AddRentalSettlementTarget(targets, transaction.LinkedRentalBillingProfileId, transaction.LinkedRentalBillingRunId);
        }

        return targets.Distinct().ToList();
    }

    private async Task<List<(Guid ProfileId, Guid? RunId)>> LoadRentalSettlementTargetsForPaymentsAsync(
        IEnumerable<PaymentDto> payload,
        CancellationToken cancellationToken)
    {
        var invoiceIds = new HashSet<Guid>();
        var payments = (payload ?? Enumerable.Empty<PaymentDto>()).ToList();
        foreach (var payment in payments)
        {
            if (payment.InvoiceId != Guid.Empty)
                invoiceIds.Add(payment.InvoiceId);
        }

        var paymentIds = payments
            .Select(payment => payment.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (paymentIds.Count > 0)
        {
            var existingInvoiceIds = await _dbContext.Payments.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(payment => paymentIds.Contains(payment.Id))
                .Select(payment => payment.InvoiceId)
                .ToListAsync(cancellationToken);
            foreach (var invoiceId in existingInvoiceIds)
            {
                if (invoiceId != Guid.Empty)
                    invoiceIds.Add(invoiceId);
            }
        }

        if (invoiceIds.Count == 0)
            return [];

        var targets = await _dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => invoiceIds.Contains(invoice.Id) &&
                              invoice.LinkedRentalBillingProfileId.HasValue &&
                              invoice.LinkedRentalBillingProfileId.Value != Guid.Empty)
            .Select(invoice => new
            {
                ProfileId = invoice.LinkedRentalBillingProfileId!.Value,
                RunId = invoice.LinkedRentalBillingRunId
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        return targets
            .Select(target => (target.ProfileId, target.RunId))
            .Distinct()
            .ToList();
    }

    private async Task<List<(Guid ProfileId, Guid? RunId)>> SynchronizeAcceptedPaymentsToLinkedTransactionsAsync(
        IEnumerable<PaymentDto> payload,
        CancellationToken cancellationToken)
    {
        var payments = (payload ?? Enumerable.Empty<PaymentDto>())
            .Where(payment => !payment.IsDeleted &&
                              payment.Id != Guid.Empty &&
                              payment.InvoiceId != Guid.Empty)
            .GroupBy(payment => payment.Id)
            .Select(group => group.Last())
            .ToList();
        if (payments.Count == 0)
            return [];

        var paymentById = payments.ToDictionary(payment => payment.Id);
        var paymentIds = paymentById.Keys.ToList();
        var transactions = await _dbContext.Transactions.IgnoreQueryFilters()
            .Where(transaction => paymentIds.Contains(transaction.Id))
            .ToListAsync(cancellationToken);

        var invoiceIds = payments
            .Select(payment => payment.InvoiceId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var invoicesById = await _dbContext.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoiceIds.Contains(invoice.Id) && !invoice.IsDeleted)
            .ToDictionaryAsync(invoice => invoice.Id, cancellationToken);
        var transactionsById = transactions
            .GroupBy(transaction => transaction.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var targets = new List<(Guid ProfileId, Guid? RunId)>();
        foreach (var payment in payments)
        {
            if (!invoicesById.TryGetValue(payment.InvoiceId, out var invoice))
            {
                continue;
            }

            if (!transactionsById.TryGetValue(payment.Id, out var transaction))
            {
                transaction = new TransactionRecord
                {
                    Id = payment.Id,
                    CreatedAtUtc = payment.CreatedAtUtc == default ? DateTime.UtcNow : payment.CreatedAtUtc,
                    UpdatedAtUtc = payment.UpdatedAtUtc == default ? DateTime.UtcNow : payment.UpdatedAtUtc
                };
                _dbContext.Transactions.Add(transaction);
                transactionsById[payment.Id] = transaction;
            }
            else if (!transaction.IsDeleted)
            {
                AddRentalSettlementTarget(targets, transaction.LinkedRentalBillingProfileId, transaction.LinkedRentalBillingRunId);
            }

            SynchronizeLinkedTransactionFromPayment(transaction, payment, invoice);
            AddRentalSettlementTarget(targets, transaction.LinkedRentalBillingProfileId, transaction.LinkedRentalBillingRunId);
        }

        return targets.Distinct().ToList();
    }

    private static void SynchronizeLinkedTransactionFromPayment(
        TransactionRecord transaction,
        PaymentDto payment,
        Invoice invoice)
    {
        transaction.CustomerId = invoice.CustomerId;
        transaction.TenantCode = invoice.TenantCode;
        transaction.OfficeCode = invoice.OfficeCode;
        transaction.ResponsibleOfficeCode = invoice.ResponsibleOfficeCode;
        transaction.TransactionDate = payment.PaymentDate;
        transaction.LinkedInvoiceId = payment.InvoiceId;
        transaction.LinkedInvoiceNumber = ResolveInvoiceDisplayNumber(invoice);
        transaction.LinkedRentalBillingProfileId = invoice.LinkedRentalBillingProfileId;
        transaction.LinkedRentalBillingRunId = invoice.LinkedRentalBillingRunId;
        transaction.SettlementAmount = payment.Amount;
        var transactionKind = ResolveLinkedTransactionKind(invoice);
        transaction.TransactionKind = transactionKind;
        ApplyLinkedTransactionTotals(transaction, payment.Amount, IsPaymentVoucher(invoice.VoucherType));
        transaction.Note = NormalizeLinkedPaymentNote(payment.Note, transactionKind);
        transaction.IsDeleted = false;
    }

    private static string NormalizeLinkedPaymentNote(string? note, string transactionKind)
    {
        var trimmed = (note ?? string.Empty).Trim();
        var kindLabel = (transactionKind ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || string.IsNullOrWhiteSpace(kindLabel))
            return trimmed;

        if (string.Equals(trimmed, kindLabel, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        foreach (var separator in new[] { " - ", "-", " / ", "/" })
        {
            var prefix = kindLabel + separator;
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[prefix.Length..].Trim();
        }

        return trimmed;
    }

    private static string ResolveInvoiceDisplayNumber(Invoice invoice)
        => string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            ? invoice.LocalTempNumber
            : invoice.InvoiceNumber;

    private static string ResolveLinkedTransactionKind(Invoice invoice)
    {
        if (invoice.LinkedRentalBillingProfileId is Guid rentalProfileId && rentalProfileId != Guid.Empty)
            return "렌탈수금";

        return IsPaymentVoucher(invoice.VoucherType)
            ? "전표지급"
            : "전표수금";
    }

    private static bool IsPaymentVoucher(VoucherType voucherType)
        => voucherType is VoucherType.Purchase or VoucherType.Procurement;

    private static void ApplyLinkedTransactionTotals(
        TransactionRecord transaction,
        decimal amount,
        bool isPayment)
    {
        if (isPayment)
        {
            transaction.CashReceipt = 0m;
            transaction.CardReceipt = 0m;
            transaction.BankReceipt = 0m;
            transaction.DiscountApplied = 0m;
            transaction.ReceiptTotal = 0m;
            transaction.PaymentTotal = amount;
            ApplySinglePaymentChannel(transaction, amount);
            return;
        }

        transaction.CashPayment = 0m;
        transaction.CardPayment = 0m;
        transaction.BankPayment = 0m;
        transaction.DiscountReceived = 0m;
        transaction.PaymentTotal = 0m;
        transaction.ReceiptTotal = amount;
        ApplySingleReceiptChannel(transaction, amount);
    }

    private static void ApplySingleReceiptChannel(TransactionRecord transaction, decimal amount)
    {
        if (transaction.CashReceipt >= 0m &&
            transaction.CardReceipt >= 0m &&
            transaction.BankReceipt >= 0m &&
            transaction.DiscountApplied >= 0m &&
            transaction.CashReceipt + transaction.CardReceipt +
            transaction.BankReceipt + transaction.DiscountApplied == amount)
        {
            return;
        }

        var useCash = transaction.CashReceipt != 0m &&
                      transaction.CardReceipt == 0m &&
                      transaction.BankReceipt == 0m &&
                      transaction.DiscountApplied == 0m;
        var useCard = transaction.CardReceipt != 0m &&
                      transaction.CashReceipt == 0m &&
                      transaction.BankReceipt == 0m &&
                      transaction.DiscountApplied == 0m;

        transaction.CashReceipt = useCash ? amount : 0m;
        transaction.CardReceipt = useCard ? amount : 0m;
        transaction.BankReceipt = !useCash && !useCard ? amount : 0m;
        transaction.DiscountApplied = 0m;
    }

    private static void ApplySinglePaymentChannel(TransactionRecord transaction, decimal amount)
    {
        if (transaction.CashPayment >= 0m &&
            transaction.CardPayment >= 0m &&
            transaction.BankPayment >= 0m &&
            transaction.DiscountReceived >= 0m &&
            transaction.CashPayment + transaction.CardPayment +
            transaction.BankPayment + transaction.DiscountReceived == amount)
        {
            return;
        }

        var useCash = transaction.CashPayment != 0m &&
                      transaction.CardPayment == 0m &&
                      transaction.BankPayment == 0m &&
                      transaction.DiscountReceived == 0m;
        var useCard = transaction.CardPayment != 0m &&
                      transaction.CashPayment == 0m &&
                      transaction.BankPayment == 0m &&
                      transaction.DiscountReceived == 0m;

        transaction.CashPayment = useCash ? amount : 0m;
        transaction.CardPayment = useCard ? amount : 0m;
        transaction.BankPayment = !useCash && !useCard ? amount : 0m;
        transaction.DiscountReceived = 0m;
    }

    private async Task<List<(Guid ProfileId, Guid? RunId)>> CascadeDeletedPaymentsToLinkedTransactionsAsync(
        IEnumerable<PaymentDto> payload,
        CancellationToken cancellationToken)
    {
        var deletedPayments = (payload ?? Enumerable.Empty<PaymentDto>())
            .Where(payment => payment.IsDeleted && payment.Id != Guid.Empty)
            .GroupBy(payment => payment.Id)
            .Select(group => group.First())
            .ToList();
        if (deletedPayments.Count == 0)
            return [];

        var deletedPaymentById = deletedPayments.ToDictionary(payment => payment.Id);
        var paymentIds = deletedPaymentById.Keys.ToList();
        var transactions = await _dbContext.Transactions.IgnoreQueryFilters()
            .Where(transaction => paymentIds.Contains(transaction.Id))
            .ToListAsync(cancellationToken);
        if (transactions.Count == 0)
            return [];

        var transactionIds = transactions.Select(transaction => transaction.Id).ToList();
        var attachments = await _dbContext.TransactionAttachments.IgnoreQueryFilters()
            .Where(attachment => transactionIds.Contains(attachment.TransactionId) && !attachment.IsDeleted)
            .ToListAsync(cancellationToken);
        var attachmentsByTransactionId = attachments
            .GroupBy(attachment => attachment.TransactionId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var targets = new List<(Guid ProfileId, Guid? RunId)>();
        foreach (var transaction in transactions)
        {
            if (!deletedPaymentById.TryGetValue(transaction.Id, out var payment))
                continue;

            if (transaction.LinkedInvoiceId.HasValue &&
                payment.InvoiceId != Guid.Empty &&
                transaction.LinkedInvoiceId.Value != payment.InvoiceId)
            {
                continue;
            }

            if (!transaction.IsDeleted)
            {
                transaction.IsDeleted = true;
                AddRentalSettlementTarget(targets, transaction.LinkedRentalBillingProfileId, transaction.LinkedRentalBillingRunId);
            }

            if (attachmentsByTransactionId.TryGetValue(transaction.Id, out var transactionAttachments))
            {
                foreach (var attachment in transactionAttachments)
                    attachment.IsDeleted = true;
            }
        }

        return targets.Distinct().ToList();
    }

    private async Task SoftDeletePaymentAttachmentsForDeletedPaymentsAsync(
        IEnumerable<PaymentDto> payload,
        CancellationToken cancellationToken)
    {
        var deletedPaymentIds = (payload ?? Enumerable.Empty<PaymentDto>())
            .Where(payment => payment.IsDeleted && payment.Id != Guid.Empty)
            .Select(payment => payment.Id)
            .Distinct()
            .ToList();
        if (deletedPaymentIds.Count == 0)
            return;

        var attachments = await _dbContext.PaymentAttachments.IgnoreQueryFilters()
            .Where(attachment => deletedPaymentIds.Contains(attachment.PaymentId) && !attachment.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var attachment in attachments)
            attachment.IsDeleted = true;
    }

    private static void AddRentalSettlementTarget(List<(Guid ProfileId, Guid? RunId)> targets, Guid? profileId, Guid? runId)
    {
        if (!profileId.HasValue || profileId.Value == Guid.Empty)
            return;

        targets.Add((profileId.Value, runId));
    }

    private async Task<List<CompanyProfileDto>> PrepareScopedCompanyProfilesAsync(
        IEnumerable<CompanyProfileDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<CompanyProfileDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.CompanyProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(profile => profile.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForCompanyProfiles(existing.OfficeCode))
            {
                AddClientConflict(dto, nameof(CompanyProfile), "Current account cannot modify this company profile office scope.", result);
                continue;
            }

            if (OfficeCodeCatalog.TryNormalizeOfficeCode(dto.OfficeCode, out var requestedOfficeCode))
            {
                if (!_officeScopeService.CanWriteOfficeForCompanyProfiles(requestedOfficeCode))
                {
                    AddClientConflict(dto, nameof(CompanyProfile), "Requested company profile office is outside the writable office scope.", result);
                    continue;
                }

                dto.OfficeCode = requestedOfficeCode;
            }
            else if (OfficeCodeCatalog.TryNormalizeOfficeCode(existing?.OfficeCode, out var existingOfficeCode) &&
                     _officeScopeService.CanWriteOfficeForCompanyProfiles(existingOfficeCode))
            {
                dto.OfficeCode = existingOfficeCode;
            }
            else
            {
                dto.OfficeCode = _officeScopeService.CurrentOfficeCode;
            }

            if (!_officeScopeService.CanWriteOfficeForCompanyProfiles(dto.OfficeCode))
            {
                AddClientConflict(dto, nameof(CompanyProfile), "Current account cannot modify this company profile office scope.", result);
                continue;
            }

            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<CustomerMasterDto>> PrepareScopedCustomerMastersAsync(
        IEnumerable<CustomerMasterDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<CustomerMasterDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.CustomerMasters.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForCustomers(existing.OfficeCode, existing.TenantCode))
            {
                AddClientConflict(dto, nameof(CustomerMaster), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(
                dto.OfficeCode,
                existing?.OfficeCode);
            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
                dto.TenantCode,
                dto.OfficeCode,
                existing?.TenantCode,
                existing?.OfficeCode);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<CustomerMasterDto>> FilterValidCustomerMastersAsync(
        IEnumerable<CustomerMasterDto> payload, SyncPushResult result, CancellationToken cancellationToken)
    {
        var valid = new List<CustomerMasterDto>();

        foreach (var dto in payload)
        {
            if (dto.CategoryId.HasValue &&
                !await ExistsOrTrackedAsync(_dbContext.CustomerCategories, dto.CategoryId.Value, cancellationToken))
            {
                dto.CategoryId = null;
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<CustomerDto>> PrepareScopedCustomersAsync(
        IEnumerable<CustomerDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var payloadRows = payload as IReadOnlyCollection<CustomerDto> ?? payload.ToList();
        var existingCustomersById = new Dictionary<Guid, Customer>();
        var requestedCustomerIds = payloadRows
            .Select(dto => dto.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        foreach (var customerIdBatch in requestedCustomerIds.Chunk(500))
        {
            var batch = customerIdBatch.ToArray();
            var existingCustomers = await _dbContext.Customers
                .IgnoreQueryFilters()
                .Where(customer => batch.Contains(customer.Id))
                .ToListAsync(cancellationToken);
            foreach (var existingCustomer in existingCustomers)
                existingCustomersById.TryAdd(existingCustomer.Id, existingCustomer);
        }

        var scoped = new List<CustomerDto>();

        foreach (var dto in payloadRows)
        {
            existingCustomersById.TryGetValue(dto.Id, out var existing);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForCustomers(existing.ResponsibleOfficeCode, existing.TenantCode, existing.OfficeCode))
            {
                AddClientConflict(dto, nameof(Customer), "Current account cannot modify this office scope.", result);
                continue;
            }

            if (dto.IsDeleted && existing is not null && !existing.IsDeleted)
            {
                var referenceBlockMessage = await CustomerDeletionReferenceGuard.BuildActiveReferenceBlockMessageAsync(
                    _dbContext,
                    existing.Id,
                    cancellationToken);
                if (referenceBlockMessage is not null)
                {
                    AddClientConflict(dto, nameof(Customer), referenceBlockMessage, result);
                    continue;
                }
            }

            if (existing is not null)
                PreserveCustomerTextWhenIncomingLooksLossy(dto, existing);

            dto.ResponsibleOfficeCode = _officeScopeService.ResolveCustomerResponsibleScopeForCreate(
                dto.ResponsibleOfficeCode,
                existing?.ResponsibleOfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                existing?.OfficeCode);
            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
                dto.TenantCode,
                dto.OfficeCode,
                existing?.TenantCode,
                existing?.OfficeCode);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<CustomerDto>> FilterValidCustomersAsync(
        IEnumerable<CustomerDto> payload, SyncPushResult result, CancellationToken cancellationToken)
    {
        var payloadRows = payload as IReadOnlyCollection<CustomerDto> ?? payload.ToList();
        foreach (var dto in payloadRows)
            NormalizeCustomerClassification(dto);

        var activeCategoryIds = _dbContext.CustomerCategories.Local
            .Where(category => !category.IsDeleted)
            .Select(category => category.Id)
            .ToHashSet();
        var requestedCategoryIds = payloadRows
            .Where(dto => dto.CategoryId.HasValue && dto.CategoryId.Value != Guid.Empty)
            .Select(dto => dto.CategoryId!.Value)
            .Distinct()
            .ToArray();
        foreach (var categoryIdBatch in requestedCategoryIds.Chunk(500))
        {
            var batch = categoryIdBatch.ToArray();
            var persistedCategoryIds = await _dbContext.CustomerCategories
                .IgnoreQueryFilters()
                .Where(category => batch.Contains(category.Id) && !category.IsDeleted)
                .Select(category => category.Id)
                .ToListAsync(cancellationToken);
            activeCategoryIds.UnionWith(persistedCategoryIds);
        }

        var customerMastersById = new Dictionary<Guid, CustomerMaster>();
        var requestedCustomerMasterIds = payloadRows
            .Where(dto => dto.CustomerMasterId.HasValue && dto.CustomerMasterId.Value != Guid.Empty)
            .Select(dto => dto.CustomerMasterId!.Value)
            .Distinct()
            .ToArray();
        foreach (var customerMasterIdBatch in requestedCustomerMasterIds.Chunk(500))
        {
            var batch = customerMasterIdBatch.ToArray();
            var customerMasters = await _dbContext.CustomerMasters
                .IgnoreQueryFilters()
                .Where(customerMaster => batch.Contains(customerMaster.Id))
                .ToListAsync(cancellationToken);
            foreach (var customerMaster in customerMasters)
                customerMastersById.TryAdd(customerMaster.Id, customerMaster);
        }

        var valid = new List<CustomerDto>();

        foreach (var dto in payloadRows)
        {
            if (dto.CategoryId.HasValue &&
                !activeCategoryIds.Contains(dto.CategoryId.Value))
            {
                dto.CategoryId = null;
            }

            if (dto.CustomerMasterId.HasValue)
            {
                if (!customerMastersById.TryGetValue(dto.CustomerMasterId.Value, out var customerMaster) ||
                    customerMaster.IsDeleted)
                {
                    dto.CustomerMasterId = null;
                    valid.Add(dto);
                    continue;
                }

                if (!_officeScopeService.CanReadOfficeForCustomers(customerMaster.OfficeCode, customerMaster.TenantCode))
                {
                    dto.CustomerMasterId = null;
                }
            }

            valid.Add(dto);
        }

        return valid;
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

    private async Task<List<ItemDto>> PrepareScopedItemsAsync(
        IEnumerable<ItemDto> payload,
        IDictionary<Guid, Guid> resolvedIncomingItemIds,
        ISet<Guid> scopeRejectedIncomingItemIds,
        ISet<Guid> inventoryTrackingTransitionItemIds,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new Dictionary<Guid, ItemDto>();
        var incomingCanonicalIdsByNaturalKey = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var incomingItemIdentityClaims = new Dictionary<string, PreparedItemIdentityClaim>(StringComparer.OrdinalIgnoreCase);
        var originalInventorySupportByItemId = new Dictionary<Guid, bool>();
        var scopedNaturalKeyCandidates = new Dictionary<string, List<ItemNaturalKeyCandidate>>(StringComparer.OrdinalIgnoreCase);

        foreach (var dto in payload)
        {
            var originalItemId = dto.Id;
            var existing = await _dbContext.Items.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            var existingScopeWriteChecked = false;

            string resolvedOfficeCode;
            string resolvedTenantCode;
            if (existing is not null)
            {
                if (!_officeScopeService.CanWriteOfficeForItems(existing.OfficeCode, existing.TenantCode))
                {
                    scopeRejectedIncomingItemIds.Add(originalItemId);
                    AddClientConflict(
                        dto,
                        nameof(Item),
                        "Current account cannot modify this office scope.",
                        result);
                    continue;
                }

                existingScopeWriteChecked = true;
                if (!TryEvaluateRequestedItemScope(
                        dto,
                        existing,
                        out var requestsDifferentScope,
                        out var scopeError))
                {
                    scopeRejectedIncomingItemIds.Add(originalItemId);
                    AddClientConflict(dto, nameof(Item), scopeError, result);
                    continue;
                }

                if (requestsDifferentScope)
                {
                    scopeRejectedIncomingItemIds.Add(originalItemId);
                    AddClientConflict(
                        dto,
                        nameof(Item),
                        "Item tenant/office scope cannot be changed for an existing item.",
                        result);
                    continue;
                }

                resolvedOfficeCode = existing.OfficeCode;
                resolvedTenantCode = existing.TenantCode;
                dto.OfficeCode = existing.OfficeCode;
                dto.TenantCode = existing.TenantCode;
            }
            else
            {
                if (!TryValidateExplicitItemScope(dto, out var scopeError))
                {
                    scopeRejectedIncomingItemIds.Add(originalItemId);
                    AddClientConflict(dto, nameof(Item), scopeError, result);
                    continue;
                }

                resolvedOfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode);
                resolvedTenantCode = _officeScopeService.ResolveTenantForCreate(
                    dto.TenantCode,
                    resolvedOfficeCode);
            }
            var preservesExistingNaturalIdentity =
                existing is not null &&
                PreservesExistingItemNaturalIdentity(
                    dto,
                    existing,
                    resolvedOfficeCode,
                    resolvedTenantCode);

            var naturalKey = BuildScopedItemNaturalKey(
                dto,
                resolvedOfficeCode,
                resolvedTenantCode);
            var scopedIdentityKeys = BuildScopedItemIdentityKeys(
                dto,
                resolvedOfficeCode,
                resolvedTenantCode);
            var descriptorKey = BuildItemDescriptorKey(dto);
            var claimedIdentities = preservesExistingNaturalIdentity
                ? []
                : scopedIdentityKeys
                    .Where(incomingItemIdentityClaims.ContainsKey)
                    .Select(key => incomingItemIdentityClaims[key])
                    .ToList();
            if (claimedIdentities.Any(claim =>
                    !string.Equals(claim.DescriptorKey, descriptorKey, StringComparison.OrdinalIgnoreCase)))
            {
                AddClientConflict(
                    dto,
                    nameof(Item),
                    "Item identity is ambiguous within the same sync request because the same material or serial number has a different descriptor.",
                    result);
                continue;
            }

            var batchCanonicalIds = claimedIdentities
                .Select(claim => claim.CanonicalId)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            if (!preservesExistingNaturalIdentity &&
                !string.IsNullOrWhiteSpace(naturalKey) &&
                incomingCanonicalIdsByNaturalKey.TryGetValue(naturalKey, out var naturalKeyBatchCanonicalId))
            {
                batchCanonicalIds.Add(naturalKeyBatchCanonicalId);
                batchCanonicalIds = batchCanonicalIds.Distinct().ToList();
            }

            if (batchCanonicalIds.Count > 1)
            {
                AddClientConflict(
                    dto,
                    nameof(Item),
                    "Item identity is ambiguous within the same sync request because material and serial numbers resolve to different items.",
                    result);
                continue;
            }

            var requestedItemId = dto.Id;
            var naturalKeyScope = string.Join('\u001f', resolvedTenantCode, resolvedOfficeCode);
            if (!scopedNaturalKeyCandidates.TryGetValue(naturalKeyScope, out var naturalKeyCandidates))
            {
                naturalKeyCandidates = await _dbContext.Items.IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(item =>
                        item.OfficeCode == resolvedOfficeCode &&
                        item.TenantCode == resolvedTenantCode)
                    .Select(item => new ItemNaturalKeyCandidate(
                        item.Id,
                        item.NameMatchKey,
                        item.NameOriginal,
                        item.SpecificationMatchKey,
                        item.SpecificationOriginal,
                        item.CategoryName,
                        item.ItemKind,
                        item.TrackingType,
                        item.IsRental,
                        item.MaterialNumber,
                        item.SerialNumber))
                    .ToListAsync(cancellationToken);
                scopedNaturalKeyCandidates[naturalKeyScope] = naturalKeyCandidates;
            }

            var naturalKeyResolution = preservesExistingNaturalIdentity
                ? ItemNaturalKeyResolution.Match(existing!.Id)
                : FindExistingItemByNaturalKey(
                    dto,
                    naturalKeyCandidates,
                    existing?.Id);
            if (!string.IsNullOrWhiteSpace(naturalKeyResolution.ConflictReason))
            {
                AddClientConflict(
                    dto,
                    nameof(Item),
                    naturalKeyResolution.ConflictReason,
                    result);
                continue;
            }

            var batchCanonicalId = batchCanonicalIds.SingleOrDefault();
            if (existing is not null)
            {
                if ((naturalKeyResolution.ItemId.HasValue && naturalKeyResolution.ItemId.Value != existing.Id) ||
                    (batchCanonicalId != Guid.Empty && batchCanonicalId != existing.Id))
                {
                    AddClientConflict(
                        dto,
                        nameof(Item),
                        "Item identity is ambiguous because this item update resolves to a different server or same-request item.",
                        result);
                    continue;
                }
            }
            else
            {
                if (naturalKeyResolution.ItemId.HasValue)
                {
                    existing = await _dbContext.Items.IgnoreQueryFilters()
                        .FirstAsync(item => item.Id == naturalKeyResolution.ItemId.Value, cancellationToken);
                }
                if (existing is not null &&
                    batchCanonicalId != Guid.Empty &&
                    existing.Id != batchCanonicalId)
                {
                    AddClientConflict(
                        dto,
                        nameof(Item),
                        "Item identity is ambiguous because the same request and the server resolve it to different items.",
                        result);
                    continue;
                }

                var canonicalId = existing?.Id ?? batchCanonicalId;
                if (canonicalId != Guid.Empty)
                {
                    dto.Id = canonicalId;
                    if (requestedItemId != canonicalId)
                    {
                        var mergedIntoServerItem = existing is not null;
                        AddNotice(
                            result,
                            nameof(Item),
                            canonicalId,
                            mergedIntoServerItem ? "item-natural-key-merged" : "item-natural-key-batch-merged",
                            mergedIntoServerItem
                                ? $"품목 '{dto.NameOriginal}'은(는) 기존 서버 품목과 동일한 시리얼/관리번호/품목키로 판단되어 해당 품목에 병합되었습니다."
                                : $"품목 '{dto.NameOriginal}'은(는) 동일한 시리얼/관리번호/품목키가 같은 요청 안에 이미 있어 기존 저장 대상으로 합쳐졌습니다.");
                    }
                }
                else if (dto.Id == Guid.Empty)
                {
                    dto.Id = Guid.NewGuid();
                }

                if (existing is not null)
                {
                    resolvedOfficeCode = _officeScopeService.ResolveScopeForCreate(
                        dto.OfficeCode,
                        existing.OfficeCode);
                    resolvedTenantCode = _officeScopeService.ResolveTenantForCreate(
                        dto.TenantCode,
                        resolvedOfficeCode,
                        existing.TenantCode,
                        existing.OfficeCode);
                }
            }

            if (existing is not null &&
                !existingScopeWriteChecked &&
                !_officeScopeService.CanWriteOfficeForItems(existing.OfficeCode, existing.TenantCode))
            {
                scopeRejectedIncomingItemIds.Add(originalItemId);
                AddClientConflict(dto, nameof(Item), "Current account cannot modify this office scope.", result);
                continue;
            }

            if (dto.IsDeleted && existing is not null && !existing.IsDeleted)
            {
                var referenceBlockMessage = await ItemDeletionReferenceGuard.BuildActiveReferenceBlockMessageAsync(
                    _dbContext,
                    existing.Id,
                    cancellationToken);
                if (referenceBlockMessage is not null)
                {
                    AddClientConflict(dto, nameof(Item), referenceBlockMessage, result);
                    continue;
                }
            }

            dto.OfficeCode = resolvedOfficeCode;
            dto.TenantCode = resolvedTenantCode;

            if (dto.Id == Guid.Empty)
            {
                dto.Id = Guid.NewGuid();
            }

            if (!preservesExistingNaturalIdentity)
            {
                if (!string.IsNullOrWhiteSpace(naturalKey))
                    incomingCanonicalIdsByNaturalKey[naturalKey] = dto.Id;
                foreach (var scopedIdentityKey in scopedIdentityKeys)
                {
                    incomingItemIdentityClaims[scopedIdentityKey] =
                        new PreparedItemIdentityClaim(dto.Id, descriptorKey);
                }
            }

            if (existing is not null)
            {
                originalInventorySupportByItemId[dto.Id] =
                    ItemOperationalPolicy.SupportsInventory(existing.TrackingType);
            }

            if (scoped.TryGetValue(dto.Id, out var existingScoped))
            {
                if (originalItemId != Guid.Empty)
                    resolvedIncomingItemIds[originalItemId] = dto.Id;

                if (ShouldReplacePreparedItem(existingScoped, dto))
                    scoped[dto.Id] = dto;

                continue;
            }

            if (originalItemId != Guid.Empty)
                resolvedIncomingItemIds[originalItemId] = dto.Id;
            scoped[dto.Id] = dto;
        }

        foreach (var dto in scoped.Values.Where(item => !item.IsDeleted))
        {
            if (!originalInventorySupportByItemId.TryGetValue(dto.Id, out var previouslySupportedInventory))
                continue;

            var requestedTrackingType = ItemOperationalPolicy.NormalizeTrackingType(
                dto.TrackingType,
                dto.ItemKind,
                dto.CategoryName,
                dto.IsRental);
            var willSupportInventory = ItemOperationalPolicy.SupportsInventory(requestedTrackingType);
            if (previouslySupportedInventory == willSupportInventory)
                continue;

            inventoryTrackingTransitionItemIds.Add(dto.Id);
        }

        return scoped.Values.ToList();
    }

    private sealed record PreparedItemIdentityClaim(Guid CanonicalId, string DescriptorKey);

    private static bool TryEvaluateRequestedItemScope(
        ItemDto dto,
        Item existing,
        out bool requestsDifferentScope,
        out string scopeError)
    {
        requestsDifferentScope = false;
        scopeError = string.Empty;
        var existingOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(existing.OfficeCode);
        var requestedOfficeCode = existingOfficeCode;
        if (!string.IsNullOrWhiteSpace(dto.OfficeCode) &&
            !OfficeCodeCatalog.TryNormalizeScope(dto.OfficeCode, out requestedOfficeCode))
        {
            scopeError = "Item office scope is invalid.";
            return false;
        }

        var existingTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(
            existing.TenantCode,
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

    private static bool TryValidateExplicitItemScope(ItemDto dto, out string scopeError)
    {
        if (!string.IsNullOrWhiteSpace(dto.OfficeCode) &&
            !OfficeCodeCatalog.TryNormalizeScope(dto.OfficeCode, out _))
        {
            scopeError = "Item office scope is invalid.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(dto.TenantCode) &&
            !TenantScopeCatalog.TryNormalizeTenantCode(dto.TenantCode, out _))
        {
            scopeError = "Item tenant scope is invalid.";
            return false;
        }

        scopeError = string.Empty;
        return true;
    }

    private static void RemapIncomingItemReferences(
        SyncPushRequest request,
        IReadOnlyDictionary<Guid, Guid> resolvedIncomingItemIds)
    {
        if (resolvedIncomingItemIds.Count == 0)
            return;

        foreach (var priceGrade in request.ItemPriceGrades ?? [])
        {
            if (resolvedIncomingItemIds.TryGetValue(priceGrade.ItemId, out var canonicalItemId))
                priceGrade.ItemId = canonicalItemId;
        }

        foreach (var stock in request.ItemWarehouseStocks ?? [])
        {
            if (resolvedIncomingItemIds.TryGetValue(stock.ItemId, out var canonicalItemId))
                stock.ItemId = canonicalItemId;
        }

        foreach (var marker in request.ItemWarehouseStockSnapshotMarkers ?? [])
        {
            if (resolvedIncomingItemIds.TryGetValue(
                    marker.ItemId,
                    out var canonicalItemId))
            {
                marker.ItemId = canonicalItemId;
            }
        }

        foreach (var asset in request.RentalAssets ?? [])
        {
            if (asset.ItemId.HasValue &&
                resolvedIncomingItemIds.TryGetValue(asset.ItemId.Value, out var canonicalItemId))
            {
                asset.ItemId = canonicalItemId;
            }
        }

        foreach (var invoice in request.Invoices ?? [])
        {
            foreach (var line in invoice.Lines ?? [])
            {
                if (line.ItemId.HasValue &&
                    resolvedIncomingItemIds.TryGetValue(line.ItemId.Value, out var canonicalItemId))
                {
                    line.ItemId = canonicalItemId;
                }
            }
        }

        foreach (var transfer in request.InventoryTransfers ?? [])
        {
            foreach (var line in transfer.Lines ?? [])
            {
                if (line.ItemId.HasValue &&
                    resolvedIncomingItemIds.TryGetValue(line.ItemId.Value, out var canonicalItemId))
                {
                    line.ItemId = canonicalItemId;
                }
            }
        }
    }

    private async Task RemoveInventoryRuntimeStateForDisabledItemsAsync(
        IReadOnlyCollection<ItemDto> acceptedItems,
        CancellationToken cancellationToken)
    {
        var acceptedItemIds = acceptedItems
            .Where(item => item.Id != Guid.Empty)
            .Select(item => item.Id)
            .Distinct()
            .ToList();
        if (acceptedItemIds.Count == 0)
            return;

        var acceptedEntities = await _dbContext.Items
            .IgnoreQueryFilters()
            .Where(item => acceptedItemIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var disabledItemIds = acceptedEntities
            .Where(item =>
                item.IsDeleted ||
                !ItemOperationalPolicy.SupportsInventory(item.TrackingType))
            .Select(item => item.Id)
            .Distinct()
            .ToList();
        if (disabledItemIds.Count == 0)
            return;

        var staleRows = await _dbContext.ItemWarehouseStocks
            .IgnoreQueryFilters()
            .Where(stock => disabledItemIds.Contains(stock.ItemId))
            .ToListAsync(cancellationToken);
        if (staleRows.Count > 0)
            _dbContext.ItemWarehouseStocks.RemoveRange(staleRows);

        var staleLedgerEntries = await _dbContext.InventoryLedgerEntries
            .Where(entry => disabledItemIds.Contains(entry.ItemId))
            .ToListAsync(cancellationToken);
        if (staleLedgerEntries.Count > 0)
            _dbContext.InventoryLedgerEntries.RemoveRange(staleLedgerEntries);
    }

    private async Task EnsureItemCategoryOptionsForItemsAsync(
        IReadOnlyCollection<ItemDto> payload,
        CancellationToken cancellationToken)
    {
        var ensuredOptions = await ItemCategoryOptionGuard.EnsureActiveOptionsAsync(
            _dbContext,
            payload.Where(item => !item.IsDeleted).Select(item => item.CategoryName),
            cancellationToken);

        foreach (var dto in payload.Where(item => !item.IsDeleted))
        {
            var normalizedKey = RentalCatalogValueNormalizer.NormalizeLooseKey(
                RentalCatalogValueNormalizer.NormalizeCategoryDisplayName(dto.CategoryName));
            dto.CategoryName = string.IsNullOrWhiteSpace(normalizedKey) || !ensuredOptions.TryGetValue(normalizedKey, out var canonicalName)
                ? string.Empty
                : canonicalName;
        }
    }

    private static ItemNaturalKeyResolution FindExistingItemByNaturalKey(
        ItemDto dto,
        IReadOnlyCollection<ItemNaturalKeyCandidate> scopedItems,
        Guid? excludedItemId)
    {
        var descriptorKey = BuildItemDescriptorKey(dto);
        var scopedCandidates = scopedItems
            .Where(item => !excludedItemId.HasValue || item.Id != excludedItemId.Value)
            .ToList();

        var materialKey = NormalizeItemIdentityValue(dto.MaterialNumber);
        var materialMatches = new List<ItemNaturalKeyCandidate>();
        if (HasMeaningfulItemIdentityValue(materialKey))
        {
            materialMatches = scopedCandidates
                .Where(item => string.Equals(
                    NormalizeItemIdentityValue(item.MaterialNumber),
                    materialKey,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (materialMatches.Count > 1)
            {
                return ItemNaturalKeyResolution.Conflict(
                    $"Item identity is ambiguous: material number '{dto.MaterialNumber}' matches multiple items in the same scope.");
            }
        }

        var serialKey = NormalizeItemIdentityValue(dto.SerialNumber);
        var serialMatches = new List<ItemNaturalKeyCandidate>();
        if (HasMeaningfulItemIdentityValue(serialKey))
        {
            serialMatches = scopedCandidates
                .Where(item => string.Equals(
                    NormalizeItemIdentityValue(item.SerialNumber),
                    serialKey,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (serialMatches.Count > 1)
            {
                return ItemNaturalKeyResolution.Conflict(
                    $"Item identity is ambiguous: serial number '{dto.SerialNumber}' matches multiple items in the same scope.");
            }
        }

        var materialCandidate = materialMatches.SingleOrDefault();
        var serialCandidate = serialMatches.SingleOrDefault();
        if (materialCandidate is not null &&
            serialCandidate is not null &&
            materialCandidate.Id != serialCandidate.Id)
        {
            return ItemNaturalKeyResolution.Conflict(
                "Item identity is ambiguous: material number and serial number resolve to different items.");
        }

        var identityCandidate = materialCandidate ?? serialCandidate;
        if (identityCandidate is not null)
        {
            if (!string.Equals(
                    BuildItemDescriptorKey(identityCandidate),
                    descriptorKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ItemNaturalKeyResolution.Conflict(
                    "Item identity conflicts with an existing item that has a different descriptor.");
            }

            return ItemNaturalKeyResolution.Match(identityCandidate);
        }

        if (string.IsNullOrWhiteSpace(descriptorKey))
            return ItemNaturalKeyResolution.None;

        var matchingDescriptorCandidates = scopedCandidates
            .Where(item => string.Equals(BuildItemDescriptorKey(item), descriptorKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingDescriptorCandidates.Count > 1)
        {
            return ItemNaturalKeyResolution.Conflict(
                "Item descriptor is ambiguous because multiple items match in the same scope.");
        }
        if (matchingDescriptorCandidates.Count == 0)
            return ItemNaturalKeyResolution.None;

        var candidate = matchingDescriptorCandidates[0];
        var candidateMaterialKey = NormalizeItemIdentityValue(candidate.MaterialNumber);
        var candidateSerialKey = NormalizeItemIdentityValue(candidate.SerialNumber);

        if (HasMeaningfulItemIdentityValue(materialKey) || HasMeaningfulItemIdentityValue(serialKey))
        {
            if (HasMeaningfulItemIdentityValue(candidateMaterialKey) || HasMeaningfulItemIdentityValue(candidateSerialKey))
                return ItemNaturalKeyResolution.None;
        }

        return ItemNaturalKeyResolution.Match(candidate);
    }

    private sealed record ItemNaturalKeyCandidate(
        Guid Id,
        string? NameMatchKey,
        string? NameOriginal,
        string? SpecificationMatchKey,
        string? SpecificationOriginal,
        string? CategoryName,
        string? ItemKind,
        string? TrackingType,
        bool IsRental,
        string? MaterialNumber,
        string? SerialNumber);

    private sealed record ItemNaturalKeyResolution(Guid? ItemId, string? ConflictReason)
    {
        public static ItemNaturalKeyResolution None { get; } = new(null, null);

        public static ItemNaturalKeyResolution Match(ItemNaturalKeyCandidate item)
            => new(item.Id, null);

        public static ItemNaturalKeyResolution Match(Guid itemId)
            => new(itemId, null);

        public static ItemNaturalKeyResolution Conflict(string reason)
            => new(null, reason);
    }

    private static bool PreservesExistingItemNaturalIdentity(
        ItemDto dto,
        Item existing,
        string resolvedOfficeCode,
        string resolvedTenantCode)
        => string.Equals(
               existing.OfficeCode,
               resolvedOfficeCode,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               existing.TenantCode,
               resolvedTenantCode,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               BuildItemDescriptorKey(dto),
               BuildItemDescriptorKey(existing),
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               NormalizeItemIdentityValue(dto.MaterialNumber),
               NormalizeItemIdentityValue(existing.MaterialNumber),
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               NormalizeItemIdentityValue(dto.SerialNumber),
               NormalizeItemIdentityValue(existing.SerialNumber),
               StringComparison.OrdinalIgnoreCase);

    private static string BuildScopedItemNaturalKey(
        ItemDto dto,
        string resolvedOfficeCode,
        string resolvedTenantCode)
    {
        var descriptorKey = BuildItemDescriptorKey(dto);
        if (string.IsNullOrWhiteSpace(descriptorKey))
            return string.Empty;

        var materialKey = NormalizeItemIdentityValue(dto.MaterialNumber);
        if (HasMeaningfulItemIdentityValue(materialKey))
        {
            return string.Join('|',
                resolvedTenantCode,
                resolvedOfficeCode,
                "MAT",
                materialKey,
                descriptorKey);
        }

        var serialKey = NormalizeItemIdentityValue(dto.SerialNumber);
        if (HasMeaningfulItemIdentityValue(serialKey))
        {
            return string.Join('|',
                resolvedTenantCode,
                resolvedOfficeCode,
                "SER",
                serialKey,
                descriptorKey);
        }

        return string.Join('|',
            resolvedTenantCode,
            resolvedOfficeCode,
            "DESC",
            descriptorKey);
    }

    private static IReadOnlyList<string> BuildScopedItemIdentityKeys(
        ItemDto dto,
        string resolvedOfficeCode,
        string resolvedTenantCode)
    {
        var keys = new List<string>(2);
        var materialKey = NormalizeItemIdentityValue(dto.MaterialNumber);
        if (HasMeaningfulItemIdentityValue(materialKey))
        {
            keys.Add(string.Join('|',
                resolvedTenantCode,
                resolvedOfficeCode,
                "MAT",
                materialKey));
        }

        var serialKey = NormalizeItemIdentityValue(dto.SerialNumber);
        if (HasMeaningfulItemIdentityValue(serialKey))
        {
            keys.Add(string.Join('|',
                resolvedTenantCode,
                resolvedOfficeCode,
                "SER",
                serialKey));
        }

        return keys;
    }

    private static string BuildItemDescriptorKey(ItemDto dto)
        => BuildItemDescriptorKey(
            dto.NameMatchKey,
            dto.NameOriginal,
            dto.SpecificationMatchKey,
            dto.SpecificationOriginal,
            dto.CategoryName,
            dto.ItemKind,
            dto.TrackingType,
            dto.IsRental);

    private static string BuildItemDescriptorKey(Item item)
        => BuildItemDescriptorKey(
            item.NameMatchKey,
            item.NameOriginal,
            item.SpecificationMatchKey,
            item.SpecificationOriginal,
            item.CategoryName,
            item.ItemKind,
            item.TrackingType,
            item.IsRental);

    private static string BuildItemDescriptorKey(ItemNaturalKeyCandidate item)
        => BuildItemDescriptorKey(
            item.NameMatchKey,
            item.NameOriginal,
            item.SpecificationMatchKey,
            item.SpecificationOriginal,
            item.CategoryName,
            item.ItemKind,
            item.TrackingType,
            item.IsRental);

    private static string BuildItemDescriptorKey(
        string? nameMatchKey,
        string? nameOriginal,
        string? specificationMatchKey,
        string? specificationOriginal,
        string? categoryName,
        string? itemKind,
        string? trackingType,
        bool isRental)
    {
        var normalizedTrackingType = ItemOperationalPolicy.NormalizeTrackingType(
            trackingType,
            itemKind,
            categoryName,
            isRental);
        var normalizedItemKind = ItemOperationalPolicy.NormalizeItemKind(
            itemKind,
            trackingType,
            categoryName,
            isRental);

        return string.Join('|', new[]
        {
            string.IsNullOrWhiteSpace(nameMatchKey)
                ? RentalCatalogValueNormalizer.NormalizeLooseKey(nameOriginal)
                : RentalCatalogValueNormalizer.NormalizeLooseKey(nameMatchKey),
            string.IsNullOrWhiteSpace(specificationMatchKey)
                ? RentalCatalogValueNormalizer.NormalizeLooseKey(specificationOriginal)
                : RentalCatalogValueNormalizer.NormalizeLooseKey(specificationMatchKey),
            RentalCatalogValueNormalizer.NormalizeLooseKey(categoryName),
            normalizedItemKind.Trim().ToUpperInvariant(),
            normalizedTrackingType.Trim().ToUpperInvariant()
        });
    }

    private static string NormalizeItemIdentityValue(string? value)
        => RentalCatalogValueNormalizer.NormalizeLooseKey(value);

    private static bool HasMeaningfulItemIdentityValue(string? value)
    {
        var normalized = NormalizeItemIdentityValue(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return normalized != "미상" &&
               normalized != "UNKNOWN" &&
               normalized != "NONE" &&
               normalized != "NA" &&
               normalized != "N/A" &&
               normalized != "없음";
    }

    private static bool ShouldReplacePreparedItem(ItemDto current, ItemDto incoming)
    {
        if (incoming.Revision != current.Revision)
            return incoming.Revision > current.Revision;

        var updatedComparison = DateTime.Compare(
            incoming.UpdatedAtUtc.ToUniversalTime(),
            current.UpdatedAtUtc.ToUniversalTime());
        if (updatedComparison != 0)
            return updatedComparison > 0;

        var createdComparison = DateTime.Compare(
            incoming.CreatedAtUtc.ToUniversalTime(),
            current.CreatedAtUtc.ToUniversalTime());
        if (createdComparison != 0)
            return createdComparison > 0;

        return true;
    }

    private async Task<List<TransactionDto>> PrepareScopedTransactionsAsync(
        IEnumerable<TransactionDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<TransactionDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.Transactions.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForPayments(existing.ResponsibleOfficeCode, existing.TenantCode, existing.OfficeCode))
            {
                AddClientConflict(dto, nameof(TransactionRecord), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.ResponsibleOfficeCode = _officeScopeService.ResolvePaymentResponsibleScopeForCreate(
                dto.ResponsibleOfficeCode,
                existing?.ResponsibleOfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                existing?.OfficeCode);
            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
                dto.TenantCode,
                dto.OfficeCode,
                existing?.TenantCode,
                existing?.OfficeCode);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<TransactionDto>> FilterValidTransactionsAsync(
        IEnumerable<TransactionDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<TransactionDto>();

        foreach (var dto in payload)
        {
            var originalTransactionKind = dto.TransactionKind;
            var originalLinkedInvoiceId = dto.LinkedInvoiceId;
            var originalLinkedRentalBillingProfileId = dto.LinkedRentalBillingProfileId;
            var originalLinkedRentalBillingRunId = dto.LinkedRentalBillingRunId;
            var originalCustomerId = dto.CustomerId;
            var existing = await _dbContext.Transactions.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (dto.IsDeleted && existing is null)
            {
                valid.Add(dto);
                continue;
            }

            if (existing is not null &&
                !await ValidateWritableRentalSettlementProfileReferenceAsync(
                    existing.LinkedRentalBillingProfileId,
                    dto,
                    nameof(TransactionRecord),
                    result,
                    cancellationToken))
            {
                continue;
            }

            if (dto.IsDeleted && existing is not null)
            {
                PreserveExistingTransactionStateForDelete(dto, existing);
                valid.Add(dto);
                continue;
            }

            Invoice? invoice = null;
            if (dto.LinkedInvoiceId.HasValue && dto.LinkedInvoiceId.Value != Guid.Empty)
            {
                invoice = await _dbContext.Invoices.IgnoreQueryFilters()
                    .Include(current => current.Customer)
                    .FirstOrDefaultAsync(x => x.Id == dto.LinkedInvoiceId.Value, cancellationToken);
                if (invoice is null || invoice.IsDeleted)
                {
                    if (dto.IsDeleted && existing is null)
                        continue;

                    if (string.Equals(dto.TransactionKind, "선수금차감", StringComparison.OrdinalIgnoreCase))
                    {
                        AddClientConflict(dto, nameof(TransactionRecord),
                            $"Referenced invoice was not found: {dto.LinkedInvoiceId}.", result);
                        continue;
                    }

                    dto.LinkedInvoiceId = null;
                    dto.SettlementAmount = 0m;
                    dto.TransactionKind = NormalizeTransactionKindWithoutInvoice(dto.TransactionKind, dto.PaymentTotal, dto.ReceiptTotal);
                    invoice = null;
                    AddNotice(
                        result,
                        nameof(TransactionRecord),
                        dto.Id,
                        "transaction-invoice-link-cleared",
                        $"수금/지급 '{dto.Id:D}'은(는) 연결 전표를 찾지 못해 전표 연결을 해제하고 일반 처리 기준으로 보정했습니다.");
                }
                else if (!_officeScopeService.CanWriteOfficeForPayments(invoice.ResponsibleOfficeCode, invoice.TenantCode, invoice.OfficeCode))
                {
                    AddClientConflict(dto, nameof(TransactionRecord),
                        $"Referenced invoice is outside the writable payment office scope: {dto.LinkedInvoiceId}.", result);
                    continue;
                }

                if (invoice is not null && (invoice.Customer is null || invoice.Customer.IsDeleted))
                {
                    if (dto.IsDeleted)
                    {
                        valid.Add(dto);
                        continue;
                    }

                    AddClientConflict(dto, nameof(TransactionRecord),
                        $"Referenced invoice customer was not found: {invoice.CustomerId}.", result);
                    continue;
                }

                if (invoice is not null && existing is null && !dto.IsDeleted && dto.ExpectedRevision > 0 && invoice.Revision != dto.ExpectedRevision)
                {
                    AddClientConflict(dto, nameof(TransactionRecord),
                        $"Referenced invoice revision mismatch. client={dto.ExpectedRevision}, server={invoice.Revision}", result);
                    continue;
                }

                var transactionSettlementAmount = Math.Abs(dto.SettlementAmount);
                if (invoice is not null && existing is null && !dto.IsDeleted && dto.ExpectedRevision > 0 && transactionSettlementAmount > 0m)
                {
                    var serverSettledAmounts = await _dbContext.Payments.IgnoreQueryFilters()
                        .Where(payment =>
                            payment.InvoiceId == invoice.Id &&
                            !payment.IsDeleted &&
                            payment.Id != dto.Id)
                        .Select(payment => payment.Amount)
                        .ToListAsync(cancellationToken);
                    var outstandingAmount = Math.Max(0m, invoice.TotalAmount - serverSettledAmounts.Sum());
                    if (transactionSettlementAmount > outstandingAmount)
                    {
                        AddClientConflict(dto, nameof(TransactionRecord),
                            $"Transaction amount exceeds current outstanding balance. outstanding={outstandingAmount:N0}, amount={transactionSettlementAmount:N0}.", result);
                        continue;
                    }
                }

                if (invoice is not null &&
                    invoice.LinkedRentalBillingProfileId.HasValue &&
                    invoice.LinkedRentalBillingProfileId.Value != Guid.Empty)
                {
                    if (dto.LinkedRentalBillingProfileId != invoice.LinkedRentalBillingProfileId)
                        dto.LinkedRentalBillingProfileId = invoice.LinkedRentalBillingProfileId;
                    if (dto.LinkedRentalBillingRunId != invoice.LinkedRentalBillingRunId)
                        dto.LinkedRentalBillingRunId = invoice.LinkedRentalBillingRunId;
                }
                else if (invoice is not null &&
                         (dto.LinkedRentalBillingProfileId.HasValue ||
                          dto.LinkedRentalBillingRunId.HasValue))
                {
                    dto.LinkedRentalBillingProfileId = null;
                    dto.LinkedRentalBillingRunId = null;
                    dto.TransactionKind = ResolveLinkedTransactionKind(invoice);
                }
            }

            RentalBillingProfile? profile = null;
            if (dto.LinkedRentalBillingProfileId.HasValue && dto.LinkedRentalBillingProfileId.Value != Guid.Empty)
            {
                profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.LinkedRentalBillingProfileId.Value, cancellationToken);
                if (profile is null || profile.IsDeleted)
                {
                    if (dto.IsDeleted && existing is null)
                        continue;

                    dto.LinkedRentalBillingProfileId = null;
                    dto.SettlementAmount = 0m;
                    if (string.Equals(dto.TransactionKind, "렌탈수금", StringComparison.OrdinalIgnoreCase))
                        dto.TransactionKind = "일반수금";
                    profile = null;
                    AddNotice(
                        result,
                        nameof(TransactionRecord),
                        dto.Id,
                        "transaction-rental-link-cleared",
                        $"수금/지급 '{dto.Id:D}'은(는) 연결 렌탈 청구 대상을 찾지 못해 렌탈 연결을 해제하고 일반 수금으로 보정했습니다.");
                }
                else if (!_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode))
                {
                    AddClientConflict(dto, nameof(TransactionRecord),
                        $"Referenced rental billing profile is outside the writable office scope: {dto.LinkedRentalBillingProfileId}.", result);
                    continue;
                }

                if (profile is not null &&
                    !dto.IsDeleted &&
                    !ValidateActiveRentalBillingRunReference(
                        profile.BillingRunsJson,
                        dto.LinkedRentalBillingRunId,
                        dto,
                        nameof(TransactionRecord),
                        result))
                {
                    continue;
                }
            }

            if (!dto.IsDeleted &&
                dto.LinkedRentalBillingProfileId.HasValue &&
                dto.LinkedRentalBillingProfileId.Value != Guid.Empty &&
                dto.SettlementAmount <= 0m)
            {
                dto.LinkedRentalBillingProfileId = null;
                dto.LinkedRentalBillingRunId = null;
                if (string.Equals(dto.TransactionKind, "렌탈수금", StringComparison.OrdinalIgnoreCase))
                    dto.TransactionKind = "일반수금";
                profile = null;
                AddNotice(
                    result,
                    nameof(TransactionRecord),
                    dto.Id,
                    "transaction-rental-zero-link-cleared",
                    $"수금/지급 '{dto.Id:D}'은(는) 정산금액이 0원이라 렌탈 청구 연결을 해제하고 일반 수금 기준으로 보정했습니다.");
            }

            Customer? customer = null;
            string? customerRelinkMessage = null;
            if (invoice?.Customer is not null && !invoice.Customer.IsDeleted)
            {
                customer = invoice.Customer;
                customerRelinkMessage = $"수금/지급 '{dto.Id:D}'의 거래처를 연결 전표 기준으로 다시 맞췄습니다.";
            }
            else if (profile?.CustomerId is Guid profileCustomerId && profileCustomerId != Guid.Empty)
            {
                customer = await _dbContext.Customers.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == profileCustomerId, cancellationToken);
                if (customer is null || customer.IsDeleted)
                {
                    AddClientConflict(dto, nameof(TransactionRecord),
                        $"Referenced rental billing profile customer was not found: {profileCustomerId}.", result);
                    continue;
                }

                var profileTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                    profile.TenantCode,
                    profile.OfficeCode,
                    profile.TenantCode,
                    profile.ResponsibleOfficeCode);
                if (!CanReadCustomerForRentalReference(customer) ||
                    !CustomerReferenceTenantMatches(customer, profileTenantCode))
                {
                    AddClientConflict(dto, nameof(TransactionRecord),
                        $"Referenced rental billing profile customer is outside the readable tenant scope: {profileCustomerId}.", result);
                    continue;
                }

                customerRelinkMessage = $"수금/지급 '{dto.Id:D}'의 거래처를 연결 렌탈 청구 기준으로 다시 맞췄습니다.";
            }
            else if (dto.CustomerId != Guid.Empty)
            {
                customer = await _dbContext.Customers.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
            }

            if (customer is null || customer.IsDeleted)
            {
                if (dto.IsDeleted && existing is null)
                    continue;

                AddClientConflict(dto, nameof(TransactionRecord),
                    $"Referenced customer was not found: {dto.CustomerId}.", result);
                continue;
            }

            if (dto.CustomerId != customer.Id)
            {
                dto.CustomerId = customer.Id;
                if (originalCustomerId != customer.Id)
                {
                    AddNotice(
                        result,
                        nameof(TransactionRecord),
                        dto.Id,
                        "transaction-customer-relinked",
                        customerRelinkMessage ??
                        $"수금/지급 '{dto.Id:D}'의 거래처를 서버 기준으로 다시 맞췄습니다.");
                }
            }

            var authoritativeTenantCode = invoice?.TenantCode
                ?? profile?.TenantCode
                ?? customer.TenantCode;
            var authoritativeOfficeCode = invoice?.OfficeCode
                ?? profile?.OfficeCode
                ?? customer.OfficeCode;
            var authoritativeResponsibleOfficeCode = invoice?.ResponsibleOfficeCode
                ?? profile?.ResponsibleOfficeCode
                ?? customer.ResponsibleOfficeCode;
            var canWriteAuthoritativePaymentScope =
                _officeScopeService.CanWriteOfficeForPayments(
                    authoritativeResponsibleOfficeCode,
                    authoritativeTenantCode,
                    authoritativeOfficeCode);
            if (!canWriteAuthoritativePaymentScope)
            {
                AddClientConflict(dto, nameof(TransactionRecord),
                    $"Referenced customer is outside the writable office scope: {dto.CustomerId}.", result);
                continue;
            }

            var resolvedResponsibleOfficeCode = _officeScopeService.ResolvePaymentResponsibleScopeForCreate(
                authoritativeResponsibleOfficeCode,
                authoritativeResponsibleOfficeCode);
            var resolvedOfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                authoritativeOfficeCode,
                resolvedResponsibleOfficeCode,
                authoritativeOfficeCode);
            var resolvedTenantCode = _officeScopeService.ResolveTenantForCreate(
                authoritativeTenantCode,
                resolvedOfficeCode,
                authoritativeTenantCode,
                authoritativeOfficeCode);

            dto.ResponsibleOfficeCode = resolvedResponsibleOfficeCode;
            dto.OfficeCode = resolvedOfficeCode;
            dto.TenantCode = resolvedTenantCode;

            if (!string.Equals(originalTransactionKind, dto.TransactionKind, StringComparison.OrdinalIgnoreCase))
            {
                AddNotice(
                    result,
                    nameof(TransactionRecord),
                    dto.Id,
                    "transaction-kind-normalized",
                    $"수금/지급 '{dto.Id:D}'의 처리구분을 '{originalTransactionKind}'에서 '{dto.TransactionKind}'(으)로 보정했습니다.");
            }

            if (originalLinkedInvoiceId != dto.LinkedInvoiceId)
            {
                AddNotice(
                    result,
                    nameof(TransactionRecord),
                    dto.Id,
                    "transaction-link-updated",
                    $"수금/지급 '{dto.Id:D}'의 연결 전표 값이 서버 기준으로 조정되었습니다.");
            }

            if (originalLinkedRentalBillingProfileId != dto.LinkedRentalBillingProfileId ||
                originalLinkedRentalBillingRunId != dto.LinkedRentalBillingRunId)
            {
                AddNotice(
                    result,
                    nameof(TransactionRecord),
                    dto.Id,
                    "transaction-rental-link-updated",
                    $"수금/지급 '{dto.Id:D}'의 연결 렌탈 청구 값이 서버 기준으로 조정되었습니다.");
            }

            valid.Add(dto);
        }

        return valid;
    }

    private static void ApplyTransactionMutation(TransactionRecord entity, TransactionDto dto)
    {
        if (dto.IsDeleted)
        {
            entity.IsDeleted = true;
            return;
        }

        entity.Apply(dto);
    }

    private static void PreserveExistingTransactionStateForDelete(
        TransactionDto dto,
        TransactionRecord existing)
    {
        dto.CustomerId = existing.CustomerId;
        dto.TenantCode = existing.TenantCode;
        dto.OfficeCode = existing.OfficeCode;
        dto.ResponsibleOfficeCode = existing.ResponsibleOfficeCode;
        dto.TransactionDate = existing.TransactionDate;
        dto.TransactionKind = existing.TransactionKind;
        dto.LinkedInvoiceId = existing.LinkedInvoiceId;
        dto.LinkedInvoiceNumber = existing.LinkedInvoiceNumber;
        dto.LinkedRentalBillingProfileId = existing.LinkedRentalBillingProfileId;
        dto.LinkedRentalBillingRunId = existing.LinkedRentalBillingRunId;
        dto.SettlementAmount = existing.SettlementAmount;
        dto.AdvanceDelta = existing.AdvanceDelta;
        dto.PrepaidDelta = existing.PrepaidDelta;
        dto.CashReceipt = existing.CashReceipt;
        dto.CardReceipt = existing.CardReceipt;
        dto.BankReceipt = existing.BankReceipt;
        dto.DiscountApplied = existing.DiscountApplied;
        dto.ReceiptTotal = existing.ReceiptTotal;
        dto.CashPayment = existing.CashPayment;
        dto.CardPayment = existing.CardPayment;
        dto.BankPayment = existing.BankPayment;
        dto.DiscountReceived = existing.DiscountReceived;
        dto.PaymentTotal = existing.PaymentTotal;
        dto.Note = existing.Note;
        dto.Memo = existing.Memo;
        dto.IsDeleted = true;
    }

    private static string NormalizeTransactionKindWithoutInvoice(string? kind, decimal paymentTotal, decimal receiptTotal)
    {
        if (string.Equals(kind, "전표지급", StringComparison.OrdinalIgnoreCase))
            return "일반지급";

        if (string.Equals(kind, "전표수금", StringComparison.OrdinalIgnoreCase))
            return "일반수금";

        return paymentTotal > 0m && receiptTotal <= 0m
            ? "일반지급"
            : "일반수금";
    }

    private async Task<List<TransactionAttachmentDto>> FilterValidTransactionAttachmentsAsync(
        IEnumerable<TransactionAttachmentDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<TransactionAttachmentDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.TransactionAttachments.IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == dto.Id, cancellationToken);
            TransactionRecord? existingTransaction = null;
            if (existing is not null)
            {
                existingTransaction = await _dbContext.Transactions.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(current => current.Id == existing.TransactionId, cancellationToken);
            }

            if (existing is not null &&
                existingTransaction is null &&
                !_officeScopeService.HasGlobalDataScope)
            {
                AddClientConflict(dto, nameof(TransactionAttachment),
                    $"Existing transaction reference is missing, so current account cannot verify writable office scope: {existing.TransactionId}.", result);
                continue;
            }

            if (existingTransaction is not null &&
                !_officeScopeService.CanWriteOfficeForPayments(existingTransaction.ResponsibleOfficeCode, existingTransaction.TenantCode, existingTransaction.OfficeCode))
            {
                AddClientConflict(dto, nameof(TransactionAttachment),
                    $"Existing transaction is outside the writable office scope: {existingTransaction.Id}.", result);
                continue;
            }

            var transaction = await _dbContext.Transactions.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.TransactionId, cancellationToken);
            if (dto.TransactionId == Guid.Empty || transaction is null || transaction.IsDeleted)
            {
                if (dto.IsDeleted && existing is null)
                    continue;

                if (existing is not null)
                {
                    dto.TransactionId = existing.TransactionId;
                    dto.IsDeleted = true;
                    valid.Add(dto);
                    continue;
                }

                AddClientConflict(dto, nameof(TransactionAttachment),
                    $"Referenced transaction was not found: {dto.TransactionId}.", result);
                continue;
            }

            if (!_officeScopeService.CanWriteOfficeForPayments(transaction.ResponsibleOfficeCode, transaction.TenantCode, transaction.OfficeCode))
            {
                AddClientConflict(dto, nameof(TransactionAttachment),
                    $"Referenced transaction is outside the writable office scope: {dto.TransactionId}.", result);
                continue;
            }

            if (!dto.IsDeleted)
            {
                var fileContent = dto.FileContent ?? [];
                if (fileContent.Length == 0)
                {
                    AddClientConflict(dto, nameof(TransactionAttachment), "Attachment file content is required.", result);
                    continue;
                }

                var fileName = Path.GetFileName(dto.FileName ?? string.Empty);
                var mimeType = EvidenceAttachmentFilePolicy.NormalizeContentType(dto.MimeType, fileName);
                if (fileContent.LongLength > EvidenceAttachmentFilePolicy.MaxFileSizeBytes)
                {
                    AddClientConflict(dto, nameof(TransactionAttachment),
                        $"Attachment file size exceeds the {EvidenceAttachmentFilePolicy.MaxFileSizeBytes / (1024 * 1024)}MB limit.", result);
                    continue;
                }

                if (!EvidenceAttachmentFilePolicy.IsAllowedFileType(fileName, mimeType))
                {
                    AddClientConflict(dto, nameof(TransactionAttachment),
                        "Only PDF or image attachments are allowed.", result);
                    continue;
                }

                if (!EvidenceAttachmentFilePolicy.ContentMatchesFileType(fileName, mimeType, fileContent))
                {
                    AddClientConflict(dto, nameof(TransactionAttachment),
                        "Attachment file content does not match the declared file type.", result);
                    continue;
                }

                dto.FileName = fileName;
                dto.MimeType = mimeType;

                if (fileContent.Length > 0)
                {
                    dto.FileSize = fileContent.LongLength;
                    dto.FileHash = ComputeSha256Hex(fileContent);
                }
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<InventoryTransferDto>> PrepareScopedInventoryTransfersAsync(
        IEnumerable<InventoryTransferDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<InventoryTransferDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.InventoryTransfers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            var purgeRecord = existing is null && dto.Id != Guid.Empty
                ? await _dbContext.RecycleBinPurgeRecords
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        record =>
                            record.EntityId == dto.Id &&
                            (record.Kind == "inventory-transfer" ||
                             record.Kind == "inventorytransfer"),
                        cancellationToken)
                : null;
            var canWriteExisting = existing is not null
                ? _officeScopeService.CanWriteInventoryTransferRoute(
                    existing.SourceOfficeCode,
                    existing.TargetOfficeCode,
                    existing.TenantCode)
                : purgeRecord is null ||
                  _officeScopeService.CanWriteInventoryTransferRoute(
                      purgeRecord.SourceOfficeCode,
                      purgeRecord.TargetOfficeCode,
                      purgeRecord.TenantCode);
            if (!canWriteExisting)
            {
                AddClientConflict(dto, nameof(InventoryTransfer), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.SourceOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeLoose(
                dto.SourceOfficeCode,
                dto.FromWarehouseCode,
                existing?.SourceOfficeCode ?? OfficeCodeCatalog.Usenet);
            dto.TargetOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeLoose(
                dto.TargetOfficeCode,
                dto.ToWarehouseCode,
                existing?.TargetOfficeCode ?? OfficeCodeCatalog.Yeonsu);
            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
                dto.TenantCode,
                dto.SourceOfficeCode,
                existing?.TenantCode,
                existing?.SourceOfficeCode);
            dto.FromWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(dto.SourceOfficeCode);
            dto.ToWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(dto.TargetOfficeCode);
            if (purgeRecord is not null &&
                !InventoryTransferRouteMatchesPurgeRecord(
                    dto,
                    purgeRecord))
            {
                AddClientConflict(
                    dto,
                    nameof(InventoryTransfer),
                    "Inventory transfer route does not match the durable purge record.",
                    result);
                continue;
            }

            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<InventoryTransferDto>> FilterValidInventoryTransfersAsync(
        IEnumerable<InventoryTransferDto> payload,
        IReadOnlySet<string> ambiguousIncomingMutationIds,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<InventoryTransferDto>();
        var materializedPayload = payload.ToList();
        var duplicateActiveLineIds = materializedPayload
            .SelectMany(dto => (dto.Lines ?? [])
                .Where(line => !line.IsDeleted && line.Id != Guid.Empty)
                .Select(line => line.Id))
            .GroupBy(lineId => lineId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        foreach (var dto in materializedPayload)
        {
            var mutationId = NormalizeMutationId(dto.MutationId);
            if (!string.IsNullOrWhiteSpace(mutationId) &&
                ambiguousIncomingMutationIds.Contains(mutationId))
            {
                AddClientConflict(
                    dto,
                    nameof(InventoryTransfer),
                    AmbiguousIncomingMutationIdConflictReason,
                    result);
                continue;
            }

            var existing = await _dbContext.InventoryTransfers.IgnoreQueryFilters()
                .Include(x => x.Lines)
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            var sourceTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(dto.TenantCode, dto.SourceOfficeCode);
            var targetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, dto.TargetOfficeCode);
            if (!string.Equals(sourceTenantCode, targetTenantCode, StringComparison.OrdinalIgnoreCase))
            {
                AddClientConflict(
                    dto,
                    nameof(InventoryTransfer),
                    "재고이동은 같은 업체 내부 지점 간 이동만 지원합니다. 다른 업체로 보내려면 대상 업체에 품목을 먼저 등록/복제한 뒤 처리하세요.",
                    result);
                continue;
            }

            var normalizedStatus = InventoryTransferStatusNormalizer.Normalize(
                dto.TransferStatus,
                dto.ReceivedByUsername,
                dto.ReceivedAtUtc,
                dto.RejectedByUsername,
                dto.RejectedAtUtc);

            if (HasStrictProcessedMutationReplay(
                    dto,
                    nameof(InventoryTransfer)))
            {
                if (!CanAcceptInventoryTransferExactReplayScope(
                        dto,
                        existing,
                        normalizedStatus,
                        out var exactReplayScopeConflictReason))
                {
                    AddClientConflict(
                        dto,
                        nameof(InventoryTransfer),
                        exactReplayScopeConflictReason,
                        result);
                    continue;
                }

                valid.Add(dto);
                continue;
            }

            if (existing is null &&
                (dto.IsDeleted ||
                 await HasDurableInventoryTransferPurgeRecordAsync(
                     dto.Id,
                     cancellationToken)))
            {
                if (!CanAcceptInventoryTransferScopeMutation(
                        dto,
                        existing,
                        normalizedStatus,
                        out var acknowledgementScopeConflictReason))
                {
                    AddClientConflict(
                        dto,
                        nameof(InventoryTransfer),
                        acknowledgementScopeConflictReason,
                        result);
                    continue;
                }

                // Missing deletes and prior-incarnation mutations are handled as
                // receipt-only acknowledgements after concurrency validation. Do
                // not let stale or oversized child rows block that idempotent path.
                valid.Add(dto);
                continue;
            }

            var activeLines = (dto.Lines ?? [])
                .Where(line => !line.IsDeleted)
                .ToList();
            var duplicateActiveLineId = activeLines
                .Where(line => line.Id != Guid.Empty)
                .Select(line => line.Id)
                .FirstOrDefault(duplicateActiveLineIds.Contains);
            if (duplicateActiveLineId != Guid.Empty)
            {
                AddClientConflict(
                    dto,
                    nameof(InventoryTransfer),
                    $"Active inventory transfer line id is duplicated within the same Push payload: {duplicateActiveLineId}.",
                    result);
                continue;
            }

            var nonEmptyActiveLineIds = activeLines
                .Where(line => line.Id != Guid.Empty)
                .Select(line => line.Id)
                .Distinct()
                .ToList();
            var foreignOwnedLine = nonEmptyActiveLineIds.Count == 0
                ? null
                : await _dbContext.InventoryTransferLines.IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(line =>
                        nonEmptyActiveLineIds.Contains(line.Id) &&
                        line.TransferId != dto.Id)
                    .Select(line => new { line.Id, line.TransferId })
                    .FirstOrDefaultAsync(cancellationToken);
            if (foreignOwnedLine is not null)
            {
                AddClientConflict(
                    dto,
                    nameof(InventoryTransfer),
                    $"Active inventory transfer line id belongs to a different inventory transfer: {foreignOwnedLine.Id} ({foreignOwnedLine.TransferId}).",
                    result);
                continue;
            }

            if (existing?.IsDeleted == true && !dto.IsDeleted)
            {
                await AddServerConflictAsync(
                    dto,
                    existing,
                    nameof(InventoryTransfer),
                    "Server inventory transfer is deleted. Restore it through the recycle bin.",
                    result,
                    cancellationToken);
                continue;
            }

            if (!CanAcceptInventoryTransferScopeMutation(dto, existing, normalizedStatus, out var scopeConflictReason))
            {
                AddClientConflict(dto, nameof(InventoryTransfer), scopeConflictReason, result);
                continue;
            }

            if (!dto.IsDeleted && activeLines.Count == 0)
            {
                AddClientConflict(
                    dto,
                    nameof(InventoryTransfer),
                    "Non-deleted inventory transfer must contain at least one active item-backed line.",
                    result);
                continue;
            }

            var itemlessLine = dto.IsDeleted
                ? null
                : activeLines.FirstOrDefault(line =>
                    !line.ItemId.HasValue || line.ItemId.Value == Guid.Empty);
            if (itemlessLine is not null)
            {
                AddClientConflict(
                    dto,
                    nameof(InventoryTransfer),
                    $"Active inventory transfer line must reference a non-empty item id: {itemlessLine.Id}.",
                    result);
                continue;
            }

            var invalidQuantityLine = dto.IsDeleted
                ? null
                : activeLines.FirstOrDefault(line =>
                    !DatabaseNumericContract.IsPositiveQuantity18Scale2(line.Quantity));
            if (invalidQuantityLine is not null)
            {
                AddClientConflict(
                    dto,
                    nameof(InventoryTransfer),
                    $"Active inventory transfer line quantity must be greater than zero and fit numeric(18,2): {invalidQuantityLine.Id}.",
                    result);
                continue;
            }

            if (string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Received, StringComparison.OrdinalIgnoreCase))
            {
                var invalidReceivedQuantityLine = activeLines.FirstOrDefault(line =>
                {
                    var receivedQuantity = line.ReceivedQuantity ?? line.Quantity;
                    return receivedQuantity < 0m ||
                           receivedQuantity > line.Quantity ||
                           receivedQuantity > DatabaseNumericContract.MaxQuantity18Scale2 ||
                           decimal.Round(
                               receivedQuantity,
                               DatabaseNumericContract.QuantityScale,
                               MidpointRounding.ToEven) != receivedQuantity;
                });
                if (invalidReceivedQuantityLine is not null)
                {
                    AddClientConflict(
                        dto,
                        nameof(InventoryTransfer),
                        $"Received inventory transfer line quantity must be nonnegative, no greater than the requested quantity, and fit numeric(18,2): {invalidReceivedQuantityLine.Id}.",
                        result);
                    continue;
                }

                foreach (var line in activeLines)
                {
                    var receivedQuantity = line.ReceivedQuantity ?? line.Quantity;
                    line.ReceivedQuantity = receivedQuantity;
                    line.QuantityDifference = receivedQuantity - line.Quantity;
                }
            }

            var lineConflict = false;
            foreach (var line in activeLines.Where(_ => !dto.IsDeleted))
            {
                var item = await _dbContext.Items.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == line.ItemId!.Value, cancellationToken);
                if (item is null || item.IsDeleted)
                {
                    AddClientConflict(dto, nameof(InventoryTransfer),
                        $"Referenced item was not found: {line.ItemId}.", result);
                    lineConflict = true;
                    break;
                }

                var itemTenantCode =
                    TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                        item.TenantCode,
                        item.OfficeCode);
                if (!string.Equals(
                        itemTenantCode,
                        sourceTenantCode,
                        StringComparison.OrdinalIgnoreCase))
                {
                    AddClientConflict(dto, nameof(InventoryTransfer),
                        $"Referenced item tenant does not match inventory transfer tenant: {line.ItemId}.", result);
                    lineConflict = true;
                    break;
                }

                if (!_officeScopeService.CanReadOfficeForItems(item.OfficeCode, item.TenantCode))
                {
                    AddClientConflict(dto, nameof(InventoryTransfer),
                        $"Referenced item is outside the readable office scope: {line.ItemId}.", result);
                    lineConflict = true;
                    break;
                }
            }

            if (lineConflict)
                continue;

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<InventoryTransferDto>>
        FilterInventoryTransferPurgeAndMissingDeleteAcknowledgementsAsync(
            IEnumerable<InventoryTransferDto> payload,
            SyncPushResult result,
            string deviceId,
            CancellationToken cancellationToken)
    {
        var valid = new List<InventoryTransferDto>();
        var purgeAcceptedEntityIdsForHistoricalConflictResolution =
            new HashSet<Guid>();
        var missingDeleteAcceptedEntityIdsForHistoricalConflictResolution =
            new HashSet<Guid>();

        foreach (var dto in payload)
        {
            var entityExists = dto.Id != Guid.Empty &&
                               await _dbContext.InventoryTransfers
                                   .IgnoreQueryFilters()
                                   .AnyAsync(
                                       transfer => transfer.Id == dto.Id,
                                       cancellationToken);
            if (entityExists)
            {
                valid.Add(dto);
                continue;
            }

            var purgeRecords = dto.Id == Guid.Empty
                ? []
                : await _dbContext.RecycleBinPurgeRecords
                    .AsNoTracking()
                    .Where(record =>
                        record.EntityId == dto.Id &&
                        (record.Kind == "inventory-transfer" ||
                         record.Kind == "inventorytransfer"))
                    .ToListAsync(cancellationToken);
            if (purgeRecords.Count > 0)
            {
                if (purgeRecords.Any(record =>
                        !InventoryTransferRouteMatchesPurgeRecord(dto, record)))
                {
                    AddClientConflict(
                        dto,
                        nameof(InventoryTransfer),
                        "Durable inventory transfer purge receipts have an incompatible route scope.",
                        result);
                    continue;
                }

                var purgeRecord = purgeRecords
                    .OrderByDescending(record => record.Revision)
                    .ThenByDescending(record =>
                        NormalizeConflictUtc(record.UpdatedAtUtc))
                    .First();
                if (!IsPriorInventoryTransferIncarnation(dto, purgeRecord))
                {
                    AddClientConflict(
                        dto,
                        nameof(InventoryTransfer),
                        "Inventory transfer mutation is newer than or ambiguous with the durable purge record. Pull before retrying.",
                        result);
                    continue;
                }

                var exactReplay = HasExactProcessedMutationReplay(
                    dto,
                    nameof(InventoryTransfer));
                if (TryAcceptDuplicateMutation(
                        dto,
                        nameof(InventoryTransfer),
                        result,
                        purgeAcceptedEntityIdsForHistoricalConflictResolution))
                {
                    if (exactReplay)
                    {
                        purgeAcceptedEntityIdsForHistoricalConflictResolution.Add(
                            dto.Id);
                        AddInventoryTransferPurgeAcknowledgement(
                            result,
                            dto.Id,
                            purgeRecord);
                    }

                    continue;
                }

                RegisterProcessedMutation(
                    dto,
                    nameof(InventoryTransfer),
                    deviceId);
                result.AcceptedCount++;
                purgeAcceptedEntityIdsForHistoricalConflictResolution.Add(dto.Id);
                AddInventoryTransferPurgeAcknowledgement(
                    result,
                    dto.Id,
                    purgeRecord);
                continue;
            }

            if (dto.IsDeleted)
            {
                if (dto.Id == Guid.Empty)
                {
                    AddClientConflict(
                        dto,
                        nameof(InventoryTransfer),
                        "InventoryTransfer delete requires an id.",
                        result);
                    continue;
                }

                var exactReplayEntityIds = new HashSet<Guid>();
                if (!TryAcceptDuplicateMutation(
                        dto,
                        nameof(InventoryTransfer),
                        result,
                        exactReplayEntityIds))
                {
                    RegisterProcessedMutation(
                        dto,
                        nameof(InventoryTransfer),
                        deviceId);
                    result.AcceptedCount++;
                }

                AddMissingInventoryTransferDeleteAcknowledgement(result, dto);
                missingDeleteAcceptedEntityIdsForHistoricalConflictResolution.Add(
                    dto.Id);
                continue;
            }

            valid.Add(dto);
        }

        await ResolveHistoricalConflictsAsync(
            nameof(InventoryTransfer),
            purgeAcceptedEntityIdsForHistoricalConflictResolution,
            "The inventory transfer was permanently purged, so the prior-incarnation mutation was acknowledged without recreating data.",
            cancellationToken);
        await ResolveHistoricalConflictsAsync(
            nameof(InventoryTransfer),
            missingDeleteAcceptedEntityIdsForHistoricalConflictResolution,
            "The inventory transfer no longer exists, so the delete mutation was acknowledged without creating a tombstone.",
            cancellationToken);

        return valid;
    }

    private Task<bool> HasDurableInventoryTransferPurgeRecordAsync(
        Guid transferId,
        CancellationToken cancellationToken)
        => transferId == Guid.Empty
            ? Task.FromResult(false)
            : _dbContext.RecycleBinPurgeRecords
                .AsNoTracking()
                .AnyAsync(
                    record =>
                        record.EntityId == transferId &&
                        (record.Kind == "inventory-transfer" ||
                         record.Kind == "inventorytransfer"),
                    cancellationToken);

    private static bool IsPriorInventoryTransferIncarnation(
        InventoryTransferDto dto,
        RecycleBinPurgeRecord purgeRecord)
    {
        var knownRevision = Math.Max(dto.ExpectedRevision, dto.Revision);
        if (knownRevision > 0)
            return knownRevision <= purgeRecord.Revision;

        if (dto.IsDeleted)
            return true;

        var legacyTimestamps = new[]
            {
                dto.MutationCreatedAtUtc.GetValueOrDefault(),
                dto.UpdatedAtUtc
            }
            .Where(timestamp => timestamp != default)
            .Select(NormalizeConflictUtc)
            .ToList();
        return legacyTimestamps.Count > 0 &&
               legacyTimestamps.Max() <=
               NormalizeConflictUtc(purgeRecord.PurgedAtUtc);
    }

    private static void AddInventoryTransferPurgeAcknowledgement(
        SyncPushResult result,
        Guid transferId,
        RecycleBinPurgeRecord purgeRecord)
    {
        var purgeRecordDto = purgeRecord.ToDto();
        if (!result.PurgeRecords.Any(record => record.Id == purgeRecordDto.Id))
            result.PurgeRecords.Add(purgeRecordDto);

        if (!result.AcceptedRevisions.Any(revision =>
                string.Equals(
                    revision.EntityName,
                    nameof(InventoryTransfer),
                    StringComparison.OrdinalIgnoreCase) &&
                revision.EntityId == transferId))
        {
            result.AcceptedRevisions.Add(new SyncAcceptedRevisionDto
            {
                EntityName = nameof(InventoryTransfer),
                EntityId = transferId,
                Revision = purgeRecord.Revision,
                UpdatedAtUtc = purgeRecord.UpdatedAtUtc,
                IsDeleted = true
            });
        }

        AddNotice(
            result,
            nameof(InventoryTransfer),
            transferId,
            "inventory-transfer-purged-mutation-noop",
            "The inventory transfer was already permanently removed. The prior mutation was acknowledged without recreating data; pull to continue.");
    }

    private static void AddMissingInventoryTransferDeleteAcknowledgement(
        SyncPushResult result,
        InventoryTransferDto dto)
    {
        if (dto.Id == Guid.Empty ||
            result.AcceptedRevisions.Any(revision =>
                string.Equals(
                    revision.EntityName,
                    nameof(InventoryTransfer),
                    StringComparison.OrdinalIgnoreCase) &&
                revision.EntityId == dto.Id))
        {
            return;
        }

        result.AcceptedRevisions.Add(new SyncAcceptedRevisionDto
        {
            EntityName = nameof(InventoryTransfer),
            EntityId = dto.Id,
            Revision = Math.Max(dto.ExpectedRevision, dto.Revision),
            UpdatedAtUtc = NormalizeUtc(dto.UpdatedAtUtc),
            IsDeleted = true
        });
    }

    private async Task<List<InventoryTransferDto>> FilterInventoryTransferConcurrencyConflictsAsync(
        IEnumerable<InventoryTransferDto> payload,
        IReadOnlySet<string> ambiguousIncomingMutationIds,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<InventoryTransferDto>();
        foreach (var dto in payload)
        {
            if (HasExactProcessedMutationReplay(dto, nameof(InventoryTransfer)))
            {
                valid.Add(dto);
                continue;
            }

            var mutationId = NormalizeMutationId(dto.MutationId);
            if (!string.IsNullOrWhiteSpace(mutationId) &&
                ambiguousIncomingMutationIds.Contains(mutationId))
            {
                AddClientConflict(
                    dto,
                    nameof(InventoryTransfer),
                    AmbiguousIncomingMutationIdConflictReason,
                    result);
                continue;
            }

            if (ItemWarehouseStockMutationReceipt.IsReservedMutationId(mutationId))
            {
                AddClientConflict(
                    dto,
                    nameof(InventoryTransfer),
                    "Mutation id uses a server-reserved receipt namespace.",
                    result);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(mutationId) &&
                _processedMutationsById.ContainsKey(mutationId))
            {
                AddClientConflict(
                    dto,
                    nameof(InventoryTransfer),
                    "Mutation id was already processed with a different entity, expected revision, or payload.",
                    result);
                continue;
            }

            var entity = await _dbContext.InventoryTransfers
                .IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .FirstOrDefaultAsync(
                    transfer => transfer.Id == dto.Id,
                    cancellationToken);
            if (entity is null ||
                (dto.IsDeleted && entity.IsDeleted))
            {
                valid.Add(dto);
                continue;
            }

            if (HasExpectedRevisionConflict(entity, dto))
            {
                await AddServerConflictAsync(
                    dto,
                    entity,
                    nameof(InventoryTransfer),
                    BuildExpectedRevisionConflictReason(
                        dto.ExpectedRevision,
                        entity.Revision),
                    result,
                    cancellationToken);
                continue;
            }

            if (IsServerEntityNewer(entity, dto))
            {
                await AddServerConflictAsync(
                    dto,
                    entity,
                    nameof(InventoryTransfer),
                    "Server version is newer.",
                    result,
                    cancellationToken);
                continue;
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<(HashSet<string> StockKeys, HashSet<Guid> ItemIds)>
        BuildAmbiguousInvoiceStockScopeAsync(
            IReadOnlyCollection<InvoiceDto> ambiguousInvoices,
            IReadOnlyDictionary<Guid, Guid> resolvedIncomingItemIds,
            CancellationToken cancellationToken)
    {
        var stockKeys = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var itemIds = new HashSet<Guid>();
        if (ambiguousInvoices.Count == 0)
            return (stockKeys, itemIds);

        foreach (var invoice in ambiguousInvoices)
        {
            AddInvoiceStockScope(
                stockKeys,
                itemIds,
                invoice,
                resolvedIncomingItemIds);
        }

        var existingInvoiceIds = ambiguousInvoices
            .Select(invoice => invoice.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (existingInvoiceIds.Count == 0)
            return (stockKeys, itemIds);

        var existingInvoices = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Where(invoice => existingInvoiceIds.Contains(invoice.Id))
            .ToListAsync(cancellationToken);
        foreach (var invoice in existingInvoices)
            AddInvoiceStockScope(stockKeys, itemIds, invoice);

        return (stockKeys, itemIds);
    }

    private static void AddInvoiceStockScope(
        ISet<string> stockKeys,
        ISet<Guid> itemIds,
        InvoiceDto invoice,
        IReadOnlyDictionary<Guid, Guid> resolvedIncomingItemIds)
    {
        var warehouseCode =
            OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
                invoice.SourceWarehouseCode,
                invoice.ResponsibleOfficeCode,
                invoice.OfficeCode);
        foreach (var line in (invoice.Lines ?? [])
                     .Where(line =>
                         !line.IsDeleted &&
                         line.ItemId.HasValue &&
                         line.ItemId.Value != Guid.Empty))
        {
            var incomingItemId = line.ItemId!.Value;
            var itemId = resolvedIncomingItemIds.TryGetValue(
                incomingItemId,
                out var resolvedItemId)
                ? resolvedItemId
                : incomingItemId;
            itemIds.Add(itemId);
            stockKeys.Add(
                BuildItemWarehouseStockSnapshotKey(
                    itemId,
                    warehouseCode));
        }
    }

    private static void AddInvoiceStockScope(
        ISet<string> stockKeys,
        ISet<Guid> itemIds,
        Invoice invoice)
    {
        var warehouseCode =
            OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
                invoice.SourceWarehouseCode,
                invoice.ResponsibleOfficeCode,
                invoice.OfficeCode);
        foreach (var line in invoice.Lines.Where(line =>
                     !line.IsDeleted &&
                     line.ItemId.HasValue &&
                     line.ItemId.Value != Guid.Empty))
        {
            var itemId = line.ItemId!.Value;
            itemIds.Add(itemId);
            stockKeys.Add(
                BuildItemWarehouseStockSnapshotKey(
                    itemId,
                    warehouseCode));
        }
    }

    private async Task<(HashSet<string> StockKeys, HashSet<Guid> ItemIds)>
        BuildRejectedInventoryTransferStockScopeAsync(
            IReadOnlyCollection<InventoryTransferDto> requestedTransfers,
            IReadOnlyCollection<InventoryTransferDto> validTransfers,
            CancellationToken cancellationToken)
    {
        var rejectedTransfers = requestedTransfers
            .Where(dto => !validTransfers.Contains(dto))
            .ToList();
        var stockKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemIds = new HashSet<Guid>();
        if (rejectedTransfers.Count == 0)
            return (stockKeys, itemIds);

        foreach (var dto in rejectedTransfers)
            AddInventoryTransferStockScope(stockKeys, itemIds, dto);

        var rejectedTransferIds = rejectedTransfers
            .Select(dto => dto.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (rejectedTransferIds.Count == 0)
            return (stockKeys, itemIds);

        var existingTransfers = await _dbContext.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(transfer => transfer.Lines)
            .Where(transfer => rejectedTransferIds.Contains(transfer.Id))
            .ToListAsync(cancellationToken);
        foreach (var transfer in existingTransfers)
            AddInventoryTransferStockScope(stockKeys, itemIds, transfer);

        return (stockKeys, itemIds);
    }

    private static void AddInventoryTransferStockScope(
        ISet<string> stockKeys,
        ISet<Guid> itemIds,
        InventoryTransferDto transfer)
    {
        var fromWarehouseCode =
            OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
                transfer.FromWarehouseCode,
                transfer.SourceOfficeCode,
                transfer.SourceOfficeCode);
        var toWarehouseCode =
            OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
                transfer.ToWarehouseCode,
                transfer.TargetOfficeCode,
                transfer.TargetOfficeCode);
        foreach (var line in (transfer.Lines ?? [])
                     .Where(line =>
                         !line.IsDeleted &&
                         line.ItemId.HasValue &&
                         line.ItemId.Value != Guid.Empty))
        {
            var itemId = line.ItemId!.Value;
            itemIds.Add(itemId);
            stockKeys.Add(
                BuildItemWarehouseStockSnapshotKey(
                    itemId,
                    fromWarehouseCode));
            stockKeys.Add(
                BuildItemWarehouseStockSnapshotKey(
                    itemId,
                    toWarehouseCode));
        }
    }

    private static void AddInventoryTransferStockScope(
        ISet<string> stockKeys,
        ISet<Guid> itemIds,
        InventoryTransfer transfer)
    {
        var fromWarehouseCode =
            OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
                transfer.FromWarehouseCode,
                transfer.SourceOfficeCode,
                transfer.SourceOfficeCode);
        var toWarehouseCode =
            OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
                transfer.ToWarehouseCode,
                transfer.TargetOfficeCode,
                transfer.TargetOfficeCode);
        foreach (var line in transfer.Lines.Where(line =>
                     !line.IsDeleted &&
                     line.ItemId.HasValue &&
                     line.ItemId.Value != Guid.Empty))
        {
            var itemId = line.ItemId!.Value;
            itemIds.Add(itemId);
            stockKeys.Add(
                BuildItemWarehouseStockSnapshotKey(
                    itemId,
                    fromWarehouseCode));
            stockKeys.Add(
                BuildItemWarehouseStockSnapshotKey(
                    itemId,
                    toWarehouseCode));
        }
    }

    private bool CanAcceptInventoryTransferExactReplayScope(
        InventoryTransferDto dto,
        InventoryTransfer? existing,
        string normalizedStatus,
        out string reason)
    {
        reason = string.Empty;
        var candidateSourceOffice =
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                dto.SourceOfficeCode);
        var candidateTargetOffice =
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                dto.TargetOfficeCode);
        var candidateTenant =
            TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                dto.TenantCode,
                candidateSourceOffice);
        if (existing is not null)
        {
            var existingSourceOffice =
                OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                    existing.SourceOfficeCode);
            var existingTargetOffice =
                OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                    existing.TargetOfficeCode);
            var existingTenant =
                TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                    existing.TenantCode,
                    existingSourceOffice);
            if (!string.Equals(
                    candidateTenant,
                    existingTenant,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    candidateSourceOffice,
                    existingSourceOffice,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    candidateTargetOffice,
                    existingTargetOffice,
                    StringComparison.OrdinalIgnoreCase))
            {
                reason =
                    "Exact inventory transfer replay route no longer matches the stored transfer.";
                return false;
            }
        }

        var canWriteSource =
            _officeScopeService.CanWriteOfficeForDeliveries(
                candidateSourceOffice,
                candidateTenant);
        var canWriteTarget =
            _officeScopeService.CanWriteOfficeForDeliveries(
                candidateTargetOffice,
                candidateTenant);
        var requiresTargetScope =
            !dto.IsDeleted &&
            IsFinalInventoryTransferStatus(
                normalizedStatus);
        if (requiresTargetScope
                ? canWriteTarget
                : canWriteSource)
        {
            return true;
        }

        reason = requiresTargetScope
            ? $"Exact inventory transfer replay target office is outside the writable delivery scope: {candidateTargetOffice}."
            : $"Exact inventory transfer replay source office is outside the writable delivery scope: {candidateSourceOffice}.";
        return false;
    }

    private bool CanAcceptInventoryTransferScopeMutation(
        InventoryTransferDto dto,
        InventoryTransfer? existing,
        string normalizedStatus,
        out string reason)
    {
        reason = string.Empty;

        var candidateSourceOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(dto.SourceOfficeCode);
        var candidateTargetOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(dto.TargetOfficeCode);
        var candidateTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(dto.TenantCode, candidateSourceOffice);
        var existingTenant = existing is null
            ? candidateTenant
            : TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(existing.TenantCode, existing.SourceOfficeCode);

        var canWriteCandidateSource = _officeScopeService.CanWriteOfficeForDeliveries(candidateSourceOffice, candidateTenant);
        var canWriteCandidateTarget = _officeScopeService.CanWriteOfficeForDeliveries(candidateTargetOffice, candidateTenant);
        var canWriteExistingSource = existing is null ||
            _officeScopeService.CanWriteOfficeForDeliveries(existing.SourceOfficeCode, existingTenant);
        var canWriteExistingTarget = existing is null ||
            _officeScopeService.CanWriteOfficeForDeliveries(existing.TargetOfficeCode, existingTenant);
        var canWriteSource = canWriteCandidateSource && canWriteExistingSource;
        var canWriteTarget = canWriteCandidateTarget && canWriteExistingTarget;

        if (!canWriteSource && !canWriteTarget)
        {
            reason = "Current account cannot modify the source or target office scope.";
            return false;
        }

        var existingStatus = existing is null
            ? string.Empty
            : InventoryTransferStatusNormalizer.Normalize(
                existing.TransferStatus,
                existing.ReceivedByUsername,
                existing.ReceivedAtUtc,
                existing.RejectedByUsername,
                existing.RejectedAtUtc);
        var candidateIsFinal = IsFinalInventoryTransferStatus(normalizedStatus);
        var existingIsFinal = IsFinalInventoryTransferStatus(existingStatus);

        if (dto.IsDeleted)
        {
            if (existingIsFinal)
            {
                if (canWriteSource && canWriteTarget)
                    return true;

                reason = !canWriteSource
                    ? $"Inventory transfer source office is outside the writable delivery scope: {candidateSourceOffice}."
                    : $"Inventory transfer target office is outside the writable delivery scope: {candidateTargetOffice}.";
                return false;
            }

            if (canWriteSource)
                return true;

            reason = $"Inventory transfer source office is outside the writable delivery scope: {candidateSourceOffice}.";
            return false;
        }

        if (existing is null)
        {
            if (!canWriteSource)
            {
                reason = $"Inventory transfer source office is outside the writable delivery scope: {candidateSourceOffice}.";
                return false;
            }

            if (candidateIsFinal && !canWriteTarget)
            {
                reason = $"Inventory transfer target office is outside the writable delivery scope: {candidateTargetOffice}.";
                return false;
            }

            return true;
        }

        if (existingIsFinal)
        {
            if (!candidateIsFinal)
            {
                reason = "Final inventory transfer status cannot be changed after receipt or rejection.";
                return false;
            }

            if (!IsFinalInventoryTransferSnapshotUnchanged(existing, dto, existingStatus, normalizedStatus))
            {
                reason = "Final inventory transfer status cannot be changed after receipt or rejection.";
                return false;
            }

            return true;
        }

        if (candidateIsFinal)
        {
            if (!canWriteTarget)
            {
                reason = $"Inventory transfer target office is outside the writable delivery scope: {candidateTargetOffice}.";
                return false;
            }

            if (!IsTargetOnlyInventoryTransferStatusMutation(existing, dto, normalizedStatus) ||
                !IsInitialInventoryTransferFinalStatusAuditValid(existing, dto, normalizedStatus))
            {
                reason = "Inventory transfer target-only status updates cannot change source-controlled, audit, evidence, opposite-status, or requested-line fields.";
                return false;
            }

            return true;
        }

        if (canWriteSource)
        {
            if (!IsSourceOnlyInventoryTransferPendingMutation(existing, dto))
            {
                reason = "Inventory transfer pending source updates cannot change immutable, audit, evidence, receipt, rejection, or status fields.";
                return false;
            }

            return true;
        }

        reason = $"Inventory transfer source office is outside the writable delivery scope: {candidateSourceOffice}.";
        return false;
    }

    private static bool InventoryTransferRouteMatchesPurgeRecord(
        InventoryTransferDto dto,
        RecycleBinPurgeRecord purgeRecord)
    {
        if (!OfficeCodeCatalog.TryNormalizeOfficeCode(
                dto.SourceOfficeCode,
                out var dtoSourceOffice) ||
            !OfficeCodeCatalog.TryNormalizeOfficeCode(
                dto.TargetOfficeCode,
                out var dtoTargetOffice) ||
            !TenantScopeCatalog.TryNormalizeTenantCode(
                dto.TenantCode,
                out var dtoTenant) ||
            !OfficeCodeCatalog.TryNormalizeOfficeCode(
                purgeRecord.SourceOfficeCode,
                out var purgeSourceOffice) ||
            !OfficeCodeCatalog.TryNormalizeOfficeCode(
                purgeRecord.TargetOfficeCode,
                out var purgeTargetOffice) ||
            !TenantScopeCatalog.TryNormalizeTenantCode(
                purgeRecord.TenantCode,
                out var purgeTenant) ||
            !OfficeCodeCatalog.TryNormalizeScope(
                purgeRecord.OfficeCode,
                out var purgeOfficeScope) ||
            !string.Equals(
                purgeOfficeScope,
                OfficeCodeCatalog.Shared,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(
                   dtoTenant,
                   purgeTenant,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   dtoSourceOffice,
                   purgeSourceOffice,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   dtoTargetOffice,
                   purgeTargetOffice,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFinalInventoryTransferStatus(string? status)
        => string.Equals(status, InventoryTransferStatusNormalizer.Received, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, InventoryTransferStatusNormalizer.Rejected, StringComparison.OrdinalIgnoreCase);

    private static bool IsTargetOnlyInventoryTransferStatusMutation(
        InventoryTransfer existing,
        InventoryTransferDto dto,
        string normalizedStatus)
    {
        if (!IsFinalInventoryTransferStatus(normalizedStatus) || dto.IsDeleted)
            return false;

        if ((dto.Lines ?? []).Any(line => line.IsDeleted))
            return false;

        if (!string.Equals(
                TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(dto.TenantCode, dto.SourceOfficeCode),
                TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(existing.TenantCode, existing.SourceOfficeCode),
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(
                OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(dto.SourceOfficeCode),
                OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(existing.SourceOfficeCode),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(dto.TargetOfficeCode),
                OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(existing.TargetOfficeCode),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                OfficeCodeCatalog.NormalizeWarehouseCodeLoose(dto.FromWarehouseCode),
                OfficeCodeCatalog.NormalizeWarehouseCodeLoose(existing.FromWarehouseCode),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                OfficeCodeCatalog.NormalizeWarehouseCodeLoose(dto.ToWarehouseCode),
                OfficeCodeCatalog.NormalizeWarehouseCodeLoose(existing.ToWarehouseCode),
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(NormalizeInventoryTransferGuardText(dto.TransferNumber), NormalizeInventoryTransferGuardText(existing.TransferNumber), StringComparison.Ordinal) ||
            dto.TransferDate != existing.TransferDate ||
            !string.Equals(NormalizeInventoryTransferGuardText(dto.Memo), NormalizeInventoryTransferGuardText(existing.Memo), StringComparison.Ordinal) ||
            !string.Equals(NormalizeInventoryTransferGuardText(dto.CreatedByUsername), NormalizeInventoryTransferGuardText(existing.CreatedByUsername), StringComparison.Ordinal) ||
            NormalizeInventoryTransferGuardUtc(dto.CreatedAtUtc) != NormalizeInventoryTransferGuardUtc(existing.CreatedAtUtc) ||
            !string.Equals(NormalizeInventoryTransferGuardText(dto.RequestedByUsername), NormalizeInventoryTransferGuardText(existing.RequestedByUsername), StringComparison.Ordinal) ||
            NormalizeInventoryTransferGuardUtc(dto.RequestedAtUtc) != NormalizeInventoryTransferGuardUtc(existing.RequestedAtUtc) ||
            !string.Equals(NormalizeInventoryTransferGuardText(dto.ReceiveEvidencePath), NormalizeInventoryTransferGuardText(existing.ReceiveEvidencePath), StringComparison.Ordinal))
            return false;

        var existingLines = existing.Lines
            .Where(line => !line.IsDeleted)
            .OrderBy(line => line.Id)
            .ToList();
        var incomingLines = (dto.Lines ?? [])
            .Where(line => !line.IsDeleted)
            .OrderBy(line => line.Id)
            .ToList();

        if (existingLines.Count != incomingLines.Count)
            return false;

        for (var i = 0; i < existingLines.Count; i++)
        {
            var existingLine = existingLines[i];
            var incomingLine = incomingLines[i];
            if (existingLine.Id != incomingLine.Id ||
                existingLine.TransferId != incomingLine.TransferId ||
                existingLine.ItemId != incomingLine.ItemId ||
                !string.Equals(NormalizeInventoryTransferGuardText(existingLine.ItemNameOriginal), NormalizeInventoryTransferGuardText(incomingLine.ItemNameOriginal), StringComparison.Ordinal) ||
                !string.Equals(NormalizeInventoryTransferGuardText(existingLine.SpecificationOriginal), NormalizeInventoryTransferGuardText(incomingLine.SpecificationOriginal), StringComparison.Ordinal) ||
                !string.Equals(UnitCatalogNormalizer.Normalize(existingLine.Unit), UnitCatalogNormalizer.Normalize(incomingLine.Unit), StringComparison.Ordinal) ||
                existingLine.Quantity != incomingLine.Quantity ||
                !string.Equals(NormalizeInventoryTransferGuardText(existingLine.Remark), NormalizeInventoryTransferGuardText(incomingLine.Remark), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsInitialInventoryTransferFinalStatusAuditValid(
        InventoryTransfer existing,
        InventoryTransferDto dto,
        string normalizedStatus)
    {
        var currentUsername = NormalizeInventoryTransferGuardText(_currentUserContext.Username);
        if (string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Received, StringComparison.OrdinalIgnoreCase))
        {
            var receivedAtUtc = NormalizeInventoryTransferGuardUtc(dto.ReceivedAtUtc);
            return receivedAtUtc.HasValue &&
                   string.Equals(NormalizeInventoryTransferGuardText(dto.ReceivedByUsername), currentUsername, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeInventoryTransferGuardText(dto.LastSavedByUsername), currentUsername, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeInventoryTransferGuardText(dto.LastStatusChangedByUsername), currentUsername, StringComparison.OrdinalIgnoreCase) &&
                   NormalizeInventoryTransferGuardUtc(dto.LastSavedAtUtc) == receivedAtUtc &&
                   NormalizeInventoryTransferGuardUtc(dto.LastStatusChangedAtUtc) == receivedAtUtc &&
                   string.Equals(NormalizeInventoryTransferGuardText(dto.RejectedByUsername), NormalizeInventoryTransferGuardText(existing.RejectedByUsername), StringComparison.Ordinal) &&
                   NormalizeInventoryTransferGuardUtc(dto.RejectedAtUtc) == NormalizeInventoryTransferGuardUtc(existing.RejectedAtUtc) &&
                   string.Equals(NormalizeInventoryTransferGuardText(dto.RejectReason), NormalizeInventoryTransferGuardText(existing.RejectReason), StringComparison.Ordinal);
        }

        if (string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            var rejectedAtUtc = NormalizeInventoryTransferGuardUtc(dto.RejectedAtUtc);
            return rejectedAtUtc.HasValue &&
                   string.Equals(NormalizeInventoryTransferGuardText(dto.RejectedByUsername), currentUsername, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeInventoryTransferGuardText(dto.LastSavedByUsername), currentUsername, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeInventoryTransferGuardText(dto.LastStatusChangedByUsername), currentUsername, StringComparison.OrdinalIgnoreCase) &&
                   NormalizeInventoryTransferGuardUtc(dto.LastSavedAtUtc) == rejectedAtUtc &&
                   NormalizeInventoryTransferGuardUtc(dto.LastStatusChangedAtUtc) == rejectedAtUtc &&
                   string.Equals(NormalizeInventoryTransferGuardText(dto.ReceivedByUsername), NormalizeInventoryTransferGuardText(existing.ReceivedByUsername), StringComparison.Ordinal) &&
                   NormalizeInventoryTransferGuardUtc(dto.ReceivedAtUtc) == NormalizeInventoryTransferGuardUtc(existing.ReceivedAtUtc) &&
                   string.Equals(NormalizeInventoryTransferGuardText(dto.ReceiveMemo), NormalizeInventoryTransferGuardText(existing.ReceiveMemo), StringComparison.Ordinal);
        }

        return false;
    }

    private bool IsSourceOnlyInventoryTransferPendingMutation(
        InventoryTransfer existing,
        InventoryTransferDto dto)
    {
        var currentUsername = NormalizeInventoryTransferGuardText(_currentUserContext.Username);
        var lastSavedAtUtc = NormalizeInventoryTransferGuardUtc(dto.LastSavedAtUtc);
        if (!string.Equals(NormalizeInventoryTransferGuardText(existing.TransferNumber), NormalizeInventoryTransferGuardText(dto.TransferNumber), StringComparison.Ordinal) ||
            NormalizeInventoryTransferGuardUtc(existing.CreatedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.CreatedAtUtc) ||
            !string.Equals(NormalizeInventoryTransferGuardText(existing.CreatedByUsername), NormalizeInventoryTransferGuardText(dto.CreatedByUsername), StringComparison.Ordinal) ||
            !string.Equals(NormalizeInventoryTransferGuardText(existing.RequestedByUsername), NormalizeInventoryTransferGuardText(dto.RequestedByUsername), StringComparison.Ordinal) ||
            NormalizeInventoryTransferGuardUtc(existing.RequestedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.RequestedAtUtc) ||
            !string.Equals(NormalizeInventoryTransferGuardText(existing.ReceivedByUsername), NormalizeInventoryTransferGuardText(dto.ReceivedByUsername), StringComparison.Ordinal) ||
            NormalizeInventoryTransferGuardUtc(existing.ReceivedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.ReceivedAtUtc) ||
            !string.Equals(NormalizeInventoryTransferGuardText(existing.ReceiveMemo), NormalizeInventoryTransferGuardText(dto.ReceiveMemo), StringComparison.Ordinal) ||
            !string.Equals(NormalizeInventoryTransferGuardText(existing.ReceiveEvidencePath), NormalizeInventoryTransferGuardText(dto.ReceiveEvidencePath), StringComparison.Ordinal) ||
            !string.Equals(NormalizeInventoryTransferGuardText(existing.RejectedByUsername), NormalizeInventoryTransferGuardText(dto.RejectedByUsername), StringComparison.Ordinal) ||
            NormalizeInventoryTransferGuardUtc(existing.RejectedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.RejectedAtUtc) ||
            !string.Equals(NormalizeInventoryTransferGuardText(existing.RejectReason), NormalizeInventoryTransferGuardText(dto.RejectReason), StringComparison.Ordinal) ||
            !string.Equals(NormalizeInventoryTransferGuardText(existing.LastStatusChangedByUsername), NormalizeInventoryTransferGuardText(dto.LastStatusChangedByUsername), StringComparison.Ordinal) ||
            NormalizeInventoryTransferGuardUtc(existing.LastStatusChangedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.LastStatusChangedAtUtc) ||
            !string.Equals(NormalizeInventoryTransferGuardText(dto.LastSavedByUsername), currentUsername, StringComparison.OrdinalIgnoreCase) ||
            !lastSavedAtUtc.HasValue ||
            lastSavedAtUtc != NormalizeInventoryTransferGuardUtc(dto.UpdatedAtUtc))
        {
            return false;
        }

        return true;
    }

    private static bool IsFinalInventoryTransferSnapshotUnchanged(
        InventoryTransfer existing,
        InventoryTransferDto dto,
        string existingStatus,
        string normalizedStatus)
    {
        if (!string.Equals(existingStatus, normalizedStatus, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsTargetOnlyInventoryTransferStatusMutation(existing, dto, normalizedStatus))
            return false;

        if (!string.Equals(NormalizeInventoryTransferGuardText(existing.LastSavedByUsername), NormalizeInventoryTransferGuardText(dto.LastSavedByUsername), StringComparison.Ordinal) ||
            NormalizeInventoryTransferGuardUtc(existing.LastSavedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.LastSavedAtUtc) ||
            !string.Equals(NormalizeInventoryTransferGuardText(existing.ReceiveEvidencePath), NormalizeInventoryTransferGuardText(dto.ReceiveEvidencePath), StringComparison.Ordinal) ||
            !string.Equals(NormalizeInventoryTransferGuardText(existing.LastStatusChangedByUsername), NormalizeInventoryTransferGuardText(dto.LastStatusChangedByUsername), StringComparison.Ordinal) ||
            NormalizeInventoryTransferGuardUtc(existing.LastStatusChangedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.LastStatusChangedAtUtc))
        {
            return false;
        }

        if (string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Received, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(NormalizeInventoryTransferGuardText(existing.ReceivedByUsername), NormalizeInventoryTransferGuardText(dto.ReceivedByUsername), StringComparison.Ordinal) ||
                NormalizeInventoryTransferGuardUtc(existing.ReceivedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.ReceivedAtUtc) ||
                !string.Equals(NormalizeInventoryTransferGuardText(existing.ReceiveMemo), NormalizeInventoryTransferGuardText(dto.ReceiveMemo), StringComparison.Ordinal) ||
                !string.Equals(NormalizeInventoryTransferGuardText(existing.RejectedByUsername), NormalizeInventoryTransferGuardText(dto.RejectedByUsername), StringComparison.Ordinal) ||
                NormalizeInventoryTransferGuardUtc(existing.RejectedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.RejectedAtUtc) ||
                !string.Equals(NormalizeInventoryTransferGuardText(existing.RejectReason), NormalizeInventoryTransferGuardText(dto.RejectReason), StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(NormalizeInventoryTransferGuardText(existing.RejectedByUsername), NormalizeInventoryTransferGuardText(dto.RejectedByUsername), StringComparison.Ordinal) ||
                NormalizeInventoryTransferGuardUtc(existing.RejectedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.RejectedAtUtc) ||
                !string.Equals(NormalizeInventoryTransferGuardText(existing.RejectReason), NormalizeInventoryTransferGuardText(dto.RejectReason), StringComparison.Ordinal) ||
                !string.Equals(NormalizeInventoryTransferGuardText(existing.ReceivedByUsername), NormalizeInventoryTransferGuardText(dto.ReceivedByUsername), StringComparison.Ordinal) ||
                NormalizeInventoryTransferGuardUtc(existing.ReceivedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.ReceivedAtUtc) ||
                !string.Equals(NormalizeInventoryTransferGuardText(existing.ReceiveMemo), NormalizeInventoryTransferGuardText(dto.ReceiveMemo), StringComparison.Ordinal))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        var existingLines = existing.Lines
            .Where(line => !line.IsDeleted)
            .OrderBy(line => line.Id)
            .ToList();
        var incomingLines = (dto.Lines ?? [])
            .Where(line => !line.IsDeleted)
            .OrderBy(line => line.Id)
            .ToList();
        if (existingLines.Count != incomingLines.Count)
            return false;

        for (var i = 0; i < existingLines.Count; i++)
        {
            var existingLine = existingLines[i];
            var incomingLine = incomingLines[i];
            if (existingLine.Id != incomingLine.Id ||
                (existingLine.ReceivedQuantity ?? existingLine.Quantity) !=
                (incomingLine.ReceivedQuantity ?? incomingLine.Quantity) ||
                (existingLine.QuantityDifference ?? 0m) !=
                (incomingLine.QuantityDifference ?? 0m) ||
                !string.Equals(NormalizeInventoryTransferGuardText(existingLine.ReceiptRemark), NormalizeInventoryTransferGuardText(incomingLine.ReceiptRemark), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeInventoryTransferGuardText(string? value)
        => (value ?? string.Empty).Trim();

    private static DateTime? NormalizeInventoryTransferGuardUtc(DateTime? value)
        => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;

    private async Task<int> UpsertInventoryTransfersAsync(
        IEnumerable<InventoryTransferDto> payload,
        SyncPushResult result,
        string deviceId,
        IReadOnlySet<string> itemWarehouseStockKeysHandledByClient,
        IReadOnlyDictionary<
            InvoiceStockSnapshotService.InvoiceStockKey,
            decimal> originalClientHandledStockQuantities,
        IReadOnlyDictionary<
            InvoiceStockSnapshotService.InvoiceStockKey,
            decimal> acceptedInvoiceStockDeltaDifferences,
        CancellationToken cancellationToken)
    {
        var acceptedCount = 0;
        var projectedClientHandledStockQuantities =
            originalClientHandledStockQuantities.ToDictionary(
                entry => entry.Key,
                entry => entry.Value);
        foreach (var (key, difference) in
                 acceptedInvoiceStockDeltaDifferences)
        {
            if (projectedClientHandledStockQuantities
                .TryGetValue(key, out var currentQuantity))
            {
                projectedClientHandledStockQuantities[key] =
                    currentQuantity + difference;
            }
        }
        var exactReplayEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();
        var acceptedEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();
        foreach (var dto in payload)
        {
            if (TryAcceptDuplicateMutation(
                    dto,
                    nameof(InventoryTransfer),
                    result,
                    exactReplayEntityIdsForHistoricalConflictResolution))
                continue;

            var entity = await _dbContext.InventoryTransfers.IgnoreQueryFilters()
                .Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);

            if (entity is null)
            {
                entity = new InventoryTransfer { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
                var dtoToCreate = BuildWhitelistedInventoryTransferCreate(
                    entity.Id,
                    dto,
                    _currentUserContext.Username,
                    DateTime.UtcNow);
                entity.Apply(dtoToCreate);
                ApplyInventoryTransferLines(entity, dtoToCreate.Lines ?? []);
                var currentStockDeltas = await _invoiceStockSnapshotService.BuildInventoryTransferStockDeltasAsync(entity, cancellationToken);
                var stockShortages = await _invoiceStockSnapshotService.FindStockShortagesAsync(
                    new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
                    currentStockDeltas,
                    projectedClientHandledStockQuantities,
                    cancellationToken);
                if (stockShortages.Count > 0)
                {
                    AddClientConflict(dto, nameof(InventoryTransfer), InvoiceStockSnapshotService.FormatStockShortageMessage(stockShortages), result);
                    continue;
                }

                await ApplyStockSnapshotDeltaAsync(
                    new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
                    currentStockDeltas,
                    itemWarehouseStockKeysHandledByClient,
                    cancellationToken);
                ApplyProjectedClientHandledStockDeltaDifferences(
                    projectedClientHandledStockQuantities,
                    new Dictionary<
                        InvoiceStockSnapshotService.InvoiceStockKey,
                        decimal>(),
                    currentStockDeltas,
                    itemWarehouseStockKeysHandledByClient);
                _dbContext.InventoryTransfers.Add(entity);
                RegisterProcessedMutation(dto, nameof(InventoryTransfer), deviceId);
                acceptedEntityIdsForHistoricalConflictResolution.Add(entity.Id);
                acceptedCount++;
                result.AcceptedCount++;
                continue;
            }

            var previousStockDeltas = await _invoiceStockSnapshotService.BuildInventoryTransferStockDeltasAsync(entity, cancellationToken);
            var canWriteExisting =
                _officeScopeService.CanWriteInventoryTransferRoute(
                    entity.SourceOfficeCode,
                    entity.TargetOfficeCode,
                    entity.TenantCode);
            if (!canWriteExisting)
            {
                AddClientConflict(dto, nameof(InventoryTransfer), "Current account cannot modify this office scope.", result);
                continue;
            }

            if (await TryAcceptAlreadyDeletedMutationAsync(entity, dto, nameof(InventoryTransfer), deviceId, result, cancellationToken))
                continue;

            if (entity.IsDeleted && !dto.IsDeleted)
            {
                await AddServerConflictAsync(
                    dto,
                    entity,
                    nameof(InventoryTransfer),
                    "Server inventory transfer is deleted. Restore it through the recycle bin.",
                    result,
                    cancellationToken);
                continue;
            }

            if (HasExpectedRevisionConflict(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, nameof(InventoryTransfer), BuildExpectedRevisionConflictReason(dto.ExpectedRevision, entity.Revision), result, cancellationToken);
                continue;
            }

            if (IsServerEntityNewer(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, nameof(InventoryTransfer), "Server version is newer.", result, cancellationToken);
                continue;
            }

            var existingStatus = InventoryTransferStatusNormalizer.Normalize(
                entity.TransferStatus,
                entity.ReceivedByUsername,
                entity.ReceivedAtUtc,
                entity.RejectedByUsername,
                entity.RejectedAtUtc);
            var candidateStatus = InventoryTransferStatusNormalizer.Normalize(
                dto.TransferStatus,
                dto.ReceivedByUsername,
                dto.ReceivedAtUtc,
                dto.RejectedByUsername,
                dto.RejectedAtUtc);
            if (IsFinalInventoryTransferStatus(existingStatus) &&
                !dto.IsDeleted &&
                string.Equals(existingStatus, candidateStatus, StringComparison.OrdinalIgnoreCase))
            {
                RegisterProcessedMutation(dto, nameof(InventoryTransfer), deviceId);
                acceptedEntityIdsForHistoricalConflictResolution.Add(entity.Id);
                acceptedCount++;
                result.AcceptedCount++;
                continue;
            }

            var isInitialFinalStatusTransition =
                !IsFinalInventoryTransferStatus(existingStatus) &&
                IsFinalInventoryTransferStatus(candidateStatus);
            var isPendingSourceEdit =
                !dto.IsDeleted &&
                !IsFinalInventoryTransferStatus(existingStatus) &&
                !IsFinalInventoryTransferStatus(candidateStatus);
            var dtoToApply = dto.IsDeleted
                ? BuildWhitelistedInventoryTransferTombstone(
                    entity,
                    dto,
                    _currentUserContext.Username,
                    DateTime.UtcNow)
                : isInitialFinalStatusTransition
                    ? BuildWhitelistedInventoryTransferFinalTransition(entity, dto, candidateStatus)
                    : isPendingSourceEdit
                        ? BuildWhitelistedInventoryTransferPendingSourceEdit(
                            entity,
                            dto,
                            _currentUserContext.Username)
                        : dto;
            var candidate = new InventoryTransfer { Id = entity.Id };
            candidate.Apply(dtoToApply);
            ApplyInventoryTransferLines(candidate, dtoToApply.Lines ?? []);
            var updatedStockDeltas = await _invoiceStockSnapshotService.BuildInventoryTransferStockDeltasAsync(candidate, cancellationToken);
            var updateStockShortages = await _invoiceStockSnapshotService.FindStockShortagesAsync(
                previousStockDeltas,
                updatedStockDeltas,
                projectedClientHandledStockQuantities,
                cancellationToken);
            if (updateStockShortages.Count > 0)
            {
                AddClientConflict(dto, nameof(InventoryTransfer), InvoiceStockSnapshotService.FormatStockShortageMessage(updateStockShortages), result);
                continue;
            }

            await ApplyStockSnapshotDeltaAsync(
                previousStockDeltas,
                updatedStockDeltas,
                itemWarehouseStockKeysHandledByClient,
                cancellationToken);
            ApplyProjectedClientHandledStockDeltaDifferences(
                projectedClientHandledStockQuantities,
                previousStockDeltas,
                updatedStockDeltas,
                itemWarehouseStockKeysHandledByClient);
            entity.Apply(dtoToApply);
            if (isInitialFinalStatusTransition)
            {
                ApplyWhitelistedInventoryTransferFinalLineChanges(
                    entity,
                    dtoToApply.Lines ?? [],
                    candidateStatus);
            }
            else
            {
                _dbContext.InventoryTransferLines.RemoveRange(entity.Lines);
                entity.Lines.Clear();
                ApplyInventoryTransferLines(entity, dtoToApply.Lines ?? []);
            }
            RegisterProcessedMutation(dto, nameof(InventoryTransfer), deviceId);
            acceptedEntityIdsForHistoricalConflictResolution.Add(entity.Id);
            acceptedCount++;
            result.AcceptedCount++;
        }

        await ResolveHistoricalConflictsAsync(
            nameof(InventoryTransfer),
            exactReplayEntityIdsForHistoricalConflictResolution,
            "이미 처리된 동일 mutation 이 확인되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);
        await ResolveHistoricalConflictsAsync(
            nameof(InventoryTransfer),
            acceptedEntityIdsForHistoricalConflictResolution,
            "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);

        return acceptedCount;
    }

    private static InventoryTransferDto BuildWhitelistedInventoryTransferCreate(
        Guid transferId,
        InventoryTransferDto incoming,
        string authenticatedUsername,
        DateTime serverUtc)
    {
        var normalizedServerUtc = DateTime.SpecifyKind(serverUtc, DateTimeKind.Utc);
        var normalizedStatus = InventoryTransferStatusNormalizer.Normalize(
            incoming.TransferStatus,
            incoming.ReceivedByUsername,
            incoming.ReceivedAtUtc,
            incoming.RejectedByUsername,
            incoming.RejectedAtUtc);
        var isReceived = string.Equals(
            normalizedStatus,
            InventoryTransferStatusNormalizer.Received,
            StringComparison.OrdinalIgnoreCase);
        var isRejected = string.Equals(
            normalizedStatus,
            InventoryTransferStatusNormalizer.Rejected,
            StringComparison.OrdinalIgnoreCase);
        return new InventoryTransferDto
        {
            Id = transferId,
            IsDeleted = incoming.IsDeleted,
            CreatedAtUtc = normalizedServerUtc,
            UpdatedAtUtc = normalizedServerUtc,
            ExpectedRevision = incoming.ExpectedRevision,
            MutationId = incoming.MutationId,
            MutationCreatedAtUtc = incoming.MutationCreatedAtUtc,
            TenantCode = incoming.TenantCode,
            SourceOfficeCode = incoming.SourceOfficeCode,
            TargetOfficeCode = incoming.TargetOfficeCode,
            TransferNumber = incoming.TransferNumber,
            TransferDate = incoming.TransferDate,
            FromWarehouseCode = incoming.FromWarehouseCode,
            ToWarehouseCode = incoming.ToWarehouseCode,
            Memo = incoming.Memo,
            CreatedByUsername = authenticatedUsername,
            LastSavedByUsername = authenticatedUsername,
            LastSavedAtUtc = normalizedServerUtc,
            TransferStatus = isReceived || isRejected
                ? normalizedStatus
                : InventoryTransferStatusNormalizer.Pending,
            RequestedByUsername = authenticatedUsername,
            RequestedAtUtc = normalizedServerUtc,
            ReceivedByUsername = isReceived ? authenticatedUsername : string.Empty,
            ReceivedAtUtc = isReceived ? normalizedServerUtc : null,
            ReceiveMemo = isReceived ? incoming.ReceiveMemo : string.Empty,
            ReceiveEvidencePath = string.Empty,
            RejectedByUsername = isRejected ? authenticatedUsername : string.Empty,
            RejectedAtUtc = isRejected ? normalizedServerUtc : null,
            RejectReason = isRejected ? incoming.RejectReason : string.Empty,
            LastStatusChangedByUsername = authenticatedUsername,
            LastStatusChangedAtUtc = normalizedServerUtc,
            Lines = (incoming.Lines ?? [])
                .Where(line => !line.IsDeleted)
                .Select(line => new InventoryTransferLineDto
                {
                    Id = line.Id,
                    TransferId = transferId,
                    ItemId = line.ItemId,
                    ItemNameOriginal = line.ItemNameOriginal,
                    SpecificationOriginal = line.SpecificationOriginal,
                    Unit = line.Unit,
                    Quantity = line.Quantity,
                    ReceivedQuantity = isReceived
                        ? line.ReceivedQuantity ?? line.Quantity
                        : line.Quantity,
                    QuantityDifference = isReceived
                        ? (line.ReceivedQuantity ?? line.Quantity) - line.Quantity
                        : 0m,
                    Remark = line.Remark,
                    ReceiptRemark = isReceived ? line.ReceiptRemark : string.Empty,
                    IsDeleted = false
                })
                .ToList()
        };
    }

    private static InventoryTransferDto BuildWhitelistedInventoryTransferTombstone(
        InventoryTransfer existing,
        InventoryTransferDto incoming,
        string authenticatedUsername,
        DateTime serverUtc)
    {
        var whitelisted = existing.ToDto();
        whitelisted.IsDeleted = true;
        whitelisted.UpdatedAtUtc = DateTime.SpecifyKind(serverUtc, DateTimeKind.Utc);
        whitelisted.ExpectedRevision = incoming.ExpectedRevision;
        whitelisted.MutationId = incoming.MutationId;
        whitelisted.MutationCreatedAtUtc = incoming.MutationCreatedAtUtc;
        whitelisted.LastSavedByUsername = authenticatedUsername;
        whitelisted.LastSavedAtUtc = whitelisted.UpdatedAtUtc;
        return whitelisted;
    }

    private static InventoryTransferDto BuildWhitelistedInventoryTransferFinalTransition(
        InventoryTransfer existing,
        InventoryTransferDto incoming,
        string normalizedStatus)
    {
        var whitelisted = existing.ToDto();
        whitelisted.TransferStatus = normalizedStatus;
        whitelisted.LastSavedByUsername = incoming.LastSavedByUsername;
        whitelisted.LastSavedAtUtc = incoming.LastSavedAtUtc;
        whitelisted.LastStatusChangedByUsername = incoming.LastStatusChangedByUsername;
        whitelisted.LastStatusChangedAtUtc = incoming.LastStatusChangedAtUtc;

        if (string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Received, StringComparison.OrdinalIgnoreCase))
        {
            whitelisted.ReceivedByUsername = incoming.ReceivedByUsername;
            whitelisted.ReceivedAtUtc = incoming.ReceivedAtUtc;
            whitelisted.ReceiveMemo = incoming.ReceiveMemo;
            var incomingLines = (incoming.Lines ?? [])
                .Where(line => !line.IsDeleted)
                .ToDictionary(line => line.Id);
            foreach (var line in whitelisted.Lines)
            {
                if (!incomingLines.TryGetValue(line.Id, out var incomingLine))
                    continue;

                line.ReceivedQuantity = incomingLine.ReceivedQuantity;
                line.QuantityDifference = incomingLine.QuantityDifference;
                line.ReceiptRemark = incomingLine.ReceiptRemark;
            }
        }
        else
        {
            whitelisted.RejectedByUsername = incoming.RejectedByUsername;
            whitelisted.RejectedAtUtc = incoming.RejectedAtUtc;
            whitelisted.RejectReason = incoming.RejectReason;
        }

        return whitelisted;
    }

    private static InventoryTransferDto BuildWhitelistedInventoryTransferPendingSourceEdit(
        InventoryTransfer existing,
        InventoryTransferDto incoming,
        string authenticatedUsername)
    {
        var whitelisted = existing.ToDto();
        whitelisted.TenantCode = incoming.TenantCode;
        whitelisted.SourceOfficeCode = incoming.SourceOfficeCode;
        whitelisted.TargetOfficeCode = incoming.TargetOfficeCode;
        whitelisted.TransferDate = incoming.TransferDate;
        whitelisted.FromWarehouseCode = incoming.FromWarehouseCode;
        whitelisted.ToWarehouseCode = incoming.ToWarehouseCode;
        whitelisted.Memo = incoming.Memo;
        whitelisted.LastSavedByUsername = authenticatedUsername;
        whitelisted.LastSavedAtUtc = incoming.LastSavedAtUtc;
        whitelisted.TransferStatus = InventoryTransferStatusNormalizer.Pending;
        whitelisted.IsDeleted = false;
        whitelisted.UpdatedAtUtc = incoming.UpdatedAtUtc;
        whitelisted.ExpectedRevision = incoming.ExpectedRevision;

        whitelisted.Lines = (incoming.Lines ?? [])
            .Where(line => !line.IsDeleted)
            .Select(line =>
            {
                return new InventoryTransferLineDto
                {
                    Id = line.Id,
                    TransferId = existing.Id,
                    ItemId = line.ItemId,
                    ItemNameOriginal = line.ItemNameOriginal,
                    SpecificationOriginal = line.SpecificationOriginal,
                    Unit = line.Unit,
                    Quantity = line.Quantity,
                    ReceivedQuantity = line.Quantity,
                    QuantityDifference = 0m,
                    Remark = line.Remark,
                    ReceiptRemark = string.Empty,
                    IsDeleted = false
                };
            })
            .ToList();

        return whitelisted;
    }

    private static void ApplyWhitelistedInventoryTransferFinalLineChanges(
        InventoryTransfer existing,
        IReadOnlyCollection<InventoryTransferLineDto> whitelistedLines,
        string normalizedStatus)
    {
        if (!string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Received, StringComparison.OrdinalIgnoreCase))
            return;

        var incomingLines = whitelistedLines.ToDictionary(line => line.Id);
        foreach (var existingLine in existing.Lines.Where(line => !line.IsDeleted))
        {
            if (!incomingLines.TryGetValue(existingLine.Id, out var incomingLine))
                continue;

            existingLine.ReceivedQuantity = incomingLine.ReceivedQuantity;
            existingLine.QuantityDifference = incomingLine.QuantityDifference;
            existingLine.ReceiptRemark = incomingLine.ReceiptRemark;
        }
    }

    private static void ApplyProjectedClientHandledStockDeltaDifferences(
        IDictionary<
            InvoiceStockSnapshotService.InvoiceStockKey,
            decimal> projectedQuantities,
        IReadOnlyDictionary<
            InvoiceStockSnapshotService.InvoiceStockKey,
            decimal> previous,
        IReadOnlyDictionary<
            InvoiceStockSnapshotService.InvoiceStockKey,
            decimal> current,
        IReadOnlySet<string> itemWarehouseStockKeysHandledByClient)
    {
        foreach (var key in previous.Keys
                     .Concat(current.Keys)
                     .Distinct())
        {
            if (!itemWarehouseStockKeysHandledByClient.Contains(
                    BuildItemWarehouseStockSnapshotKey(
                        key.ItemId,
                        key.WarehouseCode)) ||
                !projectedQuantities.TryGetValue(
                    key,
                    out var projectedQuantity))
            {
                continue;
            }

            previous.TryGetValue(
                key,
                out var previousQuantity);
            current.TryGetValue(
                key,
                out var currentQuantity);
            projectedQuantities[key] =
                projectedQuantity +
                currentQuantity -
                previousQuantity;
        }
    }

    private async Task ApplyStockSnapshotDeltaAsync(
        IReadOnlyDictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal> previousStockDeltas,
        IReadOnlyDictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal> currentStockDeltas,
        IReadOnlySet<string> itemWarehouseStockKeysHandledByClient,
        CancellationToken cancellationToken)
    {
        var previousToApply = ExcludeClientHandledStockDeltas(previousStockDeltas, itemWarehouseStockKeysHandledByClient);
        var currentToApply = ExcludeClientHandledStockDeltas(currentStockDeltas, itemWarehouseStockKeysHandledByClient);
        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            previousToApply,
            currentToApply,
            cancellationToken);
    }

    private static IReadOnlyDictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal> ExcludeClientHandledStockDeltas(
        IReadOnlyDictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal> stockDeltas,
        IReadOnlySet<string> itemWarehouseStockKeysHandledByClient)
    {
        if (stockDeltas.Count == 0 || itemWarehouseStockKeysHandledByClient.Count == 0)
            return stockDeltas;

        var filtered = new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>();
        foreach (var (key, quantity) in stockDeltas)
        {
            if (itemWarehouseStockKeysHandledByClient.Contains(BuildItemWarehouseStockSnapshotKey(key.ItemId, key.WarehouseCode)))
                continue;

            filtered[key] = quantity;
        }

        return filtered;
    }

    private async Task<List<RentalManagementCompanyDto>> PrepareScopedRentalManagementCompaniesAsync(
        IEnumerable<RentalManagementCompanyDto> payload,
        IDictionary<Guid, Guid> resolvedIncomingRentalManagementCompanyIds,
        ICollection<IncomingRentalManagementCompanyMutation> incomingMutations,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<RentalManagementCompanyDto>();
        var reservedCompanyIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var preparedPayload = new List<PreparedRentalManagementCompanyInput>();

        foreach (var dto in payload)
        {
            var originalCompanyId = dto.Id;
            var originalMutationId = NormalizeMutationId(dto.MutationId);
            var originalClientJson = JsonSerializer.Serialize(dto, ConflictJsonOptions);
            dto.Code = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.Code, dto.Code);
            dto.TenantCode = _officeScopeService.ResolveTenantForRentalCreate(dto.TenantCode, dto.Code);

            var naturalKey = $"{dto.TenantCode}|{dto.Code}";
            preparedPayload.Add(new PreparedRentalManagementCompanyInput(
                dto,
                originalCompanyId,
                originalMutationId,
                originalClientJson,
                naturalKey));
        }

        var existingById = new Dictionary<Guid, RentalManagementCompany>();
        var requestedIds = preparedPayload
            .Select(input => input.Dto.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        foreach (var requestedIdBatch in requestedIds.Chunk(500))
        {
            var batch = requestedIdBatch.ToArray();
            var existingRows = await _dbContext.RentalManagementCompanies
                .IgnoreQueryFilters()
                .Where(company => batch.Contains(company.Id))
                .ToListAsync(cancellationToken);
            foreach (var existingRow in existingRows)
                existingById.TryAdd(existingRow.Id, existingRow);
        }

        var existingByNaturalKey = new Dictionary<string, RentalManagementCompany>(StringComparer.Ordinal);
        var naturalKeyCandidates = preparedPayload
            .GroupBy(input => input.NaturalKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        foreach (var naturalKeyBatch in naturalKeyCandidates.Chunk(250))
        {
            var batch = naturalKeyBatch.ToArray();
            var tenantCodes = batch
                .Select(input => input.Dto.TenantCode)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var companyCodes = batch
                .Select(input => input.Dto.Code)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var batchNaturalKeys = batch
                .Select(input => input.NaturalKey)
                .ToHashSet(StringComparer.Ordinal);
            var existingRows = await _dbContext.RentalManagementCompanies
                .IgnoreQueryFilters()
                .Where(company =>
                    tenantCodes.Contains(company.TenantCode) &&
                    companyCodes.Contains(company.Code))
                .ToListAsync(cancellationToken);
            foreach (var existingRow in existingRows)
            {
                var existingNaturalKey = $"{existingRow.TenantCode}|{existingRow.Code}";
                if (batchNaturalKeys.Contains(existingNaturalKey))
                    existingByNaturalKey.TryAdd(existingNaturalKey, existingRow);
            }
        }

        foreach (var input in preparedPayload)
        {
            var dto = input.Dto;
            var originalCompanyId = input.OriginalId;
            var originalMutationId = input.MutationId;
            var originalClientJson = input.OriginalClientJson;
            var naturalKey = input.NaturalKey;
            if (reservedCompanyIds.TryGetValue(naturalKey, out var reservedId))
                dto.Id = reservedId;

            existingById.TryGetValue(dto.Id, out var existing);
            if (existing is null)
            {
                existingByNaturalKey.TryGetValue(naturalKey, out existing);
                if (existing is not null)
                    dto.Id = existing.Id;
            }

            if (existing is not null && !_officeScopeService.HasGlobalDataScope &&
                !_officeScopeService.CanWriteOfficeForRentals(existing.Code, existing.TenantCode))
            {
                AddClientConflict(dto, nameof(RentalManagementCompany), "Current account cannot modify this tenant scope.", result);
                continue;
            }

            dto.TenantCode = _officeScopeService.ResolveTenantForRentalCreate(dto.TenantCode, dto.Code, existing?.TenantCode, existing?.Code);
            dto.Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id;
            reservedCompanyIds[naturalKey] = existing?.Id ?? dto.Id;
            if (originalCompanyId != Guid.Empty)
                resolvedIncomingRentalManagementCompanyIds[originalCompanyId] = dto.Id;
            incomingMutations.Add(new IncomingRentalManagementCompanyMutation(
                originalCompanyId,
                dto.Id,
                originalMutationId,
                originalClientJson,
                dto));
            scoped.Add(dto);
        }

        return scoped;
    }

    private sealed record PreparedRentalManagementCompanyInput(
        RentalManagementCompanyDto Dto,
        Guid OriginalId,
        string MutationId,
        string OriginalClientJson,
        string NaturalKey);

    private readonly record struct RentalProfileTenantIdentity(
        Guid ProfileId,
        string TenantCode);

    private static bool TryCreateRentalProfileTenantIdentity(
        Guid? profileId,
        string? tenantCode,
        out RentalProfileTenantIdentity identity)
    {
        identity = default;
        if (!profileId.HasValue ||
            profileId.Value == Guid.Empty ||
            !TenantScopeCatalog.TryNormalizeTenantCode(
                tenantCode,
                out var normalizedTenantCode))
        {
            return false;
        }

        identity = new RentalProfileTenantIdentity(
            profileId.Value,
            normalizedTenantCode);
        return true;
    }

    private static void AddBlockedRentalProfileIdentity(
        ISet<RentalProfileTenantIdentity> blockedIdentities,
        Guid? profileId,
        string? tenantCode)
    {
        if (TryCreateRentalProfileTenantIdentity(
                profileId,
                tenantCode,
                out var identity))
        {
            blockedIdentities.Add(identity);
        }
    }

    private static bool IsBlockedRentalProfileIdentity(
        IReadOnlySet<RentalProfileTenantIdentity> blockedIdentities,
        Guid? profileId,
        string? tenantCode)
        => TryCreateRentalProfileTenantIdentity(
               profileId,
               tenantCode,
               out var identity) &&
           blockedIdentities.Contains(identity);

    private readonly record struct RentalProfileNaturalKey(string TenantCode, string ProfileKey);
    private readonly record struct RentalManagementCompanyNaturalKey(string TenantCode, string Code);

    private sealed class RentalBillingProfilePushSnapshot
    {
        private readonly Dictionary<Guid, RentalBillingProfile> _profilesById = [];
        private readonly Dictionary<RentalProfileNaturalKey, RentalBillingProfile> _profilesByNaturalKey = [];
        private readonly Dictionary<Guid, Customer> _customersById = [];
        private readonly Dictionary<Guid, RentalAsset> _assetsById = [];
        private readonly List<RentalAsset> _assets = [];
        private readonly Dictionary<Guid, Item> _itemsById = [];
        private readonly List<Item> _items = [];
        private readonly Dictionary<Guid, List<RentalAsset>> _activeLinkedAssetsByProfileId = [];
        private readonly HashSet<RentalManagementCompanyNaturalKey> _activeManagementCompanies = [];

        public IReadOnlyList<Customer> ActiveCustomers { get; private set; } = [];
        public IReadOnlyList<Item> Items => _items;
        public IReadOnlyList<RentalAsset> Assets => _assets;

        public IEnumerable<Guid> ProfileIds => _profilesById.Keys;

        public RentalBillingProfile? FindProfile(Guid profileId)
            => profileId != Guid.Empty && _profilesById.TryGetValue(profileId, out var profile)
                ? profile
                : null;

        public RentalBillingProfile? FindProfile(string tenantCode, string profileKey)
            => _profilesByNaturalKey.TryGetValue(
                new RentalProfileNaturalKey(tenantCode, profileKey),
                out var profile)
                    ? profile
                    : null;

        public Customer? FindCustomer(Guid? customerId)
            => customerId is Guid id && id != Guid.Empty && _customersById.TryGetValue(id, out var customer)
                ? customer
                : null;

        public RentalAsset? FindAsset(Guid assetId)
            => assetId != Guid.Empty && _assetsById.TryGetValue(assetId, out var asset)
                ? asset
                : null;

        public RentalAsset? FindAssetByNaturalKey(RentalAssetDto dto)
        {
            var managementNumber = dto.ManagementNumber?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(managementNumber))
            {
                var match = _assets.FirstOrDefault(asset =>
                    string.Equals(asset.ManagementNumber, managementNumber, StringComparison.Ordinal));
                if (match is not null)
                    return match;
            }

            var managementId = dto.ManagementId?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(managementId))
            {
                var match = _assets.FirstOrDefault(asset =>
                    string.Equals(asset.ManagementId, managementId, StringComparison.Ordinal));
                if (match is not null)
                    return match;
            }

            var assetKey = dto.AssetKey?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(assetKey)
                ? null
                : _assets.FirstOrDefault(asset =>
                    string.Equals(asset.AssetKey, assetKey, StringComparison.Ordinal));
        }

        public RentalAsset? FindActiveAssetRestoreConflict(RentalAsset target)
            => _assets.FirstOrDefault(candidate =>
                candidate.Id != target.Id &&
                !candidate.IsDeleted &&
                (RentalAssetRestoreKeysMatch(candidate.ManagementNumber, target.ManagementNumber) ||
                 RentalAssetRestoreKeysMatch(candidate.ManagementId, target.ManagementId) ||
                 RentalAssetRestoreKeysMatch(candidate.AssetKey, target.AssetKey)));

        public bool IsAssetIdentifierAvailable(string value, Guid currentId, Func<RentalAsset, string> selector)
            => !_assets.Any(asset =>
                asset.Id != currentId &&
                string.Equals(selector(asset), value, StringComparison.Ordinal));

        public bool ExistingAssetUsesCustomer(Guid assetId, Guid customerId)
            => FindAsset(assetId) is { IsDeleted: false } asset && asset.CustomerId == customerId;

        public Item? FindItem(Guid? itemId)
            => itemId is Guid id && id != Guid.Empty && _itemsById.TryGetValue(id, out var item)
                ? item
                : null;

        public IReadOnlyList<RentalAsset> GetActiveLinkedAssets(Guid profileId)
            => _activeLinkedAssetsByProfileId.TryGetValue(profileId, out var assets)
                ? assets
                : [];

        public bool HasActiveManagementCompany(string tenantCode, string code)
            => _activeManagementCompanies.Contains(
                new RentalManagementCompanyNaturalKey(tenantCode, code));

        public void AddProfile(RentalBillingProfile profile)
        {
            _profilesById[profile.Id] = profile;
            var tenantCode = profile.TenantCode ?? string.Empty;
            var profileKey = profile.ProfileKey ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(tenantCode) && !string.IsNullOrWhiteSpace(profileKey))
            {
                _profilesByNaturalKey.TryAdd(
                    new RentalProfileNaturalKey(tenantCode, profileKey),
                    profile);
            }
        }

        public void AddCustomer(Customer customer)
            => _customersById[customer.Id] = customer;

        public void AddAsset(RentalAsset asset)
        {
            _assetsById[asset.Id] = asset;
            if (_assets.All(existing => existing.Id != asset.Id))
                _assets.Add(asset);
        }

        public void AddItem(Item item)
        {
            _itemsById[item.Id] = item;
            if (_items.All(existing => existing.Id != item.Id))
                _items.Add(item);
        }

        public void SetActiveCustomers(IEnumerable<Customer> customers)
            => ActiveCustomers = customers
                .Where(customer => !customer.IsDeleted)
                .DistinctBy(customer => customer.Id)
                .OrderByDescending(customer => customer.UpdatedAtUtc)
                .ToList();

        public void AddActiveLinkedAsset(RentalAsset asset)
        {
            if (asset.BillingProfileId is not Guid profileId || profileId == Guid.Empty)
                return;
            if (!_activeLinkedAssetsByProfileId.TryGetValue(profileId, out var assets))
            {
                assets = [];
                _activeLinkedAssetsByProfileId[profileId] = assets;
            }
            assets.Add(asset);
        }

        public void AddActiveManagementCompany(RentalManagementCompany company)
            => _activeManagementCompanies.Add(
                new RentalManagementCompanyNaturalKey(company.TenantCode, company.Code));
    }

    private async Task<RentalBillingProfilePushSnapshot> BuildRentalBillingProfilePushSnapshotAsync(
        IReadOnlyCollection<RentalBillingProfileDto> payload,
        IReadOnlyDictionary<RentalBillingProfileDto, Guid> originalProfileIds,
        IReadOnlyCollection<RentalAssetDto> projectedAssets,
        CancellationToken cancellationToken)
    {
        var snapshot = new RentalBillingProfilePushSnapshot();
        var requestedProfileIds = payload
            .SelectMany(dto => GetRentalBillingProfilePreflightIds(dto, originalProfileIds))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var tenantCodes = new HashSet<string>(StringComparer.Ordinal);
        var profileKeys = new HashSet<string>(StringComparer.Ordinal);
        var requestedCustomerIds = new HashSet<Guid>();
        var requestedCustomerNames = new HashSet<string>(StringComparer.Ordinal);
        var requestedCustomerNameKeys = new HashSet<string>(StringComparer.Ordinal);
        var requiresBusinessNumberLookup = false;
        var requestedItemIds = new HashSet<Guid>();

        foreach (var dto in payload)
        {
            if (TryResolveRequestedRentalBillingProfileTenant(dto, out var tenantCode))
                tenantCodes.Add(tenantCode);

            var profileKey = (dto.ProfileKey ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(profileKey))
                profileKeys.Add(profileKey);
            var legacyProfileKey = RentalDuplicateNormalizer.BuildLegacyProfileKey(
                dto.ManagementCompanyCode,
                dto.CustomerId,
                dto.BusinessNumber,
                dto.CustomerName,
                dto.BillingType,
                dto.BillingAdvanceMode,
                dto.BillingDay,
                dto.BillingCycleMonths,
                dto.BillingMethod);
            if (!string.IsNullOrWhiteSpace(legacyProfileKey))
                profileKeys.Add(legacyProfileKey);

            if (dto.CustomerId is Guid customerId && customerId != Guid.Empty)
                requestedCustomerIds.Add(customerId);
            var customerName = (dto.CustomerName ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(customerName))
            {
                requestedCustomerNames.Add(customerName);
                var nameKey = MatchKeyNormalizer.Normalize(customerName);
                if (!string.IsNullOrWhiteSpace(nameKey))
                    requestedCustomerNameKeys.Add(nameKey);
            }
            requiresBusinessNumberLookup |= !string.IsNullOrWhiteSpace(
                NormalizeBusinessNumber(dto.BusinessNumber));
        }

        foreach (var asset in projectedAssets)
        {
            if (asset.CustomerId is Guid customerId && customerId != Guid.Empty)
                requestedCustomerIds.Add(customerId);
            if (asset.ItemId is Guid itemId && itemId != Guid.Empty)
                requestedItemIds.Add(itemId);
            var customerName = (asset.CustomerName ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(customerName))
            {
                requestedCustomerNames.Add(customerName);
                var nameKey = MatchKeyNormalizer.Normalize(customerName);
                if (!string.IsNullOrWhiteSpace(nameKey))
                    requestedCustomerNameKeys.Add(nameKey);
            }
        }

        foreach (var assetIdBatch in projectedAssets
                     .Select(asset => asset.Id)
                     .Where(assetId => assetId != Guid.Empty)
                     .Distinct()
                     .Chunk(500))
        {
            var batch = assetIdBatch.ToArray();
            var assets = await _dbContext.RentalAssets
                .IgnoreQueryFilters()
                .Where(asset => batch.Contains(asset.Id))
                .ToListAsync(cancellationToken);
            foreach (var asset in assets)
                snapshot.AddAsset(asset);
        }
        var idLoadedAssets = snapshot.Assets.ToList();
        foreach (var values in new[]
                 {
                     projectedAssets.Select(asset => asset.ManagementNumber?.Trim() ?? string.Empty)
                         .Concat(idLoadedAssets.Select(asset => asset.ManagementNumber?.Trim() ?? string.Empty)).ToList(),
                     projectedAssets.Select(asset => asset.ManagementId?.Trim() ?? string.Empty)
                         .Concat(idLoadedAssets.Select(asset => asset.ManagementId?.Trim() ?? string.Empty)).ToList(),
                     projectedAssets.Select(asset => asset.AssetKey?.Trim() ?? string.Empty)
                         .Concat(idLoadedAssets.Select(asset => asset.AssetKey?.Trim() ?? string.Empty)).ToList()
                 })
        {
            foreach (var valueBatch in values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Chunk(400))
            {
                var batch = valueBatch.ToArray();
                var assets = await _dbContext.RentalAssets
                    .IgnoreQueryFilters()
                    .Where(asset =>
                        batch.Contains(asset.ManagementNumber) ||
                        batch.Contains(asset.ManagementId) ||
                        batch.Contains(asset.AssetKey))
                    .ToListAsync(cancellationToken);
                foreach (var asset in assets)
                    snapshot.AddAsset(asset);
            }
        }
        var relevantExistingAssets = projectedAssets
            .Select(asset => snapshot.FindAsset(asset.Id) ?? snapshot.FindAssetByNaturalKey(asset))
            .OfType<RentalAsset>()
            .DistinctBy(asset => asset.Id)
            .ToList();
        var idLoadedAssetIds = idLoadedAssets
            .Select(asset => asset.Id)
            .ToHashSet();
        var naturalKeyDiscoveredRelevantAssets = relevantExistingAssets
            .Where(asset => !idLoadedAssetIds.Contains(asset.Id))
            .ToList();
        foreach (var values in new[]
                 {
                     naturalKeyDiscoveredRelevantAssets.Select(asset => asset.ManagementNumber?.Trim() ?? string.Empty).ToList(),
                     naturalKeyDiscoveredRelevantAssets.Select(asset => asset.ManagementId?.Trim() ?? string.Empty).ToList(),
                     naturalKeyDiscoveredRelevantAssets.Select(asset => asset.AssetKey?.Trim() ?? string.Empty).ToList()
                 })
        {
            foreach (var valueBatch in values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Chunk(400))
            {
                var batch = valueBatch.ToArray();
                var assets = await _dbContext.RentalAssets
                    .IgnoreQueryFilters()
                    .Where(asset =>
                        batch.Contains(asset.ManagementNumber) ||
                        batch.Contains(asset.ManagementId) ||
                        batch.Contains(asset.AssetKey))
                    .ToListAsync(cancellationToken);
                foreach (var asset in assets)
                    snapshot.AddAsset(asset);
            }
        }
        var persistedRestoreAssets = projectedAssets
            .Where(asset => !asset.IsDeleted)
            .Select(asset => relevantExistingAssets.FirstOrDefault(existing => existing.Id == asset.Id) ??
                             snapshot.FindAssetByNaturalKey(asset))
            .OfType<RentalAsset>()
            .Where(asset => asset.IsDeleted)
            .DistinctBy(asset => asset.Id)
            .ToList();
        if (persistedRestoreAssets.Count > 0)
        {
            var activeAssets = await _dbContext.RentalAssets
                .IgnoreQueryFilters()
                .Where(asset => !asset.IsDeleted)
                .ToListAsync(cancellationToken);
            foreach (var asset in activeAssets)
                snapshot.AddAsset(asset);
        }

        requestedProfileIds.AddRange(projectedAssets
            .Select(asset => asset.BillingProfileId.GetValueOrDefault())
            .Concat(relevantExistingAssets.Select(asset => asset.BillingProfileId.GetValueOrDefault()))
            .Where(profileId => profileId != Guid.Empty));

        foreach (var itemIdBatch in requestedItemIds.Chunk(500))
        {
            var batch = itemIdBatch.ToArray();
            var items = await _dbContext.Items
                .IgnoreQueryFilters()
                .Where(item => batch.Contains(item.Id))
                .ToListAsync(cancellationToken);
            foreach (var item in items)
                snapshot.AddItem(item);
        }

        foreach (var profileIdBatch in requestedProfileIds.Distinct().Chunk(500))
        {
            var batch = profileIdBatch.ToArray();
            var profiles = await _dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .Where(profile => batch.Contains(profile.Id))
                .ToListAsync(cancellationToken);
            foreach (var profile in profiles)
                snapshot.AddProfile(profile);
        }

        foreach (var profileKeyBatch in profileKeys.Chunk(400))
        {
            var batch = profileKeyBatch.ToArray();
            var tenants = tenantCodes.ToArray();
            var profiles = await _dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .Where(profile =>
                    tenants.Contains(profile.TenantCode) &&
                    batch.Contains(profile.ProfileKey))
                .ToListAsync(cancellationToken);
            foreach (var profile in profiles)
                snapshot.AddProfile(profile);
        }

        var loadedProfileIds = snapshot.ProfileIds.ToList();
        foreach (var profileIdBatch in loadedProfileIds.Chunk(500))
        {
            var batch = profileIdBatch.ToArray();
            var linkedAssets = await _dbContext.RentalAssets
                .IgnoreQueryFilters()
                .Where(asset =>
                    !asset.IsDeleted &&
                    asset.BillingProfileId.HasValue &&
                    batch.Contains(asset.BillingProfileId.Value))
                .ToListAsync(cancellationToken);
            foreach (var asset in linkedAssets)
            {
                snapshot.AddActiveLinkedAsset(asset);
                if (asset.CustomerId is Guid customerId && customerId != Guid.Empty)
                    requestedCustomerIds.Add(customerId);
            }
        }

        foreach (var profileId in loadedProfileIds)
        {
            if (snapshot.FindProfile(profileId)?.CustomerId is Guid customerId && customerId != Guid.Empty)
                requestedCustomerIds.Add(customerId);
        }

        foreach (var customerIdBatch in requestedCustomerIds.Chunk(500))
        {
            var batch = customerIdBatch.ToArray();
            var customers = await _dbContext.Customers
                .IgnoreQueryFilters()
                .Where(customer => batch.Contains(customer.Id))
                .ToListAsync(cancellationToken);
            foreach (var customer in customers)
                snapshot.AddCustomer(customer);
        }

        var activeCustomerCandidates = new List<Customer>();
        if (requiresBusinessNumberLookup)
        {
            activeCustomerCandidates.AddRange(await _dbContext.Customers
                .IgnoreQueryFilters()
                .Where(customer => !customer.IsDeleted)
                .OrderByDescending(customer => customer.UpdatedAtUtc)
                .ToListAsync(cancellationToken));
        }
        else
        {
            var names = requestedCustomerNames.ToArray();
            var nameKeys = requestedCustomerNameKeys.ToArray();
            foreach (var nameBatch in names.Chunk(400))
            {
                var batch = nameBatch.ToArray();
                activeCustomerCandidates.AddRange(await _dbContext.Customers
                    .IgnoreQueryFilters()
                    .Where(customer => !customer.IsDeleted && batch.Contains(customer.NameOriginal))
                    .ToListAsync(cancellationToken));
            }
            foreach (var nameKeyBatch in nameKeys.Chunk(400))
            {
                var batch = nameKeyBatch.ToArray();
                activeCustomerCandidates.AddRange(await _dbContext.Customers
                    .IgnoreQueryFilters()
                    .Where(customer => !customer.IsDeleted && batch.Contains(customer.NameMatchKey))
                    .ToListAsync(cancellationToken));
            }
        }
        foreach (var customer in activeCustomerCandidates)
            snapshot.AddCustomer(customer);
        snapshot.SetActiveCustomers(activeCustomerCandidates.Concat(
            requestedCustomerIds
                .Select(customerId => snapshot.FindCustomer(customerId))
                .OfType<Customer>()));

        var canonicalNaturalKeys = new HashSet<RentalProfileNaturalKey>();
        foreach (var dto in payload)
        {
            if (!TryResolveRequestedRentalBillingProfileTenant(dto, out var tenantCode))
                continue;
            var canonicalLegacyKey = await BuildCanonicalRentalBillingProfileLegacyKeyAsync(
                dto,
                snapshot,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(canonicalLegacyKey) ||
                snapshot.FindProfile(tenantCode, canonicalLegacyKey) is not null)
            {
                continue;
            }

            canonicalNaturalKeys.Add(new RentalProfileNaturalKey(tenantCode, canonicalLegacyKey));
        }

        var profileIdsBeforeCanonicalLookup = snapshot.ProfileIds.ToHashSet();
        foreach (var canonicalKeyBatch in canonicalNaturalKeys
                     .Select(key => key.ProfileKey)
                     .Distinct(StringComparer.Ordinal)
                     .Chunk(400))
        {
            var batch = canonicalKeyBatch.ToArray();
            var tenants = canonicalNaturalKeys
                .Select(key => key.TenantCode)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var profiles = await _dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .Where(profile =>
                    tenants.Contains(profile.TenantCode) &&
                    batch.Contains(profile.ProfileKey))
                .ToListAsync(cancellationToken);
            foreach (var profile in profiles)
                snapshot.AddProfile(profile);
        }

        var canonicalProfileIds = snapshot.ProfileIds
            .Where(profileId => !profileIdsBeforeCanonicalLookup.Contains(profileId))
            .ToList();
        var canonicalCustomerIds = new HashSet<Guid>();
        foreach (var profileIdBatch in canonicalProfileIds.Chunk(500))
        {
            var batch = profileIdBatch.ToArray();
            var linkedAssets = await _dbContext.RentalAssets
                .IgnoreQueryFilters()
                .Where(asset =>
                    !asset.IsDeleted &&
                    asset.BillingProfileId.HasValue &&
                    batch.Contains(asset.BillingProfileId.Value))
                .ToListAsync(cancellationToken);
            foreach (var asset in linkedAssets)
            {
                snapshot.AddActiveLinkedAsset(asset);
                if (asset.CustomerId is Guid customerId && customerId != Guid.Empty)
                    canonicalCustomerIds.Add(customerId);
            }
        }
        foreach (var profileId in canonicalProfileIds)
        {
            if (snapshot.FindProfile(profileId)?.CustomerId is Guid customerId && customerId != Guid.Empty)
                canonicalCustomerIds.Add(customerId);
        }
        var canonicalCustomers = new List<Customer>();
        foreach (var customerIdBatch in canonicalCustomerIds.Chunk(500))
        {
            var batch = customerIdBatch.ToArray();
            canonicalCustomers.AddRange(await _dbContext.Customers
                .IgnoreQueryFilters()
                .Where(customer => batch.Contains(customer.Id))
                .ToListAsync(cancellationToken));
        }
        foreach (var customer in canonicalCustomers)
            snapshot.AddCustomer(customer);
        if (canonicalCustomers.Count > 0)
            snapshot.SetActiveCustomers(snapshot.ActiveCustomers.Concat(canonicalCustomers));

        if (tenantCodes.Count > 0)
        {
            var tenants = tenantCodes.ToArray();
            var companies = await _dbContext.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(company => tenants.Contains(company.TenantCode) && !company.IsDeleted)
                .ToListAsync(cancellationToken);
            foreach (var company in companies)
                snapshot.AddActiveManagementCompany(company);
        }

        return snapshot;
    }

    private async Task<List<RentalBillingProfileDto>> PrepareScopedRentalBillingProfilesAsync(
        IEnumerable<RentalBillingProfileDto> payload,
        IReadOnlyDictionary<RentalBillingProfileDto, Guid> originalProfileIds,
        ISet<RentalProfileTenantIdentity> blockedPriorGenerationProfileIdentitiesForReferences,
        SyncPushResult result,
        string deviceId,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var scoped = new List<RentalBillingProfileDto>();
        var payloadRows = payload as IReadOnlyCollection<RentalBillingProfileDto> ?? payload.ToList();
        var requestedProfileIds = payloadRows
            .SelectMany(dto => GetRentalBillingProfilePreflightIds(dto, originalProfileIds))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var purgeRecordsByEntityId = new Dictionary<Guid, List<RecycleBinPurgeRecord>>();
        foreach (var profileIdBatch in requestedProfileIds.Chunk(500))
        {
            var batch = profileIdBatch.ToArray();
            var purgeRecords = await _dbContext.RecycleBinPurgeRecords
                .AsNoTracking()
                .Where(record =>
                    batch.Contains(record.EntityId) &&
                    (record.Kind == "rental-billing-profile" ||
                     record.Kind == "rentalbillingprofile"))
                .ToListAsync(cancellationToken);
            foreach (var purgeRecord in purgeRecords)
            {
                if (!purgeRecordsByEntityId.TryGetValue(purgeRecord.EntityId, out var entityRecords))
                {
                    entityRecords = [];
                    purgeRecordsByEntityId[purgeRecord.EntityId] = entityRecords;
                }

                entityRecords.Add(purgeRecord);
            }
        }

        var purgeAcceptedEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();

        foreach (var dto in payloadRows)
        {
            var originalProfileId = GetOriginalRentalBillingProfileId(dto, originalProfileIds);
            var deterministicProfileId = SyncIdentityGenerator.CreateRentalBillingProfileId(dto.ProfileKey);
            var responseProfileId = originalProfileId == Guid.Empty
                ? deterministicProfileId
                : originalProfileId;
            if (originalProfileId == Guid.Empty && deterministicProfileId != Guid.Empty)
                dto.Id = deterministicProfileId;

            if (!TryResolveRequestedRentalBillingProfileTenant(dto, out var requestedTenantCode))
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    "Rental billing profile tenant scope is invalid or ambiguous.",
                    result);
                continue;
            }

            dto.TenantCode = requestedTenantCode;

            var existing = pushSnapshot is null
                ? await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == responseProfileId, cancellationToken)
                : pushSnapshot.FindProfile(responseProfileId);
            if (existing is not null &&
                !RentalBillingProfileTenantMatches(existing.TenantCode, requestedTenantCode))
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    "Rental billing profile identity belongs to another tenant.",
                    result);
                continue;
            }

            var applicablePurgeRecords = GetApplicableRentalBillingProfilePurgeRecords(
                originalProfileId,
                deterministicProfileId,
                requestedTenantCode,
                purgeRecordsByEntityId);
            if (existing is null && applicablePurgeRecords.Count > 0)
            {
                if (applicablePurgeRecords.Any(record =>
                        !_officeScopeService.CanWriteOfficeForRentals(
                            record.OfficeCode,
                            record.TenantCode)))
                {
                    AddClientConflict(
                        dto,
                        nameof(RentalBillingProfile),
                        "Current account cannot modify this office scope.",
                        result);
                    continue;
                }

                if (!TrySelectRentalBillingProfilePurgeRecord(
                        applicablePurgeRecords,
                        requestedTenantCode,
                        out var purgeRecord))
                {
                    AddClientConflict(
                        dto,
                        nameof(RentalBillingProfile),
                        "Durable rental billing profile purge receipts have an incompatible tenant or office scope.",
                        result);
                    continue;
                }

                var knownRevision = Math.Max(dto.ExpectedRevision, dto.Revision);
                if (dto.IsDeleted && knownRevision > purgeRecord.Revision)
                {
                    AddClientConflict(
                        dto,
                        nameof(RentalBillingProfile),
                        "Delete revision is newer than the durable purge record. Pull before retrying the delete.",
                        result);
                    continue;
                }

                if (IsPriorRentalBillingProfileIncarnation(dto, purgeRecord))
                {
                    void BlockPriorGenerationReferences()
                    {
                        AddBlockedRentalProfileIdentity(
                            blockedPriorGenerationProfileIdentitiesForReferences,
                            originalProfileId,
                            requestedTenantCode);
                        AddBlockedRentalProfileIdentity(
                            blockedPriorGenerationProfileIdentitiesForReferences,
                            responseProfileId,
                            requestedTenantCode);
                        AddBlockedRentalProfileIdentity(
                            blockedPriorGenerationProfileIdentitiesForReferences,
                            deterministicProfileId,
                            requestedTenantCode);
                        foreach (var applicablePurgeEntityId in applicablePurgeRecords.Select(record => record.EntityId))
                        {
                            AddBlockedRentalProfileIdentity(
                                blockedPriorGenerationProfileIdentitiesForReferences,
                                applicablePurgeEntityId,
                                requestedTenantCode);
                        }
                    }

                    var exactReplay = HasExactProcessedMutationReplay(
                        dto,
                        nameof(RentalBillingProfile));
                    if (TryAcceptDuplicateMutation(
                            dto,
                            nameof(RentalBillingProfile),
                            result,
                            purgeAcceptedEntityIdsForHistoricalConflictResolution))
                    {
                        if (exactReplay)
                        {
                            BlockPriorGenerationReferences();
                            purgeAcceptedEntityIdsForHistoricalConflictResolution.Add(responseProfileId);
                            purgeAcceptedEntityIdsForHistoricalConflictResolution.Add(deterministicProfileId);
                            foreach (var applicablePurgeEntityId in applicablePurgeRecords.Select(record => record.EntityId))
                                purgeAcceptedEntityIdsForHistoricalConflictResolution.Add(applicablePurgeEntityId);
                            AddRentalBillingProfilePurgeAcknowledgement(
                                result,
                                responseProfileId,
                                purgeRecord);
                        }

                        continue;
                    }

                    RegisterProcessedMutation(
                        dto,
                        nameof(RentalBillingProfile),
                        deviceId);
                    result.AcceptedCount++;
                    BlockPriorGenerationReferences();
                    purgeAcceptedEntityIdsForHistoricalConflictResolution.Add(responseProfileId);
                    purgeAcceptedEntityIdsForHistoricalConflictResolution.Add(deterministicProfileId);
                    foreach (var applicablePurgeEntityId in applicablePurgeRecords.Select(record => record.EntityId))
                        purgeAcceptedEntityIdsForHistoricalConflictResolution.Add(applicablePurgeEntityId);
                    AddRentalBillingProfilePurgeAcknowledgement(
                        result,
                        responseProfileId,
                        purgeRecord);
                    continue;
                }

                if (deterministicProfileId != Guid.Empty &&
                    applicablePurgeRecords.Any(record => record.EntityId == deterministicProfileId))
                {
                    dto.Id = deterministicProfileId;
                    existing = pushSnapshot is null
                        ? await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                            .FirstOrDefaultAsync(x => x.Id == deterministicProfileId, cancellationToken)
                        : pushSnapshot.FindProfile(deterministicProfileId);
                    if (existing is not null &&
                        !RentalBillingProfileTenantMatches(existing.TenantCode, requestedTenantCode))
                    {
                        AddClientConflict(
                            dto,
                            nameof(RentalBillingProfile),
                            "Rental billing profile identity belongs to another tenant.",
                            result);
                        continue;
                    }
                }
            }

            if (existing is null)
            {
                existing = await FindExistingRentalBillingProfileByNaturalKeyAsync(
                    dto,
                    requestedTenantCode,
                    cancellationToken,
                    pushSnapshot);
                if (existing is not null)
                    dto.Id = existing.Id;
                else if (dto.Id == Guid.Empty)
                {
                    if (deterministicProfileId != Guid.Empty)
                        dto.Id = deterministicProfileId;
                }
            }

            if (existing is not null && !_officeScopeService.CanWriteOfficeForRentals(existing.ResponsibleOfficeCode, existing.TenantCode, existing.OfficeCode))
            {
                AddClientConflict(dto, nameof(RentalBillingProfile), "Current account cannot modify this office scope.", result);
                continue;
            }

            var requestedResponsibleOfficeCode = TenantScopeCatalog.TryNormalizeTenantCode(dto.TenantCode, out var requestedTenantCodeForResponsible) &&
                                                 string.Equals(requestedTenantCodeForResponsible, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
                ? OfficeCodeCatalog.Itworld
                : dto.ResponsibleOfficeCode;
            dto.ResponsibleOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(
                requestedResponsibleOfficeCode,
                existing?.ResponsibleOfficeCode ?? dto.OfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                existing?.OfficeCode);
            var resolvedTenantCode = _officeScopeService.ResolveTenantForRentalCreate(
                dto.TenantCode,
                dto.OfficeCode,
                existing?.TenantCode,
                existing?.OfficeCode);
            dto.TenantCode = TenantScopeCatalog.TryNormalizeTenantCode(dto.TenantCode, out var normalizedRequestedTenantCode) &&
                             TenantScopeCatalog.TenantContainsOffice(normalizedRequestedTenantCode, dto.OfficeCode)
                ? normalizedRequestedTenantCode
                : resolvedTenantCode;
            scoped.Add(dto);
        }

        await ResolveHistoricalConflictsAsync(
            nameof(RentalBillingProfile),
            purgeAcceptedEntityIdsForHistoricalConflictResolution,
            "The rental billing profile was permanently purged, so the prior-incarnation mutation was acknowledged without recreating data.",
            cancellationToken);

        return scoped;
    }

    private static bool IsPriorRentalBillingProfileIncarnation(
        RentalBillingProfileDto dto,
        RecycleBinPurgeRecord purgeRecord)
    {
        var knownRevision = Math.Max(dto.ExpectedRevision, dto.Revision);
        if (knownRevision > 0)
            return knownRevision <= purgeRecord.Revision;

        if (dto.IsDeleted)
            return true;

        var legacyTimestamps = new[]
            {
                dto.MutationCreatedAtUtc.GetValueOrDefault(),
                dto.UpdatedAtUtc
            }
            .Where(timestamp => timestamp != default)
            .Select(NormalizeConflictUtc)
            .ToList();
        return legacyTimestamps.Count > 0 &&
               legacyTimestamps.Max() <= NormalizeConflictUtc(purgeRecord.PurgedAtUtc);
    }

    private static void AddRentalBillingProfilePurgeAcknowledgement(
        SyncPushResult result,
        Guid profileId,
        RecycleBinPurgeRecord purgeRecord)
    {
        if (!result.AcceptedRevisions.Any(revision =>
                string.Equals(
                    revision.EntityName,
                    nameof(RentalBillingProfile),
                    StringComparison.OrdinalIgnoreCase) &&
                revision.EntityId == profileId))
        {
            result.AcceptedRevisions.Add(new SyncAcceptedRevisionDto
            {
                EntityName = nameof(RentalBillingProfile),
                EntityId = profileId,
                Revision = purgeRecord.Revision,
                UpdatedAtUtc = purgeRecord.UpdatedAtUtc,
                IsDeleted = true
            });
        }

        AddNotice(
            result,
            nameof(RentalBillingProfile),
            profileId,
            "rental-billing-profile-purged-mutation-noop",
            "The rental billing profile was already permanently removed. The prior mutation was acknowledged without recreating data; pull to continue.");
    }

    private async Task<RentalBillingProfile?> FindExistingRentalBillingProfileByNaturalKeyAsync(
        RentalBillingProfileDto dto,
        string requestedTenantCode,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var profileKey = (dto.ProfileKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(profileKey))
            return null;

        var exact = pushSnapshot is null
            ? await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    profile =>
                        profile.TenantCode == requestedTenantCode &&
                        profile.ProfileKey == profileKey,
                    cancellationToken)
            : pushSnapshot.FindProfile(requestedTenantCode, profileKey);
        if (exact is not null)
            return exact;

        var legacyProfileKey = RentalDuplicateNormalizer.BuildLegacyProfileKey(
            dto.ManagementCompanyCode,
            dto.CustomerId,
            dto.BusinessNumber,
            dto.CustomerName,
            dto.BillingType,
            dto.BillingAdvanceMode,
            dto.BillingDay,
            dto.BillingCycleMonths,
            dto.BillingMethod);
        if (string.IsNullOrWhiteSpace(legacyProfileKey) ||
            string.Equals(profileKey, legacyProfileKey, StringComparison.Ordinal))
            return null;

        var linkedCustomer = await GetRentalReferenceCustomerAsync(
            dto.CustomerId,
            cancellationToken,
            pushSnapshot);
        if (IsDistinctBillingCustomerAlias(dto.CustomerName, linkedCustomer?.NameOriginal))
            return null;

        var legacyMatch = pushSnapshot is null
            ? await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    profile =>
                        profile.TenantCode == requestedTenantCode &&
                        profile.ProfileKey == legacyProfileKey,
                    cancellationToken)
            : pushSnapshot.FindProfile(requestedTenantCode, legacyProfileKey);
        if (legacyMatch is not null || pushSnapshot is null)
            return legacyMatch;

        var canonicalLegacyKey = await BuildCanonicalRentalBillingProfileLegacyKeyAsync(
            dto,
            pushSnapshot,
            cancellationToken);
        return string.IsNullOrWhiteSpace(canonicalLegacyKey) ||
               string.Equals(canonicalLegacyKey, legacyProfileKey, StringComparison.Ordinal)
            ? null
            : pushSnapshot.FindProfile(requestedTenantCode, canonicalLegacyKey);
    }

    private async Task<string> BuildCanonicalRentalBillingProfileLegacyKeyAsync(
        RentalBillingProfileDto dto,
        RentalBillingProfilePushSnapshot pushSnapshot,
        CancellationToken cancellationToken)
    {
        var resolvedCustomerId = await ResolveRentalBillingProfileCustomerReferenceAsync(
            dto,
            cancellationToken,
            pushSnapshot);
        var linkedCustomer = await GetRentalReferenceCustomerAsync(
            resolvedCustomerId,
            cancellationToken,
            pushSnapshot);
        if (linkedCustomer is null)
        {
            return RentalDuplicateNormalizer.BuildLegacyProfileKey(
                dto.ManagementCompanyCode,
                dto.CustomerId,
                dto.BusinessNumber,
                dto.CustomerName,
                dto.BillingType,
                dto.BillingAdvanceMode,
                dto.BillingDay,
                dto.BillingCycleMonths,
                dto.BillingMethod);
        }

        var resolvedResponsibleOfficeCode = ResolveRentalCustomerOfficeCode(
            linkedCustomer.ResponsibleOfficeCode);
        var resolvedOwnerOfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            linkedCustomer.OfficeCode,
            resolvedResponsibleOfficeCode,
            linkedCustomer.OfficeCode);
        return RentalDuplicateNormalizer.BuildLegacyProfileKey(
            resolvedOwnerOfficeCode,
            linkedCustomer.Id,
            linkedCustomer.BusinessNumber?.Trim(),
            RentalCatalogValueNormalizer.NormalizeDisplayText(linkedCustomer.NameOriginal),
            dto.BillingType,
            dto.BillingAdvanceMode,
            dto.BillingDay,
            dto.BillingCycleMonths,
            dto.BillingMethod);
    }

    private static Dictionary<RentalBillingProfileDto, Guid> CaptureOriginalRentalBillingProfileIds(
        IEnumerable<RentalBillingProfileDto> payload)
    {
        var result = new Dictionary<RentalBillingProfileDto, Guid>(ReferenceEqualityComparer.Instance);
        foreach (var dto in payload)
            result[dto] = dto.Id;

        return result;
    }

    private static Guid GetOriginalRentalBillingProfileId(
        RentalBillingProfileDto dto,
        IReadOnlyDictionary<RentalBillingProfileDto, Guid> originalProfileIds)
        => originalProfileIds.TryGetValue(dto, out var originalId)
            ? originalId
            : dto.Id;

    private static IEnumerable<Guid> GetRentalBillingProfilePreflightIds(
        RentalBillingProfileDto dto,
        IReadOnlyDictionary<RentalBillingProfileDto, Guid> originalProfileIds)
    {
        var originalId = GetOriginalRentalBillingProfileId(dto, originalProfileIds);
        if (originalId != Guid.Empty)
            yield return originalId;

        var deterministicId = SyncIdentityGenerator.CreateRentalBillingProfileId(dto.ProfileKey);
        if (deterministicId != Guid.Empty && deterministicId != originalId)
            yield return deterministicId;
    }

    private bool TryResolveRequestedRentalBillingProfileTenant(
        RentalBillingProfileDto dto,
        out string requestedTenantCode)
    {
        if (!_officeScopeService.HasGlobalDataScope)
        {
            requestedTenantCode = _officeScopeService.CurrentTenantCode;
            return TenantScopeCatalog.TryNormalizeTenantCode(
                requestedTenantCode,
                out requestedTenantCode);
        }

        var rawTenantCode = (dto.TenantCode ?? string.Empty).Trim();
        var hasRequestedTenant = !string.IsNullOrWhiteSpace(rawTenantCode);
        requestedTenantCode = string.Empty;
        if (hasRequestedTenant &&
            !TenantScopeCatalog.TryNormalizeTenantCode(rawTenantCode, out requestedTenantCode))
        {
            requestedTenantCode = string.Empty;
            return false;
        }

        string? requestedOfficeCode = null;
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(dto.OfficeCode, out var normalizedOfficeCode) &&
            !OfficeCodeCatalog.IsSharedOfficeCode(normalizedOfficeCode))
        {
            requestedOfficeCode = normalizedOfficeCode;
        }
        else if (!hasRequestedTenant &&
                 OfficeCodeCatalog.TryNormalizeOfficeCode(dto.ResponsibleOfficeCode, out var normalizedResponsibleOfficeCode) &&
                 !OfficeCodeCatalog.IsSharedOfficeCode(normalizedResponsibleOfficeCode))
        {
            requestedOfficeCode = normalizedResponsibleOfficeCode;
        }

        if (hasRequestedTenant)
        {
            return requestedOfficeCode is null ||
                   TenantScopeCatalog.TenantContainsOffice(
                       requestedTenantCode,
                       requestedOfficeCode);
        }

        if (requestedOfficeCode is not null)
        {
            requestedTenantCode = TenantScopeCatalog.GetTenantCodeForOffice(requestedOfficeCode);
            return true;
        }

        requestedTenantCode = _officeScopeService.CurrentTenantCode;
        return TenantScopeCatalog.TryNormalizeTenantCode(
            requestedTenantCode,
            out requestedTenantCode);
    }

    private static bool RentalBillingProfileTenantMatches(
        string? candidateTenantCode,
        string requestedTenantCode)
        => TenantScopeCatalog.TryNormalizeTenantCode(candidateTenantCode, out var normalizedCandidateTenantCode) &&
           string.Equals(
               normalizedCandidateTenantCode,
               requestedTenantCode,
               StringComparison.OrdinalIgnoreCase);

    private static List<RecycleBinPurgeRecord> GetApplicableRentalBillingProfilePurgeRecords(
        Guid originalProfileId,
        Guid deterministicProfileId,
        string requestedTenantCode,
        IReadOnlyDictionary<Guid, List<RecycleBinPurgeRecord>> purgeRecordsByEntityId)
    {
        var applicable = new List<RecycleBinPurgeRecord>();
        var primaryProfileId = originalProfileId == Guid.Empty
            ? deterministicProfileId
            : originalProfileId;
        if (primaryProfileId != Guid.Empty &&
            purgeRecordsByEntityId.TryGetValue(primaryProfileId, out var primaryRecords))
        {
            applicable.AddRange(primaryRecords);
        }

        if (originalProfileId != Guid.Empty &&
            deterministicProfileId != Guid.Empty &&
            deterministicProfileId != originalProfileId &&
            purgeRecordsByEntityId.TryGetValue(deterministicProfileId, out var deterministicRecords) &&
            deterministicRecords.Any(record =>
                RentalBillingProfileTenantMatches(
                    record.TenantCode,
                    requestedTenantCode)))
        {
            applicable.AddRange(deterministicRecords);
        }

        return applicable
            .DistinctBy(record => record.Id)
            .ToList();
    }

    private bool TrySelectRentalBillingProfilePurgeRecord(
        IReadOnlyCollection<RecycleBinPurgeRecord> purgeRecords,
        string requestedTenantCode,
        out RecycleBinPurgeRecord purgeRecord)
    {
        purgeRecord = null!;
        if (purgeRecords.Count == 0)
            return false;

        var scopeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in purgeRecords)
        {
            if (!RentalBillingProfileTenantMatches(record.TenantCode, requestedTenantCode) ||
                !OfficeCodeCatalog.TryNormalizeOfficeCode(record.OfficeCode, out var normalizedOfficeCode) ||
                !_officeScopeService.CanWriteOfficeForRentals(record.OfficeCode, record.TenantCode))
            {
                return false;
            }

            scopeKeys.Add(string.Join("|", requestedTenantCode, normalizedOfficeCode));
        }

        if (scopeKeys.Count != 1)
            return false;

        purgeRecord = purgeRecords
            .OrderByDescending(record => record.Revision)
            .ThenByDescending(record => NormalizeConflictUtc(record.UpdatedAtUtc))
            .First();
        return true;
    }

    private List<RentalBillingProfileDto> FilterAmbiguousIncomingRentalBillingProfiles(
        IEnumerable<RentalBillingProfileDto> payload,
        IReadOnlyDictionary<RentalBillingProfileDto, Guid> originalProfileIds,
        SyncPushResult result)
    {
        var rows = payload.ToList();
        var rejected = new HashSet<RentalBillingProfileDto>(ReferenceEqualityComparer.Instance);
        void RejectAmbiguousGroup(IEnumerable<RentalBillingProfileDto> candidates)
        {
            var group = candidates
                .Where(dto => !rejected.Contains(dto))
                .ToList();
            if (group.Count <= 1)
                return;

            var exactReplayCount = 0;
            var anonymousNovelCount = 0;
            var namedNovelSignatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dto in group)
            {
                if (HasExactProcessedMutationReplay(
                        dto,
                        nameof(RentalBillingProfile),
                        GetOriginalRentalBillingProfileId(dto, originalProfileIds)))
                {
                    exactReplayCount++;
                    continue;
                }

                var mutationId = NormalizeMutationId(dto.MutationId);
                if (string.IsNullOrWhiteSpace(mutationId))
                {
                    anonymousNovelCount++;
                    continue;
                }

                namedNovelSignatures.Add(string.Join(
                    "|",
                    mutationId,
                    dto.ExpectedRevision.ToString(CultureInfo.InvariantCulture),
                    SyncMutationPayloadHasher.Compute(dto)));
            }

            var logicalNovelCount = anonymousNovelCount + namedNovelSignatures.Count;
            if (logicalNovelCount <= 1 && (exactReplayCount == 0 || logicalNovelCount == 0))
                return;

            foreach (var dto in group)
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    "Multiple rental billing profile mutations resolve to the same canonical identity. Pull before retrying them separately.",
                    result);
                rejected.Add(dto);
            }
        }

        foreach (var group in rows
                     .Where(dto => dto.Id != Guid.Empty)
                     .GroupBy(dto => dto.Id))
        {
            RejectAmbiguousGroup(group);
        }

        foreach (var group in rows
                     .Where(dto =>
                         !rejected.Contains(dto) &&
                         !string.IsNullOrWhiteSpace(dto.TenantCode) &&
                         !string.IsNullOrWhiteSpace(dto.ProfileKey))
                     .GroupBy(dto => new RentalProfileNaturalKey(
                         dto.TenantCode,
                         dto.ProfileKey.Trim())))
        {
            RejectAmbiguousGroup(group);
        }

        return rows
            .Where(dto => !rejected.Contains(dto))
            .ToList();
    }

    private async Task<Dictionary<Guid, Guid>> BuildRentalBillingProfileRestoreCustomerIdsAsync(
        List<RentalBillingProfileDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var linkedCustomerIds = new Dictionary<Guid, Guid>();
        var rejectedProfileIds = new HashSet<Guid>();
        var candidates = payload
            .Where(dto => dto.Id != Guid.Empty && !dto.IsDeleted)
            .ToList();
        var profilesById = new Dictionary<Guid, RentalBillingProfile>();
        if (pushSnapshot is null)
        {
            foreach (var profileIdBatch in candidates.Select(dto => dto.Id).Distinct().Chunk(500))
            {
                var batch = profileIdBatch.ToArray();
                var profiles = await _dbContext.RentalBillingProfiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(profile => batch.Contains(profile.Id))
                    .ToListAsync(cancellationToken);
                foreach (var profile in profiles)
                    profilesById[profile.Id] = profile;
            }
        }

        var candidateCustomerIds = candidates
            .Select(dto =>
            {
                var existing = pushSnapshot?.FindProfile(dto.Id) ?? profilesById.GetValueOrDefault(dto.Id);
                return dto.CustomerId is Guid customerId && customerId != Guid.Empty
                    ? customerId
                    : existing?.CustomerId.GetValueOrDefault() ?? Guid.Empty;
            })
            .Where(customerId => customerId != Guid.Empty)
            .Distinct()
            .ToList();
        var customersById = new Dictionary<Guid, Customer>();
        foreach (var customerId in candidateCustomerIds)
        {
            var customer = pushSnapshot?.FindCustomer(customerId);
            if (customer is not null)
                customersById[customerId] = customer;
        }
        foreach (var customerIdBatch in candidateCustomerIds
                     .Where(customerId => !customersById.ContainsKey(customerId))
                     .Chunk(500))
        {
            var batch = customerIdBatch.ToArray();
            var customers = await _dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => batch.Contains(customer.Id))
                .ToListAsync(cancellationToken);
            foreach (var customer in customers)
                customersById[customer.Id] = customer;
        }

        foreach (var dto in candidates)
        {
            var existing = pushSnapshot?.FindProfile(dto.Id) ?? profilesById.GetValueOrDefault(dto.Id);
            if (existing is null || !existing.IsDeleted)
                continue;

            var customerId = dto.CustomerId.HasValue && dto.CustomerId.Value != Guid.Empty
                ? dto.CustomerId.Value
                : existing.CustomerId.GetValueOrDefault();
            if (customerId == Guid.Empty)
                continue;

            var customer = customersById.GetValueOrDefault(customerId);
            if (customer is null || !customer.IsDeleted)
                continue;

            if (!_officeScopeService.CanEditCustomers())
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    $"Linked deleted customer cannot be restored without customer edit permission: {customerId}.",
                    result);
                rejectedProfileIds.Add(dto.Id);
                continue;
            }

            if (!_officeScopeService.CanWriteOfficeForCustomers(
                    customer.ResponsibleOfficeCode,
                    customer.TenantCode,
                    customer.OfficeCode))
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    $"Linked deleted customer cannot be restored in the current office scope: {customerId}.",
                    result);
                rejectedProfileIds.Add(dto.Id);
                continue;
            }

            linkedCustomerIds[dto.Id] = customerId;
        }

        if (rejectedProfileIds.Count > 0)
            payload.RemoveAll(dto => rejectedProfileIds.Contains(dto.Id));

        return linkedCustomerIds;
    }

    private async Task RestoreLinkedDeletedCustomerContractsForRentalBillingProfilesAsync(
        IEnumerable<RentalBillingProfileDto> acceptedProfiles,
        IReadOnlyDictionary<Guid, Guid> linkedCustomerIds,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        if (linkedCustomerIds.Count == 0)
            return;

        var candidates = acceptedProfiles
            .Where(dto => !dto.IsDeleted && dto.Id != Guid.Empty && linkedCustomerIds.ContainsKey(dto.Id))
            .ToList();
        var profilesById = new Dictionary<Guid, RentalBillingProfile>();
        foreach (var dto in candidates)
        {
            var profile = pushSnapshot?.FindProfile(dto.Id);
            if (profile is not null)
                profilesById[dto.Id] = profile;
        }
        foreach (var profileIdBatch in candidates
                     .Select(dto => dto.Id)
                     .Where(profileId => !profilesById.ContainsKey(profileId))
                     .Distinct()
                     .Chunk(500))
        {
            var batch = profileIdBatch.ToArray();
            var profiles = await _dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .Where(profile => batch.Contains(profile.Id))
                .ToListAsync(cancellationToken);
            foreach (var profile in profiles)
                profilesById[profile.Id] = profile;
        }

        var restoredCustomers = await RestoreDeletedLinkedCustomersAndContractsAsync(
            candidates.Select(dto => linkedCustomerIds[dto.Id]),
            cancellationToken,
            pushSnapshot);
        foreach (var dto in candidates)
        {
            var profile = profilesById.GetValueOrDefault(dto.Id);
            if (profile is null || profile.IsDeleted)
                continue;

            var customerId = linkedCustomerIds[dto.Id];
            if (!restoredCustomers.TryGetValue(customerId, out var customer))
                continue;

            profile.CustomerId = customer.Id;
            if (string.IsNullOrWhiteSpace(profile.CustomerName))
                profile.CustomerName = customer.NameOriginal;
        }
    }

    private async Task<List<RentalBillingProfileDto>> UpsertRentalBillingProfilesAsync(
        IEnumerable<RentalBillingProfileDto> payload,
        IReadOnlyDictionary<RentalBillingProfileDto, Guid> originalProfileIds,
        IDictionary<Guid, Guid> acknowledgedProfileIds,
        IDictionary<Guid, Guid> acceptedActiveProfileIdsForReferences,
        ISet<RentalProfileTenantIdentity> blockedPriorGenerationProfileIdentitiesForReferences,
        SyncPushResult result,
        string deviceId,
        ICollection<(Guid ProfileId, Guid? RunId)> tombstoneSettlementTargets,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var accepted = new List<RentalBillingProfileDto>();
        var exactReplayEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();
        var acceptedEntityIdsForHistoricalConflictResolution = new HashSet<Guid>();
        foreach (var dto in payload)
        {
            var originalProfileId = GetOriginalRentalBillingProfileId(dto, originalProfileIds);
            var exactReplay = HasExactProcessedMutationReplay(
                dto,
                nameof(RentalBillingProfile),
                originalProfileId);
            if (TryAcceptDuplicateMutation(
                    dto,
                    nameof(RentalBillingProfile),
                    result,
                    exactReplayEntityIdsForHistoricalConflictResolution,
                    originalProfileId))
            {
                if (exactReplay)
                {
                    if (originalProfileId != Guid.Empty)
                        exactReplayEntityIdsForHistoricalConflictResolution.Add(originalProfileId);
                    var replayedEntity = pushSnapshot is null
                        ? await _dbContext.RentalBillingProfiles
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .FirstOrDefaultAsync(profile => profile.Id == dto.Id, cancellationToken)
                        : pushSnapshot.FindProfile(dto.Id);
                    var currentGenerationProven = replayedEntity is not null &&
                                                  CanProveExactReplayTargetsCurrentRentalProfileGeneration(
                                                      dto,
                                                      originalProfileId,
                                                      replayedEntity);
                    if (!currentGenerationProven)
                    {
                        AddBlockedRentalProfileIdentity(
                            blockedPriorGenerationProfileIdentitiesForReferences,
                            originalProfileId,
                            dto.TenantCode);
                        AddBlockedRentalProfileIdentity(
                            blockedPriorGenerationProfileIdentitiesForReferences,
                            dto.Id,
                            dto.TenantCode);
                    }

                    if (replayedEntity is not null)
                    {
                        if (!currentGenerationProven)
                        {
                            AddBlockedRentalProfileIdentity(
                                blockedPriorGenerationProfileIdentitiesForReferences,
                                replayedEntity.Id,
                                replayedEntity.TenantCode);
                        }

                        RecordRentalBillingProfileAcknowledgement(
                            originalProfileId,
                            replayedEntity.Id,
                            currentGenerationProven,
                            acknowledgedProfileIds,
                            acceptedActiveProfileIdsForReferences);
                    }
                }

                continue;
            }

            var entity = pushSnapshot is null
                ? await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken)
                : pushSnapshot.FindProfile(dto.Id);
            if (entity is null)
            {
                entity = await FindExistingRentalBillingProfileByNaturalKeyAsync(
                    dto,
                    dto.TenantCode,
                    cancellationToken,
                    pushSnapshot);
            }

            if (entity is null)
            {
                var deterministicProfileId = SyncIdentityGenerator.CreateRentalBillingProfileId(dto.ProfileKey);
                var newEntity = new RentalBillingProfile
                {
                    Id = dto.Id == Guid.Empty
                        ? (deterministicProfileId == Guid.Empty ? Guid.NewGuid() : deterministicProfileId)
                        : dto.Id
                };
                var newEntityTombstonePreparation = await PrepareRentalBillingRunTombstoneMutationAsync(
                    existing: null,
                    dto: dto,
                    authoritativeProfileId: newEntity.Id,
                    allowManualIntent: true,
                    cancellationToken: cancellationToken);
                if (!newEntityTombstonePreparation.Accepted)
                {
                    AddClientConflict(
                        dto,
                        nameof(RentalBillingProfile),
                        newEntityTombstonePreparation.Error,
                        result);
                    continue;
                }

                ApplyRentalBillingProfileForSync(
                    newEntity,
                    dto,
                    isNew: true,
                    reusesExistingNaturalKey: false,
                    allowManualStateTransition: false);
                dto.Id = newEntity.Id;
                _dbContext.RentalBillingProfiles.Add(newEntity);
                pushSnapshot?.AddProfile(newEntity);
                RegisterProcessedMutation(dto, nameof(RentalBillingProfile), deviceId);
                acceptedEntityIdsForHistoricalConflictResolution.Add(newEntity.Id);
                RecordRentalBillingProfileAcknowledgement(
                    originalProfileId,
                    newEntity.Id,
                    !newEntity.IsDeleted,
                    acknowledgedProfileIds,
                    acceptedActiveProfileIdsForReferences);
                accepted.Add(dto);
                foreach (var runId in newEntityTombstonePreparation.NewlyTombstonedRunIds)
                    tombstoneSettlementTargets.Add((newEntity.Id, runId));
                result.AcceptedCount++;
                continue;
            }

            if (await TryAcceptAlreadyDeletedMutationAsync(entity, dto, nameof(RentalBillingProfile), deviceId, result, cancellationToken))
            {
                RecordRentalBillingProfileAcknowledgement(
                    originalProfileId,
                    entity.Id,
                    false,
                    acknowledgedProfileIds,
                    acceptedActiveProfileIdsForReferences);
                continue;
            }

            if (HasExpectedRevisionConflict(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, nameof(RentalBillingProfile), BuildExpectedRevisionConflictReason(dto.ExpectedRevision, entity.Revision), result, cancellationToken);
                continue;
            }

            if (IsServerEntityNewer(entity, dto))
            {
                await AddServerConflictAsync(dto, entity, nameof(RentalBillingProfile), "Server version is newer.", result, cancellationToken);
                continue;
            }

            var reusesExistingNaturalKey = IsIncomingRentalBillingProfileIdReusedByNaturalKey(
                originalProfileId,
                entity.Id,
                dto.ProfileKey);
            if (!reusesExistingNaturalKey &&
                !TryValidateProjectedRentalBillingRunLimit(
                    entity.BillingRunsJson,
                    dto.BillingRunsJson,
                    out var projectedRunsValidationError))
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    projectedRunsValidationError,
                    result);
                continue;
            }

            var allowManualStateTransition = !reusesExistingNaturalKey &&
                                             originalProfileId == entity.Id &&
                                             dto.ExpectedRevision > 0 &&
                                              dto.ExpectedRevision == entity.Revision;
            var tombstonePreparation = await PrepareRentalBillingRunTombstoneMutationAsync(
                existing: entity,
                dto: dto,
                authoritativeProfileId: entity.Id,
                allowManualIntent: allowManualStateTransition,
                cancellationToken: cancellationToken);
            if (!tombstonePreparation.Accepted)
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    tombstonePreparation.Error,
                    result);
                continue;
            }

            dto.Id = entity.Id;
            ApplyRentalBillingProfileForSync(
                entity,
                dto,
                isNew: false,
                reusesExistingNaturalKey,
                allowManualStateTransition);
            RegisterProcessedMutation(dto, nameof(RentalBillingProfile), deviceId);
            acceptedEntityIdsForHistoricalConflictResolution.Add(entity.Id);
            RecordRentalBillingProfileAcknowledgement(
                originalProfileId,
                entity.Id,
                !entity.IsDeleted,
                acknowledgedProfileIds,
                acceptedActiveProfileIdsForReferences);
            accepted.Add(dto);
            foreach (var runId in tombstonePreparation.NewlyTombstonedRunIds)
                tombstoneSettlementTargets.Add((entity.Id, runId));
            result.AcceptedCount++;
        }

        await ResolveHistoricalConflictsAsync(
            nameof(RentalBillingProfile),
            exactReplayEntityIdsForHistoricalConflictResolution,
            "이미 처리된 동일 mutation 이 확인되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);
        await ResolveHistoricalConflictsAsync(
            nameof(RentalBillingProfile),
            acceptedEntityIdsForHistoricalConflictResolution,
            "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.",
            cancellationToken);

        return accepted;
    }

    private static bool IsIncomingRentalBillingProfileIdReusedByNaturalKey(
        Guid originalProfileId,
        Guid existingEntityId,
        string? profileKey)
        => originalProfileId != Guid.Empty &&
           originalProfileId != existingEntityId &&
           !string.IsNullOrWhiteSpace(profileKey);

    private static bool CanProveExactReplayTargetsCurrentRentalProfileGeneration(
        RentalBillingProfileDto dto,
        Guid originalProfileId,
        RentalBillingProfile entity)
    {
        if (originalProfileId == Guid.Empty ||
            originalProfileId != dto.Id ||
            dto.Id != entity.Id ||
            dto.IsDeleted ||
            entity.IsDeleted ||
            dto.Revision <= 0 ||
            dto.ExpectedRevision != dto.Revision ||
            entity.Revision != dto.Revision ||
            dto.UpdatedAtUtc == default ||
            entity.UpdatedAtUtc == default ||
            NormalizeConflictUtc(dto.UpdatedAtUtc) !=
            NormalizeConflictUtc(entity.UpdatedAtUtc) ||
            string.IsNullOrWhiteSpace(dto.ProfileKey) ||
            !string.Equals(
                dto.ProfileKey.Trim(),
                entity.ProfileKey.Trim(),
                StringComparison.Ordinal) ||
            !RentalBillingProfileTenantMatches(
                entity.TenantCode,
                dto.TenantCode))
        {
            return false;
        }

        return true;
    }

    private static void RecordRentalBillingProfileAcknowledgement(
        Guid originalProfileId,
        Guid canonicalProfileId,
        bool canRemapActiveReferences,
        IDictionary<Guid, Guid> acknowledgedProfileIds,
        IDictionary<Guid, Guid> acceptedActiveProfileIdsForReferences)
    {
        if (canonicalProfileId == Guid.Empty)
            return;

        var responseProfileId = originalProfileId == Guid.Empty
            ? canonicalProfileId
            : originalProfileId;
        acknowledgedProfileIds[responseProfileId] = canonicalProfileId;
        if (canRemapActiveReferences)
            acceptedActiveProfileIdsForReferences[responseProfileId] = canonicalProfileId;
    }

    private async Task<RentalBillingRunTombstonePreparation> PrepareRentalBillingRunTombstoneMutationAsync(
        RentalBillingProfile? existing,
        RentalBillingProfileDto dto,
        Guid authoritativeProfileId,
        bool allowManualIntent,
        CancellationToken cancellationToken)
    {
        if (!TryParseRentalBillingRunObjects(dto.BillingRunsJson, out var incomingRuns))
        {
            return RentalBillingRunTombstonePreparation.Reject(
                "Rental billing run history must be a valid JSON array before applying a tombstone.");
        }

        var requestedTombstones = incomingRuns
            .Where(IsRentalBillingRunTombstoneRequested)
            .ToList();
        if (requestedTombstones.Count == 0)
            return RentalBillingRunTombstonePreparation.Accept([]);

        List<JsonObject> existingRuns = [];
        if (existing is not null &&
            !TryParseRentalBillingRunObjects(existing.BillingRunsJson, out existingRuns))
        {
            return RentalBillingRunTombstonePreparation.Reject(
                "The server rental billing run history is malformed and cannot accept a tombstone.");
        }

        var newlyTombstonedRunIds = new List<Guid>();
        var tombstonedAtUtc = DateTime.UtcNow;
        var tombstonedByUsername = string.IsNullOrWhiteSpace(_currentUserContext.Username)
            ? "system"
            : _currentUserContext.Username.Trim();
        foreach (var incomingRun in requestedTombstones)
        {
            if (!TryGetRentalBillingRunId(incomingRun, out var runId))
            {
                return RentalBillingRunTombstonePreparation.Reject(
                    "A rental billing run tombstone requires a non-empty RunId.");
            }

            JsonObject? authoritativeRun = null;
            if (existing is not null)
            {
                var lookup = RentalBillingRunTombstonePolicy.LookupForServerMutation(
                    existing.BillingRunsJson,
                    runId);
                if (!lookup.IsValid)
                {
                    return RentalBillingRunTombstonePreparation.Reject(
                        "The server rental billing run history contains an invalid tombstone marker.");
                }

                if (lookup.IsTombstoned)
                    continue;

                authoritativeRun = existingRuns.FirstOrDefault(run =>
                    TryGetRentalBillingRunId(run, out var existingRunId) &&
                    existingRunId == runId);
                if (authoritativeRun is null || !lookup.IsFound)
                {
                    return RentalBillingRunTombstonePreparation.Reject(
                        $"Rental billing run was not found for tombstone: {runId:D}.");
                }
            }

            if (!allowManualIntent)
            {
                return RentalBillingRunTombstonePreparation.Reject(
                    "Rental billing run tombstone requires an exact profile revision and explicit manual intent.");
            }

            var authoritativeStatus = GetRentalBillingRunString(
                authoritativeRun ?? incomingRun,
                "Status");
            if (!string.Equals(
                    authoritativeStatus.Trim(),
                    "\uC608\uC815",
                    StringComparison.OrdinalIgnoreCase))
            {
                return RentalBillingRunTombstonePreparation.Reject(
                    "Only a planned rental billing run can be tombstoned.");
            }

            if (await HasRentalBillingRunFinancialEvidenceAsync(
                    authoritativeProfileId,
                    runId,
                    cancellationToken))
            {
                return RentalBillingRunTombstonePreparation.Reject(
                    "A rental billing run with invoice, settlement transaction, or direct payment evidence cannot be tombstoned.");
            }

            ApplyCanonicalRentalBillingRunTombstone(
                incomingRun,
                tombstonedAtUtc,
                tombstonedByUsername);
            newlyTombstonedRunIds.Add(runId);
        }

        dto.BillingRunsJson = new JsonArray(
            incomingRuns.Select(run => (JsonNode?)run).ToArray()).ToJsonString();
        return RentalBillingRunTombstonePreparation.Accept(newlyTombstonedRunIds);
    }

    private async Task<bool> HasRentalBillingRunFinancialEvidenceAsync(
        Guid profileId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty || runId == Guid.Empty)
            return false;

        if (await _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().AnyAsync(invoice =>
                !invoice.IsDeleted &&
                invoice.IsLatestVersion &&
                invoice.LinkedRentalBillingProfileId == profileId &&
                invoice.LinkedRentalBillingRunId == runId,
                cancellationToken))
        {
            return true;
        }

        if (await _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().AnyAsync(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedRentalBillingProfileId == profileId &&
                transaction.LinkedRentalBillingRunId == runId,
                cancellationToken))
        {
            return true;
        }

        return await (
            from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking()
            join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
                on payment.InvoiceId equals invoice.Id
            where !payment.IsDeleted &&
                  !invoice.IsDeleted &&
                  invoice.IsLatestVersion &&
                  invoice.LinkedRentalBillingProfileId == profileId &&
                  invoice.LinkedRentalBillingRunId == runId
            select payment.Id).AnyAsync(cancellationToken);
    }

    private static bool IsRentalBillingRunTombstoneRequested(JsonObject run)
        => TryGetRentalBillingRunProperty(
               run,
               RentalBillingRunTombstonePolicy.IsTombstonedPropertyName,
               out var node) &&
           node is JsonValue value &&
           value.TryGetValue<bool>(out var isTombstoned) &&
           isTombstoned;

    private static void ApplyCanonicalRentalBillingRunTombstone(
        JsonObject run,
        DateTime tombstonedAtUtc,
        string tombstonedByUsername)
    {
        SetCanonicalRentalBillingRunProperty(
            run,
            RentalBillingRunTombstonePolicy.IsTombstonedPropertyName,
            JsonValue.Create(true));
        SetCanonicalRentalBillingRunProperty(
            run,
            RentalBillingRunTombstonePolicy.TombstonedAtUtcPropertyName,
            JsonValue.Create(tombstonedAtUtc));
        SetCanonicalRentalBillingRunProperty(
            run,
            RentalBillingRunTombstonePolicy.TombstonedByUsernamePropertyName,
            JsonValue.Create(tombstonedByUsername));
        SetRentalBillingRunProperty(run, "Status", JsonValue.Create("\uCDE8\uC18C"));
        SetRentalBillingRunProperty(run, "BilledAmount", JsonValue.Create(0m));
        SetRentalBillingRunProperty(run, "SettledAmount", JsonValue.Create(0m));
        SetRentalBillingRunProperty(run, "SettlementStatus", JsonValue.Create("\uBBF8\uC785\uAE08"));
        SetRentalBillingRunProperty(run, "SettledDate", null);
    }

    private static DateTime ReadRentalBillingRunTombstonedAtUtc(JsonObject run)
    {
        if (!TryGetRentalBillingRunProperty(
                run,
                RentalBillingRunTombstonePolicy.TombstonedAtUtcPropertyName,
                out var node) ||
            node is not JsonValue value)
        {
            return DateTime.UtcNow;
        }

        if (value.TryGetValue<DateTime>(out var timestamp))
            return timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
        if (value.TryGetValue<string>(out var text) &&
            DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out timestamp))
        {
            return timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
        }

        return DateTime.UtcNow;
    }

    private static string ReadRentalBillingRunTombstonedByUsername(JsonObject run)
        => GetRentalBillingRunString(
               run,
               RentalBillingRunTombstonePolicy.TombstonedByUsernamePropertyName)
           .Trim() is { Length: > 0 } username
            ? username
            : "system";

    private static void SetCanonicalRentalBillingRunProperty(
        JsonObject run,
        string propertyName,
        JsonNode? value)
    {
        foreach (var existingPropertyName in run
                     .Select(property => property.Key)
                     .Where(name => string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            run.Remove(existingPropertyName);
        }

        run[propertyName] = value;
    }

    private sealed record RentalBillingRunTombstonePreparation(
        bool Accepted,
        IReadOnlyCollection<Guid> NewlyTombstonedRunIds,
        string Error)
    {
        public static RentalBillingRunTombstonePreparation Accept(
            IReadOnlyCollection<Guid> runIds)
            => new(true, runIds, string.Empty);

        public static RentalBillingRunTombstonePreparation Reject(string error)
            => new(false, [], error);
    }

    private static void ApplyRentalBillingProfileForSync(
        RentalBillingProfile entity,
        RentalBillingProfileDto dto,
        bool isNew,
        bool reusesExistingNaturalKey,
        bool allowManualStateTransition)
    {
        const string billingStatusPlanned = "\uC608\uC815";
        const string settlementStatusUnpaid = "\uBBF8\uC785\uAE08";
        const string completionStatusPending = "\uBBF8\uC644\uB8CC";

        var existingTemplateJson = entity.BillingTemplateJson;
        var existingRunsJson = entity.BillingRunsJson;
        var existingLastBilledDate = entity.LastBilledDate;
        var existingLastSettledDate = entity.LastSettledDate;
        var existingSettledAmount = entity.SettledAmount;
        var existingOutstandingAmount = entity.OutstandingAmount;
        var existingSettlementStatus = entity.SettlementStatus;
        var existingCompletionStatus = entity.CompletionStatus;
        var existingBillingStatus = entity.BillingStatus;
        var existingRequiresFollowUp = entity.RequiresFollowUp;

        entity.Apply(dto);

        if (isNew)
        {
            entity.LastBilledDate = null;
            entity.LastSettledDate = null;
            entity.SettledAmount = 0m;
            entity.OutstandingAmount = 0m;
            entity.SettlementStatus = settlementStatusUnpaid;
            entity.CompletionStatus = completionStatusPending;
            entity.BillingStatus = IsManualRentalBillingStopStatus(dto.BillingStatus)
                ? dto.BillingStatus.Trim()
                : billingStatusPlanned;
            entity.RequiresFollowUp = IsRentalBillingHoldStatus(entity.BillingStatus);
            entity.BillingRunsJson = BuildServerAuthoritativeRentalBillingRunsJson(
                existingJson: null,
                dto.BillingRunsJson,
                allowManualIntent: true,
                allowManualResume: false);
            return;
        }

        if (reusesExistingNaturalKey)
        {
            entity.BillingTemplateJson = RentalDuplicateNormalizer.MergeBillingTemplateJson(
                existingTemplateJson,
                dto.BillingTemplateJson);
            entity.BillingRunsJson = existingRunsJson;
            entity.BillingStatus = existingBillingStatus;
        }
        else
        {
            entity.BillingRunsJson = BuildServerAuthoritativeRentalBillingRunsJson(
                existingRunsJson,
                dto.BillingRunsJson,
                allowManualIntent: allowManualStateTransition,
                allowManualResume: allowManualStateTransition);
            entity.BillingStatus = ResolveRentalBillingManualStatusTransition(
                existingBillingStatus,
                dto.BillingStatus,
                allowManualIntent: allowManualStateTransition,
                allowManualResume: allowManualStateTransition);
        }

        entity.LastBilledDate = existingLastBilledDate;
        entity.LastSettledDate = existingLastSettledDate;
        entity.SettledAmount = existingSettledAmount;
        entity.OutstandingAmount = existingOutstandingAmount;
        entity.SettlementStatus = existingSettlementStatus;
        entity.CompletionStatus = existingCompletionStatus;
        entity.RequiresFollowUp = ResolveRentalBillingRequiresFollowUpTransition(
            existingBillingStatus,
            dto.BillingStatus,
            existingRequiresFollowUp,
            existingOutstandingAmount,
            allowManualStateTransition);
    }

    private static string BuildServerAuthoritativeRentalBillingRunsJson(
        string? existingJson,
        string? incomingJson,
        bool allowManualIntent,
        bool allowManualResume)
    {
        const int maxIncomingRuns = 512;
        const string billingStatusPlanned = "\uC608\uC815";
        const string settlementStatusUnpaid = "\uBBF8\uC785\uAE08";

        if (!TryParseRentalBillingRunObjects(incomingJson, out var incomingRuns))
            return string.IsNullOrWhiteSpace(existingJson) ? "[]" : existingJson;

        if (string.IsNullOrWhiteSpace(existingJson))
        {
            var sanitizedNewRuns = new JsonArray();
            var seenIncomingIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var incomingRun in incomingRuns)
            {
                if (sanitizedNewRuns.Count >= maxIncomingRuns ||
                    !TryGetRentalBillingRunIdentity(incomingRun, out var identity) ||
                    !seenIncomingIdentities.Add(identity))
                {
                    continue;
                }

                var sanitized = (JsonObject)incomingRun.DeepClone();
                if (IsRentalBillingRunTombstoneRequested(sanitized))
                {
                    NeutralizeIncomingOnlyRentalBillingRunSchedule(sanitized);
                    ApplyCanonicalRentalBillingRunTombstone(
                        sanitized,
                        ReadRentalBillingRunTombstonedAtUtc(sanitized),
                        ReadRentalBillingRunTombstonedByUsername(sanitized));
                    sanitizedNewRuns.Add(sanitized);
                    continue;
                }

                SetRentalBillingRunProperty(sanitized, "BilledAmount", JsonValue.Create(0m));
                SetRentalBillingRunProperty(sanitized, "SettledAmount", JsonValue.Create(0m));
                SetRentalBillingRunProperty(sanitized, "SettlementStatus", JsonValue.Create(settlementStatusUnpaid));
                SetRentalBillingRunProperty(sanitized, "SettledDate", null);
                var incomingStatus = GetRentalBillingRunString(incomingRun, "Status");
                var preservesManualIntent = IsManualRentalBillingStopStatus(incomingStatus);
                NeutralizeIncomingOnlyRentalBillingRunSchedule(sanitized);
                SetRentalBillingRunProperty(
                    sanitized,
                    "Status",
                    JsonValue.Create(preservesManualIntent
                        ? incomingStatus.Trim()
                        : billingStatusPlanned));
                sanitizedNewRuns.Add(sanitized);
            }

            return sanitizedNewRuns.ToJsonString();
        }

        if (!TryParseRentalBillingRunObjects(existingJson, out var existingRuns))
            return existingJson;
        if (incomingRuns.Count == 0)
            return existingJson;

        var mergedRuns = new JsonArray();
        var existingByRunId = new Dictionary<Guid, JsonObject>();
        var existingByNormalizedRunKey =
            new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var existingRun in existingRuns)
        {
            var clone = (JsonObject)existingRun.DeepClone();
            mergedRuns.Add(clone);
            if (TryGetRentalBillingRunId(existingRun, out var runId))
                existingByRunId.TryAdd(runId, clone);
            var normalizedRunKey = NormalizeRentalBillingRunKey(existingRun);
            if (!string.IsNullOrWhiteSpace(normalizedRunKey))
                existingByNormalizedRunKey.TryAdd(normalizedRunKey, clone);
        }

        var seenIncoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incomingOnlyCount = 0;
        foreach (var incomingRun in incomingRuns)
        {
            if (!TryGetRentalBillingRunIdentity(incomingRun, out var identity) ||
                !seenIncoming.Add(identity))
            {
                continue;
            }

            JsonObject? existingRun = null;
            var hasIncomingRunId = TryGetRentalBillingRunId(incomingRun, out var incomingRunId);
            if (hasIncomingRunId)
                existingByRunId.TryGetValue(incomingRunId, out existingRun);
            var incomingRunKey = GetRentalBillingRunString(incomingRun, "RunKey").Trim();
            var normalizedIncomingRunKey = NormalizeRentalBillingRunKey(incomingRun);
            if (!string.IsNullOrWhiteSpace(normalizedIncomingRunKey) &&
                existingByNormalizedRunKey.TryGetValue(normalizedIncomingRunKey, out var existingByRunKey))
            {
                existingRun ??= existingByRunKey;
            }

            if (existingRun is not null)
            {
                if (hasIncomingRunId && !TryGetRentalBillingRunId(existingRun, out _))
                {
                    SetRentalBillingRunProperty(
                        existingRun,
                        "RunId",
                        JsonValue.Create(incomingRunId));
                    existingByRunId.TryAdd(incomingRunId, existingRun);
                }

                var normalizedExistingRunKey = NormalizeRentalBillingRunKey(existingRun);
                if (string.IsNullOrWhiteSpace(normalizedExistingRunKey) &&
                    !string.IsNullOrWhiteSpace(normalizedIncomingRunKey))
                {
                    SetRentalBillingRunProperty(
                        existingRun,
                        "RunKey",
                        JsonValue.Create(incomingRunKey));
                    existingByNormalizedRunKey.TryAdd(normalizedIncomingRunKey, existingRun);
                }

                if (IsRentalBillingRunTombstoneRequested(existingRun))
                {
                    ApplyCanonicalRentalBillingRunTombstone(
                        existingRun,
                        ReadRentalBillingRunTombstonedAtUtc(existingRun),
                        ReadRentalBillingRunTombstonedByUsername(existingRun));
                    continue;
                }

                if (IsRentalBillingRunTombstoneRequested(incomingRun))
                {
                    ApplyCanonicalRentalBillingRunTombstone(
                        existingRun,
                        ReadRentalBillingRunTombstonedAtUtc(incomingRun),
                        ReadRentalBillingRunTombstonedByUsername(incomingRun));
                    continue;
                }

                var incomingStatus = GetRentalBillingRunString(incomingRun, "Status");
                var existingStatus = GetRentalBillingRunString(existingRun, "Status");
                SetRentalBillingRunProperty(
                    existingRun,
                    "Status",
                    JsonValue.Create(ResolveRentalBillingManualStatusTransition(
                        existingStatus,
                        incomingStatus,
                        allowManualIntent,
                        allowManualResume)));
                if (TryGetRentalBillingRunProperty(incomingRun, "Note", out var incomingNote))
                {
                    SetRentalBillingRunProperty(
                        existingRun,
                        "Note",
                        incomingNote?.DeepClone());
                }

                continue;
            }

            if (incomingOnlyCount >= maxIncomingRuns)
                continue;

            incomingOnlyCount++;
            var sanitized = (JsonObject)incomingRun.DeepClone();
            if (IsRentalBillingRunTombstoneRequested(sanitized))
            {
                NeutralizeIncomingOnlyRentalBillingRunSchedule(sanitized);
                ApplyCanonicalRentalBillingRunTombstone(
                    sanitized,
                    ReadRentalBillingRunTombstonedAtUtc(sanitized),
                    ReadRentalBillingRunTombstonedByUsername(sanitized));
                mergedRuns.Add(sanitized);
                continue;
            }

            SetRentalBillingRunProperty(sanitized, "BilledAmount", JsonValue.Create(0m));
            SetRentalBillingRunProperty(sanitized, "SettledAmount", JsonValue.Create(0m));
            SetRentalBillingRunProperty(sanitized, "SettlementStatus", JsonValue.Create(settlementStatusUnpaid));
            SetRentalBillingRunProperty(sanitized, "SettledDate", null);
            var unmatchedStatus = GetRentalBillingRunString(incomingRun, "Status");
            var preservesManualIntent = allowManualIntent &&
                                        IsManualRentalBillingStopStatus(unmatchedStatus);
            NeutralizeIncomingOnlyRentalBillingRunSchedule(sanitized);
            SetRentalBillingRunProperty(
                sanitized,
                "Status",
                JsonValue.Create(preservesManualIntent
                    ? unmatchedStatus.Trim()
                    : billingStatusPlanned));
            mergedRuns.Add(sanitized);
        }

        return mergedRuns.ToJsonString();
    }

    private static void NeutralizeIncomingOnlyRentalBillingRunSchedule(JsonObject run)
    {
        SetRentalBillingRunProperty(run, "ScheduledDate", JsonValue.Create(DateOnly.MinValue));
        SetRentalBillingRunProperty(run, "PeriodStartDate", JsonValue.Create(DateOnly.MinValue));
        SetRentalBillingRunProperty(run, "PeriodEndDate", JsonValue.Create(DateOnly.MinValue));
        SetRentalBillingRunProperty(run, "CycleMonths", JsonValue.Create(1));
        SetRentalBillingRunProperty(run, "PeriodLabel", JsonValue.Create(string.Empty));
    }

    private static string ResolveRentalBillingManualStatusTransition(
        string? existingStatus,
        string? incomingStatus,
        bool allowManualIntent,
        bool allowManualResume)
    {
        var existing = existingStatus?.Trim() ?? string.Empty;
        var incoming = incomingStatus?.Trim() ?? string.Empty;
        if (allowManualIntent && IsManualRentalBillingStopStatus(incoming))
            return incoming;
        if (allowManualResume &&
            IsManualRentalBillingStopStatus(existing) &&
            IsRentalBillingResumeStatus(incoming))
        {
            return incoming;
        }

        return existing;
    }

    private static bool ResolveRentalBillingRequiresFollowUpTransition(
        string? existingBillingStatus,
        string? incomingBillingStatus,
        bool existingRequiresFollowUp,
        decimal existingOutstandingAmount,
        bool allowManualStateTransition)
    {
        if (!allowManualStateTransition)
            return existingRequiresFollowUp;
        if (IsRentalBillingHoldStatus(incomingBillingStatus))
            return true;
        if (IsManualRentalBillingStopStatus(existingBillingStatus) &&
            IsRentalBillingResumeStatus(incomingBillingStatus))
        {
            return existingOutstandingAmount > 0m;
        }

        return existingRequiresFollowUp;
    }

    private static bool IsManualRentalBillingStopStatus(string? status)
        => IsRentalBillingHoldStatus(status) ||
           IsRentalBillingCancelledStatus(status);

    private static bool IsRentalBillingHoldStatus(string? status)
    {
        var normalized = status?.Trim();
        return string.Equals(normalized, "\uBCF4\uB958", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRentalBillingCancelledStatus(string? status)
    {
        var normalized = status?.Trim();
        return string.Equals(normalized, "\uCDE8\uC18C", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRentalBillingResumeStatus(string? status)
    {
        var normalized = status?.Trim();
        return string.Equals(normalized, "\uC608\uC815", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "\uCCAD\uAD6C\uC911", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseRentalBillingRunObjects(
        string? json,
        out List<JsonObject> runs)
    {
        runs = [];
        if (string.IsNullOrWhiteSpace(json))
            return true;

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array)
                return false;
            if (array.Any(node => node is not JsonObject))
                return false;

            runs = array
                .OfType<JsonObject>()
                .Select(run => (JsonObject)run.DeepClone())
                .ToList();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryValidateIncomingRentalBillingRuns(
        string? json,
        out string validationError)
    {
        const int maxIncomingRuns = 512;
        validationError = string.Empty;
        var strictValidation = RentalBillingRunTombstonePolicy.ValidateForServerMutation(json);
        if (!strictValidation.IsValid)
        {
            var validationDetail = string.IsNullOrWhiteSpace(strictValidation.Error)
                ? "The payload contains invalid JSON or tombstone metadata."
                : strictValidation.Error;
            validationError = $"Rental billing run history is invalid: {validationDetail}";
            return false;
        }

        if (!TryParseRentalBillingRunObjects(json, out var runs))
        {
            validationError = "Rental billing run history must be a valid JSON array of run objects.";
            return false;
        }

        if (runs.Count > maxIncomingRuns)
        {
            validationError = $"Rental billing run history exceeds the safe limit of {maxIncomingRuns} runs.";
            return false;
        }

        var runsById = new Dictionary<Guid, JsonObject>();
        var runIdsByNormalizedKey = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            if (!TryValidateRentalBillingRunCoreFields(run, out validationError))
                return false;

            if (!TryGetRentalBillingRunIdentity(run, out var identity))
            {
                validationError = "Rental billing run history contains a run without a valid RunId or RunKey.";
                return false;
            }

            var hasRunId = TryGetRentalBillingRunId(run, out var runId);
            var normalizedRunKey = NormalizeRentalBillingRunKey(run);
            var isCompatibleTombstoneDuplicate = false;
            if (hasRunId && runsById.TryGetValue(runId, out var previousRun))
            {
                isCompatibleTombstoneDuplicate =
                    IsCompatibleRentalBillingRunTombstoneDuplicate(
                        previousRun,
                        run,
                        runId,
                        normalizedRunKey);
                if (!isCompatibleTombstoneDuplicate)
                {
                    validationError = "Rental billing run history contains a duplicate RunId or RunKey.";
                    return false;
                }
            }
            else if (hasRunId)
            {
                runsById[runId] = run;
            }

            if (!string.IsNullOrWhiteSpace(normalizedRunKey) &&
                runIdsByNormalizedKey.TryGetValue(normalizedRunKey, out var existingRunId))
            {
                if (!isCompatibleTombstoneDuplicate || existingRunId != runId)
                {
                    validationError = "Rental billing run history contains a duplicate RunId or RunKey.";
                    return false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(normalizedRunKey))
            {
                runIdsByNormalizedKey[normalizedRunKey] = runId;
            }
        }

        return true;
    }

    private static bool IsCompatibleRentalBillingRunTombstoneDuplicate(
        JsonObject previousRun,
        JsonObject currentRun,
        Guid runId,
        string normalizedRunKey)
        => runId != Guid.Empty &&
           IsRentalBillingRunTombstoneRequested(previousRun) &&
           IsRentalBillingRunTombstoneRequested(currentRun) &&
           TryGetRentalBillingRunId(previousRun, out var previousRunId) &&
           previousRunId == runId &&
           string.Equals(
               NormalizeRentalBillingRunKey(previousRun),
               normalizedRunKey,
               StringComparison.Ordinal);

    private static bool TryValidateRentalBillingRunCoreFields(
        JsonObject run,
        out string validationError)
    {
        validationError = string.Empty;
        var seenCoreProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in run)
        {
            if (!RentalBillingRunCorePropertyNames.Contains(property.Key))
                continue;
            if (seenCoreProperties.Add(property.Key))
                continue;

            validationError =
                $"Rental billing run history contains a duplicate core property '{property.Key}' with ambiguous casing.";
            return false;
        }

        var hasValidRunId = TryGetRentalBillingRunId(run, out _);
        if (TryGetRentalBillingRunProperty(run, "RunId", out var runIdNode) &&
            !hasValidRunId)
        {
            validationError = "Rental billing run history contains an invalid RunId.";
            return false;
        }

        if (!TryValidateRentalBillingRunOptionalString(
                run,
                "RunKey",
                allowNull: hasValidRunId,
                allowBlank: hasValidRunId) ||
            !TryValidateRentalBillingRunOptionalString(run, "PeriodLabel", allowNull: false, allowBlank: true) ||
            !TryValidateRentalBillingRunOptionalString(run, "Status", allowNull: false, allowBlank: true) ||
            !TryValidateRentalBillingRunOptionalString(run, "SettlementStatus", allowNull: false, allowBlank: false) ||
            !TryValidateRentalBillingRunOptionalString(run, "Note", allowNull: true, allowBlank: true))
        {
            validationError = "Rental billing run history contains a core text field with an invalid type or value.";
            return false;
        }

        if (!TryValidateRentalBillingRunKnownStatus(
                run,
                "Status",
                RentalBillingRunTombstonePolicy.IsValidRunStatus) ||
            !TryValidateRentalBillingRunKnownStatus(
                run,
                "SettlementStatus",
                RentalBillingRunTombstonePolicy.IsValidRunSettlementStatus))
        {
            validationError = "Rental billing run history contains a core status field with an invalid value.";
            return false;
        }

        if (!TryGetRentalBillingRunDate(run, "ScheduledDate", allowNull: false, out _) ||
            !TryGetRentalBillingRunDate(run, "PeriodStartDate", allowNull: false, out var periodStartDate) ||
            !TryGetRentalBillingRunDate(run, "PeriodEndDate", allowNull: false, out var periodEndDate) ||
            !TryGetRentalBillingRunDate(run, "SettledDate", allowNull: true, out _))
        {
            validationError = "Rental billing run history contains a core date field with an invalid type or value.";
            return false;
        }

        if (periodStartDate.HasValue &&
            periodEndDate.HasValue &&
            periodStartDate.Value > periodEndDate.Value)
        {
            validationError = "Rental billing run history contains an invalid billing period date range.";
            return false;
        }

        if (!TryValidateRentalBillingRunNonNegativeAmount(run, "BilledAmount") ||
            !TryValidateRentalBillingRunNonNegativeAmount(run, "SettledAmount"))
        {
            validationError = "Rental billing run history contains a core amount field with an invalid type or value.";
            return false;
        }

        if (TryGetRentalBillingRunProperty(run, "CycleMonths", out var cycleMonthsNode) &&
            (cycleMonthsNode is not JsonValue cycleMonthsValue ||
             !cycleMonthsValue.TryGetValue<int>(out var cycleMonths) ||
             cycleMonths is < 1 or > 1200))
        {
            validationError = "Rental billing run history contains an invalid CycleMonths value.";
            return false;
        }

        if (!TryValidateRentalBillingRunTombstoneMarker(run, out validationError))
            return false;

        if (!TryValidateRentalBillingRunItems(run))
        {
            validationError = "Rental billing run history contains malformed Items data.";
            return false;
        }

        return true;
    }

    private static bool TryValidateRentalBillingRunTombstoneMarker(
        JsonObject run,
        out string validationError)
    {
        validationError = string.Empty;
        var hasIsTombstoned = TryGetRentalBillingRunProperty(
            run,
            RentalBillingRunTombstonePolicy.IsTombstonedPropertyName,
            out var isTombstonedNode);
        var hasTombstonedAtUtc = TryGetRentalBillingRunProperty(
            run,
            RentalBillingRunTombstonePolicy.TombstonedAtUtcPropertyName,
            out var tombstonedAtUtcNode);
        var hasTombstonedByUsername = TryGetRentalBillingRunProperty(
            run,
            RentalBillingRunTombstonePolicy.TombstonedByUsernamePropertyName,
            out var tombstonedByUsernameNode);

        if (!hasIsTombstoned && !hasTombstonedAtUtc && !hasTombstonedByUsername)
            return true;

        if (!hasIsTombstoned || !hasTombstonedAtUtc || !hasTombstonedByUsername ||
            isTombstonedNode is not JsonValue isTombstonedValue ||
            !isTombstonedValue.TryGetValue<bool>(out var isTombstoned) ||
            tombstonedByUsernameNode is not JsonValue tombstonedByUsernameValue ||
            !tombstonedByUsernameValue.TryGetValue<string>(out var tombstonedByUsername))
        {
            validationError =
                "Rental billing run history contains an incomplete tombstone marker or an invalid marker field type.";
            return false;
        }

        DateTime? tombstonedAtUtc = null;
        if (tombstonedAtUtcNode is not null)
        {
            if (tombstonedAtUtcNode is not JsonValue tombstonedAtUtcValue ||
                !tombstonedAtUtcValue.TryGetValue<string>(out var timestampText) ||
                !DateTime.TryParse(
                    timestampText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedTimestamp) ||
                parsedTimestamp.Kind != DateTimeKind.Utc)
            {
                validationError =
                    "Rental billing run history contains a tombstone timestamp that is not a UTC DateTime string.";
                return false;
            }

            tombstonedAtUtc = parsedTimestamp;
        }

        if (isTombstoned)
        {
            if (!TryGetRentalBillingRunId(run, out _) ||
                !tombstonedAtUtc.HasValue ||
                string.IsNullOrWhiteSpace(tombstonedByUsername))
            {
                validationError =
                    "A tombstoned rental billing run requires RunId, UTC timestamp, and username metadata.";
                return false;
            }

            return true;
        }

        if (tombstonedAtUtc.HasValue || !string.IsNullOrWhiteSpace(tombstonedByUsername))
        {
            validationError =
                "An active rental billing run cannot retain tombstone metadata.";
            return false;
        }

        return true;
    }

    private static bool TryGetNonEmptyRentalBillingRunGuid(JsonNode? node, out Guid value)
    {
        value = Guid.Empty;
        if (node is not JsonValue jsonValue)
            return false;
        if (jsonValue.TryGetValue<Guid>(out value))
            return value != Guid.Empty;
        return jsonValue.TryGetValue<string>(out var text) &&
               Guid.TryParse(text, out value) &&
               value != Guid.Empty;
    }

    private static bool TryValidateRentalBillingRunOptionalString(
        JsonObject run,
        string propertyName,
        bool allowNull,
        bool allowBlank)
    {
        if (!TryGetRentalBillingRunProperty(run, propertyName, out var node))
            return true;
        if (node is null)
            return allowNull;
        return node is JsonValue value &&
               value.TryGetValue<string>(out var text) &&
               (allowBlank || !string.IsNullOrWhiteSpace(text));
    }

    private static bool TryValidateRentalBillingRunKnownStatus(
        JsonObject run,
        string propertyName,
        Func<string?, bool> validator)
    {
        if (!TryGetRentalBillingRunProperty(run, propertyName, out var node))
            return true;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            return false;

        return validator(text);
    }

    private static bool TryValidateRentalBillingRunItems(JsonObject run)
    {
        if (!TryGetRentalBillingRunProperty(run, "Items", out var itemsNode))
            return true;
        if (itemsNode is not JsonArray items)
            return false;

        foreach (var itemNode in items)
        {
            if (itemNode is not JsonObject item)
                return false;

            var seenKnownProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in item)
            {
                if (RentalBillingRunItemKnownPropertyNames.Contains(property.Key) &&
                    !seenKnownProperties.Add(property.Key))
                {
                    return false;
                }
            }

            if (!TryValidateRentalBillingRunItemGuid(item, "ItemId", allowNull: false) ||
                !TryValidateRentalBillingRunItemGuid(item, "CatalogItemId", allowNull: true) ||
                !TryValidateRentalBillingRunItemGuid(item, "RepresentativeAssetId", allowNull: true))
            {
                return false;
            }

            foreach (var propertyName in new[]
                     {
                         "DisplayItemName",
                         "BillingLineMode",
                         "IndividualGroupingMode",
                         "Specification",
                         "Unit",
                         "MaterialNumber",
                         "Note"
                     })
            {
                if (!TryGetRentalBillingRunProperty(item, propertyName, out var valueNode))
                    continue;
                if (valueNode is not null &&
                    (valueNode is not JsonValue value || !value.TryGetValue<string>(out _)))
                {
                    return false;
                }
            }

            if (!TryValidateRentalBillingRunNonNegativeAmount(item, "Quantity") ||
                !TryValidateRentalBillingRunNonNegativeAmount(item, "UnitPrice") ||
                !TryValidateRentalBillingRunNonNegativeAmount(item, "Amount"))
            {
                return false;
            }

            if (!TryGetRentalBillingRunProperty(item, "IncludedAssetIds", out var includedAssetIdsNode))
                continue;
            if (includedAssetIdsNode is not JsonArray includedAssetIds ||
                includedAssetIds.Any(assetIdNode =>
                    !TryGetNonEmptyRentalBillingRunGuid(assetIdNode, out _)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateRentalBillingRunItemGuid(
        JsonObject item,
        string propertyName,
        bool allowNull)
    {
        if (!TryGetRentalBillingRunProperty(item, propertyName, out var node))
            return true;
        if (node is null)
            return allowNull;
        return TryGetNonEmptyRentalBillingRunGuid(node, out _);
    }

    private static bool TryGetRentalBillingRunDate(
        JsonObject run,
        string propertyName,
        bool allowNull,
        out DateOnly? parsedDate)
    {
        parsedDate = null;
        if (!TryGetRentalBillingRunProperty(run, propertyName, out var node))
            return true;
        if (node is null)
            return allowNull;
        if (node is not JsonValue value ||
            !value.TryGetValue<string>(out var text) ||
            !DateOnly.TryParseExact(
                text,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return false;
        }

        parsedDate = date;
        return true;
    }

    private static bool TryValidateRentalBillingRunNonNegativeAmount(
        JsonObject run,
        string propertyName)
    {
        if (!TryGetRentalBillingRunProperty(run, propertyName, out var node))
            return true;
        return node is JsonValue value &&
               value.TryGetValue<decimal>(out var amount) &&
               amount >= 0m;
    }

    private static bool TryValidateProjectedRentalBillingRunLimit(
        string? existingJson,
        string? incomingJson,
        out string validationError)
    {
        const int maxMergedDistinctRuns = 512;
        validationError = string.Empty;
        if (!TryParseRentalBillingRunObjects(existingJson, out var existingRuns) ||
            !TryParseRentalBillingRunObjects(incomingJson, out var incomingRuns))
        {
            return true;
        }

        var projectedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectedRunIdsByNormalizedKey =
            new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);
        var projectedNormalizedRunKeysByRunId = new Dictionary<Guid, string>();
        var existingPhysicalRowByRunId = new Dictionary<Guid, int>();
        var existingPhysicalRowByNormalizedRunKey =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unidentifiedExistingRuns = 0;
        for (var existingIndex = 0; existingIndex < existingRuns.Count; existingIndex++)
        {
            var existingRun = existingRuns[existingIndex];
            if ((TryGetRentalBillingRunIdentity(existingRun, out var existingIdentity) &&
                 !existingIdentities.Add(existingIdentity)) ||
                !TryAddRentalBillingRunIdentityMapping(
                    existingRun,
                    projectedRunIdsByNormalizedKey,
                    projectedNormalizedRunKeysByRunId,
                    allowIdentityEnrichment: false))
            {
                validationError = "Rental billing run history contains a duplicate RunId or RunKey.";
                return false;
            }

            if (TryGetRentalBillingRunId(existingRun, out var existingRunId))
                existingPhysicalRowByRunId.TryAdd(existingRunId, existingIndex);
            var existingRunKey = NormalizeRentalBillingRunKey(existingRun);
            if (!string.IsNullOrWhiteSpace(existingRunKey))
            {
                existingPhysicalRowByNormalizedRunKey.TryAdd(
                    existingRunKey,
                    existingIndex);
            }

            if (!string.IsNullOrEmpty(existingIdentity))
                projectedIdentities.Add(existingIdentity);
            else
                unidentifiedExistingRuns++;
        }

        if (incomingRuns.Count == 0)
            return true;

        var projectedDistinctCount = projectedIdentities.Count + unidentifiedExistingRuns;
        var addsNewIdentity = false;
        foreach (var incomingRun in incomingRuns)
        {
            var incomingRunKey = NormalizeRentalBillingRunKey(incomingRun);
            if (TryGetRentalBillingRunId(incomingRun, out var incomingRunId) &&
                !string.IsNullOrWhiteSpace(incomingRunKey) &&
                existingPhysicalRowByRunId.TryGetValue(incomingRunId, out var runIdRowIndex) &&
                existingPhysicalRowByNormalizedRunKey.TryGetValue(
                    incomingRunKey,
                    out var runKeyRowIndex) &&
                runIdRowIndex != runKeyRowIndex)
            {
                validationError = "Rental billing run history contains a duplicate RunId or RunKey.";
                return false;
            }

            var matchesProjectedIdentity = MatchesRentalBillingRunIdentityMapping(
                incomingRun,
                projectedRunIdsByNormalizedKey,
                projectedNormalizedRunKeysByRunId);
            if (!TryAddRentalBillingRunIdentityMapping(
                    incomingRun,
                    projectedRunIdsByNormalizedKey,
                    projectedNormalizedRunKeysByRunId,
                    allowIdentityEnrichment: true))
            {
                validationError = "Rental billing run history contains a duplicate RunId or RunKey.";
                return false;
            }

            if (matchesProjectedIdentity)
                continue;
            if (!TryGetRentalBillingRunIdentity(incomingRun, out var identity) ||
                !projectedIdentities.Add(identity))
            {
                continue;
            }

            addsNewIdentity = true;
            projectedDistinctCount++;
        }

        if (!addsNewIdentity || projectedDistinctCount <= maxMergedDistinctRuns)
            return true;

        validationError =
            $"Rental billing run history exceeds the safe cumulative limit of {maxMergedDistinctRuns} distinct runs.";
        return false;
    }

    private static bool TryAddRentalBillingRunIdentityMapping(
        JsonObject run,
        Dictionary<string, Guid?> runIdsByNormalizedKey,
        Dictionary<Guid, string> normalizedRunKeysByRunId,
        bool allowIdentityEnrichment)
    {
        var normalizedRunKey = NormalizeRentalBillingRunKey(run);
        if (string.IsNullOrWhiteSpace(normalizedRunKey))
            return true;

        var hasRunId = TryGetRentalBillingRunId(run, out var parsedRunId);
        Guid? runId = hasRunId ? parsedRunId : null;
        if (!runIdsByNormalizedKey.TryGetValue(normalizedRunKey, out var existingRunId))
            runIdsByNormalizedKey.Add(normalizedRunKey, runId);
        else if (existingRunId != runId)
        {
            if (!allowIdentityEnrichment ||
                (existingRunId.HasValue && runId.HasValue))
            {
                return false;
            }

            if (!existingRunId.HasValue && runId.HasValue)
                runIdsByNormalizedKey[normalizedRunKey] = runId;
        }

        if (!hasRunId)
            return true;
        if (!normalizedRunKeysByRunId.TryGetValue(parsedRunId, out var existingRunKey))
        {
            normalizedRunKeysByRunId.Add(parsedRunId, normalizedRunKey);
            return true;
        }

        return string.Equals(
            existingRunKey,
            normalizedRunKey,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRentalBillingRunIdentityMapping(
        JsonObject run,
        IReadOnlyDictionary<string, Guid?> runIdsByNormalizedKey,
        IReadOnlyDictionary<Guid, string> normalizedRunKeysByRunId)
    {
        if (TryGetRentalBillingRunId(run, out var runId) &&
            normalizedRunKeysByRunId.ContainsKey(runId))
        {
            return true;
        }

        var normalizedRunKey = NormalizeRentalBillingRunKey(run);
        if (string.IsNullOrWhiteSpace(normalizedRunKey) ||
            !runIdsByNormalizedKey.TryGetValue(normalizedRunKey, out var existingRunId))
        {
            return false;
        }

        return !TryGetRentalBillingRunId(run, out runId) ||
               !existingRunId.HasValue ||
               existingRunId.Value == runId;
    }

    private static bool TryGetRentalBillingRunId(JsonObject run, out Guid runId)
    {
        runId = Guid.Empty;
        return TryGetRentalBillingRunProperty(run, "RunId", out var runIdNode) &&
               TryGetNonEmptyRentalBillingRunGuid(runIdNode, out runId);
    }

    private static bool TryGetRentalBillingRunIdentity(
        JsonObject run,
        out string identity)
    {
        identity = string.Empty;
        if (TryGetRentalBillingRunId(run, out var runId))
        {
            identity = $"RUN:{runId:D}";
            return true;
        }

        var runKey = NormalizeRentalBillingRunKey(run);
        if (string.IsNullOrWhiteSpace(runKey))
            return false;

        identity = $"RUNKEY:{runKey}";
        return true;
    }

    private static string GetRentalBillingRunString(JsonObject run, string propertyName)
    {
        if (!TryGetRentalBillingRunProperty(run, propertyName, out var node) ||
            node is not JsonValue value ||
            !value.TryGetValue<string>(out var text))
        {
            return string.Empty;
        }

        return text ?? string.Empty;
    }

    private static string NormalizeRentalBillingRunKey(JsonObject run)
        => RentalDuplicateNormalizer.NormalizeProfileKeyPart(
            GetRentalBillingRunString(run, "RunKey"));

    private static bool TryGetRentalBillingRunProperty(
        JsonObject run,
        string propertyName,
        out JsonNode? value)
    {
        foreach (var property in run)
        {
            if (!string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            return true;
        }

        value = null;
        return false;
    }

    private static void SetRentalBillingRunProperty(
        JsonObject run,
        string propertyName,
        JsonNode? value)
    {
        var existingPropertyName = run
            .Select(property => property.Key)
            .FirstOrDefault(name => string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase));
        run[existingPropertyName ?? propertyName] = value;
    }

    private async Task<List<RentalBillingProfileDto>> FilterRentalBillingProfilesWithSafeProjectedAssetCoverageAsync(
        IEnumerable<RentalBillingProfileDto> profiles,
        IReadOnlyCollection<RentalAssetDto> rentalAssets,
        SyncPushResult result,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        const string projectedCoverageConflictMessage =
            "청구 프로필의 명시적 자산 구성을 변경할 수 없습니다. 연결 해제할 렌탈 자산이 같은 동기화에서 안전하게 저장되지 않습니다. 청구관리에서 새로고침 후 다시 시도하세요.";
        var assetMutationsById = rentalAssets
            .Where(asset => asset.Id != Guid.Empty)
            .GroupBy(asset => asset.Id)
            .ToDictionary(group => group.Key, group => group.ToList());
        var valid = new List<RentalBillingProfileDto>();

        foreach (var dto in profiles)
        {
            var templateCoverage = RentalBillingTemplateAssetCoverageRules.Evaluate(
                dto.BillingTemplateJson,
                Guid.Empty);
            if (!dto.IsDeleted && templateCoverage == RentalBillingTemplateAssetCoverage.MalformedTemplate)
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    "청구 프로필의 자산 구성 JSON이 올바르지 않습니다. 청구관리에서 구성을 확인한 뒤 다시 동기화하세요.",
                    result);
                continue;
            }

            var existing = pushSnapshot is null
                ? await _dbContext.RentalBillingProfiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(profile => profile.Id == dto.Id, cancellationToken)
                : pushSnapshot.FindProfile(dto.Id);
            existing ??= await FindExistingRentalBillingProfileByNaturalKeyAsync(
                dto,
                dto.TenantCode,
                cancellationToken,
                pushSnapshot);
            if (existing is null || existing.IsDeleted)
            {
                valid.Add(dto);
                continue;
            }

            var existingTemplateCoverage = RentalBillingTemplateAssetCoverageRules.Evaluate(
                existing.BillingTemplateJson,
                Guid.Empty);
            if (!dto.IsDeleted &&
                templateCoverage == RentalBillingTemplateAssetCoverage.NoExplicitCoverage &&
                existingTemplateCoverage == RentalBillingTemplateAssetCoverage.NoExplicitCoverage)
            {
                valid.Add(dto);
                continue;
            }

            var linkedAssets = pushSnapshot is null
                ? await _dbContext.RentalAssets
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(asset => !asset.IsDeleted && asset.BillingProfileId == existing.Id)
                    .ToListAsync(cancellationToken)
                : pushSnapshot.GetActiveLinkedAssets(existing.Id);
            var hasUnsafeOmission = false;
            foreach (var linkedAsset in linkedAssets)
            {
                if (!dto.IsDeleted &&
                    RentalBillingTemplateAssetCoverageRules.Evaluate(
                        dto.BillingTemplateJson,
                        linkedAsset.Id) == RentalBillingTemplateAssetCoverage.UniqueReference)
                {
                    continue;
                }

                if (!assetMutationsById.TryGetValue(linkedAsset.Id, out var matchingMutations) ||
                    matchingMutations.Count != 1)
                {
                    hasUnsafeOmission = true;
                    break;
                }

                var assetMutation = matchingMutations[0];
                var safelyRequestsUnlink = assetMutation.IsDeleted ||
                    (!assetMutation.BillingProfileId.HasValue ||
                     assetMutation.BillingProfileId.Value == Guid.Empty);
                var preservesServerReferences =
                    assetMutation.CustomerId == linkedAsset.CustomerId &&
                    assetMutation.ItemId == linkedAsset.ItemId &&
                    string.Equals(assetMutation.TenantCode, linkedAsset.TenantCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(assetMutation.OfficeCode, linkedAsset.OfficeCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(assetMutation.ResponsibleOfficeCode, linkedAsset.ResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(assetMutation.ManagementCompanyCode, linkedAsset.ManagementCompanyCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(assetMutation.ManagementId, linkedAsset.ManagementId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(assetMutation.ManagementNumber, linkedAsset.ManagementNumber, StringComparison.OrdinalIgnoreCase);
                var normalizedAssetMutationId = NormalizeMutationId(assetMutation.MutationId);
                var hasUniqueMutationId = string.IsNullOrWhiteSpace(normalizedAssetMutationId) ||
                    rentalAssets.Count(candidate => string.Equals(
                        NormalizeMutationId(candidate.MutationId),
                        normalizedAssetMutationId,
                        StringComparison.Ordinal)) == 1;
                var usesReservedMutationId = ItemWarehouseStockMutationReceipt.IsReservedMutationId(
                    normalizedAssetMutationId);
                var reusesProcessedMutation = !string.IsNullOrWhiteSpace(normalizedAssetMutationId) &&
                    _processedMutationsById.ContainsKey(normalizedAssetMutationId);
                var hasValidCustomerReference = true;
                if (linkedAsset.CustomerId is Guid linkedCustomerId && linkedCustomerId != Guid.Empty)
                {
                    var linkedCustomer = pushSnapshot is null
                        ? await _dbContext.Customers
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .FirstOrDefaultAsync(customer => customer.Id == linkedCustomerId, cancellationToken)
                        : pushSnapshot.FindCustomer(linkedCustomerId);
                    hasValidCustomerReference = linkedCustomer is not null &&
                        !linkedCustomer.IsDeleted &&
                        CanReadCustomerForRentalReference(linkedCustomer);
                }
                if (!safelyRequestsUnlink ||
                    !preservesServerReferences ||
                    !hasUniqueMutationId ||
                    usesReservedMutationId ||
                    reusesProcessedMutation ||
                    !hasValidCustomerReference ||
                    !_officeScopeService.CanWriteOfficeForRentals(
                        linkedAsset.ResponsibleOfficeCode,
                        linkedAsset.TenantCode,
                        linkedAsset.OfficeCode) ||
                    HasExpectedRevisionConflict(linkedAsset, assetMutation) ||
                    IsServerEntityNewer(linkedAsset, assetMutation))
                {
                    hasUnsafeOmission = true;
                    break;
                }
            }

            if (hasUnsafeOmission)
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    projectedCoverageConflictMessage,
                    result);
                continue;
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<RentalBillingProfileDto>> FilterRentalBillingProfilesWithValidIncludedAssetReferencesAsync(
        IEnumerable<RentalBillingProfileDto> profiles,
        IReadOnlyCollection<RentalAssetDto> projectedRentalAssets,
        IReadOnlyDictionary<RentalBillingProfileDto, Guid> originalProfileIds,
        SyncPushResult result,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        const string unavailableAssetConflictMessage =
            "청구 프로필의 자산 구성에 삭제되었거나 더 이상 존재하지 않는 자산이 포함되어 있습니다. 청구관리에서 자산 목록을 새로고침한 뒤 다시 저장하세요.";
        const string duplicateAssetConflictMessage =
            "청구 프로필의 자산 구성에서 동일한 자산이 여러 번 참조됩니다. 자산은 하나의 청구 항목에 한 번만 포함한 뒤 다시 저장하세요.";
        var candidates = new List<(
            RentalBillingProfileDto Dto,
            HashSet<Guid> IncludedAssetIds,
            HashSet<Guid> ExistingIncludedAssetIds)>();
        var allIncludedAssetIds = new HashSet<Guid>();

        foreach (var dto in profiles)
        {
            if (dto.IsDeleted)
            {
                candidates.Add((dto, [], []));
                continue;
            }

            if (!RentalBillingTemplateAssetCoverageRules.TryGetExplicitIncludedAssetIds(
                    dto.BillingTemplateJson,
                    out _,
                    out var incomingHasDuplicateReferences))
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    unavailableAssetConflictMessage,
                    result);
                continue;
            }

            if (incomingHasDuplicateReferences)
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    duplicateAssetConflictMessage,
                    result);
                continue;
            }

            var originalProfileId = GetOriginalRentalBillingProfileId(dto, originalProfileIds);
            var existing = dto.Id == Guid.Empty
                ? null
                : pushSnapshot is null
                    ? await _dbContext.RentalBillingProfiles
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(profile => profile.Id == dto.Id, cancellationToken)
                    : pushSnapshot.FindProfile(dto.Id);
            existing ??= await FindExistingRentalBillingProfileByNaturalKeyAsync(
                dto,
                dto.TenantCode,
                cancellationToken,
                pushSnapshot);

            var effectiveTemplateJson = dto.BillingTemplateJson;
            if (existing is not null &&
                IsIncomingRentalBillingProfileIdReusedByNaturalKey(
                    originalProfileId,
                    existing.Id,
                    dto.ProfileKey))
            {
                if (!RentalBillingTemplateAssetCoverageRules.TryGetExplicitIncludedAssetIds(
                        existing.BillingTemplateJson,
                        out _,
                        out var existingHasDuplicateReferences))
                {
                    AddClientConflict(
                        dto,
                        nameof(RentalBillingProfile),
                        unavailableAssetConflictMessage,
                        result);
                    continue;
                }

                if (existingHasDuplicateReferences)
                {
                    AddClientConflict(
                        dto,
                        nameof(RentalBillingProfile),
                        duplicateAssetConflictMessage,
                        result);
                    continue;
                }

                effectiveTemplateJson = RentalDuplicateNormalizer.MergeBillingTemplateJson(
                    existing.BillingTemplateJson,
                    dto.BillingTemplateJson);
            }

            if (!RentalBillingTemplateAssetCoverageRules.TryGetExplicitIncludedAssetIds(
                    effectiveTemplateJson,
                    out var includedAssetIds,
                    out var hasDuplicateReferences))
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    unavailableAssetConflictMessage,
                    result);
                continue;
            }

            if (hasDuplicateReferences)
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    duplicateAssetConflictMessage,
                    result);
                continue;
            }

            var includedAssetIdSet = includedAssetIds.ToHashSet();
            var existingIncludedAssetIdSet = new HashSet<Guid>();
            if (existing is not null &&
                RentalBillingTemplateAssetCoverageRules.TryGetExplicitIncludedAssetIds(
                    existing.BillingTemplateJson,
                    out var existingIncludedAssetIds,
                    out _))
            {
                existingIncludedAssetIdSet.UnionWith(existingIncludedAssetIds);
            }

            candidates.Add((dto, includedAssetIdSet, existingIncludedAssetIdSet));
            allIncludedAssetIds.UnionWith(includedAssetIdSet);
        }

        var activeAssetsById = new Dictionary<Guid, RentalAsset>();
        foreach (var assetIdBatch in allIncludedAssetIds.Chunk(500))
        {
            var batch = assetIdBatch.ToArray();
            var assets = await _dbContext.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => batch.Contains(asset.Id) && !asset.IsDeleted)
                .ToListAsync(cancellationToken);
            foreach (var asset in assets)
                activeAssetsById[asset.Id] = asset;
        }

        var projectedAssetsById = projectedRentalAssets
            .Where(asset => asset.Id != Guid.Empty)
            .GroupBy(asset => asset.Id)
            .ToDictionary(group => group.Key, group => group.ToList());
        var valid = new List<RentalBillingProfileDto>();
        foreach (var candidate in candidates)
        {
            var referencesUnavailableAsset = false;
            foreach (var includedAssetId in candidate.IncludedAssetIds)
            {
                if (activeAssetsById.TryGetValue(includedAssetId, out var activeAsset))
                {
                    var isNewReference = !candidate.ExistingIncludedAssetIds.Contains(includedAssetId);
                    if (!isNewReference ||
                        _officeScopeService.CanReadOfficeForRentals(
                            activeAsset.ResponsibleOfficeCode,
                            activeAsset.TenantCode,
                            activeAsset.OfficeCode))
                    {
                        continue;
                    }
                }
                else if (projectedAssetsById.TryGetValue(includedAssetId, out var projectedAssets) &&
                         projectedAssets.Count == 1 &&
                         await CanProjectActiveRentalAssetReferenceAsync(
                             projectedAssets[0],
                             candidate.Dto.Id,
                             GetOriginalRentalBillingProfileId(candidate.Dto, originalProfileIds),
                             cancellationToken,
                             pushSnapshot))
                {
                    continue;
                }

                referencesUnavailableAsset = true;
                break;
            }

            if (referencesUnavailableAsset)
            {
                AddClientConflict(
                    candidate.Dto,
                    nameof(RentalBillingProfile),
                    unavailableAssetConflictMessage,
                    result);
                continue;
            }

            valid.Add(candidate.Dto);
        }

        return valid;
    }

    private async Task<bool> CanProjectActiveRentalAssetReferenceAsync(
        RentalAssetDto projectedAsset,
        Guid canonicalProfileId,
        Guid originalProfileId,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        if (projectedAsset.Id == Guid.Empty || projectedAsset.IsDeleted)
            return false;

        if (projectedAsset.BillingProfileId is Guid projectedProfileId &&
            projectedProfileId != Guid.Empty &&
            projectedProfileId != canonicalProfileId &&
            projectedProfileId != originalProfileId)
        {
            return false;
        }

        var normalizedMutationId = NormalizeMutationId(projectedAsset.MutationId);
        if (ItemWarehouseStockMutationReceipt.IsReservedMutationId(normalizedMutationId) ||
            (!string.IsNullOrWhiteSpace(normalizedMutationId) &&
             _processedMutationsById.ContainsKey(normalizedMutationId)))
        {
            return false;
        }

        var existing = pushSnapshot is null
            ? await _dbContext.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(asset => asset.Id == projectedAsset.Id, cancellationToken)
            : pushSnapshot.FindAsset(projectedAsset.Id);
        if (existing is not null)
        {
            if (!existing.IsDeleted ||
                !_officeScopeService.CanWriteOfficeForRentals(
                    existing.ResponsibleOfficeCode,
                    existing.TenantCode,
                    existing.OfficeCode) ||
                HasExpectedRevisionConflict(existing, projectedAsset) ||
                IsServerEntityNewer(existing, projectedAsset) ||
                (pushSnapshot is null
                    ? await FindActiveRentalAssetRestoreConflictAsync(existing, cancellationToken)
                    : pushSnapshot.FindActiveAssetRestoreConflict(existing)) is not null)
            {
                return false;
            }
        }
        else
        {
            var naturalKeyMatch = pushSnapshot is null
                ? await FindExistingRentalAssetByNaturalKeyAsync(projectedAsset, cancellationToken)
                : pushSnapshot.FindAssetByNaturalKey(projectedAsset);
            if (naturalKeyMatch is not null && naturalKeyMatch.Id != projectedAsset.Id)
                return false;

            var requestedResponsibleOfficeCode =
                TenantScopeCatalog.TryNormalizeTenantCode(
                    projectedAsset.TenantCode,
                    out var requestedTenantCodeForResponsible) &&
                string.Equals(
                    requestedTenantCodeForResponsible,
                    TenantScopeCatalog.Itworld,
                    StringComparison.OrdinalIgnoreCase)
                    ? OfficeCodeCatalog.Itworld
                    : projectedAsset.ResponsibleOfficeCode;
            var responsibleOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(
                requestedResponsibleOfficeCode,
                projectedAsset.OfficeCode);
            var officeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                projectedAsset.OfficeCode,
                responsibleOfficeCode,
                projectedAsset.OfficeCode);
            var tenantCode = _officeScopeService.ResolveTenantForRentalCreate(
                projectedAsset.TenantCode,
                officeCode,
                projectedAsset.TenantCode,
                projectedAsset.OfficeCode);
            if (!_officeScopeService.CanWriteOfficeForRentals(
                    responsibleOfficeCode,
                    tenantCode,
                    officeCode))
            {
                return false;
            }
        }

        if (projectedAsset.CustomerId is Guid customerId && customerId != Guid.Empty)
        {
            var customer = pushSnapshot is null
                ? await _dbContext.Customers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(current => current.Id == customerId, cancellationToken)
                : pushSnapshot.FindCustomer(customerId);
            if (customer is null ||
                customer.IsDeleted ||
                !CanReadCustomerForRentalReference(customer))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<List<Guid>> FindAcceptedRentalBillingProfilesWithUnavailableTemplateAssetsAsync(
        IReadOnlyCollection<RentalBillingProfileDto> acceptedProfiles,
        CancellationToken cancellationToken)
    {
        var acceptedActiveProfileIds = acceptedProfiles
            .Where(profile => profile.Id != Guid.Empty && !profile.IsDeleted && profile.IsActive)
            .Select(profile => profile.Id)
            .Distinct()
            .ToList();
        if (acceptedActiveProfileIds.Count == 0)
            return [];

        var storedProfiles = new List<RentalBillingProfile>();
        foreach (var profileIdBatch in acceptedActiveProfileIds.Chunk(500))
        {
            var batch = profileIdBatch.ToArray();
            storedProfiles.AddRange(await _dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => batch.Contains(profile.Id) && !profile.IsDeleted && profile.IsActive)
                .ToListAsync(cancellationToken));
        }

        var includedAssetIdsByProfileId = new Dictionary<Guid, HashSet<Guid>>();
        var allIncludedAssetIds = new HashSet<Guid>();
        var malformedProfileIds = new HashSet<Guid>();
        foreach (var profile in storedProfiles)
        {
            if (!RentalBillingTemplateAssetCoverageRules.TryGetExplicitIncludedAssetIds(
                    profile.BillingTemplateJson,
                    out var includedAssetIds,
                    out var hasDuplicateReferences) ||
                hasDuplicateReferences)
            {
                malformedProfileIds.Add(profile.Id);
                continue;
            }

            var includedAssetIdSet = includedAssetIds.ToHashSet();
            includedAssetIdsByProfileId[profile.Id] = includedAssetIdSet;
            allIncludedAssetIds.UnionWith(includedAssetIdSet);
        }

        var activeAssetIds = new HashSet<Guid>();
        foreach (var assetIdBatch in allIncludedAssetIds.Chunk(500))
        {
            var batch = assetIdBatch.ToArray();
            var existingIds = await _dbContext.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => batch.Contains(asset.Id) && !asset.IsDeleted)
                .Select(asset => asset.Id)
                .ToListAsync(cancellationToken);
            activeAssetIds.UnionWith(existingIds);
        }

        return storedProfiles
            .Where(profile =>
                malformedProfileIds.Contains(profile.Id) ||
                (includedAssetIdsByProfileId.TryGetValue(profile.Id, out var includedAssetIds) &&
                 includedAssetIds.Any(assetId => !activeAssetIds.Contains(assetId))))
            .Select(profile => profile.Id)
            .Distinct()
            .ToList();
    }

    private async Task<List<RentalBillingProfileDto>> FilterValidRentalBillingProfilesAsync(
        IEnumerable<RentalBillingProfileDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var valid = new List<RentalBillingProfileDto>();

        foreach (var dto in payload)
        {
            if (!TryValidateIncomingRentalBillingRuns(
                    dto.BillingRunsJson,
                    out var rentalBillingRunsValidationError))
            {
                AddClientConflict(
                    dto,
                    nameof(RentalBillingProfile),
                    rentalBillingRunsValidationError,
                    result);
                continue;
            }

            var requestedCustomerId = dto.CustomerId;
            dto.CustomerId = await ResolveRentalBillingProfileCustomerReferenceAsync(
                dto,
                cancellationToken,
                pushSnapshot);
            if (!await ValidateExplicitRentalCustomerReferenceAsync(
                    dto,
                    nameof(RentalBillingProfile),
                    requestedCustomerId,
                    dto.CustomerId,
                    result,
                    cancellationToken,
                    pushSnapshot))
            {
                continue;
            }

            var linkedCustomer = await GetRentalReferenceCustomerAsync(
                dto.CustomerId,
                cancellationToken,
                pushSnapshot);
            if (linkedCustomer is not null)
            {
                var resolvedResponsibleOfficeCode = ResolveRentalCustomerOfficeCode(linkedCustomer.ResponsibleOfficeCode);
                var resolvedOwnerOfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                    linkedCustomer.OfficeCode,
                    resolvedResponsibleOfficeCode,
                    linkedCustomer.OfficeCode);
                dto.TenantCode = _officeScopeService.ResolveTenantForRentalCreate(
                    dto.TenantCode,
                    resolvedOwnerOfficeCode,
                    linkedCustomer.TenantCode,
                    linkedCustomer.OfficeCode);
                dto.ResponsibleOfficeCode = resolvedResponsibleOfficeCode;
                dto.OfficeCode = resolvedOwnerOfficeCode;
                dto.ManagementCompanyCode = resolvedOwnerOfficeCode;
                var normalizedCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(linkedCustomer.NameOriginal);
                dto.CustomerName = normalizedCustomerName;
                dto.BusinessNumber = linkedCustomer.BusinessNumber?.Trim() ?? string.Empty;
                dto.Email = linkedCustomer.Email?.Trim() ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(dto.ManagementCompanyCode))
            {
                var managementCompanyCode = dto.ManagementCompanyCode.Trim();
                var exists = pushSnapshot is null
                    ? await _dbContext.RentalManagementCompanies.IgnoreQueryFilters()
                        .AnyAsync(x =>
                            x.TenantCode == dto.TenantCode &&
                            x.Code == managementCompanyCode &&
                            !x.IsDeleted,
                            cancellationToken)
                    : pushSnapshot.HasActiveManagementCompany(dto.TenantCode, managementCompanyCode);
                if (!exists)
                {
                    AddClientConflict(dto, nameof(RentalBillingProfile),
                        $"Referenced management company was not found: {dto.ManagementCompanyCode}.", result);
                    continue;
                }
            }

            if (!_officeScopeService.CanWriteOfficeForRentals(dto.ResponsibleOfficeCode, dto.TenantCode, dto.OfficeCode))
            {
                AddClientConflict(dto, nameof(RentalBillingProfile),
                    $"Rental billing profile resolves outside the writable office scope: {dto.ResponsibleOfficeCode}.", result);
                continue;
            }

            valid.Add(dto);
        }

        return valid;
    }

    private static bool IsServerEntityNewer(TrackedEntity entity, SyncEntityDto dto)
    {
        if (entity.Revision > 0 && dto.Revision > 0)
            return entity.Revision > dto.Revision;

        return NormalizeConflictUtc(entity.UpdatedAtUtc) > NormalizeConflictUtc(dto.UpdatedAtUtc);
    }

    private static bool HasExpectedRevisionConflict(TrackedEntity entity, SyncEntityDto dto)
        => dto.ExpectedRevision > 0 && entity.Revision != dto.ExpectedRevision;

    private static string BuildExpectedRevisionConflictReason(long expectedRevision, long currentRevision)
        => $"Expected revision mismatch. client={expectedRevision}, server={currentRevision}";

    private async Task<bool> TryAcceptAlreadyDeletedMutationAsync<TEntity>(
        TEntity entity,
        SyncEntityDto dto,
        string entityName,
        string deviceId,
        SyncPushResult result,
        CancellationToken cancellationToken,
        bool preserveOriginalIncomingPayloadHashForReceipt = false)
        where TEntity : TrackedEntity
    {
        if (!dto.IsDeleted || !entity.IsDeleted)
            return false;

        RegisterProcessedMutation(
            dto,
            entityName,
            deviceId,
            preserveOriginalIncomingPayloadHashForReceipt);
        await ResolveHistoricalConflictsAsync(
            entityName,
            entity.Id,
            "The requested entity is already deleted, so the delete mutation was accepted without another state change.",
            cancellationToken);
        result.AcceptedCount++;
        return true;
    }

    private bool TryAcceptDuplicateMutation(
        SyncEntityDto dto,
        string entityName,
        SyncPushResult result,
        ISet<Guid> exactReplayEntityIdsForHistoricalConflictResolution,
        Guid alternateEntityId = default)
    {
        var mutationId = NormalizeMutationId(dto.MutationId);
        if (string.IsNullOrWhiteSpace(mutationId))
            return false;

        if (ItemWarehouseStockMutationReceipt
            .IsReservedMutationId(mutationId))
        {
            AddClientConflict(
                dto,
                entityName,
                "Mutation id uses a server-reserved receipt namespace.",
                result);
            return true;
        }

        if (!_processedMutationsById.TryGetValue(mutationId, out var processedMutation))
        {
            var incomingPayloadHash =
                SyncMutationPayloadHasher.Compute(dto);
            _incomingMutationPayloadHashes.TryAdd(
                mutationId,
                incomingPayloadHash);
            return false;
        }

        var payloadEvaluation =
            SyncMutationPayloadHasher.EvaluateForReceiptReplay(
                dto,
                processedMutation.PayloadHash,
                processedMutation.MutationId);
        if (!ProcessedMutationMetadataMatches(
                dto,
                entityName,
                processedMutation,
                alternateEntityId) ||
            (!string.IsNullOrWhiteSpace(processedMutation.PayloadHash) &&
             !payloadEvaluation.StoredPayloadMatches &&
             !OriginalIncomingPayloadHashMatches(
                 mutationId,
                 processedMutation.PayloadHash)))
        {
            AddClientConflict(
                dto,
                entityName,
                "Mutation id was already processed with a different entity, expected revision, or payload.",
                result);
            return true;
        }

        if (dto.Id != Guid.Empty &&
            (!string.Equals(
                 entityName,
                 nameof(InventoryTransfer),
                 StringComparison.OrdinalIgnoreCase) ||
             !string.IsNullOrWhiteSpace(
                 processedMutation.PayloadHash)))
        {
            exactReplayEntityIdsForHistoricalConflictResolution.Add(dto.Id);
        }

        result.AcceptedCount++;
        result.DuplicateMutationCount++;
        return true;
    }

    private bool HasExactProcessedMutationReplay(
        SyncEntityDto dto,
        string entityName,
        Guid alternateEntityId = default)
    {
        var mutationId = NormalizeMutationId(dto.MutationId);
        return !string.IsNullOrWhiteSpace(mutationId) &&
               !ItemWarehouseStockMutationReceipt.IsReservedMutationId(mutationId) &&
               _processedMutationsById.TryGetValue(mutationId, out var processedMutation) &&
               ProcessedMutationMatches(
                   dto,
                   entityName,
                   processedMutation,
                   alternateEntityId);
    }

    private bool HasStrictProcessedMutationReplay(
        SyncEntityDto dto,
        string entityName)
    {
        var mutationId = NormalizeMutationId(dto.MutationId);
        return !string.IsNullOrWhiteSpace(mutationId) &&
               !ItemWarehouseStockMutationReceipt.IsReservedMutationId(
                   mutationId) &&
               _processedMutationsById.TryGetValue(
                   mutationId,
                   out var processedMutation) &&
               !string.IsNullOrWhiteSpace(
                   processedMutation.PayloadHash) &&
               ProcessedMutationMatches(
                   dto,
                   entityName,
                   processedMutation);
    }

    private bool ProcessedMutationMatches(
        SyncEntityDto dto,
        string entityName,
        ProcessedSyncMutation processedMutation,
        Guid alternateEntityId = default)
    {
        var payloadEvaluation =
            SyncMutationPayloadHasher.EvaluateForReceiptReplay(
                dto,
                processedMutation.PayloadHash,
                processedMutation.MutationId);
        return ProcessedMutationMetadataMatches(
                   dto,
                   entityName,
                   processedMutation,
                   alternateEntityId) &&
               (string.IsNullOrWhiteSpace(processedMutation.PayloadHash) ||
                payloadEvaluation.StoredPayloadMatches ||
                OriginalIncomingPayloadHashMatches(
                    NormalizeMutationId(dto.MutationId),
                    processedMutation.PayloadHash));
    }

    private bool OriginalIncomingPayloadHashMatches(
        string mutationId,
        string storedPayloadHash)
        => !string.IsNullOrWhiteSpace(mutationId) &&
           _incomingMutationPayloadHashes.TryGetValue(mutationId, out var originalPayloadHash) &&
           string.Equals(
               originalPayloadHash,
               storedPayloadHash,
               StringComparison.OrdinalIgnoreCase);

    private static bool ProcessedMutationMetadataMatches(
        SyncEntityDto dto,
        string entityName,
        ProcessedSyncMutation processedMutation,
        Guid alternateEntityId = default)
    {
        var entityIdMatches = string.Equals(
            processedMutation.EntityId,
            dto.Id.ToString("D"),
            StringComparison.OrdinalIgnoreCase) ||
            (alternateEntityId != Guid.Empty &&
             string.Equals(
                 processedMutation.EntityId,
                 alternateEntityId.ToString("D"),
                 StringComparison.OrdinalIgnoreCase));
        return string.Equals(
                   processedMutation.EntityName,
                   entityName,
                   StringComparison.OrdinalIgnoreCase) &&
               entityIdMatches &&
               processedMutation.ExpectedRevision == dto.ExpectedRevision;
    }

    private void RegisterProcessedMutation(
        SyncEntityDto dto,
        string entityName,
        string deviceId,
        bool preserveOriginalIncomingPayloadHash = false)
    {
        var mutationId = NormalizeMutationId(dto.MutationId);
        if (string.IsNullOrWhiteSpace(mutationId))
            return;

        if (_processedMutationsById.ContainsKey(mutationId))
            return;

        var payloadHash = SyncMutationPayloadHasher.Compute(dto);
        if ((preserveOriginalIncomingPayloadHash ||
             string.Equals(
                 entityName,
                 nameof(RentalBillingProfile),
                 StringComparison.OrdinalIgnoreCase)) &&
            _incomingMutationPayloadHashes.TryGetValue(
                mutationId,
                out var originalIncomingPayloadHash))
        {
            // Some server-authoritative mutations are normalized before their
            // receipt is written. Preserve the exact pre-normalization payload
            // so strict replay recognizes only the command the client sent.
            payloadHash = originalIncomingPayloadHash;
        }

        var processedMutation = new ProcessedSyncMutation
        {
            MutationId = mutationId,
            DeviceId = deviceId,
            EntityName = entityName,
            EntityId = dto.Id.ToString("D"),
            ExpectedRevision = dto.ExpectedRevision,
            PayloadHash = payloadHash,
            ProcessedAtUtc = dto.MutationCreatedAtUtc.HasValue && dto.MutationCreatedAtUtc.Value != default
                ? NormalizeUtc(dto.MutationCreatedAtUtc.Value)
                : DateTime.UtcNow
        };
        _dbContext.ProcessedSyncMutations.Add(processedMutation);
        _processedMutationsById.Add(mutationId, processedMutation);
    }

    private Task ResolveHistoricalConflictsAsync(
        string entityName,
        Guid entityId,
        string resolutionNote,
        CancellationToken cancellationToken)
        => entityId == Guid.Empty
            ? Task.CompletedTask
            : ResolveHistoricalConflictsAsync(
                entityName,
                [entityId],
                resolutionNote,
                cancellationToken);

    private async Task ResolveHistoricalConflictsAsync(
        string entityName,
        IReadOnlyCollection<Guid> entityIds,
        string resolutionNote,
        CancellationToken cancellationToken)
    {
        var entityIdTexts = entityIds
            .Where(entityId => entityId != Guid.Empty)
            .Select(entityId => entityId.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entityIdTexts.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var normalizedNote = (resolutionNote ?? string.Empty).Trim();

        foreach (var entityIdBatch in entityIdTexts.Chunk(500))
        {
            var batch = entityIdBatch.ToArray();
            await _dbContext.ConflictLogs
                .Where(conflict =>
                    conflict.EntityName == entityName &&
                    batch.Contains(conflict.EntityId) &&
                    conflict.Status != "Resolved")
                .ExecuteUpdateAsync(setters => setters
                        .SetProperty(conflict => conflict.Status, "Resolved")
                        .SetProperty(conflict => conflict.ResolvedAtUtc, now)
                        .SetProperty(conflict => conflict.ResolutionNote, normalizedNote),
                    cancellationToken);
        }
    }

    private async Task DeduplicateOpenConflictLogsForResultAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        CancellationToken cancellationToken)
    {
        if (conflicts.Count == 0)
            return;

        var fingerprints = conflicts
            .Select(conflict => new ConflictFingerprint(
                (conflict.EntityName ?? string.Empty).Trim(),
                (conflict.EntityId ?? string.Empty).Trim(),
                conflict.Reason ?? string.Empty))
            .Where(conflict =>
                !string.IsNullOrWhiteSpace(conflict.EntityName) ||
                !string.IsNullOrWhiteSpace(conflict.EntityId) ||
                !string.IsNullOrWhiteSpace(conflict.Reason))
            .Distinct()
            .ToList();

        var duplicateIds = new HashSet<Guid>();
        foreach (var fingerprintBatch in fingerprints.Chunk(100))
        {
            var batch = fingerprintBatch.ToArray();
            var batchFingerprints = batch.ToHashSet();
            var entityNames = batch
                .Select(fingerprint => fingerprint.EntityName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var entityIds = batch
                .Select(fingerprint => fingerprint.EntityId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var reasons = batch
                .Select(fingerprint => fingerprint.Reason)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var candidates = await _dbContext.ConflictLogs
                .AsNoTracking()
                .Where(conflict =>
                    conflict.Status != "Resolved" &&
                    entityNames.Contains(conflict.EntityName) &&
                    entityIds.Contains(conflict.EntityId) &&
                    reasons.Contains(conflict.Reason))
                .Select(conflict => new OpenConflictCandidate(
                    conflict.Id,
                    conflict.EntityName,
                    conflict.EntityId,
                    conflict.Reason,
                    conflict.CreatedAtUtc))
                .ToListAsync(cancellationToken);

            foreach (var duplicateId in candidates
                         .Where(candidate => batchFingerprints.Contains(candidate.Fingerprint))
                         .GroupBy(candidate => candidate.Fingerprint)
                         .SelectMany(group => group
                             .OrderByDescending(candidate => candidate.CreatedAtUtc)
                             .ThenByDescending(candidate => candidate.Id)
                             .Skip(1))
                         .Select(candidate => candidate.Id))
            {
                duplicateIds.Add(duplicateId);
            }
        }

        foreach (var duplicateIdBatch in duplicateIds.Chunk(500))
        {
            var batch = duplicateIdBatch.ToArray();
            await _dbContext.ConflictLogs
                .Where(conflict => batch.Contains(conflict.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    private sealed record ConflictFingerprint(
        string EntityName,
        string EntityId,
        string Reason);

    private sealed record OpenConflictCandidate(
        Guid Id,
        string EntityName,
        string EntityId,
        string Reason,
        DateTime CreatedAtUtc)
    {
        public ConflictFingerprint Fingerprint => new(EntityName, EntityId, Reason);
    }

    private static string NormalizeDeviceId(string? deviceId)
    {
        var normalized = (deviceId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "unknown-device" : normalized;
    }

    private static string NormalizeMutationId(string? mutationId)
        => ProcessedSyncMutationRecorder.NormalizeMutationId(mutationId);

    private static DateTime NormalizeConflictUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };

    private async Task<Guid?> ResolveRentalBillingProfileCustomerReferenceAsync(
        RentalBillingProfileDto dto,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var candidateKeys = BuildRentalCustomerReferenceKeys(
            dto.CustomerName);
        var normalizedBusinessNumber = NormalizeBusinessNumber(dto.BusinessNumber);
        var preferredOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(dto.ResponsibleOfficeCode, dto.OfficeCode);
        var preferredTenantCode = _officeScopeService.ResolveTenantForRentalCreate(dto.TenantCode, preferredOfficeCode);
        if (dto.CustomerId.HasValue && dto.CustomerId.Value != Guid.Empty)
        {
            var directCustomer = pushSnapshot is null
                ? await _dbContext.Customers.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(customer => customer.Id == dto.CustomerId.Value, cancellationToken)
                : pushSnapshot.FindCustomer(dto.CustomerId);
            if (directCustomer is not null &&
                !directCustomer.IsDeleted &&
                CanReadCustomerForRentalReference(directCustomer) &&
                CustomerReferenceTenantMatches(directCustomer, preferredTenantCode) &&
                (CustomerReferenceLooksValid(directCustomer, candidateKeys, normalizedBusinessNumber) ||
                 await ExistingRentalBillingProfileUsesCustomerAsync(
                     dto.Id,
                     directCustomer.Id,
                     cancellationToken,
                     pushSnapshot)))
            {
                return directCustomer.Id;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedBusinessNumber))
        {
            var businessMatches = pushSnapshot is null
                ? await _dbContext.Customers.IgnoreQueryFilters()
                    .Where(customer => !customer.IsDeleted)
                    .OrderByDescending(customer => customer.UpdatedAtUtc)
                    .ToListAsync(cancellationToken)
                : pushSnapshot.ActiveCustomers.ToList();
            businessMatches = businessMatches
                .Where(customer => NormalizeBusinessNumber(customer.BusinessNumber) == normalizedBusinessNumber)
                .Where(customer => CustomerMatchesRentalReferenceNames(customer, candidateKeys))
                .ToList();
            var resolvedBusinessMatch = ResolveReadableCustomerReference(
                businessMatches,
                preferredOfficeCode,
                preferredTenantCode);
            if (resolvedBusinessMatch.HasValue)
                return resolvedBusinessMatch.Value;
        }

        var candidateNames = new[]
            {
                dto.CustomerName
            }
            .Select(current => (current ?? string.Empty).Trim())
            .Where(current => !string.IsNullOrWhiteSpace(current))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidateNames.Count == 0)
            return null;

        var exactNameMatches = pushSnapshot is null
            ? await _dbContext.Customers.IgnoreQueryFilters()
                .Where(customer =>
                    !customer.IsDeleted &&
                    candidateNames.Contains(customer.NameOriginal))
                .OrderByDescending(customer => customer.UpdatedAtUtc)
                .ToListAsync(cancellationToken)
            : pushSnapshot.ActiveCustomers
                .Where(customer => candidateNames.Contains(customer.NameOriginal))
                .OrderByDescending(customer => customer.UpdatedAtUtc)
                .ToList();
        var resolvedExactNameMatch = ResolveReadableCustomerReference(
            exactNameMatches,
            preferredOfficeCode,
            preferredTenantCode);
        if (resolvedExactNameMatch.HasValue)
            return resolvedExactNameMatch.Value;

        var normalizedMatchKeys = candidateNames
            .Select(MatchKeyNormalizer.Normalize)
            .Where(current => !string.IsNullOrWhiteSpace(current))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedMatchKeys.Count == 0)
            return null;

        var nameKeyMatches = pushSnapshot is null
            ? await _dbContext.Customers.IgnoreQueryFilters()
                .Where(customer =>
                    !customer.IsDeleted &&
                    normalizedMatchKeys.Contains(customer.NameMatchKey))
                .OrderByDescending(customer => customer.UpdatedAtUtc)
                .ToListAsync(cancellationToken)
            : pushSnapshot.ActiveCustomers
                .Where(customer => normalizedMatchKeys.Contains(customer.NameMatchKey))
                .OrderByDescending(customer => customer.UpdatedAtUtc)
                .ToList();
        return ResolveReadableCustomerReference(
            nameKeyMatches,
            preferredOfficeCode,
            preferredTenantCode);
    }

    private async Task<List<RentalAssetDto>> PrepareScopedRentalAssetsAsync(
        IEnumerable<RentalAssetDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var scoped = new List<RentalAssetDto>();
        var reservedManagementIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var reservedManagementNumbers = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        Task<List<RentalAssetIdentifierUniverseEntry>>? identifierUniverseTask = null;
        Task<List<RentalAssetIdentifierUniverseEntry>> LoadIdentifierUniverseAsync()
            => identifierUniverseTask ??= _dbContext.RentalAssets
                .IgnoreQueryFilters()
                .Select(asset => new RentalAssetIdentifierUniverseEntry(
                    asset.Id,
                    asset.ManagementId,
                    asset.ManagementNumber))
                .ToListAsync(cancellationToken);

        foreach (var dto in payload)
        {
            dto.Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id;
            var existing = pushSnapshot is null
                ? await _dbContext.RentalAssets.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken)
                : pushSnapshot.FindAsset(dto.Id);
            if (existing is null)
            {
                existing = pushSnapshot is null
                    ? await FindExistingRentalAssetByNaturalKeyAsync(dto, cancellationToken)
                    : pushSnapshot.FindAssetByNaturalKey(dto);
                if (existing is not null)
                    dto.Id = existing.Id;
            }

            if (existing is not null && !_officeScopeService.CanWriteOfficeForRentals(existing.ResponsibleOfficeCode, existing.TenantCode, existing.OfficeCode))
            {
                AddClientConflict(dto, nameof(RentalAsset), "Current account cannot modify this office scope.", result);
                continue;
            }

            if (existing is not null && existing.IsDeleted && !dto.IsDeleted)
            {
                var activeConflict = pushSnapshot is null
                    ? await FindActiveRentalAssetRestoreConflictAsync(existing, cancellationToken)
                    : pushSnapshot.FindActiveAssetRestoreConflict(existing);
                if (activeConflict is not null)
                {
                    var message = "Cannot restore rental asset because an active asset uses the same rental asset identifiers.";
                    if (_officeScopeService.CanReadOfficeForRentals(
                            activeConflict.ResponsibleOfficeCode,
                            activeConflict.TenantCode,
                            activeConflict.OfficeCode))
                    {
                        message += $" Active asset: {BuildRentalAssetConflictDisplay(activeConflict)}.";
                    }

                    AddClientConflict(dto, nameof(RentalAsset), message, result);
                    continue;
                }
            }

            var requestedResponsibleOfficeCode = TenantScopeCatalog.TryNormalizeTenantCode(dto.TenantCode, out var requestedTenantCodeForResponsible) &&
                                                 string.Equals(requestedTenantCodeForResponsible, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
                ? OfficeCodeCatalog.Itworld
                : dto.ResponsibleOfficeCode;
            dto.ResponsibleOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(
                requestedResponsibleOfficeCode,
                existing?.ResponsibleOfficeCode ?? dto.OfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                existing?.OfficeCode);
            var resolvedTenantCode = _officeScopeService.ResolveTenantForRentalCreate(
                dto.TenantCode,
                dto.OfficeCode,
                existing?.TenantCode,
                existing?.OfficeCode);
            dto.TenantCode = TenantScopeCatalog.TryNormalizeTenantCode(dto.TenantCode, out var requestedTenantCode) &&
                             TenantScopeCatalog.TenantContainsOffice(requestedTenantCode, dto.OfficeCode)
                ? requestedTenantCode
                : resolvedTenantCode;
            dto.ManagementCompanyCode = string.IsNullOrWhiteSpace(dto.ManagementCompanyCode)
                ? dto.OfficeCode
                : dto.ManagementCompanyCode.Trim();
            await EnsureRentalAssetIdentifiersAsync(
                dto,
                existing,
                reservedManagementIds,
                reservedManagementNumbers,
                cancellationToken,
                pushSnapshot,
                LoadIdentifierUniverseAsync);
            dto.AssetKey = BuildRentalAssetKey(dto.ManagementCompanyCode, dto.ManagementNumber, dto.ManagementId, dto.MachineNumber, dto.CustomerName, dto.ItemName);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<Dictionary<Guid, Guid>> BuildRentalAssetRestoreCustomerIdsAsync(
        List<RentalAssetDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var linkedCustomerIds = new Dictionary<Guid, Guid>();
        var rejectedAssetIds = new HashSet<Guid>();
        var candidates = payload
            .Where(dto => dto.Id != Guid.Empty && !dto.IsDeleted)
            .ToList();
        var assetsById = new Dictionary<Guid, RentalAsset>();
        foreach (var dto in candidates)
        {
            var asset = pushSnapshot?.FindAsset(dto.Id);
            if (asset is not null)
                assetsById[dto.Id] = asset;
        }
        foreach (var assetIdBatch in candidates
                     .Select(dto => dto.Id)
                     .Where(assetId => !assetsById.ContainsKey(assetId))
                     .Distinct()
                     .Chunk(500))
        {
            var batch = assetIdBatch.ToArray();
            var assets = await _dbContext.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => batch.Contains(asset.Id))
                .ToListAsync(cancellationToken);
            foreach (var asset in assets)
                assetsById[asset.Id] = asset;
        }

        var candidateCustomerIds = candidates
            .Select(dto =>
            {
                var existing = assetsById.GetValueOrDefault(dto.Id);
                return dto.CustomerId is Guid customerId && customerId != Guid.Empty
                    ? customerId
                    : existing?.CustomerId.GetValueOrDefault() ?? Guid.Empty;
            })
            .Where(customerId => customerId != Guid.Empty)
            .Distinct()
            .ToList();
        var customersById = new Dictionary<Guid, Customer>();
        foreach (var customerId in candidateCustomerIds)
        {
            var customer = pushSnapshot?.FindCustomer(customerId);
            if (customer is not null)
                customersById[customerId] = customer;
        }
        foreach (var customerIdBatch in candidateCustomerIds
                     .Where(customerId => !customersById.ContainsKey(customerId))
                     .Chunk(500))
        {
            var batch = customerIdBatch.ToArray();
            var customers = await _dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => batch.Contains(customer.Id))
                .ToListAsync(cancellationToken);
            foreach (var customer in customers)
                customersById[customer.Id] = customer;
        }

        foreach (var dto in candidates)
        {
            var existing = assetsById.GetValueOrDefault(dto.Id);
            if (existing is null || !existing.IsDeleted)
                continue;

            var customerId = dto.CustomerId.HasValue && dto.CustomerId.Value != Guid.Empty
                ? dto.CustomerId.Value
                : existing.CustomerId.GetValueOrDefault();
            if (customerId == Guid.Empty)
                continue;

            var customer = customersById.GetValueOrDefault(customerId);
            if (customer is null || !customer.IsDeleted)
                continue;

            if (!_officeScopeService.CanEditCustomers())
            {
                AddClientConflict(
                    dto,
                    nameof(RentalAsset),
                    $"Linked deleted customer cannot be restored without customer edit permission: {customerId}.",
                    result);
                rejectedAssetIds.Add(dto.Id);
                continue;
            }

            if (!_officeScopeService.CanWriteOfficeForCustomers(
                    customer.ResponsibleOfficeCode,
                    customer.TenantCode,
                    customer.OfficeCode))
            {
                AddClientConflict(
                    dto,
                    nameof(RentalAsset),
                    $"Linked deleted customer cannot be restored in the current office scope: {customerId}.",
                    result);
                rejectedAssetIds.Add(dto.Id);
                continue;
            }

            linkedCustomerIds[dto.Id] = customerId;
        }

        if (rejectedAssetIds.Count > 0)
            payload.RemoveAll(dto => rejectedAssetIds.Contains(dto.Id));

        return linkedCustomerIds;
    }

    private async Task RestoreLinkedDeletedCustomerContractsForRentalAssetsAsync(
        IEnumerable<RentalAssetDto> acceptedAssets,
        IReadOnlyDictionary<Guid, Guid> linkedCustomerIds,
        CancellationToken cancellationToken)
    {
        if (linkedCustomerIds.Count == 0)
            return;

        var candidates = acceptedAssets
            .Where(dto => !dto.IsDeleted && dto.Id != Guid.Empty && linkedCustomerIds.ContainsKey(dto.Id))
            .ToList();
        var assetsById = new Dictionary<Guid, RentalAsset>();
        foreach (var assetIdBatch in candidates.Select(dto => dto.Id).Distinct().Chunk(500))
        {
            var batch = assetIdBatch.ToArray();
            var assets = await _dbContext.RentalAssets
                .IgnoreQueryFilters()
                .Where(asset => batch.Contains(asset.Id))
                .ToListAsync(cancellationToken);
            foreach (var asset in assets)
                assetsById[asset.Id] = asset;
        }

        var restoredCustomers = await RestoreDeletedLinkedCustomersAndContractsAsync(
            candidates.Select(dto => linkedCustomerIds[dto.Id]),
            cancellationToken);
        foreach (var dto in candidates)
        {
            var asset = assetsById.GetValueOrDefault(dto.Id);
            if (asset is null || asset.IsDeleted)
                continue;

            var customerId = linkedCustomerIds[dto.Id];
            if (!restoredCustomers.TryGetValue(customerId, out var customer))
                continue;

            asset.CustomerId = customer.Id;
            if (string.IsNullOrWhiteSpace(asset.CustomerName))
                asset.CustomerName = customer.NameOriginal;
            if (string.IsNullOrWhiteSpace(asset.CurrentCustomerName))
                asset.CurrentCustomerName = customer.NameOriginal;
        }
    }

    private async Task<List<RentalAssetAssignmentHistoryDto>> PrepareScopedRentalAssetAssignmentHistoriesAsync(
        IEnumerable<RentalAssetAssignmentHistoryDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<RentalAssetAssignmentHistoryDto>();

        foreach (var dto in payload)
        {
            dto.Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id;
            if (dto.AssetId == Guid.Empty)
            {
                AddNotice(
                    result,
                    nameof(RentalAssetAssignmentHistory),
                    dto.Id,
                    "missing-rental-asset",
                    "Referenced rental asset was not found. The stale assignment history was skipped.");
                continue;
            }

            var existing = await _dbContext.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(history => history.Id == dto.Id, cancellationToken);
            var asset = await _dbContext.RentalAssets
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == dto.AssetId, cancellationToken);

            if ((asset is null || asset.IsDeleted) && existing is null)
            {
                AddNotice(
                    result,
                    nameof(RentalAssetAssignmentHistory),
                    dto.Id,
                    "missing-rental-asset",
                    "Referenced rental asset was not found. The stale assignment history was skipped.");
                continue;
            }

            if (asset is not null && !asset.IsDeleted &&
                !_officeScopeService.CanWriteOfficeForRentals(asset.ResponsibleOfficeCode, asset.TenantCode, asset.OfficeCode))
            {
                AddClientConflict(dto, nameof(RentalAssetAssignmentHistory),
                    $"Referenced rental asset is outside the writable office scope: {dto.AssetId}.", result);
                continue;
            }

            if ((asset is null || asset.IsDeleted) &&
                existing is not null &&
                existing.AssetId != dto.AssetId)
            {
                AddClientConflict(dto, nameof(RentalAssetAssignmentHistory),
                    $"Referenced rental asset was not found: {dto.AssetId}.", result);
                continue;
            }

            if (!await ValidateRentalAssignmentHistoryReferencesAsync(dto, result, cancellationToken))
                continue;

            var responsibleOfficeCode = existing?.ResponsibleOfficeCode
                                        ?? asset?.ResponsibleOfficeCode
                                        ?? dto.ResponsibleOfficeCode;
            var officeCode = existing?.OfficeCode
                             ?? asset?.OfficeCode
                             ?? dto.OfficeCode;
            var tenantCode = existing?.TenantCode
                             ?? asset?.TenantCode
                             ?? dto.TenantCode;

            if (!_officeScopeService.CanWriteOfficeForRentals(responsibleOfficeCode, tenantCode, officeCode))
            {
                AddClientConflict(dto, nameof(RentalAssetAssignmentHistory), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.ResponsibleOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(
                responsibleOfficeCode,
                officeCode);
            dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                officeCode,
                dto.ResponsibleOfficeCode,
                officeCode);
            dto.TenantCode = _officeScopeService.ResolveTenantForRentalCreate(
                tenantCode,
                dto.OfficeCode,
                tenantCode,
                dto.OfficeCode);

            if (asset is not null)
            {
                dto.ItemName = string.IsNullOrWhiteSpace(dto.ItemName) ? asset.ItemName : dto.ItemName.Trim();
                dto.MachineNumber = string.IsNullOrWhiteSpace(dto.MachineNumber) ? asset.MachineNumber : dto.MachineNumber.Trim();
                dto.ManagementNumber = string.IsNullOrWhiteSpace(dto.ManagementNumber) ? asset.ManagementNumber : dto.ManagementNumber.Trim();
                if (dto.MonthlyFee <= 0m)
                    dto.MonthlyFee = asset.MonthlyFee;
                dto.ContractStartDate ??= asset.ContractStartDate;
                dto.ContractEndDate ??= asset.RentalEndDate;
            }

            dto.CustomerName = dto.CustomerName?.Trim() ?? string.Empty;
            dto.InstallLocation = dto.InstallLocation?.Trim() ?? string.Empty;
            dto.BillingProfileDisplay = dto.BillingProfileDisplay?.Trim() ?? string.Empty;
            dto.ChangeReason = dto.ChangeReason?.Trim() ?? string.Empty;
            if (!dto.IsCurrent && dto.UnlinkedAtUtc is null)
                dto.UnlinkedAtUtc = dto.LinkedAtUtc == default ? DateTime.UtcNow : dto.LinkedAtUtc;
            if (dto.LinkedAtUtc == default)
                dto.LinkedAtUtc = dto.UnlinkedAtUtc ?? DateTime.UtcNow;

            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<bool> ValidateRentalAssignmentHistoryReferencesAsync(
        RentalAssetAssignmentHistoryDto dto,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        if (dto.BillingProfileId.HasValue && dto.BillingProfileId.Value != Guid.Empty)
        {
            var requestedBillingProfileId = dto.BillingProfileId.Value;
            var billingProfile = await _dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(profile => profile.Id == requestedBillingProfileId, cancellationToken);
            if (billingProfile is null || billingProfile.IsDeleted)
            {
                if (!dto.IsCurrent)
                {
                    dto.BillingProfileId = null;
                    AddNotice(
                        result,
                        nameof(RentalAssetAssignmentHistory),
                        dto.Id,
                        "historical-rental-assignment-profile-reference-cleared",
                        $"Historical rental assignment '{dto.Id:D}' referenced a missing or deleted billing profile '{requestedBillingProfileId:D}'. The display snapshot was kept and the stale profile reference was cleared.");
                }
                else
                {
                    AddClientConflict(dto, nameof(RentalAssetAssignmentHistory),
                        $"Referenced rental billing profile is missing or deleted: {requestedBillingProfileId}.", result);
                    return false;
                }
            }

            if (billingProfile is not null &&
                !billingProfile.IsDeleted &&
                !_officeScopeService.CanWriteOfficeForRentals(billingProfile.ResponsibleOfficeCode, billingProfile.TenantCode, billingProfile.OfficeCode))
            {
                AddClientConflict(dto, nameof(RentalAssetAssignmentHistory),
                    $"Referenced rental billing profile is outside the writable office scope: {billingProfile.Id}.", result);
                return false;
            }
        }

        if (dto.CustomerId.HasValue && dto.CustomerId.Value != Guid.Empty)
        {
            var requestedCustomerId = dto.CustomerId.Value;
            var customer = await _dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == requestedCustomerId, cancellationToken);
            if (customer is null || customer.IsDeleted)
            {
                if (!dto.IsCurrent)
                {
                    dto.CustomerId = null;
                    AddNotice(
                        result,
                        nameof(RentalAssetAssignmentHistory),
                        dto.Id,
                        "historical-rental-assignment-customer-reference-cleared",
                        $"Historical rental assignment '{dto.Id:D}' referenced a missing or deleted customer '{requestedCustomerId:D}'. The display snapshot was kept and the stale customer reference was cleared.");
                }
                else
                {
                    AddClientConflict(dto, nameof(RentalAssetAssignmentHistory),
                        $"Referenced customer is missing or deleted: {requestedCustomerId}.", result);
                    return false;
                }
            }

            if (customer is not null &&
                !customer.IsDeleted &&
                !CanReadCustomerForRentalReference(customer))
            {
                AddClientConflict(dto, nameof(RentalAssetAssignmentHistory),
                    $"Referenced customer is outside the readable office scope: {customer.Id}.", result);
                return false;
            }
        }

        return true;
    }

    private async Task<RentalAsset?> FindExistingRentalAssetByNaturalKeyAsync(
        RentalAssetDto dto,
        CancellationToken cancellationToken)
    {
        var managementNumber = dto.ManagementNumber?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(managementNumber))
        {
            var byManagementNumber = await _dbContext.RentalAssets.IgnoreQueryFilters()
                .FirstOrDefaultAsync(asset => asset.ManagementNumber == managementNumber, cancellationToken);
            if (byManagementNumber is not null)
                return byManagementNumber;
        }

        var managementId = dto.ManagementId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(managementId))
        {
            var byManagementId = await _dbContext.RentalAssets.IgnoreQueryFilters()
                .FirstOrDefaultAsync(asset => asset.ManagementId == managementId, cancellationToken);
            if (byManagementId is not null)
                return byManagementId;
        }

        var assetKey = dto.AssetKey?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(assetKey))
        {
            var byAssetKey = await _dbContext.RentalAssets.IgnoreQueryFilters()
                .FirstOrDefaultAsync(asset => asset.AssetKey == assetKey, cancellationToken);
            if (byAssetKey is not null)
                return byAssetKey;
        }

        return null;
    }

    private async Task EnsureRentalAssetIdentifiersAsync(
        RentalAssetDto dto,
        RentalAsset? existing,
        IDictionary<string, Guid> reservedManagementIds,
        IDictionary<string, Guid> reservedManagementNumbers,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null,
        Func<Task<List<RentalAssetIdentifierUniverseEntry>>>? loadIdentifierUniverseAsync = null)
    {
        dto.ManagementId = await ResolveManagementIdAsync(
            dto, existing, reservedManagementIds, cancellationToken, pushSnapshot, loadIdentifierUniverseAsync);
        ReserveManagementValue(reservedManagementIds, dto.ManagementId, dto.Id);

        dto.ManagementNumber = await ResolveManagementNumberAsync(
            dto, existing, reservedManagementNumbers, cancellationToken, pushSnapshot, loadIdentifierUniverseAsync);
        ReserveManagementValue(reservedManagementNumbers, dto.ManagementNumber, dto.Id);
    }

    private async Task<string> ResolveManagementIdAsync(
        RentalAssetDto dto,
        RentalAsset? existing,
        IDictionary<string, Guid> reservedManagementIds,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null,
        Func<Task<List<RentalAssetIdentifierUniverseEntry>>>? loadIdentifierUniverseAsync = null)
    {
        var requestedValue = existing?.ManagementId ?? dto.ManagementId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(requestedValue) &&
            await IsManagementIdAvailableAsync(requestedValue, dto.Id, reservedManagementIds, cancellationToken, pushSnapshot))
        {
            return requestedValue;
        }

        var usedIds = loadIdentifierUniverseAsync is null
            ? await _dbContext.RentalAssets.IgnoreQueryFilters()
                .Where(asset => asset.Id != dto.Id)
                .Select(asset => asset.ManagementId)
                .ToListAsync(cancellationToken)
            : (await loadIdentifierUniverseAsync())
                .Where(asset => asset.Id != dto.Id)
                .Select(asset => asset.ManagementId)
                .ToList();

        var nextValue = usedIds
            .Select(ParseManagementId)
            .Concat(reservedManagementIds.Keys.Select(ParseManagementId))
            .DefaultIfEmpty(0)
            .Max() + 1;

        return nextValue.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<string> ResolveManagementNumberAsync(
        RentalAssetDto dto,
        RentalAsset? existing,
        IDictionary<string, Guid> reservedManagementNumbers,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null,
        Func<Task<List<RentalAssetIdentifierUniverseEntry>>>? loadIdentifierUniverseAsync = null)
    {
        var requestedValue = existing?.ManagementNumber ?? dto.ManagementNumber?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(requestedValue) &&
            await IsManagementNumberAvailableAsync(requestedValue, dto.Id, reservedManagementNumbers, cancellationToken, pushSnapshot))
        {
            return requestedValue;
        }

        var registeredLocalDate = ConvertUtcToKoreaDate(dto.CreatedAtUtc == default ? DateTime.UtcNow : dto.CreatedAtUtc);
        var prefix = registeredLocalDate.ToString("yyMM", CultureInfo.InvariantCulture);
        var usedNumbers = loadIdentifierUniverseAsync is null
            ? await _dbContext.RentalAssets.IgnoreQueryFilters()
                .Where(asset => asset.Id != dto.Id)
                .Select(asset => asset.ManagementNumber)
                .ToListAsync(cancellationToken)
            : (await loadIdentifierUniverseAsync())
                .Where(asset => asset.Id != dto.Id)
                .Select(asset => asset.ManagementNumber)
                .ToList();

        var nextSequence = usedNumbers
            .Select(number => ParseManagementNumberSequence(number, prefix))
            .Concat(reservedManagementNumbers.Keys.Select(number => ParseManagementNumberSequence(number, prefix)))
            .DefaultIfEmpty(0)
            .Max() + 1;

        return $"{prefix}-{nextSequence:000}";
    }

    private async Task<RentalAsset?> FindActiveRentalAssetRestoreConflictAsync(
        RentalAsset target,
        CancellationToken cancellationToken)
    {
        var candidates = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .Where(current => current.Id != target.Id && !current.IsDeleted)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(candidate =>
            RentalAssetRestoreKeysMatch(candidate.ManagementNumber, target.ManagementNumber) ||
            RentalAssetRestoreKeysMatch(candidate.ManagementId, target.ManagementId) ||
            RentalAssetRestoreKeysMatch(candidate.AssetKey, target.AssetKey));
    }

    private static bool RentalAssetRestoreKeysMatch(string? left, string? right)
    {
        var normalizedLeft = (left ?? string.Empty).Trim();
        var normalizedRight = (right ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(normalizedLeft) &&
               string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRentalAssetConflictDisplay(RentalAsset asset)
        => string.Join(
            ", ",
            new[]
            {
                string.IsNullOrWhiteSpace(asset.ManagementNumber) ? null : $"management number {asset.ManagementNumber}",
                string.IsNullOrWhiteSpace(asset.ManagementId) ? null : $"management id {asset.ManagementId}",
                string.IsNullOrWhiteSpace(asset.AssetKey) ? null : $"asset key {asset.AssetKey}",
                string.IsNullOrWhiteSpace(asset.ItemName) ? null : asset.ItemName
            }.Where(segment => !string.IsNullOrWhiteSpace(segment)));

    private async Task<bool> IsManagementIdAvailableAsync(
        string managementId,
        Guid currentId,
        IDictionary<string, Guid> reservedManagementIds,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var normalizedValue = (managementId ?? string.Empty).Trim();
        if (reservedManagementIds.TryGetValue(normalizedValue, out var reservedId) && reservedId != currentId)
            return false;

        if (pushSnapshot is not null)
            return pushSnapshot.IsAssetIdentifierAvailable(normalizedValue, currentId, asset => asset.ManagementId);

        return await _dbContext.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id != currentId)
            .AllAsync(asset => asset.ManagementId != normalizedValue, cancellationToken);
    }

    private async Task<bool> IsManagementNumberAvailableAsync(
        string managementNumber,
        Guid currentId,
        IDictionary<string, Guid> reservedManagementNumbers,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var normalizedValue = (managementNumber ?? string.Empty).Trim();
        if (reservedManagementNumbers.TryGetValue(normalizedValue, out var reservedId) && reservedId != currentId)
            return false;

        if (pushSnapshot is not null)
            return pushSnapshot.IsAssetIdentifierAvailable(normalizedValue, currentId, asset => asset.ManagementNumber);

        return await _dbContext.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id != currentId)
            .AllAsync(asset => asset.ManagementNumber != normalizedValue, cancellationToken);
    }

    private static void ReserveManagementValue(IDictionary<string, Guid> reservedValues, string? value, Guid ownerId)
    {
        var normalizedValue = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue))
            return;

        reservedValues[normalizedValue] = ownerId;
    }

    private static string BuildRentalAssetKey(
        string? managementCompanyCode,
        string? managementNumber,
        string? managementId,
        string? machineNumber,
        string? customerName,
        string? itemName)
    {
        var primary = !string.IsNullOrWhiteSpace(managementNumber)
            ? managementNumber
            : !string.IsNullOrWhiteSpace(managementId)
                ? managementId
                : machineNumber;

        return string.Join('|',
            NormalizeKeyPart(managementCompanyCode),
            NormalizeKeyPart(primary),
            NormalizeKeyPart(customerName),
            NormalizeKeyPart(itemName));
    }

    private static string NormalizeKeyPart(string? value)
        => new string((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(ch => !char.IsWhiteSpace(ch) && ch != '[' && ch != ']')
            .ToArray());

    private readonly record struct RentalAssetIdentifierUniverseEntry(
        Guid Id,
        string ManagementId,
        string ManagementNumber);

    private readonly record struct RentalDependentOperationalScope(
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode);

    private RentalDependentOperationalScope ResolveInvoiceDependentOperationalScope(
        InvoiceDto dto,
        Invoice? existing,
        Customer? customer)
    {
        var responsibleOfficeCode = _officeScopeService.ResolveInvoiceResponsibleScopeForCreate(
            dto.ResponsibleOfficeCode,
            customer?.ResponsibleOfficeCode ?? existing?.ResponsibleOfficeCode);
        var fallbackOfficeCode = existing?.OfficeCode ?? customer?.OfficeCode;
        var officeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            dto.OfficeCode,
            responsibleOfficeCode,
            fallbackOfficeCode);
        var tenantCode = _officeScopeService.ResolveTenantForCreate(
            dto.TenantCode,
            officeCode,
            existing?.TenantCode ?? customer?.TenantCode,
            fallbackOfficeCode);
        return new RentalDependentOperationalScope(
            tenantCode,
            officeCode,
            responsibleOfficeCode);
    }

    private RentalDependentOperationalScope ResolveTransactionDependentOperationalScope(
        TransactionDto dto,
        TransactionRecord? existing)
    {
        var responsibleOfficeCode = _officeScopeService.ResolvePaymentResponsibleScopeForCreate(
            dto.ResponsibleOfficeCode,
            existing?.ResponsibleOfficeCode);
        var officeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            dto.OfficeCode,
            responsibleOfficeCode,
            existing?.OfficeCode);
        var tenantCode = _officeScopeService.ResolveTenantForCreate(
            dto.TenantCode,
            officeCode,
            existing?.TenantCode,
            existing?.OfficeCode);
        return new RentalDependentOperationalScope(
            tenantCode,
            officeCode,
            responsibleOfficeCode);
    }

    private RentalDependentOperationalScope ResolveRentalAssetDependentOperationalScope(
        RentalAssetDto dto,
        RentalAsset? existing,
        Customer? linkedCustomer)
    {
        var requestedResponsibleOfficeCode =
            TenantScopeCatalog.TryNormalizeTenantCode(
                dto.TenantCode,
                out var requestedTenantCodeForResponsible) &&
            string.Equals(
                requestedTenantCodeForResponsible,
                TenantScopeCatalog.Itworld,
                StringComparison.OrdinalIgnoreCase)
                ? OfficeCodeCatalog.Itworld
                : dto.ResponsibleOfficeCode;
        var responsibleOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(
            requestedResponsibleOfficeCode,
            existing?.ResponsibleOfficeCode ?? dto.OfficeCode);
        var officeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            dto.OfficeCode,
            responsibleOfficeCode,
            existing?.OfficeCode);
        var resolvedTenantCode = _officeScopeService.ResolveTenantForRentalCreate(
            dto.TenantCode,
            officeCode,
            existing?.TenantCode,
            existing?.OfficeCode);
        var tenantCode = TenantScopeCatalog.TryNormalizeTenantCode(
                             dto.TenantCode,
                             out var requestedTenantCode) &&
                         TenantScopeCatalog.TenantContainsOffice(
                             requestedTenantCode,
                             officeCode)
            ? requestedTenantCode
            : resolvedTenantCode;

        if (linkedCustomer is not null && !linkedCustomer.IsDeleted)
        {
            responsibleOfficeCode = ResolveRentalCustomerOfficeCode(
                linkedCustomer.ResponsibleOfficeCode);
            officeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                linkedCustomer.OfficeCode,
                responsibleOfficeCode,
                linkedCustomer.OfficeCode);
            tenantCode = _officeScopeService.ResolveTenantForRentalCreate(
                tenantCode,
                officeCode,
                linkedCustomer.TenantCode,
                linkedCustomer.OfficeCode);
        }

        return new RentalDependentOperationalScope(
            tenantCode,
            officeCode,
            responsibleOfficeCode);
    }

    private RentalDependentOperationalScope ResolveRentalAssignmentHistoryDependentOperationalScope(
        RentalAssetAssignmentHistoryDto dto,
        RentalAssetAssignmentHistory? existing,
        RentalDependentOperationalScope? assetScope,
        RentalAsset? existingAsset)
    {
        var responsibleOfficeCode = existing?.ResponsibleOfficeCode
                                    ?? assetScope?.ResponsibleOfficeCode
                                    ?? existingAsset?.ResponsibleOfficeCode
                                    ?? dto.ResponsibleOfficeCode;
        var officeCode = existing?.OfficeCode
                         ?? assetScope?.OfficeCode
                         ?? existingAsset?.OfficeCode
                         ?? dto.OfficeCode;
        var tenantCode = existing?.TenantCode
                         ?? assetScope?.TenantCode
                         ?? existingAsset?.TenantCode
                         ?? dto.TenantCode;
        responsibleOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(
            responsibleOfficeCode,
            officeCode);
        officeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            officeCode,
            responsibleOfficeCode,
            officeCode);
        tenantCode = _officeScopeService.ResolveTenantForRentalCreate(
            tenantCode,
            officeCode,
            tenantCode,
            officeCode);
        return new RentalDependentOperationalScope(
            tenantCode,
            officeCode,
            responsibleOfficeCode);
    }

    private RentalDependentOperationalScope ResolveRentalBillingLogDependentOperationalScope(
        RentalBillingLogDto dto,
        RentalBillingLog? existing,
        RentalBillingProfile? profile)
    {
        var responsibleOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(
            dto.ResponsibleOfficeCode,
            existing?.ResponsibleOfficeCode);
        var officeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            dto.OfficeCode,
            responsibleOfficeCode,
            existing?.OfficeCode);
        var tenantCode = _officeScopeService.ResolveTenantForRentalCreate(
            dto.TenantCode,
            officeCode,
            existing?.TenantCode,
            existing?.OfficeCode);
        if (profile is not null && !profile.IsDeleted)
        {
            responsibleOfficeCode = profile.ResponsibleOfficeCode;
            officeCode = profile.OfficeCode;
            tenantCode = _officeScopeService.ResolveTenantForRentalCreate(
                tenantCode,
                officeCode,
                profile.TenantCode,
                profile.OfficeCode);
        }

        return new RentalDependentOperationalScope(
            tenantCode,
            officeCode,
            responsibleOfficeCode);
    }

    private static int ParseManagementId(string? managementId)
        => int.TryParse((managementId ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static int ParseManagementNumberSequence(string? managementNumber, string prefix)
    {
        var normalizedValue = (managementNumber ?? string.Empty).Trim();
        if (!normalizedValue.StartsWith($"{prefix}-", StringComparison.OrdinalIgnoreCase))
            return 0;

        var sequenceText = normalizedValue[(prefix.Length + 1)..];
        return int.TryParse(sequenceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static DateOnly ConvertUtcToKoreaDate(DateTime utcDateTime)
    {
        var normalizedUtc = utcDateTime.Kind == DateTimeKind.Utc
            ? utcDateTime
            : utcDateTime.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc)
                : utcDateTime.ToUniversalTime();
        var koreaDateTime = TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, KoreaTimeZone);
        return DateOnly.FromDateTime(koreaDateTime);
    }

    private static TimeZoneInfo ResolveKoreaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        }
    }

    private async Task RejectBlockedPriorGenerationRentalDependentsAsync(
        SyncPushRequest request,
        IReadOnlySet<RentalProfileTenantIdentity> blockedProfileIdentities,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        if (blockedProfileIdentities.Count == 0)
            return;

        const string conflictReason =
            "A referenced rental billing profile was acknowledged as a purge no-op or exact replay with an unproven generation. Pull before retrying this dependent mutation.";
        static IEnumerable<Guid> NonEmptyIds(IEnumerable<Guid?> ids)
            => ids
                .Where(id => id.HasValue && id.Value != Guid.Empty)
                .Select(id => id!.Value);

        var invoices = request.Invoices ?? [];
        var transactions = request.Transactions ?? [];
        var rentalAssets = request.RentalAssets ?? [];
        var assignmentHistories = request.RentalAssetAssignmentHistories ?? [];
        var billingLogs = request.RentalBillingLogs ?? [];
        var payments = request.Payments ?? [];

        var transactionLookupIds = transactions
            .Select(dto => dto.Id)
            .Concat(payments.Select(dto => dto.Id))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var existingTransactions = transactionLookupIds.Count == 0
            ? new Dictionary<Guid, TransactionRecord>()
            : await _dbContext.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => transactionLookupIds.Contains(entity.Id))
                .ToDictionaryAsync(entity => entity.Id, cancellationToken);

        var paymentIds = payments
            .Select(dto => dto.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var existingPayments = paymentIds.Count == 0
            ? new Dictionary<Guid, Payment>()
            : await _dbContext.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => paymentIds.Contains(entity.Id))
                .ToDictionaryAsync(entity => entity.Id, cancellationToken);

        var invoiceLookupIds = invoices
            .Select(dto => dto.Id)
            .Concat(NonEmptyIds(transactions.Select(dto => dto.LinkedInvoiceId)))
            .Concat(NonEmptyIds(existingTransactions.Values.Select(entity => entity.LinkedInvoiceId)))
            .Concat(payments.Select(dto => dto.InvoiceId))
            .Concat(existingPayments.Values.Select(entity => entity.InvoiceId))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var existingInvoices = invoiceLookupIds.Count == 0
            ? new Dictionary<Guid, Invoice>()
            : await _dbContext.Invoices
                .IgnoreQueryFilters()
                .Include(entity => entity.Customer)
                .AsNoTracking()
                .Where(entity => invoiceLookupIds.Contains(entity.Id))
                .ToDictionaryAsync(entity => entity.Id, cancellationToken);

        var invoiceCustomerIds = invoices
            .Select(dto => dto.CustomerId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var invoiceCustomers = invoiceCustomerIds.Count == 0
            ? new Dictionary<Guid, Customer>()
            : await _dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => invoiceCustomerIds.Contains(customer.Id))
                .ToDictionaryAsync(customer => customer.Id, cancellationToken);

        var invoiceProfileIdentitiesById =
            new Dictionary<Guid, HashSet<RentalProfileTenantIdentity>>();
        void AddInvoiceProfileIdentity(
            Guid invoiceId,
            Guid? profileId,
            string? tenantCode)
        {
            if (invoiceId == Guid.Empty ||
                !TryCreateRentalProfileTenantIdentity(
                    profileId,
                    tenantCode,
                    out var identity))
            {
                return;
            }

            if (!invoiceProfileIdentitiesById.TryGetValue(
                    invoiceId,
                    out var profileIdentities))
            {
                profileIdentities = [];
                invoiceProfileIdentitiesById[invoiceId] = profileIdentities;
            }

            profileIdentities.Add(identity);
        }

        foreach (var existingInvoice in existingInvoices.Values)
        {
            AddInvoiceProfileIdentity(
                existingInvoice.Id,
                existingInvoice.LinkedRentalBillingProfileId,
                existingInvoice.TenantCode);
        }
        var effectiveInvoiceScopes =
            new Dictionary<InvoiceDto, RentalDependentOperationalScope>(
                ReferenceEqualityComparer.Instance);
        foreach (var dto in invoices)
        {
            existingInvoices.TryGetValue(dto.Id, out var existing);
            Customer? customer = null;
            if (dto.CustomerId != Guid.Empty)
            {
                if (existing?.Customer?.Id == dto.CustomerId)
                    customer = existing.Customer;
                else
                    invoiceCustomers.TryGetValue(dto.CustomerId, out customer);
            }

            customer ??= existing?.Customer;
            var effectiveScope = ResolveInvoiceDependentOperationalScope(
                dto,
                existing,
                customer);
            effectiveInvoiceScopes[dto] = effectiveScope;
            AddInvoiceProfileIdentity(
                dto.Id,
                dto.LinkedRentalBillingProfileId,
                effectiveScope.TenantCode);
        }

        bool InvoiceReferencesBlocked(Guid? invoiceId)
            => invoiceId.HasValue &&
               invoiceProfileIdentitiesById.TryGetValue(
                   invoiceId.Value,
                   out var profileIdentities) &&
               profileIdentities.Overlaps(blockedProfileIdentities);

        var rejectedInvoices = new HashSet<InvoiceDto>(ReferenceEqualityComparer.Instance);
        foreach (var dto in invoices)
        {
            existingInvoices.TryGetValue(dto.Id, out var existing);
            var effectiveTenantCode = effectiveInvoiceScopes[dto].TenantCode;
            if (!IsBlockedRentalProfileIdentity(
                    blockedProfileIdentities,
                    dto.LinkedRentalBillingProfileId,
                    effectiveTenantCode) &&
                !IsBlockedRentalProfileIdentity(
                    blockedProfileIdentities,
                    existing?.LinkedRentalBillingProfileId,
                    existing?.TenantCode))
            {
                continue;
            }

            AddClientConflict(dto, nameof(Invoice), conflictReason, result);
            rejectedInvoices.Add(dto);
        }

        invoices.RemoveAll(dto => rejectedInvoices.Contains(dto));

        var transactionProfileIdentitiesById =
            new Dictionary<Guid, HashSet<RentalProfileTenantIdentity>>();
        void AddTransactionProfileIdentity(
            Guid transactionId,
            Guid? profileId,
            string? tenantCode)
        {
            if (transactionId == Guid.Empty ||
                !TryCreateRentalProfileTenantIdentity(
                    profileId,
                    tenantCode,
                    out var identity))
            {
                return;
            }

            if (!transactionProfileIdentitiesById.TryGetValue(
                    transactionId,
                    out var profileIdentities))
            {
                profileIdentities = [];
                transactionProfileIdentitiesById[transactionId] = profileIdentities;
            }

            profileIdentities.Add(identity);
        }

        void AddTransactionInvoiceProfileIds(Guid transactionId, Guid? invoiceId)
        {
            if (!invoiceId.HasValue ||
                !invoiceProfileIdentitiesById.TryGetValue(
                    invoiceId.Value,
                    out var profileIdentities))
            {
                return;
            }

            if (!transactionProfileIdentitiesById.TryGetValue(
                    transactionId,
                    out var transactionProfileIdentities))
            {
                transactionProfileIdentities = [];
                transactionProfileIdentitiesById[transactionId] =
                    transactionProfileIdentities;
            }

            transactionProfileIdentities.UnionWith(profileIdentities);
        }

        foreach (var existingTransaction in existingTransactions.Values)
        {
            AddTransactionProfileIdentity(
                existingTransaction.Id,
                existingTransaction.LinkedRentalBillingProfileId,
                existingTransaction.TenantCode);
            AddTransactionInvoiceProfileIds(existingTransaction.Id, existingTransaction.LinkedInvoiceId);
        }
        var effectiveTransactionScopes =
            new Dictionary<TransactionDto, RentalDependentOperationalScope>(
                ReferenceEqualityComparer.Instance);
        foreach (var dto in transactions)
        {
            existingTransactions.TryGetValue(dto.Id, out var existing);
            var effectiveScope = ResolveTransactionDependentOperationalScope(
                dto,
                existing);
            effectiveTransactionScopes[dto] = effectiveScope;
            AddTransactionProfileIdentity(
                dto.Id,
                dto.LinkedRentalBillingProfileId,
                effectiveScope.TenantCode);
            AddTransactionInvoiceProfileIds(dto.Id, dto.LinkedInvoiceId);
        }

        var rejectedTransactions = new HashSet<TransactionDto>(ReferenceEqualityComparer.Instance);
        foreach (var dto in transactions)
        {
            existingTransactions.TryGetValue(dto.Id, out var existing);
            var effectiveTenantCode = effectiveTransactionScopes[dto].TenantCode;
            var referencesBlockedProfile =
                IsBlockedRentalProfileIdentity(
                    blockedProfileIdentities,
                    dto.LinkedRentalBillingProfileId,
                    effectiveTenantCode) ||
                IsBlockedRentalProfileIdentity(
                    blockedProfileIdentities,
                    existing?.LinkedRentalBillingProfileId,
                    existing?.TenantCode) ||
                InvoiceReferencesBlocked(dto.LinkedInvoiceId) ||
                InvoiceReferencesBlocked(existing?.LinkedInvoiceId) ||
                (transactionProfileIdentitiesById.TryGetValue(
                     dto.Id,
                     out var profileIdentities) &&
                 profileIdentities.Overlaps(blockedProfileIdentities));
            if (!referencesBlockedProfile)
                continue;

            AddClientConflict(dto, nameof(TransactionRecord), conflictReason, result);
            rejectedTransactions.Add(dto);
        }

        transactions.RemoveAll(dto => rejectedTransactions.Contains(dto));

        var assetIds = rentalAssets
            .Select(dto => dto.Id)
            .Concat(assignmentHistories.Select(dto => dto.AssetId))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var existingAssets = assetIds.Count == 0
            ? new Dictionary<Guid, RentalAsset>()
            : await _dbContext.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => assetIds.Contains(entity.Id))
                .ToDictionaryAsync(entity => entity.Id, cancellationToken);
        var assetCustomerIds = NonEmptyIds(
                rentalAssets.Select(dto => dto.CustomerId))
            .Distinct()
            .ToList();
        var assetCustomers = assetCustomerIds.Count == 0
            ? new Dictionary<Guid, Customer>()
            : await _dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => assetCustomerIds.Contains(customer.Id))
                .ToDictionaryAsync(customer => customer.Id, cancellationToken);
        var effectiveAssetScopes =
            new Dictionary<RentalAssetDto, RentalDependentOperationalScope>(
                ReferenceEqualityComparer.Instance);
        var rejectedAssets = new HashSet<RentalAssetDto>(ReferenceEqualityComparer.Instance);
        foreach (var dto in rentalAssets)
        {
            existingAssets.TryGetValue(dto.Id, out var existing);
            existing ??= await FindExistingRentalAssetByNaturalKeyAsync(
                dto,
                cancellationToken);
            Customer? linkedCustomer = null;
            if (dto.CustomerId.HasValue && dto.CustomerId.Value != Guid.Empty)
                assetCustomers.TryGetValue(dto.CustomerId.Value, out linkedCustomer);
            var effectiveScope = ResolveRentalAssetDependentOperationalScope(
                dto,
                existing,
                linkedCustomer);
            effectiveAssetScopes[dto] = effectiveScope;
            var referencesBlockedProfile =
                IsBlockedRentalProfileIdentity(
                    blockedProfileIdentities,
                    dto.BillingProfileId,
                    effectiveScope.TenantCode) ||
                IsBlockedRentalProfileIdentity(
                    blockedProfileIdentities,
                    dto.LastBillingProfileId,
                    effectiveScope.TenantCode) ||
                IsBlockedRentalProfileIdentity(
                    blockedProfileIdentities,
                    existing?.BillingProfileId,
                    existing?.TenantCode) ||
                IsBlockedRentalProfileIdentity(
                    blockedProfileIdentities,
                    existing?.LastBillingProfileId,
                    existing?.TenantCode);
            if (!referencesBlockedProfile)
            {
                var resolvedProfile = await ResolveRentalAssetBillingProfileReferenceAsync(
                    dto,
                    cancellationToken);
                resolvedProfile ??= await ResolveRentalAssetBillingProfileReferenceByFieldsAsync(
                    dto,
                    cancellationToken);
                referencesBlockedProfile = resolvedProfile is not null &&
                    IsBlockedRentalProfileIdentity(
                        blockedProfileIdentities,
                        resolvedProfile.Id,
                        resolvedProfile.TenantCode);
            }

            if (!referencesBlockedProfile)
                continue;

            AddClientConflict(dto, nameof(RentalAsset), conflictReason, result);
            rejectedAssets.Add(dto);
        }

        rentalAssets.RemoveAll(dto => rejectedAssets.Contains(dto));
        var incomingAssetScopesById =
            new Dictionary<Guid, RentalDependentOperationalScope>();
        foreach (var dto in rentalAssets)
        {
            if (dto.Id != Guid.Empty && effectiveAssetScopes.TryGetValue(dto, out var scope))
                incomingAssetScopesById[dto.Id] = scope;
        }

        var historyIds = assignmentHistories
            .Select(dto => dto.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var existingHistories = historyIds.Count == 0
            ? new Dictionary<Guid, RentalAssetAssignmentHistory>()
            : await _dbContext.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => historyIds.Contains(entity.Id))
                .ToDictionaryAsync(entity => entity.Id, cancellationToken);
        var rejectedHistories = new HashSet<RentalAssetAssignmentHistoryDto>(ReferenceEqualityComparer.Instance);
        foreach (var dto in assignmentHistories)
        {
            existingHistories.TryGetValue(dto.Id, out var existing);
            existingAssets.TryGetValue(dto.AssetId, out var existingAsset);
            RentalDependentOperationalScope? incomingAssetScope =
                incomingAssetScopesById.TryGetValue(
                    dto.AssetId,
                    out var resolvedIncomingAssetScope)
                    ? resolvedIncomingAssetScope
                    : null;
            var effectiveScope = ResolveRentalAssignmentHistoryDependentOperationalScope(
                dto,
                existing,
                incomingAssetScope,
                existingAsset);
            if (!IsBlockedRentalProfileIdentity(
                    blockedProfileIdentities,
                    dto.BillingProfileId,
                    effectiveScope.TenantCode) &&
                !IsBlockedRentalProfileIdentity(
                    blockedProfileIdentities,
                    existing?.BillingProfileId,
                    existing?.TenantCode))
            {
                continue;
            }

            AddClientConflict(dto, nameof(RentalAssetAssignmentHistory), conflictReason, result);
            rejectedHistories.Add(dto);
        }

        assignmentHistories.RemoveAll(dto => rejectedHistories.Contains(dto));

        var billingLogIds = billingLogs
            .Select(dto => dto.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var existingBillingLogs = billingLogIds.Count == 0
            ? new Dictionary<Guid, RentalBillingLog>()
            : await _dbContext.RentalBillingLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => billingLogIds.Contains(entity.Id))
                .ToDictionaryAsync(entity => entity.Id, cancellationToken);
        var billingLogProfileIds = billingLogs
            .Select(dto => dto.BillingProfileId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var billingLogProfiles = billingLogProfileIds.Count == 0
            ? new Dictionary<Guid, RentalBillingProfile>()
            : await _dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => billingLogProfileIds.Contains(profile.Id))
                .ToDictionaryAsync(profile => profile.Id, cancellationToken);
        var rejectedBillingLogs = new HashSet<RentalBillingLogDto>(ReferenceEqualityComparer.Instance);
        foreach (var dto in billingLogs)
        {
            existingBillingLogs.TryGetValue(dto.Id, out var existing);
            billingLogProfiles.TryGetValue(dto.BillingProfileId, out var profile);
            var effectiveScope = ResolveRentalBillingLogDependentOperationalScope(
                dto,
                existing,
                profile);
            if (!IsBlockedRentalProfileIdentity(
                    blockedProfileIdentities,
                    dto.BillingProfileId,
                    effectiveScope.TenantCode) &&
                !IsBlockedRentalProfileIdentity(
                    blockedProfileIdentities,
                    existing?.BillingProfileId,
                    existing?.TenantCode))
            {
                continue;
            }

            AddClientConflict(dto, nameof(RentalBillingLog), conflictReason, result);
            rejectedBillingLogs.Add(dto);
        }

        billingLogs.RemoveAll(dto => rejectedBillingLogs.Contains(dto));

        var rejectedPayments = new HashSet<PaymentDto>(ReferenceEqualityComparer.Instance);
        foreach (var dto in payments)
        {
            existingPayments.TryGetValue(dto.Id, out var existingPayment);
            var referencesBlockedProfile =
                InvoiceReferencesBlocked(dto.InvoiceId) ||
                InvoiceReferencesBlocked(existingPayment?.InvoiceId);
            if (!referencesBlockedProfile &&
                transactionProfileIdentitiesById.TryGetValue(
                    dto.Id,
                    out var transactionProfileIdentities))
            {
                referencesBlockedProfile =
                    transactionProfileIdentities.Overlaps(
                        blockedProfileIdentities);
            }

            if (!referencesBlockedProfile)
                continue;

            AddClientConflict(dto, nameof(Payment), conflictReason, result);
            rejectedPayments.Add(dto);
        }

        payments.RemoveAll(dto => rejectedPayments.Contains(dto));
    }

    private async Task<HashSet<Guid>> FindRentalAssetDeleteIdsReferencedByActiveProfilesAsync(
        IReadOnlyCollection<Guid> requestedDeleteIds,
        CancellationToken cancellationToken)
    {
        if (requestedDeleteIds.Count == 0)
            return [];

        var requestedDeleteIdSet = requestedDeleteIds.ToHashSet();
        var protectedAssetIds = new HashSet<Guid>();
        var activeProfileTemplates = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => !profile.IsDeleted && profile.IsActive)
            .Select(profile => profile.BillingTemplateJson)
            .ToListAsync(cancellationToken);
        foreach (var templateJson in activeProfileTemplates)
        {
            if (RentalBillingTemplateAssetCoverageRules.TryGetExplicitIncludedAssetIds(
                    templateJson,
                    out var includedAssetIds,
                    out _))
            {
                protectedAssetIds.UnionWith(
                    includedAssetIds.Where(requestedDeleteIdSet.Contains));
            }
            else
            {
                if (!TryExtractIncludedAssetIdsFromParseableTemplate(
                        templateJson,
                        out var recoverableIncludedAssetIds))
                {
                    protectedAssetIds.UnionWith(requestedDeleteIdSet);
                }
                else
                {
                    protectedAssetIds.UnionWith(
                        recoverableIncludedAssetIds.Where(requestedDeleteIdSet.Contains));
                }
            }

            if (protectedAssetIds.Count == requestedDeleteIdSet.Count)
                break;
        }

        return protectedAssetIds;
    }

    private static bool TryExtractIncludedAssetIdsFromParseableTemplate(
        string? templateJson,
        out HashSet<Guid> includedAssetIds)
    {
        includedAssetIds = [];
        if (string.IsNullOrWhiteSpace(templateJson))
            return true;

        try
        {
            using var document = JsonDocument.Parse(templateJson);
            if (CollectIncludedAssetIds(document.RootElement, includedAssetIds))
                return true;

            includedAssetIds = [];
            return false;
        }
        catch (JsonException)
        {
            includedAssetIds = [];
            return false;
        }

        static bool CollectIncludedAssetIds(JsonElement element, ISet<Guid> destination)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var recoveryComplete = true;
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(
                            property.Name,
                            "IncludedAssetIds",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (!CollectGuidValues(property.Value, destination))
                            recoveryComplete = false;
                    }
                    else
                    {
                        if (!CollectIncludedAssetIds(property.Value, destination))
                            recoveryComplete = false;
                    }
                }

                return recoveryComplete;
            }

            if (element.ValueKind != JsonValueKind.Array)
                return true;

            var arrayRecoveryComplete = true;
            foreach (var item in element.EnumerateArray())
            {
                if (!CollectIncludedAssetIds(item, destination))
                    arrayRecoveryComplete = false;
            }

            return arrayRecoveryComplete;
        }

        static bool CollectGuidValues(JsonElement element, ISet<Guid> destination)
        {
            if (element.ValueKind == JsonValueKind.Null)
                return true;

            if (element.ValueKind == JsonValueKind.String)
            {
                if (!Guid.TryParse(element.GetString(), out var assetId))
                    return false;
                if (assetId != Guid.Empty)
                    destination.Add(assetId);
                return true;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                var hasProperties = false;
                var objectRecoveryComplete = true;
                foreach (var property in element.EnumerateObject())
                {
                    hasProperties = true;
                    if (!CollectGuidValues(property.Value, destination))
                        objectRecoveryComplete = false;
                }

                return hasProperties && objectRecoveryComplete;
            }

            if (element.ValueKind != JsonValueKind.Array)
                return false;

            var arrayRecoveryComplete = true;
            foreach (var item in element.EnumerateArray())
            {
                if (!CollectGuidValues(item, destination))
                    arrayRecoveryComplete = false;
            }

            return arrayRecoveryComplete;
        }
    }

    private async Task<List<RentalAssetDto>> FilterValidRentalAssetsAsync(
        IEnumerable<RentalAssetDto> payload,
        IReadOnlyDictionary<Guid, Guid> resolvedRentalProfileIds,
        SyncPushResult result,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        const string referencedAssetDeleteConflictMessage =
            "활성 청구 프로필의 자산 구성에 포함된 렌탈 자산은 삭제할 수 없습니다. 청구 프로필에서 자산 참조를 먼저 제거하고 동기화한 뒤 다시 삭제하세요.";
        var payloadRows = payload as IReadOnlyCollection<RentalAssetDto> ?? payload.ToList();
        var referencedAssetDeleteIds = await FindRentalAssetDeleteIdsReferencedByActiveProfilesAsync(
            payloadRows
                .Where(asset => asset.IsDeleted && asset.Id != Guid.Empty)
                .Select(asset => asset.Id)
                .Distinct()
                .ToList(),
            cancellationToken);
        var valid = new List<RentalAssetDto>();

        foreach (var dto in payloadRows)
        {
            if (dto.IsDeleted && referencedAssetDeleteIds.Contains(dto.Id))
            {
                AddClientConflict(
                    dto,
                    nameof(RentalAsset),
                    referencedAssetDeleteConflictMessage,
                    result);
                continue;
            }

            if (dto.BillingProfileId.HasValue &&
                dto.BillingProfileId.Value != Guid.Empty &&
                resolvedRentalProfileIds.TryGetValue(dto.BillingProfileId.Value, out var remappedBillingProfileId))
            {
                dto.BillingProfileId = remappedBillingProfileId;
            }

            var requestedCustomerId = dto.CustomerId;
            dto.CustomerId = await ResolveRentalAssetCustomerReferenceAsync(dto, cancellationToken, pushSnapshot);
            if (!await ValidateExplicitRentalCustomerReferenceAsync(
                    dto,
                    nameof(RentalAsset),
                    requestedCustomerId,
                    dto.CustomerId,
                    result,
                    cancellationToken,
                    pushSnapshot))
            {
                continue;
            }

            dto.ItemId = await ResolveRentalAssetItemReferenceAsync(dto, cancellationToken, pushSnapshot);
            var linkedCustomer = await GetRentalReferenceCustomerAsync(dto.CustomerId, cancellationToken, pushSnapshot);
            if (linkedCustomer is not null)
                ApplyRentalAssetLinkedCustomerSnapshot(dto, linkedCustomer);

            var existingAsset = dto.Id == Guid.Empty
                ? null
                : pushSnapshot is null
                    ? await _dbContext.RentalAssets
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(asset => asset.Id == dto.Id, cancellationToken)
                    : pushSnapshot.FindAsset(dto.Id);
            RentalBillingProfile? billingProfile = null;
            if (dto.BillingProfileId.HasValue && dto.BillingProfileId.Value != Guid.Empty)
            {
                var requestedBillingProfileId = dto.BillingProfileId.Value;
                billingProfile = await ResolveRentalAssetBillingProfileReferenceAsync(dto, cancellationToken, pushSnapshot);
                if (billingProfile is null || billingProfile.IsDeleted)
                {
                    billingProfile = await ResolveRentalAssetBillingProfileReferenceByFieldsAsync(dto, cancellationToken, pushSnapshot: pushSnapshot);
                    if (billingProfile is null || billingProfile.IsDeleted)
                    {
                        AddClientConflict(dto, nameof(RentalAsset),
                            $"Referenced rental billing profile was not found: {requestedBillingProfileId}.", result);
                        continue;
                    }
                }

                if (!RentalBillingTemplateAssetCoverageRules.AllowsLink(
                        billingProfile.BillingTemplateJson,
                        dto.Id))
                {
                    AddClientConflict(
                        dto,
                        nameof(RentalAsset),
                        RentalBillingTemplateAssetCoverageRules.ExplicitCoverageConflictMessage,
                        result);
                    continue;
                }

                dto.BillingProfileId = billingProfile.Id;
            }
            else
            {
                if (existingAsset?.BillingProfileId is not Guid existingBillingProfileId ||
                    existingBillingProfileId == Guid.Empty)
                {
                    billingProfile = await ResolveRentalAssetBillingProfileReferenceByFieldsAsync(
                        dto,
                        cancellationToken,
                        requireTemplateCoverage: true,
                        pushSnapshot: pushSnapshot);
                }
                if (billingProfile is not null)
                    dto.BillingProfileId = billingProfile.Id;
            }

            var previousBillingProfileId = existingAsset?.BillingProfileId is Guid persistedBillingProfileId &&
                                           persistedBillingProfileId != Guid.Empty
                ? persistedBillingProfileId
                : (Guid?)null;
            var nextBillingProfileId = billingProfile?.Id is Guid resolvedBillingProfileId &&
                                       resolvedBillingProfileId != Guid.Empty
                ? resolvedBillingProfileId
                : (Guid?)null;
            if (previousBillingProfileId.HasValue && previousBillingProfileId != nextBillingProfileId)
            {
                var previousBillingProfile = pushSnapshot is null
                    ? await _dbContext.RentalBillingProfiles
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            profile => profile.Id == previousBillingProfileId.Value && !profile.IsDeleted,
                            cancellationToken)
                    : pushSnapshot.FindProfile(previousBillingProfileId.Value) is { IsDeleted: false } activeProfile
                        ? activeProfile
                        : null;
                var previousCoverage = RentalBillingTemplateAssetCoverageRules.Evaluate(
                    previousBillingProfile?.BillingTemplateJson,
                    dto.Id);
                if (previousCoverage is RentalBillingTemplateAssetCoverage.UniqueReference or
                    RentalBillingTemplateAssetCoverage.AmbiguousReference or
                    RentalBillingTemplateAssetCoverage.MalformedTemplate)
                {
                    AddClientConflict(
                        dto,
                        nameof(RentalAsset),
                        "기존 청구 프로필의 명시적 자산 구성에서 먼저 이 렌탈 자산을 제외해야 합니다. 청구관리에서 자산 포함 항목을 변경한 뒤 다시 동기화하세요.",
                        result);
                    continue;
                }
            }

            if (billingProfile is not null)
            {
                if (!_officeScopeService.CanWriteOfficeForRentals(billingProfile.ResponsibleOfficeCode, billingProfile.TenantCode, billingProfile.OfficeCode))
                {
                    AddClientConflict(dto, nameof(RentalAsset),
                        $"Referenced rental billing profile is outside the writable office scope: {billingProfile.Id}.", result);
                    continue;
                }

                if (billingProfile.CustomerId.HasValue && billingProfile.CustomerId.Value != Guid.Empty)
                    dto.CustomerId = billingProfile.CustomerId.Value;

                dto.TenantCode = _officeScopeService.ResolveTenantForRentalCreate(
                    dto.TenantCode,
                    billingProfile.OfficeCode,
                    billingProfile.TenantCode,
                    billingProfile.OfficeCode);
                dto.ResponsibleOfficeCode = billingProfile.ResponsibleOfficeCode;
                dto.OfficeCode = billingProfile.OfficeCode;
                dto.ManagementCompanyCode = billingProfile.OfficeCode;
            }

            linkedCustomer = await GetRentalReferenceCustomerAsync(dto.CustomerId, cancellationToken, pushSnapshot);
            if (linkedCustomer is not null)
                ApplyRentalAssetLinkedCustomerSnapshot(dto, linkedCustomer);

            dto.AssetKey = BuildRentalAssetKey(
                dto.ManagementCompanyCode,
                dto.ManagementNumber,
                dto.ManagementId,
                dto.MachineNumber,
                dto.CustomerName,
                dto.ItemName);
            valid.Add(dto);
        }

        return valid;
    }

    private void ApplyRentalAssetLinkedCustomerSnapshot(RentalAssetDto dto, Customer linkedCustomer)
    {
        var resolvedResponsibleOfficeCode = ResolveRentalCustomerOfficeCode(linkedCustomer.ResponsibleOfficeCode);
        var resolvedOwnerOfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            linkedCustomer.OfficeCode,
            resolvedResponsibleOfficeCode,
            linkedCustomer.OfficeCode);
        dto.TenantCode = _officeScopeService.ResolveTenantForRentalCreate(
            dto.TenantCode,
            resolvedOwnerOfficeCode,
            linkedCustomer.TenantCode,
            linkedCustomer.OfficeCode);
        dto.ResponsibleOfficeCode = resolvedResponsibleOfficeCode;
        dto.OfficeCode = resolvedOwnerOfficeCode;
        dto.ManagementCompanyCode = resolvedOwnerOfficeCode;
        var normalizedCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(linkedCustomer.NameOriginal);
        dto.CustomerName = normalizedCustomerName;
        dto.CurrentCustomerName = normalizedCustomerName;
    }

    private async Task<RentalBillingProfile?> ResolveRentalAssetBillingProfileReferenceAsync(
        RentalAssetDto dto,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        if (!dto.BillingProfileId.HasValue || dto.BillingProfileId.Value == Guid.Empty)
            return null;

        var preferredOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(dto.ResponsibleOfficeCode, dto.OfficeCode);
        var preferredTenantCode = _officeScopeService.ResolveTenantForRentalCreate(dto.TenantCode, preferredOfficeCode);
        var direct = pushSnapshot is null
            ? await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.BillingProfileId.Value, cancellationToken)
            : pushSnapshot.FindProfile(dto.BillingProfileId.Value);
        if (direct is not null &&
            !direct.IsDeleted &&
            RentalBillingProfileMatchesRentalAssetScope(direct, preferredOfficeCode, preferredTenantCode) &&
            (RentalBillingProfileMatchesRentalAssetReference(direct, dto) ||
             !direct.CustomerId.HasValue ||
             direct.CustomerId.Value == Guid.Empty))
            return direct;

        var existingAsset = pushSnapshot is null
            ? await _dbContext.RentalAssets.IgnoreQueryFilters()
                .FirstOrDefaultAsync(asset => asset.Id == dto.Id, cancellationToken)
            : pushSnapshot.FindAsset(dto.Id);
        if (existingAsset?.BillingProfileId is Guid existingBillingProfileId)
        {
            var fromExistingAsset = pushSnapshot is null
                ? await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == existingBillingProfileId, cancellationToken)
                : pushSnapshot.FindProfile(existingBillingProfileId);
            if (fromExistingAsset is not null &&
                !fromExistingAsset.IsDeleted &&
                RentalBillingProfileMatchesRentalAssetScope(fromExistingAsset, preferredOfficeCode, preferredTenantCode) &&
                (RentalBillingProfileMatchesRentalAssetReference(fromExistingAsset, dto) ||
                 !fromExistingAsset.CustomerId.HasValue ||
                 fromExistingAsset.CustomerId.Value == Guid.Empty))
                return fromExistingAsset;
        }

        return null;
    }

    private async Task<RentalBillingProfile?> ResolveRentalAssetBillingProfileReferenceByFieldsAsync(
        RentalAssetDto dto,
        CancellationToken cancellationToken,
        bool requireTemplateCoverage = false,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var resolvedCustomerId = dto.CustomerId;
        if (!resolvedCustomerId.HasValue || resolvedCustomerId.Value == Guid.Empty)
        {
            resolvedCustomerId = await ResolveRentalAssetCustomerReferenceAsync(dto, cancellationToken, pushSnapshot);
        }

        if (!resolvedCustomerId.HasValue || resolvedCustomerId.Value == Guid.Empty)
            return null;

        var preferredOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(dto.ResponsibleOfficeCode, null);
        var preferredTenantCode = _officeScopeService.ResolveTenantForRentalCreate(dto.TenantCode, preferredOfficeCode);
        var customerKeys = BuildRentalCustomerKeys(dto.CustomerName, dto.CurrentCustomerName);
        var candidates = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .Where(profile => !profile.IsDeleted)
            .ToListAsync(cancellationToken);
        candidates = candidates
            .Where(profile => _officeScopeService.CanReadOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode))
            .ToList();
        if (requireTemplateCoverage)
        {
            candidates = candidates
                .Where(profile => RentalBillingTemplateAssetCoverageRules.AllowsLink(
                    profile.BillingTemplateJson,
                    dto.Id))
                .ToList();
        }
        var scopedCandidates = candidates
            .Where(profile => RentalBillingProfileMatchesRentalAssetScope(profile, preferredOfficeCode, preferredTenantCode))
            .ToList();
        if (scopedCandidates.Count > 0)
            candidates = scopedCandidates;

        var customerIdMatches = candidates
            .Where(profile => profile.CustomerId == resolvedCustomerId.Value)
            .ToList();
        if (customerIdMatches.Count > 0)
        {
            candidates = customerIdMatches;
        }
        else if (customerKeys.Count > 0)
        {
            var nameMatches = candidates
                .Where(profile => ProfileMatchesRentalNames(profile, customerKeys))
                .ToList();
            if (nameMatches.Count > 0)
                candidates = nameMatches;
        }

        if (candidates.Count == 0)
            return null;

        var normalizedItemKey = RentalCatalogValueNormalizer.NormalizeLooseKey(dto.ItemName);
        var siteKeys = BuildRentalSiteKeys(dto.InstallLocation, dto.InstallSiteName);

        if (!string.IsNullOrWhiteSpace(normalizedItemKey))
        {
            var itemMatches = candidates
                .Where(profile => ProfileMatchesRentalAssetItem(profile, normalizedItemKey))
                .ToList();

            if (siteKeys.Count > 0)
            {
                var strictMatches = itemMatches
                    .Where(profile => ProfileMatchesRentalAssetSite(profile, siteKeys))
                    .ToList();
                if (strictMatches.Count == 1)
                    return strictMatches[0];
            }

            if (itemMatches.Count == 1)
                return itemMatches[0];
        }

        if (siteKeys.Count > 0)
        {
            var siteMatches = candidates
                .Where(profile => ProfileMatchesRentalAssetSite(profile, siteKeys))
                .ToList();
            if (siteMatches.Count == 1)
                return siteMatches[0];
        }

        return null;
    }

    private async Task<Guid?> ResolveRentalAssetItemReferenceAsync(
        RentalAssetDto dto,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var preferredOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeLoose(
            dto.OfficeCode,
            dto.ManagementCompanyCode,
            OfficeCodeCatalog.Shared);
        var preferredTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            dto.TenantCode,
            preferredOfficeCode);

        if (dto.ItemId.HasValue && dto.ItemId.Value != Guid.Empty)
        {
            var directItem = pushSnapshot is null
                ? await _dbContext.Items.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(item => item.Id == dto.ItemId.Value, cancellationToken)
                : pushSnapshot.FindItem(dto.ItemId);
            if (directItem is not null &&
                !directItem.IsDeleted &&
                ItemOperationalPolicy.IsAsset(directItem.TrackingType) &&
                CanReadItemForRentalReference(directItem))
            {
                return directItem.Id;
            }
        }

        var normalizedMaterialNumber = (dto.ManagementNumber ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedMaterialNumber))
        {
            var materialMatches = await _dbContext.Items.IgnoreQueryFilters()
                .Where(item =>
                    !item.IsDeleted &&
                    item.MaterialNumber == normalizedMaterialNumber)
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ToListAsync(cancellationToken);
            materialMatches = materialMatches
                .Where(item => ItemOperationalPolicy.IsAsset(item.TrackingType))
                .ToList();
            var resolvedMaterialMatch = ResolveReadableItemReference(
                materialMatches,
                preferredOfficeCode,
                preferredTenantCode);
            if (resolvedMaterialMatch.HasValue)
                return resolvedMaterialMatch.Value;
        }

        var normalizedMachineNumber = (dto.MachineNumber ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedMachineNumber))
        {
            var serialMatches = await _dbContext.Items.IgnoreQueryFilters()
                .Where(item =>
                    !item.IsDeleted &&
                    item.SerialNumber == normalizedMachineNumber)
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ToListAsync(cancellationToken);
            serialMatches = serialMatches
                .Where(item => ItemOperationalPolicy.IsAsset(item.TrackingType))
                .ToList();
            var resolvedSerialMatch = ResolveReadableItemReference(
                serialMatches,
                preferredOfficeCode,
                preferredTenantCode);
            if (resolvedSerialMatch.HasValue)
                return resolvedSerialMatch.Value;
        }

        var normalizedItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(dto.ItemName);
        if (string.IsNullOrWhiteSpace(normalizedItemName))
            return null;

        var exactNameMatches = await _dbContext.Items.IgnoreQueryFilters()
            .Where(item =>
                !item.IsDeleted &&
                item.NameOriginal == normalizedItemName)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        exactNameMatches = exactNameMatches
            .Where(item => ItemOperationalPolicy.IsAsset(item.TrackingType))
            .ToList();
        var resolvedExactNameMatch = ResolveReadableItemReference(
            exactNameMatches,
            preferredOfficeCode,
            preferredTenantCode);
        if (resolvedExactNameMatch.HasValue)
            return resolvedExactNameMatch.Value;

        var normalizedNameKey = MatchKeyNormalizer.Normalize(normalizedItemName);
        if (string.IsNullOrWhiteSpace(normalizedNameKey))
            return null;

        var nameKeyMatches = await _dbContext.Items.IgnoreQueryFilters()
            .Where(item =>
                !item.IsDeleted &&
                item.NameMatchKey == normalizedNameKey)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        nameKeyMatches = nameKeyMatches
            .Where(item => ItemOperationalPolicy.IsAsset(item.TrackingType))
            .ToList();
        return ResolveReadableItemReference(nameKeyMatches, preferredOfficeCode, preferredTenantCode);
    }

    private async Task<Guid?> ResolveRentalAssetCustomerReferenceAsync(
        RentalAssetDto dto,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var candidateKeys = BuildRentalCustomerReferenceKeys(
            dto.CustomerName,
            dto.CurrentCustomerName);
        var preferredOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(dto.ResponsibleOfficeCode, null);
        var preferredTenantCode = _officeScopeService.ResolveTenantForRentalCreate(dto.TenantCode, preferredOfficeCode);
        if (dto.CustomerId.HasValue && dto.CustomerId.Value != Guid.Empty)
        {
            var directCustomer = pushSnapshot is null
                ? await _dbContext.Customers.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(customer => customer.Id == dto.CustomerId.Value, cancellationToken)
                : pushSnapshot.FindCustomer(dto.CustomerId);
            if (directCustomer is not null &&
                !directCustomer.IsDeleted &&
                CanReadCustomerForRentalReference(directCustomer) &&
                CustomerReferenceTenantMatches(directCustomer, preferredTenantCode) &&
                (CustomerReferenceLooksValid(directCustomer, candidateKeys, null) ||
                 await ExistingRentalAssetUsesCustomerAsync(dto.Id, directCustomer.Id, cancellationToken, pushSnapshot)))
            {
                return directCustomer.Id;
            }
        }

        var candidateNames = new[]
            {
                dto.CustomerName,
                dto.CurrentCustomerName
            }
            .Select(current => (current ?? string.Empty).Trim())
            .Where(current => !string.IsNullOrWhiteSpace(current))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidateNames.Count == 0)
            return null;

        var exactNameMatches = pushSnapshot is null
            ? await _dbContext.Customers.IgnoreQueryFilters()
                .Where(customer => !customer.IsDeleted && candidateNames.Contains(customer.NameOriginal))
                .OrderByDescending(customer => customer.UpdatedAtUtc)
                .ToListAsync(cancellationToken)
            : pushSnapshot.ActiveCustomers
                .Where(customer => candidateNames.Contains(customer.NameOriginal))
                .OrderByDescending(customer => customer.UpdatedAtUtc)
                .ToList();
        var resolvedExactNameMatch = ResolveReadableCustomerReference(
            exactNameMatches,
            preferredOfficeCode,
            preferredTenantCode);
        if (resolvedExactNameMatch.HasValue)
            return resolvedExactNameMatch.Value;

        var normalizedMatchKeys = candidateNames
            .Select(MatchKeyNormalizer.Normalize)
            .Where(current => !string.IsNullOrWhiteSpace(current))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedMatchKeys.Count == 0)
            return null;

        var nameKeyMatches = pushSnapshot is null
            ? await _dbContext.Customers.IgnoreQueryFilters()
                .Where(customer => !customer.IsDeleted && normalizedMatchKeys.Contains(customer.NameMatchKey))
                .OrderByDescending(customer => customer.UpdatedAtUtc)
                .ToListAsync(cancellationToken)
            : pushSnapshot.ActiveCustomers
                .Where(customer => normalizedMatchKeys.Contains(customer.NameMatchKey))
                .OrderByDescending(customer => customer.UpdatedAtUtc)
                .ToList();
        return ResolveReadableCustomerReference(nameKeyMatches, preferredOfficeCode, preferredTenantCode);
    }

    private async Task<Customer?> GetRentalReferenceCustomerAsync(
        Guid? customerId,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        if (!customerId.HasValue || customerId.Value == Guid.Empty)
            return null;

        var customer = pushSnapshot is null
            ? await _dbContext.Customers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == customerId.Value, cancellationToken)
            : pushSnapshot.FindCustomer(customerId);
        return customer is not null && !customer.IsDeleted && CanReadCustomerForRentalReference(customer)
            ? customer
            : null;
    }

    private async Task<bool> ValidateExplicitRentalCustomerReferenceAsync(
        SyncEntityDto dto,
        string entityName,
        Guid? requestedCustomerId,
        Guid? resolvedCustomerId,
        SyncPushResult result,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        if (!requestedCustomerId.HasValue ||
            requestedCustomerId.Value == Guid.Empty ||
            resolvedCustomerId.HasValue)
        {
            return true;
        }

        var requestedCustomer = pushSnapshot is null
            ? await _dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(customer => customer.Id == requestedCustomerId.Value, cancellationToken)
            : pushSnapshot.FindCustomer(requestedCustomerId);
        if (requestedCustomer is null || requestedCustomer.IsDeleted)
            return true;

        if (CanReadCustomerForRentalReference(requestedCustomer))
            return true;

        AddClientConflict(
            dto,
            entityName,
            $"Referenced customer is outside the readable office scope: {requestedCustomerId.Value}.",
            result);
        return false;
    }

    private async Task<bool> ExistingRentalBillingProfileUsesCustomerAsync(
        Guid profileId,
        Guid customerId,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        if (profileId == Guid.Empty || customerId == Guid.Empty)
            return false;

        if (pushSnapshot is not null)
        {
            var profile = pushSnapshot.FindProfile(profileId);
            return profile is not null &&
                   !profile.IsDeleted &&
                   profile.CustomerId == customerId;
        }

        return await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AnyAsync(profile =>
                profile.Id == profileId &&
                !profile.IsDeleted &&
                profile.CustomerId == customerId,
                cancellationToken);
    }

    private async Task<bool> ExistingRentalAssetUsesCustomerAsync(
        Guid assetId,
        Guid customerId,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        if (assetId == Guid.Empty || customerId == Guid.Empty)
            return false;

        if (pushSnapshot is not null)
            return pushSnapshot.ExistingAssetUsesCustomer(assetId, customerId);

        return await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .AnyAsync(asset =>
                asset.Id == assetId &&
                !asset.IsDeleted &&
                asset.CustomerId == customerId,
                cancellationToken);
    }

    private static string ResolveRentalCustomerOfficeCode(string? officeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeLoose(officeCode, null, OfficeCodeCatalog.Usenet);

    private static List<string> BuildRentalCustomerKeys(params string?[] values)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            foreach (var variant in EnumerateRentalNameVariants(value))
            {
                var normalized = RentalCatalogValueNormalizer.NormalizeLooseKey(variant);
                if (!string.IsNullOrWhiteSpace(normalized))
                    keys.Add(normalized);
            }
        }

        return [.. keys];
    }

    private static List<string> BuildRentalCustomerReferenceKeys(params string?[] values)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            foreach (var variant in EnumerateStrictRentalNameVariants(value))
            {
                var normalized = RentalCatalogValueNormalizer.NormalizeLooseKey(variant);
                if (!string.IsNullOrWhiteSpace(normalized))
                    keys.Add(normalized);
            }
        }

        return [.. keys];
    }

    private static IEnumerable<string> EnumerateRentalNameVariants(string? value)
    {
        var display = RentalCatalogValueNormalizer.NormalizeDisplayText(value);
        if (string.IsNullOrWhiteSpace(display))
            yield break;

        yield return display;

        var openBracket = display.IndexOf('[');
        var closeBracket = openBracket >= 0 ? display.IndexOf(']', openBracket + 1) : -1;
        if (openBracket < 0 || closeBracket <= openBracket)
            yield break;

        var prefix = openBracket == 0
            ? display[(openBracket + 1)..closeBracket].Trim()
            : display[..openBracket].Trim();
        var suffix = openBracket == 0
            ? display[(closeBracket + 1)..].Trim()
            : display[(openBracket + 1)..closeBracket].Trim();

        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(suffix))
            yield break;

        yield return prefix;
        yield return prefix + suffix;
        yield return suffix + prefix;
    }

    private static IEnumerable<string> EnumerateStrictRentalNameVariants(string? value)
    {
        var display = RentalCatalogValueNormalizer.NormalizeDisplayText(value);
        if (string.IsNullOrWhiteSpace(display))
            yield break;

        yield return display;

        var normalizedBracketDisplay = display
            .Replace('｛', '[')
            .Replace('｝', ']')
            .Replace('{', '[')
            .Replace('}', ']')
            .Trim();
        if (!string.Equals(normalizedBracketDisplay, display, StringComparison.Ordinal))
            yield return normalizedBracketDisplay;
    }

    private static bool CustomerReferenceLooksValid(
        Customer customer,
        IReadOnlyCollection<string> candidateKeys,
        string? normalizedBusinessNumber)
    {
        if (!CustomerBusinessNumberLooksValid(customer, normalizedBusinessNumber))
            return false;

        return candidateKeys.Count == 0 || CustomerMatchesRentalReferenceNames(customer, candidateKeys);
    }

    private static bool CustomerReferenceTenantMatches(Customer customer, string preferredTenantCode)
        => string.IsNullOrWhiteSpace(preferredTenantCode) ||
           string.Equals(customer.TenantCode, preferredTenantCode, StringComparison.OrdinalIgnoreCase);

    private static bool CustomerBusinessNumberLooksValid(Customer customer, string? normalizedBusinessNumber)
    {
        if (string.IsNullOrWhiteSpace(normalizedBusinessNumber))
            return true;

        var customerBusinessNumber = NormalizeBusinessNumber(customer.BusinessNumber);
        return string.IsNullOrWhiteSpace(customerBusinessNumber) ||
               string.Equals(customerBusinessNumber, normalizedBusinessNumber, StringComparison.Ordinal);
    }

    private static bool CustomerMatchesRentalNames(Customer customer, IReadOnlyCollection<string> candidateKeys)
    {
        if (candidateKeys.Count == 0)
            return true;

        var customerKeys = BuildRentalCustomerKeys(customer.NameOriginal);
        return customerKeys.Any(customerKey =>
            candidateKeys.Any(candidateKey =>
                !string.IsNullOrWhiteSpace(customerKey) &&
                !string.IsNullOrWhiteSpace(candidateKey) &&
                string.Equals(customerKey, candidateKey, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool CustomerMatchesRentalReferenceNames(Customer customer, IReadOnlyCollection<string> candidateKeys)
    {
        if (candidateKeys.Count == 0)
            return true;

        var customerKeys = BuildRentalCustomerReferenceKeys(customer.NameOriginal);
        var customerMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(customer.NameMatchKey);
        if (!string.IsNullOrWhiteSpace(customerMatchKey))
            customerKeys.Add(customerMatchKey);

        return customerKeys.Any(customerKey =>
            candidateKeys.Any(candidateKey =>
                !string.IsNullOrWhiteSpace(customerKey) &&
                !string.IsNullOrWhiteSpace(candidateKey) &&
                string.Equals(customerKey, candidateKey, StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeBusinessNumber(string? businessNumber)
        => new string((businessNumber ?? string.Empty).Where(char.IsDigit).ToArray());

    private static HashSet<string> BuildRentalSiteKeys(params string?[] values)
        => values
            .Select(RentalCatalogValueNormalizer.NormalizeLooseKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool ProfileMatchesRentalAssetItem(RentalBillingProfile profile, string normalizedItemKey)
    {
        var profileItemKey = RentalCatalogValueNormalizer.NormalizeLooseKey(profile.ItemName);
        if (string.IsNullOrWhiteSpace(profileItemKey) || string.IsNullOrWhiteSpace(normalizedItemKey))
            return false;

        return string.Equals(profileItemKey, normalizedItemKey, StringComparison.OrdinalIgnoreCase)
               || profileItemKey.Contains(normalizedItemKey, StringComparison.OrdinalIgnoreCase)
               || normalizedItemKey.Contains(profileItemKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProfileMatchesRentalAssetSite(RentalBillingProfile profile, IReadOnlyCollection<string> siteKeys)
    {
        if (siteKeys.Count == 0)
            return false;

        var profileSiteKey = RentalCatalogValueNormalizer.NormalizeLooseKey(profile.InstallSiteName);
        return !string.IsNullOrWhiteSpace(profileSiteKey) &&
               siteKeys.Contains(profileSiteKey, StringComparer.OrdinalIgnoreCase);
    }

    private bool RentalBillingProfileMatchesRentalAssetReference(
        RentalBillingProfile profile,
        RentalAssetDto dto)
    {
        var preferredOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(dto.ResponsibleOfficeCode, null);
        var preferredTenantCode = _officeScopeService.ResolveTenantForRentalCreate(dto.TenantCode, preferredOfficeCode);
        if (!RentalBillingProfileMatchesRentalAssetScope(profile, preferredOfficeCode, preferredTenantCode))
            return false;

        if (dto.CustomerId.HasValue && dto.CustomerId.Value != Guid.Empty)
        {
            if (profile.CustomerId == dto.CustomerId.Value)
                return true;

            if (profile.CustomerId.HasValue && profile.CustomerId.Value != Guid.Empty)
                return false;
        }

        var candidateKeys = BuildRentalCustomerKeys(dto.CustomerName, dto.CurrentCustomerName);
        return candidateKeys.Count == 0 || ProfileMatchesRentalNames(profile, candidateKeys);
    }

    private static bool RentalBillingProfileMatchesRentalAssetScope(
        RentalBillingProfile profile,
        string preferredOfficeCode,
        string preferredTenantCode)
    {
        var profileOfficeCode = ResolveRentalCustomerOfficeCode(profile.ResponsibleOfficeCode);
        var profileTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            profile.TenantCode,
            profile.OfficeCode,
            profile.TenantCode,
            profile.ResponsibleOfficeCode);

        return string.Equals(profileOfficeCode, preferredOfficeCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(profileTenantCode, preferredTenantCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProfileMatchesRentalNames(
        RentalBillingProfile profile,
        IReadOnlyCollection<string> candidateKeys)
    {
        if (candidateKeys.Count == 0)
            return true;

        var profileKeys = BuildRentalCustomerKeys(profile.CustomerName);
        return profileKeys.Any(profileKey =>
            candidateKeys.Any(candidateKey =>
                !string.IsNullOrWhiteSpace(profileKey) &&
                !string.IsNullOrWhiteSpace(candidateKey) &&
                string.Equals(profileKey, candidateKey, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsDistinctBillingCustomerAlias(string? profileCustomerName, string? linkedCustomerName)
    {
        var profileNameKey = RentalDuplicateNormalizer.NormalizeProfileKeyPart(profileCustomerName);
        var linkedNameKey = RentalDuplicateNormalizer.NormalizeProfileKeyPart(linkedCustomerName);
        return !string.IsNullOrWhiteSpace(profileNameKey) &&
               !string.IsNullOrWhiteSpace(linkedNameKey) &&
               !string.Equals(profileNameKey, linkedNameKey, StringComparison.OrdinalIgnoreCase);
    }

    private Guid? ResolveReadableItemReference(
        IReadOnlyCollection<Item> candidates,
        string preferredOfficeCode,
        string preferredTenantCode)
    {
        var readableCandidates = candidates
            .Where(CanReadItemForRentalReference)
            .ToList();
        if (readableCandidates.Count == 0)
            return null;

        var preferredCandidates = readableCandidates
            .Where(item =>
                string.Equals(
                    OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(item.OfficeCode, OfficeCodeCatalog.Shared),
                    preferredOfficeCode,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                        item.TenantCode,
                        item.OfficeCode,
                        preferredTenantCode,
                        preferredOfficeCode),
                    preferredTenantCode,
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToList();
        if (preferredCandidates.Count == 1)
            return preferredCandidates[0].Id;

        if (readableCandidates.Count == 1)
            return readableCandidates[0].Id;

        return null;
    }

    private Guid? ResolveReadableCustomerReference(
        IReadOnlyCollection<Customer> candidates,
        string preferredOfficeCode,
        string preferredTenantCode)
    {
        var readableCandidates = candidates
            .Where(CanReadCustomerForRentalReference)
            .ToList();
        if (readableCandidates.Count == 0)
            return null;

        var tenantCandidates = readableCandidates
            .Where(customer => CustomerReferenceTenantMatches(customer, preferredTenantCode))
            .ToList();
        if (!string.IsNullOrWhiteSpace(preferredTenantCode) && tenantCandidates.Count == 0)
            return null;

        var preferredCandidates = readableCandidates
            .Where(customer =>
                CustomerReferenceTenantMatches(customer, preferredTenantCode) &&
                string.Equals(customer.ResponsibleOfficeCode, preferredOfficeCode, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(preferredTenantCode) ||
                 string.Equals(customer.TenantCode, preferredTenantCode, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (preferredCandidates.Count == 1)
            return preferredCandidates[0].Id;

        return tenantCandidates.Count == 1
            ? tenantCandidates[0].Id
            : null;
    }

    private bool CanReadItemForRentalReference(Item item)
        => _officeScopeService.CanReadOfficeForItems(item.OfficeCode, item.TenantCode) ||
           _officeScopeService.CanReadOfficeForRentals(item.OfficeCode, item.TenantCode);

    private bool CanReadCustomerForRentalReference(Customer customer)
        => _officeScopeService.CanReadOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode) ||
           _officeScopeService.CanReadOfficeForRentals(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode);

    private async Task<List<RentalBillingLogDto>> PrepareScopedRentalBillingLogsAsync(
        IEnumerable<RentalBillingLogDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<RentalBillingLogDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.RentalBillingLogs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForRentals(existing.ResponsibleOfficeCode, existing.TenantCode, existing.OfficeCode))
            {
                AddClientConflict(dto, nameof(RentalBillingLog), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.ResponsibleOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(
                dto.ResponsibleOfficeCode,
                existing?.ResponsibleOfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                existing?.OfficeCode);
            dto.TenantCode = _officeScopeService.ResolveTenantForRentalCreate(
                dto.TenantCode,
                dto.OfficeCode,
                existing?.TenantCode,
                existing?.OfficeCode);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<RentalBillingLogDto>> FilterValidRentalBillingLogsAsync(
        IEnumerable<RentalBillingLogDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<RentalBillingLogDto>();

        foreach (var dto in payload)
        {
            var billingProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.BillingProfileId, cancellationToken);
            if (dto.BillingProfileId == Guid.Empty || billingProfile is null || billingProfile.IsDeleted)
            {
                AddClientConflict(dto, nameof(RentalBillingLog),
                    $"Referenced rental billing profile was not found: {dto.BillingProfileId}.", result);
                continue;
            }

            if (!_officeScopeService.CanWriteOfficeForRentals(billingProfile.ResponsibleOfficeCode, billingProfile.TenantCode, billingProfile.OfficeCode))
            {
                AddClientConflict(dto, nameof(RentalBillingLog),
                    $"Referenced rental billing profile is outside the writable office scope: {dto.BillingProfileId}.", result);
                continue;
            }

            dto.ResponsibleOfficeCode = billingProfile.ResponsibleOfficeCode;
            dto.OfficeCode = billingProfile.OfficeCode;
            dto.TenantCode = _officeScopeService.ResolveTenantForRentalCreate(
                dto.TenantCode,
                billingProfile.OfficeCode,
                billingProfile.TenantCode,
                billingProfile.OfficeCode);
            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<InvoiceDto>> FilterValidInvoicesAsync(
        IEnumerable<InvoiceDto> payload, SyncPushResult result, CancellationToken cancellationToken)
    {
        var valid = new List<InvoiceDto>();

        foreach (var dto in payload)
        {
            var originalCustomerId = dto.CustomerId;
            var existing = await _dbContext.Invoices.IgnoreQueryFilters()
                .Include(x => x.Customer)
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            var requestedScopeCustomer =
                dto.CustomerId == Guid.Empty
                    ? null
                    : existing?.Customer?.Id == dto.CustomerId
                        ? existing.Customer
                        : await _dbContext.Customers
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .FirstOrDefaultAsync(
                                customer =>
                                    customer.Id ==
                                    dto.CustomerId,
                                cancellationToken);
            if (existing is null &&
                !IsInvoiceVersionScopeInternallyConsistent(
                    BuildInvoiceVersionScopeKey(
                        dto,
                        requestedScopeCustomer)))
            {
                AddClientConflict(
                    dto,
                    nameof(Invoice),
                    "Invoice tenant and office scope values are inconsistent.",
                    result);
                continue;
            }

            if (!await ValidateInvoiceDeletePaymentSideEffectPermissionAsync(dto, existing, result, cancellationToken))
                continue;
            if (existing is not null &&
                !await ValidateWritableRentalSettlementProfileReferenceAsync(
                    existing.LinkedRentalBillingProfileId,
                    dto,
                    nameof(Invoice),
                    result,
                    cancellationToken))
            {
                continue;
            }

            if (dto.IsDeleted &&
                existing is not null &&
                !await ValidateLinkedTransactionScopesForInvoiceDeleteAsync([existing.Id], dto, result, cancellationToken))
            {
                continue;
            }

            var canTriggerVersionNormalization =
                existing is not null &&
                (!dto.IsDeleted ||
                 !existing.IsDeleted ||
                 existing.IsLatestVersion);
            if (canTriggerVersionNormalization &&
                !await ValidateInvoiceVersionNormalizationScopeAsync(
                    existing!,
                    dto,
                    result,
                    cancellationToken))
            {
                continue;
            }

            if (dto.IsDeleted && existing is not null)
            {
                if (!CanWriteInvoiceUsingResolvedVersionScope(
                        existing))
                {
                    AddClientConflict(
                        dto,
                        nameof(Invoice),
                        "Current account cannot modify this office scope.",
                        result);
                    continue;
                }

                valid.Add(dto);
                continue;
            }

            var customer = requestedScopeCustomer;
            if (dto.CustomerId == Guid.Empty || customer is null || customer.IsDeleted)
            {
                if (dto.IsDeleted && existing is null)
                    continue;

                if (existing is not null)
                {
                    if (!CanWriteInvoiceUsingResolvedVersionScope(
                            existing))
                    {
                        AddClientConflict(dto, nameof(Invoice),
                            "Current account cannot modify this office scope.", result);
                        continue;
                    }

                    if (dto.IsDeleted)
                    {
                        PreserveExistingInvoiceScopeForDelete(dto, existing);
                        if (!await ValidateReadableInvoiceLineItemsAsync(dto, result, cancellationToken))
                            continue;
                        if (!await ValidateWritableInvoiceRentalBillingProfileAsync(dto, result, cancellationToken))
                            continue;

                        valid.Add(dto);
                        continue;
                    }

                    customer = existing.Customer;
                    if ((customer is null || customer.IsDeleted) && !string.IsNullOrWhiteSpace(dto.CustomerName))
                        customer = await FindWritableCustomerByNameAsync(dto.CustomerName, cancellationToken);

                    if (customer is not null && !customer.IsDeleted)
                    {
                        dto.CustomerId = customer.Id;
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
                        if (originalCustomerId != customer.Id)
                        {
                            AddNotice(
                                result,
                                nameof(Invoice),
                                dto.Id,
                                "invoice-customer-relinked",
                                $"전표 '{dto.Id:D}'의 거래처를 기존 전표/이름 기준으로 다시 연결했습니다.");
                        }
                        if (!await ValidateReadableInvoiceLineItemsAsync(dto, result, cancellationToken))
                            continue;
                        if (!await ValidateWritableInvoiceRentalBillingProfileAsync(dto, result, cancellationToken))
                            continue;

                        valid.Add(dto);
                        continue;
                    }

                    if (dto.IsDeleted)
                    {
                        dto.CustomerId = existing.CustomerId;
                        if (!await ValidateReadableInvoiceLineItemsAsync(dto, result, cancellationToken))
                            continue;
                        if (!await ValidateWritableInvoiceRentalBillingProfileAsync(dto, result, cancellationToken))
                            continue;

                        valid.Add(dto);
                        continue;
                    }
                }

                if (!string.IsNullOrWhiteSpace(dto.CustomerName))
                {
                    customer = await FindWritableCustomerByNameAsync(dto.CustomerName, cancellationToken);
                    if (customer is not null && !customer.IsDeleted)
                    {
                        dto.CustomerId = customer.Id;
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
                        if (originalCustomerId != customer.Id)
                        {
                            AddNotice(
                                result,
                                nameof(Invoice),
                                dto.Id,
                                "invoice-customer-relinked",
                                $"전표 '{dto.Id:D}'의 거래처를 이름 기준으로 다시 연결했습니다.");
                        }
                        if (!await ValidateReadableInvoiceLineItemsAsync(dto, result, cancellationToken))
                            continue;
                        if (!await ValidateWritableInvoiceRentalBillingProfileAsync(dto, result, cancellationToken))
                            continue;

                        valid.Add(dto);
                        continue;
                    }
                }

                AddClientConflict(dto, nameof(Invoice),
                    $"Referenced customer was not found: {dto.CustomerId}.", result);
                continue;
            }

            if (!_officeScopeService.CanWriteOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
            {
                if (dto.IsDeleted &&
                    existing is not null &&
                    CanWriteInvoiceUsingResolvedVersionScope(
                        existing))
                {
                    PreserveExistingInvoiceScopeForDelete(dto, existing);
                    if (!await ValidateReadableInvoiceLineItemsAsync(dto, result, cancellationToken))
                        continue;
                    if (!await ValidateWritableInvoiceRentalBillingProfileAsync(dto, result, cancellationToken))
                        continue;

                    valid.Add(dto);
                    continue;
                }

                if (existing is not null &&
                    existing.CustomerId != customer.Id &&
                    CanWriteInvoiceUsingResolvedVersionScope(
                        existing))
                {
                    dto.CustomerId = existing.CustomerId;
                    dto.ResponsibleOfficeCode = _officeScopeService.ResolveInvoiceResponsibleScopeForCreate(
                        dto.ResponsibleOfficeCode,
                        existing.ResponsibleOfficeCode);
                    dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                        dto.OfficeCode,
                        dto.ResponsibleOfficeCode,
                        existing.OfficeCode);
                    dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
                        dto.TenantCode,
                        dto.OfficeCode,
                        existing.TenantCode,
                        existing.OfficeCode);
                    if (originalCustomerId != dto.CustomerId)
                    {
                        AddNotice(
                            result,
                            nameof(Invoice),
                            dto.Id,
                            "invoice-customer-relinked",
                            $"전표 '{dto.Id:D}'의 거래처를 기존 저장값 기준으로 유지했습니다.");
                    }
                    if (!await ValidateReadableInvoiceLineItemsAsync(dto, result, cancellationToken))
                        continue;
                    if (!await ValidateWritableInvoiceRentalBillingProfileAsync(dto, result, cancellationToken))
                        continue;

                    valid.Add(dto);
                    continue;
                }

                AddClientConflict(dto, nameof(Invoice),
                    $"Referenced customer is outside the writable office scope: {dto.CustomerId}.", result);
                continue;
            }

            if (existing is not null &&
                !CanWriteInvoiceUsingResolvedVersionScope(
                    existing))
            {
                AddClientConflict(dto, nameof(Invoice),
                    "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.ResponsibleOfficeCode = _officeScopeService.ResolveInvoiceResponsibleScopeForCreate(
                dto.ResponsibleOfficeCode,
                customer.ResponsibleOfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                existing?.OfficeCode ?? customer.OfficeCode);
            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
                dto.TenantCode,
                dto.OfficeCode,
                existing?.TenantCode ?? customer.TenantCode,
                existing?.OfficeCode ?? customer.OfficeCode);

            if (originalCustomerId != dto.CustomerId)
            {
                AddNotice(
                    result,
                    nameof(Invoice),
                    dto.Id,
                    "invoice-customer-relinked",
                    $"전표 '{dto.Id:D}'의 거래처를 서버 기준 거래처로 다시 맞췄습니다.");
            }

            if (!await ValidateReadableInvoiceLineItemsAsync(dto, result, cancellationToken))
                continue;
            if (!await ValidateWritableInvoiceRentalBillingProfileAsync(dto, result, cancellationToken))
                continue;

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<bool> ValidateInvoiceDeletePaymentSideEffectPermissionAsync(
        InvoiceDto dto,
        Invoice? existing,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        if (!dto.IsDeleted || existing is null || HasPermission(PermissionNames.PaymentEdit))
            return true;

        if (!await HasActivePaymentSideEffectsForInvoiceDeleteAsync([existing.Id], cancellationToken))
            return true;

        AddClientConflict(
            dto,
            nameof(Invoice),
            "Deleting an invoice with linked payment/transaction records requires Payment.Edit permission.",
            result);
        return false;
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

    private async Task<bool> ValidateLinkedTransactionScopesForInvoiceDeleteAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        InvoiceDto dto,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return true;

        var linkedTransactions = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction =>
                transaction.LinkedInvoiceId.HasValue &&
                invoiceIds.Contains(transaction.LinkedInvoiceId.Value))
            .Select(transaction => new
            {
                transaction.Id,
                transaction.ResponsibleOfficeCode,
                transaction.TenantCode,
                transaction.OfficeCode,
                transaction.LinkedRentalBillingProfileId
            })
            .ToListAsync(cancellationToken);

        foreach (var linkedTransaction in linkedTransactions)
        {
            if (!_officeScopeService.CanWriteOfficeForPayments(
                    linkedTransaction.ResponsibleOfficeCode,
                    linkedTransaction.TenantCode,
                    linkedTransaction.OfficeCode))
            {
                AddClientConflict(dto, nameof(Invoice),
                    $"Linked transaction is outside the writable office scope: {linkedTransaction.Id}.", result);
                return false;
            }

            if (!await ValidateWritableRentalSettlementProfileReferenceAsync(
                    linkedTransaction.LinkedRentalBillingProfileId,
                    dto,
                    nameof(Invoice),
                    result,
                    cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> ValidateReadableInvoiceLineItemsAsync(
        InvoiceDto dto,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        if (dto.IsDeleted)
            return true;

        var warehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
            dto.SourceWarehouseCode,
            dto.ResponsibleOfficeCode,
            dto.OfficeCode);
        if (!_officeScopeService.CanWriteWarehouse(warehouseCode, dto.OfficeCode))
        {
            AddClientConflict(
                dto,
                nameof(Invoice),
                $"Invoice source warehouse is outside the writable warehouse scope: {warehouseCode}.",
                result);
            return false;
        }

        dto.SourceWarehouseCode = warehouseCode;
        var activeLines = (dto.Lines ?? [])
            .Where(line => !line.IsDeleted)
            .ToList();
        var invalidQuantityLine = activeLines.FirstOrDefault(line =>
            !DatabaseNumericContract.IsPositiveQuantity18Scale2(line.Quantity));
        if (invalidQuantityLine is not null)
        {
            AddClientConflict(
                dto,
                nameof(Invoice),
                $"Active invoice line quantity must be greater than zero and fit numeric(18,2): {invalidQuantityLine.Id}.",
                result);
            return false;
        }

        var itemIds = activeLines
            .Where(line => line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
            .Select(line => line.ItemId!.Value)
            .Distinct()
            .ToList();
        if (itemIds.Count == 0)
            return true;

        var items = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) && !item.IsDeleted)
            .Select(item => new { item.Id, item.OfficeCode, item.TenantCode, item.TrackingType })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        foreach (var itemId in itemIds)
        {
            if (!items.TryGetValue(itemId, out var item))
            {
                AddClientConflict(
                    dto,
                    nameof(Invoice),
                    $"Referenced invoice line item was not found: {itemId}.",
                    result);
                return false;
            }

            if (_officeScopeService.CanReadOfficeForItems(item.OfficeCode, item.TenantCode))
                continue;

            AddClientConflict(
                dto,
                nameof(Invoice),
                $"Referenced item is outside the readable office scope: {itemId}.",
                result);
            return false;
        }

        foreach (var line in activeLines.Where(line => line.ItemId.HasValue && line.ItemId.Value != Guid.Empty))
        {
            if (items.TryGetValue(line.ItemId!.Value, out var item))
                line.ItemTrackingType = ItemTrackingTypes.Normalize(item.TrackingType);
        }

        return true;
    }

    private static void PreserveExistingInvoiceScopeForDelete(InvoiceDto dto, Invoice existing)
    {
        dto.CustomerId = existing.CustomerId;
        dto.TenantCode = existing.TenantCode;
        dto.OfficeCode = existing.OfficeCode;
        dto.ResponsibleOfficeCode = existing.ResponsibleOfficeCode;
        dto.SourceWarehouseCode = existing.SourceWarehouseCode;
        dto.PurchaseReceivingOfficeCode = existing.PurchaseReceivingOfficeCode;
        dto.PurchaseReceivingWarehouseCode = existing.PurchaseReceivingWarehouseCode;
        dto.LinkedRentalBillingProfileId = existing.LinkedRentalBillingProfileId;
        dto.LinkedRentalBillingRunId = existing.LinkedRentalBillingRunId;
        dto.VersionGroupId = existing.VersionGroupId;
        dto.VersionNumber = existing.VersionNumber;
        dto.PreviousVersionId = existing.PreviousVersionId;
        dto.IsLatestVersion = existing.IsLatestVersion;
    }

    private static void IsolateNewInvoiceDeleteTombstone(Invoice invoice)
    {
        invoice.VersionGroupId = invoice.Id;
        invoice.PreviousVersionId = null;
        invoice.IsLatestVersion = false;
    }

    private async Task<bool> ValidateWritableInvoiceRentalBillingProfileAsync(
        InvoiceDto dto,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        if (!dto.LinkedRentalBillingProfileId.HasValue || dto.LinkedRentalBillingProfileId.Value == Guid.Empty)
            return true;

        if (dto.IsDeleted)
        {
            return await ValidateWritableRentalSettlementProfileReferenceAsync(
                dto.LinkedRentalBillingProfileId,
                dto,
                nameof(Invoice),
                result,
                cancellationToken);
        }

        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id == dto.LinkedRentalBillingProfileId.Value)
            .Select(current => new
            {
                current.IsDeleted,
                current.ResponsibleOfficeCode,
                current.TenantCode,
                current.OfficeCode,
                current.BillingRunsJson
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (profile is null || profile.IsDeleted)
        {
            AddClientConflict(
                dto,
                nameof(Invoice),
                $"Referenced rental billing profile was not found: {dto.LinkedRentalBillingProfileId}.",
                result);
            return false;
        }

        if (!_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode))
        {
            AddClientConflict(
                dto,
                nameof(Invoice),
                $"Referenced rental billing profile is outside the writable office scope: {dto.LinkedRentalBillingProfileId}.",
                result);
            return false;
        }

        return ValidateActiveRentalBillingRunReference(
            profile.BillingRunsJson,
            dto.LinkedRentalBillingRunId,
            dto,
            nameof(Invoice),
            result);
    }

    private bool ValidateActiveRentalBillingRunReference(
        string? billingRunsJson,
        Guid? runId,
        SyncEntityDto dto,
        string entityName,
        SyncPushResult result)
    {
        if (!TryValidateIncomingRentalBillingRuns(
                billingRunsJson,
                out var validationError))
        {
            AddClientConflict(
                dto,
                entityName,
                $"Referenced rental billing profile has malformed run JSON: {validationError}",
                result);
            return false;
        }

        var requestedRunId = runId.GetValueOrDefault();
        var markerLookupRunId = requestedRunId == Guid.Empty
            ? Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")
            : requestedRunId;
        var lookup = RentalBillingRunTombstonePolicy.LookupForServerMutation(
            billingRunsJson,
            markerLookupRunId);
        if (!lookup.IsValid)
        {
            AddClientConflict(
                dto,
                entityName,
                "Referenced rental billing profile has malformed tombstone metadata.",
                result);
            return false;
        }

        if (requestedRunId == Guid.Empty || !lookup.IsTombstoned)
            return true;

        AddClientConflict(
            dto,
            entityName,
            $"Referenced rental billing run is tombstoned and cannot accept active financial data: {requestedRunId:D}.",
            result);
        return false;
    }

    private async Task<bool> ValidateWritableRentalSettlementProfileReferenceAsync(
        Guid? profileId,
        SyncEntityDto dto,
        string entityName,
        SyncPushResult result,
        CancellationToken cancellationToken,
        bool allowMissingOrDeleted = true)
    {
        if (!profileId.HasValue || profileId.Value == Guid.Empty)
            return true;

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
                return true;

            AddClientConflict(
                dto,
                entityName,
                $"Referenced rental billing profile was not found: {profileId}.",
                result);
            return false;
        }

        if (_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode))
            return true;

        AddClientConflict(
            dto,
            entityName,
            $"Referenced rental billing profile is outside the writable office scope: {profileId}.",
            result);
        return false;
    }

    private async Task<Customer?> FindWritableCustomerByNameAsync(string? customerName, CancellationToken cancellationToken)
    {
        var trimmedName = (customerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
            return null;

        var nameMatchKey = MatchKeyNormalizer.Normalize(trimmedName);
        var candidates = await _officeScopeService.ApplyCustomerScope(_dbContext.Customers.IgnoreQueryFilters())
            .Where(customer => !customer.IsDeleted)
            .Where(customer =>
                    customer.NameOriginal == trimmedName ||
                    customer.NameMatchKey == nameMatchKey)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(customer =>
            _officeScopeService.CanWriteOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode));
    }

    private async Task<List<CustomerContractDto>> FilterValidCustomerContractsAsync(
        IEnumerable<CustomerContractDto> payload, SyncPushResult result, CancellationToken cancellationToken)
    {
        var valid = new List<CustomerContractDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.CustomerContracts.IgnoreQueryFilters()
                .Include(x => x.Customer)
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);

            if (existing?.Customer is not null &&
                !_officeScopeService.CanWriteOfficeForContracts(
                    existing.Customer.ResponsibleOfficeCode,
                    existing.Customer.TenantCode,
                    existing.Customer.OfficeCode))
            {
                AddClientConflict(dto, nameof(CustomerContract),
                    "Current account cannot modify this office scope.", result);
                continue;
            }

            if (!dto.IsDeleted)
            {
                var customer = await _dbContext.Customers.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
                if (dto.CustomerId == Guid.Empty || customer is null || customer.IsDeleted)
                {
                    AddClientConflict(dto, nameof(CustomerContract),
                        $"Referenced customer was not found: {dto.CustomerId}.", result);
                    continue;
                }

                if (!_officeScopeService.CanWriteOfficeForContracts(
                        customer.ResponsibleOfficeCode,
                        customer.TenantCode,
                        customer.OfficeCode))
                {
                    AddClientConflict(dto, nameof(CustomerContract),
                        $"Referenced customer is outside the writable office scope: {dto.CustomerId}.", result);
                    continue;
                }
            }
            else if (existing is null)
            {
                // 삭제 동기화는 멱등적으로 처리한다.
                // 이미 서버에 없는 계약서를 다시 삭제하려는 경우 충돌로 막지 않고
                // 클라이언트의 stale dirty row를 정리할 수 있게 그냥 통과시킨다.
                continue;
            }

            if (!dto.IsDeleted)
            {
                var fileContent = dto.FileContent ?? [];
                var fileName = Path.GetFileName(dto.FileName ?? string.Empty);
                var mimeType = dto.MimeType?.Trim() ?? string.Empty;
                var hasAttachedFilePayload = fileContent.Length > 0 || dto.FileSize > 0 || !string.IsNullOrWhiteSpace(dto.FileHash);

                if (!hasAttachedFilePayload)
                {
                    dto.FileSize = 0;
                    dto.FileHash = string.Empty;
                    dto.FileContent = [];
                    valid.Add(dto);
                    continue;
                }

                if (fileContent.Length == 0)
                {
                    if (existing is not null &&
                        !existing.IsDeleted &&
                        (!string.IsNullOrWhiteSpace(existing.StoragePath) || existing.FileContent.Length > 0))
                    {
                        // 이미 서버에 파일이 보관된 계약서는 PC가 메타데이터만 수정할 수 있다.
                        // Pull payload에는 파일 본문이 포함되지 않으므로, 파일 내용 없이 제목/일자/대표 여부만
                        // 재전송되는 정상 흐름을 충돌로 막지 않는다. 비어 있는 파일 메타데이터는 기존값으로 보존한다.
                        dto.FileContent = [];
                        dto.FileName = string.IsNullOrWhiteSpace(fileName) ? existing.FileName : fileName;
                        dto.MimeType = string.IsNullOrWhiteSpace(mimeType) ? existing.MimeType : mimeType;
                        dto.FileSize = existing.FileSize;
                        dto.FileHash = existing.FileHash;
                    }
                    else
                    {
                        dto.FileContent = [];
                        dto.FileName = "PDF not registered";
                        dto.MimeType = string.Empty;
                        dto.FileSize = 0;
                        dto.FileHash = string.Empty;
                        AddNotice(
                            result,
                            nameof(CustomerContract),
                            dto.Id,
                            "customer-contract-file-payload-missing",
                            "Contract PDF metadata was received without file content, so it was saved as a draft contract. Reattach the PDF if needed.");
                        valid.Add(dto);
                        continue;
                    }
                }

                if (fileContent.LongLength > MaxContractFileSizeBytes)
                {
                    AddClientConflict(dto, nameof(CustomerContract),
                        $"Contract file size exceeds the {MaxContractFileSizeBytes / (1024 * 1024)}MB limit.", result);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(fileName) ||
                    !string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                {
                    AddClientConflict(dto, nameof(CustomerContract),
                        "Only PDF contracts are allowed.", result);
                    continue;
                }

                if (fileContent.Length > 0)
                {
                    dto.FileSize = fileContent.LongLength;
                    dto.FileHash = ComputeSha256Hex(fileContent);
                }
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<PaymentTransactionAtomicityFilterResult> FilterAtomicPaymentTransactionPairsAsync(
        IReadOnlyCollection<TransactionDto> requestedTransactions,
        List<TransactionDto> validTransactions,
        IReadOnlyCollection<PaymentDto> requestedPayments,
        List<PaymentDto> validPayments,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var requestedPaymentIds = requestedPayments
            .Where(payment => payment.Id != Guid.Empty)
            .Select(payment => payment.Id)
            .ToHashSet();
        var requestedTransactionsById = requestedTransactions
            .Where(transaction => transaction.Id != Guid.Empty)
            .GroupBy(transaction => transaction.Id)
            .ToDictionary(group => group.Key, group => group.ToList());
        var requestedPaymentsById = requestedPayments
            .Where(payment => payment.Id != Guid.Empty)
            .GroupBy(payment => payment.Id)
            .ToDictionary(group => group.Key, group => group.ToList());
        var pairIds = requestedTransactionsById.Keys
            .Where(requestedPaymentsById.ContainsKey)
            .ToHashSet();
        if (pairIds.Count == 0)
        {
            return new PaymentTransactionAtomicityFilterResult(
                validTransactions,
                validPayments,
                requestedPaymentIds);
        }

        var validTransactionReferences = validTransactions
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var validPaymentReferences = validPayments
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var existingTransactions = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .Where(transaction => pairIds.Contains(transaction.Id))
            .ToDictionaryAsync(transaction => transaction.Id, cancellationToken);
        var existingPayments = await _dbContext.Payments
            .IgnoreQueryFilters()
            .Where(payment => pairIds.Contains(payment.Id))
            .ToDictionaryAsync(payment => payment.Id, cancellationToken);
        var invoiceIds = requestedPaymentsById
            .Where(entry => pairIds.Contains(entry.Key))
            .SelectMany(entry => entry.Value)
            .Select(payment => payment.InvoiceId)
            .Where(invoiceId => invoiceId != Guid.Empty)
            .Distinct()
            .ToList();
        var invoices = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoiceIds.Contains(invoice.Id))
            .ToDictionaryAsync(invoice => invoice.Id, cancellationToken);

        var rejectedPairIds = new HashSet<Guid>();
        foreach (var pairId in pairIds)
        {
            var transactionRows = requestedTransactionsById[pairId];
            var paymentRows = requestedPaymentsById[pairId];
            if (transactionRows.Count != 1 || paymentRows.Count != 1)
            {
                rejectedPairIds.Add(pairId);
                foreach (var transactionRow in transactionRows)
                {
                    AddAtomicPairConflictIfMissing(
                        transactionRow,
                        nameof(TransactionRecord),
                        "Payment-backed transaction requires exactly one Transaction and one Payment row for the same id.",
                        result);
                }

                foreach (var paymentRow in paymentRows)
                {
                    AddAtomicPairConflictIfMissing(
                        paymentRow,
                        nameof(Payment),
                        "Payment-backed transaction requires exactly one Transaction and one Payment row for the same id.",
                        result);
                }

                continue;
            }

            var transaction = transactionRows[0];
            var payment = paymentRows[0];
            var transactionPassedStructuralValidation = validTransactionReferences.Contains(transaction);
            var paymentPassedStructuralValidation = validPaymentReferences.Contains(payment);
            existingTransactions.TryGetValue(pairId, out var existingTransaction);
            existingPayments.TryGetValue(pairId, out var existingPayment);
            var attemptsToClaimExistingTransactionId =
                existingTransaction is not null && existingPayment is null;
            var transactionPreflightReason = string.Empty;
            var paymentPreflightReason = string.Empty;
            var transactionCanCommit = transactionPassedStructuralValidation &&
                                       CanAcceptTrackedMutationPreflight(
                                           transaction,
                                           existingTransaction,
                                           nameof(TransactionRecord),
                                           out transactionPreflightReason);
            var paymentCanCommit = paymentPassedStructuralValidation &&
                                   CanAcceptTrackedMutationPreflight(
                                       payment,
                                       existingPayment,
                                       nameof(Payment),
                                       out paymentPreflightReason);
            invoices.TryGetValue(payment.InvoiceId, out var invoice);
            var payloadsAgree = transactionPassedStructuralValidation &&
                                paymentPassedStructuralValidation &&
                                PaymentTransactionPairPayloadsAgree(transaction, payment, invoice);
            if (!attemptsToClaimExistingTransactionId &&
                transactionCanCommit &&
                paymentCanCommit &&
                payloadsAgree)
                continue;

            rejectedPairIds.Add(pairId);
            var sharedReason = attemptsToClaimExistingTransactionId
                ? "A Payment cannot claim an existing Transaction id that is not already owned by a Payment."
                : !transactionPassedStructuralValidation
                    ? "The paired Transaction failed validation, so neither side of the payment command was applied."
                : !paymentPassedStructuralValidation
                    ? "The paired Payment failed validation, so neither side of the payment command was applied."
                    : !transactionCanCommit
                        ? $"The paired Transaction cannot be committed atomically: {transactionPreflightReason}"
                        : !paymentCanCommit
                            ? $"The paired Payment cannot be committed atomically: {paymentPreflightReason}"
                            : "The paired Payment and Transaction disagree on invoice, date, deletion state, or amount.";
            AddAtomicPairConflictIfMissing(transaction, nameof(TransactionRecord), sharedReason, result);
            AddAtomicPairConflictIfMissing(payment, nameof(Payment), sharedReason, result);
        }

        return new PaymentTransactionAtomicityFilterResult(
            validTransactions.Where(transaction => !rejectedPairIds.Contains(transaction.Id)).ToList(),
            validPayments.Where(payment => !rejectedPairIds.Contains(payment.Id)).ToList(),
            requestedPaymentIds);
    }

    private async Task<List<TransactionDto>> FilterPaymentControlledTransactionOnlyMutationsAsync(
        IEnumerable<TransactionDto> payload,
        IReadOnlySet<Guid> requestedPaymentIds,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var transactions = payload.ToList();
        var candidateIds = transactions
            .Select(transaction => transaction.Id)
            .Where(transactionId => transactionId != Guid.Empty &&
                                    !requestedPaymentIds.Contains(transactionId))
            .Distinct()
            .ToList();
        if (candidateIds.Count == 0)
            return transactions;

        var paymentControlledIdRows = await _dbContext.Payments
            .IgnoreQueryFilters()
            .Where(payment => candidateIds.Contains(payment.Id))
            .Select(payment => payment.Id)
            .ToListAsync(cancellationToken);
        var paymentControlledIds = paymentControlledIdRows.ToHashSet();
        if (paymentControlledIds.Count == 0)
            return transactions;

        var storedTransactions = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .Where(transaction => paymentControlledIds.Contains(transaction.Id))
            .ToDictionaryAsync(transaction => transaction.Id, cancellationToken);
        var accepted = new List<TransactionDto>();
        foreach (var transaction in transactions)
        {
            if (!paymentControlledIds.Contains(transaction.Id))
            {
                accepted.Add(transaction);
                continue;
            }

            if (HasExactProcessedMutationReplay(transaction, nameof(TransactionRecord)) ||
                (storedTransactions.TryGetValue(transaction.Id, out var storedTransaction) &&
                 TransactionMutationMatchesStoredState(transaction, storedTransaction)))
            {
                accepted.Add(transaction);
                continue;
            }

            AddAtomicPairConflictIfMissing(
                transaction,
                nameof(TransactionRecord),
                "This transaction is controlled by a Payment with the same id and cannot be changed without its source Payment command.",
                result);
        }

        return accepted;
    }

    private List<TransactionAttachmentDto> FilterDeferredPaymentTransactionAttachments(
        IEnumerable<TransactionAttachmentDto> payload,
        IReadOnlySet<Guid> acceptedPaymentIds,
        SyncPushResult result)
    {
        var accepted = new List<TransactionAttachmentDto>();
        foreach (var attachment in payload)
        {
            if (acceptedPaymentIds.Contains(attachment.TransactionId))
            {
                accepted.Add(attachment);
                continue;
            }

            AddAtomicPairConflictIfMissing(
                attachment,
                nameof(TransactionAttachment),
                "The parent Payment command was not accepted, so its Transaction attachment was not applied.",
                result);
        }

        return accepted;
    }

    private bool CanAcceptTrackedMutationPreflight(
        SyncEntityDto dto,
        TrackedEntity? existing,
        string entityName,
        out string reason)
    {
        var mutationId = NormalizeMutationId(dto.MutationId);
        if (ItemWarehouseStockMutationReceipt.IsReservedMutationId(mutationId))
        {
            reason = "Mutation id uses a server-reserved receipt namespace.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(mutationId) &&
            _processedMutationsById.ContainsKey(mutationId))
        {
            if (HasExactProcessedMutationReplay(dto, entityName))
            {
                reason = string.Empty;
                return true;
            }

            reason = "Mutation id was already processed with a different entity, expected revision, or payload.";
            return false;
        }

        if (existing is null || (dto.IsDeleted && existing.IsDeleted))
        {
            reason = string.Empty;
            return true;
        }

        if (HasExpectedRevisionConflict(existing, dto))
        {
            reason = BuildExpectedRevisionConflictReason(dto.ExpectedRevision, existing.Revision);
            return false;
        }

        if (IsServerEntityNewer(existing, dto))
        {
            reason = "Server version is newer.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool PaymentTransactionPairPayloadsAgree(
        TransactionDto transaction,
        PaymentDto payment,
        Invoice? invoice)
    {
        if (transaction.IsDeleted || payment.IsDeleted)
            return transaction.IsDeleted && payment.IsDeleted;
        if (invoice is null || invoice.IsDeleted ||
            transaction.LinkedInvoiceId != payment.InvoiceId ||
            transaction.TransactionDate != payment.PaymentDate ||
            Math.Abs(transaction.SettlementAmount) != payment.Amount)
        {
            return false;
        }

        if (IsPaymentVoucher(invoice.VoucherType))
        {
            return transaction.ReceiptTotal == 0m &&
                   transaction.PaymentTotal == payment.Amount &&
                   transaction.CashPayment + transaction.CardPayment +
                   transaction.BankPayment + transaction.DiscountReceived == payment.Amount;
        }

        return transaction.PaymentTotal == 0m &&
               transaction.ReceiptTotal == payment.Amount &&
               transaction.CashReceipt + transaction.CardReceipt +
               transaction.BankReceipt + transaction.DiscountApplied == payment.Amount;
    }

    private static bool TransactionMutationMatchesStoredState(
        TransactionDto dto,
        TransactionRecord entity)
        => dto.CustomerId == entity.CustomerId &&
           string.Equals(dto.TenantCode?.Trim(), entity.TenantCode?.Trim(), StringComparison.OrdinalIgnoreCase) &&
           string.Equals(dto.OfficeCode?.Trim(), entity.OfficeCode?.Trim(), StringComparison.OrdinalIgnoreCase) &&
           string.Equals(dto.ResponsibleOfficeCode?.Trim(), entity.ResponsibleOfficeCode?.Trim(), StringComparison.OrdinalIgnoreCase) &&
           dto.TransactionDate == entity.TransactionDate &&
           string.Equals(dto.TransactionKind?.Trim(), entity.TransactionKind?.Trim(), StringComparison.OrdinalIgnoreCase) &&
           dto.LinkedInvoiceId == entity.LinkedInvoiceId &&
           string.Equals(dto.LinkedInvoiceNumber?.Trim(), entity.LinkedInvoiceNumber?.Trim(), StringComparison.Ordinal) &&
           dto.LinkedRentalBillingProfileId == entity.LinkedRentalBillingProfileId &&
           dto.LinkedRentalBillingRunId == entity.LinkedRentalBillingRunId &&
           dto.SettlementAmount == entity.SettlementAmount &&
           dto.AdvanceDelta == entity.AdvanceDelta &&
           dto.PrepaidDelta == entity.PrepaidDelta &&
           dto.CashReceipt == entity.CashReceipt &&
           dto.CardReceipt == entity.CardReceipt &&
           dto.BankReceipt == entity.BankReceipt &&
           dto.DiscountApplied == entity.DiscountApplied &&
           dto.ReceiptTotal == entity.ReceiptTotal &&
           dto.CashPayment == entity.CashPayment &&
           dto.CardPayment == entity.CardPayment &&
           dto.BankPayment == entity.BankPayment &&
           dto.DiscountReceived == entity.DiscountReceived &&
           dto.PaymentTotal == entity.PaymentTotal &&
           string.Equals(dto.Note?.Trim(), entity.Note?.Trim(), StringComparison.Ordinal) &&
           string.Equals(dto.Memo?.Trim(), entity.Memo?.Trim(), StringComparison.Ordinal) &&
           dto.IsDeleted == entity.IsDeleted;

    private void AddAtomicPairConflictIfMissing<TDto>(
        TDto dto,
        string entityName,
        string reason,
        SyncPushResult result)
        where TDto : SyncEntityDto
    {
        if (result.Conflicts.Any(conflict =>
                string.Equals(conflict.EntityName, entityName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(conflict.EntityId, dto.Id.ToString("D"), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        AddClientConflict(dto, entityName, reason, result);
    }

    private sealed record PaymentTransactionAtomicityFilterResult(
        List<TransactionDto> ValidTransactions,
        List<PaymentDto> ValidPayments,
        HashSet<Guid> RequestedPaymentIds);

    private async Task<bool> ValidateCompatibleLinkedTransactionForPaymentAsync(
        PaymentDto dto,
        Payment? existing,
        Guid targetInvoiceId,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        if (dto.Id == Guid.Empty)
            return true;

        var linkedTransaction = await _dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(transaction => transaction.Id == dto.Id, cancellationToken);
        if (linkedTransaction is null)
            return true;

        if (existing is null)
        {
            AddClientConflict(dto, nameof(Payment),
                $"A Payment cannot claim an existing Transaction id that is not already owned by a Payment: {linkedTransaction.Id}.", result);
            return false;
        }

        if (linkedTransaction.IsDeleted)
            return true;

        if (!_officeScopeService.CanWriteOfficeForPayments(
                linkedTransaction.ResponsibleOfficeCode,
                linkedTransaction.TenantCode,
                linkedTransaction.OfficeCode))
        {
            AddClientConflict(dto, nameof(Payment),
                $"Linked transaction is outside the writable office scope: {linkedTransaction.Id}.", result);
            return false;
        }

        if (!await ValidateWritableRentalSettlementProfileReferenceAsync(
                linkedTransaction.LinkedRentalBillingProfileId,
                dto,
                nameof(Payment),
                result,
                cancellationToken))
        {
            return false;
        }

        if (!linkedTransaction.LinkedInvoiceId.HasValue ||
            linkedTransaction.LinkedInvoiceId.Value == Guid.Empty)
        {
            AddClientConflict(dto, nameof(Payment),
                $"Linked transaction does not point to a payment invoice: {linkedTransaction.Id}.", result);
            return false;
        }

        var linkedInvoiceId = linkedTransaction.LinkedInvoiceId.Value;
        if (linkedInvoiceId == targetInvoiceId)
            return true;

        if (existing is not null && linkedInvoiceId == existing.InvoiceId)
            return true;

        AddClientConflict(dto, nameof(Payment),
            $"Linked transaction invoice does not match the payment invoice: {linkedInvoiceId}.", result);
        return false;
    }

    private async Task<List<PaymentDto>> FilterValidPaymentsAsync(
        IEnumerable<PaymentDto> payload, SyncPushResult result, CancellationToken cancellationToken)
    {
        var valid = new List<PaymentDto>();
        var acceptedAmountByInvoiceId = new Dictionary<Guid, decimal>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.Payments.IgnoreQueryFilters()
                .Include(x => x.Invoice)
                .ThenInclude(invoice => invoice!.Customer)
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing?.Invoice is not null &&
                !_officeScopeService.CanWriteOfficeForPayments(existing.Invoice.ResponsibleOfficeCode, existing.Invoice.TenantCode, existing.Invoice.OfficeCode))
            {
                AddClientConflict(dto, nameof(Payment),
                    $"Existing invoice is outside the writable office scope: {existing.InvoiceId}.", result);
                continue;
            }

            if (existing is null &&
                !await ValidateCompatibleLinkedTransactionForPaymentAsync(
                    dto,
                    existing,
                    dto.InvoiceId,
                    result,
                    cancellationToken))
            {
                continue;
            }

            var invoice = await _dbContext.Invoices.IgnoreQueryFilters()
                .Include(x => x.Customer)
                .FirstOrDefaultAsync(x => x.Id == dto.InvoiceId, cancellationToken);
            if (dto.InvoiceId == Guid.Empty || invoice is null || invoice.IsDeleted)
            {
                if (dto.IsDeleted && existing is null)
                {
                    valid.Add(dto);
                    continue;
                }

                if (existing is not null)
                {
                    dto.IsDeleted = true;
                    dto.InvoiceId = existing.InvoiceId;
                    if (!await ValidateCompatibleLinkedTransactionForPaymentAsync(
                            dto,
                            existing,
                            existing.InvoiceId,
                            result,
                            cancellationToken))
                    {
                        continue;
                    }

                    valid.Add(dto);
                    continue;
                }

                AddClientConflict(dto, nameof(Payment),
                    $"Referenced invoice was not found: {dto.InvoiceId}.", result);
                continue;
            }

            if (!_officeScopeService.CanWriteOfficeForPayments(invoice.ResponsibleOfficeCode, invoice.TenantCode, invoice.OfficeCode))
            {
                AddClientConflict(dto, nameof(Payment),
                    $"Referenced invoice is outside the writable office scope: {dto.InvoiceId}.", result);
                continue;
            }

            if (!await ValidateWritableRentalSettlementProfileReferenceAsync(
                    invoice.LinkedRentalBillingProfileId,
                    dto,
                    nameof(Payment),
                    result,
                    cancellationToken,
                    allowMissingOrDeleted: false))
            {
                continue;
            }

            if (!dto.IsDeleted &&
                invoice.LinkedRentalBillingProfileId.HasValue &&
                invoice.LinkedRentalBillingProfileId.Value != Guid.Empty)
            {
                var billingRunsJson = await _dbContext.RentalBillingProfiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(profile => profile.Id == invoice.LinkedRentalBillingProfileId.Value)
                    .Select(profile => profile.BillingRunsJson)
                    .FirstOrDefaultAsync(cancellationToken);
                if (!ValidateActiveRentalBillingRunReference(
                        billingRunsJson,
                        invoice.LinkedRentalBillingRunId,
                        dto,
                        nameof(Payment),
                        result))
                {
                    continue;
                }
            }

            if (invoice.Customer is null || invoice.Customer.IsDeleted)
            {
                if (dto.IsDeleted)
                {
                    valid.Add(dto);
                    continue;
                }

                AddClientConflict(dto, nameof(Payment),
                    $"Referenced invoice customer was not found: {invoice.CustomerId}.", result);
                continue;
            }

            if (existing is null && !dto.IsDeleted && dto.ExpectedRevision > 0 && invoice.Revision != dto.ExpectedRevision)
            {
                AddClientConflict(dto, nameof(Payment),
                    $"Referenced invoice revision mismatch. client={dto.ExpectedRevision}, server={invoice.Revision}", result);
                continue;
            }

            if (existing is not null &&
                !await ValidateCompatibleLinkedTransactionForPaymentAsync(
                    dto,
                    existing,
                    dto.InvoiceId,
                    result,
                    cancellationToken))
            {
                continue;
            }

            if (!dto.IsDeleted)
            {
                if (dto.Amount <= 0m)
                {
                    AddClientConflict(dto, nameof(Payment), "Payment amount must be greater than zero.", result);
                    continue;
                }

                if (existing is not null && (HasExpectedRevisionConflict(existing, dto) || IsServerEntityNewer(existing, dto)))
                {
                    valid.Add(dto);
                    continue;
                }

                var serverSettledAmounts = await _dbContext.Payments.IgnoreQueryFilters()
                    .Where(payment =>
                        payment.InvoiceId == dto.InvoiceId &&
                        !payment.IsDeleted &&
                        payment.Id != dto.Id)
                    .Select(payment => payment.Amount)
                    .ToListAsync(cancellationToken);
                var serverSettledAmount = serverSettledAmounts.Sum();
                acceptedAmountByInvoiceId.TryGetValue(dto.InvoiceId, out var acceptedBatchAmount);
                var outstandingAmount = Math.Max(0m, invoice.TotalAmount - serverSettledAmount - acceptedBatchAmount);
                if (dto.Amount > outstandingAmount)
                {
                    AddClientConflict(dto, nameof(Payment),
                        $"Payment amount exceeds current outstanding balance. outstanding={outstandingAmount:N0}, amount={dto.Amount:N0}.", result);
                    continue;
                }

                acceptedAmountByInvoiceId[dto.InvoiceId] = acceptedBatchAmount + dto.Amount;
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<ItemPriceGradeDto>> FilterValidItemPriceGradesAsync(
        IEnumerable<ItemPriceGradeDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var incomingRows = payload
            .Where(dto => dto.ItemId != Guid.Empty && dto.PriceGradeOptionId != Guid.Empty)
            .GroupBy(dto => new { dto.ItemId, dto.PriceGradeOptionId })
            .Select(group => group.Last())
            .ToList();
        if (incomingRows.Count == 0)
            return new List<ItemPriceGradeDto>();

        var itemIds = incomingRows.Select(row => row.ItemId).Distinct().ToArray();
        var optionIds = incomingRows.Select(row => row.PriceGradeOptionId).Distinct().ToArray();
        var items = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => itemIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var options = await _dbContext.PriceGradeOptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(option => optionIds.Contains(option.Id))
            .ToDictionaryAsync(option => option.Id, cancellationToken);
        var existingRows = await _dbContext.ItemPriceGrades
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(row => itemIds.Contains(row.ItemId) && optionIds.Contains(row.PriceGradeOptionId))
            .ToListAsync(cancellationToken);
        var existingByKey = existingRows
            .GroupBy(row => $"{row.ItemId:N}|{row.PriceGradeOptionId:N}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.UpdatedAtUtc).First(), StringComparer.OrdinalIgnoreCase);

        var valid = new List<ItemPriceGradeDto>();
        foreach (var dto in incomingRows)
        {
            if (dto.UnitPrice < 0m)
            {
                AddClientConflict(dto, nameof(ItemPriceGrade), "Item price grade cannot be negative.", result);
                continue;
            }

            if (!items.TryGetValue(dto.ItemId, out var item) ||
                (item.IsDeleted && !dto.IsDeleted))
            {
                AddClientConflict(dto, nameof(ItemPriceGrade), "Item price grade references a missing or deleted item.", result);
                continue;
            }

            if (!_officeScopeService.CanWriteOfficeForItems(item.OfficeCode, item.TenantCode))
            {
                AddClientConflict(dto, nameof(ItemPriceGrade), "Item price grade item is outside the writable item scope.", result);
                continue;
            }

            if (!options.TryGetValue(dto.PriceGradeOptionId, out var option) || option.IsDeleted || (!option.IsActive && !dto.IsDeleted))
            {
                AddClientConflict(dto, nameof(ItemPriceGrade), "Item price grade references a missing or inactive price grade option.", result);
                continue;
            }

            var key = $"{dto.ItemId:N}|{dto.PriceGradeOptionId:N}";
            if (existingByKey.TryGetValue(key, out var existing))
            {
                dto.Id = existing.Id;
            }
            else if (dto.Id == Guid.Empty)
            {
                dto.Id = Guid.NewGuid();
            }

            dto.PriceGradeName = option.Name?.Trim() ?? dto.PriceGradeName?.Trim() ?? string.Empty;
            dto.UnitPrice = Math.Max(0m, dto.UnitPrice);
            dto.IsActive = !dto.IsDeleted && dto.IsActive;
            valid.Add(dto);
        }

        return valid;
    }

    private async Task RemoveItemPriceGradesForDeletedItemsAsync(
        IReadOnlyCollection<Guid> deletedItemIds,
        CancellationToken cancellationToken)
    {
        if (deletedItemIds.Count == 0)
            return;

        var rows = await _dbContext.ItemPriceGrades
            .IgnoreQueryFilters()
            .Where(row => deletedItemIds.Contains(row.ItemId) && !row.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.IsDeleted = true;
            row.IsActive = false;
        }
    }

    private async Task<ItemWarehouseStockUpsertResult> UpsertItemWarehouseStocksAsync(
        IEnumerable<ItemWarehouseStockDto> payload,
        IEnumerable<ItemWarehouseStockSnapshotMarkerDto>
            snapshotMarkers,
        SyncPushResult result,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var missingItemCount = 0;
        var deletedItemCount = 0;
        var outOfScopeItemCount = 0;
        var outOfScopeWarehouseCount = 0;
        var nonInventoryItemIds = new HashSet<Guid>();
        var affectedItemIds = new HashSet<Guid>();
        var acceptedStockKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appliedStockKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var originalQuantitiesByAppliedKey =
            new Dictionary<
                InvoiceStockSnapshotService.InvoiceStockKey,
                decimal>();
        var itemIdsBlockingOmittedRowDeletion = new HashSet<Guid>();

        void AcknowledgeAcceptedStock(ItemWarehouseStockDto dto)
        {
            var key = BuildItemWarehouseStockSnapshotKey(dto.ItemId, dto.WarehouseCode);
            if (!acceptedStockKeys.Add(key))
                return;

            result.AcceptedItemWarehouseStockKeys.Add(new SyncAcceptedItemWarehouseStockKeyDto
            {
                ItemId = dto.ItemId,
                WarehouseCode = dto.WarehouseCode
            });
        }

        void MarkAppliedStock(
            ItemWarehouseStockDto dto,
            decimal originalQuantity)
        {
            var canonicalWarehouseCode =
                OfficeCodeCatalog.NormalizeWarehouseCodeLoose(
                    dto.WarehouseCode);
            appliedStockKeys.Add(
                BuildItemWarehouseStockSnapshotKey(
                    dto.ItemId,
                    canonicalWarehouseCode));
            var canonicalKey =
                new InvoiceStockSnapshotService.InvoiceStockKey(
                    dto.ItemId,
                    canonicalWarehouseCode);
            originalQuantitiesByAppliedKey[canonicalKey] =
                originalQuantitiesByAppliedKey.TryGetValue(
                    canonicalKey,
                    out var existingOriginalQuantity)
                    ? existingOriginalQuantity + originalQuantity
                    : originalQuantity;
        }

        var incomingRows = new List<ItemWarehouseStockDto>();
        foreach (var dto in payload)
        {
            if (dto.ItemId == Guid.Empty)
            {
                AddInvalidItemWarehouseStockConflict(
                    dto,
                    InvalidItemWarehouseStockItemIdentityToken,
                    "Warehouse stock row has an empty item id.",
                    result);
                AddNotice(
                    result,
                    nameof(ItemWarehouseStock),
                    Guid.Empty,
                    "item-warehouse-stock-skip-empty-item-id",
                    "품목 ID가 비어 있는 재고 수량 행은 서버 반영에서 제외했습니다.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(dto.WarehouseCode))
            {
                itemIdsBlockingOmittedRowDeletion.Add(dto.ItemId);
                AddInvalidItemWarehouseStockConflict(
                    dto,
                    InvalidItemWarehouseStockWarehouseIdentityToken,
                    "Warehouse stock row has an empty warehouse code.",
                    result);
                AddNotice(
                    result,
                    nameof(ItemWarehouseStock),
                    dto.ItemId,
                    "item-warehouse-stock-skip-empty-warehouse-code",
                    "창고 코드가 비어 있는 재고 수량 행은 서버 반영에서 제외하고 누락 창고 삭제도 수행하지 않았습니다.");
                continue;
            }

            if (!OfficeCodeCatalog.TryNormalizeWarehouseCode(dto.WarehouseCode, out var normalizedWarehouseCode))
            {
                itemIdsBlockingOmittedRowDeletion.Add(dto.ItemId);
                AddInvalidItemWarehouseStockConflict(
                    dto,
                    InvalidItemWarehouseStockWarehouseIdentityToken,
                    "Warehouse stock row has an unknown warehouse code.",
                    result);
                AddNotice(
                    result,
                    nameof(ItemWarehouseStock),
                    dto.ItemId,
                    "item-warehouse-stock-skip-unknown-warehouse-code",
                    "알 수 없는 창고 코드가 포함된 재고 수량 행은 서버 반영에서 제외하고 누락 창고 삭제도 수행하지 않았습니다.");
                continue;
            }

            incomingRows.Add(new ItemWarehouseStockDto
            {
                ItemId = dto.ItemId,
                WarehouseCode = normalizedWarehouseCode,
                Quantity = dto.Quantity,
                UpdatedAtUtc =
                    ItemWarehouseStockMutationReceipt
                        .NormalizeUpdatedAtUtc(
                            dto.UpdatedAtUtc),
                Revision = dto.Revision,
                ExpectedRevision = dto.ExpectedRevision
            });
        }

        var normalizedGroups = incomingRows
            .GroupBy(dto => new { dto.ItemId, dto.WarehouseCode })
            .ToList();
        foreach (var duplicateGroup in normalizedGroups.Where(group => group.Count() > 1))
        {
            itemIdsBlockingOmittedRowDeletion.Add(duplicateGroup.Key.ItemId);
            foreach (var duplicate in duplicateGroup.SkipLast(1))
            {
                AddClientConflict(
                    duplicate,
                    nameof(ItemWarehouseStock),
                    "Multiple warehouse stock rows normalize to the same item and warehouse key.",
                    result);
            }

            AddNotice(
                result,
                nameof(ItemWarehouseStock),
                duplicateGroup.Key.ItemId,
                "item-warehouse-stock-normalized-duplicate-key",
                $"같은 품목/창고({duplicateGroup.Key.WarehouseCode})로 정규화된 재고 수량 {duplicateGroup.Count():N0}건 중 마지막 행만 반영하고 누락 창고 삭제는 수행하지 않았습니다.");
        }

        var sanitized = normalizedGroups
            .Select(group => group.Last())
            .ToList();
        var completeSnapshotMarkersByItemId =
            snapshotMarkers
                .Where(marker =>
                    marker.ItemId != Guid.Empty)
                .GroupBy(marker => marker.ItemId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Max(marker =>
                        Math.Max(
                            0,
                            marker.MaxKnownRevision)));
        var receiptIdentitiesByDto = sanitized.ToDictionary(
            dto => dto,
            dto => ItemWarehouseStockMutationReceipt.Create(
                dto,
                deviceId));
        await LoadProcessedMutationCacheEntriesAsync(
            receiptIdentitiesByDto.Values
                .Select(identity => identity.MutationId)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            cancellationToken);

        var groupedByItem = sanitized
            .GroupBy(dto => dto.ItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var dto in sanitized)
        {
            var item = await _dbContext.Items.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == dto.ItemId, cancellationToken);
            if (item is null)
            {
                missingItemCount++;
                itemIdsBlockingOmittedRowDeletion.Add(dto.ItemId);
                AddClientConflict(
                    dto,
                    nameof(ItemWarehouseStock),
                    "Warehouse stock row references a missing item.",
                    result);
                continue;
            }

            if (!_officeScopeService.CanWriteOfficeForItems(item.OfficeCode, item.TenantCode))
            {
                outOfScopeItemCount++;
                itemIdsBlockingOmittedRowDeletion.Add(dto.ItemId);
                AddClientConflict(
                    dto,
                    nameof(ItemWarehouseStock),
                    "Current account cannot modify this item scope.",
                    result);
                continue;
            }

            if (!_officeScopeService.CanWriteWarehouse(dto.WarehouseCode, item.OfficeCode))
            {
                outOfScopeWarehouseCount++;
                itemIdsBlockingOmittedRowDeletion.Add(dto.ItemId);
                AddClientConflict(
                    dto,
                    nameof(ItemWarehouseStock),
                    "Current account cannot modify this warehouse scope.",
                    result);
                continue;
            }

            var receiptIdentity =
                receiptIdentitiesByDto[dto];
            if (_processedMutationsById.TryGetValue(
                    receiptIdentity.MutationId,
                    out var processedMutation))
            {
                if (ItemWarehouseStockReceiptMatches(
                        processedMutation,
                        receiptIdentity))
                {
                    AcknowledgeAcceptedStock(dto);
                    continue;
                }

                itemIdsBlockingOmittedRowDeletion.Add(dto.ItemId);
                AddClientConflict(
                    dto,
                    nameof(ItemWarehouseStock),
                    "Warehouse stock snapshot receipt belongs to a different device, entity, revision, or payload.",
                    result);
                continue;
            }

            if (item.IsDeleted)
            {
                deletedItemCount++;
                itemIdsBlockingOmittedRowDeletion.Add(dto.ItemId);
                AddClientConflict(
                    dto,
                    nameof(ItemWarehouseStock),
                    "Warehouse stock row references a deleted item.",
                    result);
                continue;
            }

            if (!ItemOperationalPolicy.SupportsInventory(item.TrackingType))
            {
                nonInventoryItemIds.Add(dto.ItemId);
                AddClientConflict(
                    dto,
                    nameof(ItemWarehouseStock),
                    "Warehouse stock row references a non-inventory item.",
                    result);
                continue;
            }

            var entity = await _dbContext.ItemWarehouseStocks
                .FirstOrDefaultAsync(x => x.ItemId == dto.ItemId && x.WarehouseCode == dto.WarehouseCode, cancellationToken);

            if (entity is null)
            {
                var expectedExistingRevision = dto.ExpectedRevision > 0
                    ? dto.ExpectedRevision
                    : dto.Revision;
                if (expectedExistingRevision > 0)
                {
                    itemIdsBlockingOmittedRowDeletion.Add(dto.ItemId);
                    AddClientConflict(
                        dto,
                        nameof(ItemWarehouseStock),
                        $"{BuildExpectedRevisionConflictReason(expectedExistingRevision, 0)}. Server warehouse stock row no longer exists.",
                        result,
                        new ItemWarehouseStockDto
                        {
                            ItemId = dto.ItemId,
                            WarehouseCode = dto.WarehouseCode,
                            Quantity = 0m,
                            UpdatedAtUtc = DateTime.UnixEpoch,
                            Revision = 0,
                            ExpectedRevision = expectedExistingRevision,
                            IsDeleted = true
                        });
                    continue;
                }

                _dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
                {
                    ItemId = dto.ItemId,
                    WarehouseCode = dto.WarehouseCode,
                    Quantity = dto.Quantity,
                    UpdatedAtUtc = NormalizeUtc(dto.UpdatedAtUtc)
                });
                affectedItemIds.Add(dto.ItemId);
                AcknowledgeAcceptedStock(dto);
                MarkAppliedStock(dto, originalQuantity: 0m);
                RegisterItemWarehouseStockReceipt(
                    receiptIdentity);
                continue;
            }

            if (dto.ExpectedRevision > 0 && entity.Revision != dto.ExpectedRevision)
            {
                itemIdsBlockingOmittedRowDeletion.Add(dto.ItemId);
                await AddServerConflictAsync(
                    dto,
                    entity,
                    nameof(ItemWarehouseStock),
                    BuildExpectedRevisionConflictReason(dto.ExpectedRevision, entity.Revision),
                    result,
                    cancellationToken);
                continue;
            }

            if (dto.ExpectedRevision <= 0 &&
                dto.Revision > 0 &&
                entity.Revision > dto.Revision)
            {
                itemIdsBlockingOmittedRowDeletion.Add(dto.ItemId);
                await AddServerConflictAsync(dto, entity, nameof(ItemWarehouseStock), "Server version is newer.", result, cancellationToken);
                continue;
            }

            if (dto.ExpectedRevision <= 0 &&
                dto.Revision <= 0)
            {
                if (entity.Quantity == dto.Quantity &&
                    NormalizeUtc(entity.UpdatedAtUtc) ==
                    NormalizeUtc(dto.UpdatedAtUtc))
                {
                    AcknowledgeAcceptedStock(dto);
                    MarkAppliedStock(
                        dto,
                        entity.Quantity);
                    RegisterItemWarehouseStockReceipt(
                        receiptIdentity);
                    continue;
                }

                itemIdsBlockingOmittedRowDeletion.Add(dto.ItemId);
                await AddServerConflictAsync(
                    dto,
                    entity,
                    nameof(ItemWarehouseStock),
                    "An existing warehouse stock row requires a matching revision; zero-revision payloads may only confirm an identical snapshot.",
                    result,
                    cancellationToken);
                continue;
            }

            var originalQuantity = entity.Quantity;
            entity.Quantity = dto.Quantity;
            entity.UpdatedAtUtc = NormalizeUtc(dto.UpdatedAtUtc);
            affectedItemIds.Add(dto.ItemId);
            AcknowledgeAcceptedStock(dto);
            MarkAppliedStock(
                dto,
                originalQuantity);
            RegisterItemWarehouseStockReceipt(
                receiptIdentity);
        }

        var snapshotInvariantItemIds =
            completeSnapshotMarkersByItemId.Keys
                .Concat(nonInventoryItemIds)
                .Distinct()
                .ToList();
        foreach (var itemId in snapshotInvariantItemIds)
        {
            var hasCompleteSnapshotMarker =
                completeSnapshotMarkersByItemId
                    .TryGetValue(
                        itemId,
                        out var markerMaxKnownRevision);
            var scopedItem = await _dbContext.Items.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == itemId, cancellationToken);
            if (scopedItem is null || scopedItem.IsDeleted || !_officeScopeService.CanWriteOfficeForItems(scopedItem.OfficeCode, scopedItem.TenantCode))
                continue;

            if (!ItemOperationalPolicy.SupportsInventory(scopedItem.TrackingType))
            {
                nonInventoryItemIds.Add(itemId);
                var nonInventoryRows = await _officeScopeService.ApplyWarehouseScope(_dbContext.ItemWarehouseStocks)
                    .Where(x => x.ItemId == itemId)
                    .ToListAsync(cancellationToken);
                if (nonInventoryRows.Count > 0)
                {
                    _dbContext.ItemWarehouseStocks.RemoveRange(nonInventoryRows);
                    scopedItem.CurrentStock = 0m;
                    scopedItem.SafetyStock = 0m;
                    scopedItem.UpdatedAtUtc = DateTime.UtcNow;
                    affectedItemIds.Add(itemId);
                }

                continue;
            }

            if (!hasCompleteSnapshotMarker)
                continue;

            // Omission means deletion only after every incoming row for this item is writable
            // and has passed its revision check. Non-conflicting rows above remain independently accepted.
            if (itemIdsBlockingOmittedRowDeletion.Contains(itemId))
                continue;

            var itemSnapshotRows =
                groupedByItem.GetValueOrDefault(itemId) ??
                [];
            var desiredCodes = itemSnapshotRows
                .Select(stock => stock.WarehouseCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var maxKnownRevision = itemSnapshotRows
                .Select(stock => Math.Max(stock.ExpectedRevision, stock.Revision))
                .Append(markerMaxKnownRevision)
                .Max();

            var staleRows = await _officeScopeService.ApplyWarehouseScope(_dbContext.ItemWarehouseStocks)
                .Where(x => x.ItemId == itemId && !desiredCodes.Contains(x.WarehouseCode))
                .ToListAsync(cancellationToken);
            var writableStaleRows = staleRows
                .Where(row => _officeScopeService.CanWriteWarehouse(row.WarehouseCode, scopedItem.OfficeCode))
                .ToList();
            var readOnlyStaleRowCount = staleRows.Count - writableStaleRows.Count;
            if (readOnlyStaleRowCount > 0)
            {
                AddNotice(
                    result,
                    nameof(ItemWarehouseStock),
                    itemId,
                    "item-warehouse-stock-preserve-read-only-row",
                    $"재고 수량 {readOnlyStaleRowCount:N0}건은 현재 계정에 해당 창고 쓰기 권한이 없어 삭제하지 않았습니다.");
            }

            staleRows = writableStaleRows;
            var protectedStaleRows = staleRows
                .Where(row => maxKnownRevision <= 0 || row.Revision > maxKnownRevision)
                .ToList();
            if (protectedStaleRows.Count > 0)
            {
                AddNotice(
                    result,
                    nameof(ItemWarehouseStock),
                    itemId,
                    "item-warehouse-stock-preserve-newer-server-row",
                    $"재고 수량 {protectedStaleRows.Count:N0}건은 서버에 더 최신 창고 행이 있어 삭제하지 않았습니다.");
            }

            staleRows = staleRows
                .Except(protectedStaleRows)
                .ToList();
            var now = DateTime.UtcNow;
            var zeroedRowCount = 0;
            foreach (var staleRow in staleRows)
            {
                var originalQuantity = staleRow.Quantity;
                MarkAppliedStock(
                    new ItemWarehouseStockDto
                    {
                        ItemId = staleRow.ItemId,
                        WarehouseCode = staleRow.WarehouseCode
                    },
                    originalQuantity);
                if (staleRow.Quantity == 0m)
                    continue;

                staleRow.Quantity = 0m;
                staleRow.UpdatedAtUtc = now;
                zeroedRowCount++;
            }

            if (zeroedRowCount > 0)
            {
                scopedItem.UpdatedAtUtc = now;
                affectedItemIds.Add(itemId);
            }
        }

        if (missingItemCount > 0)
        {
            AddNotice(
                result,
                nameof(ItemWarehouseStock),
                Guid.Empty,
                "item-warehouse-stock-skip-missing-item",
                $"재고 수량 {missingItemCount:N0}건은 참조 품목을 찾지 못해 서버 반영에서 제외했습니다.");
        }

        if (deletedItemCount > 0)
        {
            AddNotice(
                result,
                nameof(ItemWarehouseStock),
                Guid.Empty,
                "item-warehouse-stock-skip-deleted-item",
                $"재고 수량 {deletedItemCount:N0}건은 삭제된 품목을 참조해 서버 반영에서 제외했습니다.");
        }

        if (outOfScopeItemCount > 0)
        {
            AddNotice(
                result,
                nameof(ItemWarehouseStock),
                Guid.Empty,
                "item-warehouse-stock-skip-out-of-scope-item",
                $"재고 수량 {outOfScopeItemCount:N0}건은 현재 계정이 수정할 수 없는 품목 범위라 서버 반영에서 제외했습니다.");
        }

        if (outOfScopeWarehouseCount > 0)
        {
            AddNotice(
                result,
                nameof(ItemWarehouseStock),
                Guid.Empty,
                "item-warehouse-stock-skip-out-of-scope-warehouse",
                $"재고 수량 {outOfScopeWarehouseCount:N0}건은 현재 계정이 수정할 수 없는 창고 범위라 서버 반영에서 제외했습니다.");
        }
        if (nonInventoryItemIds.Count > 0)
        {
            AddNotice(
                result,
                nameof(ItemWarehouseStock),
                Guid.Empty,
                "item-warehouse-stock-skip-non-inventory-item",
                $"재고 추적 대상이 아닌 품목의 창고 수량 {nonInventoryItemIds.Count:N0}건은 서버 반영에서 제외했습니다.");
        }

        return new ItemWarehouseStockUpsertResult(
            affectedItemIds,
            appliedStockKeys,
            originalQuantitiesByAppliedKey);
    }

    private static string BuildItemWarehouseStockSnapshotKey(Guid itemId, string? warehouseCode)
        => $"{itemId:N}|{OfficeCodeCatalog.NormalizeWarehouseCodeLoose(warehouseCode)}";

    private static bool ItemWarehouseStockReceiptMatches(
        ProcessedSyncMutation processedMutation,
        ItemWarehouseStockReceiptIdentity identity)
        => string.Equals(
               processedMutation.DeviceId,
               identity.DeviceId,
               StringComparison.Ordinal) &&
           string.Equals(
               processedMutation.EntityName,
               nameof(ItemWarehouseStock),
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               processedMutation.EntityId,
               identity.EntityId,
               StringComparison.OrdinalIgnoreCase) &&
           processedMutation.ExpectedRevision ==
           identity.ExpectedRevision &&
           string.Equals(
               processedMutation.PayloadHash,
               identity.PayloadHash,
               StringComparison.OrdinalIgnoreCase);

    private void RegisterItemWarehouseStockReceipt(
        ItemWarehouseStockReceiptIdentity identity)
    {
        if (_processedMutationsById.ContainsKey(
                identity.MutationId))
        {
            return;
        }

        var processedMutation = new ProcessedSyncMutation
        {
            MutationId = identity.MutationId,
            DeviceId = identity.DeviceId,
            EntityName = nameof(ItemWarehouseStock),
            EntityId = identity.EntityId,
            ExpectedRevision =
                identity.ExpectedRevision,
            PayloadHash = identity.PayloadHash,
            ProcessedAtUtc = DateTime.UtcNow
        };
        _dbContext.ProcessedSyncMutations.Add(
            processedMutation);
        _processedMutationsById.Add(
            identity.MutationId,
            processedMutation);
    }

    private sealed record ItemWarehouseStockUpsertResult(
        HashSet<Guid> AffectedItemIds,
        HashSet<string> AppliedStockKeys,
        Dictionary<
            InvoiceStockSnapshotService.InvoiceStockKey,
            decimal> OriginalQuantitiesByAppliedKey);

    private sealed class InventoryTransferStockAtomicityRollbackException
        : Exception
    {
    }

    private sealed class RentalProfileAssetAtomicityRollbackException
        : Exception
    {
    }

    private const string InvalidItemWarehouseStockItemIdentityToken = "INVALID-ITEM-ID";
    private const string InvalidItemWarehouseStockWarehouseIdentityToken = "INVALID-WAREHOUSE-CODE";

    private void AddInvalidItemWarehouseStockConflict(
        ItemWarehouseStockDto client,
        string warehouseIdentityToken,
        string reason,
        SyncPushResult result)
    {
        var sanitizedClient = new ItemWarehouseStockDto
        {
            ItemId = client.ItemId,
            WarehouseCode = warehouseIdentityToken,
            Quantity = client.Quantity,
            UpdatedAtUtc = client.UpdatedAtUtc,
            Revision = client.Revision,
            ExpectedRevision = client.ExpectedRevision,
            IsDeleted = client.IsDeleted
        };

        AddClientConflict(
            sanitizedClient,
            nameof(ItemWarehouseStock),
            reason,
            result);
    }

    private async Task RecalculateItemCurrentStocksFromWarehousesAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
            return;

        var stockRows = await _dbContext.ItemWarehouseStocks
            .Where(stock => itemIds.Contains(stock.ItemId))
            .Select(stock => new { stock.ItemId, stock.Quantity })
            .ToListAsync(cancellationToken);

        var stockTotals = stockRows
            .GroupBy(stock => stock.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(stock => stock.Quantity));

        var items = await _dbContext.Items
            .IgnoreQueryFilters()
            .Where(item => itemIds.Contains(item.Id))
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            var recalculated = ItemOperationalPolicy.SupportsInventory(item.TrackingType) &&
                               stockTotals.TryGetValue(item.Id, out var stockTotal)
                ? stockTotal
                : 0m;

            if (item.CurrentStock == recalculated)
                continue;

            item.CurrentStock = recalculated;
            item.UpdatedAtUtc = now;
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
        var lineAmount = line.LineAmount == 0 ? line.Quantity * line.UnitPrice : line.LineAmount;
        entity.Id = resolvedId;
        entity.InvoiceId = invoiceId;
        entity.ItemId = line.ItemId;
        entity.ItemNameOriginal = line.ItemNameOriginal;
        entity.SpecificationOriginal = line.SpecificationOriginal;
        entity.Unit = line.Unit;
        entity.Quantity = line.Quantity;
        entity.UnitPrice = line.UnitPrice;
        entity.LineAmount = lineAmount;
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

    private static InventoryTransferLine CreateInventoryTransferLine(Guid transferId, InventoryTransferLineDto line, Guid resolvedId)
    {
        var entity = new InventoryTransferLine();
        ApplyInventoryTransferLine(entity, transferId, line, resolvedId);
        return entity;
    }

    private static void ApplyInventoryTransferLine(
        InventoryTransferLine entity,
        Guid transferId,
        InventoryTransferLineDto line,
        Guid resolvedId)
    {
        entity.Id = resolvedId;
        entity.TransferId = transferId;
        entity.ItemId = line.ItemId;
        entity.ItemNameOriginal = line.ItemNameOriginal;
        entity.SpecificationOriginal = line.SpecificationOriginal;
        entity.Unit = line.Unit;
        entity.Quantity = line.Quantity;
        entity.ReceivedQuantity = line.ReceivedQuantity;
        entity.QuantityDifference = line.QuantityDifference;
        entity.Remark = line.Remark;
        entity.ReceiptRemark = line.ReceiptRemark;
        entity.IsDeleted = line.IsDeleted;
    }

    private static void ApplyInvoiceLines(Invoice invoice, IEnumerable<InvoiceLineDto> lines)
    {
        if (invoice.IsDeleted)
            return;

        var order = 1;
        foreach (var line in lines)
        {
            if (line.IsDeleted)
                continue;

            invoice.Lines.Add(CreateInvoiceLine(invoice.Id, line, line.Id == Guid.Empty ? Guid.NewGuid() : line.Id, order++));
        }
    }

    private static void SoftDeleteInvoicePreservingSnapshot(Invoice invoice)
    {
        invoice.IsDeleted = true;
        invoice.IsLatestVersion = false;
        foreach (var line in invoice.Lines)
            line.IsDeleted = true;
    }

    private static void ApplyInventoryTransferLines(InventoryTransfer transfer, IEnumerable<InventoryTransferLineDto> lines)
    {
        foreach (var line in lines)
        {
            if (line.IsDeleted)
                continue;

            transfer.Lines.Add(CreateInventoryTransferLine(transfer.Id, line, line.Id == Guid.Empty ? Guid.NewGuid() : line.Id));
        }
    }

    private async Task<bool> ExistsOrTrackedAsync<TEntity>(
        DbSet<TEntity> dbSet, Guid id, CancellationToken cancellationToken)
        where TEntity : TrackedEntity
    {
        if (id == Guid.Empty)
        {
            return false;
        }

        if (dbSet.Local.Any(x => x.Id == id && !x.IsDeleted))
        {
            return true;
        }

        return await dbSet.IgnoreQueryFilters().AnyAsync(x => x.Id == id && !x.IsDeleted, cancellationToken);
    }

    private void AddClientConflict<TDto>(
        TDto client,
        string entityName,
        string reason,
        SyncPushResult result,
        object? serverSnapshot = null)
    {
        var entityId = client switch
        {
            ItemWarehouseStockDto stock => $"{stock.ItemId:D}|{stock.WarehouseCode}",
            SyncEntityDto entity => entity.Id.ToString(),
            _ => string.Empty
        };

        var conflict = new ConflictLog
        {
            UserId = _currentUserContext.UserId,
            Username = _currentUserContext.Username,
            EntityName = entityName,
            EntityId = entityId,
            ClientJson = JsonSerializer.Serialize(client, ConflictJsonOptions),
            ServerJson = serverSnapshot is null
                ? string.Empty
                : JsonSerializer.Serialize(serverSnapshot, ConflictJsonOptions),
            Reason = reason,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.ConflictLogs.Add(conflict);
        result.ConflictCount++;
        result.Conflicts.Add(conflict.ToDto());
    }

    private static void AddNotice(
        SyncPushResult result,
        string entityName,
        Guid entityId,
        string code,
        string message)
    {
        var normalizedMessage = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedMessage))
            return;

        var normalizedEntityName = (entityName ?? string.Empty).Trim();
        var normalizedCode = (code ?? string.Empty).Trim();
        var entityIdText = entityId == Guid.Empty ? string.Empty : entityId.ToString("D");

        if (result.Notices.Any(existing =>
                string.Equals(existing.EntityName, normalizedEntityName, StringComparison.Ordinal) &&
                string.Equals(existing.EntityId, entityIdText, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Code, normalizedCode, StringComparison.Ordinal) &&
                string.Equals(existing.Message, normalizedMessage, StringComparison.Ordinal)))
        {
            return;
        }

        result.Notices.Add(new SyncNoticeDto
        {
            EntityName = normalizedEntityName,
            EntityId = entityIdText,
            Code = normalizedCode,
            Message = normalizedMessage
        });
    }

    private static void ResetRolledBackSyncPushResult(
        SyncPushResult result)
    {
        result.AcceptedCount = 0;
        result.DuplicateMutationCount = 0;
        result.AcceptedRevisions.Clear();
        result.PurgeRecords.Clear();
        result.AcceptedItemWarehouseStockKeys.Clear();
        result.AssignedInvoiceNumbers.Clear();
        result.AssignedTaxInvoiceNumbers.Clear();
        result.Notices.Clear();
        result.ConflictCount = result.Conflicts.Count;
        result.CurrentServerRevision = 0;
    }

    private Task AddServerConflictAsync<TDto, TEntity>(
        TDto client,
        TEntity server,
        string entityName,
        string reason,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var conflict = BuildConflict(client, server, entityName, reason);
        _dbContext.ConflictLogs.Add(conflict);
        result.ConflictCount++;

        var dto = conflict.ToDto();
        result.Conflicts.Add(dto);
        return Task.CompletedTask;
    }

    private ConflictLog BuildConflict<TDto, TEntity>(TDto client, TEntity server, string entityName, string reason)
    {
        return new ConflictLog
        {
            UserId = _currentUserContext.UserId,
            Username = _currentUserContext.Username,
            EntityName = entityName,
            EntityId = server switch
            {
                TrackedEntity tracked => tracked.Id.ToString(),
                ItemWarehouseStock stock => $"{stock.ItemId:D}|{stock.WarehouseCode}",
                _ => string.Empty
            },
            ClientJson = JsonSerializer.Serialize(client, ConflictJsonOptions),
            ServerJson = SerializeConflictServerSnapshot(server),
            Reason = reason,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private async Task PopulateServerConflictActorsAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        DateTime pushStartedAtUtc,
        CancellationToken cancellationToken)
    {
        var serverConflicts = conflicts
            .Where(conflict => !string.IsNullOrWhiteSpace(conflict.ServerJson))
            .Select(conflict => new
            {
                Conflict = conflict,
                Fingerprint = new AuditEntityFingerprint(
                    (conflict.EntityName ?? string.Empty).Trim(),
                    (conflict.EntityId ?? string.Empty).Trim())
            })
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Fingerprint.EntityName) &&
                !string.IsNullOrWhiteSpace(candidate.Fingerprint.EntityId))
            .ToList();
        if (serverConflicts.Count == 0)
            return;

        var latestAuditsByFingerprint = new Dictionary<AuditEntityFingerprint, AuditActorCandidate>();
        foreach (var fingerprintBatch in serverConflicts
                     .Select(candidate => candidate.Fingerprint)
                     .Distinct()
                     .Chunk(100))
        {
            var batch = fingerprintBatch.ToArray();
            var batchFingerprints = batch.ToHashSet();
            var entityNames = batch
                .Select(fingerprint => fingerprint.EntityName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var entityIds = batch
                .Select(fingerprint => fingerprint.EntityId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var auditCandidates = await _dbContext.AuditLogs
                .AsNoTracking()
                .Where(audit =>
                    audit.CreatedAtUtc < pushStartedAtUtc &&
                    entityNames.Contains(audit.EntityName) &&
                    entityIds.Contains(audit.EntityId))
                .GroupBy(audit => new
                {
                    audit.EntityName,
                    audit.EntityId
                })
                .Select(group => group
                    .OrderByDescending(audit => audit.CreatedAtUtc)
                    .ThenByDescending(audit => audit.Id)
                    .Select(audit => new AuditActorCandidate(
                        audit.Id,
                        audit.EntityName,
                        audit.EntityId,
                        audit.UserId,
                        audit.Username,
                        audit.CreatedAtUtc))
                    .First())
                .ToListAsync(cancellationToken);

            foreach (var latestAudit in auditCandidates
                         .Where(candidate => batchFingerprints.Contains(candidate.Fingerprint))
                         .GroupBy(candidate => candidate.Fingerprint)
                         .Select(group => group
                             .OrderByDescending(candidate => candidate.CreatedAtUtc)
                             .ThenByDescending(candidate => candidate.Id)
                             .First()))
            {
                latestAuditsByFingerprint[latestAudit.Fingerprint] = latestAudit;
            }
        }

        foreach (var candidate in serverConflicts)
        {
            if (!latestAuditsByFingerprint.TryGetValue(candidate.Fingerprint, out var latestAudit))
                continue;

            candidate.Conflict.ServerUserId = latestAudit.UserId;
            candidate.Conflict.ServerUsername = latestAudit.Username;
        }
    }

    private sealed record AuditEntityFingerprint(
        string EntityName,
        string EntityId);

    private sealed record AuditActorCandidate(
        Guid Id,
        string EntityName,
        string EntityId,
        Guid? UserId,
        string Username,
        DateTime CreatedAtUtc)
    {
        public AuditEntityFingerprint Fingerprint => new(EntityName, EntityId);
    }

    private static string SerializeConflictServerSnapshot(object? server)
    {
        var snapshot = server switch
        {
            null => null,
            CompanyProfile entity => entity.ToDto(),
            Unit entity => entity.ToDto(),
            CustomerCategory entity => entity.ToDto(),
            CustomerMaster entity => entity.ToDto(),
            Customer entity => entity.ToDto(),
            CustomerContract entity => entity.ToDto(false),
            Item entity => entity.ToDto(),
            ItemWarehouseStock entity => entity.ToDto(),
            TransactionRecord entity => entity.ToDto(),
            TransactionAttachment entity => entity.ToDto(false),
            InventoryTransfer entity => entity.ToDto(),
            RentalManagementCompany entity => entity.ToDto(),
            RentalBillingProfile entity => entity.ToDto(),
            RentalAsset entity => entity.ToDto(),
            RentalBillingLog entity => entity.ToDto(),
            Invoice entity => entity.ToDto(),
            Payment entity => entity.ToDto(),
            _ => CreateScalarSnapshot(server)
        };

        return JsonSerializer.Serialize(snapshot, ConflictJsonOptions);
    }

    private static object CreateScalarSnapshot(object server)
    {
        var type = server.GetType();
        var dict = new Dictionary<string, object?>();

        foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            var propertyType = property.PropertyType;
            if (propertyType != typeof(string) &&
                typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType))
            {
                continue;
            }

            if (!propertyType.IsPrimitive &&
                propertyType != typeof(string) &&
                propertyType != typeof(Guid) && propertyType != typeof(Guid?) &&
                propertyType != typeof(DateTime) && propertyType != typeof(DateTime?) &&
                propertyType != typeof(DateOnly) && propertyType != typeof(DateOnly?) &&
                propertyType != typeof(decimal) && propertyType != typeof(decimal?) &&
                propertyType != typeof(int) && propertyType != typeof(int?) &&
                propertyType != typeof(long) && propertyType != typeof(long?) &&
                propertyType != typeof(bool) && propertyType != typeof(bool?))
            {
                continue;
            }

            dict[property.Name] = property.GetValue(server);
        }

        return dict;
    }

    private async Task<Dictionary<Guid, DateTime>> CaptureDeletedCustomerRestoreGenerationsAsync(
        IEnumerable<CustomerDto> customers,
        CancellationToken cancellationToken)
    {
        var customerIds = customers
            .Where(customer => !customer.IsDeleted && customer.Id != Guid.Empty)
            .Select(customer => customer.Id)
            .Distinct()
            .ToList();
        var generations = new Dictionary<Guid, DateTime>();
        foreach (var customerIdBatch in customerIds.Chunk(500))
        {
            var batch = customerIdBatch.ToArray();
            var deletedCustomers = await _dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => batch.Contains(customer.Id) && customer.IsDeleted)
                .Select(customer => new { customer.Id, customer.UpdatedAtUtc })
                .ToListAsync(cancellationToken);
            foreach (var customer in deletedCustomers)
                generations[customer.Id] = customer.UpdatedAtUtc;
        }

        return generations;
    }

    private async Task RestoreAcceptedCustomerDeletionGenerationContractsAsync(
        IEnumerable<CustomerDto> acceptedCustomers,
        IReadOnlyDictionary<Guid, DateTime> restoreGenerations,
        CancellationToken cancellationToken)
    {
        var customerIds = acceptedCustomers
            .Where(customer => !customer.IsDeleted && restoreGenerations.ContainsKey(customer.Id))
            .Select(customer => customer.Id)
            .Distinct()
            .ToList();
        if (customerIds.Count == 0)
            return;

        var customersById = _dbContext.Customers.Local
            .Where(customer => customerIds.Contains(customer.Id) && !customer.IsDeleted)
            .ToDictionary(customer => customer.Id);
        foreach (var customerIdBatch in customerIds
                     .Where(customerId => !customersById.ContainsKey(customerId))
                     .Chunk(500))
        {
            var batch = customerIdBatch.ToArray();
            var customers = await _dbContext.Customers
                .IgnoreQueryFilters()
                .Where(customer => batch.Contains(customer.Id) && !customer.IsDeleted)
                .ToListAsync(cancellationToken);
            foreach (var customer in customers)
                customersById[customer.Id] = customer;
        }

        await RestoreDeletedContractsForCustomerGenerationsAsync(
            customersById,
            restoreGenerations,
            cancellationToken);
    }

    private async Task CascadeDeletedCustomerContractsAsync(
        IEnumerable<CustomerDto> customers,
        CancellationToken cancellationToken)
    {
        var deletedCustomerIds = customers
            .Where(customer => customer.IsDeleted)
            .Select(customer => customer.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (deletedCustomerIds.Count == 0)
            return;

        var contracts = await _dbContext.CustomerContracts
            .IgnoreQueryFilters()
            .Where(contract => deletedCustomerIds.Contains(contract.CustomerId) && !contract.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var contract in contracts)
        {
            contract.IsDeleted = true;
            contract.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private async Task<IReadOnlyDictionary<Guid, Customer>> RestoreDeletedLinkedCustomersAndContractsAsync(
        IEnumerable<Guid> requestedCustomerIds,
        CancellationToken cancellationToken,
        RentalBillingProfilePushSnapshot? pushSnapshot = null)
    {
        var customerIds = requestedCustomerIds
            .Where(customerId => customerId != Guid.Empty)
            .Distinct()
            .ToList();
        if (customerIds.Count == 0)
            return new Dictionary<Guid, Customer>();

        var customersById = new Dictionary<Guid, Customer>();
        foreach (var customerId in customerIds)
        {
            var customer = pushSnapshot?.FindCustomer(customerId);
            if (customer is not null)
                customersById[customerId] = customer;
        }
        foreach (var customerIdBatch in customerIds
                     .Where(customerId => !customersById.ContainsKey(customerId))
                     .Chunk(500))
        {
            var batch = customerIdBatch.ToArray();
            var customers = await _dbContext.Customers
                .IgnoreQueryFilters()
                .Where(customer => batch.Contains(customer.Id))
                .ToListAsync(cancellationToken);
            foreach (var customer in customers)
                customersById[customer.Id] = customer;
        }

        var restoredCustomers = customersById.Values
            .Where(customer =>
                !customer.IsDeleted ||
                _officeScopeService.CanWriteOfficeForCustomers(
                    customer.ResponsibleOfficeCode,
                    customer.TenantCode,
                    customer.OfficeCode))
            .ToDictionary(customer => customer.Id);
        var deletedCustomers = restoredCustomers.Values
            .Where(customer => customer.IsDeleted)
            .ToList();
        await RestoreDeletedContractsForCustomerGenerationsAsync(
            deletedCustomers.ToDictionary(customer => customer.Id),
            deletedCustomers.ToDictionary(customer => customer.Id, customer => customer.UpdatedAtUtc),
            cancellationToken);

        foreach (var customer in deletedCustomers)
            customer.IsDeleted = false;

        return restoredCustomers;
    }

    private async Task RestoreDeletedContractsForCustomerGenerationsAsync(
        IReadOnlyDictionary<Guid, Customer> customersById,
        IReadOnlyDictionary<Guid, DateTime> restoreGenerations,
        CancellationToken cancellationToken)
    {
        var contractWritableCustomers = customersById.Values
            .Where(customer => restoreGenerations.ContainsKey(customer.Id))
            .Where(customer => _officeScopeService.CanWriteOfficeForContracts(
                customer.ResponsibleOfficeCode,
                customer.TenantCode,
                customer.OfficeCode))
            .ToDictionary(customer => customer.Id);

        var matchingDeletedContracts = new List<CustomerContract>();
        foreach (var customerIdBatch in contractWritableCustomers.Keys.Chunk(500))
        {
            var batch = customerIdBatch.ToArray();
            var deletedContracts = await _dbContext.CustomerContracts
                .IgnoreQueryFilters()
                .Where(contract => batch.Contains(contract.CustomerId) && contract.IsDeleted)
                .ToListAsync(cancellationToken);
            matchingDeletedContracts.AddRange(deletedContracts.Where(contract =>
                contract.UpdatedAtUtc == restoreGenerations[contract.CustomerId]));
        }

        var primaryRestoreCustomerIds = matchingDeletedContracts
            .Where(contract => contract.IsPrimary)
            .Select(contract => contract.CustomerId)
            .Distinct()
            .ToList();
        foreach (var customerIdBatch in primaryRestoreCustomerIds.Chunk(500))
        {
            var batch = customerIdBatch.ToArray();
            var otherActivePrimaryContracts = await _dbContext.CustomerContracts
                .IgnoreQueryFilters()
                .Where(contract =>
                    batch.Contains(contract.CustomerId) &&
                    !contract.IsDeleted &&
                    contract.IsPrimary)
                .ToListAsync(cancellationToken);
            foreach (var other in otherActivePrimaryContracts)
                other.IsPrimary = false;
        }

        foreach (var contract in matchingDeletedContracts)
            contract.IsDeleted = false;
    }

    private async Task PersistCustomerContractsToStorageAsync(
        IEnumerable<CustomerContractDto> contracts,
        ICollection<string> savedStoragePaths,
        ICollection<string> replacedStoragePaths,
        CancellationToken cancellationToken)
    {
        foreach (var dto in contracts.Where(current => !current.IsDeleted && (current.FileContent?.Length ?? 0) > 0))
        {
            var entity = await _dbContext.CustomerContracts
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == dto.Id, cancellationToken);
            if (entity is null)
                continue;

            var previousStoragePath = entity.StoragePath;
            var storedPath = await _fileStorage.SaveBytesAsync(
                "customer-contracts",
                entity.CustomerId.ToString("N"),
                Guid.NewGuid(),
                entity.FileName,
                dto.FileContent ?? [],
                cancellationToken);
            entity.StoragePath = storedPath;
            entity.FileContent = [];
            savedStoragePaths.Add(storedPath);
            AddReplacedStoragePath(replacedStoragePaths, previousStoragePath, storedPath);
        }
    }

    private static string ComputeSha256Hex(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content));

    private async Task PersistTransactionAttachmentsToStorageAsync(
        IEnumerable<TransactionAttachmentDto> attachments,
        ICollection<string> savedStoragePaths,
        ICollection<string> replacedStoragePaths,
        CancellationToken cancellationToken)
    {
        foreach (var dto in attachments.Where(current => !current.IsDeleted && (current.FileContent?.Length ?? 0) > 0))
        {
            var entity = await _dbContext.TransactionAttachments
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == dto.Id, cancellationToken);
            if (entity is null)
                continue;

            var previousStoragePath = entity.StoragePath;
            var storedPath = await _fileStorage.SaveBytesAsync(
                "transaction-attachments",
                entity.TransactionId.ToString("N"),
                Guid.NewGuid(),
                entity.FileName,
                dto.FileContent ?? [],
                cancellationToken);
            entity.StoragePath = storedPath;
            entity.FileContent = [];
            savedStoragePaths.Add(storedPath);
            AddReplacedStoragePath(replacedStoragePaths, previousStoragePath, storedPath);
        }
    }

    private static void AddReplacedStoragePath(ICollection<string> replacedStoragePaths, string? previousStoragePath, string newStoragePath)
    {
        if (string.IsNullOrWhiteSpace(previousStoragePath))
            return;

        if (string.Equals(previousStoragePath.Trim(), newStoragePath.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        replacedStoragePaths.Add(previousStoragePath);
    }

    private async Task RemoveSupersededPurgeRecordsAsync<TDto>(
        string kind,
        IEnumerable<TDto> payload,
        CancellationToken cancellationToken)
        where TDto : SyncEntityDto
    {
        var normalizedKind = NormalizePurgeRecordKind(kind);
        var compatibleKinds = normalizedKind == "rental-billing-profile"
            ? new[] { "rental-billing-profile", "rentalbillingprofile" }
            : new[] { normalizedKind };
        var activeIds = payload
            .Where(current => current.Id != Guid.Empty && !current.IsDeleted)
            .Select(current => current.Id)
            .Distinct()
            .ToList();
        if (activeIds.Count == 0)
            return;

        await _dbContext.RecycleBinPurgeRecords
            .Where(current => compatibleKinds.Contains(current.Kind) && activeIds.Contains(current.EntityId))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<List<RecycleBinPurgeRecord>> FilterSupersededPurgeRecordsAsync(
        IReadOnlyList<RecycleBinPurgeRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
            return [];

        var filtered = new List<RecycleBinPurgeRecord>(records.Count);
        foreach (var record in records)
        {
            if (await IsPurgeRecordSupersededByActiveEntityAsync(record, cancellationToken))
                continue;

            filtered.Add(record);
        }

        return filtered;
    }

    private bool CanReadPurgeRecord(RecycleBinPurgeRecord record)
    {
        var normalizedKind = NormalizePurgeRecordKind(record.Kind);
        if (IsGlobalSettingPurgeRecordKind(normalizedKind))
            return true;

        return normalizedKind switch
        {
            "customer" => _officeScopeService.CanReadOfficeForCustomers(record.OfficeCode, record.TenantCode),
            "contract" => _officeScopeService.CanReadOfficeForContracts(record.OfficeCode, record.TenantCode),
            "item" => _officeScopeService.CanReadOfficeForItems(record.OfficeCode, record.TenantCode),
            "company-profile" or "companyprofile" => _officeScopeService.CanReadOfficeForCompanyProfiles(record.OfficeCode),
            "invoice" => _officeScopeService.CanReadOfficeForSyncInvoices(record.OfficeCode, record.TenantCode),
            "payment" or "transaction" => _officeScopeService.CanReadOfficeForPayments(record.OfficeCode, record.TenantCode),
            "inventory-transfer" or "inventorytransfer" => _officeScopeService.CanReadInventoryTransferPurgeRecord(
                record.SourceOfficeCode,
                record.TargetOfficeCode,
                record.TenantCode,
                record.OfficeCode),
            "rental-management-company" or "rentalmanagementcompany" => _officeScopeService.CanReadOfficeForRentals(record.OfficeCode, record.TenantCode),
            "rental-billing-profile" or "rentalbillingprofile" => _officeScopeService.CanReadOfficeForRentals(record.OfficeCode, record.TenantCode),
            "rental-asset" or "rentalasset" => _officeScopeService.CanReadOfficeForRentals(record.OfficeCode, record.TenantCode),
            "rental-billing-log" or "rentalbillinglog" => _officeScopeService.CanReadOfficeForRentals(record.OfficeCode, record.TenantCode),
            _ => _officeScopeService.CanReadOffice(record.OfficeCode, record.TenantCode)
        };
    }

    private static bool IsGlobalSettingPurgeRecordKind(string? normalizedKind)
        => normalizedKind is "customer-category"
            or "customercategory"
            or "price-grade-option"
            or "pricegradeoption"
            or "trade-type-option"
            or "tradetypeoption"
            or "item-category-option"
            or "itemcategoryoption";

    private Task<bool> IsPurgeRecordSupersededByActiveEntityAsync(
        RecycleBinPurgeRecord record,
        CancellationToken cancellationToken)
    {
        if (record.EntityId == Guid.Empty)
            return Task.FromResult(false);

        return NormalizePurgeRecordKind(record.Kind) switch
        {
            "customer" => HasActiveEntityNewerThanPurgeAsync(_dbContext.Customers.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "contract" => HasActiveEntityNewerThanPurgeAsync(_dbContext.CustomerContracts.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "item" => HasActiveEntityNewerThanPurgeAsync(_dbContext.Items.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "company-profile" => HasActiveEntityNewerThanPurgeAsync(_dbContext.CompanyProfiles.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "companyprofile" => HasActiveEntityNewerThanPurgeAsync(_dbContext.CompanyProfiles.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "customer-category" => HasActiveEntityNewerThanPurgeAsync(_dbContext.CustomerCategories.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "customercategory" => HasActiveEntityNewerThanPurgeAsync(_dbContext.CustomerCategories.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "price-grade-option" => HasActiveEntityNewerThanPurgeAsync(_dbContext.PriceGradeOptions.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "pricegradeoption" => HasActiveEntityNewerThanPurgeAsync(_dbContext.PriceGradeOptions.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "trade-type-option" => HasActiveEntityNewerThanPurgeAsync(_dbContext.TradeTypeOptions.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "tradetypeoption" => HasActiveEntityNewerThanPurgeAsync(_dbContext.TradeTypeOptions.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "item-category-option" => HasActiveEntityNewerThanPurgeAsync(_dbContext.ItemCategoryOptions.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "itemcategoryoption" => HasActiveEntityNewerThanPurgeAsync(_dbContext.ItemCategoryOptions.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "invoice" => HasActiveEntityNewerThanPurgeAsync(_dbContext.Invoices.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "payment" => HasActiveEntityNewerThanPurgeAsync(_dbContext.Payments.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "transaction" => HasActiveEntityNewerThanPurgeAsync(_dbContext.Transactions.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "inventory-transfer" => HasActiveEntityNewerThanPurgeAsync(_dbContext.InventoryTransfers.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "inventorytransfer" => HasActiveEntityNewerThanPurgeAsync(_dbContext.InventoryTransfers.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "rental-management-company" => HasActiveEntityNewerThanPurgeAsync(_dbContext.RentalManagementCompanies.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "rentalmanagementcompany" => HasActiveEntityNewerThanPurgeAsync(_dbContext.RentalManagementCompanies.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "rental-billing-profile" => HasActiveEntityNewerThanPurgeAsync(_dbContext.RentalBillingProfiles.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "rentalbillingprofile" => HasActiveEntityNewerThanPurgeAsync(_dbContext.RentalBillingProfiles.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "rental-asset" => HasActiveEntityNewerThanPurgeAsync(_dbContext.RentalAssets.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "rentalasset" => HasActiveEntityNewerThanPurgeAsync(_dbContext.RentalAssets.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "rental-billing-log" => HasActiveEntityNewerThanPurgeAsync(_dbContext.RentalBillingLogs.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            "rentalbillinglog" => HasActiveEntityNewerThanPurgeAsync(_dbContext.RentalBillingLogs.IgnoreQueryFilters(), record.EntityId, record.Revision, cancellationToken),
            _ => Task.FromResult(false)
        };
    }

    private static Task<bool> HasActiveEntityNewerThanPurgeAsync<TEntity>(
        IQueryable<TEntity> query,
        Guid entityId,
        long purgeRevision,
        CancellationToken cancellationToken)
        where TEntity : TrackedEntity
        => query.AnyAsync(entity =>
            entity.Id == entityId &&
            !entity.IsDeleted &&
            entity.Revision > purgeRevision,
            cancellationToken);

    private static string NormalizePurgeRecordKind(string? kind)
        => (kind ?? string.Empty).Trim().ToLowerInvariant();

    private async Task<long> GetCurrentRevisionAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.GetCommittedRevisionAsync(cancellationToken);
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
            return DateTime.UtcNow;

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static string NormalizeOptionName(string? value)
        => (value ?? string.Empty).Trim();

    private static void PreserveCustomerTextWhenIncomingLooksLossy(CustomerDto dto, Customer existing)
    {
        var preservedName = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.NameOriginal, dto.NameOriginal);
        if (!string.Equals(preservedName, dto.NameOriginal, StringComparison.Ordinal))
        {
            dto.NameOriginal = preservedName;
            dto.NameMatchKey = MatchKeyNormalizer.Normalize(preservedName);
        }

        dto.TradeType = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.TradeType, dto.TradeType);
        dto.Department = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.Department, dto.Department);
        dto.ContactPerson = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.ContactPerson, dto.ContactPerson);
        dto.Representative = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.Representative, dto.Representative);
        dto.BusinessNumber = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.BusinessNumber, dto.BusinessNumber);
        dto.BusinessType = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.BusinessType, dto.BusinessType);
        dto.BusinessItem = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.BusinessItem, dto.BusinessItem);
        dto.Address = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.Address, dto.Address);
        dto.DetailAddress = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.DetailAddress, dto.DetailAddress);
        dto.Notes = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.Notes, dto.Notes);
        dto.Phone = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.Phone, dto.Phone);
        dto.MobilePhone = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.MobilePhone, dto.MobilePhone);
        dto.FaxNumber = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.FaxNumber, dto.FaxNumber);
        dto.Email = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.Email, dto.Email);
        dto.HomePage = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.HomePage, dto.HomePage);
        dto.Recipient = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.Recipient, dto.Recipient);
        dto.PriceGrade = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.PriceGrade, dto.PriceGrade);
    }
}
