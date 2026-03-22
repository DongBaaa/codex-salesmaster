namespace 거래플랜.Server.Api.Services;

public sealed class CentralFileStorageOptions
{
    public const string SectionName = "FileStorage";

    public string RootPath { get; set; } = string.Empty;
}
