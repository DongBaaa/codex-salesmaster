using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize(Policy = "AdminOrGod")]
[Route("tenant-settings")]
public sealed class TenantSettingsController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public TenantSettingsController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<TenantConfigurationSnapshotDto>> Get(CancellationToken cancellationToken)
    {
        var snapshot = new TenantConfigurationSnapshotDto
        {
            Tenants = await _dbContext.TenantDefinitions.AsNoTracking()
                .OrderBy(entity => entity.TenantCode)
                .Select(entity => entity.ToDto())
                .ToListAsync(cancellationToken),
            Offices = await _dbContext.TenantOfficeDefinitions.AsNoTracking()
                .OrderBy(entity => entity.TenantCode)
                .ThenByDescending(entity => entity.IsHeadOffice)
                .ThenBy(entity => entity.OfficeCode)
                .Select(entity => entity.ToDto())
                .ToListAsync(cancellationToken),
            SharingPolicies = await _dbContext.DataSharingPolicies.AsNoTracking()
                .OrderBy(entity => entity.TargetTenantCode)
                .ThenBy(entity => entity.TargetOfficeCode)
                .ThenBy(entity => entity.SourceTenantCode)
                .ThenBy(entity => entity.SourceOfficeCode)
                .Select(entity => entity.ToDto())
                .ToListAsync(cancellationToken)
        };

        return Ok(snapshot);
    }

    [HttpPut("tenants/{tenantCode}")]
    public async Task<ActionResult<TenantDefinitionDto>> UpdateTenant(
        string tenantCode,
        [FromBody] UpdateTenantDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(tenantCode);
        var entity = await _dbContext.TenantDefinitions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.TenantCode == normalizedTenantCode, cancellationToken);
        if (entity is null)
            return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, entity, request.ExpectedRevision, nameof(TenantDefinition)) is { } conflict)
            return conflict;

        entity.TenantCode = normalizedTenantCode;
        entity.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? TenantScopeCatalog.GetTenantDisplayName(normalizedTenantCode)
            : request.DisplayName.Trim();
        entity.StorageMode = TenantScopeCatalog.NormalizeStorageModeOrDefault(request.StorageMode, entity.StorageMode);
        entity.Description = request.Description?.Trim() ?? string.Empty;
        entity.IsActive = request.IsActive;
        entity.IsDeleted = !request.IsActive;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("offices/{officeCode}")]
    public async Task<ActionResult<TenantOfficeDefinitionDto>> UpdateOffice(
        string officeCode,
        [FromBody] UpdateTenantOfficeDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode);
        var entity = await _dbContext.TenantOfficeDefinitions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.OfficeCode == normalizedOfficeCode, cancellationToken);
        if (entity is null)
            return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, entity, request.ExpectedRevision, nameof(TenantOfficeDefinition)) is { } conflict)
            return conflict;

        entity.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? OfficeCodeCatalog.GetOfficeDisplayName(normalizedOfficeCode)
            : request.DisplayName.Trim();
        entity.IsHeadOffice = request.IsHeadOffice;
        entity.IsActive = request.IsActive;
        entity.IsDeleted = !request.IsActive;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPost("sharing-policies")]
    public async Task<ActionResult<DataSharingPolicyDto>> CreateSharingPolicy(
        [FromBody] UpsertDataSharingPolicyRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = await NormalizePolicyRequestAsync(request, cancellationToken);
        if (!normalized.Success)
            return BadRequest(normalized.ErrorMessage);

        var duplicate = await _dbContext.DataSharingPolicies.IgnoreQueryFilters().AnyAsync(current =>
            !current.IsDeleted &&
            current.SourceTenantCode == normalized.SourceTenantCode &&
            current.SourceOfficeCode == normalized.SourceOfficeCode &&
            current.TargetTenantCode == normalized.TargetTenantCode &&
            current.TargetOfficeCode == normalized.TargetOfficeCode &&
            current.Id != normalized.Id,
            cancellationToken);
        if (duplicate)
            return Conflict("같은 업체/지점 연동 정책이 이미 존재합니다.");

        var entity = new DataSharingPolicy();
        ApplyPolicy(entity, normalized);
        _dbContext.DataSharingPolicies.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("sharing-policies/{id:guid}")]
    public async Task<ActionResult<DataSharingPolicyDto>> UpdateSharingPolicy(
        Guid id,
        [FromBody] UpsertDataSharingPolicyRequest request,
        CancellationToken cancellationToken)
    {
        var entity = await _dbContext.DataSharingPolicies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, entity, request.ExpectedRevision, nameof(DataSharingPolicy)) is { } conflict)
            return conflict;

        var normalized = await NormalizePolicyRequestAsync(request, cancellationToken, id);
        if (!normalized.Success)
            return BadRequest(normalized.ErrorMessage);

        var duplicate = await _dbContext.DataSharingPolicies.IgnoreQueryFilters().AnyAsync(current =>
            !current.IsDeleted &&
            current.Id != id &&
            current.SourceTenantCode == normalized.SourceTenantCode &&
            current.SourceOfficeCode == normalized.SourceOfficeCode &&
            current.TargetTenantCode == normalized.TargetTenantCode &&
            current.TargetOfficeCode == normalized.TargetOfficeCode,
            cancellationToken);
        if (duplicate)
            return Conflict("같은 업체/지점 연동 정책이 이미 존재합니다.");

        ApplyPolicy(entity, normalized);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("sharing-policies/{id:guid}")]
    public async Task<IActionResult> DeleteSharingPolicy(Guid id, [FromQuery] long? expectedRevision, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.DataSharingPolicies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();
        if (OptimisticConcurrencyGuard.Check(this, entity, expectedRevision, nameof(DataSharingPolicy)) is { } conflict)
            return conflict;

        entity.IsDeleted = true;
        entity.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<(bool Success, string ErrorMessage, Guid Id, string SourceTenantCode, string SourceOfficeCode, string TargetTenantCode, string TargetOfficeCode, bool ShareCustomers, bool ShareItems, bool ShareInvoices, bool SharePayments, bool ShareContracts, bool ShareReports, bool ShareRentals, bool ShareDeliveries, bool AllowTargetWrite, bool IsActive, string Note)> NormalizePolicyRequestAsync(
        UpsertDataSharingPolicyRequest request,
        CancellationToken cancellationToken,
        Guid? existingId = null)
    {
        var sourceOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(request.SourceOfficeCode);
        var targetOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(request.TargetOfficeCode);
        var sourceTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(request.SourceTenantCode, sourceOfficeCode);
        var targetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(request.TargetTenantCode, targetOfficeCode);

        if (string.Equals(sourceTenantCode, targetTenantCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(sourceOfficeCode, targetOfficeCode, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "동일 지점 간 자기 자신 연동 정책은 만들 수 없습니다.", Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, false, false, false, false, false, false, false, false, false, string.Empty);
        }

        var sourceTenant = await _dbContext.TenantDefinitions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.TenantCode == sourceTenantCode, cancellationToken);
        var targetTenant = await _dbContext.TenantDefinitions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.TenantCode == targetTenantCode, cancellationToken);
        if (sourceTenant is null || targetTenant is null)
        {
            return (false, "업체 권역 정의를 먼저 저장하세요.", Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, false, false, false, false, false, false, false, false, false, string.Empty);
        }

        if (!string.Equals(sourceTenantCode, targetTenantCode, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(TenantScopeCatalog.NormalizeStorageModeOrDefault(sourceTenant.StorageMode), TenantScopeCatalog.StorageDedicatedDatabase, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(TenantScopeCatalog.NormalizeStorageModeOrDefault(targetTenant.StorageMode), TenantScopeCatalog.StorageDedicatedDatabase, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, "별도 업무 DB 권역은 현재 다른 업체 권역과 직접 연동할 수 없습니다.", Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, false, false, false, false, false, false, false, false, false, string.Empty);
        }

        var sourceOffice = await _dbContext.TenantOfficeDefinitions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.OfficeCode == sourceOfficeCode, cancellationToken);
        var targetOffice = await _dbContext.TenantOfficeDefinitions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.OfficeCode == targetOfficeCode, cancellationToken);
        if (sourceOffice is null || targetOffice is null)
        {
            return (false, "지점 정의를 먼저 저장하세요.", Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, false, false, false, false, false, false, false, false, false, string.Empty);
        }

        return (
            true,
            string.Empty,
            existingId ?? Guid.Empty,
            sourceTenantCode,
            sourceOfficeCode,
            targetTenantCode,
            targetOfficeCode,
            request.ShareCustomers,
            request.ShareItems,
            request.ShareInvoices,
            request.SharePayments,
            request.ShareContracts,
            request.ShareReports,
            request.ShareRentals,
            request.ShareDeliveries,
            request.AllowTargetWrite,
            request.IsActive,
            request.Note?.Trim() ?? string.Empty);
    }

    private static void ApplyPolicy(
        DataSharingPolicy entity,
        (bool Success, string ErrorMessage, Guid Id, string SourceTenantCode, string SourceOfficeCode, string TargetTenantCode, string TargetOfficeCode, bool ShareCustomers, bool ShareItems, bool ShareInvoices, bool SharePayments, bool ShareContracts, bool ShareReports, bool ShareRentals, bool ShareDeliveries, bool AllowTargetWrite, bool IsActive, string Note) normalized)
    {
        entity.SourceTenantCode = normalized.SourceTenantCode;
        entity.SourceOfficeCode = normalized.SourceOfficeCode;
        entity.TargetTenantCode = normalized.TargetTenantCode;
        entity.TargetOfficeCode = normalized.TargetOfficeCode;
        entity.ShareCustomers = normalized.ShareCustomers;
        entity.ShareItems = normalized.ShareItems;
        entity.ShareInvoices = normalized.ShareInvoices;
        entity.SharePayments = normalized.SharePayments;
        entity.ShareContracts = normalized.ShareContracts;
        entity.ShareReports = normalized.ShareReports;
        entity.ShareRentals = normalized.ShareRentals;
        entity.ShareDeliveries = normalized.ShareDeliveries;
        entity.AllowTargetWrite = normalized.AllowTargetWrite;
        entity.IsActive = normalized.IsActive;
        entity.IsDeleted = !normalized.IsActive;
        entity.Note = normalized.Note;
    }
}
