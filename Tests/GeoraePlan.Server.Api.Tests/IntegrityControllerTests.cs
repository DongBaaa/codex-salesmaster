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
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class IntegrityControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _fileStorageRoot;

    public IntegrityControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fileStorageRoot = Path.Combine(Path.GetTempPath(), "georaeplan-integrity-file-storage-tests", Guid.NewGuid().ToString("N"));
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

        var controller = CreateController(dbContext, currentUser);

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
    public async Task GetReport_FlagsInvoiceLinesWithHardMissingInvoiceRows()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var missingInvoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();

        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = lineId,
            InvoiceId = missingInvoiceId,
            ItemNameOriginal = "Hard Missing Invoice Line",
            SpecificationOriginal = "A4",
            Unit = "대",
            Quantity = 2m,
            UnitPrice = 15000m,
            LineAmount = 30000m,
            Remark = "missing invoice",
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issue = Assert.Single(payload.Issues, current => current.Code == "invoice_line_missing_invoice_rows");

        Assert.Equal("Error", issue.Severity);
        Assert.Equal(1, issue.Count);

        var detailsResponse = await controller.GetReportDetails("invoice_line_missing_invoice_rows", CancellationToken.None);
        var detailsOk = Assert.IsType<OkObjectResult>(detailsResponse.Result);
        var details = Assert.IsType<IntegrityIssueDetailResultDto>(detailsOk.Value);
        var row = Assert.Single(details.Rows);

        Assert.Equal("전표세부내역", row.EntityType);
        Assert.Equal(FormatGuidForTest(lineId), row.EntityIdText);
        Assert.Equal("Hard Missing Invoice Line", row.PrimaryText);
        Assert.Contains(FormatGuidForTest(missingInvoiceId), row.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("삭제상태 삭제", row.DetailText, StringComparison.Ordinal);
        Assert.Contains("금액 30,000", row.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReport_FlagsRemainingChildRowsWithHardMissingParentRows()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var missingTransactionId = Guid.NewGuid();
        var transactionAttachmentId = Guid.NewGuid();
        var missingCustomerId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var missingProfileId = Guid.NewGuid();
        var billingLogId = Guid.NewGuid();
        var missingTransferId = Guid.NewGuid();
        var transferLineId = Guid.NewGuid();

        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = transactionAttachmentId,
            TransactionId = missingTransactionId,
            FileName = "deleted-transaction-attachment.pdf",
            AttachmentType = "증빙",
            FileSize = 256,
            StoragePath = "attachments/transactions/deleted-transaction-attachment.pdf",
            IsDeleted = true
        });
        dbContext.CustomerContracts.Add(new CustomerContract
        {
            Id = contractId,
            CustomerId = missingCustomerId,
            ContractType = "거래계약서",
            FileName = "missing-customer-contract.pdf",
            FileSize = 512,
            StoragePath = "contracts/missing-customer-contract.pdf"
        });
        dbContext.RentalBillingLogs.Add(new RentalBillingLog
        {
            Id = billingLogId,
            BillingProfileId = missingProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BillingYearMonth = "2026-06",
            ScheduledDate = new DateOnly(2026, 6, 25),
            Status = "예정",
            BilledAmount = 77000m
        });
        dbContext.InventoryTransferLines.Add(new InventoryTransferLine
        {
            Id = transferLineId,
            TransferId = missingTransferId,
            ItemNameOriginal = "Missing Transfer Item",
            SpecificationOriginal = "A4",
            Unit = "대",
            Quantity = 3m,
            ReceivedQuantity = 1m,
            QuantityDifference = -2m,
            Remark = "missing transfer"
        });
        await dbContext.SaveChangesAsync();
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issues = payload.Issues.ToDictionary(issue => issue.Code, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(1, issues["deleted_transaction_attachment_missing_transaction_rows"].Count);
        Assert.Equal(1, issues["customer_contract_missing_customer_rows"].Count);
        Assert.Equal(1, issues["rental_billing_log_missing_profile_rows"].Count);
        Assert.Equal(1, issues["inventory_transfer_line_missing_transfer_rows"].Count);

        var transactionAttachmentDetails = await GetSingleDetailRowAsync(controller, "deleted_transaction_attachment_missing_transaction_rows");
        Assert.Equal("삭제 거래첨부", transactionAttachmentDetails.EntityType);
        Assert.Equal(FormatGuidForTest(transactionAttachmentId), transactionAttachmentDetails.EntityIdText);
        Assert.Contains(FormatGuidForTest(missingTransactionId), transactionAttachmentDetails.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("삭제상태 삭제", transactionAttachmentDetails.DetailText, StringComparison.Ordinal);

        var contractDetails = await GetSingleDetailRowAsync(controller, "customer_contract_missing_customer_rows");
        Assert.Equal("거래처계약서", contractDetails.EntityType);
        Assert.Equal(FormatGuidForTest(contractId), contractDetails.EntityIdText);
        Assert.Contains(FormatGuidForTest(missingCustomerId), contractDetails.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("missing-customer-contract.pdf", contractDetails.PrimaryText, StringComparison.Ordinal);

        var billingLogDetails = await GetSingleDetailRowAsync(controller, "rental_billing_log_missing_profile_rows");
        Assert.Equal("렌탈 청구로그", billingLogDetails.EntityType);
        Assert.Equal(FormatGuidForTest(billingLogId), billingLogDetails.EntityIdText);
        Assert.Contains(FormatGuidForTest(missingProfileId), billingLogDetails.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("청구 77,000", billingLogDetails.SecondaryText, StringComparison.Ordinal);

        var transferLineDetails = await GetSingleDetailRowAsync(controller, "inventory_transfer_line_missing_transfer_rows");
        Assert.Equal("재고이동 세부내역", transferLineDetails.EntityType);
        Assert.Equal(FormatGuidForTest(transferLineId), transferLineDetails.EntityIdText);
        Assert.Contains(FormatGuidForTest(missingTransferId), transferLineDetails.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("요청수량 3", transferLineDetails.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReport_DoesNotExposeUnscopableHardMissingChildRowsToOfficeScopedUsers()
    {
        var currentUser = CreateOfficeScopedUser();
        await using var dbContext = CreateDbContext(currentUser);
        var missingTransactionId = Guid.NewGuid();
        var transactionAttachmentId = Guid.NewGuid();
        var missingCustomerId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var missingProfileId = Guid.NewGuid();
        var billingLogId = Guid.NewGuid();
        var missingTransferId = Guid.NewGuid();
        var transferLineId = Guid.NewGuid();

        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = transactionAttachmentId,
            TransactionId = missingTransactionId,
            FileName = "deleted-transaction-attachment.pdf",
            AttachmentType = "evidence",
            FileSize = 256,
            IsDeleted = true
        });
        dbContext.CustomerContracts.Add(new CustomerContract
        {
            Id = contractId,
            CustomerId = missingCustomerId,
            ContractType = "contract",
            FileName = "missing-customer-contract.pdf",
            FileSize = 512
        });
        dbContext.RentalBillingLogs.Add(new RentalBillingLog
        {
            Id = billingLogId,
            BillingProfileId = missingProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BillingYearMonth = "2026-06",
            ScheduledDate = new DateOnly(2026, 6, 25),
            Status = "scheduled",
            BilledAmount = 77000m
        });
        dbContext.InventoryTransferLines.Add(new InventoryTransferLine
        {
            Id = transferLineId,
            TransferId = missingTransferId,
            ItemNameOriginal = "Missing Transfer Item",
            Unit = "EA",
            Quantity = 3m
        });
        await dbContext.SaveChangesAsync();
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);

        Assert.DoesNotContain(payload.Issues, issue => issue.Code == "deleted_transaction_attachment_missing_transaction_rows");
        Assert.DoesNotContain(payload.Issues, issue => issue.Code == "customer_contract_missing_customer_rows");
        Assert.DoesNotContain(payload.Issues, issue => issue.Code == "inventory_transfer_line_missing_transfer_rows");
        var rentalLogIssue = Assert.Single(payload.Issues, issue => issue.Code == "rental_billing_log_missing_profile_rows");
        Assert.Equal(1, rentalLogIssue.Count);

        foreach (var code in new[]
        {
            "deleted_transaction_attachment_missing_transaction_rows",
            "customer_contract_missing_customer_rows",
            "inventory_transfer_line_missing_transfer_rows"
        })
        {
            var detailResponse = await controller.GetReportDetails(code, CancellationToken.None);
            var detailOk = Assert.IsType<OkObjectResult>(detailResponse.Result);
            var detailPayload = Assert.IsType<IntegrityIssueDetailResultDto>(detailOk.Value);
            Assert.Empty(detailPayload.Rows);
        }
    }

    [Fact]
    public async Task GetReport_FlagsRentalAssignmentHistoryMissingReferencesAndMultipleCurrentRows()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var missingAssetId = Guid.NewGuid();
        var missingCustomerId = Guid.NewGuid();
        var missingProfileId = Guid.NewGuid();
        var missingHistoryId = Guid.NewGuid();
        var duplicateAssetId = Guid.NewGuid();
        var duplicateHistoryId1 = Guid.NewGuid();
        var duplicateHistoryId2 = Guid.NewGuid();

        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = duplicateAssetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "ASSET-CURRENT-DUP",
            ManagementId = "CURRENT-DUP",
            ManagementNumber = "CURRENT-DUP",
            CustomerName = "Current Duplicate Customer",
            ItemName = "Copier",
            MonthlyFee = 100000m
        });
        dbContext.RentalAssetAssignmentHistories.AddRange(
            new RentalAssetAssignmentHistory
            {
                Id = missingHistoryId,
                AssetId = missingAssetId,
                CustomerId = missingCustomerId,
                BillingProfileId = missingProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Missing History Customer",
                InstallLocation = "Missing Site",
                BillingProfileDisplay = "Missing Profile",
                ItemName = "Missing History Copier",
                MachineNumber = "MISSING-SN",
                ManagementNumber = "HIST-MISSING",
                MonthlyFee = 100000m,
                ContractStartDate = new DateOnly(2026, 6, 1),
                IsCurrent = true,
                LinkedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new RentalAssetAssignmentHistory
            {
                Id = duplicateHistoryId1,
                AssetId = duplicateAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Current Duplicate Customer",
                ItemName = "Copier",
                ManagementNumber = "CURRENT-DUP-1",
                MonthlyFee = 100000m,
                IsCurrent = true,
                LinkedAtUtc = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc)
            },
            new RentalAssetAssignmentHistory
            {
                Id = duplicateHistoryId2,
                AssetId = duplicateAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Current Duplicate Customer",
                ItemName = "Copier",
                ManagementNumber = "CURRENT-DUP-2",
                MonthlyFee = 100000m,
                IsCurrent = true,
                LinkedAtUtc = new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc)
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issues = payload.Issues.ToDictionary(issue => issue.Code, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(1, issues["rental_assignment_missing_reference_rows"].Count);
        Assert.Equal(2, issues["rental_asset_multiple_current_assignments"].Count);

        var missingDetails = await GetSingleDetailRowAsync(controller, "rental_assignment_missing_reference_rows");
        Assert.Equal(FormatGuidForTest(missingHistoryId), missingDetails.EntityIdText);
        Assert.Contains(FormatGuidForTest(missingAssetId), missingDetails.ReferenceText, StringComparison.Ordinal);
        Assert.Contains(FormatGuidForTest(missingCustomerId), missingDetails.ReferenceText, StringComparison.Ordinal);
        Assert.Contains(FormatGuidForTest(missingProfileId), missingDetails.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("100,000", missingDetails.DetailText, StringComparison.Ordinal);

        var duplicateDetailsResponse = await controller.GetReportDetails("rental_asset_multiple_current_assignments", CancellationToken.None);
        var duplicateDetailsOk = Assert.IsType<OkObjectResult>(duplicateDetailsResponse.Result);
        var duplicateDetails = Assert.IsType<IntegrityIssueDetailResultDto>(duplicateDetailsOk.Value);
        Assert.Equal(2, duplicateDetails.DetailCount);
        Assert.Contains(duplicateDetails.Rows, row => row.EntityIdText == FormatGuidForTest(duplicateHistoryId1));
        Assert.Contains(duplicateDetails.Rows, row => row.EntityIdText == FormatGuidForTest(duplicateHistoryId2));
        Assert.All(duplicateDetails.Rows, row =>
        {
            Assert.Contains(FormatGuidForTest(duplicateAssetId), row.ReferenceText, StringComparison.Ordinal);
            Assert.Contains("2", row.ReferenceText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task GetReport_ClassifiesPastRentalAssignmentStaleReferencesAsInfo()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var assetId = Guid.NewGuid();
        var missingCustomerId = Guid.NewGuid();
        var missingProfileId = Guid.NewGuid();
        var historyId = Guid.NewGuid();

        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "ASSET-PAST-STALE",
            ManagementId = "PAST-STALE",
            ManagementNumber = "PAST-STALE",
            CustomerName = "Past Snapshot Customer",
            ItemName = "Copier",
            MonthlyFee = 100000m
        });
        dbContext.RentalAssetAssignmentHistories.Add(new RentalAssetAssignmentHistory
        {
            Id = historyId,
            AssetId = assetId,
            CustomerId = missingCustomerId,
            BillingProfileId = missingProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = "Past Snapshot Customer",
            BillingProfileDisplay = "Past Snapshot Profile",
            ItemName = "Copier",
            ManagementNumber = "PAST-STALE",
            MonthlyFee = 100000m,
            IsCurrent = false,
            LinkedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UnlinkedAtUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);

        Assert.DoesNotContain(payload.Issues, issue => issue.Code == "rental_assignment_missing_reference_rows");
        var staleIssue = Assert.Single(payload.Issues, issue => issue.Code == "rental_assignment_historical_stale_reference_rows");
        Assert.Equal("Info", staleIssue.Severity);
        Assert.Equal(1, staleIssue.Count);

        var details = await GetSingleDetailRowAsync(controller, "rental_assignment_historical_stale_reference_rows");
        Assert.Equal(FormatGuidForTest(historyId), details.EntityIdText);
        Assert.Contains(FormatGuidForTest(missingCustomerId), details.ReferenceText, StringComparison.Ordinal);
        Assert.Contains(FormatGuidForTest(missingProfileId), details.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("100,000", details.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReport_FiltersRentalAssignmentHistoryIssuesByOfficeScope()
    {
        var currentUser = CreateOfficeScopedUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scopedHistoryId = Guid.NewGuid();

        dbContext.RentalAssetAssignmentHistories.AddRange(
            new RentalAssetAssignmentHistory
            {
                Id = scopedHistoryId,
                AssetId = Guid.NewGuid(),
                BillingProfileId = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Scoped Missing History",
                ItemName = "Scoped Copier",
                ManagementNumber = "SCOPED-HIST",
                IsCurrent = true,
                LinkedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new RentalAssetAssignmentHistory
            {
                Id = Guid.NewGuid(),
                AssetId = Guid.NewGuid(),
                BillingProfileId = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                CustomerName = "Outside Missing History",
                ItemName = "Outside Copier",
                ManagementNumber = "OUTSIDE-HIST",
                IsCurrent = true,
                LinkedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issue = Assert.Single(payload.Issues, current => current.Code == "rental_assignment_missing_reference_rows");
        Assert.Equal(1, issue.Count);

        var row = await GetSingleDetailRowAsync(controller, "rental_assignment_missing_reference_rows");
        Assert.Equal(FormatGuidForTest(scopedHistoryId), row.EntityIdText);
        Assert.Contains(OfficeCodeCatalog.Usenet, row.ScopeText, StringComparison.Ordinal);
        Assert.DoesNotContain(OfficeCodeCatalog.Yeonsu, row.ScopeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReport_FlagsRentalBillingRunSettlementMismatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Settlement mismatch customer",
            NameMatchKey = "SETTLEMENTMISMATCHCUSTOMER",
            TradeType = "매출"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Settlement mismatch customer",
            MonthlyAmount = 100_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "완료",
                    BilledAmount = 100_000m,
                    SettledAmount = 100_000m,
                    SettlementStatus = "입금확인",
                    SettledDate = new DateOnly(2026, 6, 26)
                }
            })
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 26),
            TransactionKind = "렌탈수금",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 60_000m,
            ReceiptTotal = 60_000m,
            SettlementAmount = 60_000m,
            IsDeleted = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issue = Assert.Single(payload.Issues, current => current.Code == "rental_billing_run_settlement_mismatch");

        Assert.Equal("Error", issue.Severity);
        Assert.Equal(1, issue.Count);

        var detailsResponse = await controller.GetReportDetails("rental_billing_run_settlement_mismatch", CancellationToken.None);
        var detailsOk = Assert.IsType<OkObjectResult>(detailsResponse.Result);
        var details = Assert.IsType<IntegrityIssueDetailResultDto>(detailsOk.Value);
        var row = Assert.Single(details.Rows);

        Assert.Equal(FormatGuidForTest(profileId), row.EntityIdText);
        Assert.Contains(FormatGuidForTest(runId), row.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("100,000", row.SecondaryText, StringComparison.Ordinal);
        Assert.Contains("60,000", row.SecondaryText, StringComparison.Ordinal);
        Assert.Contains("거래내역 합계 60,000", row.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReport_FlagsRentalBillingProfileSummaryMismatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Profile summary mismatch customer",
            NameMatchKey = "PROFILESUMMARYMISMATCHCUSTOMER",
            TradeType = "매출"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Profile summary mismatch customer",
            MonthlyAmount = 100_000m,
            BillingStatus = "청구중",
            SettlementStatus = "미입금",
            CompletionStatus = "미완료",
            SettledAmount = 0m,
            OutstandingAmount = 100_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "완료",
                    BilledAmount = 100_000m,
                    SettledAmount = 100_000m,
                    SettlementStatus = "입금확인",
                    SettledDate = new DateOnly(2026, 6, 26)
                }
            })
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 26),
            TransactionKind = "렌탈수금",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 100_000m,
            ReceiptTotal = 100_000m,
            SettlementAmount = 100_000m,
            IsDeleted = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issue = Assert.Single(payload.Issues, current => current.Code == "rental_billing_profile_summary_mismatch");

        Assert.Equal("Error", issue.Severity);
        Assert.Equal(1, issue.Count);
        Assert.DoesNotContain(payload.Issues, current => current.Code == "rental_billing_run_settlement_mismatch");

        var detailsResponse = await controller.GetReportDetails("rental_billing_profile_summary_mismatch", CancellationToken.None);
        var detailsOk = Assert.IsType<OkObjectResult>(detailsResponse.Result);
        var details = Assert.IsType<IntegrityIssueDetailResultDto>(detailsOk.Value);
        var row = Assert.Single(details.Rows);

        Assert.Equal(FormatGuidForTest(profileId), row.EntityIdText);
        Assert.Contains(FormatGuidForTest(runId), row.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("프로필 저장 정산 0", row.SecondaryText, StringComparison.Ordinal);
        Assert.Contains("기대 100,000", row.SecondaryText, StringComparison.Ordinal);
        Assert.Contains("프로필 저장 미수 100,000", row.DetailText, StringComparison.Ordinal);
        Assert.Contains("기대 미수 0", row.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReport_FlagsRentalBillingRunMissingRunIdAsInfo()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Missing run id customer",
            NameMatchKey = "MISSINGRUNIDCUSTOMER",
            TradeType = "매출"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Missing run id customer",
            MonthlyAmount = 100_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = Guid.Empty,
                    RunKey = "legacy-2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "완료",
                    BilledAmount = 100_000m,
                    SettledAmount = 100_000m,
                    SettlementStatus = "입금확인",
                    SettledDate = new DateOnly(2026, 6, 26)
                }
            })
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issue = Assert.Single(payload.Issues, current => current.Code == "rental_billing_run_missing_run_id");

        Assert.Equal("Info", issue.Severity);
        Assert.Equal(1, issue.Count);
        Assert.DoesNotContain(payload.Issues, current => current.Code == "rental_billing_run_settlement_mismatch");
        Assert.DoesNotContain(payload.Issues, current => current.Code == "rental_billing_profile_summary_mismatch");

        var row = await GetSingleDetailRowAsync(controller, "rental_billing_run_missing_run_id");

        Assert.Equal(FormatGuidForTest(profileId), row.EntityIdText);
        Assert.Equal("RunId 없음", row.ReferenceText);
        Assert.Contains("legacy-2026-06", row.PrimaryText, StringComparison.Ordinal);
        Assert.Contains("100,000", row.SecondaryText, StringComparison.Ordinal);
        Assert.Contains("전표/수금과 안정적으로 대조할 수 없는 과거 청구 JSON", row.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReport_FlagsRestoredRentalInvoiceWithDeletedPaymentAndDetachedTransaction()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Detached rental invoice customer",
            NameMatchKey = "DETACHEDRENTALINVOICECUSTOMER",
            TradeType = "매출"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Detached rental invoice customer",
            MonthlyAmount = 100_000m
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "RENTAL-DETACHED-001",
            InvoiceDate = new DateOnly(2026, 6, 18),
            VoucherType = VoucherType.Sales,
            TotalAmount = 100_000m,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            IsDeleted = false
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 19),
            Amount = 100_000m,
            Note = "deleted payment after incomplete invoice restore",
            IsDeleted = true
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = paymentId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 19),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = null,
            LinkedInvoiceNumber = string.Empty,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 100_000m,
            ReceiptTotal = 100_000m,
            SettlementAmount = 0m,
            IsDeleted = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issue = Assert.Single(payload.Issues, current => current.Code == "rental_invoice_deleted_payment_detached_transaction");

        Assert.Equal("Error", issue.Severity);
        Assert.Equal(1, issue.Count);

        var detailsResponse = await controller.GetReportDetails("rental_invoice_deleted_payment_detached_transaction", CancellationToken.None);
        var detailsOk = Assert.IsType<OkObjectResult>(detailsResponse.Result);
        var details = Assert.IsType<IntegrityIssueDetailResultDto>(detailsOk.Value);
        var row = Assert.Single(details.Rows);

        Assert.Equal(FormatGuidForTest(paymentId), row.EntityIdText);
        Assert.Contains(FormatGuidForTest(invoiceId), row.DetailText, StringComparison.Ordinal);
        Assert.Contains(FormatGuidForTest(paymentId), row.DetailText, StringComparison.Ordinal);
        Assert.Contains("거래내역 전표링크 없음", row.ReferenceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReport_FlagsDeletedPaymentResiduesWithHardMissingParents()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var missingInvoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var missingPaymentId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();

        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = missingInvoiceId,
            PaymentDate = new DateOnly(2026, 6, 18),
            Amount = 45000m,
            Note = "hard missing invoice",
            IsDeleted = true
        });
        dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = attachmentId,
            PaymentId = missingPaymentId,
            FileName = "deleted-payment-attachment.pdf",
            StoragePath = "attachments/payment/deleted-payment-attachment.pdf",
            FileSize = 512,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var paymentIssue = Assert.Single(payload.Issues, issue => issue.Code == "deleted_payment_missing_invoice_rows");
        var attachmentIssue = Assert.Single(payload.Issues, issue => issue.Code == "deleted_payment_attachment_missing_payment_rows");

        Assert.Equal("Error", paymentIssue.Severity);
        Assert.Equal(1, paymentIssue.Count);
        Assert.Equal("Error", attachmentIssue.Severity);
        Assert.Equal(1, attachmentIssue.Count);

        var paymentDetailsResponse = await controller.GetReportDetails("deleted_payment_missing_invoice_rows", CancellationToken.None);
        var paymentDetailsOk = Assert.IsType<OkObjectResult>(paymentDetailsResponse.Result);
        var paymentDetails = Assert.IsType<IntegrityIssueDetailResultDto>(paymentDetailsOk.Value);
        var paymentRow = Assert.Single(paymentDetails.Rows);

        Assert.Equal("삭제 결제", paymentRow.EntityType);
        Assert.Equal(FormatGuidForTest(paymentId), paymentRow.EntityIdText);
        Assert.Contains(FormatGuidForTest(missingInvoiceId), paymentRow.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("삭제상태 삭제", paymentRow.DetailText, StringComparison.Ordinal);

        var attachmentDetailsResponse = await controller.GetReportDetails("deleted_payment_attachment_missing_payment_rows", CancellationToken.None);
        var attachmentDetailsOk = Assert.IsType<OkObjectResult>(attachmentDetailsResponse.Result);
        var attachmentDetails = Assert.IsType<IntegrityIssueDetailResultDto>(attachmentDetailsOk.Value);
        var attachmentRow = Assert.Single(attachmentDetails.Rows);

        Assert.Equal("삭제 결제첨부", attachmentRow.EntityType);
        Assert.Equal(FormatGuidForTest(attachmentId), attachmentRow.EntityIdText);
        Assert.Contains(FormatGuidForTest(missingPaymentId), attachmentRow.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("삭제상태 삭제", attachmentRow.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReport_FlagsInvoiceLinkedTransactionWithoutMatchingPaymentRow()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Transaction payment mismatch customer",
            NameMatchKey = "TRANSACTIONPAYMENTMISMATCHCUSTOMER",
            TradeType = "매출"
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAY-MISMATCH-001",
            InvoiceDate = new DateOnly(2026, 6, 19),
            VoucherType = VoucherType.Sales,
            TotalAmount = 100_000m,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            IsDeleted = false
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 20),
            TransactionKind = "수금",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "PAY-MISMATCH-001",
            BankReceipt = 60_000m,
            ReceiptTotal = 60_000m,
            SettlementAmount = 60_000m,
            IsDeleted = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issue = Assert.Single(payload.Issues, current => current.Code == "invoice_linked_transaction_payment_mismatch");

        Assert.Equal("Error", issue.Severity);
        Assert.Equal(1, issue.Count);

        var row = await GetSingleDetailRowAsync(controller, "invoice_linked_transaction_payment_mismatch");

        Assert.Equal(FormatGuidForTest(transactionId), row.EntityIdText);
        Assert.Contains("수금·지급 행 없음", row.ReferenceText, StringComparison.Ordinal);
        Assert.Contains(FormatGuidForTest(invoiceId), row.DetailText, StringComparison.Ordinal);
        Assert.Contains("거래 정산 60,000", row.SecondaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReport_FlagsAttachmentsWithFileSizeButNoReadableContent()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "파일 누락 거래처",
            NameMatchKey = "FILEMISSINGCUSTOMER",
            TradeType = "매출"
        });
        dbContext.CustomerContracts.Add(new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ContractType = "거래계약서",
            FileName = "contract-missing.pdf",
            FileSize = 12,
            StoragePath = string.Empty,
            FileContent = []
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 12),
            TransactionKind = "수금"
        });
        dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            FileName = "transaction-missing.pdf",
            FileSize = 24,
            StoragePath = string.Empty,
            FileContent = []
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-FILE-MISSING",
            InvoiceDate = new DateOnly(2026, 6, 12)
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 12),
            Amount = 30000m
        });
        dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = paymentId,
            FileName = "payment-missing.pdf",
            FileSize = 36,
            StoragePath = string.Empty,
            FileContent = []
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issue = Assert.Single(payload.Issues, current => current.Code == "file_content_unavailable");

        Assert.Equal("Error", issue.Severity);
        Assert.Equal(3, issue.Count);
        Assert.DoesNotContain(payload.Issues, current => current.Code == "file_content_db_residue");

        var detailsResponse = await controller.GetReportDetails("file_content_unavailable", CancellationToken.None);
        var detailsOk = Assert.IsType<OkObjectResult>(detailsResponse.Result);
        var details = Assert.IsType<IntegrityIssueDetailResultDto>(detailsOk.Value);

        Assert.Equal(3, details.DetailCount);
        Assert.All(details.Rows, row =>
        {
            Assert.Contains("StoragePath 비어 있음", row.DetailText, StringComparison.Ordinal);
            Assert.Contains("DB FileContent 0 bytes", row.DetailText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task GetReport_FlagsDbFileContentResidueAfterStorageMigration()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DB 본문 잔류 거래처",
            NameMatchKey = "DBRESIDUECUSTOMER",
            TradeType = "매출"
        });
        dbContext.CustomerContracts.Add(new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ContractType = "거래계약서",
            FileName = "contract-residue.pdf",
            FileSize = 4,
            StoragePath = "contracts/contract-residue.pdf",
            FileContent = [1, 2, 3, 4]
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 13),
            TransactionKind = "수금"
        });
        dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            FileName = "transaction-residue.pdf",
            FileSize = 3,
            StoragePath = "attachments/transaction-residue.pdf",
            FileContent = [5, 6, 7]
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-FILE-RESIDUE",
            InvoiceDate = new DateOnly(2026, 6, 13)
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 13),
            Amount = 40000m
        });
        dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = paymentId,
            FileName = "payment-residue.pdf",
            FileSize = 2,
            StoragePath = "attachments/payment-residue.pdf",
            FileContent = [8, 9]
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issue = Assert.Single(payload.Issues, current => current.Code == "file_content_db_residue");

        Assert.Equal("Warning", issue.Severity);
        Assert.Equal(3, issue.Count);
        Assert.DoesNotContain(payload.Issues, current => current.Code == "file_content_unavailable");

        var detailsResponse = await controller.GetReportDetails("file_content_db_residue", CancellationToken.None);
        var detailsOk = Assert.IsType<OkObjectResult>(detailsResponse.Result);
        var details = Assert.IsType<IntegrityIssueDetailResultDto>(detailsOk.Value);

        Assert.Equal(3, details.DetailCount);
        Assert.Contains(details.Rows, row => row.PrimaryText == "contract-residue.pdf" && row.DetailText.Contains("DB FileContent 4 bytes", StringComparison.Ordinal));
        Assert.Contains(details.Rows, row => row.PrimaryText == "transaction-residue.pdf" && row.DetailText.Contains("DB FileContent 3 bytes", StringComparison.Ordinal));
        Assert.Contains(details.Rows, row => row.PrimaryText == "payment-residue.pdf" && row.DetailText.Contains("DB FileContent 2 bytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetReport_FlagsStoredFilesMissingFromCentralStorage()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "저장파일 누락 거래처",
            NameMatchKey = "STORAGEFILEMISSINGCUSTOMER",
            TradeType = "매출"
        });
        dbContext.CustomerContracts.Add(new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ContractType = "거래계약서",
            FileName = "contract-storage-missing.pdf",
            FileSize = 12,
            StoragePath = Path.Combine(_fileStorageRoot, "missing-contract.pdf"),
            FileContent = []
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 14),
            TransactionKind = "수금"
        });
        dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            FileName = "transaction-storage-missing.pdf",
            FileSize = 24,
            StoragePath = Path.Combine(_fileStorageRoot, "missing-transaction.pdf"),
            FileContent = []
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-STORAGE-MISSING",
            InvoiceDate = new DateOnly(2026, 6, 14)
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 14),
            Amount = 50000m
        });
        dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = paymentId,
            FileName = "payment-storage-missing.pdf",
            FileSize = 36,
            StoragePath = Path.Combine(_fileStorageRoot, "missing-payment.pdf"),
            FileContent = []
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var issue = Assert.Single(payload.Issues, current => current.Code == "file_storage_missing");

        Assert.Equal("Error", issue.Severity);
        Assert.Equal(3, issue.Count);
        Assert.DoesNotContain(payload.Issues, current => current.Code == "file_content_unavailable");

        var detailsResponse = await controller.GetReportDetails("file_storage_missing", CancellationToken.None);
        var detailsOk = Assert.IsType<OkObjectResult>(detailsResponse.Result);
        var details = Assert.IsType<IntegrityIssueDetailResultDto>(detailsOk.Value);

        Assert.Equal(3, details.DetailCount);
        Assert.All(details.Rows, row =>
        {
            Assert.Contains("저장파일 없음", row.DetailText, StringComparison.Ordinal);
            Assert.Contains("stored_file_not_found", row.DetailText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task GetReport_FlagsStoredFileSizeAndHashMismatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var storedBytes = new byte[] { 1, 2, 3 };
        var actualHash = Convert.ToHexString(SHA256.HashData(storedBytes));
        var wrongHash = Convert.ToHexString(SHA256.HashData(new byte[] { 9, 9, 9 }));
        var storedPath = await CreateFileStorage().SaveBytesAsync(
            "customer-contracts",
            customerId.ToString("N"),
            contractId,
            "contract-storage-mismatch.pdf",
            storedBytes);

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "저장파일 불일치 거래처",
            NameMatchKey = "STORAGEFILEMISMATCHCUSTOMER",
            TradeType = "매출"
        });
        dbContext.CustomerContracts.Add(new CustomerContract
        {
            Id = contractId,
            CustomerId = customerId,
            ContractType = "거래계약서",
            FileName = "contract-storage-mismatch.pdf",
            FileSize = 999,
            FileHash = wrongHash,
            StoragePath = storedPath,
            FileContent = []
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetReport(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityReportDto>(ok.Value);
        var sizeIssue = Assert.Single(payload.Issues, current => current.Code == "file_storage_size_mismatch");
        var hashIssue = Assert.Single(payload.Issues, current => current.Code == "file_storage_hash_mismatch");

        Assert.Equal("Error", sizeIssue.Severity);
        Assert.Equal(1, sizeIssue.Count);
        Assert.Equal("Error", hashIssue.Severity);
        Assert.Equal(1, hashIssue.Count);

        var sizeDetailsResponse = await controller.GetReportDetails("file_storage_size_mismatch", CancellationToken.None);
        var sizeDetailsOk = Assert.IsType<OkObjectResult>(sizeDetailsResponse.Result);
        var sizeDetails = Assert.IsType<IntegrityIssueDetailResultDto>(sizeDetailsOk.Value);
        var sizeRow = Assert.Single(sizeDetails.Rows);

        Assert.Contains("FileSize 999 bytes", sizeRow.DetailText, StringComparison.Ordinal);
        Assert.Contains("저장파일 크기 3 bytes", sizeRow.DetailText, StringComparison.Ordinal);

        var hashDetailsResponse = await controller.GetReportDetails("file_storage_hash_mismatch", CancellationToken.None);
        var hashDetailsOk = Assert.IsType<OkObjectResult>(hashDetailsResponse.Result);
        var hashDetails = Assert.IsType<IntegrityIssueDetailResultDto>(hashDetailsOk.Value);
        var hashRow = Assert.Single(hashDetails.Rows);

        Assert.Contains($"FileHash {wrongHash}", hashRow.DetailText, StringComparison.Ordinal);
        Assert.Contains($"저장파일 SHA256 {actualHash}", hashRow.DetailText, StringComparison.Ordinal);
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

        var controller = CreateController(dbContext, currentUser);

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

        var controller = CreateController(dbContext, currentUser);

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

        var controller = CreateController(dbContext, currentUser);

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

        var controller = CreateController(dbContext, currentUser);

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

        var controller = CreateController(dbContext, currentUser);

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

        var controller = CreateController(dbContext, currentUser);

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

        var controller = CreateController(dbContext, currentUser);

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

        var controller = CreateController(dbContext, currentUser);

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

        var controller = CreateController(dbContext, currentUser);

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

        var controller = CreateController(dbContext, currentUser);

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

        var controller = CreateController(dbContext, currentUser);

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

        var controller = CreateController(dbContext, currentUser);

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

        var controller = CreateController(dbContext, currentUser);

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
        if (Directory.Exists(_fileStorageRoot))
            Directory.Delete(_fileStorageRoot, recursive: true);
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

    private IntegrityController CreateController(AppDbContext dbContext, TestCurrentUserContext currentUser)
        => new(dbContext, new OfficeScopeService(currentUser, dbContext), CreateFileStorage());

    private CentralFileStorage CreateFileStorage()
        => new(
            Options.Create(new CentralFileStorageOptions { RootPath = _fileStorageRoot }),
            new TestHostEnvironment());

    private static async Task<IntegrityIssueDetailRowDto> GetSingleDetailRowAsync(IntegrityController controller, string code)
    {
        var response = await controller.GetReportDetails(code, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<IntegrityIssueDetailResultDto>(ok.Value);
        return Assert.Single(payload.Rows);
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

    private static TestCurrentUserContext CreateOfficeScopedUser()
        => new()
        {
            Username = "office-user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsAdmin = false
        };

    private static string FormatGuidForTest(Guid value)
        => value.ToString("D");

    private sealed class ServerRentalBillingRunSnapshot
    {
        public Guid RunId { get; set; }
        public string RunKey { get; set; } = string.Empty;
        public DateOnly ScheduledDate { get; set; }
        public DateOnly PeriodStartDate { get; set; }
        public DateOnly PeriodEndDate { get; set; }
        public string PeriodLabel { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal BilledAmount { get; set; }
        public decimal SettledAmount { get; set; }
        public string SettlementStatus { get; set; } = string.Empty;
        public DateOnly? SettledDate { get; set; }
    }

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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "GeoraePlan.Server.Api.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
