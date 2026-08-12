using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace 거래플랜.Shared.Contracts;

public static class LegacyInstallRollbackStateProbe
{
    private const string RollbackJournalsDirectoryName = "rollback-journals";

    public static IReadOnlyList<string> GetDefaultArtifactRoots()
        => GetDefaultArtifactRootsCore(
            Environment.GetEnvironmentVariable("GEORAEPLAN_TEMP_ROOT"),
            IsDriveReady("D:\\"),
            Path.GetTempPath(),
            Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT"),
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData));

    internal static IReadOnlyList<string> GetDefaultArtifactRootsCore(
        string? configuredTempRoot,
        bool includeDefaultDDriveRoot,
        string systemTempRoot,
        string? configuredAppRoot,
        string? localAppDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemTempRoot);

        var candidates = new List<string?> { configuredTempRoot };
        if (includeDefaultDDriveRoot)
        {
            candidates.Add(
                Path.Combine("D:\\", "거래플랜", "temp"));
        }
        candidates.Add(systemTempRoot);
        if (!string.IsNullOrWhiteSpace(configuredAppRoot))
            candidates.Add(Path.Combine(configuredAppRoot, "temp"));
        if (!string.IsNullOrWhiteSpace(localAppDataRoot))
        {
            candidates.Add(
                Path.Combine(
                    localAppDataRoot,
                    "Temp"));
            candidates.Add(
                Path.Combine(
                    localAppDataRoot,
                    "거래플랜",
                    "temp"));
        }

        return GetArtifactRootsCore(candidates);
    }

    internal static IReadOnlyList<string> GetArtifactRootsCore(
        IEnumerable<string?> candidateTempRoots)
    {
        ArgumentNullException.ThrowIfNull(candidateTempRoots);

        return candidateTempRoots
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(
                static candidate =>
                    Path.Combine(
                        Path.TrimEndingDirectorySeparator(
                            Path.GetFullPath(candidate!)),
                        "GeoraePlan"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsDriveReady(string rootPath)
    {
        try
        {
            return new DriveInfo(rootPath).IsReady;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            IOException or
            SecurityException or
            UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static InstallRecoveryStateProbeResult Probe(
        string artifactRoot,
        string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        try
        {
            var physicalInstallRoot =
                InstallRootPathIdentity.Resolve(installRoot);
            var legacyInstallRoot =
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(installRoot));
            return ProbeCore(
                artifactRoot,
                physicalInstallRoot,
                legacyInstallRoot);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            InvalidOperationException or
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
    }

    public static InstallRecoveryStateProbeResult Probe(
        string artifactRoot,
        string physicalInstallRoot,
        string legacyInstallRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalInstallRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyInstallRoot);

        try
        {
            return ProbeCore(
                artifactRoot,
                InstallRootPathIdentity.Resolve(physicalInstallRoot),
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(legacyInstallRoot)));
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            InvalidOperationException or
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
    }

    internal static InstallRecoveryStateProbeResult ProbeCore(
        string artifactRoot,
        string physicalInstallRoot,
        string legacyInstallRoot)
        => ProbeCore(
            artifactRoot,
            physicalInstallRoot,
            legacyInstallRoot,
            static (parentPath, exactName) =>
                Directory.EnumerateFileSystemEntries(
                    parentPath,
                    exactName,
                    SearchOption.TopDirectoryOnly),
            File.GetAttributes);

    internal static InstallRecoveryStateProbeResult ProbeCore(
        string artifactRoot,
        string physicalInstallRoot,
        string legacyInstallRoot,
        Func<string, string, IEnumerable<string>> enumerateExactEntries,
        Func<string, FileAttributes> getAttributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalInstallRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyInstallRoot);
        ArgumentNullException.ThrowIfNull(enumerateExactEntries);
        ArgumentNullException.ThrowIfNull(getAttributes);

        string[] statePaths;
        try
        {
            statePaths = GetCandidateStatePathsCore(
                artifactRoot,
                physicalInstallRoot,
                legacyInstallRoot);
            var rollbackJournalsRoot =
                Path.GetDirectoryName(statePaths[0])
                ?? throw new InvalidOperationException(
                    "Legacy rollback journal parent could not be resolved.");
            var parentPresence =
                GetPathPresence(rollbackJournalsRoot, getAttributes);
            if (parentPresence == PathPresence.Absent)
            {
                return new InstallRecoveryStateProbeResult(
                    InstallRecoveryStateStatus.Absent,
                    statePaths[0]);
            }
            if (parentPresence != PathPresence.Directory)
            {
                throw new IOException(
                    $"Legacy rollback journal parent is not a regular directory: {rollbackJournalsRoot}");
            }

            var presentStatePaths = new List<string>(capacity: 2);
            foreach (var statePath in statePaths)
            {
                var exactName = Path.GetFileName(statePath);
                if (string.IsNullOrWhiteSpace(exactName) ||
                    exactName.IndexOfAny(['*', '?']) >= 0)
                {
                    throw new IOException(
                        "Legacy rollback state name is not an exact safe name.");
                }

                var matches = enumerateExactEntries(
                        rollbackJournalsRoot,
                        exactName)
                    .Take(2)
                    .Select(Path.GetFullPath)
                    .ToArray();
                if (matches.Length == 0)
                    continue;
                if (matches.Length != 1 ||
                    !string.Equals(
                        matches[0],
                        Path.GetFullPath(statePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "Legacy rollback state enumeration returned an unexpected entry.");
                }

                var statePresence = GetPathPresence(
                    statePath,
                    getAttributes);
                if (statePresence != PathPresence.Directory)
                {
                    throw new IOException(
                        $"Legacy rollback state is not a regular directory: {statePath}");
                }

                presentStatePaths.Add(statePath);
            }

            return presentStatePaths.Count switch
            {
                0 => new InstallRecoveryStateProbeResult(
                    InstallRecoveryStateStatus.Absent,
                    statePaths[0]),
                1 => new InstallRecoveryStateProbeResult(
                    InstallRecoveryStateStatus.Present,
                    presentStatePaths[0]),
                _ => new InstallRecoveryStateProbeResult(
                    InstallRecoveryStateStatus.AccessError,
                    string.Join(
                        Path.PathSeparator,
                        presentStatePaths),
                    new IOException(
                        "Physical and legacy rollback states are both present; recovery target is ambiguous."))
            };
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            InvalidOperationException or
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
    }

    internal static string[] GetCandidateStatePathsCore(
        string artifactRoot,
        string physicalInstallRoot,
        string legacyInstallRoot)
    {
        var rollbackJournalsRoot = InstallRootPathIdentity.Resolve(
            Path.Combine(
                InstallRootPathIdentity.Resolve(artifactRoot),
                RollbackJournalsDirectoryName));
        var normalizedRoots = new[]
            {
                InstallRootPathIdentity.Resolve(physicalInstallRoot),
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(legacyInstallRoot))
            }
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return normalizedRoots
            .Select(
                normalizedRoot =>
                {
                    var key = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                normalizedRoot.ToUpperInvariant())));
                    return Path.Combine(rollbackJournalsRoot, key);
                })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PathPresence GetPathPresence(
        string path,
        Func<string, FileAttributes> getAttributes)
    {
        FileAttributes attributes;
        try
        {
            attributes = getAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return PathPresence.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return PathPresence.Absent;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return PathPresence.ReparsePoint;
        return (attributes & FileAttributes.Directory) != 0
            ? PathPresence.Directory
            : PathPresence.File;
    }

    private enum PathPresence
    {
        Absent,
        File,
        Directory,
        ReparsePoint
    }
}
