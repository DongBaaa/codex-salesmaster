using System.Net;
using System.Text.Json;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ApiErrorMessageFormatterTests
{
    [Fact]
    public void BuildFailureMessage_ValidationProblemPayload_ReturnsReadableValidationDetails()
    {
        var body = JsonSerializer.Serialize(new
        {
            title = "One or more validation errors occurred.",
            status = 400,
            detail = "입력값을 확인하세요.",
            errors = new Dictionary<string, string[]>
            {
                ["InvoiceDate"] = ["날짜가 올바르지 않습니다."],
                ["CustomerId"] = ["거래처가 필요합니다."]
            }
        });

        var message = ApiErrorMessageFormatter.BuildFailureMessage(
            HttpStatusCode.BadRequest,
            "Bad Request",
            body);

        Assert.StartsWith("400 Bad Request", message);
        Assert.Contains("입력값을 확인하세요.", message);
        Assert.Contains("InvoiceDate: 날짜가 올바르지 않습니다.", message);
        Assert.Contains("CustomerId: 거래처가 필요합니다.", message);
        Assert.DoesNotContain("{\"title\"", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFailureMessage_StringValidationErrorPayload_ReturnsReadableMessage()
    {
        var body = JsonSerializer.Serialize(new
        {
            title = "검증 오류",
            errors = new Dictionary<string, string>
            {
                ["Quantity"] = "수량은 0보다 커야 합니다."
            }
        });

        var message = ApiErrorMessageFormatter.BuildFailureMessage(
            HttpStatusCode.BadRequest,
            "Bad Request",
            body);

        Assert.Contains("검증 오류", message);
        Assert.Contains("Quantity: 수량은 0보다 커야 합니다.", message);
    }

    [Fact]
    public void BuildFailureMessage_EmptyForbiddenPayload_ReturnsPermissionGuidance()
    {
        var message = ApiErrorMessageFormatter.BuildFailureMessage(
            HttpStatusCode.Forbidden,
            "Forbidden",
            string.Empty);

        Assert.Equal(
            "403 Forbidden 현재 계정에 이 작업을 수행할 권한이 없습니다. 관리자에게 권한 확인을 요청하세요.",
            message);
    }

    [Fact]
    public void BuildFailureMessage_KnownAttachmentErrorPayload_ReturnsBusinessGuidanceWithoutRawErrorCode()
    {
        var body = JsonSerializer.Serialize(new
        {
            error = "attachment_content_unavailable",
            message = "파일 본문이 없습니다."
        });

        var message = ApiErrorMessageFormatter.BuildFailureMessage(
            HttpStatusCode.NotFound,
            "Not Found",
            body);

        Assert.StartsWith("404 Not Found", message);
        Assert.Contains("첨부 파일 내용을 찾을 수 없습니다.", message);
        Assert.Contains("파일 본문이 없습니다.", message);
        Assert.DoesNotContain("attachment_content_unavailable", message);
    }
}
