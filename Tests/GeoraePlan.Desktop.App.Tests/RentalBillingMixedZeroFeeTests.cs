using System.Reflection;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingMixedZeroFeeTests
{
    public static IEnumerable<object[]> FeeCases()
    {
        foreach (var mode in new[] { "개별", "묶음" })
        foreach (var fees in new[] {
            new decimal[] { 240_000m, 0m },
            new decimal[] { 240_000m, 240_000m, 240_000m, 240_000m, 0m, 0m, 0m },
            new decimal[] { 240_000m, 240_000m },
            new decimal[] { 240_000m, 120_000m },
            new decimal[] { 0m, 0m },
            new decimal[] { 100.01m, 0m, 0m } })
            yield return new object[] { mode, fees };
    }

    [Theory]
    [MemberData(nameof(FeeCases))]
    public void AutomaticAmount_PreservesFeeSumAndIndividualAssetFees(string mode, decimal[] fees)
    {
        var (vm, item, assets) = Create(mode, fees);
        Invoke(vm, "ApplyIncludedAssetMonthlyFeesToTemplateItem", item, true);
        Assert.Equal(Math.Round(fees.Sum(), 2), Math.Round(item.EffectiveAmount, 2));
        Assert.Equal(mode == "묶음" ? 1m : fees.Length, item.Quantity);
        Assert.Equal(fees, assets.Select(x => x.MonthlyFee));
        var edits = Invoke<IReadOnlyList<RentalBillingAssetLinkEdit>>(vm, "BuildPendingAssetLinkEdits");
        foreach (var edit in edits.Where(x => x.MonthlyFee.HasValue))
            Assert.Equal(assets.Single(x => x.AssetId == edit.AssetId).MonthlyFee, edit.MonthlyFee.GetValueOrDefault());
    }

    [Fact]
    public void ModelGroupingCommand_PreservesMixedZeroFeeTotalAndAssetIdentities()
    {
        var (vm, item, assets) = Create("개별", [240_000m, 0m, 0m]);
        Assert.True(vm.AutoGroupIndividualTemplateItemsByModelCommand.CanExecute(null));
        vm.AutoGroupIndividualTemplateItemsByModelCommand.Execute(null);
        var grouped = Assert.Single(vm.TemplateItems);
        Assert.Equal(240_000m, grouped.Amount);
        Assert.Equal(3m, grouped.Quantity);
        Assert.Equal(assets.Select(x => x.AssetId).OrderBy(x => x), grouped.IncludedAssetIds.OrderBy(x => x));
        Assert.Equal(new decimal[] { 240_000m, 0m, 0m }, assets.Select(x => x.MonthlyFee));
    }

    [Fact]
    public void IncludedAssetMonthlyFeeChange_ToZero_UpdatesAmountWithoutChangingOtherAssetFees()
    {
        var (vm, item, assets) = Create("개별", [240_000m, 240_000m]);
        Invoke(vm, "RefreshBillingAssetCollections", (object?)null);
        var row = vm.IncludedAssets.Single(x => x.AssetId == assets[1].AssetId);
        row.MonthlyFee = 0m;
        Assert.Equal(240_000m, item.Amount);
        Assert.Equal(240_000m, assets[0].MonthlyFee);
        Assert.Equal(0m, assets[1].MonthlyFee);
        var edits = Invoke<IReadOnlyList<RentalBillingAssetLinkEdit>>(vm, "BuildPendingAssetLinkEdits");
        Assert.Contains(edits, x => x.AssetId == row.AssetId && x.MonthlyFee == 0m);
        Assert.DoesNotContain(edits, x => x.AssetId == assets[0].AssetId && x.MonthlyFee.HasValue && x.MonthlyFee != 240_000m);
    }

    private static (RentalBillingViewModel vm, RentalBillingTemplateEditorItem item, List<RentalBillingAssetOption> assets) Create(string mode, decimal[] fees)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "fee-regression", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [AppPermissionNames.RentalProfileEdit, AppPermissionNames.RentalAssetEdit] });
        var vm = new RentalBillingViewModel(null!, null!, session) { EditBillingType = mode, EditCustomerName = "격리 요금 검증" };
        var assets = (List<RentalBillingAssetOption>)typeof(RentalBillingViewModel).GetField("_includedAssetPool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        var quantity = mode == "묶음" ? 1m : fees.Length;
        var item = new RentalBillingTemplateEditorItem { DisplayItemName = "같은 모델", BillingLineMode = mode, Quantity = quantity, UnitPrice = fees.Sum() / quantity };
        foreach (var fee in fees)
        {
            var asset = new RentalBillingAssetOption { AssetId = Guid.NewGuid(), ItemName = "같은 모델", MonthlyFee = fee, TargetCustomerName = vm.EditCustomerName };
            assets.Add(asset); item.IncludedAssetIds.Add(asset.AssetId);
        }
        vm.TemplateItems.Add(item); vm.SelectedTemplateItem = item;
        return (vm, item, assets);
    }

    private static object? Invoke(RentalBillingViewModel vm, string name, params object?[] args)
        => typeof(RentalBillingViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args);
    private static T Invoke<T>(RentalBillingViewModel vm, string name, params object?[] args)
        => Assert.IsAssignableFrom<T>(Invoke(vm, name, args));
}
