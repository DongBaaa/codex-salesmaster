using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class SyncMutationPayloadHasherSemanticTests
{
    [Fact]
    public void Compute_Invoice_IgnoresDerivedCustomerNameAndEmbeddedPayments()
    {
        var baseline = CreateInvoice();
        baseline.CustomerName = "서버 조회로 보강된 거래처명";
        baseline.Payments =
        [
            new PaymentDto
            {
                Id = Guid.Parse("a1000000-0000-0000-0000-000000000001"),
                InvoiceId = baseline.Id,
                PaymentDate = new DateOnly(2026, 7, 20),
                Amount = 15_000m,
                Note = "첫 수금",
                MutationId = "device-a:payment:first"
            }
        ];

        var variant = CreateInvoice();
        variant.CustomerName = "다른 캐시에서 보강된 표시명";
        variant.Payments =
        [
            new PaymentDto
            {
                Id = Guid.Parse("a2000000-0000-0000-0000-000000000001"),
                InvoiceId = variant.Id,
                PaymentDate = new DateOnly(2026, 7, 21),
                Amount = 999_999m,
                Note = "별도 mutation으로 전송되는 다른 수금",
                MutationId = "device-b:payment:different"
            },
            new PaymentDto
            {
                Id = Guid.Parse("a2000000-0000-0000-0000-000000000002"),
                InvoiceId = variant.Id,
                PaymentDate = new DateOnly(2026, 7, 22),
                Amount = 1m,
                Note = "두 번째 수금",
                MutationId = "device-b:payment:second"
            }
        ];

        Assert.Equal(
            SyncMutationPayloadHasher.Compute(baseline),
            SyncMutationPayloadHasher.Compute(variant));
    }

    [Fact]
    public void Compute_Invoice_ChangesWhenMeaningfulHeaderOrLinePayloadChanges()
    {
        var baselineHash = SyncMutationPayloadHasher.Compute(CreateInvoice());

        var changedMemo = CreateInvoice();
        changedMemo.Memo = "사용자가 변경한 의미 있는 전표 메모";

        var changedLine = CreateInvoice();
        changedLine.Lines[0].Quantity += 1m;
        changedLine.Lines[0].LineAmount =
            changedLine.Lines[0].Quantity * changedLine.Lines[0].UnitPrice;

        Assert.NotEqual(
            baselineHash,
            SyncMutationPayloadHasher.Compute(changedMemo));
        Assert.NotEqual(
            baselineHash,
            SyncMutationPayloadHasher.Compute(changedLine));
    }

    [Fact]
    public void Compute_InventoryTransfer_IsIndependentOfLineOrder()
    {
        var baseline = CreateInventoryTransfer();
        var reversed = CreateInventoryTransfer();
        reversed.Lines.Reverse();

        Assert.Equal(
            SyncMutationPayloadHasher.Compute(baseline),
            SyncMutationPayloadHasher.Compute(reversed));
    }

    [Fact]
    public void EvaluateForReceiptReplay_ExactReplaySerializesDtoExactlyOnce()
    {
        var dto = new CountingSyncEntityDto
        {
            Id = Guid.Parse("81000000-0000-0000-0000-000000000001"),
            MutationId = "device-a:counting:exact-replay"
        };
        var storedPayloadHash = SyncMutationPayloadHasher.Compute(dto);
        dto.ResetSerializationProbeReadCount();

        var evaluation =
            SyncMutationPayloadHasher.EvaluateForReceiptReplay(
                dto,
                storedPayloadHash,
                dto.MutationId);

        Assert.True(evaluation.StoredPayloadMatches);
        Assert.Equal(storedPayloadHash, evaluation.CanonicalPayloadHash);
        Assert.Equal(1, dto.SerializationProbeReadCount);
    }

    [Fact]
    public void EvaluateForReceiptReplay_DoesNotAcceptPayloadChangedAfterPriorHash()
    {
        var dto = CreateItem();
        var storedPayloadHash = SyncMutationPayloadHasher.Compute(dto);
        dto.Unit = "EA";

        var evaluation =
            SyncMutationPayloadHasher.EvaluateForReceiptReplay(
                dto,
                storedPayloadHash,
                dto.MutationId);

        Assert.False(evaluation.StoredPayloadMatches);
        Assert.NotEqual(
            storedPayloadHash,
            evaluation.CanonicalPayloadHash);
    }

    [Fact]
    public void ItemOptionalCatalogFields_NullPayloadMatchesPreUpgradeRawHash_AndValuesChangePayload()
    {
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacyCompatible = CreateItem();
        var preUpgradeRawBytes = JsonSerializer.SerializeToUtf8Bytes(
                legacyCompatible,
                legacyCompatible.GetType(),
                serializerOptions);
        var preUpgradeRawHash = Convert.ToHexString(
                SHA256.HashData(preUpgradeRawBytes))
            .ToLowerInvariant();

        using var nullPayloadJson = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(
                legacyCompatible,
                legacyCompatible.GetType(),
                serializerOptions));
        Assert.False(nullPayloadJson.RootElement.TryGetProperty("boxQuantity", out _));
        Assert.False(nullPayloadJson.RootElement.TryGetProperty("storageLocation", out _));
        Assert.False(nullPayloadJson.RootElement.TryGetProperty("lastPurchaseDate", out _));
        Assert.False(nullPayloadJson.RootElement.TryGetProperty("lastPurchaseDateSpecified", out _));
        Assert.False(nullPayloadJson.RootElement.TryGetProperty("lastSaleDate", out _));
        Assert.False(nullPayloadJson.RootElement.TryGetProperty("lastSaleDateSpecified", out _));
        Assert.True(SyncMutationPayloadHasher.Matches(
            legacyCompatible,
            preUpgradeRawHash,
            legacyCompatible.MutationId));

        var populated = CreateItem();
        populated.BoxQuantity = 20m;
        populated.StorageLocation = "A-01-02";
        populated.LastPurchaseDate = new DateOnly(2026, 7, 20);
        populated.LastPurchaseDateSpecified = true;
        populated.LastSaleDate = new DateOnly(2026, 7, 24);
        populated.LastSaleDateSpecified = true;
        using var populatedJson = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(
                populated,
                populated.GetType(),
                serializerOptions));
        Assert.Equal(20m, populatedJson.RootElement.GetProperty("boxQuantity").GetDecimal());
        Assert.Equal("A-01-02", populatedJson.RootElement.GetProperty("storageLocation").GetString());
        Assert.Equal("2026-07-20", populatedJson.RootElement.GetProperty("lastPurchaseDate").GetString());
        Assert.True(populatedJson.RootElement.GetProperty("lastPurchaseDateSpecified").GetBoolean());
        Assert.Equal("2026-07-24", populatedJson.RootElement.GetProperty("lastSaleDate").GetString());
        Assert.True(populatedJson.RootElement.GetProperty("lastSaleDateSpecified").GetBoolean());
        Assert.NotEqual(
            preUpgradeRawHash,
            SyncMutationPayloadHasher.Compute(populated));
        Assert.True(SyncMutationPayloadHasher.Matches(
            populated,
            preUpgradeRawHash,
            populated.MutationId));

        var changedLegacyField = CreateItem();
        changedLegacyField.BoxQuantity = populated.BoxQuantity;
        changedLegacyField.StorageLocation = populated.StorageLocation;
        changedLegacyField.LastPurchaseDate = populated.LastPurchaseDate;
        changedLegacyField.LastPurchaseDateSpecified = true;
        changedLegacyField.LastSaleDate = populated.LastSaleDate;
        changedLegacyField.LastSaleDateSpecified = true;
        changedLegacyField.Unit = "EA";
        Assert.False(SyncMutationPayloadHasher.Matches(
            changedLegacyField,
            preUpgradeRawHash,
            changedLegacyField.MutationId));
    }

    private static ItemDto CreateItem()
        => new()
        {
            Id = Guid.Parse("90000000-0000-0000-0000-000000000001"),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "해시 호환 품목",
            NameMatchKey = "해시 호환 품목",
            SpecificationOriginal = "BOX-20",
            SpecificationMatchKey = "BOX-20",
            CategoryName = "소모품",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "BOX",
            CurrentStock = 10m,
            SafetyStock = 2m,
            PurchasePrice = 1_000m,
            SalePrice = 1_500m,
            RetailPrice = 2_000m,
            CreatedAtUtc = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 7, 24, 2, 0, 0, DateTimeKind.Utc),
            Revision = 7,
            ExpectedRevision = 7,
            MutationId = "device-a:item:optional-catalog-fields",
            MutationCreatedAtUtc = new DateTime(2026, 7, 24, 2, 0, 0, DateTimeKind.Utc)
        };

    private static InvoiceDto CreateInvoice()
    {
        var invoiceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        return new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
            CustomerName = "기본 거래처 표시명",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "S-202607-0001",
            LocalTempNumber = "LOCAL-202607-0001",
            TaxInvoiceNumber = "TAX-202607-0001",
            VersionGroupId = Guid.Parse("30000000-0000-0000-0000-000000000001"),
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 7, 24),
            TotalAmount = 110_000m,
            SupplyAmount = 100_000m,
            VatAmount = 10_000m,
            VatMode = InvoiceVatModes.Included,
            Memo = "원본 전표 메모",
            CreatedAtUtc = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 7, 24, 2, 0, 0, DateTimeKind.Utc),
            Revision = 7,
            ExpectedRevision = 7,
            MutationId = "device-a:invoice:semantic-payload",
            MutationCreatedAtUtc = new DateTime(2026, 7, 24, 2, 0, 0, DateTimeKind.Utc),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = Guid.Parse("40000000-0000-0000-0000-000000000001"),
                    InvoiceId = invoiceId,
                    ItemId = Guid.Parse("50000000-0000-0000-0000-000000000001"),
                    ItemNameOriginal = "복합기 임대료",
                    SpecificationOriginal = "A3 COLOR",
                    Unit = "대",
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    LineAmount = 100_000m,
                    Remark = "월 임대",
                    OrderIndex = 1,
                    ItemTrackingType = ItemTrackingTypes.Asset
                }
            ]
        };
    }

    private static InventoryTransferDto CreateInventoryTransfer()
    {
        var transferId = Guid.Parse("60000000-0000-0000-0000-000000000001");
        return new InventoryTransferDto
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            TransferNumber = "MOVE-20260724-0001",
            TransferDate = new DateOnly(2026, 7, 24),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            Memo = "지점 재고 이동",
            CreatedByUsername = "admin",
            LastSavedByUsername = "admin",
            LastSavedAtUtc = new DateTime(2026, 7, 24, 3, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 7, 24, 2, 30, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 7, 24, 3, 0, 0, DateTimeKind.Utc),
            Revision = 11,
            ExpectedRevision = 11,
            MutationId = "device-a:inventory-transfer:semantic-payload",
            MutationCreatedAtUtc = new DateTime(2026, 7, 24, 3, 0, 0, DateTimeKind.Utc),
            Lines =
            [
                new InventoryTransferLineDto
                {
                    Id = Guid.Parse("70000000-0000-0000-0000-000000000002"),
                    TransferId = transferId,
                    ItemId = Guid.Parse("80000000-0000-0000-0000-000000000002"),
                    ItemNameOriginal = "토너 B",
                    SpecificationOriginal = "BLACK",
                    Unit = "EA",
                    Quantity = 2m,
                    Remark = "두 번째 품목"
                },
                new InventoryTransferLineDto
                {
                    Id = Guid.Parse("70000000-0000-0000-0000-000000000001"),
                    TransferId = transferId,
                    ItemId = Guid.Parse("80000000-0000-0000-0000-000000000001"),
                    ItemNameOriginal = "토너 A",
                    SpecificationOriginal = "CYAN",
                    Unit = "EA",
                    Quantity = 1m,
                    Remark = "첫 번째 품목"
                }
            ]
        };
    }

    public sealed class CountingSyncEntityDto : SyncEntityDto
    {
        private int _serializationProbeReadCount;

        [JsonIgnore]
        public int SerializationProbeReadCount => _serializationProbeReadCount;

        public void ResetSerializationProbeReadCount()
            => _serializationProbeReadCount = 0;

        public string SerializationProbe
        {
            get
            {
                _serializationProbeReadCount++;
                return "probe";
            }
            set
            {
            }
        }
    }
}
