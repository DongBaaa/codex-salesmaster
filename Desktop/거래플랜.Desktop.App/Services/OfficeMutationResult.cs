using System;
using System.Collections.Generic;
using System.Linq;

namespace 거래플랜.Desktop.App.Services;

public sealed class OfficeMutationResult
{
    public bool Success { get; init; }
    public bool ConcurrencyConflict { get; init; }
    public bool PermissionDenied { get; init; }
    public bool NotFound { get; init; }
    public bool GrantedTemporaryAccess { get; init; }
    public Guid EntityId { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string Message { get; init; } = string.Empty;

    public static OfficeMutationResult Ok(
        Guid entityId = default,
        string message = "",
        bool grantedTemporaryAccess = false,
        IEnumerable<string>? warnings = null) => new()
    {
        Success = true,
        EntityId = entityId,
        Warnings = NormalizeWarnings(warnings),
        Message = ComposeMessage(message, warnings),
        GrantedTemporaryAccess = grantedTemporaryAccess
    };

    public static OfficeMutationResult Denied(string message) => new()
    {
        PermissionDenied = true,
        Message = message
    };

    public static OfficeMutationResult Conflict(string message) => new()
    {
        ConcurrencyConflict = true,
        Message = message
    };

    public static OfficeMutationResult Missing(string message) => new()
    {
        NotFound = true,
        Message = message
    };

    private static IReadOnlyList<string> NormalizeWarnings(IEnumerable<string>? warnings)
        => warnings?
            .Select(warning => (warning ?? string.Empty).Trim())
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.Ordinal)
            .ToArray()
           ?? Array.Empty<string>();

    private static string ComposeMessage(string message, IEnumerable<string>? warnings)
    {
        var normalizedMessage = (message ?? string.Empty).Trim();
        var normalizedWarnings = NormalizeWarnings(warnings);
        if (normalizedWarnings.Count == 0)
            return normalizedMessage;

        var warningText = string.Join(" / ", normalizedWarnings);
        return string.IsNullOrWhiteSpace(normalizedMessage)
            ? warningText
            : $"{normalizedMessage} ({warningText})";
    }
}
