namespace 거래플랜.Shared.Contracts;

public static class QuantityNumericContract
{
    public const int QuantityScale = 2;
    public const decimal MaxQuantity18Scale2 = 9_999_999_999_999_999.99m;

    public static bool IsPositiveQuantity18Scale2(decimal value)
        => value > 0m && IsNonNegativeQuantity18Scale2(value);

    public static bool IsNonNegativeQuantity18Scale2(decimal value)
        => value >= 0m &&
           value <= MaxQuantity18Scale2 &&
           decimal.Round(value, QuantityScale, MidpointRounding.ToEven) == value;

    public static bool IsValidReceivedQuantity18Scale2(
        decimal receivedQuantity,
        decimal requestedQuantity)
        => IsPositiveQuantity18Scale2(requestedQuantity) &&
           IsNonNegativeQuantity18Scale2(receivedQuantity) &&
           receivedQuantity <= requestedQuantity;
}
