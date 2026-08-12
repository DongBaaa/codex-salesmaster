using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobileSingletonPermissionBoundaryTests
{
    [Fact]
    public void SettingsConnectionWrites_HoldCurrentOwnerLeaseAndRecheckPermission()
    {
        var source = ReadMobileViewModel("SettingsViewModel.cs");

        Assert.Contains(
            "CommitConnectionSettingIfAuthorizedAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AcquireOwnerCommitLeaseAsync(expectedOwner)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!snapshot.CanEditSettings)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "() => _settings.SaveBaseUrlAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_settings.ResetBaseUrlAsync",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (StaleMobileSessionOwnerException)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrityReportSingleton_DropsOldOwnerCompletionAndClearsVisibleData()
    {
        var source = ReadMobileViewModel(
            "IntegrityReportViewModel.cs");

        Assert.Contains(
            "new MobileOwnerOperationGate(sessionStore)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var operation = _ownerOperations.TryBegin(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!_ownerOperations.CanCommit(operation))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_ownerOperations.Complete(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void ResetForOwner()",
            source,
            StringComparison.Ordinal);

        var reset = ExtractMethod(
            source,
            "private void ResetForOwner()");
        Assert.Contains(
            "ClearReport();",
            reset,
            StringComparison.Ordinal);
        Assert.Contains(
            "CanViewIntegrityReport = false;",
            reset,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HomeAndSyncSingletons_DoNotPublishOldOwnerState()
    {
        var home = ReadMobileViewModel("HomeViewModel.cs");
        Assert.Contains(
            "Interlocked.Increment(ref _refreshVersion)",
            home,
            StringComparison.Ordinal);
        Assert.Contains(
            "_syncStateStore.LoadAsync(owner)",
            home,
            StringComparison.Ordinal);
        Assert.Contains(
            "!_sessionStore.IsOwnerCurrent(owner)",
            home,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void ResetForOwner()",
            home,
            StringComparison.Ordinal);

        var sync = ReadMobileViewModel("SyncViewModel.cs");
        Assert.Contains(
            "new MobileOwnerOperationGate(sessionStore)",
            sync,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!_ownerOperations.CanCommit(operation))",
            sync,
            StringComparison.Ordinal);
        Assert.Contains(
            "_ownerOperations.Complete(",
            sync,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void ResetForOwner()",
            sync,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecycleBinSingleton_NewOwnerCannotSeeOrMutateOldOwnerRows()
    {
        var source = ReadMobileViewModel(
            "RecycleBinViewModel.cs");

        Assert.Contains(
            "new MobileOwnerOperationGate(sessionStore)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var currentEntry = Entries.FirstOrDefault(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (_ownerOperations.CanCommit(operation))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_ownerOperations.Complete(",
            source,
            StringComparison.Ordinal);

        var reset = ExtractMethod(
            source,
            "private void ClearStaleOwnerView()");
        Assert.Contains(
            "ReplaceEntries([]);",
            reset,
            StringComparison.Ordinal);
        Assert.Contains(
            "SearchText = string.Empty;",
            reset,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedKind = string.Empty;",
            reset,
            StringComparison.Ordinal);
    }

    private static string ReadMobileViewModel(string fileName)
        => File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            fileName));

    private static string ExtractMethod(
        string source,
        string signature)
    {
        var start = source.IndexOf(
            signature,
            StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method not found: {signature}");

        var openingBrace = source.IndexOf('{', start);
        Assert.True(openingBrace >= 0);
        var depth = 0;
        for (var index = openingBrace;
             index < source.Length;
             index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}')
                depth--;

            if (depth == 0)
            {
                return source.Substring(
                    start,
                    index - start + 1);
            }
        }

        throw new InvalidDataException(
            $"Method body did not terminate: {signature}");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(
            AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(
                    Path.Combine(current.FullName, "Mobile")) &&
                Directory.Exists(
                    Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Repository root was not found.");
    }
}
