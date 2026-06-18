using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegrityIssueServicePerformanceTests
{
    [Fact]
    public async Task ScanAsync_UsesPreGroupedLinkedAssetsForManyRentalProfiles()
    {
        PrepareAppRoot("georaeplan-integrity-linked-assets-grouping");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int profileCount = 650;
            for (var index = 0; index < profileCount; index++)
            {
                var profileId = Guid.NewGuid();
                db.RentalBillingProfiles.Add(CreateProfile(profileId, index));
                db.RentalAssets.Add(CreateLinkedAsset(profileId, index));
            }

            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());

            Assert.DoesNotContain(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalProfileWithoutLinkedAssets);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_UsesItemNameLookupForManyInventoryReferenceIssues()
    {
        PrepareAppRoot("georaeplan-integrity-inventory-item-lookup");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int itemCount = 650;
            for (var index = 0; index < itemCount; index++)
            {
                var item = CreateInventoryItem(index);
                db.Items.Add(item);
                db.ItemWarehouseStocks.Add(CreateStockWithMissingWarehouse(item.Id));
                db.InventoryMovements.Add(CreateMovementWithMissingWarehouse(item.Id));
            }

            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());
            var missingWarehouseIssues = result.Issues
                .Where(issue => issue.Code == DataIntegrityIssueCodes.InventoryWarehouseReferenceMissing)
                .ToList();

            Assert.Equal(itemCount * 2, missingWarehouseIssues.Count);
            Assert.All(missingWarehouseIssues, issue => Assert.StartsWith("Inventory Item ", issue.ItemName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_LoadsInventorySourcesOnlyForScopedItems()
    {
        PrepareAppRoot("georaeplan-integrity-inventory-source-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int outsideOfficeItemCount = 650;
            for (var index = 0; index < outsideOfficeItemCount; index++)
            {
                var item = CreateInventoryItem(index, OfficeCodeCatalog.Usenet);
                db.Items.Add(item);
                db.ItemWarehouseStocks.Add(CreateStockWithMissingWarehouse(item.Id));
                db.InventoryMovements.Add(CreateMovementWithMissingWarehouse(item.Id));
            }

            var scopedItem = CreateInventoryItem(outsideOfficeItemCount + 1, OfficeCodeCatalog.Yeonsu);
            db.Items.Add(scopedItem);
            db.ItemWarehouseStocks.Add(CreateStockWithMissingWarehouse(scopedItem.Id));
            db.InventoryMovements.Add(CreateMovementWithMissingWarehouse(scopedItem.Id));
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateYeonsuAdminSession());
            var missingWarehouseIssues = result.Issues
                .Where(issue => issue.Code == DataIntegrityIssueCodes.InventoryWarehouseReferenceMissing)
                .ToList();

            Assert.Equal(2, missingWarehouseIssues.Count);
            Assert.All(missingWarehouseIssues, issue => Assert.Equal(scopedItem.NameOriginal, issue.ItemName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_FindsDeletedItemStockResidueOnlyInsideSessionScope()
    {
        PrepareAppRoot("georaeplan-integrity-deleted-item-stock-residue");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var outsideDeletedItem = CreateInventoryItem(701, OfficeCodeCatalog.Usenet);
            outsideDeletedItem.IsDeleted = true;
            outsideDeletedItem.CurrentStock = 7m;
            var scopedDeletedItem = CreateInventoryItem(702, OfficeCodeCatalog.Yeonsu);
            scopedDeletedItem.IsDeleted = true;
            scopedDeletedItem.CurrentStock = 5m;
            db.Items.AddRange(outsideDeletedItem, scopedDeletedItem);
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = outsideDeletedItem.Id,
                WarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Usenet),
                Quantity = 7m,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = scopedDeletedItem.Id,
                WarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Yeonsu),
                Quantity = 5m,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateYeonsuAdminSession());
            var residueIssue = Assert.Single(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.InventoryDeletedItemStockResidue);

            Assert.Equal(scopedDeletedItem.Id, residueIssue.EntityId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, residueIssue.OfficeCode);
            Assert.Equal(scopedDeletedItem.NameOriginal, residueIssue.ItemName);
            Assert.Contains("창고행 1", residueIssue.CurrentValue, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_PrefiltersInvoiceSourceLoadByOperationalScope()
    {
        PrepareAppRoot("georaeplan-integrity-invoice-scope-prefilter");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int outsideOfficeInvoiceCount = 650;
            for (var index = 0; index < outsideOfficeInvoiceCount; index++)
                db.Invoices.Add(CreateMismatchedInvoice(index, OfficeCodeCatalog.Usenet));
            db.Invoices.Add(CreateMismatchedInvoice(outsideOfficeInvoiceCount + 1, OfficeCodeCatalog.Yeonsu));
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateYeonsuAdminSession());
            var invoiceIssues = result.Issues
                .Where(issue => issue.Code == DataIntegrityIssueCodes.InvoiceAmountMismatch)
                .ToList();

            var issue = Assert.Single(invoiceIssues);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, issue.OfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_LoadsInvoiceLineAndPaymentTotalsInBatches()
    {
        PrepareAppRoot("georaeplan-integrity-invoice-aggregate-batch-load");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int balancedInvoiceCount = 1_100;
            for (var index = 0; index < balancedInvoiceCount; index++)
                db.Invoices.Add(CreateInvoiceWithAggregates(index, OfficeCodeCatalog.Yeonsu, lineAmount: 100m, invoiceTotal: 100m, paymentAmount: 100m));

            var problemInvoice = CreateInvoiceWithAggregates(
                balancedInvoiceCount + 1,
                OfficeCodeCatalog.Yeonsu,
                lineAmount: 120m,
                invoiceTotal: 100m,
                paymentAmount: 150m);
            db.Invoices.Add(problemInvoice);
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateYeonsuAdminSession());
            var invoiceIssues = result.Issues
                .Where(issue =>
                    issue.Code == DataIntegrityIssueCodes.InvoiceAmountMismatch ||
                    issue.Code == DataIntegrityIssueCodes.InvoiceOverSettled)
                .ToList();

            Assert.Equal(2, invoiceIssues.Count);
            Assert.Contains(invoiceIssues, issue =>
                issue.Code == DataIntegrityIssueCodes.InvoiceAmountMismatch &&
                issue.EntityId == problemInvoice.Id);
            Assert.Contains(invoiceIssues, issue =>
                issue.Code == DataIntegrityIssueCodes.InvoiceOverSettled &&
                issue.EntityId == problemInvoice.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_FindsInvoiceLineRowsWhoseInvoiceRowIsHardMissing()
    {
        PrepareAppRoot("georaeplan-integrity-invoice-line-missing-invoice");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var missingInvoiceId = Guid.NewGuid();
            var lineId = Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            db.InvoiceLines.Add(new LocalInvoiceLine
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
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());
            var issue = Assert.Single(result.Issues, current =>
                current.Code == DataIntegrityIssueCodes.InvoiceLineMissingInvoiceReference);

            Assert.Equal(lineId, issue.EntityId);
            Assert.Equal("Hard Missing Invoice Line", issue.ItemName);
            Assert.Equal(DataIntegrityDirectActionKind.OpenSyncDiagnostics, issue.DirectActionKind);
            Assert.Contains(missingInvoiceId.ToString("D"), issue.CurrentValue, StringComparison.Ordinal);
            Assert.Contains("삭제상태 삭제", issue.CurrentValue, StringComparison.Ordinal);
            Assert.Contains("전표 참조", issue.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_FindsPaymentRowsWhoseInvoiceRowIsHardMissing()
    {
        PrepareAppRoot("georaeplan-integrity-payment-missing-invoice");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var missingInvoiceId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = missingInvoiceId,
                PaymentDate = new DateOnly(2026, 6, 18),
                Amount = 23000m,
                Note = "hard missing invoice",
                IsDeleted = true,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());
            var issue = Assert.Single(result.Issues, current =>
                current.Code == DataIntegrityIssueCodes.PaymentMissingInvoiceReference);

            Assert.Equal(paymentId, issue.EntityId);
            Assert.Equal(DataIntegrityDirectActionKind.OpenSyncDiagnostics, issue.DirectActionKind);
            Assert.Contains(missingInvoiceId.ToString("D"), issue.CurrentValue, StringComparison.Ordinal);
            Assert.Contains("삭제상태 삭제", issue.CurrentValue, StringComparison.Ordinal);
            Assert.Contains("전표 참조", issue.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }


    [Fact]
    public async Task ScanAsync_FindsRentalBillingRunSettlementMismatch()
    {
        PrepareAppRoot("georaeplan-integrity-rental-run-settlement-mismatch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();

            db.Customers.Add(CreateCustomer(customerId, OfficeCodeCatalog.Usenet, "Run settlement mismatch customer"));
            var profile = CreateProfile(profileId, 9401, OfficeCodeCatalog.Usenet, customerId, "Run settlement mismatch customer");
            profile.BillingRunsJson = JsonSerializer.Serialize(new List<RentalBillingRunModel>
            {
                new()
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = PaymentFlowConstants.BillingStatusCompleted,
                    BilledAmount = 100_000m,
                    SettledAmount = 100_000m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusConfirmed,
                    SettledDate = new DateOnly(2026, 6, 26)
                }
            });
            db.RentalBillingProfiles.Add(profile);
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 26),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                BankReceipt = 60_000m,
                ReceiptTotal = 60_000m,
                SettlementAmount = 60_000m,
                IsDeleted = false,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());
            var issue = Assert.Single(result.Issues, current =>
                current.Code == DataIntegrityIssueCodes.RentalBillingRunSettlementMismatch);

            Assert.Equal(profileId, issue.EntityId);
            Assert.Equal("Error", issue.Severity);
            Assert.Equal(DataIntegrityDirectActionKind.OpenRentalBillingProfile, issue.DirectActionKind);
            Assert.Contains(runId.ToString("D"), issue.ReviewInfo, StringComparison.Ordinal);
            Assert.Contains("저장 정산 100,000", issue.CurrentValue, StringComparison.Ordinal);
            Assert.Contains("실제 60,000", issue.CurrentValue, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_FindsRentalBillingProfileSummaryMismatch()
    {
        PrepareAppRoot("georaeplan-integrity-rental-profile-summary-mismatch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();

            db.Customers.Add(CreateCustomer(customerId, OfficeCodeCatalog.Usenet, "Profile summary mismatch customer"));
            var profile = CreateProfile(profileId, 9402, OfficeCodeCatalog.Usenet, customerId, "Profile summary mismatch customer");
            profile.BillingStatus = PaymentFlowConstants.BillingStatusInProgress;
            profile.SettlementStatus = PaymentFlowConstants.SettlementStatusPending;
            profile.CompletionStatus = PaymentFlowConstants.CompletionPending;
            profile.SettledAmount = 0m;
            profile.OutstandingAmount = 100_000m;
            profile.BillingRunsJson = JsonSerializer.Serialize(new List<RentalBillingRunModel>
            {
                new()
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = PaymentFlowConstants.BillingStatusCompleted,
                    BilledAmount = 100_000m,
                    SettledAmount = 100_000m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusConfirmed,
                    SettledDate = new DateOnly(2026, 6, 26)
                }
            });
            db.RentalBillingProfiles.Add(profile);
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 26),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                BankReceipt = 100_000m,
                ReceiptTotal = 100_000m,
                SettlementAmount = 100_000m,
                IsDeleted = false,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());
            var issue = Assert.Single(result.Issues, current =>
                current.Code == DataIntegrityIssueCodes.RentalBillingProfileSummaryMismatch);

            Assert.Equal(profileId, issue.EntityId);
            Assert.Equal("Error", issue.Severity);
            Assert.Equal(DataIntegrityDirectActionKind.OpenRentalBillingProfile, issue.DirectActionKind);
            Assert.Contains(runId.ToString("D"), issue.ReviewInfo, StringComparison.Ordinal);
            Assert.Contains("프로필 저장 정산 0", issue.CurrentValue, StringComparison.Ordinal);
            Assert.Contains("대표 run 실제 정산 100,000", issue.ExpectedValue, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Issues, current =>
                current.Code == DataIntegrityIssueCodes.RentalBillingRunSettlementMismatch);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_FindsRestoredRentalInvoiceWithDeletedPaymentAndDetachedTransaction()
    {
        PrepareAppRoot("georaeplan-integrity-rental-invoice-detached-payment");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();

            db.Customers.Add(CreateCustomer(customerId, OfficeCodeCatalog.Usenet, "Detached local rental customer"));
            db.RentalBillingProfiles.Add(CreateProfile(profileId, 9301, OfficeCodeCatalog.Usenet, customerId, "Detached local rental customer"));
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                InvoiceNumber = "LOCAL-RENTAL-DETACHED-001",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 18),
                TotalAmount = 100_000m,
                SupplyAmount = 90_909m,
                VatAmount = 9_091m,
                VatMode = InvoiceVatModes.Included,
                IsLatestVersion = true,
                IsDeleted = false,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 19),
                Amount = 100_000m,
                Note = "deleted local payment after incomplete invoice restore",
                IsDeleted = true,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = paymentId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 19),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedInvoiceId = null,
                LinkedInvoiceNumber = string.Empty,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                BankReceipt = 100_000m,
                ReceiptTotal = 100_000m,
                SettlementAmount = 0m,
                IsDeleted = false,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());
            var issue = Assert.Single(result.Issues, current =>
                current.Code == DataIntegrityIssueCodes.RentalInvoiceDeletedPaymentDetachedTransaction);

            Assert.Equal(paymentId, issue.EntityId);
            Assert.Equal("Error", issue.Severity);
            Assert.Equal(DataIntegrityDirectActionKind.OpenPaymentForInvoice, issue.DirectActionKind);
            Assert.Contains(invoiceId.ToString("D"), issue.ReviewInfo, StringComparison.Ordinal);
            Assert.Contains(paymentId.ToString("D"), issue.ReviewInfo, StringComparison.Ordinal);
            Assert.Contains("거래 전표링크 없음", issue.CurrentValue, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_FindsRemainingChildRowsWhoseParentRowsAreHardMissing()
    {
        PrepareAppRoot("georaeplan-integrity-remaining-child-parent-missing");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var missingCustomerId = Guid.NewGuid();
            var customerContractId = Guid.NewGuid();
            var missingTransactionId = Guid.NewGuid();
            var transactionAttachmentId = Guid.NewGuid();
            var missingBillingProfileId = Guid.NewGuid();
            var rentalBillingLogId = Guid.NewGuid();
            var missingTransferId = Guid.NewGuid();
            var transferLineId = Guid.NewGuid();

            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            db.CustomerContracts.Add(new LocalCustomerContract
            {
                Id = customerContractId,
                CustomerId = missingCustomerId,
                ContractType = "contract",
                FileName = "missing-customer-contract.pdf",
                FileSize = 512,
                IsDeleted = false,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = transactionAttachmentId,
                TransactionId = missingTransactionId,
                AttachmentType = "evidence",
                FileName = "missing-transaction-attachment.pdf",
                FileSize = 256,
                IsDeleted = true,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.RentalBillingLogs.Add(new LocalRentalBillingLog
            {
                Id = rentalBillingLogId,
                BillingProfileId = missingBillingProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingYearMonth = "2026-06",
                ScheduledDate = new DateOnly(2026, 6, 25),
                Status = "scheduled",
                BilledAmount = 77000m,
                IsDeleted = false,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.InventoryTransferLines.Add(new LocalInventoryTransferLine
            {
                Id = transferLineId,
                TransferId = missingTransferId,
                ItemNameOriginal = "Missing Transfer Item",
                SpecificationOriginal = "A4",
                Unit = "EA",
                Quantity = 3m,
                ReceivedQuantity = 1m,
                QuantityDifference = -2m,
                Remark = "missing transfer",
                IsDeleted = false
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());
            var issuesByCode = result.Issues.ToLookup(issue => issue.Code, StringComparer.OrdinalIgnoreCase);

            var customerContractIssue = Assert.Single(issuesByCode[DataIntegrityIssueCodes.CustomerContractMissingCustomerReference]);
            Assert.Equal(customerContractId, customerContractIssue.EntityId);
            Assert.Contains(missingCustomerId.ToString("D"), customerContractIssue.CurrentValue, StringComparison.Ordinal);
            Assert.Contains("missing-customer-contract.pdf", customerContractIssue.CurrentValue, StringComparison.Ordinal);
            Assert.Equal(DataIntegrityDirectActionKind.OpenSyncDiagnostics, customerContractIssue.DirectActionKind);

            var transactionAttachmentIssue = Assert.Single(issuesByCode[DataIntegrityIssueCodes.TransactionAttachmentMissingTransactionReference]);
            Assert.Equal(transactionAttachmentId, transactionAttachmentIssue.EntityId);
            Assert.Contains(missingTransactionId.ToString("D"), transactionAttachmentIssue.CurrentValue, StringComparison.Ordinal);
            Assert.Contains("missing-transaction-attachment.pdf", transactionAttachmentIssue.CurrentValue, StringComparison.Ordinal);
            Assert.Equal(DataIntegrityDirectActionKind.OpenSyncDiagnostics, transactionAttachmentIssue.DirectActionKind);

            var rentalBillingLogIssue = Assert.Single(issuesByCode[DataIntegrityIssueCodes.RentalBillingLogMissingProfileReference]);
            Assert.Equal(rentalBillingLogId, rentalBillingLogIssue.EntityId);
            Assert.Contains(missingBillingProfileId.ToString("D"), rentalBillingLogIssue.CurrentValue, StringComparison.Ordinal);
            Assert.Contains("2026-06", rentalBillingLogIssue.CurrentValue, StringComparison.Ordinal);
            Assert.Equal(DataIntegrityDirectActionKind.OpenSyncDiagnostics, rentalBillingLogIssue.DirectActionKind);

            var transferLineIssue = Assert.Single(issuesByCode[DataIntegrityIssueCodes.InventoryTransferLineMissingTransferReference]);
            Assert.Equal(transferLineId, transferLineIssue.EntityId);
            Assert.Equal("Missing Transfer Item", transferLineIssue.ItemName);
            Assert.Contains(missingTransferId.ToString("D"), transferLineIssue.CurrentValue, StringComparison.Ordinal);
            Assert.Contains("3.00", transferLineIssue.CurrentValue, StringComparison.Ordinal);
            Assert.Equal(DataIntegrityDirectActionKind.OpenSyncDiagnostics, transferLineIssue.DirectActionKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_ClassifiesPastRentalAssignmentStaleReferencesAsInfo()
    {
        PrepareAppRoot("georaeplan-integrity-past-rental-assignment-stale-reference");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var activeCustomerId = Guid.NewGuid();
            var activeProfileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var missingCustomerId = Guid.NewGuid();
            var missingProfileId = Guid.NewGuid();
            var historyId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(activeCustomerId, OfficeCodeCatalog.Usenet, "Active Rental Customer"));
            db.RentalBillingProfiles.Add(CreateProfile(activeProfileId, 9101, OfficeCodeCatalog.Usenet, activeCustomerId, "Active Rental Customer"));
            db.RentalAssets.Add(CreateLinkedAsset(activeProfileId, 9101, OfficeCodeCatalog.Usenet, activeCustomerId, "Active Rental Customer", assetId));
            db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
            {
                Id = historyId,
                AssetId = assetId,
                CustomerId = missingCustomerId,
                BillingProfileId = missingProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Past Snapshot Customer",
                InstallLocation = "Past Site",
                BillingProfileDisplay = "Past Snapshot Profile",
                ItemName = "Past Copier",
                MachineNumber = "PAST-SN",
                ManagementNumber = "PAST-HIST",
                MonthlyFee = 100_000m,
                IsCurrent = false,
                LinkedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UnlinkedAtUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                IsDeleted = false,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());

            Assert.DoesNotContain(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalAssignmentMissingReference &&
                issue.EntityId == historyId);
            var staleIssue = Assert.Single(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalAssignmentHistoricalStaleReference &&
                issue.EntityId == historyId);

            Assert.Equal("Info", staleIssue.Severity);
            Assert.Contains(missingCustomerId.ToString("D"), staleIssue.CurrentValue, StringComparison.Ordinal);
            Assert.Contains(missingProfileId.ToString("D"), staleIssue.CurrentValue, StringComparison.Ordinal);
            Assert.Equal(DataIntegrityDirectActionKind.OpenRentalAsset, staleIssue.DirectActionKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_PrefiltersMasterDataSourceLoadByOperationalScope()
    {
        PrepareAppRoot("georaeplan-integrity-master-scope-prefilter");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int outsideOfficeCount = 650;
            for (var index = 0; index < outsideOfficeCount; index++)
            {
                db.Customers.Add(CreateDuplicateCustomer(index, OfficeCodeCatalog.Usenet, "Outside Duplicate Customer"));
                db.Items.Add(CreateDuplicateItem(index, OfficeCodeCatalog.Usenet, "Outside Duplicate Item", "Outside Duplicate Spec"));
                db.Warehouses.Add(CreateDuplicateWarehouse(index, OfficeCodeCatalog.Usenet, $"OUTSIDE-DUPLICATE-{index:D4}", "Outside Duplicate Warehouse"));
            }

            db.Customers.Add(CreateDuplicateCustomer(outsideOfficeCount + 1, OfficeCodeCatalog.Yeonsu, "Scoped Duplicate Customer"));
            db.Customers.Add(CreateDuplicateCustomer(outsideOfficeCount + 2, OfficeCodeCatalog.Yeonsu, "Scoped Duplicate Customer"));
            db.Items.Add(CreateDuplicateItem(outsideOfficeCount + 1, OfficeCodeCatalog.Yeonsu, "Scoped Duplicate Item", "Scoped Duplicate Spec"));
            db.Items.Add(CreateDuplicateItem(outsideOfficeCount + 2, OfficeCodeCatalog.Yeonsu, "Scoped Duplicate Item", "Scoped Duplicate Spec"));
            db.Warehouses.Add(CreateDuplicateWarehouse(outsideOfficeCount + 1, OfficeCodeCatalog.Yeonsu, "SCOPED-DUPLICATE-0001", "Scoped Duplicate Warehouse"));
            db.Warehouses.Add(CreateDuplicateWarehouse(outsideOfficeCount + 2, OfficeCodeCatalog.Yeonsu, "SCOPED-DUPLICATE-0002", "Scoped Duplicate Warehouse"));
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateYeonsuAdminSession());
            var masterIssues = result.Issues
                .Where(issue =>
                    issue.Code == DataIntegrityIssueCodes.CustomerDuplicateCandidate ||
                    issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate ||
                    issue.Code == DataIntegrityIssueCodes.WarehouseDuplicateCandidate)
                .ToList();

            Assert.Contains(masterIssues, issue =>
                issue.Code == DataIntegrityIssueCodes.CustomerDuplicateCandidate &&
                issue.OfficeCode == OfficeCodeCatalog.Yeonsu &&
                issue.CustomerName == "Scoped Duplicate Customer");
            Assert.Contains(masterIssues, issue =>
                issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate &&
                issue.OfficeCode == OfficeCodeCatalog.Yeonsu &&
                issue.ItemName == "Scoped Duplicate Item");
            Assert.Contains(masterIssues, issue =>
                issue.Code == DataIntegrityIssueCodes.WarehouseDuplicateCandidate &&
                issue.OfficeCode == OfficeCodeCatalog.Yeonsu &&
                issue.CurrentValue.Contains("Scoped Duplicate Warehouse", StringComparison.Ordinal));
            Assert.DoesNotContain(masterIssues, issue =>
                string.Equals(issue.OfficeCode, OfficeCodeCatalog.Usenet, StringComparison.OrdinalIgnoreCase) ||
                issue.CustomerName.Contains("Outside", StringComparison.Ordinal) ||
                issue.ItemName.Contains("Outside", StringComparison.Ordinal) ||
                issue.CurrentValue.Contains("Outside", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_PrefiltersRentalSourceLoadByOperationalScope()
    {
        PrepareAppRoot("georaeplan-integrity-rental-source-scope-prefilter");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int outsideOfficeCount = 650;
            for (var index = 0; index < outsideOfficeCount; index++)
            {
                var outsideProfileId = Guid.NewGuid();
                db.RentalBillingProfiles.Add(CreateProfile(outsideProfileId, index, OfficeCodeCatalog.Usenet));
                db.RentalAssets.Add(CreateBillableAssetWithoutMonthlyFee(index, OfficeCodeCatalog.Usenet));
                db.RentalAssetAssignmentHistories.Add(CreateMissingReferenceHistory(index, OfficeCodeCatalog.Usenet));
            }

            var scopedProfileId = Guid.NewGuid();
            db.RentalBillingProfiles.Add(CreateProfile(scopedProfileId, outsideOfficeCount + 1, OfficeCodeCatalog.Yeonsu));
            db.RentalAssets.Add(CreateBillableAssetWithoutMonthlyFee(outsideOfficeCount + 1, OfficeCodeCatalog.Yeonsu));
            db.RentalAssetAssignmentHistories.Add(CreateMissingReferenceHistory(outsideOfficeCount + 1, OfficeCodeCatalog.Yeonsu));
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateYeonsuAdminSession());
            var rentalIssues = result.Issues
                .Where(issue =>
                    issue.Code == DataIntegrityIssueCodes.RentalProfileWithoutLinkedAssets ||
                    issue.Code == DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee ||
                    issue.Code == DataIntegrityIssueCodes.RentalAssignmentMissingReference)
                .ToList();

            Assert.Contains(rentalIssues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalProfileWithoutLinkedAssets &&
                issue.OfficeCode == OfficeCodeCatalog.Yeonsu);
            Assert.Contains(rentalIssues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee &&
                issue.OfficeCode == OfficeCodeCatalog.Yeonsu);
            Assert.Contains(rentalIssues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalAssignmentMissingReference &&
                issue.OfficeCode == OfficeCodeCatalog.Yeonsu);
            Assert.DoesNotContain(rentalIssues, issue =>
                string.Equals(issue.OfficeCode, OfficeCodeCatalog.Usenet, StringComparison.OrdinalIgnoreCase) ||
                issue.CustomerName.Contains("Outside", StringComparison.Ordinal) ||
                issue.ItemName.Contains("Outside", StringComparison.Ordinal) ||
                issue.AssetDisplayName.Contains("Outside", StringComparison.Ordinal) ||
                issue.CurrentValue.Contains("Outside", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_LoadsLinkedCustomersInBatchesForRentalReferences()
    {
        PrepareAppRoot("georaeplan-integrity-linked-customer-batch-load");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int linkedReferenceCount = 1_100;
            for (var index = 0; index < linkedReferenceCount; index++)
            {
                var customerId = Guid.NewGuid();
                var profileId = Guid.NewGuid();
                var assetId = Guid.NewGuid();
                var customerName = $"Scoped Linked Customer {index:D4}";

                db.Customers.Add(CreateCustomer(customerId, OfficeCodeCatalog.Yeonsu, customerName));
                db.RentalBillingProfiles.Add(CreateProfile(profileId, index, OfficeCodeCatalog.Yeonsu, customerId, customerName));
                db.RentalAssets.Add(CreateLinkedAsset(profileId, index, OfficeCodeCatalog.Yeonsu, customerId, customerName, assetId));
                db.RentalAssetAssignmentHistories.Add(CreateCurrentHistory(index, OfficeCodeCatalog.Yeonsu, assetId, profileId, customerId, customerName));
            }

            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateYeonsuAdminSession());

            Assert.DoesNotContain(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalAssignmentMissingReference);
            Assert.DoesNotContain(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalCustomerNameMismatch);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalRentalBillingProfile CreateProfile(
        Guid profileId,
        int index,
        string officeCode = OfficeCodeCatalog.Usenet,
        Guid? customerId = null,
        string? customerName = null)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            ManagementCompanyCode = officeCode,
            CustomerId = customerId,
            ProfileKey = $"INTEGRITY-PROFILE-{index:D4}",
            CustomerName = customerName ?? $"{ResolveTestScopeLabel(officeCode)} Integrity Customer {index:D4}",
            ItemName = $"{ResolveTestScopeLabel(officeCode)} Integrity Copier {index:D4}",
            InstallSiteName = "Main Office",
            MonthlyAmount = 100_000m,
            BillingTemplateJson = "[]",
            BillingRunsJson = "[]",
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAsset CreateLinkedAsset(
        Guid profileId,
        int index,
        string officeCode = OfficeCodeCatalog.Usenet,
        Guid? customerId = null,
        string? customerName = null,
        Guid? assetId = null)
        => new()
        {
            Id = assetId ?? Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            ManagementCompanyCode = officeCode,
            CustomerId = customerId,
            BillingProfileId = profileId,
            ManagementId = $"INTEGRITY-ASSET-{index:D4}",
            ManagementNumber = $"INT-{index:D4}",
            AssetKey = $"INTEGRITY-ASSET-{Guid.NewGuid():N}",
            CustomerName = customerName ?? $"{ResolveTestScopeLabel(officeCode)} Integrity Customer {index:D4}",
            CurrentCustomerName = customerName ?? $"{ResolveTestScopeLabel(officeCode)} Integrity Customer {index:D4}",
            ItemCategoryName = "Copier",
            ItemName = $"{ResolveTestScopeLabel(officeCode)} Integrity Copier {index:D4}",
            MachineNumber = $"INT-SN-{index:D4}",
            InstallSiteName = "Main Office",
            InstallLocation = "Main Office",
            AssetStatus = "Rental",
            BillingEligibilityStatus = "Billable",
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalCustomer CreateCustomer(Guid customerId, string officeCode, string name)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            NameOriginal = name,
            NameMatchKey = name.ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal),
            TradeType = CustomerTradeTypes.Sales,
            BusinessNumber = string.Empty,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAssetAssignmentHistory CreateCurrentHistory(
        int index,
        string officeCode,
        Guid assetId,
        Guid profileId,
        Guid customerId,
        string customerName)
        => new()
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            BillingProfileId = profileId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            ResponsibleOfficeCode = officeCode,
            CustomerName = customerName,
            InstallLocation = "Main Office",
            BillingProfileDisplay = $"{customerName} Profile",
            ItemName = $"{ResolveTestScopeLabel(officeCode)} Linked Copier {index:D4}",
            MachineNumber = $"LINKED-SN-{index:D4}",
            ManagementNumber = $"LINKED-{officeCode}-{index:D4}",
            MonthlyFee = 100_000m,
            IsCurrent = true,
            LinkedAtUtc = DateTime.UtcNow,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAsset CreateBillableAssetWithoutMonthlyFee(int index, string officeCode)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            ManagementCompanyCode = officeCode,
            ManagementId = $"INTEGRITY-ZERO-FEE-{officeCode}-{index:D4}",
            ManagementNumber = $"ZERO-{officeCode}-{index:D4}",
            AssetKey = $"INTEGRITY-ZERO-FEE-{Guid.NewGuid():N}",
            CustomerName = $"{ResolveTestScopeLabel(officeCode)} Zero Fee Customer {index:D4}",
            CurrentCustomerName = $"{ResolveTestScopeLabel(officeCode)} Zero Fee Customer {index:D4}",
            ItemCategoryName = "Copier",
            ItemName = $"{ResolveTestScopeLabel(officeCode)} Zero Fee Copier {index:D4}",
            MachineNumber = $"ZERO-SN-{index:D4}",
            InstallSiteName = "Main Office",
            InstallLocation = "Main Office",
            AssetStatus = "렌탈중",
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = 0m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAssetAssignmentHistory CreateMissingReferenceHistory(int index, string officeCode)
        => new()
        {
            Id = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            BillingProfileId = Guid.NewGuid(),
            CustomerId = null,
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            ResponsibleOfficeCode = officeCode,
            CustomerName = $"{ResolveTestScopeLabel(officeCode)} History Customer {index:D4}",
            InstallLocation = "Main Office",
            BillingProfileDisplay = $"{ResolveTestScopeLabel(officeCode)} Missing Profile {index:D4}",
            ItemName = $"{ResolveTestScopeLabel(officeCode)} History Copier {index:D4}",
            MachineNumber = $"HIST-SN-{index:D4}",
            ManagementNumber = $"HIST-{officeCode}-{index:D4}",
            MonthlyFee = 100_000m,
            IsCurrent = true,
            LinkedAtUtc = DateTime.UtcNow,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static string ResolveTestScopeLabel(string officeCode)
        => string.Equals(officeCode, OfficeCodeCatalog.Usenet, StringComparison.OrdinalIgnoreCase)
            ? "Outside"
            : "Scoped";

    private static LocalItem CreateInventoryItem(int index, string officeCode = OfficeCodeCatalog.Usenet)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            NameOriginal = $"Inventory Item {index:D4}",
            NameMatchKey = $"INVENTORYITEM{index:D4}",
            SpecificationOriginal = $"Spec {index:D4}",
            SpecificationMatchKey = $"SPEC{index:D4}",
            CategoryName = "Inventory",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            CurrentStock = 1m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalCustomer CreateDuplicateCustomer(int index, string officeCode, string name)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            NameOriginal = name,
            NameMatchKey = name.ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal),
            TradeType = CustomerTradeTypes.Sales,
            BusinessNumber = string.Empty,
            Phone = $"010-{index / 10000:D4}-{index % 10000:D4}",
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalItem CreateDuplicateItem(int index, string officeCode, string name, string specification)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            NameOriginal = name,
            NameMatchKey = name.ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal),
            SpecificationOriginal = specification,
            SpecificationMatchKey = specification.ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal),
            CategoryName = "Inventory",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            CurrentStock = 1m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalWarehouse CreateDuplicateWarehouse(int index, string officeCode, string code, string name)
        => new()
        {
            Id = Guid.NewGuid(),
            OfficeCode = officeCode,
            Code = code,
            Name = name,
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalItemWarehouseStock CreateStockWithMissingWarehouse(Guid itemId)
        => new()
        {
            ItemId = itemId,
            WarehouseCode = "MISSING-WAREHOUSE",
            Quantity = 1m,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalInventoryMovement CreateMovementWithMissingWarehouse(Guid itemId)
        => new()
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            WarehouseCode = "MISSING-WAREHOUSE",
            MovementType = "조정",
            QuantityDelta = 1m,
            OccurredDate = DateOnly.FromDateTime(DateTime.Today),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };

    private static LocalInvoice CreateMismatchedInvoice(int index, string officeCode)
    {
        var invoiceId = Guid.NewGuid();
        return new LocalInvoice
        {
            Id = invoiceId,
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            CustomerId = Guid.NewGuid(),
            InvoiceNumber = $"INV-SCOPE-{officeCode}-{index:D4}",
            VoucherType = VoucherType.Sales,
            InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
            SupplyAmount = 200m,
            VatAmount = 0m,
            TotalAmount = 200m,
            VatMode = InvoiceVatModes.Included,
            SourceWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(officeCode),
            IsLatestVersion = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Lines =
            [
                new LocalInvoiceLine
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemNameOriginal = $"Scope Item {index:D4}",
                    Quantity = 1m,
                    UnitPrice = 100m,
                    LineAmount = 100m,
                    IsDeleted = false
                }
            ]
        };
    }

    private static LocalInvoice CreateInvoiceWithAggregates(int index, string officeCode, decimal lineAmount, decimal invoiceTotal, decimal paymentAmount)
    {
        var invoiceId = Guid.NewGuid();
        return new LocalInvoice
        {
            Id = invoiceId,
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            CustomerId = Guid.NewGuid(),
            InvoiceNumber = $"INV-AGG-{officeCode}-{index:D4}",
            VoucherType = VoucherType.Sales,
            InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
            SupplyAmount = invoiceTotal,
            VatAmount = 0m,
            TotalAmount = invoiceTotal,
            VatMode = InvoiceVatModes.None,
            SourceWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(officeCode),
            IsLatestVersion = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Lines =
            [
                new LocalInvoiceLine
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemNameOriginal = $"Aggregate Item {index:D4}",
                    Quantity = 1m,
                    UnitPrice = lineAmount,
                    LineAmount = lineAmount,
                    IsDeleted = false
                }
            ],
            Payments =
            [
                new LocalPayment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    PaymentDate = DateOnly.FromDateTime(DateTime.Today),
                    Amount = paymentAmount,
                    IsDeleted = false,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        };
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static SessionState CreateYeonsuAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "yeonsu-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }
}
