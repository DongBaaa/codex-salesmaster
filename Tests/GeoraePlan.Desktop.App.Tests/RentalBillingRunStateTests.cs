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
