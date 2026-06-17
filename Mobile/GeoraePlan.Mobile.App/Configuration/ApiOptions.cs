namespace GeoraePlan.Mobile.App.Configuration;

public sealed class ApiOptions
{
#if DEBUG || GEORAEPLAN_MOBILE_LOCAL_TEST
    public const string DefaultBaseUrl = "http://10.0.2.2:19080";
#else
    public const string DefaultBaseUrl = "https://trade.2884.kr";
#endif
}
