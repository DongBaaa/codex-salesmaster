using System.Text.RegularExpressions;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class ControllerAuthorizationGuardTests
{
    [Fact]
    public void RecycleBinHttpActions_RequireBackupRestorePolicy()
    {
        var violations = typeof(RecycleBinController)
            .GetMethods()
            .Where(method => method.GetCustomAttributes(typeof(HttpMethodAttribute), inherit: true).Any())
            .Where(method => !method
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
                .Any(attribute => string.Equals(attribute.Policy, PermissionNames.DataBackupRestore, StringComparison.Ordinal)))
            .Select(method => method.Name)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Recycle-bin read/restore/purge endpoints expose deleted business data and must require Data.BackupRestore: "
            + string.Join(", ", violations));
    }

    [Fact]
    public void MutatingControllerActions_DeclareAuthorizationPolicyOrExplicitSafeException()
    {
        var controllerDirectory = FindRepositoryRoot()
            .GetDirectories("Server")
            .Single()
            .GetDirectories("*.Server.Api")
            .Single()
            .GetDirectories("Controllers")
            .Single();

        var violations = new List<string>();
        foreach (var file in controllerDirectory.GetFiles("*.cs").OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            var lines = File.ReadAllLines(file.FullName);
            var isAdminOnlyController = lines.Take(30).Any(line =>
                line.Contains("[Authorize(Policy = \"AdminOrGod\")]", StringComparison.Ordinal));

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (!Regex.IsMatch(line, @"^\[Http(Post|Put|Delete|Patch)"))
                    continue;

                var attributeWindow = string.Join(
                    "\n",
                    lines.Skip(Math.Max(0, index - 2)).Take(8).Select(current => current.Trim()));

                if (IsExplicitSafeException(file.Name, line, isAdminOnlyController, attributeWindow, lines))
                    continue;

                if (!attributeWindow.Contains("[Authorize(Policy = PermissionNames.", StringComparison.Ordinal))
                    violations.Add($"{file.Name}:{index + 1} {line}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Mutating controller actions must declare an explicit business authorization policy or be listed as a reviewed exception:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static bool IsExplicitSafeException(
        string fileName,
        string httpAttributeLine,
        bool isAdminOnlyController,
        string attributeWindow,
        IReadOnlyList<string> lines)
    {
        if (isAdminOnlyController)
            return true;

        if (fileName.Equals("AuthController.cs", StringComparison.OrdinalIgnoreCase))
            return attributeWindow.Contains("[AllowAnonymous]", StringComparison.Ordinal) ||
                   attributeWindow.Contains("[Authorize]", StringComparison.Ordinal);

        if (fileName.Equals("RuntimeEditSessionsController.cs", StringComparison.OrdinalIgnoreCase))
            return true;

        if (fileName.Equals("SyncController.cs", StringComparison.OrdinalIgnoreCase) &&
            httpAttributeLine.Contains("\"push\"", StringComparison.Ordinal))
        {
            return lines.Any(line => line.Contains("ValidatePushPermissions", StringComparison.Ordinal));
        }

        return false;
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "거래플랜.sln")))
                return directory;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜.sln을 찾을 수 없습니다.");
    }
}
