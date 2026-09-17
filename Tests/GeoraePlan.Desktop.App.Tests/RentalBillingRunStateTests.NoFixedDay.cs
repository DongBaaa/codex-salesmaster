using System.Text.Json;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class RentalBillingRunStateTests
{
    [Theory]
    [InlineData(false, "후불")]
    [InlineData(true, "후불")]
    [InlineData(false, "당월")]
    [InlineData(true, "당월")]
    public async Task NoFixedDay_SavePreviewInvoiceAndRetry_PreservePeriodAndSelectedInvoiceDate(bool itworld, string advanceMode)
    {
        PrepareAppRoot("georaeplan-no-fixed-day-workflow");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var customer = CreateCustomer(customerId, "No fixed day fixture");
            var asset = CreateRentalAsset(assetId, customer.NameOriginal, profileId);
            var profile = CreateBillingProfile(profileId, assetId, customer.NameOriginal, customerId);
            var tenant = itworld ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup;
            var office = itworld ? OfficeCodeCatalog.Itworld : OfficeCodeCatalog.Usenet;
            customer.TenantCode = asset.TenantCode = profile.TenantCode = tenant;
            customer.OfficeCode = asset.OfficeCode = profile.OfficeCode = office;
            customer.ResponsibleOfficeCode = asset.ResponsibleOfficeCode = profile.ResponsibleOfficeCode = office;
            asset.ManagementCompanyCode = profile.ManagementCompanyCode = office;
            profile.BillingDayMode = RentalBillingScheduleRules.BillingDayModeNoFixedDay;
            profile.BillingDay = 31;
            profile.BillingAdvanceMode = advanceMode;
            profile.BillingStartDate = new DateOnly(2026, 9, 1);
            profile.ContractStartDate = profile.BillingStartDate;
            profile.MonthlyAmount = asset.MonthlyFee = 55000;
            var template = Assert.Single(DeserializeTemplateItems(profile.BillingTemplateJson));
            template.UnitPrice = template.Amount = 55000;
            profile.BillingTemplateJson = JsonSerializer.Serialize(new[] { template });
            db.Customers.Add(customer);
            db.RentalAssets.Add(asset);
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();
            var maintenance = typeof(LocalDbInitializer).GetMethod("NormalizeRentalBillingScheduleRulesAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            await (Task)maintenance.Invoke(null, new object[] { db })!;
            Assert.Equal(RentalBillingScheduleRules.BillingDayModeNoFixedDay, profile.BillingDayMode);
            Assert.Equal(0, profile.BillingDay);
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin", Role = DomainConstants.RoleAdmin,
                ScopeType = TenantScopeCatalog.ScopeAdmin, TenantCode = tenant, OfficeCode = office
            });
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);
            db.ChangeTracker.Clear();
            var settingsEdit = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(p => p.Id == profileId);
            var save = await service.SaveBillingProfileAsync(settingsEdit, session);
            Assert.True(save.Success, save.Message);
            var saved = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(p => p.Id == profileId);
            Assert.Equal(RentalBillingScheduleRules.BillingDayModeNoFixedDay, saved.BillingDayMode);
            Assert.Equal(0, saved.BillingDay);
            Assert.Equal(advanceMode, saved.BillingAdvanceMode);
            var beforePreview = JsonSerializer.Serialize(saved);
            foreach (var day in new[] { 1, 17, 30 })
            {
                var row = await service.GetBillingRowAsync(profileId, session, new DateOnly(2026, 9, day));
                Assert.NotNull(row);
                Assert.Equal(advanceMode, row.BillingAdvanceMode);
                Assert.Null(row.NextBillingDate);
                Assert.Null(row.DocumentIssueDate);
                Assert.Null(row.DaysRemaining);
            }
            Assert.Equal(beforePreview, JsonSerializer.Serialize(await db.RentalBillingProfiles.AsNoTracking().SingleAsync(p => p.Id == profileId)));
            Assert.Empty(await db.Invoices.ToListAsync());

            var first = await service.StartBillingAsync(profileId, new DateOnly(2026, 9, 17), session);
            Assert.True(first.Success, first.Message);
            var invoice = Assert.Single(await db.Invoices.AsNoTracking().Where(i => !i.IsDeleted).ToListAsync());
            Assert.Equal(new DateOnly(2026, 9, 17), invoice.InvoiceDate);
            Assert.Equal(55000m, invoice.TotalAmount);
            var retry = await service.StartBillingAsync(profileId, new DateOnly(2026, 9, 30), session);
            Assert.True(retry.Success, retry.Message);
            var repeated = Assert.Single(await db.Invoices.AsNoTracking().Where(i => !i.IsDeleted).ToListAsync());
            Assert.Equal(invoice.Id, repeated.Id);
            Assert.Equal(invoice.InvoiceDate, repeated.InvoiceDate);
            var stored = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(p => p.Id == profileId);
            var run = Assert.Single(DeserializeRuns(stored.BillingRunsJson));
            Assert.Equal("20260901-20260930", run.RunKey);
            Assert.Equal(new DateOnly(2026, 9, 17), run.ScheduledDate);
            Assert.Equal(RentalBillingScheduleRules.BillingDayModeNoFixedDay, stored.BillingDayMode);
            Assert.Equal(advanceMode, stored.BillingAdvanceMode);
            Assert.Null(service.GetNextBillingDate(stored, new DateOnly(2026, 9, 30)));
            var nextMonth = await service.StartBillingAsync(profileId, new DateOnly(2026, 10, 1), session);
            Assert.True(nextMonth.Success, nextMonth.Message);
            var invoices = await db.Invoices.AsNoTracking().Where(i => !i.IsDeleted).ToListAsync();
            Assert.Equal(2, invoices.Count);
            Assert.All(invoices, i => Assert.Equal(55000m, i.TotalAmount));
            var october = Assert.Single(invoices, i => i.InvoiceDate == new DateOnly(2026, 10, 1));
            Assert.NotEqual(invoice.Id, october.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }
}
