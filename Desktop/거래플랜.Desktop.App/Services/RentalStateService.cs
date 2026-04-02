using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using ExcelDataReader;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class RentalStateService
{
    public const string AutoCreatedRentalItemMemo = "렌탈 자산/설치현황 자동 동기화 생성";
    private const string AlertDaysSettingKey = "Rental.AlertDaysBefore";
    private const string BillingWorkbookPathSettingKey = "Rental.ImportBillingWorkbookPath";
    private const string AssetWorkbookPathSettingKey = "Rental.ImportAssetWorkbookPath";
    private const string BillingEditorDraftSettingPrefix = "Rental.BillingEditorDraft";
    private const string OnboardingDraftSettingPrefix = "Rental.OnboardingDraft";
    private static readonly TimeZoneInfo KoreaTimeZone = ResolveKoreaTimeZone();
    private static readonly SemaphoreSlim AssetSaveLock = new(1, 1);
    private static readonly JsonSerializerOptions RentalJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly IReadOnlyDictionary<string, string> ImportLocationStatusMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["렌탈"] = "임대진행중",
        ["창고"] = "회수",
        ["판매"] = "판매",
        ["폐기"] = "폐기"
    };
    private static readonly IReadOnlyDictionary<string, string> ImportManagementOfficeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["아이티월드"] = DomainConstants.OfficeItworld,
        ["ITWORLD"] = DomainConstants.OfficeItworld,
        ["유즈넷"] = DomainConstants.OfficeUsenet,
        ["USENET"] = DomainConstants.OfficeUsenet,
        ["연수구"] = DomainConstants.OfficeYeonsu,
        ["YEONSU"] = DomainConstants.OfficeYeonsu
    };
    private static readonly IReadOnlyDictionary<string, string> DefaultAssignedUsernameByOffice = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [DomainConstants.OfficeUsenet] = "usenet",
        [DomainConstants.OfficeItworld] = "itworld",
        [DomainConstants.OfficeYeonsu] = "yeonsu"
    };

    private readonly LocalDbContext _db;
    private readonly LocalStateService? _local;

    public RentalStateService(LocalDbContext db)
        : this(db, null)
    {
    }

    public RentalStateService(LocalDbContext db, LocalStateService? local)
    {
        _db = db;
        _local = local;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<IReadOnlyList<LocalRentalManagementCompany>> GetManagementCompaniesAsync(CancellationToken ct = default)
        => await _db.RentalManagementCompanies
            .AsNoTracking()
            .OrderBy(company => company.Name)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> GetAssignedUsernamesAsync(CancellationToken ct = default)
    {
        var assignments = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .Select(profile => new
            {
                profile.AssignedUsername,
                profile.ResponsibleOfficeCode,
                profile.ManagementCompanyCode
            })
            .Concat(_db.RentalAssets.IgnoreQueryFilters().Select(asset => new
            {
                asset.AssignedUsername,
                asset.ResponsibleOfficeCode,
                asset.ManagementCompanyCode
            }))
            .ToListAsync(ct);

        return assignments
            .Select(entry => ResolveAssignedUsernameForDisplay(entry.AssignedUsername, entry.ResponsibleOfficeCode, entry.ManagementCompanyCode))
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(username => username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<int> RepairRoleBasedAssignedUsernamesAsync(CancellationToken ct = default)
    {
        var changed = 0;
        var now = DateTime.UtcNow;

        foreach (var profile in await _db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync(ct))
        {
            var normalized = ResolveAssignedUsernameForDisplay(profile.AssignedUsername, profile.ResponsibleOfficeCode, profile.ManagementCompanyCode);
            if (string.Equals(profile.AssignedUsername ?? string.Empty, normalized, StringComparison.Ordinal))
                continue;

            profile.AssignedUsername = normalized;
            profile.UpdatedAtUtc = now;
            profile.IsDirty = true;
            changed++;
        }

        foreach (var asset in await _db.RentalAssets.IgnoreQueryFilters().ToListAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(asset.AssignedUsername))
                continue;

            asset.AssignedUsername = string.Empty;
            asset.UpdatedAtUtc = now;
            asset.IsDirty = true;
            changed++;
        }

        foreach (var log in await _db.RentalBillingLogs.IgnoreQueryFilters().ToListAsync(ct))
        {
            var normalized = ResolveAssignedUsernameForDisplay(log.AssignedUsername, log.ResponsibleOfficeCode, null);
            if (string.Equals(log.AssignedUsername ?? string.Empty, normalized, StringComparison.Ordinal))
                continue;

            log.AssignedUsername = normalized;
            log.UpdatedAtUtc = now;
            log.IsDirty = true;
            changed++;
        }

        if (changed > 0)
            await _db.SaveChangesAsync(ct);

        return changed;
    }

    public string GetAssignedUsernameDisplay(string? assignedUsername, string? responsibleOfficeCode, string? managementCompanyCode = null)
        => ResolveAssignedUsernameForDisplay(assignedUsername, responsibleOfficeCode, managementCompanyCode);

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

    public async Task<RentalBillingEditorDraftModel?> GetBillingEditorDraftAsync(SessionState session, CancellationToken ct = default)
        => await GetDraftAsync<RentalBillingEditorDraftModel>(BuildDraftSettingKey(BillingEditorDraftSettingPrefix, session), ct);

    public async Task SaveBillingEditorDraftAsync(RentalBillingEditorDraftModel draft, SessionState session, CancellationToken ct = default)
        => await UpsertSettingAsync(
            BuildDraftSettingKey(BillingEditorDraftSettingPrefix, session),
            JsonSerializer.Serialize(draft, RentalJsonOptions),
            ct);

    public async Task ClearBillingEditorDraftAsync(SessionState session, CancellationToken ct = default)
        => await RemoveSettingAsync(BuildDraftSettingKey(BillingEditorDraftSettingPrefix, session), ct);

    public async Task<RentalCustomerOnboardingDraftModel?> GetOnboardingDraftAsync(SessionState session, CancellationToken ct = default)
        => await GetDraftAsync<RentalCustomerOnboardingDraftModel>(BuildDraftSettingKey(OnboardingDraftSettingPrefix, session), ct);

    public async Task SaveOnboardingDraftAsync(RentalCustomerOnboardingDraftModel draft, SessionState session, CancellationToken ct = default)
        => await UpsertSettingAsync(
            BuildDraftSettingKey(OnboardingDraftSettingPrefix, session),
            JsonSerializer.Serialize(draft, RentalJsonOptions),
            ct);

    public async Task ClearOnboardingDraftAsync(SessionState session, CancellationToken ct = default)
        => await RemoveSettingAsync(BuildDraftSettingKey(OnboardingDraftSettingPrefix, session), ct);

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
        var billingAssets = await ApplyAssetScope(_db.RentalAssets.AsNoTracking(), session)
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue)
            .ToListAsync(ct);
        var assetsByProfile = billingAssets
            .GroupBy(asset => asset.BillingProfileId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var query = ApplyBillingScope(_db.RentalBillingProfiles.AsNoTracking(), session);
        query = ApplyBillingFilter(query, filter, session);
        var profiles = await query
            .OrderBy(profile => profile.CustomerName)
            .ThenBy(profile => profile.ItemName)
            .ToListAsync(ct);
        var customerIds = profiles
            .Where(profile => profile.CustomerId.HasValue && profile.CustomerId.Value != Guid.Empty)
            .Select(profile => profile.CustomerId!.Value)
            .Distinct()
            .ToList();
        var customerNameMap = customerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => customerIds.Contains(customer.Id))
                .ToDictionaryAsync(customer => customer.Id, customer => customer.NameOriginal, ct);

        var previewRuns = profiles
            .Select(profile => GetOrCreateBillingRun(profile, filter.ReferenceDate, persistChanges: false))
            .Where(run => run is not null)
            .Cast<RentalBillingRunModel>()
            .ToDictionary(run => run.RunId, run => run);
        var settledByRun = previewRuns.Count == 0
            ? new Dictionary<Guid, decimal>()
            : (await _db.Transactions.AsNoTracking()
                    .Where(transaction => !transaction.IsDeleted && transaction.LinkedRentalBillingRunId.HasValue && previewRuns.Keys.Contains(transaction.LinkedRentalBillingRunId.Value))
                    .Select(transaction => new
                    {
                        RunId = transaction.LinkedRentalBillingRunId!.Value,
                        transaction.SettlementAmount
                    })
                    .ToListAsync(ct))
                .GroupBy(transaction => transaction.RunId)
                .ToDictionary(group => group.Key, group => group.Sum(transaction => transaction.SettlementAmount));

        var alertWindow = (await GetAlertDayValuesAsync(ct)).DefaultIfEmpty(7).Max();
        var rows = profiles.Select(profile =>
        {
            var customerDisplayName = ResolveBillingProfileCustomerDisplayName(profile, customerNameMap);
            assetsByProfile.TryGetValue(profile.Id, out var profileAssets);
            profileAssets ??= new List<LocalRentalAsset>();
            var templateItems = GetBillingTemplateItems(profile, profileAssets);
            var includedAssetCount = templateItems.SelectMany(item => item.IncludedAssetIds).Distinct().Count();
            var nextBillingDate = GetNextBillingDate(profile, filter.ReferenceDate);
            var documentIssueDate = nextBillingDate.HasValue
                ? RentalBillingScheduleRules.CalculateDocumentIssueDate(nextBillingDate, profile.DocumentIssueMode, profile.DocumentLeadDays)
                : null;
            var alertDate = nextBillingDate.HasValue
                ? RentalBillingScheduleRules.ResolveAlertDate(nextBillingDate.Value, documentIssueDate)
                : (DateOnly?)null;
            var daysRemaining = alertDate.HasValue
                ? alertDate.Value.DayNumber - filter.ReferenceDate.DayNumber
                : nextBillingDate.HasValue
                    ? nextBillingDate.Value.DayNumber - filter.ReferenceDate.DayNumber
                    : (int?)null;
            var currentRun = GetOrCreateBillingRun(profile, filter.ReferenceDate, persistChanges: false);
            var billedAmount = currentRun?.BilledAmount ?? profile.MonthlyAmount;
            var settledAmount = currentRun is not null && settledByRun.TryGetValue(currentRun.RunId, out var runSettledAmount)
                ? runSettledAmount
                : profile.SettledAmount;
            var outstandingAmount = Math.Max(0m, billedAmount - settledAmount);
            var currentRunStatus = currentRun?.Status ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentRunStatus) || string.Equals(currentRunStatus, PaymentFlowConstants.BillingStatusPlanned, StringComparison.OrdinalIgnoreCase))
            {
                currentRunStatus = outstandingAmount <= 0m && billedAmount > 0m
                    ? PaymentFlowConstants.BillingStatusCompleted
                    : settledAmount > 0m
                        ? PaymentFlowConstants.BillingStatusInProgress
                        : PaymentFlowConstants.BillingStatusPlanned;
            }
            var dataIssues = BuildBillingDataIssues(profile, profileAssets, templateItems);
            return new RentalBillingViewRow
            {
                Source = profile,
                CustomerDisplayName = customerDisplayName,
                ResponsibleOfficeName = ResolveOfficeDisplayName(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode, offices),
                AssignedUsernameDisplay = ResolveAssignedUsernameForDisplay(profile.AssignedUsername, profile.ResponsibleOfficeCode, profile.ManagementCompanyCode),
                NextBillingDate = nextBillingDate,
                DaysRemaining = daysRemaining,
                DisplayStatus = BuildBillingDisplayStatus(profile, alertDate ?? nextBillingDate, daysRemaining),
                SettlementStatus = currentRun is not null
                    ? DetermineBillingSettlementStatus(profile, settledAmount, billedAmount)
                    : PaymentFlowConstants.NormalizeSettlementStatus(profile.SettlementStatus),
                CompletionStatus = outstandingAmount <= 0m
                    ? PaymentFlowConstants.CompletionDone
                    : PaymentFlowConstants.NormalizeCompletionStatus(profile.CompletionStatus),
                SettledAmount = settledAmount,
                OutstandingAmount = outstandingAmount,
                RequiresFollowUp = profile.RequiresFollowUp || outstandingAmount > 0m,
                FollowUpNote = profile.FollowUpNote,
                LastSettledDate = profile.LastSettledDate,
                AssetCount = profileAssets.Count,
                TemplateItemCount = templateItems.Count,
                IncludedAssetCount = includedAssetCount,
                BillingType = string.IsNullOrWhiteSpace(profile.BillingType) ? "묶음" : profile.BillingType,
                BillToCustomerName = string.IsNullOrWhiteSpace(profile.BillToCustomerName) ? customerDisplayName : profile.BillToCustomerName,
                InstallSiteName = string.IsNullOrWhiteSpace(profile.InstallSiteName) ? profile.RealCustomerName : profile.InstallSiteName,
                BillingAdvanceMode = string.IsNullOrWhiteSpace(profile.BillingAdvanceMode) ? "후불" : profile.BillingAdvanceMode,
                BillingDayMode = RentalBillingScheduleRules.NormalizeBillingDayMode(profile.BillingDayMode),
                BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
                    profile.BillingCycleMonths,
                    profile.BillingAnchorMonth,
                    profile.BillingAnchorDate,
                    profile.BillingStartDate,
                    profile.ContractStartDate,
                    profile.ContractDate,
                    profile.LastBilledDate,
                    filter.ReferenceDate),
                DocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(profile.DocumentIssueMode),
                DocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(profile.DocumentLeadDays),
                DocumentIssueDate = documentIssueDate,
                AlertDate = alertDate,
                AlertReason = nextBillingDate.HasValue
                    ? RentalBillingScheduleRules.ResolveAlertReason(nextBillingDate.Value, documentIssueDate)
                    : string.Empty,
                CurrentBillingRunId = currentRun?.RunId,
                CurrentBillingPeriodLabel = currentRun?.PeriodLabel ?? string.Empty,
                CurrentBillingRunStatus = currentRunStatus,
                CurrentBilledAmount = billedAmount,
                HasDataIssue = dataIssues.Count > 0,
                DataIssueSummary = dataIssues.Count == 0 ? string.Empty : string.Join(" / ", dataIssues)
            };
        });

        if (filter.DueOnly)
        {
            rows = rows.Where(row => row.DaysRemaining.HasValue && row.DaysRemaining.Value <= alertWindow);
        }

        return rows
            .OrderBy(row => row.DaysRemaining ?? int.MaxValue)
            .ThenBy(row => row.CustomerDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string ResolveBillingProfileCustomerDisplayName(
        LocalRentalBillingProfile profile,
        IReadOnlyDictionary<Guid, string> customerNameMap)
    {
        if (profile.CustomerId.HasValue &&
            profile.CustomerId.Value != Guid.Empty &&
            customerNameMap.TryGetValue(profile.CustomerId.Value, out var customerName) &&
            !string.IsNullOrWhiteSpace(customerName))
        {
            return customerName.Trim();
        }

        return string.IsNullOrWhiteSpace(profile.CustomerName)
            ? "(거래처 미지정)"
            : profile.CustomerName.Trim();
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
            .Select(asset =>
            {
                var issues = BuildAssetDataIssues(asset);
                return new RentalAssetViewRow
                {
                    Source = asset,
                    ResponsibleOfficeName = ResolveOfficeDisplayName(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, offices),
                    DaysRemaining = asset.RentalEndDate.HasValue
                        ? asset.RentalEndDate.Value.DayNumber - filter.ReferenceDate.DayNumber
                        : null,
                    CurrentCustomerName = string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ? asset.CustomerName : asset.CurrentCustomerName,
                    BillToCustomerName = string.IsNullOrWhiteSpace(asset.BillToCustomerName) ? asset.CustomerName : asset.BillToCustomerName,
                    InstallLocationDisplay = string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation,
                    BillingEligibilityStatus = string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus) ? GetDefaultBillingEligibilityStatus(asset) : asset.BillingEligibilityStatus,
                    HasDataIssue = issues.Count > 0
                };
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
            .ThenBy(asset => asset.ItemName)
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

        profile.CustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(customerName);
        profile.RealCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(profile.RealCustomerName);
        profile.BillToCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(string.IsNullOrWhiteSpace(profile.BillToCustomerName) ? profile.CustomerName : profile.BillToCustomerName);
        profile.InstallSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(string.IsNullOrWhiteSpace(profile.InstallSiteName) ? profile.RealCustomerName : profile.InstallSiteName);
        if (profile.CustomerId is null || profile.CustomerId == Guid.Empty)
            profile.CustomerId = await ResolveCustomerIdAsync(profile.CustomerName, profile.BusinessNumber, ct);
        if ((!profile.CustomerId.HasValue || profile.CustomerId.Value == Guid.Empty) && !string.IsNullOrWhiteSpace(profile.BillToCustomerName))
            profile.CustomerId = await ResolveCustomerIdAsync(profile.BillToCustomerName, profile.BusinessNumber, ct);
        if ((!profile.CustomerId.HasValue || profile.CustomerId.Value == Guid.Empty) && !string.IsNullOrWhiteSpace(profile.RealCustomerName))
            profile.CustomerId = await ResolveCustomerIdAsync(profile.RealCustomerName, profile.BusinessNumber, ct);

        var linkedCustomer = await GetRentalLinkedCustomerAsync(profile.CustomerId, ct);
        if (linkedCustomer is not null)
        {
            officeCode = NormalizeOfficeCode(linkedCustomer.ResponsibleOfficeCode, session.OfficeCode);
            var normalizedCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(linkedCustomer.NameOriginal);
            profile.CustomerName = normalizedCustomerName;
            profile.RealCustomerName = normalizedCustomerName;
            profile.BillToCustomerName = normalizedCustomerName;
        }

        profile.BillingType = NormalizeBillingType(profile.BillingType);
        profile.BillingAdvanceMode = NormalizeBillingAdvanceMode(profile.BillingAdvanceMode);
        NormalizeBillingSchedule(profile, DateOnly.FromDateTime(DateTime.Today));
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
        profile.AssignedUsername = NormalizeAssignedUsername(profile.AssignedUsername, officeCode, session, allowBlankForAdmin: true);
        if (!CanAccessRental(profile.AssignedUsername, profile.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구 데이터를 저장할 수 없습니다.");

        var templateItems = GetBillingTemplateItems(profile, Array.Empty<LocalRentalAsset>());
        profile.BillingTemplateJson = SerializeBillingTemplateItems(templateItems);
        profile.MonthlyAmount = templateItems.Count == 0
            ? Math.Max(0m, profile.MonthlyAmount)
            : templateItems.Sum(item => ResolveTemplateMonthlyAmount(item));
        profile.ItemName = BuildProfileItemName(profile, templateItems);
        profile.ProfileKey = string.IsNullOrWhiteSpace(profile.ProfileKey)
            ? BuildProfileKey(profile.ManagementCompanyCode, profile.BusinessNumber, profile.CustomerName, profile.RealCustomerName, profile.ItemName)
            : profile.ProfileKey;
        profile.IsActive = true;
        profile.IsDeleted = false;

        var duplicate = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id != profile.Id && current.ProfileKey == profile.ProfileKey, ct);
        if (duplicate is not null)
            return LocalMutationResult.Denied("같은 청구 프로필이 이미 존재합니다.");

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            var deterministicProfileId = SyncIdentityGenerator.CreateRentalBillingProfileId(profile.ProfileKey);
            profile.Id = profile.Id == Guid.Empty
                ? (deterministicProfileId == Guid.Empty ? Guid.NewGuid() : deterministicProfileId)
                : profile.Id;
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
        await SyncBillingProfileAssetsAsync(profile, templateItems, ct);
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
        DateOnly referenceDate,
        SessionState session,
        CancellationToken ct = default)
    {
        var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("렌탈 청구 프로필을 찾을 수 없습니다.");
        if (!CanAccessRental(profile.AssignedUsername, profile.ResponsibleOfficeCode, session))
            return LocalMutationResult.Denied("권한이 없어 해당 렌탈 청구를 시작할 수 없습니다.");
        if (_local is null)
            return LocalMutationResult.Denied("렌탈 청구 전표 저장 서비스를 사용할 수 없습니다.");
        NormalizeBillingSchedule(profile, referenceDate);
        if (!IsBillingMonth(profile, referenceDate))
        {
            var nextBillingDate = GetNextBillingDate(profile, referenceDate);
            return LocalMutationResult.Denied(BuildBillingMonthDeniedMessage(profile, referenceDate, nextBillingDate));
        }

        var currentRun = GetOrCreateBillingRun(profile, referenceDate, persistChanges: true);
        if (currentRun is null)
            return LocalMutationResult.Denied("이번 회차 청구 정보를 만들 수 없습니다.");

        var linkedInvoice = await GetActiveBillingInvoiceAsync(currentRun.RunId, ct);
        Guid invoiceId;
        decimal billedAmount;
        if (linkedInvoice is not null)
        {
            invoiceId = linkedInvoice.Id;
            billedAmount = linkedInvoice.TotalAmount;
        }
        else
        {
            var customerId = profile.CustomerId;
            if (!customerId.HasValue || customerId.Value == Guid.Empty)
                customerId = await ResolveCustomerIdAsync(profile.CustomerName, profile.BusinessNumber, ct);
            if ((!customerId.HasValue || customerId.Value == Guid.Empty) && !string.IsNullOrWhiteSpace(profile.BillToCustomerName))
                customerId = await ResolveCustomerIdAsync(profile.BillToCustomerName, profile.BusinessNumber, ct);
            if ((!customerId.HasValue || customerId.Value == Guid.Empty) && !string.IsNullOrWhiteSpace(profile.RealCustomerName))
                customerId = await ResolveCustomerIdAsync(profile.RealCustomerName, profile.BusinessNumber, ct);
            if (!customerId.HasValue || customerId.Value == Guid.Empty)
                return LocalMutationResult.Denied("렌탈 청구 전표를 만들 거래처를 찾을 수 없습니다.");

            profile.CustomerId = customerId;

            var templateItems = currentRun.Items.Count > 0
                ? currentRun.Items
                : GetBillingTemplateItems(profile);
            var lineBuildResult = await BuildRentalBillingInvoiceLinesAsync(profile, currentRun, templateItems, session, ct);
            if (!lineBuildResult.Success)
                return LocalMutationResult.Denied(lineBuildResult.Message);

            var serializedTemplateItems = SerializeBillingTemplateItems(templateItems);
            if (!string.Equals(serializedTemplateItems, profile.BillingTemplateJson ?? string.Empty, StringComparison.Ordinal))
            {
                profile.BillingTemplateJson = serializedTemplateItems;
                profile.MonthlyAmount = templateItems.Sum(ResolveTemplateMonthlyAmount);
                profile.ItemName = BuildProfileItemName(profile, templateItems);
                await SyncBillingProfileAssetsAsync(profile, templateItems, ct);
            }

            var officeCode = NormalizeOfficeCode(profile.ResponsibleOfficeCode, DomainConstants.OfficeUsenet);
            var invoice = new LocalInvoice
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId.Value,
                VoucherType = VoucherType.Sales,
                InvoiceDate = currentRun.ScheduledDate,
                Memo = string.Empty,
                ResponsibleOfficeCode = officeCode,
                SourceWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(null, officeCode, officeCode),
                TaxInvoiceIssued = false,
                LinkedRentalBillingProfileId = profile.Id,
                LinkedRentalBillingRunId = currentRun.RunId,
                Lines = lineBuildResult.Lines
            };

            var savedInvoice = await _local.SaveInvoiceAsync(invoice, ct);
            invoiceId = savedInvoice.Id;
            billedAmount = savedInvoice.TotalAmount;
        }

        profile.BillingStatus = PaymentFlowConstants.BillingStatusInProgress;
        profile.CompletionStatus = PaymentFlowConstants.CompletionPending;
        profile.SettledAmount = Math.Max(0m, profile.SettledAmount);
        profile.OutstandingAmount = Math.Max(0m, billedAmount - profile.SettledAmount);
        profile.SettlementStatus = DetermineBillingSettlementStatus(profile, profile.SettledAmount, billedAmount);
        if (string.Equals(profile.SettlementStatus, PaymentFlowConstants.SettlementStatusUnpaid, StringComparison.OrdinalIgnoreCase))
            profile.SettlementStatus = PaymentFlowConstants.SettlementStatusPending;
        profile.RequiresFollowUp = profile.RequiresFollowUp || profile.OutstandingAmount > 0m;
        currentRun.Status = PaymentFlowConstants.BillingStatusInProgress;
        currentRun.BilledAmount = billedAmount;
        currentRun.SettledAmount = profile.SettledAmount;
        currentRun.SettlementStatus = profile.SettlementStatus;
        UpsertBillingRun(profile, currentRun);
        profile.IsDirty = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(billingProfileId, "렌탈 청구를 시작했습니다.", invoiceId);
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
        var currentRun = GetOrCreateBillingRun(profile, DateOnly.FromDateTime(DateTime.Today), persistChanges: true);
        if (currentRun is not null)
        {
            currentRun.Status = PaymentFlowConstants.BillingStatusOnHold;
            currentRun.Note = normalizedNote;
            UpsertBillingRun(profile, currentRun);
        }
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

        NormalizeBillingSchedule(profile, referenceDate);
        var currentRun = GetOrCreateBillingRun(profile, referenceDate, persistChanges: true);
        var billedAmount = currentRun?.BilledAmount ?? profile.MonthlyAmount;
        var amount = settledAmount.GetValueOrDefault(billedAmount);
        if (amount < 0m)
            amount = 0m;

        profile.SettledAmount = amount;
        profile.OutstandingAmount = Math.Max(0m, billedAmount - amount);
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
        if (currentRun is not null)
        {
            currentRun.BilledAmount = billedAmount;
            currentRun.SettledAmount = amount;
            currentRun.SettlementStatus = profile.SettlementStatus;
            currentRun.SettledDate = referenceDate;
            currentRun.Note = normalizedNote;
            currentRun.Status = profile.OutstandingAmount <= 0m
                ? PaymentFlowConstants.BillingStatusCompleted
                : PaymentFlowConstants.BillingStatusInProgress;
            UpsertBillingRun(profile, currentRun);
        }

        var scheduledDate = currentRun?.ScheduledDate
            ?? GetNextBillingDate(profile, referenceDate)
            ?? RentalBillingScheduleRules.BuildBillingDate(referenceDate.Year, referenceDate.Month, profile.BillingDay, profile.BillingDayMode);
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
                BilledAmount = billedAmount,
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
            log.BilledAmount = billedAmount;
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

    public async Task<IReadOnlyList<LocalRentalAsset>> GetBillingAssetCandidatesAsync(
        Guid? billingProfileId,
        string? customerName,
        string? billToCustomerName,
        string? installSiteName,
        SessionState session,
        CancellationToken ct = default)
    {
        var trimmedCustomer = (customerName ?? string.Empty).Trim();
        var trimmedBillTo = (billToCustomerName ?? string.Empty).Trim();
        var trimmedInstallSite = (installSiteName ?? string.Empty).Trim();
        var nameCandidates = new[] { trimmedCustomer, trimmedBillTo }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var query = ApplyAssetScope(_db.RentalAssets.AsNoTracking(), session)
            .Where(asset => !asset.IsDeleted);

        if (billingProfileId.HasValue && billingProfileId.Value != Guid.Empty)
        {
            var profileId = billingProfileId.Value;
            query = query.Where(asset =>
                asset.BillingProfileId == profileId ||
                nameCandidates.Contains(asset.CustomerName) ||
                nameCandidates.Contains(asset.CurrentCustomerName) ||
                nameCandidates.Contains(asset.BillToCustomerName) ||
                (!string.IsNullOrWhiteSpace(trimmedInstallSite) &&
                    (asset.InstallSiteName == trimmedInstallSite || asset.InstallLocation == trimmedInstallSite)));
        }
        else if (nameCandidates.Length > 0 || !string.IsNullOrWhiteSpace(trimmedInstallSite))
        {
            query = query.Where(asset =>
                nameCandidates.Contains(asset.CustomerName) ||
                nameCandidates.Contains(asset.CurrentCustomerName) ||
                nameCandidates.Contains(asset.BillToCustomerName) ||
                (!string.IsNullOrWhiteSpace(trimmedInstallSite) &&
                    (asset.InstallSiteName == trimmedInstallSite || asset.InstallLocation == trimmedInstallSite)));
        }
        else
        {
            query = query.Where(asset => asset.BillingProfileId.HasValue);
        }

        return await query
            .OrderBy(asset => asset.CustomerName)
            .ThenBy(asset => asset.ManagementNumber)
            .Take(200)
            .ToListAsync(ct);
    }

    public async Task<LocalMutationResult> SaveAssetAsync(
        LocalRentalAsset asset,
        SessionState session,
        CancellationToken ct = default)
    {
        if (asset is null)
            throw new ArgumentNullException(nameof(asset));

        await AssetSaveLock.WaitAsync(ct);
        try
        {
            var existing = await _db.RentalAssets.IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == asset.Id, ct);
            if (existing is not null && !CanEditAssetScope(existing.ResponsibleOfficeCode, session))
                return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산을 수정할 수 없습니다.");

            var officeCode = await ResolveRentalOfficeCodeAsync(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, session.OfficeCode, ct);
            if (string.IsNullOrWhiteSpace(officeCode))
                return LocalMutationResult.Denied("담당지점을 선택하세요.");

            if (!CanManageAllAssetScope(session) &&
                !string.Equals(officeCode, GetDefaultAssetOfficeCode(session), StringComparison.OrdinalIgnoreCase))
            {
                return LocalMutationResult.Denied("일반 사용자는 본인 담당지점 자산만 등록/수정할 수 있습니다.");
            }

            asset.ManagementCompanyCode = officeCode;
            asset.ResponsibleOfficeCode = officeCode;
            asset.AssignedUsername = string.Empty;
            if (!CanEditAssetScope(officeCode, session))
                return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산을 저장할 수 없습니다.");
            asset.ManagementNumber = existing is null
                ? string.Empty
                : (asset.ManagementNumber ?? existing.ManagementNumber ?? string.Empty).Trim();
            asset.ManagementId = existing is null
                ? string.Empty
                : (asset.ManagementId ?? existing.ManagementId ?? string.Empty).Trim();
            asset.CustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CustomerName);
            asset.CurrentCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ? asset.CustomerName : asset.CurrentCustomerName);
            asset.BillToCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(string.IsNullOrWhiteSpace(asset.BillToCustomerName) ? asset.CustomerName : asset.BillToCustomerName);
            asset.CurrentLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CurrentLocation);
            asset.InstallSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallLocation);
            asset.ItemCategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(asset.ItemCategoryName);
            asset.Manufacturer = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.Manufacturer);
            asset.ItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
            asset.MachineNumber = (asset.MachineNumber ?? string.Empty).Trim();
            asset.PurchaseVendor = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.PurchaseVendor);
            asset.InstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallLocation);
            asset.DepositText = (asset.DepositText ?? string.Empty).Trim();
            asset.BillingEligibilityStatus = string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus)
                ? GetDefaultBillingEligibilityStatus(asset)
                : asset.BillingEligibilityStatus.Trim();
            asset.BillingExclusionReason = (asset.BillingExclusionReason ?? string.Empty).Trim();
            await EnsureAssetManagementIdentifiersAsync(
                asset,
                existing,
                existing?.CreatedAtUtc ?? DateTime.UtcNow,
                ct);
            try
            {
                await EnrichAssetReferencesAsync(asset, ct);
            }
            catch (InvalidOperationException ex)
            {
                return LocalMutationResult.Denied(ex.Message);
            }

            var linkedCustomer = await GetRentalLinkedCustomerAsync(asset.CustomerId, ct);
            if (linkedCustomer is not null)
            {
                officeCode = NormalizeOfficeCode(linkedCustomer.ResponsibleOfficeCode, session.OfficeCode);
                asset.ManagementCompanyCode = officeCode;
                asset.ResponsibleOfficeCode = officeCode;
                if (!CanManageAllAssetScope(session) &&
                    !string.Equals(officeCode, GetDefaultAssetOfficeCode(session), StringComparison.OrdinalIgnoreCase))
                {
                    return LocalMutationResult.Denied("일반 사용자는 본인 담당지점 자산만 등록/수정할 수 있습니다.");
                }

                if (!CanEditAssetScope(officeCode, session))
                    return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산을 저장할 수 없습니다.");
            }

            if (!asset.BillingProfileId.HasValue && asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
                asset.BillingProfileId = await FindMatchingBillingProfileIdAsync(asset, ct);

            var baseAssetKey = BuildAssetKey(asset.ManagementCompanyCode, asset.ManagementNumber, asset.ManagementId, asset.MachineNumber, asset.CustomerName, asset.ItemName);
            asset.AssetKey = baseAssetKey;
            asset.AssetStatus = ResolveAssetStatus(asset.AssetStatus, asset.CurrentLocation, asset.DisposalDate);
            asset.IsDeleted = false;

            var duplicate = await _db.RentalAssets.IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id != asset.Id && current.AssetKey == asset.AssetKey, ct);
            if (duplicate is not null && existing is not null)
            {
                asset.AssetKey = BuildLegacyCollisionAssetKey(baseAssetKey, existing.Id);
                duplicate = await _db.RentalAssets.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(current => current.Id != asset.Id && current.AssetKey == asset.AssetKey, ct);
            }

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

            await _db.SaveChangesAsync(ct);
            return LocalMutationResult.Ok(asset.Id, "렌탈 자산을 저장했습니다.");
        }
        finally
        {
            AssetSaveLock.Release();
        }
    }

    public async Task<LocalMutationResult> DeleteAssetAsync(Guid assetId, SessionState session, CancellationToken ct = default)
    {
        var asset = await _db.RentalAssets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == assetId, ct);
        if (asset is null)
            return LocalMutationResult.Missing("렌탈 자산을 찾을 수 없습니다.");
            if (!CanEditAssetScope(asset.ResponsibleOfficeCode, session))
                return LocalMutationResult.Denied("권한이 없어 해당 렌탈 자산을 삭제할 수 없습니다.");

        asset.IsDeleted = true;
        asset.IsDirty = true;
        asset.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(assetId, "렌탈 자산을 삭제했습니다.");
    }

    public Task<RentalCatalogRepairResult> RepairRentalCatalogLinksAsync(CancellationToken ct = default)
        => RepairRentalCatalogLinksAsync(assetIds: null, ct);

    public async Task<RentalCatalogRepairResult> RepairRentalCatalogLinksAsync(
        IReadOnlyCollection<Guid>? assetIds,
        CancellationToken ct = default)
    {
        await AssetSaveLock.WaitAsync(ct);
        try
        {
            var result = new RentalCatalogRepairResult();
            var activeItems = await GetActiveItemsAsync(ct);
            var assetQuery = _db.RentalAssets.IgnoreQueryFilters()
                .Where(asset => !asset.IsDeleted);

            if (assetIds is { Count: > 0 })
            {
                var candidateIds = assetIds
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();

                if (candidateIds.Count == 0)
                    return result;

                assetQuery = assetQuery.Where(asset => candidateIds.Contains(asset.Id));
            }

            var assets = await assetQuery
                .OrderBy(asset => asset.CustomerName)
                .ThenBy(asset => asset.ManagementNumber)
                .ToListAsync(ct);

            result.ScannedAssetCount = assets.Count;

            var beforeSignatureByAssetId = new Dictionary<Guid, string>();
            var assetBaseKeyById = new Dictionary<Guid, string>();
            var displacedItemIds = new HashSet<Guid>();

            foreach (var asset in assets)
            {
                var beforeSignature = BuildAssetRepairSignature(asset);
                beforeSignatureByAssetId[asset.Id] = beforeSignature;
                var previousItemId = asset.ItemId;

                asset.CustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CustomerName);
                asset.CurrentLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CurrentLocation);
                asset.ItemCategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(asset.ItemCategoryName);
                asset.Manufacturer = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.Manufacturer);
                asset.ItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
                asset.MachineNumber = (asset.MachineNumber ?? string.Empty).Trim();
                asset.PurchaseVendor = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.PurchaseVendor);
                asset.InstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallLocation);
                asset.DepositText = (asset.DepositText ?? string.Empty).Trim();

                await EnrichAssetReferencesAsync(asset, ct, result, activeItems);
                if (previousItemId.HasValue &&
                    previousItemId.Value != Guid.Empty &&
                    previousItemId != asset.ItemId)
                {
                    displacedItemIds.Add(previousItemId.Value);
                }

                assetBaseKeyById[asset.Id] = BuildAssetKey(asset.ManagementCompanyCode, asset.ManagementNumber, asset.ManagementId, asset.MachineNumber, asset.CustomerName, asset.ItemName);
                asset.AssetStatus = ResolveAssetStatus(asset.AssetStatus, asset.CurrentLocation, asset.DisposalDate);
            }

            AssignUniqueAssetKeysForRepair(assets, assetBaseKeyById);

            foreach (var asset in assets)
            {
                var beforeSignature = beforeSignatureByAssetId[asset.Id];
                if (!string.Equals(beforeSignature, BuildAssetRepairSignature(asset), StringComparison.Ordinal))
                {
                    result.UpdatedAssetCount++;
                    asset.IsDirty = true;
                    asset.UpdatedAtUtc = DateTime.UtcNow;
                }
            }

            await RetireOrphanedAutoCreatedRentalItemsAsync(displacedItemIds, ct);

            SortAndDistinct(result.AddedCategoryNames);
            SortAndDistinct(result.AddedItemNames);
            SortAndDistinct(result.AmbiguousItemNames);

            await _db.SaveChangesAsync(ct);
            return result;
        }
        finally
        {
            AssetSaveLock.Release();
        }
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

        NormalizeBillingSchedule(profile, referenceDate);
        if (!IsBillingMonth(profile, referenceDate))
        {
            var nextBillingDate = GetNextBillingDate(profile, referenceDate);
            return LocalMutationResult.Denied(BuildBillingMonthDeniedMessage(profile, referenceDate, nextBillingDate));
        }

        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "완료" : status.Trim();
        var currentRun = GetOrCreateBillingRun(profile, referenceDate, persistChanges: true);
        var scheduledDate = currentRun?.ScheduledDate
            ?? GetNextBillingDate(profile, referenceDate)
            ?? RentalBillingScheduleRules.BuildBillingDate(referenceDate.Year, referenceDate.Month, profile.BillingDay, profile.BillingDayMode);
        var billingYearMonth = $"{scheduledDate.Year:0000}-{scheduledDate.Month:00}";
        var log = await _db.RentalBillingLogs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.BillingProfileId == billingProfileId && current.BillingYearMonth == billingYearMonth, ct);
        var billedAmount = currentRun?.BilledAmount ?? profile.MonthlyAmount;
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
                BilledAmount = billedAmount,
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
            log.BilledAmount = billedAmount;
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
            ? billedAmount
            : profile.SettledAmount;
        profile.OutstandingAmount = Math.Max(0m, billedAmount - profile.SettledAmount);
        profile.SettlementStatus = profile.OutstandingAmount <= 0m
            ? PaymentFlowConstants.SettlementStatusConfirmed
            : PaymentFlowConstants.SettlementStatusPartial;
        profile.RequiresFollowUp = profile.OutstandingAmount > 0m;
        if (!profile.RequiresFollowUp)
            profile.FollowUpNote = string.Empty;
        if (currentRun is not null)
        {
            currentRun.BilledAmount = billedAmount;
            currentRun.SettledAmount = profile.SettledAmount;
            currentRun.SettlementStatus = profile.SettlementStatus;
            currentRun.SettledDate = profile.LastSettledDate;
            currentRun.Status = profile.BillingStatus;
            currentRun.Note = (note ?? string.Empty).Trim();
            UpsertBillingRun(profile, currentRun);
        }
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
                var itemName = GetCellString(row, headerMap, "품명", "모델명");
                if (string.IsNullOrWhiteSpace(customerName) && string.IsNullOrWhiteSpace(itemName))
                    continue;

                try
                {
                    var officeValue = GetCellString(row, headerMap, "담당지점", "관리업체");
                    var officeCode = await ResolveRentalOfficeCodeAsync(officeValue, officeValue, session.OfficeCode, ct);
                    var billingDay = ParseIntValue(GetCellValue(row, headerMap, "청구일")) ?? 25;
                    var billingCycleMonths = ParseIntValue(GetCellValue(row, headerMap, "청구기간[개월수]")) ?? 1;
                    var businessNumber = GetCellString(row, headerMap, "사업자번호");
                    var realCustomerName = GetCellString(row, headerMap, "실거래처명");
                    var billingDayMode = billingDay >= 31
                        ? RentalBillingScheduleRules.BillingDayModeEndOfMonth
                        : RentalBillingScheduleRules.BillingDayModeFixedDay;
                    var anchorDate = sheetInfo.Anchor.HasValue
                        ? RentalBillingScheduleRules.BuildBillingDate(sheetInfo.Anchor.Value.Year, sheetInfo.Anchor.Value.Month, billingDay, billingDayMode)
                        : (DateOnly?)null;

                    var profileKey = BuildProfileKey(officeCode, businessNumber, customerName, realCustomerName, itemName);
                    var existing = await _db.RentalBillingProfiles.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(current => current.ProfileKey == profileKey, ct);

                    var profile = existing ?? new LocalRentalBillingProfile
                    {
                        Id = SyncIdentityGenerator.CreateRentalBillingProfileId(profileKey),
                        ProfileKey = profileKey,
                        CreatedAtUtc = DateTime.UtcNow
                    };

                    profile.CustomerName = customerName.Trim();
                    profile.BusinessNumber = businessNumber.Trim();
                    profile.RealCustomerName = realCustomerName.Trim();
                    profile.ItemName = itemName.Trim();
                    profile.ManagementCompanyCode = officeCode;
                    profile.ResponsibleOfficeCode = officeCode;
                    profile.BillingMethod = NormalizeBillingMethod(GetCellString(row, headerMap, "청구방식").Trim());
                    profile.PaymentMethod = GetCellString(row, headerMap, "결제방식").Trim();
                    profile.BillingStatus = string.IsNullOrWhiteSpace(GetCellString(row, headerMap, "청구상태"))
                        ? profile.BillingStatus
                        : GetCellString(row, headerMap, "청구상태").Trim();
                    profile.Email = GetCellString(row, headerMap, "E-Mail").Trim();
                    profile.BillingDayMode = billingDayMode;
                    profile.BillingDay = RentalBillingScheduleRules.NormalizeBillingDay(billingDay);
                    profile.BillingCycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(billingCycleMonths);
                    profile.MonthlyAmount = ParseDecimalValue(GetCellValue(row, headerMap, "월청구대금"));
                    profile.SubmissionDocuments = GetCellString(row, headerMap, "추가제출서류").Trim();
                    profile.Notes = GetCellString(row, headerMap, "비고").Trim();
                    profile.BillingAnchorDate = anchorDate ?? profile.BillingAnchorDate;
                    profile.BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
                        profile.BillingCycleMonths,
                        profile.BillingAnchorMonth,
                        profile.BillingAnchorDate,
                        profile.BillingStartDate,
                        profile.ContractStartDate,
                        profile.ContractDate,
                        profile.LastBilledDate,
                        sheetInfo.Anchor ?? DateOnly.FromDateTime(DateTime.Today));
                    profile.DocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(profile.DocumentIssueMode);
                    profile.DocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(profile.DocumentLeadDays);
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
                        ? NormalizeAssignedUsername(existing?.AssignedUsername, officeCode, session, allowBlankForAdmin: false)
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
            var sourceManagementId = GetCellString(row, headerMap, "관리ID");
            var sourceManagementNumber = GetCellString(row, headerMap, "관리번호");
            var officeValue = GetCellString(row, headerMap, "관리업체", "담당지점");
            var currentLocation = GetCellString(row, headerMap, "현재위치").Trim();
            var itemCategoryName = GetCellString(row, headerMap, "품목분류", "상품분류");
            var manufacturer = GetCellString(row, headerMap, "제조사");
            var customerName = GetCellString(row, headerMap, "고객명");
            var itemName = GetCellString(row, headerMap, "품명", "모델명");
            var machineNumber = GetCellString(row, headerMap, "기계번호");
            var installLocation = GetCellString(row, headerMap, "설치위치");
            if (string.IsNullOrWhiteSpace(sourceManagementNumber) && string.IsNullOrWhiteSpace(customerName) && string.IsNullOrWhiteSpace(itemName))
                continue;
            if (IsRentalAssetSummaryRow(
                    sourceManagementId,
                    sourceManagementNumber,
                    officeValue,
                    currentLocation,
                    itemCategoryName,
                    manufacturer,
                    itemName,
                    machineNumber,
                    customerName,
                    installLocation))
            {
                continue;
            }

            try
            {
                if (!TryResolveImportManagementOfficeCode(officeValue, out var officeCode, out var officeError))
                    throw new InvalidOperationException(officeError);

                var disposalDate = ParseDateValue(GetCellValue(row, headerMap, "폐기일"));
                if (!TryResolveImportAssetStatus(currentLocation, disposalDate, out var assetStatus, out var statusError))
                    throw new InvalidOperationException(statusError);

                var existing = await FindExistingAssetForImportAsync(
                    officeCode,
                    sourceManagementId,
                    sourceManagementNumber,
                    machineNumber,
                    customerName,
                    itemName,
                    installLocation,
                    ct);
                var wasExisting = existing is not null;

                var asset = new LocalRentalAsset
                {
                    Id = existing?.Id ?? Guid.NewGuid(),
                    CustomerId = existing?.CustomerId,
                    ItemId = existing?.ItemId,
                    BillingProfileId = existing?.BillingProfileId,
                    ManagementId = existing?.ManagementId ?? string.Empty,
                    ManagementNumber = existing?.ManagementNumber ?? string.Empty,
                    AssignedUsername = string.Empty,
                    CreatedAtUtc = existing?.CreatedAtUtc ?? DateTime.UtcNow,
                    Notes = BuildImportedAssetNotes(existing?.Notes, sourceManagementId, sourceManagementNumber)
                };

                asset.ManagementCompanyCode = officeCode;
                asset.ResponsibleOfficeCode = officeCode;
                asset.CurrentLocation = currentLocation;
                asset.ItemCategoryName = itemCategoryName.Trim();
                asset.Manufacturer = manufacturer.Trim();
                asset.ItemName = itemName.Trim();
                asset.MachineNumber = machineNumber.Trim();
                asset.PurchaseVendor = GetCellString(row, headerMap, "매입처").Trim();
                asset.PurchaseDate = ParseDateValue(GetCellValue(row, headerMap, "매입일"));
                asset.DisposalDate = disposalDate;
                asset.PurchasePrice = ParseDecimalValue(GetCellValue(row, headerMap, "매입가"));
                asset.SalePrice = ParseDecimalValue(GetCellValue(row, headerMap, "판매가"));
                asset.CustomerName = customerName.Trim();
                asset.InstallLocation = installLocation.Trim();
                asset.DepositText = GetCellString(row, headerMap, "보증금").Trim();
                asset.MonthlyFee = ParseDecimalValue(GetCellValue(row, headerMap, "렌탈요금"));
                asset.ContractMonths = ParseIntValue(GetCellValue(row, headerMap, "계약기간")) ?? 0;
                asset.ContractDate = ParseDateValue(GetCellValue(row, headerMap, "계약일"));
                asset.InstallDate = ParseDateValue(GetCellValue(row, headerMap, "설치일"));
                asset.ContractStartDate = ParseDateValue(GetCellValue(row, headerMap, "계약시작"));
                asset.RentalEndDate = ParseDateValue(GetCellValue(row, headerMap, "렌탈만료"));
                asset.FreeSupplyItems = GetCellString(row, headerMap, "무상품목").Trim();
                asset.PaidSupplyItems = GetCellString(row, headerMap, "유상품목").Trim();
                asset.AssetStatus = assetStatus;

                var saveResult = await SaveAssetAsync(asset, session, ct);
                if (!saveResult.Success)
                    throw new InvalidOperationException(saveResult.Message);

                if (wasExisting)
                    result.UpdatedCount++;
                else
                    result.CreatedCount++;
            }
            catch (Exception ex)
            {
                result.ErrorCount++;
                result.Messages.Add($"렌탈재고관리 {rowIndex + 1}행: {ex.Message}");
            }
        }

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
                profile.ItemName.Contains(keyword) ||
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
                asset.ItemCategoryName.Contains(keyword) ||
                asset.ItemName.Contains(keyword) ||
                asset.MachineNumber.Contains(keyword) ||
                asset.InstallLocation.Contains(keyword));
        }

        if (!string.IsNullOrWhiteSpace(filter.OfficeCode))
            query = query.Where(asset =>
                asset.ResponsibleOfficeCode == filter.OfficeCode ||
                asset.ManagementCompanyCode == filter.OfficeCode);

        if (!string.IsNullOrWhiteSpace(filter.ItemCategoryName))
            query = query.Where(asset => asset.ItemCategoryName == filter.ItemCategoryName);

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
        if (CanViewAllAssetScope(session))
            return query;

        var officeCode = GetDefaultAssetOfficeCode(session);
        return query.Where(asset =>
            asset.ResponsibleOfficeCode == officeCode ||
            asset.ManagementCompanyCode == officeCode);
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
            session.HasAdministrativePrivileges ||
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

    public bool CanViewAllAssetScope(SessionState? session)
        => session is not null && session.IsLoggedIn && (
            session.IsAdmin ||
            session.HasAssignedPermission(AppPermissionNames.RentalViewAll) ||
            session.HasAssignedPermission(AppPermissionNames.RentalEditAll));

    public bool CanManageAllAssetScope(SessionState? session)
        => session is not null && session.IsLoggedIn && (
            session.IsAdmin ||
            session.HasPermission(AppPermissionNames.RentalEditAll));

    public HashSet<string> GetReadableAssetOfficeCodes(SessionState? session)
    {
        if (session is null || !session.IsLoggedIn)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (CanViewAllAssetScope(session))
        {
            return OfficeCodeCatalog.All
                .Select(code => NormalizeOfficeCode(code, DomainConstants.OfficeUsenet))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            GetDefaultAssetOfficeCode(session)
        };
    }

    public string GetDefaultAssetOfficeCode(SessionState session)
        => NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet);

    public bool CanEditAssetScope(string? officeCode, SessionState? session)
    {
        if (session is null || !session.IsLoggedIn)
            return false;

        if (CanManageAllAssetScope(session))
            return true;

        var normalizedOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet);
        var defaultOfficeCode = GetDefaultAssetOfficeCode(session);
        return string.Equals(normalizedOfficeCode, defaultOfficeCode, StringComparison.OrdinalIgnoreCase);
    }

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

    private async Task EnsureAssetManagementIdentifiersAsync(
        LocalRentalAsset asset,
        LocalRentalAsset? existing,
        DateTime registeredAtUtc,
        CancellationToken ct)
    {
        if (existing is not null)
        {
            asset.ManagementId = string.IsNullOrWhiteSpace(asset.ManagementId)
                ? existing.ManagementId
                : asset.ManagementId.Trim();
            asset.ManagementNumber = string.IsNullOrWhiteSpace(asset.ManagementNumber)
                ? existing.ManagementNumber
                : asset.ManagementNumber.Trim();
        }

        if (string.IsNullOrWhiteSpace(asset.ManagementId))
            asset.ManagementId = await GenerateNextManagementIdAsync(asset.Id, ct);

        if (string.IsNullOrWhiteSpace(asset.ManagementNumber))
            asset.ManagementNumber = await GenerateNextManagementNumberAsync(asset.Id, registeredAtUtc, ct);
    }

    private async Task<string> GenerateNextManagementIdAsync(Guid currentAssetId, CancellationToken ct)
    {
        var usedManagementIds = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id != currentAssetId)
            .Select(asset => asset.ManagementId)
            .ToListAsync(ct);

        var nextValue = usedManagementIds
            .Select(ParseManagementId)
            .DefaultIfEmpty(0)
            .Max() + 1;

        return nextValue.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<string> GenerateNextManagementNumberAsync(Guid currentAssetId, DateTime registeredAtUtc, CancellationToken ct)
    {
        var registeredLocalDate = ConvertUtcToKoreaDate(registeredAtUtc);
        var prefix = registeredLocalDate.ToString("yyMM", CultureInfo.InvariantCulture);
        var usedManagementNumbers = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id != currentAssetId)
            .Select(asset => asset.ManagementNumber)
            .ToListAsync(ct);

        var nextSequence = usedManagementNumbers
            .Select(number => ParseManagementNumberSequence(number, prefix))
            .DefaultIfEmpty(0)
            .Max() + 1;

        return $"{prefix}-{nextSequence:000}";
    }

    private async Task<LocalRentalAsset?> FindExistingAssetForImportAsync(
        string officeCode,
        string? sourceManagementId,
        string? sourceManagementNumber,
        string? machineNumber,
        string? customerName,
        string? itemName,
        string? installLocation,
        CancellationToken ct)
    {
        var normalizedOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet);
        var candidates = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset =>
                asset.ManagementCompanyCode == normalizedOfficeCode ||
                asset.ResponsibleOfficeCode == normalizedOfficeCode)
            .ToListAsync(ct);

        var normalizedManagementNumber = NormalizeProfileKeyPart(sourceManagementNumber);
        if (!string.IsNullOrWhiteSpace(normalizedManagementNumber))
        {
            var byManagementNumber = candidates.FirstOrDefault(asset =>
                string.Equals(NormalizeProfileKeyPart(asset.ManagementNumber), normalizedManagementNumber, StringComparison.Ordinal));
            if (byManagementNumber is not null)
                return byManagementNumber;

            var bySourceManagementNumber = candidates.FirstOrDefault(asset =>
                HasImportedSourceIdentifier(asset.Notes, "원본 관리번호", sourceManagementNumber));
            if (bySourceManagementNumber is not null)
                return bySourceManagementNumber;
        }

        var normalizedManagementId = NormalizeProfileKeyPart(sourceManagementId);
        if (!string.IsNullOrWhiteSpace(normalizedManagementId))
        {
            var byManagementId = candidates.FirstOrDefault(asset =>
                string.Equals(NormalizeProfileKeyPart(asset.ManagementId), normalizedManagementId, StringComparison.Ordinal));
            if (byManagementId is not null)
                return byManagementId;

            var bySourceManagementId = candidates.FirstOrDefault(asset =>
                HasImportedSourceIdentifier(asset.Notes, "원본 관리ID", sourceManagementId));
            if (bySourceManagementId is not null)
                return bySourceManagementId;
        }

        var normalizedMachineNumber = NormalizeProfileKeyPart(machineNumber);
        if (!string.IsNullOrWhiteSpace(normalizedMachineNumber))
        {
            var byMachineNumber = candidates.FirstOrDefault(asset =>
                string.Equals(NormalizeProfileKeyPart(asset.MachineNumber), normalizedMachineNumber, StringComparison.Ordinal));
            if (byMachineNumber is not null)
                return byMachineNumber;

            return null;
        }

        var normalizedCustomerName = RentalCatalogValueNormalizer.NormalizeLooseKey(customerName);
        var normalizedItemName = RentalCatalogValueNormalizer.NormalizeLooseKey(itemName);
        var normalizedInstallLocation = RentalCatalogValueNormalizer.NormalizeLooseKey(installLocation);
        if (string.IsNullOrWhiteSpace(normalizedCustomerName) ||
            string.IsNullOrWhiteSpace(normalizedItemName) ||
            string.IsNullOrWhiteSpace(normalizedInstallLocation))
        return null;

        return candidates.FirstOrDefault(asset =>
            string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.CustomerName), normalizedCustomerName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.ItemName), normalizedItemName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.InstallLocation), normalizedInstallLocation, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveImportManagementOfficeCode(string? rawValue, out string officeCode, out string errorMessage)
    {
        var normalizedValue = (rawValue ?? string.Empty).Trim();
        if (ImportManagementOfficeMap.TryGetValue(normalizedValue, out var mappedOfficeCode))
        {
            officeCode = mappedOfficeCode;
            errorMessage = string.Empty;
            return true;
        }

        officeCode = string.Empty;
        errorMessage = string.IsNullOrWhiteSpace(normalizedValue)
            ? "관리업체 값이 비어 있어 담당지점을 변환할 수 없습니다."
            : $"관리업체 '{normalizedValue}'는 담당지점 변환 규칙에 없습니다.";
        return false;
    }

    private static bool IsRentalAssetSummaryRow(
        string? sourceManagementId,
        string? sourceManagementNumber,
        string? officeValue,
        string? currentLocation,
        string? itemCategoryName,
        string? manufacturer,
        string? itemName,
        string? machineNumber,
        string? customerName,
        string? installLocation)
    {
        if (!string.IsNullOrWhiteSpace(sourceManagementId) ||
            !string.IsNullOrWhiteSpace(sourceManagementNumber) ||
            !string.IsNullOrWhiteSpace(officeValue) ||
            !string.IsNullOrWhiteSpace(itemCategoryName) ||
            !string.IsNullOrWhiteSpace(manufacturer) ||
            !string.IsNullOrWhiteSpace(machineNumber) ||
            !string.IsNullOrWhiteSpace(customerName) ||
            !string.IsNullOrWhiteSpace(installLocation))
        {
            return false;
        }

        return IsBlankOrNumericSummaryValue(currentLocation) &&
               IsBlankOrNumericSummaryValue(itemName);
    }

    private static bool TryResolveImportAssetStatus(
        string? currentLocation,
        DateOnly? disposalDate,
        out string assetStatus,
        out string errorMessage)
    {
        var normalizedLocation = (currentLocation ?? string.Empty).Trim();
        if (ImportLocationStatusMap.TryGetValue(normalizedLocation, out var mappedStatus))
        {
            assetStatus = mappedStatus;
            errorMessage = string.Empty;
            return true;
        }

        if (disposalDate.HasValue)
        {
            assetStatus = "폐기";
            errorMessage = string.Empty;
            return true;
        }

        assetStatus = string.Empty;
        errorMessage = string.IsNullOrWhiteSpace(normalizedLocation)
            ? "현재 위치 값이 비어 있어 상태를 변환할 수 없습니다."
            : $"현재 위치 '{normalizedLocation}'는 상태 변환 규칙에 없습니다.";
        return false;
    }

    private static bool IsBlankOrNumericSummaryValue(string? value)
    {
        var normalizedValue = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue))
            return true;

        normalizedValue = normalizedValue.Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(normalizedValue, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
    }

    private static int ParseManagementId(string? managementId)
        => int.TryParse((managementId ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static int ParseManagementNumberSequence(string? managementNumber, string prefix)
    {
        var normalizedValue = (managementNumber ?? string.Empty).Trim();
        if (!normalizedValue.StartsWith($"{prefix}-", StringComparison.OrdinalIgnoreCase))
            return 0;

        var sequenceText = normalizedValue[(prefix.Length + 1)..];
        return int.TryParse(sequenceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static DateOnly ConvertUtcToKoreaDate(DateTime utcDateTime)
    {
        var normalizedUtc = utcDateTime.Kind == DateTimeKind.Utc
            ? utcDateTime
            : utcDateTime.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc)
                : utcDateTime.ToUniversalTime();
        var koreaDateTime = TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, KoreaTimeZone);
        return DateOnly.FromDateTime(koreaDateTime);
    }

    private static TimeZoneInfo ResolveKoreaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        }
    }

    private static string BuildImportedAssetNotes(string? existingNotes, string? sourceManagementId, string? sourceManagementNumber)
    {
        var lines = new List<string>();
        AddDistinctNoteLines(lines, existingNotes);

        if (!string.IsNullOrWhiteSpace(sourceManagementId))
            AddDistinctNoteLine(lines, $"원본 관리ID: {sourceManagementId.Trim()}");
        if (!string.IsNullOrWhiteSpace(sourceManagementNumber))
            AddDistinctNoteLine(lines, $"원본 관리번호: {sourceManagementNumber.Trim()}");

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddDistinctNoteLines(ICollection<string> lines, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return;

        foreach (var line in source
                     .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(current => current.Trim())
                     .Where(current => !string.IsNullOrWhiteSpace(current)))
        {
            AddDistinctNoteLine(lines, line);
        }
    }

    private static void AddDistinctNoteLine(ICollection<string> lines, string line)
    {
        if (!lines.Any(existing => string.Equals(existing, line, StringComparison.OrdinalIgnoreCase)))
            lines.Add(line);
    }

    private static bool HasImportedSourceIdentifier(string? notes, string label, string? value)
    {
        var normalizedValue = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue) || string.IsNullOrWhiteSpace(notes))
            return false;

        var expectedLine = $"{label}: {normalizedValue}";
        return notes
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Any(line => string.Equals(line, expectedLine, StringComparison.OrdinalIgnoreCase));
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

    private async Task RemoveSettingAsync(string key, CancellationToken ct)
    {
        var setting = await _db.Settings.FindAsync([key], ct);
        if (setting is null)
            return;

        _db.Settings.Remove(setting);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<TDraft?> GetDraftAsync<TDraft>(string key, CancellationToken ct)
        where TDraft : class
    {
        var payload = await _db.Settings.AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TDraft>(payload, RentalJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private string BuildDraftSettingKey(string prefix, SessionState session)
    {
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, DomainConstants.OfficeUsenet);
        var username = (session.User?.Username ?? "anonymous").Trim();
        if (string.IsNullOrWhiteSpace(username))
            username = "anonymous";

        return $"{prefix}.{officeCode}.{username}".ToUpperInvariant();
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
        NormalizeBillingSchedule(profile, referenceDate);
        var nextBillingDate = GetNextBillingDate(profile, referenceDate);
        if (!nextBillingDate.HasValue)
            return null;

        var documentIssueDate = RentalBillingScheduleRules.CalculateDocumentIssueDate(nextBillingDate, profile.DocumentIssueMode, profile.DocumentLeadDays);
        var alertDate = RentalBillingScheduleRules.ResolveAlertDate(nextBillingDate.Value, documentIssueDate);
        var alertReason = RentalBillingScheduleRules.ResolveAlertReason(nextBillingDate.Value, documentIssueDate);

        return new RentalAlertItem
        {
            BillingProfileId = profile.Id,
            ResponsibleOfficeName = ResolveOfficeDisplayName(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode, officeMap),
            CustomerName = profile.CustomerName,
            RealCustomerName = profile.RealCustomerName,
            ItemName = profile.ItemName,
            AssignedUsername = profile.AssignedUsername,
            MonthlyAmount = profile.MonthlyAmount,
            NextBillingDate = nextBillingDate.Value,
            DocumentIssueDate = documentIssueDate,
            AlertDate = alertDate,
            AlertReason = alertReason,
            DaysRemaining = alertDate.DayNumber - referenceDate.DayNumber,
            Severity = alertDate.DayNumber < referenceDate.DayNumber
                ? "지연"
                : alertDate == referenceDate ? "오늘" : "예정"
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
            ItemName = asset.ItemName,
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

    public List<RentalBillingTemplateItemModel> GetBillingTemplateItems(LocalRentalBillingProfile profile, IReadOnlyList<LocalRentalAsset>? assets = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        assets ??= Array.Empty<LocalRentalAsset>();
        var profileBillingType = NormalizeBillingType(profile.BillingType);

        List<RentalBillingTemplateItemModel>? parsed = null;
        if (!string.IsNullOrWhiteSpace(profile.BillingTemplateJson))
        {
            try
            {
                parsed = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(profile.BillingTemplateJson, RentalJsonOptions);
            }
            catch
            {
                parsed = null;
            }
        }

        if (parsed is null || parsed.Count == 0)
        {
            var legacyIncludedAssetIds = assets
                .Where(asset => asset.BillingProfileId == profile.Id)
                .Select(asset => asset.Id)
                .Distinct()
                .ToList();
            parsed =
            [
                new RentalBillingTemplateItemModel
                {
                    DisplayItemName = string.IsNullOrWhiteSpace(profile.ItemName) ? "렌탈 임대료" : profile.ItemName,
                    BillingLineMode = profileBillingType == "혼합" ? string.Empty : profileBillingType,
                    Quantity = 1m,
                    UnitPrice = Math.Max(0m, profile.MonthlyAmount),
                    Amount = Math.Max(0m, profile.MonthlyAmount),
                    IncludedAssetIds = legacyIncludedAssetIds
                }
            ];
        }

        var normalized = new List<RentalBillingTemplateItemModel>();
        foreach (var current in parsed)
        {
            if (current is null)
                continue;

            var displayItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(current.DisplayItemName);
            var quantity = current.Quantity <= 0m ? 1m : current.Quantity;
            var unitPrice = Math.Max(0m, current.UnitPrice);
            var amount = current.Amount > 0m ? current.Amount : quantity * unitPrice;
            var includedAssetIds = (current.IncludedAssetIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            var billingLineMode = NormalizeBillingLineMode(current.BillingLineMode);
            if (!string.Equals(profileBillingType, "혼합", StringComparison.OrdinalIgnoreCase))
                billingLineMode = profileBillingType;
            normalized.Add(new RentalBillingTemplateItemModel
            {
                ItemId = current.ItemId == Guid.Empty ? Guid.NewGuid() : current.ItemId,
                DisplayItemName = string.IsNullOrWhiteSpace(displayItemName) ? "렌탈 임대료" : displayItemName,
                BillingLineMode = billingLineMode,
                Quantity = quantity,
                UnitPrice = unitPrice,
                Amount = Math.Max(0m, amount),
                Note = (current.Note ?? string.Empty).Trim(),
                IncludedAssetIds = includedAssetIds
            });
        }

        if (normalized.Count == 0)
        {
            normalized.Add(new RentalBillingTemplateItemModel
            {
                DisplayItemName = string.IsNullOrWhiteSpace(profile.ItemName) ? "렌탈 임대료" : profile.ItemName,
                BillingLineMode = profileBillingType == "혼합" ? string.Empty : profileBillingType,
                Quantity = 1m,
                UnitPrice = Math.Max(0m, profile.MonthlyAmount),
                Amount = Math.Max(0m, profile.MonthlyAmount)
            });
        }

        return normalized;
    }

    public string SerializeBillingTemplateItems(IEnumerable<RentalBillingTemplateItemModel> items)
        => JsonSerializer.Serialize((items ?? Enumerable.Empty<RentalBillingTemplateItemModel>()).ToList(), RentalJsonOptions);

    public List<RentalBillingRunModel> GetBillingRuns(LocalRentalBillingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.BillingRunsJson))
            return new List<RentalBillingRunModel>();

        try
        {
            return JsonSerializer.Deserialize<List<RentalBillingRunModel>>(profile.BillingRunsJson, RentalJsonOptions) ?? new List<RentalBillingRunModel>();
        }
        catch
        {
            return new List<RentalBillingRunModel>();
        }
    }

    public RentalBillingRunModel? GetOrCreateBillingRun(
        LocalRentalBillingProfile profile,
        DateOnly referenceDate,
        bool persistChanges)
    {
        ArgumentNullException.ThrowIfNull(profile);
        NormalizeBillingSchedule(profile, referenceDate);
        var scheduledDate = GetNextBillingDate(profile, referenceDate);
        if (!scheduledDate.HasValue)
            return null;

        var templateItems = GetBillingTemplateItems(profile);
        var cycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(profile.BillingCycleMonths);
        var period = ResolveBillingPeriod(profile, scheduledDate.Value, cycleMonths);
        var runKey = $"{period.StartDate:yyyyMMdd}-{period.EndDate:yyyyMMdd}";
        var runs = GetBillingRuns(profile);
        var existing = runs.FirstOrDefault(run => string.Equals(run.RunKey, runKey, StringComparison.OrdinalIgnoreCase));
        var billedAmount = templateItems.Sum(item => ResolveTemplateMonthlyAmount(item)) * cycleMonths;
        var deterministicRunId = SyncIdentityGenerator.CreateRentalBillingRunId(profile.Id, runKey);
        if (existing is null)
        {
            existing = new RentalBillingRunModel
            {
                RunId = deterministicRunId == Guid.Empty ? Guid.NewGuid() : deterministicRunId,
                RunKey = runKey,
                ScheduledDate = scheduledDate.Value,
                PeriodStartDate = period.StartDate,
                PeriodEndDate = period.EndDate,
                CycleMonths = cycleMonths,
                PeriodLabel = BuildBillingPeriodLabel(period.StartDate, period.EndDate),
                Status = PaymentFlowConstants.BillingStatusPlanned,
                BilledAmount = billedAmount,
                SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
                Items = CloneTemplateItemsForRun(templateItems, cycleMonths)
            };
            runs.Add(existing);
        }
        else
        {
            existing.RunId = existing.RunId == Guid.Empty
                ? (deterministicRunId == Guid.Empty ? Guid.NewGuid() : deterministicRunId)
                : existing.RunId;
            existing.ScheduledDate = scheduledDate.Value;
            existing.PeriodStartDate = period.StartDate;
            existing.PeriodEndDate = period.EndDate;
            existing.CycleMonths = cycleMonths;
            existing.PeriodLabel = BuildBillingPeriodLabel(period.StartDate, period.EndDate);
            existing.BilledAmount = billedAmount;
            existing.Items = CloneTemplateItemsForRun(templateItems, cycleMonths);
        }

        if (persistChanges)
            profile.BillingRunsJson = JsonSerializer.Serialize(runs, RentalJsonOptions);

        return existing;
    }

    private static List<RentalBillingTemplateItemModel> CloneTemplateItemsForRun(
        IEnumerable<RentalBillingTemplateItemModel> items,
        int cycleMonths)
        => (items ?? Enumerable.Empty<RentalBillingTemplateItemModel>())
            .Select(item => new RentalBillingTemplateItemModel
            {
                ItemId = item.ItemId == Guid.Empty ? Guid.NewGuid() : item.ItemId,
                DisplayItemName = item.DisplayItemName,
                BillingLineMode = item.BillingLineMode,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                Amount = ResolveTemplateMonthlyAmount(item),
                Note = item.Note,
                IncludedAssetIds = item.IncludedAssetIds?.Distinct().ToList() ?? new List<Guid>()
            })
            .ToList();

    private static decimal ResolveTemplateMonthlyAmount(RentalBillingTemplateItemModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Amount > 0m)
            return item.Amount;
        return Math.Max(0m, item.Quantity <= 0m ? 1m : item.Quantity) * Math.Max(0m, item.UnitPrice);
    }

    private static string NormalizeBillingLineMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized switch
        {
            "개별" => "개별",
            "묶음" => "묶음",
            _ => string.Empty
        };
    }

    private async Task<LocalInvoice?> GetActiveBillingInvoiceAsync(Guid billingRunId, CancellationToken ct)
    {
        if (billingRunId == Guid.Empty)
            return null;

        return await _db.Invoices
            .Include(invoice => invoice.Lines.Where(line => !line.IsDeleted))
            .Include(invoice => invoice.Payments.Where(payment => !payment.IsDeleted))
            .AsNoTracking()
            .Where(invoice => !invoice.IsDeleted &&
                              invoice.IsLatestVersion &&
                              invoice.LinkedRentalBillingRunId == billingRunId)
            .OrderByDescending(invoice => invoice.LastSavedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<(bool Success, string Message, List<LocalInvoiceLine> Lines)> BuildRentalBillingInvoiceLinesAsync(
        LocalRentalBillingProfile profile,
        RentalBillingRunModel run,
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems,
        SessionState session,
        CancellationToken ct)
    {
        if (templateItems.Count == 0)
            return (false, "청구항목이 없어 판매전표를 만들 수 없습니다.", new List<LocalInvoiceLine>());

        var billingMonths = BuildBillingMonths(run);
        if (billingMonths.Count == 0)
            return (false, "청구월 정보를 계산할 수 없어 판매전표를 만들 수 없습니다.", new List<LocalInvoiceLine>());
        if (billingMonths.Count != Math.Max(1, run.CycleMonths))
        {
            return (false,
                $"청구월 계산 결과({billingMonths.Count}개월)가 청구주기({Math.Max(1, run.CycleMonths)}개월)와 맞지 않습니다.",
                new List<LocalInvoiceLine>());
        }

        var includedAssetIds = templateItems
            .SelectMany(item => item.IncludedAssetIds ?? Enumerable.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        var assetsById = includedAssetIds.Count == 0
            ? new Dictionary<Guid, LocalRentalAsset>()
            : await _db.RentalAssets
                .AsNoTracking()
                .Where(asset => !asset.IsDeleted && includedAssetIds.Contains(asset.Id))
                .ToDictionaryAsync(asset => asset.Id, ct);

        var lines = new List<LocalInvoiceLine>();
        var profileBillingType = NormalizeBillingType(profile.BillingType);
        IReadOnlyList<LocalRentalAsset>? billingAssetCandidates = null;

        foreach (var templateItem in templateItems)
        {
            var lineMode = string.Equals(profileBillingType, "혼합", StringComparison.OrdinalIgnoreCase)
                ? NormalizeBillingLineMode(templateItem.BillingLineMode)
                : profileBillingType;

            if (string.IsNullOrWhiteSpace(lineMode))
            {
                return (false,
                    $"청구항목 '{templateItem.DisplayItemName}'의 라인유형이 지정되지 않아 판매전표를 만들 수 없습니다.",
                    new List<LocalInvoiceLine>());
            }

            var templateAssetIds = (templateItem.IncludedAssetIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (templateAssetIds.Count == 0)
            {
                billingAssetCandidates ??= await GetBillingAssetCandidatesAsync(
                    profile.Id,
                    profile.CustomerName,
                    profile.BillToCustomerName,
                    profile.InstallSiteName,
                    session,
                    ct);

                if (billingAssetCandidates.Count == 1)
                {
                    var autoLinkedAssetId = billingAssetCandidates[0].Id;
                    templateItem.IncludedAssetIds ??= new List<Guid>();
                    if (!templateItem.IncludedAssetIds.Contains(autoLinkedAssetId))
                        templateItem.IncludedAssetIds.Add(autoLinkedAssetId);

                    templateAssetIds = templateItem.IncludedAssetIds
                        .Where(id => id != Guid.Empty)
                        .Distinct()
                        .ToList();

                    if (!assetsById.ContainsKey(autoLinkedAssetId))
                        assetsById[autoLinkedAssetId] = billingAssetCandidates[0];
                }

                if (templateAssetIds.Count == 0 && billingAssetCandidates.Count > 0)
                {
                    return (false,
                        $"청구항목 '{templateItem.DisplayItemName}'에 연결된 설치장비가 없습니다. 후보 장비 {billingAssetCandidates.Count}대를 '선택 장비를 현재 품목에 연결' 버튼으로 연결한 뒤 다시 시도하세요.",
                        new List<LocalInvoiceLine>());
                }

                if (templateAssetIds.Count == 0)
                {
                    return (false,
                        $"청구항목 '{templateItem.DisplayItemName}'에 연결된 설치장비가 없어 판매전표를 만들 수 없습니다.",
                        new List<LocalInvoiceLine>());
                }
            }

            var templateAssets = new List<LocalRentalAsset>();
            foreach (var assetId in templateAssetIds)
            {
                if (!assetsById.TryGetValue(assetId, out var asset))
                {
                    return (false,
                        $"청구항목 '{templateItem.DisplayItemName}'에 연결된 장비를 찾을 수 없어 판매전표를 만들 수 없습니다.",
                        new List<LocalInvoiceLine>());
                }

                if (asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != profile.Id)
                {
                    return (false,
                        $"장비 '{asset.ItemName}'가 다른 렌탈 청구설정에 연결되어 있어 판매전표를 만들 수 없습니다.",
                        new List<LocalInvoiceLine>());
                }

                var eligibilityStatus = string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus)
                    ? GetDefaultBillingEligibilityStatus(asset)
                    : asset.BillingEligibilityStatus.Trim();

                if (string.Equals(eligibilityStatus, "청구제외", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(asset.AssetStatus, "회수", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(asset.AssetStatus, "폐기", StringComparison.OrdinalIgnoreCase))
                {
                    return (false,
                        $"장비 '{asset.ItemName}'는 청구제외 상태라 판매전표를 만들 수 없습니다.",
                        new List<LocalInvoiceLine>());
                }

                templateAssets.Add(asset);
            }

            if (string.Equals(lineMode, "묶음", StringComparison.OrdinalIgnoreCase))
            {
                var representativeAsset = SelectRepresentativeBillingAsset(templateAssets);
                if (representativeAsset is null)
                {
                    return (false,
                        $"청구항목 '{templateItem.DisplayItemName}'의 대표 장비명을 정할 수 없어 판매전표를 만들 수 없습니다.",
                        new List<LocalInvoiceLine>());
                }

                var monthlyAmount = ResolveTemplateMonthlyAmount(templateItem);
                foreach (var billingMonth in billingMonths)
                {
                    lines.Add(new LocalInvoiceLine
                    {
                        Id = Guid.NewGuid(),
                        ItemId = null,
                        ItemTrackingType = ItemTrackingTypes.NonStock,
                        ItemNameOriginal = BuildMonthlyRentalInvoiceItemName(billingMonth),
                        SpecificationOriginal = representativeAsset.ItemName.Trim(),
                        Unit = string.Empty,
                        Quantity = 1m,
                        UnitPrice = monthlyAmount,
                        LineAmount = monthlyAmount,
                        Remark = (templateItem.Note ?? string.Empty).Trim()
                    });
                }

                continue;
            }

            foreach (var asset in templateAssets)
            {
                if (asset.MonthlyFee <= 0m)
                {
                    return (false,
                        $"장비 '{asset.ItemName}'의 월 청구금액이 없어 판매전표를 만들 수 없습니다.",
                        new List<LocalInvoiceLine>());
                }
            }

            var groupedAssets = templateAssets
                .GroupBy(
                    asset => new
                    {
                        ItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName),
                        UnitPrice = asset.MonthlyFee
                    })
                .OrderBy(group => group.Key.ItemName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(group => group.Key.UnitPrice)
                .ToList();

            foreach (var billingMonth in billingMonths)
            {
                foreach (var assetGroup in groupedAssets)
                {
                    var firstAsset = assetGroup.FirstOrDefault();
                    if (firstAsset is null)
                        continue;

                    var quantity = assetGroup.Count();
                    var unitPrice = firstAsset.MonthlyFee;
                    lines.Add(new LocalInvoiceLine
                    {
                        Id = Guid.NewGuid(),
                        ItemId = null,
                        ItemTrackingType = ItemTrackingTypes.NonStock,
                        ItemNameOriginal = BuildMonthlyRentalInvoiceItemName(billingMonth),
                        SpecificationOriginal = firstAsset.ItemName.Trim(),
                        Unit = string.Empty,
                        Quantity = quantity,
                        UnitPrice = unitPrice,
                        LineAmount = quantity * unitPrice,
                        Remark = (templateItem.Note ?? string.Empty).Trim()
                    });
                }
            }
        }

        if (lines.Count == 0)
            return (false, "판매전표에 넣을 청구라인을 만들지 못했습니다.", new List<LocalInvoiceLine>());

        return (true, string.Empty, lines);
    }

    private static List<DateOnly> BuildBillingMonths(RentalBillingRunModel run)
    {
        var months = new List<DateOnly>();
        var current = new DateOnly(run.PeriodStartDate.Year, run.PeriodStartDate.Month, 1);
        var end = new DateOnly(run.PeriodEndDate.Year, run.PeriodEndDate.Month, 1);
        while (current <= end)
        {
            months.Add(current);
            current = current.AddMonths(1);
        }

        return months;
    }

    private static LocalRentalAsset? SelectRepresentativeBillingAsset(IReadOnlyList<LocalRentalAsset> assets)
        => assets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.ItemName))
            .OrderBy(asset => asset.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.MachineNumber, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();

    private static string BuildMonthlyRentalInvoiceItemName(DateOnly billingMonth)
        => $"사무기기 렌탈대금[{billingMonth.Month}월]";

    private static string BuildProfileItemName(LocalRentalBillingProfile profile, IReadOnlyList<RentalBillingTemplateItemModel> templateItems)
    {
        if (templateItems.Count == 0)
            return RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(profile.ItemName);

        if (templateItems.Count == 1)
            return templateItems[0].DisplayItemName;

        return $"{templateItems[0].DisplayItemName} 외 {templateItems.Count - 1}건";
    }

    private static string NormalizeBillingType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized switch
        {
            "개별" => "개별",
            "혼합" => "혼합",
            _ => "묶음"
        };
    }

    private static string NormalizeBillingAdvanceMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.Equals(normalized, "선불", StringComparison.OrdinalIgnoreCase) ? "선불" : "후불";
    }

    private static DateOnly NormalizeReferenceDate(DateOnly referenceDate)
        => referenceDate == default
            ? DateOnly.FromDateTime(DateTime.Today)
            : referenceDate;

    private static void NormalizeBillingSchedule(LocalRentalBillingProfile profile, DateOnly referenceDate)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var normalizedReference = NormalizeReferenceDate(referenceDate);
        profile.BillingDayMode = RentalBillingScheduleRules.NormalizeBillingDayMode(profile.BillingDayMode);
        profile.BillingDay = RentalBillingScheduleRules.NormalizeBillingDay(profile.BillingDay);
        profile.BillingCycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(profile.BillingCycleMonths);
        profile.BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            profile.BillingCycleMonths,
            profile.BillingAnchorMonth,
            profile.BillingAnchorDate,
            profile.BillingStartDate,
            profile.ContractStartDate,
            profile.ContractDate,
            profile.LastBilledDate,
            normalizedReference);
        profile.DocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(profile.DocumentIssueMode);
        profile.DocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(profile.DocumentLeadDays);
    }

    private static string BuildBillingMonthDeniedMessage(
        LocalRentalBillingProfile profile,
        DateOnly referenceDate,
        DateOnly? nextBillingDate)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var dayModeText = string.Equals(
            RentalBillingScheduleRules.NormalizeBillingDayMode(profile.BillingDayMode),
            RentalBillingScheduleRules.BillingDayModeEndOfMonth,
            StringComparison.Ordinal)
            ? "말일"
            : $"매월 {RentalBillingScheduleRules.NormalizeBillingDay(profile.BillingDay)}일";
        var nextDateText = nextBillingDate.HasValue
            ? $"다음 결제예정일은 {nextBillingDate.Value:yyyy-MM-dd}입니다."
            : "다음 결제예정일을 계산할 수 없습니다.";
        return $"이 렌탈은 {RentalBillingScheduleRules.NormalizeCycleMonths(profile.BillingCycleMonths)}개월 {NormalizeBillingAdvanceMode(profile.BillingAdvanceMode)} / {dayModeText} / 기준월 {profile.BillingAnchorMonth}월로 계산되어 {NormalizeReferenceDate(referenceDate):yyyy-MM-dd}에는 청구할 수 없습니다. {nextDateText}";
    }

    private static (DateOnly StartDate, DateOnly EndDate) ResolveBillingPeriod(LocalRentalBillingProfile profile, DateOnly scheduledDate, int cycleMonths)
        => RentalBillingScheduleRules.ResolveBillingPeriod(
            RentalBillingScheduleRules.NormalizeCycleMonths(cycleMonths),
            NormalizeBillingAdvanceMode(profile.BillingAdvanceMode),
            scheduledDate);

    private static string BuildBillingPeriodLabel(DateOnly startDate, DateOnly endDate)
        => startDate == endDate || (startDate.Year == endDate.Year && startDate.Month == endDate.Month)
            ? $"{startDate:yyyy-MM}"
            : $"{startDate:yyyy-MM} ~ {endDate:yyyy-MM}";

    private static string DetermineBillingSettlementStatus(LocalRentalBillingProfile profile, decimal settledAmount, decimal billedAmount)
    {
        if (settledAmount <= 0m)
            return PaymentFlowConstants.GetPendingSettlementStatus(profile.BillingMethod, profile.BillingMethod);
        if (settledAmount < billedAmount)
            return PaymentFlowConstants.SettlementStatusPartial;
        return PaymentFlowConstants.GetDisplaySettlementCompleteStatus(profile.BillingMethod, profile.BillingMethod);
    }

    private List<string> BuildBillingDataIssues(
        LocalRentalBillingProfile profile,
        IReadOnlyList<LocalRentalAsset> assets,
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems)
    {
        var issues = new List<string>();
        var profileBillingType = NormalizeBillingType(profile.BillingType);
        if (templateItems.Count == 0)
            issues.Add("표시품목 없음");
        if (assets.Count == 0)
            issues.Add("연결장비 없음");
        if (string.IsNullOrWhiteSpace(profile.BillToCustomerName))
            issues.Add("청구처 미설정");
        if (string.IsNullOrWhiteSpace(profile.InstallSiteName))
            issues.Add("설치처 미설정");
        if (templateItems.Any(item => item.IncludedAssetIds.Count == 0))
            issues.Add("장비 미연결 품목");
        if (string.Equals(profileBillingType, "혼합", StringComparison.OrdinalIgnoreCase) &&
            templateItems.Any(item => string.IsNullOrWhiteSpace(NormalizeBillingLineMode(item.BillingLineMode))))
        {
            issues.Add("혼합 라인유형 미지정");
        }
        if (assets.Any(asset => string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus) || string.Equals(asset.BillingEligibilityStatus, "미확인", StringComparison.OrdinalIgnoreCase)))
            issues.Add("청구대상 검토 필요");
        return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> BuildAssetDataIssues(LocalRentalAsset asset)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(asset.CurrentCustomerName) && string.IsNullOrWhiteSpace(asset.CustomerName))
            issues.Add("현재거래처 없음");
        if (string.IsNullOrWhiteSpace(asset.InstallSiteName) && string.IsNullOrWhiteSpace(asset.InstallLocation))
            issues.Add("설치위치 불명");
        if (string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus))
            issues.Add("청구상태 미확정");
        if (string.Equals(asset.AssetStatus, "회수", StringComparison.OrdinalIgnoreCase) && asset.BillingProfileId.HasValue)
            issues.Add("회수장비 청구연결");
        return issues;
    }

    private static string GetDefaultBillingEligibilityStatus(LocalRentalAsset asset)
    {
        if (asset.BillingProfileId.HasValue && !string.Equals(asset.AssetStatus, "회수", StringComparison.OrdinalIgnoreCase) && !string.Equals(asset.AssetStatus, "폐기", StringComparison.OrdinalIgnoreCase))
            return "청구대상";
        if (string.Equals(asset.AssetStatus, "회수", StringComparison.OrdinalIgnoreCase) || string.Equals(asset.AssetStatus, "폐기", StringComparison.OrdinalIgnoreCase))
            return "청구제외";
        return "미확인";
    }

    private async Task SyncBillingProfileAssetsAsync(
        LocalRentalBillingProfile profile,
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems,
        CancellationToken ct)
    {
        var includedAssetIds = templateItems
            .SelectMany(item => item.IncludedAssetIds ?? Enumerable.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToHashSet();

        var linkedAssets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .Where(asset => asset.BillingProfileId == profile.Id || includedAssetIds.Contains(asset.Id))
            .ToListAsync(ct);

        if (linkedAssets.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var asset in linkedAssets)
        {
            if (includedAssetIds.Contains(asset.Id))
            {
                asset.BillingProfileId = profile.Id;
                asset.BillToCustomerName = string.IsNullOrWhiteSpace(profile.BillToCustomerName) ? profile.CustomerName : profile.BillToCustomerName;
                asset.InstallSiteName = string.IsNullOrWhiteSpace(profile.InstallSiteName) ? profile.RealCustomerName : profile.InstallSiteName;
                asset.CustomerName = string.IsNullOrWhiteSpace(asset.CustomerName) ? profile.CustomerName : asset.CustomerName;
                asset.CurrentCustomerName = string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ? profile.CustomerName : asset.CurrentCustomerName;
                asset.BillingEligibilityStatus = string.Equals(asset.AssetStatus, "회수", StringComparison.OrdinalIgnoreCase) || string.Equals(asset.AssetStatus, "폐기", StringComparison.OrdinalIgnoreCase)
                    ? "청구제외"
                    : "청구대상";
            }
            else if (asset.BillingProfileId == profile.Id)
            {
                asset.BillingProfileId = null;
                asset.BillingEligibilityStatus = string.Equals(asset.AssetStatus, "회수", StringComparison.OrdinalIgnoreCase) || string.Equals(asset.AssetStatus, "폐기", StringComparison.OrdinalIgnoreCase)
                    ? "청구제외"
                    : "미확인";
            }

            asset.IsDirty = true;
            asset.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private void UpsertBillingRun(LocalRentalBillingProfile profile, RentalBillingRunModel run)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(run);

        var runs = GetBillingRuns(profile);
        var existingIndex = runs.FindIndex(current => current.RunId == run.RunId);
        if (existingIndex >= 0)
            runs[existingIndex] = run;
        else
            runs.Add(run);

        profile.BillingRunsJson = JsonSerializer.Serialize(runs, RentalJsonOptions);
    }

    public DateOnly? GetNextBillingDate(LocalRentalBillingProfile profile, DateOnly referenceDate)
    {
        if (profile is null || !profile.IsActive)
            return null;

        NormalizeBillingSchedule(profile, referenceDate);
        return RentalBillingScheduleRules.ResolveApplicableBillingDate(
            profile.BillingDay,
            profile.BillingDayMode,
            profile.BillingCycleMonths,
            profile.BillingAnchorMonth,
            NormalizeReferenceDate(referenceDate),
            profile.LastBilledDate);
    }

    public bool IsBillingMonth(LocalRentalBillingProfile profile, DateOnly referenceDate)
    {
        NormalizeBillingSchedule(profile, referenceDate);
        return RentalBillingScheduleRules.IsBillingMonth(
            profile.BillingCycleMonths,
            profile.BillingAnchorMonth,
            NormalizeReferenceDate(referenceDate));
    }

    private static DateOnly GetCycleAnchor(LocalRentalBillingProfile profile, DateOnly referenceDate)
    {
        var normalizedReference = NormalizeReferenceDate(referenceDate);
        var anchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            profile.BillingCycleMonths,
            profile.BillingAnchorMonth,
            profile.BillingAnchorDate,
            profile.BillingStartDate,
            profile.ContractStartDate,
            profile.ContractDate,
            profile.LastBilledDate,
            normalizedReference);
        return profile.BillingAnchorDate
               ?? profile.BillingStartDate
               ?? profile.ContractStartDate
               ?? profile.ContractDate
               ?? profile.LastBilledDate
               ?? new DateOnly(normalizedReference.Year, anchorMonth, 1);
    }

    private static DateOnly BuildBillingDate(int year, int month, int billingDay)
        => RentalBillingScheduleRules.BuildBillingDate(year, month, billingDay, RentalBillingScheduleRules.BillingDayModeFixedDay);

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

    private static string NormalizeAssignedUsername(string? assignedUsername, string? officeCode, SessionState session, bool allowBlankForAdmin)
    {
        var normalized = ResolveAssignedUsernameForDisplay(assignedUsername, officeCode, null);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        if (allowBlankForAdmin && session.HasAdministrativePrivileges)
            return string.Empty;

        return ResolveAssignedUsernameForDisplay(session.User?.Username, officeCode, null);
    }

    private static string NormalizeUsername(string? username)
        => (username ?? string.Empty).Trim();

    private static string ResolveAssignedUsernameForDisplay(string? assignedUsername, string? responsibleOfficeCode, string? managementCompanyCode)
    {
        var normalized = NormalizeUsername(assignedUsername);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        if (!IsRoleLikeAssignedUsername(normalized))
            return normalized;

        var officeCode = NormalizeOfficeCode(
            string.IsNullOrWhiteSpace(responsibleOfficeCode) ? managementCompanyCode : responsibleOfficeCode,
            DomainConstants.OfficeUsenet);

        return DefaultAssignedUsernameByOffice.TryGetValue(officeCode, out var mappedUsername)
            ? mappedUsername
            : string.Empty;
    }

    private static bool IsRoleLikeAssignedUsername(string? assignedUsername)
    {
        var normalized = NormalizeUsername(assignedUsername);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return string.Equals(normalized, DomainConstants.RoleAdmin, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, DomainConstants.RoleUser, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "administrator", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Guid?> ResolveCustomerIdAsync(string? customerName, string? businessNumber, CancellationToken ct)
    {
        var normalizedName = (customerName ?? string.Empty).Trim();
        var normalizedBusinessNumber = (businessNumber ?? string.Empty).Trim();
        var nameCandidates = BuildWorkbookCustomerNameCandidates(normalizedName).ToList();

        if (!string.IsNullOrWhiteSpace(normalizedBusinessNumber))
        {
            var businessMatches = await _db.Customers.AsNoTracking()
                .Where(customer => customer.BusinessNumber == normalizedBusinessNumber)
                .Select(customer => new { customer.Id, customer.NameOriginal, customer.NameMatchKey, customer.UpdatedAtUtc })
                .ToListAsync(ct);
            if (businessMatches.Count == 1)
                return businessMatches[0].Id;

            if (businessMatches.Count > 1 && nameCandidates.Count > 0)
            {
                var businessExactMatches = businessMatches
                    .Where(customer => nameCandidates.Contains(customer.NameOriginal, StringComparer.CurrentCultureIgnoreCase))
                    .Select(customer => customer.Id)
                    .Distinct()
                    .ToList();
                if (businessExactMatches.Count == 1)
                    return businessExactMatches[0];

                var candidateKeys = nameCandidates
                    .Select(RentalCatalogValueNormalizer.NormalizeLooseKey)
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var businessNormalizedMatches = businessMatches
                    .Select(customer => new
                    {
                        customer.Id,
                        MatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(
                            string.IsNullOrWhiteSpace(customer.NameMatchKey) ? customer.NameOriginal : customer.NameMatchKey)
                    })
                    .Where(customer => candidateKeys.Any(key =>
                        string.Equals(customer.MatchKey, key, StringComparison.OrdinalIgnoreCase)))
                    .Select(customer => customer.Id)
                    .Distinct()
                    .ToList();
                if (businessNormalizedMatches.Count == 1)
                    return businessNormalizedMatches[0];
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedName))
            return null;

        var directMatches = await _db.Customers.AsNoTracking()
            .Where(customer => nameCandidates.Contains(customer.NameOriginal))
            .OrderByDescending(customer => customer.UpdatedAtUtc)
            .Select(customer => customer.Id)
            .Distinct()
            .ToListAsync(ct);
        if (directMatches.Count == 1)
            return directMatches[0];

        var normalizedNameKeys = nameCandidates
            .Select(RentalCatalogValueNormalizer.NormalizeLooseKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedNameKeys.Count == 0)
            return null;

        var keyMatches = await _db.Customers.AsNoTracking()
            .Where(customer => customer.NameMatchKey != string.Empty)
            .Select(customer => new { customer.Id, customer.NameMatchKey, customer.NameOriginal, customer.UpdatedAtUtc })
            .ToListAsync(ct);

        var normalizedMatches = keyMatches
            .Select(customer => new
            {
                customer.Id,
                customer.NameOriginal,
                customer.UpdatedAtUtc,
                MatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(
                    string.IsNullOrWhiteSpace(customer.NameMatchKey) ? customer.NameOriginal : customer.NameMatchKey)
            })
            .Where(customer => !string.IsNullOrWhiteSpace(customer.MatchKey))
            .ToList();

        var exactMatch = normalizedMatches
            .Where(customer => normalizedNameKeys.Any(key => string.Equals(customer.MatchKey, key, StringComparison.OrdinalIgnoreCase)))
            .Select(customer => customer.Id)
            .Distinct()
            .ToList();
        return exactMatch.Count == 1
            ? exactMatch[0]
            : null;
    }

    private async Task<LocalCustomer?> GetRentalLinkedCustomerAsync(Guid? customerId, CancellationToken ct)
    {
        if (!customerId.HasValue || customerId.Value == Guid.Empty)
            return null;

        var customer = await _db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == customerId.Value && !current.IsDeleted, ct);
        return customer;
    }

    private async Task EnrichAssetReferencesAsync(
        LocalRentalAsset asset,
        CancellationToken ct,
        RentalCatalogRepairResult? repairResult = null,
        List<LocalItem>? activeItems = null,
        bool allowCategoryRecovery = false,
        bool allowDerivedAssetBackfill = true)
    {
        asset.ItemCategoryName = await EnsureRentalItemCategoryOptionAsync(asset.ItemCategoryName, repairResult, ct, allowCategoryRecovery);

        LocalCustomer? customer = null;
        if (asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
        {
            customer = await _db.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == asset.CustomerId.Value, ct);
        }

        if (customer is null)
            asset.CustomerId = await ResolveCustomerIdAsync(asset.CustomerName, null, ct);

        if (asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
        {
            customer ??= await _db.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == asset.CustomerId.Value, ct);
            if (customer is not null)
            {
                if (string.IsNullOrWhiteSpace(asset.CustomerName))
                    asset.CustomerName = customer.NameOriginal.Trim();
            }
            else
            {
                asset.CustomerId = null;
            }
        }

        var item = await EnsureRentalItemAsync(asset, repairResult, ct, activeItems);
        if (item is null)
            return;

        ApplyRentalItemMetadata(asset, item, allowDerivedAssetBackfill);

        if (allowDerivedAssetBackfill && string.IsNullOrWhiteSpace(asset.PurchaseVendor))
            asset.PurchaseVendor = await ResolveLatestPurchaseVendorNameAsync(item.Id, ct);
    }

    private async Task<LocalItem?> ResolveItemAsync(
        Guid? itemId,
        string? itemName,
        string? itemCategoryName,
        string? managementNumber,
        string? machineNumber,
        string preferredOfficeCode,
        string preferredTenantCode,
        CancellationToken ct,
        IReadOnlyList<LocalItem>? activeItems = null)
    {
        var availableItems = activeItems ?? await GetActiveItemsAsync(ct);
        var scopedAssetItems = availableItems
            .Where(item =>
                ItemOperationalPolicy.IsAsset(item.TrackingType) &&
                MatchesRentalItemScope(item, preferredOfficeCode, preferredTenantCode))
            .ToList();

        if (itemId.HasValue && itemId.Value != Guid.Empty)
        {
            var direct = scopedAssetItems.FirstOrDefault(item => item.Id == itemId.Value);
            if (direct is not null)
                return direct;
        }

        var normalizedMaterialNumber = (managementNumber ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedMaterialNumber))
        {
            var materialMatch = scopedAssetItems
                .Where(item => item.MaterialNumber == normalizedMaterialNumber)
                .OrderByDescending(item => item.UpdatedAtUtc)
                .FirstOrDefault();
            if (materialMatch is not null)
                return materialMatch;
        }

        var normalizedMachineNumber = (machineNumber ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedMachineNumber))
        {
            var serialMatch = scopedAssetItems
                .Where(item => item.SerialNumber == normalizedMachineNumber)
                .OrderByDescending(item => item.UpdatedAtUtc)
                .FirstOrDefault();
            if (serialMatch is not null)
                return serialMatch;
        }

        var normalizedItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(itemName);
        if (string.IsNullOrWhiteSpace(normalizedItemName))
            return null;

        var matches = await GetNameMatchedItemsAsync(normalizedItemName, ct, availableItems);
        var assetMatches = matches
            .Where(item =>
                ItemOperationalPolicy.IsAsset(item.TrackingType) &&
                MatchesRentalItemScope(item, preferredOfficeCode, preferredTenantCode))
            .ToList();

        if (assetMatches.Count == 1)
            return assetMatches[0];

        var normalizedCategoryKey = RentalCatalogValueNormalizer.NormalizeLooseKey(itemCategoryName);
        if (!string.IsNullOrWhiteSpace(normalizedCategoryKey))
        {
            var categoryMatches = assetMatches
                .Where(item =>
                    string.Equals(
                        RentalCatalogValueNormalizer.NormalizeLooseKey(item.CategoryName),
                        normalizedCategoryKey,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.LastPurchaseDate)
                .ThenByDescending(item => item.UpdatedAtUtc)
                .ToList();

            if (categoryMatches.Count == 1)
                return categoryMatches[0];
        }

        var displayMatches = assetMatches
            .Where(item =>
                string.Equals(
                    RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.NameOriginal),
                    normalizedItemName,
                    StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(item => item.LastPurchaseDate)
            .ThenByDescending(item => item.UpdatedAtUtc)
            .ToList();

        return displayMatches.Count == 1 ? displayMatches[0] : null;
    }

    private async Task<string> EnsureRentalItemCategoryOptionAsync(
        string? itemCategoryName,
        RentalCatalogRepairResult? repairResult,
        CancellationToken ct,
        bool allowCreateOrReactivate = false)
    {
        var normalizedName = SelectionOptionDefaults.NormalizeItemCategoryName(itemCategoryName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return string.Empty;

        var normalizedKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedName);
        var options = _db.ItemCategoryOptions.Local
            .Concat(await _db.ItemCategoryOptions.IgnoreQueryFilters().ToListAsync(ct))
            .GroupBy(option => option.Id)
            .Select(group => group.First())
            .ToList();
        var existing = options.FirstOrDefault(option =>
            string.Equals(
                RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name),
                normalizedKey,
                StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            var existingName = string.IsNullOrWhiteSpace(existing.Name) ? normalizedName : existing.Name;
            if (existing.IsActive && !existing.IsDeleted)
                return existingName;

            if (!allowCreateOrReactivate)
            {
                TryAddUnique(repairResult?.MissingCategoryNames, existingName);
                if (repairResult is not null)
                    return existingName;

                throw new InvalidOperationException($"삭제되었거나 비활성화된 품목분류 '{existingName}'입니다. 선택값 관리에서 먼저 복구하세요.");
            }

            existing.IsActive = true;
            existing.IsDeleted = false;
            existing.IsDirty = true;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            TryAddUnique(repairResult?.AddedCategoryNames, existingName);
            return existingName;
        }

        if (!allowCreateOrReactivate)
        {
            TryAddUnique(repairResult?.MissingCategoryNames, normalizedName);
            if (repairResult is not null)
                return normalizedName;

            throw new InvalidOperationException($"등록되지 않은 품목분류 '{normalizedName}'입니다. 선택값 관리에서 먼저 추가하세요.");
        }

        var now = DateTime.UtcNow;
        var nextSortOrder = options
            .Where(option => !option.IsDeleted)
            .Select(option => option.SortOrder)
            .DefaultIfEmpty(0)
            .Max() + 10;

        _db.ItemCategoryOptions.Add(new LocalItemCategoryOption
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            SortOrder = nextSortOrder,
            IsSystemDefault = false,
            IsActive = true,
            IsDeleted = false,
            IsDirty = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

        TryAddUnique(repairResult?.AddedCategoryNames, normalizedName);
        return normalizedName;
    }

    private async Task<LocalItem?> EnsureRentalItemAsync(
        LocalRentalAsset asset,
        RentalCatalogRepairResult? repairResult,
        CancellationToken ct,
        List<LocalItem>? activeItems = null)
    {
        var assetOfficeCode = ResolveAssetItemOfficeCode(asset);
        var assetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, assetOfficeCode);
        var item = await ResolveItemAsync(
            asset.ItemId,
            asset.ItemName,
            asset.ItemCategoryName,
            asset.ManagementNumber,
            asset.MachineNumber,
            assetOfficeCode,
            assetTenantCode,
            ct,
            activeItems);
        if (item is not null)
            return item;

        var normalizedItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
        if (string.IsNullOrWhiteSpace(normalizedItemName))
            return null;

        var nameMatches = (await GetNameMatchedItemsAsync(normalizedItemName, ct, activeItems))
            .Where(item => ItemOperationalPolicy.IsAsset(item.TrackingType))
            .ToList();
        var scopedNameMatches = nameMatches
            .Where(item => MatchesRentalItemScope(item, assetOfficeCode, assetTenantCode))
            .ToList();

        if (scopedNameMatches.Count == 1)
            return scopedNameMatches[0];

        if (scopedNameMatches.Count > 1)
        {
            TryAddUnique(repairResult?.AmbiguousItemNames, normalizedItemName);
            return null;
        }

        var now = DateTime.UtcNow;
        var created = new LocalItem
        {
            Id = Guid.NewGuid(),
            TenantCode = assetTenantCode,
            OfficeCode = assetOfficeCode,
            NameOriginal = normalizedItemName,
            NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedItemName),
            SpecificationOriginal = string.Empty,
            SpecificationMatchKey = string.Empty,
            CategoryName = asset.ItemCategoryName,
            ItemKind = ItemKinds.Asset,
            TrackingType = ItemTrackingTypes.Asset,
            StorageLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CurrentLocation),
            PurchasePrice = asset.PurchasePrice,
            SalePrice = asset.SalePrice,
            RetailPrice = asset.SalePrice,
            SimpleMemo = AutoCreatedRentalItemMemo,
            IsRental = true,
            IsSale = false,
            SerialNumber = (asset.MachineNumber ?? string.Empty).Trim(),
            MaterialNumber = (asset.ManagementNumber ?? string.Empty).Trim(),
            InstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallLocation),
            RentalStartDate = asset.ContractStartDate ?? asset.InstallDate ?? asset.ContractDate,
            RentalEndDate = asset.RentalEndDate,
            Notes = BuildAutoCreatedRentalItemNote(asset),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsDirty = true
        };

        _db.Items.Add(created);
        activeItems?.Add(created);
        TryAddUnique(repairResult?.AddedItemNames, created.NameOriginal);
        return created;
    }

    private static bool MatchesRentalItemScope(LocalItem item, string preferredOfficeCode, string preferredTenantCode)
    {
        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(item.OfficeCode, OfficeCodeCatalog.Shared);
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            item.TenantCode,
            item.OfficeCode,
            preferredTenantCode,
            preferredOfficeCode);

        return string.Equals(normalizedOfficeCode, preferredOfficeCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(normalizedTenantCode, preferredTenantCode, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RetireOrphanedAutoCreatedRentalItemsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct)
    {
        if (itemIds.Count == 0)
            return;

        var candidateIds = itemIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (candidateIds.Count == 0)
            return;

        var referencedItemIds = new HashSet<Guid>(
            await _db.RentalAssets.IgnoreQueryFilters()
                .Where(asset => !asset.IsDeleted && asset.ItemId.HasValue && candidateIds.Contains(asset.ItemId.Value))
                .Select(asset => asset.ItemId!.Value)
                .Distinct()
                .ToListAsync(ct));
        foreach (var invoiceItemId in await _db.InvoiceLines.IgnoreQueryFilters()
                     .Where(line => !line.IsDeleted && line.ItemId.HasValue && candidateIds.Contains(line.ItemId.Value))
                     .Select(line => line.ItemId!.Value)
                     .Distinct()
                     .ToListAsync(ct))
        {
            referencedItemIds.Add(invoiceItemId);
        }

        foreach (var stockItemId in await _db.ItemWarehouseStocks
                     .Where(stock => candidateIds.Contains(stock.ItemId))
                     .Select(stock => stock.ItemId)
                     .Distinct()
                     .ToListAsync(ct))
        {
            referencedItemIds.Add(stockItemId);
        }

        foreach (var item in await _db.Items.IgnoreQueryFilters()
                     .Where(current => candidateIds.Contains(current.Id) && !current.IsDeleted)
                     .ToListAsync(ct))
        {
            if (referencedItemIds.Contains(item.Id))
                continue;
            if (!ItemOperationalPolicy.IsAsset(item.TrackingType))
                continue;
            if (!string.Equals(item.SimpleMemo, AutoCreatedRentalItemMemo, StringComparison.Ordinal))
                continue;

            item.IsDeleted = true;
            item.IsDirty = false;
            item.CurrentStock = 0m;
            item.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private async Task<List<LocalItem>> GetNameMatchedItemsAsync(string? itemName, CancellationToken ct, IReadOnlyList<LocalItem>? activeItems = null)
    {
        var normalizedKey = RentalCatalogValueNormalizer.NormalizeLooseKey(itemName);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return [];

        var availableItems = activeItems ?? await GetActiveItemsAsync(ct);
        return availableItems
            .Where(item =>
                string.Equals(
                    RentalCatalogValueNormalizer.NormalizeLooseKey(item.NameOriginal),
                    normalizedKey,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<List<LocalItem>> GetActiveItemsAsync(CancellationToken ct)
    {
        return _db.Items.Local
            .Concat(await _db.Items.IgnoreQueryFilters().Where(item => !item.IsDeleted).ToListAsync(ct))
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .Where(item => !item.IsDeleted)
            .OrderByDescending(item => item.LastPurchaseDate)
            .ThenByDescending(item => item.UpdatedAtUtc)
            .ToList();
    }

    private static void ApplyRentalItemMetadata(LocalRentalAsset asset, LocalItem item, bool allowAssetBackfill = true)
    {
        var itemChanged = false;
        var now = DateTime.UtcNow;
        var assetOfficeCode = ResolveAssetItemOfficeCode(asset);
        var assetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, assetOfficeCode);
        var normalizedCategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(item.CategoryName);
        var normalizedAssetCategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(asset.ItemCategoryName);

        if (string.IsNullOrWhiteSpace(normalizedCategoryName) && !string.IsNullOrWhiteSpace(normalizedAssetCategoryName))
        {
            item.CategoryName = normalizedAssetCategoryName;
            normalizedCategoryName = normalizedAssetCategoryName;
            itemChanged = true;
        }

        var normalizedAssetItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
        if (!string.Equals(item.NameOriginal, normalizedAssetItemName, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(item.NameOriginal))
        {
            item.NameOriginal = normalizedAssetItemName;
            item.NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedAssetItemName);
            itemChanged = true;
        }

        if (string.IsNullOrWhiteSpace(item.MaterialNumber) && !string.IsNullOrWhiteSpace(asset.ManagementNumber))
        {
            item.MaterialNumber = asset.ManagementNumber.Trim();
            itemChanged = true;
        }

        if (string.IsNullOrWhiteSpace(item.SerialNumber) && !string.IsNullOrWhiteSpace(asset.MachineNumber))
        {
            item.SerialNumber = asset.MachineNumber.Trim();
            itemChanged = true;
        }

        if (string.IsNullOrWhiteSpace(item.InstallLocation) && !string.IsNullOrWhiteSpace(asset.InstallLocation))
        {
            item.InstallLocation = asset.InstallLocation.Trim();
            itemChanged = true;
        }

        if (string.IsNullOrWhiteSpace(item.StorageLocation) && !string.IsNullOrWhiteSpace(asset.CurrentLocation))
        {
            item.StorageLocation = asset.CurrentLocation.Trim();
            itemChanged = true;
        }

        if (item.PurchasePrice <= 0m && asset.PurchasePrice > 0m)
        {
            item.PurchasePrice = asset.PurchasePrice;
            itemChanged = true;
        }

        if (item.SalePrice <= 0m && asset.SalePrice > 0m)
        {
            item.SalePrice = asset.SalePrice;
            if (item.RetailPrice <= 0m)
                item.RetailPrice = asset.SalePrice;
            itemChanged = true;
        }

        var rentalStartDate = asset.ContractStartDate ?? asset.InstallDate ?? asset.ContractDate;
        if (!item.RentalStartDate.HasValue && rentalStartDate.HasValue)
        {
            item.RentalStartDate = rentalStartDate;
            itemChanged = true;
        }

        if (!item.RentalEndDate.HasValue && asset.RentalEndDate.HasValue)
        {
            item.RentalEndDate = asset.RentalEndDate;
            itemChanged = true;
        }

        if (!item.IsRental)
        {
            item.IsRental = true;
            itemChanged = true;
        }

        if (!string.Equals(item.TrackingType, ItemTrackingTypes.Asset, StringComparison.Ordinal))
        {
            item.TrackingType = ItemTrackingTypes.Asset;
            itemChanged = true;
        }

        if (!string.Equals(item.ItemKind, ItemKinds.Asset, StringComparison.Ordinal))
        {
            item.ItemKind = ItemKinds.Asset;
            itemChanged = true;
        }

        if (item.IsSale)
        {
            item.IsSale = false;
            itemChanged = true;
        }

        if (!string.Equals(item.OfficeCode, assetOfficeCode, StringComparison.OrdinalIgnoreCase))
        {
            item.OfficeCode = assetOfficeCode;
            itemChanged = true;
        }

        if (!string.Equals(item.TenantCode, assetTenantCode, StringComparison.OrdinalIgnoreCase))
        {
            item.TenantCode = assetTenantCode;
            itemChanged = true;
        }

        if (string.IsNullOrWhiteSpace(item.SimpleMemo))
        {
            item.SimpleMemo = AutoCreatedRentalItemMemo;
            itemChanged = true;
        }

        asset.ItemId = item.Id;
        asset.ItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.NameOriginal);
        asset.ItemCategoryName = normalizedCategoryName;
        if (asset.PurchasePrice <= 0m && item.PurchasePrice > 0m)
            asset.PurchasePrice = item.PurchasePrice;
        if (allowAssetBackfill && asset.SalePrice <= 0m && item.SalePrice > 0m)
            asset.SalePrice = item.SalePrice;
        if (allowAssetBackfill && string.IsNullOrWhiteSpace(asset.InstallLocation) && !string.IsNullOrWhiteSpace(item.InstallLocation))
            asset.InstallLocation = item.InstallLocation.Trim();
        if (allowAssetBackfill && string.IsNullOrWhiteSpace(asset.CurrentLocation) && !string.IsNullOrWhiteSpace(item.StorageLocation))
            asset.CurrentLocation = item.StorageLocation.Trim();
        if (allowAssetBackfill && string.IsNullOrWhiteSpace(asset.MachineNumber) && !string.IsNullOrWhiteSpace(item.SerialNumber))
            asset.MachineNumber = item.SerialNumber.Trim();

        if (itemChanged)
        {
            item.IsDirty = true;
            item.UpdatedAtUtc = now;
        }
    }

    private static string ResolveAssetItemOfficeCode(LocalRentalAsset asset)
        => OfficeCodeCatalog.NormalizeOfficeCodeLoose(
            asset.ResponsibleOfficeCode,
            asset.ManagementCompanyCode,
            DomainConstants.OfficeUsenet);

    private static string BuildAutoCreatedRentalItemNote(LocalRentalAsset asset)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(asset.CustomerName))
            parts.Add($"거래처:{asset.CustomerName.Trim()}");
        if (!string.IsNullOrWhiteSpace(asset.ManagementNumber))
            parts.Add($"관리번호:{asset.ManagementNumber.Trim()}");
        if (!string.IsNullOrWhiteSpace(asset.MachineNumber))
            parts.Add($"기계번호:{asset.MachineNumber.Trim()}");
        return string.Join(" / ", parts);
    }

    private static string BuildAssetRepairSignature(LocalRentalAsset asset)
        => string.Join("|",
            asset.CustomerId?.ToString("N") ?? string.Empty,
            asset.ItemId?.ToString("N") ?? string.Empty,
            asset.AssetKey,
            asset.CustomerName,
            asset.ItemCategoryName,
            asset.ItemName,
            asset.ManagementNumber,
            asset.ManagementId,
            asset.MachineNumber,
            asset.PurchaseVendor,
            asset.InstallLocation,
            asset.CurrentLocation,
            asset.AssetStatus,
            asset.PurchasePrice.ToString(CultureInfo.InvariantCulture),
            asset.SalePrice.ToString(CultureInfo.InvariantCulture));

    private static void TryAddUnique(ICollection<string>? values, string? value)
    {
        if (values is null || string.IsNullOrWhiteSpace(value))
            return;

        if (values.Any(existing =>
                string.Equals(
                    RentalCatalogValueNormalizer.NormalizeLooseKey(existing),
                    RentalCatalogValueNormalizer.NormalizeLooseKey(value),
                    StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        values.Add(value.Trim());
    }

    private static void SortAndDistinct(List<string> values)
    {
        if (values.Count <= 1)
            return;

        var ordered = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        values.Clear();
        values.AddRange(ordered);
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
        if ((!asset.CustomerId.HasValue || asset.CustomerId.Value == Guid.Empty) &&
            string.IsNullOrWhiteSpace(asset.CustomerName))
        return null;

        var profiles = _db.RentalBillingProfiles.AsNoTracking()
            .Where(profile => !profile.IsDeleted);

        if (asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
        {
            profiles = profiles.Where(profile => profile.CustomerId == asset.CustomerId.Value);
        }
        else
        {
            profiles = profiles.Where(profile => profile.CustomerName == asset.CustomerName);
        }

        var candidates = await profiles.ToListAsync(ct);

        if (candidates.Count == 0)
            return null;

        var normalizedItemKey = RentalCatalogValueNormalizer.NormalizeLooseKey(asset.ItemName);
        var siteKeys = new[] { asset.InstallLocation, asset.InstallSiteName }
            .Select(RentalCatalogValueNormalizer.NormalizeLooseKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(normalizedItemKey))
        {
            var itemMatches = candidates
                .Where(profile =>
                {
                    var profileItemKey = RentalCatalogValueNormalizer.NormalizeLooseKey(profile.ItemName);
                    return !string.IsNullOrWhiteSpace(profileItemKey) &&
                           (string.Equals(profileItemKey, normalizedItemKey, StringComparison.OrdinalIgnoreCase) ||
                            profileItemKey.Contains(normalizedItemKey, StringComparison.OrdinalIgnoreCase) ||
                            normalizedItemKey.Contains(profileItemKey, StringComparison.OrdinalIgnoreCase));
                })
                .ToList();

            if (siteKeys.Count > 0)
            {
                var strictMatches = itemMatches
                    .Where(profile =>
                    {
                        var profileSiteKey = RentalCatalogValueNormalizer.NormalizeLooseKey(profile.InstallSiteName);
                        return !string.IsNullOrWhiteSpace(profileSiteKey) &&
                               siteKeys.Contains(profileSiteKey, StringComparer.OrdinalIgnoreCase);
                    })
                    .ToList();
                if (strictMatches.Count == 1)
                    return strictMatches[0].Id;
            }

            if (itemMatches.Count == 1)
                return itemMatches[0].Id;
        }

        if (siteKeys.Count > 0)
        {
            var siteMatches = candidates
                .Where(profile =>
                {
                    var profileSiteKey = RentalCatalogValueNormalizer.NormalizeLooseKey(profile.InstallSiteName);
                    return !string.IsNullOrWhiteSpace(profileSiteKey) &&
                           siteKeys.Contains(profileSiteKey, StringComparer.OrdinalIgnoreCase);
                })
                .ToList();
            if (siteMatches.Count == 1)
                return siteMatches[0].Id;
        }

        return null;
    }

    private static string BuildProfileKey(
        string managementCompanyCode,
        string? businessNumber,
        string? customerName,
        string? realCustomerName,
        string? itemName)
    {
        return string.Join('|',
            NormalizeProfileKeyPart(managementCompanyCode),
            NormalizeProfileKeyPart(businessNumber),
            NormalizeProfileKeyPart(customerName),
            NormalizeProfileKeyPart(realCustomerName),
            NormalizeProfileKeyPart(itemName));
    }

    private static string BuildAssetKey(
        string managementCompanyCode,
        string? managementNumber,
        string? managementId,
        string? machineNumber,
        string? customerName,
        string? itemName)
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
            NormalizeProfileKeyPart(itemName));
    }

    private static void AssignUniqueAssetKeysForRepair(
        IReadOnlyList<LocalRentalAsset> assets,
        IReadOnlyDictionary<Guid, string> assetBaseKeyById)
    {
        var assignedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in assets
                     .GroupBy(asset => assetBaseKeyById.TryGetValue(asset.Id, out var key) ? key : string.Empty, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var orderedAssets = group
                .OrderBy(asset => asset.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(asset => asset.ManagementId, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(asset => asset.CustomerName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(asset => asset.ItemName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(asset => asset.Id)
                .ToList();

            var baseKey = group.Key;
            for (var index = 0; index < orderedAssets.Count; index++)
            {
                var asset = orderedAssets[index];
                var candidate = index == 0 && !string.IsNullOrWhiteSpace(baseKey)
                    ? baseKey
                    : BuildLegacyCollisionAssetKey(baseKey, asset.Id);

                if (assignedKeys.Contains(candidate))
                    candidate = BuildLegacyCollisionAssetKey(candidate, asset.Id);

                asset.AssetKey = candidate;
                assignedKeys.Add(candidate);
            }
        }
    }

    private static string BuildLegacyCollisionAssetKey(string? baseKey, Guid assetId)
    {
        var normalizedBaseKey = (baseKey ?? string.Empty).Trim();
        var idSuffix = assetId == Guid.Empty ? Guid.NewGuid().ToString("N") : assetId.ToString("N");
        return string.IsNullOrWhiteSpace(normalizedBaseKey)
            ? idSuffix
            : $"{normalizedBaseKey}|{idSuffix}";
    }

    private static string NormalizeProfileKeyPart(string? value)
        => new string((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(ch => !char.IsWhiteSpace(ch) && ch != '[' && ch != ']')
            .ToArray());

    private static string ResolveAssetStatus(string? requestedStatus, string? currentLocation, DateOnly? disposalDate)
    {
        var status = (requestedStatus ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(status))
            return status;

        if (disposalDate.HasValue)
            return "폐기";

        var location = (currentLocation ?? string.Empty).Trim();
        if (location.Contains("판매", StringComparison.OrdinalIgnoreCase))
            return "판매";
        if (location.Contains("폐기", StringComparison.OrdinalIgnoreCase))
            return "폐기";
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
