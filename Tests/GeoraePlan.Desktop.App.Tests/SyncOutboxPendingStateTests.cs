using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncOutboxPendingStateTests
{
    [Fact]
    public async Task HasPendingSyncChangesAsync_TreatsNonAcknowledgedOutboxAsPending()
    {
        PrepareAppRoot("georaeplan-outbox-pending-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            db.SyncOutboxEntries.Add(CreateOutboxEntry("Sent"));
            await db.SaveChangesAsync();

            Assert.True(await local.HasPendingSyncChangesAsync());

            var summary = await local.GetPendingSyncSummaryAsync();
            Assert.Contains(summary.Buckets, bucket => bucket.EntityDisplayName == "동기화 전송 확인");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MarkOutboxAcknowledgedForCleanEntities_RequiresPulledMatchingServerSnapshot()
    {
        PrepareAppRoot("georaeplan-outbox-reconcile-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "outbox 서버 확인 거래처",
                NameMatchKey = "outbox 서버 확인 거래처",
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now,
                Revision = 12,
                IsDirty = false
            });
            db.SyncOutboxEntries.Add(CreateOutboxEntry("Sent", nameof(LocalCustomer), customerId));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var changedWithoutServerEvidence = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                sync,
                []);

            Assert.Equal(0, changedWithoutServerEvidence);
            Assert.Equal("Sent", await ReadOutboxStatusAsync(db));

            var mismatchDto = LocalMappings.ToDto(await db.Customers.AsNoTracking().SingleAsync(customer => customer.Id == customerId));
            mismatchDto.NameOriginal = "서버의 다른 거래처명";
            mismatchDto.NameMatchKey = "서버의 다른 거래처명";
            var changedWithMismatch = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                sync,
                [mismatchDto]);

            Assert.Equal(0, changedWithMismatch);
            Assert.Equal("Sent", await ReadOutboxStatusAsync(db));

            var matchingDto = LocalMappings.ToDto(await db.Customers.AsNoTracking().SingleAsync(customer => customer.Id == customerId));
            var changedWithMatchingServerSnapshot = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                sync,
                [matchingDto]);

            Assert.Equal(1, changedWithMatchingServerSnapshot);
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncOutboxDiagnosticsOperations_AreLimitedToCurrentSessionScope()
    {
        PrepareAppRoot("georaeplan-outbox-session-scope-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetSession = CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), usenetSession);
            var usenetFailedId = Guid.NewGuid();
            var itworldFailedId = Guid.NewGuid();
            var usenetAcknowledgedId = Guid.NewGuid();
            var itworldAcknowledgedId = Guid.NewGuid();
            db.SyncOutboxEntries.AddRange(
                CreateOutboxEntry(
                    "Failed",
                    entryId: usenetFailedId,
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet,
                    responsibleOfficeCode: OfficeCodeCatalog.Usenet),
                CreateOutboxEntry(
                    "Failed",
                    entryId: itworldFailedId,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld),
                CreateOutboxEntry(
                    "Acknowledged",
                    entryId: usenetAcknowledgedId,
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet,
                    responsibleOfficeCode: OfficeCodeCatalog.Usenet),
                CreateOutboxEntry(
                    "Acknowledged",
                    entryId: itworldAcknowledgedId,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld));
            await db.SaveChangesAsync();

            var scopedEntries = await local.GetSyncOutboxEntriesAsync(usenetSession, 20);
            Assert.Equal(2, scopedEntries.Count);
            Assert.All(scopedEntries, entry => Assert.Equal(OfficeCodeCatalog.Usenet, entry.ResponsibleOfficeCode));

            var scopedSummary = await local.GetSyncOutboxSummaryAsync(usenetSession);
            Assert.Equal(2, scopedSummary.TotalCount);
            Assert.Equal(1, scopedSummary.FailedCount);
            Assert.Equal(1, scopedSummary.AcknowledgedCount);

            Assert.Equal(0, await local.ResetSyncOutboxEntriesForRetryAsync([itworldFailedId], usenetSession));
            Assert.Equal("Failed", await ReadOutboxStatusAsync(db, itworldFailedId));

            Assert.Equal(1, await local.ResetAllPendingSyncOutboxEntriesForRetryAsync(usenetSession));
            Assert.Equal("Prepared", await ReadOutboxStatusAsync(db, usenetFailedId));
            Assert.Equal("Failed", await ReadOutboxStatusAsync(db, itworldFailedId));

            Assert.Equal(1, await local.ClearAcknowledgedSyncOutboxEntriesAsync(usenetSession));
            Assert.False(await db.SyncOutboxEntries.AsNoTracking().AnyAsync(entry => entry.Id == usenetAcknowledgedId));
            Assert.True(await db.SyncOutboxEntries.AsNoTracking().AnyAsync(entry => entry.Id == itworldAcknowledgedId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalIntegrityReport_OutboxIssuesAreLimitedToCurrentSessionScope()
    {
        PrepareAppRoot("georaeplan-integrity-outbox-session-scope-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetSession = CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), usenetSession);
            var staleSentAtUtc = DateTime.UtcNow.AddMinutes(-30);
            db.SyncOutboxEntries.AddRange(
                CreateOutboxEntry(
                    "Failed",
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet,
                    responsibleOfficeCode: OfficeCodeCatalog.Usenet),
                CreateOutboxEntry(
                    "Failed",
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld),
                CreateOutboxEntry(
                    "Sent",
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet,
                    responsibleOfficeCode: OfficeCodeCatalog.Usenet,
                    sentAtUtc: staleSentAtUtc),
                CreateOutboxEntry(
                    "Sent",
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld,
                    sentAtUtc: staleSentAtUtc));
            await db.SaveChangesAsync();

            var report = await local.BuildIntegrityReportAsync(usenetSession);

            var failedIssue = Assert.Single(report.Issues, issue => issue.Code == "sync_outbox_failed_pending");
            Assert.Equal(1, failedIssue.Count);
            var staleIssue = Assert.Single(report.Issues, issue => issue.Code == "sync_outbox_sent_stuck");
            Assert.Equal(1, staleIssue.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_ForbiddenPush_KeepsDirtyAndRecordsReadableOutboxFailure()
    {
        const string permissionMessage = "현재 계정 권한으로 서버 동기화 반영이 허용되지 않는 변경이 포함되어 있습니다: 환경설정/분류";
        PrepareAppRoot("georaeplan-forbidden-push-outbox-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.Parse("99999999-aaaa-bbbb-cccc-dddddddddddd");
            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = "권한 거부 테스트 단위",
                IsActive = true,
                Revision = 0,
                IsDirty = true,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new ForbiddenPushThenEmptyPullHandler(permissionMessage);
            using var sync = CreateSyncService(db, session, handler);

            var synced = await sync.FlushPendingChangesAsync();

            Assert.False(synced);
            Assert.Equal(1, handler.PushCount);
            Assert.True(await db.Units.IgnoreQueryFilters().AsNoTracking().AnyAsync(unit => unit.Id == unitId && unit.IsDirty));

            var outbox = await db.SyncOutboxEntries.AsNoTracking().SingleAsync();
            Assert.Equal("Failed", outbox.Status);
            Assert.Contains(permissionMessage, outbox.ErrorMessage);
            Assert.DoesNotContain("{\"message\"", outbox.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\u", outbox.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            var lastError = await db.Settings
                .AsNoTracking()
                .Where(setting => setting.Key == "Sync.LastError")
                .Select(setting => setting.Value)
                .SingleAsync();
            Assert.Contains("동기화 업로드(sync/push) 실패", lastError);
            Assert.Contains(permissionMessage, lastError);
            Assert.DoesNotContain("{\"message\"", lastError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\u", lastError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalBillingProfileAcceptedAlias_IsAcknowledgedAndCanonicalizedByPull()
    {
        PrepareAppRoot("georaeplan-rental-profile-accepted-alias");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var profileKey = "USENET|ACCEPTED-ALIAS-PC|개별|후불|25|1|전자세금계산서";
            var localTemporaryProfileId = Guid.NewGuid();
            var canonicalProfileId = Guid.NewGuid();
            var mutationId = $"test-device:{nameof(LocalRentalBillingProfile)}:{localTemporaryProfileId:N}:accepted-alias";
            var acceptedUpdatedAtUtc = DateTime.UtcNow;

            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = localTemporaryProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = profileKey,
                CustomerName = "accepted alias pc customer",
                BusinessNumber = "111-22-33333",
                ItemName = "IMC2010",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingMethod = "전자세금계산서",
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 55_000m,
                Revision = 0,
                IsDirty = true,
                CreatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-30),
                UpdatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-20)
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = mutationId,
                DeviceId = "test-device",
                EntityName = nameof(LocalRentalBillingProfile),
                EntityId = localTemporaryProfileId,
                ExpectedRevision = 0,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                Status = "Sent",
                PreparedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-10),
                SentAtUtc = acceptedUpdatedAtUtc.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var acceptedRevisions = new List<SyncAcceptedRevisionDto>
            {
                new()
                {
                    EntityName = "RentalBillingProfile",
                    EntityId = canonicalProfileId,
                    Revision = 42,
                    UpdatedAtUtc = acceptedUpdatedAtUtc
                },
                new()
                {
                    EntityName = "RentalBillingProfile",
                    EntityId = localTemporaryProfileId,
                    Revision = 42,
                    UpdatedAtUtc = acceptedUpdatedAtUtc
                }
            };

            await InvokeApplyAcceptedRevisionsAsync(sync, acceptedRevisions);
            await InvokeMarkOutboxAcknowledgedAsync(
                sync,
                new SyncPushRequest
                {
                    DeviceId = "test-device",
                    RentalBillingProfiles =
                    [
                        new RentalBillingProfileDto
                        {
                            Id = localTemporaryProfileId,
                            MutationId = mutationId
                        }
                    ]
                },
                acceptedRevisions);

            var acknowledgedOutbox = await db.SyncOutboxEntries.AsNoTracking().SingleAsync();
            Assert.Equal("Acknowledged", acknowledgedOutbox.Status);
            Assert.NotNull(acknowledgedOutbox.AcknowledgedAtUtc);

            var acceptedLocalProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(profile => profile.Id == localTemporaryProfileId);
            Assert.False(acceptedLocalProfile.IsDirty);
            Assert.Equal(42, acceptedLocalProfile.Revision);

            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    RentalBillingProfiles =
                    [
                        new RentalBillingProfileDto
                        {
                            Id = canonicalProfileId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                            ProfileKey = profileKey,
                            CustomerName = "accepted alias pc customer",
                            BusinessNumber = "111-22-33333",
                            ItemName = "IMC2010",
                            BillingType = "개별",
                            BillingAdvanceMode = "후불",
                            BillingMethod = "전자세금계산서",
                            BillingDay = 28,
                            BillingCycleMonths = 1,
                            MonthlyAmount = 77_000m,
                            Revision = 43,
                            CreatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-1),
                            UpdatedAtUtc = acceptedUpdatedAtUtc
                        }
                    ]
                });

            db.ChangeTracker.Clear();
            var profiles = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync();
            var canonicalProfile = Assert.Single(profiles);
            Assert.Equal(canonicalProfileId, canonicalProfile.Id);
            Assert.Equal(profileKey, canonicalProfile.ProfileKey);
            Assert.False(canonicalProfile.IsDirty);
            Assert.Equal(43, canonicalProfile.Revision);
            Assert.Equal(28, canonicalProfile.BillingDay);
            Assert.Equal(77_000m, canonicalProfile.MonthlyAmount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalSyncOutboxEntry CreateOutboxEntry(
        string status,
        string entityName = nameof(LocalCustomer),
        Guid? entityId = null,
        Guid? entryId = null,
        string tenantCode = TenantScopeCatalog.UsenetGroup,
        string officeCode = OfficeCodeCatalog.Usenet,
        string responsibleOfficeCode = OfficeCodeCatalog.Usenet,
        DateTime? sentAtUtc = null)
        => new()
        {
            Id = entryId ?? Guid.NewGuid(),
            MutationId = $"test-device:{entityName}:{(entityId ?? Guid.NewGuid()):N}:1:{DateTime.UtcNow.Ticks}:0",
            DeviceId = "test-device",
            EntityName = entityName,
            EntityId = entityId ?? Guid.NewGuid(),
            ExpectedRevision = 1,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            Status = status,
            PreparedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            SentAtUtc = sentAtUtc ?? DateTime.UtcNow
        };

    private static Task<string> ReadOutboxStatusAsync(LocalDbContext db)
        => db.SyncOutboxEntries
            .AsNoTracking()
            .Select(entry => entry.Status)
            .SingleAsync();

    private static Task<string> ReadOutboxStatusAsync(LocalDbContext db, Guid entryId)
        => db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry => entry.Id == entryId)
            .Select(entry => entry.Status)
            .SingleAsync();

    private static async Task<int> InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<TLocal, TDto>(
        SyncService sync,
        IReadOnlyCollection<TDto> serverEntities)
        where TLocal : class, ILocalSyncEntity
        where TDto : SyncEntityDto
    {
        var method = typeof(SyncService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(info =>
                info.Name == "MarkOutboxAcknowledgedForCleanEntitiesAsync" &&
                info.IsGenericMethodDefinition &&
                info.GetGenericArguments().Length == 2);
        var generic = method.MakeGenericMethod(typeof(TLocal), typeof(TDto));
        var task = (Task<int>)generic.Invoke(sync, [serverEntities, CancellationToken.None])!;
        return await task;
    }

    private static Task InvokeApplyAcceptedRevisionsAsync(
        SyncService sync,
        IReadOnlyCollection<SyncAcceptedRevisionDto> acceptedRevisions)
    {
        var method = typeof(SyncService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(info =>
                info.Name == "ApplyAcceptedRevisionsAsync" &&
                !info.IsGenericMethodDefinition &&
                info.GetParameters().Length == 2);
        return (Task)method.Invoke(sync, [acceptedRevisions, CancellationToken.None])!;
    }

    private static Task InvokeMarkOutboxAcknowledgedAsync(
        SyncService sync,
        SyncPushRequest request,
        IReadOnlyCollection<SyncAcceptedRevisionDto> acceptedRevisions)
    {
        var method = typeof(SyncService).GetMethod(
            "MarkOutboxAcknowledgedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SyncService), "MarkOutboxAcknowledgedAsync");
        return (Task)method.Invoke(sync, [request, acceptedRevisions, CancellationToken.None])!;
    }

    private static Task InvokeApplyPullAsync(SyncService sync, SyncPullResponse pull)
    {
        var method = typeof(SyncService).GetMethod("ApplyPullAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SyncService), "ApplyPullAsync");
        return (Task)method.Invoke(
            sync,
            [
                pull,
                0L,
                CancellationToken.None,
                false
            ])!;
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetSession(
            "test-token",
            new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = "outbox-admin",
                Role = "Admin",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            },
            DateTime.UtcNow.AddDays(1));
        return session;
    }

    private static SessionState CreateOfficeSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"{officeCode.ToLowerInvariant()}-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        });
        return session;
    }

    private static SyncService CreateSyncService(LocalDbContext db, SessionState session)
        => CreateSyncService(db, session, handler: null);

    private static SyncService CreateSyncService(LocalDbContext db, SessionState session, HttpMessageHandler? handler)
    {
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rental = new RentalStateService(db, local);
        var diagnostics = new SyncDiagnosticsService(session);
        var httpClient = handler is null
            ? new HttpClient()
            : new HttpClient(handler);
        httpClient.BaseAddress = new Uri("http://localhost/");
        var api = new ErpApiClient(httpClient, session);
        return new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
    }

    private sealed class ForbiddenPushThenEmptyPullHandler : HttpMessageHandler
    {
        private readonly string _message;

        public ForbiddenPushThenEmptyPullHandler(string message)
        {
            _message = message;
        }

        public int PushCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/sync/push")
            {
                PushCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = JsonContent.Create(new { message = _message })
                });
            }

            if (path == "/sync/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPullResponse())
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
