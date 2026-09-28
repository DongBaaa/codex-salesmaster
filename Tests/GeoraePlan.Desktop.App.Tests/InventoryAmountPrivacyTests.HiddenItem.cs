using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Converters;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class InventoryAmountPrivacyTests
{
    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task HiddenItemPrices_RemainUnknownForGrantedEditorAndDoNotBecomePendingWrites(bool purchaseHidden, bool salesHidden)
    {
        await using var f=await Fixture.CreateAsync(true,true);
        HidePrices(f.Item,purchaseHidden,salesHidden);
        using var vm=f.Editor();
        Assert.Equal(purchaseHidden ? null : (decimal?)110m,vm.EditablePurchasePrice);
        Assert.Equal(salesHidden ? null : (decimal?)220m,vm.EditableSalePrice);
        Assert.Equal(salesHidden ? null : (decimal?)330m,vm.EditableRetailPrice);
        Assert.Equal(purchaseHidden ? null : (decimal?)330m,vm.AssetValue);
        Assert.Equal(purchaseHidden,vm.IsPurchasePriceReadOnly);
        Assert.Equal(salesHidden,vm.IsSalesPriceReadOnly);
        Assert.Equal(salesHidden ? null : (decimal?)777m,vm.PriceGradeRows[0].EditableUnitPrice);
        Assert.True(vm.CanSaveItems); Assert.False(vm.HasPendingChanges);
        if(purchaseHidden) vm.EditablePurchasePrice=999m;
        if(salesHidden) {vm.EditableSalePrice=999m;vm.EditableRetailPrice=999m;vm.PriceGradeRows[0].EditableUnitPrice=999m;}
        Assert.False(vm.HasPendingChanges);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HiddenGrade_DoesNotOverwriteKnownLegacyPriceWhenMemoIsSaved(bool autoSave)
    {
        await using var f=await Fixture.CreateAsync(true,true);
        f.Grade.UnitPrice=0; f.Grade.AmountsHidden=true; await f.Db.SaveChangesAsync();
        using var vm=f.Editor();
        Assert.Equal(220m,vm.EditableSalePrice);
        Assert.Null(vm.PriceGradeRows[0].EditableUnitPrice);
        Assert.True(vm.PriceGradeRows[0].IsPriceReadOnly);
        vm.PriceGradeRows[0].EditableUnitPrice=999m;
        Assert.Equal(0m,vm.PriceGradeRows[0].UnitPrice);
        vm.EditSimpleMemo="비금액 수정";
        if(autoSave) Assert.True(await vm.TryAutoSaveOnCloseAsync(),vm.StatusMessage);
        else await SaveSnapshot(vm);
        var item=await f.Db.Items.AsNoTracking().SingleAsync();
        var grade=await f.Db.ItemPriceGrades.AsNoTracking().SingleAsync();
        Assert.Equal("비금액 수정",item.SimpleMemo); Assert.Equal(440m,item.PriceGradeA);
        Assert.True(grade.AmountsHidden); Assert.Equal(0m,grade.UnitPrice);
        Assert.True(item.IsDirty); Assert.Equal(3m,item.CurrentStock);
    }

    [Theory]
    [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task HiddenSnapshot_RestoreAndRepeatedNewItemKeepUnknownPrices(bool purchaseHidden,bool salesHidden)
    {
        await using var f=await Fixture.CreateAsync(true,true);
        HidePrices(f.Item,purchaseHidden,salesHidden);
        f.Grade.AmountsHidden=true;f.Grade.UnitPrice=0;await f.Db.SaveChangesAsync();
        using var vm=f.Editor();
        var snapshot=Invoke(vm,"CaptureEditSnapshot");
        vm.PrepareNewItemRegistration("new known zero");
        Assert.Equal(0m,vm.EditablePurchasePrice);Assert.Equal(0m,vm.EditableSalePrice);
        Invoke(vm,"ApplySnapshot",snapshot,false);
        Assert.Equal(purchaseHidden ? null : (decimal?)110m,vm.EditablePurchasePrice);
        Assert.Equal(salesHidden ? null : (decimal?)220m,vm.EditableSalePrice);
        Assert.Null(vm.PriceGradeRows[0].EditableUnitPrice);
        Invoke(vm,"PrepareRepeatedNewItemRegistration",snapshot,"repeat");
        Assert.True(vm.IsNew); Assert.NotEqual(f.Item.Id,vm.EditId);
        Assert.Equal(purchaseHidden ? null : (decimal?)110m,vm.EditablePurchasePrice);
        Assert.Equal(salesHidden ? null : (decimal?)220m,vm.EditableSalePrice);
        vm.EditName="새 품목 비금액";await SaveSnapshot(vm);
        var created=await f.Db.Items.AsNoTracking().SingleAsync(i=>i.Id==vm.EditId);
        Assert.Equal(purchaseHidden,created.PurchaseAmountsHidden);Assert.Equal(salesHidden,created.SalesAmountsHidden);
        Assert.Empty(await f.Db.ItemPriceGrades.Where(g=>g.ItemId==created.Id).ToListAsync());
    }

    [Fact]
    public async Task HiddenLegacyFallbackGrade_IsNotShownAsZero()
    {
        await using var f=await Fixture.CreateAsync(true,true);
        HidePrices(f.Item,false,true);
        using var vm=f.Editor();
        var cache=(Dictionary<Guid,List<LocalItemPriceGrade>>)typeof(InventoryViewModel).GetField("_itemPriceGradesByItemId",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(vm)!;
        cache.Clear(); Load(vm,f.Item);
        Assert.Null(vm.PriceGradeRows[0].EditableUnitPrice);
        Assert.False(vm.HasPendingChanges);
    }

    [Fact]
    public void ItemSwitch_UpdatesWpfBindingsBetweenKnownZeroAndHiddenWithoutUserWrites()
    {
        RunSta(()=>
        {
            var f=Fixture.CreateAsync(true,true).GetAwaiter().GetResult();
            try
            {
                using var vm=f.Editor();var box=new TextBox();
                box.SetBinding(TextBox.TextProperty,new Binding(nameof(vm.EditablePurchasePrice)){Source=vm,Mode=BindingMode.TwoWay,Converter=new DecimalToStringConverter(),ConverterParameter="hiddenWhenNull"});
                var readOnly=new TextBox();readOnly.SetBinding(TextBox.IsReadOnlyProperty,new Binding(nameof(vm.IsPurchasePriceReadOnly)){Source=vm});
                Assert.Equal("110",box.Text);Assert.False(readOnly.IsReadOnly);
                HidePrices(f.Item,true,true);Load(vm,f.Item);
                Assert.Equal("비공개",box.Text);Assert.True(readOnly.IsReadOnly);Assert.Null(vm.AssetValue);
                Assert.False(vm.HasPendingChanges);
                f.Item.PurchaseAmountsHidden=false;f.Item.SalesAmountsHidden=false;Load(vm,f.Item);
                Assert.Equal("0",box.Text);Assert.False(readOnly.IsReadOnly);Assert.Equal(0m,vm.AssetValue);
                Assert.False(vm.HasPendingChanges);
            }
            finally{f.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        });
    }

    private static void HidePrices(LocalItem item,bool purchase,bool sales)
    {
        item.PurchaseAmountsHidden=purchase;item.SalesAmountsHidden=sales;
        if(purchase)item.PurchasePrice=0;
        if(sales)item.SalePrice=item.RetailPrice=item.PriceGradeA=item.PriceGradeB=item.PriceGradeC=0;
    }
    private static object Invoke(InventoryViewModel vm,string method,params object[] args)
        =>typeof(InventoryViewModel).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(vm,args)!;
    private static void Load(InventoryViewModel vm,LocalItem item)
        =>Invoke(vm,"LoadFormFromItem",new InventoryItemRow(item,new Dictionary<string,decimal>{{OfficeCodeCatalog.Usenet,3m}},OfficeCodeCatalog.Usenet));
    private static Task SaveSnapshot(InventoryViewModel vm)
        =>(Task)Invoke(vm,"SaveItemSnapshotToLocalAsync",Invoke(vm,"CaptureEditSnapshot"));

    [Theory]
    [InlineData(true,false,false)] [InlineData(false,true,false)] [InlineData(true,true,false)]
    [InlineData(true,false,true)] [InlineData(false,true,true)] [InlineData(true,true,true)]
    public async Task RefreshedHiddenPrices_CloseVisibilityWhileKeepingFilteredPendingMemo(bool purchaseHidden,bool salesHidden,bool filteredOut)
    {
        await using var f=await Fixture.CreateAsync(true,true);
        using var vm=f.Editor();vm.EditSimpleMemo="pending memo";
        var refreshed=LocalMappings.ToLocal(LocalMappings.ToDto(f.Item));HidePrices(refreshed,purchaseHidden,salesHidden);
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        typeof(InventoryViewModel).GetField("_allItems",flags)!.SetValue(vm,new List<LocalItem>{refreshed});
        var cache=(Dictionary<Guid,List<LocalItemPriceGrade>>)typeof(InventoryViewModel).GetField("_itemPriceGradesByItemId",flags)!.GetValue(vm)!;
        cache[f.Item.Id]=[new LocalItemPriceGrade {Id=f.Grade.Id,ItemId=f.Item.Id,PriceGradeOptionId=f.Option.Id,AmountsHidden=true}];
        if(filteredOut)typeof(InventoryViewModel).GetField("_searchText",flags)!.SetValue(vm,"no item matches this");
        Invoke(vm,"ApplyFilter");
        Assert.Equal("pending memo",vm.EditSimpleMemo);Assert.True(vm.HasPendingChanges);
        Assert.Equal(purchaseHidden ? null : (decimal?)110m,vm.EditablePurchasePrice);
        Assert.Equal(salesHidden ? null : (decimal?)220m,vm.EditableSalePrice);
        Assert.Null(vm.PriceGradeRows[0].EditableUnitPrice);Assert.True(vm.PriceGradeRows[0].IsPriceReadOnly);
        if(filteredOut)Assert.Null(vm.SelectedItem);else Assert.Equal(f.Item.Id,vm.SelectedItem!.Id);
        Assert.False((await f.Db.Items.AsNoTracking().SingleAsync()).IsDirty);
        Assert.Equal("original",(await f.Db.Items.AsNoTracking().SingleAsync()).SimpleMemo);
    }
}
