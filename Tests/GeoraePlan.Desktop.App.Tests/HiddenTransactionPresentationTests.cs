using System.Reflection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class HiddenTransactionPresentationTests
{
    [Theory]
    [InlineData("일반수금")]
    [InlineData("일반지급")]
    [InlineData("선수금환불")]
    [InlineData("unknown")]
    public void HiddenTransactionRowDoesNotExposeStaleAmountsOrChannel(string kind)
    {
        var transaction = new LocalTransaction { Id = Guid.NewGuid(), TransactionKind = kind, AmountsHidden = true,
            CashReceipt = 1234, ReceiptTotal = 1234, CashPayment = 5678, PaymentTotal = 5678, IsDirty = true, Revision = 17 };
        var row = InvoiceListRow.From(transaction, "시험 거래처", true);
        Assert.True(row.AmountsHidden); Assert.Null(row.ReceiptAmount); Assert.Null(row.PaymentAmount);
        Assert.Null(row.TotalAmount); Assert.Null(row.SupplyAmount); Assert.Null(row.VatAmount); Assert.Null(row.BalanceAmount);
        Assert.Equal("비공개", row.ReceiptAmountDisplay); Assert.Equal("비공개", row.PaymentAmountDisplay);
        Assert.Equal("수금/지급", row.VoucherTypeDisplayOverride); Assert.False(row.IsBalanceCleared);
        Assert.Equal(transaction.Id, row.TransactionId); Assert.Equal("시험 거래처", row.CustomerName);
        Assert.True(transaction.IsDirty); Assert.Equal(17, transaction.Revision); Assert.Equal(1234, transaction.CashReceipt);
        var filter = typeof(MainViewModel).GetMethod("GetStandaloneTransactionLedgerAmount", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Null(filter.Invoke(null, [transaction]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1234)]
    public void DisclosedTransactionKeepsRealZeroAndChannel(decimal amount)
    {
        var transaction = new LocalTransaction { TransactionKind = "일반수금", CashReceipt = amount, ReceiptTotal = amount };
        var row = InvoiceListRow.From(transaction, "시험", true);
        Assert.False(row.AmountsHidden); Assert.Equal(amount, row.ReceiptAmount); Assert.Equal(amount, row.BalanceAmount);
        Assert.Equal(amount == 0 ? "수금" : "현금수금", row.VoucherTypeDisplayOverride);
        var filter = typeof(MainViewModel).GetMethod("GetStandaloneTransactionLedgerAmount", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(amount, filter.Invoke(null, [transaction]));
    }
}
