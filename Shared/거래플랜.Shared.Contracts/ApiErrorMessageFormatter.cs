using System.Net;
using System.Text.Json;

namespace 거래플랜.Shared.Contracts;

public static class ApiErrorMessageFormatter
{
    public static string BuildFailureMessage(HttpStatusCode statusCode, string? reasonPhrase, string? body)
    {
        var statusText = BuildStatusText(statusCode, reasonPhrase);

        if (TryBuildPayloadMessage(statusCode, statusText, body, out var payloadMessage))
            return payloadMessage;

        var trimmedBody = TrimBody(body);

        if (statusCode == HttpStatusCode.Unauthorized)
            return $"{statusText} 로그인 세션이 만료되었거나 권한이 없습니다. 다시 로그인하세요. {trimmedBody}".Trim();

        if (statusCode == HttpStatusCode.Forbidden)
            return $"{statusText} 현재 계정에 이 작업을 수행할 권한이 없습니다. 관리자에게 권한 확인을 요청하세요.".Trim();

        return $"{statusText} {trimmedBody}".Trim();
    }

    private static bool TryBuildPayloadMessage(HttpStatusCode statusCode, string statusText, string? body, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var value = root.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    return false;

                message = $"{statusText} {value.Trim()}".Trim();
                return true;
            }

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (statusCode == HttpStatusCode.Conflict &&
                TryBuildExpectedRevisionConflictMessage(statusText, root, out message))
            {
                return true;
            }

            var parts = new List<string>();
            var errorCode = GetStringProperty(root, "error");
            AddDistinctPart(parts, MapKnownError(errorCode) ?? errorCode);
            AddDistinctPart(parts, GetStringProperty(root, "message"));
            AddDistinctPart(parts, GetStringProperty(root, "detail"));
            AddDistinctPart(parts, ApiConflictReasonTranslator.ToUserMessage(GetStringProperty(root, "reason")));
            AddDistinctPart(parts, GetStringProperty(root, "title"));

            foreach (var validationError in GetValidationErrorMessages(root))
                AddDistinctPart(parts, validationError);

            if (parts.Count == 0)
                return false;

            message = $"{statusText} {string.Join(" ", parts)}".Trim();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryBuildExpectedRevisionConflictMessage(string statusText, JsonElement root, out string message)
    {
        message = string.Empty;
        var entityName = GetStringProperty(root, "entityName");
        var reason = GetStringProperty(root, "reason");
        var expectedRevision = GetInt64Property(root, "expectedRevision");
        var currentRevision = GetInt64Property(root, "currentRevision");

        if (string.IsNullOrWhiteSpace(entityName) &&
            expectedRevision is null &&
            currentRevision is null)
        {
            return false;
        }

        var entityDisplayName = ResolveEntityDisplayName(entityName);
        var revisionHint = expectedRevision is > 0 && currentRevision is > 0
            ? $" (요청 rev {expectedRevision.Value:N0} / 서버 rev {currentRevision.Value:N0})"
            : currentRevision is > 0
                ? $" (서버 rev {currentRevision.Value:N0})"
                : string.Empty;

        var translatedReason = ApiConflictReasonTranslator.ToUserMessage(reason);
        var baseMessage = $"{statusText} {entityDisplayName} 저장 충돌: 서버 최신 내용과 현재 저장 내용이 충돌했습니다. 최신 데이터를 다시 불러온 뒤 다시 시도하세요.{revisionHint}";
        message = string.IsNullOrWhiteSpace(translatedReason)
            ? baseMessage
            : $"{baseMessage} {translatedReason}";
        return true;
    }

    private static IEnumerable<string> GetValidationErrorMessages(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errorsElement) ||
            errorsElement.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in errorsElement.EnumerateObject())
        {
            var messages = ReadValidationMessages(property.Value)
                .Select(message => message.Trim())
                .Where(message => message.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (messages.Count == 0)
                continue;

            yield return string.IsNullOrWhiteSpace(property.Name)
                ? string.Join(", ", messages)
                : $"{property.Name}: {string.Join(", ", messages)}";
        }
    }

    private static IEnumerable<string> ReadValidationMessages(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var message = value.GetString();
            if (!string.IsNullOrWhiteSpace(message))
                yield return message;

            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var message = item.GetString();
            if (!string.IsNullOrWhiteSpace(message))
                yield return message;
        }
    }

    private static string? GetStringProperty(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static long? GetInt64Property(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value))
            return value;

        if (property.ValueKind == JsonValueKind.String &&
            long.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement property)
    {
        if (root.TryGetProperty(propertyName, out property))
            return true;

        foreach (var current in root.EnumerateObject())
        {
            if (string.Equals(current.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = current.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string? MapKnownError(string? errorCode)
        => errorCode switch
        {
            "contract_content_unavailable" =>
                "계약서 파일 내용을 찾을 수 없습니다. 서버에는 계약서 정보가 있으나 실제 파일이 없거나 손상되었습니다. 운영 점검의 파일 저장소 무결성 결과를 확인한 뒤 다시 시도하세요.",
            "attachment_content_unavailable" =>
                "첨부 파일 내용을 찾을 수 없습니다. 서버에는 첨부 정보가 있으나 실제 파일이 없거나 손상되었습니다. 운영 점검의 파일 저장소 무결성 결과를 확인한 뒤 다시 시도하세요.",
            _ => null
        };

    private static string ResolveEntityDisplayName(string? entityName)
        => entityName?.Trim() switch
        {
            "Customer" => "거래처",
            "Item" => "품목",
            "Invoice" => "전표",
            "Payment" => "수금/지급",
            "TransactionRecord" => "거래내역",
            "InventoryTransfer" => "재고이동",
            "RentalBillingProfile" => "렌탈 청구",
            "RentalAsset" => "렌탈 자산",
            "UserAccount" => "사용자",
            { Length: > 0 } value => value,
            _ => "데이터"
        };

    private static void AddDistinctPart(List<string> parts, string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (parts.Any(part => string.Equals(part, normalized, StringComparison.OrdinalIgnoreCase)))
            return;

        parts.Add(normalized);
    }

    private static string BuildStatusText(HttpStatusCode statusCode, string? reasonPhrase)
    {
        var reason = string.IsNullOrWhiteSpace(reasonPhrase)
            ? statusCode.ToString()
            : reasonPhrase.Trim();
        return $"{(int)statusCode} {reason}".Trim();
    }

    private static string TrimBody(string? body)
    {
        var trimmedBody = body?.Trim() ?? string.Empty;
        return trimmedBody.Length > 200 ? trimmedBody[..200] + "..." : trimmedBody;
    }
}
