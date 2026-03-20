namespace 거래플랜.Server.Api.Security;

public sealed class SeedUsersOptions
{
    public const string SectionName = "SeedUsers";
    public const string DefaultAdminPassword = "CHANGE_THIS_ADMIN_PASSWORD";
    public const string DefaultUserPassword = "CHANGE_THIS_USER_PASSWORD";
    public const string DefaultItwPassword = "CHANGE_THIS_ITW_PASSWORD";

    public bool EnableSeedUsers { get; set; } = true;
    public bool WarnOnDefaultPasswords { get; set; } = true;
    public bool UpdateExistingItwPassword { get; set; } = true;
    public string? AdminPassword { get; set; }
    public string? UserPassword { get; set; }
    public string? ItwPassword { get; set; }

    public bool UsesDefaultAdminPassword() => string.Equals(AdminPassword, DefaultAdminPassword, StringComparison.Ordinal);
    public bool UsesDefaultUserPassword() => string.Equals(UserPassword, DefaultUserPassword, StringComparison.Ordinal);
    public bool UsesDefaultItwPassword() => string.Equals(ItwPassword, DefaultItwPassword, StringComparison.Ordinal);
}
