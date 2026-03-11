using SalesMaster.Server.Api.Domain;
using SalesMaster.Server.Api.Security;
using SalesMaster.Server.Api.Services;
using SalesMaster.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace SalesMaster.Server.Api.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var revisionClock = scope.ServiceProvider.GetRequiredService<RevisionClock>();

        if (dbContext.Database.IsSqlite())
        {
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        }
        else
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
        }

        var maxRevision = await GetMaxRevisionAsync(dbContext, cancellationToken);
        revisionClock.Initialize(maxRevision);

        if (!await dbContext.Users.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            var admin = new UserAccount
            {
                Username = "admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("CHANGE_THIS_ADMIN_PASSWORD"),
                Role = "Admin",
                IsActive = true
            };

            foreach (var permission in AllPermissions())
            {
                admin.Permissions.Add(new UserPermission { UserId = admin.Id, Permission = permission });
            }

            var user = new UserAccount
            {
                Username = "user",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("CHANGE_THIS_USER_PASSWORD"),
                Role = "User",
                IsActive = true
            };

            dbContext.Users.AddRange(admin, user);
        }

        await EnsureDefaultCustomerCategoriesAsync(dbContext, cancellationToken);

        if (!await dbContext.Units.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            dbContext.Units.AddRange(
                new Unit { Name = "EA", IsActive = true },
                new Unit { Name = "SET", IsActive = true },
                new Unit { Name = "대", IsActive = true },
                new Unit { Name = "개", IsActive = true },
                new Unit { Name = "박스", IsActive = true });
        }

        if (!await dbContext.CompanyProfiles.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            dbContext.CompanyProfiles.Add(new CompanyProfile
            {
                TradeName = "기본 상호",
                Representative = "대표자",
                BusinessNumber = string.Empty,
                BusinessType = string.Empty,
                BusinessItem = string.Empty,
                Address = string.Empty,
                ContactNumber = string.Empty,
                Email = string.Empty,
                BankAccountText = "입금용 계좌번호를 입력하세요."
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<long> GetMaxRevisionAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var revisions = new List<long>
        {
            await dbContext.Users.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.CompanyProfiles.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.Units.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.CustomerCategories.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.CustomerMasters.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.Customers.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.Items.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.Invoices.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.Payments.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0
        };

        return revisions.Max();
    }

    private static string[] AllPermissions() =>
    [
        PermissionNames.CompanyProfileEdit,
        PermissionNames.AmountViewSales,
        PermissionNames.AmountViewPurchase,
        PermissionNames.SettingsEdit,
        PermissionNames.DataBackupRestore
    ];

    private static async Task EnsureDefaultCustomerCategoriesAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var categories = await dbContext.CustomerCategories.IgnoreQueryFilters().ToListAsync(cancellationToken);

        foreach (var definition in DefaultCustomerCategories.All)
        {
            var canonical = categories.FirstOrDefault(category => category.Id == definition.Id);
            if (canonical is null)
            {
                canonical = new CustomerCategory
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    IsSystemDefault = true,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };

                dbContext.CustomerCategories.Add(canonical);
                categories.Add(canonical);
            }
            else
            {
                TouchCanonicalCategory(canonical, definition.Name, isSystemDefault: true);
            }
        }

        var groups = categories
            .Where(category => !string.IsNullOrWhiteSpace(category.Name))
            .GroupBy(category => DefaultCustomerCategories.NormalizeName(category.Name), StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            var canonical = ResolveCanonicalCategory(group);
            TouchCanonicalCategory(canonical, DefaultCustomerCategories.NormalizeName(canonical.Name), canonical.IsSystemDefault);

            var duplicateIds = group
                .Where(category => category.Id != canonical.Id)
                .Select(category => category.Id)
                .Distinct()
                .ToList();

            if (duplicateIds.Count == 0)
                continue;

            var duplicateIdSet = duplicateIds.ToHashSet();

            var customerMasters = await dbContext.CustomerMasters.IgnoreQueryFilters()
                .Where(customer => customer.CategoryId.HasValue && duplicateIdSet.Contains(customer.CategoryId.Value))
                .ToListAsync(cancellationToken);
            foreach (var customerMaster in customerMasters)
                customerMaster.CategoryId = canonical.Id;

            var customers = await dbContext.Customers.IgnoreQueryFilters()
                .Where(customer => customer.CategoryId.HasValue && duplicateIdSet.Contains(customer.CategoryId.Value))
                .ToListAsync(cancellationToken);
            foreach (var customer in customers)
                customer.CategoryId = canonical.Id;

            foreach (var duplicate in group.Where(category => category.Id != canonical.Id))
                duplicate.IsDeleted = true;
        }
    }

    private static CustomerCategory ResolveCanonicalCategory(IGrouping<string, CustomerCategory> group)
    {
        if (DefaultCustomerCategories.TryGetByName(group.Key, out var definition))
        {
            var fixedCategory = group.FirstOrDefault(category => category.Id == definition.Id);
            if (fixedCategory is not null)
                return fixedCategory;
        }

        return group
            .OrderBy(category => category.IsDeleted)
            .ThenByDescending(category => category.IsSystemDefault)
            .ThenBy(category => category.CreatedAtUtc)
            .ThenBy(category => category.Id)
            .First();
    }

    private static void TouchCanonicalCategory(
        CustomerCategory category,
        string normalizedName,
        bool isSystemDefault)
    {
        category.Name = normalizedName;
        category.IsSystemDefault = category.IsSystemDefault || isSystemDefault;
        category.IsDeleted = false;
    }
}
