using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingRunIdentityConflictTests
{
    private const string IssueCode = "rental_billing_run_key_conflicting_run_ids";
    private const string MalformedIssueCode = "rental_billing_runs_json_malformed";

    [Theory]
    [InlineData("start")]
    [InlineData("hold")]
    [InlineData("cancel")]
    [InlineData("settlement")]
    [InlineData("delete")]
    [InlineData("complete")]
    public async Task FinancialMutation_RejectsNormalizedRunKeyWithDifferentRunIdsWithoutChangingState(
        string operation)
    {
        PrepareAppRoot($"georaeplan-rental-run-identity-{operation}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var runIdA = Guid.NewGuid();
            var runIdB = Guid.NewGuid();
            var originalRunsJson = SerializeRuns(
                CreateRun(runIdA, "  cycle-2026-08  "),
                CreateRun(runIdB, "CYCLE-2026-08"));
            db.RentalBillingProfiles.Add(CreateProfile(profileId, originalRunsJson));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var service = new RentalStateService(db);
            var session = CreateAdminSession();
            var referenceDate = new DateOnly(2026, 8, 10);

            var result = operation switch
            {
                "start" => await service.StartBillingAsync(profileId, referenceDate, session),
                "hold" => await service.HoldBillingAsync(profileId, referenceDate, "hold", session),
                "cancel" => await service.CancelBillingAsync(profileId, referenceDate, "cancel", session),
                "settlement" => await service.RegisterBillingSettlementAsync(
                    profileId,
                    referenceDate,
                    10_000m,
                    "settlement",
                    session,
                    billingRunId: runIdA),
                "delete" => await service.DeleteBillingHistoryAsync(profileId, runIdA, session),
                "complete" => await service.MarkBillingCompletedAsync(
                    profileId,
                    referenceDate,
                    PaymentFlowConstants.CompletionDone,
                    "complete",
                    session,
                    billingRunId: runIdA),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            };

            Assert.False(result.Success);
            Assert.True(result.ConcurrencyConflict || result.PermissionDenied);
            Assert.Contains("RunKey", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CYCLE-2026-08", result.Message, StringComparison.Ordinal);
            Assert.Contains(runIdA.ToString("D"), result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(runIdB.ToString("D"), result.Message, StringComparison.OrdinalIgnoreCase);

            db.ChangeTracker.Clear();
            var persisted = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(profile => profile.Id == profileId);
            Assert.Equal(originalRunsJson, persisted.BillingRunsJson);
            Assert.False(persisted.IsDirty);
            Assert.Equal(PaymentFlowConstants.BillingStatusPlanned, persisted.BillingStatus);
            Assert.Empty(await db.Invoices.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await db.Transactions.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await db.Payments.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
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
    public void RunIdentityGuard_AllowsSingleRunAndDuplicateRowsWithTheSameRunId(bool duplicateRow)
    {
        var runId = Guid.NewGuid();
        var runs = duplicateRow
            ? new[]
            {
                CreateRun(runId, "  cycle-2026-08 "),
                CreateRun(runId, "CYCLE-2026-08")
            }
            : new[] { CreateRun(runId, " cycle-2026-08 ") };
        var profile = CreateProfile(Guid.NewGuid(), SerializeRuns(runs));

        var method = typeof(RentalStateService).GetMethod(
            "TryGetConflictingBillingRunIdentity",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var args = new object?[] { profile, null, null };
        var hasConflict = Assert.IsType<bool>(method!.Invoke(null, args));

        Assert.False(hasConflict);
        Assert.Null(args[1]);
        Assert.Null(args[2]);
    }

    [Fact]
    public async Task IntegrityScan_ReportsNormalizedRunKeyAndBothRunIdsWithBillingProfileAction()
    {
        PrepareAppRoot("georaeplan-rental-run-identity-integrity");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var runIdA = Guid.NewGuid();
            var runIdB = Guid.NewGuid();
            var singleRunProfileId = Guid.NewGuid();
            var sameRunIdProfileId = Guid.NewGuid();
            var sameRunId = Guid.NewGuid();
            db.RentalBillingProfiles.AddRange(
                CreateProfile(
                    profileId,
                    SerializeRuns(
                        CreateRun(runIdA, "  cycle-2026-08  "),
                        CreateRun(runIdB, "CYCLE-2026-08"))),
                CreateProfile(
                    singleRunProfileId,
                    SerializeRuns(CreateRun(Guid.NewGuid(), " cycle-2026-09 "))),
                CreateProfile(
                    sameRunIdProfileId,
                    SerializeRuns(
                        CreateRun(sameRunId, "  cycle-2026-10  "),
                        CreateRun(sameRunId, "CYCLE-2026-10"))));
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());

            var issue = Assert.Single(result.Issues, issue => issue.Code == IssueCode);
            Assert.Equal(profileId, issue.ProfileId);
            Assert.Equal(DataIntegrityDirectActionKind.OpenRentalBillingProfile, issue.DirectActionKind);
            var display = $"{issue.CurrentValue} {issue.Message} {issue.ReviewInfo}";
            Assert.Contains(profileId.ToString("D"), display, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CYCLE-2026-08", display, StringComparison.Ordinal);
            Assert.Contains(runIdA.ToString("D"), display, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(runIdB.ToString("D"), display, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                result.Issues,
                candidate => candidate.Code == IssueCode &&
                             (candidate.ProfileId == singleRunProfileId || candidate.ProfileId == sameRunIdProfileId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AutomaticInvoicePeriodRepair_SkipsConflictingRunIdentityWithoutReplacingIds()
    {
        PrepareAppRoot("georaeplan-rental-run-identity-repair");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var runIdA = Guid.NewGuid();
            var runIdB = Guid.NewGuid();
            var originalRunsJson = SerializeRuns(
                CreateRun(runIdA, "  cycle-2026-08  "),
                CreateRun(runIdB, "CYCLE-2026-08"));
            var profile = CreateProfile(profileId, originalRunsJson);
            profile.CustomerId = customerId;
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(profile);
            db.Invoices.Add(CreateRentalInvoice(invoiceId, customerId, profileId, runIdA));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var service = new RentalStateService(db, local);

            var result = await service.RepairBillingInvoicePeriodLinksAsync(
                session,
                new DateOnly(2026, 9, 10));

            Assert.False(result.HasChanges);
            Assert.True(result.SkippedCount > 0);
            var message = string.Join(" ", result.Notes);
            Assert.Contains("RunKey", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(runIdA.ToString("D"), message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(runIdB.ToString("D"), message, StringComparison.OrdinalIgnoreCase);

            db.ChangeTracker.Clear();
            var persistedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var persistedInvoice = await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == invoiceId);
            Assert.Equal(originalRunsJson, persistedProfile.BillingRunsJson);
            Assert.False(persistedProfile.IsDirty);
            Assert.Equal(runIdA, persistedInvoice.LinkedRentalBillingRunId);
            Assert.False(persistedInvoice.IsDirty);
            Assert.Empty(await db.Transactions.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await db.Payments.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("hold")]
    public async Task BillingMutation_ReusesSingletonLegacyRunIdForNormalizedRunKey(string operation)
    {
        PrepareAppRoot($"georaeplan-rental-normalized-run-reuse-{operation}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var legacyRunId = Guid.NewGuid();
            var profile = CreateProfile(
                profileId,
                SerializeRuns(CreateRunForMonth(legacyRunId, "  20260801-20260831  ", 2026, 8)));
            profile.CustomerId = customerId;
            profile.BillingTemplateJson = JsonSerializer.Serialize(new[]
            {
                new RentalBillingTemplateItemModel
                {
                    DisplayItemName = "Run identity rental",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m,
                    RepresentativeAssetId = assetId,
                    IncludedAssetIds = [assetId]
                }
            });
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerId, profileId));
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);
            var referenceDate = new DateOnly(2026, 8, 10);

            var result = operation == "start"
                ? await service.StartBillingAsync(profileId, referenceDate, session)
                : await service.HoldBillingAsync(profileId, referenceDate, "hold", session);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var persisted = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var runs = DeserializeRuns(persisted.BillingRunsJson);
            var run = Assert.Single(runs);
            Assert.Equal(legacyRunId, run.RunId);
            Assert.Equal("20260801-20260831", SyncIdentityGenerator.NormalizeKey(run.RunKey));
            var invoices = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .Where(invoice => !invoice.IsDeleted && invoice.IsLatestVersion)
                .ToListAsync();
            if (operation == "start")
                Assert.Equal(legacyRunId, Assert.Single(invoices).LinkedRentalBillingRunId);
            else
                Assert.Empty(invoices);
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
    public async Task AutomaticInvoicePeriodRepair_ReusesOrPromotesSingletonExistingTargetRunId(
        bool targetRunIdIsEmpty)
    {
        PrepareAppRoot("georaeplan-rental-normalized-repair-target-reuse");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var augustRunId = targetRunIdIsEmpty ? Guid.Empty : Guid.NewGuid();
            var expectedAugustRunId = targetRunIdIsEmpty
                ? SyncIdentityGenerator.CreateRentalBillingRunId(profileId, "20260801-20260831")
                : augustRunId;
            var septemberRunId = Guid.NewGuid();
            var profile = CreateProfile(
                profileId,
                SerializeRuns(
                    CreateRunForMonth(augustRunId, "  20260801-20260831  ", 2026, 8),
                    CreateRunForMonth(septemberRunId, "20260901-20260930", 2026, 9)));
            profile.CustomerId = customerId;
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(profile);
            db.Invoices.Add(CreateRentalInvoice(
                invoiceId,
                customerId,
                profileId,
                septemberRunId,
                new DateOnly(2026, 9, 4),
                "렌탈 대금 [8월]"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var result = await service.RepairBillingInvoicePeriodLinksAsync(session, new DateOnly(2026, 9, 10));

            Assert.Equal(1, result.RepairedInvoiceCount);
            db.ChangeTracker.Clear();
            var latestInvoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(invoice => invoice.VersionGroupId == invoiceId && invoice.IsLatestVersion && !invoice.IsDeleted);
            Assert.Equal(expectedAugustRunId, latestInvoice.LinkedRentalBillingRunId);
            Assert.True(latestInvoice.IsDirty);
            var persisted = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.True(persisted.IsDirty);
            var allRuns = DeserializeRuns(persisted.BillingRunsJson);
            var augustRuns = allRuns
                .Where(run => SyncIdentityGenerator.NormalizeKey(run.RunKey) == "20260801-20260831")
                .ToList();
            Assert.Equal(expectedAugustRunId, Assert.Single(augustRuns).RunId);
            Assert.Single(allRuns);
            Assert.DoesNotContain(allRuns, run => run.RunId == Guid.Empty);
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
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
    public async Task ShiftedFutureInvoiceRepair_ReusesOrPromotesSingletonExpectedRunId(
        bool targetRunIdIsEmpty)
    {
        PrepareAppRoot("georaeplan-rental-normalized-shifted-repair-reuse");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var mayRunId = Guid.NewGuid();
            var juneRunId = targetRunIdIsEmpty ? Guid.Empty : Guid.NewGuid();
            var expectedJuneRunId = targetRunIdIsEmpty
                ? SyncIdentityGenerator.CreateRentalBillingRunId(profileId, "20260601-20260630")
                : juneRunId;
            var futureRunId = Guid.NewGuid();
            var mayRun = CreateRunForMonth(mayRunId, "20260501-20260531", 2026, 5);
            mayRun.Status = PaymentFlowConstants.BillingStatusCompleted;
            mayRun.BilledAmount = 100_000m;
            mayRun.SettledAmount = 100_000m;
            mayRun.SettlementStatus = PaymentFlowConstants.SettlementStatusConfirmed;
            var profile = CreateProfile(
                profileId,
                SerializeRuns(
                    mayRun,
                    CreateRunForMonth(juneRunId, "  20260601-20260630  ", 2026, 6)));
            profile.CustomerId = customerId;
            profile.LastBilledDate = new DateOnly(2026, 5, 25);
            profile.BillingAnchorMonth = 5;
            profile.BillingStartDate = new DateOnly(2026, 5, 1);
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(profile);
            db.Invoices.Add(CreateRentalInvoice(
                invoiceId,
                customerId,
                profileId,
                futureRunId,
                new DateOnly(2026, 7, 25),
                "렌탈 대금 [7월]"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);
            var repairResult = new RentalBillingReferenceRepairResult();

            var repaired = await InvokeShiftedFutureRepairAsync(
                service,
                profileId,
                session,
                new DateOnly(2026, 6, 29),
                repairResult);

            Assert.True(repaired);
            db.ChangeTracker.Clear();
            var latestInvoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(invoice => invoice.VersionGroupId == invoiceId && invoice.IsLatestVersion && !invoice.IsDeleted);
            Assert.Equal(expectedJuneRunId, latestInvoice.LinkedRentalBillingRunId);
            Assert.True(latestInvoice.IsDirty);
            var persisted = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.True(persisted.IsDirty);
            var allRuns = DeserializeRuns(persisted.BillingRunsJson);
            var juneRuns = allRuns
                .Where(run => SyncIdentityGenerator.NormalizeKey(run.RunKey) == "20260601-20260630")
                .ToList();
            Assert.Equal(expectedJuneRunId, Assert.Single(juneRuns).RunId);
            Assert.Equal(2, allRuns.Count);
            Assert.DoesNotContain(allRuns, run => run.RunId == Guid.Empty);
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AutomaticInvoicePeriodRepair_SkipsMultipleKeyOnlyTargetRowsBeforeInvoiceMutation()
    {
        PrepareAppRoot("georaeplan-rental-repair-ambiguous-key-only-target");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var septemberRunId = Guid.NewGuid();
            var originalRunsJson = SerializeRuns(
                CreateRunForMonth(Guid.Empty, "  20260801-20260831  ", 2026, 8),
                CreateRunForMonth(Guid.Empty, "20260801-20260831", 2026, 8),
                CreateRunForMonth(septemberRunId, "20260901-20260930", 2026, 9));
            var profile = CreateProfile(profileId, originalRunsJson);
            profile.CustomerId = customerId;
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(profile);
            db.Invoices.Add(CreateRentalInvoice(
                invoiceId,
                customerId,
                profileId,
                septemberRunId,
                new DateOnly(2026, 9, 4),
                "렌탈 대금 [8월]"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var result = await service.RepairBillingInvoicePeriodLinksAsync(session, new DateOnly(2026, 9, 10));

            Assert.False(result.HasChanges);
            Assert.True(result.SkippedCount > 0);
            Assert.Contains("RunId", string.Join(" ", result.Notes), StringComparison.OrdinalIgnoreCase);
            db.ChangeTracker.Clear();
            var persisted = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var invoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == invoiceId);
            Assert.Equal(originalRunsJson, persisted.BillingRunsJson);
            Assert.False(persisted.IsDirty);
            Assert.Equal(septemberRunId, invoice.LinkedRentalBillingRunId);
            Assert.False(invoice.IsDirty);
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ShiftedFutureInvoiceRepair_SkipsMultipleKeyOnlyExpectedRowsBeforeInvoiceMutation()
    {
        PrepareAppRoot("georaeplan-rental-shifted-repair-ambiguous-key-only-target");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var futureRunId = Guid.NewGuid();
            var mayRun = CreateRunForMonth(Guid.NewGuid(), "20260501-20260531", 2026, 5);
            mayRun.Status = PaymentFlowConstants.BillingStatusCompleted;
            mayRun.BilledAmount = 100_000m;
            mayRun.SettledAmount = 100_000m;
            mayRun.SettlementStatus = PaymentFlowConstants.SettlementStatusConfirmed;
            var originalRunsJson = SerializeRuns(
                mayRun,
                CreateRunForMonth(Guid.Empty, "  20260601-20260630  ", 2026, 6),
                CreateRunForMonth(Guid.Empty, "20260601-20260630", 2026, 6));
            var profile = CreateProfile(profileId, originalRunsJson);
            profile.CustomerId = customerId;
            profile.LastBilledDate = new DateOnly(2026, 5, 25);
            profile.BillingAnchorMonth = 5;
            profile.BillingStartDate = new DateOnly(2026, 5, 1);
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(profile);
            db.Invoices.Add(CreateRentalInvoice(
                invoiceId,
                customerId,
                profileId,
                futureRunId,
                new DateOnly(2026, 7, 25),
                "렌탈 대금 [7월]"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);
            var repairResult = new RentalBillingReferenceRepairResult();

            var repaired = await InvokeShiftedFutureRepairAsync(
                service,
                profileId,
                session,
                new DateOnly(2026, 6, 29),
                repairResult);

            Assert.False(repaired);
            Assert.True(repairResult.SkippedCount > 0);
            Assert.Contains("RunId", string.Join(" ", repairResult.Notes), StringComparison.OrdinalIgnoreCase);
            db.ChangeTracker.Clear();
            var persisted = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var invoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == invoiceId);
            Assert.Equal(originalRunsJson, persisted.BillingRunsJson);
            Assert.False(persisted.IsDirty);
            Assert.Equal(futureRunId, invoice.LinkedRentalBillingRunId);
            Assert.Equal(new DateOnly(2026, 7, 25), invoice.InvoiceDate);
            Assert.False(invoice.IsDirty);
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("hold")]
    [InlineData("cancel")]
    [InlineData("settlement")]
    [InlineData("delete")]
    [InlineData("complete")]
    public async Task FinancialMutation_RejectsMalformedBillingRunsJsonWithoutChangingState(string operation)
    {
        PrepareAppRoot($"georaeplan-rental-malformed-runs-{operation}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            const string malformedJson = "[{ malformed-rental-run-json";
            db.RentalBillingProfiles.Add(CreateProfile(profileId, malformedJson));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var service = new RentalStateService(db);
            var session = CreateAdminSession();
            var referenceDate = new DateOnly(2026, 8, 10);
            var result = operation switch
            {
                "start" => await service.StartBillingAsync(profileId, referenceDate, session),
                "hold" => await service.HoldBillingAsync(profileId, referenceDate, "hold", session),
                "cancel" => await service.CancelBillingAsync(profileId, referenceDate, "cancel", session),
                "settlement" => await service.RegisterBillingSettlementAsync(
                    profileId,
                    referenceDate,
                    10_000m,
                    "settlement",
                    session,
                    billingRunId: runId),
                "delete" => await service.DeleteBillingHistoryAsync(profileId, runId, session),
                "complete" => await service.MarkBillingCompletedAsync(
                    profileId,
                    referenceDate,
                    PaymentFlowConstants.CompletionDone,
                    "complete",
                    session,
                    billingRunId: runId),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            };

            Assert.False(result.Success);
            Assert.True(result.ConcurrencyConflict || result.PermissionDenied);
            Assert.Contains("BillingRunsJson", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("JSON", result.Message, StringComparison.OrdinalIgnoreCase);

            db.ChangeTracker.Clear();
            var persisted = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(malformedJson, persisted.BillingRunsJson);
            Assert.False(persisted.IsDirty);
            Assert.Empty(await db.Invoices.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await db.Transactions.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await db.Payments.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task IdentityConflictFollowedByInvalidTombstone_RemainsGenericMalformedAndPreservesRawJson()
    {
        PrepareAppRoot("georaeplan-rental-identity-conflict-hidden-invalid-marker");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var firstRun = CreateRun(Guid.NewGuid(), " cycle-2026-08 ");
            var secondRun = CreateRun(Guid.NewGuid(), "CYCLE-2026-08");
            var invalidTombstoneRunId = Guid.NewGuid();
            var rawJson =
                $"[{JsonSerializer.Serialize(firstRun)},{JsonSerializer.Serialize(secondRun)}," +
                $"{{\"RunId\":\"{invalidTombstoneRunId:D}\",\"IsTombstoned\":true}}]";
            db.RentalBillingProfiles.Add(CreateProfile(profileId, rawJson));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var result = await new RentalStateService(db).StartBillingAsync(
                profileId,
                new DateOnly(2026, 8, 10),
                CreateAdminSession());

            Assert.False(result.Success);
            Assert.Contains("BillingRunsJson", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("JSON", result.Message, StringComparison.OrdinalIgnoreCase);
            db.ChangeTracker.Clear();
            var persisted = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            Assert.Equal(rawJson, persisted.BillingRunsJson);
            Assert.False(persisted.IsDirty);
            Assert.Empty(await db.Invoices.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AutomaticInvoicePeriodRepair_RejectsMalformedBillingRunsJsonWithoutChangingState()
    {
        PrepareAppRoot("georaeplan-rental-malformed-runs-repair");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            const string malformedJson = "{ malformed-rental-run-json";
            var profile = CreateProfile(profileId, malformedJson);
            profile.CustomerId = customerId;
            db.Customers.Add(CreateCustomer(customerId));
            db.RentalBillingProfiles.Add(profile);
            db.Invoices.Add(CreateRentalInvoice(invoiceId, customerId, profileId, runId));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var result = await service.RepairBillingInvoicePeriodLinksAsync(session, new DateOnly(2026, 9, 10));

            Assert.False(result.HasChanges);
            Assert.True(result.SkippedCount > 0);
            Assert.Contains("BillingRunsJson", string.Join(" ", result.Notes), StringComparison.OrdinalIgnoreCase);
            db.ChangeTracker.Clear();
            var persisted = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var invoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == invoiceId);
            Assert.Equal(malformedJson, persisted.BillingRunsJson);
            Assert.False(persisted.IsDirty);
            Assert.Equal(runId, invoice.LinkedRentalBillingRunId);
            Assert.False(invoice.IsDirty);
            Assert.Empty(await db.Transactions.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await db.Payments.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task IntegrityScan_ReportsMalformedRunsJsonButAllowsBlankNullAndEmptyRunKeys()
    {
        PrepareAppRoot("georaeplan-rental-malformed-empty-run-integrity");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var malformedProfileId = Guid.NewGuid();
            var blankProfileId = Guid.NewGuid();
            var emptyKeyProfileId = Guid.NewGuid();
            var malformed = CreateProfile(malformedProfileId, "[ malformed");
            var blank = CreateProfile(blankProfileId, "   ");
            var emptyKeys = CreateProfile(
                emptyKeyProfileId,
                SerializeRuns(CreateRun(Guid.NewGuid(), ""), CreateRun(Guid.NewGuid(), "   ")));
            db.RentalBillingProfiles.AddRange(malformed, blank, emptyKeys);
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());

            var malformedIssue = Assert.Single(
                result.Issues,
                issue => issue.Code == MalformedIssueCode && issue.ProfileId == malformedProfileId);
            Assert.Equal(DataIntegrityDirectActionKind.OpenRentalBillingProfile, malformedIssue.DirectActionKind);
            Assert.Contains(malformedProfileId.ToString("D"), malformedIssue.CurrentValue, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                result.Issues,
                issue => issue.Code == MalformedIssueCode && issue.ProfileId == blankProfileId);
            Assert.DoesNotContain(
                result.Issues,
                issue => issue.Code == IssueCode && issue.ProfileId == emptyKeyProfileId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RunIdentityGuard_AllowsDifferentRunIdsWhenNormalizedRunKeyIsEmpty()
    {
        var profile = CreateProfile(
            Guid.NewGuid(),
            SerializeRuns(CreateRun(Guid.NewGuid(), ""), CreateRun(Guid.NewGuid(), "   ")));
        var method = typeof(RentalStateService).GetMethod(
            "TryGetConflictingBillingRunIdentity",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var args = new object?[] { profile, null, null };
        var hasConflict = Assert.IsType<bool>(method!.Invoke(null, args));

        Assert.False(hasConflict);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void BillingRunParser_TreatsNullBlankAndJsonNullAsValidEmpty(string? billingRunsJson)
    {
        var profile = CreateProfile(Guid.NewGuid(), billingRunsJson!);
        var service = new RentalStateService(null!);

        Assert.Empty(service.GetBillingRuns(profile));
        var method = typeof(RentalStateService).GetMethod(
            "TryGetConflictingBillingRunIdentity",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var args = new object?[] { profile, null, null };
        Assert.False(Assert.IsType<bool>(method!.Invoke(null, args)));
    }

    private static LocalRentalBillingProfile CreateProfile(Guid profileId, string billingRunsJson)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"RUN-CONFLICT-{profileId:N}",
            CustomerName = "Run identity customer",
            ItemName = "Run identity rental",
            BillingType = "bundle",
            BillingMethod = "bank",
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
            BillingDay = 25,
            BillingCycleMonths = 1,
            BillingAnchorMonth = 8,
            BillingStartDate = new DateOnly(2026, 8, 1),
            MonthlyAmount = 100_000m,
            BillingTemplateJson = "[]",
            BillingRunsJson = billingRunsJson,
            IsActive = true,
            IsDirty = false,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };

    private static RentalBillingRunModel CreateRun(Guid runId, string runKey)
        => new()
        {
            RunId = runId,
            RunKey = runKey,
            ScheduledDate = new DateOnly(2026, 8, 25),
            PeriodStartDate = new DateOnly(2026, 8, 1),
            PeriodEndDate = new DateOnly(2026, 8, 31),
            CycleMonths = 1,
            PeriodLabel = "2026-08",
            Status = PaymentFlowConstants.BillingStatusPlanned,
            BilledAmount = 100_000m,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid
        };

    private static RentalBillingRunModel CreateRunForMonth(
        Guid runId,
        string runKey,
        int year,
        int month)
    {
        var start = new DateOnly(year, month, 1);
        var end = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return new RentalBillingRunModel
        {
            RunId = runId,
            RunKey = runKey,
            ScheduledDate = new DateOnly(year, month, Math.Min(25, end.Day)),
            PeriodStartDate = start,
            PeriodEndDate = end,
            CycleMonths = 1,
            PeriodLabel = $"{year:0000}-{month:00}",
            Status = PaymentFlowConstants.BillingStatusPlanned,
            BilledAmount = 100_000m,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid
        };
    }

    private static LocalCustomer CreateCustomer(Guid customerId)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Run identity customer",
            NameMatchKey = "RUN IDENTITY CUSTOMER",
            TradeType = CustomerTradeTypes.Sales,
            IsDirty = false,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };

    private static LocalRentalAsset CreateRentalAsset(
        Guid assetId,
        Guid customerId,
        Guid profileId)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            CustomerId = customerId,
            BillingProfileId = profileId,
            AssetKey = $"RUN-IDENTITY-{assetId:N}",
            ManagementNumber = $"RUN-{assetId:N}",
            ItemName = "Run identity rental",
            CurrentCustomerName = "Run identity customer",
            CustomerName = "Run identity customer",
            AssetStatus = "임대진행중",
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = 100_000m,
            IsDirty = false,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };

    private static LocalInvoice CreateRentalInvoice(
        Guid invoiceId,
        Guid customerId,
        Guid profileId,
        Guid runId,
        DateOnly? invoiceDate = null,
        string lineName = "렌탈 대금 [8월]")
        => new()
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            SourceWarehouseCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            InvoiceDate = invoiceDate ?? new DateOnly(2026, 9, 4),
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
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            LastSavedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            IsDirty = false,
            IsDeleted = false,
            Lines =
            [
                new LocalInvoiceLine
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemNameOriginal = lineName,
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    LineAmount = 100_000m,
                    OrderIndex = 1
                }
            ]
        };

    private static List<RentalBillingRunModel> DeserializeRuns(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<RentalBillingRunModel>>(json) ?? [];

    private static async Task<bool> InvokeShiftedFutureRepairAsync(
        RentalStateService service,
        Guid profileId,
        SessionState session,
        DateOnly referenceDate,
        RentalBillingReferenceRepairResult result)
    {
        var method = typeof(RentalStateService).GetMethod(
            "RepairShiftedFutureBillingInvoiceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            service,
            new object?[] { profileId, session, referenceDate, result, CancellationToken.None }));
        await task;
        return Assert.IsType<bool>(task.GetType().GetProperty("Result")!.GetValue(task));
    }

    private static string SerializeRuns(params RentalBillingRunModel[] runs)
        => JsonSerializer.Serialize(runs);

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
}
