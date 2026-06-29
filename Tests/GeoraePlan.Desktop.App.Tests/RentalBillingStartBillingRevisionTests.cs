using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingStartBillingRevisionTests
{
    [Fact]
    public void RentalBillingViewModel_StartBillingSavesUnsavedProfileBeforeCreatingInvoice()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "\uAC70\uB798\uD50C\uB79C.Desktop.App",
            "ViewModels",
            "RentalBillingViewModel.cs"));
        var startBilling = ExtractBlock(
            source,
            "private async Task StartBillingAsync()",
            "private async Task StartAggregateBillingAsync");

        var saveIndex = startBilling.IndexOf("await SaveAsync();", StringComparison.Ordinal);
        var startIndex = startBilling.IndexOf("_rental.StartBillingAsync", StringComparison.Ordinal);

        Assert.Contains("HasUnsavedSelectedRowChanges()", startBilling, StringComparison.Ordinal);
        Assert.True(saveIndex >= 0, "StartBillingAsync must save pending editor changes before creating a billing invoice.");
        Assert.True(startIndex > saveIndex, "StartBillingAsync must save before calling RentalStateService.StartBillingAsync.");
    }

    [Fact]
    public async Task RentalStateService_StartBillingOperationRebasesRecentlyAcknowledgedSamePcRevision()
    {
        PrepareAppRoot("georaeplan-rental-billing-start-revision-rebase");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var acknowledgedAtUtc = DateTime.UtcNow.AddSeconds(-20);
            var serverUpdatedAtUtc = acknowledgedAtUtc.AddSeconds(5);
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "revision rebase customer",
                ItemName = "revision rebase rental",
                BillingType = "bundle",
                Revision = 200,
                IsDirty = false,
                IsDeleted = false,
                CreatedAtUtc = acknowledgedAtUtc.AddMinutes(-5),
                UpdatedAtUtc = serverUpdatedAtUtc
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                MutationId = $"test-device:{nameof(LocalRentalBillingProfile)}:{profileId:N}:ack",
                DeviceId = "test-device",
                EntityName = nameof(LocalRentalBillingProfile),
                EntityId = profileId,
                ExpectedRevision = 100,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                Status = "Acknowledged",
                PreparedAtUtc = acknowledgedAtUtc.AddMinutes(-1),
                SentAtUtc = acknowledgedAtUtc.AddSeconds(-30),
                AcknowledgedAtUtc = acknowledgedAtUtc
            });
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var profile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);

            var result = await InvokeOperationGuardAsync(service, profile, expectedRevision: 100);

            Assert.True(result.Success, result.ConflictMessage);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_StartBillingOperationKeepsRealStaleRevisionConflict()
    {
        PrepareAppRoot("georaeplan-rental-billing-start-real-conflict");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "real conflict customer",
                ItemName = "real conflict rental",
                BillingType = "bundle",
                Revision = 200,
                IsDirty = false,
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var profile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);

            var result = await InvokeOperationGuardAsync(service, profile, expectedRevision: 100);

            Assert.False(result.Success);
            Assert.Contains("100", result.ConflictMessage, StringComparison.Ordinal);
            Assert.Contains("200", result.ConflictMessage, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static async Task<(bool Success, string ConflictMessage)> InvokeOperationGuardAsync(
        RentalStateService service,
        LocalRentalBillingProfile profile,
        long expectedRevision)
    {
        var method = typeof(RentalStateService).GetMethod(
            "TryEnsureRentalBillingProfileOperationAllowedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            service,
            new object?[] { profile, expectedRevision, CancellationToken.None }));
        await task;

        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var success = Assert.IsType<bool>(result.GetType().GetField("Item1")!.GetValue(result));
        var conflictMessage = Assert.IsType<string>(result.GetType().GetField("Item2")!.GetValue(result));
        return (success, conflictMessage);
    }

    private static string ExtractBlock(string source, string startMarker, string endMarker)
    {
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = normalized.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        var end = normalized.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return normalized[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "\uAC70\uB798\uD50C\uB79C.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
