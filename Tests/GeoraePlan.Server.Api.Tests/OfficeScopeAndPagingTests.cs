using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class OfficeScopeAndPagingTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public OfficeScopeAndPagingTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task OfficeOnlyUser_ReadsSharedSourceOfficeData_WhenSharingPolicyExists()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "yeonsu_user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var dbContext = CreateDbContext(currentUser);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            Id = Guid.NewGuid(),
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            ShareItems = true,
            IsActive = true
        });

        var usenetCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "USENET-CUSTOMER",
            NameMatchKey = "USENETCUSTOMER",
            TradeType = "매출"
        };
        var yeonsuCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "YEONSU-CUSTOMER",
            NameMatchKey = "YEONSUCUSTOMER",
            TradeType = "매출"
        };

        var usenetItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "USENET-ITEM",
            NameMatchKey = "USENETITEM"
        };
        var yeonsuItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "YEONSU-ITEM",
            NameMatchKey = "YEONSUITEM"
        };

        dbContext.Customers.AddRange(usenetCustomer, yeonsuCustomer);
        dbContext.Items.AddRange(usenetItem, yeonsuItem);
        await dbContext.SaveChangesAsync();

        var service = new OfficeScopeService(currentUser, dbContext);

        var visibleCustomers = await service.ApplyCustomerScope(dbContext.Customers.AsNoTracking())
            .OrderBy(x => x.NameOriginal)
            .Select(x => x.NameOriginal)
            .ToListAsync();
        var visibleItems = await service.ApplyItemScope(dbContext.Items.AsNoTracking())
            .OrderBy(x => x.NameOriginal)
            .Select(x => x.NameOriginal)
            .ToListAsync();

        Assert.Equal(["USENET-CUSTOMER", "YEONSU-CUSTOMER"], visibleCustomers);
        Assert.Equal(["USENET-ITEM", "YEONSU-ITEM"], visibleItems);
        Assert.False(service.CanWriteOfficeForCustomers(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
        Assert.False(service.CanWriteOfficeForItems(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
    }

    [Fact]
    public async Task YeonsuUser_ReadsOwnerUsenetButResponsibleYeonsuOperationalRows()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "yeonsu_user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var dbContext = CreateDbContext(currentUser);

        var visibleCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "VISIBLE-CUSTOMER",
            NameMatchKey = "VISIBLECUSTOMER",
            TradeType = "매출"
        };
        var hiddenCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "HIDDEN-CUSTOMER",
            NameMatchKey = "HIDDENCUSTOMER",
            TradeType = "매출"
        };

        dbContext.Customers.AddRange(visibleCustomer, hiddenCustomer);
        dbContext.Invoices.AddRange(
            new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = visibleCustomer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                InvoiceNumber = "INV-001",
                InvoiceDate = new DateOnly(2026, 4, 1)
            },
            new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = hiddenCustomer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "INV-002",
                InvoiceDate = new DateOnly(2026, 4, 1)
            });
        dbContext.RentalBillingProfiles.AddRange(
            new RentalBillingProfile
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ProfileKey = "PROFILE-VISIBLE",
                CustomerId = visibleCustomer.Id,
                CustomerName = visibleCustomer.NameOriginal
            },
            new RentalBillingProfile
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "PROFILE-HIDDEN",
                CustomerId = hiddenCustomer.Id,
                CustomerName = hiddenCustomer.NameOriginal
            });
        await dbContext.SaveChangesAsync();

        var service = new OfficeScopeService(currentUser, dbContext);

        var customers = await service.ApplyCustomerScope(dbContext.Customers.AsNoTracking())
            .Select(entity => entity.NameOriginal)
            .ToListAsync();
        var invoices = await service.ApplyInvoiceScope(dbContext.Invoices.AsNoTracking())
            .Select(entity => entity.InvoiceNumber)
            .ToListAsync();
        var profiles = await service.ApplyRentalBillingProfileScope(dbContext.RentalBillingProfiles.AsNoTracking())
            .Select(entity => entity.ProfileKey)
            .ToListAsync();

        Assert.Equal(["VISIBLE-CUSTOMER"], customers);
        Assert.Equal(["INV-001"], invoices);
        Assert.Equal(["PROFILE-VISIBLE"], profiles);
        Assert.True(service.CanReadOfficeForCustomers(OfficeCodeCatalog.Yeonsu, TenantScopeCatalog.UsenetGroup));
        Assert.False(service.CanReadOfficeForCustomers(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
    }

    [Fact]
    public async Task YeonsuUser_PaymentAndRentalScopes_ReturnOnlyResponsibleOfficeRows()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "yeonsu_user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var dbContext = CreateDbContext(currentUser);

        var visibleCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "VISIBLE-CUSTOMER",
            NameMatchKey = "VISIBLECUSTOMER",
            TradeType = "Sales"
        };
        var hiddenCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "HIDDEN-CUSTOMER",
            NameMatchKey = "HIDDENCUSTOMER",
            TradeType = "Sales"
        };
        var visibleInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = visibleCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            InvoiceNumber = "PAY-VISIBLE",
            InvoiceDate = new DateOnly(2026, 5, 1)
        };
        var hiddenInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = hiddenCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAY-HIDDEN",
            InvoiceDate = new DateOnly(2026, 5, 1)
        };
        var visibleProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "RENTAL-VISIBLE",
            CustomerId = visibleCustomer.Id,
            CustomerName = visibleCustomer.NameOriginal
        };
        var hiddenProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "RENTAL-HIDDEN",
            CustomerId = hiddenCustomer.Id,
            CustomerName = hiddenCustomer.NameOriginal
        };

        dbContext.Customers.AddRange(visibleCustomer, hiddenCustomer);
        dbContext.Invoices.AddRange(visibleInvoice, hiddenInvoice);
        dbContext.Payments.AddRange(
            new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = visibleInvoice.Id,
                PaymentDate = new DateOnly(2026, 5, 2),
                Amount = 100m,
                Note = "PAYMENT-VISIBLE"
            },
            new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = hiddenInvoice.Id,
                PaymentDate = new DateOnly(2026, 5, 2),
                Amount = 100m,
                Note = "PAYMENT-HIDDEN"
            });
        dbContext.Transactions.AddRange(
            new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = visibleCustomer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                TransactionKind = "TRANSACTION-VISIBLE",
                ReceiptTotal = 100m
            },
            new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = hiddenCustomer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionKind = "TRANSACTION-HIDDEN",
                ReceiptTotal = 100m
            });
        dbContext.RentalBillingProfiles.AddRange(visibleProfile, hiddenProfile);
        dbContext.RentalAssets.AddRange(
            new RentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                AssetKey = "ASSET-VISIBLE",
                CustomerId = visibleCustomer.Id,
                BillingProfileId = visibleProfile.Id
            },
            new RentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "ASSET-HIDDEN",
                CustomerId = hiddenCustomer.Id,
                BillingProfileId = hiddenProfile.Id
            });
        dbContext.RentalBillingLogs.AddRange(
            new RentalBillingLog
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                BillingProfileId = visibleProfile.Id,
                BillingYearMonth = "2026-05"
            },
            new RentalBillingLog
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = hiddenProfile.Id,
                BillingYearMonth = "2026-06"
            });
        await dbContext.SaveChangesAsync();

        var service = new OfficeScopeService(currentUser, dbContext);

        var payments = await service.ApplyPaymentScope(dbContext.Payments.AsNoTracking().Include(payment => payment.Invoice))
            .Select(payment => payment.Note)
            .ToListAsync();
        var transactions = await service.ApplyTransactionScope(dbContext.Transactions.AsNoTracking())
            .Select(transaction => transaction.TransactionKind)
            .ToListAsync();
        var profiles = await service.ApplyRentalBillingProfileScope(dbContext.RentalBillingProfiles.AsNoTracking())
            .Select(profile => profile.ProfileKey)
            .ToListAsync();
        var assets = await service.ApplyRentalAssetScope(dbContext.RentalAssets.AsNoTracking())
            .Select(asset => asset.AssetKey)
            .ToListAsync();
        var logs = await service.ApplyRentalBillingLogScope(dbContext.RentalBillingLogs.AsNoTracking())
            .Select(log => log.BillingYearMonth)
            .ToListAsync();

        Assert.Equal(["PAYMENT-VISIBLE"], payments);
        Assert.Equal(["TRANSACTION-VISIBLE"], transactions);
        Assert.Equal(["RENTAL-VISIBLE"], profiles);
        Assert.Equal(["ASSET-VISIBLE"], assets);
        Assert.Equal(["2026-05"], logs);
        Assert.True(service.CanWriteOfficeForPayments(OfficeCodeCatalog.Yeonsu, TenantScopeCatalog.UsenetGroup));
        Assert.False(service.CanWriteOfficeForPayments(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
        Assert.True(service.CanWriteOfficeForRentals(OfficeCodeCatalog.Yeonsu, TenantScopeCatalog.UsenetGroup));
        Assert.False(service.CanWriteOfficeForRentals(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
    }

    [Fact]
    public async Task UsenetOfficeUser_DoesNotGainAdministrativeWriteAccess_OnlyFromOfficeCode()
    {
        var userId = Guid.NewGuid();
        var currentUser = new TestCurrentUserContext
        {
            UserId = userId,
            Username = "usenet_user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsAdmin = false,
            IsGodMode = false
        };

        await using var dbContext = CreateDbContext(currentUser);
        dbContext.Users.Add(new UserAccount
        {
            Id = userId,
            Username = currentUser.Username,
            PasswordHash = "hash",
            Role = "User",
            TenantCode = currentUser.TenantCode,
            OfficeCode = currentUser.OfficeCode,
            ScopeType = currentUser.ScopeType,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        var service = new OfficeScopeService(currentUser, dbContext);

        Assert.False(service.HasAdministrativeWriteAccess);
        Assert.False(await service.HasAdministrativeWriteAccessAsync());
    }

    [Fact]
    public async Task TenantAllUser_CanReadAndWrite_AllTenantCustomerScopes_WithoutSharingPolicy()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet_user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeTenantAll
        };

        await using var dbContext = CreateDbContext(currentUser);
        dbContext.Customers.AddRange(
            new Customer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "USENET-CUSTOMER",
                NameMatchKey = "USENETCUSTOMER",
                TradeType = "매출"
            },
            new Customer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "YEONSU-CUSTOMER",
                NameMatchKey = "YEONSUCUSTOMER",
                TradeType = "매출"
            });
        await dbContext.SaveChangesAsync();

        var service = new OfficeScopeService(currentUser, dbContext);

        var visibleCustomers = await service.ApplyCustomerScope(dbContext.Customers.AsNoTracking())
            .OrderBy(x => x.NameOriginal)
            .Select(x => x.NameOriginal)
            .ToListAsync();

        Assert.Equal(["USENET-CUSTOMER", "YEONSU-CUSTOMER"], visibleCustomers);
        Assert.True(service.CanReadOfficeForCustomers(OfficeCodeCatalog.Yeonsu, TenantScopeCatalog.UsenetGroup));
        Assert.True(service.CanWriteOfficeForCustomers(OfficeCodeCatalog.Yeonsu, TenantScopeCatalog.UsenetGroup));

        var customerArea = Assert.Single(service.BuildCurrentScopeMatrix().Areas, area => area.AreaCode == "customers");
        Assert.Equal(
            [OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu],
            customerArea.ReadableOfficeCodes.OrderBy(code => code).ToArray());
        Assert.Equal(
            [OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu],
            customerArea.WritableOfficeCodes.OrderBy(code => code).ToArray());
    }

    [Fact]
    public async Task BuildCurrentScopeMatrix_IncludesReadableAndWritableOfficeSets()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "yeonsu_user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var dbContext = CreateDbContext(currentUser);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            Id = Guid.NewGuid(),
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            ShareItems = true,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        var service = new OfficeScopeService(currentUser, dbContext);
        var matrix = service.BuildCurrentScopeMatrix();

        var customerArea = Assert.Single(matrix.Areas, area => area.AreaCode == "customers");
        Assert.Equal([OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu], customerArea.ReadableOfficeCodes.OrderBy(code => code).ToArray());
        Assert.Equal([OfficeCodeCatalog.Yeonsu], customerArea.WritableOfficeCodes);

        var rentalArea = Assert.Single(matrix.Areas, area => area.AreaCode == "rentals");
        Assert.Equal([OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu], rentalArea.ReadableOfficeCodes.OrderBy(code => code).ToArray());
        Assert.Equal([OfficeCodeCatalog.Yeonsu], rentalArea.WritableOfficeCodes);
    }

    [Fact]
    public async Task OfficeOnlyUser_CanWriteSharedSourceOfficeData_WhenSharingPolicyAllowsTargetWrite()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "yeonsu_user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var dbContext = CreateDbContext(currentUser);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            Id = Guid.NewGuid(),
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            ShareItems = true,
            ShareInvoices = true,
            SharePayments = true,
            ShareContracts = true,
            ShareRentals = true,
            ShareDeliveries = true,
            AllowTargetWrite = true,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        var service = new OfficeScopeService(currentUser, dbContext);
        var matrix = service.BuildCurrentScopeMatrix();

        Assert.True(service.CanWriteOfficeForCustomers(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
        Assert.True(service.CanWriteOfficeForItems(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
        Assert.True(service.CanWriteOfficeForInvoices(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
        Assert.True(service.CanWriteOfficeForPayments(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
        Assert.True(service.CanWriteOfficeForContracts(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
        Assert.True(service.CanWriteOfficeForRentals(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
        Assert.True(service.CanWriteOfficeForDeliveries(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));

        var customerArea = Assert.Single(matrix.Areas, area => area.AreaCode == "customers");
        Assert.Equal(
            [OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu],
            customerArea.WritableOfficeCodes.OrderBy(code => code).ToArray());
    }


    [Fact]
    public async Task ScopeDiagnosticsController_Get_ReturnsCurrentScopeMatrix()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "yeonsu_user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var dbContext = CreateDbContext(currentUser);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            Id = Guid.NewGuid(),
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            ShareItems = true,
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        var controller = new ScopeDiagnosticsController(new OfficeScopeService(currentUser, dbContext));

        var response = controller.Get();
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var matrix = Assert.IsType<ScopeMatrixSnapshotDto>(ok.Value);

        Assert.Equal(OfficeCodeCatalog.Yeonsu, matrix.OfficeCode);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, matrix.TenantCode);

        var customerArea = Assert.Single(matrix.Areas, area => area.AreaCode == "customers");
        Assert.Equal([OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu], customerArea.ReadableOfficeCodes.OrderBy(code => code).ToArray());
        Assert.Equal([OfficeCodeCatalog.Yeonsu], customerArea.WritableOfficeCodes);
    }

    [Fact]
    public async Task CustomersController_GetAll_ReturnsAllScopedRows_WithoutHardcoded200Cap()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        dbContext.Customers.AddRange(Enumerable.Range(1, 260).Select(index => new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = $"CUSTOMER-{index:D3}",
            NameMatchKey = $"CUSTOMER{index:D3}",
            TradeType = "매출"
        }));
        await dbContext.SaveChangesAsync();

        var controller = new CustomersController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage());

        var response = await controller.GetAll(null, null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var items = Assert.IsType<List<CustomerDto>>(ok.Value);

        Assert.Equal(260, items.Count);
        Assert.Equal("CUSTOMER-001", items.First().NameOriginal);
        Assert.Equal("CUSTOMER-260", items.Last().NameOriginal);
    }

    [Fact]
    public async Task ItemsController_GetAll_AppliesSkipAndTake()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        dbContext.Items.AddRange(Enumerable.Range(1, 30).Select(index => new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = $"ITEM-{index:D3}",
            NameMatchKey = $"ITEM{index:D3}",
            CategoryName = "기타"
        }));
        await dbContext.SaveChangesAsync();

        var controller = new ItemsController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));

        var response = await controller.GetAll(null, null, 10, 5, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var items = Assert.IsType<List<ItemDto>>(ok.Value);

        Assert.Equal(5, items.Count);
        Assert.Equal(["ITEM-011", "ITEM-012", "ITEM-013", "ITEM-014", "ITEM-015"], items.Select(x => x.NameOriginal).ToArray());
    }

    [Fact]
    public async Task InvoicesController_GetAll_ForYeonsuUser_ReturnsOnlyScopedRows()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "yeonsu_user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var dbContext = CreateDbContext(currentUser);
        var visibleCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "VISIBLE-CUSTOMER",
            NameMatchKey = "VISIBLECUSTOMER",
            TradeType = "매출"
        };
        var hiddenCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "HIDDEN-CUSTOMER",
            NameMatchKey = "HIDDENCUSTOMER",
            TradeType = "매출"
        };

        dbContext.Customers.AddRange(visibleCustomer, hiddenCustomer);
        dbContext.Invoices.AddRange(
            new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = visibleCustomer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                InvoiceNumber = "INV-001",
                InvoiceDate = new DateOnly(2026, 4, 1)
            },
            new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = hiddenCustomer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "INV-002",
                InvoiceDate = new DateOnly(2026, 4, 2)
            });
        await dbContext.SaveChangesAsync();

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new InvoiceNumberService(dbContext),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()));

        var response = await controller.GetAll(null, null, 200, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var items = Assert.IsType<List<InvoiceDto>>(ok.Value);

        var visibleInvoice = Assert.Single(items);
        Assert.Equal("INV-001", visibleInvoice.InvoiceNumber);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        var dbContext = new AppDbContext(options, currentUser, revisionClock);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static TestCurrentUserContext CreateAdminUser()
        => new()
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = [];

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(string area, string ownerId, Guid fileId, string fileName, byte[] content, CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, fileName));

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null)
            => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
