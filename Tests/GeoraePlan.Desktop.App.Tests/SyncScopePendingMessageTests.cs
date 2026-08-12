using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncScopePendingMessageTests
{
    [Fact]
    public async Task DirtyRentalSyncSelection_ScopeAdminUsenet_DoesNotSelectItworldEntities()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-sync-scope-admin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profile = new LocalRentalBillingProfile
            {
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                ProfileKey = $"ITWORLD-SYNC-SCOPE-{Guid.NewGuid():N}",
                CustomerName = "ITWORLD sync scope customer",
                IsDirty = true
            };
            var asset = CreateDirtyRentalAsset(
                OfficeCodeCatalog.Itworld,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                "ITWORLD-SYNC-SCOPE");
            asset.BillingProfileId = profile.Id;
            var assignmentHistory = new LocalRentalAssetAssignmentHistory
            {
                AssetId = asset.Id,
                BillingProfileId = profile.Id,
                TenantCode = TenantScopeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                CustomerName = profile.CustomerName,
                IsDirty = true
            };
            var billingLog = new LocalRentalBillingLog
            {
                BillingProfileId = profile.Id,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                BillingYearMonth = "2026-07",
                ScheduledDate = new DateOnly(2026, 7, 25),
                IsDirty = true
            };
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(asset);
            db.RentalAssetAssignmentHistories.Add(assignmentHistory);
            db.RentalBillingLogs.Add(billingLog);
            await db.SaveChangesAsync();

            var usenetScopeAdmin = CreateAdminSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            Assert.True(usenetScopeAdmin.HasGlobalDataScope);
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                usenetScopeAdmin);

            Assert.Empty(await service.GetDirtyRentalBillingProfilesForSyncAsync(usenetScopeAdmin));
            Assert.Empty(await service.GetDirtyRentalAssetsForSyncAsync(usenetScopeAdmin));
            Assert.Empty(await service.GetDirtyRentalAssetAssignmentHistoriesForSyncAsync(usenetScopeAdmin));
            Assert.Empty(await service.GetDirtyRentalBillingLogsForSyncAsync(usenetScopeAdmin));

            Assert.Single(await service.GetDirtyRentalBillingProfilesForOutboundSyncAsync(usenetScopeAdmin));
            Assert.Single(await service.GetDirtyRentalAssetsForOutboundSyncAsync(usenetScopeAdmin));
            Assert.Single(await service.GetDirtyRentalAssetAssignmentHistoriesForOutboundSyncAsync(usenetScopeAdmin));
            Assert.Single(await service.GetDirtyRentalBillingLogsForOutboundSyncAsync(usenetScopeAdmin));

            var usenetTenantAdmin = CreateAdminSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeTenantAll);
            Assert.False(usenetTenantAdmin.HasGlobalDataScope);
            Assert.Empty(await service.GetDirtyRentalBillingProfilesForOutboundSyncAsync(usenetTenantAdmin));
            Assert.Empty(await service.GetDirtyRentalAssetsForOutboundSyncAsync(usenetTenantAdmin));
            Assert.Empty(await service.GetDirtyRentalAssetAssignmentHistoriesForOutboundSyncAsync(usenetTenantAdmin));
            Assert.Empty(await service.GetDirtyRentalBillingLogsForOutboundSyncAsync(usenetTenantAdmin));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PendingSyncWaitingMessage_UsenetLogin_DoesNotReportItworldRentalDirty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-pending-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.RentalAssets.AddRange(
                CreateDirtyRentalAsset(
                    OfficeCodeCatalog.Usenet,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    "USENET-DIRTY"),
                CreateDirtyRentalAsset(
                    OfficeCodeCatalog.Itworld,
                    TenantScopeCatalog.Itworld,
                    OfficeCodeCatalog.Itworld,
                    OfficeCodeCatalog.Usenet,
                    "ITWORLD-MIXED-DIRTY"));
            await db.SaveChangesAsync();

            var usenetSession = CreateAdminSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var itworldSession = CreateAdminSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), usenetSession);

            Assert.Equal(2, await service.CountDirtyAsync());
            Assert.Equal(1, await service.CountDirtyAsync(usenetSession));
            Assert.Equal(1, await service.CountDirtyAsync(itworldSession));

            var usenetDirtyAssets = await service.GetDirtyRentalAssetsForSyncAsync(usenetSession);
            var itworldDirtyAssets = await service.GetDirtyRentalAssetsForSyncAsync(itworldSession);
            Assert.Single(usenetDirtyAssets);
            Assert.Equal("USENET-DIRTY", usenetDirtyAssets[0].MachineNumber);
            Assert.Single(itworldDirtyAssets);
            Assert.Equal("ITWORLD-MIXED-DIRTY", itworldDirtyAssets[0].MachineNumber);

            var usenetMessage = await service.GetPendingSyncWaitingMessageAsync(usenetSession, "status:");
            Assert.NotNull(usenetMessage);
            Assert.Contains("유즈넷", usenetMessage);
            Assert.DoesNotContain("ITWORLD", usenetMessage);

            var itworldMessage = await service.GetPendingSyncWaitingMessageAsync(itworldSession, "status:");
            Assert.NotNull(itworldMessage);
            Assert.Contains("ITWORLD", itworldMessage);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void StartupMaintenanceChange_DoesNotCreateNewDirtyRows()
    {
        var method = typeof(LocalDbInitializer).GetMethod(
            "MarkStartupMaintenanceChange",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var cleanAsset = CreateDirtyRentalAsset(
            OfficeCodeCatalog.Itworld,
            TenantScopeCatalog.Itworld,
            OfficeCodeCatalog.Itworld,
            OfficeCodeCatalog.Itworld,
            "ITWORLD-CLEAN");
        cleanAsset.IsDirty = false;

        var dirtyAsset = CreateDirtyRentalAsset(
            OfficeCodeCatalog.Itworld,
            TenantScopeCatalog.Itworld,
            OfficeCodeCatalog.Itworld,
            OfficeCodeCatalog.Itworld,
            "ITWORLD-DIRTY");
        dirtyAsset.IsDirty = true;

        var updatedAtUtc = DateTime.UtcNow.AddMinutes(1);
        method!.Invoke(null, [cleanAsset, updatedAtUtc]);
        method.Invoke(null, [dirtyAsset, updatedAtUtc]);

        Assert.Equal(updatedAtUtc, cleanAsset.UpdatedAtUtc);
        Assert.False(cleanAsset.IsDirty);
        Assert.Equal(updatedAtUtc, dirtyAsset.UpdatedAtUtc);
        Assert.True(dirtyAsset.IsDirty);
    }

    [Fact]
    public async Task CountDirtyAsync_OfficeUserWithoutSettingsPermission_IgnoresSharedSettingsDirty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-shared-dirty-viewer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.Units.Add(new LocalUnit
            {
                Id = Guid.NewGuid(),
                Name = "감사 공용 단위",
                IsDirty = true
            });
            await db.SaveChangesAsync();

            var viewerSession = CreateOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), viewerSession);

            Assert.Equal(1, await service.CountDirtyAsync());
            Assert.Equal(0, await service.CountDirtyAsync(viewerSession));
            Assert.Null(await service.GetPendingSyncWaitingMessageAsync(viewerSession, "status:"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PendingSyncWaitingMessage_SettingsEditor_ReportsSharedSettingsDirty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-shared-dirty-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.Units.Add(new LocalUnit
            {
                Id = Guid.NewGuid(),
                Name = "감사 공용 단위",
                IsDirty = true
            });
            await db.SaveChangesAsync();

            var settingsEditorSession = CreateOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.SettingsEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), settingsEditorSession);

            Assert.Equal(1, await service.CountDirtyAsync(settingsEditorSession));

            var message = await service.GetPendingSyncWaitingMessageAsync(settingsEditorSession, "status:");

            Assert.NotNull(message);
            Assert.Contains("공용", message);
            Assert.Contains("단위 변경", message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalRentalAsset CreateDirtyRentalAsset(
        string officeCode,
        string tenantCode,
        string managementCompanyCode,
        string responsibleOfficeCode,
        string serialNumber)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            AssetKey = $"TEST:{serialNumber}",
            ManagementCompanyCode = managementCompanyCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            ManagementNumber = serialNumber,
            MachineNumber = serialNumber,
            ItemName = "Rental Printer",
            CustomerName = "Customer",
            CurrentCustomerName = "Customer",
            AssetStatus = "Active",
            BillingEligibilityStatus = "Pending",
            IsDirty = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static SessionState CreateAdminSession(
        string tenantCode,
        string officeCode,
        string scopeType = TenantScopeCatalog.ScopeAdmin)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = $"{officeCode.ToLowerInvariant()}-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = scopeType
        });
        return session;
    }

    private static SessionState CreateOfficeSession(string tenantCode, string officeCode, params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = $"{officeCode.ToLowerInvariant()}-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }
}
