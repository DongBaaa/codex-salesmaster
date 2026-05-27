using System.Reflection;
using System.Text.Json;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class DbInitializerRegressionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _dbContext;

    public DbInitializerRegressionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var currentUser = new TestCurrentUserContext
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new AppDbContext(options, currentUser, revisionClock);
        _dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task EnsureInvoiceVersionColumnsAsync_DoesNotRequire_SourceWarehouseCode_BeforeRuntimeSchema()
    {
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"InvoiceLines\";");
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Payments\";");
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Invoices\";");
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "Invoices" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Invoices" PRIMARY KEY,
                "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                "CreatedAtUtc" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                "Revision" INTEGER NOT NULL DEFAULT 0
            );
            """);
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var invoiceId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);
        var updatedAt = createdAt.AddMinutes(5);
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "Invoices" ("Id", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc", "Revision")
             VALUES ({invoiceId.ToString()}, 0, {createdAt}, {updatedAt}, 1);
             """);

        var method = typeof(DbInitializer).GetMethod(
            "EnsureInvoiceVersionColumnsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT \"VersionGroupId\", \"VersionNumber\", \"IsLatestVersion\" FROM \"Invoices\" WHERE \"Id\" = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", invoiceId.ToString());

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(invoiceId.ToString(), reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task EnsureOperationalRuntimeSchemaAsync_DoesNotDispose_DbConnection()
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureOperationalRuntimeSchemaAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;

        await _dbContext.Database.ExecuteSqlRawAsync("SELECT 1;");
    }

    [Fact]
    public async Task VerifyRequiredOperationalSchemaAsync_Throws_WhenCriticalSchemaColumnMissing()
    {
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"ItemWarehouseStocks\";");
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "ItemWarehouseStocks" (
                "ItemId" TEXT NOT NULL,
                "WarehouseCode" TEXT NOT NULL,
                "Quantity" REAL NOT NULL DEFAULT 0,
                "UpdatedAtUtc" TEXT NOT NULL,
                CONSTRAINT "PK_ItemWarehouseStocks" PRIMARY KEY ("ItemId", "WarehouseCode")
            );
            """);

        var method = typeof(DbInitializer).GetMethod(
            "VerifyRequiredOperationalSchemaAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => task!);
        Assert.Contains("ItemWarehouseStocks", exception.Message);
        Assert.Contains("Revision", exception.Message);
    }

    [Fact]
    public async Task NormalizeInventoryTransferIntegrityAsync_RemovesCrossTenantTransfers()
    {
        var transferId = Guid.NewGuid();
        _dbContext.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Itworld,
            TransferNumber = "TR-INVALID-001",
            TransferDate = new DateOnly(2026, 4, 13),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });
        _dbContext.InventoryTransferLines.Add(new InventoryTransferLine
        {
            Id = Guid.NewGuid(),
            TransferId = transferId,
            ItemNameOriginal = "테스트 품목",
            Unit = "EA",
            Quantity = 1m
        });
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "NormalizeInventoryTransferIntegrityAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        Assert.False(await _dbContext.InventoryTransfers.IgnoreQueryFilters().AnyAsync(current => current.Id == transferId));
        Assert.False(await _dbContext.InventoryTransferLines.IgnoreQueryFilters().AnyAsync(current => current.TransferId == transferId));
    }

    [Fact]
    public async Task CleanupDeletedInvoiceChainAsync_MarksActiveLinesUnderDeletedInvoices()
    {
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Deleted invoice cleanup customer",
            NameMatchKey = "DELETEDINVOICECLEANUPCUSTOMER",
            TradeType = "Sales"
        });
        _dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-DELETED-CLEANUP",
            InvoiceDate = new DateOnly(2026, 5, 28),
            IsDeleted = true
        });
        _dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = lineId,
            InvoiceId = invoiceId,
            ItemNameOriginal = "active line under deleted invoice",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 1000m,
            LineAmount = 1000m,
            IsDeleted = false
        });
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "CleanupDeletedInvoiceChainAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        Assert.True(await _dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.Id == lineId)
            .Select(line => line.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task MergeBusinessDuplicateCustomersAsync_UsesResponsibleOfficeCode_ForDuplicateKey()
    {
        var duplicateA = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "연수 테스트 거래처",
            NameMatchKey = "연수테스트거래처",
            BusinessNumber = "123-45-67890",
            TradeType = "매출",
            Address = "인천 연수구 테스트로 1",
            Phone = "032-000-0000",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
        };
        var duplicateB = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "연수 테스트 거래처",
            NameMatchKey = "연수테스트거래처",
            BusinessNumber = "123-45-67890",
            TradeType = "매출",
            Address = "인천 연수구 테스트로 1",
            Phone = "032-000-0000",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };

        _dbContext.Customers.AddRange(duplicateA, duplicateB);
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "MergeBusinessDuplicateCustomersAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var customers = await _dbContext.Customers.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted && current.NameOriginal == "연수 테스트 거래처")
            .ToListAsync();

        var remaining = Assert.Single(customers);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, remaining.ResponsibleOfficeCode);
    }

    [Fact]
    public async Task EnsureRentalAssetsTableAsync_AllowsDeletedNaturalKeyDuplicates_ButBlocksActiveDuplicates()
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureRentalAssetsTableAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;

        var activeAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "asset-index-duplicate",
            ManagementId = "MID-INDEX-DUP",
            ManagementNumber = "MN-INDEX-DUP",
            ItemName = "active asset",
            IsDeleted = false
        };
        var deletedAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "asset-index-duplicate",
            ManagementId = "MID-INDEX-DUP",
            ManagementNumber = "MN-INDEX-DUP",
            ItemName = "deleted asset",
            IsDeleted = true
        };

        _dbContext.RentalAssets.AddRange(activeAsset, deletedAsset);
        await _dbContext.SaveChangesAsync();

        _dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "asset-index-duplicate-other",
            ManagementId = "MID-INDEX-DUP",
            ManagementNumber = "MN-INDEX-DUP-OTHER",
            ItemName = "second active asset",
            IsDeleted = false
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => _dbContext.SaveChangesAsync());
        Assert.Contains("UNIQUE", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepairRentalCustomerLinkageAsync_NormalizesItworldRentalScope_AndPreservesYeonsuScope()
    {
        var brokenProfileId = Guid.Parse("91111111-1111-1111-1111-111111111111");
        var brokenAssetId = Guid.Parse("92222222-2222-2222-2222-222222222222");
        var brokenLogId = Guid.Parse("93333333-3333-3333-3333-333333333333");
        var yeonsuProfileId = Guid.Parse("94444444-4444-4444-4444-444444444444");
        var yeonsuAssetId = Guid.Parse("95555555-5555-5555-5555-555555555555");
        var wrongUsenetCustomerId = Guid.Parse("96666666-6666-6666-6666-666666666666");

        _dbContext.Customers.Add(new Customer
        {
            Id = wrongUsenetCustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Wrong USENET Customer",
            NameMatchKey = "WRONGUSENETCUSTOMER"
        });

        _dbContext.RentalBillingProfiles.AddRange(
            new RentalBillingProfile
            {
                Id = brokenProfileId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerId = wrongUsenetCustomerId,
                CustomerName = "Broken ITWORLD Customer",
                InstallSiteName = "ITWORLD Site",
                ItemName = "Printer",
                MonthlyAmount = 120000m,
                BillingTemplateJson = "[]"
            },
            new RentalBillingProfile
            {
                Id = yeonsuProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                CustomerName = "YEONSU Customer",
                InstallSiteName = "YEONSU Site",
                ItemName = "Copier",
                MonthlyAmount = 90000m,
                BillingTemplateJson = "[]"
            });

        _dbContext.RentalAssets.AddRange(
            new RentalAsset
            {
                Id = brokenAssetId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerId = wrongUsenetCustomerId,
                BillingProfileId = brokenProfileId,
                AssetKey = "ITWORLD|BROKEN-001|SN-BROKEN",
                CustomerName = "Broken ITWORLD Customer",
                CurrentCustomerName = "Broken ITWORLD Customer",
                InstallSiteName = "ITWORLD Site",
                InstallLocation = "ITWORLD Site",
                ItemName = "Printer",
                ManagementNumber = "BROKEN-001",
                MachineNumber = "SN-BROKEN",
                AssetStatus = "ACTIVE",
                BillingEligibilityStatus = string.Empty,
                MonthlyFee = 120000m
            },
            new RentalAsset
            {
                Id = yeonsuAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                BillingProfileId = yeonsuProfileId,
                AssetKey = "USENET|YEONSU-001|SN-YEONSU",
                CustomerName = "YEONSU Customer",
                CurrentCustomerName = "YEONSU Customer",
                InstallSiteName = "YEONSU Site",
                InstallLocation = "YEONSU Site",
                ItemName = "Copier",
                ManagementNumber = "YEONSU-001",
                MachineNumber = "SN-YEONSU",
                AssetStatus = "ACTIVE",
                BillingEligibilityStatus = string.Empty,
                MonthlyFee = 90000m
            });

        _dbContext.RentalBillingLogs.Add(new RentalBillingLog
        {
            Id = brokenLogId,
            BillingProfileId = brokenProfileId,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BillingYearMonth = "202604",
            Status = "PENDING",
            BilledAmount = 120000m
        });

        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "RepairRentalCustomerLinkageAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var fixedProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == brokenProfileId);
        var fixedAsset = await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == brokenAssetId);
        var fixedLog = await _dbContext.RentalBillingLogs.IgnoreQueryFilters().SingleAsync(log => log.Id == brokenLogId);
        var yeonsuProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == yeonsuProfileId);
        var yeonsuAsset = await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == yeonsuAssetId);

        Assert.Equal(TenantScopeCatalog.Itworld, fixedProfile.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedProfile.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedProfile.ManagementCompanyCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedProfile.ResponsibleOfficeCode);

        Assert.Equal(TenantScopeCatalog.Itworld, fixedAsset.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedAsset.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedAsset.ManagementCompanyCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedAsset.ResponsibleOfficeCode);

        Assert.Equal(TenantScopeCatalog.Itworld, fixedLog.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedLog.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, fixedLog.ResponsibleOfficeCode);

        Assert.Equal(TenantScopeCatalog.UsenetGroup, yeonsuProfile.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuProfile.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuProfile.ManagementCompanyCode);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, yeonsuProfile.ResponsibleOfficeCode);

        Assert.Equal(TenantScopeCatalog.UsenetGroup, yeonsuAsset.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuAsset.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuAsset.ManagementCompanyCode);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, yeonsuAsset.ResponsibleOfficeCode);
    }

    [Fact]
    public async Task RepairRentalCustomerLinkageAsync_RecalculatesBillingTemplateAndProfileAmountFromLinkedAssetFees()
    {
        var customerId = Guid.Parse("96666666-6666-6666-6666-666666666667");
        var profileId = Guid.Parse("97777777-7777-7777-7777-777777777777");
        var firstAssetId = Guid.Parse("98888888-8888-8888-8888-888888888888");
        var secondAssetId = Guid.Parse("99999999-9999-9999-9999-999999999999");

        var templateJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                ItemId = Guid.Parse("9aaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                DisplayItemName = "??? ???",
                BillingLineMode = "??",
                Quantity = 1m,
                UnitPrice = 100m,
                Amount = 100m,
                IncludedAssetIds = new[] { firstAssetId }
            }
        });

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "?? ??? ??? ???",
            NameMatchKey = "???????????"
        });

        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = "?? ??? ??? ???",
            InstallSiteName = "?? ??? ??? ???",
            ItemName = "??? ???",
            BillingType = "??",
            MonthlyAmount = 100m,
            BillingTemplateJson = templateJson,
            IsActive = true
        });

        _dbContext.RentalAssets.AddRange(
            new RentalAsset
            {
                Id = firstAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = profileId,
                CustomerId = customerId,
                AssetKey = "USENET|MONTHLY-001|SN-001",
                CustomerName = "?? ??? ??? ???",
                CurrentCustomerName = "?? ??? ??? ???",
                InstallSiteName = "??? ???",
                InstallLocation = "??? ???",
                ItemName = "???",
                ManagementNumber = "MONTHLY-001",
                MachineNumber = "SN-001",
                AssetStatus = "ACTIVE",
                MonthlyFee = 110000m
            },
            new RentalAsset
            {
                Id = secondAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = profileId,
                CustomerId = customerId,
                AssetKey = "USENET|MONTHLY-002|SN-002",
                CustomerName = "?? ??? ??? ???",
                CurrentCustomerName = "?? ??? ??? ???",
                InstallSiteName = "??? ???",
                InstallLocation = "??? ???",
                ItemName = "???",
                ManagementNumber = "MONTHLY-002",
                MachineNumber = "SN-002",
                AssetStatus = "ACTIVE",
                MonthlyFee = 220000m
            });

        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "RepairRentalCustomerLinkageAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
        Assert.Equal(330000m, profile.MonthlyAmount);

        using var document = JsonDocument.Parse(profile.BillingTemplateJson);
        var item = document.RootElement.EnumerateArray().Single();
        Assert.Equal(1m, item.GetProperty("Quantity").GetDecimal());
        Assert.Equal(330000m, item.GetProperty("UnitPrice").GetDecimal());
        Assert.Equal(330000m, item.GetProperty("Amount").GetDecimal());
        var includedAssetIds = item.GetProperty("IncludedAssetIds")
            .EnumerateArray()
            .Select(value => value.GetGuid())
            .OrderBy(value => value)
            .ToList();
        Assert.Equal(new[] { firstAssetId, secondAssetId }.OrderBy(value => value), includedAssetIds);
    }

    [Fact]
    public async Task RepairRentalCustomerLinkageAsync_ResolvesProfileCustomerFromTemplateLinkedAsset()
    {
        var customerId = Guid.Parse("9bbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var profileId = Guid.Parse("9ccccccc-cccc-cccc-cccc-cccccccccccc");
        var assetId = Guid.Parse("9ddddddd-dddd-dddd-dddd-dddddddddddd");

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "????[?????]",
            NameMatchKey = "?????????"
        });

        var templateJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                ItemId = Guid.Parse("9eeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                DisplayItemName = "IMC2010",
                BillingLineMode = "??",
                Quantity = 1m,
                UnitPrice = 240000m,
                Amount = 240000m,
                IncludedAssetIds = new[] { assetId }
            }
        });

        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = "[???]???-?????",
            InstallSiteName = "[???]???-?????",
            ItemName = "IMC2010",
            BillingType = "??",
            MonthlyAmount = 240000m,
            BillingTemplateJson = templateJson,
            IsActive = true
        });

        _dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            CustomerId = customerId,
            AssetKey = "USENET|HEALTH-001|SN-HEALTH",
            CustomerName = "????[?????]",
            CurrentCustomerName = "????[?????]",
            InstallSiteName = "?.??",
            InstallLocation = "?.??",
            ItemName = "IMC2010",
            ManagementNumber = "HEALTH-001",
            MachineNumber = "SN-HEALTH",
            AssetStatus = "ACTIVE",
            MonthlyFee = 90000m
        });

        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "RepairRentalCustomerLinkageAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
        Assert.Equal(customerId, profile.CustomerId);
        Assert.Equal("????[?????]", profile.CustomerName);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, profile.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, profile.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, profile.ManagementCompanyCode);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, profile.ResponsibleOfficeCode);
        Assert.Equal(90000m, profile.MonthlyAmount);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; }
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public bool HasPermission(string permission) => IsAdmin;
    }
}
