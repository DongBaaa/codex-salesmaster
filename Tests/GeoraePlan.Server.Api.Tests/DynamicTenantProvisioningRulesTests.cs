using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class DynamicTenantProvisioningRulesTests
{
    [Fact]
    public void CustomTenant_UsesSameCodeForHeadOfficeAndDedicatedDatabase()
    {
        Assert.True(TenantScopeCatalog.TryNormalizeTenantCode(" org_newco_01 ", out var tenantCode));
        Assert.Equal("ORG_NEWCO_01", tenantCode);
        Assert.True(OfficeCodeCatalog.TryNormalizeOfficeCode("org_newco_01", out var officeCode));
        Assert.Equal(tenantCode, officeCode);
        Assert.True(TenantScopeCatalog.TenantContainsOffice(tenantCode, officeCode));
        Assert.Equal([tenantCode], TenantScopeCatalog.GetOfficeCodesForTenant(tenantCode));
        Assert.Equal(tenantCode, TenantScopeCatalog.GetDatabaseName(tenantCode));
        Assert.Equal("georaeplan_org_newco_01", TenantScopeCatalog.GetPhysicalDatabaseName(tenantCode));
        Assert.Equal("ORG_NEWCO_01_MAIN", OfficeCodeCatalog.GetMainWarehouseCode(officeCode));
        Assert.Equal(
            OfficeCodeCatalog.GetDefaultCompanyProfileId(officeCode),
            OfficeCodeCatalog.GetDefaultCompanyProfileId("ORG_NEWCO_01"));
    }

    [Fact]
    public void ExistingSubcompany_RemainsInsideUsenetTenantDatabase()
    {
        Assert.Equal(
            TenantScopeCatalog.UsenetGroup,
            TenantScopeCatalog.GetTenantCodeForOffice(OfficeCodeCatalog.Yeonsu));
        Assert.True(TenantScopeCatalog.TenantContainsOffice(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu));
        Assert.Equal(
            "georaeplan_usenet",
            TenantScopeCatalog.GetPhysicalDatabaseName(TenantScopeCatalog.UsenetGroup));
    }

    [Theory]
    [InlineData("ALL")]
    [InlineData("CENTRAL")]
    [InlineData("POSTGRES")]
    [InlineData("NEWCO_MAIN")]
    [InlineData("NEWCO_01")]
    [InlineData("ORG_")]
    [InlineData("ORG_1NEWCO")]
    [InlineData("ORG_NEWCO_")]
    [InlineData("1NEWCO")]
    [InlineData("NEW-CO")]
    public void ReservedOrUnsafeCustomTenantCode_IsRejected(string value)
        => Assert.False(TenantScopeCatalog.TryNormalizeCustomTenantCode(value, out _));

    [Fact]
    public void Resolver_DerivesDedicatedDatabaseForApprovedCustomTenantCodeShape()
    {
        var resolver = new TenantDatabaseConnectionResolver(
            new TenantDatabaseRoutingOptions
            {
                UseSqlite = false,
                DefaultConnectionString =
                    "Host=db-server;Port=5432;Database=georaeplan;Username=app;Password=test"
            },
            new HttpContextAccessor());

        var resolved = resolver.ResolveBusinessTenant("ORG_NEWCO_01");
        var builder = new NpgsqlConnectionStringBuilder(resolved.ConnectionString);

        Assert.Equal("ORG_NEWCO_01", resolved.TenantCode);
        Assert.True(resolved.IsDedicatedBusinessDatabase);
        Assert.Equal("georaeplan_org_newco_01", builder.Database);
        Assert.Equal("db-server", builder.Host);
        Assert.Equal("app", builder.Username);
    }

    [Fact]
    public void Resolver_DoesNotFallbackInvalidTenantToUsenet()
    {
        var resolver = new TenantDatabaseConnectionResolver(
            new TenantDatabaseRoutingOptions
            {
                UseSqlite = false,
                DefaultConnectionString =
                    "Host=db-server;Port=5432;Database=georaeplan;Username=app;Password=test"
            },
            new HttpContextAccessor());

        Assert.Throws<InvalidOperationException>(() => resolver.ResolveBusinessTenant("NEW-CO"));
    }
}
