using 거래플랜.Desktop.App.Infrastructure;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class IsolatedTestAutoLoginSafetyTests
{
    [Fact]
    public void Evaluate_CertifiedIsolatedRuntime_ReturnsRedactedRequest()
    {
        using var fixture = new CertifiedRuntimeFixture();
        var password = Guid.NewGuid().ToString("N");

        var result = IsolatedTestAutoLogin.Evaluate(
            enabled: "1",
            username: "admin",
            password: password,
            appBaseDirectory: fixture.AppDirectory,
            appRoot: fixture.AppDataDirectory,
            isTestRuntime: true);

        Assert.True(result.Requested);
        var request = Assert.IsType<IsolatedTestAutoLoginRequest>(
            result.Request);
        Assert.Equal("admin", request.Username);
        Assert.Equal(password, request.Password);
        Assert.DoesNotContain(
            password,
            request.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "redacted",
            request.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, result.FailureReason);
    }

    [Theory]
    [InlineData(false, true, false, "runtime-not-certified")]
    [InlineData(true, false, false, "runtime-not-certified")]
    [InlineData(true, true, true, "runtime-not-certified")]
    public void Evaluate_UncertifiedRuntime_RejectsWithoutReturningCredentials(
        bool isTestRuntime,
        bool useExpectedAppRoot,
        bool createInvalidMarker,
        string expectedReason)
    {
        using var fixture = new CertifiedRuntimeFixture();
        if (createInvalidMarker)
            File.WriteAllText(fixture.InvalidMarkerPath, "invalid");
        var appRoot = useExpectedAppRoot
            ? fixture.AppDataDirectory
            : Path.Combine(fixture.RuntimeDirectory, "OtherData");

        var result = IsolatedTestAutoLogin.Evaluate(
            enabled: "true",
            username: "admin",
            password: Guid.NewGuid().ToString("N"),
            appBaseDirectory: fixture.AppDirectory,
            appRoot: appRoot,
            isTestRuntime: isTestRuntime);

        Assert.True(result.Requested);
        Assert.Null(result.Request);
        Assert.Equal(expectedReason, result.FailureReason);
    }

    [Fact]
    public void Evaluate_MissingCredentials_FailsClosed()
    {
        using var fixture = new CertifiedRuntimeFixture();

        var result = IsolatedTestAutoLogin.Evaluate(
            enabled: "yes",
            username: "admin",
            password: string.Empty,
            appBaseDirectory: fixture.AppDirectory,
            appRoot: fixture.AppDataDirectory,
            isTestRuntime: true);

        Assert.True(result.Requested);
        Assert.Null(result.Request);
        Assert.Equal(
            "credentials-missing",
            result.FailureReason);
    }

    [Fact]
    public void Evaluate_RemoteApiBaseUrl_FailsClosed()
    {
        using var fixture = new CertifiedRuntimeFixture();
        fixture.SetApiBaseUrl(
            "https://trade.example.invalid");

        var result = IsolatedTestAutoLogin.Evaluate(
            enabled: "1",
            username: "admin",
            password: Guid.NewGuid().ToString("N"),
            appBaseDirectory: fixture.AppDirectory,
            appRoot: fixture.AppDataDirectory,
            isTestRuntime: true);

        Assert.True(result.Requested);
        Assert.Null(result.Request);
        Assert.Equal(
            "runtime-not-certified",
            result.FailureReason);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"not-an-object\"")]
    [InlineData("null")]
    public void Evaluate_NonObjectAppSettingsRoot_FailsClosed(
        string appSettingsJson)
    {
        using var fixture = new CertifiedRuntimeFixture();
        fixture.SetRawAppSettings(
            appSettingsJson);

        var exception = Record.Exception(
            () =>
            {
                var result =
                    IsolatedTestAutoLogin.Evaluate(
                        enabled: "1",
                        username: "admin",
                        password:
                            Guid.NewGuid().ToString("N"),
                        appBaseDirectory:
                            fixture.AppDirectory,
                        appRoot:
                            fixture.AppDataDirectory,
                        isTestRuntime: true);

                Assert.True(result.Requested);
                Assert.Null(result.Request);
                Assert.Equal(
                    "runtime-not-certified",
                    result.FailureReason);
            });

        Assert.Null(exception);
    }

    [Fact]
    public void Evaluate_DisabledRequest_IgnoresSuppliedValues()
    {
        var result = IsolatedTestAutoLogin.Evaluate(
            enabled: "0",
            username: "admin",
            password: Guid.NewGuid().ToString("N"),
            appBaseDirectory: "not-a-runtime",
            appRoot: "not-app-data",
            isTestRuntime: false);

        Assert.False(result.Requested);
        Assert.Null(result.Request);
        Assert.Equal(string.Empty, result.FailureReason);
    }

    [Fact]
    public void TakeFromCurrentProcess_AlwaysClearsAutoLoginEnvironment()
    {
        var keys = new[]
        {
            IsolatedTestAutoLogin.EnabledEnvironmentKey,
            IsolatedTestAutoLogin.UsernameEnvironmentKey,
            IsolatedTestAutoLogin.PasswordEnvironmentKey
        };
        var previousValues = keys.ToDictionary(
            static key => key,
            static key => Environment.GetEnvironmentVariable(
                key,
                EnvironmentVariableTarget.Process),
            StringComparer.Ordinal);

        try
        {
            Environment.SetEnvironmentVariable(
                IsolatedTestAutoLogin.EnabledEnvironmentKey,
                "1",
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(
                IsolatedTestAutoLogin.UsernameEnvironmentKey,
                "admin",
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(
                IsolatedTestAutoLogin.PasswordEnvironmentKey,
                Guid.NewGuid().ToString("N"),
                EnvironmentVariableTarget.Process);

            _ = IsolatedTestAutoLogin.TakeFromCurrentProcess();

            foreach (var key in keys)
            {
                Assert.Null(
                    Environment.GetEnvironmentVariable(
                        key,
                        EnvironmentVariableTarget.Process));
            }
        }
        finally
        {
            foreach (var key in keys)
            {
                Environment.SetEnvironmentVariable(
                    key,
                    previousValues[key],
                    EnvironmentVariableTarget.Process);
            }
        }
    }

    [Fact]
    public void AppStartup_ConsumesAutoLoginBeforeOtherStartupWork()
    {
        var appSource = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "Desktop",
                "거래플랜.Desktop.App",
                "App.xaml.cs"));

        var startupIndex = appSource.IndexOf(
            "protected override async void OnStartup",
            StringComparison.Ordinal);
        var takeIndex = appSource.IndexOf(
            "IsolatedTestAutoLogin.TakeFromCurrentProcess();",
            startupIndex,
            StringComparison.Ordinal);
        var rejectedRequestIndex = appSource.IndexOf(
            "testAutoLogin.Request is null",
            takeIndex,
            StringComparison.Ordinal);
        var rejectedShutdownIndex = appSource.IndexOf(
            "Shutdown(1);",
            rejectedRequestIndex,
            StringComparison.Ordinal);
        var renderModeIndex = appSource.IndexOf(
            "DesktopRenderModePolicy.ApplyForCurrentRuntime();",
            startupIndex,
            StringComparison.Ordinal);
        var baseStartupIndex = appSource.IndexOf(
            "base.OnStartup(e);",
            startupIndex,
            StringComparison.Ordinal);
        var installGateIndex = appSource.IndexOf(
            "InstallRootUpdateGate.TryAcquire(",
            startupIndex,
            StringComparison.Ordinal);

        Assert.True(startupIndex >= 0);
        Assert.True(takeIndex > startupIndex);
        Assert.True(rejectedRequestIndex > takeIndex);
        Assert.True(
            rejectedShutdownIndex >
            rejectedRequestIndex);
        Assert.True(
            renderModeIndex >
            rejectedShutdownIndex);
        Assert.True(renderModeIndex > takeIndex);
        Assert.True(baseStartupIndex > takeIndex);
        Assert.True(installGateIndex > takeIndex);
        Assert.Equal(
            takeIndex,
            appSource.LastIndexOf(
                "IsolatedTestAutoLogin.TakeFromCurrentProcess();",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AppStartup_RequestedAutoLoginFailsFastWithoutManualFallback()
    {
        var appSource = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "Desktop",
                "거래플랜.Desktop.App",
                "App.xaml.cs"));
        var autoLoginBranchStart = appSource.IndexOf(
            "if (testAutoLogin.Requested)",
            StringComparison.Ordinal);
        var manualLoginBranchStart = appSource.IndexOf(
            "var loginWin = new LoginWindow(loginVm);",
            autoLoginBranchStart,
            StringComparison.Ordinal);
        var startupCatchStart = appSource.IndexOf(
            "AppLogger.Error(\"APP\", \"Startup failure\", ex);",
            manualLoginBranchStart,
            StringComparison.Ordinal);
        var shutdownIndex = appSource.IndexOf(
            "Shutdown(1);",
            startupCatchStart,
            StringComparison.Ordinal);

        Assert.True(autoLoginBranchStart >= 0);
        Assert.True(manualLoginBranchStart > autoLoginBranchStart);
        Assert.True(startupCatchStart > manualLoginBranchStart);
        Assert.True(shutdownIndex > startupCatchStart);

        var requestedBranch = appSource[
            autoLoginBranchStart..manualLoginBranchStart];
        Assert.Contains(
            "throw new InvalidOperationException(",
            requestedBranch,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new LoginWindow(",
            requestedBranch,
            StringComparison.Ordinal);

        var unattendedFailureBlock = appSource[
            startupCatchStart..shutdownIndex];
        Assert.Contains(
            "if (!testAutoLogin.Requested)",
            unattendedFailureBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "MessageBox.Show(",
            unattendedFailureBlock,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(
            AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "거래플랜.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "거래플랜.sln을 찾을 수 없습니다.");
    }

    private sealed class CertifiedRuntimeFixture : IDisposable
    {
        private readonly string _root;

        internal CertifiedRuntimeFixture()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "georaeplan-auto-login-tests",
                Guid.NewGuid().ToString("N"),
                "테스트 시행");
            RuntimeDirectory = Path.Combine(
                _root,
                "실행환경");
            AppDirectory = Path.Combine(
                RuntimeDirectory,
                "App");
            AppDataDirectory = Path.Combine(
                RuntimeDirectory,
                "AppData");
            Directory.CreateDirectory(AppDirectory);
            Directory.CreateDirectory(AppDataDirectory);
            File.WriteAllText(
                Path.Combine(
                    RuntimeDirectory,
                    ".georaeplan-runtime-ready"),
                "ready");
            SetApiBaseUrl(
                "http://127.0.0.1:18888");
        }

        internal string RuntimeDirectory { get; }
        internal string AppDirectory { get; }
        internal string AppDataDirectory { get; }
        internal string InvalidMarkerPath => Path.Combine(
            RuntimeDirectory,
            ".georaeplan-runtime-invalid");

        internal void SetApiBaseUrl(string baseUrl)
        {
            SetRawAppSettings(
                $$"""
                  {
                    "Api": {
                      "BaseUrl": "{{baseUrl}}"
                    }
                  }
                  """);
        }

        internal void SetRawAppSettings(
            string contents)
        {
            File.WriteAllText(
                Path.Combine(
                    AppDirectory,
                    "appsettings.json"),
                contents);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Cleanup must not hide a safety assertion.
            }
        }
    }
}
