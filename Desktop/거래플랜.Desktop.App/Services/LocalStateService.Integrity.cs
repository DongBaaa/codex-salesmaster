using System.IO;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private const int LocalIntegrityDetailRowLimit = 1_000;

    public async Task<LocalIntegrityReport> BuildIntegrityReportAsync(SessionState session, CancellationToken ct = default)
    {
        var createdAtUtc = DateTime.UtcNow;
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session?.OfficeCode, DomainConstants.OfficeUsenet);
        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session?.TenantCode, session?.OfficeCode);
        var dirtyCount = session is not null && session.IsLoggedIn
            ? await CountDirtyAsync(session, ct)
            : await CountDirtyAsync(ct);
        var pendingServerMirrorRefresh = await IsServerMirrorRefreshRequiredAsync(ct);
        var issues = new List<LocalIntegrityIssue>();

        if (session is null || !session.IsLoggedIn)
            return new LocalIntegrityReport(createdAtUtc, officeCode, tenantCode, dirtyCount, pendingServerMirrorRefresh, issues);

        var integrityTenantCode = ResolveIntegrityTenantCode(session);
        var integrityOfficeCodes = ResolveIntegrityOfficeCodes(integrityTenantCode);

        var customerQuery = _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(customer => !customer.IsDeleted);
        var totalCustomerCount = await customerQuery.CountAsync(ct);
        var readableCustomerCount = await ApplyCustomerScope(customerQuery, session).CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "out_of_scope_customers",
            Math.Max(0, totalCustomerCount - readableCustomerCount),
            "현재 계정 범위 밖 거래처 캐시가 로컬 DB에 남아 있습니다.");

        var itemQuery = _db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => !item.IsDeleted);
        var totalItemCount = await itemQuery.CountAsync(ct);
        var readableItemCount = await ApplyItemScope(itemQuery, session).CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "out_of_scope_items",
            Math.Max(0, totalItemCount - readableItemCount),
            "현재 계정 범위 밖 품목/재고 캐시가 로컬 DB에 남아 있습니다.");

        var invoiceQuery = _db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => !invoice.IsDeleted && invoice.IsLatestVersion);
        var totalInvoiceCount = await invoiceQuery.CountAsync(ct);
        var readableInvoiceCount = await ApplyInvoiceScope(invoiceQuery, session).CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "out_of_scope_invoices",
            Math.Max(0, totalInvoiceCount - readableInvoiceCount),
            "현재 계정 범위 밖 전표 캐시가 로컬 DB에 남아 있습니다.");

        var transactionQuery = _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction => !transaction.IsDeleted);
        var totalTransactionCount = await transactionQuery.CountAsync(ct);
        var readableTransactionCount = await ApplyTransactionScope(transactionQuery, session).CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "out_of_scope_transactions",
            Math.Max(0, totalTransactionCount - readableTransactionCount),
            "현재 계정 범위 밖 거래/수금 캐시가 로컬 DB에 남아 있습니다.");

        var rentalProfileScopes = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => !profile.IsDeleted)
            .Select(profile => new { profile.ResponsibleOfficeCode, profile.TenantCode })
            .ToListAsync(ct);
        var outOfScopeRentalProfileCount = rentalProfileScopes.Count(scope =>
            !CanReadRentalCustomerScope(session, scope.ResponsibleOfficeCode, scope.TenantCode));
        AddIssueIfNeeded(
            issues,
            "out_of_scope_rental_profiles",
            outOfScopeRentalProfileCount,
            "현재 계정 범위 밖 렌탈 청구 프로필 캐시가 로컬 DB에 남아 있습니다.");

        var rentalAssetScopes = await _db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => !asset.IsDeleted)
            .Select(asset => new { asset.ResponsibleOfficeCode, asset.TenantCode })
            .ToListAsync(ct);
        var outOfScopeRentalAssetCount = rentalAssetScopes.Count(scope =>
            !CanReadRentalCustomerScope(session, scope.ResponsibleOfficeCode, scope.TenantCode));
        AddIssueIfNeeded(
            issues,
            "out_of_scope_rental_assets",
            outOfScopeRentalAssetCount,
            "현재 계정 범위 밖 렌탈 자산 캐시가 로컬 DB에 남아 있습니다.");

        var staleOutboxSentCutoffUtc = DateTime.UtcNow - StaleSyncOutboxSentThreshold;
        var staleOutboxSentCount = await _db.SyncOutboxEntries
            .AsNoTracking()
            .CountAsync(entry =>
                entry.Status == "Sent" &&
                entry.SentAtUtc.HasValue &&
                entry.SentAtUtc.Value <= staleOutboxSentCutoffUtc, ct);
        AddIssueIfNeeded(
            issues,
            "sync_outbox_sent_stuck",
            staleOutboxSentCount,
            "전송 중 상태로 오래 멈춘 sync outbox가 남아 있습니다.",
            severity: "Error");

        var failedOutboxCount = await _db.SyncOutboxEntries
            .AsNoTracking()
            .CountAsync(entry => entry.Status == "Failed", ct);
        AddIssueIfNeeded(
            issues,
            "sync_outbox_failed_pending",
            failedOutboxCount,
            "실패 상태의 sync outbox가 남아 있어 수동 재시도 또는 원인 확인이 필요합니다.");

        var integrityRentalProfileQuery = ApplyIntegrityRentalProfileScope(
            _db.RentalBillingProfiles.IgnoreQueryFilters(),
            integrityTenantCode,
            integrityOfficeCodes);
        var integrityRentalAssetQuery = ApplyIntegrityRentalAssetScope(
            _db.RentalAssets.IgnoreQueryFilters(),
            integrityTenantCode,
            integrityOfficeCodes);
        var integrityCustomerQuery = ApplyIntegrityCustomerScope(
            _db.Customers.IgnoreQueryFilters(),
            integrityTenantCode,
            integrityOfficeCodes);
        var integrityItemQuery = ApplyIntegrityItemScope(
            _db.Items.IgnoreQueryFilters(),
            integrityTenantCode,
            integrityOfficeCodes);

        var duplicateRentalProfileKeyCount = await integrityRentalProfileQuery
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => !profile.IsDeleted && !string.IsNullOrWhiteSpace(profile.ProfileKey))
            .GroupBy(profile => profile.ProfileKey)
            .Where(group => group.Count() > 1)
            .Select(group => (int?)group.Count())
            .SumAsync(ct) ?? 0;
        AddIssueIfNeeded(
            issues,
            "duplicate_rental_profile_keys",
            duplicateRentalProfileKeyCount,
            "중복된 렌탈 청구 프로필 키가 남아 있습니다.",
            severity: "Error");

        var duplicateRentalAssetKeyCount = await integrityRentalAssetQuery
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => !asset.IsDeleted && !string.IsNullOrWhiteSpace(asset.AssetKey))
            .GroupBy(asset => asset.AssetKey)
            .Where(group => group.Count() > 1)
            .Select(group => (int?)group.Count())
            .SumAsync(ct) ?? 0;
        AddIssueIfNeeded(
            issues,
            "duplicate_rental_asset_keys",
            duplicateRentalAssetKeyCount,
            "중복된 렌탈 자산 키가 남아 있습니다.",
            severity: "Error");

        var activeCustomerMasterIds = _db.CustomerMasters
            .IgnoreQueryFilters()
            .Where(customerMaster => !customerMaster.IsDeleted)
            .Select(customerMaster => customerMaster.Id);
        var activeCustomerCategoryIds = _db.CustomerCategories
            .IgnoreQueryFilters()
            .Where(category => !category.IsDeleted)
            .Select(category => category.Id);
        var activeCustomerIds = integrityCustomerQuery
            .Where(customer => !customer.IsDeleted)
            .Select(customer => customer.Id);
        var activeRentalProfileIds = _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .Where(profile => !profile.IsDeleted)
            .Select(profile => profile.Id);
        var activeItemIds = integrityItemQuery
            .Where(item => !item.IsDeleted)
            .Select(item => item.Id);
        var activeInvoiceIds = _db.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => !invoice.IsDeleted)
            .Select(invoice => invoice.Id);
        var activeTransactionIds = _db.Transactions
            .IgnoreQueryFilters()
            .Where(transaction => !transaction.IsDeleted)
            .Select(transaction => transaction.Id);

        var inventoryItemSnapshots = await _db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => !item.IsDeleted)
            .Select(item => new
            {
                item.Id,
                item.TrackingType,
                item.CurrentStock
            })
            .ToListAsync(ct);
        var warehouseStockSnapshots = await _db.ItemWarehouseStocks
            .AsNoTracking()
            .Select(stock => new
            {
                stock.ItemId,
                stock.Quantity
            })
            .ToListAsync(ct);
        var inventoryItemIdSet = inventoryItemSnapshots
            .Select(item => item.Id)
            .ToHashSet();
        var warehouseStockTotals = warehouseStockSnapshots
            .GroupBy(stock => stock.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(stock => stock.Quantity));
        var inventorySnapshotMismatchCount = inventoryItemSnapshots.Count(item =>
        {
            if (!ItemOperationalPolicy.SupportsInventory(item.TrackingType))
                return false;

            var snapshotTotal = warehouseStockTotals.TryGetValue(item.Id, out var totalQuantity)
                ? totalQuantity
                : 0m;
            return item.CurrentStock != snapshotTotal;
        });
        AddIssueIfNeeded(
            issues,
            "inventory_current_stock_snapshot_mismatch",
            inventorySnapshotMismatchCount,
            "품목 현재고와 재고 스냅샷 합계가 서로 맞지 않습니다.",
            severity: "Error");

        var nonInventorySnapshotResidueCount = inventoryItemSnapshots.Count(item =>
        {
            if (ItemOperationalPolicy.SupportsInventory(item.TrackingType))
                return false;

            var snapshotTotal = warehouseStockTotals.TryGetValue(item.Id, out var totalQuantity)
                ? totalQuantity
                : 0m;
            return item.CurrentStock != 0m || snapshotTotal != 0m;
        });
        AddIssueIfNeeded(
            issues,
            "inventory_nonstock_snapshot_residue",
            nonInventorySnapshotResidueCount,
            "재고 미관리 품목에 현재고 또는 창고 재고 스냅샷이 남아 있습니다.",
            severity: "Error");

        var orphanWarehouseStockCount = warehouseStockSnapshots.Count(stock => !inventoryItemIdSet.Contains(stock.ItemId));
        AddIssueIfNeeded(
            issues,
            "orphan_item_warehouse_stock_refs",
            orphanWarehouseStockCount,
            "창고 재고 스냅샷이 삭제되었거나 없는 품목을 참조하고 있습니다.",
            severity: "Error");

        var orphanStockLayerItemCount = await _db.StockLayers
            .AsNoTracking()
            .Where(layer => layer.ItemId.HasValue && !activeItemIds.Contains(layer.ItemId.Value))
            .CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_stock_layer_item_refs",
            orphanStockLayerItemCount,
            "재고 원가 레이어가 삭제되었거나 없는 품목을 참조하고 있습니다.",
            severity: "Error");

        var orphanInventoryMovementItemCount = await _db.InventoryMovements
            .AsNoTracking()
            .Where(movement => movement.ItemId.HasValue && !activeItemIds.Contains(movement.ItemId.Value))
            .CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_inventory_movement_item_refs",
            orphanInventoryMovementItemCount,
            "재고 이동 원장이 삭제되었거나 없는 품목을 참조하고 있습니다.",
            severity: "Error");

        var orphanSerialLedgerItemCount = await _db.SerialLedgers
            .AsNoTracking()
            .Where(ledger => ledger.ItemId.HasValue && !activeItemIds.Contains(ledger.ItemId.Value))
            .CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_serial_ledger_item_refs",
            orphanSerialLedgerItemCount,
            "시리얼 원장이 삭제되었거나 없는 품목을 참조하고 있습니다.",
            severity: "Error");

        var orphanInventoryTransferLineItemCount = await _db.InventoryTransferLines
            .AsNoTracking()
            .Where(line => !line.IsDeleted && line.ItemId.HasValue && !activeItemIds.Contains(line.ItemId.Value))
            .CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_inventory_transfer_line_item_refs",
            orphanInventoryTransferLineItemCount,
            "재고이동 상세가 삭제되었거나 없는 품목을 참조하고 있습니다.",
            severity: "Error");

        var crossTenantInventoryTransferCount = (await _db.InventoryTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(transfer => !transfer.IsDeleted)
                .Select(transfer => new
                {
                    transfer.FromWarehouseCode,
                    transfer.ToWarehouseCode
                })
                .ToListAsync(ct))
            .Count(transfer => IsCrossTenantInventoryTransferRoute(
                transfer.FromWarehouseCode,
                transfer.ToWarehouseCode));
        AddIssueIfNeeded(
            issues,
            "cross_tenant_inventory_transfers",
            crossTenantInventoryTransferCount,
            "업체 간 직접 재고이동 문서가 로컬 DB에 남아 있습니다.",
            severity: "Error");

        var missingCustomerMasterCategoryCount = await _db.CustomerMasters
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(customerMaster => !customerMaster.IsDeleted && customerMaster.CategoryId.HasValue && !activeCustomerCategoryIds.Contains(customerMaster.CategoryId.Value))
            .CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_customer_master_category_refs",
            missingCustomerMasterCategoryCount,
            "거래처 기준 정보가 존재하지 않는 거래처 분류를 참조하고 있습니다.",
            severity: "Error");

        var missingCustomerMasterCount = await _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(customer => !customer.IsDeleted && customer.CustomerMasterId.HasValue && !activeCustomerMasterIds.Contains(customer.CustomerMasterId.Value))
            .CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_customer_master_refs",
            missingCustomerMasterCount,
            "거래처가 존재하지 않는 거래처 기준 정보를 참조하고 있습니다.",
            severity: "Error");

        var missingCustomerCategoryCount = await _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(customer => !customer.IsDeleted && customer.CategoryId.HasValue && !activeCustomerCategoryIds.Contains(customer.CategoryId.Value))
            .CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_customer_category_refs",
            missingCustomerCategoryCount,
            "거래처가 존재하지 않는 거래처 분류를 참조하고 있습니다.",
            severity: "Error");

        var orphanRentalProfileCustomerQuery = integrityRentalProfileQuery
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => !profile.IsDeleted && profile.CustomerId.HasValue && !activeCustomerIds.Contains(profile.CustomerId.Value));
        var orphanRentalProfileCustomerCount = await orphanRentalProfileCustomerQuery.CountAsync(ct);
        var orphanRentalProfileCustomerRows = await orphanRentalProfileCustomerQuery
            .OrderBy(profile => profile.CustomerName)
            .ThenBy(profile => profile.ProfileKey)
            .Take(LocalIntegrityDetailRowLimit + 1)
            .Select(profile => new
            {
                profile.ProfileKey,
                profile.CustomerName,
                profile.InstallSiteName,
                profile.CustomerId,
                profile.ResponsibleOfficeCode,
                profile.TenantCode
            })
            .ToListAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_rental_profile_customer_refs",
            orphanRentalProfileCustomerCount,
            "렌탈 청구 프로필이 존재하지 않는 거래처를 참조하고 있습니다.",
            severity: "Error",
            detailRows: BuildIntegrityDetailRows(
                orphanRentalProfileCustomerRows,
                orphanRentalProfileCustomerCount,
                row => $"프로필키={FormatDetailValue(row.ProfileKey)} / 거래처명={FormatDetailValue(row.CustomerName)} / 설치처={FormatDetailValue(row.InstallSiteName)} / 누락 CustomerId={FormatDetailGuid(row.CustomerId)} / 담당지점={FormatDetailValue(row.ResponsibleOfficeCode)} / 테넌트={FormatDetailValue(row.TenantCode)}"));

        var orphanRentalAssetCustomerQuery = integrityRentalAssetQuery
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => !asset.IsDeleted && asset.CustomerId.HasValue && !activeCustomerIds.Contains(asset.CustomerId.Value));
        var orphanRentalAssetCustomerCount = await orphanRentalAssetCustomerQuery.CountAsync(ct);
        var orphanRentalAssetCustomerRows = await orphanRentalAssetCustomerQuery
            .OrderBy(asset => asset.CustomerName)
            .ThenBy(asset => asset.ItemName)
            .ThenBy(asset => asset.MachineNumber)
            .Take(LocalIntegrityDetailRowLimit + 1)
            .Select(asset => new
            {
                asset.AssetKey,
                asset.ManagementNumber,
                asset.MachineNumber,
                asset.ItemName,
                asset.CustomerName,
                asset.CurrentCustomerName,
                asset.InstallLocation,
                asset.CustomerId,
                asset.ResponsibleOfficeCode,
                asset.TenantCode
            })
            .ToListAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_rental_asset_customer_refs",
            orphanRentalAssetCustomerCount,
            "렌탈 자산이 존재하지 않는 거래처를 참조하고 있습니다.",
            severity: "Error",
            detailRows: BuildIntegrityDetailRows(
                orphanRentalAssetCustomerRows,
                orphanRentalAssetCustomerCount,
                row => $"자산키={FormatDetailValue(row.AssetKey)} / 관리번호={FormatDetailValue(row.ManagementNumber)} / 시리얼={FormatDetailValue(row.MachineNumber)} / 품목={FormatDetailValue(row.ItemName)} / 거래처={FormatDetailValue(row.CustomerName, row.CurrentCustomerName)} / 설치위치={FormatDetailValue(row.InstallLocation)} / 누락 CustomerId={FormatDetailGuid(row.CustomerId)} / 담당지점={FormatDetailValue(row.ResponsibleOfficeCode)} / 테넌트={FormatDetailValue(row.TenantCode)}"));

        var orphanRentalAssetProfileQuery = integrityRentalAssetQuery
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue && !activeRentalProfileIds.Contains(asset.BillingProfileId.Value));
        var orphanRentalAssetProfileCount = await orphanRentalAssetProfileQuery.CountAsync(ct);
        var orphanRentalAssetProfileRows = await orphanRentalAssetProfileQuery
            .OrderBy(asset => asset.CustomerName)
            .ThenBy(asset => asset.ItemName)
            .ThenBy(asset => asset.MachineNumber)
            .Take(LocalIntegrityDetailRowLimit + 1)
            .Select(asset => new
            {
                asset.AssetKey,
                asset.ManagementNumber,
                asset.MachineNumber,
                asset.ItemName,
                asset.CustomerName,
                asset.InstallLocation,
                asset.BillingProfileId,
                asset.ResponsibleOfficeCode,
                asset.TenantCode
            })
            .ToListAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_rental_asset_profile_refs",
            orphanRentalAssetProfileCount,
            "렌탈 자산이 존재하지 않는 청구 프로필을 참조하고 있습니다.",
            severity: "Error",
            detailRows: BuildIntegrityDetailRows(
                orphanRentalAssetProfileRows,
                orphanRentalAssetProfileCount,
                row => $"자산키={FormatDetailValue(row.AssetKey)} / 관리번호={FormatDetailValue(row.ManagementNumber)} / 시리얼={FormatDetailValue(row.MachineNumber)} / 품목={FormatDetailValue(row.ItemName)} / 거래처={FormatDetailValue(row.CustomerName)} / 설치위치={FormatDetailValue(row.InstallLocation)} / 누락 BillingProfileId={FormatDetailGuid(row.BillingProfileId)} / 담당지점={FormatDetailValue(row.ResponsibleOfficeCode)} / 테넌트={FormatDetailValue(row.TenantCode)}"));

        var orphanRentalAssetItemQuery = integrityRentalAssetQuery
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => !asset.IsDeleted && asset.ItemId.HasValue && !activeItemIds.Contains(asset.ItemId.Value));
        var orphanRentalAssetItemCount = await orphanRentalAssetItemQuery.CountAsync(ct);
        var orphanRentalAssetItemRows = await orphanRentalAssetItemQuery
            .OrderBy(asset => asset.ItemName)
            .ThenBy(asset => asset.CustomerName)
            .ThenBy(asset => asset.MachineNumber)
            .Take(LocalIntegrityDetailRowLimit + 1)
            .Select(asset => new
            {
                asset.AssetKey,
                asset.ManagementNumber,
                asset.MachineNumber,
                asset.ItemName,
                asset.Manufacturer,
                asset.CustomerName,
                asset.InstallLocation,
                asset.ItemId,
                asset.ResponsibleOfficeCode,
                asset.TenantCode
            })
            .ToListAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_rental_asset_item_refs",
            orphanRentalAssetItemCount,
            "렌탈 자산이 존재하지 않는 품목을 참조하고 있습니다.",
            severity: "Error",
            detailRows: BuildIntegrityDetailRows(
                orphanRentalAssetItemRows,
                orphanRentalAssetItemCount,
                row => $"자산키={FormatDetailValue(row.AssetKey)} / 관리번호={FormatDetailValue(row.ManagementNumber)} / 시리얼={FormatDetailValue(row.MachineNumber)} / 품목={FormatDetailValue(row.ItemName)} / 제조사={FormatDetailValue(row.Manufacturer)} / 거래처={FormatDetailValue(row.CustomerName)} / 설치위치={FormatDetailValue(row.InstallLocation)} / 누락 ItemId={FormatDetailGuid(row.ItemId)} / 담당지점={FormatDetailValue(row.ResponsibleOfficeCode)} / 테넌트={FormatDetailValue(row.TenantCode)}"));

        var missingInvoiceCustomerCount = await _db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => !invoice.IsDeleted && invoice.CustomerId != Guid.Empty && !activeCustomerIds.Contains(invoice.CustomerId))
            .CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_invoice_customer_refs",
            missingInvoiceCustomerCount,
            "전표가 존재하지 않는 거래처를 참조하고 있습니다.",
            severity: "Error");

        var missingTransactionInvoiceCount = await _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction => !transaction.IsDeleted && transaction.LinkedInvoiceId.HasValue && !activeInvoiceIds.Contains(transaction.LinkedInvoiceId.Value))
            .CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_transaction_invoice_refs",
            missingTransactionInvoiceCount,
            "거래/수금 내역이 존재하지 않는 전표를 참조하고 있습니다.",
            severity: "Error");

        var missingPaymentInvoiceCount = await _db.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(payment => !payment.IsDeleted && payment.InvoiceId != Guid.Empty && !activeInvoiceIds.Contains(payment.InvoiceId))
            .CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_payment_invoice_refs",
            missingPaymentInvoiceCount,
            "수금/지급 내역이 존재하지 않는 전표를 참조하고 있습니다.",
            severity: "Error");

        var missingAttachmentTransactionCount = await _db.TransactionAttachments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(attachment => !attachment.IsDeleted && attachment.TransactionId != Guid.Empty && !activeTransactionIds.Contains(attachment.TransactionId))
            .CountAsync(ct);
        AddIssueIfNeeded(
            issues,
            "orphan_attachment_transaction_refs",
            missingAttachmentTransactionCount,
            "증빙 첨부가 존재하지 않는 거래내역을 참조하고 있습니다.",
            severity: "Error");

        var attachmentPathSnapshots = await _db.TransactionAttachments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(attachment => !attachment.IsDeleted)
            .Select(attachment => new
            {
                attachment.StoredPath,
                attachment.FileSize
            })
            .ToListAsync(ct);
        var missingAttachmentFileCount = attachmentPathSnapshots.Count(attachment =>
            attachment.FileSize > 0 &&
            (string.IsNullOrWhiteSpace(attachment.StoredPath) || !File.Exists(attachment.StoredPath)));
        AddIssueIfNeeded(
            issues,
            "missing_attachment_files",
            missingAttachmentFileCount,
            "로컬 증빙 파일이 없어 다시 동기화하거나 첨부 복구가 필요합니다.");

        return new LocalIntegrityReport(createdAtUtc, officeCode, tenantCode, dirtyCount, pendingServerMirrorRefresh, issues);
    }

    private static string ResolveIntegrityTenantCode(SessionState session)
        => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);

    private static List<string> ResolveIntegrityOfficeCodes(string tenantCode)
        => TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode)
            .Select(officeCode => NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet))
            .Where(officeCode => !string.IsNullOrWhiteSpace(officeCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IQueryable<LocalCustomer> ApplyIntegrityCustomerScope(
        IQueryable<LocalCustomer> query,
        string tenantCode,
        IReadOnlyCollection<string> officeCodes)
    {
        if (string.Equals(tenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase))
        {
            return query.Where(customer =>
                customer.TenantCode == TenantScopeCatalog.Itworld ||
                customer.OfficeCode == OfficeCodeCatalog.Itworld ||
                customer.ResponsibleOfficeCode == OfficeCodeCatalog.Itworld);
        }

        return query.Where(customer =>
            customer.TenantCode != TenantScopeCatalog.Itworld &&
            customer.OfficeCode != OfficeCodeCatalog.Itworld &&
            customer.ResponsibleOfficeCode != OfficeCodeCatalog.Itworld &&
            (
                customer.ResponsibleOfficeCode == "ALL" ||
                customer.OfficeCode == "ALL" ||
                officeCodes.Contains(customer.ResponsibleOfficeCode) ||
                officeCodes.Contains(customer.OfficeCode) ||
                customer.TenantCode == tenantCode
            ));
    }

    private static IQueryable<LocalItem> ApplyIntegrityItemScope(
        IQueryable<LocalItem> query,
        string tenantCode,
        IReadOnlyCollection<string> officeCodes)
    {
        if (string.Equals(tenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase))
        {
            return query.Where(item =>
                item.TenantCode == TenantScopeCatalog.Itworld ||
                item.OfficeCode == OfficeCodeCatalog.Itworld);
        }

        return query.Where(item =>
            item.TenantCode != TenantScopeCatalog.Itworld &&
            item.OfficeCode != OfficeCodeCatalog.Itworld &&
            (
                item.OfficeCode == "ALL" ||
                officeCodes.Contains(item.OfficeCode) ||
                item.TenantCode == tenantCode
            ));
    }

    private static IQueryable<LocalRentalBillingProfile> ApplyIntegrityRentalProfileScope(
        IQueryable<LocalRentalBillingProfile> query,
        string tenantCode,
        IReadOnlyCollection<string> officeCodes)
    {
        if (string.Equals(tenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase))
        {
            return query.Where(profile =>
                profile.TenantCode == TenantScopeCatalog.Itworld ||
                profile.OfficeCode == OfficeCodeCatalog.Itworld ||
                profile.ManagementCompanyCode == OfficeCodeCatalog.Itworld ||
                profile.ResponsibleOfficeCode == OfficeCodeCatalog.Itworld);
        }

        return query.Where(profile =>
            profile.TenantCode != TenantScopeCatalog.Itworld &&
            profile.OfficeCode != OfficeCodeCatalog.Itworld &&
            profile.ManagementCompanyCode != OfficeCodeCatalog.Itworld &&
            profile.ResponsibleOfficeCode != OfficeCodeCatalog.Itworld &&
            (
                profile.TenantCode == tenantCode ||
                officeCodes.Contains(profile.OfficeCode) ||
                officeCodes.Contains(profile.ManagementCompanyCode) ||
                officeCodes.Contains(profile.ResponsibleOfficeCode)
            ));
    }

    private static IQueryable<LocalRentalAsset> ApplyIntegrityRentalAssetScope(
        IQueryable<LocalRentalAsset> query,
        string tenantCode,
        IReadOnlyCollection<string> officeCodes)
    {
        if (string.Equals(tenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase))
        {
            return query.Where(asset =>
                asset.TenantCode == TenantScopeCatalog.Itworld ||
                asset.OfficeCode == OfficeCodeCatalog.Itworld ||
                asset.ManagementCompanyCode == OfficeCodeCatalog.Itworld ||
                asset.ResponsibleOfficeCode == OfficeCodeCatalog.Itworld);
        }

        return query.Where(asset =>
            asset.TenantCode != TenantScopeCatalog.Itworld &&
            asset.OfficeCode != OfficeCodeCatalog.Itworld &&
            asset.ManagementCompanyCode != OfficeCodeCatalog.Itworld &&
            asset.ResponsibleOfficeCode != OfficeCodeCatalog.Itworld &&
            (
                asset.TenantCode == tenantCode ||
                officeCodes.Contains(asset.OfficeCode) ||
                officeCodes.Contains(asset.ManagementCompanyCode) ||
                officeCodes.Contains(asset.ResponsibleOfficeCode)
            ));
    }

    private static void AddIssueIfNeeded(
        ICollection<LocalIntegrityIssue> issues,
        string code,
        int count,
        string message,
        string severity = "Warning",
        IReadOnlyList<string>? detailRows = null)
    {
        if (count <= 0)
            return;

        issues.Add(new LocalIntegrityIssue(code, severity, count, message, detailRows));
    }

    private static IReadOnlyList<string> BuildIntegrityDetailRows<T>(
        IReadOnlyList<T> rows,
        int totalCount,
        Func<T, string> formatter)
    {
        var details = rows
            .Take(LocalIntegrityDetailRowLimit)
            .Select(formatter)
            .Where(row => !string.IsNullOrWhiteSpace(row))
            .ToList();

        var hiddenCount = Math.Max(0, totalCount - LocalIntegrityDetailRowLimit);
        if (hiddenCount > 0)
            details.Add($"... 외 {hiddenCount:N0}건은 원본 화면 또는 서버 무결성 상세 목록에서 추가 확인하세요.");

        return details;
    }

    private static string FormatDetailValue(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "-";
    }

    private static string FormatDetailGuid(Guid? value)
        => value.HasValue ? value.Value.ToString("N") : "-";

    private static bool IsCrossTenantInventoryTransferRoute(string? fromWarehouseCode, string? toWarehouseCode)
    {
        var normalizedFromWarehouseCode = NormalizeWarehouseCode(
            fromWarehouseCode,
            DomainConstants.OfficeUsenet,
            DomainConstants.OfficeUsenet);
        var normalizedToWarehouseCode = NormalizeWarehouseCode(
            toWarehouseCode,
            DomainConstants.OfficeYeonsu,
            DomainConstants.OfficeYeonsu);
        var sourceOfficeCode = ResolveOfficeCodeFromWarehouseCode(normalizedFromWarehouseCode);
        var targetOfficeCode = ResolveOfficeCodeFromWarehouseCode(normalizedToWarehouseCode);
        var sourceTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, sourceOfficeCode);
        var targetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, targetOfficeCode);

        return !string.Equals(sourceTenantCode, targetTenantCode, StringComparison.OrdinalIgnoreCase);
    }
}
