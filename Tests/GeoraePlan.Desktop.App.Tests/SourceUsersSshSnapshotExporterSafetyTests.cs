using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SourceUsersSshSnapshotExporterSafetyTests
{
    [Fact]
    public void Exporter_UsesPowerShellCoreCompatibleAclHelpers()
    {
        var source = File.ReadAllText(FindExporter());
        var preparationSource = File.ReadAllText(FindPreparationScript());

        Assert.Contains(
            "'Get-SourceUsersSnapshotFileSystemAcl'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'Set-SourceUsersSnapshotDirectoryAcl'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Set-SourceUsersSnapshotDirectoryAcl `",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$acl = Get-SourceUsersSnapshotFileSystemAcl `",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Set-Acl -LiteralPath $Path -AclObject $acl",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$acl = Get-Acl -LiteralPath $Path",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new DirectoryInfo(fullPath).GetAccessControl(",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new FileInfo(path).GetAccessControl(",
            preparationSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Directory.GetAccessControl(",
            preparationSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "File.GetAccessControl(",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ConvertFrom-Json -DateKind String",
            preparationSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Exporter_RequiresProtectedAclBeforeHealthOrSshAccess()
    {
        var source = File.ReadAllText(FindExporter());
        var importIndex = source.IndexOf(
            ". ([ScriptBlock]::Create($validationFunctionSource))",
            StringComparison.Ordinal);
        var aclIndex = source.IndexOf(
            "Assert-SourceUsersSnapshotAcl `",
            importIndex,
            StringComparison.Ordinal);
        var healthIndex = source.IndexOf(
            "$health = Invoke-WebRequest `",
            StringComparison.Ordinal);
        var sshIndex = source.IndexOf(
            "$transportText = Invoke-RemoteSnapshotQuery `",
            StringComparison.Ordinal);

        Assert.True(importIndex >= 0);
        Assert.True(aclIndex > importIndex);
        Assert.True(healthIndex > aclIndex);
        Assert.True(sshIndex > healthIndex);
        Assert.Contains("-Path $OutputDirectory `", source, StringComparison.Ordinal);
        Assert.Contains("-AllowedRoot $OutputDirectory", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Exporter_VerifiesBoundedFailureCleanupAndPreservesPrimaryCause()
    {
        var source = File.ReadAllText(FindExporter());
        var cleanupStart = source.IndexOf(
            "function Remove-OwnedSnapshotFile {",
            StringComparison.Ordinal);
        Assert.True(cleanupStart >= 0);
        var cleanupEnd = source.IndexOf(
            "function Get-ReadOnlySnapshotSql {",
            cleanupStart,
            StringComparison.Ordinal);

        Assert.True(cleanupEnd > cleanupStart);
        var cleanup = source[cleanupStart..cleanupEnd];
        Assert.Contains(
            "^source-users-\\d{8}-\\d{6}-[0-9a-f]{32}\\.json(?:\\.tmp)?$",
            cleanup,
            StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Delete($fullPath)", cleanup, StringComparison.Ordinal);
        Assert.Contains("if (Test-Path -LiteralPath $fullPath)", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("SilentlyContinue", cleanup, StringComparison.Ordinal);
        Assert.Contains("Cleanup verification failed:", source, StringComparison.Ordinal);
        Assert.Contains("$primaryError.Exception.Message", source, StringComparison.Ordinal);
    }

    private static string FindExporter()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "tools",
                "linux",
                "Export-GeoraeplanUserPermissionSnapshot.ps1");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private static string FindPreparationScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "테스트 시행",
                "테스트-환경-준비.ps1");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
