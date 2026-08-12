using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace 거래플랜.Shared.Contracts;

public enum InstallRecoveryStateStatus
{
    Absent,
    Present,
    AccessError
}

public sealed record InstallRecoveryStateProbeResult(
    InstallRecoveryStateStatus Status,
    string StatePath,
    Exception? Error = null);

public static class InstallRecoveryStateProbe
{
    public const string StateDirectoryPrefix =
        ".tradeplan-update-supervisor-state-";

    public static string GetStatePath(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var normalizedInstallRoot =
            InstallRootPathIdentity.Resolve(installRoot);
        var installParent = Path.GetDirectoryName(normalizedInstallRoot);
        if (string.IsNullOrWhiteSpace(installParent))
        {
            throw new InvalidOperationException(
                $"설치 경로의 부모를 확인하지 못했습니다: {normalizedInstallRoot}");
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    normalizedInstallRoot.ToUpperInvariant())));
        return Path.Combine(
            installParent,
            StateDirectoryPrefix + hash);
    }

    public static InstallRecoveryStateProbeResult Probe(string installRoot)
        => ProbeCore(
            installRoot,
            static (parentPath, exactName) =>
                Directory.EnumerateFileSystemEntries(
                    parentPath,
                    exactName,
                    SearchOption.TopDirectoryOnly));

    internal static InstallRecoveryStateProbeResult ProbeCore(
        string installRoot,
        Func<string, string, IEnumerable<string>> enumerateExactEntries)
    {
        ArgumentNullException.ThrowIfNull(enumerateExactEntries);

        string statePath;
        try
        {
            statePath = GetStatePath(installRoot);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            IOException or
            NotSupportedException or
            SecurityException or
            UnauthorizedAccessException or
            Win32Exception)
        {
            return new InstallRecoveryStateProbeResult(
                InstallRecoveryStateStatus.AccessError,
                string.Empty,
                ex);
        }

        try
        {
            var stateParent = Path.GetDirectoryName(statePath)
                ?? throw new InvalidOperationException(
                    "Install recovery state parent could not be resolved.");
            var exactName = Path.GetFileName(statePath);
            if (string.IsNullOrWhiteSpace(exactName) ||
                exactName.IndexOfAny(['*', '?']) >= 0)
            {
                throw new InvalidOperationException(
                    "Install recovery state name is not an exact safe name.");
            }

            var matches = enumerateExactEntries(
                    stateParent,
                    exactName)
                .Take(2)
                .Select(Path.GetFullPath)
                .ToArray();
            if (matches.Length == 0)
            {
                return new InstallRecoveryStateProbeResult(
                    InstallRecoveryStateStatus.Absent,
                    statePath);
            }
            if (matches.Length != 1 ||
                !string.Equals(
                    matches[0],
                    Path.GetFullPath(statePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Install recovery state enumeration returned an unexpected entry.");
            }

            return new InstallRecoveryStateProbeResult(
                InstallRecoveryStateStatus.Present,
                statePath);
        }
        catch (DirectoryNotFoundException)
        {
            return new InstallRecoveryStateProbeResult(
                InstallRecoveryStateStatus.Absent,
                statePath);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or
            IOException or
            SecurityException or
            NotSupportedException)
        {
            return new InstallRecoveryStateProbeResult(
                InstallRecoveryStateStatus.AccessError,
                statePath,
                ex);
        }
    }
}
