using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetLinkAmountPrivacyTests
{
    public static IEnumerable<object[]> AmountCases()
    {
        foreach (var purchase in new[] { false, true })
        foreach (var sales in new[] { false, true })
        foreach (var purchaseHidden in new[] { false, true })
        foreach (var salesHidden in new[] { false, true })
        foreach (var zero in new[] { false, true })
            yield return [purchase, sales, purchaseHidden, salesHidden, zero];
    }

    [Theory]
    [MemberData(nameof(AmountCases))]
    public async Task CandidateLoadAndSelectedClonePreserveAmountAccess(bool purchase, bool sales, bool purchaseHidden, bool salesHidden, bool zero)
    {
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var session = new SessionState();
        var permissions = new List<string> { AppPermissionNames.RentalViewAll, AppPermissionNames.RentalProfileEdit, AppPermissionNames.RentalAssetEdit };
        if (purchase) permissions.Add(AppPermissionNames.AmountViewPurchase);
        if (sales) permissions.Add(AppPermissionNames.AmountViewSales);
        session.SetOfflineSession(new UserSessionDto { Username = "link-privacy", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly, Permissions = permissions });
        var asset = new LocalRentalAsset { Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet, ManagementNumber = "LINK-PRIVACY", ItemName = "장비",
            AssetStatus = "창고", PurchasePrice = zero ? 0 : 100, SalePrice = zero ? 0 : 200, MonthlyFee = zero ? 0 : 300,
            PurchaseVendor = "원본 공급사", DepositText = "50000", ContractMonths = 24,
            FreeSupplyItems = "정품 토너", PaidSupplyItems = "특수 용지", BillingExclusionReason = "원본 사유",
            RentalEndDate = new(2028, 9, 1), DisposalDate = new(2028, 10, 1),
            PurchaseAmountsHidden = purchaseHidden, SalesAmountsHidden = salesHidden, IsDirty = false };
        db.RentalAssets.Add(asset); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var rental = new RentalStateService(db, local);
        var vm = new RentalAssetLinkDialogViewModel(rental, session, null, null, "시험 거래처", OfficeCodeCatalog.Usenet, "본점");
        await vm.LoadAsync();
        var candidate = Assert.Single(vm.Assets);
        AssertAmounts(candidate);
        candidate.IsSelected = true; candidate.Notes = "비금액 수정";
        var copy = Assert.Single(vm.GetSelectedAssets());
        AssertAmounts(copy); Assert.Equal("비금액 수정", copy.Notes);
        var billing = new RentalBillingViewModel(rental, local, session);
        try
        {
            await billing.LoadAsync();
            await billing.NewProfileCommand.ExecuteAsync(null);
            billing.EditBillingType = "개별";
            billing.EditCustomerName = "시험 거래처";
            billing.ApplyAssetLinkSelections([copy]);
            var included = Assert.Single(billing.IncludedAssets);
            AssertAmounts(included);
            var edits = (IReadOnlyList<RentalBillingAssetLinkEdit>)typeof(RentalBillingViewModel)
                .GetMethod("BuildPendingAssetLinkEdits", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(billing, null)!;
            var edit = Assert.Single(edits);
            Assert.Equal<decimal?>(purchase && !purchaseHidden ? asset.PurchasePrice : null, edit.PurchasePrice);
            Assert.Equal<decimal?>(sales && !salesHidden ? asset.SalePrice : null, edit.SalePrice);
            Assert.Equal<decimal?>(sales && !salesHidden ? asset.MonthlyFee : null, edit.MonthlyFee);
            if (!sales || salesHidden)
            {
                var linkedLines = billing.TemplateItems.Where(line => line.IncludedAssetIds.Contains(asset.Id)).ToList();
                Assert.NotEmpty(linkedLines);
                Assert.All(linkedLines, line => { Assert.Null(line.UnitPrice); Assert.Null(line.Amount); });
            }
        }
        finally { await billing.CancelAndDrainPendingBackgroundWorkAsync(); }
        var persisted = await db.RentalAssets.AsNoTracking().SingleAsync();
        Assert.Equal(asset.MonthlyFee, persisted.MonthlyFee); Assert.False(persisted.IsDirty);

        void AssertAmounts(RentalBillingAssetOption row)
        {
            Assert.Equal<decimal?>(purchase && !purchaseHidden ? asset.PurchasePrice : null, row.PurchasePrice);
            Assert.Equal<decimal?>(sales && !salesHidden ? asset.SalePrice : null, row.SalePrice);
            Assert.Equal<decimal?>(sales && !salesHidden ? asset.MonthlyFee : null, row.MonthlyFee);
            Assert.Equal(!purchase || purchaseHidden, row.PurchaseAmountsReadOnly);
            Assert.Equal(!sales || salesHidden, row.SalesAmountsReadOnly);
            Assert.Equal(asset.PurchaseVendor, row.PurchaseVendor);
            Assert.Equal(sales && !salesHidden ? asset.DepositText : string.Empty, row.DepositText);
            Assert.Equal(asset.ContractMonths, row.ContractMonths);
            Assert.Equal(asset.FreeSupplyItems, row.FreeSupplyItems);
            Assert.Equal(asset.PaidSupplyItems, row.PaidSupplyItems);
            Assert.Equal(asset.BillingExclusionReason, row.BillingExclusionReason);
            Assert.Equal(asset.RentalEndDate?.ToDateTime(TimeOnly.MinValue), row.RentalEndDate);
            Assert.Equal(asset.DisposalDate?.ToDateTime(TimeOnly.MinValue), row.DisposalDate);
        }
    }
}
