using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task GetManifestAsync_UsesPointerSelectedImmutableGeneration()
    {
        const string generationId = "0123456789abcdef0123456789abcdef";
        var generationManifest = new AppUpdateManifestDto
        {
            Channel = "stable",
            GenerationId = generationId,
            Desktop = new AppUpdatePackageDto
            {
                Platform = "desktop",
                Version = "2.0.0",
                FileName = "generation.zip",
                Sha256 = new string('A', 64),
                FileSize = 123
            }
        };
        await WriteManifestAsync(
            "stable",
            new AppUpdateManifestDto
            {
                Channel = "stable",
                Desktop = new AppUpdatePackageDto
                {
                    Platform = "desktop",
                    Version = "1.0.0",
                    FileName = "legacy.zip",
                    Sha256 = new string('B', 64),
                    FileSize = 456
                }
            });
        await WritePointerGenerationAsync(
            "stable",
            generationId,
            generationManifest);

        var response = await CreateController().GetManifestAsync(
            "stable",
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<AppUpdateManifestDto>(ok.Value);
        Assert.Equal(generationId, payload.GenerationId);
        Assert.Equal("2.0.0", payload.Desktop?.Version);
    }

    [Fact]
    public async Task GetManifestAsync_ReturnsPreviousGenerationAfterPointerAwareRollback()
    {
        const string previousGenerationId =
            "0123456789abcdef0123456789abcdef";
        const string currentGenerationId =
            "fedcba9876543210fedcba9876543210";
        var projectRoot = Path.Combine(_storageRoot, "rollback-project");
        var deliveryRoot = Path.Combine(projectRoot, "\uBC30\uD3EC");
        var deliveryGenerationRoot = Path.Combine(
            deliveryRoot,
            ".georaeplan-release-generations",
            "stable");
        var stagedDeliveryGenerationRoot = Path.Combine(
            _storageRoot,
            "manifest",
            "delivery-generations",
            "stable");
        var runtimeGenerationRoot = Path.Combine(
            _storageRoot,
            "manifest",
            "generations",
            "stable");
        Directory.CreateDirectory(deliveryGenerationRoot);
        Directory.CreateDirectory(stagedDeliveryGenerationRoot);
        Directory.CreateDirectory(runtimeGenerationRoot);
        var desktopRoot = Path.Combine(_storageRoot, "downloads", "desktop");
        var previousPackagePath =
            Path.Combine(desktopRoot, "desktop-previous.zip");
        var currentPackagePath =
            Path.Combine(desktopRoot, "desktop-current.zip");
        await File.WriteAllTextAsync(previousPackagePath, "previous");
        await File.WriteAllTextAsync(currentPackagePath, "current");

        static AppUpdateManifestDto CreateManifest(
            string generationId,
            string version,
            string packagePath)
        {
            var packageBytes = File.ReadAllBytes(packagePath);
            return new AppUpdateManifestDto
            {
                Channel = "stable",
                GenerationId = generationId,
                Desktop = new AppUpdatePackageDto
                {
                    Platform = "desktop",
                    Version = version,
                    FileName = Path.GetFileName(packagePath),
                    Sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)),
                    FileSize = packageBytes.LongLength
                }
            };
        }

        var previousManifest = CreateManifest(
            previousGenerationId,
            "1.0.0",
            previousPackagePath);
        var currentManifest = CreateManifest(
            currentGenerationId,
            "2.0.0",
            currentPackagePath);
        var jsonOptions =
            new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var previousBytes =
            JsonSerializer.SerializeToUtf8Bytes(previousManifest, jsonOptions);
        var currentBytes =
            JsonSerializer.SerializeToUtf8Bytes(currentManifest, jsonOptions);
        var previousRuntimePath = Path.Combine(
            runtimeGenerationRoot,
            previousGenerationId + ".json");
        var currentRuntimePath = Path.Combine(
            runtimeGenerationRoot,
            currentGenerationId + ".json");
        var previousDeliveryPath = Path.Combine(
            deliveryGenerationRoot,
            previousGenerationId + ".json");
        var previousStagedDeliveryPath = Path.Combine(
            stagedDeliveryGenerationRoot,
            previousGenerationId + ".json");
        var currentDeliveryPath = Path.Combine(
            deliveryGenerationRoot,
            currentGenerationId + ".json");
        await File.WriteAllBytesAsync(previousRuntimePath, previousBytes);
        await File.WriteAllBytesAsync(currentRuntimePath, currentBytes);
        await File.WriteAllBytesAsync(
            previousStagedDeliveryPath,
            previousBytes);
        await File.WriteAllBytesAsync(currentDeliveryPath, currentBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(_storageRoot, "manifest", "stable.json"),
            currentBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(_storageRoot, "manifest", "stable.previous.json"),
            previousBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(deliveryRoot, "stable.json"),
            currentBytes);
        var currentHash =
            Convert.ToHexString(SHA256.HashData(currentBytes));
        var pointer = new Dictionary<string, string>
        {
            ["owner"] = "georaeplan-release-manifest-pointer",
            ["schemaVersion"] = "1",
            ["channel"] = "stable",
            ["generationId"] = currentGenerationId,
            ["manifestRelativePath"] =
                $"generations/stable/{currentGenerationId}.json",
            ["manifestSha256"] = currentHash,
            ["manifestFileSize"] = currentBytes.LongLength.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["deliveryManifestPath"] = currentDeliveryPath,
            ["deliveryManifestSha256"] = currentHash,
            ["deliveryManifestFileSize"] = currentBytes.LongLength.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        };
        await File.WriteAllBytesAsync(
            Path.Combine(
                _storageRoot,
                "manifest",
                "stable.current.json"),
            JsonSerializer.SerializeToUtf8Bytes(pointer));

        var rollbackScript = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        var rollback = await RunPowerShellAsync(
            rollbackScript,
            ("-ProjectRoot", projectRoot),
            ("-OutputRoot", _storageRoot),
            ("-Apply", null));

        Assert.True(
            rollback.ExitCode == 0,
            rollback.StdOut + Environment.NewLine + rollback.StdErr);
        Assert.Contains(
            $"rollback_manifest=SWAPPED generation={previousGenerationId}",
            rollback.StdOut,
            StringComparison.Ordinal);
        var response = await CreateController().GetManifestAsync(
            "stable",
            CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<AppUpdateManifestDto>(ok.Value);
        Assert.Equal(previousGenerationId, payload.GenerationId);
        Assert.Equal("1.0.0", payload.Desktop?.Version);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(previousBytes)),
            Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(previousDeliveryPath))));
    }

    [Fact]
    public async Task GetManifestAsync_FailsClosed_WhenPointerGenerationIsMissing()
    {
        const string generationId = "123456789abcdef0123456789abcdef0";
        await WriteManifestAsync(
            "stable",
            new AppUpdateManifestDto
            {
                Channel = "stable",
                Desktop = new AppUpdatePackageDto
                {
                    Platform = "desktop",
                    Version = "1.0.0",
                    FileName = "legacy.zip",
                    Sha256 = new string('A', 64),
                    FileSize = 1
                }
            });
        await WriteManifestPointerAsync(
            "stable",
            generationId,
            new string('B', 64),
            123);

        var response = await CreateController().GetManifestAsync(
            "stable",
            CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
    }

    [Fact]
    public async Task GetManifestAsync_FailsClosed_WhenPointerHashDoesNotMatch()
    {
        const string generationId = "23456789abcdef0123456789abcdef01";
        await WritePointerGenerationAsync(
            "stable",
            generationId,
            new AppUpdateManifestDto
            {
                Channel = "stable",
                GenerationId = generationId
            },
            pointerSha256: new string('F', 64));

        var response = await CreateController().GetManifestAsync(
            "stable",
            CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
    }

    [Fact]
    public async Task GetManifestAsync_FailsClosed_WhenGeneratedCompatibilityManifestHasNoPointer()
    {
        const string generationId = "3456789abcdef0123456789abcdef012";
        await WriteManifestAsync(
            "stable",
            new AppUpdateManifestDto
            {
                Channel = "stable",
                GenerationId = generationId
            });

        var response = await CreateController().GetManifestAsync(
            "stable",
            CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            unavailable.StatusCode);
    }

    [Fact]
    public async Task GetManifestAsync_FailsClosed_WhenLegacyGenerationIdIsWhitespace()
    {
        await WriteManifestAsync(
            "stable",
            new AppUpdateManifestDto
            {
                Channel = "stable",
                GenerationId = " "
            });

        var response = await CreateController().GetManifestAsync(
            "stable",
            CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            unavailable.StatusCode);
    }

    [Fact]
    public async Task GetManifestAsync_FailsClosed_WhenLegacyManifestChannelDoesNotMatch()
    {
        await WriteManifestAsync(
            "stable",
            new AppUpdateManifestDto
            {
                Channel = "beta"
            });

        var response = await CreateController().GetManifestAsync(
            "stable",
            CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            unavailable.StatusCode);
    }

    [Theory]
    [InlineData("beta", false)]
    [InlineData("beta", true)]
    [InlineData("test", false)]
    [InlineData("test", true)]
    public async Task GetManifestAsync_AllowsLegacyMissingOrEmptyChannel(
        string channel,
        bool includeEmptyChannel)
    {
        var manifestPath = Path.Combine(
            _storageRoot,
            "manifest",
            channel + ".json");
        var json = includeEmptyChannel
            ? """{"channel":"","desktop":null,"android":null}"""
            : """{"desktop":null,"android":null}""";
        await File.WriteAllTextAsync(manifestPath, json);

        var response = await CreateController().GetManifestAsync(
            channel,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<AppUpdateManifestDto>(ok.Value);
        Assert.Equal(channel, payload.Channel);
        Assert.Empty(payload.GenerationId);
    }

    [Fact]
    public async Task GetManifestAsync_FailsClosed_WhenPointerEvidencePairDiffers()
    {
        const string generationId = "456789abcdef0123456789abcdef0123";
        await WritePointerGenerationAsync(
            "stable",
            generationId,
            new AppUpdateManifestDto
            {
                Channel = "stable",
                GenerationId = generationId
            },
            deliverySha256: new string('E', 64));

        var response = await CreateController().GetManifestAsync(
            "stable",
            CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            unavailable.StatusCode);
    }

    [Fact]
    public async Task GetManifestAsync_FailsClosed_WhenSelectedGenerationChannelDiffers()
    {
        const string generationId = "56789abcdef0123456789abcdef01234";
        await WritePointerGenerationAsync(
            "stable",
            generationId,
            new AppUpdateManifestDto
            {
                Channel = "beta",
                GenerationId = generationId
            });

        var response = await CreateController().GetManifestAsync(
            "stable",
            CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            unavailable.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetManifestAsync_FailsClosed_WhenSelectedGenerationChannelIsMissingOrEmpty(
        bool includeEmptyChannel)
    {
        const string generationId =
            "6789abcdef0123456789abcdef012345";
        var generationDirectory = Path.Combine(
            _storageRoot,
            "manifest",
            "generations",
            "stable");
        Directory.CreateDirectory(generationDirectory);
        var json = includeEmptyChannel
            ? $$"""{"channel":"","generationId":"{{generationId}}"}"""
            : $$"""{"generationId":"{{generationId}}"}""";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        await File.WriteAllBytesAsync(
            Path.Combine(
                generationDirectory,
                generationId + ".json"),
            bytes);
        await WriteManifestPointerAsync(
            "stable",
            generationId,
            Convert.ToHexString(SHA256.HashData(bytes)),
            bytes.LongLength);

        var response = await CreateController().GetManifestAsync(
            "stable",
            CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            unavailable.StatusCode);
    }

    [Fact]
    public async Task GetManifestAsync_ConcurrentPointerReplacement_ReturnsWholeGenerationsOrTransientUnavailable()
    {
        const string firstGeneration = "3456789abcdef0123456789abcdef012";
        const string secondGeneration = "456789abcdef0123456789abcdef0123";
        var firstPointer = await WritePointerGenerationAsync(
            "stable",
            firstGeneration,
            new AppUpdateManifestDto
            {
                Channel = "stable",
                GenerationId = firstGeneration,
                Desktop = new AppUpdatePackageDto { Version = "3.0.0" }
            });
        var secondPointer = await WritePointerGenerationAsync(
            "stable",
            secondGeneration,
            new AppUpdateManifestDto
            {
                Channel = "stable",
                GenerationId = secondGeneration,
                Desktop = new AppUpdatePackageDto { Version = "4.0.0" }
            });
        var pointerPath = Path.Combine(
            _storageRoot,
            "manifest",
            "stable.current.json");
        await File.WriteAllBytesAsync(pointerPath, firstPointer);

        var readerTasks = Enumerable.Range(0, 4)
            .Select(async _ =>
            {
                var observed = new List<string>();
                for (var index = 0; index < 75; index++)
                {
                    var response = await CreateController().GetManifestAsync(
                        "stable",
                        CancellationToken.None);
                    if (response.Result is not OkObjectResult ok)
                    {
                        Assert.True(
                            response.Result is NotFoundObjectResult ||
                            response.Result is ObjectResult
                            {
                                StatusCode: StatusCodes.Status503ServiceUnavailable
                            },
                            $"Unexpected result type: {response.Result?.GetType().FullName ?? "<null>"}");
                        continue;
                    }

                    var manifest = Assert.IsType<AppUpdateManifestDto>(ok.Value);
                    Assert.Contains(
                        manifest.GenerationId,
                        new[] { firstGeneration, secondGeneration });
                    observed.Add(manifest.GenerationId);
                }
                return observed;
            })
            .ToArray();
        var writerTask = Task.Run(async () =>
        {
            for (var index = 0; index < 150; index++)
            {
                var pendingPath = pointerPath + "." + Guid.NewGuid().ToString("N");
                var backupPath = pointerPath + "." + Guid.NewGuid().ToString("N") + ".bak";
                await File.WriteAllBytesAsync(
                    pendingPath,
                    index % 2 == 0 ? secondPointer : firstPointer);
                try
                {
                    File.Replace(
                        pendingPath,
                        pointerPath,
                        backupPath,
                        ignoreMetadataErrors: true);
                }
                finally
                {
                    File.Delete(pendingPath);
                    File.Delete(backupPath);
                }
            }
        });

        await Task.WhenAll(readerTasks.Cast<Task>().Append(writerTask));
        Assert.NotEmpty(readerTasks.SelectMany(task => task.Result));
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
    public async Task GetManifestAsync_ExpandsDesktopNativeInstallerUrls_UsingCurrentRequestHost()
    {
        var manifest = new AppUpdateManifestDto
        {
            Channel = "stable",
            Desktop = new AppUpdatePackageDto
            {
                Platform = "desktop",
                Version = "1.1.683",
                FileName = "tradeplan-pc-installer-v1.1.683.zip",
                PackageUrl = "/updates/download/desktop/tradeplan-pc-installer-v1.1.683.zip",
                Installers =
                [
                    new AppUpdateInstallerDto
                    {
                        Audience = "user",
                        Format = "exe",
                        Version = "1.1.683",
                        FileName = "tradeplan-pc-setup-v1.1.683.exe",
                        PackageUrl = "/updates/download/desktop/tradeplan-pc-setup-v1.1.683.exe"
                    },
                    new AppUpdateInstallerDto
                    {
                        Audience = "administrator",
                        Format = "msi",
                        Version = "1.1.683",
                        FileName = "tradeplan-pc-admin-v1.1.683.msi",
                        PackageUrl = "/updates/download/desktop/tradeplan-pc-admin-v1.1.683.msi"
                    }
                ]
            }
        };

        await WriteManifestAsync("stable", manifest);
        var controller = CreateController();

        var response = await controller.GetManifestAsync("stable", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<AppUpdateManifestDto>(ok.Value);

        Assert.NotNull(payload.Desktop);
        Assert.Collection(
            payload.Desktop!.Installers,
            installer => Assert.Equal(
                "https://updates.example.com/updates/download/desktop/tradeplan-pc-setup-v1.1.683.exe",
                installer.PackageUrl),
            installer => Assert.Equal(
                "https://updates.example.com/updates/download/desktop/tradeplan-pc-admin-v1.1.683.msi",
                installer.PackageUrl));
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
        Assert.Equal("bytes", controller.Response.Headers.AcceptRanges.ToString());
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal(fileName, Uri.UnescapeDataString(controller.Response.Headers["X-Update-FileName"].ToString()));
    }

    [Fact]
    public void DownloadPackage_EnablesRangeProcessing_ForExistingDesktopPackage()
    {
        const string fileName = "package.zip";
        var packagePath = Path.Combine(_storageRoot, "downloads", "desktop", fileName);
        File.WriteAllBytes(packagePath, Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());
        var controller = CreateController();

        var result = Assert.IsType<FileStreamResult>(
            controller.DownloadPackage("desktop", fileName));
        using var stream = result.FileStream;

        Assert.True(result.EnableRangeProcessing);
        Assert.Equal("application/zip", result.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal(fileName, Uri.UnescapeDataString(controller.Response.Headers["X-Update-FileName"].ToString()));
    }

    [Theory]
    [InlineData("bytes=0-15", StatusCodes.Status206PartialContent, "bytes 0-15/32", 16)]
    [InlineData("bytes=32-", StatusCodes.Status416RangeNotSatisfiable, "bytes */32", 0)]
    public async Task DownloadPackage_ExecutesBoundedAndUnsatisfiedRanges(
        string range,
        int expectedStatusCode,
        string expectedContentRange,
        int expectedBodyLength)
    {
        const string fileName = "package.zip";
        var expectedBytes = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var packagePath = Path.Combine(_storageRoot, "downloads", "desktop", fileName);
        await File.WriteAllBytesAsync(packagePath, expectedBytes);
        var controller = CreateController();
        var httpContext = controller.ControllerContext.HttpContext;
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Headers.Range = range;
        httpContext.Response.Body = new MemoryStream();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IActionResultExecutor<FileStreamResult>, FileStreamResultExecutor>()
            .BuildServiceProvider();
        httpContext.RequestServices = services;

        var result = Assert.IsType<FileStreamResult>(
            controller.DownloadPackage("desktop", fileName));
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());
        await result.ExecuteResultAsync(actionContext);

        Assert.Equal(expectedStatusCode, httpContext.Response.StatusCode);
        Assert.Equal("bytes", httpContext.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(expectedContentRange, httpContext.Response.Headers.ContentRange.ToString());
        Assert.Equal(expectedBodyLength, httpContext.Response.Body.Length);
        if (expectedBodyLength > 0)
        {
            Assert.Equal(
                expectedBytes.Take(expectedBodyLength).ToArray(),
                ((MemoryStream)httpContext.Response.Body).ToArray());
        }
    }

    [Theory]
    [InlineData("tradeplan-pc-setup-v1.1.683.exe", "application/vnd.microsoft.portable-executable")]
    [InlineData("tradeplan-pc-admin-v1.1.683.msi", "application/x-msi")]
    public void HeadPackage_ReturnsNativeInstallerContentType(string fileName, string expectedContentType)
    {
        var packagePath = Path.Combine(_storageRoot, "downloads", "desktop", fileName);
        File.WriteAllBytes(packagePath, [1, 2, 3, 4]);
        var controller = CreateController();

        var result = controller.HeadPackage("desktop", fileName);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(expectedContentType, controller.Response.ContentType);
        Assert.Equal(4, controller.Response.ContentLength);
    }

    [Fact]
    public void HeadPackage_ReturnsNotFound_ForPathTraversalFileName()
    {
        var controller = CreateController();

        var result = controller.HeadPackage("desktop", "../package.zip");

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownloadEndpoints_ReturnServiceUnavailable_WhenPackageOpenRaces(
        bool headRequest)
    {
        const string fileName = "locked-package.zip";
        var packagePath = Path.Combine(
            _storageRoot,
            "downloads",
            "desktop",
            fileName);
        File.WriteAllBytes(packagePath, [1, 2, 3, 4]);
        using var exclusiveLease = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var controller = CreateController();

        var result = headRequest
            ? controller.HeadPackage("desktop", fileName)
            : controller.DownloadPackage("desktop", fileName);

        var unavailable = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            unavailable.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownloadEndpoints_ReturnNotFound_WhenPackageDisappears(
        bool headRequest)
    {
        const string fileName = "missing-package.zip";
        var controller = CreateController();

        var result = headRequest
            ? controller.HeadPackage("desktop", fileName)
            : controller.DownloadPackage("desktop", fileName);

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownloadEndpoints_RejectAncestorReparsePoint(
        bool headRequest)
    {
        const string fileName = "external-package.zip";
        var platformRoot = Path.Combine(
            _storageRoot,
            "downloads",
            "desktop");
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-updates-outside-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllBytes(
            Path.Combine(outsideRoot, fileName),
            [9, 9, 9, 9, 9]);
        Directory.Delete(platformRoot);
        CreateDirectoryLink(platformRoot, outsideRoot);
        try
        {
            var controller = CreateController();

            var result = headRequest
                ? controller.HeadPackage("desktop", fileName)
                : controller.DownloadPackage("desktop", fileName);

            var unavailable = Assert.IsType<StatusCodeResult>(result);
            Assert.Equal(
                StatusCodes.Status503ServiceUnavailable,
                unavailable.StatusCode);
        }
        finally
        {
            RemoveDirectoryEntry(platformRoot);
            Directory.CreateDirectory(platformRoot);
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownloadEndpoints_RejectLeafReparsePoint(
        bool headRequest)
    {
        const string fileName = "linked-package.zip";
        var linkPath = Path.Combine(
            _storageRoot,
            "downloads",
            "desktop",
            fileName);
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-updates-leaf-link-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        CreateDirectoryLink(linkPath, outsideRoot);
        try
        {
            var controller = CreateController();

            var result = headRequest
                ? controller.HeadPackage("desktop", fileName)
                : controller.DownloadPackage("desktop", fileName);

            var unavailable = Assert.IsType<StatusCodeResult>(result);
            Assert.Equal(
                StatusCodes.Status503ServiceUnavailable,
                unavailable.StatusCode);
        }
        finally
        {
            RemoveDirectoryEntry(linkPath);
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadEndpoints_NeverServeExternalBytesDuringReparseRace(
        bool headRequest)
    {
        const string fileName = "racing-package.zip";
        byte[] safeBytes = [1, 2, 3];
        byte[] externalBytes = Enumerable.Repeat((byte)9, 17).ToArray();
        var platformRoot = Path.Combine(
            _storageRoot,
            "downloads",
            "desktop");
        var safePackagePath = Path.Combine(platformRoot, fileName);
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-updates-reparse-race-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllBytes(
            Path.Combine(outsideRoot, fileName),
            externalBytes);
        File.WriteAllBytes(safePackagePath, safeBytes);

        try
        {
            var writer = Task.Run(async () =>
            {
                for (var index = 0; index < 12; index++)
                {
                    RetryPathMutation(() =>
                    {
                        RemoveDirectoryEntry(platformRoot);
                        CreateDirectoryLink(platformRoot, outsideRoot);
                    });
                    await Task.Yield();
                    RetryPathMutation(() =>
                    {
                        RemoveDirectoryEntry(platformRoot);
                        Directory.CreateDirectory(platformRoot);
                        File.WriteAllBytes(safePackagePath, safeBytes);
                    });
                }
            });
            var readers = Enumerable.Range(0, 2)
                .Select(async _ =>
                {
                    for (var index = 0; index < 80; index++)
                    {
                        var controller = CreateController();
                        var result = headRequest
                            ? controller.HeadPackage("desktop", fileName)
                            : controller.DownloadPackage(
                                "desktop",
                                fileName);
                        if (result is FileStreamResult fileResult)
                        {
                            await using var stream = fileResult.FileStream;
                            using var buffer = new MemoryStream();
                            await stream.CopyToAsync(buffer);
                            Assert.Equal(safeBytes, buffer.ToArray());
                        }
                        else if (result is EmptyResult)
                        {
                            Assert.True(headRequest);
                            Assert.Equal(
                                safeBytes.LongLength,
                                controller.Response.ContentLength);
                        }
                        else if (result is NotFoundResult)
                        {
                        }
                        else if (result is StatusCodeResult status)
                        {
                            Assert.Equal(
                                StatusCodes.Status503ServiceUnavailable,
                                status.StatusCode);
                        }
                        else
                        {
                            Assert.Fail(
                                $"Unexpected action result type: {result.GetType().FullName}");
                        }
                        await Task.Yield();
                    }
                })
                .ToArray();

            await Task.WhenAll(readers.Cast<Task>().Append(writer));
        }
        finally
        {
            RetryPathMutation(() =>
            {
                RemoveDirectoryEntry(platformRoot);
                Directory.CreateDirectory(platformRoot);
            });
            Directory.Delete(outsideRoot, recursive: true);
        }
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

    private async Task<byte[]> WritePointerGenerationAsync(
        string channel,
        string generationId,
        AppUpdateManifestDto manifest,
        string? pointerSha256 = null,
        string? deliverySha256 = null,
        long? deliveryFileSize = null)
    {
        var generationDirectory = Path.Combine(
            _storageRoot,
            "manifest",
            "generations",
            channel);
        Directory.CreateDirectory(generationDirectory);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllBytesAsync(
            Path.Combine(generationDirectory, generationId + ".json"),
            manifestBytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));
        await WriteManifestPointerAsync(
            channel,
            generationId,
            pointerSha256 ?? sha256,
            manifestBytes.LongLength,
            deliverySha256,
            deliveryFileSize);
        return await File.ReadAllBytesAsync(
            Path.Combine(_storageRoot, "manifest", channel + ".current.json"));
    }

    private async Task WriteManifestPointerAsync(
        string channel,
        string generationId,
        string sha256,
        long fileSize,
        string? deliverySha256 = null,
        long? deliveryFileSize = null)
    {
        var deliveryPath = Path.Combine(
            _storageRoot,
            ".georaeplan-release-generations",
            channel,
            generationId + ".json");
        var pointer = new Dictionary<string, string>
        {
            ["owner"] = "georaeplan-release-manifest-pointer",
            ["schemaVersion"] = "1",
            ["channel"] = channel,
            ["generationId"] = generationId,
            ["manifestRelativePath"] =
                $"generations/{channel}/{generationId}.json",
            ["manifestSha256"] = sha256,
            ["manifestFileSize"] = fileSize.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["deliveryManifestPath"] = deliveryPath,
            ["deliveryManifestSha256"] = deliverySha256 ?? sha256,
            ["deliveryManifestFileSize"] = (
                deliveryFileSize ?? fileSize).ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        };
        await File.WriteAllBytesAsync(
            Path.Combine(_storageRoot, "manifest", channel + ".current.json"),
            JsonSerializer.SerializeToUtf8Bytes(pointer));
    }

    private static string ReadUpdatesControllerSource()
    {
        var root = FindRepositoryRoot();
        var serverRoot = Path.Combine(root, "Server");
        var apiDirectory = Directory.EnumerateDirectories(serverRoot, "*.Server.Api").Single();
        return File.ReadAllText(Path.Combine(apiDirectory, "Controllers", "UpdatesController.cs"));
    }

    private static void CreateDirectoryLink(
        string linkPath,
        string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "/d",
            "/c",
            "mklink",
            "/J",
            linkPath,
            targetPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Directory junction creation failed." +
                Environment.NewLine +
                stdout +
                Environment.NewLine +
                stderr);
        }
    }

    private static void RemoveDirectoryEntry(string path)
    {
        if (!Directory.Exists(path) && !System.IO.File.Exists(path))
            return;
        var attributes = System.IO.File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path);
            return;
        }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            System.IO.File.Delete(path);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(path))
            System.IO.File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(path))
            Directory.Delete(directory, recursive: true);
        Directory.Delete(path);
    }

    private static void RetryPathMutation(Action action)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception;
                Thread.Sleep(5);
            }
        }
        throw new IOException(
            "Timed out mutating the download race fixture.",
            lastError);
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

    private static async Task<ProcessResult> RunPowerShellAsync(
        string scriptPath,
        params (string Name, string? Value)[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["PSModulePath"] =
            Path.Combine(Path.GetTempPath(), "georaeplan-missing-psmodules");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var (name, value) in arguments)
        {
            startInfo.ArgumentList.Add(name);
            if (value is not null)
                startInfo.ArgumentList.Add(value);
        }

        var result =
            await RedirectedProcessRunner.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(120),
                $"PowerShell script '{scriptPath}'");
        return new ProcessResult(
            result.ExitCode,
            result.StdOut,
            result.StdErr);
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StdOut,
        string StdErr);

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
