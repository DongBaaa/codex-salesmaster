using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Utilities;

public static class ProcessedSyncMutationRecorder
{
    public const string DirectApiDeviceId = "direct-api";

    public static async Task RecordAsync(
        AppDbContext dbContext,
        SyncEntityDto dto,
        string entityName,
        CancellationToken cancellationToken,
        string deviceId = DirectApiDeviceId)
    {
        var mutationId = NormalizeMutationId(dto.MutationId);
        if (string.IsNullOrWhiteSpace(mutationId))
            return;

        if (dbContext.ProcessedSyncMutations.Local.Any(entity =>
                string.Equals(entity.MutationId, mutationId, StringComparison.OrdinalIgnoreCase)) ||
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .AnyAsync(entity => entity.MutationId == mutationId, cancellationToken))
        {
            return;
        }

        dbContext.ProcessedSyncMutations.Add(new ProcessedSyncMutation
        {
            MutationId = mutationId,
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? DirectApiDeviceId : deviceId.Trim(),
            EntityName = entityName,
            EntityId = dto.Id.ToString("D"),
            ExpectedRevision = dto.ExpectedRevision,
            ProcessedAtUtc = NormalizeUtc(dto.MutationCreatedAtUtc)
        });
    }

    private static string NormalizeMutationId(string? mutationId)
        => string.IsNullOrWhiteSpace(mutationId) ? string.Empty : mutationId.Trim();

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
