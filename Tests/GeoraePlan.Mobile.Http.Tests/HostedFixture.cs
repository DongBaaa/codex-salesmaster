using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;
namespace GeoraePlan.Mobile.Http.Tests;
    internal sealed class HostedFixture : IAsyncDisposable
    {
        private readonly Process _process;
        public Uri BaseAddress { get; }
        public string Password { get; } = "Hosted-" + Guid.NewGuid().ToString("N") + "!9aA";
        private HostedFixture(Process process, Uri address) { _process = process; BaseAddress = address; }
        public static async Task<HostedFixture> StartAsync(string root)
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, "Server", "거래플랜.Server.Api"))) repo = repo.Parent;
            Assert.NotNull(repo);
            var source = Path.Combine(repo.FullName, "Server", "거래플랜.Server.Api", "bin", "Release", "net8.0");
            var target = Path.Combine(root, "server"); Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (extension is not (".dll" or ".json" or ".so" or ".dylib")) continue;
                var dest = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest);
                File.SetAttributes(dest, File.GetAttributes(dest) & ~FileAttributes.ReadOnly);
            }
            var dll = Path.Combine(target, "거래플랜.Server.Api.dll"); Assert.True(File.Exists(dll), "Build the Release API before running the hosted desktop test.");
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet")
            { WorkingDirectory = target, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(dll);
            var process = new Process { StartInfo = start };
            var fixture = new HostedFixture(process, new Uri($"http://127.0.0.1:{port}/"));
            foreach (var pair in new Dictionary<string,string>
            {
                ["ASPNETCORE_URLS"] = fixture.BaseAddress.ToString(), ["Kestrel__Endpoints__Http__Url"] = fixture.BaseAddress.ToString(),
                ["ASPNETCORE_ENVIRONMENT"] = "Development", ["ERP_DB_FALLBACK_SQLITE"] = "1", ["Database__EnableSqliteFallback"] = "true",
                ["Jwt__Issuer"] = "Mobile.Hosted.Tests", ["Jwt__Audience"] = "Mobile.Hosted.Tests",
                ["Jwt__SigningKey"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                ["Security__RequireHttpsForwardedProto"] = "false", ["SeedUsers__EnableSeedUsers"] = "true", ["SeedUsers__WarnOnDefaultPasswords"] = "false",
                ["SeedUsers__AdminPassword"] = fixture.Password, ["SeedUsers__UserPassword"] = fixture.Password,
                ["SeedUsers__ItwPassword"] = fixture.Password, ["SeedUsers__UsenetPassword"] = fixture.Password,
                ["FileStorage__RootPath"] = Path.Combine(target,"files"), ["Updates__StorageRoot"] = Path.Combine(target,"updates"),
                ["DataProtection__KeyRingPath"] = Path.Combine(target,"keys"), ["Logging__LogLevel__Default"] = "Warning"
            }) start.Environment[pair.Key] = pair.Value;
            // Drain diagnostics without printing authentication material into test output.
            process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
            Assert.True(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            try
            {
                using var client = new HttpClient { BaseAddress = fixture.BaseAddress, Timeout = TimeSpan.FromSeconds(2) };
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    Assert.False(process.HasExited, "Isolated API exited before readiness.");
                    try { using var response = await client.GetAsync("readyz"); if (response.IsSuccessStatusCode) return fixture; }
                    catch (HttpRequestException) { } catch (TaskCanceledException) { }
                    await Task.Delay(150);
                }
                throw new TimeoutException("Isolated API readiness timeout.");
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            _process.Dispose();
        }
    }
