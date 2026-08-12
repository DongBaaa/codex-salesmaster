using System.Reflection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class PeriodLedgerPaymentAggregationTests
{
    [Fact]
    public void BuildPaymentLedgerResult_PreservesIndependentSameDayEqualPayments()
    {
        var customerId = Guid.NewGuid();
        var paymentDate = new DateOnly(2026, 7, 23);
        var firstPaymentId = Guid.NewGuid();
        var secondPaymentId = Guid.NewGuid();
        var invoice = CreateInvoice(customerId, VoucherType.Sales);
        invoice.Payments =
        [
            CreatePayment(firstPaymentId, invoice.Id, paymentDate, 100_000m),
            CreatePayment(secondPaymentId, invoice.Id, paymentDate, 100_000m)
        ];

        var result = BuildResult([invoice], []);

        Assert.Equal(2, result.PaymentRows.Count);
        Assert.Equal(200_000m, result.Totals.ReceiptAmount);
        Assert.Equal(
            new[] { firstPaymentId, secondPaymentId }.OrderBy(id => id).ToArray(),
            result.PaymentRows.Select(row => row.PaymentId!.Value).OrderBy(id => id).ToArray());
    }

    [Fact]
    public void BuildPaymentLedgerResult_MergesOnlyMatchingIdTransactionAndPreservesEqualIndependentTransaction()
    {
        var customerId = Guid.NewGuid();
        var paymentDate = new DateOnly(2026, 7, 23);
        var paymentId = Guid.NewGuid();
        var independentTransactionId = Guid.NewGuid();
        var invoice = CreateInvoice(customerId, VoucherType.Sales);
        invoice.Payments =
        [
            CreatePayment(paymentId, invoice.Id, paymentDate, 100_000m)
        ];
        var transactions = new[]
        {
            CreateReceiptTransaction(paymentId, customerId, invoice.Id, paymentDate, 100_000m),
            CreateReceiptTransaction(independentTransactionId, customerId, null, paymentDate, 100_000m)
        };

        var result = BuildResult([invoice], transactions);

        Assert.Equal(2, result.PaymentRows.Count);
        Assert.Equal(200_000m, result.Totals.ReceiptAmount);

        var mergedRow = Assert.Single(result.PaymentRows, row => row.PaymentId == paymentId);
        Assert.Equal(paymentId, mergedRow.TransactionId);

        var independentRow = Assert.Single(result.PaymentRows, row => row.TransactionId == independentTransactionId);
        Assert.Null(independentRow.PaymentId);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("amount")]
    public void BuildPaymentLedgerResult_DoesNotMergeSameIdTransactionWhenStrongMirrorFieldsMismatch(
        string mismatch)
    {
        var customerId = Guid.NewGuid();
        var paymentDate = new DateOnly(2026, 7, 23);
        var paymentId = Guid.NewGuid();
        var invoice = CreateInvoice(customerId, VoucherType.Sales);
        invoice.Payments =
        [
            CreatePayment(paymentId, invoice.Id, paymentDate, 100_000m)
        ];
        var transaction = CreateReceiptTransaction(
            paymentId,
            customerId,
            invoice.Id,
            paymentDate,
            mismatch == "amount" ? 90_000m : 100_000m);
        if (mismatch == "tenant")
        {
            transaction.TenantCode = TenantScopeCatalog.Itworld;
            transaction.OfficeCode = OfficeCodeCatalog.Itworld;
            transaction.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
        }

        var result = BuildResult([invoice], [transaction]);

        Assert.Equal(2, result.PaymentRows.Count);
        Assert.Null(Assert.Single(result.PaymentRows, row => row.PaymentId == paymentId).TransactionId);
        Assert.Equal(
            paymentId,
            Assert.Single(result.PaymentRows, row => row.PaymentId is null).TransactionId);
        Assert.Equal(
            mismatch == "amount" ? 190_000m : 200_000m,
            result.Totals.ReceiptAmount);
    }

    [Fact]
    public void BuildPaymentLedgerResult_MergesUniqueStrongLegacyMirrorAndPreservesEqualIndependentTransaction()
    {
        var customerId = Guid.NewGuid();
        var paymentDate = new DateOnly(2026, 7, 23);
        var paymentId = Guid.NewGuid();
        var legacyMirrorId = Guid.NewGuid();
        var independentTransactionId = Guid.NewGuid();
        var invoice = CreateInvoice(customerId, VoucherType.Sales);
        invoice.Payments =
        [
            CreatePayment(paymentId, invoice.Id, paymentDate, 100_000m)
        ];
        var transactions = new[]
        {
            CreateReceiptTransaction(legacyMirrorId, customerId, invoice.Id, paymentDate, 100_000m),
            CreateReceiptTransaction(independentTransactionId, customerId, null, paymentDate, 100_000m)
        };

        var result = BuildResult([invoice], transactions);

        Assert.Equal(2, result.PaymentRows.Count);
        Assert.Equal(200_000m, result.Totals.ReceiptAmount);

        var mergedRow = Assert.Single(result.PaymentRows, row => row.PaymentId == paymentId);
        Assert.Equal(legacyMirrorId, mergedRow.TransactionId);

        var independentRow = Assert.Single(result.PaymentRows, row => row.TransactionId == independentTransactionId);
        Assert.Null(independentRow.PaymentId);
    }

    [Fact]
    public void BuildPaymentLedgerResult_DoesNotMergeLegacyCandidateOutsideInvoiceTenantScope()
    {
        var customerId = Guid.NewGuid();
        var paymentDate = new DateOnly(2026, 7, 23);
        var paymentId = Guid.NewGuid();
        var crossTenantTransactionId = Guid.NewGuid();
        var invoice = CreateInvoice(customerId, VoucherType.Sales);
        invoice.Payments =
        [
            CreatePayment(paymentId, invoice.Id, paymentDate, 100_000m)
        ];
        var crossTenantTransaction = CreateReceiptTransaction(
            crossTenantTransactionId,
            customerId,
            invoice.Id,
            paymentDate,
            100_000m);
        crossTenantTransaction.TenantCode = TenantScopeCatalog.Itworld;
        crossTenantTransaction.OfficeCode = OfficeCodeCatalog.Itworld;
        crossTenantTransaction.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;

        var result = BuildResult([invoice], [crossTenantTransaction]);

        Assert.Equal(2, result.PaymentRows.Count);
        Assert.Equal(200_000m, result.Totals.ReceiptAmount);
        Assert.Null(Assert.Single(result.PaymentRows, row => row.PaymentId == paymentId).TransactionId);
        Assert.Equal(
            crossTenantTransactionId,
            Assert.Single(result.PaymentRows, row => row.PaymentId is null).TransactionId);
    }

    [Fact]
    public void BuildPaymentLedgerResult_ClassifiesPurchaseInvoicePaymentAsOutflow()
    {
        var customerId = Guid.NewGuid();
        var paymentDate = new DateOnly(2026, 7, 23);
        var paymentId = Guid.NewGuid();
        var invoice = CreateInvoice(customerId, VoucherType.Purchase);
        invoice.Payments =
        [
            CreatePayment(paymentId, invoice.Id, paymentDate, 125_000m)
        ];
        var mirroredTransaction = CreatePaymentTransaction(
            paymentId,
            customerId,
            invoice.Id,
            paymentDate,
            125_000m);

        var result = BuildResult([invoice], [mirroredTransaction]);

        var row = Assert.Single(result.PaymentRows);
        Assert.Equal("지급(매입전표)", row.Division);
        Assert.Equal(0m, row.ReceiptAmount);
        Assert.Equal(125_000m, row.PaymentAmount);
        Assert.Equal(0m, result.Totals.ReceiptAmount);
        Assert.Equal(125_000m, result.Totals.PaymentAmount);
        Assert.Equal(paymentId, row.PaymentId);
        Assert.Equal(paymentId, row.TransactionId);
    }

    [Fact]
    public void BuildPaymentLedgerResult_ClassifiesProcurementInvoicePaymentAsOutflow()
    {
        var customerId = Guid.NewGuid();
        var paymentDate = new DateOnly(2026, 7, 23);
        var paymentId = Guid.NewGuid();
        var invoice = CreateInvoice(customerId, VoucherType.Procurement);
        invoice.Payments =
        [
            CreatePayment(paymentId, invoice.Id, paymentDate, 80_000m)
        ];
        var mirroredTransaction = CreatePaymentTransaction(
            paymentId,
            customerId,
            invoice.Id,
            paymentDate,
            80_000m);

        var result = BuildResult([invoice], [mirroredTransaction]);

        var row = Assert.Single(result.PaymentRows);
        Assert.Equal("지급(매입전표)", row.Division);
        Assert.Equal(0m, row.ReceiptAmount);
        Assert.Equal(80_000m, row.PaymentAmount);
        Assert.Equal(0m, result.Totals.ReceiptAmount);
        Assert.Equal(80_000m, result.Totals.PaymentAmount);
        Assert.Equal(paymentId, row.PaymentId);
        Assert.Equal(paymentId, row.TransactionId);
    }

    private static PeriodLedgerBuildResult BuildResult(
        IReadOnlyList<LocalInvoice> invoices,
        IReadOnlyList<LocalTransaction> transactions)
    {
        var method = typeof(PeriodLedgerAggregationService).GetMethod(
            "BuildPaymentLedgerResult",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var service = new PeriodLedgerAggregationService(null!);
        var customerNames = invoices
            .Select(invoice => invoice.CustomerId)
            .Concat(transactions.Select(transaction => transaction.CustomerId))
            .Distinct()
            .ToDictionary(customerId => customerId, _ => "테스트 거래처");
        var query = new PeriodLedgerQuery
        {
            From = new DateOnly(2026, 7, 1),
            To = new DateOnly(2026, 7, 31),
            LedgerType = PeriodLedgerType.ReceiptPayment,
            Scope = PeriodLedgerScope.AllCustomers
        };

        return Assert.IsType<PeriodLedgerBuildResult>(method!.Invoke(
            service,
            [
                query,
                invoices,
                transactions,
                customerNames,
                Array.Empty<PeriodLedgerMonthlySalesChartPoint>()
            ]));
    }

    private static LocalInvoice CreateInvoice(Guid customerId, VoucherType voucherType)
        => new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            VoucherType = voucherType,
            InvoiceDate = new DateOnly(2026, 7, 23),
            TotalAmount = 500_000m,
            SupplyAmount = 500_000m,
            VatAmount = 0m,
            IsLatestVersion = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalPayment CreatePayment(
        Guid id,
        Guid invoiceId,
        DateOnly paymentDate,
        decimal amount)
        => new()
        {
            Id = id,
            InvoiceId = invoiceId,
            PaymentDate = paymentDate,
            Amount = amount,
            Note = "통장",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalTransaction CreateReceiptTransaction(
        Guid id,
        Guid customerId,
        Guid? linkedInvoiceId,
        DateOnly transactionDate,
        decimal amount)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            LinkedInvoiceId = linkedInvoiceId,
            TransactionDate = transactionDate,
            BankReceipt = amount,
            ReceiptTotal = amount,
            SettlementAmount = amount,
            Note = "통장",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalTransaction CreatePaymentTransaction(
        Guid id,
        Guid customerId,
        Guid linkedInvoiceId,
        DateOnly transactionDate,
        decimal amount)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            LinkedInvoiceId = linkedInvoiceId,
            TransactionDate = transactionDate,
            BankPayment = amount,
            PaymentTotal = amount,
            SettlementAmount = amount,
            Note = "통장",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
}
