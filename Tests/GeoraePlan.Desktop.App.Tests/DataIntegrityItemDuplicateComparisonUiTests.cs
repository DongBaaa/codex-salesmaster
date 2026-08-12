using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegrityItemDuplicateComparisonUiTests
{
    [Fact]
    public void ComparisonWindow_ShowsEveryCandidateAndKeepsMergeBehindExplicitSelectionAndSafetyGate()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "ItemDuplicateComparisonWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "ItemDuplicateComparisonWindow.xaml.cs"));

        Assert.Contains("ItemsSource=\"{Binding Comparison.Candidates}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("후보 ID", xaml, StringComparison.Ordinal);
        Assert.Contains("참조 영향", xaml, StringComparison.Ordinal);
        Assert.Contains("자산별 필드", xaml, StringComparison.Ordinal);
        Assert.Contains("동기화 상태", xaml, StringComparison.Ordinal);
        Assert.Contains("비교 화면을 여는 것만으로는 데이터가 변경되지 않습니다.", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"900\" MinHeight=\"600\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(this)", code, StringComparison.Ordinal);
        Assert.Contains("MergeSelectedButton.IsEnabled = hasSelection && Review.CanMerge", code, StringComparison.Ordinal);
        Assert.Contains("Review.BlockingReasonText", code, StringComparison.Ordinal);
        Assert.Contains("SelectedCanonicalItemId = SelectedCandidate.ItemId", code, StringComparison.Ordinal);
        Assert.Contains("RequestedItemId = SelectedCandidate.ItemId", code, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrityEntryPoints_UseSameComparisonSnapshotAndExplicitCanonicalMergeApi()
    {
        var root = FindRepositoryRoot();
        var mainWindowCode = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.xaml.cs"));
        var environmentCode = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "EnvironmentSettingsViewModel.Sync.cs"));
        var issueXaml = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "DataIntegrityIssueWindow.xaml"));

        foreach (var source in new[] { mainWindowCode, environmentCode })
        {
            Assert.Contains("PrepareItemDuplicateReviewAsync(issue, _session)", source, StringComparison.Ordinal);
            Assert.Contains("new ItemDuplicateComparisonWindow(review)", source, StringComparison.Ordinal);
            Assert.Contains("SelectedCanonicalItemId", source, StringComparison.Ordinal);
            Assert.Contains("MergeDuplicateItemIssueAsync", source, StringComparison.Ordinal);
            Assert.Contains("review.Comparison.SnapshotToken", source, StringComparison.Ordinal);
        }

        Assert.Contains("SelectedIssue.CanReviewDuplicateCandidates", issueXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedIssue.DuplicateReviewActionText", issueXaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "거래플랜.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜.sln을 찾을 수 없습니다.");
    }
}
