using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.ViewModels;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingSelectionCacheTests
{
    [Fact]
    public void RentalBillingViewModel_StartCandidateAssetsLoad_ReusesCompletedCacheForSameSignature()
    {
        var profileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var customerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var includedAssetId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var candidateAssetId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession())
        {
            LinkAssetsLater = true
        };
        var templateItem = new RentalBillingTemplateEditorItem
        {
            ItemId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            BillingLineMode = "\uBB36\uC74C"
        };
        vm.TemplateItems.Add(templateItem);
        vm.SelectedTemplateItem = templateItem;

        var includedPool = GetPrivateField<List<RentalBillingAssetOption>>(vm, "_includedAssetPool");
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = includedAssetId,
            ItemName = "Included asset",
            IsLinkedToCurrentProfile = true
        });

        var candidatePool = GetPrivateField<List<RentalBillingAssetOption>>(vm, "_candidateAssetPool");
        candidatePool.Add(new RentalBillingAssetOption
        {
            AssetId = candidateAssetId,
            ItemName = "Candidate asset"
        });

        var signature = InvokePrivateInstance<string>(
            vm,
            "BuildCandidateAssetsLoadSignature",
            profileId,
            customerId,
            "Candidate customer",
            OfficeCodeCatalog.Usenet,
            false,
            false);
        InvokePrivateInstance(vm, "StoreCandidateAssetsLoadCache", signature);

        InvokePrivateInstance(
            vm,
            "StartCandidateAssetsLoad",
            profileId,
            customerId,
            "Candidate customer",
            OfficeCodeCatalog.Usenet,
            false,
            false);

        var included = Assert.Single(vm.IncludedAssets);
        Assert.Equal(includedAssetId, included.AssetId);
        var candidate = Assert.Single(vm.CandidateAssets);
        Assert.Equal(candidateAssetId, candidate.AssetId);
        Assert.Null(GetPrivateFieldValue(vm, "_candidateAssetsLoadCts"));
        Assert.Null(GetPrivateFieldValue(vm, "_candidateAssetsLoadTask"));
    }

    [Fact]
    public void RentalBillingViewModel_StartBillingHistoryRowsLoad_ReusesCompletedCacheForSameProfileSignature()
    {
        var profileId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var billingRunId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession());
        var row = new RentalBillingViewRow
        {
            SelectionId = profileId,
            HasPersistedProfile = true,
            Source = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet
            }
        };
        var histories = new List<RentalBillingHistoryRow>
        {
            new()
            {
                BillingProfileId = profileId,
                BillingRunId = billingRunId,
                PeriodLabel = "2026-07",
                ScheduledDate = new DateOnly(2026, 7, 25)
            }
        };

        var signature = InvokePrivateInstance<string>(vm, "BuildBillingHistoryLoadSignature", row);
        InvokePrivateInstance(vm, "StoreBillingHistoryLoadCache", signature, histories);

        InvokePrivateInstance(vm, "StartBillingHistoryRowsLoad", row);

        var history = Assert.Single(vm.BillingHistoryRows);
        Assert.Equal(billingRunId, history.BillingRunId);
        Assert.Single(row.BillingHistoryRows);
        Assert.Null(GetPrivateFieldValue(vm, "_billingHistoryLoadCts"));
    }

    [Fact]
    public async Task RentalBillingViewModel_RefreshContractDateFromSourcesAsync_ReusesCompletedCacheForSameCustomerAssetSignature()
    {
        var customerId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var cachedDate = new DateOnly(2026, 7, 1);
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession())
        {
            EditCustomerId = customerId,
            EditOfficeCode = OfficeCodeCatalog.Usenet
        };

        var signature = InvokePrivateInstance<string>(vm, "BuildContractDateRefreshSignature");
        InvokePrivateInstance(vm, "StoreContractDateCache", signature, cachedDate);

        await InvokePrivateInstanceTaskAsync(
            vm,
            "RefreshContractDateFromSourcesAsync",
            false,
            false,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(cachedDate.ToDateTime(TimeOnly.MinValue), vm.EditContractDate);
        Assert.Equal(cachedDate.ToDateTime(TimeOnly.MinValue), vm.EditBillingStartDate);
    }

    [Fact]
    public void RentalBillingViewModel_CancelPendingLoadMethods_DoNotDisposeActiveTokens()
    {
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession());

        AssertCancellationSourceRemainsUsable(vm, "_candidateAssetsLoadCts", "CancelPendingCandidateAssetsLoad");
        AssertCancellationSourceRemainsUsable(vm, "_contractDateRefreshCts", "CancelPendingContractDateRefresh");
        AssertCancellationSourceRemainsUsable(vm, "_billingHistoryLoadCts", "CancelBillingHistoryLoad");
        AssertCancellationSourceRemainsUsable(vm, "_includedAssetHistoryLoadCts", "CancelIncludedAssetHistoryLoad");
        AssertCancellationSourceRemainsUsable(vm, "_filterReloadCts", "CancelPendingFilterReload");
    }

    [Fact]
    public void RentalBillingViewModel_SelectionLoadSignatures_RespectProfileCustomerAndOfficeBoundaries()
    {
        var profileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var anotherProfileId = Guid.Parse("12121212-1212-1212-1212-121212121212");
        var customerId = Guid.Parse("13131313-1313-1313-1313-131313131313");
        var anotherCustomerId = Guid.Parse("14141414-1414-1414-1414-141414141414");
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession())
        {
            EditCustomerId = customerId,
            EditOfficeCode = OfficeCodeCatalog.Usenet
        };

        var templateItem = new RentalBillingTemplateEditorItem
        {
            ItemId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        };
        var includedAssetId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        templateItem.IncludedAssetIds.Add(includedAssetId);
        vm.TemplateItems.Add(templateItem);
        vm.SelectedTemplateItem = templateItem;

        var candidateSignature = InvokePrivateInstance<string>(
            vm,
            "BuildCandidateAssetsLoadSignature",
            profileId,
            customerId,
            "Boundary customer",
            OfficeCodeCatalog.Usenet,
            false,
            false);
        var anotherProfileCandidateSignature = InvokePrivateInstance<string>(
            vm,
            "BuildCandidateAssetsLoadSignature",
            anotherProfileId,
            customerId,
            "Boundary customer",
            OfficeCodeCatalog.Usenet,
            false,
            false);
        var anotherCustomerCandidateSignature = InvokePrivateInstance<string>(
            vm,
            "BuildCandidateAssetsLoadSignature",
            profileId,
            anotherCustomerId,
            "Boundary customer",
            OfficeCodeCatalog.Usenet,
            false,
            false);
        var anotherOfficeCandidateSignature = InvokePrivateInstance<string>(
            vm,
            "BuildCandidateAssetsLoadSignature",
            profileId,
            customerId,
            "Boundary customer",
            OfficeCodeCatalog.Yeonsu,
            false,
            false);

        Assert.NotEqual(candidateSignature, anotherProfileCandidateSignature);
        Assert.NotEqual(candidateSignature, anotherCustomerCandidateSignature);
        Assert.NotEqual(candidateSignature, anotherOfficeCandidateSignature);

        var contractSignature = InvokePrivateInstance<string>(vm, "BuildContractDateRefreshSignature");
        templateItem.IncludedAssetIds.Add(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));
        var anotherAssetContractSignature = InvokePrivateInstance<string>(vm, "BuildContractDateRefreshSignature");
        templateItem.IncludedAssetIds.Clear();
        templateItem.IncludedAssetIds.Add(includedAssetId);
        vm.EditCustomerId = anotherCustomerId;
        var anotherCustomerContractSignature = InvokePrivateInstance<string>(vm, "BuildContractDateRefreshSignature");
        vm.EditCustomerId = customerId;
        vm.EditOfficeCode = OfficeCodeCatalog.Yeonsu;
        var anotherOfficeContractSignature = InvokePrivateInstance<string>(vm, "BuildContractDateRefreshSignature");

        Assert.NotEqual(contractSignature, anotherAssetContractSignature);
        Assert.NotEqual(contractSignature, anotherCustomerContractSignature);
        Assert.NotEqual(contractSignature, anotherOfficeContractSignature);

        var row = CreateBillingRow(profileId);
        var anotherRow = CreateBillingRow(anotherProfileId);
        var billingHistorySignature = InvokePrivateInstance<string>(vm, "BuildBillingHistoryLoadSignature", row);
        var anotherBillingHistorySignature = InvokePrivateInstance<string>(vm, "BuildBillingHistoryLoadSignature", anotherRow);

        Assert.NotEqual(billingHistorySignature, anotherBillingHistorySignature);
    }

    [Fact]
    public void RentalBillingViewModel_LoadCandidateAssetsAsync_RefreshesBillingAssetCollectionsOnlyOncePerLoad()
    {
        var source = ReadRentalBillingViewModelSource();
        var loadMethod = ExtractSourceBlock(
            source,
            "private async Task LoadCandidateAssetsAsync(",
            "private void CancelPendingSelectionLoads()");

        Assert.Single(Regex.Matches(loadMethod, "RefreshBillingAssetCollections\\(previousSelections\\);").Cast<Match>());
        Assert.Contains("StoreCandidateAssetsLoadCache(", loadMethod, StringComparison.Ordinal);
        Assert.Contains("var completedSignature = BuildCandidateAssetsLoadSignature(", loadMethod, StringComparison.Ordinal);
        Assert.Contains("StoreCandidateAssetsLoadCache(completedSignature);", loadMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void RentalBillingViewModel_ReloadAndDeferredMaintenance_InvalidateSelectionCachesWithoutLegacyOnlyReload()
    {
        var source = ReadRentalBillingViewModelSource();
        var reloadBody = ExtractSourceBlock(
            source,
            "private async Task ReloadCoreAsync(CancellationToken ct)",
            "private bool ShouldPreserveSelectedEditorDuringReload()");
        var maintenanceBody = ExtractSourceBlock(
            source,
            "private async Task RunDeferredInitialMaintenanceAsync()",
            "public async Task LoadAndSelectProfileAsync(Guid profileId)");
        var selectionBody = ExtractSourceBlock(
            source,
            "partial void OnSelectedRowChanged(RentalBillingViewRow? value)",
            "private void RefreshBillingHistoryRows(RentalBillingViewRow? row)");
        var filterRequestBody = ExtractSourceBlock(
            source,
            "private void RequestFilterReload()",
            "private async Task RunDebouncedFilterReloadAsync(");
        var filterRunBody = ExtractSourceBlock(
            source,
            "private async Task RunDebouncedFilterReloadAsync(",
            "private void CancelPendingFilterReload()");

        Assert.Contains("CancelPendingSelectionLoads();", reloadBody, StringComparison.Ordinal);
        Assert.Contains("InvalidateSelectionLoadCaches();", reloadBody, StringComparison.Ordinal);
        Assert.Contains("if (_pendingFilterReload || repairResult is { HasChanges: true })", maintenanceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("var hasMaintenanceChanges = cleanedLegacyAssignments > 0 || repairResult is { HasChanges: true };", maintenanceBody, StringComparison.Ordinal);
        Assert.Contains("CancelPendingSelectionLoads();", selectionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("new CancellationTokenSource()", filterRequestBody, StringComparison.Ordinal);
        Assert.Contains("using var cts = new CancellationTokenSource();", filterRunBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RentalStateService_LegacyColumnProbe_DoesNotDisposeDbContextOwnedConnection()
    {
        var appRoot = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "rental-connection-ownership-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appRoot);

        try
        {
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={Path.Combine(appRoot, "거래플랜-tests.db")}")
                .Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var rental = new RentalStateService(db, local);

            await rental.CleanupLegacyAssignedUsernamesAsync();

            _ = await db.Customers.AsNoTracking().CountAsync();
            Assert.NotEqual(System.Data.ConnectionState.Broken, db.Database.GetDbConnection().State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(appRoot))
                Directory.Delete(appRoot, recursive: true);
        }
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static RentalBillingViewRow CreateBillingRow(Guid profileId)
        => new()
        {
            SelectionId = profileId,
            HasPersistedProfile = true,
            Source = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet
            }
        };

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(target));
    }

    private static object? GetPrivateFieldValue(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target);
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static void InvokePrivateInstance(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, args);
    }

    private static T InvokePrivateInstance<T>(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<T>(method!.Invoke(target, args));
    }

    private static async Task InvokePrivateInstanceTaskAsync(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(target, args);
        var task = Assert.IsAssignableFrom<Task>(result);
        await task;
    }

    private static string ReadRentalBillingViewModelSource()
    {
        var root = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            root,
            "Desktop",
            "\uAC70\uB798\uD50C\uB79C.Desktop.App",
            "ViewModels",
            "RentalBillingViewModel.cs");
        return File.ReadAllText(sourcePath);
    }

    private static string ExtractSourceBlock(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found after start: {endMarker}");
        return source[start..end];
    }

    private static void AssertCancellationSourceRemainsUsable(object target, string fieldName, string cancelMethodName)
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        SetPrivateField(target, fieldName, cts);

        InvokePrivateInstance(target, cancelMethodName);

        var exception = Record.Exception(() =>
        {
            using var registration = token.Register(static () => { });
        });

        Assert.Null(exception);
        cts.Dispose();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Desktop", "\uAC70\uB798\uD50C\uB79C.Desktop.App");
            if (Directory.Exists(candidate))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
