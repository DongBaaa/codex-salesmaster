using 거래플랜.Desktop.App.Printing;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InvoicePrintModelCurrentInfoSynchronizerTests
{
    [Fact]
    public void RefreshLinkedBusinessPartyFields_ReplacesSavedPartySnapshotAndKeepsDocumentCustomizations()
    {
        var oldStamp = new byte[] { 1, 2, 3 };
        var freshStamp = new byte[] { 9, 8, 7 };
        var saved = new InvoicePrintModel
        {
            SupplierBusinessNumber = "OLD-SUPPLIER-BIZ",
            SupplierName = "OldSupplier",
            SupplierRepresentative = "OldSupplierRep",
            SupplierPhone = "OldSupplierPhone",
            SupplierAddress = "OldSupplierAddress",
            BuyerBusinessNumber = "OLD-BUYER-BIZ",
            BuyerName = "OldBuyer",
            BuyerRepresentative = "OldBuyerRep",
            BuyerPhone = "OldBuyerPhone",
            BuyerAddress = "OldBuyerAddress",
            ManagerName = "OldManager",
            BankAccountText = "OldBank 999-999",
            SupplierStampImage = oldStamp,
            Memo = "KeepMemo",
            FooterText = "KeepFooter",
            EstimateValidityText = "KeepValidity",
            EstimateRemarks = "KeepRemarks",
            Lines =
            {
                new InvoicePrintLineModel { No = 1, ItemName = "KeepLine", Quantity = 1m, UnitPrice = 10m, Amount = 10m }
            }
        };
        var currentDefault = new InvoicePrintModel
        {
            SupplierBusinessNumber = "NEW-SUPPLIER-BIZ",
            SupplierName = "FreshSupplier",
            SupplierRepresentative = "FreshSupplierRep",
            SupplierPhone = "FreshSupplierPhone",
            SupplierAddress = "FreshSupplierAddress",
            BuyerBusinessNumber = "NEW-BUYER-BIZ",
            BuyerName = "FreshBuyer",
            BuyerRepresentative = "FreshBuyerRep",
            BuyerPhone = "FreshBuyerPhone",
            BuyerAddress = "FreshBuyerAddress",
            ManagerName = "FreshManager",
            BankAccountText = "FreshBank 111-222",
            SupplierStampImage = freshStamp,
            Memo = "DefaultMemo",
            FooterText = "DefaultFooter",
            EstimateValidityText = "DefaultValidity",
            EstimateRemarks = "DefaultRemarks"
        };

        InvoicePrintModelCurrentInfoSynchronizer.RefreshLinkedBusinessPartyFields(saved, currentDefault);

        Assert.Equal("NEW-SUPPLIER-BIZ", saved.SupplierBusinessNumber);
        Assert.Equal("FreshSupplier", saved.SupplierName);
        Assert.Equal("FreshSupplierRep", saved.SupplierRepresentative);
        Assert.Equal("FreshSupplierPhone", saved.SupplierPhone);
        Assert.Equal("FreshSupplierAddress", saved.SupplierAddress);
        Assert.Equal("NEW-BUYER-BIZ", saved.BuyerBusinessNumber);
        Assert.Equal("FreshBuyer", saved.BuyerName);
        Assert.Equal("FreshBuyerRep", saved.BuyerRepresentative);
        Assert.Equal("FreshBuyerPhone", saved.BuyerPhone);
        Assert.Equal("FreshBuyerAddress", saved.BuyerAddress);
        Assert.Equal("FreshManager", saved.ManagerName);
        Assert.Equal("FreshBank 111-222", saved.BankAccountText);
        Assert.Equal(freshStamp, saved.SupplierStampImage);
        Assert.NotSame(freshStamp, saved.SupplierStampImage);

        Assert.Equal("KeepMemo", saved.Memo);
        Assert.Equal("KeepFooter", saved.FooterText);
        Assert.Equal("KeepValidity", saved.EstimateValidityText);
        Assert.Equal("KeepRemarks", saved.EstimateRemarks);
        Assert.Single(saved.Lines);
        Assert.Equal("KeepLine", saved.Lines[0].ItemName);
        Assert.Equal(new byte[] { 1, 2, 3 }, oldStamp);
    }

    [Fact]
    public void SavedPayloadLoadersAndSupplementDocuments_PreferCurrentMasterInfoBeforeRendering()
    {
        var root = FindRepositoryRoot();
        var mainViewModel = File.ReadAllText(Path.Combine(root.FullName, "Desktop", "거래플랜.Desktop.App", "ViewModels", "MainViewModel.cs"));
        var salesViewModel = File.ReadAllText(Path.Combine(root.FullName, "Desktop", "거래플랜.Desktop.App", "ViewModels", "SalesViewModel.cs"));
        var supplementDocumentBuilder = File.ReadAllText(Path.Combine(root.FullName, "Desktop", "거래플랜.Desktop.App", "Services", "SupplementDocumentBuilder.cs"));
        const string call = "InvoicePrintModelCurrentInfoSynchronizer.RefreshLinkedBusinessPartyFields(saved, defaultModel)";

        Assert.Contains(call, mainViewModel);
        Assert.Contains(call, salesViewModel);
        Assert.Contains("Safe(company.BusinessNumber, printModel?.SupplierBusinessNumber ?? string.Empty)", supplementDocumentBuilder);
        Assert.Contains("Safe(company.TradeName, Safe(printModel?.SupplierName, \"기본 상호\"))", supplementDocumentBuilder);
        Assert.Contains("Safe(company.Representative, Safe(printModel?.SupplierRepresentative, \"대표자\"))", supplementDocumentBuilder);
        Assert.Contains("Safe(company.Address, printModel?.SupplierAddress ?? string.Empty)", supplementDocumentBuilder);
        Assert.Contains("Safe(company.ContactNumber, printModel?.SupplierPhone ?? string.Empty)", supplementDocumentBuilder);
        Assert.True(
            supplementDocumentBuilder.IndexOf("customer.Recipient", StringComparison.Ordinal) <
            supplementDocumentBuilder.IndexOf("printModel?.BuyerName", StringComparison.Ordinal));
        Assert.True(
            supplementDocumentBuilder.IndexOf("company.BankAccountText", StringComparison.Ordinal) <
            supplementDocumentBuilder.IndexOf("printModel?.BankAccountText", StringComparison.Ordinal));
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Desktop")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!;
    }

}
