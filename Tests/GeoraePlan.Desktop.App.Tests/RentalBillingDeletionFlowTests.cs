using System.Reflection;
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
    public async Task DeleteBillingHistory_ReloadsTrackedProfileBeforeRevisionCheck()
    {
        PrepareAppRoot("georaeplan-rental-delete-history-reloads-tracked-profile");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var customerName = "Tracked revision customer";
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.Revision = 100;
            profile.BillingRunsJson = JsonSerializer.Serialize(new List<RentalBillingRunModel>
            {
                new()
                {
                    RunId = runId,
                    RunKey = "2026-05",
                    ScheduledDate = new DateOnly(2026, 5, 25),
                    PeriodStartDate = new DateOnly(2026, 5, 1),
                    PeriodEndDate = new DateOnly(2026, 5, 31),
                    PeriodLabel = "2026-05",
                    Status = PaymentFlowConstants.BillingStatusInProgress,
                    BilledAmount = 100_000m,
                    SettledAmount = 0m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPending
                }
            });
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var trackedProfile = await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId);
            Assert.Equal(100, trackedProfile.Revision);

            await using (var updateDb = new LocalDbContext())
            {
                var storedProfile = await updateDb.RentalBillingProfiles.SingleAsync(current => current.Id == profileId);
                storedProfile.Revision = 200;
                storedProfile.UpdatedAtUtc = DateTime.UtcNow;
                await updateDb.SaveChangesAsync();
            }

            var service = new RentalStateService(db);
            var result = await service.DeleteBillingHistoryAsync(
                profileId,
                runId,
                CreateAdminSession(),
                expectedRevision: 200);

            Assert.True(result.Success, result.Message);
            var refreshed = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var remainingRuns = JsonSerializer.Deserialize<List<RentalBillingRunModel>>(refreshed.BillingRunsJson)
                                ?? new List<RentalBillingRunModel>();
            Assert.DoesNotContain(remainingRuns, run => run.RunId == runId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteBillingHistory_RequiresInvoiceEditPermissionForLinkedSalesInvoice()
    {
        PrepareAppRoot("georaeplan-rental-delete-history-requires-invoice-edit");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Rental delete permission customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var invoiceEditorSession = CreateUserSession(
                AppPermissionNames.RentalProfileEdit,
                AppPermissionNames.InvoiceEdit);
            var invoiceEditorLocal = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), invoiceEditorSession);
            var invoiceEditorRental = new RentalStateService(db, invoiceEditorLocal);
            var started = await invoiceEditorRental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), invoiceEditorSession);
            Assert.True(started.Success, started.Message);

            var invoice = await db.Invoices
                .AsNoTracking()
                .SingleAsync(current => current.Id == started.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);

            var rentalOnlySession = CreateUserSession(AppPermissionNames.RentalProfileEdit);
            var rentalOnlyLocal = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), rentalOnlySession);
            var rentalOnlyService = new RentalStateService(db, rentalOnlyLocal);

            var denied = await rentalOnlyService.DeleteBillingHistoryAsync(profileId, runId, rentalOnlySession);

            Assert.False(denied.Success);
            Assert.Contains("전표", denied.Message);
            var persistedInvoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == invoice.Id);
            Assert.False(persistedInvoice.IsDeleted);
            var persistedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Contains(DeserializeRuns(persistedProfile), current => current.RunId == runId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteBillingHistory_RequiresPaymentEditPermissionForDirectInvoicePayment()
    {
        PrepareAppRoot("georaeplan-rental-delete-history-requires-payment-edit-direct-payment");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Rental direct payment delete permission customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "\uCCAD\uAD6C\uB300\uC0C1"));
            await db.SaveChangesAsync();

            var adminSession = CreateAdminSession();
            var adminLocal = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), adminSession);
            var adminRental = new RentalStateService(db, adminLocal);
            var started = await adminRental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), adminSession);
            Assert.True(started.Success, started.Message);

            var invoice = await db.Invoices
                .AsNoTracking()
                .SingleAsync(current => current.Id == started.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            var paymentId = Guid.NewGuid();

            var savePayment = await adminLocal.SavePaymentAsync(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 5, 27),
                Amount = invoice.TotalAmount,
                Note = "direct rental payment permission guard"
            }, adminSession);
            Assert.True(savePayment.Success, savePayment.Message);
            Assert.False(await db.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == paymentId));

            var invoiceOnlySession = CreateUserSession(
                AppPermissionNames.RentalProfileEdit,
                AppPermissionNames.InvoiceEdit);
            var invoiceOnlyLocal = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), invoiceOnlySession);
            var invoiceOnlyRental = new RentalStateService(db, invoiceOnlyLocal);

            var denied = await invoiceOnlyRental.DeleteBillingHistoryAsync(profileId, runId, invoiceOnlySession);

            Assert.False(denied.Success);
            Assert.Contains("수금", denied.Message);
            var persistedPayment = await db.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == paymentId);
            Assert.False(persistedPayment.IsDeleted);
            var persistedInvoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == invoice.Id);
            Assert.False(persistedInvoice.IsDeleted);
            var persistedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Contains(DeserializeRuns(persistedProfile), current => current.RunId == runId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task BillingHistoryRows_IncludeFinancialRunMissingFromProfileJson()
    {
        PrepareAppRoot("georaeplan-rental-history-financial-run-missing-json");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var customerName = "Financial run missing json customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            profile.BillingCycleMonths = 12;
            profile.MonthlyAmount = 330_000m;
            profile.BillingRunsJson = "[]";
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            db.Transactions.Add(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 1, 8),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                ReceiptTotal = 3_960_000m,
                BankReceipt = 3_960_000m,
                SettlementAmount = 3_960_000m,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var histories = await service.GetBillingHistoryRowsAsync(
                new[] { profileId },
                session,
                new DateOnly(2026, 6, 17));

            var history = Assert.Single(histories, current => current.BillingRunId == runId);
            Assert.Equal(3_960_000m, history.SettledAmount);
            Assert.Equal(3_960_000m, history.BilledAmount);
            Assert.Equal(0m, history.OutstandingAmount);
            Assert.True(history.CanDelete);

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ReferenceDate = new DateOnly(2026, 6, 17),
                    ExpandCustomerSummaryRows = true
                },
                session);
            var row = Assert.Single(rows, current => current.Source.Id == profileId);
            Assert.Contains(row.BillingHistoryRows, current => current.BillingRunId == runId);

            var delete = await service.DeleteBillingHistoryAsync(profileId, runId, session);
            Assert.True(delete.Success, delete.Message);
            var deletedTransaction = await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.LinkedRentalBillingRunId == runId);
            Assert.True(deletedTransaction.IsDeleted);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task BillingHistoryRows_IgnoresZeroSettlementTransactionMissingFromProfileJson()
    {
        PrepareAppRoot("georaeplan-rental-history-zero-settlement-orphan-run");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var customerName = "Zero settlement orphan rental customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            profile.BillingCycleMonths = 12;
            profile.MonthlyAmount = 330_000m;
            profile.BillingRunsJson = "[]";
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            db.Transactions.Add(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 1, 8),
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                ReceiptTotal = 0m,
                BankReceipt = 0m,
                SettlementAmount = 0m,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var histories = await service.GetBillingHistoryRowsAsync(
                new[] { profileId },
                session,
                new DateOnly(2026, 6, 17));

            Assert.DoesNotContain(histories, current => current.BillingRunId == runId);

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ReferenceDate = new DateOnly(2026, 6, 17),
                    ExpandCustomerSummaryRows = true
                },
                session);
            var row = Assert.Single(rows, current => current.Source.Id == profileId);
            Assert.DoesNotContain(row.BillingHistoryRows, current => current.BillingRunId == runId);
            Assert.False(row.HasPastUnresolved);
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
            Assert.Equal(300_000m, row.CurrentBilledAmount);
            Assert.Equal(0m, row.OutstandingAmount);
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
            db.Transactions.Add(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 2),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                ReceiptTotal = 15_000m,
                BankReceipt = 15_000m,
                SettlementAmount = 15_000m,
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
            Assert.Equal(45_000m, row.PastUnresolvedAmount);
            var pastHistory = Assert.Single(row.BillingHistoryRows, history => history.BillingRunId == runId);
            Assert.True(pastHistory.IsPastUnresolved);
            Assert.Equal(100_000m, pastHistory.BilledAmount);
            Assert.Equal(55_000m, pastHistory.SettledAmount);
            Assert.Equal(45_000m, pastHistory.OutstandingAmount);
            Assert.Equal(new DateOnly(2026, 6, 2), pastHistory.SettledDate);

            var summaryOnlyRows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { ReferenceDate = new DateOnly(2026, 6, 25), ExpandCustomerSummaryRows = true, IncludeHistoryRows = false },
                session);
            var summaryOnlyRow = Assert.Single(summaryOnlyRows, current => current.Source.Id == profileId);
            Assert.True(summaryOnlyRow.HasPastUnresolved);
            Assert.Equal(1, summaryOnlyRow.PastUnresolvedCount);
            Assert.Equal(45_000m, summaryOnlyRow.PastUnresolvedAmount);
            Assert.Empty(summaryOnlyRow.BillingHistoryRows);

            var selectedHistories = await service.GetBillingHistoryRowsAsync(
                new[] { profileId },
                session,
                new DateOnly(2026, 6, 25));
            var selectedHistory = Assert.Single(selectedHistories, history => history.BillingRunId == runId);
            Assert.True(selectedHistory.IsPastUnresolved);
            Assert.Equal(100_000m, selectedHistory.BilledAmount);
            Assert.Equal(55_000m, selectedHistory.SettledAmount);
            Assert.Equal(45_000m, selectedHistory.OutstandingAmount);
            Assert.Equal(new DateOnly(2026, 6, 2), selectedHistory.SettledDate);

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

    [Fact]
    public async Task SavePayment_DirectRentalBillingInvoicePayment_UpdatesRentalSettlement()
    {
        PrepareAppRoot("georaeplan-rental-direct-payment-updates-settlement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Direct rental invoice payment customer";
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
            var paymentId = Guid.NewGuid();

            var savePayment = await local.SavePaymentAsync(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 5, 27),
                Amount = invoice.TotalAmount,
                Note = "직접 전표 수금"
            }, session);

            Assert.True(savePayment.Success, savePayment.Message);
            Assert.False(await db.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == paymentId));
            var savedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == paymentId);
            Assert.False(savedPayment.IsDeleted);
            Assert.Equal(invoice.TotalAmount, savedPayment.Amount);

            var updatedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, updatedProfile.SettledAmount);
            Assert.Equal(0m, updatedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, updatedProfile.CompletionStatus);
            var updatedRun = DeserializeRuns(updatedProfile).Single(current => current.RunId == runId);
            Assert.Equal(invoice.TotalAmount, updatedRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, updatedRun.Status);
            Assert.Equal(PaymentFlowConstants.SettlementStatusConfirmed, updatedRun.SettlementStatus);
            Assert.Equal(new DateOnly(2026, 5, 27), updatedRun.SettledDate);

            var complete = await rental.MarkBillingCompletedAsync(
                profileId,
                new DateOnly(2026, 5, 27),
                PaymentFlowConstants.BillingStatusCompleted,
                "Direct payment completion",
                session,
                billingRunId: runId);
            Assert.True(complete.Success, complete.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SavePayment_RelinkBetweenRentalBillingInvoices_RecalculatesPreviousAndTargetSettlement()
    {
        PrepareAppRoot("georaeplan-rental-payment-relink-recalculates-both-settlements");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var firstProfileId = Guid.NewGuid();
            var firstAssetId = Guid.NewGuid();
            var firstCustomerId = Guid.NewGuid();
            var firstCustomerName = "Payment relink source rental customer";
            db.Customers.Add(CreateCustomer(firstCustomerId, firstCustomerName));
            var firstProfile = CreateBillingProfile(firstProfileId, firstAssetId, firstCustomerName);
            firstProfile.CustomerId = firstCustomerId;
            db.RentalBillingProfiles.Add(firstProfile);
            db.RentalAssets.Add(CreateRentalAsset(firstAssetId, firstCustomerName, firstProfileId, "Billing standby"));

            var secondProfileId = Guid.NewGuid();
            var secondAssetId = Guid.NewGuid();
            var secondCustomerId = Guid.NewGuid();
            var secondCustomerName = "Payment relink target rental customer";
            db.Customers.Add(CreateCustomer(secondCustomerId, secondCustomerName));
            var secondProfile = CreateBillingProfile(secondProfileId, secondAssetId, secondCustomerName);
            secondProfile.CustomerId = secondCustomerId;
            db.RentalBillingProfiles.Add(secondProfile);
            db.RentalAssets.Add(CreateRentalAsset(secondAssetId, secondCustomerName, secondProfileId, "Billing standby"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);

            var firstStart = await rental.StartBillingAsync(firstProfileId, new DateOnly(2026, 5, 25), session);
            Assert.True(firstStart.Success, firstStart.Message);
            var secondStart = await rental.StartBillingAsync(secondProfileId, new DateOnly(2026, 5, 25), session);
            Assert.True(secondStart.Success, secondStart.Message);

            var firstInvoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == firstStart.RelatedEntityId);
            var secondInvoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == secondStart.RelatedEntityId);
            var firstRunId = Assert.IsType<Guid>(firstInvoice.LinkedRentalBillingRunId);
            var secondRunId = Assert.IsType<Guid>(secondInvoice.LinkedRentalBillingRunId);
            var paymentId = Guid.NewGuid();

            var saveSourcePayment = await local.SavePaymentAsync(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = firstInvoice.Id,
                PaymentDate = new DateOnly(2026, 5, 27),
                Amount = firstInvoice.TotalAmount,
                Note = "Initial source rental payment"
            }, session);
            Assert.True(saveSourcePayment.Success, saveSourcePayment.Message);

            var sourcePaidProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == firstProfileId);
            Assert.Equal(firstInvoice.TotalAmount, sourcePaidProfile.SettledAmount);
            Assert.Equal(0m, sourcePaidProfile.OutstandingAmount);

            var relinkPayment = await local.SavePaymentAsync(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = secondInvoice.Id,
                PaymentDate = new DateOnly(2026, 5, 28),
                Amount = secondInvoice.TotalAmount,
                Note = "Relinked target rental payment"
            }, session);
            Assert.True(relinkPayment.Success, relinkPayment.Message);

            var savedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == paymentId);
            Assert.Equal(secondInvoice.Id, savedPayment.InvoiceId);
            Assert.Equal(secondInvoice.TotalAmount, savedPayment.Amount);

            var revertedSourceProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == firstProfileId);
            Assert.Equal(0m, revertedSourceProfile.SettledAmount);
            Assert.Equal(firstInvoice.TotalAmount, revertedSourceProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedSourceProfile.CompletionStatus);
            var revertedSourceRun = DeserializeRuns(revertedSourceProfile).Single(current => current.RunId == firstRunId);
            Assert.Equal(0m, revertedSourceRun.SettledAmount);
            Assert.NotEqual(PaymentFlowConstants.BillingStatusCompleted, revertedSourceRun.Status);
            Assert.NotEqual(PaymentFlowConstants.SettlementStatusConfirmed, revertedSourceRun.SettlementStatus);
            Assert.Null(revertedSourceRun.SettledDate);

            var completedTargetProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == secondProfileId);
            Assert.Equal(secondInvoice.TotalAmount, completedTargetProfile.SettledAmount);
            Assert.Equal(0m, completedTargetProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, completedTargetProfile.CompletionStatus);
            var completedTargetRun = DeserializeRuns(completedTargetProfile).Single(current => current.RunId == secondRunId);
            Assert.Equal(secondInvoice.TotalAmount, completedTargetRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, completedTargetRun.Status);
            Assert.Equal(PaymentFlowConstants.SettlementStatusConfirmed, completedTargetRun.SettlementStatus);
            Assert.Equal(new DateOnly(2026, 5, 28), completedTargetRun.SettledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RegisterBillingSettlement_CreatesLinkedTransactionAndPaymentEvidence()
    {
        PrepareAppRoot("georaeplan-rental-register-settlement-evidence");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Register settlement evidence customer";
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

            var register = await rental.RegisterBillingSettlementAsync(
                profileId,
                new DateOnly(2026, 5, 27),
                invoice.TotalAmount,
                "Register settlement evidence",
                session,
                billingRunId: runId);

            Assert.True(register.Success, register.Message);

            var repeat = await rental.RegisterBillingSettlementAsync(
                profileId,
                new DateOnly(2026, 5, 27),
                invoice.TotalAmount,
                "Register settlement evidence repeat",
                session,
                billingRunId: runId);
            Assert.True(repeat.Success, repeat.Message);

            var transaction = Assert.Single(await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(current =>
                    !current.IsDeleted &&
                    current.LinkedRentalBillingProfileId == profileId &&
                    current.LinkedRentalBillingRunId == runId)
                .ToListAsync());
            Assert.Equal(PaymentFlowConstants.TransactionKindRentalReceipt, transaction.TransactionKind);
            Assert.Equal(invoice.Id, transaction.LinkedInvoiceId);
            Assert.Equal(invoice.TotalAmount, transaction.SettlementAmount);
            Assert.Equal(invoice.TotalAmount, transaction.ReceiptTotal);
            Assert.Equal(invoice.TotalAmount, transaction.BankReceipt);

            var payment = await db.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == transaction.Id);
            Assert.Equal(invoice.Id, payment.InvoiceId);
            Assert.Equal(invoice.TotalAmount, payment.Amount);
            Assert.Equal(new DateOnly(2026, 5, 27), payment.PaymentDate);

            var updatedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, updatedProfile.SettledAmount);
            Assert.Equal(0m, updatedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, updatedProfile.CompletionStatus);
            var updatedRun = DeserializeRuns(updatedProfile).Single(current => current.RunId == runId);
            Assert.Equal(invoice.TotalAmount, updatedRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, updatedRun.Status);
            Assert.Equal(PaymentFlowConstants.SettlementStatusConfirmed, updatedRun.SettlementStatus);
            Assert.Equal(new DateOnly(2026, 5, 27), updatedRun.SettledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveInvoice_RentalBillingInvoiceRevision_RecalculatesBillingRunAmountAndMarksProfileDirty()
    {
        PrepareAppRoot("georaeplan-rental-invoice-revision-recalculates-settlement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "렌탈 전표 수정 정산 재계산 거래처";
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

            var invoice = await db.Invoices.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);

            var receipt = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TransactionDate = new DateOnly(2026, 5, 27),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                BankReceipt = 50_000m,
                ReceiptTotal = 50_000m,
                SettlementAmount = 50_000m
            }, session);
            Assert.True(receipt.Success, receipt.Message);

            var latestInvoice = await db.Invoices.IgnoreQueryFilters()
                .Include(current => current.Lines.Where(line => !line.IsDeleted))
                .Include(current => current.Payments.Where(payment => !payment.IsDeleted))
                .AsNoTracking()
                .SingleAsync(current => current.Id == invoice.Id);
            var line = Assert.Single(latestInvoice.Lines);
            line.UnitPrice = 120_000m;
            line.LineAmount = 120_000m;

            var revise = await local.SaveInvoiceAsync(
                latestInvoice,
                new InvoiceSaveContext
                {
                    Username = "admin",
                    Role = DomainConstants.RoleAdmin,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ExpectedConcurrencyStamp = latestInvoice.ConcurrencyStamp
                },
                session);

            Assert.True(revise.Success, revise.Message);
            Assert.NotEqual(invoice.Id, revise.SavedInvoiceId);

            var updatedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.True(updatedProfile.IsDirty);
            Assert.Equal(50_000m, updatedProfile.SettledAmount);
            Assert.Equal(70_000m, updatedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, updatedProfile.CompletionStatus);

            var updatedRun = DeserializeRuns(updatedProfile).Single(current => current.RunId == runId);
            Assert.Equal(120_000m, updatedRun.BilledAmount);
            Assert.Equal(50_000m, updatedRun.SettledAmount);
            Assert.Equal("부분입금", updatedRun.SettlementStatus);
            Assert.Equal(PaymentFlowConstants.BillingStatusInProgress, updatedRun.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeletePayment_DirectRentalBillingInvoicePayment_RevertsRentalSettlement()
    {
        PrepareAppRoot("georaeplan-rental-direct-payment-delete-settlement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Delete direct rental invoice payment customer";
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
            var paymentId = Guid.NewGuid();

            var savePayment = await local.SavePaymentAsync(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 5, 27),
                Amount = invoice.TotalAmount,
                Note = "삭제 전 직접 전표 수금"
            }, session);
            Assert.True(savePayment.Success, savePayment.Message);
            Assert.Equal(invoice.TotalAmount, (await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId)).SettledAmount);

            await local.DeletePaymentAsync(paymentId);

            Assert.True(await db.Payments.IgnoreQueryFilters().Where(current => current.Id == paymentId).Select(current => current.IsDeleted).SingleAsync());
            var revertedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(0m, revertedProfile.SettledAmount);
            Assert.Equal(invoice.TotalAmount, revertedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedProfile.CompletionStatus);
            var revertedRun = DeserializeRuns(revertedProfile).Single(current => current.RunId == runId);
            Assert.Equal(0m, revertedRun.SettledAmount);
            Assert.NotEqual(PaymentFlowConstants.BillingStatusCompleted, revertedRun.Status);
            Assert.NotEqual(PaymentFlowConstants.SettlementStatusConfirmed, revertedRun.SettlementStatus);
            Assert.Null(revertedRun.SettledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeletePayment_DerivedRentalBillingInvoicePayment_DeletesSourceTransactionAndRevertsSettlement()
    {
        PrepareAppRoot("georaeplan-rental-derived-payment-delete-settlement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Delete derived rental invoice payment customer";
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
            var transactionId = Guid.NewGuid();
            var save = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TransactionDate = new DateOnly(2026, 5, 27),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedInvoiceId = invoice.Id,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                BankReceipt = invoice.TotalAmount,
                ReceiptTotal = invoice.TotalAmount,
                SettlementAmount = invoice.TotalAmount,
                Note = "삭제 전 전표 연동 렌탈 수금"
            }, session);
            Assert.True(save.Success, save.Message);
            Assert.Equal(invoice.TotalAmount, (await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId)).SettledAmount);

            await local.DeletePaymentAsync(transactionId);

            var deletedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.True(deletedPayment.IsDeleted);
            var deletedTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.True(deletedTransaction.IsDeleted);
            var revertedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(0m, revertedProfile.SettledAmount);
            Assert.Equal(invoice.TotalAmount, revertedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedProfile.CompletionStatus);
            var revertedRun = DeserializeRuns(revertedProfile).Single(current => current.RunId == runId);
            Assert.Equal(0m, revertedRun.SettledAmount);
            Assert.NotEqual(PaymentFlowConstants.BillingStatusCompleted, revertedRun.Status);
            Assert.NotEqual(PaymentFlowConstants.SettlementStatusConfirmed, revertedRun.SettlementStatus);
            Assert.Null(revertedRun.SettledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteInvoice_RentalBillingSalesInvoice_RevertsRentalSettlementAndMarksProfileDirty()
    {
        PrepareAppRoot("georaeplan-rental-delete-linked-invoice-recalculates");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Delete linked rental sales invoice customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "\uCCAD\uAD6C\uB300\uC0C1"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);

            var invoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            var transactionId = Guid.NewGuid();
            var save = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
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

            var completedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, completedProfile.SettledAmount);
            Assert.Equal(0m, completedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, completedProfile.CompletionStatus);

            var delete = await local.DeleteInvoiceAsync(invoice.Id, session);
            Assert.True(delete.Success, delete.Message);

            var deletedInvoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == invoice.Id);
            Assert.True(deletedInvoice.IsDeleted);

            var detachedTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.Null(detachedTransaction.LinkedInvoiceId);
            Assert.Equal(0m, detachedTransaction.SettlementAmount);
            Assert.Null(detachedTransaction.LinkedRentalBillingProfileId);
            Assert.Null(detachedTransaction.LinkedRentalBillingRunId);
            Assert.Equal(PaymentFlowConstants.TransactionKindReceipt, detachedTransaction.TransactionKind);

            var deletedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.True(deletedPayment.IsDeleted);

            var revertedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(0m, revertedProfile.SettledAmount);
            Assert.Equal(invoice.TotalAmount, revertedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedProfile.CompletionStatus);
            Assert.True(revertedProfile.IsDirty);

            var revertedRun = DeserializeRuns(revertedProfile).Single(current => current.RunId == runId);
            Assert.Equal(0m, revertedRun.SettledAmount);
            Assert.NotEqual(PaymentFlowConstants.BillingStatusCompleted, revertedRun.Status);
            Assert.NotEqual(PaymentFlowConstants.SettlementStatusConfirmed, revertedRun.SettlementStatus);
            Assert.Null(revertedRun.SettledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPull_RentalBillingInvoiceDelete_RevertsLocalSettlementWithoutCreatingDirtyRows()
    {
        PrepareAppRoot("georaeplan-rental-pull-invoice-delete-side-effects");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Pulled invoice delete rental customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "\uCCAD\uAD6C\uB300\uC0C1"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);

            var invoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            var transactionId = Guid.NewGuid();
            var save = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TransactionDate = new DateOnly(2026, 5, 26),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedInvoiceId = invoice.Id,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                BankReceipt = invoice.TotalAmount,
                ReceiptTotal = invoice.TotalAmount,
                SettlementAmount = invoice.TotalAmount
            }, session);
            Assert.True(save.Success, save.Message);

            var completedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, completedProfile.SettledAmount);
            Assert.Equal(0m, completedProfile.OutstandingAmount);

            var trackedProfile = await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId);
            trackedProfile.IsDirty = false;
            var trackedInvoice = await db.Invoices.SingleAsync(current => current.Id == invoice.Id);
            trackedInvoice.IsDirty = false;
            trackedInvoice.Revision = 940;
            var trackedTransaction = await db.Transactions.SingleAsync(current => current.Id == transactionId);
            trackedTransaction.IsDirty = false;
            trackedTransaction.Revision = 940;
            var trackedPayment = await db.Payments.SingleAsync(current => current.Id == transactionId);
            trackedPayment.IsDirty = false;
            trackedPayment.Revision = 940;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var pulledInvoice = LocalMappings.ToDto(await db.Invoices
                .IgnoreQueryFilters()
                .Include(current => current.Lines)
                .Include(current => current.Payments)
                .AsNoTracking()
                .SingleAsync(current => current.Id == invoice.Id));
            pulledInvoice.IsDeleted = true;
            pulledInvoice.IsLatestVersion = false;
            pulledInvoice.Revision = 941;
            pulledInvoice.UpdatedAtUtc = DateTime.UtcNow;
            pulledInvoice.Lines.Clear();
            pulledInvoice.Payments.Clear();

            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);

            await InvokePrivateInstanceTaskAsync(
                sync,
                "ApplyPullAsync",
                new SyncPullResponse
                {
                    CurrentServerRevision = 941,
                    Invoices = { pulledInvoice }
                },
                0L,
                CancellationToken.None,
                false);

            var deletedInvoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == invoice.Id);
            Assert.True(deletedInvoice.IsDeleted);
            Assert.False(deletedInvoice.IsDirty);

            var detachedTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.Null(detachedTransaction.LinkedInvoiceId);
            Assert.Equal(0m, detachedTransaction.SettlementAmount);
            Assert.Null(detachedTransaction.LinkedRentalBillingProfileId);
            Assert.Null(detachedTransaction.LinkedRentalBillingRunId);
            Assert.Equal(PaymentFlowConstants.TransactionKindReceipt, detachedTransaction.TransactionKind);
            Assert.False(detachedTransaction.IsDirty);

            var deletedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.True(deletedPayment.IsDeleted);
            Assert.False(deletedPayment.IsDirty);

            var revertedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(0m, revertedProfile.SettledAmount);
            Assert.Equal(invoice.TotalAmount, revertedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedProfile.CompletionStatus);
            Assert.False(revertedProfile.IsDirty);

            var revertedRun = DeserializeRuns(revertedProfile).Single(current => current.RunId == runId);
            Assert.Equal(0m, revertedRun.SettledAmount);
            Assert.NotEqual(PaymentFlowConstants.BillingStatusCompleted, revertedRun.Status);
            Assert.NotEqual(PaymentFlowConstants.SettlementStatusConfirmed, revertedRun.SettlementStatus);
            Assert.Null(revertedRun.SettledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestoreTransaction_RentalReceipt_RebuildsRunSettlementAndInvoicePayment()
    {
        PrepareAppRoot("georaeplan-rental-restore-transaction-run-settlement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Restore rental receipt customer";
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
            var transactionId = Guid.NewGuid();

            var save = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
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

            var delete = await local.DeleteTransactionAsync(transactionId, session);
            Assert.True(delete.Success, delete.Message);
            var deletedRun = await GetBillingRunAsync(db, profileId, runId);
            Assert.Equal(0m, deletedRun.SettledAmount);
            Assert.NotEqual(PaymentFlowConstants.BillingStatusCompleted, deletedRun.Status);
            Assert.True(await db.Payments.IgnoreQueryFilters().Where(current => current.Id == transactionId).Select(current => current.IsDeleted).SingleAsync());

            var restore = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Transaction,
                transactionId,
                session);

            Assert.True(restore.Success, restore.Message);
            var restoredTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.False(restoredTransaction.IsDeleted);
            var restoredPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.False(restoredPayment.IsDeleted);
            Assert.Equal(invoice.Id, restoredPayment.InvoiceId);
            Assert.Equal(invoice.TotalAmount, restoredPayment.Amount);
            var restoredProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, restoredProfile.SettledAmount);
            Assert.Equal(0m, restoredProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, restoredProfile.CompletionStatus);
            var restoredRun = DeserializeRuns(restoredProfile).Single(current => current.RunId == runId);
            Assert.Equal(invoice.TotalAmount, restoredRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, restoredRun.Status);
            Assert.Equal(PaymentFlowConstants.SettlementStatusConfirmed, restoredRun.SettlementStatus);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestoreTransaction_RestoresDeletedTransactionAttachments()
    {
        PrepareAppRoot("georaeplan-restore-transaction-attachments");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, "Restore transaction attachment customer"));
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 18),
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                ReceiptTotal = 1000m,
                SettlementAmount = 1000m,
                IsDeleted = true,
                IsDirty = false
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = transactionId,
                AttachmentType = "증빙",
                FileName = "restore-transaction-attachment.pdf",
                StoredFileName = "restore-transaction-attachment.pdf",
                StoredPath = "storage/restore-transaction-attachment.pdf",
                MimeType = "application/pdf",
                FileSize = 16,
                FileHash = "restore-transaction-attachment-hash",
                IsDeleted = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var restore = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Transaction,
                transactionId,
                session);

            Assert.True(restore.Success, restore.Message);
            var restoredAttachment = await db.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == attachmentId);
            Assert.False(restoredAttachment.IsDeleted);
            Assert.True(restoredAttachment.IsDirty);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestorePayment_DerivedFromDeletedRentalTransaction_RestoresSourceTransaction()
    {
        PrepareAppRoot("georaeplan-rental-restore-derived-payment-transaction");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Restore derived payment customer";
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
            var transactionId = Guid.NewGuid();

            var save = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TransactionDate = new DateOnly(2026, 5, 26),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoice.Id,
                BankReceipt = invoice.TotalAmount,
                ReceiptTotal = invoice.TotalAmount,
                SettlementAmount = invoice.TotalAmount
            }, session);
            Assert.True(save.Success, save.Message);

            var delete = await local.DeleteTransactionAsync(transactionId, session);
            Assert.True(delete.Success, delete.Message);
            Assert.True(await db.Transactions.IgnoreQueryFilters().Where(current => current.Id == transactionId).Select(current => current.IsDeleted).SingleAsync());
            Assert.True(await db.Payments.IgnoreQueryFilters().Where(current => current.Id == transactionId).Select(current => current.IsDeleted).SingleAsync());

            var restorePayment = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Payment,
                transactionId,
                session);

            Assert.True(restorePayment.Success, restorePayment.Message);
            var restoredTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.False(restoredTransaction.IsDeleted);
            Assert.Equal(profileId, restoredTransaction.LinkedRentalBillingProfileId);
            Assert.Equal(runId, restoredTransaction.LinkedRentalBillingRunId);
            var restoredPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.False(restoredPayment.IsDeleted);
            Assert.Equal(invoice.TotalAmount, restoredPayment.Amount);
            var restoredRun = await GetBillingRunAsync(db, profileId, runId);
            Assert.Equal(invoice.TotalAmount, restoredRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, restoredRun.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestorePayment_DerivedTransaction_RestoresDeletedTransactionAttachments()
    {
        PrepareAppRoot("georaeplan-restore-payment-derived-transaction-attachments");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, "Restore payment transaction attachment customer"));
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "LOCAL-PAY-RESTORE-TX-ATTACH",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 18),
                TotalAmount = 1000m,
                SupplyAmount = 1000m,
                VersionGroupId = invoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                IsDeleted = false
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 18),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoiceId,
                LinkedInvoiceNumber = "LOCAL-PAY-RESTORE-TX-ATTACH",
                ReceiptTotal = 1000m,
                SettlementAmount = 1000m,
                IsDeleted = true,
                IsDirty = false
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = transactionId,
                AttachmentType = "증빙",
                FileName = "restore-payment-transaction-attachment.pdf",
                StoredFileName = "restore-payment-transaction-attachment.pdf",
                StoredPath = "storage/restore-payment-transaction-attachment.pdf",
                MimeType = "application/pdf",
                FileSize = 16,
                FileHash = "restore-payment-transaction-attachment-hash",
                IsDeleted = true,
                IsDirty = false
            });
            db.Payments.Add(new LocalPayment
            {
                Id = transactionId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 18),
                Amount = 1000m,
                IsDeleted = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var restore = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Payment,
                transactionId,
                session);

            Assert.True(restore.Success, restore.Message);
            Assert.False(await db.Payments.IgnoreQueryFilters()
                .Where(current => current.Id == transactionId)
                .Select(current => current.IsDeleted)
                .SingleAsync());
            Assert.False(await db.Transactions.IgnoreQueryFilters()
                .Where(current => current.Id == transactionId)
                .Select(current => current.IsDeleted)
                .SingleAsync());
            var restoredAttachment = await db.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == attachmentId);
            Assert.False(restoredAttachment.IsDeleted);
            Assert.True(restoredAttachment.IsDirty);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestorePayment_AfterRentalInvoiceDelete_RelinksActiveTransactionAndRunSettlement()
    {
        PrepareAppRoot("georaeplan-rental-restore-payment-after-invoice-delete");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Restore payment after invoice delete customer";
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
            var transactionId = Guid.NewGuid();

            var save = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TransactionDate = new DateOnly(2026, 5, 26),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoice.Id,
                BankReceipt = invoice.TotalAmount,
                ReceiptTotal = invoice.TotalAmount,
                SettlementAmount = invoice.TotalAmount
            }, session);
            Assert.True(save.Success, save.Message);

            var delete = await local.DeleteInvoiceAsync(invoice.Id, session);
            Assert.True(delete.Success, delete.Message);
            Assert.True(await db.Payments.IgnoreQueryFilters()
                .Where(current => current.Id == transactionId)
                .Select(current => current.IsDeleted)
                .SingleAsync());
            var detachedTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.Null(detachedTransaction.LinkedInvoiceId);
            Assert.Equal(0m, detachedTransaction.SettlementAmount);
            Assert.Null(detachedTransaction.LinkedRentalBillingProfileId);
            Assert.Null(detachedTransaction.LinkedRentalBillingRunId);
            Assert.Equal(PaymentFlowConstants.TransactionKindReceipt, detachedTransaction.TransactionKind);

            var restore = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Payment,
                transactionId,
                session);

            Assert.True(restore.Success, restore.Message);
            var restoredInvoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == invoice.Id);
            Assert.False(restoredInvoice.IsDeleted);
            var restoredPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.False(restoredPayment.IsDeleted);
            Assert.Equal(invoice.Id, restoredPayment.InvoiceId);
            var restoredTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.False(restoredTransaction.IsDeleted);
            Assert.Equal(invoice.Id, restoredTransaction.LinkedInvoiceId);
            Assert.Equal(invoice.TotalAmount, restoredTransaction.SettlementAmount);
            Assert.Equal(profileId, restoredTransaction.LinkedRentalBillingProfileId);
            Assert.Equal(runId, restoredTransaction.LinkedRentalBillingRunId);
            var restoredRun = await GetBillingRunAsync(db, profileId, runId);
            Assert.Equal(invoice.TotalAmount, restoredRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, restoredRun.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestoreInvoice_RentalLinkedReceipt_RestoresPaymentTransactionAndRunSettlement()
    {
        PrepareAppRoot("georaeplan-rental-restore-invoice-linked-receipt");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Restore invoice linked receipt customer";
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
            var transactionId = Guid.NewGuid();

            var save = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TransactionDate = new DateOnly(2026, 5, 26),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoice.Id,
                BankReceipt = invoice.TotalAmount,
                ReceiptTotal = invoice.TotalAmount,
                SettlementAmount = invoice.TotalAmount
            }, session);
            Assert.True(save.Success, save.Message);

            var baselineRun = await GetBillingRunAsync(db, profileId, runId);
            Assert.Equal(invoice.TotalAmount, baselineRun.SettledAmount);

            var delete = await local.DeleteInvoiceAsync(invoice.Id, session);
            Assert.True(delete.Success, delete.Message);
            Assert.True(await db.Invoices.IgnoreQueryFilters()
                .Where(current => current.Id == invoice.Id)
                .Select(current => current.IsDeleted)
                .SingleAsync());
            Assert.True(await db.Payments.IgnoreQueryFilters()
                .Where(current => current.Id == transactionId)
                .Select(current => current.IsDeleted)
                .SingleAsync());
            var detachedTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.Null(detachedTransaction.LinkedInvoiceId);
            Assert.Equal(0m, detachedTransaction.SettlementAmount);
            Assert.Null(detachedTransaction.LinkedRentalBillingProfileId);
            Assert.Null(detachedTransaction.LinkedRentalBillingRunId);
            Assert.Equal(PaymentFlowConstants.TransactionKindReceipt, detachedTransaction.TransactionKind);
            var deletedRun = await GetBillingRunAsync(db, profileId, runId);
            Assert.Equal(0m, deletedRun.SettledAmount);

            var restore = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                invoice.Id,
                session);

            Assert.True(restore.Success, restore.Message);
            var restoredInvoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == invoice.Id);
            Assert.False(restoredInvoice.IsDeleted);
            var restoredPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.False(restoredPayment.IsDeleted);
            Assert.Equal(invoice.Id, restoredPayment.InvoiceId);
            Assert.Equal(invoice.TotalAmount, restoredPayment.Amount);
            var restoredTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.False(restoredTransaction.IsDeleted);
            Assert.Equal(invoice.Id, restoredTransaction.LinkedInvoiceId);
            Assert.Equal(invoice.TotalAmount, restoredTransaction.SettlementAmount);
            Assert.Equal(profileId, restoredTransaction.LinkedRentalBillingProfileId);
            Assert.Equal(runId, restoredTransaction.LinkedRentalBillingRunId);
            var restoredProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, restoredProfile.SettledAmount);
            Assert.Equal(0m, restoredProfile.OutstandingAmount);
            var restoredRun = DeserializeRuns(restoredProfile).Single(current => current.RunId == runId);
            Assert.Equal(invoice.TotalAmount, restoredRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, restoredRun.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestoreTransaction_RejectsLinkedInvoiceOutsideAccessibleOffice()
    {
        PrepareAppRoot("georaeplan-rental-restore-transaction-linked-invoice-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var transactionCustomerId = Guid.NewGuid();
            var hiddenCustomerId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(transactionCustomerId, "Scoped transaction customer"));
            db.Customers.Add(new LocalCustomer
            {
                Id = hiddenCustomerId,
                TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, OfficeCodeCatalog.Yeonsu),
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "Hidden invoice customer",
                NameMatchKey = "HiddenInvoiceCustomer",
                TradeType = CustomerTradeTypes.Sales,
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = hiddenInvoiceId,
                CustomerId = hiddenCustomerId,
                TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, OfficeCodeCatalog.Yeonsu),
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                InvoiceNumber = "LOCAL-HIDDEN-INVOICE",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 17),
                TotalAmount = 1000m,
                SupplyAmount = 1000m,
                IsDeleted = false
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = transactionCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 17),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = hiddenInvoiceId,
                BankReceipt = 1000m,
                ReceiptTotal = 1000m,
                SettlementAmount = 1000m,
                IsDeleted = true
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeOnlySession(OfficeCodeCatalog.Usenet);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var restore = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Transaction,
                transactionId,
                session);

            Assert.False(restore.Success);
            Assert.Contains("전표", restore.Message);
            Assert.True(await db.Transactions.IgnoreQueryFilters()
                .Where(current => current.Id == transactionId)
                .Select(current => current.IsDeleted)
                .SingleAsync());
            Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestoreInvoice_RejectsVersionGroupOutsideWritableOffice()
    {
        PrepareAppRoot("georaeplan-invoice-group-restore-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var visibleCustomerId = Guid.NewGuid();
            var hiddenCustomerId = Guid.NewGuid();
            var visibleInvoiceId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(visibleCustomerId, "Visible invoice group customer", OfficeCodeCatalog.Usenet));
            db.Customers.Add(CreateCustomer(hiddenCustomerId, "Hidden invoice group customer", OfficeCodeCatalog.Yeonsu));
            db.Invoices.Add(CreateInvoice(
                visibleInvoiceId,
                visibleCustomerId,
                OfficeCodeCatalog.Usenet,
                "LOCAL-GROUP-SCOPE-RESTORE",
                versionGroupId,
                versionNumber: 1,
                isDeleted: true,
                isLatestVersion: false));
            db.Invoices.Add(CreateInvoice(
                hiddenInvoiceId,
                hiddenCustomerId,
                OfficeCodeCatalog.Yeonsu,
                "LOCAL-GROUP-SCOPE-RESTORE-HIDDEN",
                versionGroupId,
                versionNumber: 2,
                isDeleted: true,
                isLatestVersion: true));
            await db.SaveChangesAsync();

            var session = CreateOfficeOnlySession(OfficeCodeCatalog.Usenet);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var restore = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                visibleInvoiceId,
                session);

            Assert.False(restore.Success);
            Assert.Contains("전표 묶음", restore.Message);
            db.ChangeTracker.Clear();
            var invoices = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(current => current.Id == visibleInvoiceId || current.Id == hiddenInvoiceId)
                .ToDictionaryAsync(current => current.Id);
            Assert.True(invoices[visibleInvoiceId].IsDeleted);
            Assert.True(invoices[hiddenInvoiceId].IsDeleted);
            Assert.False(invoices[visibleInvoiceId].IsDirty);
            Assert.False(invoices[hiddenInvoiceId].IsDirty);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PermanentlyDeleteInvoice_RejectsVersionGroupOutsideWritableOffice()
    {
        PrepareAppRoot("georaeplan-invoice-group-purge-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var visibleCustomerId = Guid.NewGuid();
            var hiddenCustomerId = Guid.NewGuid();
            var visibleInvoiceId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(visibleCustomerId, "Visible invoice purge customer", OfficeCodeCatalog.Usenet));
            db.Customers.Add(CreateCustomer(hiddenCustomerId, "Hidden invoice purge customer", OfficeCodeCatalog.Yeonsu));
            db.Invoices.Add(CreateInvoice(
                visibleInvoiceId,
                visibleCustomerId,
                OfficeCodeCatalog.Usenet,
                "LOCAL-GROUP-SCOPE-PURGE",
                versionGroupId,
                versionNumber: 1,
                isDeleted: true,
                isLatestVersion: false));
            db.Invoices.Add(CreateInvoice(
                hiddenInvoiceId,
                hiddenCustomerId,
                OfficeCodeCatalog.Yeonsu,
                "LOCAL-GROUP-SCOPE-PURGE-HIDDEN",
                versionGroupId,
                versionNumber: 2,
                isDeleted: true,
                isLatestVersion: true));
            await db.SaveChangesAsync();

            var session = CreateOfficeOnlySession(OfficeCodeCatalog.Usenet);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await local.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                visibleInvoiceId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("전표 묶음", purge.Message);
            db.ChangeTracker.Clear();
            Assert.True(await db.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == visibleInvoiceId));
            Assert.True(await db.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == hiddenInvoiceId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PermanentlyDeleteInvoice_AddsPurgeAuditForDeletedLinkedPayments()
    {
        PrepareAppRoot("georaeplan-invoice-purge-linked-payment-audit");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, "Invoice purge linked payment customer"));
            db.Invoices.Add(CreateInvoice(
                invoiceId,
                customerId,
                OfficeCodeCatalog.Usenet,
                "LOCAL-INV-PURGE-DELETED-PAYMENT",
                invoiceId,
                versionNumber: 1,
                isDeleted: true,
                isLatestVersion: true));
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 18),
                Amount = 1000m,
                IsDeleted = true
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var purge = await local.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId,
                session);

            Assert.True(purge.Success, purge.Message);
            Assert.False(await db.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == invoiceId));
            Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == paymentId));
            Assert.True(await db.AuditLogs.AnyAsync(current =>
                current.Action == "Purge" &&
                current.EntityName == nameof(LocalInvoice) &&
                current.EntityId == invoiceId.ToString("D")));
            Assert.True(await db.AuditLogs.AnyAsync(current =>
                current.Action == "Purge" &&
                current.EntityName == nameof(LocalPayment) &&
                current.EntityId == paymentId.ToString("D")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyServerPurgeInvoice_RemovesDeletedLinkedPayments()
    {
        PrepareAppRoot("georaeplan-server-purge-invoice-linked-payment");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, "Server purge invoice linked payment customer"));
            db.Invoices.Add(CreateInvoice(
                invoiceId,
                customerId,
                OfficeCodeCatalog.Usenet,
                "SERVER-PURGE-INV-DELETED-PAYMENT",
                invoiceId,
                versionNumber: 1,
                isDeleted: true,
                isLatestVersion: true));
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 19),
                Amount = 1000m,
                IsDeleted = true
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await local.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId);

            Assert.True(purge.Success, purge.Message);
            Assert.False(await db.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == invoiceId));
            Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == paymentId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ServerPurgeTransaction_RentalReceipt_RebuildsRunSettlementAndRemovesDerivedPayment()
    {
        PrepareAppRoot("georaeplan-rental-purge-transaction-run-settlement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Purge rental receipt customer";
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
            var transactionId = Guid.NewGuid();

            var save = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
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

            var purge = await local.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Transaction,
                transactionId);

            Assert.True(purge.Success, purge.Message);
            Assert.False(await db.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
            Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
            var purgedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(0m, purgedProfile.SettledAmount);
            Assert.Equal(invoice.TotalAmount, purgedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, purgedProfile.CompletionStatus);
            var purgedRun = DeserializeRuns(purgedProfile).Single(current => current.RunId == runId);
            Assert.Equal(0m, purgedRun.SettledAmount);
            Assert.NotEqual(PaymentFlowConstants.BillingStatusCompleted, purgedRun.Status);
            Assert.NotEqual(PaymentFlowConstants.SettlementStatusConfirmed, purgedRun.SettlementStatus);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PermanentlyDeleteTransaction_RejectsWhenLinkedPaymentIsActive()
    {
        PrepareAppRoot("georaeplan-rental-purge-transaction-active-linked-payment");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentFile = Path.Combine(Path.GetTempPath(), $"georaeplan-blocked-purge-{Guid.NewGuid():N}.bin");
            await File.WriteAllTextAsync(attachmentFile, "blocked purge evidence");
            db.Customers.Add(CreateCustomer(customerId, "Active linked payment customer"));
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "LOCAL-TX-PURGE-ACTIVE-PAYMENT",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 17),
                TotalAmount = 1000m,
                SupplyAmount = 1000m,
                IsDeleted = false
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 17),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoiceId,
                ReceiptTotal = 1000m,
                SettlementAmount = 1000m,
                IsDeleted = true
            });
            db.Payments.Add(new LocalPayment
            {
                Id = transactionId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 17),
                Amount = 1000m,
                IsDeleted = false
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                FileName = Path.GetFileName(attachmentFile),
                StoredFileName = Path.GetFileName(attachmentFile),
                StoredPath = attachmentFile,
                IsDeleted = true
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var purge = await local.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Transaction,
                transactionId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("활성", purge.Message);
            Assert.True(await db.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
            Assert.True(await db.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId && !current.IsDeleted));
            Assert.True(await db.TransactionAttachments.IgnoreQueryFilters().AnyAsync(current => current.TransactionId == transactionId));
            Assert.True(File.Exists(attachmentFile));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PermanentlyDeletePayment_RejectsWhenLinkedTransactionStillExists()
    {
        PrepareAppRoot("georaeplan-payment-purge-linked-transaction-exists");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, "Linked transaction payment purge customer"));
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "LOCAL-PAY-PURGE-LINKED-TX",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 18),
                TotalAmount = 1000m,
                SupplyAmount = 1000m,
                IsDeleted = false
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 18),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoiceId,
                ReceiptTotal = 1000m,
                SettlementAmount = 1000m,
                IsDeleted = true
            });
            db.Payments.Add(new LocalPayment
            {
                Id = transactionId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 18),
                Amount = 1000m,
                IsDeleted = true
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var purge = await local.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Payment,
                transactionId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("거래내역", purge.Message);
            Assert.True(await db.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
            Assert.True(await db.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PermanentlyDeletePayment_RejectsTemporaryAccessOutsideWritableOffice()
    {
        PrepareAppRoot("georaeplan-payment-purge-temp-access-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var hiddenCustomerId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            var hiddenPaymentId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(hiddenCustomerId, "Hidden payment purge customer", OfficeCodeCatalog.Yeonsu));
            db.Invoices.Add(CreateInvoice(
                hiddenInvoiceId,
                hiddenCustomerId,
                OfficeCodeCatalog.Yeonsu,
                "LOCAL-PAYMENT-PURGE-TEMP-HIDDEN",
                hiddenInvoiceId,
                versionNumber: 1,
                isDeleted: false,
                isLatestVersion: true));
            db.Payments.Add(new LocalPayment
            {
                Id = hiddenPaymentId,
                InvoiceId = hiddenInvoiceId,
                PaymentDate = new DateOnly(2026, 6, 18),
                Amount = 1000m,
                IsDeleted = true
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeOnlySession(OfficeCodeCatalog.Usenet);
            var officeAccess = new OfficeAccessService();
            officeAccess.GrantTemporaryCustomerAccess(session, hiddenCustomerId);
            var local = new LocalStateService(db, officeAccess, new SyncRequestDispatcher(), session);

            var purge = await local.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Payment,
                hiddenPaymentId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("권한", purge.Message);
            Assert.True(await db.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == hiddenPaymentId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PermanentlyDeleteTransaction_RejectsLinkedPaymentInvoiceTemporaryAccessOutsideWritableOffice()
    {
        PrepareAppRoot("georaeplan-transaction-purge-temp-linked-payment-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var transactionCustomerId = Guid.NewGuid();
            var hiddenCustomerId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentFile = Path.Combine(Path.GetTempPath(), $"georaeplan-hidden-linked-purge-{Guid.NewGuid():N}.bin");
            await File.WriteAllTextAsync(attachmentFile, "hidden linked payment purge evidence");
            db.Customers.Add(CreateCustomer(transactionCustomerId, "Visible transaction purge customer", OfficeCodeCatalog.Usenet));
            db.Customers.Add(CreateCustomer(hiddenCustomerId, "Hidden linked payment purge customer", OfficeCodeCatalog.Yeonsu));
            db.Invoices.Add(CreateInvoice(
                hiddenInvoiceId,
                hiddenCustomerId,
                OfficeCodeCatalog.Yeonsu,
                "LOCAL-TX-PURGE-TEMP-HIDDEN",
                hiddenInvoiceId,
                versionNumber: 1,
                isDeleted: false,
                isLatestVersion: true));
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = transactionCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 18),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = hiddenInvoiceId,
                ReceiptTotal = 1000m,
                SettlementAmount = 1000m,
                IsDeleted = true
            });
            db.Payments.Add(new LocalPayment
            {
                Id = transactionId,
                InvoiceId = hiddenInvoiceId,
                PaymentDate = new DateOnly(2026, 6, 18),
                Amount = 1000m,
                IsDeleted = true
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                FileName = Path.GetFileName(attachmentFile),
                StoredFileName = Path.GetFileName(attachmentFile),
                StoredPath = attachmentFile,
                IsDeleted = true
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeOnlySession(OfficeCodeCatalog.Usenet);
            var officeAccess = new OfficeAccessService();
            officeAccess.GrantTemporaryCustomerAccess(session, hiddenCustomerId);
            var local = new LocalStateService(db, officeAccess, new SyncRequestDispatcher(), session);

            var purge = await local.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Transaction,
                transactionId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("연동 수금/지급", purge.Message);
            Assert.True(await db.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
            Assert.True(await db.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
            Assert.True(await db.TransactionAttachments.IgnoreQueryFilters().AnyAsync(current => current.TransactionId == transactionId));
            Assert.True(File.Exists(attachmentFile));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPull_DirectRentalBillingInvoicePayment_UpdatesRentalSettlement()
    {
        PrepareAppRoot("georaeplan-rental-pull-payment-settlement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Pulled payment rental customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "\uCCAD\uAD6C\uB300\uC0C1"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);

            var invoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            var trackedProfile = await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId);
            trackedProfile.IsDirty = false;
            var trackedInvoice = await db.Invoices.SingleAsync(current => current.Id == invoice.Id);
            trackedInvoice.IsDirty = false;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            var paymentId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            await InvokePrivateInstanceTaskAsync(
                sync,
                "ApplyPullAsync",
                new SyncPullResponse
                {
                    CurrentServerRevision = 900,
                    Payments =
                    {
                        new PaymentDto
                        {
                            Id = paymentId,
                            InvoiceId = invoice.Id,
                            PaymentDate = new DateOnly(2026, 5, 27),
                            Amount = invoice.TotalAmount,
                            Note = "pulled direct rental payment",
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            Revision = 900,
                            IsDeleted = false
                        }
                    }
                },
                0L,
                CancellationToken.None,
                false);

            var pulledPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == paymentId);
            Assert.False(pulledPayment.IsDeleted);
            Assert.False(pulledPayment.IsDirty);
            Assert.Equal(invoice.TotalAmount, pulledPayment.Amount);

            var updatedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, updatedProfile.SettledAmount);
            Assert.Equal(0m, updatedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, updatedProfile.CompletionStatus);
            Assert.False(updatedProfile.IsDirty);

            var updatedRun = DeserializeRuns(updatedProfile).Single(current => current.RunId == runId);
            Assert.Equal(invoice.TotalAmount, updatedRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, updatedRun.Status);
            Assert.Equal(PaymentFlowConstants.SettlementStatusConfirmed, updatedRun.SettlementStatus);
            Assert.Equal(new DateOnly(2026, 5, 27), updatedRun.SettledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPull_RelinkedDirectRentalBillingPayment_RecalculatesPreviousAndTargetSettlement()
    {
        PrepareAppRoot("georaeplan-rental-pull-payment-relink-settlement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var firstProfileId = Guid.NewGuid();
            var firstAssetId = Guid.NewGuid();
            var firstCustomerId = Guid.NewGuid();
            var firstCustomerName = "Pulled payment relink source customer";
            db.Customers.Add(CreateCustomer(firstCustomerId, firstCustomerName));
            var firstProfile = CreateBillingProfile(firstProfileId, firstAssetId, firstCustomerName);
            firstProfile.CustomerId = firstCustomerId;
            db.RentalBillingProfiles.Add(firstProfile);
            db.RentalAssets.Add(CreateRentalAsset(firstAssetId, firstCustomerName, firstProfileId, "Billing standby"));

            var secondProfileId = Guid.NewGuid();
            var secondAssetId = Guid.NewGuid();
            var secondCustomerId = Guid.NewGuid();
            var secondCustomerName = "Pulled payment relink target customer";
            db.Customers.Add(CreateCustomer(secondCustomerId, secondCustomerName));
            var secondProfile = CreateBillingProfile(secondProfileId, secondAssetId, secondCustomerName);
            secondProfile.CustomerId = secondCustomerId;
            db.RentalBillingProfiles.Add(secondProfile);
            db.RentalAssets.Add(CreateRentalAsset(secondAssetId, secondCustomerName, secondProfileId, "Billing standby"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);

            var firstStart = await rental.StartBillingAsync(firstProfileId, new DateOnly(2026, 5, 25), session);
            Assert.True(firstStart.Success, firstStart.Message);
            var secondStart = await rental.StartBillingAsync(secondProfileId, new DateOnly(2026, 5, 25), session);
            Assert.True(secondStart.Success, secondStart.Message);

            var firstInvoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == firstStart.RelatedEntityId);
            var secondInvoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == secondStart.RelatedEntityId);
            var firstRunId = Assert.IsType<Guid>(firstInvoice.LinkedRentalBillingRunId);
            var secondRunId = Assert.IsType<Guid>(secondInvoice.LinkedRentalBillingRunId);
            var paymentId = Guid.NewGuid();

            var savePayment = await local.SavePaymentAsync(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = firstInvoice.Id,
                PaymentDate = new DateOnly(2026, 5, 27),
                Amount = firstInvoice.TotalAmount,
                Note = "local source payment before pull",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            }, session);
            Assert.True(savePayment.Success, savePayment.Message);

            var baselineProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == firstProfileId);
            Assert.Equal(firstInvoice.TotalAmount, baselineProfile.SettledAmount);

            foreach (var profile in await db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync())
                profile.IsDirty = false;
            foreach (var invoice in await db.Invoices.IgnoreQueryFilters().ToListAsync())
                invoice.IsDirty = false;
            var trackedPayment = await db.Payments.IgnoreQueryFilters().SingleAsync(current => current.Id == paymentId);
            trackedPayment.IsDirty = false;
            trackedPayment.Revision = 900;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            var now = DateTime.UtcNow;

            await InvokePrivateInstanceTaskAsync(
                sync,
                "ApplyPullAsync",
                new SyncPullResponse
                {
                    CurrentServerRevision = 901,
                    Payments =
                    {
                        new PaymentDto
                        {
                            Id = paymentId,
                            InvoiceId = secondInvoice.Id,
                            PaymentDate = new DateOnly(2026, 5, 28),
                            Amount = secondInvoice.TotalAmount,
                            Note = "pulled relinked target payment",
                            CreatedAtUtc = now.AddDays(-1),
                            UpdatedAtUtc = now,
                            Revision = 901,
                            IsDeleted = false
                        }
                    }
                },
                0L,
                CancellationToken.None,
                false);

            var pulledPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == paymentId);
            Assert.Equal(secondInvoice.Id, pulledPayment.InvoiceId);
            Assert.Equal(secondInvoice.TotalAmount, pulledPayment.Amount);
            Assert.False(pulledPayment.IsDirty);

            var revertedSourceProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == firstProfileId);
            Assert.Equal(0m, revertedSourceProfile.SettledAmount);
            Assert.Equal(firstInvoice.TotalAmount, revertedSourceProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedSourceProfile.CompletionStatus);
            Assert.False(revertedSourceProfile.IsDirty);
            var revertedSourceRun = DeserializeRuns(revertedSourceProfile).Single(current => current.RunId == firstRunId);
            Assert.Equal(0m, revertedSourceRun.SettledAmount);
            Assert.NotEqual(PaymentFlowConstants.BillingStatusCompleted, revertedSourceRun.Status);
            Assert.NotEqual(PaymentFlowConstants.SettlementStatusConfirmed, revertedSourceRun.SettlementStatus);
            Assert.Null(revertedSourceRun.SettledDate);

            var completedTargetProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == secondProfileId);
            Assert.Equal(secondInvoice.TotalAmount, completedTargetProfile.SettledAmount);
            Assert.Equal(0m, completedTargetProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, completedTargetProfile.CompletionStatus);
            Assert.False(completedTargetProfile.IsDirty);
            var completedTargetRun = DeserializeRuns(completedTargetProfile).Single(current => current.RunId == secondRunId);
            Assert.Equal(secondInvoice.TotalAmount, completedTargetRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, completedTargetRun.Status);
            Assert.Equal(PaymentFlowConstants.SettlementStatusConfirmed, completedTargetRun.SettlementStatus);
            Assert.Equal(new DateOnly(2026, 5, 28), completedTargetRun.SettledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPull_RentalReceiptTransaction_CreatesDerivedPaymentAndUpdatesSettlement()
    {
        PrepareAppRoot("georaeplan-rental-pull-transaction-settlement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Pulled rental receipt transaction customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "\uCCAD\uAD6C\uB300\uC0C1"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);

            var invoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            var trackedProfile = await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId);
            trackedProfile.IsDirty = false;
            var trackedInvoice = await db.Invoices.SingleAsync(current => current.Id == invoice.Id);
            trackedInvoice.IsDirty = false;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            var transactionId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            await InvokePrivateInstanceTaskAsync(
                sync,
                "ApplyPullAsync",
                new SyncPullResponse
                {
                    CurrentServerRevision = 910,
                    Transactions =
                    {
                        new TransactionDto
                        {
                            Id = transactionId,
                            CustomerId = customerId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                            TransactionDate = new DateOnly(2026, 5, 27),
                            TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                            LinkedInvoiceId = invoice.Id,
                            LinkedInvoiceNumber = invoice.InvoiceNumber,
                            LinkedRentalBillingProfileId = profileId,
                            LinkedRentalBillingRunId = runId,
                            BankReceipt = invoice.TotalAmount,
                            ReceiptTotal = invoice.TotalAmount,
                            SettlementAmount = invoice.TotalAmount,
                            Note = "pulled rental receipt transaction",
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            Revision = 910,
                            IsDeleted = false
                        }
                    }
                },
                0L,
                CancellationToken.None,
                false);

            var pulledTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.False(pulledTransaction.IsDeleted);
            Assert.False(pulledTransaction.IsDirty);
            Assert.Equal(profileId, pulledTransaction.LinkedRentalBillingProfileId);
            Assert.Equal(runId, pulledTransaction.LinkedRentalBillingRunId);

            var derivedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.False(derivedPayment.IsDeleted);
            Assert.False(derivedPayment.IsDirty);
            Assert.Equal(invoice.Id, derivedPayment.InvoiceId);
            Assert.Equal(invoice.TotalAmount, derivedPayment.Amount);

            var updatedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, updatedProfile.SettledAmount);
            Assert.Equal(0m, updatedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, updatedProfile.CompletionStatus);
            Assert.False(updatedProfile.IsDirty);

            var updatedRun = DeserializeRuns(updatedProfile).Single(current => current.RunId == runId);
            Assert.Equal(invoice.TotalAmount, updatedRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, updatedRun.Status);
            Assert.Equal(PaymentFlowConstants.SettlementStatusConfirmed, updatedRun.SettlementStatus);
            Assert.Equal(new DateOnly(2026, 5, 27), updatedRun.SettledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPull_RentalReceiptTransactionDelete_RemovesDerivedPaymentAndRevertsSettlement()
    {
        PrepareAppRoot("georaeplan-rental-pull-transaction-delete-settlement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Pulled rental receipt transaction delete customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "\uCCAD\uAD6C\uB300\uC0C1"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);

            var invoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            var transactionId = Guid.NewGuid();
            var save = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TransactionDate = new DateOnly(2026, 5, 27),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedInvoiceId = invoice.Id,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                BankReceipt = invoice.TotalAmount,
                ReceiptTotal = invoice.TotalAmount,
                SettlementAmount = invoice.TotalAmount,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            }, session);
            Assert.True(save.Success, save.Message);

            var baselineProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, baselineProfile.SettledAmount);

            var trackedProfile = await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId);
            trackedProfile.IsDirty = false;
            var trackedTransaction = await db.Transactions.SingleAsync(current => current.Id == transactionId);
            trackedTransaction.IsDirty = false;
            trackedTransaction.Revision = 920;
            var trackedPayment = await db.Payments.SingleAsync(current => current.Id == transactionId);
            trackedPayment.IsDirty = false;
            trackedPayment.Revision = 920;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            var now = DateTime.UtcNow;

            await InvokePrivateInstanceTaskAsync(
                sync,
                "ApplyPullAsync",
                new SyncPullResponse
                {
                    CurrentServerRevision = 921,
                    Transactions =
                    {
                        new TransactionDto
                        {
                            Id = transactionId,
                            CustomerId = customerId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                            TransactionDate = new DateOnly(2026, 5, 27),
                            TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                            LinkedInvoiceId = invoice.Id,
                            LinkedInvoiceNumber = invoice.InvoiceNumber,
                            LinkedRentalBillingProfileId = profileId,
                            LinkedRentalBillingRunId = runId,
                            BankReceipt = invoice.TotalAmount,
                            ReceiptTotal = invoice.TotalAmount,
                            SettlementAmount = invoice.TotalAmount,
                            Note = "pulled rental receipt transaction delete",
                            CreatedAtUtc = now.AddDays(-1),
                            UpdatedAtUtc = now,
                            Revision = 921,
                            IsDeleted = true
                        }
                    }
                },
                0L,
                CancellationToken.None,
                false);

            var deletedTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.True(deletedTransaction.IsDeleted);
            Assert.False(deletedTransaction.IsDirty);

            var deletedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == transactionId);
            Assert.True(deletedPayment.IsDeleted);
            Assert.False(deletedPayment.IsDirty);

            var revertedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(0m, revertedProfile.SettledAmount);
            Assert.Equal(invoice.TotalAmount, revertedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedProfile.CompletionStatus);
            Assert.False(revertedProfile.IsDirty);

            var revertedRun = DeserializeRuns(revertedProfile).Single(current => current.RunId == runId);
            Assert.Equal(0m, revertedRun.SettledAmount);
            Assert.NotEqual(PaymentFlowConstants.BillingStatusCompleted, revertedRun.Status);
            Assert.NotEqual(PaymentFlowConstants.SettlementStatusConfirmed, revertedRun.SettlementStatus);
            Assert.Null(revertedRun.SettledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPull_OutOfOfficeRentalTransactionSideEffects_DoNotBecomeDirtyOrVisibleToOfficeOnlyUser()
    {
        PrepareAppRoot("georaeplan-rental-pull-cross-office-side-effect-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var hiddenProfileId = Guid.NewGuid();
            var hiddenAssetId = Guid.NewGuid();
            var hiddenCustomerId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            var hiddenRunId = Guid.NewGuid();
            var hiddenTransactionId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            var session = CreateOfficeOnlySession(OfficeCodeCatalog.Usenet);
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);

            await InvokePrivateInstanceTaskAsync(
                sync,
                "ApplyPullAsync",
                new SyncPullResponse
                {
                    CurrentServerRevision = 930,
                    Customers =
                    {
                        new CustomerDto
                        {
                            Id = hiddenCustomerId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Yeonsu,
                            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                            NameOriginal = "Hidden Yeonsu customer",
                            NameMatchKey = "HIDDENYEONSU",
                            TradeType = CustomerTradeTypes.Sales,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            Revision = 930
                        }
                    },
                    RentalBillingProfiles =
                    {
                        CreateBillingProfileDto(
                            hiddenProfileId,
                            hiddenAssetId,
                            hiddenCustomerId,
                            "Hidden Yeonsu customer",
                            OfficeCodeCatalog.Yeonsu,
                            hiddenRunId,
                            revision: 930)
                    },
                    RentalAssets =
                    {
                        CreateRentalAssetDto(
                            hiddenAssetId,
                            hiddenProfileId,
                            "Hidden Yeonsu customer",
                            OfficeCodeCatalog.Yeonsu,
                            revision: 930)
                    },
                    Invoices =
                    {
                        CreateRentalInvoiceDto(
                            hiddenInvoiceId,
                            hiddenCustomerId,
                            hiddenProfileId,
                            hiddenRunId,
                            OfficeCodeCatalog.Yeonsu,
                            revision: 930)
                    },
                    Transactions =
                    {
                        new TransactionDto
                        {
                            Id = hiddenTransactionId,
                            CustomerId = hiddenCustomerId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Yeonsu,
                            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                            TransactionDate = new DateOnly(2026, 5, 27),
                            TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                            LinkedInvoiceId = hiddenInvoiceId,
                            LinkedInvoiceNumber = "HIDDEN-YEONSU-INV",
                            LinkedRentalBillingProfileId = hiddenProfileId,
                            LinkedRentalBillingRunId = hiddenRunId,
                            BankReceipt = 100_000m,
                            ReceiptTotal = 100_000m,
                            SettlementAmount = 100_000m,
                            Note = "hidden cross office pulled rental transaction",
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            Revision = 930
                        }
                    }
                },
                0L,
                CancellationToken.None,
                false);

            var hiddenPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == hiddenTransactionId);
            Assert.False(hiddenPayment.IsDirty);
            Assert.Equal(hiddenInvoiceId, hiddenPayment.InvoiceId);
            var hiddenProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == hiddenProfileId);
            Assert.False(hiddenProfile.IsDirty);
            Assert.Equal(100_000m, hiddenProfile.SettledAmount);

            Assert.Empty(await local.GetDirtyTransactionsForSyncAsync(session));
            Assert.Empty(await local.GetDirtyPaymentsForSyncAsync(session));
            var visibleInvoices = await local.GetInvoicesAsync(null, null, null, session);
            Assert.DoesNotContain(visibleInvoices, invoice => invoice.Id == hiddenInvoiceId);

            var visibleRows = await rental.GetBillingRowsAsync(
                new RentalBillingFilter { ExpandCustomerSummaryRows = true },
                session);
            Assert.DoesNotContain(visibleRows, row => row.SelectionId == hiddenProfileId || row.CustomerDisplayName == "Hidden Yeonsu customer");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPull_DirectRentalBillingInvoicePaymentDelete_RevertsRentalSettlement()
    {
        PrepareAppRoot("georaeplan-rental-pull-payment-delete-settlement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Pulled payment delete rental customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "\uCCAD\uAD6C\uB300\uC0C1"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);

            var invoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            var paymentId = Guid.NewGuid();
            var save = await local.SavePaymentAsync(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 5, 27),
                Amount = invoice.TotalAmount,
                Note = "local baseline rental payment",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            }, session);
            Assert.True(save.Success, save.Message);

            var baselineProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, baselineProfile.SettledAmount);

            var trackedProfile = await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId);
            trackedProfile.IsDirty = false;
            var trackedPayment = await db.Payments.SingleAsync(current => current.Id == paymentId);
            trackedPayment.IsDirty = false;
            trackedPayment.Revision = 900;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            var now = DateTime.UtcNow;

            await InvokePrivateInstanceTaskAsync(
                sync,
                "ApplyPullAsync",
                new SyncPullResponse
                {
                    CurrentServerRevision = 901,
                    Payments =
                    {
                        new PaymentDto
                        {
                            Id = paymentId,
                            InvoiceId = invoice.Id,
                            PaymentDate = new DateOnly(2026, 5, 27),
                            Amount = invoice.TotalAmount,
                            Note = "pulled direct rental payment delete",
                            CreatedAtUtc = now.AddDays(-1),
                            UpdatedAtUtc = now,
                            Revision = 901,
                            IsDeleted = true
                        }
                    }
                },
                0L,
                CancellationToken.None,
                false);

            var deletedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == paymentId);
            Assert.True(deletedPayment.IsDeleted);
            Assert.False(deletedPayment.IsDirty);

            var revertedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(0m, revertedProfile.SettledAmount);
            Assert.Equal(invoice.TotalAmount, revertedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedProfile.CompletionStatus);
            Assert.False(revertedProfile.IsDirty);

            var revertedRun = DeserializeRuns(revertedProfile).Single(current => current.RunId == runId);
            Assert.Equal(0m, revertedRun.SettledAmount);
            Assert.NotEqual(PaymentFlowConstants.BillingStatusCompleted, revertedRun.Status);
            Assert.NotEqual(PaymentFlowConstants.SettlementStatusConfirmed, revertedRun.SettlementStatus);
            Assert.Null(revertedRun.SettledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RegisterBillingSettlement_RequiresPaymentEditBeforeCreatingEvidence()
    {
        PrepareAppRoot("georaeplan-rental-register-settlement-requires-payment-edit");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Register settlement payment permission customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            profile.SettledAmount = 0m;
            profile.OutstandingAmount = 100_000m;
            profile.SettlementStatus = PaymentFlowConstants.SettlementStatusPending;
            profile.CompletionStatus = PaymentFlowConstants.CompletionPending;
            profile.IsDirty = false;
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var session = CreateUserSession(AppPermissionNames.RentalProfileEdit);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);

            var result = await rental.RegisterBillingSettlementAsync(
                profileId,
                new DateOnly(2026, 5, 27),
                50_000m,
                "payment permission denied",
                session);

            Assert.False(result.Success);
            Assert.Contains("수금/지급 편집 권한", result.Message);
            Assert.Empty(await db.Transactions.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.Payments.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.RentalBillingLogs.IgnoreQueryFilters().ToListAsync());

            var storedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.False(storedProfile.IsDirty);
            Assert.Equal(0m, storedProfile.SettledAmount);
            Assert.Equal(100_000m, storedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.SettlementStatusPending, storedProfile.SettlementStatus);
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
        => CreateCustomer(customerId, customerName, OfficeCodeCatalog.Usenet);

    private static LocalCustomer CreateCustomer(Guid customerId, string customerName, string officeCode, bool isDeleted = false)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            NameOriginal = customerName,
            NameMatchKey = customerName,
            TradeType = CustomerTradeTypes.Sales,
            ResponsibleOfficeCode = officeCode,
            IsDeleted = isDeleted,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalInvoice CreateInvoice(
        Guid invoiceId,
        Guid customerId,
        string officeCode,
        string invoiceNumber,
        Guid versionGroupId,
        int versionNumber,
        bool isDeleted,
        bool isLatestVersion)
        => new()
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            InvoiceNumber = invoiceNumber,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 1000m,
            SupplyAmount = 1000m,
            VersionGroupId = versionGroupId,
            VersionNumber = versionNumber,
            IsLatestVersion = isLatestVersion,
            IsDeleted = isDeleted,
            IsDirty = false,
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

    private static RentalBillingProfileDto CreateBillingProfileDto(
        Guid profileId,
        Guid assetId,
        Guid customerId,
        string customerName,
        string officeCode,
        Guid runId,
        long revision)
    {
        var now = DateTime.UtcNow;
        return new RentalBillingProfileDto
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            ManagementCompanyCode = officeCode,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = customerName,
            InstallSiteName = customerName,
            ItemName = "Scope guard rental item",
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            BillingStatus = "청구중",
            SettlementStatus = PaymentFlowConstants.SettlementStatusPending,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            BillingDay = 25,
            BillingCycleMonths = 1,
            MonthlyAmount = 100_000m,
            OutstandingAmount = 100_000m,
            BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "Scope guard rental item",
                    BillingLineMode = "묶음",
                    RepresentativeAssetId = assetId,
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m,
                    IncludedAssetIds = [assetId]
                }
            }),
            BillingRunsJson = JsonSerializer.Serialize(new List<RentalBillingRunModel>
            {
                new()
                {
                    RunId = runId,
                    RunKey = "2026-05",
                    ScheduledDate = new DateOnly(2026, 5, 25),
                    PeriodStartDate = new DateOnly(2026, 5, 1),
                    PeriodEndDate = new DateOnly(2026, 5, 31),
                    PeriodLabel = "2026-05",
                    Status = PaymentFlowConstants.BillingStatusInProgress,
                    BilledAmount = 100_000m,
                    SettledAmount = 0m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPending
                }
            }),
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = revision
        };
    }

    private static RentalAssetDto CreateRentalAssetDto(
        Guid assetId,
        Guid profileId,
        string customerName,
        string officeCode,
        long revision)
    {
        var now = DateTime.UtcNow;
        return new RentalAssetDto
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            ManagementCompanyCode = officeCode,
            ManagementId = $"M-{assetId:N}",
            ManagementNumber = ShortCode("MN", assetId),
            AssetKey = $"AK-{assetId:N}",
            CustomerName = customerName,
            CurrentCustomerName = customerName,
            InstallSiteName = customerName,
            InstallLocation = customerName,
            ItemName = "Scope guard rental item",
            MachineNumber = ShortCode("SN", assetId),
            AssetStatus = "임대진행중",
            BillingProfileId = profileId,
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = 100_000m,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = revision
        };
    }

    private static InvoiceDto CreateRentalInvoiceDto(
        Guid invoiceId,
        Guid customerId,
        Guid profileId,
        Guid runId,
        string officeCode,
        long revision)
    {
        var now = DateTime.UtcNow;
        return new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customerId,
            CustomerName = "Hidden Yeonsu customer",
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            InvoiceNumber = "HIDDEN-YEONSU-INV",
            LocalTempNumber = "HIDDEN-YEONSU-TMP",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 25),
            TotalAmount = 100_000m,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = revision,
            Lines =
            {
                new InvoiceLineDto
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "Scope guard rental item",
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    LineAmount = 100_000m,
                    OrderIndex = 0
                }
            }
        };
    }

    private static string ShortCode(string prefix, Guid id)
        => $"{prefix}-{id:N}".Substring(0, 12);

    private static async Task<RentalBillingRunModel> GetBillingRunAsync(
        LocalDbContext db,
        Guid profileId,
        Guid runId)
    {
        var profile = await db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == profileId);
        return DeserializeRuns(profile).Single(current => current.RunId == runId);
    }

    private static readonly JsonSerializerOptions BillingRunJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static List<RentalBillingRunModel> DeserializeRuns(LocalRentalBillingProfile profile)
        => JsonSerializer.Deserialize<List<RentalBillingRunModel>>(profile.BillingRunsJson, BillingRunJsonOptions)
           ?? new List<RentalBillingRunModel>();

    private static async Task InvokePrivateInstanceTaskAsync(object target, string methodName, params object?[]? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method!.Invoke(target, args);
        Assert.NotNull(result);
        var task = Assert.IsAssignableFrom<Task>(result);
        await task;
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

    private static SessionState CreateOfficeOnlySession(string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = $"user-{officeCode}",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        });
        return session;
    }
}
