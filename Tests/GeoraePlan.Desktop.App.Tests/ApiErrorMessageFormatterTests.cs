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

    [Fact]
    public void BuildFailureMessage_ExpectedRevisionConflictPayload_ReturnsBusinessGuidanceWithoutRawJson()
    {
        var body = JsonSerializer.Serialize(new
        {
            entityName = "Invoice",
            entityId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            expectedRevision = 10,
            currentRevision = 12,
            reason = "A paid, rental-linked, or versioned invoice cannot be structurally changed with the same invoice id. Save it as a new invoice version."
        });

        var message = ApiErrorMessageFormatter.BuildFailureMessage(
            HttpStatusCode.Conflict,
            "Conflict",
            body);

        Assert.StartsWith("409 Conflict", message);
        Assert.Contains("\uC804\uD45C \uC800\uC7A5 \uCDA9\uB3CC", message);
        Assert.Contains("\uC694\uCCAD rev 10", message);
        Assert.Contains("\uC11C\uBC84 rev 12", message);
        Assert.Contains("\uC0C8 \uBC84\uC804", message);
        Assert.Contains("\uC218\uAE08/\uC9C0\uAE09", message);
        Assert.DoesNotContain("same invoice id", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{\"entityName\"", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFailureMessage_ExpectedRevisionMismatchPayload_ReturnsReloadGuidance()
    {
        var body = JsonSerializer.Serialize(new
        {
            EntityName = "Customer",
            EntityId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            ExpectedRevision = 3,
            CurrentRevision = 4,
            Reason = "Expected revision mismatch. client=3, server=4"
        });

        var message = ApiErrorMessageFormatter.BuildFailureMessage(
            HttpStatusCode.Conflict,
            "Conflict",
            body);

        Assert.StartsWith("409 Conflict", message);
        Assert.Contains("\uAC70\uB798\uCC98 \uC800\uC7A5 \uCDA9\uB3CC", message);
        Assert.Contains("\uCD5C\uC2E0 \uB370\uC774\uD130\uB97C \uB2E4\uC2DC \uBD88\uB7EC\uC628 \uB4A4 \uB2E4\uC2DC \uC2DC\uB3C4", message);
        Assert.DoesNotContain("Expected revision mismatch", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{\"EntityName\"", message, StringComparison.OrdinalIgnoreCase);
    }

}
