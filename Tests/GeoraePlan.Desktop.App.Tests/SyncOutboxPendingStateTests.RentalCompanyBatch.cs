using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UpsertPulledRentalManagementCompanies_BatchRekeyPreservesEveryCompanyAndPendingCustomer(
        bool reverseOrder, bool newCustomer)
    {
        PrepareAppRoot("rental-company-batch-rekey");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var now = DateTime.UtcNow;
            var codes = new[] { OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Itworld, OfficeCodeCatalog.Yeonsu };
            var oldIds = codes.Select(_ => Guid.NewGuid()).ToArray();
            var serverIds = codes.Select(_ => Guid.NewGuid()).ToArray();
            for (var index = 0; index < codes.Length; index++)
                db.RentalManagementCompanies.Add(new LocalRentalManagementCompany
                {
                    Id = oldIds[index], Code = codes[index], Name = "old " + codes[index],
                    IsActive = true, IsDirty = false, Revision = 3,
                    CreatedAtUtc = now.AddHours(-2), UpdatedAtUtc = now.AddHours(-1)
                });
            var customer = new LocalCustomer
            {
                Id = Guid.NewGuid(), NameOriginal = "Pending customer", NameMatchKey = "PENDING CUSTOMER",
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, Notes = "before", IsDirty = true
            };
            if (!newCustomer)
                db.Customers.Add(customer);
            await db.SaveChangesAsync();
            if (newCustomer)
                db.Customers.Add(customer);
            customer.Notes = "preserve this pending change";

            var incoming = codes.Select((code, index) => CreatePulledRentalManagementCompany(
                serverIds[index], code, "server " + code, revision: 7, now)).ToArray();
            if (reverseOrder)
                Array.Reverse(incoming);
            using var sync = CreateSyncService(db, CreateAdminSession());
            await InvokeUpsertPulledRentalManagementCompaniesAsync(sync, incoming);

            await using var verify = new LocalDbContext();
            var companies = await verify.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            Assert.Equal(3, companies.Count);
            for (var index = 0; index < codes.Length; index++)
            {
                var company = Assert.Single(companies, item => item.Code == codes[index]);
                Assert.Equal(serverIds[index], company.Id);
                Assert.Equal("server " + codes[index], company.Name);
                Assert.Equal(7, company.Revision);
                Assert.False(company.IsDirty);
                Assert.DoesNotContain(companies, item => item.Id == oldIds[index]);
            }
            var retainedCustomer = await verify.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == customer.Id);
            Assert.Equal("preserve this pending change", retainedCustomer.Notes);
            Assert.True(retainedCustomer.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
