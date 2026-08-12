using 거래플랜.Server.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace 거래플랜.Server.Api.Middleware;

public sealed class DbUpdateConcurrencyExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<DbUpdateConcurrencyExceptionMiddleware> _logger;

    public DbUpdateConcurrencyExceptionMiddleware(
        RequestDelegate next,
        ILogger<DbUpdateConcurrencyExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            if (context.Response.HasStarted)
                throw;

            var response = await DbUpdateConcurrencyConflictMapper.MapAsync(
                exception,
                context.RequestAborted);

            _logger.LogWarning(
                "Optimistic concurrency conflict returned HTTP 409. TraceIdentifier={TraceIdentifier}, ConflictCount={ConflictCount}",
                context.TraceIdentifier,
                response.Conflicts.Count);

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(response, cancellationToken: context.RequestAborted);
        }
    }
}

public static class DbUpdateConcurrencyConflictMapper
{
    public static async Task<DbUpdateConcurrencyConflictResponse> MapAsync(
        DbUpdateConcurrencyException exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var conflicts = new List<DbUpdateConcurrencyConflictDetail>(exception.Entries.Count);
        foreach (var entry in exception.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var revisionProperty = entry.Metadata.FindProperty(nameof(ITrackedEntity.Revision));
            var originalRevision = revisionProperty is null
                ? null
                : TryReadLong(entry.OriginalValues, revisionProperty.Name);
            var actualRevision = await TryReadActualRevisionAsync(
                entry,
                revisionProperty?.Name,
                cancellationToken);

            conflicts.Add(new DbUpdateConcurrencyConflictDetail
            {
                EntityName = ResolveEntityName(entry),
                EntityId = TryResolveEntityId(entry),
                OriginalRevision = originalRevision,
                ActualRevision = actualRevision
            });
        }

        return new DbUpdateConcurrencyConflictResponse
        {
            Conflicts = conflicts
        };
    }

    private static string ResolveEntityName(EntityEntry entry)
        => string.IsNullOrWhiteSpace(entry.Metadata.ClrType.Name)
            ? "Unknown"
            : entry.Metadata.ClrType.Name;

    private static Guid? TryResolveEntityId(EntityEntry entry)
    {
        var idProperty = entry.Metadata.FindProperty(nameof(ITrackedEntity.Id));
        if (idProperty is null ||
            (idProperty.ClrType != typeof(Guid) && idProperty.ClrType != typeof(Guid?)))
        {
            return null;
        }

        return TryReadGuid(entry.OriginalValues, idProperty.Name)
               ?? TryReadGuid(entry.CurrentValues, idProperty.Name);
    }

    private static async Task<long?> TryReadActualRevisionAsync(
        EntityEntry entry,
        string? revisionPropertyName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revisionPropertyName))
            return null;

        try
        {
            var databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken);
            return databaseValues is null
                ? null
                : TryReadLong(databaseValues, revisionPropertyName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Conflict responses must remain available even when the current
            // database row cannot be read (for example, after a concurrent delete).
            return null;
        }
    }

    private static Guid? TryReadGuid(PropertyValues values, string propertyName)
    {
        try
        {
            return values[propertyName] switch
            {
                Guid value when value != Guid.Empty => value,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static long? TryReadLong(PropertyValues values, string propertyName)
    {
        try
        {
            return values[propertyName] switch
            {
                long value => value,
                int value => value,
                short value => value,
                byte value => value,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}

public sealed class DbUpdateConcurrencyConflictResponse
{
    public string Error { get; init; } = "concurrency_conflict";
    public string Message { get; init; } = "저장 중 데이터가 다른 작업에서 변경되었습니다. 최신 데이터를 다시 불러온 뒤 재시도하세요.";
    public IReadOnlyList<DbUpdateConcurrencyConflictDetail> Conflicts { get; init; } =
        Array.Empty<DbUpdateConcurrencyConflictDetail>();
}

public sealed class DbUpdateConcurrencyConflictDetail
{
    public string EntityName { get; init; } = string.Empty;
    public Guid? EntityId { get; init; }
    public long? OriginalRevision { get; init; }
    public long? ActualRevision { get; init; }
}
