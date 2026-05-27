using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingDeletionFlowTests
{
    [Fact]
    public async Task ExcludeUnlinkedBillingAsset_HidesFromBillingListButKeepsLinkCandidate()
    {
        PrepareAppRoot("georaeplan-rental-exclude-unlinked");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateRentalAsset(assetId, "A거래처", billingProfileId: null, "미확인"));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var session = CreateAdminSession();

            var beforeRows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { Status = "미연결", ExpandCustomerSummaryRows = true },
                session);
            Assert.Contains(beforeRows, row => row.SelectionId == assetId && !row.HasPersistedProfile);

            var result = await service.ExcludeUnlinkedBillingAssetFromBillingListAsync(assetId, session);
            Assert.True(result.Success, result.Message);

            var afterRows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { Status = "미연결", ExpandCustomerSummaryRows = true },
                session);
            Assert.DoesNotContain(afterRows, row => row.SelectionId == assetId);

            var persistedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            Assert.Equal("청구제외", persistedAsset.BillingEligibilityStatus);
            Assert.Equal("청구관리 목록 정리", persistedAsset.BillingExclusionReason);
            Assert.Null(persistedAsset.BillingProfileId);

            var candidates = await service.GetAssetLinkCandidatesAsync(
                currentBillingProfileId: null,
                customerId: null,
                customerName: "A거래처",
                officeCode: OfficeCodeCatalog.Usenet,
                session,
                includeOtherOfficeAssets: true);
            Assert.Contains(candidates, candidate => candidate.Source.Id == assetId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteBillingProfile_UnlinksIncludedAssetsAndSuppressesFromUnlinkedBillingList()
    {
        PrepareAppRoot("georaeplan-rental-delete-profile");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.RentalBillingProfiles.Add(CreateBillingProfile(profileId, assetId, "A거래처"));
            db.RentalAssets.Add(CreateRentalAsset(assetId, "A거래처", profileId, "청구대상"));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var session = CreateAdminSession();

            var result = await service.DeleteBillingProfileAsync(profileId, session);
            Assert.True(result.Success, result.Message);

            var deletedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == profileId);
            Assert.True(deletedProfile.IsDeleted);

            var unlinkedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            Assert.Null(unlinkedAsset.BillingProfileId);
            Assert.Equal("청구제외", unlinkedAsset.BillingEligibilityStatus);
            Assert.Equal("청구 프로필 삭제로 청구목록 제외", unlinkedAsset.BillingExclusionReason);
            Assert.Equal(profileId, unlinkedAsset.LastBillingProfileId);

            var histories = await db.RentalAssetAssignmentHistories
                .Where(history => history.AssetId == assetId)
                .ToListAsync();
            var endedHistory = Assert.Single(histories);
            Assert.False(endedHistory.IsCurrent);
            Assert.Equal(profileId, endedHistory.BillingProfileId);
            Assert.Equal("청구 프로필 삭제", endedHistory.ChangeReason);

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { Status = "미연결", ExpandCustomerSummaryRows = true },
                session);
            Assert.DoesNotContain(rows, row => row.SelectionId == assetId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task StartBilling_AllowsNextUnbilledCycle_WhenReferenceDateIsOutsideBillingMonth()
    {
        PrepareAppRoot("georaeplan-rental-start-outside-billing-month");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "청구일 외 테스트 거래처";
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = customerName,
                NameMatchKey = customerName,
                TradeType = CustomerTradeTypes.Sales,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            profile.BillingCycleMonths = 3;
            profile.BillingAnchorMonth = 5;
            profile.LastBilledDate = new DateOnly(2026, 5, 25);
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var result = await service.StartBillingAsync(profileId, new DateOnly(2026, 6, 10), session);

            Assert.True(result.Success, result.Message);
            var invoice = await db.Invoices.SingleAsync(current => current.Id == result.RelatedEntityId);
            Assert.Equal(new DateOnly(2026, 8, 25), invoice.InvoiceDate);
            Assert.Equal(profileId, invoice.LinkedRentalBillingProfileId);
            Assert.NotNull(invoice.LinkedRentalBillingRunId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task StartBilling_RebuildsUnpaidExistingInvoiceWhenTemplateChanges()
    {
        PrepareAppRoot("georaeplan-rental-start-idempotent");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "중복 청구 방지 거래처";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var first = await service.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(first.Success, first.Message);
            var firstInvoice = await db.Invoices.SingleAsync(current => current.Id == first.RelatedEntityId);
            Assert.Equal(100_000m, firstInvoice.TotalAmount);

            var persistedProfile = await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId);
            persistedProfile.BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "복합기 렌탈료",
                    BillingLineMode = "묶음",
                    RepresentativeAssetId = assetId,
                    Quantity = 1m,
                    UnitPrice = 200_000m,
                    Amount = 200_000m,
                    IncludedAssetIds = [assetId]
                }
            });
            persistedProfile.MonthlyAmount = 200_000m;
            await db.SaveChangesAsync();

            var second = await service.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(second.Success, second.Message);
            Assert.NotEqual(first.RelatedEntityId, second.RelatedEntityId);

            var invoices = await db.Invoices
                .Where(current => current.LinkedRentalBillingProfileId == profileId)
                .ToListAsync();
            Assert.Equal(2, invoices.Count);
            Assert.Contains(invoices, current => current.Id == first.RelatedEntityId && !current.IsLatestVersion);
            var secondInvoice = Assert.Single(invoices, current => current.Id == second.RelatedEntityId && current.IsLatestVersion);
            Assert.Equal(200_000m, secondInvoice.TotalAmount);

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { ReferenceDate = new DateOnly(2026, 5, 25), ExpandCustomerSummaryRows = true },
                session);
            var row = Assert.Single(rows, current => current.Source.Id == profileId);
            Assert.Equal(200_000m, row.CurrentBilledAmount);
            Assert.Equal(200_000m, row.OutstandingAmount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MarkCompleted_AllowsSelectedRunOutsideBillingMonthAndMovesNextRunWithoutSettledCarryover()
    {
        PrepareAppRoot("georaeplan-rental-complete-outside-billing-month");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "청구 완료 회차 이동 거래처";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            profile.BillingCycleMonths = 3;
            profile.BillingAnchorMonth = 5;
            profile.LastBilledDate = new DateOnly(2026, 5, 25);
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var start = await service.StartBillingAsync(profileId, new DateOnly(2026, 6, 10), session);
            Assert.True(start.Success, start.Message);
            var invoice = await db.Invoices.SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            db.Transactions.Add(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 10),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                ReceiptTotal = invoice.TotalAmount,
                BankReceipt = invoice.TotalAmount,
                SettlementAmount = invoice.TotalAmount,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var completed = await service.MarkBillingCompletedAsync(
                profileId,
                new DateOnly(2026, 6, 10),
                "완료",
                string.Empty,
                session,
                billingRunId: runId);

            Assert.True(completed.Success, completed.Message);
            var completedProfile = await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId);
            Assert.Equal(new DateOnly(2026, 8, 25), completedProfile.LastBilledDate);

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { ReferenceDate = new DateOnly(2026, 6, 10), ExpandCustomerSummaryRows = true },
                session);
            var row = Assert.Single(rows, current => current.Source.Id == profileId);
            Assert.Equal(new DateOnly(2026, 11, 25), row.NextBillingDate);
            Assert.Equal(0m, row.SettledAmount);
            Assert.Equal(row.CurrentBilledAmount, row.OutstandingAmount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingRows_ExposesPastUnresolvedHistoryAndFilter()
    {
        PrepareAppRoot("georaeplan-rental-past-unresolved-history");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "과거 미처리 거래처";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var start = await service.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);
            var invoice = await db.Invoices.SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);

            db.Transactions.Add(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 5, 30),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                ReceiptTotal = 40_000m,
                BankReceipt = 40_000m,
                SettlementAmount = 40_000m,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { ReferenceDate = new DateOnly(2026, 6, 25), ExpandCustomerSummaryRows = true },
                session);
            var row = Assert.Single(rows, current => current.Source.Id == profileId);

            Assert.True(row.HasPastUnresolved);
            Assert.Equal(1, row.PastUnresolvedCount);
            Assert.Equal(60_000m, row.PastUnresolvedAmount);
            var pastHistory = Assert.Single(row.BillingHistoryRows, history => history.BillingRunId == runId);
            Assert.True(pastHistory.IsPastUnresolved);
            Assert.Equal(100_000m, pastHistory.BilledAmount);
            Assert.Equal(40_000m, pastHistory.SettledAmount);
            Assert.Equal(60_000m, pastHistory.OutstandingAmount);

            var filteredRows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { ReferenceDate = new DateOnly(2026, 6, 25), ExpandCustomerSummaryRows = true, PastDueOnly = true },
                session);
            Assert.Contains(filteredRows, current => current.Source.Id == profileId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveTransaction_RentalReceipt_AlsoUpdatesLinkedSalesInvoicePayment()
    {
        PrepareAppRoot("georaeplan-rental-receipt-links-invoice");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "렌탈 입금 전표 연동 거래처";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);

            var invoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);

            var save = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TransactionDate = new DateOnly(2026, 5, 26),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                BankReceipt = invoice.TotalAmount,
                ReceiptTotal = invoice.TotalAmount,
                SettlementAmount = invoice.TotalAmount
            }, session);

            Assert.True(save.Success, save.Message);
            var transaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == save.EntityId);
            Assert.Equal(invoice.Id, transaction.LinkedInvoiceId);
            Assert.Equal(profileId, transaction.LinkedRentalBillingProfileId);
            Assert.Equal(runId, transaction.LinkedRentalBillingRunId);

            var payment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transaction.Id);
            Assert.Equal(invoice.Id, payment.InvoiceId);
            Assert.Equal(invoice.TotalAmount, payment.Amount);

            var updatedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, updatedProfile.SettledAmount);
            Assert.Equal(0m, updatedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, updatedProfile.CompletionStatus);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveTransaction_RentalSalesInvoiceReceipt_AlsoUpdatesRentalSettlement()
    {
        PrepareAppRoot("georaeplan-rental-invoice-receipt-links-billing");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "전표 수금 렌탈 연동 거래처";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);

            var invoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);

            var save = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TransactionDate = new DateOnly(2026, 5, 26),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoice.Id,
                BankReceipt = invoice.TotalAmount,
                ReceiptTotal = invoice.TotalAmount,
                SettlementAmount = invoice.TotalAmount
            }, session);

            Assert.True(save.Success, save.Message);
            var transaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == save.EntityId);
            Assert.Equal(PaymentFlowConstants.TransactionKindRentalReceipt, transaction.TransactionKind);
            Assert.Equal(invoice.Id, transaction.LinkedInvoiceId);
            Assert.Equal(profileId, transaction.LinkedRentalBillingProfileId);
            Assert.Equal(runId, transaction.LinkedRentalBillingRunId);

            var payment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transaction.Id);
            Assert.Equal(invoice.Id, payment.InvoiceId);
            Assert.Equal(invoice.TotalAmount, payment.Amount);

            var updatedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, updatedProfile.SettledAmount);
            Assert.Equal(0m, updatedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, updatedProfile.CompletionStatus);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static LocalCustomer CreateCustomer(Guid customerId, string customerName)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = customerName,
            NameMatchKey = customerName,
            TradeType = CustomerTradeTypes.Sales,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAsset CreateRentalAsset(
        Guid assetId,
        string customerName,
        Guid? billingProfileId,
        string billingEligibilityStatus)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"M-{assetId:N}",
            ManagementNumber = ShortCode("MN", assetId),
            AssetKey = $"AK-{assetId:N}",
            CustomerName = customerName,
            CurrentCustomerName = customerName,
            InstallSiteName = customerName,
            InstallLocation = "사무실",
            ItemName = "복합기",
            MachineNumber = ShortCode("SN", assetId),
            AssetStatus = "임대진행중",
            BillingProfileId = billingProfileId,
            BillingEligibilityStatus = billingEligibilityStatus,
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalBillingProfile CreateBillingProfile(Guid profileId, Guid assetId, string customerName)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerName = customerName,
            InstallSiteName = "사무실",
            ItemName = "복합기 렌탈료",
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            BillingDay = 25,
            BillingCycleMonths = 1,
            MonthlyAmount = 100_000m,
            BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "복합기 렌탈료",
                    BillingLineMode = "묶음",
                    RepresentativeAssetId = assetId,
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m,
                    IncludedAssetIds = [assetId]
                }
            }),
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static string ShortCode(string prefix, Guid id)
        => $"{prefix}-{id:N}".Substring(0, 12);

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
}
