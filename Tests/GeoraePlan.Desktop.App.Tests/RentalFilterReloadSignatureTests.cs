using System.Reflection;
using Microsoft.Data.Sqlite;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalFilterReloadSignatureTests
{
    [Fact]
    public void RentalBillingViewModel_RequestFilterReloadSkipsSameActiveSignature()
    {
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession());
        var activeSignature = InvokePrivateInstance<string>(vm, "BuildCurrentFilterReloadSignature");
        var activeCts = new CancellationTokenSource();

        vm.IsBusy = true;
        SetPrivateField(vm, "_activeFilterReloadSignature", activeSignature);
        SetPrivateField(vm, "_filterReloadCts", activeCts);

        try
        {
            InvokePrivateInstance(vm, "RequestFilterReload");

            Assert.False(activeCts.IsCancellationRequested);
            Assert.Same(activeCts, GetPrivateField<CancellationTokenSource>(vm, "_filterReloadCts"));
            Assert.Equal(string.Empty, GetPrivateField<string>(vm, "_pendingFilterReloadSignature"));
        }
        finally
        {
            vm.CancelPendingBackgroundWork();
        }
    }

    [Fact]
    public void RentalBillingViewModel_StartCandidateAssetsLoadSkipsSameActiveSignature()
    {
        var profileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var customerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession())
        {
            LinkAssetsLater = true,
            SelectedTemplateItem = new RentalBillingTemplateEditorItem
            {
                ItemId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                BillingLineMode = "묶음"
            }
        };
        vm.TemplateItems.Add(vm.SelectedTemplateItem);
        var activeSignature = InvokePrivateInstance<string>(
            vm,
            "BuildCandidateAssetsLoadSignature",
            profileId,
            customerId,
            "테스트 거래처",
            OfficeCodeCatalog.Usenet,
            false,
            false);
        var activeCts = new CancellationTokenSource();
        var activeTask = new TaskCompletionSource();

        SetPrivateField(vm, "_activeCandidateAssetsLoadSignature", activeSignature);
        SetPrivateField(vm, "_candidateAssetsLoadCts", activeCts);
        SetPrivateField(vm, "_candidateAssetsLoadTask", activeTask.Task);

        try
        {
            InvokePrivateInstance(
                vm,
                "StartCandidateAssetsLoad",
                profileId,
                customerId,
                "테스트 거래처",
                OfficeCodeCatalog.Usenet,
                false,
                false);

            Assert.False(activeCts.IsCancellationRequested);
            Assert.Same(activeCts, GetPrivateField<CancellationTokenSource>(vm, "_candidateAssetsLoadCts"));
            Assert.Same(activeTask.Task, GetPrivateField<Task>(vm, "_candidateAssetsLoadTask"));
        }
        finally
        {
            vm.CancelPendingBackgroundWork();
            activeTask.SetCanceled();
        }
    }

    [Fact]
    public void RentalAssetViewModel_RequestFilterReloadSkipsSameActiveSignature()
    {
        PrepareAppRoot("georaeplan-rental-asset-active-filter-signature");

        try
        {
            using var db = new LocalDbContext();
            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var vm = new RentalAssetViewModel(rental, local, documents: null!, printService: null!, session);
            var activeSignature = InvokePrivateInstance<string>(vm, "BuildCurrentFilterReloadSignature");
            var activeCts = new CancellationTokenSource();

            vm.IsBusy = true;
            SetPrivateField(vm, "_activeFilterReloadSignature", activeSignature);
            SetPrivateField(vm, "_filterReloadCts", activeCts);

            try
            {
                InvokePrivateInstance(vm, "RequestFilterReload");

                Assert.False(activeCts.IsCancellationRequested);
                Assert.Same(activeCts, GetPrivateField<CancellationTokenSource>(vm, "_filterReloadCts"));
                Assert.Equal(string.Empty, GetPrivateField<string>(vm, "_pendingFilterReloadSignature"));
            }
            finally
            {
                vm.CancelPendingBackgroundWork();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(target));
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static T InvokePrivateInstance<T>(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<T>(method!.Invoke(target, args));
    }

    private static void InvokePrivateInstance(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, args);
    }
}
