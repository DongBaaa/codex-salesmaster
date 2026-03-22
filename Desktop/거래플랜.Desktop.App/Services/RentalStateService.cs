using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed class RentalStateService
{
    private const string AlertDaysSettingKey = "Rental.AlertDaysBefore";
    private const string BillingWorkbookPathSettingKey = "Rental.ImportBillingWorkbookPath";
    private const string AssetWorkbookPathSettingKey = "Rental.ImportAssetWorkbookPath";

    private readonly LocalDbContext _db;

    public RentalStateService(LocalDbContext db)
    {
        _db = db;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<IReadOnlyList<LocalRentalManagementCompany>> GetManagementCompaniesAsync(CancellationToken ct = default)
        => await _db.RentalManagementCompanies
            .AsNoTracking()
            .OrderBy(company => company.Name)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> GetAssignedUsernamesAsync(CancellationToken ct = default)
    {
        var names = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .Select(profile => profile.AssignedUsername)
            .Concat(_db.RentalAssets.IgnoreQueryFilters().Select(asset => asset.AssignedUsername))
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Distinct()
            .OrderBy(username => username)
            .ToListAsync(ct);

        return names;
    }

    public async Task<string> GetAlertDaysTextAsync(CancellationToken ct = default)
        => await _db.Settings.AsNoTracking()
               .Where(setting => setting.Key == AlertDaysSettingKey)
               .Select(setting => setting.Value)
               .FirstOrDefaultAsync(ct)
           ?? "7,3,1,0";

    public async Task SaveAlertDaysTextAsync(string value, CancellationToken ct = default)
    {
        var normalized = NormalizeAlertDaysText(value);
        await UpsertSettingAsync(AlertDaysSettingKey, normalized, ct);
    }

    public async Task<(string BillingPath, string AssetPath)> GetImportPathsAsync(CancellationToken ct = default)
    {
        var billingPath = await _db.Settings.AsNoTracking()
            .Where(setting => setting.Key == BillingWorkbookPathSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct)
            ?? string.Empty;
        var assetPath = await _db.Settings.AsNoTracking()
            .Where(setting => setting.Key == AssetWorkbookPathSettingKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct)
            ?? string.Empty;
        return (billingPath, assetPath);
    }

    public async Task SaveImportPathsAsync(string billingPath, string assetPath, CancellationToken ct = default)
    {
        await UpsertSettingAsync(BillingWorkbookPathSettingKey, billingPath ?? string.Empty, ct);
        await UpsertSettingAsync(AssetWorkbookPathSettingKey, assetPath ?? string.Empty, ct);
    }

    public async Task<RentalDashboardSummary> GetDashboardSummaryAsync(
        SessionState session,
        DateOnly referenceDate,
        CancellationToken ct = default)
    {
        var alertDays = await GetAlertDayValuesAsync(ct);
        var alertWindow = alertDays.Count == 0 ? 7 : alertDays.Max();

        var offices = await GetOfficeMapAsync(ct);
        var profiles = await ApplyBillingScope(_db.RentalBillingProfiles.AsNoTracking(), session)
            .Where(profile => profile.IsActive)
            .ToListAsync(ct);
        var assets = await ApplyAssetScope(_db.RentalAssets.AsNoTracking(), session)
            .Where(asset => asset.AssetStatus != "폐기")
            .ToListAsync(ct);

        var alertItems = profiles
            .Select(profile => ToAlertItem(profile, offices, referenceDate))
            .Where(item => item is not null)
            .Cast<RentalAlertItem>()
            .Where(item => item.DaysRemaining <= alertWindow)
            .OrderBy(item => item.DaysRemaining)
            .ThenBy(item => item.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .Take(30)
            .ToList();

        var expiringAssets = assets
            .Where(asset => asset.RentalEndDate.HasValue)
            .Select(asset => ToExpiringAssetItem(asset, offices, referenceDate))
            .Where(item => item is not null && item.DaysRemaining <= 30)
            .Cast<RentalExpiringAssetItem>()
            .OrderBy(item => item.DaysRemaining)
            .ThenBy(item => item.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .Take(20)
            .ToList();

        var summary = new RentalDashboardSummary
        {
            DueTodayCount = alertItems.Count(item => item.DaysRemaining == 0),
            UpcomingCount = alertItems.Count(item => item.DaysRemaining is > 0),
            OverdueCount = alertItems.Count(item => item.DaysRemaining < 0),
            ActiveAssetCount = assets.Count,
            ExpiringContractCount = expiringAssets.Count,
            UnassignedCount = CanViewAllRental(session)
                ? profiles.Count(profile => string.IsNullOrWhiteSpace(profile.AssignedUsername)) +
                  assets.Count(asset => string.IsNullOrWhiteSpace(asset.AssignedUsername))
                : 0,
            AlertItems = alertItems,
            ExpiringAssets = expiringAssets,
            AlertPopupMessage = BuildAlertPopupMessage(alertItems, expiringAssets)
        };

        return summary;
    }

    public async Task<IReadOnlyList<RentalBillingViewRow>> GetBillingRowsAsync(
        RentalBillingFilter filter,
        SessionState session,
        CancellationToken ct = default)
    {
        var offices = await GetOfficeMapAsync(ct);
        var assetCounts = await ApplyAssetScope(_db.RentalAssets.AsNoTracking(), session)
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue)
            .GroupBy(asset => asset.BillingProfileId!.Value)
            .Select(group => new { BillingProfileId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.BillingProfileId, group => group.Count, ct);

        var query = ApplyBillingScope(_db.RentalBillingProfiles.AsNoTracking(), session);
        query = ApplyBillingFilter(query, filter, session);
        var profiles = await query
            .OrderBy(profile => profile.CustomerName)
            .ThenBy(profile => profile.ModelName)
            .ToListAsync(ct);

        var alertWindow = (await GetAlertDayValuesAsync(ct)).DefaultIfEmpty(7).Max();
        var rows = profiles.Select(profile =>
        {
            var nextBillingDate = GetNextBillingDate(profile, filter.ReferenceDate);
            var daysRemaining = nextBillingDate.HasValue
                ? nextBillingDate.Value.DayNumber - filter.ReferenceDate.DayNumber
                : (int?)null;
            return new RentalBillingViewRow
            {
                Source = profile,
                ResponsibleOfficeName = ResolveOfficeDisplayName(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode, offices),
                NextBillingDate = nextBillingDate,
                DaysRemaining = daysRemaining,
                DisplayStatus = BuildBillingDisplayStatus(profile, nextBillingDate, daysRemaining),
                SettlementStatus = PaymentFlowConstants.NormalizeSettlementStatus(profile.SettlementStatus),
                CompletionStatus = PaymentFlowConstants.NormalizeCompletionStatus(profile.CompletionStatus),
                SettledAmount = profile.SettledAmount,
                OutstandingAmount = profile.OutstandingAmount,
                RequiresFollowUp = profile.RequiresFollowUp,
                FollowUpNote = profile.FollowUpNote,
                LastSettledDate = profile.LastSettledDate,
                AssetCount = assetCounts.TryGetValue(profile.Id, out var count) ? count : 0
            };
        });

        if (filter.DueOnly)
        {
            rows = rows.Where(row => row.DaysRemaining.HasValue && row.DaysRemaining.Value <= alertWindow);
        }

        return rows
            .OrderBy(row => row.DaysRemaining ?? int.MaxValue)
            .ThenBy(row => row.Source.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<RentalAssetViewRow>> GetAssetRowsAsync(
        RentalAssetFilter filter,
        SessionState session,
        CancellationToken ct = default)
    {
        var offices = await GetOfficeMapAsync(ct);
        var query = ApplyAssetScope(_db.RentalAssets.AsNoTracking(), session);
        query = ApplyAssetFilter(query, filter, session);

        var assets = await query
            .OrderBy(asset => asset.CustomerName)
            .ThenBy(asset => asset.ManagementNumber)
            .ToListAsync(ct);

        return assets
            .Select(asset => new RentalAssetViewRow
            {
                Source = asset,
                ResponsibleOfficeName = ResolveOfficeDisplayName(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, offices),
                DaysRemaining = asset.RentalEndDate.HasValue
                    ? asset.RentalEndDate.Value.DayNumber - filter.ReferenceDate.DayNumber
                    : null
            })
            .OrderBy(row => row.Source.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Source.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<LocalRentalAsset>> GetAssetsForEquipmentDetailAsync(
        LocalRentalAsset anchorAsset,
        SessionState session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(anchorAsset);

        var query = ApplyAssetScope(_db.RentalAssets.AsNoTracking(), session);
        var officeCode = NormalizeOfficeCode(anchorAsset.ResponsibleOfficeCode, anchorAsset.ManagementCompanyCode);
        if (!string.IsNullOrWhiteSpace(officeCode))
        {
            query = query.Where(asset =>
                asset.ResponsibleOfficeCode == officeCode ||
                asset.ManagementCompanyCode == officeCode);
        }

        if (anchorAsset.BillingProfileId.HasValue)
        {
            var billingProfileId = anchorAsset.BillingProfileId.Value;
            query = query.Where(asset => asset.BillingProfileId == billingProfileId);
        }
        else if (anchorAsset.CustomerId.HasValue)
        {
            var customerId = anchorAsset.CustomerId.Value;
            query = query.Where(asset => asset.CustomerId == customerId);
        }
        else
        {
            var customerName = (anchorAsset.CustomerName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(customerName))
                return [];

            query = query.Where(asset => asset.CustomerName == customerName);
        }

        return await query
            .OrderBy(asset => asset.ManagementNumber)
            .ThenBy(asset => asset.ModelName)
            .ToListAsync(ct);
    }

    public async Task<LocalMutationResult> SaveManagementCompanyAsync(
        LocalRentalManagementCompany company,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanEditRentalSettings(session))
            return LocalMutationResult.Denied("권한이 없어 렌탈 기준설정을 저장할 수 없습니다.");

        var code = NormalizeOfficeCode(company.Code, string.Empty);
        var name = (company.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
            return LocalMutationResult.Denied("관리업체 코드를 입력하세요.");
        if (string.IsNullOrWhiteSpace(name))
            return LocalMutationResult.Denied("관리업체 이름을 입력하세요.");

        var duplicate = await _db.RentalManagementCompanies.IgnoreQueryFilters()
            .AnyAsync(current => current.Id != company.Id && current.Code == code, ct);
        if (duplicate)
            return LocalMutationResult.Denied("같은 관리업체 코드가 이미 있습니다.");

        var now = DateTime.UtcNow;
        var existing = await _db.RentalManagementCompanies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == company.Id, ct);
        if (existing is null)
        {
            company.Id = company.Id == Guid.Empty ? Guid.NewGuid() : company.Id;
            company.Code = code;
            company.Name = name;
            company.IsActive = true;
            company.IsDeleted = false;
            company.IsDirty = true;
            company.CreatedAtUtc = now;
            company.UpdatedAtUtc = now;
            _db.RentalManagementCompanies.Add(company);
        }
        else
        {
            existing.Code = code;
            existing.Name = name;
            existing.IsActive = company.IsActive;
            existing.IsSystemDefault = existing.IsSystemDefault || company.IsSystemDefault;
            existing.IsDeleted = false;
            existing.IsDirty = true;
            existing.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(company.Id, "관리업체를 저장했습니다.");
    }

    public async Task<LocalMutationResult> DeleteManagementCompanyAsync(
        Guid companyId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanEditRentalSettings(session))
            return LocalMutationResult.Denied("권한이 없어 렌탈 기준설정을 수정할 수 없습니다.");

        var company = await _db.RentalManagementCompanies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == companyId, ct);
        if (company is null)
            return LocalMutationResult.Missing("관리업체를 찾을 수 없습니다.");
        if (company.IsSystemDefault)
            return LocalMutationResult.Denied("기본 관리업체는 삭제할 수 없습니다.");

        var inUse = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .AnyAsync(profile => profile.ManagementCompanyCode == company.Code, ct)
            || await _db.RentalAssets.IgnoreQueryFilters()
                .AnyAsync(asset => asset.ManagementCompanyCode == company.Code, ct);
        if (inUse)
            return LocalMutationResult.Denied("사용 중인 관리업체는 삭제할 수 없습니다.");

        company.IsDeleted = true;
        company.IsDirty = true;
        company.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(company.Id, "관리업체를 삭제했습니다.");
    }

    public async Task<LocalMutationResult> SaveBillingProfileAsync(
        LocalRentalBillingProfile profile,
        SessionState session,
        CancellationToken ct = default)
    {
        if (profile is null)
            throw new ArgumentNullException(nameof(profile));

        var existing = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profile.Id, ct);
        if (existing is not null && !CanAccessRental(existing.AssignedUsername, existing.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구 데이터를 수정할 수 없습니다.");

        var customerName = (profile.CustomerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(customerName))
            return LocalMutationResult.Denied("거래처명을 입력하세요.");

        var officeCode = await ResolveRentalOfficeCodeAsync(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode, session.OfficeCode, ct);
        if (string.IsNullOrWhiteSpace(officeCode))
            return LocalMutationResult.Denied("담당지점을 선택하세요.");

        profile.CustomerName = customerName;
        profile.RealCustomerName = (profile.RealCustomerName ?? string.Empty).Trim();
        profile.ModelName = (profile.ModelName ?? string.Empty).Trim();
        profile.BillingDay = Math.Clamp(profile.BillingDay <= 0 ? 25 : profile.BillingDay, 1, 31);
        profile.BillingCycleMonths = Math.Max(1, profile.BillingCycleMonths);
        profile.BillingMethod = NormalizeBillingMethod(profile.BillingMethod);
        profile.BillingStatus = PaymentFlowConstants.NormalizeBillingStatus(profile.BillingStatus);
        profile.SettlementStatus = PaymentFlowConstants.NormalizeSettlementStatus(profile.SettlementStatus);
        profile.CompletionStatus = PaymentFlowConstants.NormalizeCompletionStatus(profile.CompletionStatus);
        profile.SettledAmount = Math.Max(0m, profile.SettledAmount);
        profile.OutstandingAmount = Math.Max(0m, profile.OutstandingAmount);
        profile.RequiresFollowUp = profile.RequiresFollowUp || profile.OutstandingAmount > 0m;
        profile.FollowUpNote = (profile.FollowUpNote ?? string.Empty).Trim();
        profile.ResponsibleOfficeCode = officeCode;
        profile.ManagementCompanyCode = officeCode;
        profile.AssignedUsername = NormalizeAssignedUsername(profile.AssignedUsername, session, allowBlankForAdmin: true);
        profile.ProfileKey = string.IsNullOrWhiteSpace(profile.ProfileKey)
            ? BuildProfileKey(profile.ManagementCompanyCode, profile.BusinessNumber, profile.CustomerName, profile.RealCustomerName, profile.ModelName)
            : profile.ProfileKey;
        profile.IsActive = true;
        profile.IsDeleted = false;

        var duplicate = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id != profile.Id && current.ProfileKey == profile.ProfileKey, ct);
        if (duplicate is not null)
            return LocalMutationResult.Denied("같은 청구 프로필이 이미 존재합니다.");

        if (profile.CustomerId is null || profile.CustomerId == Guid.Empty)
            profile.CustomerId = await ResolveCustomerIdAsync(profile.CustomerName, profile.BusinessNumber, ct);

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            profile.Id = profile.Id == Guid.Empty ? Guid.NewGuid() : profile.Id;
            profile.CreatedAtUtc = now;
            profile.UpdatedAtUtc = now;
            profile.IsDirty = true;
            _db.RentalBillingProfiles.Add(profile);
        }
        else
        {
            profile.CreatedAtUtc = existing.CreatedAtUtc;
            profile.UpdatedAtUtc = now;
            profile.IsDirty = true;
            _db.Entry(existing).CurrentValues.SetValues(profile);
        }

        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(profile.Id, "렌탈 청구 프로필을 저장했습니다.");
    }

    public async Task<LocalMutationResult> DeleteBillingProfileAsync(Guid profileId, SessionState session, CancellationToken ct = default)
    {
        var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");
        if (!CanAccessRental(profile.AssignedUsername, profile.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구 데이터를 삭제할 수 없습니다.");

        profile.IsDeleted = true;
        profile.IsDirty = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(profileId, "렌탈 청구 프로필을 삭제했습니다.");
    }

    public async Task<LocalMutationResult> StartBillingAsync(
        Guid billingProfileId,
        SessionState session,
        CancellationToken ct = default)
    {
        var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");
        if (!CanAccessRental(profile.AssignedUsername, profile.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구를 시작할 수 없습니다.");

        profile.BillingStatus = PaymentFlowConstants.BillingStatusInProgress;
        profile.CompletionStatus = PaymentFlowConstants.CompletionPending;
        profile.SettlementStatus = string.IsNullOrWhiteSpace(profile.SettlementStatus)
            ? PaymentFlowConstants.SettlementStatusPending
            : PaymentFlowConstants.NormalizeSettlementStatus(profile.SettlementStatus);
        if (string.Equals(profile.SettlementStatus, PaymentFlowConstants.SettlementStatusUnpaid, StringComparison.OrdinalIgnoreCase))
            profile.SettlementStatus = PaymentFlowConstants.SettlementStatusPending;
        profile.RequiresFollowUp = profile.RequiresFollowUp || profile.OutstandingAmount > 0m;
        profile.IsDirty = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(billingProfileId, "렌탈 청구를 시작했습니다.");
    }

    public async Task<LocalMutationResult> HoldBillingAsync(
        Guid billingProfileId,
        string note,
        SessionState session,
        CancellationToken ct = default)
    {
        var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");
        if (!CanAccessRental(profile.AssignedUsername, profile.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구를 보류할 수 없습니다.");

        profile.BillingStatus = PaymentFlowConstants.BillingStatusOnHold;
        profile.CompletionStatus = PaymentFlowConstants.CompletionPending;
        profile.RequiresFollowUp = true;
        var normalizedNote = (note ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedNote))
            profile.FollowUpNote = normalizedNote;
        profile.IsDirty = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(billingProfileId, "렌탈 청구를 보류했습니다.");
    }

    public async Task<LocalMutationResult> RegisterBillingSettlementAsync(
        Guid billingProfileId,
        DateOnly referenceDate,
        decimal? settledAmount,
        string note,
        SessionState session,
        CancellationToken ct = default)
    {
        var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");
        if (!CanAccessRental(profile.AssignedUsername, profile.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구의 수금을 등록할 수 없습니다.");

        var amount = settledAmount.GetValueOrDefault(profile.MonthlyAmount);
        if (amount < 0m)
            amount = 0m;

        profile.SettledAmount = amount;
        profile.OutstandingAmount = Math.Max(0m, profile.MonthlyAmount - amount);
        profile.SettlementStatus = profile.OutstandingAmount <= 0m
            ? PaymentFlowConstants.SettlementStatusConfirmed
            : PaymentFlowConstants.SettlementStatusPartial;
        profile.LastSettledDate = referenceDate;
        profile.RequiresFollowUp = profile.OutstandingAmount > 0m;
        var normalizedNote = (note ?? string.Empty).Trim();
        profile.FollowUpNote = profile.RequiresFollowUp
            ? normalizedNote
            : string.Empty;
        if (!string.Equals(profile.BillingStatus, PaymentFlowConstants.BillingStatusCompleted, StringComparison.OrdinalIgnoreCase))
        {
            profile.BillingStatus = PaymentFlowConstants.BillingStatusInProgress;
        }

        var scheduledDate = BuildBillingDate(referenceDate.Year, referenceDate.Month, profile.BillingDay);
        var billingYearMonth = $"{scheduledDate.Year:0000}-{scheduledDate.Month:00}";
        var log = await _db.RentalBillingLogs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.BillingProfileId == billingProfileId && current.BillingYearMonth == billingYearMonth, ct);

        var now = DateTime.UtcNow;
        if (log is null)
        {
            log = new LocalRentalBillingLog
            {
                Id = Guid.NewGuid(),
                BillingProfileId = billingProfileId,
                BillingYearMonth = billingYearMonth,
                ScheduledDate = scheduledDate,
                ProcessedDate = referenceDate,
                ProcessedByUsername = session.User?.Username ?? string.Empty,
                Status = profile.SettlementStatus,
                BilledAmount = profile.SettledAmount,
                Note = normalizedNote,
                ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
                AssignedUsername = profile.AssignedUsername,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                IsDirty = true
            };
            _db.RentalBillingLogs.Add(log);
        }
        else
        {
            log.ProcessedDate = referenceDate;
            log.ProcessedByUsername = session.User?.Username ?? string.Empty;
            log.Status = profile.SettlementStatus;
            log.BilledAmount = profile.SettledAmount;
            log.Note = normalizedNote;
            log.ResponsibleOfficeCode = profile.ResponsibleOfficeCode;
            log.AssignedUsername = profile.AssignedUsername;
            log.UpdatedAtUtc = now;
            log.IsDirty = true;
            log.IsDeleted = false;
        }

        profile.IsDirty = true;
        profile.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(billingProfileId, "수금을 등록했습니다.");
    }

    public async Task<LocalMutationResult> SaveAssetAsync(
        LocalRentalAsset asset,
        SessionState session,
        CancellationToken ct = default)
    {
        if (asset is null)
            throw new ArgumentNullException(nameof(asset));

        var existing = await _db.RentalAssets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == asset.Id, ct);
        if (existing is not null && !CanAccessRental(existing.AssignedUsername, existing.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산을 수정할 수 없습니다.");

        if (string.IsNullOrWhiteSpace(asset.ManagementNumber) && string.IsNullOrWhiteSpace(asset.ManagementId))
            return LocalMutationResult.Denied("관리번호 또는 관리ID를 입력하세요.");

        var officeCode = await ResolveRentalOfficeCodeAsync(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, session.OfficeCode, ct);
        if (string.IsNullOrWhiteSpace(officeCode))
            return LocalMutationResult.Denied("담당지점을 선택하세요.");

        asset.ManagementCompanyCode = officeCode;
        asset.ResponsibleOfficeCode = officeCode;
        asset.AssignedUsername = NormalizeAssignedUsername(asset.AssignedUsername, session, allowBlankForAdmin: true);
        asset.ManagementNumber = (asset.ManagementNumber ?? string.Empty).Trim();
        asset.ManagementId = (asset.ManagementId ?? string.Empty).Trim();
        asset.CustomerName = (asset.CustomerName ?? string.Empty).Trim();
        asset.ModelName = (asset.ModelName ?? string.Empty).Trim();
        await EnrichAssetReferencesAsync(asset, ct);
        asset.AssetKey = string.IsNullOrWhiteSpace(asset.AssetKey)
            ? BuildAssetKey(asset.ManagementCompanyCode, asset.ManagementNumber, asset.ManagementId, asset.MachineNumber, asset.CustomerName, asset.ModelName)
            : asset.AssetKey;
        asset.AssetStatus = ResolveAssetStatus(asset.AssetStatus, asset.CurrentLocation, asset.DisposalDate);
        asset.IsDeleted = false;

        var duplicate = await _db.RentalAssets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id != asset.Id && current.AssetKey == asset.AssetKey, ct);
        if (duplicate is not null)
            return LocalMutationResult.Denied("같은 렌탈 자산이 이미 존재합니다.");

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            asset.Id = asset.Id == Guid.Empty ? Guid.NewGuid() : asset.Id;
            asset.CreatedAtUtc = now;
            asset.UpdatedAtUtc = now;
            asset.IsDirty = true;
            _db.RentalAssets.Add(asset);
        }
        else
        {
            asset.CreatedAtUtc = existing.CreatedAtUtc;
            asset.UpdatedAtUtc = now;
            asset.IsDirty = true;
            _db.Entry(existing).CurrentValues.SetValues(asset);
        }

        if (asset.BillingProfileId is null && !string.IsNullOrWhiteSpace(asset.CustomerName))
            asset.BillingProfileId = await FindMatchingBillingProfileIdAsync(asset, ct);

        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(asset.Id, "렌탈 자산을 저장했습니다.");
    }

    public async Task<LocalMutationResult> DeleteAssetAsync(Guid assetId, SessionState session, CancellationToken ct = default)
    {
        var asset = await _db.RentalAssets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == assetId, ct);
        if (asset is null)
            return LocalMutationResult.Missing("렌탈 자산을 찾을 수 없습니다.");
        if (!CanAccessRental(asset.AssignedUsername, asset.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산을 삭제할 수 없습니다.");

        asset.IsDeleted = true;
        asset.IsDirty = true;
        asset.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(assetId, "렌탈 자산을 삭제했습니다.");
    }

    public async Task<LocalMutationResult> MarkBillingCompletedAsync(
        Guid billingProfileId,
        DateOnly referenceDate,
        string status,
        string note,
        SessionState session,
        CancellationToken ct = default)
    {
        var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");
        if (!CanAccessRental(profile.AssignedUsername, profile.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구를 처리할 수 없습니다.");

        if (!IsBillingMonth(profile, referenceDate))
            return LocalMutationResult.Denied("선택한 기준일은 해당 렌탈의 청구월이 아닙니다.");

        var scheduledDate = BuildBillingDate(referenceDate.Year, referenceDate.Month, profile.BillingDay);
        var billingYearMonth = $"{scheduledDate.Year:0000}-{scheduledDate.Month:00}";
        var log = await _db.RentalBillingLogs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.BillingProfileId == billingProfileId && current.BillingYearMonth == billingYearMonth, ct);

        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "완료" : status.Trim();
        var now = DateTime.UtcNow;
        if (log is null)
        {
            log = new LocalRentalBillingLog
            {
                Id = Guid.NewGuid(),
                BillingProfileId = billingProfileId,
                BillingYearMonth = billingYearMonth,
                ScheduledDate = scheduledDate,
                ProcessedDate = referenceDate,
                ProcessedByUsername = session.User?.Username ?? string.Empty,
                Status = normalizedStatus,
                BilledAmount = profile.MonthlyAmount,
                Note = (note ?? string.Empty).Trim(),
                ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
                AssignedUsername = profile.AssignedUsername,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                IsDirty = true
            };
            _db.RentalBillingLogs.Add(log);
        }
        else
        {
            log.ProcessedDate = referenceDate;
            log.ProcessedByUsername = session.User?.Username ?? string.Empty;
            log.Status = normalizedStatus;
            log.BilledAmount = profile.MonthlyAmount;
            log.Note = (note ?? string.Empty).Trim();
            log.ResponsibleOfficeCode = profile.ResponsibleOfficeCode;
            log.AssignedUsername = profile.AssignedUsername;
            log.UpdatedAtUtc = now;
            log.IsDirty = true;
            log.IsDeleted = false;
        }

        profile.LastBilledDate = scheduledDate;
        profile.BillingStatus = PaymentFlowConstants.BillingStatusCompleted;
        profile.CompletionStatus = PaymentFlowConstants.NormalizeCompletionStatus(normalizedStatus);
        profile.SettledAmount = profile.SettledAmount <= 0m && profile.OutstandingAmount <= 0m
            ? profile.MonthlyAmount
            : profile.SettledAmount;
        profile.OutstandingAmount = Math.Max(0m, profile.MonthlyAmount - profile.SettledAmount);
        profile.SettlementStatus = profile.OutstandingAmount <= 0m
            ? PaymentFlowConstants.SettlementStatusConfirmed
            : PaymentFlowConstants.SettlementStatusPartial;
        profile.RequiresFollowUp = profile.OutstandingAmount > 0m;
        if (!profile.RequiresFollowUp)
            profile.FollowUpNote = string.Empty;
        profile.UpdatedAtUtc = now;
        profile.IsDirty = true;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(billingProfileId, "렌탈 청구 처리 이력을 저장했습니다.");
    }

    public async Task<RentalImportResult> ImportBillingWorkbookAsync(
        string path,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanImportRental(session))
            throw new InvalidOperationException("권한이 없어 렌탈 청구 엑셀을 가져올 수 없습니다.");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("렌탈 청구 엑셀 파일을 찾을 수 없습니다.", path);

        await UpsertSettingAsync(BillingWorkbookPathSettingKey, path, ct);
        var result = new RentalImportResult { SourcePath = path };
        var workbook = ReadWorkbook(path);
        var sheetInfos = workbook.Tables
            .Cast<DataTable>()
            .Select(table => new { Table = table, Anchor = ParseSheetAnchorDate(table.TableName, table) })
            .Where(item => item.Table.TableName.Contains("렌탈판매 청구 리스트", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Anchor ?? DateOnly.MinValue)
            .ToList();

        if (sheetInfos.Count == 0)
            throw new InvalidOperationException("렌탈 청구 시트를 찾지 못했습니다.");

        foreach (var sheetInfo in sheetInfos)
        {
            var headerRowIndex = FindHeaderRow(sheetInfo.Table, "청구일", "거래처명");
            if (headerRowIndex < 0)
            {
                result.Messages.Add($"{sheetInfo.Table.TableName}: 헤더를 찾지 못해 건너뜀");
                continue;
            }

            var headerMap = BuildHeaderMap(sheetInfo.Table, headerRowIndex);
            for (var rowIndex = headerRowIndex + 1; rowIndex < sheetInfo.Table.Rows.Count; rowIndex++)
            {
                var row = sheetInfo.Table.Rows[rowIndex];
                var customerName = GetCellString(row, headerMap, "거래처명");
                var modelName = GetCellString(row, headerMap, "모델명");
                if (string.IsNullOrWhiteSpace(customerName) && string.IsNullOrWhiteSpace(modelName))
                    continue;

                try
                {
                    var officeValue = GetCellString(row, headerMap, "담당지점", "관리업체");
                    var officeCode = await ResolveRentalOfficeCodeAsync(officeValue, officeValue, session.OfficeCode, ct);
                    var billingDay = ParseIntValue(GetCellValue(row, headerMap, "청구일")) ?? 25;
                    var billingCycleMonths = ParseIntValue(GetCellValue(row, headerMap, "청구기간[개월수]")) ?? 1;
                    var businessNumber = GetCellString(row, headerMap, "사업자번호");
                    var realCustomerName = GetCellString(row, headerMap, "실거래처명");
                    var anchorDate = sheetInfo.Anchor.HasValue
                        ? BuildBillingDate(sheetInfo.Anchor.Value.Year, sheetInfo.Anchor.Value.Month, billingDay)
                        : (DateOnly?)null;

                    var profileKey = BuildProfileKey(officeCode, businessNumber, customerName, realCustomerName, modelName);
                    var existing = await _db.RentalBillingProfiles.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(current => current.ProfileKey == profileKey, ct);

                    var profile = existing ?? new LocalRentalBillingProfile
                    {
                        Id = Guid.NewGuid(),
                        ProfileKey = profileKey,
                        CreatedAtUtc = DateTime.UtcNow
                    };

                    profile.CustomerName = customerName.Trim();
                    profile.BusinessNumber = businessNumber.Trim();
                    profile.RealCustomerName = realCustomerName.Trim();
                    profile.ModelName = modelName.Trim();
                    profile.ManagementCompanyCode = officeCode;
                    profile.ResponsibleOfficeCode = officeCode;
                    profile.BillingMethod = NormalizeBillingMethod(GetCellString(row, headerMap, "청구방식").Trim());
                    profile.PaymentMethod = GetCellString(row, headerMap, "결제방식").Trim();
                    profile.BillingStatus = string.IsNullOrWhiteSpace(GetCellString(row, headerMap, "청구상태"))
                        ? profile.BillingStatus
                        : GetCellString(row, headerMap, "청구상태").Trim();
                    profile.Email = GetCellString(row, headerMap, "E-Mail").Trim();
                    profile.BillingDay = Math.Clamp(billingDay, 1, 31);
                    profile.BillingCycleMonths = Math.Max(1, billingCycleMonths);
                    profile.MonthlyAmount = ParseDecimalValue(GetCellValue(row, headerMap, "월청구대금"));
                    profile.SubmissionDocuments = GetCellString(row, headerMap, "추가제출서류").Trim();
                    profile.Notes = GetCellString(row, headerMap, "비고").Trim();
                    profile.BillingAnchorDate = anchorDate ?? profile.BillingAnchorDate;
                    if (existing is not null)
                    {
                        profile.SettlementStatus = existing.SettlementStatus;
                        profile.CompletionStatus = existing.CompletionStatus;
                        profile.SettledAmount = existing.SettledAmount;
                        profile.OutstandingAmount = existing.OutstandingAmount;
                        profile.RequiresFollowUp = existing.RequiresFollowUp;
                        profile.FollowUpNote = existing.FollowUpNote;
                        profile.LastSettledDate = existing.LastSettledDate;
                    }
                    else
                    {
                        profile.SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid;
                        profile.CompletionStatus = PaymentFlowConstants.CompletionPending;
                        profile.SettledAmount = 0m;
                        profile.OutstandingAmount = 0m;
                        profile.RequiresFollowUp = false;
                        profile.FollowUpNote = string.Empty;
                        profile.LastSettledDate = null;
                    }
                    profile.AssignedUsername = string.IsNullOrWhiteSpace(existing?.AssignedUsername)
                        ? NormalizeAssignedUsername(existing?.AssignedUsername, session, allowBlankForAdmin: false)
                        : existing!.AssignedUsername;
                    profile.CustomerId = existing?.CustomerId ?? await ResolveCustomerIdAsync(profile.CustomerName, profile.BusinessNumber, ct);
                    profile.IsActive = true;
                    profile.IsDeleted = false;
                    profile.UpdatedAtUtc = DateTime.UtcNow;
                    profile.IsDirty = true;

                    if (existing is null)
                    {
                        _db.RentalBillingProfiles.Add(profile);
                        result.CreatedCount++;
                    }
                    else
                    {
                        _db.Entry(existing).CurrentValues.SetValues(profile);
                        result.UpdatedCount++;
                    }
                }
                catch (Exception ex)
                {
                    result.ErrorCount++;
                    result.Messages.Add($"{sheetInfo.Table.TableName} {rowIndex + 1}행: {ex.Message}");
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<RentalImportResult> ImportAssetWorkbookAsync(
        string path,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanImportRental(session))
            throw new InvalidOperationException("권한이 없어 렌탈 자산 엑셀을 가져올 수 없습니다.");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("렌탈 자산 엑셀 파일을 찾을 수 없습니다.", path);

        await UpsertSettingAsync(AssetWorkbookPathSettingKey, path, ct);
        var result = new RentalImportResult { SourcePath = path };
        var workbook = ReadWorkbook(path);
        var table = workbook.Tables.Cast<DataTable>()
            .FirstOrDefault(current => string.Equals(current.TableName, "렌탈재고관리", StringComparison.OrdinalIgnoreCase));
        if (table is null)
            throw new InvalidOperationException("렌탈재고관리 시트를 찾지 못했습니다.");

        var headerRowIndex = FindHeaderRow(table, "관리번호", "고객명");
        if (headerRowIndex < 0)
            throw new InvalidOperationException("렌탈자산 헤더를 찾지 못했습니다.");

        var headerMap = BuildHeaderMap(table, headerRowIndex);
        for (var rowIndex = headerRowIndex + 1; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var managementNumber = GetCellString(row, headerMap, "관리번호");
            var customerName = GetCellString(row, headerMap, "고객명");
            var modelName = GetCellString(row, headerMap, "모델명");
            if (string.IsNullOrWhiteSpace(managementNumber) && string.IsNullOrWhiteSpace(customerName) && string.IsNullOrWhiteSpace(modelName))
                continue;

            try
            {
                var officeValue = GetCellString(row, headerMap, "담당지점", "관리업체");
                var officeCode = await ResolveRentalOfficeCodeAsync(officeValue, officeValue, session.OfficeCode, ct);
                var managementId = GetCellString(row, headerMap, "관리ID");
                var machineNumber = GetCellString(row, headerMap, "기계번호");
                var assetKey = BuildAssetKey(officeCode, managementNumber, managementId, machineNumber, customerName, modelName);
                var existing = await _db.RentalAssets.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(current => current.AssetKey == assetKey, ct);

                var asset = existing ?? new LocalRentalAsset
                {
                    Id = Guid.NewGuid(),
                    AssetKey = assetKey,
                    CreatedAtUtc = DateTime.UtcNow
                };

                asset.ManagementId = managementId.Trim();
                asset.ManagementNumber = managementNumber.Trim();
                asset.ManagementCompanyCode = officeCode;
                asset.ResponsibleOfficeCode = officeCode;
                asset.CurrentLocation = GetCellString(row, headerMap, "현재위치").Trim();
                asset.ProductCategory = GetCellString(row, headerMap, "상품분류").Trim();
                asset.Manufacturer = GetCellString(row, headerMap, "제조사").Trim();
                asset.ModelName = modelName.Trim();
                asset.MachineNumber = machineNumber.Trim();
                asset.PurchaseVendor = GetCellString(row, headerMap, "매입처").Trim();
                asset.PurchaseDate = ParseDateValue(GetCellValue(row, headerMap, "매입일"));
                asset.DisposalDate = ParseDateValue(GetCellValue(row, headerMap, "폐기일"));
                asset.PurchasePrice = ParseDecimalValue(GetCellValue(row, headerMap, "매입가"));
                asset.SalePrice = ParseDecimalValue(GetCellValue(row, headerMap, "판매가"));
                asset.CustomerName = customerName.Trim();
                asset.InstallLocation = GetCellString(row, headerMap, "설치위치").Trim();
                asset.DepositText = GetCellString(row, headerMap, "보증금").Trim();
                asset.MonthlyFee = ParseDecimalValue(GetCellValue(row, headerMap, "렌탈요금"));
                asset.ContractMonths = ParseIntValue(GetCellValue(row, headerMap, "계약기간")) ?? 0;
                asset.ContractDate = ParseDateValue(GetCellValue(row, headerMap, "계약일"));
                asset.InstallDate = ParseDateValue(GetCellValue(row, headerMap, "설치일"));
                asset.ContractStartDate = ParseDateValue(GetCellValue(row, headerMap, "계약시작"));
                asset.RentalEndDate = ParseDateValue(GetCellValue(row, headerMap, "렌탈만료"));
                asset.FreeSupplyItems = GetCellString(row, headerMap, "무상품목").Trim();
                asset.PaidSupplyItems = GetCellString(row, headerMap, "유상품목").Trim();
                asset.CustomerId = existing?.CustomerId ?? await ResolveCustomerIdAsync(asset.CustomerName, null, ct);
                asset.ItemId = existing?.ItemId;
                await EnrichAssetReferencesAsync(asset, ct);
                asset.BillingProfileId = await FindMatchingBillingProfileIdAsync(asset, ct);
                asset.AssignedUsername = string.IsNullOrWhiteSpace(existing?.AssignedUsername)
                    ? NormalizeAssignedUsername(existing?.AssignedUsername, session, allowBlankForAdmin: false)
                    : existing!.AssignedUsername;
                asset.AssetStatus = ResolveAssetStatus(existing?.AssetStatus, asset.CurrentLocation, asset.DisposalDate);
                asset.IsDeleted = false;
                asset.IsDirty = true;
                asset.UpdatedAtUtc = DateTime.UtcNow;

                if (existing is null)
                {
                    _db.RentalAssets.Add(asset);
                    result.CreatedCount++;
                }
                else
                {
                    _db.Entry(existing).CurrentValues.SetValues(asset);
                    result.UpdatedCount++;
                }
            }
            catch (Exception ex)
            {
                result.ErrorCount++;
                result.Messages.Add($"렌탈재고관리 {rowIndex + 1}행: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync(ct);
        return result;
    }

    private IQueryable<LocalRentalBillingProfile> ApplyBillingFilter(
        IQueryable<LocalRentalBillingProfile> query,
        RentalBillingFilter filter,
        SessionState session)
    {
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var keyword = filter.SearchText.Trim();
            query = query.Where(profile =>
                profile.CustomerName.Contains(keyword) ||
                profile.RealCustomerName.Contains(keyword) ||
                profile.BusinessNumber.Contains(keyword) ||
                profile.ModelName.Contains(keyword) ||
                profile.Notes.Contains(keyword));
        }

        if (!string.IsNullOrWhiteSpace(filter.OfficeCode))
            query = query.Where(profile =>
                profile.ResponsibleOfficeCode == filter.OfficeCode ||
                profile.ManagementCompanyCode == filter.OfficeCode);

        if (CanViewAllRental(session) && !string.IsNullOrWhiteSpace(filter.AssignedUsername))
            query = query.Where(profile => profile.AssignedUsername == filter.AssignedUsername);

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            if (filter.Status == "활성")
                query = query.Where(profile => profile.IsActive);
            else if (filter.Status == "비활성")
                query = query.Where(profile => !profile.IsActive);
            else
                query = query.Where(profile => profile.BillingStatus == filter.Status);
        }

        return query;
    }

    private IQueryable<LocalRentalAsset> ApplyAssetFilter(
        IQueryable<LocalRentalAsset> query,
        RentalAssetFilter filter,
        SessionState session)
    {
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var keyword = filter.SearchText.Trim();
            query = query.Where(asset =>
                asset.ManagementNumber.Contains(keyword) ||
                asset.CustomerName.Contains(keyword) ||
                asset.ModelName.Contains(keyword) ||
                asset.MachineNumber.Contains(keyword) ||
                asset.InstallLocation.Contains(keyword));
        }

        if (!string.IsNullOrWhiteSpace(filter.OfficeCode))
            query = query.Where(asset =>
                asset.ResponsibleOfficeCode == filter.OfficeCode ||
                asset.ManagementCompanyCode == filter.OfficeCode);

        if (CanViewAllRental(session) && !string.IsNullOrWhiteSpace(filter.AssignedUsername))
            query = query.Where(asset => asset.AssignedUsername == filter.AssignedUsername);

        if (!string.IsNullOrWhiteSpace(filter.AssetStatus))
            query = query.Where(asset => asset.AssetStatus == filter.AssetStatus);

        return query;
    }

    private IQueryable<LocalRentalBillingProfile> ApplyBillingScope(
        IQueryable<LocalRentalBillingProfile> query,
        SessionState session)
    {
        if (CanViewAllRental(session))
            return query;

        if (CanViewTenantRental(session))
        {
            var readableOfficeCodes = GetReadableOfficeCodes(session);
            return query.Where(profile => readableOfficeCodes.Contains(profile.ResponsibleOfficeCode));
        }

        var username = NormalizeUsername(session.User?.Username);
        var officeCode = NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet);
        return query.Where(profile =>
            profile.AssignedUsername == username ||
            (profile.AssignedUsername == string.Empty && profile.ResponsibleOfficeCode == officeCode));
    }

    private IQueryable<LocalRentalAsset> ApplyAssetScope(
        IQueryable<LocalRentalAsset> query,
        SessionState session)
    {
        if (CanViewAllRental(session))
            return query;

        if (CanViewTenantRental(session))
        {
            var readableOfficeCodes = GetReadableOfficeCodes(session);
            return query.Where(asset => readableOfficeCodes.Contains(asset.ResponsibleOfficeCode));
        }

        var username = NormalizeUsername(session.User?.Username);
        var officeCode = NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet);
        return query.Where(asset =>
            asset.AssignedUsername == username ||
            (asset.AssignedUsername == string.Empty && asset.ResponsibleOfficeCode == officeCode));
    }

    private bool CanAccessRental(string? assignedUsername, string? officeCode, SessionState session)
    {
        if (CanEditAllRental(session))
            return true;

        var username = NormalizeUsername(session.User?.Username);
        var normalizedAssigned = NormalizeUsername(assignedUsername);
        if (!string.IsNullOrWhiteSpace(normalizedAssigned) && normalizedAssigned == username)
            return true;

        if (CanViewTenantRental(session))
            return GetReadableOfficeCodes(session).Contains(NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet));

        return string.IsNullOrWhiteSpace(normalizedAssigned) &&
               string.Equals(NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet), NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet), StringComparison.OrdinalIgnoreCase);
    }

    private bool CanViewAllRental(SessionState? session)
        => session is not null && session.IsLoggedIn && (
            session.HasGlobalDataScope ||
            session.HasAssignedPermission(AppPermissionNames.RentalViewAll) ||
            session.HasAssignedPermission(AppPermissionNames.RentalEditAll));

    private static bool CanViewTenantRental(SessionState? session)
        => session is not null &&
           session.IsLoggedIn &&
           !session.HasGlobalDataScope &&
           string.Equals(session.ScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase);

    private bool CanEditAllRental(SessionState? session)
        => session is not null && session.IsLoggedIn && (
            session.HasAdministrativePrivileges ||
            session.HasPermission(AppPermissionNames.RentalEditAll));

    private static HashSet<string> GetReadableOfficeCodes(SessionState session)
    {
        if (session.HasGlobalDataScope)
            return OfficeCodeCatalog.All.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(session.ScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
        {
            return TenantScopeCatalog.GetOfficeCodesForTenant(session.TenantCode)
                .Select(code => NormalizeOfficeCode(code, DomainConstants.OfficeUsenet))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet)
        };
    }

    private bool CanEditRentalSettings(SessionState? session)
        => session is not null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.RentalSettingsEdit));

    private bool CanImportRental(SessionState? session)
        => session is not null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.RentalImport));

    private static string NormalizeAlertDaysText(string value)
    {
        var parsed = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) ? day : (int?)null)
            .Where(day => day.HasValue && day.Value >= 0)
            .Select(day => day!.Value)
            .Distinct()
            .OrderByDescending(day => day)
            .ToList();

        if (parsed.Count == 0)
            parsed.AddRange([7, 3, 1, 0]);

        return string.Join(',', parsed);
    }

    private async Task<List<int>> GetAlertDayValuesAsync(CancellationToken ct)
    {
        var text = await GetAlertDaysTextAsync(ct);
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) ? day : -1)
            .Where(day => day >= 0)
            .Distinct()
            .OrderBy(day => day)
            .ToList();
    }

    private async Task UpsertSettingAsync(string key, string value, CancellationToken ct)
    {
        var setting = await _db.Settings.FindAsync([key], ct);
        if (setting is null)
            _db.Settings.Add(new LocalSetting { Key = key, Value = value });
        else
            setting.Value = value;

        await _db.SaveChangesAsync(ct);
    }

    private async Task<Dictionary<string, string>> GetOfficeMapAsync(CancellationToken ct)
    {
        var offices = await _db.Offices.AsNoTracking().ToListAsync(ct);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var office in offices)
        {
            var code = NormalizeOfficeCode(office.Code, string.Empty);
            if (string.IsNullOrWhiteSpace(code))
                continue;

            map[code] = string.IsNullOrWhiteSpace(office.Name)
                ? ResolveDefaultOfficeName(code)
                : office.Name.Trim();
        }

        return map;
    }

    private string ResolveOfficeDisplayName(
        string? responsibleOfficeCode,
        string? legacyManagementCompanyCode,
        IReadOnlyDictionary<string, string> officeMap)
    {
        foreach (var candidate in new[] { responsibleOfficeCode, legacyManagementCompanyCode })
        {
            var normalizedCode = NormalizeOfficeCode(candidate, string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedCode))
                continue;

            if (officeMap.TryGetValue(normalizedCode, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;

            return ResolveDefaultOfficeName(normalizedCode);
        }

        return string.Empty;
    }

    private RentalAlertItem? ToAlertItem(
        LocalRentalBillingProfile profile,
        IReadOnlyDictionary<string, string> officeMap,
        DateOnly referenceDate)
    {
        var nextBillingDate = GetNextBillingDate(profile, referenceDate);
        if (!nextBillingDate.HasValue)
            return null;

        return new RentalAlertItem
        {
            BillingProfileId = profile.Id,
            ResponsibleOfficeName = ResolveOfficeDisplayName(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode, officeMap),
            CustomerName = profile.CustomerName,
            RealCustomerName = profile.RealCustomerName,
            ModelName = profile.ModelName,
            AssignedUsername = profile.AssignedUsername,
            MonthlyAmount = profile.MonthlyAmount,
            NextBillingDate = nextBillingDate.Value,
            DaysRemaining = nextBillingDate.Value.DayNumber - referenceDate.DayNumber,
            Severity = nextBillingDate.Value.DayNumber < referenceDate.DayNumber
                ? "지연"
                : nextBillingDate.Value == referenceDate ? "오늘" : "예정"
        };
    }

    private RentalExpiringAssetItem? ToExpiringAssetItem(
        LocalRentalAsset asset,
        IReadOnlyDictionary<string, string> officeMap,
        DateOnly referenceDate)
    {
        if (!asset.RentalEndDate.HasValue)
            return null;

        return new RentalExpiringAssetItem
        {
            AssetId = asset.Id,
            ManagementNumber = asset.ManagementNumber,
            ResponsibleOfficeName = ResolveOfficeDisplayName(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, officeMap),
            CustomerName = asset.CustomerName,
            ModelName = asset.ModelName,
            InstallLocation = asset.InstallLocation,
            RentalEndDate = asset.RentalEndDate,
            DaysRemaining = asset.RentalEndDate.Value.DayNumber - referenceDate.DayNumber
        };
    }

    private string BuildAlertPopupMessage(
        IReadOnlyList<RentalAlertItem> alertItems,
        IReadOnlyList<RentalExpiringAssetItem> expiringAssets)
    {
        if (alertItems.Count == 0 && expiringAssets.Count == 0)
            return string.Empty;

        var overdue = alertItems.Count(item => item.DaysRemaining < 0);
        var today = alertItems.Count(item => item.DaysRemaining == 0);
        var upcoming = alertItems.Count(item => item.DaysRemaining > 0);
        var expiring = expiringAssets.Count;

        return $"렌탈 알림\n" +
               $"- 지연 청구 {overdue:N0}건\n" +
               $"- 오늘 청구 {today:N0}건\n" +
               $"- 예정 청구 {upcoming:N0}건\n" +
               $"- 30일 내 만료 {expiring:N0}건";
    }

    public DateOnly? GetNextBillingDate(LocalRentalBillingProfile profile, DateOnly referenceDate)
    {
        if (profile is null || !profile.IsActive)
            return null;

        var billingDay = Math.Clamp(profile.BillingDay <= 0 ? 25 : profile.BillingDay, 1, 31);
        var cycleMonths = Math.Max(1, profile.BillingCycleMonths);
        var anchorDate = GetCycleAnchor(profile, referenceDate);
        var anchorMonth = new DateOnly(anchorDate.Year, anchorDate.Month, 1);
        var referenceMonth = new DateOnly(referenceDate.Year, referenceDate.Month, 1);
        var monthsDiff = ((referenceMonth.Year - anchorMonth.Year) * 12) + (referenceMonth.Month - anchorMonth.Month);
        if (monthsDiff < 0)
            monthsDiff = 0;

        var steps = monthsDiff / cycleMonths;
        var candidateMonth = anchorMonth.AddMonths(steps * cycleMonths);
        var candidate = BuildBillingDate(candidateMonth.Year, candidateMonth.Month, billingDay);

        while (candidate > referenceDate && candidateMonth > anchorMonth)
        {
            candidateMonth = candidateMonth.AddMonths(-cycleMonths);
            candidate = BuildBillingDate(candidateMonth.Year, candidateMonth.Month, billingDay);
        }

        if (profile.LastBilledDate.HasValue)
        {
            while (candidate <= profile.LastBilledDate.Value)
            {
                candidateMonth = candidateMonth.AddMonths(cycleMonths);
                candidate = BuildBillingDate(candidateMonth.Year, candidateMonth.Month, billingDay);
            }
        }

        return candidate;
    }

    public bool IsBillingMonth(LocalRentalBillingProfile profile, DateOnly referenceDate)
    {
        var cycleMonths = Math.Max(1, profile.BillingCycleMonths);
        if (cycleMonths == 1)
            return true;

        var anchor = GetCycleAnchor(profile, referenceDate);
        var monthsDiff = ((referenceDate.Year - anchor.Year) * 12) + (referenceDate.Month - anchor.Month);
        if (monthsDiff < 0)
            return false;

        return monthsDiff % cycleMonths == 0;
    }

    private static DateOnly GetCycleAnchor(LocalRentalBillingProfile profile, DateOnly referenceDate)
    {
        return profile.BillingAnchorDate
               ?? profile.ContractStartDate
               ?? profile.ContractDate
               ?? profile.LastBilledDate
               ?? new DateOnly(referenceDate.Year, referenceDate.Month, 1);
    }

    private static DateOnly BuildBillingDate(int year, int month, int billingDay)
    {
        var day = Math.Clamp(billingDay, 1, DateTime.DaysInMonth(year, month));
        return new DateOnly(year, month, day);
    }

    private async Task<string> ResolveRentalOfficeCodeAsync(
        string? officeCode,
        string? officeCodeOrName,
        string? fallbackOfficeCode,
        CancellationToken ct)
    {
        var offices = await _db.Offices.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        foreach (var candidate in new[] { officeCode, officeCodeOrName })
        {
            var resolved = ResolveOfficeCodeCandidate(candidate, offices);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }

        var fallback = ResolveOfficeCodeCandidate(fallbackOfficeCode, offices);
        return string.IsNullOrWhiteSpace(fallback)
            ? DomainConstants.OfficeUsenet
            : fallback;
    }

    private static string ResolveOfficeCodeCandidate(string? value, IReadOnlyCollection<LocalOffice> offices)
    {
        var normalized = NormalizeOfficeToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var exactCode = offices.FirstOrDefault(office =>
            string.Equals(NormalizeOfficeCode(office.Code, string.Empty), normalized, StringComparison.OrdinalIgnoreCase));
        if (exactCode is not null)
            return NormalizeOfficeCode(exactCode.Code, normalized);

        var exactName = offices.FirstOrDefault(office =>
            string.Equals((office.Name ?? string.Empty).Trim(), (value ?? string.Empty).Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (exactName is not null)
            return NormalizeOfficeCode(exactName.Code, normalized);

        var containedName = offices.FirstOrDefault(office =>
            !string.IsNullOrWhiteSpace(office.Name) &&
            (value ?? string.Empty).Contains(office.Name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (containedName is not null)
            return NormalizeOfficeCode(containedName.Code, normalized);

        return normalized;
    }

    private static string NormalizeOfficeToken(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        return OfficeCodeCatalog.NormalizeOfficeCodeLoose(trimmed, null, string.Empty);
    }

    private static string NormalizeBillingMethod(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        if (trimmed.Contains("CMS", StringComparison.OrdinalIgnoreCase))
            return "CMS";
        if (trimmed.Contains("전자", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("세금", StringComparison.OrdinalIgnoreCase))
            return "전자세금계산서";
        if (trimmed.Contains("카드", StringComparison.OrdinalIgnoreCase))
            return "카드";
        if (trimmed.Contains("현금", StringComparison.OrdinalIgnoreCase))
            return "현금";

        return trimmed;
    }

    private static string NormalizeOfficeCode(string? officeCode, string? fallback)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, fallback);

    private static string ResolveDefaultOfficeName(string officeCode)
        => OfficeCodeCatalog.GetOfficeDisplayName(officeCode);

    private static string NormalizeAssignedUsername(string? assignedUsername, SessionState session, bool allowBlankForAdmin)
    {
        var normalized = NormalizeUsername(assignedUsername);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        if (allowBlankForAdmin && session.HasAdministrativePrivileges)
            return string.Empty;

        return NormalizeUsername(session.User?.Username);
    }

    private static string NormalizeUsername(string? username)
        => (username ?? string.Empty).Trim();

    private async Task<Guid?> ResolveCustomerIdAsync(string? customerName, string? businessNumber, CancellationToken ct)
    {
        var normalizedName = (customerName ?? string.Empty).Trim();
        var normalizedBusinessNumber = (businessNumber ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(normalizedBusinessNumber))
        {
            var businessMatch = await _db.Customers.AsNoTracking()
                .Where(customer => customer.BusinessNumber == normalizedBusinessNumber)
                .Select(customer => (Guid?)customer.Id)
                .FirstOrDefaultAsync(ct);
            if (businessMatch.HasValue)
                return businessMatch;
        }

        if (string.IsNullOrWhiteSpace(normalizedName))
            return null;

        return await _db.Customers.AsNoTracking()
            .Where(customer => customer.NameOriginal == normalizedName)
            .Select(customer => (Guid?)customer.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task EnrichAssetReferencesAsync(LocalRentalAsset asset, CancellationToken ct)
    {
        if (asset.CustomerId is null || asset.CustomerId == Guid.Empty)
            asset.CustomerId = await ResolveCustomerIdAsync(asset.CustomerName, null, ct);

        if (asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
        {
            var customer = await _db.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == asset.CustomerId.Value, ct);
            if (customer is not null && string.IsNullOrWhiteSpace(asset.CustomerName))
                asset.CustomerName = customer.NameOriginal.Trim();
        }

        var item = await ResolveItemAsync(asset.ItemId, asset.ModelName, asset.ManagementNumber, asset.MachineNumber, ct);
        if (item is null)
            return;

        asset.ItemId = item.Id;
        if (string.IsNullOrWhiteSpace(asset.ModelName))
            asset.ModelName = item.NameOriginal.Trim();
        if (string.IsNullOrWhiteSpace(asset.ProductCategory))
            asset.ProductCategory = (item.CategoryName ?? string.Empty).Trim();
        if (asset.PurchasePrice <= 0m && item.PurchasePrice > 0m)
            asset.PurchasePrice = item.PurchasePrice;
        if (asset.SalePrice <= 0m && item.SalePrice > 0m)
            asset.SalePrice = item.SalePrice;
        if (string.IsNullOrWhiteSpace(asset.InstallLocation) && !string.IsNullOrWhiteSpace(item.InstallLocation))
            asset.InstallLocation = item.InstallLocation.Trim();
        if (string.IsNullOrWhiteSpace(asset.CurrentLocation) && !string.IsNullOrWhiteSpace(item.StorageLocation))
            asset.CurrentLocation = item.StorageLocation.Trim();
        if (string.IsNullOrWhiteSpace(asset.MachineNumber) && !string.IsNullOrWhiteSpace(item.SerialNumber))
            asset.MachineNumber = item.SerialNumber.Trim();

        if (string.IsNullOrWhiteSpace(asset.PurchaseVendor))
            asset.PurchaseVendor = await ResolveLatestPurchaseVendorNameAsync(item.Id, ct);
    }

    private async Task<LocalItem?> ResolveItemAsync(
        Guid? itemId,
        string? modelName,
        string? managementNumber,
        string? machineNumber,
        CancellationToken ct)
    {
        if (itemId.HasValue && itemId.Value != Guid.Empty)
        {
            var direct = await _db.Items.AsNoTracking().FirstOrDefaultAsync(item => item.Id == itemId.Value, ct);
            if (direct is not null)
                return direct;
        }

        var normalizedMaterialNumber = (managementNumber ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedMaterialNumber))
        {
            var materialMatch = await _db.Items.AsNoTracking()
                .OrderByDescending(item => item.UpdatedAtUtc)
                .FirstOrDefaultAsync(item => item.MaterialNumber == normalizedMaterialNumber, ct);
            if (materialMatch is not null)
                return materialMatch;
        }

        var normalizedMachineNumber = (machineNumber ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedMachineNumber))
        {
            var serialMatch = await _db.Items.AsNoTracking()
                .OrderByDescending(item => item.UpdatedAtUtc)
                .FirstOrDefaultAsync(item => item.SerialNumber == normalizedMachineNumber, ct);
            if (serialMatch is not null)
                return serialMatch;
        }

        var normalizedModelName = (modelName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedModelName))
            return null;

        return await _db.Items.AsNoTracking()
            .OrderByDescending(item => item.LastPurchaseDate)
            .ThenByDescending(item => item.UpdatedAtUtc)
            .FirstOrDefaultAsync(item => item.NameOriginal == normalizedModelName, ct);
    }

    private async Task<string> ResolveLatestPurchaseVendorNameAsync(Guid itemId, CancellationToken ct)
    {
        if (itemId == Guid.Empty)
            return string.Empty;

        var vendorName = await (
                from line in _db.InvoiceLines.AsNoTracking()
                join invoice in _db.Invoices.AsNoTracking() on line.InvoiceId equals invoice.Id
                join customer in _db.Customers.AsNoTracking() on invoice.CustomerId equals customer.Id
                where !line.IsDeleted
                      && !invoice.IsDeleted
                      && invoice.IsLatestVersion
                      && invoice.IsConfirmed
                      && line.ItemId == itemId
                      && (invoice.VoucherType == 거래플랜.Shared.Contracts.VoucherType.Purchase ||
                          invoice.VoucherType == 거래플랜.Shared.Contracts.VoucherType.Procurement)
                orderby invoice.InvoiceDate descending, invoice.UpdatedAtUtc descending
                select customer.NameOriginal)
            .FirstOrDefaultAsync(ct);

        return vendorName?.Trim() ?? string.Empty;
    }

    private async Task<Guid?> FindMatchingBillingProfileIdAsync(LocalRentalAsset asset, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(asset.CustomerName))
            return null;

        var officeCode = NormalizeOfficeCode(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode);
        return await _db.RentalBillingProfiles.AsNoTracking()
            .Where(profile =>
                profile.ResponsibleOfficeCode == officeCode ||
                profile.ManagementCompanyCode == officeCode)
            .Where(profile => profile.CustomerName == asset.CustomerName)
            .Where(profile => string.IsNullOrWhiteSpace(asset.ModelName) || profile.ModelName == asset.ModelName)
            .Select(profile => (Guid?)profile.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static string BuildProfileKey(
        string managementCompanyCode,
        string? businessNumber,
        string? customerName,
        string? realCustomerName,
        string? modelName)
    {
        return string.Join('|',
            NormalizeProfileKeyPart(managementCompanyCode),
            NormalizeProfileKeyPart(businessNumber),
            NormalizeProfileKeyPart(customerName),
            NormalizeProfileKeyPart(realCustomerName),
            NormalizeProfileKeyPart(modelName));
    }

    private static string BuildAssetKey(
        string managementCompanyCode,
        string? managementNumber,
        string? managementId,
        string? machineNumber,
        string? customerName,
        string? modelName)
    {
        var primary = !string.IsNullOrWhiteSpace(managementNumber)
            ? managementNumber
            : !string.IsNullOrWhiteSpace(managementId)
                ? managementId
                : machineNumber;

        return string.Join('|',
            NormalizeProfileKeyPart(managementCompanyCode),
            NormalizeProfileKeyPart(primary),
            NormalizeProfileKeyPart(customerName),
            NormalizeProfileKeyPart(modelName));
    }

    private static string NormalizeProfileKeyPart(string? value)
        => new string((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(ch => !char.IsWhiteSpace(ch) && ch != '[' && ch != ']')
            .ToArray());

    private static string ResolveAssetStatus(string? requestedStatus, string? currentLocation, DateOnly? disposalDate)
    {
        if (disposalDate.HasValue)
            return "폐기";

        var status = (requestedStatus ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(status))
            return status;

        var location = (currentLocation ?? string.Empty).Trim();
        if (location.Contains("회수", StringComparison.OrdinalIgnoreCase))
            return "회수";
        if (location.Contains("대기", StringComparison.OrdinalIgnoreCase))
            return "대기";
        return "임대진행중";
    }

    private static DataSet ReadWorkbook(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        return reader.AsDataSet(new ExcelDataSetConfiguration
        {
            ConfigureDataTable = _ => new ExcelDataTableConfiguration
            {
                UseHeaderRow = false
            }
        });
    }

    private static int FindHeaderRow(DataTable table, params string[] requiredHeaders)
    {
        var maxScan = Math.Min(20, table.Rows.Count);
        for (var rowIndex = 0; rowIndex < maxScan; rowIndex++)
        {
            var rowValues = Enumerable.Range(0, table.Columns.Count)
                .Select(columnIndex => Convert.ToString(table.Rows[rowIndex][columnIndex], CultureInfo.CurrentCulture)?.Trim() ?? string.Empty)
                .ToList();

            if (requiredHeaders.All(header => rowValues.Any(value => string.Equals(value, header, StringComparison.OrdinalIgnoreCase))))
                return rowIndex;
        }

        return -1;
    }

    private static Dictionary<string, int> BuildHeaderMap(DataTable table, int headerRowIndex)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
        {
            var header = Convert.ToString(table.Rows[headerRowIndex][columnIndex], CultureInfo.CurrentCulture)?.Trim();
            if (string.IsNullOrWhiteSpace(header) || map.ContainsKey(header))
                continue;
            map[header] = columnIndex;
        }

        return map;
    }

    private static object? GetCellValue(DataRow row, IReadOnlyDictionary<string, int> headerMap, params string[] headers)
    {
        foreach (var header in headers)
        {
            if (headerMap.TryGetValue(header, out var columnIndex))
                return row[columnIndex];
        }

        return null;
    }

    private static string GetCellString(DataRow row, IReadOnlyDictionary<string, int> headerMap, params string[] headers)
    {
        var value = GetCellValue(row, headerMap, headers);
        return Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
    }

    private static DateOnly? ParseSheetAnchorDate(string sheetName, DataTable table)
    {
        for (var rowIndex = 0; rowIndex < Math.Min(8, table.Rows.Count); rowIndex++)
        {
            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                var date = ParseDateValue(table.Rows[rowIndex][columnIndex]);
                if (date.HasValue)
                    return date;
            }
        }

        var monthToken = new string(sheetName.Where(ch => char.IsDigit(ch)).ToArray());
        if (int.TryParse(monthToken, out var month) && month is >= 1 and <= 12)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            return new DateOnly(today.Year, month, 1);
        }

        return null;
    }

    private static DateOnly? ParseDateValue(object? value)
    {
        if (value is null || value == DBNull.Value)
            return null;
        if (value is DateOnly dateOnly)
            return dateOnly;
        if (value is DateTime dateTime)
            return DateOnly.FromDateTime(dateTime);
        if (value is double oaDate)
        {
            try
            {
                return DateOnly.FromDateTime(DateTime.FromOADate(oaDate));
            }
            catch
            {
                return null;
            }
        }

        var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Replace('.', '-').Replace('/', '-');

        var formats = new[]
        {
            "yyyy-MM-dd",
            "yyyy-M-d",
            "yyyy년 M월 d일",
            "yyyy년 MM월 dd일",
            "yyyyMMdd"
        };

        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(text, format, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed))
                return DateOnly.FromDateTime(parsed);
        }

        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var fallback)
            ? DateOnly.FromDateTime(fallback)
            : null;
    }

    private static int? ParseIntValue(object? value)
    {
        if (value is null || value == DBNull.Value)
            return null;
        if (value is int intValue)
            return intValue;
        if (value is long longValue)
            return (int)longValue;
        if (value is double doubleValue)
            return (int)Math.Round(doubleValue, MidpointRounding.AwayFromZero);

        var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Replace(",", string.Empty).Replace("개월", string.Empty).Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed)
            ? parsed
            : null;
    }

    private static decimal ParseDecimalValue(object? value)
    {
        if (value is null || value == DBNull.Value)
            return 0m;
        if (value is decimal decimalValue)
            return decimalValue;
        if (value is double doubleValue)
            return Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
        if (value is float floatValue)
            return Convert.ToDecimal(floatValue, CultureInfo.InvariantCulture);
        if (value is int intValue)
            return intValue;
        if (value is long longValue)
            return longValue;

        var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) ||
            text.Contains("면제", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("무료", StringComparison.OrdinalIgnoreCase) ||
            text == "-" ||
            text == "무")
        {
            return 0m;
        }

        var digits = new string(text.Where(ch => char.IsDigit(ch) || ch == '.' || ch == '-').ToArray());
        return decimal.TryParse(digits, NumberStyles.Number | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private static string BuildBillingDisplayStatus(LocalRentalBillingProfile profile, DateOnly? nextBillingDate, int? daysRemaining)
    {
        if (!profile.IsActive)
            return "비활성";

        var completionStatus = PaymentFlowConstants.NormalizeCompletionStatus(profile.CompletionStatus);
        if (string.Equals(completionStatus, PaymentFlowConstants.CompletionDone, StringComparison.OrdinalIgnoreCase))
            return "완료";

        var billingStatus = PaymentFlowConstants.NormalizeBillingStatus(profile.BillingStatus);
        if (string.Equals(billingStatus, PaymentFlowConstants.BillingStatusOnHold, StringComparison.OrdinalIgnoreCase))
            return "보류";
        var settlementStatus = PaymentFlowConstants.NormalizeSettlementStatus(profile.SettlementStatus);
        if (string.Equals(settlementStatus, PaymentFlowConstants.SettlementStatusPartial, StringComparison.OrdinalIgnoreCase))
            return "부분수금";
        if (string.Equals(settlementStatus, PaymentFlowConstants.SettlementStatusConfirmed, StringComparison.OrdinalIgnoreCase))
            return "수금완료";
        if (string.Equals(settlementStatus, PaymentFlowConstants.SettlementStatusPending, StringComparison.OrdinalIgnoreCase))
            return "수금대기";
        if (string.Equals(settlementStatus, PaymentFlowConstants.SettlementStatusUnpaid, StringComparison.OrdinalIgnoreCase))
            return "미수";
        if (string.Equals(billingStatus, PaymentFlowConstants.BillingStatusInProgress, StringComparison.OrdinalIgnoreCase))
            return "청구중";

        if (!nextBillingDate.HasValue || !daysRemaining.HasValue)
            return string.IsNullOrWhiteSpace(billingStatus) ? "예정" : billingStatus;
        if (daysRemaining.Value < 0)
            return $"지연 {Math.Abs(daysRemaining.Value)}일";
        if (daysRemaining.Value == 0)
            return "오늘";
        return $"D-{daysRemaining.Value}";
    }
}
