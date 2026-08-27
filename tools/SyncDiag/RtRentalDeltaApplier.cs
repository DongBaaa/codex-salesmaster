using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Tools.SyncDiag;

internal sealed class RtRentalDeltaPlan
{
    public int SchemaVersion { get; set; }
    public string PlanId { get; set; } = string.Empty;
    public string BusinessDatabaseName { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; }
    public List<RtRentalDeltaPlanEntry> Entries { get; set; } = [];
}

internal sealed class RtRentalDeltaPlanEntry
{
    public Guid AssetId { get; set; }
    public long ExpectedRevision { get; set; }
    public DateTime ExpectedUpdatedAtUtc { get; set; }
    public string ExpectedTenantCode { get; set; } = string.Empty;
    public string ExpectedOfficeCode { get; set; } = string.Empty;
    public string ExpectedManagementCompanyCode { get; set; } = string.Empty;
    public string ExpectedResponsibleOfficeCode { get; set; } = string.Empty;
    public string ExpectedManagementNumber { get; set; } = string.Empty;
    public string ExpectedAssetStatus { get; set; } = string.Empty;
    public RtRentalScalarValues Values { get; set; } = new();
}

internal sealed class RtRentalScalarValues
{
    public string CurrentLocation { get; set; } = string.Empty;
    public string ItemCategoryName { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string MachineNumber { get; set; } = string.Empty;
    public string PurchaseVendor { get; set; } = string.Empty;
    public DateOnly? PurchaseDate { get; set; }
    public DateOnly? DisposalDate { get; set; }
    public decimal PurchasePrice { get; set; }
    public decimal SalePrice { get; set; }
    public string InstallLocation { get; set; } = string.Empty;
    public string DepositText { get; set; } = string.Empty;
    public decimal MonthlyFee { get; set; }
    public int ContractMonths { get; set; }
    public DateOnly? ContractDate { get; set; }
    public DateOnly? InstallDate { get; set; }
    public DateOnly? ContractStartDate { get; set; }
    public DateOnly? RentalEndDate { get; set; }
    public string FreeSupplyItems { get; set; } = string.Empty;
    public string PaidSupplyItems { get; set; } = string.Empty;
}

internal sealed record RtRentalDeltaRunResult(
    string PlanSha256,
    string SourceSha256,
    string BusinessDatabaseName,
    int PlannedCount,
    int SubmittedCount,
    int AcceptedCount,
    int SkippedNoChangeCount,
    long ServerRevisionBefore,
    long ServerRevisionAfter);

internal static class RtRentalDeltaApplier
{
    internal const string RootMarkerName =
        ".georaeplan-rt-rental-migration-root";
    internal const string ApplyEnvironmentKey =
        "GEORAEPLAN_RT_RENTAL_APPLY";
    internal const string RootEnvironmentKey =
        "GEORAEPLAN_RT_MIGRATION_ROOT";
    internal const string PlanShaEnvironmentKey =
        "GEORAEPLAN_RT_RENTAL_PLAN_SHA256";
    internal const string BaseUrlEnvironmentKey =
        "GEORAEPLAN_SYNC_BASEURL";
    internal const string ProductionBaseUrl =
        "https://trade.2884.kr/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static async Task<RtRentalDeltaRunResult> ApplyAsync(
        string planPath,
        string sourceCsvPath,
        string credentialDatabasePath,
        CancellationToken cancellationToken = default)
        => await RunAsync(
            planPath,
            sourceCsvPath,
            credentialDatabasePath,
            apply: true,
            cancellationToken);

    internal static async Task<RtRentalDeltaRunResult> PreviewAsync(
        string planPath,
        string sourceCsvPath,
        string credentialDatabasePath,
        CancellationToken cancellationToken = default)
        => await RunAsync(
            planPath,
            sourceCsvPath,
            credentialDatabasePath,
            apply: false,
            cancellationToken);

    private static async Task<RtRentalDeltaRunResult> RunAsync(
        string planPath,
        string sourceCsvPath,
        string credentialDatabasePath,
        bool apply,
        CancellationToken cancellationToken)
    {
        if (apply)
            RequireExactEnvironment(ApplyEnvironmentKey, "1");
        var root = RequireMigrationRoot();
        var fullPlanPath = RequireContainedRegularFile(root, planPath, "plan");
        var fullSourcePath = RequireContainedRegularFile(root, sourceCsvPath, "source");
        var fullCredentialPath = RequireContainedRegularFile(
            root,
            credentialDatabasePath,
            "credential database");

        var planBytes = await File.ReadAllBytesAsync(
            fullPlanPath,
            cancellationToken);
        var planSha256 = ComputeSha256(planBytes);
        RequireExactEnvironment(PlanShaEnvironmentKey, planSha256);
        var plan = JsonSerializer.Deserialize<RtRentalDeltaPlan>(
                       planBytes,
                       JsonOptions)
                   ?? throw new InvalidDataException(
                       "The RT rental delta plan is empty or invalid.");
        ValidatePlan(plan);

        var sourceSha256 = ComputeFileSha256(fullSourcePath);
        if (!string.Equals(
                sourceSha256,
                plan.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The RT rental source snapshot hash does not match the approved plan.");
        }

        var baseUrl = NormalizeRequiredProductionBaseUrl();
        var credentialHashBefore = ComputeFileSha256(fullCredentialPath);
        var credentials = new List<IsolatedStoredCredential>();
        var savedLogin = await IsolatedStoredCredentialReader.ReadSavedLoginAsync(
            fullCredentialPath,
            cancellationToken);
        if (savedLogin is not null)
            credentials.Add(savedLogin);
        credentials.AddRange(await IsolatedStoredCredentialReader.ReadAsync(
            fullCredentialPath,
            cancellationToken));
        credentials = credentials
            .GroupBy(
                credential => credential.Username,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (credentials.Count == 0)
            throw new InvalidDataException(
                "The migration credential snapshot has no saved login candidates.");

        var session = new SessionState();
        using var http = new HttpClient
        {
            BaseAddress = baseUrl,
            Timeout = TimeSpan.FromSeconds(120)
        };
        var api = new ErpApiClient(http, session);
        try
        {
            var selectedCredential = false;
            foreach (var credential in credentials)
            {
                var password = UnprotectCredential(
                    credential.PasswordProtected);
                try
                {
                    var login = await api.LoginAsync(
                        credential.Username,
                        password,
                        cancellationToken);
                    if (login is null ||
                        string.IsNullOrWhiteSpace(login.Token))
                    {
                        continue;
                    }

                    session.SetSession(login.Token, login.User);
                    if (!session.HasPermission("Rental.AssetEdit"))
                        continue;

                    session.SetBusinessDatabase(plan.BusinessDatabaseName);
                    if (!string.Equals(
                            session.SelectedBusinessDatabaseName,
                            plan.BusinessDatabaseName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    selectedCredential = true;
                    break;
                }
                finally
                {
                    password = string.Empty;
                }
            }

            if (!selectedCredential)
            {
                throw new InvalidOperationException(
                    "No saved login candidate can select the approved business database with rental-asset edit permission.");
            }

            var before = await PullRentalAdministrationAsync(
                api,
                plan.BusinessDatabaseName,
                cancellationToken);
            var prepared = PrepareMutations(
                plan,
                planSha256,
                before.RentalAssets);

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
                       ?? throw new InvalidDataException(
                           "The rental delta push returned an empty response.");

            RequireCompleteAcceptance(push, prepared.Mutations);
            var after = await PullRentalAdministrationAsync(
                api,
                plan.BusinessDatabaseName,
                cancellationToken);
            VerifyAppliedValues(
                plan,
                prepared.Mutations.Select(asset => asset.Id).ToHashSet(),
                after.RentalAssets);

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
            if (!string.Equals(
                    credentialHashBefore,
                    ComputeFileSha256(fullCredentialPath),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The migration credential snapshot changed while it was in use.");
            }
        }
    }

    internal static RtRentalPreparedMutations PrepareMutations(
        RtRentalDeltaPlan plan,
        string planSha256,
        IReadOnlyCollection<RentalAssetDto> currentAssets)
    {
        ValidatePlan(plan);
        ValidateSha256(planSha256, "plan");
        var currentById = currentAssets
            .GroupBy(asset => asset.Id)
            .ToDictionary(
                group => group.Key,
                group => group.Single());
        var mutations = new List<RentalAssetDto>();
        var skippedNoChangeCount = 0;

        foreach (var entry in plan.Entries.OrderBy(entry => entry.AssetId))
        {
            if (!currentById.TryGetValue(entry.AssetId, out var current))
                throw new InvalidDataException(
                    "An approved rental asset no longer exists in the selected business database.");

            ValidateCurrentAsset(plan, entry, current);
            var protectedSnapshot = CaptureProtectedFields(current);
            ApplyScalarValues(current, entry.Values);
            AssertProtectedFieldsUnchanged(current, protectedSnapshot);

            if (entry.Values.MonthlyFee != protectedSnapshot.MonthlyFee &&
                current.BillingProfileId is Guid billingProfileId &&
                billingProfileId != Guid.Empty)
            {
                throw new InvalidDataException(
                    "A plan attempted to change the fee of an asset linked to a billing profile.");
            }

            if (ScalarValuesEqual(current, protectedSnapshot))
            {
                skippedNoChangeCount++;
                continue;
            }

            current.ExpectedRevision = current.Revision;
            current.MutationId = BuildMutationId(
                planSha256,
                current.Id,
                current.Revision);
            current.MutationCreatedAtUtc = EnsureUtc(plan.GeneratedAtUtc);
            mutations.Add(current);
        }

        return new RtRentalPreparedMutations(
            mutations,
            skippedNoChangeCount);
    }

    internal static void VerifyAppliedValues(
        RtRentalDeltaPlan plan,
        IReadOnlySet<Guid> submittedIds,
        IReadOnlyCollection<RentalAssetDto> currentAssets)
    {
        var currentById = currentAssets
            .GroupBy(asset => asset.Id)
            .ToDictionary(group => group.Key, group => group.Single());
        foreach (var entry in plan.Entries.Where(entry => submittedIds.Contains(entry.AssetId)))
        {
            if (!currentById.TryGetValue(entry.AssetId, out var current))
                throw new InvalidDataException(
                    "An accepted rental asset is missing from the verification pull.");
            ValidateProtectedScope(entry, current);
            if (!TargetValuesEqual(current, entry.Values))
                throw new InvalidDataException(
                    "The server verification pull does not contain every approved scalar value.");
        }
    }

    private static async Task<SyncPullResponse> PullRentalAdministrationAsync(
        ErpApiClient api,
        string businessDatabaseName,
        CancellationToken cancellationToken)
        => await api.PullAsync(
               0,
               businessDatabaseName,
               rentalAdministrationOnly: true,
               cancellationToken)
           ?? throw new InvalidDataException(
               "The rental-administration pull returned an empty response.");

    private static void RequireCompleteAcceptance(
        SyncPushResult result,
        IReadOnlyCollection<RentalAssetDto> submitted)
    {
        if (result.AcceptedCount != submitted.Count ||
            result.ConflictCount != 0 ||
            result.DuplicateMutationCount != 0 ||
            result.Conflicts.Count != 0)
        {
            throw new InvalidOperationException(
                "The RT rental delta push was not accepted completely.");
        }

        var expectedIds = submitted.Select(asset => asset.Id).ToHashSet();
        var acceptedIds = result.AcceptedRevisions
            .Where(revision => string.Equals(
                revision.EntityName,
                "RentalAsset",
                StringComparison.OrdinalIgnoreCase))
            .Select(revision => revision.EntityId)
            .ToHashSet();
        if (!acceptedIds.SetEquals(expectedIds))
            throw new InvalidOperationException(
                "The RT rental delta receipt did not acknowledge every submitted asset.");
    }

    private static void ValidatePlan(RtRentalDeltaPlan plan)
    {
        if (plan.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported RT rental delta plan schema.");
        if (string.IsNullOrWhiteSpace(plan.PlanId) || plan.PlanId.Length > 64 ||
            plan.PlanId.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
        {
            throw new InvalidDataException("The RT rental delta plan ID is invalid.");
        }

        var normalizedDatabaseName = TenantScopeCatalog.GetDatabaseName(
            plan.BusinessDatabaseName);
        if (!string.Equals(
                normalizedDatabaseName,
                plan.BusinessDatabaseName,
                StringComparison.OrdinalIgnoreCase) ||
            (!string.Equals(
                 normalizedDatabaseName,
                 TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.UsenetGroup),
                 StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(
                 normalizedDatabaseName,
                 TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld),
                 StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The RT rental delta plan targets an unsupported business database.");
        }

        ValidateSha256(plan.SourceSha256, "source");
        if (plan.GeneratedAtUtc == default ||
            EnsureUtc(plan.GeneratedAtUtc) > DateTime.UtcNow.AddMinutes(5) ||
            EnsureUtc(plan.GeneratedAtUtc) < DateTime.UtcNow.AddDays(-7))
        {
            throw new InvalidDataException(
                "The RT rental delta plan timestamp is outside the allowed window.");
        }

        if (plan.Entries.Count == 0 || plan.Entries.Count > 1200 ||
            plan.Entries.Select(entry => entry.AssetId).Distinct().Count() != plan.Entries.Count)
        {
            throw new InvalidDataException(
                "The RT rental delta plan entry set is empty, too large, or duplicated.");
        }

        foreach (var entry in plan.Entries)
        {
            if (entry.AssetId == Guid.Empty || entry.ExpectedRevision <= 0 ||
                entry.ExpectedUpdatedAtUtc == default ||
                string.IsNullOrWhiteSpace(entry.ExpectedTenantCode) ||
                string.IsNullOrWhiteSpace(entry.ExpectedOfficeCode) ||
                string.IsNullOrWhiteSpace(entry.ExpectedManagementCompanyCode) ||
                string.IsNullOrWhiteSpace(entry.ExpectedResponsibleOfficeCode) ||
                string.IsNullOrWhiteSpace(entry.ExpectedManagementNumber) ||
                string.IsNullOrWhiteSpace(entry.ExpectedAssetStatus))
            {
                throw new InvalidDataException(
                    "An RT rental delta plan entry has incomplete concurrency or scope guards.");
            }

            if (entry.Values.ContractMonths < 0 ||
                entry.Values.PurchasePrice < 0 ||
                entry.Values.SalePrice < 0 ||
                entry.Values.MonthlyFee < 0)
            {
                throw new InvalidDataException(
                    "An RT rental delta plan entry contains a negative amount or duration.");
            }
        }
    }

    private static void ValidateCurrentAsset(
        RtRentalDeltaPlan plan,
        RtRentalDeltaPlanEntry entry,
        RentalAssetDto current)
    {
        if (current.IsDeleted || current.Revision != entry.ExpectedRevision ||
            EnsureUtc(current.UpdatedAtUtc) != EnsureUtc(entry.ExpectedUpdatedAtUtc))
        {
            throw new InvalidDataException(
                "An approved rental asset changed after the plan snapshot was created.");
        }

        ValidateProtectedScope(entry, current);
        if (!string.Equals(
                current.AssetStatus,
                entry.ExpectedAssetStatus,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.ManagementNumber,
                entry.ExpectedManagementNumber,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "An approved rental asset identity or status changed after planning.");
        }

        if (string.Equals(
                plan.BusinessDatabaseName,
                TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld),
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                current.ResponsibleOfficeCode,
                OfficeCodeCatalog.Itworld,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Cross-office ITWORLD rental assets are excluded from automatic RT migration.");
        }
    }

    private static void ValidateProtectedScope(
        RtRentalDeltaPlanEntry entry,
        RentalAssetDto current)
    {
        if (!string.Equals(current.TenantCode, entry.ExpectedTenantCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.OfficeCode, entry.ExpectedOfficeCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.ManagementCompanyCode, entry.ExpectedManagementCompanyCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.ResponsibleOfficeCode, entry.ExpectedResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "An approved rental asset scope changed after planning.");
        }
    }

    private static RtRentalProtectedFields CaptureProtectedFields(
        RentalAssetDto asset)
        => new(
            asset.TenantCode,
            asset.OfficeCode,
            asset.ResponsibleOfficeCode,
            asset.ManagementCompanyCode,
            asset.ManagementId,
            asset.ManagementNumber,
            asset.CustomerId,
            asset.CustomerName,
            asset.CurrentCustomerName,
            asset.InstallSiteName,
            asset.ItemId,
            asset.BillingProfileId,
            asset.AssetStatus,
            asset.Notes,
            asset.BillingEligibilityStatus,
            asset.BillingExclusionReason,
            asset.LastCustomerName,
            asset.LastInstallLocation,
            asset.LastBillingProfileId,
            asset.LastBillingProfileDisplay,
            asset.LastAssignmentClearedAtUtc,
            asset.CurrentLocation,
            asset.ItemCategoryName,
            asset.Manufacturer,
            asset.ItemName,
            asset.MachineNumber,
            asset.PurchaseVendor,
            asset.PurchaseDate,
            asset.DisposalDate,
            asset.PurchasePrice,
            asset.SalePrice,
            asset.InstallLocation,
            asset.DepositText,
            asset.MonthlyFee,
            asset.ContractMonths,
            asset.ContractDate,
            asset.InstallDate,
            asset.ContractStartDate,
            asset.RentalEndDate,
            asset.FreeSupplyItems,
            asset.PaidSupplyItems);

    private static void AssertProtectedFieldsUnchanged(
        RentalAssetDto asset,
        RtRentalProtectedFields expected)
    {
        if (!string.Equals(asset.TenantCode, expected.TenantCode, StringComparison.Ordinal) ||
            !string.Equals(asset.OfficeCode, expected.OfficeCode, StringComparison.Ordinal) ||
            !string.Equals(asset.ResponsibleOfficeCode, expected.ResponsibleOfficeCode, StringComparison.Ordinal) ||
            !string.Equals(asset.ManagementCompanyCode, expected.ManagementCompanyCode, StringComparison.Ordinal) ||
            !string.Equals(asset.ManagementId, expected.ManagementId, StringComparison.Ordinal) ||
            !string.Equals(asset.ManagementNumber, expected.ManagementNumber, StringComparison.Ordinal) ||
            asset.CustomerId != expected.CustomerId ||
            !string.Equals(asset.CustomerName, expected.CustomerName, StringComparison.Ordinal) ||
            !string.Equals(asset.CurrentCustomerName, expected.CurrentCustomerName, StringComparison.Ordinal) ||
            !string.Equals(asset.InstallSiteName, expected.InstallSiteName, StringComparison.Ordinal) ||
            asset.ItemId != expected.ItemId ||
            asset.BillingProfileId != expected.BillingProfileId ||
            !string.Equals(asset.AssetStatus, expected.AssetStatus, StringComparison.Ordinal) ||
            !string.Equals(asset.Notes, expected.Notes, StringComparison.Ordinal) ||
            !string.Equals(asset.BillingEligibilityStatus, expected.BillingEligibilityStatus, StringComparison.Ordinal) ||
            !string.Equals(asset.BillingExclusionReason, expected.BillingExclusionReason, StringComparison.Ordinal) ||
            !string.Equals(asset.LastCustomerName, expected.LastCustomerName, StringComparison.Ordinal) ||
            !string.Equals(asset.LastInstallLocation, expected.LastInstallLocation, StringComparison.Ordinal) ||
            asset.LastBillingProfileId != expected.LastBillingProfileId ||
            !string.Equals(asset.LastBillingProfileDisplay, expected.LastBillingProfileDisplay, StringComparison.Ordinal) ||
            asset.LastAssignmentClearedAtUtc != expected.LastAssignmentClearedAtUtc)
        {
            throw new InvalidDataException(
                "The RT rental scalar migration attempted to alter a protected field.");
        }
    }

    private static void ApplyScalarValues(
        RentalAssetDto asset,
        RtRentalScalarValues values)
    {
        asset.CurrentLocation = NormalizeText(values.CurrentLocation);
        asset.ItemCategoryName = NormalizeText(values.ItemCategoryName);
        asset.Manufacturer = NormalizeText(values.Manufacturer);
        asset.ItemName = NormalizeText(values.ItemName);
        asset.MachineNumber = NormalizeText(values.MachineNumber);
        asset.PurchaseVendor = NormalizeText(values.PurchaseVendor);
        asset.PurchaseDate = values.PurchaseDate;
        asset.DisposalDate = values.DisposalDate;
        asset.PurchasePrice = values.PurchasePrice;
        asset.SalePrice = values.SalePrice;
        asset.InstallLocation = NormalizeText(values.InstallLocation);
        asset.DepositText = NormalizeText(values.DepositText);
        asset.MonthlyFee = values.MonthlyFee;
        asset.ContractMonths = values.ContractMonths;
        asset.ContractDate = values.ContractDate;
        asset.InstallDate = values.InstallDate;
        asset.ContractStartDate = values.ContractStartDate;
        asset.RentalEndDate = values.RentalEndDate;
        asset.FreeSupplyItems = NormalizeText(values.FreeSupplyItems);
        asset.PaidSupplyItems = NormalizeText(values.PaidSupplyItems);
    }

    private static bool ScalarValuesEqual(
        RentalAssetDto current,
        RtRentalProtectedFields before)
        => string.Equals(current.CurrentLocation, before.CurrentLocation, StringComparison.Ordinal) &&
           string.Equals(current.ItemCategoryName, before.ItemCategoryName, StringComparison.Ordinal) &&
           string.Equals(current.Manufacturer, before.Manufacturer, StringComparison.Ordinal) &&
           string.Equals(current.ItemName, before.ItemName, StringComparison.Ordinal) &&
           string.Equals(current.MachineNumber, before.MachineNumber, StringComparison.Ordinal) &&
           string.Equals(current.PurchaseVendor, before.PurchaseVendor, StringComparison.Ordinal) &&
           current.PurchaseDate == before.PurchaseDate &&
           current.DisposalDate == before.DisposalDate &&
           current.PurchasePrice == before.PurchasePrice &&
           current.SalePrice == before.SalePrice &&
           string.Equals(current.InstallLocation, before.InstallLocation, StringComparison.Ordinal) &&
           string.Equals(current.DepositText, before.DepositText, StringComparison.Ordinal) &&
           current.MonthlyFee == before.MonthlyFee &&
           current.ContractMonths == before.ContractMonths &&
           current.ContractDate == before.ContractDate &&
           current.InstallDate == before.InstallDate &&
           current.ContractStartDate == before.ContractStartDate &&
           current.RentalEndDate == before.RentalEndDate &&
           string.Equals(current.FreeSupplyItems, before.FreeSupplyItems, StringComparison.Ordinal) &&
           string.Equals(current.PaidSupplyItems, before.PaidSupplyItems, StringComparison.Ordinal);

    private static bool TargetValuesEqual(
        RentalAssetDto current,
        RtRentalScalarValues values)
        => string.Equals(current.CurrentLocation, NormalizeText(values.CurrentLocation), StringComparison.Ordinal) &&
           string.Equals(current.ItemCategoryName, NormalizeText(values.ItemCategoryName), StringComparison.Ordinal) &&
           string.Equals(current.Manufacturer, NormalizeText(values.Manufacturer), StringComparison.Ordinal) &&
           string.Equals(current.ItemName, NormalizeText(values.ItemName), StringComparison.Ordinal) &&
           string.Equals(current.MachineNumber, NormalizeText(values.MachineNumber), StringComparison.Ordinal) &&
           string.Equals(current.PurchaseVendor, NormalizeText(values.PurchaseVendor), StringComparison.Ordinal) &&
           current.PurchaseDate == values.PurchaseDate &&
           current.DisposalDate == values.DisposalDate &&
           current.PurchasePrice == values.PurchasePrice &&
           current.SalePrice == values.SalePrice &&
           string.Equals(current.InstallLocation, NormalizeText(values.InstallLocation), StringComparison.Ordinal) &&
           string.Equals(current.DepositText, NormalizeText(values.DepositText), StringComparison.Ordinal) &&
           current.MonthlyFee == values.MonthlyFee &&
           current.ContractMonths == values.ContractMonths &&
           current.ContractDate == values.ContractDate &&
           current.InstallDate == values.InstallDate &&
           current.ContractStartDate == values.ContractStartDate &&
           current.RentalEndDate == values.RentalEndDate &&
           string.Equals(current.FreeSupplyItems, NormalizeText(values.FreeSupplyItems), StringComparison.Ordinal) &&
           string.Equals(current.PaidSupplyItems, NormalizeText(values.PaidSupplyItems), StringComparison.Ordinal);

    private static string RequireMigrationRoot()
    {
        var rawRoot = Environment.GetEnvironmentVariable(RootEnvironmentKey);
        if (string.IsNullOrWhiteSpace(rawRoot))
            throw new InvalidOperationException(
                $"{RootEnvironmentKey} is required.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawRoot));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(
                "The RT rental migration root does not exist.");
        EnsureNoReparsePoints(root);

        var markerPath = Path.Combine(root, RootMarkerName);
        if (!File.Exists(markerPath) ||
            !string.Equals(
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(File.ReadAllText(markerPath).Trim())),
                root,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The RT rental migration root marker is missing or invalid.");
        }
        return root;
    }

    private static string RequireContainedRegularFile(
        string root,
        string path,
        string label)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            throw new InvalidOperationException(
                $"The RT rental {label} must be an existing file inside the migration root.");
        }
        EnsureNoReparsePoints(fullPath);
        return fullPath;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
                   ?? throw new InvalidOperationException("A rooted path is required.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
                break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "RT rental migration paths cannot contain reparse points.");
        }
    }

    private static Uri NormalizeRequiredProductionBaseUrl()
    {
        var raw = Environment.GetEnvironmentVariable(BaseUrlEnvironmentKey);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"{BaseUrlEnvironmentKey} must be an absolute URI.");
        var normalized = new Uri(uri.ToString().TrimEnd('/') + "/");
        if (!string.Equals(
                normalized.AbsoluteUri,
                ProductionBaseUrl,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "RT rental delta apply is restricted to the TradePlan production API.");
        }
        return normalized;
    }

    private static void RequireExactEnvironment(string key, string expected)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(key)?.Trim(),
                expected,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{key} does not match the explicitly approved value.");
        }
    }

    private static string UnprotectCredential(string protectedText)
    {
        byte[]? protectedBytes = null;
        byte[]? plainBytes = null;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedText);
            plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            if (protectedBytes is not null)
                CryptographicOperations.ZeroMemory(protectedBytes);
            if (plainBytes is not null)
                CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ComputeSha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static void ValidateSha256(string value, string label)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException(
                $"The RT rental {label} SHA-256 is invalid.");
    }

    private static string BuildMutationId(
        string planSha256,
        Guid assetId,
        long revision)
        => $"rt-rental-{planSha256[..24].ToLowerInvariant()}-{assetId:N}-{revision}";

    private static string BuildDeviceId(string planId)
        => $"syncdiag-rt-rental-{planId}";

    private static string NormalizeText(string? value)
        => (value ?? string.Empty).Trim();

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}

internal sealed record RtRentalPreparedMutations(
    List<RentalAssetDto> Mutations,
    int SkippedNoChangeCount);

internal sealed record RtRentalProtectedFields(
    string TenantCode,
    string OfficeCode,
    string ResponsibleOfficeCode,
    string ManagementCompanyCode,
    string ManagementId,
    string ManagementNumber,
    Guid? CustomerId,
    string CustomerName,
    string CurrentCustomerName,
    string InstallSiteName,
    Guid? ItemId,
    Guid? BillingProfileId,
    string AssetStatus,
    string Notes,
    string BillingEligibilityStatus,
    string BillingExclusionReason,
    string LastCustomerName,
    string LastInstallLocation,
    Guid? LastBillingProfileId,
    string LastBillingProfileDisplay,
    DateTime? LastAssignmentClearedAtUtc,
    string CurrentLocation,
    string ItemCategoryName,
    string Manufacturer,
    string ItemName,
    string MachineNumber,
    string PurchaseVendor,
    DateOnly? PurchaseDate,
    DateOnly? DisposalDate,
    decimal PurchasePrice,
    decimal SalePrice,
    string InstallLocation,
    string DepositText,
    decimal MonthlyFee,
    int ContractMonths,
    DateOnly? ContractDate,
    DateOnly? InstallDate,
    DateOnly? ContractStartDate,
    DateOnly? RentalEndDate,
    string FreeSupplyItems,
    string PaidSupplyItems);
