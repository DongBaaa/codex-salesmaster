using SalesMaster.Server.Api.Domain;
using SalesMaster.Server.Api.Security;
using SalesMaster.Server.Api.Services;
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

        if (!await dbContext.CustomerCategories.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            dbContext.CustomerCategories.AddRange(
                new CustomerCategory { Name = "관공서", IsSystemDefault = true },
                new CustomerCategory { Name = "학교", IsSystemDefault = true },
                new CustomerCategory { Name = "기업", IsSystemDefault = true },
                new CustomerCategory { Name = "개인", IsSystemDefault = true });
        }

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
                BankAccountText = "입금은행/계좌번호를 입력하세요"
            });
        }

        if (!await dbContext.PrintTemplates.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            var template = new PrintTemplate
            {
                Name = "거래명세서 기본",
                Description = "WPF 고정 레이아웃 1차 템플릿",
                IsDefault = true
            };

            template.Versions.Add(new PrintTemplateVersion
            {
                PrintTemplateId = template.Id,
                VersionNumber = 1,
                TemplateJson = "{\"layout\":\"fixed-v1\",\"notes\":\"phase2-fastreport-ready\"}",
                IsLocked = false,
                CreatedByName = "system"
            });

            dbContext.PrintTemplates.Add(template);
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
            await dbContext.Payments.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.PrintTemplates.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.PrintTemplateVersions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0
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
}
