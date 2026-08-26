using System.Reflection;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
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
            var mirroredTransaction = await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == paymentId);
            Assert.Equal(invoice.Id, mirroredTransaction.LinkedInvoiceId);
            Assert.Equal(invoice.TotalAmount, mirroredTransaction.SettlementAmount);

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
    public async Task DeleteBillingHistory_WithStaleInvoiceRevision_DoesNotPartiallyDeleteLinkedSettlement()
    {
        PrepareAppRoot("georaeplan-rental-delete-history-stale-invoice-revision");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Rental stale invoice delete guard customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var started = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(started.Success, started.Message);

            var invoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == started.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);

            var saveSettlement = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TransactionDate = new DateOnly(2026, 5, 27),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoice.Id,
                BankReceipt = invoice.TotalAmount,
                ReceiptTotal = invoice.TotalAmount,
                SettlementAmount = invoice.TotalAmount
            }, session);
            Assert.True(saveSettlement.Success, saveSettlement.Message);

            db.ChangeTracker.Clear();
            var histories = await rental.GetBillingHistoryRowsAsync(
                new[] { profileId },
                session,
                new DateOnly(2026, 5, 31));
            var history = Assert.Single(histories, current => current.BillingRunId == runId);
            Assert.Equal(invoice.Id, history.InvoiceId);
            Assert.True(history.InvoiceRevision.HasValue);
            var staleInvoiceRevision = history.InvoiceRevision!.Value;

            var concurrentInvoice = await db.Invoices
                .IgnoreQueryFilters()
                .SingleAsync(current => current.Id == invoice.Id);
            concurrentInvoice.Memo = "concurrent invoice edit after history load";
            concurrentInvoice.Revision = staleInvoiceRevision + 1;
            concurrentInvoice.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var delete = await rental.DeleteBillingHistoryAsync(
                profileId,
                runId,
                session,
                expectedInvoiceRevision: staleInvoiceRevision);

            Assert.False(delete.Success);
            Assert.True(delete.ConcurrencyConflict);
            Assert.Contains("판매전표", delete.Message);

            var persistedTransaction = await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == saveSettlement.EntityId);
            Assert.False(persistedTransaction.IsDeleted);
            var persistedPayment = await db.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == saveSettlement.EntityId);
            Assert.False(persistedPayment.IsDeleted);

            var persistedInvoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == invoice.Id);
            Assert.False(persistedInvoice.IsDeleted);
            Assert.Equal(staleInvoiceRevision + 1, persistedInvoice.Revision);

            var persistedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Contains(DeserializeRuns(persistedProfile), current => current.RunId == runId);
            Assert.Equal(invoice.TotalAmount, persistedProfile.SettledAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, persistedProfile.CompletionStatus);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteBillingHistory_WhenInvoiceDeleteFails_RollsBackLinkedSettlementDeletion()
    {
        PrepareAppRoot("georaeplan-rental-delete-history-rollback-on-invoice-failure");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var unrelatedCustomerId = Guid.NewGuid();
            var customerName = "Rental delete rollback customer";
            db.Customers.AddRange(
                CreateCustomer(customerId, customerName),
                CreateCustomer(unrelatedCustomerId, "Unrelated pending edit customer"));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var started = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(started.Success, started.Message);

            var invoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == started.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);

            var saveSettlement = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TransactionDate = new DateOnly(2026, 5, 27),
                TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
                LinkedInvoiceId = invoice.Id,
                BankReceipt = invoice.TotalAmount,
                ReceiptTotal = invoice.TotalAmount,
                SettlementAmount = invoice.TotalAmount
            }, session);
            Assert.True(saveSettlement.Success, saveSettlement.Message);
            db.ChangeTracker.Clear();

            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER fail_invoice_delete_for_rollback_test
                BEFORE UPDATE OF IsDeleted ON Invoices
                WHEN NEW.Id = OLD.Id AND NEW.IsDeleted = 1 AND OLD.IsDeleted = 0
                BEGIN
                    SELECT RAISE(ABORT, 'forced invoice delete failure');
                END;
                """);

            var unrelatedCustomer = await db.Customers
                .SingleAsync(current => current.Id == unrelatedCustomerId);
            unrelatedCustomer.NameOriginal = "Unrelated pending edit preserved";
            unrelatedCustomer.NameMatchKey = "UNRELATED PENDING EDIT PRESERVED";

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                rental.DeleteBillingHistoryAsync(profileId, runId, session));

            Assert.Equal(EntityState.Modified, db.Entry(unrelatedCustomer).State);
            Assert.Equal("Unrelated pending edit preserved", unrelatedCustomer.NameOriginal);
            Assert.DoesNotContain(
                db.ChangeTracker.Entries<LocalTransaction>(),
                entry => entry.Entity.Id == saveSettlement.EntityId);
            Assert.DoesNotContain(
                db.ChangeTracker.Entries<LocalPayment>(),
                entry => entry.Entity.Id == saveSettlement.EntityId);
            Assert.DoesNotContain(
                db.ChangeTracker.Entries<LocalInvoice>(),
                entry => entry.Entity.Id == invoice.Id);
            var trackedProfile = Assert.Single(
                db.ChangeTracker.Entries<LocalRentalBillingProfile>(),
                entry => entry.Entity.Id == profileId);
            Assert.Equal(EntityState.Unchanged, trackedProfile.State);
            Assert.Contains(DeserializeRuns(trackedProfile.Entity), current => current.RunId == runId);

            await db.SaveChangesAsync();
            Assert.Equal(EntityState.Unchanged, db.Entry(unrelatedCustomer).State);
            var persistedUnrelatedCustomer = await db.Customers.AsNoTracking()
                .SingleAsync(current => current.Id == unrelatedCustomerId);
            Assert.Equal("Unrelated pending edit preserved", persistedUnrelatedCustomer.NameOriginal);

            var persistedTransaction = await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == saveSettlement.EntityId);
            Assert.False(persistedTransaction.IsDeleted);

            var persistedPayment = await db.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == saveSettlement.EntityId);
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
            Assert.Equal(invoice.TotalAmount, persistedProfile.SettledAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, persistedProfile.CompletionStatus);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteBillingHistory_SuccessDoesNotCommitUnrelatedPendingCustomerEdit()
    {
        PrepareAppRoot("georaeplan-rental-delete-history-preserves-success-pending-edit");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var unrelatedCustomerId = Guid.NewGuid();
            var pendingAddedCustomerId = Guid.NewGuid();
            var pendingDeletedCustomerId = Guid.NewGuid();
            var unrelatedInvoiceId = Guid.NewGuid();
            var unrelatedInvoiceLineId = Guid.NewGuid();
            const string originalUnrelatedName = "Unrelated success pending customer";
            const string pendingUnrelatedName = "Unrelated success pending customer edited";
            db.Customers.AddRange(
                CreateCustomer(customerId, "Rental delete success customer"),
                CreateCustomer(unrelatedCustomerId, originalUnrelatedName),
                CreateCustomer(pendingDeletedCustomerId, "Unrelated success pending delete customer"));
            var unrelatedInvoice = CreateInvoice(
                unrelatedInvoiceId,
                unrelatedCustomerId,
                OfficeCodeCatalog.Usenet,
                "UNRELATED-PENDING-001",
                unrelatedInvoiceId,
                1,
                isDeleted: false,
                isLatestVersion: true);
            unrelatedInvoice.IsConfirmed = true;
            unrelatedInvoice.Lines.Add(new LocalInvoiceLine
            {
                Id = unrelatedInvoiceLineId,
                InvoiceId = unrelatedInvoiceId,
                ItemNameOriginal = "Unrelated pending invoice line",
                Quantity = 1m,
                UnitPrice = 1_000m,
                LineAmount = 1_000m
            });
            db.Invoices.Add(unrelatedInvoice);
            var profile = CreateBillingProfile(profileId, assetId, "Rental delete success customer");
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, profile.CustomerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var started = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(started.Success, started.Message);
            var invoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == started.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);

            db.ChangeTracker.Clear();
            var unrelatedCustomer = await db.Customers.SingleAsync(current => current.Id == unrelatedCustomerId);
            unrelatedCustomer.NameOriginal = pendingUnrelatedName;
            unrelatedCustomer.NameMatchKey = pendingUnrelatedName.ToUpperInvariant();
            var pendingAddedCustomer = CreateCustomer(
                pendingAddedCustomerId,
                "Unrelated success pending added customer");
            db.Customers.Add(pendingAddedCustomer);
            var pendingDeletedCustomer = await db.Customers
                .SingleAsync(current => current.Id == pendingDeletedCustomerId);
            db.Customers.Remove(pendingDeletedCustomer);
            var pendingInvoice = await db.Invoices.Include(current => current.Lines)
                .SingleAsync(current => current.Id == unrelatedInvoiceId);
            pendingInvoice.Memo = "unrelated pending invoice edit";
            var pendingInvoiceLine = Assert.Single(pendingInvoice.Lines);
            db.InvoiceLines.Remove(pendingInvoiceLine);

            var result = await rental.DeleteBillingHistoryAsync(profileId, runId, session);

            Assert.True(result.Success, result.Message);
            Assert.Equal(EntityState.Modified, db.Entry(unrelatedCustomer).State);
            Assert.Equal(pendingUnrelatedName, unrelatedCustomer.NameOriginal);
            Assert.Equal(EntityState.Added, db.Entry(pendingAddedCustomer).State);
            Assert.Equal(EntityState.Deleted, db.Entry(pendingDeletedCustomer).State);
            Assert.Equal(EntityState.Modified, db.Entry(pendingInvoice).State);
            Assert.Equal(EntityState.Deleted, db.Entry(pendingInvoiceLine).State);
            await using (var verificationDb = new LocalDbContext())
            {
                var persistedBeforeExplicitSave = await verificationDb.Customers.AsNoTracking()
                    .SingleAsync(current => current.Id == unrelatedCustomerId);
                Assert.Equal(originalUnrelatedName, persistedBeforeExplicitSave.NameOriginal);
                Assert.False(await verificationDb.Customers.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(current => current.Id == pendingAddedCustomerId));
                Assert.True(await verificationDb.Customers.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(current => current.Id == pendingDeletedCustomerId));
                Assert.NotEqual("unrelated pending invoice edit", (await verificationDb.Invoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(current => current.Id == unrelatedInvoiceId)).Memo);
                Assert.True(await verificationDb.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(current => current.Id == unrelatedInvoiceLineId));
            }

            await db.SaveChangesAsync();
            Assert.Equal(EntityState.Unchanged, db.Entry(unrelatedCustomer).State);
            await using (var verificationDb = new LocalDbContext())
            {
                var persistedAfterExplicitSave = await verificationDb.Customers.AsNoTracking()
                    .SingleAsync(current => current.Id == unrelatedCustomerId);
                Assert.Equal(pendingUnrelatedName, persistedAfterExplicitSave.NameOriginal);
                Assert.True(await verificationDb.Customers.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(current => current.Id == pendingAddedCustomerId));
                Assert.False(await verificationDb.Customers.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(current => current.Id == pendingDeletedCustomerId));
                Assert.Equal("unrelated pending invoice edit", (await verificationDb.Invoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(current => current.Id == unrelatedInvoiceId)).Memo);
                Assert.False(await verificationDb.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(current => current.Id == unrelatedInvoiceLineId));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteBillingHistory_WithPendingTargetInvoiceChange_FailsBeforeMutationAndPreservesPendingState()
    {
        PrepareAppRoot("georaeplan-rental-delete-history-pending-target-conflict");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, "Rental pending target customer"));
            var profile = CreateBillingProfile(profileId, assetId, "Rental pending target customer");
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, profile.CustomerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var started = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(started.Success, started.Message);
            db.ChangeTracker.Clear();
            var invoice = await db.Invoices.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == started.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            invoice.Memo = "pending invoice edit";

            var result = await rental.DeleteBillingHistoryAsync(profileId, runId, session);

            Assert.False(result.Success);
            Assert.True(result.ConcurrencyConflict);
            Assert.Equal(EntityState.Modified, db.Entry(invoice).State);
            Assert.Equal("pending invoice edit", invoice.Memo);
            await using var verificationDb = new LocalDbContext();
            var persistedInvoice = await verificationDb.Invoices.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == invoice.Id);
            Assert.False(persistedInvoice.IsDeleted);
            Assert.NotEqual("pending invoice edit", persistedInvoice.Memo);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("line")]
    [InlineData("serial")]
    [InlineData("unrelated-serial")]
    public async Task DeleteBillingHistory_WithPendingTargetInvoiceAggregateChange_FailsBeforeMutation(
        string pendingKind)
    {
        PrepareAppRoot($"georaeplan-rental-delete-history-pending-target-{pendingKind}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, "Rental pending aggregate customer"));
            var profile = CreateBillingProfile(profileId, assetId, "Rental pending aggregate customer");
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, profile.CustomerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var started = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(started.Success, started.Message);
            db.ChangeTracker.Clear();
            var invoice = await db.Invoices.IgnoreQueryFilters().Include(current => current.Lines)
                .SingleAsync(current => current.Id == started.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            var line = Assert.Single(invoice.Lines);
            object pendingAggregate;
            if (pendingKind == "line")
            {
                line.Remark = "pending target line edit";
                pendingAggregate = line;
            }
            else
            {
                var serial = new LocalInvoiceLineSerial
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = pendingKind == "serial" ? invoice.Id : Guid.NewGuid(),
                    InvoiceLineId = pendingKind == "serial" ? line.Id : Guid.NewGuid(),
                    ItemId = line.ItemId,
                    SerialNumber = pendingKind == "serial"
                        ? "PENDING-TARGET-SERIAL"
                        : "PENDING-UNRELATED-SERIAL"
                };
                db.InvoiceLineSerials.Add(serial);
                pendingAggregate = serial;
            }

            var result = await rental.DeleteBillingHistoryAsync(profileId, runId, session);

            Assert.False(result.Success);
            Assert.True(result.ConcurrencyConflict, result.Message);
            Assert.Equal(
                pendingKind == "line" ? EntityState.Modified : EntityState.Added,
                db.Entry(pendingAggregate).State);
            await using var verificationDb = new LocalDbContext();
            var persistedInvoice = await verificationDb.Invoices.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == invoice.Id);
            Assert.False(persistedInvoice.IsDeleted);
            Assert.Contains(
                DeserializeRuns(await verificationDb.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(current => current.Id == profileId)),
                current => current.RunId == runId);
            if (pendingKind == "line")
            {
                Assert.NotEqual("pending target line edit", (await verificationDb.InvoiceLines.AsNoTracking()
                    .SingleAsync(current => current.Id == line.Id)).Remark);
            }
            else if (pendingKind == "serial")
            {
                Assert.False(await verificationDb.InvoiceLineSerials.AsNoTracking()
                    .AnyAsync(current => current.SerialNumber == "PENDING-TARGET-SERIAL"));
            }
            else
            {
                Assert.False(await verificationDb.InvoiceLineSerials.AsNoTracking()
                    .AnyAsync(current => current.SerialNumber == "PENDING-UNRELATED-SERIAL"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteBillingHistory_DeletesDirectInvoicePaymentAndRevertsBillingRun()
    {
        PrepareAppRoot("georaeplan-rental-delete-history-direct-payment");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Rental direct payment delete customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var started = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(started.Success, started.Message);

            var invoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == started.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            var paymentId = Guid.NewGuid();

            var savePayment = await local.SavePaymentAsync(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 5, 27),
                Amount = invoice.TotalAmount,
                Note = "direct rental payment delete success"
            }, session);
            Assert.True(savePayment.Success, savePayment.Message);
            var mirroredTransaction = await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == paymentId);
            Assert.Equal(invoice.Id, mirroredTransaction.LinkedInvoiceId);
            Assert.Equal(invoice.TotalAmount, mirroredTransaction.SettlementAmount);

            var paidProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, paidProfile.SettledAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, paidProfile.CompletionStatus);

            var deleted = await rental.DeleteBillingHistoryAsync(profileId, runId, session);
            Assert.True(deleted.Success, deleted.Message);
            Assert.Contains("입금 내역", deleted.Message);

            var deletedPayment = await db.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == paymentId);
            Assert.True(deletedPayment.IsDeleted);
            Assert.True(deletedPayment.IsDirty);

            var deletedInvoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == invoice.Id);
            Assert.True(deletedInvoice.IsDeleted);
            Assert.True(deletedInvoice.IsDirty);

            var revertedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.DoesNotContain(DeserializeRuns(revertedProfile), current => current.RunId == runId);
            Assert.Equal(0m, revertedProfile.SettledAmount);
            Assert.Equal(0m, revertedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedProfile.CompletionStatus);
            Assert.True(revertedProfile.IsDirty);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteInvoice_RemovesRentalBillingRunWhenNoFinancialEvidenceRemains()
    {
        PrepareAppRoot("georaeplan-rental-delete-invoice-removes-orphan-run");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Riel Partners rental invoice delete customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "\uCCAD\uAD6C\uB300\uC0C1"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var started = await rental.StartBillingAsync(profileId, new DateOnly(2026, 6, 25), session);
            Assert.True(started.Success, started.Message);

            var invoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == started.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);

            var beforeHistories = await rental.GetBillingHistoryRowsAsync(
                new[] { profileId },
                session,
                new DateOnly(2026, 6, 30));
            var beforeHistory = Assert.Single(beforeHistories, current => current.BillingRunId == runId);
            Assert.True(beforeHistory.HasInvoice);
            Assert.True(beforeHistory.CanDelete);

            var deleteInvoice = await local.DeleteInvoiceAsync(invoice.Id, session, expectedRevision: invoice.Revision);
            Assert.True(deleteInvoice.Success, deleteInvoice.Message);
            db.ChangeTracker.Clear();

            var deletedInvoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == invoice.Id);
            Assert.True(deletedInvoice.IsDeleted);

            var revertedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.DoesNotContain(DeserializeRuns(revertedProfile), current => current.RunId == runId);
            Assert.Equal(0m, revertedProfile.SettledAmount);
            Assert.Equal(0m, revertedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedProfile.CompletionStatus);

            var afterHistories = await rental.GetBillingHistoryRowsAsync(
                new[] { profileId },
                session,
                new DateOnly(2026, 6, 30));
            Assert.DoesNotContain(afterHistories, current => current.BillingRunId == runId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteInvoice_DuplicateRentalRunInvoicesKeepRunUntilLastInvoiceIsDeleted()
    {
        PrepareAppRoot("georaeplan-rental-delete-duplicate-run-invoices");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Duplicate rental run invoice customer";
            var runId = Guid.NewGuid();
            var firstInvoiceId = Guid.NewGuid();
            var secondInvoiceId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
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
                    Status = PaymentFlowConstants.BillingStatusInProgress,
                    BilledAmount = 200_000m,
                    SettledAmount = 0m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPending
                }
            });
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            db.Invoices.AddRange(
                CreateRentalRunInvoice(firstInvoiceId, customerId, customerName, profileId, runId, "RENT-DUP-001", 100_000m),
                CreateRentalRunInvoice(secondInvoiceId, customerId, customerName, profileId, runId, "RENT-DUP-002", 100_000m));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);

            var firstDelete = await local.DeleteInvoiceAsync(firstInvoiceId, session);
            Assert.True(firstDelete.Success, firstDelete.Message);
            db.ChangeTracker.Clear();

            var profileAfterFirstDelete = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var remainingRun = Assert.Single(DeserializeRuns(profileAfterFirstDelete), current => current.RunId == runId);
            Assert.Equal(100_000m, remainingRun.BilledAmount);
            Assert.Equal(100_000m, profileAfterFirstDelete.OutstandingAmount);
            Assert.True(await db.Invoices.IgnoreQueryFilters()
                .AnyAsync(current => current.Id == firstInvoiceId && current.IsDeleted));
            Assert.True(await db.Invoices.IgnoreQueryFilters()
                .AnyAsync(current => current.Id == secondInvoiceId && !current.IsDeleted && current.IsLatestVersion));

            var historiesAfterFirstDelete = await rental.GetBillingHistoryRowsAsync(
                new[] { profileId },
                session,
                new DateOnly(2026, 6, 30));
            var historyAfterFirstDelete = Assert.Single(historiesAfterFirstDelete, current => current.BillingRunId == runId);
            Assert.True(historyAfterFirstDelete.HasInvoice);
            Assert.True(historyAfterFirstDelete.CanDelete);
            Assert.Equal(100_000m, historyAfterFirstDelete.BilledAmount);

            var secondDelete = await local.DeleteInvoiceAsync(secondInvoiceId, session);
            Assert.True(secondDelete.Success, secondDelete.Message);
            db.ChangeTracker.Clear();

            var profileAfterSecondDelete = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.DoesNotContain(DeserializeRuns(profileAfterSecondDelete), current => current.RunId == runId);
            Assert.Equal(0m, profileAfterSecondDelete.SettledAmount);
            Assert.Equal(0m, profileAfterSecondDelete.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, profileAfterSecondDelete.CompletionStatus);

            var historiesAfterSecondDelete = await rental.GetBillingHistoryRowsAsync(
                new[] { profileId },
                session,
                new DateOnly(2026, 6, 30));
            Assert.DoesNotContain(historiesAfterSecondDelete, current => current.BillingRunId == runId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task BillingHistoryRows_AllowsDeletingPersistedRunWithoutActiveInvoiceOrSettlement()
    {
        PrepareAppRoot("georaeplan-rental-delete-orphan-history-row");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerName = "Orphan rental billing run customer";
            var runId = Guid.NewGuid();
            var profile = CreateBillingProfile(profileId, assetId, customerName);
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
                    Status = PaymentFlowConstants.BillingStatusInProgress,
                    BilledAmount = 100_000m,
                    SettledAmount = 0m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPending
                }
            });
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);

            var histories = await rental.GetBillingHistoryRowsAsync(
                new[] { profileId },
                session,
                new DateOnly(2026, 6, 30));
            var history = Assert.Single(histories, current => current.BillingRunId == runId);
            Assert.False(history.HasInvoice);
            Assert.False(history.HasSettlement);
            Assert.True(history.CanDelete);

            var delete = await rental.DeleteBillingHistoryAsync(profileId, runId, session);
            Assert.True(delete.Success, delete.Message);

            var revertedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.DoesNotContain(DeserializeRuns(revertedProfile), current => current.RunId == runId);
            Assert.Equal(0m, revertedProfile.SettledAmount);
            Assert.Equal(0m, revertedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedProfile.CompletionStatus);
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
            profile.BillingCycleMonths = 3;
            profile.BillingAnchorMonth = 7;
            profile.MonthlyAmount = 132_000m;
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
                TransactionDate = new DateOnly(2026, 7, 13),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId,
                ReceiptTotal = 396_000m,
                BankReceipt = 396_000m,
                SettlementAmount = 396_000m,
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
                new DateOnly(2026, 7, 13));

            var history = Assert.Single(histories, current => current.BillingRunId == runId);
            Assert.Equal(396_000m, history.SettledAmount);
            Assert.Equal(396_000m, history.BilledAmount);
            Assert.Equal(0m, history.OutstandingAmount);
            Assert.Equal(new DateOnly(2026, 9, 25), history.ScheduledDate);
            Assert.Equal("2026-07 ~ 2026-09", history.PeriodLabel);
            Assert.True(history.CanDelete);

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ReferenceDate = new DateOnly(2026, 7, 13),
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
    public async Task RecalculateRentalSettlements_RestoresConfiguredQuarterlyPeriodFromLinkedInvoice()
    {
        PrepareAppRoot("georaeplan-rental-local-quarterly-run-reconstruction");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var customerName = "인천보건환경연구원[삼산동농산물검사소]";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            profile.BillingDay = 25;
            profile.BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay;
            profile.BillingCycleMonths = 3;
            profile.BillingAnchorMonth = 7;
            profile.BillingRunsJson = "[]";
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            var invoice = CreateRentalRunInvoice(
                invoiceId,
                customerId,
                customerName,
                profileId,
                runId,
                "RENTAL-QUARTERLY-RECONSTRUCT-001",
                396_000m);
            invoice.InvoiceDate = new DateOnly(2026, 7, 13);
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            await local.RecalculateRentalSettlementsAsync([(profileId, runId)]);

            var updatedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var restoredRun = Assert.Single(DeserializeRuns(updatedProfile), current => current.RunId == runId);
            Assert.Equal("20260701-20260930", restoredRun.RunKey);
            Assert.Equal(new DateOnly(2026, 9, 25), restoredRun.ScheduledDate);
            Assert.Equal(new DateOnly(2026, 7, 1), restoredRun.PeriodStartDate);
            Assert.Equal(new DateOnly(2026, 9, 30), restoredRun.PeriodEndDate);
            Assert.Equal(3, restoredRun.CycleMonths);
            Assert.Equal("2026-07 ~ 2026-09", restoredRun.PeriodLabel);
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
            Assert.Equal(new DateOnly(2026, 6, 10), invoice.InvoiceDate);
            Assert.Equal(profileId, invoice.LinkedRentalBillingProfileId);
            Assert.NotNull(invoice.LinkedRentalBillingRunId);

            var persisted = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            var run = Assert.Single(DeserializeRuns(persisted));
            Assert.Equal(new DateOnly(2026, 10, 25), run.ScheduledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task StartBilling_LoadsExistingInvoiceWithoutRebuildingWhenTemplateChanges()
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
            Assert.True(second.RelatedEntityAlreadyExisted);
            Assert.Equal(first.RelatedEntityId, second.RelatedEntityId);
            Assert.Contains("불러왔습니다", second.Message, StringComparison.Ordinal);

            var invoices = await db.Invoices
                .Where(current => current.LinkedRentalBillingProfileId == profileId)
                .ToListAsync();
            var existingInvoice = Assert.Single(invoices);
            Assert.True(existingInvoice.IsLatestVersion);
            Assert.Equal(100_000m, existingInvoice.TotalAmount);

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { ReferenceDate = new DateOnly(2026, 5, 25), ExpandCustomerSummaryRows = true },
                session);
            var row = Assert.Single(rows, current => current.Source.Id == profileId);
            Assert.Equal(100_000m, row.CurrentBilledAmount);
            Assert.Equal(100_000m, row.OutstandingAmount);
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
            Assert.Equal(new DateOnly(2026, 10, 25), completedProfile.LastBilledDate);

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { ReferenceDate = new DateOnly(2026, 6, 10), ExpandCustomerSummaryRows = true },
                session);
            var row = Assert.Single(rows, current => current.Source.Id == profileId);
            Assert.Equal(new DateOnly(2026, 7, 25), row.NextBillingDate);
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
            var mirroredTransaction = await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == paymentId);
            Assert.Equal(invoice.Id, mirroredTransaction.LinkedInvoiceId);
            Assert.Equal(invoice.TotalAmount, mirroredTransaction.SettlementAmount);
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
    public async Task RegisterBillingSettlement_UsesBillingMethodReceiptBucketForDirectInput()
    {
        PrepareAppRoot($"georaeplan-rental-register-settlement-method-{Guid.NewGuid():N}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var cases = new[]
            {
                new { BillingMethod = "\uD604\uAE08", ExpectedBucket = "cash" },
                new { BillingMethod = "\uCE74\uB4DC", ExpectedBucket = "card" },
                new { BillingMethod = "CMS", ExpectedBucket = "bank" },
                new { BillingMethod = "\uC804\uC790\uC138\uAE08\uACC4\uC0B0\uC11C", ExpectedBucket = "bank" }
            };

            foreach (var currentCase in cases)
            {
                var profileId = Guid.NewGuid();
                var assetId = Guid.NewGuid();
                var customerId = Guid.NewGuid();
                var customerName = $"Billing method settlement {currentCase.ExpectedBucket} {profileId:N}";
                db.Customers.Add(CreateCustomer(customerId, customerName));
                var profile = CreateBillingProfile(profileId, assetId, customerName);
                profile.CustomerId = customerId;
                profile.BillingMethod = currentCase.BillingMethod;
                db.RentalBillingProfiles.Add(profile);
                db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "\uCCAD\uAD6C\uB300\uC0C1"));
                await db.SaveChangesAsync();

                var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
                Assert.True(start.Success, start.Message);

                var invoice = await db.Invoices.AsNoTracking().SingleAsync(row => row.Id == start.RelatedEntityId);
                var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);

                var register = await rental.RegisterBillingSettlementAsync(
                    profileId,
                    new DateOnly(2026, 5, 27),
                    invoice.TotalAmount,
                    "billing method bucket",
                    session,
                    billingRunId: runId);
                Assert.True(register.Success, register.Message);

                var transaction = Assert.Single(await db.Transactions
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(row =>
                        !row.IsDeleted &&
                        row.LinkedRentalBillingProfileId == profileId &&
                        row.LinkedRentalBillingRunId == runId)
                    .ToListAsync());

                Assert.Equal(invoice.TotalAmount, transaction.SettlementAmount);
                Assert.Equal(invoice.TotalAmount, transaction.ReceiptTotal);
                Assert.Equal(currentCase.ExpectedBucket == "cash" ? invoice.TotalAmount : 0m, transaction.CashReceipt);
                Assert.Equal(currentCase.ExpectedBucket == "card" ? invoice.TotalAmount : 0m, transaction.CardReceipt);
                Assert.Equal(currentCase.ExpectedBucket == "bank" ? invoice.TotalAmount : 0m, transaction.BankReceipt);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RegisterBillingSettlement_PartialThenCompleteCreatesDeltaEvidenceAndCompletedRun()
    {
        PrepareAppRoot("georaeplan-rental-register-settlement-partial-complete");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Partial then complete settlement customer";
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
            var firstAmount = invoice.TotalAmount / 2m;

            var firstRegister = await rental.RegisterBillingSettlementAsync(
                profileId,
                new DateOnly(2026, 5, 27),
                firstAmount,
                "partial settlement",
                session,
                billingRunId: runId);
            Assert.True(firstRegister.Success, firstRegister.Message);

            db.ChangeTracker.Clear();
            var partiallyPaidProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(firstAmount, partiallyPaidProfile.SettledAmount);
            Assert.Equal(invoice.TotalAmount - firstAmount, partiallyPaidProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, partiallyPaidProfile.CompletionStatus);
            var partiallyPaidRun = DeserializeRuns(partiallyPaidProfile).Single(current => current.RunId == runId);
            Assert.Equal(firstAmount, partiallyPaidRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusInProgress, partiallyPaidRun.Status);
            Assert.Equal(PaymentFlowConstants.SettlementStatusPartial, partiallyPaidRun.SettlementStatus);

            var completeRegister = await rental.RegisterBillingSettlementAsync(
                profileId,
                new DateOnly(2026, 5, 28),
                invoice.TotalAmount,
                "complete settlement",
                session,
                billingRunId: runId);
            Assert.True(completeRegister.Success, completeRegister.Message);

            var repeatComplete = await rental.RegisterBillingSettlementAsync(
                profileId,
                new DateOnly(2026, 5, 28),
                invoice.TotalAmount,
                "complete settlement repeat",
                session,
                billingRunId: runId);
            Assert.True(repeatComplete.Success, repeatComplete.Message);

            db.ChangeTracker.Clear();
            var settledInvoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == invoice.Id);
            var settledInvoiceDisplayNumber = string.IsNullOrWhiteSpace(settledInvoice.InvoiceNumber)
                ? settledInvoice.LocalTempNumber
                : settledInvoice.InvoiceNumber;
            Assert.False(string.IsNullOrWhiteSpace(settledInvoiceDisplayNumber));

            var transactions = await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(current =>
                    !current.IsDeleted &&
                    current.LinkedRentalBillingProfileId == profileId &&
                    current.LinkedRentalBillingRunId == runId)
                .OrderBy(current => current.TransactionDate)
                .ThenBy(current => current.Id)
                .ToListAsync();
            Assert.Equal(2, transactions.Count);
            Assert.All(transactions, transaction =>
            {
                Assert.Equal(PaymentFlowConstants.TransactionKindRentalReceipt, transaction.TransactionKind);
                Assert.Equal(invoice.Id, transaction.LinkedInvoiceId);
                Assert.Equal(settledInvoiceDisplayNumber, transaction.LinkedInvoiceNumber);
                Assert.Equal(firstAmount, transaction.SettlementAmount);
                Assert.Equal(firstAmount, transaction.ReceiptTotal);
                Assert.Equal(firstAmount, transaction.BankReceipt);
            });
            Assert.Equal(new DateOnly(2026, 5, 27), transactions[0].TransactionDate);
            Assert.Equal(new DateOnly(2026, 5, 28), transactions[1].TransactionDate);
            Assert.Equal(invoice.TotalAmount, transactions.Sum(transaction => transaction.SettlementAmount));

            var transactionIds = transactions.Select(transaction => transaction.Id).ToHashSet();
            var payments = await db.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(current => transactionIds.Contains(current.Id))
                .OrderBy(current => current.PaymentDate)
                .ThenBy(current => current.Id)
                .ToListAsync();
            Assert.Equal(2, payments.Count);
            Assert.All(payments, payment =>
            {
                Assert.Equal(invoice.Id, payment.InvoiceId);
                Assert.Equal(firstAmount, payment.Amount);
            });
            Assert.Equal(new DateOnly(2026, 5, 27), payments[0].PaymentDate);
            Assert.Equal(new DateOnly(2026, 5, 28), payments[1].PaymentDate);
            Assert.Equal(invoice.TotalAmount, payments.Sum(payment => payment.Amount));

            var completedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, completedProfile.SettledAmount);
            Assert.Equal(0m, completedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, completedProfile.CompletionStatus);
            Assert.Equal(PaymentFlowConstants.SettlementStatusConfirmed, completedProfile.SettlementStatus);
            Assert.Equal(new DateOnly(2026, 5, 28), completedProfile.LastSettledDate);
            Assert.True(completedProfile.IsDirty);

            var completedRun = DeserializeRuns(completedProfile).Single(current => current.RunId == runId);
            Assert.Equal(invoice.TotalAmount, completedRun.BilledAmount);
            Assert.Equal(invoice.TotalAmount, completedRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, completedRun.Status);
            Assert.Equal(PaymentFlowConstants.SettlementStatusConfirmed, completedRun.SettlementStatus);
            Assert.Equal(new DateOnly(2026, 5, 28), completedRun.SettledDate);

            var log = Assert.Single(await db.RentalBillingLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(current => current.BillingProfileId == profileId)
                .ToListAsync());
            Assert.False(log.IsDeleted);
            Assert.True(log.IsDirty);
            Assert.Equal(PaymentFlowConstants.SettlementStatusConfirmed, log.Status);
            Assert.Equal(invoice.TotalAmount, log.BilledAmount);

            var history = Assert.Single(await rental.GetBillingHistoryRowsAsync(
                new[] { profileId },
                session,
                new DateOnly(2026, 5, 28)));
            Assert.Equal(runId, history.BillingRunId);
            Assert.True(history.HasInvoice);
            Assert.Equal(invoice.Id, history.InvoiceId);
            Assert.Equal(invoice.TotalAmount, history.BilledAmount);
            Assert.Equal(invoice.TotalAmount, history.SettledAmount);
            Assert.Equal(0m, history.OutstandingAmount);
            Assert.Equal(new DateOnly(2026, 5, 28), history.SettledDate);
            Assert.Equal(PaymentFlowConstants.SettlementStatusConfirmed, history.SettlementStatus);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RegisterBillingSettlement_RejectsAmountAboveBillingRunOutstandingBeforeCreatingEvidence()
    {
        PrepareAppRoot("georaeplan-rental-register-settlement-over-outstanding");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Over outstanding settlement customer";
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
            var overAmount = invoice.TotalAmount + 1_000m;

            var register = await rental.RegisterBillingSettlementAsync(
                profileId,
                new DateOnly(2026, 5, 27),
                overAmount,
                "over outstanding settlement",
                session,
                billingRunId: runId);

            Assert.False(register.Success);
            Assert.Contains("현재 청구 잔액", register.Message);

            Assert.Empty(await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(current =>
                    !current.IsDeleted &&
                    current.LinkedRentalBillingProfileId == profileId &&
                    current.LinkedRentalBillingRunId == runId)
                .ToListAsync());
            Assert.Empty(await db.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(current => !current.IsDeleted && current.InvoiceId == invoice.Id)
                .ToListAsync());

            var unchangedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(0m, unchangedProfile.SettledAmount);
            Assert.Equal(invoice.TotalAmount, unchangedProfile.OutstandingAmount);
            var unchangedRun = DeserializeRuns(unchangedProfile).Single(current => current.RunId == runId);
            Assert.Equal(0m, unchangedRun.SettledAmount);
            Assert.Equal(invoice.TotalAmount, unchangedRun.BilledAmount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PaymentViewModel_RentalBillingSettlementWindow_SaveCreatesTransactionPaymentAndRunSettlement()
    {
        PrepareAppRoot("georaeplan-rental-payment-window-save-settlement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Payment window rental settlement customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            profile.BillingMethod = "CMS";
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);

            var invoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            db.ChangeTracker.Clear();

            var paymentProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var paymentViewModel = new PaymentViewModel(local, session);
            await paymentViewModel.LoadAsync();
            await paymentViewModel.ConfigureForRentalBillingAsync(
                paymentProfile,
                runId,
                invoice.TotalAmount,
                "2026-05");

            Assert.Equal(PaymentFlowConstants.TransactionKindRentalReceipt, paymentViewModel.SelectedTransactionKind);
            Assert.Equal(invoice.TotalAmount, paymentViewModel.SettlementAmount);
            Assert.Equal(invoice.TotalAmount, paymentViewModel.BankReceipt);
            Assert.Equal(invoice.TotalAmount, paymentViewModel.ReceiptTotal);

            await paymentViewModel.SaveCommand.ExecuteAsync(null);

            Assert.Contains("수금/지급을 저장했습니다.", paymentViewModel.StatusMessage);

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
            Assert.True(transaction.IsDirty);

            var payment = await db.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == transaction.Id);
            Assert.False(payment.IsDeleted);
            Assert.Equal(invoice.Id, payment.InvoiceId);
            Assert.Equal(invoice.TotalAmount, payment.Amount);
            Assert.True(payment.IsDirty);

            var updatedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(invoice.TotalAmount, updatedProfile.SettledAmount);
            Assert.Equal(0m, updatedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionDone, updatedProfile.CompletionStatus);
            Assert.True(updatedProfile.IsDirty);

            var updatedRun = DeserializeRuns(updatedProfile).Single(current => current.RunId == runId);
            Assert.Equal(invoice.TotalAmount, updatedRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, updatedRun.Status);
            Assert.Equal(PaymentFlowConstants.SettlementStatusConfirmed, updatedRun.SettlementStatus);
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
            Assert.Equal(0m, revertedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedProfile.CompletionStatus);
            Assert.True(revertedProfile.IsDirty);
            Assert.DoesNotContain(DeserializeRuns(revertedProfile), current => current.RunId == runId);
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
            Assert.Equal(0m, revertedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.CompletionPending, revertedProfile.CompletionStatus);
            Assert.False(revertedProfile.IsDirty);
            Assert.DoesNotContain(DeserializeRuns(revertedProfile), current => current.RunId == runId);
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
            var deletedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.DoesNotContain(DeserializeRuns(deletedProfile), current => current.RunId == runId);

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
    public async Task RestoreInvoice_RestoresSelectedCompositeChainAndPreservesForeignRawCollision()
    {
        PrepareAppRoot("georaeplan-invoice-group-restore-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var visibleCustomerId = Guid.NewGuid();
            var hiddenCustomerId = Guid.NewGuid();
            var visiblePreviousInvoiceId = Guid.Parse("51000000-0000-0000-0000-000000000001");
            var visibleInvoiceId = Guid.Parse("51000000-0000-0000-0000-000000000002");
            var hiddenInvoiceId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(visibleCustomerId, "Visible invoice group customer", OfficeCodeCatalog.Usenet));
            db.Customers.Add(CreateCustomer(hiddenCustomerId, "Hidden invoice group customer", OfficeCodeCatalog.Yeonsu));
            var visiblePrevious = CreateInvoice(
                visiblePreviousInvoiceId,
                visibleCustomerId,
                OfficeCodeCatalog.Usenet,
                "LOCAL-GROUP-SCOPE-RESTORE-PREVIOUS",
                versionGroupId,
                versionNumber: 2,
                isDeleted: true,
                isLatestVersion: true);
            visiblePrevious.UpdatedAtUtc = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
            var visibleCurrent = CreateInvoice(
                visibleInvoiceId,
                visibleCustomerId,
                OfficeCodeCatalog.Usenet,
                "LOCAL-GROUP-SCOPE-RESTORE",
                versionGroupId,
                versionNumber: 2,
                isDeleted: true,
                isLatestVersion: true);
            visibleCurrent.UpdatedAtUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            db.Invoices.AddRange(visiblePrevious, visibleCurrent);
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

            Assert.True(restore.Success, restore.Message);
            db.ChangeTracker.Clear();
            var invoices = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(current =>
                    current.Id == visiblePreviousInvoiceId ||
                    current.Id == visibleInvoiceId ||
                    current.Id == hiddenInvoiceId)
                .ToDictionaryAsync(current => current.Id);
            Assert.False(invoices[visiblePreviousInvoiceId].IsDeleted);
            Assert.False(invoices[visibleInvoiceId].IsDeleted);
            Assert.True(invoices[hiddenInvoiceId].IsDeleted);
            Assert.False(invoices[visiblePreviousInvoiceId].IsLatestVersion);
            Assert.True(invoices[visibleInvoiceId].IsLatestVersion);
            Assert.True(invoices[visiblePreviousInvoiceId].IsDirty);
            Assert.True(invoices[visibleInvoiceId].IsDirty);
            Assert.False(invoices[hiddenInvoiceId].IsDirty);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PermanentlyDeleteInvoice_PurgesSelectedCompositeChainAndPreservesForeignRawCollision()
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
            var visiblePreviousInvoiceId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            var visiblePaymentId = Guid.NewGuid();
            var hiddenPaymentId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(visibleCustomerId, "Visible invoice purge customer", OfficeCodeCatalog.Usenet));
            db.Customers.Add(CreateCustomer(hiddenCustomerId, "Hidden invoice purge customer", OfficeCodeCatalog.Yeonsu));
            db.Invoices.Add(CreateInvoice(
                visiblePreviousInvoiceId,
                visibleCustomerId,
                OfficeCodeCatalog.Usenet,
                "LOCAL-GROUP-SCOPE-PURGE-PREVIOUS",
                versionGroupId,
                versionNumber: 1,
                isDeleted: true,
                isLatestVersion: false));
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
            db.Payments.AddRange(
                new LocalPayment
                {
                    Id = visiblePaymentId,
                    InvoiceId = visibleInvoiceId,
                    PaymentDate = new DateOnly(2026, 7, 30),
                    Amount = 1000m,
                    IsDeleted = true
                },
                new LocalPayment
                {
                    Id = hiddenPaymentId,
                    InvoiceId = hiddenInvoiceId,
                    PaymentDate = new DateOnly(2026, 7, 30),
                    Amount = 2000m,
                    IsDeleted = true
                });
            await db.SaveChangesAsync();

            var session = CreateOfficeOnlySession(OfficeCodeCatalog.Usenet);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await local.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                visibleInvoiceId,
                session);

            Assert.True(purge.Success, purge.Message);
            db.ChangeTracker.Clear();
            Assert.False(await db.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == visiblePreviousInvoiceId));
            Assert.False(await db.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == visibleInvoiceId));
            Assert.True(await db.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == hiddenInvoiceId));
            Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == visiblePaymentId));
            Assert.True(await db.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == hiddenPaymentId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RecycleBinInvoiceOperations_KeepForeignRawVersionGroupCollisionUntouched()
    {
        PrepareAppRoot("georaeplan-invoice-recycle-composite-isolation");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var selectedCustomerId = Guid.NewGuid();
            var foreignCustomerId = Guid.NewGuid();
            var selectedInvoiceId = Guid.NewGuid();
            var foreignInvoiceId = Guid.NewGuid();
            var foreignPaymentId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            db.Customers.AddRange(
                CreateCustomer(selectedCustomerId, "Selected recycle customer", OfficeCodeCatalog.Usenet),
                CreateCustomer(foreignCustomerId, "Foreign recycle customer", OfficeCodeCatalog.Yeonsu));
            var selectedInvoice = CreateInvoice(
                selectedInvoiceId,
                selectedCustomerId,
                OfficeCodeCatalog.Usenet,
                "LOCAL-RECYCLE-COMPOSITE-SELECTED",
                versionGroupId,
                versionNumber: 1,
                isDeleted: true,
                isLatestVersion: true);
            selectedInvoice.IsDirty = false;
            var foreignInvoice = CreateInvoice(
                foreignInvoiceId,
                foreignCustomerId,
                OfficeCodeCatalog.Yeonsu,
                "LOCAL-RECYCLE-COMPOSITE-FOREIGN",
                versionGroupId,
                versionNumber: 99,
                isDeleted: true,
                isLatestVersion: true);
            foreignInvoice.IsDirty = true;
            db.Invoices.AddRange(selectedInvoice, foreignInvoice);
            db.Payments.Add(new LocalPayment
            {
                Id = foreignPaymentId,
                InvoiceId = foreignInvoiceId,
                PaymentDate = new DateOnly(2026, 7, 30),
                Amount = 2000m,
                IsDeleted = false,
                IsDirty = true
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var entries = await local.GetRecycleBinEntriesAsync(session);
            Assert.Equal(2, entries.Count(current => current.Kind == RecycleBinEntityKind.Invoice));

            var dependency = await local.GetRecycleBinDependencyInfoAsync(
                RecycleBinEntityKind.Invoice,
                selectedInvoiceId,
                session);
            Assert.True(dependency.CanPurge, dependency.Summary);

            var restore = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                selectedInvoiceId,
                session);
            Assert.True(restore.Success, restore.Message);

            await local.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.Invoice,
                selectedInvoiceId);
            db.ChangeTracker.Clear();
            Assert.False((await db.Invoices.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == selectedInvoiceId)).IsDirty);
            var preservedForeignInvoice = await db.Invoices.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == foreignInvoiceId);
            Assert.True(preservedForeignInvoice.IsDeleted);
            Assert.True(preservedForeignInvoice.IsDirty);
            Assert.True((await db.Payments.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == foreignPaymentId)).IsDirty);

            var deleteAgain = await local.DeleteInvoiceAsync(selectedInvoiceId, session);
            Assert.True(deleteAgain.Success, deleteAgain.Message);

            var purgeFence = await local.CaptureServerPurgeConfirmationFenceAsync(
                RecycleBinEntityKind.Invoice,
                selectedInvoiceId,
                businessDatabaseName: "USENET");
            Assert.NotNull(purgeFence);
            var acceptedRevision = await db.Invoices.IgnoreQueryFilters()
                .Where(current => current.Id == selectedInvoiceId)
                .Select(current => current.Revision)
                .SingleAsync();
            var serverPurge = await local.ApplyConfirmedServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                selectedInvoiceId,
                acceptedRevision,
                businessDatabaseName: "USENET",
                purgeFence);
            Assert.True(serverPurge.Success, serverPurge.Message);
            db.ChangeTracker.Clear();
            Assert.False(await db.Invoices.IgnoreQueryFilters()
                .AnyAsync(current => current.Id == selectedInvoiceId));
            Assert.True(await db.Invoices.IgnoreQueryFilters()
                .AnyAsync(current => current.Id == foreignInvoiceId));
            Assert.True(await db.Payments.IgnoreQueryFilters()
                .AnyAsync(current => current.Id == foreignPaymentId));
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
            var invoiceToPurge = CreateInvoice(
                invoiceId,
                customerId,
                OfficeCodeCatalog.Usenet,
                "SERVER-PURGE-INV-DELETED-PAYMENT",
                invoiceId,
                versionNumber: 1,
                isDeleted: true,
                isLatestVersion: true);
            invoiceToPurge.IsDirty = false;
            db.Invoices.Add(invoiceToPurge);
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 19),
                Amount = 1000m,
                IsDeleted = true,
                IsDirty = false
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
            var savedTransaction = await db.Transactions
                .IgnoreQueryFilters()
                .SingleAsync(current =>
                    current.Id == transactionId);
            savedTransaction.IsDirty = false;
            var savedPayment = await db.Payments
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(current =>
                    current.Id == transactionId);
            if (savedPayment is not null)
                savedPayment.IsDirty = false;
            var savedProfile = await db
                .RentalBillingProfiles
                .IgnoreQueryFilters()
                .SingleAsync(current =>
                    current.Id == profileId);
            savedProfile.IsDirty = false;
            await db.SaveChangesAsync();

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
            var trackedTransaction = await db.Transactions.IgnoreQueryFilters().SingleAsync(current => current.Id == paymentId);
            trackedTransaction.IsDirty = false;
            trackedTransaction.Revision = 900;
            db.SyncOutboxEntries.RemoveRange(db.SyncOutboxEntries);
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
    public async Task SyncPull_PastCompletedRunAndLatestUnpaidRun_LeavesProfileSummaryOnLatestAuthoritativeRun()
    {
        PrepareAppRoot("georaeplan-rental-pull-latest-summary");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            const string customerName = "Pulled latest rental summary customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);

            var pastStart = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(pastStart.Success, pastStart.Message);
            var pastInvoice = await db.Invoices.AsNoTracking()
                .SingleAsync(current => current.Id == pastStart.RelatedEntityId);
            var pastRunId = Assert.IsType<Guid>(pastInvoice.LinkedRentalBillingRunId);
            var pastTransactionId = Guid.NewGuid();
            var pastReceipt = await local.SaveTransactionAsync(new LocalTransaction
            {
                Id = pastTransactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 5, 27),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedInvoiceId = pastInvoice.Id,
                LinkedInvoiceNumber = pastInvoice.InvoiceNumber,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = pastRunId,
                BankReceipt = pastInvoice.TotalAmount,
                ReceiptTotal = pastInvoice.TotalAmount,
                SettlementAmount = pastInvoice.TotalAmount,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            }, session);
            Assert.True(pastReceipt.Success, pastReceipt.Message);

            var latestStart = await rental.StartBillingAsync(profileId, new DateOnly(2026, 6, 25), session);
            Assert.True(latestStart.Success, latestStart.Message);
            var latestInvoice = await db.Invoices.AsNoTracking()
                .SingleAsync(current => current.Id == latestStart.RelatedEntityId);
            var latestRunId = Assert.IsType<Guid>(latestInvoice.LinkedRentalBillingRunId);

            var now = DateTime.UtcNow;
            var trackedProfile = await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId);
            trackedProfile.IsDirty = false;
            trackedProfile.Revision = 910;
            trackedProfile.UpdatedAtUtc = now;
            foreach (var invoice in await db.Invoices.IgnoreQueryFilters().ToListAsync())
            {
                invoice.IsDirty = false;
                invoice.Revision = 910;
                invoice.UpdatedAtUtc = now;
            }
            var trackedTransaction = await db.Transactions.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == pastTransactionId);
            trackedTransaction.IsDirty = false;
            trackedTransaction.Revision = 910;
            trackedTransaction.UpdatedAtUtc = now;
            var trackedPayment = await db.Payments.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == pastTransactionId);
            trackedPayment.IsDirty = false;
            trackedPayment.Revision = 910;
            trackedPayment.UpdatedAtUtc = now;
            db.SyncOutboxEntries.RemoveRange(db.SyncOutboxEntries);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var authoritativeProfile = LocalMappings.ToDto(
                await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId));
            var authoritativeInvoices = (await db.Invoices.Include(current => current.Lines).AsNoTracking().ToListAsync())
                .Select(LocalMappings.ToDto)
                .ToList();
            var authoritativePastTransaction = LocalMappings.ToDto(
                await db.Transactions.AsNoTracking().SingleAsync(current => current.Id == pastTransactionId));
            var authoritativePastPayment = LocalMappings.ToDto(
                await db.Payments.AsNoTracking().SingleAsync(current => current.Id == pastTransactionId));

            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            var pull = new SyncPullResponse { CurrentServerRevision = 910 };
            pull.RentalBillingProfiles.Add(authoritativeProfile);
            pull.Invoices.AddRange(authoritativeInvoices);
            pull.Transactions.Add(authoritativePastTransaction);
            pull.Payments.Add(authoritativePastPayment);

            await InvokePrivateInstanceTaskAsync(
                sync,
                "ApplyPullAsync",
                pull,
                0L,
                CancellationToken.None,
                false);

            var persisted = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(0m, persisted.SettledAmount);
            Assert.Equal(latestInvoice.TotalAmount, persisted.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.SettlementStatusPending, persisted.SettlementStatus);
            Assert.Equal(PaymentFlowConstants.CompletionPending, persisted.CompletionStatus);
            Assert.False(persisted.IsDirty);

            var runs = DeserializeRuns(persisted);
            Assert.Contains(runs, run => run.RunId == pastRunId && run.SettledAmount == pastInvoice.TotalAmount);
            Assert.Contains(runs, run => run.RunId == latestRunId && run.SettledAmount == 0m && run.BilledAmount == latestInvoice.TotalAmount);
            Assert.Equal(2, await db.Invoices.CountAsync(current => current.LinkedRentalBillingProfileId == profileId));
            Assert.Equal(customerId, persisted.CustomerId);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, persisted.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, persisted.ResponsibleOfficeCode);
            Assert.False((await db.Transactions.AsNoTracking().SingleAsync(current => current.Id == pastTransactionId)).IsDirty);
            Assert.False((await db.Payments.AsNoTracking().SingleAsync(current => current.Id == pastTransactionId)).IsDirty);
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPull_PastTransaction_DoesNotMutateDirtyRentalProfileOrLoseItsOutbox()
    {
        PrepareAppRoot("georaeplan-rental-pull-dirty-profile-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            const string customerName = "Dirty pulled rental profile customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);

            var cleanInvoice = await db.Invoices.SingleAsync(current => current.Id == invoice.Id);
            cleanInvoice.IsDirty = false;
            cleanInvoice.Revision = 919;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var authoritativeProfile = LocalMappings.ToDto(
                await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId));
            var authoritativeInvoice = LocalMappings.ToDto(
                await db.Invoices
                    .Include(current => current.Lines)
                    .AsNoTracking()
                    .SingleAsync(current => current.Id == invoice.Id));

            var dirtyProfile = await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId);
            dirtyProfile.Notes = "LOCAL-DIRTY-EDIT-MUST-SURVIVE";
            dirtyProfile.SettledAmount = 12_345m;
            dirtyProfile.OutstandingAmount = 67_890m;
            dirtyProfile.SettlementStatus = "LOCAL-SETTLEMENT";
            dirtyProfile.CompletionStatus = "LOCAL-COMPLETION";
            dirtyProfile.IsDirty = true;
            var baselineRunsJson = dirtyProfile.BillingRunsJson;
            var outboxId = Guid.NewGuid();
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = outboxId,
                MutationId = $"dirty-rental-profile:{profileId:N}",
                DeviceId = "test-device",
                EntityName = nameof(LocalRentalBillingProfile),
                EntityId = profileId,
                ExpectedRevision = dirtyProfile.Revision,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                Status = "Prepared",
                PreparedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var transactionId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var pull = new SyncPullResponse { CurrentServerRevision = 920 };
            pull.RentalBillingProfiles.Add(authoritativeProfile);
            pull.Invoices.Add(authoritativeInvoice);
            pull.Transactions.Add(new TransactionDto
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
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Revision = 920
            });
            pull.Payments.Add(new PaymentDto
            {
                Id = transactionId,
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 5, 27),
                Amount = invoice.TotalAmount,
                Note = "pulled payment must not overwrite dirty profile",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Revision = 920
            });

            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            await InvokePrivateInstanceTaskAsync(sync, "ApplyPullAsync", pull, 0L, CancellationToken.None, false);

            var persisted = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.True(persisted.IsDirty);
            Assert.Equal("LOCAL-DIRTY-EDIT-MUST-SURVIVE", persisted.Notes);
            Assert.Equal(baselineRunsJson, persisted.BillingRunsJson);
            Assert.Equal(12_345m, persisted.SettledAmount);
            Assert.Equal(67_890m, persisted.OutstandingAmount);
            Assert.Equal("LOCAL-SETTLEMENT", persisted.SettlementStatus);
            Assert.Equal("LOCAL-COMPLETION", persisted.CompletionStatus);
            Assert.True(await db.SyncOutboxEntries.AsNoTracking().AnyAsync(entry => entry.Id == outboxId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AuthoritativeRentalProfileRefresh_IgnoresForeignScopeEvidenceWithSameProfileAndRunIds()
    {
        PrepareAppRoot("georaeplan-rental-authoritative-refresh-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var pastRunId = Guid.NewGuid();
            var foreignLatestRunId = Guid.NewGuid();
            var profile = CreateBillingProfile(profileId, assetId, "Scoped evidence customer");
            profile.CustomerId = customerId;
            profile.IsDirty = false;
            profile.BillingRunsJson = JsonSerializer.Serialize(new List<RentalBillingRunModel>
            {
                new()
                {
                    RunId = pastRunId,
                    RunKey = "2026-05",
                    ScheduledDate = new DateOnly(2026, 5, 25),
                    PeriodStartDate = new DateOnly(2026, 5, 1),
                    PeriodEndDate = new DateOnly(2026, 5, 31),
                    PeriodLabel = "2026-05",
                    Status = PaymentFlowConstants.BillingStatusOnHold,
                    BilledAmount = 100_000m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPending
                },
                new()
                {
                    RunId = foreignLatestRunId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = PaymentFlowConstants.BillingStatusInProgress,
                    BilledAmount = 200_000m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPending
                }
            });
            db.Customers.Add(CreateCustomer(customerId, "Scoped evidence customer"));
            db.RentalBillingProfiles.Add(profile);
            var localInvoice = CreateRentalRunInvoice(
                Guid.NewGuid(), customerId, profile.CustomerName, profileId, pastRunId, "LOCAL-RUN", 100_000m);
            localInvoice.InvoiceDate = new DateOnly(2026, 5, 25);
            db.Invoices.Add(localInvoice);
            var foreignInvoice = CreateRentalRunInvoice(
                Guid.NewGuid(), customerId, profile.CustomerName, profileId, foreignLatestRunId, "FOREIGN-RUN", 200_000m);
            foreignInvoice.TenantCode = TenantScopeCatalog.Itworld;
            foreignInvoice.OfficeCode = OfficeCodeCatalog.Itworld;
            foreignInvoice.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
            foreignInvoice.InvoiceDate = new DateOnly(2026, 6, 25);
            db.Invoices.Add(foreignInvoice);
            db.Payments.Add(new LocalPayment
            {
                Id = Guid.NewGuid(),
                InvoiceId = foreignInvoice.Id,
                PaymentDate = new DateOnly(2026, 6, 27),
                Amount = 200_000m,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                TransactionDate = new DateOnly(2026, 6, 27),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = foreignLatestRunId,
                SettlementAmount = 200_000m,
                ReceiptTotal = 200_000m,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var authoritativeRefreshCount = 0;
            local.TestOnlyRentalProfileAuthoritativeRefreshAsync = (_, _) =>
            {
                authoritativeRefreshCount++;
                return Task.CompletedTask;
            };
            await local.RefreshRentalProfileSummariesFromAuthoritativeRunEvidenceAsync([profileId]);

            var persisted = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(1, authoritativeRefreshCount);
            Assert.Equal(100_000m, persisted.OutstandingAmount);
            Assert.Equal(0m, persisted.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusOnHold, persisted.BillingStatus);
            Assert.Equal(PaymentFlowConstants.CompletionPending, persisted.CompletionStatus);
            var runs = DeserializeRuns(persisted);
            Assert.Contains(runs, run => run.RunId == foreignLatestRunId && run.BilledAmount == 200_000m && run.SettledAmount == 0m);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AuthoritativeRentalProfileRefresh_PreservesFuturePlannedRunWithoutFinancialEvidence()
    {
        PrepareAppRoot("georaeplan-rental-authoritative-refresh-planned-run");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var completedRunId = Guid.NewGuid();
            var futurePlannedRunId = Guid.NewGuid();
            var profile = CreateBillingProfile(profileId, assetId, "Planned run preservation customer");
            profile.CustomerId = customerId;
            profile.IsDirty = false;
            profile.BillingRunsJson = JsonSerializer.Serialize(new List<RentalBillingRunModel>
            {
                new()
                {
                    RunId = completedRunId,
                    RunKey = "2026-05",
                    ScheduledDate = new DateOnly(2026, 5, 25),
                    PeriodStartDate = new DateOnly(2026, 5, 1),
                    PeriodEndDate = new DateOnly(2026, 5, 31),
                    PeriodLabel = "2026-05",
                    Status = PaymentFlowConstants.BillingStatusCompleted,
                    BilledAmount = 100_000m,
                    SettledAmount = 100_000m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusConfirmed,
                    SettledDate = new DateOnly(2026, 5, 27)
                },
                new()
                {
                    RunId = futurePlannedRunId,
                    RunKey = "2026-12",
                    ScheduledDate = new DateOnly(2026, 12, 25),
                    PeriodStartDate = new DateOnly(2026, 12, 1),
                    PeriodEndDate = new DateOnly(2026, 12, 31),
                    PeriodLabel = "2026-12",
                    Status = PaymentFlowConstants.BillingStatusPlanned,
                    BilledAmount = 100_000m,
                    SettledAmount = 0m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPending,
                    SettledDate = null
                }
            });
            db.Customers.Add(CreateCustomer(customerId, profile.CustomerName));
            db.RentalBillingProfiles.Add(profile);
            var completedInvoice = CreateRentalRunInvoice(
                Guid.NewGuid(), customerId, profile.CustomerName, profileId, completedRunId, "COMPLETED-RUN", 100_000m);
            completedInvoice.InvoiceDate = new DateOnly(2026, 5, 25);
            db.Invoices.Add(completedInvoice);
            db.Transactions.Add(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 5, 27),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedInvoiceId = completedInvoice.Id,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = completedRunId,
                SettlementAmount = 100_000m,
                ReceiptTotal = 100_000m,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            await local.RefreshRentalProfileSummariesFromAuthoritativeRunEvidenceAsync([profileId]);

            var persisted = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            var runs = DeserializeRuns(persisted);
            var plannedRun = Assert.Single(runs, run => run.RunId == futurePlannedRunId);
            Assert.Equal(PaymentFlowConstants.BillingStatusPlanned, plannedRun.Status);
            Assert.Equal(100_000m, plannedRun.BilledAmount);
            Assert.Equal(0m, plannedRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.SettlementStatusPending, plannedRun.SettlementStatus);
            Assert.Null(plannedRun.SettledDate);
            Assert.Equal(100_000m, persisted.SettledAmount);
            Assert.Equal(0m, persisted.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusCompleted, persisted.BillingStatus);
            Assert.Equal(new DateOnly(2026, 5, 25), persisted.LastBilledDate);
            Assert.False(persisted.RequiresFollowUp);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AuthoritativeRentalProfileRefresh_ManyRunsUsesBoundedFinancialEvidenceQueries()
    {
        PrepareAppRoot("georaeplan-rental-authoritative-refresh-query-count");
        var databasePath = Path.Combine(
            Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!,
            $"query-count-{Guid.NewGuid():N}.db");
        var commandCounter = new ReaderCommandCounter();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .AddInterceptors(commandCounter)
            .Options;

        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var profile = CreateBillingProfile(profileId, assetId, "Bounded run query customer");
            profile.CustomerId = customerId;
            profile.IsDirty = false;
            var firstScheduledDate = new DateOnly(2025, 1, 25);
            var runs = Enumerable.Range(0, 24)
                .Select(index => new RentalBillingRunModel
                {
                    RunId = Guid.NewGuid(),
                    RunKey = firstScheduledDate.AddMonths(index).ToString("yyyy-MM"),
                    ScheduledDate = firstScheduledDate.AddMonths(index),
                    PeriodStartDate = new DateOnly(
                        firstScheduledDate.AddMonths(index).Year,
                        firstScheduledDate.AddMonths(index).Month,
                        1),
                    PeriodEndDate = new DateOnly(
                        firstScheduledDate.AddMonths(index).Year,
                        firstScheduledDate.AddMonths(index).Month,
                        1).AddMonths(1).AddDays(-1),
                    PeriodLabel = firstScheduledDate.AddMonths(index).ToString("yyyy-MM"),
                    Status = PaymentFlowConstants.BillingStatusInProgress,
                    BilledAmount = 100_000m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPending
                })
                .ToList();
            profile.BillingRunsJson = JsonSerializer.Serialize(runs);
            db.Customers.Add(CreateCustomer(customerId, profile.CustomerName));
            db.RentalBillingProfiles.Add(profile);
            foreach (var run in runs)
            {
                var invoice = CreateRentalRunInvoice(
                    Guid.NewGuid(), customerId, profile.CustomerName, profileId, run.RunId, $"RUN-{run.RunKey}", 100_000m);
                invoice.InvoiceDate = run.ScheduledDate;
                db.Invoices.Add(invoice);
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            commandCounter.Reset();
            await local.RefreshRentalProfileSummariesFromAuthoritativeRunEvidenceAsync([profileId]);

            Assert.Equal(9, commandCounter.ReaderCount);
            var persisted = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(100_000m, persisted.OutstandingAmount);
            Assert.Equal(runs[^1].ScheduledDate, persisted.LastBilledDate);
            Assert.All(DeserializeRuns(persisted), run =>
            {
                Assert.Equal(100_000m, run.BilledAmount);
                Assert.Equal(0m, run.SettledAmount);
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AuthoritativeRentalProfileRefresh_OverChunkBoundaryAggregatesEveryRunWithBoundedQueries()
    {
        PrepareAppRoot("georaeplan-rental-authoritative-refresh-chunk-boundary");
        var databasePath = Path.Combine(
            Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!,
            $"chunk-boundary-{Guid.NewGuid():N}.db");
        var commandCounter = new ReaderCommandCounter();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .AddInterceptors(commandCounter)
            .Options;

        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var profile = CreateBillingProfile(profileId, assetId, "Chunk boundary customer");
            profile.CustomerId = customerId;
            profile.IsDirty = false;
            var firstScheduledDate = new DateOnly(2020, 1, 25);
            var runs = Enumerable.Range(0, 501)
                .Select(index => new RentalBillingRunModel
                {
                    RunId = Guid.NewGuid(),
                    RunKey = firstScheduledDate.AddMonths(index).ToString("yyyy-MM"),
                    ScheduledDate = firstScheduledDate.AddMonths(index),
                    PeriodStartDate = new DateOnly(
                        firstScheduledDate.AddMonths(index).Year,
                        firstScheduledDate.AddMonths(index).Month,
                        1),
                    PeriodEndDate = new DateOnly(
                        firstScheduledDate.AddMonths(index).Year,
                        firstScheduledDate.AddMonths(index).Month,
                        1).AddMonths(1).AddDays(-1),
                    PeriodLabel = firstScheduledDate.AddMonths(index).ToString("yyyy-MM"),
                    Status = PaymentFlowConstants.BillingStatusInProgress,
                    BilledAmount = 100_000m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPending
                })
                .ToList();
            profile.BillingRunsJson = JsonSerializer.Serialize(runs);
            db.Customers.Add(CreateCustomer(customerId, profile.CustomerName));
            db.RentalBillingProfiles.Add(profile);
            foreach (var run in runs)
            {
                var invoice = CreateRentalRunInvoice(
                    Guid.NewGuid(), customerId, profile.CustomerName, profileId, run.RunId, $"RUN-{run.RunKey}", 100_000m);
                invoice.InvoiceDate = run.ScheduledDate;
                db.Invoices.Add(invoice);
            }

            db.Transactions.AddRange(
                new LocalTransaction
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = runs[0].ScheduledDate.AddDays(1),
                    TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                    LinkedRentalBillingProfileId = profileId,
                    LinkedRentalBillingRunId = runs[0].RunId,
                    SettlementAmount = 40_000m,
                    ReceiptTotal = 40_000m,
                    IsDirty = false,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                },
                new LocalTransaction
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = runs[^1].ScheduledDate.AddDays(1),
                    TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                    LinkedRentalBillingProfileId = profileId,
                    LinkedRentalBillingRunId = runs[^1].RunId,
                    SettlementAmount = 25_000m,
                    ReceiptTotal = 25_000m,
                    IsDirty = false,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            commandCounter.Reset();
            await local.RefreshRentalProfileSummariesFromAuthoritativeRunEvidenceAsync([profileId]);

            Assert.Equal(12, commandCounter.ReaderCount);
            var persisted = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            var persistedRuns = DeserializeRuns(persisted);
            Assert.Equal(501, persistedRuns.Count);
            Assert.Equal(40_000m, Assert.Single(persistedRuns, run => run.RunId == runs[0].RunId).SettledAmount);
            Assert.Equal(25_000m, Assert.Single(persistedRuns, run => run.RunId == runs[^1].RunId).SettledAmount);
            Assert.Equal(25_000m, persisted.SettledAmount);
            Assert.Equal(75_000m, persisted.OutstandingAmount);
            Assert.Equal(runs[^1].ScheduledDate, persisted.LastBilledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AuthoritativeRentalProfileRefresh_NetsNegativeAdjustmentsAndFloorsSettlementAtZero()
    {
        PrepareAppRoot("georaeplan-rental-authoritative-refresh-negative-adjustment");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var netRunId = Guid.NewGuid();
            var floorRunId = Guid.NewGuid();
            var netRunDate = new DateOnly(2026, 5, 25);
            var floorRunDate = new DateOnly(2026, 6, 25);
            var profile = CreateBillingProfile(profileId, assetId, "Negative adjustment customer");
            profile.CustomerId = customerId;
            profile.IsDirty = false;
            profile.BillingRunsJson = JsonSerializer.Serialize(new List<RentalBillingRunModel>
            {
                new()
                {
                    RunId = netRunId,
                    RunKey = "2026-05",
                    ScheduledDate = netRunDate,
                    PeriodStartDate = new DateOnly(2026, 5, 1),
                    PeriodEndDate = new DateOnly(2026, 5, 31),
                    PeriodLabel = "2026-05",
                    Status = PaymentFlowConstants.BillingStatusInProgress,
                    BilledAmount = 100_000m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPending
                },
                new()
                {
                    RunId = floorRunId,
                    RunKey = "2026-06",
                    ScheduledDate = floorRunDate,
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = PaymentFlowConstants.BillingStatusInProgress,
                    BilledAmount = 100_000m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPending
                }
            });
            db.Customers.Add(CreateCustomer(customerId, profile.CustomerName));
            db.RentalBillingProfiles.Add(profile);
            var netInvoice = CreateRentalRunInvoice(
                Guid.NewGuid(), customerId, profile.CustomerName, profileId, netRunId, "NET-RUN", 100_000m);
            netInvoice.InvoiceDate = netRunDate;
            var floorInvoice = CreateRentalRunInvoice(
                Guid.NewGuid(), customerId, profile.CustomerName, profileId, floorRunId, "FLOOR-RUN", 100_000m);
            floorInvoice.InvoiceDate = floorRunDate;
            db.Invoices.AddRange(netInvoice, floorInvoice);

            var now = DateTime.UtcNow;
            db.Transactions.AddRange(
                new LocalTransaction
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = netRunDate.AddDays(1),
                    TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                    LinkedRentalBillingProfileId = profileId,
                    LinkedRentalBillingRunId = netRunId,
                    SettlementAmount = 100_000m,
                    ReceiptTotal = 100_000m,
                    IsDirty = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                },
                new LocalTransaction
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = netRunDate.AddDays(2),
                    TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                    LinkedRentalBillingProfileId = profileId,
                    LinkedRentalBillingRunId = netRunId,
                    SettlementAmount = -40_000m,
                    ReceiptTotal = -40_000m,
                    Note = "negative settlement adjustment",
                    IsDirty = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                },
                new LocalTransaction
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = floorRunDate.AddDays(1),
                    TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                    LinkedRentalBillingProfileId = profileId,
                    LinkedRentalBillingRunId = floorRunId,
                    SettlementAmount = 50_000m,
                    ReceiptTotal = 50_000m,
                    IsDirty = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                },
                new LocalTransaction
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = floorRunDate.AddDays(2),
                    TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                    LinkedRentalBillingProfileId = profileId,
                    LinkedRentalBillingRunId = floorRunId,
                    SettlementAmount = -80_000m,
                    ReceiptTotal = -80_000m,
                    Note = "negative settlement adjustment below zero",
                    IsDirty = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            await local.RefreshRentalProfileSummariesFromAuthoritativeRunEvidenceAsync([profileId]);

            var persisted = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            var persistedRuns = DeserializeRuns(persisted);
            var netRun = Assert.Single(persistedRuns, run => run.RunId == netRunId);
            Assert.Equal(60_000m, netRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusInProgress, netRun.Status);
            Assert.Equal(netRunDate.AddDays(2), netRun.SettledDate);
            var floorRun = Assert.Single(persistedRuns, run => run.RunId == floorRunId);
            Assert.Equal(0m, floorRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.BillingStatusInProgress, floorRun.Status);
            Assert.Null(floorRun.SettledDate);
            Assert.Equal(0m, persisted.SettledAmount);
            Assert.Equal(100_000m, persisted.OutstandingAmount);
            Assert.Equal(floorRunDate, persisted.LastBilledDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPull_ManyTransactionsForOneProfile_DoesNotRecalculateSettlementPerTransaction()
    {
        PrepareAppRoot("georaeplan-rental-pull-bounded-recalculation");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            const string customerName = "Bounded pulled recalculation customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            (await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId)).IsDirty = false;
            (await db.Invoices.SingleAsync(current => current.Id == invoice.Id)).IsDirty = false;
            db.SyncOutboxEntries.RemoveRange(db.SyncOutboxEntries);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var perRunRecalculationCount = 0;
            var authoritativeRefreshCount = 0;
            local.TestOnlyRentalSettlementRecalculationAfterProfileReloadAsync = _ =>
            {
                perRunRecalculationCount++;
                return Task.CompletedTask;
            };
            local.TestOnlyRentalProfileAuthoritativeRefreshAsync = (_, _) =>
            {
                authoritativeRefreshCount++;
                return Task.CompletedTask;
            };
            var pull = new SyncPullResponse { CurrentServerRevision = 980 };
            var now = DateTime.UtcNow;
            for (var index = 0; index < 20; index++)
            {
                pull.Transactions.Add(new TransactionDto
                {
                    Id = Guid.NewGuid(),
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
                    BankReceipt = 1m,
                    ReceiptTotal = 1m,
                    SettlementAmount = 1m,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Revision = 961 + index
                });
            }

            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            await InvokePrivateInstanceTaskAsync(sync, "ApplyPullAsync", pull, 0L, CancellationToken.None, false);

            Assert.Equal(0, perRunRecalculationCount);
            Assert.Equal(1, authoritativeRefreshCount);
            Assert.Equal(20, await db.Payments.CountAsync(payment => !payment.IsDeleted && payment.InvoiceId == invoice.Id));
            Assert.False((await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId)).IsDirty);
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPull_MoreThanContainsBatchDirectPayments_AppliesWithoutParameterOverflow()
    {
        PrepareAppRoot("georaeplan-rental-pull-direct-payment-batch-boundary");
        var databasePath = Path.Combine(
            Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!,
            $"direct-payment-batch-{Guid.NewGuid():N}.db");
        var commandCounter = new ReaderCommandCounter();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .AddInterceptors(commandCounter)
            .Options;

        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            const string customerName = "Direct payment batch boundary customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(start.Success, start.Message);
            var primaryInvoice = await db.Invoices.AsNoTracking()
                .SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(primaryInvoice.LinkedRentalBillingRunId);

            var invoices = new List<LocalInvoice> { primaryInvoice };
            for (var index = 1; index < 501; index++)
            {
                var invoiceId = Guid.NewGuid();
                var invoice = CreateRentalRunInvoice(
                    invoiceId,
                    customerId,
                    customerName,
                    profileId,
                    runId,
                    $"BATCH-{index:000}",
                    1m);
                invoices.Add(invoice);
                db.Invoices.Add(invoice);
            }

            (await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId)).IsDirty = false;
            foreach (var trackedInvoice in db.ChangeTracker.Entries<LocalInvoice>())
                trackedInvoice.Entity.IsDirty = false;
            db.SyncOutboxEntries.RemoveRange(db.SyncOutboxEntries);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var pull = new SyncPullResponse { CurrentServerRevision = 2000 };
            var now = DateTime.UtcNow;
            for (var index = 0; index < invoices.Count; index++)
            {
                pull.Payments.Add(new PaymentDto
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoices[index].Id,
                    PaymentDate = new DateOnly(2026, 5, 27),
                    Amount = 1m,
                    Note = $"batch direct payment {index:000}",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Revision = 1500 + index
                });
            }

            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            commandCounter.Reset();
            await InvokePrivateInstanceTaskAsync(sync, "ApplyPullAsync", pull, 0L, CancellationToken.None, false);
            var pullSelectCount = commandCounter.SelectCount;

            Assert.True(
                pullSelectCount is >= 1 and <= 50,
                $"pull SELECT count={pullSelectCount}; {commandCounter.DescribeTopCommands()}");
            Assert.Equal(501, await db.Payments.IgnoreQueryFilters().CountAsync(current => !current.IsDeleted));
            Assert.Equal(501, await db.Transactions.IgnoreQueryFilters().CountAsync(current => !current.IsDeleted));
            Assert.All(
                await db.Payments.IgnoreQueryFilters().AsNoTracking().ToListAsync(),
                current => Assert.False(current.IsDirty));
            Assert.False((await db.RentalBillingProfiles.AsNoTracking()
                .SingleAsync(current => current.Id == profileId)).IsDirty);
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());

            commandCounter.Reset();
            await local.ApplyPulledInvoiceDeleteSideEffectsAsync(
                invoices.Select(invoice => (invoice.Id, now.AddMinutes(1), 3000L)),
                CancellationToken.None);
            var deleteSideEffectSelectCount = commandCounter.SelectCount;

            Assert.InRange(deleteSideEffectSelectCount, 1, 40);
            Assert.Equal(501, await db.Payments.IgnoreQueryFilters().CountAsync(current => current.IsDeleted));
            Assert.Equal(501, await db.Transactions.IgnoreQueryFilters().CountAsync(current =>
                !current.IsDeleted &&
                !current.LinkedInvoiceId.HasValue &&
                current.SettlementAmount == 0m));
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
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
    public async Task SyncPull_DeletedPayment_DoesNotOverwriteDirtyPaymentOrItsTransactionMirror()
    {
        PrepareAppRoot("georaeplan-rental-pull-dirty-payment-mirror-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var fixture = await CreateRentalPaymentMirrorFixtureAsync(db, "Dirty payment mirror customer");

            var localPayment = await db.Payments.SingleAsync(current => current.Id == fixture.TransactionId);
            localPayment.Note = "LOCAL-DIRTY-PAYMENT-MUST-SURVIVE";
            localPayment.IsDirty = true;
            localPayment.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
            var cleanMirror = await db.Transactions.SingleAsync(current => current.Id == fixture.TransactionId);
            cleanMirror.Note = "CLEAN-TRANSACTION-MIRROR-MUST-STAY";
            cleanMirror.IsDirty = false;
            var baselineMirrorUpdatedAtUtc = cleanMirror.UpdatedAtUtc;
            var outbox = CreatePreparedOutbox(nameof(LocalPayment), fixture.TransactionId, localPayment.Revision);
            db.SyncOutboxEntries.Add(outbox);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var now = DateTime.UtcNow;
            var authoritativeInvoice = LocalMappings.ToDto(
                await db.Invoices
                    .Include(current => current.Lines)
                    .Include(current => current.Payments)
                    .AsNoTracking()
                    .SingleAsync(current => current.Id == fixture.Invoice.Id));
            var nestedServerPayment = Assert.Single(
                authoritativeInvoice.Payments,
                current => current.Id == fixture.TransactionId);
            nestedServerPayment.Note = "SERVER-NESTED-PAYMENT-MUST-NOT-OVERWRITE";
            nestedServerPayment.UpdatedAtUtc = now;
            nestedServerPayment.Revision = 921;
            var diagnostics = new SyncDiagnosticsService(fixture.Session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, fixture.Session);
            using var sync = new SyncService(
                db,
                fixture.Local,
                fixture.Rental,
                api,
                fixture.Session,
                fixture.Dispatcher,
                diagnostics);
            await InvokePrivateInstanceTaskAsync(
                sync,
                "ApplyPullAsync",
                new SyncPullResponse
                {
                    CurrentServerRevision = 921,
                    Invoices = { authoritativeInvoice },
                    Payments =
                    {
                        new PaymentDto
                        {
                            Id = fixture.TransactionId,
                            InvoiceId = fixture.Invoice.Id,
                            PaymentDate = new DateOnly(2026, 5, 27),
                            Amount = fixture.Invoice.TotalAmount,
                            Note = "SERVER-DELETED-PAYMENT",
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

            var preservedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == fixture.TransactionId);
            Assert.True(preservedPayment.IsDirty);
            Assert.False(preservedPayment.IsDeleted);
            Assert.Equal("LOCAL-DIRTY-PAYMENT-MUST-SURVIVE", preservedPayment.Note);

            var preservedMirror = await db.Transactions.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == fixture.TransactionId);
            Assert.False(preservedMirror.IsDirty);
            Assert.False(preservedMirror.IsDeleted);
            Assert.Equal("CLEAN-TRANSACTION-MIRROR-MUST-STAY", preservedMirror.Note);
            Assert.Equal(fixture.Invoice.Id, preservedMirror.LinkedInvoiceId);
            Assert.Equal(fixture.Invoice.TotalAmount, preservedMirror.SettlementAmount);
            Assert.Equal(baselineMirrorUpdatedAtUtc, preservedMirror.UpdatedAtUtc);
            Assert.True(await db.SyncOutboxEntries.AsNoTracking().AnyAsync(entry => entry.Id == outbox.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPull_DeletedPayment_DoesNotOverwriteDirtyTransactionMirror()
    {
        PrepareAppRoot("georaeplan-rental-pull-dirty-transaction-mirror-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var fixture = await CreateRentalPaymentMirrorFixtureAsync(db, "Dirty transaction mirror customer");

            var dirtyMirror = await db.Transactions.SingleAsync(current => current.Id == fixture.TransactionId);
            dirtyMirror.Note = "LOCAL-DIRTY-TRANSACTION-MUST-SURVIVE";
            dirtyMirror.IsDirty = true;
            dirtyMirror.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
            var baselineMirrorUpdatedAtUtc = dirtyMirror.UpdatedAtUtc;
            var outbox = CreatePreparedOutbox(nameof(LocalTransaction), fixture.TransactionId, dirtyMirror.Revision);
            db.SyncOutboxEntries.Add(outbox);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var now = DateTime.UtcNow;
            var diagnostics = new SyncDiagnosticsService(fixture.Session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, fixture.Session);
            using var sync = new SyncService(
                db,
                fixture.Local,
                fixture.Rental,
                api,
                fixture.Session,
                fixture.Dispatcher,
                diagnostics);
            await InvokePrivateInstanceTaskAsync(
                sync,
                "ApplyPullAsync",
                new SyncPullResponse
                {
                    CurrentServerRevision = 921,
                    Payments =
                    {
                        new PaymentDto
                        {
                            Id = fixture.TransactionId,
                            InvoiceId = fixture.Invoice.Id,
                            PaymentDate = new DateOnly(2026, 5, 27),
                            Amount = fixture.Invoice.TotalAmount,
                            Note = "SERVER-DELETED-PAYMENT",
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

            var appliedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == fixture.TransactionId);
            Assert.False(appliedPayment.IsDirty);
            Assert.True(appliedPayment.IsDeleted);
            Assert.Equal(921, appliedPayment.Revision);

            var preservedMirror = await db.Transactions.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == fixture.TransactionId);
            Assert.True(preservedMirror.IsDirty);
            Assert.False(preservedMirror.IsDeleted);
            Assert.Equal("LOCAL-DIRTY-TRANSACTION-MUST-SURVIVE", preservedMirror.Note);
            Assert.Equal(fixture.Invoice.Id, preservedMirror.LinkedInvoiceId);
            Assert.Equal(fixture.Invoice.TotalAmount, preservedMirror.SettlementAmount);
            Assert.Equal(baselineMirrorUpdatedAtUtc, preservedMirror.UpdatedAtUtc);
            Assert.True(await db.SyncOutboxEntries.AsNoTracking().AnyAsync(entry => entry.Id == outbox.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPull_DeletedTransaction_DoesNotOverwriteDirtyPaymentMirror()
    {
        PrepareAppRoot("georaeplan-rental-pull-dirty-payment-from-transaction-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var fixture = await CreateRentalPaymentMirrorFixtureAsync(db, "Dirty payment transaction side effect customer");

            var dirtyPayment = await db.Payments.SingleAsync(current => current.Id == fixture.TransactionId);
            dirtyPayment.Note = "LOCAL-DIRTY-PAYMENT-MUST-SURVIVE-TRANSACTION-PULL";
            dirtyPayment.IsDirty = true;
            dirtyPayment.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
            var baselinePaymentUpdatedAtUtc = dirtyPayment.UpdatedAtUtc;
            var outbox = CreatePreparedOutbox(nameof(LocalPayment), fixture.TransactionId, dirtyPayment.Revision);
            db.SyncOutboxEntries.Add(outbox);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var now = DateTime.UtcNow;
            var diagnostics = new SyncDiagnosticsService(fixture.Session);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, fixture.Session);
            using var sync = new SyncService(
                db,
                fixture.Local,
                fixture.Rental,
                api,
                fixture.Session,
                fixture.Dispatcher,
                diagnostics);
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
                            Id = fixture.TransactionId,
                            CustomerId = fixture.Invoice.CustomerId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                            TransactionDate = new DateOnly(2026, 5, 27),
                            TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                            LinkedInvoiceId = fixture.Invoice.Id,
                            LinkedInvoiceNumber = fixture.Invoice.InvoiceNumber,
                            LinkedRentalBillingProfileId = fixture.ProfileId,
                            LinkedRentalBillingRunId = fixture.RunId,
                            BankReceipt = fixture.Invoice.TotalAmount,
                            ReceiptTotal = fixture.Invoice.TotalAmount,
                            SettlementAmount = fixture.Invoice.TotalAmount,
                            Note = "SERVER-DELETED-TRANSACTION",
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

            var appliedTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == fixture.TransactionId);
            Assert.False(appliedTransaction.IsDirty);
            Assert.True(appliedTransaction.IsDeleted);
            Assert.Equal(921, appliedTransaction.Revision);

            var preservedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == fixture.TransactionId);
            Assert.True(preservedPayment.IsDirty);
            Assert.False(preservedPayment.IsDeleted);
            Assert.Equal("LOCAL-DIRTY-PAYMENT-MUST-SURVIVE-TRANSACTION-PULL", preservedPayment.Note);
            Assert.Equal(fixture.Invoice.Id, preservedPayment.InvoiceId);
            Assert.Equal(fixture.Invoice.TotalAmount, preservedPayment.Amount);
            Assert.Equal(baselinePaymentUpdatedAtUtc, preservedPayment.UpdatedAtUtc);
            Assert.True(await db.SyncOutboxEntries.AsNoTracking().AnyAsync(entry => entry.Id == outbox.Id));
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
    public async Task OfficeOnlyUser_DirectOutOfOfficeBillingIds_DoNotRevealOrMutateHistory()
    {
        PrepareAppRoot("georaeplan-rental-direct-cross-office-billing-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var hiddenProfileId = Guid.NewGuid();
            var hiddenAssetId = Guid.NewGuid();
            var hiddenCustomerId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            var hiddenPaymentId = Guid.NewGuid();
            var hiddenTransactionId = Guid.NewGuid();
            var hiddenLogId = Guid.NewGuid();
            var hiddenRunId = Guid.NewGuid();
            var hiddenCustomerName = "Hidden Yeonsu direct billing customer";
            var now = DateTime.UtcNow;
            var hiddenTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, OfficeCodeCatalog.Yeonsu);

            db.Customers.Add(CreateCustomer(hiddenCustomerId, hiddenCustomerName, OfficeCodeCatalog.Yeonsu));

            var profile = CreateBillingProfile(hiddenProfileId, hiddenAssetId, hiddenCustomerName);
            profile.TenantCode = hiddenTenant;
            profile.OfficeCode = OfficeCodeCatalog.Yeonsu;
            profile.ManagementCompanyCode = OfficeCodeCatalog.Yeonsu;
            profile.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
            profile.CustomerId = hiddenCustomerId;
            profile.BillingStatus = PaymentFlowConstants.BillingStatusInProgress;
            profile.SettlementStatus = PaymentFlowConstants.SettlementStatusPartial;
            profile.CompletionStatus = PaymentFlowConstants.CompletionPending;
            profile.SettledAmount = 25_000m;
            profile.OutstandingAmount = 75_000m;
            profile.LastSettledDate = new DateOnly(2026, 5, 27);
            profile.BillingRunsJson = JsonSerializer.Serialize(new List<RentalBillingRunModel>
            {
                new()
                {
                    RunId = hiddenRunId,
                    RunKey = "2026-05",
                    ScheduledDate = new DateOnly(2026, 5, 25),
                    PeriodStartDate = new DateOnly(2026, 5, 1),
                    PeriodEndDate = new DateOnly(2026, 5, 31),
                    PeriodLabel = "2026-05",
                    Status = PaymentFlowConstants.BillingStatusInProgress,
                    BilledAmount = 100_000m,
                    SettledAmount = 25_000m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPartial,
                    SettledDate = new DateOnly(2026, 5, 27)
                }
            });
            profile.IsDirty = false;
            db.RentalBillingProfiles.Add(profile);

            var asset = CreateRentalAsset(hiddenAssetId, hiddenCustomerName, hiddenProfileId, "\uCCAD\uAD6C\uB300\uC0C1");
            asset.TenantCode = hiddenTenant;
            asset.OfficeCode = OfficeCodeCatalog.Yeonsu;
            asset.ManagementCompanyCode = OfficeCodeCatalog.Yeonsu;
            asset.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
            asset.CustomerId = hiddenCustomerId;
            asset.IsDirty = false;
            db.RentalAssets.Add(asset);

            db.Invoices.Add(new LocalInvoice
            {
                Id = hiddenInvoiceId,
                CustomerId = hiddenCustomerId,
                TenantCode = hiddenTenant,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                SourceWarehouseCode = DomainConstants.WarehouseYeonsuMain,
                InvoiceNumber = "Y202605-9999",
                LocalTempNumber = "YL202605-9999",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 5, 25),
                TotalAmount = 100_000m,
                SupplyAmount = 90_909m,
                VatAmount = 9_091m,
                LinkedRentalBillingProfileId = hiddenProfileId,
                LinkedRentalBillingRunId = hiddenRunId,
                VersionGroupId = hiddenInvoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDirty = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastSavedAtUtc = now,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        Id = Guid.NewGuid(),
                        InvoiceId = hiddenInvoiceId,
                        ItemNameOriginal = "Hidden rental line",
                        Unit = "EA",
                        Quantity = 1m,
                        UnitPrice = 100_000m,
                        LineAmount = 100_000m,
                        OrderIndex = 0
                    }
                }
            });
            db.Payments.Add(new LocalPayment
            {
                Id = hiddenPaymentId,
                InvoiceId = hiddenInvoiceId,
                PaymentDate = new DateOnly(2026, 5, 27),
                Amount = 25_000m,
                Note = "hidden cross-office payment",
                IsDirty = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = hiddenTransactionId,
                CustomerId = hiddenCustomerId,
                TenantCode = hiddenTenant,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                TransactionDate = new DateOnly(2026, 5, 27),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedInvoiceId = hiddenInvoiceId,
                LinkedInvoiceNumber = "Y202605-9999",
                LinkedRentalBillingProfileId = hiddenProfileId,
                LinkedRentalBillingRunId = hiddenRunId,
                BankReceipt = 25_000m,
                ReceiptTotal = 25_000m,
                SettlementAmount = 25_000m,
                Note = "hidden cross-office transaction",
                IsDirty = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            db.RentalBillingLogs.Add(new LocalRentalBillingLog
            {
                Id = hiddenLogId,
                BillingProfileId = hiddenProfileId,
                TenantCode = hiddenTenant,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                BillingYearMonth = "2026-05",
                ScheduledDate = new DateOnly(2026, 5, 25),
                ProcessedDate = new DateOnly(2026, 5, 27),
                ProcessedByUsername = "hidden-yeonsu-user",
                Status = PaymentFlowConstants.SettlementStatusPartial,
                BilledAmount = 100_000m,
                Note = "hidden cross-office log",
                IsDirty = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateOfficeOnlySession(
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.RentalProfileEdit,
                AppPermissionNames.InvoiceEdit,
                AppPermissionNames.PaymentEdit);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);

            var directHistory = await rental.GetBillingHistoryRowsAsync(
                new[] { hiddenProfileId },
                session,
                new DateOnly(2026, 5, 28));
            Assert.Empty(directHistory);

            var start = await rental.StartBillingAsync(hiddenProfileId, new DateOnly(2026, 5, 25), session);
            Assert.False(start.Success);
            Assert.Contains("권한", start.Message);

            var register = await rental.RegisterBillingSettlementAsync(
                hiddenProfileId,
                new DateOnly(2026, 5, 28),
                50_000m,
                "cross-office direct register attempt",
                session,
                billingRunId: hiddenRunId);
            Assert.False(register.Success);
            Assert.Contains("권한", register.Message);

            var delete = await rental.DeleteBillingHistoryAsync(hiddenProfileId, hiddenRunId, session);
            Assert.False(delete.Success);
            Assert.Contains("권한", delete.Message);

            db.ChangeTracker.Clear();
            var storedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == hiddenProfileId);
            Assert.False(storedProfile.IsDirty);
            Assert.False(storedProfile.IsDeleted);
            Assert.Equal(25_000m, storedProfile.SettledAmount);
            Assert.Equal(75_000m, storedProfile.OutstandingAmount);
            var storedRun = DeserializeRuns(storedProfile).Single(current => current.RunId == hiddenRunId);
            Assert.Equal(25_000m, storedRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.SettlementStatusPartial, storedRun.SettlementStatus);

            var storedInvoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == hiddenInvoiceId);
            Assert.False(storedInvoice.IsDirty);
            Assert.False(storedInvoice.IsDeleted);
            Assert.Equal(100_000m, storedInvoice.TotalAmount);

            var storedPayment = await db.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == hiddenPaymentId);
            Assert.False(storedPayment.IsDirty);
            Assert.False(storedPayment.IsDeleted);
            Assert.Equal(25_000m, storedPayment.Amount);

            var transactions = await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(current => current.LinkedRentalBillingProfileId == hiddenProfileId)
                .ToListAsync();
            var storedTransaction = Assert.Single(transactions);
            Assert.Equal(hiddenTransactionId, storedTransaction.Id);
            Assert.False(storedTransaction.IsDirty);
            Assert.False(storedTransaction.IsDeleted);
            Assert.Equal(25_000m, storedTransaction.SettlementAmount);

            var storedLog = await db.RentalBillingLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == hiddenLogId);
            Assert.False(storedLog.IsDirty);
            Assert.False(storedLog.IsDeleted);
            Assert.Equal(PaymentFlowConstants.SettlementStatusPartial, storedLog.Status);

            Assert.Empty(await local.GetDirtyTransactionsForSyncAsync(session));
            Assert.Empty(await local.GetDirtyPaymentsForSyncAsync(session));
            Assert.Empty(await local.GetDirtyRentalBillingLogsForSyncAsync(session));
            var visibleInvoices = await local.GetInvoicesAsync(null, null, null, session);
            Assert.DoesNotContain(visibleInvoices, invoice => invoice.Id == hiddenInvoiceId);
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
            var trackedTransaction = await db.Transactions.SingleAsync(current => current.Id == paymentId);
            trackedTransaction.IsDirty = false;
            trackedTransaction.Revision = 900;
            db.SyncOutboxEntries.RemoveRange(db.SyncOutboxEntries);
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

    [Fact]
    public async Task RegisterBillingSettlement_ItworldLogKeepsProfileScopeAndIsSyncable()
    {
        PrepareAppRoot("georaeplan-rental-register-settlement-itworld-log-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "ITWORLD settlement syncable log customer";
            db.Customers.Add(CreateCustomer(customerId, customerName, OfficeCodeCatalog.Itworld));

            var profile = CreateBillingProfile(profileId, assetId, customerName);
            profile.CustomerId = customerId;
            profile.TenantCode = TenantScopeCatalog.Itworld;
            profile.OfficeCode = OfficeCodeCatalog.Itworld;
            profile.ManagementCompanyCode = OfficeCodeCatalog.Itworld;
            profile.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
            db.RentalBillingProfiles.Add(profile);

            var asset = CreateRentalAsset(assetId, customerName, profileId, "청구대상");
            asset.TenantCode = TenantScopeCatalog.Itworld;
            asset.OfficeCode = OfficeCodeCatalog.Itworld;
            asset.ManagementCompanyCode = OfficeCodeCatalog.Itworld;
            asset.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var session = CreateOfficeOnlySession(
                OfficeCodeCatalog.Itworld,
                AppPermissionNames.RentalProfileEdit,
                AppPermissionNames.InvoiceEdit,
                AppPermissionNames.PaymentEdit);
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
                "ITWORLD settlement syncable log",
                session,
                billingRunId: runId);
            Assert.True(register.Success, register.Message);

            var log = Assert.Single(await db.RentalBillingLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(current => current.BillingProfileId == profileId)
                .ToListAsync());
            Assert.Equal(TenantScopeCatalog.Itworld, log.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, log.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, log.ResponsibleOfficeCode);
            Assert.True(log.IsDirty);

            var dirtyLogs = await local.GetDirtyRentalBillingLogsForSyncAsync(session);
            Assert.Contains(dirtyLogs, current => current.Id == log.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetDirtyRentalBillingLogsForSync_LegacyItworldLogWithDefaultTenantStillSyncable()
    {
        PrepareAppRoot("georaeplan-rental-legacy-itworld-log-sync-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var profile = CreateBillingProfile(profileId, assetId, "Legacy ITWORLD dirty log customer");
            profile.TenantCode = TenantScopeCatalog.Itworld;
            profile.OfficeCode = OfficeCodeCatalog.Itworld;
            profile.ManagementCompanyCode = OfficeCodeCatalog.Itworld;
            profile.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
            db.RentalBillingProfiles.Add(profile);

            var logId = Guid.NewGuid();
            db.RentalBillingLogs.Add(new LocalRentalBillingLog
            {
                Id = logId,
                BillingProfileId = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = string.Empty,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                BillingYearMonth = "2026-05",
                ScheduledDate = new DateOnly(2026, 5, 25),
                ProcessedDate = new DateOnly(2026, 5, 27),
                ProcessedByUsername = "legacy-user",
                Status = PaymentFlowConstants.SettlementStatusConfirmed,
                BilledAmount = 100_000m,
                IsDirty = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeOnlySession(OfficeCodeCatalog.Itworld, AppPermissionNames.RentalProfileEdit);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var dirtyLogs = await local.GetDirtyRentalBillingLogsForSyncAsync(session);
            Assert.Contains(dirtyLogs, current => current.Id == logId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestorePaymentFreeRentalInvoice_RecalculatesBillingRunAndOutstandingAmount()
    {
        PrepareAppRoot("georaeplan-rental-restore-payment-free-invoice");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Restore payment-free rental invoice customer";
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

            var invoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == start.RelatedEntityId);
            var runId = Assert.IsType<Guid>(invoice.LinkedRentalBillingRunId);
            Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync(current => current.InvoiceId == invoice.Id));

            var delete = await local.DeleteInvoiceAsync(invoice.Id, session);
            Assert.True(delete.Success, delete.Message);
            var revertedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.DoesNotContain(DeserializeRuns(revertedProfile), current => current.RunId == runId);

            var restore = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                invoice.Id,
                session);

            Assert.True(restore.Success, restore.Message);
            var restoredProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var restoredRun = Assert.Single(
                DeserializeRuns(restoredProfile),
                current => current.RunId == runId);
            Assert.Equal(invoice.TotalAmount, restoredRun.BilledAmount);
            Assert.Equal(invoice.TotalAmount, restoredProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.SettlementStatusPending, restoredProfile.SettlementStatus);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static async Task<(
        SessionState Session,
        SyncRequestDispatcher Dispatcher,
        LocalStateService Local,
        RentalStateService Rental,
        LocalInvoice Invoice,
        Guid ProfileId,
        Guid RunId,
        Guid TransactionId)> CreateRentalPaymentMirrorFixtureAsync(
        LocalDbContext db,
        string customerName)
    {
        var profileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        db.Customers.Add(CreateCustomer(customerId, customerName));
        var profile = CreateBillingProfile(profileId, assetId, customerName);
        profile.CustomerId = customerId;
        db.RentalBillingProfiles.Add(profile);
        db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId, "청구대상"));
        await db.SaveChangesAsync();

        var session = CreateAdminSession();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rental = new RentalStateService(db, local);
        var start = await rental.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
        Assert.True(start.Success, start.Message);
        var invoice = await db.Invoices.AsNoTracking()
            .SingleAsync(current => current.Id == start.RelatedEntityId);
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
            Note = "baseline rental payment mirror",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        }, session);
        Assert.True(save.Success, save.Message);

        (await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId)).IsDirty = false;
        (await db.Invoices.SingleAsync(current => current.Id == invoice.Id)).IsDirty = false;
        var transaction = await db.Transactions.SingleAsync(current => current.Id == transactionId);
        transaction.IsDirty = false;
        transaction.Revision = 920;
        var payment = await db.Payments.SingleAsync(current => current.Id == transactionId);
        payment.IsDirty = false;
        payment.Revision = 920;
        db.SyncOutboxEntries.RemoveRange(db.SyncOutboxEntries);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return (session, dispatcher, local, rental, invoice, profileId, runId, transactionId);
    }

    private static LocalSyncOutboxEntry CreatePreparedOutbox(
        string entityName,
        Guid entityId,
        long expectedRevision)
    {
        return new LocalSyncOutboxEntry
        {
            Id = Guid.NewGuid(),
            MutationId = $"dirty-mirror:{entityName}:{entityId:N}",
            DeviceId = "test-device",
            EntityName = entityName,
            EntityId = entityId,
            ExpectedRevision = expectedRevision,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            Status = "Prepared",
            PreparedAtUtc = DateTime.UtcNow
        };
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

    private static LocalInvoice CreateRentalRunInvoice(
        Guid invoiceId,
        Guid customerId,
        string customerName,
        Guid profileId,
        Guid runId,
        string invoiceNumber,
        decimal amount)
        => new()
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = invoiceNumber,
            LocalTempNumber = invoiceNumber,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 25),
            TotalAmount = amount,
            SupplyAmount = amount,
            VatAmount = 0m,
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            IsConfirmed = true,
            IsDeleted = false,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            IsDirty = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            LastSavedAtUtc = DateTime.UtcNow,
            Lines =
            {
                new LocalInvoiceLine
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "사무기기 렌탈대금[6월]",
                    SpecificationOriginal = "복합기",
                    Unit = "대",
                    Quantity = 1m,
                    UnitPrice = amount,
                    LineAmount = amount,
                    OrderIndex = 1
                }
            }
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

    private sealed class ReaderCommandCounter : DbCommandInterceptor
    {
        private int _readerCount;
        private int _selectCount;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _commandShapes = new();

        public int ReaderCount => Volatile.Read(ref _readerCount);
        public int SelectCount => Volatile.Read(ref _selectCount);

        public void Reset()
        {
            Interlocked.Exchange(ref _readerCount, 0);
            Interlocked.Exchange(ref _selectCount, 0);
            _commandShapes.Clear();
        }

        public string DescribeTopCommands()
            => string.Join(
                " | ",
                _commandShapes
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(8)
                    .Select(pair => $"{pair.Value}x {pair.Key}"));

        private void Record(DbCommand command)
        {
            Interlocked.Increment(ref _readerCount);
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _selectCount);
            }
            var normalized = string.Join(
                " ",
                command.CommandText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (normalized.Length > 180)
                normalized = normalized[..180];
            _commandShapes.AddOrUpdate(normalized, 1, (_, count) => count + 1);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
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
        => CreateOfficeOnlySession(officeCode, Array.Empty<string>());

    private static SessionState CreateOfficeOnlySession(string officeCode, params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = $"user-{officeCode}",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }
}
