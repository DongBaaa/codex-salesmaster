using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RestrictedInvoiceTestLauncherTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("admin-role")]
    [InlineData("money-permission")]
    [InlineData("wrong-office")]
    [InlineData("login-scope")]
    [InlineData("redirect")]
    [InlineData("error-body")]
    public async Task ActualPowerShellProvisioning_ValidatesScopeAndSuppressesCredentials(string scenario)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string? adminSecret = null, staffSecret = null, username = null;
        var paths = new List<string>();
        var server = Task.Run(async () =>
        {
            try
            {
                while (!deadline.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
                    await using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var request = (await reader.ReadLineAsync(deadline.Token))!.Split(' ');
                    Assert.Equal("POST", request[0]); paths.Add(request[1]);
                    var length = 0; string? header;
                    while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(deadline.Token)))
                    {
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = int.Parse(header.Split(':', 2)[1]);
                        if (header.Equals("Expect: 100-continue", StringComparison.OrdinalIgnoreCase))
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), deadline.Token);
                    }
                    Assert.InRange(length, 1, 4096);
                    var body = new char[length]; var offset = 0;
                    while (offset < length)
                    {
                        var read = await reader.ReadAsync(body.AsMemory(offset), deadline.Token);
                        Assert.True(read > 0); offset += read;
                    }
                    using var json = JsonDocument.Parse(new string(body));
                    var data = json.RootElement;
                    object payload;
                    var status = "200 OK";
                    var extraHeader = "";
                    if (paths.Count == 1)
                    {
                        Assert.Equal("/auth/login", request[1]);
                        Assert.Equal("admin", data.GetProperty("username").GetString());
                        adminSecret = data.GetProperty("password").GetString();
                        payload = new { token = "test-admin-token" };
                        if (scenario == "redirect")
                        {
                            status = "307 Temporary Redirect";
                            extraHeader = $"Location: http://127.0.0.1:{port}/should-never-follow\r\n";
                        }
                        if (scenario == "error-body")
                        {
                            status = "400 Bad Request";
                            payload = new { detail = adminSecret };
                        }
                    }
                    else
                    {
                        if (paths.Count == 2)
                        {
                            Assert.Equal("/users", request[1]);
                            username = data.GetProperty("username").GetString();
                            staffSecret = data.GetProperty("password").GetString();
                            Assert.StartsWith("ui-noamount-", username);
                            Assert.True(adminSecret != staffSecret, "Staff credential must be independent.");
                            Assert.Equal("user", data.GetProperty("role").GetString());
                            Assert.Equal("OfficeOnly", data.GetProperty("scopeType").GetString());
                            Assert.Equal("Invoice.Edit", Assert.Single(data.GetProperty("permissions").EnumerateArray()).GetString());
                        }
                        else
                        {
                            Assert.Equal(3, paths.Count);
                            Assert.Equal("/auth/login", request[1]);
                            Assert.Equal(username, data.GetProperty("username").GetString());
                            Assert.True(staffSecret == data.GetProperty("password").GetString());
                        }
                        var user = new Dictionary<string, object?> {
                            ["username"] = username, ["role"] = "User", ["tenantCode"] = "USENET_GROUP",
                            ["officeCode"] = "USENET", ["scopeType"] = "OfficeOnly",
                            ["permissions"] = new[] { "Invoice.Edit" }
                        };
                        // Session DTO deliberately has no IsActive property.
                        if (paths.Count == 2) user["isActive"] = true;
                        if (scenario == "admin-role") user["role"] = "admin";
                        if (scenario == "money-permission") user["permissions"] = new[] { "Invoice.Edit", "Amount.ViewSales" };
                        if (scenario == "wrong-office") user["officeCode"] = "ITWORLD";
                        if (scenario == "login-scope" && paths.Count == 3) user["scopeType"] = "TenantAll";
                        payload = paths.Count == 2 ? user : new { token = "test-staff-token", user };
                    }
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{extraHeader}Content-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), deadline.Token);
                    await stream.WriteAsync(bytes, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            catch (SocketException) when (deadline.IsCancellationRequested) { }
        }, deadline.Token);
        try
        {
            var output = await RunHarnessAsync($"http://127.0.0.1:{port}/", checkProfiles: false);
            Assert.Equal(scenario == "success" ? "accepted" : "rejected", output.Trim());
            Assert.False(adminSecret is not null && output.Contains(adminSecret), "Admin credential leaked.");
            Assert.False(staffSecret is not null && output.Contains(staffSecret), "Staff credential leaked.");
            Assert.Equal(scenario == "success" || scenario == "login-scope" ? 3 : scenario is "redirect" or "error-body" ? 1 : 2, paths.Count);
        }
        finally
        {
            deadline.Cancel(); listener.Stop();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("http://example.invalid:19080/")]
    [InlineData("http://localhost:19080/")]
    [InlineData("https://127.0.0.1:19080/")]
    [InlineData("http://127.0.0.1:19080/other")]
    [InlineData("http://127.0.0.1:19080/?token=invalid")]
    public async Task Provisioning_RejectsNonOwnedEndpointBeforeSending(string endpoint)
        => Assert.Equal("rejected", (await RunHarnessAsync(endpoint, checkProfiles: false)).Trim());

    [Fact]
    public async Task Profile_DefaultRestrictedAndUnknown_AreValidatedAndConsumed()
        => Assert.Equal("profiles-ok", (await RunHarnessAsync("", checkProfiles: true)).Trim());

    private static async Task<string> RunHarnessAsync(string endpoint, bool checkProfiles,
        [CallerFilePath] string sourceFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "../.."));
        var source = File.ReadAllText(Path.Combine(root, "테스트 시행", "테스트-환경-준비.ps1"));
        const string marker = "$runAllPsContent = @'";
        var start = source.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = source.IndexOf("\n'@", start, StringComparison.Ordinal);
        Assert.True(start >= marker.Length && end > start);
        var directory = Path.GetFullPath(Path.Combine(TestProcessIsolation.TempRoot, "restricted-launcher-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            var template = Path.Combine(directory, "template.ps1");
            File.WriteAllText(template, source[start..end].Replace("__RUN_ALL_LOCK_PROBE_PARAMETER__", ""), new UTF8Encoding(true));
            var harness = Path.Combine(directory, "harness.ps1");
            File.WriteAllText(harness, """
                param([string]$Template, [string]$Endpoint, [string]$CheckProfiles)
                $ErrorActionPreference='Stop'
                $tokens=$null; $errors=$null
                $ast=[Management.Automation.Language.Parser]::ParseFile($Template,[ref]$tokens,[ref]$errors)
                if($errors.Count){throw 'Generated launcher parse failed'}
                foreach($name in @('Get-IsolatedTestLoginProfile','Assert-RestrictedInvoiceTestUser','New-RestrictedInvoiceTestLogin','New-LocalTestPassword')){
                    $f=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
                    if($null -eq $f){throw 'Missing function'}
                    . ([scriptblock]::Create($f.Extent.Text))
                }
                if($CheckProfiles -eq '1'){
                    foreach($value in @('', 'Admin', 'RestrictedInvoiceEditor', 'Invalid')){
                        [Environment]::SetEnvironmentVariable('GEORAEPLAN_TEST_LOGIN_PROFILE',$value,'Process')
                        $rejected=$false
                        try{$result=Get-IsolatedTestLoginProfile}catch{$rejected=$true}
                        if([Environment]::GetEnvironmentVariable('GEORAEPLAN_TEST_LOGIN_PROFILE','Process')){throw 'Profile leaked'}
                        if($value -eq 'Invalid'){if(-not $rejected){throw 'Unknown profile accepted'}}
                        elseif($rejected -or $result -cne $(if($value){$value}else{'Admin'})){throw 'Profile mismatch'}
                    }
                    'profiles-ok'; exit 0
                }
                try {
                    $login=New-RestrictedInvoiceTestLogin -ServerUri ([uri]$Endpoint) -AdminPassword (New-LocalTestPassword)
                    if(-not $login.Username.StartsWith('ui-noamount-') -or $login.Password.Length -lt 20){throw 'Incomplete test login'}
                    $login.Clear(); 'accepted'
                } catch { 'rejected' }
                """, new UTF8Encoding(true));
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness, "-Template", template, "-Endpoint", endpoint, "-CheckProfiles", checkProfiles ? "1" : "0" }) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            Assert.Equal(0, process.ExitCode); Assert.Equal("", await stderr);
            return await stdout;
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(TestProcessIsolation.TempRoot).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(directory));
            Directory.Delete(directory, true);
        }
    }
}
