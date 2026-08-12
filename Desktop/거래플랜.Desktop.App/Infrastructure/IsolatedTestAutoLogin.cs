using System.IO;
using System.Text.Json;

namespace 거래플랜.Desktop.App.Infrastructure;

internal sealed class IsolatedTestAutoLoginRequest
{
    internal IsolatedTestAutoLoginRequest(
        string username,
        string password)
    {
        Username = username;
        Password = password;
    }

    internal string Username { get; }
    internal string Password { get; }

    public override string ToString()
        => nameof(IsolatedTestAutoLoginRequest) + " [redacted]";
}

internal sealed record IsolatedTestAutoLoginTakeResult(
    bool Requested,
    IsolatedTestAutoLoginRequest? Request,
    string FailureReason);

internal static class IsolatedTestAutoLogin
{
    internal const string EnabledEnvironmentKey =
        "GEORAEPLAN_TEST_AUTO_LOGIN";
    internal const string UsernameEnvironmentKey =
        "GEORAEPLAN_TEST_AUTO_LOGIN_USERNAME";
    internal const string PasswordEnvironmentKey =
        "GEORAEPLAN_TEST_AUTO_LOGIN_PASSWORD";

    private const string TestModeEnvironmentKey =
        "GEORAEPLAN_TEST_MODE";
    private const string AppRootEnvironmentKey =
        "GEORAEPLAN_APP_ROOT";
    private const string ReadyMarkerFileName =
        ".georaeplan-runtime-ready";
    private const string InvalidMarkerFileName =
        ".georaeplan-runtime-invalid";
    private const string AppSettingsFileName =
        "appsettings.json";

    internal static IsolatedTestAutoLoginTakeResult
        TakeFromCurrentProcess()
    {
        try
        {
            var enabled = Environment.GetEnvironmentVariable(
                EnabledEnvironmentKey,
                EnvironmentVariableTarget.Process);
            var username = Environment.GetEnvironmentVariable(
                UsernameEnvironmentKey,
                EnvironmentVariableTarget.Process);
            var password = Environment.GetEnvironmentVariable(
                PasswordEnvironmentKey,
                EnvironmentVariableTarget.Process);
            var appRoot = Environment.GetEnvironmentVariable(
                AppRootEnvironmentKey,
                EnvironmentVariableTarget.Process);
            var testMode = Environment.GetEnvironmentVariable(
                TestModeEnvironmentKey,
                EnvironmentVariableTarget.Process);

            return Evaluate(
                enabled,
                username,
                password,
                AppContext.BaseDirectory,
                appRoot,
                AppRuntimeInfo.IsTestRuntime &&
                IsTruthy(testMode));
        }
        finally
        {
            ClearCredentialEnvironment();
        }
    }

    internal static IsolatedTestAutoLoginTakeResult Evaluate(
        string? enabled,
        string? username,
        string? password,
        string appBaseDirectory,
        string? appRoot,
        bool isTestRuntime)
    {
        if (!IsTruthy(enabled))
        {
            return new IsolatedTestAutoLoginTakeResult(
                Requested: false,
                Request: null,
                FailureReason: string.Empty);
        }

        if (!IsCertifiedRuntime(
                appBaseDirectory,
                appRoot,
                isTestRuntime))
        {
            return new IsolatedTestAutoLoginTakeResult(
                Requested: true,
                Request: null,
                FailureReason: "runtime-not-certified");
        }

        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrEmpty(password))
        {
            return new IsolatedTestAutoLoginTakeResult(
                Requested: true,
                Request: null,
                FailureReason: "credentials-missing");
        }

        return new IsolatedTestAutoLoginTakeResult(
            Requested: true,
            Request: new IsolatedTestAutoLoginRequest(
                username.Trim(),
                password),
            FailureReason: string.Empty);
    }

    internal static bool IsCertifiedRuntime(
        string appBaseDirectory,
        string? appRoot,
        bool isTestRuntime)
    {
        if (!isTestRuntime ||
            string.IsNullOrWhiteSpace(appBaseDirectory) ||
            string.IsNullOrWhiteSpace(appRoot))
        {
            return false;
        }

        try
        {
            var normalizedAppDirectory = NormalizeDirectory(
                appBaseDirectory);
            var appDirectory = new DirectoryInfo(
                normalizedAppDirectory);
            var runtimeDirectory = appDirectory.Parent;
            var testExecutionDirectory = runtimeDirectory?.Parent;
            if (runtimeDirectory is null ||
                testExecutionDirectory is null ||
                !string.Equals(
                    appDirectory.Name,
                    "App",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    runtimeDirectory.Name,
                    "실행환경",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    testExecutionDirectory.Name,
                    "테스트 시행",
                    StringComparison.Ordinal))
            {
                return false;
            }

            var expectedAppRoot = NormalizeDirectory(
                Path.Combine(
                    runtimeDirectory.FullName,
                    "AppData"));
            if (!string.Equals(
                    NormalizeDirectory(appRoot),
                    expectedAppRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var readyMarkerPath = Path.Combine(
                runtimeDirectory.FullName,
                ReadyMarkerFileName);
            var invalidMarkerPath = Path.Combine(
                runtimeDirectory.FullName,
                InvalidMarkerFileName);
            return File.Exists(readyMarkerPath) &&
                   !File.Exists(invalidMarkerPath) &&
                   HasLoopbackApiConfiguration(
                       appDirectory.FullName);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            IOException or
            JsonException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            return false;
        }
    }

    private static void ClearCredentialEnvironment()
    {
        Environment.SetEnvironmentVariable(
            EnabledEnvironmentKey,
            null,
            EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable(
            UsernameEnvironmentKey,
            null,
            EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable(
            PasswordEnvironmentKey,
            null,
            EnvironmentVariableTarget.Process);
    }

    private static string NormalizeDirectory(string path)
        => Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

    private static bool HasLoopbackApiConfiguration(
        string appDirectory)
    {
        var appSettingsPath = Path.Combine(
            appDirectory,
            AppSettingsFileName);
        using var appSettings = JsonDocument.Parse(
            File.ReadAllText(appSettingsPath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling =
                    JsonCommentHandling.Skip
            });
        if (!TryGetPropertyIgnoreCase(
                appSettings.RootElement,
                "Api",
                out var apiElement) ||
            apiElement.ValueKind !=
                JsonValueKind.Object ||
            !TryGetPropertyIgnoreCase(
                apiElement,
                "BaseUrl",
                out var baseUrlElement) ||
            baseUrlElement.ValueKind !=
                JsonValueKind.String)
        {
            return false;
        }

        var baseUrl = baseUrlElement.GetString();
        return Uri.TryCreate(
                   baseUrl,
                   UriKind.Absolute,
                   out var uri) &&
               uri.IsLoopback &&
               (uri.Scheme.Equals(
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)) &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment);
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind !=
            JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in
                 element.EnumerateObject())
        {
            if (property.Name.Equals(
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsTruthy(string? raw)
        => string.Equals(
               raw?.Trim(),
               "1",
               StringComparison.OrdinalIgnoreCase) ||
           string.Equals(
               raw?.Trim(),
               "true",
               StringComparison.OrdinalIgnoreCase) ||
           string.Equals(
               raw?.Trim(),
               "yes",
               StringComparison.OrdinalIgnoreCase) ||
           string.Equals(
               raw?.Trim(),
               "on",
               StringComparison.OrdinalIgnoreCase);
}
