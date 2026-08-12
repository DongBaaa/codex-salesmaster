using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingRunTombstoneTests
{
    [Fact]
    public async Task DeletePlannedRun_PersistsDurableTombstoneWithoutFinancialSideEffectsOrResurrection()
    {
        PrepareAppRoot("georaeplan-rental-run-tombstone-delete");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), isTombstoned: false);
            var profile = CreateProfile(profileId, assetId, customerId, run);
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalAssets.Add(CreateAsset(assetId, profileId));
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var service = new RentalStateService(db);
            var beforeOutboxCount = await db.SyncOutboxEntries.CountAsync();

            var deleted = await service.DeleteBillingHistoryAsync(profileId, run.RunId, session);

            Assert.True(deleted.Success, deleted.Message);
            db.ChangeTracker.Clear();
            var storedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var storedRun = Assert.Single(DeserializeRuns(storedProfile.BillingRunsJson));
            Assert.Equal(run.RunId, storedRun.RunId);
            Assert.Equal(run.RunKey, storedRun.RunKey);
            Assert.True(storedRun.IsTombstoned);
            Assert.NotNull(storedRun.TombstonedAtUtc);
            Assert.Equal("admin", storedRun.TombstonedByUsername);
            Assert.Equal(PaymentFlowConstants.BillingStatusCancelled, storedRun.Status);
            Assert.Equal(0m, storedRun.BilledAmount);
            Assert.Equal(0m, storedRun.SettledAmount);
            Assert.Equal(PaymentFlowConstants.SettlementStatusUnpaid, storedRun.SettlementStatus);
            Assert.Null(storedRun.SettledDate);
            Assert.True(storedProfile.IsDirty);
            Assert.Equal(0m, storedProfile.SettledAmount);
            Assert.Equal(0m, storedProfile.OutstandingAmount);
            Assert.Null(storedProfile.LastBilledDate);
            Assert.Null(storedProfile.LastSettledDate);

            Assert.False(await db.Invoices.IgnoreQueryFilters().AnyAsync());
            Assert.False(await db.Transactions.IgnoreQueryFilters().AnyAsync());
            Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync());
            Assert.Equal(beforeOutboxCount, await db.SyncOutboxEntries.CountAsync());

            var reloadedService = new RentalStateService(db);
            Assert.Empty(reloadedService.GetBillingRuns(storedProfile));
            Assert.Null(reloadedService.GetOrCreateBillingRun(
                storedProfile,
                new DateOnly(2026, 5, 25),
                persistChanges: true));
            Assert.True(Assert.Single(DeserializeRuns(storedProfile.BillingRunsJson)).IsTombstoned);

            var rows = await reloadedService.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    IncludeHistoryRows = true,
                    ExpandCustomerSummaryRows = true,
                    ReferenceDate = new DateOnly(2026, 5, 25)
                },
                session);
            Assert.DoesNotContain(rows, row => row.CurrentBillingRunId == run.RunId);
            Assert.DoesNotContain(rows.SelectMany(row => row.BillingHistoryRows), row => row.BillingRunId == run.RunId);
            Assert.DoesNotContain(rows, row => row.PastUnresolvedCount > 0 || row.PastUnresolvedAmount > 0m);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TombstonedRun_BlocksMutationsRecalculationAndAutomaticReferenceRepair()
    {
        PrepareAppRoot("georaeplan-rental-run-tombstone-guards");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), isTombstoned: true);
            var profile = CreateProfile(profileId, assetId, customerId, run);
            profile.BillingStatus = PaymentFlowConstants.BillingStatusInProgress;
            profile.SettlementStatus = PaymentFlowConstants.SettlementStatusPending;
            profile.CompletionStatus = PaymentFlowConstants.CompletionDone;
            profile.SettledAmount = 25_000m;
            profile.OutstandingAmount = 75_000m;
            profile.LastBilledDate = new DateOnly(2026, 5, 25);
            profile.LastSettledDate = new DateOnly(2026, 5, 26);
            profile.RequiresFollowUp = true;
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalAssets.Add(CreateAsset(assetId, profileId));
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);
            var referenceDate = new DateOnly(2026, 5, 25);

            Assert.False((await service.StartBillingAsync(profileId, referenceDate, session)).Success);
            Assert.False((await service.HoldBillingAsync(profileId, referenceDate, "hold", session)).Success);
            Assert.False((await service.CancelBillingAsync(profileId, referenceDate, "cancel", session)).Success);
            Assert.False((await service.RegisterBillingSettlementAsync(
                profileId,
                referenceDate,
                0m,
                "settle",
                session,
                billingRunId: run.RunId)).Success);
            Assert.False((await service.DeleteBillingHistoryAsync(profileId, run.RunId, session)).Success);
            Assert.False((await service.MarkBillingCompletedAsync(
                profileId,
                referenceDate,
                PaymentFlowConstants.CompletionDone,
                "complete",
                session,
                billingRunId: run.RunId)).Success);

            await local.RecalculateRentalSettlementsAsync(
                new[] { (profileId, (Guid?)run.RunId) },
                CancellationToken.None,
                markDirty: true);
            await local.RecalculateRentalSettlementsAsync(
                new[] { (profileId, (Guid?)null) },
                CancellationToken.None,
                markDirty: true);

            db.ChangeTracker.Clear();
            var afterMutations = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            AssertTombstonePreserved(afterMutations.BillingRunsJson, run);
            Assert.Equal(PaymentFlowConstants.BillingStatusPlanned, afterMutations.BillingStatus);
            Assert.Equal(PaymentFlowConstants.SettlementStatusUnpaid, afterMutations.SettlementStatus);
            Assert.Equal(PaymentFlowConstants.CompletionPending, afterMutations.CompletionStatus);
            Assert.Equal(0m, afterMutations.SettledAmount);
            Assert.Equal(0m, afterMutations.OutstandingAmount);
            Assert.Null(afterMutations.LastBilledDate);
            Assert.Null(afterMutations.LastSettledDate);
            Assert.False(afterMutations.RequiresFollowUp);
            Assert.False(await db.Invoices.IgnoreQueryFilters().AnyAsync());
            Assert.False(await db.Transactions.IgnoreQueryFilters().AnyAsync());
            Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync());

            var invoiceId = Guid.NewGuid();
            db.Invoices.Add(CreateInvoice(invoiceId, customerId, profileId, run.RunId));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var repair = await service.RepairBillingInvoicePeriodLinksAsync(session, referenceDate);

            Assert.False(repair.HasChanges);
            Assert.True(repair.SkippedCount > 0);
            var storedInvoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == invoiceId);
            Assert.Equal(run.RunId, storedInvoice.LinkedRentalBillingRunId);
            var afterRepair = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            AssertTombstonePreserved(afterRepair.BillingRunsJson, run);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("invoice", false)]
    [InlineData("invoice", true)]
    [InlineData("transaction", false)]
    [InlineData("transaction", true)]
    [InlineData("direct-payment", false)]
    [InlineData("direct-payment", true)]
    public async Task DeletePlannedRun_FinancialEvidenceRecheckBlocksTombstoneAndLeavesRowsUntouched(
        string evidenceKind,
        bool zeroValue)
    {
        PrepareAppRoot($"georaeplan-rental-run-tombstone-evidence-{evidenceKind}-{zeroValue}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), isTombstoned: false);
            var profile = CreateProfile(profileId, assetId, customerId, run);
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalAssets.Add(CreateAsset(assetId, profileId));
            db.RentalBillingProfiles.Add(profile);

            Guid? invoiceId = null;
            Guid? transactionId = null;
            Guid? paymentId = null;
            if (evidenceKind == "invoice")
            {
                invoiceId = Guid.NewGuid();
                var invoice = CreateInvoice(invoiceId.Value, customerId, profileId, run.RunId);
                if (zeroValue)
                    ZeroInvoiceAmounts(invoice);
                db.Invoices.Add(invoice);
            }
            else if (evidenceKind == "transaction")
            {
                transactionId = Guid.NewGuid();
                var transaction = CreateTransaction(transactionId.Value, customerId, profileId, run.RunId);
                if (zeroValue)
                    ZeroTransactionAmounts(transaction);
                db.Transactions.Add(transaction);
            }
            else
            {
                invoiceId = Guid.NewGuid();
                var zeroAmountInvoice = CreateInvoice(invoiceId.Value, customerId, profileId, run.RunId);
                ZeroInvoiceAmounts(zeroAmountInvoice);
                db.Invoices.Add(zeroAmountInvoice);

                paymentId = Guid.NewGuid();
                var payment = CreatePayment(paymentId.Value, invoiceId.Value);
                if (zeroValue)
                    payment.Amount = 0m;
                db.Payments.Add(payment);
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var originalJson = (await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId)).BillingRunsJson;

            var result = await new RentalStateService(db).DeleteBillingHistoryAsync(
                profileId,
                run.RunId,
                CreateAdminSession());

            Assert.False(result.Success);
            db.ChangeTracker.Clear();
            var storedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(originalJson, storedProfile.BillingRunsJson);
            Assert.False(Assert.Single(DeserializeRuns(storedProfile.BillingRunsJson)).IsTombstoned);

            if (invoiceId.HasValue)
            {
                var storedInvoice = await db.Invoices
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(current => current.Id == invoiceId.Value);
                Assert.False(storedInvoice.IsDeleted);
                Assert.False(storedInvoice.IsDirty);
            }

            if (transactionId.HasValue)
            {
                var storedTransaction = await db.Transactions
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(current => current.Id == transactionId.Value);
                Assert.False(storedTransaction.IsDeleted);
                Assert.False(storedTransaction.IsDirty);
            }

            if (paymentId.HasValue)
            {
                var storedPayment = await db.Payments
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(current => current.Id == paymentId.Value);
                Assert.False(storedPayment.IsDeleted);
                Assert.False(storedPayment.IsDirty);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("deleted-invoice")]
    [InlineData("old-version-invoice")]
    [InlineData("deleted-transaction")]
    [InlineData("deleted-payment")]
    public async Task DeletePlannedRun_InactiveFinancialRowsDoNotBlockTombstone(string evidenceKind)
    {
        PrepareAppRoot($"georaeplan-rental-run-inactive-evidence-{evidenceKind}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), false);
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(CreateProfile(profileId, Guid.NewGuid(), customerId, run));

            Guid? invoiceId = null;
            Guid? transactionId = null;
            Guid? paymentId = null;
            if (evidenceKind is "deleted-invoice" or "old-version-invoice" or "deleted-payment")
            {
                invoiceId = Guid.NewGuid();
                var invoice = CreateInvoice(invoiceId.Value, customerId, profileId, run.RunId);
                if (evidenceKind == "deleted-invoice")
                    invoice.IsDeleted = true;
                else
                    invoice.IsLatestVersion = false;
                db.Invoices.Add(invoice);

                if (evidenceKind == "deleted-payment")
                {
                    paymentId = Guid.NewGuid();
                    var payment = CreatePayment(paymentId.Value, invoiceId.Value);
                    payment.IsDeleted = true;
                    db.Payments.Add(payment);
                }
            }
            else
            {
                transactionId = Guid.NewGuid();
                var transaction = CreateTransaction(transactionId.Value, customerId, profileId, run.RunId);
                transaction.IsDeleted = true;
                db.Transactions.Add(transaction);
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var result = await new RentalStateService(db).DeleteBillingHistoryAsync(
                profileId,
                run.RunId,
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var marker = Assert.Single(DeserializeRuns(storedProfile.BillingRunsJson));
            Assert.True(marker.IsTombstoned);
            Assert.Equal(run.RunId, marker.RunId);
            if (invoiceId.HasValue)
            {
                var storedInvoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(current => current.Id == invoiceId.Value);
                Assert.Equal(evidenceKind == "deleted-invoice", storedInvoice.IsDeleted);
                Assert.Equal(evidenceKind == "deleted-invoice", storedInvoice.IsLatestVersion);
            }
            if (transactionId.HasValue)
            {
                var storedTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(current => current.Id == transactionId.Value);
                Assert.True(storedTransaction.IsDeleted);
            }
            if (paymentId.HasValue)
            {
                var storedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(current => current.Id == paymentId.Value);
                Assert.True(storedPayment.IsDeleted);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("transaction")]
    [InlineData("payment")]
    public async Task DeletePlannedRun_WithPendingAddedTargetEvidence_FailsClosed(string evidenceKind)
    {
        PrepareAppRoot($"georaeplan-rental-run-pending-target-evidence-{evidenceKind}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), false);
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(CreateProfile(profileId, Guid.NewGuid(), customerId, run));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            object pendingEvidence;
            if (evidenceKind == "invoice")
            {
                var invoice = CreateInvoice(Guid.NewGuid(), customerId, profileId, run.RunId);
                ZeroInvoiceAmounts(invoice);
                db.Invoices.Add(invoice);
                pendingEvidence = invoice;
            }
            else if (evidenceKind == "transaction")
            {
                var transaction = CreateTransaction(Guid.NewGuid(), customerId, profileId, run.RunId);
                ZeroTransactionAmounts(transaction);
                db.Transactions.Add(transaction);
                pendingEvidence = transaction;
            }
            else
            {
                var invoice = CreateInvoice(Guid.NewGuid(), customerId, profileId, run.RunId);
                ZeroInvoiceAmounts(invoice);
                var payment = CreatePayment(Guid.NewGuid(), invoice.Id);
                payment.Amount = 0m;
                db.Invoices.Add(invoice);
                db.Payments.Add(payment);
                pendingEvidence = payment;
            }

            var result = await new RentalStateService(db).DeleteBillingHistoryAsync(
                profileId,
                run.RunId,
                CreateAdminSession());

            Assert.False(result.Success);
            Assert.Equal(EntityState.Added, db.Entry(pendingEvidence).State);
            await using var verificationDb = new LocalDbContext();
            var storedProfile = await verificationDb.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.False(Assert.Single(DeserializeRuns(storedProfile.BillingRunsJson)).IsTombstoned);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeletePlannedRun_SuccessIsolatesUnrelatedPendingAndPreservesUnchangedEntries()
    {
        PrepareAppRoot("georaeplan-rental-run-tombstone-pending-isolation");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), false);
            var modifiedId = Guid.NewGuid();
            var addedId = Guid.NewGuid();
            var deletedId = Guid.NewGuid();
            var unchangedId = Guid.NewGuid();
            db.Customers.AddRange(
                CreateCustomer(modifiedId),
                CreateCustomer(deletedId),
                CreateCustomer(unchangedId));
            db.RentalBillingProfiles.Add(CreateProfile(profileId, Guid.NewGuid(), Guid.NewGuid(), run));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var modified = await db.Customers.SingleAsync(current => current.Id == modifiedId);
            modified.NameOriginal = "pending modified tombstone customer";
            var added = CreateCustomer(addedId);
            db.Customers.Add(added);
            var deleted = await db.Customers.SingleAsync(current => current.Id == deletedId);
            db.Customers.Remove(deleted);
            var unchanged = await db.Customers.SingleAsync(current => current.Id == unchangedId);
            var pendingOutbox = CreatePendingOutbox(Guid.NewGuid());
            db.SyncOutboxEntries.Add(pendingOutbox);

            var result = await new RentalStateService(db).DeleteBillingHistoryAsync(
                profileId,
                run.RunId,
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            Assert.Equal(EntityState.Modified, db.Entry(modified).State);
            Assert.Equal(EntityState.Added, db.Entry(added).State);
            Assert.Equal(EntityState.Deleted, db.Entry(deleted).State);
            Assert.Equal(EntityState.Unchanged, db.Entry(unchanged).State);
            Assert.Equal(EntityState.Added, db.Entry(pendingOutbox).State);
            await using (var verificationDb = new LocalDbContext())
            {
                Assert.NotEqual("pending modified tombstone customer", (await verificationDb.Customers.AsNoTracking()
                    .SingleAsync(current => current.Id == modifiedId)).NameOriginal);
                Assert.False(await verificationDb.Customers.IgnoreQueryFilters().AnyAsync(current => current.Id == addedId));
                Assert.True(await verificationDb.Customers.IgnoreQueryFilters().AnyAsync(current => current.Id == deletedId));
                Assert.Equal("Tombstone customer", (await verificationDb.Customers.AsNoTracking()
                    .SingleAsync(current => current.Id == unchangedId)).NameOriginal);
                Assert.False(await verificationDb.SyncOutboxEntries.AsNoTracking()
                    .AnyAsync(current => current.Id == pendingOutbox.Id));
            }

            unchanged.NameOriginal = "tracked unchanged after successful tombstone";
            await db.SaveChangesAsync();
            await using (var verificationDb = new LocalDbContext())
            {
                Assert.Equal("pending modified tombstone customer", (await verificationDb.Customers.AsNoTracking()
                    .SingleAsync(current => current.Id == modifiedId)).NameOriginal);
                Assert.True(await verificationDb.Customers.IgnoreQueryFilters().AnyAsync(current => current.Id == addedId));
                Assert.False(await verificationDb.Customers.IgnoreQueryFilters().AnyAsync(current => current.Id == deletedId));
                Assert.Equal("tracked unchanged after successful tombstone", (await verificationDb.Customers.AsNoTracking()
                    .SingleAsync(current => current.Id == unchangedId)).NameOriginal);
                var storedOutbox = await verificationDb.SyncOutboxEntries.AsNoTracking()
                    .SingleAsync(current => current.Id == pendingOutbox.Id);
                Assert.Equal(pendingOutbox.MutationId, storedOutbox.MutationId);
                Assert.Equal("Prepared", storedOutbox.Status);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("dispose-before")]
    public async Task DeletePlannedRun_CommitFailureBeforeProviderCommit_RollsBackAndRestoresPendingTracker(
        string rollbackFault)
    {
        PrepareAppRoot($"georaeplan-rental-run-tombstone-commit-before-{rollbackFault}");
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"rental-run-tombstone-commit-before-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            var profileId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), false);
            var modifiedId = Guid.NewGuid();
            var addedId = Guid.NewGuid();
            var deletedId = Guid.NewGuid();
            await using (var setupDb = new LocalDbContext(baseOptions))
            {
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Customers.AddRange(
                    CreateCustomer(modifiedId),
                    CreateCustomer(deletedId));
                setupDb.RentalBillingProfiles.Add(
                    CreateProfile(profileId, Guid.NewGuid(), Guid.NewGuid(), run));
                await setupDb.SaveChangesAsync();
            }

            var interceptor = new TombstoneTransactionFaultInterceptor(
                throwBeforeCommit: true,
                throwAfterCommit: false,
                throwBeforeRollback: rollbackFault == "before",
                throwAfterRollback: rollbackFault == "after");
            var failingOptions = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor)
                .Options;
            await using var db = new LocalDbContext(failingOptions);
            var modified = await db.Customers.SingleAsync(current => current.Id == modifiedId);
            modified.NameOriginal = "pending modified after failed tombstone";
            var added = CreateCustomer(addedId);
            db.Customers.Add(added);
            var deleted = await db.Customers.SingleAsync(current => current.Id == deletedId);
            db.Customers.Remove(deleted);
            var pendingOutbox = CreatePendingOutbox(Guid.NewGuid());
            db.SyncOutboxEntries.Add(pendingOutbox);

            var disposeAttemptCount = 0;
            DbTransaction? disposeTargetTransaction = null;
            var service = new RentalStateService(db);
            if (rollbackFault == "dispose-before")
            {
                service.TombstoneTransactionDisposeAsyncForTesting = transaction =>
                {
                    disposeAttemptCount++;
                    disposeTargetTransaction = transaction.GetDbTransaction();
                    return ValueTask.FromException(
                        new InvalidOperationException("simulated exception before tombstone transaction dispose"));
                };
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DeleteBillingHistoryAsync(
                    profileId,
                    run.RunId,
                    CreateAdminSession()));

            Assert.Equal("simulated tombstone transaction commit failure", exception.Message);
            Assert.Equal(1, interceptor.CommitAttemptCount);
            Assert.True(interceptor.RollbackAttemptCount >= 1);
            Assert.Equal(rollbackFault == "before" ? 0 : 1, interceptor.RollbackCompletedCount);
            Assert.Equal(rollbackFault == "dispose-before" ? 1 : 0, disposeAttemptCount);
            Assert.Null(db.Database.CurrentTransaction);
            if (rollbackFault == "dispose-before")
                Assert.Null(Assert.IsAssignableFrom<DbTransaction>(disposeTargetTransaction).Connection);
            Assert.Equal(EntityState.Modified, db.Entry(modified).State);
            Assert.Equal("pending modified after failed tombstone", modified.NameOriginal);
            Assert.Equal("Tombstone customer", db.Entry(modified).Property(current => current.NameOriginal).OriginalValue);
            Assert.True(db.Entry(modified).Property(current => current.NameOriginal).IsModified);
            Assert.Equal(EntityState.Added, db.Entry(added).State);
            Assert.Equal(EntityState.Deleted, db.Entry(deleted).State);
            Assert.Equal(EntityState.Added, db.Entry(pendingOutbox).State);

            await using var verificationDb = new LocalDbContext(baseOptions);
            var storedProfile = await verificationDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.False(Assert.Single(DeserializeRuns(storedProfile.BillingRunsJson)).IsTombstoned);
            Assert.Equal("Tombstone customer", (await verificationDb.Customers.AsNoTracking()
                .SingleAsync(current => current.Id == modifiedId)).NameOriginal);
            Assert.False(await verificationDb.Customers.IgnoreQueryFilters()
                .AnyAsync(current => current.Id == addedId));
            Assert.True(await verificationDb.Customers.IgnoreQueryFilters()
                .AnyAsync(current => current.Id == deletedId));
            Assert.False(await verificationDb.SyncOutboxEntries.AsNoTracking()
                .AnyAsync(current => current.Id == pendingOutbox.Id));

            await db.SaveChangesAsync();
            Assert.Equal(2, interceptor.CommitAttemptCount);
            await using var postSaveVerificationDb = new LocalDbContext(baseOptions);
            var postSaveProfile = await postSaveVerificationDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.False(Assert.Single(DeserializeRuns(postSaveProfile.BillingRunsJson)).IsTombstoned);
            Assert.Equal("pending modified after failed tombstone", (await postSaveVerificationDb.Customers.AsNoTracking()
                .SingleAsync(current => current.Id == modifiedId)).NameOriginal);
            Assert.True(await postSaveVerificationDb.Customers.IgnoreQueryFilters()
                .AnyAsync(current => current.Id == addedId));
            Assert.False(await postSaveVerificationDb.Customers.IgnoreQueryFilters()
                .AnyAsync(current => current.Id == deletedId));
            var storedPendingOutbox = await postSaveVerificationDb.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(current => current.Id == pendingOutbox.Id);
            Assert.Equal(pendingOutbox.MutationId, storedPendingOutbox.MutationId);
            Assert.Equal("Prepared", storedPendingOutbox.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task DeletePlannedRun_ProviderCommittedThenThrew_ReturnsCommittedSuccessAndPreservesPendingTracker()
    {
        PrepareAppRoot("georaeplan-rental-run-tombstone-commit-after");
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"rental-run-tombstone-commit-after-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            var profileId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), false);
            var modifiedId = Guid.NewGuid();
            var addedId = Guid.NewGuid();
            await using (var setupDb = new LocalDbContext(baseOptions))
            {
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Customers.Add(CreateCustomer(modifiedId));
                setupDb.RentalBillingProfiles.Add(
                    CreateProfile(profileId, Guid.NewGuid(), Guid.NewGuid(), run));
                await setupDb.SaveChangesAsync();
            }

            var interceptor = new TombstoneTransactionFaultInterceptor(
                throwBeforeCommit: false,
                throwAfterCommit: true,
                throwBeforeRollback: false);
            var ambiguousOptions = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor)
                .Options;
            await using var db = new LocalDbContext(ambiguousOptions);
            var modified = await db.Customers.SingleAsync(current => current.Id == modifiedId);
            modified.NameOriginal = "pending modified across ambiguous commit";
            var added = CreateCustomer(addedId);
            db.Customers.Add(added);
            var pendingOutbox = CreatePendingOutbox(Guid.NewGuid());
            db.SyncOutboxEntries.Add(pendingOutbox);

            var result = await new RentalStateService(db).DeleteBillingHistoryAsync(
                profileId,
                run.RunId,
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, interceptor.CommitAttemptCount);
            Assert.Equal(1, interceptor.CommitCompletedCount);
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(EntityState.Modified, db.Entry(modified).State);
            Assert.Equal("pending modified across ambiguous commit", modified.NameOriginal);
            Assert.Equal("Tombstone customer", db.Entry(modified).Property(current => current.NameOriginal).OriginalValue);
            Assert.True(db.Entry(modified).Property(current => current.NameOriginal).IsModified);
            Assert.Equal(EntityState.Added, db.Entry(added).State);
            Assert.Equal(EntityState.Added, db.Entry(pendingOutbox).State);

            await using var verificationDb = new LocalDbContext(baseOptions);
            var storedProfile = await verificationDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var storedRun = Assert.Single(DeserializeRuns(storedProfile.BillingRunsJson));
            Assert.Equal(run.RunId, storedRun.RunId);
            Assert.True(storedRun.IsTombstoned);
            Assert.Equal("Tombstone customer", (await verificationDb.Customers.AsNoTracking()
                .SingleAsync(current => current.Id == modifiedId)).NameOriginal);
            Assert.False(await verificationDb.Customers.IgnoreQueryFilters()
                .AnyAsync(current => current.Id == addedId));
            Assert.False(await verificationDb.SyncOutboxEntries.AsNoTracking()
                .AnyAsync(current => current.Id == pendingOutbox.Id));

            await db.SaveChangesAsync();
            Assert.Equal(2, interceptor.CommitAttemptCount);
            await using var postSaveVerificationDb = new LocalDbContext(baseOptions);
            var postSaveProfile = await postSaveVerificationDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.True(Assert.Single(DeserializeRuns(postSaveProfile.BillingRunsJson)).IsTombstoned);
            Assert.Equal("pending modified across ambiguous commit", (await postSaveVerificationDb.Customers.AsNoTracking()
                .SingleAsync(current => current.Id == modifiedId)).NameOriginal);
            Assert.True(await postSaveVerificationDb.Customers.IgnoreQueryFilters()
                .AnyAsync(current => current.Id == addedId));
            var storedPendingOutbox = await postSaveVerificationDb.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(current => current.Id == pendingOutbox.Id);
            Assert.Equal(pendingOutbox.MutationId, storedPendingOutbox.MutationId);
            Assert.Equal("Prepared", storedPendingOutbox.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task DeletePlannedRun_ProviderCommittedThenVerificationFailed_DoesNotRestoreStaleTargetTracker()
    {
        PrepareAppRoot("georaeplan-rental-run-tombstone-commit-after-verification-failure");
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"rental-run-tombstone-commit-after-verification-failure-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            var profileId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), false);
            await using (var setupDb = new LocalDbContext(baseOptions))
            {
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.RentalBillingProfiles.Add(
                    CreateProfile(profileId, Guid.NewGuid(), Guid.NewGuid(), run));
                await setupDb.SaveChangesAsync();
            }

            var interceptor = new TombstoneTransactionFaultInterceptor(
                throwBeforeCommit: false,
                throwAfterCommit: true,
                throwBeforeRollback: false);
            var ambiguousOptions = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor)
                .Options;
            await using var db = new LocalDbContext(ambiguousOptions);
            var originallyTrackedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);
            var pendingOutbox = CreatePendingOutbox(Guid.NewGuid());
            db.SyncOutboxEntries.Add(pendingOutbox);
            var service = new RentalStateService(db)
            {
                BeforeTombstoneCommitVerificationAsyncForTesting = () =>
                    ValueTask.FromException(
                        new InvalidOperationException("simulated tombstone commit verification failure"))
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DeleteBillingHistoryAsync(
                    profileId,
                    run.RunId,
                    CreateAdminSession()));

            Assert.Equal("simulated exception after tombstone transaction commit", exception.Message);
            Assert.Equal(1, interceptor.CommitAttemptCount);
            Assert.Equal(1, interceptor.CommitCompletedCount);
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(EntityState.Detached, db.Entry(originallyTrackedProfile).State);
            Assert.Equal(EntityState.Added, db.Entry(pendingOutbox).State);

            await using (var verificationDb = new LocalDbContext(baseOptions))
            {
                var storedProfile = await verificationDb.RentalBillingProfiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(current => current.Id == profileId);
                Assert.True(Assert.Single(DeserializeRuns(storedProfile.BillingRunsJson)).IsTombstoned);
                Assert.False(await verificationDb.SyncOutboxEntries.AsNoTracking()
                    .AnyAsync(current => current.Id == pendingOutbox.Id));
            }

            var refreshedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);
            Assert.NotSame(originallyTrackedProfile, refreshedProfile);
            Assert.True(Assert.Single(DeserializeRuns(refreshedProfile.BillingRunsJson)).IsTombstoned);

            await db.SaveChangesAsync();
            await using var postSaveVerificationDb = new LocalDbContext(baseOptions);
            var postSaveProfile = await postSaveVerificationDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.True(Assert.Single(DeserializeRuns(postSaveProfile.BillingRunsJson)).IsTombstoned);
            var storedPendingOutbox = await postSaveVerificationDb.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(current => current.Id == pendingOutbox.Id);
            Assert.Equal(pendingOutbox.MutationId, storedPendingOutbox.MutationId);
            Assert.Equal("Prepared", storedPendingOutbox.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletePlannedRun_ProviderDisposeFault_ReturnsCommittedSuccessWithoutTransactionOrOutboxLeak(
        bool throwBeforeDispose)
    {
        PrepareAppRoot("georaeplan-rental-run-tombstone-dispose-after");
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"rental-run-tombstone-dispose-after-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            var profileId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), false);
            await using (var setupDb = new LocalDbContext(options))
            {
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.RentalBillingProfiles.Add(
                    CreateProfile(profileId, Guid.NewGuid(), Guid.NewGuid(), run));
                await setupDb.SaveChangesAsync();
            }

            await using var db = new LocalDbContext(options);
            var pendingOutbox = CreatePendingOutbox(Guid.NewGuid());
            db.SyncOutboxEntries.Add(pendingOutbox);
            var disposeCount = 0;
            DbTransaction? disposeTargetTransaction = null;
            var service = new RentalStateService(db)
            {
                TombstoneTransactionDisposeAsyncForTesting = async transaction =>
                {
                    disposeCount++;
                    disposeTargetTransaction = transaction.GetDbTransaction();
                    if (!throwBeforeDispose)
                        await transaction.DisposeAsync();
                    throw new InvalidOperationException(
                        throwBeforeDispose
                            ? "simulated exception before tombstone transaction dispose"
                            : "simulated exception after tombstone transaction dispose");
                }
            };

            var result = await service.DeleteBillingHistoryAsync(
                profileId,
                run.RunId,
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, disposeCount);
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Null(Assert.IsAssignableFrom<DbTransaction>(disposeTargetTransaction).Connection);
            Assert.Equal(EntityState.Added, db.Entry(pendingOutbox).State);
            await using (var probeTransaction = await db.Database.BeginTransactionAsync())
            {
                await probeTransaction.RollbackAsync();
            }

            await using var verificationDb = new LocalDbContext(options);
            var storedProfile = await verificationDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.True(Assert.Single(DeserializeRuns(storedProfile.BillingRunsJson)).IsTombstoned);
            Assert.False(await verificationDb.SyncOutboxEntries.AsNoTracking()
                .AnyAsync(current => current.Id == pendingOutbox.Id));

            await db.SaveChangesAsync();
            await using var postSaveVerificationDb = new LocalDbContext(options);
            var postSaveProfile = await postSaveVerificationDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.True(Assert.Single(DeserializeRuns(postSaveProfile.BillingRunsJson)).IsTombstoned);
            var storedPendingOutbox = await postSaveVerificationDb.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(current => current.Id == pendingOutbox.Id);
            Assert.Equal(pendingOutbox.MutationId, storedPendingOutbox.MutationId);
            Assert.Equal("Prepared", storedPendingOutbox.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Theory]
    [InlineData("zero-invoice")]
    [InlineData("zero-transaction")]
    [InlineData("zero-payment")]
    [InlineData("cross-run-same-id-payment")]
    public async Task RecalculateRentalSettlement_ActiveZeroOrCrossRunPaymentEvidencePreservesRun(
        string evidenceKind)
    {
        PrepareAppRoot($"georaeplan-rental-run-recalculate-evidence-{evidenceKind}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), false);
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(CreateProfile(profileId, Guid.NewGuid(), customerId, run));

            if (evidenceKind == "zero-transaction")
            {
                var transaction = CreateTransaction(Guid.NewGuid(), customerId, profileId, run.RunId);
                ZeroTransactionAmounts(transaction);
                db.Transactions.Add(transaction);
            }
            else
            {
                var invoice = CreateInvoice(Guid.NewGuid(), customerId, profileId, run.RunId);
                ZeroInvoiceAmounts(invoice);
                db.Invoices.Add(invoice);
                if (evidenceKind is "zero-payment" or "cross-run-same-id-payment")
                {
                    var payment = CreatePayment(Guid.NewGuid(), invoice.Id);
                    if (evidenceKind == "zero-payment")
                        payment.Amount = 0m;
                    db.Payments.Add(payment);
                    if (evidenceKind == "cross-run-same-id-payment")
                    {
                        var otherRunTransaction = CreateTransaction(
                            payment.Id,
                            customerId,
                            profileId,
                            Guid.NewGuid());
                        db.Transactions.Add(otherRunTransaction);
                    }
                }
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            await local.RecalculateRentalSettlementsAsync([(profileId, (Guid?)run.RunId)]);

            db.ChangeTracker.Clear();
            var stored = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Contains(DeserializeRuns(stored.BillingRunsJson), current => current.RunId == run.RunId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecalculateRentalSettlement_WhenEvidenceIsGoneRemovesWholeLegacyIdentityGroup(
        bool omitLegacyRunId)
    {
        PrepareAppRoot($"georaeplan-rental-run-recalculate-remove-identity-{omitLegacyRunId}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), false);
            var legacyCompanion = CreateRun(Guid.Empty, false);
            var unrelatedRun = CreateRun(Guid.NewGuid(), false);
            unrelatedRun.RunKey = "20260601-20260630";
            var profile = CreateProfile(profileId, Guid.NewGuid(), Guid.NewGuid(), run);
            profile.BillingRunsJson = SerializeRunsWithLegacyCompanion(
                run,
                legacyCompanion,
                omitLegacyRunId,
                unrelatedRun);
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            await local.RecalculateRentalSettlementsAsync([(profileId, (Guid?)run.RunId)]);

            db.ChangeTracker.Clear();
            var stored = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var remaining = Assert.Single(DeserializeRuns(stored.BillingRunsJson));
            Assert.Equal(unrelatedRun.RunId, remaining.RunId);
            Assert.Equal(unrelatedRun.RunKey, remaining.RunKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task OrdinaryCancelWithFinancialEvidence_DoesNotCreateTombstoneMarker()
    {
        PrepareAppRoot("georaeplan-rental-run-normal-cancel");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), isTombstoned: false);
            var profile = CreateProfile(profileId, assetId, customerId, run);
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalAssets.Add(CreateAsset(assetId, profileId));
            db.RentalBillingProfiles.Add(profile);
            db.Invoices.Add(CreateInvoice(Guid.NewGuid(), customerId, profileId, run.RunId));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var cancelled = await service.CancelBillingAsync(
                profileId,
                new DateOnly(2026, 5, 25),
                "ordinary cancel",
                CreateAdminSession());

            Assert.True(cancelled.Success, cancelled.Message);
            db.ChangeTracker.Clear();
            var storedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var storedRun = Assert.Single(DeserializeRuns(storedProfile.BillingRunsJson));
            Assert.False(storedRun.IsTombstoned);
            Assert.Null(storedRun.TombstonedAtUtc);
            Assert.Equal(string.Empty, storedRun.TombstonedByUsername);
            Assert.Equal(PaymentFlowConstants.BillingStatusCancelled, storedRun.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task IntegrityScan_IgnoresTombstoneFinancialStateButStillReportsIdentityConflict()
    {
        PrepareAppRoot("georaeplan-rental-run-tombstone-integrity");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var ignoredProfileId = Guid.NewGuid();
            var ignoredRun = CreateRun(Guid.NewGuid(), isTombstoned: true);
            ignoredRun.SettledAmount = 123m;
            var ignoredProfile = CreateProfile(ignoredProfileId, Guid.NewGuid(), Guid.NewGuid(), ignoredRun);
            ignoredProfile.SettledAmount = 456m;
            ignoredProfile.OutstandingAmount = 789m;

            var conflictProfileId = Guid.NewGuid();
            var firstConflictRun = CreateRun(Guid.NewGuid(), isTombstoned: true);
            var secondConflictRun = CreateRun(Guid.NewGuid(), isTombstoned: true);
            var conflictProfile = CreateProfile(conflictProfileId, Guid.NewGuid(), Guid.NewGuid(), firstConflictRun);
            conflictProfile.BillingRunsJson = JsonSerializer.Serialize(new[] { firstConflictRun, secondConflictRun });
            db.RentalBillingProfiles.AddRange(ignoredProfile, conflictProfile);
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());

            Assert.DoesNotContain(result.Issues, issue =>
                issue.ProfileId == ignoredProfileId &&
                (issue.Code == DataIntegrityIssueCodes.RentalBillingRunMissingRunId ||
                 issue.Code == DataIntegrityIssueCodes.RentalBillingRunSettlementMismatch ||
                 issue.Code == DataIntegrityIssueCodes.RentalBillingProfileSummaryMismatch));
            Assert.Contains(result.Issues, issue =>
                issue.ProfileId == conflictProfileId &&
                issue.Code == DataIntegrityIssueCodes.RentalBillingRunKeyConflictingRunIds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TombstoneWinsIdentityGroup_RegardlessOfJsonOrder(bool tombstoneFirst)
    {
        var runId = Guid.NewGuid();
        var active = CreateRun(runId, isTombstoned: false);
        var tombstone = CreateRun(runId, isTombstoned: true);
        var profile = CreateProfile(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), active);
        profile.BillingRunsJson = JsonSerializer.Serialize(
            tombstoneFirst ? new[] { tombstone, active } : new[] { active, tombstone });

        var service = new RentalStateService(null!);

        Assert.Empty(service.GetBillingRuns(profile));
        Assert.Null(service.GetOrCreateBillingRun(profile, new DateOnly(2026, 5, 25), persistChanges: true));
        Assert.Equal(2, DeserializeRuns(profile.BillingRunsJson).Count);
    }

    [Fact]
    public async Task DeleteDuplicatePlannedRun_CanonicalizesEveryExactIdentityRow()
    {
        PrepareAppRoot("georaeplan-rental-run-duplicate-delete");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), isTombstoned: false);
            var duplicate = CreateRun(run.RunId, isTombstoned: false);
            duplicate.BilledAmount = 50_000m;
            var profile = CreateProfile(profileId, Guid.NewGuid(), Guid.NewGuid(), run);
            profile.BillingRunsJson = JsonSerializer.Serialize(new[] { run, duplicate });
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).DeleteBillingHistoryAsync(
                profileId, run.RunId, CreateAdminSession());

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var stored = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var markers = DeserializeRuns(stored.BillingRunsJson);
            Assert.Equal(2, markers.Count);
            Assert.All(markers, marker => Assert.True(marker.IsTombstoned));
            Assert.Single(markers.Select(marker => marker.TombstonedAtUtc).Distinct());
            Assert.All(markers, marker => Assert.Equal(0m, marker.BilledAmount));
            Assert.Empty(new RentalStateService(db).GetBillingRuns(stored));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletePlannedRun_WithLegacyEmptyOrMissingRunIdCompanion_PromotesWholeIdentityGroup(
        bool omitLegacyRunId)
    {
        PrepareAppRoot($"georaeplan-rental-run-legacy-companion-tombstone-{omitLegacyRunId}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), isTombstoned: false);
            var legacyCompanion = CreateRun(Guid.Empty, isTombstoned: false);
            legacyCompanion.BilledAmount = 50_000m;
            var profile = CreateProfile(profileId, Guid.NewGuid(), Guid.NewGuid(), run);
            profile.BillingRunsJson = SerializeRunsWithLegacyCompanion(run, legacyCompanion, omitLegacyRunId);
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).DeleteBillingHistoryAsync(
                profileId, run.RunId, CreateAdminSession());

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var stored = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.True(RentalBillingRunTombstonePolicy.Validate(stored.BillingRunsJson).IsValid);
            var markers = DeserializeRuns(stored.BillingRunsJson);
            Assert.Equal(2, markers.Count);
            Assert.All(markers, marker => Assert.Equal(run.RunId, marker.RunId));
            Assert.All(markers, marker => Assert.True(marker.IsTombstoned));
            Assert.Single(markers.Select(marker => marker.TombstonedAtUtc).Distinct());
            Assert.Single(markers.Select(marker => marker.TombstonedByUsername).Distinct());
            Assert.All(markers, marker => Assert.Equal(PaymentFlowConstants.BillingStatusCancelled, marker.Status));
            Assert.All(markers, marker => Assert.Equal(0m, marker.BilledAmount));
            Assert.All(markers, marker => Assert.Equal(0m, marker.SettledAmount));
            Assert.Empty(new RentalStateService(db).GetBillingRuns(stored));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("cancel")]
    public async Task ConcurrentDeleteAndStatusMutation_AcrossContexts_CannotResurrectRun(string mutation)
    {
        PrepareAppRoot($"georaeplan-rental-run-barrier-{mutation}");
        try
        {
            var profileId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), isTombstoned: false);
            await using (var seedDb = new LocalDbContext())
            {
                await seedDb.Database.EnsureDeletedAsync();
                await seedDb.Database.EnsureCreatedAsync();
                seedDb.RentalBillingProfiles.Add(CreateProfile(profileId, Guid.NewGuid(), Guid.NewGuid(), run));
                await seedDb.SaveChangesAsync();
            }

            await using var firstDb = new LocalDbContext();
            await using var secondDb = new LocalDbContext();
            var session = CreateAdminSession();
            var deleteTask = new RentalStateService(firstDb).DeleteBillingHistoryAsync(profileId, run.RunId, session);
            var mutateTask = mutation == "hold"
                ? new RentalStateService(secondDb).HoldBillingAsync(profileId, new DateOnly(2026, 5, 25), "hold", session)
                : new RentalStateService(secondDb).CancelBillingAsync(profileId, new DateOnly(2026, 5, 25), "cancel", session);
            await Task.WhenAll(deleteTask, mutateTask);

            firstDb.ChangeTracker.Clear();
            var stored = await firstDb.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.All(DeserializeRuns(stored.BillingRunsJson), marker => Assert.True(marker.IsTombstoned));
            Assert.Empty(new RentalStateService(firstDb).GetBillingRuns(stored));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task Recalculation_InvalidTombstoneMarker_PreservesBillingRunsJsonBytesAndSummary()
    {
        PrepareAppRoot("georaeplan-rental-run-invalid-marker-recalc");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var profile = CreateProfile(profileId, Guid.NewGuid(), Guid.NewGuid(), CreateRun(Guid.NewGuid(), false));
            const string originalJson = "[ { \"RunKey\": \"20260501-20260531\", \"IsTombstoned\": true, \"TombstonedAtUtc\": \"2026-05-01T00:00:00Z\", \"TombstonedByUsername\": \"admin\" } ]";
            profile.BillingRunsJson = originalJson;
            profile.SettledAmount = 12_345m;
            profile.OutstandingAmount = 87_655m;
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), CreateAdminSession());
            await local.RecalculateRentalSettlementsAsync(
                new[] { (profileId, (Guid?)null) }, CancellationToken.None, markDirty: true);

            db.ChangeTracker.Clear();
            var stored = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(originalJson, stored.BillingRunsJson);
            Assert.Equal(12_345m, stored.SettledAmount);
            Assert.Equal(87_655m, stored.OutstandingAmount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ProfileRecalculation_ExcludesTombstoneRunEvidence_AndPreservesProfileLevelEvidence()
    {
        PrepareAppRoot("georaeplan-rental-run-mixed-recalc");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var active = CreateRun(Guid.NewGuid(), false);
            var tombstone = CreateRun(Guid.NewGuid(), true);
            tombstone.RunKey = "20260601-20260630";
            tombstone.ScheduledDate = new DateOnly(2026, 6, 25);
            tombstone.PeriodStartDate = new DateOnly(2026, 6, 1);
            tombstone.PeriodEndDate = new DateOnly(2026, 6, 30);
            var profile = CreateProfile(profileId, Guid.NewGuid(), customerId, active);
            profile.BillingRunsJson = JsonSerializer.Serialize(new[] { active, tombstone });
            var activeReceipt = CreateTransaction(Guid.NewGuid(), customerId, profileId, active.RunId);
            activeReceipt.SettlementAmount = activeReceipt.BankReceipt = activeReceipt.ReceiptTotal = 30_000m;
            var tombstoneReceipt = CreateTransaction(Guid.NewGuid(), customerId, profileId, tombstone.RunId);
            tombstoneReceipt.SettlementAmount = tombstoneReceipt.BankReceipt = tombstoneReceipt.ReceiptTotal = 80_000m;
            db.RentalBillingProfiles.Add(profile);
            db.Transactions.AddRange(activeReceipt, tombstoneReceipt);
            await db.SaveChangesAsync();

            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), CreateAdminSession());
            await local.RecalculateRentalSettlementsAsync(
                new[] { (profileId, (Guid?)null) }, CancellationToken.None, markDirty: true);
            db.ChangeTracker.Clear();
            var afterRunEvidence = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(30_000m, afterRunEvidence.SettledAmount);
            Assert.Equal(70_000m, afterRunEvidence.OutstandingAmount);
            Assert.True(DeserializeRuns(afterRunEvidence.BillingRunsJson).Single(run => run.RunId == tombstone.RunId).IsTombstoned);

            var profileReceipt = CreateTransaction(Guid.NewGuid(), customerId, profileId, Guid.Empty);
            profileReceipt.SettlementAmount = profileReceipt.BankReceipt = profileReceipt.ReceiptTotal = 20_000m;
            db.Transactions.Add(profileReceipt);
            await db.SaveChangesAsync();
            await local.RecalculateRentalSettlementsAsync(
                new[] { (profileId, (Guid?)null) }, CancellationToken.None, markDirty: true);
            db.ChangeTracker.Clear();
            var afterProfileEvidence = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(20_000m, afterProfileEvidence.SettledAmount);
            Assert.Equal(80_000m, afterProfileEvidence.OutstandingAmount);
            Assert.True(DeserializeRuns(afterProfileEvidence.BillingRunsJson).Single(run => run.RunId == tombstone.RunId).IsTombstoned);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(PaymentFlowConstants.BillingStatusOnHold, "invoice")]
    [InlineData(PaymentFlowConstants.BillingStatusOnHold, "transaction")]
    [InlineData(PaymentFlowConstants.BillingStatusOnHold, "direct-payment")]
    [InlineData(PaymentFlowConstants.BillingStatusCancelled, "invoice")]
    [InlineData(PaymentFlowConstants.BillingStatusCancelled, "transaction")]
    [InlineData(PaymentFlowConstants.BillingStatusCancelled, "direct-payment")]
    public async Task DeleteHeldOrCancelledRun_WithFinancialEvidence_UsesOrdinaryHistoryDelete(
        string runStatus,
        string evidenceKind)
    {
        PrepareAppRoot($"georaeplan-rental-run-existing-history-delete-{runStatus}-{evidenceKind}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), false);
            run.Status = runStatus;
            var legacyCompanion = CreateRun(Guid.Empty, false);
            legacyCompanion.Status = runStatus;
            var unrelatedRun = CreateRun(Guid.NewGuid(), false);
            unrelatedRun.RunKey = "20260601-20260630";
            unrelatedRun.ScheduledDate = new DateOnly(2026, 6, 25);
            unrelatedRun.PeriodStartDate = new DateOnly(2026, 6, 1);
            unrelatedRun.PeriodEndDate = new DateOnly(2026, 6, 30);
            unrelatedRun.PeriodLabel = "2026-06";
            var profile = CreateProfile(profileId, Guid.NewGuid(), customerId, run);
            profile.BillingStatus = runStatus;
            profile.BillingRunsJson = SerializeRunsWithLegacyCompanion(
                run,
                legacyCompanion,
                true,
                unrelatedRun);
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(profile);

            Guid? invoiceId = null;
            Guid? transactionId = null;
            Guid? paymentId = null;
            if (evidenceKind == "invoice")
            {
                invoiceId = Guid.NewGuid();
                var invoice = CreateInvoice(invoiceId.Value, customerId, profileId, run.RunId);
                ZeroInvoiceAmounts(invoice);
                db.Invoices.Add(invoice);
            }
            else if (evidenceKind == "transaction")
            {
                transactionId = Guid.NewGuid();
                var transaction = CreateTransaction(transactionId.Value, customerId, profileId, run.RunId);
                ZeroTransactionAmounts(transaction);
                db.Transactions.Add(transaction);
            }
            else
            {
                invoiceId = Guid.NewGuid();
                var invoice = CreateInvoice(invoiceId.Value, customerId, profileId, run.RunId);
                ZeroInvoiceAmounts(invoice);
                db.Invoices.Add(invoice);

                paymentId = Guid.NewGuid();
                var payment = CreatePayment(paymentId.Value, invoiceId.Value);
                payment.Amount = 0m;
                db.Payments.Add(payment);

                transactionId = paymentId;
                var mirroredTransaction = CreateTransaction(
                    transactionId.Value,
                    customerId,
                    profileId,
                    run.RunId);
                mirroredTransaction.LinkedInvoiceId = invoiceId;
                ZeroTransactionAmounts(mirroredTransaction);
                db.Transactions.Add(mirroredTransaction);
            }
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await new RentalStateService(db, local).DeleteBillingHistoryAsync(
                profileId,
                run.RunId,
                session);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var remainingRun = Assert.Single(DeserializeRuns(storedProfile.BillingRunsJson));
            Assert.Equal(unrelatedRun.RunId, remainingRun.RunId);
            Assert.Equal(unrelatedRun.RunKey, remainingRun.RunKey);
            if (invoiceId.HasValue)
            {
                var storedInvoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(current => current.Id == invoiceId.Value);
                Assert.True(storedInvoice.IsDeleted);
            }
            if (transactionId.HasValue)
            {
                var storedTransaction = await db.Transactions.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(current => current.Id == transactionId.Value);
                Assert.True(storedTransaction.IsDeleted);
            }
            if (paymentId.HasValue)
            {
                var storedPayment = await db.Payments.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(current => current.Id == paymentId.Value);
                Assert.True(storedPayment.IsDeleted);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteHeldRun_WithSameIdPendingPayment_FailsBeforeTransactionMutation()
    {
        PrepareAppRoot("georaeplan-rental-run-pending-payment-same-transaction-id");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), false);
            run.Status = PaymentFlowConstants.BillingStatusOnHold;
            var profile = CreateProfile(profileId, Guid.NewGuid(), customerId, run);
            profile.BillingStatus = run.Status;
            var transaction = CreateTransaction(Guid.NewGuid(), customerId, profileId, run.RunId);
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(profile);
            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var pendingPayment = CreatePayment(transaction.Id, Guid.NewGuid());
            db.Payments.Add(pendingPayment);
            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await new RentalStateService(db, local).DeleteBillingHistoryAsync(
                profileId,
                run.RunId,
                session);

            Assert.False(result.Success);
            Assert.True(result.ConcurrencyConflict, result.Message);
            Assert.Equal(EntityState.Added, db.Entry(pendingPayment).State);
            await using var verificationDb = new LocalDbContext();
            var storedTransaction = await verificationDb.Transactions.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == transaction.Id);
            Assert.False(storedTransaction.IsDeleted);
            Assert.False(await verificationDb.Payments.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(current => current.Id == pendingPayment.Id));
            var storedProfile = await verificationDb.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Contains(DeserializeRuns(storedProfile.BillingRunsJson), current => current.RunId == run.RunId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ConcurrentDeleteAndRecalculation_AcrossContexts_CannotResurrectRun()
    {
        PrepareAppRoot("georaeplan-rental-run-delete-recalculate-barrier");
        try
        {
            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var run = CreateRun(Guid.NewGuid(), false);
            run.Status = PaymentFlowConstants.BillingStatusCancelled;
            var transaction = CreateTransaction(Guid.NewGuid(), customerId, profileId, run.RunId);
            await using (var seedDb = new LocalDbContext())
            {
                await seedDb.Database.EnsureDeletedAsync();
                await seedDb.Database.EnsureCreatedAsync();
                seedDb.Customers.Add(CreateCustomer(customerId));
                seedDb.RentalBillingProfiles.Add(CreateProfile(profileId, Guid.NewGuid(), customerId, run));
                seedDb.Transactions.Add(transaction);
                await seedDb.SaveChangesAsync();
            }

            await using var deleteDb = new LocalDbContext();
            await using var recalculateDb = new LocalDbContext();
            var session = CreateAdminSession();
            var recalculationReadProfile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowRecalculationToContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recalculateLocal = new LocalStateService(
                recalculateDb,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session)
            {
                TestOnlyRentalSettlementRecalculationAfterProfileReloadAsync = async ct =>
                {
                    recalculationReadProfile.TrySetResult();
                    await allowRecalculationToContinue.Task.WaitAsync(ct);
                }
            };
            var recalculateTask = recalculateLocal.RecalculateRentalSettlementsAsync(
                new[] { (profileId, (Guid?)run.RunId) },
                CancellationToken.None,
                markDirty: true);
            await recalculationReadProfile.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var deleteLocal = new LocalStateService(
                deleteDb,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var deleteTask = new RentalStateService(deleteDb, deleteLocal).DeleteBillingHistoryAsync(
                profileId,
                run.RunId,
                session);
            await Task.Delay(50);
            Assert.False(deleteTask.IsCompleted);

            allowRecalculationToContinue.TrySetResult();

            await Task.WhenAll(deleteTask, recalculateTask);
            var deleteResult = await deleteTask;
            Assert.True(deleteResult.Success, deleteResult.Message);

            deleteDb.ChangeTracker.Clear();
            var stored = await deleteDb.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Empty(DeserializeRuns(stored.BillingRunsJson));
            var storedTransaction = await deleteDb.Transactions.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == transaction.Id);
            Assert.True(storedTransaction.IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ActiveLegacyMissingOrEmptyRunId_RemainsDeterministicAndDiagnosable()
    {
        const string missingRunIdJson = "[{\"RunKey\":\"20260501-20260531\",\"Status\":\"예정\",\"Items\":[]}]";
        const string emptyRunIdJson = "[{\"RunId\":\"00000000-0000-0000-0000-000000000000\",\"RunKey\":\"20260601-20260630\",\"Status\":\"예정\",\"Items\":[]}]";
        const string tombstoneWithEmptyRunIdJson = "[{\"RunId\":\"00000000-0000-0000-0000-000000000000\",\"RunKey\":\"20260501-20260531\",\"IsTombstoned\":true,\"TombstonedAtUtc\":\"2026-05-01T00:00:00Z\",\"TombstonedByUsername\":\"admin\"}]";
        Assert.True(RentalBillingRunTombstonePolicy.Validate(missingRunIdJson).IsValid);
        Assert.True(RentalBillingRunTombstonePolicy.Validate(emptyRunIdJson).IsValid);
        Assert.False(RentalBillingRunTombstonePolicy.Validate(tombstoneWithEmptyRunIdJson).IsValid);
        Assert.Equal(Guid.Empty, new RentalBillingRunModel().RunId);

        PrepareAppRoot("georaeplan-rental-run-legacy-missing-id-diagnosis");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var profile = CreateProfile(profileId, Guid.NewGuid(), Guid.NewGuid(), CreateRun(Guid.NewGuid(), false));
            profile.BillingRunsJson = missingRunIdJson;
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());

            Assert.Contains(result.Issues, issue =>
                issue.ProfileId == profileId &&
                issue.Code == DataIntegrityIssueCodes.RentalBillingRunMissingRunId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SupplementalFinancialRun_WithTombstonedNormalizedRunKey_IsNotPersistedOrDisplayed()
    {
        PrepareAppRoot("georaeplan-rental-run-supplemental-key-tombstone");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var tombstone = CreateRun(Guid.NewGuid(), true);
            var evidenceRunId = Guid.NewGuid();
            var profile = CreateProfile(profileId, Guid.NewGuid(), customerId, tombstone);
            var invoice = CreateInvoice(Guid.NewGuid(), customerId, profileId, evidenceRunId);
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(profile);
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            var originalJson = profile.BillingRunsJson;

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            await local.RecalculateRentalSettlementsAsync(
                new[] { (profileId, (Guid?)evidenceRunId) },
                CancellationToken.None,
                markDirty: true);

            db.ChangeTracker.Clear();
            var stored = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(originalJson, stored.BillingRunsJson);
            Assert.True(Assert.Single(DeserializeRuns(stored.BillingRunsJson)).IsTombstoned);

            var rows = await new RentalStateService(db).GetBillingHistoryRowsAsync(
                new[] { profileId },
                session,
                new DateOnly(2026, 5, 25));
            Assert.DoesNotContain(rows, row => row.BillingRunId == evidenceRunId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static string SerializeRunsWithLegacyCompanion(
        RentalBillingRunModel run,
        RentalBillingRunModel legacyCompanion,
        bool omitLegacyRunId,
        params RentalBillingRunModel[] additionalRuns)
    {
        var runs = new[] { run, legacyCompanion }.Concat(additionalRuns).ToArray();
        var array = JsonNode.Parse(JsonSerializer.Serialize(runs))!.AsArray();
        if (omitLegacyRunId)
            array[1]!.AsObject().Remove(nameof(RentalBillingRunModel.RunId));
        return array.ToJsonString();
    }

    private static void ZeroInvoiceAmounts(LocalInvoice invoice)
    {
        invoice.TotalAmount = 0m;
        invoice.SupplyAmount = 0m;
        invoice.VatAmount = 0m;
        invoice.Lines.Clear();
    }

    private static void ZeroTransactionAmounts(LocalTransaction transaction)
    {
        transaction.SettlementAmount = 0m;
        transaction.BankReceipt = 0m;
        transaction.ReceiptTotal = 0m;
    }

    private static RentalBillingRunModel CreateRun(Guid runId, bool isTombstoned)
        => new()
        {
            RunId = runId,
            RunKey = "20260501-20260531",
            ScheduledDate = new DateOnly(2026, 5, 25),
            PeriodStartDate = new DateOnly(2026, 5, 1),
            PeriodEndDate = new DateOnly(2026, 5, 31),
            CycleMonths = 1,
            PeriodLabel = "2026-05",
            Status = isTombstoned
                ? PaymentFlowConstants.BillingStatusCancelled
                : PaymentFlowConstants.BillingStatusPlanned,
            BilledAmount = isTombstoned ? 0m : 100_000m,
            SettledAmount = 0m,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
            IsTombstoned = isTombstoned,
            TombstonedAtUtc = isTombstoned ? DateTime.UtcNow.AddMinutes(-1) : null,
            TombstonedByUsername = isTombstoned ? "admin" : string.Empty
        };

    private static LocalRentalBillingProfile CreateProfile(
        Guid profileId,
        Guid assetId,
        Guid customerId,
        RentalBillingRunModel run)
        => new()
        {
            Id = profileId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerName = "Tombstone customer",
            InstallSiteName = "Main office",
            ItemName = "Rental fee",
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            BillingDay = 25,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = 1,
            BillingAnchorMonth = 5,
            BillingStartDate = new DateOnly(2026, 5, 1),
            MonthlyAmount = 100_000m,
            BillingTemplateJson = JsonSerializer.Serialize(new[]
            {
                new RentalBillingTemplateItemModel
                {
                    DisplayItemName = "Rental fee",
                    BillingLineMode = "묶음",
                    RepresentativeAssetId = assetId,
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m,
                    IncludedAssetIds = [assetId]
                }
            }),
            BillingRunsJson = JsonSerializer.Serialize(new[] { run }),
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalCustomer CreateCustomer(Guid customerId)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Tombstone customer",
            NameMatchKey = "TOMBSTONE CUSTOMER",
            TradeType = CustomerTradeTypes.Sales,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAsset CreateAsset(Guid assetId, Guid profileId)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"M-{assetId:N}",
            ManagementNumber = $"MN-{assetId:N}"[..12],
            AssetKey = $"AK-{assetId:N}",
            CustomerName = "Tombstone customer",
            CurrentCustomerName = "Tombstone customer",
            InstallSiteName = "Main office",
            InstallLocation = "Main office",
            ItemName = "Rental copier",
            MachineNumber = $"SN-{assetId:N}"[..12],
            AssetStatus = "임대 진행중",
            BillingProfileId = profileId,
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalInvoice CreateInvoice(Guid invoiceId, Guid customerId, Guid profileId, Guid runId)
        => new()
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            SourceWarehouseCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 25),
            TotalAmount = 100_000m,
            SupplyAmount = 100_000m,
            VatAmount = 0m,
            VatMode = InvoiceVatModes.Included,
            IsLatestVersion = true,
            IsConfirmed = true,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            CreatedByUsername = "test",
            LastSavedByUsername = "test",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            LastSavedAtUtc = DateTime.UtcNow,
            IsDirty = false,
            IsDeleted = false,
            Lines =
            [
                new LocalInvoiceLine
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "[5월] Rental fee",
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    LineAmount = 100_000m,
                    OrderIndex = 1
                }
            ]
        };

    private static LocalTransaction CreateTransaction(
        Guid transactionId,
        Guid customerId,
        Guid profileId,
        Guid runId)
        => new()
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 5, 26),
            TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            SettlementAmount = 10_000m,
            BankReceipt = 10_000m,
            ReceiptTotal = 10_000m,
            IsDirty = false,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalPayment CreatePayment(Guid paymentId, Guid invoiceId)
        => new()
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 5, 26),
            Amount = 10_000m,
            Note = "direct payment evidence",
            IsDirty = false,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalSyncOutboxEntry CreatePendingOutbox(Guid id)
        => new()
        {
            Id = id,
            MutationId = $"pending-{id:N}",
            DeviceId = "fault-injection-device",
            EntityName = nameof(LocalCustomer),
            EntityId = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            Status = "Prepared",
            PreparedAtUtc = DateTime.UtcNow
        };

    private sealed class TombstoneTransactionFaultInterceptor(
        bool throwBeforeCommit,
        bool throwAfterCommit,
        bool throwBeforeRollback,
        bool throwAfterRollback = false)
        : DbTransactionInterceptor
    {
        public int CommitAttemptCount { get; private set; }
        public int CommitCompletedCount { get; private set; }
        public int RollbackAttemptCount { get; private set; }
        public int RollbackCompletedCount { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            CommitAttemptCount++;
            return throwBeforeCommit && CommitAttemptCount == 1
                ? ValueTask.FromException<InterceptionResult>(
                    new InvalidOperationException(
                        "simulated tombstone transaction commit failure"))
                : ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            CommitCompletedCount++;
            return throwAfterCommit && CommitCompletedCount == 1
                ? Task.FromException(
                    new InvalidOperationException(
                        "simulated exception after tombstone transaction commit"))
                : Task.CompletedTask;
        }

        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            RollbackAttemptCount++;
            return throwBeforeRollback
                ? ValueTask.FromException<InterceptionResult>(
                    new InvalidOperationException(
                        "simulated tombstone transaction rollback failure"))
                : ValueTask.FromResult(result);
        }

        public override Task TransactionRolledBackAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            RollbackCompletedCount++;
            return throwAfterRollback
                ? Task.FromException(
                    new InvalidOperationException(
                        "simulated exception after tombstone transaction rollback"))
                : Task.CompletedTask;
        }
    }

    private static readonly JsonSerializerOptions BillingRunJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static List<RentalBillingRunModel> DeserializeRuns(string? json)
        => JsonSerializer.Deserialize<List<RentalBillingRunModel>>(json ?? "[]", BillingRunJsonOptions) ?? [];

    private static void AssertTombstonePreserved(string? json, RentalBillingRunModel expected)
    {
        var actual = Assert.Single(DeserializeRuns(json));
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.RunKey, actual.RunKey);
        Assert.True(actual.IsTombstoned);
        Assert.Equal(expected.TombstonedAtUtc, actual.TombstonedAtUtc);
        Assert.Equal(expected.TombstonedByUsername, actual.TombstonedByUsername);
        Assert.Equal(0m, actual.BilledAmount);
        Assert.Equal(0m, actual.SettledAmount);
        Assert.Equal(PaymentFlowConstants.SettlementStatusUnpaid, actual.SettlementStatus);
        Assert.Null(actual.SettledDate);
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

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup for an isolated temporary SQLite database.
        }
    }
}
