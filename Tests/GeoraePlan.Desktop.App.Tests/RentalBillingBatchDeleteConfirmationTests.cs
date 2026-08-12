using System.Reflection;
using 거래플랜.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingBatchDeleteConfirmationTests
{
    [Fact]
    public void BuildDeleteCheckedConfirmationMessage_CombinedSelection_DisclosesEveryAppliedAndSkippedCount()
    {
        var message = InvokeBuildDeleteCheckedConfirmationMessage(
            persistedProfileCount: 2,
            unlinkedAssetCount: 3,
            skippedAggregateCount: 4,
            skippedPermissionCount: 5);

        Assert.Contains("청구 프로필 2건", message, StringComparison.Ordinal);
        Assert.Contains("청구설정 필요 장비 3대", message, StringComparison.Ordinal);
        Assert.Contains("권한/담당지점 범위 밖 5건은 제외됩니다.", message, StringComparison.Ordinal);
        Assert.Contains("거래처별 요약행 4건은 제외됩니다.", message, StringComparison.Ordinal);
        Assert.Contains("자산 자체는 삭제되지 않지만 프로필 연결은 해제", message, StringComparison.Ordinal);
        Assert.Contains("자산 연결은 자동 복구되지 않으므로", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, 0, "선택한 청구 프로필 2건을 삭제하시겠습니까?", "프로필 연결은 해제")]
    [InlineData(0, 3, "청구설정 필요 장비 3대를 청구 목록에서 제외하시겠습니까?", "자산 정보는 삭제되지 않습니다.")]
    public void BuildDeleteCheckedConfirmationMessage_SingleTargetType_PreservesSpecificGuidance(
        int persistedProfileCount,
        int unlinkedAssetCount,
        string expectedQuestion,
        string expectedGuidance)
    {
        var message = InvokeBuildDeleteCheckedConfirmationMessage(
            persistedProfileCount,
            unlinkedAssetCount,
            skippedAggregateCount: 0,
            skippedPermissionCount: 0);

        Assert.StartsWith(expectedQuestion, message, StringComparison.Ordinal);
        Assert.Contains(expectedGuidance, message, StringComparison.Ordinal);
        Assert.DoesNotContain("0건은 제외", message, StringComparison.Ordinal);
    }

    private static string InvokeBuildDeleteCheckedConfirmationMessage(
        int persistedProfileCount,
        int unlinkedAssetCount,
        int skippedAggregateCount,
        int skippedPermissionCount)
    {
        var method = typeof(RentalBillingViewModel).GetMethod(
            "BuildDeleteCheckedConfirmationMessage",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(
            null,
            [persistedProfileCount, unlinkedAssetCount, skippedAggregateCount, skippedPermissionCount]));
    }
}
