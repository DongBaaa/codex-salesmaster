using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.RegularExpressions;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private static readonly Regex SqlIdentifierPattern = new(
        "^[A-Za-z0-9_]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly (Guid Id, string Name, string PriceSource, int SortOrder)[] DefaultPriceGradeOptions =
    [
        (Guid.Parse("1b5ea4f8-ff61-4fc6-ac79-175e2125cba0"), "매출단가", "Sales", 0),
        (Guid.Parse("c8a868c6-3f8d-4e29-a2c9-ec00d68f20a1"), "A_단가 적용", "A", 10),
        (Guid.Parse("b1af9d5e-33e1-4e4c-bf0c-2fb437d4f1c6"), "B_단가 적용", "B", 20),
        (Guid.Parse("8aa3856d-3133-4b38-b7f3-ce83cb2fe82d"), "C_단가 적용", "C", 30),
        (Guid.Parse("2e99b0b8-7f53-4dbc-a3c8-0dce274235a6"), "소매단가", "Retail", 40)
    ];

    private static readonly (Guid Id, string Name, bool AllowsSales, bool AllowsPurchase, int SortOrder)[] DefaultTradeTypeOptions =
    [
        (Guid.Parse("8ce85079-4f9f-49a1-bcd2-dbc653f54025"), "매출", true, false, 0),
        (Guid.Parse("4ab67a47-1b4e-4f17-8b3c-761023c2c3e3"), "매입", false, true, 10),
        (Guid.Parse("9c305d74-3dd4-4fff-9679-dbd4dd6fdb49"), "매출/매입", true, true, 20)
    ];

    public static async Task InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var revisionClock = scope.ServiceProvider.GetRequiredService<RevisionClock>();
        var hostEnvironment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(DbInitializer).FullName ?? nameof(DbInitializer));
        var seedUsersOptions = scope.ServiceProvider.GetRequiredService<IOptions<SeedUsersOptions>>().Value;
        var connectionResolver = scope.ServiceProvider.GetRequiredService<ITenantDatabaseConnectionResolver>();
        var fileStorage = scope.ServiceProvider.GetRequiredService<ICentralFileStorage>();

        await EnsureBusinessDatabaseSchemaAsync(dbContext, cancellationToken);
        await BackfillCustomerScopeFieldsAsync(dbContext, cancellationToken);
        await BackfillCustomerMasterScopeFieldsAsync(dbContext, cancellationToken);

        var dedicatedBusinessConnections = connectionResolver.GetDedicatedBusinessConnections();
        foreach (var connectionInfo in dedicatedBusinessConnections)
        {
            await EnsureDedicatedBusinessDatabaseExistsAsync(connectionInfo, logger, cancellationToken);
            await using var tenantDbContext = CreateDbContext(connectionInfo, revisionClock);
            await EnsureBusinessDatabaseSchemaAsync(tenantDbContext, cancellationToken);
            await BackfillCustomerScopeFieldsAsync(tenantDbContext, cancellationToken);
            await BackfillCustomerMasterScopeFieldsAsync(tenantDbContext, cancellationToken);
        }

        var maxRevision = await GetMaxRevisionAsync(dbContext, cancellationToken);
        foreach (var connectionInfo in dedicatedBusinessConnections)
        {
            await using var tenantDbContext = CreateDbContext(connectionInfo, revisionClock);
            maxRevision = Math.Max(maxRevision, await GetMaxRevisionAsync(tenantDbContext, cancellationToken));
        }

        revisionClock.Initialize(maxRevision);

        if (seedUsersOptions.EnableSeedUsers)
        {
            LogSeedUserWarnings(hostEnvironment, logger, seedUsersOptions);

            await EnsureSeedUserAsync(
                dbContext,
                logger,
                username: "admin",
                password: seedUsersOptions.AdminPassword,
                role: "Admin",
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                scopeType: TenantScopeCatalog.ScopeAdmin,
                grantAllPermissions: true,
                updatePasswordIfExists: false,
                cancellationToken);

            await EnsureSeedUserAsync(
                dbContext,
                logger,
                username: "user",
                password: seedUsersOptions.UserPassword,
                role: "User",
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Yeonsu,
                scopeType: TenantScopeCatalog.ScopeOfficeOnly,
                grantAllPermissions: false,
                updatePasswordIfExists: false,
                cancellationToken);

            await EnsureSeedUserAsync(
                dbContext,
                logger,
                username: "itw",
                password: seedUsersOptions.ItwPassword,
                role: "User",
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Itworld,
                scopeType: TenantScopeCatalog.ScopeTenantAll,
                grantAllPermissions: false,
                updatePasswordIfExists: seedUsersOptions.UpdateExistingItwPassword,
                cancellationToken);
        }
        else
        {
            logger.LogInformation("Seed user creation is disabled by configuration.");
        }

        await EnsureReferenceDataAsync(dbContext, cancellationToken);
        await NormalizeCustomerClassificationIntegrityAsync(dbContext, cancellationToken);
        await NormalizeUnitCatalogAsync(dbContext, cancellationToken);
        await NormalizeInventoryTransferIntegrityAsync(dbContext, cancellationToken);
        await PurgeDeletedInventoryTransferDataAsync(dbContext, cancellationToken);
        await RepairDeletedCustomerRentalProfileLinksAsync(dbContext, cancellationToken);
        await MergeDuplicateCustomerMastersAsync(dbContext, cancellationToken);
        await MergeDuplicateCustomersAsync(dbContext, cancellationToken);
        await MergeBusinessDuplicateCustomersAsync(dbContext, cancellationToken);
        await NormalizeRentalBillingScheduleRulesAsync(dbContext, cancellationToken);
        await NormalizeRentalAssetOfficeOwnershipAsync(dbContext, cancellationToken);
        await MergeDuplicateRentalBillingProfilesAsync(dbContext, cancellationToken);
        await MergeDuplicateRentalAssetsAsync(dbContext, cancellationToken);
        await MergeDuplicateCompanyProfilesAsync(dbContext, cancellationToken);
        await RepairRentalCustomerLinkageAsync(dbContext, cancellationToken);
        await MergeDuplicateItemsAsync(dbContext, cancellationToken);
        await CleanupDeletedInvoiceChainAsync(dbContext, cancellationToken);
        await MigrateStoredFilesToCentralStorageAsync(dbContext, fileStorage, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await EnsureUnitsUniqueIndexAsync(dbContext, cancellationToken);

        foreach (var connectionInfo in dedicatedBusinessConnections)
        {
            await using var tenantDbContext = CreateDbContext(connectionInfo, revisionClock);
            await SyncTenantConfigurationAsync(dbContext, tenantDbContext, cancellationToken);
            await EnsureReferenceDataAsync(tenantDbContext, cancellationToken);
            await NormalizeCustomerClassificationIntegrityAsync(tenantDbContext, cancellationToken);
            await NormalizeUnitCatalogAsync(tenantDbContext, cancellationToken);
            await NormalizeInventoryTransferIntegrityAsync(tenantDbContext, cancellationToken);
            await PurgeDeletedInventoryTransferDataAsync(tenantDbContext, cancellationToken);
            await RepairDeletedCustomerRentalProfileLinksAsync(tenantDbContext, cancellationToken);
            await MergeDuplicateCustomerMastersAsync(tenantDbContext, cancellationToken);
            await MergeDuplicateCustomersAsync(tenantDbContext, cancellationToken);
            await MergeBusinessDuplicateCustomersAsync(tenantDbContext, cancellationToken);
            await NormalizeRentalBillingScheduleRulesAsync(tenantDbContext, cancellationToken);
            await NormalizeRentalAssetOfficeOwnershipAsync(tenantDbContext, cancellationToken);
            await MergeDuplicateRentalBillingProfilesAsync(tenantDbContext, cancellationToken);
            await MergeDuplicateRentalAssetsAsync(tenantDbContext, cancellationToken);
            await MergeDuplicateCompanyProfilesAsync(tenantDbContext, cancellationToken);
            await RepairRentalCustomerLinkageAsync(tenantDbContext, cancellationToken);
            await MergeDuplicateItemsAsync(tenantDbContext, cancellationToken);
            await CleanupDeletedInvoiceChainAsync(tenantDbContext, cancellationToken);
            await MigrateStoredFilesToCentralStorageAsync(tenantDbContext, fileStorage, cancellationToken);
            await tenantDbContext.SaveChangesAsync(cancellationToken);
            await EnsureUnitsUniqueIndexAsync(tenantDbContext, cancellationToken);
            logger.LogInformation("Dedicated business database initialized for tenant {TenantCode}.", connectionInfo.TenantCode);
        }
    }

    private static async Task EnsureBusinessDatabaseSchemaAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureDatabaseSchemaAsync(dbContext, cancellationToken);

        await EnsureCustomerContractsTableAsync(dbContext, cancellationToken);
        await EnsurePaymentAttachmentsTableAsync(dbContext, cancellationToken);
        await EnsureItemWarehouseStocksTableAsync(dbContext, cancellationToken);
        await EnsureTransactionsTableAsync(dbContext, cancellationToken);
        await EnsureTransactionPrepaidDeltaColumnAsync(dbContext, cancellationToken);
        await EnsureTransactionAttachmentsTableAsync(dbContext, cancellationToken);
        await EnsureInventoryTransfersTableAsync(dbContext, cancellationToken);
        await EnsureRentalManagementCompaniesTableAsync(dbContext, cancellationToken);
        await EnsureRentalBillingProfilesTableAsync(dbContext, cancellationToken);
        await EnsureRentalAssetsTableAsync(dbContext, cancellationToken);
        await EnsureRentalBillingEnhancementColumnsAsync(dbContext, cancellationToken);
        await EnsureLegacyRentalNamingColumnsAsync(dbContext, cancellationToken);
        await EnsureRentalBillingLogsTableAsync(dbContext, cancellationToken);
        await EnsureCustomerTradeTypeColumnAsync(dbContext, cancellationToken);
        await EnsureCustomerRepresentativeColumnAsync(dbContext, cancellationToken);
        await EnsureCustomerBusinessTypeColumnAsync(dbContext, cancellationToken);
        await EnsureCustomerBusinessItemColumnAsync(dbContext, cancellationToken);
        await EnsureUserOfficeCodeColumnAsync(dbContext, cancellationToken);
        await EnsureUserTenantScopeColumnsAsync(dbContext, cancellationToken);
        await EnsureTenantDefinitionsTableAsync(dbContext, cancellationToken);
        await EnsureTenantOfficeDefinitionsTableAsync(dbContext, cancellationToken);
        await EnsureDataSharingPoliciesTableAsync(dbContext, cancellationToken);
        await EnsurePriceGradeOptionsTableAsync(dbContext, cancellationToken);
        await EnsureTradeTypeOptionsTableAsync(dbContext, cancellationToken);
        await EnsureItemCategoryOptionsTableAsync(dbContext, cancellationToken);
        await EnsureItemCatalogColumnsAsync(dbContext, cancellationToken);
        await EnsureInvoiceLineOperationalColumnsAsync(dbContext, cancellationToken);
        await EnsureCustomerMasterOfficeCodeColumnAsync(dbContext, cancellationToken);
        await EnsureCustomerMasterTenantCodeColumnAsync(dbContext, cancellationToken);
        await EnsureCustomerOfficeCodeColumnAsync(dbContext, cancellationToken);
        await EnsureCustomerTenantCodeColumnAsync(dbContext, cancellationToken);
        await EnsureItemOfficeCodeColumnAsync(dbContext, cancellationToken);
        await EnsureItemTenantCodeColumnAsync(dbContext, cancellationToken);
        await EnsureInvoiceOfficeCodeColumnAsync(dbContext, cancellationToken);
        await EnsureInvoiceTenantCodeColumnAsync(dbContext, cancellationToken);
        await EnsureInvoiceTaxInvoiceIssuedColumnAsync(dbContext, cancellationToken);
        await EnsureInvoiceVersionColumnsAsync(dbContext, cancellationToken);
        await EnsureRecycleBinPurgeRecordsTableAsync(dbContext, cancellationToken);
        await EnsureCustomerContractStoragePathColumnAsync(dbContext, cancellationToken);
        await EnsurePaymentAttachmentStoragePathColumnAsync(dbContext, cancellationToken);
        await EnsureTransactionAttachmentStoragePathColumnAsync(dbContext, cancellationToken);
    }

    private static async Task BackfillCustomerScopeFieldsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var customers = await dbContext.Customers.IgnoreQueryFilters().ToListAsync(cancellationToken);
        if (customers.Count == 0)
            return;

        var changed = false;
        foreach (var customer in customers)
        {
            var desiredOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                customer.OfficeCode,
                OfficeCodeCatalog.Shared);
            var desiredTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                customer.TenantCode,
                desiredOfficeCode,
                TenantScopeCatalog.UsenetGroup,
                desiredOfficeCode);

            if (!string.Equals(customer.OfficeCode, desiredOfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                customer.OfficeCode = desiredOfficeCode;
                changed = true;
            }

            if (!string.Equals(customer.TenantCode, desiredTenantCode, StringComparison.OrdinalIgnoreCase))
            {
                customer.TenantCode = desiredTenantCode;
                changed = true;
            }
        }

        if (changed)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task BackfillCustomerMasterScopeFieldsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var customerMasters = await dbContext.CustomerMasters.IgnoreQueryFilters().ToListAsync(cancellationToken);
        if (customerMasters.Count == 0)
            return;

        var references = await dbContext.Customers.IgnoreQueryFilters()
            .Where(customer => customer.CustomerMasterId.HasValue)
            .Select(customer => new
            {
                CustomerMasterId = customer.CustomerMasterId!.Value,
                customer.OfficeCode,
                customer.TenantCode
            })
            .ToListAsync(cancellationToken);

        var referenceLookup = references
            .GroupBy(entry => entry.CustomerMasterId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var changed = false;
        foreach (var customerMaster in customerMasters)
        {
            var desiredOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                customerMaster.OfficeCode,
                OfficeCodeCatalog.Shared);
            var desiredTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                customerMaster.TenantCode,
                desiredOfficeCode,
                TenantScopeCatalog.UsenetGroup,
                desiredOfficeCode);

            if (referenceLookup.TryGetValue(customerMaster.Id, out var scopedCustomers) && scopedCustomers.Count > 0)
            {
                var officeCodes = scopedCustomers
                    .Select(entry => OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(entry.OfficeCode, OfficeCodeCatalog.Shared))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                desiredOfficeCode = officeCodes.Count == 1 ? officeCodes[0] : OfficeCodeCatalog.Shared;

                var tenantCodes = scopedCustomers
                    .Select(entry => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entry.TenantCode, entry.OfficeCode))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                desiredTenantCode = tenantCodes.Count == 1
                    ? tenantCodes[0]
                    : TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, desiredOfficeCode);
            }

            if (!string.Equals(customerMaster.OfficeCode, desiredOfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                customerMaster.OfficeCode = desiredOfficeCode;
                changed = true;
            }

            if (!string.Equals(customerMaster.TenantCode, desiredTenantCode, StringComparison.OrdinalIgnoreCase))
            {
                customerMaster.TenantCode = desiredTenantCode;
                changed = true;
            }
        }

        if (changed)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureReferenceDataAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureDefaultTenantConfigurationAsync(dbContext, cancellationToken);
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

        foreach (var definition in DefaultPriceGradeOptions)
        {
            var existing = await dbContext.PriceGradeOptions.IgnoreQueryFilters().FirstOrDefaultAsync(option => option.Id == definition.Id, cancellationToken)
                ?? await dbContext.PriceGradeOptions.IgnoreQueryFilters().FirstOrDefaultAsync(option => option.Name == definition.Name, cancellationToken);
            if (existing is null)
            {
                dbContext.PriceGradeOptions.Add(new PriceGradeOption
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    PriceSource = definition.PriceSource,
                    SortOrder = definition.SortOrder,
                    IsActive = true
                });
                continue;
            }

            existing.Name = definition.Name;
            existing.PriceSource = definition.PriceSource;
            existing.SortOrder = definition.SortOrder;
            existing.IsActive = true;
            existing.IsDeleted = false;
        }

        foreach (var definition in DefaultTradeTypeOptions)
        {
            var existing = await dbContext.TradeTypeOptions.IgnoreQueryFilters().FirstOrDefaultAsync(option => option.Id == definition.Id, cancellationToken)
                ?? await dbContext.TradeTypeOptions.IgnoreQueryFilters().FirstOrDefaultAsync(option => option.Name == definition.Name, cancellationToken);
            if (existing is null)
            {
                dbContext.TradeTypeOptions.Add(new TradeTypeOption
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    AllowsSales = definition.AllowsSales,
                    AllowsPurchase = definition.AllowsPurchase,
                    SortOrder = definition.SortOrder,
                    IsActive = true
                });
                continue;
            }

            existing.Name = definition.Name;
            existing.AllowsSales = definition.AllowsSales;
            existing.AllowsPurchase = definition.AllowsPurchase;
            existing.SortOrder = definition.SortOrder;
            existing.IsActive = true;
            existing.IsDeleted = false;
        }

        var invalidTradeTypeOptions = await dbContext.TradeTypeOptions.IgnoreQueryFilters()
            .Where(option => !option.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var option in invalidTradeTypeOptions.Where(option => CustomerClassificationNormalizer.TradeTypeDefinition.Find(option.Name) is null))
        {
            option.IsDeleted = true;
            option.IsActive = false;
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
    }

    private static async Task NormalizeCustomerClassificationIntegrityAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var customers = await dbContext.Customers.IgnoreQueryFilters().ToListAsync(cancellationToken);
        if (customers.Count == 0)
            return;

        var changed = false;
        foreach (var customer in customers)
        {
            var customerChanged = false;
            var rawTradeType = (customer.TradeType ?? string.Empty).Trim();

            if (CustomerClassificationNormalizer.TryExtractCompositeCategoryAndTradeType(rawTradeType, out var category, out var normalizedCompositeTradeType))
            {
                if (!customer.CategoryId.HasValue || customer.CategoryId == Guid.Empty)
                {
                    customer.CategoryId = category.Id;
                    customerChanged = true;
                }

                if (!string.Equals(customer.TradeType, normalizedCompositeTradeType, StringComparison.CurrentCulture))
                {
                    customer.TradeType = normalizedCompositeTradeType;
                    customerChanged = true;
                }
            }
            else if (CustomerClassificationNormalizer.TryResolveCategory(rawTradeType, out var standaloneCategory))
            {
                if (!customer.CategoryId.HasValue || customer.CategoryId == Guid.Empty)
                {
                    customer.CategoryId = standaloneCategory.Id;
                    customerChanged = true;
                }

                if (!string.Equals(customer.TradeType, CustomerClassificationNormalizer.Sales, StringComparison.CurrentCulture))
                {
                    customer.TradeType = CustomerClassificationNormalizer.Sales;
                    customerChanged = true;
                }
            }
            else if (CustomerClassificationNormalizer.TryNormalizeTradeType(rawTradeType, out var normalizedTradeType) &&
                     !string.Equals(customer.TradeType, normalizedTradeType, StringComparison.CurrentCulture))
            {
                customer.TradeType = normalizedTradeType;
                customerChanged = true;
            }
            else if (!string.IsNullOrWhiteSpace(rawTradeType) &&
                     !CustomerClassificationNormalizer.TryNormalizeTradeType(rawTradeType, out _) &&
                     !string.Equals(customer.TradeType, CustomerClassificationNormalizer.Sales, StringComparison.CurrentCulture))
            {
                customer.TradeType = CustomerClassificationNormalizer.Sales;
                customerChanged = true;
            }

            changed |= customerChanged;
        }

        if (changed)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task SyncTenantConfigurationAsync(
        AppDbContext sourceDbContext,
        AppDbContext targetDbContext,
        CancellationToken cancellationToken)
    {
        var tenantDefinitions = await sourceDbContext.TenantDefinitions.IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        var targetTenantDefinitions = await targetDbContext.TenantDefinitions.IgnoreQueryFilters().ToListAsync(cancellationToken);
        foreach (var source in tenantDefinitions)
        {
            var target = targetTenantDefinitions.FirstOrDefault(current =>
                string.Equals(current.TenantCode, source.TenantCode, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                target = new TenantDefinition();
                targetDbContext.TenantDefinitions.Add(target);
                targetTenantDefinitions.Add(target);
            }

            target.TenantCode = source.TenantCode;
            target.DisplayName = source.DisplayName;
            target.StorageMode = source.StorageMode;
            target.Description = source.Description;
            target.IsActive = source.IsActive;
            target.IsDeleted = source.IsDeleted;
        }

        var officeDefinitions = await sourceDbContext.TenantOfficeDefinitions.IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        var targetOfficeDefinitions = await targetDbContext.TenantOfficeDefinitions.IgnoreQueryFilters().ToListAsync(cancellationToken);
        foreach (var source in officeDefinitions)
        {
            var target = targetOfficeDefinitions.FirstOrDefault(current =>
                string.Equals(current.OfficeCode, source.OfficeCode, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                target = new TenantOfficeDefinition();
                targetDbContext.TenantOfficeDefinitions.Add(target);
                targetOfficeDefinitions.Add(target);
            }

            target.TenantCode = source.TenantCode;
            target.OfficeCode = source.OfficeCode;
            target.DisplayName = source.DisplayName;
            target.IsHeadOffice = source.IsHeadOffice;
            target.IsActive = source.IsActive;
            target.IsDeleted = source.IsDeleted;
        }

        var sharingPolicies = await sourceDbContext.DataSharingPolicies.IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        var targetSharingPolicies = await targetDbContext.DataSharingPolicies.IgnoreQueryFilters().ToListAsync(cancellationToken);
        foreach (var source in sharingPolicies)
        {
            var target = targetSharingPolicies.FirstOrDefault(current => current.Id == source.Id);
            if (target is null)
            {
                target = new DataSharingPolicy { Id = source.Id };
                targetDbContext.DataSharingPolicies.Add(target);
                targetSharingPolicies.Add(target);
            }

            target.SourceTenantCode = source.SourceTenantCode;
            target.SourceOfficeCode = source.SourceOfficeCode;
            target.TargetTenantCode = source.TargetTenantCode;
            target.TargetOfficeCode = source.TargetOfficeCode;
            target.ShareCustomers = source.ShareCustomers;
            target.ShareItems = source.ShareItems;
            target.ShareInvoices = source.ShareInvoices;
            target.SharePayments = source.SharePayments;
            target.ShareContracts = source.ShareContracts;
            target.ShareReports = source.ShareReports;
            target.ShareRentals = source.ShareRentals;
            target.ShareDeliveries = source.ShareDeliveries;
            target.AllowTargetWrite = source.AllowTargetWrite;
            target.Note = source.Note;
            target.IsActive = source.IsActive;
            target.IsDeleted = source.IsDeleted;
        }
    }

    private static AppDbContext CreateDbContext(TenantDatabaseConnectionInfo connectionInfo, RevisionClock revisionClock)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        if (connectionInfo.UseSqlite)
        {
            optionsBuilder.UseSqlite(connectionInfo.ConnectionString);
        }
        else
        {
            optionsBuilder.UseNpgsql(connectionInfo.ConnectionString);
        }

        return new AppDbContext(optionsBuilder.Options, SystemCurrentUserContext.Instance, revisionClock);
    }

    private static async Task EnsureDedicatedBusinessDatabaseExistsAsync(
        TenantDatabaseConnectionInfo connectionInfo,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (connectionInfo.UseSqlite || !connectionInfo.IsDedicatedBusinessDatabase)
            return;

        var targetBuilder = new NpgsqlConnectionStringBuilder(connectionInfo.ConnectionString);
        var targetDatabaseName = targetBuilder.Database?.Trim();
        if (string.IsNullOrWhiteSpace(targetDatabaseName))
            return;

        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionInfo.ConnectionString)
        {
            Database = "postgres"
        };

        await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = "SELECT 1 FROM pg_database WHERE datname = @databaseName";
        existsCommand.Parameters.AddWithValue("databaseName", targetDatabaseName);
        var exists = await existsCommand.ExecuteScalarAsync(cancellationToken) is not null;
        if (exists)
            return;

        var escapedDatabaseName = targetDatabaseName.Replace("\"", "\"\"");
        await using var createCommand = connection.CreateCommand();
        createCommand.CommandText = $"CREATE DATABASE \"{escapedDatabaseName}\"";
        await createCommand.ExecuteNonQueryAsync(cancellationToken);

        logger.LogInformation(
            "Created dedicated business database {DatabaseName} for tenant {TenantCode}.",
            targetDatabaseName,
            connectionInfo.TenantCode);
    }

    private static async Task EnsureDatabaseSchemaAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsSqlite())
        {
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            return;
        }

        var migrationsAssembly = dbContext.Database.GetService<IMigrationsAssembly>();
        if (migrationsAssembly.Migrations.Count > 0)
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
            return;
        }

        var relationalDatabaseCreator = dbContext.Database.GetService<IRelationalDatabaseCreator>();
        if (!await relationalDatabaseCreator.ExistsAsync(cancellationToken))
        {
            await relationalDatabaseCreator.CreateAsync(cancellationToken);
        }

        if (!await HasTableAsync(dbContext, "Users", cancellationToken))
        {
            await relationalDatabaseCreator.CreateTablesAsync(cancellationToken);
        }
    }

    private static async Task<bool> HasTableAsync(
        AppDbContext dbContext,
        string tableName,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            var providerName = dbContext.Database.ProviderName ?? string.Empty;

            command.CommandText = providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
                ? "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @tableName LIMIT 1;"
                : "SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @tableName LIMIT 1;";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@tableName";
            parameter.DbType = DbType.String;
            parameter.Value = tableName;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is not null && result != DBNull.Value;
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureLegacyRentalNamingColumnsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureRenamedTextColumnAsync(
            dbContext,
            "RentalAssets",
            "ProductCategory",
            "ItemCategoryName",
            "TEXT NOT NULL DEFAULT ''",
            "text NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureRenamedTextColumnAsync(
            dbContext,
            "RentalAssets",
            "ModelName",
            "ItemName",
            "TEXT NOT NULL DEFAULT ''",
            "text NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureRenamedTextColumnAsync(
            dbContext,
            "RentalBillingProfiles",
            "ModelName",
            "ItemName",
            "TEXT NOT NULL DEFAULT ''",
            "text NOT NULL DEFAULT ''",
            cancellationToken);
    }

    private static async Task EnsureRenamedTextColumnAsync(
        AppDbContext dbContext,
        string tableName,
        string oldColumnName,
        string newColumnName,
        string sqliteDefinition,
        string postgresDefinition,
        CancellationToken cancellationToken)
    {
        if (!IsSafeSqlIdentifier(tableName) ||
            !IsSafeSqlIdentifier(oldColumnName) ||
            !IsSafeSqlIdentifier(newColumnName))
        {
            return;
        }

        var providerName = dbContext.Database.ProviderName ?? string.Empty;
        var quotedTableName = QuoteSqlIdentifier(tableName);
        var quotedOldColumnName = QuoteSqlIdentifier(oldColumnName);
        var quotedNewColumnName = QuoteSqlIdentifier(newColumnName);
        var hasNewColumn = await HasColumnAsync(dbContext, tableName, newColumnName, cancellationToken);
        var hasOldColumn = await HasColumnAsync(dbContext, tableName, oldColumnName, cancellationToken);

        if (!hasNewColumn && hasOldColumn)
        {
            try
            {
                if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    var renameSql = "ALTER TABLE " + quotedTableName + " RENAME COLUMN " + quotedOldColumnName + " TO " + quotedNewColumnName + ";";
                    await dbContext.Database.ExecuteSqlRawAsync(renameSql, cancellationToken);
                }
                else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
                {
                    var renameSql = "ALTER TABLE " + quotedTableName + " RENAME COLUMN " + quotedOldColumnName + " TO " + quotedNewColumnName + ";";
                    await dbContext.Database.ExecuteSqlRawAsync(renameSql, cancellationToken);
                }
            }
            catch
            {
                // Fallback to copy migration below.
            }

            hasNewColumn = await HasColumnAsync(dbContext, tableName, newColumnName, cancellationToken);
            hasOldColumn = await HasColumnAsync(dbContext, tableName, oldColumnName, cancellationToken);
        }

        if (!hasNewColumn)
        {
            try
            {
                if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    var addSql = "ALTER TABLE " + quotedTableName + " ADD COLUMN " + quotedNewColumnName + " " + sqliteDefinition + ";";
                    await dbContext.Database.ExecuteSqlRawAsync(addSql, cancellationToken);
                }
                else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
                {
                    var addSql = "ALTER TABLE " + quotedTableName + " ADD COLUMN IF NOT EXISTS " + quotedNewColumnName + " " + postgresDefinition + ";";
                    await dbContext.Database.ExecuteSqlRawAsync(addSql, cancellationToken);
                }
            }
            catch
            {
                // Existing databases may already contain the renamed column.
            }

            hasNewColumn = await HasColumnAsync(dbContext, tableName, newColumnName, cancellationToken);
        }

        if (!hasNewColumn || !hasOldColumn)
            return;

        try
        {
            var copySql =
                "UPDATE " + quotedTableName + Environment.NewLine +
                "SET " + quotedNewColumnName + " = CASE" + Environment.NewLine +
                "    WHEN COALESCE(TRIM(" + quotedNewColumnName + "), '') = '' THEN COALESCE(" + quotedOldColumnName + ", '')" + Environment.NewLine +
                "    ELSE " + quotedNewColumnName + Environment.NewLine +
                "END" + Environment.NewLine +
                "WHERE COALESCE(TRIM(" + quotedOldColumnName + "), '') <> '';";
            await dbContext.Database.ExecuteSqlRawAsync(copySql, cancellationToken);
        }
        catch
        {
            // Ignore copy failures on partially migrated databases.
        }
    }

    private static async Task<bool> HasColumnAsync(
        AppDbContext dbContext,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;
        var connection = dbContext.Database.GetDbConnection();
        await using var _ = await EnsureConnectionAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();

        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        }
        else
        {
            command.CommandText = """
                                  SELECT 1
                                  FROM information_schema.columns
                                  WHERE table_schema = 'public'
                                    AND table_name = @tableName
                                    AND column_name = @columnName
                                  LIMIT 1;
                                  """;

            var tableParameter = command.CreateParameter();
            tableParameter.ParameterName = "@tableName";
            tableParameter.DbType = DbType.String;
            tableParameter.Value = tableName;
            command.Parameters.Add(tableParameter);

            var columnParameter = command.CreateParameter();
            columnParameter.ParameterName = "@columnName";
            columnParameter.DbType = DbType.String;
            columnParameter.Value = columnName;
            command.Parameters.Add(columnParameter);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(reader["name"]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private static async Task EnsureTenantDefinitionsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "TenantDefinitions" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0,
                        "TenantCode" TEXT NOT NULL,
                        "DisplayName" TEXT NOT NULL DEFAULT '',
                        "StorageMode" TEXT NOT NULL DEFAULT 'SharedBusinessDatabase',
                        "Description" TEXT NOT NULL DEFAULT '',
                        "IsActive" INTEGER NOT NULL DEFAULT 1
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TenantDefinitions_TenantCode\" ON \"TenantDefinitions\" (\"TenantCode\");",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "TenantDefinitions" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "IsDeleted" boolean NOT NULL DEFAULT FALSE,
                        "CreatedAtUtc" timestamptz NOT NULL,
                        "UpdatedAtUtc" timestamptz NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0,
                        "TenantCode" text NOT NULL,
                        "DisplayName" text NOT NULL DEFAULT '',
                        "StorageMode" text NOT NULL DEFAULT 'SharedBusinessDatabase',
                        "Description" text NOT NULL DEFAULT '',
                        "IsActive" boolean NOT NULL DEFAULT TRUE
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TenantDefinitions_TenantCode\" ON \"TenantDefinitions\" (\"TenantCode\");",
                    cancellationToken);
            }
        }
        catch
        {
            // Table may already exist or provider may not support IF NOT EXISTS in the same way.
        }
    }

    private static async Task EnsureTenantOfficeDefinitionsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "TenantOfficeDefinitions" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0,
                        "TenantCode" TEXT NOT NULL,
                        "OfficeCode" TEXT NOT NULL,
                        "DisplayName" TEXT NOT NULL DEFAULT '',
                        "IsHeadOffice" INTEGER NOT NULL DEFAULT 0,
                        "IsActive" INTEGER NOT NULL DEFAULT 1
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TenantOfficeDefinitions_OfficeCode\" ON \"TenantOfficeDefinitions\" (\"OfficeCode\");",
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TenantOfficeDefinitions_TenantCode_OfficeCode\" ON \"TenantOfficeDefinitions\" (\"TenantCode\", \"OfficeCode\");",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "TenantOfficeDefinitions" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "IsDeleted" boolean NOT NULL DEFAULT FALSE,
                        "CreatedAtUtc" timestamptz NOT NULL,
                        "UpdatedAtUtc" timestamptz NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0,
                        "TenantCode" text NOT NULL,
                        "OfficeCode" text NOT NULL,
                        "DisplayName" text NOT NULL DEFAULT '',
                        "IsHeadOffice" boolean NOT NULL DEFAULT FALSE,
                        "IsActive" boolean NOT NULL DEFAULT TRUE
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TenantOfficeDefinitions_OfficeCode\" ON \"TenantOfficeDefinitions\" (\"OfficeCode\");",
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TenantOfficeDefinitions_TenantCode_OfficeCode\" ON \"TenantOfficeDefinitions\" (\"TenantCode\", \"OfficeCode\");",
                    cancellationToken);
            }
        }
        catch
        {
            // Table may already exist or provider may not support IF NOT EXISTS in the same way.
        }
    }

    private static async Task EnsureDataSharingPoliciesTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "DataSharingPolicies" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0,
                        "SourceTenantCode" TEXT NOT NULL,
                        "SourceOfficeCode" TEXT NOT NULL,
                        "TargetTenantCode" TEXT NOT NULL,
                        "TargetOfficeCode" TEXT NOT NULL,
                        "ShareCustomers" INTEGER NOT NULL DEFAULT 1,
                        "ShareItems" INTEGER NOT NULL DEFAULT 0,
                        "ShareInvoices" INTEGER NOT NULL DEFAULT 1,
                        "SharePayments" INTEGER NOT NULL DEFAULT 1,
                        "ShareContracts" INTEGER NOT NULL DEFAULT 1,
                        "ShareReports" INTEGER NOT NULL DEFAULT 1,
                        "ShareRentals" INTEGER NOT NULL DEFAULT 1,
                        "ShareDeliveries" INTEGER NOT NULL DEFAULT 1,
                        "AllowTargetWrite" INTEGER NOT NULL DEFAULT 0,
                        "Note" TEXT NOT NULL DEFAULT '',
                        "IsActive" INTEGER NOT NULL DEFAULT 1
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE INDEX IF NOT EXISTS "IX_DataSharingPolicies_SourceTarget"
                    ON "DataSharingPolicies" ("SourceTenantCode", "SourceOfficeCode", "TargetTenantCode", "TargetOfficeCode");
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "DataSharingPolicies" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "IsDeleted" boolean NOT NULL DEFAULT FALSE,
                        "CreatedAtUtc" timestamptz NOT NULL,
                        "UpdatedAtUtc" timestamptz NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0,
                        "SourceTenantCode" text NOT NULL,
                        "SourceOfficeCode" text NOT NULL,
                        "TargetTenantCode" text NOT NULL,
                        "TargetOfficeCode" text NOT NULL,
                        "ShareCustomers" boolean NOT NULL DEFAULT TRUE,
                        "ShareItems" boolean NOT NULL DEFAULT FALSE,
                        "ShareInvoices" boolean NOT NULL DEFAULT TRUE,
                        "SharePayments" boolean NOT NULL DEFAULT TRUE,
                        "ShareContracts" boolean NOT NULL DEFAULT TRUE,
                        "ShareReports" boolean NOT NULL DEFAULT TRUE,
                        "ShareRentals" boolean NOT NULL DEFAULT TRUE,
                        "ShareDeliveries" boolean NOT NULL DEFAULT TRUE,
                        "AllowTargetWrite" boolean NOT NULL DEFAULT FALSE,
                        "Note" text NOT NULL DEFAULT '',
                        "IsActive" boolean NOT NULL DEFAULT TRUE
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE INDEX IF NOT EXISTS "IX_DataSharingPolicies_SourceTarget"
                    ON "DataSharingPolicies" ("SourceTenantCode", "SourceOfficeCode", "TargetTenantCode", "TargetOfficeCode");
                    """,
                    cancellationToken);
            }
        }
        catch
        {
            // Table may already exist or provider may not support IF NOT EXISTS in the same way.
        }
    }

    private static async Task EnsurePriceGradeOptionsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "PriceGradeOptions" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0,
                        "Name" TEXT NOT NULL,
                        "PriceSource" TEXT NOT NULL DEFAULT 'Sales',
                        "SortOrder" INTEGER NOT NULL DEFAULT 0,
                        "IsSystemDefault" INTEGER NOT NULL DEFAULT 0,
                        "IsActive" INTEGER NOT NULL DEFAULT 1
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PriceGradeOptions_Name\" ON \"PriceGradeOptions\" (\"Name\");",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "PriceGradeOptions" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "IsDeleted" boolean NOT NULL DEFAULT FALSE,
                        "CreatedAtUtc" timestamptz NOT NULL,
                        "UpdatedAtUtc" timestamptz NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0,
                        "Name" text NOT NULL,
                        "PriceSource" text NOT NULL DEFAULT 'Sales',
                        "SortOrder" integer NOT NULL DEFAULT 0,
                        "IsSystemDefault" boolean NOT NULL DEFAULT FALSE,
                        "IsActive" boolean NOT NULL DEFAULT TRUE
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PriceGradeOptions_Name\" ON \"PriceGradeOptions\" (\"Name\");",
                    cancellationToken);
            }
        }
        catch
        {
            // Table may already exist or provider may not support IF NOT EXISTS in the same way.
        }
    }

    private static async Task EnsureTradeTypeOptionsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "TradeTypeOptions" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0,
                        "Name" TEXT NOT NULL,
                        "AllowsSales" INTEGER NOT NULL DEFAULT 1,
                        "AllowsPurchase" INTEGER NOT NULL DEFAULT 0,
                        "SortOrder" INTEGER NOT NULL DEFAULT 0,
                        "IsSystemDefault" INTEGER NOT NULL DEFAULT 0,
                        "IsActive" INTEGER NOT NULL DEFAULT 1
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TradeTypeOptions_Name\" ON \"TradeTypeOptions\" (\"Name\");",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "TradeTypeOptions" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "IsDeleted" boolean NOT NULL DEFAULT FALSE,
                        "CreatedAtUtc" timestamptz NOT NULL,
                        "UpdatedAtUtc" timestamptz NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0,
                        "Name" text NOT NULL,
                        "AllowsSales" boolean NOT NULL DEFAULT TRUE,
                        "AllowsPurchase" boolean NOT NULL DEFAULT FALSE,
                        "SortOrder" integer NOT NULL DEFAULT 0,
                        "IsSystemDefault" boolean NOT NULL DEFAULT FALSE,
                        "IsActive" boolean NOT NULL DEFAULT TRUE
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TradeTypeOptions_Name\" ON \"TradeTypeOptions\" (\"Name\");",
                    cancellationToken);
            }
        }
        catch
        {
            // Table may already exist or provider may not support IF NOT EXISTS in the same way.
        }
    }

    private static async Task EnsureItemCategoryOptionsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "ItemCategoryOptions" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0,
                        "Name" TEXT NOT NULL,
                        "SortOrder" INTEGER NOT NULL DEFAULT 0,
                        "IsSystemDefault" INTEGER NOT NULL DEFAULT 0,
                        "IsActive" INTEGER NOT NULL DEFAULT 1
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ItemCategoryOptions_Name\" ON \"ItemCategoryOptions\" (\"Name\");",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "ItemCategoryOptions" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "IsDeleted" boolean NOT NULL DEFAULT FALSE,
                        "CreatedAtUtc" timestamptz NOT NULL,
                        "UpdatedAtUtc" timestamptz NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0,
                        "Name" text NOT NULL,
                        "SortOrder" integer NOT NULL DEFAULT 0,
                        "IsSystemDefault" boolean NOT NULL DEFAULT FALSE,
                        "IsActive" boolean NOT NULL DEFAULT TRUE
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ItemCategoryOptions_Name\" ON \"ItemCategoryOptions\" (\"Name\");",
                    cancellationToken);
            }
        }
        catch
        {
            // Table may already exist or provider may not support IF NOT EXISTS in the same way.
        }
    }

    private static async Task EnsureDefaultTenantConfigurationAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await UpsertTenantDefinitionAsync(
            dbContext,
            tenantCode: TenantScopeCatalog.UsenetGroup,
            displayName: "USENET / 연수구",
            storageMode: TenantScopeCatalog.StorageSharedDatabase,
            description: "유즈넷과 연수구는 같은 업체 권역/공용 업무 DB를 사용합니다.",
            cancellationToken);

        await UpsertTenantDefinitionAsync(
            dbContext,
            tenantCode: TenantScopeCatalog.Itworld,
            displayName: "ITWORLD",
            storageMode: TenantScopeCatalog.StorageDedicatedDatabase,
            description: "아이티월드는 별도 업체 권역으로 분리해 운영합니다.",
            cancellationToken);

        await UpsertTenantOfficeDefinitionAsync(dbContext, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet, "유즈넷", true, cancellationToken);
        await UpsertTenantOfficeDefinitionAsync(dbContext, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu, "연수구", false, cancellationToken);
        await UpsertTenantOfficeDefinitionAsync(dbContext, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "아이티월드", true, cancellationToken);

        await UpsertDataSharingPolicyAsync(
            dbContext,
            sourceTenantCode: TenantScopeCatalog.UsenetGroup,
            sourceOfficeCode: OfficeCodeCatalog.Yeonsu,
            targetTenantCode: TenantScopeCatalog.UsenetGroup,
            targetOfficeCode: OfficeCodeCatalog.Usenet,
            shareCustomers: true,
            shareItems: true,
            shareInvoices: true,
            sharePayments: true,
            shareContracts: true,
            shareReports: true,
            shareRentals: true,
            shareDeliveries: true,
            allowTargetWrite: false,
            note: "연수구에서 등록/수정한 데이터는 유즈넷 상급권한 계정에서 조회할 수 있습니다.",
            cancellationToken);

        await UpsertDataSharingPolicyAsync(
            dbContext,
            sourceTenantCode: TenantScopeCatalog.UsenetGroup,
            sourceOfficeCode: OfficeCodeCatalog.Usenet,
            targetTenantCode: TenantScopeCatalog.UsenetGroup,
            targetOfficeCode: OfficeCodeCatalog.Yeonsu,
            shareCustomers: true,
            shareItems: true,
            shareInvoices: true,
            sharePayments: true,
            shareContracts: true,
            shareReports: true,
            shareRentals: true,
            shareDeliveries: true,
            allowTargetWrite: false,
            note: "유즈넷에서 등록/수정한 데이터는 연수구 계정에서도 조회할 수 있습니다.",
            cancellationToken);
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

    private static async Task EnsureCustomerRepresentativeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Customers\" ADD COLUMN \"Representative\" TEXT NOT NULL DEFAULT '';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Customers\" ADD COLUMN IF NOT EXISTS \"Representative\" text NOT NULL DEFAULT '';",
                    cancellationToken);
            }
        }
        catch
        {
            // Column may already exist.
        }
    }

    private static async Task EnsureCustomerBusinessTypeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Customers\" ADD COLUMN \"BusinessType\" TEXT NOT NULL DEFAULT '';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Customers\" ADD COLUMN IF NOT EXISTS \"BusinessType\" text NOT NULL DEFAULT '';",
                    cancellationToken);
            }
        }
        catch
        {
            // Column may already exist.
        }
    }

    private static async Task EnsureCustomerBusinessItemColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Customers\" ADD COLUMN \"BusinessItem\" TEXT NOT NULL DEFAULT '';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Customers\" ADD COLUMN IF NOT EXISTS \"BusinessItem\" text NOT NULL DEFAULT '';",
                    cancellationToken);
            }
        }
        catch
        {
            // Column may already exist.
        }
    }

    private static async Task<long> GetMaxRevisionAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var revisions = new List<long>
        {
            await dbContext.Users.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.CompanyProfiles.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.TenantDefinitions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.TenantOfficeDefinitions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.DataSharingPolicies.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
              await dbContext.Units.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
              await dbContext.CustomerCategories.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
              await dbContext.PriceGradeOptions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
              await dbContext.TradeTypeOptions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
              await dbContext.ItemCategoryOptions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
              await dbContext.CustomerMasters.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
              await dbContext.Customers.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.CustomerContracts.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.Items.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.Transactions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.TransactionAttachments.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.InventoryTransfers.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.RentalManagementCompanies.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.RentalBillingProfiles.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.RentalAssets.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.RentalBillingLogs.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
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

    private static async Task EnsureUserTenantScopeColumnsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Users\" ADD COLUMN \"TenantCode\" TEXT NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}';",
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Users\" ADD COLUMN \"ScopeType\" TEXT NOT NULL DEFAULT '{TenantScopeCatalog.ScopeOfficeOnly}';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"TenantCode\" text NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}';",
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"ScopeType\" text NOT NULL DEFAULT '{TenantScopeCatalog.ScopeOfficeOnly}';",
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"""
                UPDATE "Users"
                SET "TenantCode" = CASE
                    WHEN UPPER(TRIM(COALESCE("OfficeCode", ''))) = '{OfficeCodeCatalog.Itworld}' THEN '{TenantScopeCatalog.Itworld}'
                    ELSE '{TenantScopeCatalog.UsenetGroup}'
                END,
                    "ScopeType" = CASE
                    WHEN UPPER(TRIM(COALESCE("ScopeType", ''))) IN ('OFFICEONLY', 'OFFICE_ONLY', 'OFFICE') THEN '{TenantScopeCatalog.ScopeOfficeOnly}'
                    WHEN UPPER(TRIM(COALESCE("ScopeType", ''))) IN ('TENANTALL', 'TENANT_ALL', 'TENANT', 'COMPANY') THEN '{TenantScopeCatalog.ScopeTenantAll}'
                    WHEN UPPER(TRIM(COALESCE("ScopeType", ''))) = 'ADMIN' THEN '{TenantScopeCatalog.ScopeAdmin}'
                    WHEN UPPER(TRIM(COALESCE("OfficeCode", ''))) = '{OfficeCodeCatalog.Itworld}' THEN '{TenantScopeCatalog.ScopeTenantAll}'
                    WHEN UPPER(TRIM(COALESCE("OfficeCode", ''))) = '{OfficeCodeCatalog.Usenet}' THEN '{TenantScopeCatalog.ScopeTenantAll}'
                    ELSE '{TenantScopeCatalog.ScopeOfficeOnly}'
                END;
                """,
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Users_TenantCode_OfficeCode\" ON \"Users\" (\"TenantCode\", \"OfficeCode\");",
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureCustomerMasterOfficeCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureOfficeScopeColumnAsync(
            dbContext,
            tableName: "CustomerMasters",
            indexName: "IX_CustomerMasters_OfficeCode",
            cancellationToken);
    }

    private static async Task EnsureCustomerMasterTenantCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureTenantScopeColumnAsync(
            dbContext,
            tableName: "CustomerMasters",
            indexName: "IX_CustomerMasters_TenantCode",
            cancellationToken);
    }

    private static async Task EnsureCustomerOfficeCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureOfficeScopeColumnAsync(
            dbContext,
            tableName: "Customers",
            indexName: "IX_Customers_OfficeCode",
            cancellationToken);
    }

    private static async Task EnsureCustomerTenantCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureTenantScopeColumnAsync(
            dbContext,
            tableName: "Customers",
            indexName: "IX_Customers_TenantCode",
            cancellationToken);
    }

    private static async Task EnsureItemOfficeCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureOfficeScopeColumnAsync(
            dbContext,
            tableName: "Items",
            indexName: "IX_Items_OfficeCode",
            cancellationToken);
    }

    private static async Task EnsureItemTenantCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureTenantScopeColumnAsync(
            dbContext,
            tableName: "Items",
            indexName: "IX_Items_TenantCode",
            cancellationToken);
    }

    private static async Task EnsureInvoiceOfficeCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

#pragma warning disable EF1002
        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "Invoices" ADD COLUMN "OfficeCode" TEXT NOT NULL DEFAULT '';
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "Invoices" ADD COLUMN IF NOT EXISTS "OfficeCode" text NOT NULL DEFAULT '';
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"""
                    UPDATE "Invoices"
                    SET "OfficeCode" = CASE
                        WHEN COALESCE(TRIM("OfficeCode"), '') = '' THEN COALESCE((
                            SELECT CASE
                                WHEN COALESCE(TRIM("Customers"."OfficeCode"), '') = '' THEN '{OfficeCodeCatalog.Shared}'
                                WHEN UPPER(TRIM("Customers"."OfficeCode")) IN ('ALL', 'SHARED') OR TRIM("Customers"."OfficeCode") IN ('공용', '전체') THEN '{OfficeCodeCatalog.Shared}'
                                WHEN UPPER(TRIM("Customers"."OfficeCode")) IN ('USENET', 'UZNET') OR TRIM("Customers"."OfficeCode") = '유즈넷' THEN 'USENET'
                                WHEN UPPER(TRIM("Customers"."OfficeCode")) = 'ITWORLD' OR TRIM("Customers"."OfficeCode") = '아이티월드' THEN 'ITWORLD'
                                WHEN UPPER(TRIM("Customers"."OfficeCode")) = 'YEONSU' OR TRIM("Customers"."OfficeCode") IN ('연수구', '연수구 사무실') THEN 'YEONSU'
                                ELSE '{OfficeCodeCatalog.Shared}'
                            END
                            FROM "Customers"
                            WHERE "Customers"."Id" = "Invoices"."CustomerId"
                        ), '{OfficeCodeCatalog.Shared}')
                        WHEN UPPER(TRIM("OfficeCode")) IN ('ALL', 'SHARED') OR TRIM("OfficeCode") IN ('공용', '전체') THEN '{OfficeCodeCatalog.Shared}'
                        WHEN UPPER(TRIM("OfficeCode")) IN ('USENET', 'UZNET') OR TRIM("OfficeCode") = '유즈넷' THEN 'USENET'
                        WHEN UPPER(TRIM("OfficeCode")) = 'ITWORLD' OR TRIM("OfficeCode") = '아이티월드' THEN 'ITWORLD'
                        WHEN UPPER(TRIM("OfficeCode")) = 'YEONSU' OR TRIM("OfficeCode") IN ('연수구', '연수구 사무실') THEN 'YEONSU'
                        ELSE '{OfficeCodeCatalog.Shared}'
                    END;
                    """,
                    cancellationToken);
            }
            else
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"""
                    UPDATE "Invoices" AS invoice
                    SET "OfficeCode" = CASE
                        WHEN COALESCE(TRIM(invoice."OfficeCode"), '') = '' THEN COALESCE(
                            CASE
                                WHEN COALESCE(TRIM(customer."OfficeCode"), '') = '' THEN '{OfficeCodeCatalog.Shared}'
                                WHEN UPPER(TRIM(customer."OfficeCode")) IN ('ALL', 'SHARED') OR TRIM(customer."OfficeCode") IN ('공용', '전체') THEN '{OfficeCodeCatalog.Shared}'
                                WHEN UPPER(TRIM(customer."OfficeCode")) IN ('USENET', 'UZNET') OR TRIM(customer."OfficeCode") = '유즈넷' THEN 'USENET'
                                WHEN UPPER(TRIM(customer."OfficeCode")) = 'ITWORLD' OR TRIM(customer."OfficeCode") = '아이티월드' THEN 'ITWORLD'
                                WHEN UPPER(TRIM(customer."OfficeCode")) = 'YEONSU' OR TRIM(customer."OfficeCode") IN ('연수구', '연수구 사무실') THEN 'YEONSU'
                                ELSE '{OfficeCodeCatalog.Shared}'
                            END,
                            '{OfficeCodeCatalog.Shared}')
                        WHEN UPPER(TRIM(invoice."OfficeCode")) IN ('ALL', 'SHARED') OR TRIM(invoice."OfficeCode") IN ('공용', '전체') THEN '{OfficeCodeCatalog.Shared}'
                        WHEN UPPER(TRIM(invoice."OfficeCode")) IN ('USENET', 'UZNET') OR TRIM(invoice."OfficeCode") = '유즈넷' THEN 'USENET'
                        WHEN UPPER(TRIM(invoice."OfficeCode")) = 'ITWORLD' OR TRIM(invoice."OfficeCode") = '아이티월드' THEN 'ITWORLD'
                        WHEN UPPER(TRIM(invoice."OfficeCode")) = 'YEONSU' OR TRIM(invoice."OfficeCode") IN ('연수구', '연수구 사무실') THEN 'YEONSU'
                        ELSE '{OfficeCodeCatalog.Shared}'
                    END
                    FROM "Customers" AS customer
                    WHERE customer."Id" = invoice."CustomerId";

                    UPDATE "Invoices"
                    SET "OfficeCode" = '{OfficeCodeCatalog.Shared}'
                    WHERE COALESCE(TRIM("OfficeCode"), '') = '';
                    """,
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
                CREATE INDEX IF NOT EXISTS "IX_Invoices_OfficeCode" ON "Invoices" ("OfficeCode");
                """,
                cancellationToken);
        }
        catch
        {
        }
#pragma warning restore EF1002
    }

    private static async Task EnsureInvoiceTenantCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Invoices\" ADD COLUMN \"TenantCode\" TEXT NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"TenantCode\" text NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}';",
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"""
                UPDATE "Invoices"
                SET "TenantCode" = CASE
                    WHEN UPPER(TRIM(COALESCE("OfficeCode", ''))) = '{OfficeCodeCatalog.Itworld}' THEN '{TenantScopeCatalog.Itworld}'
                    ELSE '{TenantScopeCatalog.UsenetGroup}'
                END;
                """,
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Invoices_TenantCode\" ON \"Invoices\" (\"TenantCode\");",
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureOfficeScopeColumnAsync(
        AppDbContext dbContext,
        string tableName,
        string indexName,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

#pragma warning disable EF1002
        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{tableName}\" ADD COLUMN \"OfficeCode\" TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.Shared}';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{tableName}\" ADD COLUMN IF NOT EXISTS \"OfficeCode\" text NOT NULL DEFAULT '{OfficeCodeCatalog.Shared}';",
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"""
                UPDATE "{tableName}"
                SET "OfficeCode" = CASE
                    WHEN COALESCE(TRIM("OfficeCode"), '') = '' THEN '{OfficeCodeCatalog.Shared}'
                    WHEN UPPER(TRIM("OfficeCode")) IN ('ALL', 'SHARED') OR TRIM("OfficeCode") IN ('공용', '전체') THEN '{OfficeCodeCatalog.Shared}'
                    WHEN UPPER(TRIM("OfficeCode")) IN ('USENET', 'UZNET') OR TRIM("OfficeCode") = '유즈넷' THEN 'USENET'
                    WHEN UPPER(TRIM("OfficeCode")) = 'ITWORLD' OR TRIM("OfficeCode") = '아이티월드' THEN 'ITWORLD'
                    WHEN UPPER(TRIM("OfficeCode")) = 'YEONSU' OR TRIM("OfficeCode") IN ('연수구', '연수구 사무실') THEN 'YEONSU'
                    ELSE '{OfficeCodeCatalog.Shared}'
                END;
                """,
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"CREATE INDEX IF NOT EXISTS \"{indexName}\" ON \"{tableName}\" (\"OfficeCode\");",
                cancellationToken);
        }
        catch
        {
        }
#pragma warning restore EF1002
    }

    private static async Task EnsureTenantScopeColumnAsync(
        AppDbContext dbContext,
        string tableName,
        string indexName,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

#pragma warning disable EF1002
        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{tableName}\" ADD COLUMN \"TenantCode\" TEXT NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{tableName}\" ADD COLUMN IF NOT EXISTS \"TenantCode\" text NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}';",
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"""
                UPDATE "{tableName}"
                SET "TenantCode" = CASE
                    WHEN UPPER(TRIM(COALESCE("OfficeCode", ''))) = '{OfficeCodeCatalog.Itworld}' THEN '{TenantScopeCatalog.Itworld}'
                    ELSE '{TenantScopeCatalog.UsenetGroup}'
                END;
                """,
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"CREATE INDEX IF NOT EXISTS \"{indexName}\" ON \"{tableName}\" (\"TenantCode\");",
                cancellationToken);
        }
        catch
        {
        }
#pragma warning restore EF1002
    }

    private static async Task EnsureItemCatalogColumnsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        var columns = new (string Name, string SqliteDefinition, string PostgresDefinition)[]
        {
            ("CategoryName", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''"),
            ("ItemKind", "TEXT NOT NULL DEFAULT '일반상품'", "text NOT NULL DEFAULT '일반상품'"),
            ("TrackingType", "TEXT NOT NULL DEFAULT '재고'", "text NOT NULL DEFAULT '재고'"),
            ("CurrentStock", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("SafetyStock", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("PurchasePrice", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("SalePrice", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("RetailPrice", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("PriceGradeA", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("PriceGradeB", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("PriceGradeC", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("SimpleMemo", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''")
        };

        foreach (var (name, sqliteDefinition, postgresDefinition) in columns)
        {
            try
            {
#pragma warning disable EF1002
                if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    await dbContext.Database.ExecuteSqlRawAsync(
                        $"ALTER TABLE \"Items\" ADD COLUMN \"{name}\" {sqliteDefinition};",
                        cancellationToken);
                }
                else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
                {
                    await dbContext.Database.ExecuteSqlRawAsync(
                        $"ALTER TABLE \"Items\" ADD COLUMN IF NOT EXISTS \"{name}\" {postgresDefinition};",
                        cancellationToken);
                }
#pragma warning restore EF1002
            }
            catch
            {
                // Existing databases may already contain the column.
            }
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Items"
                SET "CategoryName" = COALESCE("CategoryName", ''),
                    "ItemKind" = COALESCE(NULLIF(TRIM("ItemKind"), ''), '일반상품'),
                    "TrackingType" = COALESCE(NULLIF(TRIM("TrackingType"), ''), '재고'),
                    "SimpleMemo" = COALESCE("SimpleMemo", '');
                """,
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Items_CategoryName\" ON \"Items\" (\"CategoryName\");",
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureTransactionPrepaidDeltaColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "Transactions" ADD COLUMN "PrepaidDelta" REAL NOT NULL DEFAULT 0;
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "Transactions" ADD COLUMN IF NOT EXISTS "PrepaidDelta" numeric(18,2) NOT NULL DEFAULT 0;
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }
    }

    private static async Task EnsureInvoiceTaxInvoiceIssuedColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "Invoices" ADD COLUMN "TaxInvoiceIssued" INTEGER NOT NULL DEFAULT 0;
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "Invoices" ADD COLUMN IF NOT EXISTS "TaxInvoiceIssued" boolean NOT NULL DEFAULT false;
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }
    }

    private static async Task EnsureInvoiceVersionColumnsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(dbContext, "Invoices", "LinkedRentalBillingProfileId", "TEXT NULL", "uuid NULL", cancellationToken);
        await EnsureColumnAsync(dbContext, "Invoices", "LinkedRentalBillingRunId", "TEXT NULL", "uuid NULL", cancellationToken);
        await EnsureColumnAsync(dbContext, "Invoices", "VersionGroupId", "TEXT NULL", "uuid NULL", cancellationToken);
        await EnsureColumnAsync(dbContext, "Invoices", "VersionNumber", "INTEGER NOT NULL DEFAULT 1", "integer NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(dbContext, "Invoices", "PreviousVersionId", "TEXT NULL", "uuid NULL", cancellationToken);
        await EnsureColumnAsync(dbContext, "Invoices", "IsLatestVersion", "INTEGER NOT NULL DEFAULT 1", "boolean NOT NULL DEFAULT true", cancellationToken);

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Invoices_VersionGroupId\" ON \"Invoices\" (\"VersionGroupId\");",
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Invoices_IsLatestVersion\" ON \"Invoices\" (\"IsLatestVersion\");",
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Invoices_LinkedRentalBillingProfileId\" ON \"Invoices\" (\"LinkedRentalBillingProfileId\");",
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Invoices_LinkedRentalBillingRunId\" ON \"Invoices\" (\"LinkedRentalBillingRunId\");",
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Invoices"
                SET "VersionGroupId" = "Id"
                WHERE "VersionGroupId" IS NULL;
                """,
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Invoices"
                SET "VersionNumber" = 1
                WHERE "VersionNumber" IS NULL OR "VersionNumber" <= 0;
                """,
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Invoices"
                SET "IsLatestVersion" = TRUE
                WHERE "IsLatestVersion" IS NULL;
                """,
                cancellationToken);
        }
        catch
        {
        }

        var invoices = await dbContext.Invoices
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        if (invoices.Count == 0)
            return;

        var changed = false;
        foreach (var invoice in invoices)
        {
            var desiredGroupId = invoice.VersionGroupId == Guid.Empty ? invoice.Id : invoice.VersionGroupId;
            var desiredVersion = invoice.VersionNumber <= 0 ? 1 : invoice.VersionNumber;

            if (invoice.VersionGroupId != desiredGroupId)
            {
                invoice.VersionGroupId = desiredGroupId;
                changed = true;
            }

            if (invoice.VersionNumber != desiredVersion)
            {
                invoice.VersionNumber = desiredVersion;
                changed = true;
            }
        }

        foreach (var group in invoices.GroupBy(current => current.VersionGroupId == Guid.Empty ? current.Id : current.VersionGroupId))
        {
            var latest = group
                .OrderByDescending(current => current.VersionNumber <= 0 ? 1 : current.VersionNumber)
                .ThenByDescending(current => current.UpdatedAtUtc)
                .ThenByDescending(current => current.Id)
                .First();

            foreach (var invoice in group)
            {
                var shouldBeLatest = invoice.Id == latest.Id;
                if (invoice.IsLatestVersion != shouldBeLatest)
                {
                    invoice.IsLatestVersion = shouldBeLatest;
                    changed = true;
                }
            }
        }

        if (changed)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureRecycleBinPurgeRecordsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "RecycleBinPurgeRecords" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_RecycleBinPurgeRecords" PRIMARY KEY,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0,
                        "Kind" TEXT NOT NULL,
                        "EntityId" TEXT NOT NULL,
                        "TenantCode" TEXT NOT NULL,
                        "OfficeCode" TEXT NOT NULL,
                        "PurgedAtUtc" TEXT NOT NULL
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "RecycleBinPurgeRecords" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "IsDeleted" boolean NOT NULL DEFAULT false,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0,
                        "Kind" text NOT NULL,
                        "EntityId" uuid NOT NULL,
                        "TenantCode" text NOT NULL,
                        "OfficeCode" text NOT NULL,
                        "PurgedAtUtc" timestamp with time zone NOT NULL
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        foreach (var sql in new[]
                 {
                     "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RecycleBinPurgeRecords_Kind_EntityId\" ON \"RecycleBinPurgeRecords\" (\"Kind\", \"EntityId\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_RecycleBinPurgeRecords_TenantCode\" ON \"RecycleBinPurgeRecords\" (\"TenantCode\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_RecycleBinPurgeRecords_OfficeCode\" ON \"RecycleBinPurgeRecords\" (\"OfficeCode\");"
                 })
        {
            try
            {
                await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }
            catch
            {
            }
        }
    }

    private static async Task EnsureInvoiceLineOperationalColumnsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
#pragma warning disable EF1002
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"InvoiceLines\" ADD COLUMN \"ItemTrackingType\" TEXT NOT NULL DEFAULT '재고';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"InvoiceLines\" ADD COLUMN IF NOT EXISTS \"ItemTrackingType\" text NOT NULL DEFAULT '재고';",
                    cancellationToken);
            }
#pragma warning restore EF1002
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "InvoiceLines"
                SET "ItemTrackingType" = COALESCE(NULLIF(TRIM("ItemTrackingType"), ''), '재고');
                """,
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureCustomerContractsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "CustomerContracts" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_CustomerContracts" PRIMARY KEY,
                        "CustomerId" TEXT NOT NULL,
                        "ContractType" TEXT NOT NULL DEFAULT '거래계약서',
                        "FileName" TEXT NOT NULL DEFAULT '',
                        "MimeType" TEXT NOT NULL DEFAULT 'application/pdf',
                        "FileSize" INTEGER NOT NULL DEFAULT 0,
                        "FileHash" TEXT NOT NULL DEFAULT '',
                        "Description" TEXT NOT NULL DEFAULT '',
                        "SignedDate" TEXT NULL,
                        "ExpireDate" TEXT NULL,
                        "IsPrimary" INTEGER NOT NULL DEFAULT 0,
                        "UploadedByUsername" TEXT NOT NULL DEFAULT '',
                        "UploadedAtUtc" TEXT NOT NULL,
                        "FileContent" BLOB NOT NULL DEFAULT X'',
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "CustomerContracts" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "CustomerId" uuid NOT NULL,
                        "ContractType" text NOT NULL DEFAULT '거래계약서',
                        "FileName" text NOT NULL DEFAULT '',
                        "MimeType" text NOT NULL DEFAULT 'application/pdf',
                        "FileSize" bigint NOT NULL DEFAULT 0,
                        "FileHash" text NOT NULL DEFAULT '',
                        "Description" text NOT NULL DEFAULT '',
                        "SignedDate" date NULL,
                        "ExpireDate" date NULL,
                        "IsPrimary" boolean NOT NULL DEFAULT false,
                        "UploadedByUsername" text NOT NULL DEFAULT '',
                        "UploadedAtUtc" timestamp with time zone NOT NULL,
                        "FileContent" bytea NOT NULL DEFAULT ''::bytea,
                        "IsDeleted" boolean NOT NULL DEFAULT false,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IF NOT EXISTS \"IX_CustomerContracts_CustomerId\" ON \"CustomerContracts\" (\"CustomerId\");",
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IF NOT EXISTS \"IX_CustomerContracts_CustomerId_IsPrimary\" ON \"CustomerContracts\" (\"CustomerId\", \"IsPrimary\");",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IF NOT EXISTS \"IX_CustomerContracts_CustomerId\" ON \"CustomerContracts\" (\"CustomerId\");",
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IF NOT EXISTS \"IX_CustomerContracts_CustomerId_IsPrimary\" ON \"CustomerContracts\" (\"CustomerId\", \"IsPrimary\");",
                    cancellationToken);
            }
        }
        catch
        {
        }
    }

    private static async Task EnsurePaymentAttachmentsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "PaymentAttachments" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_PaymentAttachments" PRIMARY KEY,
                        "PaymentId" TEXT NOT NULL,
                        "AttachmentType" TEXT NOT NULL DEFAULT '내역첨부',
                        "FileName" TEXT NOT NULL DEFAULT '',
                        "MimeType" TEXT NOT NULL DEFAULT '',
                        "FileSize" INTEGER NOT NULL DEFAULT 0,
                        "FileHash" TEXT NOT NULL DEFAULT '',
                        "Description" TEXT NOT NULL DEFAULT '',
                        "UploadedAtUtc" TEXT NOT NULL,
                        "FileContent" BLOB NOT NULL DEFAULT X'',
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "PaymentAttachments" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "PaymentId" uuid NOT NULL,
                        "AttachmentType" text NOT NULL DEFAULT '내역첨부',
                        "FileName" text NOT NULL DEFAULT '',
                        "MimeType" text NOT NULL DEFAULT '',
                        "FileSize" bigint NOT NULL DEFAULT 0,
                        "FileHash" text NOT NULL DEFAULT '',
                        "Description" text NOT NULL DEFAULT '',
                        "UploadedAtUtc" timestamp with time zone NOT NULL,
                        "FileContent" bytea NOT NULL DEFAULT ''::bytea,
                        "IsDeleted" boolean NOT NULL DEFAULT false,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_PaymentAttachments_PaymentId\" ON \"PaymentAttachments\" (\"PaymentId\");",
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureItemWarehouseStocksTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "ItemWarehouseStocks" (
                        "ItemId" TEXT NOT NULL,
                        "WarehouseCode" TEXT NOT NULL,
                        "Quantity" REAL NOT NULL DEFAULT 0,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        CONSTRAINT "PK_ItemWarehouseStocks" PRIMARY KEY ("ItemId", "WarehouseCode")
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "ItemWarehouseStocks" (
                        "ItemId" uuid NOT NULL,
                        "WarehouseCode" text NOT NULL,
                        "Quantity" numeric(18,2) NOT NULL DEFAULT 0,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        CONSTRAINT "PK_ItemWarehouseStocks" PRIMARY KEY ("ItemId", "WarehouseCode")
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_ItemWarehouseStocks_WarehouseCode\" ON \"ItemWarehouseStocks\" (\"WarehouseCode\");",
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureTransactionsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "Transactions" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_Transactions" PRIMARY KEY,
                        "CustomerId" TEXT NOT NULL,
                        "TenantCode" TEXT NOT NULL DEFAULT 'USENET_GROUP',
                        "OfficeCode" TEXT NOT NULL DEFAULT 'ALL',
                        "TransactionDate" TEXT NOT NULL,
                        "TransactionKind" TEXT NOT NULL DEFAULT '',
                        "LinkedInvoiceId" TEXT NULL,
                        "LinkedInvoiceNumber" TEXT NOT NULL DEFAULT '',
                        "LinkedRentalBillingProfileId" TEXT NULL,
                        "SettlementAmount" REAL NOT NULL DEFAULT 0,
                        "AdvanceDelta" REAL NOT NULL DEFAULT 0,
                        "PrepaidDelta" REAL NOT NULL DEFAULT 0,
                        "CashReceipt" REAL NOT NULL DEFAULT 0,
                        "CardReceipt" REAL NOT NULL DEFAULT 0,
                        "BankReceipt" REAL NOT NULL DEFAULT 0,
                        "DiscountApplied" REAL NOT NULL DEFAULT 0,
                        "ReceiptTotal" REAL NOT NULL DEFAULT 0,
                        "CashPayment" REAL NOT NULL DEFAULT 0,
                        "CardPayment" REAL NOT NULL DEFAULT 0,
                        "BankPayment" REAL NOT NULL DEFAULT 0,
                        "DiscountReceived" REAL NOT NULL DEFAULT 0,
                        "PaymentTotal" REAL NOT NULL DEFAULT 0,
                        "Note" TEXT NOT NULL DEFAULT '',
                        "Memo" TEXT NOT NULL DEFAULT '',
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "Transactions" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "CustomerId" uuid NOT NULL,
                        "TenantCode" text NOT NULL DEFAULT 'USENET_GROUP',
                        "OfficeCode" text NOT NULL DEFAULT 'ALL',
                        "TransactionDate" date NOT NULL,
                        "TransactionKind" text NOT NULL DEFAULT '',
                        "LinkedInvoiceId" uuid NULL,
                        "LinkedInvoiceNumber" text NOT NULL DEFAULT '',
                        "LinkedRentalBillingProfileId" uuid NULL,
                        "SettlementAmount" numeric(18,2) NOT NULL DEFAULT 0,
                        "AdvanceDelta" numeric(18,2) NOT NULL DEFAULT 0,
                        "PrepaidDelta" numeric(18,2) NOT NULL DEFAULT 0,
                        "CashReceipt" numeric(18,2) NOT NULL DEFAULT 0,
                        "CardReceipt" numeric(18,2) NOT NULL DEFAULT 0,
                        "BankReceipt" numeric(18,2) NOT NULL DEFAULT 0,
                        "DiscountApplied" numeric(18,2) NOT NULL DEFAULT 0,
                        "ReceiptTotal" numeric(18,2) NOT NULL DEFAULT 0,
                        "CashPayment" numeric(18,2) NOT NULL DEFAULT 0,
                        "CardPayment" numeric(18,2) NOT NULL DEFAULT 0,
                        "BankPayment" numeric(18,2) NOT NULL DEFAULT 0,
                        "DiscountReceived" numeric(18,2) NOT NULL DEFAULT 0,
                        "PaymentTotal" numeric(18,2) NOT NULL DEFAULT 0,
                        "Note" text NOT NULL DEFAULT '',
                        "Memo" text NOT NULL DEFAULT '',
                        "IsDeleted" boolean NOT NULL DEFAULT false,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        foreach (var sql in new[]
                 {
                     "CREATE INDEX IF NOT EXISTS \"IX_Transactions_CustomerId\" ON \"Transactions\" (\"CustomerId\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_Transactions_TenantCode\" ON \"Transactions\" (\"TenantCode\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_Transactions_OfficeCode\" ON \"Transactions\" (\"OfficeCode\");"
                 })
        {
            try { await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken); } catch { }
        }
    }

    private static async Task EnsureTransactionAttachmentsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "TransactionAttachments" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_TransactionAttachments" PRIMARY KEY,
                        "TransactionId" TEXT NOT NULL,
                        "AttachmentType" TEXT NOT NULL DEFAULT '기타',
                        "FileName" TEXT NOT NULL DEFAULT '',
                        "MimeType" TEXT NOT NULL DEFAULT '',
                        "FileSize" INTEGER NOT NULL DEFAULT 0,
                        "FileHash" TEXT NOT NULL DEFAULT '',
                        "Description" TEXT NOT NULL DEFAULT '',
                        "UploadedByUsername" TEXT NOT NULL DEFAULT '',
                        "UploadedAtUtc" TEXT NOT NULL,
                        "VerificationStatus" TEXT NOT NULL DEFAULT '미확인',
                        "VerifiedByUsername" TEXT NOT NULL DEFAULT '',
                        "VerifiedAtUtc" TEXT NULL,
                        "VerificationMemo" TEXT NOT NULL DEFAULT '',
                        "SortOrder" INTEGER NOT NULL DEFAULT 0,
                        "FileContent" BLOB NOT NULL DEFAULT X'',
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "TransactionAttachments" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "TransactionId" uuid NOT NULL,
                        "AttachmentType" text NOT NULL DEFAULT '기타',
                        "FileName" text NOT NULL DEFAULT '',
                        "MimeType" text NOT NULL DEFAULT '',
                        "FileSize" bigint NOT NULL DEFAULT 0,
                        "FileHash" text NOT NULL DEFAULT '',
                        "Description" text NOT NULL DEFAULT '',
                        "UploadedByUsername" text NOT NULL DEFAULT '',
                        "UploadedAtUtc" timestamp with time zone NOT NULL,
                        "VerificationStatus" text NOT NULL DEFAULT '미확인',
                        "VerifiedByUsername" text NOT NULL DEFAULT '',
                        "VerifiedAtUtc" timestamp with time zone NULL,
                        "VerificationMemo" text NOT NULL DEFAULT '',
                        "SortOrder" integer NOT NULL DEFAULT 0,
                        "FileContent" bytea NOT NULL DEFAULT ''::bytea,
                        "IsDeleted" boolean NOT NULL DEFAULT false,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_TransactionAttachments_TransactionId\" ON \"TransactionAttachments\" (\"TransactionId\");",
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureInventoryTransfersTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "InventoryTransfers" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_InventoryTransfers" PRIMARY KEY,
                        "TenantCode" TEXT NOT NULL DEFAULT 'USENET_GROUP',
                        "SourceOfficeCode" TEXT NOT NULL DEFAULT 'USENET',
                        "TargetOfficeCode" TEXT NOT NULL DEFAULT 'YEONSU',
                        "TransferNumber" TEXT NOT NULL DEFAULT '',
                        "TransferDate" TEXT NOT NULL,
                        "FromWarehouseCode" TEXT NOT NULL DEFAULT '',
                        "ToWarehouseCode" TEXT NOT NULL DEFAULT '',
                        "Memo" TEXT NOT NULL DEFAULT '',
                        "CreatedByUsername" TEXT NOT NULL DEFAULT '',
                        "LastSavedByUsername" TEXT NOT NULL DEFAULT '',
                        "LastSavedAtUtc" TEXT NOT NULL,
                        "TransferStatus" TEXT NOT NULL DEFAULT '수령대기',
                        "RequestedByUsername" TEXT NOT NULL DEFAULT '',
                        "RequestedAtUtc" TEXT NULL,
                        "ReceivedByUsername" TEXT NOT NULL DEFAULT '',
                        "ReceivedAtUtc" TEXT NULL,
                        "ReceiveMemo" TEXT NOT NULL DEFAULT '',
                        "ReceiveEvidencePath" TEXT NOT NULL DEFAULT '',
                        "RejectedByUsername" TEXT NOT NULL DEFAULT '',
                        "RejectedAtUtc" TEXT NULL,
                        "RejectReason" TEXT NOT NULL DEFAULT '',
                        "LastStatusChangedByUsername" TEXT NOT NULL DEFAULT '',
                        "LastStatusChangedAtUtc" TEXT NULL,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);

                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "InventoryTransferLines" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_InventoryTransferLines" PRIMARY KEY,
                        "TransferId" TEXT NOT NULL,
                        "ItemId" TEXT NULL,
                        "ItemNameOriginal" TEXT NOT NULL DEFAULT '',
                        "SpecificationOriginal" TEXT NOT NULL DEFAULT '',
                        "Unit" TEXT NOT NULL DEFAULT '',
                        "Quantity" REAL NOT NULL DEFAULT 0,
                        "ReceivedQuantity" REAL NULL,
                        "QuantityDifference" REAL NULL,
                        "Remark" TEXT NOT NULL DEFAULT '',
                        "ReceiptRemark" TEXT NOT NULL DEFAULT '',
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "InventoryTransfers" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "TenantCode" text NOT NULL DEFAULT 'USENET_GROUP',
                        "SourceOfficeCode" text NOT NULL DEFAULT 'USENET',
                        "TargetOfficeCode" text NOT NULL DEFAULT 'YEONSU',
                        "TransferNumber" text NOT NULL DEFAULT '',
                        "TransferDate" date NOT NULL,
                        "FromWarehouseCode" text NOT NULL DEFAULT '',
                        "ToWarehouseCode" text NOT NULL DEFAULT '',
                        "Memo" text NOT NULL DEFAULT '',
                        "CreatedByUsername" text NOT NULL DEFAULT '',
                        "LastSavedByUsername" text NOT NULL DEFAULT '',
                        "LastSavedAtUtc" timestamp with time zone NOT NULL,
                        "TransferStatus" text NOT NULL DEFAULT '수령대기',
                        "RequestedByUsername" text NOT NULL DEFAULT '',
                        "RequestedAtUtc" timestamp with time zone NULL,
                        "ReceivedByUsername" text NOT NULL DEFAULT '',
                        "ReceivedAtUtc" timestamp with time zone NULL,
                        "ReceiveMemo" text NOT NULL DEFAULT '',
                        "ReceiveEvidencePath" text NOT NULL DEFAULT '',
                        "RejectedByUsername" text NOT NULL DEFAULT '',
                        "RejectedAtUtc" timestamp with time zone NULL,
                        "RejectReason" text NOT NULL DEFAULT '',
                        "LastStatusChangedByUsername" text NOT NULL DEFAULT '',
                        "LastStatusChangedAtUtc" timestamp with time zone NULL,
                        "IsDeleted" boolean NOT NULL DEFAULT false,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);

                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "InventoryTransferLines" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "TransferId" uuid NOT NULL,
                        "ItemId" uuid NULL,
                        "ItemNameOriginal" text NOT NULL DEFAULT '',
                        "SpecificationOriginal" text NOT NULL DEFAULT '',
                        "Unit" text NOT NULL DEFAULT '',
                        "Quantity" numeric(18,2) NOT NULL DEFAULT 0,
                        "ReceivedQuantity" numeric(18,2) NULL,
                        "QuantityDifference" numeric(18,2) NULL,
                        "Remark" text NOT NULL DEFAULT '',
                        "ReceiptRemark" text NOT NULL DEFAULT '',
                        "IsDeleted" boolean NOT NULL DEFAULT false
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        foreach (var sql in new[]
                 {
                     "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransfers_TenantCode\" ON \"InventoryTransfers\" (\"TenantCode\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransfers_SourceOfficeCode\" ON \"InventoryTransfers\" (\"SourceOfficeCode\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransfers_TargetOfficeCode\" ON \"InventoryTransfers\" (\"TargetOfficeCode\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransfers_TransferNumber\" ON \"InventoryTransfers\" (\"TransferNumber\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransferLines_TransferId\" ON \"InventoryTransferLines\" (\"TransferId\");"
                 })
        {
            try { await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken); } catch { }
        }
    }

    private static async Task EnsureRentalManagementCompaniesTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "RentalManagementCompanies" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_RentalManagementCompanies" PRIMARY KEY,
                        "TenantCode" TEXT NOT NULL DEFAULT 'USENET_GROUP',
                        "Code" TEXT NOT NULL DEFAULT '',
                        "Name" TEXT NOT NULL DEFAULT '',
                        "IsSystemDefault" INTEGER NOT NULL DEFAULT 0,
                        "IsActive" INTEGER NOT NULL DEFAULT 1,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "RentalManagementCompanies" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "TenantCode" text NOT NULL DEFAULT 'USENET_GROUP',
                        "Code" text NOT NULL DEFAULT '',
                        "Name" text NOT NULL DEFAULT '',
                        "IsSystemDefault" boolean NOT NULL DEFAULT false,
                        "IsActive" boolean NOT NULL DEFAULT true,
                        "IsDeleted" boolean NOT NULL DEFAULT false,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RentalManagementCompanies_TenantCode_Code\" ON \"RentalManagementCompanies\" (\"TenantCode\", \"Code\");",
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureRentalBillingProfilesTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "RentalBillingProfiles" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_RentalBillingProfiles" PRIMARY KEY,
                        "TenantCode" TEXT NOT NULL DEFAULT 'USENET_GROUP',
                        "OfficeCode" TEXT NOT NULL DEFAULT 'ALL',
                        "ProfileKey" TEXT NOT NULL DEFAULT '',
                        "CustomerId" TEXT NULL,
                        "CustomerName" TEXT NOT NULL DEFAULT '',
                        "BusinessNumber" TEXT NOT NULL DEFAULT '',
                        "RealCustomerName" TEXT NOT NULL DEFAULT '',
                        "ItemName" TEXT NOT NULL DEFAULT '',
                        "ManagementCompanyCode" TEXT NOT NULL DEFAULT '',
                        "BillingMethod" TEXT NOT NULL DEFAULT '',
                        "PaymentMethod" TEXT NOT NULL DEFAULT '',
                        "BillingStatus" TEXT NOT NULL DEFAULT '',
                        "Email" TEXT NOT NULL DEFAULT '',
                        "BillingDay" INTEGER NOT NULL DEFAULT 25,
                        "BillingDayMode" TEXT NOT NULL DEFAULT '고정일',
                        "BillingCycleMonths" INTEGER NOT NULL DEFAULT 1,
                        "BillingAnchorMonth" INTEGER NOT NULL DEFAULT 3,
                        "DocumentIssueMode" TEXT NOT NULL DEFAULT '결제일과 동일',
                        "DocumentLeadDays" INTEGER NOT NULL DEFAULT 0,
                        "MonthlyAmount" REAL NOT NULL DEFAULT 0,
                        "DepositAmount" REAL NOT NULL DEFAULT 0,
                        "SubmissionDocuments" TEXT NOT NULL DEFAULT '',
                        "Notes" TEXT NOT NULL DEFAULT '',
                        "BillingAnchorDate" TEXT NULL,
                        "ContractDate" TEXT NULL,
                        "ContractStartDate" TEXT NULL,
                        "ContractEndDate" TEXT NULL,
                        "LastBilledDate" TEXT NULL,
                        "SettlementStatus" TEXT NOT NULL DEFAULT '',
                        "CompletionStatus" TEXT NOT NULL DEFAULT '',
                        "SettledAmount" REAL NOT NULL DEFAULT 0,
                        "OutstandingAmount" REAL NOT NULL DEFAULT 0,
                        "RequiresFollowUp" INTEGER NOT NULL DEFAULT 0,
                        "FollowUpNote" TEXT NOT NULL DEFAULT '',
                        "LastSettledDate" TEXT NULL,
                        "AssignedUsername" TEXT NOT NULL DEFAULT '',
                        "IsActive" INTEGER NOT NULL DEFAULT 1,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "RentalBillingProfiles" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "TenantCode" text NOT NULL DEFAULT 'USENET_GROUP',
                        "OfficeCode" text NOT NULL DEFAULT 'ALL',
                        "ProfileKey" text NOT NULL DEFAULT '',
                        "CustomerId" uuid NULL,
                        "CustomerName" text NOT NULL DEFAULT '',
                        "BusinessNumber" text NOT NULL DEFAULT '',
                        "RealCustomerName" text NOT NULL DEFAULT '',
                        "ItemName" text NOT NULL DEFAULT '',
                        "ManagementCompanyCode" text NOT NULL DEFAULT '',
                        "BillingMethod" text NOT NULL DEFAULT '',
                        "PaymentMethod" text NOT NULL DEFAULT '',
                        "BillingStatus" text NOT NULL DEFAULT '',
                        "Email" text NOT NULL DEFAULT '',
                        "BillingDay" integer NOT NULL DEFAULT 25,
                        "BillingDayMode" text NOT NULL DEFAULT '고정일',
                        "BillingCycleMonths" integer NOT NULL DEFAULT 1,
                        "BillingAnchorMonth" integer NOT NULL DEFAULT 3,
                        "DocumentIssueMode" text NOT NULL DEFAULT '결제일과 동일',
                        "DocumentLeadDays" integer NOT NULL DEFAULT 0,
                        "MonthlyAmount" numeric(18,2) NOT NULL DEFAULT 0,
                        "DepositAmount" numeric(18,2) NOT NULL DEFAULT 0,
                        "SubmissionDocuments" text NOT NULL DEFAULT '',
                        "Notes" text NOT NULL DEFAULT '',
                        "BillingAnchorDate" date NULL,
                        "ContractDate" date NULL,
                        "ContractStartDate" date NULL,
                        "ContractEndDate" date NULL,
                        "LastBilledDate" date NULL,
                        "SettlementStatus" text NOT NULL DEFAULT '',
                        "CompletionStatus" text NOT NULL DEFAULT '',
                        "SettledAmount" numeric(18,2) NOT NULL DEFAULT 0,
                        "OutstandingAmount" numeric(18,2) NOT NULL DEFAULT 0,
                        "RequiresFollowUp" boolean NOT NULL DEFAULT false,
                        "FollowUpNote" text NOT NULL DEFAULT '',
                        "LastSettledDate" date NULL,
                        "AssignedUsername" text NOT NULL DEFAULT '',
                        "IsActive" boolean NOT NULL DEFAULT true,
                        "IsDeleted" boolean NOT NULL DEFAULT false,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        foreach (var sql in new[]
                 {
                     "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RentalBillingProfiles_TenantCode_ProfileKey\" ON \"RentalBillingProfiles\" (\"TenantCode\", \"ProfileKey\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_RentalBillingProfiles_OfficeCode\" ON \"RentalBillingProfiles\" (\"OfficeCode\");"
                 })
        {
            try { await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken); } catch { }
        }
    }

    private static async Task EnsureRentalAssetsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "RentalAssets" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_RentalAssets" PRIMARY KEY,
                        "TenantCode" TEXT NOT NULL DEFAULT 'USENET_GROUP',
                        "OfficeCode" TEXT NOT NULL DEFAULT 'ALL',
                        "AssetKey" TEXT NOT NULL DEFAULT '',
                        "CustomerId" TEXT NULL,
                        "ItemId" TEXT NULL,
                        "BillingProfileId" TEXT NULL,
                        "ManagementId" TEXT NOT NULL DEFAULT '',
                        "ManagementNumber" TEXT NOT NULL DEFAULT '',
                        "ManagementCompanyCode" TEXT NOT NULL DEFAULT '',
                        "CurrentLocation" TEXT NOT NULL DEFAULT '',
                        "ItemCategoryName" TEXT NOT NULL DEFAULT '',
                        "Manufacturer" TEXT NOT NULL DEFAULT '',
                        "ItemName" TEXT NOT NULL DEFAULT '',
                        "MachineNumber" TEXT NOT NULL DEFAULT '',
                        "PurchaseVendor" TEXT NOT NULL DEFAULT '',
                        "PurchaseDate" TEXT NULL,
                        "DisposalDate" TEXT NULL,
                        "PurchasePrice" REAL NOT NULL DEFAULT 0,
                        "SalePrice" REAL NOT NULL DEFAULT 0,
                        "CustomerName" TEXT NOT NULL DEFAULT '',
                        "InstallLocation" TEXT NOT NULL DEFAULT '',
                        "DepositText" TEXT NOT NULL DEFAULT '',
                        "MonthlyFee" REAL NOT NULL DEFAULT 0,
                        "ContractMonths" INTEGER NOT NULL DEFAULT 0,
                        "ContractDate" TEXT NULL,
                        "InstallDate" TEXT NULL,
                        "ContractStartDate" TEXT NULL,
                        "RentalEndDate" TEXT NULL,
                        "FreeSupplyItems" TEXT NOT NULL DEFAULT '',
                        "PaidSupplyItems" TEXT NOT NULL DEFAULT '',
                        "AssignedUsername" TEXT NOT NULL DEFAULT '',
                        "AssetStatus" TEXT NOT NULL DEFAULT '',
                        "Notes" TEXT NOT NULL DEFAULT '',
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "RentalAssets" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "TenantCode" text NOT NULL DEFAULT 'USENET_GROUP',
                        "OfficeCode" text NOT NULL DEFAULT 'ALL',
                        "AssetKey" text NOT NULL DEFAULT '',
                        "CustomerId" uuid NULL,
                        "ItemId" uuid NULL,
                        "BillingProfileId" uuid NULL,
                        "ManagementId" text NOT NULL DEFAULT '',
                        "ManagementNumber" text NOT NULL DEFAULT '',
                        "ManagementCompanyCode" text NOT NULL DEFAULT '',
                        "CurrentLocation" text NOT NULL DEFAULT '',
                        "ItemCategoryName" text NOT NULL DEFAULT '',
                        "Manufacturer" text NOT NULL DEFAULT '',
                        "ItemName" text NOT NULL DEFAULT '',
                        "MachineNumber" text NOT NULL DEFAULT '',
                        "PurchaseVendor" text NOT NULL DEFAULT '',
                        "PurchaseDate" date NULL,
                        "DisposalDate" date NULL,
                        "PurchasePrice" numeric(18,2) NOT NULL DEFAULT 0,
                        "SalePrice" numeric(18,2) NOT NULL DEFAULT 0,
                        "CustomerName" text NOT NULL DEFAULT '',
                        "InstallLocation" text NOT NULL DEFAULT '',
                        "DepositText" text NOT NULL DEFAULT '',
                        "MonthlyFee" numeric(18,2) NOT NULL DEFAULT 0,
                        "ContractMonths" integer NOT NULL DEFAULT 0,
                        "ContractDate" date NULL,
                        "InstallDate" date NULL,
                        "ContractStartDate" date NULL,
                        "RentalEndDate" date NULL,
                        "FreeSupplyItems" text NOT NULL DEFAULT '',
                        "PaidSupplyItems" text NOT NULL DEFAULT '',
                        "AssignedUsername" text NOT NULL DEFAULT '',
                        "AssetStatus" text NOT NULL DEFAULT '',
                        "Notes" text NOT NULL DEFAULT '',
                        "IsDeleted" boolean NOT NULL DEFAULT false,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        foreach (var sql in new[]
                 {
                     "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RentalAssets_TenantCode_AssetKey\" ON \"RentalAssets\" (\"TenantCode\", \"AssetKey\");",
                     "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RentalAssets_ManagementId\" ON \"RentalAssets\" (\"ManagementId\") WHERE TRIM(\"ManagementId\") <> '';",
                     "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RentalAssets_ManagementNumber\" ON \"RentalAssets\" (\"ManagementNumber\") WHERE TRIM(\"ManagementNumber\") <> '';",
                     "CREATE INDEX IF NOT EXISTS \"IX_RentalAssets_OfficeCode\" ON \"RentalAssets\" (\"OfficeCode\");"
                 })
        {
            try { await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken); } catch { }
        }
    }

    private static async Task EnsureRentalBillingLogsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "RentalBillingLogs" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_RentalBillingLogs" PRIMARY KEY,
                        "TenantCode" TEXT NOT NULL DEFAULT 'USENET_GROUP',
                        "OfficeCode" TEXT NOT NULL DEFAULT 'ALL',
                        "BillingProfileId" TEXT NOT NULL,
                        "BillingYearMonth" TEXT NOT NULL DEFAULT '',
                        "ScheduledDate" TEXT NOT NULL,
                        "ProcessedDate" TEXT NULL,
                        "ProcessedByUsername" TEXT NOT NULL DEFAULT '',
                        "Status" TEXT NOT NULL DEFAULT '예정',
                        "BilledAmount" REAL NOT NULL DEFAULT 0,
                        "Note" TEXT NOT NULL DEFAULT '',
                        "AssignedUsername" TEXT NOT NULL DEFAULT '',
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "RentalBillingLogs" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "TenantCode" text NOT NULL DEFAULT 'USENET_GROUP',
                        "OfficeCode" text NOT NULL DEFAULT 'ALL',
                        "BillingProfileId" uuid NOT NULL,
                        "BillingYearMonth" text NOT NULL DEFAULT '',
                        "ScheduledDate" date NOT NULL,
                        "ProcessedDate" date NULL,
                        "ProcessedByUsername" text NOT NULL DEFAULT '',
                        "Status" text NOT NULL DEFAULT '예정',
                        "BilledAmount" numeric(18,2) NOT NULL DEFAULT 0,
                        "Note" text NOT NULL DEFAULT '',
                        "AssignedUsername" text NOT NULL DEFAULT '',
                        "IsDeleted" boolean NOT NULL DEFAULT false,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        foreach (var sql in new[]
                 {
                     "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RentalBillingLogs_BillingProfileId_BillingYearMonth\" ON \"RentalBillingLogs\" (\"BillingProfileId\", \"BillingYearMonth\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_RentalBillingLogs_OfficeCode\" ON \"RentalBillingLogs\" (\"OfficeCode\");"
                 })
        {
            try { await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken); } catch { }
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
        PermissionNames.DeliveryViewAll,
        PermissionNames.RentalSettingsEdit,
        PermissionNames.RentalImport
    ];

    private static async Task UpsertTenantDefinitionAsync(
        AppDbContext dbContext,
        string tenantCode,
        string displayName,
        string storageMode,
        string description,
        CancellationToken cancellationToken)
    {
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(tenantCode);
        var normalizedStorageMode = TenantScopeCatalog.NormalizeStorageModeOrDefault(storageMode);
        var entity = dbContext.TenantDefinitions.Local.FirstOrDefault(current =>
                         string.Equals(current.TenantCode, normalizedTenantCode, StringComparison.OrdinalIgnoreCase))
                     ?? await dbContext.TenantDefinitions.IgnoreQueryFilters()
                         .FirstOrDefaultAsync(current => current.TenantCode == normalizedTenantCode, cancellationToken);

        if (entity is null)
        {
            entity = new TenantDefinition
            {
                TenantCode = normalizedTenantCode
            };
            dbContext.TenantDefinitions.Add(entity);
        }

        entity.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? TenantScopeCatalog.GetTenantDisplayName(normalizedTenantCode)
            : displayName.Trim();
        entity.StorageMode = normalizedStorageMode;
        entity.Description = description?.Trim() ?? string.Empty;
        entity.IsActive = true;
        entity.IsDeleted = false;
        entity.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static async Task UpsertTenantOfficeDefinitionAsync(
        AppDbContext dbContext,
        string tenantCode,
        string officeCode,
        string displayName,
        bool isHeadOffice,
        CancellationToken cancellationToken)
    {
        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode);
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, normalizedOfficeCode);
        var entity = dbContext.TenantOfficeDefinitions.Local.FirstOrDefault(current =>
                         string.Equals(current.OfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase))
                     ?? await dbContext.TenantOfficeDefinitions.IgnoreQueryFilters()
                         .FirstOrDefaultAsync(current => current.OfficeCode == normalizedOfficeCode, cancellationToken);

        if (entity is null)
        {
            entity = new TenantOfficeDefinition
            {
                OfficeCode = normalizedOfficeCode
            };
            dbContext.TenantOfficeDefinitions.Add(entity);
        }

        entity.TenantCode = normalizedTenantCode;
        entity.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? OfficeCodeCatalog.GetOfficeDisplayName(normalizedOfficeCode)
            : displayName.Trim();
        entity.IsHeadOffice = isHeadOffice;
        entity.IsActive = true;
        entity.IsDeleted = false;
        entity.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static async Task UpsertDataSharingPolicyAsync(
        AppDbContext dbContext,
        string sourceTenantCode,
        string sourceOfficeCode,
        string targetTenantCode,
        string targetOfficeCode,
        bool shareCustomers,
        bool shareItems,
        bool shareInvoices,
        bool sharePayments,
        bool shareContracts,
        bool shareReports,
        bool shareRentals,
        bool shareDeliveries,
        bool allowTargetWrite,
        string note,
        CancellationToken cancellationToken)
    {
        var normalizedSourceOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(sourceOfficeCode);
        var normalizedTargetOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(targetOfficeCode);
        var normalizedSourceTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(sourceTenantCode, normalizedSourceOffice);
        var normalizedTargetTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(targetTenantCode, normalizedTargetOffice);

        var entity = dbContext.DataSharingPolicies.Local.FirstOrDefault(current =>
                         string.Equals(current.SourceTenantCode, normalizedSourceTenant, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(current.SourceOfficeCode, normalizedSourceOffice, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(current.TargetTenantCode, normalizedTargetTenant, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(current.TargetOfficeCode, normalizedTargetOffice, StringComparison.OrdinalIgnoreCase))
                     ?? await dbContext.DataSharingPolicies.IgnoreQueryFilters()
                         .FirstOrDefaultAsync(current =>
                                 current.SourceTenantCode == normalizedSourceTenant &&
                                 current.SourceOfficeCode == normalizedSourceOffice &&
                                 current.TargetTenantCode == normalizedTargetTenant &&
                                 current.TargetOfficeCode == normalizedTargetOffice,
                             cancellationToken);

        if (entity is null)
        {
            entity = new DataSharingPolicy();
            dbContext.DataSharingPolicies.Add(entity);
        }

        entity.SourceTenantCode = normalizedSourceTenant;
        entity.SourceOfficeCode = normalizedSourceOffice;
        entity.TargetTenantCode = normalizedTargetTenant;
        entity.TargetOfficeCode = normalizedTargetOffice;
        entity.ShareCustomers = shareCustomers;
        entity.ShareItems = shareItems;
        entity.ShareInvoices = shareInvoices;
        entity.SharePayments = sharePayments;
        entity.ShareContracts = shareContracts;
        entity.ShareReports = shareReports;
        entity.ShareRentals = shareRentals;
        entity.ShareDeliveries = shareDeliveries;
        entity.AllowTargetWrite = allowTargetWrite;
        entity.Note = note?.Trim() ?? string.Empty;
        entity.IsActive = true;
        entity.IsDeleted = false;
        entity.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static async Task EnsureSeedUserAsync(
        AppDbContext dbContext,
        ILogger logger,
        string username,
        string? password,
        string role,
        string tenantCode,
        string officeCode,
        string scopeType,
        bool grantAllPermissions,
        bool updatePasswordIfExists,
        CancellationToken cancellationToken)
    {
        var normalizedUsername = username.Trim();
        var normalizedPassword = NormalizeSeedPassword(password);

        if (string.IsNullOrWhiteSpace(normalizedPassword))
        {
            logger.LogWarning("Seed user '{Username}' skipped because password was not configured.", normalizedUsername);
            return;
        }
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
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(normalizedPassword),
                Role = role,
                TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode),
                OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode),
                ScopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(scopeType),
                IsActive = true,
                IsDeleted = false
            };
            dbContext.Users.Add(user);
        }
        else
        {
            if (updatePasswordIfExists)
                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(normalizedPassword);

            user.Role = role;
            user.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode);
            user.OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode);
            user.ScopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(scopeType);
            user.IsActive = true;
            user.IsDeleted = false;
            user.UpdatedAtUtc = DateTime.UtcNow;
        }

        if (grantAllPermissions)
            EnsurePermissions(user, AllPermissions());
    }

    private static string? NormalizeSeedPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return null;

        var trimmed = password.Trim();
        if (trimmed.StartsWith("CHANGE_THIS_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("__DISABLE__", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    private static void LogSeedUserWarnings(
        IHostEnvironment hostEnvironment,
        ILogger logger,
        SeedUsersOptions seedUsersOptions)
    {
        if (hostEnvironment.IsDevelopment() || !seedUsersOptions.WarnOnDefaultPasswords)
            return;

        if (seedUsersOptions.UsesDefaultAdminPassword())
        {
            logger.LogWarning("SeedUsers: admin password is still using the default value. 운영 전 반드시 변경하세요.");
        }

        if (seedUsersOptions.UsesDefaultUserPassword())
        {
            logger.LogWarning("SeedUsers: user password is still using the default value. 운영 전 반드시 변경하거나 비활성화하세요.");
        }

        if (seedUsersOptions.UsesDefaultItwPassword())
        {
            logger.LogWarning("SeedUsers: itw password is still using the default value. 운영 전 반드시 변경하거나 비활성화하세요.");
        }
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

    private static Task EnsureCustomerContractStoragePathColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
        => EnsureNullableTextColumnAsync(dbContext, "CustomerContracts", "StoragePath", cancellationToken);

    private static Task EnsurePaymentAttachmentStoragePathColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
        => EnsureNullableTextColumnAsync(dbContext, "PaymentAttachments", "StoragePath", cancellationToken);

    private static Task EnsureTransactionAttachmentStoragePathColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
        => EnsureNullableTextColumnAsync(dbContext, "TransactionAttachments", "StoragePath", cancellationToken);

    private static async Task EnsureRentalBillingEnhancementColumnsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(dbContext, "Transactions", "LinkedRentalBillingRunId", "TEXT NULL", "uuid NULL", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingProfiles", "BillingType", "TEXT NOT NULL DEFAULT '묶음'", "text NOT NULL DEFAULT '묶음'", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingProfiles", "BillToCustomerName", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingProfiles", "InstallSiteName", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingProfiles", "BillingAdvanceMode", "TEXT NOT NULL DEFAULT '후불'", "text NOT NULL DEFAULT '후불'", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingProfiles", "BillingStartDate", "TEXT NULL", "date NULL", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingProfiles", "BillingDayMode", "TEXT NOT NULL DEFAULT '고정일'", "text NOT NULL DEFAULT '고정일'", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingProfiles", "BillingAnchorMonth", "INTEGER NOT NULL DEFAULT 3", "integer NOT NULL DEFAULT 3", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingProfiles", "DocumentIssueMode", "TEXT NOT NULL DEFAULT '결제일과 동일'", "text NOT NULL DEFAULT '결제일과 동일'", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingProfiles", "DocumentLeadDays", "INTEGER NOT NULL DEFAULT 0", "integer NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingProfiles", "BillingTemplateJson", "TEXT NOT NULL DEFAULT '[]'", "text NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingProfiles", "BillingRunsJson", "TEXT NOT NULL DEFAULT '[]'", "text NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalAssets", "CurrentCustomerName", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalAssets", "BillToCustomerName", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalAssets", "InstallSiteName", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalAssets", "BillingEligibilityStatus", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalAssets", "BillingExclusionReason", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
    }

    private static async Task EnsureNullableTextColumnAsync(
        AppDbContext dbContext,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
        => await EnsureColumnAsync(dbContext, tableName, columnName, "TEXT NULL", "text NULL", cancellationToken);

    private static async Task EnsureColumnAsync(
        AppDbContext dbContext,
        string tableName,
        string columnName,
        string sqliteDefinition,
        string postgresDefinition,
        CancellationToken cancellationToken)
    {
        if (!IsSafeSqlIdentifier(tableName) || !IsSafeSqlIdentifier(columnName))
            return;

        var quotedTableName = QuoteSqlIdentifier(tableName);
        var quotedColumnName = QuoteSqlIdentifier(columnName);
        if (dbContext.Database.IsSqlite())
        {
            var connection = dbContext.Database.GetDbConnection();
            await using var _ = await EnsureConnectionAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(" + quotedTableName + ")";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader["name"]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            var addSql = "ALTER TABLE " + quotedTableName + " ADD COLUMN " + quotedColumnName + " " + sqliteDefinition + ";";
            await dbContext.Database.ExecuteSqlRawAsync(addSql, cancellationToken);
            return;
        }

        var postgresSql = "ALTER TABLE " + quotedTableName + " ADD COLUMN IF NOT EXISTS " + quotedColumnName + " " + postgresDefinition + ";";
        await dbContext.Database.ExecuteSqlRawAsync(postgresSql, cancellationToken);
    }

    private static bool IsSafeSqlIdentifier(string value)
        => !string.IsNullOrWhiteSpace(value) && SqlIdentifierPattern.IsMatch(value);

    private static string QuoteSqlIdentifier(string value)
        => "\"" + value + "\"";

    private static async Task<IAsyncDisposable> EnsureConnectionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (connection.State == ConnectionState.Open)
            return new AsyncNoopDisposable();

        await connection.OpenAsync(cancellationToken);
        return new AsyncCloseConnection(connection);
    }

    private static async Task MigrateStoredFilesToCentralStorageAsync(
        AppDbContext dbContext,
        ICentralFileStorage fileStorage,
        CancellationToken cancellationToken)
    {
        var contracts = await dbContext.CustomerContracts.IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted && entity.FileContent.Length > 0 && string.IsNullOrWhiteSpace(entity.StoragePath))
            .ToListAsync(cancellationToken);
        foreach (var contract in contracts)
        {
            contract.StoragePath = await fileStorage.SaveBytesAsync(
                "customer-contracts",
                contract.CustomerId.ToString("N"),
                contract.Id,
                contract.FileName,
                contract.FileContent,
                cancellationToken);
            contract.FileContent = [];
        }

        var paymentAttachments = await dbContext.PaymentAttachments.IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted && entity.FileContent.Length > 0 && string.IsNullOrWhiteSpace(entity.StoragePath))
            .ToListAsync(cancellationToken);
        foreach (var attachment in paymentAttachments)
        {
            attachment.StoragePath = await fileStorage.SaveBytesAsync(
                "payment-attachments",
                attachment.PaymentId.ToString("N"),
                attachment.Id,
                attachment.FileName,
                attachment.FileContent,
                cancellationToken);
            attachment.FileContent = [];
        }

        var transactionAttachments = await dbContext.TransactionAttachments.IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted && entity.FileContent.Length > 0 && string.IsNullOrWhiteSpace(entity.StoragePath))
            .ToListAsync(cancellationToken);
        foreach (var attachment in transactionAttachments)
        {
            attachment.StoragePath = await fileStorage.SaveBytesAsync(
                "transaction-attachments",
                attachment.TransactionId.ToString("N"),
                attachment.Id,
                attachment.FileName,
                attachment.FileContent,
                cancellationToken);
            attachment.FileContent = [];
        }
    }

    private sealed class AsyncNoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AsyncCloseConnection(DbConnection connection) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            connection.Close();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SystemCurrentUserContext : ICurrentUserContext
    {
        public static SystemCurrentUserContext Instance { get; } = new();

        public Guid? UserId => null;
        public string Username => "system";
        public string TenantCode => TenantScopeCatalog.UsenetGroup;
        public string OfficeCode => OfficeCodeCatalog.Usenet;
        public string ScopeType => TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin => true;
        public bool IsGodMode => true;
        public bool HasPermission(string permission) => true;
    }
}
