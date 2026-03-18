namespace SalesMaster.Server.Api.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "SalesMaster";
    public string Audience { get; set; } = "SalesMasterDesktop";
    public string SigningKey { get; set; } = "ChangeThisSigningKeyForProduction_AtLeast32Chars";
    public int ExpirationMinutes { get; set; } = 480;
}
