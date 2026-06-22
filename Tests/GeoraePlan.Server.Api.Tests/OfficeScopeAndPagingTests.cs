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
    public async Task OfficeOnlyUser_OperationalScopesHideSharedResponsibleRows_WhenOwnerOfficeBelongsToAnotherTenant()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet_user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var dbContext = CreateDbContext(currentUser);

        var leakedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "MISMATCH-SHARED-CUSTOMER",
            NameMatchKey = "MISMATCHSHAREDCUSTOMER",
            TradeType = "Sales"
        };
        var leakedInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = leakedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Shared,
            InvoiceNumber = "MISMATCH-SHARED-INVOICE",
            InvoiceDate = new DateOnly(2026, 6, 23)
        };
        var leakedProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Shared,
            ProfileKey = "MISMATCH-SHARED-PROFILE",
            CustomerId = leakedCustomer.Id,
            CustomerName = leakedCustomer.NameOriginal
        };

        dbContext.Customers.Add(leakedCustomer);
        dbContext.Invoices.Add(leakedInvoice);
        dbContext.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = leakedInvoice.Id,
            PaymentDate = new DateOnly(2026, 6, 23),
            Amount = 100m,
            Note = "MISMATCH-SHARED-PAYMENT"
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = leakedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Shared,
            TransactionKind = "MISMATCH-SHARED-TRANSACTION",
            ReceiptTotal = 100m
        });
        dbContext.RentalBillingProfiles.Add(leakedProfile);
        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Shared,
            AssetKey = "MISMATCH-SHARED-ASSET",
            CustomerId = leakedCustomer.Id,
            BillingProfileId = leakedProfile.Id
        });
        dbContext.RentalBillingLogs.Add(new RentalBillingLog
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Shared,
            BillingProfileId = leakedProfile.Id,
            BillingYearMonth = "2026-06"
        });
        await dbContext.SaveChangesAsync();

        var service = new OfficeScopeService(currentUser, dbContext);

        Assert.False(service.CanReadOfficeForCustomers(leakedCustomer.ResponsibleOfficeCode, leakedCustomer.TenantCode, leakedCustomer.OfficeCode));
        Assert.Empty(await service.ApplyCustomerScope(dbContext.Customers.AsNoTracking()).ToListAsync());
        Assert.Empty(await service.ApplyInvoiceScope(dbContext.Invoices.AsNoTracking()).ToListAsync());
        Assert.Empty(await service.ApplyPaymentScope(dbContext.Payments.AsNoTracking().Include(payment => payment.Invoice)).ToListAsync());
        Assert.Empty(await service.ApplyTransactionScope(dbContext.Transactions.AsNoTracking()).ToListAsync());
        Assert.Empty(await service.ApplyRentalBillingProfileScope(dbContext.RentalBillingProfiles.AsNoTracking()).ToListAsync());
        Assert.Empty(await service.ApplyRentalAssetScope(dbContext.RentalAssets.AsNoTracking()).ToListAsync());
        Assert.Empty(await service.ApplyRentalBillingLogScope(dbContext.RentalBillingLogs.AsNoTracking()).ToListAsync());
    }

    [Fact]
    public async Task OfficeOnlyUser_OperationalScopesUseOwnerOffice_WhenResponsibleOfficeIsBlank()
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
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = string.Empty,
            NameOriginal = "VISIBLE-BLANK-CUSTOMER",
            NameMatchKey = "VISIBLEBLANKCUSTOMER",
            TradeType = "Sales"
        };
        var hiddenCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = string.Empty,
            NameOriginal = "HIDDEN-BLANK-CUSTOMER",
            NameMatchKey = "HIDDENBLANKCUSTOMER",
            TradeType = "Sales"
        };
        var visibleInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = visibleCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = string.Empty,
            InvoiceNumber = "INV-BLANK-VISIBLE",
            InvoiceDate = new DateOnly(2026, 6, 22)
        };
        var hiddenInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = hiddenCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = string.Empty,
            InvoiceNumber = "INV-BLANK-HIDDEN",
            InvoiceDate = new DateOnly(2026, 6, 22)
        };
        var visiblePayment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = visibleInvoice.Id,
            PaymentDate = new DateOnly(2026, 6, 22),
            Amount = 10_000m,
            Note = "PAYMENT-BLANK-VISIBLE"
        };
        var hiddenPayment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = hiddenInvoice.Id,
            PaymentDate = new DateOnly(2026, 6, 22),
            Amount = 10_000m,
            Note = "PAYMENT-BLANK-HIDDEN"
        };
        var visibleTransaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = visibleCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = string.Empty,
            TransactionKind = "TRANSACTION-BLANK-VISIBLE",
            ReceiptTotal = 10_000m
        };
        var hiddenTransaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = hiddenCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = string.Empty,
            TransactionKind = "TRANSACTION-BLANK-HIDDEN",
            ReceiptTotal = 10_000m
        };
        var visibleProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = string.Empty,
            ProfileKey = "PROFILE-BLANK-VISIBLE",
            CustomerId = visibleCustomer.Id,
            CustomerName = visibleCustomer.NameOriginal
        };
        var hiddenProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = string.Empty,
            ProfileKey = "PROFILE-BLANK-HIDDEN",
            CustomerId = hiddenCustomer.Id,
            CustomerName = hiddenCustomer.NameOriginal
        };

        dbContext.Customers.AddRange(visibleCustomer, hiddenCustomer);
        dbContext.Invoices.AddRange(visibleInvoice, hiddenInvoice);
        dbContext.Payments.AddRange(visiblePayment, hiddenPayment);
        dbContext.Transactions.AddRange(visibleTransaction, hiddenTransaction);
        dbContext.TransactionAttachments.AddRange(
            new TransactionAttachment
            {
                Id = Guid.NewGuid(),
                TransactionId = visibleTransaction.Id,
                FileName = "visible.pdf",
                MimeType = "application/pdf"
            },
            new TransactionAttachment
            {
                Id = Guid.NewGuid(),
                TransactionId = hiddenTransaction.Id,
                FileName = "hidden.pdf",
                MimeType = "application/pdf"
            });
        dbContext.RentalBillingProfiles.AddRange(visibleProfile, hiddenProfile);
        dbContext.RentalAssets.AddRange(
            new RentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = string.Empty,
                AssetKey = "ASSET-BLANK-VISIBLE",
                CustomerId = visibleCustomer.Id,
                BillingProfileId = visibleProfile.Id
            },
            new RentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = string.Empty,
                AssetKey = "ASSET-BLANK-HIDDEN",
                CustomerId = hiddenCustomer.Id,
                BillingProfileId = hiddenProfile.Id
            });
        dbContext.RentalBillingLogs.AddRange(
            new RentalBillingLog
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = string.Empty,
                BillingProfileId = visibleProfile.Id,
                BillingYearMonth = "202606"
            },
            new RentalBillingLog
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = string.Empty,
                BillingProfileId = hiddenProfile.Id,
                BillingYearMonth = "202607"
            });
        await dbContext.SaveChangesAsync();

        var service = new OfficeScopeService(currentUser, dbContext);

        var customers = await service.ApplyCustomerScope(dbContext.Customers.AsNoTracking())
            .Select(customer => customer.NameOriginal)
            .ToListAsync();
        var invoices = await service.ApplyInvoiceScope(dbContext.Invoices.AsNoTracking())
            .Select(invoice => invoice.InvoiceNumber)
            .ToListAsync();
        var payments = await service.ApplyPaymentScope(dbContext.Payments.AsNoTracking().Include(payment => payment.Invoice))
            .Select(payment => payment.Note)
            .ToListAsync();
        var transactions = await service.ApplyTransactionScope(dbContext.Transactions.AsNoTracking())
            .Select(transaction => transaction.TransactionKind)
            .ToListAsync();
        var transactionAttachments = await service.ApplyTransactionAttachmentScope(dbContext.TransactionAttachments.AsNoTracking().Include(attachment => attachment.Transaction))
            .Select(attachment => attachment.FileName)
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

        Assert.Equal(["VISIBLE-BLANK-CUSTOMER"], customers);
        Assert.Equal(["INV-BLANK-VISIBLE"], invoices);
        Assert.Equal(["PAYMENT-BLANK-VISIBLE"], payments);
        Assert.Equal(["TRANSACTION-BLANK-VISIBLE"], transactions);
        Assert.Equal(["visible.pdf"], transactionAttachments);
        Assert.Equal(["PROFILE-BLANK-VISIBLE"], profiles);
        Assert.Equal(["ASSET-BLANK-VISIBLE"], assets);
        Assert.Equal(["202606"], logs);

        Assert.False(service.CanReadOfficeForInvoices(string.Empty, TenantScopeCatalog.UsenetGroup));
        Assert.False(service.CanWriteOfficeForInvoices(string.Empty, TenantScopeCatalog.UsenetGroup));
        Assert.False(service.CanReadOfficeForPayments(string.Empty, TenantScopeCatalog.UsenetGroup));
        Assert.False(service.CanWriteOfficeForPayments(string.Empty, TenantScopeCatalog.UsenetGroup));
        Assert.True(service.CanReadOfficeForInvoices(string.Empty, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu));
        Assert.True(service.CanWriteOfficeForInvoices(string.Empty, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu));
        Assert.True(service.CanReadOfficeForPayments(string.Empty, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu));
        Assert.True(service.CanWriteOfficeForPayments(string.Empty, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu));
        Assert.False(service.CanReadOfficeForInvoices(string.Empty, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet));
        Assert.False(service.CanWriteOfficeForPayments(string.Empty, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet));
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
    public async Task CustomersController_GetDetail_ReturnsRecentPaymentsOutsideRecentInvoiceWindow()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PAYMENT-HISTORY-CUSTOMER",
            NameMatchKey = "PAYMENTHISTORYCUSTOMER",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);

        var oldInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "OLD-INVOICE-WITH-RECENT-PAYMENT",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2024, 1, 1),
            TotalAmount = 10000m
        };
        dbContext.Invoices.Add(oldInvoice);

        dbContext.Invoices.AddRange(Enumerable.Range(1, 25).Select(index => new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = $"RECENT-INVOICE-{index:D2}",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 1, 1).AddDays(index),
            TotalAmount = 1000m + index
        }));

        var recentPayment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = oldInvoice.Id,
            PaymentDate = new DateOnly(2026, 6, 19),
            Amount = 7000m,
            Note = "recent payment on old invoice"
        };
        var attachment = new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = recentPayment.Id,
            AttachmentType = "PDF",
            FileName = "old-invoice-payment.pdf",
            MimeType = "application/pdf",
            FileSize = 12,
            FileHash = "hash",
            Description = "recent evidence",
            UploadedAtUtc = new DateTime(2026, 6, 19, 1, 0, 0, DateTimeKind.Utc)
        };
        dbContext.Payments.Add(recentPayment);
        dbContext.PaymentAttachments.Add(attachment);
        await dbContext.SaveChangesAsync();

        var controller = new CustomersController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage());

        var response = await controller.GetDetail(customer.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var detail = Assert.IsType<CustomerDetailDto>(ok.Value);

        Assert.Equal(20, detail.RecentInvoices.Count);
        Assert.DoesNotContain(detail.RecentInvoices, invoice => invoice.Id == oldInvoice.Id);

        var payment = Assert.Single(detail.RecentPayments, row => row.PaymentId == recentPayment.Id);
        Assert.Equal(oldInvoice.Id, payment.InvoiceId);
        Assert.Equal("OLD-INVOICE-WITH-RECENT-PAYMENT", payment.InvoiceNumber);
        Assert.Equal(VoucherType.Sales, payment.VoucherType);
        Assert.Equal(recentPayment.PaymentDate, payment.PaymentDate);
        Assert.Equal(recentPayment.Amount, payment.Amount);
        Assert.Single(payment.Attachments);
    }

    [Fact]
    public async Task InvoicesController_Create_RejectsDeletedLineItemReference()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REST-DELETED-LINE-ITEM-CUSTOMER",
            NameMatchKey = "RESTDELETEDLINEITEMCUSTOMER",
            TradeType = "매출"
        };
        var deletedItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REST-DELETED-LINE-ITEM",
            NameMatchKey = "RESTDELETEDLINEITEM",
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(deletedItem);
        await dbContext.SaveChangesAsync();

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new InvoiceNumberService(dbContext),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var invoiceId = Guid.NewGuid();
        var response = await controller.Create(new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            VoucherType = VoucherType.Sales,
            VatMode = InvoiceVatModes.None,
            InvoiceDate = new DateOnly(2026, 6, 19),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemId = deletedItem.Id,
                    ItemNameOriginal = deletedItem.NameOriginal,
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 1000m,
                    LineAmount = 1000m
                }
            ]
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("Referenced invoice line item was not found", badRequest.Value?.ToString(), StringComparison.Ordinal);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(invoice => invoice.Id == invoiceId));
        Assert.False(await dbContext.InvoiceLines.IgnoreQueryFilters().AnyAsync(line => line.InvoiceId == invoiceId));
    }

    [Fact]
    public async Task InvoicesController_Create_RejectsDeletedCustomerReference()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var deletedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REST-DELETED-INVOICE-CUSTOMER",
            NameMatchKey = "RESTDELETEDINVOICECUSTOMER",
            TradeType = "매출",
            IsDeleted = true
        };
        dbContext.Customers.Add(deletedCustomer);
        await dbContext.SaveChangesAsync();

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new InvoiceNumberService(dbContext),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var invoiceId = Guid.NewGuid();
        var response = await controller.Create(new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = deletedCustomer.Id,
            CustomerName = deletedCustomer.NameOriginal,
            TenantCode = deletedCustomer.TenantCode,
            OfficeCode = deletedCustomer.OfficeCode,
            ResponsibleOfficeCode = deletedCustomer.ResponsibleOfficeCode,
            VoucherType = VoucherType.Sales,
            VatMode = InvoiceVatModes.None,
            InvoiceDate = new DateOnly(2026, 6, 19)
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal("Referenced customer was not found.", badRequest.Value);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(invoice => invoice.Id == invoiceId));
    }

    [Fact]
    public async Task InvoicesController_Update_RejectsDeletedCustomerReference()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var activeCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REST-ACTIVE-INVOICE-CUSTOMER",
            NameMatchKey = "RESTACTIVEINVOICECUSTOMER",
            TradeType = "매출"
        };
        var deletedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REST-DELETED-INVOICE-UPDATE-CUSTOMER",
            NameMatchKey = "RESTDELETEDINVOICEUPDATECUSTOMER",
            TradeType = "매출",
            IsDeleted = true
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = activeCustomer.Id,
            TenantCode = activeCustomer.TenantCode,
            OfficeCode = activeCustomer.OfficeCode,
            ResponsibleOfficeCode = activeCustomer.ResponsibleOfficeCode,
            InvoiceNumber = "REST-UPDATE-DELETED-CUSTOMER",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 19),
            Memo = "before"
        };
        dbContext.Customers.AddRange(activeCustomer, deletedCustomer);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new InvoiceNumberService(dbContext),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Update(invoice.Id, new InvoiceDto
        {
            Id = invoice.Id,
            CustomerId = deletedCustomer.Id,
            CustomerName = deletedCustomer.NameOriginal,
            TenantCode = deletedCustomer.TenantCode,
            OfficeCode = deletedCustomer.OfficeCode,
            ResponsibleOfficeCode = deletedCustomer.ResponsibleOfficeCode,
            InvoiceNumber = invoice.InvoiceNumber,
            VoucherType = VoucherType.Sales,
            VatMode = InvoiceVatModes.None,
            InvoiceDate = invoice.InvoiceDate,
            Memo = "after"
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal("Referenced customer was not found.", badRequest.Value);

        dbContext.ChangeTracker.Clear();
        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(row => row.Id == invoice.Id);
        Assert.Equal(activeCustomer.Id, storedInvoice.CustomerId);
        Assert.Equal("before", storedInvoice.Memo);
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
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

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
