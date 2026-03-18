namespace 거래플랜.Desktop.App.Configuration;

public sealed class ApiOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8080";
}

public sealed class SyncOptions
{
    public int RetryMinutes { get; set; } = 3;
}
