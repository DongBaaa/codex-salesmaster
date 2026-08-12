using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Utilities;

public static class ProcessedSyncMutationRecorder
{
    public const string DirectApiDeviceId = "direct-api";

    public static async Task<DirectMutationCheck> CheckAsync(
        AppDbContext dbContext,
        SyncEntityDto dto,
        string entityName,
        CancellationToken cancellationToken,
        string deviceId = DirectApiDeviceId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);

        var mutationId = NormalizeMutationId(dto.MutationId);
        if (string.IsNullOrWhiteSpace(mutationId))
            return DirectMutationCheck.NotTracked;

        if (ItemWarehouseStockMutationReceipt
            .IsReservedMutationId(mutationId))
        {
            return new DirectMutationCheck(
                DirectMutationStatus.Conflict,
                mutationId,
                string.Empty,
                entityName,
                dto.Id,
                dto.ExpectedRevision,
                NormalizeDeviceId(deviceId),
                NormalizeUtc(dto.MutationCreatedAtUtc),
                null,
                "Mutation id uses a server-reserved receipt namespace.");
        }

        var existing = dbContext.ProcessedSyncMutations.Local.FirstOrDefault(entity =>
                           string.Equals(
                               NormalizeMutationId(entity.MutationId),
                               mutationId,
                               StringComparison.Ordinal)) ??
                       await dbContext.ProcessedSyncMutations
                           .AsNoTracking()
                           .FirstOrDefaultAsync(
                               entity => entity.MutationId.Trim().ToLower() == mutationId,
                               cancellationToken);

        if (existing is null)
        {
            var payloadHash = SyncMutationPayloadHasher.Compute(dto);
            return new DirectMutationCheck(
                DirectMutationStatus.New,
                mutationId,
                payloadHash,
                entityName,
                dto.Id,
                dto.ExpectedRevision,
                NormalizeDeviceId(deviceId),
                NormalizeUtc(dto.MutationCreatedAtUtc),
                null,
                string.Empty);
        }

        var payloadEvaluation =
            SyncMutationPayloadHasher.EvaluateForReceiptReplay(
                dto,
                existing.PayloadHash,
                existing.MutationId);
        var requestedEntityMatches = dto.Id == Guid.Empty ||
                                     string.Equals(
                                         existing.EntityId,
                                         dto.Id.ToString("D"),
                                         StringComparison.OrdinalIgnoreCase);
        var metadataMatches = string.Equals(
                                  existing.EntityName,
                                  entityName,
                                  StringComparison.OrdinalIgnoreCase) &&
                              requestedEntityMatches &&
                                  existing.ExpectedRevision == dto.ExpectedRevision;
        var payloadMatches = string.IsNullOrWhiteSpace(existing.PayloadHash) ||
                             payloadEvaluation.StoredPayloadMatches;

        if (!metadataMatches || !payloadMatches)
        {
            var reason = string.IsNullOrWhiteSpace(existing.PayloadHash)
                ? "Mutation id belongs to a legacy receipt whose payload cannot be verified."
                : "Mutation id was already processed with a different entity, expected revision, or payload.";
            return new DirectMutationCheck(
                DirectMutationStatus.Conflict,
                mutationId,
                payloadEvaluation.CanonicalPayloadHash,
                entityName,
                dto.Id,
                dto.ExpectedRevision,
                NormalizeDeviceId(deviceId),
                NormalizeUtc(dto.MutationCreatedAtUtc),
                existing,
                reason);
        }

        return new DirectMutationCheck(
            DirectMutationStatus.Duplicate,
            mutationId,
            payloadEvaluation.CanonicalPayloadHash,
            entityName,
            dto.Id,
            dto.ExpectedRevision,
            NormalizeDeviceId(deviceId),
            NormalizeUtc(dto.MutationCreatedAtUtc),
            existing,
            string.Empty);
    }

    public static void Record(
        AppDbContext dbContext,
        DirectMutationCheck check,
        Guid resolvedEntityId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(check);

        if (check.Status != DirectMutationStatus.New ||
            string.IsNullOrWhiteSpace(check.MutationId))
        {
            return;
        }

        if (dbContext.ProcessedSyncMutations.Local.Any(entity =>
                string.Equals(
                    NormalizeMutationId(entity.MutationId),
                    check.MutationId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Mutation id was registered more than once in the same unit of work: {check.MutationId}");
        }

        dbContext.ProcessedSyncMutations.Add(new ProcessedSyncMutation
        {
            MutationId = check.MutationId,
            DeviceId = check.DeviceId,
            EntityName = check.EntityName,
            EntityId = resolvedEntityId.ToString("D"),
            ExpectedRevision = check.ExpectedRevision,
            PayloadHash = check.PayloadHash,
            ProcessedAtUtc = check.ProcessedAtUtc
        });
    }

    public static DirectMutationConflictResponse BuildConflictResponse(DirectMutationCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return new DirectMutationConflictResponse
        {
            MutationId = check.MutationId,
            EntityName = check.EntityName,
            EntityId = check.RequestedEntityId,
            Reason = check.ConflictReason
        };
    }

    public static string NormalizeMutationId(string? mutationId)
        => string.IsNullOrWhiteSpace(mutationId)
            ? string.Empty
            : mutationId.Trim().ToLowerInvariant();

    private static string NormalizeDeviceId(string? deviceId)
        => string.IsNullOrWhiteSpace(deviceId) ? DirectApiDeviceId : deviceId.Trim();

    private static DateTime NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue || value.Value == default)
            return DateTime.UtcNow;

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
            _ => value.Value
        };
    }
}

public enum DirectMutationStatus
{
    NotTracked,
    New,
    Duplicate,
    Conflict
}

public sealed record DirectMutationCheck(
    DirectMutationStatus Status,
    string MutationId,
    string PayloadHash,
    string EntityName,
    Guid RequestedEntityId,
    long ExpectedRevision,
    string DeviceId,
    DateTime ProcessedAtUtc,
    ProcessedSyncMutation? ExistingReceipt,
    string ConflictReason)
{
    public static DirectMutationCheck NotTracked { get; } = new(
        DirectMutationStatus.NotTracked,
        string.Empty,
        string.Empty,
        string.Empty,
        Guid.Empty,
        0,
        ProcessedSyncMutationRecorder.DirectApiDeviceId,
        DateTime.UnixEpoch,
        null,
        string.Empty);
}

public sealed class DirectMutationConflictResponse
{
    public string Error { get; set; } = "mutation_id_conflict";
    public string MutationId { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string Reason { get; set; } = string.Empty;
}
