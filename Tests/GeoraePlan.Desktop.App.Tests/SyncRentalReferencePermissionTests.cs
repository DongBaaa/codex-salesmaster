using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncRentalReferencePermissionTests
{
    [Fact]
    public async Task FlushPendingChangesAsync_ProfileEditorWithoutSettingsPermission_IgnoresUnsyncableManagementCompanyDirty()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        db.RentalManagementCompanies.Add(new LocalRentalManagementCompany
        {
                Id = Guid.NewGuid(),
                Code = OfficeCodeCatalog.Usenet,
                Name = "유즈넷",
                IsSystemDefault = true,
                IsActive = true,
                Revision = 3,
                IsDirty = true,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
        });
        var profileId = Guid.NewGuid();
        db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
        {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"SYNC-PROFILE-{profileId:N}",
                CustomerName = "연수구 테스트 거래처",
                BillingCycleMonths = 3,
                Revision = 7,
                IsDirty = true,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();

        var session = new SessionState();
        session.SetSession("test-token", new UserSessionDto
        {
                Username = "yeonsu",
                Role = DomainConstants.RoleUser,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                Permissions =
                [
                    AppPermissionNames.RentalProfileEdit,
                    AppPermissionNames.RentalAssetEdit
                ]
        });

        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rentalState = new RentalStateService(db);
        var handler = new CaptureRentalPushHandler();
        var api = new ErpApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, session);
        var diagnostics = new SyncDiagnosticsService(session);
        using var sync = new SyncService(db, localState, rentalState, api, session, dispatcher, diagnostics);

        var flushed = await sync.FlushPendingChangesAsync();

        Assert.True(flushed);
        Assert.NotNull(handler.LastPushRequest);
        Assert.Empty(handler.LastPushRequest!.RentalManagementCompanies);
        Assert.Equal(profileId, Assert.Single(handler.LastPushRequest.RentalBillingProfiles).Id);
        Assert.True((await db.RentalManagementCompanies.AsNoTracking().SingleAsync()).IsDirty);
        Assert.False((await db.RentalBillingProfiles.AsNoTracking().SingleAsync()).IsDirty);
        Assert.True(await localState.HasPendingSyncChangesAsync());
        Assert.False(await localState.HasPendingSyncChangesAsync(session));
    }

    private sealed class CaptureRentalPushHandler : HttpMessageHandler
    {
        public SyncPushRequest? LastPushRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.Equals("/sync/push", StringComparison.OrdinalIgnoreCase))
            {
                if (request.RequestUri.AbsolutePath.Equals("/sync/pull", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new SyncPullResponse { CurrentServerRevision = 8 }),
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }

            LastPushRequest = await request.Content!.ReadFromJsonAsync<SyncPushRequest>(cancellationToken: cancellationToken);
            var profile = Assert.Single(LastPushRequest!.RentalBillingProfiles);
            var result = new SyncPushResult
            {
                AcceptedCount = 1,
                CurrentServerRevision = 8,
                AcceptedRevisions =
                [
                    new SyncAcceptedRevisionDto
                    {
                        EntityName = nameof(LocalRentalBillingProfile),
                        EntityId = profile.Id,
                        Revision = 8,
                        UpdatedAtUtc = DateTime.UtcNow
                    }
                ]
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json")
            };
        }
    }
}
