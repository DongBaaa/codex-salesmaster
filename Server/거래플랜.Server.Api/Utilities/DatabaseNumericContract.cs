namespace 거래플랜.Server.Api.Utilities;

public static class DatabaseNumericContract
{
    public const int QuantityScale =
        거래플랜.Shared.Contracts.QuantityNumericContract.QuantityScale;
    public const decimal MaxQuantity18Scale2 =
        거래플랜.Shared.Contracts.QuantityNumericContract.MaxQuantity18Scale2;

    public static bool IsPositiveQuantity18Scale2(decimal value)
        => 거래플랜.Shared.Contracts.QuantityNumericContract
            .IsPositiveQuantity18Scale2(value);
}
