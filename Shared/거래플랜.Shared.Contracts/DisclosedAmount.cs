namespace 거래플랜.Shared.Contracts;

/// <summary>Null is undisclosed money, never a zero-valued price or total.</summary>
public static class DisclosedAmount
{
    public static decimal Require(decimal? value)
        => value ?? throw new InvalidOperationException(
            "비공개 금액은 계산에 사용할 수 없습니다. 금액 공개 상태를 먼저 확인하세요.");
}
