using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Utilities;

public static class SyncMutationPayloadHasher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string Compute(SyncEntityDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return ComputeWithMutationId(
            dto,
            ProcessedSyncMutationRecorder.NormalizeMutationId(dto.MutationId),
            canonicalizeSemanticPayload: true);
    }

    public static bool Matches(
        SyncEntityDto dto,
        string? storedPayloadHash,
        string? storedMutationId)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (string.IsNullOrWhiteSpace(storedPayloadHash))
            return false;

        return EvaluateForReceiptReplay(
            dto,
            storedPayloadHash,
            storedMutationId).StoredPayloadMatches;
    }

    internal static PayloadHashEvaluation EvaluateForReceiptReplay(
        SyncEntityDto dto,
        string? storedPayloadHash,
        string? storedMutationId)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var canonicalPayloadHash = Compute(dto);
        var storedPayloadMatches = MatchesCanonicalPayloadHash(
            dto,
            canonicalPayloadHash,
            storedPayloadHash,
            storedMutationId);
        return new PayloadHashEvaluation(
            canonicalPayloadHash,
            storedPayloadMatches);
    }

    private static bool MatchesCanonicalPayloadHash(
        SyncEntityDto dto,
        string canonicalPayloadHash,
        string? storedPayloadHash,
        string? storedMutationId)
    {
        if (string.IsNullOrWhiteSpace(storedPayloadHash))
            return false;

        if (string.Equals(
                storedPayloadHash,
                canonicalPayloadHash,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Receipts created before mutation-id canonicalization hashed the original
        // (trimmed) casing. Recompute only that historical representation; all
        // semantic payload fields still have to match.
        var storedLegacyMutationId = storedMutationId ?? string.Empty;
        var trimmedLegacyMutationId = string.IsNullOrWhiteSpace(storedLegacyMutationId)
            ? string.Empty
            : storedLegacyMutationId.Trim();
        if (string.Equals(
                storedPayloadHash,
                ComputeWithMutationId(
                    dto,
                    trimmedLegacyMutationId,
                    canonicalizeSemanticPayload: true),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 기존 receipt는 파생 표시값/중복 자식 컬렉션까지 포함한 전체 DTO를 해시했다.
        // 전환 이후에도 동일한 과거 payload 재전송은 계속 인정한다.
        var normalizedMutationId =
            ProcessedSyncMutationRecorder.NormalizeMutationId(dto.MutationId);
        var legacyPayloadMatches = string.Equals(
                   storedPayloadHash,
                   ComputeWithMutationId(
                       dto,
                       normalizedMutationId,
                       canonicalizeSemanticPayload: false),
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   storedPayloadHash,
                   ComputeWithMutationId(
                       dto,
                       trimmedLegacyMutationId,
                       canonicalizeSemanticPayload: false),
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   storedPayloadHash,
                   ComputeWithMutationId(
                       dto,
                       storedLegacyMutationId,
                       canonicalizeSemanticPayload: false),
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   storedPayloadHash,
                   ComputeLegacyRawPayload(dto),
                   StringComparison.OrdinalIgnoreCase);
        if (legacyPayloadMatches)
            return true;

        if (dto is not ItemDto itemDto)
            return false;

        // 구버전 서버는 아래 품목 확장 필드를 관찰하지 못한 채 receipt를 기록했다.
        // 동일 mutation의 재시도라면 그 서버가 실제로 본 pre-field shape도 역사적
        // payload 후보로 인정하되, 나머지 기존 필드는 계속 정확히 일치해야 한다.
        var legacyItemMutationIds = new[]
        {
            normalizedMutationId,
            trimmedLegacyMutationId,
            storedLegacyMutationId,
            itemDto.MutationId ?? string.Empty
        };
        return legacyItemMutationIds
            .Distinct(StringComparer.Ordinal)
            .Any(mutationId => string.Equals(
                storedPayloadHash,
                ComputeLegacyItemPayloadWithoutCatalogExtensions(
                    itemDto,
                    mutationId),
                StringComparison.OrdinalIgnoreCase));
    }

    private static string ComputeLegacyItemPayloadWithoutCatalogExtensions(
        ItemDto dto,
        string mutationId)
    {
        var payload = JsonSerializer.SerializeToNode(
            dto,
            dto.GetType(),
            SerializerOptions);
        if (payload is JsonObject payloadObject)
        {
            payloadObject.Remove(GetSerializedPropertyName(nameof(ItemDto.BoxQuantity)));
            payloadObject.Remove(GetSerializedPropertyName(nameof(ItemDto.StorageLocation)));
            payloadObject.Remove(GetSerializedPropertyName(nameof(ItemDto.LastPurchaseDate)));
            payloadObject.Remove(GetSerializedPropertyName(nameof(ItemDto.LastPurchaseDateSpecified)));
            payloadObject.Remove(GetSerializedPropertyName(nameof(ItemDto.LastSaleDate)));
            payloadObject.Remove(GetSerializedPropertyName(nameof(ItemDto.LastSaleDateSpecified)));
            payloadObject[GetSerializedPropertyName(nameof(SyncEntityDto.MutationId))] =
                mutationId;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var stream = new HashingWriteStream(hash))
        {
            JsonSerializer.Serialize(stream, payload, SerializerOptions);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeLegacyRawPayload(SyncEntityDto dto)
    {
        // The original hasher serialized the DTO exactly as received. At that
        // time the receipt key was trimmed separately, so the persisted hash
        // can legitimately include MutationId leading or trailing whitespace.
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            dto,
            dto.GetType(),
            SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static string ComputeWithMutationId(
        SyncEntityDto dto,
        string mutationId,
        bool canonicalizeSemanticPayload)
    {
        var canonicalPayload = JsonSerializer.SerializeToNode(
            dto,
            dto.GetType(),
            SerializerOptions);
        if (canonicalPayload is JsonObject payloadObject)
        {
            if (canonicalizeSemanticPayload)
                CanonicalizeSemanticPayload(dto, payloadObject);

            var mutationIdPropertyName = GetSerializedPropertyName(
                nameof(SyncEntityDto.MutationId));
            payloadObject[mutationIdPropertyName] = mutationId;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var stream = new HashingWriteStream(hash))
        {
            JsonSerializer.Serialize(stream, canonicalPayload, SerializerOptions);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void CanonicalizeSemanticPayload(
        SyncEntityDto dto,
        JsonObject payloadObject)
    {
        if (dto is InvoiceDto)
        {
            // CustomerName은 CustomerId 조회로 보강하는 파생 표시값이고, Payments는
            // request.Payments에서 별도 mutation으로 처리된다.
            payloadObject[GetSerializedPropertyName(nameof(InvoiceDto.CustomerName))] =
                string.Empty;
            payloadObject[GetSerializedPropertyName(nameof(InvoiceDto.Payments))] =
                new JsonArray();
            SortArray(
                payloadObject,
                GetSerializedPropertyName(nameof(InvoiceDto.Lines)),
                node => (
                    ReadJsonInt32(node, nameof(InvoiceLineDto.OrderIndex)),
                    ReadJsonString(node, nameof(InvoiceLineDto.Id))));
        }
        else if (dto is InventoryTransferDto)
        {
            // 재고이동 line의 컬렉션 순서는 의미가 없으므로 ID 기준으로 고정한다.
            SortArray(
                payloadObject,
                GetSerializedPropertyName(nameof(InventoryTransferDto.Lines)),
                node => (
                    0,
                    ReadJsonString(node, nameof(InventoryTransferLineDto.Id))));
        }
    }

    private static void SortArray(
        JsonObject payloadObject,
        string propertyName,
        Func<JsonNode?, (int Order, string Id)> keySelector)
    {
        if (payloadObject[propertyName] is not JsonArray array)
            return;

        var sorted = array
            .Select(node => node?.DeepClone())
            .OrderBy(node => keySelector(node).Order)
            .ThenBy(node => keySelector(node).Id, StringComparer.Ordinal)
            .ToArray();
        payloadObject[propertyName] = new JsonArray(sorted);
    }

    private static int ReadJsonInt32(JsonNode? node, string propertyName)
    {
        if (node is not JsonObject value ||
            value[GetSerializedPropertyName(propertyName)] is not JsonValue propertyValue ||
            !propertyValue.TryGetValue<int>(out var result))
        {
            return int.MaxValue;
        }

        return result > 0 ? result : int.MaxValue;
    }

    private static string ReadJsonString(JsonNode? node, string propertyName)
    {
        if (node is not JsonObject value ||
            value[GetSerializedPropertyName(propertyName)] is not JsonValue propertyValue)
        {
            return string.Empty;
        }

        return propertyValue.TryGetValue<string>(out var result)
            ? result ?? string.Empty
            : propertyValue.ToJsonString();
    }

    private static string GetSerializedPropertyName(string propertyName)
        => SerializerOptions.PropertyNamingPolicy?.ConvertName(propertyName) ??
           propertyName;

    internal readonly record struct PayloadHashEvaluation(
        string CanonicalPayloadHash,
        bool StoredPayloadMatches);

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
