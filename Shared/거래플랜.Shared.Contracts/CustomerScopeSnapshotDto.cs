namespace 거래플랜.Shared.Contracts;

public sealed class CustomerScopeSnapshotDto
{
    public int Version { get; set; } = 1;
    public Guid UserId { get; set; }
    /// <summary>Authenticated tenant; the selected business DB is bound by the pull request.</summary>
    public string TenantCode { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = string.Empty;
    public string ScopeType { get; set; } = string.Empty;
    // Includes tombstones, so ordinary deletions remain handled by normal sync.
    // Missing is invalid; it must not be confused with an explicit empty scope.
    public List<Guid> VisibleCustomerIds { get; set; } = null!;
}
