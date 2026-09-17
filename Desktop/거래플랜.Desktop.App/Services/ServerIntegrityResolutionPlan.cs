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
        ImpactExplanation = DiagnosticUserMessageFormatter.BuildIntegrityImpact(issue.Severity);
        ActionSteps = DiagnosticUserMessageFormatter.BuildServerIntegrityActionSteps(
            SuggestedAction,
            CanOpenTarget,
            IsInformational);
    }

    public IntegrityIssueDto Issue { get; }
    public IntegrityIssueDetailRowDto Detail { get; }
    public DataIntegrityDirectActionKind DirectActionKind { get; }
    public Guid? TargetEntityId { get; }
    public string ActionButtonText { get; }
    public string OpenActionButtonText => $"{ActionButtonText} 열기";
    public string SuggestedAction { get; }
    public string ProblemExplanation { get; }
    public string ImpactExplanation { get; }
    public string ActionSteps { get; }
    public bool CanOpenTarget =>
        DirectActionKind != DataIntegrityDirectActionKind.None &&
        TargetEntityId.HasValue;
    public bool IsInformational =>
        string.Equals(Issue.Severity, "Info", StringComparison.OrdinalIgnoreCase);
    public string SeverityText => DataIntegritySeverityFormatter.ToDisplayText(Issue.Severity);
    public string ActionAvailabilityText => CanOpenTarget
        ? $"아래 '{OpenActionButtonText}'를 누르면 수정할 자료가 있는 기존 화면으로 이동합니다. 저장하고 편집창을 닫으면 이 문제를 다시 검사합니다."
        : IsInformational
            ? "이 항목은 현재 업무를 막는 오류가 아닙니다. 과거 표시 내용이 정상이라면 그대로 두세요."
            : "프로그램이 자동으로 수정 대상을 한 건으로 확정할 수 없습니다. 잘못된 삭제를 막기 위해 자동 수정하지 않으므로, '확인할 대상'과 '지금 할 일'을 보고 원본 자료를 확인하세요.";

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
