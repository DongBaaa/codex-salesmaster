using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class TaxInvoiceNumberAssignmentTests
{
    [Fact]
    public async Task EnsureAssigned_AssignsNextMonthlyTaxInvoiceNumber_WhenIssuedWithoutNumber()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = CreateDbContext(connection);

        var juneCustomer1 = CreateCustomer();
        var juneCustomer2 = CreateCustomer();
        var mayCustomer = CreateCustomer();
        dbContext.Customers.AddRange(juneCustomer1, juneCustomer2, mayCustomer);
        dbContext.Invoices.AddRange(
            new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = juneCustomer1.Id,
                InvoiceDate = new DateOnly(2026, 6, 1),
                TaxInvoiceIssued = true,
                TaxInvoiceNumber = "TAX-202606-0001"
            },
            new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = juneCustomer2.Id,
                InvoiceDate = new DateOnly(2026, 6, 2),
                TaxInvoiceIssued = true,
                TaxInvoiceNumber = "TAX-202606-0007",
                IsDeleted = true
            },
            new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = mayCustomer.Id,
                InvoiceDate = new DateOnly(2026, 5, 31),
                TaxInvoiceIssued = true,
                TaxInvoiceNumber = "TAX-202605-0099"
            });
        await dbContext.SaveChangesAsync();

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            InvoiceDate = new DateOnly(2026, 6, 25),
            TaxInvoiceIssued = true
        };

        var assigned = await TaxInvoiceNumberAssignmentService.EnsureAssignedAsync(dbContext, invoice);

        Assert.Equal("TAX-202606-0008", assigned);
        Assert.Equal("TAX-202606-0008", invoice.TaxInvoiceNumber);
    }

    [Fact]
    public async Task EnsureAssigned_ClearsTaxInvoiceNumber_WhenInvoiceIsNotIssued()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = CreateDbContext(connection);
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            InvoiceDate = new DateOnly(2026, 6, 25),
            TaxInvoiceIssued = false,
            TaxInvoiceNumber = "TAX-202606-0008"
        };

        var assigned = await TaxInvoiceNumberAssignmentService.EnsureAssignedAsync(dbContext, invoice);

        Assert.Null(assigned);
        Assert.Equal(string.Empty, invoice.TaxInvoiceNumber);
    }

    [Fact]
    public void InvoiceMapping_PreservesExistingTaxInvoiceNumber_WhenIssuedDtoOmitsNumber()
    {
        var customerId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            InvoiceDate = new DateOnly(2026, 6, 25),
            VoucherType = VoucherType.Sales,
            TaxInvoiceIssued = true,
            TaxInvoiceNumber = "TAX-202606-0008"
        };

        invoice.Apply(new InvoiceDto
        {
            Id = invoice.Id,
            CustomerId = customerId,
            InvoiceDate = invoice.InvoiceDate,
            VoucherType = VoucherType.Sales,
            TaxInvoiceIssued = true,
            TaxInvoiceNumber = string.Empty,
            Lines = []
        });

        Assert.True(invoice.TaxInvoiceIssued);
        Assert.Equal("TAX-202606-0008", invoice.TaxInvoiceNumber);
    }

    private static AppDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var dbContext = new AppDbContext(options, new TestCurrentUserContext(), new RevisionClock());
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static Customer CreateCustomer() =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = $"세금계산서 테스트 {Guid.NewGuid():N}",
            NameMatchKey = Guid.NewGuid().ToString("N"),
            TradeType = CustomerClassificationNormalizer.Sales
        };

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = "tax-number-test";
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; } = true;
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();

        public bool HasPermission(string permission) => true;
    }
}
