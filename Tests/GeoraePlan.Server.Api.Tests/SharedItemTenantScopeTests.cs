using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class SharedItemTenantScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SharedItemTenantScopeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public void NormalizeTenantCodeForOfficeOrDefault_PreservesExplicitTenant_ForSharedOffice()
    {
        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            TenantScopeCatalog.Itworld,
            OfficeCodeCatalog.Shared);

        Assert.Equal(TenantScopeCatalog.Itworld, tenantCode);
    }

    [Fact]
    public void NormalizeTenantCodeForOfficeOrDefault_PrefersFallbackTenant_ForSharedOffice()
    {
        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            null,
            OfficeCodeCatalog.Shared,
            fallbackTenantCode: TenantScopeCatalog.Itworld,
            fallbackOfficeCode: OfficeCodeCatalog.Usenet);

        Assert.Equal(TenantScopeCatalog.Itworld, tenantCode);
    }

    [Fact]
    public void NormalizeTenantCodeForOfficeOrDefault_UsesEffectiveCanonicalFallbackOffice_WhenPrimaryOfficeMissing()
    {
        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            TenantScopeCatalog.Itworld,
            null,
            fallbackTenantCode: null,
            fallbackOfficeCode: OfficeCodeCatalog.Yeonsu);

        Assert.Equal(TenantScopeCatalog.UsenetGroup, tenantCode);
    }

    [Fact]
    public void ItemScopeInference_UsesSingleTenantEvidence_ToRepairSharedScope()
    {
        var inference = ItemScopeInference.Analyze(
            OfficeCodeCatalog.Shared,
            TenantScopeCatalog.UsenetGroup,
            warehouseOfficeCodes: [OfficeCodeCatalog.Itworld],
            invoiceOfficeCodes: [OfficeCodeCatalog.Itworld]);

        Assert.Equal(OfficeCodeCatalog.Itworld, inference.DesiredOfficeCode);
        Assert.Equal(TenantScopeCatalog.Itworld, inference.DesiredTenantCode);
        Assert.True(inference.CanAutoResolveTenant);
        Assert.False(inference.HasCrossTenantEvidence);
    }

    [Fact]
    public void ItemScopeInference_LeavesSharedScope_WhenEvidenceSpansMultipleTenants()
    {
        var inference = ItemScopeInference.Analyze(
            OfficeCodeCatalog.Shared,
            TenantScopeCatalog.UsenetGroup,
            warehouseOfficeCodes: [OfficeCodeCatalog.Itworld],
            invoiceOfficeCodes: [OfficeCodeCatalog.Usenet]);

        Assert.Equal(OfficeCodeCatalog.Shared, inference.DesiredOfficeCode);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, inference.DesiredTenantCode);
        Assert.False(inference.CanAutoResolveTenant);
        Assert.True(inference.HasCrossTenantEvidence);
        Assert.Equal(
            [TenantScopeCatalog.UsenetGroup, TenantScopeCatalog.Itworld],
            inference.EvidenceTenantCodes);
    }

    [Fact]
    public void ItemMappings_PreserveItworldTenant_ForSharedOffice()
    {
        var entity = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "공용 품목",
            NameMatchKey = "공용품목"
        };

        var dto = entity.ToDto();

        Assert.Equal(TenantScopeCatalog.Itworld, dto.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Shared, dto.OfficeCode);

        var updated = new Item
        {
            Id = entity.Id,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = entity.NameOriginal,
            NameMatchKey = entity.NameMatchKey
        };

        updated.Apply(new ItemDto
        {
            Id = entity.Id,
            TenantCode = string.Empty,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "공용 품목 수정"
        });

        Assert.Equal(TenantScopeCatalog.Itworld, updated.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Shared, updated.OfficeCode);
    }

    [Fact]
    public async Task OfficeScopeService_AllowsItworldTenantAllUser_ToWriteSharedItemScope()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "itworld_user",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeTenantAll
        };

        await using var dbContext = CreateDbContext(currentUser);
        var service = new OfficeScopeService(currentUser, dbContext);

        Assert.True(service.CanReadOfficeForItems(OfficeCodeCatalog.Shared, TenantScopeCatalog.Itworld));
        Assert.True(service.CanWriteOfficeForItems(OfficeCodeCatalog.Shared, TenantScopeCatalog.Itworld));
        Assert.False(service.CanWriteOfficeForItems(OfficeCodeCatalog.Shared, TenantScopeCatalog.UsenetGroup));
    }

    [Fact]
    public void TenantContainsOffice_DoesNotTreatSharedScope_AsCanonicalTenantOffice()
    {
        Assert.False(TenantScopeCatalog.TenantContainsOffice(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Shared));
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        var dbContext = new AppDbContext(options, currentUser, revisionClock);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = [];

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }
}
