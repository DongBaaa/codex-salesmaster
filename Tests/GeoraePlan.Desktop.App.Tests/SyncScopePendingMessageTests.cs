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

    private static SessionState CreateAdminSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = $"{officeCode.ToLowerInvariant()}-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeAdmin
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
