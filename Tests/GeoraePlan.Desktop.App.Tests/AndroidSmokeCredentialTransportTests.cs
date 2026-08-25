using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AndroidSmokeCredentialTransportTests
{
    [Fact]
    public void LoginCredentialsHaveNoDefaultsAndTextUsesAdbStandardInput()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidSmoke.ps1"));
        var textInput = ExtractBetween(
            source,
            "function Invoke-AdbShellTextInput {",
            "function Set-MobileDiagnosticFault {");
        var slowInput = ExtractBetween(
            source,
            "function Set-AndroidTextSlow {",
            "function Clear-AndroidTextField {");

        Assert.Contains("[string]$Username = ''", source, StringComparison.Ordinal);
        Assert.Contains("[string]$Password = ''", source, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardInput = $true", textInput, StringComparison.Ordinal);
        Assert.Contains("$process.StandardInput.AutoFlush = $true", textInput, StringComparison.Ordinal);
        Assert.Contains("$process.StandardInput.WriteLine('input text ' + $quoted)", textInput, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Milliseconds 60", textInput, StringComparison.Ordinal);
        Assert.Contains("Invoke-AdbShellTextInput", slowInput, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Adb -AdbPath", slowInput, StringComparison.Ordinal);
        Assert.Contains("로그인 화면에는 명시적 자격 증명이 필요합니다.", source, StringComparison.Ordinal);
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tools", "mobile")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Tests")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
