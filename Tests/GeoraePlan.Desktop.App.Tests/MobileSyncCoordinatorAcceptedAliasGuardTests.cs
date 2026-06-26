using System.Text.RegularExpressions;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobileSyncCoordinatorAcceptedAliasGuardTests
{
    [Fact]
    public void MobileSyncCoordinator_RemovesAcceptedRentalBillingProfilePendingMutation_WhenServerReturnsAliasAcceptedRevision()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));

        Assert.Contains(
            "RemoveAccepted(pendingPush.RentalBillingProfiles, acceptedRevisions, RentalBillingProfileEntityName);",
            source,
            StringComparison.Ordinal);

        Assert.Matches(
            new Regex(
                @"else\s*\{\s*RemoveAcceptedPendingMutations\(pendingPush,\s*result\.AcceptedRevisions\);\s*\}",
                RegexOptions.Singleline),
            source);

        Assert.Matches(
            new Regex(
                @"private\s+static\s+void\s+RemoveAccepted<[^>]+>\s*\([^)]*List<[^>]+>\s+pending,[^)]*IReadOnlyCollection<SyncAcceptedRevisionDto>\s+acceptedRevisions,[^)]*string\s+entityName\)[^{]*\{[^}]*pending\.RemoveAll\(entity\s*=>\s*WasAccepted\(acceptedRevisions,\s*entityName,\s*entity\.Id\)\);[^}]*\}",
                RegexOptions.Singleline),
            source);

        Assert.Matches(
            new Regex(
                @"revision\.EntityId\s*==\s*entityId\s*&&\s*string\.Equals\(revision\.EntityName,\s*entityName,\s*StringComparison\.OrdinalIgnoreCase\)",
                RegexOptions.Singleline),
            source);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "거래플랜.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
