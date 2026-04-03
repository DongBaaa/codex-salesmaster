using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;

var runtimeDir = Path.Combine(AppContext.BaseDirectory, "runtime");
if (Directory.Exists(runtimeDir))
    Directory.Delete(runtimeDir, recursive: true);
Directory.CreateDirectory(runtimeDir);

var result = new VerificationResult();
await VerifyLocalAsync(Path.Combine(runtimeDir, "local"), result.Local);
await VerifyServerAsync(Path.Combine(runtimeDir, "server"), result.Server);

var resultPath = Path.Combine(AppContext.BaseDirectory, "result.json");
await File.WriteAllTextAsync(
    resultPath,
    JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"Wrote verification result to {resultPath}");

static async Task VerifyLocalAsync(string runtimeDir, LocalVerificationResult result)
{
    const string AliasUsenet = "\uC720\uC988\uB137";
    const string AliasItworld = "\uC544\uC774\uD2F0\uC6D4\uB4DC";
    const string AliasYeonsuOffice = "\uC5F0\uC218\uAD6C \uC0AC\uBB34\uC2E4";
    const string AliasYeonsu = "\uC5F0\uC218\uAD6C";
    const string AliasRentalStatus = "\uC784\uB300\uC9C4\uD589\uC911";
    const string AliasBillingStatus = "\uC608\uC815";

    Directory.CreateDirectory(runtimeDir);
    var localAppData = Path.Combine(runtimeDir, "LocalAppData");
    var georaePlanRoot = Path.Combine(runtimeDir, "거래플랜");
    Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", georaePlanRoot);
    Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", "1");
    Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", "1");
    Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData);

    await using (var db = new LocalDbContext())
    {
        await LocalDbInitializer.InitializeAsync(db);
    }

    await using (var db = new LocalDbContext())
    {
        var usenetOfficeId = await db.Offices.IgnoreQueryFilters().Where(x => x.Code == OfficeCodeCatalog.Usenet).Select(x => x.Id).SingleAsync();
        var itworldOfficeId = await db.Offices.IgnoreQueryFilters().Where(x => x.Code == OfficeCodeCatalog.Itworld).Select(x => x.Id).SingleAsync();
        var yeonsuOfficeId = await db.Offices.IgnoreQueryFilters().Where(x => x.Code == OfficeCodeCatalog.Yeonsu).Select(x => x.Id).SingleAsync();

        var aliasOfficeUsenet = new LocalOffice { Id = Guid.NewGuid(), Code = AliasUsenet, Name = AliasUsenet };
        var aliasOfficeItworld = new LocalOffice { Id = Guid.NewGuid(), Code = AliasItworld, Name = AliasItworld };
        var aliasOfficeYeonsu = new LocalOffice { Id = Guid.NewGuid(), Code = AliasYeonsuOffice, Name = AliasYeonsuOffice };
        var aliasOfficeUznet = new LocalOffice { Id = Guid.NewGuid(), Code = "UZNET", Name = "UZNET" };
        db.Offices.AddRange(aliasOfficeUsenet, aliasOfficeItworld, aliasOfficeYeonsu, aliasOfficeUznet);

        db.Warehouses.AddRange(
            new LocalWarehouse { OfficeId = aliasOfficeUsenet.Id, OfficeCode = AliasUsenet, Code = $"{AliasUsenet}_MAIN", Name = $"{AliasUsenet} warehouse" },
            new LocalWarehouse { OfficeId = aliasOfficeItworld.Id, OfficeCode = AliasItworld, Code = $"{AliasItworld}_MAIN", Name = $"{AliasItworld} warehouse" },
            new LocalWarehouse { OfficeId = aliasOfficeYeonsu.Id, OfficeCode = AliasYeonsuOffice, Code = $"{AliasYeonsu}_MAIN", Name = $"{AliasYeonsu} warehouse" },
            new LocalWarehouse { OfficeId = aliasOfficeUznet.Id, OfficeCode = "UZNET", Code = "UZNET_MAIN", Name = "UZNET warehouse" });

        var customer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "Alias Customer",
            NameMatchKey = "ALIAS CUSTOMER",
            ResponsibleOfficeCode = AliasYeonsuOffice
        };
        db.Customers.Add(customer);

        var item = new LocalItem
        {
            Id = Guid.NewGuid(),
            NameOriginal = "Alias Item",
            NameMatchKey = "ALIAS ITEM",
            SpecificationOriginal = "",
            SpecificationMatchKey = "",
            CurrentStock = 5m
        };
        db.Items.Add(item);

        db.CompanyProfiles.Add(new LocalCompanyProfile
        {
            Id = Guid.NewGuid(),
            ProfileName = $"{AliasYeonsu} default",
            OfficeCode = AliasYeonsuOffice,
            TradeName = AliasYeonsu,
            IsDefaultForOffice = true,
            IsActive = true
        });

        var invoice = new LocalInvoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            InvoiceNumber = "ALIAS-INV-1",
            VoucherType = VoucherType.Sales,
            ResponsibleOfficeCode = AliasUsenet,
            SourceWarehouseCode = $"{AliasUsenet}_MAIN",
            VersionGroupId = Guid.NewGuid(),
            IsLatestVersion = true,
            IsConfirmed = true
        };
        db.Invoices.Add(invoice);

        db.Transactions.Add(new LocalTransaction
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            ResponsibleOfficeCode = AliasItworld
        });

        db.AuditLogs.Add(new LocalAuditLog
        {
            Id = Guid.NewGuid(),
            EntityName = "LocalInvoice",
            EntityId = invoice.Id.ToString("D"),
            Action = "AliasSeed",
            Username = "seed",
            Role = "Admin",
            OfficeCode = AliasUsenet
        });

        var cachedOfficeSetting = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Key == "CachedSession_OfficeCode");
        if (cachedOfficeSetting is null)
        {
            db.Settings.Add(new LocalSetting { Key = "CachedSession_OfficeCode", Value = AliasYeonsuOffice });
        }
        else
        {
            cachedOfficeSetting.Value = AliasYeonsuOffice;
        }

        db.InventoryMovements.Add(new LocalInventoryMovement
        {
            Id = Guid.NewGuid(),
            ItemId = item.Id,
            WarehouseCode = $"{AliasItworld}_MAIN",
            MovementType = "PurchaseIn",
            QuantityDelta = 2m,
            UnitCost = 10m,
            Amount = 20m,
            CreatedByUsername = "seed"
        });

        db.StockLayers.Add(new LocalStockLayer
        {
            Id = Guid.NewGuid(),
            ItemId = item.Id,
            WarehouseCode = $"{AliasYeonsu}_MAIN",
            UnitCost = 10m,
            OriginalQuantity = 2m,
            RemainingQuantity = 2m
        });

        db.CostAllocations.Add(new LocalCostAllocation
        {
            Id = Guid.NewGuid(),
            SalesInvoiceId = invoice.Id,
            SalesInvoiceLineId = Guid.NewGuid(),
            WarehouseCode = $"{AliasUsenet}_MAIN",
            Quantity = 1m,
            UnitCost = 10m,
            CostAmount = 10m
        });

        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = $"{AliasItworld}_MAIN",
            Quantity = 7m
        });

        db.SerialLedgers.Add(new LocalSerialLedger
        {
            Id = Guid.NewGuid(),
            SerialNumber = $"SERIAL-ALIAS-{Guid.NewGuid():N}",
            ItemId = item.Id,
            WarehouseCode = $"{AliasYeonsu}_MAIN",
            Status = "InStock",
            LastMovementType = "PurchaseIn"
        });

        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = Guid.NewGuid(),
            TransferNumber = "TR-ALIAS-1",
            FromWarehouseCode = $"{AliasUsenet}_MAIN",
            ToWarehouseCode = $"{AliasYeonsu}_MAIN",
            CreatedByUsername = "seed",
            LastSavedByUsername = "seed"
        });

        var billingProfile = new LocalRentalBillingProfile
        {
            Id = Guid.NewGuid(),
            ProfileKey = $"RENTAL-ALIAS-{Guid.NewGuid():N}",
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            ItemName = item.NameOriginal,
            ResponsibleOfficeCode = AliasUsenet,
            ManagementCompanyCode = AliasItworld,
            BillingStatus = AliasBillingStatus
        };
        db.RentalBillingProfiles.Add(billingProfile);

        db.RentalAssets.Add(new LocalRentalAsset
        {
            Id = Guid.NewGuid(),
            AssetKey = $"ASSET-ALIAS-{Guid.NewGuid():N}",
            CustomerId = customer.Id,
            ItemId = item.Id,
            BillingProfileId = billingProfile.Id,
            CustomerName = customer.NameOriginal,
            ItemName = item.NameOriginal,
            ResponsibleOfficeCode = AliasYeonsuOffice,
            ManagementCompanyCode = AliasUsenet,
            AssetStatus = AliasRentalStatus
        });

        db.RentalBillingLogs.Add(new LocalRentalBillingLog
        {
            Id = Guid.NewGuid(),
            BillingProfileId = billingProfile.Id,
            BillingYearMonth = "202603",
            ResponsibleOfficeCode = AliasItworld
        });

        db.RentalManagementCompanies.AddRange(
            new LocalRentalManagementCompany { Id = Guid.NewGuid(), Code = AliasUsenet, Name = AliasUsenet },
            new LocalRentalManagementCompany { Id = Guid.NewGuid(), Code = AliasItworld, Name = AliasItworld },
            new LocalRentalManagementCompany { Id = Guid.NewGuid(), Code = AliasYeonsu, Name = AliasYeonsu });

        await db.SaveChangesAsync();
    }

    await using (var db = new LocalDbContext())
    {
        await LocalDbInitializer.InitializeAsync(db);
    }

    await using (var db = new LocalDbContext())
    {
        result.OfficeCodes = await db.Offices.IgnoreQueryFilters().OrderBy(x => x.Code).Select(x => x.Code).ToListAsync();
        result.WarehouseCodes = await db.Warehouses.IgnoreQueryFilters().OrderBy(x => x.Code).Select(x => x.Code).ToListAsync();
        result.CompanyProfileOfficeCodes = await db.CompanyProfiles.IgnoreQueryFilters().Select(x => x.OfficeCode).Distinct().OrderBy(x => x).ToListAsync();
        result.CustomerOfficeCodes = await db.Customers.IgnoreQueryFilters().Select(x => x.ResponsibleOfficeCode).Distinct().OrderBy(x => x).ToListAsync();
        result.InvoiceOfficeCodes = await db.Invoices.IgnoreQueryFilters().Select(x => x.ResponsibleOfficeCode).Distinct().OrderBy(x => x).ToListAsync();
        result.InvoiceWarehouseCodes = await db.Invoices.IgnoreQueryFilters().Select(x => x.SourceWarehouseCode).Distinct().OrderBy(x => x).ToListAsync();
        result.TransactionOfficeCodes = await db.Transactions.IgnoreQueryFilters().Select(x => x.ResponsibleOfficeCode).Distinct().OrderBy(x => x).ToListAsync();
        result.AuditOfficeCodes = await db.AuditLogs.Select(x => x.OfficeCode).Distinct().OrderBy(x => x).ToListAsync();
        result.RentalManagementCompanyCodes = await db.RentalManagementCompanies.IgnoreQueryFilters().Select(x => x.Code).Distinct().OrderBy(x => x).ToListAsync();
        result.RentalBillingOfficeCodes = await db.RentalBillingProfiles.IgnoreQueryFilters().Select(x => x.ResponsibleOfficeCode).Distinct().OrderBy(x => x).ToListAsync();
        result.RentalBillingCompanyCodes = await db.RentalBillingProfiles.IgnoreQueryFilters().Select(x => x.ManagementCompanyCode).Distinct().OrderBy(x => x).ToListAsync();
        result.RentalAssetOfficeCodes = await db.RentalAssets.IgnoreQueryFilters().Select(x => x.ResponsibleOfficeCode).Distinct().OrderBy(x => x).ToListAsync();
        result.RentalAssetCompanyCodes = await db.RentalAssets.IgnoreQueryFilters().Select(x => x.ManagementCompanyCode).Distinct().OrderBy(x => x).ToListAsync();
        result.RentalBillingLogOfficeCodes = await db.RentalBillingLogs.IgnoreQueryFilters().Select(x => x.ResponsibleOfficeCode).Distinct().OrderBy(x => x).ToListAsync();
        result.InventoryWarehouseCodes = await db.InventoryMovements.Select(x => x.WarehouseCode).Distinct().OrderBy(x => x).ToListAsync();
        result.StockLayerWarehouseCodes = await db.StockLayers.Select(x => x.WarehouseCode).Distinct().OrderBy(x => x).ToListAsync();
        result.CostAllocationWarehouseCodes = await db.CostAllocations.Select(x => x.WarehouseCode).Distinct().OrderBy(x => x).ToListAsync();
        result.ItemWarehouseCodes = await db.ItemWarehouseStocks.Select(x => x.WarehouseCode).Distinct().OrderBy(x => x).ToListAsync();
        result.SerialLedgerWarehouseCodes = await db.SerialLedgers.Select(x => x.WarehouseCode).Distinct().OrderBy(x => x).ToListAsync();
        result.TransferWarehouseCodes = await db.InventoryTransfers.IgnoreQueryFilters().Select(x => x.FromWarehouseCode + "->" + x.ToWarehouseCode).Distinct().OrderBy(x => x).ToListAsync();
        result.CachedOfficeCode = await db.Settings.Where(x => x.Key == "CachedSession_OfficeCode").Select(x => x.Value).FirstOrDefaultAsync();
        var session = new SessionState();
        session.SetSession("token", new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "session-alias",
            Role = "User",
            OfficeCode = AliasYeonsuOffice,
            Permissions = new List<string>()
        });
        result.RuntimeSessionOfficeCode = session.OfficeCode;

        result.IsCanonical =
            result.OfficeCodes.SequenceEqual(new[] { OfficeCodeCatalog.Itworld, OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu }) &&
            result.WarehouseCodes.SequenceEqual(new[] { OfficeCodeCatalog.ItworldMainWarehouse, OfficeCodeCatalog.UsenetMainWarehouse, OfficeCodeCatalog.YeonsuMainWarehouse }) &&
            string.Equals(result.CachedOfficeCode, OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(result.RuntimeSessionOfficeCode, OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase) &&
            result.CompanyProfileOfficeCodes.All(OfficeCodeCatalog.IsCanonicalOfficeCode) &&
            result.CustomerOfficeCodes.All(OfficeCodeCatalog.IsCanonicalOfficeCode) &&
            result.InvoiceOfficeCodes.All(OfficeCodeCatalog.IsCanonicalOfficeCode) &&
            result.TransactionOfficeCodes.All(OfficeCodeCatalog.IsCanonicalOfficeCode) &&
            result.AuditOfficeCodes.All(OfficeCodeCatalog.IsCanonicalOfficeCode) &&
            result.RentalManagementCompanyCodes.All(OfficeCodeCatalog.IsCanonicalOfficeCode) &&
            result.RentalBillingOfficeCodes.All(OfficeCodeCatalog.IsCanonicalOfficeCode) &&
            result.RentalBillingCompanyCodes.All(OfficeCodeCatalog.IsCanonicalOfficeCode) &&
            result.RentalAssetOfficeCodes.All(OfficeCodeCatalog.IsCanonicalOfficeCode) &&
            result.RentalAssetCompanyCodes.All(OfficeCodeCatalog.IsCanonicalOfficeCode) &&
            result.RentalBillingLogOfficeCodes.All(OfficeCodeCatalog.IsCanonicalOfficeCode) &&
            result.InvoiceWarehouseCodes.All(IsCanonicalWarehouseCode) &&
            result.InventoryWarehouseCodes.All(IsCanonicalWarehouseCode) &&
            result.StockLayerWarehouseCodes.All(IsCanonicalWarehouseCode) &&
            result.CostAllocationWarehouseCodes.All(IsCanonicalWarehouseCode) &&
            result.ItemWarehouseCodes.All(IsCanonicalWarehouseCode) &&
            result.SerialLedgerWarehouseCodes.All(IsCanonicalWarehouseCode) &&
            result.TransferWarehouseCodes.All(pair => pair.Split("->").All(IsCanonicalWarehouseCode));
    }
}

static async Task VerifyServerAsync(string runtimeDir, ServerVerificationResult result)
{
    const string AliasYeonsuOffice = "\uC5F0\uC218\uAD6C \uC0AC\uBB34\uC2E4";
    const string AliasItworld = "\uC544\uC774\uD2F0\uC6D4\uB4DC";

    Directory.CreateDirectory(runtimeDir);
    var dbPath = Path.Combine(runtimeDir, "server-test.db");
    if (File.Exists(dbPath))
        File.Delete(dbPath);

    var services = new ServiceCollection();
    services.AddSingleton<ICurrentUserContext, FakeCurrentUserContext>();
    services.AddSingleton<RevisionClock>();
    services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(runtimeDir));
    services.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(builder => { }));
    services.AddSingleton<IOptions<SeedUsersOptions>>(_ => Options.Create(new SeedUsersOptions
    {
        EnableSeedUsers = true,
        WarnOnDefaultPasswords = false,
        UpdateExistingItwPassword = true,
        AdminPassword = SeedUsersOptions.DefaultAdminPassword,
        UserPassword = SeedUsersOptions.DefaultUserPassword,
        ItwPassword = SeedUsersOptions.DefaultItwPassword
    }));
    services.AddSingleton<ITenantDatabaseConnectionResolver>(new FakeTenantDatabaseConnectionResolver($"Data Source={dbPath}"));
    services.AddSingleton<ICentralFileStorage>(new StubCentralFileStorage(runtimeDir));
    services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
    var provider = services.BuildServiceProvider();

    await DbInitializer.InitializeAsync(provider);

    await using (var scope = provider.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new UserAccount
        {
            Username = "alias_seed",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Alias123!"),
            Role = "User",
            OfficeCode = AliasYeonsuOffice,
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    await DbInitializer.InitializeAsync(provider);

    await using (var scope = provider.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUserContext>();
        var controller = new UsersController(db, currentUser);

        var users = await db.Users.IgnoreQueryFilters().OrderBy(x => x.Username).ToListAsync();
        result.SeededUsers = users.Select(user => new UserOfficeRow(user.Username, user.OfficeCode)).ToList();
        var getAll = await controller.GetAll(CancellationToken.None);
        result.GetAllOfficeCodes = getAll.Value?.Select(user => user.OfficeCode).OrderBy(code => code).ToList() ?? new List<string>();

        var createAliasResult = await controller.Create(new CreateUserRequest
        {
            Username = "alias_create",
            Password = "Alias123!",
            Role = "User",
            OfficeCode = AliasYeonsuOffice,
            IsActive = true,
            Permissions = new List<string>()
        }, CancellationToken.None);

        result.AliasCreateSucceeded = createAliasResult.Result is CreatedAtActionResult;
        result.AliasCreateOfficeCode = (createAliasResult.Result as CreatedAtActionResult)?.Value is UserAccountDto dto ? dto.OfficeCode : null;

        var badRequestResult = await controller.Create(new CreateUserRequest
        {
            Username = "bad_office",
            Password = "Alias123!",
            Role = "User",
            OfficeCode = "UNKNOWN_OFFICE",
            IsActive = true,
            Permissions = new List<string>()
        }, CancellationToken.None);
        result.InvalidOfficeRejected = badRequestResult.Result is BadRequestObjectResult;

        var aliasCreatedUser = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Username == "alias_create");
        result.StoredAliasCreateOfficeCode = aliasCreatedUser.OfficeCode;

        var jwtFactory = new JwtTokenFactory(Options.Create(new JwtOptions()));
        var jwtResponse = jwtFactory.Create(new UserAccount
        {
            Username = "jwt_alias",
            Role = "User",
            OfficeCode = AliasItworld,
            IsActive = true,
            Permissions = new List<UserPermission>()
        });
        result.JwtResponseOfficeCode = jwtResponse.User.OfficeCode;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(jwtResponse.AccessToken);
        result.JwtClaimOfficeCode = token.Claims.FirstOrDefault(claim => claim.Type == "office")?.Value;

        result.IsCanonical =
            result.SeededUsers.All(user => OfficeCodeCatalog.IsCanonicalOfficeCode(user.OfficeCode)) &&
            result.GetAllOfficeCodes.All(OfficeCodeCatalog.IsCanonicalOfficeCode) &&
            result.AliasCreateSucceeded &&
            string.Equals(result.AliasCreateOfficeCode, OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase) &&
            result.InvalidOfficeRejected &&
            string.Equals(result.StoredAliasCreateOfficeCode, OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(result.JwtResponseOfficeCode, OfficeCodeCatalog.Itworld, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(result.JwtClaimOfficeCode, OfficeCodeCatalog.Itworld, StringComparison.OrdinalIgnoreCase);
    }
}

static bool IsCanonicalWarehouseCode(string code)
    => code is OfficeCodeCatalog.UsenetMainWarehouse or OfficeCodeCatalog.ItworldMainWarehouse or OfficeCodeCatalog.YeonsuMainWarehouse;

file sealed class FakeHostEnvironment : IHostEnvironment
{
    public FakeHostEnvironment(string runtimeDir)
    {
        ApplicationName = "OfficeCodeVerifier";
        EnvironmentName = Environments.Development;
        ContentRootPath = runtimeDir;
        ContentRootFileProvider = new PhysicalFileProvider(runtimeDir);
    }

    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; }
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}

file sealed class FakeTenantDatabaseConnectionResolver : ITenantDatabaseConnectionResolver
{
    private readonly TenantDatabaseConnectionInfo _current;

    public FakeTenantDatabaseConnectionResolver(string connectionString)
    {
        _current = new TenantDatabaseConnectionInfo
        {
            UseSqlite = true,
            ConnectionString = connectionString,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            IsControlPlane = true,
            IsDedicatedBusinessDatabase = false
        };
    }

    public TenantDatabaseConnectionInfo ResolveCurrent() => _current;
    public TenantDatabaseConnectionInfo ResolveCentral() => _current;
    public TenantDatabaseConnectionInfo ResolveBusinessTenant(string? tenantCode) => _current;
    public IReadOnlyList<TenantDatabaseConnectionInfo> GetDedicatedBusinessConnections() => Array.Empty<TenantDatabaseConnectionInfo>();
}

file sealed class StubCentralFileStorage : ICentralFileStorage
{
    public StubCentralFileStorage(string runtimeDir)
    {
        RootPath = Path.Combine(runtimeDir, "file-storage");
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public Task<string> SaveBytesAsync(string area, string ownerId, Guid fileId, string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        var areaDir = Path.Combine(RootPath, area, ownerId);
        Directory.CreateDirectory(areaDir);
        var path = Path.Combine(areaDir, $"{fileId:N}_{fileName}");
        File.WriteAllBytes(path, content);
        return Task.FromResult(path);
    }

    public byte[] ReadBytes(string? storedPath, byte[]? fallback = null)
        => !string.IsNullOrWhiteSpace(storedPath) && File.Exists(storedPath)
            ? File.ReadAllBytes(storedPath)
            : fallback ?? Array.Empty<byte>();

    public void DeleteIfExists(string? storedPath)
    {
        if (!string.IsNullOrWhiteSpace(storedPath) && File.Exists(storedPath))
            File.Delete(storedPath);
    }
}

file sealed class FakeCurrentUserContext : ICurrentUserContext
{
    public Guid? UserId => Guid.Parse("11111111-1111-1111-1111-111111111111");
    public string Username => "admin";
    public string TenantCode => TenantScopeCatalog.UsenetGroup;
    public string OfficeCode => OfficeCodeCatalog.Usenet;
    public string ScopeType => TenantScopeCatalog.ScopeAdmin;
    public bool IsAdmin => true;
    public bool IsGodMode => false;
    public bool HasPermission(string permission) => true;
}

file sealed class VerificationResult
{
    public LocalVerificationResult Local { get; set; } = new();
    public ServerVerificationResult Server { get; set; } = new();
}

file sealed class LocalVerificationResult
{
    public bool IsCanonical { get; set; }
    public List<string> OfficeCodes { get; set; } = new();
    public List<string> WarehouseCodes { get; set; } = new();
    public List<string> CompanyProfileOfficeCodes { get; set; } = new();
    public List<string> CustomerOfficeCodes { get; set; } = new();
    public List<string> InvoiceOfficeCodes { get; set; } = new();
    public List<string> InvoiceWarehouseCodes { get; set; } = new();
    public List<string> TransactionOfficeCodes { get; set; } = new();
    public List<string> AuditOfficeCodes { get; set; } = new();
    public List<string> RentalManagementCompanyCodes { get; set; } = new();
    public List<string> RentalBillingOfficeCodes { get; set; } = new();
    public List<string> RentalBillingCompanyCodes { get; set; } = new();
    public List<string> RentalAssetOfficeCodes { get; set; } = new();
    public List<string> RentalAssetCompanyCodes { get; set; } = new();
    public List<string> RentalBillingLogOfficeCodes { get; set; } = new();
    public List<string> InventoryWarehouseCodes { get; set; } = new();
    public List<string> StockLayerWarehouseCodes { get; set; } = new();
    public List<string> CostAllocationWarehouseCodes { get; set; } = new();
    public List<string> ItemWarehouseCodes { get; set; } = new();
    public List<string> SerialLedgerWarehouseCodes { get; set; } = new();
    public List<string> TransferWarehouseCodes { get; set; } = new();
    public string? CachedOfficeCode { get; set; }
    public string? RuntimeSessionOfficeCode { get; set; }
}

file sealed class ServerVerificationResult
{
    public bool IsCanonical { get; set; }
    public List<UserOfficeRow> SeededUsers { get; set; } = new();
    public List<string> GetAllOfficeCodes { get; set; } = new();
    public bool AliasCreateSucceeded { get; set; }
    public string? AliasCreateOfficeCode { get; set; }
    public bool InvalidOfficeRejected { get; set; }
    public string? StoredAliasCreateOfficeCode { get; set; }
    public string? JwtResponseOfficeCode { get; set; }
    public string? JwtClaimOfficeCode { get; set; }
}

file sealed record UserOfficeRow(string Username, string OfficeCode);
