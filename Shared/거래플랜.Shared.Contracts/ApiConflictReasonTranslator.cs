namespace 거래플랜.Shared.Contracts;

public static class ApiConflictReasonTranslator
{
    public const string ProtectedInvoiceSameIdStructuralMutation =
        "이미 수금/지급, 거래 연결, 렌탈 청구 또는 버전 이력이 있는 전표는 같은 전표 ID로 품목·수량·금액을 바꿀 수 없습니다. 최신 내용을 다시 불러온 뒤 필요한 경우 새 버전으로 저장하세요.";

    private const string ProtectedInvoiceSameIdStructuralMutationEnglish =
        "A paid, rental-linked, or versioned invoice cannot be structurally changed with the same invoice id. Save it as a new invoice version.";

    public static string ToUserMessage(string? reason)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return string.Empty;

        if (IsProtectedInvoiceSameIdStructuralMutation(trimmed))
            return ProtectedInvoiceSameIdStructuralMutation;

        if (trimmed.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return "다른 PC 또는 사용자가 먼저 저장했습니다. 최신 데이터를 다시 불러온 뒤 다시 시도하세요.";

        if (string.Equals(trimmed, "Server version is newer.", StringComparison.OrdinalIgnoreCase))
            return "서버에 더 최신 데이터가 있습니다. 최신 데이터를 다시 불러온 뒤 다시 시도하세요.";

        return trimmed;
    }

    private static bool IsProtectedInvoiceSameIdStructuralMutation(string reason)
        => string.Equals(reason, ProtectedInvoiceSameIdStructuralMutation, StringComparison.Ordinal) ||
           string.Equals(reason, ProtectedInvoiceSameIdStructuralMutationEnglish, StringComparison.OrdinalIgnoreCase) ||
           reason.Contains("same invoice id", StringComparison.OrdinalIgnoreCase) &&
           (reason.Contains("paid", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("rental-linked", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("versioned", StringComparison.OrdinalIgnoreCase));
}
