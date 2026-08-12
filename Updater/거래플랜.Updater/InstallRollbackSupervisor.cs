using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Updater;

internal enum InstallRollbackPhase
{
    Preparing,
    Prepared,
    Installing,
    Recovering,
    Restored,
    Committed,
    CleanupPending
}

internal sealed class InstallRollbackFileRecord
{
    public string RelativePath { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int Attributes { get; set; }
    public long CreationTimeUtcTicks { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }
}

internal sealed class InstallRollbackDirectoryRecord
{
    public string RelativePath { get; set; } = string.Empty;
    public int Attributes { get; set; }
    public long CreationTimeUtcTicks { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }
}

internal sealed class InstallRollbackJournal
{
    public int FormatVersion { get; set; } = 1;
    public InstallRollbackPhase Phase { get; set; }
    public string InstallRoot { get; set; } = string.Empty;
    public string StateRoot { get; set; } = string.Empty;
    public string SnapshotRoot { get; set; } = string.Empty;
    public bool HadExistingInstall { get; set; }
    public int RootAttributes { get; set; }
    public long RootCreationTimeUtcTicks { get; set; }
    public long RootLastWriteTimeUtcTicks { get; set; }
    public List<InstallRollbackFileRecord> Files { get; set; } = [];
    public List<InstallRollbackDirectoryRecord> Directories { get; set; } = [];
    public List<string> PreexistingInstallerRollbackDirectories { get; set; } = [];
}

internal sealed class InstallRollbackSession
{
    internal required string JournalPath { get; init; }
    internal required InstallRollbackJournal Journal { get; init; }
}

internal static class InstallRollbackSupervisor
{
    private const string JournalFileName = "journal.json";
    private const string SnapshotDirectoryName = "snapshot";
    private const string InstallerRollbackDirectoryPrefix = ".tradeplan-install-rollback-";
    private static readonly JsonSerializerOptions JournalJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
    private static readonly AsyncLocal<Func<string, Exception?>?>
        CompletedStateCleanupFailureFactoryForTestsSlot = new();
    private static readonly AsyncLocal<Func<string, Exception?>?>
        JournalAfterFlushCrashFactoryForTestsSlot = new();
    private static readonly AsyncLocal<Func<string, FileAttributes>?>
        PresenceProbeForTestsSlot = new();

    internal static Func<string, Exception?>?
        CompletedStateCleanupFailureFactoryForTests
    {
        get => CompletedStateCleanupFailureFactoryForTestsSlot.Value;
        set => CompletedStateCleanupFailureFactoryForTestsSlot.Value = value;
    }

    internal static Func<string, Exception?>?
        JournalAfterFlushCrashFactoryForTests
    {
        get => JournalAfterFlushCrashFactoryForTestsSlot.Value;
        set => JournalAfterFlushCrashFactoryForTestsSlot.Value = value;
    }

    internal static Func<string, FileAttributes>?
        PresenceProbeForTests
    {
        get => PresenceProbeForTestsSlot.Value;
        set => PresenceProbeForTestsSlot.Value = value;
    }

    internal static string GetStateRoot(string artifactRoot, string installRoot)
    {
        var normalizedInstallRoot = NormalizeDirectoryPath(installRoot);
        var key = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    normalizedInstallRoot.ToUpperInvariant())));
        return Path.Combine(
            NormalizeDirectoryPath(artifactRoot),
            "rollback-journals",
            key);
    }

    internal static async Task RecoverPendingUntilResolvedAsync(
        string artifactRoot,
        string installRoot,
        Action<string>? log = null,
        TimeSpan? retryDelay = null)
    {
        var delay = retryDelay ?? TimeSpan.FromSeconds(5);
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        while (true)
        {
            try
            {
                await RecoverPendingOnceAsync(artifactRoot, installRoot, log)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                log?.Invoke(
                    $"ROLLBACK recovery pending; install gate retained. error={ex}");
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }
    }

    internal static async Task RecoverPendingUntilResolvedAsync(
        IReadOnlyList<string> artifactRoots,
        string installRoot,
        string legacyInstallRoot,
        Action<string>? log = null,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(artifactRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyInstallRoot);
        if (artifactRoots.Count == 0)
        {
            throw new ArgumentException(
                "At least one rollback artifact root is required.",
                nameof(artifactRoots));
        }

        var delay = retryDelay ?? TimeSpan.FromSeconds(5);
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        while (true)
        {
            try
            {
                await RecoverPendingCandidatesOnceAsync(
                        artifactRoots,
                        installRoot,
                        legacyInstallRoot,
                        log)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                log?.Invoke(
                    $"ROLLBACK candidate recovery pending; install gate retained. error={ex}");
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<InstallRollbackSession> PrepareAsync(
        string artifactRoot,
        string installRoot,
        Action<string>? log = null)
    {
        await RecoverPendingUntilResolvedAsync(
                artifactRoot,
                installRoot,
                log)
            .ConfigureAwait(false);

        var fullInstallRoot = NormalizeDirectoryPath(installRoot);
        var stateRoot = GetStateRoot(artifactRoot, fullInstallRoot);
        var rollbackRoot = Path.GetDirectoryName(stateRoot)
            ?? throw new InvalidOperationException(
                "Rollback state parent directory could not be resolved.");
        Directory.CreateDirectory(rollbackRoot);
        EnsureTreeContainsNoReparsePoints(rollbackRoot);
        EnsurePathIsWithin(stateRoot, rollbackRoot);

        if (DirectoryExistsExact(stateRoot))
        {
            if (Directory.EnumerateFileSystemEntries(stateRoot).Any())
            {
                throw new InvalidOperationException(
                    $"Rollback state exists without a resolvable journal: {stateRoot}");
            }

            Directory.Delete(stateRoot);
            EnsurePathAbsentExact(
                stateRoot,
                "Rollback state directory deletion was not durable.");
        }

        Directory.CreateDirectory(stateRoot);
        EnsureTreeContainsNoReparsePoints(stateRoot);
        var snapshotRoot = Path.Combine(stateRoot, SnapshotDirectoryName);
        var journalPath = Path.Combine(stateRoot, JournalFileName);
        var installParent = Path.GetDirectoryName(fullInstallRoot)
            ?? throw new InvalidOperationException(
                "Install root parent directory could not be resolved.");

        var journal = new InstallRollbackJournal
        {
            Phase = InstallRollbackPhase.Preparing,
            InstallRoot = fullInstallRoot,
            StateRoot = stateRoot,
            SnapshotRoot = snapshotRoot,
            HadExistingInstall = DirectoryExistsExact(fullInstallRoot),
            PreexistingInstallerRollbackDirectories =
                EnumerateInstallerRollbackDirectories(installParent)
        };

        if (journal.HadExistingInstall)
        {
            var manifest = CaptureManifest(fullInstallRoot);
            journal.RootAttributes = manifest.RootAttributes;
            journal.RootCreationTimeUtcTicks =
                manifest.RootCreationTimeUtcTicks;
            journal.RootLastWriteTimeUtcTicks =
                manifest.RootLastWriteTimeUtcTicks;
            journal.Files = manifest.Files;
            journal.Directories = manifest.Directories;
        }

        WriteJournalAtomically(journalPath, journal);

        if (journal.HadExistingInstall)
        {
            log?.Invoke(
                $"ROLLBACK snapshot start source={fullInstallRoot} destination={snapshotRoot}");
            EnsureTreeContainsNoReparsePoints(fullInstallRoot);
            await RunRobocopyMirrorAsync(fullInstallRoot, snapshotRoot)
                .ConfigureAwait(false);
            ApplyManifestMetadata(snapshotRoot, journal);
            VerifyManifest(snapshotRoot, journal);
            log?.Invoke(
                $"ROLLBACK snapshot verified files={journal.Files.Count} directories={journal.Directories.Count}");
        }

        journal.Phase = InstallRollbackPhase.Prepared;
        WriteJournalAtomically(journalPath, journal);
        return new InstallRollbackSession
        {
            JournalPath = journalPath,
            Journal = journal
        };
    }

    internal static void MarkInstallerStarting(InstallRollbackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Journal.Phase = InstallRollbackPhase.Installing;
        WriteJournalAtomically(session.JournalPath, session.Journal);
    }

    internal static void Commit(
        InstallRollbackSession session,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        CompletePendingCleanupOnce(
            session.JournalPath,
            session.Journal,
            log);
    }

    internal static async Task RecoverUntilVerifiedAsync(
        InstallRollbackSession session,
        Action<string>? log = null,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var delay = retryDelay ?? TimeSpan.FromSeconds(5);
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        while (true)
        {
            try
            {
                if (session.Journal.Phase is
                    InstallRollbackPhase.CleanupPending or
                    InstallRollbackPhase.Restored or
                    InstallRollbackPhase.Committed)
                {
                    CompletePendingCleanupOnce(
                        session.JournalPath,
                        session.Journal,
                        log);
                }
                else
                {
                    await RestoreAndVerifyAsync(
                            session.JournalPath,
                            session.Journal,
                            log)
                        .ConfigureAwait(false);
                }
                return;
            }
            catch (Exception ex)
            {
                log?.Invoke(
                    $"ROLLBACK restore pending; install gate retained. state={session.Journal.StateRoot} error={ex}");
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }
    }

    internal static void VerifyManifest(
        string rootPath,
        InstallRollbackJournal expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var actual = CaptureManifest(rootPath);
        if (actual.RootAttributes != expected.RootAttributes ||
            (expected.RootCreationTimeUtcTicks > 0 &&
             actual.RootCreationTimeUtcTicks !=
             expected.RootCreationTimeUtcTicks) ||
            actual.RootLastWriteTimeUtcTicks !=
            expected.RootLastWriteTimeUtcTicks)
        {
            throw new InvalidOperationException(
                $"Rollback root metadata verification failed: {rootPath}");
        }

        if (actual.Files.Count != expected.Files.Count ||
            actual.Directories.Count != expected.Directories.Count)
        {
            throw new InvalidOperationException(
                $"Rollback manifest entry count differs: {rootPath}");
        }

        for (var index = 0; index < expected.Files.Count; index++)
        {
            var expectedFile = expected.Files[index];
            var actualFile = actual.Files[index];
            if (!string.Equals(
                    actualFile.RelativePath,
                    expectedFile.RelativePath,
                    StringComparison.OrdinalIgnoreCase) ||
                actualFile.Length != expectedFile.Length ||
                !string.Equals(
                    actualFile.Sha256,
                    expectedFile.Sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                actualFile.Attributes != expectedFile.Attributes ||
                (expectedFile.CreationTimeUtcTicks > 0 &&
                 actualFile.CreationTimeUtcTicks !=
                 expectedFile.CreationTimeUtcTicks) ||
                actualFile.LastWriteTimeUtcTicks !=
                expectedFile.LastWriteTimeUtcTicks)
            {
                throw new InvalidOperationException(
                    $"Rollback file verification failed: {expectedFile.RelativePath}");
            }
        }

        for (var index = 0;
             index < expected.Directories.Count;
             index++)
        {
            var expectedDirectory = expected.Directories[index];
            var actualDirectory = actual.Directories[index];
            if (!string.Equals(
                    actualDirectory.RelativePath,
                    expectedDirectory.RelativePath,
                    StringComparison.OrdinalIgnoreCase) ||
                actualDirectory.Attributes != expectedDirectory.Attributes ||
                (expectedDirectory.CreationTimeUtcTicks > 0 &&
                 actualDirectory.CreationTimeUtcTicks !=
                 expectedDirectory.CreationTimeUtcTicks) ||
                actualDirectory.LastWriteTimeUtcTicks !=
                expectedDirectory.LastWriteTimeUtcTicks)
            {
                throw new InvalidOperationException(
                    $"Rollback directory verification failed: {expectedDirectory.RelativePath}");
            }
        }
    }

    internal static async Task RecoverPendingOnceAsync(
        string artifactRoot,
        string installRoot,
        Action<string>? log)
    {
        var stateRoot = GetStateRoot(artifactRoot, installRoot);
        await RecoverPendingStateOnceAsync(
                stateRoot,
                installRoot,
                log)
            .ConfigureAwait(false);
    }

    internal static async Task RecoverPendingCandidatesOnceAsync(
        IReadOnlyList<string> artifactRoots,
        string installRoot,
        Action<string>? log)
        => await RecoverPendingCandidatesOnceAsync(
                artifactRoots,
                installRoot,
                installRoot,
                log)
            .ConfigureAwait(false);

    internal static async Task RecoverPendingCandidatesOnceAsync(
        IReadOnlyList<string> artifactRoots,
        string installRoot,
        string legacyInstallRoot,
        Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(artifactRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyInstallRoot);
        if (artifactRoots.Count == 0)
        {
            throw new ArgumentException(
                "At least one rollback artifact root is required.",
                nameof(artifactRoots));
        }

        var pendingStateRoots = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var artifactRoot in artifactRoots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
            var probe = LegacyInstallRollbackStateProbe.Probe(
                artifactRoot,
                installRoot,
                legacyInstallRoot);
            switch (probe.Status)
            {
                case InstallRecoveryStateStatus.Absent:
                    continue;
                case InstallRecoveryStateStatus.Present:
                    pendingStateRoots.Add(
                        NormalizeDirectoryPath(probe.StatePath));
                    continue;
                default:
                    throw new IOException(
                        $"Rollback state candidate could not be inspected safely: {probe.StatePath}",
                        probe.Error);
            }
        }

        if (pendingStateRoots.Count > 1)
        {
            throw new InvalidOperationException(
                "Multiple rollback state candidates are present; automatic recovery target is ambiguous.");
        }
        if (pendingStateRoots.Count == 0)
            return;

        await RecoverPendingStateOnceAsync(
                pendingStateRoots.Single(),
                installRoot,
                log)
            .ConfigureAwait(false);
    }

    private static async Task RecoverPendingStateOnceAsync(
        string stateRoot,
        string installRoot,
        Action<string>? log)
    {
        if (!DirectoryExistsExact(stateRoot))
            return;

        EnsureTreeContainsNoReparsePoints(stateRoot);
        DeleteOrphanedJournalTemporaryFiles(stateRoot);
        var journalPath = Path.Combine(stateRoot, JournalFileName);
        if (!FileExistsExact(journalPath))
        {
            if (!Directory.EnumerateFileSystemEntries(stateRoot).Any())
            {
                Directory.Delete(stateRoot);
                EnsurePathAbsentExact(
                    stateRoot,
                    "Empty rollback state directory deletion was not durable.");
                return;
            }

            throw new InvalidOperationException(
                $"Rollback state exists but its journal is missing: {stateRoot}");
        }

        var journal = ReadAndValidateJournal(
            journalPath,
            stateRoot,
            installRoot);
        switch (journal.Phase)
        {
            case InstallRollbackPhase.Preparing:
                DeleteStateDirectory(journal.StateRoot);
                return;
            case InstallRollbackPhase.Restored:
            case InstallRollbackPhase.Committed:
            case InstallRollbackPhase.CleanupPending:
                CompletePendingCleanupOnce(
                    journalPath,
                    journal,
                    log);
                return;
            case InstallRollbackPhase.Prepared:
            case InstallRollbackPhase.Installing:
            case InstallRollbackPhase.Recovering:
                await RestoreAndVerifyAsync(journalPath, journal, log)
                    .ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unknown rollback journal phase: {journal.Phase}");
        }
    }

    private static async Task RestoreAndVerifyAsync(
        string journalPath,
        InstallRollbackJournal journal,
        Action<string>? log)
    {
        journal.Phase = InstallRollbackPhase.Recovering;
        WriteJournalAtomically(journalPath, journal);

        if (journal.HadExistingInstall)
        {
            VerifyManifest(journal.SnapshotRoot, journal);
            if (DirectoryExistsExact(journal.InstallRoot))
            {
                EnsureTreeContainsNoReparsePoints(journal.InstallRoot);
                MakeTreeWritable(journal.InstallRoot);
            }

            log?.Invoke(
                $"ROLLBACK restore start source={journal.SnapshotRoot} destination={journal.InstallRoot}");
            await RunRobocopyMirrorAsync(
                    journal.SnapshotRoot,
                    journal.InstallRoot)
                .ConfigureAwait(false);
            ApplyManifestMetadata(journal.InstallRoot, journal);
            VerifyManifest(journal.InstallRoot, journal);
        }
        else
        {
            if (DirectoryExistsExact(journal.InstallRoot))
            {
                EnsureTreeContainsNoReparsePoints(journal.InstallRoot);
                DeleteDirectoryTree(journal.InstallRoot);
            }

            EnsurePathAbsentExact(
                journal.InstallRoot,
                "Failed to remove the incomplete new install.");
        }

        RemoveNewInstallerRollbackDirectories(journal);
        log?.Invoke(
            $"ROLLBACK restore verified installRoot={journal.InstallRoot}");
        CompletePendingCleanupOnce(
            journalPath,
            journal,
            log);
    }

    private static InstallRollbackJournal ReadAndValidateJournal(
        string journalPath,
        string expectedStateRoot,
        string expectedInstallRoot)
    {
        var bytes = File.ReadAllBytes(journalPath);
        var journal = JsonSerializer.Deserialize<InstallRollbackJournal>(
                          bytes,
                          JournalJsonOptions)
                      ?? throw new InvalidOperationException(
                          $"Rollback journal is empty: {journalPath}");
        if (journal.FormatVersion != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported rollback journal format: {journal.FormatVersion}");
        }

        if (!PathsEqual(journal.StateRoot, expectedStateRoot) ||
            !PathsEqual(journal.InstallRoot, expectedInstallRoot) ||
            !PathsEqual(
                journal.SnapshotRoot,
                Path.Combine(
                    expectedStateRoot,
                    SnapshotDirectoryName)))
        {
            throw new InvalidOperationException(
                $"Rollback journal path binding failed: {journalPath}");
        }

        return journal;
    }

    private static void WriteJournalAtomically(
        string journalPath,
        InstallRollbackJournal journal)
    {
        var stateRoot = Path.GetDirectoryName(journalPath)
            ?? throw new InvalidOperationException(
                "Rollback journal directory could not be resolved.");
        Directory.CreateDirectory(stateRoot);
        var rollbackRoot = Path.GetDirectoryName(stateRoot)
            ?? throw new InvalidOperationException(
                "Rollback state parent directory could not be resolved.");
        EnsureTreeContainsNoReparsePoints(rollbackRoot);
        EnsureTreeContainsNoReparsePoints(stateRoot);
        EnsurePathIsWithin(stateRoot, rollbackRoot);
        var temporaryPrefix =
            GetJournalTemporaryFilePrefix(stateRoot);
        var temporaryPath = Path.Combine(
            rollbackRoot,
            $"{temporaryPrefix}{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        EnsurePathIsWithin(temporaryPath, rollbackRoot);
        if (!string.Equals(
                Path.GetPathRoot(temporaryPath),
                Path.GetPathRoot(journalPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Rollback journal temporary file must be on the journal volume.");
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            journal,
            JournalJsonOptions);
        var preserveTemporaryFile = false;
        try
        {
            EnsureTreeContainsNoReparsePoints(rollbackRoot);
            EnsureTreeContainsNoReparsePoints(stateRoot);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            var injectedCrash =
                JournalAfterFlushCrashFactoryForTests?.Invoke(
                    temporaryPath);
            if (injectedCrash is not null)
            {
                preserveTemporaryFile = true;
                throw injectedCrash;
            }

            EnsureTreeContainsNoReparsePoints(rollbackRoot);
            EnsureTreeContainsNoReparsePoints(stateRoot);
            File.Move(temporaryPath, journalPath, overwrite: true);
            if (!FileExistsExact(journalPath))
            {
                throw new IOException(
                    $"Rollback journal move did not create the destination: {journalPath}");
            }
            EnsurePathAbsentExact(
                temporaryPath,
                "Rollback journal move did not remove the temporary file.");
        }
        finally
        {
            if (!preserveTemporaryFile &&
                FileExistsExact(temporaryPath))
            {
                EnsureTreeContainsNoReparsePoints(rollbackRoot);
                File.Delete(temporaryPath);
                EnsurePathAbsentExact(
                    temporaryPath,
                    "Rollback journal temporary file deletion was not durable.");
            }
        }
    }

    private static string GetJournalTemporaryFilePrefix(
        string stateRoot)
        => $".{Path.GetFileName(NormalizeDirectoryPath(stateRoot))}.journal.";

    private static void DeleteOrphanedJournalTemporaryFiles(
        string stateRoot)
    {
        var rollbackRoot = Path.GetDirectoryName(
                NormalizeDirectoryPath(stateRoot))
            ?? throw new InvalidOperationException(
                "Rollback state parent directory could not be resolved.");
        var temporaryPrefix =
            GetJournalTemporaryFilePrefix(stateRoot);
        foreach (var temporaryPath in Directory.EnumerateFiles(
                     rollbackRoot,
                     temporaryPrefix + "*.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            EnsurePathIsWithin(temporaryPath, rollbackRoot);
            var attributes = File.GetAttributes(temporaryPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Rollback journal temporary file is a reparse point: {temporaryPath}");
            }

            File.SetAttributes(temporaryPath, FileAttributes.Normal);
            File.Delete(temporaryPath);
            EnsurePathAbsentExact(
                temporaryPath,
                "Orphaned rollback journal temporary file deletion was not durable.");
        }
    }

    private static InstallRollbackJournal CaptureManifest(string rootPath)
    {
        var fullRoot = NormalizeDirectoryPath(rootPath);
        if (!DirectoryExistsExact(fullRoot))
        {
            throw new DirectoryNotFoundException(
                $"Rollback manifest root was not found: {fullRoot}");
        }

        EnsureTreeContainsNoReparsePoints(fullRoot);
        var rootInfo = new DirectoryInfo(fullRoot);
        var files = new List<InstallRollbackFileRecord>();
        var directories = new List<InstallRollbackDirectoryRecord>();
        var pending = new Stack<string>();
        pending.Push(fullRoot);

        while (pending.Count > 0)
        {
            var currentDirectory = pending.Pop();
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(
                         currentDirectory))
            {
                var attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"Rollback source contains a reparse point: {entryPath}");
                }

                var relativePath = ValidateRelativePath(
                    Path.GetRelativePath(fullRoot, entryPath));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var directoryInfo = new DirectoryInfo(entryPath);
                    directories.Add(new InstallRollbackDirectoryRecord
                    {
                        RelativePath = relativePath,
                        Attributes = (int)attributes,
                        CreationTimeUtcTicks =
                            directoryInfo.CreationTimeUtc.Ticks,
                        LastWriteTimeUtcTicks =
                            directoryInfo.LastWriteTimeUtc.Ticks
                    });
                    pending.Push(entryPath);
                    continue;
                }

                var fileInfo = new FileInfo(entryPath);
                using var stream = new FileStream(
                    entryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                files.Add(new InstallRollbackFileRecord
                {
                    RelativePath = relativePath,
                    Length = fileInfo.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(stream)),
                    Attributes = (int)attributes,
                    CreationTimeUtcTicks =
                        fileInfo.CreationTimeUtc.Ticks,
                    LastWriteTimeUtcTicks =
                        fileInfo.LastWriteTimeUtc.Ticks
                });
            }
        }

        files.Sort(
            static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
                left.RelativePath,
                right.RelativePath));
        directories.Sort(
            static (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(
                    left.RelativePath,
                    right.RelativePath));
        return new InstallRollbackJournal
        {
            RootAttributes = (int)rootInfo.Attributes,
            RootCreationTimeUtcTicks =
                rootInfo.CreationTimeUtc.Ticks,
            RootLastWriteTimeUtcTicks =
                rootInfo.LastWriteTimeUtc.Ticks,
            Files = files,
            Directories = directories
        };
    }

    private static void ApplyManifestMetadata(
        string rootPath,
        InstallRollbackJournal manifest)
    {
        foreach (var file in manifest.Files)
        {
            var filePath = ResolveManifestPath(
                rootPath,
                file.RelativePath);
            var expectedAttributes =
                (FileAttributes)file.Attributes;
            File.SetAttributes(
                filePath,
                expectedAttributes & ~FileAttributes.ReadOnly);
            try
            {
                if (file.CreationTimeUtcTicks > 0)
                {
                    File.SetCreationTimeUtc(
                        filePath,
                        new DateTime(
                            file.CreationTimeUtcTicks,
                            DateTimeKind.Utc));
                }
                File.SetLastWriteTimeUtc(
                    filePath,
                    new DateTime(
                        file.LastWriteTimeUtcTicks,
                        DateTimeKind.Utc));
            }
            finally
            {
                File.SetAttributes(filePath, expectedAttributes);
            }
        }

        foreach (var directory in manifest.Directories
                     .OrderByDescending(
                         static item =>
                             item.RelativePath.Count(
                                 static character =>
                                     character ==
                                     Path.DirectorySeparatorChar)))
        {
            var directoryPath = ResolveManifestPath(
                rootPath,
                directory.RelativePath);
            var expectedAttributes =
                (FileAttributes)directory.Attributes;
            File.SetAttributes(
                directoryPath,
                expectedAttributes & ~FileAttributes.ReadOnly);
            try
            {
                if (directory.CreationTimeUtcTicks > 0)
                {
                    Directory.SetCreationTimeUtc(
                        directoryPath,
                        new DateTime(
                            directory.CreationTimeUtcTicks,
                            DateTimeKind.Utc));
                }
                Directory.SetLastWriteTimeUtc(
                    directoryPath,
                    new DateTime(
                        directory.LastWriteTimeUtcTicks,
                        DateTimeKind.Utc));
            }
            finally
            {
                File.SetAttributes(directoryPath, expectedAttributes);
            }
        }

        var expectedRootAttributes =
            (FileAttributes)manifest.RootAttributes;
        File.SetAttributes(
            rootPath,
            expectedRootAttributes & ~FileAttributes.ReadOnly);
        try
        {
            if (manifest.RootCreationTimeUtcTicks > 0)
            {
                Directory.SetCreationTimeUtc(
                    rootPath,
                    new DateTime(
                        manifest.RootCreationTimeUtcTicks,
                        DateTimeKind.Utc));
            }
            Directory.SetLastWriteTimeUtc(
                rootPath,
                new DateTime(
                    manifest.RootLastWriteTimeUtcTicks,
                    DateTimeKind.Utc));
        }
        finally
        {
            File.SetAttributes(rootPath, expectedRootAttributes);
        }
    }

    private static async Task RunRobocopyMirrorAsync(
        string sourcePath,
        string destinationPath)
    {
        var source = NormalizeDirectoryPath(sourcePath);
        var destination = NormalizeDirectoryPath(destinationPath);
        EnsureTreeContainsNoReparsePoints(source);
        if (DirectoryExistsExact(destination))
            EnsureTreeContainsNoReparsePoints(destination);
        Directory.CreateDirectory(destination);
        EnsureTreeContainsNoReparsePoints(source);
        EnsureTreeContainsNoReparsePoints(destination);

        var startInfo = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     source,
                     destination,
                     "/MIR",
                     "/COPY:DAT",
                     "/DCOPY:DAT",
                     "/XJ",
                     "/R:2",
                     "/W:1",
                     "/NFL",
                     "/NDL",
                     "/NJH",
                     "/NJS",
                     "/NP"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "robocopy process could not be started.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode is < 0 or > 7)
        {
            throw new InvalidOperationException(
                $"robocopy failed. exitCode={process.ExitCode}, stdout={stdout}, stderr={stderr}");
        }
    }

    private static void RemoveNewInstallerRollbackDirectories(
        InstallRollbackJournal journal)
    {
        var installParent = Path.GetDirectoryName(journal.InstallRoot)
            ?? throw new InvalidOperationException(
                "Install root parent directory could not be resolved.");
        var preexisting = new HashSet<string>(
            journal.PreexistingInstallerRollbackDirectories
                .Select(NormalizeDirectoryPath),
            StringComparer.OrdinalIgnoreCase);
        foreach (var directoryPath in
                 EnumerateInstallerRollbackDirectories(installParent))
        {
            if (preexisting.Contains(directoryPath))
                continue;

            EnsurePathIsWithin(directoryPath, installParent);
            EnsureTreeContainsNoReparsePoints(directoryPath);
            DeleteDirectoryTree(directoryPath);
        }
    }

    private static List<string> EnumerateInstallerRollbackDirectories(
        string installParent)
    {
        if (!DirectoryExistsExact(installParent))
            return [];

        return Directory.EnumerateDirectories(
                installParent,
                InstallerRollbackDirectoryPrefix + "*",
                SearchOption.TopDirectoryOnly)
            .Select(NormalizeDirectoryPath)
            .OrderBy(
                static path => path,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CompletePendingCleanupOnce(
        string journalPath,
        InstallRollbackJournal journal,
        Action<string>? log)
    {
        if (journal.Phase != InstallRollbackPhase.CleanupPending)
        {
            journal.Phase = InstallRollbackPhase.CleanupPending;
            WriteJournalAtomically(journalPath, journal);
        }

        try
        {
            var injectedFailure =
                CompletedStateCleanupFailureFactoryForTests?.Invoke(
                    journal.StateRoot);
            if (injectedFailure is not null)
                throw injectedFailure;

            DeleteStateDirectory(journal.StateRoot);
        }
        catch (Exception ex)
        {
            log?.Invoke(
                $"ROLLBACK completed state cleanup deferred. state={journal.StateRoot} error={ex.Message}");
            throw;
        }
    }

    private static void DeleteStateDirectory(string stateRoot)
    {
        if (!DirectoryExistsExact(stateRoot))
            return;

        var rollbackRoot = Path.GetDirectoryName(stateRoot)
            ?? throw new InvalidOperationException(
                "Rollback state parent directory could not be resolved.");
        EnsurePathIsWithin(stateRoot, rollbackRoot);
        EnsureTreeContainsNoReparsePoints(stateRoot);
        DeleteDirectoryTree(
            stateRoot,
            Path.Combine(stateRoot, JournalFileName));
        EnsurePathAbsentExact(
            stateRoot,
            "Rollback state cleanup did not leave the state path absent.");
    }

    private static void MakeTreeWritable(string rootPath)
    {
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(
                     rootPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(entryPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to modify a reparse point: {entryPath}");
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(
                    entryPath,
                    attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static void DeleteDirectoryTree(
        string rootPath,
        string? fileToDeleteLast = null)
    {
        var fullRoot = NormalizeDirectoryPath(rootPath);
        EnsureTreeContainsNoReparsePoints(fullRoot);
        MakeTreeWritable(fullRoot);
        var fullFileToDeleteLast = string.IsNullOrWhiteSpace(fileToDeleteLast)
            ? null
            : InstallRootPathIdentity.Resolve(fileToDeleteLast);
        if (fullFileToDeleteLast is not null)
            EnsurePathIsWithin(fullFileToDeleteLast, fullRoot);

        var directories = Directory.EnumerateDirectories(
                fullRoot,
                "*",
                SearchOption.AllDirectories)
            .OrderByDescending(static path => path.Length)
            .ToArray();
        var files = Directory.EnumerateFiles(
                fullRoot,
                "*",
                SearchOption.AllDirectories)
            .ToArray();
        foreach (var filePath in files.Where(path =>
                     fullFileToDeleteLast is null ||
                     !string.Equals(
                         InstallRootPathIdentity.Resolve(path),
                         fullFileToDeleteLast,
                         StringComparison.OrdinalIgnoreCase)))
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
            File.Delete(filePath);
        }

        foreach (var directoryPath in directories)
        {
            File.SetAttributes(directoryPath, FileAttributes.Directory);
            Directory.Delete(directoryPath);
        }

        if (fullFileToDeleteLast is not null &&
            FileExistsExact(fullFileToDeleteLast))
        {
            File.SetAttributes(
                fullFileToDeleteLast,
                FileAttributes.Normal);
            File.Delete(fullFileToDeleteLast);
            EnsurePathAbsentExact(
                fullFileToDeleteLast,
                "Rollback journal deletion was not durable.");
        }

        File.SetAttributes(fullRoot, FileAttributes.Directory);
        Directory.Delete(fullRoot);
        EnsurePathAbsentExact(
            fullRoot,
            "Rollback directory tree deletion was not durable.");
    }

    private static void EnsureTreeContainsNoReparsePoints(
        string rootPath)
    {
        var fullRoot = NormalizeDirectoryPath(rootPath);
        var current = new DirectoryInfo(fullRoot);
        while (current is not null)
        {
            if (TryGetPathAttributesExact(
                    current.FullName,
                    out var currentAttributes) &&
                (currentAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Reparse-point paths cannot be used for rollback: {current.FullName}");
            }

            current = current.Parent;
        }

        if (!DirectoryExistsExact(fullRoot))
            return;

        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var directoryPath = pending.Pop();
            foreach (var entryPath in
                     Directory.EnumerateFileSystemEntries(directoryPath))
            {
                var attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"Rollback tree contains a reparse point: {entryPath}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entryPath);
            }
        }
    }

    private static string ResolveManifestPath(
        string rootPath,
        string relativePath)
    {
        var safeRelativePath = ValidateRelativePath(relativePath);
        var fullRoot = NormalizeDirectoryPath(rootPath);
        var resolvedPath = InstallRootPathIdentity.Resolve(
            Path.Combine(fullRoot, safeRelativePath));
        EnsurePathIsWithin(resolvedPath, fullRoot);
        return resolvedPath;
    }

    private static string ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathFullyQualified(relativePath) ||
            string.Equals(relativePath, ".", StringComparison.Ordinal) ||
            relativePath.Split(
                    [Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(
                    static segment =>
                        string.Equals(
                            segment,
                            "..",
                            StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Unsafe rollback manifest path: {relativePath}");
        }

        return relativePath;
    }

    private static void EnsurePathIsWithin(
        string candidatePath,
        string parentPath)
    {
        var candidate = NormalizeDirectoryPath(candidatePath);
        var parent = NormalizeDirectoryPath(parentPath);
        var prefix = parent + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path escapes its expected parent. candidate={candidate}, parent={parent}");
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            NormalizeDirectoryPath(left),
            NormalizeDirectoryPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool DirectoryExistsExact(string path)
    {
        if (!TryGetPathAttributesExact(path, out var attributes))
            return false;

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException(
                $"Expected a directory but found a file: {path}");
        }

        return true;
    }

    private static bool FileExistsExact(string path)
    {
        if (!TryGetPathAttributesExact(path, out var attributes))
            return false;

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new IOException(
                $"Expected a file but found a directory: {path}");
        }

        return true;
    }

    private static bool TryGetPathAttributesExact(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes =
                PresenceProbeForTests?.Invoke(path) ??
                File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void EnsurePathAbsentExact(
        string path,
        string message)
    {
        if (TryGetPathAttributesExact(path, out _))
        {
            throw new IOException($"{message} path={path}");
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Directory path is required.", nameof(path));

        return InstallRootPathIdentity.Resolve(path);
    }
}
