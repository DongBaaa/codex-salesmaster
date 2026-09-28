using System.Reflection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SalesHiddenCatalogPriceTests
{
    private static SalesViewModel Editor(VoucherType type = VoucherType.Sales, bool sales = true, bool purchase = true)
    {
        var permissions = new List<string> { AppPermissionNames.InvoiceEdit };
        if (sales) permissions.Add(AppPermissionNames.AmountViewSales);
        if (purchase) permissions.Add(AppPermissionNames.AmountViewPurchase);
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(), Username = "hidden-catalog", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly, Permissions = permissions
        });
        return new SalesViewModel(null!, null!, null!, session, type);
    }

    private static LocalItem Item(bool salesHidden = false, bool purchaseHidden = false) => new()
    {
        Id = Guid.NewGuid(), NameOriginal = "시험 품목", Unit = "EA", TrackingType = ItemTrackingTypes.NonStock,
        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
        SalePrice = 1100, RetailPrice = 1500, PurchasePrice = 700, PriceGradeA = 900,
        SalesAmountsHidden = salesHidden, PurchaseAmountsHidden = purchaseHidden
    };

    private static void Invoke(SalesViewModel vm, string name, params object[] args)
        => typeof(SalesViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args);

    private static void Cache(SalesViewModel vm, params LocalItemPriceGrade[] rows)
        => Invoke(vm, "ApplyItemPriceGradeCache", (object)rows);

    private static void AssertHiddenLine(SalesViewModel vm, LocalItem item, decimal quantity, string remark)
    {
        var row = Assert.Single(vm.Lines);
        Assert.Equal(item.Id, row.ItemId);
        Assert.Equal(quantity, row.Quantity);
        Assert.Equal(remark, row.Remark);
        Assert.Equal("비공개", row.UnitPriceDisplay);
        var dto = LocalMappings.ToDto(row.ToLocal(Guid.NewGuid()));
        Assert.Null(dto.UnitPrice);
        Assert.Null(dto.LineAmount);
        Assert.Equal("비공개", vm.TotalAmountDisplay);
    }

    [Theory]
    [InlineData(VoucherType.Sales)]
    [InlineData(VoucherType.Purchase)]
    [InlineData(VoucherType.Procurement)]
    public void HiddenCatalogDoesNotBecomeZeroOrFallbackWhenQuantityAndMemoAreAdded(VoucherType type)
    {
        using var vm = Editor(type);
        var item = Item(type == VoucherType.Sales, type != VoucherType.Sales);
        vm.ApplyInputItem(item);
        Assert.Null(vm.EditableInputUnitPrice);
        vm.EditableInputUnitPrice = 99000;
        vm.InputQty = 3;
        vm.InputRemark = "가격 없이 저장";
        Assert.Null(vm.EditableInputLineAmount);
        vm.AddLineCommand.Execute(null);
        AssertHiddenLine(vm, item, 3, "가격 없이 저장");
        Assert.Equal(1100m, item.SalePrice);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 880)]
    [InlineData(false, 0)]
    [InlineData(false, 880)]
    public void CustomGradeUnknownIsDistinctFromKnownZeroAndMissing(bool hidden, int rawPrice)
    {
        using var vm = Editor();
        var item = Item();
        Cache(vm, new LocalItemPriceGrade { ItemId = item.Id, PriceGradeName = "A", UnitPrice = rawPrice, AmountsHidden = hidden, IsActive = true });
        vm.CustomerPriceGrade = "A";
        vm.ApplyInputItem(item);
        Assert.Equal(hidden ? (decimal?)null : rawPrice > 0 ? rawPrice : 900m, vm.EditableInputUnitPrice);
        vm.CustomerPriceGrade = "없는 등급";
        Assert.Equal(1100m, vm.EditableInputUnitPrice);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PurchaseSalesFallbackRequiresDisclosedSalesPriceAndSalesPermission(bool salesPermission, bool salesHidden)
    {
        using var vm = Editor(VoucherType.Purchase, sales: salesPermission);
        var item = Item(salesHidden);
        item.PurchasePrice = 0;
        vm.ApplyInputItem(item);
        Assert.Equal(salesPermission && !salesHidden ? 1100m : (decimal?)null, vm.EditableInputUnitPrice);
        item.PurchasePrice = 700;
        vm.ApplyInputItem(item);
        Assert.Equal(700m, vm.EditableInputUnitPrice);
    }

    [Fact]
    public void KnownZeroAndManualItemRemainEditableAfterHiddenItemIsCleared()
    {
        using var vm = Editor();
        vm.ApplyInputItem(Item(salesHidden: true));
        Invoke(vm, "ClearLineInput");
        vm.InputItemName = "수기 품목";
        vm.EditableInputUnitPrice = 3300;
        Assert.Equal(3300m, vm.EditableInputUnitPrice);
        var zero = Item(); zero.SalePrice = zero.RetailPrice = 0;
        vm.ApplyInputItem(zero);
        Assert.Equal(0m, vm.EditableInputUnitPrice);
        Assert.True(vm.CanEditInputAmounts);
    }

    [Fact]
    public void ExistingKnownInvoiceLineKeepsItsPriceWhenCatalogIsHidden()
    {
        using var vm = Editor();
        var item = Item(salesHidden: true);
        typeof(SalesViewModel).GetField("_allItems", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, new List<LocalItem> { item });
        vm.ApplyInputItem(item);
        var row = new InvoiceLineEditModel { ItemId = item.Id, ItemName = item.NameOriginal, Unit = "EA", Quantity = 2, UnitPrice = 1250 };
        vm.Lines.Add(row);
        vm.SelectedLine = row;
        Assert.Equal(1250m, vm.EditableInputUnitPrice);
        vm.CustomerPriceGrade = "A";
        Assert.Equal(1250m, vm.EditableInputUnitPrice);
        vm.InputQty = 4;
        vm.InputRemark = "기존 전표 단가 유지";
        vm.UpdateLineCommand.Execute(null);
        Assert.Equal(5000m, LocalMappings.ToDto(row.ToLocal(Guid.NewGuid())).LineAmount);
        Assert.Equal("기존 전표 단가 유지", row.Remark);
    }

    [Fact]
    public void ReplacingKnownInvoiceItemWithHiddenItemDoesNotReuseOldMoney()
    {
        using var vm = Editor();
        var row = new InvoiceLineEditModel { ItemName = "수기 품목", Quantity = 2, UnitPrice = 1250 };
        vm.Lines.Add(row); vm.SelectedLine = row;
        var item = Item(salesHidden: true);
        vm.ApplyInputItem(item);
        vm.InputQty = 4; vm.InputRemark = "품목 변경";
        vm.UpdateLineCommand.Execute(null);
        AssertHiddenLine(vm, item, 4, "품목 변경");
    }

    [Fact]
    public void ImplicitItemLinkCannotSubmitPlaceholderPrice()
    {
        using var vm = Editor();
        var item = Item(salesHidden: true);
        typeof(SalesViewModel).GetField("_allItems", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, new List<LocalItem> { item });
        vm.InputItemName = item.NameOriginal; vm.InputUnit = item.Unit;
        vm.InputQty = 2; vm.InputRemark = "직접 검색 입력";
        vm.AddLineCommand.Execute(null);
        AssertHiddenLine(vm, item, 2, "직접 검색 입력");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CatalogRefreshPreservesDraftAndKeepsExistingInvoicePriceAuthoritative(bool existingLine, bool missingItem)
    {
        using var vm = Editor();
        var item = Item();
        typeof(SalesViewModel).GetField("_allItems", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, new List<LocalItem> { item });
        vm.ApplyInputItem(item);
        if (existingLine)
        {
            var row = new InvoiceLineEditModel { ItemId = item.Id, ItemName = item.NameOriginal, Unit = item.Unit, Quantity = 2, UnitPrice = 1250 };
            vm.Lines.Add(row); vm.SelectedLine = row;
        }
        vm.InputQty = 4; vm.InputRemark = "아직 저장하지 않은 비고";
        var originalPrice = vm.InputUnitPrice;
        vm.MarkCurrentStateAsPristine();
        var hidden = Item(salesHidden: true); hidden.Id = item.Id;
        Invoke(vm, "ApplyRefreshedItems", missingItem ? new List<LocalItem>() : new List<LocalItem> { hidden }, new List<LocalItemPriceGrade>(), new List<LocalItemWarehouseStock>(), 0);
        Assert.Equal(4m, vm.InputQty);
        Assert.Equal("아직 저장하지 않은 비고", vm.InputRemark);
        Assert.Equal(originalPrice, vm.InputUnitPrice);
        Assert.False(vm.HasPendingChanges);
        Assert.Equal(existingLine ? originalPrice : (decimal?)null, vm.EditableInputUnitPrice);
        if (!existingLine)
        {
            // Receiving another disclosed catalog snapshot does not silently
            // disclose an old draft; an explicit item choice is required.
            Invoke(vm, "ApplyRefreshedItems", new List<LocalItem> { item }, new List<LocalItemPriceGrade>(), new List<LocalItemWarehouseStock>(), 0);
            Assert.Null(vm.EditableInputUnitPrice);
            vm.ApplyInputItem(item);
            Assert.Equal(1100m, vm.EditableInputUnitPrice);
        }
    }

    [Fact]
    public void SalesPermissionRevocationClosesPurchaseFallbackWithoutChangingDraft()
    {
        var session = new SessionState();
        var user = new UserSessionDto
        {
            UserId = Guid.NewGuid(), Username = "fallback", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            Permissions = [AppPermissionNames.AmountViewPurchase, AppPermissionNames.AmountViewSales]
        };
        session.SetSession("fixture", user);
        using var vm = new SalesViewModel(null!, null!, null!, session, VoucherType.Purchase);
        var item = Item(); item.PurchasePrice = 0;
        vm.ApplyInputItem(item); vm.InputRemark = "비고";
        vm.MarkCurrentStateAsPristine();
        user.Permissions = [AppPermissionNames.AmountViewPurchase];
        session.RefreshSession("revoked", user);
        Assert.Null(vm.EditableInputUnitPrice);
        Assert.Equal(1100m, vm.InputUnitPrice);
        Assert.Equal("비고", vm.InputRemark);
        Assert.False(vm.HasPendingChanges);
    }
}
