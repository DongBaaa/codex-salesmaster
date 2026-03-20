namespace 거래플랜.Server.Api.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public bool EnableRateLimiting { get; set; } = true;
    public int LoginPermitLimitPerMinute { get; set; } = 10;
    public int ApiPermitLimitPerMinute { get; set; } = 300;
    public bool AddSecurityHeaders { get; set; } = true;
    public bool RequireHttpsForwardedProto { get; set; } = false;
    public bool AllowAnyCorsOrigin { get; set; } = false;
    public List<string> AllowedCorsOrigins { get; set; } = [];
}
