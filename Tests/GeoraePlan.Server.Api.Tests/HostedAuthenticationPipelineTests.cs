using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class HostedAuthenticationPipelineTests
{
    [Fact]
    public async Task HostedPipeline_EnforcesAuthenticationAndAdminAuthorization()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-hosted-auth",
            Guid.NewGuid().ToString("N"));
        var serverRoot = Path.Combine(testRoot, "server");
        var sourceRoot = AppContext.BaseDirectory;
        var serverDll = Path.Combine(
            serverRoot,
            "거래플랜.Server.Api.dll");
        var adminPassword =
            "HostedAdmin-" + Guid.NewGuid().ToString("N") + "!9aA";
        var userPassword =
            "HostedUser-" + Guid.NewGuid().ToString("N") + "!9aA";
        var port = GetAvailableLoopbackPort();

        Directory.CreateDirectory(serverRoot);
        CopyDirectory(sourceRoot, serverRoot);
        Assert.True(
            File.Exists(serverDll),
            $"Hosted server assembly was not copied: {serverDll}");

        await using var server = StartServer(
            serverRoot,
            serverDll,
            port,
            adminPassword,
            userPassword);
        using var client = new HttpClient(
            new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        try
        {
            await WaitUntilReadyAsync(server, client);

            using (var health =
                   await client.GetAsync("healthz"))
            {
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                using var payload =
                    JsonDocument.Parse(
                        await health.Content.ReadAsStringAsync());
                Assert.Equal(
                    "ok",
                    payload.RootElement
                        .GetProperty("status")
                        .GetString());
                AssertCompatibilitySnapshot(
                    payload.RootElement.GetProperty(
                        "clientCompatibility"));
            }

            using (var readiness =
                   await client.GetAsync("readyz"))
            {
                Assert.Equal(
                    HttpStatusCode.OK,
                    readiness.StatusCode);
                using var payload =
                    JsonDocument.Parse(
                        await readiness.Content.ReadAsStringAsync());
                Assert.Equal(
                    "ready",
                    payload.RootElement
                        .GetProperty("status")
                        .GetString());
                AssertCompatibilitySnapshot(
                    payload.RootElement.GetProperty(
                        "clientCompatibility"));
            }

            using (var anonymousUsers =
                   await client.GetAsync("users"))
            {
                Assert.Equal(
                    HttpStatusCode.Unauthorized,
                    anonymousUsers.StatusCode);
            }

            var userToken = await LoginAsync(
                client,
                "user",
                userPassword);

            var deniedProfileId = Guid.NewGuid();
            var deniedMutationId =
                "hosted-permission-denied-" +
                Guid.NewGuid().ToString("N");
            var sqliteDbPath = Path.Combine(
                serverRoot,
                "거래플랜-local.db");
            var beforeDeniedPush =
                await ReadSyncMutationStateAsync(
                    sqliteDbPath,
                    deniedProfileId,
                    deniedMutationId);
            using (var userPushRequest =
                   new HttpRequestMessage(
                       HttpMethod.Post,
                       "sync/push"))
            {
                userPushRequest.Headers.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        userToken);
                userPushRequest.Content = JsonContent.Create(
                    new SyncPushRequest
                    {
                        DeviceId = "hosted-auth-pipeline",
                        CompanyProfiles =
                        [
                            new CompanyProfileDto
                            {
                                Id = deniedProfileId,
                                MutationId = deniedMutationId,
                                OfficeCode =
                                    OfficeCodeCatalog.Yeonsu,
                                ProfileName =
                                    "권한 없는 동기화 요청",
                                TradeName =
                                    "저장되면 안 되는 회사설정",
                                CreatedAtUtc =
                                    DateTime.UtcNow.AddMinutes(-1),
                                UpdatedAtUtc =
                                    DateTime.UtcNow
                            }
                        ]
                    });
                using var forbiddenPush =
                    await client.SendAsync(userPushRequest);
                Assert.Equal(
                    HttpStatusCode.Forbidden,
                    forbiddenPush.StatusCode);
            }

            var afterDeniedPush =
                await ReadSyncMutationStateAsync(
                    sqliteDbPath,
                    deniedProfileId,
                    deniedMutationId);
            Assert.Equal(
                beforeDeniedPush.CompanyProfileCount,
                afterDeniedPush.CompanyProfileCount);
            Assert.Equal(
                beforeDeniedPush.MutationReceiptCount,
                afterDeniedPush.MutationReceiptCount);
            Assert.Equal(
                0,
                afterDeniedPush.TargetCompanyProfileCount);
            Assert.Equal(
                0,
                afterDeniedPush.TargetMutationReceiptCount);

            using (var userRequest =
                   new HttpRequestMessage(HttpMethod.Get, "users"))
            {
                userRequest.Headers.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        userToken);
                using var forbiddenUsers =
                    await client.SendAsync(userRequest);
                Assert.Equal(
                    HttpStatusCode.Forbidden,
                    forbiddenUsers.StatusCode);
            }

            var adminToken = await LoginAsync(
                client,
                "admin",
                adminPassword);
            using (var adminRequest =
                   new HttpRequestMessage(HttpMethod.Get, "users"))
            {
                adminRequest.Headers.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        adminToken);
                using var authorizedUsers =
                    await client.SendAsync(adminRequest);
                Assert.Equal(
                    HttpStatusCode.OK,
                    authorizedUsers.StatusCode);
                var users =
                    await authorizedUsers.Content
                        .ReadFromJsonAsync<List<UserAccountDto>>();
                Assert.NotNull(users);
                Assert.Contains(
                    users,
                    static user => string.Equals(
                        user.Username,
                        "admin",
                        StringComparison.Ordinal));
            }

            using (var invalidTokenRequest =
                   new HttpRequestMessage(HttpMethod.Get, "users"))
            {
                invalidTokenRequest.Headers.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        "not-a-valid-jwt");
                using var invalidTokenResponse =
                    await client.SendAsync(invalidTokenRequest);
                Assert.Equal(
                    HttpStatusCode.Unauthorized,
                    invalidTokenResponse.StatusCode);
            }
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                ex.Message +
                Environment.NewLine +
                server.Diagnostics);
        }
        finally
        {
            await server.StopAsync();
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    private static async Task<string> LoginAsync(
        HttpClient client,
        string username,
        string password)
    {
        using var response = await client.PostAsJsonAsync(
            "auth/login",
            new LoginRequest
            {
                Username = username,
                Password = password
            });
        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);
        var login =
            await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);
        Assert.False(
            string.IsNullOrWhiteSpace(login.Token));
        return login.Token;
    }

    private static async Task<SyncMutationState>
        ReadSyncMutationStateAsync(
            string sqliteDbPath,
            Guid companyProfileId,
            string mutationId)
    {
        var connectionString =
            new SqliteConnectionStringBuilder
            {
                DataSource = sqliteDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
        await using var connection =
            new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM "CompanyProfiles"),
                (SELECT COUNT(*) FROM "ProcessedSyncMutations"),
                (SELECT COUNT(*) FROM "CompanyProfiles"
                    WHERE lower("Id") = lower($companyProfileId)),
                (SELECT COUNT(*) FROM "ProcessedSyncMutations"
                    WHERE lower(trim("MutationId")) =
                          lower(trim($mutationId)));
            """;
        command.Parameters.AddWithValue(
            "$companyProfileId",
            companyProfileId.ToString("D"));
        command.Parameters.AddWithValue(
            "$mutationId",
            mutationId);

        await using var reader =
            await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new SyncMutationState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    private static async Task WaitUntilReadyAsync(
        HostedServerProcess server,
        HttpClient client)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (server.HasExited)
            {
                throw new InvalidOperationException(
                    "Hosted server exited before readiness." +
                    Environment.NewLine +
                    server.Diagnostics);
            }

            try
            {
                using var response =
                    await client.GetAsync("readyz");
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            "Hosted server readiness timed out." +
            Environment.NewLine +
            server.Diagnostics);
    }

    private static void AssertCompatibilitySnapshot(
        JsonElement snapshot)
    {
        Assert.Equal(
            "AuditOnly",
            snapshot.GetProperty("mode").GetString());
        Assert.Equal(
            0,
            snapshot.GetProperty(
                "configuredPolicyCount").GetInt32());
        Assert.Equal(
            0,
            snapshot.GetProperty(
                "enabledPolicyCount").GetInt32());
        Assert.Equal(
            JsonValueKind.Array,
            snapshot.GetProperty("policies").ValueKind);
        Assert.Equal(
            0,
            snapshot.GetProperty("policies").GetArrayLength());
        Assert.False(
            snapshot.TryGetProperty(
                "updateUrl",
                out _));
        Assert.False(
            snapshot.TryGetProperty(
                "upgradeToken",
                out _));
    }

    private static HostedServerProcess StartServer(
        string serverRoot,
        string serverDll,
        int port,
        string adminPassword,
        string userPassword)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetCommand(),
            WorkingDirectory = serverRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(serverDll);
        startInfo.Environment["ASPNETCORE_URLS"] =
            $"http://127.0.0.1:{port}";
        startInfo.Environment["Kestrel__Endpoints__Http__Url"] =
            $"http://127.0.0.1:{port}";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] =
            "Development";
        startInfo.Environment["ERP_DB_FALLBACK_SQLITE"] =
            "1";
        startInfo.Environment["Database__EnableSqliteFallback"] =
            "true";
        startInfo.Environment["Jwt__Issuer"] =
            "GeoraePlan.HostedAuth.Tests";
        startInfo.Environment["Jwt__Audience"] =
            "GeoraePlan.HostedAuth.Tests";
        startInfo.Environment["Jwt__SigningKey"] =
            "HostedAuth-" + Guid.NewGuid().ToString("N") +
            Guid.NewGuid().ToString("N");
        startInfo.Environment["Security__RequireHttpsForwardedProto"] =
            "false";
        startInfo.Environment["SeedUsers__EnableSeedUsers"] =
            "true";
        startInfo.Environment["SeedUsers__WarnOnDefaultPasswords"] =
            "false";
        startInfo.Environment["SeedUsers__AdminPassword"] =
            adminPassword;
        startInfo.Environment["SeedUsers__UserPassword"] =
            userPassword;
        startInfo.Environment["SeedUsers__ItwPassword"] =
            "HostedItw-" + Guid.NewGuid().ToString("N") + "!9aA";
        startInfo.Environment["SeedUsers__UsenetUsername"] =
            "usenet";
        startInfo.Environment["SeedUsers__UsenetPassword"] =
            "HostedUsenet-" + Guid.NewGuid().ToString("N") + "!9aA";
        startInfo.Environment["FileStorage__RootPath"] =
            Path.Combine(serverRoot, "FileStore");
        startInfo.Environment["Updates__StorageRoot"] =
            Path.Combine(serverRoot, "updates");
        startInfo.Environment["DataProtection__KeyRingPath"] =
            Path.Combine(serverRoot, "data-protection-keys");
        startInfo.Environment["Logging__LogLevel__Default"] =
            "Warning";
        startInfo.Environment[
            "Logging__LogLevel__Microsoft.AspNetCore"] =
            "Warning";
        startInfo.Environment[
            "Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command"] =
            "Warning";

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var diagnostics = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
                return;
            lock (diagnostics)
                diagnostics.AppendLine(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
                return;
            lock (diagnostics)
                diagnostics.AppendLine(args.Data);
        };

        Assert.True(
            process.Start(),
            "Hosted server process did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new HostedServerProcess(
            process,
            diagnostics);
    }

    private static string ResolveDotnetCommand()
    {
        var environmentDotnet =
            Environment.GetEnvironmentVariable("DOTNET_EXE");
        if (
            !string.IsNullOrWhiteSpace(environmentDotnet) &&
            File.Exists(environmentDotnet)
        )
        {
            return environmentDotnet;
        }

        if (OperatingSystem.IsWindows())
        {
            var programFilesDotnet = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),
                "dotnet",
                "dotnet.exe");
            if (File.Exists(programFilesDotnet))
                return programFilesDotnet;
        }

        return "dotnet";
    }

    private static int GetAvailableLoopbackPort()
    {
        var listener = new TcpListener(
            IPAddress.Loopback,
            0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void CopyDirectory(
        string sourceRoot,
        string targetRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(
                Path.Combine(
                    targetRoot,
                    Path.GetRelativePath(
                        sourceRoot,
                        directory)));
        }

        foreach (var file in Directory.EnumerateFiles(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            var target = Path.Combine(
                targetRoot,
                Path.GetRelativePath(
                    sourceRoot,
                    file));
            Directory.CreateDirectory(
                Path.GetDirectoryName(target)
                ?? targetRoot);
            File.Copy(
                file,
                target,
                overwrite: true);
        }
    }

    private static async Task DeleteDirectoryWithRetriesAsync(
        string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                Directory.Delete(
                    path,
                    recursive: true);
                return;
            }
            catch when (attempt < 5)
            {
                await Task.Delay(100 * (attempt + 1));
            }
        }
    }

    private sealed class HostedServerProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _diagnostics;

        public HostedServerProcess(
            Process process,
            StringBuilder diagnostics)
        {
            _process = process;
            _diagnostics = diagnostics;
        }

        public bool HasExited => _process.HasExited;

        public string Diagnostics
        {
            get
            {
                lock (_diagnostics)
                    return _diagnostics.ToString();
            }
        }

        public async Task StopAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(
                    entireProcessTree: true);
            }

            try
            {
                await _process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _process.Dispose();
        }
    }

    private sealed record SyncMutationState(
        long CompanyProfileCount,
        long MutationReceiptCount,
        long TargetCompanyProfileCount,
        long TargetMutationReceiptCount);
}
