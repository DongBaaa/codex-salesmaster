using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;

namespace 거래플랜.Updater;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = UpdateArguments.Parse(args);
            await ExecuteAsync(options);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"업데이트를 완료하지 못했습니다.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "거래플랜 업데이터",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
    }

    private static async Task ExecuteAsync(UpdateArguments options)
    {
        var workRoot = Path.Combine(Path.GetTempPath(), "GeoraePlan", "updates", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(workRoot);

        var packagePath = Path.Combine(workRoot, string.IsNullOrWhiteSpace(options.FileName)
            ? $"desktop-{options.Version}.zip"
            : options.FileName);
        await DownloadAsync(options.PackageUrl, packagePath);
        await VerifySha256Async(packagePath, options.Sha256);
        await WaitForProcessExitAsync(options.ProcessId);

        var extractRoot = Path.Combine(workRoot, "package");
        ZipFile.ExtractToDirectory(packagePath, extractRoot, overwriteFiles: true);
        var installScriptPath = Path.Combine(extractRoot, "Install-GeoraePlan.ps1");
        if (!File.Exists(installScriptPath))
            throw new FileNotFoundException("설치 스크립트를 찾지 못했습니다.", installScriptPath);

        var installProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{installScriptPath}\" -InstallRoot \"{options.InstallRoot}\" -NoLaunch",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = extractRoot
        });

        if (installProcess is null)
            throw new InvalidOperationException("업데이트 설치 프로세스를 시작하지 못했습니다.");

        await installProcess.WaitForExitAsync();
        if (installProcess.ExitCode != 0)
            throw new InvalidOperationException($"업데이트 설치가 실패했습니다. exitCode={installProcess.ExitCode}");

        if (!string.IsNullOrWhiteSpace(options.LaunchExe) && File.Exists(options.LaunchExe))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = options.LaunchExe,
                WorkingDirectory = Path.GetDirectoryName(options.LaunchExe) ?? options.InstallRoot,
                UseShellExecute = true
            });
        }
    }

    private static async Task DownloadAsync(string packageUrl, string targetPath)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync();
        await using var destination = File.Create(targetPath);
        await source.CopyToAsync(destination);
        await destination.FlushAsync();
    }

    private static async Task VerifySha256Async(string filePath, string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
            return;

        await using var stream = File.OpenRead(filePath);
        using var algorithm = SHA256.Create();
        var hash = await algorithm.ComputeHashAsync(stream);
        var actual = Convert.ToHexString(hash);
        if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("다운로드한 업데이트 패키지의 SHA256 검증에 실패했습니다.");
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        if (processId <= 0)
            return;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited)
                return;

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException)
        {
            // already exited
        }
    }
}

internal sealed class UpdateArguments
{
    public string PackageUrl { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string InstallRoot { get; init; } = string.Empty;
    public string LaunchExe { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public int ProcessId { get; init; }

    public static UpdateArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
                continue;

            var value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            values[key] = value;
            i++;
        }

        var packageUrl = Require(values, "--package-url");
        var installRoot = Require(values, "--install-root");
        var launchExe = Require(values, "--launch-exe");

        return new UpdateArguments
        {
            PackageUrl = packageUrl,
            Sha256 = Require(values, "--sha256"),
            InstallRoot = installRoot,
            LaunchExe = launchExe,
            Version = values.GetValueOrDefault("--version", string.Empty),
            FileName = values.GetValueOrDefault("--file-name", Path.GetFileName(new Uri(packageUrl).AbsolutePath)),
            ProcessId = int.TryParse(values.GetValueOrDefault("--process-id", "0"), out var pid) ? pid : 0
        };
    }

    private static string Require(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"필수 인자가 없습니다: {key}");

        return value.Trim();
    }
}
