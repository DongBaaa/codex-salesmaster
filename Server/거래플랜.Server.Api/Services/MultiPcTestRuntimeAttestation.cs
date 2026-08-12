using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

[assembly: InternalsVisibleTo("GeoraePlan.Server.Api.Tests")]

namespace 거래플랜.Server.Api.Services;

public sealed record MultiPcTestRuntimeAttestationSnapshot(
    string InstanceSha256,
    string CertificationId,
    string ServerDllSha256,
    string RuntimeReadyMarkerSha256,
    int ProcessId,
    DateTimeOffset ProcessStartTimeUtc,
    string Role,
    string AssemblyPathSha256);

public static class MultiPcTestRuntimeAttestation
{
    private const string NonceEnvironmentKey = "GEORAEPLAN_MULTI_PC_E2E_NONCE";
    private const string RunRootEnvironmentKey = "GEORAEPLAN_MULTI_PC_E2E_RUN_ROOT";
    private const string RuntimeRootEnvironmentKey = "GEORAEPLAN_MULTI_PC_RUNTIME_ROOT";
    private const string CertificationIdEnvironmentKey = "GEORAEPLAN_MULTI_PC_CERTIFICATION_ID";
    private const string RoleEnvironmentKey = "GEORAEPLAN_MULTI_PC_E2E_ROLE";
    private const string RuntimeReadyMarkerFileName = ".georaeplan-runtime-ready";

    public static MultiPcTestRuntimeAttestationSnapshot? TryCreate(
        IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
            return null;

        try
        {
            using var process = Process.GetCurrentProcess();
            return TryCreate(
                environment,
                Environment.GetEnvironmentVariable(RoleEnvironmentKey),
                Environment.GetEnvironmentVariable(NonceEnvironmentKey),
                Environment.GetEnvironmentVariable(RunRootEnvironmentKey),
                Environment.GetEnvironmentVariable(RuntimeRootEnvironmentKey),
                Environment.GetEnvironmentVariable(CertificationIdEnvironmentKey),
                Assembly.GetExecutingAssembly().Location,
                process.Id,
                process.StartTime.ToUniversalTime());
        }
        catch
        {
            return null;
        }
    }

    internal static MultiPcTestRuntimeAttestationSnapshot? TryCreate(
        IHostEnvironment environment,
        string? role,
        string? nonce,
        string? runRootRaw,
        string? runtimeRootRaw,
        string? certificationId,
        string assemblyPath,
        int processId,
        DateTimeOffset processStartTimeUtc)
    {
        if (!environment.IsDevelopment())
            return null;

        if (!string.Equals(role, "A", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(nonce) ||
            string.IsNullOrWhiteSpace(runRootRaw) ||
            string.IsNullOrWhiteSpace(runtimeRootRaw) ||
            string.IsNullOrWhiteSpace(certificationId) ||
            string.IsNullOrWhiteSpace(assemblyPath) ||
            processId <= 0 ||
            processStartTimeUtc == default)
        {
            return null;
        }

        try
        {
            var runRoot = NormalizePath(runRootRaw);
            var runtimeRoot = NormalizePath(runtimeRootRaw);
            if (!IsTestEvidenceRoot(runRoot))
                return null;

            var markerPath = Path.Combine(runtimeRoot, RuntimeReadyMarkerFileName);
            if (!File.Exists(markerPath) || !HasNoReparsePoints(markerPath))
                return null;

            var marker = ReadMarker(markerPath);
            if (!TryReadRequired(marker, "runtime_ready", out var runtimeReady) ||
                !bool.TryParse(runtimeReady, out var ready) ||
                !ready ||
                !TryReadRequired(marker, "certification_id", out var markerCertificationId) ||
                !string.Equals(markerCertificationId, certificationId, StringComparison.Ordinal) ||
                !TryReadRequired(marker, "runtime_root", out var markerRuntimeRoot) ||
                !PathsEqual(markerRuntimeRoot, runtimeRoot) ||
                !TryReadRequired(marker, "runtime_physical_root", out var markerPhysicalRoot) ||
                !PathsEqual(markerPhysicalRoot, runtimeRoot) ||
                !TryReadRequired(marker, "server_dll_sha256", out var certifiedServerDllHash))
            {
                return null;
            }

            var normalizedAssemblyPath = NormalizePath(assemblyPath);
            var expectedServerRoot = NormalizePath(
                Path.Combine(runtimeRoot, "Server"));
            var expectedAssemblyFileName =
                $"{typeof(MultiPcTestRuntimeAttestation).Assembly.GetName().Name}.dll";
            if (!File.Exists(normalizedAssemblyPath) ||
                !PathsEqual(
                    Path.GetDirectoryName(normalizedAssemblyPath)
                        ?? string.Empty,
                    expectedServerRoot) ||
                !string.Equals(
                    Path.GetFileName(normalizedAssemblyPath),
                    expectedAssemblyFileName,
                    StringComparison.OrdinalIgnoreCase) ||
                !HasNoReparsePoints(normalizedAssemblyPath))
            {
                return null;
            }

            var actualServerDllHash = ComputeFileSha256(normalizedAssemblyPath);
            if (!string.Equals(
                    actualServerDllHash,
                    certifiedServerDllHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var assemblyPathSha256 = ComputePathSha256(normalizedAssemblyPath);
            return new MultiPcTestRuntimeAttestationSnapshot(
                ComputeInstanceSha256(
                    nonce,
                    runRoot,
                    certificationId,
                    role!,
                    assemblyPathSha256),
                certificationId,
                actualServerDllHash,
                ComputeFileSha256(markerPath),
                processId,
                processStartTimeUtc.ToUniversalTime(),
                role!,
                assemblyPathSha256);
        }
        catch
        {
            return null;
        }
    }

    internal static string ComputeInstanceSha256(
        string nonce,
        string runRoot,
        string certificationId,
        string role,
        string assemblyPathSha256)
        => ComputeSha256(
            string.Join(
                "\n",
                nonce,
                NormalizePath(runRoot),
                certificationId,
                role,
                assemblyPathSha256));

    private static Dictionary<string, string> ReadMarker(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
                throw new InvalidDataException("Runtime certification marker is malformed.");

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (!values.TryAdd(key, value))
                throw new InvalidDataException("Runtime certification marker contains duplicate keys.");
        }

        return values;
    }

    private static bool TryReadRequired(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
        => values.TryGetValue(key, out value!) &&
           !string.IsNullOrWhiteSpace(value);

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsTestEvidenceRoot(string path)
    {
        var marker =
            $"{Path.DirectorySeparatorChar}테스트 시행{Path.DirectorySeparatorChar}기록{Path.DirectorySeparatorChar}";
        return (NormalizePath(path) + Path.DirectorySeparatorChar)
            .Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            return false;

        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            return false;

        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return false;
        }

        return true;
    }

    private static string ComputePathSha256(string path)
        => ComputeSha256(NormalizePath(path).ToUpperInvariant());

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ComputeSha256(string value)
        => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
