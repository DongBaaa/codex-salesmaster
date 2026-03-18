using SalesMaster.Server.Api.Domain;
using SalesMaster.Server.Api.Security;
using SalesMaster.Server.Api.Services;
using SalesMaster.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

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

        await EnsureCustomerTradeTypeColumnAsync(dbContext, cancellationToken);
        await EnsureUserOfficeCodeColumnAsync(dbContext, cancellationToken);

        var maxRevision = await GetMaxRevisionAsync(dbContext, cancellationToken);
        revisionClock.Initialize(maxRevision);

        await EnsureSeedUserAsync(
            dbContext,
            username: "admin",
            password: "CHANGE_THIS_ADMIN_PASSWORD",
            role: "Admin",
            officeCode: OfficeCodeCatalog.Usenet,
            grantAllPermissions: true,
            updatePasswordIfExists: false,
            cancellationToken);

        await EnsureSeedUserAsync(
            dbContext,
            username: "user",
            password: "CHANGE_THIS_USER_PASSWORD",
            role: "User",
            officeCode: OfficeCodeCatalog.Yeonsu,
            grantAllPermissions: false,
            updatePasswordIfExists: false,
            cancellationToken);

        await EnsureSeedUserAsync(
            dbContext,
            username: "itw",
            password: "CHANGE_THIS_ITW_PASSWORD",
            role: "User",
            officeCode: OfficeCodeCatalog.Itworld,
            grantAllPermissions: false,
            updatePasswordIfExists: true,
            cancellationToken);

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

    private static async Task EnsureCustomerTradeTypeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Customers\" ADD COLUMN \"TradeType\" TEXT NOT NULL DEFAULT '매출';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Customers\" ADD COLUMN IF NOT EXISTS \"TradeType\" text NOT NULL DEFAULT '매출';",
                    cancellationToken);
            }
        }
        catch
        {
            // Existing databases may already contain the column.
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Customers"
                SET "TradeType" = CASE
                    WHEN COALESCE(TRIM("TradeType"), '') IN ('', '판매', '매출처', '매출') THEN '매출'
                    WHEN COALESCE(TRIM("TradeType"), '') IN ('매입처', '매입') THEN '매입'
                    WHEN COALESCE(TRIM("TradeType"), '') IN ('판매/매입', '매출/매입', '매입/매출') THEN '매출/매입'
                    ELSE TRIM("TradeType")
                END;
                """,
                cancellationToken);
        }
        catch
        {
            // Ignore if legacy schema does not yet expose the column for some reason.
        }
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

    private static async Task EnsureUserOfficeCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Users\" ADD COLUMN \"OfficeCode\" TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"OfficeCode\" text NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}';",
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Users"
                SET "OfficeCode" = CASE
                    WHEN COALESCE(TRIM("OfficeCode"), '') = ''
                        THEN CASE
                            WHEN LOWER(COALESCE(TRIM("Username"), '')) = 'itw' THEN 'ITWORLD'
                            WHEN LOWER(COALESCE(TRIM("Role"), '')) = 'user' THEN 'YEONSU'
                            ELSE 'USENET'
                        END
                    WHEN UPPER(TRIM("OfficeCode")) IN ('USENET', 'UZNET') OR TRIM("OfficeCode") = '유즈넷'
                        THEN 'USENET'
                    WHEN UPPER(TRIM("OfficeCode")) = 'ITWORLD' OR TRIM("OfficeCode") = '아이티월드'
                        THEN 'ITWORLD'
                    WHEN UPPER(TRIM("OfficeCode")) = 'YEONSU' OR TRIM("OfficeCode") IN ('연수구', '연수구 사무실')
                        THEN 'YEONSU'
                    ELSE CASE
                        WHEN LOWER(COALESCE(TRIM("Username"), '')) = 'itw' THEN 'ITWORLD'
                        WHEN LOWER(COALESCE(TRIM("Role"), '')) = 'user' THEN 'YEONSU'
                        ELSE 'USENET'
                    END
                END;
                """,
                cancellationToken);
        }
        catch
        {
        }
    }

    private static string[] AllPermissions() =>
    [
        PermissionNames.CompanyProfileEdit,
        PermissionNames.AmountViewSales,
        PermissionNames.AmountViewPurchase,
        PermissionNames.SettingsEdit,
        PermissionNames.DataBackupRestore,
        PermissionNames.RentalViewAll,
        PermissionNames.RentalEditAll,
        PermissionNames.RentalSettingsEdit,
        PermissionNames.RentalImport
    ];

    private static async Task EnsureSeedUserAsync(
        AppDbContext dbContext,
        string username,
        string password,
        string role,
        string officeCode,
        bool grantAllPermissions,
        bool updatePasswordIfExists,
        CancellationToken cancellationToken)
    {
        var normalizedUsername = username.Trim();
        var user = dbContext.Users.Local.FirstOrDefault(current =>
            string.Equals(current.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));

        if (user is null)
        {
            user = await dbContext.Users.IgnoreQueryFilters()
                .Include(current => current.Permissions)
                .FirstOrDefaultAsync(current => current.Username == normalizedUsername, cancellationToken);
        }

        if (user is null)
        {
            user = new UserAccount
            {
                Username = normalizedUsername,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                Role = role,
                OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode),
                IsActive = true,
                IsDeleted = false
            };
            dbContext.Users.Add(user);
        }
        else
        {
            if (updatePasswordIfExists)
                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);

            user.Role = role;
            user.OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode);
            user.IsActive = true;
            user.IsDeleted = false;
            user.UpdatedAtUtc = DateTime.UtcNow;
        }

        if (grantAllPermissions)
            EnsurePermissions(user, AllPermissions());
    }

    private static void EnsurePermissions(UserAccount user, IEnumerable<string> permissions)
    {
        var existing = user.Permissions
            .Select(permission => permission.Permission)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var permission in permissions)
        {
            if (existing.Contains(permission))
                continue;

            user.Permissions.Add(new UserPermission
            {
                UserId = user.Id,
                Permission = permission,
                User = user
            });
        }
    }

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
                var canonicalName = string.IsNullOrWhiteSpace(canonical.Name)
                    ? definition.Name
                    : DefaultCustomerCategories.NormalizeName(canonical.Name);
                TouchCanonicalCategory(canonical, canonicalName, isSystemDefault: true);
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

