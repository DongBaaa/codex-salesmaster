// Platform UI/storage and attachment I/O only; HTTP, session and sync are production code.
namespace GeoraePlan.Mobile.App.Services
{
    public static class FileSystem
    {
        public static string AppDataDirectory { get; set; } = "";
        public static string CacheDirectory => AppDataDirectory;
    }
    public static class MobileAppLogger
    {
        public static void Warn(string category, string message) { }
        public static void Info(string category, string message) { }
        public static void Error(string category, string message, Exception error) { }
    }
    internal sealed record MobileClientRuntimeIdentity(string Version, int Build, int ProtocolVersion);
}
namespace GeoraePlan.Mobile.App
{
    public static class App { public static void ShowLogin() { } }
}
namespace Microsoft.Maui.ApplicationModel
{
    public static class MainThread { public static void BeginInvokeOnMainThread(Action action) => action(); }
    public sealed class AppInfo
    {
        public static AppInfo Current { get; } = new();
        public string PackageName => "kr.georaeplan.mobile";
        public string VersionString => "0.2.83";
        public string BuildString => "194";
    }
}
