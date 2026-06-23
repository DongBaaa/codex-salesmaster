using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingRunStateTests
{
    [Fact]
    public async Task HoldBilling_UsesSelectedReferenceDateForNextUnbilledRun()
    {
        PrepareAppRoot("georaeplan-rental-hold-selected-reference");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Hold reference customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName, customerId);
            profile.BillingCycleMonths = 3;
            profile.BillingAnchorMonth = 5;
            profile.LastBilledDate = new DateOnly(2026, 5, 25);
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var result = await service.HoldBillingAsync(
                profileId,
                new DateOnly(2026, 6, 10),
                string.Empty,
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var persisted = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            var runs = DeserializeRuns(persisted.BillingRunsJson);
            var heldRun = Assert.Single(runs);
            Assert.Equal(new DateOnly(2026, 8, 25), heldRun.ScheduledDate);
            Assert.Equal(PaymentFlowConstants.BillingStatusOnHold, heldRun.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task StartBilling_AfterHeldRunUsesCurrentTemplateInsteadOfStaleHeldSnapshot()
    {
        PrepareAppRoot("georaeplan-rental-held-run-current-template");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Held template refresh customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));
            db.RentalBillingProfiles.Add(CreateBillingProfile(profileId, assetId, customerName, customerId));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var hold = await service.HoldBillingAsync(
                profileId,
                new DateOnly(2026, 5, 25),
                "템플릿 조정 전 보류",
                session);
            Assert.True(hold.Success, hold.Message);

            var profile = await db.RentalBillingProfiles.SingleAsync(current => current.Id == profileId);
            profile.MonthlyAmount = 130_000m;
            profile.ItemName = "Adjusted Rental Fee";
            profile.BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "Adjusted Rental Fee",
                    BillingLineMode = "묶음",
                    RepresentativeAssetId = assetId,
                    Quantity = 1m,
                    UnitPrice = 130_000m,
                    Amount = 130_000m,
                    IncludedAssetIds = [assetId]
                }
            });
            await db.SaveChangesAsync();

            var start = await service.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);

            Assert.True(start.Success, start.Message);
            var invoice = await db.Invoices
                .Include(current => current.Lines)
                .AsNoTracking()
                .SingleAsync(current => current.LinkedRentalBillingProfileId == profileId);
            var line = Assert.Single(invoice.Lines, current => !current.IsDeleted);
            Assert.StartsWith("사무기기 렌탈대금", line.ItemNameOriginal, StringComparison.Ordinal);
            Assert.Equal(130_000m, line.LineAmount);
            Assert.Equal(130_000m, invoice.TotalAmount);

            var persistedProfile = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(130_000m, persistedProfile.MonthlyAmount);
            var run = Assert.Single(DeserializeRuns(persistedProfile.BillingRunsJson));
            var runItem = Assert.Single(run.Items);
            Assert.Equal("Adjusted Rental Fee", runItem.DisplayItemName);
            Assert.Equal(130_000m, runItem.Amount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task StartBilling_BatchesManyIncludedAssetReferencesForInvoiceLineBuild()
    {
        PrepareAppRoot("georaeplan-rental-start-included-assets-batch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Batch invoice line customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));

            var assetIds = new List<Guid>();
            for (var index = 0; index < 650; index++)
            {
                var assetId = Guid.NewGuid();
                assetIds.Add(assetId);
                db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));
            }

            db.RentalBillingProfiles.Add(CreateBillingProfile(profileId, assetIds, customerName, customerId));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var result = await service.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);

            Assert.True(result.Success, result.Message);

            var invoice = await db.Invoices
                .Include(current => current.Lines)
                .AsNoTracking()
                .SingleAsync(current => current.LinkedRentalBillingProfileId == profileId);
            var line = Assert.Single(invoice.Lines);
            Assert.Equal(100_000m, line.LineAmount);

            var persistedProfile = await db.RentalBillingProfiles
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var run = Assert.Single(DeserializeRuns(persistedProfile.BillingRunsJson));
            var runItem = Assert.Single(run.Items);
            Assert.Equal(assetIds.Count, runItem.IncludedAssetIds.Count);
            Assert.Equal(assetIds.OrderBy(id => id), runItem.IncludedAssetIds.OrderBy(id => id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task StartBilling_RejectsBundleTemplateWithZeroMonthlyAmount()
    {
        PrepareAppRoot("georaeplan-rental-bundle-zero-amount-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Zero bundle amount customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));

            var profile = CreateBillingProfile(profileId, assetId, customerName, customerId);
            profile.BillingType = "\uBB36\uC74C";
            profile.MonthlyAmount = 0m;
            profile.BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "Zero bundle rental fee",
                    BillingLineMode = "\uBB36\uC74C",
                    RepresentativeAssetId = assetId,
                    Quantity = 1m,
                    UnitPrice = 0m,
                    Amount = 0m,
                    IncludedAssetIds = [assetId]
                }
            });
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var result = await service.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);

            Assert.False(result.Success);
            Assert.False(await db.Invoices.IgnoreQueryFilters()
                .AnyAsync(current => current.LinkedRentalBillingProfileId == profileId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("개별", "묶음", "개별")]
    [InlineData("묶음", "개별", "묶음")]
    public async Task SaveBillingProfile_ProfileBillingTypeOverridesStoredTemplateLineMode(
        string requestedBillingType,
        string storedLineMode,
        string expectedLineMode)
    {
        PrepareAppRoot("georaeplan-rental-billing-type-line-mode");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Billing type line mode customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));
            await db.SaveChangesAsync();

            var profile = CreateBillingProfile(profileId, assetId, customerName, customerId);
            profile.BillingType = requestedBillingType;
            profile.BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "Rental Copier Fee",
                    BillingLineMode = storedLineMode,
                    RepresentativeAssetId = assetId,
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m,
                    IncludedAssetIds = [assetId]
                }
            });

            var service = new RentalStateService(db);
            var result = await service.SaveBillingProfileAsync(profile, CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var persisted = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(requestedBillingType, persisted.BillingType);
            var templateItem = Assert.Single(DeserializeTemplateItems(persisted.BillingTemplateJson));
            Assert.Equal(expectedLineMode, templateItem.BillingLineMode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task StartBilling_ProfileIndividualTypeOverridesStoredBundleLineModeForInvoiceLines()
    {
        PrepareAppRoot("georaeplan-rental-individual-type-invoice-lines");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Individual billing type customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var assetIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
            foreach (var assetId in assetIds)
                db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));

            var profile = CreateBillingProfile(profileId, assetIds, customerName, customerId);
            profile.BillingType = "개별";
            profile.BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "Rental Copier Fee",
                    BillingLineMode = "묶음",
                    RepresentativeAssetId = assetIds[0],
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m,
                    IncludedAssetIds = assetIds.ToList()
                }
            });
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var result = await service.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);

            Assert.True(result.Success, result.Message);
            var invoice = await db.Invoices
                .Include(current => current.Lines)
                .AsNoTracking()
                .SingleAsync(current => current.LinkedRentalBillingProfileId == profileId);
            Assert.Equal(2, invoice.Lines.Count(line => !line.IsDeleted));

            var persisted = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            var run = Assert.Single(DeserializeRuns(persisted.BillingRunsJson));
            var runItem = Assert.Single(run.Items);
            Assert.Equal("개별", runItem.BillingLineMode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task StartBilling_MixedTemplateKeepsTemplateOrderInPersistedInvoiceLines()
    {
        PrepareAppRoot("georaeplan-rental-mixed-template-line-order");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Mixed template invoice customer";
            var bundleAssetId = Guid.NewGuid();
            var individualAssetIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
            db.Customers.Add(CreateCustomer(customerId, customerName));
            db.RentalAssets.Add(CreateRentalAsset(bundleAssetId, customerName, profileId));
            foreach (var assetId in individualAssetIds)
                db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));

            var profile = CreateBillingProfile(profileId, [bundleAssetId, .. individualAssetIds], customerName, customerId);
            profile.BillingType = "혼합";
            profile.BillingCycleMonths = 2;
            profile.MonthlyAmount = 230_000m;
            profile.BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "Bundle support fee",
                    BillingLineMode = "묶음",
                    RepresentativeAssetId = bundleAssetId,
                    Quantity = 1m,
                    UnitPrice = 30_000m,
                    Amount = 30_000m,
                    IncludedAssetIds = [bundleAssetId]
                },
                new()
                {
                    DisplayItemName = "Individual device fee",
                    BillingLineMode = "개별",
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 200_000m,
                    IncludedAssetIds = individualAssetIds.ToList()
                }
            });
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var result = await service.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);

            Assert.True(result.Success, result.Message);
            var invoice = await db.Invoices
                .Include(current => current.Lines)
                .AsNoTracking()
                .SingleAsync(current => current.LinkedRentalBillingProfileId == profileId);
            var orderedLines = invoice.Lines
                .Where(line => !line.IsDeleted)
                .OrderBy(line => line.OrderIndex)
                .ToList();

            Assert.Equal(6, orderedLines.Count);
            Assert.Equal(Enumerable.Range(1, 6), orderedLines.Select(line => line.OrderIndex));
            Assert.All(orderedLines, line =>
                Assert.Contains("사무기기 렌탈대금", line.ItemNameOriginal, StringComparison.Ordinal));
            Assert.All(orderedLines.Take(2), line =>
            {
                Assert.Equal(30_000m, line.LineAmount);
            });
            Assert.All(orderedLines.Skip(2), line =>
            {
                Assert.Equal(100_000m, line.LineAmount);
            });
            Assert.Equal(460_000m, invoice.TotalAmount);

            var persisted = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            var run = Assert.Single(DeserializeRuns(persisted.BillingRunsJson));
            Assert.Equal("묶음", run.Items[0].BillingLineMode);
            Assert.Equal("개별", run.Items[1].BillingLineMode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingRows_StartMonthSevenBeforeFirstRun_ShowsJulyPeriodWithoutOutstandingPreview()
    {
        PrepareAppRoot("georaeplan-rental-start-month-seven-preview");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Start month customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName, customerId);
            profile.BillingCycleMonths = 6;
            profile.BillingAnchorMonth = 7;
            profile.BillingAnchorDate = null;
            profile.BillingStartDate = null;
            profile.ContractDate = null;
            profile.ContractStartDate = null;
            profile.LastBilledDate = null;
            profile.CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ReferenceDate = new DateOnly(2026, 6, 16),
                    ExpandCustomerSummaryRows = true
                },
                session);

            var row = Assert.Single(rows, current => current.Source.Id == profileId);
            Assert.Equal(new DateOnly(2026, 7, 25), row.NextBillingDate);
            Assert.Equal("2026-07 ~ 2026-12", row.CurrentBillingPeriodLabel);
            Assert.Equal(600_000m, row.CurrentBilledAmount);
            Assert.Equal(0m, row.OutstandingAmount);
            Assert.False(row.HasPastUnresolved);
            Assert.Equal(0, row.PastUnresolvedCount);
            Assert.Equal(0m, row.PastUnresolvedAmount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingRows_StartMonthSevenWithJanuaryContract_UsesSelectedStartMonth()
    {
        PrepareAppRoot("georaeplan-rental-start-month-seven-contract");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Start month with contract customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName, customerId);
            profile.BillingCycleMonths = 6;
            profile.BillingAnchorMonth = 7;
            profile.BillingAnchorDate = null;
            profile.BillingStartDate = new DateOnly(2026, 1, 25);
            profile.ContractDate = new DateOnly(2026, 1, 25);
            profile.ContractStartDate = null;
            profile.LastBilledDate = null;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ReferenceDate = new DateOnly(2026, 6, 16),
                    ExpandCustomerSummaryRows = true
                },
                session);

            var row = Assert.Single(rows, current => current.Source.Id == profileId);
            Assert.Equal(new DateOnly(2026, 7, 25), row.NextBillingDate);
            Assert.Equal("2026-07 ~ 2026-12", row.CurrentBillingPeriodLabel);
            Assert.Equal(0m, row.OutstandingAmount);
            Assert.False(row.HasPastUnresolved);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingRows_StartMonthSevenIgnoresOldPreviewRunBeforeFirstBillingDate()
    {
        PrepareAppRoot("georaeplan-rental-start-month-seven-old-run");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Old preview run customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName, customerId);
            profile.BillingCycleMonths = 6;
            profile.BillingAnchorMonth = 7;
            profile.BillingStartDate = new DateOnly(2026, 1, 25);
            profile.ContractDate = new DateOnly(2026, 1, 25);
            profile.LastBilledDate = null;
            profile.BillingRunsJson = JsonSerializer.Serialize(new List<RentalBillingRunModel>
            {
                new()
                {
                    RunId = Guid.NewGuid(),
                    RunKey = "20260101-20260630",
                    ScheduledDate = new DateOnly(2026, 1, 25),
                    PeriodStartDate = new DateOnly(2026, 1, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    CycleMonths = 6,
                    PeriodLabel = "2026-01 ~ 2026-06",
                    Status = PaymentFlowConstants.BillingStatusPlanned,
                    BilledAmount = 600_000m,
                    SettledAmount = 0m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid
                }
            });
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ReferenceDate = new DateOnly(2026, 6, 16),
                    ExpandCustomerSummaryRows = true
                },
                session);

            var row = Assert.Single(rows, current => current.Source.Id == profileId);
            Assert.Equal(new DateOnly(2026, 7, 25), row.NextBillingDate);
            Assert.Equal("2026-07 ~ 2026-12", row.CurrentBillingPeriodLabel);
            Assert.Equal(0m, row.PastUnresolvedAmount);
            Assert.Equal(0, row.PastUnresolvedCount);
            Assert.False(row.HasPastUnresolved);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task StartBilling_StartMonthSevenBeforeFirstRun_CreatesJulyToDecemberInvoice()
    {
        PrepareAppRoot("georaeplan-rental-start-month-seven-invoice");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Start month invoice customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            var profile = CreateBillingProfile(profileId, assetId, customerName, customerId);
            profile.BillingCycleMonths = 6;
            profile.BillingAnchorMonth = 7;
            profile.BillingStartDate = new DateOnly(2026, 1, 25);
            profile.ContractDate = new DateOnly(2026, 1, 25);
            profile.LastBilledDate = null;
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var result = await service.StartBillingAsync(profileId, new DateOnly(2026, 6, 16), session);

            Assert.True(result.Success, result.Message);
            var invoice = await db.Invoices
                .Include(current => current.Lines)
                .AsNoTracking()
                .SingleAsync(current => current.LinkedRentalBillingProfileId == profileId);
            Assert.Equal(new DateOnly(2026, 7, 25), invoice.InvoiceDate);
            Assert.Equal(6, invoice.Lines.Count(line => !line.IsDeleted));
            var orderedLineNames = invoice.Lines
                .Where(line => !line.IsDeleted)
                .OrderBy(line => line.OrderIndex)
                .Select(line => line.ItemNameOriginal)
                .ToList();
            Assert.Equal("사무기기 렌탈대금[7월]", orderedLineNames.First());
            Assert.Equal("사무기기 렌탈대금[12월]", orderedLineNames.Last());

            var persistedProfile = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            var currentRun = Assert.Single(DeserializeRuns(persistedProfile.BillingRunsJson));
            Assert.Equal(new DateOnly(2026, 7, 25), currentRun.ScheduledDate);
            Assert.Equal(new DateOnly(2026, 7, 1), currentRun.PeriodStartDate);
            Assert.Equal(new DateOnly(2026, 12, 31), currentRun.PeriodEndDate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task StartBilling_DoesNotCarryPreviousRunSettlementIntoNextRun()
    {
        PrepareAppRoot("georaeplan-rental-start-no-settlement-carryover");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Settlement carryover customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            db.RentalBillingProfiles.Add(CreateBillingProfile(profileId, assetId, customerName, customerId));
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var first = await service.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);
            Assert.True(first.Success, first.Message);
            var firstInvoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == first.RelatedEntityId);
            var firstRunId = Assert.IsType<Guid>(firstInvoice.LinkedRentalBillingRunId);
            db.Transactions.Add(new LocalTransaction
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 5, 26),
                TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = firstRunId,
                ReceiptTotal = firstInvoice.TotalAmount,
                BankReceipt = firstInvoice.TotalAmount,
                SettlementAmount = firstInvoice.TotalAmount,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var completed = await service.MarkBillingCompletedAsync(
                profileId,
                new DateOnly(2026, 5, 26),
                "완료",
                string.Empty,
                session,
                billingRunId: firstRunId);
            Assert.True(completed.Success, completed.Message);

            var second = await service.StartBillingAsync(profileId, new DateOnly(2026, 6, 25), session);
            Assert.True(second.Success, second.Message);
            var secondInvoice = await db.Invoices.AsNoTracking().SingleAsync(current => current.Id == second.RelatedEntityId);
            var secondRunId = Assert.IsType<Guid>(secondInvoice.LinkedRentalBillingRunId);

            var persisted = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(current => current.Id == profileId);
            Assert.Equal(0m, persisted.SettledAmount);
            Assert.Equal(secondInvoice.TotalAmount, persisted.OutstandingAmount);
            var secondRun = DeserializeRuns(persisted.BillingRunsJson).Single(run => run.RunId == secondRunId);
            Assert.Equal(0m, secondRun.SettledAmount);
            Assert.Equal(secondInvoice.TotalAmount, secondRun.BilledAmount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task StartBilling_RequiresInvoiceEditPermissionForGeneratedSalesInvoice()
    {
        PrepareAppRoot("georaeplan-rental-start-requires-invoice-edit");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customerName = "Permission customer";
            db.Customers.Add(CreateCustomer(customerId, customerName));
            db.RentalBillingProfiles.Add(CreateBillingProfile(profileId, assetId, customerName, customerId));
            db.RentalAssets.Add(CreateRentalAsset(assetId, customerName, profileId));
            await db.SaveChangesAsync();

            var session = CreateUserSession(AppPermissionNames.RentalProfileEdit);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);

            var result = await service.StartBillingAsync(profileId, new DateOnly(2026, 5, 25), session);

            Assert.False(result.Success);
            Assert.Empty(await db.Invoices.AsNoTracking().ToListAsync());
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
            NameMatchKey = customerName.ToUpperInvariant(),
            TradeType = CustomerTradeTypes.Sales,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAsset CreateRentalAsset(Guid assetId, string customerName, Guid billingProfileId)
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
            InstallSiteName = "Main office",
            InstallLocation = "Main office",
            ItemName = "Rental Copier",
            MachineNumber = ShortCode("SN", assetId),
            AssetStatus = "임대진행중",
            BillingProfileId = billingProfileId,
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalBillingProfile CreateBillingProfile(Guid profileId, Guid assetId, string customerName, Guid customerId)
        => CreateBillingProfile(profileId, [assetId], customerName, customerId);

    private static LocalRentalBillingProfile CreateBillingProfile(Guid profileId, IReadOnlyList<Guid> assetIds, string customerName, Guid customerId)
    {
        var representativeAssetId = assetIds.First();
        return new()
        {
            Id = profileId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerName = customerName,
            InstallSiteName = "Main office",
            ItemName = "Rental Copier Fee",
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            BillingDay = 25,
            BillingCycleMonths = 1,
            MonthlyAmount = 100_000m,
            BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "Rental Copier Fee",
                    BillingLineMode = "묶음",
                    RepresentativeAssetId = representativeAssetId,
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m,
                    IncludedAssetIds = assetIds.ToList()
                }
            }),
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private static List<RentalBillingRunModel> DeserializeRuns(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<RentalBillingRunModel>>(json) ?? [];

    private static List<RentalBillingTemplateItemModel> DeserializeTemplateItems(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(json) ?? [];

    private static string ShortCode(string prefix, Guid id)
        => $"{prefix}-{id:N}"[..12];

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
            Username = "rental-user",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }
}
