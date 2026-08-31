using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace GeoraePlan.Tools.SyncDiag;

public sealed record IsolatedLegacyInvoiceSeedCanonicalizationGroup(
    int GroupOrdinal,
    string GroupFingerprintSha256,
    string Mode,
    int ActiveInvoiceCount,
    int ExcludedDeletedInvoiceCount,
    IReadOnlyList<string> ChangedMetadataFields,
    string BeforeMetadataSha256,
    string AfterMetadataSha256);

public sealed record IsolatedLegacyInvoiceSeedCanonicalizationReport(
    int SchemaVersion,
    bool Succeeded,
    string SourceDatabaseSha256,
    string SeedScope,
    int ChangedGroupCount,
    int ChangedInvoiceCount,
    int ExcludedDeletedInvoiceCount,
    string BeforeMetadataSha256,
    string AfterMetadataSha256,
    string ActiveInvoiceIdsSha256,
    string LatestInvoiceBusinessSha256,
    string DependencyReferencesSha256,
    IReadOnlyList<IsolatedLegacyInvoiceSeedCanonicalizationGroup> Groups)
{
    public string ToDeterministicJson()
        => JsonSerializer.Serialize(
            this,
            IsolatedLegacyInvoiceSeedCanonicalizer.ReportJsonOptions);

    public string ComputeSha256()
        => IsolatedLegacyInvoiceSeedCanonicalizer.ComputeSha256(
            ToDeterministicJson());
}

public sealed record IsolatedLegacyInvoiceSeedCanonicalizationProfilePreview(
    int SchemaVersion,
    string SourceDatabaseSha256,
    string SeedScope,
    int AuthorizedNonAcknowledgedOutboxCount,
    string AuthorizedNonAcknowledgedOutboxSha256,
    int ChangedGroupCount,
    int ChangedInvoiceCount,
    int ExcludedDeletedInvoiceCount,
    int DeletedPredecessorRerootGroupCount,
    int DuplicateSiblingGroupCount,
    int ResponsibleOfficeAlignmentGroupCount,
    string BeforeMetadataSha256,
    string ProjectedAfterMetadataSha256,
    string ActiveInvoiceIdsSha256,
    string LatestInvoiceBusinessSha256,
    string DependencyReferencesSha256)
{
    public string ToDeterministicJson()
        => JsonSerializer.Serialize(
            this,
            IsolatedLegacyInvoiceSeedCanonicalizer.ReportJsonOptions);

    public string ComputeSha256()
        => IsolatedLegacyInvoiceSeedCanonicalizer.ComputeSha256(
            ToDeterministicJson());
}

public sealed record IsolatedLegacyInvoiceSeedCanonicalizationResult(
    IsolatedLegacyInvoiceSeedCanonicalizationReport Report,
    string DeterministicJson,
    string ReportSha256,
    string CommandOutput);

public sealed class IsolatedLegacyInvoiceSeedCanonicalizationException
    : InvalidOperationException
{
    public IsolatedLegacyInvoiceSeedCanonicalizationException(
        string reasonCode,
        Guid? groupId = null,
        string? evidenceSha256 = null)
        : base(
            groupId.HasValue
                ? $"Legacy invoice seed canonicalization rejected group fingerprint {Fingerprint(groupId.Value)}: {reasonCode}."
                : $"Legacy invoice seed canonicalization rejected the database: {reasonCode}.")
    {
        ReasonCode = reasonCode;
        GroupFingerprintSha256 =
            groupId.HasValue ? Fingerprint(groupId.Value) : null;
        EvidenceSha256 = evidenceSha256;
    }

    public string ReasonCode { get; }

    public string? GroupFingerprintSha256 { get; }

    public string? EvidenceSha256 { get; }

    private static string Fingerprint(Guid value)
        => IsolatedLegacyInvoiceSeedCanonicalizer.ComputeDomainSeparatedSha256(
            "canonicalization-exception-group",
            value.ToString("D").ToUpperInvariant());
}

internal sealed record IsolatedLegacyInvoiceSeedCanonicalizationProfile(
    string SourceDatabaseSha256,
    int AuthorizedNonAcknowledgedOutboxCount,
    string AuthorizedNonAcknowledgedOutboxSha256,
    int ChangedGroupCount,
    int ChangedInvoiceCount,
    int ExcludedDeletedInvoiceCount,
    int DeletedPredecessorRerootGroupCount,
    int DuplicateSiblingGroupCount,
    int ResponsibleOfficeAlignmentGroupCount,
    string BeforeMetadataSha256,
    string AfterMetadataSha256,
    string ActiveInvoiceIdsSha256,
    string LatestInvoiceBusinessSha256,
    string DependencyReferencesSha256);

#if GEORAEPLAN_CANONICALIZER_TESTING
internal enum IsolatedLegacyInvoiceSeedCanonicalizationFault
{
    None,
    CancelAfterFirstUpdate,
    ThrowAfterFirstUpdate,
    ThrowBeforeCommit,
    ThrowDuringPostcommitDispose,
    ThrowDuringRecoveryArtifactWrite,
    CreateRecoveryArtifactReparseBeforePublish
}

internal sealed class InjectedCanonicalizationFaultException
    : InvalidOperationException
{
}
#endif

public static class IsolatedLegacyInvoiceSeedCanonicalizer
{
    public const string ExplicitOptInEnvironmentKey =
        "GEORAEPLAN_TEST_SEED_CANONICALIZE_LEGACY_INVOICES";
    public const string SourceDatabaseSha256EnvironmentKey =
        "GEORAEPLAN_TEST_SEED_SOURCE_DATABASE_SHA256";
    public const string ProfileInspectionOptInEnvironmentKey =
        "GEORAEPLAN_TEST_SEED_INSPECT_LEGACY_INVOICE_PROFILE";
    public const string SourceAttestationFileName =
        ".georaeplan-isolated-seed-source-attestation.json";
    public const string RecoveryResultFileName =
        ".georaeplan-isolated-seed-canonicalization-result.json";
    public const string ActiveOperationalSeedScope =
        "active_operational_seed_only_not_deleted_history_migration";
    internal const string ApprovedSourceDatabaseSha256 =
        "795B5A6CA153B788C6272222D778D714DB10873541775493AB7B36EA091E2FBE";
    internal const string CurrentApprovedSourceDatabaseSha256 =
        "E98DF3E657205319F595AE61089F50E1B87F0BD272C650827AA123B4A8616916";
    internal const string LatestApprovedSourceDatabaseSha256 =
        "719380E811BB04DC364FB6D2E0BD4C4E04B3D3C12F4D56207233D600F80B9A5C";
    internal const string NewestApprovedSourceDatabaseSha256 =
        "F422BC337476CE0A6A47638A1CF6D1F1CE1103ED81EF02688C8382197BBD8BA1";
    internal const string SecurityResetApprovedSourceDatabaseSha256 =
        "937B93127A721A16857403DE5B3B7DDD7669C1787AC0EAD9C32C83A413B37FE2";
    internal const string CurrentLiveApprovedSourceDatabaseSha256 =
        "D7D83F5970542AAADD37491E4CE79CB63C7044E776802AD52B02BC5CA27D8CAB";
    internal const string LatestLiveApprovedSourceDatabaseSha256 =
        "73D294E643379C1808AFF89842AA899EF5107C1B269F6B07ACCEE6E59E10B636";
    internal const string CurrentOperationalApprovedSourceDatabaseSha256 =
        "1DE40C0FA21FE662EAECFA7ED3B654EA1271076FE1F69029919A3295525EBEC6";
    internal const string LatestOperationalApprovedSourceDatabaseSha256 =
        "A3C4A81A9FCA783F40844DC04810A905C99619722A608D9C379CA4BB157A0654";

    internal static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private const int MaximumSiblingBranchWidth = 2;
    private const string DeletedPredecessorRerootMode =
        "deleted_predecessor_active_chain_reroot";
    private const string DuplicateSiblingMode =
        "duplicate_sibling_linearize";
    private const string ResponsibleOfficeAlignmentMode =
        "historical_responsible_office_align";

    private static readonly IsolatedLegacyInvoiceSeedCanonicalizationProfile
        ApprovedProfile = new(
            SourceDatabaseSha256: ApprovedSourceDatabaseSha256,
            AuthorizedNonAcknowledgedOutboxCount: 0,
            AuthorizedNonAcknowledgedOutboxSha256:
                "40086EECD8956D1BCBA111D96183766E8E9024FDB7DF06C817DFD56CE7B0ABDF",
            ChangedGroupCount: 5,
            ChangedInvoiceCount: 5,
            ExcludedDeletedInvoiceCount: 2,
            DeletedPredecessorRerootGroupCount: 2,
            DuplicateSiblingGroupCount: 2,
            ResponsibleOfficeAlignmentGroupCount: 1,
            BeforeMetadataSha256:
                "8A324FC2831CF3C8F996D8D6EA6B7AD01EDBFB7E793C5CB0548ED534F960904D",
            AfterMetadataSha256:
                "3EE8A9B5E52A2AD014AB9FFD65574D70A562E867B0C12256CA7BB7168AE1230B",
            ActiveInvoiceIdsSha256:
                "0D2CCBFEDEDA9540F4C5898187BAA7BFC3418D6272112C01772C7CE834AB076E",
            LatestInvoiceBusinessSha256:
                "C80296708B5E84B5401D1D393CFA5FD2D117708C4B3F611BD3156330469D01EA",
            DependencyReferencesSha256:
                "6F7DA4EFEE728601EF5AADBC60F0AB08C59DA70A3A7D49D7B74BBA652DD1ECB9");

    private static readonly IReadOnlyDictionary<string,
            IsolatedLegacyInvoiceSeedCanonicalizationProfile>
        ApprovedProfilesBySourceDatabaseSha256 =
            new Dictionary<string,
                IsolatedLegacyInvoiceSeedCanonicalizationProfile>(
                StringComparer.OrdinalIgnoreCase)
            {
                [ApprovedSourceDatabaseSha256] = ApprovedProfile,
                [CurrentApprovedSourceDatabaseSha256] =
                    ApprovedProfile with
                    {
                        SourceDatabaseSha256 =
                            CurrentApprovedSourceDatabaseSha256,
                        AuthorizedNonAcknowledgedOutboxCount = 3,
                        AuthorizedNonAcknowledgedOutboxSha256 =
                            "AA1704A42D3954DEF917EC10191B622BF4E15DA2C494C36877376D9E25125BC7",
                        LatestInvoiceBusinessSha256 =
                            "EE5B6FC6E2C9D58B3FBC066E00C95693F8EBC63DFE1BC1FCE784EB80EDF85CE8",
                        DependencyReferencesSha256 =
                            "D5528F8C6750119E3D642C0953C8C2519CB88C1E6E37457C81868839649641F7"
                    },
                [LatestApprovedSourceDatabaseSha256] =
                    ApprovedProfile with
                    {
                        SourceDatabaseSha256 =
                            LatestApprovedSourceDatabaseSha256,
                        AuthorizedNonAcknowledgedOutboxCount = 3,
                        AuthorizedNonAcknowledgedOutboxSha256 =
                            "AA1704A42D3954DEF917EC10191B622BF4E15DA2C494C36877376D9E25125BC7",
                        LatestInvoiceBusinessSha256 =
                            "EE5B6FC6E2C9D58B3FBC066E00C95693F8EBC63DFE1BC1FCE784EB80EDF85CE8",
                        DependencyReferencesSha256 =
                            "D5528F8C6750119E3D642C0953C8C2519CB88C1E6E37457C81868839649641F7"
                    },
                [NewestApprovedSourceDatabaseSha256] =
                    ApprovedProfile with
                    {
                        SourceDatabaseSha256 =
                            NewestApprovedSourceDatabaseSha256,
                        AuthorizedNonAcknowledgedOutboxCount = 3,
                        AuthorizedNonAcknowledgedOutboxSha256 =
                            "AA1704A42D3954DEF917EC10191B622BF4E15DA2C494C36877376D9E25125BC7",
                        LatestInvoiceBusinessSha256 =
                            "EE5B6FC6E2C9D58B3FBC066E00C95693F8EBC63DFE1BC1FCE784EB80EDF85CE8",
                        DependencyReferencesSha256 =
                            "D5528F8C6750119E3D642C0953C8C2519CB88C1E6E37457C81868839649641F7"
                    },
                [SecurityResetApprovedSourceDatabaseSha256] =
                    ApprovedProfile with
                    {
                        SourceDatabaseSha256 =
                            SecurityResetApprovedSourceDatabaseSha256,
                        AuthorizedNonAcknowledgedOutboxCount = 3,
                        AuthorizedNonAcknowledgedOutboxSha256 =
                            "AA1704A42D3954DEF917EC10191B622BF4E15DA2C494C36877376D9E25125BC7",
                        LatestInvoiceBusinessSha256 =
                            "EE5B6FC6E2C9D58B3FBC066E00C95693F8EBC63DFE1BC1FCE784EB80EDF85CE8",
                        DependencyReferencesSha256 =
                            "D5528F8C6750119E3D642C0953C8C2519CB88C1E6E37457C81868839649641F7"
                    },
                [CurrentLiveApprovedSourceDatabaseSha256] =
                    ApprovedProfile with
                    {
                        SourceDatabaseSha256 =
                            CurrentLiveApprovedSourceDatabaseSha256,
                        AuthorizedNonAcknowledgedOutboxCount = 0,
                        AuthorizedNonAcknowledgedOutboxSha256 =
                            "40086EECD8956D1BCBA111D96183766E8E9024FDB7DF06C817DFD56CE7B0ABDF",
                        BeforeMetadataSha256 =
                            "470D4118ACF242C3B4C1B7C5CCC6D0FC1CC7A1E9F9D2794F08EC470630153EBA",
                        AfterMetadataSha256 =
                            "49D925656056F81EBF84A23C0ED18433E205D7FB0F87699CE75A2965BD366BF9",
                        LatestInvoiceBusinessSha256 =
                            "CCF936EF6F144E58476DA8FBFDC2D129D86B2466A9E3549BB610F39B09AF5E43",
                        DependencyReferencesSha256 =
                            "996447F6331780A5A6E15C1387979C945542E7739E49610399AC2998652EAD58"
                    },
                [LatestLiveApprovedSourceDatabaseSha256] =
                    ApprovedProfile with
                    {
                        SourceDatabaseSha256 =
                            LatestLiveApprovedSourceDatabaseSha256,
                        AuthorizedNonAcknowledgedOutboxCount = 0,
                        AuthorizedNonAcknowledgedOutboxSha256 =
                            "40086EECD8956D1BCBA111D96183766E8E9024FDB7DF06C817DFD56CE7B0ABDF",
                        BeforeMetadataSha256 =
                            "470D4118ACF242C3B4C1B7C5CCC6D0FC1CC7A1E9F9D2794F08EC470630153EBA",
                        AfterMetadataSha256 =
                            "49D925656056F81EBF84A23C0ED18433E205D7FB0F87699CE75A2965BD366BF9",
                        LatestInvoiceBusinessSha256 =
                            "BDBE73992A6E5560DD02827BB3B3D99E57BF2D886BB0163D585D1F9DF6E45043",
                        DependencyReferencesSha256 =
                            "2C20069BE6B04423A6E7F007428DAC20CDCE0E097B80D908FBD02C91981FE605"
                    },
                [CurrentOperationalApprovedSourceDatabaseSha256] =
                    ApprovedProfile with
                    {
                        SourceDatabaseSha256 =
                            CurrentOperationalApprovedSourceDatabaseSha256,
                        AuthorizedNonAcknowledgedOutboxCount = 8,
                        AuthorizedNonAcknowledgedOutboxSha256 =
                            "A9D9717B32186F8EEE3A635AF06F393AC278080D11F27B5131BB0C52C317411A",
                        BeforeMetadataSha256 =
                            "470D4118ACF242C3B4C1B7C5CCC6D0FC1CC7A1E9F9D2794F08EC470630153EBA",
                        AfterMetadataSha256 =
                            "49D925656056F81EBF84A23C0ED18433E205D7FB0F87699CE75A2965BD366BF9",
                        LatestInvoiceBusinessSha256 =
                            "49AD13A712746B8AA6C38BB8ED069053B4C9305A320E117864F2DB8040CB4AA0",
                        DependencyReferencesSha256 =
                            "AC560B78FA943CFF1934C84BAB2ED37EC16E260ACA923CF94CE0FBB9E69F2C1F"
                    },
                [LatestOperationalApprovedSourceDatabaseSha256] =
                    ApprovedProfile with
                    {
                        SourceDatabaseSha256 =
                            LatestOperationalApprovedSourceDatabaseSha256,
                        AuthorizedNonAcknowledgedOutboxCount = 88,
                        AuthorizedNonAcknowledgedOutboxSha256 =
                            "D54838917A9D1F5538FE1D153E7964B25CCA741531D9D4E25CD14CFEE14A1E6E",
                        BeforeMetadataSha256 =
                            "470D4118ACF242C3B4C1B7C5CCC6D0FC1CC7A1E9F9D2794F08EC470630153EBA",
                        AfterMetadataSha256 =
                            "49D925656056F81EBF84A23C0ED18433E205D7FB0F87699CE75A2965BD366BF9",
                        LatestInvoiceBusinessSha256 =
                            "49AD13A712746B8AA6C38BB8ED069053B4C9305A320E117864F2DB8040CB4AA0",
                        DependencyReferencesSha256 =
                            "798161B849E966FFBDCA7D008D8581118BC791A815B1D5621EAA7A954301EFA4"
                    }
            };

    public static AuthorizationLease
        AcquireAuthorization(
            IsolatedPreparationDatabaseLease preparationLease)
    {
        ArgumentNullException.ThrowIfNull(preparationLease);
        var databasePath =
            preparationLease.DatabasePath ??
            throw new InvalidOperationException(
                "Legacy invoice seed canonicalization requires an isolated AppData database lease.");
        SourceAttestationLease? sourceAttestationLease = null;
        try
        {
            sourceAttestationLease = SourceAttestationLease.Acquire(
                preparationLease.GuardedRoot);
            var sourceDatabaseSha256 = AssertAuthorizationEnvironment(
                sourceAttestationLease.Text);
            preparationLease.AssertStable();
            sourceAttestationLease.AssertStable();
            return new AuthorizationLease(
                preparationLease.GuardedRoot,
                Path.GetFullPath(databasePath),
                sourceDatabaseSha256,
                sourceAttestationLease);
        }
        catch
        {
            sourceAttestationLease?.Dispose();
            throw;
        }
    }

    public static AuthorizationLease
        AcquireProfileInspectionAuthorization(
            IsolatedPreparationDatabaseLease preparationLease)
    {
        ArgumentNullException.ThrowIfNull(preparationLease);
        var databasePath =
            preparationLease.DatabasePath ??
            throw new InvalidOperationException(
                "Legacy invoice seed profile inspection requires an isolated AppData database lease.");
        SourceAttestationLease? sourceAttestationLease = null;
        try
        {
            sourceAttestationLease = SourceAttestationLease.Acquire(
                preparationLease.GuardedRoot);
            var sourceDatabaseSha256 =
                AssertProfileInspectionEnvironment(
                    sourceAttestationLease.Text);
            preparationLease.AssertStable();
            sourceAttestationLease.AssertStable();
            return new AuthorizationLease(
                preparationLease.GuardedRoot,
                Path.GetFullPath(databasePath),
                sourceDatabaseSha256,
                sourceAttestationLease);
        }
        catch
        {
            sourceAttestationLease?.Dispose();
            throw;
        }
    }

    public static async Task<IsolatedLegacyInvoiceSeedCanonicalizationResult>
        CanonicalizeAsync(
            LocalDbContext db,
            IsolatedPreparationDatabaseLease preparationLease,
            AuthorizationLease authorization,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(preparationLease);
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.AssertMatches(db, preparationLease);
        return await CanonicalizeTransactionAsync(
            db,
            authorization.SourceDatabaseSha256,
#if GEORAEPLAN_CANONICALIZER_TESTING
            new CanonicalizationTestAdapter(
                RejectPartialPushState: true,
                RequiredProfile: GetApprovedProfile(
                    authorization.SourceDatabaseSha256),
                Fault:
                    IsolatedLegacyInvoiceSeedCanonicalizationFault.None,
                PreparationLease: preparationLease,
                Authorization: authorization,
                RecoveryResultPath: authorization.RecoveryResultPath,
                RecoveryRootPath: authorization.GuardedRoot),
#else
            preparationLease,
            authorization,
#endif
            cancellationToken);
    }

    public static async Task<
            IsolatedLegacyInvoiceSeedCanonicalizationProfilePreview>
        PreviewProfileAsync(
            LocalDbContext db,
            IsolatedPreparationDatabaseLease preparationLease,
            AuthorizationLease authorization,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(preparationLease);
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.AssertMatches(db, preparationLease);
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var preview = await BuildProfilePreviewAsync(
            db,
            authorization.SourceDatabaseSha256,
            cancellationToken);
        AssertAuthorizedNonAcknowledgedOutbox(
            GetApprovedProfile(authorization.SourceDatabaseSha256),
            new NonAcknowledgedOutboxEvidence(
                preview.AuthorizedNonAcknowledgedOutboxCount,
                preview.AuthorizedNonAcknowledgedOutboxSha256));
        authorization.AssertStable();
        preparationLease.AssertStable();
        await transaction.RollbackAsync(cancellationToken);
        return preview;
    }

    public static async Task<
            IsolatedLegacyInvoiceSeedCanonicalizationProfilePreview>
        PreviewUnapprovedProfileAsync(
            LocalDbContext db,
            IsolatedPreparationDatabaseLease preparationLease,
            AuthorizationLease authorization,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(preparationLease);
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.AssertMatches(db, preparationLease);
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var preview = await BuildProfilePreviewAsync(
            db,
            authorization.SourceDatabaseSha256,
            cancellationToken);
        authorization.AssertStable();
        preparationLease.AssertStable();
        await transaction.RollbackAsync(cancellationToken);
        return preview;
    }

    internal static async Task<
            IsolatedLegacyInvoiceSeedCanonicalizationProfilePreview>
        PreviewReadOnlyProfileAsync(
            LocalDbContext db,
            string sourceDatabaseSha256,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsSha256(sourceDatabaseSha256))
        {
            throw new ArgumentException(
                "A valid source database SHA-256 is required.",
                nameof(sourceDatabaseSha256));
        }

        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var preview = await BuildProfilePreviewAsync(
            db,
            sourceDatabaseSha256,
            cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return preview;
    }

    public static void WriteAdvisoryCommandOutput(
        TextWriter output,
        IsolatedLegacyInvoiceSeedCanonicalizationResult result)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            output.Write(result.CommandOutput);
            output.Flush();
        }
        catch (Exception)
        {
        }
    }

#if GEORAEPLAN_CANONICALIZER_TESTING
    internal static Task<
            IsolatedLegacyInvoiceSeedCanonicalizationProfilePreview>
        PreviewProfileForTestsAsync(
            LocalDbContext db,
            string sourceDatabaseSha256 =
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            CancellationToken cancellationToken = default)
        => BuildProfilePreviewAsync(
            db,
            sourceDatabaseSha256,
            cancellationToken);

    internal static async Task<
            IsolatedLegacyInvoiceSeedCanonicalizationReport>
        CanonicalizeForTestsAsync(
            LocalDbContext db,
            string sourceDatabaseSha256 =
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            bool rejectPartialPushState = true,
            CancellationToken cancellationToken = default,
            IsolatedLegacyInvoiceSeedCanonicalizationFault fault =
                IsolatedLegacyInvoiceSeedCanonicalizationFault.None,
            string? recoveryResultPath = null)
        => (await CanonicalizeTransactionAsync(
                db,
                sourceDatabaseSha256,
                new CanonicalizationTestAdapter(
                    rejectPartialPushState,
                    RequiredProfile: null,
                    fault,
                    PreparationLease: null,
                    Authorization: null,
                    recoveryResultPath,
                    RecoveryRootPath:
                        recoveryResultPath is null
                            ? null
                            : Path.GetDirectoryName(
                                Path.GetFullPath(recoveryResultPath))),
                cancellationToken))
            .Report;

    internal static async Task<
            IsolatedLegacyInvoiceSeedCanonicalizationReport>
        CanonicalizeWithProfileForTestsAsync(
            LocalDbContext db,
            IsolatedLegacyInvoiceSeedCanonicalizationProfile requiredProfile,
            bool rejectPartialPushState = true,
            CancellationToken cancellationToken = default,
            IsolatedLegacyInvoiceSeedCanonicalizationFault fault =
                IsolatedLegacyInvoiceSeedCanonicalizationFault.None,
            string? recoveryResultPath = null)
    {
        ArgumentNullException.ThrowIfNull(requiredProfile);
        return (await CanonicalizeTransactionAsync(
            db,
            requiredProfile.SourceDatabaseSha256,
            new CanonicalizationTestAdapter(
                rejectPartialPushState,
                requiredProfile,
                fault,
                PreparationLease: null,
                Authorization: null,
                recoveryResultPath,
                RecoveryRootPath:
                    recoveryResultPath is null
                        ? null
                        : Path.GetDirectoryName(
                            Path.GetFullPath(recoveryResultPath))),
            cancellationToken)).Report;
    }

    internal static void AssertDistinctInvoiceIdsForTests(
        IReadOnlyList<LocalInvoice> invoices)
        => AssertDistinctInvoiceIds(invoices);

    internal static void AssertApprovedSourceDatabaseSha256ForTests(
        string sourceDatabaseSha256)
        => AssertApprovedSourceDatabaseSha256(sourceDatabaseSha256);

    internal static string AssertAuthorizationEnvironmentForTests(
        string sourceAttestationText)
        => AssertAuthorizationEnvironment(sourceAttestationText);

    internal static string AssertProfileInspectionEnvironmentForTests(
        string sourceAttestationText)
        => AssertProfileInspectionEnvironment(sourceAttestationText);

    internal static IsolatedLegacyInvoiceSeedCanonicalizationProfile
        ApprovedProfileForTests
        => ApprovedProfile;

    internal static IsolatedLegacyInvoiceSeedCanonicalizationProfile
        ApprovedProfileForSourceDatabaseSha256ForTests(
            string sourceDatabaseSha256)
        => GetApprovedProfile(sourceDatabaseSha256);

    internal static IsolatedLegacyInvoiceSeedCanonicalizationResult
        BuildResultForTests(
            IsolatedLegacyInvoiceSeedCanonicalizationReport report)
        => BuildResult(report);

    internal static async Task<(int Count, string Sha256)>
        BuildNonAcknowledgedOutboxEvidenceForTestsAsync(
            LocalDbContext db,
            CancellationToken cancellationToken = default)
    {
        var evidence = await BuildNonAcknowledgedOutboxEvidenceAsync(
            db,
            cancellationToken);
        return (evidence.Count, evidence.Sha256);
    }
#endif

    internal static string ComputeSha256(string value)
        => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string ComputeDomainSeparatedSha256(
        string domain,
        params string[] values)
        => ComputeSha256(string.Join(
            "\n",
            [$"georaeplan:{domain}:v1", .. values]));

    private static string AssertAuthorizationEnvironment(
        string sourceAttestationText)
        => AssertAuthorizationEnvironmentCore(
            sourceAttestationText,
            ExplicitOptInEnvironmentKey,
            requireApprovedSource: true,
            operationName: "canonicalization");

    private static string AssertProfileInspectionEnvironment(
        string sourceAttestationText)
        => AssertAuthorizationEnvironmentCore(
            sourceAttestationText,
            ProfileInspectionOptInEnvironmentKey,
            requireApprovedSource: false,
            operationName: "profile inspection");

    private static string AssertAuthorizationEnvironmentCore(
        string sourceAttestationText,
        string operationOptInEnvironmentKey,
        bool requireApprovedSource,
        string operationName)
    {
        if (!IsTruthy(
                Environment.GetEnvironmentVariable(
                    "GEORAEPLAN_TEST_MODE")) ||
            !IsTruthy(
                Environment.GetEnvironmentVariable(
                    "GEORAEPLAN_TEST_SEED_MODE")) ||
            !IsTruthy(
                Environment.GetEnvironmentVariable(
                    operationOptInEnvironmentKey)))
        {
            throw new InvalidOperationException(
                $"Legacy invoice seed {operationName} requires explicit isolated test, seed, and operation opt-in flags.");
        }

        var expectedSourceDatabaseSha256 =
            Environment.GetEnvironmentVariable(
                SourceDatabaseSha256EnvironmentKey)?.Trim()
            ?? string.Empty;
        if (!IsSha256(expectedSourceDatabaseSha256))
        {
            throw new InvalidOperationException(
                "Legacy invoice seed canonicalization requires an explicit source database SHA-256.");
        }
        if (requireApprovedSource)
            AssertApprovedSourceDatabaseSha256(expectedSourceDatabaseSha256);

        var attestation = JsonSerializer.Deserialize<SourceAttestation>(
            sourceAttestationText,
            ReportJsonOptions);
        if (attestation is null ||
            attestation.SchemaVersion != 1 ||
            !string.Equals(
                attestation.DatabaseSha256,
                expectedSourceDatabaseSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The pristine isolated source attestation does not match the expected source database SHA-256.");
        }
        return expectedSourceDatabaseSha256.ToUpperInvariant();
    }

    private static async Task<IsolatedLegacyInvoiceSeedCanonicalizationResult>
        CanonicalizeTransactionAsync(
            LocalDbContext db,
            string sourceDatabaseSha256,
#if GEORAEPLAN_CANONICALIZER_TESTING
            CanonicalizationTestAdapter testAdapter,
#else
            IsolatedPreparationDatabaseLease preparationLease,
            AuthorizationLease authorization,
#endif
            CancellationToken cancellationToken)
    {
#if GEORAEPLAN_CANONICALIZER_TESTING
        if (!IsSha256(sourceDatabaseSha256))
        {
            throw new ArgumentException(
                "A valid source database SHA-256 is required.",
                nameof(sourceDatabaseSha256));
        }
#endif
        var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var commitConfirmed = false;
        try
        {
#if GEORAEPLAN_CANONICALIZER_TESTING
        var requiredProfile = testAdapter.RequiredProfile;
        var recoveryResultPath = testAdapter.RecoveryResultPath;
        var recoveryRootPath = testAdapter.RecoveryRootPath;
#else
        var requiredProfile = GetApprovedProfile(sourceDatabaseSha256);
        var recoveryResultPath = authorization.RecoveryResultPath;
        var recoveryRootPath = authorization.GuardedRoot;
#endif

        var nonAcknowledgedOutboxEvidence =
            await BuildNonAcknowledgedOutboxEvidenceAsync(
                db,
                cancellationToken);
#if GEORAEPLAN_CANONICALIZER_TESTING
        if (testAdapter.RejectPartialPushState)
        {
            if (requiredProfile is null)
            {
                if (nonAcknowledgedOutboxEvidence.Count > 0)
                {
                    throw Reject(
                        "partial_push_outbox_present",
                        evidenceSha256:
                            nonAcknowledgedOutboxEvidence.Sha256);
                }
            }
            else
            {
                AssertAuthorizedNonAcknowledgedOutbox(
                    requiredProfile,
                    nonAcknowledgedOutboxEvidence);
            }
        }
#else
        AssertAuthorizedNonAcknowledgedOutbox(
            requiredProfile,
            nonAcknowledgedOutboxEvidence);
#endif

        var before = await LoadSnapshotAsync(db, cancellationToken);
        var plan = BuildPlan(before);
        if (plan.Groups.Count == 0 &&
            requiredProfile is not null &&
            !string.IsNullOrWhiteSpace(recoveryResultPath))
        {
            var recovered = RecoverCommittedResult(
                before,
                requiredProfile,
                recoveryRootPath ??
                    throw Reject("recovery_artifact_root_missing"),
                recoveryResultPath);
#if GEORAEPLAN_CANONICALIZER_TESTING
            testAdapter.Authorization?.AssertStable();
            testAdapter.PreparationLease?.AssertStable();
#else
            authorization.AssertStable();
            preparationLease.AssertStable();
#endif
            return recovered;
        }
        if (requiredProfile is not null)
        {
            ValidateRequiredProfileBefore(
                sourceDatabaseSha256,
                before,
                plan,
                requiredProfile);
        }

        var changedUpdates = plan.Updates
            .Where(update => update.ChangedMetadataFields.Count > 0)
            .ToList();
        var affectedRows = 0;
        foreach (var update in changedUpdates)
        {
            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "Invoices"
                 SET "VersionGroupId" = {update.VersionGroupId},
                     "VersionNumber" = {update.VersionNumber},
                     "PreviousVersionId" = {update.PreviousVersionId},
                     "IsLatestVersion" = {update.IsLatestVersion},
                     "ResponsibleOfficeCode" = {update.ResponsibleOfficeCode}
                 WHERE "Id" = {update.InvoiceId}
                 """,
                cancellationToken);
            if (affected != 1)
                throw Reject("update_affected_row_count_mismatch");
            affectedRows += affected;
#if GEORAEPLAN_CANONICALIZER_TESTING
            if (affectedRows == 1)
            {
                if (testAdapter.Fault ==
                    IsolatedLegacyInvoiceSeedCanonicalizationFault
                        .CancelAfterFirstUpdate)
                {
                    throw new OperationCanceledException(
                        "Injected cancellation after the first update.",
                        cancellationToken);
                }
                if (testAdapter.Fault ==
                    IsolatedLegacyInvoiceSeedCanonicalizationFault
                        .ThrowAfterFirstUpdate)
                {
                    throw new InjectedCanonicalizationFaultException();
                }
            }
#endif
        }
        if (affectedRows != changedUpdates.Count)
        {
            throw Reject("total_affected_row_count_mismatch");
        }
#if GEORAEPLAN_CANONICALIZER_TESTING
        if (testAdapter.RequiredProfile is not null &&
            affectedRows !=
            testAdapter.RequiredProfile.ChangedInvoiceCount)
        {
            throw Reject("total_affected_row_count_mismatch");
        }
#else
        if (requiredProfile is null)
            throw Reject("source_database_sha256_not_approved");
        if (affectedRows != requiredProfile.ChangedInvoiceCount)
            throw Reject("total_affected_row_count_mismatch");
#endif

        var after = await LoadSnapshotAsync(db, cancellationToken);
        VerifyAfterState(before, after, plan);
        if (requiredProfile is not null)
        {
            ValidateRequiredProfileAfter(
                after,
                plan,
                requiredProfile);
        }

        var report = BuildReport(
            sourceDatabaseSha256.ToUpperInvariant(),
            before,
            after,
            plan);
        var result = BuildResult(report);
        if (requiredProfile is not null &&
            !string.IsNullOrWhiteSpace(recoveryResultPath))
        {
            WriteRecoveryArtifact(
                recoveryRootPath ??
                    throw Reject("recovery_artifact_root_missing"),
                recoveryResultPath,
                requiredProfile,
                after,
                result
#if GEORAEPLAN_CANONICALIZER_TESTING
                ,
                testAdapter.Fault
#endif
                );
        }
#if GEORAEPLAN_CANONICALIZER_TESTING
        if (testAdapter.Fault ==
            IsolatedLegacyInvoiceSeedCanonicalizationFault
                .ThrowBeforeCommit)
        {
            throw new InjectedCanonicalizationFaultException();
        }
        testAdapter.Authorization?.AssertStable();
        testAdapter.PreparationLease?.AssertStable();
#else
        authorization.AssertStable();
        preparationLease.AssertStable();
#endif
        await transaction.CommitAsync(cancellationToken);
        commitConfirmed = true;
        return result;
        }
        finally
        {
            if (commitConfirmed)
            {
                try
                {
                    await transaction.DisposeAsync();
#if GEORAEPLAN_CANONICALIZER_TESTING
                    if (testAdapter.Fault ==
                        IsolatedLegacyInvoiceSeedCanonicalizationFault
                            .ThrowDuringPostcommitDispose)
                    {
                        throw new InjectedCanonicalizationFaultException();
                    }
#endif
                }
                catch (Exception)
                {
                }
            }
            else
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static CanonicalizationPlan BuildPlan(DatabaseSnapshot snapshot)
    {
        AssertDistinctInvoiceIds(snapshot.Invoices);

        var invoiceById = snapshot.Invoices.ToDictionary(
            invoice => invoice.Id);
        var customerById = snapshot.Customers.ToDictionary(
            customer => customer.Id);
        var plans = new List<GroupPlan>();

        foreach (var rawGroup in snapshot.Invoices
                     .GroupBy(invoice => invoice.VersionGroupId)
                     .OrderBy(group => FormatId(group.Key)))
        {
            var groupId = rawGroup.Key;
            var members = rawGroup
                .OrderBy(invoice => FormatId(invoice.Id))
                .ToList();
            var active = members
                .Where(invoice => !invoice.IsDeleted)
                .ToList();
            if (active.Count == 0)
                continue;

            var structural = InspectStructure(
                groupId,
                members,
                active,
                invoiceById);
            var responsibleScopeDiffers =
                active.Select(invoice =>
                        NormalizeScope(invoice.ResponsibleOfficeCode))
                    .Distinct(StringComparer.Ordinal)
                    .Count() > 1;
            var protectedScopeDiffers =
                active.Select(invoice => invoice.CustomerId).Distinct().Count() > 1 ||
                active.Select(invoice => NormalizeScope(invoice.TenantCode))
                    .Distinct(StringComparer.Ordinal).Count() > 1 ||
                active.Select(invoice => NormalizeScope(invoice.OfficeCode))
                    .Distinct(StringComparer.Ordinal).Count() > 1;
            var hasCandidateAnomaly =
                responsibleScopeDiffers ||
                protectedScopeDiffers ||
                structural.HasAnomaly;
            if (!hasCandidateAnomaly)
                continue;

            if (groupId == Guid.Empty)
                throw Reject("ambiguous_version_group", groupId);

            var latest = active
                .Where(invoice => invoice.IsLatestVersion)
                .ToList();
            if (latest.Count == 0)
                throw Reject("active_latest_missing", groupId);

            List<LocalInvoice>? precomputedScopeOrder = null;
            LocalInvoice canonicalLatest;
            if (latest.Count == 1)
            {
                canonicalLatest = latest[0];
            }
            else if (responsibleScopeDiffers &&
                     !protectedScopeDiffers &&
                     !structural.HasAnomaly &&
                     TryBuildStrictLinearOrder(
                         groupId,
                         active,
                         requireRootGroupIdentity: true,
                         allowDeletedPredecessorRoot: false,
                         members,
                         out var multipleLatestScopeOrder) &&
                     multipleLatestScopeOrder[^1].IsLatestVersion)
            {
                precomputedScopeOrder = multipleLatestScopeOrder;
                canonicalLatest = multipleLatestScopeOrder[^1];
            }
            else
            {
                throw Reject("active_latest_not_unique", groupId);
            }

            if (canonicalLatest.Revision <= 0)
                throw Reject("latest_revision_not_positive", groupId);
            if (canonicalLatest.CustomerId == Guid.Empty ||
                !customerById.TryGetValue(
                    canonicalLatest.CustomerId,
                    out var latestCustomer) ||
                latestCustomer.IsDeleted)
            {
                throw Reject("latest_customer_missing_or_deleted", groupId);
            }

            var nonLatestRevisions = active
                .Where(invoice => invoice.Id != canonicalLatest.Id)
                .Select(invoice => invoice.Revision)
                .ToList();
            if (nonLatestRevisions.Any(revision => revision <= 0))
                throw Reject("nonlatest_revision_not_positive", groupId);
            if (nonLatestRevisions.Distinct().Count() !=
                nonLatestRevisions.Count)
            {
                throw Reject("nonlatest_revision_not_unique", groupId);
            }

            if (protectedScopeDiffers)
                throw Reject("protected_scope_alignment_required", groupId);

            GroupPlan groupPlan;
            if (responsibleScopeDiffers)
            {
                List<LocalInvoice> scopeOrder;
                if (precomputedScopeOrder is not null)
                {
                    scopeOrder = precomputedScopeOrder;
                }
                else if (structural.HasAnomaly ||
                         !TryBuildStrictLinearOrder(
                             groupId,
                             active,
                             requireRootGroupIdentity: true,
                             allowDeletedPredecessorRoot: false,
                             members,
                             out scopeOrder))
                {
                    throw Reject(
                        "responsible_scope_shape_not_linear",
                        groupId);
                }

                if (scopeOrder[^1].Id != canonicalLatest.Id)
                    throw Reject("latest_not_terminal", groupId);
                groupPlan = BuildResponsibleOfficeAlignmentPlan(
                    groupId,
                    members,
                    scopeOrder,
                    canonicalLatest);
            }
            else if (structural.MaximumChildCount >
                     MaximumSiblingBranchWidth)
            {
                throw Reject("sibling_branch_width_exceeded", groupId);
            }
            else if (TryBuildDeletedPredecessorRerootPlan(
                         groupId,
                         members,
                         active,
                         canonicalLatest,
                         out groupPlan))
            {
            }
            else if (TryBuildDuplicateSiblingPlan(
                         groupId,
                         members,
                         active,
                         canonicalLatest,
                         out groupPlan))
            {
            }
            else
            {
                throw Reject(
                    structural.ReasonCode ??
                    "unsupported_legacy_invoice_shape",
                    groupId);
            }

            ValidateDependencies(snapshot, groupPlan);
            plans.Add(groupPlan);
        }

        var updates = plans
            .SelectMany(plan => plan.Updates)
            .OrderBy(update => FormatId(update.InvoiceId))
            .ToList();
        return new CanonicalizationPlan(plans, updates);
    }

    private static StructuralInspection InspectStructure(
        Guid groupId,
        IReadOnlyList<LocalInvoice> members,
        IReadOnlyList<LocalInvoice> active,
        IReadOnlyDictionary<Guid, LocalInvoice> invoiceById)
    {
        var activeIds = active.Select(invoice => invoice.Id).ToHashSet();
        var maximumChildCount = 0;
        var hasAnomaly =
            active.Select(invoice => invoice.VersionNumber).Distinct().Count() !=
            active.Count;
        string? reasonCode = null;

        foreach (var invoice in active)
        {
            var previousId = NormalizeReference(invoice.PreviousVersionId);
            if (!previousId.HasValue)
            {
                if (invoice.VersionNumber != 1 ||
                    invoice.Id != groupId)
                {
                    hasAnomaly = true;
                }

                continue;
            }

            if (!invoiceById.TryGetValue(previousId.Value, out var previous))
            {
                return new StructuralInspection(
                    true,
                    maximumChildCount,
                    "external_predecessor");
            }

            if (previous.VersionGroupId != groupId)
            {
                return new StructuralInspection(
                    true,
                    maximumChildCount,
                    "cross_group_predecessor");
            }

            if (previous.IsDeleted || !activeIds.Contains(previous.Id))
                hasAnomaly = true;
        }

        var childCounts = active
            .Where(invoice =>
                NormalizeReference(invoice.PreviousVersionId).HasValue)
            .GroupBy(invoice =>
                NormalizeReference(invoice.PreviousVersionId)!.Value)
            .Select(group => group.Count())
            .ToList();
        if (childCounts.Count > 0)
            maximumChildCount = childCounts.Max();
        if (maximumChildCount > 1)
            hasAnomaly = true;

        var activeRoots = active.Count(invoice =>
        {
            var previous = NormalizeReference(
                invoice.PreviousVersionId);
            return !previous.HasValue ||
                   (invoiceById.TryGetValue(
                        previous.Value,
                        out var predecessor) &&
                    predecessor.VersionGroupId == groupId &&
                    predecessor.IsDeleted);
        });
        if (activeRoots > 1)
        {
            hasAnomaly = true;
            reasonCode = "disconnected_active_chain";
        }

        if (HasCycle(members))
        {
            hasAnomaly = true;
            reasonCode = "invoice_version_cycle";
        }

        return new StructuralInspection(
            hasAnomaly,
            maximumChildCount,
            reasonCode);
    }

    private static GroupPlan BuildResponsibleOfficeAlignmentPlan(
        Guid groupId,
        IReadOnlyList<LocalInvoice> members,
        IReadOnlyList<LocalInvoice> order,
        LocalInvoice latest)
    {
        var updates = order.Select((invoice, index) =>
            new InvoiceMetadataUpdate(
                invoice.Id,
                invoice.VersionGroupId,
                invoice.VersionNumber,
                NormalizeReference(invoice.PreviousVersionId),
                invoice.Id == latest.Id,
                latest.ResponsibleOfficeCode,
                ChangedFields(
                    invoice,
                    invoice.VersionGroupId,
                    invoice.VersionNumber,
                    NormalizeReference(invoice.PreviousVersionId),
                    invoice.Id == latest.Id,
                    latest.ResponsibleOfficeCode))).ToList();
        return NewGroupPlan(
            groupId,
            ResponsibleOfficeAlignmentMode,
            members,
            order,
            latest,
            updates);
    }

    private static bool TryBuildDeletedPredecessorRerootPlan(
        Guid groupId,
        IReadOnlyList<LocalInvoice> members,
        IReadOnlyList<LocalInvoice> active,
        LocalInvoice latest,
        out GroupPlan plan)
    {
        plan = null!;
        var memberById = members.ToDictionary(invoice => invoice.Id);
        var activeRoots = active.Where(invoice =>
        {
            var previous = NormalizeReference(invoice.PreviousVersionId);
            return !previous.HasValue ||
                   (memberById.TryGetValue(previous.Value, out var predecessor) &&
                    predecessor.IsDeleted);
        }).ToList();
        var hasDeletedOrMissingRoot =
            activeRoots.Count == 1 &&
            (activeRoots[0].VersionNumber != 1 ||
             activeRoots[0].VersionGroupId != activeRoots[0].Id ||
             NormalizeReference(activeRoots[0].PreviousVersionId).HasValue);
        if (!hasDeletedOrMissingRoot)
            return false;

        if (!TryBuildStrictLinearOrder(
                groupId,
                active,
                requireRootGroupIdentity: false,
                allowDeletedPredecessorRoot: true,
                members,
                out var order))
        {
            throw Reject("reroot_active_chain_not_linear", groupId);
        }

        if (order[^1].Id != latest.Id)
            throw Reject("latest_not_terminal", groupId);

        var newGroupId = order[0].Id;
        var updates = order.Select((invoice, index) =>
        {
            var previousId =
                index == 0 ? (Guid?)null : order[index - 1].Id;
            return new InvoiceMetadataUpdate(
                invoice.Id,
                newGroupId,
                index + 1,
                previousId,
                invoice.Id == latest.Id,
                invoice.ResponsibleOfficeCode,
                ChangedFields(
                    invoice,
                    newGroupId,
                    index + 1,
                    previousId,
                    invoice.Id == latest.Id,
                    invoice.ResponsibleOfficeCode));
        }).ToList();
        plan = NewGroupPlan(
            groupId,
            DeletedPredecessorRerootMode,
            members,
            order,
            latest,
            updates);
        return true;
    }

    private static bool TryBuildDuplicateSiblingPlan(
        Guid groupId,
        IReadOnlyList<LocalInvoice> members,
        IReadOnlyList<LocalInvoice> active,
        LocalInvoice latest,
        out GroupPlan plan)
    {
        plan = null!;
        var activeById = active.ToDictionary(invoice => invoice.Id);
        var roots = active
            .Where(invoice =>
                !NormalizeReference(invoice.PreviousVersionId).HasValue)
            .ToList();
        if (roots.Count != 1 ||
            roots[0].Id != groupId)
        {
            return false;
        }

        var children = active
            .Where(invoice =>
                NormalizeReference(invoice.PreviousVersionId).HasValue)
            .GroupBy(invoice =>
                NormalizeReference(invoice.PreviousVersionId)!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        var branchParents = children
            .Where(pair => pair.Value.Count > 1)
            .ToList();
        if (branchParents.Count != 1)
            return false;
        if (branchParents[0].Value.Count >
            MaximumSiblingBranchWidth)
        {
            throw Reject("sibling_branch_width_exceeded", groupId);
        }
        if (branchParents[0].Value.Count !=
            MaximumSiblingBranchWidth)
        {
            return false;
        }

        var branchParentId = branchParents[0].Key;
        if (!activeById.ContainsKey(branchParentId))
            throw Reject("disconnected_active_chain", groupId);
        var siblings = branchParents[0].Value;
        if (siblings.Any(sibling =>
                children.TryGetValue(
                    sibling.Id,
                    out var descendants) &&
                descendants.Count > 0))
        {
            throw Reject("branched_sibling_has_descendant", groupId);
        }
        if (!siblings.Any(sibling => sibling.Id == latest.Id))
            throw Reject("latest_not_branch_terminal", groupId);

        var prefix = new List<LocalInvoice>();
        var current = roots[0];
        var visited = new HashSet<Guid>();
        while (true)
        {
            if (!visited.Add(current.Id))
                throw Reject("invoice_version_cycle", groupId);
            prefix.Add(current);
            if (current.Id == branchParentId)
                break;

            if (!children.TryGetValue(current.Id, out var next) ||
                next.Count != 1)
            {
                throw Reject("disconnected_active_chain", groupId);
            }

            current = next[0];
        }

        if (prefix.Count + siblings.Count != active.Count)
            throw Reject("disconnected_active_chain", groupId);

        var nonLatestSiblings = siblings
            .Where(sibling => sibling.Id != latest.Id)
            .OrderBy(sibling => sibling.Revision)
            .ThenBy(sibling => FormatId(sibling.Id))
            .ToList();
        if (nonLatestSiblings.Any(sibling => sibling.Revision <= 0) ||
            nonLatestSiblings.Select(sibling => sibling.Revision)
                .Distinct().Count() != nonLatestSiblings.Count)
        {
            throw Reject("sibling_revision_order_not_proven", groupId);
        }
        if (latest.Revision <=
            nonLatestSiblings.Max(sibling => sibling.Revision))
        {
            throw Reject("latest_revision_not_after_sibling", groupId);
        }

        var order = prefix
            .Concat(nonLatestSiblings)
            .Append(latest)
            .ToList();
        var updates = order.Select((invoice, index) =>
        {
            var previousId =
                index == 0 ? (Guid?)null : order[index - 1].Id;
            return new InvoiceMetadataUpdate(
                invoice.Id,
                groupId,
                index + 1,
                previousId,
                invoice.Id == latest.Id,
                invoice.ResponsibleOfficeCode,
                ChangedFields(
                    invoice,
                    groupId,
                    index + 1,
                    previousId,
                    invoice.Id == latest.Id,
                    invoice.ResponsibleOfficeCode));
        }).ToList();
        plan = NewGroupPlan(
            groupId,
            DuplicateSiblingMode,
            members,
            order,
            latest,
            updates);
        return true;
    }

    private static bool TryBuildStrictLinearOrder(
        Guid groupId,
        IReadOnlyList<LocalInvoice> active,
        bool requireRootGroupIdentity,
        bool allowDeletedPredecessorRoot,
        IReadOnlyList<LocalInvoice> allMembers,
        out List<LocalInvoice> order)
    {
        order = [];
        var memberById = allMembers.ToDictionary(invoice => invoice.Id);
        var activeById = active.ToDictionary(invoice => invoice.Id);
        var roots = active.Where(invoice =>
        {
            var previous = NormalizeReference(invoice.PreviousVersionId);
            if (!previous.HasValue)
                return true;

            return allowDeletedPredecessorRoot &&
                   memberById.TryGetValue(
                       previous.Value,
                       out var predecessor) &&
                   predecessor.IsDeleted;
        }).ToList();
        if (roots.Count != 1)
            return false;
        if (requireRootGroupIdentity &&
            roots[0].Id != groupId)
        {
            return false;
        }

        var children = active
            .Where(invoice =>
                NormalizeReference(invoice.PreviousVersionId).HasValue &&
                activeById.ContainsKey(
                    NormalizeReference(invoice.PreviousVersionId)!.Value))
            .GroupBy(invoice =>
                NormalizeReference(invoice.PreviousVersionId)!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        if (children.Values.Any(group => group.Count != 1))
            return false;

        var current = roots[0];
        var visited = new HashSet<Guid>();
        while (true)
        {
            if (!visited.Add(current.Id))
                return false;
            order.Add(current);
            if (!children.TryGetValue(current.Id, out var next))
                break;
            current = next[0];
        }

        if (order.Count != active.Count)
            return false;

        if (!allowDeletedPredecessorRoot)
        {
            for (var index = 0; index < order.Count; index++)
            {
                var expectedPrevious =
                    index == 0 ? (Guid?)null : order[index - 1].Id;
                if (order[index].VersionNumber != index + 1 ||
                    NormalizeReference(order[index].PreviousVersionId) !=
                    expectedPrevious)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void ValidateDependencies(
        DatabaseSnapshot snapshot,
        GroupPlan plan)
    {
        var allIds = plan.AllMemberIds;
        var activeIds = plan.ActiveInvoiceIds;
        var lineById = snapshot.InvoiceLines.ToDictionary(line => line.Id);

        void RequireActive(
            Guid invoiceId,
            string reasonCode)
        {
            if (allIds.Contains(invoiceId) &&
                !activeIds.Contains(invoiceId))
            {
                throw Reject(reasonCode, plan.OriginalGroupId);
            }
        }

        foreach (var line in snapshot.InvoiceLines)
            RequireActive(line.InvoiceId, "deleted_invoice_line_dependency");
        foreach (var payment in snapshot.Payments)
            RequireActive(payment.InvoiceId, "deleted_invoice_payment_dependency");
        foreach (var transaction in snapshot.Transactions)
        {
            if (transaction.LinkedInvoiceId.HasValue)
            {
                RequireActive(
                    transaction.LinkedInvoiceId.Value,
                    "deleted_invoice_transaction_dependency");
            }
        }
        foreach (var serial in snapshot.InvoiceLineSerials)
        {
            RequireActive(
                serial.InvoiceId,
                "deleted_invoice_serial_dependency");
            if (allIds.Contains(serial.InvoiceId) &&
                (!lineById.TryGetValue(
                     serial.InvoiceLineId,
                     out var line) ||
                 line.InvoiceId != serial.InvoiceId))
            {
                throw Reject(
                    "invoice_serial_line_mismatch",
                    plan.OriginalGroupId);
            }
        }
        foreach (var movement in snapshot.InventoryMovements)
        {
            if (!movement.InvoiceId.HasValue)
                continue;
            RequireActive(
                movement.InvoiceId.Value,
                "deleted_invoice_inventory_dependency");
            if (allIds.Contains(movement.InvoiceId.Value) &&
                movement.InvoiceLineId.HasValue &&
                (!lineById.TryGetValue(
                     movement.InvoiceLineId.Value,
                     out var line) ||
                 line.InvoiceId != movement.InvoiceId.Value))
            {
                throw Reject(
                    "inventory_invoice_line_mismatch",
                    plan.OriginalGroupId);
            }
        }
        foreach (var allocation in snapshot.CostAllocations)
        {
            RequireActive(
                allocation.SalesInvoiceId,
                "deleted_sales_invoice_cost_dependency");
            if (allIds.Contains(allocation.SalesInvoiceId) &&
                (!lineById.TryGetValue(
                     allocation.SalesInvoiceLineId,
                     out var salesLine) ||
                 salesLine.InvoiceId != allocation.SalesInvoiceId))
            {
                throw Reject(
                    "sales_cost_invoice_line_mismatch",
                    plan.OriginalGroupId);
            }

            if (!allocation.PurchaseInvoiceId.HasValue)
                continue;
            RequireActive(
                allocation.PurchaseInvoiceId.Value,
                "deleted_purchase_invoice_cost_dependency");
            if (allIds.Contains(allocation.PurchaseInvoiceId.Value) &&
                allocation.PurchaseInvoiceLineId.HasValue &&
                (!lineById.TryGetValue(
                     allocation.PurchaseInvoiceLineId.Value,
                     out var purchaseLine) ||
                 purchaseLine.InvoiceId !=
                 allocation.PurchaseInvoiceId.Value))
            {
                throw Reject(
                    "purchase_cost_invoice_line_mismatch",
                    plan.OriginalGroupId);
            }
        }
        foreach (var layer in snapshot.StockLayers)
        {
            if (!layer.SourceInvoiceId.HasValue)
                continue;
            RequireActive(
                layer.SourceInvoiceId.Value,
                "deleted_invoice_stock_layer_dependency");
            if (allIds.Contains(layer.SourceInvoiceId.Value) &&
                layer.SourceInvoiceLineId.HasValue &&
                (!lineById.TryGetValue(
                     layer.SourceInvoiceLineId.Value,
                     out var sourceLine) ||
                 sourceLine.InvoiceId != layer.SourceInvoiceId.Value))
            {
                throw Reject(
                    "stock_layer_invoice_line_mismatch",
                    plan.OriginalGroupId);
            }
        }
        foreach (var ledger in snapshot.SerialLedgers)
        {
            if (ledger.SourcePurchaseInvoiceId.HasValue)
            {
                RequireActive(
                    ledger.SourcePurchaseInvoiceId.Value,
                    "deleted_purchase_invoice_serial_dependency");
            }
            if (ledger.SourceSalesInvoiceId.HasValue)
            {
                RequireActive(
                    ledger.SourceSalesInvoiceId.Value,
                    "deleted_sales_invoice_serial_dependency");
            }
            if (ledger.LastInvoiceId.HasValue)
            {
                RequireActive(
                    ledger.LastInvoiceId.Value,
                    "deleted_last_invoice_serial_dependency");
            }
        }

        var activeRentalProfileIds = snapshot.RentalBillingProfiles
            .Where(profile => !profile.IsDeleted)
            .Select(profile => profile.Id)
            .ToHashSet();
        foreach (var invoice in snapshot.Invoices.Where(invoice =>
                     allIds.Contains(invoice.Id)))
        {
            ValidateRentalReference(
                invoice.LinkedRentalBillingProfileId,
                invoice.LinkedRentalBillingRunId,
                activeRentalProfileIds,
                plan.OriginalGroupId);
        }
        foreach (var transaction in snapshot.Transactions.Where(transaction =>
                     transaction.LinkedInvoiceId.HasValue &&
                     activeIds.Contains(transaction.LinkedInvoiceId.Value)))
        {
            ValidateRentalReference(
                transaction.LinkedRentalBillingProfileId,
                transaction.LinkedRentalBillingRunId,
                activeRentalProfileIds,
                plan.OriginalGroupId);
        }
    }

    private static void ValidateRentalReference(
        Guid? profileId,
        Guid? runId,
        IReadOnlySet<Guid> activeRentalProfileIds,
        Guid groupId)
    {
        var normalizedProfileId = NormalizeReference(profileId);
        var normalizedRunId = NormalizeReference(runId);
        if (normalizedRunId.HasValue &&
            !normalizedProfileId.HasValue)
        {
            throw Reject(
                "rental_run_without_profile",
                groupId);
        }
        if (normalizedProfileId.HasValue &&
            !activeRentalProfileIds.Contains(normalizedProfileId.Value))
        {
            throw Reject(
                "rental_profile_missing_or_deleted",
                groupId);
        }
    }

    private static void ValidateRequiredProfileBefore(
        string sourceDatabaseSha256,
        DatabaseSnapshot before,
        CanonicalizationPlan plan,
        IsolatedLegacyInvoiceSeedCanonicalizationProfile requiredProfile)
    {
        if (!string.Equals(
                sourceDatabaseSha256,
                requiredProfile.SourceDatabaseSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Reject("required_source_database_sha256_mismatch");
        }

        var changedInvoiceIds = plan.Updates
            .Where(update => update.ChangedMetadataFields.Count > 0)
            .Select(update => update.InvoiceId)
            .Distinct()
            .ToHashSet();
        var activeInvoiceIds = plan.Groups
            .SelectMany(group => group.ActiveInvoiceIds)
            .Distinct()
            .ToHashSet();
        if (!changedInvoiceIds.IsSubsetOf(activeInvoiceIds))
            throw Reject("required_active_changed_invoice_scope_mismatch");

        RequireProfileValue(
            plan.Groups.Count == requiredProfile.ChangedGroupCount,
            "required_changed_group_count_mismatch");
        RequireProfileValue(
            changedInvoiceIds.Count == requiredProfile.ChangedInvoiceCount,
            "required_changed_invoice_count_mismatch");
        RequireProfileValue(
            plan.Groups.Sum(group =>
                group.ExcludedDeletedInvoiceIds.Count) ==
            requiredProfile.ExcludedDeletedInvoiceCount,
            "required_excluded_deleted_invoice_count_mismatch");
        RequireProfileValue(
            plan.Groups.Count(group =>
                string.Equals(
                    group.Mode,
                    DeletedPredecessorRerootMode,
                    StringComparison.Ordinal)) ==
            requiredProfile.DeletedPredecessorRerootGroupCount,
            "required_reroot_mode_count_mismatch");
        RequireProfileValue(
            plan.Groups.Count(group =>
                string.Equals(
                    group.Mode,
                    DuplicateSiblingMode,
                    StringComparison.Ordinal)) ==
            requiredProfile.DuplicateSiblingGroupCount,
            "required_sibling_mode_count_mismatch");
        RequireProfileValue(
            plan.Groups.Count(group =>
                string.Equals(
                    group.Mode,
                    ResponsibleOfficeAlignmentMode,
                    StringComparison.Ordinal)) ==
            requiredProfile.ResponsibleOfficeAlignmentGroupCount,
            "required_office_alignment_mode_count_mismatch");

        var beforeById = before.Invoices.ToDictionary(invoice => invoice.Id);
        var allMemberIds = plan.Groups
            .SelectMany(group => group.AllMemberIds)
            .Distinct()
            .ToHashSet();
        RequireProfileHash(
            BuildMetadataHash(allMemberIds.Select(id => beforeById[id])),
            requiredProfile.BeforeMetadataSha256,
            "required_before_metadata_mismatch");
        RequireProfileHash(
            ComputeSha256(string.Join(
                "\n",
                activeInvoiceIds.OrderBy(FormatId).Select(FormatId))),
            requiredProfile.ActiveInvoiceIdsSha256,
            "required_active_invoice_ids_mismatch");
        RequireProfileHash(
            BuildLatestBusinessHash(before, plan.Groups),
            requiredProfile.LatestInvoiceBusinessSha256,
            "required_latest_business_mismatch");
        RequireProfileHash(
            BuildDependencyReferencesHash(before, allMemberIds),
            requiredProfile.DependencyReferencesSha256,
            "required_dependency_references_mismatch");
    }

    private static void ValidateRequiredProfileAfter(
        DatabaseSnapshot after,
        CanonicalizationPlan plan,
        IsolatedLegacyInvoiceSeedCanonicalizationProfile requiredProfile)
    {
        var afterById = after.Invoices.ToDictionary(invoice => invoice.Id);
        var allMemberIds = plan.Groups
            .SelectMany(group => group.AllMemberIds)
            .Distinct()
            .ToHashSet();
        RequireProfileHash(
            BuildMetadataHash(allMemberIds.Select(id => afterById[id])),
            requiredProfile.AfterMetadataSha256,
            "required_after_metadata_mismatch");
    }

    private static void RequireProfileValue(
        bool matches,
        string reasonCode)
    {
        if (!matches)
            throw Reject(reasonCode);
    }

    private static void RequireProfileHash(
        string actual,
        string expected,
        string reasonCode)
    {
        if (!IsSha256(expected) ||
            !string.Equals(
                actual,
                expected,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Reject(
                reasonCode,
                evidenceSha256: IsSha256(actual) ? actual : null);
        }
    }

    private static void VerifyAfterState(
        DatabaseSnapshot before,
        DatabaseSnapshot after,
        CanonicalizationPlan plan)
    {
        var changedIds = plan.Groups
            .SelectMany(group => group.AllMemberIds)
            .ToHashSet();
        var beforeActiveIds = plan.Groups
            .SelectMany(group => group.ActiveInvoiceIds)
            .OrderBy(FormatId)
            .ToList();
        var afterById = after.Invoices.ToDictionary(invoice => invoice.Id);
        var afterActiveIds = beforeActiveIds
            .Where(id =>
                afterById.TryGetValue(id, out var invoice) &&
                !invoice.IsDeleted)
            .OrderBy(FormatId)
            .ToList();
        if (!beforeActiveIds.SequenceEqual(afterActiveIds))
            throw Reject("after_active_invoice_ids_changed");

        if (!string.Equals(
                BuildDependencyReferencesHash(before, changedIds),
                BuildDependencyReferencesHash(after, changedIds),
                StringComparison.Ordinal))
        {
            throw Reject("after_dependency_references_changed");
        }
        if (!string.Equals(
                BuildProtectedDependencyStateHash(
                    before,
                    changedIds),
                BuildProtectedDependencyStateHash(
                    after,
                    changedIds),
                StringComparison.Ordinal))
        {
            throw Reject("after_dependency_business_state_changed");
        }

        var beforeById = before.Invoices.ToDictionary(invoice => invoice.Id);
        foreach (var invoiceId in changedIds)
        {
            if (!afterById.TryGetValue(invoiceId, out var afterInvoice) ||
                !string.Equals(
                    BuildImmutableInvoiceHash(beforeById[invoiceId]),
                    BuildImmutableInvoiceHash(afterInvoice),
                    StringComparison.Ordinal))
            {
                throw Reject("after_protected_invoice_state_changed");
            }
        }

        foreach (var group in plan.Groups)
        {
            var active = group.ActiveInvoiceIds
                .Select(id => afterById[id])
                .OrderBy(invoice => invoice.VersionNumber)
                .ThenBy(invoice => FormatId(invoice.Id))
                .ToList();
            if (active.Count == 0 ||
                active.Count(invoice => invoice.IsLatestVersion) != 1 ||
                active[^1].Id != group.LatestInvoiceId)
            {
                throw Reject(
                    "after_latest_invariant_failed",
                    group.OriginalGroupId);
            }

            var normalizedGroupId = active[0].Id;
            for (var index = 0; index < active.Count; index++)
            {
                var invoice = active[index];
                var expectedPrevious =
                    index == 0 ? (Guid?)null : active[index - 1].Id;
                if (invoice.VersionGroupId != normalizedGroupId ||
                    invoice.VersionNumber != index + 1 ||
                    NormalizeReference(invoice.PreviousVersionId) !=
                    expectedPrevious)
                {
                    throw Reject(
                        "after_linear_chain_invariant_failed",
                        group.OriginalGroupId);
                }
            }

            var latest = active[^1];
            if (active.Any(invoice =>
                    invoice.CustomerId != latest.CustomerId ||
                    !string.Equals(
                        NormalizeScope(invoice.TenantCode),
                        NormalizeScope(latest.TenantCode),
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        NormalizeScope(invoice.OfficeCode),
                        NormalizeScope(latest.OfficeCode),
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        NormalizeScope(invoice.ResponsibleOfficeCode),
                        NormalizeScope(latest.ResponsibleOfficeCode),
                        StringComparison.Ordinal)))
            {
                throw Reject(
                    "after_scope_invariant_failed",
                    group.OriginalGroupId);
            }
        }
    }

    private static IsolatedLegacyInvoiceSeedCanonicalizationReport
        BuildReport(
            string sourceDatabaseSha256,
            DatabaseSnapshot before,
            DatabaseSnapshot after,
            CanonicalizationPlan plan)
    {
        var beforeById = before.Invoices.ToDictionary(invoice => invoice.Id);
        var afterById = after.Invoices.ToDictionary(invoice => invoice.Id);
        var changedIds = plan.Updates
            .Where(update => update.ChangedMetadataFields.Count > 0)
            .Select(update => update.InvoiceId)
            .ToHashSet();
        var allActiveIds = plan.Groups
            .SelectMany(group => group.ActiveInvoiceIds)
            .Distinct()
            .OrderBy(FormatId)
            .ToList();
        var groupReports = plan.Groups
            .OrderBy(group => FormatId(group.OriginalGroupId))
            .Select((group, index) =>
            {
                var groupChangedFields = group.Updates
                    .SelectMany(update =>
                        update.ChangedMetadataFields)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList();
                return new IsolatedLegacyInvoiceSeedCanonicalizationGroup(
                    GroupOrdinal: index + 1,
                    GroupFingerprintSha256:
                        BuildGroupFingerprint(group),
                    group.Mode,
                    ActiveInvoiceCount: group.ActiveInvoiceIds.Count,
                    ExcludedDeletedInvoiceCount:
                        group.ExcludedDeletedInvoiceIds.Count,
                    groupChangedFields,
                    BuildMetadataHash(
                        group.AllMemberIds.Select(id => beforeById[id])),
                    BuildMetadataHash(
                        group.AllMemberIds.Select(id => afterById[id])));
            })
            .ToList();

        return new IsolatedLegacyInvoiceSeedCanonicalizationReport(
            SchemaVersion: 2,
            Succeeded: true,
            SourceDatabaseSha256: sourceDatabaseSha256,
            SeedScope: ActiveOperationalSeedScope,
            ChangedGroupCount: groupReports.Count,
            ChangedInvoiceCount: changedIds.Count,
            ExcludedDeletedInvoiceCount:
                plan.Groups.Sum(group =>
                    group.ExcludedDeletedInvoiceIds.Count),
            BeforeMetadataSha256:
                BuildMetadataHash(
                    plan.Groups
                        .SelectMany(group => group.AllMemberIds)
                        .Distinct()
                        .Select(id => beforeById[id])),
            AfterMetadataSha256:
                BuildMetadataHash(
                    plan.Groups
                        .SelectMany(group => group.AllMemberIds)
                        .Distinct()
                        .Select(id => afterById[id])),
            ActiveInvoiceIdsSha256:
                ComputeSha256(string.Join(
                    "\n",
                    allActiveIds.Select(FormatId))),
            LatestInvoiceBusinessSha256:
                BuildLatestBusinessHash(before, plan.Groups),
            DependencyReferencesSha256:
                BuildDependencyReferencesHash(
                    before,
                    plan.Groups
                        .SelectMany(group => group.AllMemberIds)
                        .ToHashSet()),
            Groups: groupReports);
    }

    private static IsolatedLegacyInvoiceSeedCanonicalizationResult
        BuildResult(
            IsolatedLegacyInvoiceSeedCanonicalizationReport report)
    {
        var json = report.ToDeterministicJson();
        var reportSha256 = ComputeSha256(json);
        var output = string.Join(
            Environment.NewLine,
            "legacy_invoice_seed_canonicalization_succeeded=True",
            $"legacy_invoice_seed_canonicalization_report_sha256={reportSha256}",
            $"legacy_invoice_seed_canonicalization_json={json}",
            $"legacy_invoice_seed_scope={ActiveOperationalSeedScope}") +
            Environment.NewLine;
        return new IsolatedLegacyInvoiceSeedCanonicalizationResult(
            report,
            json,
            reportSha256,
            output);
    }

    private static void WriteRecoveryArtifact(
        string guardedRoot,
        string path,
        IsolatedLegacyInvoiceSeedCanonicalizationProfile requiredProfile,
        DatabaseSnapshot after,
        IsolatedLegacyInvoiceSeedCanonicalizationResult result
#if GEORAEPLAN_CANONICALIZER_TESTING
        ,
        IsolatedLegacyInvoiceSeedCanonicalizationFault fault
#endif
        )
    {
        var rootIdentity = AssertRecoveryArtifactLocation(
            guardedRoot,
            path);
        var authenticationKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var protectedAuthenticationKey = ProtectedData.Protect(
                authenticationKey,
                BuildRecoveryArtifactEntropy(
                    rootIdentity,
                    requiredProfile.SourceDatabaseSha256),
                DataProtectionScope.CurrentUser);
            var unsignedArtifact = new RecoveryArtifactContract(
                SchemaVersion: 2,
                SourceDatabaseSha256:
                    requiredProfile.SourceDatabaseSha256,
                RecoveryStateSha256: BuildRecoveryStateHash(after),
                ReportJson: result.DeterministicJson,
                ReportSha256: result.ReportSha256,
                ProtectedAuthenticationKey:
                    Convert.ToBase64String(protectedAuthenticationKey));
            var artifact = new RecoveryArtifact(
                unsignedArtifact.SchemaVersion,
                unsignedArtifact.SourceDatabaseSha256,
                unsignedArtifact.RecoveryStateSha256,
                unsignedArtifact.ReportJson,
                unsignedArtifact.ReportSha256,
                unsignedArtifact.ProtectedAuthenticationKey,
                ComputeRecoveryArtifactHmac(
                    unsignedArtifact,
                    authenticationKey));
            WritePrivateAtomicRecoveryArtifact(
                guardedRoot,
                path,
                rootIdentity,
                JsonSerializer.Serialize(artifact, ReportJsonOptions)
#if GEORAEPLAN_CANONICALIZER_TESTING
                ,
                fault
#endif
                );
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authenticationKey);
        }
    }

    private static IsolatedLegacyInvoiceSeedCanonicalizationResult
        RecoverCommittedResult(
            DatabaseSnapshot current,
            IsolatedLegacyInvoiceSeedCanonicalizationProfile requiredProfile,
            string guardedRoot,
            string path)
    {
        var rootIdentity = AssertRecoveryArtifactLocation(
            guardedRoot,
            path);
        var artifactText = ReadVerifiedRecoveryArtifact(
            path,
            rootIdentity);
        var artifact = JsonSerializer.Deserialize<RecoveryArtifact>(
            artifactText,
            ReportJsonOptions);
        if (artifact is null ||
            artifact.SchemaVersion != 2 ||
            !string.Equals(
                artifact.SourceDatabaseSha256,
                requiredProfile.SourceDatabaseSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                artifact.RecoveryStateSha256,
                BuildRecoveryStateHash(current),
                StringComparison.Ordinal) ||
            !string.Equals(
                artifact.ReportSha256,
                ComputeSha256(artifact.ReportJson),
                StringComparison.Ordinal))
        {
            throw Reject("committed_result_recovery_artifact_mismatch");
        }

        byte[] authenticationKey;
        try
        {
            authenticationKey = ProtectedData.Unprotect(
                Convert.FromBase64String(
                    artifact.ProtectedAuthenticationKey),
                BuildRecoveryArtifactEntropy(
                    rootIdentity,
                    requiredProfile.SourceDatabaseSha256),
                DataProtectionScope.CurrentUser);
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException)
        {
            throw Reject("committed_result_recovery_artifact_mismatch");
        }

        try
        {
            var unsignedArtifact = new RecoveryArtifactContract(
                artifact.SchemaVersion,
                artifact.SourceDatabaseSha256,
                artifact.RecoveryStateSha256,
                artifact.ReportJson,
                artifact.ReportSha256,
                artifact.ProtectedAuthenticationKey);
            var expectedHmac = ComputeRecoveryArtifactHmac(
                unsignedArtifact,
                authenticationKey);
            if (!TryFixedTimeEqualsHex(
                    expectedHmac,
                    artifact.ContractHmacSha256))
            {
                throw Reject(
                    "committed_result_recovery_artifact_mismatch");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authenticationKey);
        }

        var report =
            JsonSerializer.Deserialize<
                IsolatedLegacyInvoiceSeedCanonicalizationReport>(
                artifact.ReportJson,
                ReportJsonOptions) ??
            throw Reject("committed_result_report_invalid");
        ValidateRecoveredReport(report, requiredProfile);
        var result = BuildResult(report);
        if (!string.Equals(
                result.DeterministicJson,
                artifact.ReportJson,
                StringComparison.Ordinal) ||
            !string.Equals(
                result.ReportSha256,
                artifact.ReportSha256,
                StringComparison.Ordinal))
        {
            throw Reject("committed_result_report_not_deterministic");
        }
        return result;
    }

    private static void ValidateRecoveredReport(
        IsolatedLegacyInvoiceSeedCanonicalizationReport report,
        IsolatedLegacyInvoiceSeedCanonicalizationProfile requiredProfile)
    {
        var allowedFields = new[]
        {
            nameof(LocalInvoice.VersionGroupId),
            nameof(LocalInvoice.VersionNumber),
            nameof(LocalInvoice.PreviousVersionId),
            nameof(LocalInvoice.IsLatestVersion),
            nameof(LocalInvoice.ResponsibleOfficeCode)
        };
        var groups = report.Groups;
        var groupContractMatches =
            groups is not null &&
            groups.Count == requiredProfile.ChangedGroupCount &&
            groups.Select(group => group.GroupOrdinal)
                .SequenceEqual(
                    Enumerable.Range(
                        1,
                        requiredProfile.ChangedGroupCount)) &&
            groups.All(group =>
                IsSha256(group.GroupFingerprintSha256) &&
                IsSha256(group.BeforeMetadataSha256) &&
                IsSha256(group.AfterMetadataSha256) &&
                group.ActiveInvoiceCount > 0 &&
                group.ExcludedDeletedInvoiceCount >= 0 &&
                group.ChangedMetadataFields.Count > 0 &&
                group.ChangedMetadataFields
                    .Distinct(StringComparer.Ordinal)
                    .SequenceEqual(group.ChangedMetadataFields) &&
                group.ChangedMetadataFields.All(field =>
                    allowedFields.Contains(
                        field,
                        StringComparer.Ordinal)) &&
                group.ChangedMetadataFields.SequenceEqual(
                    group.ChangedMetadataFields
                        .OrderBy(
                            field => field,
                            StringComparer.Ordinal))) &&
            groups.Count(group => string.Equals(
                group.Mode,
                DeletedPredecessorRerootMode,
                StringComparison.Ordinal)) ==
                requiredProfile.DeletedPredecessorRerootGroupCount &&
            groups.Count(group => string.Equals(
                group.Mode,
                DuplicateSiblingMode,
                StringComparison.Ordinal)) ==
                requiredProfile.DuplicateSiblingGroupCount &&
            groups.Count(group => string.Equals(
                group.Mode,
                ResponsibleOfficeAlignmentMode,
                StringComparison.Ordinal)) ==
                requiredProfile.ResponsibleOfficeAlignmentGroupCount &&
            groups.Sum(group => group.ExcludedDeletedInvoiceCount) ==
                requiredProfile.ExcludedDeletedInvoiceCount;
        if (report.SchemaVersion != 2 ||
            !report.Succeeded ||
            !string.Equals(
                report.SeedScope,
                ActiveOperationalSeedScope,
                StringComparison.Ordinal) ||
            report.ChangedGroupCount != requiredProfile.ChangedGroupCount ||
            report.ChangedInvoiceCount !=
            requiredProfile.ChangedInvoiceCount ||
            report.ExcludedDeletedInvoiceCount !=
            requiredProfile.ExcludedDeletedInvoiceCount ||
            !string.Equals(
                report.SourceDatabaseSha256,
                requiredProfile.SourceDatabaseSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.BeforeMetadataSha256,
                requiredProfile.BeforeMetadataSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.AfterMetadataSha256,
                requiredProfile.AfterMetadataSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.ActiveInvoiceIdsSha256,
                requiredProfile.ActiveInvoiceIdsSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.LatestInvoiceBusinessSha256,
                requiredProfile.LatestInvoiceBusinessSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.DependencyReferencesSha256,
                requiredProfile.DependencyReferencesSha256,
                StringComparison.Ordinal) ||
            !groupContractMatches ||
            ContainsGuid(report.ToDeterministicJson()))
        {
            throw Reject("committed_result_report_profile_mismatch");
        }
    }

    private static bool ContainsGuid(string value)
    {
        for (var index = 0; index + 36 <= value.Length; index++)
        {
            if (Guid.TryParseExact(
                    value.AsSpan(index, 36),
                    "D",
                    out _))
            {
                return true;
            }
        }
        return false;
    }

    private static string BuildRecoveryStateHash(DatabaseSnapshot snapshot)
    {
        var invoiceIds = snapshot.Invoices
            .Select(invoice => invoice.Id)
            .ToHashSet();
        var immutableInvoices = ComputeSha256(string.Join(
            "\n",
            snapshot.Invoices
                .OrderBy(invoice => FormatId(invoice.Id))
                .Select(invoice =>
                    $"{FormatId(invoice.Id)}|{BuildImmutableInvoiceHash(invoice)}")));
        var customers = ComputeSha256(JsonSerializer.Serialize(
            snapshot.Customers
                .OrderBy(customer => FormatId(customer.Id))
                .ToList(),
            ReportJsonOptions));
        return ComputeDomainSeparatedSha256(
            "canonicalization-recovery-state",
            BuildMetadataHash(snapshot.Invoices),
            immutableInvoices,
            BuildDependencyReferencesHash(snapshot, invoiceIds),
            BuildProtectedDependencyStateHash(snapshot, invoiceIds),
            customers);
    }

    private static string AssertRecoveryArtifactLocation(
        string guardedRoot,
        string path)
    {
        if (string.IsNullOrWhiteSpace(guardedRoot) ||
            string.IsNullOrWhiteSpace(path))
        {
            throw Reject("recovery_artifact_path_missing");
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(guardedRoot));
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(
                Path.GetDirectoryName(fullPath),
                fullRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(fullPath),
                RecoveryResultFileName,
                StringComparison.Ordinal))
        {
            throw Reject("recovery_artifact_path_unsafe");
        }

        return CaptureDirectoryIdentity(fullRoot);
    }

    private static byte[] BuildRecoveryArtifactEntropy(
        string rootIdentity,
        string sourceDatabaseSha256)
        => Encoding.UTF8.GetBytes(string.Join(
            "\n",
            "georaeplan:canonicalization-recovery-artifact:v2",
            rootIdentity,
            sourceDatabaseSha256));

    private static string ComputeRecoveryArtifactHmac(
        RecoveryArtifactContract artifact,
        byte[] authenticationKey)
        => Convert.ToHexString(HMACSHA256.HashData(
            authenticationKey,
            Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    artifact,
                    ReportJsonOptions))));

    private static bool TryFixedTimeEqualsHex(
        string expected,
        string actual)
    {
        if (!IsSha256(expected) || !IsSha256(actual))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expected),
            Convert.FromHexString(actual));
    }

    private static void WritePrivateAtomicRecoveryArtifact(
        string guardedRoot,
        string path,
        string expectedRootIdentity,
        string text
#if GEORAEPLAN_CANONICALIZER_TESTING
        ,
        IsolatedLegacyInvoiceSeedCanonicalizationFault fault
#endif
        )
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(guardedRoot));
        var fullPath = Path.GetFullPath(path);
        var temporaryPath = Path.Combine(
            fullRoot,
            $".{RecoveryResultFileName}.{Guid.NewGuid():N}.tmp");
        var expectedBytes = new UTF8Encoding(false).GetBytes(text);
        var published = false;
        try
        {
            using var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            ApplyPrivateFileAcl(temporaryPath);
            AssertRegularSingleLinkFile(
                stream.SafeFileHandle,
                temporaryPath);
            var bytesToWrite = expectedBytes.Length;
#if GEORAEPLAN_CANONICALIZER_TESTING
            if (fault ==
                IsolatedLegacyInvoiceSeedCanonicalizationFault
                    .ThrowDuringRecoveryArtifactWrite)
            {
                bytesToWrite = Math.Max(1, expectedBytes.Length / 2);
            }
#endif
            stream.Write(expectedBytes, 0, bytesToWrite);
            stream.Flush(flushToDisk: true);
            AssertRegularSingleLinkFile(
                stream.SafeFileHandle,
                temporaryPath);
            if (!string.Equals(
                    ComputeStreamSha256(stream),
                    Convert.ToHexString(
                        SHA256.HashData(expectedBytes)),
                    StringComparison.Ordinal))
            {
#if GEORAEPLAN_CANONICALIZER_TESTING
                if (fault ==
                    IsolatedLegacyInvoiceSeedCanonicalizationFault
                        .ThrowDuringRecoveryArtifactWrite)
                {
                    throw new InjectedCanonicalizationFaultException();
                }
#endif
                throw Reject("recovery_artifact_write_mismatch");
            }
            AssertPrivateFileAcl(temporaryPath);
            if (!string.Equals(
                    CaptureDirectoryIdentity(fullRoot),
                    expectedRootIdentity,
                    StringComparison.Ordinal))
            {
                throw Reject("recovery_artifact_root_changed");
            }
#if GEORAEPLAN_CANONICALIZER_TESTING
            if (fault ==
                IsolatedLegacyInvoiceSeedCanonicalizationFault
                    .CreateRecoveryArtifactReparseBeforePublish)
            {
                var raceTarget = $"{fullPath}.race-target";
                Directory.CreateDirectory(raceTarget);
                File.WriteAllText(
                    Path.Combine(raceTarget, "sentinel.txt"),
                    "unchanged",
                    Encoding.UTF8);
                using var junctionProcess =
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(
                            "cmd.exe",
                            $"/d /c mklink /J \"{fullPath}\" \"{raceTarget}\"")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }) ??
                    throw new InvalidOperationException(
                        "Unable to start the injected reparse race.");
                junctionProcess.WaitForExit();
                if (junctionProcess.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "Unable to create the injected reparse race.");
                }
            }
#endif
            AssertPublishTargetSafe(fullPath);
            File.Move(temporaryPath, fullPath, overwrite: true);
            published = true;
            AssertRegularSingleLinkFile(
                stream.SafeFileHandle,
                fullPath);
            if (!string.Equals(
                    CaptureDirectoryIdentity(fullRoot),
                    expectedRootIdentity,
                    StringComparison.Ordinal))
            {
                throw Reject("recovery_artifact_root_changed");
            }
        }
        finally
        {
            if (!published)
                DeleteOwnedTemporaryArtifact(temporaryPath);
        }

        var finalText = ReadVerifiedRecoveryArtifact(
            fullPath,
            expectedRootIdentity);
        if (!string.Equals(finalText, text, StringComparison.Ordinal))
            throw Reject("recovery_artifact_publish_mismatch");
    }

    private static string ReadVerifiedRecoveryArtifact(
        string path,
        string expectedRootIdentity)
    {
        AssertPublishTargetSafe(path, requireExists: true);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        AssertRegularSingleLinkFile(stream.SafeFileHandle, path);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        var text = reader.ReadToEnd();
        stream.Position = 0;
        AssertRegularSingleLinkFile(stream.SafeFileHandle, path);
        var root = Path.GetDirectoryName(Path.GetFullPath(path)) ??
            throw Reject("recovery_artifact_path_unsafe");
        if (!string.Equals(
                CaptureDirectoryIdentity(root),
                expectedRootIdentity,
                StringComparison.Ordinal))
        {
            throw Reject("recovery_artifact_root_changed");
        }
        return text;
    }

    private static void ApplyPrivateFileAcl(string path)
    {
        var currentUser = WindowsIdentity.GetCurrent().User ??
            throw Reject("recovery_artifact_owner_missing");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        AssertPrivateFileAcl(path);
    }

    private static void AssertPrivateFileAcl(string path)
    {
        var currentUser = WindowsIdentity.GetCurrent().User ??
            throw Reject("recovery_artifact_owner_missing");
        var security = new FileInfo(path).GetAccessControl(
            AccessControlSections.Access |
            AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        if (!security.AreAccessRulesProtected ||
            owner is null ||
            !string.Equals(
                owner.Value,
                currentUser.Value,
                StringComparison.Ordinal) ||
            rules.Count != 1 ||
            rules[0].IsInherited ||
            rules[0].AccessControlType != AccessControlType.Allow ||
            rules[0].FileSystemRights != FileSystemRights.FullControl ||
            !string.Equals(
                rules[0].IdentityReference.Value,
                currentUser.Value,
                StringComparison.Ordinal))
        {
            throw Reject("recovery_artifact_acl_unsafe");
        }
    }

    private static void AssertPublishTargetSafe(
        string path,
        bool requireExists = false)
    {
        using var handle = CreateFileW(
            path,
            GenericRead,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (!requireExists &&
                error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return;
            }
            throw Reject(
                requireExists
                    ? "committed_result_recovery_artifact_missing"
                    : "recovery_artifact_path_unsafe");
        }
        if (!GetFileInformationByHandle(handle, out var information) ||
            (information.FileAttributes &
             (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
        {
            throw Reject("recovery_artifact_path_unsafe");
        }
    }

    private static void AssertRegularSingleLinkFile(
        SafeFileHandle handle,
        string expectedPath)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((information.FileAttributes &
             (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
            information.NumberOfLinks != 1 ||
            !string.Equals(
                GetFinalPath(handle),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw Reject("recovery_artifact_identity_mismatch");
        }
    }

    private static string CaptureDirectoryIdentity(string path)
    {
        using var handle = CreateFileW(
            path,
            GenericRead,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid ||
            !GetFileInformationByHandle(handle, out var information))
        {
            throw Reject("recovery_artifact_root_unsafe");
        }
        if ((information.FileAttributes & FileAttributeDirectory) == 0 ||
            (information.FileAttributes & FileAttributeReparsePoint) != 0 ||
            !string.Equals(
                GetFinalPath(handle),
                Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase))
        {
            throw Reject("recovery_artifact_root_unsafe");
        }
        return string.Join(
            ":",
            information.VolumeSerialNumber.ToString("X8"),
            information.FileIndexHigh.ToString("X8"),
            information.FileIndexLow.ToString("X8"));
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(
            handle,
            buffer,
            (uint)buffer.Capacity,
            0);
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var value = buffer.ToString();
        return value.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? value[4..]
            : value;
    }

    private static string ComputeStreamSha256(FileStream stream)
    {
        var position = stream.Position;
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = position;
        return hash;
    }

    private static void DeleteOwnedTemporaryArtifact(string path)
    {
        try
        {
            if (File.Exists(path) &&
                (File.GetAttributes(path) &
                 FileAttributes.ReparsePoint) == 0)
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private static string BuildGroupFingerprint(GroupPlan group)
        => ComputeDomainSeparatedSha256(
            "canonicalization-report-group",
            $"group={FormatId(group.OriginalGroupId)}",
            $"active={string.Join(",", group.ActiveInvoiceIds.OrderBy(FormatId).Select(FormatId))}",
            $"latest={FormatId(group.LatestInvoiceId)}",
            $"deleted={string.Join(",", group.ExcludedDeletedInvoiceIds.OrderBy(FormatId).Select(FormatId))}");

    private static GroupPlan NewGroupPlan(
        Guid groupId,
        string mode,
        IReadOnlyList<LocalInvoice> members,
        IReadOnlyList<LocalInvoice> activeOrder,
        LocalInvoice latest,
        IReadOnlyList<InvoiceMetadataUpdate> updates)
        => new(
            groupId,
            mode,
            members.Select(invoice => invoice.Id).ToHashSet(),
            activeOrder.Select(invoice => invoice.Id).ToHashSet(),
            members.Where(invoice => invoice.IsDeleted)
                .Select(invoice => invoice.Id)
                .ToHashSet(),
            latest.Id,
            updates);

    private static IReadOnlyList<string> ChangedFields(
        LocalInvoice invoice,
        Guid versionGroupId,
        int versionNumber,
        Guid? previousVersionId,
        bool isLatestVersion,
        string responsibleOfficeCode)
    {
        var fields = new List<string>();
        if (invoice.VersionGroupId != versionGroupId)
            fields.Add(nameof(LocalInvoice.VersionGroupId));
        if (invoice.VersionNumber != versionNumber)
            fields.Add(nameof(LocalInvoice.VersionNumber));
        if (NormalizeReference(invoice.PreviousVersionId) !=
            NormalizeReference(previousVersionId))
        {
            fields.Add(nameof(LocalInvoice.PreviousVersionId));
        }
        if (invoice.IsLatestVersion != isLatestVersion)
            fields.Add(nameof(LocalInvoice.IsLatestVersion));
        if (!string.Equals(
                invoice.ResponsibleOfficeCode,
                responsibleOfficeCode,
                StringComparison.Ordinal))
        {
            fields.Add(nameof(LocalInvoice.ResponsibleOfficeCode));
        }

        return fields;
    }

    private static bool HasCycle(IReadOnlyList<LocalInvoice> members)
    {
        var memberIds = members.Select(invoice => invoice.Id).ToHashSet();
        var predecessor = members.ToDictionary(
            invoice => invoice.Id,
            invoice =>
            {
                var value =
                    NormalizeReference(invoice.PreviousVersionId);
                return value.HasValue &&
                       memberIds.Contains(value.Value)
                    ? value
                    : null;
            });
        foreach (var invoice in members)
        {
            var seen = new HashSet<Guid>();
            var current = invoice.Id;
            while (true)
            {
                if (!seen.Add(current))
                    return true;
                if (!predecessor.TryGetValue(
                        current,
                        out var previous) ||
                    !previous.HasValue)
                {
                    break;
                }

                current = previous.Value;
            }
        }

        return false;
    }

    private static string BuildMetadataHash(
        IEnumerable<LocalInvoice> invoices)
        => ComputeSha256(string.Join(
            "\n",
            invoices
                .OrderBy(invoice => FormatId(invoice.Id))
                .Select(invoice => BuildMetadataLine(invoice, null))));

    private static string BuildProjectedMetadataHash(
        DatabaseSnapshot before,
        CanonicalizationPlan plan,
        IReadOnlySet<Guid> memberIds)
    {
        var updateById = plan.Updates.ToDictionary(
            update => update.InvoiceId);
        return ComputeSha256(string.Join(
            "\n",
            before.Invoices
                .Where(invoice => memberIds.Contains(invoice.Id))
                .OrderBy(invoice => FormatId(invoice.Id))
                .Select(invoice => BuildMetadataLine(
                    invoice,
                    updateById.GetValueOrDefault(invoice.Id)))));
    }

    private static string BuildMetadataLine(
        LocalInvoice invoice,
        InvoiceMetadataUpdate? update)
        => string.Join(
            "|",
            FormatId(invoice.Id),
            FormatId(update?.VersionGroupId ?? invoice.VersionGroupId),
            update?.VersionNumber ?? invoice.VersionNumber,
            FormatNullableId(
                update is null
                    ? invoice.PreviousVersionId
                    : update.PreviousVersionId),
            (update?.IsLatestVersion ?? invoice.IsLatestVersion) ? "1" : "0",
            invoice.IsDeleted ? "1" : "0",
            FormatId(invoice.CustomerId),
            NormalizeScope(invoice.TenantCode),
            NormalizeScope(invoice.OfficeCode),
            NormalizeScope(
                update?.ResponsibleOfficeCode ??
                invoice.ResponsibleOfficeCode),
            invoice.Revision,
            invoice.IsDirty ? "1" : "0");

    private static string BuildLatestBusinessHash(
        DatabaseSnapshot snapshot,
        IReadOnlyList<GroupPlan> groups)
    {
        var invoiceById = snapshot.Invoices.ToDictionary(
            invoice => invoice.Id);
        return ComputeSha256(string.Join(
            "\n",
            groups
                .OrderBy(group => FormatId(group.OriginalGroupId))
                .Select(group =>
                    $"{FormatId(group.LatestInvoiceId)}:{BuildBusinessInvoiceHash(invoiceById[group.LatestInvoiceId], snapshot.InvoiceLines)}")));
    }

    private static string BuildImmutableInvoiceHash(LocalInvoice invoice)
        => ComputeSha256(JsonSerializer.Serialize(new
        {
            invoice.Id,
            invoice.CustomerId,
            invoice.TenantCode,
            invoice.OfficeCode,
            invoice.InvoiceNumber,
            invoice.LocalTempNumber,
            invoice.TaxInvoiceNumber,
            invoice.VoucherType,
            invoice.InvoiceDate,
            invoice.TotalAmount,
            invoice.SupplyAmount,
            invoice.VatAmount,
            invoice.VatMode,
            invoice.TaxInvoiceIssued,
            invoice.PurchaseReceivingRequired,
            invoice.PurchaseReceivingStatus,
            invoice.PurchaseReceivedAtUtc,
            invoice.PurchaseReceivedByUsername,
            invoice.PurchaseReceivingOfficeCode,
            invoice.PurchaseReceivingWarehouseCode,
            invoice.PurchaseReceivingMemo,
            invoice.Memo,
            invoice.SourceWarehouseCode,
            invoice.DeliveryGroupId,
            invoice.ParentInvoiceId,
            invoice.LinkedRentalBillingProfileId,
            invoice.LinkedRentalBillingRunId,
            invoice.IsConfirmed,
            invoice.CreatedByUsername,
            invoice.LastSavedByUsername,
            invoice.LastSavedAtUtc,
            invoice.ConcurrencyStamp,
            invoice.CostStatus,
            invoice.IsDeleted,
            invoice.CreatedAtUtc,
            invoice.UpdatedAtUtc,
            invoice.Revision,
            invoice.IsDirty
        }, ReportJsonOptions));

    private static string BuildBusinessInvoiceHash(
        LocalInvoice invoice,
        IReadOnlyList<LocalInvoiceLine> invoiceLines)
        => ComputeSha256(JsonSerializer.Serialize(new
        {
            Invoice = new
            {
                invoice.Id,
                invoice.CustomerId,
                invoice.TenantCode,
                invoice.OfficeCode,
                invoice.ResponsibleOfficeCode,
                invoice.InvoiceNumber,
                invoice.LocalTempNumber,
                invoice.TaxInvoiceNumber,
                invoice.VoucherType,
                invoice.InvoiceDate,
                invoice.TotalAmount,
                invoice.SupplyAmount,
                invoice.VatAmount,
                invoice.VatMode,
                invoice.TaxInvoiceIssued,
                invoice.PurchaseReceivingRequired,
                invoice.PurchaseReceivingStatus,
                invoice.PurchaseReceivedAtUtc,
                invoice.PurchaseReceivedByUsername,
                invoice.PurchaseReceivingOfficeCode,
                invoice.PurchaseReceivingWarehouseCode,
                invoice.PurchaseReceivingMemo,
                invoice.Memo,
                invoice.SourceWarehouseCode,
                invoice.DeliveryGroupId,
                invoice.ParentInvoiceId,
                invoice.LinkedRentalBillingProfileId,
                invoice.LinkedRentalBillingRunId,
                invoice.IsConfirmed,
                invoice.CreatedByUsername,
                invoice.LastSavedByUsername,
                invoice.LastSavedAtUtc,
                invoice.ConcurrencyStamp,
                invoice.CostStatus
            },
            Lines = invoiceLines
                .Where(line => line.InvoiceId == invoice.Id)
                .OrderBy(line => line.OrderIndex)
                .ThenBy(line => FormatId(line.Id))
                .Select(line => new
                {
                    line.Id,
                    line.InvoiceId,
                    line.ItemId,
                    line.ItemNameOriginal,
                    line.SpecificationOriginal,
                    line.Unit,
                    line.Quantity,
                    line.UnitPrice,
                    line.LineAmount,
                    line.Remark,
                    line.SerialNumber,
                    line.MaterialNumber,
                    line.InstallLocation,
                    line.RentalStartDate,
                    line.RentalEndDate,
                    line.OrderIndex,
                    line.ItemTrackingType,
                    line.IsDeleted
                })
                .ToList()
        }, ReportJsonOptions));

    private static string BuildDependencyReferencesHash(
        DatabaseSnapshot snapshot,
        IReadOnlySet<Guid> invoiceIds)
    {
        var references = new List<string>();
        references.AddRange(snapshot.InvoiceLines
            .Where(line => invoiceIds.Contains(line.InvoiceId))
            .Select(line =>
                $"InvoiceLine|{FormatId(line.Id)}|{FormatId(line.InvoiceId)}"));
        references.AddRange(snapshot.Payments
            .Where(payment => invoiceIds.Contains(payment.InvoiceId))
            .Select(payment =>
                $"Payment|{FormatId(payment.Id)}|{FormatId(payment.InvoiceId)}"));
        references.AddRange(snapshot.Transactions
            .Where(transaction =>
                transaction.LinkedInvoiceId.HasValue &&
                invoiceIds.Contains(transaction.LinkedInvoiceId.Value))
            .Select(transaction => string.Join(
                "|",
                "Transaction",
                FormatId(transaction.Id),
                FormatNullableId(transaction.LinkedInvoiceId),
                FormatNullableId(
                    transaction.LinkedRentalBillingProfileId),
                FormatNullableId(
                    transaction.LinkedRentalBillingRunId))));
        references.AddRange(snapshot.InvoiceLineSerials
            .Where(serial => invoiceIds.Contains(serial.InvoiceId))
            .Select(serial => string.Join(
                "|",
                "InvoiceLineSerial",
                FormatId(serial.Id),
                FormatId(serial.InvoiceId),
                FormatId(serial.InvoiceLineId))));
        references.AddRange(snapshot.InventoryMovements
            .Where(movement =>
                movement.InvoiceId.HasValue &&
                invoiceIds.Contains(movement.InvoiceId.Value))
            .Select(movement => string.Join(
                "|",
                "InventoryMovement",
                FormatId(movement.Id),
                FormatNullableId(movement.InvoiceId),
                FormatNullableId(movement.InvoiceLineId))));
        references.AddRange(snapshot.CostAllocations
            .Where(allocation =>
                invoiceIds.Contains(allocation.SalesInvoiceId) ||
                (allocation.PurchaseInvoiceId.HasValue &&
                 invoiceIds.Contains(
                     allocation.PurchaseInvoiceId.Value)))
            .Select(allocation => string.Join(
                "|",
                "CostAllocation",
                FormatId(allocation.Id),
                FormatId(allocation.SalesInvoiceId),
                FormatId(allocation.SalesInvoiceLineId),
                FormatNullableId(allocation.PurchaseInvoiceId),
                FormatNullableId(
                    allocation.PurchaseInvoiceLineId))));
        references.AddRange(snapshot.StockLayers
            .Where(layer =>
                layer.SourceInvoiceId.HasValue &&
                invoiceIds.Contains(layer.SourceInvoiceId.Value))
            .Select(layer => string.Join(
                "|",
                "StockLayer",
                FormatId(layer.Id),
                FormatNullableId(layer.SourceInvoiceId),
                FormatNullableId(layer.SourceInvoiceLineId))));
        references.AddRange(snapshot.SerialLedgers
            .Where(ledger =>
                (ledger.SourcePurchaseInvoiceId.HasValue &&
                 invoiceIds.Contains(
                     ledger.SourcePurchaseInvoiceId.Value)) ||
                (ledger.SourceSalesInvoiceId.HasValue &&
                 invoiceIds.Contains(
                     ledger.SourceSalesInvoiceId.Value)) ||
                (ledger.LastInvoiceId.HasValue &&
                 invoiceIds.Contains(ledger.LastInvoiceId.Value)))
            .Select(ledger => string.Join(
                "|",
                "SerialLedger",
                FormatId(ledger.Id),
                FormatNullableId(ledger.SourcePurchaseInvoiceId),
                FormatNullableId(ledger.SourceSalesInvoiceId),
                FormatNullableId(ledger.LastInvoiceId))));
        references.AddRange(snapshot.Invoices
            .Where(invoice =>
                invoiceIds.Contains(invoice.Id) &&
                (NormalizeReference(
                     invoice.LinkedRentalBillingProfileId).HasValue ||
                 NormalizeReference(
                     invoice.LinkedRentalBillingRunId).HasValue))
            .Select(invoice => string.Join(
                "|",
                "InvoiceRental",
                FormatId(invoice.Id),
                FormatNullableId(
                    invoice.LinkedRentalBillingProfileId),
                FormatNullableId(invoice.LinkedRentalBillingRunId))));
        return ComputeSha256(string.Join(
            "\n",
            references.OrderBy(
                value => value,
                StringComparer.Ordinal)));
    }

    private static string BuildProtectedDependencyStateHash(
        DatabaseSnapshot snapshot,
        IReadOnlySet<Guid> invoiceIds)
    {
        var values = new List<string>();
        void Add<T>(string entityName, Guid id, T value)
            => values.Add(
                $"{entityName}|{FormatId(id)}|{ComputeSha256(JsonSerializer.Serialize(value, ReportJsonOptions))}");

        foreach (var line in snapshot.InvoiceLines.Where(line =>
                     invoiceIds.Contains(line.InvoiceId)))
        {
            Add("InvoiceLine", line.Id, line);
        }
        foreach (var payment in snapshot.Payments.Where(payment =>
                     invoiceIds.Contains(payment.InvoiceId)))
        {
            Add("Payment", payment.Id, payment);
        }
        foreach (var transaction in snapshot.Transactions.Where(transaction =>
                     transaction.LinkedInvoiceId.HasValue &&
                     invoiceIds.Contains(transaction.LinkedInvoiceId.Value)))
        {
            Add("Transaction", transaction.Id, transaction);
        }
        foreach (var serial in snapshot.InvoiceLineSerials.Where(serial =>
                     invoiceIds.Contains(serial.InvoiceId)))
        {
            Add("InvoiceLineSerial", serial.Id, serial);
        }
        foreach (var movement in snapshot.InventoryMovements.Where(movement =>
                     movement.InvoiceId.HasValue &&
                     invoiceIds.Contains(movement.InvoiceId.Value)))
        {
            Add("InventoryMovement", movement.Id, movement);
        }
        foreach (var allocation in snapshot.CostAllocations.Where(allocation =>
                     invoiceIds.Contains(allocation.SalesInvoiceId) ||
                     (allocation.PurchaseInvoiceId.HasValue &&
                      invoiceIds.Contains(
                          allocation.PurchaseInvoiceId.Value))))
        {
            Add("CostAllocation", allocation.Id, allocation);
        }
        foreach (var layer in snapshot.StockLayers.Where(layer =>
                     layer.SourceInvoiceId.HasValue &&
                     invoiceIds.Contains(layer.SourceInvoiceId.Value)))
        {
            Add("StockLayer", layer.Id, layer);
        }
        foreach (var ledger in snapshot.SerialLedgers.Where(ledger =>
                     (ledger.SourcePurchaseInvoiceId.HasValue &&
                      invoiceIds.Contains(
                          ledger.SourcePurchaseInvoiceId.Value)) ||
                     (ledger.SourceSalesInvoiceId.HasValue &&
                      invoiceIds.Contains(
                          ledger.SourceSalesInvoiceId.Value)) ||
                     (ledger.LastInvoiceId.HasValue &&
                      invoiceIds.Contains(ledger.LastInvoiceId.Value))))
        {
            Add("SerialLedger", ledger.Id, ledger);
        }

        var referencedRentalProfileIds = snapshot.Invoices
            .Where(invoice => invoiceIds.Contains(invoice.Id))
            .Select(invoice =>
                NormalizeReference(
                    invoice.LinkedRentalBillingProfileId))
            .Concat(snapshot.Transactions
                .Where(transaction =>
                    transaction.LinkedInvoiceId.HasValue &&
                    invoiceIds.Contains(
                        transaction.LinkedInvoiceId.Value))
                .Select(transaction =>
                    NormalizeReference(
                        transaction.LinkedRentalBillingProfileId)))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToHashSet();
        foreach (var profile in snapshot.RentalBillingProfiles.Where(profile =>
                     referencedRentalProfileIds.Contains(profile.Id)))
        {
            Add("RentalBillingProfile", profile.Id, profile);
        }

        return ComputeSha256(string.Join(
            "\n",
            values.OrderBy(
                value => value,
                StringComparer.Ordinal)));
    }

    private static async Task<
            IsolatedLegacyInvoiceSeedCanonicalizationProfilePreview>
        BuildProfilePreviewAsync(
            LocalDbContext db,
            string sourceDatabaseSha256,
            CancellationToken cancellationToken)
    {
        if (!IsSha256(sourceDatabaseSha256))
        {
            throw new ArgumentException(
                "A valid source database SHA-256 is required.",
                nameof(sourceDatabaseSha256));
        }

        var outboxEvidence =
            await BuildNonAcknowledgedOutboxEvidenceAsync(
                db,
                cancellationToken);
        var before = await LoadSnapshotAsync(db, cancellationToken);
        var plan = BuildPlan(before);
        var changedInvoiceIds = plan.Updates
            .Where(update => update.ChangedMetadataFields.Count > 0)
            .Select(update => update.InvoiceId)
            .Distinct()
            .ToHashSet();
        var activeInvoiceIds = plan.Groups
            .SelectMany(group => group.ActiveInvoiceIds)
            .Distinct()
            .ToHashSet();
        if (!changedInvoiceIds.IsSubsetOf(activeInvoiceIds))
            throw Reject("required_active_changed_invoice_scope_mismatch");
        var allMemberIds = plan.Groups
            .SelectMany(group => group.AllMemberIds)
            .Distinct()
            .ToHashSet();
        var beforeById = before.Invoices.ToDictionary(
            invoice => invoice.Id);

        return new IsolatedLegacyInvoiceSeedCanonicalizationProfilePreview(
            SchemaVersion: 1,
            SourceDatabaseSha256:
                sourceDatabaseSha256.ToUpperInvariant(),
            SeedScope: ActiveOperationalSeedScope,
            AuthorizedNonAcknowledgedOutboxCount:
                outboxEvidence.Count,
            AuthorizedNonAcknowledgedOutboxSha256:
                outboxEvidence.Sha256,
            ChangedGroupCount: plan.Groups.Count,
            ChangedInvoiceCount: changedInvoiceIds.Count,
            ExcludedDeletedInvoiceCount:
                plan.Groups.Sum(group =>
                    group.ExcludedDeletedInvoiceIds.Count),
            DeletedPredecessorRerootGroupCount:
                plan.Groups.Count(group => string.Equals(
                    group.Mode,
                    DeletedPredecessorRerootMode,
                    StringComparison.Ordinal)),
            DuplicateSiblingGroupCount:
                plan.Groups.Count(group => string.Equals(
                    group.Mode,
                    DuplicateSiblingMode,
                    StringComparison.Ordinal)),
            ResponsibleOfficeAlignmentGroupCount:
                plan.Groups.Count(group => string.Equals(
                    group.Mode,
                    ResponsibleOfficeAlignmentMode,
                    StringComparison.Ordinal)),
            BeforeMetadataSha256:
                BuildMetadataHash(
                    allMemberIds.Select(id => beforeById[id])),
            ProjectedAfterMetadataSha256:
                BuildProjectedMetadataHash(
                    before,
                    plan,
                    allMemberIds),
            ActiveInvoiceIdsSha256:
                ComputeSha256(string.Join(
                    "\n",
                    activeInvoiceIds
                        .OrderBy(FormatId)
                        .Select(FormatId))),
            LatestInvoiceBusinessSha256:
                BuildLatestBusinessHash(before, plan.Groups),
            DependencyReferencesSha256:
                BuildDependencyReferencesHash(
                    before,
                    allMemberIds));
    }

    private static async Task<DatabaseSnapshot> LoadSnapshotAsync(
        LocalDbContext db,
        CancellationToken cancellationToken)
        => new(
            await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .ToListAsync(cancellationToken),
            await db.Customers.IgnoreQueryFilters().AsNoTracking()
                .ToListAsync(cancellationToken),
            await db.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                .ToListAsync(cancellationToken),
            await db.Payments.IgnoreQueryFilters().AsNoTracking()
                .ToListAsync(cancellationToken),
            await db.Transactions.IgnoreQueryFilters().AsNoTracking()
                .ToListAsync(cancellationToken),
            await db.InvoiceLineSerials.AsNoTracking()
                .ToListAsync(cancellationToken),
            await db.InventoryMovements.AsNoTracking()
                .ToListAsync(cancellationToken),
            await db.CostAllocations.AsNoTracking()
                .ToListAsync(cancellationToken),
            await db.StockLayers.AsNoTracking()
                .ToListAsync(cancellationToken),
            await db.SerialLedgers.AsNoTracking()
                .ToListAsync(cancellationToken),
            await db.RentalBillingProfiles.IgnoreQueryFilters()
                .AsNoTracking().ToListAsync(cancellationToken));

    private static async Task<NonAcknowledgedOutboxEvidence>
        BuildNonAcknowledgedOutboxEvidenceAsync(
            LocalDbContext db,
            CancellationToken cancellationToken)
    {
        var entries = await db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry => entry.Status != "Acknowledged")
            .OrderBy(entry => entry.Id)
            .ToListAsync(cancellationToken);
        var descriptors = entries
            .Select(entry => new NonAcknowledgedOutboxDescriptor(
                Id: FormatId(entry.Id),
                MutationId: entry.MutationId ?? string.Empty,
                DeviceId: entry.DeviceId ?? string.Empty,
                EntityName: entry.EntityName ?? string.Empty,
                EntityId: FormatId(entry.EntityId),
                ExpectedRevision: entry.ExpectedRevision,
                TenantCode: entry.TenantCode ?? string.Empty,
                OfficeCode: entry.OfficeCode ?? string.Empty,
                ResponsibleOfficeCode:
                    entry.ResponsibleOfficeCode ?? string.Empty,
                BusinessDatabaseName:
                    entry.BusinessDatabaseName ?? string.Empty,
                SessionId: FormatId(entry.SessionId),
                UserId: FormatId(entry.UserId),
                Status: entry.Status ?? string.Empty,
                ErrorMessageSha256: ComputeDomainSeparatedSha256(
                    "authorized-outbox-error-message",
                    entry.ErrorMessage ?? string.Empty),
                PreparedAtUtcTicks:
                    NormalizeUtcTicks(entry.PreparedAtUtc),
                SentAtUtcTicks:
                    NormalizeNullableUtcTicks(entry.SentAtUtc),
                AcknowledgedAtUtcTicks:
                    NormalizeNullableUtcTicks(entry.AcknowledgedAtUtc),
                AcceptedRevision: entry.AcceptedRevision,
                AcceptedUpdatedAtUtcTicks:
                    NormalizeNullableUtcTicks(
                        entry.AcceptedUpdatedAtUtc)))
            .ToList();
        var json = JsonSerializer.Serialize(
            descriptors,
            ReportJsonOptions);
        return new NonAcknowledgedOutboxEvidence(
            descriptors.Count,
            ComputeDomainSeparatedSha256(
                "authorized-non-acknowledged-outbox",
                json));
    }

    private static void AssertAuthorizedNonAcknowledgedOutbox(
        IsolatedLegacyInvoiceSeedCanonicalizationProfile requiredProfile,
        NonAcknowledgedOutboxEvidence actual)
    {
        if (
            requiredProfile.AuthorizedNonAcknowledgedOutboxCount < 0 ||
            !IsSha256(
                requiredProfile.AuthorizedNonAcknowledgedOutboxSha256))
        {
            throw Reject("approved_partial_push_outbox_profile_invalid");
        }

        if (
            requiredProfile.AuthorizedNonAcknowledgedOutboxCount == 0 &&
            actual.Count > 0)
        {
            throw Reject(
                "partial_push_outbox_present",
                evidenceSha256: actual.Sha256);
        }

        if (
            actual.Count !=
                requiredProfile.AuthorizedNonAcknowledgedOutboxCount ||
            !string.Equals(
                actual.Sha256,
                requiredProfile.AuthorizedNonAcknowledgedOutboxSha256,
                StringComparison.Ordinal))
        {
            throw Reject(
                "approved_partial_push_outbox_mismatch",
                evidenceSha256: actual.Sha256);
        }
    }

    private static long NormalizeUtcTicks(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value.Ticks,
            DateTimeKind.Local => value.ToUniversalTime().Ticks,
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc).Ticks
        };

    private static long? NormalizeNullableUtcTicks(DateTime? value)
        => value.HasValue ? NormalizeUtcTicks(value.Value) : null;

    private static IsolatedLegacyInvoiceSeedCanonicalizationException
        Reject(
            string reasonCode,
            Guid? groupId = null,
            string? evidenceSha256 = null)
        => new(reasonCode, groupId, evidenceSha256);

    private static void AssertApprovedSourceDatabaseSha256(
        string sourceDatabaseSha256)
    {
        _ = GetApprovedProfile(sourceDatabaseSha256);
    }

    private static IsolatedLegacyInvoiceSeedCanonicalizationProfile
        GetApprovedProfile(string sourceDatabaseSha256)
    {
        if (!ApprovedProfilesBySourceDatabaseSha256.TryGetValue(
                sourceDatabaseSha256,
                out var profile) ||
            profile is null)
            throw Reject("source_database_sha256_not_approved");

        return profile;
    }

    private static void AssertDistinctInvoiceIds(
        IReadOnlyList<LocalInvoice> invoices)
    {
        var duplicateInvoiceId = invoices
            .GroupBy(invoice => invoice.Id)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicateInvoiceId is not null)
            throw Reject("duplicate_invoice_id");
    }

    private static Guid? NormalizeReference(Guid? value)
        => value.HasValue && value.Value != Guid.Empty
            ? value.Value
            : null;

    private static string NormalizeScope(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string FormatId(Guid value)
        => value.ToString("D").ToUpperInvariant();

    private static string FormatNullableId(Guid? value)
        => NormalizeReference(value).HasValue
            ? FormatId(NormalizeReference(value)!.Value)
            : "NONE";

    private static bool IsTruthy(string? value)
        => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string value)
        => value.Length == 64 &&
           value.All(character =>
               character is >= '0' and <= '9' or
                   >= 'A' and <= 'F' or
                   >= 'a' and <= 'f');

    private sealed record NonAcknowledgedOutboxEvidence(
        int Count,
        string Sha256);

    private sealed record NonAcknowledgedOutboxDescriptor(
        string Id,
        string MutationId,
        string DeviceId,
        string EntityName,
        string EntityId,
        long ExpectedRevision,
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode,
        string BusinessDatabaseName,
        string SessionId,
        string UserId,
        string Status,
        string ErrorMessageSha256,
        long PreparedAtUtcTicks,
        long? SentAtUtcTicks,
        long? AcknowledgedAtUtcTicks,
        long AcceptedRevision,
        long? AcceptedUpdatedAtUtcTicks);

    private sealed record SourceAttestation(
        int SchemaVersion,
        string DatabaseSha256);

    public sealed class AuthorizationLease : IDisposable
    {
        private readonly SourceAttestationLease _sourceAttestationLease;
        private bool _disposed;

        internal AuthorizationLease(
            string guardedRoot,
            string databasePath,
            string sourceDatabaseSha256,
            SourceAttestationLease sourceAttestationLease)
        {
            GuardedRoot = Path.GetFullPath(guardedRoot);
            DatabasePath = Path.GetFullPath(databasePath);
            SourceDatabaseSha256 = sourceDatabaseSha256;
            RecoveryResultPath = Path.Combine(
                GuardedRoot,
                RecoveryResultFileName);
            _sourceAttestationLease = sourceAttestationLease;
        }

        internal string GuardedRoot { get; }

        internal string DatabasePath { get; }

        internal string SourceDatabaseSha256 { get; }

        internal string RecoveryResultPath { get; }

        internal void AssertMatches(
            LocalDbContext db,
            IsolatedPreparationDatabaseLease preparationLease)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!string.Equals(
                    GuardedRoot,
                    Path.GetFullPath(preparationLease.GuardedRoot),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    DatabasePath,
                    Path.GetFullPath(
                        preparationLease.DatabasePath ??
                        string.Empty),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    DatabasePath,
                    Path.GetFullPath(
                        db.Database.GetDbConnection().DataSource),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The canonicalization authorization does not match the leased isolated database.");
            }
        }

        internal void AssertStable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sourceAttestationLease.AssertStable();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _sourceAttestationLease.Dispose();
        }
    }

    internal sealed class SourceAttestationLease : IDisposable
    {
        private readonly FileStream _stream;
        private readonly string _sha256;
        private readonly long _length;
        private bool _disposed;

        private SourceAttestationLease(
            FileStream stream,
            string text)
        {
            _stream = stream;
            Text = text;
            _length = stream.Length;
            _sha256 = ComputeSha256(text);
        }

        public string Text { get; }

        public static SourceAttestationLease Acquire(
            string guardedRoot)
        {
            var path = Path.Combine(
                guardedRoot,
                SourceAttestationFileName);
            if (!File.Exists(path) ||
                (File.GetAttributes(path) &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The pristine isolated source attestation is missing or unsafe.");
            }

            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            try
            {
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: true);
                var text = reader.ReadToEnd();
                stream.Position = 0;
                return new SourceAttestationLease(
                    stream,
                    text);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public void AssertStable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream.Length != _length)
            {
                throw new InvalidOperationException(
                    "The pristine isolated source attestation changed during canonicalization.");
            }

            _stream.Position = 0;
            using var reader = new StreamReader(
                _stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);
            var currentText = reader.ReadToEnd();
            _stream.Position = 0;
            if (!string.Equals(
                    _sha256,
                    ComputeSha256(currentText),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The pristine isolated source attestation changed during canonicalization.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _stream.Dispose();
        }
    }

#if GEORAEPLAN_CANONICALIZER_TESTING
    private sealed record CanonicalizationTestAdapter(
        bool RejectPartialPushState,
        IsolatedLegacyInvoiceSeedCanonicalizationProfile? RequiredProfile,
        IsolatedLegacyInvoiceSeedCanonicalizationFault Fault,
        IsolatedPreparationDatabaseLease? PreparationLease,
        AuthorizationLease? Authorization,
        string? RecoveryResultPath,
        string? RecoveryRootPath);
#endif

    private sealed record DatabaseSnapshot(
        IReadOnlyList<LocalInvoice> Invoices,
        IReadOnlyList<LocalCustomer> Customers,
        IReadOnlyList<LocalInvoiceLine> InvoiceLines,
        IReadOnlyList<LocalPayment> Payments,
        IReadOnlyList<LocalTransaction> Transactions,
        IReadOnlyList<LocalInvoiceLineSerial> InvoiceLineSerials,
        IReadOnlyList<LocalInventoryMovement> InventoryMovements,
        IReadOnlyList<LocalCostAllocation> CostAllocations,
        IReadOnlyList<LocalStockLayer> StockLayers,
        IReadOnlyList<LocalSerialLedger> SerialLedgers,
        IReadOnlyList<LocalRentalBillingProfile> RentalBillingProfiles);

    private sealed record StructuralInspection(
        bool HasAnomaly,
        int MaximumChildCount,
        string? ReasonCode);

    private sealed record InvoiceMetadataUpdate(
        Guid InvoiceId,
        Guid VersionGroupId,
        int VersionNumber,
        Guid? PreviousVersionId,
        bool IsLatestVersion,
        string ResponsibleOfficeCode,
        IReadOnlyList<string> ChangedMetadataFields);

    private sealed record GroupPlan(
        Guid OriginalGroupId,
        string Mode,
        IReadOnlySet<Guid> AllMemberIds,
        IReadOnlySet<Guid> ActiveInvoiceIds,
        IReadOnlySet<Guid> ExcludedDeletedInvoiceIds,
        Guid LatestInvoiceId,
        IReadOnlyList<InvoiceMetadataUpdate> Updates);

    private sealed record CanonicalizationPlan(
        IReadOnlyList<GroupPlan> Groups,
        IReadOnlyList<InvoiceMetadataUpdate> Updates);

    private sealed record RecoveryArtifactContract(
        int SchemaVersion,
        string SourceDatabaseSha256,
        string RecoveryStateSha256,
        string ReportJson,
        string ReportSha256,
        string ProtectedAuthenticationKey);

    private sealed record RecoveryArtifact(
        int SchemaVersion,
        string SourceDatabaseSha256,
        string RecoveryStateSha256,
        string ReportJson,
        string ReportSha256,
        string ProtectedAuthenticationKey,
        string ContractHmacSha256);

    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME
            CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME
            LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME
            LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
