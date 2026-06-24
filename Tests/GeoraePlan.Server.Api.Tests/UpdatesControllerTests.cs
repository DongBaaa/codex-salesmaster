using System.Text.Json;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class UpdatesControllerTests : IDisposable
{
    private readonly string _storageRoot;

    public UpdatesControllerTests()
    {
        _storageRoot = Path.Combine(Path.GetTempPath(), "georaeplan-updates-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_storageRoot, "manifest"));
        Directory.CreateDirectory(Path.Combine(_storageRoot, "downloads", "desktop"));
    }

    [Fact]
    public async Task GetManifestAsync_PopulatesMinimumSupportedVersion_ForMandatoryDesktopPackage()
    {
        const string version = "1.1.115";
        const string fileName = "tradeplan-pc-installer-v1.1.115.zip";

        var manifest = new AppUpdateManifestDto
        {
            Channel = "stable",
            Desktop = new AppUpdatePackageDto
            {
                Platform = "desktop",
                Version = version,
                Mandatory = true,
                FileName = fileName,
                Sha256 = "ABCDEF",
                FileSize = 1234,
                Notes = "test"
            }
        };

        await WriteManifestAsync("stable", manifest);
        var controller = CreateController();

        var response = await controller.GetManifestAsync("stable", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<AppUpdateManifestDto>(ok.Value);

        Assert.NotNull(payload.Desktop);
        Assert.Equal(version, payload.Desktop!.MinimumSupportedVersion);
        Assert.Equal($"https://updates.example.com/updates/download/desktop/{Uri.EscapeDataString(fileName)}", payload.Desktop.PackageUrl);
    }

    [Fact]
    public async Task GetManifestAsync_ExpandsRootRelativePackageUrl_UsingCurrentRequestHost()
    {
        var manifest = new AppUpdateManifestDto
        {
            Channel = "stable",
            Desktop = new AppUpdatePackageDto
            {
                Platform = "desktop",
                Version = "1.1.115",
                Mandatory = false,
                FileName = "package.zip",
                PackageUrl = "/updates/download/desktop/package.zip",
                Sha256 = "ABCDEF",
                FileSize = 4321,
                Notes = "test"
            }
        };

        await WriteManifestAsync("stable", manifest);
        var controller = CreateController();

        var response = await controller.GetManifestAsync("stable", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<AppUpdateManifestDto>(ok.Value);

        Assert.NotNull(payload.Desktop);
        Assert.Equal("https://updates.example.com/updates/download/desktop/package.zip", payload.Desktop!.PackageUrl);
        Assert.Equal(string.Empty, payload.Desktop.MinimumSupportedVersion);
    }

    [Fact]
    public async Task GetManifestAsync_RewritesRootRelativePackageUrl_WhenPlatformPathDoesNotMatchPackage()
    {
        var manifest = new AppUpdateManifestDto
        {
            Channel = "stable",
            Desktop = new AppUpdatePackageDto
            {
                Platform = "desktop",
                Version = "1.1.115",
                Mandatory = false,
                FileName = "tradeplan-pc-installer-v1.1.115.zip",
                PackageUrl = "/updates/download/android/tradeplan-android-v0.2.65.apk",
                Sha256 = "ABCDEF",
                FileSize = 4321,
                Notes = "test"
            }
        };

        await WriteManifestAsync("stable", manifest);
        var controller = CreateController();

        var response = await controller.GetManifestAsync("stable", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<AppUpdateManifestDto>(ok.Value);

        Assert.NotNull(payload.Desktop);
        Assert.Equal(
            "https://updates.example.com/updates/download/desktop/tradeplan-pc-installer-v1.1.115.zip",
            payload.Desktop!.PackageUrl);
    }

    [Fact]
    public async Task GetManifestAsync_RewritesRootRelativePackageUrl_WhenFileNameContainsEncodedSlash()
    {
        var manifest = new AppUpdateManifestDto
        {
            Channel = "stable",
            Desktop = new AppUpdatePackageDto
            {
                Platform = "desktop",
                Version = "1.1.115",
                Mandatory = false,
                FileName = "package.zip",
                PackageUrl = "/updates/download/desktop/%2e%2e%2fpackage.zip",
                Sha256 = "ABCDEF",
                FileSize = 4321,
                Notes = "test"
            }
        };

        await WriteManifestAsync("stable", manifest);
        var controller = CreateController();

        var response = await controller.GetManifestAsync("stable", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<AppUpdateManifestDto>(ok.Value);

        Assert.NotNull(payload.Desktop);
        Assert.Equal("https://updates.example.com/updates/download/desktop/package.zip", payload.Desktop!.PackageUrl);
    }

    [Fact]
    public async Task GetManifestAsync_RewritesNonHttpAbsolutePackageUrl_ToSafeServerDownloadUrl()
    {
        var manifest = new AppUpdateManifestDto
        {
            Channel = "stable",
            Desktop = new AppUpdatePackageDto
            {
                Platform = "desktop",
                Version = "1.1.115",
                Mandatory = false,
                FileName = "package.zip",
                PackageUrl = "file:///tmp/package.zip",
                Sha256 = "ABCDEF",
                FileSize = 4321,
                Notes = "test"
            }
        };

        await WriteManifestAsync("stable", manifest);
        var controller = CreateController();

        var response = await controller.GetManifestAsync("stable", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<AppUpdateManifestDto>(ok.Value);

        Assert.NotNull(payload.Desktop);
        Assert.Equal("https://updates.example.com/updates/download/desktop/package.zip", payload.Desktop!.PackageUrl);
    }

    [Fact]
    public async Task GetManifestAsync_RewritesExternalHttpsPackageUrl_ToCurrentHostDownloadUrl()
    {
        var manifest = new AppUpdateManifestDto
        {
            Channel = "stable",
            Desktop = new AppUpdatePackageDto
            {
                Platform = "desktop",
                Version = "1.1.115",
                Mandatory = false,
                FileName = "package.zip",
                PackageUrl = "https://downloads.example.invalid/package.zip",
                Sha256 = "ABCDEF",
                FileSize = 4321,
                Notes = "test"
            }
        };

        await WriteManifestAsync("stable", manifest);
        var controller = CreateController();

        var response = await controller.GetManifestAsync("stable", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<AppUpdateManifestDto>(ok.Value);

        Assert.NotNull(payload.Desktop);
        Assert.Equal("https://updates.example.com/updates/download/desktop/package.zip", payload.Desktop!.PackageUrl);
    }

    [Fact]
    public async Task GetManifestAsync_PreservesSameHostDownloadPackageUrl()
    {
        const string packageUrl = "https://updates.example.com/updates/download/desktop/package.zip";
        var manifest = new AppUpdateManifestDto
        {
            Channel = "stable",
            Desktop = new AppUpdatePackageDto
            {
                Platform = "desktop",
                Version = "1.1.115",
                Mandatory = false,
                FileName = "package.zip",
                PackageUrl = packageUrl,
                Sha256 = "ABCDEF",
                FileSize = 4321,
                Notes = "test"
            }
        };

        await WriteManifestAsync("stable", manifest);
        var controller = CreateController();

        var response = await controller.GetManifestAsync("stable", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<AppUpdateManifestDto>(ok.Value);

        Assert.NotNull(payload.Desktop);
        Assert.Equal(packageUrl, payload.Desktop!.PackageUrl);
    }

    [Fact]
    public void UpdatesController_ChecksRootRelativePackageUrlBeforeAbsoluteUriParsing()
    {
        var source = ReadUpdatesControllerSource();

        Assert.Contains("packageUrl.StartsWith(\"/\", StringComparison.Ordinal)", source, StringComparison.Ordinal);
        Assert.Contains("Uri.TryCreate(packageUrl, UriKind.Absolute, out var absolutePackageUri)", source, StringComparison.Ordinal);
        Assert.Contains("IsAllowedAbsolutePackageUri(absolutePackageUri, platform)", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "packageUrl.StartsWith(\"/\", StringComparison.Ordinal)",
            "Uri.TryCreate(packageUrl, UriKind.Absolute, out var absolutePackageUri)");
    }

    [Fact]
    public void HeadPackage_ReturnsHeaders_ForExistingDesktopPackage()
    {
        const string fileName = "package.zip";
        var packagePath = Path.Combine(_storageRoot, "downloads", "desktop", fileName);
        File.WriteAllBytes(packagePath, [1, 2, 3, 4]);
        var controller = CreateController();

        var result = controller.HeadPackage("desktop", fileName);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.Equal("application/zip", controller.Response.ContentType);
        Assert.Equal(4, controller.Response.ContentLength);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal(fileName, Uri.UnescapeDataString(controller.Response.Headers["X-Update-FileName"].ToString()));
    }

    [Fact]
    public void HeadPackage_ReturnsNotFound_ForPathTraversalFileName()
    {
        var controller = CreateController();

        var result = controller.HeadPackage("desktop", "../package.zip");

        Assert.IsType<NotFoundResult>(result);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_storageRoot))
                Directory.Delete(_storageRoot, recursive: true);
        }
        catch
        {
            // ignore temp cleanup failures
        }
    }

    private UpdatesController CreateController()
    {
        var controller = new UpdatesController(Options.Create(new UpdateOptions
        {
            StorageRoot = _storageRoot
        }));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.ControllerContext.HttpContext.Request.Scheme = "https";
        controller.ControllerContext.HttpContext.Request.Host = new HostString("updates.example.com");
        return controller;
    }

    private async Task WriteManifestAsync(string channel, AppUpdateManifestDto manifest)
    {
        var manifestPath = Path.Combine(_storageRoot, "manifest", channel + ".json");
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllTextAsync(manifestPath, json);
    }

    private static string ReadUpdatesControllerSource()
    {
        var root = FindRepositoryRoot();
        var serverRoot = Path.Combine(root, "Server");
        var apiDirectory = Directory.EnumerateDirectories(serverRoot, "*.Server.Api").Single();
        return File.ReadAllText(Path.Combine(apiDirectory, "Controllers", "UpdatesController.cs"));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Server")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void AssertInOrder(string source, params string[] fragments)
    {
        var index = -1;
        foreach (var fragment in fragments)
        {
            var nextIndex = source.IndexOf(fragment, index + 1, StringComparison.Ordinal);
            Assert.True(nextIndex >= 0, $"Fragment not found after index {index}: {fragment}");
            index = nextIndex;
        }
    }
}
