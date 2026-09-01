using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Tools.SyncDiag;

internal sealed class RtRentalResolutionPlan
{
    public int SchemaVersion { get; set; }
    public string PlanId { get; set; } = string.Empty;
    public string BusinessDatabaseName { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string ExpectedSnapshotSha256 { get; set; } = string.Empty;
    public long ExpectedServerRevision { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public List<RtRentalResolutionEntity<CustomerDto>> Customers { get; set; } = [];
    public List<RtRentalResolutionEntity<RentalBillingProfileDto>> BillingProfiles { get; set; } = [];
    public List<RtRentalResolutionEntity<RentalAssetDto>> Assets { get; set; } = [];
    public List<RtRentalResolutionEntity<RentalAssetAssignmentHistoryDto>> AssignmentHistories { get; set; } = [];
    public List<RtRentalResolutionDecision> Decisions { get; set; } = [];
    public RtRentalResolutionAudit Audit { get; set; } = new();
}

internal sealed class RtRentalResolutionEntity<T>
    where T : SyncEntityDto
{
    public string Operation { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string ExpectedEntitySha256 { get; set; } = string.Empty;
    public T Desired { get; set; } = default!;
}

internal sealed class RtRentalResolutionDecision
{
    public string BusinessDatabaseName { get; set; } = string.Empty;
    public string ManagementNumber { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string RtStatus { get; set; } = string.Empty;
    public string RtCustomerName { get; set; } = string.Empty;
    public string PreviousValue { get; set; } = string.Empty;
    public string ResolvedValue { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

internal sealed class RtRentalResolutionAudit
{
    public int SourceRowCount { get; set; }
    public int TargetSourceRowCount { get; set; }
    public int CustomerAliasKeptCount { get; set; }
    public int CustomerChangedCount { get; set; }
    public int CustomerCreatedCount { get; set; }
    public int ActiveRtBlankCustomerPreservedCount { get; set; }
    public int NonOperatingAssignmentClearedCount { get; set; }
    public int BillingProfilePreservedFeeCount { get; set; }
    public int ManualFeePreservedCount { get; set; }
    public int VatInclusiveFeeAdjustedCount { get; set; }
    public int BillingProfileTrimmedCount { get; set; }
    public int BillingProfileDeactivatedCount { get; set; }
    public int PlannedCustomerCreateCount { get; set; }
    public int PlannedBillingProfileUpdateCount { get; set; }
    public int PlannedAssetCreateCount { get; set; }
    public int PlannedAssetUpdateCount { get; set; }
    public int PlannedAssignmentHistoryCreateCount { get; set; }
    public int PlannedAssignmentHistoryUpdateCount { get; set; }
    public int PlannedEntityCount { get; set; }
}

internal sealed record RtRentalResolutionBuildResult(
    RtRentalResolutionPlan Plan,
    string SnapshotSha256);

internal sealed record RtRentalResolutionGenerationResult(
    string PlanPath,
    string ReportPath,
    string PlanSha256,
    string SourceSha256,
    string SnapshotSha256,
    string BusinessDatabaseName,
    long ServerRevision,
    int CredentialCandidateCount,
    int LoginSucceededCount,
    int RentalAssetEditAllowedCount,
    int BusinessDatabaseSelectedCount,
    RtRentalResolutionAudit Audit);

internal sealed record RtRentalResolutionRunResult(
    string PlanSha256,
    string SourceSha256,
    string BusinessDatabaseName,
    int PlannedCount,
    int SubmittedCustomerCount,
    int SubmittedProfileCount,
    int SubmittedAssetCount,
    int SubmittedAssignmentHistoryCount,
    int AcceptedCount,
    int SkippedNoChangeCount,
    long ServerRevisionBefore,
    long ServerRevisionAfter,
    string SnapshotSha256Before,
    string SnapshotSha256After,
    string ProtectedFinancialSha256Before,
    string ProtectedFinancialSha256After);

internal sealed record RtRentalResolutionPrepared(
    List<CustomerDto> Customers,
    List<RentalBillingProfileDto> Profiles,
    List<RentalAssetDto> Assets,
    List<RentalAssetAssignmentHistoryDto> AssignmentHistories,
    int SkippedNoChangeCount);

internal static class RtRentalResolutionPlanner
{
    internal const string OperationCreate = "Create";
    internal const string OperationUpdate = "Update";
    private const string Active = "임대진행중";
    private const string BillingTarget = "청구대상";
    private const string BillingExcluded = "청구제외";
    private const string BillingUnconfirmed = "미확인";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    internal static RtRentalResolutionBuildResult BuildPlan(
        IReadOnlyCollection<RtRentalSourceRow> allSourceRows,
        SyncPullResponse snapshot,
        string businessDatabaseName,
        string sourceSha256,
        string planId,
        DateTime generatedAtUtc)
    {
        var databaseName = NormalizeDatabaseName(businessDatabaseName);
        var companyCode = databaseName == TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld)
            ? OfficeCodeCatalog.Itworld
            : OfficeCodeCatalog.Usenet;
        var sourceCompany = companyCode == OfficeCodeCatalog.Itworld ? "아이티월드" : "유즈넷";
        var tenantCode = companyCode == OfficeCodeCatalog.Itworld
            ? TenantScopeCatalog.Itworld
            : TenantScopeCatalog.UsenetGroup;
        var generatedUtc = EnsureUtc(generatedAtUtc);
        var targetRows = allSourceRows
            .Where(row => string.Equals(NormalizeSimple(row.ManagementCompany), sourceCompany, StringComparison.Ordinal))
            .OrderBy(row => NormalizeSimple(row.ManagementNumber), StringComparer.Ordinal)
            .ToList();
        var duplicateSourceKeys = targetRows
            .GroupBy(row => NormalizeSimple(row.ManagementNumber), StringComparer.Ordinal)
            .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() != 1)
            .ToList();
        if (duplicateSourceKeys.Count != 0)
            throw new InvalidDataException("The RT rental resolution source has blank or duplicate management numbers.");

        var currentAssets = snapshot.RentalAssets
            .Where(asset => !asset.IsDeleted &&
                            string.Equals(asset.ManagementCompanyCode, companyCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var duplicateAssetKeys = currentAssets
            .GroupBy(asset => NormalizeSimple(asset.ManagementNumber), StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .ToList();
        if (duplicateAssetKeys.Count != 0)
            throw new InvalidDataException("The selected TradePlan database has duplicate active rental management numbers.");

        var assetsByNumber = currentAssets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.ManagementNumber))
            .ToDictionary(asset => NormalizeSimple(asset.ManagementNumber), StringComparer.Ordinal);
        var activeCustomers = snapshot.Customers.Where(customer => !customer.IsDeleted).ToList();
        var customersById = activeCustomers.ToDictionary(customer => customer.Id);
        var customersByCanonicalKey = activeCustomers
            .GroupBy(customer => CanonicalCustomerKey(customer.NameOriginal), StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var profilesById = snapshot.RentalBillingProfiles
            .Where(profile => !profile.IsDeleted)
            .ToDictionary(profile => profile.Id);
        var explicitProfilesByAssetId = BuildExplicitProfileLookup(profilesById.Values);
        var plan = new RtRentalResolutionPlan
        {
            SchemaVersion = 1,
            PlanId = planId,
            BusinessDatabaseName = databaseName,
            SourceSha256 = sourceSha256,
            ExpectedSnapshotSha256 = ComputeSnapshotSha256(snapshot),
            ExpectedServerRevision = snapshot.CurrentServerRevision,
            GeneratedAtUtc = generatedUtc,
            Audit = new RtRentalResolutionAudit
            {
                SourceRowCount = allSourceRows.Count,
                TargetSourceRowCount = targetRows.Count
            }
        };
        var createdCustomersByCanonicalKey = new Dictionary<string, CustomerDto>(StringComparer.Ordinal);
        var departingProfileAssetIds = new Dictionary<Guid, HashSet<Guid>>();

        foreach (var source in targetRows)
        {
            var managementNumber = NormalizeSimple(source.ManagementNumber);
            if (!RtRentalFullStagePlanner.TryMapSourceStatus(source.Status, out var targetStatus))
                throw new InvalidDataException($"Unsupported RT rental status at management number {managementNumber}.");
            var isActive = string.Equals(targetStatus, Active, StringComparison.Ordinal);
            var current = assetsByNumber.GetValueOrDefault(managementNumber);
            var desired = current is null
                ? CreateAsset(source, companyCode, tenantCode, targetStatus, generatedUtc)
                : Clone(current);
            ApplyRtScalarValues(desired, source, targetStatus, current);

            CustomerDto? resolvedCustomer = null;
            var rtCustomerName = NormalizeCustomerDisplay(source.CustomerName);
            var previousCustomerName = current is null
                ? string.Empty
                : FirstNonBlank(current.CurrentCustomerName, current.CustomerName);
            var customerChanged = false;
            if (isActive)
            {
                if (string.IsNullOrWhiteSpace(rtCustomerName))
                {
                    if (current?.CustomerId is Guid existingCustomerId &&
                        customersById.TryGetValue(existingCustomerId, out var existingCustomer))
                    {
                        resolvedCustomer = existingCustomer;
                        plan.Audit.ActiveRtBlankCustomerPreservedCount++;
                        AddDecision(plan, managementNumber, "거래처", source, previousCustomerName,
                            existingCustomer.NameOriginal, "현재 거래처 유지",
                            "RT 고객명이 비어 있어 기존의 유효한 거래처 연결을 보존했습니다.");
                    }
                }
                else
                {
                    var currentCustomer = current?.CustomerId is Guid currentCustomerId &&
                                          customersById.TryGetValue(currentCustomerId, out var foundCurrent)
                        ? foundCurrent
                        : null;
                    if (currentCustomer is not null &&
                        CustomerNamesEquivalent(rtCustomerName, currentCustomer.NameOriginal))
                    {
                        resolvedCustomer = currentCustomer;
                        if (!string.Equals(
                                NormalizeCustomerDisplay(rtCustomerName),
                                NormalizeCustomerDisplay(currentCustomer.NameOriginal),
                                StringComparison.Ordinal))
                        {
                            plan.Audit.CustomerAliasKeptCount++;
                            AddDecision(plan, managementNumber, "거래처", source, previousCustomerName,
                                currentCustomer.NameOriginal, "표기 차이로 현재 거래처 유지",
                                "기관명 순서, 괄호, 행정기관 접두어 또는 법인 표기만 달라 같은 거래처로 판정했습니다.");
                        }
                    }
                    else
                    {
                        resolvedCustomer = ResolveExistingCustomer(
                            rtCustomerName,
                            current,
                            customersByCanonicalKey);
                        if (resolvedCustomer is null)
                        {
                            var key = CanonicalCustomerKey(rtCustomerName);
                            if (!createdCustomersByCanonicalKey.TryGetValue(key, out resolvedCustomer))
                            {
                                resolvedCustomer = CreateCustomer(
                                    databaseName,
                                    tenantCode,
                                    companyCode,
                                    rtCustomerName,
                                    generatedUtc);
                                createdCustomersByCanonicalKey[key] = resolvedCustomer;
                                plan.Customers.Add(new RtRentalResolutionEntity<CustomerDto>
                                {
                                    Operation = OperationCreate,
                                    EntityId = resolvedCustomer.Id,
                                    Desired = resolvedCustomer
                                });
                                plan.Audit.CustomerCreatedCount++;
                                plan.Audit.PlannedCustomerCreateCount++;
                            }
                        }

                        customerChanged = current?.CustomerId != resolvedCustomer.Id;
                        if (customerChanged)
                        {
                            plan.Audit.CustomerChangedCount++;
                            AddDecision(plan, managementNumber, "거래처", source, previousCustomerName,
                                resolvedCustomer.NameOriginal,
                                current is null ? "신규 자산 거래처 연결" : "RT 최신 거래처로 변경",
                                "RT 고객명과 기존 고객명이 실제로 달라 RT 명칭의 기존 거래처를 연결하거나 신규 거래처를 만들었습니다.");
                        }
                    }
                }
            }

            var existingProfile = current?.BillingProfileId is Guid profileId &&
                                  profilesById.TryGetValue(profileId, out var foundProfile)
                ? foundProfile
                : null;
            var profileCanBePreserved = isActive && existingProfile is not null &&
                                        (resolvedCustomer is null ||
                                         !existingProfile.CustomerId.HasValue ||
                                         existingProfile.CustomerId == resolvedCustomer.Id) &&
                                        !customerChanged;
            var preservedProfile = profileCanBePreserved ? existingProfile : null;
            if (isActive)
            {
                ApplyActiveAssignment(desired, source, current, resolvedCustomer, preservedProfile, generatedUtc);
                if (existingProfile is not null && !profileCanBePreserved)
                    AddDeparture(departingProfileAssetIds, existingProfile.Id, desired.Id);
                foreach (var explicitProfile in explicitProfilesByAssetId.GetValueOrDefault(desired.Id) ?? [])
                {
                    if (preservedProfile?.Id != explicitProfile.Id)
                        AddDeparture(departingProfileAssetIds, explicitProfile.Id, desired.Id);
                }
                ApplyActiveFee(desired, source, current, preservedProfile, plan, managementNumber);
            }
            else
            {
                var hadAssignment = current is not null &&
                                    (current.CustomerId.HasValue || current.BillingProfileId.HasValue ||
                                     !string.IsNullOrWhiteSpace(previousCustomerName) ||
                                     !string.IsNullOrWhiteSpace(current.InstallLocation));
                ApplyNonOperatingAssignment(desired, source, current, existingProfile, generatedUtc);
                if (hadAssignment)
                {
                    plan.Audit.NonOperatingAssignmentClearedCount++;
                    AddDecision(plan, managementNumber, "운용상태", source, previousCustomerName,
                        targetStatus, "비운용 장비 배정 정리",
                        "RT 상태가 계약종료/창고/판매/폐기이므로 현재 거래처와 청구 프로필은 해제하고 마지막 배정값을 보존했습니다.");
                }
                if (existingProfile is not null)
                    AddDeparture(departingProfileAssetIds, existingProfile.Id, desired.Id);
                foreach (var explicitProfile in explicitProfilesByAssetId.GetValueOrDefault(desired.Id) ?? [])
                    AddDeparture(departingProfileAssetIds, explicitProfile.Id, desired.Id);
            }

            AddAssetEntryIfChanged(plan, current, desired);
        }

        BuildProfileEntries(plan, snapshot, departingProfileAssetIds);
        BuildAssignmentHistoryEntries(plan, snapshot, profilesById, generatedUtc);
        plan.Customers = plan.Customers.OrderBy(entry => entry.Desired.NameOriginal, StringComparer.CurrentCultureIgnoreCase).ToList();
        plan.BillingProfiles = plan.BillingProfiles.OrderBy(entry => entry.Desired.ProfileKey, StringComparer.Ordinal).ToList();
        plan.Assets = plan.Assets.OrderBy(entry => entry.Desired.ManagementNumber, StringComparer.Ordinal).ToList();
        plan.AssignmentHistories = plan.AssignmentHistories
            .OrderBy(entry => entry.Desired.AssetId)
            .ThenBy(entry => entry.Desired.LinkedAtUtc)
            .ToList();
        plan.Decisions = plan.Decisions
            .OrderBy(decision => decision.Category, StringComparer.CurrentCulture)
            .ThenBy(decision => decision.ManagementNumber, StringComparer.Ordinal)
            .ToList();
        plan.Audit.PlannedEntityCount = plan.Customers.Count + plan.BillingProfiles.Count +
                                        plan.Assets.Count + plan.AssignmentHistories.Count;
        return new RtRentalResolutionBuildResult(plan, plan.ExpectedSnapshotSha256);
    }

    internal static RtRentalResolutionBuildResult BuildProfileUnlinkPlan(
        IReadOnlyCollection<RtRentalSourceRow> allSourceRows,
        SyncPullResponse snapshot,
        string businessDatabaseName,
        string sourceSha256,
        string planId,
        DateTime generatedAtUtc)
    {
        var finalBuild = BuildPlan(
            allSourceRows,
            snapshot,
            businessDatabaseName,
            sourceSha256,
            planId,
            generatedAtUtc);
        var finalPlan = finalBuild.Plan;
        var finalAssetsById = finalPlan.Assets.ToDictionary(entry => entry.EntityId);
        var currentProfilesById = snapshot.RentalBillingProfiles
            .Where(profile => !profile.IsDeleted)
            .ToDictionary(profile => profile.Id);
        var currentAssetsByProfileId = snapshot.RentalAssets
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue)
            .GroupBy(asset => asset.BillingProfileId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        var plan = new RtRentalResolutionPlan
        {
            SchemaVersion = finalPlan.SchemaVersion,
            PlanId = finalPlan.PlanId,
            BusinessDatabaseName = finalPlan.BusinessDatabaseName,
            SourceSha256 = finalPlan.SourceSha256,
            ExpectedSnapshotSha256 = finalPlan.ExpectedSnapshotSha256,
            ExpectedServerRevision = finalPlan.ExpectedServerRevision,
            GeneratedAtUtc = finalPlan.GeneratedAtUtc,
            Audit = new RtRentalResolutionAudit
            {
                SourceRowCount = finalPlan.Audit.SourceRowCount,
                TargetSourceRowCount = finalPlan.Audit.TargetSourceRowCount,
                BillingProfileTrimmedCount = finalPlan.Audit.BillingProfileTrimmedCount,
                BillingProfileDeactivatedCount = finalPlan.Audit.BillingProfileDeactivatedCount
            }
        };

        foreach (var profileEntry in finalPlan.BillingProfiles.OrderBy(entry => entry.EntityId))
        {
            if (!currentProfilesById.ContainsKey(profileEntry.EntityId))
                continue;
            var safeUnlinks = new List<RentalAssetDto>();
            foreach (var currentAsset in currentAssetsByProfileId.GetValueOrDefault(profileEntry.EntityId) ?? [])
            {
                if (RentalBillingTemplateAssetCoverageRules.Evaluate(
                        profileEntry.Desired.BillingTemplateJson,
                        currentAsset.Id) == RentalBillingTemplateAssetCoverage.UniqueReference)
                {
                    continue;
                }
                if (!finalAssetsById.TryGetValue(currentAsset.Id, out var finalAssetEntry) ||
                    finalAssetEntry.Desired.BillingProfileId.HasValue)
                {
                    throw new InvalidDataException(
                        $"Profile {profileEntry.EntityId:D} removes asset {currentAsset.Id:D} without a final unlink mutation.");
                }
                var unlink = Clone(currentAsset);
                unlink.BillingProfileId = null;
                safeUnlinks.Add(unlink);
            }

            plan.BillingProfiles.Add(profileEntry);
            plan.Audit.PlannedBillingProfileUpdateCount++;
            foreach (var unlink in safeUnlinks)
            {
                if (plan.Assets.Any(entry => entry.EntityId == unlink.Id))
                    continue;
                var current = snapshot.RentalAssets.Single(asset => asset.Id == unlink.Id);
                plan.Assets.Add(new RtRentalResolutionEntity<RentalAssetDto>
                {
                    Operation = OperationUpdate,
                    EntityId = unlink.Id,
                    ExpectedEntitySha256 = ComputeEntitySha256(current),
                    Desired = unlink
                });
                plan.Audit.PlannedAssetUpdateCount++;
                AddDecision(
                    plan,
                    unlink.ManagementNumber,
                    "청구프로필",
                    null,
                    profileEntry.Desired.ProfileKey,
                    "연결 없음",
                    "프로필 선행 해제",
                    "서버 보호 규칙에 맞춰 고객·상태를 유지한 채 청구 프로필 연결만 먼저 해제합니다.");
            }
        }

        plan.BillingProfiles = plan.BillingProfiles
            .OrderBy(entry => entry.Desired.ProfileKey, StringComparer.Ordinal)
            .ToList();
        plan.Assets = plan.Assets
            .OrderBy(entry => entry.Desired.ManagementNumber, StringComparer.Ordinal)
            .ToList();
        plan.Decisions = plan.Decisions
            .OrderBy(decision => decision.ManagementNumber, StringComparer.Ordinal)
            .ToList();
        plan.Audit.PlannedEntityCount = plan.BillingProfiles.Count + plan.Assets.Count;
        return new RtRentalResolutionBuildResult(plan, plan.ExpectedSnapshotSha256);
    }

    private static void ApplyRtScalarValues(
        RentalAssetDto desired,
        RtRentalSourceRow source,
        string targetStatus,
        RentalAssetDto? current)
    {
        desired.ItemCategoryName = PreferSource(source.ItemCategoryName, desired.ItemCategoryName);
        desired.ItemName = PreferSource(source.ItemName, desired.ItemName);
        desired.Manufacturer = PreferSource(source.Manufacturer, desired.Manufacturer);
        desired.MachineNumber = PreferSource(source.MachineNumber, desired.MachineNumber);
        desired.ContractMonths = ParseMonths(source.ContractMonthsText) ?? desired.ContractMonths;
        desired.ContractStartDate = ParseDate(source.ContractStartDate) ?? desired.ContractStartDate;
        desired.RentalEndDate = ParseDate(source.RentalEndDate) ?? desired.RentalEndDate;
        desired.DisposalDate = ParseDate(source.DisposalDate) ?? desired.DisposalDate;
        desired.AssetStatus = targetStatus;
        if (string.Equals(targetStatus, RentalAssetStatusNormalizer.Warehouse, StringComparison.Ordinal))
            desired.CurrentLocation = RentalAssetStatusNormalizer.Warehouse;
        else if (string.Equals(targetStatus, RentalAssetStatusNormalizer.Sold, StringComparison.Ordinal))
            desired.CurrentLocation = RentalAssetStatusNormalizer.Sold;
        else if (string.Equals(targetStatus, RentalAssetStatusNormalizer.Disposed, StringComparison.Ordinal))
            desired.CurrentLocation = RentalAssetStatusNormalizer.Disposed;
        else if (current is null || RentalAssetStatusNormalizer.IsNonOperating(current.AssetStatus))
            desired.CurrentLocation = PreferSource(source.InstallLocation, current?.CurrentLocation ?? desired.CurrentLocation);
    }

    private static void ApplyActiveAssignment(
        RentalAssetDto desired,
        RtRentalSourceRow source,
        RentalAssetDto? current,
        CustomerDto? customer,
        RentalBillingProfileDto? profile,
        DateTime generatedAtUtc)
    {
        var installLocation = PreferSource(source.InstallLocation, current?.InstallLocation ?? desired.InstallLocation);
        if (customer is not null && current?.CustomerId != customer.Id)
        {
            desired.LastCustomerName = FirstNonBlank(current?.CurrentCustomerName, current?.CustomerName, desired.LastCustomerName);
            desired.LastInstallLocation = FirstNonBlank(current?.InstallLocation, current?.InstallSiteName, desired.LastInstallLocation);
            desired.LastBillingProfileId = current?.BillingProfileId ?? desired.LastBillingProfileId;
            desired.LastAssignmentClearedAtUtc = generatedAtUtc;
        }
        var targetCustomerId = customer?.Id ?? current?.CustomerId;
        desired.CustomerId = targetCustomerId;
        var keepsExistingCustomer = current is not null && current.CustomerId == targetCustomerId;
        var authoritativeName = keepsExistingCustomer
            ? FirstNonBlank(current!.CurrentCustomerName, current.CustomerName, customer?.NameOriginal)
            : customer?.NameOriginal ?? FirstNonBlank(current?.CurrentCustomerName, current?.CustomerName);
        desired.CustomerName = authoritativeName;
        desired.CurrentCustomerName = authoritativeName;
        desired.InstallSiteName = installLocation;
        desired.InstallLocation = installLocation;
        if (current is null || RentalAssetStatusNormalizer.IsNonOperating(current.AssetStatus))
            desired.CurrentLocation = installLocation;
        desired.BillingProfileId = profile?.Id;
        if (profile is not null)
        {
            if (current is null ||
                (RentalAssetStatusNormalizer.IsNonOperating(current.AssetStatus) &&
                 NormalizeSimple(current.BillingExclusionReason).StartsWith("자산상태:", StringComparison.Ordinal)))
            {
                desired.BillingEligibilityStatus = BillingTarget;
                desired.BillingExclusionReason = string.Empty;
            }
        }
        else if (current is null || current.BillingProfileId.HasValue ||
                 (RentalAssetStatusNormalizer.IsNonOperating(current.AssetStatus) &&
                  NormalizeSimple(current.BillingExclusionReason).StartsWith("자산상태:", StringComparison.Ordinal)))
        {
            desired.BillingEligibilityStatus = BillingUnconfirmed;
            desired.BillingExclusionReason = string.Empty;
        }
    }

    private static void ApplyNonOperatingAssignment(
        RentalAssetDto desired,
        RtRentalSourceRow source,
        RentalAssetDto? current,
        RentalBillingProfileDto? profile,
        DateTime generatedAtUtc)
    {
        desired.LastCustomerName = FirstNonBlank(
            current?.CurrentCustomerName,
            current?.CustomerName,
            desired.LastCustomerName,
            NormalizeCustomerDisplay(source.CustomerName));
        desired.LastInstallLocation = FirstNonBlank(
            current?.InstallLocation,
            current?.InstallSiteName,
            desired.LastInstallLocation,
            NormalizeSourceText(source.InstallLocation));
        desired.LastBillingProfileId = current?.BillingProfileId ?? desired.LastBillingProfileId;
        desired.LastBillingProfileDisplay = FirstNonBlank(
            profile?.ProfileKey,
            profile?.CustomerName,
            desired.LastBillingProfileDisplay);
        if ((current?.CustomerId).HasValue || (current?.BillingProfileId).HasValue ||
            !string.IsNullOrWhiteSpace(current?.CurrentCustomerName) ||
            !string.IsNullOrWhiteSpace(current?.InstallLocation))
        {
            desired.LastAssignmentClearedAtUtc ??= generatedAtUtc;
        }
        desired.CustomerId = null;
        desired.CustomerName = string.Empty;
        desired.CurrentCustomerName = string.Empty;
        desired.InstallSiteName = string.Empty;
        desired.InstallLocation = string.Empty;
        desired.BillingProfileId = null;
        desired.BillingEligibilityStatus = BillingExcluded;
        desired.BillingExclusionReason = $"자산상태: {desired.AssetStatus}";
    }

    private static void ApplyActiveFee(
        RentalAssetDto desired,
        RtRentalSourceRow source,
        RentalAssetDto? current,
        RentalBillingProfileDto? preservedProfile,
        RtRentalResolutionPlan plan,
        string managementNumber)
    {
        var rawFee = ParseFee(source.MonthlyFeeText);
        if (preservedProfile is not null)
        {
            desired.MonthlyFee = current?.MonthlyFee ?? desired.MonthlyFee;
            if (rawFee.HasValue && rawFee.Value != desired.MonthlyFee)
            {
                plan.Audit.BillingProfilePreservedFeeCount++;
                AddDecision(plan, managementNumber, "청구금액", source,
                    (current?.MonthlyFee ?? 0).ToString("N0", CultureInfo.InvariantCulture),
                    desired.MonthlyFee.ToString("N0", CultureInfo.InvariantCulture),
                    "프로필 금액 유지",
                    "거래플랜 청구 프로필 금액은 부가세 포함 실제 청구구조이므로 RT 원시 금액으로 덮어쓰지 않았습니다.");
            }
            return;
        }
        if (!rawFee.HasValue)
        {
            desired.MonthlyFee = current?.MonthlyFee ?? desired.MonthlyFee;
            return;
        }
        if (rawFee.Value == 0)
        {
            desired.MonthlyFee = current is not null && current.MonthlyFee == 0
                ? current.MonthlyFee
                : 0;
            return;
        }
        var currentFee = current?.MonthlyFee ?? 0;
        desired.MonthlyFee = current is not null && currentFee == rawFee.Value
            ? currentFee
            : rawFee.Value;
        if (current is null || currentFee != rawFee.Value)
        {
            AddDecision(plan, managementNumber, "청구금액", source,
                currentFee.ToString("N0", CultureInfo.InvariantCulture),
                rawFee.Value.ToString("N0", CultureInfo.InvariantCulture),
                "RT 원시 금액 반영",
                "청구 프로필이 없는 자산은 RT 최신 월임대료를 그대로 반영했습니다. 부가세 포함 실제 청구액은 향후 청구 프로필에서 관리합니다.");
        }
    }

    private static void BuildProfileEntries(
        RtRentalResolutionPlan plan,
        SyncPullResponse snapshot,
        IReadOnlyDictionary<Guid, HashSet<Guid>> departingProfileAssetIds)
    {
        var currentAssets = snapshot.RentalAssets.Where(asset => !asset.IsDeleted).ToList();
        foreach (var pair in departingProfileAssetIds.OrderBy(pair => pair.Key))
        {
            var current = snapshot.RentalBillingProfiles.FirstOrDefault(profile => !profile.IsDeleted && profile.Id == pair.Key);
            if (current is null)
                continue;
            var desired = Clone(current);
            var survivingLinkedAssets = currentAssets
                .Where(asset => asset.BillingProfileId == current.Id && !pair.Value.Contains(asset.Id))
                .ToList();
            if (survivingLinkedAssets.Count == 0)
            {
                desired.IsActive = false;
                desired.BillingTemplateJson = RemoveAssetIdsFromTemplate(
                    current.BillingTemplateJson,
                    pair.Value,
                    out _);
                desired.MonthlyAmount = current.MonthlyAmount;
                plan.Audit.BillingProfileDeactivatedCount++;
                AddDecision(plan, string.Empty, "청구프로필", null,
                    current.ProfileKey, current.ProfileKey, "프로필 비활성화",
                    "RT 기준 운용 장비가 남지 않아 자동청구와 재연결을 막도록 프로필을 비활성화했습니다.");
            }
            else
            {
                desired.BillingTemplateJson = RemoveAssetIdsFromTemplate(
                    current.BillingTemplateJson,
                    pair.Value,
                    out var recalculatedAmount);
                desired.MonthlyAmount = recalculatedAmount;
                plan.Audit.BillingProfileTrimmedCount++;
                AddDecision(plan, string.Empty, "청구프로필", null,
                    current.ProfileKey, current.ProfileKey, "프로필 장비 범위 정리",
                    $"비운용 또는 고객이 변경된 장비 {pair.Value.Count:N0}대를 청구 템플릿에서 제외했습니다.");
            }
            if (EntityBusinessEquals(current, desired))
                continue;
            plan.BillingProfiles.Add(new RtRentalResolutionEntity<RentalBillingProfileDto>
            {
                Operation = OperationUpdate,
                EntityId = current.Id,
                ExpectedEntitySha256 = ComputeEntitySha256(current),
                Desired = desired
            });
            plan.Audit.PlannedBillingProfileUpdateCount++;
        }
    }

    private static void BuildAssignmentHistoryEntries(
        RtRentalResolutionPlan plan,
        SyncPullResponse snapshot,
        IReadOnlyDictionary<Guid, RentalBillingProfileDto> profilesById,
        DateTime generatedAtUtc)
    {
        var currentAssetsById = snapshot.RentalAssets
            .Where(asset => !asset.IsDeleted)
            .ToDictionary(asset => asset.Id);
        var currentHistoriesByAssetId = snapshot.RentalAssetAssignmentHistories
            .Where(history => !history.IsDeleted && history.IsCurrent)
            .GroupBy(history => history.AssetId)
            .ToDictionary(group => group.Key, group => group.ToList());
        foreach (var assetEntry in plan.Assets)
        {
            var desiredAsset = assetEntry.Desired;
            currentAssetsById.TryGetValue(desiredAsset.Id, out var currentAsset);
            var assignmentChanged = currentAsset is null || AssignmentChanged(currentAsset, desiredAsset);
            if (!assignmentChanged)
                continue;
            var currentRows = currentHistoriesByAssetId.GetValueOrDefault(desiredAsset.Id) ?? [];
            foreach (var currentHistory in currentRows)
            {
                var closed = Clone(currentHistory);
                closed.IsCurrent = false;
                closed.UnlinkedAtUtc = generatedAtUtc;
                closed.ChangeReason = "RT 최신 임대 데이터 반영";
                plan.AssignmentHistories.Add(new RtRentalResolutionEntity<RentalAssetAssignmentHistoryDto>
                {
                    Operation = OperationUpdate,
                    EntityId = closed.Id,
                    ExpectedEntitySha256 = ComputeEntitySha256(currentHistory),
                    Desired = closed
                });
                plan.Audit.PlannedAssignmentHistoryUpdateCount++;
            }

            if (currentAsset is not null && currentRows.Count == 0 && HasAssignment(currentAsset))
            {
                var ended = CreateAssignmentHistory(
                    currentAsset,
                    currentAsset.CustomerId,
                    FirstNonBlank(currentAsset.CurrentCustomerName, currentAsset.CustomerName),
                    FirstNonBlank(currentAsset.InstallLocation, currentAsset.InstallSiteName),
                    currentAsset.BillingProfileId,
                    ResolveProfileDisplay(currentAsset.BillingProfileId, profilesById),
                    isCurrent: false,
                    generatedAtUtc);
                AddHistoryCreate(plan, ended);
            }

            if (HasAssignment(desiredAsset))
            {
                var current = CreateAssignmentHistory(
                    desiredAsset,
                    desiredAsset.CustomerId,
                    FirstNonBlank(desiredAsset.CurrentCustomerName, desiredAsset.CustomerName),
                    FirstNonBlank(desiredAsset.InstallLocation, desiredAsset.InstallSiteName),
                    desiredAsset.BillingProfileId,
                    ResolveProfileDisplay(desiredAsset.BillingProfileId, profilesById),
                    isCurrent: true,
                    generatedAtUtc);
                AddHistoryCreate(plan, current);
            }
            else if (currentAsset is null &&
                     (!string.IsNullOrWhiteSpace(desiredAsset.LastCustomerName) ||
                      !string.IsNullOrWhiteSpace(desiredAsset.LastInstallLocation)))
            {
                var ended = CreateAssignmentHistory(
                    desiredAsset,
                    null,
                    desiredAsset.LastCustomerName,
                    desiredAsset.LastInstallLocation,
                    desiredAsset.LastBillingProfileId,
                    desiredAsset.LastBillingProfileDisplay,
                    isCurrent: false,
                    generatedAtUtc);
                AddHistoryCreate(plan, ended);
            }
        }
    }

    private static void AddHistoryCreate(
        RtRentalResolutionPlan plan,
        RentalAssetAssignmentHistoryDto history)
    {
        if (plan.AssignmentHistories.Any(entry => entry.EntityId == history.Id))
            return;
        plan.AssignmentHistories.Add(new RtRentalResolutionEntity<RentalAssetAssignmentHistoryDto>
        {
            Operation = OperationCreate,
            EntityId = history.Id,
            Desired = history
        });
        plan.Audit.PlannedAssignmentHistoryCreateCount++;
    }

    private static RentalAssetAssignmentHistoryDto CreateAssignmentHistory(
        RentalAssetDto asset,
        Guid? customerId,
        string customerName,
        string installLocation,
        Guid? profileId,
        string profileDisplay,
        bool isCurrent,
        DateTime generatedAtUtc)
    {
        var linkedAtUtc = ResolveHistoryLinkedAtUtc(asset, generatedAtUtc);
        if (isCurrent && asset.CreatedAtUtc == generatedAtUtc)
            linkedAtUtc = generatedAtUtc;
        var id = CreateDeterministicGuid(
            $"rt-resolution-history|{asset.Id:N}|{linkedAtUtc:O}|{profileId?.ToString("N") ?? string.Empty}|" +
            $"{customerId?.ToString("N") ?? string.Empty}|" +
            $"{customerName}|{installLocation}|{isCurrent}|{generatedAtUtc:O}");
        return new RentalAssetAssignmentHistoryDto
        {
            Id = id,
            AssetId = asset.Id,
            BillingProfileId = profileId,
            CustomerId = customerId,
            TenantCode = asset.TenantCode,
            OfficeCode = asset.OfficeCode,
            ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            CustomerName = customerName,
            InstallLocation = installLocation,
            BillingProfileDisplay = profileDisplay,
            ItemName = asset.ItemName,
            MachineNumber = asset.MachineNumber,
            ManagementNumber = asset.ManagementNumber,
            MonthlyFee = asset.MonthlyFee,
            ContractStartDate = asset.ContractStartDate ?? asset.InstallDate,
            ContractEndDate = asset.RentalEndDate,
            ChangeReason = "RT 최신 임대 데이터 반영",
            IsCurrent = isCurrent,
            LinkedAtUtc = linkedAtUtc,
            UnlinkedAtUtc = isCurrent ? null : generatedAtUtc,
            CreatedAtUtc = generatedAtUtc,
            UpdatedAtUtc = generatedAtUtc
        };
    }

    private static DateTime ResolveHistoryLinkedAtUtc(RentalAssetDto asset, DateTime generatedAtUtc)
    {
        var date = asset.ContractStartDate ?? asset.InstallDate;
        var linkedAtUtc = date.HasValue
            ? DateTime.SpecifyKind(date.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
            : asset.CreatedAtUtc == default
                ? generatedAtUtc.AddMinutes(-1)
                : EnsureUtc(asset.CreatedAtUtc);
        return linkedAtUtc >= generatedAtUtc ? generatedAtUtc.AddMinutes(-1) : linkedAtUtc;
    }

    private static bool AssignmentChanged(RentalAssetDto current, RentalAssetDto desired)
        => current.CustomerId != desired.CustomerId ||
           current.BillingProfileId != desired.BillingProfileId ||
           !string.Equals(current.CurrentCustomerName, desired.CurrentCustomerName, StringComparison.Ordinal) ||
           !string.Equals(current.CustomerName, desired.CustomerName, StringComparison.Ordinal) ||
           !string.Equals(current.InstallLocation, desired.InstallLocation, StringComparison.Ordinal) ||
           !string.Equals(current.InstallSiteName, desired.InstallSiteName, StringComparison.Ordinal);

    private static bool HasAssignment(RentalAssetDto asset)
        => !asset.IsDeleted &&
           (asset.CustomerId.HasValue || asset.BillingProfileId.HasValue ||
            !string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ||
            !string.IsNullOrWhiteSpace(asset.CustomerName) ||
            !string.IsNullOrWhiteSpace(asset.InstallLocation) ||
            !string.IsNullOrWhiteSpace(asset.InstallSiteName));

    private static string ResolveProfileDisplay(
        Guid? profileId,
        IReadOnlyDictionary<Guid, RentalBillingProfileDto> profilesById)
        => profileId.HasValue && profilesById.TryGetValue(profileId.Value, out var profile)
            ? FirstNonBlank(profile.ProfileKey, profile.CustomerName, profile.ItemName)
            : string.Empty;

    private static string RemoveAssetIdsFromTemplate(
        string templateJson,
        IReadOnlySet<Guid> removals,
        out decimal monthlyAmount)
    {
        JsonArray array;
        try
        {
            array = JsonNode.Parse(string.IsNullOrWhiteSpace(templateJson) ? "[]" : templateJson) as JsonArray
                    ?? throw new InvalidDataException("The billing template must be a JSON array.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A billing profile template is not valid JSON.", exception);
        }
        var retained = new JsonArray();
        foreach (var node in array)
        {
            if (node is not JsonObject item)
                throw new InvalidDataException("A billing profile template item is not an object.");
            var includedNode = FindProperty(item, "IncludedAssetIds");
            var originalIds = ReadGuidArray(includedNode);
            var hadExplicitIds = originalIds.Count > 0;
            var remaining = originalIds.Where(id => !removals.Contains(id)).Distinct().ToList();
            if (hadExplicitIds && remaining.Count == 0)
                continue;
            if (includedNode is JsonArray includedArray && hadExplicitIds)
            {
                includedArray.Clear();
                foreach (var id in remaining)
                    includedArray.Add(id);
                var mode = ReadString(item, "BillingLineMode");
                if (string.Equals(mode, "개별", StringComparison.Ordinal))
                {
                    SetNumber(item, "Quantity", remaining.Count);
                    var unitPrice = ReadDecimal(item, "UnitPrice");
                    SetNumber(item, "Amount", unitPrice * remaining.Count);
                }
                var representative = ReadGuid(item, "RepresentativeAssetId");
                if (representative.HasValue && removals.Contains(representative.Value))
                    SetGuidOrNull(item, "RepresentativeAssetId", remaining.FirstOrDefault());
            }
            retained.Add(item.DeepClone());
        }
        monthlyAmount = retained
            .OfType<JsonObject>()
            .Sum(item => ReadDecimal(item, "Amount"));
        return retained.ToJsonString(JsonOptions);
    }

    private static void AddAssetEntryIfChanged(
        RtRentalResolutionPlan plan,
        RentalAssetDto? current,
        RentalAssetDto desired)
    {
        if (current is not null && EntityBusinessEquals(current, desired))
            return;
        plan.Assets.Add(new RtRentalResolutionEntity<RentalAssetDto>
        {
            Operation = current is null ? OperationCreate : OperationUpdate,
            EntityId = desired.Id,
            ExpectedEntitySha256 = current is null ? string.Empty : ComputeEntitySha256(current),
            Desired = desired
        });
        if (current is null)
            plan.Audit.PlannedAssetCreateCount++;
        else
            plan.Audit.PlannedAssetUpdateCount++;
    }

    private static CustomerDto? ResolveExistingCustomer(
        string rtCustomerName,
        RentalAssetDto? current,
        IReadOnlyDictionary<string, List<CustomerDto>> customersByCanonicalKey)
    {
        var key = CanonicalCustomerKey(rtCustomerName);
        if (!customersByCanonicalKey.TryGetValue(key, out var matches) || matches.Count == 0)
            return null;
        return matches
            .OrderByDescending(customer => string.Equals(
                NormalizeCustomerDisplay(customer.NameOriginal),
                NormalizeCustomerDisplay(rtCustomerName),
                StringComparison.Ordinal))
            .ThenByDescending(customer => string.Equals(
                customer.ResponsibleOfficeCode,
                current?.ResponsibleOfficeCode,
                StringComparison.OrdinalIgnoreCase))
            .ThenBy(customer => customer.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(customer => customer.Id)
            .First();
    }

    private static CustomerDto CreateCustomer(
        string databaseName,
        string tenantCode,
        string officeCode,
        string name,
        DateTime generatedAtUtc)
        => new()
        {
            Id = CreateDeterministicGuid($"rt-resolution-customer|{databaseName}|{CanonicalCustomerKey(name)}"),
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            NameOriginal = name,
            NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(name),
            TradeType = "매출",
            CreatedAtUtc = generatedAtUtc,
            UpdatedAtUtc = generatedAtUtc
        };

    private static RentalAssetDto CreateAsset(
        RtRentalSourceRow source,
        string companyCode,
        string tenantCode,
        string targetStatus,
        DateTime generatedAtUtc)
        => new()
        {
            Id = CreateDeterministicGuid($"rt-resolution-asset|{companyCode}|{NormalizeSimple(source.ManagementNumber)}"),
            TenantCode = tenantCode,
            OfficeCode = companyCode,
            ResponsibleOfficeCode = companyCode,
            ManagementCompanyCode = companyCode,
            ManagementNumber = NormalizeSimple(source.ManagementNumber),
            ManagementId = NormalizeSimple(source.ManagementNumber),
            ItemCategoryName = NormalizeSourceText(source.ItemCategoryName),
            ItemName = NormalizeSourceText(source.ItemName),
            Manufacturer = NormalizeSourceText(source.Manufacturer),
            MachineNumber = NormalizeSourceText(source.MachineNumber),
            AssetStatus = targetStatus,
            CreatedAtUtc = generatedAtUtc,
            UpdatedAtUtc = generatedAtUtc
        };

    private static Dictionary<Guid, List<RentalBillingProfileDto>> BuildExplicitProfileLookup(
        IEnumerable<RentalBillingProfileDto> profiles)
    {
        var result = new Dictionary<Guid, List<RentalBillingProfileDto>>();
        foreach (var profile in profiles)
        {
            if (!RentalBillingTemplateAssetCoverageRules.TryGetExplicitIncludedAssetIds(
                    profile.BillingTemplateJson,
                    out var ids,
                    out _))
            {
                continue;
            }
            foreach (var id in ids.Where(id => id != Guid.Empty))
            {
                if (!result.TryGetValue(id, out var list))
                    result[id] = list = [];
                list.Add(profile);
            }
        }
        return result;
    }

    private static void AddDeparture(
        IDictionary<Guid, HashSet<Guid>> destination,
        Guid profileId,
        Guid assetId)
    {
        if (profileId == Guid.Empty || assetId == Guid.Empty)
            return;
        if (!destination.TryGetValue(profileId, out var ids))
            destination[profileId] = ids = [];
        ids.Add(assetId);
    }

    internal static string CanonicalCustomerKey(string? value)
    {
        var text = NormalizeCustomerDisplay(value).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        text = text
            .Replace("{시청}", "인천시청", StringComparison.Ordinal)
            .Replace("[시청]", "인천시청", StringComparison.Ordinal)
            .Replace("인천광역시의회", "인천시의회", StringComparison.Ordinal)
            .Replace("인천광역시청", "인천시청", StringComparison.Ordinal)
            .Replace("인천광역시", "인천", StringComparison.Ordinal)
            .Replace("인천보건환경연구원", "보건환경연구원", StringComparison.Ordinal)
            .Replace("상수도사업본부", "상수도", StringComparison.Ordinal)
            .Replace("상수도사업소", "상수도", StringComparison.Ordinal)
            .Replace("연수구보건소", "연수구", StringComparison.Ordinal)
            .Replace("미추홀구보건소", "미추홀구", StringComparison.Ordinal)
            .Replace("연수구청", "연수구", StringComparison.Ordinal)
            .Replace("미추홀구청", "미추홀구", StringComparison.Ordinal)
            .Replace("부평구청", "부평구", StringComparison.Ordinal)
            .Replace("남동구청", "남동구", StringComparison.Ordinal)
            .Replace("서구청", "서구", StringComparison.Ordinal)
            .Replace("중구청", "중구", StringComparison.Ordinal)
            .Replace("계양구청", "계양구", StringComparison.Ordinal)
            .Replace("시설안전관리공단", "시설관리공단", StringComparison.Ordinal)
            .Replace("다이케스팅", "다이캐스팅", StringComparison.Ordinal)
            .Replace("주식회사", string.Empty, StringComparison.Ordinal)
            .Replace("유한회사", string.Empty, StringComparison.Ordinal)
            .Replace("(주)", string.Empty, StringComparison.Ordinal)
            .Replace("(유)", string.Empty, StringComparison.Ordinal)
            .Replace("㈜", string.Empty, StringComparison.Ordinal)
            .Replace("㈲", string.Empty, StringComparison.Ordinal);
        var compact = new string(text.Where(char.IsLetterOrDigit).ToArray());
        return compact
            .Replace("연수구보건소", "연수구", StringComparison.Ordinal)
            .Replace("미추홀구보건소", "미추홀구", StringComparison.Ordinal)
            .Replace("연수구립도서관", "연수구", StringComparison.Ordinal);
    }

    internal static bool CustomerNamesEquivalent(string? left, string? right)
    {
        var leftKey = CanonicalCustomerKey(left);
        var rightKey = CanonicalCustomerKey(right);
        return !string.IsNullOrWhiteSpace(leftKey) && string.Equals(leftKey, rightKey, StringComparison.Ordinal);
    }

    internal static string ComputeSnapshotSha256(SyncPullResponse snapshot)
    {
        var payload = new
        {
            snapshot.CurrentServerRevision,
            Customers = snapshot.Customers.OrderBy(item => item.Id).ToList(),
            Profiles = snapshot.RentalBillingProfiles.OrderBy(item => item.Id).ToList(),
            Assets = snapshot.RentalAssets.OrderBy(item => item.Id).ToList(),
            Histories = snapshot.RentalAssetAssignmentHistories.OrderBy(item => item.Id).ToList(),
            Logs = snapshot.RentalBillingLogs.OrderBy(item => item.Id).ToList(),
            Invoices = snapshot.Invoices.OrderBy(item => item.Id).ToList(),
            Payments = snapshot.Payments.OrderBy(item => item.Id).ToList()
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions)));
    }

    internal static string ComputeProtectedFinancialSha256(SyncPullResponse snapshot)
    {
        var payload = new
        {
            Logs = snapshot.RentalBillingLogs.OrderBy(item => item.Id).ToList(),
            Invoices = snapshot.Invoices.OrderBy(item => item.Id).ToList(),
            Payments = snapshot.Payments.OrderBy(item => item.Id).ToList()
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions)));
    }

    internal static string ComputeEntitySha256<T>(T entity)
        where T : SyncEntityDto
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entity, JsonOptions)));

    internal static bool EntityBusinessEquals<T>(T left, T right)
        where T : SyncEntityDto
    {
        var a = Clone(left);
        var b = Clone(right);
        ClearMutationMetadata(a);
        ClearMutationMetadata(b);
        return JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions);
    }

    internal static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)
           ?? throw new InvalidDataException("Could not clone an RT rental resolution entity.");

    internal static void ClearMutationMetadata(SyncEntityDto entity)
    {
        entity.CreatedAtUtc = default;
        entity.UpdatedAtUtc = default;
        entity.Revision = 0;
        entity.ExpectedRevision = 0;
        entity.MutationId = string.Empty;
        entity.MutationCreatedAtUtc = null;
        if (entity is RentalAssetDto asset)
            asset.AssetKey = string.Empty;
    }

    private static void AddDecision(
        RtRentalResolutionPlan plan,
        string managementNumber,
        string category,
        RtRentalSourceRow? source,
        string previous,
        string resolved,
        string decision,
        string reason)
        => plan.Decisions.Add(new RtRentalResolutionDecision
        {
            BusinessDatabaseName = plan.BusinessDatabaseName,
            ManagementNumber = managementNumber,
            Category = category,
            RtStatus = source is null ? string.Empty : NormalizeSourceText(source.Status),
            RtCustomerName = source is null ? string.Empty : NormalizeCustomerDisplay(source.CustomerName),
            PreviousValue = previous,
            ResolvedValue = resolved,
            Decision = decision,
            Reason = reason
        });

    private static void SetGuidOrNull(JsonObject item, string name, Guid value)
    {
        var property = item.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
        item[property.Key ?? name] = value == Guid.Empty ? null : JsonValue.Create(value);
    }

    private static void SetNumber(JsonObject item, string name, decimal value)
    {
        var property = item.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
        item[property.Key ?? name] = JsonValue.Create(value);
    }

    private static JsonNode? FindProperty(JsonObject item, string name)
        => item.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string ReadString(JsonObject item, string name)
        => FindProperty(item, name)?.GetValue<string>() ?? string.Empty;

    private static decimal ReadDecimal(JsonObject item, string name)
    {
        var node = FindProperty(item, name);
        if (node is null)
            return 0;
        return node.GetValue<decimal>();
    }

    private static Guid? ReadGuid(JsonObject item, string name)
    {
        var node = FindProperty(item, name);
        if (node is null)
            return null;
        return Guid.TryParse(node.ToString(), out var parsed) && parsed != Guid.Empty ? parsed : null;
    }

    private static List<Guid> ReadGuidArray(JsonNode? node)
        => node is JsonArray array
            ? array.Select(value => Guid.TryParse(value?.ToString(), out var parsed) ? parsed : Guid.Empty)
                .Where(value => value != Guid.Empty)
                .ToList()
            : [];

    private static string NormalizeDatabaseName(string value)
    {
        var normalized = TenantScopeCatalog.GetDatabaseName(value);
        var itworld = TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld);
        var usenet = TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.UsenetGroup);
        if (!string.Equals(normalized, itworld, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalized, usenet, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The RT rental resolution targets an unsupported business database.");
        }
        return normalized;
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static string NormalizeSimple(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeSourceText(string? value)
    {
        var normalized = NormalizeSimple(value);
        return normalized is "-" or "—" ? string.Empty : normalized;
    }

    private static string NormalizeCustomerDisplay(string? value)
        => NormalizeSourceText(value);

    private static string PreferSource(string? source, string? current)
    {
        var normalized = NormalizeSourceText(source);
        return string.IsNullOrWhiteSpace(normalized) ? NormalizeSimple(current) : normalized;
    }

    private static string FirstNonBlank(params string?[] values)
        => values.Select(NormalizeSimple).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static decimal? ParseFee(string? value)
    {
        var normalized = NormalizeSourceText(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        if (normalized is "무료" or "면제")
            return 0;
        normalized = normalized.Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("원", string.Empty, StringComparison.Ordinal)
            .Replace("₩", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            throw new InvalidDataException($"Invalid RT rental monthly fee: {value}");
        return parsed;
    }

    private static int? ParseMonths(string? value)
    {
        var normalized = NormalizeSourceText(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        normalized = normalized.Replace("개월", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            throw new InvalidDataException($"Invalid RT rental contract month value: {value}");
        return parsed;
    }

    private static DateOnly? ParseDate(string? value)
    {
        var normalized = NormalizeSourceText(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        if (!DateOnly.TryParseExact(normalized, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new InvalidDataException($"Invalid RT rental date: {value}");
        return parsed;
    }

    private static Guid CreateDeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }
}

internal static partial class RtRentalDeltaApplier
{
    internal static async Task<RtRentalResolutionGenerationResult> GenerateResolutionPlanAsync(
        string sourceCsvPath,
        string credentialDatabasePath,
        string businessDatabaseName,
        string planOutputPath,
        string reportOutputPath,
        CancellationToken cancellationToken = default)
        => await GenerateResolutionPlanCoreAsync(
            sourceCsvPath,
            credentialDatabasePath,
            businessDatabaseName,
            planOutputPath,
            reportOutputPath,
            profileUnlinkOnly: false,
            cancellationToken);

    internal static async Task<RtRentalResolutionGenerationResult> GenerateResolutionProfileUnlinkPlanAsync(
        string sourceCsvPath,
        string credentialDatabasePath,
        string businessDatabaseName,
        string planOutputPath,
        string reportOutputPath,
        CancellationToken cancellationToken = default)
        => await GenerateResolutionPlanCoreAsync(
            sourceCsvPath,
            credentialDatabasePath,
            businessDatabaseName,
            planOutputPath,
            reportOutputPath,
            profileUnlinkOnly: true,
            cancellationToken);

    private static async Task<RtRentalResolutionGenerationResult> GenerateResolutionPlanCoreAsync(
        string sourceCsvPath,
        string credentialDatabasePath,
        string businessDatabaseName,
        string planOutputPath,
        string reportOutputPath,
        bool profileUnlinkOnly,
        CancellationToken cancellationToken)
    {
        var root = RequireMigrationRoot();
        var sourcePath = RequireContainedRegularFile(root, sourceCsvPath, "source");
        var credentialPath = RequireContainedRegularFile(root, credentialDatabasePath, "credential database");
        var planPath = RequireContainedNewFilePath(root, planOutputPath, "resolution plan output");
        var reportPath = RequireContainedNewFilePath(root, reportOutputPath, "resolution report output");
        if (string.Equals(planPath, reportPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The RT rental resolution plan and report paths must differ.");

        var databaseName = TenantScopeCatalog.GetDatabaseName(businessDatabaseName);
        _ = NormalizeRequiredProductionBaseUrl();
        var sourceHash = ComputeFileSha256(sourcePath);
        var credentialHash = ComputeFileSha256(credentialPath);
        var credentials = await ReadCredentialCandidatesAsync(credentialPath, cancellationToken);
        var session = new SessionState();
        using var http = new HttpClient { BaseAddress = NormalizeRequiredProductionBaseUrl(), Timeout = TimeSpan.FromSeconds(120) };
        var api = new ErpApiClient(http, session);
        try
        {
            var selection = await SelectApprovedCredentialAsync(api, session, credentials, databaseName, cancellationToken);
            if (!selection.Selected)
                throw new InvalidOperationException(BuildCredentialSelectionFailureMessage(
                    selection.CandidateCount,
                    selection.LoginSucceededCount,
                    selection.RentalAssetEditAllowedCount,
                    selection.BusinessDatabaseSelectedCount));
            var snapshot = await PullRentalAdministrationAsync(api, databaseName, cancellationToken);
            var generatedAtUtc = DateTime.UtcNow;
            var planId = $"rt-resolve-{databaseName.ToLowerInvariant()}-{generatedAtUtc:yyyyMMddHHmmss}";
            var sourceRows = RtRentalDeltaPlanner.ReadSourceCsv(sourcePath);
            var build = profileUnlinkOnly
                ? RtRentalResolutionPlanner.BuildProfileUnlinkPlan(
                    sourceRows,
                    snapshot,
                    databaseName,
                    sourceHash,
                    planId,
                    generatedAtUtc)
                : RtRentalResolutionPlanner.BuildPlan(
                    sourceRows,
                    snapshot,
                    databaseName,
                    sourceHash,
                    planId,
                    generatedAtUtc);
            ValidateResolutionPlan(build.Plan);
            var planBytes = JsonSerializer.SerializeToUtf8Bytes(build.Plan, WriteJsonOptions);
            var planHash = ComputeSha256(planBytes);
            var reportBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                SchemaVersion = 1,
                build.Plan.PlanId,
                build.Plan.BusinessDatabaseName,
                build.Plan.GeneratedAtUtc,
                SourceSha256 = sourceHash,
                PlanSha256 = planHash,
                SnapshotSha256 = build.SnapshotSha256,
                ServerRevision = snapshot.CurrentServerRevision,
                CredentialSelection = selection,
                build.Plan.Audit,
                build.Plan.Decisions,
                PlannedCustomers = build.Plan.Customers.Select(entry => new
                {
                    entry.EntityId,
                    entry.Desired.NameOriginal,
                    entry.Desired.TenantCode,
                    entry.Desired.OfficeCode,
                    entry.Desired.ResponsibleOfficeCode
                }),
                PlannedProfiles = build.Plan.BillingProfiles.Select(entry => new
                {
                    entry.EntityId,
                    entry.Desired.ProfileKey,
                    entry.Desired.CustomerName,
                    entry.Desired.IsActive,
                    entry.Desired.MonthlyAmount
                }),
                PlannedAssets = build.Plan.Assets.Select(entry => new
                {
                    entry.Operation,
                    entry.EntityId,
                    entry.Desired.ManagementNumber,
                    entry.Desired.AssetStatus,
                    entry.Desired.CurrentCustomerName,
                    entry.Desired.BillingProfileId,
                    entry.Desired.MonthlyFee
                }),
                CurrentAssets = build.Plan.Assets.Select(entry =>
                {
                    var current = snapshot.RentalAssets.FirstOrDefault(asset => asset.Id == entry.EntityId);
                    return new
                    {
                        entry.EntityId,
                        ManagementNumber = current?.ManagementNumber ?? string.Empty,
                        current?.CustomerId,
                        CustomerName = current?.CustomerName ?? string.Empty,
                        CurrentCustomerName = current?.CurrentCustomerName ?? string.Empty,
                        current?.BillingProfileId,
                        AssetStatus = current?.AssetStatus ?? string.Empty,
                        current?.MonthlyFee,
                        TenantCode = current?.TenantCode ?? string.Empty,
                        OfficeCode = current?.OfficeCode ?? string.Empty,
                        ResponsibleOfficeCode = current?.ResponsibleOfficeCode ?? string.Empty,
                        current?.Revision
                    };
                }),
                DeletedAssetCandidates = build.Plan.Assets
                    .Where(entry => string.Equals(entry.Operation, RtRentalResolutionPlanner.OperationCreate, StringComparison.Ordinal))
                    .Select(entry => new
                    {
                        entry.EntityId,
                        entry.Desired.ManagementNumber,
                        Candidates = snapshot.RentalAssets
                            .Where(asset => asset.IsDeleted && string.Equals(
                                asset.ManagementNumber?.Trim(),
                                entry.Desired.ManagementNumber?.Trim(),
                                StringComparison.OrdinalIgnoreCase))
                            .Select(asset => new
                            {
                                asset.Id,
                                asset.ManagementNumber,
                                asset.AssetStatus,
                                asset.UpdatedAtUtc,
                                asset.Revision
                            })
                            .ToList()
                    }),
                PlannedAssignmentHistories = build.Plan.AssignmentHistories.Select(entry => new
                {
                    entry.Operation,
                    entry.EntityId,
                    entry.Desired.AssetId,
                    entry.Desired.ManagementNumber,
                    entry.Desired.CustomerName,
                    entry.Desired.IsCurrent,
                    entry.Desired.LinkedAtUtc,
                    entry.Desired.UnlinkedAtUtc
                })
            }, WriteJsonOptions);
            await WriteNewFileAsync(reportPath, reportBytes, cancellationToken);
            await WriteNewFileAsync(planPath, planBytes, cancellationToken);
            return new RtRentalResolutionGenerationResult(
                planPath,
                reportPath,
                planHash,
                sourceHash,
                build.SnapshotSha256,
                databaseName,
                snapshot.CurrentServerRevision,
                selection.CandidateCount,
                selection.LoginSucceededCount,
                selection.RentalAssetEditAllowedCount,
                selection.BusinessDatabaseSelectedCount,
                build.Plan.Audit);
        }
        finally
        {
            if (!string.Equals(credentialHash, ComputeFileSha256(credentialPath), StringComparison.Ordinal))
                throw new InvalidOperationException("The migration credential snapshot changed while it was in use.");
        }
    }

    internal static Task<RtRentalResolutionRunResult> PreviewResolutionAsync(
        string planPath,
        string sourceCsvPath,
        string credentialDatabasePath,
        CancellationToken cancellationToken = default)
        => RunResolutionAsync(planPath, sourceCsvPath, credentialDatabasePath, apply: false, cancellationToken);

    internal static Task<RtRentalResolutionRunResult> ApplyResolutionAsync(
        string planPath,
        string sourceCsvPath,
        string credentialDatabasePath,
        CancellationToken cancellationToken = default)
        => RunResolutionAsync(planPath, sourceCsvPath, credentialDatabasePath, apply: true, cancellationToken);

    private static async Task<RtRentalResolutionRunResult> RunResolutionAsync(
        string planPath,
        string sourceCsvPath,
        string credentialDatabasePath,
        bool apply,
        CancellationToken cancellationToken)
    {
        if (apply)
            RequireExactEnvironment(ApplyEnvironmentKey, "1");
        var root = RequireMigrationRoot();
        var fullPlanPath = RequireContainedRegularFile(root, planPath, "resolution plan");
        var fullSourcePath = RequireContainedRegularFile(root, sourceCsvPath, "source");
        var fullCredentialPath = RequireContainedRegularFile(root, credentialDatabasePath, "credential database");
        var planBytes = await File.ReadAllBytesAsync(fullPlanPath, cancellationToken);
        var planHash = ComputeSha256(planBytes);
        RequireExactEnvironment(PlanShaEnvironmentKey, planHash);
        var plan = JsonSerializer.Deserialize<RtRentalResolutionPlan>(planBytes, JsonOptions)
                   ?? throw new InvalidDataException("The RT rental resolution plan is empty or invalid.");
        ValidateResolutionPlan(plan);
        var sourceHash = ComputeFileSha256(fullSourcePath);
        if (!string.Equals(sourceHash, plan.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The RT rental source hash does not match the approved resolution plan.");
        var credentialHash = ComputeFileSha256(fullCredentialPath);
        var credentials = await ReadCredentialCandidatesAsync(fullCredentialPath, cancellationToken);
        var session = new SessionState();
        using var http = new HttpClient { BaseAddress = NormalizeRequiredProductionBaseUrl(), Timeout = TimeSpan.FromSeconds(120) };
        var api = new ErpApiClient(http, session);
        try
        {
            var selection = await SelectApprovedCredentialAsync(
                api, session, credentials, plan.BusinessDatabaseName, cancellationToken);
            if (!selection.Selected)
                throw new InvalidOperationException(BuildCredentialSelectionFailureMessage(
                    selection.CandidateCount,
                    selection.LoginSucceededCount,
                    selection.RentalAssetEditAllowedCount,
                    selection.BusinessDatabaseSelectedCount));
            var before = await PullRentalAdministrationAsync(api, plan.BusinessDatabaseName, cancellationToken);
            var beforeHash = RtRentalResolutionPlanner.ComputeSnapshotSha256(before);
            if (!string.Equals(beforeHash, plan.ExpectedSnapshotSha256, StringComparison.OrdinalIgnoreCase) ||
                before.CurrentServerRevision != plan.ExpectedServerRevision)
            {
                throw new InvalidDataException("The rental administration snapshot changed after planning. Generate a new resolution plan.");
            }
            var protectedFinancialHashBefore = RtRentalResolutionPlanner.ComputeProtectedFinancialSha256(before);
            var prepared = PrepareResolutionMutations(plan, planHash, before);
            var submittedCount = prepared.Customers.Count + prepared.Profiles.Count + prepared.Assets.Count +
                                 prepared.AssignmentHistories.Count;
            if (!apply || submittedCount == 0)
            {
                return new RtRentalResolutionRunResult(
                    planHash,
                    sourceHash,
                    plan.BusinessDatabaseName,
                    plan.Audit.PlannedEntityCount,
                    prepared.Customers.Count,
                    prepared.Profiles.Count,
                    prepared.Assets.Count,
                    prepared.AssignmentHistories.Count,
                    0,
                    prepared.SkippedNoChangeCount,
                    before.CurrentServerRevision,
                    before.CurrentServerRevision,
                    beforeHash,
                    beforeHash,
                    protectedFinancialHashBefore,
                    protectedFinancialHashBefore);
            }
            var push = await api.PushAsync(new SyncPushRequest
            {
                DeviceId = BuildDeviceId(plan.PlanId),
                Customers = prepared.Customers,
                RentalBillingProfiles = prepared.Profiles,
                RentalAssets = prepared.Assets,
                RentalAssetAssignmentHistories = prepared.AssignmentHistories
            }, plan.BusinessDatabaseName, cancellationToken)
                ?? throw new InvalidDataException("The RT rental resolution push returned an empty response.");
            RequireCompleteResolutionAcceptance(push, prepared);
            var after = await PullRentalAdministrationAsync(api, plan.BusinessDatabaseName, cancellationToken);
            VerifyResolutionApplied(plan, prepared, after);
            var protectedFinancialHashAfter = RtRentalResolutionPlanner.ComputeProtectedFinancialSha256(after);
            if (!string.Equals(protectedFinancialHashBefore, protectedFinancialHashAfter, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invoices, payments, or rental billing logs changed during the RT rental resolution push.");
            return new RtRentalResolutionRunResult(
                planHash,
                sourceHash,
                plan.BusinessDatabaseName,
                plan.Audit.PlannedEntityCount,
                prepared.Customers.Count,
                prepared.Profiles.Count,
                prepared.Assets.Count,
                prepared.AssignmentHistories.Count,
                push.AcceptedCount,
                prepared.SkippedNoChangeCount,
                before.CurrentServerRevision,
                after.CurrentServerRevision,
                beforeHash,
                RtRentalResolutionPlanner.ComputeSnapshotSha256(after),
                protectedFinancialHashBefore,
                protectedFinancialHashAfter);
        }
        finally
        {
            if (!string.Equals(credentialHash, ComputeFileSha256(fullCredentialPath), StringComparison.Ordinal))
                throw new InvalidOperationException("The migration credential snapshot changed while it was in use.");
        }
    }

    internal static RtRentalResolutionPrepared PrepareResolutionMutations(
        RtRentalResolutionPlan plan,
        string planSha256,
        SyncPullResponse snapshot)
    {
        ValidateResolutionPlan(plan);
        ValidateSha256(planSha256, "resolution plan");
        var customers = PrepareResolutionEntities(plan.Customers, snapshot.Customers, plan, planSha256, "Customer", out var customerSkipped);
        var profiles = PrepareResolutionEntities(plan.BillingProfiles, snapshot.RentalBillingProfiles, plan, planSha256, "RentalBillingProfile", out var profileSkipped);
        var assets = PrepareResolutionEntities(plan.Assets, snapshot.RentalAssets, plan, planSha256, "RentalAsset", out var assetSkipped);
        var histories = PrepareResolutionEntities(
            plan.AssignmentHistories,
            snapshot.RentalAssetAssignmentHistories,
            plan,
            planSha256,
            "RentalAssetAssignmentHistory",
            out var historySkipped);
        return new RtRentalResolutionPrepared(
            customers,
            profiles,
            assets,
            histories,
            customerSkipped + profileSkipped + assetSkipped + historySkipped);
    }

    private static List<T> PrepareResolutionEntities<T>(
        IReadOnlyCollection<RtRentalResolutionEntity<T>> entries,
        IReadOnlyCollection<T> currentEntities,
        RtRentalResolutionPlan plan,
        string planSha256,
        string entityName,
        out int skipped)
        where T : SyncEntityDto
    {
        var currentById = currentEntities.GroupBy(entity => entity.Id).ToDictionary(group => group.Key, group => group.Single());
        var prepared = new List<T>();
        skipped = 0;
        foreach (var entry in entries.OrderBy(entry => entry.EntityId))
        {
            currentById.TryGetValue(entry.EntityId, out var current);
            if (current is not null && RtRentalResolutionPlanner.EntityBusinessEquals(current, entry.Desired))
            {
                skipped++;
                continue;
            }
            if (string.Equals(entry.Operation, RtRentalResolutionPlanner.OperationCreate, StringComparison.Ordinal))
            {
                if (current is not null)
                    throw new InvalidDataException($"A planned {entityName} create ID now exists with different values.");
            }
            else
            {
                if (current is null || current.IsDeleted)
                    throw new InvalidDataException($"A planned {entityName} update no longer exists.");
                var currentHash = RtRentalResolutionPlanner.ComputeEntitySha256(current);
                if (!string.Equals(currentHash, entry.ExpectedEntitySha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"A planned {entityName} changed after planning.");
            }
            var desired = RtRentalResolutionPlanner.Clone(entry.Desired);
            desired.ExpectedRevision = current?.Revision ?? 0;
            desired.MutationId = BuildMutationId(planSha256, desired.Id, desired.ExpectedRevision);
            desired.MutationCreatedAtUtc = EnsureUtc(plan.GeneratedAtUtc);
            prepared.Add(desired);
        }
        return prepared;
    }

    private static void RequireCompleteResolutionAcceptance(
        SyncPushResult result,
        RtRentalResolutionPrepared prepared)
    {
        var expected = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Customer"] = prepared.Customers.Select(entity => entity.Id).ToHashSet(),
            ["RentalBillingProfile"] = prepared.Profiles.Select(entity => entity.Id).ToHashSet(),
            ["RentalAsset"] = prepared.Assets.Select(entity => entity.Id).ToHashSet(),
            ["RentalAssetAssignmentHistory"] = prepared.AssignmentHistories.Select(entity => entity.Id).ToHashSet()
        };
        var submitted = expected.Values.Sum(ids => ids.Count);
        if (result.AcceptedCount != submitted || result.ConflictCount != 0 ||
            result.DuplicateMutationCount != 0 || result.Conflicts.Count != 0)
        {
            var details = string.Join(" | ", result.Conflicts.Take(20).Select(conflict =>
                $"{conflict.EntityName}:{conflict.EntityId}:{conflict.Reason}"));
            var notices = string.Join(" | ", result.Notices.Take(20).Select(notice =>
                $"{notice.EntityName}:{notice.EntityId}:{notice.Code}:{notice.Message}"));
            throw new InvalidOperationException(
                $"The RT rental resolution push was not accepted completely. submitted={submitted}; " +
                $"accepted={result.AcceptedCount}; conflicts={result.ConflictCount}; duplicates={result.DuplicateMutationCount}; " +
                $"details={details}; notices={notices}");
        }
        foreach (var pair in expected.Where(pair => pair.Value.Count > 0))
        {
            var acceptedIds = result.AcceptedRevisions
                .Where(revision => string.Equals(revision.EntityName, pair.Key, StringComparison.OrdinalIgnoreCase))
                .Select(revision => revision.EntityId)
                .ToHashSet();
            if (!acceptedIds.SetEquals(pair.Value))
                throw new InvalidOperationException($"The RT rental resolution receipt omitted {pair.Key} acknowledgements.");
        }
    }

    private static void VerifyResolutionApplied(
        RtRentalResolutionPlan plan,
        RtRentalResolutionPrepared prepared,
        SyncPullResponse after)
    {
        VerifyResolutionEntities(plan.Customers, prepared.Customers, after.Customers, "Customer");
        VerifyResolutionEntities(plan.BillingProfiles, prepared.Profiles, after.RentalBillingProfiles, "RentalBillingProfile");
        VerifyResolutionEntities(plan.Assets, prepared.Assets, after.RentalAssets, "RentalAsset");
        VerifyResolutionEntities(
            plan.AssignmentHistories,
            prepared.AssignmentHistories,
            after.RentalAssetAssignmentHistories,
            "RentalAssetAssignmentHistory");
    }

    private static void VerifyResolutionEntities<T>(
        IReadOnlyCollection<RtRentalResolutionEntity<T>> entries,
        IReadOnlyCollection<T> submitted,
        IReadOnlyCollection<T> current,
        string entityName)
        where T : SyncEntityDto
    {
        var submittedIds = submitted.Select(entity => entity.Id).ToHashSet();
        var currentById = current.GroupBy(entity => entity.Id).ToDictionary(group => group.Key, group => group.Single());
        foreach (var entry in entries.Where(entry => submittedIds.Contains(entry.EntityId)))
        {
            if (!currentById.TryGetValue(entry.EntityId, out var actual) || actual.IsDeleted)
                throw new InvalidDataException($"An accepted {entityName} is missing from the verification pull.");
            if (!RtRentalResolutionPlanner.EntityBusinessEquals(actual, entry.Desired))
                throw new InvalidDataException($"The accepted {entityName} does not match the approved resolution values.");
        }
    }

    private static void ValidateResolutionPlan(RtRentalResolutionPlan plan)
    {
        if (plan.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported RT rental resolution plan schema.");
        if (string.IsNullOrWhiteSpace(plan.PlanId) || plan.PlanId.Length > 64 ||
            plan.PlanId.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
            throw new InvalidDataException("The RT rental resolution plan ID is invalid.");
        var databaseName = TenantScopeCatalog.GetDatabaseName(plan.BusinessDatabaseName);
        if (!string.Equals(databaseName, plan.BusinessDatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The RT rental resolution database name is not canonical.");
        ValidateSha256(plan.SourceSha256, "source");
        ValidateSha256(plan.ExpectedSnapshotSha256, "snapshot");
        if (plan.ExpectedServerRevision <= 0)
            throw new InvalidDataException("The RT rental resolution server revision is invalid.");
        var generated = EnsureUtc(plan.GeneratedAtUtc);
        if (generated > DateTime.UtcNow.AddMinutes(5) || generated < DateTime.UtcNow.AddDays(-7))
            throw new InvalidDataException("The RT rental resolution plan timestamp is outside the allowed window.");
        var allEntries = plan.Customers.Cast<object>()
            .Concat(plan.BillingProfiles)
            .Concat(plan.Assets)
            .Concat(plan.AssignmentHistories)
            .ToList();
        if (allEntries.Count > 2000)
            throw new InvalidDataException("The RT rental resolution plan exceeds the entity safety limit.");
        var ids = plan.Customers.Select(entry => entry.EntityId)
            .Concat(plan.BillingProfiles.Select(entry => entry.EntityId))
            .Concat(plan.Assets.Select(entry => entry.EntityId))
            .Concat(plan.AssignmentHistories.Select(entry => entry.EntityId))
            .ToList();
        if (ids.Any(id => id == Guid.Empty) || ids.Count != ids.Distinct().Count())
            throw new InvalidDataException("The RT rental resolution plan has blank or duplicate entity IDs.");
        ValidateResolutionEntries(plan.Customers);
        ValidateResolutionEntries(plan.BillingProfiles);
        ValidateResolutionEntries(plan.Assets);
        ValidateResolutionEntries(plan.AssignmentHistories);
        if (plan.Audit.PlannedEntityCount != ids.Count)
            throw new InvalidDataException("The RT rental resolution audit count does not match the plan.");
    }

    private static void ValidateResolutionEntries<T>(
        IEnumerable<RtRentalResolutionEntity<T>> entries)
        where T : SyncEntityDto
    {
        foreach (var entry in entries)
        {
            if (entry.Operation != RtRentalResolutionPlanner.OperationCreate &&
                entry.Operation != RtRentalResolutionPlanner.OperationUpdate)
            {
                throw new InvalidDataException("The RT rental resolution plan has an unsupported operation.");
            }
            if (entry.Operation == RtRentalResolutionPlanner.OperationUpdate)
                ValidateSha256(entry.ExpectedEntitySha256, "expected entity");
            if (entry.Desired is null || entry.Desired.Id != entry.EntityId)
                throw new InvalidDataException("The RT rental resolution entity payload is mismatched.");
        }
    }
}
