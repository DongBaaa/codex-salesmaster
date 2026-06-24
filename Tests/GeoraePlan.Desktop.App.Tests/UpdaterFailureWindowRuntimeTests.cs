using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class UpdaterFailureWindowRuntimeTests
{
    [Fact]
    public void UpdaterFailureWindow_CopiesFailureLogToClipboardOnStaThread()
    {
        if (!OperatingSystem.IsWindows())
            return;

        RunOnStaThread(() =>
        {
            var logMarker = "runtime-log-marker-" + Guid.NewGuid().ToString("N");
            var logPath = Path.Combine(
                Path.GetTempPath(),
                "georaeplan-updater-failure-window-" + Guid.NewGuid().ToString("N") + ".log");
            File.WriteAllText(logPath, $"INSTALL-ERR {logMarker}", Encoding.UTF8);

            object? window = null;
            try
            {
                var windowType = LoadUpdaterWindowType();
                window = Activator.CreateInstance(windowType, nonPublic: true);
                Assert.NotNull(window);

                InvokeInstanceMethod(
                    window!,
                    "ShowFailure",
                    "업데이트 실패",
                    "테스트 실패 내용",
                    logPath);

                Assert.Equal("Visible", GetPrivateFieldProperty(window!, "_buttonPanel", "Visibility")?.ToString());
                Assert.Equal("로그 복사", GetPrivateFieldProperty(window!, "_copyLogButton", "Content")?.ToString());
                Assert.Equal(true, GetPrivateFieldProperty(window!, "_openLogFolderButton", "IsEnabled"));

                InvokeInstanceMethod(window!, "CopyFailureLogToClipboard");

                Assert.Equal("복사 완료", GetPrivateFieldProperty(window!, "_copyLogButton", "Content")?.ToString());
                var clipboardText = GetClipboardText();
                Assert.Contains("거래플랜 업데이트 실패", clipboardText, StringComparison.Ordinal);
                Assert.Contains("테스트 실패 내용", clipboardText, StringComparison.Ordinal);
                Assert.Contains("--- update.log ---", clipboardText, StringComparison.Ordinal);
                Assert.Contains(logMarker, clipboardText, StringComparison.Ordinal);
            }
            finally
            {
                if (window is not null)
                    TryCloseWindow(window);
                File.Delete(logPath);
            }
        });
    }

    [Fact]
    public void UpdaterFailureWindow_DisablesOpenFolderWhenLogIsMissing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        RunOnStaThread(() =>
        {
            object? window = null;
            try
            {
                var missingLogPath = Path.Combine(
                    Path.GetTempPath(),
                    "georaeplan-updater-missing-log-" + Guid.NewGuid().ToString("N") + ".log");
                var windowType = LoadUpdaterWindowType();
                window = Activator.CreateInstance(windowType, nonPublic: true);
                Assert.NotNull(window);

                InvokeInstanceMethod(
                    window!,
                    "ShowFailure",
                    "업데이트 실패",
                    "로그 파일 없는 실패",
                    missingLogPath);

                Assert.Equal("오류 내용 복사", GetPrivateFieldProperty(window!, "_copyLogButton", "Content")?.ToString());
                Assert.Equal(false, GetPrivateFieldProperty(window!, "_openLogFolderButton", "IsEnabled"));

                InvokeInstanceMethod(window!, "CopyFailureLogToClipboard");

                var clipboardText = GetClipboardText();
                Assert.Contains("로그 파일 없는 실패", clipboardText, StringComparison.Ordinal);
                Assert.DoesNotContain("--- update.log ---", clipboardText, StringComparison.Ordinal);
            }
            finally
            {
                if (window is not null)
                    TryCloseWindow(window);
            }
        });
    }

    private static Type LoadUpdaterWindowType()
    {
        var assemblyPath = ResolveUpdaterAssemblyPath();
        var assembly = Assembly.LoadFrom(assemblyPath);
        return assembly.GetType("거래플랜.Updater.UpdateProgressWindow", throwOnError: true)!;
    }

    private static string ResolveUpdaterAssemblyPath()
    {
        var root = FindRepositoryRoot();
        var candidates = new[]
        {
            Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "bin", "Debug", "net8.0-windows", "Updater", "거래플랜.Updater.dll"),
            Path.Combine(root, "Updater", "거래플랜.Updater", "bin", "Debug", "net8.0-windows", "거래플랜.Updater.dll"),
            Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "bin", "Release", "net8.0-windows", "Updater", "거래플랜.Updater.dll"),
            Path.Combine(root, "Updater", "거래플랜.Updater", "bin", "Release", "net8.0-windows", "거래플랜.Updater.dll")
        };

        var match = candidates.FirstOrDefault(File.Exists);
        Assert.False(string.IsNullOrWhiteSpace(match), "Updater assembly was not found. Build the desktop test project first.");
        return match!;
    }

    private static string GetClipboardText()
    {
        var clipboardType = Type.GetType("System.Windows.Clipboard, PresentationCore", throwOnError: true)!;
        return (string?)clipboardType
            .GetMethod("GetText", Type.EmptyTypes)!
            .Invoke(null, Array.Empty<object>()) ?? string.Empty;
    }

    private static void InvokeInstanceMethod(object instance, string name, params object?[] args)
    {
        var method = instance.GetType().GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(instance, args);
    }

    private static object? GetPrivateFieldProperty(object instance, string fieldName, string propertyName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var fieldValue = field!.GetValue(instance);
        Assert.NotNull(fieldValue);
        var property = fieldValue!.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return property!.GetValue(fieldValue);
    }

    private static void TryCloseWindow(object window)
    {
        try
        {
            InvokeInstanceMethod(window, "Close");
        }
        catch
        {
            // best-effort cleanup only
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? captured = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completed = true;
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA WPF test timed out.");
        if (captured is not null)
            ExceptionDispatchInfo.Capture(captured).Throw();
        Assert.True(completed);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Updater")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
