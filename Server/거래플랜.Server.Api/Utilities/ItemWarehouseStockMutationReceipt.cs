using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Utilities;

internal static class ItemWarehouseStockMutationReceipt
{
    internal const string MutationIdPrefix =
        "server-receipt:item-warehouse-stock:v1:";

    private const string CanonicalFormat =
        "georaeplan:item-warehouse-stock-receipt:v1";

    internal static ItemWarehouseStockReceiptIdentity Create(
        ItemWarehouseStockDto dto,
        string? deviceId)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var normalizedDeviceId =
            NormalizeDeviceId(deviceId);
        var normalizedWarehouseCode =
            OfficeCodeCatalog.NormalizeWarehouseCodeLoose(
                dto.WarehouseCode);
        var normalizedUpdatedAtUtc =
            NormalizeUpdatedAtUtc(dto.UpdatedAtUtc);

        using var hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        AppendString(hash, CanonicalFormat);
        AppendString(hash, normalizedDeviceId);
        AppendString(
            hash,
            dto.ItemId.ToString("D"));
        AppendString(hash, normalizedWarehouseCode);
        AppendString(
            hash,
            dto.Quantity.ToString(
                "G29",
                CultureInfo.InvariantCulture));
        AppendInt64(hash, normalizedUpdatedAtUtc.Ticks);
        AppendInt64(hash, dto.Revision);
        AppendInt64(hash, dto.ExpectedRevision);
        hash.AppendData([dto.IsDeleted ? (byte)1 : (byte)0]);

        var payloadHash =
            Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
        return new ItemWarehouseStockReceiptIdentity(
            MutationId:
                $"{MutationIdPrefix}{payloadHash}",
            DeviceId: normalizedDeviceId,
            EntityId:
                $"{dto.ItemId:D}|{normalizedWarehouseCode}",
            ExpectedRevision: dto.ExpectedRevision,
            PayloadHash: payloadHash);
    }

    internal static bool IsReservedMutationId(
        string? mutationId)
        => ProcessedSyncMutationRecorder
            .NormalizeMutationId(mutationId)
            .StartsWith(
                MutationIdPrefix,
                StringComparison.Ordinal);

    internal static DateTime NormalizeUpdatedAtUtc(
        DateTime value)
    {
        if (value == default)
            return DateTime.UnixEpoch;

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local =>
                value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(
                value,
                DateTimeKind.Utc)
        };
    }

    private static string NormalizeDeviceId(
        string? deviceId)
    {
        var normalized =
            (deviceId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? "unknown-device"
            : normalized;
    }

    private static void AppendString(
        IncrementalHash hash,
        string value)
    {
        var byteCount =
            Encoding.UTF8.GetByteCount(value);
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(
            lengthBytes,
            byteCount);
        hash.AppendData(lengthBytes);

        if (byteCount == 0)
            return;

        var valueBytes =
            Encoding.UTF8.GetBytes(value);
        hash.AppendData(valueBytes);
    }

    private static void AppendInt64(
        IncrementalHash hash,
        long value)
    {
        Span<byte> valueBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(
            valueBytes,
            value);
        hash.AppendData(valueBytes);
    }
}

internal sealed record ItemWarehouseStockReceiptIdentity(
    string MutationId,
    string DeviceId,
    string EntityId,
    long ExpectedRevision,
    string PayloadHash);
