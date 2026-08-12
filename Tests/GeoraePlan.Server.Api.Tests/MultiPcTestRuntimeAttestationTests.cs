using System.Security.Cryptography;
using 거래플랜.Server.Api.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class MultiPcTestRuntimeAttestationTests
{
    [Fact]
    public void TryCreateCore_ReturnsCertifiedDevelopmentAttestationForCopiedServerDll()
    {
        using var fixture = new AttestationFixture();

        var snapshot = fixture.TryCreate();

        Assert.NotNull(snapshot);
        Assert.Equal(fixture.CertificationId, snapshot.CertificationId);
        Assert.Equal(fixture.ServerDllSha256, snapshot.ServerDllSha256);
        Assert.Equal(fixture.MarkerSha256, snapshot.RuntimeReadyMarkerSha256);
        Assert.Equal(fixture.ProcessId, snapshot.ProcessId);
        Assert.Equal(fixture.ProcessStartTimeUtc, snapshot.ProcessStartTimeUtc);
        Assert.Equal("A", snapshot.Role);
        Assert.Equal(fixture.AssemblyPathSha256, snapshot.AssemblyPathSha256);
        Assert.Equal(
            ComputeStringSha256(string.Join(
                "\n",
                fixture.Nonce,
                fixture.RunRoot,
                fixture.CertificationId,
                "A",
                fixture.AssemblyPathSha256)),
            snapshot.InstanceSha256);
    }

    [Fact]
    public void TryCreate_RejectsProduction()
    {
        Assert.Null(MultiPcTestRuntimeAttestation.TryCreate(
            new TestHostEnvironment(Environments.Production)));
    }

    [Fact]
    public void TryCreateCore_RejectsTamperedServerDllHash()
    {
        using var fixture = new AttestationFixture();
        fixture.WriteMarker(serverDllSha256: new string('0', 64));

        Assert.Null(fixture.TryCreate());
    }

    [Fact]
    public void TryCreateCore_RejectsWrongRole()
    {
        using var fixture = new AttestationFixture();

        Assert.Null(fixture.TryCreate(role: "B"));
    }

    [Fact]
    public void TryCreateCore_RejectsWrongRuntimeOrAssemblyRoot()
    {
        using var fixture = new AttestationFixture();

        fixture.WriteMarker(markerRuntimeRoot: fixture.RunRoot);
        Assert.Null(fixture.TryCreate());

        fixture.WriteMarker();
        var outsideServerAssemblyPath = Path.Combine(
            fixture.RuntimeRoot,
            Path.GetFileName(fixture.AssemblyPath));
        File.Copy(fixture.AssemblyPath, outsideServerAssemblyPath);
        Assert.Null(fixture.TryCreate(assemblyPath: outsideServerAssemblyPath));
    }

    [Fact]
    public void TryCreateCore_RejectsMarkerCertificationMismatch()
    {
        using var fixture = new AttestationFixture();
        fixture.WriteMarker(markerCertificationId: Guid.NewGuid().ToString("N"));

        Assert.Null(fixture.TryCreate());
    }

    private sealed class AttestationFixture : IDisposable
    {
        private readonly string _root;

        public AttestationFixture()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "georaeplan-multipc-attestation-tests",
                Guid.NewGuid().ToString("N"));
            RuntimeRoot = Path.Combine(_root, "테스트 시행", "실행환경");
            RunRoot = Path.Combine(_root, "테스트 시행", "기록", "run");
            var serverRoot = Path.Combine(RuntimeRoot, "Server");
            Directory.CreateDirectory(serverRoot);
            Directory.CreateDirectory(RunRoot);

            Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            CertificationId = Guid.NewGuid().ToString("N");
            ProcessId = 48217;
            ProcessStartTimeUtc =
                new DateTimeOffset(2026, 7, 30, 1, 2, 3, TimeSpan.Zero);

            var sourceAssemblyPath =
                typeof(MultiPcTestRuntimeAttestation).Assembly.Location;
            AssemblyPath = Path.Combine(
                serverRoot,
                Path.GetFileName(sourceAssemblyPath));
            File.Copy(sourceAssemblyPath, AssemblyPath);

            ServerDllSha256 = ComputeFileSha256(AssemblyPath);
            AssemblyPathSha256 = ComputePathSha256(AssemblyPath);
            WriteMarker();
        }

        public string RuntimeRoot { get; }
        public string RunRoot { get; }
        public string Nonce { get; }
        public string CertificationId { get; }
        public string AssemblyPath { get; }
        public string ServerDllSha256 { get; }
        public string AssemblyPathSha256 { get; }
        public int ProcessId { get; }
        public DateTimeOffset ProcessStartTimeUtc { get; }
        public string MarkerPath => Path.Combine(
            RuntimeRoot,
            ".georaeplan-runtime-ready");
        public string MarkerSha256 { get; private set; } = string.Empty;

        public MultiPcTestRuntimeAttestationSnapshot? TryCreate(
            string role = "A",
            string? runtimeRoot = null,
            string? assemblyPath = null)
            => MultiPcTestRuntimeAttestation.TryCreate(
                new TestHostEnvironment(Environments.Development),
                role,
                Nonce,
                RunRoot,
                runtimeRoot ?? RuntimeRoot,
                CertificationId,
                assemblyPath ?? AssemblyPath,
                ProcessId,
                ProcessStartTimeUtc);

        public void WriteMarker(
            string? serverDllSha256 = null,
            string? markerCertificationId = null,
            string? markerRuntimeRoot = null)
        {
            File.WriteAllLines(
                MarkerPath,
                [
                    "runtime_ready=True",
                    $"runtime_root={markerRuntimeRoot ?? RuntimeRoot}",
                    $"runtime_physical_root={markerRuntimeRoot ?? RuntimeRoot}",
                    $"certification_id={markerCertificationId ?? CertificationId}",
                    $"server_dll_sha256={serverDllSha256 ?? ServerDllSha256}"
                ]);
            MarkerSha256 = ComputeFileSha256(MarkerPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "GeoraePlan.Server.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private static string ComputePathSha256(string path)
        => ComputeStringSha256(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
                .ToUpperInvariant());

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ComputeStringSha256(string value)
        => Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
