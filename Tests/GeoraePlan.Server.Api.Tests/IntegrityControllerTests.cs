using System.Text.Json;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class IntegrityControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public IntegrityControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task GetReport_ReturnsExpandedIntegrityIssueSet()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var deletedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Deleted Customer",
            NameMatchKey = "DELETEDCUSTOMER",
            TradeType = "매출",
            IsDeleted = true
        };
        var deletedItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Deleted Item",
            NameMatchKey = "DELETEDITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 2m,
            IsDeleted = true
        };
        var deletedInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = deletedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-DELETED",
            InvoiceDate = new DateOnly(2026, 4, 11),
            IsDeleted = true
        };
        var deletedTransaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = deletedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 4, 11),
            TransactionKind = "수금",
            IsDeleted = true
        };
        var deletedPayment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = deletedInvoice.Id,
            PaymentDate = new DateOnly(2026, 4, 11),
            Amount = 10000m,
            IsDeleted = true
        };
        const string duplicateCustomerMatchKey = "DUPLICATECUSTOMER";
        const string duplicateItemNameOnlyMatchKey = "DUPLICATEITEMNAME";
        const string duplicateItemConflictMatchKey = "DUPLICATEITEMCONFLICT";

        dbContext.Customers.AddRange(
            deletedCustomer,
            new Customer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Duplicate Customer A",
                NameMatchKey = duplicateCustomerMatchKey,
                TradeType = "매출"
            },
            new Customer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Duplicate Customer B",
                NameMatchKey = duplicateCustomerMatchKey,
                TradeType = "매출"
            });
        dbContext.Items.AddRange(
            deletedItem,
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Stock Mismatch Item",
                NameMatchKey = "STOCKMISMATCHITEM",
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 3m
            },
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Duplicate Name Item A",
                NameMatchKey = duplicateItemNameOnlyMatchKey,
                SpecificationOriginal = "SPEC-A",
                SpecificationMatchKey = "SPECA",
                CategoryName = "테스트분류",
                TrackingType = ItemTrackingTypes.Stock
            },
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Duplicate Name Item B",
                NameMatchKey = duplicateItemNameOnlyMatchKey,
                SpecificationOriginal = "SPEC-B",
                SpecificationMatchKey = "SPECB",
                CategoryName = "테스트분류",
                TrackingType = ItemTrackingTypes.Stock
            },
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Conflict Item",
                NameMatchKey = duplicateItemConflictMatchKey,
                SpecificationOriginal = "CONFLICT-SPEC",
                SpecificationMatchKey = "CONFLICTSPEC",
                CategoryName = "테스트분류",
                TrackingType = ItemTrackingTypes.Stock
            },
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Conflict Item",
                NameMatchKey = duplicateItemConflictMatchKey,
                SpecificationOriginal = "CONFLICT-SPEC",
                SpecificationMatchKey = "CONFLICTSPEC",
                CategoryName = "테스트분류",
                TrackingType = ItemTrackingTypes.Stock
            });
        dbContext.Invoices.Add(deletedInvoice);
        dbContext.Transactions.AddRange(
            deletedTransaction,
            new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = deletedCustomer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 4, 11),
                TransactionKind = "수금",
                LinkedInvoiceId = deletedInvoice.Id
            });
        dbContext.Payments.AddRange(
            deletedPayment,
            new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = deletedInvoice.Id,
                PaymentDate = new DateOnly(2026, 4, 11),
                Amount = 12000m
            });
        dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = deletedTransaction.Id,
            FileName = "missing-transaction.pdf",
            StoragePath = "attachments/tx/missing-transaction.pdf"
        });
        dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = deletedPayment.Id,
            FileName = "missing-payment.pdf",
            StoragePath = "attachments/payment/missing-payment.pdf"
        });
        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "ASSET-ORPHAN",
            ManagementId = "1",
            ManagementNumber = "2604-001",
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            CustomerId = deletedCustomer.Id,
            ItemId = deletedItem.Id,
            CustomerName = "Deleted Customer",
            ItemName = "Deleted Item"
        });
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = deletedItem.Id,
            WarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Usenet),
            Quantity = 1m
        });

        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issues = payload.Issues.ToDictionary(issue => issue.Code, StringComparer.OrdinalIgnoreCase);

        Assert.Contains("item_stock_snapshot_mismatch", issues.Keys);
        Assert.Contains("duplicate_customer_match_keys", issues.Keys);
        Assert.Contains("duplicate_item_name_match_keys", issues.Keys);
        Assert.Contains("duplicate_item_match_keys", issues.Keys);
        Assert.Contains("deleted_item_stock_residue", issues.Keys);
        Assert.Contains("orphan_item_warehouse_stock_refs", issues.Keys);
        Assert.Contains("orphan_rental_asset_customer_refs", issues.Keys);
        Assert.Contains("orphan_rental_asset_item_refs", issues.Keys);
        Assert.Contains("orphan_transaction_invoice_refs", issues.Keys);
        Assert.Contains("orphan_payment_invoice_refs", issues.Keys);
        Assert.Contains("orphan_attachment_transaction_refs", issues.Keys);
        Assert.Contains("orphan_payment_attachment_refs", issues.Keys);
        Assert.Equal(1, issues["item_stock_snapshot_mismatch"].Count);
        Assert.Equal(2, issues["duplicate_customer_match_keys"].Count);
        Assert.Equal(4, issues["duplicate_item_name_match_keys"].Count);
        Assert.Equal(2, issues["duplicate_item_match_keys"].Count);
        Assert.Equal(1, issues["deleted_item_stock_residue"].Count);
        Assert.Equal(1, issues["orphan_item_warehouse_stock_refs"].Count);
    }

    [Fact]
    public async Task GetReport_IgnoresNonInventoryItems_WhenCountingStockMismatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        dbContext.Items.AddRange(
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Stock Item",
                NameMatchKey = "STOCKITEM",
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 5m
            },
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "NonStock Item",
                NameMatchKey = "NONSTOCKITEM",
                TrackingType = ItemTrackingTypes.NonStock,
                CurrentStock = 99m
            });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var stockMismatch = Assert.Single(payload.Issues, issue => issue.Code == "item_stock_snapshot_mismatch");

        Assert.Equal(1, stockMismatch.Count);
    }

    [Fact]
    public async Task GetReport_DoesNotTreatDisplayVariantSpecsAsConflictDuplicates()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        dbContext.Items.AddRange(
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "[공구]드라이버",
                NameMatchKey = "공구드라이버",
                SpecificationOriginal = "정밀드라이버[+]",
                SpecificationMatchKey = "정밀드라이버",
                CategoryName = "공구/잡자재",
                TrackingType = ItemTrackingTypes.Stock
            },
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "[공구]드라이버",
                NameMatchKey = "공구드라이버",
                SpecificationOriginal = "정밀드라이버[-]",
                SpecificationMatchKey = "정밀드라이버",
                CategoryName = "공구/잡자재",
                TrackingType = ItemTrackingTypes.Stock
            });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);

        Assert.Contains(payload.Issues, issue => issue.Code == "duplicate_item_name_match_keys" && issue.Count == 2);
        Assert.DoesNotContain(payload.Issues, issue => issue.Code == "duplicate_item_match_keys");
    }

    [Fact]
    public async Task GetReport_DoesNotWarnForSameCustomerNameInDifferentResponsibleOffices()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        dbContext.Customers.AddRange(
            new Customer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Same Office Customer",
                NameMatchKey = "SAMEOFFICECUSTOMER",
                TradeType = "매출"
            },
            new Customer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "Same Office Customer",
                NameMatchKey = "SAMEOFFICECUSTOMER",
                TradeType = "매출"
            });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);

        Assert.DoesNotContain(payload.Issues, issue => issue.Code == "duplicate_customer_match_keys");
    }

    [Fact]
    public async Task GetReport_DoesNotWarnForExpectedRentalOrBillingItemDuplicates()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        dbContext.Items.AddRange(
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Rental Asset Model",
                NameMatchKey = "RENTALASSETMODEL",
                CategoryName = "A3컬러복합기",
                ItemKind = ItemKinds.Asset,
                TrackingType = ItemTrackingTypes.Asset,
                IsRental = true
            },
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Rental Asset Model",
                NameMatchKey = "RENTALASSETMODEL",
                CategoryName = "A3컬러복합기",
                ItemKind = ItemKinds.Asset,
                TrackingType = ItemTrackingTypes.Asset,
                IsRental = true
            },
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "사무기기 렌탈대금[5월]",
                NameMatchKey = "사무기기렌탈대금5월",
                SpecificationOriginal = "SL-M2670FN",
                SpecificationMatchKey = "SLM2670FN",
                CategoryName = "렌탈료",
                ItemKind = ItemKinds.Billing,
                TrackingType = ItemTrackingTypes.NonStock
            },
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "사무기기 렌탈대금[5월]",
                NameMatchKey = "사무기기렌탈대금5월",
                SpecificationOriginal = "SL-M2670FN",
                SpecificationMatchKey = "SLM2670FN",
                CategoryName = "렌탈료",
                ItemKind = ItemKinds.Billing,
                TrackingType = ItemTrackingTypes.NonStock
            });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);

        Assert.Contains(payload.Issues, issue => issue.Code == "duplicate_item_name_match_keys" && issue.Count == 4);
        Assert.DoesNotContain(payload.Issues, issue => issue.Code == "duplicate_item_match_keys");
    }

    [Fact]
    public async Task GetReport_DoesNotWarnForSameItemWithDifferentMaterialNumber()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        dbContext.Items.AddRange(
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "External HDD",
                NameMatchKey = "EXTERNALHDD",
                SpecificationOriginal = "LG XD5 500GB",
                SpecificationMatchKey = "LGXD5500GB",
                CategoryName = "주변기기/전자제품",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                MaterialNumber = "화이트핑크"
            },
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "External HDD",
                NameMatchKey = "EXTERNALHDD",
                SpecificationOriginal = "LG XD5 500GB",
                SpecificationMatchKey = "LGXD5500GB",
                CategoryName = "주변기기/전자제품",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                MaterialNumber = "블랙레드"
            });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);

        Assert.Contains(payload.Issues, issue => issue.Code == "duplicate_item_name_match_keys" && issue.Count == 2);
        Assert.DoesNotContain(payload.Issues, issue => issue.Code == "duplicate_item_match_keys");
    }

    [Fact]
    public async Task GetReport_IncludesCrossTenantInventoryTransferIssue()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        dbContext.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Itworld,
            TransferNumber = "TR-CROSS-001",
            TransferDate = new DateOnly(2026, 4, 13),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse
        });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issue = Assert.Single(payload.Issues, current => current.Code == "cross_tenant_inventory_transfers");

        Assert.Equal("Error", issue.Severity);
        Assert.Equal(1, issue.Count);
    }

    [Fact]
    public async Task GetReport_IncludesOperationalDataRiskSignals()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var negativeStockItemId = Guid.NewGuid();
        var rentalProfileId = Guid.NewGuid();
        var rentalAssetId = Guid.NewGuid();
        var deletedInvoiceId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Integrity Customer",
            NameMatchKey = "INTEGRITYCUSTOMER",
            TradeType = "매출"
        });
        dbContext.Items.Add(new Item
        {
            Id = negativeStockItemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Negative Stock Item",
            NameMatchKey = "NEGATIVESTOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = -3m
        });
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = negativeStockItemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = -3m
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-NEG-STOCK",
            InvoiceDate = new DateOnly(2026, 5, 29),
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Lines =
            {
                new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    ItemId = negativeStockItemId,
                    ItemNameOriginal = "Negative Stock Item",
                    Quantity = 3m,
                    UnitPrice = 1000m,
                    LineAmount = 3000m,
                    ItemTrackingType = ItemTrackingTypes.Stock
                }
            }
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = deletedInvoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-DELETED-LINE",
            InvoiceDate = new DateOnly(2026, 5, 28),
            IsDeleted = true,
            Lines =
            {
                new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    ItemNameOriginal = "Deleted Invoice Active Line",
                    Quantity = 1m,
                    UnitPrice = 1000m,
                    LineAmount = 1000m
                }
            }
        });

        var templateJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                DisplayItemName = "렌탈료",
                Quantity = 1m,
                UnitPrice = 2_000m,
                Amount = 2_000m,
                IncludedAssetIds = new[] { rentalAssetId }
            }
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = rentalProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "PROFILE-RISK-001",
            CustomerName = "거래처명만 있는 프로필",
            ItemName = "렌탈료",
            BillingType = "묶음",
            MonthlyAmount = 1_000m,
            BillingTemplateJson = templateJson,
            IsActive = true
        });
        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = rentalAssetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BillingProfileId = rentalProfileId,
            AssetKey = "ASSET-RISK-001",
            ManagementId = "RISK-001",
            ManagementNumber = "RISK-001",
            CustomerName = "거래처명만 있는 프로필",
            ItemName = "복합기",
            MonthlyFee = 3_000m
        });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issues = payload.Issues.ToDictionary(issue => issue.Code, StringComparer.OrdinalIgnoreCase);

        Assert.False(issues.ContainsKey("item_negative_current_stock"));
        Assert.Equal(1, issues["active_invoice_lines_deleted_invoice"].Count);
        Assert.Equal(1, issues["rental_profile_customer_unlinked"].Count);
        Assert.Equal(1, issues["rental_profile_monthly_amount_mismatch"].Count);
        Assert.Equal(1, issues["rental_profile_asset_monthly_amount_mismatch"].Count);
        Assert.Equal(1, issues["rental_asset_template_monthly_mismatch"].Count);


        var lineDetailsResponse = await controller.GetReportDetails("active_invoice_lines_deleted_invoice", CancellationToken.None);
        var lineDetailsOk = Assert.IsType<OkObjectResult>(lineDetailsResponse.Result);
        var lineDetails = Assert.IsType<IntegrityIssueDetailResultDto>(lineDetailsOk.Value);
        Assert.Single(lineDetails.Rows);
        Assert.Contains("삭제 전표", lineDetails.Rows[0].ReferenceText, StringComparison.Ordinal);

        var unlinkedProfileDetailsResponse = await controller.GetReportDetails("rental_profile_customer_unlinked", CancellationToken.None);
        var unlinkedProfileDetailsOk = Assert.IsType<OkObjectResult>(unlinkedProfileDetailsResponse.Result);
        var unlinkedProfileDetails = Assert.IsType<IntegrityIssueDetailResultDto>(unlinkedProfileDetailsOk.Value);
        var unlinkedProfileRow = Assert.Single(unlinkedProfileDetails.Rows);
        Assert.Contains("ASSET-RISK-001", unlinkedProfileRow.DetailText, StringComparison.Ordinal);

        var rentalDetailsResponse = await controller.GetReportDetails("rental_profile_asset_monthly_amount_mismatch", CancellationToken.None);
        var rentalDetailsOk = Assert.IsType<OkObjectResult>(rentalDetailsResponse.Result);
        var rentalDetails = Assert.IsType<IntegrityIssueDetailResultDto>(rentalDetailsOk.Value);
        Assert.Single(rentalDetails.Rows);
        Assert.Contains("자산월요금합계 3,000", rentalDetails.Rows[0].DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReport_DoesNotWarnProfileAssetMonthlyMismatch_WhenTemplateMatchesProfileAmount()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var rentalProfileId = Guid.NewGuid();
        var rentalAssetId = Guid.NewGuid();
        var templateJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                DisplayItemName = "묶음 렌탈료",
                Quantity = 1m,
                UnitPrice = 300_000m,
                Amount = 300_000m
            }
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = rentalProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "PROFILE-BUNDLE-SELF-CONSISTENT",
            CustomerId = Guid.NewGuid(),
            CustomerName = "묶음 청구 거래처",
            ItemName = "묶음 렌탈료",
            BillingType = "묶음",
            MonthlyAmount = 300_000m,
            BillingTemplateJson = templateJson,
            IsActive = true
        });
        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = rentalAssetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BillingProfileId = rentalProfileId,
            AssetKey = "ASSET-BUNDLE-001",
            ManagementId = "BUNDLE-001",
            ManagementNumber = "BUNDLE-001",
            CustomerName = "묶음 청구 거래처",
            ItemName = "복합기",
            MonthlyFee = 100_000m
        });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);

        Assert.DoesNotContain(payload.Issues, issue => string.Equals(issue.Code, "rental_profile_monthly_amount_mismatch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(payload.Issues, issue => string.Equals(issue.Code, "rental_profile_asset_monthly_amount_mismatch", StringComparison.OrdinalIgnoreCase));

        var detailsResponse = await controller.GetReportDetails("rental_profile_asset_monthly_amount_mismatch", CancellationToken.None);
        var detailsOk = Assert.IsType<OkObjectResult>(detailsResponse.Result);
        var details = Assert.IsType<IntegrityIssueDetailResultDto>(detailsOk.Value);
        Assert.Empty(details.Rows);
    }

    [Fact]
    public async Task GetReportDetails_ForUnlinkedRentalProfile_IncludesSimilarCustomerCandidates()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        dbContext.Customers.Add(new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "명성다이캐스팅",
            NameMatchKey = MatchKeyNormalizer.Normalize("명성다이캐스팅"),
            TradeType = "매출"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "PROFILE-SIMILAR-CUSTOMER",
            CustomerName = "명성다이케스팅",
            ItemName = "렌탈료",
            BillingType = "묶음",
            MonthlyAmount = 100_000m,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var detailsResponse = await controller.GetReportDetails("rental_profile_customer_unlinked", CancellationToken.None);
        var detailsOk = Assert.IsType<OkObjectResult>(detailsResponse.Result);
        var details = Assert.IsType<IntegrityIssueDetailResultDto>(detailsOk.Value);
        var row = Assert.Single(details.Rows);
        Assert.Contains("유사 거래처", row.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("명성다이캐스팅", row.ReferenceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReport_AndDetails_IncludeAmbiguousSharedItemTenantScopeIssue()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Conflict Customer",
            NameMatchKey = "CONFLICTCUSTOMER",
            TradeType = "매출"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "Shared Conflict Item",
            NameMatchKey = "SHAREDCONFLICTITEM",
            SpecificationOriginal = "A3",
            CategoryName = "복합기",
            TrackingType = ItemTrackingTypes.Stock
        };

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-SHARED-CONFLICT",
            InvoiceDate = new DateOnly(2026, 4, 16),
            Lines =
            {
                new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    SpecificationOriginal = item.SpecificationOriginal,
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 1000m,
                    LineAmount = 1000m
                }
            }
        });
        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            AssetKey = "ASSET-SHARED-CONFLICT",
            ManagementId = "9901",
            ManagementNumber = "2604-9901",
            ItemId = item.Id,
            CustomerName = "ITWORLD Customer",
            ItemName = item.NameOriginal
        });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var reportResponse = await controller.GetReport(CancellationToken.None);
        var reportOk = Assert.IsType<OkObjectResult>(reportResponse.Result);
        var report = Assert.IsType<IntegrityReportDto>(reportOk.Value);
        var issue = Assert.Single(report.Issues, current => current.Code == "ambiguous_shared_item_tenant_scope");

        Assert.Equal("Warning", issue.Severity);
        Assert.Equal(1, issue.Count);
        Assert.Equal("공용(ALL) 품목 중 사용 이력이 서로 다른 업체로 섞여 tenant 자동 보정이 보류된 항목이 있습니다.", issue.Message);

        var detailResponse = await controller.GetReportDetails("ambiguous_shared_item_tenant_scope", CancellationToken.None);
        var detailOk = Assert.IsType<OkObjectResult>(detailResponse.Result);
        var detail = Assert.IsType<IntegrityIssueDetailResultDto>(detailOk.Value);
        var row = Assert.Single(detail.Rows);

        Assert.Equal("ambiguous_shared_item_tenant_scope", detail.Code);
        Assert.Equal(issue.Message, detail.Message);
        Assert.Equal("Shared Conflict Item", row.PrimaryText);
        Assert.Contains("tenant 후보", row.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("렌탈 ITWORLD", row.DetailText, StringComparison.Ordinal);
        Assert.Contains("전표 USENET", row.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReportDetails_ReturnsDuplicateRentalAssetRows()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        dbContext.RentalAssets.AddRange(
            new RentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "ASSET-DUP-001",
                ManagementId = "901",
                ManagementNumber = "2604-001",
                CustomerName = "알파 거래처",
                ItemName = "복합기 A",
                InstallLocation = "본관 1층"
            },
            new RentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                AssetKey = "ASSET-DUP-001",
                ManagementId = "902",
                ManagementNumber = "2604-002",
                CustomerName = "베타 거래처",
                ItemName = "복합기 B",
                InstallLocation = "본관 2층"
            });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetReportDetails("duplicate_rental_asset_keys", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityIssueDetailResultDto>(ok.Value);

        Assert.Equal("duplicate_rental_asset_keys", payload.Code);
        Assert.Equal("Error", payload.Severity);
        Assert.Equal(2, payload.DetailCount);
        Assert.Collection(
            payload.Rows,
            first =>
            {
                Assert.Equal("렌탈자산", first.EntityType);
                Assert.Equal("2604-001", first.PrimaryText);
                Assert.Contains("자산키 ASSET-DUP-001", first.DetailText, StringComparison.Ordinal);
            },
            second =>
            {
                Assert.Equal("렌탈자산", second.EntityType);
                Assert.Equal("2604-002", second.PrimaryText);
                Assert.Contains("자산키 ASSET-DUP-001", second.DetailText, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task GetReportDetails_ReturnsStockMismatchRowsWithWarehouseBreakdown()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var itemId = Guid.NewGuid();
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Mismatch Item",
            NameMatchKey = "MISMATCHITEM",
            SpecificationOriginal = "A3",
            CategoryName = "복합기",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        });
        dbContext.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = "USENET-A",
                Quantity = 2m
            },
            new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = "USENET-B",
                Quantity = 1m
            });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetReportDetails("item_stock_snapshot_mismatch", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityIssueDetailResultDto>(ok.Value);
        var row = Assert.Single(payload.Rows);

        Assert.Equal("item_stock_snapshot_mismatch", payload.Code);
        Assert.Equal(1, payload.DetailCount);
        Assert.Equal("Mismatch Item", row.PrimaryText);
        Assert.Contains("현재재고 5", row.DetailText, StringComparison.Ordinal);
        Assert.Contains("창고합계 3", row.DetailText, StringComparison.Ordinal);
        Assert.Contains("USENET-A:2", row.DetailText, StringComparison.Ordinal);
        Assert.Contains("USENET-B:1", row.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReportDetails_ReturnsDeletedItemStockResidueRows()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var deletedItemId = Guid.NewGuid();
        dbContext.Items.Add(new Item
        {
            Id = deletedItemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Deleted Residue Item",
            NameMatchKey = "DELETEDRESIDUEITEM",
            SpecificationOriginal = "A4",
            CategoryName = "복합기",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 4m,
            IsDeleted = true
        });
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = deletedItemId,
            WarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Usenet),
            Quantity = 4m
        });
        await dbContext.SaveChangesAsync();

        var controller = new IntegrityController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetReportDetails("deleted_item_stock_residue", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityIssueDetailResultDto>(ok.Value);
        var row = Assert.Single(payload.Rows);

        Assert.Equal("deleted_item_stock_residue", payload.Code);
        Assert.Equal(1, payload.DetailCount);
        Assert.Equal("삭제 품목", row.EntityType);
        Assert.Equal("Deleted Residue Item", row.PrimaryText);
        Assert.Contains("삭제 품목 현재재고 4", row.DetailText, StringComparison.Ordinal);
        Assert.Contains("창고행 1", row.DetailText, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        var dbContext = new AppDbContext(options, currentUser, revisionClock);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static TestCurrentUserContext CreateAdminUser()
        => new()
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = [];

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }
}
