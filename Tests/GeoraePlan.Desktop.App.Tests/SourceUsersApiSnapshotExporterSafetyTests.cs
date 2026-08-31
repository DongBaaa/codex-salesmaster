using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SourceUsersApiSnapshotExporterSafetyTests
{
    [Fact]
    public void StoredCredentialEnvelope_PreservesUtcTimestampAsText()
    {
        var source = File.ReadAllText(FindExporter());

        Assert.Contains(
            "$envelope = $line | ConvertFrom-Json -DateKind String",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationFunctionExtraction_IncludesAclDependency()
    {
        var source = File.ReadAllText(FindExporter());

        Assert.Contains(
            "'Get-SourceUsersSnapshotFileSystemAcl'",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.IndexOf(
                "'Get-SourceUsersSnapshotFileSystemAcl'",
                StringComparison.Ordinal) <
            source.IndexOf(
                "'Assert-SourceUsersSnapshotAcl'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void PromptedCredential_IsCollectedInTheVisibleConsoleAndPasswordIsMasked()
    {
        var source = File.ReadAllText(FindExporter());
        var functionStart = source.IndexOf(
            "function Get-PromptedSystemAdminCredential {",
            StringComparison.Ordinal);
        var functionEnd = source.IndexOf(
            "function Get-SourceUsersViaApi {",
            functionStart,
            StringComparison.Ordinal);

        Assert.True(functionStart >= 0);
        Assert.True(functionEnd > functionStart);

        var function = source[functionStart..functionEnd];
        Assert.Contains(
            "$username = Read-Host -Prompt '거래플랜 시스템 관리자 아이디'",
            function,
            StringComparison.Ordinal);
        Assert.Contains(
            "$password = Read-Host -Prompt '거래플랜 시스템 관리자 비밀번호' -AsSecureString",
            function,
            StringComparison.Ordinal);
        Assert.Contains(
            "New-Object Management.Automation.PSCredential($username, $password)",
            function,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Credential", function, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertTo-SecureString", function, StringComparison.Ordinal);
        Assert.DoesNotContain("PtrToString", function, StringComparison.Ordinal);
    }

    private static string FindExporter()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "tools",
                "maintenance",
                "Export-GeoraePlanSourceUsersSnapshot.ps1");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
