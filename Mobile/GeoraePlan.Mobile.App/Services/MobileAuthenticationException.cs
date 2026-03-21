namespace GeoraePlan.Mobile.App.Services;

public sealed class MobileAuthenticationException : Exception
{
    public MobileAuthenticationException(string requestPath, string message)
        : base(message)
    {
        RequestPath = requestPath;
    }

    public string RequestPath { get; }
}
