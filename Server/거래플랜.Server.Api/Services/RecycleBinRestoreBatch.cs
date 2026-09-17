using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

// Receipts live only for one HTTP request and only cover changes committed by that request.
internal sealed class RecycleBinRestoreBatch(AppDbContext db)
{
    private sealed record Receipt(TrackedEntity Entity, long BeforeRevision, long Revision, DateTime UpdatedAtUtc);
    private readonly Dictionary<(Guid Id, string Kind), Receipt> _completed = new();

    // This acknowledges the committed effect, not the entity's state after later concurrent edits.
    public List<RecycleBinCommittedRestoreDto> GetCommittedRestores() => _completed
        .Select(pair => new RecycleBinCommittedRestoreDto
        {
            EntityId = pair.Key.Id,
            Kind = pair.Key.Kind,
            PreviousRevision = pair.Value.BeforeRevision,
            Revision = pair.Value.Revision
        }).ToList();

    public async Task<(bool Success, string Message)> ExecuteAsync(
        RecycleBinMutationTargetDto target,
        string normalizedKind,
        Func<Task<(bool Success, string Message)>> mutation,
        CancellationToken ct)
    {
        if (_completed.TryGetValue((target.EntityId, normalizedKind), out var completed) &&
            target.ExpectedRevision > 0 && target.ExpectedRevision == completed.BeforeRevision)
        {
            var current = await db.Entry(completed.Entity).GetDatabaseValuesAsync(ct);
            if (current is not null &&
                !current.GetValue<bool>(nameof(TrackedEntity.IsDeleted)) &&
                current.GetValue<long>(nameof(TrackedEntity.Revision)) == completed.Revision &&
                current.GetValue<DateTime>(nameof(TrackedEntity.UpdatedAtUtc)) == completed.UpdatedAtUtc)
            {
                return (true, "이번 요청에서 연결 항목과 함께 이미 복원했습니다.");
            }

            return (false, "이번 요청의 연쇄 복원 이후 항목이 다시 변경되었습니다. 새로고침 후 확인하세요.");
        }

        var pendingBeforeOperation = db.ChangeTracker.Entries<TrackedEntity>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity)
            .ToHashSet();
        var candidates = new Dictionary<TrackedEntity, long>();
        var saved = new Dictionary<(Guid Id, string Kind), Receipt>();
        void CaptureSaving(object? sender, SavingChangesEventArgs args)
        {
            foreach (var entry in db.ChangeTracker.Entries<TrackedEntity>())
            {
                if (entry.State != EntityState.Modified || pendingBeforeOperation.Contains(entry.Entity) ||
                    entry.Entity.IsDeleted || !entry.OriginalValues.GetValue<bool>(nameof(TrackedEntity.IsDeleted)))
                    continue;
                candidates.TryAdd(entry.Entity, entry.OriginalValues.GetValue<long>(nameof(TrackedEntity.Revision)));
            }
        }
        void CaptureSaved(object? sender, SavedChangesEventArgs args)
        {
            foreach (var (entity, beforeRevision) in candidates)
            {
                if (!entity.IsDeleted && beforeRevision > 0 && KindOf(entity) is { } kind)
                    saved[(entity.Id, kind)] = new Receipt(entity, beforeRevision, entity.Revision, entity.UpdatedAtUtc);
            }
        }

        db.SavingChanges += CaptureSaving;
        db.SavedChanges += CaptureSaved;
        try
        {
            var result = await mutation();
            // The mutation includes its transaction commit; a failed/rolled-back operation contributes no receipt.
            if (result.Success)
                foreach (var (key, receipt) in saved)
                    _completed[key] = receipt;
            return result;
        }
        finally
        {
            db.SavingChanges -= CaptureSaving;
            db.SavedChanges -= CaptureSaved;
        }
    }

    private static string? KindOf(TrackedEntity entity) => entity switch
    {
        Customer => "customer", CustomerContract => "contract", Item => "item",
        CompanyProfile => "company-profile", CustomerCategory => "customer-category",
        PriceGradeOption => "price-grade-option", TradeTypeOption => "trade-type-option",
        ItemCategoryOption => "item-category-option", Invoice => "invoice", Payment => "payment",
        TransactionRecord => "transaction", InventoryTransfer => "inventory-transfer",
        RentalManagementCompany => "rental-management-company", RentalBillingProfile => "rental-billing-profile",
        RentalAsset => "rental-asset", RentalBillingLog => "rental-billing-log", _ => null
    };
}
