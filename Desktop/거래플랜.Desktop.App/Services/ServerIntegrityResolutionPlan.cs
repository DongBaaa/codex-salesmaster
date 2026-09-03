using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed class ServerIntegrityResolutionPlan
{
    private ServerIntegrityResolutionPlan(
        IntegrityIssueDto issue,
        IntegrityIssueDetailRowDto detail,
        DataIntegrityDirectActionKind directActionKind,
        Guid? targetEntityId,
        string actionButtonText)
    {
        Issue = issue;
        Detail = detail;
        DirectActionKind = directActionKind;
        TargetEntityId = targetEntityId;
        ActionButtonText = actionButtonText;
        SuggestedAction = IntegrityIssueGuidance.GetSuggestedAction(
            issue.Code,
            issue.Message);
        ProblemExplanation = DiagnosticUserMessageFormatter.DescribeServerIntegrityIssue(
            issue.Code,
            issue.Message);
    }

    public IntegrityIssueDto Issue { get; }
    public IntegrityIssueDetailRowDto Detail { get; }
    public DataIntegrityDirectActionKind DirectActionKind { get; }
    public Guid? TargetEntityId { get; }
    public string ActionButtonText { get; }
    public string SuggestedAction { get; }
    public string ProblemExplanation { get; }
    public bool CanOpenTarget =>
        DirectActionKind != DataIntegrityDirectActionKind.None &&
        TargetEntityId.HasValue;
    public bool IsInformational =>
        string.Equals(Issue.Severity, "Info", StringComparison.OrdinalIgnoreCase);
    public string SeverityText => IsInformational ? "참고" : Issue.Severity;
    public string ActionAvailabilityText => CanOpenTarget
        ? $"{ActionButtonText}을 열어 수정한 뒤 창을 닫으면 서버 무결성 목록을 자동으로 다시 확인합니다."
        : IsInformational
            ? "이 항목은 참고 정보입니다. 상세 내용을 확인한 뒤 실제 정리가 필요할 때만 관련 원본 화면에서 수동 점검하세요."
            : "이 항목은 상세 행 ID만으로 안전한 수정 대상을 확정할 수 없습니다. 아래 해결 방법에 따라 원본 자료를 확인하세요.";

    public static ServerIntegrityResolutionPlan Create(
        IntegrityIssueDto issue,
        IntegrityIssueDetailRowDto detail)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(detail);

        var entityType = NormalizeEntityType(detail.EntityType);
        var hasTargetId = Guid.TryParse(detail.EntityIdText, out var targetId);
        var (actionKind, buttonText) = entityType switch
        {
            "렌탈청구프로필" or "렌탈청구품목" or "렌탈청구run" =>
                (DataIntegrityDirectActionKind.OpenRentalBillingProfile, "렌탈 청구관리"),
            "렌탈자산" =>
                (DataIntegrityDirectActionKind.OpenRentalAsset, "렌탈 자산/설치현황"),
            "품목" =>
                (DataIntegrityDirectActionKind.OpenInventoryItem, "품목/재고 관리"),
            "거래처" =>
                (DataIntegrityDirectActionKind.OpenCustomer, "거래처 편집"),
            "전표" =>
                (DataIntegrityDirectActionKind.OpenInvoice, "전표 편집"),
            _ => (DataIntegrityDirectActionKind.None, "원본 화면")
        };

        var canOpenTarget = hasTargetId && actionKind != DataIntegrityDirectActionKind.None;
        return new ServerIntegrityResolutionPlan(
            issue,
            detail,
            canOpenTarget ? actionKind : DataIntegrityDirectActionKind.None,
            canOpenTarget ? targetId : null,
            buttonText);
    }

    private static string NormalizeEntityType(string? value)
        => string.Concat((value ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character)));
}
