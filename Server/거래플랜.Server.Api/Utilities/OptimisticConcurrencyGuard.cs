using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace 거래플랜.Server.Api.Utilities;

public static class OptimisticConcurrencyGuard
{
    public static ActionResult? Check(ControllerBase controller, TrackedEntity entity, SyncEntityDto dto, string entityName)
    {
        if (!TryResolveExpectedRevision(dto.ExpectedRevision, dto.Revision, controller.HttpContext?.Request, out var expectedRevision))
            return null;

        return Check(controller, entity, expectedRevision, entityName);
    }

    public static ActionResult? Check(ControllerBase controller, TrackedEntity entity, long? expectedRevision, string entityName)
    {
        if (!TryResolveExpectedRevision(expectedRevision, null, controller.HttpContext?.Request, out var normalizedExpectedRevision))
            return null;

        if (entity.Revision == normalizedExpectedRevision)
            return null;

        return controller.Conflict(new ExpectedRevisionConflictResponse
        {
            EntityName = entityName,
            EntityId = entity.Id,
            ExpectedRevision = normalizedExpectedRevision,
            CurrentRevision = entity.Revision,
            Reason = BuildExpectedRevisionConflictReason(normalizedExpectedRevision, entity.Revision)
        });
    }

    public static string BuildExpectedRevisionConflictReason(long expectedRevision, long currentRevision)
        => $"Expected revision mismatch. client={expectedRevision}, server={currentRevision}";

    private static bool TryResolveExpectedRevision(
        long? preferredRevision,
        long? fallbackRevision,
        HttpRequest? request,
        out long expectedRevision)
    {
        expectedRevision = 0;

        if (preferredRevision is > 0)
        {
            expectedRevision = preferredRevision.Value;
            return true;
        }

        if (fallbackRevision is > 0)
        {
            expectedRevision = fallbackRevision.Value;
            return true;
        }

        return request is not null && TryParseIfMatch(request, out expectedRevision);
    }

    private static bool TryParseIfMatch(HttpRequest request, out long expectedRevision)
    {
        expectedRevision = 0;

        if (!request.Headers.TryGetValue("If-Match", out var values))
            return false;

        foreach (var value in values)
        {
            if (TryParseIfMatchValue(value, out expectedRevision))
                return true;
        }

        return false;
    }

    private static bool TryParseIfMatchValue(string? rawValue, out long expectedRevision)
    {
        expectedRevision = 0;

        if (string.IsNullOrWhiteSpace(rawValue))
            return false;

        var normalized = rawValue.Trim();
        if (string.Equals(normalized, "*", StringComparison.Ordinal))
            return false;

        if (normalized.StartsWith("W/\"", StringComparison.OrdinalIgnoreCase) && normalized.EndsWith('"'))
            normalized = normalized[3..^1];
        else
            normalized = normalized.Trim('"');

        return long.TryParse(normalized, out expectedRevision) && expectedRevision > 0;
    }
}

public sealed class ExpectedRevisionConflictResponse
{
    public string EntityName { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public long ExpectedRevision { get; set; }
    public long CurrentRevision { get; set; }
    public string Reason { get; set; } = string.Empty;
}
