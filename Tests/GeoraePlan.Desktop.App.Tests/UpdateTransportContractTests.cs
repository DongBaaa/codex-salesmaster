using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GeoraePlan.UpdateTransport;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class UpdateTransportContractTests
{
    [Fact]
    public async Task InterruptedDownload_ResumesFromExactOffset()
    {
        using var fixture = new Fixture();
        const int firstLength = 4_321;
        var first = true;
        var downloader = new ResumableUpdatePackageDownloader();

        Task<HttpResponseMessage> Send(
            HttpRequestMessage request,
            CancellationToken _)
        {
            var offset = RequestOffset(request);
            if (first)
            {
                first = false;
                Assert.Equal(0, offset);
                return Task.FromResult(Response(
                    HttpStatusCode.OK,
                    new ThrowAfterStream(fixture.Bytes, firstLength),
                    fixture.Bytes.Length));
            }

            Assert.Equal(firstLength, offset);
            return Task.FromResult(RangeResponse(fixture.Bytes, offset));
        }

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(
            PackageUri(),
            fixture.TargetPath,
            fixture.Bytes.Length,
            fixture.Hash,
            Send));
        Assert.Equal(firstLength, new FileInfo(fixture.PartialPath).Length);

        await downloader.DownloadAsync(
            PackageUri(),
            fixture.TargetPath,
            fixture.Bytes.Length,
            fixture.Hash,
            Send);
        fixture.AssertPublished();
    }

    [Fact]
    public async Task FullResponseToRangeRequest_ResetsPartial()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.PartialPath, fixture.Bytes[..1_337]);
        var downloader = new ResumableUpdatePackageDownloader();

        await downloader.DownloadAsync(
            PackageUri(),
            fixture.TargetPath,
            fixture.Bytes.Length,
            fixture.Hash,
            (request, _) =>
            {
                Assert.Equal(1_337, RequestOffset(request));
                return Task.FromResult(Response(
                    HttpStatusCode.OK,
                    new MemoryStream(fixture.Bytes),
                    fixture.Bytes.Length));
            });

        fixture.AssertPublished();
    }

    [Fact]
    public async Task MismatchedContentRange_FailsClosed()
    {
        using var fixture = new Fixture();
        var prefix = fixture.Bytes[..2_000];
        await File.WriteAllBytesAsync(fixture.PartialPath, prefix);
        var downloader = new ResumableUpdatePackageDownloader();

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            PackageUri(),
            fixture.TargetPath,
            fixture.Bytes.Length,
            fixture.Hash,
            (request, _) =>
            {
                var offset = RequestOffset(request);
                var response = RangeResponse(fixture.Bytes, offset);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    offset + 1,
                    fixture.Bytes.Length - 1,
                    fixture.Bytes.Length);
                return Task.FromResult(response);
            }));

        Assert.False(File.Exists(fixture.TargetPath));
        Assert.Equal(prefix, await File.ReadAllBytesAsync(fixture.PartialPath));
    }

    [Fact]
    public async Task CompletePartialWith416_VerifiesAndPublishes()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.PartialPath, fixture.Bytes);
        var downloader = new ResumableUpdatePackageDownloader();

        await downloader.DownloadAsync(
            PackageUri(),
            fixture.TargetPath,
            fixture.Bytes.Length,
            fixture.Hash,
            (request, _) =>
            {
                Assert.Equal(fixture.Bytes.Length, RequestOffset(request));
                var response = new HttpResponseMessage(
                    HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    Content = new ByteArrayContent([])
                };
                response.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(fixture.Bytes.Length);
                return Task.FromResult(response);
            });

        fixture.AssertPublished();
    }

    [Fact]
    public async Task CorruptCompletePartialWith416_IsDeleted()
    {
        using var fixture = new Fixture();
        var corrupt = fixture.Bytes.ToArray();
        corrupt[0] ^= 0x5A;
        await File.WriteAllBytesAsync(fixture.PartialPath, corrupt);
        var downloader = new ResumableUpdatePackageDownloader();

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            PackageUri(),
            fixture.TargetPath,
            fixture.Bytes.Length,
            fixture.Hash,
            (_, _) => Task.FromResult(UnsatisfiedRangeResponse(
                fixture.Bytes.Length))));

        Assert.False(File.Exists(fixture.TargetPath));
        Assert.False(File.Exists(fixture.PartialPath));
    }

    [Fact]
    public async Task IncompletePartialWith416_IsDeletedForCleanRetry()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.PartialPath, fixture.Bytes[..1_024]);
        var downloader = new ResumableUpdatePackageDownloader();

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            PackageUri(),
            fixture.TargetPath,
            fixture.Bytes.Length,
            fixture.Hash,
            (_, _) => Task.FromResult(UnsatisfiedRangeResponse(
                fixture.Bytes.Length))));

        Assert.False(File.Exists(fixture.TargetPath));
        Assert.False(File.Exists(fixture.PartialPath));
    }

    [Fact]
    public async Task HashMismatch_DoesNotPublishAndDeletesPartial()
    {
        using var fixture = new Fixture();
        var corrupt = fixture.Bytes.ToArray();
        corrupt[^1] ^= 0x7F;
        var downloader = new ResumableUpdatePackageDownloader();

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            PackageUri(),
            fixture.TargetPath,
            fixture.Bytes.Length,
            fixture.Hash,
            (_, _) => Task.FromResult(Response(
                HttpStatusCode.OK,
                new MemoryStream(corrupt),
                corrupt.Length))));

        Assert.False(File.Exists(fixture.TargetPath));
        Assert.False(File.Exists(fixture.PartialPath));
    }

    [Fact]
    public async Task ConcurrentRequests_UseSingleTransferAndPublisher()
    {
        using var fixture = new Fixture();
        var downloader = new ResumableUpdatePackageDownloader();
        var sends = 0;

        async Task<HttpResponseMessage> Send(
            HttpRequestMessage request,
            CancellationToken _)
        {
            Assert.Equal(0, RequestOffset(request));
            Interlocked.Increment(ref sends);
            await Task.Delay(50);
            return Response(
                HttpStatusCode.OK,
                new MemoryStream(fixture.Bytes),
                fixture.Bytes.Length);
        }

        await Task.WhenAll(
            downloader.DownloadAsync(
                PackageUri(),
                fixture.TargetPath,
                fixture.Bytes.Length,
                fixture.Hash,
                Send),
            downloader.DownloadAsync(
                PackageUri(),
                fixture.TargetPath,
                fixture.Bytes.Length,
                fixture.Hash,
                Send));

        Assert.Equal(1, sends);
        fixture.AssertPublished();
    }

    [Fact]
    public async Task TargetLocks_AreReleasedAfterSequentialAndConcurrentUse()
    {
        Assert.Equal(0, ResumableUpdatePackageDownloader.ActiveTargetLockCount);
        var downloader = new ResumableUpdatePackageDownloader();
        for (var iteration = 0; iteration < 25; iteration++)
        {
            using var fixture = new Fixture();
            Task<HttpResponseMessage> Send(
                HttpRequestMessage _,
                CancellationToken __)
                => Task.FromResult(Response(
                    HttpStatusCode.OK,
                    new MemoryStream(fixture.Bytes),
                    fixture.Bytes.Length));

            await Task.WhenAll(
                downloader.DownloadAsync(
                    PackageUri(),
                    fixture.TargetPath,
                    fixture.Bytes.Length,
                    fixture.Hash,
                    Send),
                downloader.DownloadAsync(
                    PackageUri(),
                    fixture.TargetPath,
                    fixture.Bytes.Length,
                    fixture.Hash,
                    Send));
            Assert.Equal(0, ResumableUpdatePackageDownloader.ActiveTargetLockCount);
        }
    }

    [Fact]
    public void DesktopAndMobileUseSameTransportContract()
    {
        var root = FindRepositoryRoot();
        var desktop = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs"));
        var mobile = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "MobileAppUpdateService.cs"));

        Assert.Contains(
            "ResumableUpdatePackageDownloader",
            desktop,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResumableUpdatePackageDownloader",
            mobile,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CreateUniquePackageDownloadPath",
            desktop,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Guid.NewGuid():N}.download",
            mobile,
            StringComparison.Ordinal);
    }

    private static long RequestOffset(HttpRequestMessage request)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.True(request.RequestUri?.IsAbsoluteUri);
        var ranges = request.Headers.Range?.Ranges.ToArray() ?? [];
        if (ranges.Length == 0)
            return 0;
        var range = Assert.Single(ranges);
        Assert.NotNull(range.From);
        Assert.Null(range.To);
        return range.From!.Value;
    }

    private static Uri PackageUri()
        => new("https://updates.example.invalid/package.bin");

    private static HttpResponseMessage RangeResponse(byte[] bytes, long offset)
    {
        var remaining = bytes[(int)offset..];
        var response = Response(
            HttpStatusCode.PartialContent,
            new MemoryStream(remaining),
            remaining.Length);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
            offset,
            bytes.Length - 1,
            bytes.Length);
        return response;
    }

    private static HttpResponseMessage UnsatisfiedRangeResponse(long length)
    {
        var response = new HttpResponseMessage(
            HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            Content = new ByteArrayContent([])
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(length);
        return response;
    }

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        Stream stream,
        long length)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StreamContent(stream)
        };
        response.Content.Headers.ContentLength = length;
        return response;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Mobile")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "georaeplan-update-transport-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            TargetPath = Path.Combine(Root, "package.bin");
            Bytes = Enumerable.Range(0, 31_337)
                .Select(index => (byte)((index * 31 + 17) % 251))
                .ToArray();
            Hash = Convert.ToHexString(SHA256.HashData(Bytes));
        }

        public string Root { get; }
        public string TargetPath { get; }
        public string PartialPath => TargetPath + ".partial";
        public byte[] Bytes { get; }
        public string Hash { get; }

        public void AssertPublished()
        {
            Assert.False(File.Exists(PartialPath));
            Assert.Equal(Bytes, File.ReadAllBytes(TargetPath));
        }

        public void Dispose()
            => Directory.Delete(Root, recursive: true);
    }

    private sealed class ThrowAfterStream(byte[] bytes, int allowed) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= allowed)
                throw new IOException("Injected interrupted response.");
            var count = Math.Min(buffer.Length, allowed - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
