using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SalesAmountHostedSyncTests
{
    [Fact]
    public Task RestrictedEditor_LocalSaveOutboxRealHttpSyncAndRestartPreserveUnknownAmounts()
        => RunRestrictedEditorAsync(useTransportObserver: false);

    [Fact]
    public Task CurrentClient_RestrictedEditorTransportPipelinePreservesUnknownAmounts()
        => RunRestrictedEditorAsync(useTransportObserver: true);

    private static async Task RunRestrictedEditorAsync(bool useTransportObserver)
    {
        var root = Path.Combine(Path.GetTempPath(), "trade-editor-hosted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", Path.Combine(root, "desktop"));
        HostedFixture? server = null;
        Exception? testFailure = null;
        try
        {
            // AppPaths may already be initialized by another test; it must still be a test root.
            Assert.True(AppPaths.IsTestEnvironment);
            Assert.NotEqual(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "거래플랜")), Path.GetFullPath(AppPaths.AppRoot));
            server = await HostedFixture.StartAsync(root);
            using var admin = new HttpClient { BaseAddress = server.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
            var adminLogin = await LoginAsync(admin, "admin", server.Password);
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.Token);
            new DesktopClientIdentityProvider().Apply(admin);
            await PostAsync(admin, "users", new CreateUserRequest
            {
                Username = "amount-editor", Password = server.Password, Role = DomainConstants.RoleUser,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly, Permissions = [AppPermissionNames.InvoiceEdit]
            });
            var customerId = Guid.NewGuid(); var itemId = Guid.NewGuid();
            await PostAsync(admin, "customers", new CustomerDto
            {
                Id = customerId, NameOriginal = "HOSTED-EDITOR-CUSTOMER", TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet
            });
            await PostAsync(admin, "items", new ItemDto
            {
                Id = itemId, NameOriginal = "HOSTED-EDITOR-ITEM", TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, TrackingType = ItemTrackingTypes.NonStock,
                ItemKind = ItemKinds.Product, Unit = "EA", SalePrice = 1100
            });
            var replacementItemId = Guid.NewGuid();
            await PostAsync(admin, "items", new ItemDto
            {
                Id = replacementItemId, NameOriginal = "HOSTED-REPLACEMENT-ITEM", TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, TrackingType = ItemTrackingTypes.NonStock,
                ItemKind = ItemKinds.Product, Unit = "EA", SalePrice = 2200
            });
            using var loginHttp = new HttpClient { BaseAddress = server.BaseAddress };
            var login = await LoginAsync(loginHttp, "amount-editor", server.Password);
            var session = new SessionState(); session.SetSession(login.Token, login.User, login.ExpiresAtUtc);
            var dbPath = Path.Combine(root, "desktop.db");
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(new SqliteConnectionStringBuilder
                { DataSource = dbPath, Pooling = false }.ToString()).Options;
            Guid invoiceId;
            await using (var db = new LocalDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var dispatcher = new SyncRequestDispatcher();
                var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
                using var capture = new CapturePushHandler();
                using var pipeline = useTransportObserver
                    ? new DesktopUpgradeRequiredHandler { InnerHandler = capture }
                    : (HttpMessageHandler)capture;
                using var http = new HttpClient(pipeline) { BaseAddress = server.BaseAddress };
                var api = new ErpApiClient(http, session, local);
                using var sync = new SyncService(db, local, new RentalStateService(db), api, session, dispatcher,
                    new SyncDiagnosticsService(session, () => new LocalDbContext(options)));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                Assert.True(await sync.TrySyncAsync(deadline.Token), await LastSyncErrorAsync(db) + capture.FailureSummary);
                db.ChangeTracker.Clear();
                var customer = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == customerId);
                var item = await db.Items.AsNoTracking().SingleAsync(x => x.Id == itemId);
                using (var editor = new SalesViewModel(local, null!, null!, session))
                {
                    await editor.LoadAsync();
                    editor.SetCustomer(customer);
                    editor.ApplyInputItem(item);
                    editor.InputQty = 3;
                    editor.InputRemark = "신규 비고";
                    editor.InvoiceMemo = "신규 메모";
                    editor.AddLineCommand.Execute(null);
                    Assert.True(await editor.TryAutoSaveOnCloseAsync(), editor.LastAutoSaveFailureMessage);
                    invoiceId = editor.InvoiceId;
                    Assert.Equal("비공개", editor.TotalAmountDisplay);
                }
                db.ChangeTracker.Clear();
                var pending = await db.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == invoiceId);
                Assert.True(pending.IsDirty);
                Assert.True(pending.AmountsHidden);
                Assert.Null(LocalMappings.ToDto(pending).Lines.Single().UnitPrice);
                db.ChangeTracker.Clear();
                Assert.True(await sync.TrySyncAsync(deadline.Token), await LastSyncErrorAsync(db) + capture.FailureSummary);
                var first = (await admin.GetFromJsonAsync<InvoiceDto>($"invoices/{invoiceId}"))!;
                Assert.Equal(3300m, first.TotalAmount);
                Assert.Equal(1100m, first.Lines.Single().UnitPrice);
                Assert.Equal("신규 비고", first.Lines.Single().Remark);
                db.ChangeTracker.Clear();
                var hidden = (await local.GetInvoiceAsync(invoiceId, session))!;
                Assert.True(hidden.AmountsHidden);
                Assert.False(hidden.IsDirty);
                using (var editor = new SalesViewModel(local, null!, null!, session))
                {
                    await editor.LoadAsync();
                    await editor.LoadInvoiceAsync(hidden);
                    editor.Lines.Single().Quantity = 4;
                    editor.Lines.Single().Remark = "수정 비고";
                    editor.InvoiceMemo = "수정 메모";
                    Assert.True(await editor.TryAutoSaveOnCloseAsync(), editor.LastAutoSaveFailureMessage);
                    invoiceId = editor.InvoiceId;
                }
                db.ChangeTracker.Clear();
                Assert.True(await sync.TrySyncAsync(deadline.Token), await LastSyncErrorAsync(db) + capture.FailureSummary);
                var updated = (await admin.GetFromJsonAsync<InvoiceDto>($"invoices/{invoiceId}"))!;
                Assert.Equal(4400m, updated.TotalAmount);
                Assert.Equal("수정 메모", updated.Memo);
                Assert.Equal("수정 비고", updated.Lines.Single().Remark);
                db.ChangeTracker.Clear();
                var replacement = await db.Items.AsNoTracking().SingleAsync(x => x.Id == replacementItemId);
                hidden = (await local.GetInvoiceAsync(invoiceId, session))!;
                using (var editor = new SalesViewModel(local, null!, null!, session))
                {
                    await editor.LoadAsync(); await editor.LoadInvoiceAsync(hidden);
                    editor.Lines.Clear(); editor.ApplyInputItem(replacement);
                    editor.InputQty = 5; editor.InputRemark = "품목 교체 비고";
                    editor.InvoiceMemo = "품목 교체 메모"; editor.AddLineCommand.Execute(null);
                    Assert.Equal("비공개", editor.TotalAmountDisplay);
                    Assert.True(await editor.TryAutoSaveOnCloseAsync(), editor.LastAutoSaveFailureMessage);
                    invoiceId = editor.InvoiceId;
                }
                db.ChangeTracker.Clear();
                Assert.True(await sync.TrySyncAsync(deadline.Token), await LastSyncErrorAsync(db) + capture.FailureSummary);
                updated = (await admin.GetFromJsonAsync<InvoiceDto>($"invoices/{invoiceId}"))!;
                Assert.Equal(11000m, updated.TotalAmount);
                Assert.Equal(replacementItemId, updated.Lines.Single().ItemId);
                Assert.Equal(2200m, updated.Lines.Single().UnitPrice);
                Assert.Equal("품목 교체 비고", updated.Lines.Single().Remark);
                var revision = updated.Revision;
                Assert.True(await sync.TrySyncAsync(deadline.Token), await LastSyncErrorAsync(db) + capture.FailureSummary);
                Assert.Equal(revision, (await admin.GetFromJsonAsync<InvoiceDto>($"invoices/{invoiceId}"))!.Revision);
                Assert.True(capture.Invoices.Count >= 3);
                Assert.Equal(1, capture.LostAcknowledgements);
                Assert.Contains(capture.Invoices.GroupBy(x => x.MutationId), group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1);
                Assert.All(capture.Invoices, invoice =>
                {
                    Assert.Null(invoice.TotalAmount); Assert.Null(invoice.SupplyAmount); Assert.Null(invoice.VatAmount);
                    Assert.All(invoice.Lines, line => { Assert.Null(line.UnitPrice); Assert.Null(line.LineAmount); });
                });
                await using var serverDb = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = Path.Combine(root, "server", "거래플랜-local.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                await serverDb.OpenAsync(deadline.Token);
                await using var count = serverDb.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM Invoices WHERE lower(CustomerId)=lower($customer);";
                count.Parameters.AddWithValue("$customer", customerId.ToString("D"));
                Assert.Equal((long)capture.Invoices.Select(x => x.Id).Distinct().Count(), (long)(await count.ExecuteScalarAsync(deadline.Token))!);
            }
            await using (var reopened = new LocalDbContext(options))
            {
                var stored = await reopened.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == invoiceId);
                Assert.True(stored.AmountsHidden);
                Assert.False(stored.IsDirty);
                Assert.Equal(5m, stored.Lines.Single(x => !x.IsDeleted).Quantity);
                Assert.Equal(replacementItemId, stored.Lines.Single(x => !x.IsDeleted).ItemId);
                Assert.Equal("품목 교체 메모", stored.Memo);
                Assert.Null(LocalMappings.ToDto(stored).TotalAmount);
                var sent = await reopened.SyncOutboxEntries.Where(x => x.EntityName == nameof(LocalInvoice)).ToListAsync();
                Assert.NotEmpty(sent);
                Assert.All(sent, x => Assert.NotNull(x.AcknowledgedAtUtc));
            }
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", previousRoot);
            // Only the randomly named fixture root created above is eligible for cleanup.
            var full = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), full, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("trade-editor-hosted-", Path.GetFileName(full), StringComparison.Ordinal);
            for (var attempt = 0; Directory.Exists(full); attempt++)
            {
                try { Directory.Delete(full, recursive: true); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 5)
                    {
                        if (testFailure is not null) throw new AggregateException(testFailure, ex);
                        throw;
                    }
                    await Task.Delay(100 * (attempt + 1));
                }
            }
        }
    }

    private static async Task<string> LastSyncErrorAsync(LocalDbContext db)
        => (await db.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == "Sync.LastError"))?.Value ?? "No sync error detail";
    private static async Task PostAsync<T>(HttpClient client, string path, T body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, $"Fixture POST {path}: {(int)response.StatusCode}");
    }
    private static async Task<LoginResponse> LoginAsync(HttpClient client, string username, string password)
    {
        using var response = await client.PostAsJsonAsync("auth/login", new LoginRequest { Username = username, Password = password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private sealed class CapturePushHandler : DelegatingHandler
    {
        public List<InvoiceDto> Invoices { get; } = [];
        public int LostAcknowledgements { get; private set; }
        public List<string> Failures { get; } = [];
        public string FailureSummary => string.Join("; ", Failures);
        public CapturePushHandler() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.True(request.RequestUri!.IsLoopback);
            Assert.Equal("4", Assert.Single(request.Headers.GetValues(ClientCompatibilityHeaders.Protocol)));
            var hasInvoice = false;
            if (request.RequestUri.AbsolutePath == "/sync/push")
            {
                // Buffer the JSON without disposing JsonContent's stream before the
                // real transport sends it to Kestrel.
                var json = await request.Content!.ReadAsStringAsync(ct);
                var body = JsonSerializer.Deserialize<SyncPushRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Invoices.AddRange(body!.Invoices);
                hasInvoice = body.Invoices.Count > 0;
            }
            var response = await base.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.UpgradeRequired)
            {
                var gate = await response.Content.ReadFromJsonAsync<ClientUpgradeRequiredResponse>(cancellationToken: ct);
                Failures.Add($"{request.RequestUri.AbsolutePath}: HTTP 426; sent protocol {gate?.Client.ProtocolVersion}; required {gate?.Required.MinimumProtocolVersion}");
            }
            else if (!response.IsSuccessStatusCode)
                Failures.Add($"{request.RequestUri.AbsolutePath}: HTTP {(int)response.StatusCode}");
            if (hasInvoice && response.IsSuccessStatusCode && LostAcknowledgements == 0)
            {
                // The API committed the write, but the desktop did not receive its
                // acknowledgement. Its normal retry must reuse the same mutation.
                LostAcknowledgements++;
                response.Dispose();
                throw new HttpRequestException("Fixture lost acknowledgement after successful server commit.");
            }
            return response;
        }
    }

    private sealed class HostedFixture : IAsyncDisposable
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
                ["Jwt__Issuer"] = "Desktop.Hosted.Tests", ["Jwt__Audience"] = "Desktop.Hosted.Tests",
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
}
