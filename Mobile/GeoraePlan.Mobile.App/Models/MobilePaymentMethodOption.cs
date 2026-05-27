namespace GeoraePlan.Mobile.App.Models;

public sealed class MobilePaymentMethodOption
{
    public const string BucketCash = "cash";
    public const string BucketCard = "card";
    public const string BucketBank = "bank";

    public string DisplayName { get; init; } = string.Empty;
    public string TransactionKind { get; init; } = string.Empty;
    public string BucketKey { get; init; } = BucketBank;
    public bool IsPurchase { get; init; }

    public static IReadOnlyList<MobilePaymentMethodOption> CreateOptions(bool isPurchase)
    {
        var transactionKind = isPurchase ? "전표지급" : "전표수금";
        return isPurchase
            ? [
                new() { DisplayName = "현금지급", TransactionKind = transactionKind, BucketKey = BucketCash, IsPurchase = true },
                new() { DisplayName = "카드지급", TransactionKind = transactionKind, BucketKey = BucketCard, IsPurchase = true },
                new() { DisplayName = "통장지급", TransactionKind = transactionKind, BucketKey = BucketBank, IsPurchase = true }
            ]
            : [
                new() { DisplayName = "현금수금", TransactionKind = transactionKind, BucketKey = BucketCash },
                new() { DisplayName = "카드수금", TransactionKind = transactionKind, BucketKey = BucketCard },
                new() { DisplayName = "통장수금", TransactionKind = transactionKind, BucketKey = BucketBank }
            ];
    }

    public override string ToString() => DisplayName;
}
