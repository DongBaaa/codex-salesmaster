using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

public sealed class TenantProvisioningService
{
    private readonly AppDbContext _centralDbContext;
    private readonly ITenantDatabaseConnectionResolver _connectionResolver;
    private readonly RevisionClock _revisionClock;
    private readonly ILogger<TenantProvisioningService> _logger;

    public TenantProvisioningService(
        AppDbContext centralDbContext,
        ITenantDatabaseConnectionResolver connectionResolver,
        RevisionClock revisionClock,
        ILogger<TenantProvisioningService> logger)
    {
        _centralDbContext = centralDbContext;
        _connectionResolver = connectionResolver;
        _revisionClock = revisionClock;
        _logger = logger;
    }

    public async Task<TenantProvisioningResultDto> ProvisionIndependentTenantAsync(
        ProvisionIndependentTenantRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TenantScopeCatalog.TryNormalizeCustomTenantCode(request.TenantCode, out var tenantCode) ||
            TenantScopeCatalog.AllTenants.Contains(tenantCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "신규 업체 코드는 ORG_로 시작하는 5~40자의 영문 대문자, 숫자, 밑줄만 사용할 수 있으며 기본 업체 코드는 사용할 수 없습니다.");
        }

        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            throw new InvalidOperationException("신규 업체 표시 이름을 입력하세요.");

        var alreadyExists = await _centralDbContext.TenantDefinitions.IgnoreQueryFilters()
                                .AnyAsync(current => current.TenantCode == tenantCode, cancellationToken) ||
                            await _centralDbContext.TenantOfficeDefinitions.IgnoreQueryFilters()
                                .AnyAsync(current => current.OfficeCode == tenantCode, cancellationToken);
        if (alreadyExists)
            throw new InvalidOperationException("같은 업체 코드 또는 지점 코드가 이미 존재합니다.");

        var connectionInfo = _connectionResolver.ResolveBusinessTenant(tenantCode);
        await DbInitializer.ProvisionDedicatedTenantDatabaseAsync(
            connectionInfo,
            tenantCode,
            displayName,
            request.Description ?? string.Empty,
            _revisionClock,
            _logger,
            cancellationToken);

        var nowUtc = DateTime.UtcNow;
        var tenant = new TenantDefinition
        {
            TenantCode = tenantCode,
            DisplayName = displayName,
            StorageMode = TenantScopeCatalog.StorageDedicatedDatabase,
            Description = (request.Description ?? string.Empty).Trim(),
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
        var headOffice = new TenantOfficeDefinition
        {
            TenantCode = tenantCode,
            OfficeCode = tenantCode,
            DisplayName = displayName,
            IsHeadOffice = true,
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };

        await using var transaction = await _centralDbContext.Database.BeginTransactionAsync(cancellationToken);
        _centralDbContext.TenantDefinitions.Add(tenant);
        _centralDbContext.TenantOfficeDefinitions.Add(headOffice);
        await _centralDbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new TenantProvisioningResultDto
        {
            Tenant = tenant.ToDto(),
            HeadOffice = headOffice.ToDto(),
            BusinessDatabaseName = TenantScopeCatalog.GetPhysicalDatabaseName(tenantCode)
        };
    }
}
