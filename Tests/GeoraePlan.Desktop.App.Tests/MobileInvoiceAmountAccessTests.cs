using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobileInvoiceAmountAccessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReeditingSameItem_RetainsInvoiceSnapshotAndHiddenMoney(bool registered, bool hidden)
    {
        Guid? itemId = registered ? Guid.NewGuid() : null;
        var original = new InvoiceLineDto
        {
            Id = Guid.NewGuid(), ItemId = itemId, ItemNameOriginal = "계약 당시 품목", SpecificationOriginal = "당시 규격",
            Unit = "BOX", MaterialNumber = "원자재번호", SerialNumber = "원일련번호", InstallLocation = "기존 설치장소",
            RentalStartDate = new DateOnly(2025, 1, 2), RentalEndDate = new DateOnly(2027, 3, 4), OrderIndex = 7,
            Quantity = 2, UnitPrice = 125, LineAmount = 250
        };
        var existing = InvoiceLineDraftItem.FromDto(original, forceHideAmounts: hidden);
        existing.CategoryName = "기존 분류";
        var refreshedMaster = new ItemDto { Id = itemId ?? Guid.Empty, NameOriginal = "현재 품목명", Unit = "EA", SalePrice = 9900 };
        var edited = InvoiceLineDraftItem.FromEditor(refreshedMaster, existing, 5, 130, " 수정 비고 ", amountsHidden: false);
        var outgoing = JsonSerializer.Deserialize<InvoiceLineDto>(JsonSerializer.Serialize(edited.ToDto(Guid.NewGuid())))!;
        Assert.Equal(original.Id, outgoing.Id); Assert.Equal(itemId, outgoing.ItemId);
        Assert.Equal(original.ItemNameOriginal, outgoing.ItemNameOriginal);
        Assert.Equal(original.SpecificationOriginal, outgoing.SpecificationOriginal); Assert.Equal(original.Unit, outgoing.Unit);
        Assert.Equal(original.MaterialNumber, outgoing.MaterialNumber); Assert.Equal(original.SerialNumber, outgoing.SerialNumber);
        Assert.Equal(original.InstallLocation, outgoing.InstallLocation);
        Assert.Equal(original.RentalStartDate, outgoing.RentalStartDate); Assert.Equal(original.RentalEndDate, outgoing.RentalEndDate);
        Assert.Equal(7, outgoing.OrderIndex); Assert.Equal("기존 분류", edited.CategoryName);
        Assert.Equal(5, outgoing.Quantity); Assert.Equal("수정 비고", outgoing.Remark);
        Assert.Equal(hidden ? (decimal?)null : 130m, outgoing.UnitPrice);
        Assert.Equal(hidden ? (decimal?)null : 650m, outgoing.LineAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacingItem_UsesNewItemSnapshotButKeepsRowIdentity(bool hidden)
    {
        var existing = InvoiceLineDraftItem.FromDto(new InvoiceLineDto { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(),
            ItemNameOriginal = "이전 품목", Quantity = 1, UnitPrice = 100, LineAmount = 100, OrderIndex = 3,
            InstallLocation = "이전 장소", RentalStartDate = new DateOnly(2025, 1, 1) });
        var replacement = new ItemDto { Id = Guid.NewGuid(), NameOriginal = "교체 품목", Unit = "SET", SalePrice = 900 };
        var edited = InvoiceLineDraftItem.FromEditor(replacement, existing, 2, 900, "교체", hidden);
        Assert.Equal(existing.Id, edited.Id); Assert.Equal(3, edited.OrderIndex);
        Assert.Equal(replacement.Id, edited.ItemId); Assert.Equal("교체 품목", edited.ItemNameOriginal);
        Assert.Equal("SET", edited.Unit); Assert.Equal(replacement.InstallLocation, edited.InstallLocation);
        Assert.Null(edited.RentalStartDate);
        Assert.Equal(hidden ? (decimal?)null : 900m, edited.ToDto(Guid.NewGuid()).UnitPrice);
    }

    [Fact]
    public async Task OfflineCoordinator_PersistsHiddenInvoiceWithoutInventingZeroAmounts()
    {
        var session = new SessionStore();
        var snapshot = session.GetSnapshot();
        using var store = new JsonSyncStateStore(session, new MobileSyncState
        {
            OwnerUsername = snapshot.Username, OwnerTenantCode = snapshot.TenantCode,
            OwnerOfficeCode = snapshot.OfficeCode, DeviceId = "hidden-invoice-test"
        });
        var cacheRoot = Path.Combine(Path.GetTempPath(), "georaeplan-mobile-invoice-privacy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var api = new GeoraePlanApiClient { BeforeInvoiceReturnAsync = () => throw new HttpRequestException("offline") };
            var coordinator = new SyncCoordinator(store, api, new PaymentAttachmentDraftStore(),
                new CustomerContractCacheStore(session, cacheRoot, beforeAtomicPublishAsync: null), session);
            var invoice = new InvoiceDto { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(),
                TotalAmount = null, SupplyAmount = null, VatAmount = null, MutationId = "hidden-offline-command" };
            invoice.Lines = [InvoiceLineDraftItem.FromItem(new ItemDto { Id = Guid.NewGuid(), SalePrice = 9876 },
                4, amountsHidden: true).ToDto(invoice.Id)];
            invoice.Lines[0].Remark = "현장 입력";
            var result = await coordinator.SaveInvoiceImmediatelyAsync(invoice, session.CaptureOwner());
            Assert.Equal(1, result.PendingInvoiceCount);
            var reloaded = await store.LoadAsync(session.CaptureOwner(), CancellationToken.None);
            var pending = Assert.Single(reloaded.PendingPush.Invoices);
            Assert.True(pending.AmountsHidden); Assert.Null(pending.TotalAmount);
            Assert.Null(pending.Lines[0].UnitPrice); Assert.Null(pending.Lines[0].LineAmount);
            Assert.Equal(4, pending.Lines[0].Quantity); Assert.Equal("현장 입력", pending.Lines[0].Remark);
            Assert.Equal("hidden-offline-command", pending.MutationId);
        }
        finally { Directory.Delete(cacheRoot, recursive: true); }
    }

    public static IEnumerable<object[]> Permissions()
    {
        foreach (var type in Enum.GetValues<VoucherType>())
        foreach (var sales in new[] { false, true })
        foreach (var purchase in new[] { false, true })
            yield return [type, sales, purchase];
    }

    [Theory]
    [MemberData(nameof(Permissions))]
    public void InvoiceKind_UsesItsOwnAmountPermission(VoucherType type, bool sales, bool purchase)
    {
        var grants = new[] { sales ? "Amount.ViewSales" : "", purchase ? "Amount.ViewPurchase" : "" };
        var expected = type is VoucherType.Sales or VoucherType.Collection ? sales : purchase;
        Assert.Equal(expected, MobileInvoiceAmountAccess.CanView(type, true, "User", grants));
        Assert.False(MobileInvoiceAmountAccess.CanView(type, false, "Admin", grants));
    }

    [Fact]
    public void UnknownVoucher_FailsClosedEvenForAdmin()
        => Assert.False(MobileInvoiceAmountAccess.CanView((VoucherType)999, true, "Admin", []));

    [Fact]
    public void RestrictedNewLine_DoesNotImportCachedPrices_AndSurvivesOfflineRoundTrip()
    {
        var line = InvoiceLineDraftItem.FromItem(new ItemDto
        { Id = Guid.NewGuid(), SalePrice = 9999, PurchasePrice = 8888, NameOriginal = "테스트품목" },
            3, amountsHidden: true);
        line.Remark = "금액 없이 저장";
        Assert.True(line.AmountsHidden); Assert.Equal(0, line.UnitPrice);
        var outgoing = JsonSerializer.Deserialize<InvoiceLineDto>(JsonSerializer.Serialize(line.ToDto(Guid.NewGuid())))!;
        Assert.Null(outgoing.UnitPrice); Assert.Null(outgoing.LineAmount);
        Assert.Equal(3, outgoing.Quantity); Assert.Equal("금액 없이 저장", outgoing.Remark);
        Assert.DoesNotContain("9999", line.PriceSummary);
        Assert.Contains("비공개", line.AmountSummary);
    }

    [Fact]
    public void Revocation_NotifiesVisibleLabels_AndPreservesRawSnapshotForSafeHandling()
    {
        var line = InvoiceLineDraftItem.FromItem(new ItemDto { SalePrice = 1234 }, 2);
        var changed = new List<string?>(); line.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        line.AmountsHidden = true;
        Assert.Contains(nameof(line.PriceSummary), changed); Assert.Contains(nameof(line.AmountSummary), changed);
        Assert.Equal(1234, line.UnitPrice);
        Assert.Contains("비공개", line.PriceSummary); Assert.Contains("비공개", line.AmountSummary);
        Assert.Null(line.ToDto(Guid.NewGuid()).UnitPrice);
    }

    [Fact]
    public void HiddenExistingLine_QuantityAndRemarkEdit_StayNullOnWire()
    {
        var fromServer = new InvoiceLineDto { Id = Guid.NewGuid(), Quantity = 2, UnitPrice = null, LineAmount = null };
        var line = InvoiceLineDraftItem.FromDto(fromServer); line.Quantity = 5; line.Remark = "재편집";
        var dto = line.ToDto(Guid.NewGuid());
        Assert.Equal(fromServer.Id, dto.Id); Assert.Equal(5, dto.Quantity);
        Assert.Equal("재편집", dto.Remark); Assert.Null(dto.UnitPrice); Assert.Null(dto.LineAmount);
    }

    [Fact]
    public void SaveAndExportBoundary_ForceHideWithoutWaitingForUiCallback()
    {
        var line = InvoiceLineDraftItem.FromDto(new InvoiceLineDto { Quantity = 3, UnitPrice = 25, LineAmount = 75 });
        Assert.False(line.AmountsHidden);
        var hidden = line.ToDto(Guid.NewGuid(), forceHideAmounts: true);
        Assert.Null(hidden.UnitPrice); Assert.Null(hidden.LineAmount);
        Assert.Equal(25, line.UnitPrice);
        var reopened = InvoiceLineDraftItem.FromDto(hidden);
        Assert.True(reopened.AmountsHidden);
    }

    [Fact]
    public void VisibleZero_RemainsZeroAndHiddenParentCannotMakeZeroAppearKnown()
    {
        var dto = new InvoiceLineDto { Quantity = 1, UnitPrice = 0, LineAmount = 0 };
        var known = InvoiceLineDraftItem.FromDto(dto);
        Assert.DoesNotContain("비공개", known.PriceSummary); Assert.Equal(0, known.ToDto(Guid.NewGuid()).UnitPrice);
        var unknown = InvoiceLineDraftItem.FromDto(dto, forceHideAmounts: true);
        Assert.True(unknown.AmountsHidden); Assert.Null(unknown.ToDto(Guid.NewGuid()).UnitPrice);
    }
}
