using System.Text.Json;
using System.IO;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed record InventoryTransferTombstoneConflictDraft(
    Guid TransferId,
    LocalInventoryTransfer LocalDraft,
    long ServerRevision,
    DateTime ServerUpdatedAtUtc,
    DateTime DetectedAtUtc);

internal static class InventoryTransferTombstoneConflictPolicy
{
    internal const string UnresolvedStatus = "Unresolved";
    internal const string ResolvedStatus = "Resolved";
    internal const string DiscardedResolution = "Discarded";
    internal const string RecoveredAsNewResolution = "RecoveredAsNew";
    internal const string ServerRestoredPendingDecisionResolution =
        "ServerRestoredPendingDecision";
    internal const string OutboxErrorPrefix =
        "[inventory-transfer-remote-tombstone-conflict]";
}

public sealed partial class LocalStateService
{
    public async Task<IReadOnlyList<InventoryTransferTombstoneConflictDraft>>
        GetInventoryTransferTombstoneConflictDraftsAsync(
            SessionState session,
            DateOnly? from = null,
            DateOnly? to = null,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var businessDatabaseName =
            ResolveInventoryTransferConflictBusinessDatabaseName(session);
        var records = await _db.InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .Where(record =>
                record.BusinessDatabaseName == businessDatabaseName &&
                record.Status ==
                InventoryTransferTombstoneConflictPolicy.UnresolvedStatus)
            .OrderByDescending(record => record.UpdatedAtUtc)
            .ToListAsync(ct);
        var result =
            new List<InventoryTransferTombstoneConflictDraft>(records.Count);

        foreach (var record in records)
        {
            if (!CanReadInventoryTransferTombstoneConflict(record, session) ||
                !TryReadInventoryTransferTombstoneConflictDraft(
                    record,
                    out var draft))
            {
                continue;
            }

            if (from.HasValue && draft.TransferDate < from.Value)
                continue;
            if (to.HasValue && draft.TransferDate > to.Value)
                continue;

            result.Add(
                new InventoryTransferTombstoneConflictDraft(
                    record.TransferId,
                    draft,
                    record.ServerRevision,
                    record.ServerUpdatedAtUtc,
                    record.DetectedAtUtc));
        }

        return result;
    }

    public async Task<InventoryTransferTombstoneConflictDraft?>
        GetInventoryTransferTombstoneConflictDraftAsync(
            Guid transferId,
            SessionState session,
            CancellationToken ct = default)
    {
        if (transferId == Guid.Empty)
            return null;

        var businessDatabaseName =
            ResolveInventoryTransferConflictBusinessDatabaseName(session);
        var record = await _db.InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .FirstOrDefaultAsync(
                current =>
                    current.TransferId == transferId &&
                    current.BusinessDatabaseName == businessDatabaseName &&
                    current.Status ==
                    InventoryTransferTombstoneConflictPolicy.UnresolvedStatus,
                ct);
        if (record is null ||
            !CanReadInventoryTransferTombstoneConflict(record, session) ||
            !TryReadInventoryTransferTombstoneConflictDraft(
                record,
                out var draft))
        {
            return null;
        }

        return new InventoryTransferTombstoneConflictDraft(
            record.TransferId,
            draft,
            record.ServerRevision,
            record.ServerUpdatedAtUtc,
            record.DetectedAtUtc);
    }

    public async Task<bool> ResolveInventoryTransferTombstoneConflictAsync(
        Guid transferId,
        string resolution,
        SessionState session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (transferId == Guid.Empty)
            return false;

        var normalizedResolution = string.IsNullOrWhiteSpace(resolution)
            ? InventoryTransferTombstoneConflictPolicy.DiscardedResolution
            : resolution.Trim();
        if (!string.Equals(
                normalizedResolution,
                InventoryTransferTombstoneConflictPolicy
                    .DiscardedResolution,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedResolution =
            InventoryTransferTombstoneConflictPolicy
                .DiscardedResolution;
        if (_db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Discarding an inventory-transfer conflict requires ownership of its attachment transaction.");
        }

        await AttachmentFileJournal.RecoverIncompleteJournalsAsync(
            _db,
            AppPaths.AttachmentFileJournalDir,
            AppPaths.AttachmentsDir,
            ct);
        var businessDatabaseName =
            ResolveInventoryTransferConflictBusinessDatabaseName(session);
        await using var transaction =
            await _db.BeginRuntimeMutationTransactionAsync(ct);
        using var attachmentFiles = new AttachmentFileJournal(
            AppPaths.AttachmentFileJournalDir,
            AppPaths.AttachmentsDir);
        var commitAttempted = false;
        var resolved = false;
        try
        {
            var record = await _db.InventoryTransferTombstoneConflicts
                .FirstOrDefaultAsync(
                    current =>
                        current.TransferId == transferId &&
                        current.BusinessDatabaseName == businessDatabaseName &&
                        current.Status ==
                        InventoryTransferTombstoneConflictPolicy.UnresolvedStatus,
                    ct);
            if (record is null ||
                !CanResolveInventoryTransferTombstoneConflict(record, session))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(
                    record.ArchivedReceiveEvidencePath))
            {
                if (!AppPaths.IsTransactionAttachmentPath(
                        record.ArchivedReceiveEvidencePath))
                {
                    AppLogger.Warn(
                        "ATTACHMENT",
                        $"Refused to discard an inventory-transfer conflict whose archived evidence path is outside the transaction attachment root. transfer={record.TransferId:D}");
                    return false;
                }

                attachmentFiles.StageDelete(
                    record.ArchivedReceiveEvidencePath);
                record.ArchivedReceiveEvidencePath = string.Empty;
            }

            var now = DateTime.UtcNow;
            record.Status =
                InventoryTransferTombstoneConflictPolicy.ResolvedStatus;
            record.Resolution = normalizedResolution;
            record.ResolvedAtUtc = now;
            record.UpdatedAtUtc = now;

            if (!await TryAcknowledgeCapturedInventoryTransferConflictOutboxAsync(
                    record,
                    now,
                    ct))
            {
                await transaction.RollbackAsync(
                    CancellationToken.None);
                attachmentFiles.Rollback();
                _db.ChangeTracker.Clear();
                return false;
            }

            await _db.SaveChangesAsync(ct);
            await attachmentFiles.StageCommitEvidenceAsync(_db, ct);
            attachmentFiles.Promote();
            commitAttempted = true;
            await transaction.CommitAsync(ct);
            await transaction.DisposeAsync().ConfigureAwait(false);
            await attachmentFiles.CompleteAfterDatabaseCommitAsync(
                _db,
                CancellationToken.None);
            resolved = true;
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
                    "Could not establish the database rollback outcome after inventory-transfer conflict discard failed.",
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
                    commitResolution =
                        await attachmentFiles.ResolveCommitAmbiguityAsync(
                            _db,
                            CancellationToken.None);
                }

                _db.ChangeTracker.Clear();
            }

            if (commitResolution == AttachmentCommitResolution.Committed)
            {
                resolved = true;
            }
            else
            {
                throw;
            }
        }

        _db.ChangeTracker.Clear();
        if (resolved)
            RaiseInventoryStateChanged();
        return resolved;
    }

    public async Task<OfficeMutationResult>
        RecoverInventoryTransferTombstoneConflictAsNewAsync(
            Guid conflictTransferId,
            SessionState session,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (conflictTransferId == Guid.Empty)
        {
            return OfficeMutationResult.Missing(
                "복구할 원격삭제 충돌 초안을 찾을 수 없습니다.");
        }

        var businessDatabaseName =
            ResolveInventoryTransferConflictBusinessDatabaseName(session);
        var record = await _db.InventoryTransferTombstoneConflicts
            .FirstOrDefaultAsync(
                current =>
                    current.TransferId == conflictTransferId &&
                    current.BusinessDatabaseName == businessDatabaseName,
                ct);
        if (record is null)
        {
            return OfficeMutationResult.Missing(
                "복구할 원격삭제 충돌 초안을 찾을 수 없습니다.");
        }

        if (!CanResolveInventoryTransferTombstoneConflict(record, session))
        {
            return OfficeMutationResult.Denied(
                "출발지 담당자 또는 관리자만 원격삭제 충돌 초안을 새 문서로 복구할 수 있습니다.");
        }

        if (!string.IsNullOrWhiteSpace(
                record.ArchivedReceiveEvidencePath) &&
            (!AppPaths.IsTransactionAttachmentPath(
                 record.ArchivedReceiveEvidencePath) ||
             !File.Exists(record.ArchivedReceiveEvidencePath)))
        {
            return OfficeMutationResult.Conflict(
                "The conflict-owned receive evidence file is missing or outside the transaction attachment root. Discard or repair the conflict before recovery.");
        }

        if (string.Equals(
                record.Status,
                InventoryTransferTombstoneConflictPolicy.ResolvedStatus,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                record.Resolution,
                InventoryTransferTombstoneConflictPolicy
                    .RecoveredAsNewResolution,
                StringComparison.OrdinalIgnoreCase) &&
            record.RecoveredTransferId is Guid recoveredTransferId &&
            recoveredTransferId != Guid.Empty)
        {
            var recoveredAlreadyExists = await _db.InventoryTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(
                    transfer =>
                        transfer.Id == recoveredTransferId &&
                        !transfer.IsDeleted,
                    ct);
            if (!recoveredAlreadyExists)
            {
                return OfficeMutationResult.Conflict(
                    "충돌 초안의 복구 완료 기록과 새 문서가 일치하지 않습니다. 데이터 복구 점검이 필요합니다.");
            }

            if (!await
                    TryAcknowledgeCapturedInventoryTransferConflictOutboxAsync(
                        record,
                        DateTime.UtcNow,
                        ct))
            {
                return OfficeMutationResult.Conflict(
                    "복구된 문서는 확인했지만 원래 전송 대기 기록의 범위가 달라 자동 정리하지 않았습니다. 동기화 점검이 필요합니다.");
            }

            return OfficeMutationResult.Ok(
                recoveredTransferId,
                "이미 새 재고이동 문서로 복구된 초안입니다.");
        }

        if (!string.Equals(
                record.Status,
                InventoryTransferTombstoneConflictPolicy.UnresolvedStatus,
                StringComparison.OrdinalIgnoreCase))
        {
            return OfficeMutationResult.Conflict(
                "이미 처리된 원격삭제 충돌 초안입니다. 목록을 다시 불러오세요.");
        }

        if (!TryReadInventoryTransferTombstoneConflictDraft(
                record,
                out var recoveredTransfer))
        {
            return OfficeMutationResult.Conflict(
                "보관된 원격삭제 충돌 초안을 읽을 수 없습니다. 데이터 복구 점검이 필요합니다.");
        }

        var newTransferId =
            record.RecoveredTransferId is Guid reservedId &&
            reservedId != Guid.Empty &&
            reservedId != conflictTransferId
                ? reservedId
                : Guid.NewGuid();
        recoveredTransfer.Id = newTransferId;
        recoveredTransfer.Revision = 0;
        recoveredTransfer.TransferNumber = string.Empty;
        recoveredTransfer.TransferStatus = "수령대기";
        recoveredTransfer.ReceiveMemo = string.Empty;
        recoveredTransfer.RejectReason = string.Empty;
        recoveredTransfer.IsDeleted = false;
        recoveredTransfer.IsDirty = true;
        recoveredTransfer.CreatedAtUtc = DateTime.UtcNow;
        recoveredTransfer.UpdatedAtUtc = recoveredTransfer.CreatedAtUtc;
        foreach (var line in recoveredTransfer.Lines)
        {
            line.Id = Guid.NewGuid();
            line.TransferId = newTransferId;
            line.IsDeleted = false;
        }

        await using var transaction =
            await _db.BeginRuntimeMutationTransactionAsync(ct);
        using var inventoryStateChangeCapture =
            CaptureInventoryStateChanges();
        try
        {
            var reservedAtUtc = DateTime.UtcNow;
            var reservationUpdated = await _db
                .InventoryTransferTombstoneConflicts
                .Where(current =>
                    current.TransferId == conflictTransferId &&
                    current.BusinessDatabaseName ==
                    businessDatabaseName &&
                    current.Status ==
                    InventoryTransferTombstoneConflictPolicy
                        .UnresolvedStatus &&
                    (current.RecoveredTransferId == null ||
                     current.RecoveredTransferId == newTransferId))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            current => current.RecoveredTransferId,
                            (Guid?)newTransferId)
                        .SetProperty(
                            current => current.UpdatedAtUtc,
                            reservedAtUtc),
                    ct);
            if (reservationUpdated != 1)
            {
                await transaction.RollbackAsync(
                    CancellationToken.None);
                _db.ChangeTracker.Clear();
                return OfficeMutationResult.Conflict(
                    "다른 작업에서 충돌 초안을 먼저 처리했습니다. 목록을 다시 불러오세요.");
            }

            var recoveredAlreadyExists = await _db.InventoryTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(
                    transfer =>
                        transfer.Id == newTransferId &&
                        !transfer.IsDeleted,
                    ct);
            OfficeMutationResult result;
            if (recoveredAlreadyExists)
            {
                result = OfficeMutationResult.Ok(
                    newTransferId,
                    "이미 저장된 복구 문서의 충돌 처리를 마무리했습니다.");
            }
            else
            {
                result = await SaveInventoryTransferAsync(
                    recoveredTransfer,
                    session,
                    ct);
            }
            if (!result.Success)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                return result;
            }

            if (result.EntityId != newTransferId)
            {
                throw new InvalidOperationException(
                    "원격삭제 충돌 초안의 예약 복구 ID와 저장된 재고이동 ID가 일치하지 않습니다.");
            }

            var now = DateTime.UtcNow;
            if (!await
                    TryAcknowledgeCapturedInventoryTransferConflictOutboxAsync(
                        record,
                        now,
                        ct))
            {
                await transaction.RollbackAsync(
                    CancellationToken.None);
                _db.ChangeTracker.Clear();
                return OfficeMutationResult.Conflict(
                    "원래 전송 대기 기록의 범위가 달라 새 문서 복구를 완료하지 않았습니다. 원본 초안은 그대로 보관했습니다.");
            }

            var resolutionUpdated = await _db
                .InventoryTransferTombstoneConflicts
                .Where(current =>
                    current.TransferId == conflictTransferId &&
                    current.BusinessDatabaseName ==
                    businessDatabaseName &&
                    current.Status ==
                    InventoryTransferTombstoneConflictPolicy
                        .UnresolvedStatus &&
                    current.RecoveredTransferId == newTransferId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            current => current.Status,
                            InventoryTransferTombstoneConflictPolicy
                                .ResolvedStatus)
                        .SetProperty(
                            current => current.Resolution,
                            InventoryTransferTombstoneConflictPolicy
                                .RecoveredAsNewResolution)
                        .SetProperty(
                            current => current.ResolvedAtUtc,
                            (DateTime?)now)
                        .SetProperty(
                            current => current.UpdatedAtUtc,
                            now),
                    ct);
            if (resolutionUpdated != 1)
            {
                throw new DbUpdateConcurrencyException(
                    "원격삭제 충돌 초안의 복구 완료 상태를 저장하지 못했습니다.");
            }

            record.Status =
                InventoryTransferTombstoneConflictPolicy.ResolvedStatus;
            record.Resolution =
                InventoryTransferTombstoneConflictPolicy
                    .RecoveredAsNewResolution;
            record.ResolvedAtUtc = now;
            record.UpdatedAtUtc = now;
            record.RecoveredTransferId = newTransferId;
            await transaction.CommitAsync(ct);

            _db.ChangeTracker.Clear();
            inventoryStateChangeCapture.Dispose();
            if (inventoryStateChangeCapture.HasChanges)
                TryPublishInventoryStateChanged();

            return OfficeMutationResult.Ok(
                newTransferId,
                "원격에서 삭제된 초안을 새 재고이동 문서로 복구해 저장했습니다.");
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }

            throw;
        }
    }

    internal async Task<bool>
        RecordInventoryTransferTombstoneConflictServerStateAsync(
            Guid transferId,
            string businessDatabaseName,
            long serverRevision,
            DateTime serverUpdatedAtUtc,
            bool serverIsDeleted,
            string serverSnapshotJson,
            CancellationToken ct)
    {
        var normalizedBusinessDatabaseName =
            TenantScopeCatalog.GetDatabaseName(businessDatabaseName);
        var record = await _db.InventoryTransferTombstoneConflicts
            .FirstOrDefaultAsync(
                current =>
                    current.TransferId == transferId &&
                    current.BusinessDatabaseName ==
                    normalizedBusinessDatabaseName &&
                    current.Status ==
                    InventoryTransferTombstoneConflictPolicy
                        .UnresolvedStatus,
                ct);
        if (record is null)
            return false;

        var now = DateTime.UtcNow;
        record.Resolution = serverIsDeleted
            ? string.Empty
            : InventoryTransferTombstoneConflictPolicy
                .ServerRestoredPendingDecisionResolution;
        record.ServerRevision = serverRevision;
        record.ServerUpdatedAtUtc = serverUpdatedAtUtc;
        if (!string.IsNullOrWhiteSpace(serverSnapshotJson))
        {
            record.ServerTombstoneJson =
                serverSnapshotJson;
        }
        record.ResolvedAtUtc = null;
        record.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool>
        TryAcknowledgeCapturedInventoryTransferConflictOutboxAsync(
            LocalInventoryTransferTombstoneConflict record,
            DateTime acknowledgedAtUtc,
            CancellationToken ct)
    {
        if (!TryReadInventoryTransferTombstoneConflictMutationIds(
                record,
                out var capturedMutationIds))
        {
            return false;
        }

        if (capturedMutationIds.Count == 0)
            return true;

        var outboxEntityNames = new[]
        {
            nameof(LocalInventoryTransfer),
            "InventoryTransfer"
        };
        var allowLegacyDefaultDatabaseName =
            string.Equals(
                record.BusinessDatabaseName,
                TenantScopeCatalog.GetDatabaseName(record.TenantCode),
                StringComparison.OrdinalIgnoreCase);
        var capturedOutboxRows = await _db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry =>
                capturedMutationIds.Contains(entry.MutationId))
            .ToListAsync(ct);
        var unresolvedCapturedRows = capturedOutboxRows
            .Where(entry =>
                !string.Equals(
                    entry.Status,
                    "Acknowledged",
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        var matchingOutboxIds = unresolvedCapturedRows
            .Where(entry =>
                entry.EntityId == record.TransferId &&
                outboxEntityNames.Contains(entry.EntityName) &&
                (string.Equals(
                     entry.BusinessDatabaseName,
                     record.BusinessDatabaseName,
                     StringComparison.OrdinalIgnoreCase) ||
                 (allowLegacyDefaultDatabaseName &&
                  string.IsNullOrWhiteSpace(
                      entry.BusinessDatabaseName))) &&
                (string.Equals(
                     entry.TenantCode,
                     record.TenantCode,
                     StringComparison.OrdinalIgnoreCase) ||
                 string.IsNullOrWhiteSpace(entry.TenantCode)) &&
                (string.Equals(
                     entry.OfficeCode,
                     record.SourceOfficeCode,
                     StringComparison.OrdinalIgnoreCase) ||
                 string.IsNullOrWhiteSpace(entry.OfficeCode)) &&
                (string.Equals(
                     entry.ResponsibleOfficeCode,
                     record.TargetOfficeCode,
                     StringComparison.OrdinalIgnoreCase) ||
                 string.IsNullOrWhiteSpace(
                     entry.ResponsibleOfficeCode)) &&
                (entry.ErrorMessage ?? string.Empty).StartsWith(
                    InventoryTransferTombstoneConflictPolicy
                        .OutboxErrorPrefix,
                    StringComparison.Ordinal))
            .Select(entry => entry.Id)
            .ToHashSet();
        if (matchingOutboxIds.Count !=
            unresolvedCapturedRows.Count)
        {
            AppLogger.Warn(
                "SYNC",
                $"재고이동 원격삭제 충돌의 캡처 outbox 범위가 달라 승인 처리를 중단합니다: transfer={record.TransferId:D}, captured={unresolvedCapturedRows.Count}, matched={matchingOutboxIds.Count}");
            return false;
        }

        if (matchingOutboxIds.Count == 0)
            return true;

        await _db.SyncOutboxEntries
            .Where(entry => matchingOutboxIds.Contains(entry.Id))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(entry => entry.Status, "Acknowledged")
                    .SetProperty(
                        entry => entry.AcknowledgedAtUtc,
                        (DateTime?)acknowledgedAtUtc)
                    .SetProperty(
                        entry => entry.AcceptedRevision,
                        record.ServerRevision)
                    .SetProperty(
                        entry => entry.AcceptedUpdatedAtUtc,
                        (DateTime?)record.ServerUpdatedAtUtc)
                    .SetProperty(
                        entry => entry.ErrorMessage,
                        string.Empty),
                ct);
        return true;
    }

    private static bool
        TryReadInventoryTransferTombstoneConflictMutationIds(
            LocalInventoryTransferTombstoneConflict record,
            out HashSet<string> mutationIds)
    {
        mutationIds = [];
        try
        {
            mutationIds = (JsonSerializer.Deserialize<List<string>>(
                               record.OutboxMutationIdsJson) ??
                           [])
                .Where(mutationId =>
                    !string.IsNullOrWhiteSpace(mutationId))
                .Select(mutationId => mutationId.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch (JsonException ex)
        {
            AppLogger.Warn(
                "SYNC",
                $"재고이동 원격삭제 충돌의 outbox 목록을 읽지 못해 승인 처리를 건너뜁니다: transfer={record.TransferId:D}, detail={ex.Message}");
            return false;
        }
    }

    private bool CanResolveInventoryTransferTombstoneConflict(
        LocalInventoryTransferTombstoneConflict record,
        SessionState session)
        => CanReadInventoryTransferTombstoneConflict(record, session) &&
           CanEditDeliveries(session) &&
           CanWriteOfficeScope(session, record.SourceOfficeCode);

    private bool CanReadInventoryTransferTombstoneConflict(
        LocalInventoryTransferTombstoneConflict record,
        SessionState session)
    {
        var tenantWarehouseCodes = GetTenantWarehouseCodes(session);
        var sourceWarehouseCode =
            OfficeCodeCatalog.GetMainWarehouseCode(record.SourceOfficeCode);
        var targetWarehouseCode =
            OfficeCodeCatalog.GetMainWarehouseCode(record.TargetOfficeCode);
        if (!tenantWarehouseCodes.Contains(
                sourceWarehouseCode,
                StringComparer.OrdinalIgnoreCase) ||
            !tenantWarehouseCodes.Contains(
                targetWarehouseCode,
                StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (HasFullAccess(session) || CanViewAllDeliveryScope(session))
            return true;

        var readableOfficeCodes = GetReadableOfficeCodes(session);
        return readableOfficeCodes.Contains(
                   record.SourceOfficeCode,
                   StringComparer.OrdinalIgnoreCase) ||
               readableOfficeCodes.Contains(
                   record.TargetOfficeCode,
                   StringComparer.OrdinalIgnoreCase);
    }

    private static string
        ResolveInventoryTransferConflictBusinessDatabaseName(
            SessionState session)
        => TenantScopeCatalog.GetDatabaseName(
            session.SelectedBusinessDatabaseName);

    private static bool TryReadInventoryTransferTombstoneConflictDraft(
        LocalInventoryTransferTombstoneConflict record,
        out LocalInventoryTransfer draft)
    {
        draft = new LocalInventoryTransfer();
        try
        {
            var snapshot = JsonSerializer.Deserialize<InventoryTransferDto>(
                record.LocalSnapshotJson);
            if (snapshot is null ||
                snapshot.Id == Guid.Empty ||
                snapshot.Id != record.TransferId)
            {
                return false;
            }

            draft = LocalMappings.ToLocal(snapshot);
            draft.IsDeleted = false;
            draft.IsDirty = true;
            return true;
        }
        catch (JsonException ex)
        {
            AppLogger.Warn(
                "SYNC",
                $"재고이동 원격삭제 충돌 초안을 읽지 못했습니다: transfer={record.TransferId:D}, detail={ex.Message}");
            return false;
        }
    }
}
