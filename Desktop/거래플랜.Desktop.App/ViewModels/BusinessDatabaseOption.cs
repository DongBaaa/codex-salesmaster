namespace 거래플랜.Desktop.App.ViewModels;

public sealed class BusinessDatabaseOption
{
    public string DatabaseName { get; init; } = string.Empty;
    public string TenantCode { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    public override string ToString() => DisplayName;
}
