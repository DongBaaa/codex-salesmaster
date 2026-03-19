namespace 거래플랜.Server.Api.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "거래플랜";
    public string Audience { get; set; } = "georaeplan-client";
    public string SigningKey { get; set; } = "ChangeThisSigningKeyForProduction_AtLeast32Chars";
    public int ExpirationMinutes { get; set; } = 480;
}
