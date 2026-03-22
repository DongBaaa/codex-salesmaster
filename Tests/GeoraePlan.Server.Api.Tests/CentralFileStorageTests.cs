using 거래플랜.Server.Api.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
