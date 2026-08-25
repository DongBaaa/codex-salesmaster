using System.Runtime.CompilerServices;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class EnvironmentSettingsBackupResponsivenessTests
{
    [Fact]
    public async Task BackupSnapshotEnumeration_IsDispatchedBeforeTheUiCollectionIsUpdated()
    {
        var repositoryRoot = GetRepositoryRoot();
        var serviceSource = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "BackupService.cs"));
        var viewModelSource = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "EnvironmentSettingsViewModel.Backup.cs"));

        var asyncListMethod = GetRequiredBlock(
            serviceSource,
            "public Task<IReadOnlyList<BackupSnapshotInfo>> GetBackupSnapshotsAsync(",
            "public IReadOnlyList<BackupSnapshotInfo> GetBackupSnapshots()");
        Assert.Contains("RunBackupWorkOffUiThreadAsync", asyncListMethod, StringComparison.Ordinal);
        Assert.Contains("GetBackupSnapshots()", asyncListMethod, StringComparison.Ordinal);

        var reloadMethod = GetRequiredBlock(
            viewModelSource,
            "private async Task ReloadBackupSnapshotsAsync()",
            "private async Task CreateBackupSnapshotAsync()");
        const string awaitedEnumeration = "var snapshots = await _backup.GetBackupSnapshotsAsync();";
        Assert.Contains(awaitedEnumeration, reloadMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_backup.GetBackupSnapshots()", reloadMethod, StringComparison.Ordinal);
        Assert.True(
            reloadMethod.IndexOf(awaitedEnumeration, StringComparison.Ordinal) <
            reloadMethod.IndexOf("BackupSnapshots.Clear();", StringComparison.Ordinal),
            "The previous visible list must remain intact until worker-thread verification finishes.");
    }

    private static string GetRequiredBlock(string source, string startToken, string endToken)
    {
        var start = source.IndexOf(startToken, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Required start token is missing: {startToken}");
        var end = source.IndexOf(endToken, start + startToken.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Required end token is missing: {endToken}");
        return source[start..end];
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        var root = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
        Assert.True(
            Directory.Exists(Path.Combine(root, "Desktop")) &&
            Directory.Exists(Path.Combine(root, "Tests")),
            "The repository root could not be resolved from the test source path.");
        return root;
    }
}
