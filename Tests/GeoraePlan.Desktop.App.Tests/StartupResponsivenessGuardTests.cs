using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class StartupResponsivenessGuardTests
{
    [Fact]
    public void App_ShowsResponsiveLoadingWindowBeforeLocalDatabaseMaintenance()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(repositoryRoot, "Desktop", "거래플랜.Desktop.App");
        var appSource = File.ReadAllText(Path.Combine(appRoot, "App.xaml.cs"));
        var loadingWindowXaml = File.ReadAllText(Path.Combine(appRoot, "Views", "StartupLoadingWindow.xaml"));

        Assert.Contains("await RunPreLoginInitializationAsync();", appSource, StringComparison.Ordinal);
        Assert.Contains("var loadingWindow = new StartupLoadingWindow();", appSource, StringComparison.Ordinal);
        Assert.Contains("loadingWindow.Show();", appSource, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.ApplicationIdle", appSource, StringComparison.Ordinal);
        Assert.Contains("() => Task.Run(async () =>", appSource, StringComparison.Ordinal);
        Assert.True(
            appSource.IndexOf("loadingWindow.Show();", StringComparison.Ordinal) <
            appSource.IndexOf("() => Task.Run(async () =>", StringComparison.Ordinal),
            "시작 상태창은 로컬 DB 백그라운드 정비보다 먼저 표시되어야 합니다.");

        Assert.Contains("Title=\"거래플랜 - 시작 중\"", loadingWindowXaml, StringComparison.Ordinal);
        Assert.Contains("IsIndeterminate=\"True\"", loadingWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar=\"True\"", loadingWindowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PostLoginSync_ReloadsAgainOnlyWhenBusinessScopeRefreshActuallyRuns()
    {
        var repositoryRoot = FindRepositoryRoot();
        var mainViewModelSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "MainViewModel.cs"));
        var methodBody = ExtractSourceSection(
            mainViewModelSource,
            "public async Task RunPostLoginSyncAsync()",
            "private async Task<bool> ShouldSkipImmediatePostLoginSyncAsync()");

        Assert.Contains("var currentBusinessScopeRefreshAttempted = false;", methodBody, StringComparison.Ordinal);
        Assert.Contains("currentBusinessScopeRefreshAttempted = true;", methodBody, StringComparison.Ordinal);
        var normalizedMethodBody = methodBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(
            "if (currentBusinessScopeRefreshAttempted)\n                    await ReloadAfterPassiveSyncAsync();",
            normalizedMethodBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PostLoginIntegrity_UsesIsolatedBackgroundScopeAndOnlyDispatchesStatusToUi()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "App.xaml.cs"));
        var completionBody = ExtractSourceSection(
            appSource,
            "private static async Task CompletePostLoginSyncAndIntegrityAsync(",
            "private static Task<string?> RunPostLoginIntegrityChecksInBackgroundAsync(");
        var backgroundBody = ExtractSourceSection(
            appSource,
            "private static Task<string?> RunPostLoginIntegrityChecksInBackgroundAsync(",
            "private static void CloseStartupSyncPopup(");

        Assert.DoesNotContain("ReloadAfterPassiveSyncAsync", completionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("RunDataIntegrityScanAndPromptAsync", completionBody, StringComparison.Ordinal);
        Assert.Contains("Task.Run(async () =>", backgroundBody, StringComparison.Ordinal);
        Assert.Contains("scopeFactory.CreateAsyncScope()", backgroundBody, StringComparison.Ordinal);
        Assert.Contains("scopedProvider.GetRequiredService<DataIntegrityIssueService>()", backgroundBody, StringComparison.Ordinal);
        Assert.Contains("mainWin.Dispatcher.InvokeAsync", completionBody, StringComparison.Ordinal);
    }

    private static string ExtractSourceSection(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"시작 마커를 찾지 못했습니다: {startMarker}");

        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"종료 마커를 찾지 못했습니다: {endMarker}");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }
}
