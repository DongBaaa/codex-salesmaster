using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ItemPriceGradePersistenceTests
{
    [Fact]
    public async Task UpsertItemWithPriceGrades_RollsBackItemWhenGradeValidationFails()
    {
        PrepareAppRoot("georaeplan-item-price-grade-atomic-save");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var local = CreateLocalStateService(db, session);
            var itemId = Guid.NewGuid();
            var optionId = Guid.NewGuid();
            db.PriceGradeOptions.Add(new LocalPriceGradeOption
            {
                Id = optionId,
                Name = "매출단가",
                PriceSource = SelectionOptionDefaults.PriceSourceSales,
                IsActive = true
            });
            await db.SaveChangesAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() => local.UpsertItemAsync(
                new LocalItem
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "원자적 저장 확인 품목",
                    SpecificationOriginal = "A",
                    TrackingType = ItemTrackingTypes.Stock,
                    ItemKind = ItemKinds.Product
                },
                session,
                OfficeCodeCatalog.Usenet,
                [
                    new LocalItemPriceGrade
                    {
                        Id = Guid.NewGuid(),
                        PriceGradeOptionId = optionId,
                        PriceGradeName = "매출단가",
                        UnitPrice = -1m,
                        IsActive = true
                    }
                ]));

            db.ChangeTracker.Clear();
            Assert.False(await db.Items.IgnoreQueryFilters().AnyAsync(item => item.Id == itemId));
            Assert.False(await db.ItemPriceGrades.IgnoreQueryFilters().AnyAsync(row => row.ItemId == itemId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveItemPriceGradesForItem_RekeysRowIdsCopiedFromAnotherItem()
    {
        PrepareAppRoot("georaeplan-item-price-grade-copy");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var local = CreateLocalStateService(db, session);

            var optionId = Guid.NewGuid();
            var sourceItemId = Guid.NewGuid();
            var targetItemId = Guid.NewGuid();
            var copiedRowId = Guid.NewGuid();
            db.PriceGradeOptions.Add(new LocalPriceGradeOption
            {
                Id = optionId,
                Name = "매출단가",
                PriceSource = SelectionOptionDefaults.PriceSourceSales,
                IsActive = true
            });
            db.Items.AddRange(
                new LocalItem
                {
                    Id = sourceItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "원본 품목",
                    NameMatchKey = "원본품목",
                    SpecificationOriginal = "A",
                    SpecificationMatchKey = "A",
                    IsDirty = false
                },
                new LocalItem
                {
                    Id = targetItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "복사 품목",
                    NameMatchKey = "복사품목",
                    SpecificationOriginal = "B",
                    SpecificationMatchKey = "B",
                    IsDirty = false
                });
            db.ItemPriceGrades.Add(new LocalItemPriceGrade
            {
                Id = copiedRowId,
                ItemId = sourceItemId,
                PriceGradeOptionId = optionId,
                PriceGradeName = "매출단가",
                UnitPrice = 10_000m,
                IsActive = true
            });
            await db.SaveChangesAsync();

            await local.SaveItemPriceGradesForItemAsync(
                targetItemId,
                [
                    new LocalItemPriceGrade
                    {
                        Id = copiedRowId,
                        ItemId = targetItemId,
                        PriceGradeOptionId = optionId,
                        PriceGradeName = "매출단가",
                        UnitPrice = 12_000m,
                        IsActive = true
                    }
                ]);

            var rows = await db.ItemPriceGrades.AsNoTracking()
                .OrderBy(row => row.ItemId)
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows.Select(row => row.Id).Distinct().Count());
            Assert.Equal(copiedRowId, rows.Single(row => row.ItemId == sourceItemId).Id);
            var copied = rows.Single(row => row.ItemId == targetItemId);
            Assert.NotEqual(copiedRowId, copied.Id);
            Assert.Equal(12_000m, copied.UnitPrice);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveItemPriceGradesForItem_PersistsCustomOptionsAndRenamesWithOption()
    {
        PrepareAppRoot("georaeplan-item-price-grade-persist");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.NewGuid();
            var publicGradeId = Guid.NewGuid();
            var vipGradeId = Guid.NewGuid();
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                NameOriginal = "복합기",
                NameMatchKey = "복합기",
                SpecificationOriginal = "A3",
                SpecificationMatchKey = "A3",
                IsDirty = false
            });
            db.PriceGradeOptions.AddRange(
                new LocalPriceGradeOption
                {
                    Id = publicGradeId,
                    Name = "관공서",
                    PriceSource = SelectionOptionDefaults.PriceSourceSales,
                    SortOrder = 10,
                    IsActive = true,
                    IsDirty = false
                },
                new LocalPriceGradeOption
                {
                    Id = vipGradeId,
                    Name = "VIP",
                    PriceSource = SelectionOptionDefaults.PriceSourceA,
                    SortOrder = 20,
                    IsActive = true,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = CreateLocalStateService(db, session);

            await local.SaveItemPriceGradesForItemAsync(
                itemId,
                [
                    new LocalItemPriceGrade { PriceGradeOptionId = publicGradeId, PriceGradeName = "관공서", UnitPrice = 123_000m, IsActive = true },
                    new LocalItemPriceGrade { PriceGradeOptionId = vipGradeId, PriceGradeName = "VIP", UnitPrice = 98_000m, IsActive = true }
                ]);

            var savedRows = await local.GetItemPriceGradesAsync(session);
            Assert.Equal(2, savedRows.Count);
            Assert.Contains(savedRows, row => row.PriceGradeOptionId == publicGradeId && row.UnitPrice == 123_000m && row.IsDirty);
            Assert.Contains(savedRows, row => row.PriceGradeOptionId == vipGradeId && row.UnitPrice == 98_000m && row.IsDirty);

            var renameResult = await local.SavePriceGradeOptionAsync(
                new LocalPriceGradeOption
                {
                    Id = vipGradeId,
                    Name = "VIP기관",
                    PriceSource = SelectionOptionDefaults.PriceSourceA,
                    SortOrder = 20
                },
                previousName: "VIP");

            Assert.True(renameResult.Success, renameResult.Message);
            var renamedRow = await db.ItemPriceGrades.AsNoTracking().SingleAsync(row => row.PriceGradeOptionId == vipGradeId);
            Assert.Equal("VIP기관", renamedRow.PriceGradeName);
            Assert.True(renamedRow.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DirtyItemPriceGrades_AdminSelectsOnlyRowsWithExistingWritableParent()
    {
        PrepareAppRoot("georaeplan-item-price-grade-dirty-parent-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var parentItemId = Guid.NewGuid();
            var parentBackedGradeId = Guid.NewGuid();
            var orphanGradeId = Guid.NewGuid();
            db.Items.Add(new LocalItem
            {
                Id = parentItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "관리자 등급단가 부모 품목",
                NameMatchKey = "관리자등급단가부모품목",
                IsDirty = false
            });
            db.ItemPriceGrades.AddRange(
                new LocalItemPriceGrade
                {
                    Id = parentBackedGradeId,
                    ItemId = parentItemId,
                    PriceGradeOptionId = Guid.NewGuid(),
                    PriceGradeName = "부모 있음",
                    UnitPrice = 10_000m,
                    IsActive = true,
                    IsDirty = true
                },
                new LocalItemPriceGrade
                {
                    Id = orphanGradeId,
                    ItemId = Guid.NewGuid(),
                    PriceGradeOptionId = Guid.NewGuid(),
                    PriceGradeName = "부모 없음",
                    UnitPrice = 20_000m,
                    IsActive = true,
                    IsDirty = true
                });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = CreateLocalStateService(db, session);

            var selected = await local.GetDirtyItemPriceGradesForSyncAsync(session);

            Assert.Equal(parentBackedGradeId, Assert.Single(selected).Id);
            Assert.DoesNotContain(selected, row => row.Id == orphanGradeId);
            Assert.Equal(1, await local.CountDirtyAsync(session));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeletePriceGradeOption_RejectsWhenItemCustomPriceUsesIt()
    {
        PrepareAppRoot("georaeplan-item-price-grade-delete-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.NewGuid();
            var gradeId = Guid.NewGuid();
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                NameOriginal = "토너",
                NameMatchKey = "토너",
                SpecificationOriginal = "검정",
                SpecificationMatchKey = "검정",
                IsDirty = false
            });
            db.PriceGradeOptions.Add(new LocalPriceGradeOption
            {
                Id = gradeId,
                Name = "관공서",
                PriceSource = SelectionOptionDefaults.PriceSourceSales,
                SortOrder = 10,
                IsActive = true,
                IsDirty = false
            });
            db.ItemPriceGrades.Add(new LocalItemPriceGrade
            {
                ItemId = itemId,
                PriceGradeOptionId = gradeId,
                PriceGradeName = "관공서",
                UnitPrice = 10_000m,
                IsActive = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db, CreateAdminSession());

            var result = await local.DeletePriceGradeOptionAsync(gradeId);

            Assert.False(result.Success);
            Assert.Contains("커스텀 단가", result.Message);
            var option = await db.PriceGradeOptions.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == gradeId);
            Assert.False(option.IsDeleted);
            Assert.True(option.IsActive);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void SalesViewModel_ApplyInputItem_UsesCustomGradePriceByCustomerGrade()
    {
        var itemId = Guid.NewGuid();
        var viewModel = new SalesViewModel(
            local: null!,
            print: null!,
            invoicePrintService: null!,
            session: new SessionState(),
            newInvoiceVoucherType: VoucherType.Sales);
        var applyCache = typeof(SalesViewModel).GetMethod(
            "ApplyItemPriceGradeCache",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyCache);

        applyCache!.Invoke(
            viewModel,
            [
                new[]
                {
                    new LocalItemPriceGrade
                    {
                        ItemId = itemId,
                        PriceGradeName = "관공서",
                        UnitPrice = 77_000m,
                        IsActive = true
                    }
                }
            ]);
        viewModel.CustomerPriceGrade = "관공서";

        viewModel.ApplyInputItem(new LocalItem
        {
            Id = itemId,
            NameOriginal = "복합기",
            SpecificationOriginal = "A3",
            Unit = "대",
            SalePrice = 100_000m,
            RetailPrice = 120_000m,
            PriceGradeA = 90_000m
        });

        Assert.Equal(77_000m, viewModel.InputUnitPrice);
        Assert.Equal(77_000m, viewModel.InputLineAmount);
    }

    [Fact]
    public void SalesViewModel_CustomerGradeChange_RepricesCurrentInputItemWithCustomGradePrice()
    {
        var itemId = Guid.NewGuid();
        var viewModel = new SalesViewModel(
            local: null!,
            print: null!,
            invoicePrintService: null!,
            session: new SessionState(),
            newInvoiceVoucherType: VoucherType.Sales);
        var applyCache = typeof(SalesViewModel).GetMethod(
            "ApplyItemPriceGradeCache",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyCache);

        applyCache!.Invoke(
            viewModel,
            [
                new[]
                {
                    new LocalItemPriceGrade
                    {
                        ItemId = itemId,
                        PriceGradeName = "관공서",
                        UnitPrice = 77_000m,
                        IsActive = true
                    }
                }
            ]);

        viewModel.ApplyInputItem(new LocalItem
        {
            Id = itemId,
            NameOriginal = "복합기",
            SpecificationOriginal = "A3",
            Unit = "대",
            SalePrice = 100_000m,
            RetailPrice = 120_000m,
            PriceGradeA = 90_000m
        });

        Assert.Equal(100_000m, viewModel.InputUnitPrice);

        viewModel.CustomerPriceGrade = "관공서";

        Assert.Equal(77_000m, viewModel.InputUnitPrice);
        Assert.Equal(77_000m, viewModel.InputLineAmount);
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "item-price-grade-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static LocalStateService CreateLocalStateService(LocalDbContext db, SessionState session)
        => new(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
