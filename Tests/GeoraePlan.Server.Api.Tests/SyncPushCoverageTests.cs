using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class SyncPushCoverageTests
{
    [Fact]
    public void SyncPushRequest_CollectionsAreAllCoveredByPermissionAndNormalizationGates()
    {
        var requestCollectionNames = typeof(SyncPushRequest)
            .GetProperties()
            .Where(property =>
                property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(requestCollectionNames);

        var source = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "SyncController.cs");
        var permissionGateSource = ExtractMethodBlock(source, "private string? ValidatePushPermissions");
        var normalizeSource = ExtractMethodBlock(source, "private static void NormalizePushRequest");

        foreach (var collectionName in requestCollectionNames)
        {
            Assert.Contains(
                $"request.{collectionName}",
                permissionGateSource,
                StringComparison.Ordinal);
            Assert.Contains(
                $"request.{collectionName} = RemoveNullEntries(request.{collectionName});",
                normalizeSource,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SyncPushPermissionGate_CoversWriteCategoriesWithExpectedPolicies()
    {
        var source = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "SyncController.cs");
        var permissionGateSource = ExtractMethodBlock(source, "private string? ValidatePushPermissions");

        Assert.Contains("PermissionNames.CompanyProfileEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.SettingsEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.CustomerEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.ItemEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.InvoiceEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.PaymentEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.DeliveryEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.RentalSettingsEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.RentalProfileEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.RentalAssetEdit", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("PermissionNames.RentalEditAll", permissionGateSource, StringComparison.Ordinal);
        Assert.Contains("현재 계정 권한으로 서버 동기화 반영이 허용되지 않는 변경이 포함되어 있습니다", permissionGateSource, StringComparison.Ordinal);
    }

    private static string ExtractMethodBlock(string source, string methodSignature)
    {
        var signatureIndex = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Method was not found: {methodSignature}");

        var openBraceIndex = source.IndexOf('{', signatureIndex);
        Assert.True(openBraceIndex >= 0, $"Method body was not found: {methodSignature}");

        var depth = 0;
        for (var index = openBraceIndex; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return source[signatureIndex..(index + 1)];
                    break;
            }
        }

        throw new InvalidOperationException($"Method body was not closed: {methodSignature}");
    }

    private static string ReadRepositoryFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. pathParts]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Server")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Shared")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
