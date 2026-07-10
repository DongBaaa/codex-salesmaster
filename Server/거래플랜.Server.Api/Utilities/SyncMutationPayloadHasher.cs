using System.Security.Cryptography;
using System.Text.Json;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Utilities;

public static class SyncMutationPayloadHasher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string Compute(SyncEntityDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var stream = new HashingWriteStream(hash))
        {
            JsonSerializer.Serialize(stream, dto, dto.GetType(), SerializerOptions);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class HashingWriteStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => hash.AppendData(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer)
            => hash.AppendData(buffer);
    }
}
