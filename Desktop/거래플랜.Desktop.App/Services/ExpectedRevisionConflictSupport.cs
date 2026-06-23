using System.Net;
using System.Net.Http;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

internal sealed class ExpectedRevisionConflictException : HttpRequestException
{
    public ExpectedRevisionConflictException(
        string entityName,
        Guid entityId,
        long expectedRevision,
        long currentRevision,
        string? reason = null)
        : base(
            ConcurrencyConflictFormatter.BuildMessage(entityName, expectedRevision, currentRevision, reason),
            inner: null,
            statusCode: HttpStatusCode.Conflict)
    {
        EntityName = entityName;
        EntityId = entityId;
        ExpectedRevision = expectedRevision;
        CurrentRevision = currentRevision;
        Reason = reason ?? string.Empty;
    }

    public string EntityName { get; }
    public Guid EntityId { get; }
    public long ExpectedRevision { get; }
    public long CurrentRevision { get; }
    public string Reason { get; }
}

internal sealed class ExpectedRevisionConflictPayload
{
    public string EntityName { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public long ExpectedRevision { get; set; }
    public long CurrentRevision { get; set; }
    public string Reason { get; set; } = string.Empty;
}

internal static class ConcurrencyConflictFormatter
{
    private static readonly IReadOnlyDictionary<string, string> EntityDisplayNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Customer"] = "거래처",
        ["Item"] = "품목",
        ["Invoice"] = "전표",
        ["Payment"] = "수금/지급 내역",
        ["UserAccount"] = "사용자",
        ["Unit"] = "선택값",
        ["TenantDefinition"] = "업체 권역",
        ["TenantOfficeDefinition"] = "지점 정의",
        ["DataSharingPolicy"] = "연동 정책",
        ["RentalBillingProfile"] = "렌탈 청구",
        ["RentalAsset"] = "렌탈 자산"
    };

    public static string BuildMessage(string entityDisplayName, long? expectedRevision = null, long? currentRevision = null, string? reason = null)
    {
        var resolvedEntityName = ResolveEntityDisplayName(entityDisplayName);
        var revisionHint = expectedRevision is > 0 && currentRevision is > 0
            ? $" (내 rev {expectedRevision.Value:N0} / 최신 rev {currentRevision.Value:N0})"
            : currentRevision is > 0
                ? $" (최신 rev {currentRevision.Value:N0})"
                : string.Empty;

        var baseMessage = $"다른 PC에서 해당 {resolvedEntityName}의 최신 내용이 먼저 저장되었습니다. 최신값을 다시 불러온 뒤 다시 시도하세요.{revisionHint}";
        var translatedReason = ApiConflictReasonTranslator.ToUserMessage(reason);
        return string.IsNullOrWhiteSpace(translatedReason)
            ? baseMessage
            : $"{baseMessage}{Environment.NewLine}{Environment.NewLine}{translatedReason}";
    }

    private static string ResolveEntityDisplayName(string entityDisplayName)
    {
        if (string.IsNullOrWhiteSpace(entityDisplayName))
            return "데이터";

        var trimmed = entityDisplayName.Trim();
        return EntityDisplayNameMap.TryGetValue(trimmed, out var mapped)
            ? mapped
            : trimmed;
    }
}
