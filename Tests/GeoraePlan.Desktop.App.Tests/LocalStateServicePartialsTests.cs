using System.Reflection;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalStateServicePartialsTests
{
    [Fact]
    public void PendingSyncSummary_BuildWaitingMessage_UsesPrimaryBucketAndTotal()
    {
        var summary = new PendingSyncSummary(
            5,
            [
                new PendingSyncBucket("OFFICE:ITWORLD", "ITWORLD", "거래처 변경", 3),
                new PendingSyncBucket("OFFICE:USENET", "USENET", "품목 변경", 2)
            ]);

        var message = summary.BuildWaitingMessage("안내:");

        Assert.Equal("안내: ITWORLD 거래처 변경 3건 포함 총 5건이 서버 반영 대기 중입니다.", message);
        Assert.Equal("ITWORLD", summary.PrimaryBucket?.ScopeDisplayName);
    }

    [Fact]
    public void LocalIntegrityReport_BuildSummaryText_AndToMarkdown_IncludeKeySignals()
    {
        var report = new LocalIntegrityReport(
            new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc),
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.UsenetGroup,
            2,
            pendingServerMirrorRefresh: true,
            [
                new LocalIntegrityIssue("sync_outbox_failed_pending", "Error", 3, "실패 상태의 sync outbox가 남아 있습니다."),
                new LocalIntegrityIssue("out_of_scope_items", "Warning", 1, "현재 계정 범위 밖 품목 캐시가 남아 있습니다.")
            ]);

        var summary = report.BuildSummaryText(maxIssues: 1);
        var markdown = report.ToMarkdown();

        Assert.Contains("버전 변경 후 중앙 서버 기준 전체 재동기화가 대기 중입니다.", summary, StringComparison.Ordinal);
        Assert.Contains("실패 상태의 sync outbox가 남아 있습니다. (3건)", summary, StringComparison.Ordinal);
        Assert.Contains("그 외 1개 항목은 무결성 리포트에서 확인하세요.", summary, StringComparison.Ordinal);
        Assert.Contains("현재 미동기화 변경 2건이 있어", summary, StringComparison.Ordinal);

        Assert.Contains("# 무결성 점검 리포트", markdown, StringComparison.Ordinal);
        Assert.Contains("sync_outbox_failed_pending", markdown, StringComparison.Ordinal);
        Assert.Contains("out_of_scope_items", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveScope_PrefersOfficeScopeOverTenantScope()
    {
        var result = InvokePrivateStatic<(string ScopeKey, string ScopeDisplayName)>(
            "ResolveScope",
            OfficeCodeCatalog.Itworld,
            TenantScopeCatalog.UsenetGroup);

        Assert.Equal("OFFICE:ITWORLD", result.ScopeKey);
        Assert.Equal("ITWORLD", result.ScopeDisplayName);
    }

    [Fact]
    public void ResolveOfficeCodeFromTenant_ReturnsRepresentativeOffice()
    {
        var officeCode = InvokePrivateStatic<string>(
            "ResolveOfficeCodeFromTenant",
            TenantScopeCatalog.UsenetGroup);

        Assert.Equal(OfficeCodeCatalog.Usenet, officeCode);
    }

    [Fact]
    public void NormalizeOutboxErrorMessage_TruncatesLongMessage_AndSuppliesDefault()
    {
        var longMessage = new string('x', 600);

        var truncated = InvokePrivateStatic<string>("NormalizeOutboxErrorMessage", longMessage);
        var defaultMessage = InvokePrivateStatic<string>("NormalizeOutboxErrorMessage", new object?[] { null });

        Assert.Equal(500, truncated.Length);
        Assert.Equal("동기화 중 알 수 없는 오류가 발생했습니다.", defaultMessage);
    }

    [Fact]
    public void GetOutboxStatusWeight_UsesExpectedPriorityOrder()
    {
        var failed = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Failed");
        var prepared = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Prepared");
        var sent = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Sent");
        var acknowledged = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Acknowledged");
        var unknown = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Unknown");

        Assert.True(failed < prepared);
        Assert.True(prepared < sent);
        Assert.True(sent < acknowledged);
        Assert.True(acknowledged < unknown);
    }

    [Fact]
    public void SyncOutboxListItem_ComputedProperties_WorkAsExpected()
    {
        var item = new SyncOutboxListItem
        {
            EntityId = Guid.Empty,
            MutationId = new string('a', 40),
            Status = "Failed"
        };

        Assert.Equal("-", item.EntityIdText);
        Assert.Equal(39, item.ShortMutationId.Length);
        Assert.EndsWith("...", item.ShortMutationId, StringComparison.Ordinal);
        Assert.True(item.IsFailed);
        Assert.False(item.IsAcknowledged);
    }

    [Fact]
    public void RecycleBinEntry_AndDependencyModels_ComputedProperties_WorkAsExpected()
    {
        var localDeletedAt = new DateTime(2026, 4, 20, 13, 45, 0, DateTimeKind.Local);
        var entry = new RecycleBinEntry
        {
            EntityId = Guid.NewGuid(),
            Kind = RecycleBinEntityKind.InventoryTransfer,
            DeletedAtUtc = localDeletedAt.ToUniversalTime()
        };

        var dependency = new RecycleBinDependencyItem
        {
            Label = "전표",
            Count = 3
        };

        var candidate = new RecycleBinCustomerMergeCandidate
        {
            CustomerId = Guid.NewGuid(),
            Name = "거래처A",
            BusinessNumber = "",
            Phone = "010-1234-5678",
            ResponsibleOfficeCode = "ITWORLD"
        };

        Assert.Equal("재고이동", entry.KindText);
        Assert.Equal(localDeletedAt.ToString("yyyy-MM-dd HH:mm"), entry.DeletedAtLocalText);
        Assert.Equal("전표 3건", dependency.DisplayText);
        Assert.Equal("거래처A / 010-1234-5678 / ITWORLD", candidate.DisplayText);
    }

    [Fact]
    public void RecycleBinHelpers_NormalizeAndFormatAsExpected()
    {
        var joined = InvokePrivateStatic<string>(
            "JoinSegments",
            new object?[] { new string?[] { "  거래처A  ", null, " 010-1234-5678 ", " " } });
        var digits = InvokePrivateStatic<string>("NormalizeDigits", "사업자 123-45-67890 / 연락처 010-1111-2222");
        var voucher = InvokePrivateStatic<string>("GetVoucherTypeLabel", VoucherType.Sales);
        var fallbackKind = InvokePrivateStatic<string>("GetTransactionKindLabel", "  임의구분  ");
        var emptyKind = InvokePrivateStatic<string>("GetTransactionKindLabel", new object?[] { null });

        Assert.Equal("거래처A / 010-1234-5678", joined);
        Assert.Equal("123456789001011112222", digits);
        Assert.Equal("매출", voucher);
        Assert.Equal("임의구분", fallbackKind);
        Assert.Equal("거래내역", emptyKind);
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[]? args)
    {
        var method = typeof(LocalStateService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        Assert.NotNull(result);
        return (T)result!;
    }
}
