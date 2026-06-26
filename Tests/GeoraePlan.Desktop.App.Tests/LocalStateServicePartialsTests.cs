using System.Reflection;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalStateServicePartialsTests
{
    [Fact]
    public void PendingSyncSummary_BuildWaitingMessage_UsesPrimaryBucketAndTotal()
    {
        var summary = new PendingSyncSummary(
            5,
            [
                new PendingSyncBucket("OFFICE:ITWORLD", "ITWORLD", "거래처 변경", 3),
                new PendingSyncBucket("OFFICE:USENET", "USENET", "품목 변경", 2)
            ]);

        var message = summary.BuildWaitingMessage("안내:");

        Assert.Equal("안내: ITWORLD 거래처 변경 3건 포함 총 5건이 서버 반영 대기 중입니다.", message);
        Assert.Equal("ITWORLD", summary.PrimaryBucket?.ScopeDisplayName);
    }

    [Fact]
    public async Task PendingSyncSummary_CountsAllDirtySettingPushCollections()
    {
        var previous = Environment.GetEnvironmentVariable("GEORAEPLAN_LOCAL_DB_PATH");
        var dbPath = Path.Combine(Path.GetTempPath(), $"georaeplan-setting-dirty-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("GEORAEPLAN_LOCAL_DB_PATH", dbPath);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.Units.Add(new LocalUnit { Id = Guid.NewGuid(), Name = "감사단위", IsDirty = true });
            db.CustomerCategories.Add(new LocalCustomerCategory { Id = Guid.NewGuid(), Name = "감사분류", IsDirty = true });
            db.PriceGradeOptions.Add(new LocalPriceGradeOption { Id = Guid.NewGuid(), Name = "감사가격", IsDirty = true });
            db.TradeTypeOptions.Add(new LocalTradeTypeOption { Id = Guid.NewGuid(), Name = "감사거래", IsDirty = true });
            db.ItemCategoryOptions.Add(new LocalItemCategoryOption { Id = Guid.NewGuid(), Name = "감사품목", IsDirty = true });
            db.CustomerMasters.Add(new LocalCustomerMaster
            {
                Id = Guid.NewGuid(),
                NameOriginal = "감사거래처기준",
                NameMatchKey = "감사거래처기준",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                IsDirty = true
            });
            await db.SaveChangesAsync();

            db.ChangeTracker.Clear();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), CreateAdminSession());

            Assert.Equal(6, await service.CountDirtyAsync());
            Assert.True(await service.HasPendingSyncChangesAsync());

            var summary = await service.GetPendingSyncSummaryAsync();

            Assert.Equal(6, summary.TotalCount);
            Assert.Contains(summary.Buckets, bucket => bucket.EntityDisplayName == "단위 변경" && bucket.Count == 1);
            Assert.Contains(summary.Buckets, bucket => bucket.EntityDisplayName == "거래처분류 변경" && bucket.Count == 1);
            Assert.Contains(summary.Buckets, bucket => bucket.EntityDisplayName == "가격등급 변경" && bucket.Count == 1);
            Assert.Contains(summary.Buckets, bucket => bucket.EntityDisplayName == "거래유형 변경" && bucket.Count == 1);
            Assert.Contains(summary.Buckets, bucket => bucket.EntityDisplayName == "품목분류 변경" && bucket.Count == 1);
            Assert.Contains(summary.Buckets, bucket => bucket.EntityDisplayName == "거래처 기준정보 변경" && bucket.Count == 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_LOCAL_DB_PATH", previous);
            SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch
            {
                // 테스트 임시 DB 삭제 실패는 다음 임시 경로와 충돌하지 않습니다.
            }
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1.1.171", true)]
    [InlineData("1.1.172", false)]
    [InlineData("1.1.173", false)]
    [InlineData("invalid", true)]
    public void VersionChangeMaintenance_FullMirrorRefresh_RunsOnlyBeforeBaselineVersion(
        string? lastProcessedVersion,
        bool expected)
    {
        var actual = InvokePrivateStatic<bool>(
            typeof(VersionChangeMaintenanceService),
            "RequiresFullMirrorRefreshAfterVersionChange",
            new object?[] { lastProcessedVersion });

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("old-epoch", true)]
    [InlineData("2026-05-27-rental-asset-mirror", true)]
    [InlineData(" 2026-05-27-rental-asset-mirror ", true)]
    [InlineData("2026-05-27-lightweight-full-sync-mirror", false)]
    [InlineData(" 2026-05-27-lightweight-full-sync-mirror ", false)]
    public void VersionChangeMaintenance_CacheMirrorRepair_RunsUntilCurrentEpochRecorded(
        string? lastRepairEpoch,
        bool expected)
    {
        var actual = InvokePrivateStatic<bool>(
            typeof(VersionChangeMaintenanceService),
            "RequiresCacheMirrorRepair",
            new object?[] { lastRepairEpoch });

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SalesViewModel_CalculateTaxInclusiveTotals_DoesNotAddVatOnTop()
    {
        var result = InvokePrivateStatic<(decimal SupplyAmount, decimal VatAmount, decimal TotalAmount)>(
            typeof(SalesViewModel),
            "CalculateTaxInclusiveTotals",
            new[] { 2_500m, 192_000m });

        Assert.Equal(194_500m, result.TotalAmount);
        Assert.Equal(176_818m, result.SupplyAmount);
        Assert.Equal(17_682m, result.VatAmount);
    }

    [Fact]
    public void LocalStateService_CustomerFinancialSummaryInvoiceFilter_UsesOnlyActiveConfirmedLatestInvoices()
    {
        Assert.True(IsFinancialSummaryInvoice(VoucherType.Sales, isLatestVersion: true, isConfirmed: true, isDeleted: false));
        Assert.True(IsFinancialSummaryInvoice(VoucherType.Purchase, isLatestVersion: true, isConfirmed: true, isDeleted: false));
        Assert.False(IsFinancialSummaryInvoice(VoucherType.Sales, isLatestVersion: false, isConfirmed: true, isDeleted: false));
        Assert.False(IsFinancialSummaryInvoice(VoucherType.Sales, isLatestVersion: true, isConfirmed: false, isDeleted: false));
        Assert.False(IsFinancialSummaryInvoice(VoucherType.Sales, isLatestVersion: true, isConfirmed: true, isDeleted: true));
        Assert.False(IsFinancialSummaryInvoice(VoucherType.Procurement, isLatestVersion: true, isConfirmed: true, isDeleted: false));
    }

    [Fact]
    public void PaymentViewModel_ResolveInvoiceDefaultTransactionKind_UsesInvoiceSettlementKinds()
    {
        Assert.Equal(
            PaymentFlowConstants.TransactionKindInvoiceReceipt,
            ResolveInvoiceDefaultTransactionKind(VoucherType.Sales));
        Assert.Equal(
            PaymentFlowConstants.TransactionKindInvoicePayment,
            ResolveInvoiceDefaultTransactionKind(VoucherType.Purchase));
        Assert.Equal(
            PaymentFlowConstants.TransactionKindInvoicePayment,
            ResolveInvoiceDefaultTransactionKind(VoucherType.Procurement));
    }

    [Fact]
    public void DataIntegrityScanResult_PassiveStartupNotice_IgnoresDuplicateCandidateNoise()
    {
        var duplicateOnly = new DataIntegrityScanResult(
            DateTime.Now,
            [],
            [
                new DataIntegrityIssueDetail
                {
                    Code = DataIntegrityIssueCodes.ItemDuplicateCandidate,
                    Severity = "Warning",
                    Message = "품목 중복 후보"
                },
                new DataIntegrityIssueDetail
                {
                    Code = DataIntegrityIssueCodes.CustomerDuplicateCandidate,
                    Severity = "Warning",
                    Message = "거래처 중복 후보"
                }
            ]);

        Assert.True(duplicateOnly.HasIssues);
        Assert.False(duplicateOnly.HasPassiveStartupNoticeIssues);
        Assert.Equal(string.Empty, duplicateOnly.PassiveStartupNoticeSignature);

        var actionable = new DataIntegrityScanResult(
            DateTime.Now,
            [],
            [
                new DataIntegrityIssueDetail
                {
                    Code = DataIntegrityIssueCodes.InvoiceAmountMismatch,
                    Severity = "Warning",
                    Message = "전표 금액 계산 불일치"
                }
            ]);

        Assert.True(actionable.HasPassiveStartupNoticeIssues);
        Assert.Equal($"{DataIntegrityIssueCodes.InvoiceAmountMismatch}:1", actionable.PassiveStartupNoticeSignature);
    }

    [Theory]
    [InlineData("현금", "Cash")]
    [InlineData("카드", "Card")]
    [InlineData("CMS", "Bank")]
    [InlineData("cms", "Bank")]
    [InlineData("전자세금계산서", "Bank")]
    [InlineData("", "Bank")]
    [InlineData(null, "Bank")]
    public void PaymentViewModel_ResolveRentalReceiptTarget_MapsBillingMethodToReceiptBucket(
        string? billingMethod,
        string expected)
    {
        var result = InvokePrivateStatic<object>(
            typeof(PaymentViewModel),
            "ResolveRentalReceiptTarget",
            new object?[] { billingMethod });

        Assert.Equal(expected, result.ToString());
    }

    [Fact]
    public async Task PaymentViewModel_ManualSettlementInput_InvalidatesPendingDefaultSuggestion()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-payment-suggestion-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("92303030-3030-3030-3030-303030303030");
            var invoiceId = Guid.Parse("92313131-3131-3131-3131-313131313131");
            var customer = new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "수금 기본값 경합 거래처",
                NameMatchKey = "수금기본값경합거래처",
                IsDirty = false
            };

            var invoice = new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 20),
                InvoiceNumber = "GP-PAY-RACE-001",
                LocalTempNumber = "TMP-GP-PAY-RACE-001",
                TotalAmount = 1_000m,
                SupplyAmount = 909m,
                VatAmount = 91m,
                VersionGroupId = invoiceId,
                IsConfirmed = true,
                IsLatestVersion = true,
                IsDirty = false
            };

            db.Customers.Add(customer);
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var viewModel = new PaymentViewModel(local, session);
            await viewModel.LoadAsync(customer);
            await viewModel.ConfigureForInvoiceAsync(invoice);

            var versionField = typeof(PaymentViewModel).GetField(
                "_settlementSuggestionVersion",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(versionField);

            const int staleSuggestionVersion = 41;
            versionField!.SetValue(viewModel, staleSuggestionVersion);
            viewModel.SettlementAmount = 500m;
            viewModel.BankReceipt = 500m;

            var versionAfterManualInput = Assert.IsType<int>(versionField.GetValue(viewModel));
            Assert.True(versionAfterManualInput > staleSuggestionVersion);

            await InvokePrivateInstanceTaskResultAsync(
                viewModel,
                "ApplyInvoiceDefaultSettlementAsync",
                true,
                false,
                staleSuggestionVersion);

            Assert.Equal(500m, viewModel.SettlementAmount);
            Assert.Equal(500m, viewModel.BankReceipt);
            Assert.Equal(500m, viewModel.ReceiptTotal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PaymentViewModel_LoadAttachmentsAsync_PreservesSelectedAttachmentAfterRefresh()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-payment-attachment-selection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("92404040-4040-4040-4040-404040404040");
            var transactionId = Guid.Parse("92414141-4141-4141-4141-414141414141");
            var selectedAttachmentId = Guid.Parse("92424242-4242-4242-4242-424242424242");
            var otherAttachmentId = Guid.Parse("92434343-4343-4343-4343-434343434343");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "증빙 선택 유지 거래처",
                NameMatchKey = "증빙선택유지거래처",
                IsDirty = false
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 21),
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                BankReceipt = 10_000m,
                ReceiptTotal = 10_000m,
                IsDirty = false
            });
            db.TransactionAttachments.AddRange(
                new LocalTransactionAttachment
                {
                    Id = selectedAttachmentId,
                    TransactionId = transactionId,
                    AttachmentType = "입금확인증",
                    FileName = "selected.pdf",
                    StoredPath = @"D:\dummy\selected.pdf",
                    UploadedAtUtc = new DateTime(2026, 6, 21, 9, 0, 0, DateTimeKind.Utc),
                    SortOrder = 1,
                    IsDirty = false
                },
                new LocalTransactionAttachment
                {
                    Id = otherAttachmentId,
                    TransactionId = transactionId,
                    AttachmentType = "영수증",
                    FileName = "other.pdf",
                    StoredPath = @"D:\dummy\other.pdf",
                    UploadedAtUtc = new DateTime(2026, 6, 21, 8, 0, 0, DateTimeKind.Utc),
                    SortOrder = 2,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var viewModel = new PaymentViewModel(local, session);

            await InvokePrivateInstanceTaskResultAsync(viewModel, "LoadAttachmentsAsync", transactionId, 0);
            viewModel.SelectedAttachment = Assert.Single(
                viewModel.Attachments,
                attachment => attachment.Id == selectedAttachmentId);

            await InvokePrivateInstanceTaskResultAsync(viewModel, "LoadAttachmentsAsync", transactionId, 0);

            Assert.NotNull(viewModel.SelectedAttachment);
            Assert.Equal(selectedAttachmentId, viewModel.SelectedAttachment.Id);
            Assert.Equal(2, viewModel.Attachments.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu, OfficeCodeCatalog.Usenet)]
    [InlineData(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, OfficeCodeCatalog.Itworld)]
    public async Task LocalStateService_SaveTransactionAsync_NormalizesStoredTransactionScope(
        string tenantCode,
        string responsibleOfficeCode,
        string expectedOwnerOfficeCode)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-payment-scope-normalize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = tenantCode,
                OfficeCode = expectedOwnerOfficeCode,
                ResponsibleOfficeCode = responsibleOfficeCode,
                NameOriginal = "수금 scope 정규화 거래처",
                NameMatchKey = "PAYMENTSCOPECUSTOMER",
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var session = CreateUserSession(
                tenantCode,
                responsibleOfficeCode,
                TenantScopeCatalog.ScopeOfficeOnly,
                AppPermissionNames.PaymentEdit);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var save = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = "WRONG-TENANT",
                OfficeCode = string.Empty,
                ResponsibleOfficeCode = string.Empty,
                TransactionDate = new DateOnly(2026, 6, 22),
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                BankReceipt = 10_000m,
                ReceiptTotal = 10_000m,
                Note = "scope normalize"
            }, session);

            Assert.True(save.Success, save.Message);
            var stored = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.Equal(tenantCode, stored.TenantCode);
            Assert.Equal(expectedOwnerOfficeCode, stored.OfficeCode);
            Assert.Equal(responsibleOfficeCode, stored.ResponsibleOfficeCode);

            var dirty = await local.GetDirtyTransactionsForSyncAsync(session);
            var dirtyTransaction = Assert.Single(dirty);
            Assert.Equal(transactionId, dirtyTransaction.Id);
            Assert.Equal(tenantCode, dirtyTransaction.TenantCode);
            Assert.Equal(expectedOwnerOfficeCode, dirtyTransaction.OfficeCode);
            Assert.Equal(responsibleOfficeCode, dirtyTransaction.ResponsibleOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_QueryInvoicesAndTransactions_UsesOwnerOfficeWhenResponsibleOfficeIsBlank()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-ledger-owner-office-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();
            var invoiceDate = new DateOnly(2026, 6, 22);
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "연수 담당지점 공백 전표 거래처",
                NameMatchKey = "YEONSUBLANKRESPONSIBLEINVOICECUSTOMER",
                IsDirty = false
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = string.Empty,
                SourceWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                InvoiceNumber = "LOCAL-BLANK-RESP-001",
                VoucherType = VoucherType.Sales,
                InvoiceDate = invoiceDate,
                TotalAmount = 50_000m,
                SupplyAmount = 50_000m,
                VatAmount = 0m,
                VatMode = InvoiceVatModes.None,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false,
                IsDirty = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = string.Empty,
                TransactionDate = invoiceDate,
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                LinkedInvoiceId = invoiceId,
                LinkedInvoiceNumber = "LOCAL-BLANK-RESP-001",
                BankReceipt = 50_000m,
                ReceiptTotal = 50_000m,
                SettlementAmount = 50_000m,
                IsDeleted = false,
                IsDirty = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = transactionId,
                AttachmentType = "영수증",
                FileName = "blank-responsible-receipt.pdf",
                StoredFileName = "blank-responsible-receipt.pdf",
                StoredPath = "test/blank-responsible-receipt.pdf",
                MimeType = "application/pdf",
                FileSize = 1,
                UploadedAtUtc = DateTime.UtcNow,
                IsDeleted = false,
                IsDirty = true
            });
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = invoiceDate,
                Amount = 50_000m,
                Note = "blank responsible office payment",
                IsDeleted = false,
                IsDirty = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var yeonsuSession = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                TenantScopeCatalog.ScopeOfficeOnly);
            var yeonsuLocal = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), yeonsuSession);

            var yeonsuInvoices = await yeonsuLocal.GetInvoicesAsync(invoiceDate, invoiceDate, null, yeonsuSession);
            var yeonsuTransactions = await yeonsuLocal.GetTransactionsAsync(invoiceDate, invoiceDate, null, yeonsuSession);
            var yeonsuInvoice = await yeonsuLocal.GetInvoiceAsync(invoiceId, yeonsuSession);
            var yeonsuSettlement = await yeonsuLocal.GetInvoiceSettlementSummaryAsync(invoiceId, yeonsuSession);
            var yeonsuAttachments = await yeonsuLocal.GetTransactionAttachmentsAsync(transactionId, yeonsuSession);

            Assert.Contains(yeonsuInvoices, invoice => invoice.Id == invoiceId);
            Assert.Contains(yeonsuTransactions, transaction => transaction.Id == transactionId);
            Assert.NotNull(yeonsuInvoice);
            Assert.Equal(50_000m, yeonsuSettlement.InvoiceTotal);
            Assert.Contains(yeonsuAttachments, attachment => attachment.Id == attachmentId);

            var yeonsuSyncSession = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                TenantScopeCatalog.ScopeOfficeOnly,
                AppPermissionNames.InvoiceEdit,
                AppPermissionNames.PaymentEdit);
            var yeonsuSyncLocal = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), yeonsuSyncSession);

            var yeonsuDirtyInvoices = await yeonsuSyncLocal.GetDirtyInvoicesForSyncAsync(yeonsuSyncSession);
            var yeonsuDirtyTransactions = await yeonsuSyncLocal.GetDirtyTransactionsForSyncAsync(yeonsuSyncSession);
            var yeonsuDirtyAttachments = await yeonsuSyncLocal.GetDirtyTransactionAttachmentsForSyncAsync(yeonsuSyncSession);
            var yeonsuDirtyPayments = await yeonsuSyncLocal.GetDirtyPaymentsForSyncAsync(yeonsuSyncSession);

            Assert.Contains(yeonsuDirtyInvoices, invoice => invoice.Id == invoiceId);
            Assert.Contains(yeonsuDirtyTransactions, transaction => transaction.Id == transactionId);
            Assert.Contains(yeonsuDirtyAttachments, attachment => attachment.Id == attachmentId);
            Assert.Contains(yeonsuDirtyPayments, payment => payment.Id == paymentId);

            var usenetSession = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeOfficeOnly);
            var usenetLocal = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), usenetSession);

            var usenetInvoices = await usenetLocal.GetInvoicesAsync(invoiceDate, invoiceDate, null, usenetSession);
            var usenetTransactions = await usenetLocal.GetTransactionsAsync(invoiceDate, invoiceDate, null, usenetSession);
            var usenetInvoice = await usenetLocal.GetInvoiceAsync(invoiceId, usenetSession);
            var usenetSettlement = await usenetLocal.GetInvoiceSettlementSummaryAsync(invoiceId, usenetSession);
            var usenetAttachments = await usenetLocal.GetTransactionAttachmentsAsync(transactionId, usenetSession);

            Assert.DoesNotContain(usenetInvoices, invoice => invoice.Id == invoiceId);
            Assert.DoesNotContain(usenetTransactions, transaction => transaction.Id == transactionId);
            Assert.Null(usenetInvoice);
            Assert.Equal(0m, usenetSettlement.InvoiceTotal);
            Assert.DoesNotContain(usenetAttachments, attachment => attachment.Id == attachmentId);

            var usenetSyncSession = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeOfficeOnly,
                AppPermissionNames.InvoiceEdit,
                AppPermissionNames.PaymentEdit);
            var usenetSyncLocal = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), usenetSyncSession);

            var usenetDirtyInvoices = await usenetSyncLocal.GetDirtyInvoicesForSyncAsync(usenetSyncSession);
            var usenetDirtyTransactions = await usenetSyncLocal.GetDirtyTransactionsForSyncAsync(usenetSyncSession);
            var usenetDirtyAttachments = await usenetSyncLocal.GetDirtyTransactionAttachmentsForSyncAsync(usenetSyncSession);
            var usenetDirtyPayments = await usenetSyncLocal.GetDirtyPaymentsForSyncAsync(usenetSyncSession);

            Assert.DoesNotContain(usenetDirtyInvoices, invoice => invoice.Id == invoiceId);
            Assert.DoesNotContain(usenetDirtyTransactions, transaction => transaction.Id == transactionId);
            Assert.DoesNotContain(usenetDirtyAttachments, attachment => attachment.Id == attachmentId);
            Assert.DoesNotContain(usenetDirtyPayments, payment => payment.Id == paymentId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RentalStateService_NormalizeRentalBillingInvoiceLineItemNames_UsesRentalChargeItemName()
    {
        var invoice = new LocalInvoice
        {
            Lines =
            [
                new LocalInvoiceLine { ItemNameOriginal = "IMC2010[5월]", LineAmount = 440_000m },
                new LocalInvoiceLine { ItemNameOriginal = "SL-X4220RX[5월]", LineAmount = 150_000m }
            ]
        };
        var run = new RentalBillingRunModel
        {
            PeriodStartDate = new DateOnly(2026, 5, 1),
            PeriodEndDate = new DateOnly(2026, 5, 31),
            CycleMonths = 1
        };

        var changed = InvokePrivateStatic<bool>(
            typeof(RentalStateService),
            "NormalizeRentalBillingInvoiceLineItemNames",
            invoice,
            run);

        Assert.True(changed);
        Assert.All(invoice.Lines, line => Assert.Equal("사무기기 렌탈대금[5월]", line.ItemNameOriginal));
    }

    [Fact]
    public void MainViewModel_ResolveMainInvoiceQueryDateRange_IgnoresHiddenPeriodFilter()
    {
        var result = InvokePrivateStatic<(DateOnly? From, DateOnly? To)>(
            typeof(MainViewModel),
            "ResolveMainInvoiceQueryDateRange",
            new DateOnly(2026, 5, 1),
            new DateOnly(2026, 5, 31));

        Assert.Null(result.From);
        Assert.Null(result.To);
    }

    [Fact]
    public void MainViewModel_NormalizeHiddenInvoiceTextFilters_ClearsInvisibleFilters()
    {
        var result = InvokePrivateStatic<(string CustomerName, string MinAmountText, string MaxAmountText)>(
            typeof(MainViewModel),
            "NormalizeHiddenInvoiceTextFilters",
            "연수구",
            "100000",
            "200000");

        Assert.Equal(string.Empty, result.CustomerName);
        Assert.Equal(string.Empty, result.MinAmountText);
        Assert.Equal(string.Empty, result.MaxAmountText);
    }

    [Fact]
    public async Task MainViewModel_LoadAsync_PopulatesVisibleCustomerAndInvoiceRows_WhenLocalCacheHasData()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "georaeplan-main-view-probe-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);
            var viewModel = new MainViewModel(localState, sync, new BackupService(), rental, diagnostics, api, session);

            var customerId = Guid.Parse("91111111-1111-1111-1111-111111111111");
            var invoiceId = Guid.Parse("92222222-2222-2222-2222-222222222222");
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "UI 표시 검증 거래처",
                NameMatchKey = "UIVISIBLECUSTOMER",
                IsDeleted = false
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = DateOnly.FromDateTime(DateTime.Today.AddMonths(-3)),
                InvoiceNumber = "UI-PROBE-001",
                TotalAmount = 110_000m,
                SupplyAmount = 100_000m,
                VatAmount = 10_000m,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemNameOriginal = "UI 표시 검증 품목",
                        Unit = "EA",
                        Quantity = 1m,
                        UnitPrice = 110_000m,
                        LineAmount = 110_000m,
                        IsDeleted = false
                    }
                }
            });
            await db.SaveChangesAsync();

            await localState.SetSettingAsync("USENET:InvoiceFilter.CustomerName", "이전버전숨은필터");
            await localState.SetSettingAsync("USENET:InvoiceFilter.MinAmount", "999999999");
            await localState.SetSettingAsync("USENET:InvoiceFilter.MaxAmount", "999999999");

            await viewModel.LoadAsync();

            var visibleCustomer = Assert.Single(viewModel.FilteredCustomers);
            Assert.Equal(customerId, visibleCustomer.Id);
            Assert.Single(viewModel.InvoiceRows);
            Assert.Equal(invoiceId, viewModel.InvoiceRows.Single().Id);
            Assert.Equal(1, viewModel.DashboardCustomerCount);
            Assert.Equal(string.Empty, viewModel.FilterCustomerName);
            Assert.Equal(string.Empty, viewModel.FilterMinAmountText);
            Assert.Equal(string.Empty, viewModel.FilterMaxAmountText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void MainViewModel_LoadInvoiceListAsync_IgnoresCanceledStaleLoadsBeforeMutatingRows()
    {
        var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "Desktop",
                "거래플랜.Desktop.App",
                "ViewModels",
                "MainViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("private bool IsCurrentInvoiceListLoad(CancellationTokenSource loadCts)", source, StringComparison.Ordinal);

        var queryIndex = source.IndexOf("var invoiceList = await _local.GetInvoiceListSummariesAsync", StringComparison.Ordinal);
        var customerMapIndex = source.IndexOf("var customerMap = await BuildInvoiceCustomerNameMapAsync(invoiceList, ct);", StringComparison.Ordinal);
        var rowsIndex = source.IndexOf("var rows = finalInvoices.Select(inv =>", StringComparison.Ordinal);
        var replaceIndex = source.IndexOf("InvoiceRows.ReplaceWith(rows);", StringComparison.Ordinal);
        var dashboardIndex = source.IndexOf("await RefreshDashboardMetricsAsync(canReuseAsAllInvoiceSet ? invoiceList : null, ct);", StringComparison.Ordinal);
        var favoritesIndex = source.IndexOf("await LoadInvoiceFavoritesAsync(canReuseAsAllInvoiceSet ? invoiceList : null, ct);", StringComparison.Ordinal);
        var financialIndex = source.IndexOf("await RefreshSelectedCustomerFinancialPreviewAsync();", StringComparison.Ordinal);

        Assert.True(queryIndex > 0);
        Assert.True(customerMapIndex > queryIndex);
        Assert.True(rowsIndex > customerMapIndex);
        Assert.True(replaceIndex > rowsIndex);
        Assert.True(dashboardIndex > replaceIndex);
        Assert.True(favoritesIndex > dashboardIndex);
        Assert.True(financialIndex > favoritesIndex);

        Assert.InRange(source.IndexOf("if (!IsCurrentInvoiceListLoad(loadCts))", queryIndex, StringComparison.Ordinal), queryIndex, customerMapIndex);
        Assert.InRange(source.IndexOf("if (!IsCurrentInvoiceListLoad(loadCts))", customerMapIndex, StringComparison.Ordinal), customerMapIndex, rowsIndex);
        Assert.InRange(source.IndexOf("if (!IsCurrentInvoiceListLoad(loadCts))", rowsIndex, StringComparison.Ordinal), rowsIndex, replaceIndex);
        Assert.InRange(source.IndexOf("if (!IsCurrentInvoiceListLoad(loadCts))", dashboardIndex, StringComparison.Ordinal), dashboardIndex, favoritesIndex);
        Assert.InRange(source.IndexOf("if (!IsCurrentInvoiceListLoad(loadCts))", favoritesIndex, StringComparison.Ordinal), favoritesIndex, financialIndex);
    }

    [Fact]
    public async Task MainViewModel_LoadInvoiceListAsync_KeepsSelectedInvoiceAfterListRefresh()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "georaeplan-main-selection-probe-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);
            var viewModel = new MainViewModel(localState, sync, new BackupService(), rental, diagnostics, api, session);

            var customerId = Guid.Parse("92333333-3333-3333-3333-333333333333");
            var selectedInvoiceId = Guid.Parse("92444444-4444-4444-4444-444444444444");
            var newInvoiceId = Guid.Parse("92555555-5555-5555-5555-555555555555");
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "선택 유지 검증 거래처",
                NameMatchKey = "SELECTIONSTABLECUSTOMER",
                IsDeleted = false
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = selectedInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)),
                InvoiceNumber = "KEEP-SELECTION-001",
                TotalAmount = 110_000m,
                SupplyAmount = 100_000m,
                VatAmount = 10_000m,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemNameOriginal = "선택 유지 기존 품목",
                        Quantity = 1m,
                        UnitPrice = 110_000m,
                        LineAmount = 110_000m,
                        IsDeleted = false
                    }
                }
            });
            await db.SaveChangesAsync();

            await viewModel.LoadAsync();
            var selectedBeforeRefresh = Assert.Single(viewModel.InvoiceRows);
            viewModel.SelectedInvoiceRow = selectedBeforeRefresh;

            db.Invoices.Add(new LocalInvoice
            {
                Id = newInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
                InvoiceNumber = "KEEP-SELECTION-002",
                TotalAmount = 220_000m,
                SupplyAmount = 200_000m,
                VatAmount = 20_000m,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemNameOriginal = "동기화 신규 품목",
                        Quantity = 1m,
                        UnitPrice = 220_000m,
                        LineAmount = 220_000m,
                        IsDeleted = false
                    }
                }
            });
            await db.SaveChangesAsync();

            await viewModel.LoadInvoiceListCommand.ExecuteAsync(null);

            Assert.Equal(2, viewModel.InvoiceRows.Count);
            Assert.NotNull(viewModel.SelectedInvoiceRow);
            Assert.Equal(selectedInvoiceId, viewModel.SelectedInvoiceRow.Id);
            Assert.NotSame(selectedBeforeRefresh, viewModel.SelectedInvoiceRow);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MainViewModel_LoadInvoiceListAsync_PromotesSelectedInvoiceToLatestVersionAfterListRefresh()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "georaeplan-main-latest-selection-probe-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);
            var viewModel = new MainViewModel(localState, sync, new BackupService(), rental, diagnostics, api, session);

            var customerId = Guid.Parse("92666666-6666-6666-6666-666666666666");
            var versionGroupId = Guid.Parse("92777777-7777-7777-7777-777777777777");
            var oldInvoiceId = Guid.Parse("92888888-8888-8888-8888-888888888888");
            var latestInvoiceId = Guid.Parse("92999999-9999-9999-9999-999999999999");
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "최신 전표 승격 검증 거래처",
                NameMatchKey = "LATESTSELECTIONCUSTOMER",
                IsDeleted = false
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = oldInvoiceId,
                VersionGroupId = versionGroupId,
                VersionNumber = 1,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Purchase,
                InvoiceDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-3)),
                InvoiceNumber = "LATEST-SELECTION-001",
                TotalAmount = 110_000m,
                SupplyAmount = 100_000m,
                VatAmount = 10_000m,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemNameOriginal = "최신 승격 기존 품목",
                        Quantity = 1m,
                        UnitPrice = 110_000m,
                        LineAmount = 110_000m,
                        IsDeleted = false
                    }
                }
            });
            await db.SaveChangesAsync();

            await viewModel.LoadAsync();
            var selectedBeforeRefresh = Assert.Single(viewModel.InvoiceRows);
            Assert.Equal(oldInvoiceId, selectedBeforeRefresh.Id);
            Assert.Equal(versionGroupId, selectedBeforeRefresh.EffectiveVersionGroupId);
            viewModel.SelectedInvoiceRow = selectedBeforeRefresh;

            var oldInvoice = await db.Invoices.SingleAsync(invoice => invoice.Id == oldInvoiceId);
            oldInvoice.IsLatestVersion = false;
            db.Invoices.Add(new LocalInvoice
            {
                Id = latestInvoiceId,
                VersionGroupId = versionGroupId,
                VersionNumber = 2,
                PreviousVersionId = oldInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Purchase,
                InvoiceDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-2)),
                InvoiceNumber = "LATEST-SELECTION-001",
                TotalAmount = 220_000m,
                SupplyAmount = 200_000m,
                VatAmount = 20_000m,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemNameOriginal = "최신 승격 신규 품목",
                        Quantity = 1m,
                        UnitPrice = 220_000m,
                        LineAmount = 220_000m,
                        IsDeleted = false
                    }
                }
            });
            await db.SaveChangesAsync();

            await viewModel.LoadInvoiceListCommand.ExecuteAsync(null);

            var selectedAfterRefresh = viewModel.SelectedInvoiceRow;
            Assert.NotNull(selectedAfterRefresh);
            Assert.Equal(latestInvoiceId, selectedAfterRefresh!.Id);
            Assert.Equal(versionGroupId, selectedAfterRefresh.EffectiveVersionGroupId);
            Assert.Single(viewModel.InvoiceRows);
            Assert.DoesNotContain(viewModel.InvoiceRows, row => row.Id == oldInvoiceId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MainViewModel_GetLatestSelectedInvoiceAsync_ReturnsLatestInvoiceAndPromotesSelection()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "georaeplan-main-latest-open-probe-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);
            var viewModel = new MainViewModel(localState, sync, new BackupService(), rental, diagnostics, api, session);

            var customerId = Guid.Parse("92AAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA");
            var versionGroupId = Guid.Parse("92BBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB");
            var oldInvoiceId = Guid.Parse("92CCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC");
            var latestInvoiceId = Guid.Parse("92DDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD");
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "최신 전표 열기 검증 거래처",
                NameMatchKey = "LATESTOPENCUSTOMER",
                IsDeleted = false
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = oldInvoiceId,
                VersionGroupId = versionGroupId,
                VersionNumber = 1,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-3)),
                InvoiceNumber = "LATEST-OPEN-001",
                TotalAmount = 55_000m,
                SupplyAmount = 50_000m,
                VatAmount = 5_000m,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemNameOriginal = "최신 열기 기존 품목",
                        Quantity = 1m,
                        UnitPrice = 55_000m,
                        LineAmount = 55_000m,
                        IsDeleted = false
                    }
                }
            });
            await db.SaveChangesAsync();

            await viewModel.LoadAsync();
            var selectedBeforeLatestVersion = Assert.Single(viewModel.InvoiceRows);
            viewModel.SelectedInvoiceRow = selectedBeforeLatestVersion;

            var oldInvoice = await db.Invoices.SingleAsync(invoice => invoice.Id == oldInvoiceId);
            oldInvoice.IsLatestVersion = false;
            db.Invoices.Add(new LocalInvoice
            {
                Id = latestInvoiceId,
                VersionGroupId = versionGroupId,
                VersionNumber = 2,
                PreviousVersionId = oldInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-2)),
                InvoiceNumber = "LATEST-OPEN-001",
                TotalAmount = 77_000m,
                SupplyAmount = 70_000m,
                VatAmount = 7_000m,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemNameOriginal = "최신 열기 신규 품목",
                        Quantity = 1m,
                        UnitPrice = 77_000m,
                        LineAmount = 77_000m,
                        IsDeleted = false
                    }
                }
            });
            await db.SaveChangesAsync();

            var latestInvoice = await viewModel.GetLatestSelectedInvoiceAsync();

            Assert.NotNull(latestInvoice);
            Assert.Equal(latestInvoiceId, latestInvoice.Id);
            Assert.NotNull(viewModel.SelectedInvoiceRow);
            Assert.Equal(latestInvoiceId, viewModel.SelectedInvoiceRow.Id);
            Assert.Equal(versionGroupId, viewModel.SelectedInvoiceRow.EffectiveVersionGroupId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MainViewModel_PostLoginSyncSkip_ChecksServerRevisionBeforeSkippingRecentSync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "georaeplan-post-login-revision-probe-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateOnlineAdminSession();
            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(
                new HttpClient(new SyncStatusHandler(101)) { BaseAddress = new Uri("http://localhost/") },
                session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);
            var viewModel = new MainViewModel(localState, sync, new BackupService(), rental, diagnostics, api, session);

            db.Settings.AddRange(
                new LocalSetting { Key = "LastSyncRevision", Value = "100" },
                new LocalSetting { Key = "Sync.LastSuccessAt", Value = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) });
            db.Customers.Add(new LocalCustomer
            {
                Id = Guid.Parse("93111111-1111-1111-1111-111111111111"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "?? ??? ?? ???",
                NameMatchKey = "POSTLOGINREVISIONCUSTOMER",
                TradeType = "??",
                IsDeleted = false
            });
            await db.SaveChangesAsync();

            var shouldSkip = await InvokePrivateInstanceAsync<bool>(viewModel, "ShouldSkipImmediatePostLoginSyncAsync");

            Assert.False(shouldSkip);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_CorruptedPrimaryWorkCacheCheck_AllowsAllDefinedVoucherTypes()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "georaeplan-cache-voucher-probe-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("93333333-3333-3333-3333-333333333333");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "전표유형 검증 거래처",
                NameMatchKey = "VOUCHERTYPECHECK",
                TradeType = "매출/매입",
                IsDeleted = false
            });

            foreach (var voucherType in new[]
                     {
                         VoucherType.Sales,
                         VoucherType.Purchase,
                         VoucherType.Procurement,
                         VoucherType.Expense,
                         VoucherType.Collection
                     })
            {
                db.Invoices.Add(new LocalInvoice
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    VoucherType = voucherType,
                    InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
                    IsLatestVersion = true,
                    IsConfirmed = true,
                    IsDeleted = false
                });
            }

            await db.SaveChangesAsync();

            Assert.False(await service.HasLikelyCorruptedPrimaryWorkCacheAsync(session));

            db.Invoices.Add(new LocalInvoice
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = (VoucherType)999,
                InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false
            });
            await db.SaveChangesAsync();

            Assert.True(await service.HasLikelyCorruptedPrimaryWorkCacheAsync(session));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RentalAssetViewModel_BuildEditableAssetOfficeCodes_PreservesReadableSelectedOffice()
    {
        var result = InvokePrivateStatic<IReadOnlyList<string>>(
            typeof(RentalAssetViewModel),
            "BuildEditableAssetOfficeCodes",
            new object?[]
            {
                new[] { OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu },
                OfficeCodeCatalog.All.ToArray(),
                new string?[] { OfficeCodeCatalog.Itworld }
            });

        Assert.Contains(OfficeCodeCatalog.Itworld, result);
    }

    [Fact]
    public void RentalAssetViewModel_BuildOfficeDisplayOptions_AddsCatalogFallbackOffice()
    {
        var result = InvokePrivateStatic<IReadOnlyList<DisplayOption>>(
            typeof(RentalAssetViewModel),
            "BuildOfficeDisplayOptions",
            new object?[]
            {
                Array.Empty<LocalOffice>(),
                new[] { OfficeCodeCatalog.Itworld }
            });

        var option = Assert.Single(result);
        Assert.Equal(OfficeCodeCatalog.Itworld, option.Value);
        Assert.Equal(OfficeCodeCatalog.Itworld, option.DisplayName);
    }

    [Fact]
    public void RentalBillingViewModel_BuildEditableBillingOfficeCodes_PreservesReadableSelectedOffice()
    {
        var result = InvokePrivateStatic<IReadOnlyList<string>>(
            typeof(RentalBillingViewModel),
            "BuildEditableBillingOfficeCodes",
            new object?[]
            {
                new[] { OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu },
                OfficeCodeCatalog.All.ToArray(),
                new string?[] { OfficeCodeCatalog.Itworld }
            });

        Assert.Contains(OfficeCodeCatalog.Itworld, result);
    }

    [Fact]
    public void RentalBillingViewModel_ResolveProfileOfficeCode_CanonicalizesMixedItworldScope()
    {
        var profile = new LocalRentalBillingProfile
        {
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ManagementCompanyCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet
        };

        var officeCode = InvokePrivateStatic<string>(
            typeof(RentalBillingViewModel),
            "ResolveProfileOfficeCode",
            profile,
            OfficeCodeCatalog.Usenet);

        Assert.Equal(OfficeCodeCatalog.Itworld, officeCode);
    }

    [Fact]
    public async Task LocalStateService_UpsertCustomer_SynchronizesLinkedRentalCustomerSnapshots()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-linked-customer-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("9aaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var profileId = Guid.Parse("9bbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            var aliasProfileId = Guid.Parse("9ccccccc-cccc-cccc-cccc-cccccccccccc");
            var assetId = Guid.Parse("9ddddddd-dddd-dddd-dddd-dddddddddddd");
            var profileLinkedAssetId = Guid.Parse("9eeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
            var currentHistoryId = Guid.Parse("91111111-1111-1111-1111-111111111111");
            var profileLinkedHistoryId = Guid.Parse("92222222-2222-2222-2222-222222222222");
            var pastHistoryId = Guid.Parse("93333333-3333-3333-3333-333333333333");
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Old Customer",
                NameMatchKey = "OLDCUSTOMER",
                BusinessNumber = "OLD-BIZ",
                Email = "old@example.test",
                Revision = 3,
                IsDirty = false
            });
            db.RentalBillingProfiles.AddRange(
                new LocalRentalBillingProfile
                {
                    Id = profileId,
                    CustomerId = customerId,
                    CustomerName = "Old Customer",
                    BusinessNumber = "OLD-BIZ",
                    Email = "profile-old@example.test",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "PROFILE-OLD",
                    ItemName = "Rental Line",
                    IsDirty = false
                },
                new LocalRentalBillingProfile
                {
                    Id = aliasProfileId,
                    CustomerId = customerId,
                    CustomerName = "Billing Alias",
                    BusinessNumber = "ALIAS-BIZ",
                    Email = "alias@example.test",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "PROFILE-ALIAS",
                    ItemName = "Rental Line",
                    IsDirty = false
                });
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = assetId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementId = "ASSET-OLD",
                    ManagementNumber = "2605-001",
                    ItemName = "Rental Asset",
                    CustomerName = "Old Customer",
                    CurrentCustomerName = "Old Customer",
                    IsDirty = false
                },
                new LocalRentalAsset
                {
                    Id = profileLinkedAssetId,
                    BillingProfileId = profileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementId = "ASSET-PROFILE-LINKED",
                    ManagementNumber = "2605-002",
                    ItemName = "Profile Linked Rental Asset",
                    CustomerName = "Old Profile Customer",
                    CurrentCustomerName = "Old Profile Customer",
                    IsDirty = false
                });
            db.RentalAssetAssignmentHistories.AddRange(
                new LocalRentalAssetAssignmentHistory
                {
                    Id = currentHistoryId,
                    AssetId = assetId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    CustomerName = "Old Customer",
                    ItemName = "Rental Asset",
                    ManagementNumber = "2605-001",
                    IsCurrent = true,
                    IsDirty = false
                },
                new LocalRentalAssetAssignmentHistory
                {
                    Id = profileLinkedHistoryId,
                    AssetId = profileLinkedAssetId,
                    BillingProfileId = profileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    CustomerName = "Old Profile Customer",
                    BillingProfileDisplay = "PROFILE-OLD",
                    ItemName = "Profile Linked Rental Asset",
                    ManagementNumber = "2605-002",
                    IsCurrent = true,
                    IsDirty = false
                },
                new LocalRentalAssetAssignmentHistory
                {
                    Id = pastHistoryId,
                    AssetId = assetId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    CustomerName = "Past Customer Snapshot",
                    ItemName = "Past Rental Asset",
                    ManagementNumber = "2605-PAST",
                    IsCurrent = false,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), CreateAdminSession());
            var result = await service.UpsertCustomerAsync(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "New Customer",
                NameMatchKey = "NEWCUSTOMER",
                BusinessNumber = "NEW-BIZ",
                Email = "new@example.test",
                Revision = 3
            }, CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var syncedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == profileId);
            var aliasProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == aliasProfileId);
            var syncedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            var syncedProfileLinkedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == profileLinkedAssetId);
            var syncedCurrentHistory = await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().SingleAsync(history => history.Id == currentHistoryId);
            var syncedProfileLinkedHistory = await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().SingleAsync(history => history.Id == profileLinkedHistoryId);
            var preservedPastHistory = await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().SingleAsync(history => history.Id == pastHistoryId);

            foreach (var profile in new[] { syncedProfile, aliasProfile })
            {
                Assert.Equal("New Customer", profile.CustomerName);
                Assert.Equal("NEW-BIZ", profile.BusinessNumber);
                Assert.Equal("new@example.test", profile.Email);
                Assert.Equal(OfficeCodeCatalog.Yeonsu, profile.ResponsibleOfficeCode);
                Assert.Equal(OfficeCodeCatalog.Usenet, profile.OfficeCode);
                Assert.Equal(OfficeCodeCatalog.Usenet, profile.ManagementCompanyCode);
                Assert.Equal(TenantScopeCatalog.UsenetGroup, profile.TenantCode);
                Assert.True(profile.IsDirty);
            }

            Assert.Equal("New Customer", syncedAsset.CustomerName);
            Assert.Equal("New Customer", syncedAsset.CurrentCustomerName);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, syncedAsset.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, syncedAsset.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, syncedAsset.ManagementCompanyCode);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, syncedAsset.TenantCode);
            Assert.True(syncedAsset.IsDirty);

            Assert.Equal("New Customer", syncedProfileLinkedAsset.CustomerName);
            Assert.Equal("New Customer", syncedProfileLinkedAsset.CurrentCustomerName);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, syncedProfileLinkedAsset.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, syncedProfileLinkedAsset.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, syncedProfileLinkedAsset.ManagementCompanyCode);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, syncedProfileLinkedAsset.TenantCode);
            Assert.True(syncedProfileLinkedAsset.IsDirty);

            foreach (var history in new[] { syncedCurrentHistory, syncedProfileLinkedHistory })
            {
                Assert.Equal("New Customer", history.CustomerName);
                Assert.Equal(OfficeCodeCatalog.Yeonsu, history.ResponsibleOfficeCode);
                Assert.Equal(TenantScopeCatalog.UsenetGroup, history.TenantCode);
                Assert.True(history.IsDirty);
            }

            Assert.Equal("Past Customer Snapshot", preservedPastHistory.CustomerName);
            Assert.Equal(OfficeCodeCatalog.Usenet, preservedPastHistory.ResponsibleOfficeCode);
            Assert.False(preservedPastHistory.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_UpsertItem_RenameSynchronizesLinkedRentalAssetItemDisplays()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-linked-item-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.ItemCategoryOptions.AddRange(
                new LocalItemCategoryOption
                {
                    Id = Guid.Parse("9eeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                    Name = "Old Category",
                    SortOrder = 10,
                    IsActive = true,
                    IsDirty = false
                },
                new LocalItemCategoryOption
                {
                    Id = Guid.Parse("9fffffff-ffff-ffff-ffff-ffffffffffff"),
                    Name = "New Category",
                    SortOrder = 20,
                    IsActive = true,
                    IsDirty = false
                });

            var itemId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
            var assetId = Guid.Parse("a2222222-2222-2222-2222-222222222222");
            var aliasAssetId = Guid.Parse("a3333333-3333-3333-3333-333333333333");
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Old Item",
                NameMatchKey = "OLDITEM",
                CategoryName = "Old Category",
                ItemKind = ItemKinds.Asset,
                TrackingType = ItemTrackingTypes.Asset,
                Revision = 5,
                IsDirty = false
            });
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = assetId,
                    ItemId = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementId = "ASSET-ITEM",
                    ManagementNumber = "2605-002",
                    ItemName = "Old Item",
                    ItemCategoryName = "Old Category",
                    IsDirty = false
                },
                new LocalRentalAsset
                {
                    Id = aliasAssetId,
                    ItemId = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementId = "ASSET-ALIAS",
                    ManagementNumber = "2605-003",
                    ItemName = "Custom Item Alias",
                    ItemCategoryName = "Custom Category",
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), CreateAdminSession());
            await service.UpsertItemAsync(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "New Item",
                NameMatchKey = "NEWITEM",
                CategoryName = "New Category",
                ItemKind = ItemKinds.Asset,
                TrackingType = ItemTrackingTypes.Asset,
                Revision = 5
            }, CreateAdminSession(), OfficeCodeCatalog.Usenet);

            var syncedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            var aliasAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == aliasAssetId);
            Assert.Equal("New Item", syncedAsset.ItemName);
            Assert.Equal("New Category", syncedAsset.ItemCategoryName);
            Assert.True(syncedAsset.IsDirty);
            Assert.Equal("Custom Item Alias", aliasAsset.ItemName);
            Assert.Equal("Custom Category", aliasAsset.ItemCategoryName);
            Assert.False(aliasAsset.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }


    [Fact]
    public async Task RentalStateService_RepairRentalCatalogLinks_DoesNotLinkSameTenantCustomerFromDifferentOffice()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-catalog-customer-office-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.Parse("c1111111-1111-1111-1111-111111111111");
            var assetId = Guid.Parse("c2222222-2222-2222-2222-222222222222");
            var yeonsuCustomerId = Guid.Parse("c3333333-3333-3333-3333-333333333333");

            db.ItemCategoryOptions.Add(new LocalItemCategoryOption
            {
                Id = Guid.NewGuid(),
                Name = "Scoped Category",
                SortOrder = 10,
                IsActive = true,
                IsDirty = false
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Office Scoped Copier",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("Office Scoped Copier"),
                CategoryName = "Scoped Category",
                ItemKind = ItemKinds.Asset,
                TrackingType = ItemTrackingTypes.Asset,
                MaterialNumber = "US-001",
                SerialNumber = "US-SN-001",
                IsRental = true,
                IsSale = false,
                SimpleMemo = RentalStateService.AutoCreatedRentalItemMemo,
                IsDirty = false,
                IsDeleted = false
            });
            db.Customers.Add(new LocalCustomer
            {
                Id = yeonsuCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "Shared Office Customer",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("Shared Office Customer"),
                IsDirty = false,
                IsDeleted = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                ItemId = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ManagementNumber = "US-001",
                MachineNumber = "US-SN-001",
                ItemCategoryName = "Scoped Category",
                ItemName = "Office Scoped Copier",
                CustomerName = "Shared Office Customer",
                CurrentCustomerName = "Shared Office Customer",
                AssetStatus = RentalAssetStatusNormalizer.Active,
                IsDirty = false,
                IsDeleted = false
            });
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).RepairRentalCatalogLinksAsync([assetId]);

            Assert.Equal(1, result.ScannedAssetCount);
            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);
            Assert.Null(asset.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Usenet, asset.ResponsibleOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_RepairRentalCatalogLinks_ClearsExistingCustomerLinkWhenOfficeDiffers()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-catalog-customer-office-clear-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.Parse("c4444444-4444-4444-4444-444444444444");
            var assetId = Guid.Parse("c5555555-5555-5555-5555-555555555555");
            var yeonsuCustomerId = Guid.Parse("c6666666-6666-6666-6666-666666666666");

            db.ItemCategoryOptions.Add(new LocalItemCategoryOption
            {
                Id = Guid.NewGuid(),
                Name = "Scoped Category",
                SortOrder = 10,
                IsActive = true,
                IsDirty = false
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Office Scoped Existing Copier",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("Office Scoped Existing Copier"),
                CategoryName = "Scoped Category",
                ItemKind = ItemKinds.Asset,
                TrackingType = ItemTrackingTypes.Asset,
                MaterialNumber = "US-002",
                SerialNumber = "US-SN-002",
                IsRental = true,
                IsSale = false,
                SimpleMemo = RentalStateService.AutoCreatedRentalItemMemo,
                IsDirty = false,
                IsDeleted = false
            });
            db.Customers.Add(new LocalCustomer
            {
                Id = yeonsuCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "Existing Office Customer",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("Existing Office Customer"),
                IsDirty = false,
                IsDeleted = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                CustomerId = yeonsuCustomerId,
                ItemId = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ManagementNumber = "US-002",
                MachineNumber = "US-SN-002",
                ItemCategoryName = "Scoped Category",
                ItemName = "Office Scoped Existing Copier",
                CustomerName = "Existing Office Customer",
                CurrentCustomerName = "Existing Office Customer",
                AssetStatus = RentalAssetStatusNormalizer.Active,
                IsDirty = false,
                IsDeleted = false
            });
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).RepairRentalCatalogLinksAsync([assetId]);

            Assert.Equal(1, result.ScannedAssetCount);
            Assert.Equal(1, result.UpdatedAssetCount);
            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);
            Assert.Null(asset.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Usenet, asset.ResponsibleOfficeCode);
            Assert.True(asset.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }


    [Fact]
    public async Task RentalStateService_RepairRentalCatalogLinks_ExplicitAssetIdsDoNotRetireOtherOfficeOrphanItems()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-catalog-orphan-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetItemId = Guid.Parse("c7777777-7777-7777-7777-777777777777");
            var usenetAssetId = Guid.Parse("c8888888-8888-8888-8888-888888888888");
            var yeonsuOrphanItemId = Guid.Parse("c9999999-9999-9999-9999-999999999999");

            db.ItemCategoryOptions.Add(new LocalItemCategoryOption
            {
                Id = Guid.NewGuid(),
                Name = "Scoped Category",
                SortOrder = 10,
                IsActive = true,
                IsDirty = false
            });
            db.Items.AddRange(
                new LocalItem
                {
                    Id = usenetItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "Scoped Explicit Copier",
                    NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("Scoped Explicit Copier"),
                    CategoryName = "Scoped Category",
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    MaterialNumber = "US-003",
                    SerialNumber = "US-SN-003",
                    IsRental = true,
                    IsSale = false,
                    SimpleMemo = RentalStateService.AutoCreatedRentalItemMemo,
                    IsDirty = false,
                    IsDeleted = false
                },
                new LocalItem
                {
                    Id = yeonsuOrphanItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Yeonsu,
                    NameOriginal = "Other Office Orphan Copier",
                    NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("Other Office Orphan Copier"),
                    CategoryName = "Scoped Category",
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    MaterialNumber = "YS-ORPHAN",
                    SerialNumber = "YS-SN-ORPHAN",
                    IsRental = true,
                    IsSale = false,
                    SimpleMemo = RentalStateService.AutoCreatedRentalItemMemo,
                    IsDirty = false,
                    IsDeleted = false
                });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = usenetAssetId,
                ItemId = usenetItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ManagementNumber = "US-003",
                MachineNumber = "US-SN-003",
                ItemCategoryName = "Scoped Category",
                ItemName = "Scoped Explicit Copier",
                CustomerName = "Scoped Customer",
                CurrentCustomerName = "Scoped Customer",
                AssetStatus = RentalAssetStatusNormalizer.Active,
                IsDirty = false,
                IsDeleted = false
            });
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).RepairRentalCatalogLinksAsync([usenetAssetId]);

            Assert.Equal(1, result.ScannedAssetCount);
            var otherOfficeItem = await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == yeonsuOrphanItemId);
            Assert.False(otherOfficeItem.IsDeleted);
            Assert.False(otherOfficeItem.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }


    [Fact]
    public async Task RentalStateService_RepairRentalCatalogLinks_WithSessionSkipsOutOfScopeAssetIds()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-catalog-session-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetItemId = Guid.Parse("ca111111-1111-1111-1111-111111111111");
            var yeonsuItemId = Guid.Parse("ca222222-2222-2222-2222-222222222222");
            var usenetAssetId = Guid.Parse("ca333333-3333-3333-3333-333333333333");
            var yeonsuAssetId = Guid.Parse("ca444444-4444-4444-4444-444444444444");

            db.ItemCategoryOptions.Add(new LocalItemCategoryOption
            {
                Id = Guid.NewGuid(),
                Name = "Scoped Category",
                SortOrder = 10,
                IsActive = true,
                IsDirty = false
            });
            db.Items.AddRange(
                new LocalItem
                {
                    Id = usenetItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "Session Scope US Copier",
                    NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("Session Scope US Copier"),
                    CategoryName = "Scoped Category",
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    MaterialNumber = "US-004",
                    SerialNumber = "US-SN-004",
                    IsRental = true,
                    IsSale = false,
                    IsDirty = false,
                    IsDeleted = false
                },
                new LocalItem
                {
                    Id = yeonsuItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Yeonsu,
                    NameOriginal = "Session Scope YS Copier",
                    NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("Session Scope YS Copier"),
                    CategoryName = "Scoped Category",
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    MaterialNumber = "YS-004",
                    SerialNumber = "YS-SN-004",
                    IsRental = true,
                    IsSale = false,
                    IsDirty = false,
                    IsDeleted = false
                });
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = usenetAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementNumber = "US-004",
                    MachineNumber = "US-SN-004",
                    ItemCategoryName = "Scoped Category",
                    ItemName = "Session Scope US Copier",
                    CustomerName = "Session Scope Customer",
                    CurrentCustomerName = "Session Scope Customer",
                    AssetStatus = RentalAssetStatusNormalizer.Active,
                    IsDirty = false,
                    IsDeleted = false
                },
                new LocalRentalAsset
                {
                    Id = yeonsuAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementNumber = "YS-004",
                    MachineNumber = "YS-SN-004",
                    ItemCategoryName = "Scoped Category",
                    ItemName = "Session Scope YS Copier",
                    CustomerName = "Session Scope Customer",
                    CurrentCustomerName = "Session Scope Customer",
                    AssetStatus = RentalAssetStatusNormalizer.Active,
                    IsDirty = false,
                    IsDeleted = false
                });
            await db.SaveChangesAsync();

            var session = CreateUserSession(AppPermissionNames.RentalAssetEdit);
            var result = await new RentalStateService(db).RepairRentalCatalogLinksAsync(
                [usenetAssetId, yeonsuAssetId],
                session);

            Assert.Equal(1, result.ScannedAssetCount);
            var usenetAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == usenetAssetId);
            var yeonsuAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == yeonsuAssetId);
            Assert.Equal(usenetItemId, usenetAsset.ItemId);
            Assert.True(usenetAsset.IsDirty);
            Assert.Null(yeonsuAsset.ItemId);
            Assert.False(yeonsuAsset.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_RepairRentalCatalogLinks_BatchesExplicitAssetIds()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-catalog-repair-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetIds = new List<Guid>();
            for (var index = 0; index < 650; index++)
            {
                var itemId = Guid.NewGuid();
                var assetId = Guid.NewGuid();
                var itemName = $"Batch Repair Copier {index:D4}";
                var managementNumber = $"BR-{index:D4}";
                var serialNumber = $"BRSN-{index:D4}";
                assetIds.Add(assetId);

                db.Items.Add(new LocalItem
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = itemName,
                    NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(itemName),
                    CategoryName = "A3컬러복합기",
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    MaterialNumber = managementNumber,
                    SerialNumber = serialNumber,
                    IsRental = true,
                    IsDirty = false,
                    IsDeleted = false
                });

                db.RentalAssets.Add(new LocalRentalAsset
                {
                    Id = assetId,
                    ItemId = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementNumber = managementNumber,
                    MachineNumber = serialNumber,
                    ItemCategoryName = "A3컬러복합기",
                    ItemName = itemName,
                    CustomerName = $"Batch Customer {index:D4}",
                    CurrentCustomerName = $"Batch Customer {index:D4}",
                    AssetStatus = "임대진행중",
                    IsDirty = false,
                    IsDeleted = false
                });
            }

            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).RepairRentalCatalogLinksAsync(assetIds);

            Assert.Equal(assetIds.Count, result.ScannedAssetCount);
            Assert.Equal(assetIds.Count, result.UpdatedAssetCount);
            Assert.Equal(assetIds.Count, await db.RentalAssets.CountAsync(asset => asset.ItemId.HasValue));
            Assert.Equal(
                assetIds.OrderBy(id => id),
                await db.RentalAssets
                    .OrderBy(asset => asset.Id)
                    .Select(asset => asset.Id)
                    .ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_RepairRentalCatalogLinks_DoesNotReuseSameNameItemWhenAssetIdentifiersDiffer()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-item-identifier-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.ItemCategoryOptions.Add(new LocalItemCategoryOption
            {
                Id = Guid.NewGuid(),
                Name = "A3컬러복합기",
                SortOrder = 10,
                IsActive = true,
                IsDirty = false
            });

            var wrongItemId = Guid.Parse("b1111111-1111-1111-1111-111111111111");
            var assetId = Guid.Parse("b2222222-2222-2222-2222-222222222222");
            db.Items.Add(new LocalItem
            {
                Id = wrongItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "SL-X6300LX",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("SL-X6300LX"),
                CategoryName = "A3컬러복합기",
                ItemKind = ItemKinds.Asset,
                TrackingType = ItemTrackingTypes.Asset,
                MaterialNumber = "2401-008",
                SerialNumber = "ZPV9BJLW70002HE",
                SimpleMemo = "렌탈 자산/설치현황 자동 동기화 생성",
                IsRental = true,
                IsDirty = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                ItemId = wrongItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ManagementNumber = "2503-004",
                MachineNumber = "ZPV9BJSY30000VX",
                ItemCategoryName = "A3컬러복합기",
                ItemName = "SL-X6300LX",
                CustomerName = "법률사무소 리엘파트너스",
                CurrentCustomerName = "법률사무소 리엘파트너스",
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).RepairRentalCatalogLinksAsync();

            Assert.Equal(1, result.ScannedAssetCount);
            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);
            Assert.NotEqual(wrongItemId, asset.ItemId);
            Assert.True(asset.IsDirty);

            var correctedItem = await db.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == asset.ItemId);
            Assert.Equal("2503-004", correctedItem.MaterialNumber);
            Assert.Equal("ZPV9BJSY30000VX", correctedItem.SerialNumber);
            Assert.True(correctedItem.IsDirty);

            var wrongItem = await db.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == wrongItemId);
            Assert.True(wrongItem.IsDeleted);
            Assert.False(wrongItem.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_RepairRentalCatalogLinks_RetiresUnreferencedAutoCreatedItems()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-item-orphan-retire-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var orphanItemId = Guid.Parse("b3333333-3333-3333-3333-333333333333");
            var normalItemId = Guid.Parse("b4444444-4444-4444-4444-444444444444");
            db.Items.AddRange(
                new LocalItem
                {
                    Id = orphanItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "MF-4750",
                    NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("MF-4750"),
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    MaterialNumber = "1302-004",
                    SerialNumber = "ONXL03504",
                    SimpleMemo = "렌탈 자산/설치현황 자동 동기화 생성",
                    IsRental = true,
                    IsDirty = false
                },
                new LocalItem
                {
                    Id = normalItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "정상 품목",
                    NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("정상 품목"),
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    IsRental = false,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).RepairRentalCatalogLinksAsync();

            Assert.Equal(0, result.ScannedAssetCount);
            var orphanItem = await db.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == orphanItemId);
            var normalItem = await db.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == normalItemId);
            Assert.True(orphanItem.IsDeleted);
            Assert.False(orphanItem.IsDirty);
            Assert.False(normalItem.IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_RepairRentalCatalogLinks_BatchesOrphanedAutoCreatedItemRetirement()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-item-orphan-batch-retire-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var orphanItemIds = new List<Guid>();
            for (var index = 0; index < 650; index++)
            {
                var itemId = Guid.NewGuid();
                orphanItemIds.Add(itemId);
                var itemName = $"Orphan Rental Item {index:D4}";
                db.Items.Add(new LocalItem
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = itemName,
                    NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(itemName),
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    MaterialNumber = $"OR-{index:D4}",
                    SerialNumber = $"ORSN-{index:D4}",
                    SimpleMemo = RentalStateService.AutoCreatedRentalItemMemo,
                    IsRental = true,
                    IsDirty = false,
                    IsDeleted = false
                });
            }

            var normalItemId = Guid.NewGuid();
            db.Items.Add(new LocalItem
            {
                Id = normalItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "정상 재고 품목",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("정상 재고 품목"),
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                IsRental = false,
                IsDirty = false,
                IsDeleted = false
            });
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).RepairRentalCatalogLinksAsync();

            Assert.Equal(0, result.ScannedAssetCount);
            Assert.Equal(
                orphanItemIds.Count,
                await db.Items
                    .IgnoreQueryFilters()
                    .CountAsync(item => orphanItemIds.Contains(item.Id) && item.IsDeleted));
            var normalItem = await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == normalItemId);
            Assert.False(normalItem.IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_SaveAsset_AdminCanSaveItworldAssetScope()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-asset-itworld-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            });

            var service = new RentalStateService(db);
            var assetId = Guid.NewGuid();
            var result = await service.SaveAssetAsync(new LocalRentalAsset
            {
                Id = assetId,
                ManagementId = "IT-001",
                ManagementNumber = "2604-001",
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                CurrentLocation = "ITWORLD warehouse",
                ItemName = "Rental asset",
                CustomerName = "ITWORLD customer",
                CurrentCustomerName = "ITWORLD customer",
                InstallSiteName = "ITWORLD customer",
                InstallLocation = "ITWORLD customer",
                AssetStatus = "임대진행중",
                BillingEligibilityStatus = "미확인"
            }, session);

            Assert.True(result.Success, result.Message);

            var persisted = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            Assert.Equal(OfficeCodeCatalog.Itworld, persisted.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, persisted.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, persisted.OfficeCode);
            Assert.Equal(TenantScopeCatalog.Itworld, persisted.TenantCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_GetAssetLinkCandidates_ExpandedScopeIncludesCrossTenantAssets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-link-candidates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetOwnedAssetId = Guid.Parse("81111111-1111-1111-1111-111111111111");
            var yeonsuAssetId = Guid.Parse("82222222-2222-2222-2222-222222222222");
            var itworldAssetId = Guid.Parse("82333333-3333-3333-3333-333333333333");
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = usenetOwnedAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    AssetKey = "TEST:USENET-SN",
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementNumber = "U-001",
                    MachineNumber = "USENET-SN",
                    ItemName = "SL-M3820ND",
                    CustomerName = "연수구 보건소[보건행정과]",
                    CurrentCustomerName = "연수구 보건소[보건행정과]",
                    AssetStatus = "임대진행중",
                    BillingEligibilityStatus = "미확인"
                },
                new LocalRentalAsset
                {
                    Id = yeonsuAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    AssetKey = "TEST:YEONSU-SN",
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ManagementNumber = "Y-001",
                    MachineNumber = "YEONSU-SN",
                    ItemName = "SL-M3820ND",
                    CustomerName = "연수구 보건소[보건행정과]",
                    CurrentCustomerName = "연수구 보건소[보건행정과]",
                    AssetStatus = "임대진행중",
                    BillingEligibilityStatus = "미확인"
                },
                new LocalRentalAsset
                {
                    Id = itworldAssetId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    AssetKey = "TEST:ITWORLD-SN",
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    ManagementNumber = "IT-001",
                    MachineNumber = "ITWORLD-SN",
                    ItemName = "SL-M3820ND",
                    CustomerName = "연수구 보건소[보건행정과]",
                    CurrentCustomerName = "연수구 보건소[보건행정과]",
                    AssetStatus = "임대진행중",
                    BillingEligibilityStatus = "미확인"
                });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var service = new RentalStateService(db);

            var currentOfficeOnly = await service.GetAssetLinkCandidatesAsync(
                null,
                null,
                "연수구 보건소[보건행정과]",
                OfficeCodeCatalog.Yeonsu,
                session,
                includeOtherOfficeAssets: false);
            var expanded = await service.GetAssetLinkCandidatesAsync(
                null,
                null,
                "연수구 보건소[보건행정과]",
                OfficeCodeCatalog.Yeonsu,
                session,
                includeOtherOfficeAssets: true);
            var itworldExpanded = await service.GetAssetLinkCandidatesAsync(
                null,
                null,
                "연수구 보건소[보건행정과]",
                OfficeCodeCatalog.Itworld,
                session,
                includeOtherOfficeAssets: true);

            Assert.DoesNotContain(currentOfficeOnly, candidate => candidate.Source.Id == usenetOwnedAssetId);
            Assert.Contains(currentOfficeOnly, candidate => candidate.Source.Id == yeonsuAssetId);
            Assert.Contains(expanded, candidate => candidate.Source.Id == itworldAssetId);
            Assert.Contains(itworldExpanded, candidate => candidate.Source.Id == itworldAssetId);

            var expandedUsenetAsset = Assert.Single(expanded, candidate => candidate.Source.Id == usenetOwnedAssetId);
            Assert.True(expandedUsenetAsset.IsOutsideCurrentOffice);
            Assert.Equal("USENET", expandedUsenetAsset.ManagementCompanyName);
            Assert.Equal("USENET", expandedUsenetAsset.AssetScopeDisplay);

            var expandedItworldAsset = Assert.Single(expanded, candidate => candidate.Source.Id == itworldAssetId);
            Assert.True(expandedItworldAsset.IsOutsideCurrentOffice);
            Assert.Equal("ITWORLD", expandedItworldAsset.ManagementCompanyName);
            Assert.Equal("ITWORLD", expandedItworldAsset.AssetScopeDisplay);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_SaveBillingProfile_CanTransferUsenetOwnedAssetToYeonsuBillingWithoutChangingOwner()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-link-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("83333333-3333-3333-3333-333333333333");
            var assetId = Guid.Parse("84444444-4444-4444-4444-444444444444");
            var customerId = Guid.Parse("85555555-5555-5555-5555-555555555555");
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "TEST:USENET-TRANSFER-SN",
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementNumber = "U-TRANSFER-001",
                MachineNumber = "USENET-TRANSFER-SN",
                ItemName = "SL-M3820ND",
                CustomerName = "기존 거래처",
                CurrentCustomerName = "기존 거래처",
                InstallSiteName = "기존 거래처",
                InstallLocation = "기존 위치",
                MonthlyFee = 30_000m,
                AssetStatus = "임대진행중",
                BillingEligibilityStatus = "미확인"
            });
            await db.SaveChangesAsync();

            var templateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    ItemId = Guid.Parse("86666666-6666-6666-6666-666666666666"),
                    DisplayItemName = "SL-M3820ND",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 30_000m,
                    Amount = 30_000m,
                    IncludedAssetIds = [assetId]
                }
            });
            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                CustomerId = customerId,
                CustomerName = "연수구 보건소[보건행정과]",
                InstallSiteName = "연수구 보건소[보건행정과]",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = templateJson,
                MonthlyAmount = 30_000m
            };

            var result = await new RentalStateService(db).SaveBillingProfileAsync(
                profile,
                CreateAdminSession(),
                [
                    new RentalBillingAssetLinkEdit
                    {
                        AssetId = assetId,
                        CustomerId = customerId,
                        CustomerName = "연수구 보건소[보건행정과]",
                        InstallLocation = "보건행정과",
                        InstallSiteName = "연수구 보건소[보건행정과]",
                        MonthlyFee = 30_000m,
                        Notes = "link test"
                    }
                ]);

            Assert.True(result.Success, result.Message);

            var persistedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, persistedProfile.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, persistedProfile.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, persistedProfile.OfficeCode);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, persistedProfile.TenantCode);

            var persistedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);
            Assert.Equal(profileId, persistedAsset.BillingProfileId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, persistedAsset.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, persistedAsset.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, persistedAsset.OfficeCode);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, persistedAsset.TenantCode);

            var history = Assert.Single(await db.RentalAssetAssignmentHistories.Where(current => current.AssetId == assetId).ToListAsync());
            Assert.True(history.IsCurrent);
            Assert.Equal(profileId, history.BillingProfileId);
            Assert.Equal(customerId, history.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, history.ResponsibleOfficeCode);
            Assert.Equal("청구대상", persistedAsset.BillingEligibilityStatus);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }


    [Fact]
    public async Task RentalStateService_SaveBillingProfile_RemovingIncludedAssetClosesAssignmentHistoryWithoutDeletingAsset()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-history-unlink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("8a111111-1111-1111-1111-111111111111");
            var assetId = Guid.Parse("8a222222-2222-2222-2222-222222222222");
            var customerId = Guid.Parse("8a333333-3333-3333-3333-333333333333");
            var itemId = Guid.Parse("8a444444-4444-4444-4444-444444444444");
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "TEST:HISTORY-SN",
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementNumber = "H-001",
                MachineNumber = "HISTORY-SN",
                ItemName = "Printer",
                CustomerName = "Old Customer",
                CurrentCustomerName = "Old Customer",
                InstallLocation = "Old Location",
                AssetStatus = "\uC784\uB300\uC9C4\uD589\uC911",
                BillingEligibilityStatus = "\uBBF8\uD655\uC778"
            });
            await db.SaveChangesAsync();

            var linkedTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    ItemId = itemId,
                    DisplayItemName = "Printer",
                    BillingLineMode = "\uBB36\uC74C",
                    Quantity = 1m,
                    UnitPrice = 40_000m,
                    Amount = 40_000m,
                    IncludedAssetIds = [assetId]
                }
            });
            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                CustomerId = customerId,
                CustomerName = "Customer A",
                InstallSiteName = "Office 1",
                BillingType = "\uBB36\uC74C",
                BillingAdvanceMode = "\uD6C4\uBD88",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = linkedTemplateJson,
                MonthlyAmount = 40_000m
            };

            var service = new RentalStateService(db);
            var linkResult = await service.SaveBillingProfileAsync(
                profile,
                CreateAdminSession(),
                [
                    new RentalBillingAssetLinkEdit
                    {
                        AssetId = assetId,
                        CustomerId = customerId,
                        CustomerName = "Customer A",
                        InstallLocation = "Office 1",
                        MonthlyFee = 40_000m
                    }
                ]);
            Assert.True(linkResult.Success, linkResult.Message);

            db.ChangeTracker.Clear();
            var unlinkedTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    ItemId = itemId,
                    DisplayItemName = "Printer",
                    BillingLineMode = "\uBB36\uC74C",
                    Quantity = 1m,
                    UnitPrice = 40_000m,
                    Amount = 40_000m
                }
            });
            var unlinkProfile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                CustomerId = customerId,
                CustomerName = "Customer A",
                InstallSiteName = "Office 1",
                BillingType = "\uBB36\uC74C",
                BillingAdvanceMode = "\uD6C4\uBD88",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = unlinkedTemplateJson,
                MonthlyAmount = 40_000m
            };

            var unlinkResult = await new RentalStateService(db).SaveBillingProfileAsync(
                unlinkProfile,
                CreateAdminSession(),
                Array.Empty<RentalBillingAssetLinkEdit>());
            Assert.True(unlinkResult.Success, unlinkResult.Message);

            var persistedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);
            Assert.Null(persistedAsset.BillingProfileId);
            Assert.Null(persistedAsset.CustomerId);
            Assert.Equal("Customer A", persistedAsset.CurrentCustomerName);
            Assert.Equal("Customer A", persistedAsset.LastCustomerName);
            Assert.NotNull(persistedAsset.LastAssignmentClearedAtUtc);

            var history = Assert.Single(await db.RentalAssetAssignmentHistories.Where(current => current.AssetId == assetId).ToListAsync());
            Assert.False(history.IsCurrent);
            Assert.Equal(profileId, history.BillingProfileId);
            Assert.NotNull(history.UnlinkedAtUtc);

            var rows = await new RentalStateService(db).GetAssetAssignmentHistoriesAsync(assetId, CreateAdminSession());
            var row = Assert.Single(rows);
            Assert.False(row.IsCurrent);
            Assert.Equal("Customer A", row.CustomerName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_SaveBillingProfile_RelinkingSameAssetReusesEndedAssignmentHistory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-history-relink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("8b111111-1111-1111-1111-111111111111");
            var assetId = Guid.Parse("8b222222-2222-2222-2222-222222222222");
            var customerId = Guid.Parse("8b333333-3333-3333-3333-333333333333");
            var itemId = Guid.Parse("8b444444-4444-4444-4444-444444444444");
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "TEST:HISTORY-RELINK-SN",
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementNumber = "H-RELINK-001",
                MachineNumber = "HISTORY-RELINK-SN",
                ItemName = "Printer",
                CustomerName = "Old Customer",
                CurrentCustomerName = "Old Customer",
                InstallLocation = "Old Location",
                InstallDate = new DateOnly(2026, 1, 15),
                AssetStatus = "임대진행중",
                BillingEligibilityStatus = "미확인"
            });
            await db.SaveChangesAsync();

            static string BuildTemplateJson(Guid itemId, Guid? includedAssetId)
                => JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        ItemId = itemId,
                        DisplayItemName = "Printer",
                        BillingLineMode = "묶음",
                        Quantity = 1m,
                        UnitPrice = 40_000m,
                        Amount = 40_000m,
                        IncludedAssetIds = includedAssetId.HasValue
                            ? new List<Guid> { includedAssetId.Value }
                            : new List<Guid>()
                    }
                });

            LocalRentalBillingProfile BuildProfile(string templateJson) => new()
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                CustomerId = customerId,
                CustomerName = "Customer A",
                InstallSiteName = "Office 1",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = templateJson,
                MonthlyAmount = 40_000m
            };

            RentalBillingAssetLinkEdit BuildLinkEdit() => new()
            {
                AssetId = assetId,
                CustomerId = customerId,
                CustomerName = "Customer A",
                InstallLocation = "Office 1",
                MonthlyFee = 40_000m
            };

            var service = new RentalStateService(db);
            var linkResult = await service.SaveBillingProfileAsync(
                BuildProfile(BuildTemplateJson(itemId, assetId)),
                CreateAdminSession(),
                [BuildLinkEdit()]);
            Assert.True(linkResult.Success, linkResult.Message);

            var unlinkResult = await service.SaveBillingProfileAsync(
                BuildProfile(BuildTemplateJson(itemId, null)),
                CreateAdminSession(),
                Array.Empty<RentalBillingAssetLinkEdit>());
            Assert.True(unlinkResult.Success, unlinkResult.Message);

            var relinkResult = await service.SaveBillingProfileAsync(
                BuildProfile(BuildTemplateJson(itemId, assetId)),
                CreateAdminSession(),
                [BuildLinkEdit()]);
            Assert.True(relinkResult.Success, relinkResult.Message);

            var histories = await db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .Where(current => current.AssetId == assetId)
                .ToListAsync();
            var history = Assert.Single(histories);
            Assert.True(history.IsCurrent);
            Assert.Null(history.UnlinkedAtUtc);
            Assert.Equal(profileId, history.BillingProfileId);
            Assert.Equal(customerId, history.CustomerId);
            Assert.Equal("Customer A", history.CustomerName);
            Assert.Equal("Office 1", history.InstallLocation);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_SaveBillingProfile_AllowsCrossTenantAssetReferenceWithoutTransfer()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-link-cross-tenant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("87777777-7777-7777-7777-777777777777");
            var assetId = Guid.Parse("88888888-8888-8888-8888-888888888888");
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                AssetKey = "TEST:ITWORLD-TRANSFER-SN",
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementNumber = "IT-TRANSFER-001",
                MachineNumber = "ITWORLD-TRANSFER-SN",
                ItemName = "SL-M3820ND",
                CustomerName = "아이티월드 거래처",
                CurrentCustomerName = "아이티월드 거래처",
                AssetStatus = "임대진행중",
                BillingEligibilityStatus = "미확인"
            });
            await db.SaveChangesAsync();

            var templateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    ItemId = Guid.Parse("89999999-9999-9999-9999-999999999999"),
                    DisplayItemName = "SL-M3820ND",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 30_000m,
                    Amount = 30_000m,
                    IncludedAssetIds = [assetId]
                }
            });
            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                CustomerName = "연수구 보건소[보건행정과]",
                InstallSiteName = "연수구 보건소[보건행정과]",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingTemplateJson = templateJson,
                MonthlyAmount = 30_000m
            };

            var result = await new RentalStateService(db).SaveBillingProfileAsync(
                profile,
                CreateAdminSession(),
                [
                    new RentalBillingAssetLinkEdit
                    {
                        AssetId = assetId,
                        CustomerName = "연수구 보건소[보건행정과]",
                        MonthlyFee = 30_000m
                    }
                ]);

            Assert.True(result.Success, result.Message);

            var persistedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);
            Assert.Null(persistedAsset.BillingProfileId);
            Assert.Equal(OfficeCodeCatalog.Itworld, persistedAsset.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, persistedAsset.ManagementCompanyCode);
            Assert.Equal(TenantScopeCatalog.Itworld, persistedAsset.TenantCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void LocalIntegrityReport_BuildSummaryText_AndToMarkdown_IncludeKeySignals()
    {
        var report = new LocalIntegrityReport(
            new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc),
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.UsenetGroup,
            2,
            pendingServerMirrorRefresh: true,
            [
                new LocalIntegrityIssue("sync_outbox_failed_pending", "Error", 3, "실패 상태의 sync outbox가 남아 있습니다."),
                new LocalIntegrityIssue("out_of_scope_items", "Warning", 1, "현재 계정 범위 밖 품목 캐시가 남아 있습니다.")
            ]);

        var summary = report.BuildSummaryText(maxIssues: 1);
        var markdown = report.ToMarkdown();

        Assert.Contains("버전 변경 후 중앙 서버 기준 전체 재동기화가 대기 중입니다.", summary, StringComparison.Ordinal);
        Assert.Contains("실패 상태의 sync outbox가 남아 있습니다. (3건)", summary, StringComparison.Ordinal);
        Assert.Contains("그 외 1개 항목은 무결성 리포트에서 확인하세요.", summary, StringComparison.Ordinal);
        Assert.Contains("현재 미동기화 변경 2건이 있어", summary, StringComparison.Ordinal);

        Assert.Contains("# 무결성 점검 리포트", markdown, StringComparison.Ordinal);
        Assert.Contains("sync_outbox_failed_pending", markdown, StringComparison.Ordinal);
        Assert.Contains("out_of_scope_items", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalStateService_BuildIntegrityReport_ScopesRentalOrphansByTenant()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-integrity-tenant-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetCustomerId = Guid.Parse("91411111-1111-1111-1111-111111111111");
            var usenetItemId = Guid.Parse("91422222-2222-2222-2222-222222222222");
            var missingItworldCustomerId = Guid.Parse("91433333-3333-3333-3333-333333333333");
            var missingItworldItemId = Guid.Parse("91444444-4444-4444-4444-444444444444");

            db.Customers.Add(new LocalCustomer
            {
                Id = usenetCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "USENET Customer",
                NameMatchKey = "USENETCUSTOMER",
                IsDirty = false
            });
            db.Items.Add(new LocalItem
            {
                Id = usenetItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "USENET Item",
                NameMatchKey = "USENETITEM",
                SpecificationOriginal = "A4",
                SpecificationMatchKey = "A4",
                IsDirty = false
            });
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = Guid.Parse("91455555-5555-5555-5555-555555555555"),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    AssetKey = "USENET|OK|ASSET",
                    CustomerId = usenetCustomerId,
                    ItemId = usenetItemId,
                    CustomerName = "USENET Customer",
                    CurrentCustomerName = "USENET Customer",
                    ItemName = "USENET Item",
                    MachineNumber = "USENET-OK",
                    IsDirty = false
                },
                new LocalRentalAsset
                {
                    Id = Guid.Parse("91466666-6666-6666-6666-666666666666"),
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    AssetKey = "ITWORLD|ORPHAN|ASSET",
                    CustomerId = missingItworldCustomerId,
                    ItemId = missingItworldItemId,
                    CustomerName = "ITWORLD Customer",
                    CurrentCustomerName = "ITWORLD Customer",
                    ItemName = "ITWORLD Item",
                    MachineNumber = "ITWORLD-ORPHAN",
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var usenetSession = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), usenetSession);
            var usenetReport = await service.BuildIntegrityReportAsync(usenetSession);
            var itworldReport = await service.BuildIntegrityReportAsync(CreateItworldAdminSession());

            Assert.DoesNotContain(usenetReport.Issues, issue => issue.Code == "orphan_rental_asset_customer_refs");
            Assert.DoesNotContain(usenetReport.Issues, issue => issue.Code == "orphan_rental_asset_item_refs");

            Assert.Contains(itworldReport.Issues, issue =>
                issue.Code == "orphan_rental_asset_customer_refs" &&
                issue.Count == 1);
            Assert.Contains(itworldReport.Issues, issue =>
                issue.Code == "orphan_rental_asset_item_refs" &&
                issue.Count == 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_BuildIntegrityReport_YeonsuLoginExcludesUsenetOnlyRentalOrphans()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-integrity-yeonsu-office-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = Guid.Parse("92811111-1111-1111-1111-111111111111"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET|ORPHAN|ASSET",
                CustomerId = Guid.Parse("92822222-2222-2222-2222-222222222222"),
                ItemId = Guid.Parse("92833333-3333-3333-3333-333333333333"),
                CustomerName = "USENET Only Customer",
                CurrentCustomerName = "USENET Only Customer",
                ItemName = "USENET Only Item",
                MachineNumber = "USENET-ORPHAN",
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), CreateYeonsuAdminSession());
            var yeonsuReport = await service.BuildIntegrityReportAsync(CreateYeonsuAdminSession());
            var usenetReport = await service.BuildIntegrityReportAsync(CreateAdminSession());

            Assert.DoesNotContain(yeonsuReport.Issues, issue => issue.Code == "orphan_rental_asset_customer_refs");
            Assert.DoesNotContain(yeonsuReport.Issues, issue => issue.Code == "orphan_rental_asset_item_refs");
            Assert.Contains(usenetReport.Issues, issue => issue.Code == "orphan_rental_asset_customer_refs" && issue.Count == 1);
            Assert.Contains(usenetReport.Issues, issue => issue.Code == "orphan_rental_asset_item_refs" && issue.Count == 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_BuildIntegrityReport_UsenetLoginExcludesYeonsuOnlyRentalOrphans()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-integrity-usenet-office-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = Guid.Parse("92911111-1111-1111-1111-111111111111"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ManagementCompanyCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                AssetKey = "YEONSU|ORPHAN|ASSET",
                CustomerId = Guid.Parse("92922222-2222-2222-2222-222222222222"),
                ItemId = Guid.Parse("92933333-3333-3333-3333-333333333333"),
                CustomerName = "YEONSU Only Customer",
                CurrentCustomerName = "YEONSU Only Customer",
                ItemName = "YEONSU Only Item",
                MachineNumber = "YEONSU-ORPHAN",
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), CreateAdminSession());
            var usenetReport = await service.BuildIntegrityReportAsync(CreateAdminSession());
            var yeonsuReport = await service.BuildIntegrityReportAsync(CreateYeonsuAdminSession());

            Assert.DoesNotContain(usenetReport.Issues, issue => issue.Code == "orphan_rental_asset_customer_refs");
            Assert.DoesNotContain(usenetReport.Issues, issue => issue.Code == "orphan_rental_asset_item_refs");
            Assert.Contains(yeonsuReport.Issues, issue => issue.Code == "orphan_rental_asset_customer_refs" && issue.Count == 1);
            Assert.Contains(yeonsuReport.Issues, issue => issue.Code == "orphan_rental_asset_item_refs" && issue.Count == 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RentalAssetLinkDialog_SelectionCheckbox_UpdatesSourceImmediately()
    {
        var xamlPath = Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "RentalAssetLinkDialog.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("DataGridCheckBoxColumn Header=\"선택\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsSelected, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DataIntegrityAlertWindow_ShowsOnlyFixActionButtons()
    {
        var xamlPath = Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "DataIntegrityAlertWindow.xaml");

        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("자세히 보기", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("전체 자세히 보기", xaml, StringComparison.Ordinal);
        Assert.Contains("수정 화면 열기", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DataIntegrityIssueWindow_FixActionIsHandledWithoutClosingDetailWindow()
    {
        var root = FindRepositoryRoot();
        var issueWindowCode = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "DataIntegrityIssueWindow.xaml.cs"));
        var mainWindowCode = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.xaml.cs"));

        Assert.Contains("FixRequested", issueWindowCode, StringComparison.Ordinal);
        Assert.Contains("FixRequested.Invoke", issueWindowCode, StringComparison.Ordinal);
        Assert.Contains("win.FixRequested +=", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("OpenDataIntegrityFixTargetAsync(args.Issue, win)", mainWindowCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventoryViewModel_LoadAndSelectItemAsync_FillsSearchTextForTargetItem()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-inventory-select-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var targetItemId = Guid.Parse("b5100000-0000-0000-0000-000000000001");
            db.Items.AddRange(
                new LocalItem
                {
                    Id = targetItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "중복 테스트 토너",
                    NameMatchKey = "중복테스트토너",
                    SpecificationOriginal = "A4",
                    SpecificationMatchKey = "A4",
                    CategoryName = "소모품",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    IsDeleted = false,
                    IsDirty = false
                },
                new LocalItem
                {
                    Id = Guid.Parse("b5100000-0000-0000-0000-000000000002"),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "다른 테스트 용지",
                    NameMatchKey = "다른테스트용지",
                    SpecificationOriginal = "B5",
                    SpecificationMatchKey = "B5",
                    CategoryName = "소모품",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    IsDeleted = false,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var viewModel = new InventoryViewModel(local, session);

            await viewModel.LoadAndSelectItemAsync(targetItemId);

            Assert.Equal("중복 테스트 토너", viewModel.SearchText);
            Assert.NotNull(viewModel.SelectedItem);
            Assert.Equal(targetItemId, viewModel.SelectedItem.Id);
            Assert.All(viewModel.FilteredItems, row => Assert.Contains("중복 테스트 토너", row.NameOriginal, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static bool IsFinancialSummaryInvoice(
        VoucherType voucherType,
        bool isLatestVersion,
        bool isConfirmed,
        bool isDeleted)
        => InvokePrivateStatic<bool>(
            typeof(LocalStateService),
            "IsCustomerFinancialSummaryInvoice",
            new LocalInvoice
            {
                VoucherType = voucherType,
                IsLatestVersion = isLatestVersion,
                IsConfirmed = isConfirmed,
                IsDeleted = isDeleted
            });

    private static string ResolveInvoiceDefaultTransactionKind(VoucherType voucherType)
        => InvokePrivateStatic<string>(
            typeof(PaymentViewModel),
            "ResolveInvoiceDefaultTransactionKind",
            new LocalInvoice { VoucherType = voucherType });

    [Fact]
    public void ResolveScope_PrefersOfficeScopeOverTenantScope()
    {
        var result = InvokePrivateStatic<(string ScopeKey, string ScopeDisplayName)>(
            "ResolveScope",
            OfficeCodeCatalog.Itworld,
            TenantScopeCatalog.UsenetGroup);

        Assert.Equal("OFFICE:ITWORLD", result.ScopeKey);
        Assert.Equal("ITWORLD", result.ScopeDisplayName);
    }

    [Fact]
    public void ResolveOfficeCodeFromTenant_ReturnsRepresentativeOffice()
    {
        var officeCode = InvokePrivateStatic<string>(
            "ResolveOfficeCodeFromTenant",
            TenantScopeCatalog.UsenetGroup);

        Assert.Equal(OfficeCodeCatalog.Usenet, officeCode);
    }

    [Fact]
    public void NormalizeOutboxErrorMessage_TruncatesLongMessage_AndSuppliesDefault()
    {
        var longMessage = new string('x', 600);

        var truncated = InvokePrivateStatic<string>("NormalizeOutboxErrorMessage", longMessage);
        var defaultMessage = InvokePrivateStatic<string>("NormalizeOutboxErrorMessage", new object?[] { null });

        Assert.Equal(500, truncated.Length);
        Assert.Equal("동기화 확인이 필요한 상태가 발생했습니다.", defaultMessage);
    }

    [Fact]
    public void GetOutboxStatusWeight_UsesExpectedPriorityOrder()
    {
        var failed = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Failed");
        var prepared = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Prepared");
        var sent = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Sent");
        var acknowledged = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Acknowledged");
        var unknown = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Unknown");

        Assert.True(failed < prepared);
        Assert.True(prepared < sent);
        Assert.True(sent < acknowledged);
        Assert.True(acknowledged < unknown);
    }

    [Fact]
    public void SyncOutboxListItem_ComputedProperties_WorkAsExpected()
    {
        var item = new SyncOutboxListItem
        {
            EntityId = Guid.Empty,
            MutationId = new string('a', 40),
            Status = "Failed"
        };

        Assert.Equal("-", item.EntityIdText);
        Assert.Equal(39, item.ShortMutationId.Length);
        Assert.EndsWith("...", item.ShortMutationId, StringComparison.Ordinal);
        Assert.True(item.IsFailed);
        Assert.False(item.IsAcknowledged);
    }

    [Fact]
    public void RecycleBinEntry_AndDependencyModels_ComputedProperties_WorkAsExpected()
    {
        var localDeletedAt = new DateTime(2026, 4, 20, 13, 45, 0, DateTimeKind.Local);
        var entry = new RecycleBinEntry
        {
            EntityId = Guid.NewGuid(),
            Kind = RecycleBinEntityKind.InventoryTransfer,
            DeletedAtUtc = localDeletedAt.ToUniversalTime()
        };

        var dependency = new RecycleBinDependencyItem
        {
            Label = "전표",
            Count = 3
        };

        var candidate = new RecycleBinCustomerMergeCandidate
        {
            CustomerId = Guid.NewGuid(),
            Name = "거래처A",
            BusinessNumber = "",
            Phone = "010-1234-5678",
            ResponsibleOfficeCode = "ITWORLD"
        };

        Assert.Equal("재고이동", entry.KindText);
        Assert.Equal(localDeletedAt.ToString("yyyy-MM-dd HH:mm"), entry.DeletedAtLocalText);
        Assert.Equal("전표 3건", dependency.DisplayText);
        Assert.Equal("거래처A / 010-1234-5678 / ITWORLD", candidate.DisplayText);
    }

    [Fact]
    public void RecycleBinHelpers_NormalizeAndFormatAsExpected()
    {
        var joined = InvokePrivateStatic<string>(
            "JoinSegments",
            new object?[] { new string?[] { "  거래처A  ", null, " 010-1234-5678 ", " " } });
        var digits = InvokePrivateStatic<string>("NormalizeDigits", "사업자 123-45-67890 / 연락처 010-1111-2222");
        var voucher = InvokePrivateStatic<string>("GetVoucherTypeLabel", VoucherType.Sales);
        var fallbackKind = InvokePrivateStatic<string>("GetTransactionKindLabel", "  임의구분  ");
        var emptyKind = InvokePrivateStatic<string>("GetTransactionKindLabel", new object?[] { null });

        Assert.Equal("거래처A / 010-1234-5678", joined);
        Assert.Equal("123456789001011112222", digits);
        Assert.Equal("매출", voucher);
        Assert.Equal("임의구분", fallbackKind);
        Assert.Equal("거래내역", emptyKind);
    }

    [Fact]
    public void SyncEquivalentRevisionConflict_IgnoresFileContent_WhenMetadataMatches()
    {
        var id = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var now = new DateTime(2026, 4, 21, 2, 0, 0, DateTimeKind.Utc);
        var client = new CustomerContractDto
        {
            Id = id,
            CustomerId = customerId,
            ContractType = "RentalContract",
            FileName = "contract.pdf",
            MimeType = "application/pdf",
            FileSize = 3,
            FileHash = "ABC123",
            UploadedByUsername = "tester",
            UploadedAtUtc = now,
            FileContent = [1, 2, 3],
            CreatedAtUtc = now.AddMinutes(-5),
            UpdatedAtUtc = now,
            Revision = 10,
            ExpectedRevision = 10
        };
        var server = new CustomerContractDto
        {
            Id = id,
            CustomerId = customerId,
            ContractType = client.ContractType,
            FileName = client.FileName,
            MimeType = client.MimeType,
            FileSize = client.FileSize,
            FileHash = client.FileHash,
            UploadedByUsername = client.UploadedByUsername,
            UploadedAtUtc = client.UploadedAtUtc,
            FileContent = [],
            CreatedAtUtc = client.CreatedAtUtc,
            UpdatedAtUtc = now.AddSeconds(30),
            Revision = 11
        };

        var conflict = new ConflictLogDto
        {
            EntityName = "CustomerContract",
            EntityId = id.ToString("D"),
            Reason = "Expected revision mismatch. client=10, server=11",
            ClientJson = JsonSerializer.Serialize(client),
            ServerJson = JsonSerializer.Serialize(server)
        };

        var isEquivalent = InvokePrivateStatic<bool>(
            typeof(SyncService),
            "IsEquivalentRevisionConflict",
            conflict);

        Assert.True(isEquivalent);
    }

    [Fact]
    public void SyncService_HasOperationalRows_DetectsEmptyMirrorPull()
    {
        var emptyPull = new SyncPullResponse
        {
            CurrentServerRevision = 1234
        };

        var hasEmptyOperationalRows = InvokePrivateStatic<bool>(
            typeof(SyncService),
            "HasOperationalRows",
            emptyPull);

        Assert.False(hasEmptyOperationalRows);

        emptyPull.Customers.Add(new CustomerDto
        {
            Id = Guid.NewGuid(),
            NameOriginal = "거래처",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet
        });

        var hasCustomerOperationalRows = InvokePrivateStatic<bool>(
            typeof(SyncService),
            "HasOperationalRows",
            emptyPull);

        Assert.True(hasCustomerOperationalRows);
    }

    [Fact]
    public async Task SyncService_PrepareRentalBillingProfileRevisionRetry_RebasesRevisionAndRequeuesOutbox()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-rental-profile-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("71111111-1111-1111-1111-111111111111");
            var customerId = Guid.Parse("72222222-2222-2222-2222-222222222222");
            var localAssetAId = Guid.Parse("73333333-3333-3333-3333-333333333333");
            var localAssetBId = Guid.Parse("74444444-4444-4444-4444-444444444444");
            var profileOnlyAssetId = Guid.Parse("74444444-4444-4444-4444-444444444445");
            var staleServerAssetId = Guid.Parse("75555555-5555-5555-5555-555555555555");
            var templateItemId = Guid.Parse("76666666-6666-6666-6666-666666666666");
            var localRevision = 200L;
            var serverRevision = 150L;
            var updatedAtUtc = new DateTime(2026, 4, 23, 11, 22, 27, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:sync-test";

            var canonicalTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    ItemId = templateItemId,
                    DisplayItemName = "IMC2000",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 600000m,
                    Amount = 600000m,
                    IncludedAssetIds = [localAssetAId, localAssetBId]
                }
            });
            var staleServerTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    ItemId = templateItemId,
                    DisplayItemName = "IMC2000",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 600000m,
                    Amount = 600000m,
                    IncludedAssetIds = [staleServerAssetId, localAssetAId]
                }
            });

            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                CustomerName = "미추홀구 주안2동행정복지센터",
                BusinessNumber = "131-83-00632",
                ItemName = "IMC2000",
                BillingType = "묶음",
                InstallSiteName = "미추홀구 주안2동행정복지센터",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingDayMode = "고정일",
                BillingCycleMonths = 36,
                BillingAnchorMonth = 3,
                DocumentIssueMode = "결제일과 동일",
                MonthlyAmount = 600000m,
                OutstandingAmount = 600000m,
                BillingStatus = "보류",
                CompletionStatus = "미완료",
                SettlementStatus = "미입금",
                RequiresFollowUp = true,
                ProfileKey = "USENET|CUSTOMER:test|묶음|후불|25|36||",
                BillingTemplateJson = canonicalTemplateJson,
                Revision = localRevision,
                UpdatedAtUtc = updatedAtUtc,
                IsDirty = true
            };
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = localAssetAId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    BillingProfileId = profileId,
                    AssetKey = "USENET|A-001|IMC2000",
                    CustomerId = customerId,
                    CustomerName = profile.CustomerName,
                    CurrentCustomerName = profile.CustomerName,
                    InstallSiteName = profile.InstallSiteName,
                    InstallLocation = profile.InstallSiteName,
                    ItemName = "IMC2000",
                    ManagementNumber = "A-001",
                    AssetStatus = "임대진행중",
                    IsDirty = false
                },
                new LocalRentalAsset
                {
                    Id = localAssetBId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    BillingProfileId = profileId,
                    AssetKey = "USENET|A-002|IMC2000",
                    CustomerId = customerId,
                    CustomerName = profile.CustomerName,
                    CurrentCustomerName = profile.CustomerName,
                    InstallSiteName = profile.InstallSiteName,
                    InstallLocation = profile.InstallSiteName,
                    ItemName = "IMC2000",
                    ManagementNumber = "A-002",
                    AssetStatus = "임대진행중",
                    IsDirty = false
                },
                new LocalRentalAsset
                {
                    Id = profileOnlyAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    BillingProfileId = profileId,
                    AssetKey = "USENET|A-003|IMC2000",
                    CustomerId = customerId,
                    CustomerName = profile.CustomerName,
                    CurrentCustomerName = profile.CustomerName,
                    InstallSiteName = profile.InstallSiteName,
                    InstallLocation = profile.InstallSiteName,
                    ItemName = "IMC2000",
                    ManagementNumber = "A-003",
                    AssetStatus = "임대진행중",
                    IsDirty = false
                });

            var clientSnapshot = LocalMappings.ToDto(profile);
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalRentalBillingProfile),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalRentalBillingProfile),
                EntityId = profileId,
                ExpectedRevision = localRevision,
                TenantCode = profile.TenantCode,
                OfficeCode = profile.OfficeCode,
                ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(profile);
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-5);
            serverSnapshot.BillingTemplateJson = staleServerTemplateJson;

            var conflict = new ConflictLogDto
            {
                EntityName = "RentalBillingProfile",
                EntityId = profileId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var repaired = await InvokePrivateInstanceAsync<bool>(
                sync,
                "TryPrepareRentalBillingProfileRevisionRetryAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(repaired);

            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);
            var outboxRows = await db.SyncOutboxEntries.AsNoTracking()
                .Where(entry => entry.EntityName == nameof(LocalRentalBillingProfile) && entry.EntityId == profileId)
                .ToListAsync();

            Assert.Equal(serverRevision, storedProfile.Revision);
            Assert.True(storedProfile.IsDirty);
            Assert.Equal(canonicalTemplateJson, storedProfile.BillingTemplateJson);
            var storedTemplateItems = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(storedProfile.BillingTemplateJson) ?? [];
            var storedIncludedAssetIds = Assert.Single(storedTemplateItems).IncludedAssetIds;
            Assert.Equal([localAssetAId, localAssetBId], storedIncludedAssetIds);
            Assert.DoesNotContain(profileOnlyAssetId, storedIncludedAssetIds);

            var rebasedDto = LocalMappings.ToDto(storedProfile);
            rebasedDto.ExpectedRevision = serverRevision;
            var expectedMutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalRentalBillingProfile),
                rebasedDto);

            var outboxRow = Assert.Single(outboxRows);
            Assert.Equal(expectedMutationId, outboxRow.MutationId);
            Assert.Equal(serverRevision, outboxRow.ExpectedRevision);
            Assert.Equal("Prepared", outboxRow.Status);
            Assert.True(string.IsNullOrWhiteSpace(outboxRow.ErrorMessage));
            Assert.Null(outboxRow.SentAtUtc);
            Assert.Null(outboxRow.AcknowledgedAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_PrepareGenericRevisionRetry_RebasesRentalBillingProfileAndRequeuesOutbox()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-generic-rental-profile-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("77771111-1111-1111-1111-111111111111");
            var customerId = Guid.Parse("77772222-2222-2222-2222-222222222222");
            var localRevision = 100L;
            var serverRevision = 130L;
            var updatedAtUtc = new DateTime(2026, 6, 2, 13, 5, 0, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:sync-test";

            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                CustomerName = "연수구 시설안전관리공단",
                BusinessNumber = "131-83-00122",
                ItemName = "사무기기 렌탈대금",
                BillingType = "묶음",
                InstallSiteName = "송도5동 2층",
                BillingAdvanceMode = "후불",
                BillingMethod = "전자세금계산서",
                BillingStatus = "보류",
                SettlementStatus = "미입금",
                CompletionStatus = "미완료",
                BillingDay = 20,
                BillingDayMode = "고정일",
                BillingCycleMonths = 1,
                BillingAnchorMonth = 5,
                DocumentIssueMode = "결제일과 동일",
                MonthlyAmount = 150_000m,
                OutstandingAmount = 150_000m,
                Notes = "로컬에서 월요금과 메모를 수정",
                ProfileKey = "USENET|CUSTOMER:77772222|묶음|후불|20|1||",
                BillingTemplateJson = "[]",
                BillingRunsJson = "[]",
                CreatedAtUtc = updatedAtUtc.AddMonths(-1),
                UpdatedAtUtc = updatedAtUtc,
                Revision = localRevision,
                IsDirty = true
            };
            db.RentalBillingProfiles.Add(profile);

            var clientSnapshot = LocalMappings.ToDto(profile);
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalRentalBillingProfile),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalRentalBillingProfile),
                EntityId = profileId,
                ExpectedRevision = localRevision,
                TenantCode = profile.TenantCode,
                OfficeCode = profile.OfficeCode,
                ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(profile);
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-10);
            serverSnapshot.MonthlyAmount = 120_000m;
            serverSnapshot.OutstandingAmount = 120_000m;
            serverSnapshot.Notes = "서버에 먼저 저장된 이전 메모";

            var conflict = new ConflictLogDto
            {
                EntityName = "RentalBillingProfile",
                EntityId = profileId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var prepared = await InvokePrivateInstanceAsync<List<ConflictLogDto>>(
                sync,
                "PrepareGenericRevisionRetriesAsync",
                new List<ConflictLogDto> { conflict },
                deviceId,
                session,
                CancellationToken.None);

            Assert.NotNull(prepared);
            Assert.Single(prepared!);

            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);
            var outboxRow = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.EntityName == nameof(LocalRentalBillingProfile) && entry.EntityId == profileId);

            Assert.Equal(serverRevision, storedProfile.Revision);
            Assert.True(storedProfile.IsDirty);
            Assert.Equal(150_000m, storedProfile.MonthlyAmount);
            Assert.Equal("로컬에서 월요금과 메모를 수정", storedProfile.Notes);
            Assert.Equal("Prepared", outboxRow.Status);
            Assert.Null(outboxRow.SentAtUtc);
            Assert.Equal(serverRevision, outboxRow.ExpectedRevision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_FlushPendingChangesAsync_RespectsCancellationWhenSyncIsAlreadyRunning()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-shutdown-sync-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateOnlineAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);
            var runningSync = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            typeof(SyncService)
                .GetField("_currentSyncTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(sync, runningSync.Task);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                sync.FlushPendingChangesAsync(cts.Token));

            runningSync.SetResult(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_ResolveItemWarehouseStockRevisionConflict_RebasesLocalNewerSnapshot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-stock-revision-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.Parse("77773333-3333-3333-3333-333333333333");
            const string warehouseCode = "USENET";
            var localRevision = 20L;
            var serverRevision = 25L;
            var updatedAtUtc = new DateTime(2026, 6, 2, 13, 30, 0, DateTimeKind.Utc);

            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = warehouseCode,
                Quantity = 3m,
                UpdatedAtUtc = updatedAtUtc,
                Revision = localRevision
            });
            await db.SaveChangesAsync();

            var clientSnapshot = new ItemWarehouseStockDto
            {
                ItemId = itemId,
                WarehouseCode = warehouseCode,
                Quantity = 3m,
                UpdatedAtUtc = updatedAtUtc,
                Revision = localRevision,
                ExpectedRevision = localRevision
            };
            var serverSnapshot = new ItemWarehouseStockDto
            {
                ItemId = itemId,
                WarehouseCode = warehouseCode,
                Quantity = 2m,
                UpdatedAtUtc = updatedAtUtc.AddMinutes(-10),
                Revision = serverRevision,
                ExpectedRevision = serverRevision
            };
            var conflict = new ConflictLogDto
            {
                EntityName = "ItemWarehouseStock",
                EntityId = $"{itemId:D}|{warehouseCode}",
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = CreateOnlineAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var resolved = await InvokePrivateInstanceAsync<List<ConflictLogDto>>(
                sync,
                "ResolveItemWarehouseStockRevisionConflictsAsync",
                new List<ConflictLogDto> { conflict },
                CancellationToken.None);

            Assert.NotNull(resolved);
            Assert.Single(resolved!);

            var storedStock = await db.ItemWarehouseStocks.AsNoTracking()
                .SingleAsync(current => current.ItemId == itemId && current.WarehouseCode == warehouseCode);
            Assert.Equal(serverRevision, storedStock.Revision);
            Assert.Equal(3m, storedStock.Quantity);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_PrepareCustomerRevisionRetry_RebasesRevisionAndRequeuesOutbox()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-customer-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("70222222-2222-2222-2222-222222222222");
            var localRevision = 200L;
            var serverRevision = 150L;
            var updatedAtUtc = new DateTime(2026, 5, 27, 10, 30, 0, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:sync-test";

            var customer = new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "연수구 연수1동행정복지센터",
                NameMatchKey = "연수구연수1동행정복지센터",
                TradeType = CustomerTradeTypes.Sales,
                BusinessNumber = "131-83-00122",
                Phone = "032-749-6185",
                Notes = "로컬에서 수정한 거래처 메모",
                CreatedAtUtc = updatedAtUtc.AddDays(-10),
                UpdatedAtUtc = updatedAtUtc,
                Revision = localRevision,
                IsDirty = true
            };
            db.Customers.Add(customer);

            var clientSnapshot = LocalMappings.ToDto(customer);
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalCustomer),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalCustomer),
                EntityId = customerId,
                ExpectedRevision = localRevision,
                TenantCode = customer.TenantCode,
                OfficeCode = customer.OfficeCode,
                ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(customer);
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-5);
            serverSnapshot.Phone = "032-000-0000";

            var conflict = new ConflictLogDto
            {
                EntityName = "Customer",
                EntityId = customerId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var prepared = await InvokePrivateInstanceAsync<bool>(
                sync,
                "TryPrepareCustomerRevisionRetryAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(prepared);

            var storedCustomer = await db.Customers.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == customerId);
            var outboxRow = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.EntityName == nameof(LocalCustomer) && entry.EntityId == customerId);

            Assert.Equal(serverRevision, storedCustomer.Revision);
            Assert.True(storedCustomer.IsDirty);
            Assert.Equal("로컬에서 수정한 거래처 메모", storedCustomer.Notes);
            Assert.Equal("Prepared", outboxRow.Status);
            Assert.Null(outboxRow.SentAtUtc);
            Assert.Equal(serverRevision, outboxRow.ExpectedRevision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_PrepareInvoiceRevisionRetry_RebasesRevisionAndRequeuesOutbox()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-invoice-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("71222222-2222-2222-2222-222222222222");
            var invoiceId = Guid.Parse("81333333-3333-3333-3333-333333333333");
            var lineId = Guid.Parse("91444444-4444-4444-4444-444444444444");
            var localRevision = 300L;
            var serverRevision = 350L;
            var updatedAtUtc = new DateTime(2026, 6, 2, 8, 30, 0, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:sync-test";

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "연수구 연수1동행정복지센터",
                NameMatchKey = "연수구연수1동행정복지센터",
                TradeType = CustomerTradeTypes.Sales,
                CreatedAtUtc = updatedAtUtc.AddDays(-30),
                UpdatedAtUtc = updatedAtUtc.AddDays(-30),
                Revision = 10
            });

            var invoice = new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "S-20260602-001",
                LocalTempNumber = "TMP-INV-001",
                VersionGroupId = invoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                SourceWarehouseCode = DomainConstants.WarehouseUsenetMain,
                InvoiceDate = new DateOnly(2026, 6, 2),
                TotalAmount = 110_000m,
                SupplyAmount = 100_000m,
                VatAmount = 10_000m,
                VatMode = InvoiceVatModes.Included,
                Memo = "로컬에서 수정한 전표 메모",
                CreatedAtUtc = updatedAtUtc.AddDays(-3),
                UpdatedAtUtc = updatedAtUtc,
                Revision = localRevision,
                IsDirty = true
            };
            invoice.Lines.Add(new LocalInvoiceLine
            {
                Id = lineId,
                InvoiceId = invoiceId,
                ItemNameOriginal = "사무기기 렌탈대금[6월]",
                SpecificationOriginal = "IMC2010",
                Unit = "EA",
                Quantity = 1m,
                UnitPrice = 110_000m,
                LineAmount = 110_000m,
                ItemTrackingType = ItemTrackingTypes.NonStock
            });
            db.Invoices.Add(invoice);

            var clientSnapshot = LocalMappings.ToDto(invoice);
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalInvoice),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalInvoice),
                EntityId = invoiceId,
                ExpectedRevision = localRevision,
                TenantCode = invoice.TenantCode,
                OfficeCode = invoice.OfficeCode,
                ResponsibleOfficeCode = invoice.ResponsibleOfficeCode,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(invoice);
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-5);
            serverSnapshot.Memo = "서버에 먼저 저장된 전표 메모";

            var conflict = new ConflictLogDto
            {
                EntityName = "Invoice",
                EntityId = invoiceId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var prepared = await InvokePrivateInstanceAsync<bool>(
                sync,
                "TryPrepareInvoiceRevisionRetryAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(prepared);

            var storedInvoice = await db.Invoices.IgnoreQueryFilters()
                .Include(current => current.Lines)
                .Include(current => current.Payments)
                .SingleAsync(current => current.Id == invoiceId);
            var outboxRow = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.EntityName == nameof(LocalInvoice) && entry.EntityId == invoiceId);

            Assert.Equal(serverRevision, storedInvoice.Revision);
            Assert.True(storedInvoice.IsDirty);
            Assert.Equal("로컬에서 수정한 전표 메모", storedInvoice.Memo);
            Assert.Equal("Prepared", outboxRow.Status);
            Assert.Null(outboxRow.SentAtUtc);
            Assert.Equal(serverRevision, outboxRow.ExpectedRevision);

            var rebasedSnapshot = LocalMappings.ToDto(storedInvoice);
            rebasedSnapshot.ExpectedRevision = serverRevision;
            rebasedSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            var expectedMutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalInvoice),
                rebasedSnapshot);
            Assert.Equal(expectedMutationId, outboxRow.MutationId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_PreparePaymentRevisionRetry_RebasesRevisionAndRequeuesOutbox()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-payment-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("72222222-2222-2222-2222-222222222222");
            var invoiceId = Guid.Parse("82333333-3333-3333-3333-333333333333");
            var paymentId = Guid.Parse("92444444-4444-4444-4444-444444444444");
            var localRevision = 410L;
            var serverRevision = 455L;
            var updatedAtUtc = new DateTime(2026, 6, 2, 9, 10, 0, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:sync-test";

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "연수구 연수1동행정복지센터",
                NameMatchKey = "연수구연수1동행정복지센터",
                TradeType = CustomerTradeTypes.Sales,
                CreatedAtUtc = updatedAtUtc.AddDays(-30),
                UpdatedAtUtc = updatedAtUtc.AddDays(-30),
                Revision = 10
            });

            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "S-20260602-002",
                VersionGroupId = invoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                SourceWarehouseCode = DomainConstants.WarehouseUsenetMain,
                InvoiceDate = new DateOnly(2026, 6, 2),
                TotalAmount = 440_000m,
                SupplyAmount = 400_000m,
                VatAmount = 40_000m,
                VatMode = InvoiceVatModes.Included,
                CreatedAtUtc = updatedAtUtc.AddDays(-3),
                UpdatedAtUtc = updatedAtUtc.AddDays(-3),
                Revision = 300
            });

            var payment = new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 2),
                Amount = 150_000m,
                Note = "로컬에서 수정한 수금 메모",
                CreatedAtUtc = updatedAtUtc.AddDays(-1),
                UpdatedAtUtc = updatedAtUtc,
                Revision = localRevision,
                IsDirty = true
            };
            db.Payments.Add(payment);

            var clientSnapshot = LocalMappings.ToDto(payment);
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalPayment),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalPayment),
                EntityId = paymentId,
                ExpectedRevision = localRevision,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(payment);
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-5);
            serverSnapshot.Note = "서버에 먼저 저장된 수금 메모";

            var conflict = new ConflictLogDto
            {
                EntityName = "Payment",
                EntityId = paymentId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var prepared = await InvokePrivateInstanceAsync<bool>(
                sync,
                "TryPreparePaymentRevisionRetryAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(prepared);

            var storedPayment = await db.Payments.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == paymentId);
            var outboxRow = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.EntityName == nameof(LocalPayment) && entry.EntityId == paymentId);

            Assert.Equal(serverRevision, storedPayment.Revision);
            Assert.True(storedPayment.IsDirty);
            Assert.Equal("로컬에서 수정한 수금 메모", storedPayment.Note);
            Assert.Equal("Prepared", outboxRow.Status);
            Assert.Null(outboxRow.SentAtUtc);
            Assert.Equal(serverRevision, outboxRow.ExpectedRevision);

            var rebasedSnapshot = LocalMappings.ToDto(storedPayment);
            rebasedSnapshot.ExpectedRevision = serverRevision;
            rebasedSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            var expectedMutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalPayment),
                rebasedSnapshot);
            Assert.Equal(expectedMutationId, outboxRow.MutationId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_PrepareTransactionRevisionRetry_RebasesRevisionAndRequeuesOutbox()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-transaction-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("73222222-2222-2222-2222-222222222222");
            var invoiceId = Guid.Parse("83333333-3333-3333-3333-333333333333");
            var transactionId = Guid.Parse("93444444-4444-4444-4444-444444444444");
            var localRevision = 510L;
            var serverRevision = 545L;
            var updatedAtUtc = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:sync-test";

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "연수구 연수1동행정복지센터",
                NameMatchKey = "연수구연수1동행정복지센터",
                TradeType = CustomerTradeTypes.Sales,
                CreatedAtUtc = updatedAtUtc.AddDays(-30),
                UpdatedAtUtc = updatedAtUtc.AddDays(-30),
                Revision = 10
            });

            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "S-20260602-003",
                VersionGroupId = invoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                SourceWarehouseCode = DomainConstants.WarehouseUsenetMain,
                InvoiceDate = new DateOnly(2026, 6, 2),
                TotalAmount = 440_000m,
                SupplyAmount = 400_000m,
                VatAmount = 40_000m,
                VatMode = InvoiceVatModes.Included,
                CreatedAtUtc = updatedAtUtc.AddDays(-3),
                UpdatedAtUtc = updatedAtUtc.AddDays(-3),
                Revision = 300
            });

            var transaction = new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 2),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoiceId,
                LinkedInvoiceNumber = "S-20260602-003",
                SettlementAmount = 150_000m,
                BankReceipt = 150_000m,
                ReceiptTotal = 150_000m,
                Note = "전표 수금",
                Memo = "로컬에서 수정한 거래내역 메모",
                CreatedAtUtc = updatedAtUtc.AddDays(-1),
                UpdatedAtUtc = updatedAtUtc,
                Revision = localRevision,
                IsDirty = true
            };
            db.Transactions.Add(transaction);

            var clientSnapshot = LocalMappings.ToDto(transaction);
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalTransaction),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalTransaction),
                EntityId = transactionId,
                ExpectedRevision = localRevision,
                TenantCode = transaction.TenantCode,
                OfficeCode = transaction.OfficeCode,
                ResponsibleOfficeCode = transaction.ResponsibleOfficeCode,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(transaction);
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-5);
            serverSnapshot.Memo = "서버에 먼저 저장된 거래내역 메모";

            var conflict = new ConflictLogDto
            {
                EntityName = "TransactionRecord",
                EntityId = transactionId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var prepared = await InvokePrivateInstanceAsync<bool>(
                sync,
                "TryPrepareTransactionRevisionRetryAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(prepared);

            var storedTransaction = await db.Transactions.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == transactionId);
            var outboxRow = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.EntityName == nameof(LocalTransaction) && entry.EntityId == transactionId);

            Assert.Equal(serverRevision, storedTransaction.Revision);
            Assert.True(storedTransaction.IsDirty);
            Assert.Equal("로컬에서 수정한 거래내역 메모", storedTransaction.Memo);
            Assert.Equal("Prepared", outboxRow.Status);
            Assert.Null(outboxRow.SentAtUtc);
            Assert.Equal(serverRevision, outboxRow.ExpectedRevision);

            var rebasedSnapshot = LocalMappings.ToDto(storedTransaction);
            rebasedSnapshot.ExpectedRevision = serverRevision;
            rebasedSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            var expectedMutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalTransaction),
                rebasedSnapshot);
            Assert.Equal(expectedMutationId, outboxRow.MutationId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_PrepareTransactionAttachmentRevisionRetry_RebasesRevisionAndRequeuesOutbox()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-transaction-attachment-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("74222222-2222-2222-2222-222222222222");
            var transactionId = Guid.Parse("84333333-3333-3333-3333-333333333333");
            var attachmentId = Guid.Parse("94444444-4444-4444-4444-444444444444");
            var localRevision = 610L;
            var serverRevision = 645L;
            var updatedAtUtc = new DateTime(2026, 6, 2, 10, 40, 0, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:sync-test";

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "연수구 연수1동행정복지센터",
                NameMatchKey = "연수구연수1동행정복지센터",
                TradeType = CustomerTradeTypes.Sales,
                CreatedAtUtc = updatedAtUtc.AddDays(-30),
                UpdatedAtUtc = updatedAtUtc.AddDays(-30),
                Revision = 10
            });

            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 2),
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                SettlementAmount = 150_000m,
                BankReceipt = 150_000m,
                ReceiptTotal = 150_000m,
                Note = "일반 수금",
                Memo = "거래내역",
                CreatedAtUtc = updatedAtUtc.AddDays(-1),
                UpdatedAtUtc = updatedAtUtc.AddDays(-1),
                Revision = 500
            });

            var attachment = new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = transactionId,
                AttachmentType = "통장사본",
                FileName = "receipt.txt",
                StoredFileName = "receipt.txt",
                StoredPath = "transactions/receipt.txt",
                MimeType = "text/plain",
                FileSize = 11,
                FileHash = "hash-local",
                Description = "로컬에서 수정한 증빙 설명",
                UploadedByUsername = "admin",
                UploadedAtUtc = updatedAtUtc.AddHours(-1),
                VerificationStatus = "미확인",
                SortOrder = 1,
                CreatedAtUtc = updatedAtUtc.AddHours(-2),
                UpdatedAtUtc = updatedAtUtc,
                Revision = localRevision,
                IsDirty = true
            };
            db.TransactionAttachments.Add(attachment);

            var clientSnapshot = LocalMappings.ToDto(attachment, Encoding.UTF8.GetBytes("hello world"));
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalTransactionAttachment),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalTransactionAttachment),
                EntityId = attachmentId,
                ExpectedRevision = localRevision,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(attachment, Encoding.UTF8.GetBytes("server copy"));
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-5);
            serverSnapshot.Description = "서버에 먼저 저장된 증빙 설명";

            var conflict = new ConflictLogDto
            {
                EntityName = "TransactionAttachment",
                EntityId = attachmentId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var prepared = await InvokePrivateInstanceAsync<bool>(
                sync,
                "TryPrepareTransactionAttachmentRevisionRetryAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(prepared);

            var storedAttachment = await db.TransactionAttachments.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == attachmentId);
            var outboxRow = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.EntityName == nameof(LocalTransactionAttachment) && entry.EntityId == attachmentId);

            Assert.Equal(serverRevision, storedAttachment.Revision);
            Assert.True(storedAttachment.IsDirty);
            Assert.Equal("로컬에서 수정한 증빙 설명", storedAttachment.Description);
            Assert.Equal("Prepared", outboxRow.Status);
            Assert.Null(outboxRow.SentAtUtc);
            Assert.Equal(serverRevision, outboxRow.ExpectedRevision);

            var rebasedSnapshot = LocalMappings.ToDto(storedAttachment);
            rebasedSnapshot.ExpectedRevision = serverRevision;
            rebasedSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            var expectedMutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalTransactionAttachment),
                rebasedSnapshot);
            Assert.Equal(expectedMutationId, outboxRow.MutationId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_PrepareInventoryTransferRevisionRetry_RebasesRevisionAndRequeuesOutbox()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-inventory-transfer-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.Parse("75222222-2222-2222-2222-222222222222");
            var transferId = Guid.Parse("85333333-3333-3333-3333-333333333333");
            var lineId = Guid.Parse("95444444-4444-4444-4444-444444444444");
            var localRevision = 710L;
            var serverRevision = 745L;
            var updatedAtUtc = new DateTime(2026, 6, 2, 11, 20, 0, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:sync-test";

            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "A3컬러복합기",
                NameMatchKey = "A3컬러복합기",
                SpecificationOriginal = "IMC2010",
                SpecificationMatchKey = "IMC2010",
                Unit = "EA",
                TrackingType = ItemTrackingTypes.Stock,
                ItemKind = "일반상품",
                CreatedAtUtc = updatedAtUtc.AddDays(-30),
                UpdatedAtUtc = updatedAtUtc.AddDays(-30),
                Revision = 20
            });

            var transfer = new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "MV-20260602-001",
                TransferDate = new DateOnly(2026, 6, 2),
                FromWarehouseCode = DomainConstants.WarehouseUsenetMain,
                ToWarehouseCode = DomainConstants.WarehouseYeonsuMain,
                Memo = "로컬에서 수정한 재고이동 메모",
                CreatedByUsername = "admin",
                LastSavedByUsername = "admin",
                LastSavedAtUtc = updatedAtUtc,
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                RequestedByUsername = "admin",
                RequestedAtUtc = updatedAtUtc.AddHours(-1),
                CreatedAtUtc = updatedAtUtc.AddDays(-1),
                UpdatedAtUtc = updatedAtUtc,
                Revision = localRevision,
                IsDirty = true
            };
            transfer.Lines.Add(new LocalInventoryTransferLine
            {
                Id = lineId,
                TransferId = transferId,
                ItemId = itemId,
                ItemNameOriginal = "A3컬러복합기",
                SpecificationOriginal = "IMC2010",
                Unit = "EA",
                Quantity = 1m,
                Remark = "이동"
            });
            db.InventoryTransfers.Add(transfer);

            var clientSnapshot = LocalMappings.ToDto(transfer);
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalInventoryTransfer),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalInventoryTransfer),
                EntityId = transferId,
                ExpectedRevision = localRevision,
                TenantCode = clientSnapshot.TenantCode,
                OfficeCode = clientSnapshot.SourceOfficeCode,
                ResponsibleOfficeCode = clientSnapshot.SourceOfficeCode,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(transfer);
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-5);
            serverSnapshot.Memo = "서버에 먼저 저장된 재고이동 메모";

            var conflict = new ConflictLogDto
            {
                EntityName = "InventoryTransfer",
                EntityId = transferId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var prepared = await InvokePrivateInstanceAsync<bool>(
                sync,
                "TryPrepareInventoryTransferRevisionRetryAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(prepared);

            var storedTransfer = await db.InventoryTransfers.IgnoreQueryFilters()
                .Include(current => current.Lines)
                .SingleAsync(current => current.Id == transferId);
            var outboxRow = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.EntityName == nameof(LocalInventoryTransfer) && entry.EntityId == transferId);

            Assert.Equal(serverRevision, storedTransfer.Revision);
            Assert.True(storedTransfer.IsDirty);
            Assert.Equal("로컬에서 수정한 재고이동 메모", storedTransfer.Memo);
            Assert.Equal("Prepared", outboxRow.Status);
            Assert.Null(outboxRow.SentAtUtc);
            Assert.Equal(serverRevision, outboxRow.ExpectedRevision);

            var rebasedSnapshot = LocalMappings.ToDto(storedTransfer);
            rebasedSnapshot.ExpectedRevision = serverRevision;
            rebasedSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            var expectedMutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalInventoryTransfer),
                rebasedSnapshot);
            Assert.Equal(expectedMutationId, outboxRow.MutationId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_DeleteInventoryTransfer_DeniesFinalStatusWhenTargetOfficeIsNotWritable()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-transfer-delete-final-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.Parse("b7100000-0000-0000-0000-000000000001");
            var transferId = Guid.Parse("b7200000-0000-0000-0000-000000000001");
            var lineId = Guid.Parse("b7300000-0000-0000-0000-000000000001");
            var now = new DateTime(2026, 6, 17, 8, 40, 0, DateTimeKind.Utc);

            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "삭제 권한 재고이동 품목",
                NameMatchKey = "삭제권한재고이동품목",
                Unit = "EA",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 10m,
                IsDirty = false
            });
            db.ItemWarehouseStocks.AddRange(
                new LocalItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 8m,
                    UpdatedAtUtc = now,
                    Revision = 10
                },
                new LocalItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    Quantity = 2m,
                    UpdatedAtUtc = now,
                    Revision = 11
                });
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferNumber = "TR-LOCAL-FINAL-DELETE-SCOPE",
                TransferDate = new DateOnly(2026, 6, 17),
                TransferStatus = InventoryTransferStatusNormalizer.Received,
                ReceivedByUsername = "yeonsu",
                ReceivedAtUtc = now,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now,
                Revision = 25,
                IsDirty = false,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = lineId,
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = "삭제 권한 재고이동 품목",
                        Unit = "EA",
                        Quantity = 2m,
                        ReceivedQuantity = 2m
                    }
                ]
            });
            await db.SaveChangesAsync();

            var session = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeOfficeOnly,
                AppPermissionNames.DeliveryEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var result = await service.DeleteInventoryTransferAsync(transferId, session, expectedRevision: 25);

            Assert.False(result.Success);
            Assert.Contains("도착지", result.Message);
            db.ChangeTracker.Clear();
            var stored = await db.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);
            Assert.False(stored.IsDeleted);
            Assert.False(stored.IsDirty);
            Assert.Equal(8m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(2m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_DeleteInventoryTransfer_AllowsPendingStatusWhenSourceOfficeIsWritable()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-transfer-delete-pending-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.Parse("b7100000-0000-0000-0000-000000000002");
            var transferId = Guid.Parse("b7200000-0000-0000-0000-000000000002");
            var lineId = Guid.Parse("b7300000-0000-0000-0000-000000000002");
            var now = new DateTime(2026, 6, 17, 8, 45, 0, DateTimeKind.Utc);

            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "대기 삭제 재고이동 품목",
                NameMatchKey = "대기삭제재고이동품목",
                Unit = "EA",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                IsDirty = false
            });
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferNumber = "TR-LOCAL-PENDING-DELETE-SCOPE",
                TransferDate = new DateOnly(2026, 6, 17),
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now,
                Revision = 30,
                IsDirty = false,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = lineId,
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = "대기 삭제 재고이동 품목",
                        Unit = "EA",
                        Quantity = 1m
                    }
                ]
            });
            await db.SaveChangesAsync();

            var session = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeOfficeOnly,
                AppPermissionNames.DeliveryEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var result = await service.DeleteInventoryTransferAsync(transferId, session, expectedRevision: 30);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var stored = await db.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);
            Assert.True(stored.IsDeleted);
            Assert.True(stored.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreInventoryTransfer_RebuildsStockSnapshots()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-transfer-restore-stock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("b7400000-0000-0000-0000-000000000001");
            var itemId = Guid.Parse("b7500000-0000-0000-0000-000000000001");
            var transferId = Guid.Parse("b7600000-0000-0000-0000-000000000001");
            var lineId = Guid.Parse("b7700000-0000-0000-0000-000000000001");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재고이동 복원 거래처",
                NameMatchKey = "재고이동복원거래처"
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재고이동 복원 품목",
                NameMatchKey = "재고이동복원품목",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                PurchasePrice = 1000m
            });
            await db.SaveChangesAsync();

            await service.SaveInvoiceAsync(new LocalInvoice
            {
                Id = Guid.Parse("b7800000-0000-0000-0000-000000000001"),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Purchase,
                PurchaseReceivingRequired = true,
                PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed,
                PurchaseReceivedAtUtc = new DateTime(2026, 6, 17, 8, 50, 0, DateTimeKind.Utc),
                PurchaseReceivedByUsername = "admin",
                InvoiceDate = new DateOnly(2026, 6, 17),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 복원 품목",
                        ItemTrackingType = ItemTrackingTypes.Stock,
                        Unit = "EA",
                        Quantity = 5m,
                        UnitPrice = 1000m,
                        LineAmount = 5000m
                    }
                }
            });

            var saveTransferResult = await service.SaveInventoryTransferAsync(new LocalInventoryTransfer
            {
                Id = transferId,
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferNumber = "TR-RESTORE-STOCK",
                TransferDate = new DateOnly(2026, 6, 17),
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                Lines =
                {
                    new LocalInventoryTransferLine
                    {
                        Id = lineId,
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 복원 품목",
                        Unit = "EA",
                        Quantity = 2m
                    }
                }
            }, session);
            Assert.True(saveTransferResult.Success, saveTransferResult.Message);
            Assert.Equal(3m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());

            var deleteResult = await service.DeleteInventoryTransferAsync(transferId, session);
            Assert.True(deleteResult.Success, deleteResult.Message);
            Assert.Equal(5m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());

            var restoreResult = await service.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.InventoryTransfer,
                transferId,
                session);

            Assert.True(restoreResult.Success, restoreResult.Message);
            Assert.Equal(3m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(3m, (await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId)).CurrentStock);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreInventoryTransfer_RejectsWhenSourceStockWouldBecomeNegative()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-transfer-restore-shortage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("b7400000-0000-0000-0000-000000000002");
            var itemId = Guid.Parse("b7500000-0000-0000-0000-000000000002");
            var transferId = Guid.Parse("b7600000-0000-0000-0000-000000000002");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재고이동 복원 부족 거래처",
                NameMatchKey = "재고이동복원부족거래처"
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재고이동 복원 부족 품목",
                NameMatchKey = "재고이동복원부족품목",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                PurchasePrice = 1000m
            });
            await db.SaveChangesAsync();

            await service.SaveInvoiceAsync(new LocalInvoice
            {
                Id = Guid.Parse("b7800000-0000-0000-0000-000000000002"),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Purchase,
                PurchaseReceivingRequired = true,
                PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed,
                PurchaseReceivedAtUtc = new DateTime(2026, 6, 17, 8, 55, 0, DateTimeKind.Utc),
                PurchaseReceivedByUsername = "admin",
                InvoiceDate = new DateOnly(2026, 6, 17),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 복원 부족 품목",
                        ItemTrackingType = ItemTrackingTypes.Stock,
                        Unit = "EA",
                        Quantity = 1m,
                        UnitPrice = 1000m,
                        LineAmount = 1000m
                    }
                }
            });

            var saveTransferResult = await service.SaveInventoryTransferAsync(new LocalInventoryTransfer
            {
                Id = transferId,
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferNumber = "TR-RESTORE-SHORTAGE",
                TransferDate = new DateOnly(2026, 6, 17),
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                Lines =
                {
                    new LocalInventoryTransferLine
                    {
                        Id = Guid.Parse("b7700000-0000-0000-0000-000000000002"),
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 복원 부족 품목",
                        Unit = "EA",
                        Quantity = 1m
                    }
                }
            }, session);
            Assert.True(saveTransferResult.Success, saveTransferResult.Message);

            var deleteResult = await service.DeleteInventoryTransferAsync(transferId, session);
            Assert.True(deleteResult.Success, deleteResult.Message);

            await service.SaveInvoiceAsync(new LocalInvoice
            {
                Id = Guid.Parse("b7900000-0000-0000-0000-000000000002"),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 18),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 복원 부족 품목",
                        ItemTrackingType = ItemTrackingTypes.Stock,
                        Unit = "EA",
                        Quantity = 1m,
                        UnitPrice = 1500m,
                        LineAmount = 1500m
                    }
                }
            });
            Assert.Equal(0m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());

            var restoreResult = await service.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.InventoryTransfer,
                transferId,
                session);

            Assert.False(restoreResult.Success);
            Assert.Contains("재고", restoreResult.Message);
            db.ChangeTracker.Clear();
            var transfer = await db.InventoryTransfers.IgnoreQueryFilters().SingleAsync(current => current.Id == transferId);
            Assert.True(transfer.IsDeleted);
            Assert.Equal(0m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgeInventoryTransfer_RebuildsStockSnapshots()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-transfer-purge-stock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("b7400000-0000-0000-0000-000000000003");
            var itemId = Guid.Parse("b7500000-0000-0000-0000-000000000003");
            var transferId = Guid.Parse("b7600000-0000-0000-0000-000000000003");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재고이동 purge 거래처",
                NameMatchKey = "재고이동purge거래처"
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재고이동 purge 품목",
                NameMatchKey = "재고이동purge품목",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                PurchasePrice = 1000m
            });
            await db.SaveChangesAsync();

            await service.SaveInvoiceAsync(new LocalInvoice
            {
                Id = Guid.Parse("b7800000-0000-0000-0000-000000000003"),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Purchase,
                PurchaseReceivingRequired = true,
                PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed,
                PurchaseReceivedAtUtc = new DateTime(2026, 6, 17, 9, 0, 0, DateTimeKind.Utc),
                PurchaseReceivedByUsername = "admin",
                InvoiceDate = new DateOnly(2026, 6, 17),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 purge 품목",
                        ItemTrackingType = ItemTrackingTypes.Stock,
                        Unit = "EA",
                        Quantity = 5m,
                        UnitPrice = 1000m,
                        LineAmount = 5000m
                    }
                }
            });

            var saveTransferResult = await service.SaveInventoryTransferAsync(new LocalInventoryTransfer
            {
                Id = transferId,
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferNumber = "TR-PURGE-STOCK",
                TransferDate = new DateOnly(2026, 6, 17),
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                Lines =
                {
                    new LocalInventoryTransferLine
                    {
                        Id = Guid.Parse("b7700000-0000-0000-0000-000000000003"),
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 purge 품목",
                        Unit = "EA",
                        Quantity = 2m
                    }
                }
            }, session);
            Assert.True(saveTransferResult.Success, saveTransferResult.Message);
            Assert.Equal(3m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());

            var purgeResult = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.InventoryTransfer,
                transferId);

            Assert.True(purgeResult.Success, purgeResult.Message);
            Assert.False(await db.InventoryTransfers.IgnoreQueryFilters().AnyAsync(transfer => transfer.Id == transferId));
            Assert.Equal(5m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(5m, (await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId)).CurrentStock);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgeInvoice_CleansOrphanedChildrenWhenInvoiceAlreadyMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-invoice-purge-orphans-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("b8600000-0000-0000-0000-000000000001");
            var itemId = Guid.Parse("b8700000-0000-0000-0000-000000000001");
            var invoiceId = Guid.Parse("b8700000-0000-0000-0000-000000000002");
            var invoiceLineId = Guid.Parse("b8700000-0000-0000-0000-000000000003");
            var paymentId = Guid.Parse("b8700000-0000-0000-0000-000000000004");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "전표 purge 고아 거래처",
                NameMatchKey = "전표purge고아거래처"
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "전표 purge 고아 품목",
                NameMatchKey = "전표purge고아품목",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                PurchasePrice = 1000m,
                CurrentStock = 1m
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 23),
                IsDeleted = true,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        Id = invoiceLineId,
                        InvoiceId = invoiceId,
                        ItemId = itemId,
                        ItemNameOriginal = "전표 purge 고아 품목",
                        ItemTrackingType = ItemTrackingTypes.Stock,
                        Unit = "EA",
                        Quantity = 1m,
                        UnitPrice = 1000m,
                        LineAmount = 1000m
                    }
                }
            });
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 23),
                Amount = 1000m,
                IsDeleted = true
            });
            db.InvoiceLineSerials.Add(new LocalInvoiceLineSerial
            {
                InvoiceId = invoiceId,
                InvoiceLineId = invoiceLineId,
                ItemId = itemId,
                SerialNumber = "INV-PURGE-ORPHAN-SN"
            });
            db.SerialLedgers.Add(new LocalSerialLedger
            {
                ItemId = itemId,
                SerialNumber = "INV-PURGE-ORPHAN-SN",
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Status = "Sold",
                SourceSalesInvoiceId = invoiceId,
                LastInvoiceId = invoiceId
            });
            db.InventoryMovements.Add(new LocalInventoryMovement
            {
                ItemId = itemId,
                InvoiceId = invoiceId,
                InvoiceLineId = invoiceLineId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                MovementType = "Sale",
                QuantityDelta = -1m,
                OccurredDate = new DateOnly(2026, 6, 23)
            });
            db.StockLayers.Add(new LocalStockLayer
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                SourceInvoiceId = invoiceId,
                SourceInvoiceLineId = invoiceLineId,
                ReceiptDate = new DateOnly(2026, 6, 23),
                OriginalQuantity = 1m,
                RemainingQuantity = 1m,
                UnitCost = 100m
            });
            db.CostAllocations.Add(new LocalCostAllocation
            {
                SalesInvoiceId = invoiceId,
                SalesInvoiceLineId = invoiceLineId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 1m,
                UnitCost = 100m,
                CostAmount = 100m
            });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 1m
            });
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Invoices\";");
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
            db.ChangeTracker.Clear();

            Assert.False(await db.Invoices.IgnoreQueryFilters().AnyAsync(invoice => invoice.Id == invoiceId));
            Assert.True(await db.InvoiceLines.IgnoreQueryFilters().AnyAsync(line => line.InvoiceId == invoiceId));
            Assert.True(await db.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.InvoiceId == invoiceId));
            Assert.True(await db.InventoryMovements.AnyAsync(movement => movement.InvoiceId == invoiceId));

            var purgeResult = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId);

            Assert.True(purgeResult.Success, purgeResult.Message);
            db.ChangeTracker.Clear();

            Assert.False(await db.InvoiceLines.IgnoreQueryFilters().AnyAsync(line => line.InvoiceId == invoiceId));
            Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.InvoiceId == invoiceId));
            Assert.False(await db.InvoiceLineSerials.AnyAsync(serial => serial.InvoiceId == invoiceId));
            Assert.False(await db.SerialLedgers.AnyAsync(ledger => ledger.LastInvoiceId == invoiceId || ledger.SourceSalesInvoiceId == invoiceId));
            Assert.False(await db.InventoryMovements.AnyAsync(movement => movement.InvoiceId == invoiceId));
            Assert.False(await db.StockLayers.AnyAsync(layer => layer.SourceInvoiceId == invoiceId));
            Assert.False(await db.CostAllocations.AnyAsync(cost => cost.SalesInvoiceId == invoiceId || cost.PurchaseInvoiceId == invoiceId));
            Assert.False(await db.ItemWarehouseStocks.AnyAsync(stock => stock.ItemId == itemId));
            Assert.Equal(0m, (await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId)).CurrentStock);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgeInvoice_CleansStaleLinkedTransactionAndAttachment()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-invoice-purge-stale-transaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("b8740000-0000-0000-0000-000000000001");
            var invoiceId = Guid.Parse("b8740000-0000-0000-0000-000000000002");
            var transactionId = Guid.Parse("b8740000-0000-0000-0000-000000000003");
            var attachmentId = Guid.Parse("b8740000-0000-0000-0000-000000000004");
            var attachmentDirectory = Path.Combine(tempRoot, "invoice-purge-attachments");
            Directory.CreateDirectory(attachmentDirectory);
            var attachmentFile = Path.Combine(attachmentDirectory, "invoice-purge-stale-transaction.txt");
            await File.WriteAllTextAsync(attachmentFile, "invoice purge stale transaction evidence");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "전표 purge stale 거래처",
                NameMatchKey = "전표purgestale거래처"
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 23),
                IsDeleted = true
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 23),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoiceId,
                LinkedInvoiceNumber = "INV-PURGE-STALE-TX",
                ReceiptTotal = 1000m,
                SettlementAmount = 1000m,
                IsDeleted = true
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = transactionId,
                AttachmentType = "증빙",
                FileName = Path.GetFileName(attachmentFile),
                StoredFileName = Path.GetFileName(attachmentFile),
                StoredPath = attachmentFile,
                FileSize = new FileInfo(attachmentFile).Length,
                UploadedAtUtc = DateTime.UtcNow,
                IsDeleted = true
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var purgeResult = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId);

            Assert.True(purgeResult.Success, purgeResult.Message);
            db.ChangeTracker.Clear();

            Assert.False(await db.Invoices.IgnoreQueryFilters().AnyAsync(invoice => invoice.Id == invoiceId));
            Assert.False(await db.Transactions.IgnoreQueryFilters().AnyAsync(transaction => transaction.Id == transactionId));
            Assert.False(await db.TransactionAttachments.IgnoreQueryFilters().AnyAsync(attachment => attachment.TransactionId == transactionId));
            Assert.False(File.Exists(attachmentFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgePayment_CleansStaleLinkedTransactionAndAttachment()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-payment-purge-stale-transaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("b8720000-0000-0000-0000-000000000001");
            var invoiceId = Guid.Parse("b8720000-0000-0000-0000-000000000002");
            var paymentId = Guid.Parse("b8720000-0000-0000-0000-000000000003");
            var attachmentId = Guid.Parse("b8720000-0000-0000-0000-000000000004");
            var attachmentDirectory = Path.Combine(tempRoot, "payment-purge-attachments");
            Directory.CreateDirectory(attachmentDirectory);
            var attachmentFile = Path.Combine(attachmentDirectory, "payment-purge-stale-transaction.txt");
            await File.WriteAllTextAsync(attachmentFile, "payment purge stale transaction evidence");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "수금 purge stale 거래처",
                NameMatchKey = "수금purgestale거래처"
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 23)
            });
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 23),
                Amount = 1000m,
                IsDeleted = true
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = paymentId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 23),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoiceId,
                LinkedInvoiceNumber = "PAY-PURGE-STALE-TX",
                ReceiptTotal = 1000m,
                SettlementAmount = 1000m,
                IsDeleted = true
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = paymentId,
                AttachmentType = "증빙",
                FileName = Path.GetFileName(attachmentFile),
                StoredFileName = Path.GetFileName(attachmentFile),
                StoredPath = attachmentFile,
                FileSize = new FileInfo(attachmentFile).Length,
                UploadedAtUtc = DateTime.UtcNow,
                IsDeleted = true
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var purgeResult = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Payment,
                paymentId);

            Assert.True(purgeResult.Success, purgeResult.Message);
            db.ChangeTracker.Clear();

            Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.Id == paymentId));
            Assert.False(await db.Transactions.IgnoreQueryFilters().AnyAsync(transaction => transaction.Id == paymentId));
            Assert.False(await db.TransactionAttachments.IgnoreQueryFilters().AnyAsync(attachment => attachment.TransactionId == paymentId));
            Assert.False(File.Exists(attachmentFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgePayment_CleansStaleTransactionEvenWhenPaymentAlreadyMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-payment-purge-missing-payment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("b8730000-0000-0000-0000-000000000001");
            var paymentId = Guid.Parse("b8730000-0000-0000-0000-000000000002");
            var attachmentId = Guid.Parse("b8730000-0000-0000-0000-000000000003");
            var attachmentDirectory = Path.Combine(tempRoot, "payment-purge-missing-payment");
            Directory.CreateDirectory(attachmentDirectory);
            var attachmentFile = Path.Combine(attachmentDirectory, "payment-purge-missing-payment.txt");
            await File.WriteAllTextAsync(attachmentFile, "payment purge missing payment evidence");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "수금 purge missing 거래처",
                NameMatchKey = "수금purgemissing거래처"
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = paymentId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 23),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                ReceiptTotal = 1000m,
                SettlementAmount = 1000m,
                IsDeleted = true
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = paymentId,
                AttachmentType = "증빙",
                FileName = Path.GetFileName(attachmentFile),
                StoredFileName = Path.GetFileName(attachmentFile),
                StoredPath = attachmentFile,
                FileSize = new FileInfo(attachmentFile).Length,
                UploadedAtUtc = DateTime.UtcNow,
                IsDeleted = true
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var purgeResult = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Payment,
                paymentId);

            Assert.True(purgeResult.Success, purgeResult.Message);
            db.ChangeTracker.Clear();

            Assert.False(await db.Transactions.IgnoreQueryFilters().AnyAsync(transaction => transaction.Id == paymentId));
            Assert.False(await db.TransactionAttachments.IgnoreQueryFilters().AnyAsync(attachment => attachment.TransactionId == paymentId));
            Assert.False(File.Exists(attachmentFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgeTransaction_CleansLinkedPaymentAndAttachmentWhenTransactionAlreadyMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-transaction-purge-orphans-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("b8710000-0000-0000-0000-000000000001");
            var invoiceId = Guid.Parse("b8710000-0000-0000-0000-000000000002");
            var transactionId = Guid.Parse("b8710000-0000-0000-0000-000000000003");
            var attachmentId = Guid.Parse("b8710000-0000-0000-0000-000000000004");
            var attachmentDirectory = Path.Combine(tempRoot, "attachments");
            Directory.CreateDirectory(attachmentDirectory);
            var attachmentFile = Path.Combine(attachmentDirectory, "transaction-purge-orphan.txt");
            await File.WriteAllTextAsync(attachmentFile, "transaction purge orphan evidence");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "거래내역 purge 고아 거래처",
                NameMatchKey = "거래내역purge고아거래처"
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 23)
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 23),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoiceId,
                LinkedInvoiceNumber = "INV-PURGE-ORPHAN",
                ReceiptTotal = 1000m,
                SettlementAmount = 1000m,
                IsDeleted = true
            });
            db.Payments.Add(new LocalPayment
            {
                Id = transactionId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 23),
                Amount = 1000m,
                IsDeleted = true
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = transactionId,
                AttachmentType = "증빙",
                FileName = Path.GetFileName(attachmentFile),
                StoredFileName = Path.GetFileName(attachmentFile),
                StoredPath = attachmentFile,
                FileSize = new FileInfo(attachmentFile).Length,
                UploadedAtUtc = DateTime.UtcNow,
                IsDeleted = true
            });
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Transactions\";");
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
            db.ChangeTracker.Clear();

            Assert.False(await db.Transactions.IgnoreQueryFilters().AnyAsync(transaction => transaction.Id == transactionId));
            Assert.True(await db.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.Id == transactionId));
            Assert.True(await db.TransactionAttachments.IgnoreQueryFilters().AnyAsync(attachment => attachment.TransactionId == transactionId));
            Assert.True(File.Exists(attachmentFile));

            var purgeResult = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Transaction,
                transactionId);

            Assert.True(purgeResult.Success, purgeResult.Message);
            db.ChangeTracker.Clear();

            Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.Id == transactionId));
            Assert.False(await db.TransactionAttachments.IgnoreQueryFilters().AnyAsync(attachment => attachment.TransactionId == transactionId));
            Assert.False(File.Exists(attachmentFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgeItem_CleansOrphanedReferencesWhenItemAlreadyMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-item-purge-orphans-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var itemId = Guid.Parse("b8800000-0000-0000-0000-000000000001");
            var customerId = Guid.Parse("b8900000-0000-0000-0000-000000000001");
            var invoiceId = Guid.Parse("b8a00000-0000-0000-0000-000000000001");
            var invoiceLineId = Guid.Parse("b8b00000-0000-0000-0000-000000000001");
            var transferId = Guid.Parse("b8c00000-0000-0000-0000-000000000001");
            var transferLineId = Guid.Parse("b8d00000-0000-0000-0000-000000000001");
            var assetId = Guid.Parse("b8e00000-0000-0000-0000-000000000001");
            var profileId = Guid.Parse("b8f00000-0000-0000-0000-000000000001");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "품목 purge 고아 거래처",
                NameMatchKey = "품목purge고아거래처"
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 23),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        Id = invoiceLineId,
                        InvoiceId = invoiceId,
                        ItemId = itemId,
                        ItemNameOriginal = "품목 purge 고아 품목",
                        Unit = "EA",
                        Quantity = 1m,
                        UnitPrice = 1000m,
                        LineAmount = 1000m
                    }
                }
            });
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "TR-ITEM-PURGE-ORPHAN",
                TransferDate = new DateOnly(2026, 6, 23),
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Lines =
                {
                    new LocalInventoryTransferLine
                    {
                        Id = transferLineId,
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = "품목 purge 고아 품목",
                        Unit = "EA",
                        Quantity = 1m
                    }
                }
            });
            db.InvoiceLineSerials.Add(new LocalInvoiceLineSerial
            {
                InvoiceId = invoiceId,
                InvoiceLineId = invoiceLineId,
                ItemId = itemId,
                SerialNumber = "ITEM-PURGE-SN"
            });
            db.SerialLedgers.Add(new LocalSerialLedger
            {
                ItemId = itemId,
                SerialNumber = "ITEM-PURGE-SN",
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Status = "InStock"
            });
            db.InventoryMovements.Add(new LocalInventoryMovement
            {
                ItemId = itemId,
                InvoiceId = invoiceId,
                InvoiceLineId = invoiceLineId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                MovementType = "Sale",
                QuantityDelta = -1m,
                OccurredDate = new DateOnly(2026, 6, 23)
            });
            db.StockLayers.Add(new LocalStockLayer
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                SourceInvoiceId = invoiceId,
                SourceInvoiceLineId = invoiceLineId,
                ReceiptDate = new DateOnly(2026, 6, 23),
                OriginalQuantity = 1m,
                RemainingQuantity = 1m,
                UnitCost = 100m
            });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 1m
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ItemId = itemId,
                AssetKey = "USENET|ITEM-PURGE-ORPHAN|A4-MFP",
                CustomerName = "품목 purge 고아 고객",
                CurrentCustomerName = "품목 purge 고아 고객",
                ItemName = "A4-MFP",
                ManagementNumber = "ITEM-PURGE-ORPHAN-001",
                AssetStatus = "임대",
                IsDirty = true
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "품목 purge 고아 고객",
                ItemName = "A4-MFP",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        ItemId = itemId,
                        DisplayItemName = "A4-MFP",
                        BillingLineMode = "묶음",
                        Quantity = 1m,
                        UnitPrice = 10000m,
                        Amount = 10000m
                    }
                }),
                IsDirty = true
            });
            await db.SaveChangesAsync();

            var purgeResult = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Item,
                itemId);

            Assert.True(purgeResult.Success, purgeResult.Message);
            db.ChangeTracker.Clear();

            Assert.Null(await db.InvoiceLines.Select(line => line.ItemId).SingleAsync());
            Assert.Null(await db.InventoryTransferLines.Select(line => line.ItemId).SingleAsync());
            Assert.False(await db.InvoiceLineSerials.AnyAsync(serial => serial.ItemId == itemId));
            Assert.False(await db.SerialLedgers.AnyAsync(ledger => ledger.ItemId == itemId));
            Assert.Empty(await db.InventoryMovements.Where(current => current.ItemId == itemId).ToListAsync());
            Assert.Empty(await db.StockLayers.Where(current => current.ItemId == itemId).ToListAsync());
            Assert.Empty(await db.ItemWarehouseStocks.Where(current => current.ItemId == itemId).ToListAsync());

            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);
            Assert.Null(asset.ItemId);
            Assert.False(asset.IsDirty);

            var profile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
            var templateItems = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(profile.BillingTemplateJson) ?? [];
            Assert.Single(templateItems);
            Assert.Equal(Guid.Empty, templateItems[0].ItemId);
            Assert.False(profile.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgeRentalBillingProfile_CleansOrphanedChildrenWhenProfileAlreadyMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-profile-purge-orphans-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var profileId = Guid.Parse("b8100000-0000-0000-0000-000000000001");
            var assetId = Guid.Parse("b8200000-0000-0000-0000-000000000001");
            var logId = Guid.Parse("b8300000-0000-0000-0000-000000000001");
            var historyId = Guid.Parse("b8400000-0000-0000-0000-000000000001");

            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = profileId,
                AssetKey = "USENET|PURGE-ORPHAN|A4-MFP",
                CustomerName = "청구프로필 purge 고객",
                CurrentCustomerName = "청구프로필 purge 고객",
                ItemName = "A4-MFP",
                ManagementNumber = "PURGE-ORPHAN-001",
                AssetStatus = "임대",
                BillingEligibilityStatus = "청구대상",
                IsDirty = true
            });
            db.RentalBillingLogs.Add(new LocalRentalBillingLog
            {
                Id = logId,
                BillingProfileId = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingYearMonth = "2026-06",
                ScheduledDate = new DateOnly(2026, 6, 25),
                Status = "예정",
                BilledAmount = 10000m,
                IsDirty = true
            });
            db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
            {
                Id = historyId,
                AssetId = assetId,
                BillingProfileId = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "청구프로필 purge 고객",
                BillingProfileDisplay = "누락된 프로필",
                ItemName = "A4-MFP",
                ManagementNumber = "PURGE-ORPHAN-001",
                IsDirty = true
            });
            await db.SaveChangesAsync();

            var purgeResult = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.RentalBillingProfile,
                profileId);

            Assert.True(purgeResult.Success, purgeResult.Message);
            db.ChangeTracker.Clear();

            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);
            Assert.Null(asset.BillingProfileId);
            Assert.Equal("미확인", asset.BillingEligibilityStatus);
            Assert.False(asset.IsDirty);
            Assert.False(await db.RentalBillingLogs.IgnoreQueryFilters().AnyAsync(current => current.Id == logId));
            var history = await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().SingleAsync(current => current.Id == historyId);
            Assert.Null(history.BillingProfileId);
            Assert.False(history.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgeRentalAsset_CleansTemplateAndHistoryWhenAssetAlreadyMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-asset-purge-orphans-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var assetId = Guid.Parse("b8500000-0000-0000-0000-000000000001");
            var profileId = Guid.Parse("b8600000-0000-0000-0000-000000000001");
            var historyId = Guid.Parse("b8700000-0000-0000-0000-000000000001");

            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "자산 purge 고객",
                ItemName = "A4-MFP",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "A4-MFP",
                        BillingLineMode = "묶음",
                        Quantity = 1m,
                        UnitPrice = 10000m,
                        Amount = 10000m,
                        IncludedAssetIds = [assetId]
                    }
                }),
                IsDirty = true
            });
            db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
            {
                Id = historyId,
                AssetId = assetId,
                BillingProfileId = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "자산 purge 고객",
                BillingProfileDisplay = "자산 purge 프로필",
                ItemName = "A4-MFP",
                ManagementNumber = "PURGE-ASSET-001",
                IsDirty = true
            });
            await db.SaveChangesAsync();

            var purgeResult = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.RentalAsset,
                assetId);

            Assert.True(purgeResult.Success, purgeResult.Message);
            db.ChangeTracker.Clear();

            var profile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
            var items = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(profile.BillingTemplateJson) ?? [];
            Assert.DoesNotContain(items.SelectMany(item => item.IncludedAssetIds), id => id == assetId);
            Assert.False(profile.IsDirty);
            Assert.False(await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().AnyAsync(current => current.Id == historyId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task InventoryTransferViewModel_CanDeleteTransfer_RequiresBothOfficesForFinalStatus()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-transfer-delete-ui-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var sourceSession = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeOfficeOnly,
                AppPermissionNames.DeliveryEdit);
            var sourceService = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), sourceSession);
            var sourceViewModel = new InventoryTransferViewModel(sourceService, sourceSession)
            {
                TransferId = Guid.Parse("b7600000-0000-0000-0000-000000000004"),
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferStatus = InventoryTransferStatusNormalizer.Received
            };

            Assert.False(sourceViewModel.CanDeleteTransfer);

            sourceViewModel.TransferStatus = InventoryTransferStatusNormalizer.Pending;
            Assert.True(sourceViewModel.CanDeleteTransfer);

            var targetSession = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                TenantScopeCatalog.ScopeOfficeOnly,
                AppPermissionNames.DeliveryEdit);
            var targetService = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession);
            var targetViewModel = new InventoryTransferViewModel(targetService, targetSession)
            {
                TransferId = Guid.Parse("b7600000-0000-0000-0000-000000000005"),
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferStatus = InventoryTransferStatusNormalizer.Received
            };

            Assert.False(targetViewModel.CanDeleteTransfer);

            var tenantWideSession = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeTenantAll,
                AppPermissionNames.DeliveryEdit);
            var tenantWideService = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), tenantWideSession);
            var tenantWideViewModel = new InventoryTransferViewModel(tenantWideService, tenantWideSession)
            {
                TransferId = Guid.Parse("b7600000-0000-0000-0000-000000000006"),
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferStatus = InventoryTransferStatusNormalizer.Received
            };

            Assert.True(tenantWideViewModel.CanDeleteTransfer);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_TryRepairRentalAssetRevisionConflictAsync_ResolvesWhenServerCanReplaceInvalidItemReference()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-rental-asset-resolve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.Parse("81111111-1111-1111-1111-111111111111");
            var customerId = Guid.Parse("82222222-2222-2222-2222-222222222222");
            var profileId = Guid.Parse("83333333-3333-3333-3333-333333333333");
            var serverItemId = Guid.Parse("84444444-4444-4444-4444-444444444444");
            var missingLocalItemId = Guid.Parse("85555555-5555-5555-5555-555555555555");
            const long localRevision = 200L;
            const long serverRevision = 350L;
            var updatedAtUtc = new DateTime(2026, 4, 23, 14, 6, 50, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:asset-resolve";

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Misu Center",
                IsDirty = false
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                CustomerName = "Misu Center",
                InstallSiteName = "Social Welfare",
                ItemName = "MFC-L5700D",
                MonthlyAmount = 0m,
                IsDirty = false
            });
            db.Items.Add(new LocalItem
            {
                Id = serverItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "MFC-L5700D",
                NameMatchKey = "MFCL5700D",
                SpecificationOriginal = string.Empty,
                SpecificationMatchKey = string.Empty,
                CategoryName = "A4",
                ItemKind = "Rental",
                TrackingType = "Stock",
                Unit = "EA",
                BoxQuantity = 1m,
                StorageLocation = string.Empty,
                CurrentStock = 0m,
                SafetyStock = 0m,
                PurchasePrice = 297000m,
                SalePrice = 0m,
                RetailPrice = 0m,
                PriceGradeA = 0m,
                PriceGradeB = 0m,
                PriceGradeC = 0m,
                IsRental = true,
                IsSale = false,
                IsDirty = false
            });

            var asset = new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                CustomerName = "Misu Center",
                CurrentCustomerName = "Misu Center",
                BillingProfileId = profileId,
                ItemId = missingLocalItemId,
                AssetKey = "USENET|A-001|MFC-L5700D",
                ManagementId = "570",
                ManagementNumber = "A-001",
                ItemName = "MFC-L5700D",
                InstallLocation = "Social Welfare",
                InstallSiteName = "Social Welfare",
                Notes = "원본 관리ID: 570\n원본 관리번호: A-001",
                Revision = localRevision,
                UpdatedAtUtc = updatedAtUtc,
                IsDirty = true
            };
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var clientSnapshot = LocalMappings.ToDto(asset);
            clientSnapshot.ItemId = null;
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalRentalAsset),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalRentalAsset),
                EntityId = assetId,
                ExpectedRevision = localRevision,
                TenantCode = asset.TenantCode,
                OfficeCode = asset.OfficeCode,
                ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(asset);
            serverSnapshot.ItemId = serverItemId;
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-5);
            serverSnapshot.ExpectedRevision = 0;
            serverSnapshot.MutationId = string.Empty;
            serverSnapshot.MutationCreatedAtUtc = null;

            var conflict = new ConflictLogDto
            {
                EntityName = "RentalAsset",
                EntityId = assetId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var outcome = (ValueTuple<bool, bool>?)await InvokePrivateInstanceTaskResultAsync(
                sync,
                "TryRepairRentalAssetRevisionConflictAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(outcome.HasValue);
            Assert.True(outcome.Value.Item1);
            Assert.False(outcome.Value.Item2);

            var storedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == assetId);
            var outboxRows = await db.SyncOutboxEntries.AsNoTracking()
                .Where(entry => entry.EntityName == nameof(LocalRentalAsset) && entry.EntityId == assetId)
                .ToListAsync();

            Assert.Equal(serverItemId, storedAsset.ItemId);
            Assert.Equal(serverRevision, storedAsset.Revision);
            Assert.False(storedAsset.IsDirty);
            Assert.Empty(outboxRows);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_TryRepairRentalAssetRevisionConflictAsync_PreparesRetryWhenLocalStateMovedWithinAllowedFields()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-rental-asset-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.Parse("86111111-1111-1111-1111-111111111111");
            var customerId = Guid.Parse("86222222-2222-2222-2222-222222222222");
            var profileId = Guid.Parse("86333333-3333-3333-3333-333333333333");
            var itemId = Guid.Parse("86444444-4444-4444-4444-444444444444");
            var staleCustomerId = Guid.Parse("86555555-5555-5555-5555-555555555555");
            var staleProfileId = Guid.Parse("86666666-6666-6666-6666-666666666666");
            const long localRevision = 900L;
            const long serverRevision = 1200L;
            var updatedAtUtc = new DateTime(2026, 4, 23, 14, 6, 50, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:asset-retry";

            db.Customers.AddRange(
                new LocalCustomer
                {
                    Id = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    NameOriginal = "연수구 함박비류 도서관",
                    IsDirty = false
                },
                new LocalCustomer
                {
                    Id = staleCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    NameOriginal = "[연수구]함박비류도서관",
                    IsDirty = false
                });
            db.RentalBillingProfiles.AddRange(
                new LocalRentalBillingProfile
                {
                    Id = profileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    CustomerId = customerId,
                    CustomerName = "연수구 함박비류 도서관",
                    ProfileKey = "PROFILE|ACTIVE|HAMBAK",
                    InstallSiteName = "2층 컴퓨터실",
                    ItemName = "SL-M2670FN",
                    MonthlyAmount = 0m,
                    IsDirty = false
                },
                new LocalRentalBillingProfile
                {
                    Id = staleProfileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    CustomerId = staleCustomerId,
                    CustomerName = "[연수구]함박비류도서관",
                    ProfileKey = "PROFILE|STALE|HAMBAK",
                    InstallSiteName = "2층 컴퓨터실",
                    ItemName = "SL-M2670FN",
                    MonthlyAmount = 0m,
                    IsDirty = false
                });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "SL-M2670FN",
                NameMatchKey = "SLM2670FN",
                SpecificationOriginal = string.Empty,
                SpecificationMatchKey = string.Empty,
                CategoryName = "A4",
                ItemKind = "Rental",
                TrackingType = "Stock",
                Unit = "EA",
                BoxQuantity = 1m,
                StorageLocation = string.Empty,
                CurrentStock = 0m,
                SafetyStock = 0m,
                PurchasePrice = 0m,
                SalePrice = 0m,
                RetailPrice = 0m,
                PriceGradeA = 0m,
                PriceGradeB = 0m,
                PriceGradeC = 0m,
                IsRental = true,
                IsSale = false,
                IsDirty = false
            });

            var asset = new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                CustomerName = "연수구 함박비류 도서관",
                CurrentCustomerName = "연수구 함박비류 도서관",
                BillingProfileId = profileId,
                ItemId = itemId,
                AssetKey = "USENET|A-002|SL-M2670FN",
                ManagementId = "438",
                ManagementNumber = "A-002",
                ItemName = "SL-M2670FN",
                InstallLocation = "2층 컴퓨터실",
                InstallSiteName = "2층 컴퓨터실",
                Revision = localRevision,
                UpdatedAtUtc = updatedAtUtc,
                IsDirty = true
            };
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var clientSnapshot = LocalMappings.ToDto(asset);
            clientSnapshot.CustomerId = staleCustomerId;
            clientSnapshot.CustomerName = "[연수구]함박비류도서관";
            clientSnapshot.CurrentCustomerName = "[연수구]함박비류도서관";
            clientSnapshot.BillingProfileId = staleProfileId;
            clientSnapshot.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalRentalAsset),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalRentalAsset),
                EntityId = assetId,
                ExpectedRevision = localRevision,
                TenantCode = asset.TenantCode,
                OfficeCode = asset.OfficeCode,
                ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(asset);
            serverSnapshot.CustomerId = null;
            serverSnapshot.CustomerName = "[연수구]함박비류도서관";
            serverSnapshot.CurrentCustomerName = "[연수구]함박비류도서관";
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-5);
            serverSnapshot.ExpectedRevision = 0;
            serverSnapshot.MutationId = string.Empty;
            serverSnapshot.MutationCreatedAtUtc = null;

            var conflict = new ConflictLogDto
            {
                EntityName = "RentalAsset",
                EntityId = assetId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var outcome = (ValueTuple<bool, bool>?)await InvokePrivateInstanceTaskResultAsync(
                sync,
                "TryRepairRentalAssetRevisionConflictAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(outcome.HasValue);
            Assert.False(outcome.Value.Item1);
            Assert.True(outcome.Value.Item2);

            var storedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == assetId);
            var outboxRow = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.EntityName == nameof(LocalRentalAsset) && entry.EntityId == assetId);

            Assert.Equal(serverRevision, storedAsset.Revision);
            Assert.True(storedAsset.IsDirty);
            Assert.Equal(customerId, storedAsset.CustomerId);
            Assert.Equal(profileId, storedAsset.BillingProfileId);
            Assert.Equal(OfficeCodeCatalog.Usenet, storedAsset.ResponsibleOfficeCode);
            Assert.Equal("Prepared", outboxRow.Status);
            Assert.Equal(serverRevision, outboxRow.ExpectedRevision);
            Assert.True(string.IsNullOrWhiteSpace(outboxRow.ErrorMessage));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_TryPrepareItemRevisionRetryAsync_RebasesNewerLocalItemAndRequeuesOutbox()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-item-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.Parse("87111111-1111-1111-1111-111111111111");
            const long localRevision = 1779954478941L;
            const long serverRevision = 1779954716831L;
            var serverUpdatedAtUtc = new DateTime(2026, 5, 28, 7, 51, 56, DateTimeKind.Utc);
            var localUpdatedAtUtc = serverUpdatedAtUtc.AddSeconds(30);
            const string deviceId = "DESKTOP-VGCK877:item-retry";

            var item = new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                NameOriginal = "[C]Toner MLT-K250L",
                NameMatchKey = "CTONERMLTK250L",
                SpecificationOriginal = "SL-M2680FN",
                SpecificationMatchKey = "SLM2680FN",
                CategoryName = "Supplies",
                Unit = "EA",
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 2m,
                PurchasePrice = 32000m,
                CreatedAtUtc = serverUpdatedAtUtc.AddDays(-1),
                UpdatedAtUtc = localUpdatedAtUtc,
                Revision = localRevision,
                IsDirty = true
            };
            db.Items.Add(item);

            var clientSnapshot = LocalMappings.ToDto(item);
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = localUpdatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalItem),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalItem),
                EntityId = itemId,
                ExpectedRevision = localRevision,
                TenantCode = item.TenantCode,
                OfficeCode = item.OfficeCode,
                Status = "Sent",
                PreparedAtUtc = localUpdatedAtUtc,
                SentAtUtc = localUpdatedAtUtc.AddSeconds(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(item);
            serverSnapshot.CategoryName = string.Empty;
            serverSnapshot.Unit = string.Empty;
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = serverUpdatedAtUtc;
            serverSnapshot.ExpectedRevision = 0;
            serverSnapshot.MutationId = string.Empty;
            serverSnapshot.MutationCreatedAtUtc = null;

            var conflict = new ConflictLogDto
            {
                EntityName = "Item",
                EntityId = itemId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var prepared = await InvokePrivateInstanceAsync<bool>(
                sync,
                "TryPrepareItemRevisionRetryAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(prepared);

            var storedItem = await db.Items.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == itemId);
            var outboxRow = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.EntityName == nameof(LocalItem) && entry.EntityId == itemId);

            Assert.Equal(serverRevision, storedItem.Revision);
            Assert.Equal("Supplies", storedItem.CategoryName);
            Assert.Equal("EA", storedItem.Unit);
            Assert.True(storedItem.IsDirty);
            Assert.Equal("Prepared", outboxRow.Status);
            Assert.Equal(serverRevision, outboxRow.ExpectedRevision);
            Assert.True(string.IsNullOrWhiteSpace(outboxRow.ErrorMessage));
            Assert.Null(outboxRow.SentAtUtc);
            Assert.Null(outboxRow.AcknowledgedAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RentalBillingProfileKey_IncludesLinkedCustomerDisplayName()
    {
        var customerId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var officialKey = RentalDuplicateNormalizer.BuildProfileKey(
            "USENET",
            customerId,
            "123-45-67890",
            "Waterworks",
            "Bundle",
            "Postpaid",
            25,
            1,
            "TaxInvoice");
        var aliasKey = RentalDuplicateNormalizer.BuildProfileKey(
            "USENET",
            customerId,
            "123-45-67890",
            "Waterworks[Quality]",
            "Bundle",
            "Postpaid",
            25,
            1,
            "TaxInvoice");
        var legacyKey = RentalDuplicateNormalizer.BuildLegacyProfileKey(
            "USENET",
            customerId,
            "123-45-67890",
            "Waterworks[Quality]",
            "Bundle",
            "Postpaid",
            25,
            1,
            "TaxInvoice");

        Assert.Contains("NAME:WATERWORKS", officialKey, StringComparison.Ordinal);
        Assert.Contains("NAME:WATERWORKSQUALITY", aliasKey, StringComparison.Ordinal);
        Assert.NotEqual(officialKey, aliasKey);
        Assert.DoesNotContain("NAME:", legacyKey, StringComparison.Ordinal);
    }

    [Fact]
    public void RentalBillingProfileDisplay_DerivesLegacyAliasFromProfileKey()
    {
        var alias = InvokePrivateStatic<string>(
            typeof(RentalStateService),
            "TryResolveBillingProfileAliasFromProfileKey",
            "USENET||WaterworksQuality||IMC2000",
            "Waterworks");

        Assert.Equal("Waterworks[Quality]", alias);
    }

    [Fact]
    public async Task ResolveCustomerIdAsync_RespectsPreferredTenant()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-tenant-resolve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetCustomerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var itworldCustomerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            db.Customers.Add(new LocalCustomer
            {
                Id = usenetCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "아이티월드",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("아이티월드"),
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var rental = new RentalStateService(db);
            var wrongTenantOnly = await InvokePrivateInstanceAsync<Guid?>(
                rental,
                "ResolveCustomerIdAsync",
                "아이티월드",
                null,
                CancellationToken.None,
                true,
                null,
                TenantScopeCatalog.Itworld);

            Assert.Null(wrongTenantOnly);

            db.Customers.Add(new LocalCustomer
            {
                Id = itworldCustomerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "아이티월드",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("아이티월드"),
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var preferredTenantMatch = await InvokePrivateInstanceAsync<Guid?>(
                rental,
                "ResolveCustomerIdAsync",
                "아이티월드",
                null,
                CancellationToken.None,
                true,
                null,
                TenantScopeCatalog.Itworld);

            Assert.Equal(itworldCustomerId, preferredTenantMatch);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            // AppPaths is static for the test process; keep the temp root available
            // until process exit so parallel xUnit tests cannot delete the active DB path.
        }
    }

    [Fact]
    public async Task SaveBillingProfileAsync_AllowsCrossTenantIncludedAssetReference()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-profile-asset-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var crossTenantAssetId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = crossTenantAssetId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                AssetKey = "ITWORLD|2604-001|Waterworks|IMC2000",
                CustomerName = "Waterworks",
                CurrentCustomerName = "Waterworks",
                ItemName = "IMC2000",
                ManagementNumber = "2604-001",
                AssetStatus = "임대진행중"
            });
            await db.SaveChangesAsync();

            var profile = new LocalRentalBillingProfile
            {
                Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Waterworks[Quality]",
                ItemName = "IMC2000",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "IMC2000",
                        BillingLineMode = "묶음",
                        Quantity = 1,
                        UnitPrice = 90000m,
                        Amount = 90000m,
                        IncludedAssetIds = [crossTenantAssetId]
                    }
                })
            };
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var rental = new RentalStateService(db);
            var result = await rental.SaveBillingProfileAsync(profile, session);

            Assert.True(result.Success, result.Message);
            Assert.Single(await db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync());

            var persistedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == crossTenantAssetId);
            Assert.Null(persistedAsset.BillingProfileId);
            Assert.Equal(TenantScopeCatalog.Itworld, persistedAsset.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, persistedAsset.ResponsibleOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            // AppPaths is static for the test process; keep the temp root available
            // until process exit so parallel xUnit tests cannot delete the active DB path.
        }
    }

    [Fact]
    public void RentalBillingTemplateEditorItem_RecalculatesAmountFromQuantityAndUnitPrice()
    {
        var item = new RentalBillingTemplateEditorItem
        {
            Quantity = 2m,
            UnitPrice = 1000m
        };

        Assert.Equal(2000m, item.Amount);
        Assert.Equal(2000m, item.EffectiveAmount);

        item.UnitPrice = 1500m;
        Assert.Equal(3000m, item.Amount);

        item.Quantity = 3m;
        Assert.Equal(4500m, item.Amount);

        item.Amount = 9999m;
        item.NormalizeCalculatedAmount();
        Assert.Equal(4500m, item.Amount);
    }

    [Fact]
    public void RentalBillingViewModel_UpdateTemplateDerivedValues_DoesNotReenterFromCalculatedAmountNotification()
    {
        var vm = new RentalBillingViewModel(null!, null!, new SessionState());
        var item = new RentalBillingTemplateEditorItem
        {
            DisplayItemName = "렌탈료",
            BillingLineMode = "묶음",
            Quantity = 1m,
            UnitPrice = 1000m,
            Amount = 1000m
        };

        vm.TemplateItems.Add(item);
        InvokePrivateInstance(vm, "WireTemplateItem", item);
        InvokePrivateInstance(vm, "UpdateTemplateDerivedValues");

        Assert.Equal(1000m, vm.EditMonthlyAmount);
        Assert.Equal(1000m, item.Amount);
        Assert.Equal("렌탈료", vm.EditItemName);
    }

    [Fact]
    public async Task SaveAssetAsync_RefreshesLinkedBillingProfileMonthlyAmount()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-asset-monthly-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("55555555-5555-5555-5555-555555555555");
            var assetId = Guid.Parse("66666666-6666-6666-6666-666666666666");
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Monthly Sync Customer",
                InstallSiteName = "Main Office",
                ItemName = "Printer",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 100000m,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Printer",
                        BillingLineMode = "묶음",
                        Quantity = 1m,
                        UnitPrice = 100000m,
                        Amount = 100000m,
                        IncludedAssetIds = [assetId]
                    }
                })
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = profileId,
                AssetKey = "USENET|A-001|SN-001|Monthly Sync Customer|Printer",
                CustomerName = "Monthly Sync Customer",
                CurrentCustomerName = "Monthly Sync Customer",
                InstallSiteName = "Main Office",
                InstallLocation = "Main Office",
                ItemName = "Printer",
                ManagementNumber = "A-001",
                MachineNumber = "SN-001",
                AssetStatus = "임대",
                BillingEligibilityStatus = "청구대상",
                MonthlyFee = 100000m
            });
            await db.SaveChangesAsync();

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var rental = new RentalStateService(db);
            var result = await rental.SaveAssetAsync(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = profileId,
                CustomerName = "Monthly Sync Customer",
                CurrentCustomerName = "Monthly Sync Customer",
                InstallSiteName = "Main Office",
                InstallLocation = "Main Office",
                ItemName = "Printer",
                ManagementNumber = "A-001",
                MachineNumber = "SN-001",
                AssetStatus = "임대",
                BillingEligibilityStatus = "청구대상",
                MonthlyFee = 150000m
            }, session);

            Assert.True(result.Success, result.Message);
            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstAsync(profile => profile.Id == profileId);
            var storedTemplateItems = rental.GetBillingTemplateItems(storedProfile);

            Assert.Equal(150000m, storedProfile.MonthlyAmount);
            Assert.Single(storedTemplateItems);
            Assert.Equal(150000m, storedTemplateItems[0].UnitPrice);
            Assert.Equal(150000m, storedTemplateItems[0].Amount);
            Assert.True(storedProfile.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveAssetAsync_DoesNotAppendMissingProfileAssetToExplicitMultiLineTemplate()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-asset-monthly-multiline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("55666666-6666-6666-6666-666666666660");
            var firstAssetId = Guid.Parse("55666666-6666-6666-6666-666666666661");
            var missingAssetId = Guid.Parse("55666666-6666-6666-6666-666666666662");
            var secondLineAssetId = Guid.Parse("55666666-6666-6666-6666-666666666663");

            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Monthly Sync Customer",
                InstallSiteName = "Main Office",
                ItemName = "Printer",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 330000m,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Printer A",
                        BillingLineMode = "묶음",
                        Quantity = 1m,
                        UnitPrice = 110000m,
                        Amount = 110000m,
                        IncludedAssetIds = [firstAssetId]
                    },
                    new()
                    {
                        DisplayItemName = "Printer B",
                        BillingLineMode = "묶음",
                        Quantity = 1m,
                        UnitPrice = 220000m,
                        Amount = 220000m,
                        IncludedAssetIds = [secondLineAssetId]
                    }
                })
            });

            db.RentalAssets.AddRange(
                CreateMonthlySyncAsset(firstAssetId, profileId, "A-001", 110000m),
                CreateMonthlySyncAsset(missingAssetId, profileId, "A-002", 40000m),
                CreateMonthlySyncAsset(secondLineAssetId, profileId, "A-003", 220000m));
            await db.SaveChangesAsync();

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var rental = new RentalStateService(db);
            var result = await rental.SaveAssetAsync(CreateMonthlySyncAsset(missingAssetId, profileId, "A-002", 40000m), session);

            Assert.True(result.Success, result.Message);
            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstAsync(profile => profile.Id == profileId);
            var storedTemplateItems = rental.GetBillingTemplateItems(storedProfile);

            Assert.Equal(330000m, storedProfile.MonthlyAmount);
            Assert.Equal(2, storedTemplateItems.Count);
            Assert.Equal(110000m, storedTemplateItems[0].Amount);
            Assert.Equal(220000m, storedTemplateItems[1].Amount);

            var allIncludedAssetIds = storedTemplateItems
                .SelectMany(item => item.IncludedAssetIds)
                .OrderBy(id => id)
                .ToList();
            Assert.Equal(new[] { firstAssetId, secondLineAssetId }.OrderBy(id => id), allIncludedAssetIds);
            Assert.DoesNotContain(missingAssetId, allIncludedAssetIds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalRentalAsset CreateMonthlySyncAsset(Guid assetId, Guid profileId, string managementNumber, decimal monthlyFee)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            BillingProfileId = profileId,
            AssetKey = $"USENET|{managementNumber}|SN-{managementNumber}|Monthly Sync Customer|Printer",
            CustomerName = "Monthly Sync Customer",
            CurrentCustomerName = "Monthly Sync Customer",
            InstallSiteName = "Main Office",
            InstallLocation = "Main Office",
            ItemName = "Printer",
            ManagementNumber = managementNumber,
            MachineNumber = $"SN-{managementNumber}",
            AssetStatus = "임대",
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = monthlyFee
        };

    private static LocalRentalAsset CreateZeroFeeRentalAsset(
        Guid assetId,
        string officeCode,
        string tenantCode,
        string label)
        => new()
        {
            Id = assetId,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            ManagementCompanyCode = officeCode,
            AssetKey = $"{officeCode}|ZERO-FEE|{assetId:N}",
            CustomerName = $"{label} Customer",
            CurrentCustomerName = $"{label} Customer",
            InstallSiteName = $"{label} Site",
            InstallLocation = $"{label} Site",
            ItemName = $"{label} Printer",
            ManagementNumber = $"{label}-ZERO",
            MachineNumber = $"{label}-ZERO-SN",
            AssetStatus = "임대",
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = 0m,
            IsDirty = false
        };

    [Fact]
    public async Task LocalDbInitializer_RepairRentalCustomerLinkage_NormalizesItworldScope_AndKeepsYeonsuScope()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-scope-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var brokenProfileId = Guid.Parse("81111111-1111-1111-1111-111111111111");
            var brokenAssetId = Guid.Parse("82222222-2222-2222-2222-222222222222");
            var brokenLogId = Guid.Parse("83333333-3333-3333-3333-333333333333");
            var yeonsuProfileId = Guid.Parse("84444444-4444-4444-4444-444444444444");
            var yeonsuAssetId = Guid.Parse("85555555-5555-5555-5555-555555555555");
            var wrongUsenetCustomerId = Guid.Parse("86666666-6666-6666-6666-666666666666");

            db.Customers.Add(new LocalCustomer
            {
                Id = wrongUsenetCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Wrong USENET Customer",
                NameMatchKey = "WRONGUSENETCUSTOMER",
                IsDirty = false
            });

            db.RentalBillingProfiles.AddRange(
                new LocalRentalBillingProfile
                {
                    Id = brokenProfileId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    CustomerId = wrongUsenetCustomerId,
                    ProfileKey = "ITWORLD|BROKEN-ITWORLD-CUSTOMER|ITWORLD-SITE|PRINTER",
                    CustomerName = "Broken ITWORLD Customer",
                    InstallSiteName = "ITWORLD Site",
                    ItemName = "Printer",
                    MonthlyAmount = 120000m,
                    BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                    {
                        new()
                        {
                            DisplayItemName = "Printer",
                            Quantity = 1m,
                            UnitPrice = 120000m,
                            Amount = 120000m,
                            IncludedAssetIds = [brokenAssetId]
                        }
                    }),
                    IsDirty = false
                },
                new LocalRentalBillingProfile
                {
                    Id = yeonsuProfileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ProfileKey = "USENET|YEONSU-CUSTOMER|YEONSU-SITE|COPIER",
                    CustomerName = "YEONSU Customer",
                    InstallSiteName = "YEONSU Site",
                    ItemName = "Copier",
                    MonthlyAmount = 90000m,
                    BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                    {
                        new()
                        {
                            DisplayItemName = "Copier",
                            Quantity = 1m,
                            UnitPrice = 90000m,
                            Amount = 90000m,
                            IncludedAssetIds = [yeonsuAssetId]
                        }
                    }),
                    IsDirty = false
                });

            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = brokenAssetId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    CustomerId = wrongUsenetCustomerId,
                    BillingProfileId = brokenProfileId,
                    AssetKey = "ITWORLD|BROKEN-001|SN-BROKEN",
                    CustomerName = "Broken ITWORLD Customer",
                    CurrentCustomerName = "Broken ITWORLD Customer",
                    InstallSiteName = "ITWORLD Site",
                    InstallLocation = "ITWORLD Site",
                    ItemName = "Printer",
                    ManagementNumber = "BROKEN-001",
                    MachineNumber = "SN-BROKEN",
                    AssetStatus = "ACTIVE",
                    BillingEligibilityStatus = string.Empty,
                    MonthlyFee = 120000m,
                    IsDirty = false
                },
                new LocalRentalAsset
                {
                    Id = yeonsuAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    BillingProfileId = yeonsuProfileId,
                    AssetKey = "USENET|YEONSU-001|SN-YEONSU",
                    CustomerName = "YEONSU Customer",
                    CurrentCustomerName = "YEONSU Customer",
                    InstallSiteName = "YEONSU Site",
                    InstallLocation = "YEONSU Site",
                    ItemName = "Copier",
                    ManagementNumber = "YEONSU-001",
                    MachineNumber = "SN-YEONSU",
                    AssetStatus = "ACTIVE",
                    BillingEligibilityStatus = string.Empty,
                    MonthlyFee = 90000m,
                    IsDirty = false
                });

            db.RentalBillingLogs.Add(new LocalRentalBillingLog
            {
                Id = brokenLogId,
                BillingProfileId = brokenProfileId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingYearMonth = "202604",
                Status = "PENDING",
                BilledAmount = 120000m,
                IsDirty = false
            });

            await db.SaveChangesAsync();
            var repairMethod = typeof(LocalDbInitializer).GetMethod(
                "RepairRentalCustomerLinkageAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(repairMethod);

            var repairTask = repairMethod!.Invoke(null, new object?[] { db }) as Task;
            Assert.NotNull(repairTask);
            await repairTask!;
            await db.SaveChangesAsync();

            var fixedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == brokenProfileId);
            var fixedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == brokenAssetId);
            var fixedLog = await db.RentalBillingLogs.IgnoreQueryFilters().SingleAsync(log => log.Id == brokenLogId);
            var yeonsuProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == yeonsuProfileId);
            var yeonsuAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == yeonsuAssetId);

            Assert.Equal(TenantScopeCatalog.Itworld, fixedProfile.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedProfile.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedProfile.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedProfile.ResponsibleOfficeCode);
            Assert.False(fixedProfile.IsDirty);

            Assert.Equal(TenantScopeCatalog.Itworld, fixedAsset.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedAsset.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedAsset.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedAsset.ResponsibleOfficeCode);
            Assert.False(fixedAsset.IsDirty);

            Assert.Equal(TenantScopeCatalog.Itworld, fixedLog.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedLog.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedLog.ResponsibleOfficeCode);
            Assert.False(fixedLog.IsDirty);

            Assert.Equal(TenantScopeCatalog.UsenetGroup, yeonsuProfile.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuProfile.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuProfile.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, yeonsuProfile.ResponsibleOfficeCode);

            Assert.Equal(TenantScopeCatalog.UsenetGroup, yeonsuAsset.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuAsset.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuAsset.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, yeonsuAsset.ResponsibleOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbInitializer_RepairRentalCustomerLinkage_ResolvesUniqueCustomerAcrossResponsibleOfficeByName()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-cross-office-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("8f111111-1111-1111-1111-111111111111");
            var profileId = Guid.Parse("8f222222-2222-2222-2222-222222222222");
            var assetId = Guid.Parse("8f333333-3333-3333-3333-333333333333");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "연수구청[여성아동과]",
                NameMatchKey = "연수구청[여성아동과]",
                IsDirty = false
            });

            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "연수구청[여성아동과]",
                InstallSiteName = "사무실",
                ItemName = "IMC2010",
                BillingType = "묶음",
                MonthlyAmount = 300000m,
                BillingTemplateJson = "[]",
                IsDirty = false
            });

            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = profileId,
                AssetKey = "USENET|YEONSU-DEPT-001|SN-DEPT",
                CustomerName = "연수구청[여성아동과]",
                CurrentCustomerName = "연수구청[여성아동과]",
                InstallSiteName = "사무실",
                InstallLocation = "사무실",
                ItemName = "IMC2010",
                ManagementNumber = "YEONSU-DEPT-001",
                MachineNumber = "SN-DEPT",
                AssetStatus = "ACTIVE",
                MonthlyFee = 300000m,
                IsDirty = false
            });

            await db.SaveChangesAsync();

            var repairMethod = typeof(LocalDbInitializer).GetMethod(
                "RepairRentalCustomerLinkageAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(repairMethod);

            var repairTask = repairMethod!.Invoke(null, new object?[] { db }) as Task;
            Assert.NotNull(repairTask);
            await repairTask!;
            await db.SaveChangesAsync();

            var profile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);

            Assert.Equal(customerId, profile.CustomerId);
            Assert.Equal("연수구청[여성아동과]", profile.CustomerName);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, profile.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, profile.OfficeCode);
            Assert.False(profile.IsDirty);

            Assert.Equal(customerId, asset.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, asset.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, asset.OfficeCode);
            Assert.Equal(profileId, asset.BillingProfileId);
            Assert.False(asset.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbInitializer_RepairRentalCustomerLinkage_ResolvesKnownPublicOfficeAliasNames()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-public-alias-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var waterCustomerId = Guid.Parse("8f444444-4444-4444-4444-444444444444");
            var waterProfileId = Guid.Parse("8f555555-5555-5555-5555-555555555555");
            var waterAssetId = Guid.Parse("8f666666-6666-6666-6666-666666666666");
            var healthCustomerId = Guid.Parse("8f777777-7777-7777-7777-777777777777");
            var healthProfileId = Guid.Parse("8f888888-8888-8888-8888-888888888888");
            var healthAssetId = Guid.Parse("8f999999-9999-9999-9999-999999999999");

            const string waterCustomerName = "\uC0C1\uC218\uB3C4\uC0AC\uC5C5\uBCF8\uBD80 \uB9D1\uC740\uBB3C\uC5F0\uAD6C\uC18C";
            const string waterAliasName = "[\uC0C1\uC218\uB3C4\uC0AC\uC5C5\uC18C]\uB9D1\uC740\uBB3C\uC5F0\uAD6C\uC18C";
            const string healthCustomerName = "\uC5F0\uC218\uAD6C\uCCAD[\uAC74\uAC15\uC99D\uC9C4\uACFC]";
            const string healthAliasName = "[\uC5F0\uC218\uAD6C]\uBCF4\uAC74\uC18C-\uAC74\uAC15\uC99D\uC9C4\uACFC";

            db.Customers.AddRange(
                new LocalCustomer
                {
                    Id = waterCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = waterCustomerName,
                    NameMatchKey = waterCustomerName,
                    IsDirty = false
                },
                new LocalCustomer
                {
                    Id = healthCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    NameOriginal = healthCustomerName,
                    NameMatchKey = healthCustomerName,
                    IsDirty = false
                });

            db.RentalBillingProfiles.AddRange(
                new LocalRentalBillingProfile
                {
                    Id = waterProfileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "PUBLIC-ALIAS-WATER",
                    CustomerName = waterAliasName,
                    InstallSiteName = "\uC218\uC9C8\uBD84\uC11D\uD300",
                    ItemName = "IMC2010",
                    BillingType = "\uBB36\uC74C",
                    MonthlyAmount = 110000m,
                    BillingTemplateJson = "[]",
                    IsDirty = false
                },
                new LocalRentalBillingProfile
                {
                    Id = healthProfileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "PUBLIC-ALIAS-HEALTH",
                    CustomerName = healthAliasName,
                    InstallSiteName = healthAliasName,
                    ItemName = "IMC2010",
                    BillingType = "\uBB36\uC74C",
                    MonthlyAmount = 240000m,
                    BillingTemplateJson = "[]",
                    IsDirty = false
                });

            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = waterAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BillingProfileId = waterProfileId,
                    AssetKey = "USENET|2311-005|WATER|IMC2010",
                    CustomerName = waterAliasName,
                    CurrentCustomerName = waterAliasName,
                    InstallSiteName = "\uC218\uC9C8\uBD84\uC11D\uD300",
                    InstallLocation = "\uC218\uC9C8\uBD84\uC11D\uD300",
                    ItemName = "IMC2010",
                    ManagementNumber = "2311-005",
                    MachineNumber = "WATER-001",
                    AssetStatus = "ACTIVE",
                    MonthlyFee = 110000m,
                    IsDirty = false
                },
                new LocalRentalAsset
                {
                    Id = healthAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BillingProfileId = healthProfileId,
                    AssetKey = "USENET|2401-011|HEALTH|IMC2010",
                    CustomerName = healthAliasName,
                    CurrentCustomerName = healthAliasName,
                    InstallSiteName = "\uC2E4.\uACFC\uB0B4",
                    InstallLocation = "\uC2E4.\uACFC\uB0B4",
                    ItemName = "IMC2010",
                    ManagementNumber = "2401-011",
                    MachineNumber = "HEALTH-001",
                    AssetStatus = "ACTIVE",
                    MonthlyFee = 90000m,
                    IsDirty = false
                });

            await db.SaveChangesAsync();

            var repairMethod = typeof(LocalDbInitializer).GetMethod(
                "RepairRentalCustomerLinkageAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(repairMethod);

            var repairTask = repairMethod!.Invoke(null, new object?[] { db }) as Task;
            Assert.NotNull(repairTask);
            await repairTask!;
            await db.SaveChangesAsync();

            var waterProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == waterProfileId);
            var healthProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == healthProfileId);
            var waterAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == waterAssetId);
            var healthAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == healthAssetId);

            Assert.Equal(waterCustomerId, waterProfile.CustomerId);
            Assert.Equal(waterCustomerName, waterProfile.CustomerName);
            Assert.Equal(waterCustomerId, waterAsset.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Usenet, waterProfile.ResponsibleOfficeCode);

            Assert.Equal(healthCustomerId, healthProfile.CustomerId);
            Assert.Equal(healthCustomerName, healthProfile.CustomerName);
            Assert.Equal(healthCustomerId, healthAsset.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, healthProfile.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, healthAsset.ResponsibleOfficeCode);
            Assert.False(waterProfile.IsDirty);
            Assert.False(healthProfile.IsDirty);
            Assert.False(waterAsset.IsDirty);
            Assert.False(healthAsset.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DataIntegrityIssueService_ScanAsync_FindsRentalRiskSignals()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-data-integrity-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            var missingAssetId = Guid.Parse("88888888-8888-8888-8888-888888888888");
            var zeroFeeAssetId = Guid.Parse("99999999-9999-9999-9999-999999999999");
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Integrity Customer",
                InstallSiteName = "Main Office",
                ItemName = "Printer",
                MonthlyAmount = 50_000m,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Printer",
                        Quantity = 1m,
                        UnitPrice = 100_000m,
                        Amount = 100_000m,
                        IncludedAssetIds = [missingAssetId]
                    }
                }),
                IsDirty = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = zeroFeeAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET|ZERO|SN-ZERO",
                CurrentCustomerName = "Integrity Customer",
                ItemName = "Printer",
                ManagementNumber = "ZERO",
                AssetStatus = "임대",
                BillingEligibilityStatus = "청구대상",
                MonthlyFee = 0m,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db);
            var result = await service.ScanAsync(CreateAdminSession());

            Assert.True(result.HasIssues);
            Assert.Contains(result.Summaries, summary => summary.Code == DataIntegrityIssueCodes.RentalProfileMonthlyAmountMismatch);
            Assert.Contains(result.Summaries, summary => summary.Code == DataIntegrityIssueCodes.RentalTemplateMissingAsset);
            Assert.Contains(result.Summaries, summary => summary.Code == DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DataIntegrityIssueService_ScanAsync_DoesNotTreatRentalTemplateItemIdAsItemMasterReference()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-data-integrity-template-items-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var activeItemId = Guid.Parse("7a111111-1111-1111-1111-111111111111");
            var deletedItemId = Guid.Parse("7a222222-2222-2222-2222-222222222222");
            var profileId = Guid.Parse("7a333333-3333-3333-3333-333333333333");

            db.Items.AddRange(
                new LocalItem
                {
                    Id = activeItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "정상 청구 품목",
                    NameMatchKey = "정상청구품목",
                    TrackingType = ItemTrackingTypes.NonStock,
                    Unit = "EA",
                    IsDirty = false
                },
                new LocalItem
                {
                    Id = deletedItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "삭제된 청구 품목",
                    NameMatchKey = "삭제된청구품목",
                    TrackingType = ItemTrackingTypes.NonStock,
                    Unit = "EA",
                    IsDeleted = true,
                    IsDirty = false
                });

            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "무결성 거래처",
                InstallSiteName = "무결성 현장",
                ItemName = "청구 템플릿",
                MonthlyAmount = 6_000m,
                BillingTemplateJson = JsonSerializer.Serialize(new object[]
                {
                    new
                    {
                        DisplayItemName = "ItemId 없는 청구품목",
                        Quantity = 1m,
                        UnitPrice = 1_000m,
                        Amount = 1_000m
                    },
                    new
                    {
                        ItemId = deletedItemId,
                        DisplayItemName = "삭제 품목 청구품목",
                        Quantity = 1m,
                        UnitPrice = 2_000m,
                        Amount = 2_000m
                    },
                    new
                    {
                        ItemId = activeItemId,
                        DisplayItemName = "정상 품목 청구품목",
                        Quantity = 1m,
                        UnitPrice = 3_000m,
                        Amount = 3_000m
                    }
                }),
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db);
            var result = await service.ScanAsync(CreateAdminSession());

            const string removedFalsePositiveCode = "rental_billing_template_missing_item_refs";
            Assert.DoesNotContain(result.Summaries, summary => summary.Code == removedFalsePositiveCode);
            Assert.DoesNotContain(result.Issues, issue => issue.Code == removedFalsePositiveCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DataIntegrityIssueService_ScanAsync_YeonsuSessionShowsOnlyYeonsuAlerts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-data-integrity-yeonsu-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetProfileId = Guid.Parse("81111111-1111-1111-1111-111111111111");
            var yeonsuAssetId = Guid.Parse("82222222-2222-2222-2222-222222222222");
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = usenetProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "USENET Customer",
                InstallSiteName = "USENET Site",
                ItemName = "USENET Printer",
                MonthlyAmount = 50_000m,
                BillingTemplateJson = "[]",
                IsDirty = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = yeonsuAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET|YEONSU-ZERO|SN-YEONSU-ZERO",
                CurrentCustomerName = "YEONSU Customer",
                CustomerName = "YEONSU Customer",
                InstallSiteName = "YEONSU Site",
                InstallLocation = "YEONSU Site",
                ItemName = "YEONSU Printer",
                ManagementNumber = "YEONSU-ZERO",
                MachineNumber = "SN-YEONSU-ZERO",
                AssetStatus = "ACTIVE",
                BillingEligibilityStatus = string.Empty,
                MonthlyFee = 0m,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db);
            var result = await service.ScanAsync(CreateYeonsuAdminSession());

            Assert.True(result.HasIssues);
            Assert.DoesNotContain(result.Issues, issue => string.Equals(issue.OfficeCode, OfficeCodeCatalog.Usenet, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Issues, issue => issue.ProfileId == usenetProfileId || issue.EntityId == usenetProfileId);

            var yeonsuIssue = Assert.Single(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee &&
                issue.AssetId == yeonsuAssetId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, yeonsuIssue.OfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DataIntegrityIssueService_ScanAsync_UsenetSessionShowsOnlyUsenetAlerts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-data-integrity-usenet-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetAssetId = Guid.Parse("83311111-1111-1111-1111-111111111111");
            var yeonsuAssetId = Guid.Parse("83322222-2222-2222-2222-222222222222");
            var itworldAssetId = Guid.Parse("83333333-3333-3333-3333-333333333333");

            db.RentalAssets.AddRange(
                CreateZeroFeeRentalAsset(usenetAssetId, OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup, "USENET"),
                CreateZeroFeeRentalAsset(yeonsuAssetId, OfficeCodeCatalog.Yeonsu, TenantScopeCatalog.UsenetGroup, "YEONSU"),
                CreateZeroFeeRentalAsset(itworldAssetId, OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld, "ITWORLD"));
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db);
            var result = await service.ScanAsync(CreateAdminSession());

            var zeroFeeIssues = result.Issues
                .Where(issue => issue.Code == DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee)
                .ToList();
            var issue = Assert.Single(zeroFeeIssues);
            Assert.Equal(usenetAssetId, issue.AssetId);
            Assert.Equal(OfficeCodeCatalog.Usenet, issue.OfficeCode);
            Assert.DoesNotContain(result.Issues, issue => issue.AssetId == yeonsuAssetId || issue.AssetId == itworldAssetId);
            Assert.DoesNotContain(result.Issues, issue =>
                string.Equals(issue.OfficeCode, OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(issue.OfficeCode, OfficeCodeCatalog.Itworld, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DataIntegrityIssueService_ScanAsync_UsesCanonicalScopeForMixedItworldProfile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-data-integrity-itworld-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("86666666-6666-6666-6666-666666666666");
            var assetId = Guid.Parse("87777777-7777-7777-7777-777777777777");
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                CustomerName = "ITWORLD Customer",
                InstallSiteName = "ITWORLD Site",
                ItemName = "MP2555",
                MonthlyAmount = 300000m,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "MP2555",
                        Quantity = 1m,
                        UnitPrice = 300000m,
                        Amount = 300000m,
                        IncludedAssetIds = [assetId]
                    }
                }),
                IsDirty = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                BillingProfileId = profileId,
                AssetKey = "ITWORLD|1405-003|MP2555",
                CurrentCustomerName = "ITWORLD Customer",
                CustomerName = "ITWORLD Customer",
                InstallSiteName = "ITWORLD Site",
                InstallLocation = "ITWORLD Site",
                ItemName = "MP2555",
                ManagementNumber = "1405-003",
                AssetStatus = "ACTIVE",
                BillingEligibilityStatus = string.Empty,
                MonthlyFee = 300000m,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db);
            var result = await service.ScanAsync(CreateItworldAdminSession());

            Assert.DoesNotContain(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalAssetProfileScopeMismatch &&
                issue.ProfileId == profileId &&
                issue.AssetId == assetId);

            var scopeIssue = Assert.Single(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalOperationalScopeMismatch &&
                issue.ProfileId == profileId);
            Assert.Contains("ITWORLD / ITWORLD / USENET", scopeIssue.CurrentValue);
            Assert.Contains("ITWORLD / ITWORLD / ITWORLD", scopeIssue.ExpectedValue);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncDiagnosticsSummary_UsesOnlyOpenIssuesForLastFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-diagnostics-summary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var resolvedAt = new DateTime(2026, 4, 21, 11, 34, 41, DateTimeKind.Utc);
            var openAt = resolvedAt.AddMinutes(5);
            db.Settings.Add(new LocalSetting
            {
                Key = "Sync.LastSuccessAt",
                Value = resolvedAt.AddSeconds(15).ToString("O")
            });
            db.SyncDiagnosticEvents.Add(new LocalSyncDiagnosticEvent
            {
                Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                OccurredAtUtc = resolvedAt,
                LastOccurredAtUtc = resolvedAt,
                Severity = "Warning",
                Category = "integrity",
                Subcategory = "runtime-periodic-integrity",
                SyncPhase = "runtime-periodic-integrity",
                RawMessage = "resolved integrity warning",
                NormalizedMessage = "resolved integrity warning",
                RecoveryAttempted = true,
                RecoverySucceeded = true,
                ResolvedAtUtc = resolvedAt.AddSeconds(2),
                Status = "Resolved"
            });
            await db.SaveChangesAsync();

            var session = new SessionState();
            var diagnostics = new SyncDiagnosticsService(session);
            var resolvedOnlySummary = await diagnostics.GetSummaryAsync();

            Assert.Equal(0, resolvedOnlySummary.OpenIssueCount);
            Assert.Null(resolvedOnlySummary.LastFailureAtUtc);

            db.SyncDiagnosticEvents.Add(new LocalSyncDiagnosticEvent
            {
                Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                OccurredAtUtc = openAt,
                LastOccurredAtUtc = openAt,
                Severity = "Error",
                Category = "sync",
                Subcategory = "general_sync_failure",
                SyncPhase = "manual-sync",
                RawMessage = "open sync failure",
                NormalizedMessage = "open sync failure",
                Status = "Open"
            });
            await db.SaveChangesAsync();

            var openSummary = await diagnostics.GetSummaryAsync();

            Assert.Equal(1, openSummary.OpenIssueCount);
            Assert.Equal(openAt, openSummary.LastFailureAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            // AppPaths is static for the test process; keep the temp root available
            // until process exit so parallel xUnit tests cannot delete the active DB path.
        }
    }

    [Fact]
    public async Task DirtySyncQueries_RequireMatchingDomainEditPermission()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-dirty-permission-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Dirty customer",
                NameMatchKey = "DIRTYCUSTOMER",
                IsDirty = true
            });
            db.Items.Add(new LocalItem
            {
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Dirty item",
                NameMatchKey = "DIRTYITEM",
                Unit = "EA",
                IsDirty = true
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                TotalAmount = 100m,
                IsDirty = true
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                ReceiptTotal = 100m,
                IsDirty = true
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                TransactionId = transactionId,
                FileName = "receipt.pdf",
                IsDirty = true
            });
            db.Payments.Add(new LocalPayment
            {
                InvoiceId = invoiceId,
                Amount = 100m,
                IsDirty = true
            });
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                TransferNumber = "TR-1",
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                IsDirty = true
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "PROFILE-1",
                CustomerName = "Dirty customer",
                IsDirty = true
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "ASSET-1",
                ManagementNumber = "A-1",
                CustomerName = "Dirty customer",
                ItemName = "Dirty item",
                IsDirty = true
            });
            await db.SaveChangesAsync();

            var noPermissionSession = CreateUserSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), noPermissionSession);

            Assert.Empty(await service.GetDirtyCustomersForSyncAsync(noPermissionSession));
            Assert.Empty(await service.GetDirtyItemsForSyncAsync(noPermissionSession));
            Assert.Empty(await service.GetDirtyInvoicesForSyncAsync(noPermissionSession));
            Assert.Empty(await service.GetDirtyTransactionsForSyncAsync(noPermissionSession));
            Assert.Empty(await service.GetDirtyTransactionAttachmentsForSyncAsync(noPermissionSession));
            Assert.Empty(await service.GetDirtyPaymentsForSyncAsync(noPermissionSession));
            Assert.Empty(await service.GetDirtyInventoryTransfersForSyncAsync(noPermissionSession));
            Assert.Empty(await service.GetDirtyRentalBillingProfilesForSyncAsync(noPermissionSession));
            Assert.Empty(await service.GetDirtyRentalAssetsForSyncAsync(noPermissionSession));

            var paymentSession = CreateUserSession(AppPermissionNames.PaymentEdit);
            Assert.Single(await service.GetDirtyTransactionsForSyncAsync(paymentSession));
            Assert.Single(await service.GetDirtyTransactionAttachmentsForSyncAsync(paymentSession));
            Assert.Single(await service.GetDirtyPaymentsForSyncAsync(paymentSession));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveInvoiceAsync_PurchaseStockIsAppliedOnlyAfterReceivingConfirmed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-purchase-receiving-stock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("81111111-1111-1111-1111-111111111111");
            var itemId = Guid.Parse("81122222-2222-2222-2222-222222222222");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Receiving customer",
                NameMatchKey = "RECEIVINGCUSTOMER"
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Receiving stock item",
                NameMatchKey = "RECEIVINGSTOCKITEM",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                PurchasePrice = 1000m
            });
            await db.SaveChangesAsync();

            var saved = await service.SaveInvoiceAsync(new LocalInvoice
            {
                Id = Guid.Parse("81133333-3333-3333-3333-333333333333"),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Purchase,
                PurchaseReceivingRequired = true,
                PurchaseReceivingStatus = InvoiceReceivingStatuses.Pending,
                InvoiceDate = new DateOnly(2026, 5, 1),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "Receiving stock item",
                        ItemTrackingType = ItemTrackingTypes.Stock,
                        Unit = "EA",
                        Quantity = 4m,
                        UnitPrice = 1000m,
                        LineAmount = 4000m
                    }
                }
            });

            Assert.Equal(0m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleOrDefaultAsync());
            Assert.Equal(0m, (await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId)).CurrentStock);

            saved.PurchaseReceivingRequired = true;
            saved.PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed;
            saved.PurchaseReceivedAtUtc = DateTime.UtcNow;
            saved.PurchaseReceivedByUsername = "admin";

            await service.SaveInvoiceAsync(saved);

            Assert.Equal(4m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(4m, (await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId)).CurrentStock);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ResetItemInventoryValueAsync_KeepsZeroAfterInventoryRebuild()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-inventory-reset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("80111111-1111-1111-1111-111111111111");
            var itemId = Guid.Parse("80222222-2222-2222-2222-222222222222");
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Stock customer",
                NameMatchKey = "STOCKCUSTOMER"
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Stock item",
                NameMatchKey = "STOCKITEM",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                PurchasePrice = 1000m
            });
            await db.SaveChangesAsync();

            await service.SaveInvoiceAsync(new LocalInvoice
            {
                Id = Guid.Parse("80333333-3333-3333-3333-333333333333"),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Purchase,
                PurchaseReceivingRequired = true,
                PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed,
                PurchaseReceivedAtUtc = DateTime.UtcNow,
                PurchaseReceivedByUsername = "admin",
                InvoiceDate = new DateOnly(2026, 5, 1),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "Stock item",
                        ItemTrackingType = ItemTrackingTypes.Stock,
                        Unit = "EA",
                        Quantity = 5m,
                        UnitPrice = 1000m,
                        LineAmount = 5000m
                    }
                }
            });

            Assert.Equal(5m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());

            await service.SaveInvoiceAsync(new LocalInvoice
            {
                Id = Guid.Parse("80444444-4444-4444-4444-444444444444"),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 5, 2),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "Stock item",
                        ItemTrackingType = ItemTrackingTypes.Stock,
                        Unit = "EA",
                        Quantity = 2m,
                        UnitPrice = 1500m,
                        LineAmount = 3000m
                    }
                }
            });

            Assert.Equal(3m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());

            var resetResult = await service.ResetItemInventoryValueAsync(itemId, session);
            Assert.True(resetResult.Success);
            Assert.Equal(0m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(0m, (await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId)).CurrentStock);

            Assert.True(await service.RebuildInventorySnapshotsForIntegrityAsync(session));
            Assert.Equal(0m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(0m, (await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId)).CurrentStock);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveInvoiceAsync_SalesCreate_AllowsNegativeLocalStockWhenInventoryIsShort()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-local-stock-shortage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("81222222-2222-2222-2222-222222222222");
            var itemId = Guid.Parse("81233333-3333-3333-3333-333333333333");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Local stock customer",
                NameMatchKey = "LOCALSTOCKCUSTOMER"
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Local stock item",
                NameMatchKey = "LOCALSTOCKITEM",
                SpecificationOriginal = "A4",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                PurchasePrice = 1000m
            });
            await db.SaveChangesAsync();

            await service.SaveInvoiceAsync(new LocalInvoice
            {
                Id = Guid.Parse("81244444-4444-4444-4444-444444444444"),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Purchase,
                PurchaseReceivingRequired = true,
                PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed,
                PurchaseReceivedAtUtc = DateTime.UtcNow,
                PurchaseReceivedByUsername = "admin",
                InvoiceDate = new DateOnly(2026, 5, 1),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "Local stock item",
                        ItemTrackingType = ItemTrackingTypes.Stock,
                        Unit = "EA",
                        Quantity = 1m,
                        UnitPrice = 1000m,
                        LineAmount = 1000m
                    }
                }
            });

            var result = await service.SaveInvoiceAsync(
                new LocalInvoice
                {
                    Id = Guid.Parse("81255555-5555-5555-5555-555555555555"),
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 5, 2),
                    Lines =
                    {
                        new LocalInvoiceLine
                        {
                            ItemId = itemId,
                            ItemNameOriginal = "Local stock item",
                            ItemTrackingType = ItemTrackingTypes.Stock,
                            Unit = "EA",
                            Quantity = 2m,
                            UnitPrice = 1500m,
                            LineAmount = 3000m
                        }
                    }
                },
                new InvoiceSaveContext
                {
                    Username = "admin",
                    Role = DomainConstants.RoleAdmin,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ForceOverride = true
                },
                session);

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Message);
            Assert.Equal(-1m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(2, await db.Invoices.CountAsync(invoice => invoice.IsLatestVersion && !invoice.IsDeleted));
            Assert.Equal(-1m, (await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId)).CurrentStock);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RejectInventoryTransferAsync_RestoresSourceStockAndKeepsTransferDirtyForSync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-transfer-reject-stock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var sourceSession = CreateAdminSession();
            var receiverSession = CreateYeonsuAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), sourceSession);
            var customerId = Guid.Parse("81400000-0000-0000-0000-000000000001");
            var itemId = Guid.Parse("81400000-0000-0000-0000-000000000002");
            var transferId = Guid.Parse("81400000-0000-0000-0000-000000000003");
            var lineId = Guid.Parse("81400000-0000-0000-0000-000000000004");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재고이동 반려 재고복구 거래처",
                NameMatchKey = "재고이동반려재고복구거래처"
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재고이동 반려 재고복구 품목",
                NameMatchKey = "재고이동반려재고복구품목",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                PurchasePrice = 1000m
            });
            await db.SaveChangesAsync();

            await service.SaveInvoiceAsync(new LocalInvoice
            {
                Id = Guid.Parse("81400000-0000-0000-0000-000000000005"),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Purchase,
                PurchaseReceivingRequired = true,
                PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed,
                PurchaseReceivedAtUtc = new DateTime(2026, 6, 25, 2, 0, 0, DateTimeKind.Utc),
                PurchaseReceivedByUsername = "admin",
                InvoiceDate = new DateOnly(2026, 6, 25),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 반려 재고복구 품목",
                        ItemTrackingType = ItemTrackingTypes.Stock,
                        Unit = "EA",
                        Quantity = 5m,
                        UnitPrice = 1000m,
                        LineAmount = 5000m
                    }
                }
            });

            var saveTransferResult = await service.SaveInventoryTransferAsync(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferDate = new DateOnly(2026, 6, 26),
                TransferNumber = "TR-REJECT-RESTORE",
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Lines =
                {
                    new LocalInventoryTransferLine
                    {
                        Id = lineId,
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 반려 재고복구 품목",
                        Unit = "EA",
                        Quantity = 2m
                    }
                }
            }, sourceSession);
            Assert.True(saveTransferResult.Success, saveTransferResult.Message);

            db.ChangeTracker.Clear();
            var savedTransfer = await db.InventoryTransfers
                .AsNoTracking()
                .SingleAsync(transfer => transfer.Id == transferId);
            Assert.True(savedTransfer.IsDirty);
            Assert.Equal(3m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(0m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
                .Select(stock => (decimal?)stock.Quantity)
                .SingleOrDefaultAsync() ?? 0m);

            var rejectResult = await service.RejectInventoryTransferAsync(
                transferId,
                "도착지 검수 반려",
                receiverSession,
                expectedRevision: savedTransfer.Revision);

            Assert.True(rejectResult.Success, rejectResult.Message);

            db.ChangeTracker.Clear();
            var rejectedTransfer = await db.InventoryTransfers
                .AsNoTracking()
                .SingleAsync(transfer => transfer.Id == transferId);
            Assert.Equal(InventoryTransferStatusNormalizer.Rejected, rejectedTransfer.TransferStatus);
            Assert.Equal("도착지 검수 반려", rejectedTransfer.RejectReason);
            Assert.Equal("yeonsu", rejectedTransfer.RejectedByUsername);
            Assert.True(rejectedTransfer.RejectedAtUtc.HasValue);
            Assert.True(rejectedTransfer.IsDirty);
            Assert.Equal(5m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(0m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
                .Select(stock => (decimal?)stock.Quantity)
                .SingleOrDefaultAsync() ?? 0m);
            Assert.Equal(5m, (await db.Items.SingleAsync(item => item.Id == itemId)).CurrentStock);

            var dirtyTransfers = await service.GetDirtyInventoryTransfersForSyncAsync(receiverSession);
            Assert.Contains(dirtyTransfers, transfer => transfer.Id == transferId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ConfirmInventoryTransferReceiptAsync_PersistsReceiptAndRebuildsWarehouseStock()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-transfer-confirm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var sourceSession = CreateAdminSession();
            var receiverSession = CreateYeonsuAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), sourceSession);
            var customerId = Guid.Parse("81300000-0000-0000-0000-000000000001");
            var itemId = Guid.Parse("81300000-0000-0000-0000-000000000002");
            var transferId = Guid.Parse("81300000-0000-0000-0000-000000000003");
            var lineId = Guid.Parse("81300000-0000-0000-0000-000000000004");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재고이동 수령확정 거래처",
                NameMatchKey = "재고이동수령확정거래처"
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재고이동 수령확정 품목",
                NameMatchKey = "재고이동수령확정품목",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                PurchasePrice = 1000m
            });
            await db.SaveChangesAsync();

            await service.SaveInvoiceAsync(new LocalInvoice
            {
                Id = Guid.Parse("81300000-0000-0000-0000-000000000005"),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Purchase,
                PurchaseReceivingRequired = true,
                PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed,
                PurchaseReceivedAtUtc = new DateTime(2026, 6, 25, 1, 0, 0, DateTimeKind.Utc),
                PurchaseReceivedByUsername = "admin",
                InvoiceDate = new DateOnly(2026, 6, 25),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 수령확정 품목",
                        ItemTrackingType = ItemTrackingTypes.Stock,
                        Unit = "EA",
                        Quantity = 5m,
                        UnitPrice = 1000m,
                        LineAmount = 5000m
                    }
                }
            });

            var saveTransferResult = await service.SaveInventoryTransferAsync(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferDate = new DateOnly(2026, 6, 26),
                TransferNumber = "TR-CONFIRM-REQUERY",
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Lines =
                {
                    new LocalInventoryTransferLine
                    {
                        Id = lineId,
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 수령확정 품목",
                        Unit = "EA",
                        Quantity = 2m
                    }
                }
            }, sourceSession);
            Assert.True(saveTransferResult.Success, saveTransferResult.Message);

            db.ChangeTracker.Clear();
            var savedTransfer = await db.InventoryTransfers
                .Include(transfer => transfer.Lines)
                .SingleAsync(transfer => transfer.Id == transferId);
            Assert.Equal(InventoryTransferStatusNormalizer.Pending, savedTransfer.TransferStatus);
            Assert.Equal(3m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(0m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
                .Select(stock => (decimal?)stock.Quantity)
                .SingleOrDefaultAsync() ?? 0m);

            var confirmResult = await service.ConfirmInventoryTransferReceiptAsync(
                transferId,
                new[]
                {
                    new LocalInventoryTransferLine
                    {
                        Id = lineId,
                        ReceivedQuantity = 2m,
                        ReceiptRemark = "검수 완료"
                    }
                },
                "도착 확인",
                receiverSession,
                expectedRevision: savedTransfer.Revision);

            Assert.True(confirmResult.Success, confirmResult.Message);

            db.ChangeTracker.Clear();
            var confirmedTransfer = await db.InventoryTransfers
                .Include(transfer => transfer.Lines)
                .SingleAsync(transfer => transfer.Id == transferId);
            var confirmedLine = Assert.Single(confirmedTransfer.Lines);
            Assert.Equal("수령확정", confirmedTransfer.TransferStatus);
            Assert.Equal("도착 확인", confirmedTransfer.ReceiveMemo);
            Assert.Equal("yeonsu", confirmedTransfer.ReceivedByUsername);
            Assert.True(confirmedTransfer.IsDirty);
            Assert.Equal(2m, confirmedLine.ReceivedQuantity);
            Assert.Equal(0m, confirmedLine.QuantityDifference);
            Assert.Equal("검수 완료", confirmedLine.ReceiptRemark);
            Assert.Equal(3m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(2m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(5m, (await db.Items.SingleAsync(item => item.Id == itemId)).CurrentStock);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveInventoryTransferAsync_RejectsWhenSourceWarehouseStockWouldBecomeNegative()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-transfer-stock-shortage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("81266666-6666-6666-6666-666666666666");
            var itemId = Guid.Parse("81277777-7777-7777-7777-777777777777");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Transfer stock customer",
                NameMatchKey = "TRANSFERSTOCKCUSTOMER"
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Transfer stock item",
                NameMatchKey = "TRANSFERSTOCKITEM",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                PurchasePrice = 1000m
            });
            await db.SaveChangesAsync();

            await service.SaveInvoiceAsync(new LocalInvoice
            {
                Id = Guid.Parse("81288888-8888-8888-8888-888888888888"),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                VoucherType = VoucherType.Purchase,
                PurchaseReceivingRequired = true,
                PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed,
                PurchaseReceivedAtUtc = DateTime.UtcNow,
                PurchaseReceivedByUsername = "admin",
                InvoiceDate = new DateOnly(2026, 5, 1),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "Transfer stock item",
                        ItemTrackingType = ItemTrackingTypes.Stock,
                        Unit = "EA",
                        Quantity = 1m,
                        UnitPrice = 1000m,
                        LineAmount = 1000m
                    }
                }
            });

            var result = await service.SaveInventoryTransferAsync(new LocalInventoryTransfer
            {
                Id = Guid.Parse("81299999-9999-9999-9999-999999999999"),
                TransferDate = new DateOnly(2026, 5, 2),
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Lines =
                {
                    new LocalInventoryTransferLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "Transfer stock item",
                        Unit = "EA",
                        Quantity = 2m
                    }
                }
            }, session);

            Assert.False(result.Success);
            Assert.Contains("재고가 부족", result.Message);
            Assert.Equal(1m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Empty(await db.InventoryTransfers.ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SetItemOfficeStockAsync_RejectsNegativeManualStock()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-negative-manual-stock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var itemId = Guid.Parse("812aaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Manual stock item",
                NameMatchKey = "MANUALSTOCKITEM",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA"
            });
            await db.SaveChangesAsync();

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SetItemOfficeStockAsync(itemId, -1m, OfficeCodeCatalog.Usenet));

            Assert.Contains("0 이상", exception.Message);
            Assert.Empty(await db.ItemWarehouseStocks.ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RepairNegativeItemWarehouseStocksAsync_PreservesAllowedNegativeSaleStock()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-repair-negative-stock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var itemId = Guid.Parse("812bbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Negative stock item",
                NameMatchKey = "NEGATIVESTOCKITEM",
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = -1m,
                IsDirty = false,
                Unit = "EA"
            });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = -1m,
                Revision = 11
            });
            await db.SaveChangesAsync();

            var repaired = await service.RepairNegativeItemWarehouseStocksAsync();

            Assert.Equal(0, repaired);
            Assert.Equal(-1m, await db.ItemWarehouseStocks
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            var item = await db.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == itemId);
            Assert.Equal(-1m, item.CurrentStock);
            Assert.False(item.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_PushDirtyAsync_IncludesNegativeWarehouseStockSnapshots()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-negative-stock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateOnlineAdminSession();
            var itemId = Guid.Parse("82433333-3333-3333-3333-333333333333");
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Negative sync stock item",
                NameMatchKey = "NEGATIVESYNCSTOCKITEM",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                CurrentStock = -2m
            });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = -2m,
                Revision = 7,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var handler = new CapturePushHandler();
            var api = new ErpApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var method = typeof(SyncService).GetMethod("PushDirtyAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var task = Assert.IsAssignableFrom<Task>(method!.Invoke(sync, new object?[] { api, session, true, CancellationToken.None }));
            await task;

            Assert.NotNull(handler.LastPushRequest);
            var stock = Assert.Single(handler.LastPushRequest!.ItemWarehouseStocks);
            Assert.Equal(itemId, stock.ItemId);
            Assert.Equal(OfficeCodeCatalog.UsenetMainWarehouse, stock.WarehouseCode);
            Assert.Equal(-2m, stock.Quantity);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_FlushPendingChangesAsync_ReconcilesWarehouseStockSnapshotFromPull()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-stock-reconcile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateOnlineAdminSession();
            var itemId = Guid.Parse("82434444-4444-4444-4444-444444444444");
            var localUpdatedAt = DateTime.UtcNow.AddMinutes(-10);
            var serverUpdatedAt = DateTime.UtcNow;
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Warehouse stock reconcile item",
                NameMatchKey = "WAREHOUSESTOCKRECONCILEITEM",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                CurrentStock = 2m,
                Revision = 7,
                UpdatedAtUtc = localUpdatedAt,
                IsDirty = false
            });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 2m,
                Revision = 7,
                UpdatedAtUtc = localUpdatedAt
            });
            await db.SaveChangesAsync();

            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var handler = new WarehouseStockPushThenPullHandler(itemId, serverQuantity: 5m, serverRevision: 12, serverUpdatedAt);
            var api = new ErpApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var synced = await sync.FlushPendingChangesAsync();

            var pendingSummary = await localState.GetPendingSyncSummaryAsync();
            var pendingMessage = string.Join(
                ", ",
                pendingSummary.Buckets.Select(bucket => $"{bucket.ScopeDisplayName}/{bucket.EntityDisplayName}:{bucket.Count}"));
            var lastError = await localState.GetSettingAsync("Sync.LastError");
            Assert.True(
                synced,
                $"lastError={lastError}; pending={pendingMessage}; push={handler.PushCount}; pull={handler.PullCount}");
            Assert.Equal(1, handler.PushCount);
            Assert.Equal(1, handler.PullCount);
            Assert.NotNull(handler.LastPushRequest);
            var pushedStock = Assert.Single(handler.LastPushRequest!.ItemWarehouseStocks);
            Assert.Equal(itemId, pushedStock.ItemId);
            Assert.Equal(OfficeCodeCatalog.UsenetMainWarehouse, pushedStock.WarehouseCode);
            Assert.Equal(2m, pushedStock.Quantity);
            Assert.Equal(7, pushedStock.ExpectedRevision);

            var storedStock = await db.ItemWarehouseStocks.AsNoTracking()
                .SingleAsync(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse);
            Assert.Equal(5m, storedStock.Quantity);
            Assert.Equal(12, storedStock.Revision);
            Assert.Equal(serverUpdatedAt, storedStock.UpdatedAtUtc);
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
            Assert.Equal("20", await localState.GetSettingAsync("LastSyncRevision"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void SyncService_IsPushRequestEmpty_TreatsWarehouseStockSnapshotsAsPayload()
    {
        var request = new SyncPushRequest();

        var empty = InvokePrivateStatic<bool>(
            typeof(SyncService),
            "IsPushRequestEmpty",
            request);

        request.ItemWarehouseStocks.Add(new ItemWarehouseStockDto
        {
            ItemId = Guid.NewGuid(),
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = -1m,
            Revision = 7
        });

        var withWarehouseStock = InvokePrivateStatic<bool>(
            typeof(SyncService),
            "IsPushRequestEmpty",
            request);

        Assert.True(empty);
        Assert.False(withWarehouseStock);
    }

    [Fact]
    public void SyncService_IsTransient_TreatsRetryableHttpFailuresAsTransient()
    {
        var gatewayTimeout = new HttpRequestException(
            "gateway timeout",
            inner: null,
            statusCode: System.Net.HttpStatusCode.GatewayTimeout);
        var internalServerError = new HttpRequestException(
            "server error",
            inner: null,
            statusCode: System.Net.HttpStatusCode.InternalServerError);
        var userCancelled = new TaskCanceledException("cancelled by caller");
        using var userCancellation = new CancellationTokenSource();
        userCancellation.Cancel();

        var gatewayTimeoutTransient = InvokePrivateStatic<bool>(
            typeof(SyncService),
            "IsTransient",
            gatewayTimeout,
            CancellationToken.None);
        var internalServerErrorTransient = InvokePrivateStatic<bool>(
            typeof(SyncService),
            "IsTransient",
            internalServerError,
            CancellationToken.None);
        var userCancelledTransient = InvokePrivateStatic<bool>(
            typeof(SyncService),
            "IsTransient",
            userCancelled,
            userCancellation.Token);

        Assert.True(gatewayTimeoutTransient);
        Assert.True(internalServerErrorTransient);
        Assert.False(userCancelledTransient);
    }

    [Fact]
    public void SyncService_IsTransient_TreatsWrappedRetryableFailuresAsTransient()
    {
        var wrappedTimeout = new InvalidOperationException(
            "outer sync wrapper",
            new TimeoutException("inner timeout"));
        var wrappedGatewayTimeout = new InvalidOperationException(
            "outer sync wrapper",
            new HttpRequestException("gateway timeout", null, System.Net.HttpStatusCode.GatewayTimeout));
        var aggregateRetryable = new AggregateException(
            new InvalidOperationException("wrapper", new TimeoutException("inner timeout")));
        var wrappedConflict = new InvalidOperationException(
            "outer sync wrapper",
            new HttpRequestException("conflict", null, System.Net.HttpStatusCode.Conflict));

        Assert.True(InvokePrivateStatic<bool>(
            typeof(SyncService),
            "IsTransient",
            wrappedTimeout,
            CancellationToken.None));
        Assert.True(InvokePrivateStatic<bool>(
            typeof(SyncService),
            "IsTransient",
            wrappedGatewayTimeout,
            CancellationToken.None));
        Assert.True(InvokePrivateStatic<bool>(
            typeof(SyncService),
            "IsTransient",
            aggregateRetryable,
            CancellationToken.None));
        Assert.False(InvokePrivateStatic<bool>(
            typeof(SyncService),
            "IsTransient",
            wrappedConflict,
            CancellationToken.None));
    }

    [Fact]
    public void ErpApiClient_IsTransient_TreatsWrappedRetryableFailuresAsTransient()
    {
        var wrappedTimeout = new InvalidOperationException(
            "outer api wrapper",
            new TimeoutException("inner timeout"));
        var wrappedServiceUnavailable = new InvalidOperationException(
            "outer api wrapper",
            new HttpRequestException("service unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable));
        var wrappedConflict = new InvalidOperationException(
            "outer api wrapper",
            new HttpRequestException("conflict", null, System.Net.HttpStatusCode.Conflict));

        Assert.True(InvokePrivateStatic<bool>(
            typeof(ErpApiClient),
            "IsTransient",
            wrappedTimeout,
            CancellationToken.None));
        Assert.True(InvokePrivateStatic<bool>(
            typeof(ErpApiClient),
            "IsTransient",
            wrappedServiceUnavailable,
            CancellationToken.None));
        Assert.False(InvokePrivateStatic<bool>(
            typeof(ErpApiClient),
            "IsTransient",
            wrappedConflict,
            CancellationToken.None));
    }

    [Theory]
    [InlineData("push")]
    [InlineData("pull")]
    [InlineData("status")]
    [InlineData("wait")]
    public async Task ErpApiClient_SyncEndpoints_RejectNullJsonPayloads(string operation)
    {
        var session = CreateOnlineAdminSession();
        var handler = new NullJsonSyncResponseHandler();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            switch (operation)
            {
                case "push":
                    await api.PushAsync(new SyncPushRequest());
                    break;
                case "pull":
                    await api.PullAsync(0);
                    break;
                case "status":
                    await api.GetSyncStatusAsync();
                    break;
                case "wait":
                    await api.WaitForSyncChangeAsync(0, TimeSpan.FromSeconds(1));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        });

        Assert.Contains("서버 응답 본문", exception.ToString());
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task SyncService_DeferPullRefreshUntilDirtyChangesArePushed_MarksRefreshAndShowsWaitingStatus()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-pull-defer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = new SessionState();
            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var diagnostics = new SyncDiagnosticsService(session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            string? statusMessage = null;
            sync.SyncStatusChanged += message => statusMessage = message;

            await InvokePrivateInstanceTaskResultAsync(
                sync,
                "DeferPullRefreshUntilDirtyChangesArePushedAsync",
                3,
                new DbUpdateConcurrencyException("pull conflict"));

            Assert.True(await localState.IsServerMirrorRefreshRequiredAsync());
            Assert.NotNull(statusMessage);
            Assert.Contains("미동기화 변경 3", statusMessage);
            Assert.Contains("자동으로 다시 불러옵니다", statusMessage);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void SyncService_DispatcherRequestsAreHandledOnlyAfterStart()
    {
        using var db = new LocalDbContext();
        var session = new SessionState();
        session.SetSession(
            "test-token",
            new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });
        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rental = new RentalStateService(db);
        var diagnostics = new SyncDiagnosticsService(session);
        var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);

        using var idleSync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);
        dispatcher.RequestDebouncedSync();

        Assert.False(idleSync.HasActiveOrQueuedSync);

        using var startedSync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);
        startedSync.Start(TimeSpan.FromMinutes(5));
        dispatcher.RequestDebouncedSync();

        Assert.True(startedSync.HasActiveOrQueuedSync);
    }

    [Fact]
    public async Task SyncService_MarkOutboxAcknowledgedAsync_AcknowledgesOnlyAcceptedEntities()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-accepted-outbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateOnlineAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            var diagnostics = new SyncDiagnosticsService(session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var itemId = Guid.Parse("83611111-1111-1111-1111-111111111111");
            var customerId = Guid.Parse("83622222-2222-2222-2222-222222222222");
            const string itemMutationId = "device|LocalItem|item";
            const string customerMutationId = "device|LocalCustomer|customer";
            var now = DateTime.UtcNow;

            var request = new SyncPushRequest();
            request.Items.Add(new ItemDto
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Accepted item",
                NameMatchKey = "ACCEPTEDITEM",
                UpdatedAtUtc = now,
                Revision = 3,
                ExpectedRevision = 3,
                MutationId = itemMutationId
            });
            request.Customers.Add(new CustomerDto
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Conflicted customer",
                NameMatchKey = "CONFLICTEDCUSTOMER",
                UpdatedAtUtc = now,
                Revision = 4,
                ExpectedRevision = 4,
                MutationId = customerMutationId
            });

            db.SyncOutboxEntries.AddRange(
                new LocalSyncOutboxEntry
                {
                    Id = Guid.NewGuid(),
                    MutationId = itemMutationId,
                    DeviceId = "device",
                    EntityName = nameof(LocalItem),
                    EntityId = itemId,
                    ExpectedRevision = 3,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    Status = "Sent",
                    SentAtUtc = now
                },
                new LocalSyncOutboxEntry
                {
                    Id = Guid.NewGuid(),
                    MutationId = customerMutationId,
                    DeviceId = "device",
                    EntityName = nameof(LocalCustomer),
                    EntityId = customerId,
                    ExpectedRevision = 4,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    Status = "Sent",
                    SentAtUtc = now
                });
            await db.SaveChangesAsync();

            var accepted = new List<SyncAcceptedRevisionDto>
            {
                new()
                {
                    EntityName = "Item",
                    EntityId = itemId,
                    Revision = 5,
                    UpdatedAtUtc = now.AddSeconds(1)
                }
            };

            await InvokePrivateInstanceTaskResultAsync(
                sync,
                "MarkOutboxAcknowledgedAsync",
                request,
                accepted,
                CancellationToken.None);

            var itemOutbox = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.MutationId == itemMutationId);
            var customerOutbox = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.MutationId == customerMutationId);

            Assert.Equal("Acknowledged", itemOutbox.Status);
            Assert.NotNull(itemOutbox.AcknowledgedAtUtc);
            Assert.Equal("Sent", customerOutbox.Status);
            Assert.Null(customerOutbox.AcknowledgedAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RepairMissingItemMastersFromOperationalReferencesAsync_DoesNotRecoverSoftDeletedItems()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-missing-item-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("80555555-5555-5555-5555-555555555555");
            var itemId = Guid.Parse("80666666-6666-6666-6666-666666666666");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Repair customer",
                NameMatchKey = "REPAIRCUSTOMER"
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Hidden item",
                NameMatchKey = "HIDDENITEM",
                TrackingType = ItemTrackingTypes.NonStock,
                Unit = "EA"
            });
            await db.SaveChangesAsync();

            await service.SaveInvoiceAsync(new LocalInvoice
            {
                Id = Guid.Parse("80777777-7777-7777-7777-777777777777"),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 5, 3),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemId = itemId,
                        ItemNameOriginal = "Hidden item",
                        ItemTrackingType = ItemTrackingTypes.NonStock,
                        Unit = "EA",
                        Quantity = 1m,
                        UnitPrice = 100m,
                        LineAmount = 100m
                    }
                }
            });

            var item = await db.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == itemId);
            item.IsDeleted = true;
            item.IsDirty = true;
            await db.SaveChangesAsync();

            var repairResult = await service.RepairMissingItemMastersFromOperationalReferencesAsync(session);

            Assert.False(repairResult.HasChanges);
            Assert.Equal(0, repairResult.RecoveredDeletedCount);
            Assert.True(await db.Items.IgnoreQueryFilters()
                .Where(current => current.Id == itemId)
                .Select(current => current.IsDeleted)
                .SingleAsync());
            Assert.Empty(await service.GetItemsAsync(session));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetItemVendorPurchasePricesAsync_ReturnsLatestPurchasePricePerVendor()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-vendor-price-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var vendorAId = Guid.Parse("80888888-8888-8888-8888-888888888888");
            var vendorBId = Guid.Parse("80999999-9999-9999-9999-999999999999");
            var itemId = Guid.Parse("80aaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

            db.Customers.AddRange(
                new LocalCustomer
                {
                    Id = vendorAId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "매입처 A",
                    NameMatchKey = "VENDORA",
                    TradeType = CustomerTradeTypes.Purchase
                },
                new LocalCustomer
                {
                    Id = vendorBId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "매입처 B",
                    NameMatchKey = "VENDORB",
                    TradeType = CustomerTradeTypes.Purchase
                });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "공통 품목",
                NameMatchKey = "COMMONITEM",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                PurchasePrice = 10000m
            });
            await db.SaveChangesAsync();

            await SavePurchaseInvoiceAsync(service, vendorAId, itemId, new DateOnly(2026, 5, 1), 37000m, "A-OLD");
            await SavePurchaseInvoiceAsync(service, vendorAId, itemId, new DateOnly(2026, 5, 5), 39000m, "A-NEW");
            await SavePurchaseInvoiceAsync(service, vendorBId, itemId, new DateOnly(2026, 5, 3), 41000m, "B-ONE");

            var rows = await service.GetItemVendorPurchasePricesAsync(itemId, session);
            var priceMap = rows.ToDictionary(row => row.VendorCustomerId, row => row.UnitPrice);

            Assert.Equal(2, rows.Count);
            Assert.Equal(39000m, priceMap[vendorAId]);
            Assert.Equal(41000m, priceMap[vendorBId]);

            var customerPriceMap = await service.GetLatestPurchasePriceByItemForCustomerAsync(vendorAId, session);
            Assert.True(customerPriceMap.TryGetValue(itemId, out var latestVendorAPrice));
            Assert.Equal(39000m, latestVendorAPrice);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteInvoiceAsync_RejectsStaleExpectedRevision()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-invoice-delete-concurrency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var invoiceId = Guid.Parse("81111111-1111-1111-1111-111111111111");
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = Guid.Parse("82222222-2222-2222-2222-222222222222"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                TotalAmount = 100m,
                Revision = 7,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var result = await service.DeleteInvoiceAsync(invoiceId, session, expectedRevision: 6);

            Assert.False(result.Success);
            Assert.True(result.ConcurrencyConflict);
            var stored = await db.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == invoiceId);
            Assert.False(stored.IsDeleted);
            Assert.False(stored.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteInvoiceAsync_RequiresPaymentEditWhenDeletingLinkedPayments()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-invoice-delete-payment-permission-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var previousAppRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var invoiceOnlySession = CreateUserSession(AppPermissionNames.InvoiceEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), invoiceOnlySession);
            var customerId = Guid.Parse("8a111111-1111-1111-1111-111111111111");
            var invoiceId = Guid.Parse("8a222222-2222-2222-2222-222222222222");
            var paymentId = Guid.Parse("8a333333-3333-3333-3333-333333333333");
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Invoice delete payment permission customer",
                NameMatchKey = "INVOICEDELETEPAYMENTPERMISSIONCUSTOMER",
                IsDirty = false
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 23),
                InvoiceNumber = "PAYMENT-PERMISSION-GUARD",
                TotalAmount = 100_000m,
                SupplyAmount = 100_000m,
                IsLatestVersion = true,
                IsDirty = false
            });
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 24),
                Amount = 100_000m,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var result = await service.DeleteInvoiceAsync(invoiceId, invoiceOnlySession);

            Assert.False(result.Success);
            Assert.Contains("수금", result.Message);
            var storedInvoice = await db.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == invoiceId);
            var storedPayment = await db.Payments.IgnoreQueryFilters().SingleAsync(payment => payment.Id == paymentId);
            Assert.False(storedInvoice.IsDeleted);
            Assert.False(storedInvoice.IsDirty);
            Assert.False(storedPayment.IsDeleted);
            Assert.False(storedPayment.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", previousAppRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CustomerContractMutations_RejectStaleExpectedRevision()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-contract-concurrency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = Guid.Parse("83333333-3333-3333-3333-333333333333");
            var updateContractId = Guid.Parse("84444444-4444-4444-4444-444444444444");
            var deleteContractId = Guid.Parse("85555555-5555-5555-5555-555555555555");
            var primaryContractId = Guid.Parse("86666666-6666-6666-6666-666666666666");
            var attachContractId = Guid.Parse("87777777-7777-7777-7777-777777777777");
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Concurrency Customer",
                NameMatchKey = "CONCURRENCYCUSTOMER",
                IsDirty = false
            });
            db.CustomerContracts.AddRange(
                new LocalCustomerContract
                {
                    Id = updateContractId,
                    CustomerId = customerId,
                    ContractType = "거래계약서",
                    Description = "original",
                    Revision = 10,
                    IsDirty = false
                },
                new LocalCustomerContract
                {
                    Id = deleteContractId,
                    CustomerId = customerId,
                    ContractType = "거래계약서",
                    FileName = "delete.pdf",
                    Revision = 20,
                    IsDirty = false
                },
                new LocalCustomerContract
                {
                    Id = primaryContractId,
                    CustomerId = customerId,
                    ContractType = "거래계약서",
                    Revision = 30,
                    IsDirty = false
                },
                new LocalCustomerContract
                {
                    Id = attachContractId,
                    CustomerId = customerId,
                    ContractType = "거래계약서",
                    Revision = 40,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var pdfPath = Path.Combine(tempRoot, "contract.pdf");
            await File.WriteAllBytesAsync(pdfPath, [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34]);

            var updateResult = await service.UpdateCustomerContractAsync(
                updateContractId,
                "거래계약서",
                null,
                null,
                "changed",
                false,
                session,
                expectedRevision: 9);
            var deleteResult = await service.DeleteCustomerContractAsync(deleteContractId, session, expectedRevision: 19);
            var primaryResult = await service.SetPrimaryCustomerContractAsync(primaryContractId, session, expectedRevision: 29);
            var attachResult = await service.AttachCustomerContractPdfAsync(
                attachContractId,
                pdfPath,
                "거래계약서",
                null,
                null,
                "attached",
                false,
                session,
                expectedRevision: 39);

            Assert.All(new[] { updateResult, deleteResult, primaryResult, attachResult }, result =>
            {
                Assert.False(result.Success);
                Assert.True(result.ConcurrencyConflict);
            });
            var storedUpdate = await db.CustomerContracts.IgnoreQueryFilters().SingleAsync(contract => contract.Id == updateContractId);
            var storedDelete = await db.CustomerContracts.IgnoreQueryFilters().SingleAsync(contract => contract.Id == deleteContractId);
            var storedPrimary = await db.CustomerContracts.IgnoreQueryFilters().SingleAsync(contract => contract.Id == primaryContractId);
            var storedAttach = await db.CustomerContracts.IgnoreQueryFilters().SingleAsync(contract => contract.Id == attachContractId);
            Assert.Equal("original", storedUpdate.Description);
            Assert.False(storedDelete.IsDeleted);
            Assert.False(storedPrimary.IsPrimary);
            Assert.Equal(0, storedAttach.FileSize);
            Assert.False(storedUpdate.IsDirty);
            Assert.False(storedDelete.IsDirty);
            Assert.False(storedPrimary.IsDirty);
            Assert.False(storedAttach.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveTransactionAttachmentAsync_RejectsUnsupportedFileType()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-attachment-type-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var transactionId = Guid.Parse("87777777-7777-7777-7777-777777777777");
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = Guid.Parse("87777777-7777-7777-7777-777777777778"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                ReceiptTotal = 100m,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var unsupportedFilePath = Path.Combine(tempRoot, "payload.exe");
            await File.WriteAllBytesAsync(unsupportedFilePath, [0x4D, 0x5A, 0x90, 0x00]);

            var result = await service.SaveTransactionAttachmentAsync(
                transactionId,
                unsupportedFilePath,
                "입금확인증",
                "unsupported executable must not be stored",
                session);

            Assert.False(result.Success);
            Assert.True(result.PermissionDenied);
            Assert.Contains("PDF 또는 이미지", result.Message);
            Assert.Empty(await db.TransactionAttachments.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveTransactionAttachmentAsync_RejectsFileContentThatDoesNotMatchExtension()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-attachment-signature-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var transactionId = Guid.Parse("86666666-6666-6666-6666-666666666666");
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = Guid.Parse("86666666-6666-6666-6666-666666666667"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                ReceiptTotal = 100m,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var fakePdfPath = Path.Combine(tempRoot, "receipt.pdf");
            await File.WriteAllBytesAsync(fakePdfPath, [0x4D, 0x5A, 0x90, 0x00]);

            var result = await service.SaveTransactionAttachmentAsync(
                transactionId,
                fakePdfPath,
                "입금확인증",
                "fake pdf content must not be stored",
                session);

            Assert.False(result.Success);
            Assert.True(result.PermissionDenied);
            Assert.Contains("파일 내용", result.Message);
            Assert.Empty(await db.TransactionAttachments.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TransactionAttachmentMutations_RejectStaleExpectedRevision()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-attachment-concurrency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var transactionId = Guid.Parse("88888888-8888-8888-8888-888888888888");
            var verifyAttachmentId = Guid.Parse("89999999-9999-9999-9999-999999999999");
            var deleteAttachmentId = Guid.Parse("8aaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = Guid.Parse("8bbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                ReceiptTotal = 100m,
                IsDirty = false
            });
            db.TransactionAttachments.AddRange(
                new LocalTransactionAttachment
                {
                    Id = verifyAttachmentId,
                    TransactionId = transactionId,
                    FileName = "verify.pdf",
                    VerificationStatus = "미확인",
                    Revision = 15,
                    IsDirty = false
                },
                new LocalTransactionAttachment
                {
                    Id = deleteAttachmentId,
                    TransactionId = transactionId,
                    FileName = "delete.pdf",
                    VerificationStatus = "미확인",
                    Revision = 25,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var verifyResult = await service.UpdateTransactionAttachmentVerificationAsync(
                verifyAttachmentId,
                "확인완료",
                "ok",
                session,
                expectedRevision: 14);
            var deleteResult = await service.DeleteTransactionAttachmentAsync(
                deleteAttachmentId,
                session,
                expectedRevision: 24);

            Assert.False(verifyResult.Success);
            Assert.True(verifyResult.ConcurrencyConflict);
            Assert.False(deleteResult.Success);
            Assert.True(deleteResult.ConcurrencyConflict);
            var storedVerify = await db.TransactionAttachments.IgnoreQueryFilters().SingleAsync(attachment => attachment.Id == verifyAttachmentId);
            var storedDelete = await db.TransactionAttachments.IgnoreQueryFilters().SingleAsync(attachment => attachment.Id == deleteAttachmentId);
            Assert.Equal("미확인", storedVerify.VerificationStatus);
            Assert.False(storedVerify.IsDirty);
            Assert.False(storedDelete.IsDeleted);
            Assert.False(storedDelete.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalBillingAggregateRows_CarryProfileRevisions()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-aggregate-revisions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("8ccccccc-cccc-cccc-cccc-cccccccccccc");
            var firstProfileId = Guid.Parse("8ddddddd-dddd-dddd-dddd-dddddddddddd");
            var secondProfileId = Guid.Parse("8eeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Rental Aggregate Customer",
                NameMatchKey = "RENTALAGGREGATECUSTOMER",
                IsDirty = false
            });
            db.RentalBillingProfiles.AddRange(
                new LocalRentalBillingProfile
                {
                    Id = firstProfileId,
                    CustomerId = customerId,
                    CustomerName = "Rental Aggregate Customer",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "PROFILE-A",
                    ItemName = "Rental A",
                    MonthlyAmount = 100m,
                    Revision = 101,
                    IsDirty = false
                },
                new LocalRentalBillingProfile
                {
                    Id = secondProfileId,
                    CustomerId = customerId,
                    CustomerName = "Rental Aggregate Customer",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "PROFILE-B",
                    ItemName = "Rental B",
                    MonthlyAmount = 200m,
                    Revision = 202,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ExpandCustomerSummaryRows = false,
                    ReferenceDate = new DateOnly(2026, 5, 12)
                },
                CreateAdminSession());

            var aggregateRow = Assert.Single(rows);
            Assert.True(aggregateRow.IsAggregateRow);
            Assert.Equal(101, aggregateRow.GroupedProfileRevisions[firstProfileId]);
            Assert.Equal(202, aggregateRow.GroupedProfileRevisions[secondProfileId]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalBillingUnlinkedAssets_GroupByCustomerAndExposeSetupStatus()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-unlinked-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("9aaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "청구설정 필요 거래처",
                NameMatchKey = "청구설정필요거래처",
                IsDirty = false
            });
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = Guid.Parse("9bbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    CustomerName = "청구설정 필요 거래처",
                    CurrentCustomerName = "청구설정 필요 거래처",
                    ItemName = "복합기 A",
                    MachineNumber = "UNLINKED-A",
                    InstallLocation = "1층",
                    MonthlyFee = 100_000m,
                    AssetStatus = "임대",
                    IsDirty = false
                },
                new LocalRentalAsset
                {
                    Id = Guid.Parse("9ccccccc-cccc-cccc-cccc-cccccccccccc"),
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    CustomerName = "청구설정 필요 거래처",
                    CurrentCustomerName = "청구설정 필요 거래처",
                    ItemName = "복합기 B",
                    MachineNumber = "UNLINKED-B",
                    InstallLocation = "2층",
                    MonthlyFee = 150_000m,
                    AssetStatus = "임대",
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var groupedRows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ExpandCustomerSummaryRows = false,
                    ReferenceDate = new DateOnly(2026, 5, 12)
                },
                CreateAdminSession());

            var groupedRow = Assert.Single(groupedRows);
            Assert.True(groupedRow.IsAggregateRow);
            Assert.False(groupedRow.HasPersistedProfile);
            Assert.Equal(2, groupedRow.GroupedUnlinkedAssetCount);
            Assert.Equal("생성필요 2대", groupedRow.BillingSetupStatus);
            Assert.Equal("청구 전", groupedRow.SettlementStatusDisplay);
            Assert.Equal(250_000m, groupedRow.CurrentBilledAmount);

            var filteredRows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    Status = "청구설정 필요",
                    ExpandCustomerSummaryRows = true,
                    ReferenceDate = new DateOnly(2026, 5, 12)
                },
                CreateAdminSession());

            Assert.Equal(2, filteredRows.Count);
            Assert.All(filteredRows, row =>
            {
                Assert.True(row.RequiresBillingProfileCreation);
                Assert.Equal("생성필요", row.BillingSetupStatus);
                Assert.Equal("청구 전", row.SettlementStatusDisplay);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalBillingUnlinkedAssets_DefaultAllCapsButFocusedStatusShowsMore()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-unlinked-cap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var totalAssets = RentalStateService.BillingUnlinkedDefaultResultLimit + 2;
            for (var index = 0; index < totalAssets; index++)
            {
                db.RentalAssets.Add(new LocalRentalAsset
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    CustomerName = $"청구설정 필요 거래처 {index:D3}",
                    CurrentCustomerName = $"청구설정 필요 거래처 {index:D3}",
                    ItemName = $"복합기 {index:D3}",
                    MachineNumber = $"UNLINKED-CAP-{index:D3}",
                    InstallLocation = $"{index:D3}호",
                    ManagementNumber = $"CAP-{index:D3}",
                    MonthlyFee = 100_000m + index,
                    AssetStatus = "임대",
                    IsDirty = false
                });
            }

            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var session = CreateAdminSession();
            var defaultRows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ExpandCustomerSummaryRows = true,
                    ReferenceDate = new DateOnly(2026, 5, 12)
                },
                session);

            Assert.Equal(RentalStateService.BillingUnlinkedDefaultResultLimit, defaultRows.Count);
            Assert.Equal(RentalStateService.BillingUnlinkedDefaultResultLimit, defaultRows.Sum(row => row.GroupedUnlinkedAssetCount));

            var focusedRows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    Status = "청구설정 필요",
                    ExpandCustomerSummaryRows = true,
                    ReferenceDate = new DateOnly(2026, 5, 12)
                },
                session);

            Assert.Equal(totalAssets, focusedRows.Count);
            Assert.Equal(totalAssets, focusedRows.Sum(row => row.GroupedUnlinkedAssetCount));
            Assert.All(focusedRows, row => Assert.True(row.RequiresBillingProfileCreation));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false, false, null, true)]
    [InlineData(false, false, "", true)]
    [InlineData(false, false, "청구설정 필요", true)]
    [InlineData(false, false, "청구중", false)]
    [InlineData(true, false, null, false)]
    [InlineData(false, true, null, false)]
    [InlineData(false, true, "청구설정 필요", false)]
    public void RentalBillingUnlinkedAssets_SkipsUnlinkedQueryForPeriodAlertFilters(
        bool dueOnly,
        bool pastDueOnly,
        string? status,
        bool expected)
    {
        var actual = InvokePrivateStatic<bool>(
            typeof(RentalStateService),
            "ShouldLoadUnlinkedBillingAssets",
            new RentalBillingFilter
            {
                DueOnly = dueOnly,
                PastDueOnly = pastDueOnly,
                Status = status ?? string.Empty
            });

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RentalBillingDueOnlyIndividualProfilePrefilter_UsesAlertDateBeforeRowBuild()
    {
        var referenceDate = new DateOnly(2026, 5, 12);
        var dueByDocumentLeadId = Guid.Parse("9ddddddd-dddd-dddd-dddd-dddddddddddd");
        var notDueId = Guid.Parse("9eeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var profiles = new List<LocalRentalBillingProfile>
        {
            new()
            {
                Id = dueByDocumentLeadId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "서류발송 임박 거래처",
                ItemName = "복합기 A",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingAnchorMonth = 5,
                DocumentIssueMode = RentalBillingScheduleRules.DocumentIssueModeDaysBeforeDueDate,
                DocumentLeadDays = 20,
                IsActive = true
            },
            new()
            {
                Id = notDueId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "아직 여유있는 거래처",
                ItemName = "복합기 B",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingAnchorMonth = 5,
                DocumentIssueMode = RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate,
                IsActive = true
            }
        };

        var filtered = InvokePrivateStatic<List<LocalRentalBillingProfile>>(
            typeof(RentalStateService),
            "ApplyDueOnlyProfilePrefilter",
            profiles,
            new RentalBillingFilter
            {
                DueOnly = true,
                ExpandCustomerSummaryRows = true,
                ReferenceDate = referenceDate
            },
            7,
            referenceDate);

        var row = Assert.Single(filtered);
        Assert.Equal(dueByDocumentLeadId, row.Id);
    }

    [Fact]
    public void RentalBillingDueOnlyProfilePrefilter_GroupedScopeKeepsDueCustomerGroupOnly()
    {
        var referenceDate = new DateOnly(2026, 5, 12);
        var dueSameGroupId = Guid.Parse("9aaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var notDueSameGroupId = Guid.Parse("9bbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var notDueOtherGroupId = Guid.Parse("9ccccccc-cccc-cccc-cccc-cccccccccccc");
        var profiles = new List<LocalRentalBillingProfile>
        {
            new()
            {
                Id = dueSameGroupId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "그룹 유지 거래처",
                ItemName = "복합기 A",
                BillingDay = 12,
                BillingCycleMonths = 1,
                BillingAnchorMonth = 5,
                IsActive = true
            },
            new()
            {
                Id = notDueSameGroupId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "그룹 유지 거래처",
                ItemName = "복합기 B",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingAnchorMonth = 5,
                IsActive = true
            },
            new()
            {
                Id = notDueOtherGroupId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "아직 여유있는 거래처",
                ItemName = "복합기 C",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingAnchorMonth = 5,
                IsActive = true
            }
        };

        var filtered = InvokePrivateStatic<List<LocalRentalBillingProfile>>(
            typeof(RentalStateService),
            "ApplyDueOnlyProfilePrefilter",
            profiles,
            new RentalBillingFilter
            {
                DueOnly = true,
                ExpandCustomerSummaryRows = false,
                ReferenceDate = referenceDate
            },
            7,
            referenceDate);

        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, profile => profile.Id == dueSameGroupId);
        Assert.Contains(filtered, profile => profile.Id == notDueSameGroupId);
        Assert.DoesNotContain(filtered, profile => profile.Id == notDueOtherGroupId);
    }

    [Fact]
    public async Task RentalBillingPastDueIndividualProfilePrefilter_KeepsOnlyPastUnresolvedProfiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-pastdue-prefilter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var referenceDate = new DateOnly(2026, 5, 12);
            var unresolvedProfileId = Guid.Parse("91111111-1111-1111-1111-111111111111");
            var settledProfileId = Guid.Parse("92222222-2222-2222-2222-222222222222");
            var futureProfileId = Guid.Parse("93333333-3333-3333-3333-333333333333");

            var profiles = new List<LocalRentalBillingProfile>
            {
                BuildPastDuePrefilterProfile(
                    unresolvedProfileId,
                    "과거 미처리 거래처",
                    BuildPastDuePrefilterRun(
                        Guid.Parse("9aaaaaaa-1111-1111-1111-111111111111"),
                        new DateOnly(2026, 4, 25),
                        100_000m,
                        0m)),
                BuildPastDuePrefilterProfile(
                    settledProfileId,
                    "완납 거래처",
                    BuildPastDuePrefilterRun(
                        Guid.Parse("9bbbbbbb-2222-2222-2222-222222222222"),
                        new DateOnly(2026, 4, 25),
                        100_000m,
                        100_000m)),
                BuildPastDuePrefilterProfile(
                    futureProfileId,
                    "현재월 거래처",
                    BuildPastDuePrefilterRun(
                        Guid.Parse("9ccccccc-3333-3333-3333-333333333333"),
                        new DateOnly(2026, 5, 25),
                        100_000m,
                        0m))
            };

            var service = new RentalStateService(db);
            var filtered = await InvokePrivateInstanceAsync<List<LocalRentalBillingProfile>>(
                service,
                "ApplyPastDueOnlyProfilePrefilterAsync",
                profiles,
                new RentalBillingFilter
                {
                    PastDueOnly = true,
                    ExpandCustomerSummaryRows = true,
                    ReferenceDate = referenceDate
                },
                referenceDate,
                CancellationToken.None);

            Assert.NotNull(filtered);
            var row = Assert.Single(filtered);
            Assert.Equal(unresolvedProfileId, row.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalBillingPastDueGroupedProfilePrefilter_KeepsWholePastDueCustomerGroup()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-pastdue-group-prefilter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var referenceDate = new DateOnly(2026, 5, 12);
            var groupedCustomerId = Guid.Parse("94444444-4444-4444-4444-444444444444");
            var unresolvedProfileId = Guid.Parse("95555555-5555-5555-5555-555555555555");
            var companionProfileId = Guid.Parse("96666666-6666-6666-6666-666666666666");
            var unrelatedProfileId = Guid.Parse("97777777-7777-7777-7777-777777777777");

            var profiles = new List<LocalRentalBillingProfile>
            {
                BuildPastDuePrefilterProfile(
                    unresolvedProfileId,
                    "묶음 과거 미처리 거래처",
                    BuildPastDuePrefilterRun(
                        Guid.Parse("9ddddddd-4444-4444-4444-444444444444"),
                        new DateOnly(2026, 4, 25),
                        100_000m,
                        0m),
                    groupedCustomerId),
                BuildPastDuePrefilterProfile(
                    companionProfileId,
                    "묶음 과거 미처리 거래처",
                    BuildPastDuePrefilterRun(
                        Guid.Parse("9eeeeeee-5555-5555-5555-555555555555"),
                        new DateOnly(2026, 5, 25),
                        100_000m,
                        0m),
                    groupedCustomerId),
                BuildPastDuePrefilterProfile(
                    unrelatedProfileId,
                    "미처리 없는 거래처",
                    BuildPastDuePrefilterRun(
                        Guid.Parse("9fffffff-6666-6666-6666-666666666666"),
                        new DateOnly(2026, 4, 25),
                        100_000m,
                        100_000m))
            };

            var service = new RentalStateService(db);
            var filtered = await InvokePrivateInstanceAsync<List<LocalRentalBillingProfile>>(
                service,
                "ApplyPastDueOnlyProfilePrefilterAsync",
                profiles,
                new RentalBillingFilter
                {
                    PastDueOnly = true,
                    ExpandCustomerSummaryRows = false,
                    ReferenceDate = referenceDate
                },
                referenceDate,
                CancellationToken.None);

            Assert.NotNull(filtered);
            Assert.Equal(
                new[] { unresolvedProfileId, companionProfileId },
                filtered.Select(profile => profile.Id).OrderBy(id => id).ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void RentalBillingPastDueProfilePrefilter_RunsForAnyPastDueMode(
        bool pastDueOnly,
        bool expandCustomerSummaryRows,
        bool expected)
    {
        var actual = InvokePrivateStatic<bool>(
            typeof(RentalStateService),
            "ShouldPrefilterPastDueOnlyBillingProfiles",
            new RentalBillingFilter
            {
                PastDueOnly = pastDueOnly,
                ExpandCustomerSummaryRows = expandCustomerSummaryRows
            });

        Assert.Equal(expected, actual);
    }

    private static LocalRentalBillingProfile BuildPastDuePrefilterProfile(
        Guid profileId,
        string customerName,
        RentalBillingRunModel run,
        Guid? customerId = null)
        => new()
        {
            Id = profileId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"PASTDUE-PREFILTER-{profileId:N}",
            CustomerName = customerName,
            ItemName = "복합기",
            BillingDay = 25,
            BillingCycleMonths = 1,
            BillingAnchorMonth = 5,
            BillingRunsJson = JsonSerializer.Serialize(new List<RentalBillingRunModel> { run }),
            IsActive = true,
            IsDeleted = false
        };

    private static RentalBillingRunModel BuildPastDuePrefilterRun(
        Guid runId,
        DateOnly scheduledDate,
        decimal billedAmount,
        decimal settledAmount)
    {
        var monthStart = new DateOnly(scheduledDate.Year, scheduledDate.Month, 1);
        var monthEnd = new DateOnly(scheduledDate.Year, scheduledDate.Month, DateTime.DaysInMonth(scheduledDate.Year, scheduledDate.Month));
        return new RentalBillingRunModel
        {
            RunId = runId,
            RunKey = $"{monthStart:yyyyMMdd}-{monthEnd:yyyyMMdd}",
            ScheduledDate = scheduledDate,
            PeriodStartDate = monthStart,
            PeriodEndDate = monthEnd,
            CycleMonths = 1,
            PeriodLabel = $"{scheduledDate:yyyy-MM}",
            BilledAmount = billedAmount,
            SettledAmount = settledAmount,
            SettledDate = settledAmount > 0m ? scheduledDate : null
        };
    }

    [Fact]
    public async Task RentalBillingProfiles_DefaultAllCapsProfileRows()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-billing-profile-cap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var totalProfiles = RentalStateService.BillingProfileListResultLimit + 2;
            for (var index = 0; index < totalProfiles; index++)
            {
                db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = $"PROFILE-CAP-{index:D4}",
                    CustomerName = $"청구 거래처 {index:D4}",
                    ItemName = $"렌탈 품목 {index:D4}",
                    BillingType = "묶음",
                    BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
                    BillingDay = 25,
                    BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
                    BillingCycleMonths = 1,
                    BillingAnchorMonth = 1,
                    MonthlyAmount = 100_000m + index,
                    IsActive = true,
                    IsDirty = false
                });
            }

            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ExpandCustomerSummaryRows = true,
                    ReferenceDate = new DateOnly(2026, 5, 12)
                },
                CreateAdminSession());

            Assert.Equal(RentalStateService.BillingProfileListResultLimit, rows.Count);
            Assert.Equal(RentalStateService.BillingProfileListResultLimit, rows.Sum(row => row.GroupedPersistedProfileCount));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbContext_CreatesRentalBillingUnlinkedSortIndexes()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-unlinked-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'RentalAssets';";

            var indexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                indexNames.Add(reader.GetString(0));

            Assert.Contains("IX_RentalAssets_TenantOfficeBillingProfileSort", indexNames);
            Assert.Contains("IX_RentalAssets_TenantManagementBillingProfileSort", indexNames);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbContext_CreatesRentalBillingProfileListSortIndexes()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-billing-profile-list-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'RentalBillingProfiles';";

            var indexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                indexNames.Add(reader.GetString(0));

            Assert.Contains("IX_RentalBillingProfiles_TenantOfficeListSort", indexNames);
            Assert.Contains("IX_RentalBillingProfiles_TenantManagementListSort", indexNames);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MainViewModel_InvoiceOfficeFilter_DefaultsToLoginOffice()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-main-office-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                TenantScopeCatalog.ScopeTenantAll);
            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);
            var viewModel = new MainViewModel(localState, sync, new BackupService(), rental, diagnostics, api, session);

            InvokePrivateInstance(viewModel, "InitializeInvoiceOfficeFilterOptions");

            Assert.Equal(OfficeCodeCatalog.Yeonsu, viewModel.SelectedInvoiceOfficeFilterCode);
            Assert.Contains(viewModel.InvoiceOfficeFilterOptions, option => string.Equals(option.Code, OfficeCodeCatalog.Shared, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(viewModel.InvoiceOfficeFilterOptions, option => string.Equals(option.Code, OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CustomerManagementViewModel_Initialize_DefaultsOfficeFilterToLoginOffice()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-customer-office-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.Customers.AddRange(
                new LocalCustomer
                {
                    Id = Guid.Parse("9bbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "USENET 거래처",
                    NameMatchKey = "USENETCUSTOMER",
                    IsDirty = false
                },
                new LocalCustomer
                {
                    Id = Guid.Parse("9ccccccc-cccc-cccc-cccc-cccccccccccc"),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    NameOriginal = "YEONSU 거래처",
                    NameMatchKey = "YEONSUCUSTOMER",
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var session = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                TenantScopeCatalog.ScopeTenantAll);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var viewModel = new CustomerManagementViewModel(local, session);

            await viewModel.InitializeAsync();

            Assert.Equal(OfficeCodeCatalog.Yeonsu, viewModel.SelectedOfficeFilter);
            var row = Assert.Single(viewModel.Customers);
            Assert.Equal("YEONSU 거래처", row.NameOriginal);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, row.ResponsibleOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CustomerManagementViewModel_ReloadAsync_ReplacesSelectedCustomerWithFreshRow()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-customer-selection-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("9cdddddd-dddd-dddd-dddd-dddddddddddd");
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "재조회 전 거래처",
                NameMatchKey = "CUSTOMERSELECTIONBEFORERELOAD",
                Revision = 1,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var session = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                TenantScopeCatalog.ScopeTenantAll);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var viewModel = new CustomerManagementViewModel(local, session);

            await viewModel.InitializeAsync();
            var selectedBeforeReload = Assert.Single(viewModel.Customers);
            viewModel.SelectedCustomer = selectedBeforeReload;

            await db.Customers
                .IgnoreQueryFilters()
                .Where(customer => customer.Id == customerId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(customer => customer.NameOriginal, "재조회 후 거래처")
                    .SetProperty(customer => customer.NameMatchKey, "CUSTOMERSELECTIONAFTERRELOAD")
                    .SetProperty(customer => customer.Revision, 2));

            await viewModel.ReloadCommand.ExecuteAsync(null);

            Assert.NotNull(viewModel.SelectedCustomer);
            var selectedAfterReload = viewModel.SelectedCustomer!;
            var displayedRow = Assert.Single(viewModel.Customers);
            Assert.Equal(customerId, selectedAfterReload.Id);
            Assert.Same(displayedRow, selectedAfterReload);
            Assert.NotSame(selectedBeforeReload, selectedAfterReload);
            Assert.Equal("재조회 후 거래처", selectedAfterReload.NameOriginal);
            Assert.Equal(2, selectedAfterReload.Source.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalAssetViewModel_LoadAsync_DefaultsOfficeFilterToLoginOffice()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-asset-office-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                TenantScopeCatalog.ScopeTenantAll);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var viewModel = new RentalAssetViewModel(rental, local, new RentalDocumentService(), null!, session);

            await viewModel.LoadAsync();

            var selectedOffice = Assert.Single(viewModel.OfficeFilterOptions, option => option.IsSelected);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, selectedOffice.Value);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, viewModel.EditOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalBillingViewModel_LoadAsync_DefaultsOfficeFilterToLoginOfficeAndScopesCustomerLookup()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-billing-office-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.Customers.AddRange(
                new LocalCustomer
                {
                    Id = Guid.Parse("9ddddddd-dddd-dddd-dddd-dddddddddddd"),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "USENET 렌탈 거래처",
                    NameMatchKey = "USENETRENTALCUSTOMER",
                    IsDirty = false
                },
                new LocalCustomer
                {
                    Id = Guid.Parse("9eeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = "ITWORLD 렌탈 거래처",
                    NameMatchKey = "ITWORLDRENTALCUSTOMER",
                    IsDirty = false
                },
                new LocalCustomer
                {
                    Id = Guid.Parse("9fffffff-ffff-ffff-ffff-ffffffffffff"),
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "다른 담당지점 렌탈 거래처",
                    NameMatchKey = "OTHERRESPONSIBLERENTALCUSTOMER",
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var session = CreateUserSession(
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                TenantScopeCatalog.ScopeOfficeOnly);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var viewModel = new RentalBillingViewModel(rental, local, session);

            await viewModel.LoadAsync();

            Assert.Equal(OfficeCodeCatalog.Itworld, viewModel.SelectedOfficeFilter?.Value);
            Assert.Equal(OfficeCodeCatalog.Itworld, viewModel.EditOfficeCode);

            var lookupRows = await viewModel.BuildCustomerLookupRowsAsync();
            var row = Assert.Single(lookupRows);
            var customer = Assert.IsType<LocalCustomer>(row.Tag);
            Assert.Equal("ITWORLD 렌탈 거래처", customer.NameOriginal);
            Assert.Equal(OfficeCodeCatalog.Itworld, customer.ResponsibleOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalBillingViewModel_LoadAsync_FiltersRowsByResponsibleOfficeNotManagementCompany()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-billing-responsible-office-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var ownCustomerId = Guid.Parse("a1000000-0000-0000-0000-000000000001");
            var otherCustomerId = Guid.Parse("a1000000-0000-0000-0000-000000000002");
            db.Customers.AddRange(
                new LocalCustomer
                {
                    Id = ownCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "USENET 렌탈 청구 거래처",
                    NameMatchKey = "USENETBILLINGCUSTOMER",
                    IsDirty = false
                },
                new LocalCustomer
                {
                    Id = otherCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    NameOriginal = "YEONSU 렌탈 청구 거래처",
                    NameMatchKey = "YEONSUBILLINGCUSTOMER",
                    IsDirty = false
                });
            db.RentalBillingProfiles.AddRange(
                new LocalRentalBillingProfile
                {
                    Id = Guid.Parse("a2000000-0000-0000-0000-000000000001"),
                    CustomerId = ownCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "USENET-BILLING-PROFILE",
                    CustomerName = "USENET 렌탈 청구 거래처",
                    ItemName = "USENET 복합기",
                    MonthlyAmount = 100_000m,
                    IsActive = true,
                    IsDirty = false
                },
                new LocalRentalBillingProfile
                {
                    Id = Guid.Parse("a2000000-0000-0000-0000-000000000002"),
                    CustomerId = otherCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "YEONSU-BILLING-PROFILE",
                    CustomerName = "YEONSU 렌탈 청구 거래처",
                    ItemName = "YEONSU 복합기",
                    MonthlyAmount = 120_000m,
                    IsActive = true,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var session = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeTenantAll);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var viewModel = new RentalBillingViewModel(rental, local, session);

            await viewModel.LoadAsync();

            Assert.Equal(OfficeCodeCatalog.Usenet, viewModel.SelectedOfficeFilter?.Value);
            Assert.DoesNotContain(viewModel.OfficeOptions, option => string.Equals(option.Value, "전체", StringComparison.OrdinalIgnoreCase));

            var row = Assert.Single(viewModel.Rows);
            Assert.Equal("USENET 렌탈 청구 거래처", row.CustomerDisplayName);
            Assert.Equal(OfficeCodeCatalog.Usenet, row.Source.ResponsibleOfficeCode);

            var lookupRows = await viewModel.BuildCustomerLookupRowsAsync();
            var lookupRow = Assert.Single(lookupRows);
            var customer = Assert.IsType<LocalCustomer>(lookupRow.Tag);
            Assert.Equal("USENET 렌탈 청구 거래처", customer.NameOriginal);
            Assert.Equal(OfficeCodeCatalog.Usenet, customer.ResponsibleOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalBillingViewModel_ReloadPreservesUnsavedEditor_WhenSelectedRowLeavesFilterResult()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-billing-reload-draft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "청구 reload 보존 거래처",
                NameMatchKey = "BILLINGRELOADCUSTOMER",
                IsDirty = false
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "BILLING-RELOAD-DRAFT",
                CustomerName = "청구 reload 보존 거래처",
                ItemName = "청구 reload 복합기",
                MonthlyAmount = 100_000m,
                IsActive = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var session = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeOfficeOnly);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var viewModel = new RentalBillingViewModel(rental, local, session);

            await viewModel.LoadAsync();
            await viewModel.ReloadCommand.ExecuteAsync(null);
            viewModel.SelectedRow = Assert.Single(viewModel.Rows);
            viewModel.EditNotes = "사용자 입력 청구 메모";

            viewModel.SearchText = "검색결과없음";
            await viewModel.ReloadCommand.ExecuteAsync(null);

            Assert.Empty(viewModel.Rows);
            Assert.NotNull(viewModel.SelectedRow);
            Assert.Equal("사용자 입력 청구 메모", viewModel.EditNotes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalAssetViewModel_ReloadPreservesUnsavedEditor_WhenSelectedRowLeavesFilterResult()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-asset-reload-draft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "ASSET-RELOAD-DRAFT",
                CustomerName = "자산 reload 보존 거래처",
                ItemName = "자산 reload 복합기",
                ManagementNumber = "RELOAD-DRAFT-001",
                AssetStatus = "임대진행중",
                BillingEligibilityStatus = "청구대상",
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var session = CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeOfficeOnly);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var viewModel = new RentalAssetViewModel(rental, local, new RentalDocumentService(), null!, session);

            await viewModel.LoadAsync();
            await viewModel.ReloadCommand.ExecuteAsync(null);
            viewModel.SelectedRow = Assert.Single(viewModel.Rows);
            viewModel.EditNotes = "사용자 입력 자산 메모";

            viewModel.SearchText = "검색결과없음";
            await viewModel.ReloadCommand.ExecuteAsync(null);

            Assert.Empty(viewModel.Rows);
            Assert.NotNull(viewModel.SelectedRow);
            Assert.Equal("사용자 입력 자산 메모", viewModel.EditNotes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalAssetViewModel_ReloadPreservesCheckedAssetsAndStableOrder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-asset-selection-stable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetAId = Guid.Parse("b1000000-0000-0000-0000-000000000001");
            var assetBId = Guid.Parse("b1000000-0000-0000-0000-000000000002");
            var assetCId = Guid.Parse("b1000000-0000-0000-0000-000000000003");
            db.RentalAssets.AddRange(
                CreateSelectionStableAsset(assetCId, "선택 안정 거래처", "SEL-003"),
                CreateSelectionStableAsset(assetAId, "선택 안정 거래처", "SEL-001"),
                CreateSelectionStableAsset(assetBId, "선택 안정 거래처", "SEL-002"));
            await db.SaveChangesAsync();

            var session = CreateUserSession(AppPermissionNames.RentalAssetEdit);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var viewModel = new RentalAssetViewModel(rental, local, new RentalDocumentService(), null!, session);

            await viewModel.LoadAsync();
            await viewModel.ReloadCommand.ExecuteAsync(null);

            Assert.Equal(
                new[] { "SEL-001", "SEL-002", "SEL-003" },
                viewModel.Rows.Select(row => row.Source.ManagementNumber).ToArray());
            Assert.False(viewModel.CanDeleteChecked);
            Assert.False(viewModel.DeleteCheckedCommand.CanExecute(null));

            var canExecuteChangedCount = 0;
            viewModel.DeleteCheckedCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;
            viewModel.Rows[0].IsSelected = true;
            viewModel.Rows[2].IsSelected = true;

            Assert.True(canExecuteChangedCount > 0);
            Assert.True(viewModel.CanDeleteChecked);
            Assert.True(viewModel.DeleteCheckedCommand.CanExecute(null));
            var checkedIdsBeforeReload = viewModel.Rows
                .Where(row => row.IsSelected)
                .Select(row => row.Source.Id)
                .OrderBy(id => id)
                .ToArray();

            await db.RentalAssets
                .IgnoreQueryFilters()
                .Where(asset => asset.Id == assetBId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(asset => asset.Revision, 2)
                    .SetProperty(asset => asset.Notes, "재조회 후에도 체크 선택과 정렬 유지"));

            await viewModel.ReloadCommand.ExecuteAsync(null);

            Assert.Equal(
                new[] { "SEL-001", "SEL-002", "SEL-003" },
                viewModel.Rows.Select(row => row.Source.ManagementNumber).ToArray());
            Assert.Equal(
                checkedIdsBeforeReload,
                viewModel.Rows
                    .Where(row => row.IsSelected)
                    .Select(row => row.Source.Id)
                    .OrderBy(id => id)
                    .ToArray());
            Assert.True(viewModel.CanDeleteChecked);
            Assert.True(viewModel.DeleteCheckedCommand.CanExecute(null));

            foreach (var row in viewModel.Rows)
                row.IsSelected = false;

            Assert.False(viewModel.CanDeleteChecked);
            Assert.False(viewModel.DeleteCheckedCommand.CanExecute(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static SessionState CreateUserSession(params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "user",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static LocalRentalAsset CreateSelectionStableAsset(
        Guid id,
        string customerName,
        string managementNumber)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = $"SELECTION-STABLE-{managementNumber}",
            CustomerName = customerName,
            CurrentCustomerName = customerName,
            ItemName = "선택 안정 복합기",
            ManagementNumber = managementNumber,
            AssetStatus = "임대진행중",
            BillingEligibilityStatus = "청구대상",
            IsDirty = false
        };

    private static SessionState CreateUserSession(string tenantCode, string officeCode, string scopeType, params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = scopeType,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static async Task SavePurchaseInvoiceAsync(
        LocalStateService service,
        Guid vendorCustomerId,
        Guid itemId,
        DateOnly invoiceDate,
        decimal unitPrice,
        string invoiceNumber)
    {
        await service.SaveInvoiceAsync(new LocalInvoice
        {
            Id = Guid.NewGuid(),
            CustomerId = vendorCustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceNumber = invoiceNumber,
            VoucherType = VoucherType.Purchase,
            InvoiceDate = invoiceDate,
            Lines =
            {
                new LocalInvoiceLine
                {
                    ItemId = itemId,
                    ItemNameOriginal = "공통 품목",
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = unitPrice,
                    LineAmount = unitPrice
                }
            }
        });
    }

    private static SessionState CreateOnlineAdminSession()
    {
        var session = new SessionState();
        session.SetSession("test-token", new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
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
            Username = "yeonsu",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static SessionState CreateItworldAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "itworld",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "거래플랜.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜.sln을 찾을 수 없습니다.");
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[]? args)
    {
        var method = typeof(LocalStateService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        Assert.NotNull(result);
        return (T)result!;
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[]? args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        Assert.NotNull(result);
        return (T)result!;
    }

    private static void InvokePrivateInstance(object target, string methodName, params object?[]? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        method!.Invoke(target, args);
    }

    private static async Task<T?> InvokePrivateInstanceAsync<T>(object target, string methodName, params object?[]? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method!.Invoke(target, args);
        Assert.NotNull(result);
        return await (Task<T?>)result!;
    }

    private static async Task<object?> InvokePrivateInstanceTaskResultAsync(object target, string methodName, params object?[]? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method!.Invoke(target, args);
        Assert.NotNull(result);
        var task = result as Task;
        Assert.NotNull(task);
        await task!;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }
    private sealed class SyncStatusHandler : HttpMessageHandler
    {
        private readonly long _serverRevision;

        public SyncStatusHandler(long serverRevision)
        {
            _serverRevision = serverRevision;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.Equals("/sync/status", StringComparison.OrdinalIgnoreCase) == true)
            {
                var json = JsonSerializer.Serialize(new SyncStatusDto
                {
                    CurrentServerRevision = _serverRevision,
                    ServerUtc = DateTime.UtcNow
                });

                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class NullJsonSyncResponseHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.StartsWith("/sync/", StringComparison.OrdinalIgnoreCase) == true)
            {
                RequestCount++;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("null", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CapturePushHandler : HttpMessageHandler
    {
        public SyncPushRequest? LastPushRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.Equals("/sync/push", StringComparison.OrdinalIgnoreCase) == true)
            {
                LastPushRequest = await request.Content!.ReadFromJsonAsync<SyncPushRequest>(cancellationToken: cancellationToken);
                var json = JsonSerializer.Serialize(new SyncPushResult
                {
                    AcceptedCount = LastPushRequest?.ItemWarehouseStocks.Count ?? 0,
                    CurrentServerRevision = 1
                });

                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class WarehouseStockPushThenPullHandler : HttpMessageHandler
    {
        private readonly Guid _itemId;
        private readonly decimal _serverQuantity;
        private readonly long _serverRevision;
        private readonly DateTime _serverUpdatedAt;

        public WarehouseStockPushThenPullHandler(Guid itemId, decimal serverQuantity, long serverRevision, DateTime serverUpdatedAt)
        {
            _itemId = itemId;
            _serverQuantity = serverQuantity;
            _serverRevision = serverRevision;
            _serverUpdatedAt = serverUpdatedAt;
        }

        public int PushCount { get; private set; }
        public int PullCount { get; private set; }
        public SyncPushRequest? LastPushRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.Equals("/sync/push", StringComparison.OrdinalIgnoreCase) == true)
            {
                PushCount++;
                LastPushRequest = await request.Content!.ReadFromJsonAsync<SyncPushRequest>(cancellationToken: cancellationToken);
                var json = JsonSerializer.Serialize(new SyncPushResult
                {
                    AcceptedCount = LastPushRequest?.ItemWarehouseStocks.Count ?? 0,
                    CurrentServerRevision = 20
                });

                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri?.AbsolutePath.Equals("/sync/pull", StringComparison.OrdinalIgnoreCase) == true)
            {
                PullCount++;
                var json = JsonSerializer.Serialize(new SyncPullResponse
                {
                    CurrentServerRevision = 20,
                    Items =
                    [
                        new ItemDto
                        {
                            Id = _itemId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            NameOriginal = "Warehouse stock reconcile item",
                            NameMatchKey = "WAREHOUSESTOCKRECONCILEITEM",
                            TrackingType = ItemTrackingTypes.Stock,
                            Unit = "EA",
                            CurrentStock = _serverQuantity,
                            Revision = _serverRevision,
                            UpdatedAtUtc = _serverUpdatedAt
                        }
                    ],
                    ItemWarehouseStocks =
                    [
                        new ItemWarehouseStockDto
                        {
                            ItemId = _itemId,
                            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                            Quantity = _serverQuantity,
                            Revision = _serverRevision,
                            UpdatedAtUtc = _serverUpdatedAt
                        }
                    ]
                });

                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

}
