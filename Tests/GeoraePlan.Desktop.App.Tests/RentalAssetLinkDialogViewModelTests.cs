using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetLinkDialogViewModelTests
{
    [Fact]
    public void Constructor_DefaultsToCurrentOfficeAssetScope()
    {
        var vm = new RentalAssetLinkDialogViewModel(
            rental: null!,
            session: CreateAdminSession(),
            currentBillingProfileId: null,
            currentCustomerId: null,
            currentCustomerName: "테스트 거래처",
            currentOfficeCode: OfficeCodeCatalog.Usenet,
            defaultInstallLocation: "본점");

        Assert.False(vm.IncludeOtherOfficeAssets);
        Assert.Equal("현재 담당지점 자산만 표시 중입니다.", InvokePrivateInstance<string>(vm, "BuildScopeStatusSuffix"));
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

    private static T InvokePrivateInstance<T>(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<T>(method!.Invoke(target, args));
    }
}
