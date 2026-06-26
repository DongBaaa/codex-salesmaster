using System.Reflection;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingSpecificationTests
{
    [Fact]
    public void RentalBillingViewModel_AppliesRepresentativeAssetSpecificationForBundleTemplateItem()
    {
        var representativeAssetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var otherAssetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var vm = new RentalBillingViewModel(null!, null!, new SessionState());
        var includedPool = GetPrivateField<List<RentalBillingAssetOption>>(vm, "_includedAssetPool");
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = representativeAssetId,
            ItemName = "IMC 2000",
            Manufacturer = "\uB9AC\uCF54",
            ItemCategoryName = "\uBCF5\uD569\uAE30"
        });
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = otherAssetId,
            ItemName = "MFC-L5700DN",
            Manufacturer = "\uBE0C\uB77C\uB354",
            ItemCategoryName = "\uD504\uB9B0\uD130"
        });

        var item = new RentalBillingTemplateEditorItem
        {
            BillingLineMode = "\uBB36\uC74C",
            RepresentativeAssetId = representativeAssetId,
            Specification = "\uB300\uD45C \uC7A5\uBE44"
        };
        item.IncludedAssetIds.Add(representativeAssetId);
        item.IncludedAssetIds.Add(otherAssetId);

        InvokePrivateInstance(vm, "ApplyTemplateSalesFieldDefaults", item);

        Assert.Equal("\uB9AC\uCF54 IMC 2000 \uC678", item.Specification);
    }

    [Fact]
    public void RentalBillingViewModel_AppliesManufacturerModelSpecificationForIndividualTemplateItem()
    {
        var assetId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var vm = new RentalBillingViewModel(null!, null!, new SessionState());
        var includedPool = GetPrivateField<List<RentalBillingAssetOption>>(vm, "_includedAssetPool");
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = assetId,
            ItemName = "MFC-L5700DN",
            Manufacturer = "\uBE0C\uB77C\uB354",
            ItemCategoryName = "\uD504\uB9B0\uD130"
        });

        var item = new RentalBillingTemplateEditorItem
        {
            BillingLineMode = "\uAC1C\uBCC4"
        };
        item.IncludedAssetIds.Add(assetId);

        InvokePrivateInstance(vm, "ApplyTemplateSalesFieldDefaults", item);

        Assert.Equal("\uBE0C\uB77C\uB354 MFC-L5700DN", item.Specification);
    }

    [Fact]
    public void RentalBillingViewModel_SyncIndividualTemplateItems_GroupsSameModelAndUnitPriceAsQuantity()
    {
        var assetAId = Guid.Parse("33333333-3333-3333-3333-3333333333a1");
        var assetBId = Guid.Parse("33333333-3333-3333-3333-3333333333b2");
        var vm = new RentalBillingViewModel(null!, null!, new SessionState())
        {
            EditBillingType = "\uAC1C\uBCC4"
        };
        var includedPool = GetPrivateField<List<RentalBillingAssetOption>>(vm, "_includedAssetPool");
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = assetAId,
            ItemName = "IMC2010",
            MonthlyFee = 50_000m
        });
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = assetBId,
            ItemName = "IMC2010",
            MonthlyFee = 50_000m
        });

        var item = new RentalBillingTemplateEditorItem
        {
            DisplayItemName = "IMC2010",
            BillingLineMode = "\uAC1C\uBCC4",
            Quantity = 1m,
            UnitPrice = 50_000m
        };
        item.IncludedAssetIds.Add(assetAId);
        item.IncludedAssetIds.Add(assetBId);
        vm.TemplateItems.Add(item);

        InvokePrivateInstance(vm, "SyncIndividualTemplateItemsFromIncludedAssets");
        InvokePrivateInstance(vm, "UpdateTemplateDerivedValues");

        var mergedItem = Assert.Single(vm.TemplateItems);
        Assert.Equal("IMC2010", mergedItem.DisplayItemName);
        Assert.Equal(2m, mergedItem.Quantity);
        Assert.Equal(50_000m, mergedItem.UnitPrice);
        Assert.Equal(100_000m, mergedItem.Amount);
        Assert.Equal(2, mergedItem.IncludedAssetIds.Distinct().Count());
        Assert.Equal("IMC2010", mergedItem.Specification);
    }

    [Fact]
    public void RentalBillingViewModel_SyncIndividualTemplateItems_GroupsSameModelWithDifferentFeesWithoutOverwritingAssetFees()
    {
        var assetAId = Guid.Parse("33333333-3333-3333-3333-3333333335a1");
        var assetBId = Guid.Parse("33333333-3333-3333-3333-3333333335b2");
        var vm = new RentalBillingViewModel(null!, null!, new SessionState())
        {
            EditBillingType = "\uAC1C\uBCC4"
        };
        var includedPool = GetPrivateField<List<RentalBillingAssetOption>>(vm, "_includedAssetPool");
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = assetAId,
            ItemName = "IMC2010",
            MonthlyFee = 50_000m
        });
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = assetBId,
            ItemName = "IMC2010",
            MonthlyFee = 70_000m
        });

        var item = new RentalBillingTemplateEditorItem
        {
            DisplayItemName = "IMC2010",
            BillingLineMode = "\uAC1C\uBCC4",
            Quantity = 1m,
            UnitPrice = 50_000m
        };
        item.IncludedAssetIds.Add(assetAId);
        item.IncludedAssetIds.Add(assetBId);
        vm.TemplateItems.Add(item);

        InvokePrivateInstance(vm, "SyncIndividualTemplateItemsFromIncludedAssets");
        InvokePrivateInstance(vm, "UpdateTemplateDerivedValues");

        var mergedItem = Assert.Single(vm.TemplateItems);
        Assert.Equal("IMC2010", mergedItem.DisplayItemName);
        Assert.Equal(2m, mergedItem.Quantity);
        Assert.Equal(60_000m, mergedItem.UnitPrice);
        Assert.Equal(120_000m, mergedItem.Amount);
        Assert.Equal(2, mergedItem.IncludedAssetIds.Distinct().Count());
        Assert.Equal(50_000m, includedPool.Single(asset => asset.AssetId == assetAId).MonthlyFee);
        Assert.Equal(70_000m, includedPool.Single(asset => asset.AssetId == assetBId).MonthlyFee);
    }

    [Fact]
    public void RentalBillingViewModel_SyncIndividualTemplateItems_GroupsSameZeroFeeModelAsQuantity()
    {
        var assetAId = Guid.Parse("33333333-3333-3333-3333-3333333334a1");
        var assetBId = Guid.Parse("33333333-3333-3333-3333-3333333334b2");
        var vm = new RentalBillingViewModel(null!, null!, new SessionState())
        {
            EditBillingType = "\uAC1C\uBCC4"
        };
        var includedPool = GetPrivateField<List<RentalBillingAssetOption>>(vm, "_includedAssetPool");
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = assetAId,
            ItemName = "SL-M3820ND",
            Manufacturer = "\uC0BC\uC131\uC804\uC790",
            MonthlyFee = 0m
        });
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = assetBId,
            ItemName = "SL-M3820ND",
            Manufacturer = "\uC0BC\uC131\uC804\uC790",
            MonthlyFee = 0m
        });

        var item = new RentalBillingTemplateEditorItem
        {
            DisplayItemName = "SL-M3820ND",
            BillingLineMode = "\uAC1C\uBCC4",
            Specification = "\uC0BC\uC131\uC804\uC790 SL-M3820ND \uC678 1\uB300",
            Quantity = 1m,
            UnitPrice = 0m
        };
        item.IncludedAssetIds.Add(assetAId);
        item.IncludedAssetIds.Add(assetBId);
        vm.TemplateItems.Add(item);

        InvokePrivateInstance(vm, "SyncIndividualTemplateItemsFromIncludedAssets");
        InvokePrivateInstance(vm, "UpdateTemplateDerivedValues");

        var mergedItem = Assert.Single(vm.TemplateItems);
        Assert.Equal("SL-M3820ND", mergedItem.DisplayItemName);
        Assert.Equal(2m, mergedItem.Quantity);
        Assert.Equal(0m, mergedItem.UnitPrice);
        Assert.Equal(0m, mergedItem.Amount);
        Assert.Equal(2, mergedItem.IncludedAssetIds.Distinct().Count());
        Assert.Equal("SL-M3820ND", mergedItem.Specification);
    }

    [Fact]
    public void RentalStateService_InvoiceSpecificationKeepsBracketedManufacturerWithoutDuplication()
    {
        var templateItem = new RentalBillingTemplateItemModel
        {
            Specification = string.Empty
        };
        var asset = new LocalRentalAsset
        {
            ItemName = "[\uBE0C\uB77C\uB354]MFC-L8690CDW",
            Manufacturer = "\uBE0C\uB77C\uB354"
        };

        var specification = InvokePrivateStatic<string>(
            typeof(RentalStateService),
            "ResolveIndividualInvoiceSpecification",
            templateItem,
            asset);

        Assert.Equal("[\uBE0C\uB77C\uB354]MFC-L8690CDW", specification);
    }

    [Fact]
    public void RentalStateService_RentalInvoiceItemNameUsesOfficeEquipmentRentalFee()
    {
        var itemName = InvokePrivateStatic<string>(
            typeof(RentalStateService),
            "BuildMonthlyRentalInvoiceItemName",
            new DateOnly(2026, 5, 1),
            "IMC2010");

        Assert.Equal("\uC0AC\uBB34\uAE30\uAE30 \uB80C\uD0C8\uB300\uAE08[5\uC6D4]", itemName);
    }

    [Fact]
    public void RentalStateService_InvoiceSpecificationRefreshesLegacyBundlePlaceholder()
    {
        var representativeAssetId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var otherAssetId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var templateItem = new RentalBillingTemplateItemModel
        {
            Specification = "IMC 2000 \uC678 \uD504\uB9B0\uD130"
        };
        var representativeAsset = new LocalRentalAsset
        {
            Id = representativeAssetId,
            ItemName = "IMC 2000",
            Manufacturer = "\uB9AC\uCF54",
            ItemCategoryName = "\uBCF5\uD569\uAE30"
        };
        var otherAsset = new LocalRentalAsset
        {
            Id = otherAssetId,
            ItemName = "MFC-L5700DN",
            Manufacturer = "\uBE0C\uB77C\uB354",
            ItemCategoryName = "\uD504\uB9B0\uD130"
        };

        var specification = InvokePrivateStatic<string>(
            typeof(RentalStateService),
            "ResolveBundleInvoiceSpecification",
            templateItem,
            representativeAsset,
            new List<LocalRentalAsset> { representativeAsset, otherAsset });

        Assert.Equal("\uB9AC\uCF54 IMC 2000 \uC678", specification);
    }

    [Fact]
    public void RentalStateService_BundleSpecificationAddsPlainEtcWhenExplicitSpecIsRepresentativeOnly()
    {
        var representativeAssetId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var otherAssetId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var templateItem = new RentalBillingTemplateItemModel
        {
            DisplayItemName = "IMC2010",
            Specification = "\uB9AC\uCF54\uCF54\uB9AC\uC544 IMC2010"
        };
        var representativeAsset = new LocalRentalAsset
        {
            Id = representativeAssetId,
            ItemName = "IMC2010",
            Manufacturer = "\uB9AC\uCF54\uCF54\uB9AC\uC544",
            ItemCategoryName = "\uBCF5\uD569\uAE30"
        };
        var otherAsset = new LocalRentalAsset
        {
            Id = otherAssetId,
            ItemName = "IMC2010",
            Manufacturer = "\uB9AC\uCF54\uCF54\uB9AC\uC544",
            ItemCategoryName = "\uBCF5\uD569\uAE30"
        };

        var specification = InvokePrivateStatic<string>(
            typeof(RentalStateService),
            "ResolveBundleInvoiceSpecification",
            templateItem,
            representativeAsset,
            new List<LocalRentalAsset> { representativeAsset, otherAsset });

        Assert.Equal("\uB9AC\uCF54\uCF54\uB9AC\uC544 IMC2010 \uC678", specification);
    }

    [Fact]
    public void RentalStateService_BundleSpecificationFallsBackToTemplateDisplayItemName()
    {
        var representativeAssetId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var templateItem = new RentalBillingTemplateItemModel
        {
            DisplayItemName = "IMC2000",
            Specification = "\uB300\uD45C \uC7A5\uBE44 \uC678 7\uB300"
        };
        var representativeAsset = new LocalRentalAsset
        {
            Id = representativeAssetId,
            ItemName = string.Empty,
            Manufacturer = string.Empty
        };
        var assets = new List<LocalRentalAsset> { representativeAsset };
        for (var i = 0; i < 7; i++)
        {
            assets.Add(new LocalRentalAsset
            {
                Id = Guid.NewGuid(),
                ItemName = string.Empty,
                Manufacturer = string.Empty
            });
        }

        var specification = InvokePrivateStatic<string>(
            typeof(RentalStateService),
            "ResolveBundleInvoiceSpecification",
            templateItem,
            representativeAsset,
            assets);

        Assert.Equal("IMC2000 \uC678", specification);
    }

    [Fact]
    public void RentalBillingViewModel_BundleSpecificationFallsBackToDisplayItemName()
    {
        var representativeAssetId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var otherAssetId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var vm = new RentalBillingViewModel(null!, null!, new SessionState());
        var includedPool = GetPrivateField<List<RentalBillingAssetOption>>(vm, "_includedAssetPool");
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = representativeAssetId,
            ItemName = string.Empty,
            Manufacturer = string.Empty
        });
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = otherAssetId,
            ItemName = string.Empty,
            Manufacturer = string.Empty
        });

        var item = new RentalBillingTemplateEditorItem
        {
            DisplayItemName = "IMC2000",
            BillingLineMode = "\uBB36\uC74C",
            RepresentativeAssetId = representativeAssetId,
            Specification = "\uB300\uD45C \uC7A5\uBE44 \uC678 1\uB300"
        };
        item.IncludedAssetIds.Add(representativeAssetId);
        item.IncludedAssetIds.Add(otherAssetId);

        InvokePrivateInstance(vm, "ApplyTemplateSalesFieldDefaults", item);

        Assert.Equal("IMC2000 \uC678", item.Specification);
    }

    [Fact]
    public void RentalBillingViewModel_TemplateWithoutIncludedAssetsRequiresExplicitLaterFlag()
    {
        var vm = new RentalBillingViewModel(null!, null!, new SessionState());
        vm.TemplateItems.Add(new RentalBillingTemplateEditorItem
        {
            DisplayItemName = "렌탈 임대료",
            BillingLineMode = "묶음",
            Quantity = 1m,
            UnitPrice = 100_000m,
            Amount = 100_000m
        });

        var blocked = InvokeValidateTemplateConfiguration(vm);

        Assert.False(blocked.IsValid);
        Assert.Contains("내부 포함 장비", blocked.Message, StringComparison.Ordinal);

        vm.LinkAssetsLater = true;
        var allowed = InvokeValidateTemplateConfiguration(vm);

        Assert.True(allowed.IsValid, allowed.Message);
    }

    [Fact]
    public void RentalBillingViewModel_UpdateTemplateDerivedValuesWarnsWhenProfileAndTemplateAssetCountsDiffer()
    {
        var includedAssetId = Guid.Parse("12121212-1212-1212-1212-121212121212");
        var vm = new RentalBillingViewModel(null!, null!, new SessionState());
        SetPrivateField(vm, "_selectedRow", new RentalBillingViewRow
        {
            AssetCount = 3,
            IncludedAssetCount = 1,
            HasPersistedProfile = true
        });
        var item = new RentalBillingTemplateEditorItem
        {
            DisplayItemName = "IMC2010",
            BillingLineMode = "개별",
            Quantity = 1m,
            UnitPrice = 50_000m,
            Amount = 50_000m
        };
        item.IncludedAssetIds.Add(includedAssetId);
        vm.TemplateItems.Add(item);

        InvokePrivateInstance(vm, "UpdateTemplateDerivedValues");

        Assert.True(vm.HasBillingAssetCoverageWarning);
        Assert.Contains("청구 프로필 연결 자산 3대", vm.BillingAssetCoverageWarning, StringComparison.Ordinal);
        Assert.Contains("표시품목 포함 자산 1대", vm.GetBillingAssetCoverageStartWarning(), StringComparison.Ordinal);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(target));
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static (bool IsValid, string Message) InvokeValidateTemplateConfiguration(RentalBillingViewModel vm)
    {
        var method = typeof(RentalBillingViewModel).GetMethod(
            "TryValidateTemplateConfiguration",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        object?[] args = [null];
        var isValid = Assert.IsType<bool>(method!.Invoke(vm, args));
        return (isValid, Assert.IsType<string>(args[0]));
    }

    private static void InvokePrivateInstance(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, args);
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<T>(method!.Invoke(null, args));
    }
}
