using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalLoadingResponsivenessTests
{
    [Fact]
    public void RentalStateService_HotReadPaths_KeepHeavyContinuationsOffUiContext()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "RentalStateService.cs"));

        Assert.Contains("BuildBillingProfileRowsAsync(profiles, session, offices, filter.ReferenceDate, filter.IncludeHistoryRows, ct).ConfigureAwait(false)", source);
        Assert.Contains("LoadBillingRunReferencesAsync(runProfileScopes, ct).ConfigureAwait(false)", source);
        Assert.Contains("NormalizeAssetCustomerDisplayNamesAsync(assets, ct).ConfigureAwait(false)", source);
        Assert.Contains("LoadAssetSearchResultAssetsAsync(", source);
        Assert.Contains("ConfigureAwait(false)", ExtractMethod(source, "LoadAssetSearchResultAssetsAsync"));
        Assert.Contains("NormalizeAssetCustomerDisplayNamesAsync(candidateAssets, ct).ConfigureAwait(false)", source);
        Assert.Contains("GetBillingProfileDisplayTextMapAsync(profileIds, session, ct).ConfigureAwait(false)", source);
        Assert.Contains("LoadDashboardReviewCustomerCandidatesAsync(", source);
        Assert.Contains("ConfigureAwait(false)", ExtractMethod(source, "LoadDashboardReviewCustomerCandidatesAsync"));
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var nameIndex = FindMethodDeclarationNameIndex(source, methodName);
        Assert.True(nameIndex >= 0, $"Cannot find method {methodName}");
        var bodyStart = source.IndexOf('{', nameIndex);
        Assert.True(bodyStart >= 0, $"Cannot find method body for {methodName}");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[bodyStart..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Cannot extract method body for {methodName}");
    }

    private static int FindMethodDeclarationNameIndex(string source, string methodName)
    {
        var searchStart = 0;
        while (searchStart < source.Length)
        {
            var index = source.IndexOf(methodName, searchStart, StringComparison.Ordinal);
            if (index < 0)
                return -1;

            var lineStart = source.LastIndexOf('\n', index);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            var lineEnd = source.IndexOf('\n', index);
            if (lineEnd < 0)
                lineEnd = source.Length;

            var line = source[lineStart..lineEnd].TrimStart();
            if ((line.StartsWith("public ", StringComparison.Ordinal) ||
                 line.StartsWith("private ", StringComparison.Ordinal) ||
                 line.StartsWith("internal ", StringComparison.Ordinal)) &&
                line.Contains("Task", StringComparison.Ordinal))
            {
                return index;
            }

            searchStart = index + methodName.Length;
        }

        return -1;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root could not be found.");
    }
}
