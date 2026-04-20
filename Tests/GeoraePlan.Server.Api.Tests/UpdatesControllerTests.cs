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
}
