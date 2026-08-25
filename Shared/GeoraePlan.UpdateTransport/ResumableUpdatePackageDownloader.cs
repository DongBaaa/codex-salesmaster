using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace GeoraePlan.UpdateTransport;

public sealed record UpdateDownloadProgress(long DownloadedBytes, long TotalBytes);

public sealed class ResumableUpdatePackageDownloader
{
    private const int BufferSize = 128 * 1024;
    private static readonly ConcurrentDictionary<string, TargetLockEntry> TargetLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> DownloadAsync(
        Uri packageUri,
        string targetPath,
        long expectedFileSize,
        string expectedSha256,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageUri);
        if (!packageUri.IsAbsoluteUri ||
            (packageUri.Scheme != Uri.UriSchemeHttps && packageUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                "The update package URI must be an absolute HTTP(S) URI.",
                nameof(packageUri));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(sendAsync);
        if (expectedFileSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedFileSize));

        var normalizedHash = NormalizeSha256(expectedSha256);
        var finalPath = Path.GetFullPath(targetPath);
        var targetLock = AcquireTargetLock(finalPath);
        var entered = false;
        try
        {
            await targetLock.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;

            var parent = Path.GetDirectoryName(finalPath) ??
                throw new InvalidOperationException("The update target has no parent directory.");
            Directory.CreateDirectory(parent);

            if (File.Exists(finalPath) &&
                await IsExactFileAsync(
                    finalPath,
                    expectedFileSize,
                    normalizedHash,
                    cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(new(expectedFileSize, expectedFileSize));
                return finalPath;
            }

            var partialPath = finalPath + ".partial";
            var partialLength = GetBoundedPartialLength(partialPath, expectedFileSize);
            using var request = new HttpRequestMessage(HttpMethod.Get, packageUri);
            if (partialLength > 0)
                request.Headers.Range = new RangeHeaderValue(partialLength, null);

            using var response = await sendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (partialLength == expectedFileSize &&
                    HasExactUnsatisfiedRangeLength(
                        response.Content.Headers.ContentRange,
                        expectedFileSize) &&
                    await IsExactFileAsync(
                        partialPath,
                        expectedFileSize,
                        normalizedHash,
                        cancellationToken).ConfigureAwait(false))
                {
                    Publish(partialPath, finalPath);
                    progress?.Report(new(expectedFileSize, expectedFileSize));
                    return finalPath;
                }

                if (partialLength > 0)
                    TryDelete(partialPath);
                throw new InvalidDataException(
                    "The server rejected a partial offset that is not a complete verified package.");
            }

            var append = false;
            if (partialLength > 0 && response.StatusCode == HttpStatusCode.PartialContent)
            {
                ValidatePartialContentRange(
                    response.Content.Headers.ContentRange,
                    partialLength,
                    expectedFileSize);
                append = true;
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                partialLength = 0;
            }
            else if (partialLength == 0 && response.StatusCode == HttpStatusCode.PartialContent)
            {
                ValidatePartialContentRange(
                    response.Content.Headers.ContentRange,
                    0,
                    expectedFileSize);
            }
            else
            {
                response.EnsureSuccessStatusCode();
                throw new InvalidDataException(
                    $"Unsupported update response: {(int)response.StatusCode}.");
            }

            ValidateResponseLength(
                response.Content.Headers.ContentLength,
                partialLength,
                expectedFileSize);

            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var destination = new FileStream(
                partialPath,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan |
                FileOptions.WriteThrough))
            {
                var buffer = new byte[BufferSize];
                var downloaded = partialLength;
                try
                {
                    while (true)
                    {
                        var read = await source
                            .ReadAsync(buffer.AsMemory(), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                            break;
                        if (downloaded > expectedFileSize - read)
                        {
                            throw new InvalidDataException(
                                "The update response exceeds the manifest file size.");
                        }

                        await destination
                            .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                        downloaded += read;
                        progress?.Report(new(downloaded, expectedFileSize));
                    }
                }
                finally
                {
                    await destination
                        .FlushAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    destination.Flush(flushToDisk: true);
                }
            }

            if (!await IsExactFileAsync(
                    partialPath,
                    expectedFileSize,
                    normalizedHash,
                    cancellationToken).ConfigureAwait(false))
            {
                TryDelete(partialPath);
                throw new InvalidDataException(
                    "The downloaded update does not match the manifest size and SHA-256.");
            }

            Publish(partialPath, finalPath);
            progress?.Report(new(expectedFileSize, expectedFileSize));
            return finalPath;
        }
        finally
        {
            if (entered)
                targetLock.Gate.Release();
            ReleaseTargetLock(finalPath, targetLock);
        }
    }

    public static int ActiveTargetLockCount => TargetLocks.Count;

    private static TargetLockEntry AcquireTargetLock(string targetPath)
    {
        while (true)
        {
            var entry = TargetLocks.GetOrAdd(
                targetPath,
                static _ => new TargetLockEntry());
            lock (entry.SyncRoot)
            {
                if (entry.Removed)
                    continue;

                entry.ReferenceCount++;
                return entry;
            }
        }
    }

    private static void ReleaseTargetLock(string targetPath, TargetLockEntry entry)
    {
        var dispose = false;
        lock (entry.SyncRoot)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount < 0)
            {
                throw new InvalidOperationException(
                    "The update target lock reference count became negative.");
            }

            if (entry.ReferenceCount == 0)
            {
                entry.Removed = true;
                if (!TargetLocks.TryRemove(
                        new KeyValuePair<string, TargetLockEntry>(targetPath, entry)))
                {
                    throw new InvalidOperationException(
                        "The update target lock could not be removed exactly.");
                }

                dispose = true;
            }
        }

        if (dispose)
            entry.Gate.Dispose();
    }

    private static long GetBoundedPartialLength(
        string partialPath,
        long expectedFileSize)
    {
        if (!File.Exists(partialPath))
            return 0;

        var length = new FileInfo(partialPath).Length;
        if (length < 0 || length > expectedFileSize)
        {
            TryDelete(partialPath);
            return 0;
        }

        return length;
    }

    private static void ValidatePartialContentRange(
        ContentRangeHeaderValue? range,
        long expectedStart,
        long expectedLength)
    {
        if (range is null ||
            !range.HasRange ||
            !range.HasLength ||
            range.From != expectedStart ||
            range.To is null ||
            range.To < range.From ||
            range.Length != expectedLength)
        {
            throw new InvalidDataException(
                "The server returned a mismatched Content-Range.");
        }
    }

    private static bool HasExactUnsatisfiedRangeLength(
        ContentRangeHeaderValue? range,
        long expectedLength)
        => range is { HasRange: false, HasLength: true } &&
           range.Length == expectedLength;

    private static void ValidateResponseLength(
        long? responseLength,
        long offset,
        long expectedLength)
    {
        if (responseLength is null)
            return;
        if (responseLength < 0 || responseLength != expectedLength - offset)
        {
            throw new InvalidDataException(
                "The response Content-Length does not match the manifest size.");
        }
    }

    private static async Task<bool> IsExactFileAsync(
        string path,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedLength)
            return false;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var actual = await sha
            .ComputeHashAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(
            actual,
            Convert.FromHexString(expectedSha256));
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 64 ||
            normalized.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The expected SHA-256 must be exactly 64 hexadecimal characters.",
                nameof(value));
        }

        return normalized;
    }

    private static void Publish(string partialPath, string finalPath)
        => File.Move(partialPath, finalPath, overwrite: true);

    private static void TryDelete(string path) => File.Delete(path);

    private sealed class TargetLockEntry
    {
        public object SyncRoot { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Removed { get; set; }
    }
}
