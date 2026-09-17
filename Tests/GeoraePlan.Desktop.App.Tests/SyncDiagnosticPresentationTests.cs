using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncDiagnosticPresentationTests
{
    [Theory]
    [InlineData("Acknowledged", "")]
    [InlineData(" acknowledged ", "Expected revision mismatch")]
    public void AcknowledgedOutbox_DoesNotDescribeAnActiveWaitOrHistoricalFailure(string status, string error)
    {
        var row = new SyncOutboxListItem { Status = status, ErrorMessage = error };

        Assert.Equal("전송 완료", row.StatusDisplay);
        Assert.Equal("서버에서 전송 완료를 확인했습니다.", row.ErrorMessageDisplay);
        Assert.Equal(error, row.ErrorMessage);
    }

    [Theory]
    [InlineData("Prepared", "서버 전송 대기 중입니다.")]
    [InlineData("Sent", "전송 후 서버의 완료 확인을 기다리고 있습니다.")]
    [InlineData("Failed", "서버 전송에 실패했습니다. 진단 리포트에서 상세 내용을 확인하세요.")]
    [InlineData("Unrecognized", "전송 상태를 확인하세요.")]
    public void OutboxWithoutError_ExplainsItsActualState(string status, string expected)
        => Assert.Equal(expected, new SyncOutboxListItem { Status = status }.ErrorMessageDisplay);

    [Fact]
    public void FailedOutbox_PreservesTheActionableErrorExplanation()
    {
        var row = new SyncOutboxListItem { Status = "Failed", ErrorMessage = "Expected revision mismatch" };

        Assert.Contains("다른 PC", row.ErrorMessageDisplay);
        Assert.Contains("최신 데이터", row.ErrorMessageDisplay);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void PersistedUtcTimes_ArePresentedInTheSameLocalZoneAsTheSummary(DateTimeKind kind)
    {
        var stored = new DateTime(2026, 9, 14, 9, 17, 5, kind);
        var instant = new DateTimeOffset(2026, 9, 14, 9, 17, 5, TimeSpan.Zero);
        var expected = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).DateTime;
        var diagnostic = new SyncDiagnosticListItem { LastOccurredAtUtc = stored };
        var outbox = new SyncOutboxListItem { PreparedAtUtc = stored, SentAtUtc = stored };

        Assert.Equal(expected, diagnostic.LastOccurredAtLocal);
        Assert.Equal(expected, outbox.PreparedAtLocal);
        Assert.Equal(expected, outbox.SentAtLocal);
        Assert.Equal(stored, diagnostic.LastOccurredAtUtc);
        Assert.Equal(stored, outbox.PreparedAtUtc);
        Assert.Equal(stored, outbox.SentAtUtc);
        Assert.Null(new SyncOutboxListItem().SentAtLocal);
    }
}
