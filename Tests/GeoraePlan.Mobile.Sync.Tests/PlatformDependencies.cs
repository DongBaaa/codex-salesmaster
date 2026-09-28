namespace GeoraePlan.Mobile.App.Services;

public static class FileSystem
{
    public static string AppDataDirectory { get; set; } = "";
}

public static class MobileAppLogger
{
    public static List<string> Messages { get; } = [];
    public static void Warn(string category, string message) => Messages.Add(message);
    public static void Info(string category, string message) => Messages.Add(message);
}
