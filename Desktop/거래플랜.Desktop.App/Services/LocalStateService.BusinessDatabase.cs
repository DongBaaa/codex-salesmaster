using System.IO;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private const string ReplacementCharacterText = "\uFFFD";

    private static readonly string[] ServerMirrorStateSettingKeys =
    [
        "LastSyncRevision",
        "Sync.LastSuccessAt",
        "Sync.LastError"
    ];

    private static readonly string[] BusinessScopedSettingKeys =
    [
        "LastSyncRevision",
        "Sync.LastSuccessAt",
        "Sync.LastError",
        "InvoiceFilter.From",
        "InvoiceFilter.To",
        "InvoiceFilter.CustomerName",
        "InvoiceFilter.VoucherType",
        "InvoiceFilter.MinAmount",
        "InvoiceFilter.MaxAmount",
        "InvoiceFavorites.Ids"
    ];

    internal async Task ClearAdministrativeBusinessCacheRevisionSettingsAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings
            .IgnoreQueryFilters()
            .Where(setting => setting.Key.StartsWith(SyncSettingKeys.AdministrativeBusinessCacheRevisionPrefix))
            .ToListAsync(ct);
        if (settings.Count > 0)
            _db.Settings.RemoveRange(settings);
    }

    public async Task<bool> HasPendingSyncChangesAsync(CancellationToken ct = default)
    {
        if (_db.ChangeTracker.Entries<ILocalSyncEntity>().Any(entry => entry.Entity.IsDirty))
            return true;

        return await _db.CompanyProfiles.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.Units.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.CustomerCategories.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.PriceGradeOptions.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.ItemPriceGrades.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.TradeTypeOptions.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.ItemCategoryOptions.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.CustomerMasters.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.Customers.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.CustomerContracts.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.Items.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.Transactions.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.TransactionAttachments.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.InventoryTransfers.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.RentalManagementCompanies.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.RentalAssets.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.RentalBillingLogs.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.Invoices.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.Payments.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.SyncOutboxEntries.AnyAsync(entry => entry.Status != "Acknowledged", ct);
    }

    public async Task<bool> HasPendingSyncChangesAsync(SessionState session, CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return await HasPendingSyncChangesAsync(ct);

        if (await CountDirtyAsync(session, ct) > 0)
            return true;

        var outboxSummary = await GetSyncOutboxSummaryAsync(session, ct);
        return outboxSummary.PendingCount > 0 || outboxSummary.FailedCount > 0;
    }

    public async Task<bool> HasVisibleBusinessCacheAsync(SessionState session, CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return false;

        if (await HasVisiblePrimaryWorkCacheAsync(session, ct))
            return true;

        if (await ApplyItemScope(_db.Items.AsNoTracking(), session).AnyAsync(ct))
            return true;

        if (await ApplyRentalCustomerScope(_db.Customers.AsNoTracking(), session).AnyAsync(ct))
            return true;

        if (await ApplyRentalItemScope(_db.Items.AsNoTracking(), session).AnyAsync(ct))
            return true;

        return false;
    }

    public async Task<bool> HasVisiblePrimaryWorkCacheAsync(SessionState session, CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return false;

        if (await ApplyCustomerScope(_db.Customers.AsNoTracking(), session).AnyAsync(ct))
            return true;

        return await ApplyInvoiceScope(_db.Invoices.AsNoTracking(), session).AnyAsync(ct);
    }

    public async Task<bool> HasLikelyCorruptedPrimaryWorkCacheAsync(SessionState session, CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return false;

        var customerQuery = ApplyCustomerScope(_db.Customers.AsNoTracking(), session)
            .Where(customer =>
                string.IsNullOrEmpty(customer.NameOriginal) ||
                string.IsNullOrEmpty(customer.TradeType) ||
                string.IsNullOrEmpty(customer.ResponsibleOfficeCode) ||
                customer.NameOriginal.Contains(ReplacementCharacterText) ||
                customer.TradeType.Contains(ReplacementCharacterText) ||
                customer.ResponsibleOfficeCode.Contains(ReplacementCharacterText));

        if (await customerQuery.AnyAsync(ct))
            return true;

        var itemQuery = ApplyItemScope(_db.Items.AsNoTracking(), session)
            .Where(item =>
                string.IsNullOrEmpty(item.NameOriginal) ||
                string.IsNullOrEmpty(item.TrackingType) ||
                item.NameOriginal.Contains(ReplacementCharacterText) ||
                item.SpecificationOriginal.Contains(ReplacementCharacterText) ||
                item.TrackingType.Contains(ReplacementCharacterText));

        if (await itemQuery.AnyAsync(ct))
            return true;

        return await ApplyInvoiceScope(_db.Invoices.AsNoTracking(), session)
            .AnyAsync(invoice =>
                invoice.VoucherType != VoucherType.Sales &&
                invoice.VoucherType != VoucherType.Purchase &&
                invoice.VoucherType != VoucherType.Procurement &&
                invoice.VoucherType != VoucherType.Expense &&
                invoice.VoucherType != VoucherType.Collection,
                ct);
    }

    public async Task ResetSharedMirrorCacheAsync(CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "외부 DB 트랜잭션에서 공유 미러를 초기화하려면 동일 범위의 첨부파일 저널을 전달해야 합니다.");
        }

        await AttachmentFileJournal.RecoverIncompleteJournalsAsync(
            _db,
            AppPaths.AttachmentFileJournalDir,
            AppPaths.AttachmentsDir,
            ct);
        await using var transaction = await _db.BeginRuntimeMutationTransactionAsync(ct);
        using var attachmentFiles = new AttachmentFileJournal(
            AppPaths.AttachmentFileJournalDir,
            AppPaths.AttachmentsDir);
        var commitAttempted = false;

        try
        {
            await ResetSharedMirrorCacheCoreAsync(ct, attachmentFiles);
            await attachmentFiles.StageCommitEvidenceAsync(_db, ct);
            attachmentFiles.Promote();
            commitAttempted = true;
            await transaction.CommitAsync(ct);
            await transaction.DisposeAsync().ConfigureAwait(false);
            await attachmentFiles.CompleteAfterDatabaseCommitAsync(
                _db,
                CancellationToken.None);
        }
        catch
        {
            var commitResolution = AttachmentCommitResolution.RolledBack;
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                AppLogger.Error(
                    "ATTACHMENT",
                    "공유 미러 초기화 커밋 실패 후 DB 롤백 결과를 확정하지 못했습니다.",
                    rollbackException);
            }
            finally
            {
                if (!commitAttempted)
                {
                    attachmentFiles.Rollback();
                }
                else
                {
                    commitResolution = await attachmentFiles.ResolveCommitAmbiguityAsync(
                        _db,
                        CancellationToken.None);
                }

                _db.ChangeTracker.Clear();
            }

            if (commitResolution == AttachmentCommitResolution.Committed)
                return;

            throw;
        }
    }

    internal Task ResetSharedMirrorCacheWithAttachmentJournalAsync(
        AttachmentFileJournal attachmentFileJournal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachmentFileJournal);
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "공유 미러의 외부 첨부파일 저널은 동일 범위의 DB 트랜잭션과 함께 사용해야 합니다.");
        }

        return ResetSharedMirrorCacheCoreAsync(ct, attachmentFileJournal);
    }

    private async Task ResetSharedMirrorCacheCoreAsync(
        CancellationToken ct,
        AttachmentFileJournal attachmentFileJournal)
    {
        _db.ChangeTracker.Clear();
        _officeAccess.ClearSessionAccess(_session);

        var attachmentPaths = await _db.TransactionAttachments.IgnoreQueryFilters()
            .Where(current => !string.IsNullOrWhiteSpace(current.StoredPath))
            .Select(current => current.StoredPath)
            .ToListAsync(ct);
        attachmentPaths.AddRange(await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .Where(current => !string.IsNullOrWhiteSpace(current.ReceiveEvidencePath))
            .Select(current => current.ReceiveEvidencePath)
            .ToListAsync(ct));
        var conflictOwnedEvidencePaths = (await _db
                .InventoryTransferTombstoneConflicts
                .AsNoTracking()
                .Where(current =>
                    current.ArchivedReceiveEvidencePath != string.Empty)
                .Select(current => current.ArchivedReceiveEvidencePath)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var attachmentPath in attachmentPaths)
        {
            if (conflictOwnedEvidencePaths.Contains(attachmentPath))
                continue;

            try
            {
                attachmentFileJournal.StageDelete(attachmentPath);
            }
            catch (AttachmentFileJournalContentionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "ATTACHMENT",
                    $"허용된 첨부파일 저장 경로 밖의 미러 파일은 삭제하지 않습니다. {ex.Message}");
            }
        }

        await _db.Payments.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.InvoiceLineSerials.ExecuteDeleteAsync(ct);
        await _db.InventoryMovements.ExecuteDeleteAsync(ct);
        await _db.CostAllocations.ExecuteDeleteAsync(ct);
        await _db.StockLayers.ExecuteDeleteAsync(ct);
        await _db.SerialLedgers.ExecuteDeleteAsync(ct);
        await _db.InventoryTransferLines.ExecuteDeleteAsync(ct);
        await _db.InventoryTransfers.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.TransactionAttachments.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Transactions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CustomerContracts.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.InvoiceLines.ExecuteDeleteAsync(ct);
        await _db.Invoices.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.ItemPriceGrades.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.ItemWarehouseStocks.ExecuteDeleteAsync(ct);
        await _db.RentalBillingLogs.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RentalAssets.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RentalBillingProfiles.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RentalManagementCompanies.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Customers.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CustomerMasters.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Items.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CompanyProfiles.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Units.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CustomerCategories.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.PriceGradeOptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.TradeTypeOptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.ItemCategoryOptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);

        foreach (var key in ServerMirrorStateSettingKeys)
        {
            var setting = await _db.Settings.FindAsync([key], ct);
            if (setting is not null)
                _db.Settings.Remove(setting);
        }

        await ClearAdministrativeBusinessCacheRevisionSettingsAsync(ct);

        await _db.SaveChangesAsync(ct);

        // A full mirror refresh owns an outer DB transaction. Its old files stay
        // untouched here so a later pull/commit failure can roll the DB back
        // without losing the attachments referenced by the restored rows.

        _db.ChangeTracker.Clear();
    }

    internal async Task ResetBusinessDataCacheWithAttachmentJournalAsync(
        AttachmentFileJournal attachmentFileJournal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachmentFileJournal);
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "업체 DB 캐시를 교체하려면 동일 범위의 DB 트랜잭션과 첨부파일 저널이 필요합니다.");
        }

        await ResetSharedMirrorCacheCoreAsync(ct, attachmentFileJournal);

        await _db.Offices.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Warehouses.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RecentSelections.ExecuteDeleteAsync(ct);
        await _db.AttachmentSelections.ExecuteDeleteAsync(ct);
        await _db.AuditLogs.ExecuteDeleteAsync(ct);

        foreach (var key in BusinessScopedSettingKeys)
        {
            var setting = await _db.Settings.FindAsync([key], ct);
            if (setting is not null)
                _db.Settings.Remove(setting);
        }

        await ClearAdministrativeBusinessCacheRevisionSettingsAsync(ct);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task ResetBusinessDataCacheAsync(SessionState session, CancellationToken ct = default)
    {
        _officeAccess.ClearSessionAccess(session);

        await _db.TransactionAttachments.ExecuteDeleteAsync(ct);
        await _db.Transactions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CustomerContracts.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Payments.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.InvoiceLineSerials.ExecuteDeleteAsync(ct);
        await _db.InventoryMovements.ExecuteDeleteAsync(ct);
        await _db.CostAllocations.ExecuteDeleteAsync(ct);
        await _db.StockLayers.ExecuteDeleteAsync(ct);
        await _db.SerialLedgers.ExecuteDeleteAsync(ct);
        await _db.InventoryTransferLines.ExecuteDeleteAsync(ct);
        await _db.InventoryTransfers.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.InvoiceLines.ExecuteDeleteAsync(ct);
        await _db.Invoices.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.ItemPriceGrades.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.ItemWarehouseStocks.ExecuteDeleteAsync(ct);
        await _db.RentalBillingLogs.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RentalAssets.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RentalBillingProfiles.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RentalManagementCompanies.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Customers.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CustomerMasters.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Items.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CompanyProfiles.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Units.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CustomerCategories.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.PriceGradeOptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.TradeTypeOptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.ItemCategoryOptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Offices.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Warehouses.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RecentSelections.ExecuteDeleteAsync(ct);
        await _db.AttachmentSelections.ExecuteDeleteAsync(ct);
        await _db.AuditLogs.ExecuteDeleteAsync(ct);

        foreach (var key in BusinessScopedSettingKeys)
        {
            var setting = await _db.Settings.FindAsync([key], ct);
            if (setting is not null)
                _db.Settings.Remove(setting);
        }

        await ClearAdministrativeBusinessCacheRevisionSettingsAsync(ct);

        await _db.SaveChangesAsync(ct);
    }
}
