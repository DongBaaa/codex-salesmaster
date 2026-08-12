using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 거래플랜.Desktop.App.Infrastructure;

namespace 거래플랜.Desktop.App.Services;

internal enum DesktopCompatibilityStoreFaultPoint
{
    BeforeMarkerWrite,
    AfterMarkerWrite,
    BeforeSlotPublish,
    AfterSlotPublish,
    BeforePointerPublish,
    AfterPointerPublish,
    BeforeClearSlots,
    AfterClearSlots,
    BeforeClearMarker
}

internal sealed class DesktopCompatibilityEvidenceStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumEnvelopeBytes = 64 * 1024;
    private const string MarkerFileName = "blocked.marker";
    private const string SlotAFileName = "evidence-a.json";
    private const string SlotBFileName = "evidence-b.json";
    private const string PointerFileName = "active.slot";
    private readonly string _rootDirectory;
    private readonly Func<
        DesktopCompatibilityStoreFaultPoint,
        Exception?>? _faultProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            WriteIndented = false
        };

    public DesktopCompatibilityEvidenceStore()
        : this(
            AppPaths.CompatibilityDir,
            faultProvider: null)
    {
    }

    internal DesktopCompatibilityEvidenceStore(
        string rootDirectory,
        Func<
            DesktopCompatibilityStoreFaultPoint,
            Exception?>? faultProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            rootDirectory);
        _rootDirectory =
            Path.GetFullPath(rootDirectory);
        _faultProvider = faultProvider;
    }

    internal string RootDirectory =>
        _rootDirectory;

    public async Task<
        DesktopCompatibilityEvidenceLoadResult>
        LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return LoadCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PersistAsync(
        DesktopCompatibilityEvidence evidence,
        CancellationToken ct = default)
    {
        if (!DesktopCompatibilityPolicy
                .IsValidEvidenceShape(evidence))
        {
            throw new InvalidDataException(
                "Desktop compatibility evidence shape is invalid.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(
                _rootDirectory);
            ThrowIfFault(
                DesktopCompatibilityStoreFaultPoint
                    .BeforeMarkerWrite);
            WriteDurableFile(
                MarkerPath,
                Encoding.ASCII.GetBytes(
                    "blocked-v1"));
            ThrowIfFault(
                DesktopCompatibilityStoreFaultPoint
                    .AfterMarkerWrite);

            var current = LoadCore();
            var generation = current.State ==
                             DesktopCompatibilityEvidenceState
                                 .Valid
                ? checked(current.Generation + 1)
                : 1;
            var activeSlot = ReadPointerSafely();
            var inactiveSlot =
                string.Equals(
                    activeSlot,
                    "a",
                    StringComparison.Ordinal)
                    ? "b"
                    : "a";
            var targetPath = inactiveSlot == "a"
                ? SlotAPath
                : SlotBPath;
            var envelopeBytes =
                SerializeEnvelope(
                    generation,
                    evidence);
            if (envelopeBytes.Length >
                MaximumEnvelopeBytes)
            {
                throw new InvalidDataException(
                    "Desktop compatibility evidence exceeds the size limit.");
            }

            ThrowIfFault(
                DesktopCompatibilityStoreFaultPoint
                    .BeforeSlotPublish);
            PublishDurableFile(
                targetPath,
                envelopeBytes);
            ThrowIfFault(
                DesktopCompatibilityStoreFaultPoint
                    .AfterSlotPublish);
            ThrowIfFault(
                DesktopCompatibilityStoreFaultPoint
                    .BeforePointerPublish);
            PublishDurableFile(
                PointerPath,
                Encoding.ASCII.GetBytes(
                    inactiveSlot));
            ThrowIfFault(
                DesktopCompatibilityStoreFaultPoint
                    .AfterPointerPublish);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(
                _rootDirectory);
            ThrowIfFault(
                DesktopCompatibilityStoreFaultPoint
                    .BeforeMarkerWrite);
            WriteDurableFile(
                MarkerPath,
                Encoding.ASCII.GetBytes(
                    "blocked-v1"));
            ThrowIfFault(
                DesktopCompatibilityStoreFaultPoint
                    .AfterMarkerWrite);
            ThrowIfFault(
                DesktopCompatibilityStoreFaultPoint
                    .BeforeClearSlots);

            DeleteIfExists(SlotAPath);
            DeleteIfExists(SlotBPath);
            DeleteIfExists(PointerPath);
            ThrowIfFault(
                DesktopCompatibilityStoreFaultPoint
                    .AfterClearSlots);
            ThrowIfFault(
                DesktopCompatibilityStoreFaultPoint
                    .BeforeClearMarker);
            DeleteIfExists(MarkerPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DesktopCompatibilityEvidenceLoadResult LoadCore()
    {
        try
        {
            if (!File.Exists(MarkerPath))
                return DesktopCompatibilityEvidenceLoadResult.None;

            var candidates = new[]
                {
                    TryReadSlot(SlotAPath),
                    TryReadSlot(SlotBPath)
                }
                .Where(
                    static candidate =>
                        candidate is not null)
                .Cast<SlotCandidate>()
                .OrderByDescending(
                    static candidate =>
                        candidate.Generation)
                .ToList();
            if (candidates.Count == 0)
            {
                return new DesktopCompatibilityEvidenceLoadResult(
                    DesktopCompatibilityEvidenceState
                        .Unreadable,
                    null,
                    0,
                    "marker-without-valid-slot");
            }

            var selected = candidates[0];
            return new DesktopCompatibilityEvidenceLoadResult(
                DesktopCompatibilityEvidenceState.Valid,
                selected.Evidence,
                selected.Generation,
                "valid");
        }
        catch
        {
            return new DesktopCompatibilityEvidenceLoadResult(
                DesktopCompatibilityEvidenceState
                    .Unreadable,
                null,
                0,
                "read-failure");
        }
    }

    private SlotCandidate? TryReadSlot(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length <= 0 ||
                fileInfo.Length > MaximumEnvelopeBytes)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            using var document =
                JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind !=
                JsonValueKind.Object)
            {
                return null;
            }

            var propertyNames = document.RootElement
                .EnumerateObject()
                .Select(
                    static property =>
                        property.Name)
                .OrderBy(
                    static name => name,
                    StringComparer.Ordinal)
                .ToArray();
            if (!propertyNames.SequenceEqual(
                        new[]
                        {
                            "generation",
                            "payload",
                            "schemaVersion",
                            "sha256"
                        },
                        StringComparer.Ordinal))
            {
                return null;
            }

            var envelope =
                JsonSerializer.Deserialize<Envelope>(
                    bytes,
                    JsonOptions);
            if (envelope is null ||
                envelope.SchemaVersion !=
                CurrentSchemaVersion ||
                envelope.Generation < 1 ||
                envelope.Payload is null ||
                string.IsNullOrWhiteSpace(
                    envelope.Sha256) ||
                envelope.Sha256.Length != 64 ||
                !DesktopCompatibilityPolicy
                    .IsValidEvidenceShape(
                        envelope.Payload))
            {
                return null;
            }

            var payloadBytes =
                JsonSerializer.SerializeToUtf8Bytes(
                    envelope.Payload,
                    JsonOptions);
            var expectedHash =
                Convert.ToHexString(
                    SHA256.HashData(payloadBytes));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expectedHash),
                    Encoding.ASCII.GetBytes(
                        envelope.Sha256.ToUpperInvariant())))
            {
                return null;
            }

            return new SlotCandidate(
                envelope.Generation,
                envelope.Payload);
        }
        catch
        {
            return null;
        }
    }

    private byte[] SerializeEnvelope(
        long generation,
        DesktopCompatibilityEvidence evidence)
    {
        var payloadBytes =
            JsonSerializer.SerializeToUtf8Bytes(
                evidence,
                JsonOptions);
        var hash = Convert.ToHexString(
            SHA256.HashData(payloadBytes));
        return JsonSerializer.SerializeToUtf8Bytes(
            new Envelope
            {
                SchemaVersion =
                    CurrentSchemaVersion,
                Generation = generation,
                Payload = evidence,
                Sha256 = hash
            },
            JsonOptions);
    }

    private string ReadPointerSafely()
    {
        try
        {
            var value = File.Exists(PointerPath)
                ? File.ReadAllText(
                        PointerPath,
                        Encoding.ASCII)
                    .Trim()
                : string.Empty;
            return value is "a" or "b"
                ? value
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void PublishDurableFile(
        string targetPath,
        byte[] bytes)
    {
        var directory =
            Path.GetDirectoryName(targetPath) ??
            throw new InvalidOperationException(
                "Compatibility evidence directory is missing.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteDurableFile(
                temporaryPath,
                bytes);
            if (File.Exists(targetPath))
            {
                File.Replace(
                    temporaryPath,
                    targetPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(
                    temporaryPath,
                    targetPath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // The marker remains authoritative and fail-closed.
            }
        }
    }

    private static void WriteDurableFile(
        string path,
        byte[] bytes)
    {
        var directory =
            Path.GetDirectoryName(path) ??
            throw new InvalidOperationException(
                "Compatibility evidence directory is missing.");
        Directory.CreateDirectory(directory);
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteIfExists(
        string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private void ThrowIfFault(
        DesktopCompatibilityStoreFaultPoint point)
    {
        var exception = _faultProvider?.Invoke(point);
        if (exception is not null)
            throw exception;
    }

    private string MarkerPath =>
        Path.Combine(_rootDirectory, MarkerFileName);
    private string SlotAPath =>
        Path.Combine(_rootDirectory, SlotAFileName);
    private string SlotBPath =>
        Path.Combine(_rootDirectory, SlotBFileName);
    private string PointerPath =>
        Path.Combine(_rootDirectory, PointerFileName);

    private sealed record SlotCandidate(
        long Generation,
        DesktopCompatibilityEvidence Evidence);

    private sealed class Envelope
    {
        public Envelope()
        {
        }

        public int SchemaVersion { get; set; }
        public long Generation { get; set; }
        public DesktopCompatibilityEvidence? Payload { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}
