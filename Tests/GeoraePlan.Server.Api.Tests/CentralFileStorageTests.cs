using 거래플랜.Server.Api.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class CentralFileStorageTests : IDisposable
{
    private readonly string _rootPath;

    public CentralFileStorageTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "georaeplan-central-file-storage-tests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task SaveReadAndDelete_RoundTripsBytesThroughCentralStorage()
    {
        var service = CreateService();
        var fileId = Guid.NewGuid();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var storedPath = await service.SaveBytesAsync("contracts", "customer-1", fileId, "sample.pdf", bytes);

        Assert.StartsWith(_rootPath, storedPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(storedPath));
        Assert.Equal(bytes, service.ReadBytes(storedPath));

        service.DeleteIfExists(storedPath);

        Assert.False(File.Exists(storedPath));
        Assert.Empty(service.ReadBytes(storedPath));
    }

    [Fact]
    public void ReadBytes_FallsBackToProvidedBuffer_WhenStoredPathMissing()
    {
        var service = CreateService();
        var fallback = new byte[] { 9, 8, 7 };

        var bytes = service.ReadBytes(Path.Combine(_rootPath, "missing.bin"), fallback);

        Assert.Equal(fallback, bytes);
    }

    [Fact]
    public async Task Inspect_ReturnsStoredFileLengthAndHash_WhenFileExists()
    {
        var service = CreateService();
        var bytes = new byte[] { 10, 20, 30 };
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes));
        var storedPath = await service.SaveBytesAsync("contracts", "customer-1", Guid.NewGuid(), "sample.pdf", bytes);

        var result = service.Inspect(storedPath, computeHash: true);

        Assert.True(result.HasStoredPath);
        Assert.True(result.IsSafePath);
        Assert.True(result.Exists);
        Assert.Equal(bytes.Length, result.Length);
        Assert.Equal(expectedHash, result.Hash);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public void Inspect_ReportsMissingAndUnsafeStoredPaths()
    {
        var service = CreateService();

        var missing = service.Inspect(Path.Combine(_rootPath, "missing.bin"));
        var unsafePath = service.Inspect(Path.Combine(Path.GetTempPath(), "outside-georaeplan.bin"));

        Assert.True(missing.HasStoredPath);
        Assert.True(missing.IsSafePath);
        Assert.False(missing.Exists);
        Assert.Equal("stored_file_not_found", missing.Error);

        Assert.True(unsafePath.HasStoredPath);
        Assert.False(unsafePath.IsSafePath);
        Assert.False(unsafePath.Exists);
        Assert.Equal("unsafe_storage_path", unsafePath.Error);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task SaveBytesAsync_RejectsDotPathSegments(string segment)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveBytesAsync(
                segment,
                "owner",
                Guid.NewGuid(),
                "sample.pdf",
                [1, 2, 3]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveBytesAsync(
                "contracts",
                segment,
                Guid.NewGuid(),
                "sample.pdf",
                [1, 2, 3]));
    }

    [Fact]
    public async Task SaveReadInspectAndDelete_RejectDirectorySymlinkEscapingStorageRoot()
    {
        var service = CreateService();
        var outsideDirectory = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-central-file-storage-outside-tests",
            Guid.NewGuid().ToString("N"));
        var linkedArea = Path.Combine(_rootPath, "contracts");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkedArea, outsideDirectory);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or
                    PlatformNotSupportedException or
                    IOException)
            {
                return;
            }

            var outsideFile = Path.Combine(outsideDirectory, "outside.txt");
            await File.WriteAllTextAsync(outsideFile, "outside");
            var aliasedPath = Path.Combine(linkedArea, "outside.txt");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.SaveBytesAsync(
                    "contracts",
                    "owner",
                    Guid.NewGuid(),
                    "sample.pdf",
                    [1, 2, 3]));
            Assert.Equal(
                [9, 8, 7],
                service.ReadBytes(aliasedPath, [9, 8, 7]));

            var inspection = service.Inspect(aliasedPath, computeHash: true);
            Assert.True(inspection.HasStoredPath);
            Assert.False(inspection.IsSafePath);
            Assert.False(inspection.Exists);
            Assert.Equal("unsafe_storage_path", inspection.Error);

            service.DeleteIfExists(aliasedPath);
            Assert.True(File.Exists(outsideFile));
            Assert.Equal("outside", await File.ReadAllTextAsync(outsideFile));
        }
        finally
        {
            if (Directory.Exists(linkedArea))
                Directory.Delete(linkedArea);
            if (Directory.Exists(outsideDirectory))
                Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveBytesAsync_ReusesIdenticalFileButDoesNotOverwriteDifferentContent()
    {
        var service = CreateService();
        var fileId = Guid.NewGuid();
        var storedPath = await service.SaveBytesAsync(
            "contracts",
            "owner",
            fileId,
            "sample.pdf",
            [1, 2, 3]);

        var repeatedPath = await service.SaveBytesAsync(
            "contracts",
            "owner",
            fileId,
            "sample.pdf",
            [1, 2, 3]);
        await Assert.ThrowsAsync<IOException>(() =>
            service.SaveBytesAsync(
                "contracts",
                "owner",
                fileId,
                "sample.pdf",
                [9, 9, 9]));

        Assert.Equal(storedPath, repeatedPath);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(storedPath));
        Assert.Empty(
            Directory.EnumerateFiles(
                Path.GetDirectoryName(storedPath)!,
                "*.tmp",
                SearchOption.TopDirectoryOnly));
    }

    private CentralFileStorage CreateService()
    {
        var options = Options.Create(new CentralFileStorageOptions { RootPath = _rootPath });
        return new CentralFileStorage(options, new TestHostEnvironment());
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "GeoraePlan.Server.Api.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
