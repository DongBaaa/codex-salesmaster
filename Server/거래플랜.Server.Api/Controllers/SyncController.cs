using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions ConflictJsonOptions = new() { WriteIndented = false };
    private static readonly TimeZoneInfo KoreaTimeZone = ResolveKoreaTimeZone();
    private static readonly SemaphoreSlim RentalAssetSyncLock = new(1, 1);

    private readonly AppDbContext _dbContext;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IInvoiceNumberService _invoiceNumberService;
    private readonly OfficeScopeService _officeScopeService;
    private readonly ICentralFileStorage _fileStorage;
    private readonly RevisionClock _revisionClock;
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
    {
        _dbContext = dbContext;
        _currentUserContext = currentUserContext;
        _invoiceNumberService = invoiceNumberService;
        _officeScopeService = officeScopeService;
        _fileStorage = fileStorage;
        _revisionClock = revisionClock;
        _inventoryLedgerService = inventoryLedgerService;
        _invoiceStockSnapshotService = invoiceStockSnapshotService;
        _rentalAssignmentHistoryService = rentalAssignmentHistoryService;
        _rentalSettlementRecalculationService = rentalSettlementRecalculationService;
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(SyncStatusDto), StatusCodes.Status200OK)]
    public ActionResult<SyncStatusDto> GetStatus(CancellationToken cancellationToken)
    {
        return Ok(new SyncStatusDto
        {
            CurrentServerRevision = _revisionClock.Current,
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
            var currentRevision = _revisionClock.Current;
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
            CurrentServerRevision = _revisionClock.Current,
            ServerUtc = DateTime.UtcNow
        });
    }

    [HttpGet("pull")]
    [ProducesResponseType(typeof(SyncPullResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SyncPullResponse>> Pull([FromQuery] long sinceRev, CancellationToken cancellationToken)
    {
        var readableRentalAssetIds = await _officeScopeService
            .ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);
        var readableRentalAssignmentHistories = _officeScopeService
            .ApplyRentalAssignmentHistoryScope(_dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking());

        var response = new SyncPullResponse
        {
            CompanyProfiles = await _officeScopeService.ApplyCompanyProfileScope(_dbContext.CompanyProfiles.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Units = DeduplicatePulledUnits(await _dbContext.Units.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken)),
            CustomerCategories = await _dbContext.CustomerCategories.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            PriceGradeOptions = await _dbContext.PriceGradeOptions.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            TradeTypeOptions = await _dbContext.TradeTypeOptions.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            ItemCategoryOptions = await _dbContext.ItemCategoryOptions.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            CustomerMasters = await _officeScopeService.ApplyCustomerMasterScope(_dbContext.CustomerMasters.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Customers = await _officeScopeService.ApplySyncCustomerScope(_dbContext.Customers.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            CustomerContracts = await _officeScopeService.ApplyCustomerContractScope(_dbContext.CustomerContracts.IgnoreQueryFilters().AsNoTracking().Include(x => x.Customer))
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto(false)).ToListAsync(cancellationToken),
            Items = await _officeScopeService.ApplySyncItemScope(_dbContext.Items.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            ItemWarehouseStocks = await _officeScopeService.ApplyItemWarehouseStockScope(_dbContext.ItemWarehouseStocks.AsNoTracking())
                .OrderBy(x => x.ItemId).ThenBy(x => x.WarehouseCode)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Transactions = await _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.TransactionDate).ThenBy(x => x.CreatedAtUtc)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            TransactionAttachments = await _officeScopeService.ApplyTransactionAttachmentScope(_dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking().Include(x => x.Transaction))
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.UploadedAtUtc).ThenBy(x => x.SortOrder)
                .Select(x => x.ToDto(true)).ToListAsync(cancellationToken),
            InventoryTransfers = await _officeScopeService.ApplyInventoryTransferScope(_dbContext.InventoryTransfers.IgnoreQueryFilters().AsNoTracking().Include(x => x.Lines))
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.TransferDate).ThenBy(x => x.CreatedAtUtc)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            RentalManagementCompanies = await _officeScopeService.ApplyRentalManagementCompanyScope(_dbContext.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.Code).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            RentalBillingProfiles = await _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.CustomerName).ThenBy(x => x.ProfileKey)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            RentalAssets = await _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.CustomerName).ThenBy(x => x.AssetKey)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            RentalAssetAssignmentHistories = readableRentalAssetIds.Count == 0
                ? []
                : await readableRentalAssignmentHistories
                    .Where(history => history.Revision > sinceRev && readableRentalAssetIds.Contains(history.AssetId))
                    .OrderByDescending(history => history.IsCurrent)
                    .ThenByDescending(history => history.LinkedAtUtc)
                    .Select(history => history.ToDto())
                    .ToListAsync(cancellationToken),
            RentalBillingLogs = await _officeScopeService.ApplyRentalBillingLogScope(_dbContext.RentalBillingLogs.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.ScheduledDate).ThenBy(x => x.BillingYearMonth)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Invoices = await _officeScopeService.ApplySyncInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().Include(x => x.Customer).Include(x => x.Lines).Include(x => x.Payments).AsNoTracking())
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.CreatedAtUtc)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Payments = await _officeScopeService.ApplyPaymentScope(_dbContext.Payments.IgnoreQueryFilters().Include(x => x.Invoice).ThenInclude(invoice => invoice!.Customer).AsNoTracking())
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            PurgeRecords = (await FilterSupersededPurgeRecordsAsync(
                    (await _dbContext.RecycleBinPurgeRecords
                        .AsNoTracking()
                        .Where(x => x.Revision > sinceRev)
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

        response.CurrentServerRevision = await GetCurrentRevisionAsync(cancellationToken);
        return Ok(response);
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
            HasAny(request.ItemWarehouseStocks),
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
        request.ItemWarehouseStocks = RemoveNullEntries(request.ItemWarehouseStocks);
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
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
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
            var acceptedCustomers = await UpsertEntitiesAsync(validCustomers, _dbContext.Customers,
                (e, d) => e.Apply(d), d => new Customer { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            await CascadeDeletedCustomerContractsAsync(acceptedCustomers, cancellationToken);
            if (validCustomers.Count > 0)
                await _dbContext.SaveChangesAsync(cancellationToken);
            var validCustomerContracts = await FilterValidCustomerContractsAsync(request.CustomerContracts ?? [], result, cancellationToken);
            var acceptedCustomerContracts = await UpsertEntitiesAsync(validCustomerContracts, _dbContext.CustomerContracts,
                (e, d) => e.Apply(d), d => new CustomerContract { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            if (validCustomerContracts.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                await PersistCustomerContractsToStorageAsync(acceptedCustomerContracts, savedStoragePaths, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            var scopedItems = await PrepareScopedItemsAsync(request.Items ?? [], result, cancellationToken);
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
                await RemoveWarehouseStocksForDeletedItemsAsync(deletedItemIds, cancellationToken);
                await RemoveSupersededPurgeRecordsAsync("item", acceptedItems, cancellationToken);
            }
            var itemWarehouseStockResult = await UpsertItemWarehouseStocksAsync(request.ItemWarehouseStocks ?? [], result, cancellationToken);
            if (itemWarehouseStockResult.AffectedItemIds.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                await RecalculateItemCurrentStocksFromWarehousesAsync(itemWarehouseStockResult.AffectedItemIds, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            var validInvoices = await FilterValidInvoicesAsync(request.Invoices ?? [], result, cancellationToken);
            var invoiceUpsertResult = await UpsertInvoicesAsync(
                validInvoices,
                result,
                deviceId,
                itemWarehouseStockResult.AcceptedStockKeys,
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
            var existingTransactionRentalSettlementTargets =
                await LoadExistingRentalSettlementTargetsByTransactionIdAsync(validTransactions, cancellationToken);
            var acceptedTransactions = await UpsertEntitiesAsync(validTransactions, _dbContext.Transactions,
                (e, d) => e.Apply(d), d => new TransactionRecord { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            var transactionRentalSettlementTargets =
                BuildRentalSettlementTargetsForAcceptedTransactions(acceptedTransactions, existingTransactionRentalSettlementTargets);
            if (validTransactions.Count > 0)
                await _dbContext.SaveChangesAsync(cancellationToken);
            var validTransactionAttachments = await FilterValidTransactionAttachmentsAsync(request.TransactionAttachments ?? [], result, cancellationToken);
            var acceptedTransactionAttachments = await UpsertEntitiesAsync(validTransactionAttachments, _dbContext.TransactionAttachments,
                (e, d) => e.Apply(d), d => new TransactionAttachment { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            if (validTransactionAttachments.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                await PersistTransactionAttachmentsToStorageAsync(acceptedTransactionAttachments, savedStoragePaths, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            var scopedInventoryTransfers = await PrepareScopedInventoryTransfersAsync(request.InventoryTransfers ?? [], result, cancellationToken);
            var validInventoryTransfers = await FilterValidInventoryTransfersAsync(scopedInventoryTransfers, result, cancellationToken);
            var acceptedInventoryTransferCount = await UpsertInventoryTransfersAsync(validInventoryTransfers, result, deviceId, itemWarehouseStockResult.AcceptedStockKeys, cancellationToken);
            if (acceptedInventoryTransferCount > 0)
                requiresInventoryLedgerRebuild = true;
            var scopedRentalCompanies = await PrepareScopedRentalManagementCompaniesAsync(request.RentalManagementCompanies ?? [], result, cancellationToken);
            await UpsertEntitiesAsync(scopedRentalCompanies, _dbContext.RentalManagementCompanies,
                (e, d) => e.Apply(d), d => new RentalManagementCompany { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
            if (scopedRentalCompanies.Count > 0)
                await _dbContext.SaveChangesAsync(cancellationToken);
            var requestedRentalProfiles = request.RentalBillingProfiles ?? [];
            var incomingRentalProfileIdMap = BuildIncomingRentalBillingProfileIdMap(requestedRentalProfiles);
            var scopedRentalProfiles = await PrepareScopedRentalBillingProfilesAsync(requestedRentalProfiles, result, cancellationToken);
            var validRentalProfiles = await FilterValidRentalBillingProfilesAsync(scopedRentalProfiles, result, cancellationToken);
            var acceptedRentalProfiles = await UpsertRentalBillingProfilesAsync(validRentalProfiles, result, deviceId, cancellationToken);
            if (validRentalProfiles.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                if (acceptedRentalProfiles.Count > 0)
                    requiresRentalAssignmentRefresh = true;
            }

            var resolvedRentalProfileIds = BuildResolvedRentalBillingProfileIdMap(validRentalProfiles, incomingRentalProfileIdMap);
            var requiresRentalAssetLock =
                (request.RentalAssets?.Count ?? 0) > 0 ||
                (request.RentalAssetAssignmentHistories?.Count ?? 0) > 0;
            if (requiresRentalAssetLock)
                await RentalAssetSyncLock.WaitAsync(cancellationToken);

            try
            {
                var scopedRentalAssets = await PrepareScopedRentalAssetsAsync(request.RentalAssets ?? [], result, cancellationToken);
                var validRentalAssets = await FilterValidRentalAssetsAsync(scopedRentalAssets, resolvedRentalProfileIds, result, cancellationToken);
                var acceptedRentalAssets = await UpsertEntitiesAsync(validRentalAssets, _dbContext.RentalAssets,
                    (e, d) => e.Apply(d), d => new RentalAsset { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
                if (validRentalAssets.Count > 0)
                    await _dbContext.SaveChangesAsync(cancellationToken);
                var scopedRentalAssignmentHistories = await PrepareScopedRentalAssetAssignmentHistoriesAsync(request.RentalAssetAssignmentHistories ?? [], result, cancellationToken);
                await UpsertEntitiesAsync(scopedRentalAssignmentHistories, _dbContext.RentalAssetAssignmentHistories,
                    (e, d) => e.Apply(d), d => new RentalAssetAssignmentHistory { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
                var scopedRentalBillingLogs = await PrepareScopedRentalBillingLogsAsync(request.RentalBillingLogs ?? [], result, cancellationToken);
                var validRentalBillingLogs = await FilterValidRentalBillingLogsAsync(scopedRentalBillingLogs, result, cancellationToken);
                await UpsertEntitiesAsync(validRentalBillingLogs, _dbContext.RentalBillingLogs,
                    (e, d) => e.Apply(d), d => new RentalBillingLog { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
                var validPayments = await FilterValidPaymentsAsync(request.Payments ?? [], result, cancellationToken);
                var acceptedPayments = await UpsertEntitiesAsync(validPayments, _dbContext.Payments,
                    (e, d) => e.Apply(d), d => new Payment { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, deviceId, cancellationToken);
                var paymentRentalSettlementTargets =
                    await LoadRentalSettlementTargetsForPaymentsAsync(acceptedPayments, cancellationToken);
                await SoftDeletePaymentAttachmentsForDeletedPaymentsAsync(acceptedPayments, cancellationToken);
                var paymentLinkedTransactionSyncTargets =
                    await SynchronizeAcceptedPaymentsToLinkedTransactionsAsync(acceptedPayments, cancellationToken);
                var paymentLinkedTransactionSettlementTargets =
                    await CascadeDeletedPaymentsToLinkedTransactionsAsync(acceptedPayments, cancellationToken);

                await _dbContext.SaveChangesAsync(cancellationToken);
                var rentalSettlementTargets = invoiceRentalSettlementTargets
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
            await PopulateAcceptedRevisionsAsync(result, validInvoices, _dbContext.Invoices, nameof(Invoice), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, validTransactions, _dbContext.Transactions, nameof(TransactionRecord), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, validTransactionAttachments, _dbContext.TransactionAttachments, nameof(TransactionAttachment), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, validInventoryTransfers, _dbContext.InventoryTransfers, nameof(InventoryTransfer), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, scopedRentalCompanies, _dbContext.RentalManagementCompanies, nameof(RentalManagementCompany), cancellationToken);
            await PopulateAcceptedRevisionsAsync(result, validRentalProfiles, _dbContext.RentalBillingProfiles, nameof(RentalBillingProfile), cancellationToken);
            await DeduplicateOpenConflictLogsForResultAsync(result.Conflicts, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            finally
            {
                DeleteSavedStoragePaths(savedStoragePaths);
            }

            throw;
        }

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
        var conflictIds = result.Conflicts
            .Where(conflict => string.Equals(conflict.EntityName, entityName, StringComparison.OrdinalIgnoreCase))
            .Select(conflict => conflict.EntityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ids = payload
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
                UpdatedAtUtc = entity.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        result.AcceptedRevisions.AddRange(rows);
    }

    private async Task<List<TDto>> UpsertEntitiesAsync<TEntity, TDto>(
        IEnumerable<TDto> payload, DbSet<TEntity> dbSet,
        Action<TEntity, TDto> apply, Func<TDto, TEntity> create,
        SyncPushResult result, string deviceId, CancellationToken cancellationToken)
        where TEntity : TrackedEntity
        where TDto : SyncEntityDto
    {
        var entityName = typeof(TEntity).Name;
        var accepted = new List<TDto>();
        foreach (var dto in payload)
        {
            if (await TryAcceptDuplicateMutationAsync(dto, entityName, deviceId, result, cancellationToken))
            {
                continue;
            }

            var entity = await dbSet.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (entity is null)
            {
                var newEntity = create(dto);
                apply(newEntity, dto);
                dbSet.Add(newEntity);
                RegisterProcessedMutation(dto, entityName, deviceId);
                await ResolveHistoricalConflictsAsync(entityName, newEntity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
                accepted.Add(dto);
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
            await ResolveHistoricalConflictsAsync(entityName, entity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
            accepted.Add(dto);
            result.AcceptedCount++;
        }

        return accepted;
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

        foreach (var dto in payload)
        {
            if (await TryAcceptDuplicateMutationAsync(dto, nameof(CustomerCategory), deviceId, result, cancellationToken))
                continue;

            var normalizedName = DefaultCustomerCategories.NormalizeName(dto.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                AddClientConflict(dto, nameof(CustomerCategory), "Customer category name is required.", result);
                continue;
            }

            var entity = existingCategories.FirstOrDefault(current => current.Id == dto.Id);
            var activeDuplicate = dto.IsDeleted
                ? null
                : existingCategories
                    .Where(current => current.Id != dto.Id && !current.IsDeleted)
                    .FirstOrDefault(current =>
                        string.Equals(
                            DefaultCustomerCategories.NormalizeName(current.Name),
                            normalizedName,
                            StringComparison.CurrentCultureIgnoreCase));

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
                await ResolveHistoricalConflictsAsync(nameof(CustomerCategory), newEntity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
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
            await ResolveHistoricalConflictsAsync(nameof(CustomerCategory), entity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
            result.AcceptedCount++;
        }
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

        foreach (var dto in dedupedPayload)
        {
            if (await TryAcceptDuplicateMutationAsync(dto, nameof(Unit), deviceId, result, cancellationToken))
                continue;

            var normalizedName = UnitCatalogNormalizer.Normalize(dto.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                AddClientConflict(dto, nameof(Unit), "Unit name is required.", result);
                continue;
            }

            var entity = existingUnits.FirstOrDefault(current => current.Id == dto.Id)
                ?? existingUnits
                    .Where(current =>
                        string.Equals(
                            UnitCatalogNormalizer.Normalize(current.Name),
                            normalizedName,
                            StringComparison.Ordinal))
                    .OrderByDescending(current => current.UpdatedAtUtc)
                    .ThenByDescending(current => current.Revision)
                    .FirstOrDefault();

            if (entity is null)
            {
                var newEntity = new Unit { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
                newEntity.Apply(dto);
                newEntity.Name = normalizedName;
                _dbContext.Units.Add(newEntity);
                existingUnits.Add(newEntity);
                RegisterProcessedMutation(dto, nameof(Unit), deviceId);
                await ResolveHistoricalConflictsAsync(nameof(Unit), newEntity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
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
            await ResolveHistoricalConflictsAsync(nameof(Unit), entity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
            result.AcceptedCount++;
        }
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

        foreach (var dto in payload)
        {
            if (await TryAcceptDuplicateMutationAsync(dto, entityName, deviceId, result, cancellationToken))
                continue;

            var normalizedName = NormalizeOptionName(dtoNameSelector(dto));
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                AddClientConflict(dto, entityName, "Option name is required.", result);
                continue;
            }

            var entity = existingEntities.FirstOrDefault(current => current.Id == dto.Id);
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
                await ResolveHistoricalConflictsAsync(entityName, newEntity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
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
            await ResolveHistoricalConflictsAsync(entityName, entity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
            result.AcceptedCount++;
        }
    }

    private async Task<(List<(Guid ProfileId, Guid? RunId)> RentalSettlementTargets, int AcceptedCount)> UpsertInvoicesAsync(
        IEnumerable<InvoiceDto> payload,
        SyncPushResult result,
        string deviceId,
        IReadOnlySet<string> itemWarehouseStockKeysHandledByClient,
        CancellationToken cancellationToken)
    {
        var rentalSettlementTargets = new List<(Guid ProfileId, Guid? RunId)>();
        var acceptedDeletedInvoiceIds = new List<Guid>();
        var acceptedCount = 0;
        foreach (var dto in payload)
        {
            if (await TryAcceptDuplicateMutationAsync(dto, nameof(Invoice), deviceId, result, cancellationToken))
                continue;

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
                if (dto.VersionGroupId == Guid.Empty)
                    dto.VersionGroupId = invoiceId;

                entity = new Invoice { Id = invoiceId };
                entity.Apply(dto);
                if (string.IsNullOrWhiteSpace(entity.InvoiceNumber))
                {
                    entity.InvoiceNumber = await _invoiceNumberService.GenerateAsync(entity.CustomerId, entity.InvoiceDate, cancellationToken);
                    result.AssignedInvoiceNumbers[dto.Id] = entity.InvoiceNumber;
                }

                ApplyInvoiceLines(entity, dto.Lines ?? []);
                var createdStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);
                await ApplyStockSnapshotDeltaAsync(
                    new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
                    createdStockDeltas,
                    itemWarehouseStockKeysHandledByClient,
                    cancellationToken);
                _dbContext.Invoices.Add(entity);
                AddRentalSettlementTarget(rentalSettlementTargets, entity.LinkedRentalBillingProfileId, entity.LinkedRentalBillingRunId);
                if (entity.IsDeleted)
                    acceptedDeletedInvoiceIds.Add(entity.Id);
                RegisterProcessedMutation(dto, nameof(Invoice), deviceId);
                await ResolveHistoricalConflictsAsync(nameof(Invoice), entity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
                acceptedCount++;
                result.AcceptedCount++;
                continue;
            }

            if (dto.VersionGroupId == Guid.Empty)
                dto.VersionGroupId = entity.VersionGroupId == Guid.Empty ? entity.Id : entity.VersionGroupId;

            if (!_officeScopeService.CanWriteOfficeForInvoices(entity.ResponsibleOfficeCode, entity.TenantCode, entity.OfficeCode))
            {
                AddClientConflict(dto, nameof(Invoice), "Current account cannot modify this office scope.", result);
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

            var previousStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);
            AddRentalSettlementTarget(rentalSettlementTargets, entity.LinkedRentalBillingProfileId, entity.LinkedRentalBillingRunId);
            entity.Apply(dto);
            if (string.IsNullOrWhiteSpace(entity.InvoiceNumber))
            {
                entity.InvoiceNumber = await _invoiceNumberService.GenerateAsync(entity.CustomerId, entity.InvoiceDate, cancellationToken);
                result.AssignedInvoiceNumbers[dto.Id] = entity.InvoiceNumber;
            }
            AddRentalSettlementTarget(rentalSettlementTargets, entity.LinkedRentalBillingProfileId, entity.LinkedRentalBillingRunId);
            if (entity.IsDeleted)
                acceptedDeletedInvoiceIds.Add(entity.Id);

            _dbContext.InvoiceLines.RemoveRange(entity.Lines);
            entity.Lines.Clear();
            ApplyInvoiceLines(entity, dto.Lines ?? []);
            var updatedInvoiceStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);
            await ApplyStockSnapshotDeltaAsync(
                previousStockDeltas,
                updatedInvoiceStockDeltas,
                itemWarehouseStockKeysHandledByClient,
                cancellationToken);
            RegisterProcessedMutation(dto, nameof(Invoice), deviceId);
            await ResolveHistoricalConflictsAsync(nameof(Invoice), entity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
            acceptedCount++;
            result.AcceptedCount++;
        }

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

        return (rentalSettlementTargets.Distinct().ToList(), acceptedCount);
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
            .Where(transaction => !transaction.IsDeleted && paymentIds.Contains(transaction.Id))
            .ToListAsync(cancellationToken);
        if (transactions.Count == 0)
            return [];

        var invoiceIds = payments
            .Select(payment => payment.InvoiceId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var invoicesById = await _dbContext.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoiceIds.Contains(invoice.Id) && !invoice.IsDeleted)
            .ToDictionaryAsync(invoice => invoice.Id, cancellationToken);

        var targets = new List<(Guid ProfileId, Guid? RunId)>();
        foreach (var transaction in transactions)
        {
            if (!paymentById.TryGetValue(transaction.Id, out var payment) ||
                !invoicesById.TryGetValue(payment.InvoiceId, out var invoice))
            {
                continue;
            }

            AddRentalSettlementTarget(targets, transaction.LinkedRentalBillingProfileId, transaction.LinkedRentalBillingRunId);
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
        transaction.TransactionKind = ResolveLinkedTransactionKind(invoice);
        ApplyLinkedTransactionTotals(transaction, payment.Amount, IsPaymentVoucher(invoice.VoucherType));
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
            .Where(transaction => !transaction.IsDeleted && paymentIds.Contains(transaction.Id))
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

            transaction.IsDeleted = true;
            AddRentalSettlementTarget(targets, transaction.LinkedRentalBillingProfileId, transaction.LinkedRentalBillingRunId);

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
        var scoped = new List<CustomerDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.Customers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
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
        var valid = new List<CustomerDto>();

        foreach (var dto in payload)
        {
            NormalizeCustomerClassification(dto);

            if (dto.CategoryId.HasValue &&
                !await ExistsOrTrackedAsync(_dbContext.CustomerCategories, dto.CategoryId.Value, cancellationToken))
            {
                dto.CategoryId = null;
            }

            if (dto.CustomerMasterId.HasValue)
            {
                var customerMaster = await _dbContext.CustomerMasters.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.CustomerMasterId.Value, cancellationToken);
                if (customerMaster is null || customerMaster.IsDeleted)
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
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new Dictionary<Guid, ItemDto>();
        var incomingCanonicalIdsByNaturalKey = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var dto in payload)
        {
            var existing = await _dbContext.Items.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);

            var resolvedOfficeCode = _officeScopeService.ResolveScopeForCreate(
                dto.OfficeCode,
                existing?.OfficeCode);
            var resolvedTenantCode = _officeScopeService.ResolveTenantForCreate(
                dto.TenantCode,
                resolvedOfficeCode,
                existing?.TenantCode,
                existing?.OfficeCode);

            if (existing is null)
            {
                var naturalKey = BuildScopedItemNaturalKey(
                    dto,
                    resolvedOfficeCode,
                    resolvedTenantCode);

                if (!string.IsNullOrWhiteSpace(naturalKey) &&
                    incomingCanonicalIdsByNaturalKey.TryGetValue(naturalKey, out var batchCanonicalId))
                {
                    if (dto.Id != batchCanonicalId)
                    {
                        AddNotice(
                            result,
                            nameof(Item),
                            batchCanonicalId,
                            "item-natural-key-batch-merged",
                            $"품목 '{dto.NameOriginal}'은(는) 동일한 시리얼/관리번호/품목키가 같은 요청 안에 이미 있어 기존 저장 대상으로 합쳐졌습니다.");
                    }

                    dto.Id = batchCanonicalId;
                }
                else
                {
                    var requestedItemId = dto.Id;
                    existing = await FindExistingItemByNaturalKeyAsync(
                        dto,
                        resolvedOfficeCode,
                        resolvedTenantCode,
                        cancellationToken);

                    if (existing is not null)
                    {
                        dto.Id = existing.Id;
                        if (requestedItemId != existing.Id)
                        {
                            AddNotice(
                                result,
                                nameof(Item),
                                existing.Id,
                                "item-natural-key-merged",
                                $"품목 '{dto.NameOriginal}'은(는) 기존 서버 품목과 동일한 시리얼/관리번호/품목키로 판단되어 해당 품목에 병합되었습니다.");
                        }
                    }
                    else if (dto.Id == Guid.Empty)
                    {
                        dto.Id = Guid.NewGuid();
                    }

                    if (!string.IsNullOrWhiteSpace(naturalKey) && dto.Id != Guid.Empty)
                        incomingCanonicalIdsByNaturalKey[naturalKey] = dto.Id;
                }
            }

            if (existing is not null && !_officeScopeService.CanWriteOfficeForItems(existing.OfficeCode, existing.TenantCode))
            {
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

            if (scoped.TryGetValue(dto.Id, out var existingScoped))
            {
                if (ShouldReplacePreparedItem(existingScoped, dto))
                    scoped[dto.Id] = dto;

                continue;
            }

            scoped[dto.Id] = dto;
        }

        return scoped.Values.ToList();
    }

    private async Task RemoveWarehouseStocksForDeletedItemsAsync(
        IReadOnlyCollection<Guid> requestedItemIds,
        CancellationToken cancellationToken)
    {
        if (requestedItemIds.Count == 0)
            return;

        var deletedItemIds = await _dbContext.Items
            .IgnoreQueryFilters()
            .Where(item => requestedItemIds.Contains(item.Id) && item.IsDeleted)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (deletedItemIds.Count == 0)
            return;

        var staleRows = await _dbContext.ItemWarehouseStocks
            .IgnoreQueryFilters()
            .Where(stock => deletedItemIds.Contains(stock.ItemId))
            .ToListAsync(cancellationToken);
        if (staleRows.Count == 0)
            return;

        _dbContext.ItemWarehouseStocks.RemoveRange(staleRows);
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

    private async Task<Item?> FindExistingItemByNaturalKeyAsync(
        ItemDto dto,
        string resolvedOfficeCode,
        string resolvedTenantCode,
        CancellationToken cancellationToken)
    {
        var normalizedNameMatchKey = string.IsNullOrWhiteSpace(dto.NameMatchKey)
            ? RentalCatalogValueNormalizer.NormalizeLooseKey(dto.NameOriginal)
            : RentalCatalogValueNormalizer.NormalizeLooseKey(dto.NameMatchKey);
        var normalizedSpecificationMatchKey = string.IsNullOrWhiteSpace(dto.SpecificationMatchKey)
            ? RentalCatalogValueNormalizer.NormalizeLooseKey(dto.SpecificationOriginal)
            : RentalCatalogValueNormalizer.NormalizeLooseKey(dto.SpecificationMatchKey);
        var normalizedItemKind = ItemOperationalPolicy.NormalizeItemKind(dto.ItemKind, dto.TrackingType, dto.CategoryName, dto.IsRental);
        var normalizedTrackingType = ItemOperationalPolicy.NormalizeTrackingType(dto.TrackingType, dto.ItemKind, dto.CategoryName, dto.IsRental);
        var materialKey = NormalizeItemIdentityValue(dto.MaterialNumber);
        if (HasMeaningfulItemIdentityValue(materialKey))
        {
            var byMaterial = await _dbContext.Items.IgnoreQueryFilters()
                .Where(item => item.OfficeCode == resolvedOfficeCode && item.TenantCode == resolvedTenantCode)
                .Where(item => item.MaterialNumber == dto.MaterialNumber)
                .ToListAsync(cancellationToken);

            var exactMaterialMatch = byMaterial.FirstOrDefault(item =>
                string.Equals(NormalizeItemIdentityValue(item.MaterialNumber), materialKey, StringComparison.OrdinalIgnoreCase));
            if (exactMaterialMatch is not null)
                return exactMaterialMatch;
        }

        var serialKey = NormalizeItemIdentityValue(dto.SerialNumber);
        if (HasMeaningfulItemIdentityValue(serialKey))
        {
            var bySerial = await _dbContext.Items.IgnoreQueryFilters()
                .Where(item => item.OfficeCode == resolvedOfficeCode && item.TenantCode == resolvedTenantCode)
                .Where(item => item.SerialNumber == dto.SerialNumber)
                .ToListAsync(cancellationToken);

            var exactSerialMatch = bySerial.FirstOrDefault(item =>
                string.Equals(NormalizeItemIdentityValue(item.SerialNumber), serialKey, StringComparison.OrdinalIgnoreCase));
            if (exactSerialMatch is not null)
                return exactSerialMatch;
        }

        var descriptorKey = BuildItemDescriptorKey(dto);
        if (string.IsNullOrWhiteSpace(descriptorKey))
            return null;

        var descriptorCandidates = await _dbContext.Items.IgnoreQueryFilters()
            .Where(item => item.OfficeCode == resolvedOfficeCode && item.TenantCode == resolvedTenantCode)
            .Where(item =>
                item.NameMatchKey == normalizedNameMatchKey &&
                item.SpecificationMatchKey == normalizedSpecificationMatchKey &&
                item.ItemKind == normalizedItemKind &&
                item.TrackingType == normalizedTrackingType)
            .ToListAsync(cancellationToken);

        var matchingDescriptorCandidates = descriptorCandidates
            .Where(item => string.Equals(BuildItemDescriptorKey(item), descriptorKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingDescriptorCandidates.Count != 1)
            return null;

        var candidate = matchingDescriptorCandidates[0];
        var candidateMaterialKey = NormalizeItemIdentityValue(candidate.MaterialNumber);
        var candidateSerialKey = NormalizeItemIdentityValue(candidate.SerialNumber);

        if (HasMeaningfulItemIdentityValue(materialKey) || HasMeaningfulItemIdentityValue(serialKey))
        {
            if (HasMeaningfulItemIdentityValue(candidateMaterialKey) || HasMeaningfulItemIdentityValue(candidateSerialKey))
                return null;
        }

        return candidate;
    }

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

            var customer = await _dbContext.Customers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
            if (dto.CustomerId == Guid.Empty || customer is null || customer.IsDeleted)
            {
                if (invoice?.Customer is not null && !invoice.Customer.IsDeleted)
                {
                    customer = invoice.Customer;
                    dto.CustomerId = customer.Id;
                    if (originalCustomerId != customer.Id)
                    {
                        AddNotice(
                            result,
                            nameof(TransactionRecord),
                            dto.Id,
                            "transaction-customer-relinked",
                            $"수금/지급 '{dto.Id:D}'의 거래처를 연결 전표 기준으로 다시 맞췄습니다.");
                    }
                }
                else if (profile?.CustomerId.HasValue == true && profile.CustomerId.Value != Guid.Empty)
                {
                    customer = await _dbContext.Customers.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(x => x.Id == profile.CustomerId.Value, cancellationToken);
                    if (customer is not null && !customer.IsDeleted)
                    {
                        dto.CustomerId = customer.Id;
                        if (originalCustomerId != customer.Id)
                        {
                            AddNotice(
                                result,
                                nameof(TransactionRecord),
                                dto.Id,
                                "transaction-customer-relinked",
                                $"수금/지급 '{dto.Id:D}'의 거래처를 연결 렌탈 청구 기준으로 다시 맞췄습니다.");
                        }
                    }
                }

                if (customer is null || customer.IsDeleted)
                {
                    if (dto.IsDeleted && existing is null)
                        continue;

                    AddClientConflict(dto, nameof(TransactionRecord),
                        $"Referenced customer was not found: {dto.CustomerId}.", result);
                    continue;
                }
            }

            if (invoice?.Customer is not null &&
                !invoice.Customer.IsDeleted &&
                dto.CustomerId != invoice.Customer.Id)
            {
                customer = invoice.Customer;
                dto.CustomerId = customer.Id;
                if (originalCustomerId != customer.Id)
                {
                    AddNotice(
                        result,
                        nameof(TransactionRecord),
                        dto.Id,
                        "transaction-customer-relinked",
                        $"수금/지급 '{dto.Id:D}'의 거래처를 연결 전표 기준으로 다시 맞췄습니다.");
                }
            }

            var resolvedResponsibleOfficeCode = _officeScopeService.ResolvePaymentResponsibleScopeForCreate(
                dto.ResponsibleOfficeCode,
                customer.ResponsibleOfficeCode);
            var resolvedOfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
                dto.OfficeCode,
                resolvedResponsibleOfficeCode,
                customer.OfficeCode);
            var resolvedTenantCode = _officeScopeService.ResolveTenantForCreate(
                dto.TenantCode,
                resolvedOfficeCode,
                customer.TenantCode,
                customer.OfficeCode);

            var canWriteReferencedCustomer = _officeScopeService.CanWriteOfficeForCustomers(
                customer.ResponsibleOfficeCode,
                customer.TenantCode,
                customer.OfficeCode);
            var referencedCustomerTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                customer.TenantCode,
                customer.ResponsibleOfficeCode,
                customer.TenantCode,
                customer.OfficeCode);
            var referencedCustomerResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                customer.ResponsibleOfficeCode,
                customer.OfficeCode);
            var normalizedResolvedResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                resolvedResponsibleOfficeCode,
                resolvedOfficeCode);
            var canUseCustomerForWritablePaymentScope =
                string.Equals(referencedCustomerTenantCode, _officeScopeService.CurrentTenantCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(referencedCustomerResponsibleOfficeCode, normalizedResolvedResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase) &&
                _officeScopeService.CanWriteOfficeForPayments(resolvedResponsibleOfficeCode, resolvedTenantCode, resolvedOfficeCode);
            if (!canWriteReferencedCustomer && !canUseCustomerForWritablePaymentScope)
            {
                if (dto.IsDeleted &&
                    existing is not null &&
                    _officeScopeService.CanWriteOfficeForPayments(existing.ResponsibleOfficeCode, existing.TenantCode, existing.OfficeCode))
                {
                    valid.Add(dto);
                    continue;
                }

                AddClientConflict(dto, nameof(TransactionRecord),
                    $"Referenced customer is outside the writable office scope: {dto.CustomerId}.", result);
                continue;
            }

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
            var canWriteExisting = existing is null
                || _officeScopeService.CanWriteInventoryTransferRoute(
                    existing.SourceOfficeCode,
                    existing.TargetOfficeCode,
                    existing.TenantCode);
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

            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<InventoryTransferDto>> FilterValidInventoryTransfersAsync(
        IEnumerable<InventoryTransferDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<InventoryTransferDto>();

        foreach (var dto in payload)
        {
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

            if (!CanAcceptInventoryTransferScopeMutation(dto, existing, normalizedStatus, out var scopeConflictReason))
            {
                AddClientConflict(dto, nameof(InventoryTransfer), scopeConflictReason, result);
                continue;
            }

            var lines = dto.Lines ?? [];
            var lineConflict = false;
            foreach (var line in lines.Where(line => line.ItemId.HasValue && line.ItemId.Value != Guid.Empty))
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

            if (!canWriteSource &&
                !IsTargetOnlyInventoryTransferStatusMutation(existing, dto, normalizedStatus))
            {
                reason = "Inventory transfer target-only status updates cannot change source route or requested lines.";
                return false;
            }

            return true;
        }

        if (canWriteSource)
            return true;

        reason = $"Inventory transfer source office is outside the writable delivery scope: {candidateSourceOffice}.";
        return false;
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
            !string.Equals(NormalizeInventoryTransferGuardText(dto.RequestedByUsername), NormalizeInventoryTransferGuardText(existing.RequestedByUsername), StringComparison.Ordinal) ||
            NormalizeInventoryTransferGuardUtc(dto.RequestedAtUtc) != NormalizeInventoryTransferGuardUtc(existing.RequestedAtUtc))
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

        if (string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Received, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(NormalizeInventoryTransferGuardText(existing.ReceivedByUsername), NormalizeInventoryTransferGuardText(dto.ReceivedByUsername), StringComparison.Ordinal) ||
                NormalizeInventoryTransferGuardUtc(existing.ReceivedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.ReceivedAtUtc) ||
                !string.Equals(NormalizeInventoryTransferGuardText(existing.ReceiveMemo), NormalizeInventoryTransferGuardText(dto.ReceiveMemo), StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(NormalizeInventoryTransferGuardText(existing.RejectedByUsername), NormalizeInventoryTransferGuardText(dto.RejectedByUsername), StringComparison.Ordinal) ||
                NormalizeInventoryTransferGuardUtc(existing.RejectedAtUtc) != NormalizeInventoryTransferGuardUtc(dto.RejectedAtUtc) ||
                !string.Equals(NormalizeInventoryTransferGuardText(existing.RejectReason), NormalizeInventoryTransferGuardText(dto.RejectReason), StringComparison.Ordinal))
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
                existingLine.ReceivedQuantity != incomingLine.ReceivedQuantity ||
                existingLine.QuantityDifference != incomingLine.QuantityDifference ||
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
        CancellationToken cancellationToken)
    {
        var acceptedCount = 0;
        foreach (var dto in payload)
        {
            if (await TryAcceptDuplicateMutationAsync(dto, nameof(InventoryTransfer), deviceId, result, cancellationToken))
                continue;

            var entity = await _dbContext.InventoryTransfers.IgnoreQueryFilters()
                .Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);

            if (entity is null)
            {
                entity = new InventoryTransfer { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
                entity.Apply(dto);
                ApplyInventoryTransferLines(entity, dto.Lines ?? []);
                var currentStockDeltas = await _invoiceStockSnapshotService.BuildInventoryTransferStockDeltasAsync(entity, cancellationToken);
                var stockShortages = await _invoiceStockSnapshotService.FindStockShortagesAsync(
                    new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
                    currentStockDeltas,
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
                _dbContext.InventoryTransfers.Add(entity);
                RegisterProcessedMutation(dto, nameof(InventoryTransfer), deviceId);
                await ResolveHistoricalConflictsAsync(nameof(InventoryTransfer), entity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
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

            var candidate = new InventoryTransfer { Id = entity.Id };
            candidate.Apply(dto);
            ApplyInventoryTransferLines(candidate, dto.Lines ?? []);
            var updatedStockDeltas = await _invoiceStockSnapshotService.BuildInventoryTransferStockDeltasAsync(candidate, cancellationToken);
            var updateStockShortages = await _invoiceStockSnapshotService.FindStockShortagesAsync(
                previousStockDeltas,
                updatedStockDeltas,
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
            entity.Apply(dto);
            _dbContext.InventoryTransferLines.RemoveRange(entity.Lines);
            entity.Lines.Clear();
            ApplyInventoryTransferLines(entity, dto.Lines ?? []);
            RegisterProcessedMutation(dto, nameof(InventoryTransfer), deviceId);
            await ResolveHistoricalConflictsAsync(nameof(InventoryTransfer), entity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
            acceptedCount++;
            result.AcceptedCount++;
        }

        return acceptedCount;
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
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<RentalManagementCompanyDto>();
        var reservedCompanyIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var dto in payload)
        {
            dto.Code = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.Code, dto.Code);
            dto.TenantCode = _officeScopeService.ResolveTenantForRentalCreate(dto.TenantCode, dto.Code);

            var naturalKey = $"{dto.TenantCode}|{dto.Code}";
            if (reservedCompanyIds.TryGetValue(naturalKey, out var reservedId))
                dto.Id = reservedId;

            var existing = await _dbContext.RentalManagementCompanies.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is null)
            {
                existing = await FindExistingRentalManagementCompanyByNaturalKeyAsync(dto, cancellationToken);
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
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<RentalManagementCompany?> FindExistingRentalManagementCompanyByNaturalKeyAsync(
        RentalManagementCompanyDto dto,
        CancellationToken cancellationToken)
    {
        var normalizedCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.Code, dto.Code);
        var normalizedTenantCode = _officeScopeService.ResolveTenantForRentalCreate(dto.TenantCode, normalizedCode);

        return await _dbContext.RentalManagementCompanies
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(entity =>
                entity.Code == normalizedCode &&
                entity.TenantCode == normalizedTenantCode,
                cancellationToken);
    }

    private async Task<List<RentalBillingProfileDto>> PrepareScopedRentalBillingProfilesAsync(
        IEnumerable<RentalBillingProfileDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<RentalBillingProfileDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is null)
            {
                existing = await FindExistingRentalBillingProfileByNaturalKeyAsync(dto, cancellationToken);
                if (existing is not null)
                    dto.Id = existing.Id;
                else if (dto.Id == Guid.Empty)
                {
                    var deterministicProfileId = SyncIdentityGenerator.CreateRentalBillingProfileId(dto.ProfileKey);
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
            dto.TenantCode = TenantScopeCatalog.TryNormalizeTenantCode(dto.TenantCode, out var requestedTenantCode) &&
                             TenantScopeCatalog.TenantContainsOffice(requestedTenantCode, dto.OfficeCode)
                ? requestedTenantCode
                : resolvedTenantCode;
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<RentalBillingProfile?> FindExistingRentalBillingProfileByNaturalKeyAsync(
        RentalBillingProfileDto dto,
        CancellationToken cancellationToken)
    {
        var profileKey = (dto.ProfileKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(profileKey))
            return null;

        var exact = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(profile => profile.ProfileKey == profileKey, cancellationToken);
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

        var linkedCustomer = await GetRentalReferenceCustomerAsync(dto.CustomerId, cancellationToken);
        if (IsDistinctBillingCustomerAlias(dto.CustomerName, linkedCustomer?.NameOriginal))
            return null;

        return await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(profile => profile.ProfileKey == legacyProfileKey, cancellationToken);
    }

    private static Dictionary<string, List<Guid>> BuildIncomingRentalBillingProfileIdMap(
        IEnumerable<RentalBillingProfileDto> payload)
    {
        var result = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in payload)
        {
            if (dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.ProfileKey))
                continue;

            var profileKey = dto.ProfileKey.Trim();
            if (!result.TryGetValue(profileKey, out var ids))
            {
                ids = new List<Guid>();
                result[profileKey] = ids;
            }

            if (!ids.Contains(dto.Id))
                ids.Add(dto.Id);
        }

        return result;
    }

    private static Dictionary<Guid, Guid> BuildResolvedRentalBillingProfileIdMap(
        IEnumerable<RentalBillingProfileDto> payload,
        IReadOnlyDictionary<string, List<Guid>> incomingProfileIdsByKey)
    {
        var result = new Dictionary<Guid, Guid>();
        foreach (var dto in payload)
        {
            if (dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.ProfileKey))
                continue;

            if (!incomingProfileIdsByKey.TryGetValue(dto.ProfileKey.Trim(), out var originalIds))
                continue;

            foreach (var originalId in originalIds)
            {
                if (originalId != Guid.Empty)
                    result[originalId] = dto.Id;
            }
        }

        return result;
    }

    private async Task<List<RentalBillingProfileDto>> UpsertRentalBillingProfilesAsync(
        IEnumerable<RentalBillingProfileDto> payload,
        SyncPushResult result,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var accepted = new List<RentalBillingProfileDto>();
        foreach (var dto in payload)
        {
            if (await TryAcceptDuplicateMutationAsync(dto, nameof(RentalBillingProfile), deviceId, result, cancellationToken))
                continue;

            var entity = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (entity is null)
            {
                entity = await FindExistingRentalBillingProfileByNaturalKeyAsync(dto, cancellationToken);
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
                newEntity.Apply(dto);
                _dbContext.RentalBillingProfiles.Add(newEntity);
                RegisterProcessedMutation(dto, nameof(RentalBillingProfile), deviceId);
                await ResolveHistoricalConflictsAsync(nameof(RentalBillingProfile), newEntity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
                accepted.Add(dto);
                result.AcceptedCount++;
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

            dto.Id = entity.Id;
            entity.Apply(dto);
            RegisterProcessedMutation(dto, nameof(RentalBillingProfile), deviceId);
            await ResolveHistoricalConflictsAsync(nameof(RentalBillingProfile), entity.Id, "후속 동기화가 정상 반영되어 기존 충돌을 자동 해결했습니다.", cancellationToken);
            accepted.Add(dto);
            result.AcceptedCount++;
        }

        return accepted;
    }

    private async Task<List<RentalBillingProfileDto>> FilterValidRentalBillingProfilesAsync(
        IEnumerable<RentalBillingProfileDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<RentalBillingProfileDto>();

        foreach (var dto in payload)
        {
            var requestedCustomerId = dto.CustomerId;
            dto.CustomerId = await ResolveRentalBillingProfileCustomerReferenceAsync(dto, cancellationToken);
            if (!await ValidateExplicitRentalCustomerReferenceAsync(
                    dto,
                    nameof(RentalBillingProfile),
                    requestedCustomerId,
                    dto.CustomerId,
                    result,
                    cancellationToken))
            {
                continue;
            }

            var linkedCustomer = await GetRentalReferenceCustomerAsync(dto.CustomerId, cancellationToken);
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
                dto.CustomerName = string.IsNullOrWhiteSpace(dto.CustomerName)
                    ? normalizedCustomerName
                    : RentalCatalogValueNormalizer.NormalizeDisplayText(dto.CustomerName);
            }

            if (!string.IsNullOrWhiteSpace(dto.ManagementCompanyCode))
            {
                var managementCompanyCode = dto.ManagementCompanyCode.Trim();
                var exists = await _dbContext.RentalManagementCompanies.IgnoreQueryFilters()
                    .AnyAsync(x => x.TenantCode == dto.TenantCode && x.Code == managementCompanyCode && !x.IsDeleted, cancellationToken);
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

    private async Task<bool> TryAcceptDuplicateMutationAsync(
        SyncEntityDto dto,
        string entityName,
        string deviceId,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var mutationId = NormalizeMutationId(dto.MutationId);
        if (string.IsNullOrWhiteSpace(mutationId))
            return false;

        var alreadyProcessed = _dbContext.ProcessedSyncMutations.Local.Any(entity =>
                                   string.Equals(entity.MutationId, mutationId, StringComparison.OrdinalIgnoreCase)) ||
                               await _dbContext.ProcessedSyncMutations
                                   .AsNoTracking()
                                   .AnyAsync(entity => entity.MutationId == mutationId, cancellationToken);
        if (!alreadyProcessed)
            return false;

        if (dto.Id != Guid.Empty)
        {
            await ResolveHistoricalConflictsAsync(
                entityName,
                dto.Id,
                "이미 처리된 동일 mutation 이 확인되어 기존 충돌을 자동 해결했습니다.",
                cancellationToken);
        }

        result.AcceptedCount++;
        result.DuplicateMutationCount++;
        return true;
    }

    private void RegisterProcessedMutation(SyncEntityDto dto, string entityName, string deviceId)
    {
        var mutationId = NormalizeMutationId(dto.MutationId);
        if (string.IsNullOrWhiteSpace(mutationId))
            return;

        if (_dbContext.ProcessedSyncMutations.Local.Any(entity =>
                string.Equals(entity.MutationId, mutationId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _dbContext.ProcessedSyncMutations.Add(new ProcessedSyncMutation
        {
            MutationId = mutationId,
            DeviceId = deviceId,
            EntityName = entityName,
            EntityId = dto.Id.ToString("D"),
            ExpectedRevision = dto.ExpectedRevision,
            ProcessedAtUtc = dto.MutationCreatedAtUtc.HasValue && dto.MutationCreatedAtUtc.Value != default
                ? NormalizeUtc(dto.MutationCreatedAtUtc.Value)
                : DateTime.UtcNow
        });
    }

    private async Task ResolveHistoricalConflictsAsync(
        string entityName,
        Guid entityId,
        string resolutionNote,
        CancellationToken cancellationToken)
    {
        if (entityId == Guid.Empty)
            return;

        var now = DateTime.UtcNow;
        var normalizedNote = (resolutionNote ?? string.Empty).Trim();
        var entityIdText = entityId.ToString();

        await _dbContext.ConflictLogs
            .Where(conflict =>
                conflict.EntityName == entityName &&
                conflict.EntityId == entityIdText &&
                conflict.Status != "Resolved")
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(conflict => conflict.Status, "Resolved")
                    .SetProperty(conflict => conflict.ResolvedAtUtc, now)
                    .SetProperty(conflict => conflict.ResolutionNote, normalizedNote),
                cancellationToken);
    }

    private async Task DeduplicateOpenConflictLogsForResultAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        CancellationToken cancellationToken)
    {
        if (conflicts.Count == 0)
            return;

        var fingerprints = conflicts
            .Select(conflict => new
            {
                EntityName = (conflict.EntityName ?? string.Empty).Trim(),
                EntityId = (conflict.EntityId ?? string.Empty).Trim(),
                Reason = conflict.Reason ?? string.Empty
            })
            .Where(conflict =>
                !string.IsNullOrWhiteSpace(conflict.EntityName) ||
                !string.IsNullOrWhiteSpace(conflict.EntityId) ||
                !string.IsNullOrWhiteSpace(conflict.Reason))
            .Distinct()
            .ToList();

        foreach (var fingerprint in fingerprints)
        {
            var ids = await _dbContext.ConflictLogs
                .Where(conflict =>
                    conflict.Status != "Resolved" &&
                    conflict.EntityName == fingerprint.EntityName &&
                    conflict.EntityId == fingerprint.EntityId &&
                    conflict.Reason == fingerprint.Reason)
                .OrderByDescending(conflict => conflict.CreatedAtUtc)
                .ThenByDescending(conflict => conflict.Id)
                .Select(conflict => conflict.Id)
                .ToListAsync(cancellationToken);

            if (ids.Count <= 1)
                continue;

            var duplicateIds = ids.Skip(1).ToList();
            await _dbContext.ConflictLogs
                .Where(conflict => duplicateIds.Contains(conflict.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    private static string NormalizeDeviceId(string? deviceId)
    {
        var normalized = (deviceId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "unknown-device" : normalized;
    }

    private static string NormalizeMutationId(string? mutationId)
        => string.IsNullOrWhiteSpace(mutationId) ? string.Empty : mutationId.Trim();

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
        CancellationToken cancellationToken)
    {
        var candidateKeys = BuildRentalCustomerReferenceKeys(
            dto.CustomerName);
        var normalizedBusinessNumber = NormalizeBusinessNumber(dto.BusinessNumber);
        var preferredOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(dto.ResponsibleOfficeCode, dto.OfficeCode);
        var preferredTenantCode = _officeScopeService.ResolveTenantForRentalCreate(dto.TenantCode, preferredOfficeCode);
        if (dto.CustomerId.HasValue && dto.CustomerId.Value != Guid.Empty)
        {
            var directCustomer = await _dbContext.Customers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(customer => customer.Id == dto.CustomerId.Value, cancellationToken);
            if (directCustomer is not null &&
                !directCustomer.IsDeleted &&
                CanReadCustomerForRentalReference(directCustomer) &&
                CustomerReferenceTenantMatches(directCustomer, preferredTenantCode) &&
                CustomerReferenceLooksValid(directCustomer, candidateKeys, normalizedBusinessNumber))
            {
                return directCustomer.Id;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedBusinessNumber))
        {
            var businessMatches = await _dbContext.Customers.IgnoreQueryFilters()
                .Where(customer => !customer.IsDeleted)
                .OrderByDescending(customer => customer.UpdatedAtUtc)
                .ToListAsync(cancellationToken);
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

        var exactNameMatches = await _dbContext.Customers.IgnoreQueryFilters()
            .Where(customer =>
                !customer.IsDeleted &&
                candidateNames.Contains(customer.NameOriginal))
            .OrderByDescending(customer => customer.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
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

        var nameKeyMatches = await _dbContext.Customers.IgnoreQueryFilters()
            .Where(customer =>
                !customer.IsDeleted &&
                normalizedMatchKeys.Contains(customer.NameMatchKey))
            .OrderByDescending(customer => customer.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        return ResolveReadableCustomerReference(
            nameKeyMatches,
            preferredOfficeCode,
            preferredTenantCode);
    }

    private async Task<List<RentalAssetDto>> PrepareScopedRentalAssetsAsync(
        IEnumerable<RentalAssetDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<RentalAssetDto>();
        var reservedManagementIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var reservedManagementNumbers = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var dto in payload)
        {
            dto.Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id;
            var existing = await _dbContext.RentalAssets.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is null)
            {
                existing = await FindExistingRentalAssetByNaturalKeyAsync(dto, cancellationToken);
                if (existing is not null)
                    dto.Id = existing.Id;
            }

            if (existing is not null && !_officeScopeService.CanWriteOfficeForRentals(existing.ResponsibleOfficeCode, existing.TenantCode, existing.OfficeCode))
            {
                AddClientConflict(dto, nameof(RentalAsset), "Current account cannot modify this office scope.", result);
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
                cancellationToken);
            dto.AssetKey = BuildRentalAssetKey(dto.ManagementCompanyCode, dto.ManagementNumber, dto.ManagementId, dto.MachineNumber, dto.CustomerName, dto.ItemName);
            scoped.Add(dto);
        }

        return scoped;
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
        CancellationToken cancellationToken)
    {
        dto.ManagementId = await ResolveManagementIdAsync(dto, existing, reservedManagementIds, cancellationToken);
        ReserveManagementValue(reservedManagementIds, dto.ManagementId, dto.Id);

        dto.ManagementNumber = await ResolveManagementNumberAsync(dto, existing, reservedManagementNumbers, cancellationToken);
        ReserveManagementValue(reservedManagementNumbers, dto.ManagementNumber, dto.Id);
    }

    private async Task<string> ResolveManagementIdAsync(
        RentalAssetDto dto,
        RentalAsset? existing,
        IDictionary<string, Guid> reservedManagementIds,
        CancellationToken cancellationToken)
    {
        var requestedValue = existing?.ManagementId ?? dto.ManagementId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(requestedValue) &&
            await IsManagementIdAvailableAsync(requestedValue, dto.Id, reservedManagementIds, cancellationToken))
        {
            return requestedValue;
        }

        var usedIds = await _dbContext.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id != dto.Id)
            .Select(asset => asset.ManagementId)
            .ToListAsync(cancellationToken);

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
        CancellationToken cancellationToken)
    {
        var requestedValue = existing?.ManagementNumber ?? dto.ManagementNumber?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(requestedValue) &&
            await IsManagementNumberAvailableAsync(requestedValue, dto.Id, reservedManagementNumbers, cancellationToken))
        {
            return requestedValue;
        }

        var registeredLocalDate = ConvertUtcToKoreaDate(dto.CreatedAtUtc == default ? DateTime.UtcNow : dto.CreatedAtUtc);
        var prefix = registeredLocalDate.ToString("yyMM", CultureInfo.InvariantCulture);
        var usedNumbers = await _dbContext.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id != dto.Id)
            .Select(asset => asset.ManagementNumber)
            .ToListAsync(cancellationToken);

        var nextSequence = usedNumbers
            .Select(number => ParseManagementNumberSequence(number, prefix))
            .Concat(reservedManagementNumbers.Keys.Select(number => ParseManagementNumberSequence(number, prefix)))
            .DefaultIfEmpty(0)
            .Max() + 1;

        return $"{prefix}-{nextSequence:000}";
    }

    private async Task<bool> IsManagementIdAvailableAsync(
        string managementId,
        Guid currentId,
        IDictionary<string, Guid> reservedManagementIds,
        CancellationToken cancellationToken)
    {
        var normalizedValue = (managementId ?? string.Empty).Trim();
        if (reservedManagementIds.TryGetValue(normalizedValue, out var reservedId) && reservedId != currentId)
            return false;

        return await _dbContext.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id != currentId)
            .AllAsync(asset => asset.ManagementId != normalizedValue, cancellationToken);
    }

    private async Task<bool> IsManagementNumberAvailableAsync(
        string managementNumber,
        Guid currentId,
        IDictionary<string, Guid> reservedManagementNumbers,
        CancellationToken cancellationToken)
    {
        var normalizedValue = (managementNumber ?? string.Empty).Trim();
        if (reservedManagementNumbers.TryGetValue(normalizedValue, out var reservedId) && reservedId != currentId)
            return false;

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

    private async Task<List<RentalAssetDto>> FilterValidRentalAssetsAsync(
        IEnumerable<RentalAssetDto> payload,
        IReadOnlyDictionary<Guid, Guid> resolvedRentalProfileIds,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<RentalAssetDto>();

        foreach (var dto in payload)
        {
            if (dto.BillingProfileId.HasValue &&
                dto.BillingProfileId.Value != Guid.Empty &&
                resolvedRentalProfileIds.TryGetValue(dto.BillingProfileId.Value, out var remappedBillingProfileId))
            {
                dto.BillingProfileId = remappedBillingProfileId;
            }

            var requestedCustomerId = dto.CustomerId;
            dto.CustomerId = await ResolveRentalAssetCustomerReferenceAsync(dto, cancellationToken);
            if (!await ValidateExplicitRentalCustomerReferenceAsync(
                    dto,
                    nameof(RentalAsset),
                    requestedCustomerId,
                    dto.CustomerId,
                    result,
                    cancellationToken))
            {
                continue;
            }

            dto.ItemId = await ResolveRentalAssetItemReferenceAsync(dto, cancellationToken);
            var linkedCustomer = await GetRentalReferenceCustomerAsync(dto.CustomerId, cancellationToken);
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
            }

            RentalBillingProfile? billingProfile = null;
            if (dto.BillingProfileId.HasValue && dto.BillingProfileId.Value != Guid.Empty)
            {
                var requestedBillingProfileId = dto.BillingProfileId.Value;
                billingProfile = await ResolveRentalAssetBillingProfileReferenceAsync(dto, cancellationToken);
                if (billingProfile is null || billingProfile.IsDeleted)
                {
                    billingProfile = await ResolveRentalAssetBillingProfileReferenceByFieldsAsync(dto, cancellationToken);
                    if (billingProfile is null || billingProfile.IsDeleted)
                    {
                        AddClientConflict(dto, nameof(RentalAsset),
                            $"Referenced rental billing profile was not found: {requestedBillingProfileId}.", result);
                        continue;
                    }
                }

                dto.BillingProfileId = billingProfile.Id;
            }
            else
            {
                billingProfile = await ResolveRentalAssetBillingProfileReferenceByFieldsAsync(dto, cancellationToken);
                if (billingProfile is not null)
                    dto.BillingProfileId = billingProfile.Id;
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

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<RentalBillingProfile?> ResolveRentalAssetBillingProfileReferenceAsync(
        RentalAssetDto dto,
        CancellationToken cancellationToken)
    {
        if (!dto.BillingProfileId.HasValue || dto.BillingProfileId.Value == Guid.Empty)
            return null;

        var preferredOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(dto.ResponsibleOfficeCode, dto.OfficeCode);
        var preferredTenantCode = _officeScopeService.ResolveTenantForRentalCreate(dto.TenantCode, preferredOfficeCode);
        var direct = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == dto.BillingProfileId.Value, cancellationToken);
        if (direct is not null &&
            !direct.IsDeleted &&
            RentalBillingProfileMatchesRentalAssetScope(direct, preferredOfficeCode, preferredTenantCode) &&
            (RentalBillingProfileMatchesRentalAssetReference(direct, dto) ||
             !direct.CustomerId.HasValue ||
             direct.CustomerId.Value == Guid.Empty))
            return direct;

        var existingAsset = await _dbContext.RentalAssets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(asset => asset.Id == dto.Id, cancellationToken);
        if (existingAsset?.BillingProfileId is Guid existingBillingProfileId)
        {
            var fromExistingAsset = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == existingBillingProfileId, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var resolvedCustomerId = dto.CustomerId;
        if (!resolvedCustomerId.HasValue || resolvedCustomerId.Value == Guid.Empty)
        {
            resolvedCustomerId = await ResolveRentalAssetCustomerReferenceAsync(dto, cancellationToken);
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
        CancellationToken cancellationToken)
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
            var directItem = await _dbContext.Items.IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == dto.ItemId.Value, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var candidateKeys = BuildRentalCustomerReferenceKeys(
            dto.CustomerName,
            dto.CurrentCustomerName);
        var preferredOfficeCode = _officeScopeService.ResolveRentalResponsibleScopeForCreate(dto.ResponsibleOfficeCode, null);
        var preferredTenantCode = _officeScopeService.ResolveTenantForRentalCreate(dto.TenantCode, preferredOfficeCode);
        if (dto.CustomerId.HasValue && dto.CustomerId.Value != Guid.Empty)
        {
            var directCustomer = await _dbContext.Customers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(customer => customer.Id == dto.CustomerId.Value, cancellationToken);
            if (directCustomer is not null &&
                !directCustomer.IsDeleted &&
                CanReadCustomerForRentalReference(directCustomer) &&
                CustomerReferenceTenantMatches(directCustomer, preferredTenantCode) &&
                CustomerReferenceLooksValid(directCustomer, candidateKeys, null))
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

        var exactNameMatches = await _dbContext.Customers.IgnoreQueryFilters()
            .Where(customer => !customer.IsDeleted && candidateNames.Contains(customer.NameOriginal))
            .OrderByDescending(customer => customer.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
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

        var nameKeyMatches = await _dbContext.Customers.IgnoreQueryFilters()
            .Where(customer => !customer.IsDeleted && normalizedMatchKeys.Contains(customer.NameMatchKey))
            .OrderByDescending(customer => customer.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        return ResolveReadableCustomerReference(nameKeyMatches, preferredOfficeCode, preferredTenantCode);
    }

    private async Task<Customer?> GetRentalReferenceCustomerAsync(Guid? customerId, CancellationToken cancellationToken)
    {
        if (!customerId.HasValue || customerId.Value == Guid.Empty)
            return null;

        var customer = await _dbContext.Customers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == customerId.Value, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        if (!requestedCustomerId.HasValue ||
            requestedCustomerId.Value == Guid.Empty ||
            resolvedCustomerId.HasValue)
        {
            return true;
        }

        var requestedCustomer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(customer => customer.Id == requestedCustomerId.Value, cancellationToken);
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

            var customer = dto.CustomerId == Guid.Empty
                ? null
                : await _dbContext.Customers.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
            if (dto.CustomerId == Guid.Empty || customer is null || customer.IsDeleted)
            {
                if (dto.IsDeleted && existing is null)
                    continue;

                if (existing is not null)
                {
                    if (!_officeScopeService.CanWriteOfficeForInvoices(existing.ResponsibleOfficeCode, existing.TenantCode, existing.OfficeCode))
                    {
                        AddClientConflict(dto, nameof(Invoice),
                            "Current account cannot modify this office scope.", result);
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
                    _officeScopeService.CanWriteOfficeForInvoices(existing.ResponsibleOfficeCode, existing.TenantCode, existing.OfficeCode))
                {
                    dto.CustomerId = existing.CustomerId;
                    if (!await ValidateReadableInvoiceLineItemsAsync(dto, result, cancellationToken))
                        continue;
                    if (!await ValidateWritableInvoiceRentalBillingProfileAsync(dto, result, cancellationToken))
                        continue;

                    valid.Add(dto);
                    continue;
                }

                if (existing is not null &&
                    existing.CustomerId != customer.Id &&
                    _officeScopeService.CanWriteOfficeForInvoices(existing.ResponsibleOfficeCode, existing.TenantCode, existing.OfficeCode))
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

            if (existing is not null && !_officeScopeService.CanWriteOfficeForInvoices(existing.ResponsibleOfficeCode, existing.TenantCode, existing.OfficeCode))
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

        var itemIds = (dto.Lines ?? [])
            .Where(line => !line.IsDeleted && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
            .Select(line => line.ItemId!.Value)
            .Distinct()
            .ToList();
        if (itemIds.Count == 0)
            return true;

        var items = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) && !item.IsDeleted)
            .Select(item => new { item.Id, item.OfficeCode, item.TenantCode })
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

        return true;
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
                current.OfficeCode
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

        if (_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode))
            return true;

        AddClientConflict(
            dto,
            nameof(Invoice),
            $"Referenced rental billing profile is outside the writable office scope: {dto.LinkedRentalBillingProfileId}.",
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
            .FirstOrDefaultAsync(transaction => !transaction.IsDeleted && transaction.Id == dto.Id, cancellationToken);
        if (linkedTransaction is null)
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

            var invoice = await _dbContext.Invoices.IgnoreQueryFilters()
                .Include(x => x.Customer)
                .FirstOrDefaultAsync(x => x.Id == dto.InvoiceId, cancellationToken);
            if (dto.InvoiceId == Guid.Empty || invoice is null || invoice.IsDeleted)
            {
                if (dto.IsDeleted && existing is null)
                    continue;

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

            if (!await ValidateCompatibleLinkedTransactionForPaymentAsync(
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

    private async Task<ItemWarehouseStockUpsertResult> UpsertItemWarehouseStocksAsync(
        IEnumerable<ItemWarehouseStockDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var missingItemCount = 0;
        var deletedItemCount = 0;
        var outOfScopeItemCount = 0;
        var outOfScopeWarehouseCount = 0;
        var affectedItemIds = new HashSet<Guid>();
        var acceptedStockKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var incomingRows = payload
            .Where(dto => dto.ItemId != Guid.Empty && !string.IsNullOrWhiteSpace(dto.WarehouseCode))
            .Select(dto => new ItemWarehouseStockDto
            {
                ItemId = dto.ItemId,
                WarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeLoose(dto.WarehouseCode),
                Quantity = dto.Quantity,
                UpdatedAtUtc = NormalizeUtc(dto.UpdatedAtUtc),
                Revision = dto.Revision,
                ExpectedRevision = dto.ExpectedRevision
            })
            .ToList();

        var sanitized = incomingRows
            .GroupBy(dto => new { dto.ItemId, dto.WarehouseCode })
            .Select(group => group.Last())
            .ToList();

        var groupedByItem = sanitized
            .GroupBy(dto => dto.ItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var itemId in groupedByItem.Keys)
        {
            var scopedItem = await _dbContext.Items.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == itemId, cancellationToken);
            if (scopedItem is null || scopedItem.IsDeleted || !_officeScopeService.CanWriteOfficeForItems(scopedItem.OfficeCode, scopedItem.TenantCode))
                continue;

            var desiredCodes = groupedByItem[itemId]
                .Select(stock => stock.WarehouseCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var maxKnownRevision = groupedByItem[itemId]
                .Select(stock => Math.Max(stock.ExpectedRevision, stock.Revision))
                .DefaultIfEmpty(0)
                .Max();

            var staleRows = await _officeScopeService.ApplyWarehouseScope(_dbContext.ItemWarehouseStocks)
                .Where(x => x.ItemId == itemId && !desiredCodes.Contains(x.WarehouseCode))
                .ToListAsync(cancellationToken);
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

            staleRows = staleRows.Except(protectedStaleRows).ToList();
            if (staleRows.Count > 0)
            {
                _dbContext.ItemWarehouseStocks.RemoveRange(staleRows);
                scopedItem.UpdatedAtUtc = DateTime.UtcNow;
                affectedItemIds.Add(itemId);
            }
        }

        foreach (var dto in sanitized)
        {
            var item = await _dbContext.Items.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == dto.ItemId, cancellationToken);
            if (item is null)
            {
                missingItemCount++;
                continue;
            }

            if (item.IsDeleted)
            {
                deletedItemCount++;
                continue;
            }

            if (!_officeScopeService.CanWriteOfficeForItems(item.OfficeCode, item.TenantCode))
            {
                outOfScopeItemCount++;
                continue;
            }

            if (!_officeScopeService.CanWriteWarehouse(dto.WarehouseCode, item.OfficeCode))
            {
                outOfScopeWarehouseCount++;
                continue;
            }

            var entity = await _dbContext.ItemWarehouseStocks
                .FirstOrDefaultAsync(x => x.ItemId == dto.ItemId && x.WarehouseCode == dto.WarehouseCode, cancellationToken);

            if (entity is null)
            {
                _dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
                {
                    ItemId = dto.ItemId,
                    WarehouseCode = dto.WarehouseCode,
                    Quantity = dto.Quantity,
                    UpdatedAtUtc = NormalizeUtc(dto.UpdatedAtUtc),
                    Revision = _revisionClock.NextRevision()
                });
                affectedItemIds.Add(dto.ItemId);
                acceptedStockKeys.Add(BuildItemWarehouseStockSnapshotKey(dto.ItemId, dto.WarehouseCode));
                continue;
            }

            if (dto.ExpectedRevision > 0 && entity.Revision != dto.ExpectedRevision)
            {
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
                await AddServerConflictAsync(dto, entity, nameof(ItemWarehouseStock), "Server version is newer.", result, cancellationToken);
                continue;
            }

            entity.Quantity = dto.Quantity;
            entity.UpdatedAtUtc = NormalizeUtc(dto.UpdatedAtUtc);
            entity.Revision = _revisionClock.NextRevision();
            affectedItemIds.Add(dto.ItemId);
            acceptedStockKeys.Add(BuildItemWarehouseStockSnapshotKey(dto.ItemId, dto.WarehouseCode));
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
        return new ItemWarehouseStockUpsertResult(affectedItemIds, acceptedStockKeys);
    }

    private static string BuildItemWarehouseStockSnapshotKey(Guid itemId, string? warehouseCode)
        => $"{itemId:N}|{OfficeCodeCatalog.NormalizeWarehouseCodeLoose(warehouseCode)}";

    private sealed record ItemWarehouseStockUpsertResult(
        HashSet<Guid> AffectedItemIds,
        HashSet<string> AcceptedStockKeys);

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
            item.Revision = _revisionClock.NextRevision();
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

    private void AddClientConflict<TDto>(TDto client, string entityName, string reason, SyncPushResult result)
    {
        var entityId = client switch
        {
            SyncEntityDto dto => dto.Id.ToString(),
            _ => string.Empty
        };

        var conflict = new ConflictLog
        {
            UserId = _currentUserContext.UserId,
            Username = _currentUserContext.Username,
            EntityName = entityName,
            EntityId = entityId,
            ClientJson = JsonSerializer.Serialize(client, ConflictJsonOptions),
            ServerJson = string.Empty,
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

    private async Task AddServerConflictAsync<TDto, TEntity>(
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
        await PopulateServerConflictActorAsync(dto, cancellationToken);
        result.Conflicts.Add(dto);
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

    private async Task PopulateServerConflictActorAsync(ConflictLogDto conflict, CancellationToken cancellationToken)
    {
        var entityName = (conflict.EntityName ?? string.Empty).Trim();
        var entityId = (conflict.EntityId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(entityName) || string.IsNullOrWhiteSpace(entityId))
            return;

        var latestAudit = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(audit => audit.EntityName == entityName && audit.EntityId == entityId)
            .OrderByDescending(audit => audit.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestAudit is null)
            return;

        conflict.ServerUserId = latestAudit.UserId;
        conflict.ServerUsername = latestAudit.Username;
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

    private async Task PersistCustomerContractsToStorageAsync(
        IEnumerable<CustomerContractDto> contracts,
        ICollection<string> savedStoragePaths,
        CancellationToken cancellationToken)
    {
        foreach (var dto in contracts.Where(current => !current.IsDeleted && (current.FileContent?.Length ?? 0) > 0))
        {
            var entity = await _dbContext.CustomerContracts
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == dto.Id, cancellationToken);
            if (entity is null)
                continue;

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
        }
    }

    private static string ComputeSha256Hex(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content));

    private async Task PersistTransactionAttachmentsToStorageAsync(
        IEnumerable<TransactionAttachmentDto> attachments,
        ICollection<string> savedStoragePaths,
        CancellationToken cancellationToken)
    {
        foreach (var dto in attachments.Where(current => !current.IsDeleted && (current.FileContent?.Length ?? 0) > 0))
        {
            var entity = await _dbContext.TransactionAttachments
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == dto.Id, cancellationToken);
            if (entity is null)
                continue;

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
        }
    }

    private void DeleteSavedStoragePaths(IEnumerable<string> savedStoragePaths)
    {
        foreach (var storedPath in savedStoragePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _fileStorage.DeleteIfExists(storedPath);
            }
            catch
            {
                // best-effort cleanup: preserve the original sync failure.
            }
        }
    }

    private async Task RemoveSupersededPurgeRecordsAsync<TDto>(
        string kind,
        IEnumerable<TDto> payload,
        CancellationToken cancellationToken)
        where TDto : SyncEntityDto
    {
        var normalizedKind = NormalizePurgeRecordKind(kind);
        var activeIds = payload
            .Where(current => current.Id != Guid.Empty && !current.IsDeleted)
            .Select(current => current.Id)
            .Distinct()
            .ToList();
        if (activeIds.Count == 0)
            return;

        await _dbContext.RecycleBinPurgeRecords
            .Where(current => current.Kind == normalizedKind && activeIds.Contains(current.EntityId))
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
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.FromResult(_revisionClock.Current);
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
