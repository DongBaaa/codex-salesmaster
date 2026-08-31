using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Tools.SyncDiag;

internal sealed class RtRentalFullStagePlan
{
    public int SchemaVersion { get; set; }
    public string PlanId { get; set; } = string.Empty;
    public string BusinessDatabaseName { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; }
    public List<RtRentalFullStagePlanEntry> Entries { get; set; } = [];
}

internal sealed class RtRentalFullStagePlanEntry
{
    public string Operation { get; set; } = string.Empty;
    public Guid AssetId { get; set; }
    public long ExpectedRevision { get; set; }
    public DateTime ExpectedUpdatedAtUtc { get; set; }
    public string ExpectedTenantCode { get; set; } = string.Empty;
    public string ExpectedOfficeCode { get; set; } = string.Empty;
    public string ExpectedResponsibleOfficeCode { get; set; } = string.Empty;
    public string ExpectedManagementCompanyCode { get; set; } = string.Empty;
    public string ExpectedManagementNumber { get; set; } = string.Empty;
    public string ExpectedAssetStatus { get; set; } = string.Empty;
    public Guid? ExpectedCustomerId { get; set; }
    public string ExpectedCustomerName { get; set; } = string.Empty;
    public string ExpectedCurrentCustomerName { get; set; } = string.Empty;
    public Guid? ExpectedBillingProfileId { get; set; }
    public RtRentalFullStageValues Values { get; set; } = new();
}

internal sealed class RtRentalFullStageValues
{
    public string CurrentLocation { get; set; } = string.Empty;
    public string ItemCategoryName { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string MachineNumber { get; set; } = string.Empty;
    public DateOnly? DisposalDate { get; set; }
    public string InstallLocation { get; set; } = string.Empty;
    public decimal MonthlyFee { get; set; }
    public int ContractMonths { get; set; }
    public DateOnly? ContractStartDate { get; set; }
    public DateOnly? RentalEndDate { get; set; }
    public string AssetStatus { get; set; } = string.Empty;
    public string BillingEligibilityStatus { get; set; } = string.Empty;
    public string BillingExclusionReason { get; set; } = string.Empty;
}

internal sealed class RtRentalCustomerCandidate
{
    public string BusinessDatabaseName { get; set; } = string.Empty;
    public string ManagementNumber { get; set; } = string.Empty;
    public string RtStatus { get; set; } = string.Empty;
    public string RtCustomerName { get; set; } = string.Empty;
    public string CurrentCustomerName { get; set; } = string.Empty;
    public Guid? CurrentCustomerId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ProposedAction { get; set; } = string.Empty;
    public List<RtRentalCustomerMatch> TopMatches { get; set; } = [];
}

internal sealed class RtRentalCustomerMatch
{
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string ResponsibleOfficeCode { get; set; } = string.Empty;
    public decimal Score { get; set; }
}

internal sealed class RtRentalBillingFeeCandidate
{
    public string BusinessDatabaseName { get; set; } = string.Empty;
    public string ManagementNumber { get; set; } = string.Empty;
    public string RtStatus { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public Guid AssetId { get; set; }
    public Guid BillingProfileId { get; set; }
    public string BillingProfileKey { get; set; } = string.Empty;
    public decimal CurrentAssetMonthlyFee { get; set; }
    public decimal RtMonthlyFee { get; set; }
    public decimal BillingProfileMonthlyAmount { get; set; }
}

internal sealed class RtRentalBillingProfileReferenceCandidate
{
    public string BusinessDatabaseName { get; set; } = string.Empty;
    public string ManagementNumber { get; set; } = string.Empty;
    public string RtStatus { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public Guid AssetId { get; set; }
    public Guid MissingBillingProfileId { get; set; }
    public string CurrentAssetStatus { get; set; } = string.Empty;
    public string RequestedAssetStatus { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

internal sealed class RtRentalFullStageAudit
{
    public int SourceRowCount { get; set; }
    public int SourceTargetCompanyRowCount { get; set; }
    public int SourceOtherCompanyRowCount { get; set; }
    public int TargetActiveAssetCount { get; set; }
    public int DuplicateSourceKeyCount { get; set; }
    public int DuplicateTargetKeyCount { get; set; }
    public int MatchedExistingCount { get; set; }
    public int SafeCreateCount { get; set; }
    public int CustomerApprovalHeldCreateCount { get; set; }
    public int InvalidSourceCount { get; set; }
    public int AlreadyEqualCount { get; set; }
    public int PlannedUpdateCount { get; set; }
    public int PlannedCreateCount { get; set; }
    public int PlannedChangeCount { get; set; }
    public int StatusChangeCount { get; set; }
    public int CustomerConfirmationCandidateCount { get; set; }
    public int BillingProfileFeeConfirmationCandidateCount { get; set; }
    public int MissingBillingProfileReferenceHeldCount { get; set; }
    public int UnlinkedFeeChangeCount { get; set; }
    public Dictionary<string, int> SourceStatusCounts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ChangedFieldCounts { get; set; } = new(StringComparer.Ordinal);
}

internal sealed record RtRentalFullStageBuildResult(
    RtRentalFullStagePlan Plan,
    RtRentalFullStageAudit Audit,
    IReadOnlyList<RtRentalCustomerCandidate> CustomerCandidates,
    IReadOnlyList<RtRentalBillingFeeCandidate> BillingFeeCandidates,
    IReadOnlyList<RtRentalBillingProfileReferenceCandidate> BillingProfileReferenceCandidates);

internal sealed record RtRentalFullStageGenerationResult(
    string PlanPath,
    string ReportPath,
    string PlanSha256,
    string SourceSha256,
    string BusinessDatabaseName,
    long ServerRevision,
    int CredentialCandidateCount,
    int LoginSucceededCount,
    int RentalAssetEditAllowedCount,
    int BusinessDatabaseSelectedCount,
    RtRentalFullStageAudit Audit,
    int CustomerCandidateCount,
    int BillingFeeCandidateCount);

internal static class RtRentalFullStagePlanner
{
    internal const string OperationUpdate = "Update";
    internal const string OperationCreate = "Create";
    private const string BillingEligibilityTarget = "청구대상";
    private const string BillingEligibilityExcluded = "청구제외";
    private const string BillingEligibilityUnconfirmed = "미확인";

    internal static RtRentalFullStageBuildResult BuildPlan(
        IReadOnlyCollection<RtRentalSourceRow> sourceRows,
        SyncPullResponse snapshot,
        string businessDatabaseName,
        string sourceSha256,
        string planId,
        DateTime generatedAtUtc)
    {
        var normalizedDatabaseName = TenantScopeCatalog.GetDatabaseName(businessDatabaseName);
        var targetCompanyCode = ResolveTargetCompanyCode(normalizedDatabaseName);
        var targetSourceCompany = string.Equals(targetCompanyCode, OfficeCodeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
            ? "아이티월드"
            : "유즈넷";
        var targetRows = sourceRows
            .Where(row => string.Equals(NormalizeKey(row.ManagementCompany), NormalizeKey(targetSourceCompany), StringComparison.Ordinal))
            .ToList();
        var audit = new RtRentalFullStageAudit
        {
            SourceRowCount = sourceRows.Count,
            SourceTargetCompanyRowCount = targetRows.Count,
            SourceOtherCompanyRowCount = sourceRows.Count - targetRows.Count
        };
        foreach (var group in targetRows.GroupBy(row => NormalizeText(row.Status), StringComparer.Ordinal))
            audit.SourceStatusCounts[group.Key] = group.Count();

        var sourceGroups = targetRows
            .GroupBy(row => NormalizeKey(row.ManagementNumber), StringComparer.Ordinal)
            .ToList();
        audit.DuplicateSourceKeyCount = sourceGroups
            .Where(group => string.IsNullOrEmpty(group.Key) || group.Count() != 1)
            .Sum(group => group.Count());

        var currentTargetAssets = snapshot.RentalAssets
            .Where(asset =>
                !asset.IsDeleted &&
                string.Equals(
                    NormalizeKey(asset.ManagementCompanyCode),
                    NormalizeKey(targetCompanyCode),
                    StringComparison.Ordinal))
            .ToList();
        audit.TargetActiveAssetCount = currentTargetAssets.Count;
        var targetGroups = currentTargetAssets
            .GroupBy(asset => NormalizeKey(asset.ManagementNumber), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        audit.DuplicateTargetKeyCount = targetGroups
            .Where(pair => string.IsNullOrEmpty(pair.Key) || pair.Value.Count != 1)
            .Sum(pair => pair.Value.Count);

        var profilesById = snapshot.RentalBillingProfiles
            .Where(profile => !profile.IsDeleted)
            .GroupBy(profile => profile.Id)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(profile => profile.Revision).First());
        var activeCustomers = snapshot.Customers
            .Where(customer => !customer.IsDeleted)
            .ToList();
        var customerCandidates = new List<RtRentalCustomerCandidate>();
        var feeCandidates = new List<RtRentalBillingFeeCandidate>();
        var billingProfileReferenceCandidates = new List<RtRentalBillingProfileReferenceCandidate>();
        var plan = new RtRentalFullStagePlan
        {
            SchemaVersion = 2,
            PlanId = planId,
            BusinessDatabaseName = normalizedDatabaseName,
            SourceSha256 = sourceSha256,
            GeneratedAtUtc = EnsureUtc(generatedAtUtc)
        };

        foreach (var sourceGroup in sourceGroups.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(sourceGroup.Key) || sourceGroup.Count() != 1)
                continue;
            var source = sourceGroup.Single();
            if (!TryMapSourceStatus(source.Status, out var targetStatus) ||
                !TryParseSourceValues(source, out var parsed))
            {
                audit.InvalidSourceCount++;
                continue;
            }

            if (targetGroups.TryGetValue(sourceGroup.Key, out var currentGroup))
            {
                if (currentGroup.Count != 1)
                    continue;
                var current = currentGroup[0];
                audit.MatchedExistingCount++;
                AddCustomerCandidateIfNeeded(
                    customerCandidates,
                    normalizedDatabaseName,
                    source,
                    current,
                    activeCustomers,
                    isCreate: false);

                var values = BuildExistingValues(source, parsed, current, targetStatus);
                if (values.MonthlyFee != current.MonthlyFee)
                {
                    if (current.BillingProfileId is Guid billingProfileId && billingProfileId != Guid.Empty)
                    {
                        values.MonthlyFee = current.MonthlyFee;
                        profilesById.TryGetValue(billingProfileId, out var profile);
                        feeCandidates.Add(new RtRentalBillingFeeCandidate
                        {
                            BusinessDatabaseName = normalizedDatabaseName,
                            ManagementNumber = current.ManagementNumber,
                            RtStatus = NormalizeText(source.Status),
                            CustomerName = FirstNonEmpty(current.CurrentCustomerName, current.CustomerName),
                            AssetId = current.Id,
                            BillingProfileId = billingProfileId,
                            BillingProfileKey = profile?.ProfileKey ?? string.Empty,
                            CurrentAssetMonthlyFee = current.MonthlyFee,
                            RtMonthlyFee = parsed.MonthlyFee ?? current.MonthlyFee,
                            BillingProfileMonthlyAmount = profile?.MonthlyAmount ?? 0
                        });
                    }
                    else
                    {
                        audit.UnlinkedFeeChangeCount++;
                    }
                }

                if (ValuesEqual(current, values))
                {
                    audit.AlreadyEqualCount++;
                    continue;
                }

                if ((!current.BillingProfileId.HasValue || current.BillingProfileId.Value == Guid.Empty) &&
                    FindExplicitProfileThatCanAutoLink(current, profilesById.Values) is { } autoLinkProfile)
                {
                    billingProfileReferenceCandidates.Add(new RtRentalBillingProfileReferenceCandidate
                    {
                        BusinessDatabaseName = normalizedDatabaseName,
                        ManagementNumber = current.ManagementNumber,
                        RtStatus = NormalizeText(source.Status),
                        CustomerName = FirstNonEmpty(current.CurrentCustomerName, current.CustomerName),
                        AssetId = current.Id,
                        MissingBillingProfileId = autoLinkProfile.Id,
                        CurrentAssetStatus = RentalAssetStatusNormalizer.Normalize(current.AssetStatus),
                        RequestedAssetStatus = values.AssetStatus,
                        Reason = "자산에는 청구 프로필이 연결되어 있지 않지만 활성 청구 프로필의 명시적 자산 구성에 포함되어 있습니다. 서버가 상태 변경 중 프로필과 프로필 거래처를 자동 연결할 수 있으므로 승인 없이 변경하지 않습니다."
                    });
                    audit.MissingBillingProfileReferenceHeldCount++;
                    continue;
                }

                if (current.BillingProfileId is Guid referencedProfileId &&
                    referencedProfileId != Guid.Empty &&
                    (!profilesById.TryGetValue(referencedProfileId, out var referencedProfile) ||
                     !IsUsableBillingProfileReference(current, referencedProfile)))
                {
                    billingProfileReferenceCandidates.Add(new RtRentalBillingProfileReferenceCandidate
                    {
                        BusinessDatabaseName = normalizedDatabaseName,
                        ManagementNumber = current.ManagementNumber,
                        RtStatus = NormalizeText(source.Status),
                        CustomerName = FirstNonEmpty(current.CurrentCustomerName, current.CustomerName),
                        AssetId = current.Id,
                        MissingBillingProfileId = referencedProfileId,
                        CurrentAssetStatus = RentalAssetStatusNormalizer.Normalize(current.AssetStatus),
                        RequestedAssetStatus = values.AssetStatus,
                        Reason = "자산이 없거나 현재 자산 범위와 맞지 않는 청구 프로필을 참조하여 서버가 변경을 거절합니다. 프로필 범위 복구 또는 연결 해제 승인이 필요합니다."
                    });
                    audit.MissingBillingProfileReferenceHeldCount++;
                    continue;
                }

                CountChangedFields(current, values, audit);
                plan.Entries.Add(new RtRentalFullStagePlanEntry
                {
                    Operation = OperationUpdate,
                    AssetId = current.Id,
                    ExpectedRevision = current.Revision,
                    ExpectedUpdatedAtUtc = current.UpdatedAtUtc,
                    ExpectedTenantCode = current.TenantCode,
                    ExpectedOfficeCode = current.OfficeCode,
                    ExpectedResponsibleOfficeCode = current.ResponsibleOfficeCode,
                    ExpectedManagementCompanyCode = current.ManagementCompanyCode,
                    ExpectedManagementNumber = current.ManagementNumber,
                    ExpectedAssetStatus = current.AssetStatus,
                    ExpectedCustomerId = current.CustomerId,
                    ExpectedCustomerName = current.CustomerName,
                    ExpectedCurrentCustomerName = current.CurrentCustomerName,
                    ExpectedBillingProfileId = current.BillingProfileId,
                    Values = values
                });
                audit.PlannedUpdateCount++;
                continue;
            }

            var sourceCustomerName = NormalizeCustomerName(source.CustomerName);
            if (!string.IsNullOrEmpty(sourceCustomerName))
            {
                audit.CustomerApprovalHeldCreateCount++;
                AddCustomerCandidateIfNeeded(
                    customerCandidates,
                    normalizedDatabaseName,
                    source,
                    current: null,
                    activeCustomers,
                    isCreate: true);
                continue;
            }

            var createValues = BuildCreateValues(source, parsed, targetStatus);
            var assetId = CreateDeterministicGuid($"{planId}|{normalizedDatabaseName}|{sourceGroup.Key}");
            plan.Entries.Add(new RtRentalFullStagePlanEntry
            {
                Operation = OperationCreate,
                AssetId = assetId,
                ExpectedRevision = 0,
                ExpectedUpdatedAtUtc = default,
                ExpectedTenantCode = ResolveTargetTenantCode(targetCompanyCode),
                ExpectedOfficeCode = targetCompanyCode,
                ExpectedResponsibleOfficeCode = targetCompanyCode,
                ExpectedManagementCompanyCode = targetCompanyCode,
                ExpectedManagementNumber = NormalizeText(source.ManagementNumber),
                ExpectedAssetStatus = string.Empty,
                Values = createValues
            });
            CountCreateFields(createValues, audit);
            audit.SafeCreateCount++;
            audit.PlannedCreateCount++;
        }

        plan.Entries = plan.Entries
            .OrderBy(entry => entry.ExpectedManagementNumber, StringComparer.Ordinal)
            .ThenBy(entry => entry.AssetId)
            .ToList();
        audit.PlannedChangeCount = plan.Entries.Count;
        audit.CustomerConfirmationCandidateCount = customerCandidates.Count;
        audit.BillingProfileFeeConfirmationCandidateCount = feeCandidates.Count;
        return new RtRentalFullStageBuildResult(
            plan,
            audit,
            customerCandidates
                .OrderBy(candidate => candidate.ManagementNumber, StringComparer.Ordinal)
                .ToList(),
            feeCandidates
                .OrderBy(candidate => candidate.ManagementNumber, StringComparer.Ordinal)
                .ToList(),
            billingProfileReferenceCandidates
                .OrderBy(candidate => candidate.ManagementNumber, StringComparer.Ordinal)
                .ToList());
    }

    private static RtRentalFullStageValues BuildExistingValues(
        RtRentalSourceRow source,
        ParsedSourceValues parsed,
        RentalAssetDto current,
        string targetStatus)
    {
        var eligibility = ResolveEligibility(
            current.AssetStatus,
            current.BillingProfileId,
            current.BillingEligibilityStatus,
            current.BillingExclusionReason,
            targetStatus);
        return new RtRentalFullStageValues
        {
            CurrentLocation = ResolveCurrentLocation(source, current.CurrentLocation, current.AssetStatus, targetStatus),
            ItemCategoryName = PreferSource(source.ItemCategoryName, current.ItemCategoryName),
            Manufacturer = PreferSource(source.Manufacturer, current.Manufacturer),
            ItemName = PreferSource(source.ItemName, current.ItemName),
            MachineNumber = PreferSource(source.MachineNumber, current.MachineNumber),
            DisposalDate = parsed.DisposalDate ?? current.DisposalDate,
            InstallLocation = PreferSource(source.InstallLocation, current.InstallLocation),
            MonthlyFee = parsed.MonthlyFee ?? current.MonthlyFee,
            ContractMonths = parsed.ContractMonths ?? current.ContractMonths,
            ContractStartDate = parsed.ContractStartDate ?? current.ContractStartDate,
            RentalEndDate = parsed.RentalEndDate ?? current.RentalEndDate,
            AssetStatus = targetStatus,
            BillingEligibilityStatus = eligibility.Status,
            BillingExclusionReason = eligibility.Reason
        };
    }

    private static bool IsUsableBillingProfileReference(
        RentalAssetDto asset,
        RentalBillingProfileDto profile)
    {
        if (profile.IsDeleted)
            return false;

        var assetOffice = RentalScopeNormalizer.ResolveResponsibleOfficeCode(
            asset.TenantCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode,
            asset.ResponsibleOfficeCode);
        var profileOffice = OfficeCodeCatalog.NormalizeOfficeCodeLoose(
            profile.ResponsibleOfficeCode,
            null,
            OfficeCodeCatalog.Usenet);
        var assetTenant = RentalScopeNormalizer.ResolveTenantCode(
            asset.TenantCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode,
            asset.ResponsibleOfficeCode);
        var profileTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            profile.TenantCode,
            profile.OfficeCode,
            profile.TenantCode,
            profile.ResponsibleOfficeCode);
        if (!string.Equals(assetOffice, profileOffice, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(assetTenant, profileTenant, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (profile.CustomerId is Guid profileCustomerId &&
            profileCustomerId != Guid.Empty &&
            profileCustomerId != asset.CustomerId)
        {
            return false;
        }

        return RentalBillingTemplateAssetCoverageRules.AllowsLink(
            profile.BillingTemplateJson,
            asset.Id);
    }

    private static RentalBillingProfileDto? FindExplicitProfileThatCanAutoLink(
        RentalAssetDto asset,
        IEnumerable<RentalBillingProfileDto> profiles)
        => profiles
            .Where(profile =>
                IsUsableBillingProfileReference(asset, profile) &&
                RentalBillingTemplateAssetCoverageRules.Evaluate(
                    profile.BillingTemplateJson,
                    asset.Id) == RentalBillingTemplateAssetCoverage.UniqueReference)
            .OrderByDescending(profile => profile.Revision)
            .ThenBy(profile => profile.Id)
            .FirstOrDefault();

    private static RtRentalFullStageValues BuildCreateValues(
        RtRentalSourceRow source,
        ParsedSourceValues parsed,
        string targetStatus)
    {
        var eligibility = ResolveEligibility(
            string.Empty,
            null,
            string.Empty,
            string.Empty,
            targetStatus);
        return new RtRentalFullStageValues
        {
            CurrentLocation = ResolveCurrentLocation(source, string.Empty, string.Empty, targetStatus),
            ItemCategoryName = SourceOrEmpty(source.ItemCategoryName),
            Manufacturer = SourceOrEmpty(source.Manufacturer),
            ItemName = SourceOrEmpty(source.ItemName),
            MachineNumber = SourceOrEmpty(source.MachineNumber),
            DisposalDate = parsed.DisposalDate,
            InstallLocation = SourceOrEmpty(source.InstallLocation),
            MonthlyFee = parsed.MonthlyFee ?? 0,
            ContractMonths = parsed.ContractMonths ?? 0,
            ContractStartDate = parsed.ContractStartDate,
            RentalEndDate = parsed.RentalEndDate,
            AssetStatus = targetStatus,
            BillingEligibilityStatus = eligibility.Status,
            BillingExclusionReason = eligibility.Reason
        };
    }

    private static (string Status, string Reason) ResolveEligibility(
        string currentStatus,
        Guid? billingProfileId,
        string currentEligibility,
        string currentReason,
        string targetStatus)
    {
        if (RentalAssetStatusNormalizer.IsNonOperating(targetStatus))
            return (BillingEligibilityExcluded, $"자산상태: {targetStatus}");

        var currentWasNonOperating = RentalAssetStatusNormalizer.IsNonOperating(currentStatus);
        var currentReasonWasAutomatic = NormalizeText(currentReason)
            .StartsWith("자산상태:", StringComparison.OrdinalIgnoreCase);
        if (currentWasNonOperating &&
            string.Equals(NormalizeText(currentEligibility), BillingEligibilityExcluded, StringComparison.OrdinalIgnoreCase) &&
            currentReasonWasAutomatic)
        {
            return (billingProfileId.HasValue ? BillingEligibilityTarget : BillingEligibilityUnconfirmed, string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(currentStatus))
            return (NormalizeText(currentEligibility), NormalizeText(currentReason));
        return (billingProfileId.HasValue ? BillingEligibilityTarget : BillingEligibilityUnconfirmed, NormalizeText(currentReason));
    }

    private static string ResolveCurrentLocation(
        RtRentalSourceRow source,
        string currentLocation,
        string currentStatus,
        string targetStatus)
    {
        if (RentalAssetStatusNormalizer.IsWarehouse(targetStatus))
            return RentalAssetStatusNormalizer.Warehouse;
        if (string.Equals(targetStatus, RentalAssetStatusNormalizer.Sold, StringComparison.OrdinalIgnoreCase))
            return RentalAssetStatusNormalizer.Sold;
        if (RentalAssetStatusNormalizer.IsDisposed(targetStatus))
            return RentalAssetStatusNormalizer.Disposed;
        return RentalAssetStatusNormalizer.IsNonOperating(currentStatus)
            ? PreferSource(source.InstallLocation, currentLocation)
            : NormalizeText(currentLocation);
    }

    private static void AddCustomerCandidateIfNeeded(
        ICollection<RtRentalCustomerCandidate> destination,
        string businessDatabaseName,
        RtRentalSourceRow source,
        RentalAssetDto? current,
        IReadOnlyCollection<CustomerDto> customers,
        bool isCreate)
    {
        var sourceCustomerName = NormalizeCustomerName(source.CustomerName);
        if (string.IsNullOrEmpty(sourceCustomerName))
            return;
        var currentCustomerName = current is null
            ? string.Empty
            : FirstNonEmpty(current.CurrentCustomerName, current.CustomerName);
        if (!isCreate && string.Equals(
                NormalizeCustomerKey(sourceCustomerName),
                NormalizeCustomerKey(currentCustomerName),
                StringComparison.Ordinal))
        {
            return;
        }

        var matches = customers
            .Select(customer => new RtRentalCustomerMatch
            {
                CustomerId = customer.Id,
                CustomerName = NormalizeText(customer.NameOriginal),
                ResponsibleOfficeCode = NormalizeText(customer.ResponsibleOfficeCode),
                Score = CalculateSimilarity(sourceCustomerName, customer.NameOriginal)
            })
            .Where(match => match.Score >= 25)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .Take(3)
            .ToList();
        var proposedAction = matches.Count == 0
            ? "신규 거래처 생성 검토"
            : matches[0].Score >= 80
                ? "기존 거래처 연결 검토"
                : "유사 거래처 또는 신규 생성 검토";
        destination.Add(new RtRentalCustomerCandidate
        {
            BusinessDatabaseName = businessDatabaseName,
            ManagementNumber = NormalizeText(source.ManagementNumber),
            RtStatus = NormalizeText(source.Status),
            RtCustomerName = sourceCustomerName,
            CurrentCustomerName = currentCustomerName,
            CurrentCustomerId = current?.CustomerId,
            Reason = isCreate
                ? "올바른 DB에 자산이 없어 신규 자산과 거래처 연결 승인이 필요합니다."
                : string.IsNullOrWhiteSpace(currentCustomerName)
                    ? "RT에는 거래처가 있지만 거래플랜 자산에는 연결 거래처가 없습니다."
                    : "RT 거래처 표기와 현재 거래플랜 연결 거래처 표기가 다릅니다.",
            ProposedAction = proposedAction,
            TopMatches = matches
        });
    }

    private static decimal CalculateSimilarity(string left, string right)
    {
        var a = NormalizeCustomerKey(left);
        var b = NormalizeCustomerKey(right);
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return 0;
        if (string.Equals(a, b, StringComparison.Ordinal))
            return 100;

        var containment = a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)
            ? 0.9m * Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length)
            : 0m;
        var distance = LevenshteinDistance(a, b);
        var editRatio = 1m - (decimal)distance / Math.Max(a.Length, b.Length);
        var bigramRatio = BigramDice(a, b);
        return Math.Round(Math.Max(containment, (editRatio * 0.55m) + (bigramRatio * 0.45m)) * 100m, 1);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static decimal BigramDice(string left, string right)
    {
        if (left.Length < 2 || right.Length < 2)
            return left == right ? 1 : 0;
        var leftPairs = Enumerable.Range(0, left.Length - 1)
            .Select(index => left.Substring(index, 2))
            .ToList();
        var rightPairs = Enumerable.Range(0, right.Length - 1)
            .Select(index => right.Substring(index, 2))
            .ToList();
        var remaining = new List<string>(rightPairs);
        var matches = 0;
        foreach (var pair in leftPairs)
        {
            var index = remaining.IndexOf(pair);
            if (index < 0)
                continue;
            matches++;
            remaining.RemoveAt(index);
        }
        return 2m * matches / (leftPairs.Count + rightPairs.Count);
    }

    private static bool TryParseSourceValues(RtRentalSourceRow source, out ParsedSourceValues values)
    {
        values = new ParsedSourceValues();
        if (!TryParseDate(source.ContractStartDate, out var contractStartDate) ||
            !TryParseDate(source.RentalEndDate, out var rentalEndDate) ||
            !TryParseDate(source.DisposalDate, out var disposalDate) ||
            !TryParseMonths(source.ContractMonthsText, out var months) ||
            !TryParseFee(source.MonthlyFeeText, out var monthlyFee))
        {
            return false;
        }

        values = new ParsedSourceValues(
            monthlyFee,
            months,
            contractStartDate,
            rentalEndDate,
            disposalDate);
        return true;
    }

    private static bool TryParseDate(string raw, out DateOnly? value)
    {
        var normalized = SourceOrEmpty(raw);
        if (string.IsNullOrEmpty(normalized))
        {
            value = null;
            return true;
        }
        if (DateOnly.TryParseExact(
                normalized,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            value = parsed;
            return true;
        }
        value = null;
        return false;
    }

    private static bool TryParseMonths(string raw, out int? value)
    {
        var normalized = SourceOrEmpty(raw);
        if (string.IsNullOrEmpty(normalized))
        {
            value = null;
            return true;
        }
        normalized = normalized
            .Replace("개월", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }
        value = null;
        return false;
    }

    private static bool TryParseFee(string raw, out decimal? value)
    {
        var normalized = SourceOrEmpty(raw);
        if (string.IsNullOrEmpty(normalized))
        {
            value = null;
            return true;
        }
        if (string.Equals(normalized, "무료", StringComparison.Ordinal) ||
            string.Equals(normalized, "면제", StringComparison.Ordinal))
        {
            value = 0;
            return true;
        }
        normalized = normalized
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("원", string.Empty, StringComparison.Ordinal)
            .Replace("₩", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }
        value = null;
        return false;
    }

    internal static bool TryMapSourceStatus(string raw, out string status)
    {
        status = NormalizeText(raw) switch
        {
            "렌탈" => RentalAssetStatusNormalizer.Active,
            "계약종료" => RentalAssetStatusNormalizer.Warehouse,
            "창고" => RentalAssetStatusNormalizer.Warehouse,
            "판매" => RentalAssetStatusNormalizer.Sold,
            "폐기" => RentalAssetStatusNormalizer.Disposed,
            _ => string.Empty
        };
        return !string.IsNullOrEmpty(status);
    }

    private static void CountChangedFields(
        RentalAssetDto current,
        RtRentalFullStageValues values,
        RtRentalFullStageAudit audit)
    {
        Count(nameof(values.CurrentLocation), NormalizeText(current.CurrentLocation), values.CurrentLocation);
        Count(nameof(values.ItemCategoryName), NormalizeText(current.ItemCategoryName), values.ItemCategoryName);
        Count(nameof(values.Manufacturer), NormalizeText(current.Manufacturer), values.Manufacturer);
        Count(nameof(values.ItemName), NormalizeText(current.ItemName), values.ItemName);
        Count(nameof(values.MachineNumber), NormalizeText(current.MachineNumber), values.MachineNumber);
        Count(nameof(values.DisposalDate), current.DisposalDate, values.DisposalDate);
        Count(nameof(values.InstallLocation), NormalizeText(current.InstallLocation), values.InstallLocation);
        Count(nameof(values.MonthlyFee), current.MonthlyFee, values.MonthlyFee);
        Count(nameof(values.ContractMonths), current.ContractMonths, values.ContractMonths);
        Count(nameof(values.ContractStartDate), current.ContractStartDate, values.ContractStartDate);
        Count(nameof(values.RentalEndDate), current.RentalEndDate, values.RentalEndDate);
        Count(nameof(values.AssetStatus), RentalAssetStatusNormalizer.Normalize(current.AssetStatus), values.AssetStatus);
        Count(nameof(values.BillingEligibilityStatus), NormalizeText(current.BillingEligibilityStatus), values.BillingEligibilityStatus);
        Count(nameof(values.BillingExclusionReason), NormalizeText(current.BillingExclusionReason), values.BillingExclusionReason);
        if (!string.Equals(RentalAssetStatusNormalizer.Normalize(current.AssetStatus), values.AssetStatus, StringComparison.Ordinal))
            audit.StatusChangeCount++;
        return;

        void Count<T>(string field, T before, T after)
        {
            if (EqualityComparer<T>.Default.Equals(before, after))
                return;
            audit.ChangedFieldCounts[field] = audit.ChangedFieldCounts.TryGetValue(field, out var count)
                ? count + 1
                : 1;
        }
    }

    private static void CountCreateFields(RtRentalFullStageValues values, RtRentalFullStageAudit audit)
    {
        foreach (var field in new[]
                 {
                     nameof(values.CurrentLocation),
                     nameof(values.ItemCategoryName),
                     nameof(values.Manufacturer),
                     nameof(values.ItemName),
                     nameof(values.MachineNumber),
                     nameof(values.InstallLocation),
                     nameof(values.MonthlyFee),
                     nameof(values.ContractMonths),
                     nameof(values.ContractStartDate),
                     nameof(values.RentalEndDate),
                     nameof(values.DisposalDate),
                     nameof(values.AssetStatus),
                     nameof(values.BillingEligibilityStatus),
                     nameof(values.BillingExclusionReason)
                 })
        {
            audit.ChangedFieldCounts[field] = audit.ChangedFieldCounts.TryGetValue(field, out var count)
                ? count + 1
                : 1;
        }
    }

    internal static bool ValuesEqual(RentalAssetDto asset, RtRentalFullStageValues values)
        => string.Equals(NormalizeText(asset.CurrentLocation), values.CurrentLocation, StringComparison.Ordinal) &&
           string.Equals(NormalizeText(asset.ItemCategoryName), values.ItemCategoryName, StringComparison.Ordinal) &&
           string.Equals(NormalizeText(asset.Manufacturer), values.Manufacturer, StringComparison.Ordinal) &&
           string.Equals(NormalizeText(asset.ItemName), values.ItemName, StringComparison.Ordinal) &&
           string.Equals(NormalizeText(asset.MachineNumber), values.MachineNumber, StringComparison.Ordinal) &&
           asset.DisposalDate == values.DisposalDate &&
           string.Equals(NormalizeText(asset.InstallLocation), values.InstallLocation, StringComparison.Ordinal) &&
           asset.MonthlyFee == values.MonthlyFee &&
           asset.ContractMonths == values.ContractMonths &&
           asset.ContractStartDate == values.ContractStartDate &&
           asset.RentalEndDate == values.RentalEndDate &&
           string.Equals(RentalAssetStatusNormalizer.Normalize(asset.AssetStatus), values.AssetStatus, StringComparison.Ordinal) &&
           string.Equals(NormalizeText(asset.BillingEligibilityStatus), values.BillingEligibilityStatus, StringComparison.Ordinal) &&
           string.Equals(NormalizeText(asset.BillingExclusionReason), values.BillingExclusionReason, StringComparison.Ordinal);

    internal static string ResolveTargetCompanyCode(string businessDatabaseName)
        => string.Equals(
            TenantScopeCatalog.GetDatabaseName(businessDatabaseName),
            TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld),
            StringComparison.OrdinalIgnoreCase)
            ? OfficeCodeCatalog.Itworld
            : OfficeCodeCatalog.Usenet;

    internal static string ResolveTargetTenantCode(string targetCompanyCode)
        => string.Equals(targetCompanyCode, OfficeCodeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
            ? TenantScopeCatalog.Itworld
            : TenantScopeCatalog.UsenetGroup;

    private static string NormalizeCustomerName(string? value)
        => SourceOrEmpty(value);

    private static string NormalizeCustomerKey(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        foreach (var token in new[] { "주식회사", "유한회사", "(주)", "㈜", "(유)" })
            normalized = normalized.Replace(token, string.Empty, StringComparison.Ordinal);
        return string.Concat(normalized.Where(char.IsLetterOrDigit));
    }

    private static string PreferSource(string source, string fallback)
        => string.IsNullOrEmpty(SourceOrEmpty(source)) ? NormalizeText(fallback) : SourceOrEmpty(source);

    private static string SourceOrEmpty(string? value)
    {
        var normalized = NormalizeText(value);
        return normalized == "-" ? string.Empty : normalized;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.Select(NormalizeText).FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;

    private static string NormalizeKey(string? value)
        => string.Concat(
            NormalizeText(value)
                .Normalize(NormalizationForm.FormKC)
                .Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();

    private static string NormalizeText(string? value)
        => (value ?? string.Empty).Trim();

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static Guid CreateDeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }

    private sealed record ParsedSourceValues(
        decimal? MonthlyFee = null,
        int? ContractMonths = null,
        DateOnly? ContractStartDate = null,
        DateOnly? RentalEndDate = null,
        DateOnly? DisposalDate = null);
}

internal static partial class RtRentalDeltaApplier
{
    internal static async Task<RtRentalFullStageGenerationResult> GenerateFullStagePlanAsync(
        string sourceCsvPath,
        string credentialDatabasePath,
        string businessDatabaseName,
        string planOutputPath,
        string reportOutputPath,
        CancellationToken cancellationToken = default)
    {
        var root = RequireMigrationRoot();
        var fullSourcePath = RequireContainedRegularFile(root, sourceCsvPath, "source");
        var fullCredentialPath = RequireContainedRegularFile(root, credentialDatabasePath, "credential database");
        var fullPlanOutputPath = RequireContainedNewFilePath(root, planOutputPath, "full-stage plan output");
        var fullReportOutputPath = RequireContainedNewFilePath(root, reportOutputPath, "full-stage report output");
        if (string.Equals(fullPlanOutputPath, fullReportOutputPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The RT rental full-stage plan and report paths must differ.");

        var normalizedDatabaseName = NormalizeFullStageDatabaseName(businessDatabaseName);
        _ = NormalizeRequiredProductionBaseUrl();
        var sourceSha256 = ComputeFileSha256(fullSourcePath);
        var credentialHashBefore = ComputeFileSha256(fullCredentialPath);
        var credentials = await ReadCredentialCandidatesAsync(fullCredentialPath, cancellationToken);
        var session = new SessionState();
        using var http = new HttpClient
        {
            BaseAddress = NormalizeRequiredProductionBaseUrl(),
            Timeout = TimeSpan.FromSeconds(120)
        };
        var api = new ErpApiClient(http, session);
        try
        {
            var selection = await SelectApprovedCredentialAsync(
                api,
                session,
                credentials,
                normalizedDatabaseName,
                cancellationToken);
            if (!selection.Selected)
                throw new InvalidOperationException(BuildCredentialSelectionFailureMessage(
                    selection.CandidateCount,
                    selection.LoginSucceededCount,
                    selection.RentalAssetEditAllowedCount,
                    selection.BusinessDatabaseSelectedCount));

            var snapshot = await PullRentalAdministrationAsync(api, normalizedDatabaseName, cancellationToken);
            var generatedAtUtc = DateTime.UtcNow;
            var planId = $"rt-full-{normalizedDatabaseName.ToLowerInvariant()}-{generatedAtUtc:yyyyMMddHHmmss}";
            var sourceRows = RtRentalDeltaPlanner.ReadSourceCsv(fullSourcePath);
            var build = RtRentalFullStagePlanner.BuildPlan(
                sourceRows,
                snapshot,
                normalizedDatabaseName,
                sourceSha256,
                planId,
                generatedAtUtc);
            ValidateFullStagePlan(build.Plan);
            var planBytes = JsonSerializer.SerializeToUtf8Bytes(build.Plan, WriteJsonOptions);
            var planSha256 = ComputeSha256(planBytes);
            var reportBytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    SchemaVersion = 2,
                    PlanId = build.Plan.PlanId,
                    build.Plan.BusinessDatabaseName,
                    build.Plan.GeneratedAtUtc,
                    SourceSha256 = sourceSha256,
                    PlanSha256 = planSha256,
                    ServerRevision = snapshot.CurrentServerRevision,
                    CredentialSelection = new
                    {
                        selection.CandidateCount,
                        selection.LoginSucceededCount,
                        selection.RentalAssetEditAllowedCount,
                        selection.BusinessDatabaseSelectedCount
                    },
                    Audit = build.Audit,
                    CustomerApprovalRequired = build.CustomerCandidates,
                    BillingProfileFeeApprovalRequired = build.BillingFeeCandidates,
                    BillingProfileReferenceApprovalRequired = build.BillingProfileReferenceCandidates
                },
                WriteJsonOptions);
            await WriteNewFileAsync(fullReportOutputPath, reportBytes, cancellationToken);
            await WriteNewFileAsync(fullPlanOutputPath, planBytes, cancellationToken);
            return new RtRentalFullStageGenerationResult(
                fullPlanOutputPath,
                fullReportOutputPath,
                planSha256,
                sourceSha256,
                normalizedDatabaseName,
                snapshot.CurrentServerRevision,
                selection.CandidateCount,
                selection.LoginSucceededCount,
                selection.RentalAssetEditAllowedCount,
                selection.BusinessDatabaseSelectedCount,
                build.Audit,
                build.CustomerCandidates.Count,
                build.BillingFeeCandidates.Count);
        }
        finally
        {
            if (!string.Equals(credentialHashBefore, ComputeFileSha256(fullCredentialPath), StringComparison.Ordinal))
                throw new InvalidOperationException("The migration credential snapshot changed while it was in use.");
        }
    }

    internal static Task<RtRentalDeltaRunResult> PreviewFullStageAsync(
        string planPath,
        string sourceCsvPath,
        string credentialDatabasePath,
        CancellationToken cancellationToken = default)
        => RunFullStageAsync(planPath, sourceCsvPath, credentialDatabasePath, apply: false, cancellationToken);

    internal static Task<RtRentalDeltaRunResult> ApplyFullStageAsync(
        string planPath,
        string sourceCsvPath,
        string credentialDatabasePath,
        CancellationToken cancellationToken = default)
        => RunFullStageAsync(planPath, sourceCsvPath, credentialDatabasePath, apply: true, cancellationToken);

    private static async Task<RtRentalDeltaRunResult> RunFullStageAsync(
        string planPath,
        string sourceCsvPath,
        string credentialDatabasePath,
        bool apply,
        CancellationToken cancellationToken)
    {
        if (apply)
            RequireExactEnvironment(ApplyEnvironmentKey, "1");
        var root = RequireMigrationRoot();
        var fullPlanPath = RequireContainedRegularFile(root, planPath, "full-stage plan");
        var fullSourcePath = RequireContainedRegularFile(root, sourceCsvPath, "source");
        var fullCredentialPath = RequireContainedRegularFile(root, credentialDatabasePath, "credential database");
        var planBytes = await File.ReadAllBytesAsync(fullPlanPath, cancellationToken);
        var planSha256 = ComputeSha256(planBytes);
        RequireExactEnvironment(PlanShaEnvironmentKey, planSha256);
        var plan = JsonSerializer.Deserialize<RtRentalFullStagePlan>(planBytes, JsonOptions)
                   ?? throw new InvalidDataException("The RT rental full-stage plan is empty or invalid.");
        ValidateFullStagePlan(plan);
        var sourceSha256 = ComputeFileSha256(fullSourcePath);
        if (!string.Equals(sourceSha256, plan.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The RT rental source snapshot hash does not match the approved full-stage plan.");

        var credentialHashBefore = ComputeFileSha256(fullCredentialPath);
        var credentials = await ReadCredentialCandidatesAsync(fullCredentialPath, cancellationToken);
        var session = new SessionState();
        using var http = new HttpClient
        {
            BaseAddress = NormalizeRequiredProductionBaseUrl(),
            Timeout = TimeSpan.FromSeconds(120)
        };
        var api = new ErpApiClient(http, session);
        try
        {
            var selection = await SelectApprovedCredentialAsync(
                api,
                session,
                credentials,
                plan.BusinessDatabaseName,
                cancellationToken);
            if (!selection.Selected)
                throw new InvalidOperationException(BuildCredentialSelectionFailureMessage(
                    selection.CandidateCount,
                    selection.LoginSucceededCount,
                    selection.RentalAssetEditAllowedCount,
                    selection.BusinessDatabaseSelectedCount));
            var before = await PullRentalAdministrationAsync(api, plan.BusinessDatabaseName, cancellationToken);
            var prepared = PrepareFullStageMutations(plan, planSha256, before.RentalAssets);
            if (!apply || prepared.Mutations.Count == 0)
            {
                return new RtRentalDeltaRunResult(
                    planSha256,
                    sourceSha256,
                    plan.BusinessDatabaseName,
                    plan.Entries.Count,
                    prepared.Mutations.Count,
                    0,
                    prepared.SkippedNoChangeCount,
                    before.CurrentServerRevision,
                    before.CurrentServerRevision);
            }

            var push = await api.PushAsync(
                           new SyncPushRequest
                           {
                               DeviceId = BuildDeviceId(plan.PlanId),
                               RentalAssets = prepared.Mutations
                           },
                           plan.BusinessDatabaseName,
                           cancellationToken)
                       ?? throw new InvalidDataException("The rental full-stage push returned an empty response.");
            RequireCompleteAcceptance(push, prepared.Mutations);
            var after = await PullRentalAdministrationAsync(api, plan.BusinessDatabaseName, cancellationToken);
            VerifyFullStageAppliedValues(
                plan,
                prepared.Mutations.Select(asset => asset.Id).ToHashSet(),
                after.RentalAssets,
                after.Customers);
            return new RtRentalDeltaRunResult(
                planSha256,
                sourceSha256,
                plan.BusinessDatabaseName,
                plan.Entries.Count,
                prepared.Mutations.Count,
                push.AcceptedCount,
                prepared.SkippedNoChangeCount,
                before.CurrentServerRevision,
                after.CurrentServerRevision);
        }
        finally
        {
            if (!string.Equals(credentialHashBefore, ComputeFileSha256(fullCredentialPath), StringComparison.Ordinal))
                throw new InvalidOperationException("The migration credential snapshot changed while it was in use.");
        }
    }

    internal static RtRentalPreparedMutations PrepareFullStageMutations(
        RtRentalFullStagePlan plan,
        string planSha256,
        IReadOnlyCollection<RentalAssetDto> currentAssets)
    {
        ValidateFullStagePlan(plan);
        ValidateSha256(planSha256, "full-stage plan");
        var currentById = currentAssets
            .GroupBy(asset => asset.Id)
            .ToDictionary(group => group.Key, group => group.Single());
        var activeByManagementNumber = currentAssets
            .Where(asset => !asset.IsDeleted && !string.IsNullOrWhiteSpace(asset.ManagementNumber))
            .GroupBy(asset => NormalizeFullStageText(asset.ManagementNumber), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var mutations = new List<RentalAssetDto>();
        var skipped = 0;
        foreach (var entry in plan.Entries.OrderBy(entry => entry.ExpectedManagementNumber, StringComparer.Ordinal))
        {
            if (string.Equals(entry.Operation, RtRentalFullStagePlanner.OperationUpdate, StringComparison.Ordinal))
            {
                if (!currentById.TryGetValue(entry.AssetId, out var current))
                    throw new InvalidDataException("An approved full-stage rental asset no longer exists.");
                ValidateFullStageCurrentAsset(entry, current);
                ApplyFullStageValues(current, entry.Values);
                AssertFullStageProtectedValues(entry, current);
                if (RtRentalFullStagePlanner.ValuesEqual(current, entry.Values))
                {
                    current.ExpectedRevision = current.Revision;
                    current.MutationId = BuildMutationId(planSha256, current.Id, current.Revision);
                    current.MutationCreatedAtUtc = EnsureUtc(plan.GeneratedAtUtc);
                    mutations.Add(current);
                }
                else
                {
                    throw new InvalidDataException("A full-stage update could not be prepared exactly.");
                }
                continue;
            }

            var key = NormalizeFullStageText(entry.ExpectedManagementNumber);
            if (activeByManagementNumber.TryGetValue(key, out var activeMatches) && activeMatches.Count > 0)
            {
                var exactPlanned = activeMatches.FirstOrDefault(asset => asset.Id == entry.AssetId);
                if (exactPlanned is not null && RtRentalFullStagePlanner.ValuesEqual(exactPlanned, entry.Values))
                {
                    skipped++;
                    continue;
                }
                throw new InvalidDataException("A management number reserved for full-stage create now exists.");
            }

            var createdAtUtc = EnsureUtc(plan.GeneratedAtUtc);
            var created = new RentalAssetDto
            {
                Id = entry.AssetId,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = createdAtUtc,
                Revision = 0,
                ExpectedRevision = 0,
                MutationId = BuildMutationId(planSha256, entry.AssetId, 0),
                MutationCreatedAtUtc = createdAtUtc,
                TenantCode = entry.ExpectedTenantCode,
                OfficeCode = entry.ExpectedOfficeCode,
                ResponsibleOfficeCode = entry.ExpectedResponsibleOfficeCode,
                ManagementCompanyCode = entry.ExpectedManagementCompanyCode,
                ManagementNumber = entry.ExpectedManagementNumber,
                CustomerId = null,
                CustomerName = string.Empty,
                CurrentCustomerName = string.Empty,
                BillingProfileId = null
            };
            ApplyFullStageValues(created, entry.Values);
            AssertFullStageProtectedValues(entry, created);
            mutations.Add(created);
        }
        return new RtRentalPreparedMutations(mutations, skipped);
    }

    private static void ValidateFullStageCurrentAsset(RtRentalFullStagePlanEntry entry, RentalAssetDto current)
    {
        if (current.IsDeleted ||
            current.Revision != entry.ExpectedRevision ||
            EnsureUtc(current.UpdatedAtUtc) != EnsureUtc(entry.ExpectedUpdatedAtUtc) ||
            !string.Equals(current.TenantCode, entry.ExpectedTenantCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.OfficeCode, entry.ExpectedOfficeCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.ResponsibleOfficeCode, entry.ExpectedResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.ManagementCompanyCode, entry.ExpectedManagementCompanyCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.ManagementNumber, entry.ExpectedManagementNumber, StringComparison.Ordinal) ||
            !string.Equals(current.AssetStatus, entry.ExpectedAssetStatus, StringComparison.Ordinal) ||
            current.CustomerId != entry.ExpectedCustomerId ||
            !string.Equals(current.CustomerName, entry.ExpectedCustomerName, StringComparison.Ordinal) ||
            !string.Equals(current.CurrentCustomerName, entry.ExpectedCurrentCustomerName, StringComparison.Ordinal) ||
            current.BillingProfileId != entry.ExpectedBillingProfileId)
        {
            throw new InvalidDataException("An approved full-stage rental asset changed after planning.");
        }
    }

    private static void ApplyFullStageValues(RentalAssetDto asset, RtRentalFullStageValues values)
    {
        asset.CurrentLocation = NormalizeFullStageText(values.CurrentLocation);
        asset.ItemCategoryName = NormalizeFullStageText(values.ItemCategoryName);
        asset.Manufacturer = NormalizeFullStageText(values.Manufacturer);
        asset.ItemName = NormalizeFullStageText(values.ItemName);
        asset.MachineNumber = NormalizeFullStageText(values.MachineNumber);
        asset.DisposalDate = values.DisposalDate;
        asset.InstallLocation = NormalizeFullStageText(values.InstallLocation);
        asset.MonthlyFee = values.MonthlyFee;
        asset.ContractMonths = values.ContractMonths;
        asset.ContractStartDate = values.ContractStartDate;
        asset.RentalEndDate = values.RentalEndDate;
        asset.AssetStatus = values.AssetStatus;
        asset.BillingEligibilityStatus = values.BillingEligibilityStatus;
        asset.BillingExclusionReason = values.BillingExclusionReason;
    }

    private static void AssertFullStageProtectedValues(RtRentalFullStagePlanEntry entry, RentalAssetDto asset)
    {
        if (!string.Equals(asset.TenantCode, entry.ExpectedTenantCode, StringComparison.Ordinal) ||
            !string.Equals(asset.OfficeCode, entry.ExpectedOfficeCode, StringComparison.Ordinal) ||
            !string.Equals(asset.ResponsibleOfficeCode, entry.ExpectedResponsibleOfficeCode, StringComparison.Ordinal) ||
            !string.Equals(asset.ManagementCompanyCode, entry.ExpectedManagementCompanyCode, StringComparison.Ordinal) ||
            !string.Equals(asset.ManagementNumber, entry.ExpectedManagementNumber, StringComparison.Ordinal) ||
            asset.CustomerId != entry.ExpectedCustomerId ||
            !string.Equals(asset.CustomerName, entry.ExpectedCustomerName, StringComparison.Ordinal) ||
            !string.Equals(asset.CurrentCustomerName, entry.ExpectedCurrentCustomerName, StringComparison.Ordinal) ||
            asset.BillingProfileId != entry.ExpectedBillingProfileId)
        {
            throw new InvalidDataException("The RT rental full-stage migration attempted a customer, profile, or scope mutation.");
        }
    }

    private static void VerifyFullStageAppliedValues(
        RtRentalFullStagePlan plan,
        IReadOnlySet<Guid> submittedIds,
        IReadOnlyCollection<RentalAssetDto> currentAssets,
        IReadOnlyCollection<CustomerDto> currentCustomers)
    {
        var currentById = currentAssets
            .GroupBy(asset => asset.Id)
            .ToDictionary(group => group.Key, group => group.Single());
        var activeCustomerNamesById = currentCustomers
            .Where(customer => !customer.IsDeleted)
            .GroupBy(customer => customer.Id)
            .ToDictionary(
                group => group.Key,
                group => NormalizeFullStageText(group.Single().NameOriginal));
        foreach (var entry in plan.Entries.Where(entry => submittedIds.Contains(entry.AssetId)))
        {
            if (!currentById.TryGetValue(entry.AssetId, out var current) || current.IsDeleted)
                throw new InvalidDataException("An accepted full-stage rental asset is missing from verification.");
            if (!FullStageProtectedValuesMatchAfterServer(entry, current, activeCustomerNamesById))
            {
                throw new InvalidDataException(
                    "The RT rental full-stage migration changed a customer link, profile, or scope during server verification.");
            }
            if (!RtRentalFullStagePlanner.ValuesEqual(current, entry.Values))
                throw new InvalidDataException("The server verification pull does not contain every approved full-stage value.");
        }
    }

    internal static bool FullStageProtectedValuesMatchAfterServer(
        RtRentalFullStagePlanEntry entry,
        RentalAssetDto asset,
        IReadOnlyDictionary<Guid, string> activeCustomerNamesById)
    {
        if (!string.Equals(asset.TenantCode, entry.ExpectedTenantCode, StringComparison.Ordinal) ||
            !string.Equals(asset.OfficeCode, entry.ExpectedOfficeCode, StringComparison.Ordinal) ||
            !string.Equals(asset.ResponsibleOfficeCode, entry.ExpectedResponsibleOfficeCode, StringComparison.Ordinal) ||
            !string.Equals(asset.ManagementCompanyCode, entry.ExpectedManagementCompanyCode, StringComparison.Ordinal) ||
            !string.Equals(asset.ManagementNumber, entry.ExpectedManagementNumber, StringComparison.Ordinal) ||
            asset.CustomerId != entry.ExpectedCustomerId ||
            asset.BillingProfileId != entry.ExpectedBillingProfileId)
        {
            return false;
        }

        if (string.Equals(asset.CustomerName, entry.ExpectedCustomerName, StringComparison.Ordinal) &&
            string.Equals(asset.CurrentCustomerName, entry.ExpectedCurrentCustomerName, StringComparison.Ordinal))
        {
            return true;
        }

        return entry.ExpectedCustomerId is Guid customerId &&
               customerId != Guid.Empty &&
               activeCustomerNamesById.TryGetValue(customerId, out var authoritativeName) &&
               !string.IsNullOrWhiteSpace(authoritativeName) &&
               string.Equals(
                   NormalizeFullStageText(asset.CustomerName),
                   authoritativeName,
                   StringComparison.Ordinal) &&
               string.Equals(
                   NormalizeFullStageText(asset.CurrentCustomerName),
                   authoritativeName,
                   StringComparison.Ordinal);
    }

    private static void ValidateFullStagePlan(RtRentalFullStagePlan plan)
    {
        if (plan.SchemaVersion != 2)
            throw new InvalidDataException("Unsupported RT rental full-stage plan schema.");
        if (string.IsNullOrWhiteSpace(plan.PlanId) || plan.PlanId.Length > 64 ||
            plan.PlanId.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
        {
            throw new InvalidDataException("The RT rental full-stage plan ID is invalid.");
        }
        var normalizedDatabaseName = NormalizeFullStageDatabaseName(plan.BusinessDatabaseName);
        if (!string.Equals(normalizedDatabaseName, plan.BusinessDatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The RT rental full-stage database name is not canonical.");
        ValidateSha256(plan.SourceSha256, "source");
        if (plan.GeneratedAtUtc == default ||
            EnsureUtc(plan.GeneratedAtUtc) > DateTime.UtcNow.AddMinutes(5) ||
            EnsureUtc(plan.GeneratedAtUtc) < DateTime.UtcNow.AddDays(-7))
        {
            throw new InvalidDataException("The RT rental full-stage plan timestamp is outside the allowed window.");
        }
        if (plan.Entries.Count > 1200 ||
            plan.Entries.Select(entry => entry.AssetId).Distinct().Count() != plan.Entries.Count ||
            plan.Entries.Select(entry => entry.ExpectedManagementNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Entries.Count)
        {
            throw new InvalidDataException("The RT rental full-stage entry set is too large or duplicated.");
        }
        foreach (var entry in plan.Entries)
        {
            var isUpdate = string.Equals(entry.Operation, RtRentalFullStagePlanner.OperationUpdate, StringComparison.Ordinal);
            var isCreate = string.Equals(entry.Operation, RtRentalFullStagePlanner.OperationCreate, StringComparison.Ordinal);
            if ((!isUpdate && !isCreate) ||
                entry.AssetId == Guid.Empty ||
                string.IsNullOrWhiteSpace(entry.ExpectedTenantCode) ||
                string.IsNullOrWhiteSpace(entry.ExpectedOfficeCode) ||
                string.IsNullOrWhiteSpace(entry.ExpectedResponsibleOfficeCode) ||
                string.IsNullOrWhiteSpace(entry.ExpectedManagementCompanyCode) ||
                string.IsNullOrWhiteSpace(entry.ExpectedManagementNumber) ||
                entry.Values.MonthlyFee < 0 ||
                entry.Values.ContractMonths < 0 ||
                !RtRentalFullStagePlanner.TryMapSourceStatus(
                    entry.Values.AssetStatus switch
                    {
                        RentalAssetStatusNormalizer.Active => "렌탈",
                        RentalAssetStatusNormalizer.Warehouse => "창고",
                        RentalAssetStatusNormalizer.Sold => "판매",
                        RentalAssetStatusNormalizer.Disposed => "폐기",
                        _ => string.Empty
                    },
                    out _))
            {
                throw new InvalidDataException("An RT rental full-stage plan entry is incomplete or invalid.");
            }
            if (isUpdate && (entry.ExpectedRevision <= 0 || entry.ExpectedUpdatedAtUtc == default || string.IsNullOrWhiteSpace(entry.ExpectedAssetStatus)))
                throw new InvalidDataException("An RT rental full-stage update is missing concurrency guards.");
            if (isCreate && (entry.ExpectedRevision != 0 || entry.ExpectedUpdatedAtUtc != default ||
                             entry.ExpectedCustomerId.HasValue || !string.IsNullOrEmpty(entry.ExpectedCustomerName) ||
                             !string.IsNullOrEmpty(entry.ExpectedCurrentCustomerName) || entry.ExpectedBillingProfileId.HasValue))
            {
                throw new InvalidDataException("An RT rental full-stage create attempted a customer or billing-profile link.");
            }
        }
    }

    private static string NormalizeFullStageDatabaseName(string businessDatabaseName)
    {
        var normalized = TenantScopeCatalog.GetDatabaseName(businessDatabaseName);
        if (!string.Equals(normalized, TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.UsenetGroup), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalized, TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The RT rental full-stage plan targets an unsupported business database.");
        }
        return normalized;
    }

    private static string NormalizeFullStageText(string? value)
        => (value ?? string.Empty).Trim();
}
