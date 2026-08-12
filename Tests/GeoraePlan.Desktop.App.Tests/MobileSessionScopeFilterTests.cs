using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobileSessionScopeFilterTests
{
    [Fact]
    public void CanAccessCustomer_TenantWideSession_PrefersSpecificResponsibleOfficeOverSharedOwner()
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeTenantAll);
        var customer = CreateCustomer(
            OfficeCodeCatalog.Usenet,
            OfficeCodeCatalog.Shared);

        Assert.True(
            MobileSessionScopeFilter.CanAccessCustomer(
                snapshot,
                customer));
    }

    [Fact]
    public void CanAccessCustomer_OfficeSession_PrefersSpecificResponsibleOfficeOverSharedOwner()
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly);
        var customer = CreateCustomer(
            OfficeCodeCatalog.Usenet,
            OfficeCodeCatalog.Shared);

        Assert.True(
            MobileSessionScopeFilter.CanAccessCustomer(
                snapshot,
                customer));
    }

    [Fact]
    public void CanAccessCustomer_OtherOfficeSession_CannotUseSharedOwnerToOverrideResponsibleOffice()
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly);
        var customer = CreateCustomer(
            OfficeCodeCatalog.Usenet,
            OfficeCodeCatalog.Shared);

        Assert.False(
            MobileSessionScopeFilter.CanAccessCustomer(
                snapshot,
                customer));
    }

    [Fact]
    public void CanAccessOperationalScope_SharedPrimary_RemainsFailClosedWhenSharedAccessIsDisabled()
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeTenantAll);

        Assert.False(
            MobileSessionScopeFilter.CanAccessOperationalScope(
                snapshot,
                OfficeCodeCatalog.Shared,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                allowSharedOffice: false));
    }

    [Theory]
    [InlineData(TenantScopeCatalog.ScopeOfficeOnly)]
    [InlineData(TenantScopeCatalog.ScopeTenantAll)]
    public void CanAccessCustomer_ExplicitTenantMismatch_FailsClosed(
        string scopeType)
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            scopeType);
        var customer = CreateCustomer(
            OfficeCodeCatalog.Usenet,
            OfficeCodeCatalog.Shared,
            TenantScopeCatalog.Itworld);

        Assert.False(
            MobileSessionScopeFilter.CanAccessCustomer(
                snapshot,
                customer));
    }

    [Fact]
    public void CanAccessInventoryTransfer_LocalWarehouseWithCrossTenantRoute_FailsClosed()
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly);
        var transfer = new InventoryTransferDto
        {
            TenantCode = TenantScopeCatalog.Itworld,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Itworld,
            FromWarehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode =
                OfficeCodeCatalog.ItworldMainWarehouse
        };

        Assert.False(
            MobileSessionScopeFilter.CanAccessInventoryTransfer(
                snapshot,
                transfer));
    }

    [Fact]
    public void CanAccessInventoryTransfer_SameTenantRouteWithLocalEndpoint_IsAccessible()
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly);
        var transfer = new InventoryTransferDto
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            FromWarehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode =
                OfficeCodeCatalog.YeonsuMainWarehouse
        };

        Assert.True(
            MobileSessionScopeFilter.CanAccessInventoryTransfer(
                snapshot,
                transfer));
    }

    [Fact]
    public void CanAccessInventoryTransfer_LegacyWarehouseRouteInSameTenant_IsAccessible()
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly);
        var transfer = new InventoryTransferDto
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = string.Empty,
            TargetOfficeCode = string.Empty,
            FromWarehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode =
                OfficeCodeCatalog.YeonsuMainWarehouse
        };

        Assert.True(
            MobileSessionScopeFilter.CanAccessInventoryTransfer(
                snapshot,
                transfer));
    }

    [Fact]
    public void CanAccessInventoryTransfer_LegacyWarehouseRouteAcrossTenants_FailsClosed()
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly);
        var transfer = new InventoryTransferDto
        {
            TenantCode = TenantScopeCatalog.Itworld,
            SourceOfficeCode = string.Empty,
            TargetOfficeCode = string.Empty,
            FromWarehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode =
                OfficeCodeCatalog.ItworldMainWarehouse
        };

        Assert.False(
            MobileSessionScopeFilter.CanAccessInventoryTransfer(
                snapshot,
                transfer));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-scope")]
    public void IsGlobalAdminScope_AdminWithMissingOrInvalidScope_FailsClosed(
        string scopeType)
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            scopeType);

        Assert.False(
            MobileSessionScopeFilter.IsGlobalAdminScope(
                snapshot));
        Assert.DoesNotContain(
            OfficeCodeCatalog.Itworld,
            MobileSessionScopeFilter.GetReadableOfficeCodes(
                snapshot));
    }

    [Fact]
    public void IsGlobalAdminScope_ExplicitAdminScope_IsGlobal()
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeAdmin);

        Assert.True(
            MobileSessionScopeFilter.IsGlobalAdminScope(
                snapshot));
        Assert.Contains(
            OfficeCodeCatalog.Itworld,
            MobileSessionScopeFilter.GetReadableOfficeCodes(
                snapshot));
    }

    [Fact]
    public void IsGlobalAdminScope_AdminTenantAll_RemainsTenantBound()
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeTenantAll);
        var customer = CreateCustomer(
            OfficeCodeCatalog.Itworld,
            OfficeCodeCatalog.Itworld,
            TenantScopeCatalog.Itworld);

        Assert.False(
            MobileSessionScopeFilter.IsGlobalAdminScope(
                snapshot));
        Assert.False(
            MobileSessionScopeFilter.CanAccessCustomer(
                snapshot,
                customer));
    }

    [Fact]
    public void IsGlobalAdminScope_NonAdminWithAdminScope_FailsClosed()
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeAdmin,
            role: "User");

        Assert.False(
            MobileSessionScopeFilter.IsGlobalAdminScope(
                snapshot));
    }

    [Fact]
    public void CanAccessOperationalScope_SharedPrimary_RequiresMatchingTenant()
    {
        var snapshot = CreateSnapshot(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeTenantAll);

        Assert.True(
            MobileSessionScopeFilter.CanAccessOperationalScope(
                snapshot,
                OfficeCodeCatalog.Shared,
                TenantScopeCatalog.UsenetGroup,
                allowSharedOffice: true));
        Assert.False(
            MobileSessionScopeFilter.CanAccessOperationalScope(
                snapshot,
                OfficeCodeCatalog.Shared,
                TenantScopeCatalog.Itworld,
                allowSharedOffice: true));
    }

    private static SessionSnapshot CreateSnapshot(
        string officeCode,
        string scopeType,
        string role = "Admin")
        => new()
        {
            IsAuthenticated = true,
            Role = role,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode,
            ScopeType = scopeType
        };

    private static CustomerDto CreateCustomer(
        string responsibleOfficeCode,
        string ownerOfficeCode,
        string tenantCode = TenantScopeCatalog.UsenetGroup)
        => new()
        {
            TenantCode = tenantCode,
            OfficeCode = ownerOfficeCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            NameOriginal = "scope-regression-customer"
        };
}
