using System.IO;
using System.Text;

namespace SalesMaster.Desktop.App.Services;

public static class AppLogger
{
    private static readonly object SyncLock = new();

    public static void Info(string category, string message) => Write("INFO", category, message);

    public static void Warn(string category, string message) => Write("WARN", category, message);

    public static void Error(string category, string message, Exception? ex = null)
    {
        var details = ex is null
            ? message
            : $"{message}{Environment.NewLine}{ex}";
        Write("ERROR", category, details);
    }

    private static void Write(string level, string category, string message)
    {
        try
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SalesMaster",
                "logs");
            Directory.CreateDirectory(baseDir);

            var filePath = Path.Combine(baseDir, $"{DateTime.Now:yyyyMMdd}.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{category}] {message}{Environment.NewLine}";

            lock (SyncLock)
            {
                File.AppendAllText(filePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
