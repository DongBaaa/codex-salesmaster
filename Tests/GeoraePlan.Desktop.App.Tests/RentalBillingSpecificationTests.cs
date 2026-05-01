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

        Assert.Equal("\uB9AC\uCF54 IMC 2000 \uC678 \uD504\uB9B0\uD130", item.Specification);
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

        Assert.Equal("\uB9AC\uCF54 IMC 2000 \uC678 \uD504\uB9B0\uD130", specification);
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

        Assert.Equal("IMC2000 \uC678 7\uB300", specification);
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

        Assert.Equal("IMC2000 \uC678 1\uB300", item.Specification);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(target));
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
